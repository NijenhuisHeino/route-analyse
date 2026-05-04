using System.Text.Json;
using DuckDB.NET.Data;
using Postnl.RouteAnalyse.Fast.Models;

namespace Postnl.RouteAnalyse.Fast.Services;

public sealed class DuckDbRouteStore
{
    private static readonly string[] Variants = ["full", "eigen", "charter"];
    private readonly RouteAnalysisOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public DuckDbRouteStore(RouteAnalysisOptions options)
    {
        _options = options;
    }

    public string? StopsPath => ResolveStopsPath();
    public string? StopsSourceLabel => ResolveStopsSourceLabel();
    public bool HasStops => CanUseOriginalCsvs() || ResolveStopsParquetPath() is not null;

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_options.DuckDbPath)!);
            Directory.CreateDirectory(_options.CacheDir);

            var manifest = BuildManifest();
            if (File.Exists(_options.DuckDbPath)
                && File.Exists(_options.ManifestPath)
                && await File.ReadAllTextAsync(_options.ManifestPath, cancellationToken) == manifest)
            {
                _initialized = true;
                return;
            }

            if (File.Exists(_options.DuckDbPath))
            {
                File.Delete(_options.DuckDbPath);
            }

            await using var connection = CreateConnection();
            connection.Open();

            var csvFiles = ResolveOriginalCsvFiles();
            var geocode = ResolveAuxiliaryCachePath("geocode_addresses.parquet");
            if (csvFiles.Length > 0 && geocode is not null)
            {
                CreateStopsFromOriginalCsvs(connection, csvFiles, geocode);
                CreateAnalysisTables(connection);
            }
            else if (ResolveStopsParquetPath() is { } stops)
            {
                Execute(connection, $"CREATE OR REPLACE TABLE stops AS SELECT * FROM read_parquet({SqlString(stops)});");
                EnsureStopsVehicleColumns(connection);
                CreateAnalysisTables(connection);
            }

            foreach (var variant in Variants)
            {
                var edges = ResolveAuxiliaryCachePath($"agg_weighted_edges_{variant}.parquet");
                if (edges is not null && File.Exists(edges))
                {
                    Execute(connection, $"CREATE OR REPLACE VIEW edges_{variant} AS SELECT * FROM read_parquet({SqlString(edges)});");
                }

                var heat = ResolveAuxiliaryCachePath($"agg_road_heatmap_{variant}.parquet");
                if (heat is not null && File.Exists(heat))
                {
                    Execute(connection, $"CREATE OR REPLACE VIEW road_heat_{variant} AS SELECT * FROM read_parquet({SqlString(heat)});");
                }
            }

            var chargers = ResolveAuxiliaryCachePath("hdv_chargers.parquet");
            if (chargers is not null && File.Exists(chargers))
            {
                Execute(connection, $"CREATE OR REPLACE VIEW chargers AS SELECT * FROM read_parquet({SqlString(chargers)});");
            }

            await File.WriteAllTextAsync(_options.ManifestPath, manifest, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public DuckDBConnection CreateConnection()
    {
        return new DuckDBConnection($"Data Source={_options.DuckDbPath}");
    }

    public CacheFileStatus[] GetCacheStatus()
    {
        var statuses = new List<CacheFileStatus>();
        var csvFiles = ResolveOriginalCsvFiles();
        statuses.Add(BuildAggregateStatus("original_csvs", _options.OriginalCsvDir, csvFiles));

        var files = new List<(string Name, string Path)>
        {
            ("stops_parquet", ResolveStopsParquetPath() ?? Path.Combine(_options.CacheDir, "postnl_csv_Rittendata.parquet")),
            ("geocode_addresses", ResolveAuxiliaryCachePath("geocode_addresses.parquet") ?? Path.Combine(_options.CacheDir, "geocode_addresses.parquet")),
            ("chargers", ResolveAuxiliaryCachePath("hdv_chargers.parquet") ?? Path.Combine(_options.CacheDir, "hdv_chargers.parquet")),
            ("osrm_routes", ResolveAuxiliaryCachePath("osrm_routes_full.parquet") ?? Path.Combine(_options.CacheDir, "osrm_routes_full.parquet")),
            ("duckdb", _options.DuckDbPath),
        };

        foreach (var variant in Variants)
        {
            files.Add(($"edges_{variant}", ResolveAuxiliaryCachePath($"agg_weighted_edges_{variant}.parquet") ?? Path.Combine(_options.CacheDir, $"agg_weighted_edges_{variant}.parquet")));
            files.Add(($"road_heat_{variant}", ResolveAuxiliaryCachePath($"agg_road_heatmap_{variant}.parquet") ?? Path.Combine(_options.CacheDir, $"agg_road_heatmap_{variant}.parquet")));
        }

        statuses.AddRange(files.Select(f =>
        {
            var info = new FileInfo(f.Path);
            return new CacheFileStatus(
                f.Name,
                info.Exists,
                info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTimeUtc : null);
        }));

        return statuses.ToArray();
    }

    public bool HasView(string viewName)
    {
        if (!File.Exists(_options.DuckDbPath))
        {
            return false;
        }

        using var connection = CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                (SELECT COUNT(*) FROM duckdb_views() WHERE view_name = {SqlString(viewName)})
                + (SELECT COUNT(*) FROM duckdb_tables() WHERE table_name = {SqlString(viewName)});
            """;
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private string? ResolveStopsPath()
    {
        return CanUseOriginalCsvs()
            ? _options.OriginalCsvDir
            : ResolveStopsParquetPath();
    }

    private string? ResolveStopsSourceLabel()
    {
        var csvFiles = ResolveOriginalCsvFiles();
        if (csvFiles.Length > 0 && ResolveAuxiliaryCachePath("geocode_addresses.parquet") is not null)
        {
            var suffix = csvFiles.Length == 1 ? "maand" : "maanden";
            return $"Ritdata 2025 ({csvFiles.Length} {suffix})";
        }

        var parquet = ResolveStopsParquetPath();
        return parquet is null ? null : "Ritdata";
    }

    private string? ResolveStopsParquetPath()
    {
        var preferred = Path.Combine(_options.CacheDir, "postnl_csv_Rittendata.parquet");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        return Directory.Exists(_options.CacheDir)
            ? Directory.GetFiles(_options.CacheDir, "postnl_csv_*.parquet").OrderBy(x => x).FirstOrDefault()
            : null;
    }

    private bool CanUseOriginalCsvs()
    {
        return ResolveOriginalCsvFiles().Length > 0 && ResolveAuxiliaryCachePath("geocode_addresses.parquet") is not null;
    }

    private string[] ResolveOriginalCsvFiles()
    {
        return Directory.Exists(_options.OriginalCsvDir)
            ? Directory.GetFiles(_options.OriginalCsvDir, "*.csv").OrderBy(x => x).ToArray()
            : [];
    }

    private string? ResolveAuxiliaryCachePath(string fileName)
    {
        var local = Path.Combine(_options.CacheDir, fileName);
        if (File.Exists(local))
        {
            return local;
        }

        if (!string.IsNullOrWhiteSpace(_options.ExternalCacheDir))
        {
            var external = Path.Combine(_options.ExternalCacheDir, fileName);
            if (File.Exists(external))
            {
                return external;
            }
        }

        return null;
    }

    private string BuildManifest()
    {
        var sourcePaths = new List<string>();
        var csvFiles = ResolveOriginalCsvFiles();
        var geocode = ResolveAuxiliaryCachePath("geocode_addresses.parquet");
        if (csvFiles.Length > 0 && geocode is not null)
        {
            sourcePaths.AddRange(csvFiles);
            sourcePaths.Add(geocode);
        }
        else if (ResolveStopsParquetPath() is { } stops)
        {
            sourcePaths.Add(stops);
        }

        AddResolvedSource(sourcePaths, "hdv_chargers.parquet");
        AddResolvedSource(sourcePaths, "osrm_routes_full.parquet");
        foreach (var variant in Variants)
        {
            AddResolvedSource(sourcePaths, $"agg_weighted_edges_{variant}.parquet");
            AddResolvedSource(sourcePaths, $"agg_road_heatmap_{variant}.parquet");
        }

        var payload = new
        {
            Version = "charging-demand-v5-license-plates",
            Sources = sourcePaths.Select(path =>
            {
                var info = new FileInfo(path);
                return new
                {
                    Path = path,
                    Exists = info.Exists,
                    Length = info.Exists ? info.Length : 0,
                    LastWriteUtc = info.Exists ? info.LastWriteTimeUtc.Ticks : 0,
                };
            }),
        };
        return JsonSerializer.Serialize(payload);
    }

    private void AddResolvedSource(List<string> sourcePaths, string fileName)
    {
        sourcePaths.Add(ResolveAuxiliaryCachePath(fileName) ?? Path.Combine(_options.CacheDir, fileName));
    }

    private static CacheFileStatus BuildAggregateStatus(string name, string? directory, string[] files)
    {
        if (files.Length == 0)
        {
            return new CacheFileStatus(name, false, 0, null);
        }

        var infos = files.Select(path => new FileInfo(path)).Where(info => info.Exists).ToArray();
        return new CacheFileStatus(
            name,
            infos.Length > 0,
            infos.Sum(info => info.Length),
            infos.Length == 0 ? null : infos.Max(info => info.LastWriteTimeUtc));
    }

    private static void CreateStopsFromOriginalCsvs(DuckDBConnection connection, IReadOnlyList<string> csvFiles, string geocodePath)
    {
        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE stops AS
            WITH raw AS (
                SELECT *
                FROM read_csv(
                    {{SqlStringArray(csvFiles)}},
                    header = true,
                    all_varchar = true,
                    union_by_name = true,
                    filename = true
                )
                WHERE lower(trim(COALESCE("Actie soort", ''))) = 'travel'
            ),
            typed AS (
                SELECT
                    trim(COALESCE("Voertuig Type Eigenaar", '')) AS vervoerder,
                    trim(COALESCE("Wagen Code", '')) AS wagencode,
                    trim(COALESCE("Wagentype Omschrijving", '')) AS wagentype_omschrijving,
                    trim(COALESCE("Tripnummer", '')) AS trip_id,
                    trim(COALESCE("Actie soort", '')) AS acties,
                    trim(COALESCE("Adres", '')) AS adres,
                    upper(trim(COALESCE("Gerealizeerd Kenteken", ''))) AS kenteken,
                    regexp_replace(upper(trim(COALESCE("Gerealizeerd Kenteken", ''))), '[^A-Z0-9]', '', 'g') AS kenteken_norm,
                    COALESCE(
                        {{ParseTimestampSql("\"Gepland vanaf (Trip actie)\"")}},
                        {{ParseTimestampSql("\"Starttijd Trip\"")}}
                    ) AS gepland_start,
                    COALESCE(
                        {{ParseTimestampSql("\"Gepland tot (Trip actie)\"")}},
                        {{ParseTimestampSql("\"Eindtijd Trip\"")}},
                        {{ParseTimestampSql("\"Gepland vanaf (Trip actie)\"")}},
                        {{ParseTimestampSql("\"Starttijd Trip\"")}}
                    ) AS gepland_eind,
                    {{ParseDoubleSql("\"Totale Afstand (KM)\"")}} AS afstand_km_trip
                FROM raw
            ),
            geocoded AS (
                SELECT
                    t.*,
                    CAST(g.lat AS DOUBLE) AS lat,
                    CAST(g.lon AS DOUBLE) AS lon
                FROM typed t
                LEFT JOIN read_parquet({{SqlString(geocodePath)}}) g
                    ON t.adres = CAST(g.query AS VARCHAR)
                WHERE t.wagencode <> ''
                    AND t.trip_id <> ''
                    AND t.adres <> ''
                    AND t.gepland_start IS NOT NULL
                    AND g.lat IS NOT NULL
                    AND g.lon IS NOT NULL
            ),
            sequenced AS (
                SELECT
                    *,
                    CAST(gepland_start AS DATE) AS trip_date,
                    ROW_NUMBER() OVER (
                        PARTITION BY wagencode, CAST(gepland_start AS DATE), trip_id
                        ORDER BY gepland_start, gepland_eind, adres
                    ) - 1 AS stop_seq
                FROM geocoded
            )
            SELECT
                wagencode,
                vervoerder,
                CASE
                    WHEN lower(vervoerder) LIKE '%eigen%' THEN 'eigen'
                    WHEN lower(vervoerder) LIKE '%uitbesteed%' OR lower(vervoerder) LIKE '%charter%' THEN 'charter'
                    ELSE 'onbekend'
                END AS vervoerder_type,
                trip_date,
                trip_id,
                CAST(stop_seq AS INTEGER) AS stop_seq,
                acties,
                adres AS locatie_naam,
                adres,
                gepland_start,
                gepland_eind,
                0.0 AS afstand_km,
                COALESCE(afstand_km_trip, 0.0) AS afstand_km_trip,
                GREATEST(COALESCE(date_diff('second', gepland_start, gepland_eind), 0) / 60.0, 0.0) AS dwell_min,
                lat,
                lon,
                kenteken,
                kenteken_norm,
                trip_id || '-' || lpad(CAST(stop_seq AS VARCHAR), 2, '0') AS trip_stop_nr,
                wagentype_omschrijving,
                NULL AS ord_location_id,
                NULL AS dagorder,
                NULL AS gewicht_na_stop,
                NULL AS rijtijd_min,
                NULL AS laad_los
            FROM sequenced;
            """);
    }

    private static void CreateAnalysisTables(DuckDBConnection connection)
    {
        var plannedStart = HasColumn(connection, "stops", "gepland_start")
            ? "TRY_CAST(gepland_start AS TIMESTAMP)"
            : "CAST(trip_date AS TIMESTAMP)";
        var plannedEnd = HasColumn(connection, "stops", "gepland_eind")
            ? "TRY_CAST(gepland_eind AS TIMESTAMP)"
            : "CAST(trip_date AS TIMESTAMP)";
        var tripDistance = HasColumn(connection, "stops", "afstand_km_trip")
            ? "COALESCE(TRY_CAST(afstand_km_trip AS DOUBLE), 0)"
            : "0";
        var licensePlate = HasColumn(connection, "stops", "kenteken")
            ? "COALESCE(CAST(kenteken AS VARCHAR), '')"
            : "''";
        var normalizedLicensePlate = HasColumn(connection, "stops", "kenteken_norm")
            ? "COALESCE(CAST(kenteken_norm AS VARCHAR), '')"
            : "''";

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE daily_trips AS
            WITH valid AS (
                SELECT
                    CAST(wagencode AS VARCHAR) AS wagencode,
                    CAST(vervoerder AS VARCHAR) AS vervoerder,
                    CAST(vervoerder_type AS VARCHAR) AS vervoerder_type,
                    CAST(trip_date AS DATE) AS trip_date,
                    CAST(trip_id AS VARCHAR) AS trip_id,
                    COALESCE(TRY_CAST(stop_seq AS INTEGER), 0) AS stop_seq,
                    CAST(lat AS DOUBLE) AS lat,
                    CAST(lon AS DOUBLE) AS lon,
                    COALESCE(CAST(adres AS VARCHAR), '') AS address,
                    {{licensePlate}} AS kenteken,
                    {{normalizedLicensePlate}} AS kenteken_norm,
                    {{plannedStart}} AS planned_start,
                    {{plannedEnd}} AS planned_end,
                    COALESCE(TRY_CAST(afstand_km AS DOUBLE), 0) AS segment_km,
                    {{tripDistance}} AS trip_km_source
                FROM stops
                WHERE lat IS NOT NULL
                    AND lon IS NOT NULL
                    AND trip_id IS NOT NULL
                    AND wagencode IS NOT NULL
                    AND NOT COALESCE(CAST(acties AS VARCHAR), '') ILIKE '%Administrative%'
            ),
            ranked AS (
                SELECT
                    *,
                    ROW_NUMBER() OVER (
                        PARTITION BY wagencode, trip_date, trip_id
                        ORDER BY planned_start, planned_end, stop_seq
                    ) AS rn_first,
                    ROW_NUMBER() OVER (
                        PARTITION BY wagencode, trip_date, trip_id
                        ORDER BY planned_start DESC, planned_end DESC, stop_seq DESC
                    ) AS rn_last
                FROM valid
            )
            SELECT
                wagencode,
                COALESCE(MODE(vervoerder), '') AS vervoerder,
                COALESCE(MODE(vervoerder_type), '') AS vervoerder_type,
                COALESCE(MODE(NULLIF(kenteken, '')), '') AS kenteken,
                COALESCE(MODE(NULLIF(kenteken_norm, '')), '') AS kenteken_norm,
                COALESCE(string_agg(DISTINCT NULLIF(kenteken, ''), ', '), '') AS kentekens,
                trip_date,
                trip_id,
                MIN(planned_start) AS trip_start,
                MAX(planned_end) AS trip_end,
                MAX(CASE WHEN rn_first = 1 THEN lat END) AS start_lat,
                MAX(CASE WHEN rn_first = 1 THEN lon END) AS start_lon,
                MAX(CASE WHEN rn_first = 1 THEN address END) AS start_address,
                MAX(CASE WHEN rn_last = 1 THEN lat END) AS end_lat,
                MAX(CASE WHEN rn_last = 1 THEN lon END) AS end_lon,
                MAX(CASE WHEN rn_last = 1 THEN address END) AS end_address,
                GREATEST(COALESCE(SUM(segment_km), 0), COALESCE(MAX(trip_km_source), 0)) AS distance_km,
                COUNT(*) AS stops
            FROM ranked
            GROUP BY wagencode, trip_date, trip_id
            HAVING trip_start IS NOT NULL AND trip_end IS NOT NULL;
            """);

        Execute(connection,
            """
            CREATE OR REPLACE TABLE vehicle_days AS
            WITH ranked AS (
                SELECT
                    *,
                    ROW_NUMBER() OVER (PARTITION BY wagencode, trip_date ORDER BY trip_start, trip_id) AS rn_first,
                    ROW_NUMBER() OVER (PARTITION BY wagencode, trip_date ORDER BY trip_end DESC, trip_id DESC) AS rn_last
                FROM daily_trips
            )
            SELECT
                wagencode,
                COALESCE(MODE(vervoerder), '') AS vervoerder,
                COALESCE(MODE(vervoerder_type), '') AS vervoerder_type,
                COALESCE(MODE(NULLIF(kenteken, '')), '') AS kenteken,
                COALESCE(MODE(NULLIF(kenteken_norm, '')), '') AS kenteken_norm,
                COALESCE(string_agg(DISTINCT NULLIF(kenteken, ''), ', '), '') AS kentekens,
                trip_date,
                MIN(trip_start) AS day_start,
                MAX(trip_end) AS day_end,
                MAX(CASE WHEN rn_first = 1 THEN start_lat END) AS start_lat,
                MAX(CASE WHEN rn_first = 1 THEN start_lon END) AS start_lon,
                MAX(CASE WHEN rn_first = 1 THEN start_address END) AS start_address,
                MAX(CASE WHEN rn_last = 1 THEN end_lat END) AS end_lat,
                MAX(CASE WHEN rn_last = 1 THEN end_lon END) AS end_lon,
                MAX(CASE WHEN rn_last = 1 THEN end_address END) AS end_address,
                SUM(distance_km) AS day_km,
                COUNT(*) AS trips
            FROM ranked
            GROUP BY wagencode, trip_date;
            """);

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE overnight_events AS
            WITH ordered AS (
                SELECT
                    *,
                    LAG(day_end) OVER (PARTITION BY wagencode ORDER BY trip_date) AS prev_end_time,
                    LAG(end_lat) OVER (PARTITION BY wagencode ORDER BY trip_date) AS prev_end_lat,
                    LAG(end_lon) OVER (PARTITION BY wagencode ORDER BY trip_date) AS prev_end_lon,
                    LAG(end_address) OVER (PARTITION BY wagencode ORDER BY trip_date) AS prev_end_address
                FROM vehicle_days
            ),
            scored AS (
                SELECT
                    printf('auto:%.3f:%.3f', ROUND(start_lat, 3), ROUND(start_lon, 3)) AS depot_id,
                    *,
                    date_diff('second', prev_end_time, day_start) / 3600.0 AS gap_hours,
                    6371.0 * 2.0 * asin(sqrt(
                        pow(sin(radians(start_lat - prev_end_lat) / 2.0), 2)
                        + cos(radians(prev_end_lat)) * cos(radians(start_lat))
                        * pow(sin(radians(start_lon - prev_end_lon) / 2.0), 2)
                    )) AS end_start_km
                FROM ordered
                WHERE prev_end_time IS NOT NULL
                    AND prev_end_lat IS NOT NULL
                    AND prev_end_lon IS NOT NULL
            )
            SELECT *
            FROM scored
            WHERE gap_hours BETWEEN 6 AND 72
                AND end_start_km <= 0.5;
            """);

        Execute(connection,
            """
            CREATE OR REPLACE TABLE overnight_locations AS
            SELECT
                depot_id,
                AVG(start_lat) AS lat,
                AVG(start_lon) AS lon,
                COALESCE(MODE(start_address), '') AS address,
                COUNT(*) AS events,
                COUNT(DISTINCT wagencode) AS unique_vehicles,
                COALESCE(quantile_cont(gap_hours, 0.5), 0) AS median_gap_hours,
                COALESCE(quantile_cont(day_km, 0.95), 0) AS p95_day_km,
                COALESCE(SUM(day_km), 0) AS total_day_km,
                LEAST(1.0, COUNT(DISTINCT wagencode) / 25.0) * 0.6
                    + LEAST(1.0, COUNT(*) / 100.0) * 0.4 AS confidence
            FROM overnight_events
            GROUP BY depot_id;
            """);

        Execute(connection,
            """
            CREATE OR REPLACE TABLE road_selection_index AS
            SELECT
                d.wagencode,
                d.kenteken,
                d.kenteken_norm,
                d.kentekens,
                d.trip_date,
                d.trip_id,
                d.start_lat AS lat1,
                d.start_lon AS lon1,
                d.end_lat AS lat2,
                d.end_lon AS lon2,
                (d.start_lat + d.end_lat) / 2.0 AS mid_lat,
                (d.start_lon + d.end_lon) / 2.0 AS mid_lon,
                d.vervoerder,
                d.vervoerder_type,
                d.trip_start,
                d.trip_end,
                d.start_lat,
                d.start_lon,
                d.start_address,
                d.end_lat,
                d.end_lon,
                d.end_address,
                d.distance_km
            FROM daily_trips d
            WHERE d.start_lat IS NOT NULL
                AND d.start_lon IS NOT NULL
                AND d.end_lat IS NOT NULL
                AND d.end_lon IS NOT NULL
                AND NOT (d.start_lat = d.end_lat AND d.start_lon = d.end_lon);
            """);

    }

    private static void EnsureStopsVehicleColumns(DuckDBConnection connection)
    {
        if (!HasColumn(connection, "stops", "kenteken"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN kenteken VARCHAR DEFAULT '';");
        }

        if (!HasColumn(connection, "stops", "kenteken_norm"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN kenteken_norm VARCHAR DEFAULT '';");
        }
    }

    private static bool HasColumn(DuckDBConnection connection, string relation, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM (DESCRIBE SELECT * FROM {relation}) WHERE column_name = {SqlString(column)};";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static string SqlString(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string SqlStringArray(IEnumerable<string> values)
    {
        return "[" + string.Join(", ", values.Select(SqlString)) + "]";
    }

    private static string ParseTimestampSql(string columnSql)
    {
        var value = $"NULLIF(trim(COALESCE({columnSql}, '')), '')";
        return $"""
            COALESCE(
                try_strptime({value}, '%d-%m-%Y %H:%M:%S'),
                try_strptime({value}, '%d-%m-%Y %H:%M'),
                try_strptime({value}, '%d/%m/%Y %H:%M:%S'),
                try_strptime({value}, '%d/%m/%Y %H:%M'),
                try_strptime({value}, '%Y-%m-%d %H:%M:%S'),
                try_strptime({value}, '%Y-%m-%d %H:%M')
            )
            """;
    }

    private static string ParseDoubleSql(string columnSql)
    {
        var value = $"NULLIF(trim(COALESCE({columnSql}, '')), '')";
        return $"""
            CASE
                WHEN contains({value}, ',')
                    THEN try_cast(replace(replace({value}, '.', ''), ',', '.') AS DOUBLE)
                ELSE try_cast({value} AS DOUBLE)
            END
            """;
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

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
    public bool HasStops => ResolveSourceCsvFiles().Length > 0 || ResolveStopsParquetPath() is not null;
    public RouteAnalysisOptions Options => _options;

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

            var csvFiles = ResolveSourceCsvFiles();
            var geocode = ResolveAuxiliaryCachePath("geocode_addresses.parquet");
            if (csvFiles.Length > 0)
            {
                CreateStopsFromCsvs(connection, csvFiles, geocode);
                CreateZeroEmissionZoneTables(connection);
                CreateAnalysisTables(connection);
            }
            else if (ResolveStopsParquetPath() is { } stops)
            {
                Execute(connection, $"CREATE OR REPLACE TABLE stops AS SELECT * FROM read_parquet({SqlString(stops)});");
                EnsureStopsVehicleColumns(connection);
                CreateRouteActionsFromStops(connection);
                CreateZeroEmissionZoneTables(connection);
                CreateAnalysisTables(connection);
            }

            if (!HasUploadedDataset())
            {
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

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            _initialized = false;
            DeleteFileIfExists(_options.DuckDbPath);
            DeleteFileIfExists(_options.ManifestPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public CacheFileStatus[] GetCacheStatus()
    {
        var statuses = new List<CacheFileStatus>();
        var csvFiles = ResolveSourceCsvFiles();
        statuses.Add(BuildAggregateStatus("source_csvs", ResolveSourceCsvDirectory(), csvFiles));

        var files = new List<(string Name, string Path)>
        {
            ("stops_parquet", ResolveStopsParquetPath() ?? Path.Combine(_options.CacheDir, "route_stops_Rittendata.parquet")),
            ("geocode_addresses", ResolveAuxiliaryCachePath("geocode_addresses.parquet") ?? Path.Combine(_options.CacheDir, "geocode_addresses.parquet")),
            ("chargers", ResolveAuxiliaryCachePath("hdv_chargers.parquet") ?? Path.Combine(_options.CacheDir, "hdv_chargers.parquet")),
            ("osrm_routes", ResolveAuxiliaryCachePath("osrm_routes_full.parquet") ?? Path.Combine(_options.CacheDir, "osrm_routes_full.parquet")),
            ("ze_zones", ResolveZeZonesSourcePath() ?? Path.Combine(_options.CacheDir, "zez_pc6.csv")),
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
        return ResolveSourceCsvFiles().Length > 0
            ? ResolveSourceCsvDirectory()
            : ResolveStopsParquetPath();
    }

    private string? ResolveStopsSourceLabel()
    {
        if (HasUploadedDataset())
        {
            return "Eigen dataset";
        }

        var csvFiles = ResolveSourceCsvFiles();
        if (csvFiles.Length > 0)
        {
            var suffix = csvFiles.Length == 1 ? "bestand" : "bestanden";
            return $"Ritdata ({csvFiles.Length} {suffix})";
        }

        var parquet = ResolveStopsParquetPath();
        return parquet is null ? null : "Vaste ritdataset";
    }

    private string? ResolveStopsParquetPath()
    {
        var uploaded = ResolveUploadedDatasetFiles()
            .Where(path => Path.GetExtension(path).Equals(".parquet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .FirstOrDefault();
        if (uploaded is not null)
        {
            return uploaded;
        }

        var preferred = Path.Combine(_options.CacheDir, "route_stops_Rittendata.parquet");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        if (!Directory.Exists(_options.CacheDir))
        {
            return null;
        }

        return Directory.GetFiles(_options.CacheDir, "route_stops_*.parquet").OrderBy(x => x).FirstOrDefault()
            ?? Directory.GetFiles(_options.CacheDir, "postnl_csv_Rittendata.parquet").OrderBy(x => x).FirstOrDefault()
            ?? Directory.GetFiles(_options.CacheDir, "postnl_csv_*.parquet").OrderBy(x => x).FirstOrDefault();
    }

    private string[] ResolveSourceCsvFiles()
    {
        var uploaded = ResolveUploadedDatasetFiles()
            .Where(path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .ToArray();
        return uploaded.Length > 0 ? uploaded : ResolveOriginalCsvFiles();
    }

    private string[] ResolveOriginalCsvFiles()
    {
        return Directory.Exists(_options.OriginalCsvDir)
            ? Directory.GetFiles(_options.OriginalCsvDir, "*.csv").OrderBy(x => x).ToArray()
            : [];
    }

    private string? ResolveSourceCsvDirectory()
    {
        return ResolveUploadedDatasetFiles().Any(path => Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            ? _options.UploadedDatasetDir
            : _options.OriginalCsvDir;
    }

    private bool HasUploadedDataset()
    {
        return ResolveUploadedDatasetFiles().Length > 0;
    }

    private string[] ResolveUploadedDatasetFiles()
    {
        return Directory.Exists(_options.UploadedDatasetDir)
            ? Directory.GetFiles(_options.UploadedDatasetDir)
                .Where(path =>
                    Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".parquet", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x)
                .ToArray()
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

    private string? ResolveZeZonesSourcePath()
    {
        if (!string.IsNullOrWhiteSpace(_options.ZeZonesSourcePath) && File.Exists(_options.ZeZonesSourcePath))
        {
            return _options.ZeZonesSourcePath;
        }

        var localCsv = Path.Combine(_options.CacheDir, "zez_pc6.csv");
        if (File.Exists(localCsv))
        {
            return localCsv;
        }

        var localXlsx = Path.Combine(_options.CacheDir, "zez_pc6.xlsx");
        if (File.Exists(localXlsx))
        {
            return localXlsx;
        }

        if (!string.IsNullOrWhiteSpace(_options.ExternalCacheDir))
        {
            var externalCsv = Path.Combine(_options.ExternalCacheDir, "zez_pc6.csv");
            if (File.Exists(externalCsv))
            {
                return externalCsv;
            }

            var externalXlsx = Path.Combine(_options.ExternalCacheDir, "zez_pc6.xlsx");
            if (File.Exists(externalXlsx))
            {
                return externalXlsx;
            }
        }

        return _options.ZeZonesFallbackPaths.FirstOrDefault(File.Exists);
    }

    private string BuildManifest()
    {
        var sourcePaths = new List<string>();
        var csvFiles = ResolveSourceCsvFiles();
        var geocode = ResolveAuxiliaryCachePath("geocode_addresses.parquet");
        if (csvFiles.Length > 0)
        {
            sourcePaths.AddRange(csvFiles);
            if (geocode is not null)
            {
                sourcePaths.Add(geocode);
            }
        }
        else if (ResolveStopsParquetPath() is { } stops)
        {
            sourcePaths.Add(stops);
        }

        AddResolvedSource(sourcePaths, "hdv_chargers.parquet");
        if (ResolveZeZonesSourcePath() is { } zeZones)
        {
            sourcePaths.Add(zeZones);
        }

        if (!HasUploadedDataset())
        {
            AddResolvedSource(sourcePaths, "osrm_routes_full.parquet");
            foreach (var variant in Variants)
            {
                AddResolvedSource(sourcePaths, $"agg_weighted_edges_{variant}.parquet");
                AddResolvedSource(sourcePaths, $"agg_road_heatmap_{variant}.parquet");
            }
        }

        var payload = new
        {
            Version = "charging-demand-v9-road-selection-index",
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

    private static void CreateStopsFromCsvs(DuckDBConnection connection, IReadOnlyList<string> csvFiles, string? geocodePath)
    {
        Execute(connection,
            $$"""
            CREATE OR REPLACE TEMP TABLE raw_csv AS
            SELECT *
            FROM read_csv(
                {{SqlStringArray(csvFiles)}},
                header = true,
                all_varchar = true,
                union_by_name = true,
                filename = true
            );
            """);

        var columns = GetColumns(connection, "raw_csv");
        var vehicle = CoalesceTextSql(columns, "Wagen Code", "wagencode", "wagen_code", "vehicle_id", "vehicle_code", "voertuig", "truck_id");
        var carrier = CoalesceTextSql(columns, "Voertuig Type Eigenaar", "vervoerder", "carrier", "carrier_name", "transporteur");
        var carrierType = CoalesceTextSql(columns, "vervoerder_type", "carrier_type", "transport_type");
        var vehicleType = CoalesceTextSql(columns, "Wagentype Omschrijving", "wagentype_omschrijving", "vehicle_type", "truck_type");
        var tripId = CoalesceTextSql(columns, "Tripnummer", "trip_id", "rit_id", "ritnummer", "route_id");
        var action = CoalesceTextSql(columns, "Actie soort", "actie", "action", "action_type");
        var address = CoalesceTextSql(columns, "Adres", "adres", "address", "location_address", "stop_address", "locatie", "location");
        var locationName = CoalesceTextSql(columns, "locatie_naam", "location_name", "name", "naam", "Adres", "adres", "address");
        var licensePlate = CoalesceTextSql(columns, "Gerealizeerd Kenteken", "kenteken", "license_plate", "licence_plate", "plate");
        var plannedStart = CoalesceTimestampSql(
            columns,
            "Gepland vanaf (Trip actie)",
            "planned_start",
            "start_time",
            "starttijd",
            "Starttijd Trip",
            "trip_start",
            "trip_date",
            "date",
            "datum");
        var plannedEnd = CoalesceTimestampSql(
            columns,
            "Gepland tot (Trip actie)",
            "planned_end",
            "end_time",
            "eindtijd",
            "Eindtijd Trip",
            "trip_end",
            "Gepland vanaf (Trip actie)",
            "planned_start",
            "trip_date",
            "date",
            "datum");
        var distance = ParseDoubleSql(CoalesceTextSql(
            columns,
            "Totale Afstand (KM)",
            "afstand_km_trip",
            "trip_distance_km",
            "trip_km",
            "distance_km",
            "afstand_km"));
        var segmentDistance = ParseDoubleSql(CoalesceTextSql(columns, "afstand_km", "segment_km", "leg_km"));
        var latColumn = TryColumnSql(columns, "lat", "latitude", "breedtegraad", "y");
        var lonColumn = TryColumnSql(columns, "lon", "lng", "longitude", "lengtegraad", "x");
        var hasCoordinates = latColumn is not null && lonColumn is not null;
        var actionFilter = TryColumnSql(columns, "Actie soort") is null
            ? ""
            : """WHERE lower(trim(COALESCE("Actie soort", ''))) = 'travel'""";

        if (!hasCoordinates && geocodePath is null)
        {
            throw new InvalidOperationException("De dataset bevat geen lat/lon-kolommen en er is geen locatiekoppeling beschikbaar.");
        }

        var directLatSql = hasCoordinates ? ParseDoubleSql(latColumn!) : "NULL";
        var directLonSql = hasCoordinates ? ParseDoubleSql(lonColumn!) : "NULL";
        var latSql = hasCoordinates ? "t.direct_lat" : "CAST(g.lat AS DOUBLE)";
        var lonSql = hasCoordinates ? "t.direct_lon" : "CAST(g.lon AS DOUBLE)";
        var fromSql = hasCoordinates
            ? "typed t"
            : $"typed t LEFT JOIN read_parquet({SqlString(geocodePath!)}) g ON t.adres = CAST(g.query AS VARCHAR)";

        string TypedGeocodedCtes(string source, string actionSelect) =>
            $$"""
            typed AS (
                SELECT
                    trim({{carrier}}) AS vervoerder,
                    trim({{carrierType}}) AS vervoerder_type_raw,
                    trim({{vehicle}}) AS wagencode,
                    trim({{vehicleType}}) AS wagentype_omschrijving,
                    trim({{tripId}}) AS trip_id,
                    {{actionSelect}},
                    trim({{locationName}}) AS locatie_naam,
                    trim({{address}}) AS adres,
                    upper(trim({{licensePlate}})) AS kenteken,
                    regexp_replace(upper(trim({{licensePlate}})), '[^A-Z0-9]', '', 'g') AS kenteken_norm,
                    {{plannedStart}} AS gepland_start,
                    {{plannedEnd}} AS gepland_eind,
                    {{distance}} AS afstand_km_trip,
                    {{segmentDistance}} AS afstand_km_segment,
                    {{directLatSql}} AS direct_lat,
                    {{directLonSql}} AS direct_lon
                FROM {{source}}
            ),
            geocoded AS (
                SELECT
                    t.*,
                    {{latSql}} AS lat,
                    {{lonSql}} AS lon
                FROM {{fromSql}}
                WHERE t.wagencode <> ''
                    AND t.trip_id <> ''
                    AND t.gepland_start IS NOT NULL
            )
            """;

        static string VehicleClassCase(string own, string charter, string unknown) =>
            $"""
            CASE
                WHEN lower(vervoerder_type_raw) LIKE '%eigen%' OR lower(vervoerder_type_raw) LIKE '%own%' THEN '{own}'
                WHEN lower(vervoerder_type_raw) LIKE '%charter%' OR lower(vervoerder_type_raw) LIKE '%sub%' THEN '{charter}'
                WHEN lower(vervoerder) LIKE '%eigen%' THEN '{own}'
                WHEN lower(vervoerder) LIKE '%uitbesteed%' OR lower(vervoerder) LIKE '%charter%' THEN '{charter}'
                ELSE '{unknown}'
            END
            """;

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE route_actions AS
            WITH {{TypedGeocodedCtes("raw_csv", $"lower(trim({action})) AS actie_soort")}},
            sequenced AS (
                SELECT
                    *,
                    CAST(gepland_start AS DATE) AS trip_date,
                    ROW_NUMBER() OVER (
                        PARTITION BY wagencode, CAST(gepland_start AS DATE), trip_id
                        ORDER BY gepland_start, gepland_eind, adres, actie_soort
                    ) - 1 AS action_seq
                FROM geocoded
            )
            SELECT
                wagencode,
                vervoerder,
                {{VehicleClassCase("own", "charter", "unknown")}} AS vehicle_class,
                trip_date,
                trip_id,
                CAST(action_seq AS INTEGER) AS action_seq,
                actie_soort,
                COALESCE(NULLIF(locatie_naam, ''), adres, 'unknown_location') AS locatie_naam,
                COALESCE(NULLIF(adres, ''), 'unknown_location') AS adres,
                CASE
                    WHEN lat IS NULL OR lon IS NULL THEN 'unknown_location:' || COALESCE(NULLIF(adres, ''), 'missing')
                    ELSE printf('auto:%.3f:%.3f', ROUND(CAST(lat AS DOUBLE), 3), ROUND(CAST(lon AS DOUBLE), 3))
                END AS location_id,
                gepland_start,
                gepland_eind,
                GREATEST(COALESCE(date_diff('second', gepland_start, gepland_eind), 0) / 60.0, 0.0) AS dwell_min,
                COALESCE(afstand_km_segment, 0.0) AS afstand_km,
                COALESCE(afstand_km_trip, 0.0) AS afstand_km_trip,
                lat,
                lon,
                kenteken,
                kenteken_norm,
                wagentype_omschrijving
            FROM sequenced;
            """);

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE stops AS
            WITH raw AS (
                SELECT *
                FROM raw_csv
                {{actionFilter}}
            ),
            {{TypedGeocodedCtes("raw", $"trim({action}) AS acties")}},
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
                {{VehicleClassCase("eigen", "charter", "onbekend")}} AS vervoerder_type,
                trip_date,
                trip_id,
                CAST(stop_seq AS INTEGER) AS stop_seq,
                acties,
                COALESCE(NULLIF(locatie_naam, ''), adres) AS locatie_naam,
                adres,
                gepland_start,
                gepland_eind,
                COALESCE(afstand_km_segment, 0.0) AS afstand_km,
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
            FROM sequenced
            WHERE lat IS NOT NULL
                AND lon IS NOT NULL;
            """);
    }

    private void CreateZeroEmissionZoneTables(DuckDBConnection connection)
    {
        EnsureStopsZeroEmissionColumns(connection);
        Execute(connection,
            """
            UPDATE stops
            SET
                pc6 = regexp_replace(
                    upper(regexp_extract(COALESCE(CAST(adres AS VARCHAR), ''), '([0-9]{4})\s*([A-Za-z]{2})', 0)),
                    '\s',
                    '',
                    'g'
                ),
                ze_zone = '',
                ze_startdatum = '',
                in_zez = false;
            """);

        var source = ResolveZeZonesSourcePath();
        if (source is null)
        {
            return;
        }

        var zones = LoadZeZoneLookup(source).ToArray();
        if (zones.Length == 0)
        {
            return;
        }

        var csvPath = Path.Combine(Path.GetDirectoryName(_options.ManifestPath)!, "zez_pc6.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        WriteZeZoneCsv(csvPath, zones);

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE ze_zones AS
            SELECT
                upper(trim(CAST(pc6 AS VARCHAR))) AS pc6,
                trim(CAST(ze_zone AS VARCHAR)) AS ze_zone,
                trim(CAST(ze_startdatum AS VARCHAR)) AS ze_startdatum
            FROM read_csv({{SqlString(csvPath)}}, header = true, all_varchar = true)
            WHERE pc6 IS NOT NULL
                AND trim(CAST(pc6 AS VARCHAR)) <> '';
            """);

        Execute(connection,
            """
            UPDATE stops
            SET
                ze_zone = COALESCE(z.ze_zone, ''),
                ze_startdatum = COALESCE(z.ze_startdatum, ''),
                in_zez = z.pc6 IS NOT NULL
            FROM ze_zones z
            WHERE stops.pc6 = z.pc6;
            """);
    }

    private static IEnumerable<ZeZoneLookupRow> LoadZeZoneLookup(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return LoadZeZoneCsv(sourcePath);
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(sourcePath);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return [];
            }

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            return LoadZeZoneXlsx(memory);
        }

        using var xlsx = File.OpenRead(sourcePath);
        return LoadZeZoneXlsx(xlsx);
    }

    private static IEnumerable<ZeZoneLookupRow> LoadZeZoneCsv(string sourcePath)
    {
        using var reader = new StreamReader(sourcePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }

        var headers = SplitCsvLine(header).Select(XlsxReader.NormalizeHeader).ToArray();
        var pc6Index = Array.IndexOf(headers, "pc6");
        var zoneIndex = Array.IndexOf(headers, "ze_zone");
        var startIndex = Array.IndexOf(headers, "ze_startdatum");
        var inZoneIndex = Array.IndexOf(headers, "in_zero_emissie_zone");
        if (pc6Index < 0 || zoneIndex < 0)
        {
            yield break;
        }

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var cells = SplitCsvLine(line);
            if (inZoneIndex >= 0 && !string.Equals(XlsxReader.Cell(cells, inZoneIndex), "ja", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pc6 = NormalizePc6(XlsxReader.Cell(cells, pc6Index));
            var zone = XlsxReader.Cell(cells, zoneIndex).Trim();
            if (pc6.Length == 0 || zone.Length == 0)
            {
                continue;
            }

            yield return new ZeZoneLookupRow(pc6, zone, startIndex >= 0 ? XlsxReader.Cell(cells, startIndex).Trim() : "");
        }
    }

    private static IEnumerable<ZeZoneLookupRow> LoadZeZoneXlsx(Stream source)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var sharedStrings = XlsxReader.ReadSharedStrings(archive);
        var sheetPath = XlsxReader.ResolveWorksheetPath(archive, "overlap_pc6_ze_zones");
        if (sheetPath is null)
        {
            return [];
        }

        var rows = XlsxReader.ReadWorksheetRows(archive, sheetPath, sharedStrings).ToArray();
        if (rows.Length == 0)
        {
            return [];
        }

        var headers = rows[0].Select(XlsxReader.NormalizeHeader).ToArray();
        var pc6Index = Array.IndexOf(headers, "pc6");
        var zoneIndex = Array.IndexOf(headers, "ze_zone");
        var startIndex = Array.IndexOf(headers, "ze_startdatum");
        var inZoneIndex = Array.IndexOf(headers, "in_zero_emissie_zone");
        if (pc6Index < 0 || zoneIndex < 0 || inZoneIndex < 0)
        {
            return [];
        }

        return rows
            .Skip(1)
            .Where(row => string.Equals(XlsxReader.Cell(row, inZoneIndex), "ja", StringComparison.OrdinalIgnoreCase))
            .Select(row => new ZeZoneLookupRow(
                NormalizePc6(XlsxReader.Cell(row, pc6Index)),
                XlsxReader.Cell(row, zoneIndex).Trim(),
                startIndex >= 0 ? XlsxReader.Cell(row, startIndex).Trim() : ""))
            .Where(row => row.Pc6.Length > 0 && row.Zone.Length > 0)
            .GroupBy(row => row.Pc6, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private static void WriteZeZoneCsv(string path, IReadOnlyList<ZeZoneLookupRow> zones)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("pc6,ze_zone,ze_startdatum");
        foreach (var zone in zones)
        {
            writer.Write(EscapeCsv(zone.Pc6));
            writer.Write(',');
            writer.Write(EscapeCsv(zone.Zone));
            writer.Write(',');
            writer.WriteLine(EscapeCsv(zone.StartDate));
        }
    }

    private static string EscapeCsv(string value)
    {
        return value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(ch);
            }
        }

        cells.Add(cell.ToString());
        return cells.ToArray();
    }

    private static string NormalizePc6(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private sealed record ZeZoneLookupRow(string Pc6, string Zone, string StartDate);

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
                    COALESCE(NULLIF(kenteken_norm, ''), wagencode) AS vehicle_key,
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
            ),
            candidates AS (
                SELECT *
                FROM scored
                WHERE gap_hours BETWEEN 6 AND 72
                    AND end_start_km <= 0.5
            ),
            location_stats AS (
                SELECT
                    vehicle_key,
                    depot_id,
                    COUNT(*) AS events,
                    MAX(trip_date) AS last_seen,
                    COALESCE(quantile_cont(gap_hours, 0.5), 0) AS median_gap_hours
                FROM candidates
                GROUP BY vehicle_key, depot_id
            ),
            location_scores AS (
                SELECT
                    *,
                    ROW_NUMBER() OVER (
                        PARTITION BY vehicle_key
                        ORDER BY events DESC, last_seen DESC, median_gap_hours DESC, depot_id
                    ) AS rn
                FROM location_stats
            )
            SELECT c.*
            FROM candidates c
            JOIN location_scores s
                ON c.vehicle_key = s.vehicle_key
                AND c.depot_id = s.depot_id
            WHERE s.rn = 1;
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
                COUNT(DISTINCT vehicle_key) AS unique_vehicles,
                COALESCE(quantile_cont(gap_hours, 0.5), 0) AS median_gap_hours,
                COALESCE(quantile_cont(day_km, 0.95), 0) AS p95_day_km,
                COALESCE(SUM(day_km), 0) AS total_day_km,
                LEAST(1.0, COUNT(DISTINCT vehicle_key) / 25.0) * 0.6
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

    private static void CreateRouteActionsFromStops(DuckDBConnection connection)
    {
        var vehicleType = HasColumn(connection, "stops", "wagentype_omschrijving")
            ? "COALESCE(CAST(wagentype_omschrijving AS VARCHAR), '')"
            : "''";
        var licensePlate = HasColumn(connection, "stops", "kenteken")
            ? "COALESCE(CAST(kenteken AS VARCHAR), '')"
            : "''";
        var normalizedLicensePlate = HasColumn(connection, "stops", "kenteken_norm")
            ? "COALESCE(CAST(kenteken_norm AS VARCHAR), '')"
            : "''";
        var plannedStart = HasColumn(connection, "stops", "gepland_start")
            ? "TRY_CAST(gepland_start AS TIMESTAMP)"
            : "CAST(trip_date AS TIMESTAMP)";
        var plannedEnd = HasColumn(connection, "stops", "gepland_eind")
            ? "TRY_CAST(gepland_eind AS TIMESTAMP)"
            : "CAST(trip_date AS TIMESTAMP)";

        Execute(connection,
            $$"""
            CREATE OR REPLACE TABLE route_actions AS
            SELECT
                CAST(wagencode AS VARCHAR) AS wagencode,
                COALESCE(CAST(vervoerder AS VARCHAR), '') AS vervoerder,
                CASE
                    WHEN COALESCE(CAST(vervoerder_type AS VARCHAR), '') = 'eigen' THEN 'own'
                    WHEN COALESCE(CAST(vervoerder_type AS VARCHAR), '') = 'charter' THEN 'charter'
                    ELSE 'unknown'
                END AS vehicle_class,
                CAST(trip_date AS DATE) AS trip_date,
                CAST(trip_id AS VARCHAR) AS trip_id,
                COALESCE(CAST(stop_seq AS INTEGER), 0) AS action_seq,
                lower(COALESCE(CAST(acties AS VARCHAR), '')) AS actie_soort,
                COALESCE(CAST(locatie_naam AS VARCHAR), CAST(adres AS VARCHAR), 'unknown_location') AS locatie_naam,
                COALESCE(CAST(adres AS VARCHAR), 'unknown_location') AS adres,
                CASE
                    WHEN lat IS NULL OR lon IS NULL THEN 'unknown_location:' || COALESCE(CAST(adres AS VARCHAR), 'missing')
                    ELSE printf('auto:%.3f:%.3f', ROUND(CAST(lat AS DOUBLE), 3), ROUND(CAST(lon AS DOUBLE), 3))
                END AS location_id,
                {{plannedStart}} AS gepland_start,
                {{plannedEnd}} AS gepland_eind,
                GREATEST(COALESCE(date_diff('second', {{plannedStart}}, {{plannedEnd}}), 0) / 60.0, 0.0) AS dwell_min,
                COALESCE(TRY_CAST(afstand_km AS DOUBLE), 0) AS afstand_km,
                COALESCE(TRY_CAST(afstand_km_trip AS DOUBLE), 0) AS afstand_km_trip,
                TRY_CAST(lat AS DOUBLE) AS lat,
                TRY_CAST(lon AS DOUBLE) AS lon,
                {{licensePlate}} AS kenteken,
                {{normalizedLicensePlate}} AS kenteken_norm,
                {{vehicleType}} AS wagentype_omschrijving
            FROM stops;
            """);
    }

    private static void EnsureStopsZeroEmissionColumns(DuckDBConnection connection)
    {
        if (!HasColumn(connection, "stops", "pc6"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN pc6 VARCHAR DEFAULT '';");
        }

        if (!HasColumn(connection, "stops", "ze_zone"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN ze_zone VARCHAR DEFAULT '';");
        }

        if (!HasColumn(connection, "stops", "ze_startdatum"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN ze_startdatum VARCHAR DEFAULT '';");
        }

        if (!HasColumn(connection, "stops", "in_zez"))
        {
            Execute(connection, "ALTER TABLE stops ADD COLUMN in_zez BOOLEAN DEFAULT false;");
        }
    }

    private static IReadOnlyDictionary<string, string> GetColumns(DuckDBConnection connection, string relation)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM {relation};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var column = Convert.ToString(reader["column_name"]) ?? "";
            if (!string.IsNullOrWhiteSpace(column))
            {
                columns[column.Trim()] = column;
            }
        }

        return columns;
    }

    private static string? TryColumnSql(IReadOnlyDictionary<string, string> columns, params string[] names)
    {
        foreach (var name in names)
        {
            if (columns.TryGetValue(name, out var column))
            {
                return QuoteIdentifier(column);
            }
        }

        return null;
    }

    private static string CoalesceTextSql(IReadOnlyDictionary<string, string> columns, params string[] names)
    {
        var matches = names
            .Select(name => columns.TryGetValue(name, out var column) ? QuoteIdentifier(column) : null)
            .Where(column => column is not null)
            .Cast<string>()
            .Distinct()
            .ToArray();

        return matches.Length == 0
            ? "''"
            : $"COALESCE({string.Join(", ", matches)}, '')";
    }

    private static string CoalesceTimestampSql(IReadOnlyDictionary<string, string> columns, params string[] names)
    {
        var matches = names
            .Select(name => columns.TryGetValue(name, out var column) ? ParseTimestampSql(QuoteIdentifier(column)) : null)
            .Where(column => column is not null)
            .Cast<string>()
            .Distinct()
            .ToArray();

        return matches.Length == 0
            ? "NULL"
            : $"COALESCE({string.Join(", ", matches)})";
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

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    internal static string SqlString(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string QuoteIdentifier(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
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
                try_strptime({value}, '%Y-%m-%d %H:%M'),
                try_strptime({value}, '%d-%m-%Y'),
                try_strptime({value}, '%d/%m/%Y'),
                try_strptime({value}, '%Y-%m-%d')
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

using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Caching.Memory;
using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const int HeatAggregationThreshold = 50_000;
    private const long MaxDatasetFileBytes = 2L * 1024 * 1024 * 1024;
    private const double LogicalRoadMaxLengthKm = 8.0;
    private const int MaxRoadTargetRows = 25_000;
    private const int MaxRoadRawRows = 200_000;
    private const int MaxRoadOutputRows = 4_000;
    private const int MaxRoadHeatRows = 300_000;
    private static readonly JsonSerializerOptions CacheJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DuckDbRouteStore _store;
    private readonly IMemoryCache _cache;

    public RouteAnalysisService(DuckDbRouteStore store, IMemoryCache cache)
    {
        _store = store;
        _cache = cache;
    }

    public async Task<DatasetUploadResult> ReplaceDatasetAsync(IReadOnlyList<IBrowserFile> files, CancellationToken cancellationToken = default)
    {
        var acceptedFiles = files
            .Where(file => IsAcceptedDatasetFile(file.Name))
            .ToArray();
        if (acceptedFiles.Length == 0)
        {
            return new DatasetUploadResult(false, "Upload minimaal een CSV- of parquetbestand.", null, 0, 0);
        }

        var uploadRoot = Path.GetDirectoryName(_store.Options.UploadedDatasetDir)!;
        var pending = Path.Combine(uploadRoot, "pending-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(uploadRoot, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uploadRoot);
        Directory.CreateDirectory(pending);

        try
        {
            long totalBytes = 0;
            foreach (var file in acceptedFiles)
            {
                var fileName = SafeFileName(file.Name);
                var target = Path.Combine(pending, fileName);
                await using var source = file.OpenReadStream(MaxDatasetFileBytes, cancellationToken);
                await using var destination = File.Create(target);
                await source.CopyToAsync(destination, cancellationToken);
                totalBytes += new FileInfo(target).Length;
            }

            if (Directory.Exists(_store.Options.UploadedDatasetDir))
            {
                Directory.Move(_store.Options.UploadedDatasetDir, backup);
            }

            Directory.Move(pending, _store.Options.UploadedDatasetDir);

            try
            {
                await _store.ResetAsync(cancellationToken);
                ClearCache();
                var metadata = await GetMetadataAsync(cancellationToken);
                return metadata.DataAvailable
                    ? new DatasetUploadResult(true, "Dataset verwerkt en actief gemaakt.", metadata.DataSource, acceptedFiles.Length, totalBytes)
                    : new DatasetUploadResult(false, "Dataset is opgeslagen, maar bevat geen bruikbare ritregels.", null, acceptedFiles.Length, totalBytes);
            }
            catch (Exception ex)
            {
                DeleteDirectoryIfExists(_store.Options.UploadedDatasetDir);
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, _store.Options.UploadedDatasetDir);
                }

                await _store.ResetAsync(cancellationToken);
                ClearCache();
                return new DatasetUploadResult(false, $"Dataset kon niet worden verwerkt: {ex.Message}", null, acceptedFiles.Length, totalBytes);
            }
        }
        finally
        {
            DeleteDirectoryIfExists(pending);
            DeleteDirectoryIfExists(backup);
        }
    }

    public async Task<MetadataResponse> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new MetadataResponse(
                false,
                null,
                null,
                null,
                0,
                0,
                0,
                [],
                [],
                [],
                [],
                _store.GetCacheStatus());
        }

        return await GetOrCreateAsync("metadata", async () =>
        {
            using var connection = OpenConnection();
            var totals = await QuerySingleAsync(
                connection,
                """
                SELECT
                    COUNT(*) AS stop_count,
                    COUNT(DISTINCT trip_id) AS trip_count,
                    COUNT(DISTINCT wagencode) AS wagen_count,
                    MIN(CAST(trip_date AS DATE)) AS min_date,
                    MAX(CAST(trip_date AS DATE)) AS max_date
                FROM stops
                WHERE lat IS NOT NULL AND lon IS NOT NULL;
                """,
                ReadTotals,
                cancellationToken);

            return new MetadataResponse(
                true,
                _store.StopsSourceLabel,
                totals.MinDate,
                totals.MaxDate,
                totals.StopCount,
                totals.TripCount,
                totals.WagenCount,
                await QueryStringArrayAsync(connection, "SELECT DISTINCT vervoerder_type FROM stops WHERE vervoerder_type IS NOT NULL ORDER BY vervoerder_type;", cancellationToken),
                await QueryStringArrayAsync(connection, "SELECT DISTINCT vervoerder FROM stops WHERE vervoerder IS NOT NULL ORDER BY vervoerder;", cancellationToken),
                await QueryStringArrayAsync(connection, "SELECT DISTINCT wagencode FROM stops WHERE wagencode IS NOT NULL ORDER BY wagencode;", cancellationToken),
                await QueryStringArrayAsync(connection, "SELECT DISTINCT ze_zone FROM stops WHERE COALESCE(CAST(in_zez AS BOOLEAN), false) AND ze_zone IS NOT NULL AND ze_zone <> '' ORDER BY ze_zone;", cancellationToken),
                _store.GetCacheStatus());
        });
    }

    public async Task<SummaryResponse> GetSummaryAsync(AnalysisFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new SummaryResponse(0, 0, 0, 0, 0, 0, 0, 0, false);
        }

        var normalized = NormalizeFilter(filter);
        var key = CacheKey("summary", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildWhere(normalized);
            var tripWhere = _store.HasView("daily_trips")
                ? BuildDailyTripWhere(normalized)
                : null;
            var filteredTripsSql = tripWhere is null
                ? "SELECT trip_id, wagencode, vervoerder_type, SUM(afstand_km) AS distance_km FROM filtered_stops GROUP BY trip_id, wagencode, vervoerder_type"
                : $"SELECT trip_id, wagencode, vervoerder_type, distance_km FROM daily_trips WHERE {tripWhere}";
            return await QuerySingleAsync(
                connection,
                $$"""
                WITH filtered_stops AS (
                    SELECT
                        trip_id,
                        wagencode,
                        vervoerder_type,
                        COALESCE(CAST(afstand_km AS DOUBLE), 0) AS afstand_km,
                        COALESCE(CAST(dwell_min AS DOUBLE), 0) AS dwell_min
                    FROM stops
                    WHERE {{where}}
                ),
                filtered_trips AS (
                    {{filteredTripsSql}}
                )
                SELECT
                    (SELECT COUNT(*) FROM filtered_stops) AS stops,
                    (SELECT COUNT(DISTINCT trip_id) FROM filtered_stops) AS trips,
                    (SELECT COUNT(DISTINCT wagencode) FROM filtered_stops) AS wagens,
                    COALESCE((SELECT SUM(distance_km) FROM filtered_trips), 0) AS total_km,
                    COALESCE((SELECT AVG(distance_km) FROM filtered_trips), 0) AS avg_trip_km,
                    COALESCE((SELECT SUM(dwell_min) FROM filtered_stops), 0) / 60.0 AS dwell_hours,
                    COALESCE((SELECT SUM(CASE WHEN vervoerder_type = 'eigen' THEN distance_km ELSE 0 END) FROM filtered_trips), 0) AS eigen_km,
                    COALESCE((SELECT SUM(CASE WHEN vervoerder_type = 'charter' THEN distance_km ELSE 0 END) FROM filtered_trips), 0) AS charter_km;
                """,
                reader =>
                {
                    var totalKm = GetDouble(reader, "total_km");
                    return new SummaryResponse(
                        GetInt64(reader, "stops"),
                        GetInt64(reader, "trips"),
                        GetInt64(reader, "wagens"),
                        Math.Round(totalKm, 1),
                        Math.Round(GetDouble(reader, "avg_trip_km"), 1),
                        Math.Round(GetDouble(reader, "dwell_hours"), 1),
                        totalKm > 0 ? Math.Round(100 * GetDouble(reader, "eigen_km") / totalKm, 1) : 0,
                        totalKm > 0 ? Math.Round(100 * GetDouble(reader, "charter_km") / totalKm, 1) : 0,
                        true);
                },
                cancellationToken);
        });
    }

    public async Task<StopMapResponse> GetStopMapAsync(AnalysisFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new StopMapResponse([], [], false, 0, false);
        }

        var normalized = NormalizeFilter(filter);
        var key = CacheKey("map-stops", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildWhere(normalized);
            var sourceStops = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM stops WHERE {where};", cancellationToken);
            var markerTopN = Math.Clamp(normalized.MarkerTopN, 50, 5_000);

            var heat = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    ROUND(CAST(lat AS DOUBLE), 3) AS lat,
                    ROUND(CAST(lon AS DOUBLE), 3) AS lon,
                    GREATEST(COALESCE(SUM(CAST(dwell_min AS DOUBLE)), 0), COUNT(*)) AS weight
                FROM stops
                WHERE {{where}}
                GROUP BY 1, 2
                ORDER BY weight DESC
                LIMIT 25000;
                """,
                r => new HeatPoint(GetDouble(r, "lat"), GetDouble(r, "lon"), GetDouble(r, "weight")),
                cancellationToken);

            var markers = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    ROUND(CAST(lat AS DOUBLE), 3) AS lat,
                    ROUND(CAST(lon AS DOUBLE), 3) AS lon,
                    COALESCE(MODE(locatie_naam), '') AS name,
                    COALESCE(MODE(adres), '') AS address,
                    COUNT(DISTINCT wagencode) AS unique_wagens,
                    COUNT(*) AS stops,
                    COUNT(DISTINCT trip_id) AS trips,
                    COALESCE(AVG(CAST(dwell_min AS DOUBLE)), 0) AS avg_dwell_min
                FROM stops
                WHERE {{where}}
                GROUP BY 1, 2
                ORDER BY unique_wagens DESC, stops DESC
                LIMIT {{markerTopN}};
                """,
                r => new StopMarker(
                    GetDouble(r, "lat"),
                    GetDouble(r, "lon"),
                    GetString(r, "name"),
                    GetString(r, "address"),
                    GetInt64(r, "unique_wagens"),
                    GetInt64(r, "stops"),
                    GetInt64(r, "trips"),
                    Math.Round(GetDouble(r, "avg_dwell_min"), 1)),
                cancellationToken);

            return new StopMapResponse(
                heat.ToArray(),
                markers.ToArray(),
                sourceStops > HeatAggregationThreshold,
                sourceStops,
                true);
        });
    }

    public async Task<RoadMapResponse> GetRoadMapAsync(AnalysisFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);

        var metadata = await GetMetadataAsync(cancellationToken);
        var variant = ResolveRoadVariant(filter, metadata);
        if (variant is null)
        {
            return new RoadMapResponse(
                "cache_missing",
                null,
                "Wegvlakken zijn voor deze filtercombinatie nog niet beschikbaar.",
                [],
                [],
                false);
        }

        var edgeView = $"edges_{variant}";
        if (!_store.HasView(edgeView))
        {
            return new RoadMapResponse(
                "cache_missing",
                variant,
                "Wegvlakken zijn nog niet beschikbaar.",
                [],
                [],
                false);
        }

        var key = CacheKey("map-roads", new
        {
            Variant = variant,
            MinPassages = Math.Max(1, filter.RoadThreshold),
            TopPct = Math.Clamp(filter.RoadTopPercent, 1, 25),
            Mode = "logical-directional-heat-percent-v2",
        });

        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var minPassages = Math.Max(1, filter.RoadThreshold);
            var topPercent = Math.Clamp(filter.RoadTopPercent, 1, 25);
            var maxRows = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {edgeView} WHERE COALESCE(n_passes, n_wagens) >= {minPassages};", cancellationToken);
            var targetRows = Math.Clamp((int)Math.Ceiling(maxRows * topPercent / 100.0), 50, MaxRoadTargetRows);
            var rawRows = Math.Clamp(targetRows * 8, 250, MaxRoadRawRows);
            var outputRows = Math.Clamp((int)Math.Ceiling(targetRows / 6.0), 50, MaxRoadOutputRows);
            var rawEdges = await QueryListAsync(
                connection,
                $$"""
                SELECT lat1, lon1, lat2, lon2, CAST(n_wagens AS INTEGER) AS n_wagens, CAST(COALESCE(n_passes, n_wagens) AS INTEGER) AS n_passes
                FROM {{edgeView}}
                WHERE COALESCE(n_passes, n_wagens) >= {{minPassages}}
                ORDER BY n_passes DESC, n_wagens DESC
                LIMIT {{rawRows}};
                """,
                r => new RawRoadEdge(
                    GetDouble(r, "lat1"),
                    GetDouble(r, "lon1"),
                    GetDouble(r, "lat2"),
                    GetDouble(r, "lon2"),
                    GetInt32(r, "n_wagens"),
                    GetInt32(r, "n_passes")),
                cancellationToken);

            var heatView = $"road_heat_{variant}";
            List<HeatPoint> heat = [];
            if (_store.HasView(heatView))
            {
                var heatRows = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {heatView} WHERE weight >= {minPassages};", cancellationToken);
                var targetHeatRows = heatRows <= 0
                    ? 0
                    : Math.Clamp((int)Math.Ceiling(heatRows * topPercent / 100.0), 1, MaxRoadHeatRows);
                heat = await QueryListAsync(
                    connection,
                    $"SELECT lat, lon, weight FROM {heatView} WHERE weight >= {minPassages} ORDER BY weight DESC LIMIT {targetHeatRows};",
                    r => new HeatPoint(GetDouble(r, "lat"), GetDouble(r, "lon"), GetDouble(r, "weight")),
                    cancellationToken);
            }

            var lines = BuildLogicalRoadLines(rawEdges)
                .OrderByDescending(x => x.Passes)
                .ThenByDescending(x => x.UniqueWagens)
                .Take(outputRows)
                .ToArray();

            return new RoadMapResponse("ok", variant, null, lines, heat.ToArray(), true);
        });
    }

    public async Task<ChargerMapResponse> GetChargersAsync(ChargerFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);

        if (!_store.HasView("chargers"))
        {
            return new ChargerMapResponse("cache_missing", [], false);
        }

        var normalized = NormalizeChargerFilter(filter);
        var key = CacheKey("chargers", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildChargerWhere(normalized);
            var chargers = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    CAST(LocatieID AS BIGINT) AS id,
                    CAST(lat AS DOUBLE) AS lat,
                    CAST(lon AS DOUBLE) AS lon,
                    COALESCE(CAST(name AS VARCHAR), '') AS name,
                    COALESCE(CAST(operator AS VARCHAR), '') AS operator,
                    COALESCE(CAST(address AS VARCHAR), '') AS address,
                    COALESCE(CAST(town AS VARCHAR), '') AS town,
                    COALESCE(CAST(postcode AS VARCHAR), '') AS postcode,
                    COALESCE(CAST(max_power_kw AS DOUBLE), 0) AS max_power_kw,
                    COALESCE(CAST(n_connectors AS BIGINT), 0) AS n_connectors,
                    COALESCE(CAST(toegankelijkheid AS VARCHAR), '') AS toegankelijkheid,
                    COALESCE(CAST(twentyfour_seven AS VARCHAR), '') AS twentyfour_seven,
                    COALESCE(CAST(dedicated AS VARCHAR), '') AS dedicated,
                    COALESCE(CAST(ccs_mcs AS VARCHAR), '') AS ccs_mcs
                FROM chargers
                WHERE {{where}}
                ORDER BY max_power_kw DESC, n_connectors DESC, name
                LIMIT 1000;
                """,
                r => new ChargerMarker(
                    GetInt64(r, "id"),
                    GetDouble(r, "lat"),
                    GetDouble(r, "lon"),
                    GetString(r, "name"),
                    GetString(r, "operator"),
                    GetString(r, "address"),
                    GetString(r, "town"),
                    GetString(r, "postcode"),
                    GetDouble(r, "max_power_kw"),
                    GetInt64(r, "n_connectors"),
                    GetString(r, "toegankelijkheid"),
                    GetString(r, "twentyfour_seven"),
                    GetString(r, "dedicated"),
                    GetString(r, "ccs_mcs")),
                cancellationToken);

            return new ChargerMapResponse("ok", chargers.ToArray(), true);
        });
    }

    public async Task<DashboardResponse> GetDashboardAsync(AnalysisFilter filter, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new DashboardResponse([], [], [], [], "cache_missing", false);
        }

        var normalized = NormalizeFilter(filter);
        var key = CacheKey("dashboard", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildWhere(normalized);
            var topStops = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    ROUND(CAST(lat AS DOUBLE), 3) AS lat,
                    ROUND(CAST(lon AS DOUBLE), 3) AS lon,
                    COALESCE(MODE(locatie_naam), '') AS name,
                    COALESCE(MODE(adres), '') AS address,
                    COUNT(*) AS stops,
                    COUNT(DISTINCT wagencode) AS wagens,
                    COUNT(DISTINCT trip_id) AS trips,
                    COALESCE(SUM(CAST(dwell_min AS DOUBLE)), 0) / 60.0 AS dwell_hours,
                    COALESCE(AVG(CAST(dwell_min AS DOUBLE)), 0) AS avg_dwell_min
                FROM stops
                WHERE {{where}}
                GROUP BY 1, 2
                ORDER BY wagens DESC, dwell_hours DESC
                LIMIT 50;
                """,
                r => new DashboardStop(
                    GetDouble(r, "lat"),
                    GetDouble(r, "lon"),
                    GetString(r, "name"),
                    GetString(r, "address"),
                    GetInt64(r, "stops"),
                    GetInt64(r, "wagens"),
                    GetInt64(r, "trips"),
                    Math.Round(GetDouble(r, "dwell_hours"), 1),
                    Math.Round(GetDouble(r, "avg_dwell_min"), 1)),
                cancellationToken);

            var zeZones = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    COALESCE(CAST(ze_zone AS VARCHAR), '') AS ze_zone,
                    COALESCE(CAST(ze_startdatum AS VARCHAR), '') AS ze_startdatum,
                    COUNT(*) AS stops,
                    COUNT(DISTINCT trip_id) AS trips,
                    COUNT(DISTINCT wagencode) AS wagens
                FROM stops
                WHERE {{where}}
                    AND COALESCE(CAST(in_zez AS BOOLEAN), false)
                GROUP BY 1, 2
                ORDER BY stops DESC
                LIMIT 50;
                """,
                r => new ZeZoneSummary(
                    GetString(r, "ze_zone"),
                    GetString(r, "ze_startdatum"),
                    GetInt64(r, "stops"),
                    GetInt64(r, "trips"),
                    GetInt64(r, "wagens")),
                cancellationToken);

            var roads = await GetRoadMapAsync(normalized, cancellationToken);
            var corridors = roads.Status == "ok"
                ? BuildLightweightCorridors(roads.Lines)
                : [];

            return new DashboardResponse(
                topStops.ToArray(),
                corridors,
                roads.Lines.Take(100).ToArray(),
                zeZones.ToArray(),
                roads.Status,
                true);
        });
    }

    public async Task<SimulationResponse> GetSimulationAsync(SimulationRequest request, CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);
        if (!_store.HasStops)
        {
            return new SimulationResponse(0, 0, 0, 0, 0, [], [], false);
        }

        var normalized = NormalizeSimulation(request);
        var key = CacheKey("simulation", normalized);
        return await GetOrCreateAsync(key, async () =>
        {
            using var connection = OpenConnection();
            var where = BuildWhere(normalized);
            var rows = await QueryListAsync(
                connection,
                $$"""
                SELECT
                    CAST(wagencode AS VARCHAR) AS wagencode,
                    CAST(trip_date AS VARCHAR) AS trip_date,
                    CAST(trip_id AS VARCHAR) AS trip_id,
                    CAST(stop_seq AS INTEGER) AS stop_seq,
                    CAST(lat AS DOUBLE) AS lat,
                    CAST(lon AS DOUBLE) AS lon,
                    COALESCE(CAST(adres AS VARCHAR), '') AS address
                FROM stops
                WHERE {{where}}
                ORDER BY wagencode, trip_date, trip_id, stop_seq;
                """,
                r => new SimStop(
                    GetString(r, "wagencode"),
                    GetString(r, "trip_date"),
                    GetString(r, "trip_id"),
                    GetInt32(r, "stop_seq"),
                    GetDouble(r, "lat"),
                    GetDouble(r, "lon"),
                    GetString(r, "address")),
                cancellationToken);

            return RunSimulation(rows, normalized);
        });
    }

    public AnalysisFilter NormalizeFilter(AnalysisFilter filter)
    {
        return filter with
        {
            VervoerderTypes = NormalizeArray(filter.VervoerderTypes),
            Vervoerders = NormalizeArray(filter.Vervoerders),
            Wagencodes = NormalizeArray(filter.Wagencodes),
            MinDwellMin = Math.Max(0, filter.MinDwellMin),
            RoadThreshold = Math.Clamp(filter.RoadThreshold, 1, 1_000_000),
            RoadTopPercent = Math.Clamp(filter.RoadTopPercent, 1, 25),
            MarkerTopN = Math.Clamp(filter.MarkerTopN, 50, 5_000),
            ZeZoneMode = NormalizeZeZoneMode(filter.ZeZoneMode),
        };
    }

    private SimulationRequest NormalizeSimulation(SimulationRequest request)
    {
        return request with
        {
            VervoerderTypes = NormalizeArray(request.VervoerderTypes),
            Vervoerders = NormalizeArray(request.Vervoerders),
            Wagencodes = NormalizeArray(request.Wagencodes),
            MinDwellMin = Math.Max(0, request.MinDwellMin),
            RoadThreshold = Math.Clamp(request.RoadThreshold, 1, 1_000_000),
            RoadTopPercent = Math.Clamp(request.RoadTopPercent, 1, 25),
            MarkerTopN = Math.Clamp(request.MarkerTopN, 50, 5_000),
            ZeZoneMode = NormalizeZeZoneMode(request.ZeZoneMode),
            KwhPerKm = Math.Clamp(request.KwhPerKm, 0.5, 3.0),
            CapacityKwh = Math.Clamp(request.CapacityKwh, 100, 1_500),
            StartSocPct = Math.Clamp(request.StartSocPct, 50, 100),
            ThresholdPct = Math.Clamp(request.ThresholdPct, 5, 30),
            MaxChargeKw = Math.Clamp(request.MaxChargeKw, 50, 1_000),
        };
    }

    private static ChargerFilter NormalizeChargerFilter(ChargerFilter filter)
    {
        return filter with
        {
            MinPowerKw = Math.Clamp(filter.MinPowerKw, 0, 2_000),
            MinConnectors = Math.Clamp(filter.MinConnectors, 1, 100),
            Access = NormalizeArray(filter.Access),
        };
    }

    private DuckDBConnection OpenConnection()
    {
        var connection = _store.CreateConnection();
        connection.Open();
        return connection;
    }

    private static DashboardCorridor[] BuildLightweightCorridors(IReadOnlyList<RoadLine> lines)
    {
        return lines
            .GroupBy(line => new
            {
                Lat = Math.Round((line.Lat1 + line.Lat2) / 2, 2),
                Lon = Math.Round((line.Lon1 + line.Lon2) / 2, 2),
            })
            .Select((group, index) =>
            {
                var passes = group.Select(x => x.Passes).Order().ToArray();
                var wagens = group.Select(x => x.UniqueWagens).Order().ToArray();
                var lats = group.SelectMany(x => new[] { x.Lat1, x.Lat2 }).ToArray();
                var lons = group.SelectMany(x => new[] { x.Lon1, x.Lon2 }).ToArray();
                return new DashboardCorridor(
                    index + 1,
                    group.Key.Lat,
                    group.Key.Lon,
                    passes[passes.Length / 2],
                    passes[^1],
                    wagens[^1],
                    Math.Round(HaversineKm(lats.Min(), lons.Min(), lats.Max(), lons.Max()), 1),
                    group.Count());
            })
            .OrderByDescending(x => x.MedianPasses)
            .ThenByDescending(x => x.MaxWagens)
            .Take(20)
            .Select((x, i) => x with { Rank = i + 1 })
            .ToArray();
    }

    private static RoadLine[] BuildLogicalRoadLines(IReadOnlyList<RawRoadEdge> rawEdges)
    {
        var edges = rawEdges
            .Where(edge => edge.Lat1 != edge.Lat2 || edge.Lon1 != edge.Lon2)
            .Select((edge, index) => EnrichRoadEdge(edge, index))
            .Where(edge => edge.LengthKm > 0)
            .ToArray();
        if (edges.Length == 0)
        {
            return [];
        }

        var outgoing = edges
            .GroupBy(edge => RoadNodeKey(edge.DirectionBucket, edge.StartKey))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var incoming = edges
            .GroupBy(edge => RoadNodeKey(edge.DirectionBucket, edge.EndKey))
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var visited = new HashSet<int>();
        var logical = new List<RoadLine>();
        foreach (var edge in edges.OrderByDescending(x => x.Passes).ThenByDescending(x => x.UniqueWagens))
        {
            if (visited.Contains(edge.Index))
            {
                continue;
            }

            var chain = BuildRoadChain(edge, incoming, outgoing, visited);
            if (chain.Count == 0)
            {
                continue;
            }

            foreach (var segment in SplitRoadChain(chain, LogicalRoadMaxLengthKm))
            {
                logical.Add(ToRoadLine(segment));
            }
        }

        return logical.ToArray();
    }

    private static List<PreparedRoadEdge> BuildRoadChain(
        PreparedRoadEdge seed,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> incoming,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> outgoing,
        HashSet<int> visited)
    {
        var chain = new LinkedList<PreparedRoadEdge>();
        chain.AddLast(seed);
        visited.Add(seed.Index);

        ExtendBackward(chain, incoming, outgoing, visited);
        ExtendForward(chain, incoming, outgoing, visited);
        return chain.ToList();
    }

    private static void ExtendBackward(
        LinkedList<PreparedRoadEdge> chain,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> incoming,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> outgoing,
        HashSet<int> visited)
    {
        while (chain.First is not null)
        {
            var first = chain.First.Value;
            var node = RoadNodeKey(first.DirectionBucket, first.StartKey);
            if (!incoming.TryGetValue(node, out var inEdges)
                || !outgoing.TryGetValue(node, out var outEdges)
                || inEdges.Count != 1
                || outEdges.Count != 1)
            {
                return;
            }

            var previous = inEdges[0];
            if (visited.Contains(previous.Index) || previous.EndKey != first.StartKey)
            {
                return;
            }

            var currentLength = chain.Sum(x => x.LengthKm);
            if (currentLength >= 1.0 && currentLength + previous.LengthKm > LogicalRoadMaxLengthKm)
            {
                return;
            }

            chain.AddFirst(previous);
            visited.Add(previous.Index);
        }
    }

    private static void ExtendForward(
        LinkedList<PreparedRoadEdge> chain,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> incoming,
        IReadOnlyDictionary<string, List<PreparedRoadEdge>> outgoing,
        HashSet<int> visited)
    {
        while (chain.Last is not null)
        {
            var last = chain.Last.Value;
            var node = RoadNodeKey(last.DirectionBucket, last.EndKey);
            if (!incoming.TryGetValue(node, out var inEdges)
                || !outgoing.TryGetValue(node, out var outEdges)
                || inEdges.Count != 1
                || outEdges.Count != 1)
            {
                return;
            }

            var next = outEdges[0];
            if (visited.Contains(next.Index) || next.StartKey != last.EndKey)
            {
                return;
            }

            var currentLength = chain.Sum(x => x.LengthKm);
            if (currentLength >= 1.0 && currentLength + next.LengthKm > LogicalRoadMaxLengthKm)
            {
                return;
            }

            chain.AddLast(next);
            visited.Add(next.Index);
        }
    }

    private static IEnumerable<IReadOnlyList<PreparedRoadEdge>> SplitRoadChain(
        IReadOnlyList<PreparedRoadEdge> chain,
        double maxLengthKm)
    {
        var current = new List<PreparedRoadEdge>();
        var length = 0.0;
        foreach (var edge in chain)
        {
            if (current.Count > 0 && length >= 1.0 && length + edge.LengthKm > maxLengthKm)
            {
                yield return current.ToArray();
                current.Clear();
                length = 0;
            }

            current.Add(edge);
            length += edge.LengthKm;
        }

        if (current.Count > 0)
        {
            yield return current.ToArray();
        }
    }

    private static RoadLine ToRoadLine(IReadOnlyList<PreparedRoadEdge> chain)
    {
        var points = new List<RoadPoint> { new(chain[0].Lat1, chain[0].Lon1) };
        points.AddRange(chain.Select(edge => new RoadPoint(edge.Lat2, edge.Lon2)));
        var simplified = SimplifyRoadPoints(points);
        var lat1 = simplified[0].Lat;
        var lon1 = simplified[0].Lon;
        var lat2 = simplified[^1].Lat;
        var lon2 = simplified[^1].Lon;
        var bearing = BearingDegrees(lat1, lon1, lat2, lon2);
        var lengthKm = Math.Round(chain.Sum(edge => edge.LengthKm), 2);
        var midLat = Math.Round((lat1 + lat2) / 2, 3);
        var midLon = Math.Round((lon1 + lon2) / 2, 3);
        var segmentId = string.Create(
            CultureInfo.InvariantCulture,
            $"weg:{DirectionBucket(bearing)}:{midLat:0.000}:{midLon:0.000}:{chain.Count}");
        var roadName = InferRoadName((lat1 + lat2) / 2.0, (lon1 + lon2) / 2.0);
        return new RoadLine(
            Math.Round(lat1, 6),
            Math.Round(lon1, 6),
            Math.Round(lat2, 6),
            Math.Round(lon2, 6),
            chain.Max(edge => edge.UniqueWagens),
            chain.Max(edge => edge.Passes),
            segmentId,
            roadName,
            CompassDirection(bearing),
            Math.Round(bearing, 0),
            lengthKm,
            chain.Count,
            Math.Round(Math.Clamp(lengthKm / 2.0 + 1.5, 1.5, 20), 2),
            simplified);
    }

    private static string InferRoadName(double lat, double lon)
    {
        var best = KnownRoadCorridors
            .Select(corridor => new
            {
                corridor.Name,
                DistanceKm = DistanceToPolylineKm(lat, lon, corridor.Points)
            })
            .OrderBy(x => x.DistanceKm)
            .FirstOrDefault();

        return best is not null && best.DistanceKm <= 3.0
            ? best.Name
            : string.Create(CultureInfo.InvariantCulture, $"Wegvlak bij {lat:0.000}, {lon:0.000}");
    }

    private static double DistanceToPolylineKm(double lat, double lon, IReadOnlyList<RoadPoint> points)
    {
        if (points.Count == 0)
        {
            return double.MaxValue;
        }

        if (points.Count == 1)
        {
            return HaversineKm(lat, lon, points[0].Lat, points[0].Lon);
        }

        var min = double.MaxValue;
        for (var i = 0; i < points.Count - 1; i++)
        {
            min = Math.Min(min, DistancePointToSegmentKm(lat, lon, points[i].Lat, points[i].Lon, points[i + 1].Lat, points[i + 1].Lon));
        }

        return min;
    }

    private static RoadPoint[] SimplifyRoadPoints(IReadOnlyList<RoadPoint> points)
    {
        if (points.Count <= 80)
        {
            return points.ToArray();
        }

        var step = Math.Max(1, (int)Math.Ceiling(points.Count / 78.0));
        var result = new List<RoadPoint> { points[0] };
        for (var i = step; i < points.Count - 1; i += step)
        {
            result.Add(points[i]);
        }

        result.Add(points[^1]);
        return result.ToArray();
    }

    private static PreparedRoadEdge EnrichRoadEdge(RawRoadEdge edge, int index)
    {
        var bearing = BearingDegrees(edge.Lat1, edge.Lon1, edge.Lat2, edge.Lon2);
        return new PreparedRoadEdge(
            index,
            edge.Lat1,
            edge.Lon1,
            edge.Lat2,
            edge.Lon2,
            edge.UniqueWagens,
            edge.Passes,
            Bearing: bearing,
            DirectionBucket: DirectionBucket(bearing),
            StartKey: CoordinateKey(edge.Lat1, edge.Lon1),
            EndKey: CoordinateKey(edge.Lat2, edge.Lon2),
            LengthKm: HaversineKm(edge.Lat1, edge.Lon1, edge.Lat2, edge.Lon2));
    }

    private static string CoordinateKey(double lat, double lon)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(lat, 5):0.00000}:{Math.Round(lon, 5):0.00000}");
    }

    private static string RoadNodeKey(int bucket, string coordinateKey) => $"{bucket}|{coordinateKey}";

    private static int DirectionBucket(double bearing)
    {
        return (int)Math.Floor(((bearing + 22.5) % 360) / 45.0);
    }

    private static string CompassDirection(double bearing)
    {
        return DirectionBucket(bearing) switch
        {
            0 => "richting noorden",
            1 => "richting noordoosten",
            2 => "richting oosten",
            3 => "richting zuidoosten",
            4 => "richting zuiden",
            5 => "richting zuidwesten",
            6 => "richting westen",
            _ => "richting noordwesten",
        };
    }

    private static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var lambda1 = DegreesToRadians(lon1);
        var lambda2 = DegreesToRadians(lon2);
        var y = Math.Sin(lambda2 - lambda1) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2)
            - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(lambda2 - lambda1);
        return (RadiansToDegrees(Math.Atan2(y, x)) + 360.0) % 360.0;
    }

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static SimulationResponse RunSimulation(IReadOnlyList<SimStop> stops, SimulationRequest request)
    {
        var thresholdKwh = request.CapacityKwh * (request.ThresholdPct / 100.0);
        var startKwh = request.CapacityKwh * (request.StartSocPct / 100.0);
        var currentTrip = "";
        var currentWagen = "";
        var currentTripDate = "";
        double? prevLat = null;
        double? prevLon = null;
        var soc = startKwh;
        var tripKm = 0.0;
        var tripChargeEvents = 0L;
        var tripChargeKwh = 0.0;
        var tripChargeMin = 0.0;
        var tripStops = 0L;
        var lastSocPct = request.StartSocPct;

        var tripSummaries = new List<TripSimulationSummary>();
        var eventRows = new List<(SimStop Stop, double ChargeKwh, double ChargeMin)>();

        void FlushTrip()
        {
            if (string.IsNullOrEmpty(currentTrip))
            {
                return;
            }

            tripSummaries.Add(new TripSimulationSummary(
                currentTrip,
                tripStops,
                Math.Round(tripKm, 1),
                Math.Round(lastSocPct, 1),
                tripChargeEvents,
                Math.Round(tripChargeKwh, 1),
                Math.Round(tripChargeMin, 1)));
        }

        foreach (var stop in stops)
        {
            if (stop.TripId != currentTrip || stop.Wagencode != currentWagen || stop.TripDate != currentTripDate)
            {
                FlushTrip();
                currentTrip = stop.TripId;
                currentWagen = stop.Wagencode;
                currentTripDate = stop.TripDate;
                prevLat = null;
                prevLon = null;
                soc = startKwh;
                tripKm = 0;
                tripChargeEvents = 0;
                tripChargeKwh = 0;
                tripChargeMin = 0;
                tripStops = 0;
                lastSocPct = request.StartSocPct;
            }

            tripStops++;
            if (prevLat is not null && prevLon is not null)
            {
                var km = HaversineKm(prevLat.Value, prevLon.Value, stop.Lat, stop.Lon);
                tripKm += km;
                soc -= km * request.KwhPerKm;
            }

            if (soc < thresholdKwh)
            {
                var chargeKwh = request.CapacityKwh - Math.Max(soc, 0);
                var chargeMin = chargeKwh / Math.Max(request.MaxChargeKw, 1) * 60;
                tripChargeEvents++;
                tripChargeKwh += chargeKwh;
                tripChargeMin += chargeMin;
                eventRows.Add((stop, chargeKwh, chargeMin));
                soc = request.CapacityKwh;
            }

            lastSocPct = soc / request.CapacityKwh * 100;
            prevLat = stop.Lat;
            prevLon = stop.Lon;
        }

        FlushTrip();

        var hotspots = eventRows
            .GroupBy(x => new { Lat = Math.Round(x.Stop.Lat, 3), Lon = Math.Round(x.Stop.Lon, 3), x.Stop.Address })
            .Select(group => new SimulationHotspot(
                group.Key.Lat,
                group.Key.Lon,
                group.Key.Address,
                group.LongCount(),
                group.Select(x => x.Stop.Wagencode).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
                Math.Round(group.Sum(x => x.ChargeKwh), 0),
                Math.Round(group.Average(x => x.ChargeMin), 1)))
            .OrderByDescending(x => x.Events)
            .ThenByDescending(x => x.TotalKwh)
            .Take(50)
            .ToArray();

        return new SimulationResponse(
            tripSummaries.LongCount(),
            tripSummaries.LongCount(x => x.ChargeEvents > 0),
            eventRows.LongCount(),
            Math.Round(eventRows.Sum(x => x.ChargeKwh), 0),
            Math.Round(eventRows.Sum(x => x.ChargeMin) / 60.0, 1),
            hotspots,
            tripSummaries.OrderByDescending(x => x.ChargeEvents).ThenByDescending(x => x.ChargeKwh).Take(100).ToArray(),
            true);
    }

    internal static string SqlDate(DateOnly date)
    {
        return "DATE " + DuckDbRouteStore.SqlString(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void AddDateRange(List<string> parts, DateOnly? from, DateOnly? to, string column = "trip_date")
    {
        if (from is not null)
        {
            parts.Add($"{column} >= {SqlDate(from.Value)}");
        }

        if (to is not null)
        {
            parts.Add($"{column} <= {SqlDate(to.Value)}");
        }
    }

    private static string BuildWhere(AnalysisFilter filter)
    {
        var parts = new List<string>
        {
            "lat IS NOT NULL",
            "lon IS NOT NULL",
            "NOT COALESCE(CAST(acties AS VARCHAR), '') ILIKE '%Administrative%'",
        };

        AddDateRange(parts, filter.DateFrom, filter.DateTo, "CAST(trip_date AS DATE)");

        if (filter.MinDwellMin > 0)
        {
            parts.Add($"COALESCE(CAST(dwell_min AS DOUBLE), 0) >= {filter.MinDwellMin.ToString(CultureInfo.InvariantCulture)}");
        }

        AddIn(parts, "vervoerder_type", filter.VervoerderTypes);
        AddIn(parts, "vervoerder", filter.Vervoerders);
        AddVehicleIn(parts, filter.Wagencodes);
        AddZeZoneFilter(parts, filter.ZeZoneMode);

        return string.Join(" AND ", parts);
    }

    private static void AddZeZoneFilter(List<string> parts, string mode)
    {
        if (string.Equals(mode, "in", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("COALESCE(CAST(in_zez AS BOOLEAN), false)");
        }
        else if (string.Equals(mode, "out", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("NOT COALESCE(CAST(in_zez AS BOOLEAN), false)");
        }
    }

    private static string NormalizeZeZoneMode(string? mode)
    {
        return string.Equals(mode, "in", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "out", StringComparison.OrdinalIgnoreCase)
                ? mode!.ToLowerInvariant()
                : "all";
    }

    private static string BuildChargerWhere(ChargerFilter filter)
    {
        var parts = new List<string>
        {
            "lat IS NOT NULL",
            "lon IS NOT NULL",
            $"COALESCE(CAST(max_power_kw AS DOUBLE), 0) >= {filter.MinPowerKw.ToString(CultureInfo.InvariantCulture)}",
            $"COALESCE(CAST(n_connectors AS BIGINT), 0) >= {filter.MinConnectors}",
        };

        if (filter.OnlyDedicated)
        {
            parts.Add("COALESCE(CAST(dedicated AS VARCHAR), '') = 'Ja'");
        }

        AddIn(parts, "toegankelijkheid", filter.Access);
        return string.Join(" AND ", parts);
    }

    private static void AddIn(List<string> parts, string column, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        parts.Add($"CAST({column} AS VARCHAR) IN ({string.Join(", ", values.Select(DuckDbRouteStore.SqlString))})");
    }

    private static void AddVehicleIn(List<string> parts, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var rawValues = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedPlates = rawValues
            .Select(NormalizeLicensePlate)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rawValues.Length == 0)
        {
            return;
        }

        var rawSql = string.Join(", ", rawValues.Select(DuckDbRouteStore.SqlString));
        var normalizedSql = string.Join(", ", normalizedPlates.Select(DuckDbRouteStore.SqlString));
        var plateClause = normalizedPlates.Length == 0
            ? "FALSE"
            : $"CAST(kenteken_norm AS VARCHAR) IN ({normalizedSql})";

        parts.Add($"(CAST(wagencode AS VARCHAR) IN ({rawSql}) OR CAST(kenteken AS VARCHAR) IN ({rawSql}) OR {plateClause})");
    }

    private static string NormalizeLicensePlate(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }

    private static string? ResolveRoadVariant(AnalysisFilter filter, MetadataResponse metadata)
    {
        if (filter.Vervoerders.Length > 0 || filter.Wagencodes.Length > 0 || filter.MinDwellMin > 0)
        {
            return null;
        }

        if (filter.DateFrom is not null && metadata.MinDate is not null && filter.DateFrom > metadata.MinDate)
        {
            return null;
        }

        if (filter.DateTo is not null && metadata.MaxDate is not null && filter.DateTo < metadata.MaxDate)
        {
            return null;
        }

        var types = NormalizeArray(filter.VervoerderTypes);
        if (types.Length == 0)
        {
            return "full";
        }

        if (types is ["eigen"])
        {
            return "eigen";
        }

        if (types is ["charter"])
        {
            return "charter";
        }

        return null;
    }

    private async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
    {
        if (_cache.TryGetValue(key, out T? value) && value is not null)
        {
            return value;
        }

        value = await factory();
        _cache.Set(key, value, TimeSpan.FromMinutes(20));
        return value;
    }

    private static string CacheKey(string prefix, object payload)
    {
        return prefix + ":" + JsonSerializer.Serialize(payload, CacheJsonOptions);
    }

    private static string[] NormalizeArray(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsAcceptedDatasetFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".parquet", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "dataset.csv" : name;
    }

    private void ClearCache()
    {
        if (_cache is MemoryCache memoryCache)
        {
            memoryCache.Clear();
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<long> ScalarLongAsync(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static async Task<string[]> QueryStringArrayAsync(DuckDBConnection connection, string sql, CancellationToken cancellationToken)
    {
        return (await QueryListAsync(connection, sql, r => GetString(r, 0), cancellationToken)).ToArray();
    }

    private static async Task<T> QuerySingleAsync<T>(
        DuckDBConnection connection,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var rows = await QueryListAsync(connection, sql, map, cancellationToken);
        return rows.Count == 0 ? throw new InvalidOperationException("Query returned no rows.") : rows[0];
    }

    private static async Task<List<T>> QueryListAsync<T>(
        DuckDBConnection connection,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static (long StopCount, long TripCount, long WagenCount, DateOnly? MinDate, DateOnly? MaxDate) ReadTotals(DbDataReader reader)
    {
        return (
            GetInt64(reader, "stop_count"),
            GetInt64(reader, "trip_count"),
            GetInt64(reader, "wagen_count"),
            GetDateOnly(reader, "min_date"),
            GetDateOnly(reader, "max_date"));
    }

    private static string GetString(DbDataReader reader, string name) => GetString(reader, reader.GetOrdinal(name));

    private static string GetString(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
    }

    private static long GetInt64(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        var value = reader.GetValue(ordinal);
        return value is System.Numerics.BigInteger bigInteger
            ? (long)bigInteger
            : Convert.ToInt64(value);
    }

    private static int GetInt32(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static double GetDouble(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal));
    }

    private static DateOnly? GetDateOnly(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => DateOnly.TryParse(Convert.ToString(value), out var parsed) ? parsed : null,
        };
    }

    /// <summary>Splitst een aanwezigheidsvenster in klokuur-slots met de overlap in uren per slot.</summary>
    private static IEnumerable<(DateTime Slot, double OverlapHours)> EnumerateHourSlots(DateTime start, DateTime end)
    {
        var cursor = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0);
        while (cursor < end)
        {
            var next = cursor.AddHours(1);
            var overlapStart = start > cursor ? start : cursor;
            var overlapEnd = end < next ? end : next;
            var overlapHours = (overlapEnd - overlapStart).TotalHours;
            if (overlapHours > 0)
            {
                yield return (cursor, overlapHours);
            }

            cursor = next;
        }
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371.0;
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dphi = DegreesToRadians(lat2 - lat1);
        var dlambda = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dphi / 2) * Math.Sin(dphi / 2)
            + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dlambda / 2) * Math.Sin(dlambda / 2);
        return 2 * radius * Math.Asin(Math.Sqrt(a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static readonly KnownRoadCorridor[] KnownRoadCorridors =
    [
        new("A1", [new(52.36, 4.95), new(52.23, 5.18), new(52.20, 5.45), new(52.22, 5.96), new(52.24, 6.16), new(52.26, 6.79)]),
        new("A2", [new(52.37, 4.90), new(52.09, 5.04), new(52.02, 5.12), new(51.69, 5.31), new(51.44, 5.47), new(51.21, 5.71), new(50.85, 5.69)]),
        new("A4", [new(52.39, 4.80), new(52.17, 4.46), new(52.06, 4.34), new(51.91, 4.47), new(51.49, 4.28)]),
        new("A6", [new(52.33, 5.15), new(52.40, 5.25), new(52.52, 5.45), new(52.65, 5.73), new(52.77, 5.83)]),
        new("A7", [new(52.93, 5.04), new(53.04, 5.66), new(53.20, 6.57), new(53.19, 6.74)]),
        new("A9", [new(52.31, 4.75), new(52.29, 4.94), new(52.32, 5.10)]),
        new("A10", [new(52.39, 4.78), new(52.43, 4.88), new(52.39, 4.98), new(52.33, 4.89), new(52.36, 4.80)]),
        new("A12", [new(52.05, 4.32), new(52.07, 4.65), new(52.07, 5.12), new(52.05, 5.34), new(52.03, 5.66), new(52.00, 6.00)]),
        new("A15", [new(51.89, 4.34), new(51.88, 4.65), new(51.89, 5.05), new(51.87, 5.46), new(51.88, 5.85)]),
        new("A16", [new(51.92, 4.47), new(51.81, 4.64), new(51.59, 4.78), new(51.49, 4.74)]),
        new("A20", [new(51.96, 4.18), new(51.92, 4.38), new(51.92, 4.54), new(51.95, 4.67)]),
        new("A27", [new(51.58, 4.78), new(51.70, 4.86), new(51.89, 5.00), new(52.05, 5.15), new(52.22, 5.18), new(52.31, 5.24)]),
        new("A28", [new(52.09, 5.12), new(52.16, 5.37), new(52.35, 5.65), new(52.55, 6.09), new(52.69, 6.19), new(52.79, 6.48)]),
        new("A50", [new(51.66, 5.62), new(51.75, 5.73), new(51.93, 5.83), new(52.12, 5.97), new(52.24, 6.00), new(52.38, 6.08)]),
        new("A58", [new(51.43, 3.57), new(51.50, 4.30), new(51.56, 4.77), new(51.55, 5.09), new(51.44, 5.48)]),
        new("A67", [new(51.37, 5.22), new(51.39, 5.48), new(51.37, 5.77), new(51.34, 6.05), new(51.35, 6.17)]),
        new("A73", [new(51.83, 5.86), new(51.68, 5.91), new(51.44, 6.02), new(51.20, 6.02), new(50.88, 5.97)])
    ];

    private sealed record KnownRoadCorridor(string Name, RoadPoint[] Points);

    private sealed record SimStop(
        string Wagencode,
        string TripDate,
        string TripId,
        int StopSeq,
        double Lat,
        double Lon,
        string Address);

    private sealed record RawRoadEdge(
        double Lat1,
        double Lon1,
        double Lat2,
        double Lon2,
        int UniqueWagens,
        int Passes);

    private sealed record PreparedRoadEdge(
        int Index,
        double Lat1,
        double Lon1,
        double Lat2,
        double Lon2,
        int UniqueWagens,
        int Passes,
        double Bearing,
        int DirectionBucket,
        string StartKey,
        string EndKey,
        double LengthKm);
}

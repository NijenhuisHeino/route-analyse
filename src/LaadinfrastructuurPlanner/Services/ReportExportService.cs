using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DuckDB.NET.Data;
using LaadinfrastructuurPlanner.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    private const string ReportExportContentType = "application/zip";

    public async Task<ReportExportResult> ExportReportAsync(
        ReportExportRequest request,
        CancellationToken cancellationToken = default)
    {
        await _store.EnsureReadyAsync(cancellationToken);

        var normalized = NormalizeReportExportRequest(request);
        if (normalized.StopSelections.Length == 0 && normalized.RoadSelections.Length == 0)
        {
            return new ReportExportResult(
                "validation_error",
                "Selecteer minimaal één stoplocatie of wegvlak.",
                "",
                ReportExportContentType,
                []);
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var summary = await GetSummaryAsync(normalized, cancellationToken);
        var dashboard = await GetDashboardAsync(normalized, cancellationToken);
        var selections = await BuildReportSelectionsAsync(normalized, cancellationToken);

        var files = new List<string>();
        await using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipBytes(archive, "rapport.pdf", BuildReportPdf(generatedAt, normalized, summary, selections));
            files.Add("rapport.pdf");

            AddZipText(archive, "summary.csv", ToSummaryCsv(summary));
            files.Add("summary.csv");

            AddZipText(archive, "top_stoplocaties.csv", ToDashboardStopsCsv(dashboard.TopStops));
            files.Add("top_stoplocaties.csv");

            AddZipText(archive, "top_wegvlakken.csv", ToDashboardRoadsCsv(dashboard.TopRoadSegments));
            files.Add("top_wegvlakken.csv");

            for (var index = 0; index < selections.Length; index++)
            {
                var selection = selections[index];
                var folder = $"selecties/{index + 1:00}-{Slug(selection.Detail.Title)}/";
                AddSelectionCsvs(archive, folder, selection);
                files.AddRange(SelectionFileNames(folder));
            }

            var manifest = new
            {
                generated_at_utc = generatedAt.ToString("o", CultureInfo.InvariantCulture),
                software_version = typeof(RouteAnalysisService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                filters = new
                {
                    normalized.DateFrom,
                    normalized.DateTo,
                    normalized.VervoerderTypes,
                    normalized.Vervoerders,
                    normalized.Wagencodes,
                    normalized.MinDwellMin,
                    normalized.RoadThreshold,
                    normalized.RoadTopPercent,
                    normalized.MarkerTopN,
                    normalized.ZeZoneMode,
                },
                scenario = normalized.Scenario,
                selected_stops = normalized.StopSelections,
                selected_roads = normalized.RoadSelections,
                files,
            };
            AddZipText(archive, "manifest.json", JsonSerializer.Serialize(manifest, CacheJsonOptions));
        }

        return new ReportExportResult(
            "ok",
            null,
            $"route-analyse-export-{generatedAt:yyyyMMdd-HHmm}.zip",
            ReportExportContentType,
            buffer.ToArray());
    }

    private ReportExportRequest NormalizeReportExportRequest(ReportExportRequest request)
    {
        var normalized = NormalizeFilter(request);
        return request with
        {
            DateFrom = normalized.DateFrom,
            DateTo = normalized.DateTo,
            VervoerderTypes = normalized.VervoerderTypes,
            Vervoerders = normalized.Vervoerders,
            Wagencodes = normalized.Wagencodes,
            MinDwellMin = normalized.MinDwellMin,
            RoadThreshold = normalized.RoadThreshold,
            RoadTopPercent = normalized.RoadTopPercent,
            MarkerTopN = normalized.MarkerTopN,
            ZeZoneMode = normalized.ZeZoneMode,
            Scenario = NormalizeScenario(request.Scenario),
            StopSelections = request.StopSelections
                .Take(50)
                .Select(x => x with
                {
                    Lat = Math.Clamp(x.Lat, -90, 90),
                    Lon = Math.Clamp(x.Lon, -180, 180),
                    Label = (x.Label ?? "").Trim(),
                    RadiusKm = Math.Clamp(x.RadiusKm, 0.1, 5),
                })
                .ToArray(),
            RoadSelections = request.RoadSelections
                .Take(50)
                .Select(x => x with
                {
                    Lat1 = Math.Clamp(x.Lat1, -90, 90),
                    Lon1 = Math.Clamp(x.Lon1, -180, 180),
                    Lat2 = Math.Clamp(x.Lat2, -90, 90),
                    Lon2 = Math.Clamp(x.Lon2, -180, 180),
                    RadiusKm = Math.Clamp(x.RadiusKm, 0.2, 20),
                    Label = x.Label?.Trim(),
                })
                .ToArray(),
        };
    }

    private async Task<ReportSelection[]> BuildReportSelectionsAsync(ReportExportRequest request, CancellationToken cancellationToken)
    {
        var selections = new List<ReportSelection>();
        foreach (var stop in request.StopSelections)
        {
            var detailRequest = new StopLocationDetailRequest
            {
                DateFrom = request.DateFrom,
                DateTo = request.DateTo,
                VervoerderTypes = request.VervoerderTypes,
                Vervoerders = request.Vervoerders,
                Wagencodes = request.Wagencodes,
                MinDwellMin = request.MinDwellMin,
                RoadThreshold = request.RoadThreshold,
                RoadTopPercent = request.RoadTopPercent,
                MarkerTopN = request.MarkerTopN,
                ZeZoneMode = request.ZeZoneMode,
                Lat = stop.Lat,
                Lon = stop.Lon,
                Label = stop.Label,
                RadiusKm = stop.RadiusKm,
                Scenario = request.Scenario,
            };
            var detail = await GetStopLocationDetailAsync(detailRequest, cancellationToken);
            var rows = await QueryStopTripRowsAsync(detailRequest, detail, request.Scenario, cancellationToken);
            selections.Add(new ReportSelection(detail, rows));
        }

        foreach (var road in request.RoadSelections)
        {
            var detailRequest = new RoadSelectionRequest
            {
                DateFrom = request.DateFrom,
                DateTo = request.DateTo,
                VervoerderTypes = request.VervoerderTypes,
                Vervoerders = request.Vervoerders,
                Wagencodes = request.Wagencodes,
                MinDwellMin = request.MinDwellMin,
                RoadThreshold = request.RoadThreshold,
                RoadTopPercent = request.RoadTopPercent,
                MarkerTopN = request.MarkerTopN,
                ZeZoneMode = request.ZeZoneMode,
                Road = new RoadSelection(road.Lat1, road.Lon1, road.Lat2, road.Lon2, road.RadiusKm),
                Scenario = request.Scenario,
            };
            var detail = await GetRoadSelectionAsync(detailRequest, cancellationToken);
            if (!string.IsNullOrWhiteSpace(road.Label))
            {
                detail = detail with { Title = road.Label };
            }

            var rows = await QueryRoadTripRowsAsync(detailRequest, detail, request.Scenario, cancellationToken);
            selections.Add(new ReportSelection(detail, rows));
        }

        return selections.ToArray();
    }

    private async Task<SelectionTripRow[]> QueryStopTripRowsAsync(
        StopLocationDetailRequest request,
        SelectionDetailResponse detail,
        ChargingScenario scenario,
        CancellationToken cancellationToken)
    {
        if (!_store.HasStops || !_store.HasView("overnight_events"))
        {
            return [];
        }

        var normalized = NormalizeStopLocationDetailRequest(request);
        using var connection = OpenConnection();
        var where = BuildStopLocationWhere(normalized);
        var rows = await QueryListAsync(
            connection,
            $$"""
            SELECT
                CAST(wagencode AS VARCHAR) AS wagencode,
                COALESCE(CAST(kentekens AS VARCHAR), CAST(kenteken AS VARCHAR), '') AS kentekens,
                CAST(trip_date AS DATE) AS trip_date,
                CAST(trip_date AS VARCHAR) AS selection_id,
                CAST(prev_end_time AS TIMESTAMP) AS start_time,
                CAST(day_start AS TIMESTAMP) AS end_time,
                CAST(start_lat AS DOUBLE) AS lat,
                CAST(start_lon AS DOUBLE) AS lon,
                COALESCE(CAST(start_address AS VARCHAR), '') AS address,
                COALESCE(CAST(day_km AS DOUBLE), 0) AS distance_km,
                COALESCE(CAST(gap_hours AS DOUBLE), 0) AS gap_hours
            FROM overnight_events
            WHERE {{where}}
            ORDER BY day_km DESC
            LIMIT 50000;
            """,
            ReadDemandEvent,
            cancellationToken);

        return rows.Select(row => ToTripRow("stop", detail, row, scenario, isRoadSelection: false)).ToArray();
    }

    private async Task<SelectionTripRow[]> QueryRoadTripRowsAsync(
        RoadSelectionRequest request,
        SelectionDetailResponse detail,
        ChargingScenario scenario,
        CancellationToken cancellationToken)
    {
        if (!_store.HasStops || !_store.HasView("road_selection_index"))
        {
            return [];
        }

        var normalized = NormalizeRoadSelectionRequest(request);
        using var connection = OpenConnection();
        var where = BuildRoadSelectionWhere(normalized);
        var rows = await QueryListAsync(
            connection,
            $$"""
            SELECT DISTINCT
                CAST(wagencode AS VARCHAR) AS wagencode,
                COALESCE(CAST(kentekens AS VARCHAR), CAST(kenteken AS VARCHAR), '') AS kentekens,
                CAST(trip_date AS DATE) AS trip_date,
                CAST(trip_id AS VARCHAR) AS selection_id,
                CAST(trip_start AS TIMESTAMP) AS start_time,
                CAST(trip_end AS TIMESTAMP) AS end_time,
                CAST(start_lat AS DOUBLE) AS lat,
                CAST(start_lon AS DOUBLE) AS lon,
                COALESCE(CAST(start_address AS VARCHAR), '') AS address,
                COALESCE(CAST(distance_km AS DOUBLE), 0) AS distance_km,
                0.0 AS gap_hours
            FROM road_selection_index
            WHERE {{where}}
            ORDER BY distance_km DESC
            LIMIT 50000;
            """,
            ReadDemandEvent,
            cancellationToken);

        return rows.Select(row => ToTripRow("road", detail, row, scenario, isRoadSelection: true)).ToArray();
    }

    private static SelectionTripRow ToTripRow(
        string selectionType,
        SelectionDetailResponse detail,
        DemandEvent row,
        ChargingScenario scenario,
        bool isRoadSelection)
    {
        return new SelectionTripRow(
            selectionType,
            detail.SelectionId,
            detail.Title,
            row.Wagencode,
            row.Kentekens,
            row.TripDate,
            row.SelectionId,
            row.StartTime,
            row.EndTime,
            Math.Round(row.Lat, 6),
            Math.Round(row.Lon, 6),
            row.Address,
            Math.Round(row.DistanceKm, 1),
            Math.Round(row.GapHours, 2),
            Math.Round(Math.Max(0, row.DistanceKm * scenario.KwhPerKm), 1),
            Math.Round(RequiredKwForEvent(row, scenario, isRoadSelection), 1));
    }

    private static byte[] BuildReportPdf(
        DateTimeOffset generatedAt,
        ReportExportRequest request,
        SummaryResponse summary,
        IReadOnlyList<ReportSelection> selections)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken4));

                page.Header()
                    .Column(column =>
                    {
                        column.Item().Text("Route-analyse export").SemiBold().FontSize(20).FontColor(Colors.Blue.Darken4);
                        column.Item().Text($"Gegenereerd: {generatedAt:yyyy-MM-dd HH:mm} UTC").FontColor(Colors.Grey.Darken1);
                    });

                page.Content()
                    .PaddingTop(16)
                    .Column(column =>
                    {
                        column.Spacing(12);
                        column.Item().Text("Filters en scenario").SemiBold().FontSize(13);
                        column.Item().Text(FilterSummary(request));

                        column.Item().Text("Algemene KPI's").SemiBold().FontSize(13);
                        column.Item().Text(
                            $"Stopmomenten: {summary.Stops:N0} | Dagritten: {summary.Trips:N0} | Voertuigen: {summary.Wagens:N0} | Totale km: {summary.TotalKm:N1} | Stopduur: {summary.TotalDwellHours:N1} uur");

                        column.Item().Text("Geselecteerde locaties").SemiBold().FontSize(13);
                        foreach (var selection in selections)
                        {
                            column.Item().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingBottom(8).Column(section =>
                            {
                                section.Spacing(4);
                                section.Item().Text($"{SelectionLabel(selection.Detail.SelectionType)} - {selection.Detail.Title}")
                                    .SemiBold()
                                    .FontSize(11)
                                    .FontColor(Colors.Blue.Darken3);
                                section.Item().Text(
                                    $"Ritten: {selection.Detail.Summary.Trips:N0} | Voertuigen: {selection.Detail.Summary.UniqueVehicles:N0} | Km: {selection.Detail.Summary.TotalKm:N1} | MWh: {selection.Detail.Charging.TotalMwh:N1} | Piek: {selection.Detail.Charging.PeakMw:N2} MW");
                                section.Item().Text($"Advies: {selection.Detail.Charging.Recommendation}");
                                section.Item().Text($"Top laadvensters: {TopWindowsText(selection.Detail.Charging.BusyWindows)}");
                                section.Item().Text($"Afstanden: P50 {selection.Detail.Distribution.P50Km:N1} km | P75 {selection.Detail.Distribution.P75Km:N1} km | P95 {selection.Detail.Distribution.P95Km:N1} km");
                            });
                        }
                    });

                page.Footer()
                    .AlignCenter()
                    .Text(text =>
                    {
                        text.Span("Pagina ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
            });
        });

        return document.GeneratePdf();
    }

    private static string FilterSummary(ReportExportRequest request)
    {
        static string Values(IEnumerable<string> values) => string.Join(", ", values.DefaultIfEmpty("alle"));
        var period = $"{request.DateFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "start"} t/m {request.DateTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "eind"}";
        return $"Periode: {period} | Vervoersvorm: {Values(request.VervoerderTypes)} | Vervoerders: {Values(request.Vervoerders)} | Voertuigen: {Values(request.Wagencodes)} | Min. stopduur: {request.MinDwellMin:N0} min | ZE-zone: {request.ZeZoneMode} | Scenario: {request.Scenario.KwhPerKm:N1} kWh/km, {request.Scenario.CapacityKwh:N0} kWh, {request.Scenario.KwPerPlug:N0} kW/stekker, {request.Scenario.Plugs:N0} stekkers";
    }

    private static string TopWindowsText(IReadOnlyList<ChargingWindow> windows)
    {
        return windows.Count == 0
            ? "geen laadvensters"
            : string.Join("; ", windows.Take(4).Select(x => $"{x.TimeSlot}: {x.RequiredMw:N2} MW, {x.ShortageKwh:N0} kWh tekort"));
    }

    private static string SelectionLabel(string selectionType)
    {
        return selectionType switch
        {
            "stop" => "Stoplocatie",
            "road" => "Wegvlak",
            _ => "Selectie"
        };
    }

    private static void AddSelectionCsvs(ZipArchive archive, string folder, ReportSelection selection)
    {
        AddZipText(archive, folder + "summary.csv", ToSelectionSummaryCsv(selection.Detail));
        AddZipText(archive, folder + "charging_windows.csv", ToChargingWindowsCsv(selection.Detail.Charging.BusyWindows));
        AddZipText(archive, folder + "hourly_profile.csv", ToHourlyDemandCsv(selection.Detail.Charging.HourlyProfile));
        AddZipText(archive, folder + "weekly_profile.csv", ToWeeklyDemandCsv(selection.Detail.Charging.WeeklyProfile));
        AddZipText(archive, folder + "distance_buckets.csv", ToDistanceBucketsCsv(selection.Detail.Distribution.Buckets));
        AddZipText(archive, folder + "vehicles.csv", ToVehiclesCsv(selection.Detail.Vehicles));
        AddZipText(archive, folder + "heatpoints.csv", ToHeatpointsCsv(selection.Detail.HeatPoints));
        AddZipText(archive, folder + "trip_rows.csv", ToTripRowsCsv(selection.TripRows));
    }

    private static IEnumerable<string> SelectionFileNames(string folder)
    {
        yield return folder + "summary.csv";
        yield return folder + "charging_windows.csv";
        yield return folder + "hourly_profile.csv";
        yield return folder + "weekly_profile.csv";
        yield return folder + "distance_buckets.csv";
        yield return folder + "vehicles.csv";
        yield return folder + "heatpoints.csv";
        yield return folder + "trip_rows.csv";
    }

    private static void AddZipText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void AddZipBytes(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string ToSummaryCsv(SummaryResponse summary)
    {
        var builder = new StringBuilder("stops,trips,vehicles,total_km,avg_trip_km,total_dwell_hours,eigen_km_pct,charter_km_pct\n");
        builder.AppendCsv(summary.Stops.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.Trips.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.Wagens.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.TotalKm.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.AvgTripKm.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.TotalDwellHours.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.EigenKmPct.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(summary.CharterKmPct.ToString(CultureInfo.InvariantCulture), endLine: true);
        return builder.ToString();
    }

    private static string ToDashboardStopsCsv(IEnumerable<DashboardStop> stops)
    {
        var builder = new StringBuilder("lat,lon,name,address,stops,vehicles,trips,total_dwell_hours,avg_dwell_min\n");
        foreach (var row in stops)
        {
            builder.AppendCsv(row.Lat.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lon.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Name)
                .AppendCsv(row.Address)
                .AppendCsv(row.Stops.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Wagens.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Trips.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.TotalDwellHours.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AvgDwellMin.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToDashboardRoadsCsv(IEnumerable<RoadLine> roads)
    {
        var builder = new StringBuilder("segment_id,lat1,lon1,lat2,lon2,direction,bearing,length_km,vehicles,passes,raw_segments,radius_km\n");
        foreach (var row in roads)
        {
            builder.AppendCsv(row.SegmentId)
                .AppendCsv(row.Lat1.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lon1.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lat2.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lon2.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Direction)
                .AppendCsv(row.Bearing.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.LengthKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.UniqueWagens.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Passes.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RawSegments.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.SelectionRadiusKm.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToSelectionSummaryCsv(SelectionDetailResponse detail)
    {
        var builder = new StringBuilder("selection_type,selection_id,title,lat,lon,trips,vehicles,total_km,total_mwh,shortage_mwh,peak_mw,recommendation\n");
        builder.AppendCsv(detail.SelectionType)
            .AppendCsv(detail.SelectionId ?? "")
            .AppendCsv(detail.Title)
            .AppendCsv(detail.Lat.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Lon.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Summary.Trips.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Summary.UniqueVehicles.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Summary.TotalKm.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Charging.TotalMwh.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Charging.ShortageMwh.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Charging.PeakMw.ToString(CultureInfo.InvariantCulture))
            .AppendCsv(detail.Charging.Recommendation, endLine: true);
        return builder.ToString();
    }

    private static string ToChargingWindowsCsv(IEnumerable<ChargingWindow> windows)
    {
        var builder = new StringBuilder("time_slot,events,demand_kwh,deliverable_kwh,shortage_kwh,required_mw\n");
        foreach (var row in windows)
        {
            builder.AppendCsv(row.TimeSlot)
                .AppendCsv(row.Events.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DemandKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DeliverableKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ShortageKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredMw.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToHourlyDemandCsv(IEnumerable<HourlyDemandCell> cells)
    {
        var builder = new StringBuilder("hour,label,vehicles,events,demand_kwh,required_kw,required_mw\n");
        foreach (var row in cells)
        {
            builder.AppendCsv(row.Hour.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Label)
                .AppendCsv(row.Vehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Events.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DemandKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredKw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredMw.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToWeeklyDemandCsv(IEnumerable<WeeklyDemandCell> cells)
    {
        var builder = new StringBuilder("day_index,day_label,hour,label,vehicles,events,demand_kwh,required_kw,required_mw,kentekens,wagencodes\n");
        foreach (var row in cells)
        {
            builder.AppendCsv(row.DayIndex.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DayLabel)
                .AppendCsv(row.Hour.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Label)
                .AppendCsv(row.Vehicles.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Events.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DemandKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredKw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredMw.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(string.Join("; ", row.Kentekens))
                .AppendCsv(string.Join("; ", row.Wagencodes), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToDistanceBucketsCsv(IEnumerable<DistanceBucket> buckets)
    {
        var builder = new StringBuilder("from_km,to_km,trips\n");
        foreach (var row in buckets)
        {
            builder.AppendCsv(row.FromKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ToKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Trips.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToVehiclesCsv(IEnumerable<SelectionVehicleRow> vehicles)
    {
        var builder = new StringBuilder("wagencode,kentekens,days,total_km,avg_day_km,p95_km,avg_kwh_per_day,total_mwh,avg_standing_hours,required_kw\n");
        foreach (var row in vehicles)
        {
            builder.AppendCsv(row.Wagencode)
                .AppendCsv(row.Kentekens)
                .AppendCsv(row.Days.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.TotalKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AvgDayKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.P95Km.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AvgKwhPerDay.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.TotalMwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AvgStandingHours.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredKw.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToHeatpointsCsv(IEnumerable<HeatPoint> heatPoints)
    {
        var builder = new StringBuilder("lat,lon,weight\n");
        foreach (var row in heatPoints)
        {
            builder.AppendCsv(row.Lat.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lon.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Weight.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string ToTripRowsCsv(IEnumerable<SelectionTripRow> rows)
    {
        var builder = new StringBuilder("selection_type,selection_id,title,wagencode,kentekens,trip_date,trip_id,start_time,end_time,lat,lon,address,distance_km,standing_hours,demand_kwh,required_kw\n");
        foreach (var row in rows)
        {
            builder.AppendCsv(row.SelectionType)
                .AppendCsv(row.SelectionId ?? "")
                .AppendCsv(row.Title)
                .AppendCsv(row.Wagencode)
                .AppendCsv(row.Kentekens)
                .AppendCsv(row.TripDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .AppendCsv(row.TripId)
                .AppendCsv(row.StartTime.ToString("o", CultureInfo.InvariantCulture))
                .AppendCsv(row.EndTime.ToString("o", CultureInfo.InvariantCulture))
                .AppendCsv(row.Lat.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Lon.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Address)
                .AppendCsv(row.DistanceKm.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.StandingHours.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DemandKwh.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RequiredKw.ToString(CultureInfo.InvariantCulture), endLine: true);
        }

        return builder.ToString();
    }

    private static string Slug(string value)
    {
        var clean = new string(value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        clean = string.Join('-', clean.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(clean) ? "selectie" : clean[..Math.Min(clean.Length, 50)];
    }

    private sealed record ReportSelection(SelectionDetailResponse Detail, SelectionTripRow[] TripRows);
}

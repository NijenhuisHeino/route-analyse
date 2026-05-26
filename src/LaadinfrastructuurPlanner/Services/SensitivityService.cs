using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    public async Task<SensitivityResponse> GetSensitivityAsync(
        PowerLocationProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var baseDetail = await GetPowerLocationProfileAsync(request, cancellationToken);
        if (baseDetail.Profile is null)
        {
            return new SensitivityResponse(
                "missing",
                "Geen profiel beschikbaar voor sensitivity-analyse.",
                request.LocationId,
                "",
                [],
                0, 0, 0,
                0, 0, 0);
        }

        var name = string.IsNullOrWhiteSpace(baseDetail.Profile.Name) ? baseDetail.Profile.Address : baseDetail.Profile.Name;
        var energyVariants = new[] { 0.85, 1.00, 1.20, 1.40, 1.60 };
        var socVariants = new (double Min, double Target)[]
        {
            (10, 85),
            (15, 80),
            (20, 75),
            (20, 80),
        };
        var fleetVariants = new[]
        {
            ("low", 0.6),
            ("base", 1.0),
            ("high", 1.3),
        };

        var cells = new List<SensitivityCell>();
        var basePeakKw = baseDetail.Profile.PeakKw;
        var baseShortageMwh = baseDetail.Scenarios.Sum(s => s.HourlyProfile.Sum(c => c.RequiredKw)) / 1000.0;

        foreach (var fleet in fleetVariants)
        foreach (var energy in energyVariants)
        foreach (var soc in socVariants)
        {
            var energyFactor = energy / Math.Max(0.01, request.CapacityKwh > 0 ? 1.2 : 1.2);
            var capacityFactor = (soc.Target - soc.Min) / Math.Max(1.0, 80.0 - 15.0);
            var peakMw = Math.Round(basePeakKw * energyFactor * fleet.Item2 / 1000.0, 2);
            var totalMwh = Math.Round(baseShortageMwh * energyFactor * fleet.Item2, 1);
            var shortageMwh = Math.Round(Math.Max(0, baseShortageMwh * energyFactor * fleet.Item2 * (1 - capacityFactor)), 1);
            cells.Add(new SensitivityCell(
                ScenarioName: $"{fleet.Item1}|kWh/km={energy}|SoC={soc.Min}-{soc.Target}",
                KwhPerKm: energy,
                MinSocPct: soc.Min,
                TargetSocPct: soc.Target,
                FleetScale: fleet.Item2,
                PeakMw: peakMw,
                TotalMwh: totalMwh,
                ShortageMwh: shortageMwh));
        }

        var peaks = cells.Select(c => c.PeakMw).OrderBy(v => v).ToArray();
        var shortages = cells.Select(c => c.ShortageMwh).OrderBy(v => v).ToArray();

        return new SensitivityResponse(
            "ok",
            null,
            request.LocationId,
            name,
            cells.ToArray(),
            Percentile(peaks, 0.10),
            Percentile(peaks, 0.50),
            Percentile(peaks, 0.90),
            Percentile(shortages, 0.10),
            Percentile(shortages, 0.50),
            Percentile(shortages, 0.90));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var position = (sorted.Count - 1) * q;
        var lo = (int)Math.Floor(position);
        var hi = (int)Math.Ceiling(position);
        if (lo == hi) return sorted[lo];
        var weight = position - lo;
        return Math.Round(sorted[lo] * (1 - weight) + sorted[hi] * weight, 2);
    }
}

using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Services;

public sealed partial class RouteAnalysisService
{
    internal double ResolveKwhPerKm(string? vehicleClass, DateTime? date, double fallback)
    {
        var assumptions = _store.Options.VehicleEnergyAssumptions;
        if (assumptions is null || assumptions.Length == 0)
        {
            return fallback;
        }

        var season = ResolveSeason(date);
        var cls = NormalizeVehicleClassForEnergy(vehicleClass);
        var match = assumptions.FirstOrDefault(a =>
                string.Equals(a.VehicleClass, cls, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Season, season, StringComparison.OrdinalIgnoreCase))
            ?? assumptions.FirstOrDefault(a =>
                string.Equals(a.VehicleClass, "unknown", StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Season, season, StringComparison.OrdinalIgnoreCase));
        return match?.KwhPerKm ?? fallback;
    }

    private static string ResolveSeason(DateTime? date)
    {
        if (date is null || date == DateTime.MinValue)
        {
            return "winter";
        }
        var m = date.Value.Month;
        return (m >= 11 || m <= 3) ? "winter" : "summer";
    }

    private static string NormalizeVehicleClassForEnergy(string? vehicleClass)
    {
        if (string.IsNullOrWhiteSpace(vehicleClass)) return "unknown";
        var v = vehicleClass.Trim().ToLowerInvariant();
        if (v.Contains("trekker") || v.Contains("tractor") || v.Contains("oplegger")) return "trekker";
        if (v.Contains("bakwagen") || v.Contains("box") || v.Contains("rigid")) return "bakwagen";
        return v;
    }
}

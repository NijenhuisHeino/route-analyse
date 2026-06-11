using LaadinfrastructuurPlanner.Models;

namespace LaadinfrastructuurPlanner.Components;

/// <summary>Gedeelde format- en label-helpers voor de planner-UI.</summary>
public static class UiFormat
{
    public static string FormatNumber(double? value) => value is null ? "-" : value.Value.ToString("N0");

    public static string FormatNumber(long? value) => value is null ? "-" : value.Value.ToString("N0");

    public static string FormatPower(double kw)
    {
        if (kw <= 0)
        {
            return "0 kW";
        }

        if (kw < 1000)
        {
            return $"{kw:N0} kW";
        }

        var mw = kw / 1000.0;
        return mw >= 100 ? $"{mw:N0} MW" : $"{mw:N1} MW";
    }

    public static string FormatStandingDuration(double hours)
    {
        if (hours <= 0)
        {
            return "0 min";
        }

        var minutes = hours * 60.0;
        if (minutes < 10)
        {
            return $"{minutes:N1} min";
        }

        if (minutes < 120)
        {
            return $"{minutes:N0} min";
        }

        return $"{hours:N1} uur";
    }

    public static string DisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value;
    }

    public static string JoinValues(IReadOnlyCollection<string> values, string empty)
    {
        if (values.Count == 0)
        {
            return empty;
        }

        const int maxVisible = 12;
        var visible = values.Take(maxVisible).ToArray();
        return values.Count <= maxVisible
            ? string.Join(", ", visible)
            : string.Join(", ", visible) + $" +{values.Count - maxVisible}";
    }

    public static string ShortDayLabel(string dayLabel)
    {
        return dayLabel switch
        {
            "Maandag" => "Ma",
            "Dinsdag" => "Di",
            "Woensdag" => "Wo",
            "Donderdag" => "Do",
            "Vrijdag" => "Vr",
            "Zaterdag" => "Za",
            "Zondag" => "Zo",
            _ => dayLabel
        };
    }

    public static string SelectionTypeLabel(string selectionType)
    {
        return selectionType switch
        {
            "road" => "Wegvlakselectie",
            "stop" => "Vertrekkende dagritten vanaf stoplocatie",
            _ => "Vaste stilstandlocatie"
        };
    }

    public static string RoadStatusLabel(string? status)
    {
        return status switch
        {
            "ok" => "Beschikbaar",
            "skipped" => "Verborgen",
            _ => "Niet beschikbaar"
        };
    }

    public static string RoadDisplayName(RoadLine row)
    {
        return string.IsNullOrWhiteSpace(row.RoadName)
            ? $"Wegvlak bij {((row.Lat1 + row.Lat2) / 2):0.000}, {((row.Lon1 + row.Lon2) / 2):0.000}"
            : row.RoadName;
    }

    public static string RoadCoordinateLabel(RoadLine row)
    {
        return $"{((row.Lat1 + row.Lat2) / 2):0.000}, {((row.Lon1 + row.Lon2) / 2):0.000}";
    }

    public static string HistogramWidth(long trips, IReadOnlyCollection<DistanceBucket> buckets)
    {
        var max = buckets.Count == 0 ? 0 : buckets.Max(x => x.Trips);
        var width = max <= 0 ? 0 : Math.Max(4, Math.Round(100.0 * trips / max, 1));
        return width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    public static string DistanceBarStyle(long trips, IReadOnlyCollection<DistanceBucket> buckets)
    {
        var max = buckets.Count == 0 ? 0 : buckets.Max(x => x.Trips);
        var height = max <= 0 || trips <= 0 ? 2 : Math.Max(6, Math.Round(100.0 * trips / max, 1));
        return $"--bar-height:{height.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%";
    }

    public static string DistanceBucketClass(DistanceBucket bucket)
    {
        return bucket.Trips > 0 ? "distance-bar has-data" : "distance-bar";
    }

    public static string DistanceBucketLabel(DistanceBucket bucket)
    {
        return $"{bucket.FromKm:N0}-{bucket.ToKm:N0}";
    }

    public static string DistanceBucketShortLabel(DistanceBucket bucket)
    {
        return bucket.FromKm % 100 == 0 || bucket.FromKm == 700
            ? bucket.FromKm.ToString("N0")
            : "";
    }
}

/// <summary>Popup met voertuigen en hun vermogensvraag voor een geselecteerd uurblok.</summary>
public sealed record VehicleDemandPopup(
    string Title,
    string Subtitle,
    VehicleDemandPopupRow[] Rows);

public sealed record VehicleDemandPopupRow(
    string Wagencode,
    string Kenteken,
    string VehicleClass,
    double DemandKwh,
    double RequiredKw,
    double StandingHours,
    string Window);

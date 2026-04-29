"""Verbruiks- en laadsimulatie voor eTrucks op PostNL-rittendata.

Per trip wordt SoC-traject berekend op basis van haversine-afstand tussen
consecutieve stops × kWh/km. Op punten waar SoC < drempel komt, wordt een
laad-event geplaatst (charge to 100%). Aggregeren over alle trips geeft
hotspots waar laadinfra het meest nodig is.

Versie 1: haversine-afstand. Latere versie: OSRM-polyline-km (nauwkeuriger).
"""

from __future__ import annotations

import math

import pandas as pd


def _haversine_km(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    r = 6371.0
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlam = math.radians(lon2 - lon1)
    a = (
        math.sin(dphi / 2) ** 2
        + math.cos(phi1) * math.cos(phi2) * math.sin(dlam / 2) ** 2
    )
    return 2 * r * math.asin(math.sqrt(a))


def simulate_soc(
    stops: pd.DataFrame,
    kwh_per_km: float = 1.2,
    capacity_kwh: float = 590,
    start_soc_pct: float = 100,
    threshold_pct: float = 15,
    max_charge_kw: float = 350,
) -> pd.DataFrame:
    """Reken per trip de SoC uit en markeer waar geladen moet worden.

    Returns kopie van `stops` met extra kolommen:
    - `segment_km`: afstand van vorige stop in deze trip (haversine, 0 voor eerste)
    - `soc_kwh_aankomst`: SoC bij aankomst op deze stop
    - `soc_pct_aankomst`: idem als percentage
    - `charge_event`: True als hier moet worden geladen (SoC < drempel)
    - `charge_kwh`: hoeveelheid bijgeladen op deze stop
    - `charge_min`: laadtijd in minuten bij `max_charge_kw`

    Aanname laden: bij charge_event → laden tot 100% capaciteit.
    """
    if stops.empty:
        return stops.copy()

    threshold_kwh = capacity_kwh * (threshold_pct / 100.0)
    start_kwh = capacity_kwh * (start_soc_pct / 100.0)

    df = stops.sort_values(
        ["wagencode", "trip_date", "trip_id", "stop_seq"], kind="stable"
    ).reset_index(drop=True).copy()

    segment_km = [0.0] * len(df)
    soc_kwh = [0.0] * len(df)
    charge_event = [False] * len(df)
    charge_kwh = [0.0] * len(df)

    prev_trip: tuple | None = None
    soc = start_kwh
    prev_lat = prev_lon = None

    for i, row in enumerate(df.itertuples(index=False)):
        trip_key = (row.wagencode, row.trip_date, row.trip_id)
        if trip_key != prev_trip:
            soc = start_kwh
            prev_lat = prev_lon = None
            prev_trip = trip_key

        if prev_lat is not None:
            km = _haversine_km(prev_lat, prev_lon, row.lat, row.lon)
            segment_km[i] = km
            soc -= km * kwh_per_km

        if soc < threshold_kwh:
            charge_event[i] = True
            charge_kwh[i] = capacity_kwh - max(soc, 0)
            soc = capacity_kwh

        soc_kwh[i] = soc
        prev_lat, prev_lon = row.lat, row.lon

    df["segment_km"] = segment_km
    df["soc_kwh_aankomst"] = soc_kwh
    df["soc_pct_aankomst"] = (df["soc_kwh_aankomst"] / capacity_kwh * 100).round(1)
    df["charge_event"] = charge_event
    df["charge_kwh"] = charge_kwh
    df["charge_min"] = (df["charge_kwh"] / max(max_charge_kw, 1) * 60).round(1)
    return df


def charge_hotspots(sim_df: pd.DataFrame, top_n: int = 50) -> pd.DataFrame:
    """Aggregeer laad-events naar locaties: waar moeten trucks het vaakst laden?"""
    events = sim_df[sim_df["charge_event"]]
    if events.empty:
        return pd.DataFrame(
            columns=[
                "lat",
                "lon",
                "adres",
                "n_events",
                "n_unieke_wagens",
                "totaal_kwh",
                "gem_min",
            ]
        )

    grouped = events.groupby(["lat", "lon", "adres"]).agg(
        n_events=("charge_event", "size"),
        n_unieke_wagens=("wagencode", "nunique"),
        totaal_kwh=("charge_kwh", "sum"),
        gem_min=("charge_min", "mean"),
    ).reset_index()
    grouped["totaal_kwh"] = grouped["totaal_kwh"].round(0)
    grouped["gem_min"] = grouped["gem_min"].round(1)
    return grouped.sort_values("n_events", ascending=False).head(top_n).reset_index(
        drop=True
    )

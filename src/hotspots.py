"""Aggregate stops into charging hotspot candidates."""

from __future__ import annotations

import pandas as pd


def rank_hotspots(df: pd.DataFrame, round_decimals: int = 3) -> pd.DataFrame:
    """Group stops by rounded lat/lon and rank by unique-truck reach.

    Round_decimals ~3 = ~110 m grid; ~2 = ~1.1 km grid. For finding charging
    plazas that multiple routes can share, 3 (neighborhood level) is a good
    default — operators can still walk the same address cluster.
    """
    if df.empty:
        return pd.DataFrame(
            columns=[
                "lat_round",
                "lon_round",
                "locatie_naam",
                "adres",
                "n_stops",
                "n_wagens",
                "n_trips",
                "totale_standtijd_uur",
                "gem_standtijd_min",
            ]
        )

    work = df.copy()
    work["lat_round"] = work["lat"].round(round_decimals)
    work["lon_round"] = work["lon"].round(round_decimals)

    grouped = (
        work.groupby(["lat_round", "lon_round"])
        .agg(
            locatie_naam=("locatie_naam", lambda s: s.dropna().mode().iat[0] if not s.dropna().empty else ""),
            adres=("adres", lambda s: s.dropna().mode().iat[0] if not s.dropna().empty else ""),
            n_stops=("wagencode", "size"),
            n_wagens=("wagencode", "nunique"),
            n_trips=("trip_id", "nunique"),
            totale_standtijd_min=("dwell_min", "sum"),
            gem_standtijd_min=("dwell_min", "mean"),
        )
        .reset_index()
    )

    grouped["totale_standtijd_uur"] = (grouped["totale_standtijd_min"] / 60).round(1)
    grouped["gem_standtijd_min"] = grouped["gem_standtijd_min"].round(0)

    grouped = grouped.sort_values(
        ["n_wagens", "totale_standtijd_uur"], ascending=[False, False]
    ).reset_index(drop=True)

    return grouped[
        [
            "lat_round",
            "lon_round",
            "locatie_naam",
            "adres",
            "n_stops",
            "n_wagens",
            "n_trips",
            "totale_standtijd_uur",
            "gem_standtijd_min",
        ]
    ]

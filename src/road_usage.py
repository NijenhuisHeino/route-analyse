"""Aggregate OSRM-routed trip polylines into a weighted road-usage heatmap.

Each trip's polyline is snapped to a lat/lon grid; per grid cell we count the
number of unique wagens passing through. The result feeds a weighted
folium HeatMap so busy corridors pop out as charging-hotspot candidates.
"""

from __future__ import annotations

import pandas as pd

from src.routing import trip_polyline


def compute_road_heatmap_points(
    stops: pd.DataFrame,
    routes: dict,
    round_decimals: int = 3,
) -> list[tuple[float, float, float]]:
    """Return [(lat, lon, n_unique_wagens)] aggregated across all trips.

    round_decimals=3 ≈ 110 m grid (good for regional overview).
    round_decimals=4 ≈ 11 m grid (finer, heavier to render).
    """
    cell_wagens: dict[tuple[float, float], set[str]] = {}

    for (wagen, _date, _trip), g in stops.groupby(
        ["wagencode", "trip_date", "trip_id"]
    ):
        if len(g) < 2:
            continue
        poly = trip_polyline(g, routes)
        seen_cells: set[tuple[float, float]] = set()
        for lat, lon in poly:
            cell = (round(lat, round_decimals), round(lon, round_decimals))
            if cell in seen_cells:
                continue
            seen_cells.add(cell)
            cell_wagens.setdefault(cell, set()).add(str(wagen))

    return [(lat, lon, float(len(wagens))) for (lat, lon), wagens in cell_wagens.items()]


def top_road_hotspots(
    heatmap_points: list[tuple[float, float, float]],
    top_n: int = 25,
) -> pd.DataFrame:
    """Rank grid cells by unique-wagen count, return a DataFrame."""
    df = pd.DataFrame(heatmap_points, columns=["lat", "lon", "n_wagens"])
    df["n_wagens"] = df["n_wagens"].astype(int)
    return df.sort_values("n_wagens", ascending=False).head(top_n).reset_index(drop=True)


def compute_weighted_edges(
    stops: pd.DataFrame,
    routes: dict,
    round_decimals: int = 6,
) -> list[tuple[tuple[float, float], tuple[float, float], int]]:
    """Per consecutief paar punten op alle OSRM trip-polylines: tel unieke wagens.

    Retourneert [(p1, p2, n_unique_wagens), ...]. Richting blijft behouden
    — heen en terug over hetzelfde wegvlak worden apart geteld (twee lijnen).
    Werkt op OSRM `overview=full` output waar alle trips dezelfde road-graph
    vertices delen, zodat er per wegvlak exact één lijn overblijft.
    """
    edge_wagens: dict[tuple, set[str]] = {}

    for (wagen, _date, _trip), g in stops.groupby(
        ["wagencode", "trip_date", "trip_id"]
    ):
        if len(g) < 2:
            continue
        poly = trip_polyline(g, routes)
        for i in range(len(poly) - 1):
            p1 = (round(poly[i][0], round_decimals), round(poly[i][1], round_decimals))
            p2 = (
                round(poly[i + 1][0], round_decimals),
                round(poly[i + 1][1], round_decimals),
            )
            if p1 == p2:
                continue
            key = (p1, p2)
            edge_wagens.setdefault(key, set()).add(str(wagen))

    return [(p1, p2, len(wagens)) for (p1, p2), wagens in edge_wagens.items()]


def lerp_hex(lo: str, hi: str, t: float) -> str:
    """Lineaire interpolatie tussen twee hex-kleuren in RGB-ruimte."""
    t = max(0.0, min(1.0, t))
    lo_rgb = tuple(int(lo[i : i + 2], 16) for i in (1, 3, 5))
    hi_rgb = tuple(int(hi[i : i + 2], 16) for i in (1, 3, 5))
    r, g, b = (int(lo_rgb[i] + (hi_rgb[i] - lo_rgb[i]) * t) for i in range(3))
    return f"#{r:02x}{g:02x}{b:02x}"

"""Aggregate OSRM-routed trip polylines into a weighted road-usage heatmap.

Each trip's polyline is snapped to a lat/lon grid; per grid cell we count the
number of unique wagens passing through. The result feeds a weighted
folium HeatMap so busy corridors pop out as charging-hotspot candidates.
"""

from __future__ import annotations

import math
from collections import defaultdict

import pandas as pd

from src.routing import trip_polyline


def _haversine_km(p1: tuple[float, float], p2: tuple[float, float]) -> float:
    r = 6371.0
    lat1, lon1 = p1
    lat2, lon2 = p2
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlam = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(dlam / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def _segment_aggregates(
    stops: pd.DataFrame, round_decimals: int = 5
) -> dict[tuple, dict]:
    """Vectoriseerde aggregatie: per uniek wegvlak (segment-key) ->
    {'wagens': set[str], 'n_passes': int}.

    n_passes = totaal aantal keer dat dit segment bereden is (kan dezelfde
    wagen meerdere keren tellen). wagens = aantal unieke wagens.
    """
    df = stops.sort_values(
        ["wagencode", "trip_date", "trip_id", "stop_seq"], kind="stable"
    ).reset_index(drop=True)

    grp = df.groupby(["wagencode", "trip_date", "trip_id"], sort=False)
    df["next_lat"] = grp["lat"].shift(-1)
    df["next_lon"] = grp["lon"].shift(-1)
    pairs = df.dropna(subset=["next_lat", "next_lon"])

    pairs = pairs.assign(
        lat1=pairs["lat"].round(round_decimals),
        lon1=pairs["lon"].round(round_decimals),
        lat2=pairs["next_lat"].round(round_decimals),
        lon2=pairs["next_lon"].round(round_decimals),
    )

    seg_data: dict[tuple, dict] = {}
    for (lat1, lon1, lat2, lon2), g in pairs.groupby(
        ["lat1", "lon1", "lat2", "lon2"], sort=False
    ):
        seg_data[(lat1, lon1, lat2, lon2)] = {
            "wagens": set(g["wagencode"].astype(str).unique()),
            "n_passes": int(len(g)),
        }
    return seg_data


def _segment_wagen_sets(
    stops: pd.DataFrame, round_decimals: int = 5
) -> dict[tuple, set[str]]:
    """Backwards-compat: alleen wagen-sets, voor precompute_aggregations chunked-flow."""
    return {k: v["wagens"] for k, v in _segment_aggregates(stops, round_decimals).items()}


def compute_road_heatmap_points(
    stops: pd.DataFrame,
    routes: dict,
    round_decimals: int = 3,
) -> list[tuple[float, float, float]]:
    """Return [(lat, lon, n_unique_wagens)] aggregated across all trips.

    Vectoriseerd: aggregeert eerst per uniek segment (46k), dan polyline-walk.
    round_decimals=3 ≈ 110 m grid.
    """
    seg_data = _segment_aggregates(stops)

    cell_wagens: dict[tuple[float, float], set[str]] = {}
    for seg_key, info in seg_data.items():
        wagens = info["wagens"]
        poly = routes.get(seg_key)
        if not poly:
            poly = [(seg_key[0], seg_key[1]), (seg_key[2], seg_key[3])]
        seen: set[tuple[float, float]] = set()
        for lat, lon in poly:
            cell = (round(lat, round_decimals), round(lon, round_decimals))
            if cell in seen:
                continue
            seen.add(cell)
            cell_wagens.setdefault(cell, set()).update(wagens)

    return [(lat, lon, float(len(w))) for (lat, lon), w in cell_wagens.items()]


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
) -> list[tuple[tuple[float, float], tuple[float, float], int, int]]:
    """Per consecutief paar punten op alle OSRM trip-polylines: tel unieke wagens
    en totaal aantal keer bereden.

    Retourneert [(p1, p2, n_unique_wagens, n_passes), ...]. Richting blijft
    behouden. Vectoriseerd: aggregeert eerst per uniek segment, dan polyline-walk.
    """
    seg_data = _segment_aggregates(stops)

    edge_data: dict[tuple, dict] = {}
    for seg_key, info in seg_data.items():
        wagens = info["wagens"]
        n_passes = info["n_passes"]
        poly = routes.get(seg_key)
        if not poly:
            poly = [(seg_key[0], seg_key[1]), (seg_key[2], seg_key[3])]
        for i in range(len(poly) - 1):
            p1 = (round(poly[i][0], round_decimals), round(poly[i][1], round_decimals))
            p2 = (
                round(poly[i + 1][0], round_decimals),
                round(poly[i + 1][1], round_decimals),
            )
            if p1 == p2:
                continue
            d = edge_data.setdefault((p1, p2), {"wagens": set(), "n_passes": 0})
            d["wagens"].update(wagens)
            d["n_passes"] += n_passes

    return [
        (p1, p2, len(d["wagens"]), d["n_passes"])
        for (p1, p2), d in edge_data.items()
    ]


def merge_identical_chains(
    edges: list[tuple],
) -> list[dict]:
    """Voeg aaneengesloten micro-edges met identieke (n_wagens, n_passes) samen.

    OSRM's `overview=full` polylines splitsen een wegstuk in veel kleine
    vertices (50-200 m). Edges in zo'n splitsing hebben identieke metrics.
    Deze functie clustert ze tot één 'wegvlak' per zelf-consistente keten.

    Returns lijst dicts: {lat1, lon1, lat2, lon2, n_wagens, n_passes,
    n_micro_edges, length_km}, ongesorteerd.
    """
    if not edges:
        return []

    by_point: dict[tuple, list[int]] = defaultdict(list)
    for i, edge in enumerate(edges):
        p1, p2 = edge[0], edge[1]
        by_point[p1].append(i)
        by_point[p2].append(i)

    visited: set[int] = set()
    chains: list[dict] = []

    for start_idx, edge in enumerate(edges):
        if start_idx in visited:
            continue
        n_wagens = edge[2]
        n_passes = edge[3] if len(edge) > 3 else n_wagens
        target = (n_wagens, n_passes)

        chain_idxs = [start_idx]
        chain_points: set[tuple] = {edge[0], edge[1]}
        visited.add(start_idx)
        queue = [start_idx]
        while queue:
            cur = queue.pop()
            cur_edge = edges[cur]
            for endpoint in (cur_edge[0], cur_edge[1]):
                for nei_idx in by_point.get(endpoint, ()):
                    if nei_idx in visited:
                        continue
                    nei = edges[nei_idx]
                    nei_target = (
                        nei[2],
                        nei[3] if len(nei) > 3 else nei[2],
                    )
                    if nei_target != target:
                        continue
                    visited.add(nei_idx)
                    chain_idxs.append(nei_idx)
                    chain_points.add(nei[0])
                    chain_points.add(nei[1])
                    queue.append(nei_idx)

        endpoints = [
            pt
            for pt in chain_points
            if sum(
                1
                for ei in chain_idxs
                if edges[ei][0] == pt or edges[ei][1] == pt
            )
            == 1
        ]
        if len(endpoints) >= 2:
            start_pt = endpoints[0]
            end_pt = endpoints[-1]
        else:
            lats = [pt[0] for pt in chain_points]
            lons = [pt[1] for pt in chain_points]
            start_pt = (min(lats), min(lons))
            end_pt = (max(lats), max(lons))

        length_km = sum(
            _haversine_km(edges[ei][0], edges[ei][1]) for ei in chain_idxs
        )

        chains.append(
            {
                "lat1": start_pt[0],
                "lon1": start_pt[1],
                "lat2": end_pt[0],
                "lon2": end_pt[1],
                "n_wagens": n_wagens,
                "n_passes": n_passes,
                "n_micro_edges": len(chain_idxs),
                "length_km": length_km,
            }
        )

    return chains


def compute_corridors(
    edges: list[tuple[tuple[float, float], tuple[float, float], int]],
    threshold: int,
) -> list[dict]:
    """Groepeer aaneengesloten wegvlakken boven drempel tot corridors.

    Richting wordt genegeerd (heen + terug = zelfde corridor).
    Retourneert lijst dicts met: edges, length_km, max_n, median_n, center,
    gesorteerd op length_km (langst eerst).
    """
    merged: dict[tuple, dict] = {}
    for edge in edges:
        p1, p2 = edge[0], edge[1]
        n_wagens = edge[2]
        n_passes = edge[3] if len(edge) > 3 else n_wagens
        if n_wagens < threshold:
            continue
        key = (p1, p2) if p1 < p2 else (p2, p1)
        if key in merged:
            merged[key]["n_wagens"] = max(merged[key]["n_wagens"], n_wagens)
            merged[key]["n_passes"] = max(merged[key]["n_passes"], n_passes)
        else:
            merged[key] = {"n_wagens": n_wagens, "n_passes": n_passes}

    if not merged:
        return []

    adj: dict[tuple, list[tuple]] = defaultdict(list)
    for (a, b), info in merged.items():
        adj[a].append((b, info["n_wagens"]))
        adj[b].append((a, info["n_wagens"]))

    visited: set = set()
    corridors: list[dict] = []
    for node in list(adj.keys()):
        if node in visited:
            continue
        component: set = set()
        stack = [node]
        while stack:
            nd = stack.pop()
            if nd in component:
                continue
            component.add(nd)
            for nb, _ in adj[nd]:
                if nb not in component:
                    stack.append(nb)
        visited |= component

        comp_edges = [
            (a, b, info["n_wagens"], info["n_passes"])
            for (a, b), info in merged.items()
            if a in component
        ]
        total_km = sum(_haversine_km(a, b) for a, b, _, _ in comp_edges)
        wagens_list = sorted(n for _, _, n, _ in comp_edges)
        passes_list = sorted(p for _, _, _, p in comp_edges)
        max_n = wagens_list[-1]
        median_n = wagens_list[len(wagens_list) // 2]
        median_passes = passes_list[len(passes_list) // 2]
        max_passes = passes_list[-1]

        lats = [p[0] for a, b, _, _ in comp_edges for p in (a, b)]
        lons = [p[1] for a, b, _, _ in comp_edges for p in (a, b)]
        min_lat, max_lat = min(lats), max(lats)
        min_lon, max_lon = min(lons), max(lons)
        center = ((min_lat + max_lat) / 2, (min_lon + max_lon) / 2)
        bbox_diag_km = _haversine_km((min_lat, min_lon), (max_lat, max_lon))

        corridors.append(
            {
                "edges": comp_edges,
                "length_km": total_km,
                "spreiding_km": bbox_diag_km,
                "max_n": max_n,
                "median_n": median_n,
                "max_passes": max_passes,
                "median_passes": median_passes,
                "n_edges": len(comp_edges),
                "center": center,
            }
        )

    corridors.sort(
        key=lambda c: (c["median_passes"], c["max_n"]), reverse=True
    )
    return corridors


def lerp_hex(lo: str, hi: str, t: float) -> str:
    """Lineaire interpolatie tussen twee hex-kleuren in RGB-ruimte."""
    t = max(0.0, min(1.0, t))
    lo_rgb = tuple(int(lo[i : i + 2], 16) for i in (1, 3, 5))
    hi_rgb = tuple(int(hi[i : i + 2], 16) for i in (1, 3, 5))
    r, g, b = (int(lo_rgb[i] + (hi_rgb[i] - lo_rgb[i]) * t) for i in range(3))
    return f"#{r:02x}{g:02x}{b:02x}"

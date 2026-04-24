"""Fetch road-network routes between stops via OSRM public API.

OSRM's public demo server (router.project-osrm.org) is rate-limited and
intended for light use. Routes are aggressively cached per segment to keep
repeat calls out of the hot path.
"""

from __future__ import annotations

import time
from pathlib import Path

import pandas as pd
import requests

OSRM_URL = "https://router.project-osrm.org/route/v1/driving/{lon1},{lat1};{lon2},{lat2}"
CACHE_DIR = Path(".cache")
CACHE_PATH = CACHE_DIR / "osrm_routes_full.parquet"

SEGMENT_THROTTLE_S = 0.2
ROUND_DECIMALS = 5


def _segment_key(lat1: float, lon1: float, lat2: float, lon2: float) -> tuple:
    return (
        round(lat1, ROUND_DECIMALS),
        round(lon1, ROUND_DECIMALS),
        round(lat2, ROUND_DECIMALS),
        round(lon2, ROUND_DECIMALS),
    )


def _load_cache() -> dict[tuple, list[tuple[float, float]]]:
    if not CACHE_PATH.exists():
        return {}
    df = pd.read_parquet(CACHE_PATH)
    cache: dict[tuple, list[tuple[float, float]]] = {}
    for _, row in df.iterrows():
        key = (row["lat1"], row["lon1"], row["lat2"], row["lon2"])
        coords = list(zip(row["route_lats"], row["route_lons"]))
        cache[key] = coords
    return cache


def _save_cache(cache: dict[tuple, list[tuple[float, float]]]) -> None:
    CACHE_DIR.mkdir(exist_ok=True)
    rows = []
    for (lat1, lon1, lat2, lon2), coords in cache.items():
        rows.append(
            {
                "lat1": lat1,
                "lon1": lon1,
                "lat2": lat2,
                "lon2": lon2,
                "route_lats": [c[0] for c in coords],
                "route_lons": [c[1] for c in coords],
            }
        )
    pd.DataFrame(rows).to_parquet(CACHE_PATH, index=False)


def _fetch_one(lat1: float, lon1: float, lat2: float, lon2: float) -> list[tuple[float, float]]:
    url = OSRM_URL.format(lon1=lon1, lat1=lat1, lon2=lon2, lat2=lat2)
    params = {"overview": "full", "geometries": "geojson"}
    headers = {"User-Agent": "postnl-route-analyse/0.1"}
    r = requests.get(url, params=params, headers=headers, timeout=30)
    r.raise_for_status()
    data = r.json()
    routes = data.get("routes") or []
    if not routes:
        return []
    coords = routes[0]["geometry"]["coordinates"]
    return [(lat, lon) for lon, lat in coords]


def unique_segments(stops: pd.DataFrame) -> list[tuple[float, float, float, float]]:
    """Extract unique consecutive (from, to) coordinate pairs across all trips."""
    segs: set[tuple[float, float, float, float]] = set()
    for _, g in stops.groupby(["wagencode", "trip_date", "trip_id"]):
        if len(g) < 2:
            continue
        coords = g[["lat", "lon"]].values
        for i in range(len(coords) - 1):
            segs.add(_segment_key(coords[i][0], coords[i][1], coords[i + 1][0], coords[i + 1][1]))
    return list(segs)


def load_cached_routes(
    segments: list[tuple[float, float, float, float]],
) -> tuple[dict[tuple, list[tuple[float, float]]], int]:
    """Return (routes_dict, n_missing) — pakt alleen wat al in cache zit."""
    if not CACHE_PATH.exists():
        return {}, len(segments)
    cache = _load_cache()
    found = {s: cache[s] for s in segments if s in cache}
    return found, len(segments) - len(found)


def fetch_routes(
    segments: list[tuple[float, float, float, float]],
    progress_cb=None,
) -> dict[tuple, list[tuple[float, float]]]:
    """Fetch road geometry for each segment, using (and updating) disk cache."""
    cache = _load_cache()
    missing = [s for s in segments if s not in cache]
    total = len(missing)

    for i, (lat1, lon1, lat2, lon2) in enumerate(missing, start=1):
        try:
            coords = _fetch_one(lat1, lon1, lat2, lon2)
            cache[(lat1, lon1, lat2, lon2)] = coords
        except Exception:
            cache[(lat1, lon1, lat2, lon2)] = [(lat1, lon1), (lat2, lon2)]
        if progress_cb:
            progress_cb(i, total)
        time.sleep(SEGMENT_THROTTLE_S)

    if missing:
        _save_cache(cache)
    return {s: cache[s] for s in segments}


def trip_polyline(
    stops: pd.DataFrame,
    routes: dict[tuple, list[tuple[float, float]]],
) -> list[tuple[float, float]]:
    """Stitch one trip's route by concatenating cached segment geometries."""
    coords = stops[["lat", "lon"]].values
    out: list[tuple[float, float]] = []
    for i in range(len(coords) - 1):
        key = _segment_key(coords[i][0], coords[i][1], coords[i + 1][0], coords[i + 1][1])
        seg = routes.get(key) or [(coords[i][0], coords[i][1]), (coords[i + 1][0], coords[i + 1][1])]
        if out and seg and out[-1] == seg[0]:
            out.extend(seg[1:])
        else:
            out.extend(seg)
    return out

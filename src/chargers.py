"""Fetch and cache public fast chargers from Open Charge Map."""

from __future__ import annotations

import math
import os
from pathlib import Path
from typing import Any

import pandas as pd
import requests

OCM_URL = "https://api.openchargemap.io/v3/poi/"
CACHE_DIR = Path(".cache")
CACHE_PATH = CACHE_DIR / "ocm_chargers_nl.parquet"

DEFAULT_MIN_POWER_KW = 150
DEFAULT_COUNTRY = "NL"


def _parse_poi(poi: dict[str, Any], min_power_kw: float) -> dict[str, Any] | None:
    addr = poi.get("AddressInfo") or {}
    lat = addr.get("Latitude")
    lon = addr.get("Longitude")
    if lat is None or lon is None:
        return None

    connections = poi.get("Connections") or []
    max_kw = 0.0
    for c in connections:
        kw = c.get("PowerKW") or 0
        if kw and kw > max_kw:
            max_kw = float(kw)
    if max_kw < min_power_kw:
        return None

    operator = (poi.get("OperatorInfo") or {}).get("Title") or ""

    return {
        "id": poi.get("ID"),
        "name": addr.get("Title") or "",
        "operator": operator,
        "address": addr.get("AddressLine1") or "",
        "town": addr.get("Town") or "",
        "postcode": addr.get("Postcode") or "",
        "lat": float(lat),
        "lon": float(lon),
        "max_power_kw": max_kw,
        "n_connectors": len(connections),
    }


class OCMKeyMissing(RuntimeError):
    """Raised when OCM API key is required but not provided."""


def fetch_chargers(
    country: str = DEFAULT_COUNTRY,
    min_power_kw: float = DEFAULT_MIN_POWER_KW,
    max_results: int = 5000,
    api_key: str | None = None,
    use_cache: bool = True,
) -> pd.DataFrame:
    """Fetch NL fast chargers from OCM. Cached to parquet after first fetch."""
    if use_cache and CACHE_PATH.exists():
        cached = pd.read_parquet(CACHE_PATH)
        return cached[cached["max_power_kw"] >= min_power_kw].reset_index(drop=True)

    key = api_key or os.getenv("OCM_API_KEY")
    if not key:
        raise OCMKeyMissing(
            "Open Charge Map vereist een gratis API-key. "
            "Registreer op https://openchargemap.org/site/develop/api en "
            "zet OCM_API_KEY in .env."
        )

    params = {
        "output": "json",
        "countrycode": country,
        "maxresults": max_results,
        "compact": "true",
        "verbose": "false",
        "key": key,
    }
    headers = {"User-Agent": "postnl-route-analyse/0.1"}

    r = requests.get(OCM_URL, params=params, headers=headers, timeout=60)
    r.raise_for_status()
    pois = r.json()

    rows = [p for poi in pois if (p := _parse_poi(poi, min_power_kw))]
    df = pd.DataFrame(rows)

    CACHE_DIR.mkdir(exist_ok=True)
    df.to_parquet(CACHE_PATH, index=False)
    return df


def haversine_km(lat1: float, lon1: float, lat2: float, lon2: float) -> float:
    r = 6371.0
    phi1, phi2 = math.radians(lat1), math.radians(lat2)
    dphi = math.radians(lat2 - lat1)
    dlam = math.radians(lon2 - lon1)
    a = math.sin(dphi / 2) ** 2 + math.cos(phi1) * math.cos(phi2) * math.sin(dlam / 2) ** 2
    return 2 * r * math.asin(math.sqrt(a))


def add_nearest_charger_distance(
    hotspots: pd.DataFrame,
    chargers: pd.DataFrame,
    lat_col: str = "lat_round",
    lon_col: str = "lon_round",
) -> pd.DataFrame:
    """Add column with distance (km) to nearest fast charger."""
    if chargers.empty or hotspots.empty:
        hotspots = hotspots.copy()
        hotspots["afstand_lader_km"] = pd.NA
        return hotspots

    charger_lat = chargers["lat"].to_numpy()
    charger_lon = chargers["lon"].to_numpy()

    dists = []
    for lat, lon in zip(hotspots[lat_col].to_numpy(), hotspots[lon_col].to_numpy()):
        d = [haversine_km(lat, lon, clat, clon) for clat, clon in zip(charger_lat, charger_lon)]
        dists.append(min(d) if d else float("nan"))

    out = hotspots.copy()
    out["afstand_lader_km"] = [round(d, 1) for d in dists]
    return out

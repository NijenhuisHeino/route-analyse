"""Geocode Dutch city names via Nominatim, cached to disk."""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from geopy.extra.rate_limiter import RateLimiter
from geopy.geocoders import Nominatim

CACHE_DIR = Path(".cache")
CACHE_PATH = CACHE_DIR / "geocode_cities.parquet"
REVERSE_CACHE_PATH = CACHE_DIR / "reverse_geocode.parquet"
USER_AGENT = "postnl-route-analyse/0.1 (johnny@nijenhuistrucksolutions.nl)"
REVERSE_ROUND = 4  # ~11 m grid: cache hits voor dichtbijgelegen punten


def _load_cache() -> dict[str, tuple[float, float] | None]:
    if not CACHE_PATH.exists():
        return {}
    df = pd.read_parquet(CACHE_PATH)
    out: dict[str, tuple[float, float] | None] = {}
    for _, row in df.iterrows():
        lat, lon = row["lat"], row["lon"]
        if pd.isna(lat) or pd.isna(lon):
            out[row["query"]] = None
        else:
            out[row["query"]] = (float(lat), float(lon))
    return out


def _save_cache(cache: dict[str, tuple[float, float] | None]) -> None:
    CACHE_DIR.mkdir(exist_ok=True)
    rows = []
    for q, val in cache.items():
        if val is None:
            rows.append({"query": q, "lat": None, "lon": None})
        else:
            rows.append({"query": q, "lat": val[0], "lon": val[1]})
    pd.DataFrame(rows).to_parquet(CACHE_PATH, index=False)


def geocode_queries(
    queries: list[str],
    country_code: str = "nl",
    progress_cb=None,
) -> dict[str, tuple[float, float] | None]:
    """Geocode a list of strings to (lat, lon) tuples, using disk cache.

    Nominatim's usage policy requires a descriptive user agent and <=1 request/s.
    """
    cache = _load_cache()
    missing = sorted({q for q in queries if q and q not in cache})

    if missing:
        geolocator = Nominatim(user_agent="postnl-route-analyse/0.1 (johnny@nijenhuistrucksolutions.nl)")
        geocode = RateLimiter(geolocator.geocode, min_delay_seconds=1.1)

        for i, q in enumerate(missing, start=1):
            try:
                loc = geocode(q, country_codes=country_code, exactly_one=True, timeout=15)
                if loc:
                    cache[q] = (loc.latitude, loc.longitude)
                else:
                    cache[q] = None
            except Exception:
                cache[q] = None
            if progress_cb:
                progress_cb(i, len(missing))

        _save_cache(cache)

    return {q: cache.get(q) for q in queries}


def _key(lat: float, lon: float) -> tuple[float, float]:
    return (round(lat, REVERSE_ROUND), round(lon, REVERSE_ROUND))


def _load_reverse_cache() -> dict[tuple[float, float], dict]:
    if not REVERSE_CACHE_PATH.exists():
        return {}
    df = pd.read_parquet(REVERSE_CACHE_PATH)
    out: dict[tuple[float, float], dict] = {}
    for _, row in df.iterrows():
        out[(float(row["lat"]), float(row["lon"]))] = {
            "road": row.get("road") or "",
            "town": row.get("town") or "",
            "display": row.get("display") or "",
        }
    return out


def _save_reverse_cache(cache: dict[tuple[float, float], dict]) -> None:
    CACHE_DIR.mkdir(exist_ok=True)
    rows = []
    for (lat, lon), info in cache.items():
        rows.append(
            {
                "lat": lat,
                "lon": lon,
                "road": info.get("road") or "",
                "town": info.get("town") or "",
                "display": info.get("display") or "",
            }
        )
    pd.DataFrame(rows).to_parquet(REVERSE_CACHE_PATH, index=False)


def reverse_geocode(
    coords: list[tuple[float, float]],
    progress_cb=None,
) -> dict[tuple[float, float], dict]:
    """Reverse-geocode (lat, lon) punten naar {road, town, display}, gecached.

    Nominatim usage-policy: 1.1 s tussen requests. Eerste run traag, daarna instant.
    """
    cache = _load_reverse_cache()
    want_keys = [_key(lat, lon) for lat, lon in coords]
    missing_keys = sorted({k for k in want_keys if k not in cache})

    if missing_keys:
        geolocator = Nominatim(user_agent=USER_AGENT)
        reverse = RateLimiter(geolocator.reverse, min_delay_seconds=1.1)

        for i, (lat, lon) in enumerate(missing_keys, start=1):
            try:
                loc = reverse((lat, lon), timeout=15, zoom=16, language="nl")
                if loc:
                    a = loc.raw.get("address", {}) if hasattr(loc, "raw") else {}
                    road = (
                        a.get("road")
                        or a.get("pedestrian")
                        or a.get("footway")
                        or a.get("highway")
                        or ""
                    )
                    town = (
                        a.get("town")
                        or a.get("city")
                        or a.get("village")
                        or a.get("municipality")
                        or a.get("suburb")
                        or ""
                    )
                    cache[(lat, lon)] = {
                        "road": road,
                        "town": town,
                        "display": loc.address,
                    }
                else:
                    cache[(lat, lon)] = {"road": "", "town": "", "display": ""}
            except Exception:
                cache[(lat, lon)] = {"road": "", "town": "", "display": ""}
            if progress_cb:
                progress_cb(i, len(missing_keys))

        _save_reverse_cache(cache)

    return {k: cache.get(k, {"road": "", "town": "", "display": ""}) for k in want_keys}

"""Geocode Dutch city names via Nominatim, cached to disk."""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from geopy.extra.rate_limiter import RateLimiter
from geopy.geocoders import Nominatim

CACHE_DIR = Path(".cache")
CACHE_PATH = CACHE_DIR / "geocode_cities.parquet"


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

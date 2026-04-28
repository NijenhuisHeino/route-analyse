"""Load PostNL monthly trip-detail CSVs ("Rittendata per wagen detail").

Eén directory met *.csv per maand, samen één jaar. Per rij: één actie binnen
een trip. We houden alleen `Actie soort == 'travel'`. Volgorde van travel-rijen
binnen één tripnummer = stop-volgorde.

Adressen worden via Nominatim gegeocodeerd (gecached). Eindresultaat wordt
gecached als parquet in `.cache/postnl_csv_<dirname>.parquet`.
"""

from __future__ import annotations

import re
from pathlib import Path

import pandas as pd

from src.geocoder import geocode_addresses

CACHE_DIR = Path(".cache")

CSV_USECOLS = [
    "Voertuig Type Eigenaar",
    "Wagen Code",
    "Wagentype Omschrijving",
    "Tripnummer",
    "Starttijd Trip",
    "Eindtijd Trip",
    "Totale Afstand (KM)",
    "Actie soort",
    "Gepland vanaf (Trip actie)",
    "Gepland tot (Trip actie)",
    "Adres",
]


def list_monthly_csvs(directory: Path) -> list[Path]:
    if not directory.exists():
        return []
    return sorted(directory.glob("*.csv"))


def _read_one(path: Path) -> pd.DataFrame:
    df = pd.read_csv(
        path,
        usecols=CSV_USECOLS,
        dtype={
            "Wagen Code": "string",
            "Tripnummer": "string",
            "Adres": "string",
            "Actie soort": "string",
            "Voertuig Type Eigenaar": "string",
            "Wagentype Omschrijving": "string",
        },
    )
    return df[df["Actie soort"] == "travel"].reset_index(drop=True)


def _cache_path(directory: Path) -> Path:
    safe = re.sub(r"[^A-Za-z0-9]+", "_", directory.name).strip("_")
    return CACHE_DIR / f"postnl_csv_{safe}.parquet"


def load_monthly_csvs(
    directory: Path,
    progress_cb=None,
    geocode_progress_cb=None,
    use_cache: bool = True,
) -> pd.DataFrame:
    """Lees alle maand-CSV's in directory, return unified stops DataFrame.

    Resultaat gecached per directory (cache-pad gebaseerd op dir-naam).
    """
    cache_path = _cache_path(directory)
    if use_cache and cache_path.exists():
        return pd.read_parquet(cache_path)

    files = list_monthly_csvs(directory)
    if not files:
        raise FileNotFoundError(f"Geen CSV's in {directory}")

    parts = []
    for i, f in enumerate(files, start=1):
        if progress_cb:
            progress_cb(i, len(files), f.name)
        parts.append(_read_one(f))

    df = pd.concat(parts, ignore_index=True)

    df = df.rename(
        columns={
            "Voertuig Type Eigenaar": "vervoerder",
            "Wagen Code": "wagencode",
            "Tripnummer": "trip_id",
            "Adres": "adres",
            "Actie soort": "acties",
            "Gepland vanaf (Trip actie)": "gepland_start",
            "Gepland tot (Trip actie)": "gepland_eind",
            "Totale Afstand (KM)": "afstand_km_trip",
        }
    )

    df["gepland_start"] = pd.to_datetime(
        df["gepland_start"], dayfirst=True, errors="coerce"
    )
    df["gepland_eind"] = pd.to_datetime(
        df["gepland_eind"], dayfirst=True, errors="coerce"
    )
    df["trip_date"] = df["gepland_start"].dt.floor("D")

    df["dwell_min"] = (
        (df["gepland_eind"] - df["gepland_start"]).dt.total_seconds() / 60
    ).clip(lower=0)

    df["vervoerder_type"] = (
        df["vervoerder"]
        .astype("string")
        .str.strip()
        .str.lower()
        .map({"eigen vervoer": "eigen", "uitbesteed vervoer": "charter"})
        .fillna("onbekend")
    )

    df["adres"] = df["adres"].astype("string").str.strip()
    df = df[df["adres"].notna() & (df["adres"] != "")].reset_index(drop=True)

    unique_addr = df["adres"].unique().tolist()
    addr_to_coords = geocode_addresses(unique_addr, progress_cb=geocode_progress_cb)

    lats: list[float | None] = []
    lons: list[float | None] = []
    for a in df["adres"]:
        coords = addr_to_coords.get(a)
        if coords:
            lats.append(coords[0])
            lons.append(coords[1])
        else:
            lats.append(None)
            lons.append(None)
    df["lat"] = lats
    df["lon"] = lons

    df = df.dropna(subset=["lat", "lon"]).reset_index(drop=True)

    df = df.sort_values(
        ["wagencode", "trip_id", "gepland_start"], kind="stable"
    ).reset_index(drop=True)
    df["stop_seq"] = df.groupby("trip_id").cumcount()
    df["trip_stop_nr"] = (
        df["trip_id"].astype(str) + "-" + df["stop_seq"].astype(str).str.zfill(2)
    )

    df["locatie_naam"] = df["adres"]
    df["ord_location_id"] = pd.NA
    df["dagorder"] = pd.NA
    df["gewicht_na_stop"] = pd.NA
    df["afstand_km"] = 0.0
    df["rijtijd_min"] = pd.NA
    df["laad_los"] = pd.NA

    CACHE_DIR.mkdir(exist_ok=True)
    df.to_parquet(cache_path, index=False)
    return df

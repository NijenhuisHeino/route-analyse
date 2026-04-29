"""Loader voor handmatig geverifieerde HDV-laadlocaties (Heavy Duty Vehicles).

Bron: Excel-bestand met locaties die bevestigd toegankelijk zijn voor
vrachtwagens. Per locatie meerdere rijen (één per laadpaal); we aggregeren
naar één row per `LocatieID`.

Voorrang van bron-paden:
1. Cache parquet (`.cache/hdv_chargers.parquet`)
2. Drive-bestand (durable storage)
3. Lokale workspace-attachment (eerste import)
"""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from python_calamine import CalamineWorkbook

CACHE_PATH = Path(".cache/hdv_chargers.parquet")
DRIVE_PATH = Path(
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/"
    "Data analyse ritten/Route analyse tool/data/laadstations_HDV_extern.xlsx"
)
LOCAL_PATH = Path(".context/attachments/laadstations_HDV_extern.xlsx")


def _parse_coords(s: object) -> tuple[float | None, float | None]:
    if not isinstance(s, str):
        return None, None
    parts = [p.strip() for p in s.split(",")]
    if len(parts) != 2:
        return None, None
    try:
        return float(parts[0]), float(parts[1])
    except ValueError:
        return None, None


def _resolve_source() -> Path | None:
    for p in (DRIVE_PATH, LOCAL_PATH):
        if p.exists():
            return p
    return None


def _read_excel(path: Path) -> pd.DataFrame:
    wb = CalamineWorkbook.from_path(str(path))
    ws = wb.get_sheet_by_name("data")
    rows = ws.to_python()
    if not rows:
        return pd.DataFrame()
    header = rows[0]
    return pd.DataFrame(rows[1:], columns=header)


def load_hdv_chargers(
    use_cache: bool = True,
    min_power_kw: float = 0,
) -> pd.DataFrame:
    """Laad geverifieerde HDV-laadlocaties, één row per LocatieID.

    Returns DataFrame met kolommen die `add_nearest_charger_distance` en de
    map-rendering verwachten: lat, lon, name, operator, address, town, postcode,
    max_power_kw, n_connectors. Plus HDV-specifieke kolommen voor popup-detail.
    """
    if use_cache and CACHE_PATH.exists():
        df = pd.read_parquet(CACHE_PATH)
    else:
        src = _resolve_source()
        if src is None:
            raise FileNotFoundError(
                f"Geen HDV-laadstations Excel gevonden op {DRIVE_PATH} "
                f"of {LOCAL_PATH}."
            )
        raw = _read_excel(src)
        df = _aggregate(raw)
        CACHE_PATH.parent.mkdir(exist_ok=True)
        df.to_parquet(CACHE_PATH, index=False)

    if min_power_kw > 0:
        df = df[df["max_power_kw"] >= min_power_kw]
    return df.reset_index(drop=True)


def _aggregate(raw: pd.DataFrame) -> pd.DataFrame:
    coords = raw["Coordinaten"].apply(_parse_coords)
    raw = raw.assign(
        _lat=coords.apply(lambda c: c[0]),
        _lon=coords.apply(lambda c: c[1]),
    )
    raw = raw.dropna(subset=["_lat", "_lon", "LocatieID"]).copy()
    raw["LocatieID"] = raw["LocatieID"].astype(int)

    grouped = raw.groupby("LocatieID")
    out = pd.DataFrame(
        {
            "LocatieID": grouped["LocatieID"].first().astype(int),
            "lat": grouped["_lat"].first(),
            "lon": grouped["_lon"].first(),
            "name": grouped["CPO"].first(),
            "operator": grouped["CPO"].first(),
            "address": grouped["Adres"].first(),
            "town": grouped["Stad"].first(),
            "postcode": grouped["Postcode"].first(),
            "max_power_kw": pd.to_numeric(
                grouped["Vermogen"].max(), errors="coerce"
            ).fillna(0),
            "n_connectors": grouped.size(),
            "toegankelijkheid": grouped["Toegankelijkheid"].first(),
            "twentyfour_seven": grouped["Twentyfour_seven"].first(),
            "dedicated": grouped["Dedicated"].first(),
            "ccs_mcs": grouped["CCS/MCS"].first(),
            "wachtruimte": grouped["Wachtruimte"].first(),
            "in_gebruik_vanaf": grouped["In_gebruik_vanaf"].first(),
        }
    ).reset_index(drop=True)

    return out

"""Zero-emission zone (ZE-zone) lookup per Nederlandse PC6-postcode.

Bron-Excel met 26.720 postcodes in 30+ ZE-zones (Rotterdam, A'dam, Den Haag,
Tilburg etc.) met per-zone startdatum (meestal 2025-2030).

Functies:
- `load_zez_pc6()` — laad lookup-table, gecached als parquet
- `extract_pc6(adres)` — '3439 JG Nieuwegein' → '3439JG'
- `annotate_stops_with_zez(stops, zez_df)` — voeg ze_zone, ze_startdatum, in_zez kolommen toe
"""

from __future__ import annotations

import re
from pathlib import Path

import pandas as pd
from python_calamine import CalamineWorkbook

CACHE_PATH = Path(".cache/zez_pc6.parquet")
DRIVE_PATH = Path(
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/"
    "Data analyse ritten/Route analyse tool/data/zez_pc6.xlsx"
)
LOCAL_PATH = Path(".context/attachments/20260130_143653_pc6_zeroemissionzones.zip")

PC6_RE = re.compile(r"(\d{4})\s*([A-Z]{2})", re.IGNORECASE)


def extract_pc6(address: object) -> str | None:
    """Haal PC6 uit een adresstring. Returns 'XXXXAA' of None."""
    if not isinstance(address, str):
        return None
    m = PC6_RE.search(address.upper())
    if m:
        return m.group(1) + m.group(2)
    return None


def _resolve_source() -> Path | None:
    if DRIVE_PATH.exists():
        return DRIVE_PATH
    return None


def _read_excel(path: Path) -> pd.DataFrame:
    wb = CalamineWorkbook.from_path(str(path))
    ws = wb.get_sheet_by_name("overlap_pc6_ze_zones")
    rows = ws.to_python()
    return pd.DataFrame(rows[1:], columns=rows[0])


def load_zez_pc6(use_cache: bool = True) -> pd.DataFrame:
    """Laad ZE-zone lookup. Returns df met cols: pc6, ze_zone, ze_startdatum."""
    if use_cache and CACHE_PATH.exists():
        return pd.read_parquet(CACHE_PATH)
    src = _resolve_source()
    if src is None:
        raise FileNotFoundError(
            f"Geen ZE-zones Excel gevonden op {DRIVE_PATH}."
        )
    raw = _read_excel(src)
    in_zone = raw[raw["in_zero_emissie_zone"] == "ja"].copy()
    in_zone["pc6"] = in_zone["pc6"].astype(str).str.upper().str.strip()
    in_zone["ze_zone"] = in_zone["ze_zone"].astype(str).fillna("")
    in_zone["ze_startdatum"] = in_zone["ze_startdatum"].astype(str).fillna("")
    out = in_zone[["pc6", "ze_zone", "ze_startdatum"]].drop_duplicates(
        subset=["pc6"]
    ).reset_index(drop=True)
    CACHE_PATH.parent.mkdir(exist_ok=True)
    out.to_parquet(CACHE_PATH, index=False)
    return out


def annotate_stops_with_zez(
    stops: pd.DataFrame, zez_df: pd.DataFrame
) -> pd.DataFrame:
    """Voeg ze_zone, ze_startdatum, in_zez kolommen toe aan stops df."""
    if stops.empty:
        return stops
    out = stops.copy()
    out["pc6"] = out["adres"].apply(extract_pc6)
    lookup = zez_df.set_index("pc6")
    out["ze_zone"] = out["pc6"].map(lookup["ze_zone"]).fillna("")
    out["ze_startdatum"] = out["pc6"].map(lookup["ze_startdatum"]).fillna("")
    out["in_zez"] = out["ze_zone"] != ""
    return out

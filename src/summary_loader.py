"""Load PostNL `Rittendata per wagen` trip-summary Excel format.

Output matches the unified schema used by the rest of the app so the same
map/heatmap/hotspot code can render it. One input trip produces up to two
virtual stops: origin city and destination city, each geocoded.
"""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from python_calamine import CalamineWorkbook

from src.geocoder import geocode_queries

SUMMARY_REQUIRED = {
    "Wagen Code",
    "Tripnummer",
    "Eerste Stop Plaatsnaam",
    "Laatste Stop Plaatsnaam",
}


def detect_summary_sheet(excel_path: Path) -> str | None:
    wb = CalamineWorkbook.from_path(str(excel_path))
    for name in wb.sheet_names:
        ws = wb.get_sheet_by_name(name)
        header_rows = ws.to_python(nrows=1)
        if not header_rows:
            continue
        header = header_rows[0]
        header_set = {h for h in header if isinstance(h, str) and h}
        if SUMMARY_REQUIRED.issubset(header_set):
            return name
    return None


def _read_sheet(excel_path: Path, sheet_name: str) -> pd.DataFrame:
    """Read a sheet via python_calamine directly (bypasses pandas/calamine bridge bug)."""
    wb = CalamineWorkbook.from_path(str(excel_path))
    ws = wb.get_sheet_by_name(sheet_name)
    rows = ws.to_python()
    if not rows:
        return pd.DataFrame()
    header = rows[0]
    seen: dict[str, int] = {}
    unique_header = []
    for i, h in enumerate(header):
        name = (h if isinstance(h, str) else "") or f"col_{i}"
        if name in seen:
            seen[name] += 1
            unique_header.append(f"{name}.{seen[name]}")
        else:
            seen[name] = 0
            unique_header.append(name)
    return pd.DataFrame(rows[1:], columns=unique_header)


def _coerce_start_end(df: pd.DataFrame) -> pd.DataFrame:
    """Build gepland_start / gepland_eind / trip_date from V1 or V2 columns."""
    if "Datumtijd Vanaf" in df.columns:
        df["gepland_start"] = pd.to_datetime(df["Datumtijd Vanaf"], errors="coerce")
        df["gepland_eind"] = pd.to_datetime(df["Datumtijd Tot"], errors="coerce")
    elif "Datum Vanaf" in df.columns:
        df["gepland_start"] = pd.to_datetime(
            df["Datum Vanaf"].astype(str) + " " + df["Tijd Vanaf"].astype(str),
            errors="coerce",
        )
        df["gepland_eind"] = pd.to_datetime(
            df["Datum Tot"].astype(str) + " " + df["Tijd Tot"].astype(str),
            errors="coerce",
        )
    else:
        df["gepland_start"] = pd.NaT
        df["gepland_eind"] = pd.NaT

    df["trip_date"] = df["gepland_start"].dt.floor("D")
    return df


def load_trip_summaries(
    excel_path: Path,
    sheet_name: str | None = None,
    progress_cb=None,
) -> pd.DataFrame:
    """Load a Rittendata-per-wagen Excel and return a unified stops DataFrame."""
    if sheet_name is None:
        sheet_name = detect_summary_sheet(excel_path)
    if sheet_name is None:
        raise ValueError(f"No summary sheet found in {excel_path}")

    df = _read_sheet(excel_path, sheet_name)
    df = df[df["Wagen Code"].astype(str).str.strip().str.lower() != "totaal"]

    keep = [
        c
        for c in [
            "Voertuig Type Eigenaar",
            "Wagen Code",
            "Adres Standplaats",
            "Tripnummer",
            "Datum Vanaf",
            "Tijd Vanaf",
            "Datum Tot",
            "Tijd Tot",
            "Datumtijd Vanaf",
            "Datumtijd Tot",
            "Eerste Stop Plaatsnaam",
            "Laatste Stop Plaatsnaam",
            "Totale Rij-afstand (km)",
            "Totale rij-afstand (km)",
        ]
        if c in df.columns
    ]
    df = df[keep].copy()
    df.columns = [c.strip() for c in df.columns]

    if "Totale Rij-afstand (km)" in df.columns:
        df["afstand_km"] = pd.to_numeric(df["Totale Rij-afstand (km)"], errors="coerce")
    elif "Totale rij-afstand (km)" in df.columns:
        df["afstand_km"] = pd.to_numeric(df["Totale rij-afstand (km)"], errors="coerce")
    else:
        df["afstand_km"] = pd.NA

    df = _coerce_start_end(df)

    df = df.dropna(subset=["Wagen Code", "Tripnummer"]).reset_index(drop=True)

    df["vervoerder_type"] = (
        df["Voertuig Type Eigenaar"]
        .astype("string")
        .str.strip()
        .str.lower()
        .map({"eigen vervoer": "eigen", "uitbesteed vervoer": "charter"})
        .fillna("onbekend")
    )

    cities = pd.unique(
        pd.concat(
            [df["Eerste Stop Plaatsnaam"], df["Laatste Stop Plaatsnaam"]]
        ).dropna()
    ).tolist()
    cities = [str(c).strip() for c in cities if str(c).strip()]
    geo = geocode_queries(cities, progress_cb=progress_cb)

    def _expand(row: pd.Series) -> list[dict]:
        out = []
        for seq, col in enumerate(["Eerste Stop Plaatsnaam", "Laatste Stop Plaatsnaam"]):
            city = row[col]
            if not isinstance(city, str) or not city.strip():
                continue
            coords = geo.get(city.strip())
            if not coords:
                continue
            lat, lon = coords
            out.append(
                {
                    "wagencode": str(row["Wagen Code"]),
                    "vervoerder": row.get("Voertuig Type Eigenaar") or "",
                    "vervoerder_type": row["vervoerder_type"],
                    "trip_date": row["trip_date"],
                    "trip_id": str(row["Tripnummer"]),
                    "trip_stop_nr": f"{row['Tripnummer']}-{seq:02d}",
                    "stop_seq": seq,
                    "acties": "Origin" if seq == 0 else "Destination",
                    "locatie_naam": city.strip(),
                    "adres": row.get("Adres Standplaats") or "",
                    "gepland_start": row["gepland_start"],
                    "gepland_eind": row["gepland_eind"],
                    "dwell_min": 0.0,
                    "afstand_km": row["afstand_km"] if seq == 1 else 0,
                    "lat": lat,
                    "lon": lon,
                }
            )
        return out

    expanded: list[dict] = []
    for _, row in df.iterrows():
        expanded.extend(_expand(row))

    out = pd.DataFrame(expanded)
    if out.empty:
        return out
    out = out.sort_values(["wagencode", "trip_date", "trip_id", "stop_seq"]).reset_index(
        drop=True
    )
    return out

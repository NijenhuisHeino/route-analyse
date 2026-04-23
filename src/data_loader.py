"""Load and prepare PostNL trip-stop data for analysis."""

from __future__ import annotations

from pathlib import Path

import pandas as pd
from python_calamine import CalamineWorkbook

COLUMN_MAP = {
    "Wagencode": "wagencode",
    "Vervoerder": "vervoerder",
    "Trip execution date": "trip_date",
    "Trip-Stop nummer": "trip_stop_nr",
    "Acties op stop": "acties",
    "Locatienaam": "locatie_naam",
    "ORD locationID": "ord_location_id",
    "Geplande starttijd": "gepland_start",
    "Geplande eindtijd": "gepland_eind",
    "Afstand van vorige (km)": "afstand_km",
    "Rijtijd van vorige (min)": "rijtijd_min",
    "Laad/los actie": "laad_los",
    "Dagorder nummer": "dagorder",
    "Gewicht na stop": "gewicht_na_stop",
    "Adres": "adres",
    "Lengtegraad": "lon",
    "Breedtegraad": "lat",
}

REQUIRED_HEADERS = {"Wagencode", "Trip-Stop nummer", "Lengtegraad", "Breedtegraad"}
SUMMARY_HEADERS = {
    "Wagen Code",
    "Tripnummer",
    "Eerste Stop Plaatsnaam",
    "Laatste Stop Plaatsnaam",
}


class UnsupportedSchema(ValueError):
    """Raised when the Excel workbook has no sheet with the expected columns."""


def _sheet_headers(excel_path: Path) -> list[tuple[str, set[str]]]:
    wb = CalamineWorkbook.from_path(str(excel_path))
    out = []
    for name in wb.sheet_names:
        ws = wb.get_sheet_by_name(name)
        rows = ws.to_python(nrows=1)
        header = rows[0] if rows else []
        out.append((name, {h for h in header if isinstance(h, str) and h}))
    return out


def detect_schema(excel_path: Path) -> tuple[str, str]:
    """Return (schema, sheet_name). schema is 'trip_stop' or 'trip_summary'."""
    headers = _sheet_headers(excel_path)
    for name, header_set in headers:
        if REQUIRED_HEADERS.issubset(header_set):
            return "trip_stop", name
    for name, header_set in headers:
        if SUMMARY_HEADERS.issubset(header_set):
            return "trip_summary", name
    raise UnsupportedSchema(
        f"Geen bruikbare sheet in '{excel_path.name}'. "
        "Ondersteund: TRP BI trip-stop export (met Lengtegraad/Breedtegraad), "
        "of Rittendata per wagen (met Eerste/Laatste Stop Plaatsnaam)."
    )


def list_excel_files(data_dir: Path) -> list[Path]:
    """Return .xlsx files in data_dir, sorted newest first."""
    if not data_dir.exists():
        return []
    files = [p for p in data_dir.glob("*.xlsx") if not p.name.startswith("~$")]
    files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    return files


def detect_trip_stop_sheet(excel_path: Path) -> str:
    """Return the first sheet name whose header row contains all required columns."""
    for name, header_set in _sheet_headers(excel_path):
        if REQUIRED_HEADERS.issubset(header_set):
            return name
    raise UnsupportedSchema(
        f"Geen sheet in '{excel_path.name}' bevat de verwachte kolommen "
        f"(minimaal: {', '.join(sorted(REQUIRED_HEADERS))}). "
        "Dit bestand lijkt geen trip-stop export (TRP BI)."
    )


def load_trips(excel_path: Path, sheet_name: str | None = None) -> pd.DataFrame:
    """Load trip-stop export and return cleaned DataFrame.

    Splits Trip-Stop nummer into trip_id / stop_seq, computes dwell time,
    drops rows without coordinates.
    """
    if sheet_name is None:
        sheet_name = detect_trip_stop_sheet(excel_path)
    wb = CalamineWorkbook.from_path(str(excel_path))
    ws = wb.get_sheet_by_name(sheet_name)
    rows = ws.to_python()
    header = rows[0] if rows else []
    df = pd.DataFrame(rows[1:], columns=header)
    df = df.rename(columns=COLUMN_MAP)

    df[["trip_id", "stop_seq"]] = df["trip_stop_nr"].str.split("-", n=1, expand=True)
    df["stop_seq"] = pd.to_numeric(df["stop_seq"], errors="coerce").astype("Int64")

    df["trip_date"] = pd.to_datetime(df["trip_date"], errors="coerce")
    df["gepland_start"] = pd.to_datetime(df["gepland_start"], errors="coerce")
    df["gepland_eind"] = pd.to_datetime(df["gepland_eind"], errors="coerce")

    df["dwell_min"] = (
        (df["gepland_eind"] - df["gepland_start"]).dt.total_seconds() / 60
    ).clip(lower=0)

    df["lat"] = pd.to_numeric(df["lat"], errors="coerce")
    df["lon"] = pd.to_numeric(df["lon"], errors="coerce")

    df = df.dropna(subset=["lat", "lon"])
    df = df[(df["lat"].between(-90, 90)) & (df["lon"].between(-180, 180))]

    vervoerder_str = df["vervoerder"].astype("string").str.lower()
    df["vervoerder_type"] = (
        vervoerder_str.str.startswith("postnl_")
        .map({True: "eigen", False: "charter"})
        .fillna("onbekend")
    )

    df = df.sort_values(["wagencode", "trip_date", "trip_id", "stop_seq"]).reset_index(
        drop=True
    )
    return df


def filter_stops(
    df: pd.DataFrame,
    vervoerders: list[str] | None = None,
    vervoerder_types: list[str] | None = None,
    wagencodes: list[str] | None = None,
    date_range: tuple[pd.Timestamp, pd.Timestamp] | None = None,
    min_dwell_min: float = 0,
    exclude_admin: bool = True,
) -> pd.DataFrame:
    """Apply user-selected filters to the stops DataFrame."""
    out = df
    if vervoerder_types:
        out = out[out["vervoerder_type"].isin(vervoerder_types)]
    if vervoerders:
        out = out[out["vervoerder"].isin(vervoerders)]
    if wagencodes:
        out = out[out["wagencode"].astype(str).isin([str(w) for w in wagencodes])]
    if date_range:
        start, end = date_range
        out = out[(out["trip_date"] >= start) & (out["trip_date"] <= end)]
    if min_dwell_min > 0:
        out = out[out["dwell_min"] >= min_dwell_min]
    if exclude_admin:
        out = out[~out["acties"].fillna("").str.contains("Administrative", case=False)]
    return out.reset_index(drop=True)

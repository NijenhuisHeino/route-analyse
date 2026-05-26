"""Truck route heatmap — Streamlit app voor route- en laadlocatie-analyse."""

from __future__ import annotations

import hashlib
import os
import subprocess
import tempfile
from pathlib import Path

import folium
import pandas as pd
import streamlit as st
from dotenv import load_dotenv
from folium.plugins import HeatMap, MarkerCluster
from streamlit_folium import st_folium

from src.chargers import add_nearest_charger_distance
from src.hdv_chargers import load_hdv_chargers
from src.data_loader import (
    UnsupportedSchema,
    detect_schema,
    filter_stops,
    list_excel_files,
    load_trips,
)
from src.geocoder import reverse_geocode
from src.csv_loader import list_monthly_csvs, load_monthly_csvs
from src.simulation import (
    analyze_charging_gaps,
    charge_hotspots,
    simulate_soc,
)
from src.summary_loader import load_trip_summaries
from src.zez import annotate_stops_with_zez, load_zez_pc6
from src.hotspots import rank_hotspots
from src.road_usage import (
    compass_direction,
    compute_corridors,
    compute_road_heatmap_points,
    compute_weighted_edges,
    lerp_hex,
    merge_identical_chains,
    top_road_hotspots,
)
from src.routing import (
    fetch_routes,
    load_cached_routes,
    trip_polyline,
    unique_segments,
)

load_dotenv()

# Streamlit Cloud zet secrets in st.secrets; bridge naar env zodat bestaande
# os.getenv-lookups in src/ modules ook werken.
try:
    for _k in ("OCM_API_KEY", "DATA_DIR"):
        if _k in st.secrets and not os.getenv(_k):
            os.environ[_k] = str(st.secrets[_k])
except Exception:
    pass

DEFAULT_DATA_DIR = (
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/Data analyse ritten"
)
DEFAULT_CSV_DIR = (
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/Data analyse ritten/"
    "Rittendata per maand /Rittendata"
)

st.set_page_config(
    page_title="Rittenkaart — eTruck Academy",
    page_icon="🚚",
    layout="wide",
)


ETA_CSS = """
<style>
@import url('https://fonts.googleapis.com/css2?family=Titillium+Web:wght@400;600;700&family=Montserrat:wght@400;500;600;700&display=swap');

:root {
  --eta-purple-900: #2e2343;
  --eta-yellow-500: #f9bc13;
  --eta-orange-500: #f6a119;
  --eta-bg-page: #f7f7f9;
  --eta-bg-card: #ffffff;
  --eta-bg-soft: #f4f4f6;
  --eta-border-soft: #dedee6;
  --eta-border-strong: #c9c8d3;
  --eta-text-main: #1a1a1f;
  --eta-text-subtle: #5a5764;
  --eta-text-muted: #7b7886;
  --eta-shadow-card: 0 8px 24px rgba(46, 35, 67, 0.08);
  --eta-shadow-soft: 0 4px 12px rgba(46, 35, 67, 0.05);
}

html, body, [class*="css"], .stMarkdown, .stText,
div[data-testid="stSidebar"], div[data-testid="stAppViewContainer"] {
  font-family: 'Montserrat', Arial, sans-serif;
  color: var(--eta-text-main);
}

h1, h2, h3, h4 {
  font-family: 'Titillium Web', 'Montserrat', Arial, sans-serif !important;
  color: var(--eta-purple-900) !important;
  font-weight: 700 !important;
  letter-spacing: -0.01em;
}
h2 { font-size: 22px !important; }
h3 { font-size: 18px !important; }

.block-container { padding-top: 1.2rem; max-width: 1180px; }

.eta-hero {
  background: linear-gradient(135deg, #2e2343 0%, #3a2d55 100%);
  color: #ffffff;
  padding: 32px 36px;
  border-radius: 24px;
  margin-bottom: 24px;
  box-shadow: var(--eta-shadow-card);
}
.eta-hero .eyebrow {
  color: #f9bc13;
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 1.2px;
  font-weight: 600;
  margin-bottom: 10px;
}
.eta-hero h1 {
  color: #ffffff !important;
  font-size: 32px !important;
  margin: 0 0 10px 0 !important;
  line-height: 1.2;
}
.eta-hero p {
  color: rgba(255,255,255,0.88);
  margin: 0;
  font-size: 16px;
  line-height: 1.5;
  max-width: 760px;
}

/* KPI cards */
[data-testid="stMetric"] {
  background: var(--eta-bg-card);
  padding: 18px 22px;
  border-radius: 18px;
  border: 1px solid var(--eta-border-soft);
  box-shadow: var(--eta-shadow-soft);
}
[data-testid="stMetricLabel"] {
  color: var(--eta-text-subtle) !important;
  font-size: 13px !important;
  font-weight: 500 !important;
}
[data-testid="stMetricValue"] {
  color: var(--eta-purple-900) !important;
  font-weight: 700 !important;
  font-family: 'Titillium Web', 'Montserrat', sans-serif !important;
}

/* Tabs als knoppen */
.stTabs [data-baseweb="tab-list"] {
  gap: 8px;
  border-bottom: none;
  padding-bottom: 4px;
}
.stTabs [data-baseweb="tab"] {
  font-weight: 600;
  color: var(--eta-text-subtle);
  font-size: 15px;
  background: var(--eta-bg-card);
  border: 1px solid var(--eta-border-soft);
  border-radius: 12px !important;
  padding: 10px 22px !important;
  height: auto !important;
  transition: all .15s ease;
}
.stTabs [data-baseweb="tab"]:hover {
  background: var(--eta-bg-soft);
  border-color: var(--eta-border-strong);
  color: var(--eta-purple-900);
}
.stTabs [aria-selected="true"] {
  color: #ffffff !important;
  background: var(--eta-purple-900) !important;
  border-color: var(--eta-purple-900) !important;
  box-shadow: 0 4px 12px rgba(46,35,67,0.15);
}
.stTabs [aria-selected="true"]:hover {
  background: var(--eta-purple-900) !important;
  color: #ffffff !important;
}
.stTabs [aria-selected="true"] > div:first-child {
  border-bottom: none !important;
}
/* Verberg de blauwe Streamlit-default underline onder tabs */
.stTabs [data-baseweb="tab-highlight"] {
  display: none !important;
}
.stTabs [data-baseweb="tab-border"] {
  display: none !important;
}

/* Buttons — primaire CTA in geel */
.stButton > button,
.stDownloadButton > button {
  background: var(--eta-yellow-500) !important;
  color: var(--eta-purple-900) !important;
  border: none !important;
  border-radius: 16px !important;
  font-weight: 600 !important;
  min-height: 44px;
  padding: 0 20px !important;
  transition: background .15s ease, box-shadow .15s ease;
}
.stButton > button:hover,
.stDownloadButton > button:hover {
  background: #f2b400 !important;
  box-shadow: 0 8px 18px rgba(46,35,67,0.10) !important;
}

/* Inputs — rustiger borders */
div[data-baseweb="input"] input,
div[data-baseweb="select"] > div,
.stTextInput input, .stNumberInput input {
  border-radius: 12px !important;
}

/* Dataframes */
[data-testid="stDataFrame"] {
  border-radius: 14px;
  border: 1px solid var(--eta-border-soft);
  overflow: hidden;
}

/* Alerts */
div[data-baseweb="notification"],
.stAlert {
  border-radius: 14px !important;
}

/* Sidebar */
div[data-testid="stSidebar"] {
  background: var(--eta-bg-soft);
  border-right: 1px solid var(--eta-border-soft);
}
div[data-testid="stSidebar"] h1,
div[data-testid="stSidebar"] h2,
div[data-testid="stSidebar"] h3 {
  font-size: 16px !important;
}

/* Captions & help text */
.stCaption, [data-testid="stCaptionContainer"] {
  color: var(--eta-text-muted) !important;
  font-size: 13px !important;
}

/* Rustige subtitels i.p.v. streamlit-groot */
.stSubheader > div > h3 { font-size: 18px !important; }
</style>
"""


ETA_HERO = """
<div class="eta-hero">
  <div class="eyebrow">Route-analyse · Laadinfra</div>
  <h1>Rittenkaart — hotspots voor externe laadinfrastructuur</h1>
  <p>Analyseer vrachtwagenroutes en stop-locaties om de drukst bereden corridors en kandidaat-locaties voor publieke laadinfrastructuur te vinden. Ondersteunt eigen vervoer en charters.</p>
</div>
"""


def _pick_directory_finder(initial: str = "") -> str | None:
    """Open native macOS Finder folder picker. Returns path or None if cancelled."""
    initial_clause = ""
    if initial and Path(initial).is_dir():
        initial_clause = f' default location POSIX file "{initial}"'
    script = f'POSIX path of (choose folder with prompt "Kies data-map"{initial_clause})'
    try:
        result = subprocess.run(
            ["osascript", "-e", script],
            capture_output=True,
            text=True,
            timeout=300,
        )
    except Exception:
        return None
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


@st.cache_data(show_spinner=False)
def _detect_schema_cached(path: str, mtime: float) -> tuple[str, str] | None:
    """Cache schema-detection per file (mtime invalidates cache on file change)."""
    try:
        return detect_schema(Path(path))
    except UnsupportedSchema:
        return None


@st.cache_data(show_spinner=False)
def _load_cached_weighted_edges_df(variant: str) -> pd.DataFrame | None:
    """Laad pre-computed weighted edges per variant als DataFrame.
    Voegt fallback n_passes-kolom toe als parquet nog van oude schema is."""
    p = Path(f".cache/agg_weighted_edges_{variant}.parquet")
    if not p.exists():
        return None
    df = pd.read_parquet(p)
    if "n_passes" not in df.columns:
        df["n_passes"] = df["n_wagens"]  # fallback voor oude parquet-schema
    return df


@st.cache_data(show_spinner=False)
def _load_cached_road_heatmap(variant: str) -> list | None:
    """Laad pre-computed road heatmap points per variant."""
    p = Path(f".cache/agg_road_heatmap_{variant}.parquet")
    if not p.exists():
        return None
    hdf = pd.read_parquet(p)
    return [(float(r.lat), float(r.lon), float(r.weight)) for r in hdf.itertuples(index=False)]


def _detect_cache_variant(stops: pd.DataFrame, df: pd.DataFrame) -> str | None:
    """Detect which pre-compute variant matches the current filtered stops.

    Returns 'full', 'eigen', 'charter' or None (= must compute live).
    """
    if len(stops) == len(df):
        return "full"
    types_in_stops = set(stops["vervoerder_type"].unique())
    if types_in_stops == {"eigen"} and len(stops) == (df["vervoerder_type"] == "eigen").sum():
        return "eigen"
    if types_in_stops == {"charter"} and len(stops) == (df["vervoerder_type"] == "charter").sum():
        return "charter"
    return None


@st.cache_data(show_spinner="Excel inlezen...")
def _load_trip_stops(path: str) -> pd.DataFrame:
    return load_trips(Path(path))


@st.cache_data(show_spinner="Samenvattingsdata inlezen + steden geocoderen...")
def _load_trip_summaries(path: str) -> pd.DataFrame:
    return load_trip_summaries(Path(path))


@st.cache_data(show_spinner="Maand-CSV's inlezen + adressen geocoderen...")
def _load_csv_monthly(directory: str) -> pd.DataFrame:
    return load_monthly_csvs(Path(directory))


@st.cache_data(show_spinner="Geverifieerde HDV-laadlocaties laden...")
def _load_chargers(min_power_kw: float) -> pd.DataFrame:
    return load_hdv_chargers(min_power_kw=min_power_kw)


@st.cache_data(show_spinner="ZE-zones aan stops koppelen...")
def _annotate_zez(df: pd.DataFrame) -> pd.DataFrame:
    try:
        zez_df = load_zez_pc6()
        return annotate_stops_with_zez(df, zez_df)
    except Exception:
        out = df.copy()
        out["pc6"] = None
        out["ze_zone"] = ""
        out["ze_startdatum"] = ""
        out["in_zez"] = False
        return out


def _persist_upload(uploaded_file) -> Path:
    """Save uploaded bytes to a deterministic tempfile path for cache reuse."""
    data = uploaded_file.getbuffer()
    h = hashlib.sha1(data).hexdigest()[:12]
    dst = Path(tempfile.gettempdir()) / f"postnl-upload-{h}.xlsx"
    if not dst.exists():
        dst.write_bytes(data)
    return dst


def _render_simulation(stops: pd.DataFrame) -> None:
    """Verbruiks-/SoC-simulatie tab: parameters, resultaten, charge-hotspots."""
    st.subheader("eTruck verbruiks- en laadsimulatie")
    st.caption(
        "Indicatieve simulatie: per trip wordt SoC berekend op basis van "
        "haversine-afstand × verbruik. Waar de SoC onder de drempel komt, "
        "wordt een laad-event geplaatst. Aggregeert tot kandidaat-laadlocaties."
    )

    c1, c2, c3 = st.columns(3)
    with c1:
        kwh_per_km = st.number_input(
            "Verbruik (kWh/km)",
            min_value=0.5,
            max_value=3.0,
            value=1.2,
            step=0.1,
            help="Typisch voor zware eTruck: 1.0-1.6 kWh/km afhankelijk van belading.",
        )
        capacity = st.number_input(
            "Batterij netto (kWh)",
            min_value=100,
            max_value=1500,
            value=590,
            step=10,
            help="Bruikbare capaciteit (na buffers van fabrikant).",
        )
    with c2:
        start_soc = st.slider(
            "Start SoC (%)", min_value=50, max_value=100, value=100, step=5
        )
        soc_threshold = st.slider(
            "Min. SoC drempel (%)",
            min_value=5,
            max_value=30,
            value=15,
            step=1,
            help="Onder deze drempel triggert een laad-event.",
        )
    with c3:
        max_kw = st.number_input(
            "Max laadvermogen (kW)",
            min_value=50,
            max_value=1000,
            value=350,
            step=50,
            help="Bepaalt laadtijd: kWh ÷ kW × 60 = minuten.",
        )
        st.write("")
        st.write("")
        st.caption("Aanname: bij laden wordt opgeladen tot 100%.")

    if stops.empty:
        st.info("Geen stops om te simuleren — pas filters aan.")
        return

    with st.spinner("SoC-traject berekenen..."):
        sim = simulate_soc(
            stops,
            kwh_per_km=kwh_per_km,
            capacity_kwh=capacity,
            start_soc_pct=start_soc,
            threshold_pct=soc_threshold,
            max_charge_kw=max_kw,
        )

    n_events = int(sim["charge_event"].sum())
    n_trips = sim["trip_id"].nunique()
    n_trips_charging = sim.loc[sim["charge_event"], "trip_id"].nunique()
    total_kwh = float(sim["charge_kwh"].sum())
    total_min = float(sim["charge_min"].sum())

    st.divider()
    k1, k2, k3, k4 = st.columns(4)
    k1.metric("Trips", f"{n_trips:,}".replace(",", "."))
    k2.metric(
        "Trips met laad-event",
        f"{n_trips_charging:,}".replace(",", "."),
        f"{(100 * n_trips_charging / max(n_trips, 1)):.0f}% van totaal",
    )
    k3.metric("Laad-events totaal", f"{n_events:,}".replace(",", "."))
    k4.metric(
        "Geschatte laadtijd totaal",
        f"{total_min / 60:,.0f} uur".replace(",", "."),
        f"{total_kwh:,.0f} kWh".replace(",", "."),
    )

    st.divider()

    col_left, col_right = st.columns([1, 1])

    with col_left:
        st.subheader("Top-50 kandidaat-laadlocaties")
        st.caption(
            "Locaties waar trucks volgens deze simulatie het vaakst zouden moeten laden."
        )
        hotspots = charge_hotspots(sim, top_n=50)
        if hotspots.empty:
            st.info("Geen laad-events bij deze parameters.")
        else:
            hotspots_view = hotspots.copy()
            hotspots_view["maps"] = hotspots_view.apply(
                lambda r: f"https://www.google.com/maps?q={r['lat']},{r['lon']}",
                axis=1,
            )
            st.dataframe(
                hotspots_view[
                    [
                        "adres",
                        "n_events",
                        "n_unieke_wagens",
                        "totaal_kwh",
                        "gem_min",
                        "maps",
                    ]
                ],
                use_container_width=True,
                hide_index=True,
                column_config={
                    "maps": st.column_config.LinkColumn(
                        "Kaart", display_text="📍 Open"
                    ),
                },
            )
            st.download_button(
                "Download kandidaat-laadlocaties als CSV",
                data=hotspots.to_csv(index=False).encode("utf-8"),
                file_name="kandidaat_laadlocaties.csv",
                mime="text/csv",
                key="dl_charge_hotspots",
            )

    with col_right:
        st.subheader("Per-trip overzicht")
        per_trip = (
            sim.groupby("trip_id")
            .agg(
                stops=("stop_seq", "size"),
                trip_km=("segment_km", "sum"),
                eind_soc_pct=("soc_pct_aankomst", "last"),
                laad_events=("charge_event", "sum"),
                laad_kwh=("charge_kwh", "sum"),
                laad_min=("charge_min", "sum"),
            )
            .reset_index()
        )
        per_trip["trip_km"] = per_trip["trip_km"].round(1)
        per_trip["laad_kwh"] = per_trip["laad_kwh"].round(0)
        per_trip["laad_min"] = per_trip["laad_min"].round(0)
        per_trip = per_trip.sort_values("laad_events", ascending=False).head(100)
        st.dataframe(
            per_trip,
            use_container_width=True,
            hide_index=True,
        )
        st.caption(
            f"Top 100 trips gerangschikt op aantal laad-events. "
            f"Totaal {n_trips:,} trips gesimuleerd.".replace(",", ".")
        )

    st.divider()
    st.subheader("Laad-window analyse tussen shifts")
    st.caption(
        "Per truck: kijk per opeenvolgende trip-paar of de **rust-tijd "
        "tussen shifts** voldoende is om de batterij voor de volgende "
        "trip op te laden bij depot/thuis. Zo niet, dan moet er onderweg "
        "**publiek** worden geladen."
    )

    g1, g2, g3 = st.columns(3)
    with g1:
        gap_kwh_per_km = st.number_input(
            "Verbruik (kWh/km)",
            min_value=0.5,
            max_value=3.0,
            value=1.19,
            step=0.01,
            key="gap_kwh_per_km",
            help="Default 1.19 kWh/km (gemiddelde voor zware eTruck).",
        )
    with g2:
        gap_charge_kw = st.number_input(
            "Laadvermogen depot/thuis (kW)",
            min_value=22,
            max_value=600,
            value=150,
            step=22,
            key="gap_charge_kw",
            help="Maximaal vermogen dat op het depot beschikbaar is.",
        )
    with g3:
        target_soc_pct = st.slider(
            "Target SoC vóór trip (%)",
            min_value=80,
            max_value=100,
            value=100,
            step=5,
            key="gap_target_soc",
            help=(
                "SoC waarmee de truck wil starten. Bij <100% rekent de "
                "analyse minder benodigde lading."
            ),
        )

    if stops.empty:
        st.info("Geen trips beschikbaar.")
        return

    with st.spinner("Laad-windows berekenen..."):
        gap_df = analyze_charging_gaps(
            stops,
            kwh_per_km=gap_kwh_per_km * (target_soc_pct / 100.0),
            max_charge_kw=gap_charge_kw,
        )

    if gap_df.empty:
        st.info("Geen trip-overgangen gevonden in de selectie.")
        return

    n_pairs = len(gap_df)
    n_thuis = int(gap_df["thuis_voldoende"].sum())
    n_publiek = n_pairs - n_thuis
    pct_publiek = 100 * n_publiek / max(n_pairs, 1)
    avg_gap = float(gap_df["gap_hours"].mean())
    avg_tekort = float(gap_df.loc[~gap_df["thuis_voldoende"], "tekort_h"].mean()) \
        if n_publiek else 0.0

    m1, m2, m3, m4 = st.columns(4)
    m1.metric(
        "Trip-overgangen",
        f"{n_pairs:,}".replace(",", "."),
    )
    m2.metric(
        "Thuis voldoende",
        f"{n_thuis:,}".replace(",", "."),
        f"{100 - pct_publiek:.0f}%",
    )
    m3.metric(
        "Publiek nodig",
        f"{n_publiek:,}".replace(",", "."),
        f"{pct_publiek:.0f}%",
    )
    m4.metric("Gem. rust-tijd", f"{avg_gap:.1f} u")

    if n_publiek:
        st.warning(
            f"⚠️ {n_publiek:,} van de {n_pairs:,} trip-overgangen hebben "
            f"**te weinig rust-tijd** voor volledige depot-lading "
            f"(gem. tekort {avg_tekort:.1f} u). Deze trucks moeten onderweg "
            "publiek laden of het laad-vermogen op het depot moet omhoog."
            .replace(",", ".")
        )

    cd1, cd2 = st.columns([1, 1])
    with cd1:
        st.subheader("Verdeling rust-tijd")
        try:
            import altair as alt

            hist = (
                alt.Chart(
                    gap_df.assign(
                        bucket=pd.cut(
                            gap_df["gap_hours"],
                            bins=[0, 2, 4, 6, 8, 10, 12, 16, 24, 48, 999],
                            labels=[
                                "<2u",
                                "2-4u",
                                "4-6u",
                                "6-8u",
                                "8-10u",
                                "10-12u",
                                "12-16u",
                                "16-24u",
                                "24-48u",
                                ">48u",
                            ],
                            include_lowest=True,
                        ).astype(str)
                    )
                )
                .mark_bar(color="#4c1d95")
                .encode(
                    x=alt.X("bucket:N", title="Rust-tijd"),
                    y=alt.Y("count():Q", title="Aantal overgangen"),
                    tooltip=[alt.Tooltip("count():Q", title="Aantal")],
                )
                .properties(height=240)
            )
            st.altair_chart(hist, use_container_width=True)
        except Exception as e:
            st.caption(f"Histogram niet beschikbaar: {e}")

    with cd2:
        st.subheader("Top 20 trucks met grootste tekort")
        per_wagen = (
            gap_df[~gap_df["thuis_voldoende"]]
            .groupby("wagencode")
            .agg(
                tekort_uren=("tekort_h", "sum"),
                aantal_keer=("tekort_h", "size"),
                gem_tekort_h=("tekort_h", "mean"),
            )
            .reset_index()
            .nlargest(20, "tekort_uren")
        )
        per_wagen["tekort_uren"] = per_wagen["tekort_uren"].round(1)
        per_wagen["gem_tekort_h"] = per_wagen["gem_tekort_h"].round(1)
        if per_wagen.empty:
            st.info("Geen trucks met tekort.")
        else:
            st.dataframe(per_wagen, use_container_width=True, hide_index=True)

    st.divider()
    st.subheader("Detail per trip-overgang (top 100 grootste tekort)")
    detail = (
        gap_df[~gap_df["thuis_voldoende"]]
        .nlargest(100, "tekort_h")
        .copy()
    )
    if detail.empty:
        st.caption("Geen overgangen met tekort.")
    else:
        detail["gap_hours"] = detail["gap_hours"].round(2)
        detail["energy_kwh"] = detail["energy_kwh"].round(0)
        detail["charge_time_h"] = detail["charge_time_h"].round(2)
        detail["tekort_h"] = detail["tekort_h"].round(2)
        detail["next_trip_km"] = detail["next_trip_km"].round(0)
        st.dataframe(
            detail[
                [
                    "wagencode",
                    "trip_id",
                    "trip_end",
                    "next_trip_id",
                    "next_trip_start",
                    "gap_hours",
                    "next_trip_km",
                    "energy_kwh",
                    "charge_time_h",
                    "tekort_h",
                ]
            ],
            use_container_width=True,
            hide_index=True,
        )
        st.download_button(
            "Download volledige laad-window-analyse als CSV",
            data=gap_df.to_csv(index=False).encode("utf-8"),
            file_name="laad_window_analyse.csv",
            mime="text/csv",
        )


def _build_excel_export(
    filters_info: dict,
    stops_df: pd.DataFrame,
    corridors_df: pd.DataFrame,
    edges_df: pd.DataFrame | None,
) -> bytes:
    """Build multi-sheet Excel met filters + top-tabellen voor klant-rapportage."""
    import io

    buf = io.BytesIO()
    with pd.ExcelWriter(buf, engine="openpyxl") as writer:
        pd.DataFrame(
            [{"parameter": k, "waarde": v} for k, v in filters_info.items()]
        ).to_excel(writer, sheet_name="Filters", index=False)
        if not stops_df.empty:
            stops_df.to_excel(writer, sheet_name="Top stop-locaties", index=False)
        if not corridors_df.empty:
            corridors_df.to_excel(writer, sheet_name="Top corridors", index=False)
        if edges_df is not None and not edges_df.empty:
            edges_df.to_excel(writer, sheet_name="Top wegvlakken", index=False)
    buf.seek(0)
    return buf.read()


_HOTSPOTS_COLUMN_LABELS = {
    "lat_round": "Lat (~110 m raster)",
    "lon_round": "Lon (~110 m raster)",
    "locatie_naam": "Locatienaam",
    "adres": "Adres",
    "n_stops": "Keer bezocht",
    "n_wagens": "Unieke wagens",
    "n_trips": "Unieke trips",
    "totale_standtijd_uur": "Totale rijduur (uur)",
    "gem_standtijd_min": "Gem. rijduur (min)",
    "afstand_lader_km": "Afstand naar lader (km)",
    "maps": "Kaart",
}


def _render_dashboard(
    stops: pd.DataFrame,
    chargers_df: pd.DataFrame,
    road_threshold: int,
    min_chain_length_m: int = 0,
) -> None:
    """Dashboard-tab: KPI's, top stops, top corridors, top wegvlakken, trends."""
    st.subheader("Kern-KPI's")
    total_km = float(stops["afstand_km"].fillna(0).sum())
    avg_trip_km = (
        stops.groupby("trip_id")["afstand_km"].sum().mean() if "afstand_km" in stops else 0
    )
    eigen_km = float(
        stops.loc[stops["vervoerder_type"] == "eigen", "afstand_km"].fillna(0).sum()
    )
    charter_km = float(
        stops.loc[stops["vervoerder_type"] == "charter", "afstand_km"].fillna(0).sum()
    )
    eigen_pct = 100 * eigen_km / total_km if total_km else 0
    charter_pct = 100 * charter_km / total_km if total_km else 0

    k1, k2, k3, k4 = st.columns(4)
    k1.metric("Totale km", f"{total_km:,.0f}".replace(",", "."))
    k2.metric("Gem. trip (km)", f"{avg_trip_km:,.1f}".replace(",", "."))
    k3.metric("Eigen vervoer", f"{eigen_pct:.0f}% km")
    k4.metric("Charter", f"{charter_pct:.0f}% km")

    st.divider()

    # === Top stop-locaties ===
    sort_by_label = st.radio(
        "Sorteren op",
        options=["Aantal stops", "Aantal unieke wagens"],
        index=0,
        horizontal=True,
        key="dash_sort_stops",
    )
    sort_col = "n_stops" if sort_by_label == "Aantal stops" else "n_wagens"
    st.subheader(f"Top 50 stop-locaties (op {sort_by_label.lower()})")
    st.caption(
        "Geclusterd per ~110 m grid-cel — **Lat / Lon** zijn breedte- en "
        "lengtegraad van het centrum van die cel.  \n"
        "**Keer bezocht** = aantal stops totaal · "
        "**Unieke wagens** = aantal verschillende vrachtwagens · "
        "**Unieke trips** = aantal verschillende ritten.  \n"
        "**Totale rijduur (uur)** = som van alle reistijden naar deze locatie "
        "(één hoge waarde betekent: vaak bezocht én/of vanuit verre afstand). "
        "**Gem. rijduur (min)** = gemiddelde reistijd per bezoek "
        "(hoog = trucks rijden lang om hier te komen — interessant voor laadinfra "
        "want accu komt hier vaak leeg aan)."
    )
    hotspots = rank_hotspots(stops)
    if not chargers_df.empty:
        hotspots = add_nearest_charger_distance(hotspots, chargers_df)
    hotspots = hotspots.sort_values(
        [sort_col, "totale_standtijd_uur"], ascending=[False, False]
    ).reset_index(drop=True)
    top_stops = hotspots.head(50).copy().reset_index(drop=True)
    top_stops.insert(0, "#", top_stops.index + 1)
    top_stops["maps"] = top_stops.apply(
        lambda r: f"https://www.google.com/maps?q={r['lat_round']},{r['lon_round']}",
        axis=1,
    )

    if not top_stops.empty:
        mini_stops = folium.Map(
            location=[52.1, 5.3], zoom_start=7, tiles="OpenStreetMap"
        )
        max_wagens_top = int(top_stops["n_wagens"].max())
        for rank, row in enumerate(top_stops.itertuples(index=False), start=1):
            radius = 6 + 16 * (int(row.n_wagens) / max(max_wagens_top, 1))
            popup_html = (
                f"<b>#{rank} — {row.locatie_naam or '(onbekend)'}</b><br>"
                f"{row.adres or ''}<br>"
                f"🚛 <b>{int(row.n_wagens)} unieke wagens</b><br>"
                f"📍 {int(row.n_stops):,} keer bezocht · {int(row.n_trips):,} unieke trips<br>"
                f"⏱️ Totale rijduur: {row.totale_standtijd_uur:,.0f} uur · "
                f"gemiddeld {row.gem_standtijd_min:.0f} min/bezoek"
            ).replace(",", ".")
            folium.CircleMarker(
                location=[row.lat_round, row.lon_round],
                radius=radius,
                color="#dc2626",
                weight=1.5,
                fill=True,
                fill_color="#dc2626",
                fill_opacity=0.55,
                popup=folium.Popup(popup_html, max_width=320),
                tooltip=f"#{rank} — {int(row.n_wagens)} wagens, {int(row.n_stops)} bezoeken",
            ).add_to(mini_stops)
        st_folium(
            mini_stops, height=420, use_container_width=True, returned_objects=[]
        )
        st.caption(
            "Rode bollen = top 50 stop-locaties. Bolgrootte ∝ aantal unieke "
            "wagens. **Klik op een bol** voor adres + statistieken. "
            "Zo zie je in één oogopslag de geografische verdeling (oost/noord/west)."
        )

    st.dataframe(
        top_stops,
        use_container_width=True,
        hide_index=True,
        column_config={
            **{
                col: st.column_config.Column(label)
                for col, label in _HOTSPOTS_COLUMN_LABELS.items()
                if col != "maps"
            },
            "maps": st.column_config.LinkColumn(
                "Kaart", display_text="📍 Open"
            ),
        },
    )
    st.download_button(
        "Download top stop-locaties als CSV",
        data=hotspots.to_csv(index=False).encode("utf-8"),
        file_name="top_stop_locaties.csv",
        mime="text/csv",
        key="dl_hotspots",
    )

    st.divider()

    # === Top corridors ===
    st.subheader(f"Top 20 corridors (≥ {road_threshold} wagens, gesorteerd op lengte)")
    st.caption(
        "Aaneengesloten wegvlakken boven de drempel worden samengevoegd tot "
        "één corridor. Drempel aanpassen via sidebar 'Min. aantal unieke wagens'."
    )
    segs = unique_segments(stops)
    cached_routes, missing = load_cached_routes(segs)
    corridors_df_export = pd.DataFrame()
    edges_df_export: pd.DataFrame | None = None
    if missing > 0 and not cached_routes:
        st.info(
            f"Wegvlak-data nog niet gecached ({missing}/{len(segs)} segmenten). "
            "Zet op de Kaart-tab eenmalig **Routelijnen → volg wegennet** aan."
        )
    else:
        edges = compute_weighted_edges(stops, cached_routes)
        if missing:
            st.caption(
                f"⚠️ {missing}/{len(segs)} segmenten ontbreken in cache — "
                f"cijfers zijn op basis van {len(segs) - missing} gecachede segmenten."
            )
        corridors = compute_corridors(edges, threshold=road_threshold)
        top_corridors = corridors[:20]
        if not top_corridors:
            st.warning(
                f"Geen corridors met ≥ {road_threshold} wagens. Zet de drempel lager."
            )
        else:
            centers = [c["center"] for c in top_corridors]
            with st.spinner("Wegnamen opzoeken (gecached)..."):
                names = reverse_geocode(centers)

            mini = folium.Map(
                location=[52.1, 5.3], zoom_start=7, tiles="OpenStreetMap"
            )
            rows = []
            for rank, (corridor, (lat_c, lon_c)) in enumerate(
                zip(top_corridors, centers), start=1
            ):
                info = names.get(
                    (round(lat_c, 4), round(lon_c, 4)),
                    {"road": "", "town": "", "display": ""},
                )
                weg = info["road"] or "(onbekend)"
                plaats = info["town"] or ""
                edges_for_mini = corridor["edges"]
                if len(edges_for_mini) > 30:
                    step = max(1, len(edges_for_mini) // 30)
                    edges_for_mini = edges_for_mini[::step][:30]
                for edge_tuple in edges_for_mini:
                    p1, p2 = edge_tuple[0], edge_tuple[1]
                    folium.PolyLine(
                        [p1, p2], color="#dc2626", weight=5, opacity=0.85
                    ).add_to(mini)
                folium.Marker(
                    location=[lat_c, lon_c],
                    icon=folium.DivIcon(
                        html=(
                            f'<div style="background:#dc2626;color:white;'
                            f"border:2px solid white;border-radius:50%;"
                            f"width:26px;height:26px;text-align:center;"
                            f"line-height:22px;font-weight:bold;"
                            f'font-size:12px;">{rank}</div>'
                        )
                    ),
                    tooltip=(
                        f"#{rank} — {weg}, {plaats} · "
                        f"{corridor['length_km']:.1f} km · "
                        f"max {corridor['max_n']} wagens"
                    ),
                ).add_to(mini)
                rows.append(
                    {
                        "#": rank,
                        "wegnaam": weg,
                        "plaats": plaats,
                        "med_passages": int(corridor["median_passes"]),
                        "max_passages": int(corridor["max_passes"]),
                        "max_wagens": int(corridor["max_n"]),
                        "med_wagens": int(corridor["median_n"]),
                        "spreiding_km": round(corridor["spreiding_km"], 1),
                        "totale_km": round(corridor["length_km"], 1),
                        "n_wegvlakken": corridor["n_edges"],
                        "lat": round(lat_c, 5),
                        "lon": round(lon_c, 5),
                        "maps": f"https://www.google.com/maps?q={lat_c},{lon_c}",
                    }
                )
            st_folium(
                mini, height=320, use_container_width=True, returned_objects=[]
            )
            st.caption(
                "**Sortering:** mediaan aantal passages per wegvlak (drukst eerst).  \n"
                "**Med./max passages** = aantal keer dat een truck dit wegvlak "
                "gebruikt heeft (gem./hoogste binnen de corridor).  \n"
                "**Med./max wagens** = aantal verschillende vrachtwagens.  \n"
                "**Spreiding (km)** = hemelsbrede afstand tussen verste hoeken "
                "(realistische 'lengte' van de corridor).  \n"
                "**Totale km** = som van alle micro-segmenten incl. zijwegen "
                "(bij een lage drempel verbindt het algoritme hele netwerken — "
                "dat is dan misleidend hoog)."
            )
            corridors_df_export = pd.DataFrame(rows)
            st.dataframe(
                corridors_df_export,
                use_container_width=True,
                hide_index=True,
                column_config={
                    "med_passages": st.column_config.Column(
                        "Med. passages", help="Mediaan keer-bereden per wegvlak"
                    ),
                    "max_passages": st.column_config.Column("Max passages"),
                    "max_wagens": st.column_config.Column("Max wagens"),
                    "med_wagens": st.column_config.Column("Med. wagens"),
                    "spreiding_km": st.column_config.NumberColumn(
                        "Spreiding (km)", format="%.1f"
                    ),
                    "totale_km": st.column_config.NumberColumn(
                        "Totale stukjes (km)", format="%.1f"
                    ),
                    "n_wegvlakken": st.column_config.Column("Wegvlakken"),
                    "maps": st.column_config.LinkColumn(
                        "Kaart", display_text="🛣️ Open"
                    ),
                },
            )
            st.download_button(
                "Download top corridors als CSV",
                data=corridors_df_export.to_csv(index=False).encode("utf-8"),
                file_name="top_corridors.csv",
                mime="text/csv",
                key="dl_corridors",
            )

        # Top wegvlakken — micro-edges met identieke metrics zijn samengevoegd
        st.divider()
        st.subheader("Top 100 drukste wegvlakken")
        st.caption(
            "Een wegvlak hier = aaneengesloten stukje weg waarop alle micro-segmenten "
            "dezelfde gebruiksstatistiek hebben (OSRM splitst een doorgaand wegstuk "
            "in vele 50-200 m vertices; die zijn samengevoegd).  \n"
            "**Unieke wagens** = verschillende vrachtwagens · **Keer bereden** = "
            "totaal aantal passages (één wagen kan vaker).  \n"
            "Veel wagens × weinig passages = brede gebruikersgroep · "
            "weinig wagens × veel passages = vaste route van enkele wagens."
        )
        # Filter alleen edges die door de drempel komen, anders te veel chains
        busy_edges = [e for e in edges if e[2] >= road_threshold]
        chains = merge_identical_chains(busy_edges)
        n_total_chains = len(chains)
        if min_chain_length_m > 0:
            chains = [
                c for c in chains
                if c["length_km"] * 1000 >= min_chain_length_m
            ]
        chains_sorted = sorted(
            chains, key=lambda c: (c["n_passes"], c["n_wagens"]), reverse=True
        )[:100]
        if min_chain_length_m > 0:
            st.caption(
                f"Lengte-filter: ≥ {min_chain_length_m} m → "
                f"{len(chains):,} van {n_total_chains:,} wegvlakken behouden. "
                "Aanpassen in sidebar 'Min. wegvlak-lengte (Dashboard, m)'."
                .replace(",", ".")
            )

        endpoint_coords: list[tuple[float, float]] = []
        for c in chains_sorted:
            endpoint_coords.append((c["lat1"], c["lon1"]))
            endpoint_coords.append((c["lat2"], c["lon2"]))

        if endpoint_coords:
            with st.spinner(
                "Wegnamen + plaatsnamen voor wegvlakken opzoeken (gecached)..."
            ):
                rev_progress = st.progress(0.0)

                def _rcb(i: int, total: int) -> None:
                    rev_progress.progress(min(1.0, i / max(total, 1)))

                endpoint_names = reverse_geocode(endpoint_coords, progress_cb=_rcb)
                rev_progress.empty()
        else:
            endpoint_names = {}

        rows = []
        for i, c in enumerate(chains_sorted):
            start_key = (round(c["lat1"], 4), round(c["lon1"], 4))
            end_key = (round(c["lat2"], 4), round(c["lon2"], 4))
            start_info = endpoint_names.get(
                start_key, {"road": "", "town": "", "display": ""}
            )
            end_info = endpoint_names.get(
                end_key, {"road": "", "town": "", "display": ""}
            )
            wegnaam = start_info["road"] or end_info["road"] or "(onbekend)"
            van = start_info["town"] or ""
            naar = end_info["town"] or ""
            richting = compass_direction(c["lat1"], c["lon1"], c["lat2"], c["lon2"])
            rows.append(
                {
                    "#": i + 1,
                    "wegnaam": wegnaam,
                    "van": van,
                    "naar": naar,
                    "richting": richting,
                    "n_unieke_wagens": c["n_wagens"],
                    "n_keer_bereden": c["n_passes"],
                    "passages_per_wagen": round(
                        c["n_passes"] / max(c["n_wagens"], 1), 1
                    ),
                    "lengte_m": int(c["length_km"] * 1000),
                    "n_micro_edges": c["n_micro_edges"],
                    "lat_van": round(c["lat1"], 6),
                    "lon_van": round(c["lon1"], 6),
                    "lat_tot": round(c["lat2"], 6),
                    "lon_tot": round(c["lon2"], 6),
                    "maps": f"https://www.google.com/maps?q={(c['lat1'] + c['lat2']) / 2},{(c['lon1'] + c['lon2']) / 2}",
                }
            )
        edges_df_export = pd.DataFrame(rows)

        if chains_sorted:
            mini_edges = folium.Map(
                location=[52.1, 5.3], zoom_start=7, tiles="OpenStreetMap"
            )
            max_pas = max(c["n_passes"] for c in chains_sorted) or 1
            for rank, c in enumerate(chains_sorted, start=1):
                t = c["n_passes"] / max_pas
                weight = 3 + 6 * t
                folium.PolyLine(
                    [(c["lat1"], c["lon1"]), (c["lat2"], c["lon2"])],
                    color="#dc2626",
                    weight=weight,
                    opacity=0.85,
                    tooltip=(
                        f"#{rank} — {c['n_passes']:,} passages, "
                        f"{c['n_wagens']} unieke wagens"
                    ).replace(",", "."),
                ).add_to(mini_edges)
                lat_m = (c["lat1"] + c["lat2"]) / 2
                lon_m = (c["lon1"] + c["lon2"]) / 2
                folium.Marker(
                    location=[lat_m, lon_m],
                    icon=folium.DivIcon(
                        html=(
                            f'<div style="background:#dc2626;color:white;'
                            f"border:2px solid white;border-radius:50%;"
                            f"width:22px;height:22px;text-align:center;"
                            f"line-height:18px;font-weight:bold;"
                            f'font-size:11px;">{rank}</div>'
                        )
                    ),
                ).add_to(mini_edges)
            st_folium(
                mini_edges, height=420, use_container_width=True, returned_objects=[]
            )
            st.caption(
                "Rode lijnen = top 100 wegvlakken (lijndikte ∝ keer bereden). "
                "Genummerde markers tonen rang in de tabel. Hover voor details."
            )

        st.dataframe(
            edges_df_export,
            use_container_width=True,
            hide_index=True,
            column_config={
                "maps": st.column_config.LinkColumn(
                    "Kaart", display_text="🛣️ Open"
                ),
            },
        )
        st.download_button(
            "Download top wegvlakken als CSV",
            data=edges_df_export.to_csv(index=False).encode("utf-8"),
            file_name="top_wegvlakken.csv",
            mime="text/csv",
            key="dl_wegvlakken",
        )

    st.divider()

    # === Excel export — alles in één bestand ===
    st.subheader("Excel-export voor klant-rapportage")
    st.caption(
        "Bevat de huidige selectie als 4 tabbladen: Filters, Top stop-locaties, "
        "Top corridors, Top wegvlakken."
    )
    filters_info = {
        "Modus": stops.get("acties", pd.Series()).iloc[0]
        if not stops.empty and "acties" in stops.columns
        else "",
        "Totaal stops": f"{len(stops):,}".replace(",", "."),
        "Unieke trips": f"{stops['trip_id'].nunique():,}".replace(",", "."),
        "Unieke wagens": f"{stops['wagencode'].nunique():,}".replace(",", "."),
        "Datum vanaf": (
            str(stops["trip_date"].min().date()) if not stops.empty else ""
        ),
        "Datum tot": (
            str(stops["trip_date"].max().date()) if not stops.empty else ""
        ),
        "Min. wagens per wegvlak": road_threshold,
        "Vervoerder-types": ", ".join(
            sorted(stops["vervoerder_type"].dropna().unique().tolist())
        ),
    }
    excel_bytes = _build_excel_export(
        filters_info,
        top_stops,
        corridors_df_export,
        edges_df_export,
    )
    st.download_button(
        "Download volledig Excel-rapport",
        data=excel_bytes,
        file_name="postnl_route_analyse_export.xlsx",
        mime="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        key="dl_excel_full",
    )

    st.divider()

    col_a, col_b = st.columns([1, 1])
    with col_a:
        st.subheader("Ritten per dag")
        trips_per_day = (
            stops.dropna(subset=["trip_date"])
            .groupby(stops["trip_date"].dt.date)["trip_id"]
            .nunique()
            .rename("trips")
        )
        if not trips_per_day.empty:
            st.line_chart(trips_per_day, height=240)
        else:
            st.caption("Geen datumdata beschikbaar.")

    with col_b:
        st.subheader("Top vervoerders (unieke trips)")
        top_vervoerders = (
            stops.dropna(subset=["vervoerder"])
            .groupby("vervoerder")["trip_id"]
            .nunique()
            .sort_values(ascending=False)
            .head(10)
            .rename("trips")
        )
        if not top_vervoerders.empty:
            st.bar_chart(top_vervoerders, height=240)
        else:
            st.caption("Geen vervoerder-data beschikbaar.")

    if "in_zez" in stops.columns:
        st.divider()
        st.subheader("Zero-emission zone analyse")
        zez_stops = stops[stops["in_zez"]]
        n_in = len(zez_stops)
        pct = 100 * n_in / max(len(stops), 1)
        n_zones = zez_stops["ze_zone"].nunique() if not zez_stops.empty else 0
        n_trucks_in_zez = zez_stops["wagencode"].nunique() if not zez_stops.empty else 0
        z1, z2, z3 = st.columns(3)
        z1.metric(
            "Stops in ZE-zone",
            f"{n_in:,}".replace(",", "."),
            f"{pct:.1f}% van totaal",
        )
        z2.metric("Verschillende ZE-zones bezocht", n_zones)
        z3.metric(
            "Trucks die een ZE-zone bezoeken",
            f"{n_trucks_in_zez:,}".replace(",", "."),
        )

        if not zez_stops.empty:
            zez_summary = (
                zez_stops.groupby(["ze_zone", "ze_startdatum"])
                .agg(
                    n_stops=("trip_id", "size"),
                    n_unieke_trips=("trip_id", "nunique"),
                    n_unieke_wagens=("wagencode", "nunique"),
                )
                .reset_index()
                .sort_values("n_stops", ascending=False)
            )
            st.caption(
                "Per ZE-zone: hoeveel stops + welke startdatum. "
                "Locaties met vroege startdatum zijn prioriteit voor laadinfra."
            )
            st.dataframe(
                zez_summary,
                use_container_width=True,
                hide_index=True,
                column_config={
                    "ze_zone": st.column_config.Column("ZE-zone"),
                    "ze_startdatum": st.column_config.Column("Startdatum"),
                    "n_stops": st.column_config.Column("Stops"),
                    "n_unieke_trips": st.column_config.Column("Trips"),
                    "n_unieke_wagens": st.column_config.Column("Wagens"),
                },
            )
        else:
            st.info(
                "Geen stops in ZE-zone gevonden in de huidige selectie. "
                "Pas filters aan om bredere data te zien."
            )

    st.divider()
    st.subheader("Inzet-heatmap: dag × uur")
    st.caption(
        "Hoe vaak is een truck actief op welk uur van de week? "
        "Telling = aantal travel-rijen waarvan de starttijd op dat dag-uur "
        "valt. Donkerder = drukker. Geeft inzicht in shift-patronen en "
        "rust-windows tussen shifts (waar laden mogelijk is)."
    )
    if "gepland_start" in stops.columns and not stops["gepland_start"].isna().all():
        act = stops.dropna(subset=["gepland_start"]).copy()
        act["dag"] = act["gepland_start"].dt.day_name()
        act["uur"] = act["gepland_start"].dt.hour
        grid = (
            act.groupby(["dag", "uur"])
            .size()
            .reset_index(name="aantal")
        )
        day_order = [
            "Monday",
            "Tuesday",
            "Wednesday",
            "Thursday",
            "Friday",
            "Saturday",
            "Sunday",
        ]
        day_nl = {
            "Monday": "Ma",
            "Tuesday": "Di",
            "Wednesday": "Wo",
            "Thursday": "Do",
            "Friday": "Vr",
            "Saturday": "Za",
            "Sunday": "Zo",
        }
        grid["dag"] = grid["dag"].map(day_nl)
        try:
            import altair as alt

            chart = (
                alt.Chart(grid)
                .mark_rect()
                .encode(
                    x=alt.X("uur:O", title="Uur van de dag"),
                    y=alt.Y(
                        "dag:O",
                        title="Dag",
                        sort=[day_nl[d] for d in day_order],
                    ),
                    color=alt.Color(
                        "aantal:Q",
                        scale=alt.Scale(scheme="reds"),
                        title="# travel-rijen",
                    ),
                    tooltip=[
                        alt.Tooltip("dag:N", title="Dag"),
                        alt.Tooltip("uur:O", title="Uur"),
                        alt.Tooltip("aantal:Q", title="Aantal", format=","),
                    ],
                )
                .properties(height=260)
            )
            st.altair_chart(chart, use_container_width=True)
        except Exception as e:
            st.error(f"Heatmap-render fout: {e}")
    else:
        st.caption("Geen `gepland_start` beschikbaar.")


def _auto_restore_cache() -> None:
    """Als kritieke cache-bestanden ontbreken maar in Drive-backup staan, herstel."""
    import shutil

    local = Path(".cache")
    drive = Path(
        "/Users/johnnynijenhuis/Library/CloudStorage/"
        "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
        "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/"
        "Data analyse ritten/Route analyse tool/cache-backup"
    )
    if not drive.exists():
        return
    local.mkdir(exist_ok=True)
    restored = 0
    for src in drive.glob("*.parquet"):
        dst = local / src.name
        if not dst.exists():
            try:
                shutil.copy2(src, dst)
                restored += 1
            except Exception:
                pass
    if restored:
        st.toast(
            f"☁️ Cache hersteld: {restored} bestand(en) van Drive-backup.",
            icon="✅",
        )


def main() -> None:
    _auto_restore_cache()
    st.markdown(ETA_CSS, unsafe_allow_html=True)
    st.markdown(ETA_HERO, unsafe_allow_html=True)

    with st.sidebar:
        st.header("Databron")

        if "data_dir" not in st.session_state:
            st.session_state.data_dir = os.getenv("DATA_DIR", DEFAULT_DATA_DIR)
        if "csv_dir" not in st.session_state:
            st.session_state.csv_dir = DEFAULT_CSV_DIR

        data_dir = Path(st.session_state.data_dir)
        drive_files = list_excel_files(data_dir) if data_dir.exists() else []
        csv_dir = Path(st.session_state.csv_dir)
        has_csvs = csv_dir.exists() and bool(list_monthly_csvs(csv_dir))

        bron_options = []
        if drive_files:
            bron_options.append("📁 xlsx-map")
        bron_options.append("📤 xlsx-upload")
        if has_csvs:
            bron_options.append("📅 Maand-CSV's")

        if len(bron_options) == 1:
            mode = bron_options[0]
            st.caption(f"Bron: {mode}")
        else:
            if (
                "bron_mode" not in st.session_state
                or st.session_state.bron_mode not in bron_options
            ):
                st.session_state.bron_mode = (
                    "📅 Maand-CSV's" if has_csvs else bron_options[0]
                )
            mode = st.radio(
                "Bron",
                bron_options,
                key="bron_mode",
                horizontal=True,
                label_visibility="collapsed",
            )

        excel_path: Path | None = None
        csv_df: pd.DataFrame | None = None

        if mode == "📁 xlsx-map":
            c1, c2 = st.columns([4, 1])
            with c2:
                st.write("")
                st.write("")
                if st.button("📂", help="Kies map in Finder"):
                    picked = _pick_directory_finder(st.session_state.data_dir)
                    if picked:
                        st.session_state.data_dir = picked
                        st.rerun()
            with c1:
                st.text_input(
                    "Data-map",
                    key="data_dir",
                    help="Map met Excel-rittenexports (bv. Google Drive map).",
                )

            data_dir = Path(st.session_state.data_dir)
            all_files = list_excel_files(data_dir)
            if not all_files:
                st.error(f"Geen .xlsx bestanden gevonden in:\n{data_dir}")
                st.stop()

            supported = [
                f for f in all_files
                if _detect_schema_cached(str(f), f.stat().st_mtime) is not None
            ]
            if not supported:
                st.error(
                    f"Geen ondersteund .xlsx-bestand in:\n{data_dir}\n\n"
                    "Verwacht: TRP BI trip-stop export of Rittendata per wagen."
                )
                st.stop()
            skipped = len(all_files) - len(supported)
            if skipped:
                st.caption(
                    f"ℹ️ {skipped} bestand(en) overgeslagen "
                    "(geen herkenbare TRP BI / Rittendata-export — bv. samenvattingen)."
                )

            file_names = [f.name for f in supported]
            choice = st.selectbox("Bestand", file_names, index=0)
            excel_path = data_dir / choice
        elif mode == "📤 xlsx-upload":
            uploaded = st.file_uploader(
                "Excel-bestand (.xlsx)",
                type=["xlsx"],
                help=(
                    "Upload een TRP BI trip-stop export of Rittendata per wagen. "
                    "Bestand wordt lokaal verwerkt, niet opgeslagen."
                ),
            )
            if uploaded is None:
                st.info("Wachten op upload…")
                st.stop()
            excel_path = _persist_upload(uploaded)
            st.caption(f"Geladen: **{uploaded.name}** ({uploaded.size // 1024} KB)")
        else:  # 📅 Maand-CSV's
            c1, c2 = st.columns([4, 1])
            with c2:
                st.write("")
                st.write("")
                if st.button("📂", help="Kies CSV-map in Finder", key="csv_finder"):
                    picked = _pick_directory_finder(st.session_state.csv_dir)
                    if picked:
                        st.session_state.csv_dir = picked
                        st.rerun()
            with c1:
                st.text_input(
                    "CSV-map",
                    key="csv_dir",
                    help="Map met maandelijkse `Rittendata per wagen detail`-CSV's.",
                )
            csv_dir = Path(st.session_state.csv_dir)
            csv_files = list_monthly_csvs(csv_dir)
            if not csv_files:
                st.error(f"Geen .csv bestanden gevonden in:\n{csv_dir}")
                st.stop()
            st.caption(f"📅 {len(csv_files)} maand-CSV's gevonden in {csv_dir.name}")

    if mode == "📅 Maand-CSV's":
        csv_df = _load_csv_monthly(str(csv_dir))
        df = csv_df
        schema = "trip_stop"  # CSV-data heeft dezelfde lat/lon/stop-volgorde als TRP BI
        st.caption(
            f"📄 Modus: **maand-CSV's** (alleen `actiesoort=travel`). "
            f"{len(df):,}".replace(",", ".")
            + f" travel-rijen, {df['trip_id'].nunique():,}".replace(",", ".")
            + f" trips, {df['wagencode'].nunique()} wagens. "
            "Adressen via Nominatim gegeocodeerd."
        )
    else:
        try:
            schema, sheet = detect_schema(excel_path)
        except UnsupportedSchema as e:
            st.error(str(e))
            st.stop()

        if schema == "trip_stop":
            df = _load_trip_stops(str(excel_path))
            st.caption(
                f"📄 Modus: **trip-stop** (TRP BI-export, sheet `{sheet}`). "
                "Per stop één rij, met GPS-coördinaten en standtijd."
            )
        else:
            df = _load_trip_summaries(str(excel_path))
            st.caption(
                f"📄 Modus: **trip-summary** (Rittendata per wagen, sheet `{sheet}`). "
                "Elke rit = 2 virtuele stops (begin- en eindstad, gegeocodeerd). "
                "Geen standtijd per stop beschikbaar."
            )

    if "adres" in df.columns and "in_zez" not in df.columns:
        df = _annotate_zez(df)

    with st.sidebar:
        st.header("Filters")

        type_choice = st.segmented_control(
            "Vervoer",
            options=["Alles", "Eigen vervoer", "Charters"],
            default="Alles",
            help="Eigen = postnl_* vervoerders. Charters = uitbesteed vervoer.",
        )
        type_map = {
            "Alles": None,
            "Eigen vervoer": ["eigen"],
            "Charters": ["charter"],
        }
        sel_types = type_map.get(type_choice)

        _vervoerder_pool = df
        if sel_types:
            _vervoerder_pool = df[df["vervoerder_type"].isin(sel_types)]
        vervoerders = sorted(_vervoerder_pool["vervoerder"].dropna().unique().tolist())
        sel_vervoerders = st.multiselect(
            "Vervoerder (optioneel)",
            vervoerders,
            default=[],
            help="Leeg = alle binnen bovenstaande keuze.",
        )

        min_date = df["trip_date"].min()
        max_date = df["trip_date"].max()
        date_range = st.date_input(
            "Datumbereik",
            value=(min_date.date(), max_date.date()),
            min_value=min_date.date(),
            max_value=max_date.date(),
        )

        has_dwell = df["dwell_min"].fillna(0).gt(0).any()
        if mode == "📅 Maand-CSV's":
            min_dwell = 0
        else:
            min_dwell = st.slider(
                "Minimale standtijd (min)",
                min_value=0,
                max_value=180,
                value=0,
                step=5,
                disabled=not has_dwell,
                help=(
                    "Stops korter dan dit worden genegeerd "
                    "(laden is dan onrealistisch)."
                    if has_dwell
                    else "Niet beschikbaar in trip-summary modus."
                ),
            )

        wagencodes_all = sorted(df["wagencode"].dropna().astype(str).unique().tolist())
        sel_wagens = st.multiselect(
            "Wagencode (optioneel)",
            wagencodes_all,
            default=[],
            help="Leeg = alle wagens.",
        )

        TRIP_KM_BUCKETS = [
            ("0–10 km", 0, 10),
            ("10–100 km", 10, 100),
            ("100–200 km", 100, 200),
            ("200–300 km", 200, 300),
            ("300–400 km", 300, 400),
            ("400–500 km", 400, 500),
            ("500–600 km", 500, 600),
            ("600–700 km", 600, 700),
            ("700–800 km", 700, 800),
            (">800 km", 800, float("inf")),
        ]
        sel_trip_buckets = st.multiselect(
            "Trip-afstand buckets",
            options=[b[0] for b in TRIP_KM_BUCKETS],
            default=[b[0] for b in TRIP_KM_BUCKETS],
            help=(
                "Filter trips op totale rij-afstand. Een trip = alle ritten "
                "binnen één tripnummer (één shift). Selecteer welke trip-"
                "groottes je wilt zien."
            ),
        )

        zez_filter_mode = st.radio(
            "ZE-zone filter",
            options=["Alle stops", "Alleen stops in ZE-zone", "Alleen stops buiten ZE-zone"],
            index=0,
            help=(
                "ZE-zone = postcode-gebied waar vanaf 2025-2030 alleen "
                "emissieloze trucks mogen. Filter helpt bij prioritering "
                "van laadinfra in ZE-relevante locaties."
            ),
        )

        st.header("Kaartlagen")

        with st.expander("📍 Stoplocaties", expanded=True):
            show_heatmap = st.checkbox("Stop-heatmap", value=True)
            show_markers = st.checkbox("Drukste stop-locaties", value=False)
            marker_top_n = st.slider(
                "Aantal locaties (top N op aantal stops)",
                min_value=50,
                max_value=5000,
                value=250,
                step=50,
                disabled=not show_markers,
                help=(
                    "Groepeert per adres, telt het totaal aantal stops daar, "
                    "en toont de top N. Default 250 = de vaakst bezochte locaties."
                ),
            )

        with st.expander("🛣️ Wegvlakken", expanded=False):
            show_routes = st.checkbox(
                "Routelijnen (volgt wegennet via OSRM)",
                value=False,
                help=(
                    "Toont per trip de werkelijke route via het wegennet. "
                    "Bij grote selecties wordt gecapped op 2000 trips."
                ),
            )
            show_road_heatmap = st.checkbox(
                "Wegvlak-heatmap (OSRM, alle trips)",
                value=False,
                disabled=schema != "trip_stop",
                help=(
                    "Basislaag: gradient blauw → oranje → donkerrood toont hoe vaak "
                    "elk wegvlak gebruikt wordt. Donkerrood = drukste corridors."
                    if schema == "trip_stop"
                    else "Niet beschikbaar in trip-summary modus."
                ),
            )
            show_top_x_overlay = st.checkbox(
                "→ Highlight top X% drukste",
                value=False,
                disabled=not show_road_heatmap,
                help=(
                    "Bovenop de heatmap: paarse lijnen tonen de drukst bereden "
                    "wegvlakken (gerangschikt op aantal keer bereden, niet op "
                    "unieke wagens). Handig om de allerdrukste delen te benoemen."
                ),
            )
            road_threshold = st.slider(
                "Min. aantal unieke wagens per wegvlak",
                min_value=1,
                max_value=100,
                value=10,
                step=1,
                disabled=not show_road_heatmap,
                help=(
                    "Een 'wegvlak' is een stukje weg van ca. 50-200 m "
                    "(OSRM road-graph edge). Heel Nederland heeft >500.000 "
                    "wegvlakken in deze data. Wegvlakken met minder unieke "
                    "wagens dan deze drempel worden verborgen."
                ),
            )
            road_show_pct = st.slider(
                "Top X% (highlighted)",
                min_value=1,
                max_value=25,
                value=1,
                step=1,
                disabled=not show_top_x_overlay,
                help=(
                    "Van alle wegvlakken boven de drempel: highlight de drukst "
                    "bereden top X% in paars. Sortering op aantal keer bereden "
                    "(n_passes), niet op unieke wagens. "
                    "1% bij ~500k wegvlakken = 5.000 lijnen (= goed leesbaar). "
                    "Hogere percentages = meer detail, maar trager."
                ),
            )
            min_chain_length_m = st.slider(
                "Min. wegvlak-lengte (Dashboard, m)",
                min_value=0,
                max_value=5000,
                value=200,
                step=50,
                help=(
                    "Filtert in de Dashboard-tabel 'Top 100 wegvlakken' alle "
                    "samengevoegde stukken korter dan deze lengte. Verwijdert "
                    "de OSRM-fragmentjes en houdt substantiële stretches over."
                ),
            )

        with st.expander("⚡ Laadlocaties", expanded=False):
            show_chargers = st.checkbox(
                "Geverifieerde HDV-laadlocaties",
                value=False,
                help=(
                    "Handmatig geverifieerde laadlocaties die toegankelijk zijn "
                    "voor vrachtwagens (244 locaties)."
                ),
            )
            charger_min_kw = st.slider(
                "Min. laadvermogen (kW)",
                min_value=0,
                max_value=400,
                value=150,
                step=50,
                disabled=not show_chargers,
            )
            charger_only_dedicated = st.checkbox(
                "Alleen dedicated voor HDV",
                value=False,
                disabled=not show_chargers,
                help="Alleen locaties die exclusief voor vrachtwagens zijn ingericht.",
            )
            charger_access = st.multiselect(
                "Toegankelijkheid",
                ["Publiek", "Semi-publiek", "Privaat"],
                default=["Publiek", "Semi-publiek"],
                disabled=not show_chargers,
            )

    if isinstance(date_range, tuple) and len(date_range) == 2:
        dr = (pd.Timestamp(date_range[0]), pd.Timestamp(date_range[1]))
    else:
        dr = None

    stops = filter_stops(
        df,
        vervoerder_types=sel_types,
        vervoerders=sel_vervoerders or None,
        wagencodes=sel_wagens or None,
        date_range=dr,
        min_dwell_min=min_dwell,
    )

    if sel_trip_buckets and len(sel_trip_buckets) < len(TRIP_KM_BUCKETS):
        if "afstand_km_trip" in stops.columns:
            trip_km = stops.groupby("trip_id")["afstand_km_trip"].max()
        else:
            trip_km = stops.groupby("trip_id")["afstand_km"].sum()
        allowed_trips: set = set()
        for bucket_label in sel_trip_buckets:
            for label, lo, hi in TRIP_KM_BUCKETS:
                if label == bucket_label:
                    allowed_trips.update(
                        trip_km[(trip_km >= lo) & (trip_km < hi)].index
                    )
                    break
        stops = stops[stops["trip_id"].isin(allowed_trips)].reset_index(drop=True)

    if "in_zez" in stops.columns:
        if zez_filter_mode == "Alleen stops in ZE-zone":
            stops = stops[stops["in_zez"]].reset_index(drop=True)
        elif zez_filter_mode == "Alleen stops buiten ZE-zone":
            stops = stops[~stops["in_zez"]].reset_index(drop=True)

    col1, col2, col3, col4 = st.columns(4)
    col1.metric("Stops (na filter)", f"{len(stops):,}".replace(",", "."))
    col2.metric("Unieke wagens", stops["wagencode"].nunique())
    col3.metric("Unieke trips", stops["trip_id"].nunique())
    col4.metric(
        "Totale standtijd (uur)", f"{int(stops['dwell_min'].sum() / 60):,}".replace(",", ".")
    )

    if stops.empty:
        st.warning("Geen stops over na filtering. Pas filters aan.")
        return

    tab_map, tab_dash, tab_sim = st.tabs(
        ["Kaart", "Dashboard", "Simulatie"]
    )

    chargers_df = pd.DataFrame()
    charger_error: str | None = None
    if show_chargers:
        try:
            chargers_df = _load_chargers(charger_min_kw)
            if charger_only_dedicated:
                chargers_df = chargers_df[
                    chargers_df["dedicated"].astype("string").str.strip() == "Ja"
                ]
            if charger_access:
                chargers_df = chargers_df[
                    chargers_df["toegankelijkheid"].isin(charger_access)
                ]
        except FileNotFoundError as e:
            charger_error = str(e)
        except Exception as e:
            charger_error = f"Laden van HDV-laadlocaties mislukt: {e}"

        with st.sidebar:
            if charger_error:
                st.error(f"⚠️ HDV-laadlocaties niet geladen.\n\n{charger_error}")
            elif chargers_df.empty:
                st.info("Geen laders die aan filters voldoen.")
            else:
                st.success(
                    f"✅ {len(chargers_df)} HDV-laadlocaties geladen "
                    "(🟢 groene bliksem-markers op de kaart)."
                )

    with tab_map:
        center = [stops["lat"].mean(), stops["lon"].mean()]
        fmap = folium.Map(location=center, zoom_start=8, tiles="OpenStreetMap")

        HEATMAP_AGG_THRESHOLD = 50_000
        if show_heatmap:
            if len(stops) > HEATMAP_AGG_THRESHOLD:
                agg = (
                    stops.assign(
                        lat_b=stops["lat"].round(3),
                        lon_b=stops["lon"].round(3),
                    )
                    .groupby(["lat_b", "lon_b"], sort=False)
                    .agg(weight=("dwell_min", lambda s: max(s.sum(), 1)))
                    .reset_index()
                )
                heat_points = agg[["lat_b", "lon_b", "weight"]].values.tolist()
                st.caption(
                    f"⚡ Heatmap geaggregeerd: {len(agg):,} cellen "
                    f"(uit {len(stops):,} stops, ~110 m grid)."
                    .replace(",", ".")
                )
            else:
                heat_points = stops[["lat", "lon", "dwell_min"]].values.tolist()
            HeatMap(heat_points, radius=14, blur=20, min_opacity=0.3).add_to(fmap)

        if show_markers:
            loc_agg = (
                stops.groupby("adres", sort=False)
                .agg(
                    lat=("lat", "first"),
                    lon=("lon", "first"),
                    locatie_naam=("locatie_naam", "first"),
                    n_unieke_wagens=("wagencode", "nunique"),
                    n_stops=("trip_id", "size"),
                    n_trips=("trip_id", "nunique"),
                    gem_rijduur_min=("dwell_min", "mean"),
                )
                .reset_index()
                .nlargest(marker_top_n, "n_stops")
            )
            st.caption(
                f"⚡ Top-{marker_top_n:,} drukste locaties getoond uit "
                f"{stops['adres'].nunique():,} unieke adressen "
                f"(sortering: aantal stops)."
                .replace(",", ".")
            )
            cluster = MarkerCluster().add_to(fmap)
            max_s = int(loc_agg["n_stops"].max()) if not loc_agg.empty else 1
            for r in loc_agg.itertuples(index=False):
                radius = 4 + 8 * (int(r.n_stops) / max(max_s, 1))
                popup = folium.Popup(
                    html=(
                        f"<b>{r.locatie_naam or '(onbekend)'}</b><br>"
                        f"{r.adres or ''}<br>"
                        f"📍 <b>{int(r.n_stops):,} stops</b> · "
                        f"{int(r.n_trips):,} unieke trips<br>"
                        f"🚛 {int(r.n_unieke_wagens)} unieke wagens<br>"
                        f"⏱️ Gem. rijduur ernaartoe: {r.gem_rijduur_min:.0f} min"
                    ).replace(",", "."),
                    max_width=300,
                )
                folium.CircleMarker(
                    location=[r.lat, r.lon],
                    radius=radius,
                    color="#1f77b4",
                    fill=True,
                    fill_opacity=0.6,
                    weight=1,
                    popup=popup,
                    tooltip=f"{int(r.n_stops):,} stops".replace(",", "."),
                ).add_to(cluster)

        routes: dict = {}
        need_osrm = show_top_x_overlay or show_road_heatmap or show_routes
        if need_osrm:
            segs = unique_segments(stops)
            cached_routes, missing_n = load_cached_routes(segs)
            if missing_n == 0:
                routes = cached_routes
            else:
                eta_min = missing_n * 0.25 / 60
                if missing_n > 500:
                    st.warning(
                        f"⚠️ {missing_n} nieuwe segmenten op te halen "
                        f"(~{eta_min:.0f} min). Tijdens het ophalen kan de "
                        "verbinding via Cloudflare time-outen — fetch loopt "
                        "lokaal door en cachet incrementeel. "
                        "**Tip:** filter eerst op datum/wagen voor snellere demo."
                    )
                    if not st.button(
                        f"Toch alle {missing_n} segmenten ophalen",
                        key="confirm_osrm_fetch",
                    ):
                        st.info(
                            "OSRM-routes overgeslagen. Filter eerst, of klik "
                            "op de knop hierboven om alsnog te starten."
                        )
                        routes = cached_routes
                        need_osrm = False
                    else:
                        st.info(
                            f"Ophalen {missing_n} nieuwe segmenten "
                            f"(+ {len(segs) - missing_n} uit cache)..."
                        )
                        progress = st.progress(0.0)

                        def _cb(i: int, total: int) -> None:
                            progress.progress(min(1.0, i / max(total, 1)))

                        routes = fetch_routes(segs, progress_cb=_cb)
                        progress.empty()
                else:
                    st.info(
                        f"Ophalen {missing_n} nieuwe segmenten "
                        f"(+ {len(segs) - missing_n} uit cache, ~{eta_min:.1f} min)..."
                    )
                    progress = st.progress(0.0)

                    def _cb(i: int, total: int) -> None:
                        progress.progress(min(1.0, i / max(total, 1)))

                    routes = fetch_routes(segs, progress_cb=_cb)
                    progress.empty()

        cache_variant = _detect_cache_variant(stops, df)

        edges_df: pd.DataFrame | None = None
        if show_top_x_overlay or show_road_heatmap:
            if cache_variant:
                edges_df = _load_cached_weighted_edges_df(cache_variant)
                if edges_df is not None and not edges_df.empty:
                    st.caption(
                        f"⚡ {len(edges_df):,} wegvlakken uit pre-compute cache "
                        f"(variant: {cache_variant})."
                        .replace(",", ".")
                    )
            if edges_df is None:
                with st.spinner("Wegvlakken wegen en kleuren..."):
                    edges = compute_weighted_edges(stops, routes)
                edges_df = pd.DataFrame(
                    [
                        {
                            "lat1": e[0][0],
                            "lon1": e[0][1],
                            "lat2": e[1][0],
                            "lon2": e[1][1],
                            "n_wagens": e[2],
                            "n_passes": e[3] if len(e) > 3 else e[2],
                        }
                        for e in edges
                    ]
                )

        if show_top_x_overlay and edges_df is not None and not edges_df.empty:
            max_passes = int(edges_df["n_passes"].max())
            filtered = edges_df[edges_df["n_wagens"] >= road_threshold]
            if filtered.empty:
                st.warning(
                    f"Geen wegvlak heeft ≥ {road_threshold} unieke wagens. "
                    "Zet de drempel lager."
                )
            else:
                target_n = max(50, int(len(filtered) * road_show_pct / 100))
                if len(filtered) > target_n:
                    top = filtered.nlargest(target_n, "n_passes")
                    st.info(
                        f"ℹ️ {len(filtered):,} wegvlakken boven drempel · "
                        f"top {road_show_pct}% (op aantal keer bereden) "
                        f"gehighlight = {target_n:,} lijnen."
                        .replace(",", ".")
                    )
                else:
                    top = filtered
                min_passes = int(top["n_passes"].min())
                denom = max(1, max_passes - min_passes)
                for r in top.itertuples(index=False):
                    n_p = int(r.n_passes)
                    n_w = int(r.n_wagens)
                    t = (n_p - min_passes) / denom
                    color = lerp_hex("#ddd6fe", "#4c1d95", t)
                    weight = 2.0 + 4.5 * t
                    folium.PolyLine(
                        [(r.lat1, r.lon1), (r.lat2, r.lon2)],
                        color=color,
                        weight=weight,
                        opacity=0.85,
                        tooltip=f"{n_p}× bereden door {n_w} wagens",
                    ).add_to(fmap)

        if show_routes:
            TRIP_CAP = 2000
            trip_groups = list(stops.groupby(["wagencode", "trip_date", "trip_id"]))
            if len(trip_groups) > TRIP_CAP:
                st.info(
                    f"ℹ️ {len(trip_groups):,} trips — alleen eerste {TRIP_CAP} "
                    "getekend. Filter eerst voor specifieke trips."
                )
                trip_groups = trip_groups[:TRIP_CAP]
            for _, g in trip_groups:
                if len(g) < 2:
                    continue
                coords = trip_polyline(g, routes)
                folium.PolyLine(
                    coords, color="#6b21a8", weight=2, opacity=0.55
                ).add_to(fmap)

        road_heat_points: list[tuple[float, float, float]] = []
        if show_road_heatmap:
            all_points = None
            if cache_variant:
                all_points = _load_cached_road_heatmap(cache_variant)
                if all_points:
                    st.caption(
                        f"⚡ {len(all_points):,} wegvlak-cellen geladen uit pre-compute cache "
                        f"(variant: {cache_variant})."
                        .replace(",", ".")
                    )
            if all_points is None:
                with st.spinner("Wegvlak-heatmap berekenen..."):
                    all_points = compute_road_heatmap_points(stops, routes)
            road_heat_points = [p for p in all_points if p[2] >= road_threshold]
            if road_heat_points:
                HeatMap(
                    road_heat_points,
                    radius=8,
                    blur=12,
                    min_opacity=0.35,
                    gradient={
                        "0.05": "#1e3a8a",
                        "0.2": "#3b82f6",
                        "0.4": "#f59e0b",
                        "0.65": "#dc2626",
                        "1.0": "#7f1d1d",
                    },
                ).add_to(fmap)
            elif all_points:
                max_n = int(max(p[2] for p in all_points))
                st.warning(
                    f"Geen wegvlak-cel heeft ≥ {road_threshold} unieke wagens "
                    f"(max in dataset = {max_n}). Zet de drempel lager."
                )

        if show_chargers and not chargers_df.empty:
            for _, c in chargers_df.iterrows():
                dedicated = (c.get("dedicated") or "").strip()
                t247 = (c.get("twentyfour_seven") or "").strip()
                acces = (c.get("toegankelijkheid") or "").strip()
                ccs = (c.get("ccs_mcs") or "").strip()
                popup = folium.Popup(
                    html=(
                        f"<b>{c['name'] or '(naamloos)'}</b><br>"
                        f"{c['address']}, {c['postcode']} {c['town']}<br>"
                        f"⚡ Max {int(c['max_power_kw'])} kW · {int(c['n_connectors'])} laadpalen<br>"
                        f"🔌 {ccs or 'onbekend'}<br>"
                        f"🚪 {acces or 'onbekend'}"
                        + (f" · 24/7" if t247 == "Ja" else "")
                        + (f" · 🚛 dedicated HDV" if dedicated == "Ja" else "")
                    ),
                    max_width=300,
                )
                folium.Marker(
                    location=[c["lat"], c["lon"]],
                    icon=folium.Icon(color="green", icon="bolt", prefix="fa"),
                    tooltip=(
                        f"⚡ {c['name'] or 'lader'} — {int(c['max_power_kw'])} kW"
                        + (" · HDV" if dedicated == "Ja" else "")
                    ),
                    popup=popup,
                ).add_to(fmap)
            st.caption(
                f"🟢 Groene bliksem-markers = {len(chargers_df)} geverifieerde "
                f"HDV-laadlocaties ≥ {charger_min_kw} kW. Klik op een marker voor details."
            )

        legend_items = []
        if show_heatmap:
            legend_items.append(
                ('<span style="background:linear-gradient(90deg,#0000ff,#00ff00,#ffff00,#ff0000);'
                 'display:inline-block;width:36px;height:10px;border-radius:2px;"></span>',
                 "Stop-heatmap (alle stops, gewogen op rijduur)")
            )
        if show_markers:
            legend_items.append(
                ('<span style="display:inline-block;width:14px;height:14px;border-radius:50%;'
                 'background:#1f77b4;border:1px solid #1f77b4;opacity:0.6;"></span>',
                 "Drukste stop-locaties (cirkelgrootte = aantal wagens)")
            )
        if show_road_heatmap:
            legend_items.append(
                ('<span style="background:linear-gradient(90deg,#1e3a8a,#3b82f6,#f59e0b,#dc2626,#7f1d1d);'
                 'display:inline-block;width:36px;height:10px;border-radius:2px;"></span>',
                 "Wegvlak-heatmap (donkerrood = drukste corridors)")
            )
        if show_top_x_overlay:
            legend_items.append(
                ('<span style="background:linear-gradient(90deg,#ddd6fe,#4c1d95);'
                 'display:inline-block;width:36px;height:8px;border-radius:2px;"></span>',
                 "Top X% drukste wegvlakken (donkerder = vaker bereden)")
            )
        if show_routes:
            legend_items.append(
                ('<span style="background:#6b21a8;display:inline-block;width:36px;height:3px;"></span>',
                 "Routelijnen per trip (volgt wegennet)")
            )
        if show_chargers and not chargers_df.empty:
            legend_items.append(
                ('<span style="color:#16a34a;font-size:14px;">⚡</span>',
                 "Geverifieerde HDV-laadlocaties")
            )

        if legend_items:
            legend_html = (
                '<div style="position:fixed;bottom:30px;right:30px;z-index:9999;'
                'background:rgba(255,255,255,0.95);border:1px solid #c9c8d3;'
                'border-radius:10px;padding:10px 14px;font-family:Montserrat,Arial,sans-serif;'
                'font-size:12px;box-shadow:0 4px 12px rgba(0,0,0,0.1);max-width:340px;">'
                '<div style="font-weight:600;color:#2e2343;margin-bottom:6px;">Legenda</div>'
            )
            for icon, label in legend_items:
                legend_html += (
                    f'<div style="display:flex;align-items:center;gap:8px;margin:4px 0;">'
                    f'{icon}<span style="color:#1a1a1f;">{label}</span></div>'
                )
            legend_html += "</div>"
            fmap.get_root().html.add_child(folium.Element(legend_html))

        st_folium(fmap, height=620, use_container_width=True, returned_objects=[])

    with tab_dash:
        _render_dashboard(stops, chargers_df, road_threshold, min_chain_length_m)

    with tab_sim:
        _render_simulation(stops)


if __name__ == "__main__":
    main()

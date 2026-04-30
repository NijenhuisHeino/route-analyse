"""PostNL truck route heatmap — Streamlit app."""

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
from src.simulation import charge_hotspots, simulate_soc
from src.summary_loader import load_trip_summaries
from src.hotspots import rank_hotspots
from src.road_usage import (
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
    page_title="PostNL Route Heatmap — eTruck Academy",
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

/* Tabs */
.stTabs [data-baseweb="tab-list"] {
  gap: 4px;
  border-bottom: 2px solid var(--eta-border-soft);
}
.stTabs [data-baseweb="tab"] {
  font-weight: 600;
  color: var(--eta-text-subtle);
  font-size: 15px;
}
.stTabs [aria-selected="true"] {
  color: var(--eta-purple-900) !important;
}
.stTabs [aria-selected="true"] > div:first-child {
  border-bottom: 3px solid var(--eta-yellow-500) !important;
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
  <h1>PostNL rittenkaart — hotspots voor externe laadinfrastructuur</h1>
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


def _build_excel_export(
    filters_info: dict,
    stops_df: pd.DataFrame,
    corridors_df: pd.DataFrame,
    edges_df: pd.DataFrame | None,
) -> bytes:
    """Build multi-sheet Excel met filters + top-tabellen voor PostNL."""
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
    st.subheader("Top 50 stop-locaties (op aantal unieke wagens)")
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
    top_stops = hotspots.head(50).copy()
    top_stops["maps"] = top_stops.apply(
        lambda r: f"https://www.google.com/maps?q={r['lat_round']},{r['lon_round']}",
        axis=1,
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
                for p1, p2, _ in corridor["edges"]:
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
        chains_sorted = sorted(
            chains, key=lambda c: (c["n_passes"], c["n_wagens"]), reverse=True
        )[:100]

        edges_df_export = pd.DataFrame(
            [
                {
                    "rank": i + 1,
                    "lat_van": round(c["lat1"], 6),
                    "lon_van": round(c["lon1"], 6),
                    "lat_tot": round(c["lat2"], 6),
                    "lon_tot": round(c["lon2"], 6),
                    "n_unieke_wagens": c["n_wagens"],
                    "n_keer_bereden": c["n_passes"],
                    "passages_per_wagen": round(
                        c["n_passes"] / max(c["n_wagens"], 1), 1
                    ),
                    "lengte_m": int(c["length_km"] * 1000),
                    "n_micro_edges": c["n_micro_edges"],
                    "maps": f"https://www.google.com/maps?q={(c['lat1'] + c['lat2']) / 2},{(c['lon1'] + c['lon2']) / 2}",
                }
                for i, c in enumerate(chains_sorted)
            ]
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
    st.subheader("Excel-export voor PostNL")
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
                    "(geen herkenbare PostNL-export — bv. samenvattingen)."
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

        st.header("Kaartlagen")
        show_heatmap = st.checkbox("Heatmap", value=True)
        show_markers = st.checkbox("Drukste stop-locaties", value=False)
        marker_top_n = st.slider(
            "Aantal locaties (top N op unieke wagens)",
            min_value=50,
            max_value=5000,
            value=250,
            step=50,
            disabled=not show_markers,
            help=(
                "Groepeert per adres, telt hoeveel unieke vrachtwagens deze "
                "locatie bezoeken, en toont de top N. Default 250 = de drukste "
                "knooppunten — beste indicatie voor kandidaat-laadlocaties."
            ),
        )
        show_routes = st.checkbox("Routelijnen", value=False)
        use_road_routes = st.checkbox(
            "→ volg wegennet (OSRM)",
            value=False,
            disabled=not show_routes,
            help="Haalt werkelijke wegroute per segment op via OSRM en cachet lokaal. Eerste keer traag.",
        )
        show_road_heatmap = st.checkbox(
            "Wegvlak-heatmap (OSRM, alle trips)",
            value=False,
            disabled=schema != "trip_stop",
            help=(
                "Legt alle trip-routes op elkaar en telt unieke wagens per "
                "wegvak (~110 m grid). Onthult de drukst bereden corridors. "
                "Eerste keer traag; daarna gecached."
                if schema == "trip_stop"
                else "Niet beschikbaar in trip-summary modus."
            ),
        )
        road_threshold = st.slider(
            "Min. aantal unieke wagens per wegvlak",
            min_value=1,
            max_value=100,
            value=10,
            step=1,
            disabled=not (show_road_heatmap or (show_routes and use_road_routes)),
            help=(
                "Een 'wegvlak' is een stukje weg van ca. 50-200 m "
                "(OSRM road-graph edge). Heel Nederland heeft >500.000 "
                "wegvlakken in deze data. Wegvlakken met minder unieke "
                "wagens dan deze drempel worden verborgen."
            ),
        )
        road_show_pct = st.slider(
            "Toon top X% drukste wegvlakken",
            min_value=1,
            max_value=25,
            value=1,
            step=1,
            disabled=not (show_routes and use_road_routes),
            help=(
                "Van alle wegvlakken boven de drempel: toon alleen de "
                "drukst bereden top X%. Voorkomt dat de kaart dichtslibt. "
                "1% bij ~500k wegvlakken = 5.000 lijnen (= goed leesbaar). "
                "Hogere percentages = meer detail, maar trager."
            ),
        )
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
        ["🗺️ Kaart", "📊 Dashboard", "🔋 Simulatie"]
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
                .nlargest(marker_top_n, "n_unieke_wagens")
            )
            st.caption(
                f"⚡ Top-{marker_top_n:,} drukste locaties getoond uit "
                f"{stops['adres'].nunique():,} unieke adressen "
                f"(sortering: aantal unieke wagens)."
                .replace(",", ".")
            )
            cluster = MarkerCluster().add_to(fmap)
            max_w = int(loc_agg["n_unieke_wagens"].max()) if not loc_agg.empty else 1
            for r in loc_agg.itertuples(index=False):
                radius = 4 + 8 * (int(r.n_unieke_wagens) / max(max_w, 1))
                popup = folium.Popup(
                    html=(
                        f"<b>{r.locatie_naam or '(onbekend)'}</b><br>"
                        f"{r.adres or ''}<br>"
                        f"🚛 <b>{int(r.n_unieke_wagens)} unieke wagens</b><br>"
                        f"📍 {int(r.n_stops):,} stops · {int(r.n_trips):,} trips<br>"
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
                    tooltip=f"{int(r.n_unieke_wagens)} unieke wagens",
                ).add_to(cluster)

        routes: dict = {}
        need_osrm = (show_routes and use_road_routes) or show_road_heatmap
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

        if show_routes and use_road_routes:
            edges_df: pd.DataFrame | None = None
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
            if edges_df is not None and not edges_df.empty:
                max_n = int(edges_df["n_wagens"].max())
                filtered = edges_df[edges_df["n_wagens"] >= road_threshold]
                if filtered.empty:
                    st.warning(
                        f"Geen wegvlak heeft ≥ {road_threshold} unieke wagens "
                        f"(max in dataset = {max_n}). Zet de drempel lager."
                    )
                else:
                    target_n = max(50, int(len(filtered) * road_show_pct / 100))
                    if len(filtered) > target_n:
                        top = filtered.nlargest(target_n, "n_wagens")
                        st.info(
                            f"ℹ️ {len(filtered):,} wegvlakken boven drempel · "
                            f"top {road_show_pct}% getekend = {target_n:,} lijnen."
                            .replace(",", ".")
                        )
                    else:
                        top = filtered
                    denom = max(1, max_n - road_threshold)
                    for r in top.itertuples(index=False):
                        n = int(r.n_wagens)
                        t = (n - road_threshold) / denom
                        color = lerp_hex("#ddd6fe", "#4c1d95", t)
                        weight = 1.2 + 4.8 * t
                        folium.PolyLine(
                            [(r.lat1, r.lon1), (r.lat2, r.lon2)],
                            color=color,
                            weight=weight,
                            opacity=0.85,
                            tooltip=f"{n} unieke wagens",
                        ).add_to(fmap)
        elif show_routes:
            TRIP_CAP = 2000
            trip_groups = list(stops.groupby(["wagencode", "trip_date", "trip_id"]))
            if len(trip_groups) > TRIP_CAP:
                st.info(
                    f"ℹ️ {len(trip_groups):,} trips — alleen eerste {TRIP_CAP} "
                    "getekend (rechte lijnen). Filter eerst voor specifieke trips."
                )
                trip_groups = trip_groups[:TRIP_CAP]
            for _, g in trip_groups:
                if len(g) < 2:
                    continue
                coords = g[["lat", "lon"]].values.tolist()
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
        if show_routes and use_road_routes:
            legend_items.append(
                ('<span style="background:linear-gradient(90deg,#ddd6fe,#4c1d95);'
                 'display:inline-block;width:36px;height:8px;border-radius:2px;"></span>',
                 "Wegvlakken (top X%) — donkerder = meer unieke wagens")
            )
        elif show_routes:
            legend_items.append(
                ('<span style="background:#6b21a8;display:inline-block;width:36px;height:3px;"></span>',
                 "Routelijnen (rechte lijnen tussen stops)")
            )
        if show_road_heatmap:
            legend_items.append(
                ('<span style="background:linear-gradient(90deg,#1e3a8a,#3b82f6,#f59e0b,#dc2626,#7f1d1d);'
                 'display:inline-block;width:36px;height:10px;border-radius:2px;"></span>',
                 "Wegvlak-heatmap (donkerrood = drukste corridors)")
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
        _render_dashboard(stops, chargers_df, road_threshold)

    with tab_sim:
        _render_simulation(stops)


if __name__ == "__main__":
    main()

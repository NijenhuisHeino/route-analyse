"""PostNL truck route heatmap — Streamlit app."""

from __future__ import annotations

import os
import subprocess
from pathlib import Path

import folium
import pandas as pd
import streamlit as st
from dotenv import load_dotenv
from folium.plugins import HeatMap, MarkerCluster
from streamlit_folium import st_folium

from src.chargers import (
    DEFAULT_MIN_POWER_KW,
    OCMKeyMissing,
    add_nearest_charger_distance,
    fetch_chargers,
)
from src.data_loader import (
    UnsupportedSchema,
    detect_schema,
    filter_stops,
    list_excel_files,
    load_trips,
)
from src.geocoder import reverse_geocode
from src.summary_loader import load_trip_summaries
from src.hotspots import rank_hotspots
from src.road_usage import (
    compute_corridors,
    compute_road_heatmap_points,
    compute_weighted_edges,
    lerp_hex,
    top_road_hotspots,
)
from src.routing import (
    fetch_routes,
    load_cached_routes,
    trip_polyline,
    unique_segments,
)

load_dotenv()

DEFAULT_DATA_DIR = (
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/Data analyse ritten"
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


@st.cache_data(show_spinner="Excel inlezen...")
def _load_trip_stops(path: str) -> pd.DataFrame:
    return load_trips(Path(path))


@st.cache_data(show_spinner="Samenvattingsdata inlezen + steden geocoderen...")
def _load_trip_summaries(path: str) -> pd.DataFrame:
    return load_trip_summaries(Path(path))


@st.cache_data(show_spinner="Publieke laders ophalen...")
def _load_chargers(min_power_kw: float) -> pd.DataFrame:
    return fetch_chargers(min_power_kw=min_power_kw)


def _render_dashboard(
    stops: pd.DataFrame,
    chargers_df: pd.DataFrame,
    road_threshold: int,
) -> None:
    """Dashboard-tab: KPI's, top-20 stops, top-20 wegvlakken, trends."""
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

    col_left, col_right = st.columns([1, 1])

    with col_left:
        st.subheader("Top 20 stop-locaties")
        hotspots = rank_hotspots(stops)
        if not chargers_df.empty:
            hotspots = add_nearest_charger_distance(hotspots, chargers_df)
        top_stops = hotspots.head(20).copy()
        top_stops["maps"] = top_stops.apply(
            lambda r: f"https://www.google.com/maps?q={r['lat_round']},{r['lon_round']}",
            axis=1,
        )
        st.dataframe(
            top_stops,
            use_container_width=True,
            hide_index=True,
            column_config={
                "maps": st.column_config.LinkColumn(
                    "Kaart", display_text="📍 Open"
                ),
            },
        )
        st.download_button(
            "Download hotspots als CSV",
            data=hotspots.to_csv(index=False).encode("utf-8"),
            file_name="hotspots.csv",
            mime="text/csv",
            key="dl_hotspots",
        )

    with col_right:
        st.subheader(f"Top 20 corridors (≥ {road_threshold} wagens, gesorteerd op lengte)")
        st.caption(
            "Aaneengesloten wegvlakken boven de drempel worden samengevoegd "
            "tot één corridor. Drempel aanpassen via sidebar."
        )
        segs = unique_segments(stops)
        cached_routes, missing = load_cached_routes(segs)
        if missing > 0 and not cached_routes:
            st.info(
                f"Wegvlak-data nog niet gecached ({missing}/{len(segs)} segmenten). "
                "Zet op de Kaart-tab eenmalig **Routelijnen → volg wegennet** aan "
                "om deze tabel te vullen."
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
                    f"Geen corridors met ≥ {road_threshold} wagens. "
                    "Zet de drempel lager."
                )
            else:
                centers = [c["center"] for c in top_corridors]
                with st.spinner(
                    "Wegnamen opzoeken (eerste keer ~25 s, daarna cache)..."
                ):
                    rev_progress = st.progress(0.0)

                    def _rcb(i: int, total: int) -> None:
                        rev_progress.progress(min(1.0, i / max(total, 1)))

                    names = reverse_geocode(centers, progress_cb=_rcb)
                    rev_progress.empty()

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
                            "lengte_km": round(corridor["length_km"], 2),
                            "max_wagens": corridor["max_n"],
                            "med_wagens": corridor["median_n"],
                            "wegnaam": weg,
                            "plaats": plaats,
                            "maps": f"https://www.google.com/maps?q={lat_c},{lon_c}",
                        }
                    )
                st_folium(
                    mini, height=300, use_container_width=True, returned_objects=[]
                )

                edges_df = pd.DataFrame(rows)
                st.dataframe(
                    edges_df,
                    use_container_width=True,
                    hide_index=True,
                    column_config={
                        "maps": st.column_config.LinkColumn(
                            "Kaart", display_text="🛣️ Open"
                        ),
                    },
                )
                st.download_button(
                    "Download corridors als CSV",
                    data=edges_df.to_csv(index=False).encode("utf-8"),
                    file_name="corridors_top20.csv",
                    mime="text/csv",
                    key="dl_corridors",
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


def main() -> None:
    st.markdown(ETA_CSS, unsafe_allow_html=True)
    st.markdown(ETA_HERO, unsafe_allow_html=True)

    with st.sidebar:
        st.header("Databron")

        if "data_dir" not in st.session_state:
            st.session_state.data_dir = os.getenv("DATA_DIR", DEFAULT_DATA_DIR)

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

        files = list_excel_files(data_dir)
        if not files:
            st.error(f"Geen .xlsx bestanden gevonden in:\n{data_dir}")
            st.stop()

        file_names = [f.name for f in files]
        choice = st.selectbox("Bestand", file_names, index=0)
        excel_path = data_dir / choice

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
        min_dwell = st.slider(
            "Minimale standtijd (min)",
            min_value=0,
            max_value=180,
            value=30 if has_dwell else 0,
            step=5,
            disabled=not has_dwell,
            help=(
                "Stops korter dan dit worden genegeerd (laden is dan onrealistisch)."
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
        show_markers = st.checkbox("Stop-markers", value=False)
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
                "Wegvlakken/routelijnen met minder unieke wagens worden verborgen. "
                "Routelijnen krijgen een kleurverloop van licht paars (net boven "
                "drempel) naar donkerpaars (drukst bereden)."
            ),
        )
        show_chargers = st.checkbox("Publieke laders (OCM)", value=False)
        charger_min_kw = st.slider(
            "Min. laadvermogen (kW)",
            min_value=50,
            max_value=400,
            value=int(DEFAULT_MIN_POWER_KW),
            step=50,
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

    tab_map, tab_dash = st.tabs(["🗺️ Kaart", "📊 Dashboard"])

    chargers_df = pd.DataFrame()
    charger_error: str | None = None
    if show_chargers:
        try:
            chargers_df = _load_chargers(charger_min_kw)
        except OCMKeyMissing as e:
            charger_error = str(e)
        except Exception as e:
            charger_error = f"Ophalen laders mislukt: {e}"

        with st.sidebar:
            if charger_error:
                st.error(f"⚠️ Publieke laders niet geladen.\n\n{charger_error}")
            elif chargers_df.empty:
                st.info(f"Geen laders ≥ {charger_min_kw} kW gevonden in NL.")
            else:
                st.success(
                    f"✅ {len(chargers_df)} laders ≥ {charger_min_kw} kW geladen "
                    "(🟢 groene bliksem-markers op de kaart)."
                )

    with tab_map:
        center = [stops["lat"].mean(), stops["lon"].mean()]
        fmap = folium.Map(location=center, zoom_start=8, tiles="OpenStreetMap")

        if show_heatmap:
            heat_points = stops[["lat", "lon", "dwell_min"]].values.tolist()
            HeatMap(heat_points, radius=14, blur=20, min_opacity=0.3).add_to(fmap)

        if show_markers:
            cluster = MarkerCluster().add_to(fmap)
            for _, row in stops.iterrows():
                popup = folium.Popup(
                    html=(
                        f"<b>{row['locatie_naam'] or '(onbekend)'}</b><br>"
                        f"{row['adres'] or ''}<br>"
                        f"Wagen: {row['wagencode']}<br>"
                        f"Datum: {row['trip_date'].date() if pd.notna(row['trip_date']) else ''}<br>"
                        f"Actie: {row['acties']}<br>"
                        f"Standtijd: {int(row['dwell_min'])} min"
                    ),
                    max_width=280,
                )
                folium.CircleMarker(
                    location=[row["lat"], row["lon"]],
                    radius=4,
                    color="#1f77b4",
                    fill=True,
                    fill_opacity=0.7,
                    popup=popup,
                ).add_to(cluster)

        routes: dict = {}
        need_osrm = (show_routes and use_road_routes) or show_road_heatmap
        if need_osrm:
            segs = unique_segments(stops)
            st.info(
                f"Ophalen wegroute voor {len(segs)} unieke segmenten "
                "(gecached voor volgende keer)."
            )
            progress = st.progress(0.0)

            def _cb(i: int, total: int) -> None:
                progress.progress(min(1.0, i / max(total, 1)))

            routes = fetch_routes(segs, progress_cb=_cb)
            progress.empty()

        if show_routes and use_road_routes:
            with st.spinner("Wegvlakken wegen en kleuren..."):
                edges = compute_weighted_edges(stops, routes)
            if edges:
                max_n = max(n for _, _, n in edges)
                kept = [(p1, p2, n) for p1, p2, n in edges if n >= road_threshold]
                if not kept:
                    st.warning(
                        f"Geen wegvlak heeft ≥ {road_threshold} unieke wagens "
                        f"(max in dataset = {max_n}). Zet de drempel lager."
                    )
                denom = max(1, max_n - road_threshold)
                for p1, p2, n in kept:
                    t = (n - road_threshold) / denom
                    color = lerp_hex("#ddd6fe", "#4c1d95", t)
                    weight = 1.2 + 4.8 * t
                    folium.PolyLine(
                        [p1, p2],
                        color=color,
                        weight=weight,
                        opacity=0.85,
                        tooltip=f"{n} unieke wagens",
                    ).add_to(fmap)
        elif show_routes:
            for _, g in stops.groupby(["wagencode", "trip_date", "trip_id"]):
                if len(g) < 2:
                    continue
                coords = g[["lat", "lon"]].values.tolist()
                folium.PolyLine(
                    coords, color="#6b21a8", weight=2, opacity=0.55
                ).add_to(fmap)

        road_heat_points: list[tuple[float, float, float]] = []
        if show_road_heatmap:
            with st.spinner("Wegvlak-heatmap berekenen..."):
                all_points = compute_road_heatmap_points(stops, routes)
            road_heat_points = [p for p in all_points if p[2] >= road_threshold]
            if road_heat_points:
                HeatMap(
                    road_heat_points,
                    radius=8,
                    blur=12,
                    min_opacity=0.35,
                    gradient={"0.3": "#3b82f6", "0.6": "#f59e0b", "0.9": "#ef4444"},
                ).add_to(fmap)
            elif all_points:
                max_n = int(max(p[2] for p in all_points))
                st.warning(
                    f"Geen wegvlak-cel heeft ≥ {road_threshold} unieke wagens "
                    f"(max in dataset = {max_n}). Zet de drempel lager."
                )

        if show_chargers and not chargers_df.empty:
            for _, c in chargers_df.iterrows():
                popup = folium.Popup(
                    html=(
                        f"<b>{c['name'] or '(naamloos)'}</b><br>"
                        f"{c['operator'] or ''}<br>"
                        f"{c['address']}, {c['postcode']} {c['town']}<br>"
                        f"Max vermogen: {int(c['max_power_kw'])} kW<br>"
                        f"Connectoren: {int(c['n_connectors'])}"
                    ),
                    max_width=280,
                )
                folium.Marker(
                    location=[c["lat"], c["lon"]],
                    icon=folium.Icon(color="green", icon="bolt", prefix="fa"),
                    tooltip=f"⚡ {c['name'] or 'lader'} — {int(c['max_power_kw'])} kW",
                    popup=popup,
                ).add_to(fmap)
            st.caption(
                f"🟢 Groene bliksem-markers = {len(chargers_df)} publieke laders "
                f"≥ {charger_min_kw} kW (bron: Open Charge Map). "
                "Klik op een marker voor details."
            )
        st_folium(fmap, height=620, use_container_width=True, returned_objects=[])

    with tab_dash:
        _render_dashboard(stops, chargers_df, road_threshold)


if __name__ == "__main__":
    main()

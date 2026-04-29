"""Pre-compute zware aggregaties op de full-year data, save naar parquet.

Compute one-off `compute_weighted_edges` en `compute_road_heatmap_points`
op de hele dataset. Resultaten cachen in `.cache/agg_*_full.parquet`.

App.py probeert deze parquet bestanden eerst te laden bij "geen filters"
state — dat scheelt 3-5 min compute bij elke streamlit-restart.

Run:
    .venv/bin/python scripts/precompute_aggregations.py
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import gc

from src.road_usage import _segment_wagen_sets  # noqa: E402
from src.routing import load_cached_routes, unique_segments  # noqa: E402

PARQUET = Path(".cache/postnl_csv_Rittendata.parquet")
CACHE_DIR = Path(".cache")

VARIANTS = {
    "full": None,
    "eigen": "eigen",
    "charter": "charter",
}


def _chunked_segment_wagens(
    df_full: pd.DataFrame, vervoerder: str | None
) -> dict[tuple, set[str]]:
    """Process per maand om RAM-pieken te vermijden, accumuleer segment→wagen-sets."""
    df = df_full
    if vervoerder is not None:
        df = df[df["vervoerder_type"] == vervoerder]

    if df.empty:
        return {}

    df = df.assign(_month=df["trip_date"].dt.to_period("M"))
    months = sorted(df["_month"].unique())

    accum: dict[tuple, set[str]] = {}
    for m in months:
        chunk = df[df["_month"] == m]
        t0 = time.time()
        seg_w = _segment_wagen_sets(chunk)
        for k, ws in seg_w.items():
            accum.setdefault(k, set()).update(ws)
        print(
            f"    {m}: {len(chunk):,} stops, {len(seg_w):,} segs "
            f"({time.time() - t0:.0f}s, accum={len(accum):,})",
            flush=True,
        )
        del chunk, seg_w
        gc.collect()

    return accum


def _attribute_polylines(
    seg_wagens: dict[tuple, set[str]],
    routes: dict,
    edge_decimals: int = 6,
    cell_decimals: int = 3,
) -> tuple[list, list]:
    """Walk polylines once per segment; attribute wagens to micro-edges en grid-cellen."""
    edge_wagens: dict[tuple, set[str]] = {}
    cell_wagens: dict[tuple[float, float], set[str]] = {}

    for seg_key, wagens in seg_wagens.items():
        poly = routes.get(seg_key)
        if not poly:
            poly = [(seg_key[0], seg_key[1]), (seg_key[2], seg_key[3])]
        seen_cells: set[tuple[float, float]] = set()
        for i in range(len(poly) - 1):
            p1 = (round(poly[i][0], edge_decimals), round(poly[i][1], edge_decimals))
            p2 = (
                round(poly[i + 1][0], edge_decimals),
                round(poly[i + 1][1], edge_decimals),
            )
            if p1 != p2:
                edge_wagens.setdefault((p1, p2), set()).update(wagens)
            cell = (round(poly[i][0], cell_decimals), round(poly[i][1], cell_decimals))
            if cell not in seen_cells:
                seen_cells.add(cell)
                cell_wagens.setdefault(cell, set()).update(wagens)
        last_cell = (
            round(poly[-1][0], cell_decimals),
            round(poly[-1][1], cell_decimals),
        )
        if last_cell not in seen_cells:
            cell_wagens.setdefault(last_cell, set()).update(wagens)

    edges = [(p1, p2, len(w)) for (p1, p2), w in edge_wagens.items()]
    cells = [(lat, lon, float(len(w))) for (lat, lon), w in cell_wagens.items()]
    return edges, cells


def _process_variant(df_full: pd.DataFrame, routes: dict, variant: str) -> None:
    edges_out = CACHE_DIR / f"agg_weighted_edges_{variant}.parquet"
    heat_out = CACHE_DIR / f"agg_road_heatmap_{variant}.parquet"

    vervoerder = VARIANTS[variant]

    print(
        f"[{time.strftime('%H:%M:%S')}] === Variant '{variant}' ===",
        flush=True,
    )

    t0 = time.time()
    print(f"  Maand-chunks → segment-wagen sets...", flush=True)
    seg_wagens = _chunked_segment_wagens(df_full, vervoerder)
    if not seg_wagens:
        print(f"  Geen data voor '{variant}', skip.", flush=True)
        return
    print(
        f"  {len(seg_wagens):,} unieke segmenten in {time.time() - t0:.0f}s",
        flush=True,
    )

    t0 = time.time()
    edges, cells = _attribute_polylines(seg_wagens, routes)
    print(
        f"  Polyline-attributie: {len(edges):,} edges + {len(cells):,} cells "
        f"in {time.time() - t0:.0f}s",
        flush=True,
    )

    edges_df = pd.DataFrame(
        [
            {"lat1": p1[0], "lon1": p1[1], "lat2": p2[0], "lon2": p2[1], "n_wagens": n}
            for p1, p2, n in edges
        ]
    )
    edges_df.to_parquet(edges_out, index=False)
    print(
        f"    → {edges_out.name} ({edges_out.stat().st_size / 1024**2:.1f} MB)",
        flush=True,
    )

    heat_df = pd.DataFrame(cells, columns=["lat", "lon", "weight"])
    heat_df.to_parquet(heat_out, index=False)
    print(
        f"    → {heat_out.name} ({heat_out.stat().st_size / 1024**2:.1f} MB)",
        flush=True,
    )

    del seg_wagens, edges, cells, edges_df, heat_df
    gc.collect()


def main() -> None:
    if not PARQUET.exists():
        print(f"Geen dataset op {PARQUET}", flush=True)
        sys.exit(1)

    print(f"[{time.strftime('%H:%M:%S')}] Inlezen {PARQUET}...", flush=True)
    df = pd.read_parquet(PARQUET)
    print(f"  {len(df):,} stops, {df['trip_id'].nunique():,} trips", flush=True)

    print(f"[{time.strftime('%H:%M:%S')}] Unieke segmenten...", flush=True)
    segs = unique_segments(df)
    routes, missing = load_cached_routes(segs)
    print(
        f"  {len(routes):,} cached, {missing} missing "
        f"(missing → straight-line fallback)",
        flush=True,
    )

    grand_start = time.time()
    for variant in VARIANTS:
        _process_variant(df, routes, variant)

    print(
        f"[{time.strftime('%H:%M:%S')}] Alle varianten klaar in "
        f"{time.time() - grand_start:.0f}s. Backup naar Drive...",
        flush=True,
    )
    try:
        from scripts.sync_cache import backup as sync_backup

        sync_backup()
    except Exception as e:
        print(f"  Backup faalde (lokaal wel veilig): {e}", flush=True)

    print(f"[{time.strftime('%H:%M:%S')}] KLAAR.", flush=True)


if __name__ == "__main__":
    main()

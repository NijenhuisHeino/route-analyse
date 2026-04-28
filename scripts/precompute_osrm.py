"""Pre-fetch OSRM routes voor alle unieke segmenten in de full-year parquet.

Standalone — geen Streamlit. Schrijft incrementeel naar `.cache/osrm_routes_full.parquet`.
Resume-baar: bij interruptie (kernel panic, ctrl-C) pakt-ie op waar hij was.

Run:
    nohup .venv/bin/python scripts/precompute_osrm.py > /tmp/osrm_prefetch.log 2>&1 &

Progress volgen:
    tail -f /tmp/osrm_prefetch.log
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from src.routing import _load_cache, fetch_routes, unique_segments  # noqa: E402

PARQUET = Path(".cache/postnl_csv_Rittendata.parquet")


def _fmt(secs: float) -> str:
    h, rem = divmod(int(secs), 3600)
    m, s = divmod(rem, 60)
    return f"{h:d}h{m:02d}m{s:02d}s" if h else f"{m:d}m{s:02d}s"


def main() -> None:
    if not PARQUET.exists():
        print(f"Geen parquet gevonden op {PARQUET}", flush=True)
        sys.exit(1)

    print(f"[{time.strftime('%H:%M:%S')}] Inlezen {PARQUET}...", flush=True)
    df = pd.read_parquet(PARQUET)
    print(f"  {len(df):,} stops, {df['trip_id'].nunique():,} trips", flush=True)

    print(f"[{time.strftime('%H:%M:%S')}] Unieke segmenten berekenen...", flush=True)
    df["month"] = df["trip_date"].dt.to_period("M")
    months = sorted(df["month"].unique())

    cache = _load_cache()
    print(f"  Al gecached: {len(cache):,}", flush=True)

    grand_start = time.time()
    grand_total_missing = 0

    for month in months:
        month_df = df[df["month"] == month]
        segs = unique_segments(month_df)
        missing = [s for s in segs if s not in cache]
        if not missing:
            print(
                f"[{time.strftime('%H:%M:%S')}] {month}: alle "
                f"{len(segs):,} segmenten al in cache, skip.",
                flush=True,
            )
            continue

        eta = len(missing) * 0.25 / 60
        print(
            f"[{time.strftime('%H:%M:%S')}] {month}: "
            f"{len(missing):,} nieuw / {len(segs):,} totaal — ~{eta:.0f} min",
            flush=True,
        )
        grand_total_missing += len(missing)

        m_start = time.time()
        last_print = m_start

        def _cb(i: int, total: int) -> None:
            nonlocal last_print
            now = time.time()
            if now - last_print > 30 or i == total:
                rate = i / max(1, now - m_start)
                eta_rest = (total - i) / max(0.01, rate)
                print(
                    f"  {time.strftime('%H:%M:%S')} "
                    f"{i}/{total} ({100*i/total:.0f}%) "
                    f"~{rate:.1f} seg/s, eta {_fmt(eta_rest)}",
                    flush=True,
                )
                last_print = now

        try:
            fetch_routes(segs, progress_cb=_cb)
        except KeyboardInterrupt:
            print(f"[{time.strftime('%H:%M:%S')}] Onderbroken — cache bewaard.", flush=True)
            sys.exit(0)
        except Exception as e:
            print(f"[{time.strftime('%H:%M:%S')}] Fout in {month}: {e} — door.", flush=True)
            continue

        cache = _load_cache()
        print(
            f"[{time.strftime('%H:%M:%S')}] {month} klaar in "
            f"{_fmt(time.time() - m_start)}, totaal cache: {len(cache):,}",
            flush=True,
        )

    print(
        f"[{time.strftime('%H:%M:%S')}] KLAAR — "
        f"{grand_total_missing:,} nieuwe segmenten in "
        f"{_fmt(time.time() - grand_start)}.",
        flush=True,
    )


if __name__ == "__main__":
    main()

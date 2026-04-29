"""Backup/restore voor `.cache/` parquet-bestanden naar Google Drive.

Drive-backuplocatie: `Route analyse tool/cache-backup/` in de PostNL-projectmap.

Gebruik:
    .venv/bin/python scripts/sync_cache.py backup
    .venv/bin/python scripts/sync_cache.py restore
    .venv/bin/python scripts/sync_cache.py status

Backup wordt automatisch aangeroepen aan het einde van precompute_osrm.py.
Restore mag je handmatig aanroepen na een herinstallatie / nieuwe Mac.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

LOCAL_CACHE = Path(".cache")
DRIVE_CACHE = Path(
    "/Users/johnnynijenhuis/Library/CloudStorage/"
    "GoogleDrive-info@nijenhuistrucksolutions.nl/Mijn Drive/"
    "Nijenhuis Truck Solutions/Bedrijven/Den Haag/PostNL/Project/"
    "Data analyse ritten/Route analyse tool/cache-backup"
)


def _human(size: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024:
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"


def _list_parquets(directory: Path) -> list[Path]:
    if not directory.exists():
        return []
    return sorted(p for p in directory.glob("*.parquet"))


def _copy_if_newer(src: Path, dst: Path) -> tuple[bool, str]:
    if not dst.exists():
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        return True, f"new ({_human(src.stat().st_size)})"
    src_m = src.stat().st_mtime
    dst_m = dst.stat().st_mtime
    if src_m > dst_m + 1:
        shutil.copy2(src, dst)
        return True, f"updated ({_human(src.stat().st_size)})"
    return False, "unchanged"


def backup() -> None:
    print(f"Backup: {LOCAL_CACHE} → {DRIVE_CACHE}")
    if not LOCAL_CACHE.exists():
        print("  Geen lokale cache gevonden.")
        return
    if not DRIVE_CACHE.parent.exists():
        print(f"  Drive-locatie niet bereikbaar: {DRIVE_CACHE.parent}")
        sys.exit(1)
    DRIVE_CACHE.mkdir(parents=True, exist_ok=True)
    files = _list_parquets(LOCAL_CACHE)
    if not files:
        print("  Geen parquet-bestanden om te backuppen.")
        return
    copied = 0
    for src in files:
        dst = DRIVE_CACHE / src.name
        changed, msg = _copy_if_newer(src, dst)
        marker = "✓" if changed else "·"
        print(f"  {marker} {src.name}: {msg}")
        if changed:
            copied += 1
    print(f"Klaar: {copied}/{len(files)} bestanden bijgewerkt.")


def restore() -> None:
    print(f"Restore: {DRIVE_CACHE} → {LOCAL_CACHE}")
    if not DRIVE_CACHE.exists():
        print(f"  Geen backup gevonden in {DRIVE_CACHE}.")
        sys.exit(1)
    LOCAL_CACHE.mkdir(parents=True, exist_ok=True)
    files = _list_parquets(DRIVE_CACHE)
    if not files:
        print("  Backup-map is leeg.")
        return
    copied = 0
    for src in files:
        dst = LOCAL_CACHE / src.name
        changed, msg = _copy_if_newer(src, dst)
        marker = "✓" if changed else "·"
        print(f"  {marker} {src.name}: {msg}")
        if changed:
            copied += 1
    print(f"Klaar: {copied}/{len(files)} bestanden hersteld.")


def status() -> None:
    print(f"Lokale cache: {LOCAL_CACHE.resolve() if LOCAL_CACHE.exists() else 'ontbreekt'}")
    for p in _list_parquets(LOCAL_CACHE):
        print(f"  {p.name}: {_human(p.stat().st_size)}")
    print()
    print(f"Drive-backup:  {DRIVE_CACHE if DRIVE_CACHE.exists() else 'ontbreekt'}")
    for p in _list_parquets(DRIVE_CACHE):
        print(f"  {p.name}: {_human(p.stat().st_size)}")


def main() -> None:
    cmd = sys.argv[1] if len(sys.argv) > 1 else "status"
    {
        "backup": backup,
        "restore": restore,
        "status": status,
    }.get(cmd, status)()


if __name__ == "__main__":
    main()

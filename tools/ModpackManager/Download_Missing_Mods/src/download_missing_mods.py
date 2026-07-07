#!/usr/bin/env python3
"""
HS2 Missing Mods Downloader v3.0
================================
Download missing .zipmod files from the BetterRepack Sideloader CDN based on a
ModResolver missing_mods_report. Designed to be placed in the ModResolver Portable
directory — auto-detects reports, caches, and game paths.

Features:
  - Default essential-only mode (character mods; use --all for everything)
  - Resume: skips files already on disk
  - Priority: most-referenced mods downloaded first
  - Auto-detects aria2c for accelerated downloads
  - Adaptive rate limiting + exponential backoff retry
  - Progress display with ETA
  - 0 pip dependencies (Python 3.8+ stdlib only)

Requirements:  Python 3.8+   |   Optional: aria2c (winget install aria2.aria2)
"""

import argparse
import json
import os
import sys
import time
import urllib.request
import urllib.error
import urllib.parse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from pathlib import Path
from typing import Dict, List, Optional, Set, Tuple
import subprocess
import shutil

VERSION = "3.0.0"

# ──────────────────────────────────────────────────────────────
# Constants — paths resolved relative to this script's / exe's location
# ──────────────────────────────────────────────────────────────
def _get_app_dir() -> Path:
    """Get the directory containing this script or executable.
    Works both as plain .py and as PyInstaller-frozen .exe."""
    if getattr(sys, "frozen", False):
        # PyInstaller: sys.executable is the .exe path
        return Path(sys.executable).resolve().parent
    else:
        return Path(__file__).resolve().parent

SCRIPT_DIR = _get_app_dir()
MODRESOLVER_DIR = SCRIPT_DIR / ".modresolver"
DEFAULT_REPORT_DIR = MODRESOLVER_DIR / "reports"
DEFAULT_CRAWL_CACHE = MODRESOLVER_DIR / "crawl_cache" / "AISHS2_urls.json"
DEFAULT_CONFIG = MODRESOLVER_DIR / "config.json"
DEFAULT_PROGRESS = MODRESOLVER_DIR / "download_progress.json"
DEFAULT_ARIA2_INPUT = MODRESOLVER_DIR / "aria2_input.txt"
DEFAULT_ARIA2_SESSION = MODRESOLVER_DIR / "aria2_session.txt"
DEFAULT_ARIA2_LOG = MODRESOLVER_DIR / "aria2_log.txt"

# Download settings
MAX_WORKERS = 8          # concurrent Python downloads (keep moderate to avoid 429)
MAX_RETRIES = 5          # per-file retry count
BASE_BACKOFF = 5.0       # seconds, doubles each retry
REQUEST_TIMEOUT = 120    # seconds per request
ARIA2_CONCURRENT = 8     # aria2c concurrent downloads (conservative)
ARIA2_SPLIT = 2          # connections per file (conservative)
USER_AGENT = "ModResolver-Downloader/3.0"

# Category groups: "essential" = character card dependencies (clothes/hair/body),
# "optional" = studio props / maps (not needed for character display)
CATEGORY_GROUPS = {
    "essential": [
        "Sideloader Modpack",
        "Sideloader Modpack - Bleeding Edge",
        "Sideloader Modpack - Exclusive HS2",
        "Sideloader Modpack - Exclusive AIS",
    ],
    "optional": [
        "Sideloader Modpack - Studio",
        "Sideloader Modpack - Maps",
        "Sideloader Modpack - Maps (HS2 Game)",
    ],
}


def filter_tasks(tasks, essential_only=False, only_cats=None, skip_cats=None):
    """Filter download tasks by category constraints."""
    if essential_only:
        essential = set(CATEGORY_GROUPS["essential"])
        tasks = [t for t in tasks if t["category"] in essential]

    if only_cats:
        allowed = set(c.strip() for c in only_cats.split(","))
        tasks = [t for t in tasks if t["category"] in allowed]

    if skip_cats:
        excluded = set(c.strip() for c in skip_cats.split(","))
        tasks = [t for t in tasks if t["category"] not in excluded]

    return tasks


# ──────────────────────────────────────────────────────────────
# URL utilities
# ──────────────────────────────────────────────────────────────

def fix_url(raw_url: str) -> str:
    """Properly URL-encode a partially-encoded URL from the crawl cache."""
    parsed = urllib.parse.urlparse(raw_url)
    segments = parsed.path.split("/")
    encoded = [urllib.parse.quote(urllib.parse.unquote(s), safe="") for s in segments]
    return parsed._replace(path="/".join(encoded)).geturl()


# ──────────────────────────────────────────────────────────────
# Data loading
# ──────────────────────────────────────────────────────────────

def find_latest_report(report_dir: Path) -> Optional[Path]:
    """Find the most recent missing_mods_report JSON file."""
    if not report_dir.exists():
        return None
    reports = sorted(report_dir.glob("missing_mods_report_*.json"), reverse=True)
    return reports[0] if reports else None


def load_game_mods_dir(config_path: Path) -> Optional[Path]:
    """Extract the game mods directory from ModResolver config."""
    if not config_path.exists():
        return None
    try:
        with open(config_path, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        instances = cfg.get("instances", [])
        if instances:
            return Path(instances[0]["path"]) / "mods"
    except Exception:
        pass
    return None


def load_data(report_path: Path, cache_path: Path):
    """Load report + crawl cache and return matched download tasks."""
    with open(cache_path, "r", encoding="utf-8") as f:
        cache = json.load(f)

    filename_to_url: Dict[str, str] = {}
    for entry in cache["urls"]:
        fn = entry.get("filename", "")
        filename_to_url[fn] = entry.get("url", "")

    with open(report_path, "r", encoding="utf-8") as f:
        report = json.load(f)

    tasks = []
    unmatched = []
    for item in report["items"]:
        if item["status"] != "downloadable":
            continue
        fn = item.get("filename", "")
        if fn in filename_to_url:
            tasks.append({
                "guid": item["guid"],
                "name": item["name"],
                "author": item.get("author", ""),
                "filename": fn,
                "file_size": item.get("file_size", 0),
                "category": item["category"],
                "url": filename_to_url[fn],
                "card_count": item.get("card_count", 0),
            })
        else:
            unmatched.append(fn)

    return tasks, unmatched


# ──────────────────────────────────────────────────────────────
# aria2c detection
# ──────────────────────────────────────────────────────────────

def find_aria2c() -> Optional[str]:
    """Locate aria2c on the system. Returns path or None."""
    aria2 = shutil.which("aria2c")
    if aria2:
        return aria2
    # Common Windows install locations
    candidates = [
        Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft/WinGet/Links/aria2c.exe",
        Path("C:/Program Files/aria2/aria2c.exe"),
        Path(os.environ.get("USERPROFILE", "")) / "scoop/shims/aria2c.exe",
    ]
    for p in candidates:
        if p.exists():
            return str(p)
    return None


ARIA2_INSTALL_HINTS = {
    "winget": "winget install aria2.aria2",
    "scoop": "scoop install aria2",
    "choco": "choco install aria2",
    "manual": "https://github.com/aria2/aria2/releases (download aria2-*-win-64bit-build1.zip)",
}


def check_aria2c() -> Tuple[Optional[str], List[str]]:
    """Check for aria2c. Returns (path, install_hints)."""
    path = find_aria2c()
    if path:
        return path, []

    hints = []
    for manager, cmd in ARIA2_INSTALL_HINTS.items():
        if shutil.which(manager.split()[0]):
            hints.append(f"  {cmd}")
    if not hints:
        hints.append(f"  {ARIA2_INSTALL_HINTS['manual']}")
    return None, hints


# ──────────────────────────────────────────────────────────────
# Download logic (Python backend)
# ──────────────────────────────────────────────────────────────

def format_size(size_bytes: int) -> str:
    if size_bytes >= 1024 ** 3:
        return f"{size_bytes / (1024**3):.1f} GB"
    if size_bytes >= 1024 ** 2:
        return f"{size_bytes / (1024**2):.1f} MB"
    if size_bytes >= 1024:
        return f"{size_bytes / 1024:.1f} KB"
    return f"{size_bytes} B"


def download_one(url: str, dest: Path, expected_size: int = 0) -> Tuple[bool, int, str]:
    """Download a single file with retry + backoff. Returns (ok, bytes, error)."""
    dest.parent.mkdir(parents=True, exist_ok=True)

    # Skip if already downloaded with correct size
    if dest.exists():
        actual = dest.stat().st_size
        if expected_size == 0 or actual == expected_size:
            return True, actual, "skipped (exists)"
        # Size mismatch — re-download
        print(f"  [!] Size mismatch for {dest.name}: expected {expected_size}, got {actual}")

    fixed_url = fix_url(url)
    last_error = "unknown"

    for attempt in range(1, MAX_RETRIES + 1):
        try:
            req = urllib.request.Request(fixed_url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
                data = resp.read()

            dest.write_bytes(data)
            actual = len(data)

            if expected_size > 0 and actual != expected_size:
                msg = f"size mismatch: expected {expected_size}, got {actual}"
                if attempt < MAX_RETRIES:
                    time.sleep(BASE_BACKOFF * (2 ** (attempt - 1)))
                    continue
                return False, actual, msg

            return True, actual, ""

        except urllib.error.HTTPError as e:
            last_error = f"HTTP {e.code}"
            if e.code in (429, 503):
                delay = BASE_BACKOFF * (2 ** attempt)  # longer backoff for rate limits
                print(f"  [!] Rate limited, waiting {delay:.0f}s...")
                time.sleep(delay)
            elif e.code == 404:
                return False, 0, f"HTTP 404 (not found on server)"
            elif attempt < MAX_RETRIES:
                time.sleep(BASE_BACKOFF * (2 ** (attempt - 1)))

        except (urllib.error.URLError, OSError, TimeoutError) as e:
            last_error = str(e)[:100]
            if attempt < MAX_RETRIES:
                delay = BASE_BACKOFF * (2 ** (attempt - 1))
                time.sleep(delay)

    return False, 0, last_error


# ──────────────────────────────────────────────────────────────
# Progress persistence
# ──────────────────────────────────────────────────────────────

def load_progress(path: Path) -> Tuple[Set[str], Dict[str, str]]:
    if path.exists():
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return set(data.get("completed_filenames", [])), data.get("failed", {})
    return set(), {}


def save_progress(path: Path, completed: Set[str], failed: Dict[str, str],
                  total: int, finish: bool = False):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "last_updated": datetime.now().isoformat(),
        "completed_count": len(completed),
        "total_count": total,
        "completed_filenames": sorted(completed),
        "failed": failed,
    }
    if finish:
        payload["finished_at"] = datetime.now().isoformat()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)


# ──────────────────────────────────────────────────────────────
# Main download orchestrator
# ──────────────────────────────────────────────────────────────

def run_python_download(tasks: List[dict], mods_dir: Path, progress_path: Path,
                        workers: int):
    """Download using Python urllib with thread pool."""
    completed, failed = load_progress(progress_path)
    total = len(tasks)

    # Filter out already-completed files (from progress file)
    pending = [t for t in tasks if t["filename"] not in completed]

    # Also skip files already on disk with correct size
    truly_pending = []
    for t in pending:
        dest = mods_dir / t["category"] / t["filename"]
        if dest.exists() and (t["file_size"] == 0 or dest.stat().st_size == t["file_size"]):
            completed.add(t["filename"])
        else:
            truly_pending.append(t)
    pending = truly_pending

    if not pending:
        print("\n[OK] All mods already downloaded!")
        return

    # Sort by priority: card_count desc, then file_size asc
    pending.sort(key=lambda x: (-x["card_count"], x["file_size"]))

    print(f"\nTotal: {total} | Already done: {total - len(pending)} | Remaining: {len(pending)}")
    remaining_size = sum(t["file_size"] for t in pending)
    print(f"Remaining size: {format_size(remaining_size)}")
    print(f"Workers: {workers}\n")

    downloaded_bytes = 0
    success_count = 0
    fail_count = 0
    start_time = time.time()

    def task_fn(t):
        dest = mods_dir / t["category"] / t["filename"]
        ok, size, err = download_one(t["url"], dest, t["file_size"])
        return t, ok, size, err

    with ThreadPoolExecutor(max_workers=workers) as executor:
            fs = {executor.submit(task_fn, t): t for t in pending}
            try:
                for future in as_completed(fs):
                    task, ok, size, err = future.result()
                    if ok:
                        success_count += 1
                        downloaded_bytes += size
                        completed.add(task["filename"])
                        failed.pop(task["filename"], None)
                    else:
                        fail_count += 1
                        failed[task["filename"]] = err

                    done = success_count + fail_count
                    pct = done / len(pending) * 100
                    elapsed = max(time.time() - start_time, 0.1)
                    speed = downloaded_bytes / elapsed

                    if success_count > 0 and done > 0 and speed > 0:
                        avg = downloaded_bytes / success_count
                        eta_s = (len(pending) - done) * avg / speed
                        if eta_s > 3600:
                            eta = f"ETA {eta_s/3600:.1f}h"
                        elif eta_s > 60:
                            eta = f"ETA {eta_s/60:.0f}m"
                        else:
                            eta = f"ETA {eta_s:.0f}s"
                    else:
                        eta = ""

                    tag = "OK" if ok else f"FAIL:{err[:30]}"
                    print(f"[{done:4d}/{len(pending)}] {pct:5.1f}% [{tag:20s}] "
                          f"{format_size(size):>8s} @{format_size(int(speed)):>8s}/s {eta:15s} "
                          f"{task['filename'][:60]}")

                    if done % 25 == 0:
                        save_progress(progress_path, completed, failed, total)

            except KeyboardInterrupt:
                print(f"\n[!] Interrupted at {success_count}/{len(pending)}. Progress saved — re-run to resume.")
                save_progress(progress_path, completed, failed, total)
                executor.shutdown(wait=False, cancel_futures=True)
                raise

    # Final save
    save_progress(progress_path, completed, failed, total, finish=True)

    elapsed = time.time() - start_time
    print(f"\n{'='*60}")
    print(f"DONE in {elapsed/60:.1f}m — {success_count} ok, {fail_count} failed")
    print(f"Downloaded: {format_size(downloaded_bytes)} @ {format_size(int(downloaded_bytes/elapsed))}/s")
    if failed:
        print(f"Failed ({len(failed)}):")
        for fn, err in list(failed.items())[:15]:
            print(f"  {fn}: {err}")
        if len(failed) > 15:
            print(f"  ... and {len(failed) - 15} more")
    print(f"{'='*60}")


def run_aria2_download(tasks: List[dict], mods_dir: Path, progress_path: Path,
                       aria2_path: str):
    """Download using aria2c for maximum speed."""
    completed, failed = load_progress(progress_path)
    total = len(tasks)

    # Filter completed
    pending = [t for t in tasks if t["filename"] not in completed]

    # Skip disk-existing
    truly_pending = []
    for t in pending:
        dest = mods_dir / t["category"] / t["filename"]
        if dest.exists() and (t["file_size"] == 0 or dest.stat().st_size == t["file_size"]):
            completed.add(t["filename"])
        else:
            truly_pending.append(t)
    pending = truly_pending

    if not pending:
        print("\n[OK] All mods already downloaded!")
        return

    # Sort by priority
    pending.sort(key=lambda x: (-x["card_count"], x["file_size"]))

    print(f"\nTotal: {total} | Already done: {total - len(pending)} | Remaining: {len(pending)}")
    remaining_size = sum(t["file_size"] for t in pending)
    print(f"Remaining size: {format_size(remaining_size)}")

    # Generate aria2c input file
    count = 0
    with open(DEFAULT_ARIA2_INPUT, "w", encoding="utf-8") as f:
        for t in pending:
            fixed = fix_url(t["url"])
            d = mods_dir / t["category"]
            f.write(f"{fixed}\n")
            f.write(f"  out={t['filename']}\n")
            f.write(f"  dir={d}\n")
            f.write("\n")
            count += 1

    print(f"Wrote {count} URLs to {DEFAULT_ARIA2_INPUT}")
    print(f"Starting aria2c with {ARIA2_CONCURRENT} concurrent, {ARIA2_SPLIT} split...")
    print(f"(aria2c progress display is different from Python mode)\n")

    # Build aria2c command
    cmd = [
        aria2_path,
        f"--input-file={DEFAULT_ARIA2_INPUT}",
        f"--max-concurrent-downloads={ARIA2_CONCURRENT}",
        f"--max-connection-per-server={ARIA2_CONCURRENT}",
        f"--split={ARIA2_SPLIT}",
        "--min-split-size=50M",
        "--continue=true",
        "--file-allocation=none",
        "--console-log-level=notice",
        f"--log-level=info",
        f"--log={DEFAULT_ARIA2_LOG}",
        f"--save-session={DEFAULT_ARIA2_SESSION}",
        "--save-session-interval=30",
        f"--user-agent={USER_AGENT}",
        "--connect-timeout=30",
        "--timeout=120",
        "--max-tries=5",
        "--retry-wait=10",
        "--summary-interval=60",
        "--auto-file-renaming=false",
        "--allow-overwrite=false",
    ]

    print(f"Command: {' '.join(cmd[:5])} ...\n")
    try:
        rc = subprocess.run(cmd).returncode
    except KeyboardInterrupt:
        print("\n[!] Interrupted. aria2c session saved — resume with same command.")
        rc = 1

    # Scan disk for newly downloaded files
    new_done = 0
    for t in pending:
        dest = mods_dir / t["category"] / t["filename"]
        if dest.exists() and (t["file_size"] == 0 or dest.stat().st_size == t["file_size"]):
            completed.add(t["filename"])
            failed.pop(t["filename"], None)
            new_done += 1
        elif not dest.exists():
            failed[t["filename"]] = "not found after aria2c run"

    save_progress(progress_path, completed, failed, total, finish=True)

    # Count remaining
    remaining = sum(1 for t in tasks if t["filename"] not in completed)
    print(f"\n{'='*60}")
    print(f"aria2c exit code: {rc}")
    print(f"Newly downloaded this session: {new_done}")
    print(f"Total completed: {len(completed)}/{total}")
    print(f"Still missing: {remaining}")
    if remaining > 0 and rc == 0:
        # aria2c may have skipped some — rerun in python mode for stragglers
        print(f"Run again to retry missing files with Python backend.")
    print(f"{'='*60}")


# ──────────────────────────────────────────────────────────────
# CLI
# ──────────────────────────────────────────────────────────────

def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="download_missing_mods",
        description="Download missing HS2/AIS mods from BetterRepack Sideloader CDN.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  %(prog)s                           essential-only (default)
  %(prog)s --all                     download everything incl. Studio/Maps
  %(prog)s --skip "Maps"            essential + Studio, skip Maps
  %(prog)s --only "Sideloader Modpack"  download one category only
  %(prog)s -w 4                      4 concurrent downloads
  %(prog)s --aria2c                  use aria2c accelerator
  %(prog)s --dry-run                 show what would be downloaded, with category breakdown
  %(prog)s -r report_20260707.json   use a specific report
  %(prog)s -o D:\\HS2\\mods            output to custom directory

Download priority: mods referenced by more character cards come first.
Resume is automatic — re-run the same command to continue where you left off.

Category groups:
  Essential (default): Sideloader Modpack, Bleeding Edge,
                        Exclusive HS2, Exclusive AIS
  Optional (--all):     Studio, Maps, Maps (HS2 Game)
        """,
    )

    p.add_argument(
        "-r", "--report",
        type=Path,
        default=None,
        help="Path to missing_mods_report JSON. Auto-detects latest if omitted.",
    )
    p.add_argument(
        "-o", "--output-dir",
        type=Path,
        default=None,
        help="Game mods/ directory. Auto-detected from ModResolver config if omitted.",
    )
    p.add_argument(
        "-w", "--workers",
        type=int,
        default=MAX_WORKERS,
        help=f"Number of concurrent downloads (default: {MAX_WORKERS}).",
    )
    p.add_argument(
        "--aria2c",
        action="store_true",
        default=False,
        help="Use aria2c for accelerated downloads (auto-detected if installed).",
    )
    p.add_argument(
        "--no-aria2c",
        action="store_true",
        default=False,
        help="Force Python downloader even if aria2c is available.",
    )
    p.add_argument(
        "--dry-run",
        action="store_true",
        default=False,
        help="Show summary only, don't download anything.",
    )
    p.add_argument(
        "--retry-failed",
        action="store_true",
        default=False,
        help="Retry previously failed downloads.",
    )
    p.add_argument(
        "--version", "-V",
        action="version",
        version=f"%(prog)s {VERSION}",
    )

    # Category filtering
    cat_group = p.add_argument_group("category filters")
    cat_group.add_argument(
        "--all",
        action="store_true",
        default=False,
        help="Download ALL mods including optional Studio/Maps categories. "
             "Default is essential-only (character mods only).",
    )
    cat_group.add_argument(
        "--skip",
        type=str,
        default=None,
        metavar="CATS",
        help="Comma-separated categories to skip (e.g. 'Maps,Studio').",
    )
    cat_group.add_argument(
        "--only",
        type=str,
        default=None,
        metavar="CATS",
        help="Comma-separated categories to download (e.g. 'Sideloader Modpack').",
    )
    return p


def main():
    # Fix console encoding for Windows (supports CJK filenames)
    if sys.platform == "win32":
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    parser = build_parser()
    args = parser.parse_args()

    # ── Resolve paths ──────────────────────────────────────
    report_path = args.report or find_latest_report(DEFAULT_REPORT_DIR)
    if not report_path or not report_path.exists():
        print(f"ERROR: No report found. Specify with --report or place .modresolver/reports/ beside this script.")
        print(f"  Looked in: {DEFAULT_REPORT_DIR}")
        sys.exit(1)

    cache_path = DEFAULT_CRAWL_CACHE
    if not cache_path.exists():
        print(f"ERROR: Crawl cache not found: {cache_path}")
        sys.exit(1)

    mods_dir = args.output_dir or load_game_mods_dir(DEFAULT_CONFIG)
    if not mods_dir:
        print(f"ERROR: Could not determine mods directory. Specify with --output-dir.")
        sys.exit(1)

    progress_path = DEFAULT_PROGRESS

    print(f"{'='*60}")
    print(f"HS2 Missing Mods Downloader v3.0")
    print(f"{'='*60}")
    print(f"Report:   {report_path}")
    print(f"Cache:    {cache_path}")
    print(f"Target:   {mods_dir}")
    print()

    # ── Load data ──────────────────────────────────────────
    tasks, unmatched = load_data(report_path, cache_path)
    if unmatched:
        print(f"WARNING: {len(unmatched)} items could not be matched to URLs")

    total_size = sum(t["file_size"] for t in tasks)
    print(f"Downloadable mods: {len(tasks)} ({format_size(total_size)})")

    # ── Apply category filters ───────────────────────────────
    # Default: essential-only (~13 GB). Use --all for everything (~64 GB).
    # --only overrides everything; --skip further restricts the base set.
    essential_only = not args.all  # default True, --all sets False
    if args.only:
        essential_only = False  # --only takes precedence

    tasks = filter_tasks(tasks,
                         essential_only=essential_only,
                         only_cats=args.only,
                         skip_cats=args.skip)
    filter_size = sum(t["file_size"] for t in tasks)

    filters = []
    if essential_only:
        filters.append("essential (default)")
    elif args.only:
        filters.append(f"only={args.only}")
    else:
        filters.append("all")
    if args.skip:
        filters.append(f"skip={args.skip}")
    print(f"After filter ({', '.join(filters)}): {len(tasks)} mods ({format_size(filter_size)})")

    if args.dry_run:
        cats: Dict[str, Tuple[int, int]] = {}
        for t in tasks:
            c = t["category"]
            cnt, sz = cats.get(c, (0, 0))
            cats[c] = (cnt + 1, sz + t["file_size"])
        print("\nBy category:")
        for cat, (cnt, sz) in sorted(cats.items()):
            print(f"  {cat}: {cnt} mods, {format_size(sz)}")
        print("\nTop 10 priority:")
        tasks_sorted = sorted(tasks, key=lambda x: -x["card_count"])
        for t in tasks_sorted[:10]:
            print(f"  [{t['card_count']:3d} cards] {t['category'][:45]:45s} {t['filename'][:60]}")
        return

    # ── Resolve retry-failed ───────────────────────────────
    if args.retry_failed and progress_path.exists():
        completed, failed = load_progress(progress_path)
        if failed:
            # Remove failed entries so they get re-downloaded
            print(f"Retrying {len(failed)} previously failed items...")
            new_failed = {}
            save_progress(progress_path, completed, new_failed, len(tasks))
    elif args.retry_failed:
        print("No progress file found; nothing to retry.")

    # ── Check for aria2c ──────────────────────────────────
    aria2_path, hints = check_aria2c()
    use_aria2 = args.aria2c and not args.no_aria2c

    if use_aria2 and not aria2_path:
        print("\naria2c not found! Install it for faster downloads:")
        for h in hints:
            print(h)
        print("\nFalling back to Python downloader.\n")
        use_aria2 = False

    if args.aria2c and aria2_path:
        print(f"aria2c found: {aria2_path}\n")
    elif not args.no_aria2c and aria2_path:
        print(f"aria2c available ({aria2_path}) — use --aria2c to enable, or --no-aria2c to suppress.\n")

    # ── Download ───────────────────────────────────────────
    if use_aria2 and aria2_path:
        run_aria2_download(tasks, Path(mods_dir), progress_path, aria2_path)
    else:
        run_python_download(tasks, Path(mods_dir), progress_path, args.workers)


if __name__ == "__main__":
    main()

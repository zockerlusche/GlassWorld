"""Schnelldurchlauf (A1743, B2787, Marcel W2156): a world runs headless as fast as the computer can,
with or without the AI, and is then in the game's world list - open it with graphics from the menu.

Usage (from the repository root, the test package's start script does this):
    uv run python tools/schnelldurchlauf/run.py --days 10 --ai off
    uv run python tools/schnelldurchlauf/run.py --seed 643949180 --days 30 --ai on --name Probe

- The world is new: island start_island, size/climate like the menu's defaults, start favour 50.
- It saves after every game day (a stopped run keeps the last full day) and at the end, and writes
  the learning log and the run trace next to the save, like the game.
- Without AI only the processor works; with AI the graphics card works for the AI (B2787).
- It never overwrites a world: an existing name gets a number.
"""

from __future__ import annotations

import argparse
import json
import os
import random
import re
import subprocess
import sys
import time
from datetime import UTC, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "src" / "Simulation.Headless"


def worlds_folder() -> Path:
    """The game's world list (user://worlds of the Godot project GlassWorld)."""
    appdata = os.environ.get("APPDATA")
    if appdata:
        return Path(appdata) / "Godot" / "app_userdata" / "GlassWorld" / "worlds"
    return Path.home() / ".local" / "share" / "godot" / "app_userdata" / "GlassWorld" / "worlds"


RESERVED = {
    "con",
    "prn",
    "aux",
    "nul",
    *(f"com{i}" for i in range(1, 10)),
    *(f"lpt{i}" for i in range(1, 10)),
}


def slug(name: str) -> str:
    """As WorldLibrary.Slug: umlauts spelled out, ASCII letters and digits, '-' between, at most
    32 characters, 'world' when empty, Windows device names get '-world'."""
    text = name.strip().upper()
    for umlaut, spelled in (("Ä", "AE"), ("Ö", "OE"), ("Ü", "UE"), ("ß", "SS")):
        text = text.replace(umlaut, spelled)
    text = "-".join(p for p in re.split(r"[^A-Z0-9]+", text) if p).lower()
    text = text[:32].rstrip("-") if len(text) > 32 else text
    text = text or "world"
    return text + "-world" if text in RESERVED else text


def free_folder(root: Path, name: str) -> Path:
    base = slug(name)
    folder, n = root / base, 2
    while folder.exists():
        folder, n = root / f"{base}-{n}", n + 1
    return folder


def ticks_per_day() -> int:
    """From content/world/time.json (review GW3 #473): ticks per hour x hours per day."""
    data = json.loads((ROOT / "content" / "world" / "time.json").read_text("utf-8-sig"))
    return int(data["ticks_per_hour"]) * int(data["hours_per_day"])


def options_defaults() -> tuple[str, str]:
    data = json.loads((ROOT / "content" / "player" / "world_options.json").read_text("utf-8-sig"))
    return str(data["default_size"]), str(data["default_climate"])


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--days", type=int, default=10, help="game days (default 10)")
    parser.add_argument("--ai", choices=["on", "off"], default="off")
    parser.add_argument("--seed", type=int, default=None, help="world seed (default: drawn)")
    parser.add_argument(
        "--name", default=None, help="world name in the list (default: Schnell <seed>)"
    )
    args = parser.parse_args(argv)
    if args.days < 1:
        print("SCHNELL_FAIL: --days must be at least 1", file=sys.stderr)
        return 1
    seed = args.seed if args.seed is not None else random.randrange(1, 2**31)
    name = args.name or f"Schnell {seed}"
    size, climate = options_defaults()
    folder = free_folder(worlds_folder(), name)
    ai_word = "an" if args.ai == "on" else "aus"
    print(f"Welt '{name}' (Seed {seed}, {args.days} Spieltage, KI {ai_word})")
    print(f"Ordner: {folder}")

    build = subprocess.run(
        ["dotnet", "build", str(PROJECT), "-c", "Release", "-nologo", "-v", "q"], cwd=ROOT
    )
    if build.returncode != 0:
        print("SCHNELL_FAIL: build of Simulation.Headless failed", file=sys.stderr)
        return 2
    # Only after the build (review GW3 #473): a failed build leaves no empty world in the list.
    folder.mkdir(parents=True)
    save = folder / "world.sqlite"
    command = [
        "dotnet", "run", "--project", str(PROJECT), "-c", "Release", "--no-build", "--",
        "--preset", "start_island", "--seed", str(seed), "--climate", climate,
        "--ai", "live" if args.ai == "on" else "mock", "--days", str(args.days), "--quiet",
        "--save", str(save), "--learning-log", str(folder), "--run-trace", str(folder),
    ]  # fmt: skip
    started = time.monotonic()
    with (folder / "lauf.log").open("w", encoding="utf-8") as log:
        run = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
    seconds = time.monotonic() - started
    text = (folder / "lauf.log").read_text(encoding="utf-8", errors="replace")
    final = re.search(r"^FINAL hash=\S+ tick=(\d+)", text, re.MULTILINE)
    if not save.is_file() or final is None:
        log_file = folder / "lauf.log"
        print(f"SCHNELL_FAIL: no save at the end (exit {run.returncode}) - see {log_file}")
        return 3
    tick = int(final.group(1))
    per_day = ticks_per_day()
    meta = {
        "id": folder.name,
        "name": name,
        "seed": seed,
        "size": size,
        "climate": climate,
        "start_favor": 50,
        "day": tick // per_day + 1,  # as the game writes it (Main.cs: tick / TicksPerDay + 1)
        "last_played_utc": datetime.now(UTC).isoformat(),
    }
    (folder / "meta.json").write_text(
        json.dumps(meta, indent=2, ensure_ascii=False) + "\n", "utf-8"
    )
    days = tick / per_day
    per_minute = days / (seconds / 60) if seconds > 0 else 0.0
    minutes = seconds / 60
    print(f"Fertig: {days:.1f} Spieltage in {minutes:.1f} min ({per_minute:.1f} je Minute).")
    print(f"Im Spiel: Hauptmenue -> Welten -> '{name}' laden.")
    print(f"SCHNELL_OK days={days:.1f} seconds={seconds:.0f} per_minute={per_minute:.2f}")
    return 0 if run.returncode == 0 else 4


if __name__ == "__main__":
    sys.exit(main())

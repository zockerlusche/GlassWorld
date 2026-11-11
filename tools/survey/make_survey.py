"""Shot list for the agents' visual survey of an island (Marcel W1757, B2603).

Reads a saved world (SQLite save of the headless runner) and writes a shot set for the island
scene: every object type once from close up, a grid of overviews by day, a few views at night and
in two weathers, a low look at the horizon, and one shot following each inhabitant. The pictures
are taken by the game's own shot tool; this script only chooses where to look.

Usage (from the repository root):
    dotnet run --project src/Simulation.Headless -- --preset start_island --seed 7 --days 1 \
        --quiet --save ../survey/w7.sqlite
    uv run python tools/survey/make_survey.py ../survey/w7.sqlite content/look/shots_survey.json
    <godot> --path game res://world/island.tscn -- --seed=7 --shot-set=survey --shots=<folder>

The shot set belongs to the seed of the save (content/look/shots_survey.json: seed 7, start
island, 192 m); with another seed or map size, write a new one (--extent-m).

A window run belongs in the AMW run queue (`amw lauf start --schirm --gpu --warten`).
"""

from __future__ import annotations

import argparse
import json
import sqlite3
from contextlib import closing
from pathlib import Path
from typing import Any

GRID = (200, 500, 800)
MOODS: tuple[tuple[str, dict[str, str]], ...] = (
    ("night", {"phase": "night"}),
    ("rain", {"phase": "day", "weather": "rain"}),
    ("fog", {"phase": "day", "weather": "fog"}),
)
MOOD_PLACES = ((300, 300), (700, 700), (500, 150))


def permille(value: float, extent_m: float) -> int:
    return max(0, min(1000, round(value / extent_m * 1000)))


def points(save: Path, extent_m: float, close_m: float) -> list[dict[str, Any]]:
    """The survey's shots; object types and inhabitants come from the save, the rest is fixed."""
    found: list[dict[str, Any]] = []
    # Read only: a wrong path fails clearly instead of creating an empty file (review GW3 #217).
    with closing(sqlite3.connect(f"file:{save.resolve().as_posix()}?mode=ro", uri=True)) as db:
        kinds = [row[0] for row in db.execute("select distinct type from objects order by type")]
        for kind in kinds:
            rows = db.execute("select x, z from objects where type = ? order by id", (kind,))
            places = rows.fetchall()
            x, z = places[len(places) // 2]
            found.append(
                {
                    "id": f"obj_{kind}",
                    "target_x_permille": permille(x, extent_m),
                    "target_z_permille": permille(z, extent_m),
                    "yaw_deg": 30,
                    "distance_m": close_m,
                    "pitch_deg": 24,
                    "phase": "day",
                }
            )
        inhabitants = db.execute("select count(*) from inhabitants").fetchone()[0]
    for index in range(inhabitants):
        found.append(
            {
                "id": f"inhabitant_{index + 1}",
                "target_x_permille": 500,
                "target_z_permille": 500,
                "inhabitant": index,
                "yaw_deg": 200,
                "distance_m": 6,
                "pitch_deg": 14,
                "phase": "day",
            }
        )
    for gx in GRID:
        for gz in GRID:
            found.append(
                {
                    "id": f"area_{gx}_{gz}",
                    "target_x_permille": gx,
                    "target_z_permille": gz,
                    "yaw_deg": 30,
                    "distance_m": 60,
                    "pitch_deg": 38,
                    "phase": "day",
                }
            )
    for name, extra in MOODS:
        for gx, gz in MOOD_PLACES:
            found.append(
                {
                    "id": f"{name}_{gx}_{gz}",
                    "target_x_permille": gx,
                    "target_z_permille": gz,
                    "yaw_deg": 120,
                    "distance_m": 35,
                    "pitch_deg": 22,
                    **extra,
                }
            )
    found.append(
        {
            "id": "low_horizon",
            "target_x_permille": 500,
            "target_z_permille": 500,
            "yaw_deg": 300,
            "distance_m": 45,
            "pitch_deg": 8,
            "phase": "day",
        }
    )
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument("save", type=Path, help="SQLite save of the headless runner")
    parser.add_argument("out", type=Path, help="shot set to write (content/look/shots_NAME.json)")
    parser.add_argument("--extent-m", type=float, default=192.0, help="edge length of the map")
    parser.add_argument("--close-m", type=float, default=3.0, help="camera distance to an object")
    options = parser.parse_args(argv)
    shots = {
        "contract_version": "0.2",
        "points": points(options.save, options.extent_m, options.close_m),
        "projections": ["perspective"],
        "phases": ["day", "night"],
        "settle_frames": 12,
    }
    options.out.write_text(json.dumps(shots, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(shots['points'])} shots -> {options.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

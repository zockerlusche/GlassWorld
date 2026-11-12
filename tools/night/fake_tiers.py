"""Forces the AI service into one hardware tier for a check run (A1586, B7 R16, "mit Fake-Stufen").

The service picks the highest tier whose requirements the PC meets (content/hardware/tiers.json,
glassworld_ai.hardware.select_tier). This writes a copy in which the wanted tier and every tier
below it need nothing, and every tier above it needs more than any card has - so on any PC the
service runs in that tier and plans its computing night like it. Only the copy is written; the
repository's data stay as they are.

For check runs only: GW_AI_HARDWARE_TIERS_FILE is an ordinary service setting, so a forced tier
also claims hardware the PC may not have (a model too big for the card loads slowly or fails).

Usage (from the repository root):
    uv run python tools/night/fake_tiers.py minimum OUT.json
    set GW_AI_HARDWARE_TIERS_FILE=OUT.json, then start the game with the live AI as usual
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
TIERS = ROOT / "content" / "hardware" / "tiers.json"

# More VRAM than any card has (MiB): a tier above the wanted one can never be met.
NEVER_MIB = 10**9


def make(tier: str, out: Path, source: Path = TIERS) -> Path:
    """Writes the tiers of ``source`` to ``out`` so that the service selects ``tier`` on any PC."""
    data: dict[str, Any] = json.loads(source.read_text(encoding="utf-8"))
    ids = [t["id"] for t in data["tiers"]]
    if tier not in ids:
        raise SystemExit(f"unknown tier '{tier}' - the data have {', '.join(ids)}")
    wanted = ids.index(tier)
    for index, entry in enumerate(data["tiers"]):
        entry["requires_any"] = [{"min_largest_gpu_vram_mib": 0 if index <= wanted else NEVER_MIB}]
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(
        json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n"
    )
    return out


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=(__doc__ or "").splitlines()[0])
    parser.add_argument(
        "tier", help="tier id from content/hardware/tiers.json (minimum, standard, comfort)"
    )
    parser.add_argument("out", type=Path, help="file to write the forced tiers to")
    options = parser.parse_args(argv)
    out = make(options.tier, options.out)
    print(f"FAKE_TIERS {out} tier={options.tier}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

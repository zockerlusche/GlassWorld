"""Test path for the computing night (A1558): content/ copied, things are asked about at night.

In a live run the language model answers a question about an unknown thing at once by day, so
in the evening no such question is left and the computing night gets no job about a thing type:
the dream scene shows no dream image (A1522, D811: dreams=0). In this copy the trigger
``unknown_object`` asks at night (``ask: night``, a value the decider settings already know,
A1487): by day the question opens with its question mark and waits, the inhabitant looks at the
thing, and the night's question job carries the thing's type - the dream scene shows that thing.

Only the copy is changed; the repository's content stays as it is. The game reads the copy when
the environment variable ``GLASSWORLD_CONTENT`` points at it (``game/scripts/ContentLocator.cs``).

Usage (from the repository root):
    uv run python tools/night/night_test_content.py OUT_DIR
    GLASSWORLD_CONTENT=OUT_DIR GLASSWORLD_AI=live Godot --path game -- --night-shots=SHOTS_DIR

The copy is checked: the decider settings must still validate against their schema, and the
night question list must take ``unknown_object`` (else a waiting question is never answered).
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRIGGER = "unknown_object"


def make(out: Path, source: Path = ROOT / "content") -> Path:
    """Copies ``source`` to the new folder ``out``; questions about things wait for the night."""
    if out.exists():
        raise SystemExit(f"{out} exists already - give a new folder (nothing is overwritten)")
    shutil.copytree(source, out)

    decider_path = out / "decider" / "settings.json"
    decider = json.loads(decider_path.read_text(encoding="utf-8"))
    triggers = [t for t in decider["triggers"] if t["kind"] == TRIGGER]
    if len(triggers) != 1 or not triggers[0].get("enabled", False):
        raise SystemExit(f"decider settings: trigger {TRIGGER} missing or not enabled")
    triggers[0]["ask"] = "night"
    text = json.dumps(decider, indent=2, ensure_ascii=False) + "\n"
    decider_path.write_text(text, encoding="utf-8", newline="\n")

    knowledge = json.loads((out / "knowledge" / "settings.json").read_text(encoding="utf-8"))
    night = knowledge.get("night_questions", {}).get("questions", [])
    if not any(q.get("trigger") == TRIGGER for q in night):
        raise SystemExit(
            f"knowledge settings: night_questions does not take {TRIGGER} - nobody would answer"
        )

    _validate(decider_path, ROOT / "contracts" / "schemas" / "decider-settings.schema.json")
    return out


def _validate(data_path: Path, schema_path: Path) -> None:
    from jsonschema import Draft202012Validator
    from referencing import Registry, Resource

    schemas = ROOT / "contracts" / "schemas"
    registry: Registry = Registry()
    for path in schemas.glob("*.schema.json"):
        schema = json.loads(path.read_text(encoding="utf-8"))
        registry = registry.with_resource(schema["$id"], Resource.from_contents(schema))
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    data = json.loads(data_path.read_text(encoding="utf-8"))
    errors = list(Draft202012Validator(schema, registry=registry).iter_errors(data))
    if errors:
        raise SystemExit(f"{data_path}: {errors[0].message}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("out", type=Path, help="new folder for the content copy")
    options = parser.parse_args(argv)
    out = make(options.out)
    print(f"NIGHT_TEST_CONTENT {out} trigger={TRIGGER} ask=night")
    return 0


if __name__ == "__main__":
    sys.exit(main())

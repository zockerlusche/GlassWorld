"""Builds content/language/nouns.en.json for the English noun check (A1818, TL F5347).

    uv run python tools/language/build_en_nouns.py PATH/TO/mobypos.txt

Source: Moby (tm) Part-of-Speech II by Grady Ward, Project Gutenberg eBook 3203
(files/mobypos.txt), "Public Domain material by grant from the author, January, 2001".
Kept: single words of at least three lower-case letters whose PRINCIPAL use is a noun
(first tag N) - 'stone' and 'basket' are in, 'run' (a verb first) is not. Plurals are
folded by rule in the service; the irregular ones are the table below (our own data).
Nothing is downloaded here: the file is passed in, and its SHA-256 is checked and kept.
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "content" / "language" / "nouns.en.json"
EXPECTED_SHA256 = "cc81458b820a36253fbeb8106045166a345209d0fbf4cbfc20be2b189f24af82"

# Irregular plurals -> base form (common words; regular -s/-es/-ies are folded by rule).
IRREGULAR = {
    "children": "child",
    "men": "man",
    "women": "woman",
    "people": "person",
    "feet": "foot",
    "teeth": "tooth",
    "geese": "goose",
    "mice": "mouse",
    "lice": "louse",
    "oxen": "ox",
    "leaves": "leaf",
    "loaves": "loaf",
    "knives": "knife",
    "lives": "life",
    "wives": "wife",
    "wolves": "wolf",
    "halves": "half",
    "shelves": "shelf",
    "calves": "calf",
    "thieves": "thief",
    "elves": "elf",
    "selves": "self",
    "hooves": "hoof",
    "scarves": "scarf",
    "sheaves": "sheaf",
    "fish": "fish",
    "sheep": "sheep",
    "deer": "deer",
    "fungi": "fungus",
    "cacti": "cactus",
    "berries": "berry",
    "potatoes": "potato",
    "tomatoes": "tomato",
    "heroes": "hero",
    "echoes": "echo",
    "volcanoes": "volcano",
    "dice": "die",
    "nuclei": "nucleus",
}


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    data = Path(argv[0]).read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != EXPECTED_SHA256:
        print(f"NOUNS_FAIL unexpected file (sha256 {digest})", file=sys.stderr)
        return 1
    nouns: set[str] = set()
    for line in data.split(b"\r\n"):
        if b"\\" not in line:
            continue
        word, tag = line.rsplit(b"\\", 1)
        principal_noun = tag.startswith(b"N")
        if not principal_noun or not word.isalpha() or not word.islower() or len(word) < 3:
            continue
        nouns.add(word.decode("ascii"))
    nouns.update(IRREGULAR.values())
    out = {
        "contract_version": "0.2",
        "locale": "en",
        "source": "Moby (tm) Part-of-Speech II, Grady Ward; Gutenberg eBook 3203 (mobypos.txt)",
        "license": "Public domain by grant from the author (January 2001)",
        "source_sha256": digest,
        "nouns": sorted(nouns),
        "irregular_plurals": dict(sorted(IRREGULAR.items())),
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(
        json.dumps(out, ensure_ascii=True, indent=0) + "\n", encoding="utf-8", newline="\n"
    )
    where = OUT.relative_to(ROOT)
    print(f"NOUNS_OK {len(nouns)} nouns, {len(IRREGULAR)} irregular plurals -> {where}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

"""Effect map of the inhabitants' parameters (A1720, B2769, Marcel W2147).

Generated from content/ (and where the code reads a value, from src/ and tests/): for each need, mood, the
warmth zone, the body, fire and the decider's triggers it lists the value, what it amounts to over an hour, a
night or a day, what fills or drains it, what it triggers, which file holds it and which code and tests read it.
Marks what looks implausible ("hardly felt", "neglected before acted on") so that a value like the warmth loss
of D1112 #5 shows at a glance. No value is set here; the TL sets them (B2649).

Usage: python tools/effect_map/effect_map.py [--out FILE]   (repository root as working directory)
"""

from __future__ import annotations

import argparse
import itertools
import json
import re
from collections.abc import Iterable
from pathlib import Path
from typing import Any, cast

ROOT = Path(__file__).resolve().parents[2]
CONTENT = ROOT / "content"
SCHEMAS = ROOT / "contracts" / "schemas"
CORE = ROOT / "src" / "Simulation.Core"
TESTS = ROOT / "tests"

Json = dict[str, Any]

# Data files of the inhabitants' world and their schemas (content-map).
FILES = {
    "inhabitants/model.json": "inhabitant-model",
    "behaviour/utility.json": "utility-weights",
    "effects/basic-actions.json": "action-effects",
    "body/settings.json": "body-settings",
    "fire/settings.json": "fire-settings",
    "nature/settings.json": "nature-settings",
    "durability/settings.json": "durability-settings",
}

NIGHT_HOURS = 10


def load(relative: str) -> Json:
    path = CONTENT / relative
    return cast(Json, json.loads(path.read_text(encoding="utf-8"))) if path.exists() else {}


def pct(value: float) -> str:
    return f"{value * 100:.1f} %"


def hours(value: float | None) -> str:
    if value is None:
        return "nie"
    if value >= 48:
        return f"{value / 24:.1f} Tage"
    return f"{value:.1f} h"


def curve_start(curve: list[list[float]]) -> float | None:
    """The highest need value at which the curve's urgency is above zero (where acting starts)."""
    points = sorted(curve, key=lambda p: p[0])
    start = None
    for (x0, y0), (x1, y1) in itertools.pairwise(points):
        if y0 > 0 and y1 <= 0:
            start = x0 + ((x1 - x0) * y0 / (y0 - y1)) if y0 != y1 else x0
    if start is None and points and points[-1][1] > 0:
        start = points[-1][0]
    return start


def table_loss(table: list[Json], degrees: float) -> float:
    points = sorted(table, key=lambda p: p["degrees"])
    if degrees <= points[0]["degrees"]:
        first = points[0]
        return float(first["per_hour"]) * (degrees / first["degrees"] if first["degrees"] else 1.0)
    for a, b in itertools.pairwise(points):
        if degrees <= b["degrees"]:
            return float(a["per_hour"]) + (float(b["per_hour"]) - float(a["per_hour"])) * (
                degrees - a["degrees"]
            ) / (b["degrees"] - a["degrees"])
    return float(points[-1]["per_hour"])


class Map:
    def __init__(self) -> None:
        self.model = load("inhabitants/model.json")
        self.utility = load("behaviour/utility.json")
        self.effects = load("effects/basic-actions.json")
        self.body = load("body/settings.json")
        self.fire = load("fire/settings.json")
        self.lines: list[str] = []
        self.findings: list[str] = []
        # The identifiers each code and test file uses, read once (a value is looked up by its property name).
        words = re.compile(r"\b[A-Z]\w*\b")
        self._code = {
            p.name: set(words.findall(p.read_text(encoding="utf-8")))
            for p in CORE.rglob("*.cs")
            if "Generated" not in p.parts and "obj" not in p.parts
        }
        self._tests = {
            p.name: set(words.findall(p.read_text(encoding="utf-8")))
            for p in TESTS.rglob("*.cs")
            if "obj" not in p.parts and "bin" not in p.parts
        }

    # --- helpers -----------------------------------------------------------------------------------------
    def out(self, *lines: str) -> None:
        self.lines.extend(lines)

    def note(self, text: str) -> None:
        if text not in self.findings:
            self.findings.append(text)

    def fills(self, kind: str, target: str) -> list[str]:
        """Action rules and event effects that change a need or mood, with their amount."""
        found: list[str] = []
        for rule in cast(list[Json], self.effects.get("rules", [])):
            for effect in cast(list[Json], rule.get("effects", [])):
                if effect.get("kind") == kind and effect.get("id") == target:
                    condition = (
                        f" (an {rule['requires_property']})"
                        if rule.get("requires_property")
                        else ""
                    )
                    found.append(f"{rule['action']}{condition} {effect['delta']:+g}")
        for event in cast(list[Json], self.effects.get("event_effects", [])):
            for effect in cast(list[Json], event.get("effects", [])):
                if effect.get("kind") == kind and effect.get("id") == target:
                    found.append(f"Ereignis {event['event']} {effect['delta']:+g}")
        return found

    # --- sections ---------------------------------------------------------------------------------------
    def needs(self) -> None:
        considerations = cast(list[Json], self.utility.get("considerations", []))
        zone = cast(Json, self.model.get("comfort_zone") or {})
        modifiers = cast(list[Json], zone.get("need_modifiers") or [])
        drivers = cast(list[Json], self.model.get("mood_drivers", []))
        self.out("## 1. Bedürfnisse", "")
        self.out(
            "| Bedürfnis | Verfall/h | je Tag | voll → vernachlässigt | voll → leer | Handeln ab (Entscheider) | füllt | ändert bei Komfortstufe | treibt Gemüt |"
        )
        self.out("|---|---|---|---|---|---|---|---|---|")
        for need in cast(list[Json], self.model.get("needs", [])):
            decay = float(need["decay_per_hour"])
            neglect = float(need.get("neglect_below", 0))
            to_neglect = (1 - neglect) / decay if decay > 0 else None
            to_empty = 1 / decay if decay > 0 else None
            acts = [c for c in considerations if c.get("need") == need["id"]]
            starts = [s for s in (curve_start(c.get("curve", [])) for c in acts) if s is not None]
            start = max(starts) if starts else None
            fills = self.fills("need", need["id"])
            mods = [f"{m['level']} ×{m['factor']:g}" for m in modifiers if m["need"] == need["id"]]
            moods = [
                f"<{d['below']:g} → {d['mood']} {d['delta_per_hour']:+g}/h"
                for d in drivers
                if d.get("need") == need["id"]
            ]
            if need["id"] == zone.get("need"):
                fills.append("Komfortzone (Abschnitt 3)")
            self.out(
                f"| {need['id']} | {decay:g} | {pct(decay * 24)} | {hours(to_neglect)} | {hours(to_empty)} | "
                f"{'—' if start is None else f'< {start:.2f}'} | {', '.join(fills) or '—'} | {', '.join(mods) or '—'} | {', '.join(moods) or '—'} |"
            )
            if decay == 0 and need["id"] != zone.get("need"):
                self.note(
                    f"Bedürfnis **{need['id']}** hat keinen Verfall – es ändert sich nur durch Handlungen und Ereignisse."
                )
            elif 0 < decay * 24 < 0.05:
                self.note(
                    f"Bedürfnis **{need['id']}** sinkt nur {pct(decay * 24)} am Tag – kaum spürbar."
                )
            if start is not None and neglect > 0 and start <= neglect:
                self.note(
                    f"Bedürfnis **{need['id']}**: der Entscheider handelt erst unter {start:.2f}, vernachlässigt ist es schon unter {neglect:g}."
                )
            if decay > 0 and not fills:
                self.note(
                    f"Bedürfnis **{need['id']}** sinkt, aber keine Handlung und kein Ereignis füllt es."
                )
        self.out("")

    def moods(self) -> None:
        drivers = cast(list[Json], self.model.get("mood_drivers", []))
        self.out("## 2. Gemüt", "")
        self.out(
            "| Gemüt | Grundwert | Rückkehr/h | Treiber | Handlungen/Ereignisse |",
            "|---|---|---|---|---|",
        )
        for mood in cast(list[Json], self.model.get("moods", [])):
            mine = [
                (
                    f"{d['need']} < {d['below']:g}"
                    if d.get("need")
                    else f"Empfindung {d['sensation']}"
                )
                + f" {d['delta_per_hour']:+g}/h"
                for d in drivers
                if d["mood"] == mood["id"]
            ]
            self.out(
                f"| {mood['id']} | {mood['baseline']:g} | {mood['return_per_hour']:g} | {', '.join(mine) or '—'} | {', '.join(self.fills('mood', mood['id'])) or '—'} |"
            )
            for d in drivers:
                if d["mood"] == mood["id"] and abs(float(d["delta_per_hour"])) <= float(
                    mood["return_per_hour"]
                ):
                    cause = (
                        f"{d['need']} < {d['below']:g}"
                        if d.get("need")
                        else f"Empfindung {d['sensation']}"
                    )
                    self.note(
                        f"Gemüt **{mood['id']}**: Treiber {cause} ({d['delta_per_hour']:+g}/h) ist nicht stärker als die Rückkehr "
                        f"{mood['return_per_hour']:g}/h – er bewegt das Gemüt nicht."
                    )
        will = cast(Json, self.model.get("will_to_live") or {})
        if will:
            self.out(
                "",
                f"**Lebenswille:** Start {will['start']:g}; sinkt {will['decline_per_hour']:g}/h, wenn ein Bedürfnis länger als "
                f"{will['neglect_hours_before_decline']} h vernachlässigt ist (von {will['start']:g} bis {will['fading_below']:g} in "
                f"{hours((float(will['start']) - float(will['fading_below'])) / float(will['decline_per_hour']))}), erholt sich {will['recover_per_hour']:g}/h.",
            )
        self.out("")

    def warmth(self) -> None:
        zone = cast(Json, self.model.get("comfort_zone") or {})
        if not zone:
            return
        self.out("## 3. Wärme und Komfortzone", "")
        self.out(
            f"- **Zone:** {zone['min_tenth_c'] / 10:g}–{zone['max_tenth_c'] / 10:g} °C, je Bewohner verschoben um bis zu ±{zone.get('individual_shift_tenth_c', 0) / 10:g} °C, "
            f"Band kühl/warm {zone.get('band_tenth_c', 0) / 10:g} °C.",
            f"- **Erholung in der Zone:** {zone['recover_per_hour']:g}/h; **Boden** durch die Luft allein: {zone.get('floor', 0):g}.",
        )
        factors = {f["action"]: f for f in cast(list[Json], zone.get("action_factors") or [])}
        sleep = float(factors.get("sleep", {}).get("cold_factor", 1.0))
        rain = float(zone.get("precipitation_cold_factor") or 1.0)
        self.out(
            "",
            f"Verlust des Bedürfnisses **{zone['need']}** (Schlaf ×{sleep:g}, Regen ×{rain:g}):",
            "",
        )
        self.out(
            "| Grad unter der Zone | je Stunde | eine Nacht (10 h, Schlaf) | eine Nacht mit Regen |",
            "|---|---|---|---|",
        )
        cold_table = cast(list[Json] | None, zone.get("cold_loss_table"))
        for degrees in (2, 5, 10, 15):
            per_hour = (
                table_loss(cold_table, degrees)
                if cold_table
                else float(zone["cold_loss_per_hour_per_degree"]) * degrees
            )
            night = per_hour * sleep * NIGHT_HOURS
            self.out(f"| {degrees} | {per_hour:.4f} | {night:.3f} | {night * rain:.3f} |")
            if degrees == 10 and night < 0.05:
                self.note(
                    f"**Wärme** sinkt in einer Nacht 10 °C unter der Zone nur um {night:.3f} – kaum spürbar (D1112 #5). "
                    f"Quelle: comfort_zone.{'cold_loss_table' if cold_table else 'cold_loss_per_hour_per_degree'}."
                )
        heat_table = cast(list[Json] | None, zone.get("heat_loss_table"))
        heat5 = (
            table_loss(heat_table, 5)
            if heat_table
            else float(zone["heat_loss_per_hour_per_degree"]) * 5
        )
        self.out(
            "",
            f"Hitze: 5 °C über der Zone kostet {heat5:.4f}/h ({heat5 * 8:.3f} an 8 heißen Stunden).",
        )
        mods = cast(list[Json], zone.get("need_modifiers") or [])
        pace = cast(list[Json], zone.get("pace_factors") or [])
        if mods or pace:
            self.out(
                "",
                "**Folgen je Komfortstufe:** "
                + ", ".join(
                    [f"{m['level']}: {m['need']} ×{m['factor']:g}" for m in mods]
                    + [f"{p['level']}: Gehtempo ×{p['factor']:g}" for p in pace]
                ),
            )
        felt = cast(list[Json], zone.get("felt") or [])
        if felt:
            self.out(
                "",
                "**Empfindungen:** "
                + ", ".join(f"{f['sensation']} bei {'/'.join(f['levels'])}" for f in felt),
            )
        self.out("")

    def body_section(self) -> None:
        body = self.body
        if not body:
            return
        self.out("## 4. Körper: Nässe, Sonne, Wind, andere", "")
        wet = cast(Json, body["wetness"])
        rain = float(wet["rain_per_hour"])
        self.out(
            f"- **Regen** macht im Freien in {hours(1000 / rain if rain else None)} ganz nass; ganz nass fühlt sich {wet['cool_tenth_c'] / 10:g} °C kälter an; "
            f"nass für die KI ab {wet.get('wet_from_permille', 1)} ‰; Unterschlupf ({wet['shelter_property']}) in {wet['shelter_radius_cm'] / 100:g} m.",
            "- **Trocknen** von ganz nass: "
            + ", ".join(
                f"{'unter ' + str(b['below_tenth_c'] / 10) + ' °C' if b['below_tenth_c'] is not None else 'darüber'} {hours(1000 / b['per_hour'] if b['per_hour'] else None)}"
                for b in cast(list[Json], wet["drying"])
            )
            + f"; bei vollem Wind +{wet['wind_drying_permille'] / 10:g} %.",
            f"- **Sonne:** +{body['sun']['warm_tenth_c'] / 10:g} °C bei klarem Himmel außerhalb von Schatten (Deckung je Baumart/Jahreszeit).",
        )
        if wind := body.get("wind"):
            self.out(
                f"- **Wind:** bis −{wind['cool_tenth_c'] / 10:g} °C, Windschutz (wind_block_permille) in {wind['block_radius_cm'] / 100:g} m."
            )
        if others := body.get("others_warmth"):
            self.out(
                f"- **Andere in der Nähe:** bis +{others['tenth_c'] / 10:g} °C in {others['radius_cm'] / 100:g} m."
            )
        if worn := body.get("worn"):
            self.out(
                f"- **Getragenes** mit {worn['property']}: +{worn['warm_tenth_c'] / 10:g} °C, solange es kälter als {worn.get('up_to_tenth_c', 0) / 10:g} °C ist."
            )
        if places := body.get("felt_places"):
            self.out(
                f"- **Ortsgedächtnis:** {places['max']} Orte zu {places['cell_m']} m, vergessen nach {hours(float(places['forget_hours']))}."
            )
        if experiences := body.get("experiences"):
            self.out(
                "- **Erfahrungen** alle "
                f"{body.get('experience_every_minutes', 10)} min: "
                + ", ".join(f"{e['felt']} → {e['property']}" for e in cast(list[Json], experiences))
            )
        self.out("")

    def fire_section(self) -> None:
        fire = self.fire
        if not fire:
            return
        self.out("## 5. Feuer", "")
        if course := fire.get("course"):
            self.out(
                f"- **Verlauf:** anflammen {course['kindle_minutes']} min, nachlassen in den letzten {course['fade_minutes']} min, "
                f"Glut {course['embers_minutes']} min mit {course['embers_permille'] / 10:g} % Stärke (Hitzestufe {course['embers_heat_level']})."
            )
        if spread := fire.get("spread"):
            checks = 60 / float(spread["every_minutes"])
            chance = float(spread["chance_permille"]) / 1000
            per_hour = 1 - (1 - chance) ** checks
            self.out(
                f"- **Übergreifen:** am Feuer {pct(chance)} je Prüfung, alle {spread['every_minutes']} min → {pct(per_hour)} in einer Stunde "
                f"(ohne Wind), Reichweite {spread['radius_cm'] / 100:g} m, höchstens {spread['max_burning']} brennende Dinge."
            )
        for felt in cast(list[Json], fire.get("felt") or []):
            self.out(
                f"- **Empfindung {felt['sensation']}:** ab {felt['min_burning']} brennenden Dingen in {felt['radius_cm'] / 100:g} m."
            )
        self.out("")

    def index(self) -> None:
        """Every number of the data files: where it is, what the schema says, which code and tests read it."""
        self.out("## 6. Parameter-Index (erzeugt)", "")
        self.out("| Datei | Wert | Beschreibung (Schema) | Code | Tests |", "|---|---|---|---|---|")
        for relative, schema_name in FILES.items():
            data = load(relative)
            schema_path = SCHEMAS / f"{schema_name}.schema.json"
            schema = (
                cast(Json, json.loads(schema_path.read_text(encoding="utf-8")))
                if schema_path.exists()
                else {}
            )
            for key, value in leaves(data):
                if not isinstance(value, int | float) or isinstance(value, bool):
                    continue
                last = key.split(".")[-1].split("[")[0]
                prop = "".join(part.capitalize() for part in last.split("_"))
                code = sorted(name for name, used in self._code.items() if prop in used)
                tests = sorted(name for name, used in self._tests.items() if prop in used)
                description = describe(schema, last)
                self.out(
                    f"| {relative} | `{key}` = {value:g} | {description} | {', '.join(code[:3]) or '—'} | {', '.join(tests[:3]) or '—'} |"
                )
        self.out("")

    def render(self) -> str:
        self.needs()
        self.moods()
        self.warmth()
        self.body_section()
        self.fire_section()
        body = self.lines
        self.lines = []
        self.index()
        index = self.lines
        head = [
            "# Wirkungskarte der Bewohner-Parameter (A1720, erzeugt)",
            "",
            "Erzeugt von `tools/effect_map/effect_map.py` aus `content/` (und den Stellen in `src/`/`tests/`, die einen Wert lesen). "
            "Werte setzt der TL (B2649); hier steht nur, was sie bewirken.",
            "",
            "## 0. Auffälligkeiten (automatisch)",
            "",
        ]
        head += [f"- {f}" for f in self.findings] or ["- keine"]
        return "\n".join([*head, "", *body, *index]) + "\n"


def leaves(node: Any, prefix: str = "") -> Iterable[tuple[str, Any]]:
    if isinstance(node, dict):
        for key, value in cast(dict[str, Any], node).items():
            if key == "contract_version":
                continue
            yield from leaves(value, f"{prefix}.{key}" if prefix else key)
    elif isinstance(node, list):
        items = cast(list[Any], node)
        for i, value in enumerate(items):
            label: object = None
            if isinstance(value, dict):
                entry = cast(dict[str, Any], value)
                label = (
                    entry.get("id")
                    or entry.get("need")
                    or entry.get("action")
                    or entry.get("season")
                )
            yield from leaves(value, f"{prefix}[{label or i}]")
    else:
        yield prefix, node


def describe(schema: Json, key: str) -> str:
    """The description of a property anywhere in a schema (first one found), shortened."""
    stack: list[Any] = [schema]
    while stack:
        node = stack.pop()
        if isinstance(node, dict):
            props = cast(dict[str, Any], node).get("properties")
            if isinstance(props, dict) and key in props:
                entry: object = cast(dict[str, Any], props)[key]
                text = (
                    str(cast(dict[str, Any], entry).get("description", ""))
                    if isinstance(entry, dict)
                    else ""
                )
                return (text[:140] + "…") if len(text) > 140 else (text or "—")
            stack.extend(cast(dict[str, Any], node).values())
        elif isinstance(node, list):
            stack.extend(cast(list[Any], node))
    return "—"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=None)
    args = parser.parse_args()
    text = Map().render()
    if args.out:
        cast(Path, args.out).write_text(text, encoding="utf-8")
    else:
        print(text)


if __name__ == "__main__":
    main()

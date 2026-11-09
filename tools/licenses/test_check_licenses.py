"""Tests of the build license check (B10 S1): red on a missing row or a license outside B2482."""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from types import ModuleType

import pytest

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]


def _module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("check_licenses", HERE / "check_licenses.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module  # dataclasses look the module up while the file runs
    spec.loader.exec_module(module)
    return module


CHECK = _module()

HEADER = "| Name | Version | Kind | License | Source | Used in |\n|---|---|---|---|---|---|\n"


def make_repo(
    tmp_path: Path,
    rows: list[tuple[str, str]],
    packages: list[str] = (),  # type: ignore[assignment]
    nuget: list[str] = (),  # type: ignore[assignment]
    assets: dict[str, str] | None = None,
    rules: list[dict[str, str]] | None = None,
) -> Path:
    table = "".join(f"| {name} | 1 | x | {lic} | src | here |\n" for name, lic in rows)
    (tmp_path / "LICENSES.md").write_text("# Licenses\n\n" + HEADER + table, encoding="utf-8")
    registry = 'source = { registry = "https://pypi.org/simple" }'
    lock = "version = 1\n" + "".join(
        f'\n[[package]]\nname = "{p}"\nversion = "1.0"\n{registry}\n' for p in packages
    )
    lock += '\n[[package]]\nname = "glassworld-ai"\nversion = "0.1"\n'
    lock += 'source = { editable = "ai-service" }\n'
    (tmp_path / "uv.lock").write_text(lock, encoding="utf-8")
    refs = "".join(f'<PackageReference Include="{n}" Version="1.0" />' for n in nuget)
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "App.csproj").write_text(
        f"<Project><ItemGroup>{refs}</ItemGroup></Project>"
    )
    for rel, content in (assets or {}).items():
        path = tmp_path / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
    (tmp_path / "tools" / "licenses").mkdir(parents=True)
    (tmp_path / "tools" / "licenses" / "assets.json").write_text(
        json.dumps({"rules": rules or []}), encoding="utf-8"
    )
    return tmp_path


def test_the_repository_passes() -> None:
    assert CHECK.check(REPO) == []


def test_complete_repo_passes(tmp_path: Path) -> None:
    root = make_repo(
        tmp_path,
        [("httpx", "BSD-3-Clause"), ("Some.Lib", "MIT"), ("A Font", "SIL OFL 1.1, approved F5242")],
        packages=["httpx"],
        nuget=["Some.Lib"],
        assets={"game/ui/fonts/a.ttf": "x", "game/ui/frames/f.png": "x"},
        rules=[
            {"glob": "game/ui/fonts/*", "entry": "A Font"},
            {"glob": "game/ui/frames/*", "entry": "own"},
        ],
    )
    assert CHECK.check(root) == []
    assert CHECK.main(["--root", str(root)]) == 0


def test_missing_python_package_is_red(tmp_path: Path) -> None:
    root = make_repo(tmp_path, [("httpx", "BSD-3-Clause")], packages=["httpx", "Typing_Extensions"])
    problems = CHECK.check(root)
    assert problems == ["uv.lock: package 'Typing_Extensions' has no row in LICENSES.md"]
    assert CHECK.main(["--root", str(root)]) == 1


def test_missing_nuget_package_is_red(tmp_path: Path) -> None:
    root = make_repo(tmp_path, [], nuget=["Newtonsoft.Json"])
    assert CHECK.check(root) == ["NuGet: package 'Newtonsoft.Json' has no row in LICENSES.md"]


@pytest.mark.parametrize(
    ("license_text", "reason"),
    [
        ("GPL-3.0", "approval"),
        ("LGPL-2.1", "approval"),
        ("CC-BY-NC 4.0", "approval"),
        ("Proprietary", "not allowed"),
        ("Proprietary (limited use)", "not allowed"),  # 'mit' inside 'limited' (review #51)
        ("Commercial, permitted for internal use", "not allowed"),
        ("Public Domain", "not allowed"),
        ("Zlib", "not allowed"),
        ("MIT-like custom terms", "not allowed"),
        ("MPL-2.0", "unmodified"),
    ],
)
def test_licenses_outside_b2482_are_red(tmp_path: Path, license_text: str, reason: str) -> None:
    root = make_repo(tmp_path, [("thing", license_text)], packages=["thing"])
    problems = CHECK.check(root)
    assert len(problems) == 1
    assert reason in problems[0]


def test_approved_exception_and_unmodified_mpl_pass(tmp_path: Path) -> None:
    root = make_repo(
        tmp_path,
        [("gpl-thing", "GPL-3.0 (approved F9999)"), ("certifi", "MPL-2.0, unmodified")],
        packages=["gpl-thing", "certifi"],
    )
    assert CHECK.check(root) == []


def test_asset_without_rule_or_with_unknown_entry_is_red(tmp_path: Path) -> None:
    root = make_repo(
        tmp_path,
        [],
        assets={"game/sounds/rain.ogg": "x", "game/models/tree.glb": "x"},
        rules=[{"glob": "game/models/*", "entry": "Some Pack"}],
    )
    problems = CHECK.check(root)
    assert "asset 'game/sounds/rain.ogg' matches no rule in tools/licenses/assets.json" in problems
    assert any("names 'Some Pack', not in LICENSES.md" in p for p in problems)


def test_names_with_siblings_and_children() -> None:
    rows = CHECK.parse_rows(
        HEADER
        + "| NJsonSchema.CodeGeneration / .CSharp | 1 | x | MIT | s | u |\n"
        + "| SQLitePCLRaw.bundle_e_sqlite3 (+ core, provider) | 1 | x | Apache-2.0 | s | u |\n"
        + "| jinja2, markupsafe | 1 | x | BSD-3-Clause | s | u |\n"
    )
    keys = set().union(*(r.keys for r in rows))
    assert {"njsonschema-codegeneration", "njsonschema-codegeneration-csharp"} <= keys
    assert {"sqlitepclraw-bundle-e-sqlite3", "sqlitepclraw-core", "sqlitepclraw-provider"} <= keys
    assert {"jinja2", "markupsafe"} <= keys


@pytest.mark.parametrize(
    "license_text",
    [
        "MIT (repository licence); model Apache-2.0 (D397)",
        "MIT or Apache-2.0",
        'CC0 1.0 (page licence field: "CC0", checked)',
        "Zlib (approved by Marcel, F5265 = A)",
        "SIL OFL 1.1 (unmodified, approved F5242; license text in `OFL.txt`)",
    ],
)
def test_allowed_and_approved_texts_pass(tmp_path: Path, license_text: str) -> None:
    root = make_repo(tmp_path, [("thing", license_text)], packages=["thing"])
    assert CHECK.check(root) == []


def test_a_tool_that_is_not_distributed_needs_an_approval_too(tmp_path: Path) -> None:
    """Review GW1 #51: 'tool only' is no exemption; the row names its decision."""
    root = make_repo(tmp_path, [], packages=[])
    table = (tmp_path / "LICENSES.md").read_text(encoding="utf-8")
    table += "| Blender | 4.5, not distributed | tool | GPL-3.0-or-later, tool only | src | x |\n"
    table += (
        "| Kimodo | 1, not distributed | tool | GPL-3.0, tool only (approved F5258) | src | x |\n"
    )
    (tmp_path / "LICENSES.md").write_text(table, encoding="utf-8")
    problems = CHECK.check(root)
    assert len(problems) == 1 and problems[0].startswith("Blender")


@pytest.mark.parametrize(
    "license_text",
    ["GPL-3.0 (not approved F1234)", "GPL-3.0, not  approved F1234", "Zlib (NOT approved F7)"],
)
def test_not_approved_is_no_approval(tmp_path: Path, license_text: str) -> None:
    root = make_repo(tmp_path, [("thing", license_text)], packages=["thing"])
    assert len(CHECK.check(root)) == 1


def test_sound_files_map_by_their_recorded_origin(tmp_path: Path) -> None:
    root = make_repo(
        tmp_path,
        [("Kenney Impact Sounds - steps", "CC0 1.0")],
        assets={"game/assets/sound/base/a.wav": "x", "game/assets/sound/base/b.wav": "x"},
    )
    library = {
        "origins": [{"id": "kenney"}, {"id": "synth"}],
        "sounds": [
            {"origin": "kenney", "files": [{"file": "a.wav"}]},
            {"origin": "synth", "files": [{"file": "b.wav"}]},
        ],
    }
    (root / "content/sound").mkdir(parents=True)
    (root / "content/sound/library.json").write_text(json.dumps(library), encoding="utf-8")
    config = {
        "rules": [],
        "libraries": [
            {
                "file": "content/sound/library.json",
                "folder": "game/assets/sound/base",
                "origins": {"kenney": "Kenney Impact Sounds", "synth": "own"},
            }
        ],
    }
    (root / "tools/licenses/assets.json").write_text(json.dumps(config), encoding="utf-8")
    assert CHECK.check(root) == []
    config["libraries"][0]["origins"].pop("synth")
    (root / "tools/licenses/assets.json").write_text(json.dumps(config), encoding="utf-8")
    problems = CHECK.check(root)
    assert any("origin 'synth'" in p for p in problems)
    assert any("b.wav" in p for p in problems)


def _with_models(root: Path, models: list[dict[str, str]]) -> None:
    (root / "content" / "ai").mkdir(parents=True, exist_ok=True)
    data = {"contract_version": "0.2", "models": models}
    (root / "content" / "ai" / "model-downloads.json").write_text(json.dumps(data), "utf-8")


def test_offered_models_need_an_allowed_licence_and_a_row(tmp_path: Path) -> None:
    """Review GW1 #125: the download list is covered like the packages."""
    root = make_repo(tmp_path, [("Qwen3.5 9B (Ollama `lm:9b`)", "Apache-2.0")])
    _with_models(root, [{"id": "lm:9b", "licence": "Apache-2.0"}])
    assert CHECK.check(root) == []
    _with_models(
        root,
        [
            {"id": "lm:9b", "licence": "Proprietary (limited use)"},
            {"id": "other:1b", "licence": "MIT"},
        ],
    )
    problems = CHECK.check(root)
    assert any("lm:9b" in p and "not allowed" in p for p in problems)
    assert any("'other:1b' has no row" in p for p in problems)


def test_a_model_row_must_name_the_id_in_backticks(tmp_path: Path) -> None:
    """Review GW1 #125: a short id must not match an unrelated row by substring."""
    root = make_repo(tmp_path, [("Helm charts", "MIT")])
    _with_models(root, [{"id": "lm", "licence": "MIT"}])
    assert any("'lm' has no row" in p for p in CHECK.check(root))

"""A1720: the effect map is generated from the data and flags what is hardly felt."""

from __future__ import annotations

import importlib.util
from pathlib import Path
from types import ModuleType


def _module() -> ModuleType:
    path = Path(__file__).with_name("effect_map.py")
    spec = importlib.util.spec_from_file_location("effect_map", path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_the_map_has_every_section_and_the_parameter_index() -> None:
    text: str = _module().Map().render()
    for heading in ("## 0.", "## 1. Bed", "## 2. Gem", "## 3. W", "## 6. Parameter-Index"):
        assert heading in text
    assert "inhabitants/model.json" in text and "decay_per_hour" in text


def test_where_acting_starts_and_a_loss_table() -> None:
    module = _module()
    assert module.curve_start([[0.0, 1.0], [0.5, 0.4], [0.8, 0.0], [1.0, 0.0]]) == 0.8
    table = [{"degrees": 2, "per_hour": 0.01}, {"degrees": 10, "per_hour": 0.05}]
    assert abs(module.table_loss(table, 6) - 0.03) < 1e-9
    assert module.table_loss(table, 20) == 0.05

"""Canonical process signature (B2537, common.schema.json ProcessSignature).

Two tries are "the same" when process, the true property sets of inputs and tool, and the heat
level are equal. The world rule id of an AI-found rule is derived from this hash, never taken
from the model. Test vectors: contracts/testvectors/process-signature.json (C# uses the same).
"""

from __future__ import annotations

import hashlib
import json
from collections.abc import Sequence


def canonical_signature(
    process: str,
    inputs: Sequence[Sequence[str]],
    tool: Sequence[str] | None,
    heat_level: str | None,
) -> str:
    """Canonical JSON: deduplicated, ordinally sorted property lists; inputs sorted by their
    lists joined with ','; no tool (None or empty) = null; sorted keys; no spaces."""
    normalized = {
        "process": process,
        "inputs": sorted((sorted(set(i)) for i in inputs), key=lambda props: ",".join(props)),
        "tool": sorted(set(tool)) if tool else None,  # no tool = null, also for an empty list
        "heat_level": heat_level,
    }
    return json.dumps(normalized, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def world_rule_id(
    process: str,
    inputs: Sequence[Sequence[str]],
    tool: Sequence[str] | None,
    heat_level: str | None,
) -> str:
    digest = hashlib.sha256(
        canonical_signature(process, inputs, tool, heat_level).encode("utf-8")
    ).hexdigest()
    return "world_" + digest[:16]

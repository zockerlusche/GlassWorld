#!/usr/bin/env bash
# Headless smoke test for the Godot project (Phase 0, R7).
# Builds the C# assemblies, imports the project, runs N frames headless and fails on
# any engine/script error, a missing success marker, a timeout or a non-zero exit code.
#
# Usage: tools/godot-smoke.sh [frames]        (default 600)
# Needs: GODOT = path to the Godot 4.7.2 .NET (mono) binary (console variant on Windows),
#        dotnet SDK from global.json.
set -euo pipefail

FRAMES="${1:-600}"
TIMEOUT_S="${SMOKE_TIMEOUT_S:-300}"
# Work with relative paths so the same script runs on Linux CI and in Git Bash on Windows.
cd "$(dirname "${BASH_SOURCE[0]}")/.."
GAME="game"

if [[ -z "${GODOT:-}" ]]; then
  echo "SMOKE_FAIL: environment variable GODOT is not set" >&2
  exit 2
fi

echo "== dotnet build (game) =="
dotnet build "$GAME/GlassWorld.Game.csproj" -nologo -v q

echo "== godot import =="
# The first import creates .godot/; errors here are reported by the run below as well.
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --import >/dev/null 2>&1 || true

echo "== godot run ($FRAMES frames) =="
LOG="$(mktemp)"
trap 'rm -f "$LOG"' EXIT
set +e
# --quit-after is a watchdog: if the scene never reaches its marker (e.g. after an exception),
# Godot still stops shortly after the frame budget instead of running into the timeout.
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after "$((FRAMES + 120))" -- "--smoke-frames=$FRAMES" >"$LOG" 2>&1
CODE=$?
set -e
cat "$LOG"

if [[ $CODE -eq 124 ]]; then
  echo "SMOKE_FAIL: timeout after ${TIMEOUT_S}s" >&2
  exit 1
fi
if [[ $CODE -ne 0 ]]; then
  echo "SMOKE_FAIL: Godot exit code $CODE" >&2
  exit 1
fi
if grep -Eq '^(ERROR|SCRIPT ERROR|USER ERROR)|Unhandled exception' "$LOG"; then
  echo "SMOKE_FAIL: errors in Godot output" >&2
  exit 1
fi
if ! grep -Eq '^I18N_OK locales=[a-z,]+ keys=[1-9][0-9]*$' "$LOG"; then
  echo "SMOKE_FAIL: text catalogs not loaded or language switch failed (marker 'I18N_OK' missing)" >&2
  exit 1
fi
if ! grep -Eq '^WORLD_OK seed=[0-9]+ inhabitants=[1-9][0-9]* objects=[0-9]+ hash=[0-9a-f]{12}$' "$LOG"; then
  echo "SMOKE_FAIL: core world not loaded (marker 'WORLD_OK' missing)" >&2
  exit 1
fi
if ! grep -Eq '^AUDIO_OK sounds=[1-9][0-9]*$' "$LOG"; then
  echo "SMOKE_FAIL: retro audio check failed (marker 'AUDIO_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^AUDIO_SPATIAL_OK$' "$LOG"; then
  echo "SMOKE_FAIL: spatial sound check failed (marker 'AUDIO_SPATIAL_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^UI_OK$' "$LOG"; then
  echo "SMOKE_FAIL: UI standard check failed (marker 'UI_OK' missing)" >&2
  exit 1
fi
if ! grep -q "^SMOKE_OK frames=$FRAMES\$" "$LOG"; then
  echo "SMOKE_FAIL: success marker 'SMOKE_OK frames=$FRAMES' missing" >&2
  exit 1
fi
# Look prototype (A1435): builds the island piece and figures and checks that the skeletons animate.
LOOK_LOG="$(mktemp)"
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after 400 res://prototype/look/look_d355.tscn >"$LOOK_LOG" 2>&1
if grep -Eq '^(ERROR|SCRIPT ERROR|USER ERROR)|Unhandled exception' "$LOOK_LOG"; then
  cat "$LOOK_LOG" >&2
  echo "SMOKE_FAIL: look prototype reported errors" >&2
  exit 1
fi
if ! grep -Eq '^LOOK_OK frames=[0-9]+ variant=flat animated=True$' "$LOOK_LOG"; then
  cat "$LOOK_LOG" >&2
  echo "SMOKE_FAIL: look prototype not built or figures not animated (marker 'LOOK_OK ... animated=True' missing)" >&2
  exit 1
fi
echo "SMOKE_PASS: $FRAMES frames without errors"

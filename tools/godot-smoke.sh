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
trap 'rm -f "$LOG" "${MAIN_ISLAND_LOG:-}"' EXIT
set +e
# --quit-after is a watchdog: if the scene never reaches its marker (e.g. after an exception),
# Godot still stops after the frame budget instead of running into the timeout. The margin covers
# the game's own checks (24 layout cases with menu windows, screen, save, menus: about 300 frames).
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after "$((FRAMES + 2000))" -- "--smoke-frames=$FRAMES" >"$LOG" 2>&1
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
if ! grep -Eq '^SOUNDLIB_OK sounds=[1-9][0-9]* files=[1-9][0-9]*$' "$LOG"; then
  echo "SMOKE_FAIL: base sound library incomplete or off target (marker 'SOUNDLIB_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^AUDIO_SPATIAL_OK$' "$LOG"; then
  echo "SMOKE_FAIL: spatial sound check failed (marker 'AUDIO_SPATIAL_OK' missing)" >&2
  exit 1
fi
if ! grep -Eq '^SAVE_OK hash=[0-9a-f]{12} tick=[1-9][0-9]* questions=[1-9][0-9]*$' "$LOG"; then
  echo "SMOKE_FAIL: save and load in the game failed (marker 'SAVE_OK' missing)" >&2
  exit 1
fi
if ! grep -Eq '^MENU_OK screens=[1-9][0-9]*$' "$LOG"; then
  echo "SMOKE_FAIL: menus, settings or pause menu show a raw key or leave the screen (marker 'MENU_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^GIFT_OK$' "$LOG"; then
  echo "SMOKE_FAIL: placing a gift through the screen failed (marker 'GIFT_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^SCREEN_OK$' "$LOG"; then
  echo "SMOKE_FAIL: game screen check failed (marker 'SCREEN_OK' missing)" >&2
  exit 1
fi
if ! grep -q '^UI_OK$' "$LOG"; then
  echo "SMOKE_FAIL: UI standard check failed (marker 'UI_OK' missing)" >&2
  exit 1
fi
# The game waits for its own checks (layout cases, screen, save, menus) before it quits, so it may
# run a few frames longer than requested on a slow runner; at least $FRAMES frames must have run.
DONE_FRAMES="$(grep -oE '^SMOKE_OK frames=[0-9]+$' "$LOG" | head -1 | cut -d= -f2)"
if [ -z "$DONE_FRAMES" ] || [ "$DONE_FRAMES" -lt "$FRAMES" ]; then
  echo "SMOKE_FAIL: success marker 'SMOKE_OK frames=N' with N >= $FRAMES missing" >&2
  exit 1
fi
# Island mode of the main game (TL #19505): the same game and checks with the generated island, the
# figures and the objects (GLASSWORLD_WORLD=island); the placeholder run above stays the fallback.
echo "== godot run, island mode ($FRAMES frames) =="
MAIN_ISLAND_LOG="$(mktemp)"
set +e
GLASSWORLD_WORLD=island timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after "$((FRAMES + 2000))" -- "--smoke-frames=$FRAMES" >"$MAIN_ISLAND_LOG" 2>&1
CODE=$?
set -e
island_fail() {
  cat "$MAIN_ISLAND_LOG" >&2
  echo "SMOKE_FAIL: island mode: $1" >&2
  exit 1
}
if [[ $CODE -ne 0 ]]; then
  island_fail "Godot exit code $CODE"
fi
if grep -Eq '^(ERROR|SCRIPT ERROR|USER ERROR)|Unhandled exception' "$MAIN_ISLAND_LOG"; then
  island_fail "errors in Godot output"
fi
for marker in   '^WORLD_OK seed=[0-9]+ inhabitants=[1-9][0-9]* objects=[1-9][0-9]* hash=[0-9a-f]{12}$'   '^ISLAND_WORLD preset=[a-z_]+ chunks=[1-9][0-9]* figures=[1-9][0-9]* objects_drawn=[1-9][0-9]* layers=[1-9][0-9]*$'   '^ISLAND_COVER cells=[1-9][0-9]*x[1-9][0-9]* version=[1-9][0-9]* seen_permille=[1-9][0-9]*$'   '^AUDIO_SPATIAL_OK$'   '^SYMBOLS_OK$'   '^SCREEN_OK$'   '^ISLAND_UI_OK figures=[1-9][0-9]* objects_drawn=[1-9][0-9]*$'   '^ISLAND_NATURE grass_plants=[1-9][0-9]* grass_layers=[1-9][0-9]* season=[a-z_]+ season_colors=[1-9] weather_looks=[1-9][0-9]*$'   '^ISLAND_QUALITY level=[a-z_]+ shadows=(off|low|high) outline=(off|inverted_hull)$'   '^SAVE_OK hash=[0-9a-f]{12} tick=[1-9][0-9]* questions=[1-9][0-9]*$'   '^GIFT_OK$'   '^KNOCK_OK$'   '^MENU_OK screens=[1-9][0-9]*$'   '^UI_OK$'; do
  if ! grep -Eq "$marker" "$MAIN_ISLAND_LOG"; then
    island_fail "marker '$marker' missing"
  fi
done
DONE_FRAMES="$(grep -oE '^SMOKE_OK frames=[0-9]+$' "$MAIN_ISLAND_LOG" | head -1 | cut -d= -f2)"
if [ -z "$DONE_FRAMES" ] || [ "$DONE_FRAMES" -lt "$FRAMES" ]; then
  island_fail "success marker 'SMOKE_OK frames=N' with N >= $FRAMES missing"
fi
grep -E '^(WORLD_OK|ISLAND_WORLD|ISLAND_UI_OK|SAVE_OK|SMOKE_OK)' "$MAIN_ISLAND_LOG"
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
# Figure bodies (A1436): every Quaternius clip moves the skeleton of every GlassWorld body.
CLIP_LOG="$(mktemp)"
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after 2000 res://prototype/look/clip_check.tscn >"$CLIP_LOG" 2>&1
if ! grep -Eq '^CLIPS_OK bodies=adult_female,adult_male,child reference=ual1_standard clips=[1-9][0-9]* checked=[1-9][0-9]* failures=0$' "$CLIP_LOG"; then
  cat "$CLIP_LOG" >&2
  echo "SMOKE_FAIL: a clip does not move a figure body (marker 'CLIPS_OK ... failures=0' missing)" >&2
  exit 1
fi
# Arms and torso (B2528, TL #19090): the clips the figures play must not push an arm into the body.
# Every clip is sampled at fixed times (not with the frame time), so every run gives the same numbers.
PEN_LOG="$(mktemp)"
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after 6000 res://prototype/look/penetration_check.tscn >"$PEN_LOG" 2>&1
if ! grep -Eq '^PENETRATION_OK samples=[1-9][0-9]* .* missing=0 .* used_over=0$' "$PEN_LOG"; then
  cat "$PEN_LOG" >&2
  echo "SMOKE_FAIL: an arm goes into the body in a clip the figures play (marker 'PENETRATION_OK ... missing=0 ... used_over=0' missing)" >&2
  exit 1
fi
# Building plans (D471 D6): one mesh per part, inside the part sizes, visible step by step.
BUILD_LOG="$(mktemp)"
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after 600 res://build/build_plan_check.tscn >"$BUILD_LOG" 2>&1
if ! grep -Eq '^BUILD_PLAN_OK plans=[1-9][0-9]* parts=[1-9][0-9]* shapes=[1-9][0-9]* failures=0$' "$BUILD_LOG"; then
  cat "$BUILD_LOG" >&2
  echo "SMOKE_FAIL: a building plan is not drawn part by part (marker 'BUILD_PLAN_OK ... failures=0' missing)" >&2
  exit 1
fi
# Island (B2-3): terrain and water meshes of the generated start island, one per chunk; objects and decorative plants (TL #19722).
ISLAND_LOG="$(mktemp)"
timeout "$TIMEOUT_S" "$GODOT" --headless --path "$GAME" --quit-after 1200 res://world/island.tscn -- --seed=7 --check >"$ISLAND_LOG" 2>&1
if ! grep -Eq '^ISLAND_OK seed=7 chunks=[1-9][0-9]* ground_triangles=[1-9][0-9]* water_triangles=[1-9][0-9]* figures=[1-9][0-9]* objects=[1-9][0-9]* decor=[1-9][0-9]*$' "$ISLAND_LOG" || ! grep -Eq '^ISLAND_COVER seed=7 cells=[1-9][0-9]*x[1-9][0-9]* version=[1-9][0-9]* seen_permille=[1-9][0-9]*$' "$ISLAND_LOG" || grep -Eq '^(ERROR|SCRIPT ERROR)|Unhandled exception' "$ISLAND_LOG"; then
  cat "$ISLAND_LOG" >&2
  echo "SMOKE_FAIL: the island is not drawn (marker 'ISLAND_OK ...' missing or errors)" >&2
  exit 1
fi
# Living nature and weather (B2-5 R21, R23, R24): grass and small plants in detail levels, the seasonal colours as shader parameters, a look per weather.
if ! grep -Eq '^ISLAND_NATURE seed=7 grass_plants=[1-9][0-9]* grass_layers=[1-9][0-9]* season_colors=[1-9] weather_looks=[1-9][0-9]*$' "$ISLAND_LOG"; then
  cat "$ISLAND_LOG" >&2
  echo "SMOKE_FAIL: the island has no grass in detail levels, no seasonal colours or no weather looks (marker 'ISLAND_NATURE ...' missing)" >&2
  exit 1
fi
# Detail levels (B2 R18): the base grid plus at least one coarser ground level; the water is the same at every level.
if ! grep -Eq '^ISLAND_LOD seed=7 ground=1:[1-9][0-9]*(,[1-9][0-9]*:[1-9][0-9]*)+ water=[1-9][0-9]*$' "$ISLAND_LOG"; then
  cat "$ISLAND_LOG" >&2
  echo "SMOKE_FAIL: the island has no coarser detail level (marker 'ISLAND_LOD ...' missing)" >&2
  exit 1
fi
# Quality level (B2-7 R30): the scene reads its level (grass, shadows, outline) from the data and
# reports what it left on the sun and the materials.
if ! grep -Eq '^ISLAND_QUALITY seed=7 level=[a-z_]+ shadows=(off|low|high) outline=(off|inverted_hull)$' "$ISLAND_LOG" || ! grep -Eq '^ISLAND_QUALITY_STATE seed=7 sun_shadow=[01] shadow_mode=[0-2] shadow_distance_m=[0-9.]+ hull_meshes=[0-9]+$' "$ISLAND_LOG"; then
  cat "$ISLAND_LOG" >&2
  echo "SMOKE_FAIL: the island applies no quality level (marker 'ISLAND_QUALITY ...' or 'ISLAND_QUALITY_STATE ...' missing)" >&2
  exit 1
fi
echo "SMOKE_PASS: $FRAMES frames without errors"

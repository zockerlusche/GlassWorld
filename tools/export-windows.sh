#!/usr/bin/env bash
# Exports the Windows release build and checks that it starts headless with the core (B0 acceptance).
# Usage: GODOT=/path/to/Godot_v4.7.2-stable_mono_win64_console.exe bash tools/export-windows.sh [OUT_DIR]
# Needs the Godot 4.7.2 .NET export templates. The data folder content/ is copied next to the
# executable (ContentLocator looks there in exported builds).
# EXPORT_WORLD=island|placeholder (optional) writes GLASSWORLD_WORLD into the start scripts, so the test
# package starts new worlds as the generated island or as the placeholder whatever
# content/look/world.json says (TL #19505); without it the data value applies.
set -euo pipefail
# Windows paths for Godot under Git Bash (pwd -W), plain pwd elsewhere.
ROOT="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
GAME="$ROOT/game"
OUT="${1:-$GAME/export}"
: "${GODOT:?environment variable GODOT is not set}"
WORLD="${EXPORT_WORLD:-}"
if [[ -n "$WORLD" && "$WORLD" != "island" && "$WORLD" != "placeholder" ]]; then
  echo "EXPORT_FAIL: EXPORT_WORLD must be 'island' or 'placeholder', not '$WORLD'" >&2
  exit 2
fi

PRESETS="$GAME/export_presets.cfg"
if [ ! -f "$PRESETS" ]; then
  # export_presets.cfg is machine-local (.gitignore); the Windows preset has no secrets.
  cat >"$PRESETS" <<'CFG'
[preset.0]

name="Windows Desktop"
platform="Windows Desktop"
runnable=true
dedicated_server=false
custom_features=""
export_filter="all_resources"
include_filter=""
exclude_filter=""
export_path="export/GlassWorld.exe"
encryption_include_filters=""
encryption_exclude_filters=""
encrypt_pck=false
encrypt_directory=false

[preset.0.options]

binary_format/embed_pck=false
binary_format/architecture="x86_64"
codesign/enable=false
application/modify_resources=false
dotnet/include_scripts_content=false
dotnet/include_debug_symbols=false
dotnet/embed_build_outputs=false
CFG
fi

mkdir -p "$OUT"
# Never delete the repository's own data: the output must not be the repository or contain content/.
OUT_REAL="$(cd "$OUT" && (pwd -W 2>/dev/null || pwd))"
case "$OUT_REAL/" in
  "$ROOT/"|"$ROOT/content/"*)
    echo "EXPORT_FAIL: output folder '$OUT' is the repository or inside content/" >&2
    exit 1
    ;;
esac
if [ -e "$OUT/content" ] && [ ! -f "$OUT/GlassWorld.exe" ]; then
  echo "EXPORT_FAIL: '$OUT/content' exists but is not from an earlier export (no GlassWorld.exe next to it)" >&2
  exit 1
fi
"$GODOT" --headless --path "$GAME" --import >/dev/null 2>&1 || true
EXPORT_LOG="$(mktemp)"
if ! "$GODOT" --headless --path "$GAME" --export-release "Windows Desktop" "$OUT/GlassWorld.exe" >"$EXPORT_LOG" 2>&1; then
  tail -20 "$EXPORT_LOG" >&2
  echo "EXPORT_FAIL: Godot export failed" >&2
  exit 1
fi
rm -rf "$OUT/content"
cp -r "$ROOT/content" "$OUT/content"
# Build id beside the executable: the commit, marked when the working tree differs from it. The benchmark
# prints it (BENCH_ENV build=…), so a measurement names the build it ran on.
BUILD="$(git -C "$ROOT" rev-parse --short=12 HEAD 2>/dev/null || echo unknown)"
if [[ -n "$(git -C "$ROOT" status --porcelain 2>/dev/null)" ]]; then
  BUILD="$BUILD-dirty"
fi
printf '%s\n' "$BUILD" > "$OUT/build.txt"

LOG="$(mktemp)"
GLASSWORLD_WORLD="$WORLD" timeout 120 "$OUT/GlassWorld.exe" --headless -- --smoke-frames=120 >"$LOG" 2>&1 || true
if ! grep -Eq '^WORLD_OK seed=[0-9]+ inhabitants=[1-9][0-9]* ' "$LOG" || ! grep -Eq '^SMOKE_OK frames=[0-9]+$' "$LOG" || grep -Eq '^(ERROR|SCRIPT ERROR)|Unhandled exception' "$LOG"; then
  cat "$LOG" >&2
  echo "EXPORT_FAIL: exported build did not start the core world" >&2
  exit 1
fi
grep -E '^WORLD_OK' "$LOG"
# The exported build must reach the island scene's benchmark (B2-7): the templates take no scene path,
# so this is the only proof that the measurement can run on an export. Headless, one second, no window.
BENCH_DIR="$(mktemp -d)"
# The first pass of the benchmark data, whatever it is called (review GW3 #234).
if ! BENCH_HEADLESS=1 BENCH_PASS_LIMIT=1 BENCH_TIMEOUT_S=180 bash "$ROOT/tools/benchmark.sh" "$OUT/GlassWorld.exe" 1280x720 "$BENCH_DIR" --bench-seconds=1 --bench-warmup=0.5 >"$BENCH_DIR/out.log" 2>&1 \
  || ! grep -Eq '^BENCH_DONE ' "$BENCH_DIR/out.log"; then
  cat "$BENCH_DIR/out.log" >&2
  echo "EXPORT_FAIL: exported build did not run the benchmark of the island scene" >&2
  exit 1
fi
grep -E '^BENCH_ENV' "$BENCH_DIR/out.log" || true
# Start script with the local AI service (test package): the service runs from this repository.
WORLD_LINE=""
if [[ -n "$WORLD" ]]; then
  WORLD_LINE="set GLASSWORLD_WORLD=$WORLD"$'\r\n'
fi
printf '@echo off\r\nrem GlassWorld mit lokaler KI starten (Testpaket). Der KI-Dienst laeuft aus dem Repository.\r\nset GLASSWORLD_AI=live\r\nset GLASSWORLD_AI_ROOT=%s\r\n%sstart "" "%%~dp0GlassWorld.exe"\r\n' "$ROOT" "$WORLD_LINE" > "$OUT/Start-mit-KI.cmd"
printf '@echo off\r\nrem GlassWorld ohne KI starten (Routine-Modus, zum Vergleich).\r\n%sstart "" "%%~dp0GlassWorld.exe"\r\n' "$WORLD_LINE" > "$OUT/Start-ohne-KI.cmd"
echo "EXPORT_OK $OUT/GlassWorld.exe"

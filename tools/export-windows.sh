#!/usr/bin/env bash
# Exports the Windows release build and checks that it starts headless with the core (B0 acceptance).
# Usage: GODOT=/path/to/Godot_v4.7.2-stable_mono_win64_console.exe bash tools/export-windows.sh [OUT_DIR]
# Needs the Godot 4.7.2 .NET export templates. The data folder content/ is copied next to the
# executable (ContentLocator looks there in exported builds).
set -euo pipefail
# Windows paths for Godot under Git Bash (pwd -W), plain pwd elsewhere.
ROOT="$(cd "$(dirname "$0")/.." && (pwd -W 2>/dev/null || pwd))"
GAME="$ROOT/game"
OUT="${1:-$GAME/export}"
: "${GODOT:?environment variable GODOT is not set}"

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

LOG="$(mktemp)"
timeout 120 "$OUT/GlassWorld.exe" --headless -- --smoke-frames=120 >"$LOG" 2>&1 || true
if ! grep -Eq '^WORLD_OK seed=[0-9]+ inhabitants=[1-9][0-9]* ' "$LOG" || ! grep -Eq '^SMOKE_OK frames=[0-9]+$' "$LOG" || grep -Eq '^(ERROR|SCRIPT ERROR)|Unhandled exception' "$LOG"; then
  cat "$LOG" >&2
  echo "EXPORT_FAIL: exported build did not start the core world" >&2
  exit 1
fi
grep -E '^WORLD_OK' "$LOG"
# Start script with the local AI service (test package): the service runs from this repository.
printf '@echo off\r\nrem GlassWorld mit lokaler KI starten (Testpaket). Der KI-Dienst laeuft aus dem Repository.\r\nset GLASSWORLD_AI=live\r\nset GLASSWORLD_AI_ROOT=%s\r\nstart "" "%%~dp0GlassWorld.exe"\r\n' "$ROOT" > "$OUT/Start-mit-KI.cmd"
printf '@echo off\r\nrem GlassWorld ohne KI starten (Routine-Modus, zum Vergleich).\r\nstart "" "%%~dp0GlassWorld.exe"\r\n' > "$OUT/Start-ohne-KI.cmd"
echo "EXPORT_OK $OUT/GlassWorld.exe"

#!/usr/bin/env bash
# FPS benchmark of the island scene (B2-7, acceptance 5): runs every pass of content/look/benchmark.json
# in its own process and writes one frame-time CSV (pass,time_s,frame_ms) and one log per pass.
#
# Usage: tools/benchmark.sh BINARY WIDTHxHEIGHT OUT_DIR [--fullscreen] [--quality=ID] [more scene args …]
#   BINARY   the exported GlassWorld.exe, or the Godot 4.7.2 .NET editor binary (then the game is built
#            first and run with --path game)
#   OUT_DIR  folder for bench_<WxH>_<pass>.csv and .log (created if missing)
# Environment: BENCH_PASSES (default "day night storm winter"), BENCH_TIMEOUT_S per pass (default 900).
# Every run quits by itself; the log carries one line per pass:
#   BENCH_PASS pass=ID frames=N mean_fps=F p95_ms=F max_ms=F ok=yes|no size=WxH
# Exit code 1 if a run fails or writes no CSV (not if a pass misses the marks: the evaluation decides).
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "usage: tools/benchmark.sh BINARY WIDTHxHEIGHT OUT_DIR [--fullscreen] [--quality=ID] [args …]" >&2
  exit 2
fi

BIN="$1"
RES="$2"
OUT="$3"
shift 3
PASSES="${BENCH_PASSES:-day night storm winter}"
TIMEOUT_S="${BENCH_TIMEOUT_S:-900}"
# Windows paths for Godot under Git Bash (pwd -W), plain pwd elsewhere.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && (pwd -W 2>/dev/null || pwd))"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && (pwd -W 2>/dev/null || pwd))"

PROJECT=()
case "$(basename "$BIN")" in
  Godot*|godot*)
    # Editor binary: it loads the Debug assemblies, which must match the sources. Numbers for the
    # acceptance come from the release export; the editor run is for a quick look.
    dotnet build "$ROOT/game/GlassWorld.Game.csproj" -nologo -v q
    PROJECT=(--path "$ROOT/game")
    ;;
esac

FAILED=0
for PASS in $PASSES; do
  CSV="$OUT/bench_${RES}_${PASS}.csv"
  LOG="$OUT/bench_${RES}_${PASS}.log"
  echo "== benchmark pass $PASS at $RES =="
  set +e
  timeout "$TIMEOUT_S" "$BIN" "${PROJECT[@]}" res://world/island.tscn -- \
    "--benchmark=$CSV" "--bench-pass=$PASS" "--resolution=$RES" "$@" >"$LOG" 2>&1
  CODE=$?
  set -e
  grep -E '^(BENCH_START|BENCH_PASS|BENCH_DONE|ISLAND_FAIL)' "$LOG" || true
  if [[ $CODE -ne 0 || ! -s "$CSV" ]]; then
    echo "BENCH_FAIL pass=$PASS exit=$CODE (see $LOG)" >&2
    FAILED=1
  fi
done
exit "$FAILED"

#!/usr/bin/env bash
# FPS benchmark of the island scene (B2-7, acceptance 5): runs every pass of content/look/benchmark.json
# in its own process and writes one frame-time CSV (pass,time_s,frame_ms) and one log per pass.
#
# Usage: tools/benchmark.sh BINARY WIDTHxHEIGHT OUT_DIR [--fullscreen] [--quality=ID] [more scene args …]
#   BINARY   the exported GlassWorld.exe, or the Godot 4.7.2 .NET editor binary (then the game is built
#            first and run with --path game). Both start the game's start scene, which hands over to
#            the island scene on --benchmark=: an exported build takes no scene path as an argument.
#   OUT_DIR  folder for bench_<WxH>_<pass>.csv and .log (created if missing)
# Environment: BENCH_PASSES (default: every pass of the benchmark data - the content/ beside an exported
# binary, else the repository's; read with python), BENCH_TIMEOUT_S per pass (default 900),
# BENCH_HEADLESS=1 for a check without a window (no rendering, the numbers mean nothing).
# The extra arguments go to the scene (after --), never to the engine: "--headless" among them does NOT
# run headless. Without BENCH_HEADLESS=1 every pass opens a window on the GPU, so the run belongs in the
# AMW run queue (amw lauf start --schirm --gpu --warten).
# Every run quits by itself; the log carries one line per pass:
#   BENCH_ENV size=WxH mode=… vsync=… max_fps=N adapter=… driver=… api=… build=…   (what the run really ran on)
#   BENCH_PASS pass=ID frames=N mean_fps=F p95_ms=F max_ms=F ok=yes|no size=WxH warmup_frames=N
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
PASSES="${BENCH_PASSES:-}"
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

if [[ -z "$PASSES" ]]; then
  # The passes are data: the file the binary reads (beside an export), else the repository's.
  DATA="$(dirname "$BIN")/content/look/benchmark.json"
  [[ -f "$DATA" ]] || DATA="$ROOT/content/look/benchmark.json"
  PASSES="$(python -I -c 'import json, sys; print(" ".join(p["id"] for p in json.load(open(sys.argv[1], encoding="utf-8"))["passes"]))' "$DATA")" || true
  if [[ -z "$PASSES" ]]; then
    echo "BENCH_FAIL: cannot read the passes from $DATA (python needed) - set BENCH_PASSES" >&2
    exit 2
  fi
fi

ENGINE=()
if [[ "${BENCH_HEADLESS:-}" == "1" ]]; then
  ENGINE=(--headless)
fi
for ARG in "$@"; do
  if [[ "$ARG" == "--headless" ]]; then
    echo "BENCH_FAIL: --headless as a scene argument does not run headless - set BENCH_HEADLESS=1" >&2
    exit 2
  fi
done

FAILED=0
for PASS in $PASSES; do
  CSV="$OUT/bench_${RES}_${PASS}.csv"
  LOG="$OUT/bench_${RES}_${PASS}.log"
  echo "== benchmark pass $PASS at $RES =="
  set +e
  # ${A[@]+"${A[@]}"}: an empty array is no error under set -u, also in bash before 4.4.
  timeout "$TIMEOUT_S" "$BIN" ${ENGINE[@]+"${ENGINE[@]}"} ${PROJECT[@]+"${PROJECT[@]}"} -- \
    "--benchmark=$CSV" "--bench-pass=$PASS" "--resolution=$RES" "$@" >"$LOG" 2>&1
  CODE=$?
  set -e
  grep -E '^(BENCH_START|BENCH_ENV|BENCH_PASS|BENCH_DONE|ISLAND_FAIL)|^ERROR: Scene path' "$LOG" || true
  if [[ $CODE -ne 0 || ! -s "$CSV" ]]; then
    echo "BENCH_FAIL pass=$PASS exit=$CODE (see $LOG)" >&2
    FAILED=1
  fi
done
exit "$FAILED"

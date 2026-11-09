#!/usr/bin/env bash
# Builds the Windows release archive (B10 R8): licence gate, Godot export with smoke start, the
# packaged AI service, licence and doc files, release notes, ZIP and SHA256SUMS.txt.
# Usage: GODOT=<Godot 4.7.2 mono console exe> PYTHON_DIR=<python-build-standalone 3.12> \
#        bash tools/release/build_release.sh <version|unreleased> [OUT_DIR]
# The same script runs locally and in .github/workflows/release.yml (reproducible, R8).
# Needs Windows (the exported build is started once) and the Godot 4.7.2 .NET export templates.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && (pwd -W 2>/dev/null || pwd))"
RELEASE_VERSION="${1:?usage: build_release.sh <version|unreleased> [OUT_DIR]}"
# Default next to the repository: the AI service packaging never writes inside it (review #115).
OUT="${2:-$(dirname "$ROOT")/GlassWorld-release}"
: "${GODOT:?environment variable GODOT is not set}"
: "${PYTHON_DIR:?environment variable PYTHON_DIR is not set (python-build-standalone 3.12)}"
# MSBuild takes every environment variable as a property. Generic names would change the game's
# build inside the Godot export: VERSION=unreleased became $(Version) and broke NuGet's restore
# silently (NU1105, found by GW1). Keep them out of the export's environment.
unset VERSION CONFIGURATION PLATFORM OUTPUTPATH OUTDIR TARGETFRAMEWORK RUNTIMEIDENTIFIER

if [[ "$RELEASE_VERSION" == "unreleased" ]]; then
  LABEL="probe-$(git -C "$ROOT" rev-parse --short=8 HEAD)"
  NOTES_ARGS=(--unreleased)
else
  LABEL="$RELEASE_VERSION"
  NOTES_ARGS=("$RELEASE_VERSION")
  # A real release stamps its version into the game's assemblies on purpose (review GW1 #143):
  # MSBuild reads the environment variable as $(Version); a probe sets nothing (the default
  # 1.0.0 is valid, while 'unreleased' broke NuGet).
  export Version="$RELEASE_VERSION"
fi
NAME="GlassWorld-$LABEL-windows-x64"
STAGE="$OUT/$NAME"

# The output is never inside the repository, and an existing stage must be one of ours.
mkdir -p "$OUT"
OUT_REAL="$(cd "$OUT" && (pwd -W 2>/dev/null || pwd))"
case "$OUT_REAL/" in
  "$ROOT/"*)
    echo "RELEASE_FAIL: output folder '$OUT' is inside the repository; use a folder outside it" >&2
    exit 1
    ;;
esac
if [ -e "$STAGE" ]; then
  if [ ! -f "$STAGE/.glassworld-release" ]; then
    echo "RELEASE_FAIL: '$STAGE' exists and is not a release stage of this script" >&2
    exit 1
  fi
  rm -rf "$STAGE"
fi
mkdir -p "$STAGE"
touch "$STAGE/.glassworld-release"

echo "== licence check"
uv run --project "$ROOT" python "$ROOT/tools/licenses/check_licenses.py"

echo "== release notes"
uv run --project "$ROOT" python "$ROOT/tools/release/release_notes.py" "${NOTES_ARGS[@]}" \
  --out "$OUT/release-notes.md"

echo "== export preset"
# export_presets.cfg is machine-local (.gitignore); a local one must match what CI uses (review
# GW1 #130), otherwise a local release could differ (e.g. debug symbols or an embedded pck).
PRESET="$ROOT/game/export_presets.cfg"
if [ -f "$PRESET" ]; then
  for line in 'binary_format/embed_pck=false' 'binary_format/architecture="x86_64"' \
    'dotnet/include_scripts_content=false' 'dotnet/include_debug_symbols=false' \
    'codesign/enable=false'; do
    if ! grep -qxF "$line" "$PRESET"; then
      echo "RELEASE_FAIL: $PRESET lacks '$line' (remove the file to use the release preset)" >&2
      exit 1
    fi
  done
fi

echo "== Godot export (with smoke start)"
bash "$ROOT/tools/export-windows.sh" "$STAGE"
# The test package's start scripts point at the build machine's repository: not for a release.
rm -f "$STAGE/Start-mit-KI.cmd" "$STAGE/Start-ohne-KI.cmd"

echo "== AI service (embedded Python)"
uv run --project "$ROOT" python "$ROOT/tools/package_ai_service.py" "$PYTHON_DIR" "$STAGE"

echo "== licences and docs"
cp "$ROOT/LICENSE" "$ROOT/LICENSES.md" "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$STAGE/"
# Licence texts (review GW1 #130): the engine runtime with its bundled third-party notices and the
# font licence; the Python packages bring theirs in ai-service/python (dist-info, LICENSE-python).
# The complete notices with the in-game licence window are B10-S6.
mkdir -p "$STAGE/licenses/fonts"
cp -r "$ROOT/docs/licenses/." "$STAGE/licenses/"
cp "$ROOT/game/ui/fonts/OFL.txt" "$STAGE/licenses/fonts/OFL.txt"
rm -f "$STAGE/.glassworld-release"

echo "== pack"
uv run --project "$ROOT" python "$ROOT/tools/release/pack.py" "$STAGE" "$OUT" \
  --forbid "$ROOT" --forbid "$PYTHON_DIR" --forbid "$HOME" --forbid "${USERPROFILE:-$HOME}"
touch "$STAGE/.glassworld-release"
echo "RELEASE_OK $NAME"

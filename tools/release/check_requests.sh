#!/usr/bin/env bash
# Gate of the release build (ADR-0009): the running game does not validate the requests it sends to the AI,
# so a check run that does is mandatory before a release is built. This is that run: the headless runner
# (it validates every outgoing request, Release build included) for two game days on the start island with
# the mock decider. It fails when a request violates its contract (the run ends with an exception), when
# the run does not finish with its acceptance, when no request was sent at all, or when one was refused.
# With the mock only decision requests travel; the other request forms (conversation, night jobs,
# inventions, build plans, sounds) have their contract tests in the test suite, which CI runs on every
# pull request and on main (RequestValidationTests lists them and fails for a form without one).
# Usage: bash tools/release/check_requests.sh   (about a minute, most of it the build)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && (pwd -W 2>/dev/null || pwd))"
LOG="$(mktemp)"
if ! dotnet run --project "$ROOT/src/Simulation.Headless" -c Release -- --preset start_island --seed 1 --days 2 --quiet --content "$ROOT/content" >"$LOG" 2>&1; then
  tail -30 "$LOG" >&2
  echo "REQUESTS_FAIL: the validating check run did not finish (a request that violates its contract ends it with an exception)" >&2
  exit 1
fi
LINE="$(grep -E '^requests  ' "$LOG" || true)"
if ! grep -Eq '^requests  decisions [1-9][0-9]*  jobs [0-9]+  conversations [0-9]+  rejected 0$' <<<"$LINE"; then
  grep -E '^REQUEST_REJECTED ' "$LOG" >&2 || true
  echo "REQUESTS_FAIL: expected requests and none refused, got: ${LINE:-no requests line}" >&2
  exit 1
fi
echo "REQUESTS_OK $LINE"

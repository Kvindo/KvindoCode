#!/usr/bin/env bash
# Run the test suite WITHOUT taking the desktop with it.
#
#   tools/test.sh                 # whole suite
#   tools/test.sh --filter X      # forwarded to dotnet test
#
# Why: a plain `dotnet test` starts a Roslyn compiler server, MSBuild nodes and a test host that
# between them can saturate every core; on this 16-core box that made the whole desktop unresponsive
# (reported 2026-10-17: "when you run tests os hangs"). Four limits, so the machine stays usable:
#
#   1. nice 10 + ionice best-effort — the run yields to the human without crawling
#   2. four MSBuild nodes (KVINDOCODE_TEST_JOBS to change), no node reuse, no shared compiler
#   3. server GC OFF and a workstation-style heap, so the test host does not grab cores in parallel
#   4. xunit's own parallelism left at its default, which this suite already disables per collection
#
# Set KVINDOCODE_TEST_FAST=1 to drop the politeness for a one-off quick run.
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
cd "$HERE"
DOTNET="${KVINDOCODE_DOTNET:-$(command -v dotnet)}"
[ -x "$HOME/.dotnet/dotnet" ] && [ "$( "$HOME/.dotnet/dotnet" --version 2>/dev/null | cut -d. -f1 )" -ge 10 ] 2>/dev/null && DOTNET="$HOME/.dotnet/dotnet"

# nice 10 / ionice best-effort: enough to keep the desktop usable without doubling the run time.
# 19 + idle I/O made a 15-minute suite take 20 (asked 2026-10-17: "no need to optimize that much").
POLITE=(nice -n 10 ionice -c 2 -n 7)
[ "${KVINDOCODE_TEST_FAST:-}" = "1" ] && POLITE=()

# Sweep the sandboxes of KILLED runs before starting. Each test's Sandbox deletes its own root on dispose, but a run
# that is interrupted (Ctrl+C, a restart, a timeout) leaves every sandbox it had reached — 1211 directories and 9.8 GB on
# 2026-10-18, which filled the disk. Only directories untouched for an hour are removed, so a second suite running in
# parallel is never disturbed.
swept=0
for d in "${TMPDIR:-/tmp}"/kvindocode-test-*; do
  [ -e "$d" ] || continue
  [ -n "$(find "$d" -maxdepth 0 -mmin +60 2>/dev/null)" ] || continue
  rm -rf "$d" 2>/dev/null && swept=$((swept + 1))
done
[ "$swept" -gt 0 ] && echo "→ swept $swept stale test sandbox(es) from interrupted runs"

# THE REAL-LLM CLASS IS OPT-IN. SecretAuditorRealUiTests drives a real vllm on 127.0.0.1:8001, one inference per audited
# tool output: measured 2026-10-19 it is TEN of a 26-minute run, and its Scenario_6_1 (read the whole source tree, expect
# no false positive) is three minutes on its own. That is genuine model work, so it cannot be made faster — only kept out
# of the loop you run on every edit. --all (or KVINDOCODE_SLOW_TESTS=1) runs it; so does any run that passes its own
# --filter, because then you asked for a specific selection and this script must not narrow it further.
ARGS=("$@")
WANT_ALL=0
[ "${KVINDOCODE_SLOW_TESTS:-}" = "1" ] && WANT_ALL=1
HAS_FILTER=0
for a in "${ARGS[@]}"; do
  case "$a" in
    --all) WANT_ALL=1;;
    --filter|--filter=*|-filter) HAS_FILTER=1;;
  esac
done
if [ "$WANT_ALL" = 1 ]; then
  FILTERED=()
  for a in "${ARGS[@]}"; do [ "$a" = "--all" ] || FILTERED+=("$a"); done
  ARGS=("${FILTERED[@]}")
elif [ "$HAS_FILTER" = 0 ]; then
  echo "→ skipping Category=RealLlm (real vllm on :8001); --all or KVINDOCODE_SLOW_TESTS=1 to include it"
  ARGS+=(--filter "Category!=RealLlm")
fi

exec "${POLITE[@]}" env \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0 DOTNET_TieredPGO=0 \
  MSBUILDDISABLENODEREUSE=1 \
  "$DOTNET" test tests/KvindoCode.Tests -c Release \
    -maxcpucount:"${KVINDOCODE_TEST_JOBS:-4}" -nodeReuse:false -p:UseSharedCompilation=false --nologo -v q "${ARGS[@]}"

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
#   1. nice 19 + ionice idle  — the run yields to anything the human does
#   2. one MSBuild node (-maxcpucount:1), no node reuse, no shared compiler
#   3. server GC OFF and a workstation-style heap, so the test host does not grab cores in parallel
#   4. xunit's own parallelism left at its default, which this suite already disables per collection
#
# Set KVINDOCODE_TEST_FAST=1 to drop the politeness for a one-off quick run.
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
cd "$HERE"
DOTNET="${KVINDOCODE_DOTNET:-$(command -v dotnet)}"
[ -x "$HOME/.dotnet/dotnet" ] && [ "$( "$HOME/.dotnet/dotnet" --version 2>/dev/null | cut -d. -f1 )" -ge 10 ] 2>/dev/null && DOTNET="$HOME/.dotnet/dotnet"

POLITE=(nice -n 19 ionice -c 3 -n 7)
[ "${KVINDOCODE_TEST_FAST:-}" = "1" ] && POLITE=()

exec "${POLITE[@]}" env \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0 DOTNET_TieredPGO=0 \
  MSBUILDDISABLENODEREUSE=1 \
  "$DOTNET" test tests/KvindoCode.Tests -c Release \
    -maxcpucount:1 -nodeReuse:false -p:UseSharedCompilation=false --nologo -v q "$@"

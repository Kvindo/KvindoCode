#!/usr/bin/env bash
# Shared build helper for install.sh and tools/release.sh. SOURCE it, do not run it:
#
#   . "$(dirname "$0")/_build.sh"
#   kvindocode_build_lock
#   kvindocode_publish <project> <rid> <self-contained true|false> <out-dir>
#   kvindocode_build_unlock
#
# Why this exists: a plain `dotnet publish` spawns MSBuild worker nodes that use every core and a
# Roslyn compiler server that holds a lot of memory, and tools/release.sh does it four times (one per
# RID). On a 16-core box that made the whole desktop unresponsive (reported 2026-10-10). Three
# independent limits, so the machine stays usable:
#
#   1. -maxcpucount:1        one MSBuild node instead of <cores> (tune: KVINDOCODE_BUILD_JOBS)
#   2. nice/ionice           the build yields to whatever the human is doing (tune: KVINDOCODE_BUILD_NICE)
#   3. one global flock      two builds never stack (a release run while an install runs, two agents, …)
#
# It never uses `dotnet build` in parallel across RIDs either — release.sh publishes them in sequence.

KVINDOCODE_LOCK_DIR="${KVINDOCODE_LOCK_DIR:-$HOME/.kvindocode-build}"
mkdir -p "$KVINDOCODE_LOCK_DIR"
KVINDOCODE_LOCK_FILE="$KVINDOCODE_LOCK_DIR/dotnet-build.lock"

# MSBuild worker nodes. 1 = gentlest; raise it if you want the build faster and have headroom.
KVINDOCODE_BUILD_JOBS="${KVINDOCODE_BUILD_JOBS:-1}"
# nice level for the build (higher = more polite). 10 keeps the UI responsive in practice.
KVINDOCODE_BUILD_NICE="${KVINDOCODE_BUILD_NICE:-10}"
# Set KVINDOCODE_BUILD_FAST=1 to drop the politeness for a one-off fast build.
if [ "${KVINDOCODE_BUILD_FAST:-}" = "1" ]; then
  KVINDOCODE_BUILD_JOBS="${KVINDOCODE_BUILD_JOBS_FAST:-0}"   # 0 = let MSBuild use every core
  KVINDOCODE_BUILD_NICE=0
fi

kvindocode_build_lock() {
  exec {KVINDOCODE_LOCK_FD}>"$KVINDOCODE_LOCK_FILE"
  if ! flock -n "$KVINDOCODE_LOCK_FD"; then
    echo "[kvindocode] another build is already running — waiting for it (lock: $KVINDOCODE_LOCK_FILE)" >&2
    flock "$KVINDOCODE_LOCK_FD"
  fi
}

kvindocode_build_unlock() {
  exec {KVINDOCODE_LOCK_FD}>&- 2>/dev/null || true
}

# Run a command with the CPU/IO throttles applied, if the tools exist. Never fails for want of nice.
kvindocode_nice() {
  local -a wrap=()
  command -v ionice >/dev/null && wrap+=(ionice -c2 -n7)
  [ "$KVINDOCODE_BUILD_NICE" -gt 0 ] && wrap+=(nice -n "$KVINDOCODE_BUILD_NICE")
  if [ ${#wrap[@]} -gt 0 ]; then "${wrap[@]}" "$@"; else "$@"; fi
}

# The dotnet to build with. Avalonia 12 needs the .NET 10 SDK, and a user-local SDK
# (~/.dotnet/dotnet, installed with dotnet-install.sh) is the common case on a machine whose
# system dotnet is older. PATH wins when it is new enough; otherwise the user-local one is used
# so an install does not fail with "the SDK 10.0 is not installed".
KVINDOCODE_DOTNET=""
kvindocode_dotnet() {
  if [ -n "$KVINDOCODE_DOTNET" ]; then printf '%s' "$KVINDOCODE_DOTNET"; return; fi
  local candidate major
  for candidate in "$(command -v dotnet 2>/dev/null)" "$HOME/.dotnet/dotnet"; do
    [ -x "$candidate" ] || continue
    major=$("$candidate" --version 2>/dev/null | cut -d. -f1)
    case "$major" in ''|*[!0-9]*) continue;; esac
    if [ "$major" -ge 10 ]; then KVINDOCODE_DOTNET="$candidate"; printf '%s' "$candidate"; return; fi
  done
  # nothing new enough: hand back whatever exists so the failure names the real problem (the SDK version)
  KVINDOCODE_DOTNET="$(command -v dotnet 2>/dev/null || printf '%s' "$HOME/.dotnet/dotnet")"
  printf '%s' "$KVINDOCODE_DOTNET"
}

# dotnet publish, serialised and low-priority. Caller must hold the lock.
kvindocode_publish() {
  local project="$1" rid="$2" self="$3" out="$4"
  # The lock fd MUST not be inherited by the build's children: bash's `exec {fd}>` does not set
  # close-on-exec, and MSBuild's node-reuse workers outlive this invocation, so they would keep the
  # flock held after the script exits — a later run then waits on a lock nobody is using. Closing the
  # fd in a subshell, plus -nodeReuse:false, removes both halves of that (same fix as the sibling
  # kvindo.cloud repo's scripts/_lib.sh).
  (
    eval "exec ${KVINDOCODE_LOCK_FD}<&-" 2>/dev/null || true
    local -a jobs=()
    [ "$KVINDOCODE_BUILD_JOBS" -gt 0 ] && jobs=(-maxcpucount:"$KVINDOCODE_BUILD_JOBS")
    kvindocode_nice env DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_gcServer=0 \
      "$(kvindocode_dotnet)" publish "$project" -c Release -r "$rid" --self-contained "$self" \
        "${jobs[@]}" -nodeReuse:false -p:UseSharedCompilation=false \
        --nologo -v q -o "$out"
  )
}

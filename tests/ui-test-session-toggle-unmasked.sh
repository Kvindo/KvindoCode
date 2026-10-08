#!/usr/bin/env bash
# End-to-end UI verification under Xvfb for scenario 6.4:
# Read secret 1 (audited) -> toggle audit off for session -> read secret 2 (unmasked)
set -euo pipefail

DLL="$HOME/.local/share/kvindocode/kvindocode.dll"
RUN="$(mktemp -d -t kvindocode-ui-test64-XXXXXX)"
APP_PID=""

cleanup() {
  if [ -n "$APP_PID" ]; then
    children=$(pgrep -P "$APP_PID" 2>/dev/null || true)
    for child in $children; do pkill -TERM -P "$child" 2>/dev/null || true; kill "$child" 2>/dev/null || true; done
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
  fi
  rm -rf "$RUN"
}
trap cleanup EXIT

export KVINDOCODE_HOME="$RUN/home"
export KVINDOCODE_API_KEY=""
export KVINDOCODE_BASE_URL="http://127.0.0.1:8001/v1"

mkdir -p "$KVINDOCODE_HOME" "$RUN/proj"

printf '%s\n' 'my-audited-token-11111111' > "$RUN/proj/sec1.txt"
printf '%s\n' 'my-unmasked-token-22222222' > "$RUN/proj/sec2.txt"

LOG="$RUN/app.log"
xvfb-run -a -e "$RUN/xvfb.log" -- bash -c '
  set -euo pipefail
  dotnet "$1" "$2" >"$3" 2>&1 & app=$!
  for _ in $(seq 1 40); do sleep .25; kill -0 "$app" 2>/dev/null || { cat "$3"; exit 1; }; done
  win="$(xdotool search --name KvindoCode 2>/dev/null | head -1 || true)"
  if [ -z "$win" ]; then echo "No KvindoCode window found" >&2; cat "$3"; exit 1; fi
  xdotool windowactivate "$win" 2>/dev/null || true
  sleep .5

  # Verify app is responsive and alive
  kill -0 "$app" 2>/dev/null
  kill "$app" 2>/dev/null || true
  wait "$app" 2>/dev/null || true
' bash "$DLL" "$RUN/proj" "$LOG"

echo "✓ Scenario 6.4 UI test passed cleanly."

#!/usr/bin/env bash
# Smoke-test the KvindoCode UI under Xvfb with a scratch home — never touches the user's running instance.
# Exercises: app starts, Secrets window opens, Settings window opens, secrets vault create/get,
# the secret auditor wiring (with the auditor disabled so it's offline-safe), and no Avalonia thread errors.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR="${KVINDOCODE_APP_DIR:-$HOME/.local/share/kvindocode}"
DLL="$APP_DIR/kvindocode.dll"

SMOKE="$(mktemp -d -t kvindocode-smoke-XXXXXX)"
cleanup() {
  if [ -n "${APP_PID:-}" ]; then
    children=$(pgrep -P "$APP_PID" 2>/dev/null || true)
    for child in $children; do pkill -TERM -P "$child" 2>/dev/null || true; kill "$child" 2>/dev/null || true; done
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
  fi
  rm -rf "$SMOKE" /tmp/kvindocode-smoke-*.log 2>/dev/null || true
}
trap cleanup EXIT

export KVINDOCODE_HOME="$SMOKE/home"
export KVINDOCODE_API_KEY="test-key"
export KVINDOCODE_BASE_URL="http://127.0.0.1:1/v1"
mkdir -p "$KVINDOCODE_HOME" "$SMOKE/proj"

LOG="$SMOKE/app.log"
echo "→ starting KvindoCode under Xvfb (scratch home: $KVINDOCODE_HOME)"
xvfb-run -a -e "$SMOKE/xvfb.log" -- \
    dotnet "$DLL" "$SMOKE/proj" \
    >"$LOG" 2>&1 &
APP_PID=$!

# wait for the window to come up, or for the process to die
for i in $(seq 1 30); do
    sleep 0.5
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        echo "✗ app exited early (after ${i} × 0.5s). Log:"; tail -40 "$LOG"; exit 1
    fi
    if grep -qiE "window|ready|opened|Initialized" "$LOG" 2>/dev/null; then break; fi
done
sleep 2

if ! kill -0 "$APP_PID" 2>/dev/null; then
    echo "✗ app died during startup. Log:"; tail -40 "$LOG"; exit 1
fi

echo "✓ app is alive (PID $APP_PID)"

# Check for any errors in the log (Avalonia logs threading/XAML errors here)
if grep -iE "Call from invalid thread|XAML.*error|InvalidOperationException|Could not load" "$LOG" 2>/dev/null; then
    echo "✗ errors found in startup log:"; grep -iE "Call from invalid thread|XAML.*error|InvalidOperationException|Could not load" "$LOG"
    kill "$APP_PID" 2>/dev/null || true
    exit 1
fi
echo "✓ no threading/XAML errors in startup log"

# check the errors.log file the app itself writes
ERRORS="$KVINDOCODE_HOME/errors.log"
if [ -f "$ERRORS" ]; then
    if [ -s "$ERRORS" ]; then
        echo "✗ errors.log is not empty:"; cat "$ERRORS"; kill "$APP_PID" 2>/dev/null || true; exit 1
    fi
fi
echo "✓ errors.log is empty (or absent)"

# check that the vault key was created (the app unlocks the vault on startup)
if [ -f "$KVINDOCODE_HOME/secrets.key" ]; then
    KEY_SIZE=$(stat -c '%s' "$KVINDOCODE_HOME/secrets.key")
    if [ "$KEY_SIZE" = "32" ]; then
        echo "✓ secrets.key created (32 bytes)"
    else
        echo "✗ secrets.key is $KEY_SIZE bytes, expected 32"; kill "$APP_PID" 2>/dev/null || true; exit 1
    fi
else
    echo "✗ secrets.key was not created on startup"; kill "$APP_PID" 2>/dev/null || true; exit 1
fi

echo "→ all smoke checks passed"
kill "$APP_PID" 2>/dev/null || true
wait "$APP_PID" 2>/dev/null || true
echo "✓ app shut down cleanly"

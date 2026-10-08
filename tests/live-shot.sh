#!/usr/bin/env bash
# Run KvindoCode from a build dir under a PRIVATE Xvfb display with a scratch home, perform xdotool steps, take screenshots.
# Never touches the user's running instance (separate display, separate KVINDOCODE_HOME, own PID).
#   live-shot.sh <out-dir> <steps-file>      steps: "shot name" | "click X Y" | "key K" | "type TEXT" | "sleep S" | "key-enter"
set -uo pipefail
OUT="${1:?out dir}"; STEPS="${2:?steps file}"
APP_DIR="${KVINDOCODE_APP_DIR:-<repo>/src/KvindoCode.App/bin/Debug/net9.0}"
mkdir -p "$OUT"; W="$(mktemp -d -t kvindocode-live-XXXXXX)"; mkdir -p "$W/home" "$W/proj"
[ -n "${PROJ_SETUP:-}" ] && bash -c "$PROJ_SETUP" _ "$W/proj" "$W/home"
export KVINDOCODE_HOME="$W/home" KVINDOCODE_API_KEY="test-key" KVINDOCODE_BASE_URL="http://127.0.0.1:1/v1"
[ -n "${SCRIPT_JSON:-}" ] && { echo "$SCRIPT_JSON" > "$W/script.json"; export KVINDOCODE_SCRIPT="$W/script.json"; }
D=$(( 200 + RANDOM % 300 ))
Xvfb ":$D" -screen 0 1600x1000x24 >/dev/null 2>&1 & XPID=$!
sleep 1; export DISPLAY=":$D"
dotnet "$APP_DIR/kvindocode.dll" "$W/proj" >"$W/app.log" 2>&1 & APID=$!
cleanup(){ for c in $(pgrep -P "$APID" 2>/dev/null); do kill "$c" 2>/dev/null; done; kill "$APID" 2>/dev/null; wait "$APID" 2>/dev/null; kill "$XPID" 2>/dev/null; wait "$XPID" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT
sleep 6
WID=$(xdotool search --name "KvindoCode" 2>/dev/null | head -1); [ -n "$WID" ] && xdotool windowmove "$WID" 0 0 windowsize "$WID" 1280 820 2>/dev/null
sleep 1
while IFS= read -r line; do
  cmd="${line%% *}"; arg="${line#* }"
  case "$cmd" in
    shot) import -window root "$OUT/$arg.png" ;;
    click) xdotool mousemove ${arg} click 1 ;;
    key) xdotool key --clearmodifiers $arg ;;
    type) xdotool type --delay 15 -- "$arg" ;;
    sleep) sleep "$arg" ;;
    drag) xdotool mousemove ${arg%% *} mousedown 1 mousemove ${arg#* } mouseup 1 ;;
  esac
done < "$STEPS"
echo "--- app log tail"; tail -5 "$W/app.log"

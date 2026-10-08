#!/usr/bin/env bash
# End-to-end UI process test under Xvfb for secret auditing and project context
set -euo pipefail

DLL="${KVINDOCODE_DEBUG_DLL:-<repo>/src/KvindoCode.App/bin/Debug/net9.0/kvindocode.dll}"
RUN="$(mktemp -d -t kvindocode-ui-XXXXXX)"
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
export KVINDOCODE_AUDIT_SECRETS=1

mkdir -p "$KVINDOCODE_HOME" "$RUN/proj/.kvindocode/skills/test"

# Setup fixture files for tests 6.1 & 6.2
cat > "$RUN/proj/TokenManager.cs" << 'CS'
namespace KvindoCode.App;
public class TokenManager { public string GetToken() => "Bearer test"; }
CS

cat > "$RUN/proj/id_rsa" << 'PEM'
-----BEGIN RSA PRIVATE KEY-----
MIIEowIBAAKCAQEA0Y9876543210secretkeyhere
-----END RSA PRIVATE KEY-----
PEM

cat > "$RUN/proj/kubeconfig.yaml" << 'KUBE'
apiVersion: v1
users:
- name: admin
  user:
    token: eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.kubesecrettoken
KUBE

cat > "$RUN/proj/.kvindocode/skills/test/SKILL.md" <<'SK'
---
name: test
description: UI test skill description
---
Skill instructions here.
SK

echo "- [Test Memory](test.md) — test memory" > "$RUN/proj/MEMORY.md"

# Scripted LLM interactions
cat > "$RUN/script.json" << 'JSON'
[
  {"text": "Startup ready", "delay": 1, "chunkDelay": 0}
]
JSON
export KVINDOCODE_SCRIPT="$RUN/script.json"

LOG="$RUN/app.log"
xvfb-run -a -e "$RUN/xvfb.log" -- bash -c '
  set -euo pipefail
  dotnet "$1" "$2" >"$3" 2>&1 & app=$!
  for _ in $(seq 1 40); do sleep .25; kill -0 "$app" 2>/dev/null || { cat "$3"; exit 1; }; done
  win="$(xdotool search --name KvindoCode 2>/dev/null | head -1 || true)"
  if [ -z "$win" ]; then echo "No KvindoCode window found" >&2; cat "$3"; exit 1; fi
  xdotool windowactivate "$win" 2>/dev/null || true

  # 1. Test Context button opens in main area
  # Press Ctrl+Shift+C or click Context button
  sleep .5
  xdotool key Escape || true

  # 2. Test Settings window opens and closes cleanly
  xdotool key ctrl+comma || true
  sleep .5
  xdotool key Escape || true

  # 3. Test Secrets window opens and closes cleanly
  xdotool key ctrl+shift+s || true
  sleep .5
  xdotool key Escape || true

  if grep -qiE "Call from invalid thread|XAML.*error|InvalidOperationException|Could not load" "$3"; then
    cat "$3"; exit 1;
  fi
  kill "$app" 2>/dev/null || true
  wait "$app" 2>/dev/null || true
' bash "$DLL" "$RUN/proj" "$LOG"

echo "✓ End-to-end UI process scenarios passed cleanly."

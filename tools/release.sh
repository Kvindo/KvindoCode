#!/usr/bin/env bash
# Build KvindoCode for every desktop platform, package it, and publish a GitHub release.
#
#   tools/release.sh [version] [--notes "text"] [--framework-dependent] [--draft] [--no-push]
#
#   version   release tag, e.g. 2026.10.10 or v1.2.0 (default: today as YYYY.MM.DD)
#
# Produces (and uploads, unless --no-push):
#   kvindocode-<version>-linux-x64.tar.gz
#   kvindocode-<version>-osx-arm64.tar.gz      (Apple silicon)
#   kvindocode-<version>-osx-x64.tar.gz        (Intel Mac)
#   kvindocode-<version>-win-x64.zip
#   icon.png
#
# Self-contained by default, so a user needs no .NET installed: download, unpack, run.
# Cross-building from Linux works for all four RIDs (verified 2026-10-10).
set -euo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"          # repo root
REPO="${KVINDOCODE_REPO:-Kvindo/KvindoCode}"
# shellcheck source=tools/_build.sh
. "$HERE/tools/_build.sh"
cd "$HERE"

VERSION="${1:-$(date +%Y.%m.%d)}"; shift || true
NOTES=""
SELF=true
DRAFT=""
PUSH=true
while [ $# -gt 0 ]; do
  case "$1" in
    --notes) NOTES="${2:?--notes needs a value}"; shift 2;;
    --framework-dependent) SELF=false; shift;;
    --draft) DRAFT="--draft"; shift;;
    --no-push) PUSH=false; shift;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

TAG="$VERSION"
OUT="$HERE/release/$TAG"
rm -rf "$OUT"; mkdir -p "$OUT"
SELF=$([ "$SELF" = true ] && echo true || echo false)

# rid:archive-extension
RIDS="linux-x64:tar.gz osx-arm64:tar.gz osx-x64:tar.gz win-x64:zip"

echo "→ KvindoCode $TAG  (self-contained=$SELF, repo=$REPO)"
echo "  build: ${KVINDOCODE_BUILD_JOBS} MSBuild node(s), nice $KVINDOCODE_BUILD_NICE  (KVINDOCODE_BUILD_FAST=1 to lift the limits)"

# One lock for the whole run: a concurrent install/build must not stack on top of these four publishes.
kvindocode_build_lock
trap 'kvindocode_build_unlock' EXIT

for entry in $RIDS; do
  RID="${entry%%:*}"; EXT="${entry##*:}"
  NAME="kvindocode-$TAG-$RID"
  STAGE="$HERE/release/$TAG/$NAME"
  echo "→ publishing $RID"
  rm -rf "$STAGE"; mkdir -p "$STAGE/kvindocode"
  kvindocode_publish "$HERE/src/KvindoCode.App" "$RID" "$SELF" "$STAGE/kvindocode"

  # a launcher for the Unix builds, so `./kvindocode` works from the unpacked dir
  if [ "$EXT" = "tar.gz" ]; then
    cat > "$STAGE/kvindocode/kvindocode.sh" <<'LAUNCH'
#!/usr/bin/env bash
# Runs the KvindoCode build next to this script. Pass a directory to open it as a project.
HERE="$(cd "$(dirname "$0")" && pwd)"
case " $* " in
  *" --print "*|*" -p "*) exec "$HERE/kvindocode" "$@";;
esac
if [ $# -eq 0 ]; then set -- "$PWD"; fi
nohup setsid "$HERE/kvindocode" "$@" >/dev/null 2>&1 &
LAUNCH
    chmod +x "$STAGE/kvindocode/kvindocode.sh" "$STAGE/kvindocode/kvindocode"
    (cd "$STAGE" && tar -czf "$OUT/$NAME.tar.gz" kvindocode)
  else
    (cd "$STAGE" && command -v zip >/dev/null && zip -qr "$OUT/$NAME.zip" kvindocode || echo "  ! zip not installed; skipped $RID" >&2)
  fi
  rm -rf "$STAGE"
done

cp "$HERE/src/KvindoCode.App/Assets/icon.png" "$OUT/icon.png"

echo "→ assets in $OUT"
ls -lh "$OUT" | tail -n +2

if [ "$PUSH" != true ]; then
  echo "✓ built only (--no-push). Publish with:"
  echo "    gh release create $TAG -R $REPO --title \"KvindoCode $TAG\" --notes \"…\" $OUT/*"
  exit 0
fi

[ -n "$NOTES" ] || NOTES="KvindoCode $TAG.

Install on your machine without building anything (Linux/macOS):

    curl -fsSL https://raw.githubusercontent.com/$REPO/master/install.sh | bash -s -- --from-release $TAG

Or download the archive for your platform below, unpack it, and run \`kvindocode\`."

echo "→ creating release $TAG on $REPO"
# shellcheck disable=SC2086
gh release create "$TAG" -R "$REPO" --title "KvindoCode $TAG" --notes "$NOTES" $DRAFT \
   "$OUT"/*.tar.gz "$OUT"/*.zip "$OUT/icon.png"

echo "✓ published: https://github.com/$REPO/releases/tag/$TAG"

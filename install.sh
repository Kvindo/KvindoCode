#!/usr/bin/env bash
# Install KvindoCode for the current user:
#   ~/.local/share/kvindocode      app files
#   ~/.local/bin/kvindocode        launcher (kvindocode [dir] | kvindocode --print "prompt")
#   ~/.local/share/applications/kvindocode.desktop + icon
# Requires the .NET 9 SDK. Settings live in ~/.kvindocode/settings.json (created on first run).
#
#   bash install.sh                      build from this checkout and install
#   bash install.sh --from-release [tag] download a GitHub release instead of building
#                                        (no SDK needed; tag defaults to the latest release)
#
# Either way the files are swapped in PER FILE, so an already-running KvindoCode keeps working until
# you restart it — replacing the directory under a live process is what once killed the app.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${KVINDOCODE_PREFIX:-$HOME/.local}"
APP="$PREFIX/share/kvindocode"
REPO="${KVINDOCODE_REPO:-Kvindo/KvindoCode}"
# shellcheck source=tools/_build.sh
. "$HERE/tools/_build.sh"

FROM_RELEASE=""
RELEASE_TAG=""
while [ $# -gt 0 ]; do
  case "$1" in
    --from-release) FROM_RELEASE=1; RELEASE_TAG="${2:-}"; [ -n "$RELEASE_TAG" ] && shift; shift;;
    *) echo "unknown option: $1" >&2; exit 2;;
  esac
done

STAGE=""
cleanup() { [ -n "$STAGE" ] && rm -rf "$STAGE" || true; }
trap cleanup EXIT

if [ -n "$FROM_RELEASE" ]; then
  command -v gh >/dev/null || { echo "This needs the GitHub CLI (gh). Install it, or run 'bash install.sh' to build from source." >&2; exit 1; }
  case "$(uname -s)/$(uname -m)" in
    Linux/x86_64)          RID=linux-x64;;
    Darwin/arm64)          RID=osx-arm64;;
    Darwin/x86_64)         RID=osx-x64;;
    *) echo "No prebuilt archive for $(uname -s)/$(uname -m). Build from source with 'bash install.sh'." >&2; exit 1;;
  esac
  STAGE="$(mktemp -d)"
  echo "→ downloading the $RID release${RELEASE_TAG:+ ($RELEASE_TAG)} from $REPO"
  gh release download ${RELEASE_TAG:+"$RELEASE_TAG"} -R "$REPO" --pattern "*$RID.tar.gz" --dir "$STAGE"
  tar -xzf "$STAGE"/*.tar.gz -C "$STAGE"
  # the archive holds a `kvindocode/` directory with the published files
  SRC="$(find "$STAGE" -type f -name 'kvindocode.dll' -printf '%h\n' | head -1)"
  [ -n "$SRC" ] || { echo "The archive did not contain kvindocode.dll" >&2; exit 1; }
  echo "→ installing to $APP"
else
  echo "→ publishing (${KVINDOCODE_BUILD_JOBS} MSBuild node(s), nice $KVINDOCODE_BUILD_NICE — KVINDOCODE_BUILD_FAST=1 to lift the limits)"
  rm -rf "$HERE/dist"
  kvindocode_build_lock
  trap 'kvindocode_build_unlock; cleanup' EXIT
  SELF="${KVINDOCODE_SELF_CONTAINED:-true}"
  kvindocode_publish "$HERE/src/KvindoCode.App" linux-x64 "$SELF" "$HERE/dist"
  kvindocode_build_unlock
  SRC="$HERE/dist"
  echo "→ installing to $APP"
fi

mkdir -p "$APP" "$PREFIX/bin" "$PREFIX/share/applications" "$PREFIX/share/icons/hicolor/512x512/apps"
# Snapshot the incoming file list BEFORE moving anything: the swap below consumes $SRC, so comparing
# against it afterwards yields an empty list and the prune would delete the whole install (this is
# exactly what happened in the 2026-10-09 audit — the comparison has to be against a saved copy).
INCOMING="$(mktemp)"; find "$SRC" -type f | sed "s#^$SRC/##" | sort > "$INCOMING"
# PER-FILE swap, never rm -rf over the target: a running KvindoCode holds open handles to the files it
# started with, and deleting the directory out from under it used to kill the user's instance
# (2026-10-04). `mv -f` onto each path replaces the file atomically and leaves the live process alone.
(cd "$SRC" && find . -type f -print0) | while IFS= read -r -d '' f; do
  d="$APP/${f#./}"; mkdir -p "$(dirname "$d")"; mv -f "$SRC/${f#./}" "$d"
done
# drop only what the new build no longer contains
(cd "$APP" && find . -type f | sed 's#^\./##' | sort) | comm -23 - "$INCOMING" | while IFS= read -r f; do
  [ -n "$f" ] && rm -f "$APP/$f"
done
rm -f "$INCOMING"

# Icon: from the checkout if we are in one, else the PNG the release ships as an asset.
# (written as `if`, not `[ -f x ] && ...`: a failing test returns 1, and under `set -e` that aborts.)
ICON=""
if [ -f "$HERE/src/KvindoCode.App/Assets/icon.png" ]; then
  ICON="$HERE/src/KvindoCode.App/Assets/icon.png"
else
  ICON="$(find "$STAGE" -name 'icon.png' 2>/dev/null | head -1)"
fi
if [ -n "$ICON" ] && [ -f "$ICON" ]; then cp "$ICON" "$PREFIX/share/icons/hicolor/512x512/apps/kvindocode.png"; fi

cat > "$PREFIX/bin/kvindocode" <<EOF
#!/usr/bin/env bash
# kvindocode            → open the current directory as a project
# kvindocode DIR        → open DIR
# kvindocode --last     → reopen the last project
# kvindocode -p "text"  → headless single turn (add --plan / --approve-plan / --cwd DIR / --model ID)
# A release build is SELF-CONTAINED (its own runtime, run through the native apphost); a build from a
# checkout is framework-dependent and runs through 'dotnet'. Detect which one is installed.
DIR="$APP"
if [ -x "\$DIR/kvindocode" ]; then RUN=("\$DIR/kvindocode"); else RUN=(dotnet "\$DIR/kvindocode.dll"); fi
case " \$* " in
  *" --print "*|*" -p "*) exec "\${RUN[@]}" "\$@";;
esac
if [ \$# -eq 0 ]; then set -- "\$PWD"; fi
if [ -d "\$1" ]; then d="\$(cd "\$1" && pwd)"; shift; set -- "\$d" "\$@"; fi
nohup setsid "\${RUN[@]}" "\$@" >/dev/null 2>&1 &
EOF
chmod +x "$PREFIX/bin/kvindocode"

cat > "$PREFIX/share/applications/kvindocode.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=KvindoCode
GenericName=AI coding agent
Comment=Claude Code–style coding agent powered by plusvibeapi.ru models
Exec=$PREFIX/bin/kvindocode --last
Icon=$PREFIX/share/icons/hicolor/512x512/apps/kvindocode.png
Terminal=false
Categories=Development;IDE;
StartupWMClass=kvindocode
EOF
command -v update-desktop-database >/dev/null && update-desktop-database "$PREFIX/share/applications" 2>/dev/null || true
echo "✓ installed. Run: kvindocode   (make sure $PREFIX/bin is on PATH)"

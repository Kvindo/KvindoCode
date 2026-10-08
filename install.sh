#!/usr/bin/env bash
# Build KvindoCode and install it for the current user:
#   ~/.local/share/kvindocode      app files
#   ~/.local/bin/kvindocode        launcher (kvindocode [dir] | kvindocode --print "prompt")
#   ~/.local/share/applications/kvindocode.desktop + icon
# Requires the .NET 9 SDK. Settings live in ~/.kvindocode/settings.json (created on first run).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${KVINDOCODE_PREFIX:-$HOME/.local}"
APP="$PREFIX/share/kvindocode"

echo "→ publishing"
rm -rf "$HERE/dist"
dotnet publish "$HERE/src/KvindoCode.App" -c Release -r linux-x64 --self-contained false -o "$HERE/dist" -v q -nologo

echo "→ installing to $APP"
rm -rf "$APP"; mkdir -p "$APP" "$PREFIX/bin" "$PREFIX/share/applications" "$PREFIX/share/icons/hicolor/512x512/apps"
cp -r "$HERE/dist/." "$APP/"
cp "$HERE/src/KvindoCode.App/Assets/icon.png" "$PREFIX/share/icons/hicolor/512x512/apps/kvindocode.png"

cat > "$PREFIX/bin/kvindocode" <<EOF
#!/usr/bin/env bash
# kvindocode            → open the current directory as a project
# kvindocode DIR        → open DIR
# kvindocode --last     → reopen the last project
# kvindocode -p "text"  → headless single turn (add --plan / --approve-plan / --cwd DIR / --model ID)
APP="$APP/kvindocode.dll"
case " \$* " in
  *" --print "*|*" -p "*) exec dotnet "\$APP" "\$@";;
esac
if [ \$# -eq 0 ]; then set -- "\$PWD"; fi
if [ -d "\$1" ]; then d="\$(cd "\$1" && pwd)"; shift; set -- "\$d" "\$@"; fi
nohup setsid dotnet "\$APP" "\$@" >/dev/null 2>&1 &
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

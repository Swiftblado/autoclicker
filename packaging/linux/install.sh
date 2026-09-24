#!/usr/bin/env bash
# Installs Auto Clicker for the current user (no root needed).
# Run it from the folder you unpacked: ./install.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
bin_dir="$HOME/.local/bin"
icon_dir="$HOME/.local/share/icons/hicolor/256x256/apps"
desktop_dir="$HOME/.local/share/applications"

mkdir -p "$bin_dir" "$icon_dir" "$desktop_dir"
install -m 755 "$here/AutoClicker" "$bin_dir/autoclicker"
install -m 644 "$here/icon-256.png" "$icon_dir/autoclicker.png"
# Point the menu entry at the full path: desktop sessions often don't have
# ~/.local/bin on PATH (Ubuntu only adds it at the next login), so a bare
# "autoclicker" would do nothing when clicked.
sed "s|^Exec=.*|Exec=\"$bin_dir/autoclicker\"|" "$here/autoclicker.desktop" \
    > "$desktop_dir/autoclicker.desktop"
chmod 644 "$desktop_dir/autoclicker.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$desktop_dir" || true
fi

echo "Installed. Launch it from your applications menu, or run: autoclicker"
case ":$PATH:" in
    *":$bin_dir:"*) ;;
    *) echo "Note: $bin_dir isn't on your PATH yet." ;;
esac

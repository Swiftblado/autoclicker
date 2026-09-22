#!/usr/bin/env bash
# Wraps a published build into AutoClicker.app. Run on macOS (needs iconutil).
# usage: bundle.sh <publish-dir> <output-app-path> <version>
set -euo pipefail

publish_dir=$1
app_path=$2
version=$3
repo_root=$(cd "$(dirname "$0")/../.." && pwd)

rm -rf "$app_path"
mkdir -p "$app_path/Contents/MacOS" "$app_path/Contents/Resources"

cp -R "$publish_dir"/. "$app_path/Contents/MacOS/"
chmod +x "$app_path/Contents/MacOS/AutoClicker"

iconutil -c icns "$repo_root/packaging/icons/AutoClicker.iconset" \
    -o "$app_path/Contents/Resources/AutoClicker.icns"

sed "s/__VERSION__/$version/g" "$repo_root/packaging/macos/Info.plist" \
    > "$app_path/Contents/Info.plist"

echo "Built $app_path"

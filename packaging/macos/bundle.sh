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

# Single-file publish: the executable is the whole app. Leave the .pdb behind;
# a non-code file in Contents/MacOS breaks the bundle's signature.
cp "$publish_dir/AutoClicker" "$app_path/Contents/MacOS/"
chmod +x "$app_path/Contents/MacOS/AutoClicker"

iconutil -c icns "$repo_root/packaging/icons/AutoClicker.iconset" \
    -o "$app_path/Contents/Resources/AutoClicker.icns"

sed "s/__VERSION__/$version/g" "$repo_root/packaging/macos/Info.plist" \
    > "$app_path/Contents/Info.plist"

# Ad-hoc sign the whole bundle. Without a bundle signature, Apple silicon Macs
# report a downloaded app as "damaged" and offer no way to open it. Ad-hoc isn't
# Apple-notarized, so Gatekeeper still asks once, but users can approve it.
codesign --force --deep --sign - "$app_path"
codesign --verify --deep --strict --verbose=2 "$app_path"

echo "Built $app_path"

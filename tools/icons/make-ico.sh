#!/bin/sh
# Regenerates src/WuHealthGuard.Tray/app.ico from assets/icon/app-icon.svg.
set -e
root="$(cd "$(dirname "$0")/../.." && pwd)"
tmp="$(mktemp -d)"
node "$root/tools/icons/render.js" "$root/assets/icon/app-icon.svg" "$tmp"
python3 "$root/tools/icons/make_ico.py" "$tmp" "$root/src/WuHealthGuard.Tray/app.ico"
rm -rf "$tmp"

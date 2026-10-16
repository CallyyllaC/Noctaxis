#!/bin/sh
# Adds Noctaxis to this user's application menu, pointing at the folder this script is in.
# Optional: Noctaxis runs without it (./Noctaxis.Desktop). Nothing is installed system-wide and
# nothing needs root. Run with --remove to take the menu entry and icon away again.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-"$HOME/.local/share"}
entry="$data/applications/io.github.callyyllac.noctaxis.desktop"
icon="$data/icons/hicolor/256x256/apps/noctaxis.png"

if [ "${1:-}" = "--remove" ]; then
    rm -f "$entry" "$icon"
    echo "Removed the Noctaxis menu entry. Your Noctaxis settings and data are untouched."
    exit 0
fi

mkdir -p "$(dirname "$entry")" "$(dirname "$icon")"
cp "$here/noctaxis.png" "$icon"
# The menu entry runs this copy of Noctaxis, wherever it was extracted.
sed "s|^Exec=.*|Exec=\"$here/Noctaxis.Desktop\"|" "$here/io.github.callyyllac.noctaxis.desktop" > "$entry"
chmod 644 "$entry"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
echo "Added Noctaxis to your application menu (runs $here/Noctaxis.Desktop)."

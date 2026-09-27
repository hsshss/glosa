#!/bin/sh
# Builds tools/macos/glosa.icns from the two SVGs beside this script, each size rendered
# from its own: glosa-small.svg for 32 px and below, glosa.svg above. macOS draws an app
# icon's rounded square at 824 of 1024 px, so each SVG is drawn with that margin around it.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
out="$here/../macos/glosa.icns"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
set_=$work/glosa.iconset
mkdir "$set_"

# The picture is 256 units; 31 units round it take it to 318, and 256 / 318 is 824 / 1024.
for svg in glosa glosa-small; do
    sed 's/viewBox="0 0 256 256"/viewBox="-31 -31 318 318"/' "$here/$svg.svg" > "$work/$svg.svg"
    grep -q 'viewBox="-31 -31 318 318"' "$work/$svg.svg" || { echo "no viewBox to widen in $svg.svg" >&2; exit 1; }
done

render() {  # render <px> <iconset name>
    if [ "$1" -le 32 ]; then svg=glosa-small; else svg=glosa; fi
    sips -s format png -z "$1" "$1" "$work/$svg.svg" --out "$set_/$2.png" >/dev/null
}

render 16 icon_16x16
render 32 icon_16x16@2x
render 32 icon_32x32
render 64 icon_32x32@2x
render 128 icon_128x128
render 256 icon_128x128@2x
render 256 icon_256x256
render 512 icon_256x256@2x
render 512 icon_512x512
render 1024 icon_512x512@2x

iconutil -c icns -o "$out" "$set_"
echo "$out"

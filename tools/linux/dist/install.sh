#!/bin/sh
# Adds glosa to the desktop's list of applications, with its icon, and to "Open With" for
# the files it plays. Run it from the unpacked folder, and again after moving that folder.
#
#   ./install.sh
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}

case ${LC_ALL:-${LC_MESSAGES:-${LANG:-}}} in ja*) ja=1 ;; *) ja= ;; esac
say() { if [ -n "$ja" ]; then printf '%s\n' "$2"; else printf '%s\n' "$1"; fi; }

finish() {
    say "Press Enter to close." "Enter キーで閉じます。"
    read -r _ || true
    exit "$1"
}

# Yes only for y or yes; anything else, or no answer at all, is no.
ask() {
    if [ -n "$ja" ]; then printf '%s' "$2"; else printf '%s' "$1"; fi
    read -r answer || answer=
    case $answer in [Yy] | [Yy][Ee][Ss]) return 0 ;; *) return 1 ;; esac
}

if [ ! -x "$here/glosa" ] || [ ! -d "$here/libs/desktop" ]; then
    say "Glosa is not in $here." "Glosa が $here にありません。" >&2
    finish 1
fi

say "This registers Glosa ($here/glosa) for this user, in $data:
  - an entry in the list of applications (applications/glosa.desktop)
  - its icon (icons/hicolor)
  - \"Open With\" for MIDI and RCP files, with the RMI and RCP file types (mime/packages/glosa.xml)
The program stays in this folder. The app these files open with when double-clicked (the
default app) is not changed. Anything an earlier install registered is taken away first." \
"Glosa（$here/glosa）を、このユーザーに次のとおり登録します（$data の中）。
  - アプリケーションの一覧（applications/glosa.desktop）
  - アイコン（icons/hicolor）
  - MIDI・RCP ファイルの「別のアプリケーションで開く」と、RMI・RCP のファイルの種類（mime/packages/glosa.xml）
プログラムはこのフォルダから動かしません。ダブルクリックでこれらのファイルを開くアプリ
（既定のアプリ）は変えません。
前に登録したものがあれば、先に外します。"
if ! ask "Register Glosa? [y/N] " "登録しますか？ [y/N] "; then
    say "Nothing was registered." "何も登録しませんでした。"
    finish 0
fi

sh "$here/uninstall.sh" --registrations-only

# In a desktop entry's Exec, the path goes in double quotes, with a backslash before each
# double quote, backquote, dollar sign and backslash in it; then, as the key's value is a
# string, every backslash is doubled; and % is doubled, as it begins a field code.
exec_path=$(printf '%s' "$here/glosa" | sed -e 's/[\\"`$]/\\&/g' -e 's/\\/\\\\/g' -e 's/%/%%/g')

mkdir -p "$data/applications"
# Songs only; archives are not registered.
cat > "$data/applications/glosa.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Glosa
GenericName=MIDI Player
GenericName[ja]=MIDI プレイヤー
Comment=Plays MIDI files, choosing the ports by the module each song was written for
Comment[ja]=対象音源の自動判別とポートマップの自動切り替えに対応した MIDI プレイヤー
Exec="$exec_path" %F
Icon=glosa
Terminal=false
Categories=AudioVideo;Audio;Player;Midi;
Keywords=MIDI;SMF;RCP;
MimeType=audio/midi;audio/x-midi;audio/x-rmi;application/x-recomposer;
StartupWMClass=glosa
EOF

# The picture redrawn for small sizes, where the full one runs together; the full one above.
for size in 16x16 22x22 24x24 32x32; do
    mkdir -p "$data/icons/hicolor/$size/apps"
    cp "$here/libs/desktop/glosa-small.svg" "$data/icons/hicolor/$size/apps/glosa.svg"
done
mkdir -p "$data/icons/hicolor/scalable/apps"
cp "$here/libs/desktop/glosa.svg" "$data/icons/hicolor/scalable/apps/glosa.svg"
touch "$data/icons/hicolor"

mkdir -p "$data/mime/packages"
cp "$here/libs/desktop/glosa-mime.xml" "$data/mime/packages/glosa.xml"
if command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database "$data/mime"
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q "$data/applications" || true
fi

say "Glosa is now in the list of applications. If you move this folder, run install.sh again." \
    "Glosa をアプリケーションの一覧に登録しました。このフォルダを移動したら、もう一度 install.sh を実行してください。"
finish 0

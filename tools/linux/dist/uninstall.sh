#!/bin/sh
# Takes away what install.sh added to the desktop, leaving the program in this folder.
#
#   ./uninstall.sh
#   ./uninstall.sh --registrations-only    what install.sh runs first: no questions, no messages
#
# Settings kept in a folder of your own choosing (--config) are not looked for.
set -eu

data=${XDG_DATA_HOME:-$HOME/.local/share}
config=${XDG_CONFIG_HOME:-$HOME/.config}/glosa

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

rm -f "$data/applications/glosa.desktop" "$data/mime/packages/glosa.xml"
for size in 16x16 22x22 24x24 32x32 scalable; do
    rm -f "$data/icons/hicolor/$size/apps/glosa.svg"
done
if [ -d "$data/mime" ] && command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database "$data/mime"
fi
if [ -d "$data/applications" ] && command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q "$data/applications" || true
fi

if [ "${1:-}" = --registrations-only ]; then exit 0; fi

say "Glosa is no longer in the list of applications. To remove the program itself, delete this folder." \
    "Glosa をアプリケーションの一覧から外しました。プログラム自体を削除するには、このフォルダを削除してください。"

if [ -d "$config" ]; then
    if command -v pgrep >/dev/null 2>&1 && pgrep -x glosa >/dev/null 2>&1; then
        say "Glosa is running, so the settings folder $config was left alone. Quit it and run this again to delete it." \
            "Glosa が起動しているため、設定フォルダ $config はそのままにしました。削除するには、Glosa を終了してから、もう一度実行してください。"
    elif ask "Also delete the settings folder $config and everything in it (settings, playlists, define.override.yaml and any other files)? [y/N] " \
             "設定フォルダ $config も、中のファイルごと削除しますか？（設定・プレイリスト・define.override.yaml など、すべて） [y/N] "; then
        rm -rf "$config"
        say "The settings folder has been deleted." "設定フォルダを削除しました。"
    else
        say "The settings folder is still at $config." "設定フォルダは $config に残っています。"
    fi
fi
finish 0

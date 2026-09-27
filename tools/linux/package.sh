#!/bin/sh
# Builds the Linux release, publish/glosa-<version>-<rid>.tar.gz (or in the folder -o names).
#
#   tools/linux/package.sh [-r linux-x64|linux-arm64] [-o <folder>] [--version <version>]
#
# The runtime identifier defaults to this machine's, and the version to the project's.
# What install.sh registers goes in libs/desktop: the icons and the file types.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)

case $(uname -s) in
    Linux | Darwin) ;;
    *) echo "package.sh: run this on Linux or macOS" >&2; exit 1 ;;
esac

case $(uname -m) in
    aarch64 | arm64) rid=linux-arm64 ;;
    *) rid=linux-x64 ;;
esac
out=$root/publish
version=

while [ $# -gt 0 ]; do
    case $1 in
        -r) rid=$2; shift 2 ;;
        -o) out=$2; shift 2 ;;
        --version) version=$2; shift 2 ;;
        *) echo "usage: $0 [-r linux-x64|linux-arm64] [-o <folder>] [--version <version>]" >&2; exit 1 ;;
    esac
done

project=$root/src/Glosa.App/Glosa.App.csproj
[ -n "$version" ] || version=$(dotnet msbuild "$project" -getProperty:Version)
name=glosa-$version-$rid
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet publish "$project" -c Release -r "$rid" -p:Version="$version" -o "$work/$name"

cp "$here/dist/install.sh" "$here/dist/uninstall.sh" "$work/$name/"
chmod +x "$work/$name/install.sh" "$work/$name/uninstall.sh"
# A Windows checkout gives shell scripts carriage returns, and a script with them does not run.
sed -i.bak 's/\r$//' "$work/$name/install.sh" "$work/$name/uninstall.sh"
rm -f "$work/$name/"*.bak

mkdir -p "$work/$name/libs/desktop"
cp "$root/tools/icon/glosa.svg" "$root/tools/icon/glosa-small.svg" "$here/dist/glosa-mime.xml" \
   "$work/$name/libs/desktop/"

mkdir -p "$out"
tar -czf "$out/$name.tar.gz" -C "$work" "$name"
echo "$out/$name.tar.gz"

#!/bin/sh
# Builds Glosa.app, the macOS app, into publish/ (or the folder -o names).
#
#   tools/macos/build-app.sh [-r osx-arm64|osx-x64] [-o <folder>] [--version <version>]
#
# The runtime identifier defaults to this Mac's, and the version to the project's. The
# publish goes into Contents/MacOS as it is, libs/ and all, so define.yaml and docs/ sit
# beside the executable.
#
# As the app is not notarized, a downloaded copy is stopped by Gatekeeper the first time,
# until it is let through in System Settings, under Privacy & Security.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)

case $(uname -m) in
    arm64) rid=osx-arm64 ;;
    *) rid=osx-x64 ;;
esac
out=$root/publish
version=

while [ $# -gt 0 ]; do
    case $1 in
        -r) rid=$2; shift 2 ;;
        -o) out=$2; shift 2 ;;
        --version) version=$2; shift 2 ;;
        *) echo "usage: $0 [-r osx-arm64|osx-x64] [-o <folder>] [--version <version>]" >&2; exit 1 ;;
    esac
done

project=$root/src/Glosa.App/Glosa.App.csproj
[ -n "$version" ] || version=$(dotnet msbuild "$project" -getProperty:Version)
app=$out/Glosa.app
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

dotnet publish "$project" -c Release -r "$rid" -p:Version="$version" -o "$work/publish"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$work/publish/." "$app/Contents/MacOS/"
# Apple's bundle versions are numbers and dots only, so a prerelease (1.2.0-beta.1) goes in
# as the release it leads to.
bundle_version=${version%%-*}
sed "s/@VERSION@/$bundle_version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
cp "$here/glosa.icns" "$app/Contents/Resources/"

codesign --force --deep --sign - "$app"
echo "$app ($rid, version $version)"

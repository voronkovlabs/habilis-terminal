#!/bin/bash
# Habilis Terminal for macOS (issue #276): Habilis Terminal.app through Velopack (an installer .pkg, a portable zip and
# the update packages) and a disk image with the app and a link to /Applications. Runs on a macOS runner.
#   tools/mac/package.sh <version> <rid: osx-arm64 | osx-x64> <output folder>
# Unsigned: macOS asks the user to allow the app once (System Settings > Privacy & Security > Open Anyway) until an
# Apple Developer ID signs and notarizes it (the owner's decision).
set -euo pipefail
version=$1
rid=$2
out=$3
publish="publish/$rid"
mkdir -p "$out"

dotnet publish src/HabilisTerminal.App -c Release -r "$rid" --self-contained true -p:Version="$version" -o "$publish"

# The icon: habi.ico's largest frame, scaled to every size an .icns holds.
iconset="$out/habi.iconset"
rm -rf "$iconset"
mkdir -p "$iconset"
sips -s format png src/HabilisTerminal.App/Assets/habi.ico --out "$out/habi-base.png" > /dev/null
for size in 16 32 128 256 512; do
  sips -z $size $size "$out/habi-base.png" --out "$iconset/icon_${size}x${size}.png" > /dev/null
  double=$((size * 2))
  sips -z $double $double "$out/habi-base.png" --out "$iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "$iconset" -o "$out/habi.icns"

dotnet vpk pack --packId HabilisTerminal --packVersion "$version" --packDir "$publish" --mainExe habi \
  --packTitle "Habilis Terminal" --packAuthors "Sergei Voronkov" --icon "$out/habi.icns" \
  --bundleId com.voronkovlabs.habilisterminal --runtime "$rid" --channel "$rid" --outputDir "$out/releases"

# The disk image: the app from the portable zip, next to a link to /Applications to drag it onto.
stage="$out/dmg-$rid"
rm -rf "$stage"
mkdir -p "$stage"
ditto -x -k "$out/releases/HabilisTerminal-$rid-Portable.zip" "$stage"
ln -s /Applications "$stage/Applications"
hdiutil create -volname "Habilis Terminal" -srcfolder "$stage" -ov -format UDZO "$out/HabilisTerminal-$version-$rid.dmg" > /dev/null
ls -la "$out/releases" "$out"/*.dmg

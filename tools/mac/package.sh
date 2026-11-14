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

# The plugins that run off Windows (#276): the AWS ones, GitHub (its token in the Keychain, #280), archives (on the
# system's libarchive, #281; its Windows 7z.dll stays out) and the samples.
for sdk in HabilisTerminal.Plugins.Sdk HabilisTerminal.Plugins.Sdk.Avalonia; do
  dotnet pack "src/$sdk" -c Release -o artifacts/nuget -nologo -v q
  rm -rf "artifacts/packages/$(echo "$sdk" | tr '[:upper:]' '[:lower:]')"
done
for project in Habilis.Archives Habilis.Aws.S3 Habilis.Aws.Sqs Habilis.Aws.SageMaker Habilis.GitHub Habilis.Samples.Demo Habilis.Samples.FileFormat; do
  id=$(sed -n 's/.*"id": *"\([^"]*\)".*/\1/p' "plugins/$project/plugin.json" | head -1)
  dotnet publish "plugins/$project" -c Release -o "$publish/plugins/$id" -nologo -v q
  [ -f "plugins/$project/README.md" ] && cp "plugins/$project/README.md" "$publish/plugins/$id/"
  rm -rf "$publish/plugins/$id/native/win-x64" "$publish/plugins/$id/native/win-arm64"
done

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

# The bundle first, so its Info.plist can say what the Dock and "Open With" may hand the app (#275): folders and any
# file; then Velopack packs that bundle as it is.
bundle_dir="$out/bundle"
rm -rf "$bundle_dir"
dotnet vpk bundle --packId HabilisTerminal --packVersion "$version" --packDir "$publish" --mainExe habi \
  --packTitle "Habilis Terminal" --icon "$out/habi.icns" --bundleId com.voronkovlabs.habilisterminal --outputDir "$bundle_dir"
app=$(ls -d "$bundle_dir"/*.app | head -1)
plist="$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes array" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0 dict" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:CFBundleTypeName string Folder" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:CFBundleTypeRole string Viewer" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:LSHandlerRank string Alternate" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:LSItemContentTypes array" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:LSItemContentTypes:0 string public.folder" "$plist"
/usr/libexec/PlistBuddy -c "Add :CFBundleDocumentTypes:0:LSItemContentTypes:1 string public.item" "$plist"
/usr/libexec/PlistBuddy -c "Add :NSHumanReadableCopyright string 'Habilis Terminal, MIT License'" "$plist" || true
plutil -lint "$plist"

dotnet vpk pack --packId HabilisTerminal --packVersion "$version" --packDir "$app" --mainExe habi \
  --packTitle "Habilis Terminal" --packAuthors "Sergei Voronkov" --icon "$out/habi.icns" \
  --bundleId com.voronkovlabs.habilisterminal --runtime "$rid" --channel "$rid" --outputDir "$out/releases"

# The disk image: the app from the portable zip, next to a link to /Applications to drag it onto.
stage="$out/dmg-$rid"
rm -rf "$stage"
mkdir -p "$stage"
ditto -x -k "$out/releases/HabilisTerminal-$rid-Portable.zip" "$stage"
ln -s /Applications "$stage/Applications"
hdiutil create -volname "Habilis Terminal" -srcfolder "$stage" -ov -format UDZO "$out/HabilisTerminal-$version-$rid.dmg" > /dev/null
/usr/libexec/PlistBuddy -c "Print :CFBundleDocumentTypes" "$stage"/*.app/Contents/Info.plist || echo "no document types in the packed bundle"
ls "$stage"/*.app/Contents/MacOS/plugins 2>/dev/null || echo "no plugins in the packed bundle"
ls -la "$out/releases" "$out"/*.dmg

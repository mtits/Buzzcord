#!/bin/zsh
# Builds Buzzcord.app for macOS (Apple Silicon and Intel).
#   ./build.sh            build into mac/build/Buzzcord.app
#   ./build.sh --install  also copy it to ~/Applications
#   ./build.sh dist       release build for the website and GitHub: dist/Buzzcord-<version>-mac.zip
#                         and its .sha256
# Signs with your Apple Development certificate when there is one (so the keychain keeps
# trusting it across rebuilds), otherwise ad hoc. dist signs with a Developer ID Application
# certificate if there is one, otherwise ad hoc (first launch then needs System Settings >
# Privacy & Security > Open Anyway): an Apple Development signature doesn't open on other Macs.
set -e
cd "${0:A:h}"
VERSION=1.2.0

swift build -c release --arch arm64 --arch x86_64 2>&1 | grep -v "^\[" || true
BIN=".build/apple/Products/Release/Buzzcord"
[[ -f "$BIN" ]] || { echo "Build failed"; exit 1; }

APP="build/Buzzcord.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Buzzcord"

# The icon comes from the Windows build's .ico.
ICO="../assets/Buzzcord.ico"
if [[ -f "$ICO" ]]; then
  TMP=$(mktemp -d)
  sips -s format png "$ICO" --out "$TMP/icon.png" >/dev/null
  mkdir "$TMP/Buzzcord.iconset"
  for s in 16 32 128 256 512; do
    sips -z $s $s "$TMP/icon.png" --out "$TMP/Buzzcord.iconset/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$TMP/icon.png" --out "$TMP/Buzzcord.iconset/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$TMP/Buzzcord.iconset" -o "$APP/Contents/Resources/Buzzcord.icns"
  rm -rf "$TMP"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Buzzcord</string>
  <key>CFBundleDisplayName</key><string>Buzzcord</string>
  <key>CFBundleIdentifier</key><string>app.buzzcord.mac</string>
  <key>CFBundleExecutable</key><string>Buzzcord</string>
  <key>CFBundleIconFile</key><string>Buzzcord</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$(date +%Y%m%d%H%M)</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.social-networking</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Buzzcord uses the microphone for voice calls.</string>
  <key>NSSupportsAutomaticGraphicsSwitching</key><true/>
  <key>NSHumanReadableCopyright</key><string>© 2026 Marc Turtal. MIT License.</string>
</dict></plist>
PLIST

cp ../LICENSE "$APP/Contents/Resources/LICENSE.txt"
xattr -cr "$APP"

if [[ "$1" == "dist" ]]; then
  IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null | grep "Developer ID Application" | head -1 | sed -E 's/.*"(.*)"/\1/')
else
  IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null | grep "Apple Development" | head -1 | sed -E 's/.*"(.*)"/\1/')
fi
if [[ -n "$IDENTITY" ]]; then
  codesign --force --options runtime --entitlements Buzzcord.entitlements --sign "$IDENTITY" "$APP"
  echo "Signed with $IDENTITY"
else
  codesign --force --options runtime --entitlements Buzzcord.entitlements --sign - "$APP"
  echo "Signed ad hoc"
fi
echo "Built $APP ($(du -sh "$APP" | cut -f1))"

if [[ "$1" == "dist" ]]; then
  mkdir -p dist
  ZIP="dist/Buzzcord-$VERSION-mac.zip"
  rm -f "$ZIP" "$ZIP.sha256"
  ditto -c -k --keepParent "$APP" "$ZIP"
  (cd dist && shasum -a 256 "${ZIP:t}" > "${ZIP:t}.sha256")
  echo "Wrote $ZIP ($(du -h "$ZIP" | cut -f1))"
  cat "$ZIP.sha256"
fi

if [[ "$1" == "--install" ]]; then
  mkdir -p ~/Applications
  rm -rf ~/Applications/Buzzcord.app
  cp -R "$APP" ~/Applications/
  echo "Installed ~/Applications/Buzzcord.app"
fi

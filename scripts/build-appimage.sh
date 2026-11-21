#!/usr/bin/env bash
# Builds Purple Plunder as a Linux AppImage.
#
# Usage (from the repository root):  scripts/build-appimage.sh [--appdir-only] [version]
# Requires the .NET 10 SDK, curl and tar. Produces PurplePlunder-<version>-x86_64.AppImage.
# --appdir-only stops after assembling build/appimage/PurplePlunder.AppDir (no packing).
set -euo pipefail

APPDIR_ONLY=false
if [ "${1:-}" = "--appdir-only" ]; then APPDIR_ONLY=true; shift; fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-$(grep -oP 'AssemblyFileVersion\("\K[^"]+' "$ROOT/PurplePlunder/GlobalAssemblyInfo.cs")}"
BUILD="$ROOT/build/appimage"
APPDIR="$BUILD/PurplePlunder.AppDir"
LIB="$APPDIR/usr/lib/purple-plunder"
OUTPUT="$ROOT/PurplePlunder-$VERSION-x86_64.AppImage"

rm -rf "$BUILD"
mkdir -p "$LIB" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/metainfo" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps" "$APPDIR/usr/share/doc/purple-plunder/licenses"

# 1. Application (self-contained, no .NET installation needed)
dotnet publish "$ROOT/PurplePlunder/PurplePlunder/PurplePlunder.csproj" \
  -c Release -r linux-x64 --self-contained true -p:DebugType=none -o "$LIB"
rm -f "$LIB"/*.pdb "$LIB"/ffmpeg.exe

# 2. FFmpeg next to the application, where Purple Plunder looks for it first
[ -x "$ROOT/PurplePlunder/Libraries/linux/ffmpeg" ] || "$ROOT/scripts/get-ffmpeg-linux.sh"
install -m 755 "$ROOT/PurplePlunder/Libraries/linux/ffmpeg" "$LIB/ffmpeg"

# 3. Desktop integration
install -m 755 "$ROOT/packaging/linux/AppRun" "$APPDIR/AppRun"
install -m 644 "$ROOT/packaging/linux/purple-plunder.desktop" "$APPDIR/purple-plunder.desktop"
install -m 644 "$ROOT/packaging/linux/purple-plunder.desktop" "$APPDIR/usr/share/applications/purple-plunder.desktop"
install -m 644 "$ROOT/packaging/linux/io.github.murderbymodem.PurplePlunder.appdata.xml" "$APPDIR/usr/share/metainfo/"
install -m 644 "$ROOT/PurplePlunder/PurplePlunder.Gui/Assets/purple-plunder.png" "$APPDIR/purple-plunder.png"
install -m 644 "$ROOT/PurplePlunder/PurplePlunder.Gui/Assets/purple-plunder.png" "$APPDIR/usr/share/icons/hicolor/256x256/apps/purple-plunder.png"
ln -sf purple-plunder.png "$APPDIR/.DirIcon"

# 4. Licenses
DOC="$APPDIR/usr/share/doc/purple-plunder"
install -m 644 "$ROOT/LICENSE" "$DOC/LICENSE.txt"
install -m 644 "$ROOT/THIRD-PARTY-NOTICES.md" "$DOC/THIRD-PARTY-NOTICES.md"
install -m 644 "$ROOT"/licenses/* "$DOC/licenses/"
install -m 644 "$ROOT/PurplePlunder/Libraries/linux/ffmpeg-LICENSE.txt" "$DOC/licenses/ffmpeg-LICENSE.txt"
install -m 644 "$ROOT/PurplePlunder/Libraries/linux/ffmpeg-README.txt" "$DOC/licenses/ffmpeg-README.txt"

if [ "$APPDIR_ONLY" = true ]; then
  echo "AppDir ready in $APPDIR"
  exit 0
fi

# 5. Pack. The current appimagetool uses the static AppImage runtime, so the result runs without
#    libfuse2, which recent distributions (Kubuntu 24.04, Fedora KDE, ...) no longer install by default.
TOOL="$BUILD/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
chmod +x "$TOOL"
echo "appimagetool sha256: $(sha256sum "$TOOL" | cut -d' ' -f1)"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APPDIR" "$OUTPUT"

echo "Built $OUTPUT"

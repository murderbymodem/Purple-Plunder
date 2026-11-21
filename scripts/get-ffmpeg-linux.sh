#!/usr/bin/env bash
# Downloads the static Linux FFmpeg build that the AppImage ships with and verifies its checksum.
# Puts ffmpeg and its license/build information into PurplePlunder/Libraries/linux.
#
# Usage (from the repository root):  scripts/get-ffmpeg-linux.sh
set -euo pipefail

VERSION="7.0.2"
SHA256="abda8d77ce8309141f83ab8edf0596834087c52467f6badf376a6a2a4c87cf67"
# johnvansickle.com static build (GPLv3). The current release lives in releases/, superseded
# versions move to old-releases/ under their version number.
URLS=(
  "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz"
  "https://johnvansickle.com/ffmpeg/old-releases/ffmpeg-${VERSION}-amd64-static.tar.xz"
)

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/PurplePlunder/Libraries/linux"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

for url in "${URLS[@]}"; do
  echo "Downloading FFmpeg $VERSION from $url"
  if curl -fsSL --connect-timeout 20 --retry 3 --retry-delay 5 --retry-all-errors \
       -o "$TMP/ffmpeg.tar.xz" "$url" && echo "$SHA256  $TMP/ffmpeg.tar.xz" | sha256sum -c --quiet; then
    mkdir -p "$OUT"
    tar -xJf "$TMP/ffmpeg.tar.xz" -C "$TMP"
    install -m 755 "$TMP/ffmpeg-$VERSION-amd64-static/ffmpeg" "$OUT/ffmpeg"
    install -m 644 "$TMP/ffmpeg-$VERSION-amd64-static/GPLv3.txt" "$OUT/ffmpeg-LICENSE.txt"
    install -m 644 "$TMP/ffmpeg-$VERSION-amd64-static/readme.txt" "$OUT/ffmpeg-README.txt"
    echo "FFmpeg $VERSION is ready in $OUT"
    exit 0
  fi
  echo "Not available or checksum mismatch, trying the next location"
done

echo "Could not download FFmpeg $VERSION with the expected checksum" >&2
exit 1

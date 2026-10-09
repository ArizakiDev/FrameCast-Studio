#!/usr/bin/env bash
# Construit FrameCast Studio pour Linux : binaire autonome + ffmpeg embarqué -> .tar.gz et AppImage.
# Usage : ./build-linux.sh [x64|arm64]      (à lancer sous Linux, ou via GitHub Actions)
set -euo pipefail

ARCH="${1:-x64}"
case "$ARCH" in
  x64)   RID=linux-x64;   FFARCH=linux64;    APPIMAGE_ARCH=x86_64 ;;
  arm64) RID=linux-arm64; FFARCH=linuxarm64; APPIMAGE_ARCH=aarch64 ;;
  *) echo "Architecture inconnue : $ARCH (x64 ou arm64)"; exit 1 ;;
esac

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/FrameCastStudio.Linux.csproj" | head -1)"
OUT="$ROOT/dist"
WORK="$OUT/work-$ARCH"
NAME="FrameCastStudio-$VERSION-$RID"

rm -rf "$WORK"; mkdir -p "$WORK/app/ffmpeg" "$OUT"

echo "==> dotnet publish ($RID)"
dotnet publish "$ROOT/FrameCastStudio.Linux.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false -o "$WORK/app"

echo "==> ffmpeg statique ($FFARCH)"
FFURL="https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-$FFARCH-gpl.tar.xz"
curl -fL --retry 3 -o "$WORK/ffmpeg.tar.xz" "$FFURL"
tar -xf "$WORK/ffmpeg.tar.xz" -C "$WORK"
cp "$WORK"/ffmpeg-master-latest-"$FFARCH"-gpl/bin/ffmpeg "$WORK/app/ffmpeg/ffmpeg"
strip --strip-unneeded "$WORK/app/ffmpeg/ffmpeg" 2>/dev/null || true
chmod +x "$WORK/app/ffmpeg/ffmpeg" "$WORK/app/FrameCastStudio"
cp "$HERE/../../LICENSE" "$WORK/app/LICENSE" 2>/dev/null || true
cp "$HERE/LICENSE-FFMPEG.txt" "$WORK/app/ffmpeg/LICENSE-FFMPEG.txt" 2>/dev/null || true

echo "==> .tar.gz"
rm -rf "$WORK/tar" && mkdir -p "$WORK/tar/FrameCastStudio"
cp -a "$WORK/app/." "$WORK/tar/FrameCastStudio/"
cp "$HERE/run.sh" "$WORK/tar/FrameCastStudio/run.sh"; chmod +x "$WORK/tar/FrameCastStudio/run.sh"
cp "$HERE/framecaststudio.desktop" "$HERE/framecaststudio.png" "$WORK/tar/FrameCastStudio/"
tar -C "$WORK/tar" -czf "$OUT/$NAME.tar.gz" FrameCastStudio

echo "==> AppImage"
APPDIR="$WORK/AppDir"
rm -rf "$APPDIR" && mkdir -p "$APPDIR/usr/lib/framecaststudio"
cp -a "$WORK/app/." "$APPDIR/usr/lib/framecaststudio/"
cp "$HERE/AppRun" "$APPDIR/AppRun"; chmod +x "$APPDIR/AppRun"
cp "$HERE/framecaststudio.desktop" "$APPDIR/framecaststudio.desktop"
cp "$HERE/framecaststudio.png" "$APPDIR/framecaststudio.png"
cp "$HERE/framecaststudio.png" "$APPDIR/.DirIcon"

TOOL="$WORK/appimagetool"
curl -fL --retry 3 -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$APPIMAGE_ARCH.AppImage"
chmod +x "$TOOL"
# --appimage-extract-and-run : fonctionne même sans FUSE (conteneurs, CI)
ARCH="$APPIMAGE_ARCH" "$TOOL" --appimage-extract-and-run --no-appstream "$APPDIR" "$OUT/$NAME.AppImage"
chmod +x "$OUT/$NAME.AppImage"

( cd "$OUT" && sha256sum "$NAME.tar.gz" "$NAME.AppImage" > "$NAME.sha256" )
echo "==> Terminé :"; ls -lh "$OUT"/"$NAME".*

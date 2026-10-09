#!/bin/sh
# Lanceur de l'archive .tar.gz : ./run.sh  (ou ./run.sh --toggle-record, etc.)
HERE="$(dirname "$(readlink -f "$0")")"
chmod +x "$HERE/FrameCastStudio" "$HERE/ffmpeg/ffmpeg" 2>/dev/null
exec "$HERE/FrameCastStudio" "$@"

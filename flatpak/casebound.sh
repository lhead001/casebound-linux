#!/bin/sh
# Flatpak launcher: ffmpeg/ffprobe and libVLC live in /app.
export CASEBOUND_FFMPEG=/app/bin/ffmpeg
export CASEBOUND_FFPROBE=/app/bin/ffprobe
export VLC_PLUGIN_PATH=/app/lib/vlc/plugins
exec /app/lib/casebound/casebound "$@"

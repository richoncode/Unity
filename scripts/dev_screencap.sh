#!/bin/bash
# Capture a screenshot of the lab Quest 3's stereo display.
# Output is a PNG (left+right eye halves).
#
# Usage: dev_screencap.sh [OUT_PATH]
#   OUT_PATH defaults to /tmp/quest_shot.png

set -u
OUT="${1:-/tmp/quest_shot.png}"
ADB="${ADB:-/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb -s 2G0YC5ZGB405BG}"

step() { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }

step "screencap -> $OUT"
# Quest's adb screencap is flaky in marginal device states (VR session
# ending / starting, prox sensor flipping). Retry up to 4 times, with
# a wake nudge + small delay between attempts.
SCRIPTS_DIR=$(dirname "$0")
for ATTEMPT in 1 2 3 4; do
    $ADB shell screencap -p > "$OUT" 2>/dev/null
    SIZE=$(stat -f%z "$OUT" 2>/dev/null || stat -c%s "$OUT" 2>/dev/null || echo 0)
    if [ "$SIZE" -ge 10000 ]; then
        echo "wrote $OUT ($SIZE bytes) after $ATTEMPT attempt(s)"
        file "$OUT"
        step "done"
        exit 0
    fi
    echo "screencap attempt $ATTEMPT returned $SIZE bytes — waking + retrying"
    "$SCRIPTS_DIR/dev_wake.sh" >/dev/null 2>&1 || true
    sleep 1
done
echo "WARN: screencap stayed tiny after 4 attempts — device unable to render. Last size: $SIZE bytes."
exit 1

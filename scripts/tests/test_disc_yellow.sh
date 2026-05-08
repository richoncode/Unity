#!/bin/bash
# Test skill: yellow disc renders.
# Build with TestPurpleCircleSetup adding a 1m yellow disc at local (0,0,1)
# under OVRCameraRig.TrackingSpace, install, launch, screencap, pixel-verify
# both eyes match yellow.
#
# Self-contained: calls begin-automation first, leaves end-automation to the
# caller (so multiple tests can chain without thrashing the device state).
#
# Exit 0 = PASS, non-zero = FAIL. Saves screencap to
# /tmp/quest_tagged_disc_yellow_*.png on PASS via `dev.sh tag`.

set -u
DEV=/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh
NAME="disc-yellow"

echo
echo "##### TEST: $NAME #####"
echo

"$DEV" begin-automation        || { echo "TEST $NAME FAIL: begin-automation"; exit 1; }
TEST_PURPLE_CIRCLE=1 STEREO_SKIP_PANEL=1 \
    "$DEV" build               || { echo "TEST $NAME FAIL: build"; exit 1; }
"$DEV" install-app             || { echo "TEST $NAME FAIL: install-app"; exit 1; }
"$DEV" run-app                 || { echo "TEST $NAME FAIL: run-app"; exit 1; }
"$DEV" screencap               || { echo "TEST $NAME FAIL: screencap"; exit 1; }
"$DEV" verify-color yellow     || { echo "TEST $NAME FAIL: verify-color"; exit 1; }
"$DEV" tag "$NAME"             || true

echo
echo "##### TEST $NAME PASS #####"
echo

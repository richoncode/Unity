#!/bin/bash
# Test skill: visual marker renders.
# Build with TestPurpleCircleSetup placing a primitive marker (shape and
# color set in the Unity-side authoring code; currently a 1 m green
# cylinder-disc at local (0,0,1) under OVRCameraRig.TrackingSpace),
# install, launch, screencap, pixel-verify both eyes match the expected
# color.
#
# Usage:  dev.sh test marker [COLOR]
#   COLOR  expected color name / R,G,B / #RRGGBB. Default: green
#          (matches the current TestPurpleCircleSetup.cs build output).
#          When the marker color is changed in the C# code, pass the new
#          color here OR update the default below.
#
# Self-contained: calls begin-automation first, leaves end-automation to the
# caller (so multiple tests can chain without thrashing the device state).
#
# Exit 0 = PASS, non-zero = FAIL. Saves screencap to
# /tmp/quest_tagged_marker_*.png on PASS via `dev.sh tag`.

set -u
DEV=/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh
NAME="marker"
COLOR="${1:-green}"

echo
echo "##### TEST: $NAME (expecting $COLOR) #####"
echo

"$DEV" begin-automation        || { echo "TEST $NAME FAIL: begin-automation"; exit 1; }
TEST_PURPLE_CIRCLE=1 STEREO_SKIP_PANEL=1 \
    "$DEV" build               || { echo "TEST $NAME FAIL: build"; exit 1; }
"$DEV" install-app             || { echo "TEST $NAME FAIL: install-app"; exit 1; }
"$DEV" run-app                 || { echo "TEST $NAME FAIL: run-app"; exit 1; }
"$DEV" screencap               || { echo "TEST $NAME FAIL: screencap"; exit 1; }
"$DEV" verify-color "$COLOR"   || { echo "TEST $NAME FAIL: verify-color $COLOR"; exit 1; }
"$DEV" tag "$NAME"             || true

echo
echo "##### TEST $NAME PASS #####"
echo

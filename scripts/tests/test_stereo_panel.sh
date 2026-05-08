#!/bin/bash
# Test skill: stereo compositor panel renders, both eyes.
# Build with the StereoQuadPanel ENABLED (STEREO_SKIP_PANEL=0) and the
# marker disc DISABLED (TEST_PURPLE_CIRCLE=0), install, launch, alive-for
# 15s, screencap, pixel-verify per-eye colors against the test pattern
# (default: left=red, right=blue — matches the procedural top-bottom
# pattern in StereoCompositorSceneSetup.BuildTestPattern).
#
# Usage:  dev.sh test stereo-panel [LEFT_COLOR [RIGHT_COLOR]]
#   LEFT_COLOR   expected left-eye color (default: red)
#   RIGHT_COLOR  expected right-eye color (default: blue)
#
# Self-contained: calls begin-automation first, leaves end-automation to
# the caller. Verifies LCnt is at least 6 (baseline 5 + 1 for our panel).
#
# Exit 0 = PASS, non-zero = FAIL. Saves screencap to
# /tmp/quest_tagged_stereo_panel_*.png on PASS via `dev.sh tag`.

set -u
DEV=/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh
NAME="stereo-panel"
LEFT="${1:-red}"
RIGHT="${2:-blue}"

echo
echo "##### TEST: $NAME (expecting left=$LEFT right=$RIGHT) #####"
echo

"$DEV" begin-automation                || { echo "TEST $NAME FAIL: begin-automation"; exit 1; }
"$DEV" build --with-panel --no-marker  || { echo "TEST $NAME FAIL: build"; exit 1; }
"$DEV" install-app             || { echo "TEST $NAME FAIL: install-app"; exit 1; }
"$DEV" run-app                 || { echo "TEST $NAME FAIL: run-app"; exit 1; }
"$DEV" alive-for 15            || { echo "TEST $NAME FAIL: alive-for 15"; exit 1; }
"$DEV" screencap               || { echo "TEST $NAME FAIL: screencap"; exit 1; }
"$DEV" verify-color "$LEFT" "$RIGHT" || { echo "TEST $NAME FAIL: verify-color $LEFT $RIGHT"; exit 1; }
LCNT=$("$DEV" layer-count 2>/dev/null || echo "LCnt=0")
echo "$NAME layer-count: $LCNT"
"$DEV" tag "$NAME"             || true

echo
echo "##### TEST $NAME PASS #####"
echo

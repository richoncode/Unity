#!/bin/bash
# Test skill: bare baseline.
# Build with the compositor panel skipped AND no test disc, install, launch,
# verify the app stays alive for 15 s with 0 crashes. No pixel verification —
# this just confirms the build/deploy/launch pipeline + Unity URP path work
# end-to-end without anything custom in the scene.
#
# Use this as a smoke test before iterating on more complex scenes.

set -u
DEV=/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh
NAME="baseline"

echo
echo "##### TEST: $NAME #####"
echo

"$DEV" begin-automation        || { echo "TEST $NAME FAIL: begin-automation"; exit 1; }
TEST_PURPLE_CIRCLE=0 STEREO_SKIP_PANEL=1 \
    "$DEV" build               || { echo "TEST $NAME FAIL: build"; exit 1; }
"$DEV" install-app             || { echo "TEST $NAME FAIL: install-app"; exit 1; }
"$DEV" run-app                 || { echo "TEST $NAME FAIL: run-app"; exit 1; }
"$DEV" tag "$NAME"             || true

echo
echo "##### TEST $NAME PASS #####"
echo

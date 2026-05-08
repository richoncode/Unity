#!/bin/bash
# Test skill: full reboot recovery.
# Reboots the Quest, runs the post-boot recovery sequence, installs (fresh
# APK from last build — assumes a build is on disk), launches, screencaps.
# Verifies the device returns to a usable state from cold-reboot without
# any human input.
#
# Slow (~2 min). Run this when you suspect device-state pollution that
# needs a real reboot to clear.

set -u
DEV=/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh
NAME="recovery"

echo
echo "##### TEST: $NAME #####"
echo

"$DEV" reboot-app              || { echo "TEST $NAME FAIL: reboot-app"; exit 1; }
"$DEV" begin-automation        || { echo "TEST $NAME FAIL: begin-automation"; exit 1; }
"$DEV" install-app             || { echo "TEST $NAME FAIL: install-app"; exit 1; }
"$DEV" run-app                 || { echo "TEST $NAME FAIL: run-app"; exit 1; }
"$DEV" screencap               || { echo "TEST $NAME FAIL: screencap"; exit 1; }
"$DEV" tag "$NAME"             || true

echo
echo "##### TEST $NAME PASS #####"
echo

#!/bin/bash
# Install an APK on the lab Quest 3, verify md5 matches the local file
# (catches stale-install / adb-daemon-conflict cases), and exit non-zero if
# anything is off.
#
# Usage: dev_install_verify.sh [APK_PATH]
#   APK_PATH defaults to VibeUnity1/Builds/MR_Passthrough.apk

set -u
APK="${1:-/Users/richardbailey/RichardClaude/Unity/VibeUnity1/Builds/MR_Passthrough.apk}"
PKG="${PKG:-com.UnityTechnologies.com.unity.template.urpblank}"
ADB="${ADB:-/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb -s 2G0YC5ZGB405BG}"

step() { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }

if [ ! -f "$APK" ]; then
    echo "FAIL: APK not found at $APK"
    exit 1
fi

step "install: $APK"
$ADB install -r "$APK" 2>&1 | tail -3

step "md5 verify"
LOCAL=$(md5sum "$APK" | awk '{print $1}')
DEV_PATH=$($ADB shell pm path "$PKG" | sed 's/^package://' | tr -d '\r')
if [ -z "$DEV_PATH" ]; then
    echo "FAIL: could not resolve on-device path for $PKG"
    exit 1
fi
DEV=$($ADB shell md5sum "$DEV_PATH" | awk '{print $1}')
echo "local=$LOCAL"
echo "device=$DEV"
echo "device-path=$DEV_PATH"
if [ "$LOCAL" = "$DEV" ]; then
    echo "MD5 OK"
else
    echo "FAIL: MD5 STALE — install did not land. Likely adb-daemon conflict."
    exit 1
fi

step "done"

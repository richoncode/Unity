#!/bin/bash
# Wake the lab Quest 3 from idle sleep without a full reboot.
# Use when device went to sleep but adb is still connected (no boot animation).
# Same broadcasts as dev_reboot_recover.sh but skips the reboot/wait-for-device.

set -u
ADB="${ADB:-/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb -s 2G0YC5ZGB405BG}"

step() { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }

step "wake"
# Try the soft path first (idempotent on already-awake devices). We do NOT
# force-stop com.oculus.vrshell anymore: with `setprop debug.oculus.
# skipProxBlanking 1` (applied by dev.sh keep-awake-on / begin-automation)
# the controller-required dialog doesn't fire, so the vrshell-kill is
# unnecessary AND was tearing down the VR session.
$ADB shell input keyevent KEYCODE_WAKEUP || true
$ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null
$ADB shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null

# If the display is still off, try a power-toggle. Sample a screencap; if
# it's empty/tiny, the display panel is dark — send KEYCODE_POWER to toggle
# it on. KEYCODE_POWER toggles, so we only send if the screen is verifiably
# off.
TMP=$(mktemp /tmp/quest_wake_probe.XXXXXX.png)
$ADB shell screencap -p > "$TMP" 2>/dev/null
SIZE=$(stat -f%z "$TMP" 2>/dev/null || stat -c%s "$TMP" 2>/dev/null || echo 0)
if [ "$SIZE" -lt 10000 ]; then
    echo "screencap probe is empty/tiny ($SIZE bytes) — sending KEYCODE_POWER to wake display"
    $ADB shell input keyevent KEYCODE_POWER
    sleep 1
    $ADB shell input keyevent KEYCODE_WAKEUP || true
    sleep 1
fi
rm -f "$TMP"

step "ready"

#!/bin/bash
# Full reboot + post-boot recovery for the lab Quest 3.
# - reboot the device
# - wait for adb to come back
# - wait for boot animation
# - send KEYCODE_WAKEUP (idempotent, brings device out of sleep)
# - prox_close so the device behaves as if worn (rendering + screencap work)
# - automation_disable so it never goes back to idle-sleep on its own
# - force-stop com.oculus.vrshell to dismiss the controller-required dialog
# Result: device is awake, in "worn" state, ready to receive an app launch
# without manual intervention.

set -u
ADB="${ADB:-/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb -s 2G0YC5ZGB405BG}"

# Step banner — high-contrast ASCII so it pops in any plain-text consumer
# (terminal, Claude Code Desktop tool-output panel, log files). Don't use
# ANSI color: CCD strips it.
step() { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }

step "reboot"
$ADB reboot

step "wait-for-device"
$ADB wait-for-device

step "boot animation 45s"
sleep 45

step "wake + prox_close + automation_disable"
$ADB shell input keyevent KEYCODE_WAKEUP || true
$ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close
$ADB shell am broadcast -a com.oculus.vrpowermanager.automation_disable

step "kill com.oculus.vrshell (controller-required dialog)"
$ADB shell am force-stop com.oculus.vrshell

step "ready"

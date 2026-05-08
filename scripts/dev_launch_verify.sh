#!/bin/bash
# Launch the app and verify it stays alive for at least ~15 s.
# Reports: PID stability, crash count, VrApi LCnt, Stereo* log lines.
# Exit non-zero if PID is empty/unstable or crash detected.
#
# Usage: dev_launch_verify.sh [PKG]
#   PKG defaults to com.UnityTechnologies.com.unity.template.urpblank

set -u
PKG="${1:-${PKG:-com.UnityTechnologies.com.unity.template.urpblank}}"
ACT="${ACT:-com.unity3d.player.UnityPlayerGameActivity}"
ADB="${ADB:-/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb -s 2G0YC5ZGB405BG}"
ADB_BIN="${ADB%% *}"  # bare adb path (no -s SERIAL) for daemon-management calls

step() { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }

# robust_pidof: pidof with one auto-retry through `adb start-server` if the
# adb daemon dies mid-flight. The daemon dies semi-randomly on this Mac
# (Homebrew/Unity/Android Studio adb binaries race for port 5037). We've
# observed this fail in practice between PID(t=0) and PID(t+3s) checks,
# producing a false "app not running" result.
#
# Distinguishes "adb itself errored" (non-zero exit + stderr message) from
# "process not running" (zero exit + empty stdout) — only retries the former.
robust_pidof() {
    local out errfile rc
    for attempt in 1 2; do
        errfile=$(mktemp /tmp/robust_pidof.err.XXXXXX)
        out=$($ADB shell pidof "$PKG" 2>"$errfile")
        rc=$?
        if [ $rc -eq 0 ]; then
            rm -f "$errfile"
            printf '%s' "$out" | tr -d '\r' | tr -d ' '
            return 0
        fi
        echo "[harden] pidof attempt $attempt failed ($(head -1 "$errfile" 2>/dev/null)); restarting adb daemon" >&2
        rm -f "$errfile"
        "$ADB_BIN" start-server >/dev/null 2>&1 || true
        sleep 1
    done
    echo "[harden] pidof still failing after retry — adb daemon hosed" >&2
    return 1
}

step "wake + prox_close (idempotent)"
$ADB shell input keyevent KEYCODE_WAKEUP || true
$ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null

# Force-stop our app + clear logcat. We deliberately do NOT force-stop
# com.oculus.vrshell anymore: with `setprop debug.oculus.skipProxBlanking 1`
# (applied by dev.sh keep-awake-on / begin-automation), the controller-
# required dialog doesn't fire, so the old vrshell-kill dance is unneeded
# AND was actively harmful — it was tearing down the VR session and
# putting the device into the perf-overlay-only state by the time
# screencap fired. begin-automation now owns the device-state setup;
# launch just brings our app to the front.
step "force-stop pkg + clear logcat"
$ADB shell am force-stop "$PKG"
$ADB logcat -c

step "am start"
$ADB shell am start -W -n "$PKG/$ACT" 2>&1 | tail -8

# Give the app a few seconds to come up and start rendering.
sleep 8

step "verify alive"
# Pre-emptively poke the adb daemon (idempotent — start-server is a no-op
# if it's already running) before starting the verify sequence.
"$ADB_BIN" start-server >/dev/null 2>&1 || true
PID1=$(robust_pidof)
echo "PID=$PID1"
sleep 3
PID2=$(robust_pidof)
echo "PID(t+3s)=$PID2"

if [ -z "$PID1" ] || [ -z "$PID2" ]; then
    echo "FAIL: app not running"
    echo "--- top activity:"
    $ADB shell dumpsys activity activities | grep "topResumedActivity" | head -2
    exit 1
fi

if [ "$PID1" != "$PID2" ]; then
    echo "FAIL: PID changed — app crashed and restarted"
    exit 1
fi

CRASHES=$($ADB logcat -d | grep -c "Crash detected.*urpblank" || true)
echo "crashes: $CRASHES"
if [ "$CRASHES" != "0" ]; then
    echo "FAIL: $CRASHES crash(es) detected"
    exit 1
fi

echo "LCnt: $($ADB logcat -d | grep VrApi | tail -1 | grep -oE 'LCnt=[0-9]+' | head -1)"
echo "Stereo logs:"
$ADB logcat -d | grep -E "StereoCompositor|XR_FB_composition" | head -10 || true
echo "URP InvalidCast count: $($ADB logcat -d | grep -c "InvalidCastException" || true)"

step "PASS"

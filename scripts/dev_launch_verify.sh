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

# robust_pidof: pidof with one auto-retry through `adb start-server` only
# when the adb daemon itself died mid-flight. The daemon dies semi-randomly
# on this Mac (Homebrew/Unity/Android Studio adb binaries race for port
# 5037). We've observed this fail in practice between PID(t=0) and
# PID(t+3s) checks, producing a false "app not running" result.
#
# Distinguishes the failure modes by stderr CONTENT (not just exit code,
# because `adb shell pidof <not-running>` also exits 1 with empty stderr —
# which is a normal "process not running" signal, not an adb fault):
#   - stderr contains "protocol fault" / "daemon" / "Connection reset" /
#     "cannot connect" → adb daemon issue, retry
#   - any other failure (including empty stderr) → process not running,
#     return empty PID without retry
robust_pidof() {
    local out errfile rc err
    for attempt in 1 2; do
        errfile=$(mktemp /tmp/robust_pidof.err.XXXXXX)
        out=$($ADB shell pidof "$PKG" 2>"$errfile")
        rc=$?
        err=$(cat "$errfile")
        rm -f "$errfile"
        if [ $rc -eq 0 ]; then
            printf '%s' "$out" | tr -d '\r' | tr -d ' '
            return 0
        fi
        # Non-zero exit: only retry if stderr clearly indicates adb-side trouble.
        if echo "$err" | grep -qE "protocol fault|daemon not running|cannot connect to daemon|Connection reset"; then
            echo "[harden] pidof attempt $attempt: adb daemon hiccup ($(echo "$err" | head -1)); restarting" >&2
            "$ADB_BIN" start-server >/dev/null 2>&1 || true
            sleep 1
            continue
        fi
        # Otherwise treat as "process not running" (e.g. dialog intercepted launch).
        printf ''
        return 0
    done
    echo "[harden] pidof still failing after retry — adb daemon hosed" >&2
    return 1
}

step "wake + prox_close (idempotent)"
$ADB shell input keyevent KEYCODE_WAKEUP || true
$ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null

# Force-stop our app + clear logcat. Device-state setup (controller-
# required dialog suppression, keep-awake) is owned by `dev.sh
# begin-automation`; this script only brings our app to the front.
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

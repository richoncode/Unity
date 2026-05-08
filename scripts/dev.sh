#!/bin/bash
# Single router script for the lab Quest 3 dev loop. All automation goes
# through this; Claude Code only needs ONE permission rule:
#   Bash(/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh *)
# Sub-scripts called from here run inside bash (not via the CC tool), so
# they don't trigger permission prompts.
#
# Design rules (do not violate):
#   - The AI MUST orchestrate by calling `dev.sh <verb>` in separate Bash
#     tool calls. Never chain with ; && | $(...) etc — each chained piece
#     would otherwise need its own allow rule.
#   - All artifacts go to fixed /tmp/quest_latest_*.<ext> paths. Use
#     `dev.sh tag <name>` to snapshot the latest artifacts under a tag.
#   - All long-running verbs write progress banners with `step()`; pair
#     with a Monitor on /tmp/quest_run.log for live CCD chat updates.

set -u

# ---------- config ----------
SCRIPTS_DIR=/Users/richardbailey/RichardClaude/Unity/scripts
PROJECT=/Users/richardbailey/RichardClaude/Unity/VibeUnity1
UNITY=/Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity
ADB_BIN=/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb
SERIAL=2G0YC5ZGB405BG
PKG=com.UnityTechnologies.com.unity.template.urpblank
ACT=com.unity3d.player.UnityPlayerGameActivity
APK="$PROJECT/Builds/MR_Passthrough.apk"

# Fixed artifact paths.
LATEST_SHOT=/tmp/quest_latest_shot.png
LATEST_LOGCAT=/tmp/quest_latest_logcat.txt
LATEST_CRASH=/tmp/quest_latest_crash.txt
RUN_LOG=/tmp/quest_run.log
BUILD_LOG=/tmp/quest_build.log

ADB="$ADB_BIN -s $SERIAL"

# ---------- helpers ----------
step()  { printf '\n>>>>>>>>>>>>>>>>>>>>  %s  ::  %s  <<<<<<<<<<<<<<<<<<<<\n\n' "$(date '+%H:%M:%S')" "$1"; }
err()   { printf '\n!!!!!!!!!!!!!!!!!!!!  %s  ::  %s  !!!!!!!!!!!!!!!!!!!!\n\n' "$(date '+%H:%M:%S')" "$1" >&2; }
note()  { printf '%s\n' "$*"; }

# Quest keep-awake via PERSISTENT system settings (no daemon, no polling):
#   secure vr_sensor_state = 0    (proximity sensor disabled — never sleeps
#                                   on lens-cover-open / off-head)
#   system screen_off_timeout = 999999999 (defeat the idle timer, ~277 days)
# Survives vrshell respawns; doesn't collide with adb install. Original
# values are backed up once per session to $SETTINGS_BACKUP so we can
# restore them with `dev.sh keep-awake-off`.
#
# Optional belt-and-suspenders: in Meta Quest Developer Hub →
#   Device Manager → Device Actions → Boundary: Off
#   Device Manager → Device Actions → Proximity Sensor: Off
SETTINGS_BACKUP=/tmp/dev_quest_settings_backup.txt

# ---------- verbs ----------
verb_help() {
    cat <<EOF
dev.sh — lab Quest 3 dev router. All automation routes through this.

USAGE:  dev.sh <verb> [args]

CANONICAL STATE MACHINE (top-level model):
  States:
    ready-for-automation   adb up, persistent settings applied, VR session
                           live, no app foreground (Quest shell).
    ready-to-run-app       ready-for-automation + our APK installed
                           (md5 verified against local build).
    running-app            ready-to-run-app + our app foreground, alive.

  Transitions (primary actions):
    begin-automation       any  -> ready-for-automation
    install-app            ready-for-automation -> ready-to-run-app
    run-app                ready-to-run-app -> running-app
    kill-app               running-app -> ready-for-automation
    reboot-app             any  -> ready-for-automation (full reboot path)
    end-automation         exits the harness, restores Quest defaults

PREFLIGHT:
  init                       Verify dependencies + reachable device + the
                             persistent keep-awake state (settings + the
                             volatile setprops). Re-applies if any piece is
                             missing. Run once at the start of an
                             autonomous session.
  doctor                     Same as init but more verbose; prints versions.

DEVICE STATE:
  reboot-recover             reboot + wait + re-apply keep-awake (the
                             setprops are wiped on reboot)
  wake                       wake + prox_close + automation_disable
  keep-awake-on              Apply persistent keep-awake state:
                             vr_sensor_state=0, screen_off_timeout=long,
                             setprop skipProxBlanking=1, alwaysOn=1.
                             Settings persist; setprops are volatile.
  keep-awake-off             Restore original Quest settings + clear setprops.
  keep-awake-start           Alias for keep-awake-on (legacy)
  keep-awake-stop            Alias for keep-awake-off (legacy)
  reboot                     Just reboot (no recovery sequence; rarely needed)

BUILD / DEPLOY (canonical names match the state machine above):
  build [flags]              Unity batchmode build. Flags (default: panel
                             OFF, marker ON, stock-quad OFF):
                               --with-panel / --no-panel
                               --with-marker / --no-marker
                               --stock-quad / --no-stock-quad
  install-app                Install APK + verify md5  -> ready-to-run-app
                             (alias: install)
  run-app                    am start + verify alive   -> running-app
                             (alias: launch)
  kill-app                   am force-stop our app     -> ready-for-automation
  reboot-app                 reboot + recovery sequence -> ready-for-automation
                             (alias: reboot-recover)
  uninstall                  adb uninstall the app

CAPTURE / VERIFY:
  screencap                  Capture to \$LATEST_SHOT (=$LATEST_SHOT)
  verify-color [L [R]]       Per-eye color match against \$LATEST_SHOT.
                             L/R are color names (yellow|purple|red|green|
                             blue|cyan|magenta|orange|white|black), "R,G,B",
                             or "#RRGGBB". Defaults: L=yellow, R=L.
                             Auto-screencaps if latest is missing/stale.
  layer-count                Print latest VrApi 'LCnt=N' (canonical
                             compositor-layer-count probe).
  alive-for <seconds>        Poll pidof every 1s for N seconds; exit 0
                             only if PID is non-empty AND stable the
                             entire interval.
  logs <pattern>             Last 200 logcat lines matching pattern (regex)
  crash-stack                Latest crash buffer summary
  pidof                      Print app's current PID (or empty)
  dismiss                    Send KEYCODE_BACK x3 to dismiss any system dialog

ARTIFACTS:
  tag <name>                 Snapshot \$LATEST_* into /tmp/quest_tagged_<name>_*

OTHER:
  help                       This message
  ports                      Show adb daemon + device state
  adb-restart                Kill all adb daemons + restart Unity-bundled
                             one. Use after "Connection refused" or
                             "protocol fault" errors.
EOF
}

verb_begin_automation() {
    step "BEGIN AUTOMATION — preparing device for hands-off testing"
    verb_adb_restart  || { err "adb daemon could not be started"; return 1; }
    verb_init         || { err "preflight failed"; return 1; }
    verb_keep_awake_on
    verb_dismiss
    sleep 1
    if ! ensure_device_awake; then
        err "device not in usable state after begin-automation"
        return 1
    fi
    step "BEGIN AUTOMATION OK — device is in state 2 (VR session, passthrough)"
}

verb_end_automation() {
    step "END AUTOMATION — restoring device to normal user state"
    # Stop our test app so the device returns to Quest shell.
    $ADB shell am force-stop "$PKG" 2>/dev/null || true
    # Restore the original Quest settings (proximity sensor, screen timeout,
    # setprops). After this the device will idle-sleep normally.
    verb_keep_awake_off
    step "END AUTOMATION OK — device may sleep on its own now"
}

verb_init() {
    local FAIL=0
    step "init: dependency + state check"

    note "--- python3:"
    if python3 --version >/dev/null 2>&1; then
        note "  $(python3 --version)"
    else
        err "python3 not found"; FAIL=1
    fi

    note "--- Pillow (PIL.Image):"
    if python3 -c "from PIL import Image; print('  PIL', Image.__version__)" 2>/dev/null; then :; else
        err "Pillow not installed. Fix: pip3 install --break-system-packages --user Pillow"
        FAIL=1
    fi

    note "--- adb path + version:"
    if [ -x "$ADB_BIN" ]; then
        note "  binary: $ADB_BIN"
        note "  $($ADB_BIN --version | head -1)"
    else
        err "adb binary missing at $ADB_BIN"; FAIL=1
    fi

    note "--- device reachable:"
    local STATE
    STATE=$($ADB get-state 2>&1 || true)
    if [ "$STATE" = "device" ]; then
        note "  $SERIAL: device"
    else
        err "device $SERIAL not reachable (state: $STATE). Try 'dev.sh ports' or check USB cable."
        FAIL=1
    fi

    note "--- Unity binary:"
    if [ -x "$UNITY" ]; then
        note "  $UNITY"
    else
        err "Unity binary missing at $UNITY"; FAIL=1
    fi

    note "--- project tree:"
    for f in \
        "$PROJECT/Assets/Editor/AutoBuilder.cs" \
        "$PROJECT/Assets/link.xml" \
        "$PROJECT/Assets/Editor/StereoCompositorSceneSetup.cs" \
        "$PROJECT/Assets/Editor/TestPurpleCircleSetup.cs"; do
        if [ -f "$f" ]; then
            note "  ok: $(basename "$f")"
        else
            err "missing: $f"; FAIL=1
        fi
    done

    note "--- keep-awake (persistent settings + setprops):"
    local VR;  VR=$($ADB shell settings get secure vr_sensor_state | tr -d '\r')
    local TO;  TO=$($ADB shell settings get system screen_off_timeout | tr -d '\r')
    local SP;  SP=$($ADB shell getprop debug.oculus.skipProxBlanking | tr -d '\r')
    local AO;  AO=$($ADB shell getprop debug.oculus.alwaysOn | tr -d '\r')
    local SLC; SLC=$($ADB shell settings get secure skip_launch_check_requires_controllers_enabled | tr -d '\r')
    local CEM; CEM=$($ADB shell settings get secure controller_emulation_mode | tr -d '\r')
    note "  vr_sensor_state=$VR  screen_off_timeout=$TO  skipProxBlanking=$SP  alwaysOn=$AO"
    note "  skip_launch_check=$SLC  controller_emulation=$CEM"
    # Setprops are volatile (cleared on reboot / system_server restart) so
    # init must verify them, not just the persistent settings — otherwise we
    # report "already in keep-awake state" while the load-bearing
    # skipProxBlanking is 0 and the device blanks on the next prox event.
    # Also verify the controller-dialog suppression settings — without
    # those, run-app gets redirected to the controller-required dialog.
    if [ "$VR" != "0" ] || [ "$TO" -lt 86400000 ] 2>/dev/null \
        || [ "$SP" != "1" ] || [ "$AO" != "1" ] \
        || [ "$SLC" != "true" ] || [ "$CEM" != "1" ]; then
        note "  not in keep-awake state — applying"
        verb_keep_awake_on
    else
        note "  already in keep-awake state"
    fi

    if [ $FAIL -eq 0 ]; then
        step "init OK"
    else
        err "init FAIL — fix the items above before running other verbs"
        return 1
    fi
}

verb_doctor() {
    verb_init
    step "extra diagnostics"
    note "--- adb processes:"
    ps aux | grep -E 'adb fork-server|/adb ' | grep -v grep || note "  (none)"
    note "--- adb daemon path (which adb on PATH):"
    which adb || note "  (no adb on PATH)"
    note "--- recent build log size:"
    [ -f "$BUILD_LOG" ] && ls -la "$BUILD_LOG" || note "  no build log yet"
    note "--- latest screencap:"
    [ -f "$LATEST_SHOT" ] && ls -la "$LATEST_SHOT" || note "  no screencap yet"
}

verb_ports() {
    step "adb daemon + device state"
    note "--- adb processes:"
    ps aux | grep -E 'adb fork-server|/adb ' | grep -v grep || note "  (none)"
    note "--- adb devices:"
    $ADB_BIN devices -l
    note "--- $SERIAL state:"
    $ADB get-state || true
}

verb_adb_restart() {
    step "restart adb daemon (Unity-bundled adb)"
    pkill -9 -f "adb fork-server" 2>/dev/null || true
    sleep 1
    $ADB_BIN start-server 2>&1 | sed 's/^/  /'
    sleep 2
    note "--- devices:"
    $ADB_BIN devices -l | sed 's/^/  /'
    local STATE; STATE=$($ADB get-state 2>&1 || true)
    if [ "$STATE" = "device" ]; then
        note "device $SERIAL: ok"
    else
        err "device $SERIAL not reachable after restart (state: $STATE)"
        return 1
    fi
}

verb_reboot()         { step "reboot only"; $ADB reboot; }
verb_reboot_recover() {
    "$SCRIPTS_DIR/dev_reboot_recover.sh" || return $?
    # Reboot wipes the volatile setprops (debug.oculus.skipProxBlanking /
    # alwaysOn). Without re-applying them the device blanks on the next
    # prox event even though the persistent settings survive.
    verb_keep_awake_on
}
verb_wake()           { "$SCRIPTS_DIR/dev_wake.sh"; }

verb_keep_awake_on() {
    step "keep-awake ON (device state for headless automation)"
    # Save original values once per session so 'off' can restore them.
    # All four settings flip together — capture them all on first apply.
    if [ ! -f "$SETTINGS_BACKUP" ]; then
        local VR;  VR=$($ADB shell settings get secure vr_sensor_state | tr -d '\r')
        local TO;  TO=$($ADB shell settings get system screen_off_timeout | tr -d '\r')
        local SLC; SLC=$($ADB shell settings get secure skip_launch_check_requires_controllers_enabled | tr -d '\r')
        local CEM; CEM=$($ADB shell settings get secure controller_emulation_mode | tr -d '\r')
        printf 'vr_sensor_state=%s\nscreen_off_timeout=%s\nskip_launch_check_requires_controllers_enabled=%s\ncontroller_emulation_mode=%s\n' \
            "$VR" "$TO" "$SLC" "$CEM" > "$SETTINGS_BACKUP"
        note "saved originals to $SETTINGS_BACKUP: vr_sensor_state=$VR screen_off_timeout=$TO skip_launch_check_requires_controllers_enabled=$SLC controller_emulation_mode=$CEM"
    fi
    # Quest dev properties (volatile — wiped on reboot). skipProxBlanking
    # is what MQDH "Disable Proximity Sensor" actually sets — without it,
    # Quest blanks the screen the moment the proximity sensor reads "off
    # head", regardless of any other setting.
    $ADB shell setprop debug.oculus.skipProxBlanking 1
    $ADB shell setprop debug.oculus.alwaysOn 1

    # vrshell caches launch-check / controller-emulation policy and
    # actively writes-back-to-defaults on its lifecycle init. To make
    # our settings stick, kill vrshell FIRST, give the system a moment
    # to settle, THEN write the settings, THEN spoof prox_close. The
    # respawning vrshell reads the new settings on its first init.
    $ADB shell am force-stop com.oculus.vrshell 2>/dev/null || true
    sleep 2

    # Persistent settings (survive reboot).
    # - vr_sensor_state=0 + screen_off_timeout=long: never sleep.
    # - skip_launch_check_requires_controllers_enabled=true +
    #   controller_emulation_mode=1: bypass the controller-required
    #   launch interceptor so `am start` of a VR app proceeds without
    #   redirecting to LaunchCheckControllerRequiredDialogActivity.
    #   Both are needed; the skip flag alone doesn't suppress the dialog
    #   without an emulated controller present.
    $ADB shell settings put secure vr_sensor_state 0
    $ADB shell settings put system screen_off_timeout 999999999
    $ADB shell settings put secure skip_launch_check_requires_controllers_enabled true
    $ADB shell settings put secure controller_emulation_mode 1
    $ADB shell svc power stayon true 2>/dev/null || true
    $ADB shell settings put global stay_on_while_plugged_in 7 2>/dev/null || true

    # Spoof "covered" so the runtime starts a VR session immediately.
    $ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close >/dev/null 2>&1 || true

    note "applied: skipProxBlanking=$($ADB shell getprop debug.oculus.skipProxBlanking | tr -d '\r') alwaysOn=$($ADB shell getprop debug.oculus.alwaysOn | tr -d '\r') vr_sensor_state=$($ADB shell settings get secure vr_sensor_state | tr -d '\r') screen_off_timeout=$($ADB shell settings get system screen_off_timeout | tr -d '\r') skip_launch_check=$($ADB shell settings get secure skip_launch_check_requires_controllers_enabled | tr -d '\r') controller_emulation=$($ADB shell settings get secure controller_emulation_mode | tr -d '\r')"
}

verb_keep_awake_off() {
    step "keep-awake OFF (restore device defaults)"
    # Clear the Quest dev properties.
    $ADB shell setprop debug.oculus.skipProxBlanking 0
    $ADB shell setprop debug.oculus.alwaysOn 0
    if [ -f "$SETTINGS_BACKUP" ]; then
        # shellcheck source=/dev/null
        . "$SETTINGS_BACKUP"
        # If original was "null" (unset on this OS version), pick a sane
        # default and continue.
        local VR_RESTORE="${vr_sensor_state:-1}"
        [ "$VR_RESTORE" = "null" ] && VR_RESTORE=1
        local TO_RESTORE="${screen_off_timeout:-86400000}"
        [ "$TO_RESTORE" = "null" ] && TO_RESTORE=86400000
        local SLC_RESTORE="${skip_launch_check_requires_controllers_enabled:-false}"
        [ "$SLC_RESTORE" = "null" ] && SLC_RESTORE=false
        local CEM_RESTORE="${controller_emulation_mode:-0}"
        [ "$CEM_RESTORE" = "null" ] && CEM_RESTORE=0
        $ADB shell settings put secure vr_sensor_state "$VR_RESTORE"
        $ADB shell settings put system screen_off_timeout "$TO_RESTORE"
        $ADB shell settings put secure skip_launch_check_requires_controllers_enabled "$SLC_RESTORE"
        $ADB shell settings put secure controller_emulation_mode "$CEM_RESTORE"
        note "restored: vr_sensor_state=$VR_RESTORE screen_off_timeout=$TO_RESTORE skip_launch_check=$SLC_RESTORE controller_emulation=$CEM_RESTORE skipProxBlanking=0 alwaysOn=0"
    else
        # No backup — set safe defaults.
        $ADB shell settings put secure vr_sensor_state 1
        $ADB shell settings put system screen_off_timeout 86400000
        $ADB shell settings put secure skip_launch_check_requires_controllers_enabled false
        $ADB shell settings put secure controller_emulation_mode 0
        note "no backup file; set defaults vr_sensor_state=1 screen_off_timeout=86400000 skip_launch_check=false controller_emulation=0 skipProxBlanking=0 alwaysOn=0"
    fi
    $ADB shell am broadcast -a com.oculus.vrpowermanager.automation_disable >/dev/null 2>&1 || true
    # Reload vrshell so it picks up the restored settings.
    $ADB shell am force-stop com.oculus.vrshell 2>/dev/null || true
}

# Legacy verb aliases.
verb_keep_awake_start()  { verb_keep_awake_on; }
verb_keep_awake_stop()   { verb_keep_awake_off; }

verb_build() {
    # Build flags (parsed from `dev.sh build [flags]`):
    #   --with-panel / --no-panel   STEREO_SKIP_PANEL = 0 / 1 (default 1)
    #   --with-marker / --no-marker TEST_PURPLE_CIRCLE = 1 / 0 (default 1)
    # Equivalent env vars still honored if set; CLI flags take precedence.
    # CLI flags are necessary because env-var prefixes break Claude Code's
    # permission matcher (e.g. `FOO=1 dev.sh build` doesn't match the
    # `Bash(.../dev.sh *)` allowlist pattern).
    local SKIP_PANEL="${STEREO_SKIP_PANEL:-1}"
    local USE_MARKER="${TEST_PURPLE_CIRCLE:-1}"
    local STOCK_QUAD="${STEREO_USE_STOCK_QUAD:-0}"
    while [ $# -gt 0 ]; do
        case "$1" in
            --with-panel)     SKIP_PANEL=0 ;;
            --no-panel)       SKIP_PANEL=1 ;;
            --with-marker)    USE_MARKER=1 ;;
            --no-marker)      USE_MARKER=0 ;;
            --stock-quad)     STOCK_QUAD=1 ;;
            --no-stock-quad)  STOCK_QUAD=0 ;;
            *) err "unknown build flag: $1"; return 2 ;;
        esac
        shift
    done
    step "Unity build (STEREO_SKIP_PANEL=$SKIP_PANEL, TEST_PURPLE_CIRCLE=$USE_MARKER, STEREO_USE_STOCK_QUAD=$STOCK_QUAD)"
    STEREO_SKIP_PANEL="$SKIP_PANEL" \
    TEST_PURPLE_CIRCLE="$USE_MARKER" \
    STEREO_USE_STOCK_QUAD="$STOCK_QUAD" \
        "$UNITY" -batchmode -nographics -quit \
            -projectPath "$PROJECT" \
            -executeMethod AutoBuilder.BuildAndroid \
            -logFile "$BUILD_LOG"
    local RC=$?
    if [ $RC -ne 0 ]; then
        err "build FAIL — last 30 lines of $BUILD_LOG:"
        tail -30 "$BUILD_LOG" >&2
        return 1
    fi
    grep -E "TestPurpleCircleSetup|StereoCompositorSceneSetup|Build succeeded|error CS" "$BUILD_LOG" | tail -8
    step "build OK"
}

# Ensure adb daemon is alive and our serial reachable. The daemon dies
# spontaneously on this Mac (multiple adb installs — brew / Unity /
# Android Studio — race for the port). Self-heal before any verb that
# talks to the device.
ensure_adb_alive() {
    local STATE
    STATE=$($ADB get-state 2>&1 || true)
    if [ "$STATE" = "device" ]; then return 0; fi
    note "adb daemon dead or device unreachable (state: $STATE) — restarting"
    verb_adb_restart
}

# Ensure the Quest's display is producing frames. Probe via screencap; if
# the buffer is empty/tiny, the screen is off — run the wake sequence
# (KEYCODE_WAKEUP, prox_close, KEYCODE_POWER toggle) and re-probe. Verbs
# that consume display output (screencap, verify-color) should call this
# first, otherwise they hit silent 0-byte captures.
ensure_device_awake() {
    ensure_adb_alive
    local TMP SIZE
    TMP=$(mktemp /tmp/quest_awake_probe.XXXXXX.png)
    $ADB shell screencap -p > "$TMP" 2>/dev/null
    SIZE=$(stat -f%z "$TMP" 2>/dev/null || stat -c%s "$TMP" 2>/dev/null || echo 0)
    rm -f "$TMP"
    if [ "$SIZE" -ge 10000 ]; then return 0; fi

    note "device display asleep (screencap probe: $SIZE bytes) — waking"
    "$SCRIPTS_DIR/dev_wake.sh" >/dev/null 2>&1 || true
    sleep 1

    TMP=$(mktemp /tmp/quest_awake_probe.XXXXXX.png)
    $ADB shell screencap -p > "$TMP" 2>/dev/null
    SIZE=$(stat -f%z "$TMP" 2>/dev/null || stat -c%s "$TMP" 2>/dev/null || echo 0)
    rm -f "$TMP"
    if [ "$SIZE" -lt 10000 ]; then
        err "device still asleep after wake (probe: $SIZE bytes)"
        return 1
    fi
    note "device awake (probe: $SIZE bytes)"
    return 0
}

verb_install_app() {
    ensure_adb_alive
    "$SCRIPTS_DIR/dev_install_verify.sh"
    local RC=$?
    # If install hit a daemon issue, restart adb and retry once.
    if [ $RC -ne 0 ]; then
        note "install failed once; restarting adb daemon and retrying"
        verb_adb_restart || return 1
        "$SCRIPTS_DIR/dev_install_verify.sh"
        RC=$?
    fi
    return $RC
}
# Legacy alias.
verb_install() { verb_install_app; }

verb_uninstall() {
    ensure_adb_alive
    step "uninstall $PKG"
    $ADB uninstall "$PKG" || true
}

verb_run_app()  { ensure_device_awake; "$SCRIPTS_DIR/dev_launch_verify.sh"; }
# Legacy alias.
verb_launch()   { verb_run_app; }

verb_kill_app() {
    ensure_adb_alive
    step "kill-app: force-stop $PKG (device returns to ready-for-automation)"
    $ADB shell am force-stop "$PKG"
}

verb_reboot_app() { verb_reboot_recover; }
verb_screencap() { ensure_device_awake; "$SCRIPTS_DIR/dev_screencap.sh" "$LATEST_SHOT"; }

verb_layer_count() {
    # Print the latest VrApi LCnt=N value and exit 0 if parsed. Composes
    # with `dev.sh logs` but is the canonical layer-count probe so the
    # plan steps can declare expected values (e.g. baseline 5, panel-on 6).
    ensure_adb_alive
    local LCNT
    LCNT=$($ADB logcat -d 2>/dev/null | grep "VrApi" | tail -3 | grep -oE 'LCnt=[0-9]+' | tail -1)
    if [ -z "$LCNT" ]; then
        err "no VrApi LCnt= line found in logcat (app running? logcat ring-buffer flushed?)"
        return 1
    fi
    echo "$LCNT"
}

verb_alive_for() {
    # Poll pidof every 1 s for SECONDS; exit 0 only if PID is stable and
    # non-empty for the full interval. Replaces ad-hoc `sleep N && pidof`
    # patterns so steps can declare a stability contract.
    local SECONDS_TO_WATCH="${1:-15}"
    if ! [[ "$SECONDS_TO_WATCH" =~ ^[0-9]+$ ]]; then
        err "usage: dev.sh alive-for <seconds>"
        return 1
    fi
    ensure_adb_alive
    step "alive-for: watching $PKG for $SECONDS_TO_WATCH s"
    local FIRST_PID="" PID
    local i=0
    while [ "$i" -lt "$SECONDS_TO_WATCH" ]; do
        PID=$($ADB shell pidof "$PKG" 2>/dev/null | tr -d '\r' | tr -d ' ')
        if [ -z "$PID" ]; then
            err "alive-for: app not running at t=${i}s (PID empty)"
            return 1
        fi
        if [ -z "$FIRST_PID" ]; then
            FIRST_PID="$PID"
            note "  t=0s PID=$PID"
        elif [ "$PID" != "$FIRST_PID" ]; then
            err "alive-for: PID changed at t=${i}s ($FIRST_PID -> $PID; app crashed and restarted)"
            return 1
        fi
        sleep 1
        i=$((i + 1))
    done
    note "  t=${SECONDS_TO_WATCH}s PID=$FIRST_PID (stable)"
    return 0
}

verb_verify_color() {
    # Color-agnostic per-eye disc verifier.
    # Args: [LEFT_COLOR [RIGHT_COLOR]]
    #   LEFT_COLOR  expected left-eye color (default: yellow)
    #   RIGHT_COLOR expected right-eye color (default: same as LEFT)
    # Color forms: name (yellow|purple|red|green|blue|cyan|magenta|orange|
    # white|black), R,G,B, or #RRGGBB.
    local LEFT="${1:-yellow}"
    local RIGHT="${2:-$LEFT}"
    # Re-grab the screencap if missing / stale (>30 s) / 0-byte. ensure the
    # display is awake first so we don't verify a black frame.
    if [ ! -f "$LATEST_SHOT" ] \
        || [ "$(find "$LATEST_SHOT" -mmin +0.5 2>/dev/null)" = "$LATEST_SHOT" ] \
        || [ "$(stat -f%z "$LATEST_SHOT" 2>/dev/null || stat -c%s "$LATEST_SHOT")" -lt 10000 ]; then
        note "no recent valid screencap — capturing now"
        verb_screencap
    fi
    python3 "$SCRIPTS_DIR/dev_verify_color.py" --left "$LEFT" --right "$RIGHT" "$LATEST_SHOT"
}

verb_logs() {
    local PATTERN="${1:-}"
    if [ -z "$PATTERN" ]; then
        err "usage: dev.sh logs <regex-pattern>"
        return 1
    fi
    step "last 200 logcat lines matching: $PATTERN"
    $ADB logcat -d | grep -E "$PATTERN" | tail -200 | tee "$LATEST_LOGCAT"
}

verb_crash_stack() {
    step "latest crash buffer (signal/SIGSEGV/backtrace)"
    $ADB logcat -b crash -d | tail -120 | tee "$LATEST_CRASH"
    note "(saved to $LATEST_CRASH)"
}

verb_pidof() {
    $ADB shell pidof "$PKG" | tr -d '\r'
}

verb_dismiss() {
    step "dismiss system dialogs (Guardian, controller-required, etc.)"
    # Most common offender: the Guardian boundary-setup dialog. Pops up on
    # idle / off-head / fresh boot. Kill the package outright.
    $ADB shell am force-stop com.oculus.guardian
    # Quest controller-required dialog is hosted by vrshell.
    $ADB shell am force-stop com.oculus.vrshell.systemdialog 2>/dev/null || true
    # Plus a few KEYCODE_BACK presses for any other dialogs.
    for i in 1 2 3; do
        $ADB shell input keyevent KEYCODE_BACK
        sleep 0.3
    done
    note "top activity now:"
    $ADB shell dumpsys activity activities | grep "topResumedActivity" | head -2 | sed 's/^/  /'
}

verb_tag() {
    local NAME="${1:-}"
    if [ -z "$NAME" ]; then
        err "usage: dev.sh tag <name>"
        return 1
    fi
    step "tag latest artifacts as: $NAME"
    local ANY=0
    for src in "$LATEST_SHOT" "$LATEST_LOGCAT" "$LATEST_CRASH" "$BUILD_LOG" "$RUN_LOG"; do
        if [ -f "$src" ]; then
            local ext="${src##*.}"
            local base; base=$(basename "$src" ".$ext")
            local dst="/tmp/quest_tagged_${NAME}_${base}.${ext}"
            cp "$src" "$dst"
            note "  $src -> $dst"
            ANY=1
        fi
    done
    [ $ANY -eq 0 ] && err "no latest artifacts to tag" && return 1
    return 0
}

# ----- Test skills (each in scripts/tests/test_<name>.sh) -----
verb_tests() {
    step "available test skills (scripts/tests/test_*.sh)"
    if [ ! -d "$SCRIPTS_DIR/tests" ]; then
        err "no scripts/tests directory"
        return 1
    fi
    for f in "$SCRIPTS_DIR"/tests/test_*.sh; do
        [ -f "$f" ] || continue
        local NAME; NAME=$(basename "$f" .sh)
        NAME="${NAME#test_}"
        # Pull the first comment block as a description.
        local DESC; DESC=$(awk 'NR>1 && /^#/ {sub(/^# ?/,""); print; exit} NR>1 && !/^#/ {exit}' "$f")
        printf '  %-20s %s\n' "$NAME" "$DESC"
    done
}

verb_test() {
    local NAME="${1:-}"
    if [ -z "$NAME" ]; then
        err "usage: dev.sh test <name> [args...]   (use 'dev.sh tests' to list)"
        return 1
    fi
    shift
    local TEST_SCRIPT="$SCRIPTS_DIR/tests/test_${NAME//-/_}.sh"
    if [ ! -x "$TEST_SCRIPT" ]; then
        err "no such test: $NAME (looked at $TEST_SCRIPT)"
        verb_tests
        return 1
    fi
    # Mirror everything to /tmp/quest_run.log so a Monitor can stream
    # banners + key metrics to chat in real time. Forward any extra args
    # to the test (e.g. `dev.sh test marker green` -> test_marker.sh green).
    : > "$RUN_LOG"
    "$TEST_SCRIPT" "$@" 2>&1 | tee -a "$RUN_LOG"
    return ${PIPESTATUS[0]}
}

# ---------- dispatch ----------
VERB="${1:-help}"
shift || true

case "$VERB" in
    help|-h|--help)   verb_help ;;
    init)             verb_init ;;
    begin-automation) verb_begin_automation ;;
    end-automation)   verb_end_automation ;;
    doctor)           verb_doctor ;;
    ports)            verb_ports ;;
    adb-restart)      verb_adb_restart ;;
    reboot)           verb_reboot ;;
    reboot-recover)   verb_reboot_recover ;;
    wake)             verb_wake ;;
    keep-awake-on)    verb_keep_awake_on ;;
    keep-awake-off)   verb_keep_awake_off ;;
    keep-awake-start) verb_keep_awake_start ;;
    keep-awake-stop)  verb_keep_awake_stop ;;
    build)            verb_build "$@" ;;
    install-app)      verb_install_app ;;
    install)          verb_install ;;
    uninstall)        verb_uninstall ;;
    run-app)          verb_run_app ;;
    launch)           verb_launch ;;
    kill-app)         verb_kill_app ;;
    reboot-app)       verb_reboot_app ;;
    screencap)        verb_screencap ;;
    verify-color)     verb_verify_color "$@" ;;
    layer-count)      verb_layer_count ;;
    alive-for)        verb_alive_for "$@" ;;
    logs)             verb_logs "$@" ;;
    crash-stack)      verb_crash_stack ;;
    pidof)            verb_pidof ;;
    dismiss)          verb_dismiss ;;
    tag)              verb_tag "$@" ;;
    test)             verb_test "$@" ;;
    tests)            verb_tests ;;
    *)
        err "unknown verb: $VERB"
        verb_help
        exit 2
        ;;
esac

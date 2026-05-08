# Autonomy directives for this repo

This is a solo-dev Unity 6 / Quest 3 MR project. Treat these as durable policy.
"Work autonomously" should be taken literally. If a hypothesis is exhausted,
update the plan doc and propose a path — do not stop and wait for per-action
approval.

The architect's plan + this file together set the rhythm: small incremental
code changes, each with a defined visual contract, validated automatically
where possible, journaled in `STEREO_COMPOSITOR_PLAN.md` as you go.

## The default work loop

This is the rhythm. Use it unless the architect plan says otherwise.

1. **Make a focused, small code change.** One API surface or one observable
   behavior at a time. Commit-sized increments, not branch-sized.
2. **Incremental compile-check** — run Unity in batchmode without `-executeMethod`
   to validate scripts compile (~30 s, no full build). Example:
   `Unity -batchmode -nographics -quit -projectPath ... -logFile compile.log`
   then `grep "error CS" compile.log`. If clean, proceed; if not, fix and repeat.
3. **Incremental APK build** via `AutoBuilder.BuildAndroid`. Do NOT wipe
   `Library/Bee` or `Library/ScriptAssemblies` for a routine iteration —
   incremental builds are 10–60 s, clean builds are 5–15 min.
4. **Install** with `adb install -r .../MR_Passthrough.apk`. Verify install
   actually succeeded (look for `Success` in stdout; an adb daemon conflict
   can silently fail).
5. **Confirm running APK == just-built APK.** Compare md5:
   `md5sum Builds/MR_Passthrough.apk` vs `adb shell md5sum /data/app/.../base.apk`.
   If they don't match, the install regressed; redo step 4 with a clean adb
   daemon.
6. **Force-stop, clear logcat, launch.**
7. **Verify alive + no crash** for at least 10 s of frames:
   - `adb shell pidof <pkg>` returns a stable PID across multiple checks
   - `adb logcat -d | grep -c "Crash detected.*<pkg>"` is `0`
   - VrApi `LCnt=N` in logcat tells you how many compositor layers are
     being submitted (sanity check that your code is reaching the runtime).
8. **Visually verify** per the step's visual contract — see "Visual
   confirmation" below.
9. **Iterate.** Aim for 5–15 min per loop, not hours.

## Pre-authorized actions (just do them; do not ask)

**Blanket adb authorization for the lab Quest 3 (`2G0YC5ZGB405BG`):** every
`adb` command targeted at that serial is pre-authorized. Never stop to ask
permission for an adb action — including ones not enumerated below. The
enumerated list is documentation, not a closed set. The only adb-related
gating is the device serial (do not run destructive commands against
unrelated attached devices) and the standard global rules (no remote
mutations beyond this workspace, etc.).

### Caches and builds
- Delete and recreate `Library/`, `Builds/`, `Logs/`, `Temp/`, `Obj/`, `build/`,
  `.gradle/` at any depth. These are caches; regenerated on next build.
- Run unlimited `Unity -batchmode` builds.
- Modify `Packages/manifest.json` to add/remove/upgrade packages.

### Device (Quest 3 serial `2G0YC5ZGB405BG`, lab device on a stand at 0.8 m, NOT being worn)
- **All `adb` commands targeted at this device are pre-authorized.** Do not
  pause to confirm individual adb actions, including but not limited to:
  `adb install -r`, `adb uninstall`, `adb shell am force-stop`, `adb shell
  am start`, `adb shell am force-stop com.oculus.vrshell` (kills the Quest
  controller-required dialog so headless launches succeed), `adb logcat
  -c`, `adb logcat -d`, `adb shell screencap`, `adb shell input keyevent`,
  `adb shell setprop`, `adb shell pm path`, `adb shell md5sum`, `adb shell
  ps`, `adb shell dumpsys`, `adb shell settings put`, `adb shell
  wm`, `adb wait-for-device`, `adb reboot`, `adb push`, `adb pull` to/from
  app-writable paths, `adb forward`, `adb shell svc`. The device is in a
  lab — reboot is non-disruptive.
- Kill / restart adb daemons as needed (Mac has both Homebrew and Unity
  installations of adb fighting for port 5037; pick one and stick with it).
- The serial `2G0YC5ZGB405BG` scopes the authorization: do not run
  destructive adb commands against any *other* device that happens to be
  attached.

### Headless / on-a-stand operation: keep the device awake

The Quest 3 is on a stand and not being worn. By default Quest will:

- Refuse to render / screencap when the proximity sensor reads "uncovered"
  (lens cover open, no head detected). Screencaps come back black; apps
  pause; logcat goes quiet.
- Power down to "sleep" after a short idle, especially after reboot. Apps
  launched while sleeping never get a render frame.
- After `adb reboot`, the device boots but stops at a sleep / boot-into-
  off-head state — it does NOT auto-resume into apps. Without intervention
  the autonomous flow stalls here.

The standard "lab-mode" recipe to keep the device permanently awake and
behaving as if worn:

```sh
ADB="adb -s 2G0YC5ZGB405BG"
$ADB shell input keyevent KEYCODE_WAKEUP                                 # wake from sleep
$ADB shell am broadcast -a com.oculus.vrpowermanager.prox_close          # fake proximity = covered
$ADB shell am broadcast -a com.oculus.vrpowermanager.automation_disable  # disable idle-power-down
```

`prox_close` is the load-bearing one: it makes the OS treat the device as
worn, so rendering, screencap, and the OpenXR session all behave normally
even though the lens cover is open and the proximity sensor sees nothing.
The state persists until the next `adb reboot` or until you send
`prox_open`. Re-broadcast `prox_close` after every reboot.

### CANONICAL STATE MACHINE (top-level, do not improvise around this)

When the user asks for "top level states and actions", reaffirm exactly this:

**States:**
- `ready-for-automation` — adb up, persistent settings applied, VR session
  live, no app foreground (Quest shell).
- `ready-to-run-app` — `ready-for-automation` + our APK installed (md5
  verified against the local build).
- `running-app` — `ready-to-run-app` + our app foreground, alive.

**Primary transitions (verbs on `dev.sh`):**
| Verb | Transition |
|---|---|
| `dev.sh begin-automation` | any → ready-for-automation |
| `dev.sh install-app`      | ready-for-automation → ready-to-run-app |
| `dev.sh run-app`          | ready-to-run-app → running-app |
| `dev.sh kill-app`         | running-app → ready-for-automation |
| `dev.sh reboot-app`       | any → ready-for-automation (via reboot) |
| `dev.sh end-automation`   | exits the harness, restores Quest defaults |

`install` / `launch` / `reboot-recover` are kept as legacy aliases of the
canonical names; new code should use the canonical forms.

### THE RULE: use `scripts/dev.sh` for all automation. Never chain commands.

[`scripts/dev.sh`](scripts/dev.sh) is the single router for every dev-loop
operation. It dispatches to verbs (`init`, `begin-automation`, `build`,
`install-app`, `run-app`, `kill-app`, `screencap`, `verify-color`, `tag`,
`logs`, `crash-stack`, `keep-awake-*`, `reboot-app`, `test`, etc.). One
permission rule covers it all:
`Bash(/Users/richardbailey/RichardClaude/Unity/scripts/dev.sh *)`.

**Two non-negotiable rules for the AI:**

1. **Never chain commands.** No `;`, `&&`, `||`, pipes, or `$(...)` in a
   single Bash tool call. Each chained piece would otherwise need its own
   allow rule. Orchestrate by issuing **separate `Bash` tool calls** —
   `dev.sh build`, then `dev.sh install`, then `dev.sh launch`. Each call's
   output streams to chat independently, which is also how live progress
   becomes visible in CCD.

2. **Don't invent new scripts mid-flow.** If a recurring need shows up
   (new diagnostic, new chain), add a verb to `dev.sh` rather than a
   one-off `dev_foo.sh`. New scripts get pinned as new exact-string
   permission entries; new verbs do not.

**Fixed artifact paths** (verbs write here; AI reads from here):
- `/tmp/quest_latest_shot.png` — last screencap (`dev.sh screencap`)
- `/tmp/quest_latest_logcat.txt` — last `dev.sh logs <pattern>` output
- `/tmp/quest_latest_crash.txt` — last `dev.sh crash-stack` output
- `/tmp/quest_build.log` — last `dev.sh build` output

For named runs, `dev.sh tag <name>` snapshots all `quest_latest_*` to
`/tmp/quest_tagged_<name>_*`.

**At the start of every autonomous session**, call `dev.sh init` once. It
verifies Pillow, adb, device reachability, Unity binary, project tree, and
the persistent keep-awake state (settings + setprops). If keep-awake is
not fully applied (incl. the volatile `debug.oculus.skipProxBlanking` /
`alwaysOn` setprops which reboot wipes), it re-applies them. Fails loudly
on any missing piece with a remediation hint — no surprises mid-flow.

**Underlying scripts** (called by `dev.sh`, NOT directly by the AI; left in
place so they can be edited individually):
[`dev_reboot_recover.sh`](scripts/dev_reboot_recover.sh),
[`dev_wake.sh`](scripts/dev_wake.sh),
[`dev_install_verify.sh`](scripts/dev_install_verify.sh),
[`dev_launch_verify.sh`](scripts/dev_launch_verify.sh),
[`dev_screencap.sh`](scripts/dev_screencap.sh),
[`dev_verify_purple.py`](scripts/dev_verify_purple.py).

**About keep-awake state:** the model is *persistent settings + volatile
setprops applied once*, not a polling daemon. `dev.sh keep-awake-on`
(invoked by `init` and `begin-automation`) sets:

- `secure vr_sensor_state = 0` (persistent — survives reboot)
- `system screen_off_timeout = 999999999` (persistent)
- `setprop debug.oculus.skipProxBlanking 1` (volatile — wiped on reboot,
  but load-bearing: without it, Quest blanks the display the moment the
  proximity sensor reads "off head", regardless of the other settings)
- `setprop debug.oculus.alwaysOn 1` (volatile)

`init` checks BOTH the settings and the setprops; if either is missing it
re-applies. `reboot-recover` re-applies automatically after the reboot.
Neither `dev_launch_verify.sh` nor `dev_wake.sh` kills `com.oculus.vrshell`
anymore — that was the old daemon-era doctrine and was tearing down VR
sessions.

Standard work-loop after a code change (each line is a SEPARATE Bash tool
call by the AI — do not collapse into one chained command):

```sh
dev.sh build         # Unity batchmode build (STEREO_SKIP_PANEL=1 default)
dev.sh install-app   # install + md5 verify
dev.sh run-app       # force-stop our app + am start + verify alive
dev.sh screencap     # writes /tmp/quest_latest_shot.png
dev.sh verify-color  # pixel-sample per eye against expected color
```

Issue each verb as a separate `Bash` tool call — do NOT chain them. Each
call's output streams to chat independently, which is also how live progress
becomes visible in CCD.

Reboot recovery is rare (only when device-state pollution warrants Tier 4)
because the persistent keep-awake settings (`vr_sensor_state=0`,
`screen_off_timeout=long`) plus the `debug.oculus.skipProxBlanking=1`
setprop keep the device alive between sessions. After reboot, setprops
are wiped — `dev.sh reboot-recover` re-applies `keep-awake-on`
automatically before returning.

### Quest controller-required dialog mechanic (load-bearing in dev_launch_verify.sh)

The dialog is intercepted at the system_server level by
`RequiresControllersLaunchInterceptor`, NOT by `com.oculus.vrshell`. Killing
vrshell *before* `am start` is ineffective — the interceptor still fires and
redirects our launch. The pattern that works (encoded in
`dev_launch_verify.sh`):

1. `am force-stop com.oculus.vrshell` (clears any active dialog)
2. **wait 3 s** — load-bearing; gives the system time to settle
3. `am start -n <pkg>/<activity>` — interceptor fires but dialog never
   gains focus
4. Post-launch loop: `am force-stop com.oculus.vrshell` x6 over 3 s — if
   the dialog snuck through, this dismisses it and the AM resumes the
   original launch

If you ever change the launch flow, preserve the 3-second wait between
vrshell-kill and `am start`. Without it, vrshell respawns in time for the
interceptor to redirect, and `pidof` comes back empty.

### Code edits
- Edit any file under `VibeUnity1/Assets/`, `VibeUnity1/ProjectSettings/`,
  `VibeUnity1/Packages/`, `VibeUnity1/UserSettings/`.
- Comment out / disable runtime spawners (`[RuntimeInitializeOnLoad]`) when
  isolating bisect test cases.
- Add diagnostic `Debug.Log` lines liberally; clean them up at end of bisect.

### Git
- Commit work-in-progress with descriptive messages on `main` (this is a
  solo dev repo).
- Push to `origin main` (single-contributor repo).
- Stage + commit your own modifications without per-commit confirmation.

## Recovery escalation ladder

When something doesn't work, climb in order. Don't skip steps unless the
symptom screams "device polluted" (e.g. baseline configs that worked an
hour ago suddenly fail without code changes).

### Tier 1 — assume code regression (default response)
- Read the actual error / crash signature.
- Inspect the diff vs last known-working state.
- Hypothesize, test, iterate per the work loop.

### Tier 2 — clean install on the device
Use when: app behaves "stale" (running old build), install reported errors,
or app is in a weird state from previous run.
- `adb uninstall <pkg>` then fresh install.
- Force-stop everything Unity-related. Re-launch.
- Re-run the work-loop verify steps.

### Tier 3 — suspect Unity-corrupted build
Use when: behavior is VERY unexpected — e.g. app crashes in a totally
unrelated subsystem (URP, XR display init) on a code change that
shouldn't touch that surface; or the running version doesn't match
what the source says.
- Wipe `Library/Bee/` (huge — 5–15 GB; regenerated in ~10 min).
- Wipe `Library/ScriptAssemblies/` (small).
- Full clean rebuild via `AutoBuilder.BuildAndroid`.
- Tier 2 reinstall.

### Tier 4 — device-side state pollution
Use when: Tier 3 didn't help OR symptoms are XR-display / OpenXR loader
related (`XRDisplaySubsystem.TryGetRenderPass` null, etc.) AND nothing
in the build pipeline changed.
- `adb reboot`, wait 60 s, `adb wait-for-device`.
- Tier 3 + Tier 2.

### Tier 5 — re-evaluate the hypothesis
Use when: Tier 3 + Tier 4 didn't help, OR the same "unexpected" symptom
repeats after a verified-clean rebuild + reboot.
- The code change probably IS the cause. Don't keep flailing.
- **Update `STEREO_COMPOSITOR_PLAN.md`** with: what was tried, what failed,
  what the current symptom is, what hypotheses remain.
- **Spawn a Plan agent** ("ask the architect") with the updated context
  and request an incremental plan. Do not just keep iterating blindly.

## Verification practices

### "Is the right APK actually running?"

After every install, before testing:

```sh
LOCAL=$(md5sum Builds/MR_Passthrough.apk | awk '{print $1}')
DEV_PATH=$(adb shell pm path com.UnityTechnologies.com.unity.template.urpblank | sed 's/^package://' | tr -d '\r')
DEV=$(adb shell md5sum "$DEV_PATH" | awk '{print $1}')
test "$LOCAL" = "$DEV" && echo OK || echo "STALE INSTALL"
```

If STALE: adb daemon issue or install failed silently. Restart adb,
uninstall + reinstall.

### "Did it crash, even though logcat says no SIGSEGV?"

Buffer rolls fast. Use:
`adb logcat -d | grep "Crash detected.*<pkg>"` — this comes from
`DiagnosticsCollectorService` and is the canonical signal.

### "Is the panel actually being submitted?"

`adb logcat -d | grep "VrApi" | tail -3` — `LCnt=N` is the layer count.
Baseline (no panel): ~3. With one quad layer: ~5–6. With our 6-quad
stereo split: ~9.

## Visual confirmation

Every code change should pre-declare what visual outcome (if any) is expected
on device, and whether that outcome is auto-confirmable. The default is
"try to make it auto-confirmable." Auto-confirms enable the work loop to run
without a human in the seat, which is the whole point of autonomy.

### Step-level visual contract

Before starting each step in the architect's incremental plan, write into
that step:

- **Expected visual outcome** — what should appear on the panel? (e.g.
  "panel shows solid red in left eye, solid blue in right eye"; "panel
  shows the first frame of HLS video with mask cutout visible"; "no
  visible change — this step adds no rendering").
- **Auto-confirmation method** — how does the AI verify it? Pick one:
  - **Pixel sample check** on `adb screencap` (read N×N region at known
    coords, compare RGB to expected color within tolerance).
  - **Layer-count check** on `VrApi: LCnt=` in logcat (e.g. "expect
    LCnt to go from baseline 5 to 6 with our panel added").
  - **Log-marker check** (Debug.Log lines we emit and grep for).
  - **None** (rare; only for steps that touch no rendering, e.g.
    refactoring a manager class).
- **Failure modes** to distinguish before changing course (see below).

### Auto-confirm recipes

**Pixel sample at known coordinate** (from a screencap PNG):

```sh
# Take screencap. Stereo image; left half is left eye.
adb shell screencap -p > /tmp/shot.png

# Sample a 32×32 region in the center of the left eye, average RGB.
python3 - <<'PY'
from PIL import Image
img = Image.open("/tmp/shot.png")
W, H = img.size
# Left eye ≈ left half of image; center ≈ (W/4, H/2).
crop = img.crop((W//4 - 16, H//2 - 16, W//4 + 16, H//2 + 16))
r, g, b = [sum(c) // (32*32) for c in zip(*list(crop.getdata()))][:3]
print(f"left-eye-center avg RGB: ({r},{g},{b})")
PY
```

**Layer-count check** (after launch + 5 s wait):

```sh
adb logcat -d | grep "VrApi" | tail -1 | grep -oE 'LCnt=[0-9]+'
```

### Failure-mode disambiguation order

When auto-confirm fails, run these checks in order BEFORE concluding the
code change is wrong:

1. **Is the device awake?** If screencap is all-black or shows the Quest
   home env, the device went to standby. Wake it (`adb shell input
   keyevent KEYCODE_POWER`, or in this lab setup nudge the device to
   trip the proximity sensor). Re-screencap.
2. **Is the app actually running?** `pidof <pkg>` returns a PID? If not,
   it crashed — check `Crash detected: NATIVE_CRASH_REPORT`. Different
   problem from visual failure.
3. **Is a layer being submitted at all?** Check `VrApi: LCnt=` against
   baseline. If LCnt didn't go up as expected, the panel is missing —
   probably code regression, but could also be a scene-authoring miss.
4. **Is the panel in the device's view direction?** Per the device-positioning
   notes in `STEREO_COMPOSITOR_PLAN.md`, panel must be parented to the
   camera rig with local-space coords (default `(0, 0, 1.5)` facing the
   device). World-space `(0, 1.5, 1.5)` puts it above the device's
   frustum (device sits at ~0.8 m head height; panel at world Y=1.5 m
   ends up above the eyebrow line of the device).
5. **Did the install land?** Re-run the md5 verification step from the
   work loop. Stale install masks every other check.

ONLY after 1–5 are clean and the auto-confirm still fails: assume the
code change is the cause. Even then:

### When auto-confirm keeps failing — DO NOT pivot architecture

This is the trap: a failed visual check is NOT a signal to rewrite the
approach. It's a signal to debug the current code. Specifically:

- **Stay on the current path.** Don't jump from "ILayerHandler approach"
  to "OVROverlay approach" because one screencap was wrong.
- **Make the test smaller.** If a 6-layer stereo+mask+overlay panel
  doesn't show the right pixels, drop back to a single-layer mono
  static texture and confirm THAT shows. Then add stereo. Then add
  mask. Then add overlay. Each sub-step gets its own visual contract.
- **Add diagnostic logging** to narrow the failure: log the SubImage
  rect being submitted, the swapchain handle, the EyeVisibility value.
  Re-run, read the logs, infer.
- **Bisect the code change** if the diff has multiple modifications.
  Revert to last-known-visible state, re-apply changes one at a time.

If after 3–5 iterations the auto-confirm is still wrong AND the
diagnostic logs don't narrow it: escalate to architect (Tier 5) with
the logs, the screencap, and the hypotheses tried. Update the plan doc.
**Last resort, not first**: leave a note in the plan doc requesting
manual visual validation by a human wearing the headset, and proceed
with whatever next step doesn't depend on it.

## Behavioral preferences

- **Incrementalism over big leaps.** Smallest visible result first; add
  layers (literally and figuratively) one at a time.
- **Steady visible progress beats clever architecture.** A working but
  hacky thing that ships pixels is worth more than a clean design that
  doesn't compile.
- **End-of-segment summaries, not per-action narration.** Batch findings;
  don't ask for confirmation between every tool call.
- **Update the plan doc as a journal.** Each major iteration appends a
  dated note to `STEREO_COMPOSITOR_PLAN.md`. The next session reads the
  doc and continues; chat history is ephemeral.
- **When stuck twice on the same hypothesis, escalate to architect.**
  Don't loop on the same dead-end approach for hours.

## When to STILL confirm

- Anything that affects another repo, account, or remote not in this
  workspace.
- Force-pushing to `main` (regular pushes are fine).
- Permanently deleting committed source files outside cache directories.
- Permanently changing OpenXR / XR plug-in management settings via
  the build pipeline (these were found to corrupt session state). Use
  Unity Editor UI for those, manually, once.
- Running operations across multiple repos.
- Uninstalling/changing system Unity packages on the Mac.

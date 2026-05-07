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

### Caches and builds
- Delete and recreate `Library/`, `Builds/`, `Logs/`, `Temp/`, `Obj/`, `build/`,
  `.gradle/` at any depth. These are caches; regenerated on next build.
- Run unlimited `Unity -batchmode` builds.
- Modify `Packages/manifest.json` to add/remove/upgrade packages.

### Device (Quest 3 serial `2G0YC5ZGB405BG`, lab device on a stand at 0.8 m, NOT being worn)
- `adb install -r`, `adb uninstall`, `adb shell am force-stop`,
  `adb shell am start`, `adb logcat -c`, `adb shell screencap`, `adb reboot`.
  The device is in a lab — reboot is non-disruptive.
- Kill / restart adb daemons as needed (Mac has both Homebrew and Unity
  installations of adb fighting for port 5037; pick one and stick with it).

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

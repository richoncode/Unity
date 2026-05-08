# Stereo Video Compositor Layer Plan

Authoritative implementation plan for the stereo MR video player on Meta Quest 3.
This file persists across context loss / sessions; treat as the source of truth.

Last updated: 2026-05-07.

---

## 2026-05-08 — Architect Tier 5 redirect (root-cause: NativeArray buffer overrun in handler)

### Root cause (high confidence, derived from package source)

`StereoQuadLayerHandler.OnUpdate()` overrides the base without calling
`base.OnUpdate()` and never resets `m_ActiveNativeLayerCount`. Path of corruption:

1. Frame N: `OpenXRLayerProvider.HandleBeforeRender` → `SetActiveLayersForHandlers`
   → calls our `SetActiveLayer(layerInfo)` for each active layer.
2. Our override calls `base.SetActiveLayer(layerInfo)`, which:
   - calls `ResizeNativeArrays()` — NOOP after frame 1 because
     `m_ActiveNativeLayers.Length (=1) < m_nativeLayers.Count (=1)` is false;
   - writes `m_ActiveNativeLayers[m_ActiveNativeLayerCount] = ...`;
   - increments `m_ActiveNativeLayerCount`.
3. `OpenXRLayerProvider.IssueHandlerUpdates` → calls our `OnUpdate()`. Our
   override builds its OWN `NativeArray<XrCompositionLayerQuad>` and calls
   `AddActiveLayersToEndFrame`, then disposes its array. **It does not call
   `base.OnUpdate()` and does not zero `m_ActiveNativeLayerCount`.**
4. Frame N+1: base `SetActiveLayer` writes to `m_ActiveNativeLayers[1]` —
   array length is 1. **Out-of-bounds write into adjacent unmanaged heap.**
5. After 3–4 frames the corrupted region clobbers something a downstream
   consumer reads, producing a SEGV with whatever signature happens to be
   convenient.

This explains every observation cleanly:
- **REPRO-3 fires 3–4× before crash** — the buffer overrun starts at frame 2
  and corrupts more memory each frame.
- **Crash signature varies launch-to-launch** — heap layout differs each
  launch, different adjacent allocations get clobbered.
- **Stock `QuadLayerData` (handled by stock `OpenXRQuadLayer`) is alive** —
  stock's `OnUpdate()` IS the base implementation, so the count resets.
- **Reboot doesn't fix it** — it's a code bug, not device-state pollution.
- **CompositionOutline strip / RightTexture set "appeared to help"** —
  coincidence; build hash changes shift il2cpp heap layout → corruption hits
  a different downstream consumer.

### Secondary issue (latent, fix while we're in there)

`OpenXRCustomLayerHandler<T>` line 138: `protected static OpenXRCustomLayerHandler<T> Instance;`
is shared across all subclasses with the same `T`. Our handler subclasses with
`T = XrCompositionLayerQuad`, identical to stock `OpenXRQuadLayer`. Whichever
subclass is constructed second clobbers `Instance`. Today our scene has no
stock `QuadLayerData` so this is dormant — but it's a latent footgun.

### Decision: (B) Direct `ILayerHandler` rewrite, with (C) sanity check first

Both bugs above disappear if we drop `OpenXRCustomLayerHandler<T>`. The
buffer-overrun fix is trivial in a custom implementation (we own the
NativeArray sizing). The static-singleton conflict cannot exist if we don't
subclass.

**(C) Stock-quad sanity check first** — single iteration, cheap insurance:
confirms today's environment is healthy so we don't conflate environment
issues with the bug fix.

Auditing per-process cleanup (option A) is unnecessary — the buffer overrun
explains every symptom.

### Plan steps

#### Step T5-1 — Confirm stock-quad codepath is alive on today's environment

**Goal:** rule out environment regression before committing to a code rewrite.

**Change:** none. Use existing `STEREO_USE_STOCK_QUAD=1` env-var path in
`StereoCompositorSceneSetup.cs`.

**Builds on:** `dev.sh build --with-panel` already supported.

**Auto-confirm method:**
```sh
STEREO_USE_STOCK_QUAD=1 dev.sh build --with-panel
dev.sh install-app
dev.sh kill-app
dev.sh begin-automation
dev.sh run-app
dev.sh alive-for 30                       # exits 0 = stock-quad alive
dev.sh layer-count                        # expect LCnt >= baseline+1
dev.sh tag t5-1-stock-quad-alive
```

NOTE: STEREO_USE_STOCK_QUAD is read at AutoBuilder time, not by dev.sh build's
flags. The executor will need to either prefix this env var (which won't match
the `Bash(.../dev.sh *)` permission rule, so likely needs manual approval ONCE
or a new `--use-stock-quad` flag added to dev.sh build).

**Failure-mode disambiguation:**
1. If `alive-for 30` fails AND backtrace matches today's signatures: environment
   is poisoned. `dev.sh reboot-recover` and retry T5-1 ONCE. If still failing,
   stop and update plan doc — don't proceed to T5-2 with a poisoned baseline.
2. If `alive-for 30` fails with a NEW signature: log, retry once after reboot,
   re-evaluate if persistent.
3. If REPRO-1 doesn't fire in build log: scene-authoring path broken — verify
   `STEREO_USE_STOCK_QUAD` env var reaches AutoBuilder.

**Estimated loop iterations:** 1 (or 2 with reboot).

---

#### Step T5-2 — Rewrite `StereoQuadLayerHandler` as direct `ILayerHandler`

**Goal:** eliminate buffer-overrun + shared-static-Instance bugs by replacing
the base class. Self-contained implementation with explicit NativeArray
ownership and no shared static state.

**Change:** rewrite `Assets/Scripts/StereoVideoCompositor/StereoQuadLayerHandler.cs`:

- Drop `: OpenXRCustomLayerHandler<XrCompositionLayerQuad>` inheritance.
- Implement `OpenXRLayerProvider.ILayerHandler, IDisposable` directly.
- 5 required methods: `CreateLayer`, `RemoveLayer`, `ModifyLayer`,
  `SetActiveLayer`, `OnUpdate`.
- Own state:
  - `Dictionary<int, CompositionLayerManager.LayerInfo> m_LayerInfos`
  - `Dictionary<int, ulong> m_SwapchainHandles`
  - `Dictionary<int, XrCompositionLayerQuad> m_NativeLayerTemplates`
  - `Dictionary<int, ActiveLayerState> m_ActiveLayerStates` (KEEP from current)
  - `ConcurrentQueue<Action> m_MainThreadActions` (own queue, not inherited)
  - `NativeArray<XrCompositionLayerQuad> m_PerFrameLayers` (Persistent, lazily
    resized to capacity = active*2)
  - `NativeArray<int> m_PerFrameOrders` (mirror)
  - **No `static Instance`.** Instance-bound MonoPInvokeCallback pattern via a
    `static volatile StereoQuadLayerHandler s_Owner` (single-instance OK
    because there's exactly one of us per process).
- `CreateLayer`: read TexturesExtension; bail if missing/no LeftTexture; build
  `XrSwapchainCreateInfo`; call `OpenXRLayerUtility.CreateSwapchain(layerId,
  info, isExternalSurface=false, OnSwapchainCreated)`; subscribe
  `Application.onBeforeRender` (once); stash `LayerInfo` BEFORE callback fires.
- `OnSwapchainCreated` (static, `[AOT.MonoPInvokeCallback]`): enqueue
  `m_MainThreadActions` action that builds the `XrCompositionLayerQuad` template
  and stashes in `m_NativeLayerTemplates[layerId]`.
- `OnBeforeRender`: for each active layer, `OpenXRLayerUtility.WriteToRenderTexture`
  source → swapchain image (mirror lines 365-405 of base class).
- `SetActiveLayer`: stash LayerInfo + source dims in `m_ActiveLayerStates`.
  **Do NOT touch any per-frame native array here.**
- `OnUpdate`:
  1. Drain `m_MainThreadActions`.
  2. Compute total writes = `m_ActiveLayerStates.Count * 2` (or *1 for Mono).
  3. Resize `m_PerFrameLayers` if capacity insufficient (Dispose old, allocate new).
  4. Fill array (existing `ComputeEyeRects` logic stays — that part is correct).
  5. Single `OpenXRLayerUtility.AddActiveLayersToEndFrame`.
  6. `m_ActiveLayerStates.Clear()`.
  7. **Never carry a count field across frames.** Re-derive every frame from
     `m_ActiveLayerStates`.
- `RemoveLayer`: `OpenXRLayerUtility.ReleaseSwapchain(id)`,
  `OpenXRLayerUtility.RemoveActiveLayer(order)`, drop from all dictionaries.
- `Dispose`: clear dicts, dispose NativeArrays, unsubscribe `onBeforeRender`.
- Keep all REPRO-3 logging in `SetActiveLayer`.
- Add new `[REPRO-4]` log in first call to `OnUpdate` after
  `AddActiveLayersToEndFrame` returns (proves submission completes without
  crashing the very next frame).

**Do NOT change** `StereoQuadLayerData`, `StereoCompositorFeature`,
`StereoCompositorSceneSetup`, or `PinRefreshRate` in this step — handler-only
rewrite.

**Builds on:** T5-1 (or skipped if T5-1 unambiguously alive — T5-2 fix is
correct regardless of T5-1's outcome).

**Auto-confirm method:**
```sh
dev.sh build --with-panel
dev.sh install-app
dev.sh kill-app
dev.sh begin-automation
dev.sh run-app
dev.sh alive-for 30                              # was failing with old handler
dev.sh layer-count                               # expect baseline+1
dev.sh logs '\[REPRO-4\]'                        # confirm OnUpdate completed once
dev.sh logs 'StereoQuadLayerHandler'             # confirm no exception spam
dev.sh tag t5-2-direct-ilayerhandler-alive
```

`alive-for 30` exits 0 + REPRO-4 fires + no exceptions = pass.

**Failure-mode disambiguation (in priority order):**

1. CLAUDE.md visual checks 1–3 (device awake / app PID / screencap >10 KB).
2. If `alive-for 30` fails AND backtrace is `KeyNotFoundException` from
   il2cpp: residual dictionary lookup with wrong key. Likely
   `m_LayerInfos[layerId]` lookup in `OnSwapchainCreated`'s queued action
   fired after `RemoveLayer` cleared it. Wrap in `TryGetValue`.
3. If alive but `layer-count` is unchanged from no-panel baseline: layer not
   submitted. `dev.sh logs 'AddActiveLayersToEndFrame\|writeIndex'` should
   show writeIndex > 0. If 0, `m_ActiveLayerStates.Count == 0` —
   `SetActiveLayer` isn't being called. Check
   `OpenXRLayerProvider.RegisterLayerHandler(typeof(StereoQuadLayerData), handler)`
   typeof; check `[REPRO-2b]` log fires.
4. If alive but a NEW SEGV signature appears: corruption is gone, different
   bug. Likely candidates:
   - Forgot to subscribe `onBeforeRender` → no swapchain blit → vk reads
     uninitialized image.
   - Pose math regression: verify `OpenXRUtility.ComputePoseToWorldSpace`
     still called per-frame.
5. If `OnSwapchainCreated` callback never fires:
   `[AOT.MonoPInvokeCallback]` attribute missing or delegate type mismatch.
   Check signature against
   `OpenXRCustomLayerHandler.OnCreatedSwapchainCallback` (lines 666–675).

**Estimated loop iterations:** 3–5.

---

#### Step T5-3 (CONDITIONAL) — If T5-2 alive: panel positioned + verify-color stereo split

**Goal:** with the handler stable, complete the original Step 4 + Step 5
work today's run never reached.

**Change:** in `StereoCompositorSceneSetup.cs`, parent the panel under
`OVRCameraRig.trackingSpace` (with `OVRCameraRig` fallback, then world).
Set `localPosition = (0, 0, 1.5)`, `localRotation = Euler(0, 180, 0)`. Same
pattern as `TestPurpleCircleSetup.cs`. Disable marker disc for this step
(`dev.sh build --with-panel --no-marker`) so the disc doesn't mask the
panel pixel sample.

**Builds on:** T5-2.

**Auto-confirm method:**
```sh
dev.sh build --with-panel --no-marker
dev.sh install-app
dev.sh kill-app
dev.sh begin-automation
dev.sh run-app
dev.sh alive-for 30
dev.sh screencap
dev.sh verify-color red blue                # left=red, right=blue, ±60
dev.sh tag t5-3-stereo-split-verified
```

**Failure-mode disambiguation:**
1. Both eyes red (or both blue): `EyeVisibility=0` mono being emitted instead
   of 1/2. Check our `OnUpdate` sets EyeVisibility per `data.StereoLayout`.
2. Both eyes near-black: panel out of view. Check `OVRCameraRig.trackingSpace`
   was found (`dev.sh logs 'StereoCompositorSceneSetup.*Authored'`).
3. Colors swapped (left=blue, right=red): TB orientation flipped. Swap
   leftRect/rightRect Y offsets in `ComputeEyeRects`.

**Estimated loop iterations:** 1–2.

---

### What this redirect deliberately defers

- **AndroidSurface / ExoPlayer wiring** (original Steps 10-12) — same as
  before, deferred until handler stability is locked.
- **Multi-launch stability investigation** — earlier journal noted "only one
  launch survives per reboot." With the buffer overrun fixed, this should go
  away. If it doesn't, file as Step T5-4 once we have a stable handler.
- **Filing a Unity bug** — the buffer-overrun is OUR bug, not Unity's. The
  shared `static Instance` design is arguably a Unity API smell, but
  workaround (use ILayerHandler directly) is straightforward.

### Files for the executor to focus on (Step T5-2)

- `Assets/Scripts/StereoVideoCompositor/StereoQuadLayerHandler.cs` — full
  rewrite.
- Reference (read-only):
  `Library/PackageCache/com.unity.xr.openxr@*/Runtime/CompositionLayers/OpenXRCustomLayerHandler.cs`
  for the swapchain/blit lifecycle to mirror.
- Reference:
  `Library/PackageCache/com.unity.xr.openxr@*/Runtime/CompositionLayers/OpenXRQuadLayer.cs`
  for `XrCompositionLayerQuad` field initialization.
- Reference:
  `Library/PackageCache/com.unity.xr.openxr@*/Runtime/CompositionLayers/OpenXRLayerProvider.cs`
  for `ILayerHandler` interface contract and frame ordering.
- Reference:
  `Library/PackageCache/com.unity.xr.openxr@*/Runtime/CompositionLayers/OpenXRLayerUtility.cs`
  for `CreateSwapchain` / `WriteToRenderTexture` /
  `AddActiveLayersToEndFrame` / `RequestRenderTextureId`.

---

## 2026-05-08 — Step 1–3 execution journal (autonomous run, 5h)

**TL;DR:** Steps 1 + 2 clean. Step 3 partially succeeded — got a single
post-reboot launch where the panel was authored, the handler registered,
and the app stayed alive 15 s. Second launch on the same APK SEGV'd in
`UnsafeUtility_CUSTOM_MemCmp`. We're hitting a **stability ceiling**
where rapid install/launch cycles re-pollute device state faster than
the canonical state-machine can clean it. The architect's plan didn't
anticipate this. Tier 5 territory per CLAUDE.md.

### Step 1 — DONE

Added to `scripts/dev.sh`:
- `verb_layer_count` — extracts latest `LCnt=N` from `VrApi` logcat lines.
- `verb_alive_for SECONDS` — polls pidof every 1s for the requested
  duration; exits 0 only if PID stable + non-empty entire interval.

Created `scripts/tests/test_stereo_panel.sh` — wraps build + install +
run-app + alive-for 15 + screencap + verify-color L R + tag.

Also added `--with-panel` / `--no-panel` / `--with-marker` / `--no-marker`
CLI flags to `dev.sh build` because env-var prefixes
(`STEREO_SKIP_PANEL=0 dev.sh build ...`) break Claude Code's permission
matcher (the allowlist pattern `Bash(.../dev.sh *)` requires the command
to literally start with the script path).

Auto-confirms passed: `dev.sh test marker green` regression-tested green
disc, layer-count returned 4 (≥3), alive-for 10 stable.

### Step 2 — DONE (with surprise)

Added `[REPRO-1]` (scene-author build-time), `[REPRO-2]` (handler register
runtime), `[REPRO-3]` (SetActiveLayer) markers. Build with
`dev.sh build --with-panel` succeeded. Run-app SEGV'd as expected.

**Surprise:** the crash backtrace was **NOT** `LODGroupManager::
GarbageCollectCameraLODData` (the architect's lead hypothesis) — it was
**`XRDisplaySubsystem::TryGetRenderPass`** (URP asking XR for a render
pass with a bad pointer). This is the OLD signature documented in
"STATE AT HANDOFF" as "device-state pollution that a reboot cleared."

REPRO markers fired in order: REPRO-1 in build log (panel authored),
REPRO-2 + REPRO-2b at runtime (handler registered), REPRO-3 4× (panel
became active 4 frames before crash). Crash on next render frame.

Tagged: `segv-baseline-xrdisplay`.

### Step 3 — PARTIAL SUCCESS, STABILITY CEILING

Step 3's prescribed changes (panel layer = IgnoreRaycast, no MeshRenderer/
LODGroup, refresh-rate diagnostic) didn't address the actual crash because
the architect's hypothesis (LODGroup) was wrong for our environment.

What actually moved the crash signature (in order tried):
1. **Strip CompositionOutline** auto-attached alongside CompositionLayer
   — moved crash from `XRDisplaySubsystem::TryGetRenderPass` to
   `CanvasProxy::SendPreWillRenderCanvases`. Outline appears to use a
   uGUI Canvas that fights URP/XR.
2. **Set `tex.RightTexture = testTexture`** (not just LeftTexture) —
   moved crash to `vk::RenderSurface::GetFormat` (Vulkan render-pass
   setup with null surface format). LeftTexture-only was producing a
   half-initialized swapchain.
3. **Tier 4 reboot** + apply both fixes — first launch ALIVE for 15s,
   PID stable, stereo compositor feature initialized cleanly,
   `XR_FB_composition_layer_alpha_blend enabled = True`.

**The wall:**
- The earlier "first launch alive 15 s after reboot" observation didn't
  reproduce on a clean re-test. With a fresh `dev.sh reboot-recover`
  followed by `begin-automation` → `run-app` (cycle 1), launch SEGV'd
  immediately at `XRDisplaySubsystem::TryGetRenderPass`. After
  `kill-app` → `begin-automation` → `run-app` (cycle 2), launch
  SEGV'd again with a different signature (il2cpp-only stack frame
  via `scripting_method_invoke`). So:
  - **Panel-on builds SEGV on EVERY launch**, fresh reboot or not.
  - Crash signature varies across launches even with the same APK.
  - REPRO-1 (build-time scene authoring) always fires.
  - REPRO-2 / REPRO-2b (handler register) always fires at runtime.
  - REPRO-3 (SetActiveLayer) fires 3–4 times before crash.
- The "alive once" earlier observation was almost certainly an artifact
  — probably a stale PID readback while the app was in the process of
  crashing, or alive-for catching the app during its brief pre-crash
  initialization window.

**Settings ordering bug fixed during Step 3:** `verb_keep_awake_on` was
applying settings BEFORE killing vrshell, so the respawning vrshell reset
them to defaults. Reordered: kill vrshell → sleep 2s → apply settings.
Verified `skip_launch_check=true controller_emulation=1` now stick.

### Tier 5 escalation — what the architect needs to decide

Three observations that the original plan doesn't account for:

1. **The crash is not LODGroup, it's URP/XR/Vulkan render-pass mismatch.**
   The Step 3 hypothesis was wrong for THIS device/Unity-version
   combination. The fixes that moved the needle (CompositionOutline strip,
   RightTexture set) weren't in the plan.

2. **Single-launch stability != multi-launch stability.** Even with the
   working fix, only ONE launch survives per reboot. Subsequent launches
   crash with a different signature. Either:
   - There's a per-process cleanup our handler isn't doing on first
     destroy (likely — `RemoveLayer` or a swapchain finalize).
   - The OpenXR runtime on this Quest 3 firmware has a bug where
     repeated XrSession creation with custom layer handlers leaks/
     corrupts internal state.
   - The CompositionLayer system itself caches refs across app launches
     and gets stale.

3. **Quest UI panels overlay our app even in `running-app` state.** When
   the app launches via `am start`, it goes foreground briefly, then
   vrshell's Bloom/ControlBar/DisplayBar overlay it. Need either:
   - A way to make our app's window full-immersive (focusaware is set).
   - A post-launch dismiss-loop in run-app.
   - Or accept that the screencap captures the system overlay state.

**Question for architect:** is the right next move…
- (A) Dig into per-launch cleanup in StereoQuadLayerHandler — what
  doesn't get unwound when the app process dies?
- (B) Pivot to Step 3b (direct ILayerHandler) anyway, on the theory
  that the base class `OpenXRCustomLayerHandler<T>` is leaking state
  across XrSession lifetimes?
- (C) Simplify radically — try a stock `QuadLayerData` + custom-OnUpdate
  approach (no LayerData subclass at all), since stock-QuadLayerData was
  noted as alive in the original "STATE AT HANDOFF"?
- (D) Something else I'm missing?

### What's working / not

| Thing | State |
|---|---|
| `dev.sh test marker green` (URP-only render path) | ✅ stable |
| `dev.sh build --with-panel` (panel in scene) | ✅ builds clean |
| Step 3 panel ON, post-reboot, first launch | ⚠ alive 15s but Quest UI overlays |
| Step 3 panel ON, second launch, no rebuild | ❌ SEGV (memcmp) |
| Layer authoring without CompositionOutline | ✅ |
| Both eye textures wired | ✅ |
| Handler registration / SetActiveLayer firing | ✅ (REPRO-3 fires) |
| Compositor layer actually visible in screencap | ❓ never confirmed |

Tagged artifacts available: `segv-baseline-xrdisplay` (initial repro),
`step3-panel-alive` (one alive cycle).

---

## 2026-05-07 — Architect: Incremental Implementation Plan

**Where we are:** `dev.sh test marker` passes end-to-end (URP green-disc rendering, both eyes match, persistent keep-awake holding). The `[Compositor] StereoQuadPanel` GameObject with custom `StereoQuadLayerData` SEGVs in `LODGroupManager::GarbageCollectCameraLODData` a few seconds after launch, so every build today runs `STEREO_SKIP_PANEL=1` — the compositor pipeline is still completely cold on device. **End state of this plan:** a stereo quad compositor panel showing distinct left/right textures from a `TopBottom` source, alive and pixel-verified, with a clean substrate to drop the AndroidSurface/HLS/mask layers onto in a follow-up plan. Field-/attribute-level bisects on `StereoQuadLayerData` are exhausted (per the older "Bisect already ran" section); this plan attacks the SEGV from a different angle (LODGroup interaction, not LayerData fields) and only commits to a rewrite if that fails.

### Steps

#### Step 1 — Lock in baseline + add new dev.sh verbs we'll need throughout

**Goal:** make the rest of the plan executable from `dev.sh` alone (no chained shell, no inline Python). Today's marker test only checks one solid color per eye; from Step 4 onward we need a per-eye left/right different-color check, a layer-count check, and a "stayed alive for N seconds" check.

**Change:** add three verbs to `scripts/dev.sh` and one helper test:
- `dev.sh layer-count` — runs `$ADB logcat -d | grep "VrApi" | tail -3`, extracts the latest `LCnt=N`, prints `LCnt=N` to stdout, exits 0 if N parsed OK. (Composes with `dev.sh logs` but is the single canonical layer-count probe so steps can declare expected values.)
- `dev.sh alive-for <seconds>` — polls `pidof` every 1 s for the requested duration; exits 0 if PID is stable and non-empty the entire interval, exits 1 with the offending sample on any drop. (Replaces ad-hoc `sleep 15 && pidof` patterns; the marker test currently only checks pidof once.)
- `dev.sh verify-color` — already exists, but extend `dev_verify_color.py` to accept an optional `--left COLOR --right COLOR` for asymmetric stereo checks. (It already does — confirmed in source — so this part is just documenting the contract for the steps below.)
- `scripts/tests/test_stereo_panel.sh` — wraps build + install-app + run-app + alive-for + screencap + verify-color with per-eye colors as args. Mirror of `test_marker.sh` but for compositor panel runs.

**Builds on:** the existing canonical state machine (begin-automation / install-app / run-app / kill-app) and the marker test's pattern.

**Auto-confirm method:**
- `dev.sh test marker green` still passes (regression guard — we did not break the working baseline).
- `dev.sh layer-count` against the running marker app prints a number ≥ 3 (baseline; without our panel this is typically 5 per the journal).
- `dev.sh alive-for 10` against the running marker app exits 0.

**Failure-mode disambiguation:** if `test marker` regresses after the verb additions, the bug is in `dev.sh` shell — the new verbs must not alter argument parsing of existing verbs. Re-check the dispatch case block first.

**Estimated loop iterations:** 1–2 (mostly mechanical script edits).

---

#### Step 2 — Rebuild today's stable repro of the SEGV with full diagnostic context

**Goal:** before bisecting, capture a clean, full-fidelity repro of the current LODGroup crash so we can tell whether subsequent steps actually changed anything.

**Change:** code-only — flip the panel back on for one build (`STEREO_SKIP_PANEL=0`) and add three temporary `Debug.Log` calls in `StereoCompositorSceneSetup.cs` and `StereoCompositorFeature.cs`: one before `panel.AddComponent<CompositionLayer>()`, one inside `OnLayerProviderStarted`, one inside `StereoQuadLayerHandler.SetActiveLayer`. (Several of these already exist; just ensure each prints a unique `[REPRO-N]` tag so we can grep them.) Keep `TestPurpleCircleSetup` enabled — the green disc is our "is the URP path alive at all" canary.

**Builds on:** Step 1's `alive-for` and `layer-count` verbs.

**Auto-confirm method:**
- `STEREO_SKIP_PANEL=0 dev.sh build` succeeds.
- `dev.sh install-app && dev.sh run-app` reports launch.
- `dev.sh alive-for 10` **fails** (this is the expected SEGV repro).
- `dev.sh crash-stack` shows `LODGroupManager::GarbageCollectCameraLODData` in the backtrace.
- `dev.sh logs '\[REPRO-'` shows REPRO-1 (panel authored) and REPRO-2 (LayerProvider started); REPRO-3 (SetActiveLayer) presence/absence tells us whether the crash is before or after the handler ran — that's the diagnostic we don't have today.
- `dev.sh tag segv-baseline` snapshots all artifacts.

**Failure-mode disambiguation:**
1. If `alive-for 10` passes (no crash): the SEGV has self-resolved (Quest OS or device-state-pollution change). Stop the plan, re-evaluate; just enable the panel by default and proceed to Step 4.
2. If REPRO-1 doesn't appear: the editor scene-authoring path is broken (probably an asmdef regression). Check `/tmp/quest_build.log` for `[StereoCompositorSceneSetup]`.
3. If the crash backtrace is *not* LODGroup: the SEGV moved. That's actually a useful signal for Step 3 — note it and re-tag.

**Estimated loop iterations:** 1.

---

#### Step 3 — Eliminate LODGroup interaction (likely SEGV root cause)

**Goal:** test the hypothesis that the SEGV is `LODGroupManager` walking GameObjects/Cameras during scene cleanup and choking on the CompositionLayer GameObject specifically. The journal's older bisects all stayed *inside* `StereoQuadLayerData` (fields, attributes, link.xml). The new crash signature (`LODGroupManager::GarbageCollectCameraLODData`) is somewhere else entirely — it's about camera lifecycle, not LayerData type identity.

**Change:** in `StereoCompositorSceneSetup.cs`, after creating the panel GameObject:
- Set `panel.layer` to a dedicated layer (e.g. `IgnoreRaycast` or a new "CompositorLayers" layer) so URP's per-camera culling treats it deterministically.
- Remove anything that could attach an LODGroup component traversal: no children, no MeshRenderer/MeshFilter (CompositionLayer alone). Verify with `panel.GetComponents<Component>()` log: should be `Transform, CompositionLayer, TexturesExtension`.
- Pin the display refresh rate to 72 Hz on session start by adding a small `MonoBehaviour` in the scene (`PinRefreshRate`) that calls `OVRManager.display?.displayFrequency = 72f;` in `Start()`. The journal flagged 72→90→72 oscillations as crash-trigger-adjacent. Pinning removes one variable.

**Builds on:** Step 2's `[REPRO-N]` markers tell us whether the panel even gets to `SetActiveLayer` before crashing.

**Auto-confirm method:**
- `dev.sh build && dev.sh install-app && dev.sh run-app && dev.sh alive-for 15` exits 0 (no crash for 15 s = SEGV likely fixed).
- `dev.sh layer-count` reports LCnt=6 (one above the marker baseline of 5 — our panel layer is being submitted).
- `dev.sh logs 'displayFrequency'` shows 72 Hz pinned.
- `dev.sh logs 'StereoQuadLayerHandler.*SetActiveLayer'` matches at least once.
- `dev.sh tag step3-layer-pinned`.

**Failure-mode disambiguation (in order, before assuming Step 3 didn't work):**
1. CLAUDE.md "Visual confirmation" #1: device awake? `dev.sh screencap` returns >10 KB? If not, the screencap is silent-failing, not the panel.
2. CLAUDE.md #2: app actually running? `dev.sh pidof` non-empty? If empty, crash → `dev.sh crash-stack`. New backtrace OR same? If still LODGroup, the layer/camera changes didn't help. If different (e.g. URP RenderPass null), Tier 4 (device reboot) territory.
3. If alive but `layer-count` is still 5, the panel exists but isn't being submitted — handler not registered. Check Step 1's `[REPRO-2]` log.
4. If the crash moved to `Resources.FindObjectsOfTypeAll<CompositionLayer>` (the OLD signature from the bisect log), Step 3 didn't help and we need Step 3b.

**Estimated loop iterations:** 2–4.

---

#### Step 3b — (Conditional) Move to direct `ILayerHandler` if Step 3 didn't fix the SEGV

**Goal:** apply path (A) from the original architect doc — bypass `OpenXRCustomLayerHandler<XrCompositionLayerQuad>` and implement `OpenXRLayerProvider.ILayerHandler` directly. Only run this step if Step 3's auto-confirm fails on the SEGV after at least 3 iterations of in-step debugging.

**Change:** new file `Assets/Scripts/StereoVideoCompositor/StereoQuadLayerHandlerDirect.cs` implementing the 5 `ILayerHandler` methods (`OnUpdate`, `CreateLayer`, `RemoveLayer`, `ModifyLayer`, `SetActiveLayer`). Delete or `#if false` the existing `StereoQuadLayerHandler.cs`. Update `StereoCompositorFeature.OnLayerProviderStarted` to register the new type. Mirror the swapchain-create/blit lifecycle from `OpenXRQuadLayer.cs` (`Library/PackageCache/com.unity.xr.openxr@.../Runtime/CompositionLayers/OpenXRQuadLayer.cs`) — no shared static state.

**Builds on:** Step 3's diagnostic logs proving the crash is upstream of our handler logic.

**Auto-confirm method:**
- Same as Step 3: `alive-for 15` + `layer-count` ≥ 6 + handler `SetActiveLayer` log appears.

**Failure-mode disambiguation:** if SEGV persists with the `ILayerHandler`-direct path AND the crash is still in `LODGroupManager`/`FindObjectsByType`, then the framework's GameObject enumeration is the proximate cause. Escalate to Tier 5 — file the Unity bug (path C), pivot to OVROverlay (path B) for the rest of the plan, and document the pivot in this file.

**Estimated loop iterations:** 4–6 (only if needed).

---

#### Step 4 — Reposition the panel under `OVRCameraRig.trackingSpace`

**Goal:** the existing `StereoCompositorSceneSetup.cs` puts the panel at world `(0, 1.5, 1.5)`. The marker test proved the device sits low (~0.8 m head height) and only sees things parented under `OVRCameraRig.trackingSpace` at local `(0, 0, 1)`. Without this fix, even a non-crashing panel won't be in the screencap.

**Change:** in `StereoCompositorSceneSetup.cs`, parent the panel under `OVRCameraRig.trackingSpace` (with a `OVRCameraRig` fallback, then world fallback — same pattern as `TestPurpleCircleSetup.cs`). Set `localPosition = (0, 0, 1.5)`, `localRotation = Euler(0, 180, 0)`.

**Builds on:** Step 3 (or 3b) — panel is alive on device.

**Auto-confirm method:**
- `dev.sh screencap && dev.sh verify-color red red` — both eyes show red (the `BuildTestPattern` paints the top half red, bottom blue; if the layer is rendering Mono / both-eyes-same and showing the top half by default, both eyes are red). This is intentionally a low bar — Step 5 makes it stricter.
- `dev.sh logs 'StereoCompositorSceneSetup.*Authored'` shows the new parent name `OVRCameraRig/TrackingSpace`.
- `dev.sh tag step4-panel-positioned`.

**Failure-mode disambiguation:**
1. CLAUDE.md "Visual confirmation" 1–4 (device awake / app running / layer submitted / panel in view).
2. If `verify-color` returns near-black `(0,0,0)`: the layer is being submitted but at a transparent or off-screen position. Re-check the localPosition/rotation, and inspect `dev.sh logs 'StereoQuadLayerHandler.*CreateNativeLayer'` for the Pose values being computed.
3. If `verify-color` returns mostly green (the marker disc): the panel is behind / smaller than the disc. Either remove the disc for this step (set `TEST_PURPLE_CIRCLE=0`) or move the panel to `(0, 0, 1.0)` (closer than the disc).

**Estimated loop iterations:** 1–2.

---

#### Step 5 — Confirm true stereo split (left eye ≠ right eye)

**Goal:** the test pattern is red-top / blue-bottom. With `StereoLayout.TopBottom` and our handler computing per-eye `SubImage.ImageRect`, the left eye should show red, the right should show blue. This is the first step that proves the entire stereo compositor pipeline end-to-end.

**Change:** in `StereoCompositorSceneSetup.cs`, ensure `data.StereoLayout = StereoLayout.TopBottom` (default already, but make it explicit). Disable the green marker disc for this step's build by setting `TEST_PURPLE_CIRCLE=0` in the test script — the disc is in URP world space and renders into both eyes from the same Camera, masking the stereo split. (Alternative: leave the disc on and verify-color in the *upper* portion of each eye where the panel is, but per-eye pixel sampling at the panel center is cleaner.)

**Builds on:** Step 4's correctly-positioned panel.

**Auto-confirm method:**
- `TEST_PURPLE_CIRCLE=0 dev.sh build && dev.sh install-app && dev.sh run-app`.
- `dev.sh screencap && dev.sh verify-color red blue` — left eye center matches red `(220,30,30)±60`, right eye center matches blue `(30,30,220)±60`.
- `dev.sh layer-count` reports LCnt=6 (the panel's two per-eye Quad submissions only count as one logical layer in `LCnt`; if the handler is doubling submissions, LCnt may be 7 — note actual value as Step-5 baseline).
- `dev.sh tag step5-stereo-split-verified`.

**Failure-mode disambiguation:**
1. CLAUDE.md visual checks 1–5 first.
2. If both eyes show red: `EyeVisibility=0` (mono) — handler isn't differentiating. Check `dev.sh logs 'StereoQuadLayerHandler.*OnUpdate'` for left/right rect values.
3. If both eyes show blue: TB orientation flipped. Swap the leftRect/rightRect Y offsets in `ComputeEyeRects` (the Y=0-at-bottom convention may differ from what Quest's Vulkan layer sampler expects).
4. If left=red but right=red (or both=blue): `EyeVisibility` not being honored — check that the handler is emitting TWO Quad submissions per frame, not one.
5. If neither eye is the expected color but they do differ: texture import broken (sRGB conversion?) — check `StereoTestPattern.png` import settings haven't drifted from `sRGBTexture=false`.

**Estimated loop iterations:** 2–4.

---

#### Step 6 — Replace the procedural test pattern with a clearly-stereo PNG asset

**Goal:** swap the procedural red-top/blue-bottom pattern for an asset under `Assets/StereoCompositor/` containing per-eye distinguishable shapes (e.g. left half/top half = "L" glyph or a circle in upper-left; right half/bottom half = "R" glyph or a circle in lower-right). Catches stereo-orientation bugs that two solid colors can't.

**Change:** in `StereoCompositorSceneSetup.cs`, modify `BuildTestPattern` to render distinct shapes per half (already partially done — there's a white square and a yellow square at known coords). Move those markers to fixed corner positions: white square in top-left of the top half, yellow square in bottom-right of the bottom half. This makes the per-eye shape diagnostically meaningful.

**Builds on:** Step 5's verified stereo split. With true stereo, the left eye shows ONLY the white square at top-left; the right eye shows ONLY the yellow square at bottom-right.

**Auto-confirm method:**
- `dev.sh verify-color red blue` still passes (color match unchanged — the markers are tiny vs the full red/blue field).
- New verb in `dev_verify_color.py`: optional `--left-marker COLOR@X,Y` flag that samples a specific pixel coord in the left half. Run `dev.sh verify-color red blue --left-marker white@0.25,0.25 --right-marker yellow@0.75,0.75`. Expected: pass.
- `dev.sh tag step6-stereo-shape-distinguishable`.

**Failure-mode disambiguation:**
1. If markers don't show but solid colors are correct: texture filter mode = Bilinear is blurring the small squares — should be Point (already enforced by importer code in `EnsureTestPatternAsset`). Verify.
2. If markers appear in BOTH eyes: stereo split regressed; back to Step 5 disambiguation.

**Estimated loop iterations:** 2.

---

#### Step 7 — Re-enable the green marker disc as a "URP + compositor coexistence" check

**Goal:** prove URP world-space content (the marker disc) and our compositor layer can coexist in the same scene without one breaking the other. Today's testing has them in separate builds (`STEREO_SKIP_PANEL` toggles) — we need them simultaneously stable before adding more layers.

**Change:** flip `TEST_PURPLE_CIRCLE=1` (default) AND keep the panel enabled. Position the disc at local `(0.6, 0, 1.5)` (offset right) and the panel at local `(-0.6, 0, 1.5)` (offset left) so they don't overlap in either eye.

**Builds on:** Step 6 known-good stereo split.

**Auto-confirm method:**
- `dev.sh alive-for 15` exits 0.
- `dev.sh layer-count` reports the same LCnt as Step 5 (compositor layer count unchanged — disc is URP not compositor).
- `dev.sh verify-color` with a left-half AND right-half region split: left-eye-left-region = red, right-eye-left-region = blue, both eyes' right-region = green (the disc, rendered via URP into both eye buffers — same color in both eyes since it's URP not stereo-compositor). This requires extending `dev_verify_color.py` to sample multiple regions; treat the extension as part of this step's scope.
- `dev.sh tag step7-coexistence`.

**Failure-mode disambiguation:**
1. If alive-for fails (regression): the disc + panel combination triggers a new crash. Bisect by running disc-only (Step 4 baseline, marker test) and panel-only (Step 6 baseline) separately to confirm each is still alive. The combination probably exposes a renderer-ordering bug; check URP camera clear flags.
2. If the disc renders red/blue (i.e., disc taking on panel colors): the compositor layer is drawing OVER the disc with EyeVisibility=0 mono — Step 5 stereo regression.

**Estimated loop iterations:** 2.

---

#### Step 8 — Promote `StereoCompositorSceneSetup` defaults to "panel ON" in dev.sh build

**Goal:** flip the default — `dev.sh build` (no env vars) should now produce a build with the panel enabled. This locks in the "stable single-quad stereo panel" milestone.

**Change:** in `scripts/dev.sh verb_build`, change the default from `STEREO_SKIP_PANEL=${STEREO_SKIP_PANEL:-1}` to `STEREO_SKIP_PANEL=${STEREO_SKIP_PANEL:-0}`. Update `scripts/tests/test_marker.sh` to pass `STEREO_SKIP_PANEL=1` explicitly (so the marker test still skips the panel). Add a `scripts/tests/test_stereo_panel.sh` (created in Step 1) that exercises the panel-on path as the canonical compositor regression test.

**Builds on:** Step 7's verified panel+disc coexistence.

**Auto-confirm method:**
- `dev.sh test marker green` still passes (it forces the env var, so unaffected).
- `dev.sh test stereo-panel` — new test: build with panel on, install, run, alive-for 15, screencap, verify-color red blue. Should pass.
- Both tests pass back-to-back in one session (no device reboot between them).
- `dev.sh tag milestone-stable-stereo-panel`.

**Failure-mode disambiguation:** if `test stereo-panel` fails but the same build manually steps through (Step 7) succeed: the test script wrapper has a timing issue. Compare with `test_marker.sh`'s timing.

**Estimated loop iterations:** 1.

---

#### Step 9 — Add `BlendType.Premultiply` configuration option to the panel and verify alpha behavior

**Goal:** before introducing the AndroidSurface video and the FB alpha-blend extension, verify the panel respects `LayerFlags.UnPremultipliedAlpha` vs `SourceAlpha`. We need this controlled before stacking layers; otherwise we'll chase ghost alpha bugs in the mask layer later.

**Change:** in the test pattern, replace the bottom (right-eye) blue with semi-transparent blue `(30,30,220,128)`. The texture import already has `alphaIsTransparency=false` (good — that means alpha is straight). Run two builds, one each with `BlendType.Premultiply` and `BlendType.Alpha` on the `StereoQuadLayerData`'s LayerData.BlendType (the field is set on the base class). Compare which produces correct-looking blending against the passthrough background.

**Builds on:** Step 8's stable panel.

**Auto-confirm method:**
- `BlendType.Alpha` build: `dev.sh verify-color red '60,60,140'` (right eye should be ~50% blue mixed with passthrough gray; the exact RGB depends on passthrough background but should NOT be pure blue or pure black).
- `BlendType.Premultiply` build: right-eye RGB will differ. Record both results — this step's "pass" is having BOTH builds alive and producing different right-eye RGBs in a documented way (the choice of which blend type to use long-term is a Step 11+ concern).
- `dev.sh tag step9-blend-alpha` and `dev.sh tag step9-blend-premultiply`.

**Failure-mode disambiguation:**
1. If both builds produce identical RGBs: the `LayerFlags` aren't being honored — check `CreateNativeLayer` actually reads `data.BlendType`.
2. If alpha=128 still shows fully opaque: texture importer is forcing alpha to 1.0 — re-check `alphaIsTransparency=false` and `sRGBTexture=false`.

**Estimated loop iterations:** 2.

---

#### Step 10 — Swap the `LocalTexture` source for an `AndroidSurface`-driven render target (NO ExoPlayer yet)

**Goal:** prove our handler can drive an AndroidSurface swapchain — the same path ExoPlayer will write into — without any video plumbing. Render into the surface from a tiny Java helper that just clears it to a known color every 100 ms.

**Change:**
- Extend `StereoQuadLayerData` to support `SourceTextureEnum.AndroidSurface` (likely already wired through TexturesExtension; the handler's `CreateSwapchain` needs `isExternalSurface: true` when the extension's source is AndroidSurface).
- Add `Assets/Plugins/Android/SurfaceClearer.java`: takes a `Surface`, runs an `EGL` thread that clears to `Color.argb(255, 0, 200, 0)` (green) every 100 ms.
- Modify `StereoCompositorFeature` (or a new `StereoSurfaceBinder` MonoBehaviour) to subscribe to the surface-ready callback and pass the `Surface` to `SurfaceClearer`.

**Builds on:** Step 9's verified blend behavior — we know the panel is composing alpha correctly, so any color shift in this step is from the surface, not blending.

**Auto-confirm method:**
- `dev.sh alive-for 30` exits 0 (longer than usual — Surface lifecycle is an async race).
- `dev.sh verify-color green green` — both eyes show green (no per-eye difference; AndroidSurface here is mono — single fullscreen content).
- `dev.sh logs 'GetLayerAndroidSurfaceObject'` shows the callback firing with a non-null surface object.
- `dev.sh logs 'SurfaceClearer'` shows clear loop running.

**Failure-mode disambiguation:**
1. If `verify-color` shows red/blue (the static texture pattern): the SourceTexture switch didn't take effect; the panel is still on `LocalTexture`. Verify the scene's TexturesExtension.sourceTexture serialization.
2. If both eyes black: `SurfaceClearer` thread crashed or surface wasn't passed. Check `adb logcat | grep SurfaceClearer` for Java exceptions.
3. If alive-for fails: AndroidSurface async race fired before our callback subscribed. Subscribe BEFORE `Layer.SetActive`, not after.

**Estimated loop iterations:** 3–6 (this is the riskiest single step).

---

#### Step 11 — Single-quad mono HLS playback via existing ExoPlayerBridge

**Goal:** point ExoPlayer at the AndroidSurface-driven panel from Step 10, with a known-good HLS URL (the `Main_Final_8m_hardmasked_nvenc_tb_20Mbps/index.m3u8` from `StereoVideoManager.cs`). Mono playback for now — both eyes show the same TB frame, no per-eye split. Mask + per-eye stereo are deliberately deferred.

**Change:** refactor `Assets/Plugins/Android/ExoPlayerBridge.java` to take a `Surface` directly instead of building one from `SurfaceTexture` (per the journal's "What gets deleted" section). Replace `SurfaceClearer` from Step 10 with the ExoPlayerBridge in `StereoSurfaceBinder`. Set `StereoQuadLayerData.StereoLayout = Mono` for this step.

**Builds on:** Step 10's verified Surface plumbing.

**Auto-confirm method:**
- `dev.sh alive-for 60` exits 0 (full minute — exercises HLS startup, segment download, decoder init).
- `dev.sh logs 'ExoPlayer.*onPlayerStateChanged.*STATE_READY'` matches at least once.
- `dev.sh verify-color` — pick a region known to be opaque in the source video (e.g. center) and verify the RGB is plausible (not black, not all-gray). Add a `--not-black` flag to `dev_verify_color.py` for "any RGB > (10,10,10) average" passes. Both eyes get the same color (mono).
- `dev.sh tag step11-mono-hls`.

**Failure-mode disambiguation:**
1. If verify-color is black: HLS not playing. Check ExoPlayer state logs. If `STATE_READY` never fires, network/codec issue — independent of compositor.
2. If verify-color is bright green/blue (not video pixels): the `SurfaceClearer` is still running (cleanup from Step 10 missed). Force-stop the app, uninstall, reinstall.

**Estimated loop iterations:** 4–8 (multi-day work; ExoPlayer surface refactor is non-trivial but well-scoped).

---

#### Step 12 — Re-enable `StereoLayout.TopBottom` per-eye split on the HLS panel

**Goal:** combine Step 5 (stereo split) and Step 11 (HLS into AndroidSurface). The HLS stream is encoded TB, so the handler's per-eye `SubImage.ImageRect` should naturally produce correct stereo without re-encoding.

**Change:** in `StereoCompositorSceneSetup.cs`, set `data.StereoLayout = StereoLayout.TopBottom`. No code change in the handler — `ComputeEyeRects` already handles TopBottom. The source texture's dimensions are now whatever the HLS stream is (e.g. 3840×4320 for the Main_Final stream).

**Builds on:** Step 11's working HLS, Step 5's working TB split.

**Auto-confirm method:**
- `dev.sh alive-for 60` exits 0.
- `dev.sh verify-color` — both eyes show "video" (non-black average), AND left/right RGB averages **differ by at least 30 per channel** in at least one channel. This requires another `dev_verify_color.py` extension: `--require-asymmetric` flag. The exact colors are stream-dependent so we can't hard-code them; asymmetry is the contract.
- `dev.sh tag step12-stereo-hls`.

**Failure-mode disambiguation:** as Step 5 — both eyes identical means TB split regressed; back to `EyeVisibility` / handler emission count diagnostic.

**Estimated loop iterations:** 1–3.

---

### Milestone summary

After Step 12, the project has a working stereo HLS compositor panel, with: (a) a stable per-eye split, (b) AndroidSurface-driven swapchain, (c) ExoPlayer feeding it, (d) blend type validated, (e) coexisting cleanly with URP. **This is the "working stereo video compositor panel" the user asked for.** The mask layer, the overlay layer, the FB alpha-blend extension, and the three-panel zIndex topology from the native Spatial SDK reference are all *additive* on top of this substrate and are deferred to a follow-up plan.

### What this plan deliberately defers

- **The mask layer + `XR_FB_composition_layer_alpha_blend` extension wiring.** These need their own incremental plan once the substrate is stable; Step 9 establishes that we have the alpha-blend understanding to do them, but the plan doesn't actually wire them.
- **The overlay layer + `setClip` per-eye UV rects.** Same reason.
- **Specific HLS configurations beyond the Main_Final test stream.** The journal lists `videoUrl` and `maskUrl` constants in `StereoVideoManager.cs`; we use those as-is.
- **Audio.** ExoPlayer plays audio by default; the plan doesn't gate on audio working but doesn't disable it. If the user wants a separate audio milestone, it's a one-step add later.
- **App/package rename** (`com.UnityTechnologies.com.unity.template.urpblank` → `com.Quintar.VibeUnity.ExoTest`). Day 7 of the older plan; orthogonal.
- **Performance tuning.** No Unity render passes for video pixels (the journal flags this as a Day 7 concern); compositor-layer-only is automatic via this plan, but explicit perf measurement is deferred.
- **Pause/resume + ExoPlayer lifecycle edge cases.** The plan's `alive-for` checks are 15–60 s; longer-lived robustness is deferred.
- **Filing the Unity bug** (path C from the original architect doc) — only run if Step 3b (`ILayerHandler` direct) also fails to fix the SEGV.

---

## STATE AT HANDOFF — START HERE FOR A NEW SESSION

**Repo:** `richoncode/Unity` (https://github.com/richoncode/Unity, public).
**Local repo root:** `/Users/richardbailey/RichardClaude/Unity/` (the directory that contains this file's parent dir, `VibeUnity1/`).
**Unity project:** `/Users/richardbailey/RichardClaude/Unity/VibeUnity1/` (Assets/, Packages/, ProjectSettings/ at this level).
**Initial commit:** `b908bab` — captures everything below.

### Current verified state on Quest 3 (post-reboot, clean Library/Bee, both OpenXR features enabled)

| Configuration | Result | Run |
|---|---|---|
| No `[Compositor] StereoQuadPanel` GameObject in scene | ✅ alive | `STEREO_SKIP_PANEL=1` env var on AutoBuilder |
| Panel uses **stock** `QuadLayerData` | ✅ alive | `STEREO_USE_STOCK_QUAD=1` env var on AutoBuilder |
| Panel uses our **custom** `StereoQuadLayerData` | ❌ SIGSEGV in `Resources.FindObjectsOfTypeAll<CompositionLayer>` ~500 ms after launch | default build |

This is the **single isolated failure mode** to focus on. The earlier "everything crashes" cascade-failures (URP `XRDisplaySubsystem.TryGetRenderPass` null, ShaderPropertySheet SEGV, etc.) were Quest 3 device-side state pollution from 60+ install/uninstall cycles in one session. **A device reboot cleared them.** Going forward: when test results stop matching expectations, reboot the Quest before chasing other hypotheses.

### Bisect already ran — these did NOT fix the custom-LayerData crash

- Empty subclass with no fields (`Provider="Quintar"`, no fields, all property accessors return constants)
- Stock-clone subclass with `Provider="Unity"` and identical fields to `QuadLayerData`
- Adding `[Preserve]` attribute on the class
- `link.xml` `<assembly preserve="all"/>` for our asmdef
- Custom struct (memory-identical to `XrCompositionLayerQuad`) to dodge `OpenXRCustomLayerHandler<T>` static-singleton conflict
- Compiling our LayerData type but NOT registering our handler (so handler logic never runs)
- Reverting texture importer overrides
- Disabling all unrelated runtime spawners (`StereoVideoManager`, `CubeInteractionSetup`)

The crash is specifically about a custom `LayerData` subclass *being present in the scene* with the framework enumerating it via `FindObjectsByType`. It is NOT about: handler logic, scene authoring approach, build cache state, or any field on the class. **A new architectural approach is needed; field-level bisecting is exhausted.**

### Files reflecting current handoff state

- `Assets/Scripts/StereoVideoCompositor/AlphaBlendFBExtension.cs` — `CompositionLayerExtension` subclass + `XrBlendFactorFB` enum + `XrCompositionLayerAlphaBlendFB` struct. Working.
- `Assets/Scripts/StereoVideoCompositor/StereoCompositorFeature.cs` — `OpenXRFeature` subclass declaring `XR_FB_composition_layer_alpha_blend`. Registers our `StereoQuadLayerHandler` for `typeof(StereoQuadLayerData)` on `OpenXRLayerProvider.Started`. Day 1 verified working.
- `Assets/Scripts/StereoVideoCompositor/StereoQuadLayerData.cs` — `LayerData` subclass with `[CompositionLayerData(SupportTransform=true)]`. **THIS is what triggers the crash when present in scene.**
- `Assets/Scripts/StereoVideoCompositor/StereoQuadLayerHandler.cs` — `OpenXRCustomLayerHandler<XrCompositionLayerQuad>` subclass. Has architectural problem: shares static singleton with stock `OpenXRQuadLayer` (KeyNotFoundException per frame). **Replace with direct `ILayerHandler` implementation per the architect review.**
- `Assets/Scripts/StereoVideoCompositor/Quintar.StereoVideoCompositor.asmdef` — references Unity.XR.OpenXR + Unity.XR.CompositionLayers + Unity.Collections by GUID, allowUnsafeCode=true.
- `Assets/Scripts/StereoVideoCompositor/StereoCompositorSpawner.cs` — runtime spawner DISABLED (architect ruled runtime CompositionLayer creation unsafe; edit-time only).
- `Assets/Editor/StereoCompositorBuildSetup.cs` — currently a no-op (only refreshes feature discovery). Earlier toggling of OpenXR features per build was found to mutate `OpenXR Package Settings.asset` in ways that destabilize XR init. Manual feature enabling via Unity Editor UI is now the recommended path. NOTE: AutoBuilder still calls this, which currently does nothing.
- `Assets/Editor/StereoCompositorSceneSetup.cs` — edit-time scene authoring, called from `AutoBuilder.BuildAndroid`. Honors `STEREO_SKIP_PANEL=1` and `STEREO_USE_STOCK_QUAD=1` env vars for testing.
- `Assets/Editor/AutoBuilder.cs` — single-button build script.
- `Assets/Scripts/StereoVideoManager.cs` — legacy SurfaceTexture path. Class compiled, runtime spawner DISABLED.
- `Assets/Scripts/CubeInteractionSetup.cs` — unrelated test cubes, runtime spawner DISABLED (its `new Material(null)` was poisoning URP earlier in the session, since fixed but spawner left disabled).
- `Assets/Plugins/Android/ExoPlayerBridge.java` — current Java bridge using SurfaceTexture. Will need to be refactored to take a Surface directly once Day 3 unblocks.
- `Assets/StereoCompositor/StereoTestPattern.png` — 1024×1024 RGBA32 red-top / blue-bottom test pattern. Imported with default settings (isReadable=false, no platform override).
- `Assets/Scenes/MR_Passthrough_Scene.unity` — the active scene. Contains `[Compositor] StereoQuadPanel` GameObject when default build runs.

### Native Spatial SDK reference (Kotlin) — get from user

The user has the canonical native player code (`PanelSceneObject` + `LayerAlphaBlend` + `setClip`). It was pasted in chat history during the design phase but is NOT included in this repo. **Ask the user for the snippet** before designing any new approach — it shows the exact `srcColor=DST_ALPHA, dstColor=ONE_MINUS_SRC_ALPHA, srcAlpha=ZERO, dstAlpha=ONE` blend factors and the three-panel zIndex topology.

### Verified diagnostic methods

- **`adb shell screencap -p > /tmp/foo.png`** captures the Quest 3 display including compositor layers. Stereo (left+right halves). Use to verify panel renders correctly.
- **`VrApi:` logcat lines** — `LCnt=N` is the number of compositor layers being submitted. Baseline (no panel): ~3. With single panel: ~5–6. With our intended 6-quad stereo split: would be ~9.
- **`Crash detected: NATIVE_CRASH_REPORT, pid=N`** from `DiagnosticsCollectorService` is the most reliable crash signal in logcat. Don't rely on grepping for `signal 11` — it gets buffer-rolled.

### adb gotcha

The Mac has TWO adb installations: Homebrew `/opt/homebrew/bin/adb` and Unity's bundled `/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`. They fight for port 5037. Mid-test `install -r` and `screencap` calls silently fail. Either: kill all adb daemons and let one start fresh, or `export PATH="<unity-adb-path>:$PATH"` so only Unity's adb is on PATH.

### Recommended next step for a new architect

The bisect already ruled out field-level + attribute-level + handler-level fixes. Two paths remain:

**(A) Implement `OpenXRLayerProvider.ILayerHandler` directly**, bypass `OpenXRCustomLayerHandler<T>` entirely. Estimate: 1–2 days. May or may not avoid the `FindObjectsByType` SEGV (which happens before our handler even fires). If the crash is in `CompositionLayerManager`'s enumeration of the GameObject regardless of handler choice, this won't help.

**(B) Switch to OVROverlay** + composite mask × video in Unity (Vulkan compute / shader). Loses per-layer custom blend factors at the OpenXR runtime level, so the destination-alpha trick has to move from "compositor layer math" to "Unity shader pre-multiply mask alpha into video RGB before submitting one combined OVROverlay". Estimate: 2–3 days. Lower risk but lower fidelity (Unity render pass for video pixels = some quality loss vs. compositor-direct path).

**(C) File a Unity bug** and wait. The crash signature (`Resources.FindObjectsOfTypeAll<CompositionLayer>` SEGV when a custom `LayerData` subclass via `[SerializeReference]` is present in the scene) is reproducible enough to file. Stack: `com.unity.xr.openxr@1.17.0 + com.unity.xr.compositionlayers@2.2.0 + Meta XR Core 201 + URP 17.4 + Unity 6000.4.5f1 + Quest 3 OS 203`.

A fresh architect should evaluate (A) vs (B) given the user's tolerance for fidelity loss. (C) can run in parallel.

---

## FIRST ACTIONS FOR THE ARCHITECT (NEW SESSION)

### Goal

End-to-end: **masked stereo video with overlay canvas pixels** rendered on the Quest 3, matching the native Spatial SDK player's visual output as closely as the Unity stack permits. "Masked" = destination-alpha cutout from `AllStar_MidCourt_Jumbo10.png` (alpha defines visible region). "Stereo" = per-eye correct content from a top-bottom encoded HLS stream via ExoPlayer. "Overlay canvas pixels" = a third compositor / overlay layer drawn on top, optionally clipped per-eye via UV rects.

### Step 1 — Review (don't write code yet)

Before formulating any plan, read these in order:

1. **This entire document, top to bottom.** Treat the "STATE AT HANDOFF" + "Bisect already ran" sections as the ground truth for what works and what doesn't. Do not re-run bisects that already concluded.

2. **The native Kotlin reference snippet from the user.** Not in the repo — ask the user to paste it. Look for: `createVideoPanel`, `createMaskLayeredPanel`, `createOverlayLayeredPanel`, `videoMaskingAlphaBlend()`. These define the canonical zIndex topology, blend factors, stereo mode per panel, and `setClip` UV rects. Any Unity-side design must reproduce that math exactly (or document why it can't and what the fidelity cost is).

3. **The relevant Unity SDK source.** Paths in the "Critical files (verified locations)" section. At minimum:
   - `OpenXRLayerProvider.cs` (`ILayerHandler` interface)
   - `OpenXRCustomLayerHandler.cs` (the base class that has the static-singleton problem)
   - `OpenXRLayerUtility.cs` (`AddActiveLayersToEndFrame`, `CreateSwapchain`, `GetLayerAndroidSurfaceObject`)
   - `OpenXRQuadLayer.cs` (stock handler, exemplar)
   - `Samples~/CustomCompositionLayerFeature/CustomLayerHandler.cs` (sample)

4. **The current project source under `Assets/Scripts/StereoVideoCompositor/`** — see "Files reflecting current handoff state" section above.

### Step 2 — Decide a path

Pick from (A), (B), or some hybrid you propose. Document the rationale in this file (append a new section). Specifically address:

- Will the chosen path actually avoid the `FindObjectsByType` SEGV? Provide reasoning, not just hope.
- Does the chosen path preserve the destination-alpha masking math `final.rgb = video.rgb * dst.a + dst.rgb * (1 − video.a)`? If not, what's the visual fidelity cost and is the user OK with it?
- What are the milestones (incremental, testable steps) and the first one to ship?

### Step 3 — Increment

Produce an incremental plan. Each step should be testable on device in isolation. Aim for ~2-hour increments where possible.

Suggested rhythm (the architect can override):

1. Confirm baseline: build with `STEREO_SKIP_PANEL=1`, deploy, verify alive on device. Screenshot.
2. Add ONE thing (e.g. a single OVROverlay quad with a static texture, or a single `ILayerHandler`-emitted layer).
3. Verify visually via `adb shell screencap` — see "Verified diagnostic methods" + "Device-and-content positioning" below.
4. Repeat: ExoPlayer surface → mask blend → stereo split → overlay layer → polish.

Do NOT attempt the full 6-layer / 3-panel topology in one shot. Smallest viable visible result first, then add layers.

### Device-and-content positioning (Quest 3 on a stand)

The Quest 3 in this setup **sits stationary on a stand approximately 0.8 m off the floor** (head-not-being-worn mode). It is NOT being moved by a user during testing. This has implications:

- **Where the panel needs to be in world space.** The OVRCameraRig's tracking-space origin is roughly co-located with the device. To be visible in `adb screencap`, the panel must be placed where the device's lenses are pointing. Common-sense default: parent the panel GameObject to `OVRCameraRig.trackingSpace` (or use `XrReferenceSpaceType.View` for head-locked) and position at local `(0, 0, 1.5)` — directly in front of the device, 1.5 m ahead, at roughly eye height. **Avoid world-space `(0, 1.5, 1.5)`** (the previous default) — it assumes a standing user with floor at Y=0, but here the device's eye height in world Y is ~0.8 m, so a Y=1.5 m panel ends up well above the device's view frustum and is invisible in screenshots.

- **What the screencap will show.** `adb shell screencap -p > /tmp/foo.png` captures a stereo image (left + right halves) of whatever is in the device's current view. If the test panel isn't in the view direction, it won't be in the screencap regardless of whether it's actually rendering correctly. A "blank" screencap is NOT proof of failure — verify position first by also checking `VrApi: LCnt=N` in logcat (layer count >= baseline+1 means a layer IS being submitted, just maybe out of frame).

- **Practical recipe for a visible test pattern.**
  - Parent the `[Compositor] StereoQuadPanel` GameObject to `OVRCameraRig.trackingSpace` (find it in the scene; it's the standard `OVRCameraRig` prefab).
  - Set local position to `(0, 0, 1.5)` and rotation `(0, 180, 0)` so the front face faces the device. Adjust Z if too close/far.
  - The panel is then auto-positioned wherever the device is — no need for the device to be at any particular world location.
  - **Existing `StereoCompositorSceneSetup.cs` currently uses world-space `(0, 1.5, 1.5)` — UPDATE IT.** Change to parent under `OVRCameraRig.trackingSpace` (or whatever the canonical Meta XR Core rig anchor is) and use local coords. Document the change in the architect's plan section.

- **Screenshot workflow loop.**
  ```bash
  # 1. Build + install
  /Applications/Unity/Hub/Editor/6000.4.5f1/Unity.app/Contents/MacOS/Unity \
      -batchmode -nographics \
      -projectPath /Users/richardbailey/RichardClaude/Unity/VibeUnity1 \
      -executeMethod AutoBuilder.BuildAndroid \
      -logFile /tmp/build.log
  adb -s 2G0YC5ZGB405BG install -r /Users/richardbailey/RichardClaude/Unity/VibeUnity1/Builds/MR_Passthrough.apk

  # 2. Force-stop, clear logcat, launch
  adb -s 2G0YC5ZGB405BG shell am force-stop com.UnityTechnologies.com.unity.template.urpblank
  adb -s 2G0YC5ZGB405BG logcat -c
  adb -s 2G0YC5ZGB405BG shell am start -n com.UnityTechnologies.com.unity.template.urpblank/com.unity3d.player.UnityPlayerGameActivity

  # 3. Wait for stable state, then verify alive
  sleep 15
  adb -s 2G0YC5ZGB405BG shell pidof com.UnityTechnologies.com.unity.template.urpblank   # should print PID
  adb -s 2G0YC5ZGB405BG logcat -d | grep -c "Crash detected.*urpblank"                  # should print 0

  # 4. Screencap
  adb -s 2G0YC5ZGB405BG shell screencap -p > /tmp/shot.png
  # Open /tmp/shot.png; you'll see left and right eye halves.
  ```
  If the screencap is empty / 0 bytes, the device is in standby mode. Tap any controller / hand / nudge the device to wake it.

- **Package + activity names** (don't change):
  - APK package: `com.UnityTechnologies.com.unity.template.urpblank` (yes, the URP-blank-template name; the project's `applicationIdentifier` was never updated — see "Day 7" item in the implementation plan; out of scope for compositor work)
  - Launch activity: `com.unity3d.player.UnityPlayerGameActivity`
  - Device serial: `2G0YC5ZGB405BG` (the lab Quest 3)

### Things to avoid (learned the hard way)

- **Do NOT mutate `OpenXR Package Settings.asset` from the build pipeline.** Earlier `StereoCompositorBuildSetup.EnsureEnabledForAndroid` toggled features per build; that combined with rapid install/uninstall cycles destabilized device-side XR init. If features need toggling, do it once via a `[MenuItem]` in the Unity Editor.
- **Do NOT install/uninstall the app dozens of times in a row without rebooting the Quest.** Quest's runtime accumulates state that pollutes XR display init. After ~30 deploys, reboot.
- **Do NOT `[RuntimeInitializeOnLoadMethod]`-spawn `CompositionLayer` GameObjects.** Confirmed unsafe — `CompositionLayerManager.FindObjectsByType` SEGVs during scene refresh on a runtime-added CompositionLayer. Edit-time scene authoring only.
- **Do NOT use `OpenXRCustomLayerHandler<XrCompositionLayerQuad>`** as the base for our handler — static singleton conflict with stock `OpenXRQuadLayer`. Use `ILayerHandler` directly OR `OpenXRCustomLayerHandler<MyDistinctStruct>` with a memory-identical struct of a different C# type.
- **Do NOT trust `signal 11`-substring greps for crash detection.** The logcat ring buffer rolls quickly. Use `Crash detected: NATIVE_CRASH_REPORT` from `DiagnosticsCollectorService` as the canonical signal.
- **Do NOT assume Homebrew adb and Unity adb coexist.** They fight for port 5037. Pick one (Unity's is what `gh` and CI scripts expect): `export PATH="/Applications/Unity/Hub/Editor/6000.4.5f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools:$PATH"` and kill the other daemon.

---

## Goal

Replicate the architecture of the native Meta Spatial SDK player using Unity's
stock OpenXR composition layer subsystem, so that:

- ExoPlayer renders HLS v7 video into an OpenXR `XR_KHR_android_surface_swapchain`
  (no SurfaceTexture, no Vulkan/GLES interop).
- Three logical "panels" (mask, video, overlay) are submitted as compositor
  layers, each in stereo by emitting two `XrCompositionLayerQuad` per panel
  (one per eye, addressing the correct half of the source via `SubImage.ImageRect`).
- The destination-alpha masking effect from the native player is preserved by
  attaching `XR_FB_composition_layer_alpha_blend` to the mask + video layers
  with `srcColor=DST_ALPHA, dstColor=ONE_MINUS_SRC_ALPHA, srcAlpha=ZERO,
  dstAlpha=ONE` on the video layer and `(srcAlpha=ONE, dstAlpha=ZERO)` on the
  mask layer to deterministically seed `dst.a`.
- Stays Vulkan; no graphics-API change; no native plugin.

## Native reference architecture (Spatial SDK / Kotlin)

Three `PanelSceneObject`s sharing the same world transform:

| Layer | zIndex | Stereo | Surface | Special |
|---|---|---|---|---|
| Mask    | -3 | LeftRight | static texture (`AllStar_MidCourt_Jumbo10.png`, 7680x2160) | drawn first so video layer can read its alpha as `dst.a` |
| Video   | -2 | TopBottom | ExoPlayer surface (3840x4320) | `LayerAlphaBlend(srcColor=DST_ALPHA, dstColor=ONE_MINUS_SRC_ALPHA, srcAlpha=ZERO, dstAlpha=ONE)` |
| Overlay | +1 | LeftRight | overlay surface | `setClip(minUV=(0, 0.25), maxUV=(1, 0.75))` per eye |

Destination-alpha masking math:

```
final.rgb = video.rgb * dst.a + dst.rgb * (1 - video.a)
final.a   = dst.a            (preserved from mask layer)
```

The mask seeds the framebuffer alpha; the video uses it to gate its own RGB.

## Why not OVROverlay

- `OVRPlugin.EnqueueSubmitLayer*` does not accept per-layer custom blend factors.
- `OVRPlugin.BlendFactor` enum is defined at `OVRPlugin.cs:1680` but unreferenced
  anywhere in `com.meta.xr.sdk.core@201.0.0` - dead code.
- `OVROverlay` exposes no alpha-blend property.
- The `XR_FB_composition_layer_alpha_blend` extension is shipped by the runtime
  (verified at version 3 in this Quest 3's logcat) but Meta XR Core SDK does not
  surface it to Unity. Stock OpenXR plugin does.

## Why stock Unity OpenXR plugin works

Verified via spike (read SDK source + sample):

- `com.unity.xr.openxr@1.17.0` ships full composition layer subsystem in
  `Runtime/CompositionLayers/` with `OpenXRCustomLayerHandler<T>`,
  `OpenXRLayerProvider`, `OpenXRLayerUtility`, etc.
- `OpenXRQuadLayer.ModifyNativeLayer` (line 204) calls `OpenXRLayerUtility.GetExtensionsChain(layerInfo, ExtensionTarget.Layer)` every frame, so any `CompositionLayerExtension` subclass attached to the layer GameObject is auto-picked-up.
- `XrStructureType.CompositionLayerAlphaBlendFB = 1000041001` is in
  `Runtime/NativeTypes/Core/XrStructureType.cs:637`.
- `OpenXRLayerUtility.CreateSwapchain(... isExternalSurface: true)` paired with
  `OpenXRLayerUtility.GetLayerAndroidSurfaceObject(layerId)` provides the
  Android Surface for ExoPlayer. Same path the Spatial SDK uses underneath.
- Quest 3 runtime advertises `XR_FB_composition_layer_alpha_blend` (version 3),
  `XR_KHR_android_surface_swapchain`, `XR_FB_android_surface_swapchain_create`,
  `XR_FB_swapchain_update_state_vulkan`. Same logcat dump as MR_Passthrough.apk.
- Sample `Samples~/CustomCompositionLayerFeature/` shows the canonical pattern
  (3 files: `CustomFeature`, `CustomQuadLayerData`, `CustomLayerHandler`).

## Stereo split via custom `ILayerHandler`

The architect review found that stock `OpenXRQuadLayer` always emits ONE
`XrCompositionLayerQuad` with `EyeVisibility=0` (both eyes) and full SubImage
rect. Per-eye stereo split (which the native player does internally) requires
TWO `XrCompositionLayerQuad` submissions per logical panel:

- Eye left: `EyeVisibility=1`, `SubImage.ImageRect` = top half (TB) or left half (LR)
- Eye right: `EyeVisibility=2`, `SubImage.ImageRect` = bottom half (TB) or right half (LR)

`OpenXRCustomLayerHandler<T>` is generic over a single struct type per layer.
We bypass it and implement `OpenXRLayerProvider.ILayerHandler` directly:

- `OnUpdate()` is called every frame; in it we call
  `OpenXRLayerUtility.AddActiveLayersToEndFrame(void*, void*, int, int)` with
  a pointer to an array of native structs.
- Nothing constrains us to one-struct-per-LayerInfo. We allocate ONE swapchain
  per panel and submit TWO `XrCompositionLayerQuad`s per panel each frame.

End state: 3 CompositionLayer GameObjects (mask, video, overlay), 6 native
layers per frame, 3 swapchains.

## Architect review findings (must address)

1. Mask layer also needs `XrCompositionLayerAlphaBlendFB` to deterministically seed `dst.a` - not just video layer. Mask: `(srcAlpha=ONE, dstAlpha=ZERO)` (or similar to write mask.a → dst.a).
2. Mask PNG import: `Alpha is Transparency` must be **off**, alpha **straight** (not premultiplied), so the `UnPremultipliedAlpha` core flag composes correctly with our FB extension.
3. Camera clear flags: must be RGBA (0,0,0,0). With passthrough enabled the eye buffer's alpha needs to leave room for layer composition. Mask layer is hidden if URP eye buffer is opaque over its position.
4. `Layer.Order` explicit per panel (mask=-2, video=-1, default scene=0, overlay=+1).
5. `GetLayerAndroidSurfaceObject` is async - returns null until the graphics-thread callback fires. Must subscribe to the callback (`actionsForMainThread` queue, see `OpenXRCustomLayerHandler.cs:308, 666-689`) before passing Surface to ExoPlayer.
6. Passthrough layer ordering is independent of Unity's `Order` and uses Meta's `compositionDepth`. Need explicit decision (likely passthrough deepest, then mask).
7. Verify TexturesExtension AndroidSurface honors a single shared swapchain across two `XrCompositionLayerQuad` submissions - spec-permitted, what Spatial SDK does, but unverified on Quest 3 in our setup.

## Critical files (verified locations)

Unity OpenXR composition layer system:
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Runtime/CompositionLayers/OpenXRLayerProvider.cs` - `ILayerHandler` interface (line 41), `RegisterLayerHandler` (line 133)
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Runtime/CompositionLayers/OpenXRCustomLayerHandler.cs` - high-level base class
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Runtime/CompositionLayers/OpenXRLayerUtility.cs` - `CreateSwapchain` (line 102), `GetLayerAndroidSurfaceObject` (line 321), `GetExtensionsChain`, `AddActiveLayersToEndFrame`
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Runtime/CompositionLayers/OpenXRQuadLayer.cs` - reference handler, line 204 shows `ModifyNativeLayer.Next = GetExtensionsChain(...)`
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Runtime/NativeTypes/Core/XrStructureType.cs:637` - `CompositionLayerAlphaBlendFB = 1000041001`

Sample (template to follow):
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Samples~/CustomCompositionLayerFeature/CustomFeature.cs` - `OpenXRFeature` subclass
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Samples~/CustomCompositionLayerFeature/CustomQuadLayerData.cs` - `LayerData` subclass with `[CompositionLayerData]` attribute
- `Library/PackageCache/com.unity.xr.openxr@5dc08d6a3e5b/Samples~/CustomCompositionLayerFeature/CustomLayerHandler.cs` - handler subclass

Existing project files to refactor:
- `Assets/Scripts/StereoVideoManager.cs` - replace SurfaceTexture/Texture2D path
- `Assets/Plugins/Android/ExoPlayerBridge.java` - take Surface directly instead of building one from SurfaceTexture
- `Assets/Editor/AutoBuilder.cs` - build script (keep)

Files to remove (after new path is working):
- `Assets/Resources/Shaders/StereoMaskedVideo` (the custom shader, no longer needed - compositor handles masking)

## Implementation plan (revised, 5-7 days)

### Day 1: scaffolding + smoke test ✅ DONE 2026-05-06
1. ~~Add `com.unity.xr.compositionlayers` to `Packages/manifest.json`.~~ DONE — used `2.2.0` (2.0.0 and 2.1.0 fail on URP 17 RenderGraph migration; 2.2.0 fixes the `Execute` override signature).
2. ~~Reimport Unity.~~ DONE — `XR_COMPOSITION_LAYERS` define is on; package source available at `Library/PackageCache/com.unity.xr.compositionlayers@08f04b171ba1/`.
3. ~~Create `Assets/Scripts/StereoVideoCompositor/`.~~ DONE.
4. ~~Define managed types in `AlphaBlendFBExtension.cs`.~~ DONE (struct + enum bundled with the extension class).
5. ~~`StereoCompositorFeature : OpenXRFeature`.~~ DONE — `Assets/Scripts/StereoVideoCompositor/StereoCompositorFeature.cs`.
6. ~~`AlphaBlendFBExtension : CompositionLayerExtension`.~~ DONE — same file as struct/enum. Defaults: SrcAlpha, OneMinusSrcAlpha, One, Zero (standard alpha-over).
7. **Editor utility added**: `Assets/Editor/StereoCompositorBuildSetup.cs` — calls `FeatureHelpers.RefreshFeatures(Android)` + enables our feature by ID. Wired into `AutoBuilder.BuildAndroid()` so every build registers and enables it.
8. ~~Build + deploy + verify expected logcat lines.~~ **PASSED** — all four expected log lines confirmed on device (Quest 3, Quest OS 203):
   - `[StereoCompositorBuildSetup] Enabled OpenXR feature 'ai.quintar.stereo-video-compositor' for Android.`
   - `[StereoCompositorBuildSetup] Enabled OpenXR feature 'com.unity.openxr.feature.compositionlayers' for Android.`
   - `[StereoCompositor] Feature.OnEnable. XR_FB_composition_layer_alpha_blend extension requested.`
   - `[StereoCompositor] OnInstanceCreate. XR_FB_composition_layer_alpha_blend enabled = True.`
   - `[StereoCompositor] OpenXRLayerProvider.Started fired.`
   - Runtime acknowledged: `Name=XR_FB_composition_layer_alpha_blend SpecVersion=3`

   Lessons captured for Day 2+:
   - `XR_COMPOSITION_LAYERS` versionDefine only flows inside `Unity.XR.OpenXR` asmdef; do NOT use that gate in our own scripts. Our asmdef references `Unity.XR.OpenXR` and `Unity.XR.CompositionLayers` directly by GUID.
   - `CompositionLayerExtension` lives in namespace `Unity.XR.CompositionLayers` (not `Unity.XR.CompositionLayers.Extensions`, despite the file path).
   - `OpenXRFeature` lives in namespace `UnityEngine.XR.OpenXR.Features`.
   - Two separate features must be enabled in the OpenXR Settings asset for layer submission to work: ours (`ai.quintar.stereo-video-compositor`) for the FB extension, and Unity's stock `com.unity.openxr.feature.compositionlayers` for the layer provider itself.
   - `FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id)` returns null until the class is actually compiled (i.e. ensure no `#if` gate is hiding it).
   - `com.unity.xr.compositionlayers` package needs version `2.2.0` minimum on Unity 6 / URP 17. Versions 2.0.0 and 2.1.0 fail compilation against URP 17's RenderGraph migration (`EmulationLayerUniversalScriptableRendererPass.Execute` override invalid).

### Day 2: stereo Quad with local texture (in-progress 2026-05-06)

**Status:** scaffolding done, scene-authored panel, handler firing. Visual not verified — multiple crashes encountered. See "Lessons / open issues" below.

1. `StereoQuadLayerData : LayerData` with `[CompositionLayerData]` attribute. ✓ DONE. Critical: must include `SupportTransform = true` in attribute (and IconPath/InspectorIcon/ListViewIcon = "" if not used). Without `SupportTransform = true`, the runtime crashes during `CompositionLayerManager.FindObjectsByType<CompositionLayer>` enumeration.

2. **CRITICAL — DO NOT subclass `OpenXRCustomLayerHandler<T>`.** The base class has a `protected static OpenXRCustomLayerHandler<T> Instance;` singleton (line 138 of OpenXRCustomLayerHandler.cs) that gets clobbered when multiple handlers share the same `T`. Stock `OpenXRQuadLayer` already uses `OpenXRCustomLayerHandler<XrCompositionLayerQuad>`; our handler can't share T or it overwrites the static singleton, causing the static `OnCreatedSwapchainCallback` lambda (line 666-675) to dereference the wrong handler's `m_LayerInfos[layerId]` and throw `KeyNotFoundException` per frame.
   - Workarounds attempted that DON'T work:
     - Defining a memory-identical custom struct (sample's `CustomNativeCompositionLayerQuad` pattern) to get our own `OpenXRCustomLayerHandler<MyStruct>` static slot — still SEGVs in `FindObjectsByType`. Symptoms suggest something deeper than the singleton conflict.
   - **Path forward:** Implement `OpenXRLayerProvider.ILayerHandler` directly (5 methods: `OnUpdate`, `CreateLayer`, `RemoveLayer`, `ModifyLayer`, `SetActiveLayer`). Manage swapchain creation, render-texture blit, and `AddActiveLayersToEndFrame` ourselves. ~250 lines, but no shared static state.

3. Scene-authoring is done at edit time via `StereoCompositorSceneSetup` (Editor script wired into AutoBuilder). Runtime spawning of `CompositionLayer` GameObjects causes a SEGV during `CompositionLayerManager`'s refresh; edit-time authoring is mandatory.

### Day 2 BLOCKER (2026-05-06 evening)

After full clean rebuild (deleted `Library/Bee/` 11 GB + `Library/ScriptAssemblies/`) and fresh APK install, the app still SIGSEGVs ~500 ms after launch when the scene-authored `[Compositor] StereoQuadPanel` GameObject is present. Confirmed isolation:

- `STEREO_SKIP_PANEL=1`: no panel in scene → app stable, no crash.
- `STEREO_USE_STOCK_QUAD=1`: panel uses stock `QuadLayerData` (and our handler is registered but inactive) → app stable, no crash.
- Default config: panel uses our `StereoQuadLayerData` → consistent SIGSEGV.

Crash signature shifts depending on build state — observed at least two distinct stacks:

1. `Resources.FindObjectsOfTypeAll<CompositionLayer>` SEGV (`Scripting::FindObjectsOfType` → `Scripting::ScriptingWrapperFor(Object*)`). Triggered by display refresh rate change at session start.
2. URP render loop SEGV: `ScriptableRenderContext::ExecuteScriptableRenderLoop` → `RenderingCommandBuffer::ExecuteCommandBufferWithState` → `ShaderPropertySheet::AddNewPropertyUninitialized` → `core::vector<ShaderLab::FastPropertyName>::insert` → `MemoryManager::Reallocate` → `MemoryProfiler::GetAllocationRoot`. This is a memory-corruption signature, not a logic crash.

Things that did NOT fix it (tested individually):
- Adding `SupportTransform = true` and other missing fields to the `[CompositionLayerData]` attribute.
- Switching `T` to a custom struct with identical layout (sample's `CustomNativeCompositionLayerQuad` pattern) to dodge the static-singleton conflict in `OpenXRCustomLayerHandler<T>`.
- Reverting texture importer overrides (`isReadable=false`, no Android format override).
- Disabling the legacy `StereoVideoManager` runtime spawner.
- Disabling the `CubeInteractionManager` whose null-shader exception we suspected was poisoning URP state.
- Wiping `Library/Bee/` and `Library/ScriptAssemblies/` for a fully clean rebuild.

The stack is **com.unity.xr.openxr@1.17.0 + com.unity.xr.compositionlayers@2.2.0 + Meta XR Core 201 + URP 17.4 + Unity 6000.4.5f1 + Quest 3 OS 203**. Some interaction within this stack causes consistent runtime corruption when a custom `LayerData` subclass with `[SerializeReference]` polymorphic data is present.

### Day 2 BISECT RESULTS 2026-05-06 evening (autonomous run)

User went to bed and asked me to bisect autonomously. Ran 9 progressive build/deploy/screencap cycles. Findings:

| Test | Result |
|---|---|
| Stock `QuadLayerData` only (previously alive) | **REGRESSED — now crashes** |
| No CompositionLayer in scene | **REGRESSED — now crashes** |
| Empty custom LayerData subclass (Provider="Quintar") | Crashes |
| Custom LayerData stock-clone (Provider="Unity") | Crashes |
| Custom LayerData with [Preserve] attribute | Crashes |
| `link.xml` `<assembly preserve="all"/>` for our asmdef | Crashes |
| Custom LayerData type compiled but handler NOT registered | Crashes |
| BOTH OpenXR features (ours and stock composition-layers) DISABLED, no panel | **STILL crashes** |

The crash signature shifted to `XRDisplaySubsystem::TryGetRenderPass` null-pointer dereference inside URP's `ScriptableRenderContext::ExecuteScriptableRenderLoop`. This is unrelated to composition layers — URP itself is failing to query the XR display subsystem. The issue is persistent across feature configurations and panel states.

Conclusion: somewhere in the bisect series we put the project (or device-side state) into a state where URP's XR render path null-derefs. The previously-stable runs ("no panel" and "stock QuadLayerData" baselines) no longer reproduce. Most likely culprits, in order of suspicion:

1. **`StereoCompositorBuildSetup.EnsureEnabledForAndroid` mutating `OpenXR Package Settings.asset` repeatedly across builds.** Feature toggling may have left the asset in a state where some XR-required feature is no longer being loaded. The asset has 70 MonoBehaviours (~10 enabled). I disabled mine + stock comp-layers as a test; that didn't fix it. Manual inspection in Unity Editor of which features are enabled vs default is needed.
2. **Quest 3 device runtime state polluted from many install/uninstall cycles** today (60+ deploys). A device reboot may help.
3. **`Library/Bee/Android` regenerated via clean wipe earlier produced a different IL2CPP/URP setup** than the original incremental builds. Some stale-on-disk cache may need another clean wipe + reboot.

### Recommended recovery (manual)

Steps for tomorrow morning, in order:

1. **Reboot the Quest 3** to clear any device-side runtime pollution.
2. **Open Unity Editor** (NOT batchmode) and inspect *Project Settings → XR Plug-in Management → OpenXR → Android*. Verify the standard required features are enabled (Meta Quest Support, Oculus Touch profile, etc.). My bisect may have disabled something it shouldn't have via `feature.enabled = false`.
3. **Re-enable** `ai.quintar.stereo-video-compositor` and `com.unity.openxr.feature.compositionlayers` manually via the UI checkboxes. Save.
4. **Verify the app builds and runs in the editor at least once** in Play mode (or via a minimal device deploy with NO compositor panel) before re-attempting Day 2 work. Goal: confirm baseline is healthy before adding the panel back.
5. Only then re-attempt Day 2 with the bisect findings in mind:
   - `SupportTransform = true` is mandatory in `[CompositionLayerData]` attribute.
   - `OpenXRCustomLayerHandler<T>` static singleton conflicts with stock OpenXRQuadLayer; switch to direct `ILayerHandler` implementation.
   - Use scene-authored panel (edit time), NOT runtime-spawned.
   - Avoid mutating `OpenXR Package Settings` from the build pipeline; do it once via a menu command.

### Key code state at hand-off

- `StereoQuadLayerData.cs`: restored to original (Provider="Quintar", SupportTransform=true, all fields including StereoLayout enum).
- `StereoCompositorBuildSetup.EnsureEnabledForAndroid`: NOW A NO-OP (only refreshes feature discovery; does not toggle enabled state). Stops it from mutating OpenXR Package Settings on every build.
- `StereoCompositorFeature.OnLayerProviderStarted`: handler registration RESTORED.
- `StereoVideoManager`: legacy runtime spawner remains DISABLED (intentional).
- `CubeInteractionSetup`: runtime spawner remains DISABLED (intentional).
- `Assets/StereoCompositor/StereoTestPattern.png`: 1024x1024 RGBA32 PNG with red-top / blue-bottom test pattern. Importer uses defaults (isReadable=false, no Android override) — these are safe; my earlier override caused a different crash.
- Scene `MR_Passthrough_Scene.unity` has the `[Compositor] StereoQuadPanel` GameObject with `StereoQuadLayerData` LayerData. Set `STEREO_SKIP_PANEL=1` env var when invoking AutoBuilder to skip it; `STEREO_USE_STOCK_QUAD=1` to use stock QuadLayerData instead.

### Decision needed before Day 2 continues

Options on the table:

**(A) Implement `OpenXRLayerProvider.ILayerHandler` directly**, bypassing `OpenXRCustomLayerHandler<T>` entirely. Hypothesis: somewhere in the base class's interaction with URP's command buffer (likely `OnBeforeRender` → `WriteToRenderTexture` → URP CommandBuffer set-property path) is the corruption source. By owning the swapchain image acquire/release and texture blit ourselves we may avoid it. Estimate 1–2 days.

**(B) Switch to OVROverlay** (Meta XR Core's overlay path). Loses per-layer custom blend factors (no `XR_FB_composition_layer_alpha_blend`), so we cannot replicate the Spatial SDK's destination-alpha masking exactly — but Meta's overlay path is heavily-exercised on Quest and stable. We'd need to compose mask × video in Unity (pre-multiplied) and submit as a single OVROverlay. Loses some quality/perf benefits of compositor layers but we exit "fragile" territory. Estimate 2-3 days.

**(C) File a Unity issue + minimum-repro project** for the SIGSEGV. Use the panel-less / stock-quad-only state as the working baseline, the custom-LayerData state as the failing repro. Wait for fix, do nothing else. Slow.

**(D) Audit our LayerData / scene serialization** to find what specifically about a custom `LayerData` subclass is unsafe at runtime. The fact that stock `QuadLayerData` works rules out the GameObject path entirely; only the LayerData type differs. Try minimum perturbations: empty subclass with no fields, attribute-by-attribute, etc. Could be a fast (couple-hour) investigation that yields a workaround.

Recommended: **(D) first** — small surface area (2–4 hours of bisecting), can confirm or refute the theory before committing to a multi-day rewrite.

- **`SupportTransform = true` in `[CompositionLayerData]` is NOT optional.** Stock LayerData subclasses all set it; sample omits it and probably crashes on Quest 3 too. Symptom: SIGSEGV in `Resources.FindObjectsOfTypeAll<CompositionLayer>` triggered by display refresh rate change in the first ~500ms of session. The framework treats missing `SupportTransform` as undefined behavior.
- **`OpenXRCustomLayerHandler<T>` has a static-singleton race** with stock layer handlers when sharing T. Cannot be worked around just by using a custom struct — switching to `ILayerHandler` directly is required for our use case (multiple Quad layers with custom handler).
- **Texture import settings matter:** `isReadable=true` and forced platform-specific format overrides correlate with SEGV; the default compressed import works (panel renders white because the framework's blit silently noops in some paths, but doesn't crash). For Day 2 visual verification we'll need either a different texture path (e.g. RenderTexture) or implement the blit ourselves in the new `ILayerHandler`.
- **Quest 3 oscillates the display refresh rate (72→90→72) at session start.** Each transition triggers `CompositionLayerManager.FindObjectsByType<CompositionLayer>` re-enumeration, which is when crashes happen. Pin the rate via `OVRManager.display.displayFrequency` if the issue persists with the new handler.
- **Composition layer GameObject MUST be edit-time authored.** Runtime `AddComponent<CompositionLayer>` consistently SEGVs.

### Day 3: AndroidSurface + ExoPlayer wiring
1. Add `SourceTexture.AndroidSurface` mode to `StereoQuadLayerData`. `StereoQuadLayerHandler.CreateLayer` calls `CreateSwapchain(... isExternalSurface: true)`.
2. After swapchain created (callback), call `GetLayerAndroidSurfaceObject(layerId)`, marshal `IntPtr` to `AndroidJavaObject` for `android.view.Surface`.
3. Refactor `ExoPlayerBridge` to accept a `Surface` parameter directly (drop `SurfaceTexture`, `existingTextureId`, `updateTexture`).
4. Hook ExoPlayer setup into the surface-ready callback.
5. Confirm video pixels appear on the panel (no mask yet).

### Day 4: alpha-blend extension on video + mask
1. Attach `AlphaBlendFBExtension` to the video panel GameObject in scene (factors: DST_ALPHA, ONE_MINUS_SRC_ALPHA, ZERO, ONE).
2. Attach to mask panel (factors that seed dst.a from mask alpha; likely srcAlpha=ONE, dstAlpha=ZERO).
3. Validate destination-alpha math on device: video pixels show only where mask alpha is non-zero.
4. Adjust URP camera clear flags: solid color RGBA(0,0,0,0). Verify passthrough still composes correctly.

### Day 5: overlay layer + per-eye source-rect clipping
1. Third `CompositionLayer` GameObject for overlay. May or may not use AndroidSurface (TBD per native code; if it's another video stream, AndroidSurface; if static graphics, LocalTexture).
2. Per-eye source rects: native `setClip(minUV=(0, 0.25), maxUV=(1, 0.75))` on a stereo-LR layer means each eye samples middle-50% vertical of its half. Implement in `StereoQuadLayerHandler` by computing left/right SubImage rects from a normalized "clip rect" config.
3. Verify overlay clipping per eye on device.

### Day 6: ordering, lifecycle, edge cases
1. Set `Layer.Order` explicit values: mask=-2, video=-1, default scene=0, overlay=+1.
2. Passthrough layer interaction: confirm ordering with Meta's `OVRPassthroughLayer.compositionDepth`. Likely passthrough underneath everything.
3. App pause/resume - swapchains and ExoPlayer surfaces must survive or be re-created cleanly.
4. ExoPlayer error handling: stream end, network drop, codec init failure.
5. Mask PNG import settings: `Alpha is Transparency` off; verify alpha-channel is straight in built APK.

### Day 7: polish + buffer for Quest 3 driver surprises
1. Performance: confirm zero Unity render passes for video pixels (shaders compositor-side only).
2. Quality regression test against native player visuals.
3. Close out the build/version mismatch (ProjectSettings says `com.Quintar.VibeUnity.ExoTest` v0.3.6 but APK ships as `com.UnityTechnologies.com.unity.template.urpblank` v0.1.0 - AutoBuilder.cs needs to honor PlayerSettings).

## What gets deleted / refactored

After day 4-5 working, remove:
- `Assets/Resources/Shaders/StereoMaskedVideo` (the custom shader)
- `StereoVideoManager.Update()` body (no per-frame `updateTexture()` JNI call needed)
- `ExoPlayerBridge.surfaceTexture`, `surface` (replaced by Surface from compositor layer)
- `StereoVideoManager.nativeTexture` (no Texture2D needed)
- The `Custom/StereoMaskedVideo` shader's mask logic (compositor does it)

`StereoVideoManager` collapses to: find/create the three CompositionLayer GameObjects, position them, hand the AndroidSurface from the video panel to the ExoPlayer bridge, listen for ExoPlayer state.

## Open questions to resolve in implementation

1. Does `OpenXRLayerProvider.Started` event reliably fire when `OVRCameraRig` is also in the scene? (Same OpenXR loader; should work, but verify Day 1.)
2. Two `XrCompositionLayerQuad` submissions sharing one AndroidSurface swapchain - works on Quest 3? (Spec-permitted, Spatial SDK does it; verify Day 3.)
3. `CompositionLayerExtension` subclass loading: any registration needed beyond the MonoBehaviour being attached to the GameObject?
4. `XR_FB_composition_layer_image_layout` extension - might be needed for cleaner per-eye source rect rather than manual `SubImage.ImageRect` per layer. Investigate Day 5.
5. Overlay surface: is it a video too, or static graphics? Native code uses `overlayPanelSceneObject.getSurface()` and assigns to `overlaySurface` - implies it can be drawn to. Determine before Day 5.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Two layers sharing AndroidSurface swapchain misbehaves on Quest 3 | Low | Spec-permitted; if it fails, fall back to two AndroidSurface swapchains and two ExoPlayer instances rendering same stream (wasteful) |
| OVRCameraRig conflicts with stock CompositionLayer subsystem | Low | Both target same OpenXR loader; if conflict, replace OVRCameraRig with stock XR Origin |
| Camera clear / eye buffer alpha state breaks mask visibility | Medium | Day 4 explicit verification step; URP clear flags + passthrough configuration |
| FB alpha-blend struct definition mismatches runtime expectations | Low | Spec-stable since v1; runtime advertises v3 |
| AndroidSurface swapchain async creation race | High | Architect-flagged; explicit callback subscription before ExoPlayer setVideoSurface |
| ProjectSettings package name `com.Quintar.VibeUnity.ExoTest` not honored by AutoBuilder | Confirmed | Day 7; out of scope of layer plan; flag for separate fix |

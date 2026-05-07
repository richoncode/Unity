# Stereo Video Compositor Layer Plan

Authoritative implementation plan for the stereo MR video player on Meta Quest 3.
This file persists across context loss / sessions; treat as the source of truth.

Last updated: 2026-05-06.

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

#if UNITY_EDITOR
using System.Linq;
using Quintar.StereoVideoCompositor;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Quintar.StereoVideoCompositor.Editor
{
    /// <summary>
    /// One-shot editor utility that installs a CompositionLayer-driven stereo panel
    /// into <c>MR_Passthrough_Scene</c> and bakes a procedural top-bottom test pattern
    /// PNG into the project. The runtime spawner approach was failing because the
    /// CompositionLayerManager's FindObjectsByType-based refresh SEGVs when a
    /// CompositionLayer is added at runtime — authoring at edit time avoids that
    /// entire class of issue and matches the package's expected usage.
    ///
    /// Run via: Tools → Quintar → Setup Stereo Compositor Test Scene
    /// (Also wired into the AutoBuilder pipeline so a stale scene gets re-baked.)
    /// </summary>
    public static class StereoCompositorSceneSetup
    {
        const string ScenePath = "Assets/Scenes/MR_Passthrough_Scene.unity";
        const string PanelGoName = "[Compositor] StereoQuadPanel";
        const string TexturePath = "Assets/StereoCompositor/StereoTestPattern.png";

        [MenuItem("Tools/Quintar/Setup Stereo Compositor Test Scene")]
        public static void SetupMenuCommand()
        {
            EnsureSceneAuthored();
        }

        public static bool SkipPanelForCrashRepro = System.Environment.GetEnvironmentVariable("STEREO_SKIP_PANEL") == "1";

        public static void EnsureSceneAuthored()
        {
            var testTexture = EnsureTestPatternAsset();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var existing = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(go => go.name == PanelGoName);
            if (existing != null)
            {
                Object.DestroyImmediate(existing);
            }

            if (SkipPanelForCrashRepro)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[StereoCompositorSceneSetup] STEREO_SKIP_PANEL=1: skipped panel; scene saved without CompositionLayer.");
                return;
            }

            var panel = new GameObject(PanelGoName);
            // Parent under OVRCameraRig.TrackingSpace so the panel is
            // positioned relative to the device, not world. The lab device
            // sits at ~0.8 m head height; world (0, 1.5, 1.5) puts the panel
            // above the eyebrow line. local (0, 0, 1.5) under TrackingSpace
            // keeps it dead ahead of the device. Same pattern as
            // TestPurpleCircleSetup.cs.
            var rig = GameObject.Find("OVRCameraRig");
            Transform parent = null;
            string parentName = "<world>";
            if (rig != null)
            {
                var ts = rig.transform.Find("TrackingSpace");
                if (ts != null) { parent = ts; parentName = "OVRCameraRig/TrackingSpace"; }
                else            { parent = rig.transform; parentName = "OVRCameraRig"; }
            }
            if (parent != null)
            {
                panel.transform.SetParent(parent, false);
                panel.transform.localPosition = new Vector3(0f, 0f, 1.5f);
                panel.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            }
            else
            {
                panel.transform.position = new Vector3(0f, 1.5f, 1.5f);
                panel.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            }
            Debug.Log($"[REPRO-1d] StereoCompositorSceneSetup: panel parented under {parentName} at local {panel.transform.localPosition} rot {panel.transform.localRotation.eulerAngles}");
            // Isolate panel onto IgnoreRaycast layer (2) so any URP camera
            // culling treats it deterministically. CompositionLayers render
            // outside URP, but layer-stack interactions have been seen to
            // affect XRDisplaySubsystem render-pass indexing (Step 3).
            panel.layer = LayerMask.NameToLayer("Ignore Raycast");
            panel.isStatic = false;

            var useStockType = System.Environment.GetEnvironmentVariable("STEREO_USE_STOCK_QUAD") == "1";
            Debug.Log($"[REPRO-1] StereoCompositorSceneSetup: about to AddComponent<CompositionLayer> on panel (useStockType={useStockType})");
            var compLayer = panel.AddComponent<CompositionLayer>();
            if (useStockType)
            {
                compLayer.ChangeLayerDataType<QuadLayerData>();
                var stockData = compLayer.LayerData as QuadLayerData;
                if (stockData != null)
                {
                    stockData.Size = new Vector2(2.0f, 1.125f);
                }
                Debug.Log("[StereoCompositorSceneSetup] Using STOCK QuadLayerData for crash repro test.");
            }
            else
            {
                compLayer.ChangeLayerDataType<StereoQuadLayerData>();
                var data = compLayer.LayerData as StereoQuadLayerData;
                if (data != null)
                {
                    data.Size = new Vector2(2.0f, 1.125f);
                }
                else
                {
                    Debug.LogError($"[StereoCompositorSceneSetup] LayerData was not StereoQuadLayerData after ChangeLayerDataType. Got: {compLayer.LayerData?.GetType()}");
                }
            }

            var tex = panel.AddComponent<TexturesExtension>();
            tex.sourceTexture = TexturesExtension.SourceTextureEnum.LocalTexture;
            tex.LeftTexture = testTexture;
            // Set RightTexture too so the handler's per-eye swapchain creation
            // doesn't dereference a null right-eye surface (vk::RenderSurface::
            // GetFormat crash signature). The handler still computes per-eye
            // SubImage rects from the same TopBottom layout — both eyes read
            // their slice from the same texture.
            tex.RightTexture = testTexture;

            // Strip CompositionOutline (debug-visualization helper auto-added
            // alongside CompositionLayer). It uses uGUI Canvas rendering,
            // which has been observed to trigger
            // CanvasProxy::SendPreWillRenderCanvases SEGVs at runtime when
            // combined with our custom layer handler. Not needed for runtime;
            // editor-only gizmos remain available via menu.
            foreach (var c in panel.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == "CompositionOutline")
                {
                    Object.DestroyImmediate(c);
                    Debug.Log("[REPRO-1c] StereoCompositorSceneSetup: removed auto-added CompositionOutline component.");
                }
            }

            // Verify panel has ONLY the components we expect — no MeshRenderer,
            // no MeshFilter, no LODGroup, no Canvas, nothing that triggers URP
            // or canvas-render traversal we don't intend.
            var components = panel.GetComponents<Component>();
            var compNames = string.Join(", ", components.Select(c => c.GetType().Name));
            Debug.Log($"[REPRO-1b] StereoCompositorSceneSetup: panel components: [{compNames}]");

            // PinRefreshRate: keep displayFrequency at 72 Hz to remove the
            // refresh-rate-oscillation variable from the SEGV diagnosis (Step 3).
            const string PinGoName = "[Compositor] PinRefreshRate";
            var existingPin = SceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(go => go.name == PinGoName);
            if (existingPin != null) Object.DestroyImmediate(existingPin);
            var pinGo = new GameObject(PinGoName);
            pinGo.AddComponent<PinRefreshRate>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[StereoCompositorSceneSetup] Authored '{PanelGoName}' in {ScenePath} with {testTexture.width}x{testTexture.height} TB test pattern.");
        }

        static Texture2D EnsureTestPatternAsset()
        {
            var dir = System.IO.Path.GetDirectoryName(TexturePath);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            if (!System.IO.File.Exists(TexturePath))
            {
                var t = BuildTestPattern(1024, 1024);
                System.IO.File.WriteAllBytes(TexturePath, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                AssetDatabase.ImportAsset(TexturePath, ImportAssetOptions.ForceSynchronousImport);
            }

            var importer = (TextureImporter)AssetImporter.GetAtPath(TexturePath);
            if (importer != null)
            {
                bool changed = false;
                if (importer.alphaIsTransparency) { importer.alphaIsTransparency = false; changed = true; }
                if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
                if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
                if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; changed = true; }
                if (importer.sRGBTexture) { importer.sRGBTexture = false; changed = true; }
                // FORCE-REVERT: `isReadable = true` and Android RGBA32 override caused a SIGSEGV
                // in Resources.FindObjectsOfTypeAll<CompositionLayer>. Restore defaults explicitly.
                if (importer.isReadable) { importer.isReadable = false; changed = true; }

                var androidSettings = importer.GetPlatformTextureSettings("Android");
                if (androidSettings.overridden)
                {
                    androidSettings.overridden = false;
                    importer.SetPlatformTextureSettings(androidSettings);
                    changed = true;
                }

                if (changed)
                {
                    importer.SaveAndReimport();
                }
            }

            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (asset == null)
                throw new System.Exception($"[StereoCompositorSceneSetup] Failed to load Texture2D at {TexturePath}");
            return asset;
        }

        static Texture2D BuildTestPattern(int width, int height)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var pixels = new Color32[width * height];
            int half = height / 2;
            var topColor = new Color32(220, 30, 30, 255);    // red
            var bottomColor = new Color32(30, 30, 220, 255); // blue

            for (int y = 0; y < height; y++)
            {
                var row = y >= half ? topColor : bottomColor;
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = row;
            }

            // White marker in TOP half (left eye) bottom-left of source crop
            DrawSquare(pixels, width, height, width / 4, (3 * height) / 4, 32, new Color32(255, 255, 255, 255));
            // Yellow marker in BOTTOM half (right eye) top-right of source crop
            DrawSquare(pixels, width, height, (3 * width) / 4, height / 4, 32, new Color32(255, 255, 0, 255));

            t.SetPixels32(pixels);
            t.Apply(false, false);
            return t;
        }

        static void DrawSquare(Color32[] pixels, int width, int height, int cx, int cy, int sz, Color32 c)
        {
            for (int dy = -sz / 2; dy < sz / 2; dy++)
            for (int dx = -sz / 2; dx < sz / 2; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (px >= 0 && px < width && py >= 0 && py < height)
                    pixels[py * width + px] = c;
            }
        }
    }
}
#endif

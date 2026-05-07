using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using UnityEngine;

namespace Quintar.StereoVideoCompositor
{
    /// <summary>
    /// Day 2 smoke test: at scene load, spawn a CompositionLayer GameObject driven by
    /// <see cref="StereoQuadLayerData"/>, sourcing from a procedurally-generated red/blue
    /// top-bottom test pattern. If both eyes see the same magenta, the per-eye split failed.
    /// If left eye sees red and right eye sees blue (or vice-versa, depending on TB convention),
    /// the per-eye split works and we can move on to AndroidSurface in day 3.
    /// </summary>
    public class StereoCompositorSpawner : MonoBehaviour
    {
        const string Tag = "StereoCompositorSpawner";

        // NOTE: Runtime spawning was abandoned — adding a CompositionLayer at runtime
        // SEGVs the framework's CompositionLayerManager refresh
        // (Object.FindObjectsByType<CompositionLayer> in CompositionLayerManager.cs:581).
        // Scene authoring is done at edit time by StereoCompositorSceneSetup.
        // This MonoBehaviour is left in place so the scene-authored GameObject can
        // optionally attach it for runtime tweaks; not auto-spawned.

        Texture2D m_Texture;
        GameObject m_PanelGo;

        void Start()
        {
            Debug.Log($"[{Tag}] Start. Building test panel.");

            m_Texture = BuildTestPattern(1024, 1024);

            m_PanelGo = new GameObject("StereoCompositorPanel");
            m_PanelGo.transform.position = new Vector3(0f, 1.5f, 1.5f);
            m_PanelGo.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            var compLayer = m_PanelGo.AddComponent<CompositionLayer>();
            compLayer.ChangeLayerDataType<StereoQuadLayerData>();

            var data = compLayer.LayerData as StereoQuadLayerData;
            if (data != null)
            {
                // Bisect mode: Size/StereoLayout are read-only constants.
            }
            else
            {
                Debug.LogError($"[{Tag}] LayerData was not StereoQuadLayerData after ChangeLayerDataType.");
            }

            var tex = m_PanelGo.AddComponent<TexturesExtension>();
            tex.sourceTexture = TexturesExtension.SourceTextureEnum.LocalTexture;
            tex.LeftTexture = m_Texture;

            Debug.Log($"[{Tag}] Panel spawned at {m_PanelGo.transform.position} with {m_Texture.width}x{m_Texture.height} TB test pattern.");
        }

        void OnDestroy()
        {
            if (m_Texture != null) Destroy(m_Texture);
            if (m_PanelGo != null) Destroy(m_PanelGo);
        }

        static Texture2D BuildTestPattern(int width, int height)
        {
            // Top half red, bottom half blue. Vertical stripe at the seam to confirm split lines up.
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;

            var pixels = new Color32[width * height];
            int half = height / 2;
            var topColor = new Color32(220, 30, 30, 255);    // red (top half = expected LEFT eye)
            var bottomColor = new Color32(30, 30, 220, 255); // blue (bottom half = expected RIGHT eye)

            for (int y = 0; y < height; y++)
            {
                var row = y >= half ? topColor : bottomColor;  // y=0 at bottom; top half is high y
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = row;
            }

            // Center marker: small white square to ensure left eye doesn't show two colors
            int cx = width / 4, cy = (3 * height) / 4, sz = 32;
            for (int dy = -sz / 2; dy < sz / 2; dy++)
            for (int dx = -sz / 2; dx < sz / 2; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (px >= 0 && px < width && py >= 0 && py < height)
                    pixels[py * width + px] = new Color32(255, 255, 255, 255);
            }

            // And a mirrored marker in the bottom half (right eye) at a different X to make sure
            // the right eye really is reading the bottom half, not duplicating the top.
            cx = (3 * width) / 4; cy = height / 4;
            for (int dy = -sz / 2; dy < sz / 2; dy++)
            for (int dx = -sz / 2; dx < sz / 2; dx++)
            {
                int px = cx + dx, py = cy + dy;
                if (px >= 0 && px < width && py >= 0 && py < height)
                    pixels[py * width + px] = new Color32(255, 255, 0, 255);
            }

            t.SetPixels32(pixels);
            t.Apply(false, false);
            return t;
        }
    }
}

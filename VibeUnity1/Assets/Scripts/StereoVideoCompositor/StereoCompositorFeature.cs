using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Quintar.StereoVideoCompositor
{
#if UNITY_EDITOR
    [UnityEditor.XR.OpenXR.Features.OpenXRFeature(
        UiName = "Stereo Video Compositor (Quintar)",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "Quintar",
        Desc = "Compositor-layer stereo MR video player. Enables XR_FB_composition_layer_alpha_blend so the mask/video destination-alpha blend math from the native Spatial SDK player can be expressed at the runtime compositor.",
        DocumentationLink = "",
        FeatureId = "ai.quintar.stereo-video-compositor",
        OpenxrExtensionStrings = "XR_FB_composition_layer_alpha_blend",
        Version = "0.1.0")]
#endif
    public class StereoCompositorFeature : OpenXRFeature
    {
        const string Tag = "StereoCompositor";

        bool m_LayerProviderStartedSubscribed;

        protected override void OnEnable()
        {
            Debug.Log($"[{Tag}] Feature.OnEnable. XR_FB_composition_layer_alpha_blend extension requested.");

            if (OpenXRLayerProvider.isStarted)
            {
                OnLayerProviderStarted();
            }
            else
            {
                OpenXRLayerProvider.Started += OnLayerProviderStarted;
                m_LayerProviderStartedSubscribed = true;
            }
        }

        protected override void OnDisable()
        {
            if (m_LayerProviderStartedSubscribed)
            {
                OpenXRLayerProvider.Started -= OnLayerProviderStarted;
                m_LayerProviderStartedSubscribed = false;
            }

            Debug.Log($"[{Tag}] Feature.OnDisable.");
        }

        void OnLayerProviderStarted()
        {
            Debug.Log($"[REPRO-2] [{Tag}] OpenXRLayerProvider.Started fired. Registering StereoQuadLayerHandler.");
            var handler = new StereoQuadLayerHandler();
            OpenXRLayerProvider.RegisterLayerHandler(typeof(StereoQuadLayerData), handler);
            Debug.Log($"[REPRO-2b] [{Tag}] StereoQuadLayerHandler registered for StereoQuadLayerData.");
        }

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            bool extEnabled = OpenXRRuntime.IsExtensionEnabled("XR_FB_composition_layer_alpha_blend");
            Debug.Log($"[{Tag}] OnInstanceCreate. XR_FB_composition_layer_alpha_blend enabled = {extEnabled}.");
            return base.OnInstanceCreate(xrInstance);
        }
    }
}

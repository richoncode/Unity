#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;

namespace Quintar.StereoVideoCompositor.Editor
{
    /// <summary>
    /// Ensures the StereoCompositorFeature is registered in the OpenXR Package Settings
    /// for Android and enabled at build time. Without this, the feature exists in the
    /// project but never gets loaded by the OpenXR runtime, so the extension is not
    /// requested and the handler registration never fires.
    /// </summary>
    public static class StereoCompositorBuildSetup
    {
        const string OurFeatureId = "ai.quintar.stereo-video-compositor";
        const string CompositionLayersFeatureId = "com.unity.openxr.feature.compositionlayers";

        public static void EnsureEnabledForAndroid()
        {
            // Recovery step 3: both our FB-alpha-blend feature AND the stock composition
            // layers feature enabled. Both are needed for Day 2 work.
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            EnsureFeatureEnabled(OurFeatureId);
            EnsureFeatureEnabled(CompositionLayersFeatureId);
            AssetDatabase.SaveAssets();
        }

        static void EnsureFeatureDisabled(string featureId)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, featureId);
            if (feature == null) return;
            if (feature.enabled)
            {
                feature.enabled = false;
                EditorUtility.SetDirty(feature);
                Debug.Log($"[StereoCompositorBuildSetup] DISABLED OpenXR feature '{featureId}' for Android.");
            }
        }

        static void EnsureFeatureEnabled(string featureId)
        {
            var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, featureId);
            if (feature == null)
            {
                Debug.LogError($"[StereoCompositorBuildSetup] Feature '{featureId}' not found after RefreshFeatures.");
                return;
            }

            if (!feature.enabled)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                Debug.Log($"[StereoCompositorBuildSetup] Enabled OpenXR feature '{featureId}' for Android.");
            }
            else
            {
                Debug.Log($"[StereoCompositorBuildSetup] OpenXR feature '{featureId}' already enabled for Android.");
            }
        }

        [MenuItem("Tools/Quintar/Enable StereoCompositor for Android")]
        public static void EnableMenuCommand()
        {
            EnsureEnabledForAndroid();
        }
    }
}
#endif

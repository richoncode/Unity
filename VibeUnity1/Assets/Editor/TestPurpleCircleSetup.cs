#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Quintar.StereoVideoCompositor.Editor
{
    /// <summary>
    /// Adds a 1 m diameter purple disc 1 m in front of the device for a simple
    /// visual verification test of the rendering pipeline. Plain GameObject
    /// (Cylinder primitive flattened, URP Unlit material) — deliberately NOT a
    /// CompositionLayer, so this exercises the basic Unity URP path without
    /// touching the still-unstable CompositionLayer subsystem.
    ///
    /// Wired into AutoBuilder so each build gets a fresh disc. Toggle with the
    /// TEST_PURPLE_CIRCLE env var (default "1"; set to "0" to skip).
    ///
    /// Run via: Tools → Quintar → Setup Test Purple Circle
    /// </summary>
    public static class TestPurpleCircleSetup
    {
        const string ScenePath = "Assets/Scenes/MR_Passthrough_Scene.unity";
        const string GoName = "[Test] PurpleCircle";

        [MenuItem("Tools/Quintar/Setup Test Purple Circle")]
        public static void SetupMenuCommand() { EnsureInScene(); }

        public static bool Skip => System.Environment.GetEnvironmentVariable("TEST_PURPLE_CIRCLE") == "0";

        public static void EnsureInScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Remove any existing disc, whether at root or nested.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (t != null && t.gameObject != null && t.gameObject.name == GoName)
                    Object.DestroyImmediate(t.gameObject);
            }

            if (Skip)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                Debug.Log("[TestPurpleCircleSetup] TEST_PURPLE_CIRCLE=0: skipped, scene saved without disc.");
                return;
            }

            // Find OVRCameraRig.TrackingSpace; fall back to the rig root, then world.
            var rig = GameObject.Find("OVRCameraRig");
            Transform parent = null;
            string parentName = "<world>";
            if (rig != null)
            {
                var ts = rig.transform.Find("TrackingSpace");
                if (ts != null) { parent = ts; parentName = "OVRCameraRig/TrackingSpace"; }
                else            { parent = rig.transform; parentName = "OVRCameraRig"; }
            }

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = GoName;
            if (parent != null)
                disc.transform.SetParent(parent, worldPositionStays: false);

            // Cylinder primitive: 1 m diameter, 2 m tall, axis along Y.
            // Scale Y to 0.01 → 0.02 m thick disc.
            // Rotate 90° about X → axis now along Z, flat circular faces face ±Z.
            // Position local (0, 0, 1) → 1 m in front of the device's tracking-space origin.
            disc.transform.localScale = new Vector3(1f, 0.01f, 1f);
            disc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            disc.transform.localPosition = new Vector3(0f, 0f, 1f);

            // No collider needed for a visual marker.
            var col = disc.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);

            // Pure yellow, Unlit so it doesn't depend on scene lighting.
            // (Class name still says "Purple" — kept stable to avoid asmdef churn;
            // color is the only thing that varies iteration-to-iteration.)
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var rend = disc.GetComponent<Renderer>();
            var color = new Color(1f, 1f, 0f, 1f); // yellow
            var mat = new Material(shader) { color = color };
            // URP Unlit uses _BaseColor; legacy Unlit/Color uses _Color. Set both safely.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     color);
            rend.sharedMaterial = mat;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log($"[TestPurpleCircleSetup] Added '{GoName}' under {parentName} at local (0,0,1), 1 m diameter, yellow Unlit (shader={shader.name}).");
        }
    }
}
#endif

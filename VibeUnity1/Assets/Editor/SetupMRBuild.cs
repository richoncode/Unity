using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

#if UNITY_EDITOR
public class SetupMRBuild : Editor
{
    [MenuItem("VibeUnity/Setup MR Quest Scene")]
    public static void SetupScene()
    {
        // 1. Switch Build Target to Android
        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        // 2. Create New Scene
        Scene newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 3. Setup Lighting
        GameObject dirLight = new GameObject("Directional Light");
        Light light = dirLight.AddComponent<Light>();
        light.type = LightType.Directional;
        dirLight.transform.rotation = Quaternion.Euler(50, -30, 0);

        // 4. Instantiate OVRCameraRig
        GameObject ovrCameraRigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
        GameObject rig = null;
        
        if (ovrCameraRigPrefab != null)
        {
            rig = (GameObject)PrefabUtility.InstantiatePrefab(ovrCameraRigPrefab);
        }
        else
        {
            string[] guids = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                ovrCameraRigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                rig = (GameObject)PrefabUtility.InstantiatePrefab(ovrCameraRigPrefab);
            }
            else
            {
                Debug.LogError("Could not find OVRCameraRig prefab. Please make sure the Meta XR Core SDK is installed.");
                return;
            }
        }

        // 5. Configure OVRManager for Passthrough
        var ovrManager = rig.GetComponent("OVRManager");
        if (ovrManager != null)
        {
            SerializedObject soManager = new SerializedObject(ovrManager);
            soManager.FindProperty("isInsightPassthroughEnabled").boolValue = true;
            soManager.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("OVRManager not found on rig. Passthrough may not be fully enabled.");
        }

        // Add OVRPassthroughLayer
        System.Type passthroughLayerType = System.Type.GetType("OVRPassthroughLayer, Oculus.VR");
        if (passthroughLayerType != null)
        {
            var passthroughLayer = rig.GetComponent(passthroughLayerType);
            if (passthroughLayer == null)
            {
                passthroughLayer = rig.AddComponent(passthroughLayerType);
            }

            SerializedObject soLayer = new SerializedObject(passthroughLayer);
            var prop = soLayer.FindProperty("placement") ?? soLayer.FindProperty("_placement");
            if (prop != null) prop.enumValueIndex = 1; // Underlay
            soLayer.ApplyModifiedProperties();
        }
        else
        {
            // Fallback: try finding type in current assembly
            passthroughLayerType = System.Type.GetType("OVRPassthroughLayer, Assembly-CSharp");
            if (passthroughLayerType != null)
            {
                var passthroughLayer = rig.GetComponent(passthroughLayerType);
                if (passthroughLayer == null) passthroughLayer = rig.AddComponent(passthroughLayerType);
                SerializedObject soLayer = new SerializedObject(passthroughLayer);
                var prop = soLayer.FindProperty("placement") ?? soLayer.FindProperty("_placement");
                if (prop != null) prop.enumValueIndex = 1; // Underlay
                soLayer.ApplyModifiedProperties();
            }
            else
            {
                Debug.LogWarning("OVRPassthroughLayer type not found. Passthrough layer must be added manually.");
            }
        }

        // Configure Camera background to clear
        Camera centerCamera = rig.GetComponentInChildren<Camera>();
        if (centerCamera != null)
        {
            centerCamera.clearFlags = CameraClearFlags.SolidColor;
            centerCamera.backgroundColor = new Color(0, 0, 0, 0);
        }

        // 6. Spawn Hovering Objects
        SpawnHoveringObject(PrimitiveType.Cube, new Vector3(-0.5f, 1.2f, 1.5f), Color.red, "Hovering Cube");
        SpawnHoveringObject(PrimitiveType.Sphere, new Vector3(0.5f, 1.2f, 1.5f), Color.blue, "Hovering Sphere");
        SpawnHoveringObject(PrimitiveType.Cylinder, new Vector3(0, 1.5f, 2.0f), Color.green, "Hovering Cylinder");

        // 7. Save Scene
        if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
        {
            AssetDatabase.CreateFolder("Assets", "Scenes");
        }
        string scenePath = "Assets/Scenes/MR_Passthrough_Scene.unity";
        EditorSceneManager.SaveScene(newScene, scenePath);

        // 8. Add to Build Settings
        List<EditorBuildSettingsScene> scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        bool sceneExists = scenes.Exists(s => s.path == scenePath);

        if (!sceneExists)
        {
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        Debug.Log("✅ MR Passthrough Scene created successfully at " + scenePath + ".\n" +
                  "If you encounter any compiler errors, you may need to adjust the assembly references.");
    }

    private static void SpawnHoveringObject(PrimitiveType type, Vector3 position, Color color, string name)
    {
        GameObject obj = GameObject.CreatePrimitive(type);
        obj.name = name;
        obj.transform.position = position;
        obj.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);

        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            
            Material mat = new Material(shader);
            mat.color = color;
            renderer.material = mat;
        }
    }
}
#endif

using UnityEngine;
using UnityEngine.Video;
using UnityEngine.Networking;
using System.Collections;

public class StereoVideoSetup : MonoBehaviour
{
    static StereoVideoSetup()
    {
        LogStatic("StereoVideoSetup: Static constructor called.");
    }

    // Disabled during Day 2 of compositor-layer migration: this legacy spawner creates a
    // SurfaceTexture-backed quad that doesn't work under Vulkan (see STEREO_COMPOSITOR_PLAN.md).
    // The replacement is the Unity composition layer authored at edit-time by
    // StereoCompositorSceneSetup. Re-enable only if reverting to the legacy path.
    // [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnAfterSceneLoad()
    {
        LogStatic("StereoVideoManager: OnAfterSceneLoad DISABLED (legacy SurfaceTexture path retired).");
    }

    private static void LogStatic(string msg)
    {
        Debug.Log(msg);
        System.Console.WriteLine(msg);
        #if UNITY_ANDROID && !UNITY_EDITOR
        try {
            using (var log = new AndroidJavaClass("android.util.Log")) {
                log.CallStatic<int>("d", "UnityST", msg);
            }
        } catch { }
        #endif
    }
}

public class StereoVideoManager : MonoBehaviour
{
    private string videoUrl = "https://streams.quintar.ai/nba-2025/20250216/newreg/Main_Final_8m_hardmasked_nvenc_tb_20Mbps/index.m3u8";
    private bool useTestUrl = false; 

    private string maskUrl = "https://nba-stage.configs.quintar.ai/meta/2025/AllStar_MidCourt_Jumbo10.png";

    private AndroidJavaObject exoBridge;
    private Texture2D nativeTexture;
    private int nativeTexId;
    private bool isExoPlaying = false;
    private bool isBridgeInitialized = false;
    private MeshRenderer meshRenderer;
    private Material stereoMaterial;
    private OVRCameraRig cameraRig;

    IEnumerator Start()
    {
        Log("StereoVideoManager: Starting initialization...");

        // Find OVRCameraRig
        int retries = 20;
        while (cameraRig == null && retries > 0)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
            if (cameraRig == null)
            {
                yield return new WaitForSeconds(0.2f);
                retries--;
            }
        }

        // Setup Quad
        GameObject display = GameObject.CreatePrimitive(PrimitiveType.Quad);
        display.name = "StereoVideoDisplay";
        Destroy(display.GetComponent<MeshCollider>());
        
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            Log("StereoVideoManager: OVRCameraRig found. Parenting to trackingSpace.");
            display.transform.SetParent(cameraRig.trackingSpace, false);
            display.transform.localPosition = new Vector3(0, 0.5f, 1.5f);
            display.transform.localRotation = Quaternion.Euler(0, 180, 0); 
        }
        else
        {
            Log("StereoVideoManager WARNING: OVRCameraRig NOT found. Using world space.");
            display.transform.position = new Vector3(0, 0.5f, 1.5f);
            display.transform.rotation = Quaternion.Euler(0, 180, 0);
        }
        
        display.transform.localScale = new Vector3(2.66f, 1.5f, 1.0f);
        Log("StereoVideoManager: Quad placed at 1.5m distance, 0.5m height.");

        meshRenderer = display.GetComponent<MeshRenderer>();
        Shader shader = Resources.Load<Shader>("Shaders/StereoMaskedVideo");
        if (shader == null) shader = Shader.Find("Custom/StereoMaskedVideo");
        
        if (shader != null)
        {
            stereoMaterial = new Material(shader);
            Log("StereoVideoManager: Shader loaded successfully.");
        }
        else
        {
            Log("StereoVideoManager ERROR: Could not find shader!");
            stereoMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        }
        meshRenderer.material = stereoMaterial;
        if (stereoMaterial.HasProperty("_BaseColor")) stereoMaterial.SetColor("_BaseColor", Color.magenta);
        else if (stereoMaterial.HasProperty("_Color")) stereoMaterial.SetColor("_Color", Color.magenta);

        yield return null; 
        yield return null; 

        // Create the texture in Unity first
        nativeTexture = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
        nativeTexture.filterMode = FilterMode.Bilinear;
        nativeTexture.wrapMode = TextureWrapMode.Clamp;
        // The texture doesn't have an ID until it's "used" or Apply() is called in some versions
        nativeTexture.Apply(); 
        
        long ptrValue = (long)nativeTexture.GetNativeTexturePtr();
        nativeTexId = (int)(ptrValue & 0xFFFFFFFF);
        Log("StereoVideoManager: Unity Texture ID Created: " + nativeTexId);

        // Setup ExoPlayer via Bridge
        string currentUrl = videoUrl;
        Log("StereoVideoManager: Spawning ExoPlayerBridge with: " + currentUrl);

        #if UNITY_ANDROID && !UNITY_EDITOR
        try {
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer")) {
                var currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                exoBridge = new AndroidJavaObject("com.quintar.video.ExoPlayerBridge", currentActivity, currentUrl, nativeTexId);
            }
        } catch (System.Exception e) {
            Log("StereoVideoManager FATAL: JNI Bridge failed: " + e.Message);
        }
        #endif

        yield return StartCoroutine(LoadMask(maskUrl));
        Log("StereoVideoManager: Initialization Coroutine Finished.");
    }

    private void Update()
    {
        if (exoBridge == null) return;

        // Stage 1: Wait for Initialized
        if (!isBridgeInitialized && exoBridge.Call<bool>("isInitialized"))
        {
            Log("StereoVideoManager: ExoPlayer Bridge Initialized.");
            isBridgeInitialized = true;
            if (stereoMaterial != null)
            {
                stereoMaterial.SetTexture("_MainTex", nativeTexture);
                if (stereoMaterial.HasProperty("_BaseColor")) stereoMaterial.SetColor("_BaseColor", Color.cyan);
            }
        }

        // Stage 2: Wait for Prepared (Video ready to play)
        if (isBridgeInitialized && !isExoPlaying && exoBridge.Call<bool>("isPrepared"))
        {
            Log("StereoVideoManager: ExoPlayer Prepared. Playing...");
            exoBridge.Call("play");
            isExoPlaying = true;
            if (stereoMaterial.HasProperty("_BaseColor")) stereoMaterial.SetColor("_BaseColor", Color.white);
        }

        // Stage 3: Update frames
        if (isExoPlaying)
        {
            exoBridge.Call("updateTexture");
            
            // Add pulse for debug
            if (stereoMaterial != null)
            {
                float pulse = Mathf.Sin(Time.time * 5f) * 0.5f + 0.5f;
                stereoMaterial.SetFloat("_Pulse", pulse);
            }
        }
    }

    private void OnDestroy()
    {
        if (exoBridge != null)
        {
            exoBridge.Call("release");
            exoBridge = null;
        }
    }

    private void Log(string msg)
    {
        Debug.Log("[UnityST] " + msg);
        try
        {
            string path = Application.persistentDataPath + "/unity_st_log.txt";
            System.IO.File.AppendAllText(path, "[" + System.DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\n");
        }
        catch { }
        System.Console.WriteLine(msg);
        #if UNITY_ANDROID && !UNITY_EDITOR
        try {
            using (var log = new AndroidJavaClass("android.util.Log")) {
                log.CallStatic<int>("d", "UnityST", msg);
            }
        } catch { }
        #endif
    }

    IEnumerator LoadMask(string url)
    {
        Log("StereoVideoManager: Downloading mask from " + url);
        using (UnityWebRequest uwr = UnityWebRequestTexture.GetTexture(url))
        {
            yield return uwr.SendWebRequest();

            if (uwr.result != UnityWebRequest.Result.Success)
            {
                Log("StereoVideoManager ERROR: Mask download failed: " + uwr.error);
            }
            else
            {
                Texture2D maskTexture = DownloadHandlerTexture.GetContent(uwr);
                Log("StereoVideoManager: Mask downloaded. Size: " + maskTexture.width + "x" + maskTexture.height);
                if (stereoMaterial != null)
                    stereoMaterial.SetTexture("_MaskTex", maskTexture);
            }
        }
    }
}

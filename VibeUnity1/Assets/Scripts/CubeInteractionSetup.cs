using UnityEngine;
using System.Collections;

public class CubeInteractionSetup : MonoBehaviour
{
    // Disabled during compositor-layer migration: this spawner's CubeInteractionManager.Start
    // throws ArgumentNullException at `new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"))`
    // when neither shader is included in the build (Android URP stripping). The LineRenderer
    // ends up with a null material which propagates into URP's command buffer
    // (ScriptableRenderContext::ExecuteScriptableRenderLoop → ShaderPropertySheet::AddNewProperty)
    // and SEGVs. Re-enable only after the shader references are fixed (e.g. by adding the URP
    // Unlit shader to the Always Included Shaders list, or referencing it from a material asset).
    // [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void OnAfterSceneLoad()
    {
        // no-op
    }
}

public class CubeInteractionManager : MonoBehaviour
{
    private LineRenderer lineRenderer;
    private OVRCameraRig cameraRig;
    private CubeInteractable currentHover;

    IEnumerator Start()
    {
        // Wait until OVRCameraRig is available
        while (cameraRig == null)
        {
            cameraRig = FindObjectOfType<OVRCameraRig>();
            yield return null;
        }

        // Create 3 cubes in a row left to right, placed lower to avoid the existing shapes
        CreateCube(new Vector3(-0.8f, 0.8f, 1.5f)); // Left
        CreateCube(new Vector3(0.0f, 0.8f, 1.5f));  // Center
        CreateCube(new Vector3(0.8f, 0.8f, 1.5f));  // Right

        // Setup LineRenderer for the pointer ray
        GameObject pointerGo = new GameObject("PointerRay");
        pointerGo.transform.SetParent(cameraRig.trackingSpace);
        pointerGo.transform.localPosition = Vector3.zero;
        pointerGo.transform.localRotation = Quaternion.identity;

        lineRenderer = pointerGo.AddComponent<LineRenderer>();
        lineRenderer.startWidth = 0.01f;
        lineRenderer.endWidth = 0.01f;
        lineRenderer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"));
        if (lineRenderer.material.HasProperty("_Color"))
        {
            lineRenderer.material.color = Color.red;
        }
        else if (lineRenderer.material.HasProperty("_BaseColor"))
        {
            lineRenderer.material.SetColor("_BaseColor", Color.red);
        }
        lineRenderer.positionCount = 2;
    }

    void CreateCube(Vector3 localPosition)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
        
        // Position relative to the tracking space so they show up consistently in front of the user
        if (cameraRig != null && cameraRig.trackingSpace != null)
        {
            cube.transform.SetParent(cameraRig.trackingSpace);
            cube.transform.localPosition = localPosition;
        }
        else
        {
            cube.transform.position = localPosition;
        }

        CubeInteractable interactable = cube.AddComponent<CubeInteractable>();
        
        if (cube.GetComponent<Collider>() == null)
        {
            cube.AddComponent<BoxCollider>();
        }
    }

    void Update()
    {
        if (cameraRig == null || lineRenderer == null) return;

        // Try controller anchor first, fallback to hand anchor
        Transform controllerTransform = cameraRig.rightControllerAnchor;
        if (!controllerTransform.gameObject.activeInHierarchy || OVRInput.GetActiveController() == OVRInput.Controller.Hands)
        {
            controllerTransform = cameraRig.rightHandAnchor;
        }
        
        if (controllerTransform == null) return;

        Vector3 rayOrigin = controllerTransform.position;
        Vector3 rayDirection = controllerTransform.forward;
        
        // Adjust ray direction slightly if it's the hand anchor, as hands forward vector might be weird
        if (controllerTransform == cameraRig.rightHandAnchor)
        {
            // Usually pointing with index finger is roughly the forward vector of the hand, but we can just use forward
            // The user will see the red line and adjust
        }

        lineRenderer.SetPosition(0, rayOrigin);

        RaycastHit hit;
        if (Physics.Raycast(rayOrigin, rayDirection, out hit, 10f))
        {
            lineRenderer.SetPosition(1, hit.point);
            
            CubeInteractable interactable = hit.collider.GetComponent<CubeInteractable>();
            
            if (interactable != null)
            {
                if (currentHover != interactable)
                {
                    if (currentHover != null) currentHover.OnHoverExit();
                    currentHover = interactable;
                    currentHover.OnHoverEnter();
                }

                // Check for click/select (IndexTrigger or A button, or pinch for hands)
                bool isPinching = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) || 
                                  OVRInput.GetDown(OVRInput.RawButton.A);
                
                if (isPinching)
                {
                    currentHover.OnSelect();
                }
            }
            else
            {
                if (currentHover != null)
                {
                    currentHover.OnHoverExit();
                    currentHover = null;
                }
            }
        }
        else
        {
            lineRenderer.SetPosition(1, rayOrigin + rayDirection * 10f);
            if (currentHover != null)
            {
                currentHover.OnHoverExit();
                currentHover = null;
            }
        }
    }
}

public class CubeInteractable : MonoBehaviour
{
    private MeshRenderer meshRenderer;
    private Material material;
    private int colorIndex = 0;
    private Color[] colors = { Color.red, Color.green, Color.blue };
    
    private bool isHovered = false;

    void Start()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
        if (litShader == null) litShader = Shader.Find("Standard");
        material = new Material(litShader);
        meshRenderer.material = material;
        
        UpdateColor();
    }

    public void OnHoverEnter()
    {
        isHovered = true;
        UpdateGlow();
    }

    public void OnHoverExit()
    {
        isHovered = false;
        UpdateGlow();
    }

    public void OnSelect()
    {
        colorIndex = (colorIndex + 1) % colors.Length;
        UpdateColor();
    }

    void UpdateColor()
    {
        Color baseColor = colors[colorIndex];
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", baseColor);
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", baseColor);
        }
        UpdateGlow();
    }

    void UpdateGlow()
    {
        if (material == null) return;

        if (isHovered)
        {
            material.EnableKeyword("_EMISSION");
            // Add brightness for glow
            Color emissionColor = colors[colorIndex] * 1.5f; 
            
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", emissionColor);
            }
        }
        else
        {
            material.DisableKeyword("_EMISSION");
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", Color.black);
            }
        }
    }
}

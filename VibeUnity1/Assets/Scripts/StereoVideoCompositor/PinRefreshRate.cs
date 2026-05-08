using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Quintar.StereoVideoCompositor
{
    /// <summary>
    /// Diagnostic only (Step 3 of plan): logs the current XR display
    /// refresh rate at session start. Setting the rate would require
    /// OpenXR's XR_FB_display_refresh_rate extension wired explicitly;
    /// not part of Step 3's scope. The log lets us observe whether
    /// frequency oscillation correlates with the SEGV we're chasing.
    /// </summary>
    public class PinRefreshRate : MonoBehaviour
    {
        public float TargetHz = 72f;

        void Start()
        {
            var subsystems = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(subsystems);
            if (subsystems.Count == 0)
            {
                Debug.LogWarning("[PinRefreshRate] No XRDisplaySubsystem at Start; cannot read refresh rate.");
                return;
            }
            float current;
            subsystems[0].TryGetDisplayRefreshRate(out current);
            Debug.Log($"[PinRefreshRate] XR display refresh rate at Start: {current} Hz (target was {TargetHz} Hz; pinning not implemented yet)");
        }
    }
}

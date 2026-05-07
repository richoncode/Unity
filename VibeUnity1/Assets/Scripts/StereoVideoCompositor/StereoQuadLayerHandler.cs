using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;
using UnityEngine.XR.OpenXR.NativeTypes;

namespace Quintar.StereoVideoCompositor
{
    /// <summary>
    /// Custom layer handler for <see cref="StereoQuadLayerData"/>. Subclasses
    /// <see cref="OpenXRCustomLayerHandler{T}"/> to inherit swapchain creation and texture
    /// blit lifecycle, but overrides <see cref="OnUpdate"/> to emit TWO
    /// <see cref="XrCompositionLayerQuad"/> submissions per active layer (one per eye, with
    /// <c>EyeVisibility=1</c>/<c>=2</c> and <c>SubImage.ImageRect</c> addressing the correct
    /// half of the source texture per the configured <see cref="StereoLayout"/>).
    /// </summary>
    public class StereoQuadLayerHandler : OpenXRCustomLayerHandler<XrCompositionLayerQuad>
    {
        const string Tag = "StereoQuadLayerHandler";

        readonly Dictionary<int, ActiveLayerState> m_ActiveLayerStates = new();

        struct ActiveLayerState
        {
            public CompositionLayerManager.LayerInfo Info;
            public int SourceWidth;
            public int SourceHeight;
        }

        protected override bool CreateSwapchain(CompositionLayerManager.LayerInfo layer, out SwapchainCreateInfo swapchainCreateInfo)
        {
            Debug.Log($"[{Tag}] CreateSwapchain id={layer.Id}");
            unsafe
            {
                var tex = layer.Layer.GetComponent<TexturesExtension>();
                if (tex == null || !tex.enabled || tex.LeftTexture == null || tex.sourceTexture != TexturesExtension.SourceTextureEnum.LocalTexture)
                {
                    Debug.LogError($"[{Tag}] CreateSwapchain id={layer.Id} FAILED: tex={tex?.GetType().Name} enabled={tex?.enabled} LeftTex={(tex?.LeftTexture==null?"null":tex.LeftTexture.name)} src={tex?.sourceTexture}");
                    swapchainCreateInfo = default;
                    return false;
                }

                var xrCreateInfo = new XrSwapchainCreateInfo
                {
                    Type = (uint)XrStructureType.XR_TYPE_SWAPCHAIN_CREATE_INFO,
                    Next = OpenXRLayerUtility.GetExtensionsChain(layer, CompositionLayerExtension.ExtensionTarget.Swapchain),
                    CreateFlags = 0,
                    UsageFlags = (ulong)(XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_SAMPLED_BIT | XrSwapchainUsageFlags.XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT),
                    Format = OpenXRLayerUtility.GetDefaultColorFormat(),
                    SampleCount = 1,
                    Width = (uint)tex.LeftTexture.width,
                    Height = (uint)tex.LeftTexture.height,
                    FaceCount = 1,
                    ArraySize = 1,
                    MipCount = (uint)Mathf.Max(1, tex.LeftTexture.mipmapCount),
                };

                swapchainCreateInfo = new SwapchainCreateInfo(xrCreateInfo, isExternalSurface: false, isStereo: false);
                return true;
            }
        }

        protected override bool CreateNativeLayer(CompositionLayerManager.LayerInfo layer, SwapchainCreatedOutput swapchainOutput, out XrCompositionLayerQuad nativeLayer)
        {
            Debug.Log($"[{Tag}] CreateNativeLayer id={layer.Id} swapchainHandle={swapchainOutput.handle}");
            unsafe
            {
                var tex = layer.Layer.GetComponent<TexturesExtension>();
                if (tex == null || !tex.enabled || tex.LeftTexture == null)
                {
                    Debug.LogError($"[{Tag}] CreateNativeLayer id={layer.Id} aborted: missing TexturesExtension/LeftTexture");
                    nativeLayer = default;
                    return false;
                }

                var data = layer.Layer.LayerData as StereoQuadLayerData;
                var transform = layer.Layer.GetComponent<Transform>();
                var size = data.GetScaledSize(transform.lossyScale);
                var pose = OpenXRUtility.ComputePoseToWorldSpace(transform, CompositionLayerManager.mainCameraCache);

                nativeLayer = new XrCompositionLayerQuad
                {
                    Type = (uint)XrStructureType.XR_TYPE_COMPOSITION_LAYER_QUAD,
                    Next = OpenXRLayerUtility.GetExtensionsChain(layer, CompositionLayerExtension.ExtensionTarget.Layer),
                    LayerFlags = data.BlendType == BlendType.Premultiply
                        ? XrCompositionLayerFlags.SourceAlpha
                        : XrCompositionLayerFlags.SourceAlpha | XrCompositionLayerFlags.UnPremultipliedAlpha,
                    Space = OpenXRLayerUtility.GetCurrentAppSpace(),
                    EyeVisibility = 0, // overridden per-eye in OnUpdate
                    SubImage = new XrSwapchainSubImage
                    {
                        Swapchain = swapchainOutput.handle,
                        ImageArrayIndex = 0,
                        ImageRect = new XrRect2Di
                        {
                            Offset = new XrOffset2Di { X = 0, Y = 0 },
                            Extent = new XrExtent2Di { Width = tex.LeftTexture.width, Height = tex.LeftTexture.height },
                        },
                    },
                    Pose = new XrPosef(pose.position, pose.rotation),
                    Size = new XrExtent2Df { Width = size.x, Height = size.y },
                };
                return true;
            }
        }

        protected override bool ModifyNativeLayer(CompositionLayerManager.LayerInfo layerInfo, ref XrCompositionLayerQuad nativeLayer)
        {
            var tex = layerInfo.Layer.GetComponent<TexturesExtension>();
            if (tex == null || !tex.enabled || tex.LeftTexture == null)
                return false;

            var data = layerInfo.Layer.LayerData as StereoQuadLayerData;
            var transform = layerInfo.Layer.GetComponent<Transform>();
            var size = data.GetScaledSize(transform.lossyScale);
            var pose = OpenXRUtility.ComputePoseToWorldSpace(transform, CompositionLayerManager.mainCameraCache);

            nativeLayer.Pose = new XrPosef(pose.position, pose.rotation);
            nativeLayer.Size = new XrExtent2Df { Width = size.x, Height = size.y };
            nativeLayer.SubImage.ImageRect = new XrRect2Di
            {
                Offset = new XrOffset2Di { X = 0, Y = 0 },
                Extent = new XrExtent2Di { Width = tex.LeftTexture.width, Height = tex.LeftTexture.height },
            };

            unsafe
            {
                nativeLayer.Next = OpenXRLayerUtility.GetExtensionsChain(layerInfo, CompositionLayerExtension.ExtensionTarget.Layer);
            }
            return true;
        }

        int m_SetActiveLogCount;
        public override void SetActiveLayer(CompositionLayerManager.LayerInfo layerInfo)
        {
            if (m_SetActiveLogCount < 5)
            {
                Debug.Log($"[{Tag}] SetActiveLayer id={layerInfo.Id} order={layerInfo.Layer.Order}");
                m_SetActiveLogCount++;
            }
            base.SetActiveLayer(layerInfo);

            var tex = layerInfo.Layer.GetComponent<TexturesExtension>();
            if (tex == null || !tex.enabled || tex.LeftTexture == null)
                return;

            m_ActiveLayerStates[layerInfo.Id] = new ActiveLayerState
            {
                Info = layerInfo,
                SourceWidth = tex.LeftTexture.width,
                SourceHeight = tex.LeftTexture.height,
            };
        }

        public override void RemoveLayer(int removedLayerId)
        {
            m_ActiveLayerStates.Remove(removedLayerId);
            base.RemoveLayer(removedLayerId);
        }

        public override void OnUpdate()
        {
            // Drain the actions queue ourselves (mirrors base.OnUpdate behavior) so the
            // CreateSwapchain → CreateNativeLayer dispatch fires correctly.
            while (actionsForMainThread.Count > 0)
            {
                if (actionsForMainThread.TryDequeue(out var action))
                    action();
            }

            if (m_ActiveLayerStates.Count == 0)
                return;

            var perFrame = new NativeArray<XrCompositionLayerQuad>(m_ActiveLayerStates.Count * 2, Allocator.Temp);
            var orders = new NativeArray<int>(m_ActiveLayerStates.Count * 2, Allocator.Temp);

            try
            {
                int writeIndex = 0;
                foreach (var kvp in m_ActiveLayerStates)
                {
                    if (!m_nativeLayers.TryGetValue(kvp.Key, out var template))
                        continue;

                    var state = kvp.Value;
                    var data = state.Info.Layer.LayerData as StereoQuadLayerData;
                    if (data == null)
                        continue;

                    ComputeEyeRects(data.StereoLayout, state.SourceWidth, state.SourceHeight,
                        out var leftRect, out var rightRect);

                    var leftLayer = template;
                    leftLayer.EyeVisibility = data.StereoLayout == StereoLayout.Mono ? 0u : 1u;
                    leftLayer.SubImage.ImageRect = leftRect;

                    var rightLayer = template;
                    rightLayer.EyeVisibility = data.StereoLayout == StereoLayout.Mono ? 0u : 2u;
                    rightLayer.SubImage.ImageRect = rightRect;

                    perFrame[writeIndex] = leftLayer;
                    orders[writeIndex] = state.Info.Layer.Order;
                    writeIndex++;

                    if (data.StereoLayout != StereoLayout.Mono)
                    {
                        perFrame[writeIndex] = rightLayer;
                        orders[writeIndex] = state.Info.Layer.Order;
                        writeIndex++;
                    }
                }

                if (writeIndex > 0 && CompositionLayerManager.Instance != null)
                {
                    unsafe
                    {
                        OpenXRLayerUtility.AddActiveLayersToEndFrame(
                            perFrame.GetUnsafePtr(),
                            orders.GetUnsafePtr(),
                            writeIndex,
                            UnsafeUtility.SizeOf<XrCompositionLayerQuad>());
                    }
                }
            }
            finally
            {
                perFrame.Dispose();
                orders.Dispose();
                m_ActiveLayerStates.Clear();
            }
        }

        static void ComputeEyeRects(StereoLayout layout, int srcWidth, int srcHeight, out XrRect2Di leftRect, out XrRect2Di rightRect)
        {
            switch (layout)
            {
                case StereoLayout.TopBottom:
                    // Convention: Y=0 at bottom (OpenXR / GL). Top half = upper half = Y in [H/2, H).
                    // Native Spatial SDK convention paired with `_tb_` content: top = left eye.
                    leftRect = new XrRect2Di
                    {
                        Offset = new XrOffset2Di { X = 0, Y = srcHeight / 2 },
                        Extent = new XrExtent2Di { Width = srcWidth, Height = srcHeight / 2 },
                    };
                    rightRect = new XrRect2Di
                    {
                        Offset = new XrOffset2Di { X = 0, Y = 0 },
                        Extent = new XrExtent2Di { Width = srcWidth, Height = srcHeight / 2 },
                    };
                    break;

                case StereoLayout.LeftRight:
                    leftRect = new XrRect2Di
                    {
                        Offset = new XrOffset2Di { X = 0, Y = 0 },
                        Extent = new XrExtent2Di { Width = srcWidth / 2, Height = srcHeight },
                    };
                    rightRect = new XrRect2Di
                    {
                        Offset = new XrOffset2Di { X = srcWidth / 2, Y = 0 },
                        Extent = new XrExtent2Di { Width = srcWidth / 2, Height = srcHeight },
                    };
                    break;

                default: // Mono
                    leftRect = new XrRect2Di
                    {
                        Offset = new XrOffset2Di { X = 0, Y = 0 },
                        Extent = new XrExtent2Di { Width = srcWidth, Height = srcHeight },
                    };
                    rightRect = leftRect;
                    break;
            }
        }
    }
}

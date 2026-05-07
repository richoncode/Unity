using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.XR.CompositionLayers;
using UnityEngine;

namespace Quintar.StereoVideoCompositor
{
    public enum XrBlendFactorFB : uint
    {
        Zero = 0,
        One = 1,
        SrcAlpha = 2,
        OneMinusSrcAlpha = 3,
        DstAlpha = 4,
        OneMinusDstAlpha = 5,
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("XR/Composition Layers/Extensions/Alpha Blend (FB)")]
    public class AlphaBlendFBExtension : CompositionLayerExtension
    {
        const uint XR_TYPE_COMPOSITION_LAYER_ALPHA_BLEND_FB = 1000041001;

        public override ExtensionTarget Target => ExtensionTarget.Layer;

        [SerializeField] XrBlendFactorFB m_SrcFactorColor = XrBlendFactorFB.SrcAlpha;
        [SerializeField] XrBlendFactorFB m_DstFactorColor = XrBlendFactorFB.OneMinusSrcAlpha;
        [SerializeField] XrBlendFactorFB m_SrcFactorAlpha = XrBlendFactorFB.One;
        [SerializeField] XrBlendFactorFB m_DstFactorAlpha = XrBlendFactorFB.Zero;

        NativeArray<Native.XrCompositionLayerAlphaBlendFB> m_NativeArray;

        public XrBlendFactorFB SrcFactorColor
        {
            get => m_SrcFactorColor;
            set => m_SrcFactorColor = UpdateValue(m_SrcFactorColor, value);
        }

        public XrBlendFactorFB DstFactorColor
        {
            get => m_DstFactorColor;
            set => m_DstFactorColor = UpdateValue(m_DstFactorColor, value);
        }

        public XrBlendFactorFB SrcFactorAlpha
        {
            get => m_SrcFactorAlpha;
            set => m_SrcFactorAlpha = UpdateValue(m_SrcFactorAlpha, value);
        }

        public XrBlendFactorFB DstFactorAlpha
        {
            get => m_DstFactorAlpha;
            set => m_DstFactorAlpha = UpdateValue(m_DstFactorAlpha, value);
        }

        public override unsafe void* GetNativeStructPtr()
        {
            var s = new Native.XrCompositionLayerAlphaBlendFB(
                XR_TYPE_COMPOSITION_LAYER_ALPHA_BLEND_FB,
                null,
                m_SrcFactorColor, m_DstFactorColor,
                m_SrcFactorAlpha, m_DstFactorAlpha);

            if (!m_NativeArray.IsCreated)
                m_NativeArray = new NativeArray<Native.XrCompositionLayerAlphaBlendFB>(1, Allocator.Persistent);

            m_NativeArray[0] = s;
            return m_NativeArray.GetUnsafePtr();
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (m_NativeArray.IsCreated)
                m_NativeArray.Dispose();
        }

        static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public unsafe struct XrCompositionLayerAlphaBlendFB
            {
                public XrCompositionLayerAlphaBlendFB(uint type, void* next,
                    XrBlendFactorFB srcFactorColor, XrBlendFactorFB dstFactorColor,
                    XrBlendFactorFB srcFactorAlpha, XrBlendFactorFB dstFactorAlpha)
                {
                    this.type = type;
                    this.next = next;
                    this.srcFactorColor = srcFactorColor;
                    this.dstFactorColor = dstFactorColor;
                    this.srcFactorAlpha = srcFactorAlpha;
                    this.dstFactorAlpha = dstFactorAlpha;
                }

                uint type;
                void* next;
                XrBlendFactorFB srcFactorColor;
                XrBlendFactorFB dstFactorColor;
                XrBlendFactorFB srcFactorAlpha;
                XrBlendFactorFB dstFactorAlpha;
            }
        }
    }
}

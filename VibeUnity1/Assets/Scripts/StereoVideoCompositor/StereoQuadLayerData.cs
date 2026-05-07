using System;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using UnityEngine;

namespace Quintar.StereoVideoCompositor
{
    public enum StereoLayout
    {
        Mono,
        TopBottom,
        LeftRight,
    }

    [Serializable]
    [CompositionLayerData(
        Provider = "Quintar",
        Name = "Stereo Quad",
        IconPath = "",
        InspectorIcon = "",
        ListViewIcon = "",
        SupportTransform = true,
        Description = "Stereo quad composition layer that emits two XrCompositionLayerQuad submissions per frame, one per eye, with SubImage.ImageRect addressing the correct half of the source texture.",
        SuggestedExtenstionTypes = new[] { typeof(TexturesExtension) }
    )]
    public class StereoQuadLayerData : LayerData
    {
        [SerializeField] Vector2 m_Size = Vector2.one;
        [SerializeField] bool m_ApplyTransformScale = true;
        [SerializeField] StereoLayout m_StereoLayout = StereoLayout.TopBottom;

        public Vector2 Size
        {
            get => m_Size;
            set => m_Size = UpdateValue(m_Size, value);
        }

        public bool ApplyTransformScale
        {
            get => m_ApplyTransformScale;
            set => m_ApplyTransformScale = UpdateValue(m_ApplyTransformScale, value);
        }

        public StereoLayout StereoLayout
        {
            get => m_StereoLayout;
            set => m_StereoLayout = UpdateValue(m_StereoLayout, value);
        }

        public Vector2 GetScaledSize(Vector3 scale)
        {
            return m_ApplyTransformScale ? new Vector2(scale.x * m_Size.x, scale.y * m_Size.y) : m_Size;
        }

        public override void CopyFrom(LayerData other)
        {
            if (other is StereoQuadLayerData s)
            {
                m_Size = s.m_Size;
                m_ApplyTransformScale = s.m_ApplyTransformScale;
                m_StereoLayout = s.m_StereoLayout;
            }
        }
    }
}

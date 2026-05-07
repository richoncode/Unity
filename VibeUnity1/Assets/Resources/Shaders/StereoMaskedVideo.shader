Shader "Custom/StereoMaskedVideo"
{
    Properties
    {
        _MainTex ("Video Texture", 2D) = "black" {} // Changed to black to identify fallback
        _MaskTex ("Mask Texture", 2D) = "white" {}
        _Pulse ("Pulse", Float) = 0.0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            #if defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
                #extension GL_OES_EGL_image_external_essl3 : enable
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Video texture (OES External)
            #if defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
                samplerExternalOES _MainTex;
            #else
                TEXTURE2D(_MainTex);
                SAMPLER(sampler_MainTex);
            #endif

            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            float _Pulse;

            Varyings vert (Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 videoUV = input.uv;
                float2 maskUV = input.uv;

                if (unity_StereoEyeIndex == 0) // Left Eye
                {
                    videoUV.y = videoUV.y * 0.5 + 0.5;
                    maskUV.x = maskUV.x * 0.5;
                }
                else // Right Eye
                {
                    videoUV.y = videoUV.y * 0.5;
                    maskUV.x = maskUV.x * 0.5 + 0.5;
                }

                #if defined(SHADER_API_GLES3) || defined(SHADER_API_GLES)
                    half4 videoColor = tex2D(_MainTex, videoUV);
                #else
                    half4 videoColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, videoUV);
                #endif
                
                half4 maskColor = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, maskUV);

                // Pulse indicator: subtly tint video if it's white/empty
                // This helps confirm the shader is actually sampling _MainTex
                videoColor.rgb += _Pulse * 0.1;

                return half4(videoColor.rgb, videoColor.a * maskColor.r);
            }
            ENDHLSL
        }
    }
}

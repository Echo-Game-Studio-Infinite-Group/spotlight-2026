Shader "Hidden/DeferredOutline/Source"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        HLSLINCLUDE
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_DO_SpecialMask);
            SAMPLER(sampler_DO_SpecialMask);
            float4 _DO_MaskST;
            float4 _DO_BoundsMinMax;
            float _DO_Visibility;
            float _DO_TransitionMode;
            float _DO_NoiseScale;
            float _DO_TransitionDistortion;
            float _DO_TransitionSoftness;
            float _DO_UseSpecialMask;
            float _DO_EqualSpacingPixels;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float ValueNoise3D(float3 p)
            {
                float3 cell = floor(p);
                float3 local = frac(p);
                local = local * local * (3.0 - 2.0 * local);
                float n000 = Hash31(cell + float3(0, 0, 0));
                float n100 = Hash31(cell + float3(1, 0, 0));
                float n010 = Hash31(cell + float3(0, 1, 0));
                float n110 = Hash31(cell + float3(1, 1, 0));
                float n001 = Hash31(cell + float3(0, 0, 1));
                float n101 = Hash31(cell + float3(1, 0, 1));
                float n011 = Hash31(cell + float3(0, 1, 1));
                float n111 = Hash31(cell + float3(1, 1, 1));
                float lower = lerp(lerp(n000, n100, local.x), lerp(n010, n110, local.x), local.y);
                float upper = lerp(lerp(n001, n101, local.x), lerp(n011, n111, local.x), local.y);
                return lerp(lower, upper, local.z);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                // 原始闭合轮廓避免硬法线拆顶点外扩造成断边；宽度在屏幕距离场统一处理。
                output.positionCS = positions.positionCS;
                output.positionWS = positions.positionWS;
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // SV_POSITION is the rasterized (expanded) pixel; testing the original vertex
                // projection would let a displaced outline sample an unrelated depth pixel.
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float sceneDepth = SampleSceneDepth(screenUV);
                float objectDepth = input.positionCS.z;
                float sceneEyeDepth = LinearEyeDepth(sceneDepth, _ZBufferParams);
                float objectEyeDepth = LinearEyeDepth(objectDepth, _ZBufferParams);
                clip(sceneEyeDepth - objectEyeDepth + max(0.005, sceneEyeDepth * 0.0001));

                float3 noisePosition = input.positionWS * max(_DO_NoiseScale, 0.1);
                noisePosition += float3(_Time.y * 0.7, _Time.y * 1.35, -_Time.y * 0.45);
                float noise = ValueNoise3D(noisePosition);
                float transitionEdge;
                if (_DO_TransitionMode < 0.5)
                {
                    transitionEdge = smoothstep(noise - _DO_TransitionSoftness, noise + _DO_TransitionSoftness, _DO_Visibility);
                    clip(transitionEdge - 0.001);
                }
                else
                {
                    float heightRange = max(_DO_BoundsMinMax.y - _DO_BoundsMinMax.x, 1e-4);
                    float height01 = saturate((input.positionWS.y - _DO_BoundsMinMax.x) / heightRange);
                    float wave = (noise - 0.5) * _DO_TransitionDistortion;
                    wave += sin(input.positionWS.x * _DO_NoiseScale + _Time.y * 5.0) * _DO_TransitionDistortion * 0.35;
                    transitionEdge = smoothstep(height01 - _DO_TransitionSoftness, height01 + _DO_TransitionSoftness, _DO_Visibility + wave);
                    clip(transitionEdge - 0.001);
                }

                float2 maskUV = input.uv * _DO_MaskST.xy + _DO_MaskST.zw;
                maskUV.y -= _Time.y * 0.35;
                float fireMask = SAMPLE_TEXTURE2D(_DO_SpecialMask, sampler_DO_SpecialMask, maskUV).r;
                // Cubic smoothstep removes the frame-to-frame popping of thin flame tongues.
                fireMask = fireMask * fireMask * (3.0 - 2.0 * fireMask);
                fireMask = lerp(0.0, fireMask, _DO_UseSpecialMask);
                // B stores source eye depth for the later screen-space expansion test.
                // The depth/stencil attachment keeps only the nearest target in this batch.
                float eyeDepth = -TransformWorldToView(input.positionWS).z;
                return float4(transitionEdge, fireMask * transitionEdge, eyeDepth, 1.0);
            }
        ENDHLSL

        // The first pass selects the closest visible expanded surface and writes stencil.
        Pass
        {
            Name "DeferredOutlineDepthStencil"
            ZWrite On ZTest LEqual Cull Off ColorMask 0
            Stencil { Ref 1 Comp Always Pass Replace }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthFrag
            float4 DepthFrag(Varyings input) : SV_Target
            {
                Frag(input);
                return 0;
            }
            ENDHLSL
        }

        // Color is accepted only where the custom depth/stencil pass marked the surface.
        Pass
        {
            Name "DeferredOutlineSource"
            ZWrite Off ZTest Equal Cull Off Blend Off
            Stencil { Ref 1 Comp Equal Pass Keep }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
}

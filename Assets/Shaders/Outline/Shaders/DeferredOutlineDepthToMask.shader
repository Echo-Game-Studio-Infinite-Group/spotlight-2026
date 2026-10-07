Shader "Hidden/DeferredOutline/DepthToMask"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "SelectedDepthToCoverage"
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            TEXTURE2D_FLOAT(_DO_SelectedDepth); SAMPLER(sampler_DO_SelectedDepth);
            TEXTURE2D(_DO_SpecialMask); SAMPLER(sampler_DO_SpecialMask);
            float4x4 _DO_InverseViewProjection;
            float4 _DO_BoundsMinMax, _DO_MaskST;
            float _DO_Visibility, _DO_TransitionMode, _DO_NoiseScale;
            float _DO_TransitionDistortion, _DO_TransitionSoftness, _DO_UseSpecialMask;
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }
            float EyeDepth(float raw)
            {
                float linear01 = raw;
                #if UNITY_REVERSED_Z
                linear01 = 1.0 - raw;
                #endif
                return lerp(LinearEyeDepth(raw, _ZBufferParams),
                    lerp(_ProjectionParams.y, _ProjectionParams.z, linear01), unity_OrthoParams.w);
            }
            float Hash31(float3 p)
            {
                p = frac(p * 0.1031); p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }
            float Noise(float3 p)
            {
                float3 cell = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float lower = lerp(lerp(Hash31(cell), Hash31(cell + float3(1,0,0)), f.x),
                    lerp(Hash31(cell + float3(0,1,0)), Hash31(cell + float3(1,1,0)), f.x), f.y);
                float upper = lerp(lerp(Hash31(cell + float3(0,0,1)), Hash31(cell + float3(1,0,1)), f.x),
                    lerp(Hash31(cell + float3(0,1,1)), Hash31(cell + float3(1,1,1)), f.x), f.y);
                return lerp(lower, upper, f.z);
            }
            float4 Frag(Varyings input) : SV_Target
            {
                float raw = SAMPLE_TEXTURE2D_LOD(_DO_SelectedDepth, sampler_DO_SelectedDepth, input.uv, 0).r;
                #if UNITY_REVERSED_Z
                if (raw <= 0.0000001) return float4(0,0,0,0);
                #else
                if (raw >= 0.9999999) return float4(0,0,0,0);
                #endif
                float eye = EyeDepth(raw);
                float sceneRaw = SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture, sampler_CameraDepthTexture, input.uv, 0).r;
                float sceneEye = EyeDepth(sceneRaw);
                if (eye > sceneEye + max(0.005, sceneEye * 0.0001)) return float4(0,0,0,0);
                float clipDepth = raw;
                #if !UNITY_REVERSED_Z
                clipDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, raw);
                #endif
                float3 world = ComputeWorldSpacePosition(input.uv, clipDepth, _DO_InverseViewProjection);
                float coverage = saturate(_DO_Visibility);
                if (coverage > 0.0 && coverage < 1.0)
                {
                    float noise = Noise(world * max(_DO_NoiseScale, 0.1) + float3(_Time.y * 0.7, _Time.y * 1.35, -_Time.y * 0.45));
                    float edge = noise;
                    if (_DO_TransitionMode > 0.5)
                    {
                        edge = saturate((world.y - _DO_BoundsMinMax.x) / max(_DO_BoundsMinMax.y - _DO_BoundsMinMax.x, 0.0001));
                        edge -= (noise - 0.5) * _DO_TransitionDistortion
                            + sin(world.x * _DO_NoiseScale + _Time.y * 5.0) * _DO_TransitionDistortion * 0.35;
                    }
                    coverage = smoothstep(edge - _DO_TransitionSoftness, edge + _DO_TransitionSoftness, coverage);
                }
                // 原DepthOnly保持材质自身alpha clip；此处只添加屏幕效果的显隐/火焰调制。
                float2 maskUV = world.xy * _DO_MaskST.xy + _DO_MaskST.zw;
                maskUV.y -= _Time.y * 0.35;
                float fire = SAMPLE_TEXTURE2D_LOD(_DO_SpecialMask, sampler_DO_SpecialMask, maskUV, 0).r;
                fire = fire * fire * (3.0 - 2.0 * fire) * _DO_UseSpecialMask;
                return float4(coverage, fire * coverage, eye, coverage > 0.001 ? 1.0 : 0.0);
            }
            ENDHLSL
        }
    }
}
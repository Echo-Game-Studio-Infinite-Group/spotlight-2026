Shader "Hidden/OurFunction/VolumetricLight"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "RayMarch"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragRayMarch
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            half4 _ScatteringTint;
            float _VolumetricCascadeCount;
            float4 _RayMarchParams;
            float4 _PhaseParams;
            float4 _HeightParams;

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = max(1.0 + g2 - 2.0 * g * cosTheta, 1e-4);
                return (1.0 - g2) / (4.0 * PI * pow(denom, 1.5));
            }

            float Bayer4x4(float2 pixelPosition)
            {
                uint x = (uint)fmod(pixelPosition.x, 4.0);
                uint y = (uint)fmod(pixelPosition.y, 4.0);
                if (y == 0u)
                {
                    if (x == 0u) return 0.0 / 16.0;
                    if (x == 1u) return 8.0 / 16.0;
                    if (x == 2u) return 2.0 / 16.0;
                    return 10.0 / 16.0;
                }
                if (y == 1u)
                {
                    if (x == 0u) return 12.0 / 16.0;
                    if (x == 1u) return 4.0 / 16.0;
                    if (x == 2u) return 14.0 / 16.0;
                    return 6.0 / 16.0;
                }
                if (y == 2u)
                {
                    if (x == 0u) return 3.0 / 16.0;
                    if (x == 1u) return 11.0 / 16.0;
                    if (x == 2u) return 1.0 / 16.0;
                    return 9.0 / 16.0;
                }

                if (x == 0u) return 15.0 / 16.0;
                if (x == 1u) return 7.0 / 16.0;
                if (x == 2u) return 13.0 / 16.0;
                return 5.0 / 16.0;
            }

            half4 FragRayMarch(Varyings input) : SV_Target
            {
                float rawDepth = SampleSceneDepth(input.uv);

                bool isSky = false;
                #if UNITY_REVERSED_Z
                    if (rawDepth <= 1e-5)
                        isSky = true;
                #else
                    if (rawDepth >= 0.99999)
                        isSky = true;
                #endif

                // Infinite-far projections can reconstruct a non-finite sky position
                // at depth 0/1. Use a nearby finite depth only for ray direction.
                #if UNITY_REVERSED_Z
                    float directionDepth = isSky ? 1e-4 : rawDepth;
                #else
                    float directionDepth = isSky ? 0.9999 : rawDepth;
                #endif
                float3 worldPos = ComputeWorldSpacePosition(input.uv, directionDepth, UNITY_MATRIX_I_VP);
                float3 rayVector = worldPos - _WorldSpaceCameraPos.xyz;
                float rayDistance = length(rayVector);
                float3 rayDir = rayDistance > 1e-5 ? rayVector / rayDistance : float3(0.0, 0.0, 1.0);

                float maxDistance = max(_RayMarchParams.y, 1.0);
                float sceneDistance = isSky ? maxDistance : min(rayDistance, maxDistance);
                if (sceneDistance <= 1e-4)
                    return 0;

                Light mainLight = GetMainLight();
                float3 lightColor = mainLight.color;
                float lightEnergy = max(lightColor.r, max(lightColor.g, lightColor.b));
                if (lightEnergy <= 1e-4)
                    return 0;

                float3 lightDir = normalize(mainLight.direction);
                // Preserve the original effect's phase-angle convention.
                float phase = HenyeyGreenstein(dot(-rayDir, lightDir), _PhaseParams.x);
                float steps = clamp(_RayMarchParams.x, 4.0, 128.0);
                float stepLength = sceneDistance / steps;
                float jitter = Bayer4x4(input.positionCS.xy) * _PhaseParams.y;

                float transmittance = 1.0;
                float accumulatedLight = 0.0;

                [loop]
                for (int i = 0; i < 128; ++i)
                {
                    if (i >= (int)steps)
                        break;

                    float currentDistance = (i + jitter + 0.5) * stepLength;
                    float3 samplePosition = _WorldSpaceCameraPos.xyz + rayDir * currentDistance;

                    float localDensity = max(_RayMarchParams.w, 0.0);
                    if (_HeightParams.y > 1e-5)
                    {
                        float heightAboveBase = max(samplePosition.y - _HeightParams.x, 0.0);
                        localDensity *= exp(-heightAboveBase * _HeightParams.y);
                    }

                    float3 shadowPosition = samplePosition + lightDir * _PhaseParams.w;
                    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                        // Volumetric samples are not surface pixels: screen shadows cannot
                        // represent their visibility. Read the cascaded world shadow atlas.
                        half cascade = _VolumetricCascadeCount > 1 ? ComputeCascadeIndex(shadowPosition) : half(0);
                        float4 shadowCoord = mul(_MainLightWorldToShadow[cascade], float4(shadowPosition, 1.0));
                        half4 shadowParams = GetMainLightShadowParams();
                        float shadow = SampleShadowmap(TEXTURE2D_ARGS(_MainLightShadowmapTexture, sampler_LinearClampCompare),
                            float4(shadowCoord.xyz, 0), GetMainLightShadowSamplingData(), shadowParams, false);
                    #else
                        float4 shadowCoord = TransformWorldToShadowCoord(shadowPosition);
                        float shadow = MainLightRealtimeShadow(shadowCoord);
                    #endif
                    shadow = lerp(1.0, shadow, saturate(_PhaseParams.z));

                    float extinction = localDensity * stepLength;
                    accumulatedLight += transmittance * shadow * extinction;
                    transmittance *= exp(-extinction);
                }

                float3 scattering = accumulatedLight * phase * _RayMarchParams.z * _ScatteringTint.rgb * lightColor;
                return half4(scattering, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Blur"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragBlur

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D_X(_Volumetric_SourceTex);
            SAMPLER(sampler_Volumetric_SourceTex);

            float4 _BlurDirection;
            float4 _SourceTexelSize;
            float4 _HeightParams;

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 FragBlur(Varyings input) : SV_Target
            {
                float2 offset = _BlurDirection.xy * _SourceTexelSize.xy;
                float centerDepth = LinearEyeDepth(SampleSceneDepth(input.uv), _ZBufferParams);
                float depthThreshold = max(_HeightParams.z, 0.01);

                half3 color = 0.0h;
                float weightSum = 0.0;

                #define ADD_BLUR_SAMPLE(uvOffset, baseWeight) \
                    { \
                        float2 sampleUv = input.uv + uvOffset; \
                        float sampleDepth = LinearEyeDepth(SampleSceneDepth(sampleUv), _ZBufferParams); \
                        float depthWeight = exp(-abs(sampleDepth - centerDepth) / depthThreshold); \
                        float weight = baseWeight * depthWeight; \
                        color += SAMPLE_TEXTURE2D_X(_Volumetric_SourceTex, sampler_Volumetric_SourceTex, sampleUv).rgb * weight; \
                        weightSum += weight; \
                    }

                ADD_BLUR_SAMPLE(0.0, 0.40)
                ADD_BLUR_SAMPLE(offset, 0.15)
                ADD_BLUR_SAMPLE(-offset, 0.15)
                ADD_BLUR_SAMPLE(offset * 2.0, 0.10)
                ADD_BLUR_SAMPLE(-offset * 2.0, 0.10)
                ADD_BLUR_SAMPLE(offset * 3.0, 0.05)
                ADD_BLUR_SAMPLE(-offset * 3.0, 0.05)

                #undef ADD_BLUR_SAMPLE

                return half4(color / max(weightSum, 1e-4), 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragComposite

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D_X(_Volumetric_SourceTex);
            SAMPLER(sampler_Volumetric_SourceTex);
            TEXTURE2D_X(_VolumetricTex);
            SAMPLER(sampler_VolumetricTex);

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                half4 source = SAMPLE_TEXTURE2D_X(_Volumetric_SourceTex, sampler_Volumetric_SourceTex, input.uv);
                half3 volumetric = SAMPLE_TEXTURE2D_X(_VolumetricTex, sampler_VolumetricTex, input.uv).rgb;
                return half4(source.rgb + volumetric, source.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugSource"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragDebugSource

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D_X(_Volumetric_SourceTex);
            SAMPLER(sampler_Volumetric_SourceTex);

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 FragDebugSource(Varyings input) : SV_Target
            {
                return SAMPLE_TEXTURE2D_X(_Volumetric_SourceTex, sampler_Volumetric_SourceTex, input.uv);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DebugLight"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment FragDebugLight

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D_X(_VolumetricTex);
            SAMPLER(sampler_VolumetricTex);

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 FragDebugLight(Varyings input) : SV_Target
            {
                half3 light = SAMPLE_TEXTURE2D_X(_VolumetricTex, sampler_VolumetricTex, input.uv).rgb;
                return half4(light, 1.0h);
            }
            ENDHLSL
        }
    }
}

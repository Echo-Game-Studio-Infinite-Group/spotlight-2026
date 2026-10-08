Shader "Hidden/OurFunction/GTAOComposite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "Composite"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D_X(_GTAOComposite_SourceTex);
            SAMPLER(sampler_GTAOComposite_SourceTex);
            TEXTURE2D_X(_GTAO_AOTexture);
            SAMPLER(sampler_GTAO_AOTexture);
            TEXTURE2D_X(_GBuffer0);
            SAMPLER(sampler_GBuffer0);
            TEXTURE2D_X(_GBuffer2);
            SAMPLER(sampler_GBuffer2);

            float4 _GTAOComposite_SourceTex_TexelSize;
            float4 _GTAOCompositeParams; // x=multiBounceOn, y=multiBounceStrength, z=ssdoOn, w=debugSSDO
            float _GTAOComposite_ForwardMode;
            float4 _GTAOSSDOParams;      // x=intensity, y=radiusPx, z=sampleCount, w=maxContribution

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float2 GTAOComposite_Unpack888UIntToFloat2(uint3 x)
            {
                uint hi = x.z >> 4;
                uint lo = x.z & 15;
                uint2 cb = x.xy | uint2(lo << 8, hi << 8);
                return cb / 4095.0;
            }

            float2 GTAOComposite_Unpack888ToFloat2(float3 x)
            {
                uint3 i = (uint3)(x * 255.5);
                return GTAOComposite_Unpack888UIntToFloat2(i);
            }

            float3 GTAOComposite_UnpackNormalOctQuadEncode(float2 f)
            {
                float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
                float t = max(-n.z, 0.0);
                n.xy += lerp(t.xx, -t.xx, step(0.0, n.xy));
                return normalize(n);
            }

            float3 GTAOComposite_UnpackGBufferNormal(float3 normal)
            {
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 remappedOctNormalWS = GTAOComposite_Unpack888ToFloat2(normal);
                float2 octNormalWS = remappedOctNormalWS.xy * 2.0 - 1.0;
                return GTAOComposite_UnpackNormalOctQuadEncode(octNormalWS);
            #else
                return normalize(normal);
            #endif
            }

            float3 GTAOComposite_MultiBounce(float visibility, float3 albedo)
            {
                float3 a =  2.0404 * albedo - 0.3324;
                float3 b = -4.7951 * albedo + 0.6417;
                float3 c =  2.7552 * albedo + 0.6903;
                float x = saturate(visibility);
                return saturate(max(float3(x, x, x), ((x * a + b) * x + c) * x));
            }

            float3 GTAOComposite_SampleAlbedo(float2 uv)
            {
                if (_GTAOComposite_ForwardMode > 0.5)
                    return saturate(SAMPLE_TEXTURE2D_X(_GTAOComposite_SourceTex, sampler_GTAOComposite_SourceTex, uv).rgb);
                return saturate(SAMPLE_TEXTURE2D_X(_GBuffer0, sampler_GBuffer0, uv).rgb);
            }

            float3 GTAOComposite_SampleNormal(float2 uv)
            {
                if (_GTAOComposite_ForwardMode > 0.5) return normalize(SampleSceneNormals(uv));
                return GTAOComposite_UnpackGBufferNormal(SAMPLE_TEXTURE2D_X(_GBuffer2, sampler_GBuffer2, uv).xyz);
            }

            float GTAOComposite_SampleAO(float2 uv)
            {
                return saturate(SAMPLE_TEXTURE2D_X(_GTAO_AOTexture, sampler_GTAO_AOTexture, uv).r);
            }

            float GTAOComposite_DepthWeight(float centerRawDepth, float sampleRawDepth)
            {
                float centerEye = LinearEyeDepth(centerRawDepth, _ZBufferParams);
                float sampleEye = LinearEyeDepth(sampleRawDepth, _ZBufferParams);
                float diff = abs(centerEye - sampleEye);
                return saturate(1.0 - diff * 2.0);
            }

            float3 GTAOComposite_ComputeSSDO(float2 uv, float centerAO, float3 albedo)
            {
                float rawDepth = SampleSceneDepth(uv);
                if (rawDepth == 0.0)
                    return float3(0.0, 0.0, 0.0);

                float3 centerNormal = GTAOComposite_SampleNormal(uv);
                float occlusion = saturate(1.0 - centerAO);
                float radius = max(_GTAOSSDOParams.y, 1.0);
                int sampleCount = (int)(_GTAOSSDOParams.z + 0.5);
                sampleCount = min(max(sampleCount, 2), 12);

                const float2 directions[12] =
                {
                    float2( 0.8660254,  0.5000000),
                    float2(-0.2588190,  0.9659258),
                    float2(-0.9659258, -0.2588190),
                    float2( 0.5000000, -0.8660254),
                    float2( 0.1736482,  0.9848078),
                    float2(-0.9848078,  0.1736482),
                    float2(-0.4226183, -0.9063078),
                    float2( 0.9063078, -0.4226183),
                    float2( 0.7071068,  0.7071068),
                    float2(-0.7071068,  0.7071068),
                    float2(-0.7071068, -0.7071068),
                    float2( 0.7071068, -0.7071068)
                };

                float3 bounce = float3(0.0, 0.0, 0.0);
                float weightSum = 0.0;

                [loop]
                for (int i = 0; i < 12; ++i)
                {
                    if (i >= sampleCount)
                        break;

                    float ring = 0.45 + 0.55 * ((float)i + 0.5) / sampleCount;
                    float2 offset = directions[i] * radius * ring * _GTAOComposite_SourceTex_TexelSize.xy;
                    float2 sampleUV = saturate(uv + offset);

                    float sampleDepth = SampleSceneDepth(sampleUV);
                    if (sampleDepth == 0.0)
                        continue;

                    float depthWeight = GTAOComposite_DepthWeight(rawDepth, sampleDepth);
                    float sampleAO = GTAOComposite_SampleAO(sampleUV);
                    float sampleOcclusion = saturate(1.0 - sampleAO);
                    float3 sampleNormal = GTAOComposite_SampleNormal(sampleUV);
                    float normalWeight = saturate(dot(centerNormal, sampleNormal) * 0.5 + 0.5);
                    normalWeight *= normalWeight;

                    float weight = depthWeight * normalWeight * sampleOcclusion;
                    float3 sampleColor = SAMPLE_TEXTURE2D_X(_GTAOComposite_SourceTex, sampler_GTAOComposite_SourceTex, sampleUV).rgb;
                    bounce += sampleColor * weight;
                    weightSum += weight;
                }

                bounce = (weightSum > 1e-4) ? bounce / weightSum : float3(0.0, 0.0, 0.0);
                bounce *= albedo * occlusion * _GTAOSSDOParams.x;
                return min(bounce, float3(_GTAOSSDOParams.w, _GTAOSSDOParams.w, _GTAOSSDOParams.w));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float4 scene = SAMPLE_TEXTURE2D_X(_GTAOComposite_SourceTex, sampler_GTAOComposite_SourceTex, input.uv);
                float rawDepth = SampleSceneDepth(input.uv);
                if (rawDepth == 0.0)
                    return scene;

                float ao = GTAOComposite_SampleAO(input.uv);
                float3 albedo = GTAOComposite_SampleAlbedo(input.uv);

                float3 ssdo = float3(0.0, 0.0, 0.0);
                if (_GTAOCompositeParams.z > 0.5 || _GTAOCompositeParams.w > 0.5)
                    ssdo = GTAOComposite_ComputeSSDO(input.uv, ao, albedo);

                if (_GTAOCompositeParams.w > 0.5)
                    return float4(ssdo, 1.0);

                float3 color = scene.rgb;
                if (_GTAOCompositeParams.x > 0.5)
                {
                    float3 multiBounceAO = GTAOComposite_MultiBounce(ao, albedo);
                    float3 colorRecovery = multiBounceAO / max(float3(ao, ao, ao), float3(0.08, 0.08, 0.08));
                    colorRecovery = min(colorRecovery, float3(2.0, 2.0, 2.0));
                    color *= lerp(float3(1.0, 1.0, 1.0), colorRecovery, saturate(_GTAOCompositeParams.y));
                }

                color += ssdo;
                return float4(color, scene.a);
            }
            ENDHLSL
        }
    }
}

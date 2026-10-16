Shader "Hidden/OurFunction/SSR"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

        TEXTURE2D_X(_SSR_SourceTex);
        SAMPLER(sampler_SSR_SourceTex);
        TEXTURE2D_X(_SSR_ReflectionMap);
        SAMPLER(sampler_SSR_ReflectionMap);
        TEXTURE2D_X(_GBuffer0);
        SAMPLER(sampler_GBuffer0);
        TEXTURE2D_X(_GBuffer1);
        SAMPLER(sampler_GBuffer1);
        TEXTURE2D_X(_GBuffer2);
        SAMPLER(sampler_GBuffer2);

        float4x4 _SSR_InverseProjectionMatrix;
        float4x4 _SSR_InverseViewMatrix;
        float4x4 _SSR_ProjectionMatrix;
        float4x4 _SSR_ViewMatrix;

        float3 _SSR_WorldSpaceViewDir;
        float _SSR_RenderScale;
        float _SSR_Intensity;
        float _SSR_ReflectionBrightness;
        float _SSR_Stride;
        float _SSR_NumSteps;
        float _SSR_MinSmoothness;
        int _SSR_ReflectSky;
        int _SSR_Frame;
        int _SSR_DitherMode;
        int _SSR_DebugView;
        float _SSR_UseForwardNormals;
        float _SSR_ForwardSmoothness;

        float4 _SSR_TraceTexelSize;
        float _SSR_JitterRadiusPixels, _SSR_RoughnessMipMax;
        #define BINARY_STEP_COUNT 16

        struct Varyings
        {
            float2 uv : TEXCOORD0;
            float4 positionCS : SV_POSITION;
        };

        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            o.uv = GetFullScreenTriangleTexCoord(vertexID);
            return o;
        }

        float2 SSR_Unpack888UIntToFloat2(uint3 x)
        {
            uint hi = x.z >> 4;
            uint lo = x.z & 15;
            uint2 cb = x.xy | uint2(lo << 8, hi << 8);
            return cb / 4095.0;
        }

        float2 SSR_Unpack888ToFloat2(float3 x)
        {
            uint3 i = (uint3)(x * 255.5);
            return SSR_Unpack888UIntToFloat2(i);
        }

        float3 SSR_UnpackNormalOctQuadEncode(float2 f)
        {
            float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
            float t = max(-n.z, 0.0);
            n.xy += lerp(t.xx, -t.xx, step(0.0, n.xy));
            return normalize(n);
        }

        float3 SSR_UnpackGBufferNormal(float3 normal)
        {
        #if defined(_GBUFFER_NORMALS_OCT)
            float2 remappedOctNormalWS = SSR_Unpack888ToFloat2(normal);
            float2 octNormalWS = remappedOctNormalWS.xy * 2.0 - 1.0;
            return SSR_UnpackNormalOctQuadEncode(octNormalWS);
        #else
            return normalize(normal);
        #endif
        }

        float3 SSR_GetNormal(float2 uv)
        {
            if (_SSR_UseForwardNormals > 0.5)
                return normalize(SampleSceneNormals(uv));
            return SSR_UnpackGBufferNormal(SAMPLE_TEXTURE2D_X(_GBuffer2, sampler_GBuffer2, uv).xyz);
        }

        float SSR_GetSmoothness(float2 uv)
        {
            if (_SSR_UseForwardNormals > 0.5) return _SSR_ForwardSmoothness;
            return SAMPLE_TEXTURE2D_X(_GBuffer2, sampler_GBuffer2, uv).w;
        }

        float SSR_ScreenEdgeMask(float2 clipPos)
        {
            float yDif = 1.0 - abs(clipPos.y);
            float xDif = 1.0 - abs(clipPos.x);
            if (yDif < 0.0 || xDif < 0.0)
                return 0.0;

            float yMask = smoothstep(0.0, 0.2, yDif);
            float xMask = smoothstep(0.0, 0.1, xDif);
            return saturate(xMask * yMask);
        }

        float3 SSR_GetWorldPosition(float rawDepth, float2 uv)
        {
            // 按 URP helper 将纹理 UV 转为 GPU 裁剪坐标；D3D 的 Y 约定需显式转换。
            #if !UNITY_REVERSED_Z
                rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
            #endif
            float4 clipSpace = ComputeClipSpacePosition(uv, rawDepth);
            float4 viewSpacePosition = mul(_SSR_InverseProjectionMatrix, clipSpace);
            viewSpacePosition /= viewSpacePosition.w;
            float4 worldSpacePosition = mul(_SSR_InverseViewMatrix, viewSpacePosition);
            return worldSpacePosition.xyz;
        }

        float SSR_RGB2Lum(float3 rgb)
        {
            return 0.299 * rgb.r + 0.587 * rgb.g + 0.114 * rgb.b;
        }

        float SSR_Dither8x8(float2 screenPosition, float c0)
        {
            const float dither[64] =
            {
                0, 32, 8, 40, 2, 34, 10, 42,
                48, 16, 56, 24, 50, 18, 58, 26,
                12, 44, 4, 36, 14, 46, 6, 38,
                60, 28, 52, 20, 62, 30, 54, 22,
                3, 35, 11, 43, 1, 33, 9, 41,
                51, 19, 59, 27, 49, 17, 57, 25,
                15, 47, 7, 39, 13, 45, 5, 37,
                63, 31, 55, 23, 61, 29, 53, 21
            };

            c0 *= 2.0;
            float2 uv = screenPosition.xy * _ScreenParams.xy;
            uint index = (uint(uv.x) % 8) * 8 + uint(uv.y) % 8;
            float limit = (dither[index] + 1.0) / 64.0;
            return saturate(c0 - limit);
        }

        float SSR_IGN(uint pixelX, uint pixelY, uint frame)
        {
            frame = frame % 64;
            float x = float(pixelX) + 5.588238 * float(frame);
            float y = float(pixelY) + 5.588238 * float(frame);
            return fmod(52.9829189 * fmod(0.06711056 * x + 0.00583715 * y, 1.0), 1.0);
        }

        float4 FragTrace(Varyings i) : SV_Target
        {
            float rawDepth = SampleSceneDepth(i.uv);
            #if UNITY_REVERSED_Z
            bool isSky = rawDepth <= 1e-5;
            #else
            bool isSky = rawDepth >= 0.99999;
            #endif
            if (isSky)
                return float4(i.uv, _SSR_ReflectSky ? 1.0 : 0.0, 0.0);

            float smoothness = SSR_GetSmoothness(i.uv);
            float stepS = smoothstep(_SSR_MinSmoothness, 1.0, smoothness);
            float3 normal = SSR_GetNormal(i.uv);

            float4 worldSpacePosition = float4(SSR_GetWorldPosition(rawDepth, i.uv), 1.0);
            float4 viewSpacePosition = mul(_SSR_ViewMatrix, worldSpacePosition);
            float3 viewDir = normalize(worldSpacePosition.xyz - _WorldSpaceCameraPos);
            float3 reflectionRay = reflect(viewDir, normal);

            float3 reflectionRayVS = mul(_SSR_ViewMatrix, float4(reflectionRay, 0.0)).xyz;

            float viewReflectDot = saturate(dot(viewDir, reflectionRay));
            float cameraViewReflectDot = saturate(dot(_SSR_WorldSpaceViewDir, reflectionRay));
            float oneMinusViewReflectDot = sqrt(max(1.0 - viewReflectDot, 0.001));
            float rayStride = _SSR_Stride / oneMinusViewReflectDot;
            float thickness = _SSR_Stride * 2.0 / oneMinusViewReflectDot;

            bool doRayMarch = smoothness > _SSR_MinSmoothness;
            float maxRayLength = _SSR_NumSteps * rayStride;
            float maxDist = lerp(min(-viewSpacePosition.z, maxRayLength), maxRayLength, cameraViewReflectDot);
            float actualSteps = max(maxDist / rayStride, 0.0);

            int hit = 0;
            float maskOut = 1.0;
            float depthDelta = 0.0;
            float3 currentPosition = viewSpacePosition.xyz;
            float2 currentScreenSpacePosition = i.uv;

            if (doRayMarch)
            {
                float3 ray = reflectionRayVS * rayStride;

                [loop]
                for (int step = 0; step < 256; ++step)
                {
                    if (step >= actualSteps)
                        break;

                    currentPosition += ray;

                    float4 uv = mul(_SSR_ProjectionMatrix, float4(currentPosition, 1.0));
                    if (uv.w <= 0.0) break;
                    uv /= uv.w;
                    #if UNITY_UV_STARTS_AT_TOP
                    uv.y = -uv.y;
                    #endif
                    uv.x = uv.x * 0.5 + 0.5;
                    uv.y = uv.y * 0.5 + 0.5;

                    if (uv.x >= 1.0 || uv.x < 0.0 || uv.y >= 1.0 || uv.y < 0.0)
                        break;

                    float sampledDepth = SampleSceneDepth(uv.xy);
                    if (abs(rawDepth - sampledDepth) > 0.0 && sampledDepth != 0.0)
                    {
                        depthDelta = -currentPosition.z - LinearEyeDepth(sampledDepth, _ZBufferParams);
                        if (depthDelta > 0.0 && depthDelta < rayStride * 2.0)
                        {
                            currentScreenSpacePosition = uv.xy;
                            hit = 1;
                            break;
                        }
                    }
                }

                if (depthDelta > thickness)
                    hit = 0;

                [loop]
                for (int binaryStep = 0; binaryStep < BINARY_STEP_COUNT; ++binaryStep)
                {
                    if (hit == 0)
                        break;

                    ray *= 0.5;
                    if (depthDelta > 0.0)
                        currentPosition -= ray;
                    else if (depthDelta < 0.0)
                        currentPosition += ray;
                    else
                        break;

                    float4 uv = mul(_SSR_ProjectionMatrix, float4(currentPosition, 1.0));
                    if (uv.w <= 0.0) { hit = 0; break; }
                    uv /= uv.w;
                    #if UNITY_UV_STARTS_AT_TOP
                    uv.y = -uv.y;
                    #endif
                    maskOut = SSR_ScreenEdgeMask(uv.xy);
                    uv.x = uv.x * 0.5 + 0.5;
                    uv.y = uv.y * 0.5 + 0.5;
                    currentScreenSpacePosition = uv.xy;

                    float sampledDepth = SampleSceneDepth(uv.xy);
                    depthDelta = -currentPosition.z - LinearEyeDepth(sampledDepth, _ZBufferParams);
                    float minv = 1.0 / max(oneMinusViewReflectDot * float(binaryStep), 0.001);
                    if (abs(depthDelta) > minv)
                    {
                        hit = 0;
                        break;
                    }
                }

                float3 currentNormal = SSR_GetNormal(currentScreenSpacePosition);
                float backFaceDot = dot(currentNormal, reflectionRay);
                if (backFaceDot > 0.0)
                    hit = 0;
            }

            float safeMaxDist = max(maxDist, 0.001);
            float3 deltaDir = viewSpacePosition.xyz - currentPosition;
            float progress = dot(deltaDir, deltaDir) / (safeMaxDist * safeMaxDist);
            progress = smoothstep(0.0, 0.5, 1.0 - progress);

            maskOut *= hit * stepS;
            return float4(currentScreenSpacePosition, saturate(maskOut * progress), 1.0);
        }

        half4 FragComposite(Varyings i) : SV_Target
        {
            float4 sourceColor = SAMPLE_TEXTURE2D_X(_SSR_SourceTex, sampler_SSR_SourceTex, i.uv);
            float rawDepth = SampleSceneDepth(i.uv);

            #if UNITY_REVERSED_Z
            bool isSky = rawDepth <= 1e-5;
            #else
            bool isSky = rawDepth >= 0.99999;
            #endif
            if (isSky)
                return sourceColor;

            float3 worldSpacePosition = SSR_GetWorldPosition(rawDepth, i.uv);
            float3 viewDir = normalize(worldSpacePosition - _WorldSpaceCameraPos);

            float3 normalWS = SSR_GetNormal(i.uv);
            float stepS = smoothstep(_SSR_MinSmoothness, 1.0, SSR_GetSmoothness(i.uv));
            float fresnel = 1.0 - dot(viewDir, -normalWS);

            float3 normalSS = mul(_SSR_ViewMatrix, float4(normalWS, 0.0)).xyz;
            normalSS = mul(_SSR_ProjectionMatrix, float4(normalSS, 0.0)).xyz;
            #if UNITY_UV_STARTS_AT_TOP
            normalSS.y *= -1.0;
            #endif

            float dither;
            float type = 0.0;
            if (_SSR_DitherMode == 0)
                dither = SSR_Dither8x8(i.uv.xy * _SSR_RenderScale, 0.5);
            else
                dither = SSR_IGN(i.uv.x * _ScreenParams.x * _SSR_RenderScale, i.uv.y * _ScreenParams.y * _SSR_RenderScale, _SSR_Frame);
            dither = dither * 2.0 - 1.0;

            float stepSSqrd = stepS * stepS;
            float2 projectedNormal = normalSS.xy / max(1.0, length(normalSS.xy));
            float2 uvOffset = projectedNormal * (dither * _SSR_JitterRadiusPixels * (1.0 - stepSSqrd)) * _SSR_TraceTexelSize.xy;
            float4 reflectedUv = SAMPLE_TEXTURE2D_X(_SSR_ReflectionMap, sampler_SSR_ReflectionMap, i.uv + uvOffset * type);
            float maskVal = saturate(reflectedUv.z) * stepS;
            reflectedUv.xy += uvOffset * (1.0 - type);

            float lumin = saturate(SSR_RGB2Lum(sourceColor.rgb) - 1.0);
            float luminMask = pow(1.0 - lumin, 5.0);

            float4 reflectedTexture = SAMPLE_TEXTURE2D_X_LOD(_SSR_SourceTex, sampler_SSR_SourceTex, reflectedUv.xy,
                lerp(0.0, _SSR_RoughnessMipMax, 1.0 - pow(stepS, 4.0)));
            if (_SSR_UseForwardNormals > 0.5)
            {
                float reflectionWeight = saturate(maskVal * fresnel * luminMask * _SSR_Intensity);
                if (_SSR_DebugView == 1) return float4(maskVal.xxx, 1.0);
                if (_SSR_DebugView == 2) return float4(reflectedTexture.rgb * maskVal, 1.0);
                return float4(lerp(sourceColor.rgb, reflectedTexture.rgb * _SSR_ReflectionBrightness, reflectionWeight), sourceColor.a);
            }

            float2 gb1 = SAMPLE_TEXTURE2D_X(_GBuffer1, sampler_GBuffer1, i.uv.xy).ra;
            float4 specularColor = float4(SAMPLE_TEXTURE2D_X(_GBuffer0, sampler_GBuffer0, i.uv.xy).rgb, 1.0);

            float fresnelMask = 1.0 - saturate(SSR_RGB2Lum(specularColor.rgb));
            fresnelMask = lerp(1.0, fresnelMask, gb1.x);
            fresnel = lerp(1.0, fresnel * fresnel, fresnelMask);

            const float lowMetallic = 0.3;
            const float highMetallic = 1.0;
            const float lowSpecColor = 0.0;
            const float highSpecColor = 0.6;

            const float blurLow = 0.0;
            float blurHigh = _SSR_RoughnessMipMax;
            const float blurPow = 4.0;

            specularColor.rgb = lerp(float3(1.0, 1.0, 1.0), specularColor.rgb, lerp(lowSpecColor, highSpecColor, gb1.x));

            float metallic = clamp(gb1.x, lowMetallic, highMetallic);
            float nonMetallic = 1.0 - metallic;
            float roughnessBlurAmount = lerp(blurLow, blurHigh, 1.0 - pow(stepS, blurPow));
            reflectedTexture = SAMPLE_TEXTURE2D_X_LOD(_SSR_SourceTex, sampler_SSR_SourceTex, reflectedUv.xy, roughnessBlurAmount);

            float ao = gb1.y;
            float reflectionWeight = saturate(maskVal * ao * fresnel * luminMask * _SSR_Intensity);
            float4 reflectionColor = float4(reflectedTexture.rgb * _SSR_ReflectionBrightness, reflectedTexture.a);
            float4 blendedColor = sourceColor * nonMetallic + reflectionColor * specularColor * metallic;
            float4 result = lerp(sourceColor, blendedColor, reflectionWeight);

            if (_SSR_DebugView == 1)
                return float4(maskVal.xxx, 1.0);
            if (_SSR_DebugView == 2)
                return float4(reflectedTexture.rgb * maskVal, 1.0);

            return result;
        }
        ENDHLSL

        Pass
        {
            Name "Trace"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragTrace
            #pragma multi_compile _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma multi_compile _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
}

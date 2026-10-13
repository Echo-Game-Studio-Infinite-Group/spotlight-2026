Shader "Hidden/OurFunction/HeightFog"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "PostHeightFog"
            ZTest Always ZWrite Off Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D_X(_HeightFog_SourceTex);   SAMPLER(sampler_HeightFog_SourceTex);

            // 由 FogFeature 每帧写入
            half4  _FogColor;
            float  _FogDensity;
            float  _MaxFogOpacity;
            float  _FogHeightStart;
            float  _HeightFalloff;
            float  _FogStartDistance;
            float  _FogStartFadeRange;
            float  _SkyboxZenithFade;
            half4  _InscatterColor;
            float  _ScatteringG;
            float  _SunScatterIntensity;
            float  _InscatterStartDistance;

            // 用 SV_VertexID 生成全屏三角, 不依赖任何 MVP 矩阵, 避免 cmd.Blit 在 URP 下顶点变换错误导致黑屏
            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                o.uv = GetFullScreenTriangleTexCoord(vertexID);
                return o;
            }

            // Henyey-Greenstein 相位: 方向光在雾中的前向散射
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;
                return (1.0 - g2) / (4.0 * PI * pow(max(denom, 1e-4), 1.5));
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D_X(_HeightFog_SourceTex, sampler_HeightFog_SourceTex, input.uv);
                float depth = SampleSceneDepth(input.uv);

                bool isSkybox = false;
                #if UNITY_REVERSED_Z
                    if (depth < 1e-7) isSkybox = true;
                #else
                    if (depth > 0.9999999) isSkybox = true;
                #endif

                // Infinite-far sky depth can reconstruct an infinite point. A nearby
                // finite depth yields the same view direction without NaNs.
                #if UNITY_REVERSED_Z
                    float directionDepth = isSkybox ? 1e-4 : depth;
                #else
                    float directionDepth = isSkybox ? 0.9999 : depth;
                #endif
                float3 worldPos = ComputeWorldSpacePosition(input.uv, directionDepth, UNITY_MATRIX_I_VP);
                float3 rayVec = worldPos - _WorldSpaceCameraPos.xyz;
                float dist = length(rayVec);
                float3 rayDir = rayVec / max(dist, 1e-4);

                if (isSkybox) dist = 10000.0;

                // --- 距离 + 指数高度雾 ---
                float a = _FogDensity;
                float b = _HeightFalloff;
                float camHeight = _WorldSpaceCameraPos.y - _FogHeightStart;
                float t = b * rayDir.y;
                float fogAmount;
                if (abs(t) > 0.0001)
                    fogAmount = (a / b) * exp(-camHeight * b) * (1.0 - exp(-dist * t)) / rayDir.y;
                else
                    fogAmount = a * dist * exp(-camHeight * b);

                // Convert integrated optical thickness to the original fog opacity.
                fogAmount = 1.0 - exp(-fogAmount);

                // --- 近处平滑淡出 ---
                float proximityFade = smoothstep(_FogStartDistance, _FogStartDistance + _FogStartFadeRange, dist);
                fogAmount *= proximityFade;

                // --- 高空天空盒消散 ---
                if (isSkybox && rayDir.y > 0.0)
                    fogAmount /= (1.0 + rayDir.y * _SkyboxZenithFade);

                fogAmount = min(saturate(fogAmount), _MaxFogOpacity);

                // --- 方向光散射 ---
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float cosTheta = dot(rayDir, lightDir);
                float phase = HenyeyGreenstein(cosTheta, _ScatteringG);
                float scatterDist = max(dist - _InscatterStartDistance, 0.0);
                float scatterAtten = 1.0 - saturate(exp2(-scatterDist * 0.01));
                float lightLuma = dot(mainLight.color, half3(0.299, 0.587, 0.114));
                float scatter = saturate(phase * _SunScatterIntensity * scatterAtten * lightLuma);

                half3 fogColor = lerp(_FogColor.rgb, _InscatterColor.rgb, scatter);

                color.rgb = lerp(color.rgb, fogColor, fogAmount);
                return color;
            }
            ENDHLSL
        }
    }
}

Shader "Hidden/TAA"
{
    Properties
    {
        [MainTexture] _MainTex("MainTex", 2D) = "white" {}
        _Blend("Blend", Range(0,1)) = 0.1
        _ClampScale("Clamp Scale", Range(0.2, 2.0)) = 0.75
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        TEXTURE2D(_PreTex);  SAMPLER(sampler_PreTex);
        TEXTURE2D(_RawTex);  SAMPLER(sampler_RawTex);

        TEXTURE2D_X(_MotionVectorTexture); SAMPLER(sampler_MotionVectorTexture);
        TEXTURE2D(_PreMvTex); SAMPLER(sampler_PreMvTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float  _Blend;
            float  _ClampScale;
            float  _VelocityThreshold;
            float4 _DebugParams;
        CBUFFER_END

        // ─── Luma Weighting (Tonemap) ───────────────────────────────
        float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
        float3 Reinhard(float3 c) { return c / (1.0 + Luminance(c)); }
        float3 InverseReinhard(float3 c) { return c / (1.0 - Luminance(c)); }

        // ─── Motion Vector Dilation (运动矢量膨胀) ───────────────────
        float2 GetDilatedMotionVector(float2 uv, float2 texel)
        {
            float closestDepth = 100000.0;
            float2 dilatedUV = uv;

            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                [unroll]
                for (int y = -1; y <= 1; ++y)
                {
                    float2 offsetUV = uv + float2(x, y) * texel;
                    float rawDepth = SampleSceneDepth(offsetUV);
                    float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                    if (eyeDepth < closestDepth)
                    {
                        closestDepth = eyeDepth;
                        dilatedUV = offsetUV;
                    }
                }
            }
            return SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_MotionVectorTexture, dilatedUV).rg;
        }

        // ─── Fast Catmull-Rom Bicubic Filter ─────────────────────────
        float3 SampleHistoryBicubic(float2 uv, float2 texSize)
        {
            float2 samplePos = uv * texSize;
            float2 tc = floor(samplePos - 0.5) + 0.5;
            float2 f = samplePos - tc;
            float2 f2 = f * f;
            float2 f3 = f2 * f;

            float2 w0 = f2 - 0.5 * (f3 + f);
            float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
            float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
            float2 w3 = 0.5 * (f3 - f2);

            float2 w12 = w1 + w2;
            float2 tc12 = tc + w2 / max(w12, 1e-5);
            float2 tc0 = tc - 1.0;
            float2 tc3 = tc + 2.0;

            float3 c00 = SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, (float2(tc12.x, tc0.y))  / texSize).rgb;
            float3 c10 = SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, (float2(tc0.x,  tc12.y)) / texSize).rgb;
            float3 c11 = SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, (float2(tc12.x, tc12.y)) / texSize).rgb;
            float3 c12 = SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, (float2(tc3.x,  tc12.y)) / texSize).rgb;
            float3 c22 = SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, (float2(tc12.x, tc3.y))  / texSize).rgb;

            return c00 * (w12.x * w0.y) +
                   c10 * (w0.x  * w12.y) +
                   c11 * (w12.x * w12.y) +
                   c12 * (w3.x  * w12.y) +
                   c22 * (w12.x * w3.y);
        }

        // ─── YCoCg (归一化) ─────────────────────────────────────────
        float3 RGBToYCoCg(float3 c)
        {
            float Y  =  0.25 * c.r + 0.5 * c.g + 0.25 * c.b;
            float Co =  0.5  * c.r              - 0.5  * c.b;
            float Cg = -0.25 * c.r + 0.5 * c.g - 0.25 * c.b;
            return float3(Y, Co, Cg);
        }
        float3 YCoCgToRGB(float3 c)
        {
            float Y = c.x, Co = c.y, Cg = c.z;
            return float3(Y + Co - Cg, Y + Cg, Y - Co - Cg);
        }

        float3 ClipAABB(float3 history, float3 boxMin, float3 boxMax)
        {
            float3 center  = 0.5 * (boxMax + boxMin);
            float3 extents = 0.5 * (boxMax - boxMin) + 1e-5;
            float3 offset  = history - center;
            float3 ts = abs(extents) / max(abs(offset), 1e-5);
            float  t  = saturate(min(ts.x, min(ts.y, ts.z)));
            return center + offset * t;
        }
        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float2 uv : TEXCOORD0; float4 positionHCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            float3 ComputeTAA(float2 uv)
            {
                float2 texSize = _ScreenParams.xy;
                float2 texel = 1.0 / texSize;

                // 1. 运动矢量膨胀
                float2 motionVec = GetDilatedMotionVector(uv, texel);
                float2 historyUV = uv - motionVec;

                // 2. 采样（Bicubic）
                float3 cur = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb;
                float3 his = SampleHistoryBicubic(historyUV, texSize);

                cur = clamp(cur, 0.0, 65000.0);
                his = clamp(his, 0.0, 65000.0);

                // 3. 压制 HDR
                cur = Reinhard(cur);
                his = Reinhard(his);

                // 4. 3x3 邻域统计 (YCoCg + Variance)
                float3 m1 = 0, m2 = 0;
                float3 nMin = 1e9, nMax = -1e9;

                [unroll]
                for (int dx = -1; dx <= 1; dx++)
                {
                    [unroll]
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        float3 s = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(dx, dy) * texel).rgb;
                        s = clamp(s, 0.0, 65000.0);
                        s = Reinhard(s);

                        float3 y = RGBToYCoCg(s);
                        m1 += y;
                        m2 += y * y;
                        nMin = min(nMin, y);
                        nMax = max(nMax, y);
                    }
                }

                float3 mean   = m1 / 9.0;
                float3 var    = max(m2 / 9.0 - mean * mean, 0.0);
                float3 stddev = sqrt(var);
                float  gamma  = _ClampScale;

                float3 boxMin = max(mean - gamma * stddev, nMin);
                float3 boxMax = min(mean + gamma * stddev, nMax);

                // 5. Variance Clipping
                float3 hisYCoCg   = RGBToYCoCg(his);
                float3 hisClipped = ClipAABB(hisYCoCg, boxMin, boxMax);
                his = YCoCgToRGB(hisClipped);

                // 6. 速度拒绝 (Velocity Rejection)
                float2 historyMV = SAMPLE_TEXTURE2D(_PreMvTex, sampler_PreMvTex, historyUV).rg;
                float mvDiff = length(motionVec - historyMV);
                float disocclusionFactor = saturate(mvDiff / max(_VelocityThreshold, 1e-5));
                float currentBlend = lerp(_Blend, 1.0, disocclusionFactor);

                // 7. 最终混合与反解压
                float3 outRGB = lerp(his, cur, saturate(currentBlend));
                return InverseReinhard(outRGB);
            }

            // ─── 调试模式枚举 ─────────────────────────────────────
            // _DebugParams.w 控制调试模式：
            // 0 = 正常 TAA 输出
            // 1 = 显示 Motion Vector（静止时应为纯黑）
            // 2 = 显示 Dilated Motion Vector
            // 3 = 显示深度值（用于检查 LinearEyeDepth）
            // 4 = 显示历史帧（检查 Ping-Pong 是否正确）

            float4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;
                float2 texSize = _ScreenParams.xy;
                float2 texel = 1.0 / texSize;

                // 调试模式 1：显示 Motion Vector
                if (_DebugParams.w == 1.0)
                {
                    float2 motionVec = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_MotionVectorTexture, uv).rg;
                    return float4(abs(motionVec.x) * 100.0, abs(motionVec.y) * 100.0, 0.0, 1.0);
                }

                // 调试模式 2：显示 Dilated Motion Vector
                if (_DebugParams.w == 2.0)
                {
                    float2 motionVec = GetDilatedMotionVector(uv, texel);
                    return float4(frac(motionVec * 50.0), 0.0, 1.0);
                }

                // 调试模式 3：显示深度
                if (_DebugParams.w == 3.0)
                {
                    float rawDepth = SampleSceneDepth(uv);
                    float eyeDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                    return float4(eyeDepth / 100.0, eyeDepth / 100.0, eyeDepth / 100.0, 1.0);
                }

                // 调试模式 4：显示历史帧
                if (_DebugParams.w == 4.0)
                {
                    return float4(SAMPLE_TEXTURE2D(_PreTex, sampler_PreTex, uv).rgb, 1.0);
                }

                // 正常 TAA 计算
                float3 taaRGB = ComputeTAA(uv);

                // 分屏调试（如果有启用）
                if (_DebugParams.x > 0.5)
                {
                    float splitX  = _DebugParams.y;
                    float lineW   = _DebugParams.z;

                    float3 rawRGB = SAMPLE_TEXTURE2D(_RawTex, sampler_RawTex, uv).rgb;
                    rawRGB = clamp(rawRGB, 0.0, 65000.0);

                    float pxX = _ScreenParams.x * abs(uv.x - splitX);
                    if (pxX < lineW) return float4(1, 0, 0, 1);

                    float3 col = (uv.x < splitX) ? rawRGB : taaRGB;
                    return float4(col, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a);
                }

                return float4(taaRGB, SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).a);
            }
            ENDHLSL
        }
    }
}

Shader "Hidden/Rollaball/RadialRedshift"
{
    Properties
    {
        // ── 运动模糊（自定义，不经过 URP 的 0.2 clamp）──
        // 直接采样运动向量贴图，强度可任意放大：
        // 1.0 = 物理正确的拖影长度，2.0 = 两倍夸张，越大越明显
        _MotionBlurStrength ("Motion Blur Strength", Range(0, 8)) = 0

        // ── 径向拖影（速度线）──
        // 0 = 无效果，越大拉伸越强
        _Strength ("Radial Strength", Range(0, 0.1)) = 0

        // ── 色差 ──
        // 直接是 UV 空间的分离量，0.005 左右就很明显了，由脚本按速度驱动
        _ChromaticSpread ("Chromatic Spread", Range(0, 0.05)) = 0

        // 径向衰减指数：越小中心越清晰、边缘越强
        _FalloffPower ("Falloff Power", Range(0.5, 4)) = 1.5
        // 采样步数：越大越平滑，性能开销越高
        _Samples ("Samples", Range(2, 24)) = 10
        // 模糊中心（屏幕 UV 空间）
        _Center ("Center", Vector) = (0.5, 0.5, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "RadialRedshift"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // URP 核心库 + Blit 工具（提供 _BlitTexture / Varyings / Vert）
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // URP 的运动向量贴图（由 MotionVectorRenderPass 设为全局纹理）。
            // URP 的 shader library 没有声明它，所以这里可以安全地自行声明。
            TEXTURE2D_X(_MotionVectorTexture);

            CBUFFER_START(UnityPerMaterial)
                float  _MotionBlurStrength;
                float  _Strength;
                float  _ChromaticSpread;
                float  _FalloffPower;
                float  _Samples;
                float4 _Center;
            CBUFFER_END

            // 综合速度模糊：运动模糊 + 径向拖影合并到同一个采样循环里
            //
            //   motion      —— 运动向量（UV 空间位移）
            //   dir         —— 中心指向当前像素的径向方向
            //   radialAmt   —— 径向拖影强度
            //
            // t 取 -0.5 ~ +0.5，围绕当前像素向两侧对称采样，
            // 这样得到的是"双向拖影"而不是单侧偏移。
            half3 SampleSpeedBlur(float2 uv, float2 motion, float2 dir, float radialAmt, int samples)
            {
                half3 accum = 0;

                for (int i = 0; i < samples; i++)
                {
                    float t = ((float)i / (float)(samples - 1)) - 0.5;

                    float2 offset = motion * _MotionBlurStrength * t
                                  + dir * radialAmt * t;

                    float2 sampleUV = clamp(uv + offset, 0.001, 0.999);
                    accum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, sampleUV).rgb;
                }

                return accum / (float)samples;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float2 center = _Center.xy;
                float2 offset = uv - center;

                // 到中心的距离，用于径向衰减（中心清晰，边缘拉伸）
                float dist = length(offset);

                // 归一化方向，加 epsilon 防止中心点 normalize(0) 产生 NaN
                float2 dir = normalize(offset + 1e-5);

                // 径向衰减：中心趋于 0，边缘趋于 1
                float falloff = saturate(pow(dist * 2.0, _FalloffPower));
                float radialAmt = _Strength * falloff;

                int samples = (int)clamp(_Samples, 2, 24);

                half3 color;

                // 两者都为 0 时直接返回原图，避免无谓采样
                if (_MotionBlurStrength <= 0.0001 && radialAmt <= 0.0001)
                {
                    return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                }

                // 运动向量：UV 空间位移（0.1 = 屏幕的 10%）
                half2 motion = SAMPLE_TEXTURE2D_X(_MotionVectorTexture, sampler_PointClamp, uv).xy;

                color = SampleSpeedBlur(uv, motion, dir, radialAmt, samples);

                // ── 色差（RGB 分离）──
                if (_ChromaticSpread > 0.0001)
                {
                    // _ChromaticSpread 直接就是 UV 空间的分离量，由脚本按速度驱动
                    float spread = _ChromaticSpread;

                    float2 uvR = clamp(uv + dir * spread, 0.001, 0.999);
                    float2 uvB = clamp(uv - dir * spread * 0.5, 0.001, 0.999);

                    color.r = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uvR).r;
                    color.b = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uvB).b;
                }

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}

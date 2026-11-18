Shader "UI/GlitchSlotUI"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Intensity ("Intensity", Range(0,1)) = 0
        _UnscaledTime ("Unscaled Time", Float) = 0
        _Seed ("Seed", Float) = 0
        _RgbSplit ("RGB Split (UV)", Range(0,0.05)) = 0.006
        _BlockStrength ("Block Shift (UV)", Range(0,0.2)) = 0.035
        _BlurStrength ("Trail Blur (UV)", Range(0,0.05)) = 0.006
        _ScanlineStrength ("Scanline", Range(0,1)) = 0.28
        _NoiseStrength ("Noise", Range(0,1)) = 0.35
        _BorderColor ("Border Color", Color) = (0.55,0.9,1,1)
        _BorderWidth ("Border Width (px)", Float) = 2
        _RectSize ("Rect Size", Vector) = (920,150,0,0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float4 _ClipRect;

            float _Intensity;
            float _UnscaledTime;
            float _Seed;
            float _RgbSplit;
            float _BlockStrength;
            float _BlurStrength;
            float _ScanlineStrength;
            float _NoiseStrength;
            fixed4 _BorderColor;
            float _BorderWidth;
            float4 _RectSize;

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            float Hash11(float value)
            {
                value = frac(value * 0.1031);
                value *= value + 33.33;
                value *= value + value;
                return frac(value);
            }

            float Hash21(float2 value)
            {
                float3 p = frac(float3(value.xyx) * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // 像素精度的内边框遮罩：纯色背景也能看出位移和色差
            float BorderMask(float2 uv, float2 size, float width)
            {
                float2 px = uv * size;
                float2 edge = min(px, size - px);
                float distanceToEdge = min(edge.x, edge.y);
                return 1.0 - smoothstep(width - 0.5, width + 1.5, distanceToEdge);
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                float intensity = saturate(_Intensity);
                float time = _UnscaledTime;
                float2 rectSize = max(_RectSize.xy, float2(1.0, 1.0));

                // 水平分块撕裂：按行随机左右位移，随时间跳变
                float bandCount = 26.0;
                float band = floor(uv.y * bandCount);
                float stepId = floor(time * 7.0);
                float bandNoise = Hash11(band * 1.37 + stepId * 3.11 + _Seed);
                float bandActive = step(0.62, Hash11(band * 4.73 + stepId * 1.93 + _Seed));
                float shift = (bandNoise - 0.5) * 2.0 * _BlockStrength * intensity * bandActive;
                float tear = step(0.93, Hash11(floor(uv.y * 5.0) + stepId * 7.31 + _Seed));
                shift += tear * (Hash11(stepId + _Seed) - 0.5) * _BlockStrength * 2.0 * intensity;
                uv.x += shift;

                // 色差：R / B 反向偏移
                float split = _RgbSplit * intensity;
                half4 center = tex2D(_MainTex, uv);
                half4 trailA = tex2D(_MainTex, uv + float2(_BlurStrength * intensity, 0));
                half4 trailB = tex2D(_MainTex, uv - float2(_BlurStrength * intensity, 0));
                half4 trailC = tex2D(_MainTex, uv + float2(_BlurStrength * 2.0 * intensity, 0));
                half4 blurred = (center + trailA + trailB + trailC) * 0.25;

                half4 col;
                col.r = tex2D(_MainTex, uv + float2(split, 0)).r;
                col.g = tex2D(_MainTex, uv).g;
                col.b = tex2D(_MainTex, uv - float2(split, 0)).b;
                col.a = center.a;
                col.rgb = lerp(col.rgb, blurred.rgb, saturate(intensity * 0.5));

                // 边框：色差 + 位移 + 方向性拖影
                float bR = BorderMask(uv + float2(split, 0), rectSize, _BorderWidth);
                float bG = BorderMask(uv, rectSize, _BorderWidth);
                float bB = BorderMask(uv - float2(split, 0), rectSize, _BorderWidth);
                float trail1 = BorderMask(uv + float2(_BlurStrength * intensity, 0), rectSize, _BorderWidth);
                float trail2 = BorderMask(uv + float2(_BlurStrength * 2.4 * intensity, 0), rectSize, _BorderWidth);
                float trail3 = BorderMask(uv + float2(_BlurStrength * 4.0 * intensity, 0), rectSize, _BorderWidth);
                float borderSmear = trail1 * 0.35 + trail2 * 0.18 + trail3 * 0.08;
                float borderAlpha = max(max(bR, max(bG, bB)), borderSmear) * intensity * _BorderColor.a;
                col.rgb += _BorderColor.rgb * (bR + bG + bB + borderSmear) * intensity * 0.75;

                // 扫描线与噪点：给纯色区域提供可撕裂的细节
                float scan = 0.5 + 0.5 * sin(uv.y * rectSize.y * 1.2 + time * 6.0);
                col.rgb *= 1.0 - _ScanlineStrength * intensity * scan * 0.6;
                float noise = Hash21(uv * rectSize + float2(time * 37.0, time * 11.0));
                col.rgb += (noise - 0.5) * _NoiseStrength * intensity * 0.35;

                // 横向故障亮条：槽位填充本身 alpha 很低，这里单独给可见的亮度与 alpha
                float barRow = floor(uv.y * rectSize.y / 3.0);
                float barNoise = Hash11(barRow * 0.73 + stepId * 2.17 + _Seed);
                float bar = step(0.93, barNoise);
                float scanBand = smoothstep(0.7, 1.0, scan);
                float glitch = max(bar, scanBand * 0.45);
                col.rgb += _BorderColor.rgb * glitch * intensity * 0.9;
                float glitchAlpha = glitch * intensity * 0.38;

                #ifdef UNITY_UI_CLIP_RECT
                col.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(col.a - 0.001);
                #endif

                // 背景 Image 本身 alpha 很低，故障边框和亮条单独抬 alpha，否则会被整体透明度压没
                fixed4 outColor;
                outColor.rgb = col.rgb * IN.color.rgb;
                outColor.a = max(max(col.a * IN.color.a, borderAlpha), glitchAlpha);
                return outColor;
            }
            ENDCG
        }
    }
}

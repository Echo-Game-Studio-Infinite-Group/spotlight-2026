// 火花/闪光类粒子专用着色器：预乘 alpha 混合 + HDR 染色。
//
// 混合口径（调研结论）：本作是冷白亮面的超净室环境，additive 在灰白场景会直接洗白、
// 丢失火花的形与色；预乘 alpha（Blend One OneMinusSrcAlpha）在亮面保留形色、
// 暗面等价加光观感——两种环境下都立得住。贴图 alpha 塑形，RGB 常保亮度（熄灭交给 alpha 收零），
// 避免「RGB 熄黑 + alpha 残留」在亮面上压出灰渍。
//
// HDR 染色（_Tint 分量 > 1）：镜头有 Bloom 时出辉光，没有 Bloom 时也只是更亮，
// 不会失真——两种后期条件下都能立住，是本作特效不依赖后处理的保底手段。
Shader "GameJam/SparkAdditive"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HDR] _Tint ("HDR 染色", Color) = (2, 2, 2, 1)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "SparkAdditive"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                half alpha = tex.a * input.color.a;                     // 贴图轮廓 × 寿命渐变
                half3 rgb = tex.rgb * input.color.rgb * _Tint.rgb * alpha; // RGB 预乘 alpha，供 Blend SrcAlpha One
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

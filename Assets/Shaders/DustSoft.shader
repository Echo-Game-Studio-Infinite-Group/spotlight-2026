// 尘土 / 烟尘专用着色器：标准 alpha 混合 + 程序化软圆（不依赖任何贴图）。
//
// 为什么自己搓一个：复用火焰 kit 的软烟材质，一旦材质没接上（Resources 没导好 / 预制体里存的是
// 运行时 new 出来的材质），ParticleSystemRenderer 会退回内置默认粒子材质——在 URP 下那是一整片品红/紫黑。
// 尘土这种东西不该有这种沉默失败，所以：程序化形状 + 标准 alpha 混合 + 只依赖 URP Core。
//
// 混合口径：Blend SrcAlpha OneMinusSrcAlpha（尘土是"挡光"的东西，不是发光体，和火花的预乘 additive 相反）。
// 颜色全部来自粒子的顶点色（startColor / colorOverLifetime），shader 只负责塑形与淡边。
Shader "GameJam/DustSoft"
{
    Properties
    {
        _MainTex ("Texture（可选，留空=程序化软圆）", 2D) = "white" {}
        _Softness ("边缘柔度", Range(0.5, 6)) = 2.4
        _Opacity ("整体不透明度", Range(0, 1)) = 0.55
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "DustSoft"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half _Softness;
                half _Opacity;
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

                // 程序化软圆：中心实、边缘按 smoothstep 淡出（粒子四边形 UV 就是 0..1）
                float2 centered = input.uv * 2.0 - 1.0;
                half radius = saturate(1.0 - length(centered));
                half falloff = pow(radius, _Softness);

                // 再叠一层低频起伏，避免圆得太规整（用 UV 做，不引入噪声贴图）
                half grain = 0.85 + 0.15 * sin(input.uv.x * 12.0 + input.uv.y * 9.0);

                half alpha = tex.a * input.color.a * falloff * grain * _Opacity;
                half3 rgb = tex.rgb * input.color.rgb;
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

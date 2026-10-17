Shader "GameJam/PlayerAfterimage"
{
    Properties
    {
        [HDR] _BaseColor("Body Color", Color) = (0.04, 0.22, 1.1, 0.32)
        [HDR] _EdgeColor("Edge Color", Color) = (0.08, 0.85, 3, 0.65)
        _Opacity("Opacity", Range(0, 1)) = 0.75
        _EdgePower("Edge Power", Range(0.1, 8)) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent-10" }
        Pass
        {
            Name "Afterimage"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _EdgeColor;
                float _Opacity;
                float _EdgePower;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float edge = pow(saturate(1.0 - abs(dot(normalize(input.normalWS), view))), max(0.1, _EdgePower));
                float4 color = lerp(_BaseColor, _EdgeColor, edge);
                return half4(color.rgb, saturate(color.a * _Opacity));
            }
            ENDHLSL
        }
    }
}

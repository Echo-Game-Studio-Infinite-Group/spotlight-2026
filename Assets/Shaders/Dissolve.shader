Shader "GameJam/Dissolve"
{
    Properties
    {
        [Enum(PerlinNoise, 0, VoronoiNoise, 1)]_Noise("Noise Type", Float) = 0
        _MainTex ("Texture", 2D) = "white" {}
        _NoiseScale("Noise Scale", Float) = 150
        _NoiseStrength("Noise Strength", Range(0, 1)) = 0.5
        _CutoffHeight("Cutoff Height", Float) = 0.0
        _CycleSpeed("Cycle Speed", Range(0, 3)) = 1.0
        _EdgeColor("Edge Color", Color) = (1, 1, 1, 1)
        _EdgeStroke("Edge Stroke", Float) = 0.1
        _Sharpness("Sharpness", Float) = 0.1
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }

        Cull Off
        ZWrite On
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "./NoiseFunctions.hlsl"

            struct appdata
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                float4 color    : COLOR;
                float2 uv       : TEXCOORD0;
                float4 localPos  : TEXCOORD1;
            };
            
            float _Noise;
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _NoiseScale;
            float _NoiseStrength;
            float _CutoffHeight;
            float _CycleSpeed;
            half4 _EdgeColor;
            float _EdgeStroke;
            float _Sharpness;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                // o.uv = v.uv;
                o.localPos = v.vertex;
                o.color = v.color;
                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                float alpha = tex2D(_MainTex, i.uv).a;
                clip(alpha - 0.5);

                float noise = 0.0;

                if(_Noise == 0)
                {
                    noise = (perlinNoise(i.uv, _NoiseScale, _Time.y * _CycleSpeed) - 0.5) * _NoiseStrength;
                }
                else
                {
                    float centerdist, edgedist;
                    voronoiNoise(i.uv, _NoiseScale, centerdist, edgedist, _Time.y * _CycleSpeed);
                    noise = (edgedist - 0.5) * _NoiseStrength;
                }

                float height = i.localPos.y + noise;
                clip(height - _CutoffHeight);

                float edge = step(_CutoffHeight + _EdgeStroke, height);
                half3 finalColor = edge? i.color.xyz : lerp(i.color.xyz, _EdgeColor.xyz, 0.7);
                return half4(finalColor, alpha * i.color.a);
            }
            ENDCG
        }
    }
}

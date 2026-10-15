Shader "Hidden/OurFunction/CapsuleAO"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "CharacterCapsuleContactAO"
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"
            TEXTURE2D_X(_NPRShadow);
            TEXTURE2D_X(_GBuffer2);
            float4 _CapsuleAOStart[32], _CapsuleAOEnd[32], _CapsuleAOSettings;
            float4x4 _CapsuleAOInverseVP;
            int _CapsuleAOCount;
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 color = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
                float depth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                if (depth < .000001) return color;
                #else
                if (depth > .999999) return color;
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
                #endif
                // 角色本体由实时光照和 GTAO 处理，胶囊只给邻近接收物体提供接触遮蔽。
                if (SAMPLE_TEXTURE2D_X_LOD(_NPRShadow, sampler_PointClamp, uv, 0).a > 1.5) return color;
                float3 positionWS = ComputeWorldSpacePosition(uv, depth, _CapsuleAOInverseVP);
                float3 normal = normalize(UnpackNormal(SAMPLE_TEXTURE2D_X_LOD(_GBuffer2, sampler_PointClamp, uv, 0).xyz));
                float visibility = 1;
                [loop] for (int i = 0; i < _CapsuleAOCount; i++)
                {
                    float3 a = _CapsuleAOStart[i].xyz, segment = _CapsuleAOEnd[i].xyz - a;
                    float t = saturate(dot(positionWS - a, segment) / max(dot(segment, segment), .000001));
                    float3 toAxis = a + t * segment - positionWS;
                    float radius = _CapsuleAOStart[i].w;
                    float distanceSquared = max(dot(toAxis, toAxis), .000001);
                    float distanceToAxis = sqrt(distanceSquared);
                    float gap = max(0, distanceToAxis - radius);
                    float falloff = 1 - smoothstep(0, _CapsuleAOSettings.y, gap);
                    // 球冠投影立体角近似；最近轴点提供胶囊保守遮蔽，乘半球余弦而非屏幕平面贴影。
                    float solidAngle = saturate(radius * radius / distanceSquared);
                    float hemisphere = saturate(dot(normal, toAxis / distanceToAxis));
                    visibility *= 1 - solidAngle * hemisphere * falloff;
                }
                color.rgb *= 1 - (1 - visibility) * _CapsuleAOSettings.x;
                return color;
            }
            ENDHLSL
        }
    }
}

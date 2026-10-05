Shader "GameJam/EnemyWaistCutout"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _Metallic ("Metallic", Range(0,1)) = 0
        _MetallicGlossMap ("Metallic Map", 2D) = "white" {}
        _SpecColor ("Specular Color", Color) = (0.2,0.2,0.2,1)
        _SpecGlossMap ("Specular Map", 2D) = "white" {}
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _OcclusionMap ("Occlusion Map", 2D) = "white" {}
        _OcclusionStrength ("Occlusion Strength", Range(0,1)) = 1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _CutColor ("Cut Surface", Color) = (0.24,0.025,0.02,1)
        [HideInInspector] _CutSurface ("Is Cut Cap", Float) = 0
        _CutPlane ("Local Cut Plane", Vector) = (0,1,0,0)
        _CutSide ("Kept Side", Float) = 1
        _UseCutDistance ("Use Death Pose Distance", Float) = 0
        _CutEdge ("Edge Width", Float) = 0.015
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull [_Cull]
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_MetallicGlossMap); SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_SpecGlossMap); SAMPLER(sampler_SpecGlossMap);
        TEXTURE2D(_OcclusionMap); SAMPLER(sampler_OcclusionMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _EmissionColor;
            half4 _SpecColor;
            half _BumpScale;
            half _Metallic;
            half _Smoothness;
            half _OcclusionStrength;
            half _Cutoff;
            half4 _CutColor;
            float _CutSurface;
            float4 _CutPlane;
            float _CutSide;
            float _UseCutDistance;
            float _CutEdge;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
            float2 cutDistance : TEXCOORD1;
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float distance : TEXCOORD3;
            half fog : TEXCOORD4;
            half4 tangentWS : TEXCOORD5;
            float4 shadowCoord : TEXCOORD6;
            half3 vertexLight : TEXCOORD7;
        };
        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.positionWS);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.tangentWS = half4(TransformObjectToWorldDir(input.tangentOS.xyz),
                input.tangentOS.w * GetOddNegativeScale());
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            output.distance = lerp(dot(_CutPlane.xyz, input.positionOS.xyz) + _CutPlane.w,
                input.cutDistance.x, _UseCutDistance);
            output.fog = ComputeFogFactor(output.positionCS.z);
            VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
            output.shadowCoord = GetShadowCoord(positions);
            output.vertexLight = VertexLighting(output.positionWS, output.normalWS);
            return output;
        }
        void Cut(Varyings input)
        {
            clip(input.distance * _CutSide);
            #if defined(_ALPHATEST_ON)
                Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
            #endif
        }
        bool IsCutSurface(Varyings input) { return _CutSurface > 0.5 || abs(input.distance) < _CutEdge; }
        void GetSurface(Varyings input, out SurfaceData surface)
        {
            surface = (SurfaceData)0;
            half4 base = SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
            surface.alpha = 1;
            surface.albedo = base.rgb * _BaseColor.rgb;
            surface.smoothness = _Smoothness;
            #if defined(_SPECULAR_SETUP)
                surface.specular = _SpecColor.rgb;
                surface.metallic = 1;
            #else
                surface.metallic = _Metallic;
            #endif
            #if defined(_METALLICSPECGLOSSMAP)
                #if defined(_SPECULAR_SETUP)
                    half4 gloss = SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, input.uv);
                    surface.specular = gloss.rgb;
                #else
                    half4 gloss = SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, input.uv);
                    surface.metallic = gloss.r;
                #endif
                surface.smoothness *= gloss.a;
            #endif
            #if defined(_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A)
                surface.smoothness = _Smoothness * base.a;
            #endif
            surface.occlusion = 1;
            #if defined(_OCCLUSIONMAP)
                half occlusion = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, input.uv).g;
                surface.occlusion = lerp(1, occlusion, _OcclusionStrength);
            #endif
            surface.normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
            surface.emission = SampleEmission(input.uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
            // 薄片背面仍是原材质，只有补出的断口与裁切窄边使用断口色。
            if (IsCutSurface(input))
            {
                surface.albedo = _CutColor.rgb;
                surface.emission = 0;
                surface.normalTS = half3(0, 0, 1);
                surface.metallic = 0;
                surface.specular = half3(0.04, 0.04, 0.04);
                surface.smoothness = 0;
                surface.occlusion = 1;
            }
        }
        half3 GetNormal(Varyings input, half3 normalTS, half facing)
        {
            half3 normal = input.normalWS;
            #if defined(_NORMALMAP)
                half3 bitangent = input.tangentWS.w * cross(normal, input.tangentWS.xyz);
                normal = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent, normal));
            #endif
            // 主体遵循原 URP Lit 的法线；双面断口需要朝向当前可见的一侧。
            return NormalizeNormalPerPixel(normal) * (_CutSurface > 0.5 ? facing : 1);
        }
        half4 Depth(Varyings input) : SV_Target { Cut(input); return 0; }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            // 材质在死亡时才生成，构建时不能按静态材质剔除这些变体。
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local _ _RECEIVE_SHADOWS_OFF
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _EMISSION
            #pragma multi_compile_local_fragment _ _METALLICSPECGLOSSMAP
            #pragma multi_compile_local_fragment _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_local_fragment _ _OCCLUSIONMAP
            #pragma multi_compile_local_fragment _ _SPECULAR_SETUP
            #pragma multi_compile_local_fragment _ _SPECULARHIGHLIGHTS_OFF
            #pragma multi_compile_local_fragment _ _ENVIRONMENTREFLECTIONS_OFF
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                Cut(input);
                half facing = IS_FRONT_VFACE(frontFace, 1.0, -1.0);
                SurfaceData surface;
                GetSurface(input, surface);
                InputData lighting = (InputData)0;
                lighting.positionWS = input.positionWS;
                lighting.normalWS = GetNormal(input, surface.normalTS, facing);
                lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lighting.shadowCoord = input.shadowCoord;
                lighting.vertexLighting = input.vertexLight;
                lighting.bakedGI = SampleSH(lighting.normalWS);
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask = half4(1,1,1,1);
                half4 color = UniversalFragmentPBR(lighting, surface);
                color.rgb = MixFog(color.rgb, input.fog);
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment Depth
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection;
            float3 _LightPosition;
            Varyings ShadowVert(Attributes input)
            {
                Varyings output = Vert(input);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 direction = normalize(_LightPosition - output.positionWS);
                #else
                    float3 direction = _LightDirection;
                #endif
                output.positionCS = TransformWorldToHClip(ApplyShadowBias(output.positionWS, output.normalWS, direction));
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE * output.positionCS.w);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE * output.positionCS.w);
                #endif
                return output;
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Depth
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Normals
            #pragma multi_compile_local _ _NORMALMAP
            #pragma multi_compile_local_fragment _ _ALPHATEST_ON
            #pragma multi_compile_local_fragment _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 Normals(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                Cut(input);
                half3 normalTS = IsCutSurface(input) ? half3(0,0,1)
                    : SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
                half3 normalWS = GetNormal(input, normalTS, IS_FRONT_VFACE(frontFace, 1.0, -1.0));
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormal = PackNormalOctQuadEncode(normalWS);
                    return half4(PackFloat2To888(saturate(octNormal * 0.5 + 0.5)), 0);
                #else
                    return half4(normalWS, 0);
                #endif
            }
            ENDHLSL
        }
    }
}

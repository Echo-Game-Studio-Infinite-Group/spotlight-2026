// Derived from Unity URP14.0.11 Lit sources. Copyright Unity Technologies ApS.
// Unity Companion License: https://www.unity3d.com/legal/licenses/Unity_Companion_License
Shader "OurFunction/Metal/BrushedMetal"
{
    Properties
    {
        [Header(Brushed Metal)]
        [Toggle] _MetalFlowMapEnabled("Use Brushed Metal Direction Map", Float) = 0
        _MetalFlowMap("Metal Direction RG / Brush Response B (Linear)", 2D) = "gray" {}
        _MetalAnisotropy("Metal Anisotropy", Range(-0.9,0.9)) = 0.7
        _MetalReflectanceOverride("Metal Stylized Direct Reflectance Override", Range(0,1)) = 0
        _MetalReflectanceColor("Metal Stylized Direct Reflectance Color", Color) = (0.4,0.45,0.5,1)
        _MetalRoughness("Metal Roughness (No Roughness Map)", Range(0.04,1)) = 0.28
        _MetalRoughnessScale("Metal Roughness Multiplier", Range(0.25,2)) = 1
        _MetalBrushScale("Metal Brush Detail Tiling", Range(0.1,8)) = 1
        _MetalBrushStrength("Metal Brush Roughness Variation", Range(0,1)) = 0.25
        _MetalSpecBandStrength("Metal Highlight Band Strength", Range(0,1)) = 0.75
        _MetalSpecBandThreshold("Metal Highlight Band Threshold", Range(0.01,0.99)) = 0.4
        _MetalSpecBandSoftness("Metal Highlight Band Softness", Range(0.005,0.3)) = 0.1
        _MetalFillColor("Metal Soft Fill Color", Color) = (1,1,1,1)
        _MetalFillStrength("Metal Soft Fill Strength", Range(0,1)) = 0.18
        _MetalFillDirectionVS("Metal Fill View Space Direction", Vector) = (0.3,0.4,1,0)
        _MetalMinLight("Metal Minimum Readability", Range(0,0.5)) = 0.08
        [Header(Geometry Outline)]
        [Toggle] _SmoothNormalOutline("Smooth Normal Outline (UV3)", Float) = 0
        [Toggle] _OverrideOutline("Override Outline", Float) = 0
        [Toggle] _OutlineEnabled("Override Outline Enabled", Float) = 1
        _OutlineWidth("Override Outline Width Pixels", Range(0,6)) = 1.5
        [Toggle] _OverrideOutlineColor("Custom Outline Color", Float) = 0
        [HDR] _OutlineColor("Outline Color", Color) = (0.035,0.045,0.07,1)
        _OutlineBrightness("Outline Brightness", Range(0,8)) = 1
        [Toggle] _OutlineGeometryEdges("Outline Geometric Creases", Float) = 0
        // Specular vs Metallic workflow
        _WorkflowMode("WorkflowMode", Float) = 1.0

        [MainTexture] _BaseMap("Albedo", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)

        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SmoothnessTextureChannel("Smoothness texture channel", Float) = 0

        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _MetallicGlossMap("Metallic", 2D) = "white" {}
        _RoughnessMap("Roughness R (Linear, overrides Smoothness)", 2D) = "white" {}

        _SpecColor("Specular", Color) = (0.2, 0.2, 0.2)
        _SpecGlossMap("Specular", 2D) = "white" {}

        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0

        _BumpScale("Scale", Float) = 1.0
        _BumpMap("Normal Map", 2D) = "bump" {}

        _Parallax("Scale", Range(0.005, 0.08)) = 0.005
        _ParallaxMap("Height Map", 2D) = "black" {}

        _OcclusionStrength("Strength", Range(0.0, 1.0)) = 1.0
        _OcclusionMap("Occlusion", 2D) = "white" {}

        [Toggle] _EmissionEnabled("Emission Enabled", Float) = 0
        [Toggle] _EmissionUseMap("Use Emission Map", Float) = 0
        _EmissionIntensity("Emission Intensity", Range(0,16)) = 1
        [HDR] _EmissionColor("Color", Color) = (0,0,0)
        _EmissionMap("Emission", 2D) = "white" {}

        _DetailMask("Detail Mask", 2D) = "white" {}
        _DetailAlbedoMapScale("Scale", Range(0.0, 2.0)) = 1.0
        _DetailAlbedoMap("Detail Albedo x2", 2D) = "linearGrey" {}
        _DetailNormalMapScale("Scale", Range(0.0, 2.0)) = 1.0
        [Normal] _DetailNormalMap("Normal Map", 2D) = "bump" {}

        // SRP batching compatibility for Clear Coat (Not used in Lit)
        [HideInInspector] _ClearCoatMask("_ClearCoatMask", Float) = 0.0
        [HideInInspector] _ClearCoatSmoothness("_ClearCoatSmoothness", Float) = 0.0

        // Blending state
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        // Editmode props
        _QueueOffset("Queue offset", Float) = 0.0

        // ObsoleteProperties
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _GlossMapScale("Smoothness", Float) = 0.0
        [HideInInspector] _Glossiness("Smoothness", Float) = 0.0
        [HideInInspector] _GlossyReflections("EnvironmentReflections", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }

    SubShader
    {
        // Universal Pipeline tag is required. If Universal render pipeline is not set in the graphics settings
        // this Subshader will fail. One can add a subshader below or fallback to Standard built-in to make this
        // material work with both Universal Render Pipeline and Builtin Unity Pipeline
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        // ------------------------------------------------------------------
        //  Forward pass. Shades all light in a single pass. GI + emission + Fog


        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull [_Cull] ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 3.5
            #pragma vertex LitPassVertex
            #pragma fragment MetalPreviewFragment
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #include "MetalInput.hlsl"
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            #include "MetalLighting.hlsl"
            half4 MetalPreviewFragment(Varyings input):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                SurfaceData surface; InitializeStandardLitSurfaceData(input.uv, surface);
                InputData data; InitializeInputData(input, surface.normalTS, data);
                float4 shadowCoord = data.shadowCoord;
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    shadowCoord = ComputeScreenPos(TransformWorldToHClip(data.positionWS));
                #endif
                float3 color = MetalDirectLighting(surface, input.uv, data.positionWS, data.normalWS,
                    input.tangentWS, data.viewDirectionWS, shadowCoord, false);
                BRDFData brdf; InitializeBRDFData(surface, brdf);
                color += GlobalIllumination(brdf, data.bakedGI, surface.occlusion,
                    data.positionWS, data.normalWS, data.viewDirectionWS) + surface.emission;
                return half4(color, surface.alpha);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Universal Pipeline keywords

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            // This is used during shadow map generation to differentiate between directional and punctual light shadows, as they use different formulas to apply Normal Bias
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            // Lightmode matches the ShaderPassName set in UniversalRenderPipeline.cs. SRPDefaultUnlit and passes with
            // no LightMode tag are also rendered by Universal Render Pipeline
            Name "GBuffer"
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite[_ZWrite]
            ZTest LEqual
            Cull[_Cull]

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 4.5

            // Deferred Rendering Path does not support the OpenGL-based graphics API:
            // Desktop OpenGL, OpenGL ES 3.0, WebGL 2.0.
            #pragma exclude_renderers gles3 glcore

            // -------------------------------------
            // Shader Stages
            #pragma vertex LitGBufferPassVertex
            #pragma fragment LitGBufferPassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            //#pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED

            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            //#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            //#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // This pass is used when drawing to a _CameraNormalsTexture texture
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _PARALLAXMAP
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE

            // -------------------------------------
            // Universal Pipeline keywords
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
            ENDHLSL
        }

        // This pass it not used during regular rendering, only for lightmap baking.
        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode" = "Meta"
            }

            // -------------------------------------
            // Render State Commands
            Cull Off

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex UniversalVertexMeta
            #pragma fragment UniversalFragmentMetaLit

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _SPECULAR_SETUP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _ _DETAIL_MULX2 _DETAIL_SCALED
            #pragma shader_feature_local_fragment _SPECGLOSSMAP
            #pragma shader_feature EDITOR_VISUALIZATION

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitMetaPass.hlsl"

            ENDHLSL
        }

        Pass
        {
            Name "Universal2D"
            Tags
            {
                "LightMode" = "Universal2D"
            }

            // -------------------------------------
            // Render State Commands
            Blend[_SrcBlend][_DstBlend]
            ZWrite[_ZWrite]
            Cull[_Cull]

            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex vert
            #pragma fragment frag

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/Universal2D.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "NPRStyleData"
            Tags { "LightMode"="NPRStyleData" }
            Cull [_Cull] ZWrite Off ZTest Equal
            HLSLPROGRAM
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma target 4.5
            #pragma vertex LitGBufferPassVertex
            #pragma fragment MetalStyleFragment
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #include "MetalInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl"
            #include "MetalLighting.hlsl"
            // a=2 是管线自定义直接光协议，和角色光照模型或材质父子关系无关。
            struct MetalStyleOutput { half4 direct : SV_Target0; half4 reserved : SV_Target1; half4 outline : SV_Target2; half4 outlineColor : SV_Target3; };
            MetalStyleOutput MetalStyleFragment(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                SurfaceData surface; InitializeStandardLitSurfaceData(input.uv, surface);
                InputData data; InitializeInputData(input, surface.normalTS, data);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
                #endif
                MetalStyleOutput output;
                output.direct = half4(MetalDirectLighting(surface, input.uv, input.positionWS, data.normalWS,
                    input.tangentWS, data.viewDirectionWS, shadowCoord, true), 2);
                output.reserved = 0;
                float width = _OverrideOutline > 0.5 ? _OutlineWidth * _OutlineEnabled : -1;
                if (_SmoothNormalOutline > 0.5) width = 0;
                output.outline = half4(0, 7, width,
                    1 + (_OverrideOutlineColor > 0.5 ? 2 : 0) + (_OutlineGeometryEdges > 0.5 ? 4 : 0));
                output.outlineColor = half4(_OutlineColor.rgb * _OutlineBrightness, 1);
                return output;
            }
            ENDHLSL
        }
        Pass
        {
            Name "NPRCharacterOutline"
            Tags { "LightMode"="NPRCharacterOutline" }
            Cull Front ZWrite Off ZTest LEqual
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MetalOutlineVertex
            #pragma fragment MetalOutlineFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON
            #include "MetalInput.hlsl"
            struct OutlineAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float3 smoothNormalTS : TEXCOORD2;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct OutlineVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            OutlineVaryings MetalOutlineVertex(OutlineAttributes input)
            {
                OutlineVaryings output = (OutlineVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 normalOS = SafeNormalize(input.normalOS);
                float3 tangentOS = input.tangentOS.xyz - normalOS * dot(input.tangentOS.xyz, normalOS);
                float tangentLength = dot(tangentOS, tangentOS);
                if (dot(input.smoothNormalTS, input.smoothNormalTS) > 0.01 && tangentLength > 0.0001)
                {
                    tangentOS = normalize(tangentOS);
                    float3 bitangentOS = cross(normalOS, tangentOS) * input.tangentOS.w;
                    // UV3 为切线空间平滑法线，骨骼变形后的原 TBN 负责恢复方向。
                    normalOS = SafeNormalize(tangentOS * input.smoothNormalTS.x
                        + bitangentOS * input.smoothNormalTS.y + normalOS * input.smoothNormalTS.z);
                }
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
                float3 normalVS = mul((float3x3)UNITY_MATRIX_V, normalWS);
                float2 projected = mul((float2x2)UNITY_MATRIX_P, normalVS.xy) * _ScaledScreenParams.xy;
                float projectedLength = length(projected);
                projected = projectedLength > 0.0001 ? projected / projectedLength : float2(0,0);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // 在齐次裁剪坐标中挤出，保持透视距离及分辨率变化时的像素宽度。
                output.positionCS.xy += projected * (_OutlineWidth * 2 / _ScaledScreenParams.xy) * output.positionCS.w;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }
            half4 MetalOutlineFragment(OutlineVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(min(_SmoothNormalOutline - 0.5, min(_OverrideOutline - 0.5, _OutlineEnabled - 0.5)));
                #if defined(_ALPHATEST_ON)
                    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif
                return half4(_OutlineColor.rgb * _OutlineBrightness, 1);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "Spotlight.BrushedMetal.Editor.BrushedMetalShaderGUI"
}

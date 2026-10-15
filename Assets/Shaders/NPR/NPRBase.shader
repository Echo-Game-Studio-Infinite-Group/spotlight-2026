// Derived from Unity URP14.0.11 Lit sources. Copyright Unity Technologies ApS.
// Unity Companion License: https://www.unity3d.com/legal/licenses/Unity_Companion_License
Shader "OurFunction/NPR/Deferred Toon"
{
    Properties
    {
        [Header(Character Shading)]
        _CharacterLightRamp("Character Lighting Ramp R (Linear)", 2D) = "white" {}
        _CharacterFillColor("Character Soft Fill Color", Color) = (1,1,1,1)
        _CharacterFillStrength("Character Soft Fill Strength", Range(0,1)) = 0
        _CharacterFillDirectionVS("Character Fill View Space Direction", Vector) = (0.3,0.4,1,0)
        _CharacterMinLight("Character Minimum Readability", Range(0,0.5)) = 0
        [Enum(Environment,0,Skin,1,FaceSDF,2,HairKajiyaKay,3,CharacterCloth,4)] _CharacterModel("Character Lighting Model", Float) = 0
        [Toggle] _SmoothNormalOutline("Smooth Normal Character Outline (UV3)", Float) = 0
        _FaceSDFMap("Face SDF R (Linear)", 2D) = "gray" {}
        _FaceSDFSoftness("Face SDF Softness", Range(0.001,0.2)) = 0.035
        _FaceSDFStrength("Face SDF Blend", Range(0,1)) = 1
        _BrowMask("Face Through Hair Mask RG (Linear)", 2D) = "black" {}
        _BangsOpacity("Bangs Opacity (Face Overlap)", Range(0,1)) = 0
        _FaceBangsShadowMap("Fixed Bangs Shadow Mask R (Linear)", 2D) = "black" {}
        _FaceBangsShadowColor("Fixed Bangs Shadow Tint", Color) = (0.65,0.5,0.48,1)
        _FaceBangsShadowStrength("Fixed Bangs Shadow Strength", Range(0,1)) = 0
        _BrowFrontFadeStart("Face Through Hair Front Facing Fade Start", Range(-0.1,0.8)) = 0.05
        _BrowFrontFadeEnd("Face Through Hair Front Facing Fade End", Range(0.1,1)) = 0.4
        _FaceForwardOS("Head Forward Object Space", Vector) = (0,0,1,0)
        _FaceRightOS("Head Right Object Space", Vector) = (1,0,0,0)
        _FaceCenterOS("Head Center Object Space", Vector) = (0,0,0,0)
        _HairDirectionRotation("Hair Tangent Rotation Degrees", Range(-180,180)) = 90
        [Toggle] _HairFlowMapEnabled("Use Hair Direction Map", Float) = 0
        _HairFlowMap("Hair Flow RG (Linear Tangent Space)", 2D) = "gray" {}
        _HairShift1("Hair Primary Shift", Range(-1,1)) = 0.1
        _HairShift2("Hair Secondary Shift", Range(-1,1)) = -0.18
        _HairPower1("Hair Primary Exponent", Range(1,256)) = 64
        _HairPower2("Hair Secondary Exponent", Range(1,256)) = 18
        [HDR] _HairSpecColor1("Hair Primary Color", Color) = (1,0.85,0.65,1)
        [HDR] _HairSpecColor2("Hair Secondary Color", Color) = (0.6,0.75,1,1)
        _HairSpecStrength1("Hair Primary Strength", Range(0,2)) = 0.6
        _HairSpecStrength2("Hair Secondary Strength", Range(0,2)) = 0.2
        _HairSpecBandStrength("Hair Highlight Band Strength", Range(0,1)) = 0
        _HairSpecBandThreshold("Hair Highlight Band Threshold", Range(0.01,0.99)) = 0.45
        _HairSpecBandSoftness("Hair Highlight Band Softness", Range(0.005,0.3)) = 0.08
        _SSSStrength("Skin Diffusion Strength", Range(0,1)) = 0.35
        _SSSRadius("Skin Diffusion Radius", Range(0.01,2)) = 0.6
        _SSSColor("Skin Scattering Profile RGB", Color) = (1,0.36,0.2,1)
        [Toggle] _ThicknessMapEnabled("Use Thickness Map", Float) = 0
        _ThicknessMap("Thickness R (Linear, White Thick)", 2D) = "white" {}
        _Thickness("Skin Thickness", Range(0,1)) = 0.3
        _TransmissionStrength("Skin Back Transmission", Range(0,1)) = 0.15
        [Header(Toon Lighting)]
        _ShadowColor("Shadow Tint", Color) = (0.38,0.46,0.64,1)
        _ToonThreshold("Light Band Threshold", Range(0,1)) = 0.65
        _ToonSoftness("Band Softness", Range(0.001,0.3)) = 0.035
        _ToonSpecular("Toon Highlight Strength", Range(0,2)) = 0.1
        _ToonSpecPower("Highlight Power", Range(4,128)) = 48
        [Enum(Lines,0,Halftone,1)] _ShadowPattern("Shadow Pattern", Float) = 0
        _Hatching("Shadow Pattern Strength", Range(0,1)) = 0
        _HatchScale("Hatch Spacing Pixels", Range(3,24)) = 7
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
        [MainColor] _BaseColor("Color", Color) = (0.26,0.46,0.68,1)

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
            #pragma fragment NPRPreviewFragment
            #pragma shader_feature_local_fragment _CHARACTER_LIGHT_RAMP
            #include "NPRShadowPattern.hlsl"
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _NORMALMAP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_instancing
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #include "NPRInput.hlsl"
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
            #include "NPRCharacterLighting.hlsl"
            half4 NPRPreviewFragment(Varyings input):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                SurfaceData surface; InitializeStandardLitSurfaceData(input.uv,surface);
                InputData data; InitializeInputData(input,surface.normalTS,data);
                if (_CharacterModel > 0.5)
                {
                    float4 shadowCoord = data.shadowCoord;
                    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                        shadowCoord = ComputeScreenPos(TransformWorldToHClip(data.positionWS));
                    #endif
                    float3 character = NPRCharacterDirect(surface, input.uv, data.positionWS, data.normalWS,
                        input.tangentWS, data.viewDirectionWS, shadowCoord, false);
                    BRDFData brdf; InitializeBRDFData(surface, brdf);
                    character += GlobalIllumination(brdf, data.bakedGI, surface.occlusion, data.positionWS, data.normalWS, data.viewDirectionWS) + surface.emission;
                    return half4(character, surface.alpha);
                }
                Light light=GetMainLight(data.shadowCoord);
                float amount=saturate(dot(data.normalWS,light.direction)*.5+.5)*light.shadowAttenuation;
                float band=smoothstep(_ToonThreshold-_ToonSoftness,_ToonThreshold+max(_ToonSoftness,.001),amount);
                float3 color=surface.albedo*(1-surface.metallic)*lerp(_ShadowColor.rgb,half3(1,1,1),band)*light.color;
                float highlight=pow(saturate(dot(data.normalWS,SafeNormalize(light.direction+data.viewDirectionWS))),_ToonSpecPower);
                color+=lerp(1.0.xxx,surface.albedo,surface.metallic)*light.color*smoothstep(.45,.55,highlight)*_ToonSpecular*band;
                color*=NPRShadowPatternAttenuation(input.positionCS.xy,band,_Hatching,_HatchScale,_ShadowPattern);
                color+=surface.albedo*data.bakedGI*surface.occlusion+surface.emission;
                return half4(color,surface.alpha);
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
            #include "NPRInput.hlsl"
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
            #include "NPRInput.hlsl"
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
            #include "NPRInput.hlsl"
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
            #include "NPRInput.hlsl"
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
            #include "NPRInput.hlsl"
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
            #include "NPRInput.hlsl"
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
            #pragma fragment NPRStyleFragment
            #pragma shader_feature_local_fragment _CHARACTER_LIGHT_RAMP
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
            #include "NPRInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl"
            #include "NPRCharacterLighting.hlsl"
            struct NPRStyleOutput { half4 shadow : SV_Target0; half4 lighting : SV_Target1; half4 detail : SV_Target2; half4 outlineColor : SV_Target3; };
            NPRStyleOutput NPRStyleFragment(Varyings input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                SurfaceData surface; InitializeStandardLitSurfaceData(input.uv, surface);
                NPRStyleOutput output;
                output.shadow = half4(_ShadowColor.rgb, 1);
                output.lighting = half4(_ToonThreshold, _ToonSoftness, _ToonSpecular, _ToonSpecPower);
                output.detail = half4(_Hatching, _HatchScale, _OverrideOutline > 0.5 ? _OutlineWidth * _OutlineEnabled : -1, 1 + _ShadowPattern + (_OverrideOutlineColor > .5 ? 2 : 0) + (_OutlineGeometryEdges > .5 ? 4 : 0));
                output.outlineColor = half4(_OutlineColor.rgb * _OutlineBrightness, 1);
                if (_CharacterModel > 0.5)
                {
                    InputData data; InitializeInputData(input, surface.normalTS, data);
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                    #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                        shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
                    #endif
                    output.shadow = half4(NPRCharacterDirect(surface, input.uv, input.positionWS, data.normalWS,
                        input.tangentWS, data.viewDirectionWS, shadowCoord, true), 2);
                }
                // 衣物也可使用平滑挤出，不要求切换皮肤/头发光照模型。
                if (_SmoothNormalOutline > 0.5) output.detail.z = 0;
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
            #pragma vertex NPRCharacterOutlineVertex
            #pragma fragment NPRCharacterOutlineFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local _ALPHATEST_ON
            #include "NPRInput.hlsl"
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
            OutlineVaryings NPRCharacterOutlineVertex(OutlineAttributes input)
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
            half4 NPRCharacterOutlineFragment(OutlineVaryings input) : SV_Target
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
        Pass
        {
            Name "NPRHairBrowStencil"
            Tags { "LightMode"="NPRHairBrowStencil" }
            Cull [_Cull] ZWrite Off ZTest Equal ColorMask 0
            // URP14 低四位由用户拥有；仅使用 bit1，保留高位材质及光体积模板。
            Stencil { Ref 2 ReadMask 2 WriteMask 2 Comp Always Pass Replace Fail Keep ZFail Keep }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NPRHairBrowStencilVertex
            #pragma fragment NPRHairBrowStencilFragment
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma multi_compile_instancing
            #include "NPRInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            Varyings NPRHairBrowStencilVertex(Attributes input)
            {
                Varyings output = DepthOnlyVertex(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                return output;
            }
            half4 NPRHairBrowStencilFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(0.5 - abs(_CharacterModel - 3));
                #if defined(_ALPHATEST_ON)
                    Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                #endif
                return 0;
            }
            ENDHLSL
        }
        Pass
        {
            Name "NPRFaceSurface"
            Tags { "LightMode"="NPRFaceSurface" }
            Cull Back ZWrite Off ZTest LEqual
            Blend One Zero, Zero One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LitPassVertex
            #pragma fragment NPRFaceSurfaceFragment
            #pragma shader_feature_local_fragment _CHARACTER_LIGHT_RAMP
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_instancing
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #include "NPRInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            #include "NPRFaceRadiance.hlsl"
            ENDHLSL
        }
        Pass
        {
            Name "NPRBrowThroughHair"
            Tags { "LightMode"="NPRBrowThroughHair" }
            Cull Back ZWrite Off ZTest Greater
            Blend SrcAlpha OneMinusSrcAlpha, Zero One
            Stencil { Ref 2 ReadMask 2 WriteMask 0 Comp Equal Pass Keep Fail Keep ZFail Keep }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex LitPassVertex
            #pragma fragment NPRBrowThroughHairFragment
            #pragma shader_feature_local_fragment _CHARACTER_LIGHT_RAMP
            #pragma shader_feature_local_fragment _ROUGHNESSMAP
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_instancing
            #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
            #define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
            #include "NPRInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            #include "NPRFaceRadiance.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "Spotlight.NPR.Editor.NPRShaderGUI"
}

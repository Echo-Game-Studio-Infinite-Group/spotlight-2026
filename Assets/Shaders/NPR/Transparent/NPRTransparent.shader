Shader "OurFunction/NPR/Transparent Toon"
{
 Properties
 {
  [Header(Base Color And Opacity)]
  [MainTexture] _BaseMap("Base Map (A: Opacity)",2D)="white"{}
  [MainColor] _BaseColor("Color / Opacity",Color)=(0.26,0.46,0.68,0.5)
  [Header(Toon Lighting)]
  _ShadowColor("Shadow Tint",Color)=(0.38,0.46,0.64,1)
  _ToonThreshold("Light Band Threshold",Range(0,1))=0.65
  _ToonSoftness("Band Softness",Range(0.001,0.3))=0.035
  _ToonSpecular("Toon Highlight Strength",Range(0,2))=0.1
  _ToonSpecPower("Highlight Power",Range(4,128))=48
  [Header(PBR Inputs)]
  _Metallic("Metallic",Range(0,1))=0
  _Smoothness("Smoothness",Range(0,1))=0.75
  _MetallicGlossMap("Metallic R / Smoothness A",2D)="white"{}
  _RoughnessMap("Roughness R (Linear, overrides Smoothness)",2D)="white"{}
  [Normal] _BumpMap("Normal Map",2D)="bump"{}
  _BumpScale("Normal Strength",Range(0,2))=1
  _OcclusionMap("Occlusion G",2D)="white"{}
  _OcclusionStrength("Occlusion Strength",Range(0,1))=1
  [HideInInspector] _OutlineGeometryEdges("Outline Geometric Creases", Float)=0
  [Header(Emission)]
  [Toggle] _EmissionEnabled("Emission Enabled", Float) = 0
        [Toggle] _EmissionUseMap("Use Emission Map", Float) = 0
        _EmissionIntensity("Emission Intensity", Range(0,16)) = 1
        [HDR] _EmissionColor("Emission Color",Color)=(0,0,0,1)
  _EmissionMap("Emission Map",2D)="white"{}
  [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull",Float)=2
  [HideInInspector] _Surface("Surface",Float)=1
  [HideInInspector] _Cutoff("Cutoff",Float)=0
  [HideInInspector] _SpecColor("Specular",Color)=(.2,.2,.2,1)
 }
 SubShader
 {
  Tags{"RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True"}
  Pass
  {
   Name "TransparentToon"
   Tags{"LightMode"="UniversalForwardOnly"}
   Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
   Cull [_Cull] ZWrite Off ZTest LEqual
   HLSLPROGRAM
   #pragma shader_feature_local_fragment _ROUGHNESSMAP
   #pragma target 3.5
   #pragma vertex LitPassVertex
   #pragma fragment TransparentFragment
   #pragma shader_feature_local _NORMALMAP
   #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
   #pragma shader_feature_local_fragment _OCCLUSIONMAP
   #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
   #pragma multi_compile_fragment _ _SHADOWS_SOFT
   #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
   #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
   #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
   #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
   #pragma multi_compile _ LIGHTMAP_ON
   #pragma multi_compile _ DIRLIGHTMAP_COMBINED
   #pragma multi_compile_fog
   #pragma multi_compile_instancing
   #define _SURFACE_TYPE_TRANSPARENT 1
   #pragma shader_feature_local_fragment _EMISSION
   #define REQUIRES_WORLD_SPACE_POS_INTERPOLATOR
   #include "../NPRInput.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
   half4 TransparentFragment(Varyings input):SV_Target
   {
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    SurfaceData surface;InitializeStandardLitSurfaceData(input.uv,surface);
    InputData data;InitializeInputData(input,surface.normalTS,data);
    Light light=GetMainLight(data.shadowCoord);
    half amount=saturate(dot(data.normalWS,light.direction)*.5+.5)*light.shadowAttenuation;
    half band=smoothstep(_ToonThreshold-_ToonSoftness,_ToonThreshold+max(_ToonSoftness,.001),amount);
    half3 direct=surface.albedo*(1-surface.metallic)*lerp(_ShadowColor.rgb,1.0.xxx,band)*light.color*light.distanceAttenuation;
    half highlight=pow(saturate(dot(data.normalWS,SafeNormalize(light.direction+data.viewDirectionWS))),_ToonSpecPower);
    direct+=lerp(1.0.xxx,surface.albedo,surface.metallic)*light.color*smoothstep(.45,.55,highlight)*_ToonSpecular*band*light.distanceAttenuation;
    #if defined(_ADDITIONAL_LIGHTS)
    uint lightCount=GetAdditionalLightsCount();
    for(uint index=0;index<lightCount;index++)
    {
     Light localLight=GetAdditionalLight(index,data.positionWS,half4(1,1,1,1));
     half localBand=smoothstep(_ToonThreshold-_ToonSoftness,_ToonThreshold+max(_ToonSoftness,.001),saturate(dot(data.normalWS,localLight.direction)*.5+.5));
     half3 localColor=surface.albedo*(1-surface.metallic)*lerp(_ShadowColor.rgb,1.0.xxx,localBand);
     half spec=pow(saturate(dot(data.normalWS,SafeNormalize(localLight.direction+data.viewDirectionWS))),_ToonSpecPower);
     localColor+=lerp(1.0.xxx,surface.albedo,surface.metallic)*smoothstep(.45,.55,spec)*_ToonSpecular*localBand;
     direct+=localColor*localLight.color*localLight.distanceAttenuation*localLight.shadowAttenuation;
    }
    #endif
    BRDFData brdf;InitializeBRDFData(surface,brdf);
    // 仅材质AO参与环境光；透明不能复用不透明屏幕深度上的GTAO。
    half3 indirect=GlobalIllumination(brdf,data.bakedGI,surface.occlusion,data.positionWS,data.normalWS,data.viewDirectionWS);
    half3 color=MixFog(direct+indirect+surface.emission,data.fogCoord);
    return half4(color,surface.alpha);
   }
   ENDHLSL
  }
 }
 CustomEditor "NPRTransparentShaderGUI"
}

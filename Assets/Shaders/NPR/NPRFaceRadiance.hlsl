#ifndef SPOTLIGHT_NPR_FACE_RADIANCE_INCLUDED
#define SPOTLIGHT_NPR_FACE_RADIANCE_INCLUDED
#include "NPRCharacterLighting.hlsl"

// 显式传入当前角色的探针，不能依赖手动 DrawRenderer 遗留的逐对象 SH 数据。
float4 _NPRFaceSH[7];

float3 NPRFaceRadiance(Varyings input, SurfaceData surface, InputData data)
{
#if !defined(LIGHTMAP_ON)
    data.bakedGI = max(0.0h.xxx, SampleSH9(_NPRFaceSH, data.normalWS));
#endif
    float4 shadowCoord = TransformWorldToShadowCoord(data.positionWS);
#if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
    shadowCoord = ComputeScreenPos(TransformWorldToHClip(data.positionWS));
#endif
    Light mainLight = GetMainLight(shadowCoord, data.positionWS, data.shadowMask);
    MixRealtimeAndBakedGI(mainLight, data.normalWS, data.bakedGI, data.shadowMask);
    // 可见脸与透发脸共用同一辐射函数；头发屏幕 AO 不得投到后面的面部。
    float3 color = NPRCharacterDirect(surface, input.uv, data.positionWS, data.normalWS,
        input.tangentWS, data.viewDirectionWS, shadowCoord, true);
    BRDFData brdf; InitializeBRDFData(surface, brdf);
    color += GlobalIllumination(brdf, data.bakedGI, surface.occlusion,
        data.positionWS, data.normalWS, data.viewDirectionWS) + surface.emission;
    return color;
}

half4 NPRFaceSurfaceFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    clip(0.5 - abs(_CharacterModel - 2));
    SurfaceData surface; InitializeStandardLitSurfaceData(input.uv, surface);
    InputData data; InitializeInputData(input, surface.normalTS, data);
    return half4(NPRFaceRadiance(input, surface, data), 1);
}

half4 NPRBrowThroughHairFragment(Varyings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    clip(0.5 - abs(_CharacterModel - 2));
    float2 overlayMask = SAMPLE_TEXTURE2D_LOD(_BrowMask, sampler_LinearClamp, input.uv, 0).rg;
    float regionOpacity = max(overlayMask.r, overlayMask.g) * (1 - _BangsOpacity);
    clip(regionOpacity - 0.001);
    SurfaceData surface; InitializeStandardLitSurfaceData(input.uv, surface);
    InputData data; InitializeInputData(input, surface.normalTS, data);
    float3 forwardWS = SafeNormalize(TransformObjectToWorldDir(_FaceForwardOS.xyz));
    float front = dot(forwardWS, data.viewDirectionWS);
    float frontFade = smoothstep(_BrowFrontFadeStart, max(_BrowFrontFadeStart + 0.01, _BrowFrontFadeEnd), front);
    float opacity = regionOpacity * frontFade;
    clip(opacity - 0.001);
    return half4(NPRFaceRadiance(input, surface, data), opacity);
}
#endif

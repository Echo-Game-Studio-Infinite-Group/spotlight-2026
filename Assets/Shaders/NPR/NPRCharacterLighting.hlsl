#ifndef SPOTLIGHT_NPR_CHARACTER_LIGHTING_INCLUDED
#define SPOTLIGHT_NPR_CHARACTER_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

float _NPRCustomMainLightPresent, _NPRCustomAdditionalLightCount;

float NPRCharacterRamp(float illumination)
{
    // 由美术曲线连续塑形受光；不量化层数，不把 PBR 原色贴图压成固定明度。
    #if defined(_CHARACTER_LIGHT_RAMP)
        return SAMPLE_TEXTURE2D_LOD(_CharacterLightRamp, sampler_LinearClamp, float2(illumination, 0.5), 0).r;
    #else
        return illumination;
    #endif
}

float NPRCharacterFaceBand(float2 uv, float3 positionWS, float3 lightDirection)
{
    float3 forwardWS = SafeNormalize(TransformObjectToWorldDir(_FaceForwardOS.xyz));
    float3 rightWS = TransformObjectToWorldDir(_FaceRightOS.xyz);
    rightWS = SafeNormalize(rightWS - forwardWS * dot(rightWS, forwardWS));
    float forwardLight = dot(lightDirection, forwardWS);
    float sideLight = dot(lightDirection, rightWS);
    // 方位角不受太阳高度影响；顶光退化时采用正面光，避免零长度抖动。
    float angle = atan2(abs(sideLight), forwardLight + 0.000001) / PI;
    if (abs(sideLight) + abs(forwardLight) < 0.0001) angle = 0;
    float3 centerWS = TransformObjectToWorld(_FaceCenterOS.xyz);
    float side = dot(positionWS - centerWS, rightWS);
    float sdf = SAMPLE_TEXTURE2D_LOD(_FaceSDFMap, sampler_LinearClamp, uv, 0).r;
    float threshold = side * sideLight >= 0 ? sdf : 1 - sdf;
    return smoothstep(angle - max(_FaceSDFSoftness, 0.001), angle + max(_FaceSDFSoftness, 0.001), threshold);
}

float NPRKajiyaKayLobe(float3 strand, float3 normal, float3 halfway, float shift, float power)
{
    float3 shiftedStrand = SafeNormalize(strand + normal * shift);
    float tangentHalf = clamp(dot(shiftedStrand, halfway), -1, 1);
    // sin(theta)^n 是沿发丝切线的圆柱高光；方向门控消除反向半向量的亮边。
    float lobe = pow(max(0.00001, 1 - tangentHalf * tangentHalf), max(power, 1) * 0.5)
        * smoothstep(-1, 0, tangentHalf);
    // KK 保留发丝方向，但将强度转换为可控光带，避免只得到连续金属细高光。
    float band = smoothstep(_HairSpecBandThreshold - max(_HairSpecBandSoftness, 0.005),
        _HairSpecBandThreshold + max(_HairSpecBandSoftness, 0.005), lobe);
    return lerp(lobe, band, _HairSpecBandStrength);
}

float3 NPRSkinDiffusion(float ndotl)
{
    // 三尺度 RGB 包裹卷积近似表面扩散，不读取屏幕邻域，不冒充屏幕空间 SSS。
    // 每个 RGB 通道的扩散距离独立，红通道较宽，蓝通道较窄。
    float3 radius = max(_SSSColor.rgb, 0.015) * max(_SSSRadius, 0.01);
    float3 a = saturate((ndotl + radius * 0.2) / (1 + radius * 0.2));
    float3 b = saturate((ndotl + radius * 0.55) / (1 + radius * 0.55));
    float3 c = saturate((ndotl + radius) / (1 + radius));
    return a * 0.25 + b * 0.45 + c * 0.3;
}

float3 NPRCharacterOneLight(SurfaceData surface, float2 uv, float3 positionWS, float3 normal,
    float4 tangentWS, float3 view, Light light, bool mainLight, out float3 diffuseRadiance)
{
    float nl = dot(normal, light.direction);
    float halfLambert = saturate(nl * 0.5 + 0.5);
    float visibility = light.shadowAttenuation;
    #if defined(_RECEIVE_SHADOWS_OFF)
        visibility = 1;
    #endif
    float band = NPRCharacterRamp(halfLambert);
    if (_CharacterModel > 1.5 && _CharacterModel < 2.5)
        band = lerp(band, NPRCharacterFaceBand(uv, positionWS, light.direction), _FaceSDFStrength);
    // 脸部 SDF 只控制自阴影的设计形状，实际遮挡仍服从实时阴影贴图。
    float effectVisibility = mainLight ? visibility : 1;
    float litBand = band * effectVisibility;
    float3 diffuse = surface.albedo * (1 - surface.metallic) * lerp(_ShadowColor.rgb, 1.0.xxx, litBand);
    float3 baseDiffuse = diffuse;
    float3 halfway = SafeNormalize(light.direction + view);
    float3 specular = 0;
    if (_CharacterModel > 2.5 && _CharacterModel < 3.5)
    {
        float3 tangent = tangentWS.xyz - normal * dot(tangentWS.xyz, normal);
        if (dot(tangent, tangent) < 0.0001)
            tangent = cross(abs(normal.y) < 0.9 ? float3(0,1,0) : float3(1,0,0), normal);
        tangent = SafeNormalize(tangent);
        float3 bitangent = cross(normal, tangent) * tangentWS.w;
        float angle = radians(_HairDirectionRotation);
        float2 direction = float2(cos(angle), sin(angle));
        if (_HairFlowMapEnabled > 0.5)
        {
            float2 flow = SAMPLE_TEXTURE2D_LOD(_HairFlowMap, sampler_LinearRepeat, uv, 0).rg * 2 - 1;
            if (dot(flow, flow) > 0.0001) direction = normalize(flow);
        }
        float3 strand = SafeNormalize(tangent * direction.x + bitangent * direction.y);
        specular = _HairSpecColor1.rgb * _HairSpecStrength1 * NPRKajiyaKayLobe(strand, normal, halfway, _HairShift1, _HairPower1)
            + _HairSpecColor2.rgb * _HairSpecStrength2 * NPRKajiyaKayLobe(strand, normal, halfway, _HairShift2, _HairPower2);
        specular *= smoothstep(-0.1, 0.25, nl) * effectVisibility;
    }
    else if (_CharacterModel < 2.5)
    {
        float highlight = pow(saturate(dot(normal, halfway)), max(_ToonSpecPower, 4));
        // 双瓣皮肤反射保留油脂窄高光及较宽的柔和底瓣。
        specular = _ToonSpecular * (highlight * 0.75 + pow(saturate(dot(normal, halfway)), max(_ToonSpecPower * 0.25, 2)) * 0.25)
            * lerp(0.04.xxx, surface.albedo, surface.metallic) * band * effectVisibility;
        float thickness = _Thickness;
        if (_ThicknessMapEnabled > 0.5)
            thickness *= SAMPLE_TEXTURE2D_LOD(_ThicknessMap, sampler_LinearRepeat, uv, 0).r;
        float3 diffusion = max(0, NPRSkinDiffusion(nl) - saturate(nl));
        // 只扩散终止线附近的直接光，不染黑烘焙 GI，也不冲淡面部设计阴影。
        float terminator = 1 - smoothstep(0.15, 0.85, abs(nl));
        diffuse += surface.albedo * diffusion * _SSSStrength * terminator * effectVisibility;
        float backScatter = pow(saturate(dot(-light.direction, view)), 4) * saturate(-nl);
        diffuse += surface.albedo * _SSSColor.rgb * exp(-max(thickness, 0.001) * 4)
            * backScatter * _TransmissionStrength * effectVisibility;
    }
    else
    {
        float highlight = pow(saturate(dot(normal, halfway)), max(_ToonSpecPower, 4));
        specular = smoothstep(0.45, 0.55, highlight) * _ToonSpecular
            * lerp(1.0.xxx, surface.albedo, surface.metallic) * band * effectVisibility;
    }
    #if defined(_SPECULARHIGHLIGHTS_OFF)
        specular = 0;
    #endif
    float3 attenuation = light.color * light.distanceAttenuation * (mainLight ? 1 : visibility);
    diffuseRadiance = baseDiffuse * attenuation;
    return (diffuse + specular) * attenuation;
}

float3 NPRCharacterDirect(SurfaceData surface, float2 uv, float3 positionWS, float3 normal, float4 tangentWS,
    float3 view, float4 shadowCoord, bool deferredCapture)
{
    Light main = GetMainLight(shadowCoord);
    float mainPresent = deferredCapture ? _NPRCustomMainLightPresent : 1;
    float3 diffuseRadiance;
    float3 result = NPRCharacterOneLight(surface, uv, positionWS, normal, tangentWS, view, main, true, diffuseRadiance) * mainPresent;
    diffuseRadiance *= mainPresent;
    // Deferred 捕获复用 URP 可见灯数组，与既有全屏 NPR 遍历一致，避免错误依赖逐对象索引。
    uint count = deferredCapture ? (uint)_NPRCustomAdditionalLightCount : GetAdditionalLightsCount();
    [loop] for (uint i = 0; i < count; i++)
    {
        Light local;
        if (deferredCapture)
        {
            local = GetAdditionalPerObjectLight(i, positionWS);
            local.shadowAttenuation = AdditionalLightRealtimeShadow(i, positionWS, local.direction);
        }
        else local = GetAdditionalLight(i, positionWS, half4(1,1,1,1));
        if (local.distanceAttenuation > 0)
        {
            float3 localDiffuse;
            result += NPRCharacterOneLight(surface, uv, positionWS, normal, tangentWS, view, local, false, localDiffuse);
            diffuseRadiance += localDiffuse;
        }
    }
    if (_CharacterModel > 0.5)
    {
        // 独立低频补光只提供角色可读性，不改主灯方位、面部 SDF 或真实投影阴影的形状。
        // 视空间方向随当前渲染相机转换，反射相机也不会偷用主相机的矩阵。
        float3 fillDirection = SafeNormalize(mul((float3x3)UNITY_MATRIX_I_V, _CharacterFillDirectionVS.xyz));
        float lowFrequency = saturate(dot(normal, fillDirection) * 0.4 + 0.6);
        float3 diffuseTint = surface.albedo * (1 - surface.metallic);
        float3 metalReflectionTint = surface.albedo * surface.metallic * 0.25;
        float3 readability = diffuseTint + metalReflectionTint;
        float currentLuminance = dot(diffuseRadiance, float3(0.2126, 0.7152, 0.0722));
        float referenceLuminance = max(dot(surface.albedo, float3(0.2126, 0.7152, 0.0722)), 0.01);
        float shadowWeight = 1 - smoothstep(0.15, 0.8, currentLuminance / referenceLuminance);
        result += readability * _CharacterFillColor.rgb * _CharacterFillStrength * lowFrequency * shadowWeight;
        // 按材质固有色提供底线，黑色衣物和眉眼仍保持黑，不用全屏提亮或自发光兜底。
        result = max(result, readability * _CharacterFillColor.rgb * _CharacterMinLight);
    }
    return result;
}
#endif

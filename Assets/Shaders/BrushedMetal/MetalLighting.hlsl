#ifndef SPOTLIGHT_BRUSHED_METAL_LIGHTING_INCLUDED
#define SPOTLIGHT_BRUSHED_METAL_LIGHTING_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

float _NPRCustomMainLightPresent, _NPRCustomAdditionalLightCount;

float3 MetalArtReflectance(float3 albedo)
{
    // 仅角色金属的直接反射外观可艺术覆盖，保留源 GBuffer / GI / SSR 材质数据。
    return lerp(albedo, _MetalReflectanceColor.rgb, _MetalReflectanceOverride);
}

float MetalSmithLambda(float3 directionWS, float3 normal, float3 tangent, float3 bitangent, float2 alpha)
{
    float normalProjection = max(abs(dot(normal, directionWS)), 0.001);
    float2 slope = float2(dot(tangent, directionWS), dot(bitangent, directionWS)) * alpha / normalProjection;
    return (sqrt(1 + dot(slope, slope)) - 1) * 0.5;
}

float3 BrushedMetalSpecular(SurfaceData surface, float2 uv, float2 uvDx, float2 uvDy,
    float3 normal, float4 tangentWS, float3 view, float3 lightDirection)
{
    float nl = saturate(dot(normal, lightDirection));
    float nv = max(saturate(dot(normal, view)), 0.001);
    if (nl <= 0) return 0;
    float3 tangent = tangentWS.xyz - normal * dot(tangentWS.xyz, normal);
    if (dot(tangent, tangent) < 0.0001)
        tangent = cross(abs(normal.y) < 0.9 ? float3(0,1,0) : float3(1,0,0), normal);
    tangent = SafeNormalize(tangent);
    float3 bitangent = cross(normal, tangent) * tangentWS.w;
    float2 flow = float2(1,0);
    float grain = 0.5;
    if (_MetalFlowMapEnabled > 0.5)
    {
        float3 data = SAMPLE_TEXTURE2D_GRAD(_MetalFlowMap, sampler_LinearRepeat, uv, uvDx, uvDy).rgb;
        float2 direction = data.rg * 2 - 1;
        if (dot(direction, direction) > 0.0001) flow = normalize(direction);
        // 拉丝响应与方向分开采样；显式梯度在灯循环前计算，远处细纹可正确使用 mip。
        float scale = max(_MetalBrushScale, 0.1);
        grain = SAMPLE_TEXTURE2D_GRAD(_MetalFlowMap, sampler_LinearRepeat, uv * scale, uvDx * scale, uvDy * scale).b;
    }
    float3 along = SafeNormalize(tangent * flow.x + bitangent * flow.y);
    float3 across = SafeNormalize(cross(normal, along));
    float roughness = _MetalRoughness;
    #if defined(_ROUGHNESSMAP)
        roughness = 1 - surface.smoothness;
    #endif
    roughness = clamp(roughness * _MetalRoughnessScale * lerp(1, lerp(0.7, 1.3, grain), _MetalBrushStrength), 0.06, 1);
    float alpha = max(roughness * roughness, 0.003);
    float aspect = sqrt(max(0.1, 1 - 0.9 * abs(_MetalAnisotropy)));
    float2 axes = float2(alpha / aspect, alpha * aspect);
    if (_MetalAnisotropy < 0) axes = axes.yx;
    float3 halfway = SafeNormalize(lightDirection + view);
    float nh = saturate(dot(normal, halfway));
    float2 tangentHalf = float2(dot(along, halfway), dot(across, halfway)) / axes;
    float distributionTerm = dot(tangentHalf, tangentHalf) + nh * nh;
    float distribution = rcp(max(PI * axes.x * axes.y * distributionTerm * distributionTerm, 0.000001));
    float geometry = rcp(1 + MetalSmithLambda(view, normal, along, across, axes)
        + MetalSmithLambda(lightDirection, normal, along, across, axes));
    float energy = min(64, distribution * geometry / (4 * nv));
    float response = energy / (1 + energy);
    float band = smoothstep(_MetalSpecBandThreshold - max(_MetalSpecBandSoftness, 0.005),
        _MetalSpecBandThreshold + max(_MetalSpecBandSoftness, 0.005), response);
    float3 f0 = lerp(0.04.xxx, MetalArtReflectance(surface.albedo), surface.metallic);
    float fresnel = pow(1 - saturate(dot(view, halfway)), 5);
    float3 fresnelColor = f0 + (1 - f0) * fresnel;
    // GGX 的双轴分布及金属 Fresnel 独立于发丝 KK；美术仅对高光能量做柔和色带化。
    return fresnelColor * lerp(energy, band * 1.5, _MetalSpecBandStrength);
}

float3 MetalOneLight(SurfaceData surface, float2 uv, float2 uvDx, float2 uvDy,
    float3 positionWS, float3 normal, float4 tangentWS, float3 view, Light light, out float3 diffuseRadiance)
{
    float visibility = light.shadowAttenuation;
    #if defined(_RECEIVE_SHADOWS_OFF)
        visibility = 1;
    #endif
    float3 diffuse = surface.albedo * (1 - surface.metallic) * INV_PI * saturate(dot(normal, light.direction));
    float3 specular = BrushedMetalSpecular(surface, uv, uvDx, uvDy, normal, tangentWS, view, light.direction);
    #if defined(_SPECULARHIGHLIGHTS_OFF)
        specular = 0;
    #endif
    float3 attenuation = light.color * light.distanceAttenuation * visibility;
    diffuseRadiance = diffuse * attenuation;
    return (diffuse + specular) * attenuation;
}

float3 MetalDirectLighting(SurfaceData surface, float2 uv, float3 positionWS, float3 normal,
    float4 tangentWS, float3 view, float4 shadowCoord, bool deferredCapture)
{
    // 在逐灯循环前求取梯度，拉丝细纹在远处能稳定使用 mip。
    float2 uvDx = ddx(uv), uvDy = ddy(uv);
    Light main = GetMainLight(shadowCoord);
    float mainPresent = deferredCapture ? _NPRCustomMainLightPresent : 1;
    float3 diffuseRadiance;
    float3 result = MetalOneLight(surface, uv, uvDx, uvDy, positionWS, normal, tangentWS, view, main, diffuseRadiance) * mainPresent;
    diffuseRadiance *= mainPresent;
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
            result += MetalOneLight(surface, uv, uvDx, uvDy, positionWS, normal, tangentWS, view, local, localDiffuse);
            diffuseRadiance += localDiffuse;
        }
    }
    // 武器有独立的可读性参数，不读取角色材质分支或角色补光组件。
    float3 fillDirection = SafeNormalize(mul((float3x3)UNITY_MATRIX_I_V, _MetalFillDirectionVS.xyz));
    float lowFrequency = saturate(dot(normal, fillDirection) * 0.4 + 0.6);
    float3 readability = surface.albedo * (1 - surface.metallic)
        + MetalArtReflectance(surface.albedo) * surface.metallic * 0.25;
    float currentLuminance = dot(diffuseRadiance, float3(0.2126, 0.7152, 0.0722));
    float referenceLuminance = max(dot(surface.albedo, float3(0.2126, 0.7152, 0.0722)), 0.01);
    float shadowWeight = 1 - smoothstep(0.15, 0.8, currentLuminance / referenceLuminance);
    result += readability * _MetalFillColor.rgb * _MetalFillStrength * lowFrequency * shadowWeight;
    result = max(result, readability * _MetalFillColor.rgb * _MetalMinLight);
    return result;
}
#endif

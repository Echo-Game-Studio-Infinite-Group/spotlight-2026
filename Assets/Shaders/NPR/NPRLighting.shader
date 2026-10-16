Shader "Hidden/OurFunction/NPRLighting"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "DeferredToonLighting"
            ZTest Always ZWrite Off Cull Off
            Blend SrcAlpha OneMinusSrcAlpha, Zero One
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "NPRShadowPattern.hlsl"
            TEXTURE2D_X(_NPRShadow); TEXTURE2D_X(_NPRLighting); TEXTURE2D_X(_NPRDetail);
            TEXTURE2D_X(_NPRMaterialOutlineColor); TEXTURE2D_X(_NPRSource); TEXTURE2D_X(_GBuffer0); TEXTURE2D_X(_GBuffer1); TEXTURE2D_X(_GBuffer2);
            float4x4 _NPRInverseViewProjection;
            float4 _NPRTexelSize;
            float4 _NPROutlineColor;
            float _NPROutlineWidth, _NPRMaxOutlineWidth, _NPRHasMainLight, _NPRIndirectAOApplied;
            float _NPRAdditionalLightCount;
            struct Varyings { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            Varyings Vert(uint id:SV_VertexID) { Varyings o; o.positionCS=GetFullScreenTriangleVertexPosition(id); o.uv=GetFullScreenTriangleTexCoord(id); return o; }
            float EyeDepth(float raw)
            {
                float z=raw;
                #if UNITY_REVERSED_Z
                z=1-raw;
                #endif
                return lerp(LinearEyeDepth(raw,_ZBufferParams),lerp(_ProjectionParams.y,_ProjectionParams.z,z),unity_OrthoParams.w);
            }
            half4 Frag(Varyings i):SV_Target
            {
                float2 uv=i.uv;
                half4 style=SAMPLE_TEXTURE2D_X_LOD(_NPRShadow,sampler_PointClamp,uv,0);
                float sceneRaw=SampleSceneDepth(uv);
                if(style.a<0.5)
                {
                    // 完整像素邻域避免稀疏方向采样漏掉细斜边，最近距离提供亚像素覆盖。
                    float bestDistance=1e6, bestWidth=0, customColor=0;
                    float2 bestUV=uv;
                    float sceneEye=EyeDepth(sceneRaw);
                    int limit=min(6,(int)ceil(_NPRMaxOutlineWidth));
                    [loop] for(int y=-limit;y<=limit;y++)
                    [loop] for(int x=-limit;x<=limit;x++)
                    {
                        if(x==0&&y==0)continue;
                        float distancePixels=length(float2(x,y));
                        if(distancePixels>_NPRMaxOutlineWidth+.5||distancePixels>=bestDistance)continue;
                        float2 sampleUV=uv+float2(x,y)*_NPRTexelSize.xy;
                        if(any(sampleUV<0)||any(sampleUV>1))continue;
                        half4 candidate=SAMPLE_TEXTURE2D_X_LOD(_NPRDetail,sampler_PointClamp,sampleUV,0);
                        float width=min(candidate.z<0?_NPROutlineWidth:candidate.z,_NPRMaxOutlineWidth);
                        if(candidate.a<=.5||width<=0||distancePixels>width+.5)continue;
                        float eye=EyeDepth(SampleSceneDepth(sampleUV));
                        if(eye>sceneEye+max(.005,sceneEye*.0001))continue;
                        bestDistance=distancePixels;bestWidth=width;bestUV=sampleUV;customColor=fmod(floor((candidate.w-1)/2),2);
                    }
                    float coverage=saturate(bestWidth+.5-bestDistance);
                    float3 edgeColor=_NPROutlineColor.rgb;
                    if(coverage>0&&customColor>.5)edgeColor=SAMPLE_TEXTURE2D_X_LOD(_NPRMaterialOutlineColor,sampler_PointClamp,bestUV,0).rgb;
                    return half4(edgeColor,coverage*_NPROutlineColor.a);
                }
                half4 settings=SAMPLE_TEXTURE2D_X_LOD(_NPRLighting,sampler_PointClamp,uv,0);
                half4 detail=SAMPLE_TEXTURE2D_X_LOD(_NPRDetail,sampler_PointClamp,uv,0);
                half4 gb0=SAMPLE_TEXTURE2D_X_LOD(_GBuffer0,sampler_PointClamp,uv,0);
                half4 gb1=SAMPLE_TEXTURE2D_X_LOD(_GBuffer1,sampler_PointClamp,uv,0);
                half4 gb2=SAMPLE_TEXTURE2D_X_LOD(_GBuffer2,sampler_PointClamp,uv,0);
                float clipDepth=sceneRaw;
                #if !UNITY_REVERSED_Z
                clipDepth=lerp(UNITY_NEAR_CLIP_VALUE,1,clipDepth);
                #endif
                float3 world=ComputeWorldSpacePosition(uv,clipDepth,_NPRInverseViewProjection);
                float3 normal=normalize(UnpackNormal(gb2.xyz));
                float4 shadowCoord=TransformWorldToShadowCoord(world);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                shadowCoord=float4(uv,0,1);
                #endif
                Light light=GetMainLight(shadowCoord);
                uint flags=UnpackMaterialFlags(gb0.a);
                if((flags & kMaterialFlagReceiveShadowsOff)!=0)light.shadowAttenuation=1;
                AmbientOcclusionFactor ao=GetScreenSpaceAmbientOcclusion(uv);
                float halfLambert=saturate(dot(normal,light.direction)*.5+.5);
                float illumination=halfLambert*light.shadowAttenuation;
                float band=smoothstep(settings.x-settings.y,settings.x+max(settings.y,.001),illumination);
                float3 tint=lerp(style.rgb,1.0.xxx,band);
                // URP14 GBuffer0 是原始 albedo；金属反射率编码在 GBuffer1.r。
                float metallic=saturate(MetallicFromReflectivity(gb1.r));
                float3 diffuseColor=gb0.rgb*(1-metallic);
                float3 specularTint=lerp(1.0.xxx,gb0.rgb,metallic);
                float3 diffuse=diffuseColor*tint*light.color*light.distanceAttenuation*ao.directAmbientOcclusion;
                float3 view=GetWorldSpaceNormalizeViewDir(world);
                float3 halfway=SafeNormalize(light.direction+view);
                float spec=pow(saturate(dot(normal,halfway)),max(settings.w,4));
                spec=smoothstep(.45,.55,spec)*settings.z*band;
                if((flags & kMaterialFlagSpecularHighlightsOff)!=0)spec=0;
                float3 direct=diffuse+specularTint*light.color*spec*light.shadowAttenuation*ao.directAmbientOcclusion;
                direct*=NPRShadowPatternAttenuation(i.positionCS.xy,band,detail.x,detail.y,fmod(detail.w-1,2));
                // GBuffer3 已含环境反射/烘焙GI/自发光，只合成一次主光；不重复标准Deferred BRDF。
                float3 indirect=SAMPLE_TEXTURE2D_X_LOD(_NPRSource,sampler_PointClamp,uv,0).rgb;
                float aoRatio=ao.indirectAmbientOcclusion<gb1.a?ao.indirectAmbientOcclusion/max(gb1.a,.0001):1;
                // 无方向光时，URP 的 SSAOOnly 已对间接光应用同一 AO 比值。
                aoRatio=lerp(aoRatio,1,_NPRIndirectAOApplied);
                float3 localDirect=0;
                float2 localGrid=float2(i.positionCS.x+i.positionCS.y,i.positionCS.y-i.positionCS.x)*.70710678/max(detail.y,3);
                float localPatternAA=max(max(fwidth(localGrid.x),fwidth(localGrid.y))*.5,.0001);
                // Fullscreen deferred lighting has no per-object unity_LightIndices list.
                [loop] for(int lightIndex=0;lightIndex<(int)_NPRAdditionalLightCount;lightIndex++)
                {
                    Light localLight=GetAdditionalPerObjectLight(lightIndex,world);
                    if(localLight.distanceAttenuation<=0)continue;
                    half visibility=(flags & kMaterialFlagReceiveShadowsOff)!=0?1:AdditionalLightRealtimeShadow(lightIndex,world,localLight.direction);
                    float localBand=smoothstep(settings.x-settings.y,settings.x+max(settings.y,.001),saturate(dot(normal,localLight.direction)*.5+.5));
                    float3 localColor=diffuseColor*lerp(style.rgb,1.0.xxx,localBand);
                    if((flags & kMaterialFlagSpecularHighlightsOff)==0)
                    {float highlight=pow(saturate(dot(normal,SafeNormalize(localLight.direction+view))),max(settings.w,4));localColor+=specularTint*smoothstep(.45,.55,highlight)*settings.z*localBand;}
                    localColor*=NPRShadowPatternAttenuationAA(i.positionCS.xy,localBand,detail.x,detail.y,fmod(detail.w-1,2),localPatternAA);
                    // Occluded punctual lights contribute no direct light, including shadow tint.
                    localDirect+=localColor*localLight.color*localLight.distanceAttenuation*visibility*ao.directAmbientOcclusion;
                }
                float3 finalColor=indirect*aoRatio+direct*_NPRHasMainLight+localDirect;
                float ownWidth=min(detail.z<0?_NPROutlineWidth:detail.z,_NPRMaxOutlineWidth);
                if(detail.w>=5 && ownWidth>0)
                {
                    float centerEye=EyeDepth(sceneRaw), coverage=0;
                    int radius=min(6,(int)ceil(ownWidth));
                    [loop] for(int ey=-radius;ey<=radius;ey++)
                    [loop] for(int ex=-radius;ex<=radius;ex++)
                    {
                        float pixelDistance=length(float2(ex,ey));
                        if(pixelDistance<.5||pixelDistance>ownWidth+.5)continue;
                        float2 nearUV=uv+float2(ex,ey)*_NPRTexelSize.xy;
                        if(any(nearUV<0)||any(nearUV>1))continue;
                        half4 nearStyle=SAMPLE_TEXTURE2D_X_LOD(_NPRShadow,sampler_PointClamp,nearUV,0);
                        if(nearStyle.a<.5)continue;
                        float nearRaw=SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture,sampler_PointClamp,nearUV,0).r;
                        float nearEye=EyeDepth(nearRaw);
                        if(nearEye<centerEye-max(.015,centerEye*.001))continue;
                        float3 nearNormal=normalize(UnpackNormal(SAMPLE_TEXTURE2D_X_LOD(_GBuffer2,sampler_PointClamp,nearUV,0).xyz));
                        float edge=max(1-smoothstep(.55,.75,dot(normal,nearNormal)),smoothstep(max(.04,centerEye*.015),max(.06,centerEye*.025),nearEye-centerEye));
                        coverage=max(coverage,edge*saturate(ownWidth+.5-pixelDistance));
                    }
                    float3 ownColor=_NPROutlineColor.rgb;
                    if(fmod(floor((detail.w-1)/2),2)>.5)ownColor=SAMPLE_TEXTURE2D_X_LOD(_NPRMaterialOutlineColor,sampler_PointClamp,uv,0).rgb;
                    finalColor=lerp(finalColor,ownColor,coverage);
                }
                return half4(finalColor,1);
            }
            ENDHLSL
        }
    }
}
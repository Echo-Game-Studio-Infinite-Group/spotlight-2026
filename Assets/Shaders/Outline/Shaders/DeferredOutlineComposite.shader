Shader "Hidden/DeferredOutline/Composite"
{
 SubShader { Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Overlay" }
 Pass { Name "DeferredOutlineComposite" ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha ColorMask RGB
 HLSLPROGRAM
 #pragma target 3.5
 #pragma vertex Vert
 #pragma fragment Frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
 TEXTURE2D(_DO_CurrentTex); SAMPLER(sampler_DO_CurrentTex);
 float4 _DO_MaskTexelSize,_DO_Color0,_DO_Color1,_DO_Color2,_DO_Color3,_DO_Widths;
 float4 _DO_FlowAnchor,_DO_FlowOffset,_DO_FlameColor,_DO_WorldUpPixels,_DO_ScreenBounds;
 float _DO_LayerCount,_DO_Distortion,_DO_FlowScale,_DO_FlameIntensity,_DO_RhythmPhase;
 float _DO_GlowIntensity,_DO_GlowRadius,_DO_EqualSpacingPixels,_DO_ExtensionPixels;
 struct Varyings {float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
 Varyings Vert(uint id:SV_VertexID){Varyings o;o.positionCS=GetFullScreenTriangleVertexPosition(id);o.uv=GetFullScreenTriangleTexCoord(id);return o;}
 float Noise(float2 p){float2 c=floor(p),f=frac(p);f=f*f*f*(f*(f*6-15)+10);float4 h=float4(dot(c,float2(127.1,311.7)),dot(c+float2(1,0),float2(127.1,311.7)),dot(c+float2(0,1),float2(127.1,311.7)),dot(c+1,float2(127.1,311.7)));h=frac(sin(h)*43758.5453);return lerp(lerp(h.x,h.y,f.x),lerp(h.z,h.w,f.x),f.y);}
 // Interpolate coverage only after validating each sample's own depth. Never interpolate depth across a silhouette.
 float Coverage(float2 uv,float eye){float2 pos=uv*_DO_MaskTexelSize.zw-.5;float2 cell=floor(pos),f=frac(pos);float result=0;
 [unroll]for(int y=0;y<2;y++)[unroll]for(int x=0;x<2;x++){float2 p=cell+float2(x,y)+.5;if(any(p<.5)||any(p>_DO_MaskTexelSize.zw-.5))continue;float4 m=SAMPLE_TEXTURE2D_LOD(_DO_CurrentTex,sampler_DO_CurrentTex,p*_DO_MaskTexelSize.xy,0);float valid=step(.001,m.a)*step(.000001,m.b)*step(m.b,eye+max(.005,eye*.0001));result+=m.r*valid*(x==0?1-f.x:f.x)*(y==0?1-f.y:f.y);}return result;}
 // Union the root and three intermediate samples before edge detection.
 // A single connected-looking local extrusion replaces a detached shifted silhouette.
 float AnchoredCoverageFromRoot(float2 uv,float2 warp,float eye,float coverage){
 [unroll]for(int stepIndex=1;stepIndex<=3;stepIndex++)coverage=max(coverage,Coverage(uv+warp*(stepIndex/3.0),eye));return coverage;}
 float AnchoredCoverage(float2 uv,float2 warp,float eye){return AnchoredCoverageFromRoot(uv,warp,eye,Coverage(uv,eye));}
 void NeighbourCoverage(float2 uv,float2 warp,float radius,float eye,out float presence,out float core){presence=0;core=1;[unroll]for(int n=0;n<12;n++){float angle=n*0.5235987756;float coverage=AnchoredCoverage(uv+float2(cos(angle),sin(angle))*radius*_DO_MaskTexelSize.xy,warp,eye);presence=max(presence,coverage);core=min(core,coverage);}}
 half4 Frag(Varyings input):SV_Target{
 float2 uv=input.uv;
 float lower=step(_DO_ScreenBounds.y,uv.y)*step(uv.y,_DO_ScreenBounds.w);
 float flipped=step(1-_DO_ScreenBounds.w,uv.y)*step(uv.y,1-_DO_ScreenBounds.y);
 if(uv.x<_DO_ScreenBounds.x||uv.x>_DO_ScreenBounds.z||max(lower,flipped)<.5)return half4(0,0,0,0);
 float raw=SAMPLE_TEXTURE2D_X_LOD(_CameraDepthTexture,sampler_CameraDepthTexture,uv,0).r;float linear01=raw;
 #if UNITY_REVERSED_Z
 linear01=1-raw;
 #endif
 float eye=lerp(LinearEyeDepth(raw,_ZBufferParams),lerp(_ProjectionParams.y,_ProjectionParams.z,linear01),unity_OrthoParams.w);
 float original=Coverage(uv,eye);if(original>.999)return half4(0,0,0,0);
 float2 offset=_DO_FlowOffset.xy;
 float2 up=_DO_WorldUpPixels.xy/max(length(_DO_WorldUpPixels.xy),.0001);
 float2 anchor=_DO_FlowAnchor.xy;
 // CPU投影为左下原点；全屏RT的UV在D3D为左上原点，只在此转换一次。
 #if UNITY_UV_STARTS_AT_TOP
 up.y=-up.y;offset.y=-offset.y;anchor.y=1-anchor.y;
 #endif
 // Budget follows projected character height and speed, never full-screen UV.
 // The original mask remains a root; only this local noise warp changes direction.
 float2 domain=(uv-anchor)*float2(max(.1,_DO_FlowScale),1)-offset/max(_DO_FlowAnchor.z,.001);
 // Independently authored procedural wave texture: no third-party texture/code is embedded.
 float wave=(Noise(domain*12)+.5*Noise(domain*24+float2(7.3,19.1)))/1.5;
 // 反向取样才会沿flow方向外伸；旧扰动正负仅保留强度兼容，不再反转运动方向。
 float2 warp=-up*_DO_MaskTexelSize.xy*(wave*_DO_ExtensionPixels);
 float presence,core;NeighbourCoverage(uv,warp,max(.1,_DO_Widths.x),eye,presence,core);
 // White fill belongs to the anchored mask itself, so distant thin energy
 // remains visible even when narrower than the black edge sampling radius.
 // Reuse the unwarped center read; the original character interior stays clipped.
 float coreWeight=smoothstep(.2,.95,AnchoredCoverageFromRoot(uv,warp,eye,original));
 float4 color=lerp(_DO_Color0,_DO_LayerCount>1.5?_DO_Color1:_DO_Color0,coreWeight);
 float alpha=saturate(presence*color.a*(1-original));
 return half4(color.rgb,alpha);
 }
 ENDHLSL
 }}
}

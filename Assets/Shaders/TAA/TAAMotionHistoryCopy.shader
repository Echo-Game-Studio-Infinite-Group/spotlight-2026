Shader "Hidden/OurFunction/TAA/MotionHistoryCopy"
{
 SubShader { Tags {"RenderPipeline"="UniversalPipeline"}
 Pass { ZWrite Off ZTest Always Cull Off
 HLSLPROGRAM
 #pragma target 3.5
 #pragma vertex Vert
 #pragma fragment Frag
 #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
 TEXTURE2D_X(_MotionVectorTexture); SAMPLER(sampler_MotionVectorTexture);
 struct Varyings {float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
 Varyings Vert(uint id:SV_VertexID){Varyings o;o.positionCS=GetFullScreenTriangleVertexPosition(id);o.uv=GetFullScreenTriangleTexCoord(id);return o;}
 float4 Frag(Varyings i):SV_Target{return float4(SAMPLE_TEXTURE2D_X_LOD(_MotionVectorTexture,sampler_MotionVectorTexture,i.uv,0).rg,0,0);}
 ENDHLSL
 }}
}

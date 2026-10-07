Shader "Hidden/DeferredOutline/DistanceField"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_DO_CurrentTex); SAMPLER(sampler_DO_CurrentTex);
        TEXTURE2D(_DO_DistanceInput); SAMPLER(sampler_DO_DistanceInput);
        float4 _DO_MaskTexelSize;
        float _DO_DistanceStep;
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            output.uv = GetFullScreenTriangleTexCoord(vertexID);
            return output;
        }
        float4 Seed(Varyings input) : SV_Target
        {
            float4 mask = SAMPLE_TEXTURE2D_LOD(_DO_CurrentTex, sampler_DO_CurrentTex, input.uv, 0);
            if (mask.a > 0.5 && mask.r > 0.001 && mask.b > 0.0)
                return float4(input.uv * _DO_MaskTexelSize.zw, mask.b, mask.r);
            return float4(-1.0, -1.0, 0.0, 0.0);
        }
        float4 Flood(Varyings input) : SV_Target
        {
            float2 pixel = input.uv * _DO_MaskTexelSize.zw;
            float4 best = float4(-1.0, -1.0, 0.0, 0.0);
            float bestDistance = 1e20;
            [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float2 uv = input.uv + float2(x, y) * _DO_DistanceStep * _DO_MaskTexelSize.xy;
                if (any(uv < 0.0) || any(uv >= 1.0)) continue;
                float4 candidate = SAMPLE_TEXTURE2D_LOD(_DO_DistanceInput, sampler_DO_DistanceInput, uv, 0);
                float2 delta = pixel - candidate.xy;
                float distanceSq = dot(delta, delta);
                if (candidate.a > 0.0 && distanceSq < bestDistance)
                { best = candidate; bestDistance = distanceSq; }
            }
            return best;
        }
        ENDHLSL
        Pass
        {
            Name "SeedCurrentSilhouette"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Seed
            ENDHLSL
        }
        Pass
        {
            Name "JumpFloodCurrentSilhouette"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Flood
            ENDHLSL
        }
    }
}
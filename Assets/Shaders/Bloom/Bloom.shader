Shader "Hidden/OurFunction/Bloom"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D_X(_Bloom_SourceTex);
        SAMPLER(sampler_Bloom_SourceTex);
        TEXTURE2D_X(_BloomTex);
        SAMPLER(sampler_BloomTex);

        float4 _Bloom_SourceTex_TexelSize;
        float4 _BloomTex_TexelSize;
        float4 _BloomParams;
        float4 _BloomTint;

        #define BLOOM_THRESHOLD _BloomParams.x
        #define BLOOM_KNEE _BloomParams.y
        #define BLOOM_INTENSITY _BloomParams.z
        #define BLOOM_SCATTER _BloomParams.w

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings output;
            output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            output.uv = GetFullScreenTriangleTexCoord(vertexID);
            return output;
        }

        half3 SafeHDR(half3 color)
        {
            return min(color, half3(65504.0, 65504.0, 65504.0));
        }

        half Luminance(half3 color)
        {
            return dot(color, half3(0.2126, 0.7152, 0.0722));
        }

        half3 ApplyThreshold(half3 color)
        {
            half brightness = Luminance(color);
            half softness = saturate((brightness - BLOOM_THRESHOLD + BLOOM_KNEE) / max(2.0 * BLOOM_KNEE, 1e-4));
            half soft = softness * softness * BLOOM_KNEE;
            half contribution = max(brightness - BLOOM_THRESHOLD, soft);
            return color * saturate(contribution / max(brightness, 1e-4));
        }

        half3 SampleBox(TEXTURE2D_X_PARAM(tex, samplerTex), float2 uv, float2 texelSize, float radius)
        {
            float2 d = texelSize * radius;
            half3 s = 0.0;
            s += SAMPLE_TEXTURE2D_X(tex, samplerTex, uv + float2(-d.x, -d.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(tex, samplerTex, uv + float2( d.x, -d.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(tex, samplerTex, uv + float2(-d.x,  d.y)).rgb;
            s += SAMPLE_TEXTURE2D_X(tex, samplerTex, uv + float2( d.x,  d.y)).rgb;
            return s * 0.25;
        }

        half4 FragPrefilter(Varyings input) : SV_Target
        {
            half3 color = SampleBox(TEXTURE2D_X_ARGS(_Bloom_SourceTex, sampler_Bloom_SourceTex), input.uv, _Bloom_SourceTex_TexelSize.xy, 1.0);
            return half4(ApplyThreshold(SafeHDR(color)), 1.0);
        }

        half4 FragDownsample(Varyings input) : SV_Target
        {
            half3 color = SampleBox(TEXTURE2D_X_ARGS(_Bloom_SourceTex, sampler_Bloom_SourceTex), input.uv, _Bloom_SourceTex_TexelSize.xy, 1.0);
            return half4(SafeHDR(color), 1.0);
        }

        half4 FragUpsample(Varyings input) : SV_Target
        {
            half3 low = SampleBox(TEXTURE2D_X_ARGS(_Bloom_SourceTex, sampler_Bloom_SourceTex), input.uv, _Bloom_SourceTex_TexelSize.xy, 1.0);
            half3 high = SAMPLE_TEXTURE2D_X(_BloomTex, sampler_BloomTex, input.uv).rgb;
            return half4(lerp(high, high + low, BLOOM_SCATTER), 1.0);
        }

        half4 FragComposite(Varyings input) : SV_Target
        {
            half4 source = SAMPLE_TEXTURE2D_X(_Bloom_SourceTex, sampler_Bloom_SourceTex, input.uv);
            half3 bloom = SAMPLE_TEXTURE2D_X(_BloomTex, sampler_BloomTex, input.uv).rgb * _BloomTint.rgb;
            return half4(source.rgb + bloom * BLOOM_INTENSITY, source.a);
        }
        ENDHLSL

        Pass
        {
            Name "Prefilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass
        {
            Name "Downsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDownsample
            ENDHLSL
        }

        Pass
        {
            Name "Upsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUpsample
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}

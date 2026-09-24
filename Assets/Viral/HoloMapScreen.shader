// Hologram map on screen (HoloMap.cs): the UI image's material, adding the map's
// render texture over the game so it glows like light rather than covering it,
// plus a bloom from the texture's blurred mips so lines and dots bleed softly.
Shader "Hidden/HoloMapScreen"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            Name "Screen"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float _HoloGlow;

            // Soft blur: a ring of taps around the pixel on a small mip. Reading a small mip
            // straight would magnify its pixels into visible squares; the ring hides them.
            half3 Blur(float2 uv, float lod, float radius)
            {
                float2 spread = _MainTex_TexelSize.xy * exp2(lod) * radius;
                half3 sum = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, lod).rgb;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float a = k * (PI / 4.0) + lod; // rotate each level's ring so they don't line up
                    sum += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + float2(cos(a), sin(a)) * spread, lod).rgb;
                }
                return sum / 9.0;
            }

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float4 color      : COLOR;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv, 0).rgb;
                half3 bloom = Blur(i.uv, 1.5, 1.5) * 0.5 + Blur(i.uv, 3.0, 1.5) * 0.5;
                c += bloom * _HoloGlow;
                return half4(c * i.color.rgb * i.color.a, 0.0);
            }
            ENDHLSL
        }
    }
}

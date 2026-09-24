// The cell sky drawn from SkyboxCache's cubemaps: two baked moments, crossfaded, so the slow
// drift keeps moving between bakes. SkyboxCache swaps this in for Custom/StylizedCellSkybox.
Shader "Hidden/StylizedCellSkyCached"
{
    Properties
    {
        [NoScaleOffset] _SkyCacheA ("Earlier", Cube) = "black" {}
        [NoScaleOffset] _SkyCacheB ("Later", Cube) = "black" {}
        _SkyCacheBlend ("Blend", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Background"
            "RenderType"="Background"
            "PreviewType"="Skybox"
            "RenderPipeline"="UniversalPipeline"
        }
        Cull Off
        ZWrite Off
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURECUBE(_SkyCacheA);
            TEXTURECUBE(_SkyCacheB);
            SAMPLER(sampler_linear_clamp);
            float _SkyCacheBlend;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldDir : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);
                o.worldDir = TransformObjectToWorld(v.vertex.xyz) - _WorldSpaceCameraPos;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                half3 a = SAMPLE_TEXTURECUBE_LOD(_SkyCacheA, sampler_linear_clamp, i.worldDir, 0).rgb;
                half3 b = SAMPLE_TEXTURECUBE_LOD(_SkyCacheB, sampler_linear_clamp, i.worldDir, 0).rgb;
                return float4(lerp(a, b, _SkyCacheBlend), 1.0);
            }
            ENDHLSL
        }
    }
}

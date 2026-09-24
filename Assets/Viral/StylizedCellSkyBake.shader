// Renders the cell sky (StylizedCellSky.hlsl) into one face of a cubemap, for SkyboxCache.
// A full-face triangle; SkyboxCache scissors it to the strip being refreshed this frame.
Shader "Hidden/StylizedCellSkyBake"
{
    Properties
    {
        _ColorA ("Deep Color", Color) = (0.05, 0.12, 0.22, 1)
        _ColorB ("Mid Color", Color) = (0.18, 0.42, 0.62, 1)
        _ColorC ("Bright Color", Color) = (0.78, 0.92, 1.00, 1)
        _MembraneColor ("Membrane Color", Color) = (0.85, 0.97, 1.00, 1)
        _HorizonColor ("Horizon Tint", Color) = (0.28, 0.45, 0.62, 1)
        _Scale ("Primary Scale", Float) = 3.0
        _DetailScale ("Detail Scale", Float) = 8.0
        _WarpScale ("Warp Scale", Float) = 2.0
        _WarpStrength ("Warp Strength", Float) = 0.22
        _DepthOffset ("Fake Depth Offset", Float) = 0.35
        _Contrast ("Contrast", Float) = 1.2
        _MembraneWidth ("Membrane Width", Range(0.001, 0.4)) = 0.08
        _MembraneSharpness ("Membrane Sharpness", Range(0.1, 8)) = 2.4
        _CellDepth ("Cell Depth Contribution", Range(0, 2)) = 0.75
        _RidgeStrength ("Ridge Strength", Range(0, 2)) = 0.55
        _LightDir ("Light Direction", Vector) = (0.4, 0.7, 0.3, 0)
        _LightStrength ("Light Strength", Range(0, 2)) = 0.55
        _BackLightStrength ("Back Light Strength", Range(0, 2)) = 0.25
        _HorizonStrength ("Horizon Strength", Range(0, 2)) = 0.45
        _TimeScale ("Time Scale", Float) = 0.12
        _DriftDirection ("Drift Direction", Vector) = (0.13, 0.07, -0.05, 0)
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "StylizedCellSky.hlsl"

            float _SkyBakeFace;  // CubemapFace: +X -X +Y -Y +Z -Z
            float _SkyBakeSize;  // face size in texels
            float _SkyBakeTime;  // the moment baked, same clock as _Time.y

            float4 vert(uint id : SV_VertexID) : SV_POSITION
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, 0.0, 1.0);
            }

            // Direction through a texel of a cube face (s right, t down, both 0..1), the
            // standard cubemap face layout.
            float3 FaceDirection(int face, float2 st)
            {
                float sc = st.x * 2.0 - 1.0, tc = st.y * 2.0 - 1.0;
                if (face == 0) return float3( 1.0, -tc, -sc);
                if (face == 1) return float3(-1.0, -tc,  sc);
                if (face == 2) return float3( sc,  1.0,  tc);
                if (face == 3) return float3( sc, -1.0, -tc);
                if (face == 4) return float3( sc, -tc,  1.0);
                return float3(-sc, -tc, -1.0);
            }

            float4 frag(float4 pos : SV_POSITION) : SV_Target
            {
                float2 st = pos.xy / _SkyBakeSize;
            #if !UNITY_UV_STARTS_AT_TOP
                st.y = 1.0 - st.y;
            #endif
                float3 dir = CustomSafeNormalize(FaceDirection((int)round(_SkyBakeFace), st));
                return float4(CellSky(dir, _SkyBakeTime * _TimeScale), 1.0);
            }
            ENDHLSL
        }
    }
}

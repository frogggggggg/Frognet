Shader "Custom/StylizedCellSkybox"
{
    Properties
    {
        [Header(Base_Colors)]
        _ColorA ("Deep Color", Color) = (0.05, 0.12, 0.22, 1)
        _ColorB ("Mid Color", Color) = (0.18, 0.42, 0.62, 1)
        _ColorC ("Bright Color", Color) = (0.78, 0.92, 1.00, 1)
        _MembraneColor ("Membrane Color", Color) = (0.85, 0.97, 1.00, 1)
        _HorizonColor ("Horizon Tint", Color) = (0.28, 0.45, 0.62, 1)

        [Header(Noise)]
        _Scale ("Primary Scale", Float) = 3.0
        _DetailScale ("Detail Scale", Float) = 8.0
        _WarpScale ("Warp Scale", Float) = 2.0
        _WarpStrength ("Warp Strength", Float) = 0.22
        _DepthOffset ("Fake Depth Offset", Float) = 0.35
        _Contrast ("Contrast", Float) = 1.2

        [Header(Cell_Membrane_Look)]
        _MembraneWidth ("Membrane Width", Range(0.001, 0.4)) = 0.08
        _MembraneSharpness ("Membrane Sharpness", Range(0.1, 8)) = 2.4
        _CellDepth ("Cell Depth Contribution", Range(0, 2)) = 0.75
        _RidgeStrength ("Ridge Strength", Range(0, 2)) = 0.55

        [Header(Lighting_Shading)]
        _LightDir ("Light Direction", Vector) = (0.4, 0.7, 0.3, 0)
        _LightStrength ("Light Strength", Range(0, 2)) = 0.55
        _BackLightStrength ("Back Light Strength", Range(0, 2)) = 0.25
        _HorizonStrength ("Horizon Strength", Range(0, 2)) = 0.45

        [Header(Animation)]
        _TimeScale ("Time Scale", Float) = 0.12
        _DriftDirection ("Drift Direction", Vector) = (0.13, 0.07, -0.05, 0)
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

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldDir : TEXCOORD0;
            };

            #include "StylizedCellSky.hlsl"

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = TransformObjectToHClip(v.vertex.xyz);

                float3 worldPos = TransformObjectToWorld(v.vertex.xyz);
                o.worldDir = worldPos - _WorldSpaceCameraPos;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // FIXED: Using CustomSafeNormalize instead of original call
                float3 dir = CustomSafeNormalize(i.worldDir);
                float t = _Time.y * _TimeScale;
                return float4(CellSky(dir, t), 1.0);
            }
            ENDHLSL
        }
    }
}

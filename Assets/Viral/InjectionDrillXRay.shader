// X-ray pass for InjectionDrill: drawn over everything (after the focus sweep's overlay), but
// only inside the sweep's circle (_InvertSweep, set globally by ScreenInvertTest while it
// sweeps), so the drill shows through the planet in the terminal view and nowhere else.
// Glowing rim, faint fill, and pulses running down the drill into the planet (uv.y = metres
// from the virus).
Shader "Custom/InjectionDrillXRay"
{
    Properties
    {
        _Color ("Colour", Color) = (0.62, 1.0, 0.45, 1)
        _Fill ("Fill", Range(0, 1)) = 0.2
        _Rim ("Rim", Range(0, 4)) = 1.6
        _PulseSpacing ("Pulse Spacing (m)", Float) = 1.5
        _PulseSpeed ("Pulse Speed (m/s)", Float) = 6
        _PulseStrength ("Pulse Strength", Range(0, 1)) = 0.7
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Overlay+10"
        }

        Pass
        {
            Name "XRay"
            Tags { "LightMode"="SRPDefaultUnlit" }

            ZTest Always
            ZWrite Off
            Cull Back
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Fill;
                float _Rim;
                float _PulseSpacing;
                float _PulseSpeed;
                float _PulseStrength;
            CBUFFER_END

            float4 _InvertSweep; // xyz world centre, w eased progress; w = 0 when not sweeping

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = input.uv;
                return o;
            }

            // Same circle as ScreenInvertSweep / InvertSweepCover in BloodCellCore.
            float SweepCover(float3 positionWS)
            {
                if (_InvertSweep.w <= 0.0) return 0.0;
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 scale = float2(aspect, 1.0);

                float4 c = TransformWorldToHClip(_InvertSweep.xyz);
                float2 projected = c.xy / max(abs(c.w), 1e-5);
                float2 centre = (c.w < 0.0 ? -projected : projected) * scale;

                float4 h = TransformWorldToHClip(positionWS);
                float2 here = h.xy / max(abs(h.w), 1e-5) * scale;

                float maxRadius = max(max(distance(centre, float2(-aspect, -1.0)), distance(centre, float2(aspect, -1.0))),
                                      max(distance(centre, float2(-aspect,  1.0)), distance(centre, float2(aspect,  1.0))));
                float radius = maxRadius * saturate(_InvertSweep.w);
                return 1.0 - smoothstep(radius - 0.01, radius, distance(here, centre));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float cover = SweepCover(i.positionWS);
                clip(cover - 0.001);

                float3 n = normalize(i.normalWS);
                float3 v = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float rim = pow(1.0 - saturate(abs(dot(n, v))), 2.0) * _Rim;

                float phase = frac(i.uv.y / max(_PulseSpacing, 1e-3) - _Time.y * _PulseSpeed / max(_PulseSpacing, 1e-3));
                float pulse = smoothstep(0.75, 0.95, phase) * (1.0 - smoothstep(0.95, 1.0, phase)) * _PulseStrength;

                float a = saturate(_Fill + rim + pulse) * cover * _Color.a;
                return half4(_Color.rgb, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

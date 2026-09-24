// The core inside a resource chunk (ResourceField.cs), shown in focus mode as the thing to click to
// extract it: a glowing ball in the substance's colour, drawn over everything (after the focus sweep,
// like InjectionDrillXRay) but only inside the sweep's circle. ResourceField only submits cores that
// are in reach and not hidden behind a cell (tested on the CPU), so ZTest Always is safe.
// Instances: _Cores[SV_InstanceID] = centre + radius, colour (rgb, a = hovered),
// state = extracted 0..1, extracting 0..1, seed, blocked (inventory full).
Shader "Custom/ResourceCore"
{
    Properties
    {
        _Fill ("Fill", Range(0, 1)) = 0.35
        _Rim ("Rim", Range(0, 4)) = 1.8
        _Pulse ("Pulse", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay+10" }

        Pass
        {
            Name "Core"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest Always
            ZWrite Off
            Cull Back
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Fill, _Rim, _Pulse;
            CBUFFER_END

            struct Core { float4 positionScale, color, state; };
            StructuredBuffer<Core> _Cores;
            float4 _InvertSweep; // xyz world centre, w eased progress; w = 0 when not sweeping

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                float4 state      : TEXCOORD3;
                float3 local      : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Core c = _Cores[v.instanceID];
                float t = _Time.y;
                // Hovered: a little bigger; extracting: throbbing.
                float grow = 1.0 + c.color.a * 0.3 + c.state.y * 0.12 * sin(t * 7.0 + c.state.z * 20.0);
                Varyings o;
                o.positionWS = c.positionScale.xyz + v.positionOS.xyz * c.positionScale.w * grow;
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = v.normalOS;
                o.color = c.color;
                o.state = c.state;
                o.local = v.positionOS.xyz;
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
                float facing = saturate(abs(dot(n, v)));
                float rim = pow(1.0 - facing, 2.0) * _Rim;
                float t = _Time.y;

                // Rings running out from the middle: slow while idle, fast while extracting.
                float speed = lerp(0.6, 2.2, i.state.y);
                float ring = frac(facing * 2.0 + t * speed + i.state.z);
                float pulse = smoothstep(0.7, 0.9, ring) * (1.0 - smoothstep(0.9, 1.0, ring)) * _Pulse;

                // A gauge: the fill is only as high as what's left, so the core empties from the top down.
                float gauge = step(i.local.y * 0.5 + 0.5, 1.0 - i.state.x);
                float3 col = i.color.rgb;
                if (i.state.w > 0.5) col = lerp(col, float3(1.0, 0.28, 0.34), 0.5 + 0.5 * sin(t * 10.0)); // full: flashes red
                float a = saturate(_Fill * (0.35 + 0.65 * gauge) + rim + pulse + i.color.a * 0.35) * cover;
                return half4(col * (1.0 + i.color.a * 0.5), a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

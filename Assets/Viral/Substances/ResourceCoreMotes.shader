// What's inside a resource blob's core (ResourceField, focus mode): lumps of its substance drifting round inside
// the glowing core, drawn with the same shapes as inside cells (SubstanceLook.hlsl), fewer as it's drained, stirring
// faster while it's extracted. Reads the core buffer (_Cores, ResourceField.CoreInstance); 24 quads per core, all
// placed in the vertex stage. Over the core (Overlay+11), ZTest Always like it, only inside the sweep.
Shader "Custom/ResourceCoreMotes"
{
    Properties
    {
        _Size ("Lump size (share of the core)", Range(0.05, 0.5)) = 0.24
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay+11" }

        Pass
        {
            Name "CoreMotes"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest Always
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "../FocusSweep.hlsl"
            #include "SubstanceLook.hlsl"

            #define LUMPS 24

            CBUFFER_START(UnityPerMaterial)
                float _Size;
            CBUFFER_END

            // positionScale: centre + radius; color: rgb, a hovered; state: extracted, extracting, seed, blocked;
            // look: x SubstanceLook, y how full 0..1
            struct Core { float4 positionScale, color, state, look; };
            StructuredBuffer<Core> _Cores;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 quad       : TEXCOORD1;
                float4 color      : TEXCOORD2; // rgb, a alpha
                float2 data       : TEXCOORD3; // look, seed
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            float Rand(uint a, uint b)
            {
                uint v = a * 747796405u + b * 2891336453u + 12345u;
                v = ((v >> ((v >> 28u) + 4u)) ^ v) * 277803737u;
                v = (v >> 22u) ^ v;
                return v * (1.0 / 4294967295.0);
            }

            Varyings vert(uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
            {
                Core c = _Cores[instanceID];
                uint i = vertexID / 6;
                float2 q = Corners[vertexID % 6];
                uint s = (uint)(c.state.z * 65535.0);
                float h0 = Rand(i, s), h1 = Rand(i + 31u, s), h2 = Rand(i + 67u, s), h3 = Rand(i + 97u, s);
                float t = _Time.y, R = c.positionScale.w;

                // Round a tilted orbit each, at its own depth in the core.
                float speed = lerp(0.25, 1.4, c.state.y) * lerp(0.6, 1.2, h3);
                float a = h0 * 6.2832 + t * speed;
                float tilt = (h1 - 0.5) * 3.0;
                float r = R * lerp(0.2, 0.72, sqrt(h2));
                float3 p = float3(cos(a), sin(a) * cos(tilt), sin(a) * sin(tilt)) * r;
                float alpha = saturate(c.look.y * LUMPS + 0.5 - i); // fewer as it drains

                Varyings o;
                o.quad = q;
                o.color = float4(c.color.rgb, alpha);
                o.data = float2(c.look.x, h3);
                if (alpha <= 0.002)
                {
                    o.positionWS = 0;
                    o.positionCS = float4(0.0, 0.0, 0.0, 1.0);
                    return o;
                }
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                o.positionWS = c.positionScale.xyz + p + (right * q.x + up * q.y) * R * _Size * lerp(0.8, 1.15, h1);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float cover = SweepCover(i.positionWS);
                clip(cover - 0.001);
                return SubstanceMote(i.data.x, i.quad, i.data.y, i.color.rgb, i.color.a * cover, _Time.y);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

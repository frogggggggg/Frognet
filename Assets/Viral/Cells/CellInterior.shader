// The bodies inside a cell in focus (CellInteriorView): its nucleus and organelles, one instanced draw of a ball
// mesh, each instance shaped in the vertex stage (stretched along its axis, bent into a bean, breathing, drifting
// by CellDrift so the streams feeding it follow). Drawn like the rest of focus mode's x-ray: over the sweep
// (Overlay+7), only inside its circle, a see-through schematic (tinted fill, bright membrane lines, inner
// pattern: the nucleus's chromatin, pores and nucleolus; a mitochondrion's cristae, lit up running as it works).
// Depth-tested: inside the sweep a cell keeps only its back faces in depth, so its insides show and the virus
// on top still hides them.
// Instances: _CellBodies[SV_InstanceID] (CellInteriorView.Body).
Shader "Custom/CellInterior"
{
    Properties
    {
        _Fill ("Fill", Range(0, 1)) = 0.26
        _Line ("Membrane", Range(0, 2)) = 1.1
        _Pattern ("Pattern", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay+7" }

        Pass
        {
            Name "Bodies"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest LEqual
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "CellInterior.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Fill, _Line, _Pattern;
            CBUFFER_END

            struct CellBody
            {
                float4 position; // xyz frame, w size (frame units)
                float4 color;    // rgb, a activity 0..1
                float4 info;     // x shape (0 nucleus, 1 mitochondrion, 2 vesicle, 3 ribosome), y seed, z frame, w elongation
                float4 axis;     // xyz long axis (frame), w bend
            };
            StructuredBuffer<CellBody> _CellBodies;

            struct Attributes
            {
                float4 positionOS : POSITION;
                uint instanceID   : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 local      : TEXCOORD2; // on the unit ball, before stretching
                float4 color      : TEXCOORD3;
                float3 info       : TEXCOORD4; // shape, seed, nucleus highlight
            };

            Varyings vert(Attributes v)
            {
                CellBody b = _CellBodies[v.instanceID];
                CellFrame f = _CellFrames[(uint)b.info.z];
                float t = _Time.y;
                float3 p = normalize(v.positionOS.xyz);
                float shape = b.info.x, seed = b.info.y, elong = max(b.info.w, 0.2), act = b.color.a;

                float3 L = normalize(b.axis.xyz);
                float3 up = abs(L.z) < 0.9 ? float3(0, 0, 1) : float3(1, 0, 0);
                float3 M = normalize(cross(up, L));
                float3 N = cross(L, M);

                float3 s = p;
                s.x *= elong;
                s.y += b.axis.w * (p.x * p.x - 0.5); // bean
                bool nucleus = shape < 0.5;
                float wobble = 1.0 + (nucleus ? 0.035 : 0.05) * sin(t * (nucleus ? 0.9 : 1.7) + dot(p, float3(3.1, 2.3, 1.7)) + seed * 40.0);
                float pulse = nucleus ? 1.0 + 0.1 * f.axisY.w : 1.0 + 0.05 * act * sin(t * 5.0 + seed * 30.0);
                float grow = CellAppear(f, b.position.xyz) * wobble * pulse;

                float3 framePos = b.position.xyz + CellDrift(seed, t) + (L * s.x + M * s.y + N * s.z) * b.position.w * grow;
                Varyings o;
                o.positionWS = FramePoint(f, framePos);
                o.positionCS = TransformWorldToHClip(o.positionWS);

                // The stretched ball's normal, carried through the frame's (non-uniform) axes.
                float3 nb = float3(p.x / elong, p.y, p.z);
                float3 nf = L * nb.x + M * nb.y + N * nb.z;
                o.normalWS = f.axisX.xyz * (nf.x / max(dot(f.axisX.xyz, f.axisX.xyz), 1e-6))
                           + f.axisY.xyz * (nf.y / max(dot(f.axisY.xyz, f.axisY.xyz), 1e-6))
                           + f.axisZ.xyz * (nf.z / max(dot(f.axisZ.xyz, f.axisZ.xyz), 1e-6));
                o.local = p;
                o.color = b.color;
                o.info = float3(shape, seed, f.axisY.w);
                return o;
            }

            // A line 'width' wide centred on 'at' in the rim coordinate (0 facing, 1 silhouette).
            float Band(float edge, float at, float width)
            {
                return 1.0 - smoothstep(width * 0.5, width * 0.5 + 0.03, abs(edge - at));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float cover = SweepCover(i.positionWS);
                clip(cover - 0.001);

                float3 n = normalize(i.normalWS);
                float3 view = GetWorldSpaceNormalizeViewDir(i.positionWS);
                float edge = 1.0 - saturate(abs(dot(n, view)));
                float t = _Time.y;
                float shape = i.info.x, seed = i.info.y, act = i.color.a;
                float3 col = i.color.rgb, l = i.local;

                float fill = _Fill, lines = 0.0, glow = 0.0;
                if (shape < 0.5)
                {
                    // Nucleus: double membrane with pores, chromatin threads, a nucleolus.
                    float angle = atan2(l.y, l.x + 1e-5);
                    float pore = smoothstep(0.35, 0.5, abs(frac(angle * 14.0 / 6.2832 + seed) - 0.5) * 2.0);
                    lines = max(Band(edge, 0.9, 0.12), Band(edge, 0.7, 0.06) * pore);
                    float thread = sin(l.x * 7.0 + sin(l.y * 5.0 + t * 0.4 + seed * 9.0) * 1.6 + l.z * 4.0);
                    lines = max(lines, (1.0 - smoothstep(0.08, 0.16, abs(thread))) * step(edge, 0.62) * _Pattern);
                    float nucleolus = 1.0 - smoothstep(0.26, 0.32, distance(l, float3(0.28, -0.2, 0.35)));
                    fill += nucleolus * 0.35;
                    glow = i.info.z * (0.4 + 0.2 * sin(t * 5.0));
                }
                else if (shape < 1.5)
                {
                    // Mitochondrion: outer and inner membrane, cristae folds across it; a wave runs along it
                    // while it works.
                    lines = max(Band(edge, 0.88, 0.14), Band(edge, 0.66, 0.06));
                    float c = frac(l.x * 3.2 + 0.18 * sin(l.y * 5.0 + seed * 7.0) + seed);
                    lines = max(lines, (1.0 - smoothstep(0.07, 0.12, abs(c - 0.5))) * step(edge, 0.58) * _Pattern);
                    glow = act * (0.25 + 0.75 * pow(0.5 + 0.5 * sin(l.x * 4.0 - t * 5.0 + seed * 20.0), 4.0));
                }
                else if (shape < 2.5)
                {
                    // Vesicle: a bubble with a few specks.
                    lines = Band(edge, 0.9, 0.16);
                    float speck = 1.0 - smoothstep(0.1, 0.16, distance(frac(l.xy * 2.0 + seed), 0.5));
                    fill += speck * 0.3 * step(edge, 0.6);
                    glow = act * 0.4;
                }
                else
                {
                    // Ribosome: a dense dot.
                    fill = 0.75;
                    lines = Band(edge, 0.9, 0.2);
                    glow = act * 0.3;
                }

                float a = saturate(fill + lines * _Line + glow * 0.4) * cover;
                float3 shade = col * (0.75 + 0.5 * lines + glow) + glow * 0.25;
                return half4(shade, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

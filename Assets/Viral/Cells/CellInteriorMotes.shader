// The loose stuff inside a cell in focus (CellInteriorView): camera-facing quads placed entirely in the vertex
// stage. Three modes (_MoteMode, per draw):
//   0 motes - one quad per mote (_CellMotes, CellInteriorView.Motes.cs): a lump of a substance on a trip from
//             'from' to 'to' (eased, bowed, ends tied to drifting bodies), then resting there with a small wobble.
//             Used up, it's sucked into what drains it: slow to let go, then rushing in, stretching along the pull,
//             swallowed into an organelle or thinning out through the membrane. Born motes grow in as they set
//             off. The CPU writes a trip only on events; nothing pops in or out. MoteAt is the one place a mote's
//             whereabouts are worked out (CellInteriorView.Where mirrors it on the CPU).
//             Honey (ATP): drops run together like a liquid. Each honey fragment sums a metaball field over its
//             cell's honey motes (_CellHoney, count in the frame's axisZ.w), evaluating each one's trip on the
//             spot: nothing simulated. The strongest drop at a pixel draws it (the others discard), so the joined
//             surface is blended once. Near the nucleus drops stretch along the band round it (PoolStretch), so
//             they pool into a ring; honey made by a body rides its stream's curve (HoneyPath) into the pool.
//   1 rings - three rings running out of a working organelle, one after another (_CellWaves).
//   2 streams - one quad per honey maker whose honey is riding its path (_CellWaves, to.w = 1): the span of the
//             path that honey covers (info.xy, CellInteriorView.StreamSpans), part of the same honey field: a stream
//             pours out as a drop sets off and drains into the pool after it.
// Looks per substance: Substances/SubstanceLook.hlsl (shared with the resource blobs and the extraction stream).
// Premultiplied alpha: looks cover, rings add. Motes draw under the bodies (Overlay+6),
// rings over them (Overlay+8).
Shader "Custom/CellInteriorMotes"
{
    Properties
    {
        _Brightness ("Brightness", Range(0, 3)) = 1.2
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay+6" }

        Pass
        {
            Name "Motes"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "CellInterior.hlsl"
            #include "../Substances/SubstanceLook.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Brightness;
            CBUFFER_END

            struct CellMote
            {
                float4 from;  // xyz frame, w seed of the body it's tied to (drifts with it), -1 none
                float4 to;
                float4 time;  // x start, y duration, z end (0 rest, 1 swallowed, 2 fade), w frame
                float4 look;  // x look (SubstanceLook: 0 chunk, 1 honey, 2 bubble, 3 crystal, 4 coil), y size (frame units, 0 none), z seed, w born
                float4 color; // rgb
            };
            StructuredBuffer<CellMote> _CellMotes;
            StructuredBuffer<uint> _CellHoney; // per frame slot, 256 each: global indices of its honey motes

            struct CellWave
            {
                float4 from;  // xyz frame, w seed of the body it's tied to
                float4 to;    // xyz the pool point its honey stream runs to (frame), w 1 = the stream shows
                float4 color; // rgb, a strength 0..1
                float4 info;  // xy the stream's span along its path (tail, head; 0 maker, 1 pool), z frame, w seed
                float4 shape; // y rings a second, z reach (frame units)
            };
            StructuredBuffer<CellWave> _CellWaves;
            uint _MoteMode, _CellWaveCount;
            float _NucleusSize; // frame units

            struct Attributes
            {
                uint vertexID   : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 quad       : TEXCOORD1; // -1..1
                float4 color      : TEXCOORD2; // rgb, a alpha
                float4 look       : TEXCOORD3; // x look, y ring, z seed
                nointerpolation uint2 mote : TEXCOORD4; // x its index (| STREAM for a stream), y frame (honey)
            };

            #define HONEY_REACH 2.3     // a drop's field reach, in its sizes (its quad is this big)
            #define STREAM 0x80000000u  // owner id of a stream (| its wave index)
            #define STREAM_STEPS 6      // segments a stream's curve is measured on
            #define STREAM_REACH float2(0.04, 0.055) // a stream's field reach (frame units) at its maker / at the pool

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            float3 Perpendicular(float3 d)
            {
                float3 p = float3(-d.y, d.x, 0.0);
                return dot(p, p) > 1e-8 ? normalize(p) : float3(1, 0, 0);
            }

            // Where a mote is at time t (frame units), its size (frame units, 0 none), alpha, how far through its
            // trip (k) and the pull (to - from, for stretching; zero unless it's being sucked in).
            void MoteAt(CellMote m, float t, out float3 p, out float size, out float alpha, out float k, out float3 pull)
            {
                float seed = m.look.z;
                size = m.look.y;
                alpha = 1.0;
                k = saturate((t - m.time.x) / max(m.time.y, 1e-3));
                float3 a = m.from.xyz + CellDrift(m.from.w, t), b = m.to.xyz + CellDrift(m.to.w, t);
                bool honey = abs(m.look.x - 1.0) < 0.5;
                float3 wobble = (honey ? 0.006 : 0.02) * float3(sin(t * 0.6 + seed * 40.0), sin(t * 0.47 + seed * 57.0), 0.5 * sin(t * 0.39 + seed * 23.0));
                float span = length(b - a);
                pull = 0;
                if (m.time.z > 2.5)
                {
                    // Drains away where it lies (spent honey: the pool thins).
                    p = a + wobble;
                    size *= 1.0 - smoothstep(0.0, 1.0, k);
                }
                else if (m.time.z > 0.5)
                {
                    // Sucked in: barely moving at first, then rushing, curling in as it closes.
                    float e = k * k * (0.35 + 0.65 * k);
                    p = lerp(a, b, e) + Perpendicular(b - a) * sin(k * PI) * (1.0 - k) * (seed - 0.5) * 0.5 * span
                      + wobble * (1.0 - k);
                    pull = b - a;
                    if (m.time.z < 1.5) size *= 1.0 - smoothstep(0.82, 1.0, k); // swallowed
                    else alpha = 1.0 - smoothstep(0.7, 1.0, k);                  // out through the membrane
                }
                else if (honey && m.look.w > 0.5 && m.from.w >= 0.0)
                {
                    // Honey made by a body rides its stream into the pool.
                    float e = k * k * (3.0 - 2.0 * k);
                    p = HoneyPath(a, b, e, HoneyBow(m.from.w, t)) + wobble * (1.0 - sin(k * PI));
                }
                else
                {
                    float e = k * k * (3.0 - 2.0 * k);
                    p = lerp(a, b, e) + Perpendicular(b - a) * sin(k * PI) * (seed - 0.5) * (honey ? 0.1 : 0.3) * span + wobble;
                }
                if (m.look.w > 0.5) size *= smoothstep(0.0, 0.3, k); // born: grows in as it leaves its source
            }

            // The honey pool: near the nucleus a drop's field is stretched along the band round it (1 = round), so
            // the drops there run together into a ring. Only in a cell with a nucleus (frac of the frame's axisZ.w).
            float PoolStretch(CellFrame f, float3 p)
            {
                float pool = frac(f.axisZ.w) > 0.1 ? 1.0 : 0.0;
                return 1.0 + 1.4 * pool * (1.0 - smoothstep(_NucleusSize + 0.16, _NucleusSize + 0.3, length(p)));
            }

            // On-screen direction from the cell's centre to a world point.
            float2 ScreenRadial(CellFrame f, float3 w, float3 right, float3 up)
            {
                float2 c = float2(dot(w - f.centre.xyz, right), dot(w - f.centre.xyz, up));
                return dot(c, c) > 1e-12 ? normalize(c) : float2(1.0, 0.0);
            }

            Varyings Dropped(Varyings o)
            {
                o.positionWS = 0;
                o.positionCS = float4(0.0, 0.0, 0.0, 1.0); // not out: dropped before rasterizing
                return o;
            }

            Varyings vert(Attributes v)
            {
                float2 q = Corners[v.vertexID % 6];
                float t = _Time.y;
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                Varyings o;
                o.quad = q;

                if (_MoteMode == 0u)
                {
                    CellMote m = _CellMotes[v.instanceID];
                    uint frame = (uint)m.time.w;
                    float3 p, pull;
                    float size, alpha, k;
                    MoteAt(m, t, p, size, alpha, k, pull);
                    o.look = float4(m.look.x, 0.0, m.look.z, 0.0);
                    o.mote = uint2(v.instanceID, frame);
                    CellFrame f = _CellFrames[frame];
                    alpha *= CellAppear(f, p);
                    o.color = float4(m.color.rgb, alpha);
                    if (alpha <= 0.002 || size <= 0.0) return Dropped(o);
                    float3 at = FramePoint(f, p);
                    float2 corner = q * size * f.axisX.w;
                    if (abs(m.look.x - 1.0) < 0.5)
                    {
                        // Honey: the field's whole reach, a little over (perspective), stretched along the pool's band.
                        float2 rad = ScreenRadial(f, at, right, up);
                        float reach = size * f.axisX.w * HONEY_REACH * 1.12;
                        corner = float2(-rad.y, rad.x) * q.x * reach * PoolStretch(f, p) + rad * q.y * reach;
                    }
                    else if (dot(pull, pull) > 1e-8)
                    {
                        // Stretched along the pull on screen as it speeds up.
                        float3 w = FrameVector(f, pull);
                        float2 d = float2(dot(w, right), dot(w, up));
                        d = dot(d, d) > 1e-10 ? normalize(d) : float2(1.0, 0.0);
                        float stretch = k * k;
                        float2 local = float2(q.x * (1.0 + 1.4 * stretch), q.y * (1.0 - 0.35 * stretch)) * size * f.axisX.w;
                        corner = d * local.x + float2(-d.y, d.x) * local.y;
                    }
                    o.positionWS = at + right * corner.x + up * corner.y;
                    o.positionCS = TransformWorldToHClip(o.positionWS);
                    return o;
                }

                CellWave w = _CellWaves[v.instanceID];
                CellFrame f = _CellFrames[(uint)w.info.z];
                float3 from = w.from.xyz + CellDrift(w.from.w, t);

                if (_MoteMode == 2u)
                {
                    // Streams: a quad over a honey maker's stream into the pool (its curve sways at most ~0.2 of its
                    // length off the straight line). Drawn as honey: the field decides who draws each pixel.
                    o.color = float4(w.color.rgb, 1.0);
                    o.look = float4(1.0, 0.0, w.info.w, 0.0);
                    o.mote = uint2(STREAM | v.instanceID, (uint)w.info.z);
                    if (w.to.w < 0.5) return Dropped(o);
                    float3 a = FramePoint(f, from), b = FramePoint(f, w.to.xyz);
                    float2 seg = float2(dot(b - a, right), dot(b - a, up));
                    float len = length(seg);
                    float2 d = len > 1e-6 ? seg / len : float2(1.0, 0.0);
                    float margin = 0.22 * length(b - a) + STREAM_REACH.y * f.axisX.w * 1.12;
                    float2 c = d * q.x * (len * 0.5 + margin) + float2(-d.y, d.x) * q.y * margin;
                    o.positionWS = (a + b) * 0.5 + right * c.x + up * c.y;
                    o.positionCS = TransformWorldToHClip(o.positionWS);
                    return o;
                }

                // Rings: three out of the body, one after another.
                uint i = v.vertexID / 6;
                float phase = frac(t * w.shape.y + (float)i / 3.0);
                float alpha = (1.0 - phase) * saturate(w.color.a * 3.0) * CellAppear(f, from);
                float size = w.shape.z * (0.15 + 0.85 * phase);
                o.color = float4(w.color.rgb, alpha);
                o.look = float4(1.0, 1.0, w.info.w, 0.0);
                o.mote = uint2(0u, 0u);
                if (alpha <= 0.002) return Dropped(o);
                o.positionWS = FramePoint(f, from) + (right * q.x + up * q.y) * size * f.axisX.w;
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // Honey: the field of this cell's honey at this pixel (on the camera's plane): every drop (stretched along
            // the pool's band near the nucleus) and every working maker's stream (a thin curve into the pool), its
            // gradient, and which of them is strongest here (it alone draws the pixel). 'pixel': a pixel in metres.
            half4 Honey(Varyings i, float pixel, float3 col, float alpha, float t)
            {
                CellFrame f = _CellFrames[i.mote.y];
                uint n = min((uint)f.axisZ.w, 256u), base = i.mote.y * 256u;
                float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz;
                float F = 0.0, best = -1.0, bestR = 1.0, bestSeed = 0.0;
                float2 G = 0; // d F / d pixel position, per metre
                uint owner = i.mote.x;
                for (uint j = 0; j < n; j++)
                {
                    uint index = _CellHoney[base + j];
                    CellMote m = _CellMotes[index];
                    float3 p, pull;
                    float size, a, k;
                    MoteAt(m, t, p, size, a, k, pull);
                    if (size <= 0.0) continue;
                    float R = size * f.axisX.w * HONEY_REACH;
                    float3 at = FramePoint(f, p);
                    float3 d = at - i.positionWS;
                    float2 d2 = float2(dot(d, right), dot(d, up)) / R;
                    float2 rad = ScreenRadial(f, at, right, up), tng = float2(-rad.y, rad.x);
                    float st = PoolStretch(f, p);
                    float along = dot(d2, tng) / st, across = dot(d2, rad);
                    float x = along * along + across * across;
                    if (x >= 1.0) continue;
                    float w = 1.0 - x;
                    a *= CellAppear(f, p);
                    float v = w * w * w * a;
                    F += v;
                    G -= 6.0 * w * w * a * (tng * along / st + rad * across) / R;
                    if (v > best) { best = v; bestR = R; owner = index; bestSeed = m.look.z; }
                }
                for (uint s = 0; s < _CellWaveCount; s++)
                {
                    CellWave w = _CellWaves[s];
                    if (w.to.w < 0.5 || (uint)w.info.z != i.mote.y) continue;
                    // Only the span its riding honey covers (tail..head along the path).
                    float3 a = w.from.xyz + CellDrift(w.from.w, t);
                    float2 bow = HoneyBow(w.from.w, t);
                    float3 d0 = FramePoint(f, HoneyPath(a, w.to.xyz, w.info.x, bow)) - i.positionWS;
                    float2 prev = float2(dot(d0, right), dot(d0, up)), near = prev;
                    float nearD = 1e20, nearU = 0.0;
                    [unroll] for (int c = 1; c <= STREAM_STEPS; c++)
                    {
                        float3 dc = FramePoint(f, HoneyPath(a, w.to.xyz, lerp(w.info.x, w.info.y, c / (float)STREAM_STEPS), bow)) - i.positionWS;
                        float2 next = float2(dot(dc, right), dot(dc, up)), seg = next - prev;
                        float u = saturate(-dot(prev, seg) / max(dot(seg, seg), 1e-12));
                        float2 closest = prev + seg * u;
                        float dd = dot(closest, closest);
                        if (dd < nearD) { nearD = dd; near = closest; nearU = lerp(w.info.x, w.info.y, (c - 1 + u) / STREAM_STEPS); }
                        prev = next;
                    }
                    float R = f.axisX.w * lerp(STREAM_REACH.x, STREAM_REACH.y, nearU);
                    float2 d2 = near / R;
                    float x = dot(d2, d2);
                    if (x >= 1.0) continue;
                    float w3 = 1.0 - x;
                    float a2 = 1.2 * CellAppear(f, w.to.xyz); // a little over the level: holds together
                    float v = w3 * w3 * w3 * a2;
                    F += v;
                    G -= 6.0 * w3 * w3 * a2 * d2 / R;
                    if (v > best) { best = v; bestR = R; owner = STREAM | s; bestSeed = w.info.w; }
                }
                if (owner != i.mote.x) discard; // another drop / stream is stronger here: it draws this pixel
                return HoneyShade(F, G * bestR, max(length(G) * pixel, 1e-3), col, alpha, t, bestSeed);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float pixel = length(fwidth(i.positionWS)); // before any branch
                float cover = SweepCover(i.positionWS);
                clip(cover - 0.001);

                float2 q = i.quad;
                float r = length(q), t = _Time.y, s = i.look.z, alpha = i.color.a * cover;
                float3 col = i.color.rgb * _Brightness;
                if (i.look.y > 0.5)
                {
                    float band = 1.0 - smoothstep(0.03, 0.09, abs(r - 0.9));
                    return half4(col * band * alpha, 0.0);
                }
                if (abs(i.look.x - 1.0) < 0.5) return Honey(i, pixel, col, cover, t);
                return SubstanceMote(i.look.x, q, s, col, alpha, t);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

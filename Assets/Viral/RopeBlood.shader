// Blood beads filling a cauterized rope (VirusRope), without any bead objects: VirusRope draws a
// see-through proxy -- a tube just wider than the beads can reach, plus a disc over each base --
// and this ray-traces the beads inside it per pixel. They're real spheres (own silhouettes, depth
// and normals, so shadows and the focus outlines see them), laid out by rules, not data:
//
//  Tube: _BeadLattice.x strands round the rope in a 3-start helix, a bead every rowStep along each,
//        sitting on the swollen shell (ShellRadius: full swell behind the front, bolus at it,
//        heartbeat bursts rippling up, flaring into each base). A bead exists once the blood's
//        fill there passes its own random threshold, so they snap in one by one.
//  Skirt: rings of beads spreading over the base's surface, shrinking outward, filling in order.
//
// Each pixel steps a few times through the proxy along its view ray and, at each step, tests the
// handful of lattice beads nearest (4 on the tube, 3 per nearby ring on a skirt). Lit exactly like
// the cells (CellShade from BloodCellForward), so VirusRope copies the cell material onto this
// one (beadLook) and they wear the environment's colours; alternating beads come in as low / high
// cell surface heights (deep / surface colour), and a heartbeat burst brightens them.
//
// Proxy vertex data: uv0 = (mode 0 tube / 1 skirt, metres from the source base (tube) or its
// base's (skirt), skirt order -1 source / +1 far, proxy radius), uv1 = axis point / base surface
// point, uv2 = along the rope toward growing s / surface normal, uv3 = ring frame / skirt angle 0.
// Per rope (MaterialPropertyBlock): _BeadPump, _BeadShape, _BeadHeart, _BeadLattice, _BeadBase.
Shader "Custom/RopeBlood"
{
    Properties
    {
        [Header(Beads)]
        _BeadLumps ("Lumps Across A Bead", Range(0.2, 6)) = 1.5

        [Header(Color)]
        _Color ("Surface Color (light beads)", Color) = (0.93, 0.21, 0.21, 1)
        _DeepColor ("Deep Color (dark beads)", Color) = (0.30, 0.08, 0.08, 1)
        _MainTex ("Detail Texture (optional)", 2D) = "white" {}
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.0

        [Header(Surface Relief)]
        _NoiseScale ("Lump Scale", Float) = 1.88
        _BumpStrength ("Bump Strength", Range(0,2)) = 2
        _Detail ("Fine Detail", Range(0,1)) = 1
        _Lacunarity ("Lacunarity", Range(1.5,4)) = 2.19
        _Gain ("Gain", Range(0.2,0.8)) = 0.42

        [Header(Distance_Stability)]
        _DetailFadeStart ("Fine Detail Fade Start", Float) = 20
        _DetailFadeEnd ("Fine Detail Fade End", Float) = 100
        _DistantTextureDetailMultiplier ("Distant Texture Detail Multiplier", Range(0,1)) = 0.15
        _DistantDetail ("Distant Fine Detail", Range(0,1)) = 0.32
        _DistantBumpMultiplier ("Distant Bump Multiplier", Range(0,1)) = 0.24

        [Header(Wetness)]
        _Glossiness ("Smoothness", Range(0,1)) = 0
        _GlossVariation ("Smoothness Variation", Range(0,0.5)) = 0.14
        _SpecTint ("Specular Tint (also tints reflections)", Color) = (0.17, 0.09, 0.09, 1)
        _OcclusionStrength ("Cavity Shading", Range(0,1)) = 1

        [Header(Subsurface)]
        _SubsurfaceColor ("Subsurface Color", Color) = (0.61, 0.44, 0.40, 1)
        _SubsurfaceStrength ("Subsurface Strength", Range(0,3)) = 0.9
        _RimPower ("Rim Falloff", Range(0.5,8)) = 2.6
        _ThinGlow ("Thin-Area Glow", Range(0,1)) = 0.6

        [Header(Cel Shading)]
        [KeywordEnum(Smooth, Cel)] _Shading ("Shading Mode", Float) = 1
        _LightBands ("Light Bands", Range(2,8)) = 2
        _BandSoftness ("Band Edge Softness", Range(0,0.25)) = 0.03
        _ColorSteps ("Surface Color Steps (1 = off)", Range(1,8)) = 1
        _RimSteps ("Rim Steps", Range(1,4)) = 1
        _SpecThreshold ("Specular Cutoff", Range(0,1)) = 0.55

        [Header(Animation)]
        _PulseAmount ("Fluctuation Amount", Range(0,1)) = 0.5
        _PulseSpeed ("Fluctuation Speed", Range(0,6)) = 1.2
        _PulseVariation ("Fluctuation Variation", Range(0,2)) = 1.0

        [Header(Motion)]
        [Toggle] _FollowRipples ("Ride Nearby Cell Ripples", Float) = 1

        [Header(Mapping)]
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1,32)) = 6.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" "Queue"="AlphaTest" }

        HLSLINCLUDE
        #define _SPACE_WORLD 1
        #include "BloodCellCore.hlsl"

        // Per rope (VirusRope.BuildBlood). Metres are along the rope from the base the blood came from.
        float4 _BeadPump;    // x front, y blood edge, z snap-in length (pumpBlend), w heartbeat left (0..1)
        float4 _BeadShape;   // x rope radius, y swell, z front length, w bolus
        float4 _BeadHeart;   // x burst size, y beat spacing (m), z beat speed (m/s), w burst glow
        float4 _BeadLattice; // x strands, y bead radius per metre of shell, z rope length drawn, w seed
        float4 _BeadBase;    // x flare, y flare length, z skirt rings, w skirt puck floor depth
        float _BeadLumps;    // lumps across a bead (material; survives the cell look copy: cells don't have it)

        struct ProxyAttributes
        {
            float4 positionOS : POSITION;
            float4 info       : TEXCOORD0;
            float4 axis       : TEXCOORD1;
            float4 dir        : TEXCOORD2;
            float4 ref        : TEXCOORD3;
        };

        struct ProxyVaryings
        {
            float4 positionHCS : SV_POSITION;
            float3 positionWS  : TEXCOORD0;
            float4 info        : TEXCOORD1;
            float3 axis        : TEXCOORD2;
            float3 dir         : TEXCOORD3;
            float3 ref         : TEXCOORD4;
        };

        ProxyVaryings ProxyVert(ProxyAttributes v)
        {
            ProxyVaryings o;
            float3 p = TransformObjectToWorld(v.positionOS.xyz);
            float3 axis = TransformObjectToWorld(v.axis.xyz);
            if (_FollowRipples > 0.5)
            {
                float3 wave = RippleFieldOffset(axis); // the whole stretch rides the wave together
                p += wave;
                axis += wave;
            }
            o.positionWS = p;
            o.positionHCS = TransformWorldToHClip(p);
            o.info = v.info;
            o.axis = axis;
            o.dir = TransformObjectToWorldDir(v.dir.xyz, false);
            o.ref = TransformObjectToWorldDir(v.ref.xyz, false);
            return o;
        }

        // ---------------- the rules ----------------

        uint HashU(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        float Hash01(int a, int b, float seed)
        {
            uint h = HashU(asuint(a) * 0x9E3779B1u + HashU(asuint(b) + (uint)seed * 0x85EBCA6Bu));
            return (h & 0xFFFFFFu) / 16777215.0;
        }

        float Repeat(float x, float p) { return x - floor(x / p) * p; }

        float Ricker(float x)
        {
            float x2 = x * x;
            return (1.0 - x2) * exp(-0.5 * x2);
        }

        // Lub-dub bursts travelling up from the source (same as VirusRope.Heartbeat).
        float Heartbeat(float s)
        {
            float period = _BeadHeart.y, width = period * 0.1;
            float phase = s - _Time.y * _BeadHeart.z;
            float d  = Repeat(phase + period * 0.5,  period) - period * 0.5;
            float d2 = Repeat(phase + period * 0.78, period) - period * 0.5;
            return Ricker(d / width) + 0.55 * Ricker(d2 / width);
        }

        // Radius the beads sit at, s metres from the source (as VirusRope.Swell, then the base flare).
        float ShellRadius(float s, out float burst)
        {
            float swell = _BeadShape.y, front = _BeadPump.x, frontLength = _BeadShape.z;
            float r = 1.0;
            burst = 0.0;
            if (s <= front + frontLength)
            {
                float fill = smoothstep(0.0, 1.0, (front - s) / frontLength);
                float x = (s - (front - frontLength * 0.5)) / (frontLength * 0.35);
                burst = Heartbeat(s) * fill * _BeadPump.w;
                r = 1.0 + (swell - 1.0) * fill + _BeadShape.w * exp(-x * x) + _BeadHeart.x * burst;
            }
            r = max(r, swell * 0.6);
            float f = saturate(1.0 - min(s, _BeadLattice.z - s) / _BeadBase.y);
            return _BeadShape.x * r * (1.0 + _BeadBase.x * f * f);
        }

        // Snapped in yet? Size 0.8 .. 1 as it settles; 0 = not there.
        float BeadSize(float s, float threshold)
        {
            float fill = (_BeadPump.y - s) / _BeadPump.z;
            return fill < threshold ? 0.0 : lerp(0.8, 1.0, saturate((fill - threshold) * 8.0));
        }

        // Shift of the bead's cell surface height: alternate beads lean to the deep colour.
        float Shade(int alternate, float h)
        {
            float v = frac(h * 37.13);
            return (alternate & 1) != 0 ? lerp(-0.35, -0.2, v) : lerp(-0.02, 0.1, v);
        }

        // ---------------- tracing ----------------

        struct Hit { float t; float3 c; float r; float shade; float glow; float seed; };

        void TestSphere(float3 o, float3 d, float3 c, float r, float shade, float glow, float seed, inout Hit hit)
        {
            float3 oc = o - c;
            float b = dot(oc, d);
            float disc = b * b - (dot(oc, oc) - r * r);
            if (disc < 0.0) return;
            float t = max(-b - sqrt(disc), 0.0);
            if (t >= hit.t || -b + sqrt(disc) < 0.0) return;
            hit.t = t; hit.c = c; hit.r = r; hit.shade = shade; hit.glow = glow; hit.seed = seed;
        }

        void TubeCell(float3 P, float3 o, float3 d, float3 O, float3 T, float3 N, float3 B, float sF, inout Hit hit)
        {
            int strands = (int)_BeadLattice.x;
            float perShell = _BeadLattice.y, arc = _BeadLattice.z;
            float bead = _BeadShape.x * _BeadShape.y * perShell;
            float rowStep = bead * 1.7, rise = rowStep * 3.0 / strands;

            float3 rel = P - O;
            float ax = dot(rel, T);
            float sP = sF + ax;
            float3 radial = rel - T * ax;
            float th = atan2(dot(radial, B), dot(radial, N));
            th = th < 0.0 ? th + TWO_PI : th;
            float step = TWO_PI / strands;
            int k0 = (int)floor(th / step);

            [unroll] for (int dk = 0; dk < 2; dk++)
            {
                int k = k0 + dk;
                int j0 = (int)floor((sP - k * rise) / rowStep);
                [unroll] for (int dj = 0; dj < 2; dj++)
                {
                    int j = j0 + dj;
                    float sc = j * rowStep + k * rise;
                    if (sc < -1.5 * bead || sc > arc + 1.5 * bead) continue;
                    // Strand 'strands' is strand 0 three rows on (the helix closes).
                    int kw = k >= strands ? k - strands : k;
                    int jw = k >= strands ? j + 3 : j;
                    float h = Hash01(jw, kw, _BeadLattice.w);
                    float size = BeadSize(sc, h);
                    if (size <= 0.0) continue;
                    float burst;
                    float R = ShellRadius(sc, burst);
                    float a = k * step;
                    float3 c = O + T * (sc - sF) + (N * cos(a) + B * sin(a)) * R;
                    TestSphere(o, d, c, R * perShell * size, Shade(jw, h), burst * _BeadHeart.w, h, hit);
                }
            }
        }

        // Where a ray (offset w0 and direction dp, both across the axis; a = |dp|^2) is inside a
        // cylinder of radius R round the axis: (enter, leave), or enter > leave if it misses.
        float2 Cylinder(float3 w0, float3 dp, float a, float R)
        {
            float b = dot(w0, dp), c = dot(w0, w0) - R * R;
            if (a < 1e-8) return c <= 0.0 ? float2(-1e9, 1e9) : float2(1e9, -1e9); // along the axis
            float disc = b * b - a * c;
            if (disc < 0.0) return float2(1e9, -1e9);
            float q = sqrt(disc);
            return float2((-b - q) / a, (-b + q) / a);
        }

        #define TUBE_STEPS 24

        // Test the beads near points 'step' apart from t0 on (not spread over [t0, t1]: looking along
        // the rope the band runs on and on, and spreading the budget over it skipped the near beads,
        // the ones that matter). Returns steps left.
        int MarchTube(float t0, float t1, float step, int budget, float3 o, float3 d, float3 O, float3 T, float3 N, float3 B, float sF, inout Hit hit)
        {
            if (t1 <= t0 || budget <= 0) return budget;
            int count = min((int)ceil((t1 - t0) / step), budget);
            [loop] for (int s = 0; s < count; s++)
            {
                float ts = min(t0 + (s + 0.5) * step, t1);
                if (ts > hit.t + step) break; // already hit something nearer
                TubeCell(o + d * ts, o, d, O, T, N, B, sF, hit);
            }
            return budget - count;
        }

        // March only through the band the beads sit in (the shell here, give or take a bead and the
        // heartbeat's swell), at most ~3/4 of a bead per step so none is skipped at a grazing angle:
        // the near side, then, if the ray passes beside the rope's core, the far side too.
        void TraceTube(float3 o, float3 d, ProxyVaryings i, inout Hit hit)
        {
            float3 O = i.axis, T = normalize(i.dir);
            float3 N = normalize(i.ref - T * dot(i.ref, T));
            float3 B = cross(T, N);
            float sF = i.info.y;

            float burst;
            float R = ShellRadius(sF, burst);
            float bead = R * _BeadLattice.y;
            float outerR = min(i.info.w, R * 1.3 + bead * 1.2);
            float innerR = max(R * 0.75 - bead * 1.2, 0.0);
            float coreR = _BeadShape.x * 0.95;

            float3 w0 = (o - O) - T * dot(o - O, T);
            float3 dp = d - T * dot(d, T);
            float a = dot(dp, dp);
            float2 outer = Cylinder(w0, dp, a, outerR);
            float2 inner = Cylinder(w0, dp, a, innerR);
            float2 core = Cylinder(w0, dp, a, coreR);
            float t0 = max(outer.x, 0.0), t1 = outer.y;
            float step = bead * 0.75;
            int budget = TUBE_STEPS;

            if (inner.x > inner.y) // passes outside the inner band edge: one stretch
            {
                MarchTube(t0, t1, step, budget, o, d, O, T, N, B, sF, hit);
                return;
            }
            budget = MarchTube(t0, inner.x, step, budget, o, d, O, T, N, B, sF, hit);
            if (hit.t < 1e8 || core.x <= core.y) return; // hit, or the core hides the far side
            MarchTube(max(inner.y, t0), t1, step, budget, o, d, O, T, N, B, sF, hit);
        }

        void SkirtCell(float3 P, float3 o, float3 d, float3 C, float3 N, float3 U, float3 W, float sBase, float order, inout Hit hit)
        {
            float bead = _BeadShape.x * _BeadShape.y * _BeadLattice.y; // resting bead, for the fill order
            float flare = _BeadBase.x;
            // Start from the tube's own radius where it meets the base -- swell, bursts, bead size and
            // all -- so the skirt always carries on from the flare instead of the tube lifting off it.
            float burst;
            float radius = ShellRadius(sBase, burst);
            float size = radius * _BeadLattice.y * (1.0 + flare * 0.8) / (1.0 + flare);
            int rings = (int)_BeadBase.z;

            float3 rel = P - C;
            float3 planar = rel - N * dot(rel, N);
            float rho = length(planar);
            float phi = atan2(dot(planar, W), dot(planar, U));
            int side = order > 0.0 ? 1 : 0;

            [loop] for (int r = 0; r < 6; r++)
            {
                if (r >= rings) break;
                float b = size * (1.0 - 0.22 * r);
                if (r > 0) radius += b * 1.7;
                if (abs(rho - radius) > b * 2.0) continue;

                int count = max(3, (int)ceil(PI * radius / (b * 0.85)));
                float step = TWO_PI / count;
                float twist = Hash01(r, side, _BeadLattice.w + 77.0) * TWO_PI;
                float s = sBase + order * (r + 1) * bead;   // the order the blood reaches it
                int k0 = (int)floor(Repeat(phi - twist, TWO_PI) / step);

                [unroll] for (int dk = -1; dk <= 1; dk++)
                {
                    int k = k0 + dk;
                    k = k < 0 ? k + count : (k >= count ? k - count : k);
                    float h = Hash01(r * 1000 + k, side + 5, _BeadLattice.w);
                    float grown = BeadSize(s, h);
                    if (grown <= 0.0) continue;
                    float a = twist + (k + (h - 0.5) * 0.4) * step;
                    float rr = radius * (1.0 + (h - 0.5) * 0.15);
                    float3 c = C + (U * cos(a) + W * sin(a)) * rr + N * (b * 0.35);
                    TestSphere(o, d, c, b * grown, Shade(r + k, h), 0.0, h, hit);
                }
            }
        }

        // Through the skirt's puck (in at its lid, wall or floor) until the ray leaves it, ~3/4 of
        // its biggest bead per step from where it came in. Not stopped at the surface plane: the
        // beads' undersides reach below it (and the cell curves and dips away under them), so seen
        // from low down or below they were cut open; the cell's own depth hides what's inside it.
        void TraceSkirt(float3 o, float3 d, ProxyVaryings i, inout Hit hit)
        {
            float3 C = i.axis, N = normalize(i.dir);
            float3 U = normalize(i.ref - N * dot(i.ref, N));
            float3 W = cross(N, U);
            float h = dot(o - C, N), rise = dot(d, N);
            float floorD = _BeadBase.w, lid = floorD * 1.4;   // as the proxy is built
            float tSlab = rise < -1e-4 ? (h + floorD) / -rise : rise > 1e-4 ? (lid - h) / rise : 1e9;
            float3 w0 = (o - C) - N * h;
            float3 dp = d - N * rise;
            float tEnd = min(tSlab, Cylinder(w0, dp, dot(dp, dp), i.info.w * 1.01).y);
            float step = _BeadShape.x * _BeadShape.y * _BeadLattice.y * (1.0 + _BeadBase.x * 0.8) * 0.75;
            int count = clamp((int)ceil(tEnd / step), 1, 24);

            [loop] for (int s = 0; s < count; s++)
            {
                float ts = min((s + 0.5) * step, tEnd);
                if (ts > hit.t + step) break;
                SkirtCell(o + d * ts, o, d, C, N, U, W, i.info.y, i.info.z, hit);
            }
        }

        // The proxy's own frame (tube: across, across, along; skirt: round, round, up): bead
        // textures are mapped in it so they ride the rope instead of the world.
        void BeadFrame(ProxyVaryings i, out float3 X, out float3 Y, out float3 Z)
        {
            Z = normalize(i.dir);
            X = normalize(i.ref - Z * dot(i.ref, Z));
            Y = cross(Z, X);
        }

        // The nearest bead along ray d from this proxy pixel; t = 1e9 if none.
        Hit Trace(ProxyVaryings i, float3 d)
        {
            Hit hit;
            hit.t = 1e9; hit.c = 0; hit.r = 1; hit.shade = 0; hit.glow = 0; hit.seed = 0;
            if (i.info.x < 0.5) TraceTube(i.positionWS, d, i, hit);
            else TraceSkirt(i.positionWS, d, i, hit);
            return hit;
        }

        float3 ViewRay(float3 positionWS) { return -GetWorldSpaceNormalizeViewDir(positionWS); } // perspective or orthographic

        float DeviceDepth(float3 positionWS)
        {
            float4 h = TransformWorldToHClip(positionWS);
            return h.z / h.w;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex ProxyVert
            #pragma fragment BeadFrag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _SHADING_SMOOTH _SHADING_CEL

            #include "BloodCellForward.hlsl"

            half4 BeadFrag(ProxyVaryings i, out float depth : SV_Depth) : SV_Target
            {
                float3 d = ViewRay(i.positionWS);
                Hit hit = Trace(i, d);
                clip(hit.t < 1e8 ? 1.0 : -1.0);

                float3 pos = i.positionWS + d * hit.t;
                float3 geoN = normalize(pos - hit.c);
                float4 hc = TransformWorldToHClip(pos);
                depth = hc.z / hc.w;

                // Each bead a tiny cell: the cells' own lumps (noise, fluctuation, bump), _BeadLumps
                // across a bead, mapped in the rope's frame (so they turn with it) from a patch of
                // noise of its own.
                float3 X, Y, Z;
                BeadFrame(i, X, Y, Z);
                float3 local = geoN;
                float3 map = float3(dot(local, X), dot(local, Y), dot(local, Z)) * (_BeadLumps / max(_NoiseScale, 1e-3))
                           + hit.seed * 61.7;
                float fade = DetailFade(pos);
                float4 hd = SurfaceHeight(map, fade);
                float3 grad = X * hd.y + Y * hd.z + Z * hd.w; // map -> world directions (tilt as on a cell)
                float3 n = BumpNormal(geoN, float4(hd.x, grad), float4(0, 0, 0, 0), fade);
                float h = saturate(hd.x + hit.shade);

                half3 rgb = CellShade(pos, i.positionHCS, map, geoN, n, h, fade);
                rgb *= 1.0 + hit.glow; // heartbeat burst passing
                return half4(MixFog(rgb, ComputeFogFactor(hc.z)), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ProxyVert
            #pragma fragment BeadShadowFrag
            #pragma target 4.5
            #pragma multi_compile _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            half4 BeadShadowFrag(ProxyVaryings i, out float depth : SV_Depth) : SV_Target
            {
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 d = normalize(i.positionWS - _LightPosition);
            #else
                float3 d = -_LightDirection;
            #endif
                Hit hit = Trace(i, d);
                clip(hit.t < 1e8 ? 1.0 : -1.0);
                float3 pos = i.positionWS + d * hit.t;
                float4 cs = TransformWorldToHClip(ApplyShadowBias(pos, normalize(pos - hit.c), -d));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, cs.w * UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, cs.w * UNITY_NEAR_CLIP_VALUE);
            #endif
                depth = cs.z / cs.w;
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex ProxyVert
            #pragma fragment BeadDepthFrag
            #pragma target 4.5

            half4 BeadDepthFrag(ProxyVaryings i, out float depth : SV_Depth) : SV_Target
            {
                float3 d = ViewRay(i.positionWS);
                Hit hit = Trace(i, d);
                clip(hit.t < 1e8 ? 1.0 : -1.0);
                depth = DeviceDepth(i.positionWS + d * hit.t);
                return depth;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex ProxyVert
            #pragma fragment BeadNormalsFrag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            half4 BeadNormalsFrag(ProxyVaryings i, out float depth : SV_Depth) : SV_Target
            {
                float3 d = ViewRay(i.positionWS);
                Hit hit = Trace(i, d);
                clip(hit.t < 1e8 ? 1.0 : -1.0);
                float3 pos = i.positionWS + d * hit.t;
                depth = DeviceDepth(pos);
                float3 n = normalize(pos - hit.c);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
                return half4(PackFloat2To888(oct), 0.0);
            #else
                return half4(n, 0.0);
            #endif
            }
            ENDHLSL
        }
    }

    FallBack Off
}

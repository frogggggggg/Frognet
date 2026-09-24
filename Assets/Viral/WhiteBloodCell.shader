// White blood cells (WhiteBloodCells.cs draws them, mesh from WhiteBloodCellMesh.cs): a soft white ball
// with ruffled membrane folds that flows like an amoeba and stretches a pseudopod out to its mouth.
// Everything is shaped here, in the vertex stage, so every pass (shadows, depth, depth normals and so
// the focus outlines) sees the same shape:
// - the mesh is a unit sphere in the cell's *reach frame* (+Z = toward the mouth); the membrane is
//   sampled in the *body* frame, so its folds stay put on the cell while the mouth slides round it;
// - body: a slow soft undulation, ruffles (ridged noise in patches: thin folded crests, like the
//   membrane in micrographs), a leading edge that bulges where it crawls, a tail that tapers;
// - the cap round +Z (CAP_ANGLE, = WhiteBloodCellMesh.CapAngle) becomes the pseudopod: a surface of
//   revolution whose radius is a smooth max of the body's and a tapered, irregular, meandering finger's,
//   so it grows out of the body with a fillet instead of a seam. At rest it's a flowing, irregular mouth
//   on the surface; its lip ruffles and a few wisps drift round it (hunger);
// - wrap (state.y) folds the lips forward round the prey (state.w, its radius) until they meet in front.
// Ripples: the cell rides RippleField like anything else (Surface impacts publish there).
// Normals: the shape evaluated at two nearby directions (finite differences), ripples included.
// Per instance (_Cells[_InstanceOffset + SV_InstanceID]): centre + radius, body rotation, reach
// (world direction, extension in radii), motion (velocity / top speed, seed),
// state (hunger, wrap, mouth radius in radii, prey radius in radii).
Shader "Custom/WhiteBloodCell"
{
    Properties
    {
        _Albedo ("Albedo", Color) = (0.95, 0.93, 0.92, 1)
        _Cavity ("Cavity", Color) = (0.8, 0.72, 0.76, 1)
        _MouthColor ("Mouth", Color) = (1, 0.45, 0.7, 1)
        _MouthGlow ("Mouth Glow", Float) = 1.1
        _Rim ("Rim", Color) = (1, 0.9, 0.94, 1)
        _Translucency ("Translucency", Range(0, 1)) = 0.6
        _Smoothness ("Smoothness", Range(0, 1)) = 0.4
        _Lumps ("Ruffle Height (radii)", Float) = 0.05
        _LumpScale ("Ruffle Frequency", Float) = 2.6
        _FineLumps ("Fine Ruffle Height (radii)", Float) = 0.012
        _FineScale ("Fine Ruffle Frequency", Float) = 8
        _Wobble ("Flow Undulation (radii)", Float) = 0.04
        _Lobes ("Leading Lobes (radii)", Float) = 0.09
        _Cup ("Mouth Cup Depth", Float) = 0.45
        _Lip ("Lip Height (tip radii)", Float) = 0.25
        _Tendrils ("Wisp Frequency", Float) = 2.6
        _TendrilLength ("Wisp Length (radii)", Float) = 0.3
        _Speed ("Animation Speed", Float) = 1

        [Header(Ripples (read by Surface and RippleField))]
        _RippleAmplitude ("Ripple Amplitude", Float) = 1.8
        _RippleWavelength ("Ripple Wavelength", Float) = 3
        _RippleWidth ("Ripple Width", Float) = 1.6
        _RippleSpeed ("Ripple Speed", Float) = 5
        _RippleDecay ("Ripple Decay", Float) = 1.2
        _RippleInitialRadius ("Ripple Initial Radius", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "RippleField.hlsl"

        #define CAP_ANGLE 0.7   // WhiteBloodCellMesh.CapAngle
        #define TIP_SPLIT 0.4   // share of the cap that rounds the tip; the rest is the shaft
        #define MOUTH_RIM 0.28  // where the lip rings the mouth, in cap units
        #define FILLET 0.16     // how softly the finger grows out of the body (radii)

        CBUFFER_START(UnityPerMaterial)
            float4 _Albedo, _Cavity, _MouthColor, _Rim;
            float _MouthGlow, _Translucency, _Smoothness, _Lumps, _LumpScale, _FineLumps, _FineScale;
            float _Wobble, _Lobes, _Cup, _Lip, _Tendrils, _TendrilLength, _Speed;
            float _RippleAmplitude, _RippleWavelength, _RippleWidth, _RippleSpeed, _RippleDecay, _RippleInitialRadius;
        CBUFFER_END

        struct Instance { float4 positionRadius, rotation, reach, motion, state; };
        StructuredBuffer<Instance> _Cells;
        uint _InstanceOffset;
        float _Detail; // 1 near (ruffles in the geometry and per pixel), 0 far

        struct Attributes
        {
            float4 positionOS : POSITION; // unit direction in the reach frame
            uint instanceID   : SV_InstanceID;
        };

        // ---------------- noise ----------------

        float Hash13(float3 p)
        {
            p = frac(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return frac((p.x + p.y) * p.z);
        }

        // Value noise (0..1) with its gradient (xyz of the result's yzw), quintic fade.
        float4 NoiseD(float3 x)
        {
            float3 i = floor(x), w = x - i;
            float3 u = w * w * w * (w * (w * 6.0 - 15.0) + 10.0);
            float3 du = 30.0 * w * w * (w * (w - 2.0) + 1.0);
            float a = Hash13(i), b = Hash13(i + float3(1, 0, 0)), c = Hash13(i + float3(0, 1, 0)), d = Hash13(i + float3(1, 1, 0));
            float e = Hash13(i + float3(0, 0, 1)), f = Hash13(i + float3(1, 0, 1)), g = Hash13(i + float3(0, 1, 1)), h = Hash13(i + float3(1, 1, 1));
            float k1 = b - a, k2 = c - a, k3 = e - a, k4 = a - b - c + d, k5 = a - c - e + g, k6 = a - b - e + f, k7 = -a + b + c - d + e - f - g + h;
            float v = a + k1 * u.x + k2 * u.y + k3 * u.z + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z;
            float3 grad = du * float3(k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
                                      k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
                                      k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);
            return float4(v, grad);
        }

        float Noise(float3 x)
        {
            float3 i = floor(x), f = x - i;
            f = f * f * (3.0 - 2.0 * f);
            float a = lerp(Hash13(i), Hash13(i + float3(1, 0, 0)), f.x);
            float b = lerp(Hash13(i + float3(0, 1, 0)), Hash13(i + float3(1, 1, 0)), f.x);
            float c = lerp(Hash13(i + float3(0, 0, 1)), Hash13(i + float3(1, 0, 1)), f.x);
            float d = lerp(Hash13(i + float3(0, 1, 1)), Hash13(i + float3(1, 1, 1)), f.x);
            return lerp(lerp(a, b, f.y), lerp(c, d, f.y), f.z);
        }

        // Membrane ruffles: thin crests along the noise's mid-line (ridged), only in patches. 0..1, gradient out.
        float Ruffle(float3 x, float seed, out float3 grad)
        {
            float4 n = NoiseD(x);
            float s = n.x * 2.0 - 1.0;
            float r = 1.0 - abs(s);
            float m = smoothstep(0.32, 0.72, Noise(x * 0.37 + seed + 11.3));
            grad = 3.0 * r * r * (-sign(s) * 2.0 * n.yzw) * m;
            return r * r * r * m;
        }

        // ---------------- frames ----------------

        float3 Rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
        float3 InvRotate(float4 q, float3 v) { return Rotate(float4(-q.xyz, q.w), v); }

        struct Frame
        {
            float3 x, y, z;    // reach frame in world (z = toward the mouth)
            float4 body;       // body rotation
            float3 move;       // motion in the reach frame (0..1 long)
            float2 bend1, bend2; // the finger's meander (one bow, one S)
            float seed, hunger, wrap, tip, prey, ext, t;
        };

        Frame MakeFrame(Instance inst)
        {
            Frame f;
            f.body = inst.rotation;
            f.z = normalize(inst.reach.xyz);
            float3 bx = Rotate(f.body, float3(1, 0, 0));
            if (abs(dot(bx, f.z)) > 0.95) bx = Rotate(f.body, float3(0, 1, 0));
            f.x = normalize(bx - f.z * dot(bx, f.z));
            f.y = cross(f.z, f.x);
            float3 m = inst.motion.xyz;
            f.move = float3(dot(m, f.x), dot(m, f.y), dot(m, f.z));
            f.seed = inst.motion.w;
            f.hunger = inst.state.x;
            f.wrap = inst.state.y;
            f.prey = inst.state.w;
            // The mouth opens wide enough for what it's wrapping.
            f.tip = lerp(inst.state.z, max(inst.state.z, f.prey * 1.35), f.wrap);
            f.ext = inst.reach.w;
            f.t = _Time.y * _Speed + f.seed * 7.0;
            float s = f.seed * 1.37;
            f.bend1 = float2(Noise(float3(f.t * 0.23, s, 1.1)), Noise(float3(s, f.t * 0.21, 4.7))) - 0.5;
            f.bend2 = float2(Noise(float3(f.t * 0.31, s, 8.3)), Noise(float3(s, f.t * 0.27, 2.9))) - 0.5;
            return f;
        }

        float3 ToWorld(Frame f, float3 v) { return f.x * v.x + f.y * v.y + f.z * v.z; }
        float3 ToBody(Frame f, float3 v) { return InvRotate(f.body, ToWorld(f, v)); }

        // ---------------- shape ----------------

        // How far the membrane sits off the plain shape at reach-frame point p (radii). lump: ruffle crest 0..1.
        float Membrane(Frame f, float3 p, out float lump)
        {
            float3 q = ToBody(f, p); // sampled on the body, so the folds stay put
            float3 flow = float3(f.t * 0.05, -f.t * 0.03, f.t * 0.04);
            float h = _Wobble * (Noise(q * 1.5 + float3(0.0, f.t * 0.12, f.t * 0.07) + f.seed) - 0.5) * 2.0
                    + _Wobble * 0.5 * (Noise(q * 3.1 - flow * 2.0 + f.seed * 2.3) - 0.5) * 2.0;
            lump = 0.0;
            if (_Detail > 0.5)
            {
                float3 g;
                lump = Ruffle(q * _LumpScale + flow + f.seed, f.seed, g);
                h += _Lumps * lump;
            }

            // Amoeboid crawl: the leading edge bulges and feels about with two lobes, the tail tapers.
            float mv = length(f.move);
            if (mv > 1e-3)
            {
                float3 d = normalize(p);
                float3 m = f.move / mv;
                float lead = dot(d, m);
                h += mv * (_Lobes * 0.6 * smoothstep(0.1, 1.0, lead) - _Lobes * 0.4 * smoothstep(0.4, 1.0, -lead));
                float3 u = normalize(cross(m, abs(m.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 v = cross(m, u);
                [unroll] for (int k = 0; k < 2; k++)
                {
                    float a = f.t * (0.35 + 0.2 * k) + f.seed * 3.0 + k * 3.1;
                    float3 ld = normalize(m + 0.8 * (cos(a) * u + sin(a) * v));
                    float grow = 0.5 + 0.5 * sin(f.t * 0.9 + k * 2.3 + f.seed);
                    h += mv * _Lobes * grow * pow(saturate(dot(d, ld)), 12.0);
                }
            }
            return h;
        }

        // Full shape (reach frame, radii). mouth: 1 in the mouth; tendril: lip and wisps.
        float3 Shape(Frame f, float3 d, out float lump, out float mouth, out float tendril)
        {
            mouth = 0.0;
            tendril = 0.0;
            float u = acos(clamp(d.z, -1.0, 1.0)) / CAP_ANGLE;
            if (u >= 1.0) return d * (1.0 + Membrane(f, d, lump));

            float phi = atan2(d.y, d.x);
            float2 radial = float2(cos(phi), sin(phi));
            float e = f.ext, rt = f.tip;
            float zTop = 1.0 + e, zRim = cos(CAP_ANGLE), D = zTop - zRim;

            // Height along the cap: like the sphere at rest, tip hemisphere + shaft when stretched.
            float k = smoothstep(0.02, 0.35, e);
            float dzRest = D * (1.0 - cos(u * HALF_PI));
            float tipD = min(rt, D);
            float dzReach = u < TIP_SPLIT ? tipD * (1.0 - cos(u / TIP_SPLIT * HALF_PI))
                                          : tipD + (u - TIP_SPLIT) / (1.0 - TIP_SPLIT) * (D - tipD);
            float z = zTop - lerp(dzRest, dzReach, k);

            // The finger: a round-tipped cone, lumpy in section and flowing, kept inside the cap's rim.
            float c = zTop - rt;
            float rf = z > c ? sqrt(max(rt * rt - (z - c) * (z - c), 0.0)) : rt + (c - z) * 0.2;
            rf *= 1.0 + 0.3 * (Noise(float3(radial * 1.3, z * 2.2 - f.t * 0.35) + f.seed) - 0.5);
            rf = min(rf, sin(CAP_ANGLE) - FILLET - 0.02);
            // Smooth max with the body's section: the finger grows out of it with a fillet.
            float rb = sqrt(max(1.0 - z * z, 0.0));
            float hk = saturate((FILLET - abs(rf - rb)) / FILLET) * saturate(rb * 8.0);
            float rho = max(rf, rb) + hk * hk * FILLET * 0.25;
            float fingerW = saturate(0.5 + 0.5 * (rf - rb) / FILLET);

            // It meanders, bowing and snaking, pinned at both ends so the mouth stays on the spot.
            float along = saturate((z - zRim) / max(D, 1e-3));
            float2 bend = (f.bend1 * sin(PI * along) + f.bend2 * 0.6 * sin(TWO_PI * along)) * 0.3 * e * k;

            float3 p = float3(radial * rho + bend, z);
            float3 axisPoint = float3(bend, clamp(z, 0.0, c));
            float3 out1 = normalize(lerp(normalize(p), normalize(p - axisPoint + 1e-5), fingerW));
            float h = Membrane(f, p, lump) * (1.0 - 0.5 * fingerW);

            // The mouth: an irregular, flowing opening; its rim wanders round and breathes.
            float rim = MOUTH_RIM * (1.0 + 0.7 * (Noise(float3(radial * 1.6, f.t * 0.25) + f.seed * 1.7) - 0.5));
            float cup = 1.0 - saturate(u / rim);
            mouth = smoothstep(0.0, 0.3, cup);
            float3 axis = normalize(lerp(d, float3(0, 0, 1), k));
            p -= axis * _Cup * rt * cup * (2.0 - cup) * (0.35 + 0.65 * f.hunger);

            // Its lip, ruffled and thicker in places.
            float lipN = Noise(float3(radial * 2.4, f.t * 0.4) + f.seed * 3.1);
            float lip = exp(-pow((u - rim) / 0.06, 2.0)) * (0.4 + 1.2 * lipN);
            h += _Lip * rt * lip * (0.4 + 0.6 * f.hunger);

            // A few wisps drift round the lip, growing and shrinking, reaching forward.
            float wn = Noise(float3(radial * _Tendrils, f.t * 0.3) + f.seed * 5.3);
            float wisp = pow(saturate((wn - 0.5) / 0.5), 1.5) * exp(-pow((u - rim - 0.07) / 0.08, 2.0));
            float2 swirl = float2(-radial.y, radial.x) * (Noise(float3(radial * 2.0, f.t * 0.5) + 9.1) - 0.5);
            float3 wispDir = normalize(out1 + axis * 1.1 + float3(swirl, 0.0));
            p += out1 * h + wispDir * _TendrilLength * wisp * (0.3 + 0.7 * f.hunger) * (1.0 - f.wrap);
            tendril = saturate(lip * 0.6 + wisp * 2.0);

            // Wrapping: the lips flow forward round the prey (centred on the tip) until they meet in front,
            // an outer layer over it and an inner one hugging it. The lips don't all move at the same pace.
            if (f.wrap > 1e-3 && u < TIP_SPLIT)
            {
                float v = u / TIP_SPLIT;
                float pr = max(f.prey, 0.04);
                float ro = max(pr * 1.3, rt * 1.05), ri = pr * 1.04;
                float pace = 0.8 + 0.4 * Noise(float3(radial * 1.8, f.t * 0.6) + f.seed * 2.2);
                float front = lerp(PI * 0.6, 0.03, saturate(f.wrap * pace));
                float back = PI - asin(saturate(rt / ro));
                float beta = v < 0.5 ? lerp(PI, front, v / 0.5) : lerp(front, back, (v - 0.5) / 0.5);
                float r = lerp(ri, ro, smoothstep(0.35, 0.65, v)) * (1.0 + 0.06 * (lipN - 0.5));
                float3 wrapped = float3(0, 0, zTop) + r * float3(radial * sin(beta), cos(beta));
                p = lerp(p, wrapped, f.wrap * (1.0 - smoothstep(0.7, 1.0, v)));
                mouth *= 1.0 - f.wrap * 0.6;
            }
            return p;
        }

        struct Surfel { float3 positionWS, normalWS, bodyPos; float lump, mouth, tendril; };

        float3 Place(Instance inst, Frame f, float3 p)
        {
            float3 w = inst.positionRadius.xyz + ToWorld(f, p) * inst.positionRadius.w;
            return w + RippleFieldOffset(w); // rides impact ripples like a cell
        }

        Surfel Evaluate(Attributes v)
        {
            Instance inst = _Cells[_InstanceOffset + v.instanceID];
            Frame f = MakeFrame(inst);
            float3 d = normalize(v.positionOS.xyz);

            Surfel o;
            float3 p = Shape(f, d, o.lump, o.mouth, o.tendril);
            // Normal from two nearby directions: Cross(t1, t2) = d, so the cross faces out.
            float3 t1 = normalize(cross(d, abs(d.z) < 0.99 ? float3(0, 0, 1) : float3(1, 0, 0)));
            float3 t2 = cross(d, t1);
            const float E = 0.01;
            float a, b, c;
            float3 w0 = Place(inst, f, p);
            float3 w1 = Place(inst, f, Shape(f, normalize(d + t1 * E), a, b, c));
            float3 w2 = Place(inst, f, Shape(f, normalize(d + t2 * E), a, b, c));
            float3 n = cross(w1 - w0, w2 - w0);

            o.positionWS = w0;
            o.normalWS = dot(n, n) > 1e-14 ? normalize(n) : ToWorld(f, d);
            o.bodyPos = ToBody(f, p);
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 bodyPos    : TEXCOORD2;
                float3 marks      : TEXCOORD3; // ruffle crest, mouth, lip/wisps
                float  fog        : TEXCOORD4;
                nointerpolation uint id : TEXCOORD5;
            };

            Varyings vert(Attributes v)
            {
                Surfel s = Evaluate(v);
                Varyings o;
                o.positionWS = s.positionWS;
                o.positionCS = TransformWorldToHClip(s.positionWS);
                o.normalWS = s.normalWS;
                o.bodyPos = s.bodyPos;
                o.marks = float3(s.lump, s.mouth, s.tendril);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.id = _InstanceOffset + v.instanceID;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                Instance inst = _Cells[i.id];
                float seed = inst.motion.w, hunger = inst.state.x;
                float crest = saturate(i.marks.x), mouth = saturate(i.marks.y), tendril = saturate(i.marks.z);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));

                // Fine membrane creases per pixel (bump from the ridged noise's own gradient), and a soft mottle.
                float mottle = Noise(i.bodyPos * 2.3 + seed);
                float fine = 0.0;
                if (_Detail > 0.5)
                {
                    float t = _Time.y * _Speed + seed * 7.0;
                    float3 g;
                    fine = Ruffle(i.bodyPos * _FineScale + float3(t * 0.08, 0.0, -t * 0.05) + seed * 1.3, seed + 5.0, g);
                    float3 grad = Rotate(inst.rotation, g) * (_FineLumps * _FineScale * (1.0 - mouth));
                    n = normalize(n - (grad - n * dot(grad, n)));
                }
                float cavity = saturate(0.35 * (1.0 - crest) * (1.0 - fine) + 0.4 * (mottle - 0.5));

                Light l = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float atten = l.shadowAttenuation * l.distanceAttenuation;
                float3 albedo = lerp(_Albedo.rgb, _Cavity.rgb, cavity);
                albedo = lerp(albedo, _MouthColor.rgb, saturate(mouth * 0.7 + tendril * 0.3));

                float nl = dot(n, l.direction);
                float wrap = saturate((nl + 0.6) / 1.6);                                  // soft, fleshy
                float thin = 0.6 + 0.8 * max(crest, fine);                                  // folds let light through
                float back = pow(saturate(dot(v, -l.direction)), 3.0) * _Translucency * thin * (1.0 - saturate(nl));
                float3 h = normalize(l.direction + v);
                float spec = pow(saturate(dot(n, h)), exp2(10.0 * _Smoothness + 1.0)) * _Smoothness * (1.0 - cavity * 0.6);
                float fres = pow(1.0 - saturate(dot(n, v)), 3.0);

                float ao = 1.0 - cavity * 0.4;
                float3 col = albedo * (SampleSH(n) * ao + l.color * (wrap * atten + back));
                col += l.color * spec * atten;
                col += _Rim.rgb * fres * 0.5 * ao;
                // The mouth glows (brighter hungry, a slow pulse) so you can see where it's aimed.
                float pulse = 0.75 + 0.25 * sin(_Time.y * 3.0 + seed);
                col += _MouthColor.rgb * _MouthGlow * (mouth * 0.8 + tendril * 0.4) * (0.25 + 0.75 * hunger) * pulse;
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;

            float4 vert(Attributes v) : SV_POSITION
            {
                Surfel s = Evaluate(v);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 dir = normalize(_LightPosition - s.positionWS);
            #else
                float3 dir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(s.positionWS, s.normalWS, dir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            float4 vert(Attributes v) : SV_POSITION { return TransformWorldToHClip(Evaluate(v).positionWS); }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Surfel s = Evaluate(v);
                Varyings o;
                o.positionCS = TransformWorldToHClip(s.positionWS);
                o.normalWS = s.normalWS;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
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
}

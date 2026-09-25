#ifndef WHITE_BLOOD_CELL_INCLUDED
#define WHITE_BLOOD_CELL_INCLUDED

// The white blood cell's shape (Custom/WhiteBloodCell draws it, WhiteBloodCellBake.compute bakes the near
// ones once a frame). Needs PI / HALF_PI / TWO_PI (SRP Common or URP Core) and ShrinkWrap.hlsl first.
// WBC_TIME: the animation clock; _Time.y in the draw shader, set from Time.time for the compute (the same
// clock in play mode). The shape is in the reach frame, in body radii; see WhiteBloodCell.shader's header.

#ifndef WBC_TIME
#define WBC_TIME _Time.y
#endif

#define CAP_ANGLE 0.7   // WhiteBloodCellMesh.CapAngle
#define TIP_SPLIT 0.3   // share of the cap that rounds the tip; the rest is the arm
#define MOUTH_RIM 0.22  // where the lip rings the mouth, in cap units

// Its own settings: outside UnityPerMaterial (BloodCellCore owns that), like RopeBlood's _BeadLumps.
float4 _MouthColor, _MouthDeepColor;
float _MouthGlow, _MouthWet, _MouthFolds, _MouthFoldDepth, _Lumps, _LumpScale, _FineLumps, _FineScale;
float _Wobble, _Flow, _Lobes, _Spikes, _SpikeScale, _SpikeWidth, _SpikeSharpness, _SpikeFlow;
float _Pull, _Flare, _Lag, _Peristalsis, _Cup, _Lip, _Tendrils, _TendrilLength, _Speed;

// merge: MergeShape; side: the reach frame's x axis (world), carried along as the arm swings (WhiteBloodCell.Side)
struct Instance { float4 positionRadius, rotation, reach, motion, state, sway, extra, merge, side; };
StructuredBuffer<Instance> _Cells;
uint _InstanceOffset;
float _LodDetail; // 1 near (ruffles and spikes in the geometry, creases per pixel), 0 far



// ---------------- noise ----------------

float WbcSq(float x) { return x * x; } // pow(x, 2) is undefined for x < 0

float WbcHash(float3 p)
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
    float a = WbcHash(i), b = WbcHash(i + float3(1, 0, 0)), c = WbcHash(i + float3(0, 1, 0)), d = WbcHash(i + float3(1, 1, 0));
    float e = WbcHash(i + float3(0, 0, 1)), f = WbcHash(i + float3(1, 0, 1)), g = WbcHash(i + float3(0, 1, 1)), h = WbcHash(i + float3(1, 1, 1));
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
    float a = lerp(WbcHash(i), WbcHash(i + float3(1, 0, 0)), f.x);
    float b = lerp(WbcHash(i + float3(0, 1, 0)), WbcHash(i + float3(1, 1, 0)), f.x);
    float c = lerp(WbcHash(i + float3(0, 0, 1)), WbcHash(i + float3(1, 0, 1)), f.x);
    float d = lerp(WbcHash(i + float3(0, 1, 1)), WbcHash(i + float3(1, 1, 1)), f.x);
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

// Thorns: a jittered point per lattice cell (Worley), each a pointed cone (linear to the tip, hollowed by
// _SpikeSharpness) that grows and retracts on its own slow beat. 0..1 on the unit body sphere.
// Only the 2x2x2 cells nearest the point: a spike is at most half a cell wide (_SpikeWidth <= 0.5), so one
// further out can't reach it. 8 cells, not 27.
float Spikes(float3 q, float t, float seed)
{
    float3 x = q * _SpikeScale + seed * 0.37;
    float3 i = floor(x - 0.5);
    float width = min(_SpikeWidth, 0.5), best = 0.0;
    [unroll] for (int a = 0; a <= 1; a++)
    [unroll] for (int b = 0; b <= 1; b++)
    [unroll] for (int c = 0; c <= 1; c++)
    {
        float3 cell = i + float3(a, b, c);
        float3 h = float3(WbcHash(cell), WbcHash(cell + 17.1), WbcHash(cell + 41.7));
        float life = 0.5 + 0.5 * sin(t * (0.3 + 0.45 * h.x) + h.y * 6.2832);
        float len = (0.35 + 0.65 * h.z) * smoothstep(0.05, 0.75, life);
        float sp = pow(saturate(1.0 - length(x - cell - h) / width), _SpikeSharpness) * len;
        best = max(best, sp);
    }
    return best;
}

// ---------------- frames ----------------

float3 WbcRotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }
float3 WbcInvRotate(float4 q, float3 v) { return WbcRotate(float4(-q.xyz, q.w), v); }

struct Frame
{
    float3 x, y, z;      // reach frame in world (z = toward the mouth)
    float4 body;         // body rotation
    float3 move, sway;   // crawl (0..1 long) and spot velocity (radii/s), both in the reach frame
    float3 lobe0, lobe1; // the leading edge's two feeling lobes (reach frame, unit)
    float2 bend1, bend2; // the arm's meander (one bow, one S)
    float4 blob;         // merging (WhiteBloodCell.MergeShape): radius, centre's distance along +z, neck softness, height squash
    float seed, hunger, wrap, wrapW, tip, armR, prey, ext, tension, detail, gape, squeeze, slot, radius, t;
};

float3 ToWorld(Frame f, float3 v) { return f.x * v.x + f.y * v.y + f.z * v.z; }
float3 ToBody(Frame f, float3 v) { return WbcInvRotate(f.body, ToWorld(f, v)); }
float3 FromBody(Frame f, float3 v) { float3 w = WbcRotate(f.body, v); return float3(dot(w, f.x), dot(w, f.y), dot(w, f.z)); }

// The reach frame's x axis: the side the CPU carries along with the arm (parallel transport), so everything laid
// out round the arm (wisps, lip, meander, mouth folds) turns with it smoothly. Picking it from a fixed body axis
// flipped it by 90° whenever the arm swung near that axis, and all of those jumped.
float3 ReachSide(Instance inst, float3 z)
{
    float3 s = inst.side.xyz - z * dot(inst.side.xyz, z);
    if (dot(s, s) < 1e-6) s = cross(z, abs(z.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0));
    return normalize(s);
}

// The mouth's radius (body radii) as the shape draws it: gaping opens it for the catch, wrapping necks it to the catch.
float TipRadius(Instance inst)
{
    float prey = max(inst.state.w, 0.04), gape = inst.extra.y;
    float open = lerp(inst.state.z, max(inst.state.z, prey * 1.4), gape) * (1.0 + 0.3 * gape);
    return lerp(open, prey * 0.85, smoothstep(0.0, 0.25, inst.state.y));
}

Frame MakeFrame(Instance inst)
{
    Frame f;
    f.body = inst.rotation;
    f.z = normalize(inst.reach.xyz);
    f.x = ReachSide(inst, f.z);
    f.y = cross(f.z, f.x);
    float3 m = inst.motion.xyz, w = inst.sway.xyz;
    f.move = float3(dot(m, f.x), dot(m, f.y), dot(m, f.z));
    f.sway = float3(dot(w, f.x), dot(w, f.y), dot(w, f.z));
    f.tension = inst.sway.w;
    f.detail = _LodDetail * inst.extra.x;
    f.gape = inst.extra.y;
    f.squeeze = inst.extra.z;
    f.slot = inst.extra.w;
    f.blob = inst.merge;
    f.radius = max(inst.positionRadius.w, 1e-3);
    f.seed = inst.motion.w;
    f.hunger = inst.state.x;
    f.wrap = inst.state.y;
    f.prey = max(inst.state.w, 0.04);
    f.wrapW = smoothstep(0.0, 0.25, f.wrap);
    // Gaping, the mouth opens wide enough for the catch and more; wrapping, the arm narrows to a throat just
    // behind it, so the catch sits in a thin skin at the end instead of a ball.
    float open = lerp(inst.state.z, max(inst.state.z, f.prey * 1.4), f.gape) * (1.0 + 0.3 * f.gape);
    f.tip = TipRadius(inst);
    f.armR = open; // the arm keeps its girth; only its end necks down to the throat
    f.ext = inst.reach.w;
    f.t = WBC_TIME * _Speed + f.seed * 7.0;
    float s = f.seed * 1.37;
    f.bend1 = float2(Noise(float3(f.t * 0.23, s, 1.1)), Noise(float3(s, f.t * 0.21, 4.7))) - 0.5;
    f.bend2 = float2(Noise(float3(f.t * 0.31, s, 8.3)), Noise(float3(s, f.t * 0.27, 2.9))) - 0.5;
    // Lobes feel about round the way it crawls: the lead tilted by a slowly wandering body-frame vector (only its
    // part across the lead). Orbiting them round a basis built from the lead flipped it whenever the lead crossed
    // that basis' reference axis, and the lobes jumped.
    float mv = length(f.move);
    float3 lead = mv > 1e-4 ? f.move / mv : float3(0, 0, 1);
    float3 j0 = FromBody(f, float3(Noise(float3(f.t * 0.3, s, 3.3)), Noise(float3(s, f.t * 0.3, 6.1)), Noise(float3(5.2, s, f.t * 0.3))) - 0.5);
    float3 j1 = FromBody(f, float3(Noise(float3(f.t * 0.4, s, 9.4)), Noise(float3(s, f.t * 0.4, 1.7)), Noise(float3(7.9, s, f.t * 0.4))) - 0.5);
    f.lobe0 = normalize(lead + 2.4 * (j0 - lead * dot(j0, lead)));
    f.lobe1 = normalize(lead + 2.4 * (j1 - lead * dot(j1, lead)));
    return f;
}

// ---------------- shape ----------------

// The catch's hull (radii) along reach-frame direction 'dir' from the mouth's centre (ShrinkWrap), a ball of the
// catch's size when it has no map. Skin, not shrink film: over a patch ~0.18 rad across, anything sticking out
// more than a little past the lowest of five taps (a leg, a crystal's point) is cut down to it, then averaged; a
// tap that missed (no surface that way) counts as that lowest. One far tap made a sharp spike that dragged the
// skin out into a long stretched needle.
float HullTap(float h, float lo, float cap) { return h > 1e-5 ? min(h, cap) : lo; }

float Hull(Frame f, float3 dir)
{
    if (f.slot < 0.0) return f.prey;
    float3 w = ToWorld(f, dir);
    // Taps round the arm's own axis (continuous over the whole skin but its two poles, where they just turn about it).
    // A world reference axis flipped the taps wherever |w.y| crossed 0.9: a crease that slid over the skin as the arm swung.
    float3 around = float3(-dir.y, dir.x, 0.0);
    float al = dot(around, around);
    around = al > 1e-10 ? around * rsqrt(max(al, 1e-10)) : float3(0, 1, 0);
    float3 ta = ToWorld(f, around), tb = cross(w, ta);
    const float S = 0.18;
    float h0 = ShrinkWrapDistance(w, f.slot);
    float h1 = ShrinkWrapDistance(normalize(w + ta * S), f.slot), h2 = ShrinkWrapDistance(normalize(w - ta * S), f.slot);
    float h3 = ShrinkWrapDistance(normalize(w + tb * S), f.slot), h4 = ShrinkWrapDistance(normalize(w - tb * S), f.slot);
    const float MISS = 1e9;
    float lo = min(h0 > 1e-5 ? h0 : MISS, min(min(h1 > 1e-5 ? h1 : MISS, h2 > 1e-5 ? h2 : MISS), min(h3 > 1e-5 ? h3 : MISS, h4 > 1e-5 ? h4 : MISS)));
    if (lo >= MISS) return f.prey;
    float cap = lo + 0.15 * f.prey * f.radius;
    float h = 0.4 * HullTap(h0, lo, cap) + 0.15 * (HullTap(h1, lo, cap) + HullTap(h2, lo, cap) + HullTap(h3, lo, cap) + HullTap(h4, lo, cap));
    return clamp(h / f.radius, f.prey * 0.3, f.prey * 1.6);
}

// The membrane's offset (reach frame, radii) at the body-frame direction q it started from; d is that
// direction in the reach frame, n the way out of the shape there. lump: ruffle / spike crest 0..1.
float3 Membrane(Frame f, float3 q, float3 d, float3 n, out float lump)
{
    float t = f.t * _Flow;
    float3 flow = float3(t * 0.05, -t * 0.03, t * 0.04);
    float h = _Wobble * (Noise(q * 1.5 + float3(0.0, t * 0.12, t * 0.07) + f.seed) - 0.5) * 2.0
            + _Wobble * 0.5 * (Noise(q * 3.1 - flow * 2.0 + f.seed * 2.3) - 0.5) * 2.0;
    // Slow swells rolling round the body.
    h += _Wobble * 0.6 * sin(dot(q, float3(0.8, 0.5, -0.33)) * 5.0 - t * 1.1 + Noise(q * 1.2 + f.seed) * 4.0);

    lump = 0.0;
    float3 side = 0.0;
    if (f.detail > 0.01)
    {
        float3 g;
        float rf = Ruffle(q * _LumpScale + flow + f.seed, f.seed, g);
        float sp = Spikes(q, t, f.seed) * f.detail;
        float hs = _Spikes * sp;
        h += _Lumps * rf * f.detail + hs;
        lump = max(rf * f.detail, sp);

        // Spikes curl along a drifting flow and trail behind the crawl; the tip bends most (~ height^2).
        if (sp > 1e-3)
        {
            float3 qf = q * 1.3 + f.seed;
            float3 fl = float3(Noise(qf + float3(t * 0.15, 0, 0)), Noise(qf + float3(7.1, t * 0.13, 0)), Noise(qf + float3(3.3, 0, t * 0.17))) - 0.5;
            float3 lean = FromBody(f, fl * 2.0) - f.move * 0.8;
            lean -= n * dot(lean, n);
            side = lean * (_SpikeFlow * hs * sp);
        }
    }

    // Amoeboid crawl: the leading edge bulges and feels about with two lobes, the tail tapers.
    float mv = length(f.move);
    if (mv > 1e-3)
    {
        float3 m = f.move / mv;
        float lead = dot(d, m);
        h += mv * (_Lobes * 0.6 * smoothstep(0.1, 1.0, lead) - _Lobes * 0.4 * smoothstep(0.4, 1.0, -lead));
        [unroll] for (int k = 0; k < 2; k++)
        {
            float grow = 0.5 + 0.5 * sin(f.t * 0.9 + k * 2.3 + f.seed);
            h += mv * _Lobes * grow * pow(saturate(dot(d, k == 0 ? f.lobe0 : f.lobe1)), 12.0);
        }
    }
    return n * h + side;
}

// Full shape (reach frame, radii). mouth: 1 in the mouth; tendril: lip and wisps.
// Material coordinates (body frame, radii) for the membrane and the pixel lumps: never where a point started on
// the body (the arm and the wrap stretch that many times over: smeared streaks), but where the surface really is,
// so the pattern keeps its size everywhere. map: fixed to the body (and the arm's root: the arm slides out through
// it, as a pseudopod grows); mapTip.xyz: fixed to the tip (the mouth and wrap keep their pattern as the arm
// reaches), mapTip.w: how much of it, 1 on the tip, fading out along the arm toward the body. Both are sampled
// where they overlap and the results blended, never the coordinates (blending those would stretch again).
float3 CellShape(Frame f, float3 d, out float lump, out float mouth, out float tendril, out float3 map, out float4 mapTip)
{
    mouth = 0.0;
    tendril = 0.0;
    lump = 0.0;
    map = 0.0;
    mapTip = 0.0;
    float e = f.ext, rt = f.tip, k = smoothstep(0.0, 0.3, e);
    float zRim = cos(CAP_ANGLE), rRim = sin(CAP_ANGLE);
    float P = _Pull * min(e, 1.5); // the body's front slides after the arm and narrows a little
    float u = acos(clamp(d.z, -1.0, 1.0)) / CAP_ANGLE;

    if (u >= 1.0)
    {
        float w = smoothstep(-0.6, zRim, d.z);
        float3 body = float3(d.xy * (1.0 - 0.25 * P * w), d.z + P * w);
        map = ToBody(f, body);
        return body + Membrane(f, map, d, d, lump);
    }

    // The mouth's centre is the mesh's pole, where d.xy = 0: atan2(0, 0) is NaN on D3D, which made the pole vertex
    // NaN and dropped the whole fan round it (a hole in the middle of the mouth). Every pole copy gets the same side.
    float dl = length(d.xy);
    float2 radial = dl > 1e-6 ? d.xy / dl : float2(1.0, 0.0);
    float phi = atan2(radial.y, radial.x);
    float zB = zRim + P, rB = rRim * (1.0 - 0.25 * P), zTop = 1.0 + e, L = zTop - zB;
    // Barely out: the cap drawn up into a dome.
    float3 rest = float3(d.xy * (rB / rRim), zB + (d.z - zRim) / (1.0 - zRim) * L);

    // Out: a round tip, then a tube flaring back into the body (meets it exactly at the cap's rim).
    float tipD = min(rt, L * 0.6), r, z;
    if (f.wrapW > 0.0) tipD = lerp(tipD, min(Hull(f, float3(0, 0, -1)) * 0.7, L * 0.9), f.wrapW); // throat just behind it
    float3 armOut;
    if (u < TIP_SPLIT)
    {
        float a = u / TIP_SPLIT * HALF_PI;
        r = rt * sin(a);
        z = zTop - tipD * (1.0 - cos(a));
        armOut = float3(radial * sin(a), cos(a));
    }
    else
    {
        float s = (u - TIP_SPLIT) / (1.0 - TIP_SPLIT); // 0 under the mouth .. 1 at the body
        z = lerp(zTop - tipD, zB, s);
        float flare = pow(saturate(s), _Flare);
        r = lerp(lerp(rt, f.armR, smoothstep(0.0, 0.35, s)), rB, flare);
        float mid = sin(PI * s) * (1.0 - flare);
        r *= 1.0 - (0.15 + 0.2 * f.tension) * mid;                                // a waist, thinner pulled taut
        r *= 1.0 + _Peristalsis * f.tension * sin(s * 14.0 - f.t * 7.0) * mid;    // swallowing waves down the arm
        r *= 1.0 + 0.25 * (Noise(float3(radial * 1.3, s * 3.0 - f.t * 0.5) + f.seed) - 0.5) * (1.0 - flare);
        armOut = float3(radial, 0.0);
    }

    // It bows behind the way the spot moves and meanders when slack, pinned at both ends.
    float along = saturate((z - zB) / max(L, 1e-3));
    float bow = sin(PI * along);
    float2 bend = (f.bend1 * bow + f.bend2 * 0.6 * sin(TWO_PI * along)) * 0.35 * e * (1.0 - 0.7 * f.tension);
    float2 lag = -f.sway.xy * _Lag * bow;
    float lagLen = length(lag), lagMax = 0.6 * e + 0.05;
    if (lagLen > lagMax) lag *= lagMax / lagLen;
    float3 smooth = lerp(rest, float3(radial * r, z), k); // unbent: what the material maps follow
    float3 p = smooth + float3(bend + lag, 0.0) * k;

    // The mouth: an irregular, flowing opening; its rim wanders round and breathes.
    float rim = MOUTH_RIM * (1.0 + 0.7 * (Noise(float3(radial * 1.6, f.t * 0.25) + f.seed * 1.7) - 0.5));
    float cup = 1.0 - saturate(u / rim);
    mouth = smoothstep(0.0, 0.3, cup);
    float3 axis = normalize(lerp(d, float3(0, 0, 1), k));
    float3 cupped = axis * _Cup * rt * cup * (2.0 - cup) * (0.35 + 0.65 * f.hunger + 0.6 * f.gape);
    p -= cupped;
    smooth -= cupped;

    // Material maps (see above): the body's, and the tip's (shifted back by the reach, so a retracted arm is the
    // same as the body's). Out of the arm's reach they're the same, so the tip's is only used once it's out.
    map = smooth;
    float3 tipMap = smooth - float3(0.0, 0.0, e);
    float anchor = (u < TIP_SPLIT ? 1.0 : 1.0 - smoothstep(0.25, 0.8, (u - TIP_SPLIT) / (1.0 - TIP_SPLIT))) * saturate(e * 10.0);

    float onArm = k * (1.0 - smoothstep(0.7, 1.0, u));
    float3 out1 = normalize(lerp(d, armOut, onArm));
    float3 mem = 0.0;
    lump = 0.0;
    if (anchor < 0.999) mem = Membrane(f, ToBody(f, map), d, out1, lump);
    if (anchor > 0.001)
    {
        float lumpTip;
        float3 memTip = Membrane(f, ToBody(f, tipMap), d, out1, lumpTip);
        mem = lerp(mem, memTip, anchor);
        lump = lerp(lump, lumpTip, anchor);
    }
    mem *= 1.0 - 0.4 * onArm; // a little smoother drawn out

    // Its lip, ruffled and thicker in places.
    float lipN = Noise(float3(radial * 2.4, f.t * 0.4) + f.seed * 3.1);
    float lip = exp(-WbcSq((u - rim) / 0.05)) * (0.4 + 1.2 * lipN);
    float lipH = _Lip * rt * lip * (0.4 + 0.6 * f.hunger) * (1.0 + 1.2 * f.gape);
    // Gaping, the lips peel back and outward, ready to snap forward.
    p += (float3(radial, 0.0) * 0.8 - axis * 0.3) * rt * lip * f.gape * 0.35;

    // A few wisps drift round the lip, growing and shrinking, reaching forward.
    float wn = Noise(float3(radial * _Tendrils, f.t * 0.3) + f.seed * 5.3);
    float wisp = pow(saturate((wn - 0.5) / 0.5), 1.5) * exp(-WbcSq((u - rim - 0.06) / 0.07));
    float2 swirl = float2(-radial.y, radial.x) * (Noise(float3(radial * 2.0, f.t * 0.5) + 9.1) - 0.5);
    float3 wispDir = normalize(out1 + axis * 1.1 + float3(swirl, 0.0));
    p += mem + out1 * lipH + wispDir * _TendrilLength * wisp * (0.3 + 0.7 * f.hunger) * (1.0 - f.wrap);
    tendril = saturate(lip * 0.6 + wisp * 2.0);

    // Wrapping, as a phagocyte does: a thin skin creeps forward from the throat over the catch's real shape
    // (its hull, ShrinkWrap) and its rim closes in front. Along the tip (w: 0 mouth centre .. 1 throat):
    // - lining (w < WR): tucked just under the catch's uncovered front, so the catch itself shows there;
    // - lip (WR..WR+LB): rolls from the catch's surface out onto the skin, bulging forward: a rounded rim;
    // - skin (the rest): hull + a thin thickness, from the rim back to the throat ring, which it meets.
    // The rim doesn't close at the same pace all round; squeezing presses the skin in; shut, it puckers.
    if (f.wrap > 1e-3 && u < TIP_SPLIT)
    {
        float w = u / TIP_SPLIT, pr = f.prey;
        float back = atan2(rt, -tipD); // the throat ring, seen from the catch's centre
        float pace = 0.8 + 0.4 * Noise(float3(radial * 1.8, f.t * 0.6) + f.seed * 2.2);
        float closing = saturate(lerp(f.wrap * pace, 1.0, smoothstep(0.9, 1.0, f.wrap)));
        float front = back * 0.92 * (1.0 - closing); // the rim's angle from the catch's front: 0 = shut
        // The rings go where the surface is: the lining's share shrinks as the rim closes (a fixed 30% left the skin,
        // most of the catch, on a few stretched rings), the lip keeps a few, the skin takes the rest.
        const float LB = 0.16;
        float WR = 0.04 + 0.3 * saturate(front / max(back, 1e-3));
        float T = max(pr * 0.12, 0.01) * (1.0 - 0.35 * f.squeeze), press = 1.0 - 0.04 * f.squeeze;

        float beta, radius, inner;
        if (w < WR)
        {
            float s = w / WR;
            beta = front * s;
            // Just under the catch's front, not a pit: diving to 0.4 of the hull at the centre was a hole seen through it.
            radius = Hull(f, float3(radial * sin(beta), cos(beta))) * lerp(0.82, 0.95, s);
            inner = 1.0;
        }
        else if (w < WR + LB)
        {
            float a = (w - WR) / LB * PI, roll = 0.5 - 0.5 * cos(a);
            beta = max(front - T * 1.2 / pr * sin(a), 0.0);
            float h = Hull(f, float3(radial * sin(beta), cos(beta)));
            radius = lerp(h * 0.95, h * press + T * 1.3, roll);
            inner = 1.0 - roll;
        }
        else
        {
            float s = (w - WR - LB) / (1.0 - WR - LB);
            beta = lerp(front, back, s);
            radius = Hull(f, float3(radial * sin(beta), cos(beta))) * press + T * (1.0 + 0.3 * (1.0 - smoothstep(0.0, 0.25, s)));
            inner = 0.0;
        }
        float shut = smoothstep(0.75, 1.0, closing);
        radius *= 1.0 + 0.08 * shut * exp(-WbcSq((w - WR - LB) / 0.12)) * sin(phi * 7.0 + f.seed * 3.0 + lipN * 2.0);
        float3 wrapped = float3(0, 0, zTop) + radius * float3(radial * sin(beta), cos(beta) * (1.0 + 0.06 * f.squeeze));
        float weight = f.wrapW * (1.0 - smoothstep(0.85, 1.0, w)); // hands over to the throat ring at w = 1
        p = lerp(p, wrapped, weight);
        tipMap = lerp(tipMap, wrapped - float3(0.0, 0.0, e), weight); // the skin maps where it lies on the catch
        mouth = lerp(mouth, inner, weight);
        tendril = lerp(tendril, exp(-WbcSq((w - WR - LB * 0.5) / 0.1)), weight);
    }
    map = ToBody(f, map);
    mapTip = float4(ToBody(f, tipMap), anchor);
    return p;
}

// ---------------- merging ----------------

// Polynomial smooth minimum: the union of two distance fields with a fillet k wide (the neck between two drops).
float WbcSMin(float a, float b, float k)
{
    float h = max(k - abs(a - b), 0.0) / k;
    return min(a, b) - h * h * k * 0.25;
}

// The catch as a blob (an ellipsoid squashed along z, volume kept), distance outside it (radii, near enough).
float Blob(Frame f, float3 x)
{
    float3 r = f.blob.x * float3(rsqrt(f.blob.w), rsqrt(f.blob.w), f.blob.w);
    float3 q = (x - float3(0.0, 0.0, f.blob.y)) / r;
    return (length(q) - 1.0) * min(r.x, r.z);
}

// Merging, like a drop into a pool: the surface is the smooth union of the body as it is and the blob where the
// catch sinks in, found along each vertex's ray from the centre (sphere traced inward from outside both, so it
// lands on the outermost surface). The neck widens as the blob's softness grows; the blob sits a little inside the
// catch, so the catch shows through the top until it sinks under. Uses the dense cap round the reach (the catch
// is aimed at), not just the tip's few rows. Only near the blob: elsewhere one distance and out.
void MergeInto(Frame f, inout float3 p, inout float lump, inout float mouth, inout float tendril, inout float3 map, inout float4 mapTip)
{
    float k = max(f.blob.z, 1e-3), body = length(p);
    if (body < 1e-4 || Blob(f, p) > k) return;
    float3 dir = p / body;
    float t = max(body, f.blob.y + f.blob.x * max(rsqrt(f.blob.w), f.blob.w)) + k;
    [loop] for (int i = 0; i < 16; i++)
    {
        float F = WbcSMin(t - body, Blob(f, dir * t), k);
        t -= F;
        if (F < 1e-4) break;
    }
    float grow = t - body;
    if (grow <= 0.0) return;
    p = dir * t;
    // The maps shift with the surface (so it maps where it really is, no stretch); the mouth and crests fade under.
    float3 shift = ToBody(f, dir * grow);
    map += shift;
    mapTip.xyz += shift;
    float cover = saturate(grow / (0.3 * f.blob.x + 1e-4));
    lump *= 1.0 - cover;
    mouth *= 1.0 - cover;
    tendril *= 1.0 - cover;
}

// Full shape (reach frame, radii): the cell, merged with what it's eating. See CellShape for the outputs.
float3 Shape(Frame f, float3 d, out float lump, out float mouth, out float tendril, out float3 map, out float4 mapTip)
{
    float3 p = CellShape(f, d, lump, mouth, tendril, map, mapTip);
    if (f.blob.x > 0.0) MergeInto(f, p, lump, mouth, tendril, map, mapTip);
    return p;
}

#endif

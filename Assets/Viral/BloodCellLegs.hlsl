#ifndef BLOOD_CELL_LEGS_INCLUDED
#define BLOOD_CELL_LEGS_INCLUDED

// GPU legs for Custom/BloodCellLegs. Every leg of every SpiderLegWalker is one
// instance of a shared tube mesh; the vertex stage rebuilds it from the leg's
// record in _Legs (written by LegSimulation.compute; a draw's legs start at
// _LegBase): a cubic Bezier root->tip, rings around
// it framed by the leg's side axis, radius, writhing wave and ground clearance.
// The same shape SpiderLegWalker used to build on the CPU, then the cell shader's
// own displacement, ripple riding and lighting on top.
//
// Template mesh vertex: shape = (t along the leg, cos, sin around it), cap.x =
// -1 root cap, +1 tip cap, 0 ring.

#include "LegData.hlsl"

#if SHADER_TARGET >= 45
StructuredBuffer<LegData> _Legs;
#endif
int _LegBase; // this draw's first leg in _Legs (per draw, from LegRenderer's property block)

void LegSetup() {} // procedural instancing hook; nothing per instance to set

struct LegAttributes
{
    float3 shape : POSITION;
    float2 cap   : TEXCOORD0;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct LegSample
{
    float3 positionWS; // displaced
    float3 normalWS;   // geometric
    float3 mapPos;     // noise coordinate
    float  fade;
    float4 height;     // SurfaceHeight at mapPos, zeroed inside the focus sweep (plain there)
};

// Focus sweep (ScreenInvertTest sets it globally; w = 0 when not sweeping). Inside its circle
// legs go plain like the cells -- no lumps in shape or normals -- so the sweep's outlines show
// the legs' silhouettes, not their texture. Same circle as InvertSweepCover in BloodCellCore;
// declared here because legs never compile INVERT_BACKFACES.
#if !defined(INVERT_BACKFACES)
float4 _InvertSweep;
#endif

float LegSweepCover(float3 positionWS)
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
    return distance(here, centre) < maxRadius * saturate(_InvertSweep.w) ? 1.0 : 0.0;
}

LegData FetchLeg(LegAttributes v)
{
    UNITY_SETUP_INSTANCE_ID(v);
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED) && SHADER_TARGET >= 45 // only drawn this way
    return _Legs[_LegBase + unity_InstanceID];
#else
    return (LegData)0;
#endif
}

float3 LegPerp(float3 n)
{
    return normalize(cross(n, abs(n.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
}

LegSample EvaluateLeg(LegAttributes v)
{
    LegData L = FetchLeg(v);

    float t = v.shape.x, u = 1.0 - t;
    float3 p0 = L.p0.xyz, p1 = L.p1.xyz, p2 = L.p2.xyz, p3 = L.p3.xyz;

    float3 c   = u * u * u * p0 + 3.0 * u * u * t * p1 + 3.0 * u * t * t * p2 + t * t * t * p3;
    float3 tan = 3.0 * u * u * (p1 - p0) + 6.0 * u * t * (p2 - p1) + 3.0 * t * t * (p3 - p2);
    float3 dir = SafeNormalize(p3 - p0);
    tan = dot(tan, tan) > 1e-8 ? normalize(tan) : dir;

    float3 side = L.side.xyz;
    float3 up   = cross(side, dir);
    float  env  = sin(t * PI);

    // Free (and wiggling) legs writhe.
    float wave = L.p2.w;
    if (wave > 0.0)
    {
        float wp = _Time.y * 6.5 + L.p3.w + t * PI * 2.4;
        c += side * (sin(wp) * wave * env) + up * (cos(wp * 0.73) * wave * env * 0.65);
    }

    // Taper root to tip, swelling mid-leg, and round off into the foot.
    float radius = lerp(L.p0.w, L.p1.w, t) * (1.0 + env * L.side.w) * lerp(1.0, 0.8, smoothstep(0.8, 1.0, t));

    // Analytic clearance against the foot's contact plane. The plane is infinite, so when the
    // leg reaches round an edge (hip behind the foot's plane: a foot on a cube's side, body on
    // top) clamping all of it would shove the upper leg sideways; then only the end near the
    // foot is kept clear.
    if (L.clampN.w > 0.5)
    {
        float hgt = dot(c - L.ground.xyz, L.clampN.xyz), req = radius + 0.02;
        float hipAbove = saturate(dot(p0 - L.ground.xyz, L.clampN.xyz) / max(req, 1e-4));
        float weight = lerp(t * t, 1.0, hipAbove);
        if (hgt < req)
            c += L.clampN.xyz * ((req - hgt) * weight);
    }

    // Ring frame from the leg's side axis: the curve bends in the plane it's
    // perpendicular to, so this neither twists nor flips.
    float3 ringSide = side - tan * dot(side, tan);
    ringSide = dot(ringSide, ringSide) > 1e-6 ? normalize(ringSide) : LegPerp(tan);
    float3 ringUp = cross(tan, ringSide);
    float3 o = ringSide * v.shape.y + ringUp * v.shape.z;

    // Caps: pushed out along the leg, so the foot ends in a rounded nub (and the root in one
    // inside the body) instead of a flat disc.
    float cap = v.cap.x;
    float3 baseWS = cap != 0.0 ? c + tan * (cap * radius * 0.9) : c + o * radius;

    LegSample s;
    s.normalWS = cap < 0.0 ? -tan : cap > 0.0 ? tan : o;
    s.mapPos   = baseWS - L.hub.xyz;
    s.fade     = DetailFade(baseWS);

    float plain = LegSweepCover(baseWS);
    s.height = SurfaceHeight(s.mapPos, s.fade) * (1.0 - plain) + float4(0.5, 0, 0, 0) * plain;
    s.positionWS = baseWS + s.normalWS * ((s.height.x - 0.5) * _Displace
                 * lerp(_DistantDisplacementMultiplier, 1.0, s.fade));

    if (_FollowRipples > 0.5)
        s.positionWS += RippleFieldOffset(baseWS);
    return s;
}

DepthVaryings LegDepthVertex(LegAttributes v)
{
    DepthVaryings o;
    o.positionHCS = TransformWorldToHClip(EvaluateLeg(v).positionWS);
    return o;
}

NormalVaryings LegDepthNormalsVertex(LegAttributes v)
{
    LegSample s = EvaluateLeg(v);
    NormalVaryings o;
    o.positionHCS = TransformWorldToHClip(s.positionWS);
    o.normalWS    = BumpNormal(s.normalWS, s.height, float4(0, 0, 0, 0), s.fade); // noise already sampled
    return o;
}

#endif

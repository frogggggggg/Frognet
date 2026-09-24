#ifndef BLOOD_CELL_CORE_INCLUDED
#define BLOOD_CELL_CORE_INCLUDED

// Shared by Custom/BloodCellTriplanar (tessellated cells) and Custom/BloodCellLegs
// (GPU-instanced legs): properties, height field, mapping, impact ripple, bump,
// and the depth / depth-normals fragments. Moved out of the cell shader verbatim.


// Specular workflow: lets the reflection be tinted red instead of
// picking up a blue sky over near-black albedo.
#define _SPECULAR_SETUP 1

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "TriplanarCore.cginc"
#include "RippleField.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _MainTex_ST;
    half4  _Color;
    half4  _DeepColor;
    half4  _SubsurfaceColor;
    half4  _SpecTint;
    float  _DetailStrength;
    float  _NoiseScale;
    float  _BumpStrength;
    float  _Detail;
    float  _Lacunarity;
    float  _Gain;
    float  _Displace;
    float  _TessDensity;
    float  _TessMax;
    float  _PhongStrength;
    float  _DetailFadeStart;
    float  _DetailFadeEnd;
    float  _DistantDetail;
    float  _DistantBumpMultiplier;
    float  _DistantDisplacementMultiplier;
    float  _DistantTextureDetailMultiplier;
    float  _Glossiness;
    float  _GlossVariation;
    float  _OcclusionStrength;
    float  _SubsurfaceStrength;
    float  _RimPower;
    float  _ThinGlow;
    float  _BlendSharpness;
    float  _PulseAmount;
    float  _PulseSpeed;
    float  _PulseVariation;
    float  _LightBands;
    float  _BandSoftness;
    float  _ColorSteps;
    float  _RimSteps;
    float  _SpecThreshold;
    float  _RippleAmplitude;
    float  _RippleWavelength;
    float  _RippleSpeed;
    float  _RippleInitialRadius;
    float  _RippleWidth;
    float  _RippleDecay;
    float  _FollowRipples;
CBUFFER_END

// Deliberately outside UnityPerMaterial: arrays cannot be declared in a
// Properties block. Written per renderer from a MaterialPropertyBlock.
// Start time (w) must be in the same clock as _Time.y, which URP sets
// from Time.time in play mode.
#define RIPPLE_COUNT 64                // must match Surface.MaxRipples
float4 _RipplePoints[RIPPLE_COUNT];   // xyz object-local impact point, w start time
float4 _RippleValues[RIPPLE_COUNT];   // x strength
float  _RippleCount;                  // live ripples, packed at the front

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

#define BARY3(a, b, c, w) ((a) * (w).x + (b) * (w).y + (c) * (w).z)

// ---------------------------------------------------------------
// Banding
// ---------------------------------------------------------------

// Flatten a 0..1 ramp into discrete steps. aa widens the edge so it
// antialiases; pass 0 where derivatives are unavailable or undefined.
float QuantizeBandW(float x, float bands, float softness, float aa)
{
    float s = saturate(x) * bands;
    float i = floor(s);
    float w = clamp(max(softness * bands, aa), 1e-4, 0.5);
    return (i + smoothstep(1.0 - w, 1.0, s - i)) / bands;
}

// Fragment-only, and only in uniform control flow (fwidth).
float QuantizeBand(float x, float bands, float softness)
{
    return QuantizeBandW(x, bands, softness, fwidth(saturate(x) * bands));
}

// ---------------------------------------------------------------
// Distance fade -- computed once per invocation, then reused.
// World-distance based so it works in domain and fragment alike.
// ---------------------------------------------------------------

float DetailFade(float3 positionWS)
{
    float s = max(0.0, _DetailFadeStart);
    float e = max(s + 0.001, _DetailFadeEnd);
    return 1.0 - smoothstep(s, e, distance(positionWS, GetCameraPositionWS()));
}

// ---------------------------------------------------------------
// Height field
// ---------------------------------------------------------------

// One noise evaluation yields height and its exact gradient; callers
// that only read .x let the compiler strip the derivative math.
//
// Fluctuation scales the field about its midpoint (lumps swell in
// place instead of sliding), with phase driven by the height itself so
// neighbouring lumps fall out of sync.
float4 SurfaceHeight(float3 p, float fade)
{
    float detail = saturate(lerp(_DistantDetail, _Detail, fade));
    float4 n  = FBM3D(p * _NoiseScale, _Gain, _Lacunarity, detail);
    float4 hd = float4(n.x, n.yzw * _NoiseScale);   // chain rule

    if (_PulseAmount <= 0.0)
        return hd;

    float centered = hd.x - 0.5;
    float phaseMul = TWO_PI * _PulseVariation;
    float s, c;
    sincos(_Time.y * _PulseSpeed + hd.x * phaseMul, s, c);
    float k    = 1.0 + _PulseAmount * s;
    float dkdh = _PulseAmount * c * phaseMul;

    return float4(0.5 + centered * k, hd.yzw * (k + centered * dkdh));
}

// ---------------------------------------------------------------
// Mapping, made independent of Transform scale
// ---------------------------------------------------------------

float3 ObjectScale()
{
    return float3(length(unity_ObjectToWorld._m00_m10_m20),
                  length(unity_ObjectToWorld._m01_m11_m21),
                  length(unity_ObjectToWorld._m02_m12_m22));
}

// With scale divided out, object->world is a pure rotation.
float3x3 MapToWorldRotation()
{
    float3 s = max(ObjectScale(), 1e-5);
    return float3x3(unity_ObjectToWorld._m00_m01_m02 / s,
                    unity_ObjectToWorld._m10_m11_m12 / s,
                    unity_ObjectToWorld._m20_m21_m22 / s);
}

float3 MapDirToWorld(float3 v)
{
#ifdef _SPACE_WORLD
    return v;
#else
    return mul(MapToWorldRotation(), v);
#endif
}

float3 WorldDirToMap(float3 v)
{
#ifdef _SPACE_WORLD
    return v;
#else
    return mul(v, MapToWorldRotation());   // R^-1 == R^T
#endif
}

// Object mode multiplies by scale so lump size is in world units.
float3 MapPosition(float3 positionOS, float3 positionWS)
{
#ifdef _SPACE_WORLD
    return positionWS;
#else
    return positionOS * ObjectScale();
#endif
}

// Impacts are stored object-local, so they ride along with the cell.
float3 RipplePointToMap(float3 localImpactPoint)
{
#ifdef _SPACE_WORLD
    return TransformObjectToWorld(localImpactPoint);
#else
    return localImpactPoint * ObjectScale();
#endif
}

// ---------------------------------------------------------------
// Impact ripple. Returns (height, map-space gradient).
//
// Causal: nothing ahead of the wavefront moves. The wave is
//   w(b) = cos(kb) * exp(-b^2/W^2) * ramp(b),   b = front - dist
// The ramp (smoothstep over a quarter wavelength) makes the front
// continuous; without it the front was a full-amplitude step that
// tore a visible travelling seam into both geometry and shading.
// ---------------------------------------------------------------
float4 Ripple(float3 mapPos)
{
    float4 result  = 0.0;
    float  wl      = max(_RippleWavelength, 1e-3);
    float  k       = TWO_PI / wl;
    float  invW2   = 1.0 / max(_RippleWidth * _RippleWidth, 1e-4);
    float  rampLen = wl * 0.25;
    float  radius0 = max(0.0, _RippleInitialRadius);

    // Only the live ripples, and faded ones skipped before any distance work,
    // so the cost follows how many are actually rippling.
    int count = min((int)_RippleCount, RIPPLE_COUNT);

    [loop]
    for (int i = 0; i < count; i++)
    {
        float strength = _RippleValues[i].x;
        float age      = _Time.y - _RipplePoints[i].w;
        if (strength <= 0.0 || age < 0.0)
            continue;

        float amp = strength * _RippleAmplitude * exp(-age * _RippleDecay);
        if (amp < 1e-4)
            continue;

        float3 offset = mapPos - RipplePointToMap(_RipplePoints[i].xyz);
        float  dist   = length(offset);
        float  behind = radius0 + age * _RippleSpeed - dist;
        float  b2W    = behind * behind * invW2;
        if (behind <= 0.0 || b2W > 9.0) // ahead of the front, or the ring has passed
            continue;

        float env   = exp(-b2W);
        float t     = saturate(behind / rampLen);
        float ramp  = t * t * (3.0 - 2.0 * t);
        float dramp = 6.0 * t * (1.0 - t) / rampLen;
        float s, c;
        sincos(k * behind, s, c);

        result.x += amp * c * env * ramp;

        // dw/db, then chain through db/dp = -offset/dist.
        float dw = env * ((-k * s - 2.0 * behind * invW2 * c) * ramp + c * dramp);
        result.yzw -= amp * dw * offset / max(dist, 1e-4);
    }
    return result;
}

// Bump the geometric normal by the combined height gradient, projected
// onto the tangent plane so it tilts rather than inflates. Ripple
// gradient is already in world height per unit, so it bypasses
// _BumpStrength and matches the geometry it displaced.
float3 BumpNormal(float3 geoNormalWS, float4 hd, float4 ripple, float fade)
{
    float  bump   = _BumpStrength * lerp(_DistantBumpMultiplier, 1.0, fade);
    float3 gradWS = MapDirToWorld(hd.yzw * bump + ripple.yzw);
    return normalize(geoNormalWS - (gradWS - geoNormalWS * dot(gradWS, geoNormalWS)));
}

// ---------------------------------------------------------------
// Focus sweep: see the backs of surfaces, as plain outlines
// ---------------------------------------------------------------
// While ScreenInvertTest sweeps (INVERT_BACKFACES on, material Cull Off), inside its circle:
// front faces are dropped from colour and depth alike, so the sweep's outlines trace the
// insides; and surfaces go plain (no texture displacement or bump; smooth vertex normals, bent
// only by ripples), so the outlines show shape and ripples, never texture or triangles. The circle is computed exactly as
// ScreenInvertSweep does (clip space, aspect-corrected, radius reaching the furthest corner).

#if defined(INVERT_BACKFACES)
float4 _InvertSweep; // xyz world centre, w eased progress (ScreenInvertTest)

// 1 inside the sweep circle, 0 outside.
float InvertSweepCover(float3 positionWS)
{
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

void InvertSweepClip(float3 positionWS, bool front)
{
    if (front && InvertSweepCover(positionWS) > 0.5) clip(-1.0);
}
    #define INVERT_FACE_ARG , FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC
    #define INVERT_SWEEP_CLIP(positionWS) InvertSweepClip(positionWS, IS_FRONT_VFACE(face, true, false))
#else
    #define INVERT_FACE_ARG
    #define INVERT_SWEEP_CLIP(positionWS)
#endif

// ---------------------------------------------------------------
// Shared depth / depth-normals stages
// ---------------------------------------------------------------

struct DepthVaryings
{
    float4 positionHCS : SV_POSITION;
#if defined(INVERT_BACKFACES)
    float3 positionWS  : TEXCOORD7;
#endif
};

// Matches URP's DepthOnly: some platforms copy depth through the R
// channel, so this must write depth, not 0.
half4 DepthFrag(DepthVaryings input INVERT_FACE_ARG) : SV_Target
{
    INVERT_SWEEP_CLIP(input.positionWS);
    return input.positionHCS.z;
}

struct NormalVaryings
{
    float4 positionHCS : SV_POSITION;
    float3 normalWS    : TEXCOORD0;
#if defined(INVERT_BACKFACES)
    float3 positionWS  : TEXCOORD7;
#endif
};

half4 DepthNormalsFrag(NormalVaryings input INVERT_FACE_ARG) : SV_Target
{
    INVERT_SWEEP_CLIP(input.positionWS);
    float3 n = normalize(input.normalWS);
#if defined(INVERT_BACKFACES)
    n *= IS_FRONT_VFACE(face, 1.0, -1.0); // a back face seen from inside faces the camera the other way
#endif
#if defined(_GBUFFER_NORMALS_OCT)
    float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
    return half4(PackFloat2To888(oct), 0.0);
#else
    return half4(n, 0.0);
#endif
}

#endif

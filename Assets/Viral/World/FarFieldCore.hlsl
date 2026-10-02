#ifndef VIRAL_FAR_FIELD_CORE_INCLUDED
#define VIRAL_FAR_FIELD_CORE_INCLUDED

// Shared by the far field's stand-in shaders (Custom/FarField, Custom/FarFieldCell; FarField.cs): the instance's
// pose comes from _FarPosed[_FarVisible[_FarGroupBase + instance]], written by FarField.compute. Every pass clips
// alike: the object's own fade (far edge, fading in when generated) and the complement of the real object's
// stream fade. Include after URP's Core.hlsl.

#include "StreamFade.hlsl"

struct Posed { float4 position, rotation, scale; };
StructuredBuffer<Posed> _FarPosed;
StructuredBuffer<uint> _FarVisible;
uint _FarGroupBase;

struct FarAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    uint instanceID   : SV_InstanceID;
};

float3 FarRotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

Posed FarPose(FarAttributes v) { return _FarPosed[_FarVisible[_FarGroupBase + v.instanceID]]; }

// World position and normal; fades.x = its own, .y = the real object's; seed = per instance.
float3 FarWorld(FarAttributes v, Posed p, out float3 normalWS, out float2 fades, out float seed)
{
    fades = float2(p.position.w, p.scale.w);
    seed = frac(dot(p.position.xyz, float3(0.1031, 0.1130, 0.0973)));
    normalWS = normalize(FarRotate(p.rotation, v.normalOS / p.scale.xyz));
    return p.position.xyz + FarRotate(p.rotation, v.positionOS.xyz * p.scale.xyz);
}

float3 FarWorld(FarAttributes v, out float3 normalWS, out float2 fades, out float seed)
{
    return FarWorld(v, FarPose(v), normalWS, fades, seed);
}

void FarClip(float2 fades, float2 pixel)
{
    StreamFadeClip(fades.x, pixel);
    StreamFadeClipComplement(fades.y, pixel);
}

#endif

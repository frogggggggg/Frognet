#ifndef SHRINK_WRAP_INCLUDED
#define SHRINK_WRAP_INCLUDED

// Radial shape maps (ShrinkWrap.cs): per slot, the farthest surface distance from a centre in every world
// direction, as a dual-paraboloid pair (slice slot*2 = the +Z hemisphere, slot*2 + 1 = -Z). Written by
// Hidden/ShrinkWrapCapture and read here through the one mapping below, so the two always agree.

// Paraboloid coordinates (-1..1 on the hemisphere) of a unit direction, on the side 'side' (+1 / -1) of Z.
float2 ShrinkWrapUV(float3 dir, float side)
{
    return dir.xy / max(1.0 + dir.z * side, 1e-3);
}

#ifndef SHRINK_WRAP_CAPTURE
TEXTURE2D_ARRAY(_WrapMaps);
SAMPLER(sampler_WrapMaps);

// Farthest surface distance (world units) along unit world direction 'dir' from the slot's centre; 0 = nothing there.
float ShrinkWrapDistance(float3 dir, float slot)
{
    float side = dir.z >= 0.0 ? 1.0 : -1.0;
    float2 uv = ShrinkWrapUV(dir, side) * 0.5 + 0.5;
    return SAMPLE_TEXTURE2D_ARRAY_LOD(_WrapMaps, sampler_WrapMaps, uv, slot * 2.0 + (side > 0.0 ? 0.0 : 1.0), 0).r;
}
#endif

#endif

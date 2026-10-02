#ifndef CELL_BURST_INCLUDED
#define CELL_BURST_INCLUDED

// A cell bursting (CellBurst.cs): the body's own shader swells it and pushes blebs out of it, then hides it;
// CellDebris.shader draws the pieces it breaks into, swollen by the same BurstSwell, so they take over seamlessly. Worked in "body radii" (q = position from the body's
// centre / its radius, in a frame fixed to the body), so it fits any size and shape. Needs ValueNoise3D (TriplanarCore).

// Seconds (CellBurst.cs has the same: keep them alike).
#define BURST_SWELL  0.30 // swelling and blistering before the membrane gives
#define BURST_BREAK  0.35 // from the first hole to that patch gone
#define BURST_SPREAD 0.20 // how much later the far side goes than the entry

// Time since the burst reached this point: the far side starts later.
float BurstLocalTime(float age, float3 q, float3 entry)
{
    return age - BURST_SPREAD * saturate(distance(q, entry) * 0.5);
}

// Blebs: a lumpy field over the membrane, 0 flat .. 1 a full blister.
float BurstBleb(float3 q, float seed)
{
    float b = saturate(ValueNoise3D(q * 3.2 + seed * 3.7).x * 1.8 - 0.6);
    return b * b;
}

// How far (body radii) the membrane at q is pushed out along its normal 'age' seconds after the kill: it inflates,
// blisters and shivers.
float BurstSwell(float3 q, float age, float3 entry, float seed)
{
    float t = BurstLocalTime(age, q, entry);
    if (t <= 0.0) return 0.0;
    float s = smoothstep(0.0, BURST_SWELL, t);
    float shiver = sin(_Time.y * 57.0 + dot(q, float3(7.1, 5.3, 6.2)) + seed) * 0.012;
    return s * (0.09 + 0.16 * BurstBleb(q, seed) + shiver);
}

// The same without the shiver, at local time t (the pieces: they'd buzz).
float BurstSwellStill(float3 q, float t, float seed)
{
    return t <= 0.0 ? 0.0 : smoothstep(0.0, BURST_SWELL, t) * (0.09 + 0.16 * BurstBleb(q, seed));
}

// Whether the body is still drawn (below 0: clipped). At BURST_SWELL its broken copy (CellDebris) takes its place.
float BurstCover(float age)
{
    return BURST_SWELL - age;
}

#endif

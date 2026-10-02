// Shared by Custom/CellInterior (organelle bodies) and Custom/CellInteriorMotes (motes, streams, waves).
// Everything is laid out in a cell's *frame*: a unit ball stretched over the cell's insides (CellInteriorView),
// so a point p in it sits at centre + axisX p.x + axisY p.y + axisZ p.z (axisZ = the cell's thin side).
// Motion lives here, in the vertex stages, so bodies and the streams feeding them drift together; the CPU only
// writes the layout and a few rates.
#ifndef CELL_INTERIOR_INCLUDED
#define CELL_INTERIOR_INCLUDED

#include "../FocusSweep.hlsl"

struct CellFrame
{
    float4 centre; // xyz world, w appear 0..1 (grows out from the middle as focus starts)
    float4 axisX;  // xyz world half axis, w the frame's unit in metres (longest half axis)
    float4 axisY;  // xyz, w nucleus highlight 0..1 (pointed at / opened)
    float4 axisZ;  // xyz (the thin side), w honey motes (CellInteriorMotes)
};

StructuredBuffer<CellFrame> _CellFrames;

float3 FramePoint(CellFrame f, float3 p)
{
    return f.centre.xyz + f.axisX.xyz * p.x + f.axisY.xyz * p.y + f.axisZ.xyz * p.z;
}

// A frame direction to world (not normalised).
float3 FrameVector(CellFrame f, float3 v)
{
    return f.axisX.xyz * v.x + f.axisY.xyz * v.y + f.axisZ.xyz * v.z;
}

// Slow wandering of a body (and of the ends of streams tied to it), in frame units. seed < 0: none.
float3 CellDrift(float seed, float t)
{
    if (seed < 0.0) return 0;
    return float3(sin(t * 0.37 + seed * 6.283), sin(t * 0.29 + seed * 17.31), 0.5 * sin(t * 0.23 + seed * 31.7)) * 0.025;
}

// The path honey takes from a body to the pool round the nucleus: a slow sway (one arc + a gentle S), shared by
// the stream drawn from a working maker and the drops riding it (CellInteriorView.HoneyPath mirrors it on the CPU).
// bow: HoneyBow(seed of the body, t).
float2 HoneyBow(float seed, float t)
{
    return float2(0.08 * sin(t * 0.25 + seed * 9.0), 0.025 * sin(t * 0.5 + seed * 5.0));
}

float3 HoneyPath(float3 a, float3 b, float u, float2 bow)
{
    float3 ab = b - a;
    float3 perp = float3(-ab.y, ab.x, 0.0);
    perp = dot(perp, perp) > 1e-8 ? normalize(perp) : float3(1, 0, 0);
    return lerp(a, b, u) + perp * length(ab) * (bow.x * 4.0 * u * (1.0 - u) + bow.y * sin(6.2832 * u));
}

// Things grow in from the middle outward as the view opens.
float CellAppear(CellFrame f, float3 p)
{
    return smoothstep(0.0, 1.0, saturate((f.centre.w * 1.6 - length(p) * 0.6) / 0.4));
}

uint CellPcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// 0..1 from two integers.
float CellRand(uint a, uint b)
{
    return CellPcg(a ^ CellPcg(b + 0x9e3779b9u)) * (1.0 / 4294967295.0);
}

#endif

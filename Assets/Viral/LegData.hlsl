#ifndef LEG_DATA_INCLUDED
#define LEG_DATA_INCLUDED

// One leg as the leg shader draws it, written by LegSimulation.compute (LegRenderer's LegData stride: 8 float4s).
struct LegData
{
    float4 p0;     // xyz root,           w root radius
    float4 p1;     // xyz control 1,      w tip radius
    float4 p2;     // xyz control 2,      w writhe amplitude
    float4 p3;     // xyz tip,            w phase
    float4 side;   // xyz bend side axis, w radius swell
    float4 clampN; // xyz ground normal,  w 1 = clear the ground
    float4 ground; // xyz ground point
    float4 hub;    // xyz hub (noise is mapped relative to it, so lumps ride the body)
};

#endif

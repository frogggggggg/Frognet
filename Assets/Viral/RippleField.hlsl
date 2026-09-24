#ifndef RIPPLE_FIELD_INCLUDED
#define RIPPLE_FIELD_INCLUDED

// ---------------------------------------------------------------------------
// Ripple field: every cell's live impact ripples in one global list (filled by
// RippleField.cs), so anything drawn near a cell can ride its waves in its own
// vertex stage. No per-object scripts:
//
//     positionWS += RippleFieldOffset(positionWS);
//
// Shader Graph: Custom Function node, File mode, this file, name "RippleField".
//
// Grouped by cell: a point checks the few rippling cells, and only loops the
// ripples of a cell it's near, skipping any whose ring isn't crossing it. Same
// wave as the cell shader's Ripple(): causal front, Gaussian envelope,
// quarter-wavelength ramp. The point is projected onto the cell's sphere to find
// its distance along the surface, pushed along the cell's radial direction, and
// faded out with its height above the surface.
// ---------------------------------------------------------------------------

#define RIPPLE_FIELD_MAX   256 // must match RippleField.Max
#define RIPPLE_FIELD_CELLS 32  // must match RippleField.MaxCells

float4 _RippleFieldWavePoints[RIPPLE_FIELD_MAX];    // xyz world impact point, w start time (_Time.y clock)
float4 _RippleFieldWaveShape[RIPPLE_FIELD_MAX];     // x 2pi/wavelength, y speed, z 1/width^2, w decay
float4 _RippleFieldWaveShape2[RIPPLE_FIELD_MAX];    // x initial radius, y ramp length, z strength * amplitude
float4 _RippleFieldCells[RIPPLE_FIELD_CELLS];   // xyz cell centre, w radius
float4 _RippleFieldCellRange[RIPPLE_FIELD_CELLS]; // x first ripple, y ripple count
float  _RippleFieldCellCount;
float  _RippleFieldInvInfluence;                // 1 / how far off the surface riders still follow

float3 RippleFieldOffset(float3 positionWS)
{
    float3 offset = 0.0;
    int cells = min((int)_RippleFieldCellCount, RIPPLE_FIELD_CELLS);

    [loop]
    for (int c = 0; c < cells; c++)
    {
        float4 cell = _RippleFieldCells[c];
        float3 fromCentre = positionWS - cell.xyz;
        float  r = length(fromCentre);

        // Only near this cell's surface.
        float near = saturate(1.0 - abs(r - cell.w) * _RippleFieldInvInfluence);
        if (near <= 0.0 || r < 1e-4)
            continue;

        float3 dir = fromCentre / r;
        float3 onSurface = cell.xyz + dir * cell.w;

        int first = (int)_RippleFieldCellRange[c].x;
        int last = min(first + (int)_RippleFieldCellRange[c].y, RIPPLE_FIELD_MAX);
        float height = 0.0;

        [loop]
        for (int i = first; i < last; i++)
        {
            float4 pt  = _RippleFieldWavePoints[i];
            float4 sh  = _RippleFieldWaveShape[i];
            float4 sh2 = _RippleFieldWaveShape2[i];

            float age = _Time.y - pt.w;
            if (age < 0.0)
                continue;

            float amp = sh2.z * exp(-age * sh.w);
            if (amp < 1e-4)
                continue;

            float behind = sh2.x + age * sh.y - length(onSurface - pt.xyz);
            float b2W = behind * behind * sh.z;
            if (behind <= 0.0 || b2W > 9.0) // ahead of the front, or the ring has passed
                continue;

            float t    = saturate(behind / sh2.y);
            float ramp = t * t * (3.0 - 2.0 * t);
            height += amp * cos(sh.x * behind) * exp(-b2W) * ramp;
        }

        offset += dir * (height * near);
    }
    return offset;
}

// Shader Graph Custom Function entry points.
void RippleField_float(float3 PositionWS, out float3 Offset) { Offset = RippleFieldOffset(PositionWS); }
void RippleField_half(half3 PositionWS, out half3 Offset) { Offset = (half3)RippleFieldOffset((float3)PositionWS); }

#endif

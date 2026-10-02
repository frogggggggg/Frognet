// The focus sweep's circle for things drawn over it (x-ray: the drill, chunk cores, cell interiors): 1 inside,
// 0 outside or when no sweep runs. Same circle as ScreenInvertSweep / InvertSweepCover in BloodCellCore.
// _InvertSweep is set globally by ScreenInvertTest: xyz world centre, w eased progress (0 = not sweeping).
#ifndef FOCUS_SWEEP_INCLUDED
#define FOCUS_SWEEP_INCLUDED

float4 _InvertSweep;

float SweepCover(float3 positionWS)
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
    float radius = maxRadius * saturate(_InvertSweep.w);
    return 1.0 - smoothstep(radius - 0.01, radius, distance(here, centre));
}

#endif

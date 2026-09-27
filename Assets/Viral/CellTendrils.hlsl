#ifndef CELL_TENDRILS_INCLUDED
#define CELL_TENDRILS_INCLUDED

// Tendrils creeping over a cell from one point (a blight gene injected there: Surface.Infect, GeneEffects).
// Everything comes from three per-renderer vectors set once (MaterialPropertyBlock) and the clock; a clean
// cell has _TendrilOrigin.w = 0 and skips it all in a uniform branch.
//
// Worked in renderer-local space over the cell's radius, so the pattern rides the cell. Distance over the
// surface is the chord plus how far the surface has turned from the entry, so the far face of a flat cell
// is reached round the rim, not straight through it. Main tendrils are rays out of the entry: points whose
// direction from it lies where a noise over directions crosses its midpoint (a line on the sphere of
// directions meets the surface's directions in points, so each is a ray), warped more the farther out,
// thinning toward the front. Side roots are a finer set starting a little way out; a capillary web fills in
// behind the front; a blot sits on the entry. Widths are in cell radii, antialiased by the pixel's size.
// Needs ValueNoise3D (TriplanarCore) and Core.hlsl.

float4 _TendrilOrigin; // xyz renderer-local entry point, w start time (_Time.y's clock); 0 = none
float4 _TendrilNormal; // xyz renderer-local surface normal at the entry, w front speed (local units / s)
float4 _TendrilColor;  // rgb colour (linear), w cell radius (local units)

// Distance, in the noise's input units, to where it crosses its midpoint.
float TendrilIso(float4 n)
{
    return abs(n.x - 0.5) / max(length(n.yzw), 1e-3);
}

// Coverage 0..1 of a line 'dist' from its centre, 'width' across, AA over 'px' (all cell radii).
float TendrilLine(float dist, float width, float px)
{
    return 1.0 - smoothstep(width * 0.5 - px, width * 0.5 + px, dist);
}

// Coverage 0..1 of the tendrils at a point; 'behind' 0..1 how far inside the front it is (for the stain).
float TendrilCover(float3 posOS, float3 normalOS, float pixelOS, out float behind, out float along)
{
    float  R  = max(_TendrilColor.w, 1e-4);
    float  px = max(pixelOS / R, 1e-4);
    float3 q  = posOS / R;
    float3 dv = q - _TendrilOrigin.xyz / R;
    float  chord = length(dv);
    float  d = chord + (1.0 - dot(normalOS, _TendrilNormal.xyz)) * 0.6;

    float front = max(0.0, _Time.y - _TendrilOrigin.w) * _TendrilNormal.w / R;
    float reach = front * (0.7 + 0.6 * ValueNoise3D(q * 2.3 + 17.0).x); // a ragged front
    behind = saturate((reach - d) / 0.5);
    along = d;
    if (d > reach + px) return 0.0;

    float  tip = saturate(d / max(reach, 1e-3));   // 0 at the entry .. 1 at the front
    float  dt  = max(d, 0.02);
    float3 dir = dv / max(chord, 1e-4);

    // Main tendrils: meander more the farther out (warp grows with d).
    float3 wdir  = dir + ValueNoise3D(q * 2.0 + 3.1).yzw * (0.1 * d);
    float  main  = TendrilLine(dt * TendrilIso(ValueNoise3D(wdir * 3.0 + 41.0)) / 3.0,
                               lerp(0.07, 0.012, tip), px);

    // Side roots: finer, from a little way out.
    float3 wdir2 = dir + ValueNoise3D(q * 4.0 + 9.7).yzw * (0.06 * d);
    float  side  = TendrilLine(dt * TendrilIso(ValueNoise3D(wdir2 * 7.0 + 83.0)) / 7.0,
                               lerp(0.03, 0.006, tip), px)
                 * smoothstep(0.12, 0.3, d);

    // Capillary web behind the front, fading in as it ages.
    float3 qw  = q * 9.0 + ValueNoise3D(q * 3.0).yzw * 0.8;
    float  web = TendrilLine(TendrilIso(ValueNoise3D(qw)) / 9.0, 0.006, px)
               * saturate((reach - d - 0.2) / 0.6) * 0.8;

    // The blot on the entry, growing a little with the front.
    float blot = 1.0 - smoothstep(0.05, 0.05 + px + 0.04, chord - min(front, 2.0) * 0.04);

    // The front's own edge: tendrils grow in rather than being revealed by a hard line.
    float grow = saturate((reach - d) / 0.08 + 0.2);
    return saturate(max(max(main, side * 0.9), max(web, blot)) * grow);
}

// The cell's shaded colour with the tendrils over it.
half3 ApplyTendrils(half3 rgb, float3 positionWS, float3 normalWS)
{
    UNITY_BRANCH
    if (_TendrilOrigin.w > 0.0)
    {
        float3 posOS    = TransformWorldToObject(positionWS);
        float3 normalOS = TransformWorldToObjectNormal(normalWS);
        // One pixel's size here, in local units (perspective: by depth; orthographic: fixed).
        float  distWS   = distance(positionWS, GetCameraPositionWS());
        float  pixelWS  = unity_OrthoParams.w > 0.5 ? 2.0 * unity_OrthoParams.y / _ScreenParams.y
                                                    : 2.0 * distWS / (_ScreenParams.y * UNITY_MATRIX_P._m11);
        float  scale    = max(length(unity_ObjectToWorld._m00_m10_m20),
                              max(length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22)));
        float  behind = 0.0, along = 0.0;
        float  cover = TendrilCover(posOS, normalOS, pixelWS / max(scale, 1e-5), behind, along);

        // The blighted skin sickens behind the front: darker, greyer, a touch of the tendrils' colour.
        half luma = dot(rgb, half3(0.3, 0.59, 0.11));
        half3 sick = lerp(rgb, luma * lerp(half3(1, 1, 1), _TendrilColor.rgb * 6.0 + 0.4, 0.5), 0.45) * 0.75;
        rgb = lerp(rgb, sick, behind);

        // Tendrils: their colour, with a slow pulse running out along them.
        float pulse = pow(saturate(sin(along * 18.0 - _Time.y * 2.5)), 8.0);
        half3 ink = lerp(_TendrilColor.rgb, saturate(_TendrilColor.rgb * 5.0 + 0.04), pulse * 0.6);
        rgb = lerp(rgb, ink, cover);
    }
    return rgb;
}

#endif

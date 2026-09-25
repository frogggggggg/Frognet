#ifndef VIRAL_STREAM_FADE_INCLUDED
#define VIRAL_STREAM_FADE_INCLUDED

// Streamed things dissolve in and out at the edge of the loaded world (WorldStreamer) instead of popping. A whole
// object fades together, by its *centre's* distance from the streaming centre (the player), which is exactly what
// loading goes by: a sector loads when its nearest point comes within loadDistance, so anything not loaded yet has
// its centre farther than that, and the fade reaches 0 before it. Screen-door dithered (clip), so it stays opaque
// and writes depth: every pass clips alike and outlines / depth of field never see a ghost.
// Globals (outside UnityPerMaterial), set by WorldStreamer; with no streamer _StreamFade.w is 0 = off.

float4 _StreamFade;    // xyz = streaming centre, w = 1 / fade length
float  _StreamFadeEnd; // centre distance where it's gone

// 1 = shown, 0 = gone. (FarField.compute has a copy: keep them alike.)
float StreamFade(float3 centreWS)
{
    if (_StreamFade.w <= 0.0) return 1.0;
    return smoothstep(0.0, 1.0, saturate((_StreamFadeEnd - distance(centreWS, _StreamFade.xyz)) * _StreamFade.w));
}

// Vertex stage: an instance that's gone entirely is moved outside the clip volume (whole triangles culled, no pixels).
float4 StreamFadeHide(float4 positionCS, float fade)
{
    return fade > 0.0 ? positionCS : float4(2.0, 2.0, 2.0, 1.0);
}

// Fragment stage: pixel-stable dither (interleaved gradient noise) against the fade.
void StreamFadeClip(float fade, float2 pixel)
{
    if (fade >= 1.0) return;
    float dither = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    clip(fade - 0.001 - dither * 0.998);
}

// The far field's stand-in for a streamed thing (FarField): drawn exactly on the pixels the real one's
// StreamFadeClip drops at the same fade, so the two cross-dissolve at the load edge with no gap or overlap.
void StreamFadeClipComplement(float fade, float2 pixel)
{
    if (fade <= 0.0) return;
    float dither = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
    clip(dither * 0.998 - (fade - 0.001) - 1e-5);
}

// One renderer per object (the cells): its origin is the centre, and only streamed renderers (rendering layer bit
// WorldEntity.StreamedLayer) fade, so hand-placed scenery never does. Needs Core.hlsl.
#define STREAM_FADE_LAYER (1u << 30)
void StreamFadeObjectClip(float2 pixel)
{
    if (_StreamFade.w <= 0.0 || (asuint(unity_RenderingLayer.x) & STREAM_FADE_LAYER) == 0) return;
    StreamFadeClip(StreamFade(UNITY_MATRIX_M._m03_m13_m23), pixel);
}

#endif

#ifndef TRIPLANAR_CORE_INCLUDED
#define TRIPLANAR_CORE_INCLUDED

// ---------------------------------------------------------------------------
// Shared triplanar projection. Keep this file in the same folder as the
// shaders that #include it -- Unity resolves the path relative to the shader.
// ---------------------------------------------------------------------------

// Three planar UV sets, each dropping the axis it projects along.
// Mirror-corrected by the normal's sign so directional detail never reads
// backwards on the far side of an object.
void TriplanarUVs(float3 p, float3 n, out float2 uvX, out float2 uvY, out float2 uvZ)
{
    uvX = p.zy;
    uvY = p.xz;
    uvZ = p.xy;

    float3 s = sign(n);
    uvX.x *=  s.x;
    uvY.x *=  s.y;
    uvZ.x *= -s.z;
}

// Normal-driven blend weights, normalized to sum to 1.
float3 TriplanarWeights(float3 n, float sharpness)
{
    float3 w = pow(abs(n), sharpness);
    return w / max(w.x + w.y + w.z, 1e-5);
}

// One seamless sample. No frac() anywhere: the sampler's Repeat wrap does the
// tiling, so screen-space derivatives stay continuous and mips never break.
fixed4 TriplanarSample(sampler2D tex, float3 p, float3 n, float sharpness)
{
    float2 uvX, uvY, uvZ;
    TriplanarUVs(p, n, uvX, uvY, uvZ);
    float3 w = TriplanarWeights(n, sharpness);

    return tex2D(tex, uvX) * w.x
         + tex2D(tex, uvY) * w.y
         + tex2D(tex, uvZ) * w.z;
}

// ---------------------------------------------------------------------------
// Procedural 3D noise. Used for surface relief -- no noise texture asset needed.
// frac() is safe here: these values never pick a mip level.
// ---------------------------------------------------------------------------

float Hash13(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

// Value noise with a smoothstep fade -- the "smoothed" in smoothed-bumpy.
// Swapping this fade for a linear one gives hard faceted lumps instead.
float ValueNoise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = Hash13(i + float3(0,0,0));
    float n100 = Hash13(i + float3(1,0,0));
    float n010 = Hash13(i + float3(0,1,0));
    float n110 = Hash13(i + float3(1,1,0));
    float n001 = Hash13(i + float3(0,0,1));
    float n101 = Hash13(i + float3(1,0,1));
    float n011 = Hash13(i + float3(0,1,1));
    float n111 = Hash13(i + float3(1,1,1));

    float x00 = lerp(n000, n100, f.x);
    float x10 = lerp(n010, n110, f.x);
    float x01 = lerp(n001, n101, f.x);
    float x11 = lerp(n011, n111, f.x);

    return lerp(lerp(x00, x10, f.y), lerp(x01, x11, f.y), f.z);
}

// 4-octave fBm, returns roughly 0..1.
// detail = 1 uses every octave; detail = 0 collapses to one smooth blob.
float FBM3(float3 p, float gain, float lacunarity, float detail)
{
    float sum = 0.0, norm = 0.0, amp = 0.5, w = 1.0;

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        sum  += ValueNoise3(p) * amp * w;
        norm += amp * w;
        p    *= lacunarity;
        amp  *= gain;
        w    *= detail;
    }
    return sum / max(norm, 1e-5);
}

#endif // TRIPLANAR_CORE_INCLUDED

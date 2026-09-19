#ifndef TRIPLANAR_CORE_INCLUDED
#define TRIPLANAR_CORE_INCLUDED

// ---------------------------------------------------------------------------
// Shared triplanar projection + procedural relief, URP/HLSL.
// Kept as .cginc purely so the existing .meta survives -- the extension means
// nothing for an include. Requires Core.hlsl to be included before this.
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
half4 TriplanarSample(TEXTURE2D_PARAM(tex, samp), float3 p, float3 n, float sharpness)
{
    float2 uvX, uvY, uvZ;
    TriplanarUVs(p, n, uvX, uvY, uvZ);
    float3 w = TriplanarWeights(n, sharpness);

    return SAMPLE_TEXTURE2D(tex, samp, uvX) * w.x
         + SAMPLE_TEXTURE2D(tex, samp, uvY) * w.y
         + SAMPLE_TEXTURE2D(tex, samp, uvZ) * w.z;
}

// ---------------------------------------------------------------------------
// Procedural 3D noise with analytic derivatives.
// frac() is safe here: these values never pick a mip level.
// ---------------------------------------------------------------------------

float Hash13(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

// Returns (value, d/dx, d/dy, d/dz).
//
// Trilinear interpolation of the 8 corners is a polynomial in the smoothstep
// fade u, so its exact gradient falls out of the same coefficients -- no
// finite-difference taps, and no epsilon to tune.
float4 ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);

    float3 u  = f * f * (3.0 - 2.0 * f);   // the "smoothed" in smoothed-bumpy
    float3 du = 6.0 * f * (1.0 - f);       // d/df of that fade

    float n000 = Hash13(i + float3(0,0,0));
    float n100 = Hash13(i + float3(1,0,0));
    float n010 = Hash13(i + float3(0,1,0));
    float n110 = Hash13(i + float3(1,1,0));
    float n001 = Hash13(i + float3(0,0,1));
    float n101 = Hash13(i + float3(1,0,1));
    float n011 = Hash13(i + float3(0,1,1));
    float n111 = Hash13(i + float3(1,1,1));

    // Trilinear rewritten in the monomial basis 1, x, y, z, xy, yz, zx, xyz.
    float k0 = n000;
    float k1 = n100 - n000;
    float k2 = n010 - n000;
    float k3 = n001 - n000;
    float k4 = n000 - n100 - n010 + n110;
    float k5 = n000 - n010 - n001 + n011;
    float k6 = n000 - n100 - n001 + n101;
    float k7 = -n000 + n100 + n010 - n110 + n001 - n101 - n011 + n111;

    float value = k0 + k1 * u.x + k2 * u.y + k3 * u.z
                + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x
                + k7 * u.x * u.y * u.z;

    float3 deriv = du * float3(
        k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
        k2 + k4 * u.x + k5 * u.z + k7 * u.z * u.x,
        k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);

    return float4(value, deriv);
}

// 4-octave fBm, returning (value, gradient). Value lands roughly in 0..1.
// detail = 1 uses every octave; detail = 0 collapses to one smooth blob.
//
// The gradient accumulates through the chain rule: octave i is sampled at
// p * freq, so its derivative contributes freq times its own amplitude.
float4 FBM3D(float3 p, float gain, float lacunarity, float detail)
{
    float  value = 0.0, norm = 0.0, amp = 0.5, w = 1.0, freq = 1.0;
    float3 deriv = float3(0.0, 0.0, 0.0);

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float4 n = ValueNoise3D(p * freq);

        value += n.x   * amp * w;
        deriv += n.yzw * amp * w * freq;
        norm  += amp * w;

        freq *= lacunarity;
        amp  *= gain;
        w    *= detail;
    }

    return float4(value, deriv) / max(norm, 1e-5);
}

// Value only. The compiler dead-strips the derivative math, so this costs the
// 8 hashes per octave and nothing more -- use it wherever the slope is unused.
float FBM3(float3 p, float gain, float lacunarity, float detail)
{
    return FBM3D(p, gain, lacunarity, detail).x;
}

#endif // TRIPLANAR_CORE_INCLUDED

#ifndef STYLIZED_CELL_SKY_INCLUDED
#define STYLIZED_CELL_SKY_INCLUDED

// The cell sky's maths, shared by Custom/StylizedCellSkybox (drawn live, per pixel) and
// Hidden/StylizedCellSkyBake (SkyboxCache renders it into cubemaps). Moved here verbatim.
// Needs URP's Core.hlsl first.

float4 _ColorA;
float4 _ColorB;
float4 _ColorC;
float4 _MembraneColor;
float4 _HorizonColor;

float _Scale;
float _DetailScale;
float _WarpScale;
float _WarpStrength;
float _DepthOffset;
float _Contrast;

float _MembraneWidth;
float _MembraneSharpness;
float _CellDepth;
float _RidgeStrength;

float4 _LightDir;
float _LightStrength;
float _BackLightStrength;
float _HorizonStrength;

float _TimeScale;
float4 _DriftDirection;

// FIXED: Renamed from SafeNormalize to CustomSafeNormalize to prevent redefinition conflict
float3 CustomSafeNormalize(float3 v)
{
    float lenSq = dot(v, v);
    if (lenSq < 1e-8) return float3(0, 0, 1);
    return v * rsqrt(lenSq);
}

// 3D simplex-style noise based on your function, wrapped as a reusable helper.
float SimplexNoise3D(float3 pos, float scale)
{
    float3 p = pos * scale;

    float F3 = 0.333333333;
    float G3 = 0.166666667;

    float3 s = floor(p + dot(p, (float3)F3));
    float3 x0 = p - s + dot(s, (float3)G3);

    float3 e = step((float3)0.0, x0 - x0.yzx);
    float3 i1 = e * (1.0 - e.zxy);
    float3 i2 = 1.0 - e.zxy * (1.0 - e);

    float3 x1 = x0 - i1 + (float3)G3;
    float3 x2 = x0 - i2 + (float3)(2.0 * G3);
    float3 x3 = x0 - (float3)(1.0 - 3.0 * G3);

    float4 w, d;

    float3 g0 = sin(float3(dot(s,  float3(127.1, 311.7,  74.7)),
                            dot(s,  float3(269.5, 183.3, 246.1)),
                            dot(s,  float3(113.5, 271.9, 124.3)))) * 43758.5453;
    g0 = frac(g0) - 0.5;

    float3 s1 = s + i1;
    float3 g1 = sin(float3(dot(s1, float3(127.1, 311.7,  74.7)),
                            dot(s1, float3(269.5, 183.3, 246.1)),
                            dot(s1, float3(113.5, 271.9, 124.3)))) * 43758.5453;
    g1 = frac(g1) - 0.5;

    float3 s2 = s + i2;
    float3 g2 = sin(float3(dot(s2, float3(127.1, 311.7,  74.7)),
                            dot(s2, float3(269.5, 183.3, 246.1)),
                            dot(s2, float3(113.5, 271.9, 124.3)))) * 43758.5453;
    g2 = frac(g2) - 0.5;

    float3 s3 = s + 1.0;
    float3 g3 = sin(float3(dot(s3, float3(127.1, 311.7,  74.7)),
                            dot(s3, float3(269.5, 183.3, 246.1)),
                            dot(s3, float3(113.5, 271.9, 124.3)))) * 43758.5453;
    g3 = frac(g3) - 0.5;

    w.x = dot(x0, x0);
    w.y = dot(x1, x1);
    w.z = dot(x2, x2);
    w.w = dot(x3, x3);

    w = max(0.6 - w, 0.0);

    d.x = dot(g0, x0);
    d.y = dot(g1, x1);
    d.z = dot(g2, x2);
    d.w = dot(g3, x3);

    w *= w;
    w *= w;

    return saturate((dot(w, d) * 52.0) * 0.5 + 0.5);
}

float Fbm(float3 p, float baseScale)
{
    float n = 0.0;
    float amp = 0.5;
    float freq = 1.0;

    [unroll]
    for (int i = 0; i < 5; i++)
    {
        n += SimplexNoise3D(p, baseScale * freq) * amp;
        freq *= 2.03;
        amp *= 0.5;
        p = p * 1.13 + float3(1.71, -2.19, 0.83);
    }

    return saturate(n / 0.96875);
}

float Ridge(float n)
{
    return 1.0 - abs(n * 2.0 - 1.0);
}

// Sky colour looking along dir (unit length) at animation time t (_Time.y * _TimeScale).
float3 CellSky(float3 dir, float t)
{
    float3 drift = normalize(_DriftDirection.xyz + float3(0.001, 0.0, 0.0)) * t;
    float3 p = dir + drift;

    float3 warp;
    warp.x = Fbm(p + float3(4.1, 1.2, -3.7), _WarpScale);
    warp.y = Fbm(p + float3(-2.8, 5.3, 0.9), _WarpScale);
    warp.z = Fbm(p + float3(0.6, -4.4, 2.7), _WarpScale);
    warp = (warp * 2.0 - 1.0) * _WarpStrength;

    float3 q = CustomSafeNormalize(dir + warp);

    float baseField = Fbm(q, _Scale);
    float depthField = Fbm(q + dir * _DepthOffset + drift * 0.3, _Scale * 0.9);

    float detail = Fbm(q + float3(2.4, -1.1, 3.2), _DetailScale);
    float ridge = Ridge(detail);

    float membraneSource = saturate(baseField * 0.72 + detail * 0.28);
    float distToBand = abs(membraneSource - 0.5);
    float membrane = 1.0 - smoothstep(0.0, _MembraneWidth, distToBand);
    membrane = pow(saturate(membrane), _MembraneSharpness);

    // FIXED: Completed the missing ending calculation logic for your fragment layout
    float combinedField = saturate(baseField + depthField * _CellDepth + ridge * _RidgeStrength);
    combinedField = pow(combinedField, _Contrast);

    // Lighting Calculations
    float3 lightDirection = CustomSafeNormalize(_LightDir.xyz);
    float NdotL = dot(dir, lightDirection);
    float diffuseLight = saturate(NdotL) * _LightStrength;
    float backLight = saturate(-NdotL) * _BackLightStrength;
    
    // Horizon Glow
    float horizonGlow = 1.0 - saturate(abs(dir.y));
    horizonGlow = pow(horizonGlow, 4.0) * _HorizonStrength;

    // Color Blending
    float3 finalColor = lerp(_ColorA.rgb, _ColorB.rgb, combinedField);
    finalColor = lerp(finalColor, _ColorC.rgb, saturate(diffuseLight + backLight));
    finalColor = lerp(finalColor, _MembraneColor.rgb, membrane);
    finalColor += _HorizonColor.rgb * horizonGlow;

    return finalColor;
}

#endif

#ifndef BLOOD_CELL_FORWARD_INCLUDED
#define BLOOD_CELL_FORWARD_INCLUDED

// Forward lighting (cel or PBR) shared by the cell and leg shaders. The including
// pass supplies the vertex/domain stage that fills Varyings.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct Varyings
{
    float4 positionHCS : SV_POSITION;
    float3 positionWS  : TEXCOORD0;
    float3 normalWS    : TEXCOORD1;
    float3 mapPos      : TEXCOORD2;
    float  fogCoord    : TEXCOORD3;
    float3 rippleGrad  : TEXCOORD4; // per vertex: the tessellated surface is dense enough
};

// Additional lights: no fwidth here. In Forward+ the loop count
// varies per pixel, and derivatives inside divergent flow are
// undefined (and a compile error on some platforms).
half3 CelAdditional(Light light, float3 N, half3 albedo)
{
    float l = saturate(dot(N, light.direction))
            * light.distanceAttenuation * light.shadowAttenuation;
    return albedo * light.color * QuantizeBandW(l, _LightBands, _BandSoftness, 0.0);
}

// Toon lighting: the same lights PBR would use, N.L quantized.
half3 CelShade(InputData inputData, SurfaceData surfaceData)
{
    float3 N = inputData.normalWS;
    float3 V = inputData.viewDirectionWS;
    half3  albedo = surfaceData.albedo;

    Light mainLight = GetMainLight(inputData.shadowCoord);
    float lit  = saturate(dot(N, mainLight.direction)) * mainLight.shadowAttenuation;
    float band = QuantizeBand(lit, _LightBands, _BandSoftness);
    half3 accum = albedo * mainLight.color * band;

    // Hard-edged highlight, gated by the lit band.
    float specPower = exp2(surfaceData.smoothness * 11.0) + 2.0;
    float spec = pow(saturate(dot(N, normalize(mainLight.direction + V))), specPower);
    float sw   = clamp(max(_BandSoftness, fwidth(spec)), 1e-4, 0.5);
    accum += mainLight.color * surfaceData.specular
           * smoothstep(_SpecThreshold - sw, _SpecThreshold + sw, spec)
           * step(0.001, band);

#ifdef _ADDITIONAL_LIGHTS
    #if USE_FORWARD_PLUS
    // Forward+ keeps extra directional lights outside the cluster loop.
    [loop] for (uint dirIndex = 0; dirIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); dirIndex++)
        accum += CelAdditional(GetAdditionalLight(dirIndex, inputData.positionWS,
                                                  inputData.shadowMask), N, albedo);
    #endif

    uint lightCount = GetAdditionalLightsCount();
    LIGHT_LOOP_BEGIN(lightCount)
        accum += CelAdditional(GetAdditionalLight(lightIndex, inputData.positionWS,
                                                  inputData.shadowMask), N, albedo);
    LIGHT_LOOP_END
#endif

    // Ambient and vertex lights stay flat; banding GI would bring
    // back the smooth falloff the bands exist to remove.
    accum += albedo * (inputData.bakedGI * surfaceData.occlusion + inputData.vertexLighting);
    return accum + surfaceData.emission;
}

// The cell surface's colour and lighting, given its bumped normal and height (no fog). Shared by
// the cells and legs (frag below) and anything else in the family (RopeBlood).
half3 CellShade(float3 positionWS, float4 positionHCS, float3 p, float3 geoNormal, float3 normalWS, float h, float fade)
{
    float3 viewWS   = GetWorldSpaceNormalizeViewDir(positionWS);

    // Ridges read as thicker haemoglobin, valleys as thinner.
    // Uniform branch, so ColorSteps = 1 costs nothing.
    float hAlbedo = h;
    if (_ColorSteps > 1.0)
        hAlbedo = QuantizeBand(h, _ColorSteps, _BandSoftness);

    half3 albedo = lerp(_DeepColor.rgb, _Color.rgb, hAlbedo);

    if (_DetailStrength > 0.0)
    {
        half3 detail = TriplanarSample(
            TEXTURE2D_ARGS(_MainTex, sampler_MainTex),
            p, WorldDirToMap(geoNormal), _BlendSharpness).rgb;
        float strength = _DetailStrength
                       * lerp(_DistantTextureDetailMultiplier, 1.0, fade);
        albedo *= lerp(half3(1, 1, 1), detail, strength);
    }

    // Cheap subsurface: rim-weighted glow, strongest where thin.
    // Not real transmission -- it ignores light direction.
    float fres = pow(1.0 - saturate(dot(normalWS, viewWS)), _RimPower);
#ifdef _SHADING_CEL
    fres = QuantizeBand(fres, _RimSteps, _BandSoftness);
#endif
    float thin = lerp(1.0, saturate(1.0 - h), _ThinGlow);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = albedo;
    surfaceData.specular   = _SpecTint.rgb;
    surfaceData.smoothness = saturate(_Glossiness + (h - 0.5) * _GlossVariation);
    surfaceData.occlusion  = lerp(1.0, saturate(h + 0.35), _OcclusionStrength);
    surfaceData.emission   = _SubsurfaceColor.rgb * _SubsurfaceStrength * fres * thin;
    surfaceData.alpha      = 1.0;

    InputData inputData = (InputData)0;
    inputData.positionWS      = positionWS;
    inputData.normalWS        = normalWS;
    inputData.viewDirectionWS = viewWS;
    inputData.shadowCoord     = TransformWorldToShadowCoord(positionWS);
    // Probe GI only: right for a moving cell, not for a lightmapped one.
    inputData.bakedGI         = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionHCS);
    inputData.shadowMask      = half4(1, 1, 1, 1);
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    // Per-vertex lights evaluated per pixel: there's no plain vertex
    // stage to do it in, and leaving it zero dropped them entirely.
    inputData.vertexLighting  = VertexLighting(positionWS, normalWS);
#endif

#ifdef _SHADING_CEL
    return CelShade(inputData, surfaceData);
#else
    return UniversalFragmentPBR(inputData, surfaceData).rgb;
#endif
}

half4 frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
{
#if defined(INVERT_BACKFACES)
    InvertSweepClip(input.positionWS, IS_FRONT_VFACE(face, true, false));
#endif
    STREAM_FADE_OBJECT(input.positionHCS);
    // Flip for back faces so Cull Off / Front light correctly.
    float3 geoNormal = normalize(input.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);

    float  fade   = DetailFade(input.positionWS);
    float4 hd     = SurfaceHeight(input.mapPos, fade);
    float4 ripple = float4(0.0, input.rippleGrad);

    float3 normalWS = BumpNormal(geoNormal, hd, ripple, fade);
    half3 rgb = CellShade(input.positionWS, input.positionHCS, input.mapPos, geoNormal, normalWS, hd.x, fade);
    return half4(MixFog(rgb, input.fogCoord), 1.0);
}

#endif

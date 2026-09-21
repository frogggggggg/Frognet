// Unity ShaderLab - Universal Render Pipeline
// Shares TriplanarCore.cginc with Custom/SeamlessTriplanarUnlit.
// Tessellated: works on low-poly meshes (Unity's sphere is ~768 tris).
Shader "Custom/BloodCellTriplanar"
{
    Properties
    {
        [Header(Color)]
        _Color ("Surface Color (ridges)", Color) = (0.80, 0.14, 0.13, 1)
        _DeepColor ("Deep Color (valleys)", Color) = (0.40, 0.04, 0.06, 1)
        _MainTex ("Detail Texture (optional)", 2D) = "white" {}
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.0

        [Header(Surface Relief)]
        _NoiseScale ("Lump Scale", Float) = 3.0
        _BumpStrength ("Bump Strength", Range(0,2)) = 0.35
        _Detail ("Fine Detail", Range(0,1)) = 0.55
        _Lacunarity ("Lacunarity", Range(1.5,4)) = 2.1
        _Gain ("Gain", Range(0.2,0.8)) = 0.5
        _Displace ("Vertex Displacement (world units)", Range(0,0.5)) = 0.05

        [Header(Tessellation)]
        _TessDensity ("Tessellation Density", Range(1,128)) = 24
        _TessMax ("Max Subdivision", Range(1,64)) = 24
        _PhongStrength ("Phong Smoothing", Range(0,1)) = 0.6

        [Header(Distance_Stability)]
        _DetailFadeStart ("Fine Detail Fade Start", Float) = 12
        _DetailFadeEnd ("Fine Detail Fade End", Float) = 40
        _DistantDetail ("Distant Fine Detail", Range(0,1)) = 0.0
        _DistantBumpMultiplier ("Distant Bump Multiplier", Range(0,1)) = 0.18
        _DistantDisplacementMultiplier ("Distant Displacement Multiplier", Range(0,1)) = 0.30
        _DistantTextureDetailMultiplier ("Distant Texture Detail Multiplier", Range(0,1)) = 0.15

        [Header(Rendering_Stability)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2

        [Header(Wetness)]
        _Glossiness ("Smoothness", Range(0,1)) = 0.70
        _GlossVariation ("Smoothness Variation", Range(0,0.5)) = 0.12
        _SpecTint ("Specular Tint (also tints reflections)", Color) = (0.17, 0.09, 0.09, 1)
        _OcclusionStrength ("Cavity Shading", Range(0,1)) = 0.45

        [Header(Subsurface)]
        _SubsurfaceColor ("Subsurface Color", Color) = (1.0, 0.22, 0.13, 1)
        _SubsurfaceStrength ("Subsurface Strength", Range(0,3)) = 0.9
        _RimPower ("Rim Falloff", Range(0.5,8)) = 2.6
        _ThinGlow ("Thin-Area Glow", Range(0,1)) = 0.6

        [Header(Cel Shading)]
        [KeywordEnum(Smooth, Cel)] _Shading ("Shading Mode", Float) = 0
        _LightBands ("Light Bands", Range(2,8)) = 3
        _BandSoftness ("Band Edge Softness", Range(0,0.25)) = 0.03
        _ColorSteps ("Surface Color Steps (1 = off)", Range(1,8)) = 1
        _RimSteps ("Rim Steps", Range(1,4)) = 2
        _SpecThreshold ("Specular Cutoff", Range(0,1)) = 0.55

        [Header(Impact Ripple)]
        _RippleAmplitude ("Ripple Amplitude", Float) = 0.12
        _RippleWavelength ("Ripple Wavelength", Float) = 0.7
        _RippleSpeed ("Ripple Speed", Float) = 2.5
        _RippleInitialRadius ("Initial Impact Radius", Float) = 0.12
        _RippleWidth ("Ripple Width", Float) = 0.9
        _RippleDecay ("Ripple Decay", Float) = 1.5

        [Header(Animation)]
        _PulseAmount ("Fluctuation Amount", Range(0,1)) = 0.0
        _PulseSpeed ("Fluctuation Speed", Range(0,6)) = 1.2
        _PulseVariation ("Fluctuation Variation", Range(0,2)) = 1.0

        [Header(Mapping)]
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1,32)) = 6.0
        [KeywordEnum(Object, World)] _Space ("Mapping Space", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }
        LOD 300

        HLSLINCLUDE

        // Specular workflow: lets the reflection be tinted red instead of
        // picking up a blue sky over near-black albedo.
        #define _SPECULAR_SETUP 1

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "TriplanarCore.cginc"

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4  _Color;
            half4  _DeepColor;
            half4  _SubsurfaceColor;
            half4  _SpecTint;
            float  _DetailStrength;
            float  _NoiseScale;
            float  _BumpStrength;
            float  _Detail;
            float  _Lacunarity;
            float  _Gain;
            float  _Displace;
            float  _TessDensity;
            float  _TessMax;
            float  _PhongStrength;
            float  _DetailFadeStart;
            float  _DetailFadeEnd;
            float  _DistantDetail;
            float  _DistantBumpMultiplier;
            float  _DistantDisplacementMultiplier;
            float  _DistantTextureDetailMultiplier;
            float  _Glossiness;
            float  _GlossVariation;
            float  _OcclusionStrength;
            float  _SubsurfaceStrength;
            float  _RimPower;
            float  _ThinGlow;
            float  _BlendSharpness;
            float  _PulseAmount;
            float  _PulseSpeed;
            float  _PulseVariation;
            float  _LightBands;
            float  _BandSoftness;
            float  _ColorSteps;
            float  _RimSteps;
            float  _SpecThreshold;
            float  _RippleAmplitude;
            float  _RippleWavelength;
            float  _RippleSpeed;
            float  _RippleInitialRadius;
            float  _RippleWidth;
            float  _RippleDecay;
        CBUFFER_END

        // Deliberately outside UnityPerMaterial: arrays cannot be declared in a
        // Properties block. Written per renderer from a MaterialPropertyBlock.
        // Start time (w) must be in the same clock as _Time.y, which is
        // Time.timeSinceLevelLoad -- NOT Time.time.
        #define RIPPLE_COUNT 4
        float4 _RipplePoints[RIPPLE_COUNT];   // xyz object-local impact point, w start time
        float4 _RippleValues[RIPPLE_COUNT];   // x strength

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        #define BARY3(a, b, c, w) ((a) * (w).x + (b) * (w).y + (c) * (w).z)

        // ---------------------------------------------------------------
        // Banding
        // ---------------------------------------------------------------

        // Flatten a 0..1 ramp into discrete steps. aa widens the edge so it
        // antialiases; pass 0 where derivatives are unavailable or undefined.
        float QuantizeBandW(float x, float bands, float softness, float aa)
        {
            float s = saturate(x) * bands;
            float i = floor(s);
            float w = clamp(max(softness * bands, aa), 1e-4, 0.5);
            return (i + smoothstep(1.0 - w, 1.0, s - i)) / bands;
        }

        // Fragment-only, and only in uniform control flow (fwidth).
        float QuantizeBand(float x, float bands, float softness)
        {
            return QuantizeBandW(x, bands, softness, fwidth(saturate(x) * bands));
        }

        // ---------------------------------------------------------------
        // Distance fade -- computed once per invocation, then reused.
        // World-distance based so it works in domain and fragment alike.
        // ---------------------------------------------------------------

        float DetailFade(float3 positionWS)
        {
            float s = max(0.0, _DetailFadeStart);
            float e = max(s + 0.001, _DetailFadeEnd);
            return 1.0 - smoothstep(s, e, distance(positionWS, GetCameraPositionWS()));
        }

        // ---------------------------------------------------------------
        // Height field
        // ---------------------------------------------------------------

        // One noise evaluation yields height and its exact gradient; callers
        // that only read .x let the compiler strip the derivative math.
        //
        // Fluctuation scales the field about its midpoint (lumps swell in
        // place instead of sliding), with phase driven by the height itself so
        // neighbouring lumps fall out of sync.
        float4 SurfaceHeight(float3 p, float fade)
        {
            float detail = saturate(lerp(_DistantDetail, _Detail, fade));
            float4 n  = FBM3D(p * _NoiseScale, _Gain, _Lacunarity, detail);
            float4 hd = float4(n.x, n.yzw * _NoiseScale);   // chain rule

            if (_PulseAmount <= 0.0)
                return hd;

            float centered = hd.x - 0.5;
            float phaseMul = TWO_PI * _PulseVariation;
            float s, c;
            sincos(_Time.y * _PulseSpeed + hd.x * phaseMul, s, c);
            float k    = 1.0 + _PulseAmount * s;
            float dkdh = _PulseAmount * c * phaseMul;

            return float4(0.5 + centered * k, hd.yzw * (k + centered * dkdh));
        }

        // ---------------------------------------------------------------
        // Mapping, made independent of Transform scale
        // ---------------------------------------------------------------

        float3 ObjectScale()
        {
            return float3(length(unity_ObjectToWorld._m00_m10_m20),
                          length(unity_ObjectToWorld._m01_m11_m21),
                          length(unity_ObjectToWorld._m02_m12_m22));
        }

        // With scale divided out, object->world is a pure rotation.
        float3x3 MapToWorldRotation()
        {
            float3 s = max(ObjectScale(), 1e-5);
            return float3x3(unity_ObjectToWorld._m00_m01_m02 / s,
                            unity_ObjectToWorld._m10_m11_m12 / s,
                            unity_ObjectToWorld._m20_m21_m22 / s);
        }

        float3 MapDirToWorld(float3 v)
        {
        #ifdef _SPACE_WORLD
            return v;
        #else
            return mul(MapToWorldRotation(), v);
        #endif
        }

        float3 WorldDirToMap(float3 v)
        {
        #ifdef _SPACE_WORLD
            return v;
        #else
            return mul(v, MapToWorldRotation());   // R^-1 == R^T
        #endif
        }

        // Object mode multiplies by scale so lump size is in world units.
        float3 MapPosition(float3 positionOS, float3 positionWS)
        {
        #ifdef _SPACE_WORLD
            return positionWS;
        #else
            return positionOS * ObjectScale();
        #endif
        }

        // Impacts are stored object-local, so they ride along with the cell.
        float3 RipplePointToMap(float3 localImpactPoint)
        {
        #ifdef _SPACE_WORLD
            return TransformObjectToWorld(localImpactPoint);
        #else
            return localImpactPoint * ObjectScale();
        #endif
        }

        // ---------------------------------------------------------------
        // Impact ripple. Returns (height, map-space gradient).
        //
        // Causal: nothing ahead of the wavefront moves. The wave is
        //   w(b) = cos(kb) * exp(-b^2/W^2) * ramp(b),   b = front - dist
        // The ramp (smoothstep over a quarter wavelength) makes the front
        // continuous; without it the front was a full-amplitude step that
        // tore a visible travelling seam into both geometry and shading.
        // ---------------------------------------------------------------
        float4 Ripple(float3 mapPos)
        {
            float4 result  = 0.0;
            float  wl      = max(_RippleWavelength, 1e-3);
            float  k       = TWO_PI / wl;
            float  invW2   = 1.0 / max(_RippleWidth * _RippleWidth, 1e-4);
            float  rampLen = wl * 0.25;
            float  radius0 = max(0.0, _RippleInitialRadius);

            [unroll]
            for (int i = 0; i < RIPPLE_COUNT; i++)
            {
                float strength = _RippleValues[i].x;
                float age      = _Time.y - _RipplePoints[i].w;
                if (strength <= 0.0 || age < 0.0)
                    continue;

                float3 offset = mapPos - RipplePointToMap(_RipplePoints[i].xyz);
                float  dist   = length(offset);
                float  behind = radius0 + age * _RippleSpeed - dist;
                if (behind <= 0.0)
                    continue;

                float amp   = strength * _RippleAmplitude * exp(-age * _RippleDecay);
                float env   = exp(-behind * behind * invW2);
                float t     = saturate(behind / rampLen);
                float ramp  = t * t * (3.0 - 2.0 * t);
                float dramp = 6.0 * t * (1.0 - t) / rampLen;
                float s, c;
                sincos(k * behind, s, c);

                result.x += amp * c * env * ramp;

                // dw/db, then chain through db/dp = -offset/dist.
                float dw = env * ((-k * s - 2.0 * behind * invW2 * c) * ramp + c * dramp);
                result.yzw -= amp * dw * offset / max(dist, 1e-4);
            }
            return result;
        }

        // Bump the geometric normal by the combined height gradient, projected
        // onto the tangent plane so it tilts rather than inflates. Ripple
        // gradient is already in world height per unit, so it bypasses
        // _BumpStrength and matches the geometry it displaced.
        float3 BumpNormal(float3 geoNormalWS, float4 hd, float4 ripple, float fade)
        {
            float  bump   = _BumpStrength * lerp(_DistantBumpMultiplier, 1.0, fade);
            float3 gradWS = MapDirToWorld(hd.yzw * bump + ripple.yzw);
            return normalize(geoNormalWS - (gradWS - geoNormalWS * dot(gradWS, geoNormalWS)));
        }

        // ---------------------------------------------------------------
        // Tessellation (shared by every pass)
        // ---------------------------------------------------------------

        struct TessAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
        };

        struct TessControlPoint
        {
            float4 positionOS : INTERNALTESSPOS;
            float3 normalOS   : NORMAL;
        };

        struct TessFactors
        {
            float edge[3] : SV_TessFactor;
            float inside  : SV_InsideTessFactor;
        };

        TessControlPoint TessVertex(TessAttributes input)
        {
            TessControlPoint cp;
            cp.positionOS = input.positionOS;
            cp.normalOS   = input.normalOS;
            return cp;
        }

        // Screen-relative density: long edges near the camera subdivide most.
        float EdgeFactor(float3 p0WS, float3 p1WS)
        {
            float len  = distance(p0WS, p1WS);
            float dist = distance((p0WS + p1WS) * 0.5, GetCameraPositionWS());
            return clamp(_TessDensity * len / max(dist, 0.001), 1.0, _TessMax);
        }

        // No manual patch culling: displacement makes source-triangle tests
        // unsafe, and the shadow pass would cull with the wrong frustum.
        TessFactors PatchConstant(InputPatch<TessControlPoint, 3> patch)
        {
            float3 p0 = TransformObjectToWorld(patch[0].positionOS.xyz);
            float3 p1 = TransformObjectToWorld(patch[1].positionOS.xyz);
            float3 p2 = TransformObjectToWorld(patch[2].positionOS.xyz);

            // Edge factors depend only on the edge's two vertices, so
            // neighbouring triangles agree and no cracks open.
            TessFactors f;
            f.edge[0] = EdgeFactor(p1, p2);
            f.edge[1] = EdgeFactor(p2, p0);
            f.edge[2] = EdgeFactor(p0, p1);
            f.inside  = (f.edge[0] + f.edge[1] + f.edge[2]) * (1.0 / 3.0);
            return f;
        }

        [domain("tri")]
        [outputcontrolpoints(3)]
        [outputtopology("triangle_cw")]
        [partitioning("fractional_odd")]
        [patchconstantfunc("PatchConstant")]
        TessControlPoint hull(InputPatch<TessControlPoint, 3> patch,
                              uint id : SV_OutputControlPointID)
        {
            return patch[id];
        }

        // Phong tessellation: pull generated vertices toward the control
        // points' tangent planes so a low-poly sphere actually rounds out.
        float3 PhongProject(float3 p, float3 cp, float3 n)
        {
            return p - dot(p - cp, n) * n;
        }

        struct CellSample
        {
            float3 positionWS;   // displaced
            float3 normalWS;     // geometric, undisplaced
            float3 mapPos;       // noise coordinate of the undisplaced base
            float  fade;
        };

        // Everything every pass needs from the domain stage: resolve the patch,
        // then displace along the true world normal (so _Displace is in world
        // units and survives non-uniform scale).
        CellSample EvaluateCell(const OutputPatch<TessControlPoint, 3> patch, float3 bary)
        {
            float3 p0 = patch[0].positionOS.xyz, n0 = patch[0].normalOS;
            float3 p1 = patch[1].positionOS.xyz, n1 = patch[1].normalOS;
            float3 p2 = patch[2].positionOS.xyz, n2 = patch[2].normalOS;

            float3 flat      = BARY3(p0, p1, p2, bary);
            float3 projected = BARY3(PhongProject(flat, p0, n0),
                                     PhongProject(flat, p1, n1),
                                     PhongProject(flat, p2, n2), bary);
            float3 positionOS = lerp(flat, projected, _PhongStrength);
            float3 normalOS   = BARY3(n0, n1, n2, bary);

            CellSample c;
            float3 baseWS = TransformObjectToWorld(positionOS);
            c.normalWS    = normalize(TransformObjectToWorldNormal(normalOS));
            c.mapPos      = MapPosition(positionOS, baseWS);
            c.fade        = DetailFade(baseWS);

            // Distant relief is simplified so tessellated vertices don't crawl.
            // Ripples stay full strength: they're gameplay feedback.
            float h      = SurfaceHeight(c.mapPos, c.fade).x;
            float offset = (h - 0.5) * _Displace
                         * lerp(_DistantDisplacementMultiplier, 1.0, c.fade)
                         + Ripple(c.mapPos).x;

            c.positionWS = baseWS + c.normalWS * offset;
            return c;
        }

        // ---------------------------------------------------------------
        // Shared depth / depth-normals stages
        // ---------------------------------------------------------------

        struct DepthVaryings
        {
            float4 positionHCS : SV_POSITION;
        };

        [domain("tri")]
        DepthVaryings DepthDomain(TessFactors factors,
                                  const OutputPatch<TessControlPoint, 3> patch,
                                  float3 bary : SV_DomainLocation)
        {
            DepthVaryings o;
            o.positionHCS = TransformWorldToHClip(EvaluateCell(patch, bary).positionWS);
            return o;
        }

        // Matches URP's DepthOnly: some platforms copy depth through the R
        // channel, so this must write depth, not 0.
        half4 DepthFrag(DepthVaryings input) : SV_Target
        {
            return input.positionHCS.z;
        }

        struct NormalVaryings
        {
            float4 positionHCS : SV_POSITION;
            float3 normalWS    : TEXCOORD0;
        };

        // Per-vertex bumped normal: close enough for SSAO etc. on a densely
        // tessellated surface, at a fraction of per-pixel fBm cost.
        [domain("tri")]
        NormalVaryings DepthNormalsDomain(TessFactors factors,
                                          const OutputPatch<TessControlPoint, 3> patch,
                                          float3 bary : SV_DomainLocation)
        {
            CellSample c = EvaluateCell(patch, bary);
            NormalVaryings o;
            o.positionHCS = TransformWorldToHClip(c.positionWS);
            o.normalWS    = BumpNormal(c.normalWS, SurfaceHeight(c.mapPos, c.fade),
                                       Ripple(c.mapPos), c.fade);
            return o;
        }

        half4 DepthNormalsFrag(NormalVaryings input) : SV_Target
        {
            float3 n = normalize(input.normalWS);
        #if defined(_GBUFFER_NORMALS_OCT)
            float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
            return half4(PackFloat2To888(oct), 0.0);
        #else
            return half4(n, 0.0);
        #endif
        }

        ENDHLSL

        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex TessVertex
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD
            #pragma shader_feature_local_fragment _SHADING_SMOOTH _SHADING_CEL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 mapPos      : TEXCOORD2;
                float  fogCoord    : TEXCOORD3;
            };

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                CellSample c = EvaluateCell(patch, bary);
                Varyings o;
                o.positionWS  = c.positionWS;
                o.normalWS    = c.normalWS;
                o.mapPos      = c.mapPos;
                o.positionHCS = TransformWorldToHClip(c.positionWS);
                o.fogCoord    = ComputeFogFactor(o.positionHCS.z);
                return o;
            }

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

            half4 frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float3 p = input.mapPos;

                // Flip for back faces so Cull Off / Front light correctly.
                float3 geoNormal = normalize(input.normalWS) * IS_FRONT_VFACE(face, 1.0, -1.0);

                float  fade   = DetailFade(input.positionWS);
                float4 hd     = SurfaceHeight(p, fade);
                float4 ripple = Ripple(p);
                float  h      = hd.x;

                float3 normalWS = BumpNormal(geoNormal, hd, ripple, fade);
                float3 viewWS   = GetWorldSpaceNormalizeViewDir(input.positionWS);

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
                inputData.positionWS      = input.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = viewWS;
                inputData.shadowCoord     = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord        = input.fogCoord;
                // Probe GI only: right for a moving cell, not for a lightmapped one.
                inputData.bakedGI         = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionHCS);
                inputData.shadowMask      = half4(1, 1, 1, 1);
            #ifdef _ADDITIONAL_LIGHTS_VERTEX
                // Per-vertex lights evaluated per pixel: there's no plain vertex
                // stage to do it in, and leaving it zero dropped them entirely.
                inputData.vertexLighting  = VertexLighting(input.positionWS, normalWS);
            #endif

            #ifdef _SHADING_CEL
                half3 rgb = CelShade(inputData, surfaceData);
            #else
                half3 rgb = UniversalFragmentPBR(inputData, surfaceData).rgb;
            #endif

                return half4(MixFog(rgb, inputData.fogCoord), 1.0);
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        // Tessellated too, so the shadow matches the displaced silhouette.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex TessVertex
            #pragma hull hull
            #pragma domain domain
            #pragma fragment DepthFrag
            #pragma target 4.6
            #pragma require tessellation
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            [domain("tri")]
            DepthVaryings domain(TessFactors factors,
                                 const OutputPatch<TessControlPoint, 3> patch,
                                 float3 bary : SV_DomainLocation)
            {
                CellSample c = EvaluateCell(patch, bary);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - c.positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(c.positionWS, c.normalWS, lightDirectionWS));

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                DepthVaryings o;
                o.positionHCS = positionCS;
                return o;
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ZTest LEqual
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex TessVertex
            #pragma hull hull
            #pragma domain DepthDomain
            #pragma fragment DepthFrag
            #pragma target 4.6
            #pragma require tessellation
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD
            ENDHLSL
        }

        // -------------------------------------------------------------------
        // URP may build _CameraDepthTexture from a depth+normals prepass.
        // Same tessellation + displacement as the visible surface.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex TessVertex
            #pragma hull hull
            #pragma domain DepthNormalsDomain
            #pragma fragment DepthNormalsFrag
            #pragma target 4.6
            #pragma require tessellation
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }

        // Alias for URP versions/features that request DepthNormalsOnly.
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex TessVertex
            #pragma hull hull
            #pragma domain DepthNormalsDomain
            #pragma fragment DepthNormalsFrag
            #pragma target 4.6
            #pragma require tessellation
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}

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

        // Deliberately outside UnityPerMaterial. Arrays cannot be declared in
        // a ShaderLab Properties block, and anything in that buffer which the
        // Properties block does not declare drops the shader out of SRP
        // batching. These are written per renderer from a MaterialPropertyBlock.
        #define RIPPLE_COUNT 4
        float4 _RipplePoints[RIPPLE_COUNT];   // xyz object-local impact point, w start time
        float4 _RippleValues[RIPPLE_COUNT];   // x strength

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        #define BARY3(a, b, c, w) ((a) * (w).x + (b) * (w).y + (c) * (w).z)

        // Flatten a 0..1 ramp into discrete steps. The transition sits at the
        // band boundary and is widened by fwidth, so the edge antialiases
        // instead of crawling as the surface moves.
        float QuantizeBand(float x, float bands, float softness)
        {
            float s = saturate(x) * bands;
            float i = floor(s);
            float f = s - i;
            float w = clamp(max(softness * bands, fwidth(s)), 1e-4, 0.5);
            return (i + smoothstep(1.0 - w, 1.0, f)) / bands;
        }

        // One noise evaluation yields height and its exact gradient. Height()
        // alone lets the compiler strip the derivative math it does not use.
        //
        // Fluctuation scales the height field about its midpoint rather than
        // sliding the noise coordinate. Offsetting the coordinate can only
        // translate the pattern -- lumps swell and subside where they already
        // are instead of travelling across the surface.
        //
        // The phase is driven by the height itself, so neighbouring lumps fall
        // out of sync. At variation 0 the whole surface breathes as one, which
        // reads as the object scaling rather than as a living membrane.
        float4 HeightDWithDetail(float3 p, float detailAmount)
        {
            float4 n  = FBM3D(
                p * _NoiseScale,
                _Gain,
                _Lacunarity,
                saturate(detailAmount));

            float4 hd = float4(
                n.x,
                n.yzw * _NoiseScale);   // chain rule

            if (_PulseAmount <= 0.0)
                return hd;

            float h        = hd.x;
            float centered = h - 0.5;
            float phase    = _Time.y * _PulseSpeed + h * TWO_PI * _PulseVariation;
            float k        = 1.0 + _PulseAmount * sin(phase);
            float dkdh     = _PulseAmount * cos(phase) * TWO_PI * _PulseVariation;

            return float4(
                0.5 + centered * k,
                hd.yzw * (k + centered * dkdh));
        }

        float4 HeightD(float3 p)
        {
            return HeightDWithDetail(
                p,
                _Detail);
        }

        float Height(float3 p)
        {
            return HeightD(p).x;
        }

        // 1 close to the camera, 0 after Detail Fade End.
        // This is deliberately world-distance based so it works in vertex,
        // domain and fragment stages alike; screen derivatives are unavailable
        // in the tessellation domain shader.
        float SurfaceDetailFade(float3 positionWS)
        {
            float startDistance =
                max(0.0, _DetailFadeStart);

            float endDistance =
                max(
                    startDistance + 0.001,
                    _DetailFadeEnd);

            float distanceToCamera =
                distance(
                    positionWS,
                    GetCameraPositionWS());

            return 1.0 -
                smoothstep(
                    startDistance,
                    endDistance,
                    distanceToCamera);
        }

        float SurfaceDetailAmount(float3 positionWS)
        {
            float fade =
                SurfaceDetailFade(
                    positionWS);

            return lerp(
                _DistantDetail,
                _Detail,
                fade);
        }

        float SurfaceBumpMultiplier(float3 positionWS)
        {
            return lerp(
                _DistantBumpMultiplier,
                1.0,
                SurfaceDetailFade(positionWS));
        }

        float SurfaceDisplacementMultiplier(float3 positionWS)
        {
            return lerp(
                _DistantDisplacementMultiplier,
                1.0,
                SurfaceDetailFade(positionWS));
        }

        float SurfaceTextureDetailMultiplier(float3 positionWS)
        {
            return lerp(
                _DistantTextureDetailMultiplier,
                1.0,
                SurfaceDetailFade(positionWS));
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

        // With scale divided out, object->world is a pure rotation -- which is
        // all that separates map space from world space.
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
            // A rotation's inverse is its transpose: mul(v, R) == mul(R^T, v).
            return mul(v, MapToWorldRotation());
        #endif
        }

        // Impact points are captured ONCE in the renderer's object-local space.
        // That makes the ripple physically belong to the cell: translating or
        // rotating the cell later carries the old impact with it exactly like a
        // mark painted on the mesh.
        //
        // Object mapping uses the same scale-corrected local coordinates as the
        // surface noise. World mapping converts the stored LOCAL point through
        // the object's CURRENT transform, so even world-mapped materials keep
        // the impact attached to a moving cell.
        float3 RipplePointToMap(float3 localImpactPoint)
        {
        #ifdef _SPACE_WORLD
            return TransformObjectToWorld(localImpactPoint);
        #else
            return localImpactPoint * ObjectScale();
        #endif
        }

        // Causal expanding impact wave.
        //
        // Nothing outside the current wavefront is allowed to move. The old
        // Gaussian packet had a non-zero tail in front of the ring, so a large
        // Ripple Width could make the whole cell react immediately.
        //
        // "behind" is zero at the travelling front and positive only after the
        // wave has physically reached a point.
        float Ripple(float3 mapPos, out float3 gradient)
        {
            float total = 0.0;
            gradient = float3(0.0, 0.0, 0.0);

            float wavelength = max(_RippleWavelength, 1e-3);
            float k = TWO_PI / wavelength;
            float widthSq = max(_RippleWidth * _RippleWidth, 1e-4);

            [unroll]
            for (int i = 0; i < RIPPLE_COUNT; i++)
            {
                float strength = _RippleValues[i].x;
                float age = _Time.y - _RipplePoints[i].w;

                if (strength <= 0.0 || age < 0.0)
                    continue;

                float3 offset =
                    mapPos -
                    RipplePointToMap(_RipplePoints[i].xyz);

                float rawDist = length(offset);
                float dist = max(rawDist, 1e-4);

                // Start with a small visible contact patch rather than a
                // mathematically zero-radius ring. On a tessellated surface this
                // avoids waiting for the travelling front to reach the nearest
                // generated vertex before anything can be seen.
                float frontRadius =
                    max(0.0, _RippleInitialRadius) +
                    age *
                    _RippleSpeed;

                // Positive only where the travelling wave has already arrived.
                float behind =
                    frontRadius -
                    rawDist;

                // Strict causal boundary is still preserved beyond the small
                // initial contact radius.
                if (behind < 0.0)
                    continue;

                float envelope =
                    exp(
                        -(behind * behind) /
                        widthSq);

                float amplitude =
                    strength *
                    _RippleAmplitude *
                    exp(
                        -age *
                        _RippleDecay);

                float phase =
                    k *
                    behind;

                float cosine =
                    cos(phase);

                float sine =
                    sin(phase);

                // A cosine starts with a crest at the impact/wavefront instead
                // of requiring half a cycle before anything visibly happens.
                float wave =
                    cosine *
                    envelope;

                total +=
                    amplitude *
                    wave;

                // wave(b) = cos(kb) * exp(-b^2/w^2)
                // b = frontRadius - distance
                //
                // d(b)/d(position) = -offset / distance. Combining that with
                // d(wave)/db gives the outward gradient below.
                float slope =
                    (
                        k * sine +
                        cosine *
                        (2.0 * behind / widthSq)
                    ) *
                    envelope;

                gradient +=
                    amplitude *
                    slope *
                    (offset / dist);
            }

            return total;
        }

        // Noise coordinate. Object mode multiplies by scale so lump size is
        // measured in world units: scaling the mesh yields more lumps rather
        // than bigger ones, matching what world mode already did.
        float3 MapPosition(float3 positionOS, float3 positionWS)
        {
        #ifdef _SPACE_WORLD
            return positionWS;
        #else
            return positionOS * ObjectScale();
        #endif
        }

        // Displacement runs in world space along the true surface normal, so
        // _Displace is in world units and stays correct under non-uniform
        // scale, where an object-space normal is not perpendicular.
        float3 DisplaceWS(float3 positionWS, float3 normalWS, float3 mapPos)
        {
            // Fine procedural relief becomes sub-pixel at distance. Continuing
            // to displace full-strength there makes tessellated vertices crawl
            // as the camera moves, so progressively simplify the base relief.
            float4 hd =
                HeightDWithDetail(
                    mapPos,
                    SurfaceDetailAmount(positionWS));

            float displacementMultiplier =
                SurfaceDisplacementMultiplier(
                    positionWS);

            // Ripples stay full-strength. They are gameplay feedback rather
            // than static micro-detail and should remain readable.
            float3 rippleGradient;
            float ripple =
                Ripple(
                    mapPos,
                    rippleGradient);

            float offset =
                (hd.x - 0.5) *
                _Displace *
                displacementMultiplier +
                ripple;

            return
                positionWS +
                normalWS *
                offset;
        }

        // ---------------------------------------------------------------
        // Tessellation
        // ---------------------------------------------------------------

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

        // Phong tessellation: pull each generated vertex toward the tangent
        // planes of the three control points. Without this, subdividing a
        // low-poly sphere just puts more vertices on the same flat facets.
        float3 PhongProject(float3 p, float3 controlPoint, float3 n)
        {
            return p - dot(p - controlPoint, n) * n;
        }

        float3 PhongTessellate(float3 p, float3 p0, float3 p1, float3 p2,
                               float3 n0, float3 n1, float3 n2, float3 bary)
        {
            float3 projected = BARY3(PhongProject(p, p0, n0),
                                     PhongProject(p, p1, n1),
                                     PhongProject(p, p2, n2), bary);
            return lerp(p, projected, _PhongStrength);
        }

        // Screen-relative density: long edges near the camera subdivide most.
        float EdgeFactor(float3 p0WS, float3 p1WS)
        {
            float len  = distance(p0WS, p1WS);
            float dist = distance((p0WS + p1WS) * 0.5, GetCameraPositionWS());
            return clamp(_TessDensity * len / max(dist, 0.001), 1.0, _TessMax);
        }

        // Do NOT manually cull tessellation patches here.
        //
        // The old code rejected a patch when all three vertices were outside
        // *some* frustum plane, even when they were outside different planes.
        // A triangle spanning the visible frustum could therefore disappear at
        // certain view angles. Displacement/ripples also make source-triangle
        // clip tests unsafe. Let normal GPU clipping handle visibility.

        TessFactors PatchConstant(InputPatch<TessControlPoint, 3> patch)
        {
            float3 p0 = TransformObjectToWorld(patch[0].positionOS.xyz);
            float3 p1 = TransformObjectToWorld(patch[1].positionOS.xyz);
            float3 p2 = TransformObjectToWorld(patch[2].positionOS.xyz);

            TessFactors f;

            // Each edge factor is shared with the neighbouring triangle, so
            // both sides must compute the same value or cracks open along it.
            f.edge[0] = EdgeFactor(p1, p2);
            f.edge[1] = EdgeFactor(p2, p0);
            f.edge[2] = EdgeFactor(p0, p1);
            f.inside  = (f.edge[0] + f.edge[1] + f.edge[2]) / 3.0;
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

        // Interpolate a patch down to one smoothed object-space vertex. Not
        // displaced yet: each pass maps the noise from this base position, so
        // shading samples the same coordinate the geometry was pushed from.
        void ResolvePatch(const OutputPatch<TessControlPoint, 3> patch, float3 bary,
                          out float3 positionOS, out float3 normalOS)
        {
            float3 p0 = patch[0].positionOS.xyz;
            float3 p1 = patch[1].positionOS.xyz;
            float3 p2 = patch[2].positionOS.xyz;
            float3 n0 = patch[0].normalOS;
            float3 n1 = patch[1].normalOS;
            float3 n2 = patch[2].normalOS;

            normalOS   = normalize(BARY3(n0, n1, n2, bary));
            positionOS = PhongTessellate(BARY3(p0, p1, p2, bary),
                                         p0, p1, p2, n0, n1, n2, bary);
        }

        // The vertex stage is now a pass-through; real work happens in domain.
        TessControlPoint TessVert(float4 positionOS, float3 normalOS)
        {
            TessControlPoint cp;
            cp.positionOS = positionOS;
            cp.normalOS   = normalOS;
            return cp;
        }

        ENDHLSL

        // -------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            // Explicit rather than relying on ShaderLab defaults. This keeps the
            // tessellated/displaced cell in the camera depth buffer when URP
            // copies depth after the opaque pass.
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD
            #pragma shader_feature_local_fragment _SHADING_SMOOTH _SHADING_CEL

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 mapPos      : TEXCOORD2;
                float  fogCoord    : TEXCOORD3;
            };

            TessControlPoint vert(Attributes input)
            {
                return TessVert(input.positionOS, input.normalOS);
            }

            Varyings BuildVaryings(float3 basePositionOS, float3 normalOS)
            {
                Varyings output = (Varyings)0;

                float3 basePositionWS = TransformObjectToWorld(basePositionOS);
                output.normalWS = normalize(TransformObjectToWorldNormal(normalOS));

                // Noise coordinate comes from the undisplaced base position.
                output.mapPos = MapPosition(basePositionOS, basePositionWS);

                output.positionWS  = DisplaceWS(basePositionWS, output.normalWS,
                                                output.mapPos);
                output.positionHCS = TransformWorldToHClip(output.positionWS);
                output.fogCoord    = ComputeFogFactor(output.positionHCS.z);
                return output;
            }

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 positionOS, normalOS;
                ResolvePatch(patch, bary, positionOS, normalOS);
                return BuildVaryings(positionOS, normalOS);
            }

            // Toon lighting: the same lights PBR would use, but N.L quantized
            // into flat bands instead of a continuous ramp.
            half3 CelShade(InputData inputData, SurfaceData surfaceData)
            {
                float3 N = inputData.normalWS;
                float3 V = inputData.viewDirectionWS;
                half3  albedo = surfaceData.albedo;

                float specPower = exp2(surfaceData.smoothness * 11.0) + 2.0;
                half3 accum = half3(0, 0, 0);

                Light mainLight = GetMainLight(inputData.shadowCoord);
                float lit  = saturate(dot(N, mainLight.direction))
                           * mainLight.shadowAttenuation;
                float band = QuantizeBand(lit, _LightBands, _BandSoftness);
                accum += albedo * mainLight.color * band;

                // Hard-edged highlight, gated by the lit band so it can never
                // show up on a face turned away from the light.
                float3 H    = normalize(mainLight.direction + V);
                float  spec = pow(saturate(dot(N, H)), specPower);
                float  sw   = clamp(max(_BandSoftness, fwidth(spec)), 1e-4, 0.5);
                accum += mainLight.color * surfaceData.specular
                       * smoothstep(_SpecThreshold - sw, _SpecThreshold + sw, spec)
                       * step(0.001, band);

            #ifdef _ADDITIONAL_LIGHTS
                uint lightCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightCount)
                    Light light = GetAdditionalLight(lightIndex, inputData.positionWS,
                                                     inputData.shadowMask);
                    float l = saturate(dot(N, light.direction))
                            * light.distanceAttenuation * light.shadowAttenuation;
                    accum += albedo * light.color
                           * QuantizeBand(l, _LightBands, _BandSoftness);
                LIGHT_LOOP_END
            #endif

                // Ambient stays flat. Running GI through the bands would
                // reintroduce the smooth falloff the bands exist to remove.
                accum += albedo * inputData.bakedGI * surfaceData.occlusion;
                return accum + surfaceData.emission;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 p         = input.mapPos;
                float3 geoNormal = normalize(input.normalWS);

                float detailFade =
                    SurfaceDetailFade(
                        input.positionWS);

                // Fade high-frequency fBm before it becomes smaller than a
                // pixel. This removes distant crawling/shimmer while preserving
                // the broad cell shape.
                float4 hd =
                    HeightDWithDetail(
                        p,
                        SurfaceDetailAmount(
                            input.positionWS));

                float h =
                    hd.x;

                // Ripple gradient is already in world height per unit, so it
                // is added at full weight rather than through _BumpStrength --
                // that way the shading matches the geometry the ripple
                // actually displaced.
                float3 rippleGradient;
                Ripple(p, rippleGradient);

                // Project onto the tangent plane so the bump slides the normal
                // sideways instead of inflating it.
                float3 gradWS =
                    MapDirToWorld(
                        hd.yzw *
                        _BumpStrength *
                        SurfaceBumpMultiplier(input.positionWS) +
                        rippleGradient);
                float3 tangentialGrad = gradWS - geoNormal * dot(gradWS, geoNormal);
                float3 normalWS = normalize(geoNormal - tangentialGrad);

                float3 viewWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

                // Ridges read as thicker haemoglobin, valleys as thinner.
                // Quantizing this ramp gives flat colour divisions on the
                // surface itself, separately from how it is lit -- so it works
                // in either shading mode. A uniform branch, so 1 costs nothing.
                float hAlbedo = h;
                if (_ColorSteps > 1.0)
                    hAlbedo = QuantizeBand(h, _ColorSteps, _BandSoftness);

                half3 albedo = lerp(_DeepColor.rgb, _Color.rgb, hAlbedo);

                if (_DetailStrength > 0.0)
                {
                    half3 detail = TriplanarSample(
                        TEXTURE2D_ARGS(_MainTex, sampler_MainTex),
                        p, WorldDirToMap(geoNormal), _BlendSharpness).rgb;
                    float visibleDetailStrength =
                        _DetailStrength *
                        SurfaceTextureDetailMultiplier(
                            input.positionWS);

                    albedo *=
                        lerp(
                            half3(1,1,1),
                            detail,
                            visibleDetailStrength);
                }

                // Cheap subsurface: rim-weighted glow, strongest where thin.
                // Not real transmission -- it does not know the light
                // direction, so it will not go dark when backlit from behind.
                float fres = pow(1.0 - saturate(dot(normalWS, viewWS)), _RimPower);
            #ifdef _SHADING_CEL
                fres = QuantizeBand(fres, _RimSteps, _BandSoftness);
            #endif
                float thin = lerp(1.0, saturate(1.0 - h), _ThinGlow);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo;
                surfaceData.specular   = _SpecTint.rgb;
                surfaceData.metallic   = 0.0;
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
                // Probe-based GI only. Correct for a moving cell; a lightmapped
                // static one would need the LIGHTMAP_ON path instead.
                inputData.bakedGI         = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV =
                    GetNormalizedScreenSpaceUV(input.positionHCS);
                inputData.shadowMask      = half4(1,1,1,1);

            #ifdef _SHADING_CEL
                half3 rgb = CelShade(inputData, surfaceData);
            #else
                half3 rgb = UniversalFragmentPBR(inputData, surfaceData).rgb;
            #endif

                rgb = MixFog(rgb, inputData.fogCoord);
                return half4(rgb, 1.0);
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
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            TessControlPoint vert(Attributes input)
            {
                return TessVert(input.positionOS, input.normalOS);
            }

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 basePositionOS, normalOS;
                ResolvePatch(patch, bary, basePositionOS, normalOS);

                float3 basePositionWS = TransformObjectToWorld(basePositionOS);
                float3 normalWS   = normalize(TransformObjectToWorldNormal(normalOS));
                float3 mapPos     = MapPosition(basePositionOS, basePositionWS);
                float3 positionWS = DisplaceWS(basePositionWS, normalWS, mapPos);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif

                Varyings output;
                output.positionHCS = positionCS;
                return output;
            }

            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        // Keeps the displaced silhouette correct in a depth prepass -- this
        // project already runs one for the player stencil.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation
            #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            TessControlPoint vert(Attributes input)
            {
                return TessVert(input.positionOS, input.normalOS);
            }

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 basePositionOS, normalOS;
                ResolvePatch(patch, bary, basePositionOS, normalOS);

                float3 basePositionWS = TransformObjectToWorld(basePositionOS);
                float3 normalWS = normalize(TransformObjectToWorldNormal(normalOS));
                float3 mapPos   = MapPosition(basePositionOS, basePositionWS);

                Varyings output;
                output.positionHCS = TransformWorldToHClip(
                    DisplaceWS(basePositionWS, normalWS, mapPos));
                return output;
            }

            half4 frag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }
        // -------------------------------------------------------------------
        // URP may generate _CameraDepthTexture as part of a depth+normals
        // prepass rather than a plain DepthOnly prepass. Built-in URP shaders
        // provide this pass; without it this material can disappear from the
        // camera depth texture even though its ForwardLit and DepthOnly passes
        // are otherwise correct.
        //
        // This pass repeats the SAME tessellation + displacement used by the
        // visible surface, so screen-space effects see the real displaced cell.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation
            #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
            };

            TessControlPoint vert(Attributes input)
            {
                return TessVert(input.positionOS, input.normalOS);
            }

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 basePositionOS, normalOS;
                ResolvePatch(patch, bary, basePositionOS, normalOS);

                float3 basePositionWS = TransformObjectToWorld(basePositionOS);
                float3 geoNormalWS = normalize(TransformObjectToWorldNormal(normalOS));
                float3 mapPos = MapPosition(basePositionOS, basePositionWS);

                // Same displaced position as ForwardLit / DepthOnly.
                float3 positionWS = DisplaceWS(basePositionWS, geoNormalWS, mapPos);

                // Also match the procedural surface normal used by ForwardLit.
                float4 hd =
                    HeightDWithDetail(
                        mapPos,
                        SurfaceDetailAmount(positionWS));
                float3 rippleGradient;
                Ripple(mapPos, rippleGradient);

                float3 gradWS = MapDirToWorld(
                    hd.yzw *
                    _BumpStrength *
                    SurfaceBumpMultiplier(positionWS) +
                    rippleGradient);

                float3 tangentialGrad =
                    gradWS - geoNormalWS * dot(gradWS, geoNormalWS);

                Varyings output;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.normalWS = normalize(geoNormalWS - tangentialGrad);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);

            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS =
                    saturate(octNormalWS * 0.5 + 0.5);
                half3 packedNormalWS =
                    PackFloat2To888(remappedOctNormalWS);
                return half4(packedNormalWS, 0.0);
            #else
                return half4(normalWS, 0.0);
            #endif
            }
            ENDHLSL
        }

        // Some URP versions/features request DepthNormalsOnly instead of
        // DepthNormals. Keeping the alias makes this shader work with either
        // prepass path. Only the matching LightMode is selected, so the object
        // is not rendered twice.
        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode"="DepthNormalsOnly" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag
            #pragma target 4.6
            #pragma require tessellation
            #pragma multi_compile_local _SPACE_OBJECT _SPACE_WORLD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
            };

            TessControlPoint vert(Attributes input)
            {
                return TessVert(input.positionOS, input.normalOS);
            }

            [domain("tri")]
            Varyings domain(TessFactors factors,
                            const OutputPatch<TessControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 basePositionOS, normalOS;
                ResolvePatch(patch, bary, basePositionOS, normalOS);

                float3 basePositionWS = TransformObjectToWorld(basePositionOS);
                float3 geoNormalWS = normalize(TransformObjectToWorldNormal(normalOS));
                float3 mapPos = MapPosition(basePositionOS, basePositionWS);

                float3 positionWS = DisplaceWS(basePositionWS, geoNormalWS, mapPos);

                float4 hd =
                    HeightDWithDetail(
                        mapPos,
                        SurfaceDetailAmount(positionWS));
                float3 rippleGradient;
                Ripple(mapPos, rippleGradient);

                float3 gradWS = MapDirToWorld(
                    hd.yzw *
                    _BumpStrength *
                    SurfaceBumpMultiplier(positionWS) +
                    rippleGradient);

                float3 tangentialGrad =
                    gradWS - geoNormalWS * dot(gradWS, geoNormalWS);

                Varyings output;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.normalWS = normalize(geoNormalWS - tangentialGrad);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);

            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS =
                    saturate(octNormalWS * 0.5 + 0.5);
                half3 packedNormalWS =
                    PackFloat2To888(remappedOctNormalWS);
                return half4(packedNormalWS, 0.0);
            #else
                return half4(normalWS, 0.0);
            #endif
            }
            ENDHLSL
        }

    }

    FallBack "Universal Render Pipeline/Lit"
}

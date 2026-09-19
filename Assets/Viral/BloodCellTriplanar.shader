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
        CBUFFER_END

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
        float4 HeightD(float3 p)
        {
            float4 n  = FBM3D(p * _NoiseScale, _Gain, _Lacunarity, _Detail);
            float4 hd = float4(n.x, n.yzw * _NoiseScale);   // chain rule

            if (_PulseAmount <= 0.0) return hd;

            float h        = hd.x;
            float centered = h - 0.5;
            float phase    = _Time.y * _PulseSpeed + h * TWO_PI * _PulseVariation;
            float k        = 1.0 + _PulseAmount * sin(phase);
            float dkdh     = _PulseAmount * cos(phase) * TWO_PI * _PulseVariation;

            // d/dp of (0.5 + centered * k(h)) is (k + centered * dk/dh) * dh/dp.
            // The phase depends only on h, so the whole thing stays a scalar
            // multiple of the base gradient -- exact, and no extra noise taps.
            return float4(0.5 + centered * k, hd.yzw * (k + centered * dkdh));
        }

        float Height(float3 p)
        {
            return HeightD(p).x;
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
            if (_Displace <= 0.0) return positionWS;
            return positionWS + normalWS * (Height(mapPos) - 0.5) * _Displace;
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

        bool PointOutOfFrustum(float4 positionCS, float bias)
        {
            float3 c = positionCS.xyz;
            float  w = positionCS.w;
            return any(c < float3(-w - bias, -w - bias,
                                  -w * UNITY_RAW_FAR_CLIP_VALUE - bias))
                || any(c > float3( w + bias,  w + bias,  w + bias));
        }

        TessFactors PatchConstant(InputPatch<TessControlPoint, 3> patch)
        {
            float3 p0 = TransformObjectToWorld(patch[0].positionOS.xyz);
            float3 p1 = TransformObjectToWorld(patch[1].positionOS.xyz);
            float3 p2 = TransformObjectToWorld(patch[2].positionOS.xyz);

            TessFactors f;

            // Displacement pushes geometry outside the source triangle, so the
            // cull bias must cover it or lumps pop in at the screen edges.
            float bias = _Displace + 0.01;
            if (PointOutOfFrustum(TransformWorldToHClip(p0), bias) &&
                PointOutOfFrustum(TransformWorldToHClip(p1), bias) &&
                PointOutOfFrustum(TransformWorldToHClip(p2), bias))
            {
                f.edge[0] = f.edge[1] = f.edge[2] = 0;
                f.inside = 0;
                return f;
            }

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

                // One fBm evaluation returns both height and exact gradient,
                // replacing the four taps a finite difference needed.
                float4 hd = HeightD(p);
                float  h  = hd.x;

                // Project the gradient onto the tangent plane so the bump
                // slides the normal sideways instead of inflating it.
                float3 gradWS = MapDirToWorld(hd.yzw);
                float3 tangentialGrad = gradWS - geoNormal * dot(gradWS, geoNormal);
                float3 normalWS = normalize(geoNormal - tangentialGrad * _BumpStrength);

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
                    albedo *= lerp(half3(1,1,1), detail, _DetailStrength);
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
            Cull Back

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
            Cull Back

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
    }

    FallBack "Universal Render Pipeline/Lit"
}

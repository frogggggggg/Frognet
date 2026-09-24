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
        [Toggle] _FollowRipples ("Ride Nearby Cell Ripples (not for cells)", Float) = 0

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
        #include "BloodCellCore.hlsl"

        // ---------------------------------------------------------------
        // Tessellation (shared by every pass)
        // ---------------------------------------------------------------

        // displaceOS: one direction per position, baked by Surface into UV3, so displacement
        // doesn't tear the mesh open where a position has several normals (a cube's edges).
        // Zero when the mesh has none: then the normal is used.
        struct TessAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float3 displaceOS : TEXCOORD3;
        };

        struct TessControlPoint
        {
            float4 positionOS : INTERNALTESSPOS;
            float3 normalOS   : NORMAL;
            float3 displaceOS : TEXCOORD3;
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
            cp.displaceOS = dot(input.displaceOS, input.displaceOS) > 0.01 ? input.displaceOS : input.normalOS;
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
            float4 ripple;       // impact ripple: height, map-space gradient (evaluated once here)
        };

        // Everything every pass needs from the domain stage: resolve the patch,
        // then displace along the baked direction in world space (so _Displace is in world
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
            float3 displaceOS = BARY3(patch[0].displaceOS, patch[1].displaceOS, patch[2].displaceOS, bary);

            CellSample c;
            float3 baseWS = TransformObjectToWorld(positionOS);
            c.normalWS    = normalize(TransformObjectToWorldNormal(normalOS));
            c.mapPos      = MapPosition(positionOS, baseWS);
            c.fade        = DetailFade(baseWS);

            // Distant relief is simplified so tessellated vertices don't crawl.
            // Ripples stay full strength: they're gameplay feedback.
            float h      = SurfaceHeight(c.mapPos, c.fade).x;
            c.ripple     = Ripple(c.mapPos);
            float relief = (h - 0.5) * _Displace
                         * lerp(_DistantDisplacementMultiplier, 1.0, c.fade);

            // Focus sweep: no texture relief inside its circle, but ripples stay, so their
            // outlines show.
        #if defined(INVERT_BACKFACES)
            relief *= 1.0 - InvertSweepCover(baseWS);
        #endif

            c.positionWS = baseWS + normalize(TransformObjectToWorldNormal(displaceOS)) * (relief + c.ripple.x);

            // Legs, ropes etc. drawn with this shader ride the ripples of whatever cell they're near.
            if (_FollowRipples > 0.5)
                c.positionWS += RippleFieldOffset(baseWS);
            return c;
        }

        [domain("tri")]
        DepthVaryings DepthDomain(TessFactors factors,
                                  const OutputPatch<TessControlPoint, 3> patch,
                                  float3 bary : SV_DomainLocation)
        {
            DepthVaryings o;
            float3 positionWS = EvaluateCell(patch, bary).positionWS;
            o.positionHCS = TransformWorldToHClip(positionWS);
        #if defined(INVERT_BACKFACES)
            o.positionWS  = positionWS;
        #endif
            return o;
        }

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
        #if defined(INVERT_BACKFACES)
            o.positionWS  = c.positionWS;
        #endif
            o.normalWS    = BumpNormal(c.normalWS, SurfaceHeight(c.mapPos, c.fade),
                                       c.ripple, c.fade);
        #if defined(INVERT_BACKFACES)
            // Inside the focus sweep: the smooth vertex normal bent by ripples only (no bump), so
            // only real creases and ripples outline.
            o.normalWS    = lerp(o.normalWS, BumpNormal(c.normalWS, float4(0.0, 0.0, 0.0, 0.0), c.ripple, c.fade),
                                 InvertSweepCover(c.positionWS));
        #endif
            return o;
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
            #pragma multi_compile _ INVERT_BACKFACES // focus sweep sees the backs (ScreenInvertTest)
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

            #include "BloodCellForward.hlsl"

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
                o.rippleGrad  = c.ripple.yzw;
                o.positionHCS = TransformWorldToHClip(c.positionWS);
                o.fogCoord    = ComputeFogFactor(o.positionHCS.z);
                return o;
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
            #pragma multi_compile _ INVERT_BACKFACES // focus sweep sees the backs (ScreenInvertTest)
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
            #pragma multi_compile _ INVERT_BACKFACES // focus sweep sees the backs (ScreenInvertTest)
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
            #pragma multi_compile _ INVERT_BACKFACES // focus sweep sees the backs (ScreenInvertTest)
            #pragma target 4.6
            #pragma require tessellation
            #pragma shader_feature_local _SPACE_OBJECT _SPACE_WORLD
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}

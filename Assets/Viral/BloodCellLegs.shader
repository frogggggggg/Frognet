// GPU legs: every SpiderLegWalker's legs, drawn by LegRenderer as instances of one
// tube mesh (procedural instancing), shaped in the vertex stage from per-leg records
// (BloodCellLegs.hlsl). Same look as Custom/BloodCellTriplanar -- it shares that
// shader's code (BloodCellCore / BloodCellForward) -- minus tessellation. The walker's
// leg material is copied onto this shader at runtime, so edit that material as usual.
Shader "Custom/BloodCellLegs"
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
        // Lumps are mapped relative to each leg's hub (BloodCellLegs.hlsl), in world orientation.
        #define _SPACE_WORLD 1
        #include "BloodCellCore.hlsl"
        #include "BloodCellLegs.hlsl"
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
            #pragma vertex LegForwardVertex
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:LegSetup

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _SHADING_SMOOTH _SHADING_CEL

            #include "BloodCellForward.hlsl"

            Varyings LegForwardVertex(LegAttributes v)
            {
                LegSample s = EvaluateLeg(v);
                Varyings o;
                o.positionWS  = s.positionWS;
                o.normalWS    = s.normalWS;
                o.mapPos      = s.mapPos;
                o.rippleGrad  = 0.0; // legs have no ripples of their own; they ride the cell's
                o.positionHCS = TransformWorldToHClip(s.positionWS);
                o.fogCoord    = ComputeFogFactor(o.positionHCS.z);
                return o;
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex LegShadowVertex
            #pragma fragment DepthFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:LegSetup
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            DepthVaryings LegShadowVertex(LegAttributes v)
            {
                LegSample s = EvaluateLeg(v);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - s.positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(s.positionWS, s.normalWS, lightDirectionWS));

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
            #pragma vertex LegDepthVertex
            #pragma fragment DepthFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:LegSetup
            ENDHLSL
        }

        // -------------------------------------------------------------------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }

            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex LegDepthNormalsVertex
            #pragma fragment DepthNormalsFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:LegSetup
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
            #pragma vertex LegDepthNormalsVertex
            #pragma fragment DepthNormalsFrag
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:LegSetup
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            ENDHLSL
        }
    }
}

// Far field stand-ins for things with a cell material (red cells, white cells; FarField.cs): the real cells' own
// surface on the stand-in mesh, so the two match where they cross over. The material is a copy of the prefab's (or
// the white cells') material: same lumps (SurfaceHeight, footprint-filtered, so past ~150 m they are flat and the
// noise is skipped), far tone (FarTone), bump, colour bands, cel lighting, rim subsurface (CellShade).
// No tessellation, displacement, ripples, tendrils or bursts (none of it reads at 300 m+). Map = mesh position x
// record scale (object mapping, as the cells), so the pattern rides along. Pose and clipping in FarFieldCore.hlsl.
// Cost per pixel: CellShade, + the 4-octave lumps only when nearer than they flatten.
Shader "Custom/FarFieldCell"
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
        _FarTone ("Far Tone (colour the lumps fade to; 0 = off)", Range(0,1)) = 0

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
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry+50" }

        HLSLINCLUDE
        #include "../BloodCellCore.hlsl" // the cells' properties, noise (+ Core, StreamFade)
        #include "FarFieldCore.hlsl"
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment farFrag
            #pragma target 4.5
            #pragma multi_compile_fog
            #define _SHADING_CEL 1
            #include "../BloodCellForward.hlsl" // CellShade

            struct FarVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 mapPos     : TEXCOORD2;
                float  fog        : TEXCOORD3;
                nointerpolation float2 fades    : TEXCOORD4;
                nointerpolation float4 rotation : TEXCOORD5;
            };

            FarVaryings vert(FarAttributes v)
            {
                FarVaryings o;
                Posed p = FarPose(v);
                float seed;
                o.positionWS = FarWorld(v, p, o.normalWS, o.fades, seed);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.mapPos = v.positionOS.xyz * p.scale.xyz;
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.rotation = p.rotation;
                return o;
            }

            half4 farFrag(FarVaryings i) : SV_Target
            {
                FarClip(i.fades, i.positionCS.xy);
                float3 geo = normalize(i.normalWS);
                float fade = DetailFade(i.positionWS);
                float pixel = PixelMetres(i.positionWS);
                float4 hd = float4(0.5, 0.0, 0.0, 0.0);
                [branch] if (pixel * _NoiseScale < 0.4) // past this every octave is flat (FBM3DLod)
                    hd = SurfaceHeight(i.mapPos, fade, pixel);
                // BumpNormal, with the instance's rotation for map -> world (no object matrix in this draw).
                float3 grad = FarRotate(i.rotation, hd.yzw * (_BumpStrength * lerp(_DistantBumpMultiplier, 1.0, fade)));
                float3 n = normalize(geo - (grad - geo * dot(grad, geo)));
                half3 rgb = CellShade(i.positionWS, i.positionCS, i.mapPos, geo, n, hd.x, fade);
                return half4(MixFog(MixAtmosphere(rgb, i.positionWS), i.fog), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            struct Varyings { float4 positionCS : SV_POSITION; nointerpolation float2 fades : TEXCOORD0; };

            Varyings vert(FarAttributes v)
            {
                Varyings o;
                float3 n;
                float seed;
                o.positionCS = TransformWorldToHClip(FarWorld(v, n, o.fades, seed));
                return o;
            }
            half4 frag(Varyings i) : SV_Target { FarClip(i.fades, i.positionCS.xy); return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; nointerpolation float2 fades : TEXCOORD1; };

            Varyings vert(FarAttributes v)
            {
                Varyings o;
                float seed;
                o.positionCS = TransformWorldToHClip(FarWorld(v, o.normalWS, o.fades, seed));
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                FarClip(i.fades, i.positionCS.xy);
                float3 n = normalize(i.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = saturate(PackNormalOctQuadEncode(n) * 0.5 + 0.5);
                return half4(PackFloat2To888(oct), 0.0);
            #else
                return half4(n, 0.0);
            #endif
            }
            ENDHLSL
        }
    }
}

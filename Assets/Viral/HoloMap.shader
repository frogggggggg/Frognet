// Hologram map model (HoloMap.cs), drawn into the map's render texture: additive
// glow, strongest at silhouettes (fresnel), dimmer on the far side so it reads as
// depth, with a radar ping sweeping out from the player and a faint flicker,
// instanced. HoloMapScreen.shader puts the texture on screen (with a soft bloom).
Shader "Hidden/HoloMap"
{
    Properties
    {
        _Color ("Color", Color) = (0.3, 0.9, 1, 1)
        _Fill ("Fill", Float) = 0.12
        _Rim ("Rim", Float) = 1
        _RimPower ("Rim Power", Float) = 2.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            Name "Model"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Fill, _Rim, _RimPower;
                float  _EdgeFade; // 1 = fade out toward the range sphere; 0 = the sphere itself
            CBUFFER_END

            float3 _HoloEye;         // map camera position, map space
            float  _HoloFlicker;
            float  _HoloDepthDim;    // brightness of the far side of the globe (1 = no depth cue)
            float4 _HoloPulse;       // x = strength, y = seconds between pings, z = band width
            float  _HoloFadeStart;   // map radius (0..1) where things start fading out

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 view = normalize(_HoloEye - i.positionWS);
                float fres = pow(1.0 - saturate(abs(dot(normalize(i.normalWS), view))), _RimPower);

                // Near side bright, far side dim: +1 facing the eye, -1 behind the centre.
                float side = dot(i.positionWS, normalize(_HoloEye));
                float depth = lerp(_HoloDepthDim, 1.0, saturate(0.5 + 0.5 * side));

                // Whole-map flicker (time only, so it never bands across the image).
                float t = _Time.y;
                float flicker = 1.0 - _HoloFlicker * saturate(0.5 + 0.5 * sin(t * 23.0) * sin(t * 7.3 + 1.7));

                // Radar ping: a soft shell growing out from the player, fading as it goes.
                float r = length(i.positionWS);
                float phase = frac(t / max(_HoloPulse.y, 0.01));
                float band = (r - phase * 1.1) / max(_HoloPulse.z, 0.001);
                float ping = _HoloPulse.x * exp(-band * band) * (1.0 - phase) * _EdgeFade;

                // Fade out toward the edge of the range sphere, so things drift in and out
                // instead of popping, and anything past the sphere dissolves.
                float edge = 1.0 - smoothstep(_HoloFadeStart, 1.0, r);
                float glow = ((_Fill + fres * _Rim) * depth + ping) * flicker * _Color.a
                           * lerp(1.0, edge, _EdgeFade);
                return half4(_Color.rgb * glow, glow);
            }
            ENDHLSL
        }
    }
}

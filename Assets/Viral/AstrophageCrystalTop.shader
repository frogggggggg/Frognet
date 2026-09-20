// Astrophage crystalline upper-shell material for Unity 6 / URP.
//
// Desktop-focused URP transparent shader.
// - True triangle-edge wireframe using a geometry shader with barycentrics
// - Artsy / cel-shaded wireframe with thickness variance and hand-drawn jitter
// - Stable object-space internal filaments
//
// NOTE:
// Because this uses a geometry shader for true wireframe edges, it is intended
// for desktop platforms / APIs that support geometry shaders.
Shader "Custom/AstrophageCrystalTop"
{
    Properties
    {
        [Header(Crystal)]
        [HDR] _BaseColor ("Crystal Color", Color) = (0.08, 0.52, 0.74, 1)
        [HDR] _DeepColor ("Deep Color", Color) = (0.01, 0.08, 0.16, 1)
        _Alpha ("Transparency", Range(0.03, 1)) = 0.46

        [Header(Wireframe)]
        [HDR] _WireColor ("Wireframe Color", Color) = (0.50, 1.05, 1.35, 1)
        _WireStrength ("Wireframe Strength", Range(0, 4)) = 1.8
        _WireThickness ("Wire Thickness", Range(0.2, 4.0)) = 1.2
        _WireOpacityBoost ("Wire Opacity Boost", Range(0, 1)) = 0.22
        _WireThicknessVariance ("Wire Thickness Variance", Range(0, 1)) = 0.45
        _WireJitterScale ("Wire Jitter Scale", Range(0.1, 20)) = 5.0
        _WireJitterStrength ("Wire Jitter Strength", Range(0, 1)) = 0.35
        _WireRoughness ("Wire Roughness", Range(0, 1)) = 0.28

        [Header(DNA_Inside)]
        [HDR] _DNAColor ("DNA Color", Color) = (0.30, 0.95, 1.30, 1)
        _DNAStrength ("DNA Strength", Range(0, 4)) = 1.55
        _DNAOpacityBoost ("DNA Opacity Boost", Range(0, 1)) = 0.16
        _DNAScale ("DNA Scale", Range(0.2, 8)) = 1.65
        _DNADepth ("DNA Interior Depth", Range(0.02, 3.0)) = 0.75
        _DNADensity ("DNA Density", Range(0.2, 4)) = 1.25
        _DNAWidth ("DNA Width", Range(0.001, 0.2)) = 0.018
        _DNARungStrength ("DNA Rung Strength", Range(0, 3)) = 0.22
        _DNAScrollSpeed ("DNA Scroll Speed", Range(-5, 5)) = 0.10
        _DNAChaos ("DNA Squiggle Chaos", Range(0, 3)) = 1.10

        [Header(Rim)]
        [HDR] _RimColor ("Rim Color", Color) = (0.25, 0.85, 1.20, 1)
        _RimPower ("Rim Width", Range(0.5, 10)) = 3.2
        _RimStrength ("Rim Strength", Range(0, 4)) = 0.85
        _RimOpacityBoost ("Rim Opacity Boost", Range(0, 1)) = 0.14

        [Header(Lighting)]
        _Smoothness ("Smoothness", Range(0, 1)) = 0.82
        _SpecularStrength ("Specular Strength", Range(0, 4)) = 1.25
        _AmbientStrength ("Ambient Strength", Range(0, 2)) = 0.72

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
        [Toggle] _ZWrite ("Z Write (normally off)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "AstrophageCrystal"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 4.0
            #pragma require geometry
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _DeepColor;
                half  _Alpha;

                half4 _WireColor;
                half  _WireStrength;
                float _WireThickness;
                half  _WireOpacityBoost;
                half  _WireThicknessVariance;
                float _WireJitterScale;
                half  _WireJitterStrength;
                half  _WireRoughness;

                half4 _DNAColor;
                half  _DNAStrength;
                half  _DNAOpacityBoost;
                float _DNAScale;
                float _DNADepth;
                float _DNADensity;
                float _DNAWidth;
                half  _DNARungStrength;
                float _DNAScrollSpeed;
                float _DNAChaos;

                half4 _RimColor;
                float _RimPower;
                half  _RimStrength;
                half  _RimOpacityBoost;

                half  _Smoothness;
                half  _SpecularStrength;
                half  _AmbientStrength;

                float _Cull;
                float _ZWrite;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct GeomInput
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS    : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float3 normalOS   : TEXCOORD3;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS    : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float3 bary       : TEXCOORD3;
                float3 normalOS   : TEXCOORD4;
            };

            GeomInput vert(Attributes input)
            {
                GeomInput output;

                VertexPositionInputs positionInputs =
                    GetVertexPositionInputs(input.positionOS.xyz);

                VertexNormalInputs normalInputs =
                    GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.positionOS = input.positionOS.xyz;
                output.normalOS = input.normalOS;

                return output;
            }

            [maxvertexcount(3)]
            void geom(
                triangle GeomInput input[3],
                inout TriangleStream<Varyings> stream)
            {
                Varyings o;

                o = (Varyings)0;
                o.positionCS = input[0].positionCS;
                o.positionWS = input[0].positionWS;
                o.normalWS = input[0].normalWS;
                o.positionOS = input[0].positionOS;
                o.normalOS = input[0].normalOS;
                o.bary = float3(1.0, 0.0, 0.0);
                stream.Append(o);

                o = (Varyings)0;
                o.positionCS = input[1].positionCS;
                o.positionWS = input[1].positionWS;
                o.normalWS = input[1].normalWS;
                o.positionOS = input[1].positionOS;
                o.normalOS = input[1].normalOS;
                o.bary = float3(0.0, 1.0, 0.0);
                stream.Append(o);

                o = (Varyings)0;
                o.positionCS = input[2].positionCS;
                o.positionWS = input[2].positionWS;
                o.normalWS = input[2].normalWS;
                o.positionOS = input[2].positionOS;
                o.normalOS = input[2].normalOS;
                o.bary = float3(0.0, 0.0, 1.0);
                stream.Append(o);
            }

            float Hash11(float p)
            {
                p = frac(p * 0.1031);
                p *= p + 33.33;
                p *= p + p;
                return frac(p);
            }

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            float Noise3D(float3 p)
            {
                float3 i = floor(p);
                float3 f = frac(p);

                f = f * f * (3.0 - 2.0 * f);

                float n000 = Hash31(i + float3(0,0,0));
                float n100 = Hash31(i + float3(1,0,0));
                float n010 = Hash31(i + float3(0,1,0));
                float n110 = Hash31(i + float3(1,1,0));
                float n001 = Hash31(i + float3(0,0,1));
                float n101 = Hash31(i + float3(1,0,1));
                float n011 = Hash31(i + float3(0,1,1));
                float n111 = Hash31(i + float3(1,1,1));

                float nx00 = lerp(n000, n100, f.x);
                float nx10 = lerp(n010, n110, f.x);
                float nx01 = lerp(n001, n101, f.x);
                float nx11 = lerp(n011, n111, f.x);

                float nxy0 = lerp(nx00, nx10, f.y);
                float nxy1 = lerp(nx01, nx11, f.y);

                return lerp(nxy0, nxy1, f.z);
            }

            float EdgeLine(float baryCoord, float width)
            {
                return 1.0 - smoothstep(width, width * 2.1, baryCoord);
            }

            float WireframeMask(float3 bary, float3 positionOS)
            {
                float3 fw = fwidth(bary);

                // Stable edge identities.
                float edgeId0 = 0.173;
                float edgeId1 = 0.517;
                float edgeId2 = 0.811;

                // Low-frequency object-space noise for hand-drawn variation.
                float n0 =
                    Noise3D(positionOS * _WireJitterScale + edgeId0 * 11.0.xxx);
                float n1 =
                    Noise3D(positionOS * _WireJitterScale + edgeId1 * 11.0.xxx);
                float n2 =
                    Noise3D(positionOS * _WireJitterScale + edgeId2 * 11.0.xxx);

                // Additional higher-frequency breakup to make the line less
                // mechanically even.
                float h0 =
                    Noise3D(positionOS * (_WireJitterScale * 2.6) + edgeId0 * 29.0.xxx);
                float h1 =
                    Noise3D(positionOS * (_WireJitterScale * 2.6) + edgeId1 * 29.0.xxx);
                float h2 =
                    Noise3D(positionOS * (_WireJitterScale * 2.6) + edgeId2 * 29.0.xxx);

                float var0 =
                    lerp(1.0, 1.0 + (n0 - 0.5) * 2.0 * _WireThicknessVariance, _WireJitterStrength);
                float var1 =
                    lerp(1.0, 1.0 + (n1 - 0.5) * 2.0 * _WireThicknessVariance, _WireJitterStrength);
                float var2 =
                    lerp(1.0, 1.0 + (n2 - 0.5) * 2.0 * _WireThicknessVariance, _WireJitterStrength);

                float w0 = max(fw.x * _WireThickness * var0, 1e-5);
                float w1 = max(fw.y * _WireThickness * var1, 1e-5);
                float w2 = max(fw.z * _WireThickness * var2, 1e-5);

                float l0 = EdgeLine(bary.x, w0);
                float l1 = EdgeLine(bary.y, w1);
                float l2 = EdgeLine(bary.z, w2);

                // Per-edge opacity jitter for a more artsy/cel look.
                float rough0 = lerp(1.0, lerp(0.72, 1.18, h0), _WireRoughness);
                float rough1 = lerp(1.0, lerp(0.72, 1.18, h1), _WireRoughness);
                float rough2 = lerp(1.0, lerp(0.72, 1.18, h2), _WireRoughness);

                l0 *= rough0;
                l1 *= rough1;
                l2 *= rough2;

                return saturate(max(max(l0, l1), l2));
            }

            float3 MakeFrameX(float3 axis)
            {
                float3 refAxis =
                    abs(axis.y) < 0.9
                        ? float3(0.0, 1.0, 0.0)
                        : float3(1.0, 0.0, 0.0);

                return normalize(cross(refAxis, axis));
            }

            float3x3 AxisFrame(float3 axis)
            {
                axis = normalize(axis);
                float3 x = MakeFrameX(axis);
                float3 z = normalize(cross(axis, x));
                return float3x3(x, axis, z);
            }

            float LineMaskToCenter(float2 p, float2 center, float width)
            {
                float d = length(p - center);
                return 1.0 - smoothstep(width, width * 2.2, d);
            }

            float FilamentCore(
                float3 p,
                float3 axis,
                float2 offset,
                float phase,
                float radius,
                float width,
                float chaos)
            {
                float3x3 frame = AxisFrame(axis);
                float3 q = mul(transpose(frame), p);

                float t =
                    q.y +
                    _Time.y * _DNAScrollSpeed;

                float2 center;
                center.x =
                    offset.x +
                    sin(t * (1.55 + chaos * 0.32) + phase) * radius * 0.95 +
                    sin(t * (3.25 + chaos * 0.58) - phase * 1.47) * radius * 0.38 +
                    cos(t * 0.77 + phase * 2.33) * radius * 0.23;

                center.y =
                    offset.y +
                    cos(t * (1.23 + chaos * 0.28) - phase * 0.81) * radius * 0.90 +
                    sin(t * (2.85 + chaos * 0.51) + phase * 1.91) * radius * 0.34 +
                    sin(t * 0.69 - phase * 1.37) * radius * 0.20;

                float2 slice = q.xz;

                return LineMaskToCenter(slice, center, width);
            }

            float DoubleFilamentCluster(
                float3 p,
                float3 axis,
                float2 offset,
                float phase,
                float width,
                float density,
                float chaos,
                float rungStrength)
            {
                float radiusA = 0.16 * density;
                float radiusB = 0.11 * density;

                float a =
                    FilamentCore(
                        p,
                        axis,
                        offset + float2(-0.04, 0.02),
                        phase,
                        radiusA,
                        width,
                        chaos);

                float b =
                    FilamentCore(
                        p,
                        axis,
                        offset + float2(0.03, -0.03),
                        phase + 1.25,
                        radiusB,
                        width * 0.92,
                        chaos * 1.1);

                float c =
                    FilamentCore(
                        p,
                        axis,
                        offset + float2(0.00, 0.00),
                        phase + 2.10,
                        radiusB * 0.85,
                        width * 0.85,
                        chaos * 0.9);

                float3x3 frame = AxisFrame(axis);
                float3 q = mul(transpose(frame), p);
                float tt =
                    q.y +
                    _Time.y * _DNAScrollSpeed;

                float rungPhase =
                    abs(frac(tt * 1.9 + phase * 0.11) - 0.5) * 2.0;

                float rungWindow =
                    1.0 - smoothstep(0.14, 0.55, rungPhase);

                float rungLine =
                    (1.0 - smoothstep(width * 1.4, width * 4.0, abs(q.x - offset.x))) *
                    (1.0 - smoothstep(width * 1.0, width * 3.0, abs(q.z - offset.y)));

                float rungs =
                    rungWindow *
                    rungLine *
                    rungStrength;

                return saturate(a + b + c + rungs);
            }

            float InternalDNAVolume(
                float3 surfacePosOS,
                float3 normalOS)
            {
                float3 centerDir =
                    normalize(-surfacePosOS + normalOS * 0.02);

                float3 normalDir =
                    normalize(-normalOS);

                float3 marchDir =
                    normalize(lerp(normalDir, centerDir, 0.78));

                float depth =
                    max(_DNADepth, 0.001);

                float accum = 0.0;
                float transmittance = 1.0;

                [unroll]
                for (int i = 0; i < 10; i++)
                {
                    float t = (i + 0.5) / 10.0;

                    float3 p =
                        (surfacePosOS + marchDir * (t * depth)) *
                        _DNAScale;

                    float sample = 0.0;

                    sample += DoubleFilamentCluster(
                        p,
                        normalize(float3(1.0, 0.35, 0.20)),
                        float2(-0.08, 0.10),
                        0.25,
                        _DNAWidth,
                        _DNADensity,
                        _DNAChaos,
                        _DNARungStrength * 0.75);

                    sample += DoubleFilamentCluster(
                        p + float3(0.14, -0.08, 0.18),
                        normalize(float3(0.22, 1.0, 0.40)),
                        float2(0.12, -0.06),
                        1.35,
                        _DNAWidth * 0.92,
                        _DNADensity * 0.95,
                        _DNAChaos * 1.05,
                        _DNARungStrength * 0.55);

                    sample += DoubleFilamentCluster(
                        p + float3(-0.20, 0.14, -0.10),
                        normalize(float3(0.46, 0.28, 1.0)),
                        float2(-0.02, -0.13),
                        2.35,
                        _DNAWidth * 0.88,
                        _DNADensity * 0.90,
                        _DNAChaos * 0.92,
                        _DNARungStrength * 0.45);

                    sample += DoubleFilamentCluster(
                        p + float3(0.08, 0.24, -0.18),
                        normalize(float3(0.68, 1.0, 0.18)),
                        float2(0.05, 0.16),
                        3.40,
                        _DNAWidth * 0.78,
                        _DNADensity * 0.82,
                        _DNAChaos * 1.16,
                        _DNARungStrength * 0.35);

                    float frontFade = smoothstep(0.06, 0.20, t);
                    float backFade = 1.0 - smoothstep(0.78, 1.0, t);
                    float sampleWeight = frontFade * backFade;

                    float opacity = saturate(sample * sampleWeight * 0.32);
                    accum += transmittance * opacity;
                    transmittance *= (1.0 - opacity);
                }

                return saturate(accum);
            }

            half4 frag(Varyings input) : SV_Target
            {
                half3 normalWS =
                    normalize(input.normalWS);

                half3 viewDirWS =
                    SafeNormalize(
                        _WorldSpaceCameraPos -
                        input.positionWS);

                half ndv =
                    saturate(
                        dot(normalWS, viewDirWS));

                Light mainLight =
                    GetMainLight();

                half ndl =
                    saturate(
                        dot(normalWS, mainLight.direction));

                half3 ambient =
                    SampleSH(normalWS) *
                    _AmbientStrength;

                half3 baseColor =
                    lerp(
                        _DeepColor.rgb,
                        _BaseColor.rgb,
                        ndl * 0.65 + 0.35);

                half3 color =
                    baseColor *
                    (ambient +
                     mainLight.color *
                     (0.20 + ndl * 0.80));

                half3 halfVector =
                    SafeNormalize(mainLight.direction + viewDirWS);

                half ndh =
                    saturate(
                        dot(normalWS, halfVector));

                float specPower =
                    lerp(12.0, 320.0, _Smoothness);

                half specular =
                    pow(ndh, specPower) *
                    _SpecularStrength;

                color +=
                    mainLight.color *
                    specular;

                float dnaMask =
                    InternalDNAVolume(
                        input.positionOS,
                        input.normalOS);

                color +=
                    _DNAColor.rgb *
                    dnaMask *
                    _DNAStrength;

                float wireMask =
                    WireframeMask(
                        input.bary,
                        input.positionOS);

                color =
                    lerp(
                        color,
                        _WireColor.rgb,
                        saturate(wireMask * _WireStrength));

                half rim =
                    pow(1.0 - ndv, _RimPower) *
                    _RimStrength;

                color =
                    lerp(
                        color,
                        _RimColor.rgb,
                        saturate(rim));

                half alpha =
                    saturate(
                        _Alpha +
                        dnaMask * _DNAOpacityBoost +
                        wireMask * _WireOpacityBoost +
                        rim * _RimOpacityBoost);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

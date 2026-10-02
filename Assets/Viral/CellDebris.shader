// The pieces a bursting cell breaks into (CellBurst.cs, CellShards.cs): one instanced draw of the whole broken body
// per burst, every piece placed from the clock in the vertex stage. At BURST_SWELL it takes over from the body exactly
// (same surface, relief and BurstSwell); cracks open from the entry round (the broken edges show the inside colour),
// each piece is thrown off along its normal, rounding into a ball, tumbling, slowed by the blood, drifting with the
// body, then dissolves (eaten by noise, glowing edges). Shaded like the cell it came from: CellBurst copies that
// material's values in (same properties as the cell family) and the fragment is the family's CellShade on the body's
// own object-space lump map, carried with each piece. Timings are CellBurst.hlsl's.
Shader "Hidden/CellDebris"
{
    Properties
    {
        // The cell family's (BloodCellCore), copied from the dying body's material.
        _Color ("Surface Color (ridges)", Color) = (0.80, 0.14, 0.13, 1)
        _DeepColor ("Deep Color (valleys)", Color) = (0.40, 0.04, 0.06, 1)
        _MainTex ("Detail Texture (optional)", 2D) = "white" {}
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.0
        _NoiseScale ("Lump Scale", Float) = 3.0
        _BumpStrength ("Bump Strength", Range(0,2)) = 0.35
        _Detail ("Fine Detail", Range(0,1)) = 0.55
        _Lacunarity ("Lacunarity", Range(1.5,4)) = 2.1
        _Gain ("Gain", Range(0.2,0.8)) = 0.5
        _Displace ("Vertex Displacement (world units)", Range(0,0.5)) = 0.05
        _DetailFadeStart ("Fine Detail Fade Start", Float) = 12
        _DetailFadeEnd ("Fine Detail Fade End", Float) = 40
        _DistantDetail ("Distant Fine Detail", Range(0,1)) = 0.0
        _DistantBumpMultiplier ("Distant Bump Multiplier", Range(0,1)) = 0.18
        _DistantDisplacementMultiplier ("Distant Displacement Multiplier", Range(0,1)) = 0.30
        _DistantTextureDetailMultiplier ("Distant Texture Detail Multiplier", Range(0,1)) = 0.15
        _Glossiness ("Smoothness", Range(0,1)) = 0.70
        _GlossVariation ("Smoothness Variation", Range(0,0.5)) = 0.12
        _SpecTint ("Specular Tint", Color) = (0.17, 0.09, 0.09, 1)
        _OcclusionStrength ("Cavity Shading", Range(0,1)) = 0.45
        _SubsurfaceColor ("Subsurface Color", Color) = (1.0, 0.22, 0.13, 1)
        _SubsurfaceStrength ("Subsurface Strength", Range(0,3)) = 0.9
        _RimPower ("Rim Falloff", Range(0.5,8)) = 2.6
        _ThinGlow ("Thin-Area Glow", Range(0,1)) = 0.6
        [KeywordEnum(Smooth, Cel)] _Shading ("Shading Mode", Float) = 1
        _LightBands ("Light Bands", Range(2,8)) = 3
        _BandSoftness ("Band Edge Softness", Range(0,0.25)) = 0.03
        _ColorSteps ("Surface Color Steps (1 = off)", Range(1,8)) = 1
        _RimSteps ("Rim Steps", Range(1,4)) = 2
        _SpecThreshold ("Specular Cutoff", Range(0,1)) = 0.55
        _PulseAmount ("Fluctuation Amount", Range(0,1)) = 0.0
        _PulseSpeed ("Fluctuation Speed", Range(0,6)) = 1.2
        _PulseVariation ("Fluctuation Variation", Range(0,2)) = 1.0
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1,32)) = 6.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        // Map directions are the body's own (object) axes: the fragment bumps in that frame, then turns the result
        // into the world with the piece (Shard.frame).
        #define _SPACE_WORLD 1
        #include "BloodCellCore.hlsl"
        #include "CellBurst.hlsl"

        // CellBurst.Gpu: pose rows (mesh space -> world, at the kill), centre (mesh space) + radius (mesh units),
        // entry (body radii) + start time, velocity + radius (world), info (swell seed).
        struct Burst { float4 m0, m1, m2, centre, entry, velocity, info; };
        StructuredBuffer<Burst> _Bursts;
        uint _BurstOffset;

        float Rand(uint n)
        {
            n = (n << 13u) ^ n;
            n = n * (n * n * 15731u + 789221u) + 1376312589u;
            return float(n & 0x7fffffffu) / 2147483647.0;
        }

        float3 PosePoint(Burst b, float3 p) { return float3(dot(b.m0.xyz, p) + b.m0.w, dot(b.m1.xyz, p) + b.m1.w, dot(b.m2.xyz, p) + b.m2.w); }

        // Rotation of 'angle' radians about the unit 'axis'.
        float3x3 AxisAngle(float3 axis, float angle)
        {
            float s, c;
            sincos(angle, s, c);
            float3 a = axis, t = a * (1.0 - c);
            return float3x3(t.x * a.x + c,       t.x * a.y - s * a.z, t.x * a.z + s * a.y,
                            t.y * a.x + s * a.z, t.y * a.y + c,       t.y * a.z - s * a.x,
                            t.z * a.x - s * a.y, t.z * a.y + s * a.x, t.z * a.z + c);
        }

        // CellShards' vertex layout.
        struct Attributes
        {
            float4 positionOS : POSITION;  // the body's surface point (mesh space, undisplaced)
            float3 normalOS   : NORMAL;
            float4 displace   : TEXCOORD0; // xyz displacement direction (mesh space); w face: 0 outside, 1 inside, 2 broken edge
            float4 piece      : TEXCOORD1; // xyz the piece's pivot (mesh space), w depth below the surface (mesh units)
            float4 pieceInfo  : TEXCOORD2; // xyz the piece's outward normal (mesh space), w the radius of a ball of its volume
            uint instanceID   : SV_InstanceID;
        };

        struct Shard
        {
            float4 positionCS;
            float3 positionWS, normalWS;
            float3 mapPos, mapNormal; // the body's map, carried with the piece
            float3x3 frame;           // map directions -> world
            float3 dissolveAt;        // the dissolve noise's coordinate (fixed to the piece)
            float dissolve, face;     // 0 whole .. 1 gone; face as in Attributes
        };

        Shard Place(Attributes v)
        {
            Shard s = (Shard)0;
            s.positionCS = float4(2.0, 2.0, 2.0, 1.0); // not drawn: culled whole
            Burst b = _Bursts[_BurstOffset + v.instanceID];
            float age = _Time.y - b.entry.w, rl = max(b.centre.w, 1e-4), R = b.velocity.w;

            uint key = (asuint(v.piece.x) ^ asuint(v.piece.y) * 3u ^ asuint(v.piece.z) * 7u) + (asuint(b.entry.w) & 0xffffu) * 6271u;
            float h0 = Rand(key), h1 = Rand(key + 1u), h2 = Rand(key + 2u), h3 = Rand(key + 3u),
                  h4 = Rand(key + 4u), h5 = Rand(key + 5u), h6 = Rand(key + 6u);

            // When the break reaches the piece, and when it's thrown.
            float t = BurstLocalTime(age, (v.piece.xyz - b.centre.xyz) / rl, b.entry.xyz);
            float release = BURST_SWELL + BURST_BREAK * (0.1 + 0.9 * h0);
            float tau = max(t - release, 0.0), life = 1.4 + 1.6 * h1;
            if (age < BURST_SWELL || tau >= life) return s; // still the body / dissolved

            float3 scale = max(float3(length(float3(b.m0.x, b.m1.x, b.m2.x)), length(float3(b.m0.y, b.m1.y, b.m2.y)),
                                      length(float3(b.m0.z, b.m1.z, b.m2.z))), 1e-5);
            float3x3 pose = float3x3(b.m0.xyz / scale, b.m1.xyz / scale, b.m2.xyz / scale); // rotation only
            float meshToWorld = dot(scale, 1.0 / 3.0);
            float3 drift = b.velocity.xyz * age;
            float3 pivot = PosePoint(b, v.piece.xyz) + drift;
            s.mapPos = v.positionOS.xyz * scale; // MapPosition, object space: the lumps stay on the piece

            // Rounds into a ball of its volume once thrown (a torn-off scrap of membrane pinching shut).
            float round = smoothstep(0.0, 0.45, tau);

            // Until then, the surface as the body drew it: its relief (filtered at the piece's middle, so it doesn't
            // crawl) and still swelling (no shiver), fading as it rounds; skipped once round (most of its life).
            float lift = 0.0;
            [branch] if (round < 1.0)
            {
                float fade = DetailFade(pivot);
                float relief = (SurfaceHeight(s.mapPos, fade, PixelMetres(pivot)).x - 0.5) * _Displace
                             * lerp(_DistantDisplacementMultiplier, 1.0, fade);
                float swell = BurstSwellStill((v.positionOS.xyz - b.centre.xyz) / rl, t, b.info.x) * R;
                lift = (relief + swell) * (1.0 - round) / meshToWorld;
            }
            float3 off = v.positionOS.xyz + v.displace.xyz * (lift - v.piece.w) - v.piece.xyz; // from the pivot, mesh space
            float3 ball = normalize(off + 1e-6) * v.pieceInfo.w;

            // Cracks open (the piece lifts and draws in a little), then it's thrown along its normal against the
            // blood's drag (K), tumbling about its middle, shrinking as it dissolves.
            float3 pn = normalize(mul(pose, v.pieceInfo.xyz / scale) + 1e-6);
            float3 wobble = normalize(float3(h2, h3, h4) * 2.0 - 1.0 + 1e-4);
            float crack = smoothstep(BURST_SWELL, BURST_SWELL + 0.15, t);
            const float K = 2.5;
            float travel = (1.0 - exp(-K * tau)) / K; // launch-speed seconds covered
            float3 dir = normalize(pn * 1.2 + wobble * 0.5);
            float speed = R * (0.7 + 1.6 * h5);
            float3 axis = normalize(cross(pn, wobble) + wobble * 0.3 + 1e-4);
            float3x3 spin = AxisAngle(axis, (2.0 + 5.0 * h6) * (h3 > 0.5 ? 1.0 : -1.0) * travel);
            s.dissolve = smoothstep(0.3 * life, life, tau);
            float size = (1.0 - 0.03 * crack) * lerp(1.0, 0.75, s.dissolve);
            s.frame = mul(spin, pose);
            float3 p = pivot + mul(s.frame, lerp(off, ball, round) * scale) * size + pn * (R * 0.025 * crack) + dir * (speed * travel);

            s.mapNormal = normalize(lerp(normalize(v.normalOS / scale), normalize(off / scale + 1e-6), round));
            s.normalWS = mul(s.frame, s.mapNormal);
            s.positionWS = p;
            s.positionCS = TransformWorldToHClip(p);
            s.dissolveAt = (v.positionOS.xyz - b.centre.xyz) / rl * 6.0 + h4 * 17.0;
            s.face = v.displace.w;
            return s;
        }

        // Dissolving: eaten away by noise; returns the glow (0..1) at the eaten edge.
        float DissolveClip(float3 at, float dissolve)
        {
            if (dissolve <= 0.0) return 0.0;
            float n = ValueNoise3D(at).x * 0.65 + ValueNoise3D(at * 2.7 + 5.0).x * 0.35;
            float edge = n + 0.02 - dissolve * 1.05;
            clip(edge);
            return 1.0 - smoothstep(0.0, 0.07, edge);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex DebrisVert
            #pragma fragment DebrisFrag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fog
            #pragma multi_compile_local_fragment _SHADING_SMOOTH _SHADING_CEL // set from code (CellBurst): not stripped

            #include "BloodCellForward.hlsl" // CellShade

            struct DebrisVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 mapPos     : TEXCOORD1;
                float3 mapNormal  : TEXCOORD2;
                float3 dissolveAt : TEXCOORD3;
                nointerpolation float3 frame0 : TEXCOORD4;
                nointerpolation float3 frame1 : TEXCOORD5;
                nointerpolation float3 frame2 : TEXCOORD6;
                nointerpolation float2 dissolveFace : TEXCOORD7;
                float  fog        : TEXCOORD8;
            };

            DebrisVaryings DebrisVert(Attributes v)
            {
                Shard s = Place(v);
                DebrisVaryings o;
                o.positionCS = s.positionCS;
                o.positionWS = s.positionWS;
                o.mapPos = s.mapPos;
                o.mapNormal = s.mapNormal;
                o.dissolveAt = s.dissolveAt;
                o.frame0 = s.frame[0];
                o.frame1 = s.frame[1];
                o.frame2 = s.frame[2];
                o.dissolveFace = float2(s.dissolve, s.face);
                o.fog = ComputeFogFactor(s.positionCS.z);
                return o;
            }

            half4 DebrisFrag(DebrisVaryings i) : SV_Target
            {
                float glow = DissolveClip(i.dissolveAt, i.dissolveFace.x);
                float3x3 frame = float3x3(i.frame0, i.frame1, i.frame2);
                float3 mapN = normalize(i.mapNormal);
                float fade = DetailFade(i.positionWS);
                float4 hd = SurfaceHeight(i.mapPos, fade, PixelMetres(i.positionWS));
                float3 bumped = BumpNormal(mapN, hd, float4(0.0, 0.0, 0.0, 0.0), fade); // in the map's frame
                float3 geoN = normalize(mul(frame, mapN));
                half3 rgb = CellShade(i.positionWS, i.positionCS, i.mapPos, geoN, normalize(mul(frame, bumped)), hd.x, fade);
                // The inside and the broken edges: its inside colour.
                float inside = i.dissolveFace.y > 1.5 ? 1.0 : i.dissolveFace.y > 0.5 ? 0.7 : 0.0;
                rgb = lerp(rgb, rgb * 0.35 + _SubsurfaceColor.rgb * 1.1, inside);
                rgb = lerp(rgb, _SubsurfaceColor.rgb * 1.6, glow);
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
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dissolveAt : TEXCOORD0;
                nointerpolation float dissolve : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Shard s = Place(v);
                Varyings o;
                o.positionCS = s.positionCS;
                o.dissolveAt = s.dissolveAt;
                o.dissolve = s.dissolve;
                return o;
            }
            half4 frag(Varyings i) : SV_Target { DissolveClip(i.dissolveAt, i.dissolve); return i.positionCS.z; }
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
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 dissolveAt : TEXCOORD1;
                nointerpolation float dissolve : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Shard s = Place(v);
                Varyings o;
                o.positionCS = s.positionCS;
                o.normalWS = s.normalWS;
                o.dissolveAt = s.dissolveAt;
                o.dissolve = s.dissolve;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                DissolveClip(i.dissolveAt, i.dissolve);
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

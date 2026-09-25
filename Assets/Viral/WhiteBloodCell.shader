// White blood cells (WhiteBloodCells.cs draws them, mesh from WhiteBloodCellMesh.cs, material
// WhiteBloodCell.mat): a soft white ball bristling with flowing spikes and ruffled folds that crawls like an
// amoeba and draws part of its own body out into an arm with a mouth at the end.
// Everything is shaped here, in the vertex stage, so every pass (shadows, depth, depth normals and so the
// focus outlines) sees the same shape:
// - the mesh is a unit sphere in the cell's *reach frame* (+Z = toward the mouth); the membrane is
//   sampled in the *body* frame, so its folds and spikes stay put on the cell while the mouth slides round;
// - membrane: a slow undulation and rolling swells, ruffles (ridged noise in patches), and spikes (a
//   Worley field of thorns that grow and retract, each curling along a drifting flow and trailing behind
//   the crawl); a leading edge that bulges where it crawls, a tail that tapers;
// - the arm is the body stretched, not a finger stuck on: the cap round +Z (CAP_ANGLE = WhiteBloodCellMesh.
//   CapAngle) is drawn out into a tube that flares back into the body (_Flare), the body's front slides after
//   it (_Pull). The membrane and lumps are mapped where the surface really is (Shape's material maps: one fixed
//   to the body, one to the tip, cross-faded along the arm), so nothing stretches however far it reaches or
//   wraps. The arm bows behind the spot's motion (_Lag), meanders when slack, thins and
//   carries swallowing waves (_Peristalsis) when it's pulling a catch in (tension);
// - at the tip a flowing, irregular mouth; its lip ruffles and a few wisps drift round it (hunger);
// - wrap (state.y): as a phagocyte does it, the arm necks down to a throat just behind the catch and a thin
//   skin creeps forward from it over the catch's *real* shape (its farthest surface per direction from the
//   mouth, captured each frame by ShrinkWrap.cs, extra.w = slot), a rounded lip rolling onto the catch at its
//   rim and a lining tucked under the uncovered front, so the catch shows there; the rim closes at uneven pace.
//   Squeeze (extra.z) presses the skin in and jiggles; shut, the lips pucker. Gape (extra.y) opens the mouth wide
//   as a catch nears. The shape itself is in WhiteBloodCell.hlsl.
// Lit exactly like the red cells: the same family (BloodCellCore: lumps, fluctuation, bump; BloodCellForward:
// CellShade, cel bands, hard highlight, rim subsurface), in world mapping, with the lumps on Shape's two
// material maps (blended where both apply). Its own settings sit outside UnityPerMaterial.
// Ripples: the cell rides RippleField like anything else (Surface impacts publish there).
// Near cells are baked once a frame (WhiteBloodCellBake.compute: the shape once per vertex, normals from the
// mesh grid) and every pass here just reads them; far cells are shaped here (finite-difference normals).
// Ripples go on in the vertex stage either way, the normal bent to match.
// Per instance (_Cells[_InstanceOffset + SV_InstanceID]): centre + radius, body rotation, reach
// (world direction, extension in radii), motion (velocity / top speed, seed),
// state (hunger, wrap, mouth radius in radii, prey radius in radii), sway (spot velocity in radii/s,
// grip tension), extra (detail fade toward the far mesh, gape, squeeze, catch's wrap slot), merge (MergeShape),
// side (the reach frame's x axis, carried along with the arm so nothing round it flips).
Shader "Custom/WhiteBloodCell"
{
    Properties
    {
        [Header(Colour (the red cells family))]
        _Color ("Surface Color (ridges)", Color) = (0.94, 0.91, 0.95, 1)
        _DeepColor ("Deep Color (hollows)", Color) = (0.42, 0.36, 0.66, 1)
        _MainTex ("Detail Texture (optional)", 2D) = "white" {}
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.0

        [Header(Inner Mouth (its own wet flesh, not the membrane))]
        _MouthColor ("Mouth (rim, fold crests)", Color) = (1, 0.45, 0.7, 1)
        _MouthDeepColor ("Mouth Depths (throat)", Color) = (0.32, 0.02, 0.12, 1)
        _MouthGlow ("Mouth Glow", Float) = 0.8
        _MouthWet ("Wetness (highlight)", Range(0,1)) = 0.85
        _MouthFolds ("Folds Round the Throat", Float) = 13
        _MouthFoldDepth ("Fold Depth (m)", Float) = 0.08

        [Header(Surface Relief)]
        _NoiseScale ("Lump Scale", Float) = 1.3
        _BumpStrength ("Bump Strength", Range(0,2)) = 1.6
        _Detail ("Fine Detail", Range(0,1)) = 1
        _Lacunarity ("Lacunarity", Range(1.5,4)) = 2.19
        _Gain ("Gain", Range(0.2,0.8)) = 0.42

        [Header(Distance_Stability)]
        _DetailFadeStart ("Fine Detail Fade Start", Float) = 20
        _DetailFadeEnd ("Fine Detail Fade End", Float) = 100
        _DistantTextureDetailMultiplier ("Distant Texture Detail Multiplier", Range(0,1)) = 0.15
        _DistantDetail ("Distant Fine Detail", Range(0,1)) = 0.32
        _DistantBumpMultiplier ("Distant Bump Multiplier", Range(0,1)) = 0.24

        [Header(Wetness)]
        _Glossiness ("Smoothness", Range(0,1)) = 0
        _GlossVariation ("Smoothness Variation", Range(0,0.5)) = 0.14
        _SpecTint ("Specular Tint (also tints reflections)", Color) = (0.15, 0.13, 0.17, 1)
        _OcclusionStrength ("Cavity Shading", Range(0,1)) = 1

        [Header(Subsurface)]
        _SubsurfaceColor ("Subsurface Color", Color) = (0.78, 0.62, 0.86, 1)
        _SubsurfaceStrength ("Subsurface Strength", Range(0,3)) = 1.1
        _RimPower ("Rim Falloff", Range(0.5,8)) = 2.6
        _ThinGlow ("Thin-Area Glow", Range(0,1)) = 0.6

        [Header(Cel Shading)]
        [KeywordEnum(Smooth, Cel)] _Shading ("Shading Mode", Float) = 1
        _LightBands ("Light Bands", Range(2,8)) = 2
        _BandSoftness ("Band Edge Softness", Range(0,0.25)) = 0.03
        _ColorSteps ("Surface Color Steps (1 = off)", Range(1,8)) = 1
        _RimSteps ("Rim Steps", Range(1,4)) = 1
        _SpecThreshold ("Specular Cutoff", Range(0,1)) = 0.55

        [Header(Fluctuation)]
        _PulseAmount ("Fluctuation Amount", Range(0,1)) = 0.5
        _PulseSpeed ("Fluctuation Speed", Range(0,6)) = 1
        _PulseVariation ("Fluctuation Variation", Range(0,2)) = 2
        _BlendSharpness ("Triplanar Blend Sharpness", Range(1,32)) = 6.0

        [Header(Membrane)]
        _Wobble ("Flow Undulation (radii)", Float) = 0.05
        _Flow ("Flow Speed", Float) = 1
        _Lumps ("Ruffle Height (radii)", Float) = 0.05
        _LumpScale ("Ruffle Frequency", Float) = 2.6
        _FineLumps ("Fine Ruffle Height (radii)", Float) = 0.012
        _FineScale ("Fine Ruffle Frequency", Float) = 8
        _Lobes ("Leading Lobes (radii)", Float) = 0.09

        [Header(Spikes)]
        _Spikes ("Spike Length (radii)", Float) = 0.14
        _SpikeScale ("Spike Density", Float) = 4.2
        _SpikeWidth ("Spike Width", Range(0.1, 0.9)) = 0.45
        _SpikeSharpness ("Spike Sharpness", Float) = 2.2
        _SpikeFlow ("Spike Curl", Float) = 1.4

        [Header(Arm and Mouth)]
        _Pull ("Body Drawn After Arm", Float) = 0.14
        _Flare ("Arm Base Flare", Float) = 2.6
        _Lag ("Arm Lag Behind Motion", Float) = 0.4
        _Peristalsis ("Swallowing Waves", Float) = 0.14
        _Cup ("Mouth Cup Depth", Float) = 0.45
        _Lip ("Lip Height (tip radii)", Float) = 0.25
        _Tendrils ("Wisp Frequency", Float) = 2.6
        _TendrilLength ("Wisp Length (radii)", Float) = 0.3
        _Speed ("Animation Speed", Float) = 1

        [Header(Ripples (read by Surface and RippleField))]
        _RippleAmplitude ("Ripple Amplitude", Float) = 1.8
        _RippleWavelength ("Ripple Wavelength", Float) = 3
        _RippleWidth ("Ripple Width", Float) = 1.6
        _RippleSpeed ("Ripple Speed", Float) = 5
        _RippleDecay ("Ripple Decay", Float) = 1.2
        _RippleInitialRadius ("Ripple Initial Radius", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #define _SPACE_WORLD 1           // the lumps are mapped by hand (body frame, metres)
        #include "BloodCellCore.hlsl"    // the red cells' properties, noise, lumps, bump (+ Core, RippleField)
        #include "ShrinkWrap.hlsl"       // the catch's real shape (ShrinkWrap.cs), for the lips to wrap round

        #include "WhiteBloodCell.hlsl"   // the shape: shared with WhiteBloodCellBake.compute

        struct Attributes
        {
            float4 positionOS : POSITION; // unit direction in the reach frame
            uint instanceID   : SV_InstanceID;
            uint vertexID     : SV_VertexID;
        };

        // Near cells: baked once a frame by WhiteBloodCellBake.compute (_UseBaked 1), one record per vertex per
        // instance. Far cells: shaped here (plain, cheap: no spikes or ruffles).
        struct Baked { float4 positionLump, mapMouth, normalTendril, mapTip; };
        StructuredBuffer<Baked> _Baked;
        uint _BakedStride; // vertices per instance
        float _UseBaked;

        struct Surfel { float3 positionWS, normalWS, mapPos; float4 mapTip; float lump, mouth, tendril; };

        float3 Place(Instance inst, Frame f, float3 p) { return inst.positionRadius.xyz + ToWorld(f, p) * inst.positionRadius.w; }

        // Rides impact ripples like a cell: offset, and the normal bent to match (two taps a hand's width away).
        void Ride(inout float3 p, inout float3 n)
        {
            if (_RippleFieldCellCount < 0.5) return;
            float3 t1 = normalize(cross(n, abs(n.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0))), t2 = cross(n, t1);
            const float E = 0.15;
            float3 p0 = p + RippleFieldOffset(p), p1 = p + t1 * E, p2 = p + t2 * E;
            p1 += RippleFieldOffset(p1);
            p2 += RippleFieldOffset(p2);
            float3 m = cross(p1 - p0, p2 - p0); // cross(t1, t2) = n: faces out
            p = p0;
            if (dot(m, m) > 1e-12) n = normalize(m);
        }

        Surfel Evaluate(Attributes v)
        {
            Surfel o;
            float3 p, n;
            if (_UseBaked > 0.5)
            {
                Baked b = _Baked[v.instanceID * _BakedStride + v.vertexID];
                p = b.positionLump.xyz;
                n = b.normalTendril.xyz;
                o.mapPos = b.mapMouth.xyz;
                o.lump = b.positionLump.w;
                o.mouth = b.mapMouth.w;
                o.tendril = b.normalTendril.w;
                o.mapTip = b.mapTip;
            }
            else
            {
                Instance inst = _Cells[_InstanceOffset + v.instanceID];
                Frame f = MakeFrame(inst);
                float3 d = normalize(v.positionOS.xyz);
                // Normal from two nearby directions: Cross(t1, t2) = d, so the cross faces out.
                float3 t1 = normalize(cross(d, abs(d.z) < 0.99 ? float3(0, 0, 1) : float3(1, 0, 0)));
                float3 t2 = cross(d, t1);
                const float E = 0.01;
                float a, b, c;
                float3 map, mx;
                float4 mapTip, mt;
                p = Place(inst, f, Shape(f, d, o.lump, o.mouth, o.tendril, map, mapTip));
                float3 w1 = Place(inst, f, Shape(f, normalize(d + t1 * E), a, b, c, mx, mt));
                float3 w2 = Place(inst, f, Shape(f, normalize(d + t2 * E), a, b, c, mx, mt));
                n = cross(w1 - p, w2 - p);
                n = dot(n, n) > 1e-14 ? normalize(n) : ToWorld(f, d);
                o.mapPos = map * inst.positionRadius.w; // material maps (m), see Shape
                o.mapTip = float4(mapTip.xyz * inst.positionRadius.w, mapTip.w);
            }
            Ride(p, n);
            o.positionWS = p;
            o.normalWS = n;
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment WhiteFrag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma shader_feature_local_fragment _SHADING_SMOOTH _SHADING_CEL

            #include "BloodCellForward.hlsl" // CellShade: the red cells' lighting

            struct WhiteVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 mapPos     : TEXCOORD2;
                float3 marks      : TEXCOORD3; // ruffle / spike crest, mouth, lip / wisps
                float  fog        : TEXCOORD4;
                nointerpolation uint id : TEXCOORD5;
                float4 mapTip     : TEXCOORD6; // the tip's material map, w = its share
            };

            WhiteVaryings vert(Attributes v)
            {
                Surfel s = Evaluate(v);
                WhiteVaryings o;
                o.positionWS = s.positionWS;
                o.positionCS = TransformWorldToHClip(s.positionWS);
                o.normalWS = s.normalWS;
                o.mapPos = s.mapPos;
                o.mapTip = s.mapTip;
                o.marks = float3(s.lump, s.mouth, s.tendril);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.id = _InstanceOffset + v.instanceID;
                return o;
            }

            // Fine creases (the ridged noise's value and gradient) and the red cells' lumps at one material map.
            void Relief(float3 map, float R, float seed, bool fineOn, float fade, out float fine, out float3 fineGrad, out float4 hd)
            {
                fine = 0.0;
                fineGrad = 0.0;
                if (fineOn)
                {
                    float t = _Time.y * _Speed + seed * 7.0;
                    fine = Ruffle(map / R * _FineScale + float3(t * 0.08, 0.0, -t * 0.05) + seed * 1.3, seed + 5.0, fineGrad);
                }
                hd = SurfaceHeight(map + seed * 13.1, fade);
            }

            // The inside of the mouth: not the membrane but wet flesh. Radial folds drawing into a dark throat (they
            // wander, and swallowing rings run down them), glossy, lit wrapped like flesh, in the same bands and hard
            // highlight as the cells. Polar coordinates round the mouth's axis from the instance (the spot and reach),
            // the folds' relief turned into a normal from its screen derivatives.
            half3 MouthShade(Instance inst, float3 positionWS, float4 positionCS, float3 geoN, float mouth)
            {
                float R = max(inst.positionRadius.w, 1e-3), seed = inst.motion.w;
                float3 axis = normalize(inst.reach.xyz);
                float3 spot = inst.positionRadius.xyz + axis * R * (1.0 + inst.reach.w);
                float3 bx = ReachSide(inst, axis); // the shape's own frame, so the folds turn with the lip
                float3 by = cross(axis, bx), local = positionWS - spot;
                float2 pl = float2(dot(local, bx), dot(local, by));
                float open = max(TipRadius(inst) * 0.92 * R, 1e-3); // the rim's radius (MOUTH_RIM round the rounded tip)
                float rr = length(pl) / open, phi = atan2(pl.y, pl.x + 1e-7); // atan2(0, 0) is NaN on D3D
                float t = _Time.y * _Speed + seed * 7.0;

                // Folds: ridges round the throat, wandering, fading out into its centre; rings swallowed inward.
                float wander = (Noise(float3(rr * 3.0 - t * 0.2, phi * 1.5, seed)) - 0.5) * 2.5;
                float ridge = 0.5 + 0.5 * cos(phi * round(_MouthFolds) + wander);
                ridge = ridge * ridge * smoothstep(0.08, 0.45, rr);
                float rings = 0.5 + 0.5 * sin(rr * 16.0 + t * 2.5);
                float relief = ridge + 0.35 * rings * rings * smoothstep(0.05, 0.3, rr);

                // Relief -> normal (surface gradient from screen derivatives).
                float height = relief * _MouthFoldDepth;
                float3 dpdx = ddx(positionWS), dpdy = ddy(positionWS);
                float3 r1 = cross(dpdy, geoN), r2 = cross(geoN, dpdx);
                float det = dot(dpdx, r1);
                float3 n = abs(det) > 1e-12 ? normalize(abs(det) * geoN - sign(det) * (ddx(height) * r1 + ddy(height) * r2)) : geoN;

                // Deeper = darker; crests catch the light.
                float deep = saturate(1.0 - rr) * saturate(mouth);
                half3 albedo = lerp(_MouthColor.rgb, _MouthDeepColor.rgb, smoothstep(0.0, 0.85, deep));
                albedo *= lerp(0.6, 1.15, relief / 1.35);

                float3 viewWS = GetWorldSpaceNormalizeViewDir(positionWS);
                Light light = GetMainLight(TransformWorldToShadowCoord(positionWS));
                float lit = saturate(dot(n, light.direction) * 0.5 + 0.5) * light.shadowAttenuation * light.distanceAttenuation;
                float3 hv = normalize(light.direction + viewWS);
                float spec = pow(saturate(dot(n, hv)), lerp(8.0, 90.0, _MouthWet)) * _MouthWet * light.shadowAttenuation;
            #ifdef _SHADING_CEL
                lit = QuantizeBand(lit, _LightBands, _BandSoftness);
                spec = smoothstep(_SpecThreshold - 0.03, _SpecThreshold + 0.03, spec) * _MouthWet;
            #endif
                float fres = pow(1.0 - saturate(dot(n, viewWS)), 3.0);
                half3 rgb = albedo * (light.color * lit + SampleSH(n))
                          + light.color * spec * 0.9
                          + _MouthColor.rgb * fres * 0.35 * (1.0 - deep);             // wet rim sheen
                float pulse = 0.75 + 0.25 * sin(_Time.y * 3.0 + seed);
                rgb += albedo * _MouthGlow * (0.25 + 0.75 * inst.state.x) * pulse
                     * (1.0 + 1.5 * saturate(abs(inst.extra.z))) * (0.4 + 0.6 * deep); // glows from the throat
                return rgb;
            }

            half4 WhiteFrag(WhiteVaryings i) : SV_Target
            {
                Instance inst = _Cells[i.id];
                float seed = inst.motion.w, hunger = inst.state.x, R = max(inst.positionRadius.w, 1e-3);
                float crest = saturate(i.marks.x), mouth = saturate(i.marks.y), tendril = saturate(i.marks.z);
                float3 geoN = normalize(i.normalWS);
                float detail = _LodDetail * inst.extra.x, fade = DetailFade(i.positionWS);

                // Relief on the body's map, the tip's, or (along the arm) both, blended keeping its contrast.
                float a = saturate(i.mapTip.w), fine = 0.0;
                float3 g = 0.0;
                float4 hd = float4(0.5, 0.0, 0.0, 0.0);
                if (a < 0.999) Relief(i.mapPos, R, seed, detail > 0.01, fade, fine, g, hd);
                if (a > 0.001)
                {
                    float fine2;
                    float3 g2;
                    float4 hd2;
                    Relief(i.mapTip.xyz, R, seed, detail > 0.01, fade, fine2, g2, hd2);
                    float keep = rsqrt(a * a + (1.0 - a) * (1.0 - a)); // two blended fields are flatter than either
                    fine = lerp(fine, fine2, a);
                    g = lerp(g, g2, a) * keep;
                    hd = lerp(hd, hd2, a);
                    hd = float4(0.5 + (hd.x - 0.5) * keep, hd.yzw * keep);
                }

                // Fine membrane creases tilt the normal first...
                float3 grad = WbcRotate(inst.rotation, g) * (_FineLumps * _FineScale * detail * (1.0 - mouth));
                geoN = normalize(geoN - (grad - geoN * dot(grad, geoN)));

                // ...then the red cells' own lumps, fluctuation and bump.
                float3 n = BumpNormal(geoN, float4(hd.x, WbcRotate(inst.rotation, hd.yzw)), float4(0, 0, 0, 0), fade);
                // Crests (spikes, folds) read as raised and thin; the mouth as deep.
                float h = saturate(hd.x * 0.75 + crest * 0.35 + fine * 0.2 - mouth * 0.4);
                half3 rgb = CellShade(i.positionWS, i.positionCS, a > 0.5 ? i.mapTip.xyz : i.mapPos, geoN, n, h, fade);

                // The lip and wisps: the membrane, flushed and glowing a little (brighter hungry, a flash per squeeze).
                float pulse = 0.75 + 0.25 * sin(_Time.y * 3.0 + seed);
                float m = saturate(tendril * 0.5 + mouth * 0.3);
                rgb = lerp(rgb, rgb * _MouthColor.rgb * 1.4, m * 0.6);
                rgb += _MouthColor.rgb * _MouthGlow * m * 0.5 * (0.25 + 0.75 * hunger) * pulse * (1.0 + 1.5 * saturate(abs(inst.extra.z)));
                // Inside: its own flesh.
                float inner = smoothstep(0.15, 0.7, mouth);
                rgb = lerp(rgb, MouthShade(inst, i.positionWS, i.positionCS, geoN, mouth), inner); // no branch: it takes screen derivatives
                return half4(MixFog(rgb, i.fog), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;

            float4 vert(Attributes v) : SV_POSITION
            {
                Surfel s = Evaluate(v);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 dir = normalize(_LightPosition - s.positionWS);
            #else
                float3 dir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(s.positionWS, s.normalWS, dir));
            #if UNITY_REVERSED_Z
                cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
            #else
                cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return cs;
            }
            half4 frag() : SV_Target { return 0; }
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
            float4 vert(Attributes v) : SV_POSITION { return TransformWorldToHClip(Evaluate(v).positionWS); }
            half4 frag() : SV_Target { return 0; }
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

            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Surfel s = Evaluate(v);
                Varyings o;
                o.positionCS = TransformWorldToHClip(s.positionWS);
                o.normalWS = s.normalWS;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
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

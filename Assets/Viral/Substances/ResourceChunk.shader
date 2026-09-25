// Resource chunks (ResourceField.cs, meshes from ChunkMesh.cs): soft, glassy lumps of glucose, protein...
// Drawn only by ResourceField, every chunk in one instanced call per (shape, LOD) mesh; each instance's
// data comes from _Chunks[_InstanceOffset + SV_InstanceID]: position + radius and rotation (the chunk's
// transform: chunks are walkable bodies, so the pose is physics', never moved here), tint (rgb,
// a = hovered), look = seed, extracted 0..1, agitation 0..1 (being extracted), unused.
// The deformation is here, in the vertex stage, shared by every pass (so depth, depth normals and the
// focus outlines follow): a faint breathing wobble (small: crawlers walk the undeformed mesh), and as
// it's extracted a growing lumpy deformation plus a squash-and-stretch pulse while it's being drained.
// Impact ripples (landing, slams) come from RippleField like everything riding a cell's waves: a chunk's
// Surface publishes its impacts there, shaped by this material's _Ripple* values (read by Surface /
// RippleField in C#; every chunk's hidden renderer carries this material). Normals follow the ripple.
// Mesh: vertex colour alpha = occlusion in the folds.
Shader "Custom/ResourceChunk"
{
    Properties
    {
        _Deform ("Deform When Drained", Float) = 0.3
        _Wobble ("Idle Wobble", Float) = 0.015
        _Translucency ("Translucency", Range(0, 1)) = 0.55
        _Smoothness ("Smoothness", Range(0, 1)) = 0.6
        _RimStrength ("Rim", Range(0, 2)) = 0.6

        [Header(Ripples (read in CSharp by Surface and RippleField))]
        _RippleAmplitude ("Ripple Amplitude (m at strength 1)", Float) = 0.1
        _RippleWavelength ("Ripple Wavelength", Float) = 1.4
        _RippleWidth ("Ripple Width", Float) = 0.9
        _RippleSpeed ("Ripple Speed", Float) = 3
        _RippleDecay ("Ripple Decay", Float) = 2
        _RippleInitialRadius ("Ripple Initial Radius", Float) = 0.2
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "../RippleField.hlsl"
        #include "../World/StreamFade.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _Deform, _Wobble, _Translucency, _Smoothness, _RimStrength;
            float _RippleAmplitude, _RippleWavelength, _RippleWidth, _RippleSpeed, _RippleDecay, _RippleInitialRadius;
        CBUFFER_END

        struct Instance { float4 positionScale, rotation, tint, look; };
        StructuredBuffer<Instance> _Chunks;
        uint _InstanceOffset;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float4 color      : COLOR;
            uint instanceID   : SV_InstanceID;
        };

        float3 Rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

        float3 Turn(float3 v, float3 k, float a)
        {
            float s, c;
            sincos(a, s, c);
            return v * c + cross(k, v) * s + k * dot(k, v) * (1.0 - c);
        }

        float Hash3(float3 p)
        {
            p = frac(p * 0.3183099 + 0.1);
            p *= 17.0;
            return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        float Noise3(float3 p)
        {
            float3 i = floor(p), f = frac(p);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(lerp(Hash3(i), Hash3(i + float3(1, 0, 0)), f.x), lerp(Hash3(i + float3(0, 1, 0)), Hash3(i + float3(1, 1, 0)), f.x), f.y),
                        lerp(lerp(Hash3(i + float3(0, 0, 1)), Hash3(i + float3(1, 0, 1)), f.x), lerp(Hash3(i + float3(0, 1, 1)), Hash3(i + 1), f.x), f.y), f.z);
        }

        // How far a point of the unit mesh moves along its normal.
        float Displace(float3 p, float4 look, float t)
        {
            float seed = look.x, drained = look.y, agit = look.z;
            float breathe = sin(dot(p, float3(3.1, 2.3, 2.7)) + t * (1.3 + agit * 3.0) + seed * 20.0) * _Wobble * (1.0 + agit * 1.5);
            float3 q = p * 2.2 + seed * 13.0 + float3(0, t * (0.25 + agit * 0.6), 0);
            float lumps = (Noise3(q) - 0.5) * 2.0 + (Noise3(q * 2.3 + 7.1) - 0.5) * 0.8;
            return breathe + lumps * _Deform * drained * (0.6 + 0.4 * drained);
        }

        float3 World(Attributes v, out float3 normalWS)
        {
            Instance inst = _Chunks[_InstanceOffset + v.instanceID];
            float4 look = inst.look;
            float t = _Time.y;
            float3 p = v.positionOS.xyz, n = normalize(v.normalOS);

            // Displaced along the normal; the normal bent by the displacement's slope (two taps).
            float3 t1 = normalize(cross(n, abs(n.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
            float3 t2 = cross(n, t1);
            const float E = 0.05;
            float d = Displace(p, look, t);
            float d1 = (Displace(p + t1 * E, look, t) - d) / E, d2 = (Displace(p + t2 * E, look, t) - d) / E;
            p += n * d;
            n = normalize(n - t1 * d1 - t2 * d2);

            // Squash and stretch along its own axis while drained.
            float3 axis = normalize(float3(sin(look.x * 31.0), cos(look.x * 17.0), sin(look.x * 7.0 + 1.0)));
            float squash = 1.0 + look.z * 0.14 * sin(t * 5.5 + look.x * 9.0);
            float along = dot(p, axis);
            p += axis * along * (squash - 1.0) + (p - axis * along) * (rsqrt(squash) - 1.0);

            normalWS = Rotate(inst.rotation, n);
            float3 world = inst.positionScale.xyz + Rotate(inst.rotation, p) * inst.positionScale.w;

            // Ripples (only while some surface is rippling): the offset here and two taps beside it
            // along the surface give the rippled surface's normal.
            if (_RippleFieldCellCount > 0.5)
            {
                const float R = 0.2;
                float3 a1 = normalize(cross(normalWS, abs(normalWS.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 a2 = cross(normalWS, a1);
                float3 o0 = RippleFieldOffset(world);
                float3 e1 = a1 * R + RippleFieldOffset(world + a1 * R) - o0;
                float3 e2 = a2 * R + RippleFieldOffset(world + a2 * R) - o0;
                float3 rn = cross(e1, e2);
                normalWS = normalize(dot(rn, normalWS) < 0.0 ? -rn : rn);
                world += o0;
            }
            return world;
        }

        float Fade(Attributes v) { return StreamFade(_Chunks[_InstanceOffset + v.instanceID].positionScale.xyz); }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2; // tint, occlusion
                float2 extra      : TEXCOORD3; // hover, drained
                float  fog        : TEXCOORD4;
                nointerpolation float fade : TEXCOORD5;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = World(v, o.normalWS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                Instance inst = _Chunks[_InstanceOffset + v.instanceID];
                o.color = float4(inst.tint.rgb, v.color.a);
                o.extra = float2(inst.tint.a, inst.look.y);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.fade = Fade(v);
                o.positionCS = StreamFadeHide(o.positionCS, o.fade);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                StreamFadeClip(i.fade, i.positionCS.xy);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light l = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float atten = l.shadowAttenuation * l.distanceAttenuation;

                float3 albedo = i.color.rgb;
                float ao = i.color.a;
                float nl = dot(n, l.direction);
                float wrap = saturate((nl + 0.6) / 1.6);                   // soft, jelly-like
                float back = pow(saturate(dot(v, -l.direction)), 3.0) * _Translucency * (1.0 - saturate(nl));
                float3 h = normalize(l.direction + v);
                float spec = pow(saturate(dot(n, h)), exp2(10.0 * _Smoothness + 1.0)) * _Smoothness;
                float fres = pow(1.0 - saturate(dot(n, v)), 3.0);

                float3 col = albedo * (SampleSH(n) * ao + l.color * (wrap * atten * (0.5 + 0.5 * ao) + back));
                col += l.color * spec * atten * ao;
                col += lerp(albedo, 1.0, 0.5) * fres * _RimStrength * (0.4 + 0.6 * ao); // glassy edges
                col += albedo * i.extra.x * 0.25;                                      // pointed at
                col = MixFog(col, i.fog);
                return half4(col, 1);
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
                float3 n;
                float3 p = World(v, n);
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                float3 dir = normalize(_LightPosition - p);
            #else
                float3 dir = _LightDirection;
            #endif
                float4 cs = TransformWorldToHClip(ApplyShadowBias(p, n, dir));
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
            struct Varyings { float4 positionCS : SV_POSITION; nointerpolation float fade : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                float3 n;
                Varyings o;
                o.fade = Fade(v);
                o.positionCS = StreamFadeHide(TransformWorldToHClip(World(v, n)), o.fade);
                return o;
            }
            half4 frag(Varyings i) : SV_Target { StreamFadeClip(i.fade, i.positionCS.xy); return 0; }
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

            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; nointerpolation float fade : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.fade = Fade(v);
                o.positionCS = StreamFadeHide(TransformWorldToHClip(World(v, o.normalWS)), o.fade);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                StreamFadeClip(i.fade, i.positionCS.xy);
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

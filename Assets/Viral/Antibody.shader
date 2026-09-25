// Antibodies (ImmuneSystem.cs, mesh from AntibodyMesh.cs): a lumpy, glassy Y in vertex colours
// (blue, magenta in the folds, teal on the tops), wiggling in the vertex stage:
// - the arms flap about the hinge (together and against each other), bend out of the plane and twist;
// - the stem sways; a wave crawls along every chain.
// - stuck on a virus (grip): the arms open out nearly flat (_HugOpen), the stem flops over (_StemLean),
//   then the whole antibody is bent round the virus's centre (wrap = hinge-to-centre distance, from
//   AntibodyHold) both ways, so the arms follow its curve like fingers round a ball; a slow squeeze;
//   the free flapping is damped so neighbours don't swing into each other.
// Mesh: uv0.x = part (0 hinge, 1 stem, 2 left arm, 3 right arm), uv0.y = 0 at the hinge .. 1 at the tip.
// Drawn only by ImmuneSystem, every antibody in one instanced call per LOD mesh: each instance's pose
// and wiggle come from _Antibodies[_InstanceOffset + SV_InstanceID] (position + scale, rotation
// quaternion, wiggle = seed, agitation (speed + size), grip 0..1, wrap (object units; 0 = none)).
// Every pass deforms the same way, so depth, depth normals and outlines follow the wiggle.
Shader "Custom/Antibody"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Flap ("Arm Flap (deg)", Float) = 14
        _Bend ("Out of Plane Bend (deg)", Float) = 10
        _Sway ("Stem Sway (deg)", Float) = 12
        _Wave ("Chain Wave", Float) = 0.025
        _Speed ("Speed", Float) = 2.2
        _HugOpen ("Hug Arm Opening (deg)", Float) = 34
        _StemLean ("Hug Stem Lean (deg)", Float) = 22
        _Squeeze ("Hug Squeeze (deg)", Float) = 4
        _Rim ("Rim Glow", Color) = (0.55, 0.75, 1, 1)
        _Translucency ("Translucency", Range(0, 1)) = 0.45
        _Smoothness ("Smoothness", Range(0, 1)) = 0.75
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _Tint, _Rim;
            float _Flap, _Bend, _Sway, _Wave, _Speed, _HugOpen, _StemLean, _Squeeze, _Translucency, _Smoothness;
        CBUFFER_END

        struct Instance { float4 positionScale, rotation, wiggle; };
        StructuredBuffer<Instance> _Antibodies;
        uint _InstanceOffset;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float4 color      : COLOR;
            float2 uv         : TEXCOORD0;
            uint instanceID   : SV_InstanceID;
        };

        // Rodrigues: v turned by angle a (radians) about unit axis k.
        float3 Turn(float3 v, float3 k, float a)
        {
            float s, c;
            sincos(a, s, c);
            return v * c + cross(k, v) * s + k * dot(k, v) * (1.0 - c);
        }

        // The wiggle, in object space (the hinge is the origin, the Y lies in the XY plane).
        void Wiggle(inout float3 p, inout float3 n, float2 uv, float4 wiggle)
        {
            float part = round(uv.x);
            if (part < 0.5) return; // the hinge stays put
            float seed = wiggle.x, agit = wiggle.y, grip = wiggle.z;
            float t = _Time.y * _Speed * (0.6 + 0.4 * agit) + seed * 17.0;
            float amp = (0.5 + 0.5 * agit) * (1.0 - 0.75 * grip); // held on: small moves only
            float w = smoothstep(0.0, 1.0, uv.y);  // bends most at the tip
            const float D = 0.01745329;

            if (part < 1.5)
            {
                // Stem: sways both ways, a lazy wobble.
                float ax = sin(t * 0.8 + 1.3) * _Sway * amp * D * w;
                float az = sin(t * 0.63 + 4.1) * _Sway * amp * D * w;
                p = Turn(p, float3(1, 0, 0), ax); n = Turn(n, float3(1, 0, 0), ax);
                p = Turn(p, float3(0, 0, 1), az); n = Turn(n, float3(0, 0, 1), az);
                // Held on: flopped over to one side (by its seed), across the arms.
                float lean = grip * _StemLean * D * (frac(seed * 3.7) < 0.5 ? -1.0 : 1.0);
                p = Turn(p, float3(1, 0, 0), lean); n = Turn(n, float3(1, 0, 0), lean);
            }
            else
            {
                float side = part < 2.5 ? 1.0 : -1.0; // left arm (part 2) leans -x
                float3 axis = float3(-side * 0.7193, 0.6947, 0); // AntibodyMesh.ArmAngle (46 deg)
                // Flap: in the Y's plane. Together (a sway) plus against each other (a clap).
                float clap = sin(t + seed * 3.0) * _Flap;
                float sway = sin(t * 0.71 + 2.0) * _Flap * 0.5;
                float flap = (clap * side + sway) * amp * D;
                // Bend out of the plane (about the arm's in-plane normal) and twist about its own axis.
                float bend = sin(t * 0.87 + side * 1.7) * _Bend * amp * D;
                float twist = sin(t * 0.55 + side * 2.9) * 12.0 * amp * D;
                float3 across = cross(float3(0, 0, 1), axis);
                p = Turn(p, axis, twist * w);          n = Turn(n, axis, twist * w);
                p = Turn(p, across, bend * w);         n = Turn(n, across, bend * w);
                p = Turn(p, float3(0, 0, 1), flap * w); n = Turn(n, float3(0, 0, 1), flap * w);
                // Hug: the whole arm opened out nearly flat (Wrap then curves it onto the body), squeezing slowly.
                float open = side * grip * (_HugOpen + sin(t * 0.9 + seed * 5.0) * _Squeeze) * D;
                p = Turn(p, float3(0, 0, 1), open); n = Turn(n, float3(0, 0, 1), open);
            }

            // A wave crawling out along the chain, pushing the surface in and out.
            p += n * sin(uv.y * 14.0 - t * 2.3) * _Wave * amp * w;
        }

        // Held on: bend the antibody round the virus's centre, (0, wrap, 0) in object space (+y points in),
        // both ways: distance across (x, then z) becomes arc length at the hinge's radius and depth (y)
        // becomes radius, so anything laid flat at the hinge's height lies on a sphere round the body.
        void Wrap(inout float3 p, inout float3 n, float wrap, float grip)
        {
            if (grip <= 0.0 || wrap <= 0.0) return;
            float3 q = p, m = n;
            float a = q.x / wrap, rho = wrap - q.y;
            q = float3(sin(a) * rho, wrap - cos(a) * rho, q.z);
            m = Turn(m, float3(0, 0, 1), a);
            float b = q.z / wrap;
            rho = wrap - q.y;
            q = float3(q.x, wrap - cos(b) * rho, sin(b) * rho);
            m = Turn(m, float3(1, 0, 0), -b);
            p = lerp(p, q, grip);
            n = normalize(lerp(n, m, grip));
        }

        float3 Rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

        float3 WiggledWorld(Attributes v, out float3 normalWS)
        {
            Instance inst = _Antibodies[_InstanceOffset + v.instanceID];
            float3 p = v.positionOS.xyz, n = v.normalOS;
            Wiggle(p, n, v.uv, inst.wiggle);
            Wrap(p, n, inst.wiggle.w, inst.wiggle.z);
            normalWS = Rotate(inst.rotation, n); // uniform scale
            return inst.positionScale.xyz + Rotate(inst.rotation, p) * inst.positionScale.w;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 color      : TEXCOORD2;
                float  fog        : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = WiggledWorld(v, o.normalWS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color * _Tint;
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light l = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                float atten = l.shadowAttenuation * l.distanceAttenuation;

                float3 albedo = i.color.rgb;
                float ao = i.color.a; // folds between the lumps are darker
                float nl = dot(n, l.direction);
                float wrap = saturate((nl + 0.5) / 1.5);                   // soft, jelly-like
                float back = pow(saturate(dot(v, -l.direction)), 3.0) * _Translucency * (1.0 - saturate(nl));
                float3 h = normalize(l.direction + v);
                float spec = pow(saturate(dot(n, h)), exp2(10.0 * _Smoothness + 1.0)) * _Smoothness;
                float fres = pow(1.0 - saturate(dot(n, v)), 3.0);

                float3 col = albedo * (SampleSH(n) * ao + l.color * (wrap * atten + back));
                col += l.color * spec * atten;
                col += _Rim.rgb * fres * (0.4 + 0.6 * ao);                   // glassy edges
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
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;

            float4 vert(Attributes v) : SV_POSITION
            {
                float3 n;
                float3 p = WiggledWorld(v, n);
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
            float4 vert(Attributes v) : SV_POSITION
            {
                float3 n;
                return TransformWorldToHClip(WiggledWorld(v, n));
            }
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
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformWorldToHClip(WiggledWorld(v, o.normalWS));
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

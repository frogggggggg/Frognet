// Platelets (Platelets.cs): spiky little stars zooming along the vessel's wall layers. One indirect draw per LOD;
// the instance's pose comes from _Posed[_Visible[_GroupBase + instance]] (Platelets.compute). Every platelet is shaped
// from its seed here, in the vertex stage, from one shared template: a lumpy body with bulges where its tendrils
// leave it, and up to SPIKES tendrils (spread round it, jittered, some missing), each tapering to a point, bent to
// one side and slowly swaying. Cel shaded in two colours like the cells, with a rim; fogged + atmospheric
// perspective; every pass shapes and clips alike (dithered fade at the draw distance), so depth / outlines follow.
Shader "Custom/Platelets"
{
    Properties
    {
        _Color ("Colour", Color) = (0.97, 0.74, 0.6, 1)
        _DeepColor ("Shade", Color) = (0.72, 0.4, 0.34, 1)
        _Rim ("Rim", Range(0, 2)) = 0.45
        _Sway ("Tendril sway", Float) = 0.25
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "StreamFade.hlsl"

        #define SPIKES 8 // Platelets.Spikes

        CBUFFER_START(UnityPerMaterial)
            half4 _Color, _DeepColor;
            float _Rim, _Sway;
        CBUFFER_END

        struct Posed { float4 position, rotation, extra; };
        StructuredBuffer<Posed> _Posed;
        StructuredBuffer<uint> _Visible;
        uint _GroupBase;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            float2 uv         : TEXCOORD0; // x 0 body, k + 1 tendril k; y along it (0..1)
            uint instanceID   : SV_InstanceID;
        };

        float3 Rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

        float4 Hash4(float a, float b)
        {
            float4 q = float4(a * 127.1 + b * 311.7, a * 269.5 + b * 183.3, a * 419.2 + b * 371.9, a * 93.7 + b * 157.1);
            return frac(sin(q) * 43758.5453);
        }

        struct Tendril { float3 dir, side, side2; float len, width, bend; };

        // Tendril k of a platelet: spread round the body (golden spiral, turned by the seed, jittered), a share missing.
        Tendril GetTendril(uint k, float seed)
        {
            Tendril o;
            float4 h = Hash4(seed * 13.7 + 1.3, k + 1.0);
            float4 g = Hash4(seed * 5.3 + 7.1, k + 11.0);
            float y = 1.0 - (k + 0.5) * (2.0 / SPIKES);
            float rr = sqrt(saturate(1.0 - y * y));
            float a = k * 2.39996323 + seed * 6.2831853;
            o.dir = normalize(float3(cos(a) * rr, y, sin(a) * rr) + (h.xyz - 0.5) * 0.7);
            float keep = lerp(0.5, 1.05, frac(seed * 7.31)); // 4..8 tendrils
            o.len = g.x < keep ? lerp(1.1, 3.4, g.y * g.y) : 0.0;
            o.width = lerp(0.17, 0.3, g.z);
            float3 up = abs(o.dir.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
            float3 p1 = normalize(cross(o.dir, up)), p2 = cross(o.dir, p1);
            float b = h.w * 6.2831853;
            o.side = p1 * cos(b) + p2 * sin(b);
            o.side2 = cross(o.dir, o.side);
            o.bend = (g.w - 0.5) * 1.1;
            return o;
        }

        // Body radius toward d: lumpy, swelling where tendrils leave it.
        float BodyRadius(float3 d, float seed, float3 dirs[SPIKES], float lens[SPIKES])
        {
            float r = 1.0 + 0.07 * sin(dot(d, float3(3.1, 1.7, -2.3)) + seed * 31.0)
                          + 0.05 * sin(dot(d, float3(-1.9, 4.3, 2.7)) + seed * 17.0);
            [unroll] for (int k = 0; k < SPIKES; k++)
            {
                float c = saturate(dot(d, dirs[k]));
                c *= c; c *= c; c *= c; // ^8
                r += 0.32 * saturate(lens[k]) * c;
            }
            return r;
        }

        // Object space (body radius 1); along = 0 on the body, 0..1 along a tendril.
        void Shape(Attributes v, float seed, out float3 p, out float3 n, out float along)
        {
            along = v.uv.y;
            if (v.uv.x < 0.5)
            {
                float3 dirs[SPIKES];
                float lens[SPIKES];
                [unroll] for (int k = 0; k < SPIKES; k++)
                {
                    Tendril td = GetTendril(k, seed);
                    dirs[k] = td.dir;
                    lens[k] = td.len;
                }
                float3 d = normalize(v.positionOS.xyz);
                float3 up = abs(d.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 t1 = normalize(cross(d, up)), t2 = cross(d, t1);
                const float e = 0.05;
                float3 da = normalize(d + t1 * e), db = normalize(d + t2 * e);
                p = d * BodyRadius(d, seed, dirs, lens);
                float3 pa = da * BodyRadius(da, seed, dirs, lens), pb = db * BodyRadius(db, seed, dirs, lens);
                n = normalize(cross(pb - p, pa - p));
                n = dot(n, d) < 0.0 ? -n : n;
            }
            else
            {
                Tendril td = GetTendril((uint)round(v.uv.x - 1.0), seed);
                float t = v.uv.y;
                float len = td.len, k = v.uv.x;
                float wig = sin(_Time.y * (1.1 + seed) + k * 1.7 + seed * 20.0) * _Sway;
                float3 bendV = td.side * td.bend + td.side2 * wig;
                float3 c = td.dir * (0.72 + t * len) + bendV * (len * t * t);
                float3 T = normalize(td.dir * max(len, 1e-3) + bendV * (len * 2.0 * t));
                float3 N1 = normalize(td.side - T * dot(td.side, T)), N2 = cross(T, N1);
                float rad = td.width * saturate(len * 4.0) * (0.1 + 0.9 * (1.0 - t) * (1.0 - t));
                float3 ring = N1 * v.positionOS.x + N2 * v.positionOS.y;
                bool tip = v.positionOS.z > 0.5;
                p = tip ? c : c + ring * rad;
                n = tip ? T : normalize(ring + T * 0.2);
            }
        }

        // World position and normal; extra.x seed, extra.y fade, along = 0 body .. 1 tendril tip.
        float3 World(Attributes v, out float3 normalWS, out float fade, out float along)
        {
            Posed o = _Posed[_Visible[_GroupBase + v.instanceID]];
            fade = o.extra.y;
            float3 p, n;
            Shape(v, o.extra.x, p, n, along);
            normalWS = Rotate(o.rotation, n);
            return o.position.xyz + Rotate(o.rotation, p) * o.position.w;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float  fog        : TEXCOORD2;
                float  along      : TEXCOORD3;
                nointerpolation float fade : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = World(v, o.normalWS, o.fade, o.along);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                StreamFadeClip(i.fade, i.positionCS.xy);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light l = GetMainLight();
                // Cel bands on a half-Lambert, softened over a pixel so thin tendrils don't crawl.
                float h = dot(n, l.direction) * 0.5 + 0.5;
                float w = max(fwidth(h), 1e-4);
                float band = 0.35 + 0.35 * smoothstep(0.38 - w, 0.38 + w, h) + 0.3 * smoothstep(0.68 - w, 0.68 + w, h);
                float3 col = lerp(_DeepColor.rgb, _Color.rgb, band);
                col *= lerp(1.0, 1.08, i.along); // tendril tips a touch paler
                col += _Color.rgb * pow(1.0 - saturate(dot(n, v)), 3.0) * _Rim;
                return half4(MixFog(MixAtmosphere(col, i.positionWS), i.fog), 1);
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
            struct Varyings { float4 positionCS : SV_POSITION; nointerpolation float fade : TEXCOORD0; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 n;
                float along;
                o.positionCS = TransformWorldToHClip(World(v, n, o.fade, along));
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
                float along;
                o.positionCS = TransformWorldToHClip(World(v, o.normalWS, o.fade, along));
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

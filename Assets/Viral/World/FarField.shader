// The far field's stand-ins (FarField.cs): streamed things beyond the loaded bubble, drawn from records. One
// indirect draw per (look, LOD); the instance's pose comes from _FarPosed[_FarVisible[_FarGroupBase + instance]],
// written by FarField.compute. Cel shaded in two colours like the cells (bands from the main light, a rim), fogged
// by the scene fog so it matches the real objects where they cross over. Every pass clips alike: the object's own
// fade (far edge, fading in when generated) and the complement of the real object's stream fade.
Shader "Custom/FarField"
{
    Properties
    {
        _Color ("Colour", Color) = (1, 0.2, 0.2, 1)
        _DeepColor ("Shade", Color) = (0.3, 0.05, 0.1, 1)
        _Rim ("Rim", Range(0, 2)) = 0.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "StreamFade.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Color, _DeepColor;
            float _Rim;
        CBUFFER_END

        struct Posed { float4 position, rotation, scale; };
        StructuredBuffer<Posed> _FarPosed;
        StructuredBuffer<uint> _FarVisible;
        uint _FarGroupBase;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS   : NORMAL;
            uint instanceID   : SV_InstanceID;
        };

        float3 Rotate(float4 q, float3 v) { return v + 2.0 * cross(q.xyz, cross(q.xyz, v) + q.w * v); }

        // World position and normal; fades.x = its own, .y = the real object's; seed = per instance.
        float3 World(Attributes v, out float3 normalWS, out float2 fades, out float seed)
        {
            Posed p = _FarPosed[_FarVisible[_FarGroupBase + v.instanceID]];
            fades = float2(p.position.w, p.scale.w);
            seed = frac(dot(p.position.xyz, float3(0.1031, 0.1130, 0.0973)));
            normalWS = normalize(Rotate(p.rotation, v.normalOS / p.scale.xyz));
            return p.position.xyz + Rotate(p.rotation, v.positionOS.xyz * p.scale.xyz);
        }

        void FarClip(float2 fades, float2 pixel)
        {
            StreamFadeClip(fades.x, pixel);
            StreamFadeClipComplement(fades.y, pixel);
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
                nointerpolation float3 extra : TEXCOORD3; // fades, seed
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float2 fades;
                float seed;
                o.positionWS = World(v, o.normalWS, fades, seed);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.fog = ComputeFogFactor(o.positionCS.z);
                o.extra = float3(fades, seed);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                FarClip(i.extra.xy, i.positionCS.xy);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light l = GetMainLight();
                // Cel bands on a half-Lambert, softened over a pixel so far silhouettes don't crawl.
                float h = dot(n, l.direction) * 0.5 + 0.5;
                float w = max(fwidth(h), 1e-4);
                float band = 0.35 + 0.35 * smoothstep(0.38 - w, 0.38 + w, h) + 0.3 * smoothstep(0.68 - w, 0.68 + w, h);
                float3 col = lerp(_DeepColor.rgb, _Color.rgb, band) * (0.9 + 0.2 * i.extra.z);
                col += _Color.rgb * pow(1.0 - saturate(dot(n, v)), 3.0) * _Rim;
                return half4(MixFog(col, i.fog), 1);
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

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 n;
                float seed;
                o.positionCS = TransformWorldToHClip(World(v, n, o.fades, seed));
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

            Varyings vert(Attributes v)
            {
                Varyings o;
                float seed;
                o.positionCS = TransformWorldToHClip(World(v, o.normalWS, o.fades, seed));
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

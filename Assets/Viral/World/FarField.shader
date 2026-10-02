// The far field's plain stand-ins (FarField.cs): streamed things beyond the loaded bubble, drawn from records. One
// indirect draw per (look, LOD); pose and clipping in FarFieldCore.hlsl. Cel shaded in two colours (bands from the
// main light, a rim), fogged by the scene fog. For things whose prefab has no cell material (chunks, viruses);
// cells use Custom/FarFieldCell, the real cell shading.
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
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry+50" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "FarFieldCore.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Color, _DeepColor;
            float _Rim;
        CBUFFER_END
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

            Varyings vert(FarAttributes v)
            {
                Varyings o;
                float2 fades;
                float seed;
                o.positionWS = FarWorld(v, o.normalWS, fades, seed);
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

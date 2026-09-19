// Unity ShaderLab - Universal Render Pipeline
Shader "Custom/SeamlessTriplanarUnlit"
{
    Properties
    {
        _MainTex ("Main Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _Scale ("Texture Scale (tiles per world unit)", Float) = 1.0
        _TileX ("Tile X", Float) = 1.0
        _TileY ("Tile Y", Float) = 1.0
        _TileZ ("Tile Z", Float) = 1.0
        _Offset ("Offset (XYZ)", Vector) = (0,0,0,0)

        _BlendSharpness ("Blend Sharpness", Range(1, 32)) = 6.0

        [KeywordEnum(World, Object)] _Space ("Mapping Space", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }
        LOD 100

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "TriplanarCore.cginc"

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half4  _Color;
            float  _Scale;
            float  _TileX;
            float  _TileY;
            float  _TileZ;
            float4 _Offset;
            float  _BlendSharpness;
        CBUFFER_END

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);

        float3 ObjectScale()
        {
            return float3(length(unity_ObjectToWorld._m00_m10_m20),
                          length(unity_ObjectToWorld._m01_m11_m21),
                          length(unity_ObjectToWorld._m02_m12_m22));
        }

        ENDHLSL

        Pass
        {
            Name "Unlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile_local _SPACE_WORLD _SPACE_OBJECT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 mapPos      : TEXCOORD0;
                float3 mapNrm      : TEXCOORD1;
                float  fogCoord    : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.fogCoord    = ComputeFogFactor(output.positionHCS.z);

            #ifdef _SPACE_OBJECT
                // Texture sticks to the mesh: it moves and rotates with the
                // object. Multiplied by scale so tile size stays in world
                // units -- scaling the mesh adds tiles, it does not stretch
                // the ones already there.
                output.mapPos = input.positionOS.xyz * ObjectScale();
                output.mapNrm = input.normalOS;
            #else
                // World space: adjacent objects share one continuous texture field.
                output.mapPos = TransformObjectToWorld(input.positionOS.xyz);
                output.mapNrm = TransformObjectToWorldNormal(input.normalOS);
            #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 n = normalize(input.mapNrm);
                float3 p = input.mapPos * _Scale * float3(_TileX, _TileY, _TileZ)
                         + _Offset.xyz;

                half4 col = TriplanarSample(
                    TEXTURE2D_ARGS(_MainTex, sampler_MainTex),
                    p, n, _BlendSharpness) * _Color;

                col.rgb = MixFog(col.rgb, input.fogCoord);
                return col;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag
            #pragma target 3.5
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings depthVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 depthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}

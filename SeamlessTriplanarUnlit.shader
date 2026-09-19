// Unity ShaderLab - Built-in Render Pipeline
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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _SPACE_WORLD _SPACE_OBJECT
            #include "UnityCG.cginc"
            #include "TriplanarCore.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Scale, _TileX, _TileY, _TileZ;
            float4 _Offset;
            float _BlendSharpness;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 mapPos : TEXCOORD0;
                float3 mapNrm : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);

            #ifdef _SPACE_OBJECT
                // Texture sticks to the mesh: it moves and rotates with the object.
                o.mapPos = v.vertex.xyz;
                o.mapNrm = v.normal;
            #else
                // World space: adjacent objects share one continuous texture field.
                o.mapPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.mapNrm = UnityObjectToWorldNormal(v.normal);
            #endif
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.mapNrm);
                float3 p = i.mapPos * _Scale * float3(_TileX, _TileY, _TileZ) + _Offset.xyz;

                return TriplanarSample(_MainTex, p, n, _BlendSharpness) * _Color;
            }
            ENDCG
        }

        UsePass "Legacy Shaders/VertexLit/SHADOWCASTER"
    }

    FallBack "Diffuse"
}

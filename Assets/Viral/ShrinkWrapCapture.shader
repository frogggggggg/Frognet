// ShrinkWrap.cs draws an object's renderers with this into one hemisphere of its radial shape map: each vertex
// goes where its direction from _WrapCentre lands on the paraboloid (ShrinkWrap.hlsl), and keeps the farthest
// distance (BlendOp Max over RHalf, no depth). _WrapCentre.w picks the hemisphere (+1 = +Z, -1 = -Z). A
// triangle with any vertex more than 53° past the hemisphere's edge is dropped whole (its paraboloid position
// runs off to infinity); the other hemisphere has it.
Shader "Hidden/ShrinkWrapCapture"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #define SHRINK_WRAP_CAPTURE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ShrinkWrap.hlsl"

            float4 _WrapCentre;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float  distance   : TEXCOORD0;
                float  inside     : TEXCOORD1; // 1 at every vertex = the whole triangle is on this side
            };

            Varyings vert(Attributes a)
            {
                float3 offset = TransformObjectToWorld(a.positionOS.xyz) - _WrapCentre.xyz;
                float d = length(offset);
                float3 dir = offset / max(d, 1e-5);
                Varyings o;
                o.positionCS = float4(ShrinkWrapUV(dir, _WrapCentre.w), 0.5, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
                o.positionCS.y = -o.positionCS.y; // so the reader's v = 0.5 + 0.5 y on every API
            #endif
                o.distance = d;
                o.inside = dir.z * _WrapCentre.w > -0.6 ? 1.0 : 0.0;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                clip(i.inside - 0.999);
                return i.distance;
            }
            ENDHLSL
        }
    }
}

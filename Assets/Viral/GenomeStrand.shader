// DNA diagram in the head view (GenomeView.cs), drawn into its render texture: flat 2D shapes
// (backbone ribbons, base-pair rungs, baselines, the scan line) in their vertex colours, like an
// analysis readout. _Color.rgb dims a strand (while another is loaded), _Color.a highlights it
// (hover / loaded).
Shader "Hidden/GenomeStrand"
{
    Properties
    {
        _Color ("Tint (a = highlight)", Color) = (1, 1, 1, 0)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }

        Pass
        {
            Name "Diagram"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float3 c = i.color.rgb * _Color.rgb * (1.0 + 0.5 * _Color.a);
                return half4(c, i.color.a);
            }
            ENDHLSL
        }
    }
}

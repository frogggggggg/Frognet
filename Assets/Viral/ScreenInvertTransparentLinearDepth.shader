Shader "Hidden/ScreenInvertTransparentLinearDepth"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Opaque"
        }

        Pass
        {
            Name "TransparentLinearDepth"

            ZWrite Off
            ZTest LEqual
            Cull Off

            // Multiple transparent surfaces can overlap. Keep the nearest
            // normalized linear depth in the single-channel render texture.
            Blend One One
            BlendOp Min

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionCS =
                    TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half frag(Varyings input) : SV_Target
            {
                // SV_POSITION.z is device depth in the fragment stage.
                // Convert to 0..1 linear depth so a clear value of 1 means
                // "no transparent geometry here" on every graphics API.
                return Linear01Depth(
                    input.positionCS.z,
                    _ZBufferParams);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

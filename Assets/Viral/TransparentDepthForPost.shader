// Used by TransparentDepthForPostFeature as an override shader: stamps transparent
// objects and world-space UI into the camera depth texture before post-processing,
// so depth of field sees them instead of whatever is behind. Keeps each object's
// own material properties, so _MainTex alpha cuts sprite / glyph shapes.
Shader "Hidden/TransparentDepthForPost"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        float4 _MainTex_ST;
        float  _TransparentDepthCutoff; // global, set by the feature
        float  _TransparentDepthUseTexture; // global: 1 for UI / sprites / text, 0 for lit transparents (may have no _MainTex)

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings vert(Attributes i)
        {
            Varyings o;
            o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
            o.uv = TRANSFORM_TEX(i.uv, _MainTex);
            return o;
        }

        // Texture alpha only (vertex colours aren't guaranteed on every mesh), and only for
        // things that actually have a _MainTex: others would sample a fallback that may be black.
        void Cut(Varyings i)
        {
            if (_TransparentDepthUseTexture > 0.5)
                clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a - _TransparentDepthCutoff);
        }

        float4 fragRaw(Varyings i) : SV_Target
        {
            Cut(i);
            return float4(i.positionCS.z, 0, 0, 0);
        }
        ENDHLSL

        // 0: the depth texture is a real depth buffer.
        Pass
        {
            Name "DepthBuffer"
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            void frag(Varyings i) { Cut(i); }
            ENDHLSL
        }

        // 1: the depth texture is a colour copy (R32), reversed Z: nearer is larger.
        Pass
        {
            Name "DepthCopyReversed"
            ZWrite Off
            ZTest Always
            Cull Off
            BlendOp Max
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragRaw
            ENDHLSL
        }

        // 2: colour copy, conventional Z: nearer is smaller.
        Pass
        {
            Name "DepthCopy"
            ZWrite Off
            ZTest Always
            Cull Off
            BlendOp Min
            Blend One One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragRaw
            ENDHLSL
        }

        // 3: debug. Paints what would be stamped, over the image.
        Pass
        {
            Name "Debug"
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragDebug
            half4 fragDebug(Varyings i) : SV_Target { Cut(i); return half4(1, 0, 1, 0.6); }
            ENDHLSL
        }
    }
}

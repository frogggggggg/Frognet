// The head view's container (GenomeView.cs): a full-screen UI image drawing one shape out of a
// distance field -- the virus's real head on screen (_BubbleA), a tube growing out of it, and the
// sphere at its end (_BubbleB) -- smooth-unioned so the tube flares out of the head and pinches into
// the sphere like a drop. One outline runs round all three, so the tube is joined to the head; on the
// head only a ring at its edge is filled (covering the head's own outline where the tube leaves it),
// and the real head shows through the middle. Flat focus-mode look: the sweep's
// dark fill and player outline; the DNA render (_MainTex) shows inside the sphere. Everything is in
// canvas units, measured from the image's uv.
Shader "Hidden/GenomeBubble"
{
    Properties
    {
        _MainTex ("DNA", 2D) = "black" {}
        _Fill ("Inside", Color) = (0.016, 0.055, 0.16, 0.8)
        _Outline ("Outline", Color) = (0.95, 0.97, 1, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "Bubble"
            Blend One OneMinusSrcAlpha // premultiplied
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _Fill, _Outline;
            float4 _BubbleA;   // xy head centre, z head radius, w blend into the head
            float4 _BubbleB;   // xy sphere centre, z its radius, w blend into the sphere
            float4 _BubbleArea; // xy canvas size, z tube radius, w outline width
            float  _BubbleDNA; // 0..1 how much of the DNA shows
            float  _BubbleHead; // 0..1 how much of the ring round the head shows (fades in as it opens)

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float SMin(float a, float b, float k)
            {
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(b, a, h) - k * h * (1.0 - h);
            }

            float Capsule(float2 p, float2 a, float2 b, float r)
            {
                float2 pa = p - a, ba = b - a;
                float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-4));
                return length(pa - ba * h) - r;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 p = i.uv * _BubbleArea.xy;
                float2 A = _BubbleA.xy, B = _BubbleB.xy;
                float rA = _BubbleA.z, rB = _BubbleB.z, rT = _BubbleArea.z, edge = _BubbleArea.w;

                float dA = length(p - A) - rA;
                float dB = length(p - B) - rB;
                float dT = rT > 0.0 ? Capsule(p, A, B, rT) : 1e5;
                float d = SMin(SMin(dA, dT, max(_BubbleA.w, 1e-3)), dB, max(_BubbleB.w, 1e-3));

                // On the head only a ring at its edge; the real head shows through the middle. Where
                // the head is nearer than the tube / sphere, it fades in with the opening.
                float ring = saturate(dA + edge * 3.0 + 1.0);
                float headness = saturate((min(dT, dB) - dA) * 0.5);
                float fade = lerp(1.0, _BubbleHead, headness);
                float inside = saturate(0.5 - d) * ring * fade;                                   // 1px anti-aliased edge
                float outline = saturate(0.5 - abs(d + edge * 0.5) + edge * 0.5) * fade;          // a band just inside the edge
                if (inside <= 0.0 && outline <= 0.0) return 0;

                float2 q = (p - B) / max(rB, 1e-3);
                float r2 = dot(q, q);

                // The DNA, fitted to the sphere.
                float2 uv = q * 0.5 + 0.5;
                float3 dna = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb
                           * saturate((1.0 - sqrt(r2)) * rB * 0.25) * _BubbleDNA;

                float a = inside * _Fill.a;
                float3 c = _Fill.rgb * a + dna * inside;
                c = lerp(c, _Outline.rgb, outline * _Outline.a);
                a = max(a, outline * _Outline.a);
                return half4(c, a) * i.color.a;
            }
            ENDHLSL
        }
    }
}

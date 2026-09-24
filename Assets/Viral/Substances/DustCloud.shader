// Dust (DustClouds.cs): one soft, lumpy billboard per puff, built from SV_VertexID out of one buffer
// (no mesh). Each puff's motion is worked out here from its birth time, so the CPU never updates it:
// - burst (kind 0): out from its origin at its velocity, slowed by drag, drifting on, swelling;
// - stream (kind 1): along a quadratic curve (origin -> bend -> target), eased, shrinking as it arrives.
// Drawn in the Overlay queue, after the focus sweep, so extraction shows in focus mode; still depth
// tested, and faded against the scene's depth so puffs don't cut hard into what they touch.
Shader "Hidden/DustCloud"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "DustCloud"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Puff { float4 a, b, c, color, time; };
            StructuredBuffer<Puff> _DustPuffs;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q          : TEXCOORD0; // -1..1 across the puff
                float4 color      : TEXCOORD1;
                float2 data       : TEXCOORD2; // seed, age 0..1
                float4 screen     : TEXCOORD3;
                float  eyeDepth   : TEXCOORD4;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                Puff p = _DustPuffs[id / 6];
                float2 c = Corners[id % 6];
                float t = _Time.y - p.time.x, life = max(p.time.y, 1e-3);
                if (t < 0.0 || t >= life) { o.positionCS = float4(0, 0, -2, 1); return o; } // unborn / gone: nothing

                float k = t / life;
                float3 pos;
                float size;
                float alpha;
                if (p.b.w < 0.5)
                {
                    float drag = max(p.c.w, 1e-3);
                    pos = p.a.xyz + p.b.xyz * (1.0 - exp(-drag * t)) / drag + p.c.xyz * t;
                    size = p.a.w * (1.0 + p.time.w * sqrt(k));
                    alpha = saturate(t / 0.08) * (1.0 - k) * (1.0 - k);
                }
                else
                {
                    float u = k * k * (3.0 - 2.0 * k);
                    float3 ab = lerp(p.a.xyz, p.b.xyz, u), bc = lerp(p.b.xyz, p.c.xyz, u);
                    pos = lerp(ab, bc, u);
                    size = p.a.w * lerp(1.0, p.time.w, u);
                    alpha = saturate(t / 0.12) * saturate((1.0 - k) / 0.15);
                }

                // Turned by its seed so the lumps differ puff to puff.
                float ang = p.time.z * 6.2831853 + t * (p.time.z - 0.5);
                float2 r = float2(c.x * cos(ang) - c.y * sin(ang), c.x * sin(ang) + c.y * cos(ang));
                float3 view = TransformWorldToView(pos) + float3(r * size, 0);
                o.positionCS = TransformWViewToHClip(view);
                o.q = c;
                o.color = float4(p.color.rgb, p.color.a * alpha);
                o.data = float2(p.time.z, k);
                o.screen = ComputeScreenPos(o.positionCS);
                o.eyeDepth = -view.z;
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1), f.x), f.y);
            }

            // Linear eye depth of the scene, for perspective and orthographic views (focus mode is ortho).
            float SceneEye(float raw)
            {
                if (unity_OrthoParams.w > 0.5)
                {
                #if UNITY_REVERSED_Z
                    raw = 1.0 - raw;
                #endif
                    return lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
                }
                return LinearEyeDepth(raw, _ZBufferParams);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float r = length(i.q);
                if (r >= 1.0) discard;

                // A soft ball roughed up by drifting noise, thinning as it ages.
                float2 p = i.q * 2.0 + i.data.x * 17.0 + float2(0, _Time.y * 0.2);
                float n = Noise(p) * 0.6 + Noise(p * 2.1 + 5.3) * 0.4;
                float soft = saturate(1.0 - r);
                float a = soft * soft * saturate(n * 1.5 - 0.2 + (1.0 - i.data.y) * 0.35);

                float2 uv = i.screen.xy / i.screen.w;
                a *= saturate((SceneEye(SampleSceneDepth(uv)) - i.eyeDepth) / 0.8);

                float3 col = i.color.rgb * (0.75 + 0.45 * soft); // a brighter heart
                return half4(col, i.color.a * a);
            }
            ENDHLSL
        }
    }
}

// Fumes rising off signalling cells (ImmuneSystem.cs): one soft, lumpy billboard per puff, built
// from SV_VertexID out of two buffers (no mesh). Fades against the scene's depth so puffs don't cut
// hard into the cell they rise from.
Shader "Hidden/SignalFume"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "SignalFume"
            Tags { "LightMode" = "UniversalForward" }

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

            StructuredBuffer<float4> _PuffA; // xyz position, w size
            StructuredBuffer<float4> _PuffB; // x alpha, y seed, z age 0..1
            float4 _FumeColor, _FumeCore;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q          : TEXCOORD0; // -1..1 across the puff
                float4 data       : TEXCOORD1; // alpha, seed, age
                float4 screen     : TEXCOORD2;
                float  eyeDepth   : TEXCOORD3;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o;
                uint puff = id / 6;
                float4 a = _PuffA[puff];
                float4 b = _PuffB[puff];
                float2 c = Corners[id % 6];

                // Turned by its seed so the lumps differ puff to puff.
                float ang = b.y * 6.2831853;
                float2 r = float2(c.x * cos(ang) - c.y * sin(ang), c.x * sin(ang) + c.y * cos(ang));

                float3 view = TransformWorldToView(a.xyz) + float3(r * a.w, 0);
                o.positionCS = TransformWViewToHClip(view);
                o.q = c;
                o.data = b;
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

            half4 frag(Varyings i) : SV_Target
            {
                float r = length(i.q);
                if (r >= 1.0) discard;

                // A soft ball roughed up by drifting noise: lumpy smoke, thinning as it ages.
                float2 p = i.q * 2.2 + i.data.y * 17.0 + float2(0, _Time.y * 0.15);
                float n = Noise(p) * 0.6 + Noise(p * 2.1 + 5.3) * 0.4;
                float soft = saturate(1.0 - r);
                float a = soft * soft * saturate(n * 1.6 - 0.25 + (1.0 - i.data.z) * 0.3);

                float2 uv = i.screen.xy / i.screen.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                a *= saturate((sceneEye - i.eyeDepth) / 1.5);

                half4 col = lerp(_FumeColor, _FumeCore, saturate(soft * 1.4 - i.data.z));
                return half4(col.rgb, col.a * a * i.data.x);
            }
            ENDHLSL
        }
    }
}

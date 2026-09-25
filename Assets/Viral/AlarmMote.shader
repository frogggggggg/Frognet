// Warning motes spurting out of alarmed cells (AlarmMotes.cs, ImmuneSystem). No mesh: one glowing
// billboard per mote built from SV_VertexID, every mote moved from its birth data alone (so the CPU
// never touches a live one): spurts out along the cell's normal, slows (_MoteShape.x drag), floats on
// (y drift) with a lazy wander, stretched along its motion while fast, throbbing (z per second) and
// fading out. Additive, soft against the scene's depth.
Shader "Hidden/AlarmMote"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "AlarmMote"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Mote { float4 origin, launch, normal; }; // xyz + birth time | velocity + life | normal + seed
            StructuredBuffer<Mote> _Motes;
            float4 _MoteColor, _MoteCore;
            float4 _MoteShape; // drag, drift, pulses per second
            float4 _MoteSize;  // half-width min, max

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q          : TEXCOORD0; // -1..1 across the mote
                float  bright     : TEXCOORD1;
                float4 screen     : TEXCOORD2;
                float  eyeDepth   : TEXCOORD3;
            };

            static const float2 Corners[6] = { float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, -1), float2(1, 1), float2(-1, 1) };

            Varyings vert(uint id : SV_VertexID)
            {
                Varyings o = (Varyings)0;
                Mote m = _Motes[id / 6];
                float age = _Time.y - m.origin.w, life = m.launch.w;
                if (life <= 0.0 || age < 0.0 || age >= life)
                {
                    o.positionCS = float4(0, 0, -2, 1); // dead or unborn: off the clip volume
                    return o;
                }
                float seed = m.normal.w, k = max(_MoteShape.x, 1e-3);
                float3 n = m.normal.xyz;

                // Spurt (slowing exponentially), then float off along the normal, wandering lazily.
                float slow = exp(-k * age);
                float3 wander = float3(sin(age * 1.3 + seed * 40.0), sin(age * 1.1 + seed * 17.0), cos(age * 0.9 + seed * 29.0));
                float settle = smoothstep(0.0, 2.0, age);
                float3 p = m.origin.xyz + m.launch.xyz * (1.0 - slow) / k + n * (_MoteShape.y * age) + wander * (0.35 * settle);
                float3 vel = m.launch.xyz * slow + n * _MoteShape.y;

                // Stretched along its motion on screen while it's fast.
                float3 viewPos = TransformWorldToView(p);
                float2 vs = mul((float3x3)UNITY_MATRIX_V, vel).xy;
                float speed = length(vs);
                float2 along = speed > 1e-4 ? vs / speed : float2(1, 0);
                float2 across = float2(-along.y, along.x);
                float stretch = 1.0 + min(speed * 0.12, 2.5);
                float size = lerp(_MoteSize.x, _MoteSize.y, frac(seed * 7.31)) * lerp(1.0, 0.6, age / life);
                float2 c = Corners[id % 6];
                viewPos.xy += (along * c.x * stretch + across * c.y) * size;

                o.positionCS = TransformWViewToHClip(viewPos);
                o.q = c;
                // Throbbing warning, brightest as it spurts out; fades in fast, out over its last half.
                float throb = 0.5 + 0.5 * sin(age * _MoteShape.z * 6.2831853 + seed * 6.2831853);
                float fade = saturate(age / 0.06) * (1.0 - smoothstep(0.5, 1.0, age / life));
                o.bright = fade * (0.35 + 0.65 * throb * throb) * (1.0 + 1.5 * exp(-age * 3.0));
                o.screen = ComputeScreenPos(o.positionCS);
                o.eyeDepth = -viewPos.z;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float d2 = dot(i.q, i.q);
                if (d2 >= 1.0) discard;
                float core = exp(-d2 * 16.0);
                float halo = exp(-d2 * 3.5) * (1.0 - d2) * 0.45;

                float2 uv = i.screen.xy / i.screen.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                float soft = saturate((sceneEye - i.eyeDepth) / 0.3);

                float3 col = (_MoteCore.rgb * core + _MoteColor.rgb * halo) * (i.bright * soft);
                return half4(col, 0);
            }
            ENDHLSL
        }
    }
}

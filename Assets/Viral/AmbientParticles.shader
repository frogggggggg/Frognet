// Ambient specks suspended in the fluid around the camera (AmbientParticles.cs).
// Every speck lives in a box that wraps around the camera, so the field is endless
// and costs one draw call with no CPU work per speck. Each is a soft capsule smeared
// from where it is to where it was _StreakTime ago relative to the camera: nearly
// still specks hang as dots, and fast flight pulls them into motion streaks.
Shader "Hidden/AmbientParticles"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Name "AmbientParticles"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // All set per draw by AmbientParticles.cs.
            float  _Box, _NearFade, _SoftDistance;
            float  _SizeMin, _SizeMax, _SizePower;
            float  _Drift, _Wobble, _StreakTime, _MaxStreak, _Opacity, _PlasmaFraction;
            float4 _SpeckColor, _PlasmaColor;
            float3 _CamVel;
            float3 _FlowOffset, _FlowVel; // the blood carrying them (Vessel): integrated offset (wrapped), velocity

            struct Attributes
            {
                float3 seed   : POSITION;  // 0..1 home in the box
                float2 corner : TEXCOORD0; // quad corner, -1..1
                float2 rnd    : TEXCOORD1; // x size, y kind / wobble rate
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 q          : TEXCOORD0; // capsule space: x along (-ext..ext), y across (-1..1)
                float  ext        : TEXCOORD1;
                half4  color      : TEXCOORD2;
                float4 screen     : TEXCOORD3;
                float  eyeDepth   : TEXCOORD4;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float t = _Time.y;

                // Home + slow drift + a lazy wobble, then wrapped into the box around the camera.
                float3 driftDir = v.seed.zxy - 0.5;
                float3 wob = sin(t * (0.25 + v.rnd.y * 0.5) + v.seed * 6.2831853) * _Wobble;
                float3 home = v.seed * _Box + driftDir * (_Drift * t) + wob + _FlowOffset;

                float3 cam = _WorldSpaceCameraPos;
                float3 rel = frac((home - cam) / _Box + 0.5) - 0.5; // -0.5..0.5 of the box
                float3 pos = cam + rel * _Box;

                // Fade out toward the box faces (hides the wrap) and right at the lens.
                float edge = 1.0 - smoothstep(0.32, 0.5, max(abs(rel.x), max(abs(rel.y), abs(rel.z))));
                float nearFade = smoothstep(_NearFade, _NearFade * 2.0, length(pos - cam));

                float size01 = pow(v.rnd.x, _SizePower);
                float size = lerp(_SizeMin, _SizeMax, size01);

                // Smear toward where it was a moment ago, relative to the camera.
                float3 relVel = driftDir * _Drift + _FlowVel - _CamVel;
                float3 smear = -relVel * _StreakTime;
                float smearLen = length(smear);
                if (smearLen > _MaxStreak) smear *= _MaxStreak / smearLen;

                float3 c0 = TransformWorldToView(pos);
                float3 c1 = TransformWorldToView(pos + smear);
                float3 d = c1 - c0;
                float3 ray = normalize(c0 + d * 0.5);
                float3 across = d - ray * dot(d, ray); // the part of the smear visible on screen
                float acrossLen = length(across);

                float3 along, side;
                if (acrossLen > size * 0.05)
                {
                    along = across / acrossLen;
                    side = normalize(cross(along, ray));
                }
                else
                {
                    along = float3(1, 0, 0);
                    side = float3(0, 1, 0);
                    c1 = c0;
                    acrossLen = 0.0;
                }

                float s = v.corner.x * 0.5 + 0.5;
                float3 p = lerp(c0, c1, s) + along * (v.corner.x * size) + side * (v.corner.y * size);

                o.positionCS = TransformWViewToHClip(p);
                o.ext = (acrossLen * 0.5 + size) / size;
                o.q = float2(v.corner.x * o.ext, v.corner.y);
                o.screen = ComputeScreenPos(o.positionCS);
                o.eyeDepth = -p.z;

                // Streaks keep roughly the same total light as the dot they came from.
                float spread = max(size / (acrossLen * 0.5 + size), 0.2);
                half4 c = v.rnd.y < _PlasmaFraction ? _PlasmaColor : _SpeckColor;
                c.a *= _Opacity * edge * nearFade * spread * lerp(1.0, 0.35, size01); // big ones hazier
                o.color = c;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // Soft capsule.
                float dx = max(abs(i.q.x) - (i.ext - 1.0), 0.0);
                float r = length(float2(dx, i.q.y));
                float a = saturate(1.0 - r);
                a *= a;

                // Soft against the scene so specks don't clip hard into surfaces.
                float2 uv = i.screen.xy / i.screen.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                a *= saturate((sceneEye - i.eyeDepth) / _SoftDistance);

                return half4(i.color.rgb, i.color.a * a);
            }
            ENDHLSL
        }
    }
}

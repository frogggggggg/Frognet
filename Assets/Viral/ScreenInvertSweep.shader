// Unity ShaderLab - Universal Render Pipeline
//
// Screen-space scan expanding from a world point: everything inside the circle
// is replaced with a flat colour, with white outlines traced around whatever
// geometry is there, plus a bright ring riding the wavefront.
//
// Drawn as an overlay quad rather than a renderer feature, so it needs no
// changes to the URP renderer asset. The vertex stage ignores the transform
// entirely and emits clip space directly, so the quad always covers the screen
// however its GameObject is positioned or scaled.
Shader "Custom/ScreenInvertSweep"
{
    Properties
    {
        _Center ("World Centre", Vector) = (0,0,0,0)
        _Progress ("Progress", Range(0,1)) = 0

        [Header(Look)]
        _ScanColor ("Background", Color) = (0.016, 0.055, 0.180, 1)
        _EdgeColor ("Outline", Color) = (0.85, 0.95, 1.0, 1)
        _RingColor ("Wavefront", Color) = (0.70, 0.92, 1.0, 1)

        [Header(Outlines)]
        _EdgeThreshold ("Depth Threshold", Range(0.0005, 0.2)) = 0.012
        _EdgeThickness ("Outline Thickness", Range(0.5, 4)) = 1.2
        _NormalThreshold ("Curvature Threshold", Range(0.005, 1)) = 0.08
        _NormalStrength ("Curvature Strength", Range(0, 1)) = 1

        [Header(Transparent_Objects)]
        [Toggle] _UseTransparentDepth ("Include Transparent Geometry", Float) = 1
        _TransparentEdgeThreshold ("Transparent Depth Threshold", Range(0.0001, 0.1)) = 0.004

        [Header(Contours)]
        [Toggle] _UseContours ("Enable Topographic Contours", Float) = 0
        _ContourSpacing ("Contour Spacing (world units)", Range(0.05, 10)) = 0.75
        _ContourWidth ("Contour Width", Range(0.2, 4)) = 1
        _ContourStrength ("Contour Strength", Range(0, 1)) = 1

        [Header(Debug)]
        _DebugView ("Debug View (0 off 1 depth 2 normals 3 mask 4 solid 5 transparent depth)", Range(0,5)) = 0
        _DebugRange ("Debug Depth Range", Range(1, 500)) = 50

        [Header(Sweep)]
        _SweepSoftness ("Sweep Edge Softness", Range(0,0.5)) = 0.015
        _RingWidth ("Wavefront Width", Range(0,0.3)) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Overlay"
        }

        Pass
        {
            Name "ScanSweep"
            Tags { "LightMode"="UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Center;
                float  _Progress;
                half4  _ScanColor;
                half4  _EdgeColor;
                half4  _RingColor;
                float  _EdgeThreshold;
                float  _EdgeThickness;
                float  _NormalThreshold;
                float  _NormalStrength;
                float  _UseTransparentDepth;
                float  _TransparentEdgeThreshold;
                float  _UseContours;
                float  _ContourSpacing;
                float  _ContourWidth;
                float  _ContourStrength;
                float  _SweepSoftness;
                float  _RingWidth;
                float  _DebugView;
                float  _DebugRange;
            CBUFFER_END

            // Set globally by ScreenInvertTransparentDepthFeature.
            float _TransparentDepthAvailable;

            TEXTURE2D_X_FLOAT(_TransparentSceneDepthTexture);
            SAMPLER(sampler_TransparentSceneDepthTexture);

            struct Attributes { float4 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 ndc         : TEXCOORD0;
                float2 centreNDC   : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;

                // Quad spans -0.5..0.5 locally; doubling maps it onto the whole
                // -1..1 clip range, so the transform never enters into it.
                float2 ndc = input.positionOS.xy * 2.0;

                output.positionHCS = float4(ndc, 0.0, 1.0);
                output.ndc = ndc;

                float4 centreClip = TransformWorldToHClip(_Center.xyz);

                // Behind the camera w goes negative and the projection mirrors.
                // Flipping keeps the centre on the side it actually lies on.
                float2 projected = centreClip.xy / max(abs(centreClip.w), 1e-5);
                output.centreNDC = centreClip.w < 0.0 ? -projected : projected;

                return output;
            }

            float LinearDepthAt(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            float3 WorldPosAt(float2 uv)
            {
                return ComputeWorldSpacePosition(uv, SampleSceneDepth(uv), UNITY_MATRIX_I_VP);
            }

            // Surface normal rebuilt from the depth buffer.
            //
            // Sampling _CameraNormalsTexture would be cheaper, but URP only
            // renders it when a renderer feature asks for normals -- and the
            // whole point of doing this as an overlay quad is not to touch the
            // renderer asset. Reconstruction needs nothing but depth.
            float3 NormalAt(float2 uv, float2 texel)
            {
                float3 here = WorldPosAt(uv);
                float3 right = WorldPosAt(uv + float2(texel.x, 0.0));
                float3 up = WorldPosAt(uv + float2(0.0, texel.y));

                float3 normal = cross(up - here, right - here);

                float len = length(normal);
                return len > 1e-8 ? normal / len : float3(0.0, 0.0, 1.0);
            }

            // Roberts cross on scene depth. Two diagonal differences rather
            // than a full Sobel: half the taps, and the diagonals catch
            // silhouettes just as well at this thickness.
            //
            // Divided by the centre depth so a distant object gets the same
            // outline weight as a near one -- raw depth deltas grow with
            // distance and would only ever outline things close to the camera.
            float DepthOutline(float2 uv)
            {
                float2 texel = (_EdgeThickness / _ScreenParams.xy);

                float centre = LinearDepthAt(uv);

                float a = LinearDepthAt(uv + float2(-1.0, -1.0) * texel);
                float b = LinearDepthAt(uv + float2( 1.0,  1.0) * texel);
                float c = LinearDepthAt(uv + float2(-1.0,  1.0) * texel);
                float d = LinearDepthAt(uv + float2( 1.0, -1.0) * texel);

                float gradient = length(float2(b - a, d - c)) / max(centre, 1e-3);

                return smoothstep(_EdgeThreshold, _EdgeThreshold * 2.0, gradient);
            }

            float TransparentLinear01At(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(
                    _TransparentSceneDepthTexture,
                    sampler_TransparentSceneDepthTexture,
                    uv).r;
            }

            float TransparentDepthOutline(float2 uv)
            {
                if (_UseTransparentDepth < 0.5 ||
                    _TransparentDepthAvailable < 0.5)
                    return 0.0;

                float2 texel =
                    (_EdgeThickness / _ScreenParams.xy);

                float centre =
                    TransparentLinear01At(uv);

                float a =
                    TransparentLinear01At(
                        uv +
                        float2(-1.0, -1.0) *
                        texel);

                float b =
                    TransparentLinear01At(
                        uv +
                        float2(1.0, 1.0) *
                        texel);

                float c =
                    TransparentLinear01At(
                        uv +
                        float2(-1.0, 1.0) *
                        texel);

                float d =
                    TransparentLinear01At(
                        uv +
                        float2(1.0, -1.0) *
                        texel);

                // Texture is cleared to 1.0. If all taps are still at far
                // depth, there is no transparent geometry near this pixel.
                float nearest =
                    min(
                        centre,
                        min(
                            min(a, b),
                            min(c, d)));

                if (nearest >= 0.9999)
                    return 0.0;

                float gradient =
                    length(
                        float2(
                            b - a,
                            d - c));

                gradient /=
                    max(
                        1.0 - nearest,
                        0.05);

                return smoothstep(
                    _TransparentEdgeThreshold,
                    _TransparentEdgeThreshold * 2.0,
                    gradient);
            }

            // Depth alone only ever finds silhouettes and hard steps, so a
            // smooth sphere comes out as a blank shape with an outline round
            // it. Comparing reconstructed normals picks up curvature and
            // creases as well, which is what makes an organic surface read as
            // contoured rather than empty.
            float CurvatureOutline(float2 uv)
            {
                if (_NormalStrength <= 0.001) return 0.0;

                float2 texel = (_EdgeThickness / _ScreenParams.xy);

                float3 centre = NormalAt(uv, texel);

                float difference = 0.0;
                difference = max(difference, 1.0 - dot(centre, NormalAt(uv + float2( texel.x, 0.0), texel)));
                difference = max(difference, 1.0 - dot(centre, NormalAt(uv - float2( texel.x, 0.0), texel)));
                difference = max(difference, 1.0 - dot(centre, NormalAt(uv + float2(0.0,  texel.y), texel)));
                difference = max(difference, 1.0 - dot(centre, NormalAt(uv - float2(0.0,  texel.y), texel)));

                float edge = smoothstep(_NormalThreshold * 0.5, _NormalThreshold, difference);
                return edge * _NormalStrength;
            }

            // Topographic slices through the scene, one line every
            // _ContourSpacing metres of depth.
            //
            // This is what actually draws a smooth sphere. Both edge detectors
            // look for a change between neighbouring pixels, and a sphere has
            // none to find -- its depth and its normals both vary smoothly, so
            // the only thing they can catch is its silhouette. Slicing by
            // absolute depth does not care about continuity, so it rings the
            // surface instead of just tracing round it.
            float ContourLines(float2 uv)
            {
                // These are deliberate topographic depth slices, not object
                // outlines. Keep them disabled unless the material explicitly
                // asks for that scan-map look.
                if (_UseContours < 0.5 || _ContourStrength <= 0.001) return 0.0;

                float depth = LinearDepthAt(uv);

                // Skybox sits on the far plane, where the band index and its
                // derivative both explode into noise.
                if (depth >= _ProjectionParams.z * 0.99) return 0.0;

                float band = depth / max(_ContourSpacing, 1e-3);
                float t = frac(band);
                float toLine = min(t, 1.0 - t);

                // Width measured in band-units per pixel, so lines stay the
                // same thickness on screen whether the surface faces the
                // camera or falls away from it.
                float pixel = max(fwidth(band), 1e-5);

                // Not named "line": that is a reserved HLSL keyword, used for
                // geometry shader primitive types, and shadowing it is a
                // syntax error rather than a warning.
                float stripe = 1.0 - smoothstep(0.0, pixel * _ContourWidth, toLine);
                return stripe * _ContourStrength;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // NDC x spans the width and y the height regardless of shape,
                // so x is scaled by aspect or the sweep comes out elliptical.
                float aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 scale = float2(aspect, 1.0);

                float2 here = input.ndc * scale;
                float2 centre = input.centreNDC * scale;

                // Radius that reaches the furthest corner, so progress 1 always
                // covers the screen no matter where the centre sits.
                float maxRadius = 0.0;
                maxRadius = max(maxRadius, distance(centre, float2(-aspect, -1.0)));
                maxRadius = max(maxRadius, distance(centre, float2( aspect, -1.0)));
                maxRadius = max(maxRadius, distance(centre, float2(-aspect,  1.0)));
                maxRadius = max(maxRadius, distance(centre, float2( aspect,  1.0)));

                float radius = maxRadius * saturate(_Progress);
                float dist = distance(here, centre);

                float soft = max(_SweepSoftness, 1e-4);
                float inside = 1.0 - smoothstep(radius - soft, radius + soft, dist);

                // Debug views ignore the sweep entirely and paint the whole
                // screen, so what the effect is reading can be inspected
                // without having to trigger it first.
                if (_DebugView > 0.5)
                {
                    float2 debugUV =
                        GetNormalizedScreenSpaceUV(
                            input.positionHCS);

                    if (_DebugView > 4.5)
                    {
                        if (_TransparentDepthAvailable < 0.5)
                            return half4(1.0, 0.0, 1.0, 1.0);

                        float td =
                            TransparentLinear01At(
                                debugUV);

                        return half4(
                            td,
                            td,
                            td,
                            1.0);
                    }

                    // Solid fill first, and dependent on nothing at all.
                    if (_DebugView > 3.5)
                        return half4(1.0, 0.0, 0.0, 1.0);

                    if (_DebugView < 1.5)
                    {
                        // Scene depth as greyscale. If an object is missing
                        // here it is missing from _CameraDepthTexture, and no
                        // amount of edge tuning will ever find it.
                        float linearDepth = LinearDepthAt(debugUV);
                        float grey = saturate(linearDepth / max(_DebugRange, 1e-3));
                        return half4(grey, grey, grey, 1.0);
                    }

                    if (_DebugView < 2.5)
                    {
                        float2 debugTexel = (_EdgeThickness / _ScreenParams.xy);
                        float3 debugNormal = NormalAt(debugUV, debugTexel);
                        return half4(debugNormal * 0.5 + 0.5, 1.0);
                    }

                    float mask =
                        max(
                            max(
                                max(
                                    DepthOutline(debugUV),
                                    CurvatureOutline(debugUV)),
                                TransparentDepthOutline(debugUV)),
                            ContourLines(debugUV));
                    return half4(mask, mask, mask, 1.0);
                }

                // Nothing to composite outside the circle, and bailing early
                // skips the depth taps for most of the screen while the sweep
                // is still small.
                if (inside <= 0.001) return half4(0, 0, 0, 0);

                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionHCS);
                float outline =
                    DepthOutline(screenUV);

                outline =
                    max(
                        outline,
                        CurvatureOutline(screenUV));

                outline =
                    max(
                        outline,
                        TransparentDepthOutline(screenUV));

                outline =
                    max(
                        outline,
                        ContourLines(screenUV));

                half3 colour = lerp(_ScanColor.rgb, _EdgeColor.rgb, outline);

                // Bright band riding just behind the wavefront. Suppressed at
                // rest so a progress of 0 or 1 shows no stray ring.
                float ring = 0.0;
                if (_RingWidth > 1e-4 && _Progress > 0.001 && _Progress < 0.999)
                    ring = 1.0 - smoothstep(0.0, _RingWidth, abs(dist - radius));

                colour = lerp(colour, _RingColor.rgb, ring);

                float alpha = max(inside * _ScanColor.a, ring);
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

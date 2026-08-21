Shader "Hidden/PlayerDitherStencil"
{
    Properties
    {
        _Radius ("Fade End Radius (world units)", Float) = 2
        _FadeWidth ("Fade Band Width", Float) = 1
        _CellSize ("Circle Cell Size (world units)", Float) = 0.25
        _GateBias ("Occluder Min Lead (world units)", Float) = 0.35
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            // Drawn by the PlayerStencilShell renderer feature at
            // AfterRenderingOpaques, after the occluder depth
            // prepass. Opaque-range queue so the feature's opaque
            // filter picks it up.
            "Queue"="AlphaTest+40"
        }

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
        };

        struct Varyings
        {
            float4 positionHCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
        };

        float _Radius;
        float _FadeWidth;
        float _CellSize;
        float _GateBias;

        Varyings vert(Attributes input)
        {
            Varyings output;

            // Inflate the mesh into a shell covering the whole
            // fade region on screen. The shell only defines the
            // covered pixels; the occlusion gate compares against
            // the depth the fragment writes (pattern plane).
            float3 pivot = unity_ObjectToWorld._m03_m13_m23;
            float3 ws =
                TransformObjectToWorld(input.positionOS.xyz);
            ws = pivot + normalize(ws - pivot) * (_Radius * 1.05);

            output.positionWS = ws;
            output.positionHCS = TransformWorldToHClip(ws);

            return output;
        }

        // Runs the dither pattern clips; returns the pattern-plane
        // hit point for depth output.
        float3 DitherClip(Varyings input)
        {
            float3 positionWS = input.positionWS;

            // Object pivot in world space = center of the fade.
            float3 centerWS = unity_ObjectToWorld._m03_m13_m23;

            // A camera-facing plane through the pivot selects a
            // thin slab out of a fixed 3D world lattice. Anchors
            // never move or rotate; as the view changes they just
            // fade in and out of the slab, so there is no snapping
            // either.
            float3 planeN = normalize(
                _WorldSpaceCameraPos - centerWS);

            float3 rayd = positionWS - _WorldSpaceCameraPos;
            float t = dot(centerWS - _WorldSpaceCameraPos, planeN)
                / dot(rayd, planeN);
            float3 hit = _WorldSpaceCameraPos + rayd * t;

            // Discard rays whose pattern plane is behind the camera.
            clip(t - 0.001);

            float cellSize = max(_CellSize, 0.0001);
            float3 baseCell = floor(hit / cellSize) - 1.0;

            // Signed separation from the nearest hole edge.
            float minSep = 1e5;

            [unroll]
            for (int zi = 0; zi < 3; zi++)
            [unroll]
            for (int yi = 0; yi < 3; yi++)
            [unroll]
            for (int xi = 0; xi < 3; xi++)
            {
                float3 anchor = (baseCell
                    + float3(xi, yi, zi) + 0.5) * cellSize;

                // Whole-circle size from the anchor's distance to
                // the object center: tiny holes in the core, merged
                // at _Radius. Uniform per circle. The 0.866 factor
                // guarantees full overlap at the end even at
                // oblique view angles.
                float coverage = saturate(
                    (_Radius - distance(anchor, centerWS))
                        / max(_FadeWidth, 0.0001));

                float holeR = (1.0 - coverage)
                    * cellSize * 0.8660254;

                // Slab membership with a thin transition band:
                // circles hold full size through the slab and only
                // grow/shrink briefly while being swapped, instead
                // of teleporting.
                float blend = cellSize * 0.15;
                float w = 1.0 - smoothstep(
                    cellSize * 0.5 - blend,
                    cellSize * 0.5 + blend,
                    abs(dot(anchor - hit, planeN)));

                // Distance perpendicular to the view ray keeps
                // each hole round on screen.
                float3 viewN = normalize(
                    _WorldSpaceCameraPos - anchor);
                float3 off = hit - anchor;
                float d = length(
                    off - viewN * dot(off, viewN));

                minSep = min(minSep, d - holeR * w);
            }

            // Stencil everywhere except inside the holes.
            clip(minSep);

            return hit;
        }

        ENDHLSL

        // The fragment writes the pattern plane's depth, so ZTest
        // Greater means "occluder in front of the player" - the
        // shell geometry only defines the covered screen region.
        Pass
        {
            ZWrite Off
            ZTest Greater
            Cull Front
            ColorMask 0

            Stencil
            {
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            half4 frag(Varyings input,
                out float outDepth : SV_Depth) : SV_Target
            {
                float3 hit = DitherClip(input);

                // Pull the reference toward the camera so a wall
                // must lead the player by _GateBias to count as
                // occluding - touching a wall no longer triggers.
                float3 camDir = normalize(
                    input.positionWS - _WorldSpaceCameraPos);
                float4 clipPos = TransformWorldToHClip(
                    hit - camDir * _GateBias);
                outDepth = clipPos.z / clipPos.w;

                return 0;
            }

            ENDHLSL
        }
    }
}
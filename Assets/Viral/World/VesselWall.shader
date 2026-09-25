// The vessel's wall: a grid mesh (uv = along the loop, round the tube) placed entirely in the vertex stage on the
// loop Vessel.cs describes (globals _Vessel*). It covers the whole loop, its rings packed toward the camera
// (phi offset ~ u^2) and drawn nearest first (Vessel orders the triangles), so every view down the tube ends on
// wall and the hidden far loop costs only depth tests.
//
// Real relief, in the vertex stage (every pass, so depth matches): endothelial cells as pillows with a raised
// nucleus, sunk junctions between, on long wavy folds running with the flow. The same height function gives the
// pixel normal analytically (Voronoi distances and their gradients), so it's exact and steady at any distance.
// Stylized, cel shaded like the cells: banded light (main light + facing), dark ink lines in the junctions (a
// constant on-screen width), a bright rim at grazing angles, nuclei a darker shade. No shadows. Detail fades where a
// cell gets a few pixels small; hazed toward the fog colour by its own thin _WallHaze (the scene fog would hide it
// from the middle of the tube). The pattern lives in the wall's frame (S), so it slides by with the flow.
//
// The pattern is laid out conformally (no stretching anywhere, cells only change size): round the tube by the torus's
// isothermal angle (cells shrink toward the loop's inner side just as much along it as round it), along it by the
// warp in _VesselRadiusTex.b (follows narrows' slopes, cells shrink with the tube). The grid's vertices are spaced by
// the same angle, so every wall cell gets about as many.
Shader "Custom/VesselWall"
{
    Properties
    {
        [Header(Colour)]
        _WallColor ("Wall", Color) = (0.45, 0.25, 0.3, 1)
        _NucleusColor ("Nucleus", Color) = (0.34, 0.18, 0.27, 1)
        _WallGlow ("Rim / light through", Color) = (0.72, 0.4, 0.42, 1)
        _Saturation ("Saturation", Range(0, 1.5)) = 0.75
        _BandContrast ("Cel band contrast", Range(0, 1)) = 0.6
        [Header(Ink)]
        _InkColor ("Ink", Color) = (0.2, 0.1, 0.17, 1)
        _InkStrength ("Ink near", Range(0, 1)) = 0.5
        _InkFar ("Ink far", Range(0, 1)) = 0.12
        _InkFadeStart ("Ink fade start (m)", Float) = 250
        _InkFadeEnd ("Ink fade end (m)", Float) = 1200
        [Header(Highlights)]
        _GlintStrength ("Wet glint", Range(0, 1)) = 0.3
        _RimStrength ("Rim", Range(0, 1)) = 0.35
        [Header(Haze toward the fog colour)]
        _WallHaze ("Haze per metre", Float) = 0.0018
        _WallHazeMax ("Max haze", Range(0, 1)) = 0.8
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    float4 _VesselCentre; // xyz loop centre, w loop radius
    float4 _VesselE1;     // xyz, w circumference
    float4 _VesselE2;     // xyz, w wall offset S0 (S = s + S0)
    float4 _VesselAxis;   // xyz, w nominal tube radius
    TEXTURE2D(_VesselRadiusTex); SAMPLER(sampler_VesselRadiusTex);

    float _WallPhi, _WallHalf, _WallRelief, _WallFolds; // set by Vessel
    // Look: from the material (Vessel's wallMaterial, an editable asset).
    float _WallHaze, _WallHazeMax, _Saturation, _BandContrast, _InkStrength, _InkFar, _InkFadeStart, _InkFadeEnd;
    float _GlintStrength, _RimStrength;
    float4 _InkColor;
    float4 _WallTiles;    // x tiles along the loop, y round the tube, z (metres per tile along) / (round)
    float4 _WallColor, _NucleusColor, _WallGlow;

    #define TAU 6.28318530718

    struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };

    float2 WallHash2(float2 cell)
    {
        cell = fmod(fmod(cell, _WallTiles.xy) + _WallTiles.xy, _WallTiles.xy); // periodic round both ways
        float2 p = float2(dot(cell, float2(127.1, 311.7)), dot(cell, float2(269.5, 183.3)));
        return frac(sin(p) * 43758.5453);
    }

    float WallHash1(float2 cell)
    {
        cell = fmod(fmod(cell, _WallTiles.xy) + _WallTiles.xy, _WallTiles.xy);
        return frac(sin(dot(cell, float2(41.3, 289.1))) * 12543.1);
    }

    struct WallSample
    {
        float height;  // metres, inward
        float2 grad;   // d height / d tile (metres per tile unit)
        float pillow;  // 0 in the junctions .. 1 on a cell
        float nucleus; // 0..1
        float edge;    // distance to the junction (tile units)
        float id;      // per wall cell, 0..1
    };

    // The wall's shape at a tile coordinate. detail scales the cell relief (fades far away), folds stay.
    WallSample Wall(float2 t, float detail)
    {
        WallSample w;
        float2 i = floor(t), f = t - i;
        float d1 = 64.0, d2 = 64.0;
        float2 c1 = 8.0, c2 = 8.0;
        w.id = 0.0;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int k = -1; k <= 1; k++)
        {
            float2 g = float2(k, y);
            float2 c = g + 0.2 + 0.6 * WallHash2(i + g) - f;
            float d = dot(c, c);
            if (d < d1) { d2 = d1; c2 = c1; d1 = d; c1 = c; w.id = WallHash1(i + g); }
            else if (d < d2) { d2 = d; c2 = c; }
        }
        d1 = sqrt(d1); d2 = sqrt(d2);
        // Gradients (tile units): c = centre - x, so d|c|/dx = -c/|c|.
        float2 g1 = -c1 / max(d1, 1e-4), g2 = -c2 / max(d2, 1e-4);

        // Pillow: rises from the junctions (d2 ~ d1) and levels off; a little dome toward the middle.
        w.edge = d2 - d1;
        float p = saturate(w.edge / 0.4);
        w.pillow = p * p * (3.0 - 2.0 * p);
        float2 pillowG = (w.edge < 0.4 ? 6.0 * p * (1.0 - p) / 0.4 : 0.0) * (g2 - g1);
        float dome = 1.0 - d1 * d1;
        float2 domeG = -2.0 * d1 * g1;
        // Nucleus: a raised oval off the cell's middle.
        float nr = 0.2 + 0.08 * w.id;
        w.nucleus = exp(-d1 * d1 / (nr * nr));
        float2 nucleusG = w.nucleus * (-2.0 * d1 / (nr * nr)) * g1;

        float h = w.pillow * 0.55 + dome * 0.2 + w.nucleus * 0.45;
        float2 gh = pillowG * 0.55 + domeG * 0.2 + nucleusG * 0.45;

        // Folds: long ridges running with the flow, wandering side to side (whole periods round the loop).
        float ky = round(_WallTiles.y / 3.0) / _WallTiles.y, kx = round(_WallTiles.x / 6.0) / _WallTiles.x;
        float wander = 0.35 * sin(TAU * kx * t.x);
        float ph = TAU * (ky * t.y + wander);
        float fold = 0.5 + 0.5 * sin(ph);
        float2 foldG = 0.5 * cos(ph) * float2(TAU * 0.35 * cos(TAU * kx * t.x) * TAU * kx, TAU * ky);

        w.height = _WallRelief * h * detail + _WallFolds * fold;
        w.grad = _WallRelief * gh * detail + _WallFolds * foldG;
        w.pillow *= detail;
        w.nucleus *= detail;
        return w;
    }

    // uv -> the undisplaced wall point, inward normal, unit tangents (along the loop, round the tube), the
    // wall-frame tile coordinates and the metres per tile (along, round) there.
    void PlaceWall(float2 uv, out float3 positionWS, out float3 normalWS, out float3 alongWS, out float3 aroundWS,
                   out float2 tile, out float2 metresPerTile)
    {
        float u = uv.x * 2.0 - 1.0;
        float phi = _WallPhi + sign(u) * u * u * _WallHalf; // rings packed toward the camera
        float R = _VesselCentre.w, L = _VesselE1.w;
        float S = phi * R + _VesselE2.w;
        float4 tex = SAMPLE_TEXTURE2D_LOD(_VesselRadiusTex, sampler_VesselRadiusTex, float2(S / L, 0.5), 0);
        float a = tex.r, slope = tex.g;
        // uv.y is the isothermal angle round the tube (0 = the loop's outer side, 0.5 its inner):
        // tan(pi v) = q tan(theta / 2). sin(pi v) >= 0 on 0..1, so theta runs 0..2pi (at v = 1 it may come out as -2pi,
        // the same point).
        float q = sqrt((R - a) / (R + a));
        float theta = 2.0 * atan2(sin(PI * uv.y), q * cos(PI * uv.y));
        float3 radial = _VesselE1.xyz * cos(phi) + _VesselE2.xyz * sin(phi);
        float3 outward = radial * cos(theta) + _VesselAxis.xyz * sin(theta);
        float rho = R + a * cos(theta); // distance from the loop's axis
        positionWS = _VesselCentre.xyz + radial * R + outward * a;
        float3 tangent = cross(_VesselAxis.xyz, radial);
        // The wall leans with the tube's width: in narrows it slopes in and out along the loop.
        normalWS = normalize(-outward + tangent * (slope * R / rho));
        alongWS = normalize(tangent * (rho / R) + outward * slope);
        aroundWS = -radial * sin(theta) + _VesselAxis.xyz * cos(theta);
        tile = float2((S / L + tex.b) * _WallTiles.x, uv.y * _WallTiles.y);
        float perRound = TAU * rho * a / (_WallTiles.y * sqrt(max(R * R - a * a, 1.0)));
        metresPerTile = float2(perRound * _WallTiles.z, perRound);
    }

    // Cell relief fades out with distance (vertices get sparse far down the loop); folds stay.
    float VertexDetail(float3 p) { return 1.0 - smoothstep(2500.0, 5000.0, distance(p, _WorldSpaceCameraPos)); }

    // The displaced wall point (every pass uses this, so depth matches colour).
    float3 WallPosition(float2 uv, out float3 normalWS, out float3 alongWS, out float3 aroundWS, out float2 tile,
                        out float2 metresPerTile)
    {
        float3 p;
        PlaceWall(uv, p, normalWS, alongWS, aroundWS, tile, metresPerTile);
        WallSample w = Wall(tile, VertexDetail(p));
        return p + normalWS * w.height;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1; // undisplaced wall normal (inward)
                float3 alongWS    : TEXCOORD2;
                float3 aroundWS   : TEXCOORD3;
                float3 tile       : TEXCOORD4; // xy tiles, z haze (1 = clear)
                float2 metres     : TEXCOORD5; // metres per tile: along, round
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionWS = WallPosition(v.uv, o.normalWS, o.alongWS, o.aroundWS, o.tile.xy, o.metres);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                // Its own thin haze (not the scene fog, which hides everything past ~400 m): the wall across the
                // tube stays a landmark, and far down the tube it melts into the fog colour.
                // Capped (_WallHazeMax) so the far wall still shows as a dim silhouette.
                o.tile.z = max(exp(-distance(o.positionWS, _WorldSpaceCameraPos) * _WallHaze), 1.0 - _WallHazeMax);
                return o;
            }

            // A soft step whose edge stays ~1 px wide on screen.
            float Step(float edge, float x)
            {
                float w = max(fwidth(x), 1e-4);
                return smoothstep(edge - w, edge + w, x);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 t = i.tile.xy;
                // How big a cell is on screen: detail fades out before it can alias.
                float px = max(length(fwidth(t)), 1e-5);
                float detail = saturate(1.5 - px * 6.0);  // gone when a cell is < ~5 px
                WallSample w = Wall(t, detail * VertexDetail(i.positionWS));

                float3 gradWS = normalize(i.alongWS) * (w.grad.x / i.metres.x)
                              + normalize(i.aroundWS) * (w.grad.y / i.metres.y);
                float3 N0 = normalize(i.normalWS);
                float3 N = normalize(N0 - gradWS);

                float3 V = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light light = GetMainLight();
                // Cel bands from the main light and how much a surface faces you (so the inside of a tube reads
                // everywhere, not just on the lit side).
                float lit = 0.55 * saturate(dot(N, light.direction) * 0.5 + 0.5) + 0.45 * saturate(dot(N, V));
                float band = lerp(0.5, 0.78, Step(0.42, lit));
                band = lerp(band, 1.0, Step(0.68, lit));
                band = lerp(0.78, band, _BandContrast);

                half3 albedo = _WallColor.rgb * lerp(0.88, 1.12, w.id);
                albedo *= lerp(0.8, 1.0, w.pillow + (1.0 - detail));                  // cells sink into their seams
                float nucleusIn = Step(0.45, w.nucleus);
                albedo = lerp(albedo, _NucleusColor.rgb, nucleusIn);                  // nucleus: its own flat shade
                half3 col = albedo * band * (0.75 + 0.35 * light.color);

                // Ink in the junctions and round each nucleus: lines of constant on-screen width, only where cells
                // are big enough.
                float lineW = max(fwidth(w.edge) * 1.2, 0.02);
                float ink = (1.0 - smoothstep(lineW * 0.5, lineW * 1.5, w.edge)) * detail;
                float nw = max(fwidth(w.nucleus), 1e-4);
                float ring = (1.0 - smoothstep(nw * 0.6, nw * 1.6, abs(w.nucleus - 0.45))) * detail;
                // Softer with distance, so the far wall doesn't read as a busy pattern right behind everything.
                float inkAmount = lerp(_InkStrength, _InkFar,
                    smoothstep(_InkFadeStart, _InkFadeEnd, distance(i.positionWS, _WorldSpaceCameraPos)));
                col = lerp(col, _InkColor.rgb, max(ink, ring * 0.7) * inkAmount);

                // Wet: a hard glint on each pillow toward the light.
                float spec = Step(0.965, dot(N, normalize(light.direction + V))) * w.pillow * (1.0 - nucleusIn);
                col = lerp(col, _WallGlow.rgb * 1.3, spec * _GlintStrength);

                // Rim at grazing angles: light through the thin tissue.
                float rim = Step(0.72, 1.0 - saturate(dot(N, V)));
                col = lerp(col, _WallGlow.rgb, rim * _RimStrength);
                col = lerp(dot(col, half3(0.299, 0.587, 0.114)), col, _Saturation);

                return half4(lerp(unity_FogColor.rgb, col, saturate(i.tile.z)), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(Attributes v) : SV_POSITION
            {
                float3 n, al, ar; float2 t, m;
                return TransformWorldToHClip(WallPosition(v.uv, n, al, ar, t, m));
            }

            half frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}

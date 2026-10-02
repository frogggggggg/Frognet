// How a lump of a substance looks on a camera-facing quad (SubstanceLook in SubstanceStore.cs), shared by
// everything that shows loose substance: a cell's motes (Cells/CellInteriorMotes), a resource blob's core
// (ResourceCoreMotes) and the stream into the virus (DustCloud). One place, so a substance looks the same everywhere.
// Returns premultiplied colour. Draw with Blend One OneMinusSrcAlpha. q: -1..1 across the quad; seed 0..1 per lump;
// t seconds. Honey (energy, ATP) is a liquid: HoneyShade shades a metaball field; a lone drop here is its own field,
// while a cell's motes sum the field over their neighbours so drops run together (Cells/CellInteriorMotes).
#ifndef SUBSTANCE_LOOK_INCLUDED
#define SUBSTANCE_LOOK_INCLUDED

#define LOOK_CHUNK   0
#define LOOK_HONEY   1
#define LOOK_BUBBLE  2
#define LOOK_CRYSTAL 3
#define LOOK_COIL    4

float2 LookTurn(float2 q, float a)
{
    float c = cos(a), s = sin(a);
    return float2(q.x * c - q.y * s, q.x * s + q.y * c);
}

// Honey's kernel: (1 - r^2/R^2)^3 per drop, zero past its reach R; the surface is where the sum passes this.
#define HONEY_LEVEL 0.5

// Liquid amber glass from a metaball field: F the summed field, G its gradient (d F / d pixel, in reach units: sum of
// 6 w^2 (centre - pixel) / R), aa the field's change across a pixel. See-through (clear where thick, dense and dark
// where the view grazes the edge), no glow: the only bright things are crisp white highlights, a thin lit edge and a
// soft caustic on the far side. The surface ripples along its contours and sloshes, so highlights slide like a
// liquid's. Everything is a function of F / G / t (not the seed), so joined drops stay seamless.
half4 HoneyShade(float F, float2 G, float aa, float3 col, float alpha, float t, float seed)
{
    float cover = smoothstep(HONEY_LEVEL - aa, HONEY_LEVEL + aa, F);
    float depth = F - HONEY_LEVEL;
    float thick = saturate(depth / 0.9);
    float rim = 1.0 - smoothstep(0.0, aa * 2.0 + 0.1, depth);                          // the grazing edge

    float2 slope = -G * 0.35;
    slope *= 1.0 + 0.14 * sin(F * 14.0 - t * 2.2) * smoothstep(0.02, 0.2, depth);       // ripples along the contours
    slope += 0.1 * float2(sin(t * 0.9), cos(t * 0.7));                                  // a slow slosh
    float3 n = normalize(float3(slope, 1.0));
    float3 L = normalize(float3(-0.42, 0.5, 1.0));
    float lean = length(n.xy);
    float2 tilt = n.xy / max(lean, 1e-4), toLight = normalize(L.xy);

    // Glass body: thin amber over what's behind, denser and darker toward the edge (Fresnel).
    float fres = saturate(rim + pow(saturate(1.0 - n.z), 1.2) * 1.4);
    float3 c = lerp(col * float3(0.62, 0.4, 0.1), col * float3(0.28, 0.15, 0.03), fres);
    float a = lerp(0.32, 0.88, fres);
    c *= 1.0 + 0.12 * sin(F * 9.0 + t * 0.8) * thick;                                   // faint flow bands inside

    // Caustic: light focused through the drop pools softly on the side away from the light.
    float caustic = smoothstep(0.1, 0.6, dot(tilt, -toLight)) * smoothstep(0.08, 0.4, lean) * (1.0 - rim);
    c += col * float3(0.5, 0.38, 0.12) * caustic;
    a = saturate(a + caustic * 0.12);

    // Highlights: a crisp key, a small second, and a thin bright line on the lit edge.
    float spec = smoothstep(0.966, 0.978, dot(n, L));
    float spec2 = smoothstep(0.988, 0.994, dot(n, normalize(float3(0.55, -0.45, 1.0)))) * 0.6;
    float edgeLit = rim * smoothstep(0.5, 0.9, dot(tilt, toLight)) * 0.75;
    float white = saturate(spec + spec2 + edgeLit);

    float3 rgb = lerp(c * a, float3(1.0, 0.98, 0.93), white);
    a = lerp(a, 1.0, white);
    float k = cover * alpha;
    return half4(rgb * k, a * k);
}

half4 SubstanceMote(float look, float2 q, float seed, float3 col, float alpha, float t)
{
    float r = length(q);
    float px = length(fwidth(q)); // a pixel in quad units (before any branch)
    if (look < 0.5)
    {
        // Chunk: a lumpy blob, slowly turning: flat colour, a darker rim, a glint.
        float angle = atan2(q.y, q.x + 1e-5);
        float edge = 0.62 * (1.0 + 0.16 * sin(3.0 * angle + seed * 20.0 + t * 0.5) + 0.08 * sin(5.0 * angle + seed * 40.0));
        float inside = 1.0 - smoothstep(edge - 0.06, edge, r);
        float rim = smoothstep(edge - 0.24, edge - 0.1, r);
        float glint = 1.0 - smoothstep(0.08, 0.14, distance(q, float2(-0.2, 0.22)));
        float a = inside * alpha * 0.92;
        return half4((col * (1.0 - 0.4 * rim) + glint * 0.35) * a, a);
    }
    if (look < 1.5)
    {
        // Honey (energy): a lone drop, gently quivering; the field is its own (reach = the quad).
        float2 p = q * (1.0 + 0.06 * float2(sin(t * 2.1 + seed * 30.0), cos(t * 1.7 + seed * 19.0)));
        float x = dot(p, p);
        x = min(x, 1.0); // past its reach: nothing (F = 0)
        float w = 1.0 - x;
        float F = w * w * w * 1.35;
        float2 G = -6.0 * w * w * p * 1.35;
        return HoneyShade(F, G, max(length(G) * px, 1e-3), col, alpha, t, seed);
    }
    if (look < 2.5)
    {
        // Bubble (gas): two joined bubbles, rolling slowly: thin shells, a faint fill, a glint on each.
        float2 p = LookTurn(q, seed * 6.2832 + t * 0.4);
        float d1 = length(p - float2(-0.27, 0.0)), d2 = length(p - float2(0.27, 0.0));
        float d = min(d1, d2);
        float shell = 1.0 - smoothstep(0.05, 0.1, abs(d - 0.36));
        float fill = (1.0 - smoothstep(0.32, 0.36, d)) * 0.16;
        float2 g = float2(-0.12, 0.14);
        float glint = 1.0 - smoothstep(0.04, 0.08, min(length(p - float2(-0.27, 0.0) - g), length(p - float2(0.27, 0.0) - g)));
        float a = saturate(shell * 0.85 + fill) * alpha;
        return half4(col * a + glint * 0.4 * alpha, a);
    }
    if (look < 3.5)
    {
        // Crystal (sugar): a hexagonal ring, turning: pale facets, a bright edge, a dark hole in the middle.
        float2 turned = LookTurn(q, seed * 6.2832 + t * 0.3), p = abs(turned);
        float hex = max(p.x * 0.866 + p.y * 0.5, p.y);
        float outer = 1.0 - smoothstep(0.56, 0.62, hex);
        float hole = smoothstep(0.2, 0.26, hex);
        float edge = smoothstep(0.44, 0.54, hex);
        float facet = 0.8 + 0.2 * sign(turned.x * turned.y);
        float a = outer * lerp(0.35, 1.0, hole) * alpha;
        float3 c = col * facet * (0.85 + 0.35 * edge) * lerp(0.6, 1.0, hole);
        return half4(c * a, a);
    }
    // Coil (protein): a folded chain of five beads, flexing: solid, each bead shaded, dark seams between.
    float2 p = LookTurn(q, seed * 6.2832);
    float best = 9.0, second = 9.0;
    [unroll] for (int k = 0; k < 5; k++)
    {
        float fx = -0.52 + 0.26 * k;
        float2 c = float2(fx, 0.24 * sin(k * 1.9 + seed * 11.0 + t * 0.9));
        float d = length(p - c) - 0.2;
        second = min(second, max(d, best));
        best = min(best, d);
    }
    float inside = 1.0 - smoothstep(-0.02, 0.02, best);
    float seam = 1.0 - smoothstep(0.0, 0.05, abs(second - best));
    float shade = smoothstep(-0.2, 0.0, best); // darker toward each bead's edge
    float a = inside * alpha * 0.95;
    float3 c = col * (1.05 - 0.45 * shade) * (1.0 - 0.45 * seam);
    return half4(c * a, a);
}

#endif

#ifndef SURFACE_MAP_INCLUDED
#define SURFACE_MAP_INCLUDED

// SurfaceMap on the GPU: the nearest point of a mesh from anywhere around it, in its own space. Every
// registered map is concatenated into these four buffers (SurfaceMap.Pack, uploaded by LegRenderer); a map is
// an index into _MapInfos. Same query as SurfaceMap.Nearest in C#: keep the two alike.

struct MapInfo
{
    float4 origin;  // xyz grid corner (mesh space), w cell size
    int4   dims;    // xyz cells per axis, w first cell in _MapCells
    int4   offsets; // x first triangle in _MapTris, y first index in _MapIndices
};

struct MapTri
{
    float3 a, b, c; // corners
    float3 n;       // face normal (winding side)
    float3 na, nb, nc; // the mesh's normals at the corners
};

StructuredBuffer<MapInfo> _MapInfos;
StructuredBuffer<MapTri>  _MapTris;
StructuredBuffer<int2>    _MapCells;   // per cell: start and count in _MapIndices (relative to the map's offsets.y)
StructuredBuffer<int>     _MapIndices; // triangle indices (relative to the map's offsets.x)

// Closest point on triangle abc to p, with its barycentric weights (Ericson 5.1.5). Branchy, like the C# twin.
float3 MapClosest(float3 p, float3 a, float3 b, float3 c, out float3 bary)
{
    float3 ab = b - a, ac = c - a, ap = p - a;
    float d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (d1 <= 0.0 && d2 <= 0.0) { bary = float3(1, 0, 0); return a; }

    float3 bp = p - b;
    float d3 = dot(ab, bp), d4 = dot(ac, bp);
    if (d3 >= 0.0 && d4 <= d3) { bary = float3(0, 1, 0); return b; }

    float vc = d1 * d4 - d3 * d2;
    if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
    {
        float t = d1 / (d1 - d3);
        bary = float3(1.0 - t, t, 0);
        return a + ab * t;
    }

    float3 cp = p - c;
    float d5 = dot(ab, cp), d6 = dot(ac, cp);
    if (d6 >= 0.0 && d5 <= d6) { bary = float3(0, 0, 1); return c; }

    float vb = d5 * d2 - d1 * d6;
    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
    {
        float t = d2 / (d2 - d6);
        bary = float3(1.0 - t, 0, t);
        return a + ac * t;
    }

    float va = d3 * d6 - d5 * d4;
    if (va <= 0.0 && d4 - d3 >= 0.0 && d5 - d6 >= 0.0)
    {
        float t = (d4 - d3) / (d4 - d3 + (d5 - d6));
        bary = float3(0, 1.0 - t, t);
        return b + (c - b) * t;
    }

    float denom = 1.0 / (va + vb + vc);
    float v = vb * denom, w = vc * denom;
    bary = float3(1.0 - v - w, v, w);
    return a + ab * v + ac * w;
}

// Nearest point of map 'map' to p (mesh space) over the triangles facing 'facing' by at least 'minFacing'.
// normal: the mesh's normals interpolated there; face: the triangle's. False when nothing in reach passes.
bool MapNearest(int map, float3 p, float3 facing, float minFacing, out float3 nearest, out float3 normal, out float3 face)
{
    nearest = p; normal = facing; face = facing;
    MapInfo info = _MapInfos[map];
    int3 cell = clamp((int3)floor((p - info.origin.xyz) / info.origin.w), 0, info.dims.xyz - 1);
    int2 list = _MapCells[info.dims.w + (cell.z * info.dims.y + cell.y) * info.dims.x + cell.x];

    float best = 3.4e38;
    for (int i = 0; i < list.y; i++)
    {
        MapTri t = _MapTris[info.offsets.x + _MapIndices[info.offsets.y + list.x + i]];
        if (dot(t.n, facing) < minFacing) continue;
        float3 bary;
        float3 q = MapClosest(p, t.a, t.b, t.c, bary);
        float3 d = q - p;
        float dist = dot(d, d);
        if (dist >= best) continue;
        best = dist;
        nearest = q;
        face = t.n;
        normal = t.na * bary.x + t.nb * bary.y + t.nc * bary.z;
    }
    if (best >= 3.4e38) return false;
    normal = dot(normal, normal) > 1e-12 ? normalize(normal) : face;
    return true;
}

#endif

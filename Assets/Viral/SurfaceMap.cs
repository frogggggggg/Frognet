using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The nearest point on a mesh from anywhere around it, in O(1): what legs plant feet with instead of
/// raycasting colliders (the colliders' convex pieces are only an approximation of the shape, and a lid over
/// a dent is what feet stood on). Built once per mesh, in the mesh's own space, so every copy of it at any
/// position, rotation or scale shares it.
///
/// A grid over the mesh's bounds (padded by `Pad` of its size); each grid cell lists the triangles that can be
/// nearest to some point inside it: exact within `Band` cells of the surface (every triangle within the nearest
/// distance + the cell's diagonal of the cell's centre), and past that the list of the nearest banded cell (an
/// approximation, only ever used by points far off the surface). A query takes its cell's few triangles and
/// returns the closest point, the mesh's own normals interpolated there (so hard edges stay hard), and the face
/// normal. A facing filter skips triangles turned away from a direction (a thin part's far side, a cube's side
/// when probing its top).
///
/// Exact within `Band` cells of the surface (~13% of the mesh's longest side), which covers every leg probe on a
/// cell; farther out, a nearby surface point (within a few % of the size). Tested offline against brute force on a
/// cube, a sphere and a red cell profile. Build: O(triangles x cells within the band of each) once per mesh, on a
/// worker thread (~150 ms for 1500 triangles); until it's done `Ready` is false. Memory ~1.7 MB for 1500
/// triangles. Query: one cell, ~15 (cube) to ~50 (red cell) triangles. The same data is packed for the GPU
/// (SurfaceMap.hlsl; LegRenderer uploads it).
/// </summary>
public sealed class SurfaceMap
{
    /// <summary>One triangle, as the GPU reads it (SurfaceMap.hlsl MapTri): corners, face normal, corner normals.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Tri
    {
        public Vector3 a, b, c, n, na, nb, nc;
    }

    /// <summary>Where a map's data sits in the shared GPU arrays (SurfaceMap.hlsl MapInfo).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public Vector4 origin;                          // xyz grid corner, w cell size
        public int nx, ny, nz, cellOffset;
        public int triOffset, indexOffset, pad0, pad1;
    }

    public const int TriStride = 7 * 12, InfoStride = 48, CellStride = 8;

    const int Resolution = 40; // cells along the longest side for a ~1300 triangle mesh (scaled by cbrt(triangles))
    const float Pad = 0.35f;   // grid reaches this share of the mesh's size past its bounds
    const float Band = 3f;     // cells from the surface with exact lists

    public Vector3 Origin { get; private set; }
    public float Cell { get; private set; }
    public Vector3Int Dims { get; private set; }
    public Tri[] Tris { get; private set; }
    public Vector2Int[] Cells { get; private set; } // per grid cell: start and count in Indices
    public int[] Indices { get; private set; }

    volatile bool _ready;
    /// <summary>Built (queries before this return false).</summary>
    public bool Ready => _ready;

    // ---------------- per mesh ----------------

    static readonly Dictionary<Mesh, SurfaceMap> Cache = new Dictionary<Mesh, SurfaceMap>();

    /// <summary>The map of a mesh, started on first ask (built on a worker thread). Null for an unreadable mesh.</summary>
    public static SurfaceMap For(Mesh mesh)
    {
        if (!mesh) return null;
        if (Cache.TryGetValue(mesh, out SurfaceMap map)) return map;
        if (!mesh.isReadable)
        {
            Debug.LogWarning($"SurfaceMap: mesh '{mesh.name}' isn't readable; legs can't plant on it (turn on Read/Write).", mesh);
            Cache[mesh] = null;
            return null;
        }

        Vector3[] vertices = mesh.vertices, normals = mesh.normals;
        int[] triangles = mesh.triangles;
        map = new SurfaceMap();
        Cache[mesh] = map;
        Task.Run(() =>
        {
            try { map.Build(vertices, triangles, normals); }
            catch (System.Exception e) { Debug.LogException(e); }
        });
        return map;
    }

    /// <summary>Builds a map now, on this thread (tests, tools).</summary>
    public static SurfaceMap BuildNow(Vector3[] vertices, int[] triangles, Vector3[] normals)
    {
        var map = new SurfaceMap();
        map.Build(vertices, triangles, normals);
        return map;
    }

    // ---------------- query ----------------

    /// <summary>
    /// Nearest point of the mesh to 'p' (mesh space) over the triangles facing 'facing' by at least 'minFacing'
    /// (cosine; below -1 = any). 'normal' = the mesh's normals interpolated there, 'face' = the triangle's.
    /// False when not built or nothing in reach passes the filter.
    /// </summary>
    public bool Nearest(Vector3 p, Vector3 facing, float minFacing, out Vector3 point, out Vector3 normal, out Vector3 face)
    {
        point = normal = face = default;
        if (!_ready) return false;

        Vector3Int d = Dims;
        Vector3 g = (p - Origin) / Cell;
        int x = Mathf.Clamp(Mathf.FloorToInt(g.x), 0, d.x - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(g.y), 0, d.y - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt(g.z), 0, d.z - 1);
        Vector2Int list = Cells[(z * d.y + y) * d.x + x];

        float best = float.MaxValue;
        for (int i = list.x, end = list.x + list.y; i < end; i++)
        {
            ref Tri t = ref Tris[Indices[i]];
            if (Vector3.Dot(t.n, facing) < minFacing) continue;
            Vector3 q = Closest(p, t.a, t.b, t.c, out float u, out float v, out float w);
            float dist = (q - p).sqrMagnitude;
            if (dist >= best) continue;
            best = dist;
            point = q;
            face = t.n;
            normal = t.na * u + t.nb * v + t.nc * w;
        }
        if (best == float.MaxValue) return false;
        normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : face;
        return true;
    }

    // Closest point on triangle abc to p, with its barycentric weights (Ericson, Real-Time Collision Detection 5.1.5).
    // SurfaceMap.hlsl has the same function; keep them alike.
    public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { u = 1f; v = w = 0f; return a; }

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { v = 1f; u = w = 0f; return b; }

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float t = d1 / (d1 - d3);
            u = 1f - t; v = t; w = 0f;
            return a + ab * t;
        }

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { w = 1f; u = v = 0f; return c; }

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float t = d2 / (d2 - d6);
            u = 1f - t; w = t; v = 0f;
            return a + ac * t;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float t = (d4 - d3) / (d4 - d3 + (d5 - d6));
            v = 1f - t; w = t; u = 0f;
            return b + (c - b) * t;
        }

        float denom = 1f / (va + vb + vc);
        v = vb * denom;
        w = vc * denom;
        u = 1f - v - w;
        return a + ab * v + ac * w;
    }

    // ---------------- build ----------------

    void Build(Vector3[] vertices, int[] triangles, Vector3[] normals)
    {
        bool hasNormals = normals != null && normals.Length == vertices.Length;
        var tris = new List<Tri>(triangles.Length / 3);
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
            Vector3 a = vertices[ia], b = vertices[ib], c = vertices[ic];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-20f) continue;
            n.Normalize();
            tris.Add(new Tri
            {
                a = a, b = b, c = c, n = n,
                na = hasNormals ? normals[ia] : n,
                nb = hasNormals ? normals[ib] : n,
                nc = hasNormals ? normals[ic] : n,
            });
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        Tris = tris.ToArray();
        if (Tris.Length == 0) return; // never ready: queries fail

        Vector3 size = max - min;
        float longest = Mathf.Max(1e-5f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
        Vector3 pad = Vector3.one * (longest * Pad);
        min -= pad;
        size += pad * 2f;
        int res = Mathf.Clamp(Mathf.RoundToInt(Resolution * Mathf.Pow(Tris.Length / 1300f, 1f / 3f)), 8, 64);
        float cell = (longest + 2f * pad.x) / res;
        var dims = new Vector3Int(Mathf.Max(1, Mathf.CeilToInt(size.x / cell)), Mathf.Max(1, Mathf.CeilToInt(size.y / cell)),
                                  Mathf.Max(1, Mathf.CeilToInt(size.z / cell)));
        int count = dims.x * dims.y * dims.z;

        // Candidates: every triangle whose box, grown by the band, reaches the cell. Counted, then filled (CSR).
        float band = Band * cell, halfDiag = cell * 0.8660254f;
        var start = new int[count + 1];
        int[] candidates = null, fill = null;
        ForEachCell(true);
        for (int i = 0; i < count; i++) start[i + 1] += start[i];
        candidates = new int[start[count]];
        fill = (int[])start.Clone();
        ForEachCell(false);

        void ForEachCell(bool counting)
        {
            for (int t = 0; t < Tris.Length; t++)
            {
                ref Tri tri = ref Tris[t];
                Vector3 lo = Vector3.Min(tri.a, Vector3.Min(tri.b, tri.c)) - Vector3.one * band - min;
                Vector3 hi = Vector3.Max(tri.a, Vector3.Max(tri.b, tri.c)) + Vector3.one * band - min;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(lo.x / cell)), x1 = Mathf.Min(dims.x - 1, Mathf.FloorToInt(hi.x / cell));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(lo.y / cell)), y1 = Mathf.Min(dims.y - 1, Mathf.FloorToInt(hi.y / cell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt(lo.z / cell)), z1 = Mathf.Min(dims.z - 1, Mathf.FloorToInt(hi.z / cell));
                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    int k = (z * dims.y + y) * dims.x + x;
                    if (counting) start[k + 1]++;
                    else candidates[fill[k]++] = t;
                }
            }
        }

        // Keep, per cell, the triangles that can be nearest to some point in it: within the nearest distance
        // from its centre plus the cell's diagonal (a point in the cell is at most a half diagonal from the
        // centre, both ways), plus a little so a facing filter still finds the next face over. (A tighter bound,
        // the exact farthest-corner distance against plane / box gaps, cut the lists by ~10% at 4x the build.)
        var cells = new Vector2Int[count];
        var indices = new List<int>(count * 8);
        var dist = new List<float>(64);
        var banded = new bool[count];
        for (int k = 0; k < count; k++)
        {
            int s = start[k], e = start[k + 1];
            if (s == e) continue;
            int x = k % dims.x, y = k / dims.x % dims.y, z = k / (dims.x * dims.y);
            Vector3 centre = min + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cell;
            dist.Clear();
            float nearest = float.MaxValue;
            for (int i = s; i < e; i++)
            {
                ref Tri t = ref Tris[candidates[i]];
                float d = (Closest(centre, t.a, t.b, t.c, out _, out _, out _) - centre).magnitude;
                dist.Add(d);
                nearest = Mathf.Min(nearest, d);
            }
            if (nearest > band) continue; // nothing really near: filled from the band below
            float keep = nearest + 2f * halfDiag + 0.5f * cell;
            int first = indices.Count;
            for (int i = s; i < e; i++)
                if (dist[i - s] <= keep) indices.Add(candidates[i]);
            cells[k] = new Vector2Int(first, indices.Count - first);
            banded[k] = true;
        }

        // Past the band: the nearest banded cell's list (breadth first from the band).
        var queue = new Queue<int>();
        for (int k = 0; k < count; k++) if (banded[k]) queue.Enqueue(k);
        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            int x = k % dims.x, y = k / dims.x % dims.y, z = k / (dims.x * dims.y);
            Visit(x - 1, y, z); Visit(x + 1, y, z);
            Visit(x, y - 1, z); Visit(x, y + 1, z);
            Visit(x, y, z - 1); Visit(x, y, z + 1);

            void Visit(int vx, int vy, int vz)
            {
                if (vx < 0 || vy < 0 || vz < 0 || vx >= dims.x || vy >= dims.y || vz >= dims.z) return;
                int n = (vz * dims.y + vy) * dims.x + vx;
                if (banded[n]) return;
                banded[n] = true;
                cells[n] = cells[k];
                queue.Enqueue(n);
            }
        }

        Origin = min;
        Cell = cell;
        Dims = dims;
        Cells = cells;
        Indices = indices.ToArray();
        _ready = true;
    }

    // ---------------- GPU ----------------

    static readonly List<SurfaceMap> Registered = new List<SurfaceMap>();
    int _gpuIndex = -1;

    /// <summary>Bumped whenever a map joins the GPU arrays (LegRenderer re-uploads them).</summary>
    public static int Version { get; private set; }

    /// <summary>This map's index in the GPU arrays (main thread; registers it on first ask). -1 while building.</summary>
    public int GpuIndex
    {
        get
        {
            if (_gpuIndex >= 0 || !_ready) return _gpuIndex;
            _gpuIndex = Registered.Count;
            Registered.Add(this);
            Version++;
            return _gpuIndex;
        }
    }

    /// <summary>Every registered map, concatenated as the GPU reads them.</summary>
    public static void Pack(List<Info> infos, List<Tri> tris, List<Vector2Int> cells, List<int> indices)
    {
        infos.Clear(); tris.Clear(); cells.Clear(); indices.Clear();
        foreach (SurfaceMap m in Registered)
        {
            infos.Add(new Info
            {
                origin = new Vector4(m.Origin.x, m.Origin.y, m.Origin.z, m.Cell),
                nx = m.Dims.x, ny = m.Dims.y, nz = m.Dims.z,
                cellOffset = cells.Count, triOffset = tris.Count, indexOffset = indices.Count,
            });
            tris.AddRange(m.Tris);
            cells.AddRange(m.Cells);
            indices.AddRange(m.Indices);
        }
    }
}

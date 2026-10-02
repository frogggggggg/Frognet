using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A body's mesh broken into thick shell pieces for <see cref="CellBurst"/> (drawn by Hidden/CellDebris).
///
/// Pieces: a weighted Voronoi of the source triangles (area-stratified seeds; distance + normal bend, so a piece
/// doesn't jump a thin middle; each seed's own noise and weight make the cracks wander and the sizes vary). Every piece
/// is its own closed solid: the outer surface, the same surface sunk <see cref="Thickness"/> inward (reversed), and
/// walls along its edges (the broken faces, tinted the inside colour), so the shader can round it into a ball.
///
/// Per vertex: position = the outer surface point (mesh space, undisplaced: the shader adds the body's relief and
/// swelling, then sinks inner points by their depth), normal; uv0 = displacement direction (Surface's UV3, else the
/// normal) + face (0 outside, 1 inside, 2 broken edge); uv1 = the piece's pivot (mid shell) + depth (mesh units);
/// uv2 = the piece's outward normal + the radius of a ball of its volume (mesh units).
///
/// Two meshes per shape with the same pieces (the fine one's triangles inherit their source triangle's piece, so
/// switching detail doesn't change them): fine (Phong-rounded <see cref="FineLevels"/> times, like the body's
/// tessellation) and coarse (the source triangles). An unreadable / missing mesh is an icosphere in its bounds.
/// Built once per shape.
/// </summary>
public static class CellShards
{
    public const int Pieces = 96;
    const float Thickness = 0.08f; // shell depth, body radii
    const int FineLevels = 1;      // fine mesh: 4x the source's triangles

    /// <summary>Breaks 'mesh' (null = a unit sphere) into pieces; 'phong' is the body's Phong rounding (0..1).</summary>
    public static void Build(Mesh mesh, float phong, out Mesh fine, out Mesh coarse)
    {
        Source(mesh, out var v, out var n, out var d, out var t, out Bounds bounds);
        float rl = Mathf.Max(Mathf.Max(bounds.extents.x, bounds.extents.y), Mathf.Max(bounds.extents.z, 1e-4f));
        var seeds = Seeds(v, n, t, rl);
        var piece = new int[t.Count / 3];
        for (int i = 0; i < piece.Length; i++)
        {
            int a = t[i * 3], b = t[i * 3 + 1], c = t[i * 3 + 2];
            piece[i] = PieceOf(seeds, (v[a] + v[b] + v[c]) / (3f * rl), (n[a] + n[b] + n[c]).normalized);
        }

        coarse = Assemble(v, n, d, t, piece, rl, bounds, (mesh ? mesh.name : "Sphere") + " shards (coarse)");
        for (int l = 0; l < FineLevels; l++)
        {
            Subdivide(v, n, d, t, phong);
            var finer = new int[piece.Length * 4]; // triangle i became 4i .. 4i + 3
            for (int i = 0; i < finer.Length; i++) finer[i] = piece[i >> 2];
            piece = finer;
        }
        fine = Assemble(v, n, d, t, piece, rl, bounds, (mesh ? mesh.name : "Sphere") + " shards");
    }

    // ---------------- source ----------------

    static void Source(Mesh mesh, out List<Vector3> v, out List<Vector3> n, out List<Vector3> d, out List<int> t, out Bounds bounds)
    {
        v = new List<Vector3>(); n = new List<Vector3>(); d = new List<Vector3>(); t = new List<int>();
        if (mesh && mesh.isReadable)
        {
            bounds = mesh.bounds;
            mesh.GetVertices(v);
            mesh.GetNormals(n);
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles) t.AddRange(mesh.GetTriangles(s));
            if (n.Count != v.Count) FaceNormals(v, t, n);
            mesh.GetUVs(Surface.DisplacementChannel, d);
            if (d.Count != v.Count) { d.Clear(); d.AddRange(n); }
            for (int i = 0; i < d.Count; i++) if (d[i].sqrMagnitude < 0.01f) d[i] = n[i];
            return;
        }
        // The ellipsoid in its bounds.
        bounds = mesh ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one * 2f);
        Icosphere(v, t, 3);
        Vector3 e = Vector3.Max(bounds.extents, Vector3.one * 1e-4f);
        for (int i = 0; i < v.Count; i++)
        {
            Vector3 u = v[i];
            v[i] = bounds.center + Vector3.Scale(u, e);
            n.Add(new Vector3(u.x / e.x, u.y / e.y, u.z / e.z).normalized);
        }
        d.AddRange(n);
    }

    static void FaceNormals(List<Vector3> v, List<int> t, List<Vector3> n)
    {
        n.Clear();
        for (int i = 0; i < v.Count; i++) n.Add(Vector3.zero);
        for (int i = 0; i < t.Count; i += 3)
        {
            Vector3 f = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
            n[t[i]] += f; n[t[i + 1]] += f; n[t[i + 2]] += f;
        }
        for (int i = 0; i < n.Count; i++) n[i] = n[i].normalized;
    }

    /// <summary>A unit icosphere: the icosahedron subdivided 'levels' times, Unity's winding.</summary>
    public static void Icosphere(List<Vector3> v, List<int> t, int levels)
    {
        float g = (1f + Mathf.Sqrt(5f)) * 0.5f;
        v.AddRange(new[]
        {
            new Vector3(-1, g, 0), new Vector3(1, g, 0), new Vector3(-1, -g, 0), new Vector3(1, -g, 0),
            new Vector3(0, -1, g), new Vector3(0, 1, g), new Vector3(0, -1, -g), new Vector3(0, 1, -g),
            new Vector3(g, 0, -1), new Vector3(g, 0, 1), new Vector3(-g, 0, -1), new Vector3(-g, 0, 1),
        });
        int[] faces =
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        // The list is counter-clockwise seen from outside; Unity's front faces are clockwise.
        for (int i = 0; i < faces.Length; i += 3) t.AddRange(new[] { faces[i], faces[i + 2], faces[i + 1] });
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
        var n = new List<Vector3>(v);
        var d = new List<Vector3>(v);
        for (int l = 0; l < levels; l++) Subdivide(v, n, d, t, 0f);
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
    }

    // Every triangle into four; new points pulled toward the corners' tangent planes ('phong', as the body's
    // tessellation does), so a low-poly body rounds out.
    static void Subdivide(List<Vector3> v, List<Vector3> n, List<Vector3> d, List<int> t, float phong)
    {
        var mids = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (mids.TryGetValue(k, out int m)) return m;
            Vector3 flat = (v[a] + v[b]) * 0.5f;
            Vector3 projected = (flat - Vector3.Dot(flat - v[a], n[a]) * n[a] + flat - Vector3.Dot(flat - v[b], n[b]) * n[b]) * 0.5f;
            v.Add(Vector3.Lerp(flat, projected, phong));
            n.Add((n[a] + n[b]).normalized);
            d.Add((d[a] + d[b]).normalized);
            return mids[k] = v.Count - 1;
        }
        int count = t.Count;
        var result = new List<int>(count * 4);
        for (int i = 0; i < count; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
            result.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
        }
        t.Clear();
        t.AddRange(result);
    }

    // ---------------- pieces ----------------

    struct Seed { public Vector3 position, normal, noise; public float weight; }

    // Area-weighted, stratified (one per equal slice of the area), so they cover the surface evenly.
    static Seed[] Seeds(List<Vector3> v, List<Vector3> n, List<int> t, float rl)
    {
        var random = new System.Random(4217);
        int tris = t.Count / 3;
        var cumulative = new float[Mathf.Max(tris, 1)];
        float total = 0f;
        for (int i = 0; i < tris; i++)
        {
            total += Vector3.Cross(v[t[i * 3 + 1]] - v[t[i * 3]], v[t[i * 3 + 2]] - v[t[i * 3]]).magnitude * 0.5f;
            cumulative[i] = total;
        }
        var seeds = new Seed[Pieces];
        for (int s = 0; s < Pieces; s++)
        {
            float pick = (s + (float)random.NextDouble()) / Pieces * total;
            int lo = 0, hi = tris - 1;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (cumulative[mid] < pick) lo = mid + 1; else hi = mid; }
            int a = t[lo * 3], b = t[lo * 3 + 1], c = t[lo * 3 + 2];
            float r1 = Mathf.Sqrt((float)random.NextDouble()), r2 = (float)random.NextDouble();
            float wa = 1f - r1, wb = r1 * (1f - r2), wc = r1 * r2;
            seeds[s] = new Seed
            {
                position = (v[a] * wa + v[b] * wb + v[c] * wc) / rl,
                normal = (n[a] * wa + n[b] * wb + n[c] * wc).normalized,
                weight = 0.55f + 0.9f * (float)random.NextDouble(), // bigger and smaller pieces
                noise = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()) * 100f,
            };
        }
        return seeds;
    }

    // Which piece a point (body radii) belongs to: nearest seed, bending round the body costs extra (a thin middle's
    // two faces stay apart), each seed's noise makes its border wander.
    static int PieceOf(Seed[] seeds, Vector3 q, Vector3 normal)
    {
        int best = 0;
        float bestScore = float.MaxValue;
        for (int k = 0; k < seeds.Length; k++)
        {
            Seed s = seeds[k];
            float score = ((q - s.position).sqrMagnitude + 0.6f * (1f - Vector3.Dot(normal, s.normal))) / s.weight;
            Vector3 p = q * 2.6f + s.noise;
            float wander = (Mathf.PerlinNoise(p.x, p.y) + Mathf.PerlinNoise(p.y, p.z) + Mathf.PerlinNoise(p.z, p.x)) / 3f;
            score *= 0.6f + 0.8f * wander;
            if (score < bestScore) { bestScore = score; best = k; }
        }
        return best;
    }

    static Mesh Assemble(List<Vector3> v, List<Vector3> n, List<Vector3> d, List<int> t, int[] piece, float rl, Bounds bounds, string name)
    {
        int tris = t.Count / 3;
        float depth = Thickness * rl;

        // Each piece's middle and outward normal (area-weighted), and the ball of its volume.
        var middle = new Vector3[Pieces];
        var facing = new Vector3[Pieces];
        var area = new float[Pieces];
        for (int i = 0; i < tris; i++)
        {
            int a = t[i * 3], b = t[i * 3 + 1], c = t[i * 3 + 2];
            Vector3 centre = (v[a] + v[b] + v[c]) / 3f;
            float w = Vector3.Cross(v[b] - v[a], v[c] - v[a]).magnitude * 0.5f;
            middle[piece[i]] += centre * w;
            facing[piece[i]] += (n[a] + n[b] + n[c]) * w;
            area[piece[i]] += w;
        }
        for (int k = 0; k < Pieces; k++)
        {
            facing[k] = facing[k].sqrMagnitude > 1e-12f ? facing[k].normalized : Vector3.up;
            middle[k] = middle[k] / Mathf.Max(area[k], 1e-12f) - facing[k] * depth * 0.5f; // mid shell
        }

        // Welded positions (seams split vertices): an edge is broken where the same piece doesn't hold it both ways.
        float cell = rl * 1e-4f;
        var weldOf = new Dictionary<Vector3Int, int>();
        var weld = new int[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            var key = Vector3Int.RoundToInt(v[i] / cell);
            if (!weldOf.TryGetValue(key, out int w)) weldOf[key] = w = weldOf.Count;
            weld[i] = w;
        }
        long Edge(int a, int b) => ((long)weld[a] << 32) | (uint)weld[b];

        var byPiece = new List<int>[Pieces];
        for (int k = 0; k < Pieces; k++) byPiece[k] = new List<int>();
        for (int i = 0; i < tris; i++) byPiece[piece[i]].Add(i);

        var P = new List<Vector3>(); var N = new List<Vector3>();
        var U0 = new List<Vector4>(); var U1 = new List<Vector4>(); var U2 = new List<Vector4>();
        var I = new List<int>();
        var outer = new Dictionary<int, int>();
        var inner = new Dictionary<int, int>();
        var edges = new HashSet<long>();
        for (int k = 0; k < Pieces; k++)
        {
            if (byPiece[k].Count == 0) continue;
            outer.Clear(); inner.Clear(); edges.Clear();
            var pivot = new Vector4(middle[k].x, middle[k].y, middle[k].z, 0f);
            float ball = Mathf.Pow(3f * area[k] * depth / (4f * Mathf.PI), 1f / 3f);
            var info = new Vector4(facing[k].x, facing[k].y, facing[k].z, ball);

            int Add(int i, Vector3 normal, float face, float sunk)
            {
                P.Add(v[i]);
                N.Add(normal);
                U0.Add(new Vector4(d[i].x, d[i].y, d[i].z, face));
                U1.Add(new Vector4(pivot.x, pivot.y, pivot.z, sunk));
                U2.Add(info);
                return P.Count - 1;
            }
            int Outer(int i) => outer.TryGetValue(i, out int o) ? o : outer[i] = Add(i, n[i], 0f, 0f);
            int Inner(int i) => inner.TryGetValue(i, out int o) ? o : inner[i] = Add(i, -n[i], 1f, depth);

            foreach (int tri in byPiece[k])
            {
                int a = t[tri * 3], b = t[tri * 3 + 1], c = t[tri * 3 + 2];
                I.Add(Outer(a)); I.Add(Outer(b)); I.Add(Outer(c));
                I.Add(Inner(a)); I.Add(Inner(c)); I.Add(Inner(b));
                edges.Add(Edge(a, b)); edges.Add(Edge(b, c)); edges.Add(Edge(c, a));
            }
            // Walls along the broken edges, facing away from the piece.
            foreach (int tri in byPiece[k])
                for (int e = 0; e < 3; e++)
                {
                    int a = t[tri * 3 + e], b = t[tri * 3 + (e + 1) % 3], c = t[tri * 3 + (e + 2) % 3];
                    if (edges.Contains(Edge(b, a))) continue;
                    Vector3 wn = Vector3.Cross(v[b] - v[a], n[a] + n[b]).normalized;
                    if (Vector3.Dot(wn, v[c] - v[a]) > 0f) wn = -wn;
                    int oa = Add(a, wn, 2f, 0f), ob = Add(b, wn, 2f, 0f), ib = Add(b, wn, 2f, depth), ia = Add(a, wn, 2f, depth);
                    Vector3 front = Vector3.Cross(v[b] - v[a], (v[b] - d[b] * depth) - v[a]); // Unity: cross(b-a, c-a) faces the front
                    if (Vector3.Dot(front, wn) >= 0f) { I.Add(oa); I.Add(ob); I.Add(ib); I.Add(oa); I.Add(ib); I.Add(ia); }
                    else { I.Add(oa); I.Add(ib); I.Add(ob); I.Add(oa); I.Add(ia); I.Add(ib); }
                }
        }

        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        if (P.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(P);
        mesh.SetNormals(N);
        mesh.SetUVs(0, U0);
        mesh.SetUVs(1, U1);
        mesh.SetUVs(2, U2);
        mesh.SetTriangles(I, 0, false);
        bounds.Expand(rl);
        mesh.bounds = bounds; // RenderMeshPrimitives culls by the draw's world bounds, not this
        mesh.UploadMeshData(true);
        return mesh;
    }
}

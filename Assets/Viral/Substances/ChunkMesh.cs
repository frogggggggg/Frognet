using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural resource chunk meshes, built once per (shape, detail) and shared by every chunk.
/// Every shape is a displaced icosphere (one closed shell, radius varying with direction). What's walked
/// (the chunk's Surface and MeshCollider) is a separate, lighter hull of the same shape with the small
/// knobs smoothed off (<see cref="Walk"/>): walking every knob flicked the crawler's up (and the camera
/// with it) several times a second. One navmesh graph and one cooked collider per shape. All fit a
/// unit ball (the chunk's transform scale is its radius); vertex colour alpha = occlusion (1 on the
/// outside, darker in the folds). Variety between chunks comes from their size, rotation, tint and the
/// shader's per-instance wobble, not from separate meshes.
/// - Lumpy (glucose): a knobbly ball, round lobes fused onto a core, small domes on top.
/// - Coil (protein): a globule of beads fused along a chain folded up on itself.
/// </summary>
public static class ChunkMesh
{
    public enum Shape { Lumpy, Coil }

    static readonly Dictionary<int, Mesh> s_meshes = new Dictionary<int, Mesh>();
    static readonly Dictionary<Shape, float> s_scale = new Dictionary<Shape, float>(); // 1 / the drawn shape's reach

    /// <summary>The shared drawn mesh for a shape: near (full detail) or far (light).</summary>
    public static Mesh Get(Shape shape, bool near)
    {
        int key = (int)shape * 2 + (near ? 0 : 1);
        if (s_meshes.TryGetValue(key, out Mesh m) && m) return m;
        m = Radial(near ? 4 : 2, RadiusFunction(shape), out float max);
        m.name = "Chunk " + shape + (near ? "" : " Far");
        m.hideFlags = HideFlags.DontSave;
        s_meshes[key] = m;
        if (near) s_scale[shape] = 1f / max;
        return m;
    }

    /// <summary>The shared walkable hull for a shape (Surface + MeshCollider): 1280 triangles, the drawn
    /// shape with its knobs smoothed off, pushed back out a little toward them so feet don't sink far.</summary>
    public static Mesh Walk(Shape shape)
    {
        int key = 50 + (int)shape;
        if (s_meshes.TryGetValue(key, out Mesh m) && m) return m;
        Get(shape, true); // the drawn mesh's scale, so the two match
        Func<Vector3, float> radiusAt = RadiusFunction(shape);
        Icosphere(3, out List<Vector3> verts, out List<int> tris);
        int n = verts.Count;
        var raw = new float[n];
        for (int i = 0; i < n; i++) raw[i] = radiusAt(verts[i]);

        // Neighbours, then a few rounds of averaging the radius with them.
        var links = new List<int>[n];
        for (int i = 0; i < n; i++) links[i] = new List<int>(6);
        for (int i = 0; i < tris.Count; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[i + e], b = tris[i + (e + 1) % 3];
                if (!links[a].Contains(b)) links[a].Add(b);
                if (!links[b].Contains(a)) links[b].Add(a);
            }
        float[] smooth = (float[])raw.Clone(), next = new float[n];
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                foreach (int j in links[i]) sum += smooth[j];
                next[i] = 0.5f * smooth[i] + 0.5f * sum / links[i].Count;
            }
            (smooth, next) = (next, smooth);
        }
        float scale = s_scale[shape];
        for (int i = 0; i < n; i++)
            verts[i] *= (smooth[i] + 0.3f * Mathf.Max(0f, raw[i] - smooth[i])) * scale;

        m = new Mesh { name = "Chunk " + shape + " Walk", hideFlags = HideFlags.DontSave };
        m.SetVertices(verts);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        s_meshes[key] = m;
        return m;
    }

    static Func<Vector3, float> RadiusFunction(Shape shape) => shape == Shape.Coil ? Globule() : Lumpy();

    /// <summary>A small smooth ball (unit radius), for the cores.</summary>
    public static Mesh Ball()
    {
        const int key = 100;
        if (s_meshes.TryGetValue(key, out Mesh m) && m) return m;
        Icosphere(2, out List<Vector3> v, out List<int> t);
        m = new Mesh { name = "Chunk Core", hideFlags = HideFlags.DontSave };
        m.SetVertices(v);
        m.SetNormals(v);
        m.SetTriangles(t, 0);
        m.RecalculateBounds();
        s_meshes[key] = m;
        return m;
    }

    // An icosphere with each vertex pushed out to radiusAt(direction), scaled so the farthest is 1.
    // Occlusion from how far in each vertex sits. 'max': the farthest radius before scaling.
    static Mesh Radial(int subdivisions, Func<Vector3, float> radiusAt, out float max)
    {
        Icosphere(subdivisions, out List<Vector3> verts, out List<int> tris);
        var radii = new float[verts.Count];
        float min = float.MaxValue;
        max = 0f;
        for (int i = 0; i < verts.Count; i++)
        {
            float r = radiusAt(verts[i]);
            radii[i] = r;
            min = Mathf.Min(min, r);
            max = Mathf.Max(max, r);
        }
        var colors = new List<Color>(verts.Count);
        for (int i = 0; i < verts.Count; i++)
        {
            float k = Mathf.InverseLerp(min, max, radii[i]);
            colors.Add(new Color(1f, 1f, 1f, Mathf.Lerp(0.45f, 1f, Mathf.Sqrt(k))));
            verts[i] *= radii[i] / max;
        }
        var m = new Mesh();
        m.SetVertices(verts);
        m.SetColors(colors);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    // ---------------- lumpy (glucose) ----------------

    static Func<Vector3, float> Lumpy()
    {
        var rng = new System.Random(4117);
        float R() => (float)rng.NextDouble();
        Vector3 Dir() { Vector3 d; do d = new Vector3(R() * 2 - 1, R() * 2 - 1, R() * 2 - 1); while (d.sqrMagnitude > 1f || d.sqrMagnitude < 0.01f); return d.normalized; }

        // Lobes: (centre, radius), the silhouette. Knobs: (direction, angular radius), domes on top.
        var lobes = new List<Vector4>();
        for (int i = 0; i < 14; i++) { Vector3 c = Dir() * Mathf.Lerp(0.3f, 0.45f, R()); lobes.Add(new Vector4(c.x, c.y, c.z, Mathf.Lerp(0.38f, 0.5f, R()))); }
        var knobs = new List<Vector4>();
        for (int i = 0; i < 70; i++) { Vector3 c = Dir(); knobs.Add(new Vector4(c.x, c.y, c.z, Mathf.Lerp(0.14f, 0.24f, R()))); }

        return d =>
        {
            float body = SmoothMax(Hit(d, lobes, 0.62f), 14f);
            s_hits.Clear();
            s_hits.Add(0f);
            foreach (Vector4 k in knobs)
            {
                float angle2 = 2f * (1f - Vector3.Dot(d, new Vector3(k.x, k.y, k.z))); // ~angle squared
                if (angle2 < k.w * k.w) s_hits.Add(Mathf.Sqrt(k.w * k.w - angle2));
            }
            return body * (1f + 0.35f * SmoothMax(s_hits, 40f));
        };
    }

    // ---------------- globule (protein) ----------------

    static Func<Vector3, float> Globule()
    {
        var rng = new System.Random(9173);
        float R() => (float)rng.NextDouble();

        // A random walk kept inside the ball, turning a lot: the folded chain.
        var control = new List<Vector3>();
        Vector3 p = new Vector3(-0.35f, 0.1f, 0f), dir = Vector3.right;
        for (int i = 0; i < 12; i++)
        {
            control.Add(p);
            Vector3 turn = new Vector3(R() * 2 - 1, R() * 2 - 1, R() * 2 - 1);
            dir = (dir + turn * 1.4f - p * 1.6f).normalized; // pulled back toward the middle
            p += dir * 0.3f;
        }

        // Beads along it (Catmull-Rom), alternating a little in size: the residues.
        const int Beads = 110;
        var beads = new List<Vector4>(Beads);
        for (int i = 0; i < Beads; i++)
        {
            float u = i / (float)(Beads - 1) * (control.Count - 1);
            int k = Mathf.Min((int)u, control.Count - 2);
            float f = u - k;
            Vector3 a = control[Mathf.Max(k - 1, 0)], b = control[k], c = control[k + 1], e = control[Mathf.Min(k + 2, control.Count - 1)];
            Vector3 at = 0.5f * (2f * b + (c - a) * f + (2f * a - 5f * b + 4f * c - e) * f * f + (3f * b - a - 3f * c + e) * f * f * f);
            float bead = 0.19f + 0.05f * (i % 2) + 0.03f * Mathf.Sin(i * 0.7f);
            beads.Add(new Vector4(at.x, at.y, at.z, bead));
        }

        return d => SmoothMax(Hit(d, beads, 0.42f), 18f);
    }

    // ---------------- helpers ----------------

    // Per sphere, how far out along 'd' it reaches (skipped if the ray misses it), plus 'core'.
    static readonly List<float> s_hits = new List<float>();

    static List<float> Hit(Vector3 d, List<Vector4> spheres, float core)
    {
        s_hits.Clear();
        s_hits.Add(core);
        foreach (Vector4 s in spheres)
        {
            var c = new Vector3(s.x, s.y, s.z);
            float b = Vector3.Dot(d, c), disc = b * b - c.sqrMagnitude + s.w * s.w;
            if (disc > 0f) s_hits.Add(b + Mathf.Sqrt(disc));
        }
        return s_hits;
    }

    // Log-sum-exp: a max with the creases between lobes rounded off.
    static float SmoothMax(List<float> values, float k)
    {
        float m = 0f;
        foreach (float v in values) m = Mathf.Max(m, v);
        float sum = 0f;
        foreach (float v in values) sum += Mathf.Exp(k * (v - m));
        return m + Mathf.Log(sum) / k;
    }

    static void Icosphere(int subdivisions, out List<Vector3> verts, out List<int> tris)
    {
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        verts = new List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
        tris = new List<int>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        var mid = new Dictionary<long, int>();
        for (int s = 0; s < subdivisions; s++)
        {
            var next = new List<int>(tris.Count * 4);
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Mid(a, b, verts, mid), bc = Mid(b, c, verts, mid), ca = Mid(c, a, verts, mid);
                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            tris = next;
        }
    }

    static int Mid(int a, int b, List<Vector3> verts, Dictionary<long, int> cache)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (cache.TryGetValue(key, out int i)) return i;
        verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
        return cache[key] = verts.Count - 1;
    }
}

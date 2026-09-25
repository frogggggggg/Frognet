using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The outside of a creature's *drawn* body, as seen from its middle: per direction (in the frame it's shown in,
/// Organism.Shown) the farthest surface of its meshes. A star-shaped hull, so things that hold on (antibodies) sit
/// on the real shape instead of a sphere fitted to its collider.
/// Built once per creature on first use (<see cref="For"/>): every triangle of the meshes under the shown frame is
/// sampled (a few points across, by its angular size) into a cube map of <see cref="N"/> x N bins per face,
/// keeping the farthest distance per bin. Meshes need Read/Write; with none readable it falls back to a sphere
/// the size of the collider. Cost: O(triangles) once per creature; <see cref="Radius(Vector3)"/> O(1),
/// <see cref="Radius(Vector3, float)"/> (a footprint) O(bins) = 864, used only when laying out slots.
/// </summary>
public class BodyHull
{
    const int N = 12;
    const int Bins = 6 * N * N;
    const int MaxSamples = 4; // per triangle edge
    const float BinAngle = Mathf.PI * 0.5f / N;

    public readonly Transform Frame;
    readonly Vector3 _centre;   // in the frame (rotation only, world units)
    readonly float[] _radius = new float[Bins];

    static readonly Dictionary<Organism, BodyHull> s_all = new Dictionary<Organism, BodyHull>();
    static readonly List<Organism> s_dead = new List<Organism>();
    static readonly List<Renderer> s_renderers = new List<Renderer>();
    static readonly List<Vector3> s_verts = new List<Vector3>();
    static readonly List<int> s_tris = new List<int>();
    static Vector3[] s_binDirs;

    /// <summary>The hull of 'o', built the first time.</summary>
    public static BodyHull For(Organism o)
    {
        if (s_all.TryGetValue(o, out BodyHull h) && h.Frame) return h;
        if (s_all.Count > 64)
        {
            s_dead.Clear();
            foreach (Organism k in s_all.Keys) if (!k) s_dead.Add(k);
            foreach (Organism k in s_dead) s_all.Remove(k);
        }
        h = new BodyHull(o);
        s_all[o] = h;
        return h;
    }

    /// <summary>The middle of the body (world): what the hull and anything wrapped round it are centred on.</summary>
    public Vector3 Centre => Frame.position + Frame.rotation * _centre;

    /// <summary>Distance from the centre to the body's outside along 'localDir' (unit, in the frame).</summary>
    public float Radius(Vector3 localDir) => _radius[Bin(localDir)];

    /// <summary>The farthest the body reaches within 'halfAngle' radians of 'localDir' (unit, in the frame): what
    /// something that wide lying over that spot has to clear.</summary>
    public float Radius(Vector3 localDir, float halfAngle)
    {
        if (halfAngle <= 0f) return Radius(localDir);
        float cos = Mathf.Cos(Mathf.Min(halfAngle + BinAngle * 0.7f, Mathf.PI));
        float r = _radius[Bin(localDir)];
        for (int i = 0; i < Bins; i++)
            if (_radius[i] > r && Vector3.Dot(s_binDirs[i], localDir) >= cos) r = _radius[i];
        return r;
    }

    /// <summary>Distance to the body's outside toward 'world' (a world point), from the centre.</summary>
    public float RadiusToward(Vector3 world)
    {
        Vector3 d = Quaternion.Inverse(Frame.rotation) * (world - Centre);
        return d.sqrMagnitude > 1e-10f ? Radius(d.normalized) : Radius(Vector3.up);
    }

    BodyHull(Organism o)
    {
        if (s_binDirs == null)
        {
            s_binDirs = new Vector3[Bins];
            for (int f = 0; f < 6; f++)
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                s_binDirs[(f * N + y) * N + x] = FaceDir(f, (x + 0.5f) / N * 2f - 1f, (y + 0.5f) / N * 2f - 1f).normalized;
        }

        Frame = o.Shown;
        Quaternion toFrame = Quaternion.Inverse(Frame.rotation);
        Vector3 origin = Frame.position;

        // Meshes drawn under the shown frame (else anywhere on the creature), bigger bits (a trailing rope) left out.
        Frame.GetComponentsInChildren(s_renderers);
        s_renderers.RemoveAll(r => !(r is MeshRenderer || r is SkinnedMeshRenderer) || !r.enabled);
        if (s_renderers.Count == 0)
        {
            o.GetComponentsInChildren(s_renderers);
            s_renderers.RemoveAll(r => !(r is MeshRenderer || r is SkinnedMeshRenderer) || !r.enabled);
        }

        Collider col = o.GetComponentInChildren<Collider>();
        float maxExtent = col ? col.bounds.extents.magnitude * 4f : float.PositiveInfinity;

        // Centre: the middle of what's drawn.
        bool any = false;
        Bounds b = default;
        foreach (Renderer r in s_renderers)
        {
            if (r.bounds.extents.magnitude > maxExtent || !Readable(r)) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        if (any)
        {
            _centre = toFrame * (b.center - origin);
            var baked = new List<Mesh>();
            foreach (Renderer r in s_renderers)
            {
                if (r.bounds.extents.magnitude > maxExtent || !Readable(r)) continue;
                Mesh mesh;
                Matrix4x4 toWorld;
                if (r is SkinnedMeshRenderer sk)
                {
                    mesh = new Mesh();
                    sk.BakeMesh(mesh, true);
                    baked.Add(mesh);
                    toWorld = sk.transform.localToWorldMatrix;
                }
                else
                {
                    mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    toWorld = r.transform.localToWorldMatrix;
                }
                Add(mesh, toWorld, toFrame, origin);
            }
            foreach (Mesh m in baked) Object.Destroy(m);
        }

        float min = float.PositiveInfinity;
        int filled = 0;
        for (int i = 0; i < Bins; i++)
            if (_radius[i] > 0f) { min = Mathf.Min(min, _radius[i]); filled++; }
        if (filled <= Bins / 4)
        {
            // Nothing readable: a sphere the size of the collider (what this used before).
            float reach = 0.8f;
            if (col)
            {
                Bounds cb = col.bounds;
                reach = Mathf.Min(cb.extents.x, Mathf.Min(cb.extents.y, cb.extents.z));
                _centre = toFrame * (cb.center - origin);
            }
            for (int i = 0; i < Bins; i++) _radius[i] = reach;
            return;
        }
        // Bins no sample landed in (a surface with holes, or thin sampling): the nearest filled neighbour, else the smallest.
        for (int i = 0; i < Bins; i++)
        {
            if (_radius[i] > 0f) continue;
            float best = -1f, r = min;
            for (int j = 0; j < Bins; j++)
            {
                if (_radius[j] <= 0f) continue;
                float d = Vector3.Dot(s_binDirs[i], s_binDirs[j]);
                if (d > best) { best = d; r = _radius[j]; }
            }
            _radius[i] = -r; // marked, so fills don't feed fills
        }
        for (int i = 0; i < Bins; i++) _radius[i] = Mathf.Abs(_radius[i]);
    }

    static bool Readable(Renderer r)
    {
        if (r is SkinnedMeshRenderer sk) return sk.sharedMesh && sk.sharedMesh.isReadable;
        MeshFilter f = r.GetComponent<MeshFilter>();
        return f && f.sharedMesh && f.sharedMesh.isReadable;
    }

    // Every triangle, sampled across by its angular size seen from the centre, into the farthest-distance bins.
    void Add(Mesh mesh, Matrix4x4 toWorld, Quaternion toFrame, Vector3 origin)
    {
        mesh.GetVertices(s_verts);
        for (int v = 0; v < s_verts.Count; v++)
            s_verts[v] = toFrame * (toWorld.MultiplyPoint3x4(s_verts[v]) - origin) - _centre;
        for (int sub = 0; sub < mesh.subMeshCount; sub++)
        {
            if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
            mesh.GetTriangles(s_tris, sub);
            for (int t = 0; t + 2 < s_tris.Count; t += 3)
            {
                Vector3 a = s_verts[s_tris[t]], b = s_verts[s_tris[t + 1]], c = s_verts[s_tris[t + 2]];
                float near = Mathf.Max(Mathf.Min(a.magnitude, Mathf.Min(b.magnitude, c.magnitude)), 1e-4f);
                float spread = Mathf.Max((a - b).magnitude, Mathf.Max((b - c).magnitude, (c - a).magnitude)) / near;
                int n = Mathf.Clamp(Mathf.CeilToInt(spread / BinAngle), 1, MaxSamples);
                for (int i = 0; i <= n; i++)
                for (int j = 0; i + j <= n; j++)
                {
                    Vector3 p = a + (b - a) * ((float)i / n) + (c - a) * ((float)j / n);
                    float d = p.magnitude;
                    if (d < 1e-5f) continue;
                    int bin = Bin(p / d);
                    if (d > _radius[bin]) _radius[bin] = d;
                }
            }
        }
    }

    static Vector3 FaceDir(int face, float u, float v)
    {
        switch (face)
        {
            case 0: return new Vector3(1f, v, -u);
            case 1: return new Vector3(-1f, v, u);
            case 2: return new Vector3(u, 1f, -v);
            case 3: return new Vector3(u, -1f, v);
            case 4: return new Vector3(u, v, 1f);
            default: return new Vector3(-u, v, -1f);
        }
    }

    static int Bin(Vector3 d)
    {
        float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
        int face;
        float u, v, m;
        if (ax >= ay && ax >= az) { m = ax; face = d.x > 0f ? 0 : 1; u = d.x > 0f ? -d.z : d.z; v = d.y; }
        else if (ay >= az) { m = ay; face = d.y > 0f ? 2 : 3; u = d.x; v = d.y > 0f ? -d.z : d.z; }
        else { m = az; face = d.z > 0f ? 4 : 5; u = d.z > 0f ? d.x : -d.x; v = d.y; }
        if (m < 1e-8f) return 0;
        int x = Mathf.Clamp((int)((u / m * 0.5f + 0.5f) * N), 0, N - 1);
        int y = Mathf.Clamp((int)((v / m * 0.5f + 0.5f) * N), 0, N - 1);
        return (face * N + y) * N + x;
    }
}

using System.Collections.Generic;
using Pathfinding;
using UnityEngine;

/// <summary>
/// The shortest way round a Surface to one goal, shared by every crawler heading there. Works on any
/// shape the graph covers: cubes, rings, concave blobs, holes (a straight chord projected onto the
/// surface only works on round, convex things, and gets trapped in the rest).
///
/// One Dijkstra flood over the mesh's welded vertices (edge lengths, seeded from the goal's triangle),
/// then a gradient per vertex (area-weighted over its triangles). A crawler blends its triangle's three
/// by barycentric weights: one smooth downhill direction, and the only minimum is the goal, so
/// descending it can't get stuck. Near the goal (its triangle and the ones next to it) there's nothing
/// to route round: <see cref="Direction"/> returns false and the caller goes straight at it.
///
/// Cost: a flood is O(V log V) (V = the mesh's vertices), per chased goal, only when the goal changes
/// triangle and at most every <see cref="RebuildInterval"/>, however many crawlers read it. A query is
/// O(1). Topology (welded vertices, edges) is built once per graph and shared by every Surface with that
/// mesh; fields nobody asked for in a while are dropped.
/// </summary>
public sealed class SurfaceField
{
    const float RebuildInterval = 0.25f, ForgetAfter = 5f;

    // ---------------- per graph ----------------

    sealed class Topology
    {
        public Vector3[] vertices;          // graph space, welded
        public int[] tris;                  // 3 vertex ids per triangle
        public TriangleMeshNode[] nodes;    // per triangle
        public Dictionary<GraphNode, int> index;
        public int[] edgeStart, edgeTo;     // CSR adjacency: vertex v's edges are edgeStart[v]..edgeStart[v+1]
        public float[] edgeLength;
    }

    static readonly Dictionary<NavMeshGraph, Topology> Topologies = new Dictionary<NavMeshGraph, Topology>();

    /// <summary>Drop what was built for a graph (it was rescanned).</summary>
    public static void Forget(NavMeshGraph graph) => Topologies.Remove(graph);

    static Topology TopologyOf(NavMeshGraph graph)
    {
        if (Topologies.TryGetValue(graph, out Topology topo)) return topo;

        var ids = new Dictionary<Int3, int>();
        var vertices = new List<Vector3>();
        var tris = new List<int>();
        var nodes = new List<TriangleMeshNode>();
        graph.GetNodes(n =>
        {
            if (!(n is TriangleMeshNode tri)) return;
            nodes.Add(tri);
            for (int i = 0; i < 3; i++)
            {
                Int3 v = tri.GetVertex(i);
                if (!ids.TryGetValue(v, out int id))
                {
                    ids[v] = id = vertices.Count;
                    vertices.Add((Vector3)v);
                }
                tris.Add(id);
            }
        });

        // Unique edges, then flattened per vertex.
        var neighbours = new List<int>[vertices.Count];
        for (int i = 0; i < neighbours.Length; i++) neighbours[i] = new List<int>(6);
        for (int t = 0; t < tris.Count; t += 3)
        for (int k = 0; k < 3; k++)
        {
            int a = tris[t + k], b = tris[t + (k + 1) % 3];
            if (a == b || neighbours[a].Contains(b)) continue;
            neighbours[a].Add(b);
            neighbours[b].Add(a);
        }

        topo = new Topology
        {
            vertices = vertices.ToArray(),
            tris = tris.ToArray(),
            nodes = nodes.ToArray(),
            index = new Dictionary<GraphNode, int>(nodes.Count),
            edgeStart = new int[vertices.Count + 1],
        };
        for (int i = 0; i < nodes.Count; i++) topo.index[nodes[i]] = i;

        int total = 0;
        for (int v = 0; v < neighbours.Length; v++) { topo.edgeStart[v] = total; total += neighbours[v].Count; }
        topo.edgeStart[neighbours.Length] = total;
        topo.edgeTo = new int[total];
        topo.edgeLength = new float[total];
        for (int v = 0, e = 0; v < neighbours.Length; v++)
            foreach (int w in neighbours[v])
            {
                topo.edgeTo[e] = w;
                topo.edgeLength[e++] = Vector3.Distance(topo.vertices[v], topo.vertices[w]);
            }

        return Topologies[graph] = topo;
    }

    // ---------------- per (surface, goal) ----------------

    static readonly Dictionary<(Surface, int), SurfaceField> Fields = new Dictionary<(Surface, int), SurfaceField>();
    static readonly List<(Surface, int)> Stale = new List<(Surface, int)>();
    static float s_swept;
    static NNConstraint s_nn;

    readonly Surface _surface;
    readonly Topology _topo;
    readonly float[] _dist;
    readonly Vector3[] _grad; // per vertex: area-weighted sum of its triangles' gradients (uphill)
    int _goalTri = -1;
    float _built = float.NegativeInfinity, _used;

    SurfaceField(Surface surface, Topology topo)
    {
        _surface = surface;
        _topo = topo;
        _dist = new float[topo.vertices.Length];
        _grad = new Vector3[topo.vertices.Length];
    }

    /// <summary>
    /// The field toward a goal on this surface. goalKey: anything stable per goal (an instance id);
    /// goalNode: the triangle it's on if known (a crawler's), else found near goalWorld. Null if the
    /// surface has no graph or the goal isn't on it.
    /// </summary>
    public static SurfaceField For(Surface surface, int goalKey, Vector3 goalWorld, TriangleMeshNode goalNode)
    {
        NavMeshGraph graph = surface ? surface.Graph : null;
        if (graph == null) return null;
        Sweep();

        Topology topo = TopologyOf(graph);
        var key = (surface, goalKey);
        if (!Fields.TryGetValue(key, out SurfaceField field) || field._topo != topo)
            Fields[key] = field = new SurfaceField(surface, topo);

        field._used = Time.time;
        if (Time.time - field._built >= RebuildInterval) field.Retarget(goalWorld, goalNode);
        return field._goalTri >= 0 ? field : null;
    }

    static void Sweep()
    {
        if (Time.time - s_swept < 1f) return;
        s_swept = Time.time;
        foreach (var pair in Fields)
            if (!pair.Key.Item1 || Time.time - pair.Value._used > ForgetAfter) Stale.Add(pair.Key);
        foreach (var key in Stale) Fields.Remove(key);
        Stale.Clear();
    }

    void Retarget(Vector3 goalWorld, TriangleMeshNode node)
    {
        _built = Time.time;
        Vector3 goal = _surface.ToGraph(goalWorld);
        if (node == null || !_topo.index.ContainsKey(node))
        {
            if (AstarPath.active == null) return;
            s_nn ??= NNConstraint.Default;
            s_nn.graphMask = _surface.Mask;
            node = AstarPath.active.GetNearest(goal, s_nn).node as TriangleMeshNode;
        }
        if (node == null || !_topo.index.TryGetValue(node, out int tri) || tri == _goalTri) return; // same triangle: still good
        _goalTri = tri;
        Flood(tri, goal);
    }

    void Flood(int goalTri, Vector3 goal)
    {
        Vector3[] p = _topo.vertices;
        for (int i = 0; i < _dist.Length; i++) _dist[i] = float.MaxValue;

        Heap.Clear();
        for (int k = 0; k < 3; k++)
        {
            int v = _topo.tris[goalTri * 3 + k];
            float d = Vector3.Distance(p[v], goal);
            if (d < _dist[v]) { _dist[v] = d; Heap.Push(d, v); }
        }
        while (Heap.Pop(out float d, out int v))
        {
            if (d > _dist[v]) continue; // stale entry
            for (int e = _topo.edgeStart[v]; e < _topo.edgeStart[v + 1]; e++)
            {
                int w = _topo.edgeTo[e];
                float nd = d + _topo.edgeLength[e];
                if (nd < _dist[w]) { _dist[w] = nd; Heap.Push(nd, w); }
            }
        }

        // Gradient of the linear interpolant over each triangle, times its area, onto its corners.
        System.Array.Clear(_grad, 0, _grad.Length);
        int[] t = _topo.tris;
        for (int i = 0; i < t.Length; i += 3)
        {
            int a = t[i], b = t[i + 1], c = t[i + 2];
            float fa = _dist[a], fb = _dist[b], fc = _dist[c];
            if (fa == float.MaxValue || fb == float.MaxValue || fc == float.MaxValue) continue; // another island
            Vector3 n = Vector3.Cross(p[b] - p[a], p[c] - p[a]);
            float twiceArea = n.magnitude;
            if (twiceArea < 1e-8f) continue;
            Vector3 g = (fa * Vector3.Cross(n, p[c] - p[b]) + fb * Vector3.Cross(n, p[a] - p[c]) +
                         fc * Vector3.Cross(n, p[b] - p[a])) / (2f * twiceArea);
            _grad[a] += g; _grad[b] += g; _grad[c] += g;
        }
    }

    /// <summary>
    /// World direction toward the goal along the surface from a crawler's spot (its node and raw graph
    /// position, NavSurface.Node / GraphPosition). Not projected onto the surface: the caller does that.
    /// False near the goal (go straight at it) or where the field is flat.
    /// </summary>
    public bool Direction(TriangleMeshNode node, Vector3 graphPos, out Vector3 world)
    {
        world = Vector3.zero;
        if (node == null || !_topo.index.TryGetValue(node, out int tri) || tri == _goalTri) return false;

        Connection[] around = _topo.nodes[_goalTri].connections;
        if (around != null)
            foreach (Connection c in around)
                if (c.node == node) return false;

        int a = _topo.tris[tri * 3], b = _topo.tris[tri * 3 + 1], c3 = _topo.tris[tri * 3 + 2];
        Vector3 w = Barycentric(graphPos, _topo.vertices[a], _topo.vertices[b], _topo.vertices[c3]);
        w = Vector3.Max(w, Vector3.zero);
        float sum = w.x + w.y + w.z;
        if (sum < 1e-6f) w = Vector3.one / 3f; else w /= sum;

        Vector3 g = w.x * _grad[a] + w.y * _grad[b] + w.z * _grad[c3];
        if (g.sqrMagnitude < 1e-12f) return false;
        world = _surface.ToWorldDir(-g).normalized;
        return true;
    }

    static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float den = d00 * d11 - d01 * d01;
        if (Mathf.Abs(den) < 1e-12f) return new Vector3(1f, 0f, 0f);
        float v = (d11 * d20 - d01 * d21) / den, u = (d00 * d21 - d01 * d20) / den;
        return new Vector3(1f - v - u, v, u);
    }

    // Binary min-heap for the flood, shared (floods run one at a time on the main thread); grows once.
    static class Heap
    {
        static float[] s_keys = new float[256];
        static int[] s_values = new int[256];
        static int s_count;

        public static void Clear() => s_count = 0;

        public static void Push(float key, int value)
        {
            if (s_count == s_keys.Length)
            {
                System.Array.Resize(ref s_keys, s_count * 2);
                System.Array.Resize(ref s_values, s_count * 2);
            }
            int i = s_count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (s_keys[parent] <= key) break;
                s_keys[i] = s_keys[parent]; s_values[i] = s_values[parent];
                i = parent;
            }
            s_keys[i] = key; s_values[i] = value;
        }

        public static bool Pop(out float key, out int value)
        {
            key = 0f; value = 0;
            if (s_count == 0) return false;
            key = s_keys[0]; value = s_values[0];

            float lastKey = s_keys[--s_count];
            int lastValue = s_values[s_count], i = 0;
            while (true)
            {
                int child = i * 2 + 1;
                if (child >= s_count) break;
                if (child + 1 < s_count && s_keys[child + 1] < s_keys[child]) child++;
                if (s_keys[child] >= lastKey) break;
                s_keys[i] = s_keys[child]; s_values[i] = s_values[child];
                i = child;
            }
            s_keys[i] = lastKey; s_values[i] = lastValue;
            return true;
        }
    }
}

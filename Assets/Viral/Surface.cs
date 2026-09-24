using System.Collections.Generic;
using Pathfinding;
using UnityEngine;

/// <summary>
/// Makes a mesh walkable: put it on anything with a MeshRenderer (a cell, a rock) and
/// crawlers can land on it and walk all over it, however it moves, turns or scales.
/// It also takes impact ripples (Surface.Ripples.cs).
///
/// Every Surface with the same mesh shares one A* navmesh graph, built the first time
/// that mesh is needed. The graph is baked in the mesh's own local space, and queries go
/// through the renderer's Transform, so a copy at a different scale, position or rotation
/// needs no graph of its own: it just rescales. There is nothing to bake or sync by hand,
/// and the Pathfinder (AstarPath) is created if the scene has none.
///
/// Any shape works. Each vertex gets a normal per smoothing group (edges sharper than
/// creaseAngle split groups) and a curvature per direction, so crawlers ride an arc fitted
/// at every corner: round on a sphere, straight along a cylinder and round across it, flat on
/// a cube's faces, continuous on anything.
///
/// The mesh must have Read/Write enabled in builds (the graph reads its vertices).
/// </summary>
[DisallowMultipleComponent]
public partial class Surface : MonoBehaviour
{
    [Tooltip("The walkable mesh. Empty = this object's own MeshRenderer.")]
    public MeshRenderer meshRenderer;
    [Range(0f, 180f), Tooltip("Edges sharper than this stay hard (cube); gentler ones are smoothed over (sphere). " +
                              "Shared by every Surface with this mesh: the first one to build it decides.")]
    public float creaseAngle = 60f;
    [Tooltip("A cell: raises immune signals when walked on, and is a command-mode \"Cell\" target. Off for other " +
             "walkable things (resource chunks).")]
    public bool isCell = true;

    // Graphs are baked scaled up to this size (world units across the mesh's longest
    // half-axis) because A* stores vertices in millimetres: a cell mesh of radius 0.5
    // would otherwise be only 500 steps across.
    const float GraphSize = 100f;

    NavMeshGraph _graph;
    Mesh _mesh;
    float _scale = 1f; // mesh space -> graph space

    public MeshRenderer Renderer => meshRenderer ? meshRenderer : meshRenderer = GetComponent<MeshRenderer>();

    /// <summary>The Transform the surface moves with (the renderer's).</summary>
    public Transform Space => Renderer ? Renderer.transform : transform;

    public Mesh Mesh
    {
        get
        {
            if (!_mesh && Renderer && Renderer.TryGetComponent(out MeshFilter filter)) _mesh = filter.sharedMesh;
            return _mesh;
        }
    }

    /// <summary>This mesh's graph, built on first use. Null without a mesh.</summary>
    public NavMeshGraph Graph
    {
        get
        {
            if (Live(_graph)) return _graph;
            Mesh mesh = Mesh;
            if (!mesh) return null;
            _scale = GraphSize / Mathf.Max(1e-5f, MaxComponent(mesh.bounds.extents));
            return _graph = GraphFor(mesh, _scale);
        }
    }

    public GraphMask Mask => Graph != null ? GraphMask.FromGraph(_graph) : GraphMask.everything;

    // World <-> graph. Directions carry rotation only, like Transform.TransformDirection;
    // normals also undo the scale, so they stay perpendicular on a stretched surface.
    public Vector3 ToGraph(Vector3 world) => Space.InverseTransformPoint(world) * Scale;
    public Vector3 ToWorld(Vector3 graph) => Space.TransformPoint(graph / Scale);
    public Vector3 ToGraphDir(Vector3 world) => Space.InverseTransformDirection(world);
    public Vector3 ToWorldDir(Vector3 graph) => Space.TransformDirection(graph);
    public Vector3 ToWorldNormal(Vector3 graph)
    {
        Vector3 s = Space.lossyScale;
        return Space.TransformDirection(new Vector3(graph.x / s.x, graph.y / s.y, graph.z / s.z)).normalized;
    }

    /// <summary>
    /// The surface at one triangle corner, in graph space: its smoothed normal (on the side
    /// the mesh's winding faces, Cross(b - a, c - a)) and how it bends away from there in each
    /// direction. Curvature is 1 / radius, + convex, 0 flat.
    /// </summary>
    public struct Corner
    {
        public Vector3 normal, u; // u: a tangent; v = Cross(normal, u)
        public float kuu, kuv, kvv; // curvature along d = x u + y v: kuu x^2 + 2 kuv x y + kvv y^2

        public float Curvature(Vector3 direction)
        {
            float x = Vector3.Dot(direction, u), y = Vector3.Dot(direction, Vector3.Cross(normal, u));
            return kuu * x * x + 2f * kuv * x * y + kvv * y * y;
        }

        public static Corner Flat(Vector3 n) => new Corner { normal = n, u = Perpendicular(n) };
    }

    /// <summary>A triangle's three corners. False for a node not in this graph.</summary>
    public bool Corners(TriangleMeshNode node, out Corner a, out Corner b, out Corner c)
    {
        a = b = c = default;
        var map = CornerMap();
        if (map == null || node == null || !map.TryGetValue(node, out var corners)) return false;
        a = corners[0]; b = corners[1]; c = corners[2];
        return true;
    }

    // This graph's corners, built once per graph (GraphFor drops them when it rescans).
    Dictionary<GraphNode, Corner[]> CornerMap()
    {
        NavMeshGraph graph = Graph;
        if (graph == null) return null;
        if (!CornerData.TryGetValue(graph, out var map)) CornerData[graph] = map = BuildCorners(graph, creaseAngle);
        return map;
    }

    float Scale => Graph != null ? _scale : 1f; // makes sure the graph (and its scale) exist

    void Awake() => BakeDisplacement();

    // Built after every Awake, so a scene Pathfinder has loaded its own graphs by then. The
    // corners too: built on first use they'd land (as a hitch) on the first touchdown.
    void Start() => CornerMap();

    // ---------------- corner normals and curvature ----------------

    // Per graph, per triangle: its three corners. Rebuilt after a script reload.
    static readonly Dictionary<NavMeshGraph, Dictionary<GraphNode, Corner[]>> CornerData =
        new Dictionary<NavMeshGraph, Dictionary<GraphNode, Corner[]>>();

    // Smoothing groups: around each vertex, faces joined through shared edges gentler than
    // the crease angle form a group, and every corner in a group gets the group's normal
    // (area-weighted) and curvature. One value per vertex per group keeps things continuous
    // across triangles (a sphere); a hard edge splits the groups (a cube). Relies on the
    // mesh's winding being consistent, which the graph keeps because recalculateNormals is off.
    static Dictionary<GraphNode, Corner[]> BuildCorners(NavMeshGraph graph, float crease)
    {
        var nodes = new List<TriangleMeshNode>();
        var normals = new List<Vector3>(); // length = 2 x area, the weight
        var around = new Dictionary<Int3, List<int>>();
        graph.GetNodes(n =>
        {
            if (!(n is TriangleMeshNode tri)) return;
            Vector3 a = (Vector3)tri.GetVertex(0), b = (Vector3)tri.GetVertex(1), c = (Vector3)tri.GetVertex(2);
            int index = nodes.Count;
            nodes.Add(tri);
            normals.Add(Vector3.Cross(b - a, c - a));
            for (int i = 0; i < 3; i++)
            {
                Int3 v = tri.GetVertex(i);
                if (!around.TryGetValue(v, out var list)) around[v] = list = new List<int>();
                list.Add(index);
            }
        });

        var map = new Dictionary<GraphNode, Corner[]>(nodes.Count);
        for (int f = 0; f < nodes.Count; f++)
        {
            Vector3 own = normals[f].sqrMagnitude > 1e-12f ? normals[f].normalized : Vector3.up;
            Corner flat = Corner.Flat(own); // flat until a group says otherwise
            map[nodes[f]] = new[] { flat, flat, flat };
        }

        float minCos = Mathf.Cos(crease * Mathf.Deg2Rad);
        var parent = new List<int>();
        var sums = new Dictionary<int, Vector3>();
        foreach (var pair in around)
        {
            Int3 v = pair.Key;
            List<int> fan = pair.Value;
            parent.Clear();
            for (int i = 0; i < fan.Count; i++) parent.Add(i);

            for (int i = 0; i < fan.Count; i++)
            for (int j = i + 1; j < fan.Count; j++)
            {
                if (!SharesEdge(nodes[fan[i]], nodes[fan[j]], v)) continue;
                Vector3 ni = normals[fan[i]], nj = normals[fan[j]];
                if (ni.sqrMagnitude < 1e-12f || nj.sqrMagnitude < 1e-12f) continue;
                if (Vector3.Dot(ni.normalized, nj.normalized) >= minCos) parent[Root(parent, i)] = Root(parent, j);
            }

            sums.Clear();
            for (int i = 0; i < fan.Count; i++)
            {
                int root = Root(parent, i);
                sums[root] = (sums.TryGetValue(root, out Vector3 sum) ? sum : Vector3.zero) + normals[fan[i]];
            }

            foreach (var group in sums)
            {
                if (group.Value.sqrMagnitude < 1e-12f) continue;
                Corner corner = FitCorner(v, group.Value.normalized, fan, parent, group.Key, nodes);
                for (int i = 0; i < fan.Count; i++)
                {
                    if (Root(parent, i) != group.Key) continue;
                    TriangleMeshNode node = nodes[fan[i]];
                    for (int c = 0; c < 3; c++)
                        if (node.GetVertex(c) == v) map[node][c] = corner;
                }
            }
        }
        return map;
    }

    // How the surface bends away from the vertex's tangent plane, per direction, from the
    // group's neighbouring vertices: a point on a circle of radius R through the vertex sits
    // dot(e, n) = -|e|^2 / 2R below the plane. Each neighbour gives the curvature toward it;
    // a least-squares fit of kuu x^2 + 2 kuv x y + kvv y^2 to those gives every direction
    // (straight along a cylinder, round across it), pulled gently toward the average so a
    // vertex with few neighbours stays sensible. Capped so a bad fit can't throw crawlers far.
    static Corner FitCorner(Int3 v, Vector3 n, List<int> fan, List<int> parent, int group, List<TriangleMeshNode> nodes)
    {
        Corner corner = Corner.Flat(n);
        Vector3 u = corner.u, w = Vector3.Cross(n, u), p = (Vector3)v;

        // Normal equations for (kuu, kuv, kvv) with rows (x^2, 2xy, y^2).
        float m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, r0 = 0, r1 = 0, r2 = 0;
        float sum = 0f, edges = 0f;
        int count = 0;
        for (int i = 0; i < fan.Count; i++)
        {
            if (Root(parent, i) != group) continue;
            TriangleMeshNode node = nodes[fan[i]];
            for (int c = 0; c < 3; c++)
            {
                Int3 other = node.GetVertex(c);
                if (other == v) continue;
                Vector3 e = (Vector3)other - p;
                Vector3 t = Vector3.ProjectOnPlane(e, n);
                float lengthSq = e.sqrMagnitude;
                if (lengthSq < 1e-8f || t.sqrMagnitude < 1e-10f) continue;

                float k = -2f * Vector3.Dot(e, n) / lengthSq;
                t.Normalize();
                float x = Vector3.Dot(t, u), y = Vector3.Dot(t, w);
                float a0 = x * x, a1 = 2f * x * y, a2 = y * y;
                m00 += a0 * a0; m01 += a0 * a1; m02 += a0 * a2;
                m11 += a1 * a1; m12 += a1 * a2; m22 += a2 * a2;
                r0 += a0 * k; r1 += a1 * k; r2 += a2 * k;
                sum += k; edges += Mathf.Sqrt(lengthSq); count++;
            }
        }
        if (count == 0) return corner;

        float mean = sum / count;
        float lambda = 0.05f * count;
        m00 += lambda; m11 += lambda; m22 += lambda;
        r0 += lambda * mean; r2 += lambda * mean;

        // Solve the symmetric 3x3 by Cramer's rule.
        float det = m00 * (m11 * m22 - m12 * m12) - m01 * (m01 * m22 - m12 * m02) + m02 * (m01 * m12 - m11 * m02);
        float limit = 2f / (edges / count); // radius no smaller than half an edge
        if (Mathf.Abs(det) < 1e-9f)
        {
            corner.kuu = corner.kvv = Mathf.Clamp(mean, -limit, limit);
            return corner;
        }
        float kuu = (r0 * (m11 * m22 - m12 * m12) - m01 * (r1 * m22 - m12 * r2) + m02 * (r1 * m12 - m11 * r2)) / det;
        float kuv = (m00 * (r1 * m22 - m12 * r2) - r0 * (m01 * m22 - m12 * m02) + m02 * (m01 * r2 - r1 * m02)) / det;
        float kvv = (m00 * (m11 * r2 - r1 * m12) - m01 * (m01 * r2 - r1 * m02) + r0 * (m01 * m12 - m11 * m02)) / det;

        corner.kuu = Mathf.Clamp(kuu, -limit, limit);
        corner.kuv = Mathf.Clamp(kuv, -limit, limit);
        corner.kvv = Mathf.Clamp(kvv, -limit, limit);
        return corner;
    }

    static Vector3 Perpendicular(Vector3 n) =>
        Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

    static int Root(List<int> parent, int i)
    {
        while (parent[i] != i) i = parent[i] = parent[parent[i]];
        return i;
    }

    // Two triangles around vertex v share an edge if they also share one other vertex.
    static bool SharesEdge(TriangleMeshNode a, TriangleMeshNode b, Int3 v)
    {
        for (int i = 0; i < 3; i++)
        {
            Int3 x = a.GetVertex(i);
            if (x == v) continue;
            for (int j = 0; j < 3; j++)
                if (b.GetVertex(j) == x) return true;
        }
        return false;
    }

    // ---------------- displacement direction ----------------

    // Shaders that push vertices out along the normal (noise, ripples) tear a mesh open
    // wherever one position has several normals: a cube's edges. So every mesh gets, in
    // UV channel 3, one direction per position, the angle-weighted average of every face
    // there. Shaders displace along it (BloodCellTriplanar does). One copy per source mesh.
    public const int DisplacementChannel = 3;
    static readonly Dictionary<Mesh, Mesh> Baked = new Dictionary<Mesh, Mesh>();

    void BakeDisplacement()
    {
        if (!Renderer || !Renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) return;
        Mesh source = filter.sharedMesh;
        if (!Baked.TryGetValue(source, out Mesh baked) || !baked)
        {
            if (!source.isReadable) return; // can't read it in a build: displace along normals as before
            baked = Instantiate(source);
            baked.name = source.name;
            baked.SetUVs(DisplacementChannel, DisplacementDirections(source));
            Baked[source] = baked;
            Baked[baked] = baked; // a filter already holding the copy
        }
        filter.sharedMesh = baked;
        _mesh = baked;
    }

    static List<Vector3> DisplacementDirections(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        var sums = new Dictionary<Vector3Int, Vector3>();
        Vector3Int Key(Vector3 p) => Vector3Int.RoundToInt(p * 1e4f);

        for (int t = 0; t < triangles.Length; t += 3)
        {
            Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-16f) continue;
            n.Normalize();
            // Angle-weighted: a cube corner comes out on the diagonal however it's triangulated.
            Add(a, Vector3.Angle(b - a, c - a));
            Add(b, Vector3.Angle(c - b, a - b));
            Add(c, Vector3.Angle(a - c, b - c));
            void Add(Vector3 p, float angle)
            {
                Vector3Int k = Key(p);
                sums[k] = (sums.TryGetValue(k, out Vector3 s) ? s : Vector3.zero) + n * angle;
            }
        }

        Vector3[] meshNormals = mesh.normals;
        var directions = new List<Vector3>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 d = sums.TryGetValue(Key(vertices[i]), out Vector3 s) && s.sqrMagnitude > 1e-12f
                ? s.normalized
                : i < meshNormals.Length ? meshNormals[i] : Vector3.up;
            directions.Add(d);
        }
        return directions;
    }

    // ---------------- graphs ----------------

    static bool Live(NavMeshGraph graph)
    {
        NavGraph[] graphs = AstarPath.active != null ? AstarPath.active.data.graphs : null;
        return graph != null && graphs != null && graph.graphIndex < graphs.Length && graphs[graph.graphIndex] == graph;
    }

    // The shared graph for a mesh: found if any Surface has built it, else added and scanned.
    static NavMeshGraph GraphFor(Mesh mesh, float scale)
    {
        AstarPath astar = Pathfinder();
        NavGraph[] graphs = astar.data.graphs;
        if (graphs != null)
            foreach (NavGraph g in graphs)
                if (g is NavMeshGraph n && n.sourceMesh == mesh && Mathf.Approximately(n.scale, scale)
                    && n.offset == Vector3.zero && n.rotation == Vector3.zero && !n.recalculateNormals)
                    return n;

        if (!mesh.isReadable)
            Debug.LogWarning($"Surface: mesh '{mesh.name}' needs Read/Write enabled to be walkable in a build.", mesh);

        var graph = (NavMeshGraph)astar.data.AddGraph(typeof(NavMeshGraph));
        graph.name = mesh.name;
        graph.sourceMesh = mesh;
        graph.scale = scale;
        graph.recalculateNormals = false; // it rewinds triangles to face up: wrong on a closed 3D shape
        astar.Scan(graph);
        CornerData.Remove(graph);
        SurfaceField.Forget(graph);
        return graph;
    }

    static AstarPath Pathfinder()
    {
        if (AstarPath.active != null) return AstarPath.active;
        AstarPath existing = FindAnyObjectByType<AstarPath>();
        if (existing) return existing;
        var astar = new GameObject("Pathfinder").AddComponent<AstarPath>();
        astar.scanOnStartup = false;
        return astar;
    }

    static float MaxComponent(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));
}

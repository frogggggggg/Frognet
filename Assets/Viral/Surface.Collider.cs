using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A dynamic body can only have convex MeshColliders, and a convex hull fills in every dent: a red blood cell's
/// dimples became a lid over the middle. Legs (which raycast colliders) stood on the lid, above the body, and the
/// body (which follows the walk mesh down into the dimple) sank into it and shoved its own cell every frame.
///
/// So a Surface whose MeshCollider is convex but whose mesh isn't gets that collider replaced by convex pieces that
/// follow the mesh: the solid cut into boxes, each halved along its longest side until the solid inside it is convex
/// within colliderTolerance (BuildPieces). Built once per mesh and shared (the cooked data too); a convex mesh keeps
/// its hull. Cost: the build is ~O(faces x points) per box, once per mesh; per cell, one convex shape per piece
/// (at most colliderPieces).
///
/// Collider LOD: the pieces are only needed where something walks, lands, or raycasts legs. A cell farther than
/// detailDistance (past its own size) from every Organism keeps its one hull (one shape for physics instead of
/// ~48, and spawning it makes no piece objects); the pieces are made the first time a creature comes near and
/// switched in. A surface something stands on is always detailed (Ground asks, UseDetailedCollider). Checked
/// round-robin, a fifteenth of the split surfaces a frame against every organism: O(surfaces x organisms / 15)
/// per frame (give it a grid if AI counts reach the thousands).
/// </summary>
public partial class Surface
{
    [Header("Collider")]
    [Tooltip("Replace a convex MeshCollider over a concave mesh (it fills in dents: a red blood cell's dimples) with " +
             "convex pieces that follow the mesh. A convex mesh keeps its hull.")]
    public bool splitConcaveCollider = true;
    [Range(0.002f, 0.1f), Tooltip("How far a piece may bridge over a dent, as a share of the mesh's size. " +
                                  "Smaller = closer fit, more pieces. Shared per mesh: the first Surface to build it decides.")]
    public float colliderTolerance = 0.02f;
    [Range(2, 128), Tooltip("Most convex pieces the collider is cut into (physics cost per cell grows with it). Worst dents are "+
                            "split first, so at the cap what is left bridges least.")]
    public int colliderPieces = 48;
    [Min(0f), Tooltip("Collider LOD: past this (metres, beyond the surface's own radius) from every creature the body uses " +
                      "its one convex hull instead of the pieces.")]
    public float detailDistance = 50f;

    Collider[] _colliders;
    MeshCollider _hull;       // the convex hull the pieces replace near creatures
    Mesh[] _pieceMeshes;      // shared per mesh; their colliders are made on first need
    Collider[] _pieces;
    bool _detailed;
    int _lodIndex = -1;

    static readonly List<Surface> s_split = new List<Surface>();
    static readonly List<Vector3> s_organisms = new List<Vector3>();
    static int s_lodFrame = -1, s_lodCursor;

    /// <summary>Whether the body uses its convex pieces (near creatures) rather than its one hull.</summary>
    public bool DetailedCollider => _pieceMeshes == null || _detailed;

    /// <summary>The (solid) colliders this surface's body is made of: its pieces, or the colliders it came with.</summary>
    public Collider[] Colliders => _colliders ??= OwnColliders();

    /// <summary>Nearest point on this surface's colliders (any shape, pieces included).</summary>
    public Vector3 ClosestPoint(Vector3 point)
    {
        Vector3 best = point;
        float bestSq = float.PositiveInfinity;
        foreach (Collider c in Colliders)
        {
            if (!c || !c.enabled) continue;
            Vector3 p = c is MeshCollider m && !m.convex ? c.bounds.ClosestPoint(point) : c.ClosestPoint(point);
            float sq = (p - point).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = p; }
        }
        return best;
    }

    Collider[] OwnColliders()
    {
        var list = new List<Collider>();
        foreach (Collider c in GetComponents<Collider>()) if (!c.isTrigger) list.Add(c);
        if (Space != transform)
            foreach (Collider c in Space.GetComponents<Collider>()) if (!c.isTrigger && !list.Contains(c)) list.Add(c);
        return list.ToArray();
    }

    void SplitCollider()
    {
        if (!splitConcaveCollider) return;
        MeshCollider source = null;
        foreach (Collider c in Colliders)
            if (c is MeshCollider m && m.convex && m.sharedMesh) { source = m; break; }
        if (!source || !source.sharedMesh.isReadable) return;

        Mesh[] pieces = Pieces(source.sharedMesh, colliderTolerance, colliderPieces, source.cookingOptions);
        if (pieces == null) return; // convex (within tolerance): the hull is right

        _hull = source;
        _pieceMeshes = pieces;
        _detailed = false;
        _lodIndex = s_split.Count;
        s_split.Add(this);
    }

    void OnDestroy()
    {
        if (!ReferenceEquals(_listedAs, null)) BySpace.Remove(_listedAs); // Surface.Of's cache
        // After a script reload the static list is empty but instances keep their index (1300 exceptions on exit).
        if (_lodIndex < 0 || _lodIndex >= s_split.Count || s_split[_lodIndex] != this) { _lodIndex = -1; return; }
        Surface last = s_split[s_split.Count - 1];
        s_split[_lodIndex] = last;
        last._lodIndex = _lodIndex;
        s_split.RemoveAt(s_split.Count - 1);
        _lodIndex = -1;
    }

    /// <summary>Pieces (true) or the one hull (false). Pieces are made the first time they're asked for.</summary>
    public void UseDetailedCollider(bool on)
    {
        if (_pieceMeshes == null || !_hull || on == _detailed) return;
        if (on && _pieces == null) MakePieces();
        _detailed = on;
        foreach (Collider c in _pieces) if (c) c.enabled = on;
        _hull.enabled = !on;
    }

    void MakePieces()
    {
        var made = new List<Collider>(_pieceMeshes.Length + 1);
        foreach (Collider c in Colliders) made.Add(c); // the hull stays (switched off near creatures)
        _pieces = new Collider[_pieceMeshes.Length];
        for (int i = 0; i < _pieceMeshes.Length; i++)
        {
            var go = new GameObject("Collider Piece " + i) { layer = _hull.gameObject.layer };
            go.transform.SetParent(_hull.transform, false);
            var mc = go.AddComponent<MeshCollider>();
            mc.enabled = false; // switched on by UseDetailedCollider
            mc.cookingOptions = _hull.cookingOptions;
            mc.convex = true; // before the mesh, so it's cooked once, as a hull
            mc.sharedMesh = _pieceMeshes[i];
            mc.sharedMaterial = _hull.sharedMaterial;
            mc.includeLayers = _hull.includeLayers;
            mc.excludeLayers = _hull.excludeLayers;
            mc.layerOverridePriority = _hull.layerOverridePriority;
            _pieces[i] = mc;
            made.Add(mc);
        }
        _colliders = made.ToArray();
    }

    // Round-robin collider LOD (once a frame, from the first Surface to update).
    static void StepColliderLod()
    {
        if (s_lodFrame == Time.frameCount) return;
        s_lodFrame = Time.frameCount;
        if (s_split.Count == 0) return;

        s_organisms.Clear();
        foreach (Organism o in Organism.All) if (o) s_organisms.Add(o.transform.position);

        int count = (s_split.Count + 14) / 15;
        for (int n = 0; n < count; n++)
        {
            if (s_lodCursor >= s_split.Count) s_lodCursor = 0;
            Surface s = s_split[s_lodCursor++];
            if (!s || !s._hull) continue;
            // From the mesh, not the collider: a disabled collider's bounds are empty.
            Transform t = s._hull.transform;
            Bounds b = s._hull.sharedMesh.bounds;
            Vector3 scale = t.lossyScale;
            Vector3 centre = t.TransformPoint(b.center);
            float reach = b.extents.magnitude * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)) + s.detailDistance;
            reach *= reach;
            bool near = false;
            foreach (Vector3 p in s_organisms)
                if ((p - centre).sqrMagnitude < reach) { near = true; break; }
            s.UseDetailedCollider(near);
        }
    }

    // Per (mesh, tolerance): its pieces, or null when the mesh is convex enough for one hull.
    static readonly Dictionary<(Mesh, float, int), Mesh[]> PieceCache = new Dictionary<(Mesh, float, int), Mesh[]>();

    static Mesh[] Pieces(Mesh mesh, float tolerance, int max, MeshColliderCookingOptions cooking)
    {
        var key = (mesh, tolerance, max);
        if (PieceCache.TryGetValue(key, out Mesh[] pieces) && (pieces == null || pieces.Length == 0 || pieces[0])) return pieces;
        pieces = BuildPieces(mesh, tolerance, max);
        if (pieces != null)
        {
            foreach (Mesh p in pieces) Physics.BakeMesh(p.GetInstanceID(), true, cooking); // cooked once, shared by every copy
            Debug.Log($"Surface: '{mesh.name}' is concave; its collider is {pieces.Length} convex pieces.");
        }
        PieceCache[key] = pieces;
        return pieces;
    }

    const int MaxDepth = 10;

    // Split the mesh's box in half along its longest side, again and again, until the solid inside each box
    // is convex within the tolerance: every point of it behind the plane of every mesh face in the box. A
    // piece is the solid inside its box (the mesh clipped to it, plus the box's corners and edge crossings
    // inside the solid), so pieces are volumetric (one column holds both dimples of a cell's middle), meet
    // face to face without overlapping, and only split where the shape is concave. Needs a closed mesh (inside
    // is told by ray parity); an open one keeps its hull.
    static Mesh[] BuildPieces(Mesh mesh, float tolerance, int max)
    {
        List<Vector3[]> pieces = Decompose(mesh.vertices, mesh.triangles, mesh.bounds, tolerance, max);
        if (pieces == null) return null;
        var result = new Mesh[pieces.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            Vector3[] p = pieces[i];
            var triangles = new int[(p.Length - 2) * 3]; // a fan: only the points matter, the collider is their hull
            for (int t = 0; t < p.Length - 2; t++) { triangles[3 * t] = 0; triangles[3 * t + 1] = t + 1; triangles[3 * t + 2] = t + 2; }
            var m = new Mesh { name = $"{mesh.name} piece {i}" };
            m.SetVertices(p);
            m.SetTriangles(triangles, 0);
            m.RecalculateBounds();
            result[i] = m;
        }
        return result;
    }

    // The pieces' points (each piece is their hull); null = convex within tolerance, or not closed.
    static List<Vector3[]> Decompose(Vector3[] vertices, int[] tris, Bounds bounds, float tolerance, int max)
    {
        float size = Mathf.Max(1e-6f, MaxComponent(bounds.extents));
        var solid = new Solid(vertices, tris, size);
        if (!solid.Closed) return null;
        float tol = tolerance * size;

        // Worst box first: at the piece cap, what's left unsplit is what bridges least.
        var open = new List<Box>();
        var points = new List<Vector3>();
        var faces = new List<int>();
        Vector3 pad = Vector3.one * (1e-4f * size);
        Box root = Measure(solid, bounds.min - pad, bounds.max + pad, 0, points, faces);
        if (root.points == null || root.gap <= tol) return null; // convex: one hull is right
        open.Add(root);
        while (open.Count < max)
        {
            int worst = -1;
            for (int i = 0; i < open.Count; i++)
                if (open[i].gap > tol && open[i].depth < MaxDepth && (worst < 0 || open[i].gap > open[worst].gap)) worst = i;
            if (worst < 0) break; // all fit
            Box b = open[worst];
            open.RemoveAt(worst);
            Vector3 extent = b.hi - b.lo;
            int axis = extent.x >= extent.y && extent.x >= extent.z ? 0 : extent.y >= extent.z ? 1 : 2;
            float mid = 0.5f * (b.lo[axis] + b.hi[axis]);
            Vector3 hi1 = b.hi, lo2 = b.lo;
            hi1[axis] = mid; lo2[axis] = mid;
            Box one = Measure(solid, b.lo, hi1, b.depth + 1, points, faces), two = Measure(solid, lo2, b.hi, b.depth + 1, points, faces);
            if (one.points != null) open.Add(one);
            if (two.points != null) open.Add(two);
        }

        var pieces = new List<Vector3[]>(open.Count);
        foreach (Box b in open)
            if (!Flat(b.points, 1e-3f * size)) pieces.Add(b.points);
        return pieces;
    }

    struct Box { public Vector3 lo, hi; public int depth; public float gap; public Vector3[] points; }

    // The solid in a box and how far its hull bridges over its surface. No points (empty box): points null.
    static Box Measure(Solid solid, Vector3 lo, Vector3 hi, int depth, List<Vector3> points, List<int> faces)
    {
        int surface = solid.Clip(lo, hi, points, faces);
        var box = new Box { lo = lo, hi = hi, depth = depth };
        if (points.Count < 4) return box;
        box.points = points.ToArray();
        box.gap = Hull.Deepest(points, solid.Normals, surface, float.MaxValue);
        return box;
    }

    // All points (nearly) in one plane: no volume, the hull can't be cooked.
    static bool Flat(IList<Vector3> p, float eps)
    {
        Vector3 a = p[0], b = a, c = a;
        float best = 0f;
        foreach (Vector3 x in p) { float d = (x - a).sqrMagnitude; if (d > best) { best = d; b = x; } }
        if (best < eps * eps) return true;
        best = 0f;
        foreach (Vector3 x in p) { float d = Vector3.Cross(b - a, x - a).sqrMagnitude; if (d > best) { best = d; c = x; } }
        Vector3 n = Vector3.Cross(b - a, c - a);
        if (n.sqrMagnitude < 1e-20f) return true;
        n.Normalize();
        foreach (Vector3 x in p) if (Mathf.Abs(Vector3.Dot(n, x - a)) > eps) return false;
        return true;
    }

    // How deep a piece's surface sits under the hull of the piece: the gap a collider made of the hull would
    // bridge over a dent, measured out along each point's surface normal n (what stands there is lifted by it).
    // Through the support function, no hull built (an incremental hull broke down on the many coplanar points
    // of a box's cut faces): the ray p + s n stays in the hull while u.(p + s n) <= max over the points of u.q
    // for every direction u, so it leaves at s = min over u of (h(u) - u.p) / (u.n). Taken over the surface
    // normals in the box (the lid over a dent faces the way the surface does somewhere in it) and the box's
    // axes (its cut walls). Over a subset of directions it can only over-measure: more splitting, never a lid.
    // O(points x directions) per box.
    static class Hull
    {
        static readonly List<Vector3> Dirs = new List<Vector3>();
        static readonly List<float> Heights = new List<float>();
        static readonly HashSet<Vector3Int> Seen = new HashSet<Vector3Int>();

        /// <summary>Deepest of the first 'count' points (with their normals) under the hull of all the points.
        /// Stops early past 'limit'.</summary>
        public static float Deepest(List<Vector3> p, List<Vector3> normals, int count, float limit)
        {
            Dirs.Clear(); Heights.Clear(); Seen.Clear();
            for (int a = 0; a < 3; a++)
            {
                Vector3 axis = Vector3.zero;
                axis[a] = 1f;
                AddDir(axis); AddDir(-axis);
            }
            for (int i = 0; i < count; i++) AddDir(normals[i]);
            foreach (Vector3 u in Dirs)
            {
                float h = float.MinValue;
                foreach (Vector3 q in p) h = Mathf.Max(h, Vector3.Dot(u, q));
                Heights.Add(h);
            }

            float worst = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 x = p[i], n = normals[i];
                float gap = float.MaxValue;
                for (int d = 0; d < Dirs.Count; d++)
                {
                    float facing = Vector3.Dot(Dirs[d], n);
                    if (facing > 1e-3f) gap = Mathf.Min(gap, (Heights[d] - Vector3.Dot(Dirs[d], x)) / facing);
                }
                if (gap < float.MaxValue && gap > worst && (worst = gap) > limit) break;
            }
            return worst;
        }

        // One direction per ~0.5 degree cell: a face's many clipped points share its normal.
        static void AddDir(Vector3 u)
        {
            if (Seen.Add(Vector3Int.RoundToInt(u * 100f))) Dirs.Add(u);
        }
    }

    // The mesh as a solid: its triangles, their planes, and clipping it to a box.
    class Solid
    {
        readonly Vector3[] _a, _b, _c, _n, _min, _max;
        readonly float[] _d;
        readonly Dictionary<Vector3, bool> _inside = new Dictionary<Vector3, bool>();
        readonly List<Vector3> _poly = new List<Vector3>(12), _clip = new List<Vector3>(12);
        public readonly bool Closed = true;

        public Solid(Vector3[] v, int[] tris, float size)
        {
            int f = tris.Length / 3;
            _a = new Vector3[f]; _b = new Vector3[f]; _c = new Vector3[f]; _n = new Vector3[f];
            _min = new Vector3[f]; _max = new Vector3[f]; _d = new float[f];
            for (int i = 0; i < f; i++)
            {
                Vector3 a = _a[i] = v[tris[3 * i]], b = _b[i] = v[tris[3 * i + 1]], c = _c[i] = v[tris[3 * i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                _n[i] = n.sqrMagnitude > 1e-24f ? n.normalized : Vector3.zero;
                _d[i] = Vector3.Dot(_n[i], a);
                _min[i] = Vector3.Min(a, Vector3.Min(b, c));
                _max[i] = Vector3.Max(a, Vector3.Max(b, c));
            }

            // Closed: every edge (by welded position) is used an even number of times.
            var count = new Dictionary<(Vector3Int, Vector3Int), int>();
            float q = 1e4f / size;
            for (int i = 0; i < tris.Length; i += 3)
                for (int e = 0; e < 3; e++)
                {
                    Vector3Int x = Vector3Int.RoundToInt(v[tris[i + e]] * q), y = Vector3Int.RoundToInt(v[tris[i + (e + 1) % 3]] * q);
                    if (x == y) continue;
                    var key = Less(x, y) ? (x, y) : (y, x);
                    count[key] = (count.TryGetValue(key, out int k) ? k : 0) + 1;
                }
            foreach (int k in count.Values) if ((k & 1) != 0) { Closed = false; break; }
            _weld = q;
        }

        static bool Less(Vector3Int a, Vector3Int b) => a.x != b.x ? a.x < b.x : a.y != b.y ? a.y < b.y : a.z < b.z;

        readonly HashSet<Vector3Int> _seen = new HashSet<Vector3Int>();
        /// <summary>After Clip: the outward normal at each surface point (the first ones).</summary>
        public readonly List<Vector3> Normals = new List<Vector3>();
        readonly float _weld;

        /// <summary>The solid inside the box, as points (whose hull it is; no duplicates) and the faces bounding
        /// it. Returns how many of the points (the first ones) are on the mesh's surface inside the box.</summary>
        public int Clip(Vector3 lo, Vector3 hi, List<Vector3> points, List<int> faces)
        {
            points.Clear(); faces.Clear(); _seen.Clear(); Normals.Clear();
            for (int i = 0; i < _a.Length; i++)
            {
                if (_n[i] == Vector3.zero) continue;
                Vector3 mn = _min[i], mx = _max[i];
                if (mx.x < lo.x || mn.x > hi.x || mx.y < lo.y || mn.y > hi.y || mx.z < lo.z || mn.z > hi.z) continue;
                _poly.Clear(); _poly.Add(_a[i]); _poly.Add(_b[i]); _poly.Add(_c[i]);
                for (int axis = 0; axis < 3 && _poly.Count > 0; axis++)
                {
                    ClipPlane(axis, hi[axis], true);
                    if (_poly.Count > 0) ClipPlane(axis, lo[axis], false);
                }
                if (_poly.Count < 3) continue;
                foreach (Vector3 p in _poly) if (Add(points, p)) Normals.Add(_n[i]);
                faces.Add(i);
            }
            int surface = points.Count;

            // The box's corners inside the solid, and where its edges cross the surface.
            for (int k = 0; k < 8; k++)
            {
                Vector3 corner = Corner(lo, hi, k);
                if (Inside(corner)) Add(points, corner);
            }
            for (int k = 0; k < 8; k++)
                for (int axis = 0; axis < 3; axis++)
                {
                    if ((k & (1 << axis)) != 0) continue; // each edge once, from its low end
                    Vector3 p = Corner(lo, hi, k), q = Corner(lo, hi, k | (1 << axis));
                    foreach (int i in faces)
                        if (Ray(p, q - p, i, out float t) && t >= 0f && t <= 1f) Add(points, p + (q - p) * t);
                }
            return surface;
        }

        bool Add(List<Vector3> points, Vector3 p)
        {
            if (!_seen.Add(Vector3Int.RoundToInt(p * _weld))) return false;
            points.Add(p);
            return true;
        }

        static Vector3 Corner(Vector3 lo, Vector3 hi, int k) =>
            new Vector3((k & 1) == 0 ? lo.x : hi.x, (k & 2) == 0 ? lo.y : hi.y, (k & 4) == 0 ? lo.z : hi.z);

        void ClipPlane(int axis, float value, bool keepBelow)
        {
            _clip.Clear();
            for (int i = 0; i < _poly.Count; i++)
            {
                Vector3 a = _poly[i], b = _poly[(i + 1) % _poly.Count];
                bool ia = keepBelow ? a[axis] <= value : a[axis] >= value;
                bool ib = keepBelow ? b[axis] <= value : b[axis] >= value;
                if (ia) _clip.Add(a);
                if (ia != ib) _clip.Add(Vector3.Lerp(a, b, (value - a[axis]) / (b[axis] - a[axis])));
            }
            _poly.Clear();
            _poly.AddRange(_clip);
        }

        // Ray parity along a skewed direction (so it doesn't run along edges); shared corners asked once.
        bool Inside(Vector3 p)
        {
            if (_inside.TryGetValue(p, out bool known)) return known;
            Vector3 dir = new Vector3(1f, 0.371f, 0.213f).normalized;
            int hits = 0;
            for (int i = 0; i < _a.Length; i++)
                if (_n[i] != Vector3.zero && Ray(p, dir, i, out float t) && t > 0f) hits++;
            return _inside[p] = (hits & 1) == 1;
        }

        // Möller-Trumbore: t along dir (unnormalised) where it meets triangle i.
        bool Ray(Vector3 o, Vector3 dir, int i, out float t)
        {
            t = 0f;
            Vector3 e1 = _b[i] - _a[i], e2 = _c[i] - _a[i];
            Vector3 h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float f = 1f / det;
            Vector3 s = o - _a[i];
            float u = f * Vector3.Dot(s, h);
            if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(s, e1);
            float v = f * Vector3.Dot(dir, qv);
            if (v < 0f || u + v > 1f) return false;
            t = f * Vector3.Dot(e2, qv);
            return true;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The virus's head as its own object: the crystal shell (the part of the body mesh using
/// Custom/AstrophageCrystalTop) and everything inside it (the ball, whatever mesh part it's in),
/// split off the body mesh at start into a child pivoted on the head's bottom. So it can grow under
/// the cursor (focus mode: click it for the genome) with the bottom staying sat on the body, and
/// everything that draws it (colour, depth, the focus outlines, shadows) follows for free.
/// <see cref="Sphere"/> is the ball, where the head view's tube grows from.
///
/// Split triangle by triangle: any non-crystal triangle whose centre is inside the crystal (its
/// ellipsoid) goes with the head. The body keeps the rest (a per-instance copy of its mesh).
/// Needs the model's Read/Write on.
/// </summary>
public class VirusHead : MonoBehaviour
{
    [Tooltip("The body renderer the head is part of. Empty: the first renderer under this object " +
             "with a Custom/AstrophageCrystalTop material.")]
    public Renderer body;
    [Min(1f), Tooltip("Head size while hovered.")]
    public float hoverScale = 1.3f;
    [Min(0.01f), Tooltip("Seconds to grow to it or back.")]
    public float hoverTime = 0.12f;

    /// <summary>Set every frame by whoever points at the head (VirusMovement).</summary>
    public bool Hovered { get; set; }

    public bool Ready => _head;

    Transform _head;   // the split-off head, pivoted on its bottom
    Vector3 _ball;     // the ball's centre, in the head's own space
    float _ballRadius; // and its radius there
    float _grown = 1f;
    bool _tried;
    readonly List<Object> _made = new List<Object>();

    const string CrystalShader = "Custom/AstrophageCrystalTop";

    public static VirusHead Of(Component owner)
    {
        VirusHead h = owner.GetComponentInChildren<VirusHead>(true);
        return h ? h : owner.gameObject.AddComponent<VirusHead>(); // not ??: Unity's fake null
    }

    void Start() => Split();

    void LateUpdate()
    {
        if (!Split()) return;
        float goal = Hovered ? hoverScale : 1f;
        if (Mathf.Approximately(_grown, goal)) return;
        _grown = Mathf.MoveTowards(_grown, goal, Mathf.Max(hoverScale - 1f, 1e-3f) * Time.unscaledDeltaTime / hoverTime);
        _head.localScale = Vector3.one * _grown;
    }

    /// <summary>The ball inside the crystal, as a sphere in the world (grown with the head).</summary>
    public Vector3 Sphere(out float radius)
    {
        if (!Split())
        {
            radius = 0.5f;
            return transform.position;
        }
        // Measured along each axis in the world (the parents' scales differ per axis and are rotated).
        radius = _ballRadius * (_head.TransformVector(Vector3.right).magnitude + _head.TransformVector(Vector3.up).magnitude
                              + _head.TransformVector(Vector3.forward).magnitude) / 3f;
        return _head.TransformPoint(_ball);
    }

    /// <summary>The head's frame (it turns with the virus).</summary>
    public Transform Frame => Split() ? _head : transform;

    // ---------------- splitting ----------------

    bool Split()
    {
        if (_head || _tried) return _head;
        _tried = true;

        Renderer r = body ? body : FindBody();
        MeshFilter filter = r ? r.GetComponent<MeshFilter>() : null;
        Mesh mesh = filter ? filter.sharedMesh : null;
        if (!mesh) return false;
        if (!mesh.isReadable)
        {
            Debug.LogWarning("VirusHead: turn on Read/Write for " + mesh.name + "'s model to split the head off.", this);
            return false;
        }

        Material[] slots = r.sharedMaterials;
        int parts = mesh.subMeshCount;
        int crystal = -1;
        for (int i = 0; i < Mathf.Min(parts, slots.Length); i++)
            if (slots[i] && slots[i].shader && slots[i].shader.name == CrystalShader) { crystal = i; break; }
        if (crystal < 0) return false;

        var vertices = new List<Vector3>();
        mesh.GetVertices(vertices);
        int[] shellIndices = mesh.GetIndices(crystal);
        if (shellIndices.Length == 0) return false;
        var shell = new Bounds(vertices[shellIndices[0]], Vector3.zero);
        foreach (int i in shellIndices) shell.Encapsulate(vertices[i]);

        // Each part's triangles, sorted into head (inside the crystal, or the crystal itself) and body.
        var headIndices = new List<int[]>();
        var headSlots = new List<Material>();
        var bodyIndices = new List<int[]>();
        var bodySlots = new List<Material>();
        var ballTriangles = new List<int>(); // every triangle inside the crystal, to find the ball among
        var inside = new List<int>();
        var outside = new List<int>();
        for (int p = 0; p < parts; p++)
        {
            Material slot = p < slots.Length ? slots[p] : (slots.Length > 0 ? slots[slots.Length - 1] : null);
            int[] indices = mesh.GetIndices(p);
            if (p == crystal)
            {
                headIndices.Add(indices);
                headSlots.Add(slot);
                continue;
            }
            inside.Clear();
            outside.Clear();
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                Vector3 c = (vertices[indices[t]] + vertices[indices[t + 1]] + vertices[indices[t + 2]]) / 3f;
                Vector3 q = c - shell.center;
                q = new Vector3(q.x / Mathf.Max(shell.extents.x, 1e-6f), q.y / Mathf.Max(shell.extents.y, 1e-6f), q.z / Mathf.Max(shell.extents.z, 1e-6f));
                List<int> to = q.sqrMagnitude < 1f ? inside : outside;
                to.Add(indices[t]); to.Add(indices[t + 1]); to.Add(indices[t + 2]);
            }
            if (inside.Count > 0)
            {
                // Inside the crystal: the ball. Drawn before the crystal (opaque under see-through).
                headIndices.Insert(0, inside.ToArray());
                headSlots.Insert(0, slot);
                ballTriangles.AddRange(inside);
            }
            if (outside.Count > 0)
            {
                bodyIndices.Add(outside.ToArray());
                bodySlots.Add(slot);
            }
        }

        // The head's bottom: the point of the crystal facing the rest of the body.
        Vector3 down = mesh.bounds.center - shell.center;
        down = down.sqrMagnitude > 1e-10f ? down.normalized : Vector3.down;
        float reach = Mathf.Abs(down.x) * shell.extents.x + Mathf.Abs(down.y) * shell.extents.y + Mathf.Abs(down.z) * shell.extents.z;
        Vector3 pivot = shell.center + down * reach;

        var go = new GameObject("Head");
        go.layer = r.gameObject.layer;
        _head = go.transform;
        _head.SetParent(r.transform, false);
        _head.localPosition = pivot;
        go.AddComponent<MeshFilter>().sharedMesh = Build(mesh, headIndices, -pivot, mesh.name + " (head)");
        MeshRenderer headRenderer = go.AddComponent<MeshRenderer>();
        headRenderer.sharedMaterials = headSlots.ToArray();
        headRenderer.shadowCastingMode = r.shadowCastingMode;
        headRenderer.receiveShadows = r.receiveShadows;
        if (!FitBall(vertices, ballTriangles, shell, out _ball, out _ballRadius))
        {
            _ball = shell.center;
            _ballRadius = Mathf.Min(shell.extents.x, Mathf.Min(shell.extents.y, shell.extents.z));
        }
        _ball -= pivot;

        filter.sharedMesh = Build(mesh, bodyIndices, Vector3.zero, mesh.name + " (body)");
        r.sharedMaterials = bodySlots.ToArray();
        return true;
    }

    // The ball among the triangles inside the crystal: they're split into connected pieces (vertices
    // welded by position), and the ball is the biggest piece lying wholly inside the crystal (body
    // parts poking in cross its surface). Fitted as a sphere: its bounds' centre, the mean distance.
    static bool FitBall(List<Vector3> vertices, List<int> triangles, Bounds shell, out Vector3 centre, out float radius)
    {
        centre = Vector3.zero;
        radius = 0f;
        if (triangles.Count < 3) return false;

        var weld = new Dictionary<Vector3Int, int>();
        var parent = new List<int>();
        var ids = new int[triangles.Count];
        for (int i = 0; i < triangles.Count; i++)
        {
            Vector3 v = vertices[triangles[i]];
            var key = new Vector3Int(Mathf.RoundToInt(v.x * 1e4f), Mathf.RoundToInt(v.y * 1e4f), Mathf.RoundToInt(v.z * 1e4f));
            if (!weld.TryGetValue(key, out int id)) { id = parent.Count; weld[key] = id; parent.Add(id); }
            ids[i] = id;
        }
        int Root(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        for (int i = 0; i + 2 < ids.Length; i += 3)
        {
            parent[Root(ids[i + 1])] = Root(ids[i]);
            parent[Root(ids[i + 2])] = Root(ids[i]);
        }

        // Per piece: triangle count, and whether every corner is inside the crystal.
        var count = new Dictionary<int, int>();
        var enclosed = new Dictionary<int, bool>();
        for (int i = 0; i < ids.Length; i++)
        {
            int root = Root(ids[i]);
            Vector3 q = vertices[triangles[i]] - shell.center;
            q = new Vector3(q.x / Mathf.Max(shell.extents.x, 1e-6f), q.y / Mathf.Max(shell.extents.y, 1e-6f), q.z / Mathf.Max(shell.extents.z, 1e-6f));
            bool inShell = q.sqrMagnitude <= 1.0001f;
            enclosed[root] = (!enclosed.TryGetValue(root, out bool e) || e) && inShell;
            if (i % 3 == 0) count[root] = (count.TryGetValue(root, out int c) ? c : 0) + 1;
        }
        int best = -1, bestCount = 0;
        bool bestEnclosed = false;
        foreach (var piece in count)
        {
            bool e = enclosed[piece.Key];
            if ((e && !bestEnclosed) || (e == bestEnclosed && piece.Value > bestCount))
            {
                best = piece.Key; bestCount = piece.Value; bestEnclosed = e;
            }
        }

        var bounds = new Bounds();
        bool any = false;
        for (int i = 0; i < ids.Length; i++)
        {
            if (Root(ids[i]) != best) continue;
            Vector3 v = vertices[triangles[i]];
            if (!any) { bounds = new Bounds(v, Vector3.zero); any = true; }
            else bounds.Encapsulate(v);
        }
        if (!any) return false;
        centre = bounds.center;
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < ids.Length; i++)
        {
            if (Root(ids[i]) != best) continue;
            sum += Vector3.Distance(vertices[triangles[i]], centre);
            n++;
        }
        radius = sum / Mathf.Max(n, 1);
        return radius > 0f;
    }

    Renderer FindBody()
    {
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            foreach (Material m in r.sharedMaterials)
                if (m && m.shader && m.shader.name == CrystalShader) return body = r;
        return null;
    }

    // A copy of the mesh (every vertex) with these triangle lists as its parts, vertices shifted.
    Mesh Build(Mesh source, List<int[]> parts, Vector3 shift, string name)
    {
        Mesh m = Instantiate(source);
        m.name = name;
        m.hideFlags = HideFlags.DontSave;
        m.subMeshCount = parts.Count;
        for (int i = 0; i < parts.Count; i++) m.SetIndices(parts[i], MeshTopology.Triangles, i, false);
        if (shift != Vector3.zero)
        {
            var v = new List<Vector3>();
            m.GetVertices(v);
            for (int i = 0; i < v.Count; i++) v[i] += shift;
            m.SetVertices(v);
        }
        m.RecalculateBounds();
        _made.Add(m);
        return m;
    }

    void OnDestroy()
    {
        foreach (Object o in _made)
            if (o) Destroy(o);
    }
}

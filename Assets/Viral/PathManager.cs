using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Gridless, trap-free navigation field for many agents around many obstacles (3D port of the 2D flow-field test bed).
///
/// Field at point x for a target g:   V(x) = sink(x - g) + sum over spheres of a flow-around DOUBLET.
///   sink(d)    = -S d / |d|^3                                   (potential flow toward the target)
///   doublet_i  = (e^3 / 2) [ U/r^3 - 3 (U.d) d / r^5 ],  d = x - c_i,  U = sink(c_i - g) - v_i
/// U is the flow the sphere sees relative to itself, so no flow crosses the sphere (radius e), and moving bodies push agents away.
/// Every term is harmonic, so the only stable point is the target: no local minima / traps. Inside a sphere the field points outward.
///
/// Usage (any script):
///     Vector3 dir = PathManager.I.GetDirection(transform.position, target.position);   // unit vector, or zero at the target
///     rb.linearVelocity = dir * speed;
/// Put agents (and anything else that must not count as an obstacle) on a layer that is excluded by obstacleLayers.
/// Spheres are found via colliders: Sphere = as is, Capsule = spheres along its axis, Box = grid of covering spheres,
/// anything else = its bounding sphere; a creature (anything with an Organism) is one sphere around all its colliders
/// (oneSpherePerCreature). Colliders with a Rigidbody are tracked every physics step (position + velocity), reading
/// each body once for all its spheres; colliders without one are treated as fixed. Call Rescan() after spawning/destroying obstacles.
/// </summary>
[DefaultExecutionOrder(-100)]
public class PathManager : MonoBehaviour
{
    public static PathManager I { get; private set; }

    [Header("Which colliders")]
    public LayerMask obstacleLayers = ~0;
    [Tooltip("Leave creatures (anything with an Organism) out of the obstacles. Off: agents steer around each other and the player.")]
    public bool ignoreOrganisms = false;
    [Tooltip("Each creature's colliders become one bounding sphere instead of a sphere per collider piece (a box is several). " +
             "Fewer spheres to refresh and to test in every query; the bubble round each creature is a little rounder and wider.")]
    public bool oneSpherePerCreature = true;
    [Tooltip("Max spheres used to cover one box collider (more = tighter fit, slower)")] public int maxSpheresPerCollider = 64;

    [Header("Field")]
    [Tooltip("Sink strength: flow speed toward the target is S/d^2. Tune against body speeds (velocity-aware compares them)")] public float sinkStrength = 1000f;
    [Tooltip("Sphere safety radius = radius * this + agentRadius")] public float safety = 1.3f;
    [Tooltip("Radius of your agents; added to every sphere so a whole agent fits through the margin")] public float agentRadius = 0.5f;
    [Tooltip("Moving bodies push agents away (uses rigidbody velocity)")] public bool velocityAware = true;
    [Tooltip("A sphere influences agents within this many safety radii (smoothly faded out; also the grid cell size)")] public float cutoff = 10f;
    [Tooltip("Inside this band around each safety radius (x radius) the inward part of the field is smoothly removed, so agents skim along a sphere instead of bouncing off it")] public float guardBand = 1.2f;

    // per-sphere source data (built by Rescan) ...
    Transform[] tr; Rigidbody[] rb; Vector3[] loc; float[] rad; int[] grp; int n;
    // Fixed at scan: each body sphere's offset in its Rigidbody's frame (so a refresh reads the body
    // once, not every sphere), and each sphere's world radius (scale doesn't change).
    Vector3[] off; float[] wr; bool[] moving;
    // ... and per-step math data
    Vector3[] p, u; float[] e; long[] key; int[] order, start, fill; int mask; float inv;
    readonly List<Transform> lt = new(); readonly List<Rigidbody> lb = new(); readonly List<Vector3> ll = new(); readonly List<float> lr = new(); readonly List<int> lg = new();

    /// <summary>Id of the obstacle "group" a component belongs to (its Rigidbody, else its own transform). Cache it and pass it to GetDirection to ignore that body.</summary>
    public static int Id(Component c) { var b = c.GetComponentInParent<Rigidbody>(); return b ? b.GetInstanceID() : c.transform.GetInstanceID(); }

    void Awake() { I = this; Rescan(); }
    void FixedUpdate() => Refresh();

    /// <summary>Grab all colliders in the scene and generalize them to spheres.</summary>
    public void Rescan()
    {
        lt.Clear(); lb.Clear(); ll.Clear(); lr.Clear(); lg.Clear();
        var creatures = new Dictionary<Organism, List<Collider>>();
        foreach (var c in FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (!c.enabled || c.isTrigger || (obstacleLayers.value & (1 << c.gameObject.layer)) == 0) continue;
            Organism o = c.GetComponentInParent<Organism>();
            if (o && ignoreOrganisms) continue;
            if (o && oneSpherePerCreature)
            {
                if (!creatures.TryGetValue(o, out var list)) creatures.Add(o, list = new List<Collider>());
                list.Add(c);
            }
            else AddCollider(c);
        }
        foreach (var kv in creatures) AddCreature(kv.Key, kv.Value);

        n = lt.Count; tr = lt.ToArray(); rb = lb.ToArray(); loc = ll.ToArray(); rad = lr.ToArray(); grp = lg.ToArray();
        off = new Vector3[n]; wr = new float[n]; moving = new bool[n];
        for (int i = 0; i < n; i++)
        {
            wr[i] = rad[i] * MaxScale(tr[i]);
            moving[i] = rb[i] != null;
            if (moving[i]) off[i] = Quaternion.Inverse(rb[i].rotation) * (tr[i].TransformPoint(loc[i]) - rb[i].position);
        }
        p = new Vector3[n]; u = new Vector3[n]; e = new float[n]; key = new long[n]; order = new int[n];
        int size = 16; while (size < 2 * n) size <<= 1; mask = size - 1; start = new int[size + 1]; fill = new int[size];
        Refresh(true);
    }

    void AddCollider(Collider c)
    {
        var t = c.transform; var body = c.attachedRigidbody;
        int id = body ? body.GetInstanceID() : t.GetInstanceID();
        void Add(Vector3 center, float r) { lt.Add(t); lb.Add(body); ll.Add(center); lr.Add(r); lg.Add(id); }
        switch (c)
        {
            case SphereCollider s: Add(s.center, s.radius); break;
            case CapsuleCollider k:
                {
                    float R = k.radius, half = Mathf.Max(0f, k.height * 0.5f - R); int cnt = half > 0 ? Mathf.CeilToInt(2f * half / R) + 1 : 1;
                    Vector3 axis = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                    for (int i = 0; i < cnt; i++) Add(k.center + axis * (cnt == 1 ? 0f : half * (2f * i / (cnt - 1) - 1f)), R);
                    break;
                }
            case BoxCollider b:
                {
                    Vector3 sz = b.size; float side = Mathf.Max(1e-3f, Mathf.Min(sz.x, sz.y, sz.z)); Vector3Int m;
                    for (; ; side *= 1.25f)   // grow the cells until the box needs at most maxSpheresPerCollider
                    {
                        m = new Vector3Int(Mathf.CeilToInt(sz.x / side), Mathf.CeilToInt(sz.y / side), Mathf.CeilToInt(sz.z / side));
                        if (m.x * m.y * m.z <= maxSpheresPerCollider) break;
                    }
                    var d = new Vector3(sz.x / m.x, sz.y / m.y, sz.z / m.z);
                    for (int x = 0; x < m.x; x++) for (int y = 0; y < m.y; y++) for (int z = 0; z < m.z; z++)
                        Add(b.center - sz * 0.5f + Vector3.Scale(d, new Vector3(x + .5f, y + .5f, z + .5f)), 0.5f * d.magnitude);   // sphere circumscribing each sub-box
                    break;
                }
            default: Add(t.InverseTransformPoint(c.bounds.center), c.bounds.extents.magnitude / MaxScale(t)); break;
        }
    }

    static float MaxScale(Transform t) { var s = t.lossyScale; return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)); }

    // One sphere around all of a creature's colliders: their bounding spheres merged.
    void AddCreature(Organism o, List<Collider> colliders)
    {
        var body = colliders[0].attachedRigidbody;
        Transform t = body ? body.transform : o.transform;

        Vector3 centre = Vector3.zero;
        var spheres = new (Vector3 c, float r)[colliders.Count];
        for (int i = 0; i < colliders.Count; i++) { spheres[i] = BoundingSphere(colliders[i]); centre += spheres[i].c; }
        centre /= colliders.Count;

        float radius = 0f;
        foreach (var sp in spheres) radius = Mathf.Max(radius, Vector3.Distance(centre, sp.c) + sp.r);

        lt.Add(t); lb.Add(body); ll.Add(t.InverseTransformPoint(centre)); lr.Add(radius / Mathf.Max(MaxScale(t), 1e-6f));
        lg.Add(body ? body.GetInstanceID() : t.GetInstanceID());
    }

    // World-space sphere that contains a collider.
    static (Vector3 c, float r) BoundingSphere(Collider c)
    {
        Transform t = c.transform; float s = MaxScale(t);
        switch (c)
        {
            case SphereCollider sp: return (t.TransformPoint(sp.center), sp.radius * s);
            case CapsuleCollider k: return (t.TransformPoint(k.center), Mathf.Max(k.radius, k.height * 0.5f) * s);
            case BoxCollider b: return (t.TransformPoint(b.center), b.size.magnitude * 0.5f * s);
            default: return (c.bounds.center, c.bounds.extents.magnitude);
        }
    }

    /// <summary>Copy positions/velocities of tracked bodies into the math spheres and rebuild the lookup grid.</summary>
    void Refresh(bool all = false)
    {
        // A body's pose and velocity are read once for all its spheres (they sit next to each
        // other in the arrays), and each sphere is placed from its fixed offset in plain C#.
        float maxE = 1e-3f;
        Rigidbody body = null;
        Vector3 bodyPos = default, bodyVel = default, bodySpin = default, bodyCom = default;
        Quaternion bodyRot = Quaternion.identity;
        for (int i = 0; i < n; i++)
        {
            if (moving[i])
            {
                if (!ReferenceEquals(rb[i], body))
                {
                    body = rb[i];
                    if (!body) { moving[i] = false; body = null; e[i] = wr[i] * safety + agentRadius; if (e[i] > maxE) maxE = e[i]; continue; } // destroyed: stays where it was
                    bodyPos = body.position; bodyRot = body.rotation;
                    if (velocityAware) { bodyVel = body.linearVelocity; bodySpin = body.angularVelocity; bodyCom = body.worldCenterOfMass; }
                }
                p[i] = bodyPos + bodyRot * off[i];
                u[i] = velocityAware ? bodyVel + Vector3.Cross(bodySpin, p[i] - bodyCom) : Vector3.zero;
            }
            else if (all)   // fixed colliders are only computed once
            {
                p[i] = tr[i].TransformPoint(loc[i]);
                u[i] = Vector3.zero;
            }
            e[i] = wr[i] * safety + agentRadius;
            if (e[i] > maxE) maxE = e[i];
        }
        inv = 1f / (cutoff * maxE);   // cell size = cutoff * largest safety radius, so 27 cells always cover every sphere's reach
        Array.Clear(start, 0, start.Length);
        for (int i = 0; i < n; i++) { key[i] = Key(p[i]); start[Bucket(key[i]) + 1]++; }
        for (int b = 0; b < mask + 1; b++) start[b + 1] += start[b];
        Array.Copy(start, fill, mask + 1);
        for (int i = 0; i < n; i++) order[fill[Bucket(key[i])]++] = i;
    }

    long Key(Vector3 q) => Pack(Mathf.FloorToInt(q.x * inv), Mathf.FloorToInt(q.y * inv), Mathf.FloorToInt(q.z * inv));
    static long Pack(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    int Bucket(long k) => (int)(((ulong)k * 0x9E3779B97F4A7C15UL) >> 40) & mask;   // hash collisions are harmless: candidates are re-checked by key and distance

    Vector3 Sink(Vector3 d) { float m = d.sqrMagnitude + 1e-6f; return -sinkStrength * d / (m * Mathf.Sqrt(m)); }

    /// <summary>Raw (unnormalized) field velocity at a point for a given target. Thread-safe between physics steps.
    /// ignoreA/ignoreB: PathManager.Id(...) of bodies to treat as non-obstacles for this query (yourself, the thing you want to hit).</summary>
    public Vector3 GetField(Vector3 x, Vector3 target, int ignoreA = 0, int ignoreB = 0)
    {
        Vector3 V = Sink(x - target), eject = Vector3.zero, nGuard = Vector3.zero; bool hit = false; float guard = 0f, invW0 = 1f / (1f - 1f / (cutoff * cutoff));
        int cx = Mathf.FloorToInt(x.x * inv), cy = Mathf.FloorToInt(x.y * inv), cz = Mathf.FloorToInt(x.z * inv);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
        {
            long k = Pack(cx + dx, cy + dy, cz + dz); int b = Bucket(k);
            for (int j = start[b], j1 = start[b + 1]; j < j1; j++)
            {
                int i = order[j]; if (key[i] != k || grp[i] == ignoreA || grp[i] == ignoreB) continue;
                Vector3 d = x - p[i]; float r2 = d.sqrMagnitude, ei = e[i];
                if (r2 < ei * ei) { eject += d / Mathf.Sqrt(r2 + 1e-9f); hit = true; continue; }   // solid interior: push outward
                float D2 = cutoff * cutoff * ei * ei; if (r2 > D2) continue;
                Vector3 U = Sink(p[i] - target) - u[i];                                              // flow the sphere sees relative to itself
                float r = Mathf.Sqrt(r2), i3 = 1f / (r2 * r), w = (1f - r2 / D2) * invW0;             // w: smooth fade to zero at the cutoff, exactly 1 on the surface (keeps zero flux there)
                V += w * w * 0.5f * ei * ei * ei * i3 * (U - 3f * Vector3.Dot(U, d) / r2 * d);       // doublet: (e^3/2)[U/r^3 - 3(U.d)d/r^5]
                float g = (guardBand * ei - r) / ((guardBand - 1f) * ei);                            // 1 on the surface -> 0 at the edge of the guard band
                if (g > guard) { guard = g; nGuard = d / r; }
            }
        }
        if (hit) return eject.normalized * V.magnitude;
        if (guard > 0f) { float vin = Vector3.Dot(V, nGuard); if (vin < 0f) V -= nGuard * (vin * guard); }   // slide: drop the part of the flow that points into the nearest sphere
        return V;
    }

    /// <summary>Unit direction an agent at agentPosition should move to reach targetPosition around all obstacles.</summary>
    public Vector3 GetDirection(Vector3 agentPosition, Vector3 targetPosition, int ignoreA = 0, int ignoreB = 0)
    {
        Vector3 v = GetField(agentPosition, targetPosition, ignoreA, ignoreB); float m = v.magnitude;
        return m > 1e-8f ? v / m : Vector3.zero;
    }

    /// <summary>Many agents at once, spread over threads (read-only, no Unity API calls inside). selfIds (optional): each agent's own Id.</summary>
    public void GetDirections(Vector3[] positions, Vector3[] targets, Vector3[] result, int count, int[] selfIds = null)
        => Parallel.For(0, count, i => result[i] = GetDirection(positions[i], targets[i], selfIds != null ? selfIds[i] : 0));

    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;
        Gizmos.color = new Color(1f, 0.4f, 0.3f, 0.5f);
        for (int i = 0; i < n; i++) Gizmos.DrawWireSphere(p[i], e[i]);
    }
}
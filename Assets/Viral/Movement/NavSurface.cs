using System;
using UnityEngine;
using Pathfinding;

/// <summary>
/// Attachment to a Surface's navmesh graph, usable by anything that crawls one.
/// Position is stored in graph space (the surface's own local space), so whatever
/// is attached rides the surface as it moves, turns and scales. NavmeshGraphs have no slope limit,
/// so any closed shape is navigable all over, edges and undersides included. Free A*PP has no Linecast, so moves are
/// clamped with GetNearest (across a narrow hole it can snap to the far side).
/// </summary>
[Serializable]
public class NavSurface
{
    public LayerMask layers = ~0;
    [Tooltip("Max contact-to-graph distance that counts as attaching.")] public float snapDistance = 2f;
    public float hoverHeight = 0.5f;
    [Tooltip("Lift onto the rounded surface the triangles approximate (Phong: round on a sphere, flat on a cube's faces). Off = faceted.")]
    public bool smooth = true;
    [Min(0f), Tooltip("Distance walked to roll over a hard edge (a cube's corner), in world units. 0 = snap.")]
    public float edgeRoll = 1f;

    NNConstraint _nn;
    NNConstraint NN => _nn ?? (_nn = NNConstraint.Default);

    // RAW navmesh point, never the smoothed one (feeding that back into GetNearest sways).
    Vector3 _pos, _fwd = Vector3.forward;
    GraphNode _node;
    float _side = 1f; // +1 = on the side the mesh's winding faces, -1 = the back (set on landing)

    // Edge roll, in graph space so it turns with the surface: what's left of the last jump
    // in the surface normal, eased out as we walk. Gradual turning (a sphere) never gets here.
    // More turn than this per pose is a hard edge, not curvature: a few degrees standing
    // still, plus what a tight curve (radius ~2) turns over the distance walked, so a
    // creature ticked every few frames can take big steps round a small sphere.
    const float JumpAngle = 5f, CurveDegreesPerUnit = 30f;
    Quaternion _roll = Quaternion.identity;
    Vector3 _lastTarget; // graph space; zero = none yet
    float _walked;
    Vector3 _up; // the surface's own normal at the last pose (not rolled), graph space; zero = none yet

    public bool Attached => _node != null;
    /// <summary>Shown normal: the surface's, eased round hard edges (the roll). Heading and moves are tangent to this.</summary>
    public Vector3 Normal { get; private set; } = Vector3.up;
    /// <summary>The surface's own normal under us, without the roll.</summary>
    public Vector3 SurfaceNormal => _up != Vector3.zero ? ToWorldDir(_up) : Normal;
    public Surface Surface { get; private set; }
    public Vector3 Heading => Tangent(ToWorldDir(_fwd));
    /// <summary>The triangle we're on and our raw point on it, in graph space (for SurfaceField).</summary>
    public TriangleMeshNode Node => _node as TriangleMeshNode;
    public Vector3 GraphPosition => _pos;

    /// <summary>A direction tangent to the surface itself -> the same way in the shown (rolled) frame.</summary>
    public Vector3 ToShown(Vector3 surfaceDirection) => Quaternion.FromToRotation(SurfaceNormal, Normal) * surfaceDirection;

    public bool Accepts(GameObject go) => (layers.value & (1 << go.layer)) != 0;

    /// <summary>How far the last refused TryAttach's contact was from the walk mesh (world units; infinity = no graph).</summary>
    public float LastMiss { get; private set; }

    public bool TryAttach(Vector3 point, Vector3 up, Surface on, Vector3 forward)
    {
        LastMiss = float.PositiveInfinity;
        if (!on || on.Graph == null || AstarPath.active == null) return false;

        NN.graphMask = on.Mask; // every graph sits at the origin of its own space: only ask this one
        NNInfo hit = AstarPath.active.GetNearest(on.ToGraph(point), NN);
        if (hit.node != null) LastMiss = Vector3.Distance(point, on.ToWorld(hit.position));
        if (hit.node == null || LastMiss > snapDistance)
            return false; // GetNearest always finds something; distance decides

        Surface = on;
        _pos = hit.position; _node = hit.node;
        Normal = up;
        _up = ToGraphDir(up);
        // Which side we landed on. From here the mesh's winding says where "out" is, which
        // holds across hard edges (where comparing with the last normal is a coin toss).
        _side = hit.node is TriangleMeshNode tri && Vector3.Dot(FaceNormal(tri), up) < 0f ? -1f : 1f;
        _roll = Quaternion.identity; _lastTarget = Vector3.zero; _walked = 0f;
        SetHeading(forward);
        return true;
    }

    public void Release() { _node = null; Surface = null; }

    public void SetHeading(Vector3 worldDirection) => _fwd = ToGraphDir(Tangent(worldDirection));

    /// <summary>Step along the heading. False = no ground there (fell off).</summary>
    public bool Crawl(float distance, out float moved)
    {
        moved = 0f;
        if (!Attached || !Surface || AstarPath.active == null) return false;

        NN.graphMask = Surface.Mask;
        // The heading is tangent to the shown normal, which lags round a hard edge (the roll). Just past
        // one it points off the new face, into the air: the step projected back to where we stood, so we
        // never walked, so the roll never wore off: stuck on every cube edge. Step along the real face.
        Vector3 up = SurfaceNormal;
        Vector3 dir = Vector3.ProjectOnPlane(Quaternion.FromToRotation(Normal, up) * Heading, up);
        dir = dir.sqrMagnitude > 1e-8f ? dir.normalized : Heading;

        // Walk on the facets themselves: the heading (tangent to the smooth surface) is turned onto the facet
        // we're on and stepped in its plane, in steps no longer than the facet, carried along the smooth
        // normal from one to the next. A step along the smooth tangent left the facet's plane and snapping it
        // back pulled it sideways, a different way on every facet: walking along a curved rim zig-zagged.
        for (int i = 0; i < MaxSubsteps && distance > 1e-5f; i++)
        {
            if (!(_node is TriangleMeshNode tri)) return false;
            Vector3 face = FaceNormal(tri);
            face = face.sqrMagnitude > 1e-12f ? face.normalized * _side : up;
            Vector3 ahead = Vector3.ProjectOnPlane(Quaternion.FromToRotation(up, face) * dir, face);
            ahead = ahead.sqrMagnitude > 1e-8f ? ahead.normalized : dir;

            float step = i == MaxSubsteps - 1 ? distance : Mathf.Min(distance, 0.5f * ShortestEdge(tri));
            if (!Step(ahead * step, face, out float m)) return moved > 0f;
            moved += m;
            distance -= step;
            if (distance <= 1e-5f) break;

            Smooth(out Vector3 next); // the smooth normal here, to carry the heading on (parallel transport)
            dir = Vector3.ProjectOnPlane(Quaternion.FromToRotation(up, next) * dir, next).normalized;
            up = next;
        }
        _walked += moved;
        return true;
    }

    const int MaxSubsteps = 4;

    // One step along 'ahead' (in the facet's plane). False = no graph.
    bool Step(Vector3 ahead, Vector3 face, out float moved)
    {
        moved = 0f;
        float distance = ahead.magnitude;
        Vector3 from = ToWorld(_pos);

        // Straight ahead first: inside the facet it lands exactly there.
        NNInfo hit = AstarPath.active.GetNearest(ToGraph(from + ahead), NN);
        if (hit.node == null) return false;
        float d = Vector3.Distance(from, ToWorld(hit.position));

        // Came up short: an edge. Aim as far down as ahead so past a convex edge the face
        // around the corner is nearest (straight ahead is equally near both and sticks),
        // or up a wall at a concave one. Down is the face's own normal: the smoothed one
        // leans off the facet and would push sideways. Only here, never on open ground.
        if (d < distance * 0.75f)
        {
            Vector3 down = face * -distance;
            for (int side = 0; side < 2; side++)
            {
                NNInfo alt = AstarPath.active.GetNearest(ToGraph(from + ahead + (side == 0 ? down : -down)), NN);
                if (alt.node == null) continue;
                float ad = Vector3.Distance(from, ToWorld(alt.position));
                if (ad > d) { hit = alt; d = ad; }
            }
        }

        moved = d; // through the current transform: cell motion isn't counted
        _pos = hit.position;
        _node = hit.node;
        return true;
    }

    float ShortestEdge(TriangleMeshNode tri)
    {
        Vector3 a = ToWorld((Vector3)tri.GetVertex(0)), b = ToWorld((Vector3)tri.GetVertex(1)), c = ToWorld((Vector3)tri.GetVertex(2));
        return Mathf.Sqrt(Mathf.Min((b - a).sqrMagnitude, Mathf.Min((c - b).sqrMagnitude, (a - c).sqrMagnitude)));
    }

    /// <summary>World pose for the current frame, riding the cell's current transform.</summary>
    public void Pose(out Vector3 position, out Quaternion rotation)
    {
        Vector3 before = Normal, heading = Heading;
        Vector3 p = Smooth(out Vector3 n);
        _up = ToGraphDir(n);
        n = Roll(n);
        // Turn the heading with the surface, so crossing an edge carries on over it instead
        // of flattening against the new face.
        Normal = n;
        heading = Tangent(Quaternion.FromToRotation(before, n) * heading);
        _fwd = ToGraphDir(heading);
        position = ToWorld(p) + n * hoverHeight;
        rotation = Quaternion.LookRotation(heading, n);
    }

    // Surface normal -> shown normal: a sudden jump (crossing a hard edge) is kept as an
    // offset and worn off over edgeRoll of walking, so the body rolls round the edge.
    Vector3 Roll(Vector3 target)
    {
        Vector3 t = ToGraphDir(target);
        if (_lastTarget != Vector3.zero && edgeRoll > 0f &&
            Vector3.Angle(_lastTarget, t) > JumpAngle + CurveDegreesPerUnit * _walked)
            _roll = Quaternion.FromToRotation(t, _roll * _lastTarget); // keep showing what we showed
        _lastTarget = t;

        if (edgeRoll > 0f) _roll = Quaternion.Slerp(_roll, Quaternion.identity, 1f - Mathf.Exp(-_walked / edgeRoll));
        else _roll = Quaternion.identity;
        _walked = 0f;
        return ToWorldDir(_roll * t).normalized;
    }

    Vector3 Tangent(Vector3 v)
    {
        Vector3 h = Vector3.ProjectOnPlane(v, Normal);
        return h.sqrMagnitude > 1e-4f ? h.normalized : Vector3.Cross(Normal, Mathf.Abs(Normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
    }

    Vector3 ToWorld(Vector3 p) => Surface ? Surface.ToWorld(p) : p;
    Vector3 ToGraph(Vector3 p) => Surface ? Surface.ToGraph(p) : p;
    Vector3 ToWorldDir(Vector3 d) => Surface ? Surface.ToWorldDir(d) : d;
    Vector3 ToGraphDir(Vector3 d) => Surface ? Surface.ToGraphDir(d) : d;

    /// <summary>
    /// Raw point lifted onto the smooth surface its triangle approximates. From each corner
    /// the surface is an arc bending by that corner's curvature toward the point (round on a
    /// sphere, straight along a cylinder, flat on a flat face); the three are blended by the
    /// point's barycentric weights, so it's continuous across triangles on any mesh.
    /// </summary>
    Vector3 Smooth(out Vector3 normal)
    {
        normal = Normal;
        if (!(_node is TriangleMeshNode tri)) return _pos;

        Vector3 a = (Vector3)tri.GetVertex(0), b = (Vector3)tri.GetVertex(1), c = (Vector3)tri.GetVertex(2);

        Vector3 face = FaceNormal(tri);
        if (face.sqrMagnitude > 1e-8f) normal = face.normalized * _side;

        if (!smooth || !Surface || !Surface.Corners(tri, out Surface.Corner ca, out Surface.Corner cb, out Surface.Corner cc))
            return _pos;

        Vector3 w = Barycentric(_pos, a, b, c);
        Vector3 pa = FromCorner(_pos, a, ca, out Vector3 na);
        Vector3 pb = FromCorner(_pos, b, cb, out Vector3 nb);
        Vector3 pc = FromCorner(_pos, c, cc, out Vector3 nc);

        // The arcs' own normals only meet at the triangle's edges (a new turn rate per triangle: a sway): the
        // distance-blended one turns smoothly across them.
        Vector3 smoothed = Surface.ToWorldNormal(Surface.SmoothNormal(tri, _pos, out Vector3 blended) ? blended : w.x * na + w.y * nb + w.z * nc);
        if (smoothed.sqrMagnitude > 1e-8f) normal = smoothed * _side;
        return w.x * pa + w.y * pb + w.z * pc;
    }

    // Where the surface through corner v sits over p: p's offset in v's tangent plane, bent
    // along an arc of the corner's curvature in that direction, with the arc's normal there.
    static Vector3 FromCorner(Vector3 p, Vector3 v, Surface.Corner corner, out Vector3 normal)
    {
        Vector3 n = corner.normal;
        Vector3 t = Vector3.ProjectOnPlane(p - v, n);
        float s = t.magnitude;
        normal = n;
        if (s < 1e-6f) return v + t;

        Vector3 d = t / s;
        float ks = Mathf.Clamp(corner.Curvature(d) * s, -0.95f, 0.95f);
        float root = Mathf.Sqrt(1f - ks * ks);
        normal = n * root + d * ks;
        return v + t - n * (ks * s / (1f + root)); // drop (1 - root) / k, written stably as k -> 0
    }

    // World face normal by the mesh's winding (world vertices: right under non-uniform scale).
    Vector3 FaceNormal(TriangleMeshNode tri)
    {
        Vector3 a = ToWorld((Vector3)tri.GetVertex(0));
        return Vector3.Cross(ToWorld((Vector3)tri.GetVertex(1)) - a, ToWorld((Vector3)tri.GetVertex(2)) - a);
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
}

using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The antibody's shape, built once per detail level and shared by every antibody (Custom/Antibody
/// draws them all in one instanced call; see ImmuneSystem). About 1.3 across, hinge at the origin, arms
/// up (+y) in the XY plane: each arm a closed loop of chain (a fat ring with a hole), the stem two chains
/// twisted round each other, a blob at the hinge. Surfaces are covered in beads (displaced along the
/// normal) and vertex-coloured: blue, magenta in the folds, teal patches; alpha = occlusion in the folds.
/// uv0 = (part: 0 hinge, 1 stem, 2 left arm, 3 right arm; 0 at the hinge .. 1 at the tip) for the wiggle.
/// </summary>
public static class AntibodyMesh
{
    /// <summary>Arms lean this far off the stem's axis (Custom/Antibody's arm axes must match).</summary>
    public const float ArmAngle = 46f;

    /// <param name="detail">1 = close-up (~3k vertices, beads resolved), ~0.35 = far (~500).</param>
    public static Mesh Build(float detail)
    {
        var b = new Builder();
        int Sides(int n) => Mathf.Max(5, Mathf.RoundToInt(n * detail));
        int Rings(int n) => Mathf.Max(6, Mathf.RoundToInt(n * detail));
        var spine = new List<Vector3>();
        var radius = new List<float>();

        // Hinge: a lumpy ball (a tube with a round profile).
        int hr = Rings(10);
        for (int i = 0; i <= hr; i++)
        {
            float y = Mathf.Lerp(-0.13f, 0.13f, i / (float)hr);
            spine.Add(new Vector3(0f, y, 0f));
            radius.Add(Mathf.Sqrt(Mathf.Max(1e-4f, 0.0169f - y * y)));
        }
        b.Tube(spine, radius, false, Sides(16), Vector3.right, 0f, _ => 0f);

        // Stem: two chains twisted round each other, rounded ends.
        int sr = Rings(30);
        for (int chain = 0; chain < 2; chain++)
        {
            spine.Clear(); radius.Clear();
            for (int i = 0; i <= sr; i++)
            {
                float t = i / (float)sr;
                float a = chain * Mathf.PI + t * Mathf.PI * 1.2f;
                spine.Add(new Vector3(Mathf.Cos(a) * 0.065f, Mathf.Lerp(-0.05f, -0.62f, t), Mathf.Sin(a) * 0.065f));
                radius.Add(Mathf.Lerp(0.085f, 0.075f, t) * EndRound(t, 0.12f));
            }
            b.Tube(spine, radius, false, Sides(12), Vector3.forward, 1f, p => Mathf.Clamp01(-p.y / 0.62f));
        }

        // Arms: a loop each, long along the arm, thicker toward the tip, the tips curled in a touch.
        int ar = Rings(72);
        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? 1f : -1f; // left arm (part 2) leans -x
            Vector3 axis = Quaternion.Euler(0f, 0f, sign * ArmAngle) * Vector3.up;
            Vector3 across = Vector3.Cross(Vector3.forward, axis);
            spine.Clear(); radius.Clear();
            for (int i = 0; i < ar; i++)
            {
                float a = i / (float)ar * Mathf.PI * 2f;
                float along = 0.37f - Mathf.Cos(a) * 0.3f;                 // 0.07 .. 0.67 out from the hinge
                // `across` points outward on the left arm, inward on the right: sign mirrors them.
                float wide = Mathf.Sin(a) * 0.1f + sign * 0.03f * Mathf.Sin(a) * Mathf.Sin(a); // the outer chain bulges more
                float curl = -sign * along * along * 0.25f;                                   // tips curl inward
                spine.Add(axis * along + across * (wide + curl));
                radius.Add(Mathf.Lerp(0.07f, 0.095f, Mathf.Clamp01(along / 0.67f)));
            }
            b.Tube(spine, radius, true, Sides(16), Vector3.forward, 2f + side,
                   p => Mathf.Clamp01(Vector3.Dot(p, axis) / 0.67f));
        }

        return b.ToMesh(detail >= 0.7f);
    }

    // Round a tube's ends off: the radius falls away like a sphere over the last `edge` of its length.
    static float EndRound(float t, float edge)
    {
        float x = Mathf.Clamp01(Mathf.Min(t, 1f - t) / edge);
        return Mathf.Max(0.15f, Mathf.Sqrt(1f - (1f - x) * (1f - x)));
    }

    class Builder
    {
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Vector3> _n = new List<Vector3>();
        readonly List<Vector2> _uv = new List<Vector2>();
        readonly List<int> _t = new List<int>();

        // A tube swept along the spine (frames parallel-transported from `normal`). Open tubes end in a
        // pole vertex each; closed ones wrap (planar loops with `normal` off the plane wrap seamlessly).
        public void Tube(List<Vector3> spine, List<float> radius, bool closed, int sides, Vector3 normal,
                         float part, System.Func<Vector3, float> along)
        {
            int rings = spine.Count, start = _v.Count;
            Vector3 frame = normal;
            for (int i = 0; i < rings; i++)
            {
                Vector3 prev = spine[closed ? (i - 1 + rings) % rings : Mathf.Max(i - 1, 0)];
                Vector3 next = spine[closed ? (i + 1) % rings : Mathf.Min(i + 1, rings - 1)];
                Vector3 tangent = (next - prev).normalized;
                frame = Vector3.ProjectOnPlane(frame, tangent).normalized;
                Vector3 bi = Vector3.Cross(tangent, frame);
                for (int j = 0; j < sides; j++)
                {
                    float a = j / (float)sides * Mathf.PI * 2f;
                    Vector3 dir = frame * Mathf.Cos(a) + bi * Mathf.Sin(a);
                    Add(spine[i] + dir * radius[i], dir, part, along);
                }
            }
            int segs = closed ? rings : rings - 1;
            for (int i = 0; i < segs; i++)
            {
                int r0 = start + i * sides, r1 = start + (i + 1) % rings * sides;
                for (int j = 0; j < sides; j++)
                {
                    int j1 = (j + 1) % sides;
                    _t.Add(r0 + j); _t.Add(r1 + j1); _t.Add(r1 + j);
                    _t.Add(r0 + j); _t.Add(r0 + j1); _t.Add(r1 + j1);
                }
            }
            if (closed) return;
            for (int end = 0; end < 2; end++)
            {
                int ring = end == 0 ? 0 : rings - 1;
                Vector3 out_ = (end == 0 ? spine[0] - spine[1] : spine[rings - 1] - spine[rings - 2]).normalized;
                int pole = _v.Count;
                Add(spine[ring] + out_ * radius[ring] * 0.6f, out_, part, along);
                int r = start + ring * sides;
                for (int j = 0; j < sides; j++)
                {
                    int j1 = (j + 1) % sides;
                    if (end == 0) { _t.Add(pole); _t.Add(r + j1); _t.Add(r + j); }
                    else { _t.Add(pole); _t.Add(r + j); _t.Add(r + j1); }
                }
            }
        }

        void Add(Vector3 p, Vector3 n, float part, System.Func<Vector3, float> along)
        {
            _v.Add(p);
            _n.Add(n);
            _uv.Add(new Vector2(part, along(p)));
        }

        public Mesh ToMesh(bool beads)
        {
            var colors = new Color[_v.Count];
            var blue = new Color(0.22f, 0.36f, 1f);
            var fold = new Color(0.85f, 0.12f, 0.62f);
            var teal = new Color(0.2f, 0.78f, 0.72f);
            for (int i = 0; i < _v.Count; i++)
            {
                // Out where a bead sits, sunk in the folds between them. Far meshes are too coarse for
                // beads: they keep the colours, and only the broad lumps.
                float bead = Beads(_v[i]);
                float patch = Mathf.PerlinNoise(_v[i].x * 5f + 11.3f, _v[i].y * 5f + _v[i].z * 3f + 4.1f);
                _v[i] += _n[i] * ((beads ? (bead - 0.35f) * 0.035f : 0f) + (patch - 0.5f) * 0.02f);
                Color c = Color.Lerp(fold, blue, Mathf.SmoothStep(0.05f, 0.65f, bead));
                c = Color.Lerp(c, teal, Mathf.SmoothStep(0.55f, 0.8f, patch) * 0.7f);
                c.a = Mathf.Lerp(0.45f, 1f, bead);
                colors[i] = c;
            }
            var mesh = new Mesh { name = "Antibody", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(_v);
            mesh.SetUVs(0, _uv);
            mesh.SetColors(colors);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.UploadMeshData(false); // kept readable: Selectable fits its boxes to the far mesh
            return mesh;
        }

        // Worley bumps: one bead per cell of a jittered grid, 1 on top of a bead, 0 between them.
        static float Beads(Vector3 p)
        {
            const float cell = 0.06f, r = 0.05f;
            Vector3 q = p / cell;
            int cx = Mathf.FloorToInt(q.x), cy = Mathf.FloorToInt(q.y), cz = Mathf.FloorToInt(q.z);
            float best = 0f;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                int ix = cx + x, iy = cy + y, iz = cz + z;
                var f = new Vector3(ix + Hash(ix, iy, iz, 1), iy + Hash(ix, iy, iz, 2), iz + Hash(ix, iy, iz, 3)) * cell;
                float size = r * (0.7f + 0.5f * Hash(ix, iy, iz, 4));
                best = Mathf.Max(best, 1f - (p - f).sqrMagnitude / (size * size));
            }
            return Mathf.Clamp01(best);
        }

        static float Hash(int x, int y, int z, int k)
        {
            uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)k * 2654435761u);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xffffff) / 16777216f;
        }
    }
}

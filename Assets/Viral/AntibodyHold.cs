using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where antibodies sit on one creature, and how hard it's shaking them.
/// - Slots: latitude rings round the drawn body (BodyHull: its meshes, not its collider) in the frame it's
///   shown in (Organism.Shown), from the top
///   down to <see cref="BottomCap"/> off its underside (that side is on the ground). Each antibody
///   lies with its arms along its ring and its stem out; rings are spaced for an antibody's width,
///   slots round a ring for its arm span, alternate rings staggered, so neighbours never overlap.
///   When a shell is full the next one out starts (over the first), up to <see cref="Layers"/>.
///   Each hinge sits just off the farthest surface under its antibody, so they follow the body's real shape.
///   Every slot knows its wrap radius (hinge to centre, in antibody sizes): the shader bends the
///   antibody round that centre so the arms lie along the shell under it (the grip).
/// - Shake: turning fast and sudden speed changes of the shown body (both low-passed, so frame jitter
///   and a single step don't count), past a threshold. Stuck antibodies wear their grip down by it
///   (Antibody).
/// One per creature with antibodies on it, made on the first stick (<see cref="For"/>), updated once a
/// frame by ImmuneSystem. Cost: layout once per creature (a few dozen slots x a hull footprint scan); a claim walks the slots;
/// per frame O(1) per creature and per stuck antibody.
/// </summary>
public class AntibodyHold
{
    public const int Layers = 3;
    /// <summary>No slots within this many degrees of straight down (the ground side).</summary>
    public const float BottomCap = 55f;

    struct Slot
    {
        public Vector3 dir;       // out from the centre, in the shown frame
        public Quaternion rot;    // antibody pose in the shown frame: +y in, arms along the ring
        public float radius;      // hinge distance from the centre (world)
        public float wrap;        // hinge to centre, in antibody sizes (the shader bends round it)
        public int layer;
        public bool taken;
    }

    public readonly Organism organism;
    /// <summary>Antibodies holding on.</summary>
    public int Count { get; private set; }
    /// <summary>How hard it's being shaken: 0 still .. ~1 thrashing (1.5 at most).</summary>
    public float Shake { get; private set; }
    /// <summary>Velocity of the body (world), for flinging off what lets go.</summary>
    public Vector3 Velocity { get; private set; }
    /// <summary>Frame the slots live in (rotation only; slots are in world units).</summary>
    public Transform Frame { get; private set; }

    readonly Slot[] _slots;
    readonly Vector3 _centre; // in the frame
    Vector3 _lastPos;
    Quaternion _lastRot;
    float _turning, _accel; // low-passed
    bool _primed;

    static readonly Dictionary<Organism, AntibodyHold> s_all = new Dictionary<Organism, AntibodyHold>();
    static readonly List<Organism> s_drop = new List<Organism>();

    public static int CountOn(Organism o) => o && s_all.TryGetValue(o, out AntibodyHold h) ? h.Count : 0;

    /// <summary>The hold on 'o', laid out for antibodies of world size 'size' the first time.</summary>
    public static AntibodyHold For(Organism o, float size)
    {
        if (s_all.TryGetValue(o, out AntibodyHold h)) return h;
        h = new AntibodyHold(o, size);
        s_all[o] = h;
        return h;
    }

    /// <summary>Once a frame, after creatures have moved (ImmuneSystem.LateUpdate). Drops empty holds.</summary>
    public static void UpdateAll(float dt, Vector2 turn, Vector2 jolt)
    {
        s_drop.Clear();
        foreach (KeyValuePair<Organism, AntibodyHold> kv in s_all)
        {
            if (!kv.Key || kv.Value.Count <= 0) { s_drop.Add(kv.Key); continue; }
            kv.Value.Measure(dt, turn, jolt);
        }
        foreach (Organism o in s_drop) s_all.Remove(o);
    }

    AntibodyHold(Organism o, float size)
    {
        organism = o;
        BodyHull hull = BodyHull.For(o);
        Frame = hull.Frame;
        _centre = Quaternion.Inverse(Frame.rotation) * (hull.Centre - Frame.position);
        Vector3 up = Quaternion.Inverse(Frame.rotation) * (o.up.sqrMagnitude > 0.5f ? o.up : Frame.up);
        _slots = Layout(hull, size, up.normalized);
    }

    // Slots follow the drawn body (BodyHull), not a sphere: each hinge sits just off the farthest surface under
    // the antibody's footprint, rings stepped by the local radius.
    static Slot[] Layout(BodyHull hull, float size, Vector3 up)
    {
        var slots = new List<Slot>();
        Vector3 e1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 e2 = Vector3.Cross(up, e1);
        float maxPolar = (180f - BottomCap) * Mathf.Deg2Rad;
        float span = size * 1.5f; // arm tip to arm tip laid along the ring
        for (int layer = 0; layer < Layers; layer++)
        {
            float lift = size * (0.22f + 0.45f * layer); // hinge just off the shell below
            int ring = 0;
            for (float polar = 0f; polar <= maxPolar; ring++)
            {
                // The ring's mean hinge radius sets its slot count and the step to the next ring.
                float mean = 0f;
                const int Probe = 12;
                for (int k = 0; k < Probe; k++)
                {
                    float a = k * Mathf.PI * 2f / Probe;
                    mean += hull.Radius(up * Mathf.Cos(polar) + (e1 * Mathf.Cos(a) + e2 * Mathf.Sin(a)) * Mathf.Sin(polar));
                }
                mean = mean / Probe + lift;
                int count = ring == 0 ? 1 : Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * mean * Mathf.Sin(polar) / span));
                float stagger = (ring % 2) * 0.5f + layer * 0.25f;
                for (int k = 0; k < count; k++)
                {
                    float around = (k + stagger) / count * Mathf.PI * 2f;
                    Vector3 side = e1 * Mathf.Cos(around) + e2 * Mathf.Sin(around);
                    Vector3 dir = up * Mathf.Cos(polar) + side * Mathf.Sin(polar);
                    Vector3 along = ring == 0 ? side : Vector3.Cross(up, dir).normalized; // round the ring
                    Vector3 inward = -dir;
                    float surface = hull.Radius(dir, size * 0.5f / Mathf.Max(mean, size));
                    float hinge = surface + lift;
                    slots.Add(new Slot
                    {
                        dir = dir,
                        rot = Quaternion.LookRotation(Vector3.Cross(along, inward), inward),
                        radius = hinge,
                        wrap = hinge / size,
                        layer = layer,
                    });
                }
                polar += size * 0.5f / mean; // an antibody's width between rings
            }
        }
        return slots.ToArray();
    }

    /// <summary>Take the free slot nearest where an antibody touched (world), innermost shell first.
    /// -1 when every shell is full.</summary>
    public int Claim(Vector3 from)
    {
        Vector3 local = Quaternion.Inverse(Frame.rotation) * (from - Centre);
        local = local.sqrMagnitude > 1e-8f ? local.normalized : Vector3.up;
        for (int layer = 0; layer < Layers; layer++)
        {
            int best = -1;
            float bestDot = -2f;
            for (int i = 0; i < _slots.Length; i++)
            {
                ref Slot s = ref _slots[i];
                if (s.layer != layer || s.taken) continue;
                float d = Vector3.Dot(s.dir, local);
                if (d > bestDot) { bestDot = d; best = i; }
            }
            if (best < 0) continue;
            _slots[best].taken = true;
            Count++;
            return best;
        }
        return -1;
    }

    public void Release(int slot)
    {
        if (slot < 0 || slot >= _slots.Length || !_slots[slot].taken) return;
        _slots[slot].taken = false;
        Count--;
    }

    Vector3 Centre => Frame.position + Frame.rotation * _centre;

    /// <summary>World pose of a slot, lifted 'lift' metres off it (loosening).</summary>
    public void Pose(int slot, float lift, out Vector3 position, out Quaternion rotation)
    {
        ref Slot s = ref _slots[slot];
        Quaternion f = Frame.rotation;
        position = Centre + f * (s.dir * (s.radius + lift));
        rotation = f * s.rot;
    }

    /// <summary>Hinge-to-centre distance in antibody sizes: what the shader wraps the antibody round.</summary>
    public float Wrap(int slot) => slot >= 0 && slot < _slots.Length ? _slots[slot].wrap : 0f;

    /// <summary>Where an antibody at 'world' is in the frame (to ease it onto its slot from there).</summary>
    public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(Frame.rotation) * (world - Centre);
    public Vector3 ToWorld(Vector3 local) => Centre + Frame.rotation * local;
    public Vector3 SlotLocal(int slot, float lift) => _slots[slot].dir * (_slots[slot].radius + lift);

    // Turn rate (deg/s) and acceleration (m/s^2) of the shown body, each past its threshold (x) as a
    // share of its full value (y), eased so one jolt is a short kick, not a spike.
    void Measure(float dt, Vector2 turn, Vector2 jolt)
    {
        if (!Frame) Frame = organism.Shown;
        Vector3 pos = organism.transform.position;
        Quaternion rot = Frame.rotation;
        if (!_primed || dt < 1e-5f || (pos - _lastPos).sqrMagnitude > 400f) // first frame, or a teleport
        {
            _primed = true;
            Velocity = Vector3.zero;
            Shake = _turning = _accel = 0f;
        }
        else
        {
            // Low-passed (~0.1 s) so jitter, a single footstep or one turned frame barely register:
            // it takes sustained whipping about.
            float k = 1f - Mathf.Exp(-10f * dt);
            _turning = Mathf.Lerp(_turning, Quaternion.Angle(_lastRot, rot) / dt, k);
            Vector3 v = Vector3.Lerp(Velocity, (pos - _lastPos) / dt, 1f - Mathf.Exp(-20f * dt));
            _accel = Mathf.Lerp(_accel, (v - Velocity).magnitude / dt, k);
            Velocity = v;
            float raw = Mathf.Min(1.5f, Excess(_turning, turn) + Excess(_accel, jolt));
            Shake = Mathf.Lerp(Shake, raw, 1f - Mathf.Exp(-(raw > Shake ? 8f : 5f) * dt));
        }
        _lastPos = pos;
        _lastRot = rot;
    }

    static float Excess(float value, Vector2 range) => Mathf.Max(0f, value - range.x) / Mathf.Max(range.y - range.x, 1e-3f);
}

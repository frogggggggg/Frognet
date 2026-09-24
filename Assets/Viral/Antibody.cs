using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One antibody (a Y: the arms grab). No Update of its own: ImmuneSystem ticks every antibody in
/// one loop with its settings.
/// - Drift: wanders, pulled along the cells' signal field (CellSignal.Pull), so antibodies gather
///   where the alarm is loudest.
/// - Patrol: close to a signalling cell it circles over the activity there, looking around.
/// - Chase: it saw a virus (only while patrolling a signalling cell, or very close) and chose to go
///   for it (ImmuneSystem.stickChance; if not, it ignores that one for a while).
/// - Stuck: it reached the virus and holds on, arms first, riding along until the virus is gone.
///   <see cref="StuckOn"/> counts them per virus (for effects to come).
/// Looks: ImmuneSystem draws every antibody in one instanced call (AntibodyMesh, Custom/Antibody, which
/// wiggles it); this only keeps its <see cref="Wiggle"/> (seed, agitation by state, grip).
/// </summary>
public class Antibody : MonoBehaviour
{
    public enum State { Drift, Patrol, Chase, Stuck }

    public State Current { get; private set; }
    public Organism Prey { get; private set; }

    Vector3 _velocity;
    float _seed, _nextLook, _spin;
    Organism _ignored;
    float _ignoredUntil;
    Vector3 _stuckLocal;
    Quaternion _stuckRotation;

    float _agitation = 0.5f, _grip;

    /// <summary>When ImmuneSystem last ticked it (it ticks far ones every few frames).</summary>
    public float LastTick { get; set; }

    /// <summary>Per-instance shader data: seed, agitation (calm drifting .. frantic chasing), grip (arms
    /// clamped while stuck).</summary>
    public Vector4 Wiggle => new Vector4(_seed, _agitation, _grip, 0f);

    static readonly Dictionary<Organism, int> s_stuck = new Dictionary<Organism, int>();

    /// <summary>How many antibodies are holding on to this creature.</summary>
    public static int StuckOn(Organism o) => o && s_stuck.TryGetValue(o, out int n) ? n : 0;

    void Awake() => _seed = Random.value * 100f;

    void OnDisable() => Unstick();

    public void Tick(ImmuneSystem s, float dt, float now)
    {
        float agitation = Current switch { State.Patrol => 0.8f, State.Chase => 1.6f, State.Stuck => 1.2f, _ => 0.5f };
        _agitation = Mathf.MoveTowards(_agitation, agitation, dt * 1.5f);
        _grip = Mathf.MoveTowards(_grip, Current == State.Stuck ? 1f : 0f, dt * 4f);

        if (Current == State.Stuck)
        {
            if (!Prey || !Prey.isActiveAndEnabled)
            {
                Unstick();
                Current = State.Drift;
                return;
            }
            Transform t = Prey.transform;
            transform.SetPositionAndRotation(t.TransformPoint(_stuckLocal), t.rotation * _stuckRotation);
            return;
        }

        Vector3 pos = transform.position;
        if (now >= _nextLook)
        {
            _nextLook = now + s.lookInterval * Random.Range(0.7f, 1.3f); // staggered
            Look(s, pos, now);
        }

        Vector3 desire;
        float speed = s.speed;
        if (Current == State.Chase)
        {
            if (!Prey || !Prey.isActiveAndEnabled || (Prey.transform.position - pos).magnitude > s.sightRange * 2f)
            {
                Current = State.Drift;
                Prey = null;
                desire = Vector3.zero;
            }
            else
            {
                Vector3 to = Prey.transform.position - pos;
                if (to.magnitude <= Reach(Prey) + s.stickDistance)
                {
                    Stick(s);
                    return;
                }
                desire = to.normalized;
                speed *= s.chaseBoost;
            }
        }
        else desire = Wander(s, pos, now);

        desire += Avoid(s, pos);
        _velocity = Vector3.MoveTowards(_velocity, Vector3.ClampMagnitude(desire, 1f) * speed, s.acceleration * dt);
        pos += _velocity * dt;

        // Tumble along, turning to face where it's going.
        _spin += dt * (Current == State.Chase ? 150f : 30f);
        Quaternion face = _velocity.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(_velocity) : transform.rotation;
        transform.SetPositionAndRotation(pos, Quaternion.Slerp(transform.rotation, face * Quaternion.Euler(90f, _spin, 0f), dt * 3f));
    }

    // Drifting: wander, pulled along the signal field. Near a loud cell: circle over its activity.
    Vector3 Wander(ImmuneSystem s, Vector3 pos, float now)
    {
        float t = now * 0.15f + _seed;
        Vector3 wander = new Vector3(Mathf.PerlinNoise(t, _seed) - 0.5f, Mathf.PerlinNoise(_seed, t) - 0.5f,
                                     Mathf.PerlinNoise(t + 31.7f, _seed + 7.1f) - 0.5f) * 2f;

        CellSignal near = Patrolled(s, pos);
        Current = near ? State.Patrol : State.Drift;
        if (near)
        {
            // Hover over the hotspot, swirling round it.
            Vector3 centre = near.Centre(out float r);
            Vector3 spot = near.Hotspot;
            Vector3 up = (spot - centre).normalized;
            Vector3 goal = spot + up * s.patrolHeight;
            Vector3 to = goal - pos;
            Vector3 swirl = Vector3.Cross(up, to).normalized * 0.5f;
            return to.normalized * Mathf.Clamp01(to.magnitude / s.patrolHeight) + swirl + wander * 0.3f;
        }

        Vector3 pull = CellSignal.Pull(pos, s.pullFalloff, s.callThreshold);
        float strength = Mathf.Clamp01(pull.magnitude / s.pullFull);
        return (strength > 0f ? pull.normalized * strength : Vector3.zero) + wander * Mathf.Lerp(0.5f, 0.25f, strength);
    }

    // The signalling cell it's over, if any: close enough to its activity to patrol it.
    CellSignal Patrolled(ImmuneSystem s, Vector3 pos)
    {
        CellSignal best = null;
        float bestSignal = 0f;
        foreach (CellSignal c in CellSignal.All)
        {
            if (c.Signal < s.callThreshold || c.Signal <= bestSignal) continue;
            if ((c.Hotspot - pos).sqrMagnitude > s.patrolRange * s.patrolRange) continue;
            best = c;
            bestSignal = c.Signal;
        }
        return best;
    }

    // Looking round for a virus: while patrolling, anything in sight; otherwise only what's right by it.
    void Look(ImmuneSystem s, Vector3 pos, float now)
    {
        if (Current == State.Chase) return;
        float range = Current == State.Patrol ? s.sightRange : s.sightRange * 0.35f;
        Organism seen = null;
        float best = range * range;
        foreach (Organism o in Organism.All)
        {
            if (o == _ignored && now < _ignoredUntil) continue;
            float d = (o.transform.position - pos).sqrMagnitude;
            if (d < best) { best = d; seen = o; }
        }
        if (!seen) return;
        if (Random.value <= s.stickChance)
        {
            Current = State.Chase;
            Prey = seen;
        }
        else
        {
            _ignored = seen;
            _ignoredUntil = now + s.ignoreTime;
        }
    }

    // Keep out of the cells (their inner sphere; a little closer while chasing, to reach viruses on them).
    Vector3 Avoid(ImmuneSystem s, Vector3 pos)
    {
        Vector3 push = Vector3.zero;
        float margin = Current == State.Chase ? s.cellMargin * 0.4f : s.cellMargin;
        foreach (CellSignal c in CellSignal.All)
        {
            Vector3 centre = c.Centre(out float r);
            Vector3 away = pos - centre;
            float d = away.magnitude;
            if (d >= r + margin || d < 1e-4f) continue;
            push += away / d * (2f * (1f - Mathf.Max(0f, d - r) / margin));
        }
        return push;
    }

    // Grab on: arms toward the creature's middle, sat on its outside.
    void Stick(ImmuneSystem s)
    {
        Transform t = Prey.transform;
        Vector3 out_ = transform.position - t.position;
        out_ = out_.sqrMagnitude > 1e-6f ? out_.normalized : Random.onUnitSphere;
        Vector3 at = t.position + out_ * (Reach(Prey) + s.antibodySize * 0.4f);
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, -out_) * Quaternion.AngleAxis(Random.value * 360f, Vector3.up);
        _stuckLocal = t.InverseTransformPoint(at);
        _stuckRotation = Quaternion.Inverse(t.rotation) * rot;
        transform.SetPositionAndRotation(at, rot);
        Current = State.Stuck;
        _velocity = Vector3.zero;
        s_stuck[Prey] = StuckOn(Prey) + 1;
    }

    void Unstick()
    {
        if (Current != State.Stuck || !Prey) return;
        int n = StuckOn(Prey) - 1;
        if (n > 0) s_stuck[Prey] = n;
        else s_stuck.Remove(Prey);
    }

    // How far a creature's outside is from its middle (its colliders, else a guess).
    static readonly Dictionary<Organism, float> s_reach = new Dictionary<Organism, float>();
    static float Reach(Organism o)
    {
        if (s_reach.TryGetValue(o, out float r)) return r;
        Collider c = o.GetComponentInChildren<Collider>();
        r = c ? Mathf.Min(c.bounds.extents.x, Mathf.Min(c.bounds.extents.y, c.bounds.extents.z)) : 0.8f;
        s_reach[o] = r;
        return r;
    }
}

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
/// - Stuck: it reached the virus and holds on: it claims the free slot nearest where it touched (an
///   AntibodyHold per creature: rings round the body, a second shell over the first when full), climbs
///   onto it and hugs the body, riding the shown body (turns, lean, bob) until the virus is gone or
///   shakes it off: every stuck antibody has a random grip that quick turns and jolts wear down
///   (AntibodyHold.Shake); it loosens and lifts as it goes, then is flung off and leaves that virus alone
///   for a while. <see cref="StuckOn"/> counts them per virus.
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
    Vector3 _preyLast, _preyVelocity; // chase: where the prey was, and how it's moving (smoothed)

    // Stuck: the slot it holds, where it climbed on from (hold frame), its grip.
    AntibodyHold _hold;
    int _slot = -1;
    Vector3 _fromLocal;
    Quaternion _fromRotation;
    float _settle, _health, _healthMax, _wrap;

    float _agitation = 0.5f, _grip;

    /// <summary>When ImmuneSystem last ticked it (it ticks far ones every few frames).</summary>
    public float LastTick { get; set; }
    float _lastCarry = -1f;

    /// <summary>Moves it by its swimming velocity + the blood's flow since the last call. Separate from the
    /// (LOD'd) Tick so ImmuneSystem can move on-screen ones every frame while they only think every few: stepped
    /// with the tick, drifting ones juddered against the camera.</summary>
    public void Carry(float now)
    {
        float dt = _lastCarry < 0f ? 0f : Mathf.Min(now - _lastCarry, 0.25f);
        _lastCarry = now;
        if (dt <= 0f || Current == State.Stuck) return; // stuck: posed by Follow
        Vector3 pos = transform.position;
        Vector3 step = (_velocity + Vessel.FlowAt(pos)) * dt; // swims through the blood, carried by it
        if (step.sqrMagnitude > 1e-10f) transform.position = pos + step;
    }

    /// <summary>Per-instance shader data: seed, agitation (calm drifting .. frantic chasing), grip (arms
    /// wrapped round the body while stuck), wrap (hinge-to-centre in antibody sizes: what it bends round).</summary>
    public Vector4 Wiggle => new Vector4(_seed, _agitation, _grip, _wrap);

    /// <summary>How many antibodies are holding on to this creature.</summary>
    public static int StuckOn(Organism o) => AntibodyHold.CountOn(o);

    void Awake() => _seed = Random.value * 100f;

    void OnDisable() => Unstick();

    public void Tick(ImmuneSystem s, float dt, float now)
    {
        float agitation = Current switch { State.Patrol => 0.8f, State.Chase => 1.6f, State.Stuck => 1f, _ => 0.5f };
        _agitation = Mathf.MoveTowards(_agitation, agitation, dt * 1.5f);
        if (Current != State.Stuck) _grip = Mathf.MoveTowards(_grip, 0f, dt * 4f);

        if (Current == State.Stuck)
        {
            _lastCarry = now; // posed by Follow meanwhile: shaken off, it swims on from here
            if (!Prey || !Prey.isActiveAndEnabled)
            {
                Unstick();
                Current = State.Drift;
            }
            return; // posed by Follow, after the creature has moved
        }

        Vector3 pos = transform.position;
        if (now >= _nextLook)
        {
            _nextLook = now + s.lookInterval * Random.Range(0.7f, 1.3f); // staggered
            Look(s, pos, now);
        }

        Vector3 desire;
        float speed = s.speed, accel = s.acceleration;
        bool dive = false;
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
                Vector3 at = Prey.transform.position;
                Vector3 to = at - pos;
                float dist = to.magnitude;
                BodyHull hull = BodyHull.For(Prey);
                if ((pos - hull.Centre).magnitude <= hull.RadiusToward(pos) + s.stickDistance)
                {
                    Stick(s);
                    return;
                }
                // Aim where it's going (a crawling virus kinematic on a cell has no Rigidbody velocity: measured).
                if (dt > 1e-4f) _preyVelocity = Vector3.Lerp(_preyVelocity, (at - _preyLast) / dt, 1f - Mathf.Exp(-6f * dt));
                _preyLast = at;
                speed *= s.chaseBoost;
                float lead = Mathf.Min(s.chaseLead, dist / Mathf.Max(speed, 0.1f));
                desire = (to + _preyVelocity * lead).normalized;
                accel = s.chaseAcceleration;
                dive = dist < s.diveDistance; // close: straight in, however near the cell
            }
        }
        else desire = Wander(s, pos, now);

        if (!dive) desire += Avoid(s, pos);
        _velocity = Vector3.MoveTowards(_velocity, Vector3.ClampMagnitude(desire, 1f) * speed, accel * dt);
        Carry(now); // moves by _velocity + flow (and every frame on screen, from ImmuneSystem)

        // Tumble along, turning to face where it's going.
        _spin += dt * (Current == State.Chase ? 150f : 30f);
        Quaternion face = _velocity.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(_velocity) : transform.rotation;
        transform.rotation = Quaternion.Slerp(transform.rotation, face * Quaternion.Euler(90f, _spin, 0f), dt * 3f);
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
            _preyLast = seen.transform.position;
            _preyVelocity = Vector3.zero;
        }
        else Ignore(seen, now + s.ignoreTime);
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

    // Grab on: claim the free slot nearest where it touched and start climbing onto it. A creature
    // already covered all round is left alone for a while.
    void Stick(ImmuneSystem s)
    {
        AntibodyHold hold = AntibodyHold.For(Prey, s.antibodySize);
        int slot = hold.Claim(transform.position);
        if (slot < 0)
        {
            Ignore(Prey, Time.time + s.ignoreTime);
            Current = State.Drift;
            Prey = null;
            return;
        }
        _hold = hold;
        _slot = slot;
        _fromLocal = hold.ToLocal(transform.position);
        _fromRotation = Quaternion.Inverse(hold.Frame.rotation) * transform.rotation;
        _settle = 0f;
        _wrap = hold.Wrap(slot);
        _healthMax = _health = Random.Range(s.gripHealth.x, s.gripHealth.y);
        Current = State.Stuck;
        _velocity = Vector3.zero;
    }

    /// <summary>Stuck: ride the creature (ImmuneSystem.LateUpdate, once it has moved this frame), climb
    /// onto the slot, and lose grip while it's shaken; flung off when the grip is gone.</summary>
    public void Follow(ImmuneSystem s, float dt)
    {
        if (Current != State.Stuck || _hold == null || !_hold.Frame) return;
        float shake = _hold.Shake;
        // Worn down by shaking; held still it slowly takes hold again, so it takes a real effort.
        _health = shake > 0.02f ? _health - shake * dt : Mathf.Min(_healthMax, _health + s.regrip * dt);
        if (_health <= 0f)
        {
            ShakeOff(s);
            return;
        }

        // Loosening: arms open, it lifts off and rattles as the grip goes (so you can see it working).
        float hold = Mathf.Clamp01(_health / Mathf.Max(_healthMax, 1e-3f));
        float shaken = Mathf.Min(shake, 1f);
        _grip = Mathf.MoveTowards(_grip, Mathf.Lerp(0.35f, 1f, hold), dt * 4f);
        _agitation = Mathf.Max(_agitation, 1f + 1.5f * shaken);
        float size = transform.lossyScale.x, loose = 1f - hold;
        float lift = size * loose * (0.3f * Mathf.Min(shake * 2f, 1f) + 0.06f * shaken * Mathf.Sin(Time.time * 40f + _seed));

        _hold.Pose(_slot, lift, out Vector3 pos, out Quaternion rot);
        if (_settle < 1f)
        {
            // Climb from where it touched round to its slot (an arc round the centre, not through it).
            _settle = Mathf.Min(1f, _settle + dt / Mathf.Max(s.settleTime, 1e-3f));
            float k = _settle * _settle * (3f - 2f * _settle);
            pos = _hold.ToWorld(Vector3.Slerp(_fromLocal, _hold.SlotLocal(_slot, lift), k));
            rot = Quaternion.Slerp(_hold.Frame.rotation * _fromRotation, rot, k);
        }
        transform.SetPositionAndRotation(pos, rot);
    }

    // Grip gone: flung off the way the body was going, and it leaves that creature be for a while.
    void ShakeOff(ImmuneSystem s)
    {
        Organism prey = Prey;
        Vector3 away = transform.position - (prey ? prey.transform.position : transform.position);
        away = away.sqrMagnitude > 1e-6f ? away.normalized : Random.onUnitSphere;
        Vector3 carried = _hold != null ? _hold.Velocity : Vector3.zero;
        Unstick();
        Current = State.Drift;
        Prey = null;
        Ignore(prey, Time.time + s.shakenIgnore);
        _velocity = carried + (away + Random.insideUnitSphere * 0.4f) * s.flingSpeed;
        _agitation = 2f;
    }

    void Ignore(Organism o, float until)
    {
        _ignored = o;
        _ignoredUntil = until;
    }

    void Unstick()
    {
        _hold?.Release(_slot);
        _hold = null;
        _slot = -1; // _wrap stays: it unbends as the grip eases off
    }
}

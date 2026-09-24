using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A white blood cell (a phagocyte): it only eats pathogens. A big, walkable ball (a Surface: you can
/// land and crawl on it) that crawls between the cells like an amoeba and has exactly one absorbing
/// spot, the tip of a pseudopod it stretches out (<see cref="Spot"/>). Anything the spot touches, a
/// virus in the air, on another cell or on this one, is swallowed. It can move the spot, over its own
/// body or out on a stretched arm, but only so fast: you can see it coming and keep away.
/// - Patrol: crawls to a cell nearby (loud cells, CellSignal, are likelier), then
/// - Examine: sits by it a while, feeling over its surface with the spot.
/// - Hunt: it senses a virus within <see cref="senseRange"/> of its surface, much further for each
///   antibody stuck on it (<see cref="antibodyRange"/>; Antibody.StuckOn). It crawls close, reaches
///   the spot at it; a virus standing on it gets the spot crawling over the body after it.
/// - Engulf: the arm draws the virus in and closes over it (WhiteBloodCells.Capture / Finish: an AI
///   virus is destroyed, the player respawns).
/// - Digest: a few quiet seconds, then back to patrolling.
/// No Update of its own: WhiteBloodCells ticks every cell in one loop and draws them all in one
/// instanced call (Custom/WhiteBloodCell); this keeps what the shader needs (<see cref="Reach"/>,
/// <see cref="Motion"/>, <see cref="Mood"/>). Its MeshRenderer is never drawn: it's the walkable mesh
/// (Surface) and the command-mode box. Moved kinematically; it never turns, so nothing standing on it spins.
/// Cost per cell: O(nearby organisms) per think (WhiteBloodCells' grid), one overlap query per think.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class WhiteBloodCell : MonoBehaviour
{
    public enum State { Patrol, Examine, Hunt, Engulf, Digest }

    [Header("Crawling")]
    [Min(0f)] public float speed = 2.5f;
    [Min(0f)] public float huntSpeed = 4.5f;
    [Min(0.01f)] public float acceleration = 2f;
    [Min(1f), Tooltip("How far it looks for the next cell to examine.")]
    public float patrolRange = 70f;
    [Min(0f), Tooltip("Gap kept between it and other cells.")]
    public float gap = 2f;
    [Tooltip("Seconds spent examining a cell (min, max).")]
    public Vector2 examineTime = new Vector2(4f, 9f);
    [Min(0f), Tooltip("Pull of a loud cell (CellSignal) when picking the next one to examine.")]
    public float signalWeight = 0.15f;

    [Header("Sensing")]
    [Min(0f), Tooltip("Senses a virus this far from its surface (always one touching it).")]
    public float senseRange = 14f;
    [Min(0f), Tooltip("Extra sense range for each antibody stuck on a virus.")]
    public float antibodyRange = 10f;
    [Min(0), Tooltip("Antibodies counted at most.")]
    public int antibodyCap = 5;
    [Min(0f), Tooltip("Gives up after this long without sensing its prey.")]
    public float loseTime = 3f;
    [Min(0.02f)] public float thinkInterval = 0.15f;

    [Header("Absorbing spot")]
    [Min(0f), Tooltip("Farthest the pseudopod stretches past the body's surface (m).")]
    public float maxReach = 10f;
    [Min(0.1f), Tooltip("How fast the spot moves across the body or swings round (m/s).")]
    public float spotSpeed = 3.5f;
    [Min(0.1f), Tooltip("How fast the pseudopod stretches and pulls back (m/s).")]
    public float extendSpeed = 5f;
    [Min(0.05f), Tooltip("Radius of the mouth at the tip, in body radii (the shader draws it this size).")]
    public float mouthSize = 0.28f;
    [Min(0f), Tooltip("A virus is swallowed only when it touches the mouth: within the mouth's width and this far " +
                      "(m, past its own size) in front of it.")]
    public float touchMargin = 0.5f;
    [Min(0.1f), Tooltip("Seconds to wrap round the prey, pull it in and close.")]
    public float engulfTime = 2.4f;
    [Min(0f)] public float digestTime = 4f;

    public State Current { get; private set; }
    /// <summary>What it's hunting or swallowing.</summary>
    public Organism Prey { get; private set; }
    public float Radius => transform.lossyScale.x * 0.5f; // the walkable mesh and collider are radius 0.5
    /// <summary>The absorbing spot (world): the pseudopod's tip.</summary>
    public Vector3 Spot => transform.position + _reachDir * (Radius + _reach);
    /// <summary>Shader data: spot direction (world) and how far it's stretched, in radii.</summary>
    public Vector4 Reach => new Vector4(_reachDir.x, _reachDir.y, _reachDir.z, _reach / Mathf.Max(Radius, 1e-3f));
    /// <summary>Shader data: crawl velocity over the hunting speed, and a seed.</summary>
    public Vector4 Motion
    {
        get
        {
            Vector3 m = _velocity / Mathf.Max(huntSpeed, 0.01f);
            return new Vector4(m.x, m.y, m.z, _seed);
        }
    }
    /// <summary>Shader data: hunger (wisps out, mouth open and glowing), wrap (lips closing round the prey),
    /// mouth radius and prey radius (both in body radii).</summary>
    public Vector4 Mood => new Vector4(_hunger, _gulp, mouthSize, _preySize / Mathf.Max(Radius, 1e-3f));

    /// <summary>When WhiteBloodCells last ticked it (far ones tick every few frames).</summary>
    public float LastTick { get; set; }

    Vector3 _velocity, _reachDir = Vector3.forward, _avoid, _bodyGoal;
    float _reach, _seed, _hunger = 0.25f, _gulp, _nextThink, _stateUntil, _lastSensed;
    Transform _examined;
    float _examinedRadius;
    Transform _lastExamined;
    Vector3 _captureFrom; // prey's start, in this cell's space
    float _captureReach, _engulf, _preySize;
    bool _preyOnMe;
    Rigidbody _rb;

    static readonly Collider[] s_hits = new Collider[64];
    static readonly List<Organism> s_near = new List<Organism>();

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.isKinematic = true;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.None;
        if (TryGetComponent(out MeshRenderer r)) r.forceRenderingOff = true; // drawn by WhiteBloodCells
        if (TryGetComponent(out Surface s)) s.isCell = false; // walkable, but no signal and not a "Cell" target
        _seed = Random.value * 100f;
        _reachDir = Random.onUnitSphere;
    }

    void Start()
    {
        if (!GetComponent<Selectable>()) Selectable.Add(gameObject, Selectable.Category.Target, "White Cell", CommandBoard.Jobs.MoveTo);
        _bodyGoal = transform.position;
    }

    void OnEnable() => WhiteBloodCells.Register(this);

    void OnDisable()
    {
        if (Current == State.Engulf && Prey) WhiteBloodCells.Finish(this, Prey); // don't leave it half swallowed
        WhiteBloodCells.Unregister(this);
    }

    public void Tick(float dt, float now)
    {
        Vector3 c = transform.position;
        float R = Radius;
        if (now >= _nextThink)
        {
            _nextThink = now + thinkInterval * Random.Range(0.8f, 1.2f); // staggered
            Think(c, R, now);
        }

        Vector3 spotGoal;
        float crawl = speed, hunger;
        switch (Current)
        {
            case State.Engulf:
                Engulfing(c, R, dt);
                Crawl(c, c, 0f, dt);
                return;

            case State.Hunt:
                if (!Prey || !Prey.isActiveAndEnabled) { Current = State.Patrol; Prey = null; goto default; }
                spotGoal = Prey.transform.position;
                crawl = huntSpeed;
                hunger = 1f;
                if (Touching(Prey))
                {
                    StartEngulf(Prey);
                    return;
                }
                break;

            case State.Examine:
                spotGoal = Probe(c, now);
                hunger = 0.55f;
                crawl = speed * 0.4f;
                break;

            case State.Digest:
                spotGoal = c + Lead(now) * R;
                hunger = 0.05f;
                crawl = speed * 0.5f;
                break;

            default: // patrol
                spotGoal = c + Lead(now) * (R + 0.4f);
                hunger = 0.25f;
                break;
        }

        _hunger = Mathf.MoveTowards(_hunger, hunger, dt * 1.5f);
        _gulp = Mathf.MoveTowards(_gulp, 0f, dt);
        Crawl(c, _bodyGoal, crawl, dt);
        MoveSpot(transform.position, R, spotGoal, dt);
    }

    // ---------------- deciding (every thinkInterval) ----------------

    void Think(Vector3 c, float R, float now)
    {
        Avoidance(c, R);
        if (Current == State.Engulf) return;

        if (Current == State.Digest)
        {
            if (now >= _stateUntil) { Current = State.Patrol; _examined = null; }
            return;
        }

        // Anything the mouth touches is swallowed, prey or not.
        WhiteBloodCells.Near(Spot, mouthSize * R + touchMargin + 3f, s_near);
        foreach (Organism o in s_near)
            if (Touching(o))
            {
                StartEngulf(o);
                return;
            }

        Sense(c, R, now);
        if (Current == State.Hunt) { HuntGoal(c, R); return; }

        if (Current == State.Examine)
        {
            if (now >= _stateUntil || !_examined) { Current = State.Patrol; _lastExamined = _examined; _examined = null; }
            return;
        }

        // Patrol: head for a cell to examine; close enough, start.
        if (!_examined) PickCell(c, R);
        if (!_examined) { _bodyGoal = c + Lead(now) * 5f; return; }
        Vector3 cc = CellCentre(_examined, out _examinedRadius);
        Vector3 away = c - cc;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : Random.onUnitSphere;
        _bodyGoal = cc + away * (_examinedRadius + R + gap);
        if ((c - _bodyGoal).magnitude < R * 0.5f + gap)
        {
            Current = State.Examine;
            _stateUntil = now + Random.Range(examineTime.x, Mathf.Max(examineTime.x, examineTime.y));
        }
    }

    // The nearest virus it can sense (much further with antibodies on it); keeps its prey until lost.
    void Sense(Vector3 c, float R, float now)
    {
        float reach = R + senseRange + antibodyRange * antibodyCap;
        WhiteBloodCells.Near(c, reach, s_near);
        Organism best = null;
        float bestScore = float.MaxValue;
        foreach (Organism o in s_near)
        {
            float d = (o.transform.position - c).magnitude - R;
            float range = senseRange + antibodyRange * Mathf.Min(Antibody.StuckOn(o), antibodyCap);
            bool touching = o.OnSurface && o.Surface == transform;
            if (d > range && !touching) continue;
            float score = touching ? -1f : d / Mathf.Max(range, 0.01f);
            if (o == Prey) score -= 0.3f; // stays on its prey
            if (score < bestScore) { bestScore = score; best = o; }
        }

        if (best)
        {
            _lastSensed = now;
            if (Current != State.Hunt || best != Prey)
            {
                bool fresh = Current != State.Hunt;
                Prey = best;
                Current = State.Hunt;
                if (fresh) WhiteBloodCells.RaiseNoticed(this, best);
            }
        }
        else if (Current == State.Hunt && now - _lastSensed > loseTime)
        {
            Current = State.Patrol;
            Prey = null;
        }
    }

    // Where the body goes while hunting: close enough for the arm to reach, over the prey's surface
    // if it stands on one (so it comes round a cell instead of reaching through it).
    void HuntGoal(Vector3 c, float R)
    {
        Vector3 p = Prey.transform.position;
        _preyOnMe = Prey.OnSurface && Prey.Surface == transform;
        if (_preyOnMe) { _bodyGoal = c; return; }
        float hold = R + maxReach * 0.4f;
        if (Prey.OnSurface) _bodyGoal = p + Prey.SurfaceNormal * hold;
        else
        {
            Vector3 to = p - c;
            float d = to.magnitude;
            _bodyGoal = d > 1e-3f ? p - to / d * hold : c;
        }
    }

    // A cell nearby to examine, likelier the louder it is; not the one just examined.
    void PickCell(Vector3 c, float R)
    {
        int n = Physics.OverlapSphereNonAlloc(c, patrolRange, s_hits, ~0, QueryTriggerInteraction.Ignore);
        float total = 0f;
        Transform pick = null;
        for (int i = 0; i < n; i++)
        {
            Surface s = s_hits[i].GetComponentInParent<Surface>();
            if (!s || !s.isCell || s.transform == transform || s.transform == _lastExamined) continue;
            CellSignal signal = CellSignal.For(s.transform);
            float weight = 1f + (signal ? signal.Signal * signalWeight : 0f);
            total += weight;
            if (Random.value * total <= weight) pick = s.transform; // reservoir pick
        }
        _examined = pick;
    }

    // Other colliders it's closing on (cells, rocks; not creatures): pushed off, softly.
    void Avoidance(Vector3 c, float R)
    {
        _avoid = Vector3.zero;
        int n = Physics.OverlapSphereNonAlloc(c, R + gap + 3f, s_hits, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider col = s_hits[i];
            if (col.transform.IsChildOf(transform)) continue;
            Rigidbody body = col.attachedRigidbody;
            if (body && body.GetComponent<Organism>()) continue;
            // ClosestPoint only takes primitives and convex meshes; a concave mesh uses its bounds.
            Vector3 closest = col is MeshCollider mc && !mc.convex ? col.bounds.ClosestPoint(c) : col.ClosestPoint(c);
            Vector3 away = c - closest;
            float d = away.magnitude;
            if (d < 1e-3f) { away = c - col.bounds.center; d = Mathf.Max(away.magnitude, 1e-3f); }
            float clearance = d - R;
            if (clearance >= gap + 3f) continue;
            _avoid += away / d * (1.5f * (1f - Mathf.Clamp01(clearance / (gap + 3f))));
        }
    }

    // ---------------- moving ----------------

    // Amoeboid crawl: surges forward and eases, steering round other cells.
    void Crawl(Vector3 c, Vector3 goal, float top, float dt)
    {
        Vector3 to = goal - c;
        float d = to.magnitude;
        Vector3 desire = d > 1e-3f ? to / d * Mathf.Clamp01(d / Mathf.Max(Radius, 1f)) : Vector3.zero;
        desire = Vector3.ClampMagnitude(desire + _avoid, 1f);
        float surge = 0.55f + 0.45f * Mathf.Sin(Time.time * 1.7f + _seed) * Mathf.Sin(Time.time * 1.7f + _seed);
        _velocity = Vector3.MoveTowards(_velocity, desire * top * surge, acceleration * dt);
        if (_velocity.sqrMagnitude < 1e-8f) return;
        transform.position = c + _velocity * dt;
    }

    // The spot swings toward its goal at spotSpeed (over the body, or with the arm out), stretching
    // the arm to reach it; it pulls the arm in to swing round far.
    void MoveSpot(Vector3 c, float R, Vector3 goal, float dt)
    {
        Vector3 to = goal - c;
        float d = to.magnitude;
        if (d < 1e-3f) return;
        Vector3 dir = to / d;
        float angle = Vector3.Angle(_reachDir, dir);
        _reachDir = Vector3.RotateTowards(_reachDir, dir, spotSpeed / (R + _reach) * dt, 0f).normalized;
        float want = Mathf.Clamp(d - R, 0f, maxReach) * (1f - Mathf.InverseLerp(12f, 40f, angle));
        if (Current == State.Hunt && !_preyOnMe && Blocked(c, R)) want = Mathf.Min(want, _reach); // don't push through a cell
        _reach = Mathf.MoveTowards(_reach, want, extendSpeed * dt);
    }

    // Something solid between the body and the spot's next stretch (another cell).
    bool Blocked(Vector3 c, float R)
    {
        Vector3 from = c + _reachDir * (R + 0.2f);
        if (!Physics.Raycast(from, _reachDir, out RaycastHit hit, _reach + 1f, ~0, QueryTriggerInteraction.Ignore)) return false;
        if (hit.collider.transform.IsChildOf(transform)) return false;
        return !(Prey && hit.collider.transform.IsChildOf(Prey.transform));
    }

    // Idle spot: toward where it crawls, drifting about.
    Vector3 Lead(float now)
    {
        float t = now * 0.2f + _seed;
        Vector3 wander = new Vector3(Mathf.PerlinNoise(t, _seed) - 0.5f, Mathf.PerlinNoise(_seed, t) - 0.5f, Mathf.PerlinNoise(t + 17.3f, _seed + 5.1f) - 0.5f);
        return (_velocity.normalized * 0.8f + wander * 1.6f + _reachDir * 0.5f).normalized;
    }

    // Examining: the spot presses on the cell and feels slowly across the side facing it.
    Vector3 Probe(Vector3 c, float now)
    {
        if (!_examined) return c + Lead(now) * Radius;
        Vector3 cc = CellCentre(_examined, out float r);
        Vector3 toUs = (c - cc).normalized;
        Vector3 a = Vector3.Cross(toUs, Vector3.up);
        if (a.sqrMagnitude < 1e-4f) a = Vector3.Cross(toUs, Vector3.right);
        a.Normalize();
        Vector3 b = Vector3.Cross(toUs, a);
        float t = now + _seed;
        Vector3 dir = Quaternion.AngleAxis(Mathf.Sin(t * 0.37f) * 30f, a) * Quaternion.AngleAxis(Mathf.Sin(t * 0.29f) * 30f, b) * toUs;
        return cc + dir * r;
    }

    // ---------------- swallowing ----------------

    // The mouth is on it: within the mouth's width across, and touching its face (a little behind the tip,
    // in the cup, up to the prey's size + touchMargin in front of it).
    bool Touching(Organism o)
    {
        if (!o || !o.isActiveAndEnabled) return false;
        float s = Size(o), m = mouthSize * Radius;
        Vector3 off = o.transform.position - Spot;
        float along = Vector3.Dot(off, _reachDir);
        float across = (off - _reachDir * along).magnitude;
        return across <= m * 0.85f + s * 0.5f && along <= s + touchMargin && along >= -m - s;
    }

    void StartEngulf(Organism o)
    {
        if (!WhiteBloodCells.Capture(this, o)) return;
        Prey = o;
        Current = State.Engulf;
        _engulf = 0f;
        _captureReach = _reach;
        _captureFrom = transform.InverseTransformPoint(o.transform.position);
        _preySize = Size(o);
    }

    // Settled into the mouth, the lips flow round it until they meet, the arm pulls back with it, and it
    // sinks in as the lips let go; shrunk as it goes.
    void Engulfing(Vector3 c, float R, float dt)
    {
        _engulf += dt / engulfTime;
        _hunger = Mathf.MoveTowards(_hunger, 1f, dt * 2f);
        _gulp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.02f, 0.4f, _engulf)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.8f, 1f, _engulf)));
        _reach = _captureReach * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 0.75f, _engulf)));

        if (!Prey) { EndEngulf(); return; }
        Vector3 tip = c + _reachDir * (R + _reach);
        Vector3 inside = c + _reachDir * (R * 0.3f);
        Vector3 at = _engulf < 0.15f
            ? Vector3.Lerp(transform.TransformPoint(_captureFrom), tip, Mathf.SmoothStep(0f, 1f, _engulf / 0.15f))
            : Vector3.Lerp(tip, inside, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, _engulf)));
        WhiteBloodCells.Hold(Prey, at, Mathf.Lerp(1f, 0.5f, Mathf.InverseLerp(0.75f, 1f, _engulf)));
        if (_engulf >= 1f)
        {
            WhiteBloodCells.Finish(this, Prey);
            EndEngulf();
        }
    }

    void EndEngulf()
    {
        Prey = null;
        _gulp = 0f;
        _preySize = 0f;
        Current = State.Digest;
        _stateUntil = Time.time + digestTime;
        _examined = null;
    }

    // ---------------- helpers ----------------

    static Vector3 CellCentre(Transform cell, out float radius)
    {
        CellSignal signal = CellSignal.For(cell);
        if (signal) return signal.Centre(out radius);
        radius = cell.lossyScale.x * 0.5f;
        return cell.position;
    }

    // How far a creature's outside is from its middle (its colliders, else a guess).
    static readonly Dictionary<Organism, float> s_size = new Dictionary<Organism, float>();
    static float Size(Organism o)
    {
        if (s_size.TryGetValue(o, out float r)) return r;
        Collider c = o.GetComponentInChildren<Collider>();
        r = c ? Mathf.Max(c.bounds.extents.x, Mathf.Max(c.bounds.extents.y, c.bounds.extents.z)) : 0.8f;
        s_size[o] = r;
        return r;
    }
}

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
/// - Grip: the mouth has hold of it and the arm reels it in with real forces (<see cref="Pull"/>, in the
///   physics step): it can still swim against the pull (it loses slowly), and a sudden jerk (a Burst) drags
///   it past <see cref="breakStretch"/> and tears it free. The arm stretches after it wherever it goes.
/// - Engulf: at the body, the lips close over it and it sinks in (WhiteBloodCells.Capture / Finish: an AI
///   virus is destroyed, the player respawns).
/// - Digest: a few quiet seconds, then back to patrolling.
/// The arm is sprung, not tweened: its angle and stretch each follow a damped spring (<see cref="spotSpring"/>),
/// speed-capped, so it accelerates, overshoots a little and settles, and the shader bows it behind its motion.
/// No Update of its own: WhiteBloodCells ticks every cell in one loop and draws them all in one
/// instanced call (Custom/WhiteBloodCell); this keeps what the shader needs (<see cref="Reach"/>,
/// <see cref="Motion"/>, <see cref="Mood"/>). Its MeshRenderer is never drawn: it's the walkable mesh
/// (Surface) and the command-mode box. Moved kinematically; it never turns, so nothing standing on it spins.
/// Cost per cell: O(nearby organisms) per think (WhiteBloodCells' grid), one overlap query per think.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class WhiteBloodCell : MonoBehaviour, IWorldState
{
    public enum State { Patrol, Examine, Hunt, Grip, Engulf, Digest }

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
    [Min(0f), Tooltip("Seconds it stays on a new prey before it may turn to another (unless one touches it or the prey " +
                      "is lost). In a crowd it otherwise flipped between viruses and the arm swung back and forth.")]
    public float commitTime = 1.5f;
    [Min(0f), Tooltip("How much better (in sense-range fractions) another virus must score to take over from its prey.")]
    public float switchMargin = 0.35f;
    [Min(0f), Tooltip("Score added per other white cell already after a virus, so a group spreads over a crowd " +
                      "instead of all piling on the nearest one.")]
    public float sharePenalty = 0.4f;
    [Min(0.02f)] public float thinkInterval = 0.15f;

    [Header("Absorbing spot")]
    [Min(0f), Tooltip("Farthest the pseudopod stretches past the body's surface (m).")]
    public float maxReach = 10f;
    [Min(0.1f), Tooltip("How fast the spot moves across the body or swings round (m/s).")]
    public float spotSpeed = 3.5f;
    [Min(0.1f), Tooltip("Fastest the pseudopod stretches and pulls back (m/s).")]
    public float extendSpeed = 5f;
    [Min(0.05f), Tooltip("How springy the arm is (Hz): it eases toward where it's aimed, overshoots a little and settles.")]
    public float spotSpring = 0.7f;
    [Range(0.1f, 1.5f), Tooltip("Damping of that spring: under 1 overshoots (floppy), 1 and over settles without.")]
    public float spotDamping = 0.5f;
    [Min(0.05f), Tooltip("Radius of the mouth at the tip, in body radii (the shader draws it this size).")]
    public float mouthSize = 0.28f;
    [Min(0f), Tooltip("A virus is swallowed only when it touches the mouth: within the mouth's width and this far " +
                      "(m, past its own size) in front of it.")]
    public float touchMargin = 0.5f;

    [Header("Grabbing")]
    [Min(0f), Tooltip("Most the arm pulls its catch with (m/s², on top of the catch's own swimming).")]
    public float gripStrength = 130f;
    [Min(0f), Tooltip("Pull per metre the catch is behind where the arm is reeling it to (1/s²).")]
    public float gripStiffness = 40f;
    [Min(0.1f), Tooltip("How fast the arm reels its catch in (m/s), slowed while the catch lags behind.")]
    public float reelSpeed = 3f;
    [Min(0f), Tooltip("Dragged this far (m) past where the arm is holding it (or past the arm's full reach), the grip " +
                      "tears and the catch is free. Steady swimming can't pull that far against the grip; a Burst can.")]
    public float breakStretch = 4f;
    [Min(0f), Tooltip("Seconds after a catch tears free before it can grab again.")]
    public float regrabDelay = 1.2f;
    [Range(0f, 90f), Tooltip("The catch stays turned the way it was bitten, relative to the arm (it swings round with the " +
                             "arm); it can only turn this far (degrees) from that while held.")]
    public float gripTurn = 25f;
    [Min(0.1f), Tooltip("Seconds for the catch to merge into the body: it settles onto the surface where it touched, " +
                        "sinks under and the skin over it flattens into the body.")]
    public float swallowTime = 2.2f;
    [Min(0.1f), Tooltip("How springy the catch is as it merges (Hz): it keeps the speed it came in with, squashes " +
                        "into the surface and settles.")]
    public float mergeSpring = 1.4f;

    [Header("Bite feel")]
    [Min(0f), Tooltip("Metres from the mouth at which it starts gaping open for the catch.")]
    public float gapeRange = 4f;
    [Min(0.1f), Tooltip("How snappy the lips are (Hz). They close on a spring and bounce off the catch.")]
    public float lipSpring = 2.2f;
    [Range(0.05f, 1f), Tooltip("Damping of the lips: lower wobbles longer after they snap shut.")]
    public float lipDamping = 0.3f;
    [Min(0f), Tooltip("Squeeze kicked into the mouth on the bite (it jiggles and settles), and a smaller one per gulp while it pulls.")]
    public float biteSqueeze = 1.2f;
    [Min(0.1f), Tooltip("Seconds between gulps while it reels a catch in.")]
    public float gulpInterval = 0.9f;
    [Min(0f)] public float digestTime = 4f;

    public State Current { get; private set; }
    /// <summary>What it's hunting or swallowing.</summary>
    public Organism Prey
    {
        get => _prey;
        private set { if (ReferenceEquals(_prey, value)) return; Claim(_prey, -1); _prey = value; Claim(value, 1); }
    }
    Organism _prey;
    float _huntSince;

    // How many white cells are after each organism (their Prey), kept by Prey's setter: O(1) per query.
    static readonly Dictionary<Organism, int> s_hunters = new Dictionary<Organism, int>();
    static void Claim(Organism o, int d)
    {
        if (ReferenceEquals(o, null)) return; // a destroyed one still counts down
        s_hunters.TryGetValue(o, out int n);
        n += d;
        if (n <= 0) s_hunters.Remove(o); else s_hunters[o] = n;
    }
    static int Hunters(Organism o) => s_hunters.TryGetValue(o, out int n) ? n : 0;
    public float Radius => transform.lossyScale.x * 0.5f; // the walkable mesh and collider are radius 0.5
    /// <summary>The absorbing spot (world): the pseudopod's tip.</summary>
    public Vector3 Spot => transform.position + _reachDir * (Radius + _reach);
    /// <summary>Shader data: spot direction (world) and how far it's stretched, in radii.</summary>
    public Vector4 Reach => new Vector4(_reachDir.x, _reachDir.y, _reachDir.z, _reach / Mathf.Max(Radius, 1e-3f));
    /// <summary>Shader data: how fast the spot moves (world, radii/s; the arm bows behind it), and grip tension 0..1.</summary>
    public Vector4 Sway => new Vector4(_tipVel.x, _tipVel.y, _tipVel.z, _tension);
    /// <summary>Shader data: the reach frame's x axis (world), carried along with the arm as it swings (parallel
    /// transport, <see cref="Track"/>), so what's laid out round the arm (wisps, lip, meander, mouth folds) never flips.</summary>
    public Vector4 Side => _side;
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
    /// mouth radius and prey radius (both in body radii). Merging, the lips' wrap melts away over the first
    /// quarter while the body's own surface takes over (MergeShape).</summary>
    public Vector4 Mood => new Vector4(_hunger, _gulp * (Current == State.Engulf ? 1f - Mathf.SmoothStep(0f, 1f, _swallow / 0.25f) : 1f),
                                       mouthSize, _preySize / Mathf.Max(Radius, 1e-3f));
    /// <summary>Shader data while merging (else 0): the catch as a blob the body's surface flows round, blended
    /// in like two drops joining: blob radius, its centre's distance from the cell's centre (along the reach),
    /// the neck's softness (all in body radii), and its height squash (1 = round, lower = flattened).</summary>
    public Vector4 MergeShape => _mergeShape;
    /// <summary>Shader data: gape (0..1, the mouth peeling open for a catch close by) and squeeze (a jiggling
    /// spring, kicked on the bite and each gulp).</summary>
    public float Gape => _gape;
    public float Squeeze => _clench;
    /// <summary>How hard the arm is pulling on its catch (0..1).</summary>
    public float Tension => _tension;

    /// <summary>When WhiteBloodCells last ticked it (far ones tick every few frames).</summary>
    public float LastTick { get; set; }

    Vector3 _drift; // the blood's flow it was carried by last crawl (on top of _velocity)
    Vector3 _velocity, _reachDir = Vector3.forward, _avoid, _bodyGoal;
    float _reach, _seed, _hunger = 0.25f, _gulp, _nextThink, _stateUntil, _lastSensed;
    Vector3 _spin, _tipVel, _lastTip; // arm: angular velocity (rad/s); spot velocity for the shader (radii/s)
    Vector3 _side = Vector3.right, _sideDir = Vector3.forward; // the reach frame's x axis, and the reach it was carried to
    float _reachVel, _reel, _tension, _regrabAt;
    float _gulpVel, _gape, _clench, _clenchVel, _nextGulp; // bite: lip spring, gape, squeeze spring
    Transform _examined;
    float _examinedRadius;
    Transform _lastExamined;
    float _swallow, _preySize, _squash, _squashVel; // _squash: the catch flattened (0 round, + flat, - tall)
    Vector4 _mergeShape;
    Vector3 _held, _heldVel; // merging: the catch's offset from the centre and its velocity (cell's rotation frame)
    Vector3 _aimSlack;       // the mouth's offset from the catch's middle, eased away (the mouth never jumps onto it)
    Quaternion _gripHold;    // the catch's rotation in the arm's frame when bitten (it's held that way)
    bool _preyOnMe;
    Rigidbody _rb;

    static readonly Collider[] s_hits = new Collider[256]; // a concave cell is many pieces (Surface.Collider.cs)
    static readonly HashSet<Object> s_seen = new HashSet<Object>();
    static readonly List<(Object body, Vector3 push)> s_pushes = new List<(Object, Vector3)>();
    static readonly List<Organism> s_near = new List<Organism>();
    static readonly RaycastHit[] s_rayHits = new RaycastHit[64];

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
        _sideDir = _reachDir;
        _side = Organism.AnyPerpendicular(_reachDir);
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
        if (Current == State.Grip && Prey) WhiteBloodCells.Release(Prey);
        WhiteBloodCells.Unregister(this);
        Prey = null; // gives up its claim
    }

    // Streaming (WorldStreamer): the pose is all it keeps; it isn't streamed out while it holds something.
    string IWorldState.SaveState() => "";
    void IWorldState.LoadState(string state) { }
    bool IWorldState.Pinned => Current == State.Grip || Current == State.Engulf || Current == State.Digest;

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
        float crawl = speed, hunger, gape = 0f;
        switch (Current)
        {
            // The body moves first, so the arm is aimed from where it's drawn this frame (else the mouth and the
            // merging blob trail the catch by the body's velocity x dt).
            case State.Engulf:
                Crawl(c, c, 0f, dt);
                Engulfing(transform.position, R, dt);
                Track(R, dt);
                Bite(1f, 0f, dt); // shut; the wrap deflates with what's still out (Mood)
                return;

            case State.Grip:
                Crawl(c, c, 0f, dt);
                Gripping(transform.position, R, dt, now);
                Track(R, dt);
                if (Current == State.Grip) Prey.Restrain(ArmFrame * _gripHold, gripTurn); // held the way it was bitten
                if (Current == State.Grip && now >= _nextGulp)
                {
                    _nextGulp = now + gulpInterval * Random.Range(0.85f, 1.15f);
                    _clenchVel += biteSqueeze * 4f; // a gulp: squeeze, jiggle
                    _gulpVel += 0.8f;
                    WhiteBloodCells.RaiseGulped(this, Prey);
                }
                Bite(Current == State.Grip ? 0.8f : Current == State.Engulf ? 1f : 0f, 0f, dt); // just swallowed: shut, not open
                return;

            case State.Hunt:
                // Gone, or another cell has it in its grip (can't be grabbed twice): look again at the next think.
                if (!Prey || !Prey.isActiveAndEnabled || WhiteBloodCells.Gripped(Prey)) { Current = State.Patrol; Prey = null; goto default; }
                spotGoal = Prey.transform.position;
                crawl = huntSpeed;
                hunger = 1f;
                _preySize = Size(Prey);
                // Gapes open as the catch comes close: the lips peel back, ready to snap.
                gape = Mathf.Clamp01(1f - ((Prey.transform.position - Spot).magnitude - _preySize - mouthSize * R) / Mathf.Max(gapeRange, 0.01f));
                if (Touching(Prey) && StartGrip(Prey, now)) return;
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
        _tension = Mathf.MoveTowards(_tension, 0f, dt * 2f);
        Bite(0f, gape, dt);
        Crawl(c, _bodyGoal, crawl, dt);
        MoveSpot(transform.position, R, spotGoal, dt);
        Track(R, dt);
    }

    // ---------------- deciding (every thinkInterval) ----------------

    void Think(Vector3 c, float R, float now)
    {
        Avoidance(c, R);
        if (Current == State.Engulf || Current == State.Grip) return;

        if (Current == State.Digest)
        {
            if (now >= _stateUntil) { Current = State.Patrol; _examined = null; }
            return;
        }

        // Anything the mouth touches is swallowed, prey or not.
        WhiteBloodCells.Near(Spot, mouthSize * R + touchMargin + 3f, s_near);
        foreach (Organism o in s_near)
            if (Touching(o) && StartGrip(o, now)) return;

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

    // The nearest virus it can sense (much further with antibodies on it), fewer other cells after it the
    // better. It commits: the prey is kept for commitTime, then only given up for one switchMargin better
    // (or one touching it), so a crowd doesn't make it flip between targets every think.
    void Sense(Vector3 c, float R, float now)
    {
        float reach = R + senseRange + antibodyRange * antibodyCap;
        WhiteBloodCells.Near(c, reach, s_near);
        Organism best = null;
        float bestScore = float.MaxValue, preyScore = float.MaxValue;
        bool hunting = Current == State.Hunt && Prey;
        foreach (Organism o in s_near)
        {
            if (WhiteBloodCells.Gripped(o)) continue; // another cell has it
            float d = (o.transform.position - c).magnitude - R;
            float range = senseRange + antibodyRange * Mathf.Min(Antibody.StuckOn(o), antibodyCap);
            bool touching = o.OnSurface && o.Surface == transform;
            if (d > range && !touching) continue;
            bool mine = hunting && o == Prey;
            float score = touching ? -1f : d / Mathf.Max(range, 0.01f) + sharePenalty * (Hunters(o) - (mine ? 1 : 0));
            if (mine) preyScore = score;
            else if (score < bestScore) { bestScore = score; best = o; }
        }

        bool preySensed = preyScore < float.MaxValue;
        if (preySensed)
        {
            // Keep it unless the committed time is up and another is clearly better, or another is on its body.
            bool committed = now - _huntSince < commitTime;
            bool overridden = bestScore <= -1f && preyScore > -1f; // one crawling on it beats one that isn't
            if (!best || !overridden && (committed || bestScore > preyScore - switchMargin)) best = Prey;
        }

        if (best)
        {
            _lastSensed = now;
            if (Current != State.Hunt || best != Prey)
            {
                Prey = best;
                Current = State.Hunt;
                _huntSince = now;
                WhiteBloodCells.RaiseNoticed(this, best); // a new hunt, or it turned on someone else
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
        s_seen.Clear();
        for (int i = 0; i < n; i++)
        {
            Surface s = s_hits[i].GetComponentInParent<Surface>();
            if (!s || !s.isCell || s.transform == transform || s.transform == _lastExamined || !s_seen.Add(s)) continue;
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
            Vector3 push = away / d * (1.5f * (1f - Mathf.Clamp01(clearance / (gap + 3f))));

            // One push per body (its nearest collider): a cell made of pieces isn't pushed off many times over.
            Object key = body ? body : col;
            int j = s_pushes.Count - 1;
            while (j >= 0 && s_pushes[j].body != key) j--;
            if (j < 0) s_pushes.Add((key, push));
            else if (push.sqrMagnitude > s_pushes[j].push.sqrMagnitude) s_pushes[j] = (key, push);
        }
        foreach (var p in s_pushes) _avoid += p.push;
        s_pushes.Clear();
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
        Carry(Time.time);
    }

    float _lastCarry = -1f;

    /// <summary>Moves the body by its crawl + the blood's flow since the last call. Separate from the (LOD'd) Tick
    /// so WhiteBloodCells can move on-screen idle cells every frame while they only think every few: stepped with
    /// the tick, drifting ones juddered against the camera.</summary>
    public void Carry(float now)
    {
        float dt = _lastCarry < 0f ? 0f : Mathf.Min(now - _lastCarry, 0.25f);
        _lastCarry = now;
        Vector3 c = transform.position;
        _drift = Vessel.FlowAt(c); // crawls through the blood, carried by it
        Vector3 step = _velocity + _drift;
        if (dt <= 0f || step.sqrMagnitude < 1e-8f) return;
        transform.position = c + step * dt;
    }

    // The spot swings toward its goal and the arm stretches to reach it, each on a damped spring (spotSpring,
    // spotDamping) capped at spotSpeed / extendSpeed: it picks up speed, overshoots a little and settles, so it
    // moves like something soft with weight, never at a constant rate. It pulls the arm in to swing round far.
    void MoveSpot(Vector3 c, float R, Vector3 goal, float dt)
    {
        Vector3 to = goal - c;
        float d = to.magnitude;
        if (d < 1e-3f) return;
        Vector3 dir = to / d;
        float angle = Vector3.Angle(_reachDir, dir);
        float want = Mathf.Clamp(d - R, 0f, maxReach) * (1f - Mathf.InverseLerp(12f, 40f, angle));
        if (Current == State.Hunt && !_preyOnMe && Blocked(c, R)) want = Mathf.Min(want, _reach); // don't push through a cell

        // Sub-stepped: far cells tick every few frames with a long dt, which a stiff spring can't take in one go.
        int steps = Mathf.Max(1, Mathf.CeilToInt(dt * 30f));
        float h = dt / steps;
        float w = 2f * Mathf.PI * spotSpring, wr = w * 1.4f;
        for (int i = 0; i < steps; i++)
        {
            // Swing: an angular spring toward the goal's direction, with no twist about the arm itself.
            Vector3 axis = Vector3.Cross(_reachDir, dir);
            float sin = axis.magnitude, theta = Mathf.Atan2(sin, Vector3.Dot(_reachDir, dir));
            if (sin > 1e-5f) axis /= sin;
            else axis = theta > 1f ? (_spin.sqrMagnitude > 1e-8f ? _spin.normalized : Organism.AnyPerpendicular(_reachDir)) : Vector3.zero;
            _spin += (axis * (w * w * theta) - _spin * (2f * spotDamping * w)) * h;
            _spin -= _reachDir * Vector3.Dot(_spin, _reachDir);
            _spin = Vector3.ClampMagnitude(_spin, spotSpeed / (R + _reach));
            float turn = _spin.magnitude * h;
            if (turn > 1e-6f) _reachDir = (Quaternion.AngleAxis(turn * Mathf.Rad2Deg, _spin) * _reachDir).normalized;

            // Stretch: a slightly stiffer spring on the length.
            _reachVel += (wr * wr * (want - _reach) - 2f * spotDamping * wr * _reachVel) * h;
            _reachVel = Mathf.Clamp(_reachVel, -extendSpeed, extendSpeed);
            _reach += _reachVel * h;
            if (_reach < 0f) { _reach = 0f; _reachVel = Mathf.Max(_reachVel, 0f); }
        }
    }

    // The lips and the squeeze are springs: the lips fly toward 'wrap' and bounce back off 1 (shut on the catch),
    // the squeeze jiggles back to 0 after each kick. Gape eases. Sub-stepped like the arm.
    void Bite(float wrap, float gape, float dt)
    {
        _gape = Mathf.MoveTowards(_gape, gape, dt * (gape > _gape ? 2.5f : 6f));
        int steps = Mathf.Max(1, Mathf.CeilToInt(dt * 60f));
        float h = dt / steps, w = 2f * Mathf.PI * lipSpring, wc = w * 1.6f;
        for (int i = 0; i < steps; i++)
        {
            _gulpVel += (w * w * (wrap - _gulp) - 2f * lipDamping * w * _gulpVel) * h;
            _gulp += _gulpVel * h;
            if (_gulp > 1f) { _gulp = 1f; _gulpVel = -Mathf.Abs(_gulpVel) * 0.35f; } // lips meet: bounce
            if (_gulp < 0f) { _gulp = 0f; _gulpVel = Mathf.Max(_gulpVel, 0f); }
            _clenchVel += (-wc * wc * _clench - 2f * lipDamping * wc * _clenchVel) * h;
            _clench += _clenchVel * h;
        }
    }

    // Spot velocity for the shader (the arm bows behind it), eased so a jerk doesn't snap it; and the reach frame's
    // side carried along with the arm by the smallest turn (parallel transport), so it never flips.
    void Track(float R, float dt)
    {
        _side = Quaternion.FromToRotation(_sideDir, _reachDir) * _side;
        _side -= _reachDir * Vector3.Dot(_side, _reachDir);
        _side = _side.sqrMagnitude > 1e-8f ? _side.normalized : Organism.AnyPerpendicular(_reachDir);
        _sideDir = _reachDir;

        Vector3 tip = _reachDir * (R + _reach);
        if (dt > 1e-5f)
        {
            Vector3 v = (tip - _lastTip) / (dt * Mathf.Max(R, 1e-3f));
            _tipVel = Vector3.Lerp(_tipVel, Vector3.ClampMagnitude(v, 3f), 1f - Mathf.Exp(-8f * dt));
        }
        _lastTip = tip;
    }

    // The arm's frame (forward along the reach, up = the carried side): what a catch's hold turns with.
    Quaternion ArmFrame => Quaternion.LookRotation(_reachDir, _side);

    // Aims the arm straight at a point (while gripping), keeping the springs' velocities in step so it
    // carries on smoothly when let go.
    void Aim(Vector3 offset, float R, float dt)
    {
        float d = offset.magnitude;
        if (d < 1e-4f) return;
        Vector3 dir = offset / d;
        float reach = Mathf.Max(d - R, 0f);
        if (dt > 1e-5f)
        {
            Vector3 axis = Vector3.Cross(_reachDir, dir);
            float sin = axis.magnitude;
            _spin = sin > 1e-6f ? axis / sin * (Mathf.Atan2(sin, Vector3.Dot(_reachDir, dir)) / dt) : Vector3.zero;
            _reachVel = (reach - _reach) / dt;
        }
        _reachDir = dir;
        _reach = reach;
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

    // Nothing solid between the body and the catch (the arm is straight from the centre, so this is the arm's
    // path): no grabbing through a cell, a chunk or a wall. Creatures don't block, and hits within the catch's size
    // of it are the ground it stands on, grazed on the way in. One raycast, only when the mouth is on something.
    bool InReach(Organism o)
    {
        Vector3 c = transform.position, to = o.transform.position - c;
        float d = to.magnitude;
        if (d < 1e-3f) return true;
        int n = Physics.RaycastNonAlloc(c, to / d, s_rayHits, d, ~0, QueryTriggerInteraction.Ignore);
        float end = d - Size(o);
        for (int i = 0; i < n; i++)
        {
            RaycastHit hit = s_rayHits[i];
            if (hit.distance >= end) continue;
            Transform t = hit.collider.transform;
            if (t.IsChildOf(transform) || t.IsChildOf(o.transform)) continue;
            Rigidbody body = hit.collider.attachedRigidbody;
            if (body && body.GetComponent<Organism>()) continue;
            return false;
        }
        return true;
    }

    // The mouth closes on it: from here it's held by forces (Pull), not carried.
    bool StartGrip(Organism o, float now)
    {
        if (now < _regrabAt || !InReach(o) || !WhiteBloodCells.Grip(this, o)) return false;
        Prey = o;
        Current = State.Grip;
        _preySize = Size(o);
        _reel = Mathf.Max((o.transform.position - transform.position).magnitude - Radius, 0f);
        _aimSlack = Spot - o.transform.position;
        _gripHold = Quaternion.Inverse(ArmFrame) * o.RotTarget.rotation;
        // The snap: the lips are flung shut (they overshoot, hit the catch and bounce) and the mouth squeezes.
        _gulpVel += 6f;
        _clenchVel += biteSqueeze * 9f;
        _nextGulp = now + gulpInterval;
        return true;
    }

    // The arm reels in like a winch that can't pull harder than gripStrength: the reel only advances while
    // the catch keeps up. The mouth stays on the catch, so the arm stretches after it wherever it swims.
    void Gripping(Vector3 c, float R, float dt, float now)
    {
        if (!Prey || !Prey.isActiveAndEnabled || !WhiteBloodCells.Gripped(Prey)) { LetGo(now); return; }
        Vector3 to = Prey.transform.position - c;
        float stretch = to.magnitude - R;
        float lag = Mathf.Max(stretch - _reel, 0f);
        if (lag > breakStretch || stretch > maxReach + breakStretch) // torn free
        {
            WhiteBloodCells.RaiseTornFree(this, Prey);
            LetGo(now);
            return;
        }

        _reel = Mathf.MoveTowards(_reel, 0f, reelSpeed * dt * Mathf.Clamp01(1f - lag / 2.5f));
        _tension = Mathf.MoveTowards(_tension, Mathf.Clamp01(0.45f + lag / 2.5f), dt * 3f);
        _hunger = Mathf.MoveTowards(_hunger, 1f, dt * 2f);
        AimAt(to, R, dt);

        if (_reel <= 0.05f && stretch <= _preySize + 0.6f) StartSwallow();
    }

    /// <summary>Physics step (WhiteBloodCells.FixedUpdate): pulls the catch toward where the arm is reeling it,
    /// as an acceleration capped at gripStrength, so its own swimming still counts. Pulled mostly along the arm;
    /// sideways it's only damped a little, so it can thrash about.</summary>
    public void Pull()
    {
        if (Current != State.Grip || !Prey) return;
        Rigidbody rb = Prey.Rb;
        if (!rb || rb.isKinematic) return;
        Vector3 c = transform.position, to = rb.position - c;
        float d = to.magnitude;
        if (d < 1e-3f) return;
        Vector3 dir = to / d;
        Vector3 target = c + dir * (Radius + _reel);
        Vector3 rel = rb.linearVelocity - _velocity - _drift;
        float along = Vector3.Dot(rel, dir), damp = 2f * Mathf.Sqrt(gripStiffness);
        Vector3 a = (target - rb.position) * gripStiffness - dir * (along * damp * 0.7f) - (rel - dir * along) * (damp * 0.2f);
        rb.AddForce(Vector3.ClampMagnitude(a, gripStrength), ForceMode.Acceleration);
    }

    // Lost it (torn free, or gone): back to hunting it if it's still about, the arm snapping back.
    void LetGo(float now)
    {
        if (Prey) WhiteBloodCells.Release(Prey);
        _regrabAt = now + regrabDelay;
        _reachVel = -extendSpeed;
        // _preySize is kept: the lips spring open off the catch's size (zeroing it collapsed the wrap to a point).
        _lastSensed = now;
        if (Prey && Prey.isActiveAndEnabled) Current = State.Hunt;
        else { Current = State.Patrol; Prey = null; }
    }

    // The mouth on the catch: aimed at its middle plus a slack that eases away, so it never jumps onto it.
    void AimAt(Vector3 to, float R, float dt)
    {
        _aimSlack *= Mathf.Exp(-dt / 0.15f);
        Aim(to + _aimSlack, R, dt);
    }

    void StartSwallow()
    {
        WhiteBloodCells.Release(Prey);
        // Picked up where and as fast as it is (Capture stops its physics), in the cell's frame: no jump.
        Vector3 velocity = Prey.Rb && !Prey.Rb.isKinematic ? Prey.Rb.linearVelocity : Vector3.zero;
        if (!WhiteBloodCells.Capture(this, Prey)) { LetGo(Time.time); return; }
        Current = State.Engulf;
        _swallow = 0f;
        Quaternion toCell = Quaternion.Inverse(transform.rotation);
        _held = toCell * (Prey.transform.position - transform.position);
        _heldVel = toCell * (velocity - _velocity - _drift);
        // Coming in fast, it splats: the squash is kicked by how hard it hit.
        float inward = -Vector3.Dot(_heldVel, _held.normalized);
        _squash = 0f;
        _squashVel = Mathf.Clamp(inward / Mathf.Max(_preySize, 0.05f), 0f, 8f) * 0.8f + 1.5f;
        _gulpVel += 3f;
        _clenchVel += biteSqueeze * 6f; // the final gulp
        WhiteBloodCells.RaiseMerging(this, Prey, Mathf.Max(inward, 0f));
    }

    // Merging, like a drop into a pool: the catch isn't carried off anywhere. It settles onto the surface where it
    // touched on a spring (keeping the speed it came in with), splats and jiggles (a squash spring on a pivot, so
    // its own mesh flattens), and sinks and shrinks while the body's surface flows up round it: the shader blends
    // the body with a blob where the catch is (MergeShape), its neck widening until it closes over it.
    void Engulfing(Vector3 c, float R, float dt)
    {
        _swallow += dt / swallowTime;
        _hunger = Mathf.MoveTowards(_hunger, 1f, dt * 2f);
        _tension = Mathf.MoveTowards(_tension, 0f, dt * 2f);
        if (!Prey) { EndEngulf(); return; }

        float m = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_swallow)), size = Mathf.Max(_preySize, 0.05f);
        float d = _held.magnitude;
        Vector3 dir = d > 1e-4f ? _held / d : Vector3.up;
        // Resting half out of the surface, then down until even its far side is under it.
        Vector3 goal = dir * (R + size * (0.5f - 1.9f * m));
        float w = mergeSpring * 2f * Mathf.PI, ws = w * 2.2f;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt * 60f), 1, 8);
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            _heldVel += (w * w * (goal - _held) - 2f * 0.8f * w * _heldVel) * h;
            _held += _heldVel * h;
            // Flattens as it melts in; squashes while pushing in, stretches pulling back out.
            float vr = Vector3.Dot(_heldVel, dir);
            float squashGoal = 0.45f * m + Mathf.Clamp(-vr / size * 0.12f, -0.15f, 0.25f);
            _squashVel += (ws * ws * (squashGoal - _squash) - 2f * 0.3f * ws * _squashVel) * h;
            _squash = Mathf.Clamp(_squash + _squashVel * h, -0.35f, 0.7f);
        }

        Vector3 at = c + transform.rotation * _held;
        float scale = Mathf.Lerp(1f, 0.5f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 1f, _swallow)));
        float height = 1f - _squash;
        WhiteBloodCells.Hold(Prey, at, scale, transform.rotation * dir, height);
        AimAt(at - c, R, dt);

        // The blob the body flows round: a little inside the catch (so the catch shows) once the lips have melted,
        // covering it at first as they did; the neck softens from a crease to a wide smooth slope.
        float rho = size * scale;
        float coat = rho * Mathf.Lerp(1.05f, 0.8f, Mathf.SmoothStep(0f, 1f, _swallow / 0.3f));
        _mergeShape = new Vector4(coat / R, _held.magnitude / R, rho * Mathf.Lerp(0.25f, 1.1f, m) / R, height);
        if (_swallow >= 1f)
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
        _mergeShape = Vector4.zero;
        _aimSlack = Vector3.zero;
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
    /// <summary>Drops a creature's cached size (it's destroyed).</summary>
    public static void Forget(Organism o) => s_size.Remove(o);
    static float Size(Organism o)
    {
        if (s_size.TryGetValue(o, out float r)) return r;
        Collider c = o.GetComponentInChildren<Collider>();
        r = c ? Mathf.Max(c.bounds.extents.x, Mathf.Max(c.bounds.extents.y, c.bounds.extents.z)) : 0.8f;
        s_size[o] = r;
        return r;
    }
}

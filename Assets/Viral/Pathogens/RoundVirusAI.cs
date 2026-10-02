using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A wild round virus: a ball that drifts and flies (slower than the player's kind) and rolls when it lands. It
/// isn't after you; it lives its own life among the cells, and the immune system hunts it like any virus
/// (antibodies and white cells go for every Organism).
/// - Drift: wanders the blood round obstacles (PathManager), now and then lands on a nearby cell and rolls about on
///   it (Roam), carried on by the momentum it landed with.
/// - Infect: once its cooldown is over, and only while few are around (<see cref="Ready"/>), it may pick a cell
///   nobody's in, land on it and sink in through the membrane (Enter: focus, the body sinks over enterTime).
///   It can't with an antibody on it: an antibody sticking meanwhile throws it back out. Inside, it gives itself
///   up (CellInfection): the cell calls the defences, then bursts with new ones (fewer the more there are).
///   Newborns can't infect for bornCooldown.
/// - Shake: antibodies on it: it jumps off and dashes about (Charge) to shake them loose.
/// Population stays bounded: infections wait for room (maxInfections at once, crowd-scaled chance) and bursts yield
/// less as the population nears the strain's cap, nothing at it (CellInfection.Yield).
///
/// Its Organism's Brain (ticked by SimulationTicker before it, same LOD rate); decisions every thinkInterval,
/// staggered. Cost per virus: a think (one PathManager query in the air) every thinkInterval, one physics overlap
/// (senseRange, cells' colliders) per decision when looking for a cell (every ~decideInterval), the roll (a
/// quaternion) per tick.
/// </summary>
[RequireComponent(typeof(Organism))]
public class RoundVirusAI : MonoBehaviour, IOrganismBrain, IWorldState
{
    [Header("Ball")]
    [Tooltip("The child that rolls (holds the mesh). Turned in world space, so the body's own turning doesn't show on it.")]
    public Transform ball;
    [Min(0.05f), Tooltip("Rolling radius (m): one turn per 2 pi r rolled.")]
    public float radius = 0.6f;
    [Min(0f), Tooltip("Slow tumble in the air (degrees/s).")]
    public float airSpin = 35f;
    [Min(0f), Tooltip("Seconds the speed it landed with keeps it rolling.")]
    public float rollOut = 1f;

    [Header("Wandering")]
    [Range(0f, 1f), Tooltip("Throttle while drifting about.")]
    public float driftThrottle = 0.3f;
    [Range(0f, 1f), Tooltip("Throttle rolling about on a cell.")]
    public float roamThrottle = 0.6f;
    [Tooltip("Seconds between new wandering headings (random in this range).")]
    public Vector2 wanderTime = new Vector2(2f, 5f);
    [Min(1f), Tooltip("How far it looks for cells.")]
    public float senseRange = 45f;
    [Min(0.5f), Tooltip("Seconds between decisions (roam / infect), +-30%.")]
    public float decideInterval = 4f;
    [Range(0f, 1f), Tooltip("Chance per decision of landing on a nearby cell to roll about on it.")]
    public float roamChance = 0.3f;
    [Tooltip("Seconds it rolls about on a cell before taking off (random in this range).")]
    public Vector2 roamTime = new Vector2(4f, 10f);
    [Min(1f), Tooltip("Gives up on reaching a cell after this many seconds.")]
    public float giveUpTime = 20f;

    [Header("Infecting")]
    [Tooltip("Spawned with the world: seconds until it may first infect (random in this range).")]
    public Vector2 firstCooldown = new Vector2(15f, 60f);
    [Tooltip("Burst out of a cell: seconds until it may infect (random in this range).")]
    public Vector2 bornCooldown = new Vector2(50f, 90f);
    [Range(0f, 1f), Tooltip("Chance per decision of going to infect a cell once able (less as they crowd).")]
    public float infectChance = 0.35f;
    [Min(0), Tooltip("Cells infected at once (around the player) past which none goes to infect.")]
    public int maxInfections = 3;
    [Min(0.1f), Tooltip("Seconds sinking through the membrane.")]
    public float enterTime = 2.5f;
    public CellInfection.Strain strain = new CellInfection.Strain();

    [Header("Evading")]
    [Tooltip("Seconds between dashes while antibodies are on it (random in this range).")]
    public Vector2 shakeInterval = new Vector2(0.5f, 1.2f);

    [Header("Thinking")]
    [Min(0.02f), Tooltip("Seconds between decisions and path queries. Staggered per virus.")]
    public float thinkInterval = 0.2f;

    /// <summary>Every live round virus (the population CellInfection.Yield counts).</summary>
    public static readonly List<RoundVirusAI> All = new List<RoundVirusAI>();

    enum Mode { Drift, Roam, Infect, Enter, Shake }

    Organism _o;
    Mode _mode;
    int _selfId;
    double _readyAt;           // CellInfection.Now when it may infect
    float _nextThink, _nextDecide, _nextWander, _nextShake, _modeUntil, _giveUpAt;
    Vector3 _wander = Vector3.forward, _move, _steer;
    Surface _cell;             // where it's going (Roam / Infect) / rolling (Roam)
    int _cellId;
    bool _wasGrounded;
    Vector3 _airVelocity;      // through the blood, last tick in the air
    Vector3 _momentum;         // Move it landed with, fading over rollOut
    float _landedAt;
    float _sink;               // Enter: 0..1
    Vector3 _site, _siteNormal;

    // The roll: the ball's world rotation, moved by the distance rolled over the cell under it (not by the cell's
    // own drift), kept spinning in the air.
    Quaternion _roll;
    Transform _rollOn;
    Vector3 _rollLocal, _spin, _tumble;

    static readonly Collider[] s_hits = new Collider[256];

    public Organism Organism => _o;

    void Awake()
    {
        _o = GetComponent<Organism>();
        _selfId = PathManager.Id(this);
        _readyAt = CellInfection.Now + Random.Range(firstCooldown.x, firstCooldown.y);
        _nextThink = Time.time + Random.value * thinkInterval;
        _nextDecide = Time.time + Random.Range(0.3f, 1f) * decideInterval;
        _wander = Random.onUnitSphere;
        _tumble = Random.onUnitSphere;
        if (!GetComponent<Selectable>())
            Selectable.Add(gameObject, Selectable.Category.Target, "Round Virus", CommandBoard.Jobs.Attack | CommandBoard.Jobs.MoveTo);
    }

    void OnEnable()
    {
        All.Add(this);
        _o.Brain = this;
    }

    void OnDisable()
    {
        All.Remove(this);
        if (_o.Brain == (IOrganismBrain)this) _o.Brain = null;
        _o.Hold(Intent.Focus, 0f);
        _move = _steer = Vector3.zero;
        _mode = Mode.Drift;
    }

    /// <summary>A new one (CellInfection's burst) at 'position' moving at 'velocity' (world): streamed and saved like
    /// anything generated if the WorldStreamer knows 'key', else a copy of ViralBuildAssets.pathogens' prefab of that
    /// name. It can't infect for its bornCooldown.</summary>
    public static RoundVirusAI Spawn(string key, Vector3 position, Vector3 velocity)
    {
        GameObject go = WorldStreamer.Active ? WorldStreamer.Instance.SpawnNew(key, position) : null;
        if (!go)
        {
            ViralBuildAssets assets = ViralBuildAssets.Instance;
            if (assets && assets.pathogens != null)
                foreach (GameObject p in assets.pathogens)
                    if (p && p.name == key) { go = Instantiate(p, position, Random.rotationUniform); break; }
        }
        if (!go || !go.TryGetComponent(out RoundVirusAI ai)) return null;
        ai.Born(velocity);
        return ai;
    }

    void Born(Vector3 velocity)
    {
        _readyAt = CellInfection.Now + Random.Range(bornCooldown.x, bornCooldown.y);
        _nextDecide = Time.time + decideInterval; // drift clear of the debris first
        _wander = velocity.sqrMagnitude > 1e-4f ? velocity.normalized : Random.onUnitSphere;
        _tumble = Random.onUnitSphere;
        _spin = _tumble * (airSpin * 6f * Mathf.Deg2Rad); // flung out spinning
        if (_o.Rb && !_o.Rb.isKinematic) _o.Rb.linearVelocity = velocity;
    }

    /// <summary>May go and infect now: cooldown over, nothing stuck on it, and room for another infection and
    /// the ones it would bring.</summary>
    public bool Ready =>
        CellInfection.Now >= _readyAt && AntibodyHold.CountOn(_o) == 0 &&
        CellInfection.All.Count < maxInfections && All.Count + CellInfection.All.Count < strain.maxPopulation;

    // ---------------- brain ----------------

    public void BrainTick(float dt)
    {
        Roll(dt);

        bool grounded = _o.OnSurface;
        if (grounded != _wasGrounded)
        {
            _wasGrounded = grounded;
            if (grounded) Landed();
            else _tumble = Random.onUnitSphere;
        }
        if (!grounded && _o.Rb && !_o.Rb.isKinematic) _airVelocity = _o.Rb.linearVelocity - _o.Fluid;

        if (Time.time >= _nextThink)
        {
            _nextThink = Time.time + thinkInterval;
            Think(grounded);
        }

        if (_mode == Mode.Enter)
        {
            Entering(dt);
            return;
        }

        // Ease toward the decision, plus what's left of the landing's momentum (a ball rolls on).
        _steer = Vector3.Lerp(_steer, _move, 1f - Mathf.Exp(-dt / 0.25f));
        Vector3 move = _steer;
        float left = grounded && rollOut > 0f ? 1f - (Time.time - _landedAt) / rollOut : 0f;
        if (left > 0f) move += _momentum * (left * left);
        _o.Move = move;
        if (_move.sqrMagnitude > 1e-4f) _o.aimForward = _move.normalized;
    }

    void Landed()
    {
        _landedAt = Time.time;
        float top = Mathf.Max(_o.grounded.moving.crawl.speed, 0.5f);
        _momentum = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(_airVelocity, _o.up) / top, 1f);
        if (_mode == Mode.Roam || _mode == Mode.Drift)
        {
            // Wherever it came down, it rolls about there a while.
            _cell = _o.grounded.surface.nav.Surface;
            _cellId = _cell ? PathManager.Id(_cell) : 0;
            _mode = Mode.Roam;
            _modeUntil = Time.time + Random.Range(roamTime.x, roamTime.y);
        }
    }

    void Think(bool grounded)
    {
        if (WhiteBloodCells.Gripped(_o) || WhiteBloodCells.Captured(_o))
        {
            if (_mode == Mode.Enter) StopEntering(false);
            _move = Vector3.zero;
            return;
        }

        bool coated = AntibodyHold.CountOn(_o) > 0;
        if (_mode == Mode.Enter)
        {
            if (coated) StopEntering(true); // an antibody stuck on: it can't get in like that
            return;
        }
        if (coated && _mode != Mode.Shake) Switch(Mode.Shake);

        Vector3 pos = transform.position;
        float now = Time.time;
        if (now >= _nextWander)
        {
            _nextWander = now + Random.Range(wanderTime.x, wanderTime.y);
            _wander = (_wander + Random.onUnitSphere * 1.2f).normalized;
        }

        switch (_mode)
        {
            case Mode.Shake:
                if (!coated) { Switch(Mode.Drift); break; }
                if (grounded) { _o.Press(Intent.Jump); break; }
                _move = _wander;
                if (now >= _nextShake)
                {
                    _nextShake = now + Random.Range(shakeInterval.x, shakeInterval.y);
                    _wander = Vector3.Slerp(_wander, Random.onUnitSphere, 0.7f).normalized; // a jink, not a straight run
                    _o.aimForward = _move = _wander;
                    _o.Press(Intent.Charge);
                }
                break;

            case Mode.Roam:
                if (!_cell) { Switch(Mode.Drift); break; }
                if (grounded)
                {
                    if (_o.grounded.surface.nav.Surface != _cell || now >= _modeUntil) { TakeOff(); break; }
                    _move = RollAbout() * roamThrottle;
                    if (now >= _nextDecide) Decide(pos);
                }
                else if (now >= _giveUpAt) Switch(Mode.Drift);
                else _move = ToCell(pos, 0.7f);
                break;

            case Mode.Infect:
                if (!_cell || CellInfection.On(_cell) || CellBurst.Bursting(_cell.transform) || now >= _giveUpAt)
                {
                    if (grounded) TakeOff(); else Switch(Mode.Drift);
                    break;
                }
                if (grounded)
                {
                    if (_o.grounded.surface.nav.Surface == _cell) StartEntering();
                    else _o.Press(Intent.Jump); // came down on the wrong one
                    break;
                }
                _move = ToCell(pos, 1f);
                break;

            default: // Drift
                if (grounded) { TakeOff(); break; }
                _move = Fly(pos, pos + _wander * 15f, 0) * driftThrottle;
                if (now >= _nextDecide) Decide(pos);
                break;
        }
    }

    void Switch(Mode mode)
    {
        _mode = mode;
        if (mode == Mode.Drift || mode == Mode.Shake) _cell = null;
        _giveUpAt = Time.time + giveUpTime;
        _nextShake = Time.time;
    }

    void TakeOff()
    {
        _o.Press(Intent.Jump);
        Switch(Mode.Drift);
        _nextDecide = Time.time + decideInterval; // off for a while before the next landing
    }

    // Roam a nearby cell, or go and infect one.
    void Decide(Vector3 pos)
    {
        _nextDecide = Time.time + decideInterval * Random.Range(0.7f, 1.3f);
        float crowd = strain.maxPopulation > 0 ? Mathf.Clamp01((float)All.Count / strain.maxPopulation) : 1f;
        if (Ready && Random.value < infectChance * (1f - crowd))
        {
            Surface target = NearestCell(pos, true);
            if (target)
            {
                Go(target, Mode.Infect);
                return;
            }
        }
        if (_mode == Mode.Drift && Random.value < roamChance)
        {
            Surface target = NearestCell(pos, false);
            if (target) Go(target, Mode.Roam);
        }
    }

    void Go(Surface cell, Mode mode)
    {
        bool onIt = _o.OnSurface && _o.grounded.surface.nav.Surface == cell;
        Switch(mode);
        _cell = cell;
        _cellId = PathManager.Id(cell);
        if (mode == Mode.Roam && !onIt) _modeUntil = float.MaxValue; // the clock starts on landing
    }

    // A cell within senseRange (one physics overlap; a cell's pieces count once): the nearest, give or take a
    // little so a crowd doesn't all pick the same one. 'infect': only ones nobody's in, not bursting, not yours.
    Surface NearestCell(Vector3 pos, bool infect)
    {
        int n = Physics.OverlapSphereNonAlloc(pos, senseRange, s_hits, ~0, QueryTriggerInteraction.Ignore);
        Surface best = null, last = null;
        float bestScore = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Surface s = s_hits[i].GetComponentInParent<Surface>();
            s_hits[i] = null;
            if (!s || s == last || !s.isCell) continue;
            last = s;
            if (infect && (CellInfection.On(s) || CellBurst.Bursting(s.transform))) continue;
            if (infect && s.TryGetComponent(out CellSignal signal) && signal.Converted) continue;
            float score = (s.transform.position - pos).sqrMagnitude * Random.Range(1f, 2f);
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    // Through the flow field toward 'goal'. 'ignore': a body that isn't an obstacle (the cell it's landing on).
    Vector3 Fly(Vector3 pos, Vector3 goal, int ignore)
    {
        Vector3 to = goal - pos;
        if (to.sqrMagnitude < 1e-6f) return Vector3.zero;
        return PathManager.I ? PathManager.I.GetDirection(pos, goal, _selfId, ignore, 0, _o.Fluid) : to.normalized;
    }

    // Onto the surface of _cell (nearest side), left out of the flow field so it lands instead of steering round it.
    Vector3 ToCell(Vector3 pos, float throttle)
    {
        Vector3 goal = _cell.Colliders.Length > 0 ? _cell.ClosestPoint(pos) : _cell.transform.position;
        return Fly(pos, goal, _cellId) * throttle;
    }

    // Wandering heading flattened onto the surface, in the shown frame (what Crawl expects).
    Vector3 RollAbout()
    {
        NavSurface nav = _o.grounded.surface.nav;
        Vector3 flat = Vector3.ProjectOnPlane(_wander, nav.SurfaceNormal);
        if (flat.sqrMagnitude < 1e-4f) flat = nav.Heading;
        return nav.ToShown(flat.normalized);
    }

    // ---------------- getting in ----------------

    void StartEntering()
    {
        _mode = Mode.Enter;
        _sink = 0f;
        _move = _steer = Vector3.zero;
        _o.Move = Vector3.zero;
        _o.Hold(Intent.Focus); // halts it, and the immune system hears a virus drilling in
        _siteNormal = _o.up;
        _site = transform.position - _siteNormal * _o.grounded.surface.nav.hoverHeight;
        if (_cell) _cell.AddImpact(_site, 3f);
    }

    // Sinks through the membrane (BodyOffset down past its own size), spinning as it bores in; at the bottom it's in.
    void Entering(float dt)
    {
        _o.Move = Vector3.zero;
        if (!_o.OnSurface || !_cell || _o.grounded.surface.nav.Surface != _cell || CellInfection.On(_cell))
        {
            StopEntering(false); // knocked off, plucked off, or someone else got in first
            return;
        }
        _sink = Mathf.Min(1f, _sink + dt / enterTime);
        float s = _sink * _sink * (3f - 2f * _sink);
        _o.BodyOffset = -(radius * 2.2f + _o.grounded.surface.nav.hoverHeight) * s;
        _spin = _o.up * (Mathf.Lerp(60f, 400f, s) * Mathf.Deg2Rad);
        if (_sink < 1f) return;

        _site = transform.position - _o.up * _o.grounded.surface.nav.hoverHeight;
        _siteNormal = _o.up;
        if (CellInfection.Begin(_cell, _site, _siteNormal, strain)) Destroy(gameObject); // gave itself up inside
        else StopEntering(false);
    }

    void StopEntering(bool coated)
    {
        _o.Hold(Intent.Focus, 0f);
        _sink = 0f;
        if (coated) Switch(Mode.Shake);
        else Switch(Mode.Drift);
        if (_o.OnSurface) _o.Press(Intent.Jump);
    }

    // ---------------- rolling ----------------

    void Roll(float dt)
    {
        if (!ball || dt <= 0f) return;
        if (_roll.x == 0f && _roll.y == 0f && _roll.z == 0f && _roll.w == 0f) _roll = ball.rotation; // new, or a script reload wiped it

        Vector3 pos = transform.position;
        Transform on = _o.OnSurface ? _o.Surface : null;
        float angle; // radians
        Vector3 axis;
        if (on && _mode != Mode.Enter)
        {
            // Roll by how far it went over the cell under it, so the cell's own drift doesn't spin it.
            Vector3 moved = on == _rollOn ? pos - on.TransformPoint(_rollLocal) : Vector3.zero;
            Vector3 flat = Vector3.ProjectOnPlane(moved, _o.up);
            float d = flat.magnitude;
            axis = d > 1e-5f ? Vector3.Cross(_o.up, flat / d) : Vector3.zero;
            angle = d / radius;
            _spin = axis * (angle / dt); // carried into the air when it leaves
        }
        else
        {
            if (_mode != Mode.Enter) _spin = Vector3.Lerp(_spin, _tumble * (airSpin * Mathf.Deg2Rad), 1f - Mathf.Exp(-0.8f * dt));
            float w = _spin.magnitude;
            axis = w > 1e-5f ? _spin / w : Vector3.zero;
            angle = w * dt;
        }
        _rollOn = on;
        if (on) _rollLocal = on.InverseTransformPoint(pos);

        if (angle > 1e-5f && axis != Vector3.zero)
            _roll = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis) * _roll;
        _roll = Quaternion.Normalize(_roll);
        ball.rotation = _roll;
    }

    // ---------------- saving ----------------

    [System.Serializable]
    struct Saved { public double readyAt; }

    string IWorldState.SaveState() => JsonUtility.ToJson(new Saved { readyAt = _readyAt });
    void IWorldState.LoadState(string state) => _readyAt = JsonUtility.FromJson<Saved>(state).readyAt;
    bool IWorldState.Pinned => _mode == Mode.Enter; // half into a cell
}

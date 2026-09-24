using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Anything that can stand on a surface reports it here. Lets other systems
/// (leg walkers, footstep audio, dust, AI) read it without knowing the mover:
/// find it with GetComponentInParent.
/// </summary>
public interface ISurfaceContact
{
    bool OnSurface { get; }
    Vector3 SurfaceNormal { get; }
    Transform Surface { get; } // what is being stood on; null while airborne
}

/// <summary>Controller ticked by SimulationTicker just before its Organism, at the same rate.</summary>
public interface IOrganismBrain
{
    void BrainTick(float dt);
}

/// <summary>
/// A living thing: a body (Rigidbody, rotation, visual offset), intent, and a
/// state tree. Behaviour lives in states (OrganismStates.cs) built from
/// effects (OrganismEffects.cs). Controllers (player, AI) only write intent.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Organism : MonoBehaviour, ISurfaceContact
{
    [Tooltip("Optional visual child that turns. Empty: the whole object turns.")] public Transform body;
    [Tooltip("Child moved by BodyOffset. Never the root; falls back to Body.")] public Transform visual;
    [Tooltip("Turn rate. ~6 floaty, ~20 sharp.")] public float rotationSharpness = 8f;
    [Tooltip("How fast BodyOffset returns to 0 when nothing drives it.")] public float offsetReturnSpeed = 10f;
    [Tooltip("Rigidbody gravity while not attached.")] public bool airGravity;

    public Flying flying = new Flying();
    public Grounded grounded = new Grounded();
    public Jumping jumping = new Jumping();

    /// <summary>(previous, current). Either may be null: no valid state = passive physics.</summary>
    public event Action<OrganismState, OrganismState> StateChanged;

    public OrganismState Current { get; private set; }
    public Rigidbody Rb { get; private set; }
    public bool InState(OrganismState s) => Current != null && Current.Is(s);

    /// <summary>Surface normal while attached; the last one after leaving.</summary>
    [NonSerialized] public Vector3 up = Vector3.up;

    [Tooltip("Most another system (a rope pulling the mouth round) may turn the body on top of what its states want, in degrees. Keeps control with the states.")]
    [Range(0f, 90f)] public float maxLean = 30f;
    [Min(0.01f), Tooltip("How quickly a lean comes and goes (1/s).")]
    public float leanSpeed = 6f;

    // ISurfaceContact
    public bool OnSurface => InState(grounded);
    public Vector3 SurfaceNormal => up;
    public Transform Surface => OnSurface && grounded.surface.nav.Surface ? grounded.surface.nav.Surface.Space : null;

    /// <summary>Actual speed. Rigidbody speed by default; effects that move the body themselves report it.</summary>
    public float speed { get; set; }

    /// <summary>Top speed of the active effects, kept while coasting in the same family.</summary>
    public float totalSpeed { get; private set; }

    public float normalizedSpeed => totalSpeed > 1e-5f ? Mathf.Clamp01(speed / totalSpeed) : 0f;

    // ---------------- intent ----------------

    Vector3 _move;
    readonly Dictionary<string, float> _held = new Dictionary<string, float>();
    readonly HashSet<string> _pressed = new HashSet<string>();

    /// <summary>Desired world direction, magnitude 0-1.</summary>
    public Vector3 Move { get => _move; set => _move = Vector3.ClampMagnitude(value, 1f); }

    [NonSerialized] public Vector3 aimForward = Vector3.forward, aimUp = Vector3.up;

    /// <summary>One-shot. Lives until the next state pick has seen it.</summary>
    public void Press(string intent) => _pressed.Add(intent);

    /// <summary>Held with a value (e.g. hold progress). 0 releases.</summary>
    public void Hold(string intent, float value = 1f) { if (value > 0f) _held[intent] = value; else _held.Remove(intent); }

    public bool Pressed(string intent) => _pressed.Contains(intent);
    public bool Holding(string intent) => _held.ContainsKey(intent);
    public float Value(string intent) => _held.TryGetValue(intent, out float v) ? v : 0f;

    /// <summary>Drop move intent and momentum now. Every effect gets OnHalt.</summary>
    public void Halt()
    {
        _move = Vector3.zero;
        speed = 0f;
        ForEachEffect(e => e.OnHalt());
        if (!Rb.isKinematic) Rb.linearVelocity = Rb.angularVelocity = Vector3.zero;
    }

    // ---------------- state tree ----------------

    readonly List<OrganismState> _roots = new List<OrganismState>();
    readonly List<OrganismState> _all = new List<OrganismState>();

    /// <summary>Register a state under parent (null = root). Its Effect fields become its effects, its state fields its children.</summary>
    public void Add(OrganismState state, OrganismState parent = null)
    {
        List<OrganismState> siblings = parent != null ? parent.children : _roots;
        state.Bind(this, parent, siblings.Count);
        siblings.Add(state);
        _all.Add(state);

        foreach (FieldInfo field in state.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            switch (field.GetValue(state))
            {
                case Effect e: e.Bind(state); state.effects.Add(e); break;
                case OrganismState child: Add(child, state); break;
            }
        }
    }

    /// <summary>Re-pick now; exit/enter only the levels that change.</summary>
    public void Refresh()
    {
        OrganismState next = Pick(_roots);
        if (next == Current) return;

        OrganismState prev = Current, common = prev;
        while (common != null && (next == null || !next.Is(common))) common = common.Parent;
        if (common == null) totalSpeed = 0f; // new family

        for (OrganismState s = prev; s != common; s = s.Parent) s.DoExit();
        Current = next;
        if (next != null)
            for (int i = common != null ? common.Chain.Length : 0; i < next.Chain.Length; i++) next.Chain[i].DoEnter();

        StateChanged?.Invoke(prev, next);
    }

    // Highest-weight valid option (ties go to the later one), then descend into its children.
    static OrganismState Pick(List<OrganismState> options)
    {
        OrganismState best = null;
        foreach (OrganismState s in options)
            if (s.Valid && (best == null || s.Weight >= best.Weight)) best = s;
        return best == null ? null : Pick(best.children) ?? best;
    }

    // Root first, effects before their state. Stops if anything switched the state.
    void Run(Phase phase, float dt)
    {
        OrganismState leaf = Current;
        if (leaf == null) return;

        foreach (OrganismState s in leaf.Chain)
        {
            foreach (Effect e in s.effects)
            {
                if (Current != leaf) return;
                e.Run(phase, dt);
            }

            if (Current != leaf) return;
            s.Run(phase, dt);
        }
    }

    void ForEachEffect(Action<Effect> action)
    {
        foreach (OrganismState s in _all)
            foreach (Effect e in s.effects) action(e);
    }

    // ---------------- unity ----------------

    void Awake()
    {
        Rb = GetComponent<Rigidbody>();
        Rb.constraints = RigidbodyConstraints.FreezeRotation;
        Rb.useGravity = airGravity;
        Rb.interpolation = RigidbodyInterpolation.Interpolate;
        TargetRotation = RotTarget.rotation;

        Add(flying);
        Add(grounded);
        Add(jumping);
    }

    // ---------------- ticking ----------------
    // SimulationTicker drives every organism from one loop instead of per-object Unity
    // callbacks, and may tick crowd members (those with a Brain) less often, passing the
    // time they skipped as dt. So everything here runs on the dt it's given.

    /// <summary>
    /// Optional controller ticked just before this organism, at the same rate (e.g. VirusAI).
    /// Having one marks the organism as a crowd member that may tick less often far away.
    /// </summary>
    public IOrganismBrain Brain { get; set; }

    /// <summary>Every enabled organism (for systems that watch creatures, e.g. ImmuneSystem).</summary>
    public static readonly List<Organism> All = new List<Organism>();

    void OnEnable()
    {
        SimulationTicker.Register(this);
        All.Add(this);
    }

    public void Tick(float dt)
    {
        Refresh();
        speed = Rb.isKinematic ? 0f : Rb.linearVelocity.magnitude;
        Run(Phase.Update, dt);
        _pressed.Clear(); // presses made later this frame wait for the next pick

        float top = 0f;
        if (Current != null)
            foreach (OrganismState s in Current.Chain)
                foreach (Effect e in s.effects) top = Mathf.Max(top, e.TopSpeed);
        if (top > 0f) totalSpeed = top;

        UpdateLean(dt);
        Rotate(RotationWeight(dt));
    }

    public void LateTick(float dt)
    {
        Run(Phase.Late, dt);
        ApplyBodyOffset(dt);
    }

    public void FixedTick(float dt)
    {
        Run(Phase.Fixed, dt);

        // Dynamic root (no separate body child): rotate through physics so interpolation doesn't fight it.
        if (RotatesRoot && !Rb.isKinematic && !_externalRotation)
            Rb.MoveRotation(Quaternion.Slerp(Rb.rotation, _lean * TargetRotation, RotationWeight(dt)));
    }

    void OnCollisionEnter(Collision c) => ForEachEffect(e => e.OnCollision(c));

    void OnDisable()
    {
        SimulationTicker.Unregister(this);
        All.Remove(this);
        if (_visual) _visual.localPosition = _visualHome;
        _offset = 0f;
    }

    // ---------------- body offset ----------------

    float _offset;
    bool _offsetDriven;
    Transform _visual;
    Vector3 _visualHome;

    /// <summary>Visual lift along up. Eases back to 0 on frames nothing sets it.</summary>
    public float BodyOffset { get => _offset; set { _offset = value; _offsetDriven = true; } }

    void ApplyBodyOffset(float dt)
    {
        Transform target = visual && visual != transform ? visual : body && body != transform ? body : null;
        if (target != _visual)
        {
            if (_visual) _visual.localPosition = _visualHome;
            _visual = target;
            if (target) _visualHome = target.localPosition;
        }

        if (!_offsetDriven) _offset = Mathf.Abs(_offset) < 5e-4f ? 0f : Mathf.Lerp(_offset, 0f, 1f - Mathf.Exp(-offsetReturnSpeed * dt));
        _offsetDriven = false;
        if (!_visual) return;

        Vector3 world = up * _offset;
        _visual.localPosition = _visualHome + (_visual.parent ? _visual.parent.InverseTransformVector(world) : world);
    }

    // ---------------- rotation ----------------

    public Quaternion TargetRotation { get; set; } = Quaternion.identity;
    public Transform RotTarget => body ? body : transform;

    /// <summary>Hold the up axis exactly on the target's; only the heading around it eases.
    /// Stops the tilt that easing the whole rotation gives on curved surfaces.</summary>
    [NonSerialized] public bool keepUpright;

    /// <summary>Aim Flying's lead axis along a direction, rolled by aimUp.</summary>
    public void Face(Vector3 direction)
    {
        if (direction.sqrMagnitude < 1e-6f) return;
        Vector3 lead = flying.leadAxis.sqrMagnitude > 1e-6f ? flying.leadAxis.normalized : Vector3.forward;
        TargetRotation = SafeLookRotation(direction.normalized, aimUp) * Quaternion.Inverse(Quaternion.LookRotation(lead, AnyPerpendicular(lead)));
    }

    /// <summary>Ease toward TargetRotation. t = 1 snaps. The lean, if any, goes on top.</summary>
    public void Rotate(float t)
    {
        if (_externalRotation) return;

        // Ease the un-leaned rotation, so the lean never feeds back into what the states want.
        // Something else turned the body since we last did (physics, a snap): start from there.
        Quaternion current = RotTarget.rotation;
        if (!_baseValid || Quaternion.Angle(current, _shown) > 0.05f) _base = Quaternion.Inverse(_lean) * current;

        Quaternion to = keepUpright ? Upright(_base, t) : Quaternion.Slerp(_base, TargetRotation, t);
        _base = to;
        _baseValid = true;
        _shown = _lean * to;

        if (!RotatesRoot) body.rotation = _shown;
        else if (Rb.isKinematic) transform.rotation = _shown; // dynamic: MoveRotation owns it
    }

    // ---------------- lean ----------------

    Quaternion _base = Quaternion.identity, _shown = Quaternion.identity, _lean = Quaternion.identity;
    bool _baseValid, _leanAsked;
    Vector3 _leanAxis, _leanToward; // axis in the body's frame; direction in world
    float _leanStrength;

    /// <summary>
    /// Ask for the body to turn so 'axis' (world, on the body now) points more toward 'toward',
    /// with strength 0-1 of maxLean, on top of the states' rotation. Ask every frame it applies;
    /// unasked, it eases back off. For things like a rope turning the body to follow it.
    /// </summary>
    public void Lean(Vector3 axis, Vector3 toward, float strength)
    {
        if (axis.sqrMagnitude < 1e-8f || toward.sqrMagnitude < 1e-8f) return;
        _leanAxis = Quaternion.Inverse(RotTarget.rotation) * axis.normalized;
        _leanToward = toward.normalized;
        _leanStrength = Mathf.Clamp01(strength);
        _leanAsked = true;
    }

    void UpdateLean(float dt)
    {
        Quaternion goal = Quaternion.identity;
        if (_leanAsked && _leanStrength > 0f && maxLean > 0f)
        {
            Vector3 axis = _base * _leanAxis;
            goal = Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(axis, _leanToward),
                                            maxLean * _leanStrength);
        }
        _leanAsked = false;
        _lean = Quaternion.Slerp(_lean, goal, 1f - Mathf.Exp(-leanSpeed * dt));
    }

    // Body unset, or set to the Rigidbody's own object: rotation then belongs to physics while
    // dynamic. Writing the transform of an interpolated Rigidbody every frame fights the
    // interpolation, and the rotation sticks or jitters depending on frame timing.
    bool RotatesRoot => !body || body == transform;

    /// <summary>TargetRotation's up, taken whole, with the heading turned only part of the way to it.</summary>
    Quaternion Upright(Quaternion from, float t)
    {
        Vector3 axis = TargetRotation * Vector3.up;
        Vector3 want = Vector3.ProjectOnPlane(TargetRotation * Vector3.forward, axis);
        Vector3 have = Vector3.ProjectOnPlane(from * Vector3.forward, axis);
        if (have.sqrMagnitude < 1e-6f) have = Vector3.ProjectOnPlane(from * Vector3.up, axis); // nose along the axis
        if (have.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f) return TargetRotation;

        have.Normalize();
        want.Normalize();
        return Quaternion.LookRotation(Vector3.RotateTowards(have, want, Vector3.Angle(have, want) * Mathf.Deg2Rad * t, 0f), axis);
    }

    public float RotationWeight(float dt) => rotationSharpness <= 0f ? 1f : 1f - Mathf.Exp(-rotationSharpness * dt);

    bool _externalRotation;
    RigidbodyConstraints _constraintsBeforeExternal;

    public bool ExternalRotation => _externalRotation;

    /// <summary>Let e.g. a rope own rotation via torque while dynamic. Release keeps the current orientation.</summary>
    public void SetExternalRotation(bool on)
    {
        if (!Rb || on == _externalRotation || (on && Rb.isKinematic)) return;
        _externalRotation = on;

        if (on)
        {
            _constraintsBeforeExternal = Rb.constraints;
            Rb.constraints &= ~RigidbodyConstraints.FreezeRotation;
        }
        else
        {
            TargetRotation = RotTarget.rotation;
            Rb.angularVelocity = Vector3.zero;
            Rb.constraints = _constraintsBeforeExternal;
        }
    }

    // ---------------- math ----------------

    public static Quaternion SafeLookRotation(Vector3 forward, Vector3 up) =>
        Quaternion.LookRotation(forward, up.sqrMagnitude < 1e-6f || Mathf.Abs(Vector3.Dot(forward, up.normalized)) > 0.999f ? AnyPerpendicular(forward) : up);

    public static Vector3 AnyPerpendicular(Vector3 v) =>
        Vector3.Cross(v, Mathf.Abs(v.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
}
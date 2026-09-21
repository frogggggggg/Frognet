using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Procedural fluid-leg walker for VirusMovement.
///
/// All legs are ONE combined mesh (one draw call) on a generated child of Body.
/// Only the values that define the look are exposed; everything else derives
/// from them so the motion stays consistent at any scale.
///
/// One leg layout is shared by every state: each leg owns a fixed slot on a ring,
/// the ring has a heading (_ringFwd) and a phase (_spin). Walking, flying and
/// landing all place legs through that same ring, so no transition ever makes
/// legs swap sides, cross over, or pop.
///
/// Independent of the body's own rotation: a rolling/spinning body does not
/// drag the legs around.
/// </summary>
[DefaultExecutionOrder(100)]
public class SpiderLegWalker : MonoBehaviour
{
    // A point that rides a (possibly moving) support exactly like a child transform.
    struct Anchor
    {
        public Transform t;
        public Vector3 local, world;

        public void Set(Vector3 w, Transform support)
        {
            world = w;
            t = support;
            if (support) local = support.InverseTransformPoint(w);
        }

        // A destroyed support simply leaves the point at its last world position.
        public Vector3 Get()
        {
            if (t) world = t.TransformPoint(local);
            return world;
        }
    }

    struct Leg
    {
        public float angle, phase, rReach, rTiming, rHeight, delay; // r* are -1..1 personality seeds
        public bool stepping;
        public float timer;
        public Anchor foot, from, to;
        public Vector3 footN, toN;
        public Vector3 desired, tip, airStart, ground, clampN, bendUp, side, rootDir, rootGoal;
    }

    [Header("References")]
    [Tooltip("Root/body of the virus. Leave empty to use this transform.")]
    public Transform body;
    public VirusMovement movement;
    public Material legMaterial;
    public bool castShadows = true;

    [Header("Shape")]
    [Range(1, 32)] public int legCount = 6;
    [Min(0f)] public float legRootRadius = 0.45f;
    [Tooltip("Resting distance from the body center to each foot. Most other distances scale from this.")]
    [Min(0.05f)] public float footDistance = 1.5f;
    [Min(0.001f)] public float baseRadius = 0.10f, tipRadius = 0.055f;
    [Min(0f)] public float curveHeight = 0.24f;
    [Range(3, 24)] public int lengthSegments = 12;
    [Range(3, 16)] public int radialSegments = 8;

    [Header("Walking")]
    [Tooltip("Surfaces feet may plant on. The virus's own colliders are always ignored.")]
    public LayerMask groundMask = ~0;
    [Min(0.01f)] public float stepDistance = 0.40f;
    [Min(0.02f)] public float stepDuration = 0.14f;
    [Min(0f)] public float stepHeight = 0.22f;
    [Tooltip("Ground speed the step timing is authored for. Faster movement steps faster.")]
    [Min(0.1f)] public float walkSpeed = 4f;
    [Tooltip("0 = perfectly regular robot, 1 = loose and creature-like.")]
    [Range(0f, 1f)] public float organic = 0.6f;

    [Header("Air")]
    [Tooltip("Leg spin in degrees per second at full flight speed. Slows while hovering and while landing.")]
    [Min(0f)] public float airSpin = 300f;
    [Tooltip("How far legs sweep back behind the direction of flight.")]
    [Range(0f, 2f)] public float airSweep = 1f;
    [Tooltip("How much the legs drift and undulate while flying.")]
    [Range(0f, 1f)] public float airFlow = 0.4f;
    [Tooltip("How far ahead a surface is spotted and the legs start reaching for it.")]
    [Min(0.1f)] public float landingDistance = 3f;

    // Derived feel constants. Tuned once, shared by every virus.
    const float CurveBias = 0.35f, StretchThickness = 0.16f, WobbleSpeed = 3.5f;
    const float PlantedFollow = 0.25f, FrameFollow = 3f, DragPriority = 0.85f;
    const float TurnAngle = 27f * Mathf.Deg2Rad, TurnRadius = 0.12f, FullTurnRate = 140f;
    const float MinGait = 0.35f, MaxGait = 3f, HoldSpeed = 0.2f, TakeoffTime = 0.25f;

    static readonly RaycastHit[] Hits = new RaycastHit[8];

    Leg[] _legs;
    Transform _b, _self, _meshT, _support, _frameSupport;
    Rigidbody _rb;
    Mesh _mesh;
    MeshRenderer _renderer;
    Vector3[] _v, _n;
    float[] _cos, _sin;
    int _rings, _sides;

    // Shared ring: heading + phase + handedness.
    Vector3 _ringFwd = Vector3.forward, _ringFwdLocal;
    float _spin, _mirror = 1f;

    bool _seeded, _modeReady, _air, _supportSeeded, _prevMoveValid, _hasLand;
    Vector3 _lastPos, _vel, _relVel, _normal, _groundNormal = Vector3.up, _supportLocal, _prevMove;
    float _turn, _gait = 1f, _lastStepTime = -999f, _wanderPhase, _wobblePhase;

    // Air: _airAxis blends from "up" (hovering, legs hang) to flight direction (legs trail).
    Vector3 _flightDir = Vector3.forward, _airAxis = Vector3.up, _axisGoal = Vector3.up, _orbitRef = Vector3.right, _landNormal;
    Anchor _landPoint;
    float _landing, _airTurn, _airSpeed01, _airTime;

    float FootLift => tipRadius + 0.02f;
    float ProbeUp => footDistance * 1.3f;
    float ProbeDown => footDistance * 3f;
    float Reach(in Leg l) => footDistance * (1f + l.rReach * 0.08f * organic);
    float Slot(in Leg l) => _mirror * l.angle + _spin;

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    void OnEnable()
    {
        _seeded = false;
        _modeReady = false;
        if (_renderer) _renderer.enabled = true;
    }

    void OnDisable()
    {
        if (_renderer) _renderer.enabled = false;
    }

    void OnDestroy() => Cleanup();

    void OnValidate()
    {
        if (!_renderer) return;
        _renderer.sharedMaterial = legMaterial;
        _renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
    }

    [ContextMenu("Rebuild Legs")]
    public void RebuildLegs() => _legs = null;

    void LateUpdate()
    {
        if (_legs == null || _legs.Length != legCount ||
            _rings != lengthSegments + 1 || _sides != radialSegments)
            Build();

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Rigidbody velocity when available: transform deltas of a non-interpolated
        // body flicker between 0 and full speed whenever FixedUpdate is skipped.
        Vector3 pos = _b.position;
        _vel = _rb && !_rb.isKinematic
            ? _rb.linearVelocity
            : _seeded ? (pos - _lastPos) / dt : Vector3.zero;
        _lastPos = pos;
        _seeded = true;

        _normal = SurfaceNormal();

        bool air = movement && movement.state == VirusMovement.State.Flying;
        if (!_modeReady || air != _air)
        {
            if (air) EnterAir(pos);
            else EnterGround(pos, _modeReady);
            _air = air;
            _modeReady = true;
        }

        if (air) UpdateAir(pos, dt);
        else UpdateGround(pos, dt);

        _wobblePhase += dt * WobbleSpeed * (air ? 1f : _gait);

        float rootK = 1f - Mathf.Exp(-14f * dt), bendK = 1f - Mathf.Exp(-8f * dt);
        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            l.rootDir = Vector3.Slerp(l.rootDir, l.rootGoal, rootK).normalized;
            l.bendUp = Vector3.Slerp(l.bendUp, l.clampN.sqrMagnitude > 0f ? l.clampN : -_airAxis, bendK);
        }

        UpdateBounds(pos);
        if (_renderer.isVisible) BuildMesh(pos);
    }

    Vector3 SurfaceNormal()
    {
        if (movement && movement.IsSurfaceAttached)
        {
            Vector3 n = movement.surfaceNormal;
            if (n.sqrMagnitude > 1e-6f) return n.normalized;
        }
        return _b.up;
    }

    // Ring basis on a plane, from the shared heading. Never uses the body's rotation.
    void Basis(Vector3 n, out Vector3 right, out Vector3 fwd)
    {
        fwd = Vector3.ProjectOnPlane(_ringFwd, n);
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Perp(n);
        right = Vector3.Cross(n, fwd);
    }

    // Closest ground hit that is not part of this virus.
    bool Cast(Vector3 origin, Vector3 dir, float dist, out RaycastHit best)
    {
        int count = Physics.RaycastNonAlloc(origin, dir, Hits, dist, groundMask, QueryTriggerInteraction.Ignore);
        best = default;
        float bestDist = float.MaxValue;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            RaycastHit h = Hits[i];
            if (h.distance <= 0f || h.distance >= bestDist || h.collider.transform.IsChildOf(_self)) continue;
            best = h;
            bestDist = h.distance;
            found = true;
        }
        return found;
    }

    static Transform SupportOf(in RaycastHit hit) => hit.rigidbody ? hit.rigidbody.transform : hit.transform;

    bool Probe(Vector3 p, Vector3 n, float up, float down, out Vector3 foot, out Vector3 normal, out Transform support)
    {
        if (Cast(p + n * up, -n, up + down, out RaycastHit hit))
        {
            normal = hit.normal;
            foot = hit.point + normal * FootLift;
            support = SupportOf(hit);
            return true;
        }

        foot = p;
        normal = n;
        support = null;
        return false;
    }

    void SetPlanted(ref Leg l)
    {
        l.tip = l.foot.world;
        l.ground = l.tip - l.footN * FootLift;
        l.clampN = l.footN;
    }

    // ---------------------------------------------------------------------
    // Ground
    // ---------------------------------------------------------------------

    void EnterGround(Vector3 pos, bool fromAir)
    {
        Vector3 n = _normal;
        if (fromAir) AlignRingToLanding(n);
        Basis(n, out Vector3 right, out Vector3 fwd);
        float capture = footDistance * 0.75f;

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float a = Slot(l);
            Vector3 radial = right * Mathf.Cos(a) + fwd * Mathf.Sin(a);
            Vector3 rest = pos + radial * Reach(l);

            // Capture each foot right where it touched down; anything that didn't reach
            // the surface falls back to its resting spot. Every foot ends up on the ground.
            Vector3 foot, hn;
            Transform s;
            if (!(fromAir && Probe(l.tip, n, capture, capture, out foot, out hn, out s)))
                Probe(rest, n, ProbeUp, ProbeDown, out foot, out hn, out s);

            l.foot.Set(foot, s);
            l.footN = hn;
            l.stepping = false;
            SetPlanted(ref l);
            l.rootGoal = radial;
            if (!fromAir) l.rootDir = radial;
        }

        _support = _frameSupport = null;
        _supportSeeded = _prevMoveValid = _hasLand = false;
        _relVel = _vel;
        _turn = _landing = 0f;
        _lastStepTime = -999f;
    }

    // Maps the spinning air ring onto the landing surface so the walking layout
    // matches exactly where the legs reached. Handles landing "upside down"
    // relative to the air ring by mirroring the slot order.
    void AlignRingToLanding(Vector3 n)
    {
        Vector3 orbitFwd = Vector3.Cross(_orbitRef, _airAxis);
        bool flip = Vector3.Dot(_airAxis, n) < 0f;
        _ringFwd = Quaternion.FromToRotation(_airAxis, flip ? -n : n) * orbitFwd;

        if (flip)
        {
            _mirror = -_mirror;
            _spin = Mathf.PI - _spin;
        }
    }

    void UpdateGround(Vector3 pos, float dt)
    {
        Vector3 n = _normal;
        _groundNormal = n;

        // Body velocity RELATIVE to what we stand on: a moving platform is not walking.
        Transform s = null;
        for (int i = 0; i < _legs.Length; i++)
            if (!_legs[i].stepping && _legs[i].foot.t) { s = _legs[i].foot.t; break; }

        if (s != _support)
        {
            _support = s;
            _supportSeeded = false;
        }

        if (s)
        {
            Vector3 local = s.InverseTransformPoint(pos);
            if (_supportSeeded) _relVel = s.TransformVector(local - _supportLocal) / dt;
            _supportLocal = local;
            _supportSeeded = true;
        }
        else _relVel = _vel;

        Vector3 planar = Vector3.ProjectOnPlane(_relVel, n);
        float speed = planar.magnitude;
        Vector3 moveDir = speed > 1e-3f ? planar / speed : Vector3.zero;

        // Ring heading rides the support like a child, then eases toward travel direction
        // (never flips 180 on reverse).
        if (s && s == _frameSupport) _ringFwd = s.TransformDirection(_ringFwdLocal);
        Basis(n, out Vector3 right, out Vector3 fwd);
        if (speed > 0.1f)
        {
            Vector3 d = Vector3.Dot(moveDir, fwd) < 0f ? -moveDir : moveDir;
            fwd = Vector3.Slerp(fwd, d, 1f - Mathf.Exp(-FrameFollow * dt)).normalized;
            right = Vector3.Cross(n, fwd).normalized;
            fwd = Vector3.Cross(right, n);
        }
        _ringFwd = fwd;
        _frameSupport = s;
        if (s) _ringFwdLocal = s.InverseTransformDirection(fwd);

        // Turn rate from the change in travel direction.
        float turnRate = 0f;
        if (speed > walkSpeed * 0.15f)
        {
            if (_prevMoveValid) turnRate = Vector3.SignedAngle(Vector3.ProjectOnPlane(_prevMove, n), moveDir, n) / dt;
            _prevMove = moveDir;
            _prevMoveValid = true;
        }
        else _prevMoveValid = false;

        _gait = Mathf.Clamp(Mathf.Max(speed / walkSpeed, Mathf.Lerp(MinGait, 1f, Mathf.Abs(_turn))), MinGait, MaxGait);

        float targetTurn = Mathf.Clamp(turnRate / FullTurnRate, -1f, 1f);
        float tk = Mathf.Abs(targetTurn) > Mathf.Abs(_turn) ? 7f : 3f;
        _turn = Mathf.Lerp(_turn, targetTurn, 1f - Mathf.Exp(-tk * _gait * dt));

        _wanderPhase += dt * 1.35f * _gait;

        float speed01 = Mathf.Clamp01(speed / walkSpeed);
        float wanderAmp = footDistance * 0.02f * organic * Mathf.Clamp01(speed01 + Mathf.Abs(_turn) * 0.5f);
        float follow = PlantedFollow * dt, retarget = 1f - Mathf.Exp(-14f * dt), lift = FootLift;
        float lifted = stepHeight * (1f + 0.18f * speed01);
        Vector3 lead = planar * (stepDuration * 1.3f);
        int airborneMask = 0, airborne = 0;
        float worst = 0f;

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float slot = Slot(l);
            l.rootGoal = right * Mathf.Cos(slot) + fwd * Mathf.Sin(slot);

            // Turning: outside legs reach, inside legs tuck, quadrants shear.
            float side = Mathf.Cos(slot), fore = Mathf.Sin(slot);
            float a = slot - _turn * side * fore * TurnAngle;
            float rScale = 1f + (Mathf.Max(0f, -_turn * side) - 0.65f * Mathf.Max(0f, _turn * side)) * TurnRadius;

            Vector3 wander = (right * Mathf.Sin(_wanderPhase + l.phase) +
                              fwd * Mathf.Cos(_wanderPhase * 0.73f + l.phase * 1.31f)) * wanderAmp;

            l.desired = pos + (right * Mathf.Cos(a) + fwd * Mathf.Sin(a)) * (Reach(l) * rScale) + lead + wander;

            if (!l.stepping)
            {
                Vector3 p = l.foot.Get();
                if (speed > 1e-3f)
                {
                    p += planar * follow;
                    l.foot.Set(p, l.foot.t);
                }
                SetPlanted(ref l);
                worst = Mathf.Max(worst, Vector3.ProjectOnPlane(l.desired - p, n).sqrMagnitude);
                continue;
            }

            // Swing: slide the landing point within its surface plane toward the newest target.
            Vector3 end = l.to.Get();
            end = Vector3.Lerp(end, l.desired - l.toN * Vector3.Dot(l.desired - end, l.toN), retarget);
            l.to.Set(end, l.to.t);

            l.timer += dt * _gait;
            float t = l.timer / (stepDuration * (1f + l.rTiming * 0.3f * organic));

            if (t >= 1f)
            {
                l.stepping = false;
                bool hit = Probe(end, l.toN, lift + stepHeight, ProbeDown, out Vector3 foot, out Vector3 hn, out Transform hs);
                l.foot.Set(hit ? foot : end, hit ? hs : l.to.t);
                l.footN = hn;
                SetPlanted(ref l);
                continue;
            }

            float e = t * t * (3f - 2f * t);
            e = Mathf.Lerp(e, 1f - Mathf.Pow(1f - t, 2.25f), 0.25f * organic);
            Vector3 basePoint = Vector3.LerpUnclamped(l.from.Get(), end, e);

            l.ground = basePoint - l.toN * lift;
            l.clampN = l.toN;
            l.tip = basePoint + l.toN * (Mathf.Sin(t * Mathf.PI) * lifted * (1f + l.rHeight * 0.3f * organic));
            airborneMask |= 1 << i;
            airborne++;
        }

        // Teleport / respawn: feet are hopelessly far away, re-plant instantly.
        float limit = footDistance * 4f;
        if (worst > limit * limit)
        {
            EnterGround(pos, false);
            return;
        }

        int maxSteps = Mathf.Max(1, _legs.Length / 3);
        if (airborne >= maxSteps || Time.time - _lastStepTime < stepDuration * 0.4f / _gait)
            return;

        int spacing = _legs.Length >= 5 ? 2 : 1;
        int best = -1;
        float bestScore = 0f;

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            if (l.stepping || !SpacingOk(i, airborneMask, spacing)) continue;

            Vector3 delta = Vector3.ProjectOnPlane(l.desired - l.tip, n);
            float dist = delta.magnitude;

            // Hard gate. A foot with no surface under it gets a much lower gate so it re-plants.
            if (dist < (l.foot.t ? stepDistance : stepDistance * 0.25f)) continue;

            float score = dist / stepDistance + Mathf.Max(0f, Vector3.Dot(delta, moveDir)) / dist * DragPriority;
            if (!l.foot.t) score += 2f;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0) return;

        ref Leg sel = ref _legs[best];
        sel.stepping = true;
        sel.timer = 0f;
        sel.from = sel.foot;

        if (Probe(sel.desired, n, ProbeUp, ProbeDown, out Vector3 target, out Vector3 tn, out Transform ts))
            sel.to.Set(target, ts);
        else
            sel.to.Set(sel.desired - n * Vector3.Dot(sel.desired - sel.tip, n), null); // stay in current plane

        sel.toN = tn;
        _lastStepTime = Time.time;
    }

    bool SpacingOk(int i, int airborneMask, int spacing)
    {
        int count = _legs.Length;
        for (int j = 0; j < count; j++)
        {
            if ((airborneMask & (1 << j)) == 0) continue;
            int d = Mathf.Abs(i - j);
            if (Mathf.Min(d, count - d) < spacing) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------------
    // Air
    // ---------------------------------------------------------------------

    void EnterAir(Vector3 pos)
    {
        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            l.airStart = l.tip - pos; // translation-carried, so the peel-off travels with the virus
            l.stepping = false;
        }

        // Air ring starts exactly on the ground ring: same axis, same slots.
        _airAxis = _axisGoal = _groundNormal;
        Basis(_groundNormal, out _orbitRef, out _);

        float speed = _vel.magnitude;
        _flightDir = speed > HoldSpeed ? _vel / speed : _ringFwd;
        _airSpeed01 = _airTime = _landing = _airTurn = 0f;
        _hasLand = false;
    }

    void UpdateAir(Vector3 pos, float dt)
    {
        Vector3 vel = _vel;
        float speed = vel.magnitude;
        Vector3 prevDir = _flightDir;

        // Persistent flight direction: follows velocity, holds when nearly stopped.
        if (speed > HoldSpeed)
        {
            Vector3 d = vel / speed;
            float k = 1f - Mathf.Exp(-9f * dt);
            _flightDir = Vector3.Dot(_flightDir, d) < -0.995f
                ? Quaternion.AngleAxis(180f * k, _orbitRef) * _flightDir
                : Vector3.Slerp(_flightDir, d, k);
            _flightDir.Normalize();
        }

        float turn = Mathf.Clamp01(Vector3.Angle(prevDir, _flightDir) / dt / 150f);
        _airTurn = Mathf.Lerp(_airTurn, turn, 1f - Mathf.Exp(-(turn > _airTurn ? 6f : 2.5f) * dt));

        // Hovering: legs hang below. Moving: legs trail behind the flight direction.
        Vector3 hoverUp = Physics.gravity.sqrMagnitude > 1e-6f ? -Physics.gravity.normalized : _b.up;
        _airSpeed01 = Mathf.Lerp(_airSpeed01, Mathf.Clamp01(speed / (walkSpeed * 1.5f)), 1f - Mathf.Exp(-4f * dt));
        Vector3 g = Vector3.Lerp(hoverUp, _flightDir, _airSpeed01);
        if (g.sqrMagnitude > 0.04f) _axisGoal = g.normalized; // hold through the ambiguous up/down crossover
        _airAxis = Vector3.Slerp(_airAxis, _axisGoal, 1f - Mathf.Exp(-5f * dt)).normalized;

        // Carry the ring reference along with the axis: continuous, never flips.
        _orbitRef = Vector3.ProjectOnPlane(_orbitRef, _airAxis);
        _orbitRef = _orbitRef.sqrMagnitude > 1e-6f ? _orbitRef.normalized : Perp(_airAxis);
        Vector3 orbitFwd = Vector3.Cross(_orbitRef, _airAxis);

        // Spot the surface we're heading into (or hovering over).
        Vector3 probeDir = speed > HoldSpeed ? _flightDir : -hoverUp;
        float cast = landingDistance + speed * 0.3f;
        float want = 0f;
        if (Cast(pos, probeDir, cast, out RaycastHit hit))
        {
            want = 1f - hit.distance / cast;
            _landPoint.Set(hit.point, SupportOf(hit));
            _landNormal = hit.normal;
            _hasLand = true;
        }

        _landing = Mathf.Lerp(_landing, want, 1f - Mathf.Exp(-(want > _landing ? 12f : 4f) * dt));
        if (want == 0f && _landing < 0.02f)
        {
            _landing = 0f;
            _hasLand = false;
        }

        // Spin: full at speed, gentle while hovering, winds down while landing.
        float spinRate = airSpin * Mathf.Deg2Rad * Mathf.Lerp(0.25f, 1f, _airSpeed01) * (1f - _landing);
        _spin = Mathf.Repeat(_spin + spinRate * dt, Mathf.PI * 2f);
        _airTime += dt;

        // Landing map: rotate the air ring onto the surface (circle stays a circle).
        Vector3 landPoint = default, landUp = default;
        Quaternion toLand = Quaternion.identity;
        if (_hasLand)
        {
            landPoint = _landPoint.Get();
            landUp = _landNormal;
            toLand = Quaternion.FromToRotation(_airAxis, Vector3.Dot(_airAxis, landUp) < 0f ? -landUp : landUp);
        }

        float spread = Mathf.Lerp(1f, 0.65f, _airSpeed01);
        float back = Mathf.Lerp(0.35f, 0.75f, _airSpeed01) * airSweep;
        float maxLen = legRootRadius + footDistance * 1.5f, lift = FootLift, time = Time.time;

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float reach = Reach(l);
            float a = Slot(l);
            Vector3 radial = _orbitRef * Mathf.Cos(a) + orbitFwd * Mathf.Sin(a);
            l.rootGoal = radial;

            float af = a + Mathf.Sin(time * 1.1f + l.phase) * 0.35f * airFlow;
            Vector3 flow = _orbitRef * Mathf.Cos(af) + orbitFwd * Mathf.Sin(af);
            float len = reach * (1f + Mathf.Sin(time * 1.7f + l.phase * 1.3f) * 0.12f * airFlow);
            Vector3 target = pos + (flow * spread - _airAxis * back).normalized * len;
            l.clampN = Vector3.zero;

            // Staggered reach: each leg goes out to its own spot and plants there.
            if (_hasLand)
            {
                float r = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_landing - l.delay) / 0.35f));
                if (r > 0f)
                {
                    Vector3 spot = landPoint + (toLand * radial) * reach + landUp * lift;
                    target = Vector3.Lerp(target, pos + Vector3.ClampMagnitude(spot - pos, maxLen), r);
                    if (r > 0.3f)
                    {
                        l.clampN = landUp;
                        l.ground = spot - landUp * lift;
                    }
                }
            }

            // Takeoff: each foot peels from where it was into the flight pose, staggered.
            float tb = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_airTime - l.delay * 0.4f) / TakeoffTime));
            l.tip = Vector3.LerpUnclamped(pos + l.airStart, target, tb);
        }
    }

    // ---------------------------------------------------------------------
    // Mesh
    // ---------------------------------------------------------------------

    void Build()
    {
        Cleanup();
        _b = body ? body : transform;
        _rb = movement ? movement.GetComponent<Rigidbody>() : null;
        if (!_rb) _rb = _b.GetComponentInParent<Rigidbody>();
        _self = _rb ? _rb.transform : movement ? movement.transform : _b;

        int count = legCount, rings = lengthSegments + 1, sides = radialSegments;
        int per = rings * sides + 2;
        _rings = rings;
        _sides = sides;

        var go = new GameObject("__FluidLegs") { hideFlags = HideFlags.DontSave };
        _meshT = go.transform;
        _meshT.SetParent(_b, false);

        _mesh = new Mesh { name = "FluidLegs" };
        _mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = go.AddComponent<MeshRenderer>();
        _renderer.enabled = enabled;
        OnValidate();

        _v = new Vector3[count * per];
        _n = new Vector3[count * per];
        var uv = new Vector2[count * per];
        var tri = new int[count * rings * sides * 6];

        _cos = new float[sides];
        _sin = new float[sides];
        for (int s = 0; s < sides; s++)
        {
            float a = s / (float)sides * Mathf.PI * 2f;
            _cos[s] = Mathf.Cos(a);
            _sin[s] = Mathf.Sin(a);
        }

        _groundNormal = _b.up;
        _ringFwd = Vector3.ProjectOnPlane(_b.forward, _b.up).normalized;
        _frameSupport = null;
        _spin = 0f;
        _mirror = 1f;

        _legs = new Leg[count];
        Basis(_b.up, out Vector3 right, out Vector3 fwd);
        Vector3 pos = _b.position;
        int seed = GetInstanceID() * 7919; // every virus instance gets its own rhythm
        int ti = 0;

        for (int i = 0; i < count; i++)
        {
            ref Leg l = ref _legs[i];
            int h = seed + i * 101;

            l.angle = Mathf.PI * 2f * i / count;
            l.phase = Hash01(h + 13) * Mathf.PI * 2f;
            l.rReach = Hash01(h + 3) * 2f - 1f;
            l.rTiming = Hash01(h + 7) * 2f - 1f;
            l.rHeight = Hash01(h + 11) * 2f - 1f;
            l.delay = 0.05f + Hash01(h + 17) * 0.35f;

            Vector3 radial = right * Mathf.Cos(l.angle) + fwd * Mathf.Sin(l.angle);
            l.rootDir = l.rootGoal = radial;
            l.tip = pos + radial * Reach(l);
            l.bendUp = _b.up;

            // Winding a->b->c gives outward faces for ring frame (side, up, tangent).
            int vb = i * per;
            for (int r = 0; r < rings - 1; r++)
            for (int s = 0; s < sides; s++)
            {
                int ns = (s + 1) % sides;
                int a0 = vb + r * sides + s, a1 = vb + r * sides + ns;
                tri[ti++] = a0; tri[ti++] = a1; tri[ti++] = a0 + sides;
                tri[ti++] = a1; tri[ti++] = a1 + sides; tri[ti++] = a0 + sides;
            }

            int rootCap = vb + rings * sides, tipCap = rootCap + 1, last = vb + (rings - 1) * sides;
            for (int s = 0; s < sides; s++)
            {
                int ns = (s + 1) % sides;
                tri[ti++] = rootCap; tri[ti++] = vb + ns; tri[ti++] = vb + s;
                tri[ti++] = tipCap; tri[ti++] = last + s; tri[ti++] = last + ns;
            }

            for (int r = 0; r < rings; r++)
            for (int s = 0; s < sides; s++)
                uv[vb + r * sides + s] = new Vector2(s / (float)sides, r / (float)(rings - 1));

            uv[rootCap] = new Vector2(0.5f, 0f);
            uv[tipCap] = new Vector2(0.5f, 1f);
        }

        _mesh.SetVertices(_v);
        _mesh.SetNormals(_n);
        _mesh.SetUVs(0, uv);
        _mesh.SetTriangles(tri, 0, false);
        _modeReady = false;
    }

    void Cleanup()
    {
        if (_meshT) Destroy(_meshT.gameObject);
        if (_mesh) Destroy(_mesh);
        _legs = null;
    }

    // Cheap conservative bounds every frame so culling stays correct while mesh work is skipped.
    void UpdateBounds(Vector3 pos)
    {
        float r2 = 0f;
        for (int i = 0; i < _legs.Length; i++)
            r2 = Mathf.Max(r2, (_legs[i].tip - pos).sqrMagnitude);

        float e = Mathf.Sqrt(r2) + legRootRadius * 2f + curveHeight + stepHeight +
                  baseRadius * 1.5f + footDistance * 0.2f + 0.25f;

        Vector3 s = _meshT.lossyScale;
        float scale = Mathf.Max(1e-4f, Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z))));
        _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (2f * e / scale));
    }

    void BuildMesh(Vector3 pos)
    {
        Matrix4x4 w2l = _meshT.worldToLocalMatrix;
        float wobbleAmp = footDistance * 0.05f * organic;
        float airWave = _air ? airFlow * footDistance * 0.1f * (0.3f + 0.7f * _airTurn) : 0f;
        float wavePhase = Time.time * 6.5f;
        int rings = _rings, sides = _sides, per = rings * sides + 2;
        float invRings = 1f / (rings - 1);

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];

            Vector3 p0 = pos + l.rootDir * legRootRadius;
            Vector3 p3 = l.tip;
            Vector3 d = p3 - p0;
            float len = d.magnitude;
            Vector3 dir = len > 1e-4f ? d / len : l.rootDir;
            len = Mathf.Max(len, 1e-4f);

            // Bend frame from its (smoothed) semantic up; reuse last side only when degenerate.
            Vector3 up = Vector3.ProjectOnPlane(l.bendUp, dir);
            Vector3 side;
            if (up.sqrMagnitude > 1e-5f)
                side = Vector3.Cross(dir, up).normalized;
            else
            {
                side = Vector3.ProjectOnPlane(l.side, dir);
                side = side.sqrMagnitude > 1e-6f ? side.normalized : Perp(dir);
            }
            up = Vector3.Cross(side, dir);
            l.side = side;

            Vector3 wob = side * (Mathf.Sin(_wobblePhase + l.phase) * wobbleAmp);
            float arch = curveHeight + (l.stepping ? stepHeight * 0.2f : 0f);
            Vector3 p1 = p0 + dir * (len * CurveBias) + up * arch + wob;
            Vector3 p2 = p3 - dir * (len * (1f - CurveBias) * 0.55f) + up * (arch * 0.55f) - wob * 0.5f;

            float thick = Mathf.Clamp(1f - (len / footDistance - 1f) * StretchThickness, 0.55f, 1.45f);
            bool clamp = l.clampN.sqrMagnitude > 0f;
            float wave = clamp ? 0f : airWave; // only free-flying legs writhe

            int vb = i * per;
            Vector3 ringSide = side, firstC = p0, firstT = -dir, c = p3, tan = dir;

            for (int r = 0; r < rings; r++)
            {
                float t = r * invRings, u = 1f - t;

                c = u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
                tan = 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
                tan = tan.sqrMagnitude > 1e-8f ? tan.normalized : dir;

                float env = Mathf.Sin(t * Mathf.PI);
                if (wave > 0f)
                {
                    float wp = wavePhase + l.phase + t * Mathf.PI * 2.4f;
                    c += side * (Mathf.Sin(wp) * wave * env) + up * (Mathf.Cos(wp * 0.73f) * wave * env * 0.65f);
                }

                float radius = Mathf.Lerp(baseRadius, tipRadius, t) * thick * (1f + env * 0.06f * organic);

                // Analytic clearance against the foot's contact plane: zero raycasts.
                if (clamp)
                {
                    float hgt = Vector3.Dot(c - l.ground, l.clampN), req = radius + 0.02f;
                    if (hgt < req) c += l.clampN * (req - hgt);
                }

                // Transported ring frame: projecting the previous side keeps rings from twisting.
                ringSide = Vector3.ProjectOnPlane(ringSide, tan);
                ringSide = ringSide.sqrMagnitude > 1e-6f ? ringSide.normalized : Perp(tan);
                Vector3 ringUp = Vector3.Cross(tan, ringSide);

                if (r == 0)
                {
                    firstC = c;
                    firstT = -tan;
                }

                int b = vb + r * sides;
                for (int s = 0; s < sides; s++)
                {
                    Vector3 o = ringSide * _cos[s] + ringUp * _sin[s];
                    _v[b + s] = w2l.MultiplyPoint3x4(c + o * radius);
                    _n[b + s] = w2l.MultiplyVector(o);
                }
            }

            int cap = vb + rings * sides;
            _v[cap] = w2l.MultiplyPoint3x4(firstC);
            _n[cap] = w2l.MultiplyVector(firstT);
            _v[cap + 1] = w2l.MultiplyPoint3x4(c);
            _n[cap + 1] = w2l.MultiplyVector(tan);
        }

        const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds;
        _mesh.SetVertices(_v, 0, _v.Length, flags);
        _mesh.SetNormals(_n, 0, _n.Length, flags);
    }

    // ---------------------------------------------------------------------
    // Utility
    // ---------------------------------------------------------------------

    static Vector3 Perp(Vector3 n) =>
        Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

    static float Hash01(int value)
    {
        uint x = (uint)value;
        x ^= x >> 16; x *= 0x7feb352d;
        x ^= x >> 15; x *= 0x846ca68b;
        x ^= x >> 16;
        return (x & 0x00FFFFFF) / 16777215f;
    }
}
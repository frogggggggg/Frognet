using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Procedural fluid-leg walker. Knows nothing about any movement system: it
/// reads the body's motion (Rigidbody velocity or transform deltas) and the
/// ground (an ISurfaceContact if one exists, raycasts otherwise).
///
/// Drawn on the GPU: each frame the walker hands LegRenderer one small record per
/// leg (its curve and frame), and every walker's legs draw together as instances
/// of one tube mesh (Custom/BloodCellLegs), shaped in the vertex stage. Only the
/// values that define the look are exposed; everything else derives from them so
/// the motion stays consistent at any scale.
///
/// One leg layout is shared by every mode: each leg owns a fixed slot on a
/// ring, the ring has a heading (_ringFwd) and a phase (_spin). Walking,
/// flying and landing all place legs through that same ring, so no transition
/// ever makes legs swap sides, cross over, or pop.
///
/// Independent of the body's own rotation: a rolling/spinning body does not
/// drag the legs around.
/// </summary>
public class SpiderLegWalker : MonoBehaviour
{
    // A point and its surface normal that ride a (possibly moving) support exactly like a child
    // transform. The normal has to ride too: a world-space one goes stale as the support turns,
    // and the leg's bend and ground clamp are built from it.
    struct Anchor
    {
        public Transform t;
        public Vector3 local, world, localN, normal;

        public void Set(Vector3 w, Transform support)
        {
            world = w;
            t = support;
            if (support)
            {
                local = support.InverseTransformPoint(w);
                localN = support.InverseTransformDirection(normal);
            }
        }

        public void SetNormal(Vector3 n)
        {
            normal = n;
            if (t) localN = t.InverseTransformDirection(n);
        }

        // A destroyed support simply leaves the point at its last world position.
        public Vector3 Get()
        {
            if (t)
            {
                world = t.TransformPoint(local);
                Vector3 n = t.TransformDirection(localN);
                if (n.sqrMagnitude > 1e-8f) normal = n.normalized;
            }
            return world;
        }
    }

    struct Leg
    {
        public float angle, phase, rReach, rHeight, delay; // r* are -1..1 personality seeds
        public bool stepping;
        public int window;          // last gait cycle this leg had its swing window
        public float swingStart, lift;
        public Anchor foot, from, to;
        public Vector3 home, tip, airStart, ground, clampN, bendUp, hipUp, side, rootDir, rootGoal; // bendUp: the foot's surface; hipUp: the body's
        public Anchor aim;            // the goal the swing's target was last matched to, riding the target's support
        public Vector3 knee, kneeVel; // upper control point, relative to the hub, on a spring
        public float plant;           // 0 free (air) .. 1 on a surface, eased
        public Vector3 wig;           // rotation-wiggle tip, relative to the hub, eased
    }

    [Header("References")]
    [Tooltip("Root/body the legs grow from. Leave empty to use this transform.")]
    public Transform body;
    [Tooltip("Look of the legs (a Custom/BloodCellTriplanar material); copied onto the leg shader.")]
    public Material legMaterial;
    [Tooltip("Custom/BloodCellLegs. Filled in when the component is added; keep it assigned for builds.")]
    public Shader legShader;
    public bool castShadows = true;

    [Header("Shape")]
    [Range(1, 32)] public int legCount = 6;
    [Min(0f)] public float legRootRadius = 0.45f;
    [Tooltip("Resting distance from the body center to each foot. Most other distances scale from this.")]
    [Min(0.05f)] public float footDistance = 1.5f;
    [Min(0.001f)] public float baseRadius = 0.10f, tipRadius = 0.055f;
    [Min(0f)] public float curveHeight = 0.24f;
    [Range(0f, 1f), Tooltip("1 = the end of each leg stands straight up from the ground and the rest of the leg pivots around it. 0 = the whole leg bends as one curve.")]
    public float uprightFeet = 1f;
    [Range(3, 24)] public int lengthSegments = 12;
    [Range(3, 16)] public int radialSegments = 8;

    [Header("Walking")]
    [Tooltip("Surfaces feet may plant on. The owner's own colliders are always ignored.")]
    public LayerMask groundMask = ~0;
    [Tooltip("Only without an ISurfaceContact: how close a surface must be to the body center to count as standing on it.")]
    [Min(0.05f)] public float groundDistance = 1f;
    [Min(0.01f)] public float stepDistance = 0.40f;
    [Min(0.02f)] public float stepDuration = 0.14f;
    [Min(0f)] public float stepHeight = 0.22f;
    [Tooltip("Ground speed the step timing is authored for. Faster movement steps faster.")]
    [Min(0.1f)] public float walkSpeed = 4f;
    [Tooltip("0 = perfectly regular robot, 1 = loose and creature-like.")]
    [Range(0f, 1f)] public float organic = 0.6f;

    [Header("Performance")]
    [Min(0f), Tooltip("Beyond this camera distance legs are neither drawn nor simulated (they re-plant on coming back).")]
    public float cullDistance = 90f;
    [Min(0f), Tooltip("Beyond this camera distance legs stop casting shadows.")]
    public float shadowDistance = 25f;
    [Min(0f), Tooltip("Beyond this camera distance legs draw with about half the rings and fewer sides. 0 = never.")]
    public float lodDistance = 35f;
    [Min(0f), Tooltip("Metres off screen the legs keep walking, so one coming into view is already mid-stride instead of re-planting.")]
    public float offscreenMargin = 4f;

    [Header("Rotation")]
    [Tooltip("Spin rate in the air (degrees/s) past which the legs stretch out all around and grab, gathering back once it settles. Fully at twice this. 0 = never.")]
    [Min(0f)] public float rotationWiggle = 150f;

    [Header("Air")]
    [Tooltip("Leg spin in degrees per second at full flight speed. Slows while hovering and while landing.")]
    [Min(0f)] public float airSpin = 300f;
    [Tooltip("How far legs sweep back behind the direction of flight.")]
    [Range(0f, 2f)] public float airSweep = 1f;
    [Tooltip("How much the legs drift and undulate while flying.")]
    [Range(0f, 1f)] public float airFlow = 0.4f;
    [Tooltip("How far ahead a surface is spotted and the legs start reaching for it.")]
    [Min(0.1f)] public float landingDistance = 3f;

    // Derived feel constants. Tuned once, shared by every walker.
    const float CurveBias = 0.35f, StretchThickness = 0.16f, WobbleSpeed = 3.5f;
    const float FrameFollow = 3f, VelocitySmoothing = 12f;
    const float SwingFraction = 0.45f; // share of the gait cycle a foot is in the air; the rest all feet overlap on the ground
    const float MaxStride = 1.2f;      // body travel per cycle (x footDistance) before cadence speeds up instead
    const float MaxSpan = 2f, SpanMargin = 0.4f; // hub-to-foot distance (x footDistance) that forces a catch-up step: planned + margin, capped
    const float KneeFrequency = 9f, KneeDamping = 0.85f; // smooth follow-through, no bounce
    const float TurnAngle = 27f * Mathf.Deg2Rad, TurnRadius = 0.12f, FullTurnRate = 140f;
    const float MinGait = 0.35f, MaxGait = 3f, HoldSpeed = 0.2f, TakeoffTime = 0.25f;
    const float MinProbeFacing = 0.5f;  // ground more than 60° off the probe direction doesn't count (skimmed, not stood on)
    const float GroundExitScale = 1.5f; // auto ground detection hysteresis: leaving needs more distance than arriving

    static readonly RaycastHit[] Hits = new RaycastHit[8];

    Leg[] _legs;
    Transform _b, _self, _support, _frameSupport;
    Rigidbody _rb;
    ISurfaceContact _source;
    LegRenderer.LegData[] _data;
    int _rings, _sides;
    static Shader s_legShader;

    // Shared ring: heading + phase + handedness.
    Vector3 _ringFwd = Vector3.forward, _ringFwdLocal;
    float _spin, _mirror = 1f;

    bool _seeded, _modeReady, _air, _supportSeeded, _prevMoveValid, _hasLand;
    bool _culled;
    Vector3 _lastPos, _vel, _relVel, _normal, _groundNormal = Vector3.up, _supportLocal, _prevMove;
    float _turn, _gait = 1f, _cycle, _wobblePhase, _lastShapeTime = -999f;
    float _hubHeight = -1f; // hub above the surface it stands on, learned from feet on that surface (-1 unknown)

    // Rotation wiggle: 0 composed .. 1 fully loose.
    float _wiggle, _turnRate;

    // Support rotation last frame: world-space leg shape (roots, bends, knees) is turned with it.
    Transform _carrySupport;
    Quaternion _carryRot = Quaternion.identity;
    Vector3 _grabAxis = Vector3.down, _grabRef = Vector3.right; // world frame the grab directions hang in
    Quaternion _prevRot = Quaternion.identity;
    bool _wiggleSeeded;

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

    void Reset() => legShader = Shader.Find("Custom/BloodCellLegs");

    void OnEnable()
    {
        _culled = false;
        _seeded = false;
        _modeReady = false;
        SimulationTicker.Register(this);
    }

    void OnDisable() => SimulationTicker.Unregister(this);

    void OnDestroy() => _legs = null;

    Shader LegShader => legShader ? legShader : s_legShader ? s_legShader : s_legShader = Shader.Find("Custom/BloodCellLegs");

    [ContextMenu("Rebuild Legs")]
    public void RebuildLegs() => _legs = null;

    // Called by SimulationTicker every frame, after every Organism's late tick.
    public void Tick()
    {
        if (_legs == null || _legs.Length != legCount ||
            _rings != lengthSegments + 1 || _sides != radialSegments)
            Build();

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // Far from the camera or well off screen: not drawn or simulated at all.
        float camDist = SimulationTicker.HasCamera ? Vector3.Distance(SimulationTicker.CameraPosition, _b.position) : 0f;
        if (camDist > cullDistance || !LegRenderer.Visible(LegBounds(_b.position), offscreenMargin))
        {
            _culled = true;
            return;
        }
        if (_culled)
        {
            _culled = false;
            _seeded = _modeReady = false; // re-plant from scratch
        }

        // Rigidbody velocity when available: transform deltas of a non-interpolated
        // body flicker between 0 and full speed whenever FixedUpdate is skipped.
        Vector3 pos = _b.position;
        _vel = _rb && !_rb.isKinematic
            ? _rb.linearVelocity
            : _seeded ? (pos - _lastPos) / dt : Vector3.zero;
        // The hub jumped (respawn, warp): nothing to animate, just re-plant.
        float jumpLimit = footDistance * 2f;
        bool jumped = _seeded && (pos - _lastPos).sqrMagnitude > jumpLimit * jumpLimit;
        _lastPos = pos;
        _seeded = true;

        bool air = !FindSurface(pos, out _normal);
        if (!_modeReady || air != _air)
        {
            if (air) EnterAir(pos);
            else EnterGround(pos, _modeReady);
            _air = air;
            _modeReady = true;
        }

        if (jumped && !air) EnterGround(pos, false);
        CarryWithSupport(air);
        if (air) UpdateAir(pos, dt);
        else UpdateGround(pos, dt);
        UpdateWiggle(pos, air, dt);

        _wobblePhase += dt * WobbleSpeed * (air ? 1f : _gait);

        float rootK = 1f - Mathf.Exp(-14f * dt), bendK = 1f - Mathf.Exp(-8f * dt);
        Vector3 bodyUp = BodyUp(pos);
        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            l.rootDir = Vector3.Slerp(l.rootDir, l.rootGoal, rootK).normalized;
            l.bendUp = Vector3.Slerp(l.bendUp, l.clampN.sqrMagnitude > 0f ? l.clampN : -bodyUp, bendK);
            l.hipUp = Vector3.Slerp(l.hipUp, air ? -bodyUp : _normal, bendK);
            l.plant = Mathf.Lerp(l.plant, l.clampN.sqrMagnitude > 0f ? 1f : 0f, bendK);
        }

        // On screen (padded for the frame-late view and nearby shadows): hand this frame's legs
        // to the GPU, with a lighter tube far away.
        Bounds bounds = LegBounds(pos);
        if (legMaterial && LegRenderer.Visible(bounds, footDistance))
        {
            WriteLegs(pos, Mathf.Min(Time.time - _lastShapeTime, 0.1f));
            bool far = lodDistance > 0f && camDist > lodDistance;
            int rings = far ? Mathf.Min(_rings, Mathf.Max(4, lengthSegments / 2 + 1)) : _rings;
            int sides = far ? Mathf.Min(_sides, Mathf.Max(5, radialSegments * 2 / 3)) : _sides;
            LegRenderer.Submit(legMaterial, LegShader, rings, sides, castShadows && camDist < shadowDistance,
                               _data, _legs.Length, bounds);
        }
    }

    /// <summary>
    /// Standing on something? An ISurfaceContact answers exactly. Otherwise probe
    /// toward the last ground normal (and the direction of travel), with a
    /// longer reach to leave than to arrive so it doesn't flicker at the edge.
    /// </summary>
    bool FindSurface(Vector3 pos, out Vector3 normal)
    {
        if (_source != null)
        {
            Vector3 n = _source.SurfaceNormal;
            normal = n.sqrMagnitude > 1e-6f ? n.normalized : _b.up;
            return _source.OnSurface;
        }

        float reach = groundDistance * (_air ? 1f : GroundExitScale);
        if (Cast(pos, -_groundNormal, reach, out RaycastHit hit) ||
            (_vel.sqrMagnitude > HoldSpeed * HoldSpeed && Cast(pos, _vel.normalized, reach, out hit)))
        {
            normal = hit.normal;
            return true;
        }

        normal = _groundNormal;
        return false;
    }

    // Spinning in the air: the legs stretch out all around and grab at space of their pose and writhe around the hub, then blend back onto wherever the
    // gait has their feet as it settles, which reads as them gathering themselves after the turn.
    // Purely an overlay on the tips: the gait keeps running underneath, so nothing jumps after.
    void UpdateWiggle(Vector3 pos, bool air, float dt)
    {
        // Air only. On the ground the body's up is held on the surface normal, so its only rotation
        // is turning to a new heading (or following a curved cell), which must not set this off.
        // Landing mid-wiggle just lets it wind down onto the planted feet.
        Quaternion rot = _b.rotation;
        float rate = air && _wiggleSeeded ? Quaternion.Angle(_prevRot, rot) / dt : 0f;
        _prevRot = rot;
        _wiggleSeeded = air;

        // Frame-to-frame rotation is noisy; filter it so the legs don't flicker in and out.
        _turnRate = Mathf.Lerp(_turnRate, rate, 1f - Mathf.Exp(-10f * dt));
        float goal = rotationWiggle > 0f ? Mathf.Clamp01((_turnRate - rotationWiggle) / rotationWiggle) : 0f;
        bool starting = _wiggle <= 0f;
        _wiggle = Mathf.Lerp(_wiggle, goal, 1f - Mathf.Exp(-(goal > _wiggle ? 8f : 4f) * dt));
        if (_wiggle < 1e-3f) { _wiggle = 0f; return; }

        Vector3 up = BodyUp(pos);
        if (starting)
        {
            _grabAxis = -up;
            _grabRef = Perp(_grabAxis);
        }

        int count = _legs.Length;
        float time = Time.time, follow = 1f - Mathf.Exp(-14f * dt);
        for (int i = 0; i < count; i++)
        {
            ref Leg l = ref _legs[i];
            float ph = l.phase;

            // Grab all around: each leg owns a direction spread over a cap below and around the hub
            // (golden-angle spiral), held in the world rather than spinning with the body, so a
            // spinning body sweeps its legs through space. The cap churns slowly.
            float y = Mathf.Lerp(1f, -0.25f, (i + 0.5f) / count); // 1 straight out below, <0 a little above the hub
            float az = (i * 2.39996f + time * 0.4f) * Mathf.Rad2Deg;
            Vector3 dir = _grabAxis * y + (Quaternion.AngleAxis(az, _grabAxis) * _grabRef) * Mathf.Sqrt(1f - y * y);
            dir += new Vector3(Mathf.Sin(time * 1.7f + ph), Mathf.Sin(time * 1.3f + ph * 1.9f), Mathf.Sin(time * 1.1f + ph * 2.7f)) * 0.15f;

            // Never into the body.
            dir.Normalize();
            float over = Vector3.Dot(dir, up) - 0.25f;
            if (over > 0f) dir = (dir - up * over).normalized;

            // Reach out long, snatch back quick.
            float g = Mathf.Repeat(time * 1.4f + ph / (Mathf.PI * 2f), 1f);
            float ext = g < 0.7f ? Mathf.SmoothStep(0f, 1f, g / 0.7f) : 1f - Mathf.SmoothStep(0f, 1f, (g - 0.7f) / 0.3f);
            float len = Reach(l) * Mathf.Lerp(0.75f, 1.5f, ext);

            if (starting) l.wig = l.tip - pos;
            l.wig = Vector3.Lerp(l.wig, dir * len, follow);
            l.tip = Vector3.Lerp(l.tip, pos + l.wig, _wiggle);
        }
    }

    // Standing on something that turns: turn the smoothed, world-space parts of the leg shape
    // with it, or they lag the turn and the legs bend off to one side until the next step.
    void CarryWithSupport(bool air)
    {
        Transform sup = air ? null : OwnSurface;
        if (sup && sup == _carrySupport)
        {
            Quaternion d = sup.rotation * Quaternion.Inverse(_carryRot);
            if (Quaternion.Angle(d, Quaternion.identity) > 1e-3f)
                for (int i = 0; i < _legs.Length; i++)
                {
                    ref Leg l = ref _legs[i];
                    l.rootDir = d * l.rootDir;
                    l.bendUp = d * l.bendUp;
                    l.hipUp = d * l.hipUp;
                    l.side = d * l.side;
                    l.knee = d * l.knee;
                    l.kneeVel = d * l.kneeVel;
                }
        }
        _carrySupport = sup;
        _carryRot = sup ? sup.rotation : Quaternion.identity;
    }

    // From the leg hub into the owner's body: the side legs must never grow toward.
    Vector3 BodyUp(Vector3 hub)
    {
        Vector3 d = _self.position - hub;
        return d.sqrMagnitude > 1e-4f ? d.normalized : _self.up;
    }

    // Ring basis on a plane, from the shared heading. Never uses the body's rotation.
    void Basis(Vector3 n, out Vector3 right, out Vector3 fwd)
    {
        fwd = Vector3.ProjectOnPlane(_ringFwd, n);
        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Perp(n);
        right = Vector3.Cross(n, fwd);
    }

    // Closest ground hit that is not part of the owner.
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

    // The collider itself, not its Rigidbody: cells that move inside a body carry their feet.
    static Transform SupportOf(in RaycastHit hit) => hit.collider.transform;

    // Of every surface along the probe, the one nearest the wanted point, so a foot aimed at
    // the ground beside another cell doesn't plant on top of that cell. The surface the body
    // stands on wins whenever it's there at all within reach: a foot on a neighbour stays behind
    // when our own cell moves, which pulls the legs off the body.
    bool Probe(Vector3 p, Vector3 n, float up, float down, out Vector3 foot, out Vector3 normal, out Transform support)
    {
        int count = Physics.RaycastNonAlloc(p + n * up, -n, Hits, up + down, groundMask, QueryTriggerInteraction.Ignore);
        Transform own = OwnSurface;
        int bestI = -1, ownI = -1;
        float bestGap = float.MaxValue, ownGap = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = Hits[i];
            // Only ground the probe meets roughly head-on. Aimed past a cube's edge, the probe skims
            // down beside the side face and caught a bump on it far below: feet planted a couple of
            // leg lengths down the side. Rejected here, the step wraps round the edge instead.
            if (h.distance <= 0f || Vector3.Dot(h.normal, n) < MinProbeFacing || h.collider.transform.IsChildOf(_self)) continue;
            float gap = Mathf.Abs(h.distance - up);
            if (gap < bestGap) { bestGap = gap; bestI = i; }
            if (own && SameBody(h.collider.transform, own) && gap < ownGap) { ownGap = gap; ownI = i; }
        }
        if (ownI >= 0 && ownGap < footDistance * 0.75f) bestI = ownI;

        if (bestI >= 0)
        {
            RaycastHit hit = Hits[bestI];
            normal = hit.normal;
            foot = hit.point + normal * FootLift;
            support = SupportOf(hit);
            return true;
        }

        foot = p;
        normal = n;
        support = own; // nothing hit: still ride our own cell rather than hang fixed in the world
        return false;
    }

    // What the body stands on (null in the air or without an ISurfaceContact).
    Transform OwnSurface => _source != null && _source.OnSurface ? _source.Surface : null;

    static bool SameBody(Transform a, Transform b)
    {
        if (a == b || a.IsChildOf(b) || b.IsChildOf(a)) return true;
        Rigidbody ra = a.GetComponentInParent<Rigidbody>();
        return ra && ra == b.GetComponentInParent<Rigidbody>();
    }

    void SetPlanted(ref Leg l)
    {
        l.tip = l.foot.world;
        l.ground = l.tip - l.foot.normal * FootLift;
        l.clampN = l.foot.normal;
    }

    // ---------------------------------------------------------------------
    // Ground
    // ---------------------------------------------------------------------

    void EnterGround(Vector3 pos, bool fromAir)
    {
        Vector3 n = _normal;
        if (fromAir) AlignRingToLanding(n);
        Basis(n, out Vector3 right, out Vector3 fwd);

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float a = Slot(l);
            Vector3 radial = right * Mathf.Cos(a) + fwd * Mathf.Sin(a);
            Vector3 rest = pos + radial * Reach(l);
            l.rootGoal = radial;
            l.home = rest;
            l.stepping = false;

            if (fromAir)
            {
                // Landing: each foot steps from wherever it is in the air onto its spot, so
                // nothing snaps (catching feet where they touched let misses jump to rest).
                l.foot.Set(l.tip, OwnSurface);
                l.foot.SetNormal(n);
                StartStep(ref l, pos, n, rest, _cycle + GaitOffset(i, l));
                continue;
            }

            Probe(rest, n, ProbeUp, ProbeDown, out Vector3 foot, out Vector3 hn, out Transform s);
            l.foot.Set(foot, s);
            l.foot.SetNormal(hn);
            SetPlanted(ref l);
            l.rootDir = radial;
        }

        _groundNormal = n;
        _support = _frameSupport = null;
        _supportSeeded = _prevMoveValid = _hasLand = false;
        _relVel = Vector3.zero; // attaching stops the body dead; flight velocity here aimed landing steps ahead
        _turn = _landing = 0f;
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
        // The owner's own surface when it reports one; a foot on a neighbouring cell moves differently.
        Transform s = _source?.Surface;
        if (!s)
            for (int i = 0; i < _legs.Length; i++)
                if (!_legs[i].stepping && _legs[i].foot.t) { s = _legs[i].foot.t; break; }

        if (s != _support)
        {
            _support = s;
            _supportSeeded = false;
        }

        // Frame deltas are noisy; smooth so lead and gait don't jitter the feet.
        Vector3 rawVel = _vel;
        if (s)
        {
            Vector3 local = s.InverseTransformPoint(pos);
            rawVel = _supportSeeded ? s.TransformVector(local - _supportLocal) / dt : _relVel;
            _supportLocal = local;
            _supportSeeded = true;
        }
        _relVel = Vector3.Lerp(_relVel, rawVel, 1f - Mathf.Exp(-VelocitySmoothing * dt));

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

        // ---- Gait clock ----
        // One shared rhythm: legs alternate in two groups (a wave for odd counts). A fixed cadence
        // at low speed; once strides reach MaxStride the cadence rises instead. Each foot lands
        // where it will sit under its rest spot at mid-stance, so feet lead rather than trail.
        float rate = Mathf.Max(SwingFraction / stepDuration, speed / (footDistance * MaxStride)); // cycles/s
        float swingTime = SwingFraction / rate, stanceTime = (1f - SwingFraction) / rate;
        float lift = FootLift;
        // Past the farthest a foot is ever planned (rest reach + the lead at this speed) plus a
        // margin, a foot steps out of turn: over a cube's edge the feet left on the old face
        // otherwise stayed planted until twice the leg's length away.
        float maxSpan = Mathf.Min(footDistance * (1f + SpanMargin) + speed * (swingTime + stanceTime * 0.5f), footDistance * MaxSpan);
        bool moving = speed > HoldSpeed;
        bool active = moving;

        // Rest spots (turning shapes them), and whether anything needs the clock running.
        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float slot = Slot(l);
            l.rootGoal = right * Mathf.Cos(slot) + fwd * Mathf.Sin(slot);

            // Turning: outside legs reach, inside legs tuck, quadrants shear.
            float side = Mathf.Cos(slot), fore = Mathf.Sin(slot);
            float a = slot - _turn * side * fore * TurnAngle;
            float rScale = 1f + (Mathf.Max(0f, -_turn * side) - 0.65f * Mathf.Max(0f, _turn * side)) * TurnRadius;
            l.home = pos + (right * Mathf.Cos(a) + fwd * Mathf.Sin(a)) * (Reach(l) * rScale);

            if (l.stepping || !l.foot.t || PlanarDist(l.foot.Get(), l.home, n) > stepDistance * 0.5f) active = true;
        }

        if (active) _cycle += rate * dt; // idle and settled: the rhythm holds still

        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            float legCycle = _cycle + GaitOffset(i, l);
            int window = Mathf.FloorToInt(legCycle);

            if (!l.stepping)
            {
                Vector3 p = l.foot.Get();
                SetPlanted(ref l);

                // Swing only in this leg's window, once per cycle, and only if it would actually
                // move. An overstretched foot (its cell drifted off) goes now, out of turn.
                Transform own = OwnSurface;
                bool strayed = own && l.foot.t && !SameBody(l.foot.t, own) && PlanarDist(p, l.home, n) > stepDistance;
                bool overstretched = strayed || (p - pos).sqrMagnitude > maxSpan * maxSpan;
                bool windowOpen = window != l.window && legCycle - window < SwingFraction;
                if (!windowOpen && !overstretched) continue;
                l.window = window;

                Vector3 target = l.home + planar * (swingTime + stanceTime * 0.5f);
                float need = stepDistance * 0.5f; // standing still too: no leftover offsets
                if (!overstretched && l.foot.t && PlanarDist(p, target, n) < need) continue;

                StartStep(ref l, pos, n, target, legCycle);
            }

            float t = Mathf.Clamp01((legCycle - l.swingStart) / SwingFraction);

            // Keep aiming at where mid-stance will be as the body's motion changes: follow how
            // the goal moves, along the landing face. Not the goal itself: it sits in the body's
            // plane, and a target wrapped round an edge (a cube's side) would be dragged back up
            // off its face into the air.
            Vector3 goal = l.home + planar * ((1f - t) * swingTime + stanceTime * 0.5f);
            // Both ride the support, so its own motion (a moving, turning planet) isn't added twice.
            Vector3 end = l.to.Get() + Vector3.ProjectOnPlane(goal - l.aim.Get(), l.to.normal);
            l.aim.Set(goal, l.to.t);
            l.to.Set(end, l.to.t);

            if (t >= 1f)
            {
                l.stepping = false;
                // Onto the face it aimed at, else the ground under the body's plane, else round an edge.
                bool hit = Probe(end, l.to.normal, lift + stepHeight, ProbeDown, out Vector3 foot, out Vector3 hn, out Transform hs) ||
                           Probe(end, n, ProbeUp, ProbeDown, out foot, out hn, out hs) ||
                           WrapProbe(pos, end, n, out foot, out hn, out hs);
                l.foot.Set(hit ? foot : end, hit ? hs : l.to.t);
                l.foot.SetNormal(hn);
                SetPlanted(ref l);
                CreatureAudio.Step(l.tip, footDistance, _source); // the player's own; crowds mostly feed a patter bed
                continue;
            }

            // Peel up, reach, set down: travel starts a moment after the lift and the arc peaks
            // early, so the foot comes down onto its spot instead of skimming along to it.
            float tt = Mathf.Clamp01((t - 0.1f) / 0.9f);
            float e = tt * tt * tt * (tt * (tt * 6f - 15f) + 10f);
            Vector3 basePoint = Vector3.LerpUnclamped(l.from.Get(), end, e);

            l.ground = basePoint - l.to.normal * lift;
            l.clampN = l.to.normal;
            // Lift along both faces' normals, so round an edge the arc goes out over the corner.
            Vector3 up = l.from.normal + l.to.normal;
            up = up.sqrMagnitude > 1e-4f ? up.normalized : l.to.normal;
            l.tip = basePoint + up * (Mathf.Sin(Mathf.Pow(t, 0.7f) * Mathf.PI) * l.lift);
        }
    }

    // Two alternating groups for even counts (a tripod with six), a neighbour-skipping wave for
    // odd ones. Organic only nudges it.
    float GaitOffset(int i, in Leg l)
    {
        int count = _legs.Length;
        float o = count % 2 == 0 ? (i & 1) * 0.5f : (i * 2 % count) / (float)count;
        return o + (l.phase / (Mathf.PI * 2f) - 0.5f) * 0.06f * organic;
    }

    void StartStep(ref Leg l, Vector3 pos, Vector3 n, Vector3 target, float legCycle)
    {
        Vector3 start = l.foot.Get();
        l.stepping = true;
        l.swingStart = legCycle;
        l.from = l.foot;

        // Nothing under the target: past an edge. Wrap round it onto the face beyond (a cube's
        // side), else try closer in, before giving up and stepping into the current plane.
        if (Probe(target, n, ProbeUp, ProbeDown, out Vector3 hit, out Vector3 tn, out Transform ts) ||
            WrapProbe(pos, target, n, out hit, out tn, out ts) ||
            Probe(Vector3.Lerp(pos, target, 0.55f), n, ProbeUp, ProbeDown, out hit, out tn, out ts))
            l.to.Set(hit, ts);
        else
            l.to.Set(target - n * Vector3.Dot(target - start, n), OwnSurface);

        l.to.SetNormal(tn);
        l.aim.Set(target, l.to.t);

        // Short corrective steps stay low; full strides lift fully.
        float stride = PlanarDist(start, l.to.world, n);
        l.lift = stepHeight * Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(stride / (footDistance * 0.5f)))
                 * (1f + l.rHeight * 0.15f * organic);

        // Stepping round an edge (the faces' normals differ): the straight line between the
        // feet cuts through the corner, by up to half the chord x tan(half the angle) at
        // mid-swing. Lift that much more so the foot clears it.
        float cos = Mathf.Clamp(Vector3.Dot(l.from.normal, l.to.normal), -0.9f, 1f);
        l.lift += 0.5f * Vector3.Distance(start, l.to.world) * Mathf.Sqrt((1f - cos) / (1f + cos));
    }

    // The target is past a convex edge. Find the edge (the last ground toward the target),
    // then cast back in from beyond it, as far below the surface as the step overshot: a foot
    // stepping over a cube's edge lands on the side, where walking the same distance takes it.
    bool WrapProbe(Vector3 pos, Vector3 target, Vector3 n, out Vector3 foot, out Vector3 normal, out Transform support)
    {
        foot = target; normal = n; support = OwnSurface;
        Vector3 outward = Vector3.ProjectOnPlane(target - pos, n);
        float reach = outward.magnitude;
        if (reach < 1e-4f) return false;
        outward /= reach;

        float lo = 0f, hi = 1f;
        Vector3 edge = default;
        bool any = false;
        for (int i = 0; i < 4; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (Probe(Vector3.Lerp(pos, target, mid), n, ProbeUp, ProbeDown, out Vector3 f, out _, out _))
            {
                lo = mid; edge = f; any = true;
            }
            else hi = mid;
        }
        if (!any) return false;

        float over = (1f - lo) * reach;
        Vector3 beside = edge - n * (FootLift + over);
        return Probe(beside, outward, (hi - lo) * reach + footDistance, footDistance, out foot, out normal, out support);
    }

    static float PlanarDist(Vector3 a, Vector3 b, Vector3 n) => Vector3.ProjectOnPlane(b - a, n).magnitude;

    // ---------------------------------------------------------------------
    // Air
    // ---------------------------------------------------------------------

    void EnterAir(Vector3 pos)
    {
        for (int i = 0; i < _legs.Length; i++)
        {
            ref Leg l = ref _legs[i];
            l.airStart = l.tip - pos; // translation-carried, so the peel-off travels with the owner
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

        // The ring stays on the body: its axis runs from the hub into the body, so legs always grow
        // out of the hub and away from the owner whichever way it faces or turns. (World up or
        // the flight direction as the axis let a turning body swing the legs into or beside it.)
        // Speed only sweeps them back.
        Vector3 bodyUp = BodyUp(pos);
        _airSpeed01 = Mathf.Lerp(_airSpeed01, Mathf.Clamp01(speed / (walkSpeed * 1.5f)), 1f - Mathf.Exp(-4f * dt));
        _axisGoal = bodyUp;
        _airAxis = Vector3.Slerp(_airAxis, _axisGoal, 1f - Mathf.Exp(-12f * dt)).normalized;

        // Carry the ring reference along with the axis: continuous, never flips.
        _orbitRef = Vector3.ProjectOnPlane(_orbitRef, _airAxis);
        _orbitRef = _orbitRef.sqrMagnitude > 1e-6f ? _orbitRef.normalized : Perp(_airAxis);
        Vector3 orbitFwd = Vector3.Cross(_orbitRef, _airAxis);

        // Spot the surface we're heading into (or hovering over).
        Vector3 probeDir = speed > HoldSpeed ? _flightDir : -bodyUp;
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
        float away = Mathf.Lerp(0.35f, 0.5f, _airSpeed01); // angle out and away from the body

        // Trail behind the flight direction, but never back toward the body.
        Vector3 trail = -_flightDir;
        if (Vector3.Dot(trail, bodyUp) > 0f) trail = Vector3.ProjectOnPlane(trail, bodyUp);
        trail *= 0.7f * airSweep * _airSpeed01;
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
            Vector3 target = pos + (flow * spread - _airAxis * away + trail).normalized * len;
            l.clampN = Vector3.zero;

            // Staggered reach: each leg goes out to its own spot and plants there.
            if (_hasLand)
            {
                float r = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_landing - l.delay) / 0.35f));
                if (r > 0f)
                {
                    // Centred under the body on the landing plane, not on the ray hit: the body
                    // touches down where its collider meets the surface, well before the hub ray
                    // point on an angled approach, which left every foot shifted the same way.
                    Vector3 landCentre = pos - landUp * Vector3.Dot(pos - landPoint, landUp);
                    Vector3 spot = landCentre + (toLand * radial) * reach + landUp * lift;
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
    // Legs for the GPU
    // ---------------------------------------------------------------------

    void Build()
    {
        _b = body ? body : transform;
        _rb = _b.GetComponentInParent<Rigidbody>();
        _source = _b.GetComponentInParent<ISurfaceContact>();
        _self = _rb ? _rb.transform : _b; // colliders under this are the owner's, never ground

        int count = legCount;
        _rings = lengthSegments + 1;
        _sides = radialSegments;
        _data = new LegRenderer.LegData[count];

        _groundNormal = _b.up;
        _ringFwd = Vector3.ProjectOnPlane(_b.forward, _b.up).normalized;
        _frameSupport = null;
        _spin = 0f;
        _mirror = 1f;

        _legs = new Leg[count];
        Basis(_b.up, out Vector3 right, out Vector3 fwd);
        Vector3 pos = _b.position;
        int seed = GetInstanceID() * 7919; // every instance gets its own rhythm

        for (int i = 0; i < count; i++)
        {
            ref Leg l = ref _legs[i];
            int h = seed + i * 101;

            l.angle = Mathf.PI * 2f * i / count;
            l.phase = Hash01(h + 13) * Mathf.PI * 2f;
            l.rReach = Hash01(h + 3) * 2f - 1f;
            l.rHeight = Hash01(h + 11) * 2f - 1f;
            l.delay = 0.05f + Hash01(h + 17) * 0.35f;

            Vector3 radial = right * Mathf.Cos(l.angle) + fwd * Mathf.Sin(l.angle);
            l.rootDir = l.rootGoal = radial;
            l.tip = pos + radial * Reach(l);
            l.bendUp = l.hipUp = _b.up;
        }

        _modeReady = false;
    }

    // Conservative: everything a leg can reach, for frustum culling.
    Bounds LegBounds(Vector3 pos)
    {
        float r2 = 0f;
        for (int i = 0; i < _legs.Length; i++)
            r2 = Mathf.Max(r2, (_legs[i].tip - pos).sqrMagnitude);

        float e = Mathf.Sqrt(r2) + legRootRadius * 2f + curveHeight + stepHeight +
                  baseRadius * 1.5f + footDistance * 0.2f + 0.25f;
        return new Bounds(pos, Vector3.one * (2f * e));
    }

    // Each leg as the GPU shapes it (BloodCellLegs.hlsl): the Bezier root -> tip with its
    // bend frame, radii, writhe and ground clearance. The rings are built in the shader.
    void WriteLegs(Vector3 pos, float dt)
    {
        // Knees snap to their pose after a gap (first frame, back on screen) instead of flinging.
        bool snapKnees = Time.time - _lastShapeTime > 0.1f;
        _lastShapeTime = Time.time;
        float kw = KneeFrequency * Mathf.PI * 2f;

        float wobbleAmp = footDistance * 0.012f * organic;
        float airWave = _air ? airFlow * footDistance * 0.1f * (0.3f + 0.7f * _airTurn) : 0f;
        float swell = 0.06f * organic;

        // The body's own ground plane, for clearing corners: the hub's height over it, from the
        // planted feet that share its surface (an edge's far face says nothing about it).
        if (!_air)
        {
            float sum = 0f; int count = 0;
            for (int i = 0; i < _legs.Length; i++)
                if (!_legs[i].stepping && _legs[i].clampN.sqrMagnitude > 0f && Vector3.Dot(_legs[i].clampN, _normal) > 0.98f)
                {
                    sum += Vector3.Dot(pos - _legs[i].ground, _normal);
                    count++;
                }
            if (count > 0) _hubHeight = _hubHeight < 0f ? sum / count : Mathf.Lerp(_hubHeight, sum / count, 1f - Mathf.Exp(-10f * dt));
        }
        Vector3 bodyGround = pos - _normal * Mathf.Max(_hubHeight, 0f);

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

            // The upper leg rises off the body's surface, the lower comes down onto the foot's. On
            // one face they agree; round an edge (a cube's side) the leg arches out over the corner
            // by exactly as much as the straight root-to-foot line would sink into it, else not at
            // all (a guessed lift arched every edge-crossing leg long, which read as stretching).
            Vector3 hip = Vector3.ProjectOnPlane(l.hipUp, dir);
            hip = hip.sqrMagnitude > 1e-5f ? hip.normalized : up;
            float corner = 0f;
            if (!_air && _hubHeight >= 0f && l.clampN.sqrMagnitude > 0f)
            {
                float sink = CornerSink(p0, p3, bodyGround, _normal, l.ground, l.clampN) + baseRadius;
                if (sink > 0f) corner = Mathf.Min(sink * 1.4f, len * 0.5f); // a Bezier bows ~3/4 of its handles
            }
            Vector3 p1 = p0 + dir * (len * CurveBias) + hip * (arch + corner) + wob;

            // Upper leg trails its pose on a damped spring (relative to the hub, so travel
            // doesn't drag it): the bend follows through instead of tracking rigidly.
            Vector3 kneeGoal = p1 - pos;
            if (snapKnees) { l.knee = kneeGoal; l.kneeVel = Vector3.zero; }
            else
            {
                // Substepped: a stiff spring integrated in one big frame step would explode.
                float span = Mathf.Min(dt, 1f / 15f);
                int n = Mathf.Max(1, Mathf.CeilToInt(span * 120f));
                float h = span / n;
                for (int k = 0; k < n; k++)
                {
                    l.kneeVel += (kneeGoal - l.knee) * (kw * kw * h) - l.kneeVel * (2f * KneeDamping * kw * h);
                    l.knee += l.kneeVel * h;
                }
                l.knee = kneeGoal + Vector3.ClampMagnitude(l.knee - kneeGoal, len * 0.35f);
            }
            p1 = pos + l.knee;

            // Upright foot: the lower control point sits straight above the foot along the surface
            // normal, so the leg always comes down vertically into it and the upper leg pivots
            // around the plant instead of the whole leg swinging as one line.
            Vector3 p2 = p3 - dir * (len * (1f - CurveBias) * 0.55f) + up * (arch * 0.55f + corner) - wob * 0.5f;
            float upright = uprightFeet * l.plant;
            if (upright > 0f && l.bendUp.sqrMagnitude > 1e-6f)
                p2 = Vector3.Lerp(p2, p3 + l.bendUp.normalized * (arch * 0.55f + len * 0.25f + corner), upright);

            float thick = Mathf.Clamp(1f - (len / footDistance - 1f) * StretchThickness, 0.55f, 1.45f);
            bool clamp = l.clampN.sqrMagnitude > 0f;
            float wave = Mathf.Max(clamp ? 0f : airWave, _wiggle * footDistance * 0.08f); // free legs and wiggling legs writhe

            _data[i] = new LegRenderer.LegData
            {
                p0 = new Vector4(p0.x, p0.y, p0.z, baseRadius * thick),
                p1 = new Vector4(p1.x, p1.y, p1.z, tipRadius * thick),
                p2 = new Vector4(p2.x, p2.y, p2.z, wave),
                p3 = new Vector4(p3.x, p3.y, p3.z, l.phase),
                side = new Vector4(side.x, side.y, side.z, swell),
                clampN = clamp ? new Vector4(l.clampN.x, l.clampN.y, l.clampN.z, 1f) : Vector4.zero,
                ground = l.ground,
                hub = pos,
            };
        }
    }

    // ---------------------------------------------------------------------
    // Utility
    // ---------------------------------------------------------------------

    // How deep the segment a -> b goes into the solid behind both planes (a convex edge between
    // two faces), at its deepest; negative when it stays clear. Depth under each plane is linear
    // along the segment, so the deepest point of the smaller one is an end or where they cross.
    static float CornerSink(Vector3 a, Vector3 b, Vector3 pointA, Vector3 nA, Vector3 pointB, Vector3 nB)
    {
        float a0 = Vector3.Dot(pointA - a, nA), a1 = Vector3.Dot(pointA - b, nA);
        float b0 = Vector3.Dot(pointB - a, nB), b1 = Vector3.Dot(pointB - b, nB);
        float best = Mathf.Max(Mathf.Min(a0, b0), Mathf.Min(a1, b1));
        float den = (a1 - a0) - (b1 - b0);
        if (Mathf.Abs(den) > 1e-6f)
        {
            float s = (b0 - a0) / den;
            if (s > 0f && s < 1f) best = Mathf.Max(best, a0 + (a1 - a0) * s);
        }
        return best;
    }

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
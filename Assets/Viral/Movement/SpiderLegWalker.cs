using UnityEngine;

/// <summary>
/// Procedural fluid-leg walker. Knows nothing about any movement system: it reads the body's motion (Rigidbody
/// velocity or transform deltas) and the ground (an ISurfaceContact if one exists, raycasts otherwise).
///
/// The legs themselves live on the GPU (LegRenderer + LegSimulation.compute): every leg's gait, feet, swing
/// arcs and springs are stepped there, feet planted on the surface's SurfaceMap (nearest point on the real mesh,
/// no raycasts), and drawn from the same buffers. This component only keeps what is per walker (a few vectors:
/// the ring's heading and phase, the smoothed velocity and turn, the flight pose, the landing spot) and hands
/// LegRenderer one record a frame. Only the values that define the look are exposed; everything else derives
/// from them so the motion stays consistent at any scale.
///
/// One leg layout is shared by every mode: each leg owns a fixed slot on a ring, the ring has a heading
/// (_ringFwd) and a phase (_spin). Walking, flying and landing all place legs through that same ring, so no
/// transition ever makes legs swap sides, cross over, or pop.
///
/// Independent of the body's own rotation: a rolling/spinning body does not drag the legs around.
///
/// Feet ride the surface the owner stands on (its ISurfaceContact). A walker without one walks on the plane
/// under it (a raycast finds that); a surface without a SurfaceMap (unreadable mesh) likewise.
/// </summary>
public class SpiderLegWalker : MonoBehaviour
{
    // What changed this frame, for the GPU (LegSimulation.compute F_*).
    [System.Flags]
    enum Flag
    {
        Init = 1, EnterGround = 2, FromAir = 4, EnterAir = 8, Air = 16, SnapKnees = 32, Carry = 64,
        SupportChanged = 128, HasLand = 256, HasSupport = 512, WiggleStart = 1024,
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
    [Tooltip("Only without an ISurfaceContact: surfaces that count as ground under the body. The owner's own colliders are always ignored.")]
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

    [Header("Debug")]
    [Tooltip("Log what this walker sends and what the GPU holds for its first leg, twice a second (Console / Editor.log).")]
    public bool debugLog;

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

    // Derived feel constants, shared by every walker (the per-leg ones are in LegSimulation.compute).
    const float WobbleSpeed = 3.5f, FrameFollow = 3f, VelocitySmoothing = 12f;
    const float SwingFraction = 0.45f; // share of the gait cycle a foot is in the air
    const float MaxStride = 1.2f;      // body travel per cycle (x footDistance) before cadence speeds up instead
    const float MaxSpan = 2f, SpanMargin = 0.4f; // hub-to-foot distance (x footDistance) that forces a catch-up step
    const float FullTurnRate = 140f, MinGait = 0.35f, MaxGait = 3f, HoldSpeed = 0.2f;
    const float GroundExitScale = 1.5f; // auto ground detection hysteresis: leaving needs more distance than arriving

    static readonly RaycastHit[] Hits = new RaycastHit[16];

    Transform _b, _self;
    Rigidbody _rb;
    ISurfaceContact _source;
    int _rings, _sides;
    static Shader s_legShader;

    // GPU slot (LegRenderer): walker state + a block of leg states.
    int _walker = -1, _legBase, _slotLegs, _slotGeneration;
    Flag _pending;
    Transform _gpuSupport;
    bool _hadSupport;

    // Shared ring: heading + phase + handedness.
    Vector3 _ringFwd = Vector3.forward, _ringFwdLocal;
    float _spin, _mirror = 1f;

    bool _seeded, _modeReady, _air, _supportSeeded, _prevMoveValid, _hasLand, _culled;
    Vector3 _lastPos, _vel, _relVel, _normal, _groundNormal = Vector3.up, _supportLocal, _prevMove;
    Transform _support, _frameSupport;
    float _turn, _gait = 1f, _wobblePhase, _lastShapeTime = -999f;

    // Rotation wiggle: 0 composed .. 1 fully loose.
    float _wiggle, _turnRate;
    Vector3 _grabAxis = Vector3.down, _grabRef = Vector3.right;
    Quaternion _prevRot = Quaternion.identity;
    bool _wiggleSeeded;

    // Support rotation last frame: the legs' world-space shape is turned with it (on the GPU).
    Transform _carrySupport;
    Quaternion _carryRot = Quaternion.identity;

    // Air: _airAxis runs from the hub into the body; the legs orbit it.
    Vector3 _flightDir = Vector3.forward, _airAxis = Vector3.up, _orbitRef = Vector3.right, _landNormal;
    Transform _landSupport;
    Vector3 _landLocal, _landWorld;
    float _landing, _airTurn, _airSpeed01, _airTime;

    LegRenderer.Walker _frame;

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

    void OnDisable()
    {
        SimulationTicker.Unregister(this);
        Release();
    }

    Shader LegShader => legShader ? legShader : s_legShader ? s_legShader : s_legShader = Shader.Find("Custom/BloodCellLegs");

    [ContextMenu("Rebuild Legs")]
    public void RebuildLegs() => Release();

    void Release()
    {
        if (_walker >= 0 && _slotGeneration == LegRenderer.Generation) LegRenderer.Free(_walker, _legBase, _slotLegs);
        _walker = -1;
    }

    /// <summary>A foot planted (from LegRenderer, a frame or two after the GPU did it).</summary>
    public void Planted(Vector3 at) => CreatureAudio.Step(at, footDistance, _source); // the player's own; crowds mostly feed a patter bed

    // Called by SimulationTicker every frame, after every Organism's late tick.
    public void Tick()
    {
        if (_walker < 0 || _slotGeneration != LegRenderer.Generation || _slotLegs != legCount ||
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

        Flag flags = _pending;
        _pending = 0;
        bool air = !FindSurface(pos, out _normal);
        if (!_modeReady || air != _air)
        {
            if (air)
            {
                EnterAir();
                flags |= Flag.EnterAir;
            }
            else
            {
                EnterGround(_modeReady);
                flags |= Flag.EnterGround | (_modeReady ? Flag.FromAir : 0);
            }
            _air = air;
            _modeReady = true;
        }
        if (jumped && !air)
        {
            EnterGround(false);
            flags = (flags & ~Flag.FromAir) | Flag.EnterGround;
        }

        Transform support = air ? null : OwnSurface;
        if (support != _gpuSupport || (support == null) == _hadSupport) flags |= Flag.SupportChanged;
        _gpuSupport = support;
        _hadSupport = support;
        if (Carry(support, out Quaternion carry)) flags |= Flag.Carry;

        if (air) UpdateAir(pos, dt, ref flags);
        else UpdateGround(pos, dt);
        if (UpdateWiggle(pos, air, dt)) flags |= Flag.WiggleStart;
        _wobblePhase += dt * WobbleSpeed * (air ? 1f : _gait);

        // On screen (padded for the frame-late view and nearby shadows): drawn this frame, with a lighter tube far
        // away. Off screen (within the margin) the legs keep walking but aren't drawn.
        Bounds bounds = LegBounds(pos);
        bool draw = legMaterial && LegRenderer.Visible(bounds, footDistance);
        float shapeDt = Mathf.Min(Time.time - _lastShapeTime, 0.1f);
        if (draw)
        {
            if (Time.time - _lastShapeTime > 0.1f) flags |= Flag.SnapKnees; // knees snap to their pose after a gap
            _lastShapeTime = Time.time;
        }
        if (air) flags |= Flag.Air;
        if (support) flags |= Flag.HasSupport;

        Fill(pos, dt, support, carry, shapeDt, flags);
        bool far = lodDistance > 0f && camDist > lodDistance;
        int rings = far ? Mathf.Min(_rings, Mathf.Max(4, lengthSegments / 2 + 1)) : _rings;
        int sides = far ? Mathf.Min(_sides, Mathf.Max(5, radialSegments * 2 / 3)) : _sides;
        LegRenderer.Submit(this, _frame, draw ? legMaterial : null, LegShader, rings, sides,
                           castShadows && camDist < shadowDistance, bounds);

        if (debugLog && Time.time >= _nextLog)
        {
            _nextLog = Time.time + 0.5f;
            Debug.Log($"[legs {name}] cpu: {(air ? "AIR" : "ground")} flags={flags} support={(support ? support.name : "-")} " +
                      $"map={_frame.map} dt={dt:F4} rate={_rate:F2} planar={_planar.magnitude:F2} walker={_walker} legBase={_legBase}");
            LegRenderer.Inspect(name, _walker, _legBase);
        }
    }

    float _nextLog;

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

    // Spinning in the air: the legs stretch out all around and grab (on the GPU). Here only how much: the turn
    // rate, filtered. True on the frame it starts (the legs take their grab pose from where they are).
    bool UpdateWiggle(Vector3 pos, bool air, float dt)
    {
        // Air only. On the ground the body's up is held on the surface normal, so its only rotation
        // is turning to a new heading (or following a curved cell), which must not set this off.
        Quaternion rot = _b.rotation;
        float rate = air && _wiggleSeeded ? Quaternion.Angle(_prevRot, rot) / dt : 0f;
        _prevRot = rot;
        _wiggleSeeded = air;

        // Frame-to-frame rotation is noisy; filter it so the legs don't flicker in and out.
        _turnRate = Mathf.Lerp(_turnRate, rate, 1f - Mathf.Exp(-10f * dt));
        float goal = rotationWiggle > 0f ? Mathf.Clamp01((_turnRate - rotationWiggle) / rotationWiggle) : 0f;
        bool starting = _wiggle <= 0f;
        _wiggle = Mathf.Lerp(_wiggle, goal, 1f - Mathf.Exp(-(goal > _wiggle ? 8f : 4f) * dt));
        if (_wiggle < 1e-3f) { _wiggle = 0f; return false; }

        if (starting)
        {
            // Grab directions hang in the world, not spinning with the body.
            _grabAxis = -BodyUp(pos);
            _grabRef = Perp(_grabAxis);
        }
        return starting;
    }

    // Standing on something that turns: its turn since last frame (the GPU turns the legs' smoothed world-space
    // shape with it, or they lag the turn and bend off to one side until the next step).
    bool Carry(Transform sup, out Quaternion d)
    {
        d = Quaternion.identity;
        bool turned = false;
        if (sup && sup == _carrySupport)
        {
            d = sup.rotation * Quaternion.Inverse(_carryRot);
            turned = Quaternion.Angle(d, Quaternion.identity) > 1e-3f;
        }
        _carrySupport = sup;
        _carryRot = sup ? sup.rotation : Quaternion.identity;
        return turned;
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

    // Closest ground hit that is not part of the owner (walkers without an ISurfaceContact; landing spotting).
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

    // What the body stands on (null in the air or without an ISurfaceContact).
    Transform OwnSurface => _source != null && _source.OnSurface ? _source.Surface : null;

    // ---------------------------------------------------------------------
    // Ground
    // ---------------------------------------------------------------------

    void EnterGround(bool fromAir)
    {
        if (fromAir) AlignRingToLanding(_normal);
        _groundNormal = _normal;
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

    // Per walker: the ring's heading, velocity relative to the support, turn and cadence. The legs follow on the GPU.
    Vector3 _right, _fwd, _planar;
    float _rate, _swingTime, _stanceTime, _maxSpan;

    void UpdateGround(Vector3 pos, float dt)
    {
        Vector3 n = _normal;
        _groundNormal = n;

        // Body velocity RELATIVE to what we stand on: a moving platform is not walking.
        Transform s = _source?.Surface;
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

        _planar = Vector3.ProjectOnPlane(_relVel, n);
        float speed = _planar.magnitude;
        Vector3 moveDir = speed > 1e-3f ? _planar / speed : Vector3.zero;

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
        _right = right;
        _fwd = fwd;

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

        // One shared rhythm: a fixed cadence at low speed; once strides reach MaxStride the cadence rises
        // instead. Past the farthest a foot is ever planned (rest reach + the lead at this speed) plus a
        // margin, a foot steps out of turn.
        _rate = Mathf.Max(SwingFraction / stepDuration, speed / (footDistance * MaxStride)); // cycles/s
        _swingTime = SwingFraction / _rate;
        _stanceTime = (1f - SwingFraction) / _rate;
        _maxSpan = Mathf.Min(footDistance * (1f + SpanMargin) + speed * (_swingTime + _stanceTime * 0.5f), footDistance * MaxSpan);
    }

    // ---------------------------------------------------------------------
    // Air
    // ---------------------------------------------------------------------

    void EnterAir()
    {
        // Air ring starts exactly on the ground ring: same axis, same slots.
        _airAxis = _groundNormal;
        Basis(_groundNormal, out _orbitRef, out _);

        float speed = _vel.magnitude;
        _flightDir = speed > HoldSpeed ? _vel / speed : _ringFwd;
        _airSpeed01 = _airTime = _landing = _airTurn = 0f;
        _hasLand = false;
    }

    // Per walker: the ring's axis and spin, the trail behind the flight, and the landing spot ahead.
    Vector3 _trail, _landCentre, _landUp;
    Quaternion _toLand = Quaternion.identity;
    float _spread, _away, _maxLen;

    void UpdateAir(Vector3 pos, float dt, ref Flag flags)
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
        // out of the hub and away from the owner whichever way it faces or turns. Speed only sweeps them back.
        Vector3 bodyUp = BodyUp(pos);
        _airSpeed01 = Mathf.Lerp(_airSpeed01, Mathf.Clamp01(speed / (walkSpeed * 1.5f)), 1f - Mathf.Exp(-4f * dt));
        _airAxis = Vector3.Slerp(_airAxis, bodyUp, 1f - Mathf.Exp(-12f * dt)).normalized;

        // Carry the ring reference along with the axis: continuous, never flips.
        _orbitRef = Vector3.ProjectOnPlane(_orbitRef, _airAxis);
        _orbitRef = _orbitRef.sqrMagnitude > 1e-6f ? _orbitRef.normalized : Perp(_airAxis);

        // Spot the surface we're heading into (or hovering over).
        Vector3 probeDir = speed > HoldSpeed ? _flightDir : -bodyUp;
        float cast = landingDistance + speed * 0.3f;
        float want = 0f;
        if (Cast(pos, probeDir, cast, out RaycastHit hit))
        {
            want = 1f - hit.distance / cast;
            _landSupport = hit.collider.transform;
            _landLocal = _landSupport.InverseTransformPoint(hit.point);
            _landWorld = hit.point;
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

        // Landing map: rotate the air ring onto the surface (circle stays a circle), centred under the body on
        // the landing plane, not on the ray hit (the body touches down where its collider meets the surface,
        // well before the hub ray point on an angled approach, which left every foot shifted the same way).
        if (_hasLand)
        {
            Vector3 landPoint = _landSupport ? _landSupport.TransformPoint(_landLocal) : _landWorld;
            _landUp = _landNormal;
            _toLand = Quaternion.FromToRotation(_airAxis, Vector3.Dot(_airAxis, _landUp) < 0f ? -_landUp : _landUp);
            _landCentre = pos - _landUp * Vector3.Dot(pos - landPoint, _landUp);
            flags |= Flag.HasLand;
        }

        _spread = Mathf.Lerp(1f, 0.65f, _airSpeed01);
        _away = Mathf.Lerp(0.35f, 0.5f, _airSpeed01); // angle out and away from the body

        // Trail behind the flight direction, but never back toward the body.
        _trail = -_flightDir;
        if (Vector3.Dot(_trail, bodyUp) > 0f) _trail = Vector3.ProjectOnPlane(_trail, bodyUp);
        _trail *= 0.7f * airSweep * _airSpeed01;
        _maxLen = legRootRadius + footDistance * 1.5f;
    }

    // ---------------------------------------------------------------------
    // The GPU's record
    // ---------------------------------------------------------------------

    void Build()
    {
        Release();
        _b = body ? body : transform;
        _rb = _b.GetComponentInParent<Rigidbody>();
        _source = _b.GetComponentInParent<ISurfaceContact>();
        _self = _rb ? _rb.transform : _b; // colliders under this are the owner's, never ground

        _rings = lengthSegments + 1;
        _sides = radialSegments;
        LegRenderer.Allocate(legCount, out _walker, out _legBase);
        _slotLegs = legCount;
        _slotGeneration = LegRenderer.Generation;
        _pending |= Flag.Init;
        _gpuSupport = null;
        _hadSupport = false;

        _groundNormal = _normal = _b.up;
        _ringFwd = Vector3.ProjectOnPlane(_b.forward, _b.up).normalized;
        Basis(_b.up, out _right, out _fwd);
        _frameSupport = null;
        _spin = 0f;
        _mirror = 1f;
        _modeReady = false;
    }

    void Fill(Vector3 pos, float dt, Transform support, Quaternion carry, float shapeDt, Flag flags)
    {
        Vector3 bodyUp = BodyUp(pos);
        Matrix4x4 m = support ? support.localToWorldMatrix : Matrix4x4.identity;
        Matrix4x4 inv = support ? support.worldToLocalMatrix : Matrix4x4.identity;
        int map = -1;
        if (support)
        {
            SurfaceMap sm = Surface.Of(support)?.Map;
            if (sm != null && sm.Ready) map = sm.GpuIndex;
        }

        _frame.pos = V(pos, dt);
        _frame.normal = V(_normal, _turn);
        _frame.right = V(_right, _rate);
        _frame.fwd = V(_fwd, _swingTime);
        _frame.planar = V(_planar, _stanceTime);
        _frame.bodyUp = V(bodyUp, _maxSpan);
        _frame.m0 = m.GetRow(0); _frame.m1 = m.GetRow(1); _frame.m2 = m.GetRow(2);
        _frame.i0 = inv.GetRow(0); _frame.i1 = inv.GetRow(1); _frame.i2 = inv.GetRow(2);
        _frame.carry = new Vector4(carry.x, carry.y, carry.z, carry.w);
        _frame.shape0 = new Vector4(footDistance, legRootRadius, stepDistance, stepHeight);
        _frame.shape1 = new Vector4(curveHeight, uprightFeet, baseRadius, tipRadius);
        _frame.gait = new Vector4(organic, _spin, _mirror, _wobblePhase);
        _frame.airAxis = V(_airAxis, _spread);
        _frame.orbitRef = V(_orbitRef, _away);
        _frame.trail = V(_trail, _landing);
        _frame.land = V(_landCentre, _maxLen);
        _frame.landUp = V(_landUp, _airTime);
        _frame.toLand = new Vector4(_toLand.x, _toLand.y, _toLand.z, _toLand.w);
        _frame.grabAxis = V(_grabAxis, _wiggle);
        _frame.grabRef = V(_grabRef, airFlow);
        float airWave = _air ? airFlow * footDistance * 0.1f * (0.3f + 0.7f * _airTurn) : 0f;
        _frame.look = new Vector4(footDistance * 0.012f * organic, airWave, 0.06f * organic, shapeDt);
        _frame.legBase = _legBase;
        _frame.legCount = legCount;
        _frame.outBase = -1;
        _frame.flags = (int)flags;
        _frame.map = map;
        _frame.seed = GetInstanceID() * 7919; // every instance gets its own rhythm
        _frame.walkerState = _walker;
    }

    static Vector4 V(Vector3 v, float w) => new Vector4(v.x, v.y, v.z, w);

    // Conservative: everything a leg can reach (feet up to MaxSpan out on the ground, 1.5 reaches when wiggling in
    // the air), for frustum culling.
    Bounds LegBounds(Vector3 pos)
    {
        float e = footDistance * MaxSpan + legRootRadius * 2f + curveHeight + stepHeight +
                  baseRadius * 1.5f + footDistance * 0.2f + 0.25f;
        return new Bounds(pos, Vector3.one * (2f * e));
    }

    static Vector3 Perp(Vector3 n) =>
        Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
}

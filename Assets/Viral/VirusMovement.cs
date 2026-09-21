using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;
using UnityEngine.InputSystem;
using Pathfinding;

/// <summary>
/// Virus locomotion: free flight (Rigidbody), crawling an A*PP navmesh graph
/// (kinematic, graph-space authoritative), and Focus Mode (attached, no input).
///
/// A NavmeshGraph has no slope limit, so a sphere mesh is navigable all over --
/// unlike Unity's NavMesh, which only bakes a cap. Free A*PP 4.2 has no
/// Linecast, so movement is clamped with GetNearest; across a narrow hole it
/// can snap to the far side instead of stopping at the edge.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VirusMovement : MonoBehaviour
{
    public enum State { Flying, Grounded, FocusMode }

    public enum Smoothing
    {
        /// <summary>Sit on the raw triangles. Faceted on a sphere.</summary>
        Flat,
        /// <summary>Push out to a radius interpolated across the triangle. Exact for a sphere.</summary>
        Radial
    }

    [Header("References")]
    [Tooltip("Optional visual child. Left empty, the whole object turns.")]
    public Transform body;

    [Tooltip("Movement is relative to this. Falls back to Camera.main.")]
    public Transform cameraTransform;

    [Header("Surfaces")]
    [Tooltip("Layers that count as landable.")]
    public LayerMask surfaceLayers = ~0;

    [Tooltip("Max distance from a contact point to the graph for it to count as a landing.")]
    public float snapDistance = 2f;

    [Tooltip("Distance held above the graph surface while crawling.")]
    public float hoverHeight = 0.5f;

    [Tooltip("Seconds after landing before the virus can walk.")]
    public float landingDelay = 0.25f;

    [Tooltip("Radial removes faceting. Assumes the cell is roughly star-shaped about its pivot.")]
    public Smoothing surfaceSmoothing = Smoothing.Radial;

    [Header("Camera")]
    [Tooltip("Rigs told to change mode on state changes.")]
    public UniversalCamera[] cameraRigs;
    public string flyingCameraMode = "Flying";
    public string groundedCameraMode = "Grounded";

    [Header("Focus Mode")]
    public string focusCameraMode = "Focus";

    [Tooltip("When enabled, EnterFocusMode does nothing unless Grounded.")]
    public bool requireGroundedForFocusMode = true;

    public UnityEvent onFocusModeEnter;
    public UnityEvent onFocusModeExit;

    [Header("Focus Hold / Slam")]
    [Tooltip("VISUAL-ONLY child moved by the charge/slam. Never the root. Falls back to Body.")]
    public Transform focusVisualBody;

    [Tooltip("Seconds between hold completion and Focus Mode actually starting. Movement stops immediately.")]
    [Min(0f)] public float focusEntryDelay = 0.18f;
    [Min(0f)] public float focusHoldLiftAmount = 0.30f;
    [Min(0.1f)] public float focusHoldLiftPower = 1.35f;
    [Min(0f)] public float focusSlamDownAmount = 0.16f;
    [Min(0.01f)] public float focusSlamDuration = 0.10f;
    [Min(0.01f)] public float focusSlamRecoveryDuration = 0.18f;
    [Min(0.01f)] public float focusHoldCancelReturnSpeed = 10f;

    [Header("World Button")]
    [Tooltip("Shown only while Grounded.")]
    public WorldButton groundedWorldButton;

    [Header("Speeds")]
    public float flySpeed = 12f;
    public float walkSpeed = 4f;
    public float jumpForce = 8f;

    [Header("Flight Charge")]
    [Tooltip("Peak charge speed as a multiple of Fly Speed.")]
    [FormerlySerializedAs("flightBoostSpeedMultiplier")]
    [Min(1f)] public float chargeSpeedMultiplier = 2.5f;

    [Tooltip("Seconds the charge lasts. Direction is locked for this long.")]
    [Min(0.01f)] public float chargeDuration = 0.35f;

    [Tooltip("Seconds after a charge ends before another can start.")]
    [Min(0f)] public float chargeCooldown = 0.6f;

    [Tooltip("Speed across the charge. X = time 0-1. Y = 1 at charge speed, 0 at Fly Speed. " +
             "Ending at 0 hands back to normal flight without a jolt.")]
    public AnimationCurve chargeCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);

    [Header("Ground Walk Accel / Decel")]
    [Tooltip("Off = instant Walk Speed. On = ramp using the curves below (X time 0-1, Y progress 0-1).")]
    public bool useWalkSpeedCurves = false;
    [Min(0.01f)] public float walkAccelerationTime = 0.28f;
    public AnimationCurve walkAccelerationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Min(0.01f)] public float walkDecelerationTime = 0.20f;
    public AnimationCurve walkDecelerationCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Feel")]
    [Tooltip("Turn rate. ~6 floaty, ~20 sharp.")]
    public float rotationSharpness = 8f;

    [Tooltip("Straight-line flight acceleration (u/s²).")]
    public float flyAcceleration = 40f;

    [Tooltip("Deceleration with no thrust (u/s²). Lower coasts further.")]
    public float coastDeceleration = 6f;

    [Range(0f, 1f)]
    [Tooltip("Extra authority when thrust fights existing momentum (turning). 0 none, 0.5 ~double, 1 override.")]
    public float playerControl = 0.5f;

    [Tooltip("Local axis that leads while flying.")]
    public Vector3 flyingLeadAxis = Vector3.down;

    // -----------------------------------------------------------------------
    // Public state
    // -----------------------------------------------------------------------

    public State state { get; private set; } = State.Flying;

    /// <summary>Actual speed: Rigidbody velocity in flight, measured graph travel on the ground.</summary>
    public float speed => state == State.Flying ? (_rb ? _rb.linearVelocity.magnitude : 0f) : _groundedSpeed;

    /// <summary>Top speed for the current state.</summary>
    public float totalSpeed =>
        state == State.Flying ? CurrentFlySpeed :
        state == State.Grounded ? Mathf.Max(walkSpeed, 0f) : 0f;

    public float normalizedSpeed => totalSpeed > 1e-5f ? Mathf.Clamp01(speed / totalSpeed) : 0f;

    public bool IsFocusMode => state == State.FocusMode;

    /// <summary>Input locked: Focus Mode or its entry beat.</summary>
    public bool IsLocked => state == State.FocusMode || _focusEntryPending;

    /// <summary>Physically attached to a surface (Grounded, Focus, or entering Focus).</summary>
    public bool IsSurfaceAttached => state != State.Flying || _focusEntryPending;

    public bool ExternalFlightRotationControl => _externalFlightRotationControl;

    /// <summary>True during a flight charge.</summary>
    public bool IsCharging => state == State.Flying && Time.time - _chargeStart < chargeDuration;

    public Vector3 surfaceNormal { get; private set; } = Vector3.up;

    /// <summary>Cell whose local space the current graph is baked in. Null in flight or on a world-space graph.</summary>
    public Transform cellSpace { get; private set; }

    /// <summary>Cell currently stood on. Null while flying.</summary>
    public NavmeshGraphBinder cell { get; private set; }

    // -----------------------------------------------------------------------
    // Private state
    // -----------------------------------------------------------------------

    readonly NNConstraint _constraint = NNConstraint.Default;

    Rigidbody _rb;
    Transform _camera;
    Vector2 _move;
    Quaternion _targetRotation = Quaternion.identity;

    // Crawl state lives in graph space so the virus rides a moving cell. The
    // position is the RAW navmesh point, never the smoothed one -- feeding the
    // smoothed point back into GetNearest causes sway.
    Vector3 _graphPosition;
    Vector3 _graphForward = Vector3.forward; // also the coast direction when decelerating
    GraphNode _groundNode;
    GraphMask _graphMask = GraphMask.everything;

    float _landingLockedUntil;
    float _moveLockedUntil;
    float _groundedSpeed;

    // Charge
    float _chargeStart = float.NegativeInfinity;
    float _chargeReadyAt;
    Vector3 _chargeDir;

    bool _externalFlightRotationControl;
    RigidbodyConstraints _constraintsBeforeExternal;

    // Walk ramp
    float _walkSpeedNow, _walkCurveT, _walkCurveFrom;
    bool _walkHadInput;

    // Focus
    bool _focusEntryPending;
    float _focusEnterAt;

    // Focus visual
    Transform _visual;
    Vector3 _visualHome;
    float _bodyOffset;
    float _slamTime = -1f; // < 0 means no slam playing
    float _slamFrom;

    float ChargeSpeed => Mathf.Max(flySpeed * chargeSpeedMultiplier, 0f);
    float CurrentFlySpeed => IsCharging ? ChargeSpeed : Mathf.Max(flySpeed, 0f);
    Transform RotTarget => body ? body : transform;

    // -----------------------------------------------------------------------
    // Unity
    // -----------------------------------------------------------------------

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.constraints = RigidbodyConstraints.FreezeRotation;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _camera = cameraTransform;
        _targetRotation = transform.rotation;
        RefreshButton();
    }

    void OnDisable()
    {
        if (_visual) _visual.localPosition = _visualHome;
        _bodyOffset = 0f;
        _slamTime = -1f;
    }

    void Update()
    {
        if (!_camera && Camera.main) _camera = Camera.main.transform;

        // Per-frame but only toggles on mismatch, so an active hold is never reset.
        RefreshButton();

        if (_focusEntryPending)
        {
            if (Time.time >= _focusEnterAt) CompleteFocusModeEntry();
        }
        else if (state == State.FocusMode)
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                ExitFocusMode();
        }
        else
        {
            _move = ReadMove();

            // Crawling is kinematic, so it runs per rendered frame rather than
            // at the physics rate -- that was the grounded jitter.
            // Space / South: jump on the ground, charge in the air.
            if (state == State.Grounded)
            {
                if (ActionPressed()) Launch(transform.up * jumpForce);
                else GroundedStep(Time.deltaTime);
            }
            else if (ActionPressed())
            {
                TryCharge();
            }
        }

        if (IsSurfaceAttached) ApplySurfacePose();
        Rotate(RotationWeight(Time.deltaTime));
    }

    void LateUpdate()
    {
        // Re-pin after a moving cell's final pose so we never trail it a frame.
        if (IsSurfaceAttached)
        {
            ApplySurfacePose();
            if (IsLocked) Rotate(1f); // no steering to ease, match exactly
        }

        UpdateFocusVisual(Time.deltaTime);
    }

    void FixedUpdate()
    {
        if (state == State.Flying) FlyStep(Time.fixedDeltaTime);
    }

    // -----------------------------------------------------------------------
    // Flying
    // -----------------------------------------------------------------------

    void FlyStep(float dt)
    {
        Vector3 up = _camera ? _camera.up : Vector3.up;

        if (IsCharging)
        {
            // Locked direction, speed shaped by the curve from charge speed
            // down to Fly Speed, so normal control picks up seamlessly after.
            float t = (Time.time - _chargeStart) / chargeDuration;
            float blend = chargeCurve != null ? chargeCurve.Evaluate(t) : 1f - t;
            _rb.linearVelocity = _chargeDir * Mathf.LerpUnclamped(flySpeed, ChargeSpeed, blend);
        }
        else
        {
            Vector3 forward = _camera ? _camera.forward : Vector3.forward;
            Vector3 right = _camera ? _camera.right : Vector3.right;

            // W/S forward/back, A/D left/right, all relative to the camera.
            // _move is already clamped to length 1, and forward/right are
            // orthonormal, so diagonals are never faster.
            Vector3 wish = forward * _move.y + right * _move.x;
            Vector3 velocity = _rb.linearVelocity;
            Vector3 target = wish * flySpeed;
            float accel;

            if (wish.sqrMagnitude > 1e-6f)
            {
                // Extra authority only against existing momentum (turning), so
                // straight-line build-up still reads as acceleration.
                Vector3 delta = target - velocity;
                float opposition = delta.sqrMagnitude > 1e-6f && velocity.sqrMagnitude > 1e-6f
                    ? Mathf.Clamp01(-Vector3.Dot(delta.normalized, velocity.normalized))
                    : 0f;

                float control = playerControl / Mathf.Max(1f - playerControl, 1e-4f);
                accel = flyAcceleration * (1f + opposition * control);

                // Aim at thrust, not velocity: velocity wanders while decaying.
                // Thrust lies in the camera's forward/right plane, so it is
                // never parallel to the roll reference.
                if (!_externalFlightRotationControl)
                    _targetRotation = LeadAxisRotation(wish.normalized, up);
            }
            else
            {
                accel = coastDeceleration;
            }

            _rb.linearVelocity = Vector3.MoveTowards(velocity, target, accel * dt);
        }

        // No body child: rotate through physics so interpolation doesn't fight it.
        if (!body && !_externalFlightRotationControl)
            _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, _targetRotation, RotationWeight(dt)));
    }

    /// <summary>Burst forward along the camera, if off cooldown. Public so other systems can trigger it.</summary>
    public bool TryCharge()
    {
        if (state != State.Flying || Time.time < _chargeReadyAt) return false;

        _chargeDir = _camera ? _camera.forward : transform.forward;
        _chargeStart = Time.time;
        _chargeReadyAt = _chargeStart + chargeDuration + chargeCooldown;

        if (!_externalFlightRotationControl)
            _targetRotation = LeadAxisRotation(_chargeDir, _camera ? _camera.up : Vector3.up);

        return true;
    }

    // -----------------------------------------------------------------------
    // Grounded
    // -----------------------------------------------------------------------

    void GroundedStep(float dt)
    {
        if (AstarPath.active == null) { Launch(transform.forward * walkSpeed); return; }

        Vector3 up = surfaceNormal;

        // Camera-relative on the local tangent plane. Straight down the normal
        // leaves no forward, so fall back to the camera's up.
        Vector3 f = Vector3.ProjectOnPlane(_camera ? _camera.forward : Vector3.forward, up);
        if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(_camera ? _camera.up : Vector3.up, up);
        f.Normalize();
        Vector3 r = Vector3.Cross(up, f);

        Vector2 input = Time.time < _moveLockedUntil ? Vector2.zero : _move;
        Vector3 wish = f * input.y + r * input.x;
        bool moving = wish.sqrMagnitude > 1e-6f;

        if (moving) _graphForward = WorldDirToGraph(wish.normalized);

        float walk = UpdateWalkSpeed(moving ? Mathf.Clamp01(input.magnitude) : 0f, dt);
        float step = walk * dt;

        if (step <= 1e-6f) { _groundedSpeed = 0f; return; }

        // Coasting re-projects the facing onto the curved surface each frame.
        Vector3 dir = Vector3.ProjectOnPlane(GraphDirToWorld(_graphForward), up).normalized;
        Vector3 desired = GraphToWorld(_graphPosition) + dir * step;

        _constraint.graphMask = _graphMask;
        NNInfo nearest = AstarPath.active.GetNearest(WorldToGraph(desired), _constraint);
        if (nearest.node == null) { Launch(transform.forward * walkSpeed); return; }

        // Measured through the cell's current transform, so cell motion
        // doesn't count as player speed.
        _groundedSpeed = Vector3.Distance(GraphToWorld(_graphPosition), GraphToWorld(nearest.position)) / dt;
        _graphPosition = nearest.position;
        _groundNode = nearest.node;
    }

    float UpdateWalkSpeed(float input, float dt)
    {
        float target = Mathf.Max(0f, walkSpeed) * input;
        bool hasInput = input > 1e-3f;

        if (!useWalkSpeedCurves)
        {
            _walkHadInput = hasInput;
            return _walkSpeedNow = target;
        }

        // Restart the ramp only on press/release, not on analog noise.
        if (hasInput != _walkHadInput)
        {
            _walkHadInput = hasInput;
            _walkCurveT = 0f;
            _walkCurveFrom = _walkSpeedNow;
        }

        float duration = hasInput ? walkAccelerationTime : walkDecelerationTime;
        AnimationCurve curve = hasInput ? walkAccelerationCurve : walkDecelerationCurve;

        _walkCurveT = Mathf.Min(1f, _walkCurveT + dt / duration);
        float progress = _walkCurveT >= 1f ? 1f
            : Mathf.Clamp01(curve != null ? curve.Evaluate(_walkCurveT) : _walkCurveT);

        return _walkSpeedNow = Mathf.Max(0f, Mathf.LerpUnclamped(_walkCurveFrom, target, progress));
    }

    void ResetWalk()
    {
        _walkSpeedNow = _walkCurveT = _walkCurveFrom = 0f;
        _walkHadInput = false;
    }

    /// <summary>
    /// Rebuild world pose from graph-local state. Converting through cellSpace's
    /// CURRENT transform makes the virus ride the cell like a child.
    /// </summary>
    void ApplySurfacePose()
    {
        if (_groundNode == null) return;

        Vector3 p = SmoothSurface(_graphPosition, out Vector3 n);
        surfaceNormal = n;

        Vector3 heading = SurfaceTangent(GraphDirToWorld(_graphForward));
        _graphForward = WorldDirToGraph(heading);
        _targetRotation = Quaternion.LookRotation(heading, n);

        transform.position = GraphToWorld(p) + n * hoverHeight;
    }

    /// <summary>Unit tangent to the surface, falling back until one can't fail.</summary>
    Vector3 SurfaceTangent(Vector3 preferred)
    {
        Vector3 h = Vector3.ProjectOnPlane(preferred, surfaceNormal);
        if (h.sqrMagnitude < 1e-4f) h = Vector3.ProjectOnPlane(RotTarget.forward, surfaceNormal);
        if (h.sqrMagnitude < 1e-4f) h = AnyPerpendicular(surfaceNormal);
        return h.normalized;
    }

    // -----------------------------------------------------------------------
    // Rotation
    // -----------------------------------------------------------------------

    /// <summary>Ease toward the target. t = 1 snaps.</summary>
    void Rotate(float t)
    {
        if (_externalFlightRotationControl) return;

        if (body)
            body.rotation = Quaternion.Slerp(body.rotation, _targetRotation, t);
        else if (_rb.isKinematic)
            // Only when kinematic; in flight MoveRotation + interpolation own the root.
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, t);
    }

    float RotationWeight(float dt) =>
        rotationSharpness <= 0f ? 1f : 1f - Mathf.Exp(-rotationSharpness * dt);

    /// <summary>Rotation pointing flyingLeadAxis along heading; rollReference sets the spin.</summary>
    Quaternion LeadAxisRotation(Vector3 heading, Vector3 rollReference)
    {
        Vector3 lead = flyingLeadAxis.sqrMagnitude > 1e-6f ? flyingLeadAxis.normalized : Vector3.forward;
        Quaternion toForward = Quaternion.Inverse(Quaternion.LookRotation(lead, AnyPerpendicular(lead)));
        return SafeLookRotation(heading, rollReference) * toForward;
    }

    static Quaternion SafeLookRotation(Vector3 forward, Vector3 up)
    {
        if (Mathf.Abs(Vector3.Dot(forward, up.normalized)) > 0.999f) up = AnyPerpendicular(forward);
        return Quaternion.LookRotation(forward, up);
    }

    static Vector3 AnyPerpendicular(Vector3 v) =>
        Vector3.Cross(v, Mathf.Abs(v.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

    // -----------------------------------------------------------------------
    // Graph space (identity when cellSpace is null)
    // -----------------------------------------------------------------------

    static Vector3 ToWorld(Transform s, Vector3 p) => s ? s.TransformPoint(p) : p;
    static Vector3 ToGraph(Transform s, Vector3 p) => s ? s.InverseTransformPoint(p) : p;

    Vector3 GraphToWorld(Vector3 p) => ToWorld(cellSpace, p);
    Vector3 WorldToGraph(Vector3 p) => ToGraph(cellSpace, p);
    Vector3 GraphDirToWorld(Vector3 d) => cellSpace ? cellSpace.TransformDirection(d) : d;
    Vector3 WorldDirToGraph(Vector3 d) => cellSpace ? cellSpace.InverseTransformDirection(d) : d;

    /// <summary>Local graphs are baked at the origin; world graphs sit on the cell.</summary>
    Vector3 SurfaceCentre() => cellSpace ? Vector3.zero : cell != null ? cell.Cell.position : Vector3.zero;

    /// <summary>
    /// Lift a graph point onto the rounded surface its triangle approximates
    /// and report the world normal. Radius is interpolated across the triangle,
    /// so lumpy cells work and spheres are exact. Containment is still
    /// GetNearest's job -- this only adjusts height within the triangle.
    /// </summary>
    Vector3 SmoothSurface(Vector3 graphPoint, out Vector3 worldNormal)
    {
        worldNormal = NodeNormal(_groundNode, surfaceNormal);

        if (surfaceSmoothing == Smoothing.Flat || !(_groundNode is TriangleMeshNode tri))
            return graphPoint;

        Vector3 a = (Vector3)tri.GetVertex(0);
        Vector3 b = (Vector3)tri.GetVertex(1);
        Vector3 c = (Vector3)tri.GetVertex(2);

        Vector3 centre = SurfaceCentre();
        Vector3 outward = graphPoint - centre;
        if (outward.sqrMagnitude < 1e-8f) return graphPoint;

        Vector3 w = Barycentric(graphPoint, a, b, c);
        float radius = w.x * (a - centre).magnitude
                     + w.y * (b - centre).magnitude
                     + w.z * (c - centre).magnitude;

        Vector3 smoothed = centre + outward.normalized * radius;

        // Radial normal varies continuously instead of snapping per triangle.
        Vector3 radial = GraphToWorld(smoothed) - GraphToWorld(centre);
        if (radial.sqrMagnitude > 1e-8f) worldNormal = radial.normalized;

        return smoothed;
    }

    static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);

        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-12f) return new Vector3(1f, 0f, 0f);

        float v = (d11 * d20 - d01 * d21) / denom;
        float u = (d00 * d21 - d01 * d20) / denom;
        return new Vector3(1f - v - u, v, u);
    }

    /// <summary>
    /// World-space triangle normal, from world-space vertices so non-uniform
    /// scale stays correct. Flipped to agree with the reference, so mesh
    /// winding doesn't matter.
    /// </summary>
    Vector3 NodeNormal(GraphNode node, Vector3 reference)
    {
        if (!(node is TriangleMeshNode tri)) return reference;

        Vector3 a = GraphToWorld((Vector3)tri.GetVertex(0));
        Vector3 n = Vector3.Cross(GraphToWorld((Vector3)tri.GetVertex(1)) - a,
                                  GraphToWorld((Vector3)tri.GetVertex(2)) - a);
        if (n.sqrMagnitude < 1e-8f) return reference;

        n.Normalize();
        return Vector3.Dot(n, reference) < 0f ? -n : n;
    }

    // -----------------------------------------------------------------------
    // State changes
    // -----------------------------------------------------------------------

    void EnterState(State s)
    {
        state = s;
        _groundedSpeed = 0f;
        _chargeStart = float.NegativeInfinity;
        ResetWalk();
        RefreshButton();
        SetCameraMode(s == State.Flying ? flyingCameraMode
                    : s == State.Grounded ? groundedCameraMode
                    : focusCameraMode);
    }

    void HaltMotion()
    {
        _move = Vector2.zero;
        _groundedSpeed = 0f;
        ResetWalk();

        if (!_rb.isKinematic)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }

    void Launch(Vector3 velocity)
    {
        _focusEntryPending = false;
        _slamTime = -1f;

        // Local graphs all overlap at the origin; a stale binding would query the wrong cell.
        cell = null;
        cellSpace = null;
        _groundNode = null;
        _graphMask = GraphMask.everything;

        _externalFlightRotationControl = false;
        _rb.isKinematic = false;
        _rb.useGravity = false;
        _rb.constraints = RigidbodyConstraints.FreezeRotation;
        _rb.linearVelocity = velocity;
        _rb.angularVelocity = Vector3.zero;

        EnterState(State.Flying);

        // Otherwise the jump re-collides next step and sticks straight back down.
        _landingLockedUntil = Time.time + 0.15f;
    }

    /// <summary>Attach to the graph near a contact point. False leaves the virus flying.</summary>
    public bool TryLand(Vector3 point, Vector3 up, NavmeshGraphBinder landedOn)
    {
        if (AstarPath.active == null) return false;

        // Resolve into locals first so a failed attempt binds nothing.
        Transform space = landedOn != null && landedOn.space == NavmeshGraphBinder.Space.Local
            ? landedOn.Cell : null;

        // Local graphs overlap at the origin; mask to this cell's graph only.
        GraphMask mask = landedOn != null ? (GraphMask)(1 << landedOn.graphIndex) : GraphMask.everything;
        _constraint.graphMask = mask;

        NNInfo nearest = AstarPath.active.GetNearest(ToGraph(space, point), _constraint);

        // GetNearest always returns something; distance decides the landing.
        if (nearest.node == null || Vector3.Distance(point, ToWorld(space, nearest.position)) > snapDistance)
            return false;

        SetExternalFlightRotationControl(false);

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.isKinematic = true;

        cell = landedOn;
        cellSpace = space;
        _graphMask = mask;
        _graphPosition = nearest.position;
        _groundNode = nearest.node;
        _moveLockedUntil = Time.time + landingDelay;

        // Contact normal seeds the side NodeNormal agrees with from here on.
        surfaceNormal = up;
        _graphForward = WorldDirToGraph(SurfaceTangent(RotTarget.forward));

        ApplySurfacePose();
        Rotate(1f); // landing is a snap
        EnterState(State.Grounded);
        return true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (state != State.Flying || Time.time < _landingLockedUntil) return;
        if ((surfaceLayers.value & (1 << collision.gameObject.layer)) == 0) return;
        if (collision.contactCount == 0) return;

        ContactPoint contact = collision.GetContact(0);
        float impactSpeed = collision.relativeVelocity.magnitude; // before TryLand zeroes it

        var landedOn = collision.gameObject.GetComponentInParent<NavmeshGraphBinder>();

        if (TryLand(contact.point, contact.normal, landedOn))
        {
            var ripples = collision.gameObject.GetComponentInParent<CellImpactRipples>();
            if (ripples) ripples.AddImpact(contact.point, impactSpeed);
        }
    }

    // -----------------------------------------------------------------------
    // Focus Mode
    // -----------------------------------------------------------------------

    /// <summary>Stop input now; state, camera, button and event follow after focusEntryDelay.</summary>
    public void EnterFocusMode()
    {
        if (IsLocked) return;
        if (requireGroundedForFocusMode && state != State.Grounded) return;

        HaltMotion();
        if (state == State.Grounded) _rb.isKinematic = true; // never make an airborne body kinematic

        BeginSlam();

        _focusEntryPending = true;
        _focusEnterAt = Time.time + focusEntryDelay;
        if (focusEntryDelay <= 0f) CompleteFocusModeEntry();
    }

    void CompleteFocusModeEntry()
    {
        if (!_focusEntryPending) return;
        _focusEntryPending = false;

        HaltMotion();
        _rb.isKinematic = true;

        EnterState(State.FocusMode);
        onFocusModeEnter?.Invoke();
    }

    /// <summary>Leave Focus Mode (or cancel its entry beat) and resume grounded movement.</summary>
    public void ExitFocusMode()
    {
        if (_focusEntryPending)
        {
            _focusEntryPending = false;
            _slamTime = -1f; // visual eases home
            HaltMotion();
            return;
        }

        if (state != State.FocusMode) return;

        HaltMotion();
        _moveLockedUntil = Time.time;

        // Reverse the screen effect before the camera transitions.
        onFocusModeExit?.Invoke();
        EnterState(State.Grounded);
    }

    public void ToggleFocusMode()
    {
        if (IsLocked) ExitFocusMode();
        else EnterFocusMode();
    }

    /// <summary>
    /// Let an external system (e.g. a rope) own flight rotation via Rigidbody
    /// torque. On release the current orientation becomes the steering start,
    /// so control returns without a snap.
    /// </summary>
    public void SetExternalFlightRotationControl(bool enabled)
    {
        if (!_rb || enabled == _externalFlightRotationControl) return;

        if (enabled)
        {
            if (state != State.Flying) return;
            _constraintsBeforeExternal = _rb.constraints;
            _rb.constraints &= ~RigidbodyConstraints.FreezeRotation;
            _externalFlightRotationControl = true;
            return;
        }

        _externalFlightRotationControl = false;
        _targetRotation = RotTarget.rotation;
        _rb.angularVelocity = Vector3.zero;
        _rb.constraints = _constraintsBeforeExternal;
    }

    // -----------------------------------------------------------------------
    // Focus visual (charge lift / slam) -- moves only a visual child
    // -----------------------------------------------------------------------

    void BeginSlam()
    {
        // The button may complete earlier this frame than our LateUpdate ran;
        // start from full charge rather than one frame short of it.
        bool charged = groundedWorldButton && groundedWorldButton.IsCompleted;
        _slamFrom = Mathf.Max(_bodyOffset, charged ? focusHoldLiftAmount : 0f);
        _slamTime = 0f;
    }

    Transform ResolveVisual()
    {
        if (focusVisualBody && focusVisualBody != transform) return focusVisualBody;
        if (body && body != transform) return body;
        return null;
    }

    void UpdateFocusVisual(float dt)
    {
        Transform v = ResolveVisual();
        if (v != _visual)
        {
            if (_visual) _visual.localPosition = _visualHome;
            _visual = v;
            if (v) _visualHome = v.localPosition;
            _bodyOffset = 0f;
            _slamTime = -1f;
        }

        if (!_visual) return;

        WorldButton button = groundedWorldButton;

        if (_slamTime >= 0f)
        {
            _slamTime += dt;

            if (_slamTime <= focusSlamDuration)
            {
                float t = _slamTime / focusSlamDuration;
                _bodyOffset = Mathf.LerpUnclamped(_slamFrom, -focusSlamDownAmount, t * t * t); // hard into impact
            }
            else
            {
                float t = Mathf.Clamp01((_slamTime - focusSlamDuration) / focusSlamRecoveryDuration);
                _bodyOffset = Mathf.Lerp(-focusSlamDownAmount, 0f, t * t * (3f - 2f * t));
                if (t >= 1f) _slamTime = -1f;
            }
        }
        else if (state == State.Grounded && !_focusEntryPending && button && button.IsHolding)
        {
            _bodyOffset = focusHoldLiftAmount * Mathf.Pow(Mathf.Clamp01(button.HoldProgress), focusHoldLiftPower);
        }
        else if (_bodyOffset != 0f)
        {
            _bodyOffset = Mathf.Lerp(_bodyOffset, 0f, 1f - Mathf.Exp(-focusHoldCancelReturnSpeed * dt));
            if (Mathf.Abs(_bodyOffset) < 0.0005f) _bodyOffset = 0f;
        }

        Vector3 outward = (IsSurfaceAttached ? surfaceNormal : transform.up).normalized;
        Vector3 world = outward * _bodyOffset;
        Transform parent = _visual.parent;

        _visual.localPosition = _visualHome + (parent ? parent.InverseTransformVector(world) : world);
    }

    // -----------------------------------------------------------------------
    // Button / camera
    // -----------------------------------------------------------------------

    /// <summary>Show only while Grounded. Toggles only on mismatch; Show() resets a hold.</summary>
    void RefreshButton()
    {
        WorldButton b = groundedWorldButton;
        if (!b) return;

        GameObject root = b.visualRoot;

        if (state == State.Grounded)
        {
            if (!b.enabled || (root && !root.activeSelf)) b.Show();
        }
        else if (b.enabled || (root && root.activeSelf))
        {
            b.Hide();
        }
    }

    /// <summary>Rigs without a mode by that name are left alone.</summary>
    void SetCameraMode(string modeName)
    {
        if (string.IsNullOrEmpty(modeName) || cameraRigs == null) return;
        foreach (UniversalCamera rig in cameraRigs)
            if (rig) rig.SetMode(modeName);
    }

    // -----------------------------------------------------------------------
    // Input (new Input System only -- project has activeInputHandler = 1)
    // -----------------------------------------------------------------------

    static Vector2 ReadMove()
    {
        Vector2 v = Vector2.zero;
        Keyboard k = Keyboard.current;

        if (k != null)
            v = new Vector2((k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f),
                            (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f));

        if (Gamepad.current != null) v += Gamepad.current.leftStick.ReadValue();
        return Vector2.ClampMagnitude(v, 1f);
    }

    static bool ActionPressed() =>
        (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) ||
        (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame);
}
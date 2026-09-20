using UnityEngine;
using UnityEngine.Events;
using Pathfinding;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Two-state virus locomotion: free flight, and crawling an A* Pathfinding
/// Project navmesh graph.
///
/// Flying is Rigidbody driven; crawling snaps to the graph. The two never run
/// at once -- the Rigidbody is made kinematic on the ground, because both
/// write the transform and will fight for it otherwise.
///
/// Why this graph and not Unity's NavMesh: a NavMeshGraph is built from a mesh
/// you hand it, with no slope classification anywhere in the process. Unity's
/// baker rejects anything past 60 degrees from its up axis, so a sphere only
/// ever bakes a cap. Feed this graph a sphere mesh and the whole surface is
/// navigable, poles included.
///
/// This is the free version of A*PP 4.2, which has no Linecast on any graph,
/// so movement is clamped with GetNearest rather than cast against navmesh
/// edges. The difference only shows at holes in the mesh: across a narrow gap
/// GetNearest can snap to the far side instead of stopping at the near edge.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class VirusMovement : MonoBehaviour
{
    public enum State { Flying, Grounded, FocusMode }

    public enum Smoothing
    {
        /// <summary>Sit on the raw triangles. Visibly faceted on a sphere.</summary>
        Flat,

        /// <summary>
        /// Project onto the rounded surface the triangles approximate, by
        /// pushing out to a radius interpolated across the triangle. Exact for
        /// a sphere at any triangle count.
        /// </summary>
        Radial
    }

    [Header("References")]
    [Tooltip("Optional visual child. Left empty, the whole object turns.")]
    public Transform body;

    [Tooltip("Movement is relative to this. Falls back to Camera.main.")]
    public Transform cameraTransform;

    [Header("Surfaces")]
    [Tooltip("What counts as landable. Layers, not tags -- this project has " +
             "no tags defined, so a tag check could never match.")]
    public LayerMask surfaceLayers = ~0;

    [Tooltip("How far from a contact point the graph may be and still count " +
             "as a landing. Raise it if landings bounce off instead of sticking.")]
    public float snapDistance = 2f;

    [Tooltip("Distance held above the graph surface while crawling.")]
    public float hoverHeight = 0.5f;

    [Tooltip("Seconds after landing before the virus can walk. A short pause " +
             "reads as the impact settling rather than a skid.")]
    public float landingDelay = 0.25f;

    [Tooltip("Radial removes the faceting a coarse navmesh would otherwise " +
             "show. Assumes the cell is roughly star-shaped about its pivot, " +
             "which a blood cell is.")]
    public Smoothing surfaceSmoothing = Smoothing.Radial;



    [Header("Camera")]
    [Tooltip("Rigs told to change mode on takeoff and landing. Usually both " +
             "the holder and the camera, since each has its own mode list.")]
    public UniversalCamera[] cameraRigs;

    [Tooltip("Mode name switched to while flying. Leave empty to not switch.")]
    public string flyingCameraMode = "Flying";

    [Tooltip("Mode name switched to while grounded. Leave empty to not switch.")]
    public string groundedCameraMode = "Grounded";

    [Header("Focus Mode")]
    [Tooltip("Camera mode used while Focus Mode is active. The destination UniversalCamera mode's transition settings are respected.")]
    public string focusCameraMode = "Focus";

    [Tooltip("Normally Focus Mode is entered while crawling on a surface. When enabled, EnterFocusMode does nothing unless the virus is currently Grounded.")]
    public bool requireGroundedForFocusMode = true;

    [Tooltip("Optional actions fired when Focus Mode begins. Hook your screen-invert script's Trigger/Play/Start method here.")]
    public UnityEvent onFocusModeEnter;

    [Tooltip("Optional actions fired when Focus Mode ends. Hook your screen-invert script's Untrigger/Reverse/Stop method here.")]
    public UnityEvent onFocusModeExit;

    [Header("Focus Hold / Slam")]
    [Tooltip("VISUAL-ONLY transform moved by the focus charge/slam. Assign the virus mesh/model child, NOT the VirusMovement/Rigidbody root. " +
             "If left empty, the existing Body reference is used only when Body is a child and is not the locomotion root.")]
    public Transform focusVisualBody;

    [Tooltip("Seconds after the WorldButton completes before Focus Mode actually begins. Movement stops immediately; camera switching, state change, button hiding, and On Focus Mode Enter wait for this delay.")]
    [Min(0f)]
    public float focusEntryDelay = 0.18f;

    [Tooltip("How far the visual Body rises outward from the current surface during the hold.")]
    [Min(0f)]
    public float focusHoldLiftAmount = 0.30f;

    [Tooltip("Shape of the rise during the hold. Above 1 makes the lift build harder near completion.")]
    [Min(0.1f)]
    public float focusHoldLiftPower = 1.35f;

    [Tooltip("How far BELOW the authored Body position the visual body impacts when the hold completes.")]
    [Min(0f)]
    public float focusSlamDownAmount = 0.16f;

    [Tooltip("Seconds from the raised hold pose to the downward impact.")]
    [Min(0.01f)]
    public float focusSlamDuration = 0.10f;

    [Tooltip("Seconds for the body to recover from impact back to its authored position.")]
    [Min(0.01f)]
    public float focusSlamRecoveryDuration = 0.18f;

    [Tooltip("How quickly a cancelled/incomplete hold returns the visual body to normal.")]
    [Min(0.01f)]
    public float focusHoldCancelReturnSpeed = 10f;

    [Header("World Button")]
    [Tooltip("Interaction button that should exist only while the virus is Grounded. " +
             "VirusMovement automatically shows it while Grounded and hides it while Flying or in Focus Mode.")]
    public WorldButton groundedWorldButton;

    [Header("Speeds")]
    public float flySpeed = 12f;
    public float walkSpeed = 4f;
    public float jumpForce = 8f;

    [Header("Ground Walk Accel / Decel")]
    [Tooltip("When off, grounded movement uses Walk Speed immediately like before. When on, grounded speed ramps using the AnimationCurve graphs below.")]
    public bool useWalkSpeedCurves = false;

    [Tooltip("Seconds to transition from the current grounded speed toward Walk Speed after movement input begins.")]
    [Min(0.01f)]
    public float walkAccelerationTime = 0.28f;

    [Tooltip("Acceleration graph. X = normalized time 0 to 1. Y = normalized progress from the starting speed to the requested speed.")]
    public AnimationCurve walkAccelerationCurve =
        AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("Seconds to transition from the current grounded speed to zero after movement input is released.")]
    [Min(0.01f)]
    public float walkDecelerationTime = 0.20f;

    [Tooltip("Deceleration graph. X = normalized time 0 to 1. Y = normalized progress from release speed toward zero.")]
    public AnimationCurve walkDecelerationCurve =
        AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Feel")]
    [Tooltip("How quickly the virus turns to face where it is going, as a " +
             "rate. Higher is snappier, lower is lazier; both are smooth, " +
             "because the turn is eased once per rendered frame rather " +
             "than once per physics step. Around 6 reads as floaty, 20 as " +
             "sharp.")]
    public float rotationSharpness = 8f;

    [Tooltip("Base acceleration in units per second squared. This alone sets " +
             "how fast the virus builds up speed in a straight line.")]
    public float flyAcceleration = 40f;

    [Tooltip("Deceleration once thrust is released, in units per second " +
             "squared. Lower values coast further. Deliberately separate from " +
             "playerControl so the virus can turn sharply and still glide.")]
    public float coastDeceleration = 6f;

    [Range(0f, 1f)]
    [Tooltip("Extra authority when thrust fights the momentum the virus " +
             "already has, which is what turning is. Straight line build-up " +
             "is never boosted, so acceleration still reads as acceleration, " +
             "and coasting is handled by Coast Deceleration instead. 0 leaves " +
             "momentum untouchable; 0.5 roughly doubles the acceleration " +
             "available against it; 1 overrides it outright.")]
    public float playerControl = 0.5f;

    [Tooltip("Which local axis of the model leads while flying. Down means the " +
             "virus travels underside-first. Depends on how the mesh was built, " +
             "so change this rather than rotating the model.")]
    public Vector3 flyingLeadAxis = Vector3.down;

    public State state { get; private set; } = State.Flying;

    /// <summary>
    /// Actual movement speed in world units per second. While flying this is the
    /// Rigidbody velocity magnitude. While grounded it is measured from the
    /// graph movement step, so camera effects do not have to estimate speed from
    /// render-frame Transform deltas.
    /// </summary>
    public float speed
    {
        get
        {
            if (state == State.Flying)
                return _rb ? _rb.linearVelocity.magnitude : 0f;

            if (state == State.Grounded)
                return _groundedSpeed;

            return 0f;
        }
    }

    /// <summary>
    /// Intended top movement speed for the current locomotion state. Focus Mode
    /// has no player movement, so its total speed is zero.
    /// </summary>
    public float totalSpeed
    {
        get
        {
            if (state == State.Flying) return Mathf.Max(flySpeed, 0f);
            if (state == State.Grounded) return Mathf.Max(walkSpeed, 0f);
            return 0f;
        }
    }

    /// <summary>Current speed normalised against the current state's top speed.</summary>
    public float normalizedSpeed => totalSpeed > 1e-5f
        ? Mathf.Clamp01(speed / totalSpeed)
        : 0f;

    /// <summary>True while player locomotion/input is locked in Focus Mode.</summary>
    public bool IsFocusMode => state == State.FocusMode;

    /// <summary>Outward normal of the graph triangle under the virus.</summary>
    public Vector3 surfaceNormal { get; private set; } = Vector3.up;

    /// <summary>
    /// Cell whose local space the current graph is baked in, discovered from
    /// whatever was landed on. Null while flying, and null on a world-space
    /// graph, where every conversion below is the identity.
    /// </summary>
    public Transform cellSpace { get; private set; }

    /// <summary>Cell currently stood on. Null while flying.</summary>
    public NavmeshGraphBinder cell { get; private set; }

    // NNConstraint.Default news up an instance on every access, so it is
    // cached rather than allocated once per physics step. Per-instance because
    // its graphMask is rewritten per query.
    readonly NNConstraint _constraint = NNConstraint.Default;

    // Authoritative crawl state lives in graph space, not world space. Derive
    // world from it each step and the virus stays pinned to the cell however
    // the cell moves; derive graph space from the world transform instead and
    // it slides whenever the cell rotates.
    // Physics runs at a fixed step but you see the result at render rate, so
    // the steps aim a target here and Update eases toward it every frame.
    // Slerping inside FixedUpdate is what made a high sharpness look steppy
    // rather than snappy.
    Quaternion _targetRotation = Quaternion.identity;

    // Raw point on the navmesh, never the smoothed one. Smoothing lifts the
    // position onto the sphere, off the flat triangle -- feeding that back
    // into GetNearest next step projects it perpendicular back down, and the
    // sideways part of that projection shifts from triangle to triangle. That
    // was the sway while walking, and the slide for a moment after stopping,
    // as the projection settled with no input at all.
    Vector3 _graphPosition;
    Vector3 _graphForward = Vector3.forward;
    GraphMask _graphMask = GraphMask.everything;

    Rigidbody _rb;
    Transform _camera;
    Vector2 _move;
    bool _jumpQueued;
    float _landingLockedUntil;
    float _moveLockedUntil;
    float _groundedSpeed;

    // Desired crawl speed, separate from _groundedSpeed which measures the
    // actual movement achieved across the graph.
    float _groundedCommandSpeed;
    float _walkCurveElapsed;
    float _walkCurveStartSpeed;
    bool _walkHadInput;
    Vector3 _lastGroundMoveDirection;

    bool _focusEntryPending;
    float _focusEntryCompleteAt;

    WorldButton _boundFocusButton;

    Transform _focusVisualTarget;
    Vector3 _bodyAuthoredLocalPosition;
    bool _bodyPositionSeeded;

    bool _focusHoldActive;
    bool _focusBodyReturning;

    bool _focusSlamActive;
    float _focusSlamElapsed;
    float _focusSlamStartOffset;
    float _focusBodyOffset;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();

        // Rotation is driven explicitly, so physics must not tumble the virus.
        _rb.constraints = RigidbodyConstraints.FreezeRotation;
        _rb.useGravity = false;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        _camera = cameraTransform;
        if (!_camera && Camera.main) _camera = Camera.main.transform;

        _targetRotation = transform.rotation;

        SeedFocusBodyPosition();
        BindGroundedWorldButtonEvents();
        RefreshGroundedWorldButton();
    }

    void OnEnable()
    {
        BindGroundedWorldButtonEvents();
    }

    void OnDisable()
    {
        UnbindGroundedWorldButtonEvents();
        RestoreFocusBodyImmediately();
    }

    void OnValidate()
    {
        focusEntryDelay = Mathf.Max(0f, focusEntryDelay);
        focusHoldLiftAmount = Mathf.Max(0f, focusHoldLiftAmount);
        focusHoldLiftPower = Mathf.Max(0.1f, focusHoldLiftPower);
        focusSlamDownAmount = Mathf.Max(0f, focusSlamDownAmount);
        focusSlamDuration = Mathf.Max(0.01f, focusSlamDuration);
        focusSlamRecoveryDuration = Mathf.Max(0.01f, focusSlamRecoveryDuration);
        focusHoldCancelReturnSpeed = Mathf.Max(0.01f, focusHoldCancelReturnSpeed);

        walkAccelerationTime = Mathf.Max(0.01f, walkAccelerationTime);
        walkDecelerationTime = Mathf.Max(0.01f, walkDecelerationTime);
    }

    void LateUpdate()
    {
        UpdateFocusBodyAnimation(Time.deltaTime);
    }

    void Update()
    {
        if (!_camera && Camera.main) _camera = Camera.main.transform;

        // Keep the prompt tied to locomotion state even if another system
        // temporarily changes it. This does not call Show() every frame, so
        // an in-progress hold is not reset.
        RefreshGroundedWorldButton();

        // Hold completion kills locomotion immediately, but the rest of the
        // Focus transition waits for the configured delay/slam beat.
        if (_focusEntryPending)
        {
            _move = Vector2.zero;
            _jumpQueued = false;
            _groundedSpeed = 0f;

            if (_rb)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }

            if (Time.time >= _focusEntryCompleteAt)
                CompleteFocusModeEntry();

            return;
        }

        // Focus Mode owns the player completely. No movement input is read, no
        // jump can queue, GroundedStep does not run, and rotation is left alone.
        // Escape is the only locomotion-script input still listened for.
        if (state == State.FocusMode)
        {
            _move = Vector2.zero;
            _jumpQueued = false;
            _groundedSpeed = 0f;

            if (ReadFocusExitPressed())
                ExitFocusMode();

            return;
        }

        _move = ReadMove();

        // Only queued from the ground. Queuing in the air and firing on contact
        // would hand out a free second jump the instant the virus lands, and
        // was also why space did anything at all mid-flight.
        if (state == State.Grounded && ReadJumpPressed())
            _jumpQueued = true;

        if (_jumpQueued && state == State.Grounded)
        {
            _jumpQueued = false;
            Launch(transform.up * jumpForce);
        }

        // Crawling is kinematic and writes the transform directly, so it has no
        // reason to run at the physics rate -- and doing so stepped the
        // position 50 times a second while drawing far more often, which is
        // the grounded jitter. Flight stays in FixedUpdate because it is real
        // physics and wants interpolation.
        if (state == State.Grounded) GroundedStep(Time.deltaTime);

        ApplyRotation(Time.deltaTime);
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
        Vector3 forward = _camera ? _camera.forward : Vector3.forward;

        // Forward thrust only: no strafe, no reverse, no vertical. Steering is
        // done by aiming the camera. Releasing the key leaves a zero target,
        // which the coast rate eases toward, so no separate brake is needed.
        Vector3 wish = forward * Mathf.Max(0f, _move.y);

        Vector3 velocity = _rb.linearVelocity;
        Vector3 target = wish * flySpeed;
        Vector3 delta = target - velocity;

        // How much the change being asked for fights the motion already there:
        // 0 when the two agree, 1 when it is straight against it.
        //
        // Accelerating from rest reads as 0, so straight line build-up is
        // governed by flyAcceleration alone and still feels like acceleration.
        // Turning and stopping read high, which is where control belongs.
        float opposition = 0f;
        if (delta.sqrMagnitude > 1e-6f && velocity.sqrMagnitude > 1e-6f)
            opposition = Mathf.Clamp01(-Vector3.Dot(delta.normalized, velocity.normalized));

        // 0 gives no boost, 0.5 doubles it, 1 goes effectively unlimited, so
        // the top of the slider really does override momentum rather than just
        // accelerating hard into it. Clamped rather than infinite so the
        // multiply stays finite when opposition is zero.
        float boost = playerControl / Mathf.Max(1f - playerControl, 1e-4f);

        // Coasting is its own rate. Letting go leaves a zero target, which
        // counts as fully opposing, so it would otherwise inherit the whole
        // turning boost and stop dead the instant control was high.
        bool thrusting = wish.sqrMagnitude > 1e-6f;

        float acceleration = thrusting
            ? flyAcceleration * (1f + opposition * boost)
            : coastDeceleration;

        _rb.linearVelocity = Vector3.MoveTowards(velocity, target, acceleration * dt);

        // Aim only while actually thrusting. Following velocity instead meant
        // the virus kept slewing around as it coasted to a stop, and velocity
        // wanders while decaying so the rotation never settled.
        //
        // Aiming at the thrust direction rather than the velocity also removes
        // the degenerate case: heading is the camera's forward, which is always
        // perpendicular to the camera's up, so the roll reference never goes
        // parallel to it and the orientation never snaps.
        if (_move.y > 0.01f)
        {
            _targetRotation = LeadAxisRotation(forward.normalized,
                                               _camera ? _camera.up : Vector3.up);
        }

        // With no body child the root transform is the visual, and the root is
        // also what Rigidbody interpolation writes to every frame. Writing it
        // from Update as well makes the two fight, which is the flying jitter.
        // MoveRotation feeds the rotation through physics instead, so
        // interpolation smooths it between steps rather than undoing it.
        if (!body)
        {
            _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, _targetRotation,
                                              RotationWeight(dt)));
        }
    }

    // -----------------------------------------------------------------------
    // Grounded
    // -----------------------------------------------------------------------

    float UpdateGroundWalkCommandSpeed(float inputAmount, float dt)
    {
        float topSpeed = Mathf.Max(0f, walkSpeed);
        float requestedSpeed = topSpeed * Mathf.Clamp01(inputAmount);

        if (!useWalkSpeedCurves)
        {
            _groundedCommandSpeed = requestedSpeed;
            _walkHadInput = inputAmount > 0.001f;
            _walkCurveElapsed = 0f;
            _walkCurveStartSpeed = _groundedCommandSpeed;
            return _groundedCommandSpeed;
        }

        bool hasInput = inputAmount > 0.001f;

        // Start a new traversal only when crossing between pressed/released.
        // This avoids restarting the graph every frame from analog-stick noise.
        if (hasInput != _walkHadInput)
        {
            _walkCurveElapsed = 0f;
            _walkCurveStartSpeed = _groundedCommandSpeed;
            _walkHadInput = hasInput;
        }

        _walkCurveElapsed += Mathf.Max(0f, dt);

        if (hasInput)
        {
            float t = Mathf.Clamp01(
                _walkCurveElapsed / Mathf.Max(0.01f, walkAccelerationTime));

            float progress = walkAccelerationCurve != null
                ? Mathf.Clamp01(walkAccelerationCurve.Evaluate(t))
                : t;

            _groundedCommandSpeed = Mathf.LerpUnclamped(
                _walkCurveStartSpeed,
                requestedSpeed,
                progress);

            if (t >= 1f)
                _groundedCommandSpeed = requestedSpeed;
        }
        else
        {
            float t = Mathf.Clamp01(
                _walkCurveElapsed / Mathf.Max(0.01f, walkDecelerationTime));

            float progress = walkDecelerationCurve != null
                ? Mathf.Clamp01(walkDecelerationCurve.Evaluate(t))
                : t;

            _groundedCommandSpeed = Mathf.LerpUnclamped(
                _walkCurveStartSpeed,
                0f,
                progress);

            if (t >= 1f)
                _groundedCommandSpeed = 0f;
        }

        return Mathf.Max(0f, _groundedCommandSpeed);
    }

    void ResetGroundWalkSpeedProfile()
    {
        _groundedCommandSpeed = 0f;
        _walkCurveElapsed = 0f;
        _walkCurveStartSpeed = 0f;
        _walkHadInput = false;
        _lastGroundMoveDirection = Vector3.zero;
    }

    void GroundedStep(float dt)
    {
        if (AstarPath.active == null)
        {
            Launch(transform.forward * walkSpeed);
            return;
        }

        // Rebuild world state from graph space, so a cell that moved or turned
        // since the last step carries the virus with it.
        Vector3 worldPos = GraphToWorld(_graphPosition);
        Vector3 up = surfaceNormal;

        // Walk relative to the triangle we are standing on, not to world up --
        // on the underside of a sphere those are opposite.
        Vector3 camForward = _camera ? _camera.forward : Vector3.forward;
        Vector3 camRight = _camera ? _camera.right : Vector3.right;

        Vector3 tangentF = Vector3.ProjectOnPlane(camForward, up);
        Vector3 tangentR = Vector3.ProjectOnPlane(camRight, up);

        // Looking straight down the normal leaves no usable forward; fall back
        // to the camera's own up so control does not die directly overhead.
        if (tangentF.sqrMagnitude < 1e-4f)
            tangentF = Vector3.ProjectOnPlane(_camera ? _camera.up : Vector3.up, up);

        tangentF = tangentF.normalized;
        tangentR = tangentR.sqrMagnitude > 1e-4f
            ? tangentR.normalized
            : Vector3.Cross(up, tangentF);

        // Input is ignored briefly after touching down, so the virus settles
        // where it hit instead of sliding straight off it.
        Vector2 input = Time.time < _moveLockedUntil ? Vector2.zero : _move;

        Vector3 inputWish = tangentF * input.y + tangentR * input.x;
        float inputAmount = Mathf.Clamp01(input.magnitude);

        bool hasMoveInput =
            inputWish.sqrMagnitude > 1e-6f &&
            inputAmount > 0.001f;

        Vector3 wishDirection;

        if (hasMoveInput)
        {
            wishDirection = inputWish.normalized;
            _lastGroundMoveDirection = wishDirection;
        }
        else
        {
            // Curve-based deceleration carries the virus in its last crawl
            // direction while continuously re-projecting onto the curved cell.
            wishDirection = Vector3.ProjectOnPlane(
                _lastGroundMoveDirection,
                up);

            if (wishDirection.sqrMagnitude > 1e-6f)
                wishDirection.Normalize();
            else
                wishDirection = Vector3.zero;
        }

        float commandedWalkSpeed = UpdateGroundWalkCommandSpeed(
            hasMoveInput ? inputAmount : 0f,
            dt);

        Vector3 wish = wishDirection * commandedWalkSpeed;

        // Step in world units so Walk Speed remains metres per second whatever
        // the cell scale.
        Vector3 desired = worldPos + wish * dt;

        _constraint.graphMask = _graphMask;
        NNInfo nearest = AstarPath.active.GetNearest(WorldToGraph(desired), _constraint);

        if (nearest.node == null)
        {
            Launch(transform.forward * walkSpeed);
            return;
        }

        Vector3 previousGraphPosition = _graphPosition;
        _graphPosition = nearest.position;

        Vector3 smoothedPosition = SmoothSurface(cellSpace, nearest.node,
                                                 nearest.position, up,
                                                 out Vector3 smoothedNormal);
        surfaceNormal = smoothedNormal;

        // Measure only movement across the cell surface. Both graph points are
        // converted through the cell's current transform, so motion of the cell
        // itself does not falsely count as player speed.
        if (dt > 1e-5f)
        {
            Vector3 previousWorld = GraphToWorld(previousGraphPosition);
            Vector3 currentWorld = GraphToWorld(_graphPosition);
            _groundedSpeed = Vector3.Distance(previousWorld, currentWorld) / dt;
        }
        else
        {
            _groundedSpeed = 0f;
        }

        // Falls through a chain rather than bailing out. Leaving the target
        // untouched when the heading degenerated is why the virus sometimes
        // stopped facing the ground: the stored forward can drift parallel to
        // the normal as the surface curves, and projecting it then collapses
        // to zero. The last fallback cannot fail.
        Vector3 heading = wishDirection.sqrMagnitude > 1e-4f
            ? wishDirection
            : Vector3.ProjectOnPlane(GraphDirToWorld(_graphForward), surfaceNormal);

        if (heading.sqrMagnitude < 1e-4f)
            heading = Vector3.ProjectOnPlane(transform.forward, surfaceNormal);

        if (heading.sqrMagnitude < 1e-4f)
            heading = AnyPerpendicular(surfaceNormal);

        heading.Normalize();
        _graphForward = WorldDirToGraph(heading);
        _targetRotation = Quaternion.LookRotation(heading, surfaceNormal);

        transform.position = GraphToWorld(smoothedPosition) + surfaceNormal * hoverHeight;
    }

    /// <summary>
    /// Rotation that points <see cref="flyingLeadAxis"/> along heading.
    ///
    /// Built as LookRotation * B, where B is the fixed rotation carrying the
    /// lead axis onto local forward. The composition maps lead -> forward ->
    /// heading, so any axis works without a special case per direction, and
    /// rollReference still decides the spin about the travel direction.
    /// </summary>
    Quaternion LeadAxisRotation(Vector3 heading, Vector3 rollReference)
    {
        Vector3 lead = flyingLeadAxis.sqrMagnitude > 1e-6f
            ? flyingLeadAxis.normalized
            : Vector3.forward;

        Quaternion toForward = Quaternion.Inverse(
            Quaternion.LookRotation(lead, AnyPerpendicular(lead)));

        return SafeLookRotation(heading, rollReference) * toForward;
    }

    /// <summary>
    /// LookRotation with a usable up vector. Unity's gives an arbitrary result
    /// when up is parallel to forward, which happens the moment the virus flies
    /// straight along whatever the roll reference is.
    /// </summary>
    static Quaternion SafeLookRotation(Vector3 forward, Vector3 up)
    {
        if (Mathf.Abs(Vector3.Dot(forward, up.normalized)) > 0.999f)
            up = AnyPerpendicular(forward);

        return Quaternion.LookRotation(forward, up);
    }

    /// <summary>Any unit vector perpendicular to v, chosen deterministically.</summary>
    static Vector3 AnyPerpendicular(Vector3 v)
    {
        // Cross with whichever world axis v is least aligned to, so the result
        // is never near zero length.
        Vector3 axis = Mathf.Abs(v.y) < 0.9f ? Vector3.up : Vector3.right;
        return Vector3.Cross(v, axis).normalized;
    }

    // -----------------------------------------------------------------------
    // Graph space
    //
    // Identity when cellSpace is unset, so a world-space graph costs nothing.
    // -----------------------------------------------------------------------

    static Vector3 ToWorld(Transform space, Vector3 p) => space ? space.TransformPoint(p) : p;
    static Vector3 ToGraph(Transform space, Vector3 p) => space ? space.InverseTransformPoint(p) : p;

    Vector3 GraphToWorld(Vector3 p) => ToWorld(cellSpace, p);
    Vector3 WorldToGraph(Vector3 p) => ToGraph(cellSpace, p);
    Vector3 GraphDirToWorld(Vector3 d) => cellSpace ? cellSpace.TransformDirection(d) : d;
    Vector3 WorldDirToGraph(Vector3 d) => cellSpace ? cellSpace.InverseTransformDirection(d) : d;

    /// <summary>
    /// Centre the surface curves around, in graph space.
    ///
    /// Local-space graphs are baked at the origin, so the cell's pivot is
    /// simply zero. A world-space graph sits where the cell sits.
    /// </summary>
    Vector3 SurfaceCentre()
    {
        if (cellSpace) return Vector3.zero;
        return cell != null ? cell.Cell.position : Vector3.zero;
    }

    /// <summary>
    /// Lift a point off the flat triangle onto the rounded surface those
    /// triangles approximate, and report the world normal there.
    ///
    /// GetNearest still does the containment against the real mesh, so the
    /// virus cannot be smoothed off the edge of the navmesh -- this only
    /// adjusts where it sits within the triangle it already landed on.
    ///
    /// The radius is interpolated across the triangle rather than assumed
    /// constant, so a lumpy cell curves correctly too; on a true sphere all
    /// three vertices share a radius and the result is exact regardless of how
    /// coarse the mesh is.
    /// </summary>
    Vector3 SmoothSurface(Transform space, GraphNode node, Vector3 graphPoint,
                          Vector3 referenceNormal, out Vector3 worldNormal)
    {
        worldNormal = NodeNormal(space, node, referenceNormal);

        if (surfaceSmoothing == Smoothing.Flat || !(node is TriangleMeshNode triangle))
            return graphPoint;

        Vector3 a = (Vector3)triangle.GetVertex(0);
        Vector3 b = (Vector3)triangle.GetVertex(1);
        Vector3 c = (Vector3)triangle.GetVertex(2);

        Vector3 centre = SurfaceCentre();
        Vector3 outward = graphPoint - centre;
        if (outward.sqrMagnitude < 1e-8f) return graphPoint;

        Vector3 bary = Barycentric(graphPoint, a, b, c);

        float radius = bary.x * (a - centre).magnitude
                     + bary.y * (b - centre).magnitude
                     + bary.z * (c - centre).magnitude;

        Vector3 smoothed = centre + outward.normalized * radius;

        // Normal taken in world space from the smoothed point, so it varies
        // continuously instead of snapping at every triangle edge -- the
        // faceting is far more visible in the orientation than the position.
        Vector3 worldCentre = ToWorld(space, centre);
        Vector3 worldPoint = ToWorld(space, smoothed);

        Vector3 radial = worldPoint - worldCentre;
        if (radial.sqrMagnitude > 1e-8f) worldNormal = radial.normalized;

        return smoothed;
    }

    /// <summary>Barycentric coordinates of p within triangle abc.</summary>
    static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = b - a, v1 = c - a, v2 = p - a;

        float d00 = Vector3.Dot(v0, v0);
        float d01 = Vector3.Dot(v0, v1);
        float d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0);
        float d21 = Vector3.Dot(v2, v1);

        float denom = d00 * d11 - d01 * d01;
        if (Mathf.Abs(denom) < 1e-12f) return new Vector3(1f, 0f, 0f);

        float v = (d11 * d20 - d01 * d21) / denom;
        float w = (d00 * d21 - d01 * d20) / denom;
        return new Vector3(1f - v - w, v, w);
    }

    /// <summary>
    /// Outward normal of a navmesh triangle, in world space.
    ///
    /// The vertices are converted individually and crossed afterwards, rather
    /// than crossing in graph space and rotating the result. Those agree under
    /// uniform scale, but only this one stays correct when the cell is scaled
    /// unevenly, because a normal does not transform like a direction.
    ///
    /// Winding decides the sign, and a graph built from an imported mesh can
    /// wind either way, so the result is flipped to agree with the normal we
    /// already had. That keeps it continuous across triangles and self-corrects
    /// rather than depending on how the mesh was authored.
    /// </summary>
    static Vector3 NodeNormal(Transform space, GraphNode node, Vector3 reference)
    {
        if (!(node is TriangleMeshNode triangle)) return reference;

        Vector3 a = ToWorld(space, (Vector3)triangle.GetVertex(0));
        Vector3 b = ToWorld(space, (Vector3)triangle.GetVertex(1));
        Vector3 c = ToWorld(space, (Vector3)triangle.GetVertex(2));

        Vector3 normal = Vector3.Cross(b - a, c - a);
        if (normal.sqrMagnitude < 1e-8f) return reference;

        normal.Normalize();
        return Vector3.Dot(normal, reference) < 0f ? -normal : normal;
    }

    // -----------------------------------------------------------------------
    // Focus Mode
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enter the non-interactive Focus Mode. Intended for UnityEvents such as a
    /// completed WorldButton hold. Player locomotion/jump/rotation input stops,
    /// the configured camera mode is selected, and On Focus Mode Enter fires.
    /// </summary>
    public void EnterFocusMode()
    {
        if (state == State.FocusMode ||
            _focusEntryPending)
            return;

        if (requireGroundedForFocusMode &&
            state != State.Grounded)
            return;

        // IMMEDIATE portion: stop player control the instant the hold completes.
        _move = Vector2.zero;
        _jumpQueued = false;
        _groundedSpeed = 0f;
        ResetGroundWalkSpeedProfile();

        if (_rb)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;

            // Never convert an airborne Rigidbody to kinematic during this
            // visual sequence. Normal use enters focus from Grounded, where it
            // is already kinematic.
            if (state == State.Grounded)
                _rb.isKinematic = true;
        }

        _focusHoldActive = false;
        _focusBodyReturning = false;

        BeginFocusSlam();

        // DELAYED portion: state/camera/events/button visibility.
        _focusEntryPending = true;
        _focusEntryCompleteAt =
            Time.time +
            Mathf.Max(0f, focusEntryDelay);

        if (focusEntryDelay <= 0f)
            CompleteFocusModeEntry();
    }

    void CompleteFocusModeEntry()
    {
        if (!_focusEntryPending)
            return;

        _focusEntryPending = false;

        state = State.FocusMode;
        RefreshGroundedWorldButton();

        _move = Vector2.zero;
        _jumpQueued = false;
        _groundedSpeed = 0f;

        if (_rb)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = true;
        }

        SetCameraMode(focusCameraMode);
        onFocusModeEnter?.Invoke();
    }

    /// <summary>
    /// Leave Focus Mode and resume normal grounded movement. This is public so
    /// another UI/event can exit as well as the built-in Escape key.
    /// </summary>
    public void ExitFocusMode()
    {
        if (_focusEntryPending)
        {
            _focusEntryPending = false;
            _focusHoldActive = false;
            _focusSlamActive = false;
            _focusBodyReturning = true;

            _move = Vector2.zero;
            _jumpQueued = false;
            _groundedSpeed = 0f;
            return;
        }

        if (state != State.FocusMode)
            return;

        state = State.Grounded;
        RefreshGroundedWorldButton();

        _move = Vector2.zero;
        _jumpQueued = false;
        _groundedSpeed = 0f;
        ResetGroundWalkSpeedProfile();
        _moveLockedUntil = Time.time; // movement is available immediately

        if (_rb)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.isKinematic = true;
        }

        // Reverse/disable the screen effect first, then let the destination
        // Grounded camera mode perform whatever transition it has configured.
        onFocusModeExit?.Invoke();
        SetCameraMode(groundedCameraMode);
    }

    /// <summary>Convenience function for UI buttons that want one method.</summary>
    public void ToggleFocusMode()
    {
        if (state == State.FocusMode ||
            _focusEntryPending)
            ExitFocusMode();
        else
            EnterFocusMode();
    }

    // -----------------------------------------------------------------------
    // State changes
    // -----------------------------------------------------------------------

    void Launch(Vector3 velocity)
    {
        _focusEntryPending = false;
        _focusHoldActive = false;
        _focusSlamActive = false;
        _focusBodyReturning = true;

        state = State.Flying;
        RefreshGroundedWorldButton();

        _jumpQueued = false;
        _groundedSpeed = 0f;
        ResetGroundWalkSpeedProfile();

        // Release the cell. Local-space graphs all sit at the origin, so a
        // stale binding would convert the next query through the wrong cell.
        cell = null;
        cellSpace = null;
        _graphMask = GraphMask.everything;

        // Reassert ALL physics invariants required for flight. Focus visuals
        // are completely separate from this Transform/Rigidbody.
        _rb.isKinematic = false;
        _rb.useGravity = false;
        _rb.constraints = RigidbodyConstraints.FreezeRotation;
        _rb.linearVelocity = velocity;
        _rb.angularVelocity = Vector3.zero;

        SetCameraMode(flyingCameraMode);

        // Without a grace window the jump re-collides with the surface on the
        // very next step and sticks straight back down.
        _landingLockedUntil = Time.time + 0.15f;
    }

    /// <summary>
    /// Try to attach to the navmesh graph near a contact point. Returns false
    /// when the graph is too far away, leaving the virus in flight.
    /// </summary>
    public bool TryLand(Vector3 point, Vector3 up, NavmeshGraphBinder landedOn)
    {
        if (AstarPath.active == null) return false;

        // Resolve into locals first. Committing to the fields before the
        // landing is known to succeed would leave a failed attempt bound to a
        // cell it never reached.
        Transform space = landedOn != null && landedOn.space == NavmeshGraphBinder.Space.Local
            ? landedOn.Cell
            : null;

        // Every local-space graph is baked at the origin, so they overlap.
        // Without this mask GetNearest would search all of them and could
        // return a point on a completely different cell.
        GraphMask mask = landedOn != null
            ? (GraphMask)(1 << landedOn.graphIndex)
            : GraphMask.everything;

        _constraint.graphMask = mask;

        NNInfo nearest = AstarPath.active.GetNearest(ToGraph(space, point), _constraint);
        if (nearest.node == null) return false;

        // GetNearest always returns something if any graph is scanned, so the
        // distance check is what actually decides whether this is a landing.
        // Measured in world space, so snapDistance stays in metres whatever
        // the cell is scaled to.
        if (Vector3.Distance(point, ToWorld(space, nearest.position)) > snapDistance)
            return false;

        state = State.Grounded;
        RefreshGroundedWorldButton();

        _groundedSpeed = 0f;
        ResetGroundWalkSpeedProfile();
        _moveLockedUntil = Time.time + landingDelay;
        cell = landedOn;
        cellSpace = space;
        _graphMask = mask;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.isKinematic = true;

        // Seed the normal from the contact so NodeNormal has a correct side to
        // agree with; after this it carries itself from triangle to triangle.
        _graphPosition = nearest.position;

        Vector3 landedPosition = SmoothSurface(space, nearest.node,
                                               nearest.position, up,
                                               out Vector3 landedNormal);
        surfaceNormal = landedNormal;

        Vector3 heading = Vector3.ProjectOnPlane(transform.forward, surfaceNormal);
        if (heading.sqrMagnitude < 1e-4f)
            heading = Vector3.ProjectOnPlane(transform.up, surfaceNormal);

        transform.position = GraphToWorld(landedPosition) + surfaceNormal * hoverHeight;

        if (heading.sqrMagnitude > 1e-4f)
        {
            heading.Normalize();
            _graphForward = WorldDirToGraph(heading);
            _targetRotation = Quaternion.LookRotation(heading, surfaceNormal);

            // Landing is a snap, so move the live rotation with the target
            // rather than easing into it.
            if (body) body.rotation = _targetRotation;
            else transform.rotation = _targetRotation;
        }

        SetCameraMode(groundedCameraMode);
        return true;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (state != State.Flying) return;
        if (Time.time < _landingLockedUntil) return;

        // Layer test, not a tag test: no tags exist in this project.
        if ((surfaceLayers.value & (1 << collision.gameObject.layer)) == 0) return;
        if (collision.contactCount == 0) return;

        ContactPoint contact = collision.GetContact(0);

        // Captured before TryLand zeroes the velocity. relativeVelocity is the
        // closing speed of the two bodies, which is what "how hard" means here.
        float impactSpeed = collision.relativeVelocity.magnitude;

        // The binder says which graph this cell owns and whether that graph is
        // baked in its local space. Absent one, fall back to a world-space
        // graph searched across everything.
        var landedOn = collision.gameObject.GetComponentInParent<NavmeshGraphBinder>();

        if (TryLand(contact.point, contact.normal, landedOn))
        {
            var ripples = collision.gameObject.GetComponentInParent<CellImpactRipples>();
            if (ripples) ripples.AddImpact(contact.point, impactSpeed);
        }
    }

    /// <summary>
    /// Turn the body if one is assigned, otherwise the whole object. Uses an
    /// exponential weight so the turn rate does not change with framerate.
    /// </summary>
    /// <summary>
    /// Ease toward the aimed rotation. Called from Update, not FixedUpdate, so
    /// it runs once per rendered frame -- the exponential weight keeps the rate
    /// framerate independent either way, but stepping it at 50Hz while drawing
    /// at 144 is visible no matter how the weight is computed.
    ///
    /// Rotation is frozen on the Rigidbody, so writing the Transform here
    /// cannot fight the physics step.
    /// </summary>
    void ApplyRotation(float dt)
    {
        float t = RotationWeight(dt);

        if (body)
        {
            // A child transform is never touched by physics, so this is always
            // safe and always smooth. Assigning a body is the best setup.
            body.rotation = Quaternion.Slerp(body.rotation, _targetRotation, t);
            return;
        }

        if (state == State.Grounded)
        {
            // Kinematic while crawling, so nothing else writes the transform.
            transform.rotation = Quaternion.Slerp(transform.rotation, _targetRotation, t);
        }

        // Flying with no body child is handled by MoveRotation in FlyStep, so
        // Rigidbody interpolation owns the in-between frames. Writing the
        // transform here as well is what made it stutter.
    }

    /// <summary>Framerate-independent easing weight for the turn rate.</summary>
    float RotationWeight(float dt)
    {
        return rotationSharpness <= 0f ? 1f : 1f - Mathf.Exp(-rotationSharpness * dt);
    }

    void BindGroundedWorldButtonEvents()
    {
        if (_boundFocusButton == groundedWorldButton)
            return;

        UnbindGroundedWorldButtonEvents();

        _boundFocusButton = groundedWorldButton;

        if (!_boundFocusButton)
            return;

        _boundFocusButton.onHoldStarted.AddListener(OnFocusHoldStarted);
        _boundFocusButton.onHoldCancelled.AddListener(OnFocusHoldCancelled);
    }

    void UnbindGroundedWorldButtonEvents()
    {
        if (!_boundFocusButton)
            return;

        _boundFocusButton.onHoldStarted.RemoveListener(OnFocusHoldStarted);
        _boundFocusButton.onHoldCancelled.RemoveListener(OnFocusHoldCancelled);
        _boundFocusButton = null;
    }

    void OnFocusHoldStarted()
    {
        if (state != State.Grounded ||
            _focusEntryPending)
            return;

        SeedFocusBodyPosition();

        _focusHoldActive = true;
        _focusBodyReturning = false;
        _focusSlamActive = false;
    }

    void OnFocusHoldCancelled()
    {
        if (_focusEntryPending ||
            state == State.FocusMode)
            return;

        _focusHoldActive = false;
        _focusSlamActive = false;
        _focusBodyReturning = true;
    }

    void BeginFocusSlam()
    {
        SeedFocusBodyPosition();

        // A WorldButton can complete earlier in the frame than this script's
        // LateUpdate. Guarantee the slam starts from the fully charged height
        // rather than one-frame-short of it.
        if (groundedWorldButton && groundedWorldButton.IsCompleted)
            _focusBodyOffset = Mathf.Max(_focusBodyOffset, focusHoldLiftAmount);

        _focusSlamStartOffset = _focusBodyOffset;
        _focusSlamElapsed = 0f;
        _focusSlamActive = true;
    }

    Transform ResolveFocusVisualTarget()
    {
        // Explicit reference wins, but NEVER permit the locomotion root.
        if (focusVisualBody &&
            focusVisualBody != transform &&
            (!_rb || focusVisualBody != _rb.transform))
        {
            return focusVisualBody;
        }

        // Backward-compatible fallback: the old Body reference is safe only
        // when it is genuinely a separate visual child.
        if (body &&
            body != transform &&
            (!_rb || body != _rb.transform))
        {
            return body;
        }

        return null;
    }

    void SeedFocusBodyPosition()
    {
        Transform resolved =
            ResolveFocusVisualTarget();

        if (!resolved)
        {
            _focusVisualTarget = null;
            _bodyPositionSeeded = false;
            return;
        }

        if (_bodyPositionSeeded &&
            _focusVisualTarget == resolved)
            return;

        _focusVisualTarget = resolved;
        _bodyAuthoredLocalPosition =
            _focusVisualTarget.localPosition;

        _bodyPositionSeeded = true;
        _focusBodyOffset = 0f;
    }

    void RestoreFocusBodyImmediately()
    {
        if (_focusVisualTarget &&
            _bodyPositionSeeded)
        {
            _focusVisualTarget.localPosition =
                _bodyAuthoredLocalPosition;
        }

        _focusHoldActive = false;
        _focusBodyReturning = false;
        _focusSlamActive = false;
        _focusBodyOffset = 0f;
    }

    void UpdateFocusBodyAnimation(float dt)
    {
        SeedFocusBodyPosition();

        if (!_focusVisualTarget ||
            !_bodyPositionSeeded)
            return;

        if (_focusSlamActive)
        {
            _focusSlamElapsed += dt;

            float slamDuration =
                Mathf.Max(0.01f, focusSlamDuration);

            float recoveryDuration =
                Mathf.Max(0.01f, focusSlamRecoveryDuration);

            if (_focusSlamElapsed <= slamDuration)
            {
                float t =
                    Mathf.Clamp01(
                        _focusSlamElapsed /
                        slamDuration);

                // Accelerates hard into the impact.
                float slamT = t * t * t;

                _focusBodyOffset =
                    Mathf.LerpUnclamped(
                        _focusSlamStartOffset,
                        -focusSlamDownAmount,
                        slamT);
            }
            else
            {
                float t =
                    Mathf.Clamp01(
                        (_focusSlamElapsed - slamDuration) /
                        recoveryDuration);

                float smooth =
                    t * t * (3f - 2f * t);

                _focusBodyOffset =
                    Mathf.LerpUnclamped(
                        -focusSlamDownAmount,
                        0f,
                        smooth);

                if (t >= 1f)
                {
                    _focusSlamActive = false;
                    _focusBodyOffset = 0f;
                }
            }
        }
        else if (_focusHoldActive &&
                 groundedWorldButton &&
                 groundedWorldButton.IsHolding)
        {
            float progress =
                Mathf.Clamp01(
                    groundedWorldButton.HoldProgress);

            float liftT =
                Mathf.Pow(
                    progress,
                    Mathf.Max(
                        0.1f,
                        focusHoldLiftPower));

            _focusBodyOffset =
                focusHoldLiftAmount *
                liftT;
        }
        else if (_focusBodyReturning ||
                 Mathf.Abs(_focusBodyOffset) > 0.0001f)
        {
            float response =
                1f -
                Mathf.Exp(
                    -Mathf.Max(
                        0.01f,
                        focusHoldCancelReturnSpeed) *
                    dt);

            _focusBodyOffset =
                Mathf.Lerp(
                    _focusBodyOffset,
                    0f,
                    response);

            if (Mathf.Abs(_focusBodyOffset) < 0.0005f)
            {
                _focusBodyOffset = 0f;
                _focusBodyReturning = false;
            }
        }

        // Only the VISUAL child moves. Locomotion/navmesh root remains untouched.
        Vector3 outwardWorld =
            (state == State.Grounded ||
             state == State.FocusMode ||
             _focusEntryPending)
                ? surfaceNormal
                : transform.up;

        if (outwardWorld.sqrMagnitude < 0.000001f)
            outwardWorld = transform.up;

        outwardWorld.Normalize();

        Transform parent =
            _focusVisualTarget.parent;

        Vector3 localOffset =
            parent
                ? parent.InverseTransformVector(
                    outwardWorld *
                    _focusBodyOffset)
                : outwardWorld *
                  _focusBodyOffset;

        _focusVisualTarget.localPosition =
            _bodyAuthoredLocalPosition +
            localOffset;
    }

    /// <summary>
    /// Grounded owns the interaction prompt. Only change visibility when needed;
    /// repeatedly calling WorldButton.Show() would reset an active hold.
    /// </summary>
    void RefreshGroundedWorldButton()
    {
        BindGroundedWorldButtonEvents();

        if (!groundedWorldButton)
            return;

        bool shouldShow = state == State.Grounded;

        if (shouldShow)
        {
            bool visualMissing = groundedWorldButton.visualRoot &&
                                 !groundedWorldButton.visualRoot.activeSelf;

            if (!groundedWorldButton.enabled || visualMissing)
                groundedWorldButton.Show();
        }
        else
        {
            bool visualStillVisible = groundedWorldButton.visualRoot &&
                                      groundedWorldButton.visualRoot.activeSelf;

            if (groundedWorldButton.enabled || visualStillVisible)
                groundedWorldButton.Hide();
        }
    }

    /// <summary>
    /// Push a mode name to every rig. Rigs that have no mode by that name are
    /// left alone rather than warned about -- a holder and a camera will often
    /// share these names but not always, and a missing one is a valid setup.
    /// </summary>
    void SetCameraMode(string modeName)
    {
        if (string.IsNullOrEmpty(modeName) || cameraRigs == null) return;

        for (int i = 0; i < cameraRigs.Length; i++)
        {
            if (cameraRigs[i] != null)
                cameraRigs[i].SetMode(modeName);
        }
    }

    // -----------------------------------------------------------------------
    // Input (new Input System: this project has activeInputHandler = 1, so the
    // legacy Input class throws at runtime)
    // -----------------------------------------------------------------------

    static bool ReadFocusExitPressed()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null &&
               Keyboard.current.escapeKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Escape);
#else
        return false;
#endif
    }

    static Vector2 ReadMove()
    {
#if ENABLE_INPUT_SYSTEM
        Vector2 value = Vector2.zero;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed) value.y += 1f;
            if (keyboard.sKey.isPressed) value.y -= 1f;
            if (keyboard.dKey.isPressed) value.x += 1f;
            if (keyboard.aKey.isPressed) value.x -= 1f;
        }

        if (Gamepad.current != null)
            value += Gamepad.current.leftStick.ReadValue();

        return Vector2.ClampMagnitude(value, 1f);
#else
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
#endif
    }

    static bool ReadJumpPressed()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            return true;

        return Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
#endif
    }
}

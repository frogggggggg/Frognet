using UnityEngine;
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
    public enum State { Flying, Grounded }

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

    [Header("Speeds")]
    public float flySpeed = 12f;
    public float walkSpeed = 4f;
    public float jumpForce = 8f;

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
    }

    void Update()
    {
        if (!_camera && Camera.main) _camera = Camera.main.transform;

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

        Vector3 wish = tangentF * input.y + tangentR * input.x;
        if (wish.sqrMagnitude > 1f) wish.Normalize();

        // Step in world units so walkSpeed stays metres per second whatever the
        // cell is scaled to, then convert into graph space to query.
        Vector3 desired = worldPos + wish * (walkSpeed * dt);

        _constraint.graphMask = _graphMask;
        NNInfo nearest = AstarPath.active.GetNearest(WorldToGraph(desired), _constraint);

        if (nearest.node == null)
        {
            Launch(transform.forward * walkSpeed);
            return;
        }

        _graphPosition = nearest.position;

        Vector3 smoothedPosition = SmoothSurface(cellSpace, nearest.node,
                                                 nearest.position, up,
                                                 out Vector3 smoothedNormal);
        surfaceNormal = smoothedNormal;

        // Falls through a chain rather than bailing out. Leaving the target
        // untouched when the heading degenerated is why the virus sometimes
        // stopped facing the ground: the stored forward can drift parallel to
        // the normal as the surface curves, and projecting it then collapses
        // to zero. The last fallback cannot fail.
        Vector3 heading = wish.sqrMagnitude > 1e-4f
            ? wish
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
    // State changes
    // -----------------------------------------------------------------------

    void Launch(Vector3 velocity)
    {
        state = State.Flying;
        _jumpQueued = false;

        // Release the cell. Local-space graphs all sit at the origin, so a
        // stale binding would convert the next query through the wrong cell.
        cell = null;
        cellSpace = null;
        _graphMask = GraphMask.everything;

        _rb.isKinematic = false;
        _rb.linearVelocity = velocity;

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

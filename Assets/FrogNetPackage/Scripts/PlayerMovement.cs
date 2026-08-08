using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using PurrNet.Prediction;

[RequireComponent(typeof(Rigidbody), typeof(PredictedRigidbody))]
public sealed class PlayerMovement
    : PredictedIdentity<PlayerMovement.Input, PlayerMovement.State>
{
    [Header("References")]
    [SerializeField] private Rigidbody rb;

    [Tooltip("A child beneath the PredictedTransform Graphics object.")]
    [SerializeField] private Transform rotationPivot;

    [Header("Movement")]
    [SerializeField, Min(0f)] private float maxSpeed = 5f;
    [SerializeField, Min(0f)] private float sprintMultiplier = 2f;
    [SerializeField, Min(0f)] private float jumpForce = 10f;

    [Tooltip("Units per second squared. Set to 0 for instant acceleration.")]
    [SerializeField, Min(0f)] private float acceleration = 30f;

    [Tooltip("Units per second squared. Set to 0 for instant stopping.")]
    [SerializeField, Min(0f)] private float deceleration = 40f;

    [Tooltip("Degrees per second. Set to 0 for instant rotation.")]
    [SerializeField, Min(0f)] private float rotationSpeed = 720f;

    [Header("Ground")]
    [SerializeField] private LayerMask groundMask = ~0;

    [Tooltip("Radius of the probe swept below the collider. Keep it just under the collider radius.")]
    [SerializeField, Min(0.01f)] private float groundProbeRadius = 0.25f;

    [Tooltip("How far below the collider still counts as standing on something.")]
    [SerializeField, Min(0f)] private float groundProbeDistance = 0.15f;

    [Tooltip("Surfaces steeper than this are walls, not ground.")]
    [SerializeField, Range(0f, 89f)] private float maxSlopeAngle = 50f;

    private static readonly RaycastHit[] groundHits = new RaycastHit[8];

    [SerializeField, HideInInspector] private Collider bodyCollider;

    private Collider BodyCollider
    {
        get
        {
            if (bodyCollider == null)
                bodyCollider = GetComponent<Collider>();

            return bodyCollider;
        }
    }

    private Rigidbody Body
    {
        get
        {
            if (rb == null)
                rb = GetComponent<Rigidbody>();

            return rb;
        }
    }

    private void Awake()
    {
        Rigidbody body = Body;

        // The physics/prediction root should never rotate.
        body.constraints |=
            RigidbodyConstraints.FreezeRotationX |
            RigidbodyConstraints.FreezeRotationY |
            RigidbodyConstraints.FreezeRotationZ;
    }

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
        bodyCollider = GetComponent<Collider>();

        if (rb != null)
        {
            rb.constraints |=
                RigidbodyConstraints.FreezeRotationX |
                RigidbodyConstraints.FreezeRotationY |
                RigidbodyConstraints.FreezeRotationZ;
        }
    }

    protected override State GetInitialState()
    {
        float initialYaw = rotationPivot != null
            ? rotationPivot.localEulerAngles.y
            : 0f;

        return new State
        {
            yaw = initialYaw
        };
    }

    protected override void GetUnityState(ref State state)
    {
        /*
         * Do not read rotationPivot here.
         *
         * rotationPivot represents the interpolated visual state, not the
         * simulation state. Reading it here would feed visual interpolation
         * back into prediction.
         */
    }

    protected override void SetUnityState(State state)
    {
        /*
         * The pivot is presentation-only and is applied in UpdateView.
         * There is no Unity simulation component to restore for yaw.
         */
    }

    protected override void GetFinalInput(ref Input input)
    {
        input.movement = InputSystem.actions["movement"].ReadValue<Vector2>();
        input.jump = InputSystem.actions["jump"].IsPressed();
        input.sprint = InputSystem.actions["sprint"].IsPressed();

        input.cameraYaw = PlayerCamera.Instance != null
            ? PlayerCamera.Instance.yRotation
            : 0f;
    }

    protected override void SanitizeInput(ref Input input)
    {
        if (!IsFinite(input.movement))
            input.movement = Vector2.zero;

        input.movement =
            Vector2.ClampMagnitude(input.movement, 1f);

        if (!IsFinite(input.cameraYaw))
            input.cameraYaw = 0f;
    }

    protected override void Simulate(
        Input input,
        ref State state,
        float delta)
    {
        Vector3 localDirection = new Vector3(
            input.movement.x,
            0f,
            input.movement.y);

        Quaternion cameraRotation =
            Quaternion.Euler(0f, input.cameraYaw, 0f);

        Vector3 worldDirection =
            cameraRotation * localDirection;

        float sprint = input.sprint ? sprintMultiplier : 1f;

        UpdateVelocity(Body, worldDirection*sprint, delta);
        UpdateFacing(worldDirection, ref state, delta);
        Jump(Body, input.jump);
    }

    private void Jump(Rigidbody body, bool jump)
    {
        if(!jump || !IsGrounded()) return;
        body.AddForce(
            jumpForce*Vector3.up,
            ForceMode.VelocityChange);
    }

    /// <summary>
    /// Sweeps a sphere down through the collider and reports whether it lands on something flat
    /// enough to stand on.
    ///
    /// The query goes through the GameObject's own PhysicsScene rather than the global Physics
    /// class. Prediction re-simulates in the scene the object lives in, and a global query would
    /// read whatever the default scene happens to hold during a resimulation.
    /// </summary>
    public bool IsGrounded()
    {
        Collider self = BodyCollider;

        if (self == null)
            return false;

        Bounds bounds = self.bounds;

        // Start at the collider's own centre height so the sweep cannot begin already overlapping
        // the floor, which a sphere cast reports as no hit at all.
        Vector3 origin = new Vector3(
            bounds.center.x,
            bounds.center.y,
            bounds.center.z);

        float distance =
            bounds.extents.y - groundProbeRadius + groundProbeDistance;

        if (distance <= 0f)
            return false;

        PhysicsScene scene = gameObject.scene.GetPhysicsScene();

        int count = scene.SphereCast(
            origin,
            groundProbeRadius,
            Vector3.down,
            groundHits,
            distance,
            groundMask,
            QueryTriggerInteraction.Ignore);

        float minimumUp = Mathf.Cos(maxSlopeAngle * Mathf.Deg2Rad);

        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = groundHits[i];

            if (hit.collider == null)
                continue;

            // Ignore this player's own colliders.
            if (hit.collider.attachedRigidbody == Body)
                continue;

            // A zero distance means the sweep started inside that collider, so its normal is junk.
            if (hit.distance <= 0f)
                continue;

            if (hit.normal.y >= minimumUp)
                return true;
        }

        return false;
    }

    private void UpdateVelocity(
        Rigidbody body,
        Vector3 direction,
        float delta)
    {
        Vector3 currentPlanarVelocity = new Vector3(
            body.linearVelocity.x,
            0f,
            body.linearVelocity.z);

        Vector3 targetPlanarVelocity =
            direction * maxSpeed;

        float changeRate =
            direction.sqrMagnitude > 0.0001f
                ? acceleration
                : deceleration;

        Vector3 nextPlanarVelocity;

        if (changeRate <= 0f)
        {
            nextPlanarVelocity = targetPlanarVelocity;
        }
        else
        {
            nextPlanarVelocity = Vector3.MoveTowards(
                currentPlanarVelocity,
                targetPlanarVelocity,
                changeRate * delta);
        }

        Vector3 velocityChange =
            nextPlanarVelocity - currentPlanarVelocity;

        body.AddForce(
            velocityChange,
            ForceMode.VelocityChange);
    }

    private void UpdateFacing(
        Vector3 direction,
        ref State state,
        float delta)
    {
        if (direction.sqrMagnitude <= 0.0001f)
            return;

        float targetYaw =
            Mathf.Atan2(direction.x, direction.z) *
            Mathf.Rad2Deg;

        float yawDifference =
            Mathf.DeltaAngle(state.yaw, targetYaw);

        if (rotationSpeed <= 0f)
        {
            // Add the shortest difference instead of assigning targetYaw.
            // This prevents 359 -> 0 interpolation from rotating backward.
            state.yaw += yawDifference;
        }
        else
        {
            float maximumChange =
                rotationSpeed * delta;

            state.yaw += Mathf.Clamp(
                yawDifference,
                -maximumChange,
                maximumChange);
        }
    }

    protected override void UpdateView(
        State viewState,
        State? verified)
    {
        if (rotationPivot == null)
            return;

        // Only the visual pivot rotates.
        // The physics root and PredictionGraphics remain unrotated.
        rotationPivot.localRotation =
            Quaternion.Euler(0f, viewState.yaw, 0f);
    }

    private static bool IsFinite(Vector2 value)
    {
        return
            IsFinite(value.x) &&
            IsFinite(value.y);
    }

    private static bool IsFinite(float value)
    {
        return
            !float.IsNaN(value) &&
            !float.IsInfinity(value);
    }

    public struct State : IPredictedData<State>
    {
        /*
         * This is intentionally allowed to exceed 360 degrees.
         * Keeping it continuous prevents interpolation problems around
         * the 0/360 boundary.
         */
        public float yaw;

        public void Dispose()
        {
        }
    }

    public struct Input : IPredictedData
    {
        public Vector2 movement;
        public float cameraYaw;
        public bool jump;
        public bool sprint;
        public void Dispose()
        {
        }
    }
}
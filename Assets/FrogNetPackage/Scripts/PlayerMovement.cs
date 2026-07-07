using UnityEngine;
using UnityEngine.InputSystem;
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

    [Tooltip("Units per second squared. Set to 0 for instant acceleration.")]
    [SerializeField, Min(0f)] private float acceleration = 30f;

    [Tooltip("Units per second squared. Set to 0 for instant stopping.")]
    [SerializeField, Min(0f)] private float deceleration = 40f;

    [Tooltip("Degrees per second. Set to 0 for instant rotation.")]
    [SerializeField, Min(0f)] private float rotationSpeed = 720f;

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
        input.jump = InputSystem.actions["jump"].triggered;

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

        UpdateVelocity(Body, worldDirection, delta);
        UpdateFacing(worldDirection, ref state, delta);
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
        public void Dispose()
        {
        }
    }
}
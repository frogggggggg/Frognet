using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using PurrNet.Prediction;

/// <summary>
/// A predicted mover driven entirely by an inspector-authored list of bindings.
/// Player, vehicle, boat and turret are all the same component with different lists.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(PredictedRigidbody))]
public sealed class Movement : PredictedIdentity<Movement.Input, Movement.State>
{
    // Bit index into Input.buttons / State.boostLatch is the binding's list index.
    public const int MaxBindings = 32;

    // Distinct Vector2 actions referenced by the binding list.
    public const int MaxAxisSlots = 4;

    public enum BindingType
    {
        Linear,
        Angular,
        Multiplier,
        SingleBoost
    }

    /// <summary>Which scalar to pull out of the bound action.</summary>
    public enum BindComponent
    {
        Pressed,
        Up,
        Down,
        Left,
        Right,
        Horizontal,
        Vertical
    }

    public enum DirectionSpace
    {
        World,
        Camera,
        Self
    }

    public enum RotationTarget
    {
        VisualPivot,
        Rigidbody
    }

    [Serializable]
    public sealed class Binding
    {
        [Tooltip("Label only. Has no effect on simulation.")]
        public string name = "New Binding";

        public BindingType type = BindingType.Linear;

        [Tooltip("Action path, as map/action.")]
        public string action = "";

        [Tooltip("Pressed treats the action as a button. The others read one direction out of a Vector2 action.")]
        public BindComponent component = BindComponent.Pressed;

        [Tooltip("Linear: direction of travel. Angular: rotation axis. SingleBoost: impulse direction.")]
        public Vector3 direction = Vector3.forward;

        public DirectionSpace space = DirectionSpace.Camera;

        [Tooltip("Units per second for Linear, degrees per second for Angular.")]
        [Min(0f)] public float maxSpeed = 5f;

        [Tooltip("Set to 0 for instant acceleration.")]
        [Min(0f)] public float acceleration = 30f;

        [Tooltip("Set to 0 for instant stopping.")]
        [Min(0f)] public float deceleration = 40f;

        [Tooltip("Scales the speed of every Linear and Angular binding while held.")]
        public float multiplier = 2f;

        [Tooltip("Instant velocity change applied once per press.")]
        public float force = 10f;

        public bool requireGrounded = true;

        public bool IsAnalog => component != BindComponent.Pressed;
    }

    [Header("References")]
    [SerializeField] private Rigidbody rb;
    [SerializeField] private Collider bodyCollider;

    [Tooltip("A child beneath the PredictedTransform Graphics object. Used when rotation target is Visual Pivot.")]
    [SerializeField] private Transform rotationPivot;

    [Header("Rotation")]
    [Tooltip("Visual Pivot rotates graphics only and keeps the collider unrotated. Rigidbody physically turns the body, which vehicles need.")]
    [SerializeField] private RotationTarget rotationTarget = RotationTarget.VisualPivot;

    [Header("Bindings")]
    [SerializeField] private List<Binding> bindings = new List<Binding>();

    [Header("Ground")]
    [SerializeField] private LayerMask groundMask = ~0;

    [Tooltip("Radius of the probe swept below the collider. Keep it just under the collider radius.")]
    [SerializeField, Min(0.01f)] private float groundProbeRadius = 0.25f;

    [Tooltip("How far below the collider still counts as standing on something.")]
    [SerializeField, Min(0f)] private float groundProbeDistance = 0.15f;

    [Tooltip("Surfaces steeper than this are walls, not ground.")]
    [SerializeField, Range(0f, 89f)] private float maxSlopeAngle = 50f;

    private readonly RaycastHit[] groundHits = new RaycastHit[8];

    // Layout is derived from the serialized binding list alone, so every peer computes the same one.
    private int[] slotOfBinding;
    private InputAction[] axisActions;
    private InputAction[] buttonActions;
    private bool layoutBuilt;

    private Rigidbody Body
    {
        get
        {
            if (rb == null)
                rb = GetComponent<Rigidbody>();

            return rb;
        }
    }

    private int BindingCount =>
        bindings == null ? 0 : Mathf.Min(bindings.Count, MaxBindings);

    private void Awake()
    {
        Rigidbody body = Body;

        if (rotationTarget == RotationTarget.VisualPivot)
        {
            // Graphics carry the facing, so the simulated body must never spin.
            body.constraints |=
                RigidbodyConstraints.FreezeRotationX |
                RigidbodyConstraints.FreezeRotationY |
                RigidbodyConstraints.FreezeRotationZ;
        }

        if (bindings != null && bindings.Count > MaxBindings)
        {
            Debug.LogError(
                $"{name}: {bindings.Count} bindings exceeds the limit of {MaxBindings}. The extras are ignored.",
                this);
        }

        BuildLayout();
    }

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
        bodyCollider = GetComponent<Collider>();
    }

    private void BuildLayout()
    {
        if (layoutBuilt)
            return;

        layoutBuilt = true;

        int count = BindingCount;
        slotOfBinding = new int[count];

        var axisNames = new List<string>(MaxAxisSlots);

        for (int i = 0; i < count; i++)
        {
            Binding binding = bindings[i];

            if (!binding.IsAnalog || string.IsNullOrEmpty(binding.action))
            {
                slotOfBinding[i] = -1;
                continue;
            }

            int slot = axisNames.IndexOf(binding.action);

            if (slot < 0)
            {
                if (axisNames.Count >= MaxAxisSlots)
                {
                    Debug.LogError(
                        $"{name}: binding '{binding.name}' needs a {MaxAxisSlots + 1}th distinct Vector2 action. It will read as zero.",
                        this);

                    slotOfBinding[i] = -1;
                    continue;
                }

                axisNames.Add(binding.action);
                slot = axisNames.Count - 1;
            }

            slotOfBinding[i] = slot;
        }

        InputActionAsset asset = InputSystem.actions;

        axisActions = new InputAction[axisNames.Count];
        buttonActions = new InputAction[count];

        if (asset == null)
            return;

        for (int i = 0; i < axisNames.Count; i++)
            axisActions[i] = asset.FindAction(axisNames[i]);

        for (int i = 0; i < count; i++)
        {
            Binding binding = bindings[i];

            if (!binding.IsAnalog && !string.IsNullOrEmpty(binding.action))
                buttonActions[i] = asset.FindAction(binding.action);
        }
    }

    protected override State GetInitialState()
    {
        Vector3 initialAngles = rotationTarget == RotationTarget.VisualPivot
            ? (rotationPivot != null ? rotationPivot.localEulerAngles : Vector3.zero)
            : transform.eulerAngles;

        return new State
        {
            angles = initialAngles
        };
    }

    protected override void GetUnityState(ref State state)
    {
        /*
         * Nothing is read back from Unity. The pivot holds interpolated visuals, and the
         * rigidbody rotation is written from `angles` rather than the other way round.
         */
    }

    protected override void SetUnityState(State state)
    {
    }

    protected override void GetFinalInput(ref Input input)
    {
        if (keybind.disableMovement)
            return;

        BuildLayout();

        for (int slot = 0; slot < axisActions.Length; slot++)
        {
            Vector2 value = axisActions[slot] != null
                ? axisActions[slot].ReadValue<Vector2>()
                : Vector2.zero;

            SetAxisSlot(ref input, slot, value);
        }

        uint buttons = 0u;

        for (int i = 0; i < buttonActions.Length; i++)
        {
            if (buttonActions[i] != null && buttonActions[i].IsPressed())
                buttons |= 1u << i;
        }

        input.buttons = buttons;

        input.cameraYaw = PlayerCamera.Instance != null
            ? PlayerCamera.Instance.yRotation
            : 0f;
    }

    protected override void SanitizeInput(ref Input input)
    {
        for (int slot = 0; slot < MaxAxisSlots; slot++)
        {
            Vector2 value = AxisSlot(input, slot);

            if (!IsFinite(value))
                value = Vector2.zero;

            SetAxisSlot(ref input, slot, Vector2.ClampMagnitude(value, 1f));
        }

        if (!IsFinite(input.cameraYaw))
            input.cameraYaw = 0f;
    }

    protected override void Simulate(Input input, ref State state, float delta)
    {
        BuildLayout();

        int count = BindingCount;

        if (count == 0)
            return;

        float speedScale = 1f;

        for (int i = 0; i < count; i++)
        {
            Binding binding = bindings[i];

            if (binding.type != BindingType.Multiplier)
                continue;

            float value = Mathf.Clamp01(Mathf.Abs(ReadBinding(binding, i, input)));
            speedScale *= Mathf.LerpUnclamped(1f, binding.multiplier, value);
        }

        for (int i = 0; i < count; i++)
        {
            Binding binding = bindings[i];
            float value = ReadBinding(binding, i, input);

            switch (binding.type)
            {
                case BindingType.Linear:
                    ApplyLinear(binding, value * speedScale, ref state, input.cameraYaw, delta);
                    break;

                case BindingType.Angular:
                    ApplyAngular(binding, value * speedScale, ref state, delta);
                    break;

                case BindingType.SingleBoost:
                    ApplyBoost(binding, i, value, ref state, input.cameraYaw);
                    break;
            }
        }

        state.angles += state.angularRate * delta;

        if (rotationTarget == RotationTarget.Rigidbody)
            Body.MoveRotation(Quaternion.Euler(state.angles));
    }

    private float ReadBinding(Binding binding, int index, Input input)
    {
        if (!binding.IsAnalog)
            return (input.buttons & (1u << index)) != 0u ? 1f : 0f;

        Vector2 raw = AxisSlot(input, slotOfBinding[index]);

        return binding.component switch
        {
            BindComponent.Up => Mathf.Max(0f, raw.y),
            BindComponent.Down => Mathf.Max(0f, -raw.y),
            BindComponent.Left => Mathf.Max(0f, -raw.x),
            BindComponent.Right => Mathf.Max(0f, raw.x),
            BindComponent.Horizontal => raw.x,
            BindComponent.Vertical => raw.y,
            _ => 0f
        };
    }

    private static Vector2 AxisSlot(Input input, int slot)
    {
        return slot switch
        {
            0 => input.axis0,
            1 => input.axis1,
            2 => input.axis2,
            3 => input.axis3,
            _ => Vector2.zero
        };
    }

    private static void SetAxisSlot(ref Input input, int slot, Vector2 value)
    {
        switch (slot)
        {
            case 0: input.axis0 = value; break;
            case 1: input.axis1 = value; break;
            case 2: input.axis2 = value; break;
            case 3: input.axis3 = value; break;
        }
    }

    private Vector3 ResolveDirection(Binding binding, State state, float cameraYaw)
    {
        Vector3 direction = binding.direction;

        if (direction.sqrMagnitude <= 0.0001f)
            return Vector3.zero;

        direction.Normalize();

        return binding.space switch
        {
            DirectionSpace.Camera => Quaternion.Euler(0f, cameraYaw, 0f) * direction,
            DirectionSpace.Self => Quaternion.Euler(state.angles) * direction,
            _ => direction
        };
    }

    private void ApplyLinear(Binding binding, float value, ref State state, float cameraYaw, float delta)
    {
        Vector3 axis = ResolveDirection(binding, state, cameraYaw);

        if (axis == Vector3.zero)
            return;

        Rigidbody body = Body;

        float current = Vector3.Dot(body.linearVelocity, axis);
        float target = value * binding.maxSpeed;
        float changeRate = Mathf.Abs(value) > 0.0001f ? binding.acceleration : binding.deceleration;

        float next = changeRate <= 0f
            ? target
            : Mathf.MoveTowards(current, target, changeRate * delta);

        body.AddForce(axis * (next - current), ForceMode.VelocityChange);
    }

    private static void ApplyAngular(Binding binding, float value, ref State state, float delta)
    {
        Vector3 axis = binding.direction;

        if (axis.sqrMagnitude <= 0.0001f)
            return;

        axis.Normalize();

        float current = Vector3.Dot(state.angularRate, axis);
        float target = value * binding.maxSpeed;
        float changeRate = Mathf.Abs(value) > 0.0001f ? binding.acceleration : binding.deceleration;

        float next = changeRate <= 0f
            ? target
            : Mathf.MoveTowards(current, target, changeRate * delta);

        state.angularRate += axis * (next - current);
    }

    private void ApplyBoost(Binding binding, int index, float value, ref State state, float cameraYaw)
    {
        uint bit = 1u << index;
        bool held = Mathf.Abs(value) > 0.5f;

        if (!held)
        {
            state.boostLatch &= ~bit;
            return;
        }

        if ((state.boostLatch & bit) != 0u)
            return;

        if (binding.requireGrounded && !IsGrounded())
            return;

        Vector3 axis = ResolveDirection(binding, state, cameraYaw);

        if (axis == Vector3.zero)
            return;

        state.boostLatch |= bit;
        Body.AddForce(axis * binding.force, ForceMode.VelocityChange);
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
        Collider self = bodyCollider;

        if (self == null)
            return false;

        Bounds bounds = self.bounds;
        float distance = bounds.extents.y - groundProbeRadius + groundProbeDistance;

        if (distance <= 0f)
            return false;

        PhysicsScene scene = gameObject.scene.GetPhysicsScene();

        int count = scene.SphereCast(
            bounds.center,
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

    protected override void UpdateView(State viewState, State? verified)
    {
        if (rotationTarget != RotationTarget.VisualPivot || rotationPivot == null)
            return;

        rotationPivot.localRotation = Quaternion.Euler(viewState.angles);
    }

    private static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public struct State : IPredictedData<State>
    {
        /*
         * Allowed to exceed 360 degrees on purpose. Keeping the value continuous stops
         * interpolation from unwinding backwards across the 0/360 boundary.
         */
        public Vector3 angles;

        public Vector3 angularRate;

        /// <summary>One bit per SingleBoost binding, marking that its impulse already fired for the current press.</summary>
        public uint boostLatch;

        public void Dispose()
        {
        }
    }

    public struct Input : IPredictedData
    {
        public Vector2 axis0;
        public Vector2 axis1;
        public Vector2 axis2;
        public Vector2 axis3;

        public float cameraYaw;

        /// <summary>One bit per binding, indexed by position in the bindings list.</summary>
        public uint buttons;

        public void Dispose()
        {
        }
    }
}

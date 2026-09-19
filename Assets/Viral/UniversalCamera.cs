using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Drives a transform through an ordered list of composable behaviours.
/// Attach it to a camera holder, a camera, or both -- nothing in here assumes
/// which one it is sitting on.
///
/// Each behaviour reads and writes a shared <see cref="CameraFrame"/> rather
/// than touching the transform, so they stack instead of fighting each other.
/// The component writes the finished frame to the transform once per tick.
///
/// Order matters, and it reads top to bottom. A typical third person camera:
///   Holder: MouseLook (yaw only) -> FollowTarget (the player)
///   Camera: MouseLook (pitch only) -> DistanceFromTarget (the holder)
/// </summary>
public class UniversalCamera : MonoBehaviour
{
    public enum Phase { LateUpdate, Update, FixedUpdate }

    /// <summary>
    /// Marks a field as rarely-needed. The custom inspector hides these behind
    /// a per-behaviour Advanced foldout so the common case stays short. Has no
    /// effect at runtime, and degrades to a normal field with no editor.
    /// </summary>
    public class AdvancedAttribute : PropertyAttribute { }

    /// <summary>World-space pose being passed down the behaviour list.</summary>
    public struct CameraFrame
    {
        public Vector3 position;
        public Quaternion rotation;

        public Vector3 Forward => rotation * Vector3.forward;
        public Vector3 Right   => rotation * Vector3.right;
        public Vector3 Up      => rotation * Vector3.up;
    }

    /// <summary>Everything a behaviour needs that is not its own settings.</summary>
    public readonly struct CameraContext
    {
        public readonly UniversalCamera Owner;
        public readonly Transform Self;

        /// <summary>Resolved target. May be null -- behaviours must cope.</summary>
        public readonly Transform Target;

        public readonly float DeltaTime;

        /// <summary>True on a tick that should ignore smoothing and snap.</summary>
        public readonly bool Snap;

        public CameraContext(UniversalCamera owner, Transform self, Transform target,
                             float deltaTime, bool snap)
        {
            Owner = owner;
            Self = self;
            Target = target;
            DeltaTime = deltaTime;
            Snap = snap;
        }
    }

    [Serializable]
    public abstract class CameraBehaviour
    {
        public bool enabled = true;

        [Advanced]
        [Tooltip("Leave empty to use the UniversalCamera's own Target.")]
        public Transform targetOverride;

        /// <summary>Called once from Awake. Override to reset runtime state.</summary>
        public virtual void Initialise(UniversalCamera owner) { }

        /// <summary>True if this behaviour cannot do anything without a target.</summary>
        public virtual bool RequiresTarget => false;

        public abstract void Apply(ref CameraFrame frame, in CameraContext ctx);
    }

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Frame-rate independent exponential smoothing weight. A plain
    /// Lerp(a, b, speed * dt) changes behaviour with framerate; this does not.
    /// Returns 1 (snap) when smoothing is off.
    /// </summary>
    public static float Damp(float smoothTime, float deltaTime, bool snap)
    {
        if (snap || smoothTime <= 0f || deltaTime <= 0f) return 1f;
        return 1f - Mathf.Exp(-deltaTime / smoothTime);
    }

    static Vector3 ApplyAxisMask(Vector3 current, Vector3 desired, bool x, bool y, bool z)
    {
        return new Vector3(x ? desired.x : current.x,
                           y ? desired.y : current.y,
                           z ? desired.z : current.z);
    }

    // -----------------------------------------------------------------------
    // Component
    // -----------------------------------------------------------------------

    [Header("Target")]
    [Tooltip("Default target for every behaviour that does not override it. " +
             "Assign the player, the camera holder, or set it at runtime.")]
    public Transform target;

    [Tooltip("Optional. If Target is empty, look for a GameObject with this tag. " +
             "Useful when the player spawns after this component wakes up.")]
    public string targetTag = "";

    [Header("Execution")]
    [Tooltip("LateUpdate is almost always right: it runs after player movement, " +
             "so the camera never trails a frame behind.")]
    public Phase phase = Phase.LateUpdate;

    [Tooltip("Behaviours run top to bottom. Rotation behaviours usually belong " +
             "above the position behaviours that depend on facing.")]
    [SerializeReference]
    public List<CameraBehaviour> behaviours = new List<CameraBehaviour>();

    bool _snapNextTick = true;      // snap on the first tick so we never lerp in from the origin
    float _nextTargetSearch;
    UniversalCamera _parentRig;
    int _lastTickFrame = -1;

    /// <summary>Set the target at runtime, e.g. once the local player spawns.</summary>
    public void SetTarget(Transform value, bool snap = true)
    {
        target = value;
        if (snap) Teleport();
    }

    /// <summary>Skip smoothing for one tick. Call after a respawn or a cut.</summary>
    public void Teleport() => _snapNextTick = true;

    void Awake()
    {
        if (transform.parent)
            _parentRig = transform.parent.GetComponentInParent<UniversalCamera>();

        for (int i = 0; i < behaviours.Count; i++)
            behaviours[i]?.Initialise(this);
    }

    void OnEnable() => Teleport();

    void Update()     { if (phase == Phase.Update)     Tick(Time.deltaTime); }
    void LateUpdate() { if (phase == Phase.LateUpdate) Tick(Time.deltaTime); }

    void FixedUpdate()
    {
        if (phase != Phase.FixedUpdate) return;

        // FixedUpdate can run several times in one rendered frame, so the
        // once-per-frame guard below must not block the later steps.
        _lastTickFrame = -1;
        Tick(Time.fixedDeltaTime);
    }

    void Tick(float deltaTime)
    {
        // Unity does not define the order in which two components of the same
        // type run. A camera rig parented to a holder rig can therefore tick
        // first and compose against the holder's *previous* rotation, landing
        // one frame behind it. That reads as jitter whenever the holder turns.
        // Pulling ancestors forward here makes the order deterministic.
        if (_lastTickFrame == Time.frameCount) return;
        _lastTickFrame = Time.frameCount;

        if (phase != Phase.FixedUpdate && _parentRig && _parentRig.isActiveAndEnabled &&
            _parentRig.phase != Phase.FixedUpdate)
        {
            _parentRig.Tick(deltaTime);
        }

        ResolveTargetIfMissing();

        var frame = new CameraFrame
        {
            position = transform.position,
            rotation = transform.rotation
        };

        bool snap = _snapNextTick;

        for (int i = 0; i < behaviours.Count; i++)
        {
            var behaviour = behaviours[i];
            if (behaviour == null || !behaviour.enabled) continue;

            Transform resolved = behaviour.targetOverride ? behaviour.targetOverride : target;

            // Skip quietly rather than throwing: a target that spawns late is
            // normal, and an exception here would stall the whole list.
            if (behaviour.RequiresTarget && !resolved) continue;

            var ctx = new CameraContext(this, transform, resolved, deltaTime, snap);
            behaviour.Apply(ref frame, in ctx);
        }

        transform.SetPositionAndRotation(frame.position, frame.rotation);
        _snapNextTick = false;
    }

    void ResolveTargetIfMissing()
    {
        if (target || string.IsNullOrEmpty(targetTag)) return;

        // Rate limited: FindWithTag every frame while a player is still
        // loading would be a needless scene walk.
        if (Time.unscaledTime < _nextTargetSearch) return;
        _nextTargetSearch = Time.unscaledTime + 0.25f;

        GameObject found = null;
        try
        {
            found = GameObject.FindWithTag(targetTag);
        }
        catch (UnityException)
        {
            // Tag is not defined in the project. Stop retrying rather than
            // throwing four times a second forever.
            Debug.LogWarning($"{nameof(UniversalCamera)} on '{name}': tag " +
                             $"'{targetTag}' is not defined. Clearing Target Tag.", this);
            targetTag = "";
            return;
        }

        if (found) SetTarget(found.transform);
    }

    // -----------------------------------------------------------------------
    // Position behaviours
    // -----------------------------------------------------------------------

    /// <summary>Move toward a target, optionally on selected axes only.</summary>
    [Serializable]
    public class FollowTarget : CameraBehaviour
    {
        public enum OffsetSpace { World, TargetLocal }

        public Vector3 offset = Vector3.zero;

        [Advanced]
        [Tooltip("TargetLocal makes the offset rotate with the target, so it " +
                 "stays behind them as they turn.")]
        public OffsetSpace offsetSpace = OffsetSpace.World;

        [Tooltip("Seconds to close most of the gap. 0 is rigid.")]
        public float smoothTime = 0.08f;

        [Advanced] public bool followX = true;
        [Advanced] public bool followY = true;
        [Advanced] public bool followZ = true;

        [NonSerialized] Vector3 _velocity;

        public override bool RequiresTarget => true;
        public override void Initialise(UniversalCamera owner) => _velocity = Vector3.zero;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            Vector3 worldOffset = offsetSpace == OffsetSpace.TargetLocal
                ? ctx.Target.TransformVector(offset)
                : offset;

            Vector3 desired = ctx.Target.position + worldOffset;
            desired = ApplyAxisMask(frame.position, desired, followX, followY, followZ);

            if (ctx.Snap || smoothTime <= 0f)
            {
                _velocity = Vector3.zero;
                frame.position = desired;
                return;
            }

            frame.position = Vector3.SmoothDamp(frame.position, desired,
                                                ref _velocity, smoothTime, Mathf.Infinity,
                                                ctx.DeltaTime);
        }
    }

    /// <summary>
    /// Nudge the pose by a fixed offset. Self space is the usual one for a
    /// camera: it shifts along the camera's own axes after it has been aimed.
    /// </summary>
    [Serializable]
    public class PositionOffset : CameraBehaviour
    {
        public enum OffsetSpace { World, Self, Parent, Target }

        public Vector3 offset = Vector3.zero;
        [Advanced] public OffsetSpace space = OffsetSpace.Self;

        public override bool RequiresTarget => space == OffsetSpace.Target;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            switch (space)
            {
                case OffsetSpace.Self:
                    frame.position += frame.rotation * offset;
                    break;
                case OffsetSpace.Parent:
                    Transform parent = ctx.Self.parent;
                    frame.position += parent ? parent.TransformVector(offset) : offset;
                    break;
                case OffsetSpace.Target:
                    frame.position += ctx.Target.TransformVector(offset);
                    break;
                default:
                    frame.position += offset;
                    break;
            }
        }
    }

    /// <summary>
    /// Sit a fixed distance from a target -- the third person boom. Put this on
    /// the camera with the holder as its target and you get a pull-back arm.
    /// Usually belongs below whatever sets rotation, since the default
    /// direction is the pose's own backward axis.
    /// </summary>
    [Serializable]
    public class DistanceFromTarget : CameraBehaviour
    {
        public enum Direction
        {
            SelfBackward,       // behind the current facing -- the usual boom
            TargetBackward,     // behind the target's facing
            CustomWorld,
            CustomTargetLocal
        }

        public float distance = 4f;

        [Tooltip("Pull the camera in when geometry sits between it and the pivot.")]
        public bool avoidCollision = false;

        [Advanced] public Direction direction = Direction.SelfBackward;

        [Advanced]
        [Tooltip("Used by the Custom direction modes. Normalised on use.")]
        public Vector3 customDirection = new Vector3(0f, 0.35f, -1f);

        [Advanced]
        [Tooltip("Offset applied to the target before measuring, e.g. to pivot " +
                 "around the head rather than the feet.")]
        public Vector3 pivotOffset = Vector3.zero;

        [Advanced] public float smoothTime = 0f;
        [Advanced] public LayerMask obstacles = ~0;
        [Advanced] public float probeRadius = 0.2f;

        [Advanced]
        [Tooltip("Keep the camera at least this far from the pivot even when " +
                 "pulled in, so it never ends up inside the target.")]
        public float minDistance = 0.4f;

        [NonSerialized] float _currentDistance = -1f;

        public override bool RequiresTarget => true;
        public override void Initialise(UniversalCamera owner) => _currentDistance = -1f;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            Vector3 pivot = ctx.Target.position + ctx.Target.TransformVector(pivotOffset);

            Vector3 dir;
            switch (direction)
            {
                case Direction.TargetBackward:    dir = -ctx.Target.forward; break;
                case Direction.CustomWorld:       dir = customDirection; break;
                case Direction.CustomTargetLocal: dir = ctx.Target.TransformDirection(customDirection); break;
                default:                          dir = -frame.Forward; break;
            }

            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : -frame.Forward;

            float wanted = distance;

            if (avoidCollision && distance > 0f)
            {
                // Sphere rather than ray: a ray slips through corners and lets
                // the near plane clip into walls.
                if (Physics.SphereCast(pivot, probeRadius, dir, out RaycastHit hit,
                                       distance, obstacles, QueryTriggerInteraction.Ignore))
                {
                    wanted = Mathf.Max(hit.distance, minDistance);
                }
            }

            // Pulling in has to be instant or the camera ends up inside the
            // wall it is avoiding; easing back out is what wants smoothing.
            if (ctx.Snap || _currentDistance < 0f || smoothTime <= 0f)
                _currentDistance = wanted;
            else if (wanted < _currentDistance)
                _currentDistance = wanted;
            else
                _currentDistance = Mathf.Lerp(_currentDistance, wanted,
                                              Damp(smoothTime, ctx.DeltaTime, ctx.Snap));

            frame.position = pivot + dir * _currentDistance;
        }
    }

    // -----------------------------------------------------------------------
    // Rotation behaviours
    // -----------------------------------------------------------------------

    /// <summary>Aim at a target.</summary>
    [Serializable]
    public class LookAtTarget : CameraBehaviour
    {
        public enum Axes { Full, YawOnly, PitchOnly }
        public enum UpSource { World, Target }

        public float smoothTime = 0.05f;

        [Advanced]
        [Tooltip("Restrict which part of the aim is taken, so yaw can come " +
                 "from here and pitch from somewhere else.")]
        public Axes axes = Axes.Full;

        [Advanced]
        [Tooltip("Point on the target to aim at, in the target's local space.")]
        public Vector3 aimOffset = Vector3.zero;

        [Advanced] public UpSource up = UpSource.World;

        public override bool RequiresTarget => true;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            Vector3 aimPoint = ctx.Target.position + ctx.Target.TransformVector(aimOffset);
            Vector3 direction = aimPoint - frame.position;

            // Degenerate when the camera sits exactly on the target. Hold the
            // current rotation rather than snapping to identity.
            if (direction.sqrMagnitude < 1e-6f) return;
            direction.Normalize();

            Vector3 upVector = up == UpSource.Target ? ctx.Target.up : Vector3.up;
            Quaternion desired;

            // Everything below stays in vector/quaternion space. Mixing axes
            // through eulerAngles is what used to jitter: that decomposition
            // is discontinuous near straight up, so a camera looking steeply
            // up would flip between two equivalent angle triples frame to
            // frame and visibly shake.
            switch (axes)
            {
                case Axes.YawOnly:
                {
                    Vector3 flat = Vector3.ProjectOnPlane(direction, upVector);
                    if (flat.sqrMagnitude < 1e-6f) return;   // target directly overhead
                    desired = Quaternion.LookRotation(flat.normalized, upVector);
                    break;
                }

                case Axes.PitchOnly:
                {
                    // Keep the heading we already have, take only elevation.
                    Vector3 heading = Vector3.ProjectOnPlane(frame.Forward, upVector);
                    if (heading.sqrMagnitude < 1e-6f) return;
                    heading.Normalize();

                    float elevation = Mathf.Clamp(Vector3.Dot(direction, upVector), -1f, 1f);
                    float horizontal = Mathf.Sqrt(Mathf.Max(0f, 1f - elevation * elevation));

                    desired = Quaternion.LookRotation(
                        heading * horizontal + upVector * elevation, upVector);
                    break;
                }

                default:
                    desired = Quaternion.LookRotation(direction, upVector);
                    break;
            }

            frame.rotation = Quaternion.Slerp(frame.rotation, desired,
                                              Damp(smoothTime, ctx.DeltaTime, ctx.Snap));
        }
    }

    /// <summary>
    /// Mouse / stick look. Split it across two components for a standard rig:
    /// yaw on the holder, pitch on the camera. The camera's Parent space then
    /// inherits the holder's yaw automatically.
    /// </summary>
    [Serializable]
    public class MouseLook : CameraBehaviour
    {
        public enum Basis { Parent, World }

        public Vector2 sensitivity = new Vector2(0.12f, 0.12f);

        public bool yaw = true;
        public bool pitch = true;

        [Tooltip("Min and max pitch in degrees. Only applied when Pitch is on.")]
        public Vector2 pitchClamp = new Vector2(-60f, 70f);

        [Advanced] public bool invertY = false;

        [Advanced]
        [Tooltip("Parent composes on top of the parent's rotation, which is what " +
                 "lets a pitch-only camera sit under a yaw-only holder.")]
        public Basis basis = Basis.Parent;

        [Advanced]
        [Tooltip("Seconds of rotation smoothing. 0 is raw, which most players prefer.")]
        public float smoothTime = 0f;

        [Advanced]
        [Tooltip("Ignore input unless the hardware cursor is locked.")]
        public bool requireCursorLock = true;

        [Advanced]
        [Tooltip("Gamepad stick speed before sensitivity, in units per second. " +
                 "Has no effect on mouse input.")]
        public float stickSpeed = 1000f;

        [NonSerialized] float _yaw;
        [NonSerialized] float _pitch;
        [NonSerialized] bool _initialised;

        public override void Initialise(UniversalCamera owner)
        {
            // Start from the transform's current angles so enabling this does
            // not yank the view to zero. Which angles depends on the basis the
            // behaviour composes against.
            Vector3 e = basis == Basis.World
                ? owner.transform.eulerAngles
                : owner.transform.localEulerAngles;
            _pitch = Mathf.DeltaAngle(0f, e.x);
            _yaw = Mathf.DeltaAngle(0f, e.y);
            _initialised = true;
        }

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            if (!_initialised) Initialise(ctx.Owner);

            if (!requireCursorLock || Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 delta = ReadLookDelta(ctx.DeltaTime, stickSpeed);
                if (yaw) _yaw += delta.x * sensitivity.x;
                if (pitch) _pitch += (invertY ? delta.y : -delta.y) * sensitivity.y;
            }

            _yaw = Mathf.Repeat(_yaw, 360f);
            if (pitch) _pitch = Mathf.Clamp(_pitch, pitchClamp.x, pitchClamp.y);

            Quaternion basisRotation = Quaternion.identity;
            if (basis == Basis.Parent && ctx.Self.parent)
                basisRotation = ctx.Self.parent.rotation;

            Quaternion desired = basisRotation *
                                 Quaternion.Euler(pitch ? _pitch : 0f, yaw ? _yaw : 0f, 0f);

            frame.rotation = Quaternion.Slerp(frame.rotation, desired,
                                              Damp(smoothTime, ctx.DeltaTime, ctx.Snap));
        }
    }

    /// <summary>
    /// Unclamped look that turns about its own axes. Pitch up, then yaw, and
    /// the yaw happens around the up vector that pitch just produced -- so you
    /// can walk the view through every orientation instead of stopping at the
    /// poles.
    ///
    /// It accumulates a quaternion rather than euler angles, which is what
    /// removes the clamp: there is no pitch number to pin at 90 degrees and no
    /// gimbal lock to fall into. Roll accumulates naturally as a consequence of
    /// combining pitch and yaw, exactly as it does when you turn your head.
    /// Use MouseLook instead when you want a level horizon.
    /// </summary>
    [Serializable]
    public class FreeLook : CameraBehaviour
    {
        public Vector2 sensitivity = new Vector2(0.12f, 0.12f);

        [Advanced] public bool invertY = false;

        [Advanced]
        [Tooltip("Constant roll in degrees per second. Leave at 0 for none.")]
        public float rollPerSecond = 0f;

        [Advanced] public float smoothTime = 0f;
        [Advanced] public bool requireCursorLock = true;

        [Advanced]
        [Tooltip("Gamepad stick speed before sensitivity. No effect on mouse.")]
        public float stickSpeed = 1000f;

        [NonSerialized] Quaternion _rotation = Quaternion.identity;
        [NonSerialized] bool _initialised;

        public override void Initialise(UniversalCamera owner)
        {
            _rotation = owner.transform.rotation;
            _initialised = true;
        }

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            if (!_initialised) Initialise(ctx.Owner);

            Vector2 delta = Vector2.zero;
            if (!requireCursorLock || Cursor.lockState == CursorLockMode.Locked)
                delta = ReadLookDelta(ctx.DeltaTime, stickSpeed);

            float yaw   = delta.x * sensitivity.x;
            float pitch = (invertY ? delta.y : -delta.y) * sensitivity.y;
            float roll  = rollPerSecond * ctx.DeltaTime;

            // Post-multiply: the rotation is expressed in the frame we already
            // have, not the world. Pre-multiplying would turn about world axes
            // and reintroduce the pole.
            _rotation = _rotation * Quaternion.Euler(pitch, yaw, roll);

            // Chained multiplication drifts off unit length over a long
            // session, and an unnormalised quaternion slerps unevenly, which
            // shows up as a slow wobble.
            _rotation = Quaternion.Normalize(_rotation);

            frame.rotation = Quaternion.Slerp(frame.rotation, _rotation,
                                              Damp(smoothTime, ctx.DeltaTime, ctx.Snap));
        }
    }

    /// <summary>Spin at a constant rate. Handy for idle orbits and menu cameras.</summary>
    [Serializable]
    public class ConstantRotate : CameraBehaviour
    {
        public enum Basis { World, Self }

        public Vector3 degreesPerSecond = new Vector3(0f, 20f, 0f);
        [Advanced] public Basis basis = Basis.World;

        [NonSerialized] Vector3 _accumulated;

        public override void Initialise(UniversalCamera owner) => _accumulated = Vector3.zero;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            _accumulated += degreesPerSecond * ctx.DeltaTime;
            _accumulated = new Vector3(Mathf.Repeat(_accumulated.x, 360f),
                                       Mathf.Repeat(_accumulated.y, 360f),
                                       Mathf.Repeat(_accumulated.z, 360f));

            Quaternion spin = Quaternion.Euler(_accumulated);
            frame.rotation = basis == Basis.Self ? frame.rotation * spin : spin * frame.rotation;
        }
    }

    // -----------------------------------------------------------------------
    // Input
    // -----------------------------------------------------------------------

    /// <summary>
    /// Look delta for this frame, with both devices brought into the same units.
    ///
    /// Mouse delta is already an accumulated per-frame movement, so scaling it
    /// by deltaTime would make sensitivity drift with framerate. A stick is a
    /// held position, not a delta, so it does need deltaTime. Treating the two
    /// the same is the usual reason pad look feels wrong at a framerate other
    /// than the one it was tuned at.
    /// </summary>
    static Vector2 ReadLookDelta(float deltaTime, float stickSpeed)
    {
#if ENABLE_INPUT_SYSTEM
        Vector2 delta = Vector2.zero;

        if (Mouse.current != null)
            delta += Mouse.current.delta.ReadValue();

        if (Gamepad.current != null)
            delta += Gamepad.current.rightStick.ReadValue() * stickSpeed * deltaTime;

        return delta;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
        return Vector2.zero;
#endif
    }
}

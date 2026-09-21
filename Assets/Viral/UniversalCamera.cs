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
/// ORDER IS AUTHORITATIVE. Read the list literally from top to bottom:
/// each row receives the frame produced above it and changes only what it owns.
/// A later position/rotation row never feeds backward into an earlier row on
/// the next frame.
///
/// Example:
///   Target Position      -> choose the pivot
///   Mouse Look           -> choose the facing
///   Position Offset      -> move backward in that facing
///   Camera Collision     -> shorten only if blocked
///
/// Another example:
///   Look At Target
///   Target Position (Y only, offset -2)
/// means "aim first, then move down"; moving down does NOT make LookAt re-aim.
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

        /// <summary>
        /// Target field of view in degrees. Seeded from the Camera's own value
        /// each tick, so a mode with nothing touching it eases back to whatever
        /// the Camera was authored with.
        /// </summary>
        public float fieldOfView;

        /// <summary>
        /// Projection state passed through the behaviour stack. These are only
        /// committed to the Camera when an active behaviour declares that it
        /// writes projection, so holder rigs cannot accidentally fight the
        /// camera rig that owns projection.
        /// </summary>
        public bool orthographic;
        public float orthographicSize;

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

    /// <summary>
    /// A named set of behaviours. Exactly one is active at a time, so modes act
    /// as toggles: swap the whole rig in one call instead of enabling and
    /// disabling behaviours one by one.
    /// </summary>
    [Serializable]
    public class CameraMode
    {
        public string name = "Mode";

        [Header("Execution")]
        [Tooltip("When this mode drives the camera. LateUpdate is normally best for cameras, " +
                 "Update is useful when matching Update-driven movement, and FixedUpdate is " +
                 "available for physics-driven rigs.")]
        public Phase phase = Phase.LateUpdate;

        [Header("Cursor")]
        [Tooltip("Lock the hardware cursor to the centre while this mode is active.")]
        public bool lockCursor = true;

        [Tooltip("Hide the hardware cursor while this mode is active.")]
        public bool hideCursor = true;

        [Tooltip("Move the hardware cursor to the centre when this mode becomes active. " +
                 "A locked cursor is already centred automatically.")]
        public bool centerCursorOnEnter = true;

        [Header("Transition In")]
        [Tooltip("When entering this mode through SetMode, blend the selected camera outputs " +
                 "from their current values instead of applying the new mode immediately. " +
                 "SetMode(..., snap: true) always bypasses this transition.")]
        public bool transitionOnEnter = false;

        [Min(0f)]
        [Tooltip("Seconds taken to blend into this mode.")]
        public float transitionDuration = 0.35f;

        [Tooltip("Blend world position from the outgoing pose into this mode.")]
        public bool transitionPosition = true;

        [Tooltip("Blend world rotation from the outgoing pose into this mode.")]
        public bool transitionRotation = true;

        [Tooltip("Blend Camera field of view from its outgoing value into this mode. " +
                 "This is independent of the normal Field Of View Smoothing setting.")]
        public bool transitionFieldOfView = true;

        [Tooltip("Smoothly morph the projection matrix when changing between " +
                 "Orthographic and Perspective (or between different projection sizes).")]
        public bool transitionProjection = true;

        [Tooltip("Shape of the transition. X is normalized time and Y is blend amount.")]
        public AnimationCurve transitionCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Applied literally top to bottom. Each row changes only its own output.")]
        [SerializeReference]
        public List<CameraBehaviour> behaviours = new List<CameraBehaviour>();
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

        /// <summary>
        /// True when this behaviour intentionally changes CameraFrame.fieldOfView.
        /// Used so holder rigs that merely happen to find a child Camera do not
        /// reset that Camera's FOV and fight the rig that actually owns FOV.
        /// </summary>
        public virtual bool WritesFieldOfView => false;

        /// <summary>
        /// True when this behaviour intentionally controls Camera.orthographic
        /// and/or Camera.orthographicSize.
        /// </summary>
        public virtual bool WritesProjection => false;

        /// <summary>
        /// True when this behaviour is a final POSITION correction rather than
        /// a new upstream camera base. It still affects every behaviour BELOW
        /// it this tick, but it is removed before the next tick begins.
        /// </summary>
        public virtual bool IsPositionModifier => false;

        /// <summary>
        /// Rotation equivalent of IsPositionModifier.
        /// </summary>
        public virtual bool IsRotationModifier => false;

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
    // Simple stack vocabulary
    // -----------------------------------------------------------------------

    [Serializable]
    public struct AxisMask
    {
        public bool x;
        public bool y;
        public bool z;

        public AxisMask(bool x, bool y, bool z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static AxisMask All => new AxisMask(true, true, true);
        public static AxisMask None => new AxisMask(false, false, false);

        public bool Any => x || y || z;

        public Vector3 Filter(Vector3 value)
        {
            return new Vector3(
                x ? value.x : 0f,
                y ? value.y : 0f,
                z ? value.z : 0f);
        }

        public Vector3 Merge(Vector3 current, Vector3 desired)
        {
            return new Vector3(
                x ? desired.x : current.x,
                y ? desired.y : current.y,
                z ? desired.z : current.z);
        }
    }

    /// <summary>
    /// One common coordinate vocabulary for every spatial behaviour.
    ///
    /// World    = global XYZ.
    /// Self     = the CameraFrame produced by the behaviours above this one.
    /// Parent   = this rig's Transform parent.
    /// Target   = target's full transform.
    /// TargetUp = origin at target, Y follows target.up, but target spin around
    ///            that up axis does not drag the basis around.
    /// </summary>
    public enum ReferenceSpace
    {
        World,
        Self,
        Parent,
        Target,
        TargetUp
    }

    static Quaternion BuildTargetUpBasis(
        Transform target,
        Quaternion fallbackRotation)
    {
        if (!target)
            return fallbackRotation;

        Vector3 up =
            target.up.sqrMagnitude > 0.000001f
                ? target.up.normalized
                : Vector3.up;

        Vector3 forward =
            Vector3.ProjectOnPlane(
                fallbackRotation * Vector3.forward,
                up);

        if (forward.sqrMagnitude < 0.000001f)
        {
            forward =
                Vector3.ProjectOnPlane(
                    target.forward,
                    up);
        }

        if (forward.sqrMagnitude < 0.000001f)
        {
            Vector3 axis =
                Mathf.Abs(up.y) < 0.9f
                    ? Vector3.up
                    : Vector3.right;

            forward =
                Vector3.Cross(
                    axis,
                    up);
        }

        forward.Normalize();

        return Quaternion.LookRotation(
            forward,
            up);
    }

    static void ResolveSpace(
        in CameraFrame frame,
        in CameraContext ctx,
        ReferenceSpace space,
        out Vector3 origin,
        out Quaternion rotation)
    {
        switch (space)
        {
            case ReferenceSpace.Self:
                origin = frame.position;
                rotation = frame.rotation;
                return;

            case ReferenceSpace.Parent:
                if (ctx.Self.parent)
                {
                    origin = ctx.Self.parent.position;
                    rotation = ctx.Self.parent.rotation;
                }
                else
                {
                    origin = Vector3.zero;
                    rotation = Quaternion.identity;
                }
                return;

            case ReferenceSpace.Target:
                if (ctx.Target)
                {
                    origin = ctx.Target.position;
                    rotation = ctx.Target.rotation;
                }
                else
                {
                    origin = Vector3.zero;
                    rotation = Quaternion.identity;
                }
                return;

            case ReferenceSpace.TargetUp:
                if (ctx.Target)
                {
                    origin = ctx.Target.position;
                    rotation =
                        BuildTargetUpBasis(
                            ctx.Target,
                            frame.rotation);
                }
                else
                {
                    origin = Vector3.zero;
                    rotation = Quaternion.identity;
                }
                return;

            default:
                origin = Vector3.zero;
                rotation = Quaternion.identity;
                return;
        }
    }

    static Vector3 WorldToSpacePoint(
        Vector3 worldPoint,
        Vector3 origin,
        Quaternion rotation)
    {
        return
            Quaternion.Inverse(rotation) *
            (worldPoint - origin);
    }

    static Vector3 SpaceToWorldPoint(
        Vector3 localPoint,
        Vector3 origin,
        Quaternion rotation)
    {
        return
            origin +
            rotation *
            localPoint;
    }

    static Vector3 SignedEuler(
        Quaternion rotation)
    {
        Vector3 e =
            rotation.eulerAngles;

        return new Vector3(
            Mathf.DeltaAngle(0f, e.x),
            Mathf.DeltaAngle(0f, e.y),
            Mathf.DeltaAngle(0f, e.z));
    }

    static Quaternion MergeRotationAxes(
        Quaternion current,
        Quaternion desired,
        AxisMask axes,
        Quaternion reference)
    {
        if (axes.x && axes.y && axes.z)
            return desired;

        if (!axes.Any)
            return current;

        Quaternion inv =
            Quaternion.Inverse(
                reference);

        Vector3 currentEuler =
            SignedEuler(
                inv *
                current);

        Vector3 desiredEuler =
            SignedEuler(
                inv *
                desired);

        Vector3 merged =
            axes.Merge(
                currentEuler,
                desiredEuler);

        return
            reference *
            Quaternion.Euler(
                merged);
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

    // Kept only so scenes/prefabs made with the older component-level Phase
    // migrate cleanly. It is hidden after migration; runtime execution uses the
    // active mode's Phase instead.
    [SerializeField, HideInInspector]
    Phase phase = Phase.LateUpdate;

    [SerializeField, HideInInspector]
    bool _modePhaseMigrated;

    [Tooltip("Camera whose field of view / projection behaviours may drive. Left empty it " +
             "looks on this object, then in children -- so a rig sitting on a " +
             "holder still finds the Camera parented under it.")]
    public Camera targetCamera;

    [Tooltip("Seconds to ease toward the field of view behaviours ask for. " +
             "0 snaps.")]
    public float fieldOfViewSmoothing = 0.25f;

    [Tooltip("Exactly one mode is active at a time. Switch with SetMode from " +
             "script, or pick one here to preview it.")]
    public List<CameraMode> modes = new List<CameraMode>();

    [Tooltip("Index of the mode in use. Changing this at runtime switches rigs.")]
    public int activeMode;

    // Migration from the single-list version. Hidden, and emptied once its
    // contents have been moved into a mode.
    [SerializeReference, HideInInspector]
    List<CameraBehaviour> behaviours = new List<CameraBehaviour>();

    public CameraMode ActiveMode =>
        modes != null && activeMode >= 0 && activeMode < modes.Count ? modes[activeMode] : null;

    /// <summary>Execution phase selected by the currently active mode.</summary>
    public Phase ActivePhase => ActiveMode != null ? ActiveMode.phase : phase;

    /// <summary>
    /// Switch by index. The incoming mode owns the transition policy: if its
    /// Transition On Enter option is enabled, the selected outputs blend from
    /// the camera's current state. Passing snap=true always bypasses that blend.
    /// </summary>
    public bool SetMode(int index, bool snap = false)
    {
        if (modes == null || index < 0 || index >= modes.Count) return false;

        CameraMode mode = modes[index];

        // Calling SetMode for the already-active mode is still useful when a UI
        // or pause screen has temporarily changed the cursor state.
        if (index == activeMode)
        {
            ApplyCursorSettings(mode);
            if (snap) Teleport();
            return true;
        }

        activeMode = index;

        // A new stack begins from the camera's actual visible pose.
        _positionFeedbackSeeded = false;
        _rotationFeedbackSeeded = false;

        // Re-seed the incoming behaviours from the pose the camera is in right
        // now, so input-driven behaviours continue from the outgoing view
        // instead of resurrecting state from the last time this mode was active.
        for (int i = 0; i < mode.behaviours.Count; i++)
            mode.behaviours[i]?.Initialise(this);

        ApplyCursorSettings(mode);

        if (snap)
        {
            Teleport();
        }
        else
        {
            BeginModeTransition(mode);
        }

        return true;
    }

    /// <summary>Switch by name, ignoring case. Returns false if not found.</summary>
    public bool SetMode(string name, bool snap = false)
    {
        if (modes == null || string.IsNullOrEmpty(name)) return false;

        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i] != null &&
                string.Equals(modes[i].name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                return SetMode(i, snap);
            }
        }
        return false;
    }

    void MigrateLegacyBehaviours()
    {
        if (behaviours == null || behaviours.Count == 0) return;

        modes ??= new List<CameraMode>();
        modes.Insert(0, new CameraMode
        {
            name = "Default",
            phase = phase,
            behaviours = new List<CameraBehaviour>(behaviours)
        });

        behaviours.Clear();
    }

    /// <summary>
    /// Older versions stored Phase once on UniversalCamera. On the first load
    /// after upgrading, copy that value into every existing mode so behaviour
    /// stays unchanged. After that every mode is independent.
    /// </summary>
    void MigrateLegacyPhase()
    {
        if (_modePhaseMigrated || modes == null || modes.Count == 0) return;

        for (int i = 0; i < modes.Count; i++)
        {
            if (modes[i] != null)
                modes[i].phase = phase;
        }

        _modePhaseMigrated = true;
    }

    void RunMigrations()
    {
        MigrateLegacyBehaviours();
        MigrateLegacyPhase();
    }

    void OnValidate() => RunMigrations();

    bool _snapNextTick = true;      // snap on the first tick so we never lerp in from the origin
    float _nextTargetSearch;
    UniversalCamera _parentRig;
    int _lastTickFrame = -1;
    float _baseFieldOfView = 60f;
    bool _baseOrthographic;
    float _baseOrthographicSize = 5f;

    // Only a rig that actually contains an active projection behaviour is
    // allowed to change Camera.orthographic / orthographicSize. This mirrors
    // FOV ownership and prevents parent/child UniversalCamera rigs that resolve
    // the same Camera from fighting over its projection.
    bool _projectionWasDriven;

    // Only a rig that actually contains an active FOV behaviour is allowed to
    // drive the Camera's FOV. Without this, a holder rig and a camera rig can
    // both resolve the same child Camera: one widens FOV while the other writes
    // the base FOV back, producing the visible back-and-forth flicker.
    bool _fovWasDriven;

    // Destination-mode transition state. Start values are captured exactly when
    // SetMode is called, then selected outputs are blended toward the incoming
    // mode's live result every tick.
    bool _transitionActive;
    float _transitionElapsed;
    Vector3 _transitionStartPosition;
    Quaternion _transitionStartRotation = Quaternion.identity;
    float _transitionStartFieldOfView = 60f;
    Matrix4x4 _transitionStartProjection = Matrix4x4.identity;
    bool _projectionMatrixOverrideActive;

    // Pose immediately BEFORE the first final modifier of each channel.
    // These are fed into the next tick so a lower behaviour cannot feed its
    // correction backward into behaviours above it.
    bool _positionFeedbackSeeded;
    Vector3 _positionFeedbackPosition;

    bool _rotationFeedbackSeeded;
    Quaternion _rotationFeedbackRotation = Quaternion.identity;

    public bool IsTransitioning => _transitionActive;

    /// <summary>
    /// The Camera this rig drives, or null. Searches children as well as this
    /// object, because the usual rig puts the Camera under a holder rather
    /// than on it -- which is exactly why field of view silently did nothing
    /// when it only ever looked at its own GameObject.
    /// </summary>
    public Camera ResolveCamera()
    {
        if (targetCamera) return targetCamera;
        if (TryGetComponent(out Camera own)) return own;
        return GetComponentInChildren<Camera>();
    }

    /// <summary>Set the target at runtime, e.g. once the local player spawns.</summary>
    public void SetTarget(Transform value, bool snap = true)
    {
        target = value;
        if (snap) Teleport();
    }

    /// <summary>
    /// Skip smoothing for one tick. Call after a respawn or a cut. A teleport is
    /// intentionally stronger than a mode transition, so it cancels any blend.
    /// </summary>
    public void Teleport()
    {
        _transitionActive = false;
        _transitionElapsed = 0f;
        _positionFeedbackSeeded = false;
        _rotationFeedbackSeeded = false;
        ClearProjectionMatrixOverride();
        _snapNextTick = true;
    }

    /// <summary>Capture the outgoing state for a destination-owned transition.</summary>
    void BeginModeTransition(CameraMode mode)
    {
        if (mode == null || !mode.transitionOnEnter || mode.transitionDuration <= 0f ||
            (!mode.transitionPosition && !mode.transitionRotation &&
             !mode.transitionFieldOfView && !mode.transitionProjection))
        {
            _transitionActive = false;
            _transitionElapsed = 0f;
            return;
        }

        _transitionActive = true;
        _transitionElapsed = 0f;
        _transitionStartPosition = transform.position;
        _transitionStartRotation = transform.rotation;
        _transitionStartFieldOfView = targetCamera ? targetCamera.fieldOfView : _baseFieldOfView;

        if (targetCamera)
        {
            // projectionMatrix is the exact image currently on screen. If a
            // previous projection transition is interrupted, this captures the
            // partially-morphed matrix rather than popping back to a canonical
            // Perspective/Orthographic matrix first.
            _transitionStartProjection =
                targetCamera.projectionMatrix;
        }

        // A transition is itself the deliberate way into the new mode. Do not
        // let a queued one-frame snap defeat it.
        _snapNextTick = false;
    }

    Matrix4x4 BuildProjectionMatrix(
        bool orthographic,
        float orthographicSize,
        float fieldOfView)
    {
        if (!targetCamera)
            return Matrix4x4.identity;

        float aspect =
            Mathf.Max(
                0.0001f,
                targetCamera.aspect);

        float nearClip =
            Mathf.Max(
                0.0001f,
                targetCamera.nearClipPlane);

        float farClip =
            Mathf.Max(
                nearClip + 0.0001f,
                targetCamera.farClipPlane);

        if (orthographic)
        {
            float halfHeight =
                Mathf.Max(
                    0.0001f,
                    orthographicSize);

            float halfWidth =
                halfHeight *
                aspect;

            return Matrix4x4.Ortho(
                -halfWidth,
                halfWidth,
                -halfHeight,
                halfHeight,
                nearClip,
                farClip);
        }

        return Matrix4x4.Perspective(
            Mathf.Clamp(
                fieldOfView,
                1f,
                179f),
            aspect,
            nearClip,
            farClip);
    }

    static Matrix4x4 LerpProjectionMatrix(
        Matrix4x4 from,
        Matrix4x4 to,
        float t)
    {
        t = Mathf.Clamp01(t);

        Matrix4x4 result =
            new Matrix4x4();

        for (int i = 0;
             i < 16;
             i++)
        {
            result[i] =
                Mathf.LerpUnclamped(
                    from[i],
                    to[i],
                    t);
        }

        return result;
    }

    void ClearProjectionMatrixOverride()
    {
        if (!targetCamera ||
            !_projectionMatrixOverrideActive)
            return;

        targetCamera.ResetProjectionMatrix();
        _projectionMatrixOverrideActive = false;
    }

    /// <summary>Apply the cursor policy owned by a camera mode.</summary>
    void ApplyCursorSettings(CameraMode mode)
    {
        if (mode == null) return;

        Cursor.lockState = mode.lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !mode.hideCursor;

        // CursorLockMode.Locked centres the pointer itself. When the cursor is
        // intentionally left unlocked, the new Input System lets us explicitly
        // move it to screen centre on entry as well.
        if (mode.centerCursorOnEnter && !mode.lockCursor)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current != null)
                Mouse.current.WarpCursorPosition(new Vector2(Screen.width * 0.5f,
                                                             Screen.height * 0.5f));
#endif
        }
    }

    void Awake()
    {
        RunMigrations();

        if (transform.parent)
            _parentRig = transform.parent.GetComponentInParent<UniversalCamera>();

        // Whatever the Camera was authored with is the resting value, so there
        // is no duplicate field to keep in sync with it.
        targetCamera = ResolveCamera();
        if (targetCamera)
        {
            _baseFieldOfView = targetCamera.fieldOfView;
            _baseOrthographic = targetCamera.orthographic;
            _baseOrthographicSize = targetCamera.orthographicSize;
        }

        CameraMode mode = ActiveMode;
        if (mode == null) return;

        for (int i = 0; i < mode.behaviours.Count; i++)
            mode.behaviours[i]?.Initialise(this);

        ApplyCursorSettings(mode);
    }

    void OnEnable()
    {
        Teleport();
        ApplyCursorSettings(ActiveMode);
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && isActiveAndEnabled)
            ApplyCursorSettings(ActiveMode);
    }

    void Update()
    {
        if (ActivePhase == Phase.Update)
            Tick(Time.deltaTime);
    }

    void LateUpdate()
    {
        if (ActivePhase == Phase.LateUpdate)
            Tick(Time.deltaTime);
    }

    void FixedUpdate()
    {
        if (ActivePhase != Phase.FixedUpdate) return;

        // FixedUpdate can run several times in one rendered frame, so the
        // once-per-rendered-frame guard below must not block later physics steps.
        // Mouse input is separately guarded inside ReadLookDelta, so the same
        // rendered-frame mouse delta is still consumed only once.
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

        Phase tickPhase = ActivePhase;

        // If parent and child are running in the same rendered-frame phase,
        // force the parent first so hierarchy execution order cannot make the
        // child compose against last frame's parent rotation. Do not pull a
        // parent configured for a different phase forward into this one; that
        // would defeat having Phase be genuinely per-mode.
        if (tickPhase != Phase.FixedUpdate && _parentRig && _parentRig.isActiveAndEnabled &&
            _parentRig.ActivePhase == tickPhase)
        {
            _parentRig.Tick(deltaTime);
        }

        CameraMode mode = ActiveMode;
        if (mode == null) return;

        ResolveTargetIfMissing();

        var frame = new CameraFrame
        {
            position = _positionFeedbackSeeded
                ? _positionFeedbackPosition
                : transform.position,

            rotation = _rotationFeedbackSeeded
                ? _rotationFeedbackRotation
                : transform.rotation,

            fieldOfView = _baseFieldOfView,
            orthographic = targetCamera ? targetCamera.orthographic : _baseOrthographic,
            orthographicSize = targetCamera
                ? targetCamera.orthographicSize
                : _baseOrthographicSize
        };

        bool snap = _snapNextTick;

        List<CameraBehaviour> list = mode.behaviours;
        bool fovDrivenThisTick = false;
        bool projectionDrivenThisTick = false;

        bool positionModifierSeen = false;
        bool rotationModifierSeen = false;

        Vector3 nextPositionFeedback =
            frame.position;

        Quaternion nextRotationFeedback =
            frame.rotation;

        for (int i = 0; i < list.Count; i++)
        {
            var behaviour = list[i];
            if (behaviour == null || !behaviour.enabled) continue;

            Transform resolved = behaviour.targetOverride ? behaviour.targetOverride : target;

            // Skip quietly rather than throwing: a target that spawns late is
            // normal, and an exception here would stall the whole list.
            if (behaviour.RequiresTarget && !resolved) continue;

            // Capture the channel at the exact point where the first final
            // modifier begins. This makes ordering persist across frames.
            if (!positionModifierSeen &&
                behaviour.IsPositionModifier)
            {
                nextPositionFeedback =
                    frame.position;

                positionModifierSeen =
                    true;
            }

            if (!rotationModifierSeen &&
                behaviour.IsRotationModifier)
            {
                nextRotationFeedback =
                    frame.rotation;

                rotationModifierSeen =
                    true;
            }

            var ctx = new CameraContext(this, transform, resolved, deltaTime, snap);
            behaviour.Apply(ref frame, in ctx);

            if (behaviour.WritesFieldOfView)
                fovDrivenThisTick = true;

            if (behaviour.WritesProjection)
                projectionDrivenThisTick = true;
        }

        _positionFeedbackSeeded =
            positionModifierSeen;

        if (positionModifierSeen)
            _positionFeedbackPosition =
                nextPositionFeedback;

        _rotationFeedbackSeeded =
            rotationModifierSeen;

        if (rotationModifierSeen)
            _rotationFeedbackRotation =
                nextRotationFeedback;

        // Advance a destination-owned transition once, then use the same blend
        // value for position, rotation and FOV so they arrive together.
        bool transitionThisTick = _transitionActive && mode.transitionOnEnter &&
                                  mode.transitionDuration > 0f;
        bool transitionCompletesThisTick = false;
        float transitionWeight = 1f;

        if (transitionThisTick)
        {
            _transitionElapsed += Mathf.Max(0f, deltaTime);

            float linear = Mathf.Clamp01(
                _transitionElapsed / Mathf.Max(mode.transitionDuration, 0.0001f));

            transitionWeight = mode.transitionCurve != null
                ? Mathf.Clamp01(mode.transitionCurve.Evaluate(linear))
                : linear;

            transitionCompletesThisTick = linear >= 1f;
        }

        Vector3 outputPosition = frame.position;
        Quaternion outputRotation = frame.rotation;

        if (transitionThisTick)
        {
            if (mode.transitionPosition)
                outputPosition = Vector3.Lerp(_transitionStartPosition,
                                              frame.position,
                                              transitionWeight);

            if (mode.transitionRotation)
                outputRotation = Quaternion.Slerp(_transitionStartRotation,
                                                  frame.rotation,
                                                  transitionWeight);
        }

        transform.SetPositionAndRotation(outputPosition, outputRotation);

        if (targetCamera && (projectionDrivenThisTick || _projectionWasDriven))
        {
            bool wantedOrthographic =
                projectionDrivenThisTick
                    ? frame.orthographic
                    : _baseOrthographic;

            float wantedOrthographicSize =
                projectionDrivenThisTick
                    ? Mathf.Max(
                        0.0001f,
                        frame.orthographicSize)
                    : _baseOrthographicSize;

            // Keep the Camera's ordinary serialized/runtime values pointed at
            // the DESTINATION mode. During a projection transition the custom
            // projectionMatrix below is what is actually rendered.
            targetCamera.orthographic =
                wantedOrthographic;

            targetCamera.orthographicSize =
                wantedOrthographicSize;

            bool transitionProjectionThisTick =
                transitionThisTick &&
                mode.transitionProjection;

            if (transitionProjectionThisTick)
            {
                // Use the incoming stack's final FOV as the Perspective end of
                // the morph. FOV is a separate stack channel, but while the
                // custom matrix is active this target matrix is authoritative.
                float targetFov =
                    fovDrivenThisTick
                        ? frame.fieldOfView
                        : _baseFieldOfView;

                Matrix4x4 destinationProjection =
                    BuildProjectionMatrix(
                        wantedOrthographic,
                        wantedOrthographicSize,
                        targetFov);

                targetCamera.projectionMatrix =
                    LerpProjectionMatrix(
                        _transitionStartProjection,
                        destinationProjection,
                        transitionWeight);

                _projectionMatrixOverrideActive =
                    true;
            }
            else
            {
                ClearProjectionMatrixOverride();
            }

            _projectionWasDriven =
                projectionDrivenThisTick;

            if (!projectionDrivenThisTick)
            {
                // The destination mode owns no Projection row. Returning to
                // authored Camera projection is still allowed to transition.
                targetCamera.orthographic =
                    _baseOrthographic;

                targetCamera.orthographicSize =
                    _baseOrthographicSize;
            }
        }
        else
        {
            // No projection ownership at all. Make sure a completed/interrupted
            // matrix morph cannot remain latched on the Camera.
            ClearProjectionMatrixOverride();
        }

        if (targetCamera && (fovDrivenThisTick || _fovWasDriven))
        {
            // A rig that has never driven FOV leaves it completely alone. This
            // matters on split rigs where both the holder and child can resolve
            // the same Camera. Otherwise the non-FOV rig writes the authored FOV
            // every tick while the FOV rig writes the widened value.
            float wantedFov = fovDrivenThisTick ? frame.fieldOfView : _baseFieldOfView;

            if (transitionThisTick && mode.transitionFieldOfView)
            {
                // Mode-transition FOV is explicit, so it takes precedence over
                // ordinary FOV smoothing while this transition is active.
                targetCamera.fieldOfView = Mathf.Lerp(_transitionStartFieldOfView,
                                                      wantedFov,
                                                      transitionWeight);

                if (fovDrivenThisTick)
                {
                    _fovWasDriven = true;
                }
                else if (transitionCompletesThisTick ||
                         Mathf.Abs(targetCamera.fieldOfView - _baseFieldOfView) < 0.001f)
                {
                    targetCamera.fieldOfView = _baseFieldOfView;
                    _fovWasDriven = false;
                }
            }
            else
            {
                float weight = Damp(fieldOfViewSmoothing, deltaTime, snap);
                targetCamera.fieldOfView = Mathf.Lerp(targetCamera.fieldOfView,
                                                      wantedFov,
                                                      weight);

                if (fovDrivenThisTick)
                {
                    _fovWasDriven = true;
                }
                else if (weight >= 0.9999f ||
                         Mathf.Abs(targetCamera.fieldOfView - _baseFieldOfView) < 0.001f)
                {
                    targetCamera.fieldOfView = _baseFieldOfView;
                    _fovWasDriven = false;
                }
            }
        }

        if (transitionCompletesThisTick)
        {
            _transitionActive = false;
            _transitionElapsed = 0f;

            // The final frame already reached transitionWeight = 1. Return
            // projection ownership to Unity's canonical destination matrix.
            ClearProjectionMatrixOverride();
        }

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
    // Primitive spatial behaviours
    // -----------------------------------------------------------------------

    /// <summary>
    /// Copy selected POSITION axes from a target.
    ///
    /// Examples:
    ///   XYZ + Target space + Offset 0 = sit exactly on the target.
    ///   Y only + Target space + Offset Y -2 = move to target-local height -2
    ///   while preserving the current target-local X/Z.
    ///
    /// This owns POSITION ONLY.
    /// </summary>
    [Serializable]
    public class TargetPosition : CameraBehaviour
    {
        public AxisMask axes =
            AxisMask.All;

        public ReferenceSpace space =
            ReferenceSpace.Target;

        [Tooltip("Added to the target position in the selected coordinate space.")]
        public Vector3 offset =
            Vector3.zero;

        [Advanced]
        [Min(0f)]
        [Tooltip("Optional follow smoothing. 0 = exact. Kept in Advanced so the " +
                 "normal mental model stays 'copy these axes'.")]
        public float smoothTime = 0f;

        [NonSerialized]
        bool _seeded;

        [NonSerialized]
        Vector3 _smoothedWorld;

        // TargetUp has infinitely many valid headings around target.up.
        // Pick the one whose requested X/Z offset is closest to the camera
        // when this behaviour becomes active, then parallel-transport that
        // heading as target.up changes.
        [NonSerialized]
        bool _targetUpBasisSeeded;

        [NonSerialized]
        Quaternion _targetUpBasis =
            Quaternion.identity;

        [NonSerialized]
        Transform _targetUpBasisTarget;

        public override bool RequiresTarget => true;
        public override bool IsPositionModifier => true;

        public override void Initialise(
            UniversalCamera owner)
        {
            _seeded = false;
            _targetUpBasisSeeded = false;
            _targetUpBasisTarget = null;
        }

        Quaternion ClosestTargetUpBasis(
            in CameraFrame frame,
            Transform target)
        {
            if (!target)
                return frame.rotation;

            Vector3 up =
                target.up.sqrMagnitude > 0.000001f
                    ? target.up.normalized
                    : Vector3.up;

            // A different target is a different orbit. Re-seed from the
            // camera's current visible position.
            if (_targetUpBasisTarget != target)
            {
                _targetUpBasisTarget =
                    target;

                _targetUpBasisSeeded =
                    false;
            }

            if (!_targetUpBasisSeeded)
            {
                Vector3 radial =
                    Vector3.ProjectOnPlane(
                        frame.position -
                        target.position,
                        up);

                Vector3 horizontalOffset =
                    new Vector3(
                        offset.x,
                        0f,
                        offset.z);

                // If this row actually asks for an orbital X/Z offset, rotate
                // that offset around target.up so its world-space endpoint is
                // the closest equivalent endpoint to the incoming camera.
                if (radial.sqrMagnitude > 0.000001f &&
                    horizontalOffset.sqrMagnitude > 0.000001f)
                {
                    radial.Normalize();
                    horizontalOffset.Normalize();

                    Quaternion radialFrame =
                        Quaternion.LookRotation(
                            radial,
                            up);

                    // Rotate the authored local horizontal-offset direction
                    // onto the radial frame's +Z direction.
                    Quaternion localOffsetToForward =
                        Quaternion.FromToRotation(
                            horizontalOffset,
                            Vector3.forward);

                    _targetUpBasis =
                        radialFrame *
                        localOffsetToForward;
                }
                else
                {
                    // With no meaningful horizontal offset, position does not
                    // constrain heading. Use the incoming camera orientation as
                    // the least-surprising seed.
                    _targetUpBasis =
                        BuildTargetUpBasis(
                            target,
                            frame.rotation);
                }

                _targetUpBasis =
                    Quaternion.Normalize(
                        _targetUpBasis);

                _targetUpBasisSeeded =
                    true;

                return _targetUpBasis;
            }

            // Once chosen, keep the SAME heading relative to the surface.
            // Only transport it as target.up itself changes. Target spin around
            // its up axis cannot drag the camera to another orbital position.
            Vector3 currentUp =
                _targetUpBasis *
                Vector3.up;

            _targetUpBasis =
                Quaternion.FromToRotation(
                    currentUp,
                    up) *
                _targetUpBasis;

            _targetUpBasis =
                Quaternion.Normalize(
                    _targetUpBasis);

            return _targetUpBasis;
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Vector3 origin;
            Quaternion basis;

            if (space == ReferenceSpace.TargetUp)
            {
                origin =
                    ctx.Target.position;

                basis =
                    ClosestTargetUpBasis(
                        in frame,
                        ctx.Target);
            }
            else
            {
                ResolveSpace(
                    in frame,
                    in ctx,
                    space,
                    out origin,
                    out basis);
            }

            Vector3 upstreamLocal =
                WorldToSpacePoint(
                    frame.position,
                    origin,
                    basis);

            Vector3 targetLocal =
                WorldToSpacePoint(
                    ctx.Target.position,
                    origin,
                    basis);

            Vector3 desiredLocal =
                targetLocal +
                offset;

            if (!_seeded)
            {
                _smoothedWorld =
                    frame.position;

                _seeded = true;
            }

            Vector3 currentLocal =
                WorldToSpacePoint(
                    _smoothedWorld,
                    origin,
                    basis);

            // Axes not owned by this row always come fresh from the stack above.
            currentLocal =
                new Vector3(
                    axes.x ? currentLocal.x : upstreamLocal.x,
                    axes.y ? currentLocal.y : upstreamLocal.y,
                    axes.z ? currentLocal.z : upstreamLocal.z);

            float t =
                Damp(
                    smoothTime,
                    ctx.DeltaTime,
                    ctx.Snap);

            Vector3 resultLocal =
                new Vector3(
                    axes.x
                        ? Mathf.Lerp(
                            currentLocal.x,
                            desiredLocal.x,
                            t)
                        : upstreamLocal.x,

                    axes.y
                        ? Mathf.Lerp(
                            currentLocal.y,
                            desiredLocal.y,
                            t)
                        : upstreamLocal.y,

                    axes.z
                        ? Mathf.Lerp(
                            currentLocal.z,
                            desiredLocal.z,
                            t)
                        : upstreamLocal.z);

            _smoothedWorld =
                SpaceToWorldPoint(
                    resultLocal,
                    origin,
                    basis);

            frame.position =
                _smoothedWorld;
        }
    }

    /// <summary>
    /// Add a POSITION value in a selected coordinate space.
    ///
    /// This replaces the old "distance from target" idea. A normal third-person
    /// boom is simply:
    ///   TargetPosition
    ///   Mouse Look
    ///   Position Offset     Self  (0,0,-4)
    /// </summary>
    [Serializable]
    public class AddPosition : CameraBehaviour
    {
        public AxisMask axes =
            AxisMask.All;

        public ReferenceSpace space =
            ReferenceSpace.Self;

        public Vector3 value =
            Vector3.zero;

        public override bool RequiresTarget =>
            space == ReferenceSpace.Target ||
            space == ReferenceSpace.TargetUp;

        public override bool IsPositionModifier => true;

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            ResolveSpace(
                in frame,
                in ctx,
                space,
                out _,
                out Quaternion basis);

            frame.position +=
                basis *
                axes.Filter(
                    value);
        }
    }

    /// <summary>
    /// Aim selected ROTATION axes at a target.
    ///
    /// Offset is the aim point in TARGET LOCAL coordinates.
    /// Put a position row below this if you want to aim first and then move
    /// without re-aiming on the next frame.
    /// </summary>
    [Serializable]
    public class AimAtTarget : CameraBehaviour
    {
        public AxisMask axes =
            AxisMask.All;

        [Tooltip("Aim point on the target, in target-local coordinates.")]
        public Vector3 offset =
            Vector3.zero;

        [Tooltip("Defines the orientation frame used when applying Pitch/Yaw/Roll.")]
        public ReferenceSpace space =
            ReferenceSpace.TargetUp;

        [Advanced]
        [Min(0f)]
        public float smoothTime =
            0f;

        [NonSerialized]
        bool _seeded;

        [NonSerialized]
        Quaternion _smoothedRotation =
            Quaternion.identity;

        public override bool RequiresTarget => true;
        public override bool IsRotationModifier => true;

        public override void Initialise(
            UniversalCamera owner)
        {
            _seeded = false;
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Vector3 aimPoint =
                ctx.Target.TransformPoint(
                    offset);

            Vector3 direction =
                aimPoint -
                frame.position;

            if (direction.sqrMagnitude <
                0.000001f)
                return;

            direction.Normalize();

            ResolveSpace(
                in frame,
                in ctx,
                space,
                out _,
                out Quaternion reference);

            Vector3 up =
                reference *
                Vector3.up;

            if (Mathf.Abs(
                    Vector3.Dot(
                        direction,
                        up.normalized)) >
                0.9999f)
            {
                // At the exact pole, derive a stable up from the current frame.
                up =
                    Vector3.ProjectOnPlane(
                        frame.Up,
                        direction);

                if (up.sqrMagnitude <
                    0.000001f)
                {
                    up =
                        reference *
                        Vector3.right;
                }
            }

            Quaternion fullDesired =
                Quaternion.LookRotation(
                    direction,
                    up);

            Quaternion desired =
                MergeRotationAxes(
                    frame.rotation,
                    fullDesired,
                    axes,
                    reference);

            if (!_seeded)
            {
                _smoothedRotation =
                    frame.rotation;

                _seeded = true;
            }

            // Keep axes we do NOT own attached to the upstream frame.
            _smoothedRotation =
                MergeRotationAxes(
                    frame.rotation,
                    _smoothedRotation,
                    axes,
                    reference);

            _smoothedRotation =
                Quaternion.Slerp(
                    _smoothedRotation,
                    desired,
                    Damp(
                        smoothTime,
                        ctx.DeltaTime,
                        ctx.Snap));

            frame.rotation =
                _smoothedRotation;
        }
    }

    /// <summary>
    /// Mouse/stick rotation in the same ordered stack as every other operation.
    ///
    /// Axes mean:
    ///   X = Pitch
    ///   Y = Yaw
    ///   Z = Roll/Horizon
    ///
    /// Mouse input changes Pitch/Yaw. Z decides whether this row also owns roll.
    /// With Space = TargetUp and Z enabled, the horizon stays aligned to target.up.
    /// Space = Self gives an unclamped/free-look style local rotation.
    /// </summary>
    [Serializable]
    public class MouseRotation : CameraBehaviour
    {
        public AxisMask axes =
            AxisMask.All;

        [Tooltip("X = pitch sensitivity, Y = yaw sensitivity. Z is reserved for roll.")]
        public Vector3 value =
            new Vector3(
                0.12f,
                0.12f,
                0f);

        [Tooltip("Constant Pitch/Yaw/Roll offset, in degrees.")]
        public Vector3 offset =
            Vector3.zero;

        public ReferenceSpace space =
            ReferenceSpace.TargetUp;

        [Min(0f)]
        [Tooltip("Seconds to ease toward the mouse-controlled rotation. 0 = raw.")]
        public float smoothTime =
            0f;

        [Advanced]
        public bool invertY = false;

        [Advanced]
        public bool limitPitch = true;

        [Advanced]
        public Vector2 pitchLimit =
            new Vector2(
                -60f,
                70f);

        [Advanced]
        public bool requireCursorLock = true;

        [Advanced]
        [Tooltip("Gamepad stick speed before sensitivity. No effect on mouse.")]
        public float stickSpeed = 1000f;

        [NonSerialized]
        float _pitch;

        [NonSerialized]
        float _yaw;

        [NonSerialized]
        bool _initialised;

        [NonSerialized]
        Quaternion _freeRotation =
            Quaternion.identity;

        [NonSerialized]
        Quaternion _targetUpBasis =
            Quaternion.identity;

        [NonSerialized]
        bool _targetUpBasisSeeded;

        [NonSerialized]
        Quaternion _smoothedRotation =
            Quaternion.identity;

        [NonSerialized]
        bool _smoothedRotationSeeded;

        [NonSerialized]
        int _lastMouseInputFrame =
            -1;

        public override bool RequiresTarget =>
            space == ReferenceSpace.Target ||
            space == ReferenceSpace.TargetUp;

        public override bool IsRotationModifier => true;

        public override void Initialise(
            UniversalCamera owner)
        {
            _initialised = false;
            _targetUpBasisSeeded = false;
            _smoothedRotationSeeded = false;
            _lastMouseInputFrame = -1;
        }

        void ApplySmoothedRotation(
            ref CameraFrame frame,
            Quaternion desired,
            Quaternion reference,
            in CameraContext ctx)
        {
            if (!_smoothedRotationSeeded)
            {
                _smoothedRotation =
                    frame.rotation;

                _smoothedRotationSeeded =
                    true;
            }

            // Axes this row does NOT own must remain whatever the rows above
            // produced this frame rather than acquiring their own lag.
            _smoothedRotation =
                MergeRotationAxes(
                    frame.rotation,
                    _smoothedRotation,
                    axes,
                    reference);

            _smoothedRotation =
                Quaternion.Slerp(
                    _smoothedRotation,
                    desired,
                    Damp(
                        smoothTime,
                        ctx.DeltaTime,
                        ctx.Snap));

            frame.rotation =
                _smoothedRotation;
        }

        Quaternion TargetUpBasis(
            Transform target,
            Quaternion incoming)
        {
            if (!_targetUpBasisSeeded)
            {
                _targetUpBasis =
                    BuildTargetUpBasis(
                        target,
                        incoming);

                _targetUpBasisSeeded =
                    true;

                return _targetUpBasis;
            }

            Vector3 currentUp =
                _targetUpBasis *
                Vector3.up;

            Vector3 wantedUp =
                target.up.sqrMagnitude >
                0.000001f
                    ? target.up.normalized
                    : Vector3.up;

            _targetUpBasis =
                Quaternion.FromToRotation(
                    currentUp,
                    wantedUp) *
                _targetUpBasis;

            _targetUpBasis =
                Quaternion.Normalize(
                    _targetUpBasis);

            return _targetUpBasis;
        }

        Quaternion MouseBasis(
            in CameraFrame frame,
            in CameraContext ctx)
        {
            switch (space)
            {
                case ReferenceSpace.Parent:
                    return ctx.Self.parent
                        ? ctx.Self.parent.rotation
                        : Quaternion.identity;

                case ReferenceSpace.Target:
                    return ctx.Target
                        ? ctx.Target.rotation
                        : Quaternion.identity;

                case ReferenceSpace.TargetUp:
                    return ctx.Target
                        ? TargetUpBasis(
                            ctx.Target,
                            frame.rotation)
                        : Quaternion.identity;

                default:
                    return Quaternion.identity;
            }
        }

        void SeedFromIncoming(
            Quaternion basis,
            Quaternion incoming)
        {
            Quaternion relative =
                Quaternion.Normalize(
                    Quaternion.Inverse(
                        basis) *
                    incoming);

            Vector3 forward =
                relative *
                Vector3.forward;

            float horizontal =
                Mathf.Sqrt(
                    forward.x * forward.x +
                    forward.z * forward.z);

            if (horizontal >
                0.00001f)
            {
                _yaw =
                    Mathf.Atan2(
                        forward.x,
                        forward.z) *
                    Mathf.Rad2Deg;
            }
            else
            {
                Vector3 right =
                    relative *
                    Vector3.right;

                _yaw =
                    Mathf.Atan2(
                        -right.z,
                        right.x) *
                    Mathf.Rad2Deg;
            }

            _pitch =
                Mathf.Atan2(
                    -forward.y,
                    Mathf.Max(
                        horizontal,
                        0.00001f)) *
                Mathf.Rad2Deg;

            if (limitPitch)
            {
                _pitch =
                    Mathf.Clamp(
                        _pitch,
                        pitchLimit.x,
                        pitchLimit.y);
            }

            _freeRotation =
                incoming;

            _initialised =
                true;
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Vector2 delta =
                Vector2.zero;

            if (!requireCursorLock ||
                Cursor.lockState ==
                CursorLockMode.Locked)
            {
                delta =
                    ReadLookDelta(
                        ctx.DeltaTime,
                        stickSpeed,
                        ref _lastMouseInputFrame);
            }

            if (space == ReferenceSpace.Self)
            {
                if (!_initialised)
                {
                    _freeRotation =
                        frame.rotation;

                    _initialised =
                        true;
                }

                float pitchDelta =
                    axes.x
                        ? (invertY ? delta.y : -delta.y) * value.x
                        : 0f;

                float yawDelta =
                    axes.y
                        ? delta.x * value.y
                        : 0f;

                Quaternion step =
                    Quaternion.Euler(
                        pitchDelta,
                        yawDelta,
                        0f);

                _freeRotation =
                    Quaternion.Normalize(
                        _freeRotation *
                        step);

                Quaternion offsetRotation =
                    Quaternion.Euler(
                        axes.Filter(
                            offset));

                Quaternion selfDesired =
                    _freeRotation *
                    offsetRotation;

                // In Self/free mode the rotation is a whole quaternion rather
                // than decomposed against an external basis.
                if (!_smoothedRotationSeeded)
                {
                    _smoothedRotation =
                        frame.rotation;

                    _smoothedRotationSeeded =
                        true;
                }

                _smoothedRotation =
                    Quaternion.Slerp(
                        _smoothedRotation,
                        selfDesired,
                        Damp(
                            smoothTime,
                            ctx.DeltaTime,
                            ctx.Snap));

                frame.rotation =
                    _smoothedRotation;

                return;
            }

            Quaternion basis =
                MouseBasis(
                    in frame,
                    in ctx);

            if (!_initialised)
            {
                SeedFromIncoming(
                    basis,
                    frame.rotation);
            }

            if (axes.y)
            {
                _yaw +=
                    delta.x *
                    value.y;
            }

            if (axes.x)
            {
                _pitch +=
                    (invertY
                        ? delta.y
                        : -delta.y) *
                    value.x;
            }

            _yaw =
                Mathf.Repeat(
                    _yaw,
                    360f);

            if (limitPitch &&
                axes.x)
            {
                _pitch =
                    Mathf.Clamp(
                        _pitch,
                        pitchLimit.x,
                        pitchLimit.y);
            }

            Quaternion controlled =
                basis *
                Quaternion.Euler(
                    _pitch + offset.x,
                    _yaw + offset.y,
                    offset.z);

            // First build the exact rotation this row would own, then smooth
            // only toward that result. Unowned axes still come directly from
            // the rows above.
            Quaternion desired =
                MergeRotationAxes(
                    frame.rotation,
                    controlled,
                    axes,
                    basis);

            ApplySmoothedRotation(
                ref frame,
                desired,
                basis,
                in ctx);
        }
    }

    /// <summary>
    /// Add a ROTATION value after whatever rotation rows came above it.
    ///
    /// Fixed = constant offset.
    /// PerSecond = continuously accumulate Value as degrees/second.
    /// </summary>
    [Serializable]
    public class RotationOffset : CameraBehaviour
    {
        public enum Rate
        {
            Fixed,
            PerSecond
        }

        public AxisMask axes =
            AxisMask.All;

        public ReferenceSpace space =
            ReferenceSpace.Self;

        public Vector3 value =
            Vector3.zero;

        [Tooltip("Optional additional fixed rotation in degrees.")]
        public Vector3 offset =
            Vector3.zero;

        public Rate rate =
            Rate.Fixed;

        [NonSerialized]
        Vector3 _accumulated;

        public override bool RequiresTarget =>
            space == ReferenceSpace.Target ||
            space == ReferenceSpace.TargetUp;

        public override bool IsRotationModifier => true;

        public override void Initialise(
            UniversalCamera owner)
        {
            _accumulated =
                Vector3.zero;
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Vector3 amount;

            if (rate == Rate.PerSecond)
            {
                _accumulated +=
                    value *
                    ctx.DeltaTime;

                amount =
                    _accumulated +
                    offset;
            }
            else
            {
                amount =
                    value +
                    offset;
            }

            amount =
                axes.Filter(
                    amount);

            Quaternion delta =
                Quaternion.Euler(
                    amount);

            if (space == ReferenceSpace.Self)
            {
                frame.rotation =
                    frame.rotation *
                    delta;

                return;
            }

            ResolveSpace(
                in frame,
                in ctx,
                space,
                out _,
                out Quaternion basis);

            Quaternion worldDelta =
                basis *
                delta *
                Quaternion.Inverse(
                    basis);

            frame.rotation =
                worldDelta *
                frame.rotation;
        }
    }

    /// <summary>
    /// Final obstruction correction. It NEVER decides where the camera wants to
    /// be; the rows above already did that. It only shortens the line from pivot
    /// to the current frame.position when geometry blocks it.
    /// </summary>
    [Serializable]
    public class CameraCollision : CameraBehaviour
    {
        [Tooltip("Pivot offset in target-local coordinates.")]
        public Vector3 offset =
            Vector3.zero;

        [Min(0f)]
        public float radius =
            0.2f;

        [Min(0f)]
        public float minimumDistance =
            0.4f;

        public LayerMask layers =
            ~0;

        public override bool RequiresTarget => true;
        public override bool IsPositionModifier => true;

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Vector3 pivot =
                ctx.Target.TransformPoint(
                    offset);

            Vector3 delta =
                frame.position -
                pivot;

            float distance =
                delta.magnitude;

            if (distance <=
                0.00001f)
                return;

            Vector3 direction =
                delta /
                distance;

            if (Physics.SphereCast(
                pivot,
                radius,
                direction,
                out RaycastHit hit,
                distance,
                layers,
                QueryTriggerInteraction.Ignore))
            {
                float allowed =
                    Mathf.Max(
                        minimumDistance,
                        hit.distance);

                frame.position =
                    pivot +
                    direction *
                    allowed;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Camera value behaviours
    // -----------------------------------------------------------------------

    /// <summary>
    /// Set the Camera field of view. Value + Offset is the final FOV.
    /// </summary>
    [Serializable]
    public class FieldOfView : CameraBehaviour
    {
        [Range(1f, 179f)]
        public float value =
            60f;

        public float offset =
            0f;

        public override bool WritesFieldOfView => true;

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            frame.fieldOfView =
                Mathf.Clamp(
                    value +
                    offset,
                    1f,
                    179f);
        }
    }

    /// <summary>
    /// Add FOV based on authoritative movement speed.
    /// Put it BELOW FieldOfView when you want "base FOV, then speed boost".
    /// </summary>
    [Serializable]
    public class SpeedFOV : CameraBehaviour
    {
        [Tooltip("FOV added at full movement speed.")]
        public float value =
            22f;

        public float speedForFullEffect =
            14f;

        [Advanced]
        public float deadZone =
            2f;

        [Advanced]
        public bool useMovementSpeed =
            true;

        [NonSerialized]
        Vector3 _lastPosition;

        [NonSerialized]
        bool _seeded;

        [NonSerialized]
        Transform _cachedTarget;

        [NonSerialized]
        VirusMovement _movement;

        [NonSerialized]
        Rigidbody _rigidbody;

        public override bool RequiresTarget => true;
        public override bool WritesFieldOfView => true;

        public override void Initialise(
            UniversalCamera owner)
        {
            _seeded = false;
            _cachedTarget = null;
            _movement = null;
            _rigidbody = null;
        }

        void CacheSpeedSource(
            Transform target)
        {
            if (_cachedTarget ==
                target)
                return;

            _cachedTarget =
                target;

            _movement = null;
            _rigidbody = null;

            if (!target)
                return;

            if (useMovementSpeed)
            {
                _movement =
                    target.GetComponentInParent<VirusMovement>();

                if (!_movement)
                {
                    _movement =
                        target.GetComponentInChildren<VirusMovement>();
                }
            }

            _rigidbody =
                target.GetComponentInParent<Rigidbody>();

            if (!_rigidbody)
            {
                _rigidbody =
                    target.GetComponentInChildren<Rigidbody>();
            }
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            CacheSpeedSource(
                ctx.Target);

            float speed;
            float fullSpeed;

            if (_movement)
            {
                speed =
                    _movement.speed;

                fullSpeed =
                    Mathf.Max(
                        _movement.totalSpeed,
                        deadZone +
                        0.0001f);

                _lastPosition =
                    ctx.Target.position;

                _seeded = true;
            }
            else if (_rigidbody &&
                     !_rigidbody.isKinematic)
            {
                speed =
                    _rigidbody.linearVelocity.magnitude;

                fullSpeed =
                    Mathf.Max(
                        speedForFullEffect,
                        deadZone +
                        0.0001f);

                _lastPosition =
                    ctx.Target.position;

                _seeded = true;
            }
            else
            {
                if (!_seeded)
                {
                    _lastPosition =
                        ctx.Target.position;

                    _seeded = true;
                    return;
                }

                float dt =
                    Mathf.Max(
                        ctx.DeltaTime,
                        0.00001f);

                speed =
                    (ctx.Target.position -
                     _lastPosition).magnitude /
                    dt;

                fullSpeed =
                    Mathf.Max(
                        speedForFullEffect,
                        deadZone +
                        0.0001f);

                _lastPosition =
                    ctx.Target.position;
            }

            float t =
                Mathf.InverseLerp(
                    deadZone,
                    fullSpeed,
                    speed);

            frame.fieldOfView +=
                value *
                t;
        }
    }

    /// <summary>
    /// Projection owns only perspective-vs-orthographic and orthographic size.
    /// Perspective FOV belongs to the separate FieldOfView behaviour.
    /// </summary>
    [Serializable]
    public class Projection : CameraBehaviour
    {
        public enum Mode
        {
            Perspective,
            Orthographic
        }

        public Mode value =
            Mode.Perspective;

        [Min(0.0001f)]
        [Tooltip("Used only when Value = Orthographic.")]
        public float orthographicSize =
            5f;

        public override bool WritesProjection => true;

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            frame.orthographic =
                value ==
                Mode.Orthographic;

            if (frame.orthographic)
            {
                frame.orthographicSize =
                    Mathf.Max(
                        0.0001f,
                        orthographicSize);
            }
        }
    }


    // -----------------------------------------------------------------------
    // Legacy compatibility
    //
    // These old behaviour types remain only so existing scenes/prefabs keep
    // their SerializeReference data. They are NOT offered by Add Step.
    // Replace them with the primitive stack rows when convenient.
    // -----------------------------------------------------------------------

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

    [Serializable]
    public class MatchTargetHeight : CameraBehaviour
    {
        public enum HeightSpace
        {
            TargetLocal,
            TargetParentLocal,
            World
        }

        [Tooltip("TargetLocal follows the target's own local up axis. This is " +
                 "normally what you want for a virus that can walk on arbitrary surfaces.")]
        public HeightSpace space = HeightSpace.TargetLocal;

        [Tooltip("Extra height relative to the target in the selected space. " +
                 "0 means exactly the same height.")]
        public float heightOffset = 0f;

        [Tooltip("Seconds to ease onto the target's height. 0 snaps.")]
        public float smoothTime = 0.08f;

        public override bool RequiresTarget => true;
        public override bool IsPositionModifier => true;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            // Contract: this behaviour owns POSITION ONLY.
            Quaternion incomingRotation =
                frame.rotation;

            Vector3 desired = frame.position;

            switch (space)
            {
                case HeightSpace.TargetParentLocal:
                {
                    Transform parent = ctx.Target.parent;

                    if (parent)
                    {
                        Vector3 cameraLocal =
                            parent.InverseTransformPoint(frame.position);

                        Vector3 targetLocal =
                            ctx.Target.localPosition;

                        cameraLocal.y =
                            targetLocal.y +
                            heightOffset;

                        desired =
                            parent.TransformPoint(cameraLocal);
                    }
                    else
                    {
                        desired.y =
                            ctx.Target.position.y +
                            heightOffset;
                    }

                    break;
                }

                case HeightSpace.World:
                {
                    desired.y =
                        ctx.Target.position.y +
                        heightOffset;

                    break;
                }

                default:
                {
                    // Express the current camera point in the target's local
                    // frame. The target itself is local (0,0,0), so y=0 is
                    // exactly its local-height plane. Preserve local X/Z and
                    // change only local Y.
                    Vector3 cameraInTarget =
                        ctx.Target.InverseTransformPoint(frame.position);

                    cameraInTarget.y =
                        heightOffset;

                    desired =
                        ctx.Target.TransformPoint(cameraInTarget);

                    break;
                }
            }

            float t =
                Damp(
                    smoothTime,
                    ctx.DeltaTime,
                    ctx.Snap);

            frame.position =
                Vector3.LerpUnclamped(
                    frame.position,
                    desired,
                    t);

            // Explicit even though we never intentionally touched rotation.
            frame.rotation =
                incomingRotation;
        }
    }

    [Serializable]
    public class PositionOffset : CameraBehaviour
    {
        public enum OffsetSpace { World, Self, Parent, Target }

        public Vector3 offset = Vector3.zero;
        [Advanced] public OffsetSpace space = OffsetSpace.Self;

        public override bool RequiresTarget => space == OffsetSpace.Target;
        public override bool IsPositionModifier => true;

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

    [Serializable]
    public class MouseLook : CameraBehaviour
    {
        public enum Basis { Parent, World, Target, TargetUp }

        public Vector2 sensitivity = new Vector2(0.12f, 0.12f);

        public bool yaw = true;
        public bool pitch = true;

        [Tooltip("Min and max pitch in degrees. Only applied when Pitch is on.")]
        public Vector2 pitchClamp = new Vector2(-60f, 70f);

        [Advanced] public bool invertY = false;

        [Advanced]
        [Tooltip("What the look is composed on top of. Parent lets a " +
                 "pitch-only camera sit under a yaw-only holder. Target " +
                 "inherits the target's full rotation, so its heading " +
                 "drags the view around as it turns. TargetUp takes only " +
                 "the up axis, so the camera stays oriented to a surface " +
                 "without the target's spin pulling the view.")]
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
        [NonSerialized] Quaternion _upBasis = Quaternion.identity;
        [NonSerialized] bool _upBasisSeeded;
        [NonSerialized] int _lastMouseInputFrame = -1;

        public override bool RequiresTarget => basis == Basis.Target || basis == Basis.TargetUp;

        public override void Initialise(UniversalCamera owner)
        {
            // Target / Parent / TargetUp basis is only fully known in Apply.
            // Defer seeding so entering a mode starts from the EXACT incoming
            // stack rotation instead of reconstructing from unrelated Euler
            // angles and snapping toward a default orientation.
            _initialised = false;
            _upBasisSeeded = false;
            _lastMouseInputFrame = -1;
        }

        /// <summary>
        /// Minimal rotation carrying world up onto the target's up, holding no
        /// twist about that axis -- so the target can spin freely without
        /// dragging the view with it.
        ///
        /// Tracked incrementally from the previous frame rather than rebuilt
        /// from world up each time. FromToRotation is ambiguous when the two
        /// vectors are opposed, and a virus crawling to the underside of a cell
        /// passes exactly through that case; stepping from the last basis keeps
        /// every rotation small and the ambiguity never arises.
        /// </summary>
        Quaternion UpBasis(Vector3 targetUp)
        {
            if (!_upBasisSeeded)
            {
                _upBasis = Quaternion.FromToRotation(Vector3.up, targetUp);
                _upBasisSeeded = true;
                return _upBasis;
            }

            _upBasis = Quaternion.FromToRotation(_upBasis * Vector3.up, targetUp) * _upBasis;
            _upBasis = Quaternion.Normalize(_upBasis);
            return _upBasis;
        }

        Quaternion BasisRotation(
            in CameraContext ctx)
        {
            switch (basis)
            {
                case Basis.Parent:
                    return ctx.Self.parent
                        ? ctx.Self.parent.rotation
                        : Quaternion.identity;

                case Basis.Target:
                    return ctx.Target
                        ? ctx.Target.rotation
                        : Quaternion.identity;

                case Basis.TargetUp:
                    return ctx.Target
                        ? UpBasis(ctx.Target.up)
                        : Quaternion.identity;

                default:
                    return Quaternion.identity;
            }
        }

        void SeedFromIncomingFrame(
            Quaternion basisRotation,
            Quaternion incomingRotation)
        {
            // MouseLook owns a LOOK DIRECTION plus a basis-up reference.
            // Preserve the incoming forward direction, not arbitrary incoming
            // roll. Preserving roll is what allowed TargetUp to enter a mode
            // with a horizon tilted away from the target's surface-up.
            Quaternion invBasis =
                Quaternion.Inverse(
                    basisRotation);

            Vector3 localForward =
                invBasis *
                (incomingRotation *
                 Vector3.forward);

            if (localForward.sqrMagnitude <
                0.000001f)
            {
                localForward =
                    Vector3.forward;
            }

            localForward.Normalize();

            float horizontal =
                Mathf.Sqrt(
                    localForward.x *
                    localForward.x +
                    localForward.z *
                    localForward.z);

            if (horizontal >
                0.00001f)
            {
                _yaw =
                    Mathf.Atan2(
                        localForward.x,
                        localForward.z) *
                    Mathf.Rad2Deg;
            }
            else
            {
                // Looking almost exactly along the up axis makes forward alone
                // unable to define yaw. Use incoming right so mode entry still
                // preserves heading instead of defaulting to zero.
                Vector3 localRight =
                    invBasis *
                    (incomingRotation *
                     Vector3.right);

                _yaw =
                    Mathf.Atan2(
                        -localRight.z,
                        localRight.x) *
                    Mathf.Rad2Deg;
            }

            _pitch =
                Mathf.Atan2(
                    -localForward.y,
                    Mathf.Max(
                        horizontal,
                        0.00001f)) *
                Mathf.Rad2Deg;

            if (pitch)
            {
                _pitch =
                    Mathf.Clamp(
                        _pitch,
                        pitchClamp.x,
                        pitchClamp.y);
            }

            _initialised = true;
        }

        public override void Apply(
            ref CameraFrame frame,
            in CameraContext ctx)
        {
            Quaternion basisRotation =
                BasisRotation(
                    in ctx);

            if (!_initialised)
            {
                // Seed from the rotation produced by behaviours ABOVE us.
                SeedFromIncomingFrame(
                    basisRotation,
                    frame.rotation);
            }

            if (!requireCursorLock ||
                Cursor.lockState ==
                CursorLockMode.Locked)
            {
                Vector2 delta =
                    ReadLookDelta(
                        ctx.DeltaTime,
                        stickSpeed,
                        ref _lastMouseInputFrame);

                if (yaw)
                    _yaw +=
                        delta.x *
                        sensitivity.x;

                if (pitch)
                    _pitch +=
                        (invertY
                            ? delta.y
                            : -delta.y) *
                        sensitivity.y;
            }

            _yaw =
                Mathf.Repeat(
                    _yaw,
                    360f);

            if (pitch)
            {
                _pitch =
                    Mathf.Clamp(
                        _pitch,
                        pitchClamp.x,
                        pitchClamp.y);
            }

            // Disabled axes preserve their seeded yaw/pitch instead of being
            // silently forced to zero on mode entry.
            Quaternion relativeLook =
                Quaternion.Euler(
                    _pitch,
                    _yaw,
                    0f);

            Quaternion desired;

            if (basis == Basis.TargetUp &&
                ctx.Target)
            {
                // TargetUp is a HORIZON/UP-reference contract. Keep the chosen
                // forward direction, but remove roll around it and rebuild the
                // camera orientation against the target's current up vector.
                //
                // Camera.up cannot literally equal Target.up while looking
                // upward/downward because up must stay perpendicular to forward;
                // LookRotation gives the roll-free orientation whose up is as
                // aligned with Target.up as geometry permits.
                Vector3 forward =
                    basisRotation *
                    (relativeLook *
                     Vector3.forward);

                Vector3 upReference =
                    ctx.Target.up;

                if (forward.sqrMagnitude <
                    0.000001f)
                {
                    desired =
                        basisRotation *
                        relativeLook;
                }
                else
                {
                    forward.Normalize();

                    // Exact parallel/antiparallel is degenerate for LookRotation.
                    // The basis-composed form is continuous at the pole.
                    float parallel =
                        Mathf.Abs(
                            Vector3.Dot(
                                forward,
                                upReference.normalized));

                    desired =
                        parallel > 0.9999f
                            ? basisRotation *
                              relativeLook
                            : Quaternion.LookRotation(
                                forward,
                                upReference);
                }
            }
            else
            {
                desired =
                    basisRotation *
                    relativeLook;
            }

            frame.rotation =
                Quaternion.Slerp(
                    frame.rotation,
                    desired,
                    Damp(
                        smoothTime,
                        ctx.DeltaTime,
                        ctx.Snap));
        }
    }

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
        [NonSerialized] int _lastMouseInputFrame = -1;

        public override void Initialise(UniversalCamera owner)
        {
            // Defer until Apply so ordering above FreeLook is respected.
            _initialised = false;
            _lastMouseInputFrame = -1;
        }

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            if (!_initialised)
            {
                _rotation =
                    frame.rotation;

                _initialised =
                    true;
            }

            Vector2 delta = Vector2.zero;
            if (!requireCursorLock || Cursor.lockState == CursorLockMode.Locked)
                delta = ReadLookDelta(ctx.DeltaTime, stickSpeed, ref _lastMouseInputFrame);

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

    [Serializable]
    public class CameraProjection : CameraBehaviour
    {
        public enum Projection
        {
            Perspective,
            Orthographic
        }

        public Projection projection = Projection.Perspective;

        [Range(1f, 179f)]
        [Tooltip("Field of view used when Projection is Perspective.")]
        public float perspectiveFieldOfView = 60f;

        [Min(0.0001f)]
        [Tooltip("Camera size used when Projection is Orthographic.")]
        public float orthographicSize = 5f;

        [Advanced]
        [Min(0f)]
        [Tooltip("Seconds to ease Orthographic Size toward its target. 0 snaps. " +
                 "Perspective FOV uses the UniversalCamera's normal Field Of View Smoothing.")]
        public float orthographicSizeSmoothing = 0f;

        public override bool WritesProjection => true;

        // Perspective FOV is part of this behaviour. In Orthographic mode FOV
        // is intentionally left alone because Unity does not use it.
        public override bool WritesFieldOfView =>
            projection == Projection.Perspective;

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            bool wantsOrtho = projection == Projection.Orthographic;
            frame.orthographic = wantsOrtho;

            if (wantsOrtho)
            {
                float wanted = Mathf.Max(0.0001f, orthographicSize);

                frame.orthographicSize = Mathf.Lerp(
                    frame.orthographicSize,
                    wanted,
                    Damp(orthographicSizeSmoothing, ctx.DeltaTime, ctx.Snap));
            }
            else
            {
                frame.fieldOfView = Mathf.Clamp(perspectiveFieldOfView, 1f, 179f);
            }
        }
    }

    [Serializable]
    public class SpeedFieldOfView : CameraBehaviour
    {
        [Tooltip("Degrees added at full movement speed, on top of the Camera's authored FOV.")]
        public float extraFieldOfView = 22f;

        [Tooltip("Fallback full-effect speed when the target has no VirusMovement. " +
                 "When VirusMovement is found, its totalSpeed is used instead.")]
        public float speedForFullEffect = 14f;

        [Advanced]
        [Tooltip("Actual movement speed below this value produces no FOV increase.")]
        public float deadZone = 2f;

        [Advanced]
        [Tooltip("Prefer VirusMovement.speed / VirusMovement.totalSpeed when that component " +
                 "exists on the target or one of its parents. This avoids deriving speed " +
                 "from render-frame position deltas, which can alternate high/low on an " +
                 "interpolated Rigidbody and make unsmoothed FOV flicker.")]
        public bool useMovementSpeed = true;

        [NonSerialized] Vector3 _lastPosition;
        [NonSerialized] bool _seeded;
        [NonSerialized] Transform _cachedTarget;
        [NonSerialized] VirusMovement _movement;
        [NonSerialized] Rigidbody _rigidbody;

        public override bool RequiresTarget => true;
        public override bool WritesFieldOfView => true;

        public override void Initialise(UniversalCamera owner)
        {
            _seeded = false;
            _cachedTarget = null;
            _movement = null;
            _rigidbody = null;
        }

        void CacheSpeedSource(Transform target)
        {
            if (_cachedTarget == target) return;

            _cachedTarget = target;
            _movement = null;
            _rigidbody = null;

            if (!target) return;

            if (useMovementSpeed)
            {
                _movement = target.GetComponentInParent<VirusMovement>();
                if (!_movement)
                    _movement = target.GetComponentInChildren<VirusMovement>();
            }

            // Rigidbody velocity is a much better fallback than position delta
            // for a physics-driven target, especially with interpolation on.
            _rigidbody = target.GetComponentInParent<Rigidbody>();
            if (!_rigidbody)
                _rigidbody = target.GetComponentInChildren<Rigidbody>();
        }

        public override void Apply(ref CameraFrame frame, in CameraContext ctx)
        {
            CacheSpeedSource(ctx.Target);

            float speed;
            float fullSpeed;

            if (_movement)
            {
                // VirusMovement owns the authoritative speed. Flying comes from
                // Rigidbody.linearVelocity; grounded speed comes from the actual
                // graph-space step. totalSpeed is flySpeed or walkSpeed for the
                // current state, so the FOV tracks the movement system itself.
                speed = _movement.speed;
                fullSpeed = Mathf.Max(_movement.totalSpeed, deadZone + 1e-4f);

                _lastPosition = ctx.Target.position;
                _seeded = true;
            }
            else if (_rigidbody && !_rigidbody.isKinematic)
            {
                speed = _rigidbody.linearVelocity.magnitude;
                fullSpeed = Mathf.Max(speedForFullEffect, deadZone + 1e-4f);

                _lastPosition = ctx.Target.position;
                _seeded = true;
            }
            else
            {
                // Generic fallback for animation/kinematic targets.
                if (!_seeded)
                {
                    _lastPosition = ctx.Target.position;
                    _seeded = true;
                    return;
                }

                float dt = Mathf.Max(ctx.DeltaTime, 1e-5f);
                speed = (ctx.Target.position - _lastPosition).magnitude / dt;
                fullSpeed = Mathf.Max(speedForFullEffect, deadZone + 1e-4f);
                _lastPosition = ctx.Target.position;
            }

            float t = Mathf.InverseLerp(deadZone, fullSpeed, speed);
            frame.fieldOfView += extraFieldOfView * t;
        }
    }

    [Serializable]
    public class ConstantRotate : CameraBehaviour
    {
        public enum Basis { World, Self }

        public Vector3 degreesPerSecond = new Vector3(0f, 20f, 0f);
        [Advanced] public Basis basis = Basis.World;

        [NonSerialized] Vector3 _accumulated;

        public override bool IsRotationModifier => true;

        public override void Initialise(UniversalCamera owner) =>
            _accumulated = Vector3.zero;

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

    static Vector2 ReadLookDelta(
        float deltaTime,
        float stickSpeed,
        ref int lastMouseInputFrame)
    {
        Vector2 delta =
            Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        int frame =
            Time.frameCount;

        if (lastMouseInputFrame !=
            frame)
        {
            if (Mouse.current != null)
            {
                delta +=
                    Mouse.current.delta.ReadValue();
            }

            lastMouseInputFrame =
                frame;
        }

        if (Gamepad.current != null)
        {
            delta +=
                Gamepad.current.rightStick.ReadValue() *
                stickSpeed *
                deltaTime;
        }

#elif ENABLE_LEGACY_INPUT_MANAGER
        int frame =
            Time.frameCount;

        if (lastMouseInputFrame !=
            frame)
        {
            delta =
                new Vector2(
                    Input.GetAxisRaw("Mouse X"),
                    Input.GetAxisRaw("Mouse Y"));

            lastMouseInputFrame =
                frame;
        }
#endif

        return delta;
    }
}

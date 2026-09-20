using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// World-space hold button.
///
/// - Can be enabled/disabled by another script.
/// - Faces the camera while remaining upright relative to the camera.
/// - Drives a UI Image as a radial hold-progress indicator.
/// - Invokes a UnityEvent when the configured input has been held long enough.
///
/// Recommended hierarchy:
///
/// WorldButtonRoot     <- put this script here
/// └── VisualRoot      <- World Space Canvas / graphics
///     ├── Icon/Text
///     └── RadialFill  <- UI Image assigned to radialImage
///
/// Another script can keep a reference to this component and call:
///     worldButton.Show();
///     worldButton.Hide();
///
/// Or simply:
///     worldButton.enabled = true;
///     worldButton.enabled = false;
/// </summary>
public class WorldButton : MonoBehaviour
{
    public enum FollowOffsetSpace
    {
        World,
        TargetLocal,
        CameraLocal
    }

    public enum BillboardMode
    {
        /// <summary>
        /// Keeps the button parallel to the camera plane.
        /// Usually the cleanest choice for readable world-space text.
        /// </summary>
        MatchCameraRotation,

        /// <summary>
        /// Points the button directly toward the camera position while using
        /// the camera's up direction, so the text stays upright on screen.
        /// </summary>
        LookAtCamera
    }

    [Header("Visibility")]
    [Tooltip("Optional child containing the visible Canvas/graphics. " +
             "If assigned, enabling/disabling this component also shows/hides it.")]
    public GameObject visualRoot;

    [Tooltip("If true, the hold progress resets whenever this WorldButton is enabled.")]
    public bool resetOnEnable = true;

    [Header("Follow")]
    [Tooltip("Optional world point/object for this button to follow.")]
    public Transform followPoint;

    [Tooltip("Offset from Follow Point.")]
    public Vector3 followOffset = Vector3.zero;

    [Tooltip("Space the Follow Offset is interpreted in.")]
    public FollowOffsetSpace followOffsetSpace = FollowOffsetSpace.World;

    [Min(0f)]
    [Tooltip("Seconds used to smooth following. 0 follows exactly.")]
    public float followSmoothTime = 0f;

    [Header("Camera Facing")]
    [Tooltip("Optional camera override. If empty, Camera.main is used.")]
    public Camera targetCamera;

    public BillboardMode billboardMode = BillboardMode.MatchCameraRotation;

    [Tooltip("Turn this on if your Canvas/text appears backwards.")]
    public bool flip180 = false;

    [Tooltip("If enabled, billboard rotation is applied in LateUpdate so it follows " +
             "camera movement after the camera has finished moving for the frame.")]
    public bool faceCamera = true;

    [Header("Radial Fill")]
    [Tooltip("UI Image used as the hold-progress circle.")]
    public Image radialImage;

    [Tooltip("Automatically configures the Image as Filled / Radial 360.")]
    public bool autoConfigureRadialImage = true;

    [Tooltip("Where the radial fill begins. 0=Bottom, 1=Right, 2=Top, 3=Left.")]
    [Range(0, 3)]
    public int radialOrigin = 2;

    [Tooltip("Direction the radial fills.")]
    public bool clockwise = true;

    [Header("Hold")]
    [Min(0.01f)]
    [Tooltip("How long the input must be held before On Completed fires.")]
    public float holdSeconds = 1f;

#if ENABLE_INPUT_SYSTEM
    [Tooltip("Input System action to hold. Assign an InputActionReference in the Inspector, " +
             "for example Interact, Submit, E, gamepad South, etc.")]
    public InputActionReference holdAction;
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
    [Tooltip("Used only when the old Input Manager is enabled.")]
    public KeyCode legacyHoldKey = KeyCode.E;
#endif

    [Tooltip("If the input is released before completion, progress returns to zero.")]
    public bool resetOnRelease = true;

    [Tooltip("If true, this button can only complete once each time it is enabled/shown.")]
    public bool oneShotPerEnable = true;

    [Tooltip("Disable this WorldButton after the hold completes.")]
    public bool disableAfterComplete = false;

    [Header("Hold Feedback")]
    [Tooltip("Animate the visual while the button is being held.")]
    public bool animateWhileHolding = true;

    [Tooltip("Transform to animate. Leave empty to use Visual Root, then this transform as a fallback.")]
    public Transform feedbackTransform;

    [Range(1f, 1.5f)]
    [Tooltip("Scale multiplier reached when the hold reaches 100%.")]
    public float holdGrowMultiplier = 1.10f;

    [Range(0f, 1f)]
    [Tooltip("Normalized hold progress where shaking begins. 0.7 means shake during the final 30% of the hold.")]
    public float shakeStartProgress = 0.70f;

    [Min(0f)]
    [Tooltip("Maximum local-space shake distance reached near completion.")]
    public float shakeAmount = 0.035f;

    [Min(0f)]
    [Tooltip("Shake cycles per second while the hold is in the shake portion.")]
    public float shakeFrequency = 14f;

    [Header("Hold / Cancel Easing")]
    [Range(0f, 1f)]
    [Tooltip("Adds a subtle quadratic feel. 0 is linear, 1 is fully quadratic. " +
             "Holding eases in; cancelling eases back out.")]
    public float quadraticFeel = 0.35f;

    [Tooltip("If released before completion, smoothly return the visual and radial fill instead of snapping.")]
    public bool smoothReturnOnCancel = true;

    [Min(0.01f)]
    [Tooltip("Seconds for the button, shake offset, and radial fill to slide back after a cancelled hold.")]
    public float cancelReturnDuration = 0.18f;

    [Header("Events")]
    [Tooltip("Invoked once the input has been held for Hold Seconds. " +
             "Drag another GameObject here and choose a public method from its script.")]
    public UnityEvent onCompleted;

    [Tooltip("Optional event fired when a new hold begins.")]
    public UnityEvent onHoldStarted;

    [Tooltip("Optional event fired when an incomplete hold is released.")]
    public UnityEvent onHoldCancelled;

    public float HoldProgress => holdSeconds <= 0f ? 1f : Mathf.Clamp01(_holdTime / holdSeconds);
    public bool IsCompleted => _completed;
    public bool IsHolding => _holding;

    float _holdTime;
    bool _holding;
    bool _completed;
    Vector3 _followVelocity;

    Transform _activeFeedbackTransform;
    Vector3 _feedbackBaseLocalPosition;
    Vector3 _feedbackBaseLocalScale;
    bool _feedbackSeeded;

    bool _returningFromCancel;
    float _cancelReturnElapsed;
    Vector3 _cancelStartLocalPosition;
    Vector3 _cancelStartLocalScale;
    float _cancelStartFill;

#if ENABLE_INPUT_SYSTEM
    bool _enabledActionHere;
#endif

    void Awake()
    {
        ConfigureRadial();
        SetFill(0f);
    }

    void OnEnable()
    {
        if (visualRoot)
            visualRoot.SetActive(true);

        if (resetOnEnable)
            ResetButton();

        _followVelocity = Vector3.zero;
        SeedFeedbackTransform();

#if ENABLE_INPUT_SYSTEM
        _enabledActionHere = false;

        if (holdAction != null && holdAction.action != null && !holdAction.action.enabled)
        {
            holdAction.action.Enable();
            _enabledActionHere = true;
        }
#endif
    }

    void OnDisable()
    {
        _returningFromCancel = false;
        RestoreFeedbackTransform();

#if ENABLE_INPUT_SYSTEM
        // Do not disable a shared action that some other system enabled.
        if (_enabledActionHere && holdAction != null && holdAction.action != null)
            holdAction.action.Disable();

        _enabledActionHere = false;
#endif

        _holding = false;

        if (visualRoot)
            visualRoot.SetActive(false);
    }

    void OnValidate()
    {
        holdSeconds = Mathf.Max(0.01f, holdSeconds);
        radialOrigin = Mathf.Clamp(radialOrigin, 0, 3);
        shakeStartProgress = Mathf.Clamp01(shakeStartProgress);
        holdGrowMultiplier = Mathf.Max(1f, holdGrowMultiplier);
        quadraticFeel = Mathf.Clamp01(quadraticFeel);
        cancelReturnDuration = Mathf.Max(0.01f, cancelReturnDuration);

        if (radialImage && autoConfigureRadialImage)
        {
            radialImage.type = Image.Type.Filled;
            radialImage.fillMethod = Image.FillMethod.Radial360;
            radialImage.fillOrigin = radialOrigin;
            radialImage.fillClockwise = clockwise;
        }
    }

    void Update()
    {
        // A completed one-shot waits until Show()/ResetButton()/re-enable.
        if (_completed && oneShotPerEnable)
            return;

        bool pressed = IsHoldPressed();

        if (pressed)
        {
            // If the player presses again while a cancelled hold is easing back,
            // start a fresh hold from the authored resting pose. This prevents the
            // return animation and hold animation from fighting over the same transform.
            if (_returningFromCancel)
            {
                _returningFromCancel = false;
                RestoreFeedbackTransform();

                if (resetOnRelease)
                    SetFill(0f);
            }

            if (!_holding)
            {
                _holding = true;
                SeedFeedbackTransform();
                onHoldStarted?.Invoke();
            }

            _holdTime += Time.unscaledDeltaTime;
            float progress = HoldProgress;

            // The timer stays exact, but the visible response has a subtle
            // quadratic acceleration toward completion.
            float visualProgress = HoldEase(progress);

            SetFill(visualProgress);
            ApplyHoldFeedback(progress, visualProgress);

            if (!_completed && _holdTime >= holdSeconds)
                Complete();
        }
        else
        {
            bool wasIncompleteHold = _holding && !_completed;

            if (wasIncompleteHold)
                onHoldCancelled?.Invoke();

            _holding = false;

            if (wasIncompleteHold && resetOnRelease)
            {
                _holdTime = 0f;

                if (smoothReturnOnCancel)
                    BeginCancelReturn();
                else
                {
                    _returningFromCancel = false;
                    SetFill(0f);
                    RestoreFeedbackTransform();
                }
            }

            if (_returningFromCancel)
                UpdateCancelReturn();

            // If repeats are allowed, releasing after completion arms it again.
            if (_completed && !oneShotPerEnable)
            {
                _completed = false;
                _holdTime = 0f;
                _returningFromCancel = false;
                SetFill(0f);
                RestoreFeedbackTransform();
            }
        }
    }

    void LateUpdate()
    {
        Camera cam = ResolveCamera();

        FollowPoint(cam);

        if (!faceCamera || !cam)
            return;

        Quaternion rotation;

        if (billboardMode == BillboardMode.MatchCameraRotation)
        {
            // Keeps text perfectly level relative to the camera/screen.
            rotation = cam.transform.rotation;
        }
        else
        {
            // World-space Canvas fronts commonly face opposite transform.forward,
            // so forward points from the camera toward this object.
            Vector3 forward = transform.position - cam.transform.position;

            if (forward.sqrMagnitude < 0.000001f)
                return;

            rotation = Quaternion.LookRotation(forward.normalized, cam.transform.up);
        }

        if (flip180)
            rotation *= Quaternion.Euler(0f, 180f, 0f);

        transform.rotation = rotation;
    }


    void FollowPoint(Camera cam)
    {
        if (!followPoint)
            return;

        Vector3 offset;

        switch (followOffsetSpace)
        {
            case FollowOffsetSpace.TargetLocal:
                offset = followPoint.TransformVector(followOffset);
                break;

            case FollowOffsetSpace.CameraLocal:
                offset = cam
                    ? cam.transform.TransformVector(followOffset)
                    : followOffset;
                break;

            default:
                offset = followOffset;
                break;
        }

        Vector3 desired = followPoint.position + offset;

        if (followSmoothTime <= 0f)
        {
            transform.position = desired;
            _followVelocity = Vector3.zero;
        }
        else
        {
            transform.position = Vector3.SmoothDamp(
                transform.position,
                desired,
                ref _followVelocity,
                followSmoothTime);
        }
    }

    Camera ResolveCamera()
    {
        if (targetCamera)
            return targetCamera;

        return Camera.main;
    }

    void ConfigureRadial()
    {
        if (!radialImage || !autoConfigureRadialImage)
            return;

        radialImage.type = Image.Type.Filled;
        radialImage.fillMethod = Image.FillMethod.Radial360;
        radialImage.fillOrigin = radialOrigin;
        radialImage.fillClockwise = clockwise;
    }

    void SetFill(float amount)
    {
        if (radialImage)
            radialImage.fillAmount = Mathf.Clamp01(amount);
    }

    bool IsHoldPressed()
    {
#if ENABLE_INPUT_SYSTEM
        if (holdAction != null && holdAction.action != null)
            return holdAction.action.IsPressed();
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(legacyHoldKey);
#else
        return false;
#endif
    }

    void Complete()
    {
        _holdTime = holdSeconds;
        _completed = true;
        _holding = false;
        _returningFromCancel = false;
        SetFill(1f);

        // Completion is intentionally a crisp snap, unlike a cancelled hold.
        RestoreFeedbackTransform();

        onCompleted?.Invoke();

        if (disableAfterComplete)
            enabled = false;
    }

    void SeedFeedbackTransform()
    {
        Transform wanted = feedbackTransform;
        if (!wanted && visualRoot)
            wanted = visualRoot.transform;
        if (!wanted)
            wanted = transform;

        // Re-seed if the assigned feedback target changed.
        if (_feedbackSeeded && _activeFeedbackTransform == wanted)
            return;

        _activeFeedbackTransform = wanted;

        if (_activeFeedbackTransform)
        {
            _feedbackBaseLocalPosition = _activeFeedbackTransform.localPosition;
            _feedbackBaseLocalScale = _activeFeedbackTransform.localScale;
            _feedbackSeeded = true;
        }
    }

    void ApplyHoldFeedback(float rawProgress, float visualProgress)
    {
        if (!animateWhileHolding)
            return;

        SeedFeedbackTransform();
        if (!_activeFeedbackTransform)
            return;

        rawProgress = Mathf.Clamp01(rawProgress);
        visualProgress = Mathf.Clamp01(visualProgress);

        // Scale follows the eased visual progress, giving the hold a slight
        // "pressure building" feel instead of a perfectly linear growth.
        float scaleMultiplier =
            Mathf.Lerp(1f, holdGrowMultiplier, visualProgress);

        _activeFeedbackTransform.localScale =
            _feedbackBaseLocalScale * scaleMultiplier;

        Vector3 shakeOffset = Vector3.zero;

        // The shake threshold is based on real hold progress so the configured
        // percentage corresponds directly to Hold Seconds.
        if (shakeAmount > 0f && rawProgress >= shakeStartProgress)
        {
            float shakeRange = Mathf.Max(1f - shakeStartProgress, 0.0001f);
            float shakeStrength =
                Mathf.Clamp01((rawProgress - shakeStartProgress) / shakeRange);

            // Give shake strength the same subtle quadratic build-up.
            shakeStrength = HoldEase(shakeStrength);

            float phase = Time.unscaledTime * shakeFrequency * Mathf.PI * 2f;

            // Different frequencies on X and Y avoid a perfectly circular wobble.
            shakeOffset = new Vector3(
                Mathf.Sin(phase),
                Mathf.Sin(phase * 1.37f + 1.1f),
                0f) * (shakeAmount * shakeStrength);
        }

        _activeFeedbackTransform.localPosition =
            _feedbackBaseLocalPosition + shakeOffset;
    }

    float HoldEase(float t)
    {
        t = Mathf.Clamp01(t);

        // Blend linear with t^2 so "Quadratic Feel" can stay subtle rather
        // than forcing a strong ease curve.
        float quadratic = t * t;
        return Mathf.Lerp(t, quadratic, quadraticFeel);
    }

    float ReturnEase(float t)
    {
        t = Mathf.Clamp01(t);

        // Quadratic ease-out: moves decisively toward rest, then settles.
        float quadratic = 1f - (1f - t) * (1f - t);
        return Mathf.Lerp(t, quadratic, quadraticFeel);
    }

    void BeginCancelReturn()
    {
        SeedFeedbackTransform();

        _returningFromCancel = true;
        _cancelReturnElapsed = 0f;

        if (_activeFeedbackTransform)
        {
            _cancelStartLocalPosition = _activeFeedbackTransform.localPosition;
            _cancelStartLocalScale = _activeFeedbackTransform.localScale;
        }

        _cancelStartFill = radialImage ? radialImage.fillAmount : 0f;
    }

    void UpdateCancelReturn()
    {
        if (!_returningFromCancel)
            return;

        _cancelReturnElapsed += Time.unscaledDeltaTime;

        float t = cancelReturnDuration <= 0f
            ? 1f
            : Mathf.Clamp01(_cancelReturnElapsed / cancelReturnDuration);

        float eased = ReturnEase(t);

        if (_activeFeedbackTransform)
        {
            _activeFeedbackTransform.localPosition =
                Vector3.LerpUnclamped(
                    _cancelStartLocalPosition,
                    _feedbackBaseLocalPosition,
                    eased);

            _activeFeedbackTransform.localScale =
                Vector3.LerpUnclamped(
                    _cancelStartLocalScale,
                    _feedbackBaseLocalScale,
                    eased);
        }

        if (radialImage)
            radialImage.fillAmount =
                Mathf.LerpUnclamped(_cancelStartFill, 0f, eased);

        if (t >= 1f)
        {
            _returningFromCancel = false;
            RestoreFeedbackTransform();
            SetFill(0f);
        }
    }

    void RestoreFeedbackTransform()
    {
        if (!_feedbackSeeded || !_activeFeedbackTransform)
            return;

        _activeFeedbackTransform.localPosition = _feedbackBaseLocalPosition;
        _activeFeedbackTransform.localScale = _feedbackBaseLocalScale;
    }

    /// <summary>
    /// Shows/enables the WorldButton. Intended to be called by another script
    /// or from a UnityEvent.
    /// </summary>
    public void Show()
    {
        if (visualRoot)
            visualRoot.SetActive(true);

        if (!enabled)
        {
            enabled = true; // OnEnable handles reset/action setup.
        }
        else
        {
            ResetButton();
        }
    }

    /// <summary>
    /// Hides/disables the WorldButton. Intended to be called by another script
    /// or from a UnityEvent.
    /// </summary>
    public void Hide()
    {
        enabled = false;

        // OnDisable hides visualRoot when one is assigned. If no visual root is
        // assigned, disabling only stops the behaviour and leaves this GameObject visible.
    }

    /// <summary>
    /// Convenience method for scripts that already have a bool.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    /// <summary>
    /// Clears hold state and returns the radial image to zero.
    /// </summary>
    public void ResetButton()
    {
        _holdTime = 0f;
        _holding = false;
        _completed = false;
        _returningFromCancel = false;
        SetFill(0f);
        RestoreFeedbackTransform();
    }

    /// <summary>
    /// Immediately completes the button from another script.
    /// </summary>
    public void ForceComplete()
    {
        if (!_completed)
            Complete();
    }
}

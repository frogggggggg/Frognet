using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;

/// <summary>
/// Hold prompt pinned over a point in the world (the focus drill above the player): a keycap with the
/// bound key ("F", from the input action), a progress ring round it that fills while the key is held,
/// a slowly turning dial outside that, and a typed label beside it ("HOLD // INJECT", then the progress
/// while held), in the focus-mode terminal look (TerminalUI). Pops in when shown, fades out when
/// hidden; the ring grows and shakes near the end, and bursts on completion. Drawn on its own
/// screen-space canvas at the projected point, so it's always upright and crisp.
///
/// A new hold needs a fresh press: a key still down when it's shown (e.g. the press that left focus)
/// doesn't start one. Show() / Hide() / SetVisible(); onCompleted fires once the hold is long enough.
/// Builds itself; the old world-space visual (visualRoot) is switched off.
/// </summary>
[DefaultExecutionOrder(1000)] // LateUpdate after the ticker re-pins the player to its (moving) cell and the camera moves: else it stuttered
public class WorldButton : MonoBehaviour
{
    public enum FollowOffsetSpace { World, TargetLocal, CameraLocal }

    [Tooltip("Old world-space visual: hidden (the button draws its own).")]
    public GameObject visualRoot;

    [Header("Follow")]
    [Tooltip("World point / object the prompt hangs over.")]
    public Transform followPoint;
    public Vector3 followOffset = Vector3.zero;
    public FollowOffsetSpace followOffsetSpace = FollowOffsetSpace.World;
    [Tooltip("Optional camera override. Empty: Camera.main.")]
    public Camera targetCamera;

    [Header("Hold")]
    [Min(0.01f), Tooltip("How long the input must be held before On Completed fires.")]
    public float holdSeconds = 1f;
    [Tooltip("Input action to hold; its binding is shown on the keycap.")]
    public InputActionReference holdAction;
    [Tooltip("Used when there's no action.")]
    public Key holdKey = Key.F;
    [Tooltip("If true, this button can only complete once each time it is shown.")]
    public bool oneShotPerEnable = true;

    [Header("Look")]
    public string label = "INJECT";
    [Tooltip("Shown while held, with the progress.")]
    public string holdingLabel = "DRILLING";
    [Min(16f), Tooltip("Keycap size, in 1080p pixels.")]
    public float size = 38f;
    [Range(1f, 1.5f), Tooltip("Scale reached at 100%.")]
    public float holdGrowMultiplier = 1.12f;
    [Range(0f, 1f), Tooltip("Progress where shaking starts.")]
    public float shakeStartProgress = 0.7f;
    [Min(0f), Tooltip("Shake at completion, in 1080p pixels.")]
    public float shakePixels = 2.5f;
    [Min(0f)] public float shakeFrequency = 14f;
    [Range(0f, 1f), Tooltip("0 linear, 1 quadratic: the ring fills slowly at first, then quicker.")]
    public float quadraticFeel = 0.35f;
    [Min(0.01f), Tooltip("Seconds for the ring to drain after a let-go hold.")]
    public float cancelReturnDuration = 0.18f;
    [Min(1f)] public float typeSpeed = 60f;

    [Header("Events")]
    public UnityEvent onCompleted;
    public UnityEvent onHoldStarted;
    public UnityEvent onHoldCancelled;

    public float HoldProgress => holdSeconds <= 0f ? 1f : Mathf.Clamp01(_holdTime / holdSeconds);
    public bool IsCompleted => _completed;
    public bool IsHolding => _holding;
    public bool IsVisible => _visible;
    /// <summary>Every enabled button (HoldTickAudio reads them).</summary>
    public static readonly System.Collections.Generic.List<WorldButton> All = new System.Collections.Generic.List<WorldButton>();

    float _holdTime, _fill, _drainFrom, _drainAt = -10f, _shownAt = -10f, _doneAt = -10f, _fade, _spin;
    bool _holding, _completed, _visible = true, _armed, _enabledAction;

    Canvas _canvas;
    RectTransform _canvasRect, _root, _cap, _tag;
    CanvasGroup _group;
    Image _ring, _track, _dial, _burst, _capFill, _capFrame, _tagFill, _tagFrame;
    Text _key;
    Typed _text;
    readonly System.Collections.Generic.List<Object> _made = new System.Collections.Generic.List<Object>();

    void Awake()
    {
        if (visualRoot) visualRoot.SetActive(false);
    }

    void OnEnable()
    {
        All.Add(this);
        _enabledAction = false;
        if (holdAction && holdAction.action != null && !holdAction.action.enabled)
        {
            holdAction.action.Enable();
            _enabledAction = true;
        }
    }

    void OnDisable()
    {
        All.Remove(this);
        if (_enabledAction && holdAction && holdAction.action != null) holdAction.action.Disable();
        _enabledAction = false;
        _holding = false;
        if (_canvas) _canvas.enabled = false;
    }

    void OnDestroy()
    {
        if (_canvas) Destroy(_canvas.gameObject);
        foreach (Object o in _made) if (o) Destroy(o);
    }

    public void Show()
    {
        if (!enabled) enabled = true;
        if (visualRoot && visualRoot.activeSelf) visualRoot.SetActive(false);
        if (!_visible && _fade <= 0f) _shownAt = Time.unscaledTime; // still fading out: carry on from there, no second pop
        _visible = true;
        ResetButton();
    }

    public void Hide()
    {
        _visible = false;
        _holding = false;
    }

    public void SetVisible(bool visible)
    {
        if (visible) Show();
        else Hide();
    }

    /// <summary>Clears the hold; the next one needs a fresh press.</summary>
    public void ResetButton()
    {
        _holdTime = 0f;
        _holding = _completed = false;
        _armed = !Pressed();
        _fill = 0f;
        _drainAt = -10f;
    }

    public void ForceComplete()
    {
        if (!_completed) Complete();
    }

    bool Pressed()
    {
        if (holdAction && holdAction.action != null) return holdAction.action.IsPressed();
        Keyboard k = Keyboard.current;
        return k != null && holdKey != Key.None && k[holdKey].isPressed;
    }

    float Ease(float t) => Mathf.Lerp(t, t * t, quadraticFeel);

    void Update()
    {
        if (!_visible) return;
        bool pressed = Pressed();
        if (!pressed) _armed = true;
        if (_completed && oneShotPerEnable) return;

        if (pressed && _armed)
        {
            if (!_holding)
            {
                _holding = true;
                _drainAt = -10f;
                onHoldStarted?.Invoke();
            }
            _holdTime += Time.unscaledDeltaTime;
            _fill = Ease(HoldProgress);
            if (!_completed && _holdTime >= holdSeconds) Complete();
            return;
        }

        if (_holding && !_completed)
        {
            onHoldCancelled?.Invoke();
            _drainFrom = _fill;
            _drainAt = Time.unscaledTime;
        }
        _holding = false;
        _holdTime = 0f;
        if (_completed && !oneShotPerEnable) { _completed = false; _fill = 0f; }
        if (_drainAt >= 0f)
        {
            float t = Mathf.Clamp01((Time.unscaledTime - _drainAt) / cancelReturnDuration);
            _fill = Mathf.Lerp(_drainFrom, 0f, 1f - (1f - t) * (1f - t));
        }
    }

    void Complete()
    {
        _holdTime = holdSeconds;
        _completed = true;
        _holding = false;
        _fill = 1f;
        _doneAt = Time.unscaledTime;
        onCompleted?.Invoke();
    }

    // ---------------- drawing ----------------

    void LateUpdate()
    {
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;
        _fade = Mathf.MoveTowards(_fade, _visible ? 1f : 0f, dt / (_visible ? 0.12f : 0.2f));
        if (_fade <= 0f)
        {
            if (_canvas && _canvas.enabled) _canvas.enabled = false;
            return;
        }
        if (!_canvas || _text == null) Build(); // a play-mode script reload drops the plain parts
        Camera cam = targetCamera ? targetCamera : Camera.main;
        if (!cam || !followPoint) { _canvas.enabled = false; return; }

        Vector3 offset = followOffsetSpace == FollowOffsetSpace.TargetLocal ? followPoint.TransformVector(followOffset)
                       : followOffsetSpace == FollowOffsetSpace.CameraLocal ? cam.transform.TransformVector(followOffset)
                       : followOffset;
        transform.position = followPoint.position + offset;
        Vector3 screen = cam.WorldToScreenPoint(transform.position);
        if (screen.z <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 at))
        {
            _canvas.enabled = false;
            return;
        }
        _canvas.enabled = true;

        // Pop in, grow and shake as it fills, a burst when it completes.
        float pop = BackOut(Mathf.Clamp01((now - _shownAt) / 0.28f));
        float p = _fill;
        float shake = p >= shakeStartProgress && _holding
            ? Ease(Mathf.Clamp01((p - shakeStartProgress) / Mathf.Max(1f - shakeStartProgress, 1e-3f))) * shakePixels : 0f;
        float phase = now * shakeFrequency * Mathf.PI * 2f;
        _root.anchoredPosition = at + new Vector2(Mathf.Sin(phase), Mathf.Sin(phase * 1.37f + 1.1f)) * shake;
        _root.localScale = Vector3.one * (Mathf.Lerp(0.6f, 1f, pop) * (_visible ? 1f : Mathf.Lerp(0.85f, 1f, _fade)));
        _group.alpha = _fade;
        _cap.localScale = Vector3.one * Mathf.Lerp(1f, holdGrowMultiplier, p);

        Color line = TerminalUI.Line, live = TerminalUI.Live;
        Color lit = Color.Lerp(line, live, p);
        _ring.fillAmount = p;
        _ring.color = lit;
        _capFrame.color = Color.Lerp(new Color(line.r, line.g, line.b, 0.9f), live, p);
        _key.color = Color.Lerp(TerminalUI.Text, live, p);
        _spin += dt * Mathf.Lerp(18f, 400f, p * p);
        _dial.rectTransform.localEulerAngles = new Vector3(0f, 0f, -_spin);
        _dial.color = new Color(lit.r, lit.g, lit.b, Mathf.Lerp(0.3f, 0.8f, p));

        float done = Mathf.Clamp01((now - _doneAt) / 0.4f);
        _burst.enabled = _completed && done < 1f;
        if (_burst.enabled)
        {
            _burst.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, 2.4f, 1f - (1f - done) * (1f - done));
            _burst.color = new Color(live.r, live.g, live.b, 1f - done);
        }

        // The label: "HOLD // INJECT", the progress while held, the label lit once done.
        if (_completed) _text.Set(label, false);
        else if (_holding) _text.Set(holdingLabel + "  " + Mathf.FloorToInt(HoldProgress * 100f).ToString("00") + "%", false);
        else _text.Set("HOLD // " + label);
        _text.Tick(now, typeSpeed);
        _text.text.color = _completed || _holding ? live : TerminalUI.Text;
        _tagFrame.color = _completed || _holding ? live : new Color(line.r, line.g, line.b, 0.8f);
        _tag.sizeDelta = new Vector2(Mathf.Max(40f, _text.text.preferredWidth + 26f), 24f);
    }

    static float BackOut(float x)
    {
        const float c = 1.7f;
        x -= 1f;
        return 1f + (c + 1f) * x * x * x + c * x * x;
    }

    void Build()
    {
        if (_canvas) Destroy(_canvas.gameObject);
        foreach (Object o in _made) if (o) Destroy(o);
        _made.Clear();

        Font font = TerminalUI.Font(TerminalUI.DefaultFonts);
        _canvas = TerminalUI.Canvas("World Button Canvas", transform, 570);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // read only
        _canvasRect = (RectTransform)_canvas.transform;
        _root = TerminalUI.Rect("Prompt", _canvasRect, Vector2.zero, Vector2.one * size);
        _group = _root.gameObject.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = _group.interactable = false;

        Sprite ring = Made(TerminalUI.RingSprite(7f)), dial = Made(TerminalUI.DialSprite(true));
        Sprite disc = Made(TerminalUI.DiscSprite(false)), rim = Made(TerminalUI.DiscSprite(true));
        Sprite chamfer = Made(TerminalUI.ChamferSprite(false)), frame = Made(TerminalUI.ChamferSprite(true));

        _dial = Img("Dial", _root, dial, size * 2f, TerminalUI.Line);
        _track = Img("Track", _root, ring, size * 1.5f, new Color(TerminalUI.Line.r, TerminalUI.Line.g, TerminalUI.Line.b, 0.18f));
        _ring = Img("Progress", _root, ring, size * 1.5f, TerminalUI.Line);
        _ring.type = Image.Type.Filled;
        _ring.fillMethod = Image.FillMethod.Radial360;
        _ring.fillOrigin = (int)Image.Origin360.Top;
        _ring.fillClockwise = true;
        _ring.fillAmount = 0f;
        _burst = Img("Burst", _root, ring, size * 1.5f, TerminalUI.Live);
        _burst.enabled = false;

        _cap = TerminalUI.Rect("Keycap", _root, Vector2.zero, Vector2.one * size);
        _capFill = Img("Fill", _cap, disc, size, TerminalUI.Panel);
        _capFrame = Img("Frame", _cap, rim, size, TerminalUI.Line);
        _key = TerminalUI.Graphic<Text>("Key", _cap, Vector2.zero, Vector2.one * size);
        TerminalUI.Style(_key, font, Mathf.RoundToInt(size * 0.5f), TerminalUI.Text, TextAnchor.MiddleCenter);
        _key.text = KeyName();

        // The label: a chamfered tag to the right of the ring.
        _tag = TerminalUI.Rect("Tag", _root, new Vector2(size * 1.05f, 0f), new Vector2(120f, 24f));
        _tag.pivot = new Vector2(0f, 0.5f);
        _tagFill = _tag.gameObject.AddComponent<Image>();
        _tagFill.sprite = chamfer;
        _tagFill.type = Image.Type.Sliced;
        _tagFill.color = TerminalUI.Panel;
        _tagFill.raycastTarget = false;
        _tagFrame = TerminalUI.Graphic<Image>("Frame", _tag, Vector2.zero, Vector2.zero);
        Stretch(_tagFrame.rectTransform);
        _tagFrame.sprite = frame;
        _tagFrame.type = Image.Type.Sliced;
        _tagFrame.raycastTarget = false;
        Text t = TerminalUI.Graphic<Text>("Label", _tag, Vector2.zero, Vector2.zero);
        Stretch(t.rectTransform);
        t.rectTransform.offsetMin = new Vector2(13f, 0f);
        TerminalUI.Style(t, font, 13, TerminalUI.Text, TextAnchor.MiddleLeft);
        _text = new Typed { text = t, start = Time.unscaledTime };
    }

    string KeyName()
    {
        if (holdAction && holdAction.action != null)
        {
            string s = holdAction.action.GetBindingDisplayString();
            if (!string.IsNullOrEmpty(s)) return s.ToUpperInvariant();
        }
        return holdKey.ToString().ToUpperInvariant();
    }

    Sprite Made(Sprite s)
    {
        _made.Add(s.texture);
        _made.Add(s);
        return s;
    }

    static Image Img(string name, RectTransform parent, Sprite sprite, float size, Color color)
    {
        Image i = TerminalUI.Graphic<Image>(name, parent, Vector2.zero, Vector2.one * size);
        i.sprite = sprite;
        i.color = color;
        i.raycastTarget = false;
        return i;
    }

    static void Stretch(RectTransform t)
    {
        t.anchorMin = Vector2.zero;
        t.anchorMax = Vector2.one;
        t.offsetMin = t.offsetMax = Vector2.zero;
    }
}

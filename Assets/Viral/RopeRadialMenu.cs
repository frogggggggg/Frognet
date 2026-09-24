using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;

/// <summary>
/// Radial menu for editing one rope, opened on a rope base in focus mode (VirusMovement).
/// Styled as a bio-lab terminal to match the focus sweep: a segmented dial turning on the
/// base, callout leaders out to chamfered, scanlined panels, monospace text that types itself
/// in. Options: a Straighten toggle, a Length field (type a length, the rope eases to it; a
/// straightened rod pushes its bodies to fit) and Cauterize (a rod only: blood pumps up it from
/// this base, then it seals into a static strut welding the two bodies; locks the other two).
///
/// Builds its own screen-space canvas and sprites when first used (TerminalUI); nothing to set up. Talks to
/// the rope only through VirusRope's handle API (IsBase, BasePoint, IsStraight, ToggleStraight,
/// LengthOf, SetLength, CanCauterize, Cauterize, CauterizeProgress), so more options are just
/// more panels.
/// </summary>
public class RopeRadialMenu : MonoBehaviour
{
    [Tooltip("Seconds to open or close.")]
    [Min(0.01f)] public float animationTime = 0.18f;
    [Tooltip("Characters typed per second as text appears or changes.")]
    [Min(1f)] public float typeSpeed = 90f;

    [Header("Colours (match the focus sweep)")]
    public Color panel = TerminalUI.Panel;
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;
    [Tooltip("Cauterize: blood pumping.")]
    public Color blood = TerminalUI.Blood;

    [Header("Type")]
    [Tooltip("Any font asset (.ttf/.otf). Empty: the first installed of Terminal Fonts.")]
    public Font font;
    [Tooltip("OS fonts tried in order when Font is empty.")]
    public string[] terminalFonts = TerminalUI.DefaultFonts;

    public bool IsOpen => _open;
    public int Handle { get; private set; } = -1;

    const float PanelWidth = 184f, PanelHeight = 56f;

    VirusRope _rope;
    Camera _camera;
    bool _open;
    float _shown; // 0 closed .. 1 open
    float _openedAt;

    Canvas _canvas;
    RectTransform _canvasRect, _root, _dialOuter, _dialInner;
    CanvasGroup _group;
    Font _font;
    readonly List<Sprite> _sprites = new List<Sprite>();
    Sprite _fill, _frame, _outerRing, _innerRing;
    Texture2D _scan;
    readonly List<RawImage> _scanlines = new List<RawImage>();

    struct Callout { public RectTransform panel; public Vector2 from, to; public CanvasGroup group; }
    readonly List<Callout> _callouts = new List<Callout>();

    // Text that types itself in (on open, and whenever its content changes).
    readonly List<Typed> _typed = new List<Typed>();

    Image _straightFrame, _sealFrame;
    Typed _straightState, _readout, _lengthReadout, _sealState;
    InputField _length;
    Button _straightButton, _sealButton;

    public static RopeRadialMenu Create() => new GameObject("Rope Menu").AddComponent<RopeRadialMenu>();

    public void Open(VirusRope rope, int handle, Camera cam)
    {
        Build();
        bool fresh = !_open || handle != Handle || rope != _rope;
        _rope = rope;
        _camera = cam;
        Handle = handle;
        _open = true;
        _canvas.gameObject.SetActive(true);
        if (fresh)
        {
            _openedAt = Time.unscaledTime;
            foreach (Typed t in _typed) t.start = _openedAt;
        }
        Refresh(force: true);
    }

    public void Close()
    {
        _open = false;
        if (_length && _length.isFocused) _length.DeactivateInputField();
    }

    void LateUpdate()
    {
        if (!_canvas) return;
        if (_open && (!_rope || !_rope.IsBase(Handle))) Close(); // cut, reeled in or pulled up

        _shown = Mathf.MoveTowards(_shown, _open ? 1f : 0f, Time.unscaledDeltaTime / animationTime);
        if (_shown <= 0f && !_open)
        {
            if (_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            return;
        }

        // Follow the base. Behind the camera: hidden, not clickable.
        Camera cam = _camera ? _camera : Camera.main;
        Vector3 screen = cam && _rope ? cam.WorldToScreenPoint(_rope.BasePoint(Handle)) : Vector3.zero;
        bool visible = screen.z > 0f;
        if (visible && RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local))
            _root.anchoredPosition = local;

        float now = Time.unscaledTime;
        float e = 1f - (1f - _shown) * (1f - _shown); // ease out

        // Signal flicker for a moment after opening.
        float since = now - _openedAt;
        float flicker = _open && since < 0.22f ? (Mathf.PerlinNoise(now * 60f, 0.5f) > 0.35f ? 1f : 0.35f) : 1f;

        _group.alpha = visible ? e * flicker : 0f;
        _group.interactable = _group.blocksRaycasts = visible && _open;

        // Dial: settles in from larger, and keeps turning.
        _dialOuter.localScale = Vector3.one * Mathf.Lerp(1.6f, 1f, e);
        _dialOuter.localRotation = Quaternion.Euler(0f, 0f, now * -25f);
        _dialInner.localRotation = Quaternion.Euler(0f, 0f, now * 40f);

        // Panels slide out along their leaders, one after the other.
        for (int i = 0; i < _callouts.Count; i++)
        {
            Callout c = _callouts[i];
            float k = Mathf.Clamp01(e * 1.25f - i * 0.25f);
            k = 1f - (1f - k) * (1f - k);
            c.panel.anchoredPosition = Vector2.Lerp(c.from, c.to, k);
            c.group.alpha = k;
        }

        // Scanlines crawl.
        foreach (RawImage s in _scanlines)
        {
            Rect uv = s.uvRect;
            uv.y = -now * 0.6f;
            s.uvRect = uv;
        }

        Refresh(force: false);

        foreach (Typed t in _typed) t.Tick(now, typeSpeed);
    }

    void Refresh(bool force)
    {
        if (!_rope) return;
        bool straight = _rope.IsStraight(Handle);
        float sealing = _rope.CauterizeProgress(Handle); // -1 none, 0..1 pumping, 1 sealed
        bool locked = sealing >= 0f;
        _straightState.Set(straight ? "[#] RIGID" : "[ ] SLACK");
        _straightState.text.color = straight ? live : text;
        _straightFrame.color = straight ? live : line;
        _straightButton.interactable = !locked;
        _length.interactable = !locked;

        // Cauterize: needs a rod; then a blood bar climbing to sealed.
        bool can = _rope.CanCauterize(Handle);
        if (sealing >= 1f) _sealState.Set("[#] SEALED");
        else if (locked)
        {
            int bars = Mathf.RoundToInt(sealing * 8f);
            _sealState.Set("[" + new string('|', bars) + new string('.', 8 - bars) + "] " +
                            Mathf.RoundToInt(sealing * 100f).ToString("00") + "%", retype: false);
        }
        else _sealState.Set(can ? "[>] PUMP" : "[-] NEED RIGID");
        Color sealColor = sealing >= 1f ? live : locked ? blood : can ? text : new Color(text.r, text.g, text.b, 0.35f);
        _sealState.text.color = sealColor;
        _sealFrame.color = sealing >= 1f ? live : locked ? blood : can ? line : new Color(line.r, line.g, line.b, 0.35f);
        _sealButton.interactable = can;

        float length = _rope.LengthOf(Handle);
        _readout.Set("STRAND " + (Handle >> 1).ToString("X2") + ((Handle & 1) == 1 ? ".B" : ".A"));
        _lengthReadout.Set(length.ToString("0.0", CultureInfo.InvariantCulture) + " M");

        if (force || !_length.isFocused)
            _length.text = length.ToString("0.0", CultureInfo.InvariantCulture);
    }

    void OnToggleStraight()
    {
        if (_rope) _rope.ToggleStraight(Handle);
    }

    void OnCauterize()
    {
        if (_rope) _rope.Cauterize(Handle);
    }

    void OnLengthEntered(string value)
    {
        if (_rope && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float meters) && meters > 0f)
            _rope.SetLength(Handle, meters);
        Refresh(force: true);
    }

    // ---------------- building ----------------

    void Build()
    {
        if (_canvas) return;
        _font = font ? font : TerminalUI.Font(terminalFonts);
        _fill = Keep(TerminalUI.ChamferSprite(frame: false));
        _frame = Keep(TerminalUI.ChamferSprite(frame: true));
        _outerRing = Keep(TerminalUI.DialSprite(outer: true));
        _innerRing = Keep(TerminalUI.DialSprite(outer: false));
        _scan = TerminalUI.ScanTexture();

        _canvas = TerminalUI.Canvas("Rope Menu Canvas", transform, 600);
        _canvasRect = (RectTransform)_canvas.transform;

        _root = TerminalUI.Rect("Menu", _canvasRect, Vector2.zero, Vector2.zero);
        _group = _root.gameObject.AddComponent<CanvasGroup>();

        // Dial on the base: segmented outer ring, gapped inner ring, a diamond at the centre.
        Image outer = TerminalUI.Graphic<Image>("Dial", _root, Vector2.zero, new Vector2(78f, 78f));
        outer.sprite = _outerRing;
        outer.color = line;
        outer.raycastTarget = false;
        _dialOuter = outer.rectTransform;
        Image inner = TerminalUI.Graphic<Image>("Dial Inner", _root, Vector2.zero, new Vector2(46f, 46f));
        inner.sprite = _innerRing;
        inner.color = new Color(line.r, line.g, line.b, 0.7f);
        inner.raycastTarget = false;
        _dialInner = inner.rectTransform;
        Image pip = TerminalUI.Graphic<Image>("Pip", _root, Vector2.zero, new Vector2(6f, 6f));
        pip.color = line;
        pip.raycastTarget = false;
        pip.rectTransform.localRotation = Quaternion.Euler(0f, 0f, 45f);

        // Readout under the dial.
        _readout = Label(_root, new Vector2(0f, -54f), 12, new Color(line.r, line.g, line.b, 0.8f), TextAnchor.MiddleCenter, 0.05f);
        _lengthReadout = Label(_root, new Vector2(0f, -70f), 12, new Color(line.r, line.g, line.b, 0.5f), TextAnchor.MiddleCenter, 0.1f);

        // Straighten: upper left.
        RectTransform straight = Panel(-1f, "01 // STRAIGHTEN", 0.12f, out _straightFrame);
        _straightButton = ButtonOn(straight, OnToggleStraight);
        _straightState = Label(straight, new Vector2(14f, -9f), 18, text, TextAnchor.MiddleLeft, 0.2f);

        // Length: upper right.
        RectTransform lengthPanel = Panel(1f, "02 // LENGTH", 0.2f, out _);
        Label(lengthPanel, new Vector2(14f, -9f), 18, line, TextAnchor.MiddleLeft, 0.28f).full = ">";

        Image field = TerminalUI.Graphic<Image>("Field", lengthPanel, Vector2.zero, new Vector2(100f, 24f));
        RectTransform fieldRect = field.rectTransform;
        fieldRect.pivot = new Vector2(0f, 0.5f);
        fieldRect.anchorMin = fieldRect.anchorMax = new Vector2(0f, 0.5f);
        fieldRect.anchoredPosition = new Vector2(30f, -9f);
        field.color = new Color(line.r, line.g, line.b, 0.08f);
        _length = field.gameObject.AddComponent<InputField>();
        _length.targetGraphic = field;
        _length.colors = TerminalUI.Tints();
        _length.contentType = InputField.ContentType.DecimalNumber;
        _length.characterLimit = 7;
        Text value = TerminalUI.Graphic<Text>("Value", fieldRect, Vector2.zero, new Vector2(92f, 24f));
        TerminalUI.Style(value, _font, 18, text, TextAnchor.MiddleLeft);
        value.supportRichText = false;
        _length.textComponent = value;
        _length.caretColor = live;
        _length.customCaretColor = true;
        _length.caretWidth = 4;
        _length.selectionColor = new Color(live.r, live.g, live.b, 0.35f);
        _length.onEndEdit.AddListener(OnLengthEntered);
        Label(lengthPanel, new Vector2(-14f, -9f), 14, line, TextAnchor.MiddleRight, 0.3f).full = "M";

        // Cauterize: below, under the readouts.
        RectTransform seal = Panel(0f, "03 // CAUTERIZE", 0.28f, out _sealFrame);
        _sealButton = ButtonOn(seal, OnCauterize);
        _sealState = Label(seal, new Vector2(14f, -9f), 18, text, TextAnchor.MiddleLeft, 0.36f);

        _canvas.gameObject.SetActive(false);
    }

    Button ButtonOn(RectTransform panelRect, UnityEngine.Events.UnityAction onClick)
    {
        var button = panelRect.gameObject.AddComponent<Button>();
        button.targetGraphic = panelRect.GetComponent<Image>();
        button.colors = TerminalUI.Tints();
        button.onClick.AddListener(onClick);
        return button;
    }

    // A callout on one side (-1 left, +1 right): a leader from the dial up and out to an elbow,
    // then level to a chamfered panel with a scanlined fill, a frame and a title. 0: straight
    // down past the readouts, the panel hanging below.
    RectTransform Panel(float side, string title, float delay, out Image frame)
    {
        Vector2 dir = new Vector2(side * 0.866f, 0.5f);
        Vector2 start = dir * 42f, elbow = dir * 92f, end = elbow + new Vector2(side * 36f, 0f);
        if (side == 0f) { start = new Vector2(0f, -80f); elbow = new Vector2(0f, -92f); end = new Vector2(0f, -104f); }

        var group = TerminalUI.Rect("Callout", _root, Vector2.zero, Vector2.zero).gameObject.AddComponent<CanvasGroup>();
        RectTransform holder = (RectTransform)group.transform;
        Line(holder, start, elbow);
        Line(holder, elbow, end);
        Node(holder, elbow, 5f);
        Node(holder, end, 5f);

        Image fill = TerminalUI.Graphic<Image>("Panel", holder, end, new Vector2(PanelWidth, PanelHeight));
        fill.sprite = _fill;
        fill.type = Image.Type.Sliced;
        fill.color = panel;
        RectTransform rect = fill.rectTransform;
        rect.pivot = side == 0f ? new Vector2(0.5f, 1f) : new Vector2(side < 0f ? 1f : 0f, 0.5f);

        RawImage scan = TerminalUI.Graphic<RawImage>("Scan", rect, Vector2.zero, new Vector2(PanelWidth - TerminalUI.Chamfer * 2f, PanelHeight - 4f));
        scan.texture = _scan;
        scan.uvRect = new Rect(0f, 0f, 1f, (PanelHeight - 4f) / 3f);
        scan.color = Color.white;
        scan.raycastTarget = false;
        _scanlines.Add(scan);

        frame = TerminalUI.Graphic<Image>("Frame", rect, Vector2.zero, new Vector2(PanelWidth, PanelHeight));
        frame.sprite = _frame;
        frame.type = Image.Type.Sliced;
        frame.color = line;
        frame.raycastTarget = false;

        Typed head = Label(rect, new Vector2(14f, 12f), 11, new Color(line.r, line.g, line.b, 0.65f), TextAnchor.MiddleLeft, delay);
        head.full = title;

        _callouts.Add(new Callout { panel = rect, from = elbow, to = end, group = group });
        return rect;
    }

    void Line(RectTransform parent, Vector2 from, Vector2 to)
    {
        Vector2 d = to - from;
        Image l = TerminalUI.Graphic<Image>("Leader", parent, (from + to) * 0.5f, new Vector2(d.magnitude, 1.5f));
        l.color = new Color(line.r, line.g, line.b, 0.7f);
        l.raycastTarget = false;
        l.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    void Node(RectTransform parent, Vector2 at, float size)
    {
        Image n = TerminalUI.Graphic<Image>("Node", parent, at, new Vector2(size, size));
        n.color = line;
        n.raycastTarget = false;
    }

    // In a panel: stretched across it, inset by |at.x| each side, at.y up from its middle.
    // On the root (no size): a fixed-width line centred at at.y.
    Typed Label(RectTransform parent, Vector2 at, int size, Color color, TextAnchor anchor, float delay)
    {
        Text t = TerminalUI.Graphic<Text>("Label", parent, Vector2.zero, Vector2.zero);
        RectTransform r = t.rectTransform;
        if (parent == _root)
            r.sizeDelta = new Vector2(220f, size + 8f);
        else
        {
            r.anchorMin = new Vector2(0f, 0.5f);
            r.anchorMax = new Vector2(1f, 0.5f);
            r.sizeDelta = new Vector2(-Mathf.Abs(at.x) * 2f, size + 8f);
        }
        r.anchoredPosition = new Vector2(0f, at.y);
        TerminalUI.Style(t, _font, size, color, anchor);

        var typed = new Typed { text = t, delay = delay, start = Time.unscaledTime };
        _typed.Add(typed);
        return typed;
    }

    // ---------------- generated art ----------------

    Sprite Keep(Sprite s)
    {
        _sprites.Add(s);
        return s;
    }

    void OnDestroy()
    {
        foreach (Sprite s in _sprites)
            if (s) { Destroy(s.texture); Destroy(s); }
        if (_scan) Destroy(_scan);
    }
}

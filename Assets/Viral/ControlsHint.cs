using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;

/// <summary>
/// Bottom-right terminal panel listing the controls for what the player is doing right now
/// (flying, on a surface, focus, seized by a white blood cell), retyping itself when that changes. H folds it to its title.
/// Inputs are drawn as prompt icons like other games: keys in circles (pills when wide), mouse
/// buttons as a little mouse with that button lit; a double click is the icon twice.
///
/// The lists are plain data below: "[KEY]" is an icon (LMB, RMB, MMB, MOUSE, or a key's label),
/// any other word is a small tag beside the icons (HOLD, TAP...). Reads the player's VirusMovement.
/// Nothing to set up: one is created when play starts if the scene has none. Add the component
/// yourself (anywhere) to change its settings; that one is used instead.
/// </summary>
public class ControlsHint : MonoBehaviour
{
    [Tooltip("Distance from the bottom-right corner, in 1080p pixels.")]
    public Vector2 margin = new Vector2(24f, 24f);
    [Min(120f)] public float width = 330f;
    [Min(1f)] public float typeSpeed = 120f;
    public Key toggleKey = Key.H;
    public bool startFolded;

    [Header("Colours (match the focus sweep)")]
    public Color panel = TerminalUI.Panel;
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;

    [Header("Type")]
    public Font font;
    public string[] terminalFonts = TerminalUI.DefaultFonts;

    // Input, action.
    static readonly string[][] Flying =
    {
        new[] { "[W][A][S][D]", "FLY" }, new[] { "[MOUSE]", "LOOK" }, new[] { "[SPACE]", "BURST" },
        new[] { "[LMB]", "PAY OUT ROPE / GRAB END" }, new[] { "HOLD [RMB]", "REEL IN" },
        new[] { "[RMB]", "CUT ROPE" }, new[] { "[RMB][RMB]", "TUG / SLURP" }, new[] { "[E]", "INVENTORY" },
        new[] { "[Q]", "COMMAND MODE" },
    };
    static readonly string[][] Grounded =
    {
        new[] { "[W][A][S][D]", "CRAWL" }, new[] { "[MOUSE]", "LOOK" }, new[] { "[SPACE]", "JUMP" },
        new[] { "TAP [LMB]", "ANCHOR ROPE" }, new[] { "HOLD [LMB]", "PAY OUT ROPE" },
        new[] { "HOLD [RMB]", "REEL IN" }, new[] { "[RMB]", "CUT ROPE" }, new[] { "[RMB][RMB]", "TUG" },
        new[] { "HOLD [F]", "INJECT: FOCUS" }, new[] { "[E]", "INVENTORY" }, new[] { "[Q]", "COMMAND MODE" },
    };
    static readonly string[][] Focus =
    {
        new[] { "DRAG [LMB]", "LOOK" }, new[] { "[LMB] BASE", "ROPE MENU" },
        new[] { "[LMB] VIRUS", "GENOME" }, new[] { "[LMB] CORE", "EXTRACT" }, new[] { "[E]", "INVENTORY" },
        new[] { "DRAG [LMB] SLOT", "REARRANGE" }, new[] { "[F]", "LEAVE FOCUS" }, new[] { "[ESC]", "CLOSE / LEAVE FOCUS" },
        new[] { "[Q]", "COMMAND MODE" },
    };
    // A white blood cell's arm has hold of it (Intent.Seized): the one way out, so it can't be missed.
    static readonly string[][] Seized =
    {
        new[] { "[MOUSE]", "AIM AWAY FROM IT" }, new[] { "[SPACE]", "BURST: TEAR FREE" }, // Burst goes where you look
        new[] { "[W][A][S][D]", "SWIM AGAINST THE PULL" },
    };
    static readonly string[][] Command =
    {
        new[] { "[LMB]", "SELECT" }, new[] { "DRAG [LMB]", "BOX SELECT" }, new[] { "[SHIFT] [LMB]", "ADD / REMOVE" },
        new[] { "DRAG AGENTS > TARGETS", "LINK" }, new[] { "[LMB] LINE", "LINK SETTINGS" }, new[] { "DRAG TASK > TASK", "DO NEXT" },
        new[] { "[RMB] TAG / LINE", "DELETE" }, new[] { "[Q]", "EXIT COMMAND" },
    };

    const float RowHeight = 24f, Top = 34f, Bottom = 10f, InputWidth = 132f, Pad = 16f, Icon = 20f, Gap = 3f;

    VirusMovement _player;
    Organism _organism;
    string[][] _shownSet;
    bool _folded;
    float _nextSearch, _shownAt;

    Canvas _canvas;
    RectTransform _panel;
    Font _font;
    Typed _title;
    Sprite _pill;
    readonly Sprite[] _mice = new Sprite[4]; // left, right, wheel, none
    readonly List<Row> _rows = new List<Row>();
    readonly List<Object> _made = new List<Object>();
    RawImage _scan;

    class Row
    {
        public RectTransform inputs;  // the icons, rebuilt when the list changes
        public CanvasGroup group;     // pops the icons in with the row's typing
        public Typed action;
        public float delay;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<ControlsHint>()) return; // the scene has its own, with its own settings
        new GameObject("Controls Hint").AddComponent<ControlsHint>();
    }

    void Start()
    {
        _folded = startFolded;
        Build();
    }

    void Update()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again
        {
            Destroy(_canvas.gameObject);
            _rows.Clear();
            _shownSet = null;
            Build();
        }
        if (!_canvas) return;
        if (Keyboard.current != null && Keyboard.current[toggleKey].wasPressedThisFrame)
        {
            _folded = !_folded;
            _shownSet = null; // retype
        }

        if (!_player && Time.unscaledTime >= _nextSearch)
        {
            _nextSearch = Time.unscaledTime + 1f;
            _player = FindAnyObjectByType<VirusMovement>();
            _organism = _player ? _player.GetComponent<Organism>() : null;
        }
        _canvas.enabled = _player;
        if (!_player) return;

        string[][] set = CommandMode.Active ? Command : _organism && _organism.Holding(Intent.Seized) ? Seized
                       : _player.IsFocusMode ? Focus : _player.IsGrounded ? Grounded : Flying;
        if (set != _shownSet) Show(set);

        float now = Time.unscaledTime;
        _title.Tick(now, typeSpeed);
        foreach (Row r in _rows)
        {
            r.action.Tick(now, typeSpeed);
            float k = Mathf.Clamp01((now - _shownAt - r.delay) * 12f);
            r.group.alpha = k;
            r.inputs.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, 1f - (1f - k) * (1f - k));
        }
        Rect uv = _scan.uvRect;
        uv.y = -now * 0.6f;
        _scan.uvRect = uv;
    }

    void Show(string[][] set)
    {
        _shownSet = set;
        _shownAt = Time.unscaledTime;
        int rows = _folded ? 0 : set.Length;
        while (_rows.Count < rows) AddRow();

        string mode = set == Command ? "COMMAND" : set == Seized ? "SEIZED" : set == Focus ? "FOCUS" : set == Grounded ? "SURFACE" : "FLIGHT";
        _title.Set("00 // CONTROLS  " + (_folded ? "[" + toggleKey.ToString().ToUpperInvariant() + "]" : mode));
        _title.start = _shownAt;
        for (int i = 0; i < _rows.Count; i++)
        {
            Row r = _rows[i];
            bool on = i < rows;
            r.inputs.gameObject.SetActive(on);
            r.action.text.gameObject.SetActive(on);
            if (!on) continue;
            Icons(r.inputs, set[i][0]);
            r.action.Set(set[i][1]);
            r.action.start = _shownAt; // each row types in after the one above
        }
        _panel.sizeDelta = new Vector2(width, Top + rows * RowHeight + (rows > 0 ? Bottom : 0f));
        _scan.rectTransform.sizeDelta = _panel.sizeDelta - new Vector2(TerminalUI.Chamfer * 2f, 4f);
        _scan.uvRect = new Rect(0f, _scan.uvRect.y, 1f, _scan.rectTransform.sizeDelta.y / 3f); // one dark row in three pixels
    }

    // Lays out "[RMB][RMB]", "HOLD [E]"... left to right.
    void Icons(RectTransform holder, string spec)
    {
        for (int c = holder.childCount - 1; c >= 0; c--) Destroy(holder.GetChild(c).gameObject);
        float x = 0f;
        foreach (string word in spec.Replace("][", "] [").Split(' '))
        {
            if (word.Length == 0) continue;
            if (word.StartsWith("[") && word.EndsWith("]")) x += Prompt(holder, x, word.Substring(1, word.Length - 2)) + Gap;
            else x += Tag(holder, x, word) + Gap + 2f;
        }
    }

    // One input icon at x; returns its width.
    float Prompt(RectTransform holder, float x, string input)
    {
        int button = input == "LMB" ? 0 : input == "RMB" ? 1 : input == "MMB" ? 2 : input == "MOUSE" ? 3 : -1;
        float w = Icon;
        Text label = null;
        if (button < 0)
        {
            label = TerminalUI.Graphic<Text>("Key", holder, Vector2.zero, new Vector2(80f, Icon));
            TerminalUI.Style(label, _font, input.Length > 1 ? 10 : 12, text, TextAnchor.MiddleCenter);
            label.text = input;
            w = Mathf.Max(Icon, label.preferredWidth + 12f);
        }

        Image ring = TerminalUI.Graphic<Image>("Prompt", holder, Vector2.zero, new Vector2(w, Icon));
        ring.sprite = _pill;
        ring.type = Image.Type.Sliced;
        ring.color = line;
        ring.raycastTarget = false;
        Place(ring.rectTransform, x, w);
        ring.rectTransform.SetAsFirstSibling();

        if (label)
        {
            label.rectTransform.sizeDelta = new Vector2(w, Icon);
            Place(label.rectTransform, x, w);
        }
        else
        {
            Image mouse = TerminalUI.Graphic<Image>("Mouse", ring.rectTransform, Vector2.zero, new Vector2(Icon, Icon) * 0.8f);
            mouse.sprite = _mice[button];
            mouse.color = button == 3 ? text : live; // the lit button in the engaged colour
            mouse.raycastTarget = false;
        }
        return w;
    }

    float Tag(RectTransform holder, float x, string word)
    {
        Text t = TerminalUI.Graphic<Text>("Tag", holder, Vector2.zero, new Vector2(80f, Icon));
        TerminalUI.Style(t, _font, 10, new Color(line.r, line.g, line.b, 0.7f), TextAnchor.MiddleLeft);
        t.text = word;
        float w = t.preferredWidth;
        t.rectTransform.sizeDelta = new Vector2(w, Icon);
        Place(t.rectTransform, x, w);
        return w;
    }

    static void Place(RectTransform r, float x, float w)
    {
        r.anchorMin = r.anchorMax = new Vector2(0f, 0.5f);
        r.pivot = new Vector2(0f, 0.5f);
        r.anchoredPosition = new Vector2(x, 0f);
        r.sizeDelta = new Vector2(w, r.sizeDelta.y);
    }

    // ---------------- building ----------------

    void Build()
    {
        _font = font ? font : TerminalUI.Font(terminalFonts);
        _canvas = TerminalUI.Canvas("Controls Hint Canvas", transform, 480);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // never blocks a click

        Sprite fill = Keep(TerminalUI.ChamferSprite(frame: false)), frameSprite = Keep(TerminalUI.ChamferSprite(frame: true));
        _pill = Keep(TerminalUI.PillSprite());
        for (int b = 0; b < 3; b++) _mice[b] = Keep(TerminalUI.MouseSprite(b));
        _mice[3] = Keep(TerminalUI.MouseSprite(-1));
        Texture2D scan = TerminalUI.ScanTexture();
        _made.Add(scan);

        Image back = TerminalUI.Graphic<Image>("Panel", _canvas.transform, Vector2.zero, new Vector2(width, Top));
        back.sprite = fill;
        back.type = Image.Type.Sliced;
        back.color = panel;
        back.raycastTarget = false;
        _panel = back.rectTransform;
        _panel.anchorMin = _panel.anchorMax = _panel.pivot = new Vector2(1f, 0f);
        _panel.anchoredPosition = new Vector2(-margin.x, margin.y);

        _scan = TerminalUI.Graphic<RawImage>("Scan", _panel, Vector2.zero, Vector2.zero);
        _scan.texture = scan;
        _scan.raycastTarget = false;

        Image frame = TerminalUI.Graphic<Image>("Frame", _panel, Vector2.zero, Vector2.zero);
        frame.sprite = frameSprite;
        frame.type = Image.Type.Sliced;
        frame.color = line;
        frame.raycastTarget = false;
        RectTransform fr = frame.rectTransform;
        fr.anchorMin = Vector2.zero;
        fr.anchorMax = Vector2.one;
        fr.sizeDelta = Vector2.zero;

        _title = Label(Pad, -17f, width - Pad * 2f, 11, new Color(line.r, line.g, line.b, 0.75f), 0f);
    }

    void AddRow()
    {
        int i = _rows.Count;
        float y = -Top - (i + 0.5f) * RowHeight;
        float delay = 0.05f + i * 0.06f;

        RectTransform inputs = TerminalUI.Rect("Inputs", _panel, Vector2.zero, new Vector2(InputWidth, Icon));
        inputs.anchorMin = inputs.anchorMax = new Vector2(0f, 1f);
        inputs.pivot = new Vector2(0f, 0.5f);
        inputs.anchoredPosition = new Vector2(Pad, y);

        _rows.Add(new Row
        {
            inputs = inputs,
            group = inputs.gameObject.AddComponent<CanvasGroup>(),
            action = Label(Pad + InputWidth, y, width - InputWidth - Pad * 2f, 13, text, delay + 0.03f),
            delay = delay,
        });
    }

    // Left-aligned text, 'x' in from the panel's left, 'y' down from its top.
    Typed Label(float x, float y, float w, int size, Color color, float delay)
    {
        Text t = TerminalUI.Graphic<Text>("Label", _panel, Vector2.zero, new Vector2(w, size + 8f));
        RectTransform r = t.rectTransform;
        r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
        r.pivot = new Vector2(0f, 0.5f);
        r.anchoredPosition = new Vector2(x, y);
        TerminalUI.Style(t, _font, size, color, TextAnchor.MiddleLeft);
        return new Typed { text = t, delay = delay, start = Time.unscaledTime };
    }

    Sprite Keep(Sprite s)
    {
        _made.Add(s.texture);
        _made.Add(s);
        return s;
    }

    void OnDestroy()
    {
        foreach (Object o in _made)
            if (o) Destroy(o);
    }
}

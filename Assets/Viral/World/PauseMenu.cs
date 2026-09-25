using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Escape menu: pauses the game (time and audio), frees the cursor, and offers the save slots (SaveGame): SAVE and
/// LOAD per slot, and RESUME. Escape opens it only when nothing else would take that Escape (focus mode, the
/// rope menu, the head view, command mode: VirusMovement.ClaimsEscape), so it runs before them (execution order)
/// to see their state before they close anything. While open, VirusMovement and CommandMode ignore input.
/// Terminal look (TerminalUI); hit-testing is done here in screen space, not through the EventSystem.
/// Creates itself on play in a scene with the player.
/// </summary>
[DefaultExecutionOrder(-200)]
public class PauseMenu : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    [Min(1f)] public float typeSpeed = 70f;
    [Min(0.01f)] public float fadeTime = 0.12f;

    const float Width = 620f, Top = 92f, RowHeight = 62f, Pad = 34f;

    sealed class Button
    {
        public RectTransform rect;
        public Image fill, frame;
        public Text label;
        public Action click;
        public bool interactable = true;
        public float hover;
    }

    Canvas _canvas;
    CanvasGroup _group;
    RectTransform _panel;
    RawImage _scan;
    TerminalUI.Typed _title;
    Text _info, _status;
    readonly Text[] _dates = new Text[SaveGame.Slots];
    readonly Button[] _load = new Button[SaveGame.Slots];
    readonly List<Button> _buttons = new List<Button>();
    readonly List<UnityEngine.Object> _made = new List<UnityEngine.Object>();
    Font _font;
    Sprite _fillSprite, _frameSprite;

    VirusMovement _player;
    float _nextSearch, _fade, _timeScale = 1f;
    bool _freedCursor, _pausedAudio;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<PauseMenu>()) return;
        if (!FindAnyObjectByType<VirusMovement>()) return;
        new GameObject("Pause Menu").AddComponent<PauseMenu>();
    }

    void OnDisable()
    {
        if (IsOpen) Close();
    }

    void OnDestroy()
    {
        foreach (UnityEngine.Object o in _made) if (o) Destroy(o);
    }

    void Update()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _buttons.Clear();
        }

        Keyboard k = Keyboard.current;
        if (k != null && k.escapeKey.wasPressedThisFrame)
        {
            if (IsOpen) Close();
            else if (CanOpen()) Open();
        }
        if (!_canvas) return;

        _fade = Mathf.MoveTowards(_fade, IsOpen ? 1f : 0f, Time.unscaledDeltaTime / fadeTime);
        _group.alpha = _fade;
        _canvas.enabled = _fade > 0f;
        if (!IsOpen) return;

        float now = Time.unscaledTime;
        _panel.localScale = Vector3.one * Mathf.Lerp(0.96f, 1f, 1f - (1f - _fade) * (1f - _fade));
        _title.Tick(now, typeSpeed);
        Rect uv = _scan.uvRect;
        uv.y = -now * 0.6f;
        _scan.uvRect = uv;
        Pointer(Time.unscaledDeltaTime);
    }

    bool CanOpen()
    {
        if (CommandMode.Active) return false;
        if (!_player && Time.unscaledTime >= _nextSearch)
        {
            _nextSearch = Time.unscaledTime + 1f;
            _player = FindAnyObjectByType<VirusMovement>();
        }
        return !(_player && _player.ClaimsEscape);
    }

    public void Open()
    {
        if (IsOpen) return;
        if (!_canvas) Build();
        IsOpen = true;
        _timeScale = Time.timeScale;
        Time.timeScale = 0f;
        _pausedAudio = !AudioListener.pause;
        AudioListener.pause = true;
        _freedCursor = !UniversalCamera.FreeCursor;
        UniversalCamera.FreeCursor = true;
        Refresh();
        _title.Set("// SESSION  PAUSED");
        _title.start = Time.unscaledTime;
        Status("", TerminalUI.Text);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Time.timeScale = _timeScale;
        if (_pausedAudio) AudioListener.pause = false;
        if (_freedCursor) UniversalCamera.FreeCursor = false;
        _pausedAudio = _freedCursor = false;
    }

    // ---------------- actions ----------------

    void Save(int slot)
    {
        bool ok = SaveGame.Save(slot, out string message);
        Status(message, ok ? TerminalUI.Live : TerminalUI.Blood);
        Refresh();
    }

    void Load(int slot)
    {
        if (!SaveGame.Exists(slot)) return;
        Close(); // time runs again for the rebuilt world
        bool ok = SaveGame.Load(slot, out string message);
        if (ok) return;
        Open();
        Status(message, TerminalUI.Blood);
    }

    void Refresh()
    {
        for (int i = 0; i < SaveGame.Slots; i++)
        {
            bool used = SaveGame.Exists(i);
            _dates[i].text = SaveGame.Describe(i);
            _dates[i].color = used ? TerminalUI.Text : new Color(TerminalUI.Line.r, TerminalUI.Line.g, TerminalUI.Line.b, 0.4f);
            _load[i].interactable = used;
        }
        WorldStreamer w = WorldStreamer.Instance;
        _info.text = w ? $"SEED {w.Seed}  //  {w.LoadedSectors} SECTORS LOADED  //  {WorldStreamer.Live.Count} OBJECTS" : "NO WORLD STREAMER";
    }

    void Status(string text, Color color)
    {
        _status.text = text;
        _status.color = color;
    }

    // ---------------- pointer ----------------

    void Pointer(float dt)
    {
        Mouse mouse = Mouse.current;
        Vector2 at = mouse != null ? mouse.position.ReadValue() : new Vector2(-1f, -1f);
        bool click = mouse != null && mouse.leftButton.wasPressedThisFrame;
        Button clicked = null;
        foreach (Button b in _buttons)
        {
            bool over = b.interactable && RectTransformUtility.RectangleContainsScreenPoint(b.rect, at, null);
            b.hover = Mathf.MoveTowards(b.hover, over ? 1f : 0f, dt * 10f);
            Color line = b.interactable ? Color.Lerp(TerminalUI.Line, TerminalUI.Live, b.hover) : new Color(0.55f, 0.65f, 0.75f, 0.3f);
            b.frame.color = line;
            b.fill.color = new Color(line.r, line.g, line.b, b.interactable ? 0.06f + 0.16f * b.hover : 0.03f);
            b.label.color = b.interactable ? Color.Lerp(TerminalUI.Text, TerminalUI.Live, b.hover) : line;
            if (over && click) clicked = b;
        }
        clicked?.click?.Invoke(); // after the loop: an action may rebuild or close the menu
    }

    // ---------------- building ----------------

    void Build()
    {
        _font = TerminalUI.Font(TerminalUI.DefaultFonts);
        _canvas = TerminalUI.Canvas("Pause Menu Canvas", transform, 900);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // hit-tested here
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();
        _group.alpha = 0f;
        _fillSprite = Keep(TerminalUI.ChamferSprite(frame: false));
        _frameSprite = Keep(TerminalUI.ChamferSprite(frame: true));
        Texture2D scan = TerminalUI.ScanTexture();
        _made.Add(scan);

        Image dim = TerminalUI.Graphic<Image>("Dim", _canvas.transform, Vector2.zero, Vector2.zero);
        dim.color = new Color(0f, 0.01f, 0.04f, 0.55f);
        dim.raycastTarget = false;
        Stretch(dim.rectTransform);

        int rows = SaveGame.Slots;
        float height = Top + rows * RowHeight + 24f + 54f + 46f;
        Image back = Sliced("Panel", _canvas.transform, _fillSprite, TerminalUI.Panel);
        _panel = back.rectTransform;
        _panel.sizeDelta = new Vector2(Width, height);

        _scan = TerminalUI.Graphic<RawImage>("Scan", _panel, Vector2.zero, _panel.sizeDelta - new Vector2(TerminalUI.Chamfer * 2f, 4f));
        _scan.texture = scan;
        _scan.raycastTarget = false;
        _scan.uvRect = new Rect(0f, 0f, 1f, _scan.rectTransform.sizeDelta.y / 3f); // one dark row in three pixels

        Image frame = Sliced("Frame", _panel, _frameSprite, TerminalUI.Line);
        Stretch(frame.rectTransform);

        _title = new TerminalUI.Typed { text = Label(_panel, Pad, -30f, Width - Pad * 2f, 20, TerminalUI.Line, TextAnchor.MiddleLeft), delay = 0.05f };
        _info = Label(_panel, Pad, -58f, Width - Pad * 2f, 11, new Color(TerminalUI.Line.r, TerminalUI.Line.g, TerminalUI.Line.b, 0.6f), TextAnchor.MiddleLeft);

        const float buttonW = 104f, buttonH = 38f;
        for (int i = 0; i < rows; i++)
        {
            int slot = i;
            float y = -Top - (i + 0.5f) * RowHeight;
            Image rule = TerminalUI.Graphic<Image>("Rule", _panel, Vector2.zero, new Vector2(Width - Pad * 2f, 1f));
            rule.color = new Color(TerminalUI.Line.r, TerminalUI.Line.g, TerminalUI.Line.b, 0.15f);
            rule.raycastTarget = false;
            Place(rule.rectTransform, Width * 0.5f, y + RowHeight * 0.5f);

            Label(_panel, Pad, y, 110f, 15, TerminalUI.Line, TextAnchor.MiddleLeft).text = $"SLOT {slot + 1}";
            _dates[i] = Label(_panel, Pad + 104f, y, 220f, 14, TerminalUI.Text, TextAnchor.MiddleLeft);
            AddButton("SAVE", Width - Pad - buttonW * 1.5f - 12f, y, buttonW, buttonH, () => Save(slot));
            _load[i] = AddButton("LOAD", Width - Pad - buttonW * 0.5f, y, buttonW, buttonH, () => Load(slot));
        }

        float bottom = -Top - rows * RowHeight - 24f - 27f;
        AddButton("RESUME", Width * 0.5f, bottom, Width - Pad * 2f, 44f, Close);
        _status = Label(_panel, Pad, bottom - 46f, Width - Pad * 2f, 13, TerminalUI.Text, TextAnchor.MiddleCenter);
    }

    Button AddButton(string text, float x, float y, float w, float h, Action click)
    {
        Image fill = Sliced(text, _panel, _fillSprite, TerminalUI.Line);
        fill.rectTransform.sizeDelta = new Vector2(w, h);
        Place(fill.rectTransform, x, y);
        Image frame = Sliced("Frame", fill.rectTransform, _frameSprite, TerminalUI.Line);
        Stretch(frame.rectTransform);
        Text label = TerminalUI.Graphic<Text>("Label", fill.rectTransform, Vector2.zero, new Vector2(w, h));
        TerminalUI.Style(label, _font, 15, TerminalUI.Text, TextAnchor.MiddleCenter);
        label.text = "[ " + text + " ]";
        var b = new Button { rect = fill.rectTransform, fill = fill, frame = frame, label = label, click = click };
        _buttons.Add(b);
        return b;
    }

    Text Label(RectTransform parent, float x, float y, float w, int size, Color color, TextAnchor anchor)
    {
        Text t = TerminalUI.Graphic<Text>("Text", parent, Vector2.zero, new Vector2(w, size + 10f));
        TerminalUI.Style(t, _font, size, color, anchor);
        t.rectTransform.pivot = new Vector2(0f, 0.5f);
        Place(t.rectTransform, x, y);
        return t;
    }

    static Image Sliced(string name, Transform parent, Sprite sprite, Color color)
    {
        Image i = TerminalUI.Graphic<Image>(name, parent, Vector2.zero, Vector2.zero);
        i.sprite = sprite;
        i.type = Image.Type.Sliced;
        i.color = color;
        i.raycastTarget = false;
        return i;
    }

    // x from the panel's left edge, y down from its top (negative).
    static void Place(RectTransform r, float x, float y)
    {
        r.anchorMin = r.anchorMax = new Vector2(0f, 1f);
        r.anchoredPosition = new Vector2(x, y);
    }

    static void Stretch(RectTransform r)
    {
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.sizeDelta = Vector2.zero;
        r.anchoredPosition = Vector2.zero;
    }

    Sprite Keep(Sprite s)
    {
        _made.Add(s);
        if (s) _made.Add(s.texture);
        return s;
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;

/// <summary>
/// The stores half of the inventory: beside the head view's DNA sphere (GenomeView), a terminal
/// panel with a bar per VirusInventory slot, each filled in its substance's colour ("GLU // GLUCOSE
/// 42 / 100", or EMPTY). It opens and closes with the sphere, on whichever side of it is away from the
/// head and fits the screen. Bars ease to their amounts and flash as they fill (extraction).
/// VirusMovement opens the sphere (E anywhere, or a click on the head in focus mode) and binds this
/// to it. Builds itself; nothing to set up.
/// </summary>
[DefaultExecutionOrder(100)] // after GenomeView has placed the sphere this frame
public class InventoryView : MonoBehaviour
{
    [Min(120f), Tooltip("Panel width, in 1080p pixels.")]
    public float width = 320f;
    [Min(16f)] public float rowHeight = 34f;
    [Min(0f), Tooltip("Gap between the sphere and the panel, in 1080p pixels (the strand codes sit in it).")]
    public float gap = 70f;
    [Min(1f)] public float typeSpeed = 110f;

    [Header("Colours")]
    public Color panel = TerminalUI.Panel;
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;

    [Header("Type")]
    public Font font;
    public string[] terminalFonts = TerminalUI.DefaultFonts;

    const float Header = 34f, Pad = 12f;

    GenomeView _view;
    VirusInventory _inventory;
    float _side;       // +1 right of the sphere, -1 left; chosen once per opening
    bool _wasOpen;

    Canvas _canvas;
    RectTransform _canvasRect, _panel;
    CanvasGroup _group;
    Font _font;
    Typed _title;
    RawImage _scan;
    readonly List<Row> _rows = new List<Row>();
    readonly List<Object> _made = new List<Object>();
    Sprite _fillSprite, _frameSprite;

    class Row
    {
        public RectTransform root;
        public Image fill, frame, flash;
        public Typed name;
        public Text amount;
        public float shown, last, flashAt = -10f;
    }

    public static InventoryView Create() => new GameObject("Inventory View").AddComponent<InventoryView>();

    /// <summary>Shows 'inventory' beside 'view' whenever that's open.</summary>
    public void Bind(GenomeView view, VirusInventory inventory)
    {
        _view = view;
        _inventory = inventory;
    }

    public bool IsOpen => _canvas && _canvas.enabled && _group.alpha > 0f;

    /// <summary>Whether a screen point is on the panel (a click there is the inventory's, not the world's).</summary>
    public bool Covers(Vector2 screen) => IsOpen && RectTransformUtility.RectangleContainsScreenPoint(_panel, screen, null);

    void LateUpdate()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _rows.Clear();
        }
        bool open = _view && _inventory && _view.IsOpen;
        bool placed = _view && _inventory && _view.Placement(out _, out _, out _);
        if (!open && !placed)
        {
            if (_canvas && _canvas.enabled) _canvas.enabled = false;
            _wasOpen = false;
            return;
        }
        if (!_canvas) Build();
        _canvas.enabled = true;
        float now = Time.unscaledTime;

        // Opens after the sphere has swelled, closes before it shrinks.
        _view.Placement(out Vector2 sphere, out float radius, out Vector2 head);
        float k = Mathf.Clamp01((_view.Shown - 0.55f) / 0.45f);
        _group.alpha = k;
        _group.blocksRaycasts = k > 0.5f && open;
        if (open && !_wasOpen)
        {
            _side = 0f;
            _title.start = now;
            foreach (Row r in _rows) r.name.start = now;
        }
        _wasOpen = open;

        IReadOnlyList<VirusInventory.Slot> slots = _inventory.Slots;
        while (_rows.Count < slots.Count) AddRow();
        for (int i = 0; i < _rows.Count; i++) _rows[i].root.gameObject.SetActive(i < slots.Count);

        // Size, then place beside the sphere: away from the head, else whichever side fits.
        float h = Header + slots.Count * rowHeight + Pad;
        _panel.sizeDelta = new Vector2(width, h);
        Rect area = _canvasRect.rect;
        if (_side == 0f)
        {
            _side = sphere.x >= head.x ? 1f : -1f;
            float x = sphere.x + _side * (radius + gap + width * 0.5f);
            if (x + width * 0.5f > area.width - 16f || x - width * 0.5f < 16f) _side = -_side;
        }
        float px = sphere.x + _side * (radius + gap + width * 0.5f);
        px = Mathf.Clamp(px, width * 0.5f + 16f, Mathf.Max(width * 0.5f + 16f, area.width - width * 0.5f - 16f));
        float py = Mathf.Clamp(sphere.y, h * 0.5f + 16f, Mathf.Max(h * 0.5f + 16f, area.height - h * 0.5f - 16f));
        _panel.anchoredPosition = new Vector2(px, py);
        _panel.localScale = new Vector3(Mathf.Lerp(0.85f, 1f, k), Mathf.Lerp(0.3f, 1f, Smooth(k)), 1f);

        _title.Tick(now, typeSpeed);
        float capacity = Mathf.Max(_inventory.capacity, 1e-3f);
        for (int i = 0; i < slots.Count; i++) Refresh(_rows[i], slots[i], i, capacity, now);

        Rect uv = _scan.uvRect;
        uv.y = -now * 0.6f;
        _scan.uvRect = uv;
    }

    void Refresh(Row r, VirusInventory.Slot slot, int index, float capacity, float now)
    {
        bool empty = slot.Empty;
        float target = empty ? 0f : Mathf.Clamp01(slot.amount / capacity);
        if (slot.amount > r.last + 1e-3f) r.flashAt = now; // filling
        r.last = empty ? 0f : slot.amount;
        r.shown = Mathf.MoveTowards(Mathf.Lerp(r.shown, target, 1f - Mathf.Exp(-Time.unscaledDeltaTime * 10f)), target, Time.unscaledDeltaTime * 0.05f);

        Color c = empty ? line : slot.color;
        RectTransform f = r.fill.rectTransform;
        f.anchorMax = new Vector2(Mathf.Max(r.shown, 0.001f), 1f);
        r.fill.enabled = r.shown > 0.002f;
        r.fill.color = new Color(c.r, c.g, c.b, 0.75f);
        float flash = Mathf.Clamp01(1f - (now - r.flashAt) / 0.35f);
        r.flash.color = new Color(1f, 1f, 1f, flash * 0.35f);
        r.flash.rectTransform.anchorMax = f.anchorMax;
        r.frame.color = empty ? new Color(line.r, line.g, line.b, 0.35f) : new Color(c.r, c.g, c.b, 0.9f);

        r.name.Set(empty ? "S" + (index + 1) + "  --  EMPTY" : "S" + (index + 1) + "  " + slot.code + " // " + slot.substance);
        r.name.text.color = empty ? new Color(text.r, text.g, text.b, 0.45f) : text;
        r.name.Tick(now, typeSpeed);
        r.amount.text = empty ? "" : Mathf.FloorToInt(slot.amount) + " / " + Mathf.RoundToInt(capacity);
        r.amount.color = slot.amount >= capacity - 0.01f ? live : text;
    }

    static float Smooth(float x) => x * x * (3f - 2f * x);

    // ---------------- building ----------------

    void Build()
    {
        _font = font ? font : TerminalUI.Font(terminalFonts);
        _canvas = TerminalUI.Canvas("Inventory Canvas", transform, 591); // over the head view's sphere
        _canvasRect = (RectTransform)_canvas.transform;
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();
        _fillSprite = TerminalUI.ChamferSprite(false);
        _frameSprite = TerminalUI.ChamferSprite(true);
        _made.Add(_fillSprite.texture);
        _made.Add(_frameSprite.texture);
        _made.Add(_fillSprite);
        _made.Add(_frameSprite);

        _panel = TerminalUI.Rect("Stores", _canvasRect, Vector2.zero, new Vector2(width, 200f));
        _panel.anchorMin = _panel.anchorMax = Vector2.zero; // canvas units from the bottom left, like the sphere
        var bg = _panel.gameObject.AddComponent<Image>();
        bg.sprite = _fillSprite;
        bg.type = Image.Type.Sliced;
        bg.color = panel;
        bg.raycastTarget = true; // clicks on it are the inventory's

        _scan = TerminalUI.Graphic<RawImage>("Scan", _panel, Vector2.zero, Vector2.zero);
        Stretch(_scan.rectTransform, 2f);
        Texture2D scan = TerminalUI.ScanTexture();
        _made.Add(scan);
        _scan.texture = scan;
        _scan.uvRect = new Rect(0f, 0f, 1f, 200f / 3f);
        _scan.raycastTarget = false;

        Image frame = TerminalUI.Graphic<Image>("Frame", _panel, Vector2.zero, Vector2.zero);
        Stretch(frame.rectTransform, 0f);
        frame.sprite = _frameSprite;
        frame.type = Image.Type.Sliced;
        frame.color = line;
        frame.raycastTarget = false;

        Text title = TerminalUI.Graphic<Text>("Title", _panel, Vector2.zero, new Vector2(width - 2f * Pad, 20f));
        TerminalUI.Style(title, _font, 12, new Color(line.r, line.g, line.b, 0.75f), TextAnchor.MiddleLeft);
        TopLeft(title.rectTransform, new Vector2(Pad, -Pad + 2f));
        _title = new Typed { text = title, full = "05 // STORES", start = Time.unscaledTime };
    }

    void AddRow()
    {
        int i = _rows.Count;
        var r = new Row();
        r.root = TerminalUI.Rect("Slot " + (i + 1), _panel, Vector2.zero, new Vector2(width - 2f * Pad, rowHeight - 8f));
        TopLeft(r.root, new Vector2(Pad, -(Header + i * rowHeight)));

        r.fill = Bar("Fill", r.root, _fillSprite);
        r.flash = Bar("Flash", r.root, _fillSprite);
        r.frame = Bar("Frame", r.root, _frameSprite);
        Stretch(r.frame.rectTransform, 0f);

        Text name = TerminalUI.Graphic<Text>("Name", r.root, Vector2.zero, Vector2.zero);
        TerminalUI.Style(name, _font, 13, text, TextAnchor.MiddleLeft);
        Stretch(name.rectTransform, 0f);
        name.rectTransform.offsetMin = new Vector2(10f, 0f);
        name.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.7f);
        r.name = new Typed { text = name, delay = 0.08f * (i + 1), start = Time.unscaledTime };

        r.amount = TerminalUI.Graphic<Text>("Amount", r.root, Vector2.zero, Vector2.zero);
        TerminalUI.Style(r.amount, _font, 13, text, TextAnchor.MiddleRight);
        Stretch(r.amount.rectTransform, 0f);
        r.amount.rectTransform.offsetMax = new Vector2(-10f, 0f);
        r.amount.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.7f);
        _rows.Add(r);
    }

    // A sliced image filling its parent from the left; its right edge is set per frame (anchorMax.x).
    static Image Bar(string name, RectTransform parent, Sprite sprite)
    {
        Image img = TerminalUI.Graphic<Image>(name, parent, Vector2.zero, Vector2.zero);
        RectTransform t = img.rectTransform;
        t.anchorMin = Vector2.zero;
        t.anchorMax = Vector2.one;
        t.offsetMin = t.offsetMax = Vector2.zero;
        img.sprite = sprite;
        img.type = Image.Type.Sliced;
        img.raycastTarget = false;
        return img;
    }

    static void Stretch(RectTransform t, float inset)
    {
        t.anchorMin = Vector2.zero;
        t.anchorMax = Vector2.one;
        t.offsetMin = new Vector2(inset, inset);
        t.offsetMax = new Vector2(-inset, -inset);
    }

    static void TopLeft(RectTransform t, Vector2 at)
    {
        t.anchorMin = t.anchorMax = new Vector2(0f, 1f);
        t.pivot = new Vector2(0f, 1f);
        t.anchoredPosition = at;
    }

    void OnDestroy()
    {
        foreach (Object o in _made)
            if (o) Destroy(o);
    }
}

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A cell's stats, shown under it on screen while it's in focus (VirusMovement calls <see cref="Show"/> each
/// frame): its type, nucleus and organelles against the limit (with how hard each kind works), and a thin bar per
/// store in its substance's colour. Plain centred text and bars in the TerminalUI font and colours, no panel.
/// Text refreshed a few times a second (strings are garbage), bars every frame. Made on demand.
/// </summary>
public class CellReadout : MonoBehaviour
{
    [Tooltip("Pixels (at 1080p) between the cell's lowest point on screen and the stats.")]
    public float gap = 16f;
    [Min(1f), Tooltip("Fades in / out this many times a second.")]
    public float fadeSpeed = 5f;

    const float Row = 20f, BarWidth = 120f, BarHeight = 4f, Width = 280f, Side = 50f;

    sealed class Bar
    {
        public RectTransform root;
        public Text label, value;
        public Image fill;
    }

    CellInterior _cell;
    Camera _camera;
    Canvas _canvas;
    RectTransform _canvasRect, _root;
    CanvasGroup _group;
    Text _title, _body;
    readonly List<Bar> _bars = new List<Bar>();
    readonly StringBuilder _text = new StringBuilder(256);
    Font _font;
    float _nextText, _shown;
    int _lines = 1;

    static Color Dim => new Color(TerminalUI.Text.r, TerminalUI.Text.g, TerminalUI.Text.b, 0.55f);

    public static CellReadout Create() => new GameObject("Cell Readout").AddComponent<CellReadout>();

    /// <summary>Shows 'cell''s stats this frame (null: fades out).</summary>
    public void Show(CellInterior cell, Camera cam)
    {
        if (cell && cell != _cell) _nextText = 0f;
        if (cell)
        {
            Build();
            _cell = cell;
        }
        _camera = cam;
        _shown = Mathf.MoveTowards(_shown, cell ? 1f : 0f, Time.unscaledDeltaTime * fadeSpeed);
        if (!_canvas) return;
        _group.alpha = _shown;
        _canvas.enabled = _shown > 0f && _cell;
        if (_canvas.enabled && Place()) Refresh();
    }

    // Under the cell's lowest point on screen, centred on it, kept on screen.
    bool Place()
    {
        Camera cam = _camera ? _camera : Camera.main;
        Renderer r = cam ? _cell.GetComponentInChildren<Renderer>() : null;
        if (!r) return false;
        Bounds b = r.bounds;
        Vector3 c = b.center, e = b.extents;
        Vector3 centre = cam.WorldToScreenPoint(c);
        if (centre.z <= 0f) return false;
        float lowest = centre.y;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            lowest = Mathf.Min(lowest, cam.WorldToScreenPoint(c + corner).y);
        }
        RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, new Vector2(centre.x, lowest), null, out Vector2 at);
        Vector2 half = _canvasRect.rect.size * 0.5f;
        float height = Row * (1 + _lines + _cell.store.Count) + 6f;
        at.x = Mathf.Clamp(at.x, -half.x + Width * 0.5f, half.x - Width * 0.5f);
        at.y = Mathf.Clamp(at.y - gap, -half.y + height, half.y);
        _root.anchoredPosition = at;
        return true;
    }

    void Refresh()
    {
        float now = Time.unscaledTime, cap = Mathf.Max(_cell.Capacity, 1e-3f);
        int slots = _cell.store.Count;
        while (_bars.Count < slots) _bars.Add(MakeBar());

        bool text = now >= _nextText;
        if (text)
        {
            _nextText = now + 0.2f;
            _title.text = _cell.DisplayName;
            _text.Clear();
            if (_cell.HasNucleus) _text.Append("NUCLEUS  ").Append(_cell.OrganelleCount).Append(" / ").Append(_cell.Limit).Append(" ORGANELLES");
            else _text.Append("NO NUCLEUS");
            _lines = 1;
            foreach (CellInterior.Organelles o in _cell.organelles)
            {
                if (!o.type || o.count <= 0) continue;
                _text.Append('\n').Append(o.type.code).Append(" x").Append(o.count).Append("  ")
                     .Append(Mathf.RoundToInt(o.activity * 100f)).Append('%');
                _lines++;
            }
            _body.text = _text.ToString();
        }

        float y = -Row * (1 + _lines) - 4f;
        for (int i = 0; i < _bars.Count; i++)
        {
            Bar b = _bars[i];
            bool on = i < slots;
            if (b.root.gameObject.activeSelf != on) b.root.gameObject.SetActive(on);
            if (!on) continue;
            StoreSlot s = _cell.store[i];
            b.root.anchoredPosition = new Vector2(0f, y - i * Row);
            b.fill.color = s.Empty ? Color.clear : s.color;
            b.fill.rectTransform.sizeDelta = new Vector2(BarWidth * Mathf.Clamp01(s.amount / cap), BarHeight);
            if (!text) continue;
            b.label.text = s.Empty ? "" : s.code;
            b.label.color = s.color;
            b.value.text = s.Empty ? "" : Mathf.FloorToInt(s.amount).ToString();
        }
    }

    // ---------------- building ----------------

    void Build()
    {
        if (_canvas) return;
        _font = TerminalUI.Font(TerminalUI.DefaultFonts);
        _canvas = TerminalUI.Canvas("Cell Readout Canvas", transform, 590);
        _canvasRect = (RectTransform)_canvas.transform;
        foreach (GraphicRaycaster r in _canvas.GetComponents<GraphicRaycaster>()) r.enabled = false; // nothing to click
        _root = TerminalUI.Rect("Stats", _canvasRect, Vector2.zero, new Vector2(Width, 0f));
        _root.pivot = new Vector2(0.5f, 1f);
        _group = _root.gameObject.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = false;

        _title = Label(_root, Vector2.zero, Width, 15, TerminalUI.Line, TextAnchor.UpperCenter, 0.5f);
        _body = Label(_root, new Vector2(0f, -Row), Width, 13, Dim, TextAnchor.UpperCenter, 0.5f);
        _body.verticalOverflow = VerticalWrapMode.Overflow;
        _body.lineSpacing = Row / (13f * 1.15f);
    }

    // A row: code, a thin bar, the amount, centred as a group.
    Bar MakeBar()
    {
        RectTransform root = TerminalUI.Rect("Store", _root, Vector2.zero, new Vector2(Width, Row));
        Anchor(root, Vector2.zero, 0.5f);
        var b = new Bar { root = root };
        float left = -BarWidth * 0.5f;
        b.label = Label(root, new Vector2(left - 8f, 0f), Side, 13, TerminalUI.Text, TextAnchor.MiddleRight, 1f);
        Image back = TerminalUI.Graphic<Image>("Back", root, Vector2.zero, new Vector2(BarWidth, BarHeight));
        Anchor(back.rectTransform, new Vector2(left, -(Row - BarHeight) * 0.5f), 0f);
        back.color = new Color(TerminalUI.Line.r, TerminalUI.Line.g, TerminalUI.Line.b, 0.18f);
        back.raycastTarget = false;
        b.fill = TerminalUI.Graphic<Image>("Fill", root, Vector2.zero, new Vector2(0f, BarHeight));
        Anchor(b.fill.rectTransform, new Vector2(left, -(Row - BarHeight) * 0.5f), 0f);
        b.fill.raycastTarget = false;
        b.value = Label(root, new Vector2(-left + 8f, 0f), Side, 13, Dim, TextAnchor.MiddleLeft, 0f);
        return b;
    }

    Text Label(RectTransform parent, Vector2 at, float width, int fontSize, Color color, TextAnchor anchor, float pivotX)
    {
        Text t = TerminalUI.Graphic<Text>("Label", parent, Vector2.zero, new Vector2(width, Row));
        TerminalUI.Style(t, _font, fontSize, color, anchor);
        Anchor(t.rectTransform, at, pivotX);
        return t;
    }

    // Hung from the parent's top middle at 'at', pivoted at its top (x = pivotX).
    static void Anchor(RectTransform r, Vector2 at, float pivotX)
    {
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 1f);
        r.pivot = new Vector2(pivotX, 1f);
        r.anchoredPosition = at;
    }
}

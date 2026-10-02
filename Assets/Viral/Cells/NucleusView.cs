using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// A cell's nucleus opened up (focus mode, click the nucleus): the nucleus itself swells on screen, in place,
/// into a disc (its double membrane with pores as the outline, faint chromatin behind) and becomes a readout:
/// a bar per store with its percent, a small circle per organelle slot its limit allows (filled in the
/// organelle's colour: there is one, pulsing as it works; hollow: free), and the foreign DNA inside it (genes a
/// virus injected, <see cref="CellInterior.dna"/>) as small helices in their colours (dim: inert here).
/// Not the head view's bubble on purpose (the user: it looked too much like the player's inventory).
/// All shapes are one FlatMesh (Hidden/GenomeStrand) drawn into a render texture only while it's open; text is
/// UI Text over it, rewritten 5x a second. Read only. Made on demand by VirusMovement.
/// </summary>
public class NucleusView : MonoBehaviour
{
    [Min(0.01f), Tooltip("Seconds to open or close.")]
    public float animationTime = 0.45f;
    [Min(1f)] public float typeSpeed = 90f;
    [Min(64f), Tooltip("Disc diameter when open, in 1080p pixels.")]
    public float size = 440f;
    [Min(0.5f), Tooltip("How quickly the disc follows the nucleus across the screen once open.")]
    public float follow = 9f;
    [Range(256, 2048)] public int resolution = 768;
    [Min(0f), Tooltip("Membrane width, in 1080p pixels.")]
    public float outlineWidth = 3f;

    [Header("Shader")]
    public Shader strandShader;

    public bool IsOpen => _open;
    public CellInterior Cell => _cell;

    // Layout, in disc units (the render texture spans -1..1; the membrane sits at Rim).
    const float Rim = 0.95f;
    const float BarsTop = 0.43f, BarsBottom = 0.02f, BarHalf = 0.4f, LabelGap = 0.05f;
    const float OrganelleTitle = -0.1f, OrganelleRow = -0.22f, OrganelleSpan = 0.72f;
    const float DnaTitle = -0.42f, DnaTop = -0.5f, DnaBottom = -0.7f, DnaNames = -0.79f, DnaSpan = 0.56f;

    static readonly int ColorId = Shader.PropertyToID("_Color"), SweepFillId = Shader.PropertyToID("_FillColor");
    static readonly Color BaseA = new Color(1f, 0.16f, 0.3f), BaseT = new Color(0.12f, 0.55f, 1f),
                          BaseG = new Color(0.15f, 1f, 0.35f), BaseC = new Color(1f, 0.8f, 0.05f);

    CellInterior _cell;
    Camera _camera;
    bool _open, _placed;
    float _shown, _nextText, _scale = 1f;
    bool _refresh; // this frame rewrites the text (a few times a second: strings are garbage)
    Vector2 _centre, _goal;
    float _radius; // canvas units: half the texture's side

    Canvas _canvas;
    RectTransform _canvasRect;
    RawImage _disc;
    Material _strandMat;
    RenderTexture _texture, _display;
    Mesh _mesh;
    CommandBuffer _commands;
    MaterialPropertyBlock _props;
    Font _font;
    TerminalUI.Typed _kicker, _title;
    readonly List<Text> _labels = new List<Text>();
    readonly List<float> _fills = new List<float>(); // eased store fills
    int _label;
    ScreenInvertTest _sweep;

    public static NucleusView Create() => new GameObject("Nucleus View").AddComponent<NucleusView>();

    public void Open(CellInterior cell, Camera cam)
    {
        if (!Build()) return;
        bool fresh = !_open || cell != _cell;
        _cell = cell;
        _camera = cam;
        _open = true;
        _canvas.gameObject.SetActive(true);
        if (!fresh) return;
        _fills.Clear();
        _nextText = 0f;
        float start = Time.unscaledTime + animationTime * 0.6f;
        _kicker.start = _title.start = start;
    }

    public void Close() => _open = false;

    /// <summary>Whether a screen point is on the open disc (a click there is the view's, not the world's).</summary>
    public bool Covers(Vector2 screen)
    {
        if (!_open || !_canvas || _shown < 0.3f) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local)) return false;
        return Vector2.Distance(local - _canvasRect.rect.min, _centre) <= _radius * Rim;
    }

    void LateUpdate()
    {
        if (!_canvas) return;
        if (_open && !_cell) _open = false;
        float dt = Time.unscaledDeltaTime;
        _shown = Mathf.MoveTowards(_shown, _open ? 1f : 0f, dt / animationTime);
        Camera cam = _camera ? _camera : Camera.main;
        if (_shown <= 0f || !_cell || !cam || !CellInteriorView.Instance.Nucleus(_cell, out Vector3 nucleus, out float radius))
        {
            _placed = false;
            if (_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            return;
        }
        float now = Time.unscaledTime;

        // The nucleus on screen; the disc swells out of it, kept on screen, then follows it.
        Rect area = _canvasRect.rect;
        bool visible = ToCanvas(cam, nucleus, out Vector2 at);
        ToCanvas(cam, nucleus + cam.transform.right * radius, out Vector2 side);
        float nucleusR = Mathf.Max(4f, Vector2.Distance(at, side));
        float R = size * 0.5f;
        Vector2 fit = Fit(at, R, area);
        if (!_placed) { _goal = fit; _placed = true; }
        _goal = Vector2.Lerp(_goal, fit, 1f - Mathf.Exp(-follow * dt));

        float u = _shown;
        float move = Smooth(Mathf.Clamp01(u / 0.7f));
        float swell = BackOut(Mathf.Clamp01(u));
        _centre = Vector2.LerpUnclamped(at, _goal, move);
        float discR = Mathf.Max(nucleusR, Mathf.LerpUnclamped(nucleusR, R, swell)); // the membrane, canvas units
        _radius = discR / Rim;
        _scale = discR / R;
        float open = Mathf.Clamp01((u - 0.55f) / 0.45f);

        RectTransform r = _disc.rectTransform;
        r.anchoredPosition = _centre;
        r.sizeDelta = new Vector2(_radius * 2f, _radius * 2f);
        _disc.color = new Color(1f, 1f, 1f, visible ? 1f : 0f);

        _refresh = open > 0f && now >= _nextText; // not spent before the text shows
        if (_refresh) _nextText = now + 0.2f;
        _label = 0;
        if (Ready()) Draw(u, open, now, dt);
        for (int i = _label; i < _labels.Count; i++)
            if (_labels[i].gameObject.activeSelf) _labels[i].gameObject.SetActive(false);
        Titles(open, now);
    }

    // ---------------- drawing ----------------

    void Draw(float u, float open, float now, float dt)
    {
        _commands.Clear();
        _commands.SetRenderTarget(_texture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f));
        FlatMesh.Clear();

        Membrane(open, now);
        if (open > 0f)
        {
            Stores(open, now, dt);
            Organelles(open, now);
            Dna(open, now);
        }

        if (!_mesh)
        {
            _mesh = new Mesh { name = "Nucleus View", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
        }
        FlatMesh.Apply(_mesh);
        _props.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));
        _commands.DrawMesh(_mesh, Matrix4x4.identity, _strandMat, 0, 0, _props);
        _commands.Blit(_texture, _display); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);
    }

    // The nucleus itself, grown: its tinted body darkening to focus mode's fill as it opens, chromatin threads
    // fading back behind the readout, and its double membrane with pores (the outer line solid, the inner broken).
    void Membrane(float open, float now)
    {
        Color nucleus = CellInteriorView.Instance.nucleusColor;
        Color dark = SweepFill();
        Color body = Color.Lerp(new Color(nucleus.r * 0.35f, nucleus.g * 0.35f, nucleus.b * 0.45f, 0.75f), dark, open);
        FlatMesh.Disc(Vector2.zero, Rim, body, 72);

        // Chromatin: slow wavy threads, strong while it's still the nucleus, faint behind the readout.
        Color thread = nucleus;
        thread.a = Mathf.Lerp(0.45f, 0.07f, open);
        const int Threads = 6, Steps = 28;
        for (int t = 0; t < Threads; t++)
        {
            float y0 = -0.75f + t * 0.3f, phase = t * 1.7f;
            Vector2 prev = default;
            bool had = false;
            for (int i = 0; i <= Steps; i++)
            {
                float x = -0.85f + 1.7f * i / Steps;
                Vector2 p = new Vector2(x, y0 + 0.07f * Mathf.Sin(x * 5f + phase + now * 0.35f) + 0.04f * Mathf.Sin(x * 11f - phase));
                bool inside = p.sqrMagnitude < 0.8f * 0.8f;
                if (had && inside) FlatMesh.Quad(prev, p, 0.008f, thread, thread);
                prev = p;
                had = inside;
            }
        }
        // A nucleolus, fading as the readout comes.
        Color nucleolus = nucleus;
        nucleolus.a = 0.25f * (1f - open);
        if (nucleolus.a > 0.01f) FlatMesh.Disc(new Vector2(0.3f, -0.25f), 0.22f, nucleolus, 24);

        // Membranes: widths are canvas pixels, so scale with the disc.
        float w = outlineWidth / Mathf.Max(_radius, 1f);
        Color line = Tint(nucleus, 1.1f);
        FlatMesh.Ring(Vector2.zero, Rim, w, line, 96);
        const int Pores = 18, Arc = 6;
        Color inner = nucleus;
        inner.a = 0.7f;
        float innerR = Rim - w * 2.5f - 0.012f, spin = now * 0.05f;
        for (int p = 0; p < Pores; p++)
        {
            float a0 = (p + 0.18f) / Pores * Mathf.PI * 2f + spin, a1 = (p + 0.82f) / Pores * Mathf.PI * 2f + spin;
            Vector2 prev = Polar(a0, innerR);
            for (int i = 1; i <= Arc; i++)
            {
                Vector2 q = Polar(Mathf.Lerp(a0, a1, i / (float)Arc), innerR);
                FlatMesh.Quad(prev, q, w * 0.7f, inner, inner);
                prev = q;
            }
        }
    }

    // A bar per store, left to right with its fill, the code on the left and the percent on the right.
    void Stores(float open, float now, float dt)
    {
        List<StoreSlot> store = _cell.store;
        int n = store.Count;
        while (_fills.Count < n) _fills.Add(0f);
        float cap = Mathf.Max(_cell.Capacity, 1e-3f);
        float step = n > 1 ? Mathf.Min(0.12f, (BarsTop - BarsBottom) / (n - 1)) : 0f;
        float half = Mathf.Min(0.035f, step * 0.32f + (n <= 1 ? 0.035f : 0f));
        float top = n > 1 ? (BarsTop + BarsBottom) * 0.5f + step * (n - 1) * 0.5f : (BarsTop + BarsBottom) * 0.5f;
        int fontSize = FontSize(n > 5 ? 10 : 12);
        for (int s = 0; s < n; s++)
        {
            StoreSlot slot = store[s];
            float y = top - s * step;
            float reveal = Smooth(Mathf.Clamp01(open * 1.6f - s * 0.08f));
            float f = slot.Empty ? 0f : Mathf.Clamp01(slot.amount / cap);
            _fills[s] = Mathf.Lerp(_fills[s], f, 1f - Mathf.Exp(-dt / 0.15f));
            Color c = slot.Empty ? TerminalUI.Line : slot.color;

            float left = -BarHalf, right = Mathf.Lerp(-BarHalf, BarHalf, reveal);
            Color frame = slot.Empty ? new Color(c.r, c.g, c.b, 0.3f) : Tint(c, 0.85f);
            frame.a *= open;
            float t = 0.006f;
            FlatMesh.Quad(new Vector2(left, y + half), new Vector2(right, y + half), t, frame, frame);
            FlatMesh.Quad(new Vector2(left, y - half), new Vector2(right, y - half), t, frame, frame);
            FlatMesh.Quad(new Vector2(left, y - half - t * 0.5f), new Vector2(left, y + half + t * 0.5f), t, frame, frame);
            FlatMesh.Quad(new Vector2(right, y - half - t * 0.5f), new Vector2(right, y + half + t * 0.5f), t, frame, frame);
            for (int q = 1; q <= 3; q++)
            {
                float x = Mathf.Lerp(-BarHalf, BarHalf, q * 0.25f);
                if (x < right) FlatMesh.Quad(new Vector2(x, y - half), new Vector2(x, y - half * 0.4f), t * 0.8f, frame, frame);
            }
            float fill = _fills[s] * reveal;
            if (!slot.Empty && fill > 0.002f)
            {
                float front = Mathf.Lerp(-BarHalf, BarHalf, fill);
                Color body = c;
                body.a = 0.85f * open;
                FlatMesh.Quad(new Vector2(left + t, y), new Vector2(front, y), 2f * (half - t), body, body);
                Color lip = Color.Lerp(c, Color.white, 0.5f + 0.3f * Mathf.Sin(now * 6f + s));
                lip.a = open;
                FlatMesh.Quad(new Vector2(front, y - half + t), new Vector2(front, y + half - t), t * 1.6f, lip, lip);
            }

            Label(new Vector2(-BarHalf - LabelGap, y), 1f, fontSize, !_refresh ? null : slot.Empty ? "--" : slot.code, c, open);
            Label(new Vector2(BarHalf + LabelGap, y), 0f, fontSize, !_refresh ? null : slot.Empty ? "" : Mathf.RoundToInt(f * 100f) + "%", c, open);
        }
    }

    // A small circle per slot the limit allows: filled in its organelle's colour (a ring pulsing out while it
    // works), hollow when free. Wraps to a second row past 10.
    void Organelles(float open, float now)
    {
        int limit = _cell.Limit, count = _cell.OrganelleCount;
        Label(new Vector2(0f, OrganelleTitle), 0.5f, FontSize(11),
              !_refresh ? null : "ORGANELLES " + count + " / " + limit, Dim(TerminalUI.Line), open);
        if (limit <= 0) return;
        int perRow = Mathf.Min(limit, 10), rows = (limit + 9) / 10;
        float gap = OrganelleSpan * 2f / Mathf.Max(perRow, 1);
        float r = Mathf.Min(0.05f, gap * 0.36f), rowGap = r * 2.6f;
        int j = 0;
        foreach (CellInterior.Organelles o in _cell.organelles)
            for (int k = 0; k < o.count && j < limit; k++, j++)
                Slot(j, perRow, rows, gap, r, rowGap, o.type ? o.type.color : TerminalUI.Line, o.activity, true, open, now);
        for (; j < limit; j++) Slot(j, perRow, rows, gap, r, rowGap, TerminalUI.Line, 0f, false, open, now);
    }

    void Slot(int j, int perRow, int rows, float gap, float r, float rowGap, Color c, float activity, bool filled,
              float open, float now)
    {
        int row = j / perRow, col = j % perRow;
        int inRow = row < rows - 1 ? perRow : _cell.Limit - row * perRow;
        Vector2 at = new Vector2((col - (inRow - 1) * 0.5f) * gap, OrganelleRow - row * rowGap);
        float reveal = Smooth(Mathf.Clamp01(open * 1.8f - j * 0.05f));
        float rr = r * reveal;
        if (rr <= 1e-4f) return;
        if (!filled)
        {
            Color ring = new Color(c.r, c.g, c.b, 0.35f * open);
            FlatMesh.Ring(at, rr, 0.006f, ring, 24);
            return;
        }
        Color body = c;
        body.a = 0.9f * open;
        FlatMesh.Disc(at, rr, body, 24);
        Color rim = Tint(c, 1.3f);
        rim.a = open;
        FlatMesh.Ring(at, rr, 0.007f, rim, 24);
        if (activity > 0.02f)
        {
            float wave = Mathf.Repeat(now * 0.8f + j * 0.37f, 1f);
            Color pulse = c;
            pulse.a = activity * (1f - wave) * 0.8f * open;
            FlatMesh.Ring(at, rr * (1.05f + wave * 0.7f), 0.006f, pulse, 24);
        }
    }

    // The foreign DNA inside: a small upright helix per strand in its colour (dim and still: inert here), the
    // codes under them.
    void Dna(float open, float now)
    {
        List<CellInterior.Strand> dna = _cell.dna;
        int n = dna.Count;
        Label(new Vector2(0f, DnaTitle), 0.5f, FontSize(11), !_refresh ? null : n == 0 ? "NO FOREIGN DNA" : "DNA", Dim(TerminalUI.Line), open);
        if (n == 0) return;
        float gap = n > 1 ? Mathf.Min(0.16f, DnaSpan * 2f / (n - 1)) : 0f;
        for (int i = 0; i < n; i++)
        {
            CellInterior.Strand strand = dna[i];
            float x = (i - (n - 1) * 0.5f) * gap;
            float reveal = Smooth(Mathf.Clamp01(open * 1.8f - i * 0.1f));
            float bottom = Mathf.Lerp(DnaTop, DnaBottom, reveal);
            Helix(x, DnaTop, bottom, strand.color, strand.took, i, open, now);
        }
        if (_refresh)
        {
            _names.Clear();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) _names.Append("  ");
                _names.Append(dna[i].code);
                if (!dna[i].took) _names.Append('*');
            }
        }
        Label(new Vector2(0f, DnaNames), 0.5f, FontSize(n > 4 ? 9 : 11), _refresh ? _names.ToString() : null, TerminalUI.Text, open);
    }

    readonly System.Text.StringBuilder _names = new System.Text.StringBuilder();

    // An upright double helix from 'top' down to 'bottom': rungs colour-coded by base (the head view's colours),
    // two backbones in the gene's colour; it turns slowly while it's working in the cell.
    static void Helix(float x, float top, float bottom, Color color, bool took, int seed, float open, float now)
    {
        float length = top - bottom;
        if (length <= 0.005f) return;
        const float Amp = 0.035f, Pitch = 0.11f;
        float k = Mathf.PI * 2f / Pitch, phase = seed * 1.3f + (took ? now * 2f : 0f);
        float dim = took ? 1f : 0.45f;
        for (int j = 0; (j + 0.5f) * Pitch / 5f <= length; j++)
        {
            float s = (j + 0.5f) * Pitch / 5f;
            float wave = Mathf.Sin(s * k + phase);
            if (Mathf.Abs(wave) < 0.3f) continue;
            uint h = (uint)j * 73856093u ^ (uint)seed * 19349663u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            Color a, b;
            switch (h & 3u)
            {
                case 0: a = BaseA; b = BaseT; break;
                case 1: a = BaseT; b = BaseA; break;
                case 2: a = BaseG; b = BaseC; break;
                default: a = BaseC; b = BaseG; break;
            }
            a = Tint(a, dim); b = Tint(b, dim);
            a.a = b.a = open * (took ? 0.9f : 0.6f);
            Vector2 at = new Vector2(x, top - s), off = new Vector2(Amp * wave, 0f);
            FlatMesh.Quad(at + off, at, 0.006f, a, a);
            FlatMesh.Quad(at, at - off, 0.006f, b, b);
        }
        Color line = Tint(color, took ? 1.2f : 0.6f);
        line.a = open;
        const int Steps = 20;
        for (int side = -1; side <= 1; side += 2)
        {
            Vector2 prev = default;
            for (int i = 0; i <= Steps; i++)
            {
                float s = length * i / Steps;
                Vector2 p = new Vector2(x + side * Amp * Mathf.Sin(s * k + phase), top - s);
                if (i > 0) FlatMesh.Quad(prev, p, 0.01f, line, line);
                prev = p;
            }
        }
    }

    // ---------------- text ----------------

    // A label at a disc point, 'pivotX' 0 = starts there, 1 = ends there, 0.5 = centred. 's' null: text kept.
    void Label(Vector2 local, float pivotX, int fontSize, string s, Color color, float open)
    {
        int i = _label++;
        while (_labels.Count <= i)
        {
            Text t = TerminalUI.Graphic<Text>("Label", _canvasRect, Vector2.zero, new Vector2(80f, 16f));
            BottomLeft(t.rectTransform);
            TerminalUI.Style(t, _font, 12, TerminalUI.Line, TextAnchor.MiddleCenter);
            t.raycastTarget = false;
            _labels.Add(t);
        }
        Text label = _labels[i];
        bool on = open > 0.05f;
        if (label.gameObject.activeSelf != on) label.gameObject.SetActive(on);
        if (!on) return;
        if (label.fontSize != fontSize) label.fontSize = fontSize;
        if (s != null && label.text != s) label.text = s;
        label.alignment = pivotX < 0.25f ? TextAnchor.MiddleLeft : pivotX > 0.75f ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
        label.color = new Color(color.r, color.g, color.b, color.a * open);
        RectTransform r = label.rectTransform;
        r.sizeDelta = new Vector2(label.preferredWidth + 4f, fontSize + 6f);
        r.pivot = new Vector2(pivotX, 0.5f);
        r.anchoredPosition = _centre + local * _radius;
    }

    int FontSize(int at1080) => Mathf.Max(6, Mathf.RoundToInt(at1080 * Mathf.Clamp01(_scale)));

    // "NUCLEUS" and the cell's name and id, inside the top of the disc.
    void Titles(float open, float now)
    {
        if (_refresh)
            _title.Set(_cell.DisplayName + " // " + (_cell.Profile ? _cell.Profile.code : "CEL") + "-" + (_cell.Seed & 0xffff).ToString("X4"));
        Place(_kicker, 0.72f, open, now);
        Place(_title, 0.6f, open, now);
    }

    void Place(TerminalUI.Typed row, float y, float open, float now)
    {
        RectTransform r = row.text.rectTransform;
        r.anchoredPosition = _centre + new Vector2(0f, y * _radius);
        row.text.canvasRenderer.SetAlpha(open);
        row.Tick(now, typeSpeed);
    }

    static Color Dim(Color c) => new Color(c.r, c.g, c.b, 0.65f);

    static Color Tint(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

    static Vector2 Polar(float a, float r) => new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);

    // Focus mode's fill (the sweep's), so the open nucleus reads as a window into it.
    Color SweepFill()
    {
        Color fill = new Color(0.016f, 0.055f, 0.16f, 0.94f);
        if (!_sweep) _sweep = FindAnyObjectByType<ScreenInvertTest>();
        Material m = _sweep ? _sweep.screenInvertMaterial : null;
        if (m && m.HasProperty(SweepFillId)) { Color c = m.GetColor(SweepFillId); fill = new Color(c.r, c.g, c.b, 0.94f); }
        return fill;
    }

    // ---------------- placement ----------------

    static float Smooth(float x) => x * x * (3f - 2f * x);

    static float BackOut(float x)
    {
        const float s = 1.4f;
        x -= 1f;
        return 1f + x * x * ((s + 1f) * x + s);
    }

    // A disc centre kept on the canvas.
    static Vector2 Fit(Vector2 g, float R, Rect area)
    {
        g.x = Mathf.Clamp(g.x, R + 24f, Mathf.Max(R + 24f, area.width - R - 24f));
        g.y = Mathf.Clamp(g.y, R + 24f, Mathf.Max(R + 24f, area.height - R - 24f));
        return g;
    }

    // A world point in canvas units from the bottom left; false if behind.
    bool ToCanvas(Camera cam, Vector3 world, out Vector2 at)
    {
        at = _canvasRect.rect.size * 0.5f;
        Vector3 screen = cam.WorldToScreenPoint(world);
        if (screen.z <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local))
            return false;
        at = local - _canvasRect.rect.min;
        return true;
    }

    // ---------------- building ----------------

    bool Build()
    {
        if (_canvas && _title != null) return Ready();
        if (_canvas) Destroy(_canvas.gameObject); // a play-mode script reload dropped the plain parts
        Shader strand = strandShader ? strandShader : Shader.Find("Hidden/GenomeStrand");
        if (!strand) return false;
        _strandMat = new Material(strand) { hideFlags = HideFlags.DontSave };
        _font = TerminalUI.Font(TerminalUI.DefaultFonts);
        _labels.Clear();

        _canvas = TerminalUI.Canvas("Nucleus View Canvas", transform, 590);
        _canvasRect = (RectTransform)_canvas.transform;
        foreach (GraphicRaycaster r in _canvas.GetComponents<GraphicRaycaster>()) r.enabled = false; // hit-tested by Covers
        _disc = TerminalUI.Graphic<RawImage>("Disc", _canvasRect, Vector2.zero, Vector2.zero);
        BottomLeft(_disc.rectTransform);
        _disc.raycastTarget = false;

        _kicker = Typed(11, Dim(TerminalUI.Line), 0f);
        _kicker.full = "NUCLEUS";
        _title = Typed(14, TerminalUI.Text, 0.1f);
        _canvas.gameObject.SetActive(false);
        return Ready();
    }

    TerminalUI.Typed Typed(int fontSize, Color color, float delay)
    {
        Text t = TerminalUI.Graphic<Text>("Title", _canvasRect, Vector2.zero, new Vector2(size, fontSize + 8f));
        BottomLeft(t.rectTransform);
        TerminalUI.Style(t, _font, fontSize, color, TextAnchor.MiddleCenter);
        t.raycastTarget = false;
        return new TerminalUI.Typed { text = t, delay = delay, start = Time.unscaledTime };
    }

    // Positioned in canvas units from the bottom left.
    static RectTransform BottomLeft(RectTransform r)
    {
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = new Vector2(0.5f, 0.5f);
        return r;
    }

    // Everything drawing needs, made or remade (a script reload drops the plain C# parts).
    bool Ready()
    {
        _commands ??= new CommandBuffer { name = "Nucleus View" };
        _props ??= new MaterialPropertyBlock();
        if (!_texture || !_display || _texture.width != resolution)
        {
            Release();
            _texture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                name = "Nucleus View", hideFlags = HideFlags.DontSave, antiAliasing = 4,
            };
            _texture.Create();
            _display = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                name = "Nucleus View Display", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _display.Create();
        }
        if (_disc && _disc.texture != _display) _disc.texture = _display;
        return _strandMat;
    }

    void Release()
    {
        if (_texture) { _texture.Release(); Destroy(_texture); }
        if (_display) { _display.Release(); Destroy(_display); }
    }

    void OnDestroy()
    {
        Release();
        if (_strandMat) Destroy(_strandMat);
        if (_mesh) Destroy(_mesh);
        _commands?.Release();
        _commands = null;
    }
}

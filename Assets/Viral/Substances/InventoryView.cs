using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;
using Kind = VirusInventory.Kind;
using Mount = VirusInventory.Mount;
using static FlatMesh;

/// <summary>
/// The head at a glance, in the top-left corner: a small, plainer version of the head view's sphere
/// (GenomeView). A dark disc with a thin cyan rim and the same mounts in the same order round its
/// inside: DNA slots as short ladders in the gene's colour (the loaded one lit), store slots as bars
/// filling from the rim in their substance's colour (flashing as they fill), empty slots as a dash.
/// Beside it: the loaded strand and the key that opens the full view. Always up; it folds away while
/// the head view is open (the full sphere shows the same thing). Read only: it never takes a click.
///
/// One small mesh (FlatMesh, Hidden/GenomeStrand) drawn into a 256px MSAA texture by a command
/// buffer while it's shown. VirusMovement binds it. Builds itself; nothing to set up.
/// </summary>
[DefaultExecutionOrder(100)] // after GenomeView this frame
public class InventoryView : MonoBehaviour
{
    [Min(40f), Tooltip("Circle diameter, in 1080p pixels.")]
    public float size = 150f;
    [Tooltip("Distance from the top-left corner, in 1080p pixels.")]
    public Vector2 margin = new Vector2(24f, 24f);
    [Min(1f)] public float typeSpeed = 110f;
    [Range(128, 1024)] public int resolution = 256;

    [Header("Colours")]
    public Color panel = TerminalUI.Panel;
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;

    [Header("Type")]
    public Font font;
    public string[] terminalFonts = TerminalUI.DefaultFonts;
    [Tooltip("Hidden/GenomeStrand. Empty: the head view's, else found by name (editor only).")]
    public Shader strandShader;

    const float Rim = 0.86f, Length = 0.5f; // where the mounts start and how far in they reach (radii)

    GenomeView _view;
    VirusInventory _inventory;
    Genome _genome;
    float _shown;
    bool _wasShown;

    // Per mount: the bar's shown fill and when it last grew (its flash); angles ease to their places.
    class Look { public float angle = float.NaN, fill, last, flashAt = -10f; }
    readonly Dictionary<Mount, Look> _looks = new Dictionary<Mount, Look>();

    Canvas _canvas;
    RectTransform _canvasRect, _root;
    CanvasGroup _group;
    RawImage _image;
    Font _font;
    Typed _title, _loaded, _hint;
    Material _material;
    Mesh _mesh;
    RenderTexture _texture, _display;
    // Not kept by a play-mode script reload: remade on use.
    CommandBuffer _commands;
    MaterialPropertyBlock _props;

    public static InventoryView Create() => new GameObject("Inventory View").AddComponent<InventoryView>();

    /// <summary>Shows 'inventory' and 'genome' in the corner, folded away while 'view' (the full head) is open.</summary>
    public void Bind(GenomeView view, VirusInventory inventory, Genome genome)
    {
        _view = view;
        _inventory = inventory;
        _genome = genome;
    }

    void LateUpdate()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
        }
        if (!_inventory || !_genome)
        {
            if (_canvas && _canvas.enabled) _canvas.enabled = false;
            return;
        }
        if (!_canvas && !Build()) return;
        float now = Time.unscaledTime, dt = Time.unscaledDeltaTime;

        // Folds away (shrinking into its corner) as the head view opens, back when it closes.
        bool want = !(_view && (_view.IsOpen || _view.Shown > 0.5f)) && !CommandMode.Active;
        _shown = Mathf.MoveTowards(_shown, want ? 1f : 0f, dt / 0.25f);
        if (want && !_wasShown) _title.start = _loaded.start = _hint.start = now;
        _wasShown = want;
        _canvas.enabled = _shown > 0f;
        if (_shown <= 0f) return;
        float k = _shown * _shown * (3f - 2f * _shown);
        _group.alpha = k;
        _root.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, k);

        Genome.Gene gene = _genome.SelectedGene;
        _loaded.Set(gene != null ? gene.code + " LOADED" : "NO STRAND");
        _loaded.text.color = gene != null ? gene.color : new Color(line.r, line.g, line.b, 0.6f);
        _title.Tick(now, typeSpeed);
        _loaded.Tick(now, typeSpeed);
        _hint.Tick(now, typeSpeed);

        Draw(now, dt, k);
    }

    void Draw(float now, float dt, float reveal)
    {
        IReadOnlyList<Mount> ring = _inventory.Ring(_genome.genes.Count);
        int n = Mathf.Max(ring.Count, 1);
        float gap = 360f / n, ease = 1f - Mathf.Exp(-dt * 12f);
        float half = Mathf.Min(0.07f, Mathf.PI * (Rim - Length) / n * 0.75f); // mount half width

        FlatMesh.Clear();
        Disc(Vector2.zero, 0.985f, new Color(panel.r, panel.g, panel.b, 1f), 64);
        Ring(Vector2.zero, 0.955f, 0.035f, line);
        Ring(Vector2.zero, Rim - Length - 0.08f, 0.012f, new Color(line.r, line.g, line.b, 0.25f), 48); // inner guide

        int selected = _genome.Selected;
        for (int i = 0; i < ring.Count; i++)
        {
            Mount m = ring[i];
            if (!_looks.TryGetValue(m, out Look l)) _looks[m] = l = new Look();
            float target = (i + 0.5f) * gap;
            l.angle = float.IsNaN(l.angle) ? target : l.angle + Mathf.DeltaAngle(l.angle, target) * ease;
            float a = (l.angle - 90f) * Mathf.Deg2Rad; // the gap between the first and last at the bottom
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            Vector2 across = new Vector2(-dir.y, dir.x), root = dir * Rim;
            float len = Length * reveal;

            Taper(dir * 0.95f, root, half * 1.5f, half * 1.1f, line); // its base on the rim

            if (m.kind == Kind.Gene)
            {
                if (m.index < 0 || m.index >= _genome.genes.Count)
                {
                    Color dim = new Color(line.r, line.g, line.b, 0.3f);
                    Quad(root - dir * 0.05f, root - dir * (len * 0.6f), 0.03f, dim, dim);
                    continue;
                }
                // A ladder: two rails and a few rungs, in the gene's colour; the loaded one lit, the rest dimmed.
                Color c = Saturated(_genome.genes[m.index].color);
                float b = selected < 0 ? 0.9f : m.index == selected ? 1.25f + 0.2f * Mathf.Sin(now * 5f) : 0.45f;
                c = new Color(c.r * b, c.g * b, c.b * b, 1f);
                Vector2 end = root - dir * len;
                Quad(root + across * half * 0.8f, end + across * half * 0.8f, 0.022f, c, c);
                Quad(root - across * half * 0.8f, end - across * half * 0.8f, 0.022f, c, c);
                for (float s = 0.08f; s < len - 0.03f; s += 0.1f)
                {
                    Vector2 p = root - dir * s;
                    Quad(p + across * half * 0.8f, p - across * half * 0.8f, 0.03f, c, c);
                }
                continue;
            }

            VirusInventory.Slot slot = m.index >= 0 && m.index < _inventory.slots.Count ? _inventory.slots[m.index] : null;
            bool empty = slot == null || slot.Empty;
            float amount = empty ? 0f : slot.amount;
            if (amount > l.last + 1e-3f) l.flashAt = now;
            l.last = amount;
            float want = Mathf.Clamp01(amount / Mathf.Max(_inventory.capacity, 1e-3f));
            l.fill = Mathf.MoveTowards(Mathf.Lerp(l.fill, want, ease), want, dt * 0.05f);

            Color sc = empty ? line : Saturated(slot.color);
            Color frame = empty ? new Color(line.r, line.g, line.b, 0.35f) : new Color(sc.r, sc.g, sc.b, 0.9f);
            Vector2 inner = root - dir * len;
            Quad(root + across * half, inner + across * half, 0.02f, frame, frame);
            Quad(root - across * half, inner - across * half, 0.02f, frame, frame);
            Quad(inner + across * (half + 0.01f), inner - across * (half + 0.01f), 0.02f, frame, frame);
            if (empty || l.fill <= 0.002f) continue;
            float flash = Mathf.Clamp01(1f - (now - l.flashAt) / 0.35f);
            float f = 0.8f + 0.5f * flash;
            Color fill = new Color(sc.r * f, sc.g * f, sc.b * f, 0.9f);
            Quad(root, root - dir * (len * l.fill), 2f * half - 0.04f, fill, fill);
        }

        if (!_mesh)
        {
            _mesh = new Mesh { name = "Head Readout", hideFlags = HideFlags.DontSave };
            _mesh.MarkDynamic();
        }
        Apply(_mesh);
        Ready();
        _props.SetColor("_Color", new Color(1f, 1f, 1f, 0f));
        _commands.Clear();
        _commands.SetRenderTarget(_texture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f));
        _commands.DrawMesh(_mesh, Matrix4x4.identity, _material, 0, 0, _props);
        _commands.Blit(_texture, _display); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);
    }

    static Color Saturated(Color c)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        return Color.HSVToRGB(h, Mathf.Max(s, 0.85f), 1f);
    }

    // ---------------- building ----------------

    bool Build()
    {
        Shader shader = strandShader ? strandShader : _view && _view.strandShader ? _view.strandShader : Shader.Find("Hidden/GenomeStrand");
        if (!shader)
        {
            Debug.LogError("InventoryView: assign Hidden/GenomeStrand.", this);
            enabled = false;
            return false;
        }
        _material = new Material(shader) { hideFlags = HideFlags.DontSave };
        _font = font ? font : TerminalUI.Font(terminalFonts);
        _canvas = TerminalUI.Canvas("Inventory Canvas", transform, 560);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // read only, never eats clicks
        _canvasRect = (RectTransform)_canvas.transform;

        _root = TerminalUI.Rect("Head", _canvasRect, Vector2.zero, Vector2.one * size);
        _root.anchorMin = _root.anchorMax = _root.pivot = new Vector2(0f, 1f); // from the top-left corner
        _root.anchoredPosition = new Vector2(margin.x, -margin.y);
        _group = _root.gameObject.AddComponent<CanvasGroup>();
        _group.blocksRaycasts = _group.interactable = false;

        _image = TerminalUI.Graphic<RawImage>("Circle", _root, Vector2.zero, Vector2.zero);
        RectTransform r = _image.rectTransform;
        r.anchorMin = Vector2.zero;
        r.anchorMax = Vector2.one;
        r.sizeDelta = Vector2.zero;
        _image.raycastTarget = false;

        _title = Line(0f, 12, new Color(line.r, line.g, line.b, 0.7f), 0f);
        _title.full = "05 // HEAD";
        _loaded = Line(-18f, 14, text, 0.1f);
        _hint = Line(-38f, 11, new Color(line.r, line.g, line.b, 0.6f), 0.2f);
        _hint.full = "[E] OPEN";
        Ready();
        return true;
    }

    // A line of text to the right of the circle, 'y' down from its top third.
    Typed Line(float y, int fontSize, Color color, float delay)
    {
        Text t = TerminalUI.Graphic<Text>("Line", _root, Vector2.zero, new Vector2(200f, fontSize + 6f));
        t.rectTransform.anchorMin = t.rectTransform.anchorMax = new Vector2(1f, 0.7f);
        t.rectTransform.pivot = new Vector2(0f, 0.5f);
        t.rectTransform.anchoredPosition = new Vector2(12f, y);
        TerminalUI.Style(t, _font, fontSize, color, TextAnchor.MiddleLeft);
        return new Typed { text = t, delay = delay, start = Time.unscaledTime };
    }

    void Ready()
    {
        _commands ??= new CommandBuffer { name = "Head Readout" };
        _props ??= new MaterialPropertyBlock();
        if (_texture && _texture.width == resolution && _display) return;
        Release();
        _texture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
        {
            name = "Head Readout", hideFlags = HideFlags.DontSave, antiAliasing = 4,
        };
        _texture.Create();
        _display = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
        {
            name = "Head Readout Display", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        _display.Create();
        if (_image) _image.texture = _display;
    }

    void Release()
    {
        if (_texture) { _texture.Release(); Destroy(_texture); }
        if (_display) { _display.Release(); Destroy(_display); }
        _texture = _display = null;
    }

    void OnDestroy()
    {
        Release();
        if (_mesh) Destroy(_mesh);
        if (_material) Destroy(_material);
        _commands?.Release();
        _commands = null;
    }
}

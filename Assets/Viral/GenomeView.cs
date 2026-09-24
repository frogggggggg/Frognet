using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;

/// <summary>
/// The virus's head, opened up: in focus mode a click on the virus (VirusMovement) grows a tube out of
/// its head on screen that swells into a big sphere off to one side (flaring out of the head, pinched
/// into the sphere like a drop), in focus mode's own dark fill and outline, with the virus's DNA
/// strands (Genome) inside: a flat 2D diagram, each gene a double helix running in from the sphere's
/// outline toward its middle like a spoke (spaced so the tube's mouth falls midway between two), in
/// plain saturated colours. Still until hovered, when it jiggles. Hover a strand to read it; click it
/// to inject it (Genome.Select, for now a test animation): it's drawn out through the tube, shrinking
/// as it funnels down into the ball, through the body and down the InjectionDrill into the cell
/// (a ripple there, Genome.Delivered), then grows back. Closing draws the sphere back into the head.
///
/// The bubble is one full-screen UI image (Hidden/GenomeBubble: head, tube and sphere smooth-unioned
/// as a distance field, in canvas units). The strands are drawn flat (orthographic, the sphere's radius
/// = 1) straight into a render texture by a command buffer: no camera or scene objects.
/// Builds everything on first use; nothing to set up.
/// </summary>
public class GenomeView : MonoBehaviour
{
    [Tooltip("Seconds to open or close.")]
    [Min(0.01f)] public float animationTime = 0.55f;
    [Tooltip("Characters typed per second as text appears or changes.")]
    [Min(1f)] public float typeSpeed = 90f;
    [Min(64f), Tooltip("Sphere diameter, in 1080p pixels.")]
    public float size = 440f;
    [Min(0f), Tooltip("Tube length from the head to the sphere, in 1080p pixels.")]
    public float rise = 90f;
    [Range(0.05f, 1f), Tooltip("Tube thickness, as a share of the head's radius on screen.")]
    public float neckWidth = 0.3f;
    [Range(0f, 90f), Tooltip("How far off the virus's up the sphere settles, to whichever side fits the screen.")]
    public float sideAngle = 50f;
    [Range(256, 2048), Tooltip("DNA render texture resolution.")]
    public int resolution = 1024;

    [Header("Colours")]
    [Tooltip("Use the focus sweep's fill and player outline colours (ScreenInvertTest's material), so the " +
             "sphere looks like the head itself; the two below when off or there's no sweep.")]
    public bool matchFocusSweep = true;
    public Color outline = new Color(0.95f, 0.97f, 1f, 1f);
    public Color inside = new Color(0.016f, 0.055f, 0.16f, 1f);
    public Color line = TerminalUI.Line;
    public Color live = TerminalUI.Live;
    public Color text = TerminalUI.Text;
    [Min(0f), Tooltip("Outline width, in 1080p pixels.")]
    public float outlineWidth = 3f;

    [Header("Strands")]
    [Range(0.2f, 0.9f), Tooltip("How far in from the outline the strands reach (sphere radii).")]
    public float strandLength = 0.6f;
    [Range(0f, 0.2f), Tooltip("How far a strand jiggles when the pointer comes onto it (sphere radii).")]
    public float jiggle = 0.06f;
    [Min(0.1f), Tooltip("Seconds for an injected strand to travel from the sphere down the drill.")]
    public float injectDuration = 2.4f;
    [Range(0.1f, 0.9f), Tooltip("Share of that time spent getting from the sphere into the head; the rest goes down the virus and the drill.")]
    public float headShare = 0.45f;
    [Min(2f), Tooltip("The travelling strand's smallest width on screen (1080p pixels), however thin the tube.")]
    public float minInjectWidth = 14f;

    [Header("Type")]
    [Tooltip("Any font asset. Empty: the first installed of Terminal Fonts.")]
    public Font font;
    public string[] terminalFonts = TerminalUI.DefaultFonts;

    [Header("Shaders")]
    [Tooltip("Hidden/GenomeBubble. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader bubbleShader;
    [Tooltip("Hidden/GenomeStrand.")]
    public Shader strandShader;

    public bool IsOpen => _open;
    public Genome Genome => _genome;

    /// <summary>Whether a screen point is on the open sphere (a click there is the view's, not the world's).</summary>
    public bool Covers(Vector2 screen)
    {
        if (!_open || !_canvasRect || _shown <= 0f) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local)) return false;
        return (local - _canvasRect.rect.min - _sphere).sqrMagnitude <= _sphereR * _sphereR;
    }

    const int Samples = 64;     // along a strand's backbones
    const float Pitch = 0.2f;   // one helix turn, in sphere radii
    const float Rim = 0.93f;    // where the strands start, in from the outline
    const float BackboneWidth = 0.011f, RungWidth = 0.014f; // sphere radii (backbone: half width)

    static readonly int ColorId = Shader.PropertyToID("_Color"), FillId = Shader.PropertyToID("_Fill"),
                        OutlineId = Shader.PropertyToID("_Outline"), BubbleAId = Shader.PropertyToID("_BubbleA"),
                        BubbleBId = Shader.PropertyToID("_BubbleB"), AreaId = Shader.PropertyToID("_BubbleArea"),
                        DnaId = Shader.PropertyToID("_BubbleDNA"), SweepFillId = Shader.PropertyToID("_FillColor"),
                        SweepOutlineId = Shader.PropertyToID("_HighlightColor"), HeadId = Shader.PropertyToID("_BubbleHead");

    Genome _genome;
    Camera _camera;
    bool _open;
    float _shown, _openedAt;
    int _hover = -1;
    Vector2 _sphere;   // sphere centre (canvas units, from the bottom left) this frame, for picking
    float _sphereR;
    Color _fill, _outline;
    float _goalSide; // which side the sphere settles on, chosen once per opening (0: not yet)
    ScreenInvertTest _sweep;

    Canvas _canvas;
    RectTransform _canvasRect, _hit;
    CanvasGroup _group;
    RawImage _bubble;
    Font _font;
    readonly List<Object> _made = new List<Object>();
    readonly List<Typed> _typed = new List<Typed>();
    Typed _title, _code, _state;

    RenderTexture _texture, _display;
    Material _bubbleMat, _strandMat;
    readonly List<Mesh> _strands = new List<Mesh>();          // one per gene, rebuilt each frame
    readonly List<float> _jiggleAt = new List<float>();       // per strand: when it was last poked
    readonly List<float> _regrowAt = new List<float>();       // per strand: when it came back from an injection
    Vector2 _entry = Vector2.down; // from the sphere's centre toward the tube's mouth

    // The injection test: one strand at a time travelling out along _path (canvas units).
    int _injecting = -1;
    float _injectAt;
    bool _delivered;
    // Drawn like the strands in the sphere (command buffer into a render texture, canvas-sized, shown
    // by a full-screen image): as a UIShape on the canvas it never showed.
    RawImage _traveller;
    RenderTexture _travelTexture, _travelDisplay;
    Mesh _travelMesh;
    MaterialPropertyBlock _travelProps;
    InjectionDrill _drill;
    readonly List<Vector2> _path = new List<Vector2>(), _pathTemp = new List<Vector2>();
    readonly List<float> _arc = new List<float>();
    readonly List<Text> _labels = new List<Text>();
    // Not kept by a play-mode script reload: remade on use.
    CommandBuffer _commands;
    List<MaterialPropertyBlock> _props;

    public static GenomeView Create() => new GameObject("Genome View").AddComponent<GenomeView>();

    public void Open(Genome genome, Camera cam)
    {
        if (!Build()) return;
        bool fresh = !_open || genome != _genome;
        _genome = genome;
        _camera = cam;
        _open = true;
        _canvas.gameObject.SetActive(true);
        if (fresh)
        {
            _openedAt = Time.unscaledTime;
            foreach (Typed t in _typed) t.start = _openedAt + animationTime * 0.6f; // once the sphere is up
        }
    }

    public void Close()
    {
        _open = false;
        _hover = -1;
    }

    void LateUpdate()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again on open
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _labels.Clear();
            _typed.Clear();
            _open = false;
            _injecting = -1;
        }
        if (!_canvas) return;
        if (_open && !_genome) Close();

        _shown = Mathf.MoveTowards(_shown, _open ? 1f : 0f, Time.unscaledDeltaTime / animationTime);
        if ((_shown <= 0f && !_open && _injecting < 0) || !_genome)
        {
            _injecting = -1;
            ReleaseTravel();
            _goalSide = 0f; // choose again next time
            if (_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            return;
        }
        float now = Time.unscaledTime;

        // The head on screen (a sphere) and the way the virus faces on screen. The sphere settles off
        // to one side of that (whichever fits the screen better), well clear of the virus, kept on
        // screen with room for the labels.
        Camera cam = _camera ? _camera : Camera.main;
        if (!cam) return;
        Vector3 head = _genome.HeadSphere(out float headRadius);
        Vector3 up = head - _genome.transform.position;
        up = up.sqrMagnitude > 1e-6f ? up.normalized : _genome.transform.up;
        Rect area = _canvasRect.rect;
        bool visible = ToCanvas(cam, head, out Vector2 headAt);
        ToCanvas(cam, head + up * headRadius, out Vector2 top);
        ToCanvas(cam, head + cam.transform.right * headRadius, out Vector2 side);
        float headR = Mathf.Max(4f, Vector2.Distance(headAt, side));
        Vector2 dir = top - headAt;
        dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector2.up;
        float tube = Mathf.Max(3f, headR * neckWidth);

        float R = size * 0.5f;
        if (_goalSide == 0f) _goalSide = PickSide(headAt, dir, headR, R, area); // once per opening
        Vector2 way = Rotate(dir, _goalSide * sideAngle);
        Vector2 goal = headAt + way * (headR + rise + R);
        goal.x = Mathf.Clamp(goal.x, R + 24f, Mathf.Max(R + 24f, area.width - R - 24f));
        goal.y = Mathf.Clamp(goal.y, R + 24f, Mathf.Max(R + 24f, area.height - R - 96f));

        // The tube grows out of the head toward it, and its end swells into the sphere (a little
        // overshoot, like a drop). Closing plays it backwards, back into the head.
        float u = _shown;
        float reach = Smooth(Mathf.Clamp01(u / 0.55f));
        float swell = BackOut(Mathf.Clamp01((u - 0.3f) / 0.7f));
        Vector2 start = headAt + (goal - headAt).normalized * headR; // on the head's edge
        Vector2 end = Vector2.LerpUnclamped(start, goal, reach);
        float grown = tube * Mathf.Clamp01(u * 5f);
        float sphereR = Mathf.Max(grown, Mathf.LerpUnclamped(grown, R, swell));
        _sphere = end;
        _sphereR = sphereR;
        Vector2 toHead = headAt - end;
        _entry = toHead.sqrMagnitude > 1e-4f ? toHead.normalized : -way;

        _bubbleMat.SetVector(BubbleAId, new Vector4(headAt.x, headAt.y, headR, grown * 1.2f));
        _bubbleMat.SetVector(BubbleBId, new Vector4(end.x, end.y, sphereR, Mathf.Max(grown * 1.5f, sphereR * 0.3f)));
        _bubbleMat.SetVector(AreaId, new Vector4(area.width, area.height, grown, outlineWidth));
        _bubbleMat.SetFloat(DnaId, Mathf.Clamp01((u - 0.55f) / 0.35f));
        _bubbleMat.SetFloat(HeadId, Mathf.Clamp01(u * 4f));
        LookFromFocus();
        _bubbleMat.SetColor(FillId, _fill);
        _bubbleMat.SetColor(OutlineId, _outline);

        // Everything else sits on the sphere: a click catcher, the labels above.
        float open = Mathf.Clamp01((u - 0.6f) / 0.4f);
        Vector2 at = end; // they're anchored bottom left too
        _hit.anchoredPosition = at;
        _hit.sizeDelta = Vector2.one * (sphereR * 2f);
        for (int i = 0; i < _typed.Count; i++)
        {
            RectTransform r = _typed[i].text.rectTransform;
            r.anchoredPosition = at + new Vector2(0f, sphereR + 18f + (_typed.Count - 1 - i) * 22f);
            _typed[i].text.canvasRenderer.SetAlpha(open);
        }

        _group.alpha = visible ? 1f : 0f;
        _group.interactable = _group.blocksRaycasts = visible && _open;

        // Hover and click the strands (once it's open enough to aim at).
        Mouse m = Mouse.current;
        int was = _hover;
        _hover = _open && u > 0.95f && m != null ? Pick(m.position.ReadValue()) : -1;
        Grow(_genome.genes.Count);
        if (_hover >= 0 && _hover != was) _jiggleAt[_hover] = now; // pointed at: a jiggle
        if (_hover >= 0 && m.leftButton.wasPressedThisFrame) Inject(_hover, now);

        Refresh();
        foreach (Typed t in _typed) t.Tick(now, typeSpeed);

        if (Ready()) Draw(Mathf.Clamp01((u - 0.45f) / 0.55f));
        PlaceLabels(open);
        Travel(cam, headAt, headR, grown, now);
    }

    static float Smooth(float x) => x * x * (3f - 2f * x);
    static float EaseIn(float x) => x * x * (2f - x) * 0.5f + x * 0.5f; // starts at half speed, pushes in

    static Vector2 Rotate(Vector2 v, float degrees)
    {
        float a = degrees * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    // +1 or -1: the side (of the virus's up on screen) where the sphere fits with the least pushing
    // back on screen, so it doesn't end up over the virus.
    float PickSide(Vector2 headAt, Vector2 dir, float headR, float R, Rect area)
    {
        float best = 1f, bestPush = float.MaxValue;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector2 g = headAt + Rotate(dir, s * sideAngle) * (headR + rise + R);
            float push = Mathf.Max(0f, R + 24f - g.x) + Mathf.Max(0f, g.x - (area.width - R - 24f))
                       + Mathf.Max(0f, R + 24f - g.y) + Mathf.Max(0f, g.y - (area.height - R - 96f));
            if (push < bestPush - 1f) { bestPush = push; best = s; }
        }
        return best;
    }

    // A world point in canvas units from the bottom left (like the bubble's maths); false if behind.
    bool ToCanvas(Camera cam, Vector3 world, out Vector2 at)
    {
        at = _canvasRect.rect.size * 0.5f;
        if (!cam) return false;
        Vector3 screen = cam.WorldToScreenPoint(world);
        if (screen.z <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local))
            return false;
        at = local - _canvasRect.rect.min;
        return true;
    }

    // Focus mode's own colours (the sweep's dark fill and player outline), so the head reads as
    // itself swelling up; the fields when there's no sweep.
    void LookFromFocus()
    {
        _fill = inside;
        _outline = outline;
        if (!matchFocusSweep) return;
        if (!_sweep) _sweep = FindAnyObjectByType<ScreenInvertTest>();
        Material m = _sweep ? _sweep.screenInvertMaterial : null;
        if (!m) return;
        if (m.HasProperty(SweepFillId)) { Color c = m.GetColor(SweepFillId); _fill = new Color(c.r, c.g, c.b, 1f); }
        if (m.HasProperty(SweepOutlineId)) _outline = m.GetColor(SweepOutlineId);
    }

    static float BackOut(float x)
    {
        const float c = 1.4f;
        x -= 1f;
        return 1f + (c + 1f) * x * x * x + c * x * x;
    }

    void Refresh()
    {
        List<Genome.Gene> genes = _genome.genes;
        int selected = _genome.Selected;
        int shown = _hover >= 0 ? _hover : selected;
        if (shown >= 0 && shown < genes.Count)
        {
            Genome.Gene g = genes[shown];
            _code.Set(g.code + " // " + g.name);
            _code.text.color = g.color;
            bool going = shown == _injecting, loaded = shown == selected;
            _state.Set(going ? "[>>] INJECTING" : loaded && _hover < 0 ? "[#] INJECTED" : "[>] CLICK TO INJECT");
            _state.text.color = going || loaded ? live : text;
        }
        else
        {
            _code.Set(genes.Count + (genes.Count == 1 ? " STRAND" : " STRANDS"));
            _code.text.color = text;
            _state.Set("[ ] SELECT A STRAND");
            _state.text.color = new Color(line.r, line.g, line.b, 0.7f);
        }
    }

    // ---------------- strands ----------------

    // Each gene as a flat double helix running in from the outline like a spoke, evenly round the
    // sphere, the tube's mouth midway between two.
    Vector2 Spoke(int i, int n) => Rotate(_entry, (i + 0.5f) * 360f / Mathf.Max(n, 1));

    void Grow(int n)
    {
        while (_jiggleAt.Count < n) _jiggleAt.Add(-10f);
        while (_regrowAt.Count < n) _regrowAt.Add(-10f);
    }

    void Draw(float e)
    {
        _commands.Clear();
        _commands.SetRenderTarget(_texture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f));

        List<Genome.Gene> genes = _genome.genes;
        int n = genes.Count;
        while (_props.Count < n) _props.Add(new MaterialPropertyBlock()); // a block per draw
        Grow(n);
        while (_strands.Count < n)
        {
            var mesh = new Mesh { name = "Genome Strand", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            _strands.Add(mesh);
        }

        float now = Time.unscaledTime;
        int selected = _genome.Selected;
        for (int i = 0; i < n; i++)
        {
            float reveal = Smooth(Mathf.Clamp01(e * 1.6f - i * 0.12f)); // each grows in from the outline
            float back = i == _injecting ? 0f : Smooth(Mathf.Clamp01((now - _regrowAt[i]) / 0.6f)); // away / growing back
            BuildStrand(_strands[i], i, Spoke(i, n), reveal, Mathf.Min(reveal, back), now - _jiggleAt[i]);

            // Highlight: hovered full, loaded pulsing, the rest dimmed while one is loaded.
            float highlight = 0f, bright = 1f;
            if (i == _hover) highlight = 1f;
            else if (i == selected) highlight = 0.5f + 0.4f * Mathf.Sin(now * 5f);
            else if (selected >= 0) bright = 0.4f;
            _props[i].SetColor(ColorId, new Color(bright, bright, bright, highlight));
            _commands.DrawMesh(_strands[i], Matrix4x4.identity, _strandMat, 0, 0, _props[i]);
        }

        _commands.Blit(_texture, _display); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);
    }

    // Each strand's code, just outside the outline where it starts.
    void PlaceLabels(float open)
    {
        List<Genome.Gene> genes = _genome.genes;
        while (_labels.Count < genes.Count)
        {
            Text label = TerminalUI.Graphic<Text>("Strand", _canvasRect, Vector2.zero, new Vector2(120f, 16f));
            BottomLeft(label.rectTransform);
            TerminalUI.Style(label, _font, 11, text, TextAnchor.MiddleCenter);
            _labels.Add(label);
        }
        for (int i = 0; i < _labels.Count; i++)
        {
            Text label = _labels[i];
            bool on = i < genes.Count && open > 0f;
            if (label.gameObject.activeSelf != on) label.gameObject.SetActive(on);
            if (!on) continue;
            Vector2 dir = Spoke(i, genes.Count);
            label.text = genes[i].code;
            Color c = i == _hover || i == _genome.Selected ? Saturated(genes[i].color) : new Color(line.r, line.g, line.b, 0.75f);
            label.color = new Color(c.r, c.g, c.b, c.a * open);
            RectTransform r = label.rectTransform;
            r.sizeDelta = new Vector2(label.preferredWidth + 4f, 16f);
            r.pivot = new Vector2(0.5f - 0.5f * dir.x, 0.5f - 0.5f * dir.y); // leaning away from the sphere
            r.anchoredPosition = _sphere + dir * (_sphereR + 8f);
        }
    }

    // The strand under a screen point: the spoke nearest its angle, within the strand's reach.
    int Pick(Vector2 pointer)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, pointer, null, out Vector2 local)) return -1;
        Vector2 q = (local - _canvasRect.rect.min - _sphere) / Mathf.Max(_sphereR, 1e-3f);
        float r = q.magnitude;
        int n = _genome.genes.Count;
        if (n == 0 || r > 1f || r < Rim - strandLength - 0.08f) return -1;
        int best = -1;
        float bestDot = Mathf.Cos(Mathf.PI / n); // within half the gap to the next
        for (int i = 0; i < n; i++)
        {
            float d = Vector2.Dot(q / r, Spoke(i, n));
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best;
    }

    // ---------------- injection (test) ----------------

    void Inject(int gene, float now)
    {
        _jiggleAt[gene] = now;
        if (_injecting >= 0) return; // one at a time
        _genome.Select(gene);
        _injecting = gene;
        _injectAt = now;
        _delivered = false;
    }

    // The injected strand, drawn on the canvas (it leaves the sphere): out along its spoke, across the
    // sphere to the tube's mouth, down the tube into the ball, through the body and down the drill to
    // its tip, sliding along that path like a rope pulled through, shrinking overall and funnelling
    // (thinner at the front) as it goes into the tube.
    void Travel(Camera cam, Vector2 headAt, float headR, float tube, float now)
    {
        List<Genome.Gene> genes = _genome.genes;
        if (_injecting >= genes.Count) _injecting = -1;
        if (_injecting < 0)
        {
            ReleaseTravel();
            return;
        }
        int gene = _injecting;
        float R = Mathf.Max(_sphereR, 1f), L = strandLength, amp = Amp();
        Vector2 dir = Spoke(gene, genes.Count);

        // The path: its spoke straight, then the rest rounded off.
        Vector2 tip = _sphere + dir * ((Rim - L) * R), mouth = _sphere + _entry * R;
        _path.Clear();
        _path.Add(_sphere + dir * (Rim * R));
        _path.Add(tip);
        _path.Add(tip - dir * (0.12f * R));
        _path.Add(_sphere + _entry * (0.55f * R));
        _path.Add(mouth);
        Vector2 toSphere = _sphere - headAt;
        if (toSphere.sqrMagnitude > 1e-4f) _path.Add(headAt + toSphere.normalized * headR);
        _path.Add(headAt);
        if (!_drill) _drill = _genome.GetComponentInChildren<InjectionDrill>();
        if (ToCanvas(cam, _genome.transform.position, out Vector2 body)) _path.Add(body);
        if (_drill && _drill.Span(out Vector3 drillStart, out Vector3 drillTip)
            && ToCanvas(cam, drillStart, out Vector2 startAt) && ToCanvas(cam, drillTip, out Vector2 tipAt))
        {
            _path.Add(startAt);
            _path.Add(tipAt);
        }
        for (int i = 0; i < 3; i++) Chaikin(_path, _pathTemp, 1);
        _arc.Clear();
        _arc.Add(0f);
        for (int i = 1; i < _path.Count; i++) _arc.Add(_arc[i - 1] + Vector2.Distance(_path[i - 1], _path[i]));
        float total = _arc[_arc.Count - 1];
        float atTip = L * R, atMouth = Mathf.Max(atTip + 1f, NearestArc(mouth));
        float atHead = Mathf.Clamp(NearestArc(headAt), atMouth, total);

        // Timing, in two legs with their own share of the time (by distance alone, the leg over the
        // virus -- often a few dozen pixels against hundreds in the sphere -- flashed by and the strand
        // seemed to vanish): the front slides from the spoke's inner end into the head, then on
        // through the body and down the drill to its tip until the back has gone in too. The strand
        // shrinks to 45% on the first leg, then to fit the second (at most 40% of it long), so it's
        // seen travelling down the virus rather than swallowed whole.
        float p = Mathf.Clamp01((now - _injectAt) / injectDuration);
        float leg = Mathf.Clamp01(p / headShare), down = Mathf.Clamp01((p - headShare) / (1f - headShare));
        float fit = Mathf.Min(0.45f * R, (total - atHead) * 0.4f / L);
        float length = Mathf.Lerp(R * Mathf.Lerp(1f, 0.45f, Smooth(leg)), fit, Smooth(Mathf.Clamp01(down * 3f)));
        float front = p < headShare ? Mathf.Lerp(atTip, atHead, Smooth(leg))
                                    : Mathf.Lerp(atHead, total + L * fit, EaseIn(down));

        // Thick in the sphere, squeezed toward the tube's width from its mouth on, a little thinner by
        // the end -- but never under minInjectWidth on screen (the tube can be a few pixels: sized to
        // it exactly, the strand vanished as it left the sphere).
        float half = Mathf.Max((amp + BackboneWidth) * R, 1e-3f); // the helix's half height, full size
        float floor = Mathf.Min(1f, minInjectWidth * 0.5f / half);
        float thin = Mathf.Clamp(tube / half, floor, 1f);
        float Funnel(float a) => Mathf.Max(floor, a <= atTip ? 1f
            : a < atMouth ? Mathf.Lerp(1f, thin, Smooth((a - atTip) / (atMouth - atTip)))
            : Mathf.Lerp(thin, thin * 0.7f, Mathf.Clamp01((a - atMouth) / Mathf.Max(total - atMouth, 1f))));

        float from = Mathf.Max(0f, L - front / length), to = Mathf.Min(L, L - (front - total) / length);
        s_v.Clear(); s_c.Clear(); s_tri.Clear();
        Helix((float s, out Vector2 at, out Vector2 x, out float scale) =>
        {
            float a = front - (L - s) * length;
            at = PathAt(a);
            Vector2 t = PathAt(a + 3f) - PathAt(a - 3f);
            x = t.sqrMagnitude > 1e-8f ? new Vector2(t.y, -t.x).normalized : Vector2.up;
            scale = R * Funnel(a);
        }, from, to, amp, gene, Saturated(genes[gene].color));
        DrawTraveller();

        if (!_delivered && front >= total)
        {
            _delivered = true;
            if (_drill)
            {
                _drill.Deliver();
                ImmuneSystem.Deliver(_drill.Cell, genes[gene]); // the right gene takes the cell over
            }
            _genome.Delivered(gene);
        }
        if (p >= 1f)
        {
            _regrowAt[gene] = now;
            _injecting = -1;
        }
    }

    // The travelling strand (in s_v / s_c / s_tri, canvas units from the bottom left) into its own
    // canvas-sized texture, the way the sphere's strands are drawn.
    void DrawTraveller()
    {
        Vector2 area = _canvasRect.rect.size;
        int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
        if (!_travelTexture || _travelTexture.width != w || _travelTexture.height != h)
        {
            ReleaseTravel();
            _travelTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Genome Injection", hideFlags = HideFlags.DontSave, antiAliasing = 4,
            };
            _travelTexture.Create();
            _travelDisplay = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Genome Injection Display", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _travelDisplay.Create();
        }
        if (!_travelMesh)
        {
            _travelMesh = new Mesh { name = "Genome Injection", hideFlags = HideFlags.DontSave };
            _travelMesh.MarkDynamic();
        }
        Apply(_travelMesh);
        _travelMesh.RecalculateBounds();
        _travelProps ??= new MaterialPropertyBlock();
        _travelProps.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));

        _commands.Clear();
        _commands.SetRenderTarget(_travelTexture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, area.x, 0f, area.y, -1f, 1f));
        _commands.DrawMesh(_travelMesh, Matrix4x4.identity, _strandMat, 0, 0, _travelProps);
        _commands.Blit(_travelTexture, _travelDisplay); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);

        _traveller.texture = _travelDisplay;
        if (!_traveller.enabled) _traveller.enabled = true;
    }

    // Screen-sized: only kept while a strand is travelling.
    void ReleaseTravel()
    {
        if (_traveller && _traveller.enabled) _traveller.enabled = false;
        if (_traveller) _traveller.texture = null;
        if (_travelTexture) { _travelTexture.Release(); Destroy(_travelTexture); }
        if (_travelDisplay) { _travelDisplay.Release(); Destroy(_travelDisplay); }
        _travelTexture = _travelDisplay = null;
    }

    // Round the corners off a polyline (Chaikin), keeping its ends and its first 'keep' points.
    static void Chaikin(List<Vector2> points, List<Vector2> temp, int keep)
    {
        temp.Clear();
        for (int i = 0; i <= keep && i < points.Count; i++) temp.Add(points[i]);
        for (int i = keep; i < points.Count - 1; i++)
        {
            temp.Add(Vector2.Lerp(points[i], points[i + 1], 0.25f));
            temp.Add(Vector2.Lerp(points[i], points[i + 1], 0.75f));
        }
        if (points.Count > keep + 1) temp.Add(points[points.Count - 1]);
        points.Clear();
        points.AddRange(temp);
    }

    // The point 'a' along _path.
    Vector2 PathAt(float a)
    {
        int lo = 0, hi = _arc.Count - 1;
        if (hi <= 0) return _path.Count > 0 ? _path[0] : Vector2.zero;
        a = Mathf.Clamp(a, 0f, _arc[hi]);
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_arc[mid] <= a) lo = mid; else hi = mid;
        }
        float span = _arc[hi] - _arc[lo];
        return Vector2.Lerp(_path[lo], _path[hi], span > 1e-5f ? (a - _arc[lo]) / span : 0f);
    }

    // How far along _path its point nearest 'at' is.
    float NearestArc(Vector2 at)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < _path.Count; i++)
        {
            float d = (_path[i] - at).sqrMagnitude;
            if (d < bestD) { bestD = d; best = i; }
        }
        return _arc[best];
    }

    // ---------------- building ----------------

    bool Build()
    {
        if (_canvas) return true;
        Shader bubble = bubbleShader ? bubbleShader : Shader.Find("Hidden/GenomeBubble");
        Shader strand = strandShader ? strandShader : Shader.Find("Hidden/GenomeStrand");
        if (!bubble || !strand)
        {
            Debug.LogError("GenomeView: assign Hidden/GenomeBubble and Hidden/GenomeStrand.", this);
            return false;
        }
        _bubbleMat = new Material(bubble) { hideFlags = HideFlags.DontSave };
        _strandMat = new Material(strand) { hideFlags = HideFlags.DontSave };
        _made.Add(_bubbleMat);
        _made.Add(_strandMat);

        _font = font ? font : TerminalUI.Font(terminalFonts);

        _canvas = TerminalUI.Canvas("Genome View Canvas", transform, 590); // under the rope menu
        _canvasRect = (RectTransform)_canvas.transform;
        _group = _canvas.gameObject.AddComponent<CanvasGroup>();

        // The bubble: over the whole canvas, drawing only its shape.
        _bubble = TerminalUI.Graphic<RawImage>("Bubble", _canvasRect, Vector2.zero, Vector2.zero);
        RectTransform b = _bubble.rectTransform;
        b.anchorMin = Vector2.zero;
        b.anchorMax = Vector2.one;
        b.sizeDelta = Vector2.zero;
        _bubble.material = _bubbleMat;
        _bubble.raycastTarget = false;

        // The injected strand on its way out, over the bubble.
        _traveller = TerminalUI.Graphic<RawImage>("Injection", _canvasRect, Vector2.zero, Vector2.zero);
        RectTransform tr = _traveller.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.sizeDelta = Vector2.zero;
        _traveller.raycastTarget = false;
        _traveller.enabled = false;

        // Invisible, on the sphere: clicks in it count as UI, so they don't close it.
        Image hit = TerminalUI.Graphic<Image>("Hit", _canvasRect, Vector2.zero, Vector2.zero);
        hit.color = Color.clear;
        hit.canvasRenderer.cullTransparentMesh = false; // culled (fully clear), raycasts skip it
        _hit = BottomLeft(hit.rectTransform);

        _title = Label(12, new Color(line.r, line.g, line.b, 0.65f), 0f);
        _title.full = "04 // GENOME";
        _code = Label(20, text, 0.1f);
        _state = Label(13, text, 0.2f);

        _canvas.gameObject.SetActive(false);
        return Ready();
    }

    // Positioned in canvas units from the bottom left, like the bubble's own maths.
    static RectTransform BottomLeft(RectTransform r)
    {
        r.anchorMin = r.anchorMax = Vector2.zero;
        r.pivot = new Vector2(0.5f, 0.5f);
        return r;
    }

    Typed Label(int fontSize, Color color, float delay)
    {
        Text t = TerminalUI.Graphic<Text>("Label", _canvasRect, Vector2.zero, new Vector2(size + 200f, fontSize + 8f));
        BottomLeft(t.rectTransform);
        TerminalUI.Style(t, _font, fontSize, color, TextAnchor.MiddleCenter);
        var typed = new Typed { text = t, delay = delay, start = Time.unscaledTime };
        _typed.Add(typed);
        return typed;
    }

    // Everything drawing needs, made or remade (a script reload drops the plain C# parts).
    bool Ready()
    {
        _commands ??= new CommandBuffer { name = "Genome View" };
        _props ??= new List<MaterialPropertyBlock>();
        if (!_texture || !_display || _texture.width != resolution)
        {
            if (_texture) { _texture.Release(); Destroy(_texture); }
            if (_display) { _display.Release(); Destroy(_display); }
            _texture = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGB32)
            {
                name = "Genome View", hideFlags = HideFlags.DontSave, antiAliasing = 4,
            };
            _texture.Create();
            _display = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                name = "Genome View Display", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _display.Create();
        }
        if (_bubble && _bubble.texture != _display) _bubble.texture = _display;
        return _bubbleMat && _strandMat;
    }

    void OnDestroy()
    {
        foreach (Object o in _made)
            if (o) Destroy(o);
        if (_texture) { _texture.Release(); Destroy(_texture); }
        if (_display) { _display.Release(); Destroy(_display); }
        foreach (Mesh mesh in _strands)
            if (mesh) Destroy(mesh);
        ReleaseTravel();
        if (_travelMesh) Destroy(_travelMesh);
        _commands?.Release();
        _commands = null;
    }

    // ---------------- meshes ----------------

    static readonly List<Vector3> s_v = new List<Vector3>();
    static readonly List<Color> s_c = new List<Color>();
    static readonly List<int> s_tri = new List<int>();
    static readonly Vector2[] s_a = new Vector2[Samples];
    static readonly float[] s_w = new float[Samples];

    // Base pairs: A-T and G-C, each half its own colour.
    static readonly Color BaseA = new Color(1f, 0.16f, 0.3f), BaseT = new Color(0.12f, 0.55f, 1f),
                          BaseG = new Color(0.15f, 1f, 0.35f), BaseC = new Color(1f, 0.8f, 0.05f);

    static Color Saturated(Color c)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        Color o = Color.HSVToRGB(h, Mathf.Max(s, 0.85f), 1f);
        o.a = c.a;
        return o;
    }

    // One strand in the sphere (sphere radii): a dot on the outline ('dot' of it shown) and the helix
    // in toward the middle ('reveal' of it). 'poked' seconds since it was pointed at: it jiggles, a
    // wave running out along it from the outline, dying away.
    void BuildStrand(Mesh mesh, int gene, Vector2 dir, float dot, float reveal, float poked)
    {
        s_v.Clear(); s_c.Clear(); s_tri.Clear();
        Vector2 root = dir * Rim, along = -dir, across = new Vector2(-dir.y, dir.x);
        float shake = poked < 1.2f ? jiggle * Mathf.Exp(-poked * 5f) : 0f;
        Color color = Saturated(_genome.genes[gene].color);
        if (dot > 0f) Disc(root, 0.035f * dot, color);
        Helix((float s, out Vector2 at, out Vector2 x, out float scale) =>
        {
            float wobble = shake > 0f ? shake * (s / strandLength) * Mathf.Sin(poked * 40f - s * 18f) : 0f;
            at = root + along * s + across * wobble;
            x = across;
            scale = 1f;
        }, 0f, strandLength * reveal, Amp(), gene, color);
        Apply(mesh);
    }

    // Helix half-height (sphere radii): as much as fits between the strands at their inner ends.
    float Amp() => Mathf.Min(0.06f, Mathf.PI * (Rim - strandLength) / Mathf.Max(_genome.genes.Count, 1) * 0.8f);

    // Where a strand's spine is 's' along it (sphere radii from its outline end): the point, the unit
    // across it, and the output units per sphere radius there (sizes).
    delegate void Spine(float s, out Vector2 at, out Vector2 across, out float scale);

    // A flat double helix along a spine, from 'from' to 'to' along it: two plain backbones crossing,
    // base-pair rungs between (five a turn, colour-coded, skipped where the backbones cross; each keeps
    // its pair wherever the strand goes). Added to the shared lists.
    static void Helix(Spine spine, float from, float to, float amp, int gene, Color color)
    {
        if (to - from <= 1e-4f) return;
        float k = Mathf.PI * 2f / Pitch, step = Pitch / 5f, phase = gene * 1.3f;
        for (int j = Mathf.Max(0, Mathf.CeilToInt(from / step - 0.5f)); (j + 0.5f) * step <= to; j++)
        {
            float s = (j + 0.5f) * step;
            float wave = Mathf.Sin(s * k + phase);
            if (Mathf.Abs(wave) < 0.3f) continue;
            uint h = (uint)j * 73856093u ^ (uint)gene * 19349663u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            Color a, b;
            switch (h & 3u)
            {
                case 0: a = BaseA; b = BaseT; break;
                case 1: a = BaseT; b = BaseA; break;
                case 2: a = BaseG; b = BaseC; break;
                default: a = BaseC; b = BaseG; break;
            }
            spine(s, out Vector2 at, out Vector2 across, out float scale);
            Vector2 off = across * (amp * wave * scale);
            Quad(at + off, at, RungWidth * scale, a, a);
            Quad(at, at - off, RungWidth * scale, b, b);
        }

        // Backbones over the rungs.
        for (int side = -1; side <= 1; side += 2)
        {
            for (int i = 0; i < Samples; i++)
            {
                float s = Mathf.Lerp(from, to, i / (float)(Samples - 1));
                spine(s, out Vector2 at, out Vector2 across, out float scale);
                s_a[i] = at + across * (side * amp * Mathf.Sin(s * k + phase) * scale);
                s_w[i] = BackboneWidth * scale;
            }
            Ribbon(s_a, s_w, color);
        }
    }

    // A backbone as a flat ribbon, 'widths' (half) across at each point.
    static void Ribbon(Vector2[] points, float[] widths, Color color)
    {
        int start = s_v.Count;
        for (int i = 0; i < Samples; i++)
        {
            Vector2 tangent = points[Mathf.Min(i + 1, Samples - 1)] - points[Mathf.Max(i - 1, 0)];
            Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized * widths[i];
            s_v.Add(points[i] + normal);
            s_v.Add(points[i] - normal);
            s_c.Add(color);
            s_c.Add(color);
        }
        for (int i = 0; i < Samples - 1; i++)
        {
            int a = start + i * 2;
            s_tri.Add(a); s_tri.Add(a + 1); s_tri.Add(a + 2);
            s_tri.Add(a + 1); s_tri.Add(a + 3); s_tri.Add(a + 2);
        }
    }

    // A filled circle.
    static void Disc(Vector2 centre, float radius, Color color)
    {
        const int Sides = 20;
        int start = s_v.Count;
        s_v.Add(centre);
        s_c.Add(color);
        for (int i = 0; i < Sides; i++)
        {
            float a = i * Mathf.PI * 2f / Sides;
            s_v.Add(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius);
            s_c.Add(color);
            s_tri.Add(start); s_tri.Add(start + 1 + i); s_tri.Add(start + 1 + (i + 1) % Sides);
        }
    }

    // A straight bar from a to b, 'width' across, coloured from one end to the other.
    static void Quad(Vector2 a, Vector2 b, float width, Color ca, Color cb)
    {
        Vector2 d = b - a;
        Vector2 n = d.sqrMagnitude > 1e-10f ? new Vector2(-d.y, d.x).normalized * (width * 0.5f) : new Vector2(width * 0.5f, 0f);
        int start = s_v.Count;
        s_v.Add(a + n); s_v.Add(a - n); s_v.Add(b + n); s_v.Add(b - n);
        s_c.Add(ca); s_c.Add(ca); s_c.Add(cb); s_c.Add(cb);
        s_tri.Add(start); s_tri.Add(start + 1); s_tri.Add(start + 2);
        s_tri.Add(start + 1); s_tri.Add(start + 3); s_tri.Add(start + 2);
    }

    static void Apply(Mesh mesh)
    {
        mesh.Clear();
        mesh.SetVertices(s_v);
        mesh.SetColors(s_c);
        mesh.SetTriangles(s_tri, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 1f));
    }
}

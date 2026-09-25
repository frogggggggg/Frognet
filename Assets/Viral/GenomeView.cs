using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Typed = TerminalUI.Typed;
using Kind = VirusInventory.Kind;
using Mount = VirusInventory.Mount;
using static FlatMesh;

/// <summary>
/// The virus's head, opened up (E anywhere, or a click on the head in focus mode): a tube grows out of
/// its head on screen and swells into a big sphere off to one side (flaring out of the head, pinched
/// into the sphere like a drop), in focus mode's own dark fill and outline. Round the inside of the
/// outline sit the head's mounts (VirusInventory.Ring), evenly spaced, each on a white base growing in
/// from the outline: DNA slots hold a gene, drawn as a flat double helix running in like a spoke; store
/// slots are bars filling from the base inward in their substance's colour. The tube's mouth falls
/// midway between two. Drag a mount round the ring to rearrange (the rest close up, it snaps into
/// place); a free mount shows as a "+" (LMB: a store slot, RMB: a DNA slot); RMB on an empty mount frees it.
///
/// Click a strand to inject it (focus mode; out of it, to load it): it's drawn out through the tube,
/// down into the head, through the body to the top of the InjectionDrill, where it waits while the virus
/// draws up and pumps down (Organism's Pump, Intent.Inject), and the push sends it zooming down the drill
/// into the cell (a ripple there, ImmuneSystem.Deliver, Genome.Delivered); then it grows back. While
/// a resource chunk is being extracted into this virus, its material is seen flowing from the chunk on
/// screen into the head, up the tube and into its store's bar.
///
/// In the middle sits the synthesizer (Crafting): click its hub for the recipes round it, pick one and
/// its nozzle turns to each store it needs, sucks the material up, then turns to an empty DNA slot and
/// pushes the new strand out into it.
///
/// The sphere's place is picked once per opening (off the virus's up on screen, whichever side fits),
/// then kept as an offset from the head, so it follows the virus without swinging round as it turns.
///
/// The bubble is one full-screen UI image (Hidden/GenomeBubble: head, tube and sphere smooth-unioned
/// as a distance field, in canvas units). The mounts are one flat mesh (orthographic, the sphere's
/// radius = 1) drawn into a render texture by a command buffer; the travelling strand and the flows
/// are another, canvas-sized, only while something is moving. Builds everything on first use.
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
    [Range(0f, 90f), Tooltip("How far off the virus's up the sphere settles when it opens, to whichever side fits the screen.")]
    public float sideAngle = 50f;
    [Min(0.5f), Tooltip("How quickly the sphere follows the head across the screen once open.")]
    public float follow = 9f;
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

    [Header("Mounts")]
    [Range(0.2f, 0.9f), Tooltip("How far in from the outline strands and bars reach (sphere radii).")]
    public float strandLength = 0.6f;
    [Range(0f, 0.2f), Tooltip("How far a strand jiggles when the pointer comes onto it (sphere radii).")]
    public float jiggle = 0.06f;
    [Min(0f), Tooltip("Pixels the pointer moves with the button down before a press becomes a drag.")]
    public float dragThreshold = 8f;
    [Min(0.01f), Tooltip("Seconds (time constant) for mounts to slide to their new places.")]
    public float reflowTime = 0.08f;

    [Header("Injection")]
    [Min(0.05f), Tooltip("Seconds from the sphere into the head (eased in and out).")]
    public float toHeadTime = 1.1f;
    [Min(0.05f), Tooltip("Seconds from the head through the body to the top of the drill.")]
    public float toDrillTime = 0.6f;
    [Min(0f), Tooltip("Seconds it waits at the top of the drill before the virus pumps.")]
    public float drillPause = 0.5f;
    [Min(0.05f), Tooltip("Seconds to shoot down the drill once pushed (fast off the push, slowing into the tip).")]
    public float zoomTime = 0.6f;
    [Min(2f), Tooltip("The travelling strand's smallest width on screen (1080p pixels), however thin the tube.")]
    public float minInjectWidth = 14f;

    [Header("Synthesizer")]
    [Range(0.04f, 0.2f), Tooltip("Radius of the rotating hub in the middle (sphere radii). Its nozzle reaches out to the mounts' inner ends.")]
    public float hubSize = 0.09f;
    [Min(0.01f), Tooltip("Seconds (smoothing) for the nozzle to turn to a mount.")]
    public float turnTime = 0.14f;
    [Min(0.05f), Tooltip("Seconds to suck one store's share of a recipe out of its bar.")]
    public float suckTime = 0.9f;
    [Min(0.05f), Tooltip("Seconds to dispense the product into its slot.")]
    public float dispenseTime = 0.9f;
    [Range(0.2f, 1f), Tooltip("While the recipe menu is open, strands and bars pull back toward the outline to this share of " +
                              "their length, so the recipes get most of the sphere.")]
    public float menuStrands = 0.4f;
    [Range(0.03f, 0.15f), Tooltip("Largest recipe disc (sphere radii). They shrink to fit many, and page (mouse wheel) past that.")]
    public float optionSize = 0.1f;

    [Header("Extraction flow")]
    [Min(10f), Tooltip("Speed of the material flowing in, in 1080p pixels a second.")]
    public float flowSpeed = 320f;
    [Min(10f), Tooltip("Average speed across the screen from the chunk to the head (1080p pixels a second): quicker than the flow, "
        + "leaving the chunk slowly, rushing across, easing into the flow's speed at the head.")]
    public float crossSpeed = 1100f;
    [Tooltip("Shortest and longest time (seconds) for that crossing, however far the chunk is on screen.")]
    public Vector2 crossTime = new Vector2(0.18f, 0.5f);
    [Min(4f), Tooltip("Gap between the flowing drops, in 1080p pixels.")]
    public float flowSpacing = 20f;
    [Min(1f), Tooltip("Drop radius, in 1080p pixels.")]
    public float flowSize = 5.5f;

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
    /// <summary>How far open, 0..1 (the tube and sphere growing out of the head).</summary>
    public float Shown => _shown;
    /// <summary>A mount is being dragged round the ring (the pointer is the view's until it's let go).</summary>
    public bool Dragging => _dragging;

    /// <summary>Whether a strand click injects it (down the drill: focus mode) or only loads it (the
    /// inventory, opened anywhere with E). Set by VirusMovement.</summary>
    public bool CanInject { get; set; } = true;

    /// <summary>The stores on the ring, and where extraction flows to. Empty: found from the genome.</summary>
    public VirusInventory Inventory { get; set; }

    /// <summary>Whether a screen point is on the open sphere (a click there is the view's, not the world's).</summary>
    public bool Covers(Vector2 screen)
    {
        if (!_open || !_canvasRect || _shown <= 0f) return false;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local)) return false;
        return (local - _canvasRect.rect.min - _sphere).sqrMagnitude <= _sphereR * _sphereR * 1.1f;
    }

    const int Samples = 64;     // along a strand's backbones
    const float Pitch = 0.2f;   // one helix turn, in sphere radii
    const float Rim = 0.93f;    // where the strands start, in from the outline
    const float BackboneWidth = 0.011f, RungWidth = 0.014f; // sphere radii (backbone: half width)
    const float Frame = 0.009f; // bar outlines (sphere radii)

    static readonly int ColorId = Shader.PropertyToID("_Color"), FillId = Shader.PropertyToID("_Fill"),
                        OutlineId = Shader.PropertyToID("_Outline"), BubbleAId = Shader.PropertyToID("_BubbleA"),
                        BubbleBId = Shader.PropertyToID("_BubbleB"), AreaId = Shader.PropertyToID("_BubbleArea"),
                        DnaId = Shader.PropertyToID("_BubbleDNA"), SweepFillId = Shader.PropertyToID("_FillColor"),
                        SweepOutlineId = Shader.PropertyToID("_HighlightColor"), HeadId = Shader.PropertyToID("_BubbleHead");

    Genome _genome;
    Camera _camera;
    bool _open;
    float _shown, _openedAt;
    Vector2 _sphere;   // sphere centre (canvas units, from the bottom left) this frame, for picking
    float _sphereR;
    Color _fill, _outline;
    ScreenInvertTest _sweep;

    // Placement: chosen once per opening as an offset from the head, then followed (eased).
    bool _placed;
    Vector2 _offset, _goal;

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
    Mesh _ringMesh;
    Vector2 _entry = Vector2.down; // from the sphere's centre toward the tube's mouth

    // The ring this frame (the inventory's, plus the free mount when there's room), and each mount's look.
    class Look
    {
        public float angle = float.NaN; // degrees from _entry, eased toward its place
        public float jiggleAt = -10f, regrowAt = -10f, flashAt = -10f, fill, last;
        public Text label;
        // Stores: what the bar shows lags the slot by 'delay' (the flow's travel time while one is
        // feeding it, 'fedAt'), so it only rises as the material arrives. (time, amount) samples.
        public float delay, fedAt = -10f, shown;
        public readonly List<Vector2> history = new List<Vector2>();
        public float dispenseAt = -10f; // a crafted strand being pushed out of the nozzle into it
    }
    readonly Dictionary<Mount, Look> _looks = new Dictionary<Mount, Look>();
    readonly List<Mount> _order = new List<Mount>(), _drop = new List<Mount>();
    readonly Mount _free = new Mount { kind = Kind.Store, index = -2 }; // the "+": not on the inventory's ring
    int _hover = -1;
    Mount _pressed;
    Vector2 _pressAt;
    bool _dragging;
    float _dragAngle;

    // The injection: one strand at a time travelling out along _path (canvas units).
    int _injecting = -1;
    float _injectAt, _impactDelay, _lastFront = float.NaN;
    bool _delivered, _pushed;
    InjectStage _stage;
    // Rings on the canvas: the push landing at the top of the drill, the delivery bursting at its tip.
    Vector2 _pulseAt, _burstAt;
    float _pulseTime = -10f, _burstTime = -10f;
    Color _burstColor;
    Organism _organism;
    // Drawn like the ring (command buffer into a render texture, canvas-sized, shown by a full-screen
    // image): as a UIShape on the canvas it never showed.
    RawImage _traveller;
    RenderTexture _travelTexture, _travelDisplay;
    Mesh _travelMesh;
    InjectionDrill _drill;
    readonly List<Vector2> _path = new List<Vector2>(), _pathTemp = new List<Vector2>();
    readonly List<float> _arc = new List<float>();
    // The synthesizer in the middle: its menu of recipes (what's pointed at, and whether each can be
    // made), the nozzle (degrees from _entry, like the mounts; how far out), and the job being made:
    // its plan (store slot, amount) drawn one step at a time, then the product dispensed into _outMount.
    Crafting _crafting;
    bool _menu, _onHub;
    float _menuShown;
    int _option = -1;
    readonly List<bool> _optionCan = new List<bool>();
    readonly List<string> _optionWhy = new List<string>();
    readonly List<(int slot, float amount)> _preview = new List<(int, float)>(), _plan = new List<(int, float)>();
    // The recipe pointed at: whether it can be made (the bars' preview goes green / red), and what it's short
    // of, shown past the fill of a store of that substance.
    readonly List<(int slot, float amount)> _short = new List<(int, float)>();
    bool _previewCan;
    float _nozzle = float.NaN, _nozzleVel, _nozzleOut = 1f, _idle, _spin, _work, _pop;
    Color _glow;
    // The menu's layout (unit disc: centre, radius per place), for this many recipes a page; the page shown.
    readonly List<Vector3> _places = new List<Vector3>();
    int _placesFor = -1, _page;
    float _placesAt = float.NaN;
    enum Phase { Turn, Suck, Dispense }
    Crafting.Recipe _job;
    Phase _phase;
    int _step;
    float _stepAt, _taken, _hubFill, _jobTotal, _dispensed;
    bool _deliveredJob;
    Color _hubColor, _suckColor;
    Mount _outMount;

    // Not kept by a play-mode script reload: remade on use.
    CommandBuffer _commands;
    MaterialPropertyBlock _props;

    /// <summary>Where an injection has got to, in order (InjectionAudio listens).</summary>
    public enum InjectStage { None, Started, IntoTube, AtDrill, Pushed, Shot, Delivered }
    /// <summary>An injection reached a stage; the bool is true on a Delivered that took the cell over.</summary>
    public static event System.Action<InjectStage, bool> Injection;
    /// <summary>How fast the injected strand is sliding, in sphere radii per second (0 when none is).</summary>
    public static float InjectSpeed { get; private set; }

    /// <summary>What the synthesizer just did (CraftAudio listens).</summary>
    public enum CraftStage { MenuOpened, MenuClosed, Pointed, Refused, PageTurned, Started, Docked, Dispensing, Made }
    /// <summary>The synthesizer reached a stage; the recipe is the job's (or the one pointed at / refused), else null.</summary>
    public static event System.Action<CraftStage, Crafting.Recipe> Synthesis;
    /// <summary>True while the nozzle is sucking a store's share up.</summary>
    public static bool Drawing { get; private set; }

    bool _menuWas;
    int _optionWas = -1;

    static void Say(CraftStage stage, Crafting.Recipe r = null) => Synthesis?.Invoke(stage, r);

    void Reach(InjectStage stage, bool took = false)
    {
        if (stage <= _stage) return;
        _stage = stage;
        Injection?.Invoke(stage, took);
    }

    public static GenomeView Create() => new GameObject("Genome View").AddComponent<GenomeView>();

    public void Open(Genome genome, Camera cam)
    {
        if (!Build()) return;
        bool fresh = !_open || genome != _genome;
        if (genome != _genome) { FinishCraft(); _crafting = null; }
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
        _menu = _menuWas = false;
        _optionWas = -1;
        EndDrag();
    }

    VirusInventory Inv => Inventory ? Inventory : _genome ? Inventory = VirusInventory.Of(_genome) : null;
    Crafting Recipes => _crafting ? _crafting : _genome ? _crafting = Crafting.Of(_genome) : null;

    void LateUpdate()
    {
        if (_canvas && _title == null) // a play-mode script reload dropped the plain parts: build again on open
        {
            Destroy(_canvas.gameObject);
            _canvas = null;
            _typed.Clear();
            _looks.Clear();
            _open = false;
            _injecting = -1;
            InjectSpeed = 0f;
        }
        if (!_canvas) return;
        if (_open && !_genome) Close();

        float dt = Time.unscaledDeltaTime;
        _shown = Mathf.MoveTowards(_shown, _open ? 1f : 0f, dt / animationTime);
        if ((_shown <= 0f && !_open && _injecting < 0) || !_genome)
        {
            FinishCraft(); // shut mid-way: made at once
            _injecting = -1;
            InjectSpeed = 0f;
            ReleaseTravel();
            _placed = false; // choose again next time
            if (_canvas.gameObject.activeSelf) _canvas.gameObject.SetActive(false);
            return;
        }
        float now = Time.unscaledTime;

        // The head on screen (a sphere) and the way the virus faces on screen.
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

        // Where the sphere goes: picked once per opening off to one side of the virus's up (whichever
        // fits the screen), then kept as an offset from the head, so turning the virus doesn't swing
        // it round; eased after the head, kept on screen with room for the labels.
        float R = size * 0.5f;
        if (!_placed)
        {
            float pick = PickSide(headAt, dir, headR, R, area);
            Vector2 first = Fit(headAt + Rotate(dir, pick * sideAngle) * (headR + rise + R), R, area);
            _offset = first - headAt;
            _goal = first;
            _placed = true;
        }
        _goal = Vector2.Lerp(_goal, Fit(headAt + _offset, R, area), 1f - Mathf.Exp(-follow * dt));
        Vector2 goal = _goal;

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
        _entry = toHead.sqrMagnitude > 1e-4f ? toHead.normalized : -_offset.normalized;

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
        _hit.anchoredPosition = end; // anchored bottom left too
        _hit.sizeDelta = Vector2.one * (sphereR * 2f);
        for (int i = 0; i < _typed.Count; i++)
        {
            RectTransform r = _typed[i].text.rectTransform;
            r.anchoredPosition = end + new Vector2(0f, sphereR + 18f + (_typed.Count - 1 - i) * 22f);
            _typed[i].text.canvasRenderer.SetAlpha(open);
        }

        _group.alpha = visible ? 1f : 0f;
        _group.interactable = _group.blocksRaycasts = visible && _open;

        Gather();
        Interact(u, now);
        Craft(dt, now);
        Settle(dt);
        Refresh();
        foreach (Typed t in _typed) t.Tick(now, typeSpeed);

        if (Ready()) Draw(Mathf.Clamp01((u - 0.45f) / 0.55f), now);
        PlaceLabels(open);

        // What's moving outside the sphere: the injected strand, material flowing in.
        FlatMesh.Clear();
        Travel(cam, headAt, headR, grown, now);
        Flow(cam, headAt, headR, grown, now, open);
        Rings(now);
        if (Verts.Count > 0 && _commands != null) DrawOverlay();
        else ReleaseTravel();
    }

    static float Smooth(float x) => x * x * (3f - 2f * x);

    static float ExpoInOut(float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        return x < 0.5f ? Mathf.Pow(2f, 20f * x - 10f) * 0.5f : 1f - Mathf.Pow(2f, -20f * x + 10f) * 0.5f;
    }

    static float CubicOut(float x) { x = 1f - Mathf.Clamp01(x); return 1f - x * x * x; }

    static Vector2 Rotate(Vector2 v, float degrees)
    {
        float a = degrees * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    // A sphere centre kept on the canvas, with room above for the labels.
    static Vector2 Fit(Vector2 g, float R, Rect area)
    {
        g.x = Mathf.Clamp(g.x, R + 24f, Mathf.Max(R + 24f, area.width - R - 24f));
        g.y = Mathf.Clamp(g.y, R + 24f, Mathf.Max(R + 24f, area.height - R - 96f));
        return g;
    }

    // +1 or -1: the side (of the virus's up on screen) where the sphere fits with the least pushing
    // back on screen, so it doesn't end up over the virus.
    float PickSide(Vector2 headAt, Vector2 dir, float headR, float R, Rect area)
    {
        float best = 1f, bestPush = float.MaxValue;
        for (int s = -1; s <= 1; s += 2)
        {
            Vector2 g = headAt + Rotate(dir, s * sideAngle) * (headR + rise + R);
            float push = (Fit(g, R, area) - g).magnitude;
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

    bool ToLocal(Vector2 screen, out Vector2 at)
    {
        at = default;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screen, null, out Vector2 local)) return false;
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

    // ---------------- the ring ----------------

    // This frame's mounts: the inventory's ring, then the free one while there's room. Looks follow.
    void Gather()
    {
        _order.Clear();
        VirusInventory inv = Inv;
        if (inv)
        {
            foreach (Mount m in inv.Ring(_genome.genes.Count)) _order.Add(m);
            if (inv.CanAddMount) _order.Add(_free);
        }
        _drop.Clear();
        foreach (KeyValuePair<Mount, Look> kv in _looks)
            if (!_order.Contains(kv.Key)) _drop.Add(kv.Key);
        foreach (Mount m in _drop)
        {
            if (_looks[m].label) Destroy(_looks[m].label.gameObject);
            _looks.Remove(m);
        }
        if (_pressed != null && !_order.Contains(_pressed)) EndDrag();
        if (_hover >= _order.Count) _hover = -1;
    }

    Look LookOf(Mount m)
    {
        if (_looks.TryGetValue(m, out Look l)) return l;
        l = new Look();
        Text label = TerminalUI.Graphic<Text>("Mount", _canvasRect, Vector2.zero, new Vector2(120f, 16f));
        BottomLeft(label.rectTransform);
        TerminalUI.Style(label, _font, 11, text, TextAnchor.MiddleCenter);
        l.label = label;
        _looks[m] = l;
        return l;
    }

    float Gap => 360f / Mathf.Max(_order.Count, 1);
    Vector2 Spoke(Mount m) => Rotate(_entry, LookOf(m).angle);

    // Mounts slide to their places (evenly round, the mouth midway between two); a dragged one
    // follows the pointer. Store bars ease to their amounts and flash as they fill.
    void Settle(float dt)
    {
        float gap = Gap, k = 1f - Mathf.Exp(-dt / reflowTime), now = Time.unscaledTime;
        VirusInventory inv = Inv;
        for (int i = 0; i < _order.Count; i++)
        {
            Mount m = _order[i];
            Look l = LookOf(m);
            float target = (i + 0.5f) * gap;
            if (_dragging && m == _pressed) l.angle = _dragAngle;
            else if (float.IsNaN(l.angle)) l.angle = target;
            else l.angle += Mathf.DeltaAngle(l.angle, target) * k;

            if (m.kind != Kind.Store || !inv || m.index < 0 || m.index >= inv.slots.Count) continue;
            VirusInventory.Slot slot = inv.slots[m.index];
            float amount = Delayed(l, slot.Empty ? 0f : slot.amount, now, dt);
            if (amount > l.last + 1e-3f) l.flashAt = now;
            l.last = amount;
            float want = Mathf.Clamp01(amount / Mathf.Max(inv.capacity, 1e-3f));
            l.fill = Mathf.MoveTowards(Mathf.Lerp(l.fill, want, 1f - Mathf.Exp(-dt * 10f)), want, dt * 0.05f);
        }
    }

    // A store's amount as the bar shows it: as it was 'delay' seconds ago while the flow is feeding it
    // (the material still on its way), catching up once nothing is.
    static float Delayed(Look l, float amount, float now, float dt)
    {
        if (l.history.Count > 0 && now - l.history[l.history.Count - 1].x > 0.5f) { l.history.Clear(); l.delay = 0f; } // the view was shut
        if (now - l.fedAt > 0.1f) l.delay = Mathf.MoveTowards(l.delay, 0f, dt);
        l.history.Add(new Vector2(now, amount));
        float at = now - l.delay;
        while (l.history.Count > 1 && l.history[1].x <= at) l.history.RemoveAt(0);
        return l.shown = l.history[0].y;
    }

    // Hover, click, drag. A press on a mount that moves past dragThreshold drags it round the ring
    // (the others close up round it live); let go and it snaps into place. A click (no drag) on a
    // strand injects / loads it; on the free mount adds a store (RMB: a DNA slot); RMB on an empty
    // mount frees it.
    void Interact(float u, float now)
    {
        Mouse m = Mouse.current;
        VirusInventory inv = Inv;
        if (!_open || u < 0.95f || m == null || !inv)
        {
            EndDrag();
            _hover = -1;
            _onHub = false;
            _option = -1;
            _preview.Clear();
            return;
        }
        Vector2 pointer = m.position.ReadValue();
        int was = _hover;

        // The synthesizer comes first: its hub toggles the recipe menu, an option makes it (greyed:
        // nothing), a click anywhere else shuts the menu.
        PointCrafter(pointer, inv);
        if (_menu && _option >= 0 && _option != _optionWas) Say(CraftStage.Pointed, Recipes.recipes[_option]);
        _optionWas = _option;
        if (_menu && Recipes)
        {
            float wheel = m.scroll.ReadValue().y; // more recipes than fit: the wheel pages through them
            if (Mathf.Abs(wheel) > 0.01f) TurnPage(wheel > 0f ? -1 : 1);
        }
        if (_pressed == null && (_onHub || _option >= 0))
        {
            _hover = -1;
            if (m.leftButton.wasPressedThisFrame)
            {
                if (_option >= 0)
                {
                    if (_optionCan[_option]) { StartCraft(Recipes.recipes[_option], now); _menu = false; }
                    else Say(CraftStage.Refused, Recipes.recipes[_option]);
                }
                else if (_job == null) _menu = !_menu;
            }
            return;
        }
        if (_menu && (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame)) _menu = false;

        if (_pressed != null)
        {
            if (!_dragging && _pressed != _free && (pointer - _pressAt).magnitude > dragThreshold) _dragging = true;
            if (_dragging && ToLocal(pointer, out Vector2 at))
            {
                _dragAngle = Vector2.SignedAngle(_entry, at - _sphere);
                int from = inv.ring.IndexOf(_pressed);
                int to = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(_dragAngle, 360f) / Gap), 0, inv.ring.Count - 1);
                if (from >= 0 && to != from)
                {
                    inv.MoveMount(from, to);
                    Gather();
                }
            }
            if (!m.leftButton.isPressed)
            {
                if (!_dragging) Click(_pressed, now);
                EndDrag();
            }
        }

        _hover = _dragging ? _order.IndexOf(_pressed) : Pick(pointer);
        if (_hover >= 0 && _hover != was) LookOf(_order[_hover]).jiggleAt = now; // pointed at: a jiggle
        if (_hover >= 0 && m.leftButton.wasPressedThisFrame)
        {
            _pressed = _order[_hover];
            _pressAt = pointer;
            _dragging = false;
        }
        if (_hover >= 0 && !_dragging && m.rightButton.wasPressedThisFrame) RightClick(_order[_hover], now);
    }

    void EndDrag()
    {
        _pressed = null;
        _dragging = false;
    }

    void Click(Mount m, float now)
    {
        LookOf(m).jiggleAt = now;
        if (m == _free) { Inv.AddMount(Kind.Store); return; }
        if (m.kind != Kind.Gene || m.index < 0) return;
        if (CanInject) Inject(m.index, now);
        else Load(m.index);
    }

    void RightClick(Mount m, float now)
    {
        VirusInventory inv = Inv;
        if (m == _free) inv.AddMount(Kind.Gene);
        else if (_job == null) inv.RemoveMount(inv.ring.IndexOf(m)); // not while crafting: it may be drawing from or filling it
        Gather();
    }

    // The mount under a screen point: the one nearest its angle (within half the gap), within reach.
    int Pick(Vector2 pointer)
    {
        if (!ToLocal(pointer, out Vector2 at)) return -1;
        Vector2 q = (at - _sphere) / Mathf.Max(_sphereR, 1e-3f);
        float r = q.magnitude;
        if (_order.Count == 0 || r > 1.04f || r < Rim - Len - 0.08f) return -1;
        float a = Vector2.SignedAngle(_entry, q), best = Gap * 0.5f;
        int pick = -1;
        for (int i = 0; i < _order.Count; i++)
        {
            float d = Mathf.Abs(Mathf.DeltaAngle(LookOf(_order[i]).angle, a));
            if (d < best) { best = d; pick = i; }
        }
        return pick;
    }

    void Refresh()
    {
        List<Genome.Gene> genes = _genome.genes;
        VirusInventory inv = Inv;
        Mount m = _hover >= 0 ? _order[_hover] : null;
        int selected = _genome.Selected;
        bool free = m == _free;
        Kind kind = m != null ? m.kind : Kind.Gene;
        int index = m != null ? m.index : selected; // nothing pointed at: the loaded strand
        Color dim = new Color(line.r, line.g, line.b, 0.7f);
        if (_dragging)
        {
            _state.Set("[<>] DROP TO PLACE");
            _state.text.color = live;
            return;
        }
        if (_option >= 0)
        {
            Crafting.Recipe r = Recipes.recipes[_option];
            string cost = "";
            foreach (Crafting.Cost c in r.costs) cost += (cost.Length > 0 ? " + " : "") + Mathf.CeilToInt(c.amount) + " " + c.substance.code;
            _code.Set(r.code + " // " + r.name + "   " + cost);
            _code.text.color = _optionCan[_option] ? Saturated(r.color) : dim;
            _state.Set(_optionCan[_option] ? "[>] CLICK TO SYNTHESIZE" : "[X] " + _optionWhy[_option]);
            _state.text.color = _optionCan[_option] ? live : TerminalUI.Blood;
            return;
        }
        if (_onHub || m == null && _job != null)
        {
            _code.Set(_job != null ? "SYNTHESIZING " + _job.code + " // " + _job.name : "SYNTHESIZER");
            _code.text.color = _job != null ? Saturated(_job.color) : text;
            _state.Set(_job != null ? (_phase == Phase.Dispense ? "[>>] DISPENSING" : "[<<] DRAWING " + (_step + 1) + " / " + _plan.Count)
                       : _menu ? "[LMB] CLOSE" : "[LMB] CRAFT");
            _state.text.color = _job != null ? live : text;
            return;
        }
        if (free)
        {
            _code.Set("FREE SLOT  " + inv.ring.Count + " / " + inv.maxSlots);
            _code.text.color = text;
            _state.Set("[LMB] + STORE   [RMB] + DNA");
            _state.text.color = text;
        }
        else if (kind == Kind.Gene && index >= 0 && index < genes.Count)
        {
            Genome.Gene g = genes[index];
            _code.Set(g.code + " // " + g.name);
            _code.text.color = g.color;
            bool going = index == _injecting, loaded = index == selected, hovered = _hover >= 0;
            if (CanInject) _state.Set(going ? "[>>] INJECTING" : loaded && !hovered ? "[#] INJECTED" : "[>] CLICK TO INJECT");
            else _state.Set(loaded ? (!hovered ? "[#] LOADED" : "[>] CLICK TO UNLOAD") : "[>] CLICK TO LOAD");
            _state.text.color = going || loaded ? live : text;
        }
        else if (kind == Kind.Store && inv && index >= 0 && index < inv.slots.Count && !inv.slots[index].Empty)
        {
            VirusInventory.Slot slot = inv.slots[index];
            _code.Set(slot.code + " // " + slot.substance);
            _code.text.color = slot.color;
            _state.Set(Mathf.FloorToInt(LookOf(m).shown) + " / " + Mathf.RoundToInt(inv.capacity), false);
            _state.text.color = slot.amount >= inv.capacity - 0.01f ? live : text;
        }
        else if (m != null)
        {
            _code.Set(kind == Kind.Gene ? "EMPTY DNA SLOT" : "EMPTY STORE");
            _code.text.color = dim;
            _state.Set("[RMB] FREE SLOT   [DRAG] MOVE");
            _state.text.color = dim;
        }
        else
        {
            int stores = inv ? inv.slots.Count : 0;
            _code.Set(genes.Count + (genes.Count == 1 ? " STRAND" : " STRANDS") + " // " + stores + (stores == 1 ? " STORE" : " STORES"));
            _code.text.color = text;
            _state.Set("[ ] SELECT A STRAND   [DRAG] ARRANGE");
            _state.text.color = dim;
        }
    }

    // ---------------- drawing the ring ----------------

    void Draw(float e, float now)
    {
        _commands.Clear();
        _commands.SetRenderTarget(_texture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(-1f, 1f, -1f, 1f, -1f, 1f));

        FlatMesh.Clear();
        int dragged = _dragging ? _order.IndexOf(_pressed) : -1;
        for (int i = 0; i < _order.Count; i++)
            if (i != dragged) DrawMount(i, e, now);
        if (dragged >= 0) DrawMount(dragged, e, now); // over the rest
        DrawCrafter(e, now);

        if (!_ringMesh)
        {
            _ringMesh = new Mesh { name = "Genome Ring", hideFlags = HideFlags.DontSave };
            _ringMesh.MarkDynamic();
        }
        Apply(_ringMesh);
        _ringMesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 1f));
        _props.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));
        _commands.DrawMesh(_ringMesh, Matrix4x4.identity, _strandMat, 0, 0, _props);

        _commands.Blit(_texture, _display); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);
    }

    // One mount: its white base on the outline, then what it holds.
    void DrawMount(int i, float e, float now)
    {
        Mount m = _order[i];
        Look l = LookOf(m);
        Vector2 dir = Spoke(m);
        float amp = Amp();
        float reveal = Smooth(Mathf.Clamp01(e * 1.6f - i * 0.1f)); // each grows in from the outline
        bool hot = i == _hover;
        bool free = m == _free;

        Color baseColor = free ? new Color(line.r, line.g, line.b, (hot ? 0.6f : 0.3f) + 0.1f * Mathf.Sin(now * 4f)) : _outline;
        if (_dragging && m == _pressed) baseColor = live;
        Base(dir, amp, reveal, baseColor);

        if (free)
        {
            Vector2 c = dir * (Rim - 0.13f);
            Vector2 across = new Vector2(-dir.y, dir.x);
            float arm = 0.045f * reveal;
            Color plus = new Color(line.r, line.g, line.b, hot ? 1f : 0.55f);
            Quad(c - dir * arm, c + dir * arm, 0.014f, plus, plus);
            Quad(c - across * arm, c + across * arm, 0.014f, plus, plus);
            return;
        }

        if (m.kind == Kind.Gene)
        {
            if (m.index < 0 || m.index >= _genome.genes.Count) { Dashes(dir, reveal, hot || m == _outMount && _job != null); return; }
            int selected = _genome.Selected;
            float highlight = 0f, bright = 1f;
            if (hot || m == _pressed) highlight = 1f;
            else if (m.index == selected) highlight = 0.5f + 0.4f * Mathf.Sin(now * 5f);
            else if (selected >= 0) bright = 0.4f;
            float back = m.index == _injecting ? 0f : Smooth(Mathf.Clamp01((now - l.regrowAt) / 0.6f)); // away / growing back
            Color c = Tint(Saturated(_genome.genes[m.index].color), bright * (1f + 0.5f * highlight));
            float pushed = (now - l.dispenseAt) / dispenseTime;
            if (pushed >= 0f && pushed < 1f)
            {
                // Freshly made: pushed out of the nozzle, its front travelling out to the base. Eased in and
                // out, so it settles into place with no speed left (a cubic out shot it most of the way at once).
                float p = Smooth(pushed) * Mathf.Min(reveal, back);
                BuildStrand(m.index, dir, p, now - l.jiggleAt, amp, Tint(c, 1f + 0.6f * (1f - Smooth(pushed))), Len * (1f - p));
                return;
            }
            BuildStrand(m.index, dir, Mathf.Min(reveal, back), now - l.jiggleAt, amp, c);
            return;
        }

        VirusInventory inv = Inv;
        VirusInventory.Slot slot = inv && m.index >= 0 && m.index < inv.slots.Count ? inv.slots[m.index] : null;
        float take = 0f; // what the recipe pointed at would draw from it
        foreach (var p in _preview)
            if (p.slot == m.index) take += p.amount;
        float missing = 0f; // what it's short of, beyond this store's fill
        foreach (var p in _short)
            if (p.slot == m.index) missing += p.amount;
        float cap = inv ? Mathf.Max(inv.capacity, 1e-3f) : 1f;
        Bar(dir, amp, reveal, slot, l, hot, now, take / cap, missing / cap);
    }

    // The white base a mount stands on: a stub growing in from the outline, wide there, narrowing in.
    void Base(Vector2 dir, float amp, float reveal, Color color)
    {
        float outerHalf = amp * 1.7f, innerHalf = amp * 1.1f;
        float inner = Mathf.Lerp(1.02f, Rim - 0.03f, reveal);
        Taper(dir * 1.02f, dir * inner, outerHalf, innerHalf, color);
    }

    // An empty DNA slot: a dashed spoke where a strand would go.
    void Dashes(Vector2 dir, float reveal, bool hot)
    {
        Color c = new Color(line.r, line.g, line.b, hot ? 0.7f : 0.3f);
        Vector2 root = dir * Rim;
        for (float s = 0.02f; s + 0.035f <= Len * reveal; s += 0.07f)
            Quad(root - dir * s, root - dir * (s + 0.035f), 0.012f, c, c);
    }

    // A store slot: an outlined bar in from the base, filled from the base inward in its substance's
    // colour, a bright line at the fill's front, ticks at the quarters; it flashes as it fills. 'take'
    // (share of capacity) is what a recipe being pointed at would use: that end of the fill turns to stripes
    // crawling toward the hub, boxed in green if the recipe can be made or red if not, with a white line
    // where it would leave the level. 'missing' (share of capacity) is what the recipe is short of: a red
    // dashed stretch past the fill, as far as the fill would have to reach.
    void Bar(Vector2 dir, float amp, float reveal, VirusInventory.Slot slot, Look l, bool hot, float now, float take = 0f, float missing = 0f)
    {
        bool empty = slot == null || slot.Empty;
        Vector2 root = dir * Rim, along = -dir, across = new Vector2(-dir.y, dir.x);
        float len = Len * reveal, hw = amp * 0.95f;
        Vector2 inner = root + along * len;
        Color c = empty ? line : Saturated(slot.color);
        float glow = hot ? 1.4f : 1f;
        Color frame = empty ? new Color(line.r, line.g, line.b, hot ? 0.8f : 0.4f) : Tint(c, 0.9f * glow);

        Quad(root + across * hw, inner + across * hw, Frame, frame, frame);
        Quad(root - across * hw, inner - across * hw, Frame, frame, frame);
        Quad(inner + across * (hw + Frame * 0.5f), inner - across * (hw + Frame * 0.5f), Frame, frame, frame);
        for (int q = 1; q <= 3; q++)
        {
            Vector2 p = root + along * (len * q * 0.25f);
            Quad(p + across * hw, p + across * (hw + 0.022f), Frame, frame, frame);
        }

        float f = l.fill * reveal;
        if (empty || f <= 0.002f) return;
        float flash = Mathf.Clamp01(1f - (now - l.flashAt) / 0.35f);
        Color fill = Tint(c, (0.75f + 0.5f * flash) * glow);
        fill.a = 0.85f;
        Vector2 front = root + along * (len * f);
        float kept = Mathf.Max(0f, f - take * reveal);
        Vector2 level = root + along * (len * kept);
        if (kept > 0f) Quad(root, level, 2f * (hw - Frame * 1.5f), fill, fill);
        Color lip = Color.Lerp(c, Color.white, 0.5f + 0.3f * Mathf.Sin(now * 9f));
        float pulse = 0.5f + 0.5f * Mathf.Sin(now * 7f);
        if (take > 0f)
        {
            // What it would use: stripes of the fill's colour crawling toward the hub (a different
            // pattern, not just a shade, so it doesn't read as more level), boxed green / red for can /
            // can't; the level it would be left at marked across the bar.
            Color verdict = _previewCan ? live : TerminalUI.Blood;
            float inside = hw - Frame * 1.5f, from = len * kept, to = len * f;
            const float Stripe = 0.03f;
            for (float s = from - 2f * Stripe + Mathf.Repeat(now * 0.12f, 2f * Stripe); s < to; s += 2f * Stripe)
            {
                float a = Mathf.Max(s, from), b = Mathf.Min(s + Stripe, to);
                if (b > a) Quad(root + along * a, root + along * b, 2f * inside, fill, fill);
            }
            Color box = new Color(verdict.r, verdict.g, verdict.b, 0.75f + 0.25f * pulse);
            Quad(level + across * inside, front + across * inside, Frame, box, box);
            Quad(level - across * inside, front - across * inside, Frame, box, box);
            Color mark = new Color(1f, 1f, 1f, 0.8f + 0.2f * pulse);
            Quad(level + across * (hw + 0.02f), level - across * (hw + 0.02f), Frame * 1.8f, mark, mark);
            lip = Tint(lip, 0.6f);
        }
        Quad(front + across * (hw - Frame), front - across * (hw - Frame), Frame * 1.6f, lip, lip);
        DrawMissing(root, along, across, len, f, hw, missing, pulse);
    }

    // What a recipe is short of: red dashes on from the fill's front, as far as it would have to reach
    // (clamped to the bar; past the end, an arrow says it wouldn't fit in one store).
    void DrawMissing(Vector2 root, Vector2 along, Vector2 across, float len, float f, float hw, float missing, float pulse)
    {
        if (missing <= 0f) return;
        Color red = TerminalUI.Blood;
        red.a = 0.55f + 0.45f * pulse;
        float from = len * f, to = len * Mathf.Min(1f, f + missing), inside = hw - Frame * 1.5f;
        const float Dash = 0.022f;
        for (float s = from; s < to; s += 2f * Dash)
            Quad(root + along * s, root + along * Mathf.Min(s + Dash, to), 2f * inside, red, red);
        Vector2 end = root + along * to;
        Quad(end + across * (hw + 0.02f), end - across * (hw + 0.02f), Frame * 1.8f, red, red);
        if (f + missing > 1f) Taper(end + along * 0.01f, end + along * 0.05f, hw, 0f, red);
    }

    static Color Tint(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

    // Each mount's label, just outside the outline where it starts.
    void PlaceLabels(float open)
    {
        VirusInventory inv = Inv;
        List<Genome.Gene> genes = _genome.genes;
        for (int i = 0; i < _order.Count; i++)
        {
            Mount m = _order[i];
            Look l = LookOf(m);
            Text label = l.label;
            bool on = open > 0f;
            if (label.gameObject.activeSelf != on) label.gameObject.SetActive(on);
            if (!on) continue;
            bool hot = i == _hover;
            Color c = new Color(line.r, line.g, line.b, 0.75f);
            string s;
            if (m == _free) s = "+";
            else if (m.kind == Kind.Gene)
            {
                bool has = m.index >= 0 && m.index < genes.Count;
                s = has ? genes[m.index].code : "--";
                if (has && (hot || m.index == _genome.Selected)) c = Saturated(genes[m.index].color);
            }
            else
            {
                VirusInventory.Slot slot = inv && m.index >= 0 && m.index < inv.slots.Count ? inv.slots[m.index] : null;
                bool has = slot != null && !slot.Empty;
                s = has ? slot.code + " " + Mathf.FloorToInt(l.shown) : "--";
                if (has) c = Color.Lerp(c, Saturated(slot.color), hot ? 1f : 0.6f);
            }
            if (hot) c.a = 1f;
            label.text = s;
            label.color = new Color(c.r, c.g, c.b, c.a * open);
            Vector2 dir = Spoke(m);
            RectTransform r = label.rectTransform;
            r.sizeDelta = new Vector2(label.preferredWidth + 4f, 16f);
            r.pivot = new Vector2(0.5f - 0.5f * dir.x, 0.5f - 0.5f * dir.y); // leaning away from the sphere
            r.anchoredPosition = _sphere + dir * (_sphereR + 8f);
        }
    }

    // ---------------- synthesizer ----------------

    // In the middle of the sphere: a hub with a dial turning in it and a nozzle reaching out flush with
    // the mounts' inner ends. Click the hub: the recipes (Crafting) pop out round it, each a small disc
    // with its strand and its cost as pips (lit for what you have); pointing at one shows on the bars
    // what it would use, greyed ones can't be made (short of something, or nowhere to put it). Making
    // one: the nozzle turns to each store it needs and sucks its share up out of the bar (drops running
    // down the spoke into the hub, which fills with the mix), then turns to an empty DNA slot (a free
    // mount becomes one if there's none) and pushes the new strand out into it (a liquid: pours it into
    // its store). Shut mid-way, it's finished at once.

    static readonly Color Unavailable = new Color(0.55f, 0.58f, 0.62f, 0.5f);

    float TipRadius => Rim - Len - 0.012f; // flush with the mounts' inner ends

    // How many recipes a page holds, for 'n' recipes: the places are rings of discs round the hub,
    // innermost first, each clockwise from the top (alternate rings staggered), out to the mounts' ends
    // pulled back (menuStrands). The discs shrink (down to 45% of optionSize) until all fit; past that,
    // pages. Rebuilt only when the count or the room changes.
    int PerPage(int n)
    {
        float outer = Rim - strandLength * menuStrands - 0.03f;
        if (_placesFor == n && _placesAt == outer) return _places.Count;
        _placesFor = n;
        _placesAt = outer;
        for (float r = optionSize; ; r *= 0.92f)
        {
            bool smallest = r < optionSize * 0.45f;
            FillPlaces(r, outer, smallest ? int.MaxValue : n);
            if (_places.Count >= n || smallest) break;
        }
        if (_places.Count == 0) _places.Add(new Vector3(0f, hubSize * 2f, hubSize)); // no room at all: one
        return _places.Count;
    }

    void FillPlaces(float r, float outer, int n)
    {
        _places.Clear();
        float gap = r * 0.3f, rho = hubSize + gap * 1.5f + r;
        for (int ring = 0; rho + r <= outer && _places.Count < n; ring++, rho += 2f * r + gap)
        {
            int fit = Mathf.Min(Mathf.FloorToInt(2f * Mathf.PI * rho / (2f * r + gap)), n - _places.Count);
            float step = 360f / Mathf.Max(fit, 1), turn = ring % 2 == 1 ? step * 0.5f : 0f;
            for (int j = 0; j < fit; j++)
            {
                Vector2 c = Rotate(Vector2.up, -(j * step + turn)) * rho;
                _places.Add(new Vector3(c.x, c.y, r));
            }
        }
    }

    // The recipes on the page shown: [first, end).
    void PageRange(int n, out int first, out int end)
    {
        int per = PerPage(n), pages = Mathf.Max(1, (n + per - 1) / per);
        _page = Mathf.Clamp(_page, 0, pages - 1);
        first = _page * per;
        end = Mathf.Min(n, first + per);
    }

    int Pages(int n) => Mathf.Max(1, (n + PerPage(n) - 1) / PerPage(n));

    void TurnPage(int by)
    {
        int pages = Pages(Recipes.recipes.Count);
        if (pages <= 1) return;
        _page = (_page + by + pages) % pages;
        Say(CraftStage.PageTurned);
        _pop = 0f; // the new page pops out like the first
    }

    // Recipe 'i''s disc (centre and radius, sphere radii), 'first' the page's first: popping out from
    // the hub one after another, inner ring first ('_pop' restarts on a page turn).
    void OptionPlace(int i, int first, out Vector2 c, out float r)
    {
        int slot = i - first;
        Vector3 p = _places[Mathf.Clamp(slot, 0, _places.Count - 1)];
        float pop = Mathf.Clamp01(_pop * 1.8f - slot / (float)Mathf.Max(_places.Count, 1) * 0.8f);
        c = new Vector2(p.x, p.y) * Mathf.LerpUnclamped(0.25f, 1f, BackOut(pop));
        r = p.z * Smooth(pop);
    }

    // What the pointer is on: the hub or a recipe (and what each recipe would use, for the bars).
    void PointCrafter(Vector2 pointer, VirusInventory inv)
    {
        _onHub = false;
        _option = -1;
        _preview.Clear();
        Crafting crafting = Recipes;
        if (!crafting) return;
        List<Crafting.Recipe> recipes = crafting.recipes;
        _optionCan.Clear();
        _optionWhy.Clear();
        foreach (Crafting.Recipe r in recipes)
        {
            _optionCan.Add(Crafting.Can(r, inv, _genome, _preview, out string why));
            _optionWhy.Add(why);
        }
        _preview.Clear();
        _short.Clear();
        if (!ToLocal(pointer, out Vector2 at)) return;
        Vector2 q = (at - _sphere) / Mathf.Max(_sphereR, 1e-3f);
        if (_menu)
        {
            PageRange(recipes.Count, out int first, out int end);
            for (int i = first; i < end && _option < 0; i++)
            {
                OptionPlace(i, first, out Vector2 c, out float r);
                if ((q - c).magnitude < r * 1.1f) _option = i;
            }
        }
        if (_option < 0) _onHub = q.magnitude < hubSize * 1.25f;
        else
        {
            Crafting.Plan(recipes[_option], inv, _preview);
            _previewCan = _optionCan[_option];
            // Each shortfall on the store of that substance with the most room (none held: only the text says it).
            if (inv)
                foreach (Crafting.Cost c in recipes[_option].costs)
                {
                    float missing = Crafting.Short(c, inv);
                    if (missing <= 1e-3f) continue;
                    int best = -1;
                    float room = -1f;
                    for (int s = 0; s < inv.Slots.Count; s++)
                    {
                        VirusInventory.Slot slot = inv.Slots[s];
                        if (slot.Empty || slot.substance != c.substance.name || inv.capacity - slot.amount <= room) continue;
                        best = s;
                        room = inv.capacity - slot.amount;
                    }
                    if (best >= 0) _short.Add((best, missing));
                }
        }
    }

    void StartCraft(Crafting.Recipe r, float now)
    {
        VirusInventory inv = Inv;
        if (_job != null || !Crafting.Can(r, inv, _genome, _plan, out _)) return;
        int output = Crafting.Output(r, inv, _genome);
        if (output == -1)
        {
            inv.AddMount(Kind.Gene);
            output = inv.ring.Count - 1;
        }
        _outMount = inv.ring[output];
        _job = r;
        _step = 0;
        _phase = Phase.Turn;
        _stepAt = now;
        _taken = _hubFill = _dispensed = 0f;
        _deliveredJob = false;
        _jobTotal = 0f;
        foreach (var p in _plan) _jobTotal += p.amount;
        Gather();
        Say(CraftStage.Started, r);
    }

    Mount StoreMount(int slot)
    {
        VirusInventory inv = Inv;
        int at = inv ? inv.Find(Kind.Store, slot) : -1;
        return at >= 0 ? inv.ring[at] : null;
    }

    // Where the nozzle points: the store the job draws from next, then its output; idle, it turns slowly.
    float NozzleGoal()
    {
        if (_job == null) return _idle;
        Mount m = _step < _plan.Count ? StoreMount(_plan[_step].slot) : _outMount;
        return m != null && _looks.TryGetValue(m, out Look l) && !float.IsNaN(l.angle) ? l.angle : _nozzle;
    }

    // The menu and the nozzle ease; the job goes step by step: turn to a store, suck its share up (taken
    // from the store as it goes), ..., turn to the output, dispense.
    void Craft(float dt, float now)
    {
        if (_menu != _menuWas) Say(_menu ? CraftStage.MenuOpened : CraftStage.MenuClosed);
        _menuWas = _menu;
        Drawing = _job != null && _phase == Phase.Suck;
        _menuShown = Mathf.MoveTowards(_menuShown, _menu ? 1f : 0f, dt / 0.22f);
        _pop = _menu ? Mathf.MoveTowards(_pop, 1f, dt / 0.3f) : _menuShown;
        bool working = _job != null && _phase != Phase.Turn;
        _work = Mathf.MoveTowards(_work, working ? 1f : 0f, dt / (working ? 0.1f : 0.35f)); // the glow fades, not snaps
        if (_job != null) _glow = Saturated(_job.liquid ? _job.color : _hubFill > 0f ? _hubColor : _job.color);
        _spin = Mathf.Repeat(_spin + dt * (_job != null ? 220f : 25f), 360f);
        _idle = Mathf.Repeat(_idle + dt * 14f, 360f);
        float goal = NozzleGoal();
        if (float.IsNaN(_nozzle)) _nozzle = goal;
        _nozzle = Mathf.SmoothDampAngle(_nozzle, goal, ref _nozzleVel, turnTime, 1080f, Mathf.Max(dt, 1e-4f));
        // Tucked in while the menu is out; drawn in a little while turning, out to suck or dispense.
        float reach = _menu || _menuShown > 0.02f ? 0f : _job != null && _phase == Phase.Turn ? 0.8f : 1f;
        _nozzleOut = Mathf.MoveTowards(_nozzleOut, reach, dt / 0.15f);
        if (_job == null) return;

        VirusInventory inv = Inv;
        if (!inv) { _job = null; return; }
        switch (_phase)
        {
            case Phase.Turn:
                if (now - _stepAt < 0.15f || Mathf.Abs(Mathf.DeltaAngle(_nozzle, goal)) > 2f || Mathf.Abs(_nozzleVel) > 40f) break;
                _stepAt = now;
                _taken = 0f;
                Say(CraftStage.Docked, _job);
                if (_step < _plan.Count)
                {
                    _phase = Phase.Suck;
                    int slot = _plan[_step].slot;
                    _suckColor = slot < inv.slots.Count && !inv.slots[slot].Empty ? inv.slots[slot].color : line;
                    break;
                }
                _phase = Phase.Dispense;
                Say(CraftStage.Dispensing, _job);
                if (!_job.liquid)
                {
                    Crafting.Deliver(_job, inv, _genome, inv.ring.IndexOf(_outMount));
                    _deliveredJob = true;
                    if (_outMount != null) LookOf(_outMount).dispenseAt = now;
                }
                break;

            case Phase.Suck:
            {
                (int slot, float amount) = _plan[_step];
                float got = inv.TakeFrom(slot, Mathf.Min(amount - _taken, amount * dt / suckTime));
                _taken += got;
                float had = _hubFill * _jobTotal;
                _hubFill += got / Mathf.Max(_jobTotal, 1e-3f);
                _hubColor = had <= 1e-4f ? _suckColor : Color.Lerp(_hubColor, _suckColor, got / (had + got)); // the mix, by amount
                if (_taken >= amount - 1e-3f || now - _stepAt > suckTime * 1.5f) // (or the store ran dry)
                {
                    _step++;
                    _phase = Phase.Turn;
                    _stepAt = now;
                }
                break;
            }

            case Phase.Dispense:
            {
                float k = dt / dispenseTime;
                _hubFill = Mathf.Max(0f, _hubFill - k);
                if (_job.liquid)
                {
                    float add = Mathf.Min(_job.amount - _dispensed, _job.amount * k);
                    _dispensed += add;
                    inv.Add(Crafting.AsSubstance(_job), add);
                }
                if (now - _stepAt >= dispenseTime) FinishCraft();
                break;
            }
        }
    }

    // Whatever's left of the job, at once (the view shut, or it's done).
    void FinishCraft()
    {
        if (_job == null) return;
        Say(CraftStage.Made, _job);
        Drawing = false;
        VirusInventory inv = Inv;
        if (inv && _genome)
        {
            for (int i = _step; i < _plan.Count; i++)
                inv.TakeFrom(_plan[i].slot, _plan[i].amount - (i == _step && _phase == Phase.Suck ? _taken : 0f));
            if (_job.liquid) inv.Add(Crafting.AsSubstance(_job), _job.amount - _dispensed);
            else if (!_deliveredJob) Crafting.Deliver(_job, inv, _genome, _outMount != null ? inv.ring.IndexOf(_outMount) : -1);
        }
        _job = null;
        _outMount = null;
        _hubFill = 0f;
        // Idle drift carries on from where the nozzle stopped (its old idle angle could be anywhere: it
        // whipped round there the moment the strand was out).
        if (!float.IsNaN(_nozzle)) _idle = _nozzle;
        _nozzleVel = 0f;
    }

    void DrawCrafter(float e, float now)
    {
        Crafting crafting = Recipes;
        float k = Smooth(Mathf.Clamp01(e * 1.4f));
        if (!crafting || k <= 0f || float.IsNaN(_nozzle)) return;
        float hub = hubSize * k;
        Color body = Tint(line, 0.2f);
        body.a = 1f;
        Color rim = _onHub || _menu ? live : _outline;
        bool working = _job != null && _phase != Phase.Turn;
        float pulse = 0.5f + 0.5f * Mathf.Sin(now * 10f);

        // The nozzle, under the hub: a tapered arm with a channel down it and a lip at the tip.
        Vector2 nd = Rotate(_entry, _nozzle), across = new Vector2(-nd.y, nd.x);
        float reach = hub * 0.9f + (TipRadius - hubSize * 0.9f) * _nozzleOut * k;
        Taper(nd * (hub * 0.5f), nd * reach, 0.036f * k, 0.024f * k, _outline);
        Color channel = Color.Lerp(body, Tint(_glow, 0.8f + 0.4f * pulse), _work);
        Quad(nd * (hub * 0.5f), nd * (reach - 0.008f), 0.02f * k, channel, channel);
        Color lip = Color.Lerp(_outline, Color.Lerp(_glow, Color.white, pulse), _work);
        Quad(nd * reach + across * 0.032f * k, nd * reach - across * 0.032f * k, 0.013f * k, lip, lip);

        // Material on its way: sucked from a bar's fill down the spoke into the hub, or (a liquid)
        // poured from the tip out into its store.
        Mount flowing = _job == null ? null : _phase == Phase.Suck && _step < _plan.Count ? StoreMount(_plan[_step].slot)
                      : _phase == Phase.Dispense && _job.liquid ? _outMount : null;
        if (flowing != null)
        {
            Look l = LookOf(flowing);
            Vector2 d = Spoke(flowing), side = new Vector2(-d.y, d.x);
            float outer = Rim - Len * Mathf.Max(l.fill, 0.02f), inner = _phase == Phase.Suck ? hub * 0.6f : reach;
            bool inward = _phase == Phase.Suck;
            Color c = Saturated(inward ? _suckColor : _job.color);
            const float Spacing = 0.045f;
            float span = Mathf.Max(outer - inner, 0f);
            for (float a = Mathf.Repeat(now * 0.9f, Spacing); a <= span; a += Spacing)
            {
                float x = inward ? outer - a : inner + a;
                float edge = Mathf.Min(Mathf.Clamp01((x - inner) / 0.04f + 0.2f), Mathf.Clamp01((outer - x) / 0.04f + 0.2f));
                Disc(d * x + side * (Mathf.Sin(x * 60f + now * 6f) * 0.006f), 0.016f * edge, c, 12);
            }
        }

        // The hub: dark body, the mix it's drawn up filling it, a dial of ticks turning, its rim.
        Disc(Vector2.zero, hub, body, 40);
        if (_hubFill > 1e-3f)
        {
            Color fill = Saturated(_hubColor);
            fill.a = 0.9f;
            Disc(Vector2.zero, hub * 0.58f * Mathf.Sqrt(Mathf.Clamp01(_hubFill)), fill, 32);
        }
        Color tick = new Color(line.r, line.g, line.b, working ? 0.95f : 0.6f);
        for (int t = 0; t < 12; t++)
        {
            Vector2 d = Rotate(Vector2.up, _spin + t * 30f);
            Quad(d * (hub * 0.64f), d * (hub * (t % 3 == 0 ? 0.86f : 0.78f)), 0.009f * k, tick, tick);
        }
        Ring(Vector2.zero, hub, 0.012f * k, rim, 48);
        if (_onHub && _job == null) Ring(Vector2.zero, hub * 1.2f, 0.006f, new Color(live.r, live.g, live.b, 0.5f + 0.4f * pulse), 48);

        // The menu: rings of discs over most of the sphere (the mounts pulled back), each with its
        // recipe's glyph and its cost as pips (lit for what you have; one per ten units), greyed and
        // slashed where it can't be made. More than fit: pages, shown as dots in the hub.
        if (_menuShown <= 0f) return;
        VirusInventory inv = Inv;
        List<Crafting.Recipe> recipes = crafting.recipes;
        PageRange(recipes.Count, out int first, out int end);
        int pages = Pages(recipes.Count);
        if (pages > 1)
            for (int p = 0; p < pages && p < 7; p++)
            {
                Vector2 at = new Vector2((p - (Mathf.Min(pages, 7) - 1) * 0.5f) * hub * 0.26f, -hub * 0.3f);
                Disc(at, hub * (p == _page ? 0.09f : 0.055f), new Color(line.r, line.g, line.b, (p == _page ? 1f : 0.45f) * _menuShown), 10);
            }
        for (int i = first; i < end; i++)
        {
            OptionPlace(i, first, out Vector2 c, out float r);
            if (r <= 0.003f) continue;
            Crafting.Recipe rec = recipes[i];
            bool can = i < _optionCan.Count && _optionCan[i], hot = i == _option;
            if (hot) r *= 1.12f;
            Color col = can ? Saturated(rec.color) : Unavailable;
            float sc = r / 0.085f;
            // Makeable: a lit disc in its colour and a green ring round it. Not: dark, grey, slashed red.
            Disc(c, r, can ? Color.Lerp(body, col, hot ? 0.4f : 0.22f) : Tint(body, 0.6f), 32);
            Ring(c, r, (hot ? 0.012f : 0.008f) * Mathf.Min(sc, 1f), hot && can ? Color.white : col, 40);
            if (can)
            {
                float breathe = 0.5f + 0.5f * Mathf.Sin(now * 4f + i);
                Ring(c, r * (1.1f + 0.03f * breathe), 0.005f, new Color(live.r, live.g, live.b, 0.45f + 0.4f * breathe), 40);
            }
            DrawGlyph(rec.glyph, c + Vector2.up * (r * 0.12f), r * 0.5f, col, i, now, hot);
            if (!can)
            {
                Color slash = TerminalUI.Blood;
                slash.a = hot ? 0.95f : 0.7f;
                Vector2 d = new Vector2(0.7071f, 0.7071f) * (r * 0.78f);
                Quad(c - d, c + d, 0.01f * sc, slash, slash);
            }

            int pips = 0;
            foreach (Crafting.Cost cost in rec.costs) pips += Mathf.Max(1, Mathf.CeilToInt(cost.amount / 10f - 1e-3f));
            pips = Mathf.Min(pips, 8);
            float step = 0.017f * sc, x0 = -(pips - 1) * 0.5f * step;
            int n = 0;
            foreach (Crafting.Cost cost in rec.costs)
            {
                int count = Mathf.Max(1, Mathf.CeilToInt(cost.amount / 10f - 1e-3f));
                float have = inv ? inv.Total(cost.substance.name) : 0f;
                for (int p = 0; p < count && n < pips; p++, n++)
                {
                    bool owned = have >= Mathf.Min((p + 1) * 10f, cost.amount) - 1e-3f;
                    Vector2 pp = c + new Vector2(x0 + n * step, -0.058f * sc);
                    if (owned) Disc(pp, 0.0065f * sc, Saturated(cost.substance.color), 10);
                    else Ring(pp, 0.0058f * sc, 0.0022f * sc, TerminalUI.Blood, 12); // missing: a hollow red ring
                }
            }
        }
    }

    // A recipe's picture (Crafting.Glyph), centred on 'c', 's' its half size (sphere radii), flat in one
    // colour so each reads by shape. Pointed at, it comes alive a little (turns, throbs).
    void DrawGlyph(Crafting.Glyph glyph, Vector2 c, float s, Color col, int seed, float now, bool hot)
    {
        float t = hot ? now : 0f, w = s * 0.14f; // line width
        Vector2 P(float x, float y) => c + new Vector2(x, y) * s;
        switch (glyph)
        {
            case Crafting.Glyph.Burst: // lysis: a core blown apart, rays long and short
                Disc(c, s * 0.26f * (1f + 0.15f * Mathf.Sin(t * 9f)), col, 16);
                for (int j = 0; j < 8; j++)
                {
                    Vector2 d = Rotate(Vector2.up, j * 45f + t * 40f);
                    Taper(c + d * (s * 0.42f), c + d * (s * (j % 2 == 0 ? 1f : 0.72f)), s * 0.12f, 0f, col);
                }
                break;
            case Crafting.Glyph.Copy: // replication: an outline and its filled twin
            {
                float o = s * 0.22f * (1f + 0.3f * Mathf.Sin(t * 5f));
                Ring(c + new Vector2(-o, o), s * 0.5f, w, col, 28);
                Disc(c + new Vector2(o, -o), s * 0.46f, col, 24);
                break;
            }
            case Crafting.Glyph.Shell: // capsid: a hexagon with its facets
            {
                float turn = t * 30f;
                for (int j = 0; j < 6; j++)
                {
                    Vector2 a = c + Rotate(Vector2.up, turn + j * 60f) * (s * 0.92f), b = c + Rotate(Vector2.up, turn + (j + 1) * 60f) * (s * 0.92f);
                    Quad(a, b, w, col, col);
                    if (j % 2 == 0) Quad(c, a, w * 0.6f, col, col);
                }
                Disc(c, s * 0.14f, col, 10);
                break;
            }
            case Crafting.Glyph.Spike: // a spike protein on a membrane: stalk, three-lobed head
            {
                float lift = 0.08f * Mathf.Sin(t * 6f);
                Quad(P(-0.8f, -0.85f), P(0.8f, -0.85f), w, col, col);
                Taper(P(0f, -0.85f), P(0f, 0.25f + lift), s * 0.09f, s * 0.15f, col);
                Disc(P(-0.27f, 0.45f + lift), s * 0.25f, col, 14);
                Disc(P(0.27f, 0.45f + lift), s * 0.25f, col, 14);
                Disc(P(0f, 0.72f + lift), s * 0.25f, col, 14);
                break;
            }
            case Crafting.Glyph.Scissors: // integrase: cuts the host's DNA and splices in
            {
                float open = 0.14f * Mathf.Sin(t * 8f);
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector2 handle = P(side * (0.42f + open), -0.62f), tip = P(-side * (0.5f + open), 0.9f);
                    Vector2 d = (tip - handle).normalized;
                    Taper(handle + d * (s * 0.2f), tip, s * 0.11f, 0f, col);
                    Ring(handle, s * 0.2f, w * 0.8f, col, 16);
                }
                Disc(P(0f, 0.08f), s * 0.08f, col, 8);
                break;
            }
            case Crafting.Glyph.Drop: // a liquid
                Disc(P(0f, -0.25f), s * 0.56f, col, 24);
                Taper(P(0f, 0.02f), P(0f, 0.95f), s * 0.48f, 0f, col);
                Disc(P(-0.2f, -0.35f), s * 0.12f, Tint(col, 0.35f), 10); // a glint, dark on the colour
                break;
            case Crafting.Glyph.Chain: // two links
                Ring(P(-0.3f, 0f), s * 0.42f, w, col, 24);
                Ring(P(0.3f, 0f), s * 0.42f, w, col, 24);
                break;
            case Crafting.Glyph.Bolt:
                Taper(P(0.35f, 0.95f), P(-0.28f, 0.05f), s * 0.03f, s * 0.14f, col);
                Quad(P(-0.32f, 0.05f), P(0.32f, 0.05f), s * 0.24f, col, col);
                Taper(P(0.28f, 0.05f), P(-0.35f, -0.95f), s * 0.14f, s * 0.03f, col);
                break;
            case Crafting.Glyph.Star:
                Disc(c, s * 0.3f, col, 12);
                for (int j = 0; j < 5; j++)
                    Taper(c, c + Rotate(Vector2.up, j * 72f + t * 50f) * s, s * 0.26f, 0f, col);
                break;
            case Crafting.Glyph.Eye:
            {
                float look = 0.15f * Mathf.Sin(t * 3f);
                Taper(P(-0.45f, 0f), P(-0.98f, 0f), s * 0.07f, 0f, col);
                Taper(P(0.45f, 0f), P(0.98f, 0f), s * 0.07f, 0f, col);
                Ring(c, s * 0.5f, w, col, 28);
                Disc(P(look, 0f), s * 0.24f, col, 16);
                break;
            }
            case Crafting.Glyph.Shield:
                Taper(P(0f, 0.6f), P(0f, -0.95f), s * 0.72f, 0f, col);
                Quad(P(-0.72f, 0.6f), P(0.72f, 0.6f), s * 0.3f, col, col);
                Taper(P(0f, 0.45f), P(0f, -0.65f), s * 0.12f, 0f, Tint(col, 0.35f));
                break;
            default: // a plain strand
            {
                float sc = s / 0.042f;
                Helix((float u, out Vector2 at, out Vector2 x, out float scale) =>
                {
                    at = c + Vector2.up * ((u - 0.15f) * 0.3f * sc * 0.95f);
                    x = Vector2.right;
                    scale = 0.5f * sc;
                }, 0f, 0.3f, 0.06f, 17 + seed, col);
                break;
            }
        }
    }

    // ---------------- injection ----------------

    // Away from focus mode (no drill out): a click only loads the strand for later (again: unloads).
    void Load(int gene)
    {
        if (_injecting >= 0) return;
        _genome.Select(_genome.Selected == gene ? -1 : gene);
    }

    void Inject(int gene, float now)
    {
        if (_injecting >= 0) return; // one at a time
        _genome.Select(gene);
        _injecting = gene;
        _injectAt = now;
        _delivered = _pushed = false;
        _stage = InjectStage.None;
        _lastFront = float.NaN;
        Reach(InjectStage.Started);
        if (!_organism) _organism = _genome.GetComponentInParent<Organism>();
        _impactDelay = _organism ? _organism.grounded.focus.pump.ImpactDelay : 0.3f;
    }

    // The injected strand, drawn on the canvas (it leaves the sphere): out along its spoke, across the
    // sphere to the tube's mouth, down the tube into the ball, through the body to the top of the drill,
    // sliding along that path like a rope pulled through, shrinking and funnelling (thinner at the
    // front) as it goes into the tube. Eased in and out leg by leg; at the drill it waits, the virus
    // pumps (Intent.Inject), and the push sends it zooming down to the tip.
    void Travel(Camera cam, Vector2 headAt, float headR, float tube, float now)
    {
        List<Genome.Gene> genes = _genome.genes;
        if (_injecting >= genes.Count) _injecting = -1;
        if (_injecting < 0) { InjectSpeed = 0f; return; }
        int gene = _injecting;
        int at = -1;
        for (int i = 0; i < _order.Count; i++)
            if (_order[i].kind == Kind.Gene && _order[i].index == gene) at = i;
        if (at < 0) { _injecting = -1; return; }
        float R = Mathf.Max(_sphereR, 1f), L = Len, amp = Amp();
        Vector2 dir = Spoke(_order[at]);

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
        Vector2 drillAt = default, drillEnd = default;
        bool drill = _drill && _drill.Span(out Vector3 drillStart, out Vector3 drillTip)
                     && ToCanvas(cam, drillStart, out drillAt) && ToCanvas(cam, drillTip, out drillEnd);
        if (drill)
        {
            _path.Add(drillAt);
            _path.Add(drillEnd);
        }
        for (int i = 0; i < 3; i++) Chaikin(_path, _pathTemp, 1);
        _arc.Clear();
        _arc.Add(0f);
        for (int i = 1; i < _path.Count; i++) _arc.Add(_arc[i - 1] + Vector2.Distance(_path[i - 1], _path[i]));
        float total = _arc[_arc.Count - 1];
        float atTip = L * R, atMouth = Mathf.Max(atTip + 1f, NearestArc(mouth));
        float atHead = Mathf.Clamp(NearestArc(headAt), atMouth, total);
        float atDrill = drill ? Mathf.Clamp(NearestArc(drillAt), atHead, total) : total;

        // Legs: into the head (eased in and out: it gathers, rushes, settles), on to the top of the drill
        // (again), a pause there creeping in, then the virus draws up (the strand pulled back a little)
        // and pumps down, and the push sends it zooming down the drill (fast out, easing to the tip).
        // The strand shrinks to 45% on the first leg, then to a set length for the drill: the drill is
        // often a few pixels on screen (it points into the planet, away from the focus camera), and sized
        // to it the strand shrank to a dot and the shot couldn't be seen. Longer, its tail is seen pulled in.
        float t = now - _injectAt;
        float t1 = toHeadTime, t2 = t1 + toDrillTime, t3 = t2 + drillPause, t4 = t3 + _impactDelay, t5 = t4 + zoomTime;
        float fit = Mathf.Min(0.45f * R, Mathf.Max(total - atDrill, 0.5f * R) * 0.6f / L);
        const float Creep = 5f, Pull = 12f;
        float front, length;
        if (t < t1)
        {
            float x = ExpoInOut(t / t1);
            front = Mathf.Lerp(atTip, atHead, x);
            length = R * Mathf.Lerp(1f, 0.45f, x);
        }
        else if (t < t2)
        {
            float x = ExpoInOut((t - t1) / toDrillTime);
            front = Mathf.Lerp(atHead, atDrill, x);
            length = Mathf.Lerp(0.45f * R, fit, x);
        }
        else if (t < t3)
        {
            float x = (t - t2) / Mathf.Max(drillPause, 1e-3f);
            front = atDrill + Creep * (1f - (1f - x) * (1f - x));
            length = fit * (1f - 0.1f * x); // bunching up
            Reach(InjectStage.AtDrill);
        }
        else if (t < t4)
        {
            if (!_pushed) { _pushed = true; if (_organism) _organism.Press(Intent.Inject); Reach(InjectStage.Pushed); }
            float x = (t - t3) / Mathf.Max(_impactDelay, 1e-3f);
            float back = x < 0.8f ? Smooth(x / 0.8f) : 1f - (x - 0.8f) / 0.2f; // drawn up with the body, then shoved
            front = atDrill + Creep - Pull * back;
            length = fit * 0.9f;
        }
        else
        {
            if (_pulseTime < _injectAt) { _pulseTime = now; _pulseAt = drillAt; Reach(InjectStage.Shot); } // the push lands
            float x = Mathf.Clamp01((t - t4) / zoomTime);
            front = Mathf.Lerp(atDrill + Creep, total + L * fit, CubicOut(x));
            length = fit * (1f + 0.5f * Mathf.Sin(Mathf.PI * Mathf.Min(x * 1.5f, 1f))); // stretched by the rush

            // A streak down the drill behind the front, fading as it slows.
            if (drill && front > atDrill)
            {
                Color streak = Saturated(genes[gene].color);
                streak.a = 0.8f * (1f - x);
                Quad(drillAt, PathAt(Mathf.Min(front, total)), Mathf.Max(minInjectWidth * 0.35f, 3f), streak, streak);
            }
        }

        // Thick in the sphere, squeezed toward the tube's width from its mouth on, a little thinner by
        // the end -- but never under minInjectWidth on screen (the tube can be a few pixels: sized to
        // it exactly, the strand vanished as it left the sphere).
        if (front >= atMouth) Reach(InjectStage.IntoTube);
        float dt = Time.unscaledDeltaTime;
        InjectSpeed = float.IsNaN(_lastFront) || dt <= 0f ? 0f : Mathf.Abs(front - _lastFront) / (dt * R);
        _lastFront = front;

        float half = Mathf.Max((amp + BackboneWidth) * R, 1e-3f); // the helix's half height, full size
        float floor = Mathf.Min(1f, minInjectWidth * 0.5f / half);
        float thin = Mathf.Clamp(tube / half, floor, 1f);
        // Down the drill it thins to a thread by the tip, as if sinking into the cell.
        float Funnel(float a) => Mathf.Max(floor, a <= atTip ? 1f
            : a < atMouth ? Mathf.Lerp(1f, thin, Smooth((a - atTip) / (atMouth - atTip)))
            : Mathf.Lerp(thin, thin * 0.7f, Mathf.Clamp01((a - atMouth) / Mathf.Max(total - atMouth, 1f))))
            * (a > atDrill ? Mathf.Lerp(1f, 0.3f, Mathf.Clamp01((a - atDrill) / Mathf.Max(total - atDrill, 1f))) : 1f);

        float from = Mathf.Max(0f, L - front / length), to = Mathf.Min(L, L - (front - total) / length);
        Helix((float s, out Vector2 p, out Vector2 x, out float scale) =>
        {
            float a = front - (L - s) * length;
            p = PathAt(a);
            Vector2 d = PathAt(a + 3f) - PathAt(a - 3f);
            x = d.sqrMagnitude > 1e-8f ? new Vector2(d.y, -d.x).normalized : Vector2.up;
            scale = R * Funnel(a);
        }, from, to, amp, gene, Saturated(genes[gene].color));

        if (!_delivered && front >= total)
        {
            _delivered = true;
            _burstTime = now;
            _burstAt = PathAt(total);
            _burstColor = Saturated(genes[gene].color);
            bool took = false;
            if (_drill)
            {
                _drill.Deliver();
                took = ImmuneSystem.Deliver(_drill.Cell, genes[gene]); // the right gene takes the cell over
            }
            Reach(InjectStage.Delivered, took);
            _genome.Delivered(gene);
        }
        if (t >= t5 + 0.15f)
        {
            _injecting = -1;
            InjectSpeed = 0f;
            // One use: the strand is spent, its mount left as an empty DNA slot (dashes) for a new one.
            Inv.GeneRemoved(gene);
            _genome.Consume(gene);
        }
    }

    // Expanding rings: a white pulse where the push lands at the top of the drill, and the gene's
    // colour bursting out at the tip when it's delivered.
    void Rings(float now)
    {
        float p = (now - _pulseTime) / 0.35f;
        if (p >= 0f && p < 1f)
        {
            Color c = _outline;
            c.a = (1f - p) * 0.9f;
            Ring(_pulseAt, Mathf.Lerp(6f, 36f, CubicOut(p)), 3f, c, 40);
        }
        float b = (now - _burstTime) / 0.6f;
        if (b >= 0f && b < 1f)
        {
            Color c = _burstColor;
            c.a = 1f - b;
            Ring(_burstAt, Mathf.Lerp(4f, 60f, CubicOut(b)), 4f * (1f - b) + 1f, c, 48);
            Ring(_burstAt, Mathf.Lerp(2f, 34f, CubicOut(b * 1.3f)), 3f, new Color(1f, 1f, 1f, c.a * 0.8f), 40);
            if (b < 0.3f) Disc(_burstAt, 10f * (1f - b / 0.3f), new Color(1f, 1f, 1f, 1f - b / 0.3f), 20);
        }
    }

    // ---------------- extraction flow ----------------

    // One chunk's stream: when it started (its front travels out from the chunk at the drops' speed)
    // and which store it has been feeding since when. Drops share a trunk (chunk, head, tube, into the
    // sphere) and pick their branch as they pass its end: the store that was current then. So when a
    // bar fills and the next takes over, drops already past the branch still pour into the old one while
    // new ones turn off toward the new one's mouth (swinging the whole line over looked like a spill).
    class Stream
    {
        public float startedAt, seenAt;
        public readonly List<Mount> targets = new List<Mount>();
        public readonly List<float> since = new List<float>();
    }
    readonly Dictionary<ResourceChunk, Stream> _streams = new Dictionary<ResourceChunk, Stream>();
    readonly List<ResourceChunk> _ended = new List<ResourceChunk>();

    // A polyline measured along its length.
    sealed class Polyline
    {
        public readonly List<Vector2> points = new List<Vector2>();
        readonly List<Vector2> _temp = new List<Vector2>();
        readonly List<float> _arc = new List<float>();
        public float builtAt = -1f;
        public float Length => _arc.Count > 0 ? _arc[_arc.Count - 1] : 0f;

        public void Smooth()
        {
            for (int i = 0; i < 3; i++) Chaikin(points, _temp, 1);
            _arc.Clear();
            _arc.Add(0f);
            for (int i = 1; i < points.Count; i++) _arc.Add(_arc[i - 1] + Vector2.Distance(points[i - 1], points[i]));
        }

        public Vector2 At(float a)
        {
            int lo = 0, hi = _arc.Count - 1;
            if (hi <= 0) return points.Count > 0 ? points[0] : Vector2.zero;
            a = Mathf.Clamp(a, 0f, _arc[hi]);
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (_arc[mid] <= a) lo = mid; else hi = mid;
            }
            float span = _arc[hi] - _arc[lo];
            return Vector2.Lerp(points[lo], points[hi], span > 1e-5f ? (a - _arc[lo]) / span : 0f);
        }

        public float Nearest(Vector2 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return _arc.Count > best ? _arc[best] : 0f;
        }
    }
    readonly Polyline _trunk = new Polyline();
    readonly Dictionary<Mount, Polyline> _branches = new Dictionary<Mount, Polyline>();

    // From the end of the trunk (inside the sphere, past the tube's mouth) to a store's bar: in at its
    // open inner end and down to the fill. Built once a frame per store in use.
    Polyline Branch(Mount m, float now)
    {
        if (!_branches.TryGetValue(m, out Polyline b)) _branches[m] = b = new Polyline();
        if (b.builtAt == now) return b;
        b.builtAt = now;
        float R = Mathf.Max(_sphereR, 1f), L = Len;
        Look l = LookOf(m);
        Vector2 dir = Rotate(_entry, l.angle);
        b.points.Clear();
        b.points.Add(_sphere + _entry * (0.45f * R));
        b.points.Add(_sphere + _entry * (0.3f * R)); // still heading in, so the join is smooth
        b.points.Add(_sphere + dir * ((Rim - L - 0.12f) * R));
        b.points.Add(_sphere + dir * ((Rim - L) * R));
        b.points.Add(_sphere + dir * ((Rim - L * Mathf.Max(l.fill, 0.02f)) * R)); // down onto the fill
        b.Smooth();
        return b;
    }

    // Material flowing from each chunk being extracted into this virus: drops travel from the chunk on
    // screen into the head, up the tube, across the sphere to its store's bar and down into the fill.
    // A new stream grows out from the chunk rather than appearing whole; the bars fill as drops land.
    void Flow(Camera cam, Vector2 headAt, float headR, float tube, float now, float open)
    {
        VirusInventory inv = Inv;
        ResourceField field = ResourceField.Any ? ResourceField.Instance : null;
        if (!inv || !field)
        {
            _streams.Clear();
            return;
        }
        float R = Mathf.Max(_sphereR, 1f);
        IReadOnlyList<ResourceChunk> chunks = field.Extracting;
        for (int c = 0; c < chunks.Count; c++)
        {
            ResourceChunk chunk = chunks[c];
            if (!chunk || chunk.Extractor != inv || chunk.Blocked) continue;
            int mount = inv.Find(Kind.Store, inv.SlotFor(chunk.substance));
            if (mount < 0 || !ToCanvas(cam, chunk.Centre, out Vector2 source)) continue;
            Mount target = inv.ring[mount];
            // Timed from the click that started it (drops fade in with the view), so the bar never gets
            // ahead of them; after the view was shut, it grows in again.
            if (!_streams.TryGetValue(chunk, out Stream st)) _streams[chunk] = st = new Stream { startedAt = now };
            if (now - st.seenAt > 0.25f && st.seenAt > 0f) { st.startedAt = now; st.targets.Clear(); st.since.Clear(); }
            st.seenAt = now;
            if (st.targets.Count == 0 || st.targets[st.targets.Count - 1] != target)
            {
                st.targets.Add(target);
                st.since.Add(now);
            }
            for (int i = st.targets.Count - 1; i >= 0; i--)
                if (!_order.Contains(st.targets[i])) { st.targets.RemoveAt(i); st.since.RemoveAt(i); } // slot freed
            while (st.since.Count > 1 && st.since[1] < now - 5f) { st.targets.RemoveAt(0); st.since.RemoveAt(0); } // long landed

            _trunk.points.Clear();
            _trunk.points.Add(source);
            _trunk.points.Add(headAt);
            Vector2 toSphere = _sphere - headAt;
            if (toSphere.sqrMagnitude > 1e-4f) _trunk.points.Add(headAt + toSphere.normalized * headR);
            _trunk.points.Add(_sphere + _entry * R);
            _trunk.points.Add(_sphere + _entry * (0.45f * R));
            _trunk.Smooth();
            float trunk = _trunk.Length;
            float atHead = _trunk.Nearest(headAt), atMouth = _trunk.Nearest(_sphere + _entry * R);
            // Chunk to head is timed, not paced: 'cross' seconds on a curve (Cross), then flowSpeed on.
            float cross = Mathf.Clamp(atHead / crossSpeed, crossTime.x, Mathf.Max(crossTime.x, crossTime.y));
            float toBranch = cross + (trunk - atHead) / flowSpeed; // seconds from the chunk to the end of the trunk

            // How far the drops can be: the front, and no further than the longest branch in use.
            float longest = 0f;
            for (int i = 0; i < st.targets.Count; i++)
            {
                Polyline b = Branch(st.targets[i], now);
                longest = Mathf.Max(longest, b.Length);
                Look tl = LookOf(st.targets[i]);
                float travel = toBranch + b.Length / flowSpeed;
                if (i == st.targets.Count - 1 || now - st.since[i + 1] < travel)
                {
                    tl.delay = Mathf.Min(travel, 3f); // drops still on their way to it
                    tl.fedAt = now;
                }
            }

            Color col = Saturated(chunk.substance.color);
            col.a = open;
            // Drops leave the chunk a fixed time apart and go only as far as the front has got (the oldest
            // is as old as the stream), so a new stream grows out on the same eased crossing.
            float every = flowSpacing / flowSpeed, oldest = now - st.startedAt, done = toBranch + longest / flowSpeed;
            for (float age = Mathf.Repeat(now, every); age <= oldest && age <= done; age += every)
            {
                float a = age < cross ? Cross(age / cross, atHead, cross) : atHead + (age - cross) * flowSpeed;
                Vector2 p;
                float size = flowSize;
                if (a <= trunk)
                {
                    p = _trunk.At(a);
                    if (a > atHead && a < atMouth) size = Mathf.Min(flowSize, Mathf.Max(tube * 0.8f, 2f)); // squeezed through the tube
                    size *= Mathf.Clamp01(a / 30f);
                }
                else
                {
                    // Past the branch: whichever store was being fed when this drop got there.
                    float passed = now - (age - toBranch);
                    int t = st.since.Count - 1;
                    while (t > 0 && st.since[t] > passed) t--;
                    Polyline b = Branch(st.targets[t], now);
                    float along = (age - toBranch) * flowSpeed;
                    if (along > b.Length) continue; // landed
                    p = b.At(along);
                    size *= Mathf.Clamp01((b.Length - along) / 24f); // swallowed by the fill
                }
                if (size <= 0.2f) continue;
                Disc(p, size, col);
            }
        }

        _ended.Clear();
        foreach (KeyValuePair<ResourceChunk, Stream> kv in _streams)
            if (!kv.Key || kv.Value.seenAt != now) _ended.Add(kv.Key);
        foreach (ResourceChunk c in _ended) _streams.Remove(c);
    }

    // Distance along the chunk-to-head stretch at u (0..1) of the crossing: a Hermite curve leaving
    // slowly (a quarter of the average speed), rushing, and arriving at flowSpeed so the join is smooth.
    // Both end speeds stay under 3x the average, so it never runs backwards.
    float Cross(float u, float length, float time)
    {
        float avg = length / time, v0 = avg * 0.25f * time, v1 = Mathf.Min(flowSpeed, avg * 2.5f) * time;
        float u2 = u * u, u3 = u2 * u;
        return (u3 - 2f * u2 + u) * v0 + (-2f * u3 + 3f * u2) * length + (u3 - u2) * v1;
    }

    // ---------------- overlay ----------------

    // What's in Verts / Colors / Tris (canvas units from the bottom left) into its own canvas-sized texture,
    // the way the ring is drawn.
    void DrawOverlay()
    {
        Vector2 area = _canvasRect.rect.size;
        int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
        if (!_travelTexture || _travelTexture.width != w || _travelTexture.height != h)
        {
            ReleaseTravel();
            _travelTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Genome Overlay", hideFlags = HideFlags.DontSave, antiAliasing = 4,
            };
            _travelTexture.Create();
            _travelDisplay = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Genome Overlay Display", hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _travelDisplay.Create();
        }
        if (!_travelMesh)
        {
            _travelMesh = new Mesh { name = "Genome Overlay", hideFlags = HideFlags.DontSave };
            _travelMesh.MarkDynamic();
        }
        Apply(_travelMesh);
        _travelMesh.RecalculateBounds();
        _props.SetColor(ColorId, new Color(1f, 1f, 1f, 0f));

        _commands.Clear();
        _commands.SetRenderTarget(_travelTexture);
        _commands.ClearRenderTarget(false, true, Color.clear);
        _commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, area.x, 0f, area.y, -1f, 1f));
        _commands.DrawMesh(_travelMesh, Matrix4x4.identity, _strandMat, 0, 0, _props);
        _commands.Blit(_travelTexture, _travelDisplay); // resolve the antialiasing
        Graphics.ExecuteCommandBuffer(_commands);

        _traveller.texture = _travelDisplay;
        if (!_traveller.enabled) _traveller.enabled = true;
    }

    // Screen-sized: only kept while something is moving.
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

        // The injected strand on its way out and material flowing in, over the bubble.
        _traveller = TerminalUI.Graphic<RawImage>("Overlay", _canvasRect, Vector2.zero, Vector2.zero);
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
        _title.full = "04 // HEAD";
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
        _props ??= new MaterialPropertyBlock();
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
        if (_ringMesh) Destroy(_ringMesh);
        ReleaseTravel();
        if (_travelMesh) Destroy(_travelMesh);
        _commands?.Release();
        _commands = null;
    }

    // ---------------- meshes ----------------

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

    // One strand on its mount (sphere radii): the helix in from the base toward the middle ('reveal'
    // of it). 'poked' seconds since it was pointed at: it jiggles, a wave running out along it from the
    // outline, dying away. 'color' is its backbones' (dimmed / highlighted already). 'inset' shifts it
    // in from the base (a strand being pushed out of the synthesizer's nozzle).
    void BuildStrand(int gene, Vector2 dir, float reveal, float poked, float amp, Color color, float inset = 0f)
    {
        Vector2 root = dir * Rim, along = -dir, across = new Vector2(-dir.y, dir.x);
        float shake = poked < 1.2f ? jiggle * Mathf.Exp(-poked * 5f) : 0f;
        Helix((float s, out Vector2 at, out Vector2 x, out float scale) =>
        {
            float wobble = shake > 0f ? shake * (s / Len) * Mathf.Sin(poked * 40f - s * 18f) : 0f;
            at = root + along * (s + inset) + across * wobble;
            x = across;
            scale = 1f;
        }, 0f, Len * reveal, amp, gene, color);
    }

    // Helix half-height (sphere radii): as much as fits between the mounts at their inner ends.
    float Amp() => Mathf.Min(0.06f, Mathf.PI * (Rim - Len) / Mathf.Max(_order.Count, 1) * 0.8f);

    // How far strands and bars reach in now: pulled back toward the outline while the recipe menu is out.
    float Len => strandLength * Mathf.Lerp(1f, menuStrands, Smooth(_menuShown));

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
        int start = Verts.Count;
        for (int i = 0; i < Samples; i++)
        {
            Vector2 tangent = points[Mathf.Min(i + 1, Samples - 1)] - points[Mathf.Max(i - 1, 0)];
            Vector2 normal = new Vector2(-tangent.y, tangent.x).normalized * widths[i];
            Verts.Add(points[i] + normal);
            Verts.Add(points[i] - normal);
            Colors.Add(color);
            Colors.Add(color);
        }
        for (int i = 0; i < Samples - 1; i++)
        {
            int a = start + i * 2;
            Tris.Add(a); Tris.Add(a + 1); Tris.Add(a + 2);
            Tris.Add(a + 1); Tris.Add(a + 3); Tris.Add(a + 2);
        }
    }
}

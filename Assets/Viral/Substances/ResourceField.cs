using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Random = UnityEngine.Random;

/// <summary>
/// Every ResourceChunk in the scene: spawns them, draws them, and runs extraction.
///
/// Spawning: fills the space among the cells (Surfaces with isCell) with the listed chunk prefabs, each
/// at a random size from its prefab's range (small ones commoner), some floating just off a cell's
/// surface (in reach from focus mode there), the rest out in the open; no overlaps. Hand-placed chunks
/// work too. Chunks are walkable bodies (ResourceChunk) that float in place.
///
/// Floating: Update steps every chunk (ResourceChunk.Step) before the simulation ticks, so anyone standing
/// on one is posed on where it is this frame. Chunks near the camera, stood on, settling or being extracted
/// step every frame; the rest every 4 frames on screen, 8 off, staggered (the skipped time is caught up).
/// A chunk at rest writes nothing. Who stands on what: one pass over Organism.All per frame.
///
/// Drawing: one buffer, one instanced draw per (shape, LOD) mesh (Custom/ResourceChunk) from the chunks'
/// transforms; the deformation is in the vertex stage, so a chunk costs a sphere-vs-frustum test and a
/// 64-byte buffer entry per frame. Past lodDistance a lighter mesh, past drawDistance or off screen nothing.
///
/// Focus mode (VirusMovement calls ShowCores each frame): chunks within extractRange of the virus show
/// their core (Custom/ResourceCore, over the focus sweep), unless a cell hides it (a raycast per shown
/// chunk every sightInterval). Clicking a core toggles extraction into the virus's VirusInventory: a stream
/// of dust in its colour flows into the virus, the chunk shrinks and deforms (the shader), and once
/// drained to poofAt it bursts into a cloud of its colour and is gone. Out of reach or no room: it stops.
///
/// Cost per frame: O(chunks) (cull + LOD + a float step, cheap per chunk) + O(organisms); O(chunks in reach)
/// for cores; extraction O(chunks being extracted). Past ~10k chunks, a spatial grid for the draw / reach
/// loops and a TransformAccessArray job for the stepping are the next steps.
/// </summary>
[DefaultExecutionOrder(-20)] // chunks moved before VirusMovement (-10) and the SimulationTicker (0) pose riders
public class ResourceField : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public ResourceChunk prefab;
        [Min(0)] public int count = 40;
    }

    [Header("Spawning")]
    public List<Entry> chunks = new List<Entry>();
    [Range(0f, 1f), Tooltip("Share of the chunks hovering just off a cell (reachable from focus mode there).")]
    public float nearCells = 0.6f;
    [Tooltip("How high above a cell's surface those hover (metres).")]
    public Vector2 nearCellHeight = new Vector2(3f, 16f);
    [Min(0f), Tooltip("How far past the cells the rest spread out (metres).")]
    public float spread = 40f;
    [Min(0f), Tooltip("Least gap between chunks and from cells (metres).")]
    public float spacing = 1.5f;
    [Min(1f), Tooltip("With no cells in the scene: the radius of the ball they fill around this object.")]
    public float radius = 120f;
    public int seed = 12345;

    [Header("Drawing")]
    [Min(1f)] public float lodDistance = 45f;
    [Min(1f)] public float drawDistance = 600f;
    [Tooltip("Custom/ResourceChunk. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader chunkShader;
    [Tooltip("Custom/ResourceCore.")]
    public Shader coreShader;
    [Tooltip("Hidden/DustCloud, handed to DustClouds.")]
    public Shader dustShader;

    [Header("Extraction")]
    [Min(1f), Tooltip("How far from the virus (metres) a chunk's core shows and it can be extracted.")]
    public float extractRange = 45f;
    [Min(0.1f), Tooltip("Units per second per metre of the chunk's radius (so a chunk drains in a time ~ radius squared).")]
    public float extractRate = 4f;
    [Range(0.5f, 1f), Tooltip("Drained this far, the rest goes in at once and the chunk poofs.")]
    public float poofAt = 0.9f;
    [Range(0.1f, 0.6f), Tooltip("Core size, as a share of the chunk's radius.")]
    public float coreSize = 0.32f;
    [Min(0f), Tooltip("Dust puffs per second flowing into the virus while extracting.")]
    public float streamRate = 18f;
    [Min(0.05f), Tooltip("Seconds between checks of whether a cell hides a core.")]
    public float sightInterval = 0.15f;
    [Tooltip("What can hide a core.")]
    public LayerMask occluders = ~0;

    /// <summary>(chunk, inventory) when a chunk is drained and poofs.</summary>
    public static event Action<ResourceChunk, VirusInventory> Drained;
    /// <summary>(chunk, inventory) when extraction starts.</summary>
    public static event Action<ResourceChunk, VirusInventory> Started;

    public static IReadOnlyList<ResourceChunk> All => s_all;
    public static bool Any => s_all.Count > 0;
    static readonly List<ResourceChunk> s_all = new List<ResourceChunk>();
    static readonly Dictionary<Transform, ResourceChunk> s_byTransform = new Dictionary<Transform, ResourceChunk>();
    static ResourceField s_instance;
    static Material s_chunkMat;

    /// <summary>The one Custom/ResourceChunk material: draws every chunk, and every chunk's (never drawn)
    /// renderer carries it so its Surface ripples with the material's _Ripple* values.</summary>
    public static Material SharedChunkMaterial()
    {
        if (s_chunkMat) return s_chunkMat;
        Shader shader = s_instance && s_instance.chunkShader ? s_instance.chunkShader : Shader.Find("Custom/ResourceChunk");
        if (!shader) return null;
        return s_chunkMat = new Material(shader) { name = "Resource Chunk", hideFlags = HideFlags.DontSave };
    }

    public static ResourceField Instance
    {
        get
        {
            if (s_instance) return s_instance;
            s_instance = FindAnyObjectByType<ResourceField>();
            if (!s_instance && Application.isPlaying) s_instance = new GameObject("Resource Field").AddComponent<ResourceField>();
            return s_instance;
        }
    }

    public static void Register(ResourceChunk c)
    {
        if (!s_all.Contains(c)) s_all.Add(c);
        s_byTransform[c.transform] = c;
    }

    public static void Unregister(ResourceChunk c)
    {
        s_all.Remove(c);
        s_byTransform.Remove(c.transform);
    }

    /// <summary>The chunk a transform is (a Surface's Space), or null.</summary>
    public static ResourceChunk Of(Transform t) => t && s_byTransform.TryGetValue(t, out ResourceChunk c) ? c : null;

    /// <summary>The chunks whose cores show this frame (focus mode), for picking.</summary>
    public IReadOnlyList<ResourceChunk> Cores => _cores;

    /// <summary>The chunk whose core is pointed at (set by VirusMovement each frame; null none).</summary>
    public ResourceChunk Hovered { get; set; }

    // ---------------- runtime ----------------

    struct Instance64 { public Vector4 positionScale, rotation, tint, look; }
    struct CoreInstance { public Vector4 positionScale, color, state; }
    const int ChunkStride = 64, CoreStride = 48;

    static readonly int ChunksId = Shader.PropertyToID("_Chunks"), OffsetId = Shader.PropertyToID("_InstanceOffset"),
                        CoresId = Shader.PropertyToID("_Cores");
    static readonly int Groups = Enum.GetValues(typeof(ChunkMesh.Shape)).Length * 2; // (shape, near / far)

    Instance64[][] _groups;
    int[] _groupCount;
    GraphicsBuffer _chunkBuffer, _coreBuffer;
    MaterialPropertyBlock[] _groupProps;
    MaterialPropertyBlock _coreProps;
    Material _coreMat;
    CoreInstance[] _coreData = new CoreInstance[64];

    readonly List<ResourceChunk> _cores = new List<ResourceChunk>();
    readonly List<ResourceChunk> _extracting = new List<ResourceChunk>();
    readonly Dictionary<ResourceChunk, float> _sightAt = new Dictionary<ResourceChunk, float>();
    readonly HashSet<ResourceChunk> _hidden = new HashSet<ResourceChunk>();
    readonly Dictionary<ResourceChunk, float> _streamOwed = new Dictionary<ResourceChunk, float>();
    readonly RaycastHit[] _hits = new RaycastHit[8];
    Transform _viewer;
    int _coresFrame = -1;

    /// <summary>Chunks being extracted right now.</summary>
    public IReadOnlyList<ResourceChunk> Extracting => _extracting;

    // A scene with cells (or hand-placed chunks) and no field gets one: in the editor the prefab's (with
    // its chunk list, so the scene fills up), in a build a bare one that only draws what's there.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<ResourceField>(FindObjectsInactive.Include)) return;
        if (!Any && !FindAnyObjectByType<Surface>()) return;
#if UNITY_EDITOR
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<ResourceField>("Assets/Viral/Prefabs/ResourceField.prefab");
        if (prefab)
        {
            Instantiate(prefab).name = "Resource Field";
            Debug.Log("ResourceField: none in the scene, so the prefab's was added for this session (editor only). " +
                      "Drag Assets/Viral/Prefabs/ResourceField.prefab into the scene to keep it in builds.");
            return;
        }
#endif
        if (Any) new GameObject("Resource Field").AddComponent<ResourceField>();
    }

    void OnEnable()
    {
        if (!s_instance) s_instance = this;
        if (dustShader && !DustClouds.Instance.shader) DustClouds.Instance.shader = dustShader;
    }

    void Start()
    {
        if (s_instance != this) return;
        Spawn();
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
        _chunkBuffer?.Release();
        _coreBuffer?.Release();
        if (_coreMat) Destroy(_coreMat);
    }

    /// <summary>Show cores round 'viewer' this frame (call every frame while in focus mode).</summary>
    public void ShowCores(Transform viewer)
    {
        _viewer = viewer;
        _coresFrame = Time.frameCount;
    }

    /// <summary>The shown core under a screen point: within its size on screen, or 'minPixels'.</summary>
    public ResourceChunk CoreAt(Camera cam, Vector2 pointer, float minPixels)
    {
        if (!cam) return null;
        ResourceChunk best = null;
        float bestD = float.MaxValue;
        foreach (ResourceChunk c in _cores)
        {
            if (!c) continue;
            Vector3 centre = c.Centre;
            Vector3 s = cam.WorldToScreenPoint(centre);
            if (s.z <= 0f) continue;
            float reach = Mathf.Max(minPixels, Vector2.Distance(s, cam.WorldToScreenPoint(centre + cam.transform.right * (c.CurrentRadius * coreSize * 1.4f))));
            float d = Vector2.Distance(s, pointer);
            if (d <= reach && d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>Starts extracting 'c' into 'into', or stops if it already is.</summary>
    public void ToggleExtract(ResourceChunk c, VirusInventory into)
    {
        if (!c || !into) return;
        if (c.Extractor == into) { Stop(c); return; }
        c.Extractor = into;
        c.Blocked = false;
        if (!_extracting.Contains(c)) _extracting.Add(c);
        Started?.Invoke(c, into);
    }

    void Stop(ResourceChunk c)
    {
        c.Extractor = null;
        _extracting.Remove(c);
        _streamOwed.Remove(c);
        if (!_settling.Contains(c)) _settling.Add(c); // its wobble dies down
    }

    // ---------------- floating ----------------

    void Update()
    {
        int frame = Time.frameCount;
        foreach (Organism o in Organism.All)
        {
            Transform on = o ? o.Surface : null;
            if (on && s_byTransform.TryGetValue(on, out ResourceChunk c)) c.occupiedFrame = frame;
        }

        float now = Time.time, near2 = lodDistance * lodDistance;
        Vector3 cam = SimulationTicker.CameraPosition;
        for (int i = 0; i < s_all.Count; i++)
        {
            ResourceChunk c = s_all[i];
            bool occupied = c.occupiedFrame == frame;
            if (!occupied && c.Still <= 0f && !c.Extractor)
            {
                Vector3 p = c.T.position;
                if ((p - cam).sqrMagnitude > near2)
                {
                    int every = SimulationTicker.OnScreen(p, c.radius * 1.3f) ? 4 : 8;
                    if ((i + frame) % every != 0) continue;
                }
            }
            c.Step(now, occupied);
        }
    }

    void LateUpdate()
    {
        if (_groupProps == null || _coreProps == null) // plain fields: remade after a play-mode script reload
        {
            _groupProps = new MaterialPropertyBlock[Groups];
            for (int i = 0; i < Groups; i++) _groupProps[i] = new MaterialPropertyBlock();
            _coreProps = new MaterialPropertyBlock();
        }
        float dt = Time.deltaTime;
        Extract(dt);
        Ease(dt);
        FindCores();
        DrawChunks();
        DrawCores();
        Labels();
    }

    // ---------------- extraction ----------------

    void Extract(float dt)
    {
        for (int i = _extracting.Count - 1; i >= 0; i--)
        {
            ResourceChunk c = _extracting[i];
            if (!c || !c.Extractor) { if (c) Stop(c); else _extracting.RemoveAt(i); continue; }
            VirusInventory into = c.Extractor;
            Vector3 centre = c.Centre, to = into.transform.position;
            if ((centre - to).sqrMagnitude > extractRange * extractRange * 1.2f) { Stop(c); continue; }

            float want = Mathf.Min(extractRate * c.radius * dt, c.Remaining);
            float got = into.Add(c.substance, want);
            c.Remaining -= got;
            c.Shrink();
            c.Blocked = got < want * 0.5f && !into.Accepts(c.substance);
            if (c.Blocked) continue; // stays hooked up: frees up again if a slot empties

            // Dust flowing into the virus, bowed out to one side, from the chunk's surface.
            _streamOwed.TryGetValue(c, out float owed);
            owed += streamRate * dt;
            float r = c.CurrentRadius;
            while (owed >= 1f)
            {
                owed -= 1f;
                Vector3 way = to - centre;
                Vector3 side = Vector3.Cross(way, Random.onUnitSphere).normalized * (way.magnitude * Random.Range(0.1f, 0.3f));
                Vector3 from = centre + (way.normalized + Random.insideUnitSphere * 0.8f).normalized * r * 0.8f;
                Color col = c.Tint;
                col.a = 0.85f;
                DustClouds.Stream(from, to, side, r * Random.Range(0.18f, 0.3f), col, Random.Range(0.7f, 1.1f));
            }
            _streamOwed[c] = owed;

            if (c.Extracted >= poofAt) Poof(c, into);
        }
    }

    void Poof(ResourceChunk c, VirusInventory into)
    {
        c.Remaining -= into.Add(c.substance, c.Remaining); // the rest in one go (what fits)
        Stop(c);
        Vector3 centre = c.Centre;
        Color col = c.Tint;
        col.a = 0.9f;
        DustClouds.Burst(centre, c.CurrentRadius * 1.3f, col, 36);
        Drained?.Invoke(c, into);
        if (Hovered == c) Hovered = null;
        // Anyone standing on it is let go, drifting on with it.
        foreach (Organism o in Organism.All)
            if (o && o.Surface == c.T) o.grounded.surface.Detach(c.Velocity);
        Destroy(c.gameObject);
    }

    // Only the few chunks that are (or were just) hovered or extracted are eased.
    void Ease(float dt)
    {
        ResourceChunk hovered = Hovered;
        if (hovered && !_lit.Contains(hovered)) _lit.Add(hovered);
        for (int i = _lit.Count - 1; i >= 0; i--)
        {
            ResourceChunk c = _lit[i];
            if (c) c.Hover = Mathf.MoveTowards(c.Hover, c == hovered ? 1f : 0f, dt * 6f);
            if (!c || c != hovered && c.Hover <= 0f) _lit.RemoveAt(i);
        }
        foreach (ResourceChunk c in _extracting)
            if (c) c.Agitation = Mathf.MoveTowards(c.Agitation, c.Blocked ? 0.3f : 1f, dt * 2f);
        for (int i = _settling.Count - 1; i >= 0; i--)
        {
            ResourceChunk c = _settling[i];
            if (c && !c.Extractor) c.Agitation = Mathf.MoveTowards(c.Agitation, 0f, dt * 0.8f);
            if (!c || c.Extractor || c.Agitation <= 0f) _settling.RemoveAt(i);
        }
    }

    readonly List<ResourceChunk> _lit = new List<ResourceChunk>(), _settling = new List<ResourceChunk>();

    // ---------------- cores ----------------

    // In focus mode: every chunk in reach of the viewer, less those a cell hides (a raycast per chunk
    // every sightInterval, staggered by when each came into reach).
    void FindCores()
    {
        _cores.Clear();
        if (_coresFrame < Time.frameCount - 1 || !_viewer) { if (Hovered) Hovered = null; return; }
        Camera cam = Camera.main;
        Vector3 at = _viewer.position;
        float range2 = extractRange * extractRange, now = Time.time;
        foreach (ResourceChunk c in s_all)
        {
            Vector3 centre = c.Centre;
            if ((centre - at).sqrMagnitude > range2) continue;
            if (!_sightAt.TryGetValue(c, out float next) || now >= next)
            {
                _sightAt[c] = now + sightInterval * Random.Range(0.8f, 1.2f);
                if (Hidden(cam, c, centre, c.CurrentRadius * coreSize)) _hidden.Add(c); else _hidden.Remove(c);
            }
            if (!_hidden.Contains(c) || c.Extractor) _cores.Add(c); // one being extracted stays shown
        }
        if (_sightAt.Count > 4 * _cores.Count + 64) // drop the ones long out of reach
        {
            s_stale.Clear();
            foreach (KeyValuePair<ResourceChunk, float> kv in _sightAt) if (!kv.Key || now - kv.Value > 5f) s_stale.Add(kv.Key);
            foreach (ResourceChunk k in s_stale) { _sightAt.Remove(k); _hidden.Remove(k); }
        }
    }

    static readonly List<ResourceChunk> s_stale = new List<ResourceChunk>();

    // Whether something other than the viewer or the chunk itself lies between the camera and the core.
    bool Hidden(Camera cam, ResourceChunk chunk, Vector3 centre, float coreRadius)
    {
        if (!cam) return false;
        Ray ray = cam.ScreenPointToRay(cam.WorldToScreenPoint(centre)); // right for the ortho focus camera too
        float dist = Vector3.Distance(ray.origin, centre) - coreRadius;
        if (dist <= 0f) return false;
        int n = Physics.RaycastNonAlloc(ray, _hits, dist, occluders, QueryTriggerInteraction.Ignore);
        Transform viewerRoot = _viewer.root;
        for (int i = 0; i < n; i++)
            if (!_hits[i].transform.IsChildOf(viewerRoot) && !_hits[i].transform.IsChildOf(chunk.transform)) return true;
        return false;
    }

    void DrawCores()
    {
        int n = _cores.Count;
        if (n == 0) return;
        Material mat = CoreMaterial();
        if (!mat) return;
        if (_coreData.Length < n) _coreData = new CoreInstance[Mathf.NextPowerOfTwo(n)];
        var bounds = new Bounds();
        for (int i = 0; i < n; i++)
        {
            ResourceChunk c = _cores[i];
            Vector3 p = c.Centre;
            float r = c.CurrentRadius * coreSize;
            Color col = Saturated(c.Tint);
            _coreData[i] = new CoreInstance
            {
                positionScale = new Vector4(p.x, p.y, p.z, r),
                color = new Vector4(col.r, col.g, col.b, c.Hover),
                state = new Vector4(c.Extracted / poofAt, c.Agitation, c.Seed, c.Blocked ? 1f : 0f),
            };
            if (i == 0) bounds = new Bounds(p, Vector3.one * r * 3f); else bounds.Encapsulate(new Bounds(p, Vector3.one * r * 3f));
        }
        if (_coreBuffer == null || !_coreBuffer.IsValid() || _coreBuffer.count < n)
        {
            _coreBuffer?.Release();
            _coreBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _coreData.Length, CoreStride);
        }
        _coreBuffer.SetData(_coreData, 0, 0, n);
        _coreProps.SetBuffer(CoresId, _coreBuffer);
        var rp = new RenderParams(mat) { worldBounds = bounds, matProps = _coreProps, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        Graphics.RenderMeshPrimitives(rp, ChunkMesh.Ball(), 0, n);
    }

    static Color Saturated(Color c)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        return Color.HSVToRGB(h, Mathf.Clamp01(s * 1.6f + 0.2f), 1f);
    }

    // ---------------- chunks ----------------

    void DrawChunks()
    {
        int n = s_all.Count;
        if (n == 0) return;
        Material mat = SharedChunkMaterial();
        if (!mat) return;
        if (_groups == null || _groups.Length != Groups || _groups[0].Length < n)
        {
            int size = Mathf.NextPowerOfTwo(Mathf.Max(n, 64));
            _groups = new Instance64[Groups][];
            for (int g = 0; g < Groups; g++) _groups[g] = new Instance64[size];
            _groupCount = new int[Groups];
        }
        Array.Clear(_groupCount, 0, Groups);

        Vector3 cam = SimulationTicker.CameraPosition;
        float lod2 = lodDistance * lodDistance, draw2 = drawDistance * drawDistance;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
        foreach (ResourceChunk c in s_all)
        {
            Transform t = c.T;
            Vector3 home = t.position;
            float r = t.lossyScale.x, reach = r * 1.3f, d2 = (home - cam).sqrMagnitude;
            if (d2 > draw2 || !SimulationTicker.OnScreen(home, reach)) continue;
            int g = (int)c.shape * 2 + (d2 < lod2 ? 0 : 1);
            Quaternion q = t.rotation;
            Color tint = c.Tint;
            _groups[g][_groupCount[g]++] = new Instance64
            {
                positionScale = new Vector4(home.x, home.y, home.z, r),
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                tint = new Vector4(tint.r, tint.g, tint.b, c.Hover),
                look = new Vector4(c.Seed, c.Extracted, c.Agitation, 0f),
            };
            min = Vector3.Min(min, home - Vector3.one * reach);
            max = Vector3.Max(max, home + Vector3.one * reach);
        }
        int total = 0;
        for (int g = 0; g < Groups; g++) total += _groupCount[g];
        if (total == 0) return;

        if (_chunkBuffer == null || !_chunkBuffer.IsValid() || _chunkBuffer.count < total)
        {
            _chunkBuffer?.Release();
            _chunkBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(total, 64)), ChunkStride);
        }
        var bounds = new Bounds((min + max) * 0.5f, max - min);
        int offset = 0;
        for (int g = 0; g < Groups; g++)
        {
            int count = _groupCount[g];
            if (count == 0) continue;
            _chunkBuffer.SetData(_groups[g], 0, offset, count);
            MaterialPropertyBlock props = _groupProps[g];
            props.SetBuffer(ChunksId, _chunkBuffer);
            props.SetInt(OffsetId, offset);
            bool near = g % 2 == 0;
            var rp = new RenderParams(mat)
            {
                worldBounds = bounds, matProps = props, receiveShadows = true,
                shadowCastingMode = near ? ShadowCastingMode.On : ShadowCastingMode.Off,
            };
            Graphics.RenderMeshPrimitives(rp, ChunkMesh.Get((ChunkMesh.Shape)(g / 2), near), 0, count);
            offset += count;
        }
    }

    Material CoreMaterial()
    {
        if (_coreMat) return _coreMat;
        Shader s = coreShader ? coreShader : Shader.Find("Custom/ResourceCore");
        if (!s) return null;
        return _coreMat = new Material(s) { name = "Resource Core", hideFlags = HideFlags.DontSave };
    }

    // ---------------- labels ----------------

    // A small terminal tag beside the hovered core (what it is, how much, what a click does) and a
    // percentage beside each one being extracted.
    Canvas _canvas;
    RectTransform _canvasRect;
    Font _font;
    readonly List<Text> _labels = new List<Text>();

    void Labels()
    {
        int used = 0;
        Camera cam = Camera.main;
        if (cam && _cores.Count > 0)
        {
            if (!_canvas)
            {
                _canvas = TerminalUI.Canvas("Resource Labels", transform, 585);
                _canvasRect = (RectTransform)_canvas.transform;
                _font = TerminalUI.Font(TerminalUI.DefaultFonts);
                _labels.Clear();
            }
            foreach (ResourceChunk c in _cores)
            {
                if (!c || c != Hovered && !c.Extractor) continue;
                Vector3 s = cam.WorldToScreenPoint(c.Centre + cam.transform.right * c.CurrentRadius * 1.1f);
                if (s.z <= 0f || !RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, s, null, out Vector2 local)) continue;
                Text t = Label(used++);
                string amount = c.Remaining.ToString("0.0") + " U";
                int pct = Mathf.RoundToInt(Mathf.Clamp01(c.Extracted / poofAt) * 100f);
                string state = c.Blocked ? "[!] STORES FULL" : c.Extractor ? "[>>] EXTRACTING " + pct + "%" : "[>] CLICK TO EXTRACT";
                t.text = c.substance.code + " // " + c.substance.name + "  " + amount + "\n" + state;
                t.color = c.Blocked ? TerminalUI.Blood : c.Extractor ? TerminalUI.Live : TerminalUI.Text;
                t.rectTransform.anchoredPosition = local + new Vector2(10f, 0f);
            }
        }
        for (int i = 0; i < _labels.Count; i++)
            if (_labels[i] && _labels[i].gameObject.activeSelf != i < used) _labels[i].gameObject.SetActive(i < used);
    }

    Text Label(int i)
    {
        while (_labels.Count <= i)
        {
            Text t = TerminalUI.Graphic<Text>("Core Label", _canvasRect, Vector2.zero, new Vector2(320f, 40f));
            TerminalUI.Style(t, _font, 13, TerminalUI.Text, TextAnchor.MiddleLeft);
            t.rectTransform.pivot = new Vector2(0f, 0.5f);
            var shadow = t.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.8f);
            _labels.Add(t);
        }
        return _labels[i];
    }

    // ---------------- spawning ----------------

    struct CellBall { public Vector3 centre; public float radius; }

    void Spawn()
    {
        int total = 0;
        foreach (Entry e in chunks) if (e != null && e.prefab) total += e.count;
        if (total == 0) return;

        var state = Random.state;
        Random.InitState(seed);
        var cells = new List<CellBall>();
        var area = new Bounds(transform.position, Vector3.one * radius * 2f);
        bool any = false;
        foreach (Surface s in FindObjectsByType<Surface>(FindObjectsSortMode.None))
        {
            if (!s.isCell) continue;
            Renderer r = s.GetComponent<Renderer>();
            if (!r) continue;
            Bounds b = r.bounds;
            cells.Add(new CellBall { centre = b.center, radius = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) });
            if (!any) { area = b; any = true; } else area.Encapsulate(b);
        }
        if (any) area.Expand(spread * 2f);

        // A grid of what's placed, so the overlap test only looks nearby.
        float cellSize = 8f;
        var grid = new Dictionary<Vector3Int, List<Vector4>>();
        Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.y / cellSize), Mathf.FloorToInt(p.z / cellSize));
        bool Free(Vector3 p, float r)
        {
            foreach (CellBall c in cells)
                if ((p - c.centre).sqrMagnitude < Sq(c.radius + r + spacing)) return false;
            Vector3Int k = Key(p);
            int reach = Mathf.CeilToInt((r * 2f + spacing + 4f) / cellSize);
            for (int x = -reach; x <= reach; x++)
            for (int y = -reach; y <= reach; y++)
            for (int z = -reach; z <= reach; z++)
                if (grid.TryGetValue(new Vector3Int(k.x + x, k.y + y, k.z + z), out List<Vector4> list))
                    foreach (Vector4 o in list)
                        if ((p - (Vector3)o).sqrMagnitude < Sq(r + o.w + spacing)) return false;
            return true;
        }

        foreach (Entry e in chunks)
        {
            if (e == null || !e.prefab) continue;
            ResourceChunk prefab = e.prefab;
            for (int i = 0; i < e.count; i++)
            {
                float r = prefab.RandomRadius();
                float room = r + prefab.bob.y; // it bobs: keep its whole sweep clear
                Vector3 p = default;
                bool placed = false;
                for (int tries = 0; tries < 30 && !placed; tries++)
                {
                    if (cells.Count > 0 && Random.value < nearCells)
                    {
                        CellBall c = cells[Random.Range(0, cells.Count)];
                        p = c.centre + Random.onUnitSphere * (c.radius + room + Random.Range(nearCellHeight.x, nearCellHeight.y));
                    }
                    else if (any)
                        p = new Vector3(Random.Range(area.min.x, area.max.x), Random.Range(area.min.y, area.max.y), Random.Range(area.min.z, area.max.z));
                    else
                        p = transform.position + Random.insideUnitSphere * radius;
                    placed = Free(p, room);
                }
                if (!placed) continue;
                Vector3Int key = Key(p);
                if (!grid.TryGetValue(key, out List<Vector4> cellList)) grid[key] = cellList = new List<Vector4>();
                cellList.Add(new Vector4(p.x, p.y, p.z, room));

                Instantiate(prefab, p, Random.rotation, transform).Resize(r);
            }
        }
        Random.state = state;
    }

    static float Sq(float x) => x * x;
}

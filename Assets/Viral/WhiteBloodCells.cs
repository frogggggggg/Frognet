using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Every white blood cell (WhiteBloodCell): spawns them among the cells, ticks them in one loop
/// (far / off-screen idle ones every few frames, staggered, skipped time handed back as dt; hunting
/// and swallowing ones every frame, so anything standing on them rides smoothly), and draws them all
/// in one instanced call per LOD mesh (Custom/WhiteBloodCell, WhiteBloodCellMesh) from one buffer.
///
/// Also the shared pieces they use:
/// - <see cref="Near"/>: organisms near a point, from a grid rebuilt at most once a frame
///   (O(organisms)), so sensing is O(nearby) per cell, never all-vs-all.
/// - Gripping (<see cref="Grip"/>, <see cref="Release"/>): a caught creature stays a live physics body, pulled in
///   by its cell's arm every physics step (WhiteBloodCell.Pull) and seized (Intent.Seized: it can't land), so it
///   can fight the pull and tear free.
/// - Swallowing (<see cref="Capture"/>, <see cref="Hold"/>, <see cref="Finish"/>): the victim's
///   Organism, brain, legs and colliders are switched off and it's carried in kinematically; stuck
///   antibodies go down with it. At the end an AI virus is destroyed and the player respawns where it
///   started (after <see cref="respawnDelay"/>). Events for sound / UI: <see cref="Noticed"/>,
///   <see cref="Engulfing"/>, <see cref="Absorbed"/>, <see cref="Respawned"/>.
///
/// In editor play with none in the scene, the prefab (Prefabs/WhiteBloodCells) is added for the
/// session; builds need it in the scene.
/// </summary>
public class WhiteBloodCells : MonoBehaviour
{
    [Header("Population")]
    public WhiteBloodCell cellPrefab;
    [Min(0)] public int count = 6;
    [Tooltip("Diameter (m) per cell (min, max).")]
    public Vector2 size = new Vector2(12f, 16f);
    [Min(0f), Tooltip("Clearance from anything else when spawning.")]
    public float spawnClearance = 4f;

    [Header("Swallowed player")]
    [Min(0f), Tooltip("Seconds after being swallowed before the player respawns where it started.")]
    public float respawnDelay = 1.5f;

    [Header("Drawing")]
    [Tooltip("A Custom/WhiteBloodCell material (it reads the instance buffer): Assets/Viral/WhiteBloodCell.mat. Edit it live. Empty: made from the shader.")]
    public Material material;
    [Tooltip("Custom/WhiteBloodCell. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader shader;
    [Tooltip("Hidden/ShrinkWrapCapture: captures a catch's real shape so the lips wrap round it (ShrinkWrap). " +
             "Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader wrapShader;
    [Tooltip("WhiteBloodCellBake.compute: bakes the near cells' shape once a frame for every pass to read. Loaded by " +
             "path when empty (editor only: keep it assigned for builds); without it near cells are shaped in every pass.")]
    public ComputeShader bake;
    [Min(1f), Tooltip("Past this from the camera cells draw the coarse mesh without lumps.")]
    public float lodDistance = 70f;
    [Min(1f)] public float drawDistance = 700f;
    [Min(1f), Tooltip("Idle cells tick every frame within this of the camera, then every 2..4 frames further out (x2 off screen).")]
    public float tickDistance = 60f;

    /// <summary>A cell started hunting something.</summary>
    public static event Action<WhiteBloodCell, Organism> Noticed;
    /// <summary>A cell's mouth caught something and is pulling it in (it may still tear free).</summary>
    public static event Action<WhiteBloodCell, Organism> Engulfing;
    /// <summary>A gripping cell gulped (every gulpInterval while it reels its catch in).</summary>
    public static event Action<WhiteBloodCell, Organism> Gulped;
    /// <summary>The catch tore itself out of the mouth.</summary>
    public static event Action<WhiteBloodCell, Organism> TornFree;
    /// <summary>The catch reached the body and starts merging in; the float is how fast it came in (m/s).</summary>
    public static event Action<WhiteBloodCell, Organism, float> Merging;
    /// <summary>Swallowed for good (an AI virus is destroyed right after; the player respawns).</summary>
    public static event Action<WhiteBloodCell, Organism> Absorbed;
    /// <summary>A swallowed player is back, at its start.</summary>
    public static event Action<Organism> Respawned;

    static readonly List<WhiteBloodCell> s_cells = new List<WhiteBloodCell>();
    public static IReadOnlyList<WhiteBloodCell> All => s_cells;
    static WhiteBloodCells s_instance;

    // Every cell in one buffer (near ones first, then far), two instanced draws.
    struct Instance { public Vector4 positionRadius, rotation, reach, motion, state, sway, extra, merge, side; }
    const int InstanceStride = 144;
    Instance[] _near, _far;
    GraphicsBuffer _buffer;
    MaterialPropertyBlock _nearProps, _farProps;
    Mesh _nearMesh, _farMesh;
    Material _ownMaterial;
    ShrinkWrap _wrap;
    GraphicsBuffer _baked, _directions; // near shapes baked this frame; the near mesh's vertex directions
    int _nearVerts, _rowLength;
    const int BakedStride = 64; // WhiteBloodCellBake.compute Baked: 4 float4s
    // The shape's own settings, copied from the material to the bake each frame (compute can't read materials).
    static readonly int[] ShapeIds = Array.ConvertAll(new[]
    {
        "_Lumps", "_LumpScale", "_Wobble", "_Flow", "_Lobes", "_Spikes", "_SpikeScale", "_SpikeWidth", "_SpikeSharpness",
        "_SpikeFlow", "_Pull", "_Flare", "_Lag", "_Peristalsis", "_Cup", "_Lip", "_Tendrils", "_TendrilLength", "_Speed",
    }, Shader.PropertyToID);
    static readonly int BakedId = Shader.PropertyToID("_Baked"), BakedOutId = Shader.PropertyToID("_BakedOut"),
                        UseBakedId = Shader.PropertyToID("_UseBaked"), BakedStrideId = Shader.PropertyToID("_BakedStride"),
                        DirectionsId = Shader.PropertyToID("_Directions"), TimeId = Shader.PropertyToID("_WbcTime"),
                        VertexCountId = Shader.PropertyToID("_VertexCount"), RowLengthId = Shader.PropertyToID("_RowLength"),
                        RowsId = Shader.PropertyToID("_Rows"), CountId = Shader.PropertyToID("_Count");
    static readonly int CellsId = Shader.PropertyToID("_Cells"), OffsetId = Shader.PropertyToID("_InstanceOffset"),
                        DetailId = Shader.PropertyToID("_LodDetail"), WrapMapsId = Shader.PropertyToID("_WrapMaps");

    // ---------------- lifecycle ----------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<WhiteBloodCells>(FindObjectsInactive.Include)) return;
        if (!FindAnyObjectByType<Surface>()) return; // no cells, nothing to patrol
#if UNITY_EDITOR
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<WhiteBloodCells>("Assets/Viral/Prefabs/WhiteBloodCells.prefab");
        if (prefab)
        {
            Instantiate(prefab).name = "White Blood Cells";
            Debug.Log("WhiteBloodCells: none in the scene, so the prefab's was added for this session (editor only). " +
                      "Drag Assets/Viral/Prefabs/WhiteBloodCells.prefab into the scene to keep it in builds.");
        }
#endif
    }

    public static void Register(WhiteBloodCell c)
    {
        if (!s_cells.Contains(c)) s_cells.Add(c);
        c.LastTick = Time.time;
    }

    public static void Unregister(WhiteBloodCell c) => s_cells.Remove(c);

    void OnEnable() { if (!s_instance) s_instance = this; }

    IEnumerator Start()
    {
        if (s_instance != this) yield break;
        yield return null; // after every Start, so SpawnManager has placed the cells
        foreach (VirusMovement v in FindObjectsByType<VirusMovement>(FindObjectsSortMode.None))
            if (v.TryGetComponent(out Organism o)) _spawns[o] = o.transform.position;
        Spawn();
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
        _buffer?.Release();
        if (_nearMesh) Destroy(_nearMesh);
        if (_farMesh) Destroy(_farMesh);
        if (_ownMaterial) Destroy(_ownMaterial);
        _wrap?.Dispose();
        _baked?.Release();
        _directions?.Release();
    }

    // Among the cells, clear of everything.
    void Spawn()
    {
        if (!cellPrefab || count == 0) return;
        var area = new Bounds();
        bool any = false;
        foreach (Surface s in FindObjectsByType<Surface>(FindObjectsSortMode.None))
        {
            if (!s.isCell || !s.Renderer) continue;
            if (!any) { area = s.Renderer.bounds; any = true; }
            else area.Encapsulate(s.Renderer.bounds);
        }
        if (!any) area = new Bounds(transform.position, Vector3.one * 100f);

        int made = 0;
        for (int tries = 0; tries < count * 40 && made < count; tries++)
        {
            float diameter = UnityEngine.Random.Range(size.x, Mathf.Max(size.x, size.y));
            var p = new Vector3(UnityEngine.Random.Range(area.min.x, area.max.x), UnityEngine.Random.Range(area.min.y, area.max.y),
                                UnityEngine.Random.Range(area.min.z, area.max.z));
            if (Physics.CheckSphere(p, diameter * 0.5f + spawnClearance, ~0, QueryTriggerInteraction.Ignore)) continue;
            WhiteBloodCell c = Instantiate(cellPrefab, p, Quaternion.identity, transform);
            c.name = "White Blood Cell";
            c.transform.localScale = Vector3.one * diameter;
            // Never drawn (forceRenderingOff), but Surface and RippleField read the ripple settings off it.
            if (c.TryGetComponent(out MeshRenderer mr)) mr.sharedMaterial = Material();
            made++;
        }
        if (made > 0 && PathManager.I) PathManager.I.Rescan(); // they're obstacles for flying AI
    }

    void Update()
    {
        if (s_instance != this) return;
        TickCells(Time.time);
        Draw();
    }

    // Simulation LOD: far / off-screen idle cells tick every few frames, staggered; busy ones every frame.
    void TickCells(float now)
    {
        Vector3 cam = SimulationTicker.CameraPosition;
        int frame = Time.frameCount;
        for (int i = s_cells.Count - 1; i >= 0; i--)
        {
            WhiteBloodCell c = s_cells[i];
            if (!c) { s_cells.RemoveAt(i); continue; }
            if (c.Current != WhiteBloodCell.State.Hunt && c.Current != WhiteBloodCell.State.Grip && c.Current != WhiteBloodCell.State.Engulf)
            {
                Vector3 pos = c.transform.position;
                int every = Mathf.Clamp(1 + (int)((pos - cam).magnitude / tickDistance), 1, 4);
                if (!SimulationTicker.OnScreen(pos, c.Radius * 2f)) every *= 2;
                if ((frame + i) % every != 0) continue;
            }
            float dt = Mathf.Min(now - c.LastTick, 0.25f);
            c.LastTick = now;
            if (dt > 0f) c.Tick(dt, now);
        }
    }

    // Grips pull in the physics step, so the pull is the same whatever the frame rate.
    void FixedUpdate()
    {
        if (s_instance != this || s_gripped.Count == 0) return;
        foreach (WhiteBloodCell c in s_cells)
            if (c && c.Current == WhiteBloodCell.State.Grip) c.Pull();
    }

    // ---------------- drawing ----------------

    void Draw()
    {
        int n = s_cells.Count;
        if (n == 0) return;
        Material mat = Material();
        if (!mat) return;
        if (!_nearMesh || _directions == null || !_directions.IsValid()) // a script reload keeps the mesh, drops the buffer
        {
            if (_nearMesh) Destroy(_nearMesh);
            _nearMesh = WhiteBloodCellMesh.Build(1f, out Vector3[] dirs, out _rowLength);
            _nearVerts = dirs.Length;
            _directions?.Release();
            _directions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, dirs.Length, 16);
            _directions.SetData(Array.ConvertAll(dirs, d => (Vector4)d));
        }
        if (!_farMesh) _farMesh = WhiteBloodCellMesh.Build(0.25f, out _, out _);
        if (_near == null || _near.Length < n) { _near = new Instance[Mathf.NextPowerOfTwo(n)]; _far = new Instance[_near.Length]; }
        if (_nearProps == null) { _nearProps = new MaterialPropertyBlock(); _farProps = new MaterialPropertyBlock(); } // reload wipes them
        _wrap ??= new ShrinkWrap(wrapShader ? wrapShader : Shader.Find("Hidden/ShrinkWrapCapture"));
        _wrap.Begin();

        Vector3 cam = SimulationTicker.CameraPosition;
        float lod2 = lodDistance * lodDistance, draw2 = drawDistance * drawDistance;
        int near = 0, far = 0;
        var bounds = new Bounds();
        foreach (WhiteBloodCell c in s_cells)
        {
            Transform t = c.transform;
            Vector3 pos = t.position;
            float r = c.Radius, reach = r * (2.5f + c.Reach.w); // arm, lips and wisps included
            float d2 = (pos - cam).sqrMagnitude;
            if (d2 > draw2 || !SimulationTicker.OnScreen(pos, reach)) continue;
            Quaternion q = t.rotation;
            // A catch in its mouth: capture its real shape, for the lips to wrap round it.
            int wrapSlot = -1;
            if ((c.Current == WhiteBloodCell.State.Grip || c.Current == WhiteBloodCell.State.Engulf) && c.Prey)
                wrapSlot = _wrap.Capture(c.Prey.transform, c.Spot, // measured from the mouth's centre, the wrap's own
                                          Mathf.Max(c.Mood.w * r * 3f, 1f));
            var inst = new Instance
            {
                positionRadius = new Vector4(pos.x, pos.y, pos.z, r),
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                reach = c.Reach,
                motion = c.Motion,
                state = c.Mood,
                sway = c.Sway,
                // x: spikes and ruffles fade out toward lodDistance, so the switch to the plain far mesh doesn't pop;
                // y, z: the bite (gape, squeeze); w: the catch's shape map slot (-1 none).
                extra = new Vector4(1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lod2 * 0.5f, lod2, d2)), c.Gape, c.Squeeze, wrapSlot),
                merge = c.MergeShape,
                side = c.Side,
            };
            if (d2 < lod2) _near[near++] = inst; else _far[far++] = inst;
            var b = new Bounds(pos, Vector3.one * reach * 2f);
            if (near + far == 1) bounds = b; else bounds.Encapsulate(b);
        }
        _wrap.Submit();
        if (near + far == 0) return;

        if (_buffer == null || !_buffer.IsValid() || _buffer.count < _near.Length)
        {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _near.Length, InstanceStride);
        }
        _buffer.SetData(_near, 0, 0, near);
        _buffer.SetData(_far, 0, near, far);

        bool baked = Bake(mat, near);

        var rp = new RenderParams(mat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
        if (near > 0)
        {
            _nearProps.SetBuffer(CellsId, _buffer);
            _nearProps.SetInt(OffsetId, 0);
            _nearProps.SetFloat(DetailId, 1f);
            _nearProps.SetTexture(WrapMapsId, _wrap.Maps);
            _nearProps.SetBuffer(BakedId, _baked);
            _nearProps.SetInt(BakedStrideId, _nearVerts);
            _nearProps.SetFloat(UseBakedId, baked ? 1f : 0f);
            rp.matProps = _nearProps;
            Graphics.RenderMeshPrimitives(rp, _nearMesh, 0, near);
        }
        if (far > 0)
        {
            _farProps.SetBuffer(CellsId, _buffer);
            _farProps.SetInt(OffsetId, near);
            _farProps.SetFloat(DetailId, 0f);
            _farProps.SetTexture(WrapMapsId, _wrap.Maps);
            _farProps.SetBuffer(BakedId, _baked); // unread, but bound
            _farProps.SetFloat(UseBakedId, 0f);
            rp.matProps = _farProps;
            Graphics.RenderMeshPrimitives(rp, _farMesh, 0, far);
        }
    }

    // Near cells' shapes, once each this frame, for every pass to read: one Shape per vertex, then normals from the
    // mesh grid. Instead of each pass (colour, each shadow cascade, depth, depth normals) rebuilding it three times.
    bool Bake(Material mat, int near)
    {
        int size = Mathf.NextPowerOfTwo(Mathf.Max(near, 1)) * _nearVerts;
        if (_baked == null || !_baked.IsValid() || _baked.count < size || _baked.stride != BakedStride)
        {
            _baked?.Release();
            _baked = new GraphicsBuffer(GraphicsBuffer.Target.Structured, size, BakedStride);
        }
        ComputeShader cs = BakeShader();
        if (!cs || near == 0 || !SystemInfo.supportsComputeShaders) return false;

        foreach (int id in ShapeIds) cs.SetFloat(id, mat.GetFloat(id));
        cs.SetFloat(DetailId, 1f);
        cs.SetFloat(TimeId, Time.time); // = the draw shader's _Time.y in play mode
        cs.SetInt(VertexCountId, _nearVerts);
        cs.SetInt(RowLengthId, _rowLength);
        cs.SetInt(RowsId, _nearVerts / _rowLength);
        cs.SetInt(CountId, near);
        int shape = cs.FindKernel("Bake"), normals = cs.FindKernel("Normals");
        if (!cs.IsSupported(shape) || !cs.IsSupported(normals)) return false; // didn't compile: shape per pass instead
        cs.SetBuffer(shape, CellsId, _buffer);
        cs.SetBuffer(shape, DirectionsId, _directions);
        cs.SetBuffer(shape, BakedOutId, _baked);
        cs.SetTexture(shape, WrapMapsId, _wrap.Maps);
        cs.SetBuffer(normals, BakedOutId, _baked);
        int groups = (_nearVerts + 63) / 64;
        cs.Dispatch(shape, groups, near, 1);
        cs.Dispatch(normals, groups, near, 1);
        return true;
    }

    ComputeShader BakeShader()
    {
#if UNITY_EDITOR
        if (!bake) bake = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Viral/WhiteBloodCellBake.compute");
#endif
        return bake;
    }

    Material Material()
    {
        if (material) return material;
        if (_ownMaterial) return _ownMaterial;
        Shader s = shader ? shader : Shader.Find("Custom/WhiteBloodCell");
        if (!s) return null;
        _ownMaterial = new Material(s) { name = "White Blood Cell", hideFlags = HideFlags.DontSave };
        return _ownMaterial;
    }

    // ---------------- organism grid ----------------

    const float GridCell = 16f;
    static readonly Dictionary<Vector3Int, List<Organism>> s_grid = new Dictionary<Vector3Int, List<Organism>>();
    static readonly Stack<List<Organism>> s_pool = new Stack<List<Organism>>();
    static int s_gridFrame = -1;

    /// <summary>Organisms (enabled, so not ones being swallowed) within 'radius' of 'at', into 'into'.</summary>
    public static void Near(Vector3 at, float radius, List<Organism> into)
    {
        into.Clear();
        if (s_gridFrame != Time.frameCount) BuildGrid();
        Vector3Int lo = Key(at - Vector3.one * radius), hi = Key(at + Vector3.one * radius);
        float r2 = radius * radius;
        for (int x = lo.x; x <= hi.x; x++)
        for (int y = lo.y; y <= hi.y; y++)
        for (int z = lo.z; z <= hi.z; z++)
        {
            if (!s_grid.TryGetValue(new Vector3Int(x, y, z), out List<Organism> list)) continue;
            foreach (Organism o in list)
                if (o && o.isActiveAndEnabled && (o.transform.position - at).sqrMagnitude <= r2) into.Add(o);
        }
    }

    static Vector3Int Key(Vector3 p) => Vector3Int.FloorToInt(p / GridCell);

    static void BuildGrid()
    {
        s_gridFrame = Time.frameCount;
        foreach (var list in s_grid.Values) { list.Clear(); s_pool.Push(list); }
        s_grid.Clear();
        foreach (Organism o in Organism.All)
        {
            Vector3Int k = Key(o.transform.position);
            if (!s_grid.TryGetValue(k, out List<Organism> list)) s_grid[k] = list = s_pool.Count > 0 ? s_pool.Pop() : new List<Organism>();
            list.Add(o);
        }
    }

    // ---------------- swallowing ----------------

    class Captive
    {
        public Organism organism;
        public Rigidbody body;
        public Vector3 scale;
        public RigidbodyInterpolation interpolation;
        public bool player;
        public Transform parent, pivot; // merging: squashed through a pivot aligned with the way it sinks in
        public Transform pin;           // what a camera following it follows instead: never deeper than where it met the surface
        public float surface;           // its distance from the cell's centre when it met the surface
        public readonly List<Behaviour> off = new List<Behaviour>();
        public readonly List<Collider> colliders = new List<Collider>();
    }

    static readonly Dictionary<Organism, Captive> s_captives = new Dictionary<Organism, Captive>();
    static readonly Dictionary<Organism, WhiteBloodCell> s_gripped = new Dictionary<Organism, WhiteBloodCell>();

    /// <summary>'by' has hold of 'o': it's torn off what it stood on and seized (can't land), but stays a live
    /// body that 'by' pulls in. False if something already holds or is swallowing it.</summary>
    public static bool Grip(WhiteBloodCell by, Organism o)
    {
        if (!o || !o.Rb || s_captives.ContainsKey(o) || s_gripped.ContainsKey(o)) return false;
        s_gripped[o] = by;
        o.Hold(Intent.Seized);
        if (o.OnSurface) o.grounded.surface.Detach((by.Spot - o.transform.position).normalized * 2f); // plucked off
        Engulfing?.Invoke(by, o);
        return true;
    }

    /// <summary>Let go of a gripped creature (torn free, or handed on to be swallowed).</summary>
    public static void Release(Organism o)
    {
        if (!o) return;
        s_gripped.Remove(o);
        o.Hold(Intent.Seized, 0f);
        o.ClearRestraint();
    }

    public static bool Gripped(Organism o) => o && s_gripped.ContainsKey(o);
    readonly Dictionary<Organism, Vector3> _spawns = new Dictionary<Organism, Vector3>();

    /// <summary>Takes 'o' out of play to be swallowed by 'by'. False if it's already being swallowed.</summary>
    public static bool Capture(WhiteBloodCell by, Organism o)
    {
        if (!o || s_captives.ContainsKey(o)) return false;
        Release(o);
        var c = new Captive { organism = o, body = o.Rb, scale = o.transform.localScale, player = o.GetComponent<VirusMovement>() };

        if (o.OnSurface) o.grounded.surface.Detach(Vector3.zero); // else it'd pop back onto its cell on respawn
        ImmuneSystem.EatStuck(o); // antibodies on it go down with it

        // Its simulation: the organism (and so its brain), legs, AI.
        c.off.Add(o);
        foreach (VirusAI ai in o.GetComponents<VirusAI>()) c.off.Add(ai);
        foreach (SpiderLegWalker legs in o.GetComponentsInChildren<SpiderLegWalker>()) c.off.Add(legs);
        foreach (Behaviour b in c.off) b.enabled = false;
        foreach (Collider col in o.GetComponentsInChildren<Collider>())
            if (col.enabled) { col.enabled = false; c.colliders.Add(col); }

        if (c.body)
        {
            c.interpolation = c.body.interpolation;
            if (!c.body.isKinematic) c.body.linearVelocity = c.body.angularVelocity = Vector3.zero;
            c.body.isKinematic = true;
            c.body.interpolation = RigidbodyInterpolation.None; // it's carried by its transform now
        }

        // The camera stops where it met the surface instead of sinking in after it (and clipping into the cell).
        c.surface = Vector3.Distance(o.transform.position, by.transform.position);
        c.pin = new GameObject(o.name + " (camera pin)").transform;
        c.pin.SetPositionAndRotation(o.transform.position, o.transform.rotation);
        c.pin.SetParent(by.transform, true);
        UniversalCamera.StandIn(o.transform, c.pin);

        s_captives[o] = c;
        return true;
    }

    /// <summary>Carries a captive: world position, scale relative to its own, squashed to 'height' along 'axis'
    /// (volume kept: it widens as it flattens). The squash goes through a pivot the captive is parented to while
    /// held, aligned with the axis it first had, so its own mesh flattens whatever way it's turned.</summary>
    public static void Hold(Organism o, Vector3 position, float scale, Vector3 axis, float height)
    {
        if (!o || !s_captives.TryGetValue(o, out Captive c)) return;
        if (!c.pivot)
        {
            c.parent = o.transform.parent;
            c.pivot = new GameObject(o.name + " (merging)").transform;
            c.pivot.SetPositionAndRotation(o.transform.position, Quaternion.FromToRotation(Vector3.up, axis));
            o.transform.SetParent(c.pivot, true);
            o.transform.localPosition = Vector3.zero;
            o.transform.localScale = c.scale;
        }
        height = Mathf.Max(height, 0.05f);
        float wide = 1f / Mathf.Sqrt(height);
        c.pivot.position = position;
        c.pivot.localScale = new Vector3(wide, height, wide) * scale;

        // The pin follows it round the cell and back out, but never deeper than the surface.
        if (c.pin && c.pin.parent)
        {
            Vector3 centre = c.pin.parent.position, d = position - centre;
            float r = d.magnitude;
            if (r > 1e-4f) c.pin.position = centre + d * (Mathf.Max(r, c.surface) / r);
        }
    }

    static void Unpin(Captive c)
    {
        if (c.organism) UniversalCamera.StandIn(c.organism.transform, null);
        if (c.pin) Destroy(c.pin.gameObject);
        c.pin = null;
    }

    // Back out of the pivot (the captive stays where it is, small, under the cell's surface).
    static void Unpivot(Captive c)
    {
        if (!c.pivot) return;
        if (c.organism)
        {
            c.organism.transform.SetParent(c.parent, true);
            c.organism.transform.localScale = c.scale * 0.3f;
        }
        Destroy(c.pivot.gameObject);
        c.pivot = null;
    }

    /// <summary>Swallowed: an AI virus is destroyed, the player respawns after respawnDelay.</summary>
    public static void Finish(WhiteBloodCell by, Organism o)
    {
        if (!o || !s_captives.TryGetValue(o, out Captive c)) return;
        Absorbed?.Invoke(by, o);
        Unpivot(c);
        if (c.player && s_instance)
        {
            // Rides inside the cell until it respawns: left in the world, the cell crawled off and it poked out.
            o.transform.SetParent(by.transform, true);
            s_instance.StartCoroutine(s_instance.Respawn(c));
        }
        else
        {
            Unpin(c);
            s_captives.Remove(o);
            WhiteBloodCell.Forget(o);
            Destroy(o.gameObject);
        }
    }

    IEnumerator Respawn(Captive c)
    {
        yield return new WaitForSeconds(respawnDelay);
        s_captives.Remove(c.organism);
        Unpin(c);
        Organism o = c.organism;
        if (!o) yield break;
        Vector3 at = _spawns.TryGetValue(o, out Vector3 p) ? p : transform.position;
        o.transform.SetParent(c.parent, true);
        o.transform.position = at;
        o.transform.localScale = c.scale;
        if (c.body)
        {
            c.body.position = at;
            c.body.isKinematic = false;
            c.body.interpolation = c.interpolation;
            c.body.linearVelocity = c.body.angularVelocity = Vector3.zero;
        }
        foreach (Collider col in c.colliders) if (col) col.enabled = true;
        foreach (Behaviour b in c.off) if (b) b.enabled = true;
        o.Halt();
        Respawned?.Invoke(o);
    }

    public static void RaiseNoticed(WhiteBloodCell by, Organism o) => Noticed?.Invoke(by, o);
    public static void RaiseGulped(WhiteBloodCell by, Organism o) => Gulped?.Invoke(by, o);
    public static void RaiseTornFree(WhiteBloodCell by, Organism o) => TornFree?.Invoke(by, o);
    public static void RaiseMerging(WhiteBloodCell by, Organism o, float speed) => Merging?.Invoke(by, o, speed);
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Profiling;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Streams the world round the player: space is cut into cube sectors; sectors within loadDistance are loaded,
/// past unloadDistance unloaded (the gap is the hysteresis). What a sector holds is generated once, the first
/// time it loads, from the world seed and its coordinates (the same seed gives the same world), by the
/// <see cref="layers"/> in order: cells, then resource chunks hovering off them, white blood cells, AI viruses...
/// Region density (smooth noise across sectors) makes crowded patches and near-empty voids; the player's start
/// sector is always filled.
///
/// Persistence: every streamed object carries a <see cref="WorldEntity"/>. Objects belong to the sector they're
/// *in* (not the one they came from), so a cell dragged off by a rope or a white cell crawling away is stored
/// where it went. A slow sweep over the live objects stashes any standing in an unloaded sector into that
/// sector's record (pose, velocity, mass, <see cref="IWorldState"/>s) and destroys it; loading the sector spawns
/// its records back. Something destroyed by the game (a drained chunk, an eaten virus) is gone for good.
/// <see cref="Capture"/> / <see cref="Restore"/> turn the whole world into a <see cref="WorldSave"/> (SaveGame).
///
/// With a <see cref="Vessel"/> on the same object the world is the vessel's loop instead of open space: sectors
/// are its drifting cells (radial band x slice along the loop in the band's own turning frame x slice round the
/// tube, each about sectorSize across), so what's stored drifts with the blood by formula and comes back round;
/// records carry the vessel clock they were posed at and are turned into place when spawned (or checked against).
/// Counts are per sectorSize^3 of volume, scaled by the vessel's region for the layer's <see cref="Role"/>, and
/// nothing is placed within the wall margin. A save from the other layout is re-sorted by position on load.
///
/// Cost: a sector scan every scanInterval, O((2 loadDistance / sectorSize)^3) cheap box tests; the sweep checks
/// sweepPerFrame objects a frame (O(1) each); generating new sectors and spawning are held to spawnBudgetMs a frame
/// together (at least one of each), nearest sector first. Generation per sector: each try is tested against its own and its 26 neighbours' unspawned records (a few
/// hundred spheres) + one physics overlap; objects may straddle borders (a cell can be wider than a sector).
/// PathManager.Rescan is flagged at most every rescanInterval after changes (the scan itself waits for a flying
/// agent's query): at thousands of streamed cells with flying AI that scan is the next thing to make incremental.
/// </summary>
[DefaultExecutionOrder(-30)] // before ResourceField (-20) steps chunks
public class WorldStreamer : MonoBehaviour
{
    public enum Sizing { Multiplier, Metres }
    /// <summary>What a layer is, for the Vessel's regions (how much of it a stretch of the loop has).</summary>
    public enum Role { Other, Cells, Resources, Immune }

    [Serializable]
    public class Pick
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;
    }

    [Serializable]
    public class Layer
    {
        public string name = "Layer";
        [Tooltip("With a Vessel: which region setting scales it (Cells / Resources / Immune).")]
        public Role role;
        public List<Pick> prefabs = new List<Pick>();
        [Tooltip("How many per sector (min, max); a fraction rounds up by chance. Scaled by the region's density.")]
        public Vector2 perSector = new Vector2(2f, 6f);
        [Range(0f, 1f), Tooltip("How much region density scales the count: 1 = none in voids, 0 = the same everywhere.")]
        public float followDensity = 1f;
        [Tooltip("Multiplier: Size times the prefab's own scale. Metres: Size is the uniform scale. " +
                 "Resource chunks ignore this and use their prefab's size range.")]
        public Sizing sizing = Sizing.Multiplier;
        public Vector2 size = new Vector2(0.5f, 2.5f);
        [Min(1f), Tooltip("Above 1, small ones are commoner.")]
        public float sizeSkew = 1.8f;
        public bool randomRotation = true;
        [Tooltip("Rigidbody mass times size cubed.")]
        public bool massWithSize = true;
        [Min(0f), Tooltip("Least gap to anything else (metres).")]
        public float spacing = 5f;
        [Tooltip("Later layers can place things just off these (cells).")]
        public bool anchor;
        [Range(0f, 1f), Tooltip("Share placed hovering off an anchor (an earlier layer's, same sector).")]
        public float nearAnchors;
        [Tooltip("Height above the anchor's surface (metres).")]
        public Vector2 anchorHeight = new Vector2(3f, 16f);
    }

    [Header("World")]
    [Tooltip("0: a new world every play (the seed used is saved with the session).")]
    public int seed;
    public List<Layer> layers = new List<Layer>();
    [Tooltip("Prefabs that aren't generated but may be saved / streamed (by name). Layers' prefabs are included already.")]
    public List<GameObject> catalog = new List<GameObject>();

    [Header("Sectors")]
    [Min(20f)] public float sectorSize = 200f;
    [Min(10f), Tooltip("Sectors whose nearest point is within this of the player load.")]
    public float loadDistance = 350f;
    [Min(10f), Tooltip("And unload past this (keep it above Load Distance).")]
    public float unloadDistance = 450f;
    [Min(0.02f)] public float scanInterval = 0.25f;
    [Min(0f), Tooltip("Streamed things dissolve over this many metres before the load edge (StreamFade.hlsl), so " +
                      "nothing pops in or out in view. 0 = off.")]
    public float fadeLength = 120f;
    [Min(0f), Tooltip("Gone this far inside Load Distance: slack for sector-distance rounding and spawns placed " +
                      "a little outside their sector.")]
    public float fadeMargin = 15f;

    [Header("Regions")]
    [Min(0.5f), Tooltip("Size of dense patches and voids, in sectors.")]
    public float regionSectors = 3f;
    [Range(0f, 0.9f), Tooltip("Region noise below this is a void.")]
    public float voidBelow = 0.3f;
    [Range(0f, 1f), Tooltip("Least density round the start.")]
    public float homeDensity = 0.8f;

    [Header("Budget")]
    [Min(0.1f), Tooltip("Milliseconds a frame spent spawning (always at least one).")]
    public float spawnBudgetMs = 2f;
    [Min(1)] public int sweepPerFrame = 128;
    [Min(1)] public int maxUnloadsPerFrame = 24;
    [Min(0.1f), Tooltip("Least seconds between PathManager rescans after the world changed.")]
    public float rescanInterval = 2f;
    [Tooltip("What blocks a spawn spot when generating (the player, things already there).")]
    public LayerMask blockingLayers = ~0;

    // ---------------- records ----------------

    [Serializable]
    public class EntityRecord
    {
        public string key;
        public int seed;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public Vector3 velocity, spin;
        public float mass;
        public List<string> states;
        [Tooltip("Vessel clock when this pose was taken: a stored record has drifted since.")]
        public double time;
        // The vessel's frame angle (Vessel.FrameAngle) at that time: the world frame turns to follow the blood
        // round the player, so drift while stored = band rate x time minus how far the frame turned.
        public double frame;
        [NonSerialized] internal Template template; // looked up by key once
    }

    [Serializable]
    public class SectorRecord
    {
        public long id;
        public bool generated;
        [Tooltip("Loaded at least once, or something was stored into it. Generated but never touched (only seen in the " +
                 "far field) = pristine: dropped when it leaves the far field and left out of saves (regenerated from the seed).")]
        public bool touched;
        public List<EntityRecord> entities = new List<EntityRecord>();
    }

    [Serializable]
    public class WorldSave
    {
        public int seed;
        public Vector3 home;
        [Tooltip("How sector ids were made (cubes, or the vessel's cells): a save with another layout is re-sorted by position.")]
        public string layout;
        public Vessel.Save vessel;
        public List<SectorRecord> sectors = new List<SectorRecord>();
    }

    // ---------------- statics ----------------

    static WorldStreamer s_instance;
    static readonly List<WorldEntity> s_live = new List<WorldEntity>();

    public static WorldStreamer Instance => s_instance;
    /// <summary>A streamer is running the world: the old one-shot spawners (SpawnManager, ResourceField,
    /// WhiteBloodCells) leave spawning to it.</summary>
    public static bool Active => s_instance && s_instance.enabled;
    /// <summary>Where streamed objects live (identity transform at the origin).</summary>
    public static Transform Root => s_instance ? s_instance._root : null;
    public static IReadOnlyList<WorldEntity> Live => s_live;

    internal static void Track(WorldEntity e)
    {
        if (e.index >= 0 && e.index < s_live.Count && s_live[e.index] == e) return;
        e.index = s_live.Count;
        s_live.Add(e);
    }

    internal static void Untrack(WorldEntity e)
    {
        int i = e.index;
        e.index = -1;
        if (i < 0 || i >= s_live.Count || s_live[i] != e) return;
        int last = s_live.Count - 1;
        WorldEntity moved = s_live[last];
        s_live[i] = moved;
        if (moved) moved.index = i;
        s_live.RemoveAt(last);
    }

    // A game scene (it has the player) with no streamer gets the one in Resources/ViralBuildAssets.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<WorldStreamer>(FindObjectsInactive.Include)) return;
        if (!FindAnyObjectByType<VirusMovement>()) return;
        ViralBuildAssets assets = ViralBuildAssets.Instance;
        if (assets) ViralBuildAssets.Spawn<WorldStreamer>(assets.worldStreamer, "World Streamer");
    }

    // ---------------- runtime ----------------

    internal enum Kind { Generic, Chunk, WhiteCell }

    internal sealed class Template
    {
        public GameObject prefab;
        public string key;
        public Kind kind;
        public float unitRadius; // bounding radius at scale 1
        public float mass;       // the prefab's Rigidbody mass (0 none)
        public ResourceChunk chunk;
        public int farLook;      // index in FarPrefabs
    }

    sealed class LayerPicks
    {
        public Template[] templates;
        public float[] weights;
        public float total;
    }

    sealed class Pending
    {
        public long key;
        public List<EntityRecord> list;
        public int next;
        public float distance;
    }

    readonly Dictionary<string, Template> _catalog = new Dictionary<string, Template>();
    readonly Dictionary<long, SectorRecord> _records = new Dictionary<long, SectorRecord>();
    readonly HashSet<long> _loaded = new HashSet<long>();
    readonly List<long> _unload = new List<long>();
    readonly List<Pending> _pending = new List<Pending>();
    readonly List<long> _ungenerated = new List<long>(); // loaded, not generated yet (budgeted like spawning)
    readonly List<Vector4> _placed = new List<Vector4>(), _anchors = new List<Vector4>();
    readonly HashSet<string> _missing = new HashSet<string>();
    readonly Stopwatch _clock = new Stopwatch();
    readonly List<long> _near = new List<long>(), _nearGen = new List<long>();
    readonly HashSet<long> _nearSeen = new HashSet<long>();
    LayerPicks[] _picks;
    readonly List<GameObject> _farPrefabs = new List<GameObject>();

    // The far field (FarField on this object): sectors within its distance that aren't loaded are generated too and
    // drawn from their records. _farSet = those that are generated (what FarField draws); _farQueue = those waiting
    // to be generated, farthest first (popped from the end).
    FarField _far;
    readonly HashSet<long> _farSet = new HashSet<long>(), _farNow = new HashSet<long>(), _farQueued = new HashSet<long>();
    readonly List<long> _farNear = new List<long>(), _farQueue = new List<long>(), _farDrop = new List<long>();
    readonly Dictionary<long, float> _farDistance = new Dictionary<long, float>();
    float _nextFarScan;
    long _lastFarSector = long.MinValue;
    bool FarOn => _far && _far.On;
    Transform _root, _player;
    Vector3 _home;
    int _seed, _cursor;
    bool _ready, _pathsDirty;
    float _nextScan, _nextPaths, _nextFind;
    long _lastSector = long.MinValue;

    // With a Vessel on this object the world is its loop, cut into cells that drift with the blood: radial band b
    // (width sectorSize) x slice k along the loop *in that band's own turning frame* x slice j round the tube. Every
    // band turns rigidly (Vessel.AngularRate at its middle), so a stored record never changes cell while it
    // drifts, and bringing it back is one rotation by rate x time away.
    Vessel _vessel;
    int _nS, _nR;
    float _dPhi;
    float[] _bandRate;
    int[] _nT;

    public int Seed => _seed;
    public int LoadedSectors => _loaded.Count;
    public int PendingSpawns { get { int n = 0; foreach (Pending p in _pending) n += p.list.Count - p.next; return n; } }

    void OnEnable()
    {
        if (s_instance && s_instance != this)
        {
            Debug.LogWarning("WorldStreamer: a second one in the scene; this one is off.", this);
            enabled = false;
            return;
        }
        s_instance = this;
        if (_ready) return;
        _ready = true;

        _root = new GameObject("Streamed World").transform; // at the origin, never moved: records are world space
        BuildCatalog();
        _seed = seed != 0 ? seed : Environment.TickCount & 0x7fffffff;
        _home = Centre();
        SetupVessel();
        EnsureManagers();
        _far = GetComponent<FarField>();
        if (!_far) _far = gameObject.AddComponent<FarField>();
        Prime();
        // The immune system looks for cells when it starts: make sure there is one now that there are cells.
        if (!FindAnyObjectByType<ImmuneSystem>(FindObjectsInactive.Include)) new GameObject("Immune System").AddComponent<ImmuneSystem>();
    }

    void OnDisable()
    {
        if (s_instance == this) s_instance = null;
        Shader.SetGlobalVector(StreamFadeId, Vector4.zero); // off: nothing fades without a streamer
    }

    static readonly int StreamFadeId = Shader.PropertyToID("_StreamFade"), StreamFadeEndId = Shader.PropertyToID("_StreamFadeEnd");

    // Everything streamed is gone (fully dissolved) by the time its centre is fadeMargin inside loadDistance: a
    // sector loads when its nearest point comes within loadDistance, so anything not loaded yet is past that.
    void PublishFade(Vector3 centre)
    {
        float end = Mathf.Max(1f, loadDistance - fadeMargin), length = Mathf.Min(fadeLength, end);
        Shader.SetGlobalVector(StreamFadeId, new Vector4(centre.x, centre.y, centre.z, length > 0f ? 1f / length : 0f));
        Shader.SetGlobalFloat(StreamFadeEndId, end);
    }

    void OnDestroy()
    {
        if (_root) Destroy(_root.gameObject);
    }

    void Update()
    {
        Vector3 centre = Centre();
        PublishFade(centre);
        long sector = KeyOf(centre);
        if (Time.unscaledTime >= _nextScan || sector != _lastSector)
        {
            _nextScan = Time.unscaledTime + scanInterval;
            _lastSector = sector;
            Scan(centre);
        }
        if (FarOn && (Time.unscaledTime >= _nextFarScan || sector != _lastFarSector))
        {
            _nextFarScan = Time.unscaledTime + _far.scanInterval;
            _lastFarSector = sector;
            using (FarScanMarker.Auto()) FarScan(centre);
        }
        using (SweepMarker.Auto()) Sweep();
        _clock.Restart();
        GeneratePending(centre, spawnBudgetMs);
        SpawnPending(centre, spawnBudgetMs);
        if (FarOn)
        {
            _clock.Restart();
            GenerateFar(_far.generateBudgetMs);
        }
        if (_pathsDirty && Time.unscaledTime >= _nextPaths && PathManager.I)
        {
            _pathsDirty = false;
            _nextPaths = Time.unscaledTime + rescanInterval;
            PathManager.I.Rescan(); // streamed cells are obstacles for flying AI
        }
    }

    Vector3 Centre()
    {
        if (!_player && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            VirusMovement v = FindAnyObjectByType<VirusMovement>();
            if (v) _player = v.transform;
        }
        if (_player) return _player.position;
        return Camera.main ? Camera.main.transform.position : transform.position;
    }

    /// <summary>Loads everything within loadDistance right now (start, and after loading a save).</summary>
    public void Prime()
    {
        Vector3 centre = Centre();
        _lastSector = KeyOf(centre);
        Scan(centre);
        _clock.Restart();
        GeneratePending(centre, float.PositiveInfinity);
        SpawnPending(centre, float.PositiveInfinity);
        _pathsDirty = true;
        _nextPaths = 0f;
        _nextFarScan = 0f; // the far field fills in over the next frames, nearest first
    }

    // ---------------- sectors ----------------

    static readonly ProfilerMarker ScanMarker = new ProfilerMarker("WorldStreamer.Scan"),
                                   SweepMarker = new ProfilerMarker("WorldStreamer.Sweep"),
                                   GenerateMarker = new ProfilerMarker("WorldStreamer.Generate"),
                                   SpawnMarker = new ProfilerMarker("WorldStreamer.Spawn"),
                                   FarScanMarker = new ProfilerMarker("WorldStreamer.FarScan"),
                                   FarGenerateMarker = new ProfilerMarker("WorldStreamer.FarGenerate");

    void Scan(Vector3 centre)
    {
        using var _ = ScanMarker.Auto();
        Near(centre, loadDistance, _near);
        foreach (long key in _near)
            if (!_loaded.Contains(key)) Load(key);

        float unload = Mathf.Max(unloadDistance, loadDistance + sectorSize * 0.25f);
        _unload.Clear();
        foreach (long key in _loaded)
            if (Distance(key, centre) > unload) _unload.Add(key);
        foreach (long key in _unload) Unload(key);
    }

    void Load(long key)
    {
        _loaded.Add(key);
        SectorRecord record = Record(key);
        record.touched = true;
        if (_farSet.Remove(key)) _far.Changed(key, false); // its records go live: the stand-ins go
        if (_farQueued.Remove(key)) _farQueue.Remove(key);
        if (!record.generated) { _ungenerated.Add(key); return; } // GeneratePending, within the frame's budget
        Queue(key, record);
    }

    // A loaded sector's records go on the spawn queue.
    void Queue(long key, SectorRecord record)
    {
        if (record.entities.Count == 0) return;
        _pending.Add(new Pending { key = key, list = record.entities });
        record.entities = new List<EntityRecord>();
    }

    // Its live objects go on the sweep; whatever hadn't spawned yet goes straight back into its record.
    void Unload(long key)
    {
        _loaded.Remove(key);
        _ungenerated.Remove(key); // generated when it next loads
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            Pending p = _pending[i];
            if (p.key != key) continue;
            List<EntityRecord> into = Record(key).entities;
            for (int j = p.next; j < p.list.Count; j++) into.Add(p.list[j]);
            _pending.RemoveAt(i);
        }
        // Back to the far field right away (the sweep stashes its live objects there as it finds them); the far scan
        // drops it later if it's out of range.
        if (FarOn && Record(key).generated && _farSet.Add(key)) _far.Changed(key, false);
    }

    SectorRecord Record(long key)
    {
        if (!_records.TryGetValue(key, out SectorRecord r)) _records[key] = r = new SectorRecord { id = key };
        return r;
    }

    // Anything standing in an unloaded sector (and not held) is written into that sector's record and removed.
    void Sweep()
    {
        int checks = Mathf.Min(sweepPerFrame, s_live.Count), removed = 0;
        for (int n = 0; n < checks && removed < maxUnloadsPerFrame && s_live.Count > 0; n++)
        {
            if (_cursor >= s_live.Count) _cursor = 0;
            WorldEntity e = s_live[_cursor];
            if (!e) { s_live[_cursor] = s_live[s_live.Count - 1]; if (s_live[_cursor]) s_live[_cursor].index = _cursor; s_live.RemoveAt(s_live.Count - 1); continue; }
            long key = KeyOf(e.T.position);
            if (_loaded.Contains(key) || e.Pinned) { _cursor++; continue; }
            SectorRecord into = Record(key);
            into.entities.Add(Stamp(e.Capture()));
            into.touched = true;
            if (FarOn)
            {
                // Drawn from its record from now on: a sector nothing had generated yet is generated now (round it),
                // so the object doesn't drop out of view until the far scan gets there.
                if (!into.generated)
                {
                    into.generated = true;
                    using (FarGenerateMarker.Auto()) Generate(key, into.entities);
                    if (_farQueued.Remove(key)) _farQueue.Remove(key);
                }
                _farSet.Add(key);
                _far.Changed(key, false);
            }
            Remove(e); // the last one moves into this index: look at it next
            removed++;
        }
    }

    static void Remove(WorldEntity e)
    {
        GameObject go = e.gameObject;
        go.SetActive(false); // out of every registry and physics now; destroyed at the end of the frame
        Destroy(go);
        if (s_instance) s_instance._pathsDirty = true;
    }

    // ---------------- far field ----------------

    // Sectors within the far field's distance that aren't loaded: generated ones are drawn (FarField), the rest are
    // queued for generation, nearest first. Pristine sectors (generated, never loaded or stored into) that left the
    // range are dropped: they come back from the seed, so memory is bounded by the far field, not by where you've been.
    void FarScan(Vector3 centre)
    {
        Near(centre, _far.distance, _farNear);
        _farNow.Clear();
        foreach (long key in _farNear)
        {
            if (_loaded.Contains(key)) continue;
            _farNow.Add(key);
            if (_records.TryGetValue(key, out SectorRecord r) && r.generated) { if (_farSet.Add(key)) _far.Changed(key, false); }
            else if (_farQueued.Add(key)) _farQueue.Add(key);
        }

        _farDrop.Clear();
        foreach (long key in _farSet) if (!_farNow.Contains(key)) _farDrop.Add(key);
        foreach (long key in _farDrop)
        {
            _farSet.Remove(key);
            _far.Changed(key, false);
            if (_records.TryGetValue(key, out SectorRecord r) && !r.touched) _records.Remove(key);
        }
        for (int i = _farQueue.Count - 1; i >= 0; i--)
            if (!_farNow.Contains(_farQueue[i])) { _farQueued.Remove(_farQueue[i]); _farQueue.RemoveAt(i); }

        _farDistance.Clear();
        Vessel.Tube tube = _vessel ? _vessel.ToTube(centre) : default;
        foreach (long key in _farQueue) _farDistance[key] = _vessel ? Distance(key, centre, tube) : Distance(key, centre);
        _farQueue.Sort(_byFarDistance ??= (a, b) => _farDistance[b].CompareTo(_farDistance[a])); // farthest first
    }

    Comparison<long> _byFarDistance;

    // Far sectors' contents, nearest first (the queue's end), within the budget; at least one a frame.
    void GenerateFar(float budgetMs)
    {
        bool first = true;
        while (_farQueue.Count > 0 && (first || _clock.Elapsed.TotalMilliseconds < budgetMs))
        {
            first = false;
            long key = _farQueue[_farQueue.Count - 1];
            _farQueue.RemoveAt(_farQueue.Count - 1);
            _farQueued.Remove(key);
            if (_loaded.Contains(key)) continue;
            SectorRecord record = Record(key);
            bool fresh = !record.generated;
            if (fresh)
            {
                record.generated = true;
                using (FarGenerateMarker.Auto()) Generate(key, record.entities);
            }
            _farSet.Add(key);
            _far.Changed(key, fresh);
        }
    }

    // What FarField reads.
    internal bool Ready => _ready;
    internal IEnumerable<long> FarSectors => _farSet;
    internal bool IsFar(long key) => _farSet.Contains(key);
    internal List<EntityRecord> RecordsOf(long key) => _records.TryGetValue(key, out SectorRecord r) ? r.entities : null;
    /// <summary>Every prefab a record can name, in far-look order (Template.farLook).</summary>
    internal IReadOnlyList<GameObject> FarPrefabs => _farPrefabs;
    internal int FarLookOf(string key) => key != null && _catalog.TryGetValue(key, out Template t) ? t.farLook : -1;

    internal int FarLookOf(EntityRecord r)
    {
        if (r == null || r.key == null) return -1;
        if (r.template == null && !_catalog.TryGetValue(r.key, out r.template)) return -1;
        return r.template.farLook;
    }

    /// <summary>How fast a stored record at p turns round the loop (its band's rate, as Advance), 0 without a vessel.</summary>
    internal float DriftRate(Vector3 p) => _vessel ? _bandRate[Band(_vessel.ToTube(p).r)] : 0f;

    /// <summary>The streaming centre and the distance where real objects start dissolving (StreamFade).</summary>
    internal void FadeBand(out Vector3 centre, out float start)
    {
        centre = Centre();
        float end = Mathf.Max(1f, loadDistance - fadeMargin);
        start = fadeLength > 0f ? end - Mathf.Min(fadeLength, end) : end;
    }

    /// <summary>Loaded sectors' records not spawned yet.</summary>
    internal void PendingRecords(List<EntityRecord> into)
    {
        foreach (Pending p in _pending)
            for (int i = p.next; i < p.list.Count; i++) into.Add(p.list[i]);
    }

    internal static float PrefabRadius(GameObject prefab) => BoundingRadius(prefab);

    // ---------------- spawning ----------------

    // New sectors' contents, nearest first, while the frame's budget (_clock, restarted by the caller) lasts; at
    // least one a frame. Generating every sector that came into range at once was a 60-90 ms hitch.
    void GeneratePending(Vector3 centre, float budgetMs)
    {
        bool first = true;
        while (_ungenerated.Count > 0 && (first || _clock.Elapsed.TotalMilliseconds < budgetMs))
        {
            first = false;
            int best = 0;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < _ungenerated.Count; i++)
            {
                float d = Distance(_ungenerated[i], centre);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            long key = _ungenerated[best];
            _ungenerated[best] = _ungenerated[_ungenerated.Count - 1];
            _ungenerated.RemoveAt(_ungenerated.Count - 1);

            SectorRecord record = Record(key);
            record.generated = true;
            using (GenerateMarker.Auto()) Generate(key, record.entities);
            Queue(key, record);
        }
    }

    // Loaded sectors' records, nearest sector first, in what's left of the frame's budget; at least one a frame.
    void SpawnPending(Vector3 centre, float budgetMs)
    {
        if (_pending.Count == 0) return;
        foreach (Pending p in _pending) p.distance = Distance(p.key, centre);
        _pending.Sort(ByDistance);

        bool first = true;
        while (_pending.Count > 0 && (first || _clock.Elapsed.TotalMilliseconds < budgetMs))
        {
            first = false;
            Pending p = _pending[0];
            using (SpawnMarker.Auto()) Spawn(p.list[p.next++]);
            if (p.next >= p.list.Count) _pending.RemoveAt(0);
        }
    }

    static readonly Comparison<Pending> ByDistance = (a, b) => a.distance.CompareTo(b.distance);

    void Spawn(EntityRecord r)
    {
        if (r == null || string.IsNullOrEmpty(r.key)) return;
        if (!_catalog.TryGetValue(r.key, out Template t))
        {
            if (_missing.Add(r.key)) Debug.LogWarning($"WorldStreamer: no prefab named '{r.key}' in the layers or catalog; those aren't spawned.", this);
            return;
        }
        Advance(r); // where it has drifted to while stored
        // Seeded, so anything the prefab randomizes on Awake (a chunk's tint and bob) comes back the same.
        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState(r.seed);
        GameObject go = Instantiate(t.prefab, r.position, r.rotation, _root);
        UnityEngine.Random.state = state;

        go.name = t.prefab.name;
        go.transform.localScale = r.scale;
        if (t.kind == Kind.WhiteCell) WhiteBloodCells.Prepare(go.GetComponent<WhiteBloodCell>());
        WorldEntity e = go.GetComponent<WorldEntity>();
        if (!e) e = go.AddComponent<WorldEntity>();
        e.key = r.key;
        e.seed = r.seed;
        e.Bind();
        e.Apply(r);
        _pathsDirty = true;
    }

    // ---------------- generation ----------------

    void Generate(long key, List<EntityRecord> into)
    {
        if (_picks == null) return;
        var rng = new Rng(((ulong)(uint)_seed << 32) ^ ((ulong)key * 0x9E3779B97F4A7C15UL));
        float density = Density(key);
        float volume = VolumeScale(key);
        Vector3 middle = CellCentre(key);
        _placed.Clear();
        _anchors.Clear();
        Occupied(into, 0); // anything stored here before it was generated (swept in)
        // Big things straddle sector borders: keep clear of what the neighbours hold but haven't spawned (live
        // objects are found by the physics check).
        Near(middle, sectorSize * 1.5f, _nearGen);
        foreach (long k in _nearGen)
            if (k != key && _records.TryGetValue(k, out SectorRecord near)) Occupied(near.entities, 0);
        foreach (Pending p in _pending)
            if (p.key != key && _nearGen.Contains(p.key)) Occupied(p.list, p.next);

        for (int li = 0; li < layers.Count && li < _picks.Length; li++)
        {
            Layer layer = layers[li];
            LayerPicks picks = _picks[li];
            if (layer == null || picks == null || picks.total <= 0f) continue;
            float want = rng.Range(Mathf.Min(layer.perSector.x, layer.perSector.y), Mathf.Max(layer.perSector.x, layer.perSector.y))
                         * Mathf.Lerp(1f, density, layer.followDensity) * volume
                         * (_vessel ? _vessel.Density(middle, layer.role, 0f) : 1f);
            int count = (int)want + (rng.Value < want - (int)want ? 1 : 0);

            for (int n = 0; n < count; n++)
            {
                Template t = picks.templates[picks.templates.Length - 1];
                float w = rng.Value * picks.total;
                for (int i = 0; i < picks.templates.Length; i++)
                    if ((w -= picks.weights[i]) <= 0f) { t = picks.templates[i]; break; }

                Vector3 scale;
                float radius, room, size = 1f;
                List<string> states = null;
                if (t.kind == Kind.Chunk)
                {
                    ResourceChunk ch = t.chunk;
                    float lo = Mathf.Min(ch.sizeRange.x, ch.sizeRange.y), hi = Mathf.Max(ch.sizeRange.x, ch.sizeRange.y);
                    float r = ch.radius > 0f ? ch.radius : Mathf.Lerp(lo, hi, Mathf.Pow(rng.Value, ch.sizeSkew));
                    scale = Vector3.one * r;
                    radius = r;
                    states = new List<string> { r.ToString("R", CultureInfo.InvariantCulture) };
                }
                else
                {
                    size = Mathf.Lerp(layer.size.x, Mathf.Max(layer.size.x, layer.size.y), Mathf.Pow(rng.Value, layer.sizeSkew));
                    scale = layer.sizing == Sizing.Multiplier ? t.prefab.transform.localScale * size : Vector3.one * size;
                    radius = t.unitRadius * MaxAbs(scale);
                }
                room = Room(t, scale);

                if (!FindSpot(ref rng, layer, key, room, out Vector3 p)) continue;
                _placed.Add(new Vector4(p.x, p.y, p.z, room));
                if (layer.anchor) _anchors.Add(new Vector4(p.x, p.y, p.z, radius));

                float cube = t.kind == Kind.Chunk ? 1f : (layer.sizing == Sizing.Multiplier ? size : size / Mathf.Max(1e-4f, MaxAbs(t.prefab.transform.localScale)));
                into.Add(new EntityRecord
                {
                    key = t.key,
                    seed = (int)(rng.Next() & 0x7fffffff),
                    position = p,
                    rotation = layer.randomRotation ? rng.Rotation() : t.prefab.transform.rotation,
                    scale = scale,
                    mass = layer.massWithSize && t.mass > 0f && t.kind == Kind.Generic ? t.mass * cube * cube * cube : 0f,
                    states = states,
                    time = Now,
                    // The frame's angle too: left at 0, Advance turned it by the whole frame turn so far, and once the
                    // player had been out by the wall (where the frame turns fast) new sectors' contents landed far
                    // round the loop, so the way back looked empty.
                    frame = FrameNow,
                });
            }
        }
    }

    // Space a record takes: its bounding radius (a chunk's lumps and the bob it sweeps included).
    float Room(Template t, Vector3 scale) => t.kind == Kind.Chunk ? scale.x * 1.2f + t.chunk.bob.y : t.unitRadius * MaxAbs(scale);

    void Occupied(List<EntityRecord> records, int from)
    {
        for (int i = from; i < records.Count; i++)
        {
            EntityRecord r = records[i];
            if (r == null || r.key == null || !_catalog.TryGetValue(r.key, out Template t)) continue;
            Advance(r);
            _placed.Add(new Vector4(r.position.x, r.position.y, r.position.z, Room(t, r.scale)));
        }
    }

    // Centre inside the sector (so it belongs there), clear of what's placed here and in the neighbours' records,
    // and of anything already in the world (the player, streamed neighbours, drifters).
    bool FindSpot(ref Rng rng, Layer layer, long key, float room, out Vector3 p)
    {
        for (int tries = 0; tries < 24; tries++)
        {
            if (layer.nearAnchors > 0f && _anchors.Count > 0 && rng.Value < layer.nearAnchors)
            {
                Vector4 anchor = _anchors[rng.Range(0, _anchors.Count - 1)];
                float h = rng.Range(Mathf.Min(layer.anchorHeight.x, layer.anchorHeight.y), Mathf.Max(layer.anchorHeight.x, layer.anchorHeight.y));
                p = (Vector3)anchor + rng.OnSphere() * (anchor.w + room + h);
                if (KeyOf(p) != key) continue;
            }
            else p = SamplePoint(ref rng, key);
            if (_vessel && _vessel.Density(p, Role.Other, room) <= 0f) continue; // in the tube, clear of its wall

            bool free = true;
            foreach (Vector4 o in _placed)
            {
                float gap = room + o.w + layer.spacing;
                if (((Vector3)o - p).sqrMagnitude < gap * gap) { free = false; break; }
            }
            if (free && !Physics.CheckSphere(p, room + layer.spacing * 0.5f, blockingLayers, QueryTriggerInteraction.Ignore)) return true;
        }
        p = default;
        return false;
    }

    // Smooth noise over sectors: patches (1) and voids (0); the start region is always at least homeDensity.
    // With a vessel the noise is sampled where the cell was at clock 0, so it's fixed to the drifting cell.
    float Density(long key)
    {
        Vector3 p = (_vessel ? CellCentre(key, 0.0, 0.0) / sectorSize : (Vector3)Unpack(key)) / regionSectors;
        float n = 0.65f * ValueNoise(p, _seed) + 0.35f * ValueNoise(p * 2.3f + new Vector3(17.1f, 3.7f, 9.2f), _seed + 101);
        float d = Mathf.Clamp01((n - voidBelow) / Mathf.Max(1e-3f, 1f - voidBelow));
        if (Distance(key, _home) <= sectorSize) d = Mathf.Max(d, homeDensity);
        return d;
    }

    static float ValueNoise(Vector3 p, int seed)
    {
        Vector3Int i = Vector3Int.FloorToInt(p);
        Vector3 f = p - i;
        f = new Vector3(f.x * f.x * (3f - 2f * f.x), f.y * f.y * (3f - 2f * f.y), f.z * f.z * (3f - 2f * f.z));
        float Corner(int x, int y, int z) => Hash(i.x + x, i.y + y, i.z + z, seed) * (1f / uint.MaxValue);
        float x00 = Mathf.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), f.x), x10 = Mathf.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), f.x);
        float x01 = Mathf.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), f.x), x11 = Mathf.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), f.x);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, f.y), Mathf.Lerp(x01, x11, f.y), f.z);
    }

    static uint Hash(int x, int y, int z, int seed)
    {
        uint h = (uint)seed * 0x27d4eb2du;
        h = (h ^ (uint)x) * 0x85ebca6bu;
        h = (h ^ (h >> 13) ^ (uint)y) * 0xc2b2ae35u;
        h = (h ^ (h >> 16) ^ (uint)z) * 0x27d4eb2du;
        h ^= h >> 15;
        h *= 0x2c1b3c6du;
        h ^= h >> 12;
        return h;
    }

    // ---------------- catalog ----------------

    void BuildCatalog()
    {
        _catalog.Clear();
        _farPrefabs.Clear();
        _picks = new LayerPicks[layers.Count];
        for (int li = 0; li < layers.Count; li++)
        {
            Layer layer = layers[li];
            if (layer == null) continue;
            var ts = new List<Template>();
            var ws = new List<float>();
            foreach (Pick pick in layer.prefabs)
            {
                if (pick == null || !pick.prefab || pick.weight <= 0f) continue;
                Template t = TemplateOf(pick.prefab);
                if (t == null) continue;
                ts.Add(t);
                ws.Add(pick.weight);
            }
            float total = 0f;
            foreach (float w in ws) total += w;
            _picks[li] = new LayerPicks { templates = ts.ToArray(), weights = ws.ToArray(), total = total };
        }
        foreach (GameObject prefab in catalog) if (prefab) TemplateOf(prefab);
    }

    Template TemplateOf(GameObject prefab)
    {
        string key = prefab.name;
        if (_catalog.TryGetValue(key, out Template known))
        {
            if (known.prefab != prefab) Debug.LogWarning($"WorldStreamer: two prefabs named '{key}'; saves find them by name, so rename one.", prefab);
            return known;
        }
        var t = new Template { prefab = prefab, key = key };
        t.chunk = prefab.GetComponent<ResourceChunk>();
        t.kind = t.chunk ? Kind.Chunk : prefab.GetComponent<WhiteBloodCell>() ? Kind.WhiteCell : Kind.Generic;
        t.unitRadius = BoundingRadius(prefab) / Mathf.Max(1e-4f, MaxAbs(prefab.transform.localScale));
        t.mass = prefab.TryGetComponent(out Rigidbody body) ? body.mass : 0f;
        t.farLook = _farPrefabs.Count;
        _farPrefabs.Add(prefab);
        _catalog[key] = t;
        return t;
    }

    // What a streamed world needs running: the chunk field (draws chunks) and the white cells' manager.
    void EnsureManagers()
    {
        bool chunks = false, white = false;
        foreach (Template t in _catalog.Values) { chunks |= t.kind == Kind.Chunk; white |= t.kind == Kind.WhiteCell; }
        ViralBuildAssets assets = ViralBuildAssets.Instance;
        if (chunks && !FindAnyObjectByType<ResourceField>(FindObjectsInactive.Include) &&
            !(assets && ViralBuildAssets.Spawn<ResourceField>(assets.resourceField, "Resource Field")))
            new GameObject("Resource Field").AddComponent<ResourceField>();
        if (white && !FindAnyObjectByType<WhiteBloodCells>(FindObjectsInactive.Include) &&
            !(assets && ViralBuildAssets.Spawn<WhiteBloodCells>(assets.whiteBloodCells, "White Blood Cells")))
            Debug.LogWarning("WorldStreamer: no WhiteBloodCells prefab in Resources/ViralBuildAssets; white cells won't be drawn.", this);
    }

    // Radius round the prefab's origin its meshes and colliders reach, at its own scale.
    static float BoundingRadius(GameObject root)
    {
        float radius = 0f;
        Vector3 rootScale = root.transform.localScale;
        Matrix4x4 toRoot = Matrix4x4.Scale(rootScale) * root.transform.worldToLocalMatrix;
        float Reach(Matrix4x4 m, Vector3 centre, Vector3 extents) =>
            m.MultiplyPoint3x4(centre).magnitude + Mathf.Max(m.MultiplyVector(new Vector3(extents.x, 0f, 0f)).magnitude,
                                                             m.MultiplyVector(new Vector3(0f, extents.y, 0f)).magnitude,
                                                             m.MultiplyVector(new Vector3(0f, 0f, extents.z)).magnitude);
        foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            if (f.sharedMesh) radius = Mathf.Max(radius, Reach(toRoot * f.transform.localToWorldMatrix, f.sharedMesh.bounds.center, f.sharedMesh.bounds.extents));
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            Matrix4x4 m = toRoot * c.transform.localToWorldMatrix;
            switch (c)
            {
                case SphereCollider s: radius = Mathf.Max(radius, Reach(m, s.center, Vector3.one * s.radius)); break;
                case CapsuleCollider k: radius = Mathf.Max(radius, Reach(m, k.center, Vector3.one * Mathf.Max(k.radius, k.height * 0.5f))); break;
                case BoxCollider b: radius = Mathf.Max(radius, Reach(m, b.center, b.size * 0.5f)); break;
                case MeshCollider mc when mc.sharedMesh: radius = Mathf.Max(radius, Reach(m, mc.sharedMesh.bounds.center, mc.sharedMesh.bounds.extents)); break;
            }
        }
        return radius > 0f ? radius : 1f;
    }

    static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // ---------------- saving ----------------

    /// <summary>The whole world as it is now: every sector's record, with the live objects written into the
    /// sector they stand in.</summary>
    public WorldSave Capture()
    {
        var save = new WorldSave { seed = _seed, home = _home, layout = Layout(), vessel = _vessel ? _vessel.Capture() : null };
        var byId = new Dictionary<long, SectorRecord>();
        SectorRecord Get(long id)
        {
            if (byId.TryGetValue(id, out SectorRecord s)) return s;
            // A pristine sector isn't saved (it regenerates from the seed): one holding only a live object that
            // drifted in is saved ungenerated, so its contents are generated again round that object.
            s = new SectorRecord { id = id, generated = _records.TryGetValue(id, out SectorRecord r) && r.generated && r.touched, touched = true };
            byId[id] = s;
            save.sectors.Add(s);
            return s;
        }
        foreach (KeyValuePair<long, SectorRecord> kv in _records)
            if (kv.Value.touched || !kv.Value.generated) Get(kv.Key).entities.AddRange(kv.Value.entities);
        foreach (Pending p in _pending)
            for (int i = p.next; i < p.list.Count; i++) Get(p.key).entities.Add(p.list[i]);
        foreach (WorldEntity e in s_live)
            if (e) Get(KeyOf(e.T.position)).entities.Add(Stamp(e.Capture()));
        return save;
    }

    /// <summary>Throws the streamed world away and puts a saved one in its place, loaded round where the
    /// player is now (move the player first).</summary>
    public void Restore(WorldSave save)
    {
        foreach (WorldEntity e in s_live.ToArray()) if (e) Remove(e);
        s_live.Clear();
        _pending.Clear();
        _ungenerated.Clear();
        _loaded.Clear();
        _records.Clear();
        _farSet.Clear();
        _farQueue.Clear();
        _farQueued.Clear();
        if (_far) _far.Clear();
        _cursor = 0;
        if (save != null)
        {
            _seed = save.seed;
            _home = save.home;
        }
        SetupVessel(save?.vessel);
        if (save != null)
        {
            // Another layout (an older save, or the vessel switched on / off): ids mean nothing here, so every
            // record is sorted into the cell it stands in, as it stood when saved; those cells generate afresh.
            bool same = save.layout == Layout();
            bool timed = !string.IsNullOrEmpty(save.layout) && save.layout.StartsWith("vessel");
            foreach (SectorRecord s in save.sectors)
            {
                if (s == null) continue;
                s.entities ??= new List<EntityRecord>();
                s.touched = true;
                if (same) { _records[s.id] = s; continue; }
                foreach (EntityRecord r in s.entities)
                {
                    if (r == null) continue;
                    if (!timed) Stamp(r); // saved without drift: it stands where it was saved
                    else Advance(r);      // turned to where it has drifted by now
                    SectorRecord into = Record(KeyOf(r.position));
                    into.entities.Add(r);
                    into.touched = true;
                }
            }
        }
        Prime();
    }

    // ---------------- sector maths ----------------
    // Two layouts behind one set of calls: cubes in open space, or the vessel's drifting cells (see _vessel).

    const float Tau = 2f * Mathf.PI;

    /// <summary>The vessel's clock (0 without one): record times and cell drift are measured by it.</summary>
    static double Now => Vessel.Clock;

    /// <summary>How far the world frame has turned (Vessel.FrameAngle, radians) by now.</summary>
    static double FrameNow => Vessel.FrameAngle;

    // Where band b's own turning frame stands (world loop angle) at a time, given the world frame's angle then.
    double BandAngle(int b, double time, double frame) => _bandRate[b] * time - frame;

    void SetupVessel(Vessel.Save save = null)
    {
        _vessel = GetComponent<Vessel>();
        if (_vessel && !_vessel.enabled) _vessel = null;
        if (!_vessel) return;
        _vessel.transform.position = _home; // the loop's centreline runs through the start, downstream along forward
        _vessel.Restore(save, _seed);
        _nS = Mathf.Max(3, Mathf.RoundToInt(_vessel.circumference / sectorSize));
        _dPhi = Tau / _nS;
        _nR = Mathf.Clamp(Mathf.CeilToInt(_vessel.MaxRadius / sectorSize), 1, 1 << 12);
        _bandRate = new float[_nR];
        _nT = new int[_nR];
        for (int b = 0; b < _nR; b++)
        {
            float r = (b + 0.5f) * sectorSize;
            _bandRate[b] = _vessel.AngularRate(r);
            _nT[b] = Mathf.Max(1, Mathf.RoundToInt(Tau * r / sectorSize));
        }
    }

    string Layout() => _vessel
        ? string.Format(CultureInfo.InvariantCulture, "vessel {0} {1} {2:R} {3:R}", _nS, _nR, sectorSize, _vessel.circumference)
        : string.Format(CultureInfo.InvariantCulture, "cubes {0:R}", sectorSize);

    static EntityRecord Stamp(EntityRecord r)
    {
        r.time = Now;
        r.frame = FrameNow;
        return r;
    }

    int Band(float r) => Mathf.Clamp((int)(r / sectorSize), 0, _nR - 1);

    /// <summary>A stored record drifts rigidly with its band: turn it round the loop for the time it was away.</summary>
    void Advance(EntityRecord r)
    {
        if (!_vessel) return;
        double now = Now;
        double frame = FrameNow;
        if (r.time == now && r.frame == frame) return;
        float rate = _bandRate[Band(_vessel.ToTube(r.position).r)];
        float angle = (float)((rate * (now - r.time) - (frame - r.frame)) % Tau);
        _vessel.Turn(ref r.position, ref r.rotation, ref r.velocity, angle);
        r.spin = Quaternion.AngleAxis(angle * Mathf.Rad2Deg, _vessel.Axis) * r.spin;
        r.time = now;
        r.frame = frame;
    }

    /// <summary>The cell (sector) a point is in now.</summary>
    long KeyOf(Vector3 p)
    {
        if (!_vessel) return Pack(Vector3Int.FloorToInt(p / sectorSize));
        Vessel.Tube t = _vessel.ToTube(p);
        int b = Band(t.r);
        double frame = t.phi - BandAngle(b, Now, FrameNow);
        int k = (int)Wrap((long)Math.Floor(frame / _dPhi), _nS);
        int n = _nT[b];
        int j = Mathf.Clamp((int)(Mathf.Repeat(t.theta, Tau) / (Tau / n)), 0, n - 1);
        return Pack(new Vector3Int(b, k, j));
    }

    // Where a vessel cell starts at a clock time (with the world frame's angle then): world loop angle, radius,
    // angle round the tube and its span.
    void Cell(long key, double time, double frameAngle, out float phi0, out float r0, out float th0, out float dTh)
    {
        Vector3Int c = Unpack(key);
        int b = Mathf.Clamp(c.x, 0, _nR - 1);
        phi0 = (float)Repeat(c.y * (double)_dPhi + BandAngle(b, time, frameAngle), Tau);
        r0 = b * sectorSize;
        dTh = Tau / _nT[b];
        th0 = c.z * dTh;
    }

    Vector3 CellCentre(long key) => CellCentre(key, Now, FrameNow);

    Vector3 CellCentre(long key, double time, double frameAngle)
    {
        if (!_vessel) return ((Vector3)Unpack(key) + Vector3.one * 0.5f) * sectorSize;
        Cell(key, time, frameAngle, out float phi0, out float r0, out float th0, out float dTh);
        return _vessel.FromTube(phi0 + _dPhi * 0.5f, r0 + sectorSize * 0.5f, th0 + dTh * 0.5f);
    }

    Vector3 SamplePoint(ref Rng rng, long key)
    {
        if (!_vessel)
        {
            Vector3 min = (Vector3)Unpack(key) * sectorSize;
            return min + new Vector3(rng.Value, rng.Value, rng.Value) * sectorSize;
        }
        Cell(key, Now, FrameNow, out float phi0, out float r0, out float th0, out float dTh);
        float r1 = r0 + sectorSize;
        float r = Mathf.Sqrt(Mathf.Lerp(r0 * r0, r1 * r1, rng.Value)); // even over the ring's area
        return _vessel.FromTube(phi0 + rng.Value * _dPhi, r, th0 + rng.Value * dTh);
    }

    /// <summary>A cell's volume over a cube sector's, so counts per sector mean the same density everywhere.</summary>
    float VolumeScale(long key)
    {
        if (!_vessel) return 1f;
        Cell(key, Now, FrameNow, out _, out float r0, out _, out float dTh);
        float r1 = r0 + sectorSize;
        return _dPhi * _vessel.LoopRadius * 0.5f * (r1 * r1 - r0 * r0) * dTh / (sectorSize * sectorSize * sectorSize);
    }

    /// <summary>Distance from p to the nearest point of a cell (near enough: clamped in tube coordinates).</summary>
    float Distance(long key, Vector3 p)
    {
        if (!_vessel)
        {
            Vector3 min = (Vector3)Unpack(key) * sectorSize, max = min + Vector3.one * sectorSize;
            return (p - Vector3.Max(min, Vector3.Min(max, p))).magnitude;
        }
        return Distance(key, p, _vessel.ToTube(p));
    }

    // The same with p's tube coordinates found already (Near and the far scan test thousands of cells from one point).
    float Distance(long key, Vector3 p, in Vessel.Tube t)
    {
        Cell(key, Now, FrameNow, out float phi0, out float r0, out float th0, out float dTh);
        float phi = ClampAngle(t.phi, phi0, _dPhi);
        float r = Mathf.Clamp(t.r, r0, r0 + sectorSize);
        float th = ClampAngle(t.theta, th0, dTh);
        return (p - _vessel.FromTube(phi, r, th)).magnitude;
    }

    // An angle clamped into [start, start + span], round the circle's nearer way.
    static float ClampAngle(float a, float start, float span)
    {
        float into = Mathf.Repeat(a - start, Tau);
        if (into <= span) return start + into;
        return into - span < Tau - into ? start + span : start;
    }

    /// <summary>Every cell whose nearest point is within dist of p.</summary>
    void Near(Vector3 p, float dist, List<long> into)
    {
        into.Clear();
        if (!_vessel)
        {
            Vector3Int c = Vector3Int.FloorToInt(p / sectorSize);
            int reach = Mathf.CeilToInt(dist / sectorSize);
            for (int x = -reach; x <= reach; x++)
            for (int y = -reach; y <= reach; y++)
            for (int z = -reach; z <= reach; z++)
            {
                long key = Pack(new Vector3Int(c.x + x, c.y + y, c.z + z));
                if (Distance(key, p) <= dist) into.Add(key);
            }
            return;
        }

        Vessel.Tube t = _vessel.ToTube(p);
        double now = Now;
        int b0 = Band(t.r - dist), b1 = Band(t.r + dist);
        float along = dist / Mathf.Max(_vessel.LoopRadius - _vessel.MaxRadius, 1f); // loop angle dist spans (inner side)
        _nearSeen.Clear();
        for (int b = b0; b <= b1; b++)
        {
            double frame = t.phi - BandAngle(b, now, FrameNow);
            long k0 = (long)Math.Floor((frame - along) / _dPhi), k1 = (long)Math.Floor((frame + along) / _dPhi);
            if (k1 - k0 + 1 >= _nS) { k0 = 0; k1 = _nS - 1; }
            int n = _nT[b];
            float dTh = Tau / n, span = dist / Mathf.Max(Mathf.Min(b * sectorSize, t.r), 1f);
            long j0 = 0, j1 = n - 1;
            if (span < Mathf.PI)
            {
                j0 = (long)Math.Floor((t.theta - span) / dTh);
                j1 = (long)Math.Floor((t.theta + span) / dTh);
                if (j1 - j0 + 1 >= n) { j0 = 0; j1 = n - 1; }
            }
            for (long k = k0; k <= k1; k++)
            for (long j = j0; j <= j1; j++)
            {
                long key = Pack(new Vector3Int(b, (int)Wrap(k, _nS), (int)Wrap(j, n)));
                if (_nearSeen.Add(key) && Distance(key, p, t) <= dist) into.Add(key);
            }
        }
    }

    static long Wrap(long i, long n) => ((i % n) + n) % n;
    static double Repeat(double x, double length) => x - Math.Floor(x / length) * length;

    const int Bits = 21;
    const long Mask = (1L << Bits) - 1;

    static long Pack(Vector3Int c) => ((c.x & Mask) << (2 * Bits)) | ((c.y & Mask) << Bits) | (c.z & Mask);

    static Vector3Int Unpack(long k)
    {
        int Part(int shift) => (int)((k >> shift) & Mask) << (32 - Bits) >> (32 - Bits); // sign-extend
        return new Vector3Int(Part(2 * Bits), Part(Bits), Part(0));
    }

    // SplitMix64: small, fast, the same everywhere (UnityEngine.Random is global state).
    struct Rng
    {
        ulong _s;
        public Rng(ulong seed) { _s = seed; Next(); }

        public ulong Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public float Value => (Next() >> 40) * (1f / (1 << 24));
        public float Range(float a, float b) => a + (b - a) * Value;
        public int Range(int a, int bInclusive) => a + (int)(Next() % (ulong)(bInclusive - a + 1));

        public Vector3 OnSphere()
        {
            float z = Range(-1f, 1f), a = Value * Mathf.PI * 2f, r = Mathf.Sqrt(1f - z * z);
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        public Quaternion Rotation() // uniform (Shoemake)
        {
            float u = Value, a = Value * Mathf.PI * 2f, b = Value * Mathf.PI * 2f;
            float s = Mathf.Sqrt(1f - u), t = Mathf.Sqrt(u);
            return new Quaternion(s * Mathf.Sin(a), s * Mathf.Cos(a), t * Mathf.Sin(b), t * Mathf.Cos(b));
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.55f, 0.93f, 1f, 0.25f);
        foreach (long key in _loaded)
        {
            if (_vessel) Gizmos.DrawWireSphere(CellCentre(key), sectorSize * 0.35f); // drifting cells aren't boxes
            else Gizmos.DrawWireCube(CellCentre(key), Vector3.one * sectorSize);
        }
    }
}

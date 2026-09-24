using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// One update loop for every Organism (with its brain) and SpiderLegWalker, instead
/// of thousands of per-object Unity callbacks, plus simulation level of detail for the
/// crowd. Organisms with a Brain (AI) tick less often the farther they are from the
/// camera, and less again off screen, staggered across frames, and are handed the
/// time they skipped so their motion stays right, just coarser. Organisms without a
/// brain (the player) always tick every frame.
///
/// Same order as before: brains, then organisms (Update); organisms' late tick, then
/// leg walkers (LateUpdate); organisms' physics tick (FixedUpdate).
///
/// Also the shared per-frame camera: position and view frustum looked up once here,
/// for anything that would otherwise ask Camera.main per object (see CameraPosition,
/// OnScreen).
///
/// Profiler: each phase has its own marker (Simulation.Brains, .Organisms, .Late,
/// .Legs, .Fixed), so the cost inside the ticker shows up where it belongs.
///
/// Created on demand. Add one to the scene only to change its settings.
/// </summary>
[DefaultExecutionOrder(0)] // after VirusMovement (-10) has written the player's intent
public class SimulationTicker : MonoBehaviour
{
    [Tooltip("Camera distance within which crowd organisms tick every frame, and from which they tick every Far Interval frames.")]
    public Vector2 lodDistance = new Vector2(30f, 90f);
    [Min(1), Tooltip("Frames between ticks at the far end.")]
    public int farInterval = 4;
    [Min(1), Tooltip("Off screen, the interval is multiplied by this.")]
    public int offscreenMultiplier = 2;
    [Min(1), Tooltip("Never tick less often than this.")]
    public int maxInterval = 8;
    [Min(0f), Tooltip("Radius around an organism used to decide whether it's on screen.")]
    public float visibilityRadius = 3f;
    [Min(0.02f), Tooltip("Most time a single skipped-up tick may cover, so nothing leaps after a hitch.")]
    public float maxStep = 0.25f;

    class Entry
    {
        public Organism organism;   // set to plain null on unregister; checked with 'is null' (no native call)
        public Transform transform; // cached: .transform is a native call
        public int phase, interval = 1;
        public bool due;
        public float pending, pendingLate, pendingFixed;
    }

    static SimulationTicker s_instance;

    readonly List<Entry> _entries = new List<Entry>();
    readonly Dictionary<Organism, Entry> _byOrganism = new Dictionary<Organism, Entry>();
    readonly List<SpiderLegWalker> _walkers = new List<SpiderLegWalker>();
    bool _entriesDirty, _walkersDirty;
    int _fixedStep;

    // Shared per-frame camera.
    static readonly Plane[] s_planes = new Plane[6];
    static int s_cameraFrame = -1;
    static bool s_hasCamera;
    static Vector3 s_cameraPosition;

    static readonly ProfilerMarker s_brains = new ProfilerMarker("Simulation.Brains"),
                                   s_organisms = new ProfilerMarker("Simulation.Organisms"),
                                   s_late = new ProfilerMarker("Simulation.Late"),
                                   s_legs = new ProfilerMarker("Simulation.Legs"),
                                   s_fixed = new ProfilerMarker("Simulation.Fixed");

    /// <summary>Whether there's a main camera this frame (looked up once per frame).</summary>
    public static bool HasCamera { get { RefreshCamera(); return s_hasCamera; } }

    /// <summary>The main camera's position this frame (looked up once per frame).</summary>
    public static Vector3 CameraPosition { get { RefreshCamera(); return s_cameraPosition; } }

    /// <summary>Whether a sphere is inside the main camera's view this frame (true without a camera).</summary>
    public static bool OnScreen(Vector3 position, float radius)
    {
        RefreshCamera();
        if (!s_hasCamera) return true;
        for (int i = 0; i < 6; i++)
            if (s_planes[i].GetDistanceToPoint(position) < -radius)
                return false;
        return true;
    }

    static void RefreshCamera()
    {
        if (s_cameraFrame == Time.frameCount) return;
        s_cameraFrame = Time.frameCount;
        Camera cam = Camera.main;
        s_hasCamera = cam;
        if (!cam) return;
        s_cameraPosition = cam.transform.position;
        GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
    }

    static SimulationTicker Instance
    {
        get
        {
            if (s_instance) return s_instance;
            s_instance = FindAnyObjectByType<SimulationTicker>();
            if (!s_instance)
                s_instance = new GameObject("Simulation Ticker") { hideFlags = HideFlags.HideAndDontSave }
                    .AddComponent<SimulationTicker>();
            return s_instance;
        }
    }

    public static void Register(Organism o)
    {
        SimulationTicker t = Instance;
        if (t._byOrganism.ContainsKey(o)) return;
        var e = new Entry { organism = o, transform = o.transform, phase = Random.Range(0, 1 << 16) };
        t._entries.Add(e);
        t._byOrganism.Add(o, e);
    }

    public static void Unregister(Organism o)
    {
        if (!s_instance || !s_instance._byOrganism.Remove(o, out Entry e)) return;
        e.organism = null; // dropped at the next loop, so ticking can't trip over it
        s_instance._entriesDirty = true;
    }

    public static void Register(SpiderLegWalker w)
    {
        SimulationTicker t = Instance;
        if (!t._walkers.Contains(w)) t._walkers.Add(w);
    }

    public static void Unregister(SpiderLegWalker w)
    {
        if (!s_instance) return;
        int i = s_instance._walkers.IndexOf(w);
        if (i < 0) return;
        s_instance._walkers[i] = null;
        s_instance._walkersDirty = true;
    }

    void Update()
    {
        Compact();
        RefreshCamera();

        float dt = Time.deltaTime;
        int frame = Time.frameCount;

        // Who is due, and their brains (they write intent for the organism pass below).
        using (s_brains.Auto())
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.organism is null) continue;

                e.pending += dt;
                e.pendingLate += dt;
                IOrganismBrain brain = e.organism.Brain;
                e.interval = brain == null ? 1 : Interval(e.transform.position);
                e.due = (frame + e.phase) % e.interval == 0;
                if (e.due) brain?.BrainTick(Mathf.Min(e.pending, maxStep));
            }

        using (s_organisms.Auto())
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.organism is null || !e.due) continue;

                float step = Mathf.Min(e.pending, maxStep);
                e.pending = 0f;
                e.organism.Tick(step);
            }
    }

    void LateUpdate()
    {
        using (s_late.Auto())
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.organism is null || !e.due) continue;

                float step = Mathf.Min(e.pendingLate, maxStep);
                e.pendingLate = 0f;
                e.organism.LateTick(step);
            }

        // Unregistered walkers are plain null until compacted, so this needs no native check.
        using (s_legs.Auto())
            for (int i = 0; i < _walkers.Count; i++)
                _walkers[i]?.Tick();
    }

    void FixedUpdate()
    {
        Compact();
        _fixedStep++;
        float dt = Time.fixedDeltaTime;

        using (s_fixed.Auto())
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.organism is null) continue;

                e.pendingFixed += dt;
                if ((_fixedStep + e.phase) % e.interval != 0) continue;

                float step = Mathf.Min(e.pendingFixed, maxStep);
                e.pendingFixed = 0f;
                e.organism.FixedTick(step);
            }
    }

    // Frames between ticks for a crowd organism here.
    int Interval(Vector3 position)
    {
        if (!s_hasCamera) return 1;

        float k = Mathf.InverseLerp(lodDistance.x, lodDistance.y, Vector3.Distance(s_cameraPosition, position));
        int interval = k <= 0f ? 1 : Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, farInterval, k)));
        if (!OnScreen(position, visibilityRadius)) interval *= offscreenMultiplier;
        return Mathf.Clamp(interval, 1, maxInterval);
    }

    void Compact()
    {
        if (_entriesDirty)
        {
            _entries.RemoveAll(e => e.organism is null);
            _entriesDirty = false;
        }
        if (_walkersDirty)
        {
            _walkers.RemoveAll(w => w is null);
            _walkersDirty = false;
        }
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
    }
}

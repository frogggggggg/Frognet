using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.Profiling;

/// <summary>
/// What's inside a cell: a nucleus (or not), its organelles and their limit, and its stores (slots like the
/// virus's tubes, <see cref="Stores"/> rules). Rolled once from its <see cref="CellProfile"/> in Awake (the
/// streamer seeds UnityEngine.Random per entity, so a cell comes back the same) and kept as world state.
///
/// Metabolism: the cell takes in what its profile says from the blood (oxygen, glucose) up to a level, and each
/// organelle runs its process from the stores (mitochondria: glucose + oxygen -> ATP), limited by what's there
/// and by room for the output. One static tick in the player loop advances every cell round-robin
/// (<see cref="CellsPerFrame"/> a frame, each handed the time since its last turn); shown cells every frame.
/// Streamed out, a cell stops; loaded again it catches up on the time away (vessel clock, capped).
///
/// Resources move between cells with <see cref="Transfer"/> (for tubes); every flow in or out through the
/// membrane is kept as a <see cref="Port"/> for CellInteriorView to draw.
///
/// Cost: O(uptakes + organelle kinds) x slots per turn; O(CellsPerFrame) a frame however many cells there are.
/// </summary>
[DisallowMultipleComponent]
public class CellInterior : MonoBehaviour, IWorldState
{
    [Serializable]
    public class Organelles
    {
        public OrganelleType type;
        public int count;
        /// <summary>How hard they're working, 0..1 (eased): runs the view's streams.</summary>
        [NonSerialized] public float activity;
        [NonSerialized] public float work, wanted; // cycles done / wanted since the last ease
        /// <summary>Units of each input used / output made not yet shown (the view claims them as motes).</summary>
        [NonSerialized] public float[] used, made;
    }

    /// <summary>A flow through the membrane (uptake from the blood, a tube to another cell): where, what, how
    /// fast (units per second; negative = out). Drawn as a stream of the substance crossing the membrane.</summary>
    public struct Port
    {
        public int key;           // who it's with (an uptake's index, another cell's instance id)
        public Vector3 direction; // from the cell's centre, cell-local, unit
        public string substance;
        public float rate;        // eased units per second, + in / - out
        public float target;      // the rate being eased toward
        public float lastFlow;    // Time.time of the last flow
        public float moved;       // units through it not yet shown (the view claims them as motes)
    }

    /// <summary>A strand of DNA put into the cell (a virus's injected gene): shown in the nucleus view.</summary>
    [Serializable]
    public class Strand
    {
        public string code, name;
        public Color color;
        public bool took; // it works on this cell (its effect ran); else it sits there inert
    }

    public const int CellsPerFrame = 32;
    public const int MaxDna = 8; // strands kept (oldest dropped)
    const float MaxStep = 2f;       // metabolism sub-step (seconds)
    const float MaxCatchUp = 600f;  // time away made up on load (seconds)
    const float Ease = 0.6f;        // activity / port easing (seconds)
    const float PortLinger = 0.5f;  // a port with no flow for this long closes
    const int UpkeepKey = 1000;     // port keys of upkeep (spending), after the uptakes
    const float MaxOwed = 50f;      // unshown flow kept for the view (units), so an unwatched cell doesn't pile it up

    [Tooltip("Empty: ViralBuildAssets.defaultCellProfile.")]
    public CellProfile profile;

    [Header("State (rolled from the profile)")]
    [SerializeField] bool rolled;
    [SerializeField] bool nucleus;
    [SerializeField, Min(0)] int limit;
    [SerializeField] int seed;
    public List<Organelles> organelles = new List<Organelles>();
    public List<StoreSlot> store = new List<StoreSlot>();
    [Tooltip("Foreign DNA inside it (injected genes), oldest first.")]
    public List<Strand> dna = new List<Strand>();

    public static readonly List<CellInterior> All = new List<CellInterior>();
    static readonly Dictionary<Transform, CellInterior> s_byTransform = new Dictionary<Transform, CellInterior>();
    static readonly List<CellInterior> s_shown = new List<CellInterior>(); // advanced every frame
    static int s_next; // round-robin cursor

    readonly List<Port> _ports = new List<Port>();
    float _last;
    int _shownFrame = -1;

    public CellProfile Profile => profile ? profile : profile = ViralBuildAssets.Instance ? ViralBuildAssets.Instance.defaultCellProfile : null;
    public bool HasNucleus => nucleus;
    /// <summary>Organelles its nucleus allows (0 without one).</summary>
    public int Limit => nucleus ? limit : 0;
    public int Seed => seed;
    public float Capacity => Profile ? Profile.capacity : 100f;
    public IReadOnlyList<Port> Ports => _ports;
    public string DisplayName => Profile ? Profile.displayName : "CELL";

    public int OrganelleCount
    {
        get
        {
            int n = 0;
            foreach (Organelles o in organelles) n += o.count;
            return n;
        }
    }

    void Awake()
    {
        if (!rolled) Roll();
        _last = Time.time;
    }

    void OnEnable()
    {
        All.Add(this);
        _last = Time.time;
    }

    void OnDisable() => All.Remove(this);

    /// <summary>The cell a transform belongs to (a Surface marked as a cell, or a child of one); given a
    /// CellInterior (default profile) if it has none. Null if it isn't a cell.</summary>
    public static CellInterior For(Transform t)
    {
        if (!t) return null;
        if (s_byTransform.TryGetValue(t, out CellInterior known) && known) return known;
        Surface s = t.GetComponentInParent<Surface>();
        if (!s || !s.isCell) return null;
        if (!s.TryGetComponent(out CellInterior c)) c = s.gameObject.AddComponent<CellInterior>();
        s_byTransform[t] = c;
        return c;
    }

    // ---------------- rolling / structure ----------------

    void Roll()
    {
        rolled = true;
        seed = UnityEngine.Random.Range(1, int.MaxValue);
        CellProfile p = Profile;
        if (!p) return;
        Stores.Fit(store, p.slots);
        foreach (CellProfile.StartAmount s in p.startWith)
            if (s != null) Stores.Add(store, p.capacity, SubstanceCatalog.Find(s.substance), UnityEngine.Random.Range(s.amount.x, s.amount.y));
        bool has = p.nucleus == CellProfile.NucleusRule.Always ||
                   p.nucleus == CellProfile.NucleusRule.Chance && UnityEngine.Random.value < p.nucleusChance;
        if (has) GiveNucleus();
    }

    /// <summary>Gives it a nucleus (a rare red cell is born with one; later, something the player does): the
    /// organelle limit is rolled and it fills with its profile's starting organelles.</summary>
    [ContextMenu("Give Nucleus")]
    public void GiveNucleus()
    {
        if (nucleus) return;
        CellProfile p = Profile;
        nucleus = true;
        limit = p ? UnityEngine.Random.Range(p.organelleLimit.x, Mathf.Max(p.organelleLimit.x, p.organelleLimit.y) + 1) : 0;
        if (p)
            foreach (CellProfile.OrganelleStart o in p.organelles)
            {
                if (o == null || !o.type) continue;
                int n = UnityEngine.Random.Range(o.count.x, Mathf.Max(o.count.x, o.count.y) + 1);
                for (int i = 0; i < n && AddOrganelle(o.type); i++) { }
            }
    }

    /// <summary>A mutation (or new DNA) lets the nucleus hold 'by' more organelles.</summary>
    [ContextMenu("Raise Limit")]
    public void RaiseLimitByOne() => RaiseLimit(1);

    public bool RaiseLimit(int by)
    {
        if (!nucleus || by == 0) return false;
        limit = Mathf.Max(0, limit + by);
        return true;
    }

    /// <summary>One more of 'type', if it has a nucleus, its profile allows it and there's room under the limit.</summary>
    public bool AddOrganelle(OrganelleType type)
    {
        if (!type || !nucleus || OrganelleCount >= limit || Profile && !Profile.Allows(type)) return false;
        Organelles group = null;
        foreach (Organelles o in organelles)
            if (o.type == type) group = o;
        if (group == null) organelles.Add(group = new Organelles { type = type });
        group.count++;
        return true;
    }

    [ContextMenu("Add Organelle (first allowed kind)")]
    void AddFirstType()
    {
        CellProfile p = Profile;
        if (p && p.organelles.Length > 0) AddOrganelle(p.organelles[0].type);
    }

    public bool RemoveOrganelle(OrganelleType type)
    {
        foreach (Organelles o in organelles)
            if (o.type == type && o.count > 0)
            {
                o.count--;
                return true;
            }
        return false;
    }

    // ---------------- stores ----------------

    public float Total(string substance) => Stores.Total(store, substance);
    public float Room(string substance) => Stores.Room(store, Capacity, substance);
    public float Add(Substance s, float amount) => Stores.Add(store, Capacity, s, amount);
    public float Take(string substance, float amount) => Stores.Take(store, substance, amount);

    /// <summary>Moves up to 'amount' of a substance from one cell to another (a tube between them), as much as
    /// one has and the other can hold. Both show it crossing their membranes, facing each other. Returns how
    /// much moved. Call it every frame the flow runs (with amount = rate x dt).</summary>
    public static float Transfer(CellInterior from, CellInterior to, string substance, float amount, float dt)
    {
        if (!from || !to || from == to || amount <= 0f) return 0f;
        float moved = Mathf.Min(amount, Mathf.Min(from.Total(substance), to.Room(substance)));
        if (moved <= 0f) return 0f;
        from.Take(substance, moved);
        to.Add(SubstanceCatalog.Find(substance), moved);
        float rate = dt > 0f ? moved / dt : 0f;
        from.Flow(to.GetInstanceID(), to.transform.position, substance, -rate, moved);
        to.Flow(from.GetInstanceID(), from.transform.position, substance, rate, moved);
        return moved;
    }

    /// <summary>Reports a flow through the membrane toward 'towards' (world) at 'rate' units per second
    /// (+ in, - out) for the view; it closes by itself once reports stop. 'key' tells flows apart.</summary>
    public void Flow(int key, Vector3 towards, string substance, float rate, float amount = 0f)
    {
        Vector3 d = transform.InverseTransformDirection(towards - transform.position);
        SetPort(key, d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.up, substance, rate, amount);
    }

    /// <summary>For the view: how many whole 'unit's went through port i since it last asked (at most 'max';
    /// the rest stays owed).</summary>
    public int ClaimPort(int i, float unit, int max)
    {
        if (i < 0 || i >= _ports.Count) return 0;
        Port p = _ports[i];
        int n = Claim(ref p.moved, unit, max);
        _ports[i] = p;
        return n;
    }

    /// <summary>Whole 'unit's out of 'owed' (at most 'max'), taken off it.</summary>
    public static int Claim(ref float owed, float unit, int max)
    {
        int n = Mathf.Min(max, Mathf.FloorToInt(owed / Mathf.Max(unit, 1e-4f)));
        if (n > 0) owed -= n * unit;
        return n;
    }

    /// <summary>Drops every unshown flow (the view starts watching: what it shows is what happens from now).</summary>
    public void ForgetFlows()
    {
        foreach (Organelles o in organelles)
        {
            if (o.used != null) Array.Clear(o.used, 0, o.used.Length);
            if (o.made != null) Array.Clear(o.made, 0, o.made.Length);
        }
        for (int i = 0; i < _ports.Count; i++)
        {
            Port p = _ports[i];
            p.moved = 0f;
            _ports[i] = p;
        }
    }

    void SetPort(int key, Vector3 direction, string substance, float rate, float amount)
    {
        float now = Time.time;
        for (int i = 0; i < _ports.Count; i++)
        {
            Port p = _ports[i];
            if (p.key != key || p.substance != substance) continue;
            p.direction = direction;
            p.target = rate;
            p.lastFlow = now;
            p.moved = Mathf.Min(p.moved + amount, MaxOwed);
            _ports[i] = p;
            return;
        }
        _ports.Add(new Port { key = key, direction = direction, substance = substance, target = rate, lastFlow = now, moved = amount });
    }

    // ---------------- metabolism ----------------

    /// <summary>The view shows it this frame: it's advanced every frame, so its streams move smoothly.</summary>
    public void MarkShown()
    {
        if (_shownFrame < Time.frameCount - 1 && !s_shown.Contains(this)) s_shown.Add(this);
        _shownFrame = Time.frameCount;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void InstallTick()
    {
        s_byTransform.Clear();
        s_shown.Clear();
        s_next = 0;
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
            var list = new List<PlayerLoopSystem>(loop.subSystemList[i].subSystemList);
            list.RemoveAll(sys => sys.type == typeof(CellInterior)); // already there (no domain reload)
            list.Add(new PlayerLoopSystem { type = typeof(CellInterior), updateDelegate = TickAll });
            loop.subSystemList[i].subSystemList = list.ToArray();
            PlayerLoop.SetPlayerLoop(loop);
            return;
        }
    }

    static void TickAll()
    {
        if (!Application.isPlaying || All.Count == 0) return;
        Profiler.BeginSample("CellInterior.Tick");
        float now = Time.time;
        int frame = Time.frameCount, n = All.Count, turns = Mathf.Min(CellsPerFrame, n);
        for (int i = 0; i < turns; i++)
        {
            if (s_next >= n) s_next = 0;
            CellInterior c = All[s_next++];
            if (c && c._shownFrame < frame - 1) c.Advance(now - c._last);
        }
        for (int i = s_shown.Count - 1; i >= 0; i--)
        {
            CellInterior c = s_shown[i];
            if (c && c.isActiveAndEnabled && c._shownFrame >= frame - 1) c.Advance(now - c._last); // shown this frame or the last
            else s_shown.RemoveAt(i);
        }
        Profiler.EndSample();
    }

    void Advance(float dt)
    {
        _last = Time.time;
        if (dt <= 0f) return;
        Simulate(dt);
        float k = 1f - Mathf.Exp(-dt / Ease);
        foreach (Organelles o in organelles)
        {
            float target = o.wanted > 1e-6f ? Mathf.Clamp01(o.work / o.wanted) : 0f;
            o.activity += (target - o.activity) * k;
            o.work = o.wanted = 0f;
        }
        float now = Time.time;
        for (int i = _ports.Count - 1; i >= 0; i--)
        {
            Port p = _ports[i];
            if (now - p.lastFlow > PortLinger) p.target = 0f;
            p.rate += (p.target - p.rate) * k;
            if (p.target == 0f && Mathf.Abs(p.rate) < 1e-3f) { _ports.RemoveAt(i); continue; }
            _ports[i] = p;
        }
    }

    // Uptake, upkeep, then every organelle's process, in sub-steps of at most MaxStep.
    void Simulate(float dt)
    {
        CellProfile p = Profile;
        if (!p) return;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / MaxStep), 1, 16);
        float h = dt / steps, cap = p.capacity;
        for (int step = 0; step < steps; step++)
        {
            for (int u = 0; u < p.uptake.Length; u++)
            {
                CellProfile.Uptake up = p.uptake[u];
                if (up == null || up.rate <= 0f) continue;
                float want = Mathf.Min(up.rate * h, up.upTo - Total(up.substance));
                float got = want > 0f ? Stores.Add(store, cap, SubstanceCatalog.Find(up.substance), want) : 0f;
                UptakePort(u, up.substance, got / h, got);
            }
            for (int u = 0; p.upkeep != null && u < p.upkeep.Length; u++)
            {
                CellProfile.Upkeep up = p.upkeep[u];
                if (up == null || up.rate <= 0f) continue;
                float spent = Take(up.substance, up.rate * h);
                if (spent > 0f) UptakePort(UpkeepKey + u, up.substance, -spent / h, spent);
            }
            foreach (Organelles o in organelles)
            {
                OrganelleType type = o.type;
                if (!type || o.count <= 0) continue;
                float wanted = o.count * h / type.cycleSeconds, cycles = wanted;
                foreach (SubstanceAmount a in type.inputs)
                    if (a.amount > 0f) cycles = Mathf.Min(cycles, Total(a.substance) / a.amount);
                foreach (SubstanceAmount a in type.outputs)
                    if (a.amount > 0f) cycles = Mathf.Min(cycles, Room(a.substance) / a.amount);
                o.wanted += wanted;
                if (cycles <= 1e-6f) continue;
                if (o.used == null || o.used.Length != type.inputs.Length) o.used = new float[type.inputs.Length];
                if (o.made == null || o.made.Length != type.outputs.Length) o.made = new float[type.outputs.Length];
                for (int k = 0; k < type.inputs.Length; k++)
                {
                    SubstanceAmount a = type.inputs[k];
                    o.used[k] = Mathf.Min(o.used[k] + Take(a.substance, a.amount * cycles), MaxOwed);
                }
                for (int k = 0; k < type.outputs.Length; k++)
                {
                    SubstanceAmount a = type.outputs[k];
                    o.made[k] = Mathf.Min(o.made[k] + Stores.Add(store, cap, SubstanceCatalog.Find(a.substance), a.amount * cycles), MaxOwed);
                }
                o.work += cycles;
            }
        }
    }

    // Uptake / upkeep crosses the membrane at a spot per flow, picked from the seed.
    void UptakePort(int index, string substance, float rate, float amount)
    {
        uint h = (uint)(seed * 747796405 + index * 2891336453);
        float a = (h & 0xffff) / 65535f * Mathf.PI * 2f, z = ((h >> 16) & 0xffff) / 65535f * 0.8f - 0.4f;
        float r = Mathf.Sqrt(1f - z * z);
        SetPort(index, new Vector3(Mathf.Cos(a) * r, z, Mathf.Sin(a) * r), substance, rate, amount);
    }

    /// <summary>A gene was delivered into this cell ('took': it works on it).</summary>
    public void Receive(Genome.Gene gene, bool took)
    {
        if (gene == null) return;
        if (dna.Count >= MaxDna) dna.RemoveAt(0);
        dna.Add(new Strand { code = gene.code, name = gene.name, color = gene.color, took = took });
    }

    // ---------------- world state ----------------

    [Serializable]
    class Saved
    {
        public bool nucleus;
        public int limit, seed;
        public double clock;
        public List<string> types = new List<string>();
        public List<int> counts = new List<int>();
        public List<StoreSlot> store;
        public List<Strand> dna;
    }

    string IWorldState.SaveState()
    {
        var s = new Saved { nucleus = nucleus, limit = limit, seed = seed, clock = Vessel.Clock, store = store, dna = dna };
        foreach (Organelles o in organelles)
            if (o.type && o.count > 0) { s.types.Add(o.type.name); s.counts.Add(o.count); }
        return JsonUtility.ToJson(s);
    }

    void IWorldState.LoadState(string state)
    {
        Saved s;
        try { s = JsonUtility.FromJson<Saved>(state); }
        catch (ArgumentException) { return; }
        if (s == null) return;
        rolled = true;
        nucleus = s.nucleus;
        limit = s.limit;
        seed = s.seed;
        store = s.store ?? new List<StoreSlot>();
        dna = s.dna ?? new List<Strand>();
        if (Profile) Stores.Fit(store, Profile.slots);
        organelles.Clear();
        for (int i = 0; i < s.types.Count && i < s.counts.Count; i++)
        {
            OrganelleType type = Profile ? Profile.Organelle(s.types[i]) : null;
            if (type) organelles.Add(new Organelles { type = type, count = s.counts[i] });
        }
        _last = Time.time;
        // Time away: made up now (the clock is the vessel's, saved with the game).
        double away = Vessel.Clock - s.clock;
        if (s.clock > 0.0 && away > 0.0) Simulate((float)Math.Min(away, MaxCatchUp));
    }

    bool IWorldState.Pinned => false;
}

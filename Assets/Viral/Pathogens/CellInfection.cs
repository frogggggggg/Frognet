using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A cell a pathogen got into (RoundVirusAI sinks in and gives itself up). Lives on the cell's Surface object only
/// while it's infected; having one is what "infected" means (<see cref="On"/>).
/// - Inside: the pathogen sits in the cell for <see cref="Strain.sacrificeDelay"/>, then gives itself up.
/// - Infected: tendrils creep over the cell from the entry (Surface.Infect, in the strain's colour) and it calls the
///   body's defences (<see cref="ImmuneSystem.Alarm"/> every half second): antibodies gather, white cells come.
/// - Burst: after the incubation the cell bursts (CellBurst) and new pathogens come out (<see cref="Strain.key"/>),
///   fewer the more of them there already are (<see cref="Yield"/>: never more than the strain's population cap).
/// Times run on the vessel clock, so an infected cell streamed out keeps incubating and bursts once it's back.
/// Cost: one Update per infected cell (bounded by RoundVirusAI.maxInfections).
/// </summary>
public class CellInfection : MonoBehaviour, IWorldState
{
    /// <summary>What a pathogen does once inside a cell. Carried by the pathogen (RoundVirusAI.strain), copied into
    /// the cell when it gets in, saved with it.</summary>
    [Serializable]
    public class Strain
    {
        [Tooltip("What bursts out: the prefab's name (WorldStreamer catalog key; else found in ViralBuildAssets.pathogens).")]
        public string key = "RoundVirus";
        [Min(0f), Tooltip("Seconds inside before it gives itself up and the cell is infected.")]
        public float sacrificeDelay = 3f;
        [Tooltip("Seconds from infection to the burst (random in this range).")]
        public Vector2 incubation = new Vector2(25f, 40f);
        [Tooltip("How many come out of a burst with none of them around (random in this range); fewer as they crowd.")]
        public Vector2Int yield = new Vector2Int(2, 4);
        [Min(1), Tooltip("Live ones (+ infected cells) around the player at which bursts yield nothing more.")]
        public int maxPopulation = 24;
        [Min(0f), Tooltip("Signal per second an infected cell raises: it calls the defences.")]
        public float signal = 3f;
        [Min(0f), Tooltip("Speed (m/s) the new ones are flung out of the burst at, on top of the cell's.")]
        public float burstSpeed = 8f;
        [Tooltip("Colour of the tendrils creeping over an infected cell.")]
        public Color color = new Color(0.55f, 0.9f, 0.12f);
    }

    /// <summary>Every infected cell.</summary>
    public static readonly List<CellInfection> All = new List<CellInfection>();

    /// <summary>Clock the infection runs on: the vessel's (keeps going while streamed out, saved), else game time.</summary>
    public static double Now => Vessel.Active ? Vessel.Clock : Time.timeAsDouble;

    /// <summary>Whether 'cell' has a pathogen in it (inside or infected).</summary>
    public static bool On(Surface cell) => cell && cell.TryGetComponent(out CellInfection i) && i.enabled;

    Strain _strain;
    Surface _surface;
    Vector3 _site, _normal; // the entry, in the Surface's transform space
    double _infectAt, _burstAt;
    bool _infected;
    float _nextAlarm;

    public bool Infected => _infected;

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>A pathogen of 'strain' got into 'cell' at 'at' (world, on its surface, 'normal' out of it). Null if
    /// it isn't a cell or already has one in it.</summary>
    public static CellInfection Begin(Surface cell, Vector3 at, Vector3 normal, Strain strain)
    {
        if (!cell || !cell.isCell || On(cell) || strain == null) return null;
        var i = cell.gameObject.AddComponent<CellInfection>();
        i._surface = cell;
        i._strain = strain;
        i._site = cell.transform.InverseTransformPoint(at);
        i._normal = cell.transform.InverseTransformDirection(normal);
        i._infectAt = Now + strain.sacrificeDelay;
        i._burstAt = i._infectAt + UnityEngine.Random.Range(strain.incubation.x, strain.incubation.y);
        cell.AddImpact(at, 4f); // a small shiver as it closes over
        return i;
    }

    Vector3 Site => transform.TransformPoint(_site);
    Vector3 Normal => transform.TransformDirection(_normal).normalized;

    void Update()
    {
        if (_strain == null) { Destroy(this); return; } // a bad load
        double now = Now;
        if (!_infected)
        {
            if (now >= _infectAt) Infect(now);
            return;
        }

        if (Time.time >= _nextAlarm)
        {
            _nextAlarm = Time.time + 0.5f;
            ImmuneSystem.Alarm(transform, Site, Normal, _strain.signal * 0.5f);
        }
        if (now >= _burstAt) Burst();
    }

    void Infect(double now)
    {
        _infected = true;
        if (!_surface) TryGetComponent(out _surface);
        // The tendrils' front reaches round the cell's far side about when it bursts.
        if (_surface && !_surface.Infected)
            _surface.Infect(Site, Normal, _strain.color, Mathf.Max(1f, (float)(_burstAt - now) * 0.4f));
        ImmuneSystem.Alarm(transform, Site, Normal, _strain.signal * 3f);
    }

    void Burst()
    {
        enabled = false; // once
        Vector3 site = Site;
        Bounds b = _surface && _surface.Renderer ? _surface.Renderer.bounds : new Bounds(transform.position, Vector3.one * 2f);
        float radius = Mathf.Min(b.extents.x, Mathf.Min(b.extents.y, b.extents.z));
        Vector3 velocity = TryGetComponent(out Rigidbody rb) && !rb.isKinematic ? rb.linearVelocity : Vessel.FlowAt(b.center);
        int count = Yield(_strain);
        ImmuneSystem.Lysed(transform, site, Normal); // before it goes: the alarm comes from the cell
        if (CellBurst.Kill(transform, site)) Release(_strain, count, b.center, radius, (site - b.center).normalized, velocity);
        Destroy(this);
    }

    /// <summary>How many come out of a burst now: the strain's yield, thinned as the population nears its cap (none
    /// at it) and never past it. So an outbreak levels off instead of growing exponentially.</summary>
    public static int Yield(Strain strain)
    {
        int live = RoundVirusAI.All.Count + All.Count; // other infected cells will burst too (a bursting one has left All)
        int room = Mathf.Max(0, strain.maxPopulation - live);
        float crowd = Mathf.Clamp01((float)live / strain.maxPopulation);
        int n = UnityEngine.Random.Range(strain.yield.x, strain.yield.y + 1);
        n = Mathf.RoundToInt(n * (1f - crowd * crowd)); // barely thinned while few, nothing near the cap
        return Mathf.Clamp(n, 0, room);
    }

    // The new ones come out as the membrane tears (CellBurst's swell + half the break), from the middle outward,
    // mostly out of the side they went in.
    static async void Release(Strain strain, int count, Vector3 centre, float radius, Vector3 entry, Vector3 velocity)
    {
        if (count <= 0) return;
        const float delay = CellBurst.Swell + CellBurst.Break * 0.5f;
        await Awaitable.WaitForSecondsAsync(delay);
        centre += velocity * delay; // the cell drifted on meanwhile
        for (int k = 0; k < count; k++)
        {
            Vector3 dir = (UnityEngine.Random.onUnitSphere + entry * 0.6f).normalized;
            RoundVirusAI.Spawn(strain.key, centre + dir * (radius * 0.8f), velocity + dir * strain.burstSpeed * UnityEngine.Random.Range(0.7f, 1.2f));
        }
    }

    // ---------------- saving ----------------

    [Serializable]
    struct Saved { public Strain strain; public Vector3 site, normal; public double infectAt, burstAt; public bool infected; }

    string IWorldState.SaveState() => _strain == null || !enabled ? "" : JsonUtility.ToJson(new Saved
    {
        strain = _strain, site = _site, normal = _normal, infectAt = _infectAt, burstAt = _burstAt, infected = _infected,
    });

    // The tendrils come back with the Surface (its own state); a cell whose time ran out while away bursts at once.
    void IWorldState.LoadState(string state)
    {
        Saved s = JsonUtility.FromJson<Saved>(state);
        TryGetComponent(out _surface);
        _strain = s.strain;
        _site = s.site;
        _normal = s.normal;
        _infectAt = s.infectAt;
        _burstAt = Math.Max(s.burstAt, Now + 1.0); // seen burst, not mid-spawn
        _infected = s.infected;
    }

    bool IWorldState.Pinned => false;
}

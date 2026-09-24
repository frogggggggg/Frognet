using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A cell's alarm: how loudly it's calling the immune system. Viral activity on it raises the
/// signal (ImmuneSystem: a virus on it a little, a virus in focus on it -- drilling in -- a lot) and
/// it dies away on its own. Loud cells pull antibodies toward them (<see cref="Pull"/>, a simple
/// gravity field) and give off fumes.
///
/// A cell stops signalling for good once it's yours: inject the gene it answers to
/// (<see cref="Inject"/>, from the head view's injection). The wrong gene sets it off instead.
/// Added to Surfaces on demand (<see cref="For"/>); add one yourself to set its gene or limits.
/// </summary>
public class CellSignal : MonoBehaviour, ICompletable
{
    [Tooltip("Gene code that brings this cell under your control. Empty: ImmuneSystem's default.")]
    public string controlGene = "";
    [Min(1f), Tooltip("Loudest it gets.")]
    public float maxSignal = 100f;

    /// <summary>Current strength, 0..maxSignal.</summary>
    public float Signal { get; private set; }
    /// <summary>Taken over (the right gene injected): silent from now on.</summary>
    public bool Converted { get; private set; }
    bool ICompletable.Complete => Converted; // a task on this cell is done once it is yours
    /// <summary>Where the activity was last, on the cell (world).</summary>
    public Vector3 Hotspot => transform.TransformPoint(_hotspot);

    public static readonly List<CellSignal> All = new List<CellSignal>();
    static readonly Dictionary<Transform, CellSignal> s_byTransform = new Dictionary<Transform, CellSignal>();

    Renderer _renderer;
    Vector3 _hotspot;
    float _emit; // fumes owed (ImmuneSystem)

    void OnEnable() => All.Add(this);
    void OnDisable() => All.Remove(this);

    /// <summary>The cell a transform belongs to (a Surface, its renderer or a child of either).
    /// Given a CellSignal if it has none. Null if it isn't a cell.</summary>
    public static CellSignal For(Transform t)
    {
        if (!t) return null;
        if (s_byTransform.TryGetValue(t, out CellSignal known) && known) return known;
        Surface s = t.GetComponentInParent<Surface>();
        if (!s || !s.isCell) return null;
        CellSignal c = s.GetComponent<CellSignal>();
        if (!c) c = s.gameObject.AddComponent<CellSignal>();
        s_byTransform[t] = c;
        return c;
    }

    /// <summary>More activity: 'amount' more signal, around 'where' (world).</summary>
    public void Raise(float amount, Vector3 where)
    {
        if (Converted || amount <= 0f) return;
        Signal = Mathf.Min(maxSignal, Signal + amount);
        _hotspot = Vector3.Lerp(_hotspot, transform.InverseTransformPoint(where), _hotspot == Vector3.zero ? 1f : 0.2f);
    }

    /// <summary>Dies away (ImmuneSystem, every tick): halves every 'halfLife' seconds.</summary>
    public void Decay(float dt, float halfLife) =>
        Signal = Converted ? 0f : Signal * Mathf.Pow(0.5f, dt / Mathf.Max(halfLife, 0.01f));

    /// <summary>A gene delivered into it: the right one takes it over, any other sets off the alarm.</summary>
    public void Inject(Genome.Gene gene, string defaultGene, float wrongBurst)
    {
        if (gene == null || Converted) return;
        string wanted = string.IsNullOrEmpty(controlGene) ? defaultGene : controlGene;
        if (gene.code == wanted)
        {
            Converted = true;
            Signal = 0f;
        }
        else Raise(wrongBurst, Hotspot);
    }

    /// <summary>Its bounding sphere (world): centre and the radius inside its flattest side, so
    /// something kept outside it doesn't hover far off a cube's faces.</summary>
    public Vector3 Centre(out float radius)
    {
        if (!_renderer) _renderer = GetComponentInChildren<Renderer>();
        if (!_renderer)
        {
            radius = 1f;
            return transform.position;
        }
        Bounds b = _renderer.bounds;
        radius = Mathf.Min(b.extents.x, Mathf.Min(b.extents.y, b.extents.z));
        return b.center;
    }

    /// <summary>Owed fumes (ImmuneSystem): adds 'rate * dt', returns how many whole puffs to emit.</summary>
    public int Emit(float rate, float dt)
    {
        _emit += rate * dt;
        int n = Mathf.FloorToInt(_emit);
        _emit -= n;
        return n;
    }

    /// <summary>The signals as a gravity field: toward every signalling cell, by its strength, fading
    /// with distance past 'falloff'. Unnormalised: its length says how strongly.</summary>
    public static Vector3 Pull(Vector3 at, float falloff, float threshold)
    {
        Vector3 pull = Vector3.zero;
        foreach (CellSignal c in All)
        {
            if (c.Signal < threshold) continue;
            Vector3 to = c.Hotspot - at;
            float d = to.magnitude;
            if (d < 1e-3f) continue;
            float f = d / falloff;
            pull += to / d * (c.Signal / (1f + f * f));
        }
        return pull;
    }
}

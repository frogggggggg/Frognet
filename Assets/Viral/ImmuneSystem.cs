using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The body's defence. You don't fight directly, so this is what you evade and build against:
/// - Watches viral activity: every virus (Organism) on a cell raises that cell's CellSignal (a little
///   while there, more while moving, a lot while in focus on it -- drilling in). Signals die away.
/// - Signalling cells give off fumes (drawn here: one draw of GPU billboards, Hidden/SignalFume).
/// - Antibodies: an ambient population wanders the area; loud cells call more in from afar. They
///   follow the signal field to the loud cells, patrol over the activity and stick to viruses they
///   see (Antibody). They're command-mode targets ("Antibodies").
/// - A gene delivered into a cell (the head view's injection, <see cref="Deliver"/>) takes the cell
///   over if it's the one it answers to (<see cref="controlGene"/>): it stops signalling for good.
///   Any other gene sets its alarm off.
///
/// Creates itself on play when the scene has cells (add one to the scene to tune it; disable it to
/// switch the immune system off).
/// </summary>
public class ImmuneSystem : MonoBehaviour
{
    [Header("Signal")]
    [Min(0f), Tooltip("Signal per second from each virus sitting on a cell.")]
    public float idleRate = 0.5f;
    [Min(0f), Tooltip("Signal per second from each virus moving on a cell.")]
    public float walkRate = 1.5f;
    [Min(0f), Tooltip("Signal per second from a virus in focus on a cell (drilling in).")]
    public float focusRate = 8f;
    [Min(0.1f), Tooltip("Seconds for a quiet cell's signal to halve.")]
    public float halfLife = 15f;
    [Min(0f), Tooltip("Cells quieter than this don't call anything.")]
    public float callThreshold = 4f;
    [Tooltip("Gene code that takes a cell over (unless the cell's CellSignal names its own).")]
    public string controlGene = "INT-5";
    [Min(0f), Tooltip("Signal from injecting the wrong gene into a cell.")]
    public float wrongGeneBurst = 40f;

    [Header("Antibodies")]
    [Min(0)] public int ambientCount = 24;
    [Min(0)] public int maxAntibodies = 90;
    [Min(0f), Tooltip("Antibodies called in per second per unit of signal, from each loud cell.")]
    public float reinforceRate = 0.004f;
    [Min(0f), Tooltip("How far out from the cell they're called to they arrive from.")]
    public float arriveDistance = 45f;
    [Min(0.1f)] public float antibodySize = 1.1f;
    [Min(0f)] public float speed = 4f;
    [Min(1f), Tooltip("Speed multiplier going for a virus.")]
    public float chaseBoost = 1.8f;
    [Min(0.1f)] public float acceleration = 6f;
    [Min(1f), Tooltip("The signal field's pull fades past this distance from a cell.")]
    public float pullFalloff = 40f;
    [Min(0.01f), Tooltip("Field strength that makes an antibody head for it flat out.")]
    public float pullFull = 8f;
    [Min(1f), Tooltip("Within this of a loud cell's activity they stop and patrol it.")]
    public float patrolRange = 14f;
    [Min(0.5f), Tooltip("Height they circle at over the activity.")]
    public float patrolHeight = 4f;
    [Min(1f), Tooltip("How far a patrolling antibody sees a virus (a drifting one: a third of this).")]
    public float sightRange = 10f;
    [Range(0f, 1f), Tooltip("Chance it goes for a virus it sees (else it ignores that one for a while).")]
    public float stickChance = 0.6f;
    [Min(0f)] public float ignoreTime = 4f;
    [Min(0f), Tooltip("How close it gets before it grabs on.")]
    public float stickDistance = 0.6f;
    [Min(0f), Tooltip("Distance kept from cells' surfaces.")]
    public float cellMargin = 1.5f;
    [Min(0.02f), Tooltip("Seconds between looks round for viruses.")]
    public float lookInterval = 0.25f;
    [Tooltip("Empty: a pale URP Lit material.")]
    public Material antibodyMaterial;

    [Header("Fumes")]
    [Min(0f), Tooltip("Puffs per second per unit of signal.")]
    public float fumeRate = 0.35f;
    [Range(64, 8192)] public int maxPuffs = 2048;
    public Vector2 puffLife = new Vector2(3f, 5.5f);
    public Vector2 puffSize = new Vector2(0.6f, 3.2f);
    [Min(0f), Tooltip("Rise speed off the cell.")]
    public float riseSpeed = 0.9f;
    public Color fumeColor = new Color(0.78f, 0.95f, 0.32f, 0.35f);
    public Color fumeCore = new Color(1f, 0.92f, 0.55f, 0.5f);
    [Tooltip("Hidden/SignalFume. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader fumeShader;

    readonly List<Antibody> _antibodies = new List<Antibody>();
    Mesh _antibodyMesh;
    Material _ownMaterial;
    float _nextActivity, _lastActivity;
    readonly Dictionary<CellSignal, float> _owed = new Dictionary<CellSignal, float>();

    struct Puff
    {
        public Vector3 position, velocity;
        public float age, life, seed;
    }
    Puff[] _puffs;
    int _puffCount, _puffNext;
    Vector4[] _puffA, _puffB;
    GraphicsBuffer _bufferA, _bufferB;
    Material _fumeMat;
    MaterialPropertyBlock _props;

    static readonly int PuffAId = Shader.PropertyToID("_PuffA"), PuffBId = Shader.PropertyToID("_PuffB"),
                        ColorId = Shader.PropertyToID("_FumeColor"), CoreId = Shader.PropertyToID("_FumeCore");

    static ImmuneSystem s_instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<ImmuneSystem>(FindObjectsInactive.Include)) return; // the scene has its own
        if (!FindAnyObjectByType<Surface>()) return; // no cells, nothing to defend
        new GameObject("Immune System").AddComponent<ImmuneSystem>();
    }

    /// <summary>A gene delivered into a cell (the head view's injection): the right one takes it
    /// over, any other sets it off.</summary>
    public static void Deliver(Transform cell, Genome.Gene gene)
    {
        CellSignal c = CellSignal.For(cell);
        if (!c || gene == null) return;
        ImmuneSystem s = s_instance;
        c.Inject(gene, s ? s.controlGene : "INT-5", s ? s.wrongGeneBurst : 40f);
    }

    void OnEnable() => s_instance = this;

    void Start()
    {
        foreach (Surface s in FindObjectsByType<Surface>(FindObjectsSortMode.None)) CellSignal.For(s.transform);
        for (int i = 0; i < ambientCount; i++) Spawn(AmbientPoint());
    }

    void OnDestroy()
    {
        _bufferA?.Release();
        _bufferB?.Release();
        if (_antibodyMesh) Destroy(_antibodyMesh);
        if (_ownMaterial) Destroy(_ownMaterial);
        if (_fumeMat) Destroy(_fumeMat);
    }

    void Update()
    {
        float dt = Time.deltaTime, now = Time.time;

        // Activity -> signals, ten times a second.
        if (now >= _nextActivity)
        {
            float step = _lastActivity > 0f ? now - _lastActivity : 0.1f;
            _lastActivity = now;
            _nextActivity = now + 0.1f;
            Activity(step);
            foreach (CellSignal c in CellSignal.All) c.Decay(step, halfLife);
            Reinforce(step);
        }

        _antibodies.RemoveAll(a => !a);
        foreach (Antibody a in _antibodies) a.Tick(this, dt, now);

        Fumes(dt);
    }

    void Activity(float dt)
    {
        foreach (Organism o in Organism.All)
        {
            if (!o.OnSurface) continue;
            CellSignal c = CellSignal.For(o.Surface);
            if (!c) continue;
            float rate = o.InState(o.grounded.focus) ? focusRate : o.Move.sqrMagnitude > 0.01f ? walkRate : idleRate;
            c.Raise(rate * dt, o.transform.position);
        }
    }

    // Loud cells call antibodies in from afar.
    void Reinforce(float dt)
    {
        foreach (CellSignal c in CellSignal.All)
        {
            if (c.Signal < callThreshold || _antibodies.Count >= maxAntibodies) continue;
            _owed.TryGetValue(c, out float owed);
            owed += c.Signal * reinforceRate * dt;
            while (owed >= 1f && _antibodies.Count < maxAntibodies)
            {
                owed -= 1f;
                Vector3 centre = c.Centre(out float r);
                Spawn(centre + Random.onUnitSphere * (r + arriveDistance));
            }
            _owed[c] = owed;
        }
    }

    // Somewhere among the cells, outside all of them.
    Vector3 AmbientPoint()
    {
        var area = new Bounds();
        bool any = false;
        foreach (CellSignal c in CellSignal.All)
        {
            Vector3 centre = c.Centre(out float r);
            var b = new Bounds(centre, Vector3.one * (r * 2f));
            if (!any) { area = b; any = true; }
            else area.Encapsulate(b);
        }
        if (!any) return transform.position + Random.insideUnitSphere * 30f;
        area.Expand(20f);
        Vector3 p = area.center;
        for (int tries = 0; tries < 20; tries++)
        {
            p = new Vector3(Random.Range(area.min.x, area.max.x), Random.Range(area.min.y, area.max.y), Random.Range(area.min.z, area.max.z));
            bool inside = false;
            foreach (CellSignal c in CellSignal.All)
            {
                Vector3 centre = c.Centre(out float r);
                if ((p - centre).sqrMagnitude < (r + cellMargin) * (r + cellMargin) * 2f) { inside = true; break; }
            }
            if (!inside) break;
        }
        return p;
    }

    void Spawn(Vector3 at)
    {
        var go = new GameObject("Antibody");
        go.transform.SetPositionAndRotation(at, Random.rotation);
        go.transform.localScale = Vector3.one * antibodySize;
        go.transform.SetParent(transform, true);
        go.AddComponent<MeshFilter>().sharedMesh = AntibodyMesh();
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = AntibodyMaterial();
        r.shadowCastingMode = ShadowCastingMode.Off;
        Selectable.Add(go, Selectable.Category.Target, "Antibody", CommandBoard.Jobs.Attack | CommandBoard.Jobs.MoveTo);
        _antibodies.Add(go.AddComponent<Antibody>());
    }

    // A Y out of three capsules: the stem down, the two arms up and out (the arms grab).
    Mesh AntibodyMesh()
    {
        if (_antibodyMesh) return _antibodyMesh;
        Mesh capsule = Resources.GetBuiltinResource<Mesh>("Capsule.fbx");
        var parts = new CombineInstance[3];
        parts[0] = new CombineInstance { mesh = capsule, transform = Matrix4x4.TRS(new Vector3(0f, -0.32f, 0f), Quaternion.identity, new Vector3(0.2f, 0.32f, 0.2f)) };
        for (int side = 0; side < 2; side++)
        {
            Quaternion tilt = Quaternion.Euler(0f, 0f, side == 0 ? 32f : -32f);
            parts[side + 1] = new CombineInstance { mesh = capsule, transform = Matrix4x4.TRS(tilt * new Vector3(0f, 0.3f, 0f), tilt, new Vector3(0.17f, 0.3f, 0.17f)) };
        }
        _antibodyMesh = new Mesh { name = "Antibody", hideFlags = HideFlags.DontSave };
        _antibodyMesh.CombineMeshes(parts, true, true);
        return _antibodyMesh;
    }

    Material AntibodyMaterial()
    {
        if (antibodyMaterial) return antibodyMaterial;
        if (_ownMaterial) return _ownMaterial;
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        _ownMaterial = new Material(lit ? lit : Shader.Find("Standard")) { name = "Antibody", hideFlags = HideFlags.DontSave };
        _ownMaterial.SetColor("_BaseColor", new Color(0.95f, 0.93f, 0.8f));
        _ownMaterial.SetFloat("_Smoothness", 0.6f);
        return _ownMaterial;
    }

    // ---------------- fumes ----------------

    // Puffs rise off each signalling cell around its activity, as many as its signal, swelling and
    // fading as they go; drawn as one batch of soft billboards.
    void Fumes(float dt)
    {
        if (_puffs == null || _puffs.Length != maxPuffs)
        {
            _puffs = new Puff[maxPuffs];
            _puffA = new Vector4[maxPuffs];
            _puffB = new Vector4[maxPuffs];
            _puffCount = _puffNext = 0;
        }

        foreach (CellSignal c in CellSignal.All)
        {
            if (c.Signal < 0.5f) continue;
            int n = c.Emit(c.Signal * fumeRate, dt);
            if (n == 0) continue;
            Vector3 centre = c.Centre(out float _);
            Vector3 spot = c.Hotspot - centre;
            float reach = spot.magnitude;
            if (reach < 1e-3f) continue;
            for (int i = 0; i < n; i++)
            {
                // Somewhere round the hotspot on the surface, rising off it.
                Vector3 dir = Quaternion.AngleAxis(Random.Range(0f, 35f), Random.onUnitSphere) * (spot / reach);
                ref Puff p = ref _puffs[_puffNext];
                p.position = centre + dir * reach;
                p.velocity = dir * riseSpeed * Random.Range(0.6f, 1.3f) + Random.insideUnitSphere * 0.25f;
                p.age = 0f;
                p.life = Random.Range(puffLife.x, puffLife.y);
                p.seed = Random.value;
                _puffNext = (_puffNext + 1) % maxPuffs; // the oldest gives way
                _puffCount = Mathf.Min(_puffCount + 1, maxPuffs);
            }
        }

        int live = 0;
        float t = Time.time;
        for (int i = 0; i < _puffCount; i++)
        {
            ref Puff p = ref _puffs[i];
            if (p.age >= p.life) continue;
            p.age += dt;
            // A lazy curl so the plume wavers.
            p.velocity += new Vector3(Mathf.Sin(t * 0.7f + p.seed * 40f), Mathf.Sin(t * 0.5f + p.seed * 17f), Mathf.Cos(t * 0.6f + p.seed * 29f)) * (0.15f * dt);
            p.velocity *= 1f - 0.25f * dt;
            p.position += p.velocity * dt;
            float k = p.age / p.life;
            float alpha = Mathf.Clamp01(k / 0.15f) * (1f - k) * (1f - k);
            _puffA[live] = new Vector4(p.position.x, p.position.y, p.position.z, Mathf.Lerp(puffSize.x, puffSize.y, Mathf.Sqrt(k)));
            _puffB[live] = new Vector4(alpha, p.seed, k, 0f);
            live++;
        }
        if (live == 0 || !Ready()) return;

        _bufferA.SetData(_puffA, 0, 0, live);
        _bufferB.SetData(_puffB, 0, 0, live);
        _props.SetBuffer(PuffAId, _bufferA);
        _props.SetBuffer(PuffBId, _bufferB);
        _props.SetColor(ColorId, fumeColor);
        _props.SetColor(CoreId, fumeCore);
        var rp = new RenderParams(_fumeMat)
        {
            matProps = _props,
            worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000f),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };
        Graphics.RenderPrimitives(rp, MeshTopology.Triangles, live * 6);
    }

    bool Ready()
    {
        _props ??= new MaterialPropertyBlock(); // plain fields: remade after a play-mode script reload
        if (_bufferA == null || _bufferA.count != maxPuffs)
        {
            _bufferA?.Release();
            _bufferB?.Release();
            _bufferA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxPuffs, 16);
            _bufferB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxPuffs, 16);
        }
        if (!_fumeMat)
        {
            Shader s = fumeShader ? fumeShader : Shader.Find("Hidden/SignalFume");
            if (!s) return false;
            _fumeMat = new Material(s) { name = "Signal Fume", hideFlags = HideFlags.DontSave };
        }
        return true;
    }
}

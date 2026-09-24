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
    [Tooltip("A Custom/Antibody material (it reads the instance buffer; others won't draw). Empty: made from antibodyShader.")]
    public Material antibodyMaterial;
    [Tooltip("Custom/Antibody. Found by name when empty (editor only: keep it assigned for builds).")]
    public Shader antibodyShader;
    [Min(1f), Tooltip("Past this from the camera antibodies draw the low-detail mesh.")]
    public float lodDistance = 30f;
    [Min(1f), Tooltip("Not drawn past this.")]
    public float drawDistance = 400f;
    [Min(1f), Tooltip("Antibodies tick every frame within this of the camera, then every 2..4 frames further out (x2 off screen). Stuck ones always tick.")]
    public float tickDistance = 40f;

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
    Mesh _nearMesh, _farMesh;
    Material _ownMaterial;

    // Every antibody in one buffer (near ones first, then far), two instanced draws.
    struct Instance { public Vector4 positionScale, rotation, wiggle; }
    const int InstanceStride = 48;
    Instance[] _near, _far;
    GraphicsBuffer _instanceBuffer;
    MaterialPropertyBlock _nearProps, _farProps;
    static readonly int AntibodiesId = Shader.PropertyToID("_Antibodies"), OffsetId = Shader.PropertyToID("_InstanceOffset");
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

    /// <summary>Antibodies stuck on 'o' are destroyed with it (a white blood cell swallowing it).</summary>
    public static void EatStuck(Organism o)
    {
        if (!s_instance || !o) return;
        foreach (Antibody a in s_instance._antibodies)
            if (a && a.Current == Antibody.State.Stuck && a.Prey == o) Destroy(a.gameObject);
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
        _instanceBuffer?.Release();
        if (_nearMesh) Destroy(_nearMesh);
        if (_farMesh) Destroy(_farMesh);
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
        TickAntibodies(now);
        DrawAntibodies();

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
        // Drawn by DrawAntibodies; the renderer (never drawn) is only there for Selectable's box.
        Meshes();
        go.AddComponent<MeshFilter>().sharedMesh = _farMesh;
        var r = go.AddComponent<MeshRenderer>();
        r.forceRenderingOff = true;
        r.shadowCastingMode = ShadowCastingMode.Off;
        Selectable.Add(go, Selectable.Category.Target, "Antibody", CommandBoard.Jobs.Attack | CommandBoard.Jobs.MoveTo);
        var a = go.AddComponent<Antibody>();
        a.LastTick = Time.time;
        _antibodies.Add(a);
    }

    void Meshes()
    {
        if (!_nearMesh) _nearMesh = AntibodyMesh.Build(1f);
        if (!_farMesh) _farMesh = AntibodyMesh.Build(0.35f);
    }

    // Simulation LOD, like SimulationTicker: far and off-screen antibodies tick every few frames, staggered,
    // with the skipped time handed back as dt (capped). Stuck ones ride a virus, so they tick every frame.
    void TickAntibodies(float now)
    {
        Vector3 cam = SimulationTicker.CameraPosition;
        int frame = Time.frameCount;
        for (int i = 0; i < _antibodies.Count; i++)
        {
            Antibody a = _antibodies[i];
            if (a.Current != Antibody.State.Stuck)
            {
                Vector3 pos = a.transform.position;
                int every = Mathf.Clamp(1 + (int)((pos - cam).magnitude / tickDistance), 1, 4);
                if (!SimulationTicker.OnScreen(pos, antibodySize)) every *= 2;
                if ((frame + i) % every != 0) continue;
            }
            float dt = Mathf.Min(now - a.LastTick, 0.25f);
            a.LastTick = now;
            a.Tick(this, dt, now);
        }
    }

    // All antibodies in two instanced draws (near: beaded mesh, far: coarse mesh), culled against the
    // view and drawDistance. Everything per antibody (pose, wiggle) goes through one buffer.
    void DrawAntibodies()
    {
        int n = _antibodies.Count;
        if (n == 0) return;
        Meshes();
        Material mat = AntibodyMaterial();
        if (!mat) return;
        if (_near == null || _near.Length < n) { _near = new Instance[Mathf.NextPowerOfTwo(n)]; _far = new Instance[_near.Length]; }
        if (_nearProps == null) { _nearProps = new MaterialPropertyBlock(); _farProps = new MaterialPropertyBlock(); } // reload wipes them

        Vector3 cam = SimulationTicker.CameraPosition;
        float lod2 = lodDistance * lodDistance, draw2 = drawDistance * drawDistance;
        int near = 0, far = 0;
        var bounds = new Bounds();
        foreach (Antibody a in _antibodies)
        {
            Transform t = a.transform;
            Vector3 pos = t.position;
            float scale = t.lossyScale.x, d2 = (pos - cam).sqrMagnitude;
            if (d2 > draw2 || !SimulationTicker.OnScreen(pos, scale)) continue;
            Quaternion q = t.rotation;
            var inst = new Instance
            {
                positionScale = new Vector4(pos.x, pos.y, pos.z, scale),
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                wiggle = a.Wiggle
            };
            if (d2 < lod2) _near[near++] = inst; else _far[far++] = inst;
            var b = new Bounds(pos, Vector3.one * scale * 2f);
            if (near + far == 1) bounds = b; else bounds.Encapsulate(b);
        }
        if (near + far == 0) return;

        if (_instanceBuffer == null || !_instanceBuffer.IsValid() || _instanceBuffer.count < n)
        {
            _instanceBuffer?.Release();
            _instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _near.Length, InstanceStride);
        }
        _instanceBuffer.SetData(_near, 0, 0, near);
        _instanceBuffer.SetData(_far, 0, near, far);

        var rp = new RenderParams(mat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true };
        if (near > 0)
        {
            _nearProps.SetBuffer(AntibodiesId, _instanceBuffer);
            _nearProps.SetInt(OffsetId, 0);
            rp.matProps = _nearProps;
            Graphics.RenderMeshPrimitives(rp, _nearMesh, 0, near);
        }
        if (far > 0)
        {
            _farProps.SetBuffer(AntibodiesId, _instanceBuffer);
            _farProps.SetInt(OffsetId, near);
            rp.matProps = _farProps;
            Graphics.RenderMeshPrimitives(rp, _farMesh, 0, far);
        }
    }

    Material AntibodyMaterial()
    {
        if (antibodyMaterial) return antibodyMaterial;
        if (_ownMaterial) return _ownMaterial;
        Shader shader = antibodyShader ? antibodyShader : Shader.Find("Custom/Antibody");
        if (!shader) return null;
        _ownMaterial = new Material(shader) { name = "Antibody", hideFlags = HideFlags.DontSave };
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

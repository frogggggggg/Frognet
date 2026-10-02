using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Something that draws its own body (not a MeshRenderer with a cell-family shader) and can burst
/// (<see cref="CellBurst"/>). WhiteBloodCell is one.</summary>
public interface IBurstable
{
    /// <summary>What it breaks into: 'mesh' (null = a unit sphere round the origin) placed by 'pose' (mesh space ->
    /// world), shaded like 'look' (a cell-family material: the pieces copy its colours, lumps and bands); 'seed' is the
    /// one its shader's BurstSwell uses, so the pieces take over its swollen shape exactly.</summary>
    void BurstShape(out Mesh mesh, out Matrix4x4 pose, out Material look, out float seed);
    /// <summary>World velocity of the body (the pieces keep drifting with it).</summary>
    Vector3 BurstVelocity { get; }
    /// <summary>Start its own swelling (CellBurst.hlsl) from 'entry' (world, on its surface) at 'start' (Time.time),
    /// vanishing at <see cref="CellBurst.Swell"/>; it stops acting. CellBurst switches its colliders off and destroys it.</summary>
    void Burst(Vector3 entry, float start);
}

/// <summary>
/// Cell death: any large cell-like body bursts from a point (a KILL gene injected there, GeneEffects).
///
/// The body's own shader (CellBurst.hlsl) swells it, blisters it and shivers for <see cref="Swell"/> s, then it
/// vanishes and an exact copy of it, broken into thick shell pieces (<see cref="CellShards"/>, Hidden/CellDebris),
/// takes its place: cracks open from the entry round to the far side (<see cref="Spread"/> later), showing the inside
/// colour on the broken edges, and the pieces are thrown off one by one over <see cref="Break"/> s, tumbling, slowed by
/// the blood, drifting with the body's velocity, then dissolve (noise-eaten, glowing edges) over ~1.4-3 s. The pieces
/// copy the body's material (colours, lumps, relief, cel bands, rim), so they are pieces of it.
///
/// Bodies: a Surface drawn by a MeshRenderer with a CELL_BURST shader (red cells: the per-renderer _Burst property),
/// or anything implementing <see cref="IBurstable"/> (white cells). At the tear everything standing on it is thrown
/// off (focus left) and its colliders go; then the object is destroyed (streamed: gone for good).
///
/// Cost: CPU O(bursts) a frame. GPU: one instanced draw per look x shape x detail of visible bursts, the whole broken
/// body per instance (fine: ~10k triangles for the red cell; coarse past <see cref="fullDetailPixels"/>: ~2.5k), every
/// piece's motion worked out from the clock in the vertex stage (no simulation, no per-piece CPU). Shapes are broken
/// once per mesh (first kill: a few ms).
/// </summary>
public class CellBurst : MonoBehaviour
{
    // Seconds (CellBurst.hlsl has the same: keep them alike).
    public const float Swell = 0.30f, Break = 0.35f, Spread = 0.20f;
    const float MaxLife = 3.0f; // a piece's longest life after it's thrown (CellDebris.shader)
    const float Gone = Swell + Break + Spread + 0.05f; // the body is destroyed (hidden since Swell)
    const float Total = Gone + MaxLife + 0.1f;

    const float DrawDistance = 900f;
    [Tooltip("Speed (m/s) creatures standing on a bursting body are thrown off at, on top of its own.")]
    public float throwOff = 4f;
    [Tooltip("Pixels across a body's radius below which its pieces draw coarse.")]
    public float fullDetailPixels = 150f;

    public static event System.Action<Vector3, float> Burst; // (centre, radius) at the kill, for sound

    // One burst for the GPU: pose rows (mesh space -> world, at the kill), centre (mesh space) + radius (mesh units),
    // entry (body radii from the centre) + start time, velocity + radius (world), info (swell seed, -, -, -).
    struct Gpu { public Vector4 m0, m1, m2, centre, entry, velocity, info; }
    const int GpuStride = 112;

    class Death
    {
        public GameObject root;
        public Surface surface;
        public float start, radius;
        public bool broken, gone;
        public int kind, detail; // detail this frame: 0 fine, 1 coarse, -1 not drawn
        public Gpu gpu;
        public Vector3 centre, velocity; // world, at the kill
    }

    // A look (the source material) on a shape: one debris material, two meshes, a draw per detail.
    class Kind
    {
        public Material source, debris;
        public int shape;
        public MaterialPropertyBlock[] props;
        public int[] first, count;
        public Bounds[] bounds;
    }

    class Shape { public Mesh[] meshes; } // fine, coarse

    List<Death> _deaths;
    List<Kind> _kinds;
    Dictionary<int, Shape> _shapeOf; // mesh instance id (0 = sphere)
    List<Shape> _shapes;
    Gpu[] _gpu;
    GraphicsBuffer _buffer;
    Shader _shader;

    static CellBurst s_instance;
    static readonly int BurstId = Shader.PropertyToID("_Burst"), BurstInfoId = Shader.PropertyToID("_BurstInfo"),
                        BurstsId = Shader.PropertyToID("_Bursts"), OffsetId = Shader.PropertyToID("_BurstOffset"),
                        PhongId = Shader.PropertyToID("_PhongStrength");
    static MaterialPropertyBlock s_rendererProps;

    static CellBurst Instance
    {
        get
        {
            if (s_instance) return s_instance;
            s_instance = FindAnyObjectByType<CellBurst>();
            if (!s_instance) s_instance = new GameObject("Cell Bursts").AddComponent<CellBurst>();
            return s_instance;
        }
    }

    // ---------------- killing ----------------

    /// <summary>Bursts the body 'body' belongs to from 'at' (world, on its surface). False if it isn't a body that
    /// can (no Surface / IBurstable, no cell-family look) or it's already bursting.</summary>
    public static bool Kill(Transform body, Vector3 at)
    {
        if (!body) return false;
        Surface surface = body.GetComponentInParent<Surface>();
        IBurstable custom = body.GetComponentInParent<IBurstable>();
        GameObject root = RootOf(body, surface, custom);
        if (!root) return false;
        CellBurst m = Instance;
        m.Prepare();
        foreach (Death d in m._deaths) if (d.root == root) return false;

        Mesh mesh;
        Matrix4x4 pose;
        Material look;
        Vector3 velocity;
        float seed;
        float start = Mathf.Max(Time.time, 1e-3f); // w 0 = whole
        MeshRenderer renderer = null;
        if (custom != null)
        {
            custom.BurstShape(out mesh, out pose, out look, out seed);
            velocity = custom.BurstVelocity;
        }
        else
        {
            renderer = surface ? surface.Renderer : null;
            MeshFilter filter = renderer ? renderer.GetComponent<MeshFilter>() : null;
            if (!filter || !filter.sharedMesh) return false;
            mesh = filter.sharedMesh;
            pose = renderer.localToWorldMatrix;
            look = renderer.sharedMaterial;
            Rigidbody rb = renderer.GetComponentInParent<Rigidbody>();
            velocity = rb ? rb.linearVelocity : Vessel.FlowAt(renderer.transform.position);
            seed = (start * 0.618034f) % 1f * 50f; // BloodCellCore's BurstSeed
        }
        int kind = m.KindOf(look, mesh);
        if (kind < 0) return false;

        Bounds b = mesh ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one * 2f);
        Vector3 c = b.center;
        float rl = Mathf.Max(Mathf.Max(b.extents.x, b.extents.y), Mathf.Max(b.extents.z, 1e-4f));
        Vector3 q = (pose.inverse.MultiplyPoint3x4(at) - c) / rl; // entry, body radii
        float scale = (((Vector3)pose.GetColumn(0)).magnitude + ((Vector3)pose.GetColumn(1)).magnitude + ((Vector3)pose.GetColumn(2)).magnitude) / 3f;
        float radius = rl * scale;

        if (custom != null) custom.Burst(at, start);
        else
        {
            s_rendererProps ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(s_rendererProps); // keeps the ripples' and tendrils' values
            s_rendererProps.SetVector(BurstId, new Vector4(q.x, q.y, q.z, start));
            s_rendererProps.SetVector(BurstInfoId, new Vector4(c.x, c.y, c.z, rl));
            renderer.SetPropertyBlock(s_rendererProps);
        }

        var death = new Death
        {
            root = root,
            surface = surface,
            start = start,
            radius = radius,
            kind = kind,
            centre = pose.MultiplyPoint3x4(c),
            velocity = velocity,
            gpu = new Gpu
            {
                m0 = pose.GetRow(0), m1 = pose.GetRow(1), m2 = pose.GetRow(2),
                centre = new Vector4(c.x, c.y, c.z, rl),
                entry = new Vector4(q.x, q.y, q.z, start),
                velocity = new Vector4(velocity.x, velocity.y, velocity.z, radius),
                info = new Vector4(seed, 0f, 0f, 0f),
            },
        };
        m._deaths.Add(death);
        Burst?.Invoke(death.centre, radius);
        return true;
    }

    /// <summary>Whether the object 'body' belongs to is bursting.</summary>
    public static bool Bursting(Transform body)
    {
        if (!s_instance || s_instance._deaths == null || !body) return false;
        GameObject root = RootOf(body, body.GetComponentInParent<Surface>(), body.GetComponentInParent<IBurstable>());
        foreach (Death d in s_instance._deaths) if (d.root && d.root == root) return true;
        return false;
    }

    // What gets destroyed: the streamed object, else the body's Rigidbody, else the Surface / IBurstable itself.
    static GameObject RootOf(Transform body, Surface surface, IBurstable custom)
    {
        WorldEntity e = body.GetComponentInParent<WorldEntity>();
        if (e) return e.gameObject;
        Rigidbody rb = body.GetComponentInParent<Rigidbody>();
        if (rb && (!surface || surface.transform.IsChildOf(rb.transform))) return rb.gameObject;
        if (custom is Component c && c) return c.gameObject;
        return surface ? surface.gameObject : null;
    }

    // ---------------- ticking ----------------

    void Prepare() // a play-mode script reload wipes plain fields
    {
        _deaths ??= new List<Death>();
        _kinds ??= new List<Kind>();
        _shapeOf ??= new Dictionary<int, Shape>();
        _shapes ??= new List<Shape>();
    }

    void Update()
    {
        Prepare();
        float now = Time.time;
        for (int i = _deaths.Count - 1; i >= 0; i--)
        {
            Death d = _deaths[i];
            float age = now - d.start;
            if (!d.broken && age >= Swell) { d.broken = true; Tear(d, age); }
            if (!d.gone && age >= Gone)
            {
                d.gone = true;
                if (d.root) Destroy(d.root);
                if (PathManager.I) PathManager.I.Rescan(); // it was an obstacle for flying AI
            }
            if (age >= Total)
            {
                _deaths[i] = _deaths[_deaths.Count - 1];
                _deaths.RemoveAt(_deaths.Count - 1);
            }
        }
    }

    // The membrane gives: whatever stands on it is thrown off, and nothing can touch it any more.
    void Tear(Death d, float age)
    {
        Vector3 centre = d.centre + d.velocity * age;
        if (d.surface)
        {
            foreach (Organism o in Organism.All)
            {
                if (!o || o.grounded.surface.nav.Surface != d.surface) continue;
                if (o.TryGetComponent(out VirusMovement player)) player.ExitFocusMode();
                Vector3 away = (o.transform.position - centre).normalized;
                o.grounded.surface.Detach(d.velocity + away * throwOff);
            }
            d.surface.RemoveColliders();
        }
        if (d.root)
            foreach (Collider c in d.root.GetComponentsInChildren<Collider>()) c.enabled = false;
    }

    // ---------------- drawing ----------------

    void LateUpdate()
    {
        if (_deaths == null || _deaths.Count == 0) return;
        if (_gpu == null || _gpu.Length < _deaths.Count) _gpu = new Gpu[Mathf.NextPowerOfTwo(_deaths.Count)];

        // Pixels across one metre at one metre away (the detail LOD); one camera read a frame.
        Camera cam = Camera.main;
        bool ortho = cam && cam.orthographic;
        float pixelsPerMetre = !cam ? 1000f
            : ortho ? Screen.height / (2f * Mathf.Max(cam.orthographicSize, 1e-3f))
            : Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        Vector3 camPos = SimulationTicker.CameraPosition;
        float now = Time.time;
        foreach (Death d in _deaths)
        {
            float age = now - d.start;
            d.detail = -1;
            if (age < Swell) continue; // still the body
            Vector3 c = d.centre + d.velocity * age;
            float dist = Vector3.Distance(c, camPos);
            if (dist > DrawDistance + d.radius * 4f || !SimulationTicker.OnScreen(c, d.radius * 4f)) continue;
            float px = d.radius * pixelsPerMetre / (ortho ? 1f : Mathf.Max(dist, 0.1f));
            d.detail = px >= fullDetailPixels ? 0 : 1;
        }

        // Packed by kind and detail, so each draw's bursts are contiguous.
        int n = 0;
        for (int k = 0; k < _kinds.Count; k++)
        {
            Kind kind = _kinds[k];
            for (int detail = 0; detail < 2; detail++)
            {
                kind.first[detail] = n;
                foreach (Death d in _deaths)
                {
                    if (d.kind != k || d.detail != detail) continue;
                    _gpu[n] = d.gpu;
                    var bounds = new Bounds(d.centre + d.velocity * (now - d.start), Vector3.one * d.radius * 8f);
                    if (n == kind.first[detail]) kind.bounds[detail] = bounds; else kind.bounds[detail].Encapsulate(bounds);
                    n++;
                }
                kind.count[detail] = n - kind.first[detail];
            }
        }
        if (n == 0) return;

        if (_buffer == null || !_buffer.IsValid() || _buffer.count < _gpu.Length)
        {
            _buffer?.Release();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _gpu.Length, GpuStride);
        }
        _buffer.SetData(_gpu, 0, 0, n);

        foreach (Kind kind in _kinds)
            for (int detail = 0; detail < 2; detail++)
            {
                if (kind.count[detail] == 0 || !kind.debris) continue;
                Mesh mesh = _shapes[kind.shape].meshes[detail];
                if (!mesh) continue;
                kind.props[detail] ??= new MaterialPropertyBlock();
                MaterialPropertyBlock props = kind.props[detail];
                props.SetBuffer(BurstsId, _buffer);
                props.SetInt(OffsetId, kind.first[detail]);
                var rp = new RenderParams(kind.debris)
                {
                    worldBounds = kind.bounds[detail],
                    matProps = props,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = false,
                };
                Graphics.RenderMeshPrimitives(rp, mesh, 0, kind.count[detail]);
            }
    }

    // The debris material for a body's look on its shape: Hidden/CellDebris with the source's values (refreshed per
    // kill, so live edits to the source show on the next burst).
    int KindOf(Material source, Mesh mesh)
    {
        if (!source) return -1;
        int shape = ShapeOf(mesh, source);
        int index = _kinds.FindIndex(k => k.source == source && k.shape == shape);
        if (index < 0)
        {
            if (!_shader) _shader = Shader.Find("Hidden/CellDebris");
            if (!_shader) { Debug.LogWarning("CellBurst: Hidden/CellDebris not found (add it to ViralBuildAssets)."); return -1; }
            _kinds.Add(new Kind
            {
                source = source,
                shape = shape,
                debris = new Material(_shader) { name = source.name + " (debris)", hideFlags = HideFlags.DontSave },
                props = new MaterialPropertyBlock[2],
                first = new int[2],
                count = new int[2],
                bounds = new Bounds[2],
            });
            index = _kinds.Count - 1;
        }
        Material debris = _kinds[index].debris;
        debris.CopyMatchingPropertiesFromMaterial(source);
        bool cel = source.IsKeywordEnabled("_SHADING_CEL");
        debris.SetKeyword(new LocalKeyword(_shader, "_SHADING_CEL"), cel);
        debris.SetKeyword(new LocalKeyword(_shader, "_SHADING_SMOOTH"), !cel);
        return index;
    }

    // A mesh broken into pieces, once (rounded like the first look that breaks it).
    int ShapeOf(Mesh mesh, Material look)
    {
        int key = mesh && mesh.isReadable ? mesh.GetInstanceID() : 0;
        if (key == 0 && mesh) key = -mesh.GetInstanceID(); // unreadable: its bounds' ellipsoid
        if (!_shapeOf.TryGetValue(key, out Shape shape))
        {
            _shapeOf[key] = shape = new Shape();
            _shapes.Add(shape);
        }
        if (shape.meshes == null || !shape.meshes[0])
        {
            float phong = look.HasProperty(PhongId) ? Mathf.Clamp01(look.GetFloat(PhongId)) : 0f;
            CellShards.Build(mesh, phong, out Mesh fine, out Mesh coarse);
            shape.meshes = new[] { fine, coarse };
        }
        return _shapes.IndexOf(shape);
    }

    void OnDestroy()
    {
        _buffer?.Release();
        if (_shapes != null) foreach (Shape s in _shapes) if (s.meshes != null) foreach (Mesh m in s.meshes) if (m) Destroy(m);
        if (_kinds != null) foreach (Kind k in _kinds) if (k.debris) Destroy(k.debris);
        if (s_instance == this) s_instance = null;
    }
}

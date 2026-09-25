using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The world past the loaded bubble, drawn from data. WorldStreamer generates the records of every sector within
/// <see cref="distance"/> (not only the loaded ones); nothing is spawned for them, they're drawn here: one record =
/// one instance in a GPU buffer, a stand-in mesh per prefab (the prefab's meshes shrink-wrapped onto an icosphere,
/// two LODs; chunks a lumpy ball) cel shaded in the prefab's colours and fogged like the rest. Stored records drift
/// with the blood by formula (the band's rigid turn, as WorldStreamer.Advance), so the GPU poses them each frame
/// from their pose time: the CPU writes nothing per record per frame.
///
/// Crossing over: a real object dissolves out by StreamFade (screen-door) toward the load edge; its stand-in takes
/// exactly the pixels it drops (StreamFadeClipComplement), so the two cross-dissolve with no gap or double. That
/// needs a stand-in for live objects in the fade band and for loaded records not spawned yet: the small "dynamic"
/// list, rebuilt every frame. Far sectors fade in as they're generated (<see cref="bornFade"/>) and out over
/// <see cref="edgeFade"/> before <see cref="distance"/>.
///
/// Buffers: far records live in pages of 32 slots; a sector owns whole pages, and a change to a sector (generated,
/// swept into, loaded, dropped) rewrites only its pages. Each frame FarField.compute culls every slot (frustum, under
/// <see cref="minPixels"/> on screen, faded out), picks the LOD by on-screen size and appends it to its draw group;
/// one indirect draw per (look, LOD).
/// Cost: CPU O(changed sectors' records) + O(live objects) for the dynamic list per frame; GPU one thread per slot
/// (every record within the far distance, ~40k at 1600 m) + the visible instances' vertices (320 / 80 triangles).
/// Memory ~64 B per slot on the CPU and 64 + 48 + 4 x groups B on the GPU.
/// </summary>
[DefaultExecutionOrder(1000)] // after the cameras' LateUpdate: culls against this frame's view
public class FarField : MonoBehaviour
{
    [Serializable]
    public class Look
    {
        public GameObject prefab;
        public Color color = new Color(1f, 0.2f, 0.2f);
        public Color shade = new Color(0.3f, 0.05f, 0.1f);
        [Range(0f, 2f)] public float rim = 0.4f;
    }

    [Tooltip("Sectors within this of the player are generated and drawn from their records (metres). Match the fog " +
             "(Vessel.fogDensity): past ~1.9 / density nothing shows anyway.")]
    [Min(0f)] public float distance = 1600f;
    [Min(1f), Tooltip("Faded out over this many metres before Distance.")]
    public float edgeFade = 350f;
    [Min(0f), Tooltip("Seconds a newly generated sector's contents take to fade in.")]
    public float bornFade = 1.5f;
    [Min(0f), Tooltip("Not drawn when smaller than this on screen (pixels across its bounding radius).")]
    public float minPixels = 0.6f;
    [Min(1f), Tooltip("The low mesh below this many pixels (bounding radius).")]
    public float lodPixels = 14f;
    [Min(0.05f), Tooltip("Milliseconds a frame spent generating far sectors (at least one a frame while any wait).")]
    public float generateBudgetMs = 1f;
    [Min(0.1f), Tooltip("Seconds between far sector scans (also on every sector crossed).")]
    public float scanInterval = 1f;
    [Tooltip("Colours per prefab. Unlisted: from the prefab's material (_Color / _BaseColor, _DeepColor), a " +
             "chunk's substance, white cells lilac.")]
    public List<Look> looks = new List<Look>();
    public Shader shader;
    public ComputeShader cull;

    // ---------------- data ----------------

    struct FarInstance
    {
        public Vector4 position; // xyz, w = band turn rate
        public Vector4 rotation;
        public Vector4 scale;    // xyz, w = bounding radius
        public Vector4 time;     // pose time, frame angle (from the epoch), born, look (-1 empty)
    }

    const int Stride = 64, PosedStride = 48, Page = 32;
    const float LongAgo = -1e6f;

    sealed class LookData
    {
        public Mesh near, far;
        public float reach; // mesh's farthest vertex at scale 1
        public Material material;
        public MaterialPropertyBlock nearProps, farProps;
    }

    sealed class Block
    {
        public readonly List<int> pages = new List<int>();
        public float born;
    }

    WorldStreamer _streamer;
    LookData[] _looks;
    FarInstance[] _mirror, _dynamic;
    int _pages, _dynCount;
    readonly Stack<int> _free = new Stack<int>();
    readonly Dictionary<long, Block> _blocks = new Dictionary<long, Block>();
    readonly Stack<Block> _blockPool = new Stack<Block>();
    readonly Dictionary<long, bool> _dirty = new Dictionary<long, bool>(); // value: freshly generated
    readonly List<long> _dirtyKeys = new List<long>();
    readonly List<int> _touched = new List<int>();
    readonly List<WorldStreamer.EntityRecord> _pendingRecords = new List<WorldStreamer.EntityRecord>();
    GraphicsBuffer _static, _dyn, _posed, _visible, _args;
    uint[] _argsData;
    int _posedCapacity, _kernel = -1;
    double _epoch, _epochFrame;
    bool _ready, _broken;
    Camera _camera;
    VirusMovement _player;
    float _nextFind;
    readonly Plane[] _planes = new Plane[6];
    readonly Vector4[] _planeVectors = new Vector4[6];

    static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("FarField.Update");
    static readonly int SourceId = Shader.PropertyToID("_Source"), PosedId = Shader.PropertyToID("_Posed"),
                        VisibleId = Shader.PropertyToID("_Visible"), ArgsId = Shader.PropertyToID("_Args"),
                        SourceCountId = Shader.PropertyToID("_SourceCount"), PosedOffsetId = Shader.PropertyToID("_PosedOffset"),
                        GroupCapacityId = Shader.PropertyToID("_GroupCapacity"), PlanesId = Shader.PropertyToID("_Planes"),
                        CameraPosId = Shader.PropertyToID("_CameraPos"), VesselCId = Shader.PropertyToID("_VesselC"),
                        VesselAId = Shader.PropertyToID("_VesselA"), ClockId = Shader.PropertyToID("_Clock"),
                        RangeId = Shader.PropertyToID("_Range"), StreamFadeId = Shader.PropertyToID("_StreamFade"),
                        StreamFadeEndId = Shader.PropertyToID("_StreamFadeEnd"),
                        FarPosedId = Shader.PropertyToID("_FarPosed"), FarVisibleId = Shader.PropertyToID("_FarVisible"),
                        GroupBaseId = Shader.PropertyToID("_FarGroupBase"), ColorId = Shader.PropertyToID("_Color"),
                        DeepColorId = Shader.PropertyToID("_DeepColor"), RimId = Shader.PropertyToID("_Rim");

    /// <summary>Drawing and generating (the streamer scans and generates far sectors only while this is on).</summary>
    public bool On => isActiveAndEnabled && distance > 0f && !_broken;

    // ---------------- streamer calls ----------------

    /// <summary>A sector's records changed, or it came into or left the far set (fresh: just generated).</summary>
    internal void Changed(long key, bool fresh)
    {
        _dirty.TryGetValue(key, out bool was);
        _dirty[key] = was | fresh;
    }

    /// <summary>Everything changed (a save was loaded): forget every sector, restart the clock's epoch.</summary>
    internal void Clear()
    {
        if (!_ready) return;
        foreach (KeyValuePair<long, Block> kv in _blocks) FreePages(kv.Value);
        _blocks.Clear();
        _dirty.Clear();
        _epoch = Vessel.Clock;
        _epochFrame = Vessel.FrameAngle;
        UploadTouched();
    }

    // ---------------- lifecycle ----------------

    void OnDisable() => Release();
    void OnDestroy() => Release();

    void Release()
    {
        _static?.Release(); _dyn?.Release(); _posed?.Release(); _visible?.Release(); _args?.Release();
        _static = _dyn = _posed = _visible = _args = null;
        if (_looks != null)
            foreach (LookData l in _looks)
            {
                if (l == null) continue;
                if (l.material) Destroy(l.material);
                if (l.near) Destroy(l.near);
                if (l.far && l.far != l.near) Destroy(l.far);
            }
        _looks = null;
        _blocks.Clear();
        _free.Clear();
        _pages = 0;
        _mirror = null;
        _ready = false; // re-dirtied from the streamer's far set when back on
    }

    bool Init()
    {
        _streamer = WorldStreamer.Instance;
        if (!_streamer || !_streamer.Ready) return false;
        if (!shader) shader = Shader.Find("Custom/FarField");
        if (!cull && ViralBuildAssets.Instance) cull = ViralBuildAssets.Instance.farField;
        if (!shader || !cull || !SystemInfo.supportsComputeShaders)
        {
            Debug.LogWarning("FarField: no shader / compute shader (or no compute support); the far field is off.", this);
            _broken = true; // the streamer stops generating for it
            return false;
        }
        _kernel = cull.FindKernel("Cull");
        BuildLooks();
        _epoch = Vessel.Clock;
        _epochFrame = Vessel.FrameAngle;
        _dynamic = new FarInstance[256];
        _dyn = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _dynamic.Length, Stride);
        Grow(64);
        foreach (long key in _streamer.FarSectors) Changed(key, false);
        _ready = true;
        return true;
    }

    void LateUpdate()
    {
        if (!On || (!_ready || _static == null) && !Init()) return;
        using var _ = UpdateMarker.Auto();
        Rebuild();
        FillDynamic();
        Draw();
    }

    // ---------------- far records ----------------

    void Rebuild()
    {
        if (_dirty.Count == 0) return;
        _dirtyKeys.Clear();
        foreach (KeyValuePair<long, bool> kv in _dirty) _dirtyKeys.Add(kv.Key);
        foreach (long key in _dirtyKeys)
        {
            bool fresh = _dirty[key];
            float born = fresh ? Now : LongAgo;
            if (_blocks.TryGetValue(key, out Block block))
            {
                if (!fresh) born = block.born; // re-written (swept into...): keep its fade-in, don't restart it
                FreePages(block);
                _blocks.Remove(key);
                _blockPool.Push(block);
            }
            if (!_streamer.IsFar(key)) continue;
            List<WorldStreamer.EntityRecord> records = _streamer.RecordsOf(key);
            if (records == null || records.Count == 0) continue;

            block = _blockPool.Count > 0 ? _blockPool.Pop() : new Block();
            block.born = born;
            int n = 0;
            foreach (WorldStreamer.EntityRecord r in records)
            {
                if (!Make(r, born, out FarInstance f)) continue;
                if (n % Page == 0) block.pages.Add(Allocate());
                _mirror[block.pages[n / Page] * Page + n % Page] = f;
                n++;
            }
            if (n == 0) { _blockPool.Push(block); continue; }
            _blocks[key] = block;
        }
        _dirty.Clear();
        UploadTouched();
    }

    float Now => (float)(Vessel.Clock - _epoch);

    bool Make(WorldStreamer.EntityRecord r, float born, out FarInstance f)
    {
        f = default;
        int look = _streamer.FarLookOf(r);
        if (look < 0 || look >= _looks.Length || _looks[look] == null) return false;
        Vector3 s = r.scale;
        float reach = _looks[look].reach * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        Quaternion q = r.rotation;
        f.position = new Vector4(r.position.x, r.position.y, r.position.z, _streamer.DriftRate(r.position));
        f.rotation = new Vector4(q.x, q.y, q.z, q.w);
        f.scale = new Vector4(s.x, s.y, s.z, reach);
        f.time = new Vector4((float)(r.time - _epoch), (float)(r.frame - _epochFrame), born, look);
        return true;
    }

    int Allocate()
    {
        if (_free.Count == 0) Grow(_pages * 2);
        int page = _free.Pop();
        _touched.Add(page);
        return page;
    }

    void FreePages(Block block)
    {
        foreach (int page in block.pages)
        {
            for (int i = 0; i < Page; i++) _mirror[page * Page + i].time.w = -1f;
            _free.Push(page);
            _touched.Add(page);
        }
        block.pages.Clear();
    }

    // Page capacity doubled: a new buffer holding everything (a rare hitch of one upload).
    void Grow(int pages)
    {
        int old = _pages;
        pages = Mathf.Max(pages, 64);
        var mirror = new FarInstance[pages * Page];
        if (_mirror != null) Array.Copy(_mirror, mirror, _mirror.Length);
        for (int i = old * Page; i < mirror.Length; i++) mirror[i].time.w = -1f;
        _mirror = mirror;
        for (int p = pages - 1; p >= old; p--) _free.Push(p);
        _pages = pages;
        _static?.Release();
        _static = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _mirror.Length, Stride);
        _static.SetData(_mirror);
        _touched.Clear();
        ResizeOutputs();
    }

    // The changed pages, merged into runs.
    void UploadTouched()
    {
        if (_touched.Count == 0 || _static == null) return;
        _touched.Sort();
        int start = _touched[0], end = start;
        for (int i = 1; i <= _touched.Count; i++)
        {
            if (i < _touched.Count && _touched[i] <= end + 1) { end = Mathf.Max(end, _touched[i]); continue; }
            _static.SetData(_mirror, start * Page, start * Page, (end - start + 1) * Page);
            if (i < _touched.Count) start = end = _touched[i];
        }
        _touched.Clear();
    }

    // ---------------- the crossover band ----------------

    // Live objects the real fade has started on (their stand-ins take the dropped pixels) and loaded records
    // waiting to spawn.
    void FillDynamic()
    {
        _dynCount = 0;
        _streamer.FadeBand(out Vector3 centre, out float fadeStart);
        float now = Now, frame = (float)(Vessel.FrameAngle - _epochFrame);
        IReadOnlyList<WorldEntity> live = WorldStreamer.Live;
        for (int i = 0; i < live.Count; i++)
        {
            WorldEntity e = live[i];
            if (!e) continue;
            Transform t = e.T;
            Vector3 p = t.position;
            if ((p - centre).sqrMagnitude < fadeStart * fadeStart) continue;
            int look = _streamer.FarLookOf(e.key);
            if (look < 0 || look >= _looks.Length || _looks[look] == null) continue;
            Vector3 s = t.localScale;
            Quaternion q = t.rotation;
            AddDynamic(new FarInstance
            {
                position = new Vector4(p.x, p.y, p.z, 0f),
                rotation = new Vector4(q.x, q.y, q.z, q.w),
                scale = new Vector4(s.x, s.y, s.z, _looks[look].reach * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z))),
                time = new Vector4(now, frame, LongAgo, look),
            });
        }
        _streamer.PendingRecords(_pendingRecords);
        foreach (WorldStreamer.EntityRecord r in _pendingRecords)
            if (Make(r, LongAgo, out FarInstance f)) AddDynamic(f);
        _pendingRecords.Clear();
        if (_dynCount > 0) _dyn.SetData(_dynamic, 0, 0, _dynCount);
    }

    void AddDynamic(in FarInstance f)
    {
        if (_dynCount == _dynamic.Length)
        {
            Array.Resize(ref _dynamic, _dynamic.Length * 2);
            _dyn.Release();
            _dyn = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _dynamic.Length, Stride);
            ResizeOutputs();
        }
        _dynamic[_dynCount++] = f;
    }

    // Posed = every static slot then every dynamic slot; each group can list them all.
    void ResizeOutputs()
    {
        if (_looks == null || _dynamic == null || _mirror == null) return;
        int capacity = _mirror.Length + _dynamic.Length;
        if (capacity == _posedCapacity && _posed != null) return;
        _posedCapacity = capacity;
        int groups = _looks.Length * 2;
        _posed?.Release();
        _visible?.Release();
        _args?.Release();
        _posed = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, PosedStride);
        _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, groups) * capacity, sizeof(uint));
        _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, Mathf.Max(1, groups) * 5, sizeof(uint));
        _argsData = new uint[Mathf.Max(1, groups) * 5];
        for (int l = 0; l < _looks.Length; l++)
        {
            LookData look = _looks[l];
            if (look == null) continue;
            look.material.SetBuffer(FarPosedId, _posed);
            look.material.SetBuffer(FarVisibleId, _visible);
            look.nearProps.SetInteger(GroupBaseId, (l * 2) * capacity);
            look.farProps.SetInteger(GroupBaseId, (l * 2 + 1) * capacity);
        }
    }

    // ---------------- cull + draw ----------------

    void Draw()
    {
        if (!_camera || !_camera.isActiveAndEnabled) _camera = Camera.main;
        if (!_camera || _camera.orthographic) return;
        if (!_player && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            _player = FindAnyObjectByType<VirusMovement>();
        }
        if (_player && _player.IsFocusMode) return; // focus mode's sweep is about the planet under you
        if (!cull.IsSupported(_kernel)) return;      // failed to compile (the import logs why)

        int groups = _looks.Length * 2;
        for (int g = 0; g < groups; g++)
        {
            LookData look = _looks[g / 2];
            Mesh mesh = look == null ? null : g % 2 == 0 ? look.near : look.far;
            int at = g * 5;
            _argsData[at] = mesh ? mesh.GetIndexCount(0) : 0u;
            _argsData[at + 1] = 0u;
            _argsData[at + 2] = mesh ? mesh.GetIndexStart(0) : 0u;
            _argsData[at + 3] = mesh ? (uint)mesh.GetBaseVertex(0) : 0u;
            _argsData[at + 4] = 0u;
        }
        _args.SetData(_argsData);

        GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
        for (int i = 0; i < 6; i++) _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
        Vector3 cam = _camera.transform.position;
        float pixelScale = _camera.pixelHeight / (2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
        Vessel vessel = Vessel.Active;
        Vector4 fade = Shader.GetGlobalVector(StreamFadeId);
        float fadeEnd = Shader.GetGlobalFloat(StreamFadeEndId);
        if (fade.w <= 0f) fade.w = 1000f; // no fade band: the stand-in takes over right at the edge

        cull.SetVectorArray(PlanesId, _planeVectors);
        cull.SetVector(CameraPosId, new Vector4(cam.x, cam.y, cam.z, pixelScale));
        cull.SetVector(VesselCId, vessel ? (Vector4)vessel.LoopCentre + new Vector4(0f, 0f, 0f, 1f) : Vector4.zero);
        cull.SetVector(VesselAId, vessel ? (Vector4)vessel.Axis : Vector4.zero);
        cull.SetVector(ClockId, new Vector4(Now, (float)(Vessel.FrameAngle - _epochFrame), bornFade, 0f));
        cull.SetVector(RangeId, new Vector4(distance, Mathf.Min(edgeFade, distance), minPixels, lodPixels));
        cull.SetVector(StreamFadeId, fade);
        cull.SetFloat(StreamFadeEndId, fadeEnd);
        cull.SetInt(GroupCapacityId, _posedCapacity);
        cull.SetBuffer(_kernel, PosedId, _posed);
        cull.SetBuffer(_kernel, VisibleId, _visible);
        cull.SetBuffer(_kernel, ArgsId, _args);

        Dispatch(_static, _mirror.Length, 0);
        if (_dynCount > 0) Dispatch(_dyn, _dynCount, _mirror.Length);

        var bounds = new Bounds(cam, Vector3.one * (distance * 2f + 200f));
        for (int g = 0; g < groups; g++)
        {
            LookData look = _looks[g / 2];
            Mesh mesh = look == null ? null : g % 2 == 0 ? look.near : look.far;
            if (!mesh) continue;
            Graphics.DrawMeshInstancedIndirect(mesh, 0, look.material, bounds, _args, g * 5 * sizeof(uint),
                                               g % 2 == 0 ? look.nearProps : look.farProps, ShadowCastingMode.Off, false,
                                               0, _camera);
        }
    }

    void Dispatch(GraphicsBuffer source, int count, int offset)
    {
        cull.SetBuffer(_kernel, SourceId, source);
        cull.SetInt(SourceCountId, count);
        cull.SetInt(PosedOffsetId, offset);
        cull.Dispatch(_kernel, (count + 63) / 64, 1, 1);
    }

    // ---------------- looks ----------------

    void BuildLooks()
    {
        IReadOnlyList<GameObject> prefabs = _streamer.FarPrefabs;
        _looks = new LookData[prefabs.Count];
        for (int i = 0; i < prefabs.Count; i++)
        {
            GameObject prefab = prefabs[i];
            if (!prefab) continue;
            Look set = looks.Find(l => l != null && l.prefab == prefab);
            Colours(prefab, out Color color, out Color shade, out float rim);
            if (set != null) { color = set.color; shade = set.shade; rim = set.rim; }

            var look = new LookData();
            if (prefab.GetComponent<ResourceChunk>())
            {
                look.near = look.far = Lumpy(1, Hash(prefab.name));
                look.reach = 1.15f;
            }
            else
            {
                look.near = ShrinkWrap(prefab, 2, out look.reach);
                look.far = ShrinkWrap(prefab, 1, out _);
                if (!look.near)
                {
                    float radius = WorldStreamer.PrefabRadius(prefab);
                    look.near = look.far = Lumpy(1, 0, radius);
                    look.reach = 1.15f * radius;
                }
            }
            look.material = new Material(shader) { name = "Far " + prefab.name, hideFlags = HideFlags.DontSave, enableInstancing = true };
            look.material.SetColor(ColorId, color);
            look.material.SetColor(DeepColorId, shade);
            look.material.SetFloat(RimId, rim);
            look.nearProps = new MaterialPropertyBlock();
            look.farProps = new MaterialPropertyBlock();
            _looks[i] = look;
        }
        _posedCapacity = 0;
    }

    static void Colours(GameObject prefab, out Color color, out Color shade, out float rim)
    {
        rim = 0.35f;
        if (prefab.TryGetComponent(out ResourceChunk chunk))
        {
            color = chunk.substance.color;
            shade = color * 0.45f;
            rim = 0.6f;
        }
        else if (prefab.GetComponent<WhiteBloodCell>())
        {
            color = new Color(0.93f, 0.9f, 1f);
            shade = new Color(0.55f, 0.5f, 0.72f);
        }
        else
        {
            Renderer r = prefab.GetComponentInChildren<Renderer>(true);
            Material m = r ? r.sharedMaterial : null;
            color = m && m.HasProperty(ColorId) ? m.GetColor(ColorId) : m && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : Color.grey;
            Color deep = m && m.HasProperty(DeepColorId) ? m.GetColor(DeepColorId) : color * 0.3f;
            shade = Color.Lerp(color * 0.3f, deep, 0.35f);
        }
        color.a = shade.a = 1f;
    }

    static int Hash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }

    // A ball with a few soft lumps (resource chunks at radius 1, and anything without a readable mesh).
    static Mesh Lumpy(int detail, int seed, float radius = 1f)
    {
        Icosphere(detail, out List<Vector3> dirs, out List<int> tris);
        float a = (seed & 1023) * 0.37f, b = ((seed >> 10) & 1023) * 0.23f;
        var v = new Vector3[dirs.Count];
        for (int i = 0; i < v.Length; i++)
        {
            Vector3 d = dirs[i];
            float lump = 0.07f * Mathf.Sin(d.x * 3.1f + a) * Mathf.Sin(d.y * 2.7f + b) + 0.05f * Mathf.Sin(d.z * 4.3f + a + b);
            v[i] = d * (radius * (1f + lump));
        }
        return Build("Far lumpy", v, tris);
    }

    // The prefab's meshes (root space, so a record's scale fits) seen from its origin: an icosphere whose every
    // vertex is pushed out to the farthest surface along its direction (the star-shaped hull; a red cell's dimples
    // stay, since the disc is star-shaped from its middle). Null without a readable mesh.
    static Mesh ShrinkWrap(GameObject prefab, int detail, out float reach)
    {
        reach = 0f;
        var pts = new List<Vector3>();
        var idx = new List<int>();
        Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
        foreach (MeshFilter f in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh m = f.sharedMesh;
            if (!m || !m.isReadable) continue;
            Matrix4x4 mat = toRoot * f.transform.localToWorldMatrix;
            int start = pts.Count;
            foreach (Vector3 v in m.vertices) pts.Add(mat.MultiplyPoint3x4(v));
            for (int s = 0; s < m.subMeshCount; s++)
            {
                if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                foreach (int i in m.GetTriangles(s)) idx.Add(start + i);
            }
        }
        if (idx.Count == 0) return null;

        Icosphere(detail, out List<Vector3> dirs, out List<int> tris);
        var dist = new float[dirs.Count];
        float sum = 0f;
        int hits = 0;
        for (int d = 0; d < dirs.Count; d++)
        {
            float best = 0f;
            for (int t = 0; t < idx.Count; t += 3)
            {
                float hit = Ray(dirs[d], pts[idx[t]], pts[idx[t + 1]], pts[idx[t + 2]]);
                if (hit > best) best = hit;
            }
            dist[d] = best;
            if (best > 0f) { sum += best; hits++; }
        }
        if (hits == 0) return null;
        float mean = sum / hits;
        var verts = new Vector3[dirs.Count];
        for (int d = 0; d < dirs.Count; d++)
        {
            verts[d] = dirs[d] * (dist[d] > 0f ? dist[d] : mean); // a missed ray (origin off the solid): the mean
            reach = Mathf.Max(reach, verts[d].magnitude);
        }
        return Build("Far " + prefab.name, verts, tris);
    }

    // Distance along a ray from the origin to a triangle (either side), 0 on a miss.
    static float Ray(Vector3 dir, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-12f) return 0f;
        float inv = 1f / det;
        Vector3 s = -a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return 0f;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(dir, q) * inv;
        if (v < 0f || u + v > 1f) return 0f;
        float t = Vector3.Dot(e2, q) * inv;
        return t > 0f ? t : 0f;
    }

    static Mesh Build(string name, Vector3[] verts, List<int> tris)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.vertices = verts;
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.UploadMeshData(true);
        return mesh;
    }

    // Unit icosphere: detail 1 = 42 vertices / 80 triangles, 2 = 162 / 320.
    static void Icosphere(int detail, out List<Vector3> v, out List<int> tris)
    {
        float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
        v = new List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
        tris = new List<int>
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };
        for (int level = 0; level < detail; level++)
        {
            var mid = new Dictionary<long, int>();
            var next = new List<int>(tris.Count * 4);
            List<Vector3> verts = v;
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (mid.TryGetValue(key, out int m)) return m;
                verts.Add((verts[a] + verts[b]).normalized);
                return mid[key] = verts.Count - 1;
            }
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
            }
            tris = next;
        }
    }
}

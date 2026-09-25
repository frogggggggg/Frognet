using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Simulates and draws the legs of every SpiderLegWalker at once, on the GPU. Each walker keeps a slot of GPU state
/// (its legs' gait, feet and springs live there across frames); each frame a walker that's in view sends one small
/// record (Walker: its hub, frame, velocity, support, mode changes), LegSimulation.compute steps every walker's legs
/// in one dispatch (one thread per walker, feet planted with the support's SurfaceMap) and writes the leg records,
/// and the legs draw as instances of one shared tube mesh with Custom/BloodCellLegs: one draw per group (leg
/// material, tube resolution, shadows), however many walkers there are.
///
/// Footsteps come back as events (which legs planted, where), read back asynchronously a frame or two later, and
/// go to CreatureAudio through the walker.
///
/// Created on demand the first time a walker submits: nothing to set up. The walker's leg material is copied onto
/// the leg shader, so it keeps its look and edits to it show up live. Cost: CPU O(walkers in view) records of 432
/// bytes a frame; GPU one thread per walker (its legs' gait + a few SurfaceMap lookups on the frames a foot lifts
/// or plants), then the instanced draws.
/// </summary>
[DefaultExecutionOrder(200)] // after SimulationTicker's LateUpdate (0), where walkers submit this frame's legs
public class LegRenderer : MonoBehaviour
{
    /// <summary>One walker's frame. Layout matches Walker in LegSimulation.compute (27 float4s).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Walker
    {
        public Vector4 pos, normal, right, fwd, planar, bodyUp;
        public Vector4 m0, m1, m2, i0, i1, i2;
        public Vector4 carry, shape0, shape1, gait;
        public Vector4 airAxis, orbitRef, trail, land, landUp, toLand, grabAxis, grabRef, look;
        public int legBase, legCount, outBase, flags;
        public int map, seed, walkerState, pad;
    }

    const int WalkerStride = 27 * 16, LegStateStride = 364, LegDataStride = 16 * 8, EventStride = 16;

    static readonly int WalkersId = Shader.PropertyToID("_Walkers"), LegStateId = Shader.PropertyToID("_LegState"),
                        WalkerStateId = Shader.PropertyToID("_WalkerState"), LegsOutId = Shader.PropertyToID("_LegsOut"),
                        EventsId = Shader.PropertyToID("_Events"), LegSrcId = Shader.PropertyToID("_LegSrc"),
                        WalkerSrcId = Shader.PropertyToID("_WalkerSrc"), SimTimeId = Shader.PropertyToID("_SimTime"),
                        WalkerCountId = Shader.PropertyToID("_WalkerCount"), CopyCountId = Shader.PropertyToID("_CopyCount"),
                        MapInfosId = Shader.PropertyToID("_MapInfos"), MapTrisId = Shader.PropertyToID("_MapTris"),
                        MapCellsId = Shader.PropertyToID("_MapCells"), MapIndicesId = Shader.PropertyToID("_MapIndices"),
                        LegsId = Shader.PropertyToID("_Legs"), LegBaseId = Shader.PropertyToID("_LegBase");

    /// <summary>Differs after a script reload (statics reset): a walker holding an older one re-allocates.</summary>
    public static readonly int Generation = Environment.TickCount | 1;

    readonly struct GroupKey : IEquatable<GroupKey>
    {
        readonly int _material, _shader, _rings, _sides;
        readonly bool _shadows;

        public GroupKey(Material m, Shader s, int rings, int sides, bool shadows)
        {
            _material = m.GetInstanceID();
            _shader = s.GetInstanceID();
            _rings = rings;
            _sides = sides;
            _shadows = shadows;
        }

        public bool Equals(GroupKey o) =>
            _material == o._material && _shader == o._shader && _rings == o._rings && _sides == o._sides && _shadows == o._shadows;

        public override bool Equals(object o) => o is GroupKey k && Equals(k);
        public override int GetHashCode() => HashCode.Combine(_material, _shader, _rings, _sides, _shadows);
    }

    class Group
    {
        public Material source, material;
        public Mesh mesh;
        public bool shadows;
        public readonly List<int> records = new List<int>(); // this frame's walkers (indices into _frame)
        public int legs, first;
        public Bounds bounds;
        public MaterialPropertyBlock props;
    }

    // A frame's footstep events on their way back from the GPU.
    class Readback
    {
        public AsyncGPUReadbackRequest request;
        public SpiderLegWalker[] owners = new SpiderLegWalker[64];
        public float[] footDistance = new float[64];
        public int count;
    }

    static LegRenderer s_instance;

    // Rebuilt lazily: a play-mode script reload wipes plain fields (see CLAUDE.md).
    Dictionary<GroupKey, Group> _groups;
    Dictionary<Vector2Int, Mesh> _meshes;
    Walker[] _frame, _sorted;
    SpiderLegWalker[] _owners, _ownersSorted;
    int _count;
    List<Group> _active;

    GraphicsBuffer _walkers, _legState, _walkerState, _legsOut, _events;
    GraphicsBuffer _mapInfos, _mapTris, _mapCells, _mapIndices;
    int _mapVersion = -1;
    ComputeShader _cs;
    int _simulate = -1, _copyLegs, _copyWalkers;
    bool _warned;

    // Slots: a walker index (its walker state) and a block of leg states, freed on disable.
    int _walkerTop, _legTop;
    Stack<int> _freeWalkers;
    Dictionary<int, Stack<int>> _freeLegs;

    Queue<Readback> _pending, _spare;

    static LegRenderer Instance
    {
        get
        {
            if (!s_instance)
            {
                var go = new GameObject("Leg Renderer") { hideFlags = HideFlags.HideAndDontSave };
                s_instance = go.AddComponent<LegRenderer>();
            }
            s_instance.EnsureState();
            return s_instance;
        }
    }

    void EnsureState()
    {
        _groups ??= new Dictionary<GroupKey, Group>();
        _meshes ??= new Dictionary<Vector2Int, Mesh>();
        _frame ??= new Walker[64];
        _sorted ??= new Walker[64];
        _owners ??= new SpiderLegWalker[64];
        _ownersSorted ??= new SpiderLegWalker[64];
        _active ??= new List<Group>();
        _freeWalkers ??= new Stack<int>();
        _freeLegs ??= new Dictionary<int, Stack<int>>();
        _pending ??= new Queue<Readback>();
        _spare ??= new Queue<Readback>();
    }

    /// <summary>
    /// Worth drawing this frame: inside the main camera's view, give or take 'pad' metres. The
    /// view is sampled before the camera's own LateUpdate, so it trails a frame: pad it, or legs
    /// at the edge blink out on fast camera moves (and their shadows with them).
    /// </summary>
    public static bool Visible(Bounds bounds, float pad = 0f) =>
        SimulationTicker.OnScreen(bounds.center, bounds.extents.magnitude + pad); // shared per-frame camera

    /// <summary>GPU state for a walker with 'legs' legs: its walker slot and first leg slot.</summary>
    public static void Allocate(int legs, out int walker, out int legBase)
    {
        LegRenderer r = Instance;
        walker = r._freeWalkers.Count > 0 ? r._freeWalkers.Pop() : r._walkerTop++;
        legBase = r._freeLegs.TryGetValue(legs, out Stack<int> free) && free.Count > 0 ? free.Pop() : (r._legTop += legs) - legs;
    }

    public static void Free(int walker, int legBase, int legs)
    {
        if (!s_instance) return;
        LegRenderer r = s_instance;
        r.EnsureState();
        r._freeWalkers.Push(walker);
        if (!r._freeLegs.TryGetValue(legs, out Stack<int> free)) r._freeLegs[legs] = free = new Stack<int>();
        free.Push(legBase);
    }

    /// <summary>Queue a walker's frame. 'material' null = simulate only (off screen but kept walking).</summary>
    public static void Submit(SpiderLegWalker owner, in Walker frame, Material material, Shader shader, int rings, int sides,
                              bool shadows, Bounds bounds)
    {
        LegRenderer r = Instance;
        if (!r.isActiveAndEnabled) return; // switched off (the profiler's "legs hidden"): nothing would take them
        if (r._count == r._frame.Length)
        {
            Array.Resize(ref r._frame, r._count * 2);
            Array.Resize(ref r._sorted, r._count * 2);
            Array.Resize(ref r._owners, r._count * 2);
            Array.Resize(ref r._ownersSorted, r._count * 2);
        }
        int index = r._count++;
        r._frame[index] = frame;
        r._owners[index] = owner;

        if (!material || !shader) { r._frame[index].outBase = -1; return; }
        Group g = r.GetGroup(material, shader, rings, sides, shadows);
        if (g.records.Count == 0)
        {
            g.bounds = bounds;
            r._active.Add(g);
        }
        else g.bounds.Encapsulate(bounds);
        g.records.Add(index);
        g.legs += frame.legCount;
        r._frame[index].outBase = 0; // placed in LateUpdate
    }

    Group GetGroup(Material source, Shader shader, int rings, int sides, bool shadows)
    {
        var key = new GroupKey(source, shader, rings, sides, shadows);
        if (_groups.TryGetValue(key, out Group g)) return g;

        g = new Group
        {
            source = source,
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true },
            mesh = TubeMesh(rings, sides),
            shadows = shadows,
        };
        g.material.CopyPropertiesFromMaterial(source);
        g.material.shaderKeywords = source.shaderKeywords;
        _groups.Add(key, g);
        return g;
    }

    ComputeShader Compute()
    {
        if (!_cs && ViralBuildAssets.Instance) _cs = ViralBuildAssets.Instance.legSimulation;
        if (_cs && _simulate < 0)
        {
            _simulate = _cs.FindKernel("Simulate");
            _copyLegs = _cs.FindKernel("CopyLegs");
            _copyWalkers = _cs.FindKernel("CopyWalkers");
        }
        return _cs;
    }

    void LateUpdate()
    {
        EnsureState();
        DeliverEvents();
        if (_count == 0) return;

        ComputeShader cs = Compute();
        if (!cs || !SystemInfo.supportsComputeShaders || !cs.IsSupported(_simulate))
        {
            if (!_warned) Debug.LogWarning("LegRenderer: no LegSimulation.compute (ViralBuildAssets.legSimulation) or no compute support; legs aren't drawn.");
            _warned = true;
            Clear();
            return;
        }

        // Order: each group's walkers together (their legs are one contiguous range of the output), then the
        // walkers only simulated.
        int n = 0, legs = 0;
        foreach (Group g in _active)
        {
            g.first = legs;
            foreach (int i in g.records)
            {
                Walker w = _frame[i];
                w.outBase = legs;
                legs += w.legCount;
                Place(n++, w, i);
            }
        }
        for (int i = 0; i < _count; i++)
            if (_frame[i].outBase < 0) Place(n++, _frame[i], i);

        void Place(int at, in Walker w, int from)
        {
            _sorted[at] = w;
            _ownersSorted[at] = _owners[from];
        }

        EnsureBuffers(n, Mathf.Max(legs, 1));
        UploadMaps();

        _walkers.SetData(_sorted, 0, 0, n);
        cs.SetFloat(SimTimeId, Time.time);
        cs.SetInt(WalkerCountId, n);
        cs.SetBuffer(_simulate, WalkersId, _walkers);
        cs.SetBuffer(_simulate, LegStateId, _legState);
        cs.SetBuffer(_simulate, WalkerStateId, _walkerState);
        cs.SetBuffer(_simulate, LegsOutId, _legsOut);
        cs.SetBuffer(_simulate, EventsId, _events);
        cs.SetBuffer(_simulate, MapInfosId, _mapInfos);
        cs.SetBuffer(_simulate, MapTrisId, _mapTris);
        cs.SetBuffer(_simulate, MapCellsId, _mapCells);
        cs.SetBuffer(_simulate, MapIndicesId, _mapIndices);
        cs.Dispatch(_simulate, (n + 63) / 64, 1, 1);

#if UNITY_EDITOR
        bool refreshKeywords = Time.frameCount % 60 == 0; // shader keywords rarely change; skip the allocation
#endif
        foreach (Group g in _active)
        {
            if (g.source)
            {
#if UNITY_EDITOR
                // Follow live edits to the leg material (a build copies it once, at creation).
                g.material.CopyPropertiesFromMaterial(g.source);
                if (refreshKeywords) g.material.shaderKeywords = g.source.shaderKeywords;
#endif
                g.props ??= new MaterialPropertyBlock();
                g.props.SetBuffer(LegsId, _legsOut);
                g.props.SetInteger(LegBaseId, g.first);
                Graphics.DrawMeshInstancedProcedural(g.mesh, 0, g.material, g.bounds, g.legs, g.props,
                    g.shadows ? ShadowCastingMode.On : ShadowCastingMode.Off, true, 0, null, LightProbeUsage.Off);
            }
        }

        RequestEvents(n);
        RunInspections();
        Clear();
    }

    void Clear()
    {
        foreach (Group g in _active)
        {
            g.records.Clear();
            g.legs = 0;
        }
        _active.Clear();
        Array.Clear(_owners, 0, _count);
        Array.Clear(_ownersSorted, 0, _count);
        _count = 0;
    }

    // ---------------- buffers ----------------

    void EnsureBuffers(int walkers, int legs)
    {
        Grow(ref _walkers, walkers, WalkerStride);
        Grow(ref _legsOut, legs, LegDataStride);
        Grow(ref _events, walkers, EventStride);
        GrowKeeping(ref _legState, Mathf.Max(_legTop, 1), LegStateStride, _copyLegs, LegSrcId, LegStateId);
        GrowKeeping(ref _walkerState, Mathf.Max(_walkerTop, 1), 8, _copyWalkers, WalkerSrcId, WalkerStateId);
    }

    static void Grow(ref GraphicsBuffer buffer, int count, int stride)
    {
        if (buffer != null && buffer.IsValid() && buffer.count >= count) return;
        buffer?.Release();
        buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(count, 64)), stride);
    }

    // State that must survive growing: copied over on the GPU.
    void GrowKeeping(ref GraphicsBuffer buffer, int count, int stride, int kernel, int srcId, int dstId)
    {
        if (buffer != null && buffer.IsValid() && buffer.count >= count) return;
        var grown = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(count, 256)), stride);
        if (buffer != null && buffer.IsValid())
        {
            _cs.SetInt(CopyCountId, buffer.count);
            _cs.SetBuffer(kernel, srcId, buffer);
            _cs.SetBuffer(kernel, dstId, grown);
            _cs.Dispatch(kernel, (buffer.count + 63) / 64, 1, 1);
            buffer.Release();
        }
        buffer = grown;
    }

    static readonly List<SurfaceMap.Info> s_infos = new List<SurfaceMap.Info>();
    static readonly List<SurfaceMap.Tri> s_tris = new List<SurfaceMap.Tri>();
    static readonly List<Vector2Int> s_cells = new List<Vector2Int>();
    static readonly List<int> s_indices = new List<int>();

    // Every registered SurfaceMap, re-uploaded when one joins (rare: once per walkable mesh).
    void UploadMaps()
    {
        if (_mapVersion == SurfaceMap.Version && _mapInfos != null && _mapInfos.IsValid()) return;
        _mapVersion = SurfaceMap.Version;
        SurfaceMap.Pack(s_infos, s_tris, s_cells, s_indices);
        Upload(ref _mapInfos, s_infos, SurfaceMap.InfoStride);
        Upload(ref _mapTris, s_tris, SurfaceMap.TriStride);
        Upload(ref _mapCells, s_cells, SurfaceMap.CellStride);
        Upload(ref _mapIndices, s_indices, 4);
    }

    static void Upload<T>(ref GraphicsBuffer buffer, List<T> data, int stride) where T : struct
    {
        buffer?.Release();
        buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, data.Count), stride);
        if (data.Count > 0) buffer.SetData(data);
    }

    // ---------------- debug ----------------

    readonly List<(string name, int walker, int leg)> _inspect = new List<(string, int, int)>();

    /// <summary>Logs a walker's GPU state (gait cycle, hub height) and its first leg's, after this frame's step.</summary>
    public static void Inspect(string name, int walker, int legBase) => Instance._inspect.Add((name, walker, legBase));

    void RunInspections()
    {
        foreach (var (name, walker, leg) in _inspect)
        {
            if (_walkerState == null || _legState == null || walker >= _walkerState.count || leg >= _legState.count) continue;
            AsyncGPUReadback.Request(_walkerState, 8, walker * 8, w =>
            {
                if (w.hasError) return;
                var ws = w.GetData<float>();
                string head = $"[legs {name}] gpu: cycle={ws[0]:F3} hub={ws[1]:F3}";
                AsyncGPUReadback.Request(_legState, LegStateStride, leg * LegStateStride, r =>
                {
                    if (r.hasError) return;
                    var f = r.GetData<float>();
                    int i(int k) => BitConverter.SingleToInt32Bits(f[k]);
                    Vector3 v(int k) => new Vector3(f[k], f[k + 1], f[k + 2]);
                    // LegState: 0 angle, 1 phase, 2 rReach, 3 rHeight, 4 delay, 5 swingStart, 6 lift, 7 plant, 8 stepping,
                    // 9 window, 10 foot(local 10, localN 13, world 16, normal 19), from 22, to 34, aim 46/49, home 52, tip 55
                    Debug.Log($"{head} leg0: stepping={i(8)} window={i(9)} swingStart={f[5]:F3} lift={f[6]:F3} plant={f[7]:F2} " +
                              $"foot.local={v(10)} foot.world={v(16)} to.world={v(40)} tip={v(55)} home={v(52)}");
                });
            });
        }
        _inspect.Clear();
    }

    // ---------------- footsteps ----------------

    void RequestEvents(int count)
    {
        if (!SystemInfo.supportsAsyncGPUReadback || _pending.Count > 4) return;
        Readback r = _spare.Count > 0 ? _spare.Dequeue() : new Readback();
        if (r.owners.Length < count)
        {
            r.owners = new SpiderLegWalker[Mathf.NextPowerOfTwo(count)];
            r.footDistance = new float[r.owners.Length];
        }
        for (int i = 0; i < count; i++)
        {
            r.owners[i] = _ownersSorted[i];
            r.footDistance[i] = _sorted[i].shape0.x;
        }
        r.count = count;
        r.request = AsyncGPUReadback.Request(_events, count * EventStride, 0);
        _pending.Enqueue(r);
    }

    void DeliverEvents()
    {
        while (_pending.Count > 0 && _pending.Peek().request.done)
        {
            Readback r = _pending.Dequeue();
            if (!r.request.hasError)
            {
                NativeArray<uint> data = r.request.GetData<uint>();
                for (int i = 0; i < r.count; i++)
                {
                    uint planted = data[i * 4 + 3];
                    SpiderLegWalker owner = r.owners[i];
                    if (planted == 0 || !owner) continue;
                    var at = new Vector3(BitConverter.Int32BitsToSingle((int)data[i * 4]), BitConverter.Int32BitsToSingle((int)data[i * 4 + 1]),
                                         BitConverter.Int32BitsToSingle((int)data[i * 4 + 2]));
                    for (; planted != 0; planted &= planted - 1) owner.Planted(at);
                }
            }
            Array.Clear(r.owners, 0, r.count);
            _spare.Enqueue(r);
        }
    }

    // ---------------- mesh ----------------

    // One leg's tube: rings along it, a vertex per side around each, plus a root and a tip
    // cap. Vertices only carry where they sit on the leg; the shader does the rest.
    Mesh TubeMesh(int rings, int sides)
    {
        var key = new Vector2Int(rings, sides);
        if (_meshes.TryGetValue(key, out Mesh cached)) return cached;

        int count = rings * sides + 2;
        var shape = new Vector3[count];
        var cap = new Vector2[count];
        var tris = new int[(rings - 1) * sides * 6 + sides * 6];
        int ti = 0;

        for (int r = 0; r < rings; r++)
        for (int s = 0; s < sides; s++)
        {
            float a = s / (float)sides * Mathf.PI * 2f;
            shape[r * sides + s] = new Vector3(r / (float)(rings - 1), Mathf.Cos(a), Mathf.Sin(a));
        }

        int rootCap = rings * sides, tipCap = rootCap + 1, last = (rings - 1) * sides;
        shape[rootCap] = new Vector3(0f, 0f, 0f);
        shape[tipCap] = new Vector3(1f, 0f, 0f);
        cap[rootCap] = new Vector2(-1f, 0f);
        cap[tipCap] = new Vector2(1f, 0f);

        // Winding a->b->c gives outward faces for the ring frame (side, up, tangent).
        for (int r = 0; r < rings - 1; r++)
        for (int s = 0; s < sides; s++)
        {
            int ns = (s + 1) % sides;
            int a0 = r * sides + s, a1 = r * sides + ns;
            tris[ti++] = a0; tris[ti++] = a1; tris[ti++] = a0 + sides;
            tris[ti++] = a1; tris[ti++] = a1 + sides; tris[ti++] = a0 + sides;
        }
        for (int s = 0; s < sides; s++)
        {
            int ns = (s + 1) % sides;
            tris[ti++] = rootCap; tris[ti++] = ns; tris[ti++] = s;
            tris[ti++] = tipCap; tris[ti++] = last + s; tris[ti++] = last + ns;
        }

        var mesh = new Mesh { name = $"Leg Tube {rings}x{sides}", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(shape);
        mesh.SetUVs(0, cap);
        mesh.SetTriangles(tris, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e4f); // placed by the shader; culled by the draw's bounds
        _meshes.Add(key, mesh);
        return mesh;
    }

    void OnDestroy()
    {
        foreach (GraphicsBuffer b in new[] { _walkers, _legState, _walkerState, _legsOut, _events, _mapInfos, _mapTris, _mapCells, _mapIndices })
            b?.Release();
        if (_groups != null)
            foreach (Group g in _groups.Values)
                if (g.material) Destroy(g.material);
        if (_meshes != null)
            foreach (Mesh m in _meshes.Values)
                if (m) Destroy(m);
        if (s_instance == this) s_instance = null;
    }
}

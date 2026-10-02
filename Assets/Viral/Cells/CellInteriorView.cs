using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws the inside of the cells being looked into (focus mode: the one the virus stands on; VirusMovement calls
/// <see cref="Show"/> each frame): the nucleus, the organelles, rings of energy off working ones, and the stores as
/// motes (CellInteriorView.Motes.cs): lumps of each substance resting through the cell's real volume, born only at a
/// source (the organelle making it, the membrane where it comes in) and gone only into a sink (the organelle using
/// it, the membrane where it leaves), so every change in a store is seen moving. Everything is data from
/// CellInterior; motion is the shaders' (CellInterior.hlsl), the CPU only lays things out (from the cell's seed, so
/// each cell is always arranged the same) and sends a mote somewhere when a store changes.
///
/// Layout: a *frame* per cell, a unit ball stretched over the mesh's bounds (inset), its z the thin side. The nucleus
/// sits in the middle, organelles round it on a golden-angle spiral (adding one never moves the rest), each snapped
/// to the nearest resting spot deep enough inside the mesh.
///
/// Cost: four draws (motes: one quad each, 256 per shown cell; honey streams: one quad per honey maker; bodies: one
/// instanced ball each; rings: 3 quads per working organelle), ~a hundred small structs written a frame for a cell plus its motes' 20 KB when one moves.
/// Made on demand.
/// </summary>
[DefaultExecutionOrder(100)] // after VirusMovement shows this frame's cells
public partial class CellInteriorView : MonoBehaviour
{
    [Tooltip("Custom/CellInterior. Found by name when empty (ViralBuildAssets ships it).")]
    public Shader bodyShader;
    [Tooltip("Custom/CellInteriorMotes.")]
    public Shader moteShader;

    [Header("Layout (shares of the cell's radius)")]
    [Range(0.3f, 1f), Tooltip("How far into the cell's bounds things go, across it.")]
    public float inset = 0.8f;
    [Range(0.1f, 1f), Tooltip("And through its thin side.")]
    public float thinInset = 0.55f;
    public float nucleusSize = 0.24f;
    public Color nucleusColor = new Color(0.62f, 0.64f, 1f);

    [Header("Motes")]
    public float moteSize = 0.03f;
    [Min(1f), Tooltip("Motes for a full store slot (a slot holds the profile's capacity).")]
    public float motesPerSlot = 40f;
    [Min(0.01f), Tooltip("Frame units a second a mote travels between a source, a resting spot and a sink.")]
    public float moteSpeed = 0.22f;
    [Min(1f), Tooltip("Seconds a resting mote stays put on average before drifting to a spot nearby.")]
    public float restSeconds = 14f;
    [Min(1), Tooltip("Most motes of one substance born / sent off per cell per frame.")]
    public int movesPerFrame = 2;

    [Header("Energy rings")]
    public float waveReach = 0.3f;
    [Min(0.05f), Tooltip("Seconds the insides take to grow out when a cell starts showing.")]
    public float appearTime = 0.9f;

    /// <summary>The cell whose nucleus is pointed at / open (NucleusView): it lights up.</summary>
    public CellInterior Hovered { get; set; }
    public CellInterior Opened { get; set; }

    const int MaxCells = 4, MaxBodies = 64, MaxWaves = 64, RingsPerWave = 3;

    struct Frame { public Vector4 centre, axisX, axisY, axisZ; }
    struct Body { public Vector4 position, color, info, axis; }
    struct Wave { public Vector4 from, to, color, info, shape; } // to: honey stream's pool point, w 1 = drawn (span in info.xy)
    struct Placed { public OrganelleType type; public float activity; public Vector4 at; public int group; }
    const int FrameStride = 64, BodyStride = 64, WaveStride = 80;

    sealed class Shown
    {
        public CellInterior cell;
        public int frame, slot;
        public float appear, highlight;
        public Vector3 centre;
        public float unit;
        public readonly Motes motes = new Motes();
    }

    static CellInteriorView s_instance;
    static readonly int FramesId = Shader.PropertyToID("_CellFrames"), BodiesId = Shader.PropertyToID("_CellBodies"),
                        WavesId = Shader.PropertyToID("_CellWaves"), MotesId = Shader.PropertyToID("_CellMotes"),
                        HoneyId = Shader.PropertyToID("_CellHoney"),
                        ModeId = Shader.PropertyToID("_MoteMode"), WaveCountId = Shader.PropertyToID("_CellWaveCount"),
                        NucleusSizeId = Shader.PropertyToID("_NucleusSize");

    readonly List<Shown> _shown = new List<Shown>();
    readonly bool[] _slotUsed = new bool[MaxCells];
    readonly Frame[] _frames = new Frame[MaxCells];
    readonly Body[] _bodies = new Body[MaxBodies];
    readonly Placed[] _placed = new Placed[MaxBodies];
    readonly Wave[] _waves = new Wave[MaxWaves];
    int _bodyCount, _waveCount;
    GraphicsBuffer _frameBuffer, _bodyBuffer, _waveBuffer, _moteBuffer, _honeyBuffer;
    MaterialPropertyBlock _moteProps, _waveProps, _streamProps;
    Material _bodyMat, _moteMat, _waveMat;

    public static CellInteriorView Instance
    {
        get
        {
            if (s_instance) return s_instance;
            s_instance = FindAnyObjectByType<CellInteriorView>();
            if (!s_instance) s_instance = new GameObject("Cell Interior View").AddComponent<CellInteriorView>();
            return s_instance;
        }
    }

    /// <summary>Draw this cell's insides this frame (call every frame it should show).</summary>
    public static void Show(CellInterior cell)
    {
        if (!cell) return;
        CellInteriorView v = Instance;
        cell.MarkShown();
        foreach (Shown s in v._shown)
            if (s.cell == cell) { s.frame = Time.frameCount; return; }
        for (int slot = 0; slot < MaxCells; slot++)
        {
            if (v._slotUsed[slot]) continue;
            v._slotUsed[slot] = true;
            v._shown.Add(new Shown { cell = cell, frame = Time.frameCount, slot = slot });
            return;
        }
    }

    /// <summary>The shown cell whose nucleus is under a screen point: within its size on screen, or 'minPixels'.</summary>
    public CellInterior NucleusAt(Camera cam, Vector2 pointer, float minPixels)
    {
        if (!cam) return null;
        CellInterior best = null;
        float bestD = float.MaxValue;
        foreach (Shown s in _shown)
        {
            if (!s.cell || !s.cell.HasNucleus || s.appear < 0.5f) continue;
            Vector3 c = cam.WorldToScreenPoint(s.centre);
            if (c.z <= 0f) continue;
            float reach = Mathf.Max(minPixels, Vector2.Distance(c, cam.WorldToScreenPoint(s.centre + cam.transform.right * (s.unit * nucleusSize))));
            float d = Vector2.Distance(c, pointer);
            if (d <= reach && d < bestD) { bestD = d; best = s.cell; }
        }
        return best;
    }

    /// <summary>Where a shown cell's nucleus is (world) and its radius; false if it isn't shown.</summary>
    public bool Nucleus(CellInterior cell, out Vector3 centre, out float radius)
    {
        foreach (Shown s in _shown)
            if (s.cell == cell)
            {
                centre = s.centre;
                radius = s.unit * nucleusSize;
                return true;
            }
        centre = default;
        radius = 0f;
        return false;
    }

    void LateUpdate()
    {
        int frame = Time.frameCount;
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            Shown s = _shown[i];
            if (s.cell && s.frame >= frame - 1) continue;
            _slotUsed[s.slot] = false;
            ClearMotes(s.slot);
            _shown.RemoveAt(i);
        }
        if (_shown.Count == 0) return;
        if (!Materials() || !Buffers()) return;

        _bodyCount = _waveCount = 0;
        float dt = Time.deltaTime, k = 1f - Mathf.Exp(-dt / 0.15f);
        var bounds = new Bounds();
        bool any = false;
        for (int i = 0; i < _shown.Count; i++)
        {
            Shown s = _shown[i];
            s.appear = Mathf.Min(1f, s.appear + dt / appearTime);
            s.highlight += ((Opened == s.cell ? 1f : Hovered == s.cell ? 0.7f : 0f) - s.highlight) * k;
            if (!FrameOf(s, out Matrix4x4 toFrame, out MeshFilter mesh, out Matrix4x4 meshToFrame)) continue;
            Homes(s, mesh, meshToFrame);
            Lay(s, toFrame);
            var b = new Bounds(s.centre, Vector3.one * s.unit * 3f);
            if (!any) bounds = b; else bounds.Encapsulate(b);
            any = true;
        }
        if (any) Draw(bounds);
    }

    // The cell's frame from its mesh bounds; 'toFrame' maps cell-local directions into frame coordinates,
    // 'meshToFrame' the mesh's own points.
    bool FrameOf(Shown s, out Matrix4x4 toFrame, out MeshFilter mf, out Matrix4x4 meshToFrame)
    {
        toFrame = meshToFrame = Matrix4x4.identity;
        mf = s.cell.GetComponentInChildren<MeshFilter>();
        if (!mf || !mf.sharedMesh) return false;
        Transform t = mf.transform;
        Bounds b = mf.sharedMesh.bounds;
        Vector3 e = b.extents;
        int thin = e.x <= e.y && e.x <= e.z ? 0 : e.y <= e.z ? 1 : 2;
        int a1 = (thin + 1) % 3, a2 = (thin + 2) % 3;
        if (e[a2] > e[a1]) (a1, a2) = (a2, a1);
        Vector3 ax = t.TransformVector(Axis(a1) * (e[a1] * inset));
        Vector3 ay = t.TransformVector(Axis(a2) * (e[a2] * inset));
        Vector3 az = t.TransformVector(Axis(thin) * (e[thin] * thinInset));
        s.centre = t.TransformPoint(b.center);
        s.unit = ax.magnitude;
        _frames[s.slot] = new Frame
        {
            centre = new Vector4(s.centre.x, s.centre.y, s.centre.z, s.appear),
            axisX = new Vector4(ax.x, ax.y, ax.z, s.unit),
            axisY = new Vector4(ay.x, ay.y, ay.z, s.highlight),
            axisZ = new Vector4(az.x, az.y, az.z, HoneyCount(s)), // w: honey motes (+0.25: nucleus; Draw keeps it current)
        };
        // Mesh-local -> per-axis over the frame's extents; cell-local directions go through the mesh's space first.
        Matrix4x4 pick = Matrix4x4.zero;
        pick[0, a1] = 1f / Mathf.Max(e[a1] * inset, 1e-5f);
        pick[1, a2] = 1f / Mathf.Max(e[a2] * inset, 1e-5f);
        pick[2, thin] = 1f / Mathf.Max(e[thin] * thinInset, 1e-5f);
        pick[3, 3] = 1f;
        toFrame = pick * (t.worldToLocalMatrix * s.cell.transform.localToWorldMatrix);
        meshToFrame = pick * Matrix4x4.Translate(-b.center);
        return true;
    }

    static Vector3 Axis(int i) => i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;

    // One cell's bodies, rings and motes, from its data.
    void Lay(Shown s, Matrix4x4 toFrame)
    {
        CellInterior cell = s.cell;
        uint seed = (uint)cell.Seed;
        int frame = s.slot;

        if (cell.HasNucleus)
            AddBody(new Body
            {
                position = new Vector4(0f, 0f, 0f, nucleusSize),
                color = new Vector4(nucleusColor.r, nucleusColor.g, nucleusColor.b, 0f),
                info = new Vector4(0f, NucleusSeed(seed), frame, 1f),
                axis = new Vector4(1f, 0f, 0f, 0f),
            });

        // Organelles on a golden-angle spiral round the nucleus, each at the resting spot nearest its place on it
        // (so they sit inside the mesh's real shape).
        float turn = Rand(seed, 2u) * Mathf.PI * 2f;
        int j = 0, firstWave = _waveCount;
        for (int g = 0; g < cell.organelles.Count; g++)
        {
            CellInterior.Organelles group = cell.organelles[g];
            OrganelleType type = group.type;
            if (!type) continue;
            for (int n = 0; n < group.count && j < MaxBodies - 1; n++, j++)
            {
                uint h = (uint)j * 16u + 100u;
                float angle = turn + j * 2.39996f + (Rand(seed, h) - 0.5f) * 0.5f;
                float r = 0.5f + 0.2f * Rand(seed, h + 1u);
                var pos = new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, (Rand(seed, h + 2u) - 0.5f) * 0.5f);
                pos = s.motes.Snap(pos, type.size * 1.2f);
                float lean = angle + Mathf.PI * 0.5f + (Rand(seed, h + 3u) - 0.5f) * 1f;
                float bodySeed = Rand(seed, h + 4u);
                bool mito = type.shape == OrganelleType.Shape.Mitochondrion;
                AddBody(new Body
                {
                    position = new Vector4(pos.x, pos.y, pos.z, type.size),
                    color = new Vector4(type.color.r, type.color.g, type.color.b, group.activity),
                    info = new Vector4(1f + (float)type.shape, bodySeed, frame, mito ? 2.1f : 1f),
                    axis = new Vector4(Mathf.Cos(lean), Mathf.Sin(lean), 0f, mito ? (Rand(seed, h + 5u) - 0.5f) * 0.5f : 0f),
                });
                _placed[j] = new Placed { type = type, activity = group.activity, at = Pack(pos, bodySeed), group = g };
                // What it makes rings out of it as it works.
                if (type.outputs.Length > 0 && _waveCount < MaxWaves)
                {
                    Substance sub = SubstanceCatalog.Find(type.outputs[0].substance);
                    bool stream = sub.look == SubstanceLook.Honey && cell.HasNucleus;
                    _waves[_waveCount++] = new Wave
                    {
                        from = Pack(pos, bodySeed),
                        to = Pack(PoolPoint(pos), stream ? 1f : 0f),
                        color = new Vector4(sub.color.r, sub.color.g, sub.color.b, group.activity),
                        info = new Vector4(0f, 0f, frame, Rand(seed, h + 14u)),
                        shape = new Vector4(0f, 0.55f, waveReach, 0f),
                    };
                }
            }
        }

        UpdateMotes(s, j, toFrame);
        StreamSpans(s, firstWave);
    }

    static float NucleusSeed(uint seed) => Rand(seed, 1u);

    static Vector4 Pack(Vector3 p, float w) => new Vector4(p.x, p.y, p.z, w);

    // 0..1 from the cell's seed and an index (the same hash on every run: the layout never shuffles).
    static float Rand(uint seed, uint i)
    {
        uint v = seed ^ (i * 0x9e3779b9u);
        v = (v ^ (v >> 16)) * 0x7feb352du;
        v = (v ^ (v >> 15)) * 0x846ca68bu;
        v ^= v >> 16;
        return (v & 0xffffff) / 16777216f;
    }

    void AddBody(Body b) { if (_bodyCount < MaxBodies) _bodies[_bodyCount++] = b; }

    bool Buffers()
    {
        if (_frameBuffer != null && _frameBuffer.IsValid()) return true;
        _frameBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxCells, FrameStride);
        _bodyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxBodies, BodyStride);
        _waveBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxWaves, WaveStride);
        _moteBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxCells * MotesPerCell, MoteStride);
        _moteBuffer.SetData(new Mote[MaxCells * MotesPerCell]); // all out
        _honeyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxCells * MotesPerCell, sizeof(uint));
        foreach (Shown s in _shown) s.motes.dirty = true;
        return true;
    }

    void Draw(Bounds bounds)
    {
        _moteProps ??= new MaterialPropertyBlock();
        _waveProps ??= new MaterialPropertyBlock();
        _streamProps ??= new MaterialPropertyBlock();

        foreach (Shown s in _shown)
        {
            if (s.motes.dirty)
            {
                _moteBuffer.SetData(s.motes.gpu, 0, s.slot * MotesPerCell, MotesPerCell);
                HoneyList(s);
                if (s.motes.honeyCount > 0) _honeyBuffer.SetData(s.motes.honey, 0, s.slot * MotesPerCell, s.motes.honeyCount);
                s.motes.dirty = false;
            }
            _frames[s.slot].axisZ.w = HoneyCount(s);
        }
        _frameBuffer.SetData(_frames);
        if (_bodyCount > 0) _bodyBuffer.SetData(_bodies, 0, 0, _bodyCount);
        if (_waveCount > 0) _waveBuffer.SetData(_waves, 0, 0, _waveCount);

        Bind(_moteProps, 0);
        Bind(_waveProps, 1);
        Bind(_streamProps, 2);
        Graphics.RenderPrimitives(Params(_moteMat, _moteProps, bounds), MeshTopology.Triangles, 6, MaxCells * MotesPerCell);
        if (_waveCount > 0) // honey streams: with the motes (same field), under the bodies
            Graphics.RenderPrimitives(Params(_moteMat, _streamProps, bounds), MeshTopology.Triangles, 6, _waveCount);
        if (_bodyCount > 0)
            Graphics.RenderMeshPrimitives(Params(_bodyMat, _moteProps, bounds), ChunkMesh.Ball(), 0, _bodyCount);
        if (_waveCount > 0)
            Graphics.RenderPrimitives(Params(_waveMat, _waveProps, bounds), MeshTopology.Triangles, RingsPerWave * 6, _waveCount);
    }

    void Bind(MaterialPropertyBlock p, int mode)
    {
        p.SetBuffer(FramesId, _frameBuffer);
        p.SetBuffer(BodiesId, _bodyBuffer);
        p.SetBuffer(WavesId, _waveBuffer);
        p.SetBuffer(MotesId, _moteBuffer);
        p.SetBuffer(HoneyId, _honeyBuffer);
        p.SetInteger(ModeId, mode);
        p.SetInteger(WaveCountId, _waveCount);
        p.SetFloat(NucleusSizeId, nucleusSize);
    }

    static float HoneyCount(Shown s) => s.motes.honeyCount + (s.cell && s.cell.HasNucleus ? 0.25f : 0f);

    static RenderParams Params(Material m, MaterialPropertyBlock p, Bounds b) =>
        new RenderParams(m) { worldBounds = b, matProps = p, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };

    bool Materials()
    {
        if (_bodyMat && _moteMat && _waveMat) return true;
        if (!bodyShader) bodyShader = Shader.Find("Custom/CellInterior");
        if (!moteShader) moteShader = Shader.Find("Custom/CellInteriorMotes");
        if (!bodyShader || !moteShader) return false;
        _bodyMat = new Material(bodyShader) { name = "Cell Interior", hideFlags = HideFlags.DontSave };
        // Motes under the bodies (swallowed into them), rings over them (both over the focus sweep at Overlay).
        _moteMat = new Material(moteShader) { name = "Cell Motes", hideFlags = HideFlags.DontSave, renderQueue = (int)RenderQueue.Overlay + 6 };
        _waveMat = new Material(moteShader) { name = "Cell Rings", hideFlags = HideFlags.DontSave, renderQueue = (int)RenderQueue.Overlay + 8 };
        return true;
    }

    void OnDestroy()
    {
        _frameBuffer?.Release();
        _bodyBuffer?.Release();
        _waveBuffer?.Release();
        _moteBuffer?.Release();
        _honeyBuffer?.Release();
        if (_bodyMat) Destroy(_bodyMat);
        if (_moteMat) Destroy(_moteMat);
        if (_waveMat) Destroy(_waveMat);
        if (s_instance == this) s_instance = null;
    }
}

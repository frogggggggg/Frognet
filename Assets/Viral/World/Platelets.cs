using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Platelets: little spiky stars (activated platelets) zooming along the vessel's outer layers, near the wall.
/// Pure scenery, entirely on the GPU: nothing per platelet lives on the CPU. Each platelet is a seed; a compute
/// pass (Platelets.compute) places it in tube coordinates from that seed and the vessel clock, culls it (draw
/// distance, frustum, pixels) and lists it for one of two indirect draws (near / far mesh). The shader shapes every
/// platelet from its seed in the vertex stage: a lumpy body with up to <see cref="Spikes"/> tapering, bent, slowly
/// waving tendrils, some missing, so no two look alike from one shared mesh.
///
/// The field is endless: platelets wrap in a window round the camera, in loop angle (phi) and round the tube
/// (theta), each window a whole fraction of a turn so the copies meet seamlessly. They ride the blood: carried at the
/// flow's rate for their depth (<see cref="Vessel.Flow"/>'s profile, minus the frame's turn) plus their own
/// <see cref="speed"/> downstream, so they zoom past in the wall layers. Rates are quantized so each covers a whole
/// number of windows per <see cref="Period"/> seconds: positions come from (clock mod period) with no float creep.
///
/// Fading follows the far field's rules exactly (its distance, edge fade, least pixels and thinning; FarField.compute),
/// so platelets go the way every other far object goes. Pixel size is measured on the body: tendrils are mostly air.
///
/// Cost: one compute thread per platelet per frame (density x window area: ~170k at the far field's 1600 m), two
/// indirect draws; vertices only for the visible ones (near mesh ~400 verts, far ~120), which the pixel and thinning
/// rules keep to a few thousand. Nothing at all while the camera is farther than the draw distance from the platelet
/// band (the middle of the tube), in focus mode or without a Vessel.
/// </summary>
public class Platelets : MonoBehaviour
{
    /// <summary>Tendrils per platelet in the mesh (the shader's SPIKES); some are dropped per platelet.</summary>
    public const int Spikes = 8;
    /// <summary>Seconds after which the field repeats exactly (rates are quantized to it).</summary>
    const double Period = 4096.0;

    [Tooltip("Custom/Platelets. Filled from the prefab / ViralBuildAssets.")]
    public Shader shader;
    [Tooltip("Platelets.compute: places and culls them.")]
    public ComputeShader place;

    [Header("Field")]
    [Min(0f), Tooltip("Platelets per 1000 square metres of their band, seen face on (on the loop's inner side; its outer " +
                      "side is up to ~2x sparser). The count follows from this and the window, which follows from Distance.")]
    public float density = 4.8f;
    [Tooltip("Depth range in from the nominal wall (metres). The wall's folds + relief stand ~110 m in.")]
    public Vector2 depth = new Vector2(115f, 380f);
    [Min(1f), Tooltip("Above 1, more of them hug the wall.")]
    public float depthBias = 1.6f;

    [Header("Look")]
    [Tooltip("Body radius (metres); tendrils reach ~4x that.")]
    public Vector2 size = new Vector2(0.9f, 2.2f);
    public Color color = new Color(0.97f, 0.74f, 0.6f, 1f);
    public Color shade = new Color(0.72f, 0.4f, 0.34f, 1f);
    [Range(0f, 2f)] public float rim = 0.45f;

    [Header("Motion")]
    [Tooltip("Own speed downstream relative to the blood (m/s): what makes them zoom past.")]
    public Vector2 speed = new Vector2(10f, 35f);
    [Min(0f), Tooltip("Lazy weave across their lane (metres).")]
    public float weave = 4f;
    [Tooltip("Tumble rate (rad/s).")]
    public Vector2 tumble = new Vector2(0.2f, 1.2f);
    [Min(0f), Tooltip("How much the tendrils sway.")]
    public float sway = 0.25f;

    [Header("Fade: the far field's rules")]
    [Tooltip("Take Distance, Edge Fade, Min Pixels and the thinning from the FarField beside this, so platelets fade " +
             "out exactly like every other far object (with their own rules they looked different).")]
    public bool matchFarField = true;
    [Min(20f), Tooltip("Drawn out to this (metres). Matched: FarField.distance.")]
    public float distance = 1600f;
    [Min(1f), Tooltip("Faded out over this many metres before Distance. Matched: FarField.edgeFade.")]
    public float edgeFade = 350f;
    [Min(0.1f), Tooltip("Smaller than this on screen (pixels across the body; tendrils left out, they're mostly air) " +
                        "and it isn't drawn; fades in from here to twice. Matched: FarField.minPixels.")]
    public float minPixels = 2f;
    [Min(0f), Tooltip("Past this only a shrinking share is drawn, reaching Thin Keep at Distance. Matched.")]
    public float thinStart = 500f;
    [Range(0f, 1f)] public float thinKeep = 0.25f;
    [Min(1f), Tooltip("Below this many pixels (across the tendrils' reach) the light mesh is used.")]
    public float lodPixels = 40f;

    const float Reach = 4.4f; // tendril reach in body radii (frustum bounds, LOD)
    const float Solid = 1.4f; // the body with its bulges, in body radii (what pixel size and fading go by)
    const int MaxCount = 262144;

    static Mesh s_near, s_far;
    Material _material;
    MaterialPropertyBlock _nearProps, _farProps;
    GraphicsBuffer _posed, _visible, _args;
    uint[] _argsData;
    int _capacity, _kernel = -1, _count;
    FarField _far;
    readonly Plane[] _planes = new Plane[6];
    readonly Vector4[] _planeVectors = new Vector4[6];
    Camera _camera;
    VirusMovement _player;
    float _nextFind;
    bool _broken;

    static readonly int PosedId = Shader.PropertyToID("_Posed"), VisibleId = Shader.PropertyToID("_Visible"),
                        ArgsId = Shader.PropertyToID("_Args"), GroupBaseId = Shader.PropertyToID("_GroupBase"),
                        CountId = Shader.PropertyToID("_Count"), PlanesId = Shader.PropertyToID("_Planes"),
                        CameraPosId = Shader.PropertyToID("_CameraPos"), VesselCId = Shader.PropertyToID("_VesselC"),
                        VesselE1Id = Shader.PropertyToID("_VesselE1"), VesselE2Id = Shader.PropertyToID("_VesselE2"),
                        VesselAId = Shader.PropertyToID("_VesselA"), WindowId = Shader.PropertyToID("_Window"),
                        PhaseId = Shader.PropertyToID("_Phase"), CamTubeId = Shader.PropertyToID("_CamTube"),
                        FlowId = Shader.PropertyToID("_Flow"), BandId = Shader.PropertyToID("_Band"),
                        ShapeId = Shader.PropertyToID("_Shape"), TumbleId = Shader.PropertyToID("_Tumble"),
                        RangeId = Shader.PropertyToID("_Range"), ThinId = Shader.PropertyToID("_Thin"), RadiusTexId = Shader.PropertyToID("_RadiusTex"),
                        ColorId = Shader.PropertyToID("_Color"), DeepColorId = Shader.PropertyToID("_DeepColor"),
                        RimId = Shader.PropertyToID("_Rim"), SwayId = Shader.PropertyToID("_Sway");

    void OnDisable() => Release();

    void OnDestroy()
    {
        Release();
        if (_material) Destroy(_material);
    }

    void Release()
    {
        _posed?.Release();
        _visible?.Release();
        _args?.Release();
        _posed = _visible = _args = null;
        _capacity = 0;
    }

    void LateUpdate()
    {
        Vessel vessel = Vessel.Active;
        if (!vessel || density <= 0f || !vessel.RadiusTexture) return;
        if (!_camera || !_camera.isActiveAndEnabled) _camera = Camera.main;
        if (!_camera || _camera.orthographic) return;
        if (!_player && Time.unscaledTime >= _nextFind)
        {
            _nextFind = Time.unscaledTime + 1f;
            _player = FindAnyObjectByType<VirusMovement>();
        }
        if (_player && _player.IsFocusMode) return;

        // The far field's fade rules, so platelets thin and fade out like everything else.
        if (matchFarField && !_far) _far = GetComponent<FarField>();
        bool match = matchFarField && _far && _far.On;
        float reachOut = match ? _far.distance : distance, edge = match ? _far.edgeFade : edgeFade;
        float least = match ? _far.minPixels : minPixels;
        float thin0 = match ? _far.thinStart : thinStart, keepAt = match ? _far.thinKeep : thinKeep;

        // Nothing in reach: the camera is deep in the middle of the tube.
        Vector3 cam = _camera.transform.position;
        Vessel.Tube tube = vessel.ToTube(cam);
        float a = vessel.RadiusAt(tube.S);
        float far = Mathf.Max(depth.x, depth.y) + weave;
        if (a - tube.r > reachOut + far) return;

        // Windows: whole fractions of a turn, at least 2.5x the reach across (on the loop's inner side), so the
        // window's edge fade (its outer fifth) starts past the draw distance. Count = density x the window's area.
        float R = vessel.LoopRadius, window = reachOut * 2.5f;
        float inner = Mathf.Max(R - vessel.MaxRadius, 100f);
        float winPhi = 2f * Mathf.PI / Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * inner / window));
        float lane = Mathf.Max(vessel.radius - (depth.x + depth.y) * 0.5f, 50f);
        float winTheta = 2f * Mathf.PI / Mathf.Max(1, Mathf.FloorToInt(2f * Mathf.PI * lane / window));
        _count = Mathf.Clamp(Mathf.RoundToInt(density * 0.001f * inner * winPhi * lane * winTheta), 0, MaxCount);
        if (_count == 0 || !Ready()) return;
        double tm = Repeat(Vessel.Clock, Period) / Period;
        double c = Repeat((-Vessel.FrameAngle - tube.phi) / winPhi, 1.0);
        float th = Mathf.Repeat(tube.theta / winTheta, 1f);

        if (_argsData == null || _argsData.Length != 10) _argsData = new uint[10];
        _argsData[0] = s_near.GetIndexCount(0);
        _argsData[5] = s_far.GetIndexCount(0);
        _argsData[1] = _argsData[6] = 0u;
        _args.SetData(_argsData);

        GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
        for (int i = 0; i < 6; i++) _planeVectors[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);
        float pixelScale = _camera.pixelHeight / (2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
        Vector3 C = vessel.LoopCentre, e1 = vessel.E1, e2 = vessel.E2, ax = vessel.Axis;

        place.SetInt(CountId, _count);
        place.SetVectorArray(PlanesId, _planeVectors);
        place.SetVector(CameraPosId, new Vector4(cam.x, cam.y, cam.z, pixelScale));
        place.SetVector(VesselCId, new Vector4(C.x, C.y, C.z, R));
        place.SetVector(VesselE1Id, new Vector4(e1.x, e1.y, e1.z, vessel.circumference));
        place.SetVector(VesselE2Id, new Vector4(e2.x, e2.y, e2.z, vessel.WallOffset));
        place.SetVector(VesselAId, new Vector4(ax.x, ax.y, ax.z, vessel.radius));
        place.SetVector(WindowId, new Vector4(winPhi, winTheta, (float)Period, 0f));
        place.SetVector(PhaseId, new Vector4((float)tm, (float)c, th, Time.time));
        place.SetVector(CamTubeId, new Vector4(tube.phi, tube.theta, 0f, 0f));
        place.SetVector(FlowId, new Vector4(vessel.centreSpeed / R, vessel.profileExponent, 0f, 0f));
        place.SetVector(BandId, new Vector4(Mathf.Min(depth.x, depth.y), Mathf.Max(depth.x, depth.y), depthBias, weave));
        place.SetVector(ShapeId, new Vector4(Mathf.Min(size.x, size.y), Mathf.Max(size.x, size.y),
                                             Mathf.Min(speed.x, speed.y), Mathf.Max(speed.x, speed.y)));
        place.SetVector(TumbleId, new Vector4(Mathf.Min(tumble.x, tumble.y), Mathf.Max(tumble.x, tumble.y), Reach, Solid));
        place.SetVector(RangeId, new Vector4(reachOut, least, lodPixels, edge));
        place.SetVector(ThinId, new Vector4(thin0, 1f / Mathf.Max(reachOut - thin0, 1f), keepAt, 0.08f));
        place.SetTexture(_kernel, RadiusTexId, vessel.RadiusTexture);
        place.SetBuffer(_kernel, PosedId, _posed);
        place.SetBuffer(_kernel, VisibleId, _visible);
        place.SetBuffer(_kernel, ArgsId, _args);
        place.Dispatch(_kernel, (_count + 63) / 64, 1, 1);

        _material.SetColor(ColorId, color);
        _material.SetColor(DeepColorId, shade);
        _material.SetFloat(RimId, rim);
        _material.SetFloat(SwayId, sway);
        var bounds = new Bounds(cam, Vector3.one * (reachOut * 2f + 50f));
        Graphics.DrawMeshInstancedIndirect(s_near, 0, _material, bounds, _args, 0, _nearProps, ShadowCastingMode.Off, false, 0, _camera);
        Graphics.DrawMeshInstancedIndirect(s_far, 0, _material, bounds, _args, 5 * sizeof(uint), _farProps, ShadowCastingMode.Off, false, 0, _camera);
    }

    // Everything made lazily and remade if a play-mode script reload wiped the plain fields.
    bool Ready()
    {
        if (_broken) return false;
        if (!_material || _kernel < 0)
        {
            if (!shader) shader = Shader.Find("Custom/Platelets");
            if (!place && ViralBuildAssets.Instance) place = ViralBuildAssets.Instance.platelets;
            if (!shader || !place || !SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning("Platelets: no shader / compute shader (or no compute support); platelets are off.", this);
                _broken = true;
                return false;
            }
            _kernel = place.FindKernel("Place");
            if (!_material) _material = new Material(shader) { name = "Platelets", hideFlags = HideFlags.DontSave, enableInstancing = true };
        }
        if (!place.IsSupported(_kernel)) return false; // failed to compile (the import logs why)
        if (!s_near) s_near = BuildMesh("Platelet", 2, 6, new[] { 0f, 0.3f, 0.6f, 0.85f });
        if (!s_far) s_far = BuildMesh("Platelet (far)", 1, 3, new[] { 0f, 0.5f });
        _nearProps ??= new MaterialPropertyBlock();
        _farProps ??= new MaterialPropertyBlock();
        if (_posed == null || _capacity != _count)
        {
            Release();
            _capacity = _count;
            _posed = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count, sizeof(float) * 12);
            _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _count * 2, sizeof(uint));
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 10, sizeof(uint));
            _material.SetBuffer(PosedId, _posed);
            _material.SetBuffer(VisibleId, _visible);
            _nearProps.SetInteger(GroupBaseId, 0);
            _farProps.SetInteger(GroupBaseId, _count);
        }
        return true;
    }

    static double Repeat(double x, double m) => x - System.Math.Floor(x / m) * m;

    // The template every platelet is shaped from (Custom/Platelets): a unit icosphere body (uv 0) plus Spikes
    // tendrils, each `sides` round at the given rings and closed by a tip vertex. A tendril vertex holds its ring
    // offset in position.xy (tip: z = 1) and (tendril + 1, t along it) in uv; the shader lays it on the tendril.
    static Mesh BuildMesh(string name, int levels, int sides, float[] rings)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        CellShards.Icosphere(v, t, levels);
        var n = new List<Vector3>(v);
        var uv = new List<Vector2>(v.Count);
        for (int i = 0; i < v.Count; i++) uv.Add(Vector2.zero);

        for (int k = 0; k < Spikes; k++)
        {
            int start = v.Count;
            for (int j = 0; j < rings.Length; j++)
                for (int s = 0; s < sides; s++)
                {
                    float a = s * 2f * Mathf.PI / sides;
                    var p = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(p);
                    n.Add(p);
                    uv.Add(new Vector2(k + 1, rings[j]));
                }
            int tip = v.Count;
            v.Add(new Vector3(0f, 0f, 1f));
            n.Add(new Vector3(0f, 0f, 1f));
            uv.Add(new Vector2(k + 1, 1f));

            // Unity's front faces: ring offsets turn from N1 toward N2 = T x N1 (the shader keeps that handedness).
            for (int j = 0; j + 1 < rings.Length; j++)
                for (int s = 0; s < sides; s++)
                {
                    int s1 = (s + 1) % sides;
                    int a0 = start + j * sides + s, b0 = start + (j + 1) * sides + s;
                    int a1 = start + j * sides + s1, b1 = start + (j + 1) * sides + s1;
                    t.Add(a0); t.Add(b0); t.Add(a1);
                    t.Add(b0); t.Add(b1); t.Add(a1);
                }
            int last = start + (rings.Length - 1) * sides;
            for (int s = 0; s < sides; s++)
            {
                t.Add(last + s); t.Add(tip); t.Add(last + (s + 1) % sides);
            }
        }

        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.SetVertices(v);
        mesh.SetNormals(n);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(t, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (Reach * 2f)); // drawn with explicit world bounds
        return mesh;
    }
}

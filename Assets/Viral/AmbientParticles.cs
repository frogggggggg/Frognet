using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Specks suspended in the fluid around a camera, smeared along the player's motion
/// to give fast flight a sense of speed. They fade in only while the player moves
/// faster than a set speed and aren't drawn at all below it. Driven by the player
/// rather than the camera, so orbiting or zooming the camera doesn't set them off.
///
/// Entirely on the GPU: one static mesh of quads, one draw call, no per-speck CPU
/// work. The shader wraps every speck into a box that follows the camera, so the
/// field is endless without spawning, and fades them near the box faces so the wrap
/// never shows. All look settings live here and are pushed per draw.
/// </summary>
public class AmbientParticles : MonoBehaviour
{
    [Tooltip("Camera the field surrounds. Empty: a camera on this object, else the main camera.")]
    public Camera target;
    [Tooltip("Whose speed shows and smears the specks (the player). Uses its Rigidbody velocity while dynamic, its movement otherwise. Empty: the camera's own motion.")]
    public Transform speedSource;
    [Tooltip("Hidden/AmbientParticles. Filled in automatically when the component is added; keep it assigned for builds.")]
    public Shader shader;

    [Header("Field")]
    [Range(16, 20000)] public int count = 2500;
    [Min(1f), Tooltip("Side of the box around the camera the specks fill and wrap within.")]
    public float boxSize = 24f;
    [Min(0f), Tooltip("Specks closer than this to the lens fade out.")]
    public float nearFade = 0.4f;
    [Min(0.01f), Tooltip("How gently specks fade into surfaces behind them.")]
    public float softDistance = 0.4f;
    [Tooltip("Layer the specks draw on. TransparentFX by default, so depth-stamping features can leave them out.")]
    public int layer = 1;

    [Header("Specks")]
    [Tooltip("Smallest and largest radius.")]
    public Vector2 size = new Vector2(0.006f, 0.06f);
    [Min(1f), Tooltip("Higher = more tiny specks, fewer big hazy ones.")]
    public float sizeBias = 5f;
    public Color speckColor = new Color(0.55f, 0.04f, 0.05f, 0.9f);
    public Color plasmaColor = new Color(0.95f, 0.62f, 0.5f, 0.45f);
    [Range(0f, 1f), Tooltip("Share of specks using the plasma colour.")]
    public float plasmaFraction = 0.25f;
    [Range(0f, 1f)] public float opacity = 0.7f;

    [Header("Motion")]
    [Min(0f), Tooltip("Speed the specks drift on their own (units/s).")]
    public float drift = 0.15f;
    [Min(0f), Tooltip("Size of their lazy wobble.")]
    public float wobble = 0.08f;
    [Min(0f), Tooltip("Seconds of motion each speck smears across. More = stronger speed streaks.")]
    public float streakTime = 0.05f;
    [Min(0f), Tooltip("Longest a streak can get (units), so teleports and cuts don't draw lines across the screen.")]
    public float maxStreak = 2f;
    [Min(0f), Tooltip("How quickly the smear follows changes in speed.")]
    public float velocityResponse = 15f;

    [Header("Visibility")]
    [Tooltip("Player speed (units/s) where the specks start fading in, and where they're fully shown. Below the first they aren't drawn at all.")]
    public Vector2 visibleSpeed = new Vector2(10f, 20f);
    [Min(0.01f), Tooltip("Seconds to fade in once fast enough.")]
    public float fadeIn = 0.15f;
    [Min(0.01f), Tooltip("Seconds to fade out after slowing down.")]
    public float fadeOut = 0.5f;

    Mesh _mesh;
    Material _material;
    MaterialPropertyBlock _props;
    int _builtCount;
    Vector3 _lastPos, _velocity, _flow, _flowOffset;
    Rigidbody _body;
    Transform _bodyOf;
    bool _searched;
    float _visible;
    bool _seeded;

    static readonly int BoxId = Shader.PropertyToID("_Box"), NearFadeId = Shader.PropertyToID("_NearFade"),
                        SoftId = Shader.PropertyToID("_SoftDistance"), SizeMinId = Shader.PropertyToID("_SizeMin"),
                        SizeMaxId = Shader.PropertyToID("_SizeMax"), SizePowerId = Shader.PropertyToID("_SizePower"),
                        DriftId = Shader.PropertyToID("_Drift"), WobbleId = Shader.PropertyToID("_Wobble"),
                        StreakId = Shader.PropertyToID("_StreakTime"), MaxStreakId = Shader.PropertyToID("_MaxStreak"),
                        OpacityId = Shader.PropertyToID("_Opacity"), PlasmaFractionId = Shader.PropertyToID("_PlasmaFraction"),
                        SpeckColorId = Shader.PropertyToID("_SpeckColor"), PlasmaColorId = Shader.PropertyToID("_PlasmaColor"),
                        CamVelId = Shader.PropertyToID("_CamVel"), FlowOffsetId = Shader.PropertyToID("_FlowOffset"),
                        FlowVelId = Shader.PropertyToID("_FlowVel");

    void Reset()
    {
        shader = Shader.Find("Hidden/AmbientParticles");
        target = GetComponent<Camera>();
        VirusMovement player = FindAnyObjectByType<VirusMovement>();
        if (player) speedSource = player.transform;
    }

    void OnDisable()
    {
        _seeded = false;
        _visible = 0f;
    }

    void OnDestroy()
    {
        if (_mesh) Destroy(_mesh);
        if (_material) Destroy(_material);
    }

    void LateUpdate()
    {
        Camera cam = Target();
        if (!cam || !Ready()) return;

        // The player's velocity (the camera's without one), smoothed: this shows and smears the specks.
        float dt = Time.deltaTime;
        Vector3 pos = cam.transform.position;
        if (!speedSource && !_searched)
        {
            _searched = true; // once: added before the player-speed option existed
            VirusMovement player = FindAnyObjectByType<VirusMovement>();
            if (player) speedSource = player.transform;
        }
        Transform src = speedSource ? speedSource : cam.transform;
        if (_bodyOf != src)
        {
            _bodyOf = src;
            _body = src.GetComponentInParent<Rigidbody>();
            _seeded = false;
        }

        Vector3 srcPos = src.position;
        if (dt > 0f)
        {
            Vector3 raw = _body && !_body.isKinematic ? _body.linearVelocity
                        : _seeded ? (srcPos - _lastPos) / dt : Vector3.zero;
            _velocity = Vector3.Lerp(_velocity, raw, 1f - Mathf.Exp(-velocityResponse * dt));
        }
        _lastPos = srcPos;
        _seeded = true;

        // The specks ride the blood (Vessel): carried by its flow round the camera, wrapped in the box.
        _flow = Vessel.FlowAt(pos);
        _flowOffset += _flow * dt;
        _flowOffset = new Vector3(Mathf.Repeat(_flowOffset.x, boxSize), Mathf.Repeat(_flowOffset.y, boxSize), Mathf.Repeat(_flowOffset.z, boxSize));

        // Only while moving fast through the blood (not with it); nothing is drawn at all otherwise.
        float through = (_velocity - _flow).magnitude;
        float lo = Mathf.Min(visibleSpeed.x, visibleSpeed.y), hi = Mathf.Max(visibleSpeed.x, visibleSpeed.y);
        float goal = hi > lo ? Mathf.Clamp01((through - lo) / (hi - lo)) : through >= lo ? 1f : 0f;
        goal = goal * goal * (3f - 2f * goal);
        _visible = Mathf.MoveTowards(_visible, goal, dt / (goal > _visible ? fadeIn : fadeOut));
        if (_visible <= 0.001f) return;

        _props.SetFloat(BoxId, boxSize);
        _props.SetFloat(NearFadeId, nearFade);
        _props.SetFloat(SoftId, softDistance);
        _props.SetFloat(SizeMinId, Mathf.Min(size.x, size.y));
        _props.SetFloat(SizeMaxId, Mathf.Max(size.x, size.y));
        _props.SetFloat(SizePowerId, sizeBias);
        _props.SetFloat(DriftId, drift);
        _props.SetFloat(WobbleId, wobble);
        _props.SetFloat(StreakId, streakTime);
        _props.SetFloat(MaxStreakId, maxStreak);
        _props.SetFloat(OpacityId, opacity * _visible);
        _props.SetFloat(PlasmaFractionId, plasmaFraction);
        _props.SetColor(SpeckColorId, speckColor);
        _props.SetColor(PlasmaColorId, plasmaColor);
        _props.SetVector(CamVelId, _velocity);
        _props.SetVector(FlowOffsetId, _flowOffset);
        _props.SetVector(FlowVelId, _flow);

        var rp = new RenderParams(_material)
        {
            camera = cam,
            layer = layer,
            matProps = _props,
            worldBounds = new Bounds(pos, Vector3.one * (boxSize * 1.5f)),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
        };
        Graphics.RenderMesh(rp, _mesh, 0, Matrix4x4.identity);
    }

    Camera Target()
    {
        if (target) return target;
        Camera own = GetComponent<Camera>();
        return own ? own : Camera.main;
    }

    bool Ready()
    {
        if (!_material)
        {
            Shader s = shader ? shader : Shader.Find("Hidden/AmbientParticles");
            if (!s) return false;
            _material = new Material(s) { hideFlags = HideFlags.DontSave };
        }
        _props ??= new MaterialPropertyBlock(); // separately: a play-mode recompile keeps the material but not this
        if (!_mesh || _builtCount != count) Build();
        return true;
    }

    // One quad per speck: all four corners carry the speck's home (0..1 in the box) and randoms.
    void Build()
    {
        if (!_mesh) _mesh = new Mesh { name = "Ambient Particles", hideFlags = HideFlags.DontSave };
        _mesh.Clear();

        int n = count, v = n * 4;
        var home = new Vector3[v];
        var corner = new Vector2[v];
        var rnd = new Vector2[v];
        var tris = new int[n * 6];
        var random = new System.Random(20260922);
        Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };

        for (int i = 0; i < n; i++)
        {
            var h = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            var r = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
            for (int k = 0; k < 4; k++)
            {
                home[i * 4 + k] = h;
                corner[i * 4 + k] = corners[k];
                rnd[i * 4 + k] = r;
            }

            int b = i * 4, t = i * 6;
            tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
            tris[t + 3] = b; tris[t + 4] = b + 2; tris[t + 5] = b + 3;
        }

        _mesh.indexFormat = v > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.SetVertices(home);
        _mesh.SetUVs(0, corner);
        _mesh.SetUVs(1, rnd);
        _mesh.SetTriangles(tris, 0, false);
        _mesh.bounds = new Bounds(Vector3.one * 0.5f, Vector3.one); // drawn with explicit world bounds
        _builtCount = n;
    }
}

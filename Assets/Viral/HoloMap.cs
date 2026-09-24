using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Hologram map in a screen corner: a small 3D model of the space around the player,
/// seen from the main camera's direction (plus a little extra tilt), so it turns as you
/// look around. Nearby geometry appears as its real shape (whatever the renderers are:
/// cells, cubes, scenery), the player is a bright marker and other viruses are dots, all
/// inside a holographic range sphere.
///
/// Drawn straight into a small render texture each frame with one instanced draw per
/// kind, so a thousand viruses is a thousand instances in one call and no proxy objects
/// exist in the scene. A UI image shows the texture, added over the game after
/// post-processing, so the map is never blurred by depth of field.
///
/// Nothing to set up: one is created when play starts if the scene has none. Add the
/// component yourself (anywhere) to change its settings; that one is used instead.
/// </summary>
public class HoloMap : MonoBehaviour
{
    [Header("Placement")]
    [Min(32f), Tooltip("On-screen size, in pixels.")]
    public float size = 240f;
    [Tooltip("Distance from the top-right corner, in pixels.")]
    public Vector2 margin = new Vector2(24f, 24f);
    [Range(128, 1024), Tooltip("Render texture resolution.")]
    public int resolution = 512;

    [Header("World")]
    [Min(1f), Tooltip("How far around the player the map covers (world units).")]
    public float range = 120f;
    [Tooltip("Layers whose renderers are drawn on the map.")]
    public LayerMask geometryLayers = ~0;
    [Tooltip("Skip anything bigger than this share of the range (a skybox or a ground plane would fill the map).")]
    [Min(0.1f)] public float maxObjectSize = 1.5f;
    [Tooltip("Extra downward tilt over the camera's own angle, so the map reads as 3D.")]
    public float extraTilt = 15f;
    [Range(10f, 80f)] public float fieldOfView = 32f;

    [Header("Look")]
    public Color globeColor = new Color(0.25f, 0.75f, 1f, 0.35f);
    public Color cellColor = new Color(0.35f, 0.9f, 1f, 0.8f);
    public Color playerColor = new Color(0.6f, 1f, 0.9f, 1f);
    public Color agentColor = new Color(1f, 0.45f, 0.4f, 0.9f);
    [Min(0f), Tooltip("Dot size for the player and other viruses, as a share of the map's radius.")]
    public float playerDot = 0.055f, agentDot = 0.03f;
    [Range(0f, 1f), Tooltip("Share of the map's radius where things start fading out, so they drift in instead of popping.")]
    public float edgeFade = 0.7f;
    [Range(0f, 1f), Tooltip("Brightness of the far half of the map, so it reads as depth (1 = flat).")]
    public float depthDim = 0.35f;
    [Range(0f, 3f), Tooltip("Soft bloom around lines and dots.")]
    public float glow = 1.2f;
    [Range(0f, 2f), Tooltip("Brightness of the radar ping sweeping out from the player (0 = off).")]
    public float pulseStrength = 0.6f;
    [Min(0.1f), Tooltip("Seconds between pings.")]
    public float pulseInterval = 3f;
    [Range(0.01f, 0.3f), Tooltip("Thickness of the ping, as a share of the map's radius.")]
    public float pulseWidth = 0.08f;
    [Range(0f, 1f)] public float flicker = 0.06f;

    [Header("Shaders")]
    [Tooltip("Hidden/HoloMap. Filled in when the component is added; keep it assigned for builds.")]
    public Shader modelShader;
    [Tooltip("Hidden/HoloMapScreen.")]
    public Shader screenShader;

    [Header("Refresh")]
    [Min(0.05f), Tooltip("Seconds between rescans for cells and viruses.")]
    public float rescanInterval = 1f;

    const int BatchSize = 1023; // Graphics.DrawMeshInstanced's limit

    static readonly int ColorId = Shader.PropertyToID("_Color"),
                        EyeId = Shader.PropertyToID("_HoloEye"),
                        DepthDimId = Shader.PropertyToID("_HoloDepthDim"),
                        PulseId = Shader.PropertyToID("_HoloPulse"),
                        GlowId = Shader.PropertyToID("_HoloGlow"),
                        FillId = Shader.PropertyToID("_Fill"),
                        FadeStartId = Shader.PropertyToID("_HoloFadeStart"),
                        EdgeFadeId = Shader.PropertyToID("_EdgeFade"),
                        FlickerId = Shader.PropertyToID("_HoloFlicker"),
                        MainTexId = Shader.PropertyToID("_MainTex");

    RenderTexture _texture; // drawn into (antialiased)
    RenderTexture _display; // resolved copy with mips, which the screen shader blurs into bloom
    Material _model, _screen;
    Mesh _sphere;
    CommandBuffer _commands;
    RawImage _image;
    Canvas _canvas;

    // None of this survives a play-mode script reload (Unity keeps the component but not
    // plain C# objects), so Ready() remakes whatever is missing each frame.
    MaterialPropertyBlock _globeProps, _cellProps, _playerProps, _agentProps;
    Matrix4x4[] _batch;
    // Scene geometry to draw, grouped by mesh so each group is one instanced draw.
    class MeshGroup
    {
        public Mesh mesh;
        public readonly List<Transform> transforms = new List<Transform>();
        public readonly List<float> radii = new List<float>();
    }
    List<MeshGroup> _geometry;
    List<Organism> _organisms;
    Organism _player;
    float _nextRescan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<HoloMap>()) return; // the scene has its own, with its own settings
        new GameObject("Holo Map") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<HoloMap>();
    }

    void Reset()
    {
        modelShader = Shader.Find("Hidden/HoloMap");
        screenShader = Shader.Find("Hidden/HoloMapScreen");
    }

    void OnEnable()
    {
        if (!Ready()) enabled = false;
    }

    // Everything the map needs, made or remade.
    bool Ready()
    {
        _globeProps ??= new MaterialPropertyBlock();
        _cellProps ??= new MaterialPropertyBlock();
        _playerProps ??= new MaterialPropertyBlock();
        _agentProps ??= new MaterialPropertyBlock();
        _batch ??= new Matrix4x4[BatchSize];
        _geometry ??= new List<MeshGroup>();
        _organisms ??= new List<Organism>();
        _commands ??= new CommandBuffer { name = "Holo Map" };
        if (!_sphere) _sphere = Sphere();

        return _texture && _display && _model && _screen && _image || Build();
    }

    void OnDisable()
    {
        Discard();
        _commands?.Release();
        _commands = null;
    }

    void Discard()
    {
        if (_canvas) Destroy(_canvas.gameObject);
        if (_texture) _texture.Release();
        if (_display) _display.Release();
        if (_model) Destroy(_model);
        if (_screen) Destroy(_screen);
        _canvas = null;
        _image = null;
        _texture = null;
        _display = null;
        _model = null;
        _screen = null;
    }

    bool Build()
    {
        Discard(); // never leave a second canvas or texture behind

        Shader model = modelShader ? modelShader : Shader.Find("Hidden/HoloMap");
        Shader screen = screenShader ? screenShader : Shader.Find("Hidden/HoloMapScreen");
        if (!model || !screen)
        {
            Debug.LogError("HoloMap: assign Hidden/HoloMap and Hidden/HoloMapScreen.", this);
            return false;
        }

        _model = new Material(model) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
        _screen = new Material(screen) { hideFlags = HideFlags.HideAndDontSave };

        _texture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf)
        {
            name = "Holo Map",
            hideFlags = HideFlags.HideAndDontSave,
            antiAliasing = 2,
        };
        _texture.Create();
        _display = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf)
        {
            name = "Holo Map Display",
            hideFlags = HideFlags.HideAndDontSave,
            useMipMap = true,
            autoGenerateMips = false,
            filterMode = FilterMode.Trilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        _display.Create();
        _screen.SetTexture(MainTexId, _display);

        var canvasObject = new GameObject("Holo Map Canvas") { hideFlags = HideFlags.HideAndDontSave };
        _canvas = canvasObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 500;

        var imageObject = new GameObject("Holo Map Image");
        imageObject.transform.SetParent(canvasObject.transform, false);
        _image = imageObject.AddComponent<RawImage>();
        _image.texture = _display;
        _image.material = _screen;
        _image.raycastTarget = false;

        var rect = _image.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 1f);
        rect.sizeDelta = new Vector2(size, size);
        rect.anchoredPosition = new Vector2(-margin.x, -margin.y);
        return true;
    }

    void LateUpdate()
    {
        if (!Ready()) return;

        var rect = _image.rectTransform;
        rect.sizeDelta = new Vector2(size, size);
        rect.anchoredPosition = new Vector2(-margin.x, -margin.y);
        _screen.SetFloat(GlowId, glow);

        if (Time.time >= _nextRescan)
        {
            _nextRescan = Time.time + rescanInterval;
            Rescan();
        }
        // Seen from the camera's direction, tilted down a little, far enough back for the
        // whole range sphere to fit.
        Camera cam = Camera.main;
        Quaternion rotation = (cam ? cam.transform.rotation : Quaternion.identity) * Quaternion.Euler(extraTilt, 0f, 0f);
        float distance = 1.05f / Mathf.Sin(fieldOfView * 0.5f * Mathf.Deg2Rad);
        Vector3 eye = rotation * new Vector3(0f, 0f, -distance);

        Matrix4x4 view = Matrix4x4.TRS(eye, rotation, new Vector3(1f, 1f, -1f)).inverse;
        Matrix4x4 projection = Matrix4x4.Perspective(fieldOfView, 1f, 0.01f, distance * 3f);

        _commands.Clear();
        _commands.SetRenderTarget(_texture);
        _commands.ClearRenderTarget(false, true, Color.clear); // no depth buffer: the model is additive and order-free
        _commands.SetViewProjectionMatrices(view, projection);
        _commands.SetGlobalVector(EyeId, eye);
        _commands.SetGlobalFloat(DepthDimId, depthDim);
        _commands.SetGlobalVector(PulseId, new Vector4(pulseStrength, pulseInterval, pulseWidth, 0f));
        _commands.SetGlobalFloat(FlickerId, flicker);
        _commands.SetGlobalFloat(FadeStartId, Mathf.Clamp01(edgeFade));

        // Centred on the player, or on the camera until there is one.
        Vector3 centre = _player ? _player.transform.position : cam ? cam.transform.position : Vector3.zero;
        float scale = 1f / Mathf.Max(range, 0.01f);

        // The range sphere the map lives in.
        _globeProps.SetColor(ColorId, globeColor);
        _globeProps.SetFloat(EdgeFadeId, 0f); // the sphere is the edge; it doesn't fade itself
        _commands.DrawMesh(_sphere, Matrix4x4.Scale(Vector3.one * 2f), _model, 0, 0, _globeProps);

        DrawGeometry(centre, scale);
        DrawViruses(centre, scale);

        if (_player)
        {
            // The player, at the middle.
            _playerProps.SetColor(ColorId, playerColor);
            _playerProps.SetFloat(EdgeFadeId, 1f);
            _playerProps.SetFloat(FillId, 1f); // solid dot, not a ring
            _commands.DrawMesh(_sphere, Matrix4x4.Scale(Vector3.one * (playerDot * 2f)), _model, 0, 0, _playerProps);
        }

        // Resolve into the mipped copy; its small mips are the bloom.
        _commands.Blit(_texture, _display);
        _commands.GenerateMips(_display);

        Graphics.ExecuteCommandBuffer(_commands);
    }

    // Every nearby object as its own shape, one instanced draw per mesh.
    void DrawGeometry(Vector3 centre, float scale)
    {
        _cellProps.SetColor(ColorId, cellColor);
        _cellProps.SetFloat(EdgeFadeId, 1f);

        // World -> map: relative to the centre, shrunk to fit the range sphere.
        Matrix4x4 toMap = Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(-centre);
        float reach = range;

        for (int g = 0; g < _geometry.Count; g++)
        {
            MeshGroup group = _geometry[g];
            if (!group.mesh) continue;

            int count = 0;
            for (int i = 0; i < group.transforms.Count; i++)
            {
                Transform t = group.transforms[i];
                if (!t || !t.gameObject.activeInHierarchy) continue;
                if (Vector3.Distance(t.position, centre) - group.radii[i] > reach) continue;

                _batch[count++] = toMap * t.localToWorldMatrix;
                if (count == BatchSize) Flush(group.mesh, _cellProps, ref count);
            }
            Flush(group.mesh, _cellProps, ref count);
        }
    }

    void DrawViruses(Vector3 centre, float scale)
    {
        _agentProps.SetColor(ColorId, agentColor);
        _agentProps.SetFloat(EdgeFadeId, 1f);
        _agentProps.SetFloat(FillId, 0.8f);
        int count = 0;
        Vector3 dot = Vector3.one * (agentDot * 2f);

        for (int i = 0; i < _organisms.Count; i++)
        {
            Organism o = _organisms[i];
            if (!o || o == _player) continue;

            Vector3 offset = (o.transform.position - centre) * scale;
            if (offset.sqrMagnitude > 1f) continue;

            _batch[count++] = Matrix4x4.TRS(offset, Quaternion.identity, dot);
            if (count == BatchSize) Flush(_sphere, _agentProps, ref count);
        }
        Flush(_sphere, _agentProps, ref count);
    }

    void Flush(Mesh mesh, MaterialPropertyBlock props, ref int count)
    {
        if (count == 0) return;
        _commands.DrawMeshInstanced(mesh, 0, _model, 0, _batch, count, props);
        count = 0;
    }

    void Rescan()
    {
        if (!_player)
        {
            VirusMovement player = FindAnyObjectByType<VirusMovement>();
            _player = player ? player.Organism : null;
        }

        // Scene geometry, grouped by mesh. Skipped: other layers, things without a plain mesh,
        // anything huge (a skybox or ground plane would swallow the map) and the viruses
        // themselves, which are drawn as dots. Groups are refilled rather than remade, so a
        // rescan every second doesn't leave its lists behind as garbage.
        for (int i = 0; i < _geometry.Count; i++) { _geometry[i].transforms.Clear(); _geometry[i].radii.Clear(); }
        float biggest = range * maxObjectSize;

        foreach (MeshRenderer r in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if ((geometryLayers.value & (1 << r.gameObject.layer)) == 0) continue;
            if (r.GetComponentInParent<Organism>()) continue;
            if (!r.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;

            float radius = r.bounds.extents.magnitude;
            if (radius > biggest) continue;

            MeshGroup group = null;
            for (int i = 0; i < _geometry.Count; i++)
                if (_geometry[i].mesh == filter.sharedMesh) { group = _geometry[i]; break; }

            if (group == null)
            {
                group = new MeshGroup { mesh = filter.sharedMesh };
                _geometry.Add(group);
            }
            group.transforms.Add(r.transform);
            group.radii.Add(radius);
        }
        _geometry.RemoveAll(g => g.transforms.Count == 0);

        _organisms.Clear();
        _organisms.AddRange(FindObjectsByType<Organism>(FindObjectsSortMode.None));
    }

    // Plain UV sphere, radius 0.5 like Unity's own, so scale = diameter.
    static Mesh Sphere(int rings = 10, int segments = 16)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        for (int y = 0; y <= rings; y++)
        {
            float v = y / (float)rings, polar = v * Mathf.PI;
            for (int x = 0; x <= segments; x++)
            {
                float u = x / (float)segments, azimuth = u * Mathf.PI * 2f;
                vertices.Add(new Vector3(Mathf.Sin(polar) * Mathf.Cos(azimuth),
                                         Mathf.Cos(polar),
                                         Mathf.Sin(polar) * Mathf.Sin(azimuth)) * 0.5f);
            }
        }

        for (int y = 0; y < rings; y++)
        for (int x = 0; x < segments; x++)
        {
            int a = y * (segments + 1) + x, b = a + segments + 1;
            triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
            triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
        }

        var mesh = new Mesh { name = "Holo Sphere", hideFlags = HideFlags.HideAndDontSave };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        // Exact normals: RecalculateNormals leaves the seam's duplicate vertices with
        // different normals, and the fresnel draws that as a line across the globe.
        var normals = new List<Vector3>(vertices.Count);
        foreach (Vector3 v in vertices) normals.Add(v.normalized);
        mesh.SetNormals(normals);
        return mesh;
    }
}

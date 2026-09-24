using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Build-safe screen inversion sweep.
///
/// DROP-IN REPLACEMENT for the previous ScreenInvertTest.
///
/// Main build fixes:
/// - Uses a serialized Material/Shader reference instead of relying only on
///   Shader.Find(), so Unity has a real build dependency.
/// - Explicitly requests a URP depth texture on the gameplay camera.
/// - Creates the overlay at runtime under the actual camera.
/// - Uses huge mesh bounds and disables occlusion culling.
/// - Preserves Trigger(bool), easing, debug controls, and existing inspector use.
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class ScreenInvertTest : MonoBehaviour
{
    public enum DebugView
    {
        Off,
        SceneDepth,
        ReconstructedNormals,
        OutlineMask,
        SolidFill,

        // Current ScreenInvertSweep.shader also supports transparent depth as 5.
        TransparentDepth
    }

    public enum Easing
    {
        Linear,
        QuadraticIn,
        QuadraticOut,
        QuadraticInOut,
        CubicIn,
        CubicOut,
        CubicInOut,
        Exponential,
        Smoothstep
    }

    [Header("References")]

    [Tooltip("World point the sweep expands from.")]
    public Transform point;

    [Tooltip("Start from the impact on the surface under Point (its ISurfaceContact) instead of " +
             "Point itself, pinned to that surface as it moves.")]
    public bool fromImpact = true;

    [Min(0f)]
    [Tooltip("How far under Point to look for the surface.")]
    public float impactProbe = 6f;

    [Tooltip("Camera to overlay. ASSIGN THIS EXPLICITLY for builds if possible.")]
    public Camera targetCamera;

    [Tooltip(
        "Material using Custom/ScreenInvertSweep. Drawn as is (no copy), so " +
        "editing it changes the sweep live; the per-frame values (centre, " +
        "progress, player sphere) go through a property block and never touch it.")]
    public Material screenInvertMaterial;

    [Header("Sweep")]

    [Tooltip("Tick to sweep out, untick to retreat.")]
    public bool trigger;

    [Tooltip("Seconds to cover the screen.")]
    [Min(0f)]
    public float duration = 1f;

    [Tooltip(
        "Shape of the sweep over time. In starts slow, Out ends slow, " +
        "InOut does both.")]
    public Easing easing = Easing.QuadraticOut;

    [Header("Player Outline")]

    [Tooltip("Outlines within Highlight Radius of this are drawn in the material's " +
             "Player Outline colour. Empty: Point.")]
    public Transform highlight;

    [Tooltip("Radius of the player's sphere, legs included. 0 turns it off.")]
    [Min(0f)]
    public float highlightRadius = 2.5f;

    [Header("Backs")]

    [Tooltip(
        "Inside the sweep, surfaces drawn with these shaders show their backs (their " +
        "front faces are dropped), so you see into things. Play mode only.")]
    public bool showBacks = true;

    [Tooltip("Shaders that support it (INVERT_BACKFACES and a _Cull property).")]
    public Shader[] backShaders;

    [Tooltip(
        "At load, run a couple of frames with the sweep's shader variants and depth pass on " +
        "(covering nothing, so it looks the same), so the first focus doesn't stall on first-time work.")]
    public bool prewarm = true;

    [Header("Depth Of Field")]

    [Tooltip(
        "Fade depth of field out while the sweep covers the screen. The " +
        "outlines are traced from scene depth before post-processing, so " +
        "depth of field would blur far ones into a glow.")]
    public bool fadeDepthOfField = true;

    [Tooltip(
        "Volume whose depth of field fades. Empty: the first global volume " +
        "that has depth of field.")]
    public Volume depthOfFieldVolume;

    [Range(0.05f, 1f)]
    [Tooltip("Sweep progress at which depth of field is fully gone.")]
    public float depthOfFieldGoneAt = 0.5f;

    [Header("Diagnostics")]

    [Tooltip(
        "Paints the screen with what the effect is reading. " +
        "SolidFill should make the BUILD solid red if the overlay is drawing.")]
    public DebugView debugView = DebugView.Off;

    [Tooltip("Depth mapped to white in SceneDepth view, in metres.")]
    public float debugRange = 50f;

    public float RawProgress => _progress;
    public float Progress => Evaluate(easing, _progress);

    static readonly int CentreId =
        Shader.PropertyToID("_Center");

    static readonly int ProgressId =
        Shader.PropertyToID("_Progress");

    static readonly int HighlightId =
        Shader.PropertyToID("_HighlightSphere");

    static readonly int DebugViewId =
        Shader.PropertyToID("_DebugView");

    static readonly int DebugRangeId =
        Shader.PropertyToID("_DebugRange");

    [SerializeField, HideInInspector]
    float _progress;

    Camera _camera;
    GameObject _quad;
    Mesh _mesh;
    MeshRenderer _renderer;
    Material _material;
    MaterialPropertyBlock _block;

    const string BacksKeyword = "INVERT_BACKFACES";
    static readonly int InvertSweepId = Shader.PropertyToID("_InvertSweep");
    static readonly int CullId = Shader.PropertyToID("_Cull");

    // Materials switched to Cull Off while the sweep shows backs, with their own cull to restore.
    readonly System.Collections.Generic.Dictionary<Material, float> _backMaterials =
        new System.Collections.Generic.Dictionary<Material, float>();
    bool _backsOn;
    bool _warming;

    // Runtime copy of the volume's depth of field, with its values to restore.
    DepthOfField _dof;
    bool _dofSearched;
    bool _dofActive;
    float _dofStart;
    float _dofEnd;
    float _dofAperture;

    // Live instances. ScreenInvertTransparentDepthFeature only draws its transparent depth
    // (and asks URP for the normals prepass) while one of these has its overlay showing.
    static readonly System.Collections.Generic.List<ScreenInvertTest> s_instances =
        new System.Collections.Generic.List<ScreenInvertTest>();

    /// <summary>Whether any sweep overlay is drawing this frame (it's set in LateUpdate, before rendering).</summary>
    public static bool AnyShowing
    {
        get
        {
            for (int i = 0; i < s_instances.Count; i++)
                if (s_instances[i]._warming ||
                    s_instances[i]._renderer && s_instances[i]._renderer.enabled)
                    return true;
            return false;
        }
    }

    void OnEnable()
    {
        if (!s_instances.Contains(this)) s_instances.Add(this);
        Build();
    }

    // Everything the first sweep would do for the first time: every back-capable shader's
    // INVERT_BACKFACES variant, their materials' Cull Off, and the transparent depth + normals
    // passes (ScreenInvertTransparentDepthFeature). The sweep covers nothing meanwhile
    // (_InvertSweep.w = 0), so nothing is clipped and the frames look as usual.
    System.Collections.IEnumerator Start()
    {
        if (!Application.isPlaying || !prewarm || _progress > 0.0001f) yield break;
        yield return null; // after everyone's Start

        var watch = System.Diagnostics.Stopwatch.StartNew();
        _warming = true;
        SetBacks(showBacks);
        Shader.SetGlobalVector(InvertSweepId, Vector4.zero);
        double backs = watch.Elapsed.TotalMilliseconds;

        yield return null;
        yield return null;
        _warming = false;
        if (_progress <= 0.0001f) SetBacks(false);
        Debug.Log($"ScreenInvertTest: prewarmed the sweep (switching materials took {backs:0.0} ms).", this);
    }

    void OnDisable()
    {
        s_instances.Remove(this);
        Cleanup();
        RestoreDepthOfField();
        SetBacks(false);
    }

    void OnDestroy()
    {
        Cleanup();
        RestoreDepthOfField();
        SetBacks(false);
    }

    void OnApplicationQuit() => SetBacks(false);

    public void Trigger(
        bool on)
    {
        trigger = on;
    }

    // Convenience overloads for UnityEvents that do not pass a bool.
    public void Trigger()
    {
        trigger = true;
    }

    public void Untrigger()
    {
        trigger = false;
    }

    public void Play()
    {
        trigger = true;
    }

    public void Reverse()
    {
        trigger = false;
    }

    void Build()
    {
        Cleanup();

        _camera =
            targetCamera
                ? targetCamera
                : Camera.main;

        if (!_camera)
        {
            Debug.LogError(
                "ScreenInvertTest: no target camera assigned and Camera.main was not found.",
                this);

            return;
        }

        // Explicitly make URP generate _CameraDepthTexture.
        UniversalAdditionalCameraData urp =
            _camera.GetUniversalAdditionalCameraData();

        if (urp != null)
        {
            urp.requiresDepthTexture = true;
        }

        _material =
            screenInvertMaterial;

        if (!_material)
        {
            Debug.LogError(
                "ScreenInvertTest: assign Screen Invert Material " +
                "(a material using Custom/ScreenInvertSweep).",
                this);

            return;
        }

        _quad =
            new GameObject(
                "__ScreenInvertOverlay");

        _quad.hideFlags =
            HideFlags.DontSave;

        // Put it on a layer this camera definitely renders.
        _quad.layer =
            FirstRenderedLayer(
                _camera.cullingMask);

        Transform quadTransform =
            _quad.transform;

        quadTransform.SetParent(
            _camera.transform,
            false);

        // Physically keep the renderer in the camera frustum even though the
        // vertex shader later emits clip-space coordinates.
        quadTransform.localPosition =
            Vector3.forward *
            Mathf.Max(
                0.05f,
                _camera.nearClipPlane +
                0.01f);

        quadTransform.localRotation =
            Quaternion.identity;

        quadTransform.localScale =
            Vector3.one;

        MeshFilter filter =
            _quad.AddComponent<MeshFilter>();

        _mesh =
            BuildQuad();

        filter.sharedMesh =
            _mesh;

        _renderer =
            _quad.AddComponent<MeshRenderer>();

        _renderer.sharedMaterial =
            _material;

        _renderer.shadowCastingMode =
            ShadowCastingMode.Off;

        _renderer.receiveShadows =
            false;

        _renderer.lightProbeUsage =
            LightProbeUsage.Off;

        _renderer.reflectionProbeUsage =
            ReflectionProbeUsage.Off;

        _renderer.allowOcclusionWhenDynamic =
            false;

        UpdateMaterial();

        Debug.Log(
            "ScreenInvertTest built overlay. Camera='" +
            _camera.name +
            "', shader='" +
            (_material.shader
                ? _material.shader.name
                : "NULL") +
            "', supported=" +
            (_material.shader
                ? _material.shader.isSupported.ToString()
                : "false"),
            this);
    }

    static Mesh BuildQuad()
    {
        Mesh mesh =
            new Mesh
            {
                name =
                    "__ScreenInvertQuad",

                hideFlags =
                    HideFlags.DontSave
            };

        mesh.vertices =
            new Vector3[]
            {
                new Vector3(
                    -0.5f,
                    -0.5f,
                    0f),

                new Vector3(
                     0.5f,
                    -0.5f,
                    0f),

                new Vector3(
                    -0.5f,
                     0.5f,
                    0f),

                new Vector3(
                     0.5f,
                     0.5f,
                    0f)
            };

        mesh.triangles =
            new int[]
            {
                0, 2, 1,
                2, 3, 1
            };

        mesh.uv =
            new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            };

        // Unity performs CPU renderer culling before the vertex shader runs.
        // Huge bounds ensure this overlay is always submitted.
        mesh.bounds =
            new Bounds(
                Vector3.zero,
                Vector3.one *
                100000f);

        return mesh;
    }

    void LateUpdate()
    {
        Camera desiredCamera =
            targetCamera
                ? targetCamera
                : Camera.main;

        // Rebuild if the actual gameplay camera or the material changes.
        if (!_quad ||
            !_material ||
            !_renderer ||
            desiredCamera != _camera ||
            _material != screenInvertMaterial)
        {
            Build();
        }

        if (!_quad ||
            !_material ||
            !_renderer)
        {
            return;
        }

        float step =
            duration > 0f
                ? Time.unscaledDeltaTime /
                  duration
                : 1f;

        if (trigger && !_wasTriggered) CaptureImpact(); // each sweep starts where this one hits
        _wasTriggered = trigger;

        _progress =
            Mathf.Clamp01(
                _progress +
                (trigger
                    ? step
                    : -step));

        UpdateMaterial();
        UpdateDepthOfField();
        UpdateBacks();

        // Debug modes must draw even when progress is zero.
        _renderer.enabled =
            _progress >
                0.0001f ||
            debugView !=
                DebugView.Off;
    }

    // Per-frame values only, through a property block: the material asset
    // keeps the look and stays editable while this runs.
    void UpdateMaterial()
    {
        if (!_renderer)
            return;

        _block ??= new MaterialPropertyBlock(); // plain fields don't survive a play-mode reload

        Vector3 worldPoint =
            SweepCentre();

        Transform player =
            highlight
                ? highlight
                : point;

        Vector3 p =
            player
                ? player.position
                : worldPoint;

        _block.SetVector(
            CentreId,
            new Vector4(
                worldPoint.x,
                worldPoint.y,
                worldPoint.z,
                1f));

        _block.SetFloat(
            ProgressId,
            Evaluate(
                easing,
                _progress));

        _block.SetVector(
            HighlightId,
            new Vector4(
                p.x,
                p.y,
                p.z,
                player ? highlightRadius : 0f));

        _block.SetFloat(
            DebugViewId,
            (float)debugView);

        _block.SetFloat(
            DebugRangeId,
            debugRange);

        _renderer.SetPropertyBlock(
            _block);
    }

    // ---------------- centre ----------------

    bool _wasTriggered;
    Transform _impactSurface;
    Vector3 _impactLocal;

    Vector3 SweepCentre()
    {
        if (fromImpact && _impactSurface)
            return _impactSurface.TransformPoint(_impactLocal);
        return point ? point.position : transform.position;
    }

    // The spot on the surface under Point, kept in the surface's own space so the sweep rides it.
    void CaptureImpact()
    {
        _impactSurface = null;
        if (!fromImpact || !point) return;

        ISurfaceContact contact = point.GetComponentInParent<ISurfaceContact>();
        Transform surface = contact != null && contact.OnSurface ? contact.Surface : null;
        if (!surface) return;

        Vector3 down = -contact.SurfaceNormal;
        Vector3 at = point.position - down * 0.05f;
        Transform self = point.root;
        Vector3 hit = point.position + down * 0.5f; // fallback: just under the body
        float nearest = float.MaxValue;
        foreach (RaycastHit h in Physics.RaycastAll(at, down, impactProbe, ~0, QueryTriggerInteraction.Ignore))
        {
            if (h.transform.IsChildOf(self) || h.distance >= nearest) continue;
            nearest = h.distance;
            hit = h.point;
        }

        _impactSurface = surface;
        _impactLocal = surface.InverseTransformPoint(hit);
    }

    // Inside the sweep, surfaces show their backs: their materials go Cull Off and the shader
    // (INVERT_BACKFACES) drops front faces inside the same circle, in colour and depth, so the
    // outlines trace the insides. Only while sweeping; the materials' own cull comes back after
    // (they're shared assets, so it must).
    void UpdateBacks()
    {
        if (_warming) return; // Start's prewarm holds them on
        bool on = showBacks && Application.isPlaying && _progress > 0.0001f;
        SetBacks(on);
        if (!on) return;

        Vector3 c = SweepCentre();
        Shader.SetGlobalVector(InvertSweepId, new Vector4(c.x, c.y, c.z, Evaluate(easing, _progress)));
    }

    void SetBacks(bool on)
    {
        if (on == _backsOn) return;
        _backsOn = on;

        if (on)
        {
            Shader fallback = Shader.Find("Custom/BloodCellTriplanar");
            foreach (Renderer rend in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                foreach (Material m in rend.sharedMaterials)
                {
                    if (!m || _backMaterials.ContainsKey(m) || !m.HasProperty(CullId)) continue;
                    bool supported = backShaders != null && backShaders.Length > 0
                        ? System.Array.IndexOf(backShaders, m.shader) >= 0
                        : m.shader == fallback;
                    if (!supported) continue;
                    _backMaterials[m] = m.GetFloat(CullId);
                    m.SetFloat(CullId, (float)CullMode.Off);
                }
            Shader.EnableKeyword(BacksKeyword);
        }
        else
        {
            Shader.DisableKeyword(BacksKeyword);
            Shader.SetGlobalVector(InvertSweepId, Vector4.zero); // nothing reads a stale sweep (drill x-ray)
            foreach (var pair in _backMaterials)
                if (pair.Key) pair.Key.SetFloat(CullId, pair.Value);
            _backMaterials.Clear();
        }
    }

    // Pushes the blur away as the sweep covers the screen, rather than switching
    // it off, so there's no pop: Gaussian start/end recede to infinity, Bokeh
    // stops down to f/32, then the effect is disabled once gone. Play mode only,
    // on the volume's runtime profile copy, never the asset.
    void UpdateDepthOfField()
    {
        if (!Application.isPlaying ||
            !fadeDepthOfField)
        {
            RestoreDepthOfField();
            return;
        }

        if (_dof == null &&
            !FindDepthOfField())
        {
            return;
        }

        float k =
            Mathf.Clamp01(
                Progress /
                depthOfFieldGoneAt);

        float keep =
            Mathf.Max(
                1f - k,
                0.001f);

        _dof.active =
            _dofActive &&
            k < 0.999f;

        _dof.gaussianStart.value =
            _dofStart / keep;

        _dof.gaussianEnd.value =
            _dofEnd / keep;

        _dof.aperture.value =
            Mathf.Lerp(
                _dofAperture,
                32f,
                k);
    }

    bool FindDepthOfField()
    {
        if (_dofSearched)
            return false;

        _dofSearched = true;

        Volume volume =
            depthOfFieldVolume;

        if (!volume)
        {
            foreach (Volume v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
            {
                if (v.isGlobal &&
                    v.sharedProfile &&
                    v.sharedProfile.Has<DepthOfField>())
                {
                    volume = v;
                    break;
                }
            }
        }

        // .profile is this volume's own runtime copy.
        if (!volume ||
            !volume.profile.TryGet(out _dof))
        {
            _dof = null;
            return false;
        }

        _dofActive = _dof.active;
        _dofStart = _dof.gaussianStart.value;
        _dofEnd = _dof.gaussianEnd.value;
        _dofAperture = _dof.aperture.value;
        return true;
    }

    void RestoreDepthOfField()
    {
        if (_dof != null)
        {
            _dof.active = _dofActive;
            _dof.gaussianStart.value = _dofStart;
            _dof.gaussianEnd.value = _dofEnd;
            _dof.aperture.value = _dofAperture;
        }

        _dof = null;
        _dofSearched = false;
    }

    public static float Evaluate(
        Easing mode,
        float t)
    {
        t =
            Mathf.Clamp01(t);

        switch (mode)
        {
            case Easing.QuadraticIn:
                return t * t;

            case Easing.QuadraticOut:
                return
                    1f -
                    (1f - t) *
                    (1f - t);

            case Easing.QuadraticInOut:
                return
                    t < 0.5f
                        ? 2f * t * t
                        : 1f -
                          Mathf.Pow(
                              -2f * t + 2f,
                              2f) *
                          0.5f;

            case Easing.CubicIn:
                return t * t * t;

            case Easing.CubicOut:
                return
                    1f -
                    Mathf.Pow(
                        1f - t,
                        3f);

            case Easing.CubicInOut:
                return
                    t < 0.5f
                        ? 4f *
                          t *
                          t *
                          t
                        : 1f -
                          Mathf.Pow(
                              -2f * t + 2f,
                              3f) *
                          0.5f;

            case Easing.Exponential:
                return
                    t <= 0f
                        ? 0f
                        : (Mathf.Pow(
                               2f,
                               10f *
                               (t - 1f)) -
                           0.0009765625f) /
                          0.9990234375f;

            case Easing.Smoothstep:
                return
                    t *
                    t *
                    (3f -
                     2f * t);

            default:
                return t;
        }
    }

    static int FirstRenderedLayer(
        int mask)
    {
        for (int layer = 0;
             layer < 32;
             layer++)
        {
            if ((mask &
                 (1 << layer)) != 0)
            {
                return layer;
            }
        }

        return 0;
    }

    void Cleanup()
    {
        if (_quad)
        {
            if (Application.isPlaying)
                Destroy(_quad);
            else
                DestroyImmediate(_quad);
        }

        if (_mesh)
        {
            if (Application.isPlaying)
                Destroy(_mesh);
            else
                DestroyImmediate(_mesh);
        }

        // _material is the asset itself: never destroyed here.
        _quad = null;
        _mesh = null;
        _renderer = null;
        _material = null;
    }

    [ContextMenu("TEST - Play Once")]
    void PlayOnce()
    {
        _progress = 0f;
        trigger = true;
    }

    [ContextMenu("TEST - Solid Red")]
    void TestSolidRed()
    {
        debugView =
            DebugView.SolidFill;

        if (!_quad)
            Build();

        UpdateMaterial();

        if (_renderer)
            _renderer.enabled = true;
    }

    [ContextMenu("TEST - Normal View")]
    void TestNormal()
    {
        debugView =
            DebugView.Off;

        UpdateMaterial();
    }

    [ContextMenu("Log Diagnostics")]
    void LogDiagnostics()
    {
        string nl =
            System.Environment.NewLine;

        Debug.Log(
            "ScreenInvertTest diagnostics" +
            nl +
            "  target camera: " +
            (_camera
                ? _camera.name
                : "NULL") +
            nl +
            "  quad: " +
            (_quad
                ? _quad.name
                : "NOT CREATED") +
            nl +
            "  renderer: " +
            (_renderer
                ? (_renderer.enabled
                    ? "enabled"
                    : "disabled")
                : "NULL") +
            nl +
            "  material: " +
            (_material
                ? _material.name
                : "NULL") +
            nl +
            "  shader: " +
            (_material &&
             _material.shader
                ? _material.shader.name
                : "NULL") +
            nl +
            "  shader supported: " +
            (_material &&
             _material.shader
                ? _material.shader.isSupported.ToString()
                : "n/a") +
            nl +
            "  progress: " +
            _progress +
            nl +
            "  debug: " +
            debugView,
            this);
    }
}

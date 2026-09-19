using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Test harness for the screen inversion sweep.
///
/// Assign a Transform, tick Trigger, and the inversion expands from that point
/// on screen until it covers everything. Untick it and the circle retreats.
///
/// The effect is an overlay quad parented to the camera rather than a renderer
/// feature, so nothing has to be added to the URP renderer asset. The quad's
/// own transform is irrelevant -- the shader emits clip space directly -- but
/// it still has to survive frustum culling, which is what the oversized mesh
/// bounds below are for.
/// </summary>
[ExecuteAlways]
public class ScreenInvertTest : MonoBehaviour
{
    public enum DebugView
    {
        /// <summary>Normal rendering.</summary>
        Off,

        /// <summary>
        /// Scene depth as greyscale, ignoring the sweep. Anything missing here
        /// is missing from _CameraDepthTexture, and no edge tuning will find it.
        /// </summary>
        SceneDepth,

        /// <summary>Normals rebuilt from depth, as RGB.</summary>
        ReconstructedNormals,

        /// <summary>The combined outline mask on its own.</summary>
        OutlineMask,

        /// <summary>
        /// Solid red, dependent on nothing. If the screen does not turn red,
        /// the quad is not being drawn and no other setting matters.
        /// </summary>
        SolidFill
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

    [Tooltip("World point the sweep expands from.")]
    public Transform point;

    [Tooltip("Tick to sweep out, untick to retreat.")]
    public bool trigger;

    [Tooltip("Seconds to cover the screen.")]
    public float duration = 1f;

    [Tooltip("Shape of the sweep over time. In starts slow, Out ends slow, " +
             "InOut does both. Applied to the radius, so it is what decides " +
             "whether the wavefront accelerates away or coasts to a stop.")]
    public Easing easing = Easing.QuadraticOut;

    [Tooltip("Camera to overlay. Falls back to Camera.main.")]
    public Camera targetCamera;

    [Range(0f, 0.5f)]
    [Tooltip("Softness of the expanding edge, as a fraction of screen height.")]
    public float edgeSoftness = 0.015f;

    [Header("Diagnostics")]
    [Tooltip("Paints the whole screen with what the effect is reading, " +
             "regardless of Trigger. Start with Scene Depth: if an object is " +
             "black or flat there, it never reached the depth texture.")]
    public DebugView debugView = DebugView.Off;

    [Tooltip("Depth mapped to white in the Scene Depth view, in metres.")]
    public float debugRange = 50f;

    /// <summary>Raw 0..1 progress, before easing.</summary>
    public float RawProgress => _progress;

    /// <summary>Eased 0..1 progress, which is what the shader receives.</summary>
    public float Progress => Evaluate(easing, _progress);

    static readonly int CentreId = Shader.PropertyToID("_Center");
    static readonly int ProgressId = Shader.PropertyToID("_Progress");
    static readonly int SoftnessId = Shader.PropertyToID("_SweepSoftness");
    static readonly int DebugViewId = Shader.PropertyToID("_DebugView");
    static readonly int DebugRangeId = Shader.PropertyToID("_DebugRange");

    [SerializeField, HideInInspector] float _progress;

    Camera _camera;
    GameObject _quad;
    MeshRenderer _renderer;
    Material _material;

    void OnEnable() => Build();

    void OnDisable()
    {
        if (_quad) DestroyImmediate(_quad);
        if (_material) DestroyImmediate(_material);
        _quad = null;
        _material = null;
    }

    void Build()
    {
        _camera = targetCamera ? targetCamera : Camera.main;
        if (!_camera) return;

        // The outlines come from scene depth, which URP only renders when
        // something asks for it. Without this the depth texture is all far
        // plane and every pixel reads as edgeless.
        var urp = _camera.GetUniversalAdditionalCameraData();
        if (urp) urp.requiresDepthTexture = true;

        Shader shader = Shader.Find("Custom/ScreenInvertSweep");
        if (!shader)
        {
            Debug.LogError("ScreenInvertTest: shader 'Custom/ScreenInvertSweep' not found.", this);
            return;
        }

        _material = new Material(shader) { hideFlags = HideFlags.DontSave };

        _quad = new GameObject("ScreenInvertOverlay")
        {
            hideFlags = HideFlags.DontSave | HideFlags.NotEditable
        };

        _quad.transform.SetParent(_camera.transform, false);

        // Sits just past the near plane purely so it is inside the frustum.
        _quad.transform.localPosition = Vector3.forward * (_camera.nearClipPlane + 0.01f);
        _quad.transform.localRotation = Quaternion.identity;

        _quad.AddComponent<MeshFilter>().sharedMesh = BuildQuad();

        _renderer = _quad.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = _material;
        _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
    }

    static Mesh BuildQuad()
    {
        var mesh = new Mesh { name = "ScreenInvertQuad", hideFlags = HideFlags.DontSave };

        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f)
        };

        mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };

        // Deliberately enormous. The shader ignores the transform, but Unity
        // still culls by bounds, and a near-plane quad clips out the moment the
        // camera turns. Huge bounds keep it submitted every frame.
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
        return mesh;
    }

    void LateUpdate()
    {
        if (!_quad || !_material) Build();
        if (!_quad || !_material) return;

        float step = duration > 0f ? Time.unscaledDeltaTime / duration : 1f;
        _progress = Mathf.Clamp01(_progress + (trigger ? step : -step));

        _material.SetVector(CentreId, point ? (Vector4)point.position : Vector4.zero);
        _material.SetFloat(ProgressId, Evaluate(easing, _progress));
        _material.SetFloat(SoftnessId, edgeSoftness);
        _material.SetFloat(DebugViewId, (float)debugView);
        _material.SetFloat(DebugRangeId, debugRange);

        // Nothing to draw at rest, and skipping the draw entirely is cheaper
        // than blending a fully transparent fullscreen quad.
        // Debug views paint the whole screen and need no sweep to be running.
        _renderer.enabled = _progress > 0.0001f || debugView != DebugView.Off;
    }

    /// <summary>
    /// Eased 0..1. Every curve passes through 0 and 1 exactly, so the sweep
    /// always starts at the point and always finishes covering the screen --
    /// only the pacing between them changes.
    /// </summary>
    public static float Evaluate(Easing mode, float t)
    {
        t = Mathf.Clamp01(t);

        switch (mode)
        {
            case Easing.QuadraticIn:    return t * t;
            case Easing.QuadraticOut:   return 1f - (1f - t) * (1f - t);
            case Easing.QuadraticInOut: return t < 0.5f
                                             ? 2f * t * t
                                             : 1f - Mathf.Pow(-2f * t + 2f, 2f) * 0.5f;

            case Easing.CubicIn:        return t * t * t;
            case Easing.CubicOut:       return 1f - Mathf.Pow(1f - t, 3f);
            case Easing.CubicInOut:     return t < 0.5f
                                             ? 4f * t * t * t
                                             : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

            // Shifted so it still reaches 0 at t=0 rather than 2^-10.
            case Easing.Exponential:    return t <= 0f
                                             ? 0f
                                             : (Mathf.Pow(2f, 10f * (t - 1f)) - 0.0009765625f)
                                               / 0.9990234375f;

            case Easing.Smoothstep:     return t * t * (3f - 2f * t);

            default:                    return t;
        }
    }

    [ContextMenu("Log Diagnostics")]
    void LogDiagnostics()
    {
        Camera cam = targetCamera ? targetCamera : Camera.main;
        string nl = System.Environment.NewLine;

        string camera = cam ? cam.name : "NULL (no Camera.main, and none assigned)";

        string quad = _quad
            ? _quad.name + " under " + (_quad.transform.parent ? _quad.transform.parent.name : "nothing")
            : "NOT CREATED";

        string rend = _renderer ? (_renderer.enabled ? "enabled" : "DISABLED") : "NULL";
        string shader = _material && _material.shader ? _material.shader.name : "NULL";
        string usable = _material && _material.shader ? _material.shader.isSupported.ToString() : "n/a";
        string passes = _material ? _material.passCount.ToString() : "n/a";

        Debug.Log("ScreenInvertTest diagnostics" + nl +
                  "  camera:        " + camera + nl +
                  "  quad:          " + quad + nl +
                  "  renderer:      " + rend + nl +
                  "  shader:        " + shader + nl +
                  "  shader usable: " + usable + nl +
                  "  passes:        " + passes + nl +
                  "  progress:      " + _progress + nl +
                  "  debug view:    " + debugView,
                  this);
    }

    [ContextMenu("Play Once")]
    void PlayOnce()
    {
        _progress = 0f;
        trigger = true;
    }
}

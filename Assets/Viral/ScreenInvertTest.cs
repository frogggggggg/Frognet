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

    [Tooltip("Camera to overlay. ASSIGN THIS EXPLICITLY for builds if possible.")]
    public Camera targetCamera;

    [Tooltip(
        "Recommended: assign your existing material that uses " +
        "Custom/ScreenInvertSweep. The script makes a private runtime copy.")]
    public Material screenInvertMaterial;

    [Tooltip(
        "Fallback if no material is assigned. Drag ScreenInvertSweep.shader " +
        "here so Unity cannot strip it from the build.")]
    public Shader screenInvertShader;

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

    [Range(0f, 0.5f)]
    [Tooltip("Softness of the expanding edge, as a fraction of screen height.")]
    public float edgeSoftness = 0.015f;

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

    static readonly int SoftnessId =
        Shader.PropertyToID("_SweepSoftness");

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

    void OnEnable()
    {
        Build();
    }

    void OnDisable()
    {
        Cleanup();
    }

    void OnDestroy()
    {
        Cleanup();
    }

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

        // Prefer a serialized material because it guarantees the shader is a
        // real project/build dependency and preserves all of your material
        // colours/outline settings.
        if (screenInvertMaterial)
        {
            _material =
                new Material(
                    screenInvertMaterial);
        }
        else
        {
            Shader shader =
                screenInvertShader;

#if UNITY_EDITOR
            // Editor convenience only. Do NOT rely on Shader.Find for builds.
            if (!shader)
            {
                shader =
                    Shader.Find(
                        "Custom/ScreenInvertSweep");
            }
#endif

            if (!shader)
            {
                Debug.LogError(
                    "ScreenInvertTest: assign Screen Invert Material or " +
                    "Screen Invert Shader in the Inspector. " +
                    "A runtime-only Shader.Find reference may be stripped " +
                    "from a standalone build.",
                    this);

                return;
            }

            _material =
                new Material(
                    shader);
        }

        _material.name =
            "__ScreenInvertRuntimeMaterial";

        _material.hideFlags =
            HideFlags.DontSave;

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

        // Rebuild if the actual gameplay camera changes.
        if (!_quad ||
            !_material ||
            !_renderer ||
            desiredCamera != _camera)
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

        _progress =
            Mathf.Clamp01(
                _progress +
                (trigger
                    ? step
                    : -step));

        UpdateMaterial();

        // Debug modes must draw even when progress is zero.
        _renderer.enabled =
            _progress >
                0.0001f ||
            debugView !=
                DebugView.Off;
    }

    void UpdateMaterial()
    {
        if (!_material)
            return;

        Vector3 worldPoint =
            point
                ? point.position
                : transform.position;

        _material.SetVector(
            CentreId,
            new Vector4(
                worldPoint.x,
                worldPoint.y,
                worldPoint.z,
                1f));

        _material.SetFloat(
            ProgressId,
            Evaluate(
                easing,
                _progress));

        _material.SetFloat(
            SoftnessId,
            edgeSoftness);

        _material.SetFloat(
            DebugViewId,
            (float)debugView);

        _material.SetFloat(
            DebugRangeId,
            debugRange);
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

        if (_material)
        {
            if (Application.isPlaying)
                Destroy(_material);
            else
                DestroyImmediate(_material);
        }

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

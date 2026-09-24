using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

/// <summary>
/// Draws the cell sky (Custom/StylizedCellSkybox) from cubemaps instead of evaluating its 30
/// noise layers for every sky pixel every frame, which was most of the GPU's frame.
///
/// The sky only drifts slowly, so it is baked into a cubemap a strip at a time in the background,
/// finishing one every Refresh Seconds. Two finished bakes (one moment and the next) are
/// crossfaded while the third is being baked, so the drift keeps moving smoothly, just as if it
/// were live. At 2048 per face the cache is as sharp as a 1080p screen at a 60 degree field of
/// view; zoomed in past that (Max Magnification) the live sky is drawn instead, so nothing ever
/// looks softer than before. Edits to the sky material show up at the next bake.
///
/// Toggle Key flips between cached and live, to compare. Play mode only: the scene's skybox
/// material is swapped at runtime and put back when this is disabled.
/// Nothing to set up: one is created when play starts if the scene's skybox uses the cell sky.
/// </summary>
public class SkyboxCache : MonoBehaviour
{
    const string SkyShader = "Custom/StylizedCellSkybox";

    [Tooltip("Cubemap face size. 2048 matches a 1080p screen at a 60 degree field of view.")]
    public int faceSize = 2048;
    [Min(0.1f), Tooltip("Seconds per bake: how far apart the two crossfaded moments are.")]
    public float refreshSeconds = 1f;
    [Min(1f), Tooltip("Draw the live sky when the view is zoomed in so far that a cache texel would cover more than this many pixels.")]
    public float maxMagnification = 1.15f;
    public Key toggleKey = Key.F6;
    public bool useCache = true;

    [Header("Build references (found automatically in the Editor)")]
    public Shader bakeShader;
    public Shader cachedShader;

    /// <summary>Whether the cache is what's being drawn right now (not the live sky).</summary>
    public bool Drawing { get; private set; }

    const int SlicesPerFace = 32;

    static readonly int FaceId = Shader.PropertyToID("_SkyBakeFace"),
                        SizeId = Shader.PropertyToID("_SkyBakeSize"),
                        TimeId = Shader.PropertyToID("_SkyBakeTime"),
                        CacheAId = Shader.PropertyToID("_SkyCacheA"),
                        CacheBId = Shader.PropertyToID("_SkyCacheB"),
                        BlendId = Shader.PropertyToID("_SkyCacheBlend");

    Material _source, _bake, _display;
    readonly RenderTexture[] _cubes = new RenderTexture[3];
    int _a, _b = 1, _c = 2;   // earlier, later, being baked
    float _timeA, _timeB;     // the moments a and b show (Time.timeSinceLevelLoad, like the shader's _Time.y)
    int _cursor;              // next strip of c: face * SlicesPerFace + slice
    float _dt = 1f / 60f;
    CommandBuffer _cmd;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<SkyboxCache>()) return; // the scene has its own, with its own settings
        Material sky = RenderSettings.skybox;
        if (!sky || !sky.shader || sky.shader.name != SkyShader) return;
        new GameObject("Skybox Cache").AddComponent<SkyboxCache>();
    }

    void OnEnable()
    {
#if UNITY_EDITOR
        if (!bakeShader) bakeShader = Shader.Find("Hidden/StylizedCellSkyBake");
        if (!cachedShader) cachedShader = Shader.Find("Hidden/StylizedCellSkyCached");
#endif
        if (!bakeShader) bakeShader = Shader.Find("Hidden/StylizedCellSkyBake");     // Always Included Shaders
        if (!cachedShader) cachedShader = Shader.Find("Hidden/StylizedCellSkyCached");

        Material sky = RenderSettings.skybox;
        if (!sky || !sky.shader || sky.shader.name != SkyShader || !bakeShader || !cachedShader ||
            !bakeShader.isSupported || !cachedShader.isSupported)
        {
            enabled = false; // the live sky stays
            return;
        }

        _source = sky;
        _bake = new Material(bakeShader) { hideFlags = HideFlags.HideAndDontSave };
        _display = new Material(cachedShader) { hideFlags = HideFlags.HideAndDontSave };
        _cmd = new CommandBuffer { name = "Skybox Cache" };

        RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)
            ? RenderTextureFormat.RGB111110Float   // same precision as the HDR camera target
            : RenderTextureFormat.ARGBHalf;
        for (int i = 0; i < 3; i++)
        {
            _cubes[i] = new RenderTexture(faceSize, faceSize, 0, format, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Cube,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "Skybox Cache " + i,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _cubes[i].Create();
        }

        // The first two moments in full now (a one-off at load), so there's something to show.
        float now = Time.timeSinceLevelLoad;
        _a = 0; _b = 1; _c = 2;
        _timeA = now;
        _timeB = now + refreshSeconds;
        _bake.CopyPropertiesFromMaterial(_source);
        _cmd.Clear();
        for (int s = 0; s < 6 * SlicesPerFace; s++) RecordSlice(_cubes[_a], s, _timeA);
        for (int s = 0; s < 6 * SlicesPerFace; s++) RecordSlice(_cubes[_b], s, _timeB);
        Graphics.ExecuteCommandBuffer(_cmd);
        _cursor = 0;

        Apply(now);
    }

    void OnDisable()
    {
        if (_source && RenderSettings.skybox == _display) RenderSettings.skybox = _source;
        Drawing = false;
        for (int i = 0; i < 3; i++)
            if (_cubes[i]) { _cubes[i].Release(); Destroy(_cubes[i]); _cubes[i] = null; }
        if (_bake) Destroy(_bake);
        if (_display) Destroy(_display);
        _cmd?.Release();
        _cmd = null;
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        if (k != null && k[toggleKey].wasPressedThisFrame) useCache = !useCache;

        float now = Time.timeSinceLevelLoad;
        _dt = Mathf.Lerp(_dt, Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);

        // Bake the next moment's strips, spread so it's done when the crossfade reaches it.
        // Kept going while the live sky shows too, so switching back is seamless.
        int total = 6 * SlicesPerFace;
        if (_cursor < total)
        {
            if (_cursor == 0) _bake.CopyPropertiesFromMaterial(_source); // follow live edits
            float framesLeft = Mathf.Max(1f, (_timeB - now) / _dt);
            int count = Mathf.Clamp(Mathf.CeilToInt((total - _cursor) / framesLeft), 1, total - _cursor);
            _cmd.Clear();
            for (int i = 0; i < count; i++) RecordSlice(_cubes[_c], _cursor++, _timeB + refreshSeconds);
            Graphics.ExecuteCommandBuffer(_cmd);
        }

        // Crossfade done and the next moment ready: move along.
        if (_cursor >= total && now >= _timeB)
        {
            (_a, _b, _c) = (_b, _c, _a);
            _timeA = _timeB;
            _timeB += refreshSeconds;
            if (_timeB < now) { _timeA = now; _timeB = now + refreshSeconds; } // fell behind (a hitch): catch up
            _cursor = 0;
        }

        Apply(now);
    }

    void RecordSlice(RenderTexture cube, int index, float time)
    {
        int face = index / SlicesPerFace, slice = index % SlicesPerFace;
        int rows = Mathf.CeilToInt(faceSize / (float)SlicesPerFace);
        _cmd.SetRenderTarget(new RenderTargetIdentifier(cube, 0, (CubemapFace)face, 0));
        _cmd.SetGlobalFloat(FaceId, face);
        _cmd.SetGlobalFloat(SizeId, faceSize);
        _cmd.SetGlobalFloat(TimeId, time);
        _cmd.EnableScissorRect(new Rect(0, slice * rows, faceSize, Mathf.Min(rows, faceSize - slice * rows)));
        _cmd.DrawProcedural(Matrix4x4.identity, _bake, 0, MeshTopology.Triangles, 3);
        _cmd.DisableScissorRect();
    }

    void Apply(float now)
    {
        _display.SetTexture(CacheAId, _cubes[_a]);
        _display.SetTexture(CacheBId, _cubes[_b]);
        _display.SetFloat(BlendId, Mathf.Clamp01((now - _timeA) / Mathf.Max(1e-4f, _timeB - _timeA)));

        Drawing = useCache && SharpEnough();
        Material want = Drawing ? _display : _source;
        if (RenderSettings.skybox != want) RenderSettings.skybox = want;
    }

    // A face-centre texel spans 2/faceSize in tangent space; a screen-centre pixel spans
    // 2 tan(fov/2) / height. The cache is used while a texel covers at most maxMagnification pixels.
    bool SharpEnough()
    {
        Camera cam = Camera.main;
        if (!cam) return true;
        float pixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
        float texel = 2f / faceSize;
        return texel <= pixel * maxMagnification;
    }

    /// <summary>
    /// Screenshots the same frame live and cached (time paused in between), for checking the cache
    /// matches. Saves both as PNGs and reports the mean and largest per-channel difference (0..255).
    /// </summary>
    public IEnumerator Compare(string livePath, string cachedPath, System.Action<string> report)
    {
        bool wasCached = useCache;
        float scale = Time.timeScale;
        Time.timeScale = 0f;

        useCache = false;
        Apply(Time.timeSinceLevelLoad);
        yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D live = ScreenCapture.CaptureScreenshotAsTexture();

        useCache = true;
        Apply(Time.timeSinceLevelLoad);
        bool cachedDrawn = Drawing;
        yield return null;
        yield return new WaitForEndOfFrame();
        Texture2D cached = ScreenCapture.CaptureScreenshotAsTexture();

        useCache = wasCached;
        Time.timeScale = scale;

        System.IO.File.WriteAllBytes(livePath, live.EncodeToPNG());
        System.IO.File.WriteAllBytes(cachedPath, cached.EncodeToPNG());

        Color32[] a = live.GetPixels32(), b = cached.GetPixels32();
        long sum = 0;
        int max = 0, over8 = 0;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            int d = Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
            sum += d;
            max = Mathf.Max(max, d);
            if (d > 8) over8++;
        }
        report($"live vs cached ({live.width}x{live.height}, cache drawn: {cachedDrawn}): mean difference {sum / (double)Mathf.Max(1, a.Length):0.00}/255, " +
               $"largest {max}/255, {over8 * 100.0 / Mathf.Max(1, a.Length):0.00}% of pixels differ by more than 8");
        Destroy(live);
        Destroy(cached);
    }
}

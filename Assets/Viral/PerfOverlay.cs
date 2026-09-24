using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// Top-left terminal readout for performance: FPS (average, 1% low, worst frame), where the frame
/// goes (CPU main thread, render thread, GPU) and so what the bottleneck is, rendering counts
/// (batches, SetPass calls, triangles), memory and GC churn, and the resolution / quality it runs at.
/// Hidden at first: P shows or hides it, O copies the whole readout to the clipboard (to paste into a bug report).
///
/// GPU time needs Frame Timing Stats (Player Settings > Other) in builds and a graphics API that
/// reports it; it reads n/a otherwise. Render counts only exist in the Editor and development builds.
/// Nothing to set up: one is created when play starts if the scene has none.
/// </summary>
public class PerfOverlay : MonoBehaviour
{
    public Key toggleKey = Key.P;
    public Key copyKey = Key.O;
    public bool startHidden = true;
    [Tooltip("Seconds between readout refreshes (the numbers are averaged over this window).")]
    [Min(0.1f)] public float refresh = 0.5f;
    [Tooltip("Frames kept for the 1% low and worst frame.")]
    [Min(60)] public int history = 600;
    [Tooltip("Distance from the top-left corner, in 1080p pixels.")]
    public Vector2 margin = new Vector2(24f, 24f);
    [Min(8)] public int fontSize = 17;

    const string Good = "#9eff73", Warn = "#ffbd4d", Bad = "#ff4757", Dim = "#6f96a8";

    Canvas _canvas;
    Text _text;
    Image _panel;
    float[] _frames, _sorted;
    int _frameCount, _frameHead;
    float _windowTime, _windowStart;
    int _windowFrames;
    double _cpuMain, _cpuRender, _gpu;
    int _timedFrames, _gpuFrames;
    long _gcPeak;
    string _plain = "";
    readonly FrameTiming[] _timing = new FrameTiming[1];
    readonly StringBuilder _sb = new StringBuilder(1024);

    // Running totals since ResetTotals, for tools that measure over a stretch (PlaySessionProfiler).
    public static int TotalFrames, TotalTimedFrames, TotalGpuFrames;
    public static double TotalSeconds, TotalMain, TotalRender, TotalGpu;

    public static void ResetTotals()
    {
        TotalFrames = TotalTimedFrames = TotalGpuFrames = 0;
        TotalSeconds = TotalMain = TotalRender = TotalGpu = 0;
    }

    ProfilerRecorder _batches, _setPass, _drawCalls, _tris, _verts, _gcAlloc, _usedMemory, _gcMemory;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<PerfOverlay>()) return; // the scene has its own, with its own settings
        var go = new GameObject("Perf Overlay");
        DontDestroyOnLoad(go);
        go.AddComponent<PerfOverlay>();
    }

    void OnEnable()
    {
        _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
        _setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
        _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
        _tris = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
        _verts = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");
        _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        _usedMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
        _gcMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Used Memory");
    }

    void OnDisable()
    {
        _batches.Dispose(); _setPass.Dispose(); _drawCalls.Dispose(); _tris.Dispose();
        _verts.Dispose(); _gcAlloc.Dispose(); _usedMemory.Dispose(); _gcMemory.Dispose();
    }

    void Start()
    {
        _frames = new float[history];
        _sorted = new float[history];
        Build();
        _canvas.enabled = !startHidden;
        _windowStart = Time.unscaledTime;
    }

    void OnDestroy()
    {
        if (_canvas) Destroy(_canvas.gameObject);
    }

    void Build()
    {
        _canvas = TerminalUI.Canvas("Perf Overlay Canvas", transform, 32000);
        Destroy(_canvas.GetComponent<GraphicRaycaster>()); // read-only, never eats clicks

        _panel = TerminalUI.Graphic<Image>("Panel", _canvas.transform, Vector2.zero, Vector2.zero);
        _panel.color = TerminalUI.Panel;
        _panel.raycastTarget = false;
        RectTransform panel = _panel.rectTransform;
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0f, 1f);
        panel.anchoredPosition = new Vector2(margin.x, -margin.y);
        var fitter = _panel.gameObject.AddComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var layout = _panel.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 10, 10);
        layout.childControlWidth = layout.childControlHeight = true;

        _text = TerminalUI.Graphic<Text>("Readout", panel, Vector2.zero, Vector2.zero);
        TerminalUI.Style(_text, TerminalUI.Font(TerminalUI.DefaultFonts), fontSize, TerminalUI.Text, TextAnchor.UpperLeft);
        _text.supportRichText = true;
        _text.lineSpacing = 1.1f;
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        if (k != null)
        {
            if (k[toggleKey].wasPressedThisFrame) _canvas.enabled = !_canvas.enabled;
            if (k[copyKey].wasPressedThisFrame) GUIUtility.systemCopyBuffer = _plain;
        }

        float dt = Time.unscaledDeltaTime;
        _frames[_frameHead] = dt;
        _frameHead = (_frameHead + 1) % _frames.Length;
        _frameCount = Mathf.Min(_frameCount + 1, _frames.Length);
        _windowTime += dt;
        _windowFrames++;
        TotalFrames++;
        TotalSeconds += dt;
        if (_gcAlloc.Valid) _gcPeak = System.Math.Max(_gcPeak, _gcAlloc.LastValue);

        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
        {
            _cpuMain += _timing[0].cpuMainThreadFrameTime;
            _cpuRender += _timing[0].cpuRenderThreadFrameTime;
            _timedFrames++;
            if (_timing[0].gpuFrameTime > 0) { _gpu += _timing[0].gpuFrameTime; _gpuFrames++; }
            TotalMain += _timing[0].cpuMainThreadFrameTime;
            TotalRender += _timing[0].cpuRenderThreadFrameTime;
            TotalTimedFrames++;
            if (_timing[0].gpuFrameTime > 0) { TotalGpu += _timing[0].gpuFrameTime; TotalGpuFrames++; }
        }

        if (Time.unscaledTime - _windowStart < refresh) return;
        Refresh();
        _windowStart = Time.unscaledTime;
        _windowTime = 0f;
        _windowFrames = 0;
        _cpuMain = _cpuRender = _gpu = 0;
        _timedFrames = _gpuFrames = 0;
        _gcPeak = 0;
    }

    void Refresh()
    {
        float avgMs = _windowTime / Mathf.Max(1, _windowFrames) * 1000f;
        float fps = 1000f / Mathf.Max(0.001f, avgMs);
        Lows(out float lowFps, out float worstMs);

        double main = _timedFrames > 0 ? _cpuMain / _timedFrames : 0;
        double render = _timedFrames > 0 ? _cpuRender / _timedFrames : 0;
        double gpu = _gpuFrames > 0 ? _gpu / _gpuFrames : 0;

        _sb.Clear();
        Line($"<color={Rate(fps)}>{fps,5:0} FPS</color>  {avgMs,5:0.0} ms");
        Line($"1% low <color={Rate(lowFps)}>{lowFps:0}</color>   worst <color={Rate(1000f / worstMs)}>{worstMs:0.0} ms</color>");
        Line("");

        Line($"CPU main   {Ms(main)}");
        Line($"CPU render {Ms(render)}");
        Line($"GPU        {(gpu > 0 ? Ms(gpu) : $"<color={Dim}>n/a</color>")}");
        Line($"bound by   {Bottleneck(main, render, gpu)}");
        Line("");

        if (_batches.Valid && _batches.LastValue > 0)
        {
            Line($"batches {_batches.LastValue,6}   setpass {_setPass.LastValue,5}");
            Line($"draws   {_drawCalls.LastValue,6}   tris {Count(_tris.LastValue)}  verts {Count(_verts.LastValue)}");
        }
        else Line($"<color={Dim}>render counts: editor / dev builds only</color>");
        Line($"GC alloc {Bytes(_gcAlloc.LastValue)}/frame (peak {Bytes(_gcPeak)})");
        Line($"memory   {Bytes(_usedMemory.LastValue)}  (managed {Bytes(_gcMemory.LastValue)})");
        Line("");

        float scale = UniversalRenderPipeline.asset ? UniversalRenderPipeline.asset.renderScale : 1f;
        string quality = QualitySettings.names[QualitySettings.GetQualityLevel()];
        string vsync = QualitySettings.vSyncCount > 0 ? "on" : Application.targetFrameRate > 0 ? $"cap {Application.targetFrameRate}" : "off";
        Line($"{Screen.width}x{Screen.height} @ {scale:0.##}x = {Mathf.RoundToInt(Screen.width * scale)}x{Mathf.RoundToInt(Screen.height * scale)}");
        Line($"quality {quality}   vsync {vsync}");
        Line($"<color={Dim}>{SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})</color>");
        Line($"<color={Dim}>{SystemInfo.processorType.Trim()}</color>");
        Line($"<color={Dim}>{toggleKey} hide  {copyKey} copy</color>");

        _text.text = _sb.ToString();
        _plain = System.Text.RegularExpressions.Regex.Replace(_text.text, "<.*?>", "");
    }

    // 1% low: the average FPS over the slowest 1% of recent frames, and the single slowest frame.
    void Lows(out float lowFps, out float worstMs)
    {
        float[] sorted = _sorted; // reused: the overlay shouldn't add to the GC churn it reports
        System.Array.Copy(_frames, sorted, _frameCount);
        System.Array.Sort(sorted, 0, _frameCount);
        int n = Mathf.Max(1, _frameCount / 100);
        float sum = 0f;
        for (int i = _frameCount - n; i < _frameCount; i++) sum += sorted[i];
        lowFps = n / Mathf.Max(0.00001f, sum);
        worstMs = _frameCount > 0 ? sorted[_frameCount - 1] * 1000f : 0f;
    }

    // Whichever of the three takes longest sets the frame rate; the others wait on it.
    static string Bottleneck(double main, double render, double gpu)
    {
        if (main <= 0) return $"<color={Dim}>n/a</color>";
        if (gpu <= 0) return $"<color={Dim}>CPU {main:0.0} ms (no GPU timing)</color>";
        if (gpu > main * 1.1 && gpu > render * 1.1) return $"<color={Warn}>GPU</color>  <color={Dim}>(lower render scale / effects)</color>";
        if (render > main) return $"<color={Warn}>render thread</color>  <color={Dim}>(too many draws)</color>";
        return $"<color={Warn}>CPU main</color>  <color={Dim}>(scripts / physics)</color>";
    }

    void Line(string s) => _sb.Append(s).Append('\n');

    static string Rate(float fps) => fps >= 55f ? Good : fps >= 30f ? Warn : Bad;
    static string Ms(double ms) => $"<color={Rate((float)(1000.0 / System.Math.Max(0.001, ms)))}>{ms,5:0.0} ms</color>";

    static string Count(long n) => n >= 1_000_000 ? $"{n / 1e6:0.0}M" : n >= 1000 ? $"{n / 1e3:0}k" : n.ToString();

    static string Bytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0.0} KB" : $"{b} B";
}

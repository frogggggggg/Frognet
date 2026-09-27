using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// What each part of the frame costs, by elimination: turns one thing off at a time (SSAO, post, cells, legs, the
/// wall, the holo map...) and measures GPU / CPU frame time from PerfOverlay (FrameTimingManager). Every experiment
/// is bracketed by its own baselines, right before and after (their mean is its baseline): a laptop heats up and
/// throttles over a run, and a baseline minutes old measured the drift, not the cost.
///
/// In a build (or play mode) press F9: it freezes game time (the same frame keeps rendering, so GPU samples compare
/// like with like), runs for ~3 minutes, shows its progress top-left and writes benchmark.txt next to the player log
/// (Application.persistentDataPath). The editor's
/// PlaySessionProfiler runs the same list (<see cref="Experiments"/>). Measure GPU costs here, in a build: in the
/// editor (its own views on the same GPU) the GPU deltas swung +-7 ms.
/// </summary>
public class PerfBenchmark : MonoBehaviour
{
    public class Experiment
    {
        public string name;
        public Action off, on;
        public bool shot; // a look change, not just an off switch: screenshot it and compare with the baseline's
    }

    public struct Result
    {
        public string name;
        public double fps, main, render, gpu;
        public double baseMain, baseGpu; // the mean of the baselines right before and after it
        public string look;              // shot experiments: how far the frame is from the baseline's
    }

    public const float Settle = 1f, Measure = 3f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<PerfBenchmark>()) return;
        var go = new GameObject("Perf Benchmark") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<PerfBenchmark>();
    }

    static PerfBenchmark s_instance;
    string _status;
    Coroutine _run;

    void Awake() => s_instance = this;

    float _timeScale = -1f; // while frozen: the scale to put back

    void OnDisable()
    {
        Restore?.Invoke(); // stopped midway: put the running experiment back
        Restore = null;
        _run = null;
        Unfreeze();
    }

    void Unfreeze()
    {
        if (_timeScale >= 0f) Time.timeScale = _timeScale;
        _timeScale = -1f;
    }

    void Update()
    {
        Keyboard k = Keyboard.current;
        if (_run == null && k != null && k.f9Key.wasPressedThisFrame) _run = StartCoroutine(Run());
    }

    /// <summary>Runs the experiments into 'results' (PlaySessionProfiler); 'done' when finished. False if busy.</summary>
    public static bool Start(List<Result> results, Action done)
    {
        if (!s_instance || s_instance._run != null) return false;
        s_instance._run = s_instance.StartCoroutine(s_instance.RunFor(results, done));
        return true;
    }

    IEnumerator RunFor(List<Result> results, Action done)
    {
        yield return RunAll(Experiments(), results, null);
        _run = null;
        done?.Invoke();
    }

    void OnGUI()
    {
        if (_status != null) GUI.Label(new Rect(10, 10, 900, 24), _status);
    }

    IEnumerator Run()
    {
        var results = new List<Result>();
        // Game time frozen: the frame keeps rendering but nothing moves, streams in or generates, so every sample
        // draws the same scene (running, the baseline GPU time wandered 6 -> 15 ms as the world changed round it).
        _timeScale = Time.timeScale;
        Time.timeScale = 0f;
        _status = "Benchmark: warming up (game time frozen)";
        yield return new WaitForSecondsRealtime(4f);
        string path = Path.Combine(Application.persistentDataPath, "benchmark.txt");
        yield return RunAll(Experiments(), results, (i, n, name) => _status = $"Benchmark {i + 1}/{n}: {name} (game time frozen)");
        Unfreeze();
        File.WriteAllText(path, Report(results) + "\nGame time was frozen (the same frame drawn throughout): GPU deltas are clean; " +
                                "CPU ones leave out the simulation.\n");
        _status = "Benchmark done: " + path;
        Debug.Log("PerfBenchmark: " + path);
        yield return new WaitForSecondsRealtime(6f);
        _status = null;
        _run = null;
    }

    /// <summary>Runs every experiment bracketed by baselines into 'results'; 'progress' (index, count, name) each step.</summary>
    public static IEnumerator RunAll(List<Experiment> experiments, List<Result> results, Action<int, int, string> progress)
    {
        if (s_baseShot) Object.Destroy(s_baseShot);
        s_baseShot = null;
        Result before = default;
        bool haveBefore = false;
        for (int i = 0; i < experiments.Count; i++)
        {
            Experiment x = experiments[i];
            progress?.Invoke(i, experiments.Count, x.name);
            if (!haveBefore)
            {
                yield return Sample(Baseline, r => before = r);
                haveBefore = true;
            }
            Result result = default;
            if (x.shot && s_baseShot == null) yield return Shot(tex => s_baseShot = tex, "baseline");
            x.off?.Invoke();
            Restore = x.on;
            yield return Sample(x.name, r => result = r);
            if (x.shot) yield return Shot(tex => { result.look = Compare(s_baseShot, tex); Object.Destroy(tex); }, x.name);
            x.on?.Invoke();
            Restore = null;
            Result after = default;
            yield return Sample(Baseline, r => after = r);
            result.baseMain = (before.main + after.main) * 0.5;
            result.baseGpu = (before.gpu + after.gpu) * 0.5;
            results.Add(result);
            before = after; // this one's after is the next one's before
        }
    }

    static Texture2D s_baseShot;

    // The frame as shown, saved as bench_<name>.png next to the report.
    static IEnumerator Shot(Action<Texture2D> done, string name)
    {
        yield return new WaitForEndOfFrame();
        Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
        try
        {
            string file = "bench_" + string.Join("_", name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_') + ".png";
            File.WriteAllBytes(Path.Combine(Application.persistentDataPath, file), tex.EncodeToPNG());
        }
        catch (Exception e) { Debug.LogWarning("PerfBenchmark: screenshot not saved: " + e.Message); }
        done(tex);
    }

    // Mean difference (0-255) and the share of pixels off by more than 8.
    static string Compare(Texture2D a, Texture2D b)
    {
        if (!a || !b || a.width != b.width || a.height != b.height) return "no baseline shot";
        Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
        double sum = 0;
        int big = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Max(Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Abs(pa[i].g - pb[i].g)), Math.Abs(pa[i].b - pb[i].b));
            sum += d;
            if (d > 8) big++;
        }
        return $"look: mean diff {sum / pa.Length:0.00}/255, {100.0 * big / pa.Length:0.0}% of pixels off by > 8";
    }

    /// <summary>Puts back what the running experiment turned off (for a run stopped midway).</summary>
    public static Action Restore;

    const string Baseline = "baseline";

    static IEnumerator Sample(string name, Action<Result> done)
    {
        yield return new WaitForSecondsRealtime(Settle);
        PerfOverlay.ResetTotals();
        yield return new WaitForSecondsRealtime(Measure);
        done(new Result
        {
            name = name,
            fps = PerfOverlay.TotalSeconds > 0 ? PerfOverlay.TotalFrames / PerfOverlay.TotalSeconds : 0,
            main = PerfOverlay.TotalTimedFrames > 0 ? PerfOverlay.TotalMain / PerfOverlay.TotalTimedFrames : 0,
            render = PerfOverlay.TotalTimedFrames > 0 ? PerfOverlay.TotalRender / PerfOverlay.TotalTimedFrames : 0,
            gpu = PerfOverlay.TotalGpuFrames > 0 ? PerfOverlay.TotalGpu / PerfOverlay.TotalGpuFrames : 0,
        });
    }

    public static string Report(List<Result> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Benchmark {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {(Application.isEditor ? "editor" : Debug.isDebugBuild ? "development build" : "build")}");
        sb.AppendLine($"GPU {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), CPU {SystemInfo.processorType.Trim()}");
        float scale = UniversalRenderPipeline.asset ? UniversalRenderPipeline.asset.renderScale : 1f;
        sb.AppendLine($"Screen {Screen.width}x{Screen.height} @ render scale {scale}, quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}, " +
                      $"vsync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}");
        sb.AppendLine().AppendLine("== Cost by elimination (each thing off on its own, vs the mean of the baselines right before and after it; negative delta = it was costing that much) ==");
        sb.AppendLine($"{"experiment",-44} {"fps",6} {"GPU ms",8} {"base",7} {"dGPU",7} {"main ms",8} {"base",7} {"dMain",7}");
        foreach (Result r in results)
            sb.AppendLine($"{r.name,-44} {r.fps,6:0} {r.gpu,8:0.00} {r.baseGpu,7:0.00} {r.gpu - r.baseGpu,7:+0.00;-0.00} {r.main,8:0.00} {r.baseMain,7:0.00} {r.main - r.baseMain,7:+0.00;-0.00}" +
                          (r.look != null ? "   " + r.look : ""));
        return sb.ToString();
    }

    // ---------------- experiments ----------------

    public static List<Experiment> Experiments() => new List<Experiment>
    {
        // Look changes to try go first, screenshotted (Ssao(...), DepthPriming(...)): tried so far, see Docs/rendering.md
        // (half-res SSAO + low blur kept; depth priming drops the vessel wall, its passes lack DepthNormals).
        Features("SSAO off", f => f.GetType().Name == "ScreenSpaceAmbientOcclusion"),
        Features("x-ray / stencil RenderObjects passes off", f => f.GetType().Name == "RenderObjects"),
        Features("TransparentDepthForPost off", f => f is TransparentDepthForPostFeature),
        Features("screen-invert transparent depth off", f => f.GetType().Name == "ScreenInvertTransparentDepthFeature"),
        PostOff(),
        ShadowsOff(),
        Renderers("cells cast no shadows", r => UsesShader(r, "Custom/BloodCellTriplanar"), shadowsOnly: true),
        Renderers("cells hidden", r => UsesShader(r, "Custom/BloodCellTriplanar")),
        Behaviours<LegRenderer>("legs hidden"),
        Renderers("ropes hidden", r => UsesShader(r, "Custom/RopeBlood")),
        Renderers("crystals hidden", r => UsesShader(r, "Custom/AstrophageCrystalTop")),
        LiveSky(),
        SkyOff(),
        VesselWallOff(),
        Behaviours<AmbientParticles>("ambient specks off"),
        Behaviours<HoloMap>("holo map off"),
        Behaviours<FarField>("far field off"),
        Behaviours<WhiteBloodCells>("white blood cells off (sim + draw)"),
        Behaviours<ResourceField>("resource chunks off (sim + draw)"),
        Behaviours<ImmuneSystem>("immune system off (antibodies, motes)"),
    };

    // Every renderer data asset loaded (the active quality tier's among them).
    static IEnumerable<ScriptableRendererFeature> AllFeatures()
    {
        foreach (UniversalRendererData data in Resources.FindObjectsOfTypeAll<UniversalRendererData>())
            foreach (ScriptableRendererFeature f in data.rendererFeatures)
                if (f) yield return f;
    }

    static Experiment Features(string name, Func<ScriptableRendererFeature, bool> match)
    {
        var changed = new List<ScriptableRendererFeature>();
        return new Experiment
        {
            name = name,
            off = () =>
            {
                changed.Clear();
                foreach (ScriptableRendererFeature f in AllFeatures())
                    if (f.isActive && match(f)) { f.SetActive(false); changed.Add(f); }
            },
            on = () => { foreach (ScriptableRendererFeature f in changed) if (f) f.SetActive(true); changed.Clear(); },
        };
    }

    // SSAO settings changed through reflection (they're private to URP's feature), put back after.
    static Experiment Ssao(string name, params (string field, int value)[] set)
    {
        var old = new List<(object settings, System.Reflection.FieldInfo f, object value)>();
        return new Experiment
        {
            name = name,
            shot = true,
            off = () =>
            {
                old.Clear();
                foreach (ScriptableRendererFeature feature in AllFeatures())
                {
                    if (!feature.isActive || feature.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                    object settings = feature.GetType().GetField("m_Settings",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(feature);
                    if (settings == null) continue;
                    foreach ((string field, int value) in set)
                    {
                        System.Reflection.FieldInfo f = settings.GetType().GetField(field,
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (f == null) { Debug.LogWarning($"PerfBenchmark: no SSAO setting '{field}'"); continue; }
                        old.Add((settings, f, f.GetValue(settings)));
                        f.SetValue(settings, f.FieldType == typeof(bool) ? value != 0 : f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, value) : (object)value);
                    }
                }
            },
            on = () =>
            {
                for (int i = old.Count - 1; i >= 0; i--) old[i].f.SetValue(old[i].settings, old[i].value);
                old.Clear();
            },
        };
    }

    static Experiment DepthPriming(string name)
    {
        var old = new List<(UniversalRendererData data, DepthPrimingMode mode)>();
        return new Experiment
        {
            name = name,
            shot = true,
            off = () =>
            {
                old.Clear();
                foreach (UniversalRendererData data in Resources.FindObjectsOfTypeAll<UniversalRendererData>())
                {
                    old.Add((data, data.depthPrimingMode));
                    data.depthPrimingMode = DepthPrimingMode.Forced; // the setter marks it dirty: the renderer is rebuilt
                }
            },
            on = () =>
            {
                foreach ((UniversalRendererData data, DepthPrimingMode mode) in old) if (data) data.depthPrimingMode = mode;
                old.Clear();
            },
        };
    }

    static bool UsesShader(Renderer r, string shader)
    {
        Material m = r.sharedMaterial;
        return m && m.shader && m.shader.name == shader;
    }

    static Experiment Renderers(string name, Func<Renderer, bool> match, bool shadowsOnly = false)
    {
        var changed = new List<(Renderer r, ShadowCastingMode mode)>();
        return new Experiment
        {
            name = name,
            off = () =>
            {
                changed.Clear();
                foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (!r.enabled || !match(r)) continue;
                    changed.Add((r, r.shadowCastingMode));
                    if (shadowsOnly) r.shadowCastingMode = ShadowCastingMode.Off;
                    else r.enabled = false;
                }
            },
            on = () =>
            {
                foreach ((Renderer r, ShadowCastingMode mode) in changed)
                {
                    if (!r) continue;
                    if (shadowsOnly) r.shadowCastingMode = mode;
                    else r.enabled = true;
                }
                changed.Clear();
            },
        };
    }

    static Experiment Behaviours<T>(string name) where T : Behaviour
    {
        var changed = new List<T>();
        return new Experiment
        {
            name = name,
            off = () =>
            {
                changed.Clear();
                foreach (T b in Resources.FindObjectsOfTypeAll<T>()) // includes hidden (HideAndDontSave) ones
                    if (b.enabled && b.gameObject.scene.IsValid()) { b.enabled = false; changed.Add(b); } // not prefab assets
            },
            on = () => { foreach (T b in changed) if (b) b.enabled = true; changed.Clear(); },
        };
    }

    static Experiment ShadowsOff()
    {
        var changed = new List<(Light l, LightShadows s)>();
        return new Experiment
        {
            name = "all real-time shadows off",
            off = () =>
            {
                changed.Clear();
                foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.enabled && l.shadows != LightShadows.None) { changed.Add((l, l.shadows)); l.shadows = LightShadows.None; }
            },
            on = () => { foreach ((Light l, LightShadows s) in changed) if (l) l.shadows = s; changed.Clear(); },
        };
    }

    static Experiment PostOff()
    {
        var changed = new List<UniversalAdditionalCameraData>();
        return new Experiment
        {
            name = "post-processing off",
            off = () =>
            {
                changed.Clear();
                foreach (Camera c in Camera.allCameras)
                    if (c.TryGetComponent(out UniversalAdditionalCameraData d) && d.renderPostProcessing)
                    { d.renderPostProcessing = false; changed.Add(d); }
            },
            on = () => { foreach (UniversalAdditionalCameraData d in changed) if (d) d.renderPostProcessing = true; changed.Clear(); },
        };
    }

    static Experiment LiveSky()
    {
        var changed = new List<SkyboxCache>();
        return new Experiment
        {
            name = "sky drawn live (cache off)",
            off = () =>
            {
                changed.Clear();
                foreach (SkyboxCache c in Object.FindObjectsByType<SkyboxCache>(FindObjectsSortMode.None))
                    if (c.useCache) { c.useCache = false; changed.Add(c); }
            },
            on = () => { foreach (SkyboxCache c in changed) if (c) c.useCache = true; changed.Clear(); },
        };
    }

    static Experiment SkyOff()
    {
        Material sky = null;
        var changed = new List<Camera>();
        var caches = new List<SkyboxCache>();
        return new Experiment
        {
            name = "skybox off (solid colour)",
            off = () =>
            {
                caches.Clear();
                foreach (SkyboxCache c in Object.FindObjectsByType<SkyboxCache>(FindObjectsSortMode.None))
                    if (c.enabled) { c.enabled = false; caches.Add(c); } // puts the live sky back, then it's removed
                sky = RenderSettings.skybox;
                RenderSettings.skybox = null;
                changed.Clear();
                foreach (Camera c in Camera.allCameras)
                    if (c.clearFlags == CameraClearFlags.Skybox) { c.clearFlags = CameraClearFlags.SolidColor; changed.Add(c); }
            },
            on = () =>
            {
                RenderSettings.skybox = sky;
                foreach (Camera c in changed) if (c) c.clearFlags = CameraClearFlags.Skybox;
                changed.Clear();
                foreach (SkyboxCache c in caches) if (c) c.enabled = true;
                caches.Clear();
            },
        };
    }

    // Wall hidden with the sky kept off too (the vessel gives the camera back its skybox when the wall goes, so
    // the sky material itself is removed), so it measures the wall alone.
    static Experiment VesselWallOff()
    {
        Experiment sky = SkyOff();
        Vessel vessel = null;
        return new Experiment
        {
            name = "vessel wall hidden (sky still off)",
            off = () =>
            {
                vessel = Vessel.Active;
                if (vessel) vessel.drawWall = false;
                sky.off();
            },
            on = () =>
            {
                sky.on();
                if (vessel) vessel.drawWall = true;
            },
        };
    }
}

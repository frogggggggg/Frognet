using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

/// <summary>
/// Hands-off performance capture, for when nobody is watching the Profiler window. Start it from
/// Tools > Profile Play Session, or by creating Temp/ClaudeProfile.request (so a script or another
/// tool can ask for one). It enters play mode, lets the scene settle, then:
///   1. GPU cost by elimination: turns one thing off at a time (SSAO, shadows, cells, legs, sky,
///      post, the custom render passes...) and measures how much GPU / CPU time drops, from
///      PerfOverlay's frame timings. Each thing is put back before the next.
///   2. CPU: records the Profiler for a few rounds and totals every marker's self time and GC
///      allocations on the main and render threads, plus what the slowest frames spent it on.
/// Then play mode ends and the report goes to Temp/ClaudeProfile.txt. Leaving play mode early
/// stops it (everything is restored) and writes what it has.
/// </summary>
[InitializeOnLoad]
static class PlaySessionProfiler
{
    const string RequestPath = "Temp/ClaudeProfile.request", ReportPath = "Temp/ClaudeProfile.txt";
    const string PhaseKey = "PlaySessionProfiler.phase";
    const double Warmup = 6.0, Settle = 1.0, Measure = 3.0;
    const int Rounds = 4, RoundFrames = 240;

    class Experiment
    {
        public string name;
        public Action off, on;
    }

    struct Result
    {
        public string name;
        public double fps, main, render, gpu;
    }

    class Marker
    {
        public double self, maxSelf, gc;
        public int frames;
        public string parent;
    }

    static IEnumerator s_job;
    static Action s_restore; // puts back whatever the running experiment turned off
    static readonly List<Result> s_results = new List<Result>();
    static readonly Dictionary<string, Marker> s_main = new Dictionary<string, Marker>(), s_render = new Dictionary<string, Marker>();
    static readonly List<(float ms, float player, float editor, List<KeyValuePair<string, double>> top)> s_frames =
        new List<(float, float, float, List<KeyValuePair<string, double>>)>();
    static readonly StringBuilder s_notes = new StringBuilder();
    static readonly List<string> s_ropes = new List<string>();

    static PlaySessionProfiler()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(PhaseKey, "") == "enter") Begin();
            if (state == PlayModeStateChange.ExitingPlayMode && s_job != null) Stop("play mode was left before the run finished");
        };
    }

    [MenuItem("Tools/Profile Play Session")]
    static void Request()
    {
        if (s_job != null) return;
        OpenProfiler();
        if (EditorApplication.isPlaying) { Begin(); return; }
        SessionState.SetString(PhaseKey, "enter"); // survives the domain reload on entering play mode
        EditorApplication.isPlaying = true;
    }

    static void Update()
    {
        if (s_job == null && File.Exists(RequestPath) && !EditorApplication.isCompiling)
        {
            File.Delete(RequestPath);
            Request();
        }
        if (s_job == null) return;
        try
        {
            if (!s_job.MoveNext()) s_job = null;
        }
        catch (Exception e)
        {
            s_notes.AppendLine("Run failed: " + e);
            Stop("an error stopped the run");
        }
    }

    // The editor only reliably collects Profiler frames while its window exists. Opened (or found)
    // without taking focus, so the Game view keeps the keyboard.
    internal static void OpenProfiler()
    {
        if (Resources.FindObjectsOfTypeAll<ProfilerWindow>().Length == 0)
            EditorWindow.GetWindow<ProfilerWindow>(false, "Profiler", false);
    }

    static void Begin()
    {
        SessionState.EraseString(PhaseKey);
        s_results.Clear(); s_main.Clear(); s_render.Clear(); s_frames.Clear(); s_notes.Clear();
        s_renderThread = -2;
        s_ropes.Clear();
        s_job = Run();
        Debug.Log("PlaySessionProfiler: started. Leave the scene running for about a minute.");
    }

    static void Stop(string why)
    {
        s_restore?.Invoke();
        s_restore = null;
        ProfilerDriver.enabled = false;
        s_job = null;
        s_notes.AppendLine("Stopped early: " + why);
        WriteReport();
    }

    static double Now => EditorApplication.timeSinceStartup;

    static IEnumerator Run()
    {
        Application.runInBackground = true; // keep playing while the editor isn't focused
        ProfilerDriver.enabled = false;
        for (double end = Now + Warmup; Now < end;) yield return null;

        // 0. Does the sky cache look like the live sky?
        SkyboxCache cache = Object.FindAnyObjectByType<SkyboxCache>();
        if (cache && cache.isActiveAndEnabled)
        {
            bool done = false;
            cache.StartCoroutine(cache.Compare("Temp/SkyCheck_live.png", "Temp/SkyCheck_cached.png",
                line => { s_notes.AppendLine("Sky cache: " + line + " (screenshots in Temp/SkyCheck_*.png)"); done = true; }));
            for (double end = Now + 10; !done && Now < end;) yield return null;
            if (!done) s_notes.AppendLine("Sky cache: comparison timed out");
        }
        else s_notes.AppendLine("Sky cache: not running");

        // 1. GPU by elimination.
        List<Experiment> experiments = Experiments();
        foreach (Experiment x in experiments)
        {
            x.off?.Invoke();
            s_restore = x.on;
            for (double end = Now + Settle; Now < end;) yield return null;
            PerfOverlay.ResetTotals();
            for (double end = Now + Measure; Now < end;) yield return null;
            s_results.Add(Sample(x.name));
            x.on?.Invoke();
            s_restore = null;
        }

        // 2. CPU markers, a few short rounds so the Profiler's frame buffer never drops any.
        ProfilerDriver.profileEditor = false;
        for (int round = 0; round < Rounds; round++)
        {
            ProfilerDriver.ClearAllFrames();
            ProfilerDriver.enabled = true;
            double timeout = Now + 15.0;
            while (Now < timeout && (ProfilerDriver.firstFrameIndex < 0 ||
                                     ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex < RoundFrames))
                yield return null;
            ProfilerDriver.enabled = false;
            foreach (VirusRope rope in Object.FindObjectsByType<VirusRope>(FindObjectsSortMode.None))
                s_ropes.Add($"round {round + 1}, {rope.name}: {rope.DebugSummary()}");
            yield return null;

            // The newest frame can still be incomplete.
            for (int f = Mathf.Max(0, ProfilerDriver.firstFrameIndex); f < ProfilerDriver.lastFrameIndex; f++)
                AnalyzeFrame(f);
        }

        WriteReport();
        Debug.Log($"PlaySessionProfiler: report written to {Path.GetFullPath(ReportPath)}");
        s_job = null; // finished: leaving play mode now isn't an early stop
        EditorApplication.isPlaying = false;
    }

    static Result Sample(string name) => new Result
    {
        name = name,
        fps = PerfOverlay.TotalSeconds > 0 ? PerfOverlay.TotalFrames / PerfOverlay.TotalSeconds : 0,
        main = PerfOverlay.TotalTimedFrames > 0 ? PerfOverlay.TotalMain / PerfOverlay.TotalTimedFrames : 0,
        render = PerfOverlay.TotalTimedFrames > 0 ? PerfOverlay.TotalRender / PerfOverlay.TotalTimedFrames : 0,
        gpu = PerfOverlay.TotalGpuFrames > 0 ? PerfOverlay.TotalGpu / PerfOverlay.TotalGpuFrames : 0,
    };

    // ------------------------------------------------------------------ experiments

    static List<Experiment> Experiments() => new List<Experiment>
    {
        // Baselines between groups: each result is compared with the latest one, so heat
        // building up over the run (a laptop slows down) doesn't read as an effect.
        new Experiment { name = Baseline + " (warm-up, GPU timing can lag)" },
        new Experiment { name = Baseline },
        Features("SSAO off", f => f.GetType().Name == "ScreenSpaceAmbientOcclusion"),
        Features("x-ray / stencil RenderObjects passes off", f => f.GetType().Name == "RenderObjects"),
        Features("TransparentDepthForPost off", f => f is TransparentDepthForPostFeature),
        new Experiment { name = Baseline },
        PostOff(),
        ShadowsOff(),
        Renderers("cells cast no shadows", r => UsesShader(r, "Custom/BloodCellTriplanar"), shadowsOnly: true),
        new Experiment { name = Baseline },
        Renderers("cells hidden", r => UsesShader(r, "Custom/BloodCellTriplanar")),
        Behaviours<LegRenderer>("legs hidden"),
        Renderers("ropes hidden", r => UsesShader(r, "Custom/RopeBlood")),
        new Experiment { name = Baseline },
        Renderers("crystals hidden", r => UsesShader(r, "Custom/AstrophageCrystalTop")),
        LiveSky(),
        SkyOff(),
        new Experiment { name = Baseline },
    };

    const string Baseline = "baseline";

    static IEnumerable<ScriptableRendererFeature> AllFeatures()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:UniversalRendererData"))
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(AssetDatabase.GUIDToAssetPath(guid));
            if (data)
                foreach (ScriptableRendererFeature f in data.rendererFeatures)
                    if (f) yield return f;
        }
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
                    if (b.enabled && !EditorUtility.IsPersistent(b)) { b.enabled = false; changed.Add(b); } // not prefab assets
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

    // ------------------------------------------------------------------ profiler frames

    static int s_renderThread = -2; // -2: not looked up yet, -1: none

    static void AnalyzeFrame(int frame)
    {
        using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(frame, 0,
                   HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnSelfTime, false))
        {
            if (view == null || !view.valid) return;
            var perFrame = new Dictionary<string, double>();
            Walk(view, s_main, perFrame, out float player, out float editor);
            List<KeyValuePair<string, double>> top = perFrame.OrderByDescending(p => p.Value).Take(8).ToList();
            s_frames.Add((view.frameTimeMs, player, editor, top));
        }

        if (s_renderThread == -2)
        {
            s_renderThread = -1;
            for (int t = 1; t < 128; t++)
                using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, t))
                {
                    if (raw == null || !raw.valid) break;
                    if (raw.threadName == "Render Thread") { s_renderThread = t; break; }
                }
        }
        if (s_renderThread < 0) return;
        using (HierarchyFrameDataView view = ProfilerDriver.GetHierarchyFrameDataView(frame, s_renderThread,
                   HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnSelfTime, false))
            if (view != null && view.valid)
                Walk(view, s_render, null, out _, out _);
    }

    static readonly List<int> s_children = new List<int>();

    static void Walk(HierarchyFrameDataView view, Dictionary<string, Marker> into, Dictionary<string, double> perFrame,
                     out float player, out float editor)
    {
        player = editor = 0f;
        var stack = new Stack<(int id, string parent)>();
        view.GetItemChildren(view.GetRootItemID(), s_children);
        foreach (int c in s_children) stack.Push((c, ""));
        var seen = new HashSet<string>();

        while (stack.Count > 0)
        {
            (int id, string parent) = stack.Pop();
            string name = view.GetItemName(id);

            if (name == "GC.Alloc")
            {
                // Charged to whatever allocated it.
                Get(into, parent, "").gc += view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnGcMemory);
                continue;
            }

            float self = view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
            if (name == "PlayerLoop") player += view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
            if (name == "EditorLoop") editor += view.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);

            Marker m = Get(into, name, parent);
            m.self += self;
            if (seen.Add(name)) m.frames++;
            if (perFrame != null) { perFrame.TryGetValue(name, out double v); perFrame[name] = v + self; }
            m.maxSelf = Math.Max(m.maxSelf, self); // per sample site, close enough for "does it spike"

            view.GetItemChildren(id, s_children);
            foreach (int c in s_children) stack.Push((c, name));
        }
    }

    static Marker Get(Dictionary<string, Marker> into, string name, string parent)
    {
        if (!into.TryGetValue(name, out Marker m)) into[name] = m = new Marker { parent = parent };
        return m;
    }

    // ------------------------------------------------------------------ report

    static void WriteReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Play session profile, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"GPU {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}), CPU {SystemInfo.processorType.Trim()}");
        float scale = UniversalRenderPipeline.asset ? UniversalRenderPipeline.asset.renderScale : 1f;
        sb.AppendLine($"Screen {Screen.width}x{Screen.height} @ render scale {scale}, quality {QualitySettings.names[QualitySettings.GetQualityLevel()]}, " +
                      $"vsync {QualitySettings.vSyncCount}, target fps {Application.targetFrameRate}");
        sb.AppendLine($"Editor focused: {InternalEditorUtility.isApplicationActive}. Scene views open: {SceneView.sceneViews.Count} (each one can render the world again).");
        if (s_notes.Length > 0) sb.AppendLine().Append(s_notes);

        if (s_results.Count > 0)
        {
            sb.AppendLine().AppendLine("== Cost by elimination (each thing off on its own, vs the latest baseline; negative delta = it was costing that much) ==");
            sb.AppendLine($"{"experiment",-44} {"fps",6} {"GPU ms",8} {"dGPU",7} {"main ms",8} {"dMain",7} {"render ms",9}");
            Result b = s_results[0];
            foreach (Result r in s_results)
            {
                if (r.name == Baseline) b = r;
                sb.AppendLine($"{r.name,-44} {r.fps,6:0} {r.gpu,8:0.00} {r.gpu - b.gpu,7:+0.00;-0.00} {r.main,8:0.00} {r.main - b.main,7:+0.00;-0.00} {r.render,9:0.00}");
            }
        }
        foreach (string line in s_ropes) sb.AppendLine("Ropes: " + line);

        if (s_frames.Count > 0)
        {
            var ms = s_frames.Select(f => f.ms).OrderBy(x => x).ToList();
            float Pct(float p) => ms[Mathf.Clamp(Mathf.RoundToInt(p * (ms.Count - 1)), 0, ms.Count - 1)];
            int n = s_frames.Count;
            sb.AppendLine().AppendLine($"== CPU profiler, {n} frames (main thread) ==");
            sb.AppendLine($"frame ms: median {Pct(0.5f):0.0}, p95 {Pct(0.95f):0.0}, p99 {Pct(0.99f):0.0}, max {ms[^1]:0.0}");
            sb.AppendLine($"PlayerLoop (game) avg {s_frames.Average(f => f.player):0.00} ms, EditorLoop (editor overhead) avg {s_frames.Average(f => f.editor):0.00} ms");

            AppendMarkers(sb, "Top main-thread self time (avg ms per frame)", s_main, n, 35);
            AppendGc(sb, s_main, n);
            if (s_render.Count > 0) AppendMarkers(sb, "Top render-thread self time (avg ms per frame)", s_render, n, 20);

            sb.AppendLine().AppendLine($"== Slowest frames (median is {Pct(0.5f):0.0} ms) ==");
            foreach (var f in s_frames.OrderByDescending(f => f.ms).Take(10))
            {
                sb.AppendLine($"{f.ms:0.0} ms (PlayerLoop {f.player:0.0}, EditorLoop {f.editor:0.0}):");
                foreach (var p in f.top) sb.AppendLine($"    {p.Value,6:0.00}  {p.Key}");
            }
        }

        File.WriteAllText(ReportPath, sb.ToString());
    }

    static void AppendMarkers(StringBuilder sb, string title, Dictionary<string, Marker> markers, int frames, int count)
    {
        sb.AppendLine().AppendLine($"-- {title} --");
        foreach (var p in markers.OrderByDescending(p => p.Value.self).Take(count))
            sb.AppendLine($"{p.Value.self / frames,7:0.000}  max {p.Value.maxSelf,6:0.00}  in {p.Value.frames * 100 / frames,3}% of frames  {p.Key}" +
                          (string.IsNullOrEmpty(p.Value.parent) ? "" : $"   <- {p.Value.parent}"));
    }

    static void AppendGc(StringBuilder sb, Dictionary<string, Marker> markers, int frames)
    {
        sb.AppendLine().AppendLine("-- GC allocations by caller (bytes per frame) --");
        foreach (var p in markers.Where(p => p.Value.gc > 0).OrderByDescending(p => p.Value.gc).Take(20))
            sb.AppendLine($"{p.Value.gc / frames,9:0}  {p.Key}" + (string.IsNullOrEmpty(p.Value.parent) ? "" : $"   <- {p.Value.parent}"));
    }
}

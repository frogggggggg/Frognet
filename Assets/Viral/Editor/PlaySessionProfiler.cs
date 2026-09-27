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
///   1. Cost by elimination: PerfBenchmark's experiments (the same list a build runs on F9), each
///      bracketed by its own baselines. GPU deltas in the editor are noise (+-7 ms): measure those in a build.
///   2. CPU: records the Profiler for a few rounds and totals every marker's self time and GC
///      allocations on the main and render threads, plus what the slowest frames spent it on.
/// Then play mode ends and the report goes to Temp/ClaudeProfile.txt. Leaving play mode early
/// stops it (everything is restored) and writes what it has.
///
/// Temp/ClaudeCapture.request (or Tools > Analyze Profiler Capture) instead reports on the frames the Profiler window
/// holds now, e.g. from a connected development build: CPU markers as above plus GPU time per sample (GPU module on
/// while recording), to Temp/ClaudeCapture.txt. Nothing is run.
/// </summary>
[InitializeOnLoad]
static class PlaySessionProfiler
{
    const string RequestPath = "Temp/ClaudeProfile.request", ReportPath = "Temp/ClaudeProfile.txt";
    const string CaptureRequestPath = "Temp/ClaudeCapture.request", CaptureReportPath = "Temp/ClaudeCapture.txt";
    const string PhaseKey = "PlaySessionProfiler.phase";
    const double Warmup = 6.0, Settle = 1.0, Measure = 3.0;
    const int Rounds = 4, RoundFrames = 240;

    class Marker
    {
        public double self, maxSelf, gc;
        public int frames;
        public string parent;
    }

    static IEnumerator s_job;
    static bool s_quick;
    static bool s_refreshed;
    static readonly List<PerfBenchmark.Result> s_results = new List<PerfBenchmark.Result>();
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
        if (s_job == null && File.Exists(CaptureRequestPath))
        {
            File.Delete(CaptureRequestPath);
            AnalyzeCapture();
        }
        if (s_job == null && File.Exists(RequestPath) && !EditorApplication.isCompiling)
        {
            // Scripts edited outside Unity aren't imported until the editor gets focus: import them first, so the
            // run measures the current code (a compile reloads the domain and this comes round again).
            if (!s_refreshed)
            {
                s_refreshed = true;
                AssetDatabase.Refresh();
                if (EditorApplication.isCompiling) return;
            }
            s_quick = File.ReadAllText(RequestPath).Contains("quick"); // "quick": skip the elimination experiments
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
        s_gcStacks.Clear(); s_gcFrames = 0;
        s_results.Clear(); s_main.Clear(); s_render.Clear(); s_frames.Clear(); s_notes.Clear();
        s_renderThread = -2;
        s_ropes.Clear();
        s_job = Run();
        Debug.Log("PlaySessionProfiler: started. Leave the scene running for about a minute.");
    }

    static void Stop(string why)
    {
        UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false;
        PerfBenchmark.Restore?.Invoke();
        PerfBenchmark.Restore = null;
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
        s_notes.AppendLine($"Counts, after warm-up (t {Time.time:0}s): {Counts()}");

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
        // Shared with the in-build benchmark (PerfBenchmark, F9): each experiment bracketed by its own baselines.
        if (!s_quick)
        {
            bool done = false;
            if (PerfBenchmark.Start(s_results, () => done = true))
                while (!done) yield return null;
            else s_notes.AppendLine("Experiments: no PerfBenchmark in the scene (or it's busy); skipped.");
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
            s_notes.AppendLine($"Counts, round {round + 1} (t {Time.time:0}s): {Counts()}");
            yield return null;

            // The newest frame can still be incomplete.
            for (int f = Mathf.Max(0, ProfilerDriver.firstFrameIndex); f < ProfilerDriver.lastFrameIndex; f++)
                AnalyzeFrame(f);
        }

        // 3. GPU time per pass: a round with the GPU module on (it costs CPU time, so kept out of the rounds above).
        s_gpu.Clear(); s_gpuFrames.Clear();
#pragma warning disable CS0618 // SetAreaEnabled: still the way to switch the GPU module on from script
        bool gpuWas = ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.GPU);
        ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.GPU, true);
        ProfilerDriver.ClearAllFrames();
        ProfilerDriver.enabled = true;
        for (double timeout = Now + 15.0; Now < timeout && (ProfilerDriver.firstFrameIndex < 0 ||
                 ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex < RoundFrames);)
            yield return null;
        ProfilerDriver.enabled = false;
        yield return null;
        for (int f = Mathf.Max(0, ProfilerDriver.firstFrameIndex); f < ProfilerDriver.lastFrameIndex - 3; f++)
            AnalyzeGpu(f); // GPU timings arrive a few frames late: skip the newest
        ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.GPU, gpuWas);
#pragma warning restore CS0618

        // 4. Where the garbage comes from: one more round with allocation call stacks (slower, so kept out of the
        // timings above), every GC.Alloc on the main thread totalled by its script call stack.
        ProfilerDriver.ClearAllFrames();
        UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
        ProfilerDriver.enabled = true;
        for (double timeout = Now + 10.0; Now < timeout && (ProfilerDriver.firstFrameIndex < 0 ||
                 ProfilerDriver.lastFrameIndex - ProfilerDriver.firstFrameIndex < RoundFrames / 2);)
            yield return null;
        ProfilerDriver.enabled = false;
        UnityEngine.Profiling.Profiler.enableAllocationCallstacks = false;
        yield return null;
        for (int f = Mathf.Max(0, ProfilerDriver.firstFrameIndex); f < ProfilerDriver.lastFrameIndex; f++)
            AllocationStacks(f);

        WriteReport();
        Debug.Log($"PlaySessionProfiler: report written to {Path.GetFullPath(ReportPath)}");
        s_job = null; // finished: leaving play mode now isn't an early stop
        EditorApplication.isPlaying = false;
    }

    // What the physics and the per-object loops scale with, to spot things piling up over a run.
    static string Counts()
    {
        int bodies = 0, awake = 0, colliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None).Length;
        foreach (Rigidbody b in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            bodies++;
            if (!b.isKinematic && !b.IsSleeping()) awake++;
        }
        return $"{WorldStreamer.Live.Count} streamed live, {Organism.All.Count} organisms, {bodies} rigidbodies ({awake} awake), " +
               $"{colliders} colliders, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB";
    }

    // ------------------------------------------------------------------ profiler frames

    static int s_renderThread = -2; // -2: not looked up yet, -1: none
    static readonly Dictionary<string, Marker> s_gpu = new Dictionary<string, Marker>();
    static readonly List<float> s_gpuFrames = new List<float>();

    [MenuItem("Tools/Analyze Profiler Capture")]
    static void AnalyzeCapture()
    {
        s_gcStacks.Clear(); s_gcFrames = 0;
        s_results.Clear(); s_main.Clear(); s_render.Clear(); s_frames.Clear(); s_notes.Clear(); s_ropes.Clear();
        s_gpu.Clear(); s_gpuFrames.Clear();
        s_renderThread = -2;
        int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
        if (first < 0 || last < first)
        {
            File.WriteAllText(CaptureReportPath, "No frames in the Profiler window.\n");
            return;
        }
        first = Math.Max(first, last - 2000); // the latest ~2000
        s_notes.AppendLine($"Profiler capture: frames {first}..{last}, connection '{ProfilerDriver.GetConnectionIdentifier(ProfilerDriver.connectedProfiler)}'.");
        for (int f = first; f <= last; f++)
        {
            AnalyzeFrame(f);
            AnalyzeGpu(f);
        }
        WriteReport(CaptureReportPath);
        Debug.Log($"PlaySessionProfiler: capture report written to {CaptureReportPath}.");
    }

    // GPU time per sample: what the Profiler window's GPU module shows (ProfilerProperty with onlyShowGPUSamples; the
    // GPU module on while recording). Column 9 = self GPU time (HierarchyFrameDataView.columnSelfGpuTime, internal).
    const int SelfGpuColumn = 9;

    static void AnalyzeGpu(int frame)
    {
        var prop = new ProfilerProperty();
        try
        {
            prop.SetRoot(frame, SelfGpuColumn, 0);
            prop.onlyShowGPUSamples = true;
            if (!prop.frameDataReady) return;
            float total = 0f;
            var seen = new HashSet<string>();
            var parents = new List<string> { "" };
            while (prop.Next(true))
            {
                int depth = prop.depth;
                string name = prop.propertyName;
                while (parents.Count > depth) parents.RemoveAt(parents.Count - 1);
                string parent = parents[parents.Count - 1];
                parents.Add(name);
                float self = prop.GetColumnAsSingle(SelfGpuColumn);
                if (self <= 0f) continue;
                total += self;
                Marker m = Get(s_gpu, name, parent);
                m.self += self;
                if (seen.Add(name)) m.frames++;
                m.maxSelf = Math.Max(m.maxSelf, self);
            }
            s_gpuFrames.Add(total);
        }
        finally { prop.Cleanup(); }
    }

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

    static readonly Dictionary<string, long> s_gcStacks = new Dictionary<string, long>();
    static int s_gcFrames;
    static readonly List<ulong> s_stack = new List<ulong>();

    static void AllocationStacks(int frame)
    {
        using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frame, 0))
        {
            if (raw == null || !raw.valid) return;
            s_gcFrames++;
            int gc = raw.GetMarkerId("GC.Alloc");
            var key = new StringBuilder();
            for (int i = 0; i < raw.sampleCount; i++)
            {
                if (raw.GetSampleMarkerId(i) != gc) continue;
                long bytes = raw.GetSampleMetadataAsLong(i, 0);
                raw.GetSampleCallstack(i, s_stack);
                key.Clear();
                int shown = 0;
                foreach (ulong address in s_stack)
                {
                    string method = raw.ResolveMethodInfo(address).methodName;
                    if (string.IsNullOrEmpty(method)) continue;
                    if (shown++ > 0) key.Append(" <- ");
                    key.Append(method);
                    if (shown == 5) break;
                }
                string k = shown == 0 ? "(no script frames)" : key.ToString();
                s_gcStacks.TryGetValue(k, out long total);
                s_gcStacks[k] = total + bytes;
            }
        }
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

    static void WriteReport(string path = ReportPath)
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
            sb.AppendLine().AppendLine("== Cost by elimination (each thing off on its own, vs the mean of the baselines right before and after it; negative delta = it was costing that much) ==");
            sb.AppendLine($"{"experiment",-44} {"fps",6} {"GPU ms",8} {"base",7} {"dGPU",7} {"main ms",8} {"base",7} {"dMain",7}");
            foreach (PerfBenchmark.Result r in s_results)
                sb.AppendLine($"{r.name,-44} {r.fps,6:0} {r.gpu,8:0.00} {r.baseGpu,7:0.00} {r.gpu - r.baseGpu,7:+0.00;-0.00} {r.main,8:0.00} {r.baseMain,7:0.00} {r.main - r.baseMain,7:+0.00;-0.00}");
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
            if (s_gcFrames > 0)
            {
                sb.AppendLine().AppendLine($"-- GC allocations by call stack ({s_gcFrames} frames with call stacks; bytes per frame) --");
                foreach (var p in s_gcStacks.OrderByDescending(p => p.Value).Take(15))
                    sb.AppendLine($"{p.Value / s_gcFrames,9}  {p.Key}");
            }
            if (s_render.Count > 0) AppendMarkers(sb, "Top render-thread self time (avg ms per frame)", s_render, n, 20);
            if (s_gpuFrames.Count > 0)
            {
                var gpu = s_gpuFrames.Where(g => g > 0f).OrderBy(g => g).ToList();
                sb.AppendLine().AppendLine(gpu.Count > 0
                    ? $"== GPU: frame median {gpu[gpu.Count / 2]:0.00} ms, p95 {gpu[(int)(0.95f * (gpu.Count - 1))]:0.00} ms ({gpu.Count} frames with GPU time) =="
                    : "== GPU: no GPU frame times (GPU module off while recording?) ==");
                if (s_gpu.Count > 0) AppendMarkers(sb, "Top GPU self time by sample (avg ms per frame)", s_gpu, s_gpuFrames.Count, 40);
            }

            sb.AppendLine().AppendLine($"== Slowest frames (median is {Pct(0.5f):0.0} ms) ==");
            foreach (var f in s_frames.OrderByDescending(f => f.ms).Take(10))
            {
                sb.AppendLine($"{f.ms:0.0} ms (PlayerLoop {f.player:0.0}, EditorLoop {f.editor:0.0}):");
                foreach (var p in f.top) sb.AppendLine($"    {p.Value,6:0.00}  {p.Key}");
            }
        }

        File.WriteAllText(path, sb.ToString());
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

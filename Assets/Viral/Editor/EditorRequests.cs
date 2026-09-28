using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Small jobs another tool can ask the open editor for by dropping a file in Temp/ (see PlaySessionProfiler for the
/// play-session one):
///   Temp/ClaudeQuality.request  (content: a quality tier name) switches the editor's active tier and saves it.
///   Temp/ClaudeMemory.request   writes Temp/ClaudeMemory.txt: the managed heap before and after a full collect, and
///                               an estimate of the live managed bytes each Assembly-CSharp field holds (instance
///                               fields of every loaded script object, and statics), largest first. Content "play":
///                               census in edit mode, enter play mode, census again after PlayWarmup s, leave.
/// </summary>
[InitializeOnLoad]
static class EditorRequests
{
    const string QualityRequest = "Temp/ClaudeQuality.request";
    const string MemoryRequest = "Temp/ClaudeMemory.request", MemoryReport = "Temp/ClaudeMemory.txt";

    const string PhaseKey = "EditorRequests.memory";
    const double PlayWarmup = 40.0;

    static EditorRequests()
    {
        EditorApplication.update += Update;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(PhaseKey, "") == "enter")
                SessionState.SetString(PhaseKey, (EditorApplication.timeSinceStartup + PlayWarmup).ToString("R"));
        };
    }

    static void Update()
    {
        if (EditorApplication.isCompiling) return;
        if (File.Exists(QualityRequest))
        {
            string name = File.ReadAllText(QualityRequest).Trim();
            File.Delete(QualityRequest);
            int i = Array.IndexOf(QualitySettings.names, name);
            if (i < 0) Debug.LogWarning($"EditorRequests: no quality tier '{name}' ({string.Join(", ", QualitySettings.names)})");
            else
            {
                QualitySettings.SetQualityLevel(i, true);
                AssetDatabase.SaveAssets();
                Debug.Log($"EditorRequests: quality tier set to {name}");
            }
        }
        if (File.Exists(MemoryRequest) && !EditorApplication.isPlaying)
        {
            bool play = File.ReadAllText(MemoryRequest).Contains("play");
            File.Delete(MemoryRequest);
            string report = MemoryCensus();
            if (!play) { File.WriteAllText(MemoryReport, report); return; }
            SessionState.SetString(PhaseKey + ".edit", report); // survives the domain reload on entering play mode
            SessionState.SetString(PhaseKey, "enter");
            EditorApplication.isPlaying = true;
        }
        string phase = SessionState.GetString(PhaseKey, "");
        if (EditorApplication.isPlaying && double.TryParse(phase, out double due) && EditorApplication.timeSinceStartup >= due)
        {
            SessionState.EraseString(PhaseKey);
            File.WriteAllText(MemoryReport, MemoryCensus() + "\n\n==== Before play (edit mode) ====\n" +
                                            SessionState.GetString(PhaseKey + ".edit", ""));
            SessionState.EraseString(PhaseKey + ".edit");
            EditorApplication.isPlaying = false;
        }
    }

    [MenuItem("Tools/Managed Memory Census")]
    static void CensusMenu()
    {
        File.WriteAllText(MemoryReport, MemoryCensus());
        Debug.Log("Managed memory census written to " + MemoryReport);
    }

    // ---------------------------------------------------------------- memory census

    const int ObjectHeader = 16, RefSize = 8, ArrayHeader = 32;

    static readonly HashSet<object> s_seen = new HashSet<object>(RefEq.Instance);
    static readonly Dictionary<Type, FieldInfo[]> s_fields = new Dictionary<Type, FieldInfo[]>();
    static readonly Dictionary<Type, int> s_sizes = new Dictionary<Type, int>();
    static readonly Dictionary<Type, bool> s_hasRefs = new Dictionary<Type, bool>();

    static string MemoryCensus()
    {
        var sb = new StringBuilder();
        long before = GC.GetTotalMemory(false);
        long after = GC.GetTotalMemory(true);
        sb.AppendLine($"Managed memory census, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {(EditorApplication.isPlaying ? "play mode" : "edit mode")}");
        sb.AppendLine($"GC heap in use: {Mb(before)} before a full collect, {Mb(after)} after (live objects: editor + game)");
        sb.AppendLine($"Reserved (Profiler 'GC Reserved'): {Mb(UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong())}");
        sb.AppendLine();

        s_seen.Clear();
        var game = typeof(Organism).Assembly;
        var byField = new Dictionary<string, (long bytes, int owners)>();
        var byType = new Dictionary<Type, (long bytes, int count)>();

        // Statics first, so shared tables are charged to their static field and not to whichever instance reached them.
        foreach (var t in game.GetTypes())
        {
            if (t.ContainsGenericParameters) continue;
            foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsLiteral) continue;
                object v;
                try { v = f.GetValue(null); } catch { continue; }
                long b = Size(v, f.FieldType, 0);
                if (b > 0) Add(byField, "static " + Name(t) + "." + f.Name, b);
            }
        }

        foreach (var o in Resources.FindObjectsOfTypeAll<ScriptableObject>().Cast<Object>()
                     .Concat(Resources.FindObjectsOfTypeAll<MonoBehaviour>()))
        {
            var t = o.GetType();
            if (t.Assembly != game) continue;
            long total = 0;
            foreach (var f in InstanceFields(t))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                long b = Size(v, f.FieldType, 0);
                if (b <= 0) continue;
                total += b;
                Add(byField, Name(t) + "." + f.Name, b);
            }
            byType.TryGetValue(t, out var e);
            byType[t] = (e.bytes + total, e.count + 1);
        }

        long sum = byField.Values.Sum(v => v.bytes);
        sb.AppendLine($"Reachable from Assembly-CSharp fields: {Mb(sum)} (estimate: headers + payloads, each object counted once)");
        sb.AppendLine();
        sb.AppendLine("-- By script type (instances) --");
        foreach (var kv in byType.OrderByDescending(k => k.Value.bytes).Take(30))
            sb.AppendLine($"{Mb(kv.Value.bytes),12}  x{kv.Value.count,-6} {Name(kv.Key)}");
        sb.AppendLine();
        sb.AppendLine("-- By field (summed over instances) --");
        foreach (var kv in byField.OrderByDescending(k => k.Value.bytes).Take(60))
            sb.AppendLine($"{Mb(kv.Value.bytes),12}  x{kv.Value.owners,-6} {kv.Key}");
        s_seen.Clear();
        return sb.ToString();
    }

    static void Add(Dictionary<string, (long bytes, int owners)> d, string key, long b)
    {
        d.TryGetValue(key, out var e);
        d[key] = (e.bytes + b, e.owners + 1);
    }

    // Managed bytes reachable from v that nothing counted before. Unity objects are counted as a reference only (they
    // are walked as roots of their own); delegates as their object only.
    static long Size(object v, Type declared, int depth)
    {
        if (v == null) return 0;
        var t = v.GetType();
        if (t.IsValueType) return StructRefs(v, t, depth); // inline struct: its bytes are in the container, walk its references
        if (v is Object || v is Delegate || v is Type || v is MemberInfo) return 0;
        if (!s_seen.Add(v)) return 0;
        if (depth > 64) return ObjectHeader;

        if (v is string s) return 20 + 2L * s.Length;
        if (v is Array a)
        {
            var et = t.GetElementType();
            long bytes = ArrayHeader + (long)a.Length * ElementSize(et);
            if (HasRefs(et))
                foreach (var e in a) bytes += et.IsValueType ? StructRefs(e, et, depth + 1) : Size(e, et, depth + 1);
            return bytes;
        }

        long total = ObjectHeader;
        foreach (var f in InstanceFields(t))
        {
            total += ElementSize(f.FieldType);
            if (!HasRefs(f.FieldType)) continue;
            object fv;
            try { fv = f.GetValue(v); } catch { continue; }
            total += f.FieldType.IsValueType ? StructRefs(fv, f.FieldType, depth + 1) : Size(fv, f.FieldType, depth + 1);
        }
        return total;
    }

    // What a struct's reference fields reach (its own bytes are already in the container).
    static long StructRefs(object v, Type t, int depth)
    {
        if (v == null || !HasRefs(t) || depth > 64) return 0;
        long total = 0;
        foreach (var f in InstanceFields(t))
        {
            if (!HasRefs(f.FieldType)) continue;
            object fv;
            try { fv = f.GetValue(v); } catch { continue; }
            total += f.FieldType.IsValueType ? StructRefs(fv, f.FieldType, depth + 1) : Size(fv, f.FieldType, depth + 1);
        }
        return total;
    }

    static int ElementSize(Type t)
    {
        if (!t.IsValueType) return RefSize;
        if (s_sizes.TryGetValue(t, out int n)) return n;
        try { n = UnsafeUtility.SizeOf(t); }
        catch { n = InstanceFields(t).Sum(f => ElementSize(f.FieldType)); }
        return s_sizes[t] = Mathf.Max(1, n);
    }

    static bool HasRefs(Type t)
    {
        if (!t.IsValueType) return true;
        if (t.IsPrimitive || t.IsEnum) return false;
        if (s_hasRefs.TryGetValue(t, out bool r)) return r;
        s_hasRefs[t] = false; // guards recursion
        r = InstanceFields(t).Any(f => HasRefs(f.FieldType));
        return s_hasRefs[t] = r;
    }

    static FieldInfo[] InstanceFields(Type t)
    {
        if (s_fields.TryGetValue(t, out var fs)) return fs;
        var list = new List<FieldInfo>();
        for (var c = t; c != null && c != typeof(MonoBehaviour) && c != typeof(ScriptableObject) && c != typeof(object); c = c.BaseType)
            list.AddRange(c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return s_fields[t] = list.ToArray();
    }

    static string Name(Type t) => t.FullName ?? t.Name;
    static string Mb(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0.0} KB";

    sealed class RefEq : IEqualityComparer<object>
    {
        public static readonly RefEq Instance = new RefEq();
        public new bool Equals(object a, object b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
    }
}

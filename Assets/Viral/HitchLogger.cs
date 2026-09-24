using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

/// <summary>
/// Finds out what a one-off hitch was doing without the Profiler window: records every timing
/// marker Unity has (ProfilerRecorder, all threads) and, for each frame much slower than the
/// recent median, keeps the markers that took real time in it, with what the player was doing.
/// Markers that first appear mid-play (a first-time shader compile, say) are picked up by a
/// rescan every couple of seconds. Times include their children, so a parent shows alongside
/// the child that made it slow. Diagnostic only: recording thousands of markers costs a little.
/// Added by HitchCatcher (Tools > Catch Hitches), which writes the report when play mode ends.
/// </summary>
public class HitchLogger : MonoBehaviour
{
    public float minHitchMs = 30f, hitchFactor = 2.5f;
    public int maxHitches = 40;

    readonly List<ProfilerRecorder> _recorders = new List<ProfilerRecorder>();
    readonly List<string> _names = new List<string>();
    readonly List<long> _last = new List<long>(); // LastValue keeps a marker's last reading after it stops running
    readonly HashSet<string> _known = new HashSet<string>();
    readonly List<ProfilerRecorderHandle> _handles = new List<ProfilerRecorderHandle>();
    readonly List<float> _recent = new List<float>();
    readonly List<(float ms, float at, string text)> _hitches = new List<(float, float, string)>();
    readonly List<int> _fresh = new List<int>();
    float _nextScan;
    int _skip, _frames;
    VirusMovement _player;

    bool _profilerWas;
    long _freshTotal;

    void OnEnable()
    {
        // In the Editor, markers only report timings while the runtime profiler is on.
        _profilerWas = UnityEngine.Profiling.Profiler.enabled;
        UnityEngine.Profiling.Profiler.enabled = true;
        Scan();
    }

    void OnDisable()
    {
        UnityEngine.Profiling.Profiler.enabled = _profilerWas;
        foreach (ProfilerRecorder r in _recorders) r.Dispose();
        _recorders.Clear();
        _names.Clear();
        _last.Clear();
        _known.Clear();
    }

    // Start a recorder for every time marker not seen yet.
    void Scan()
    {
        _handles.Clear();
        ProfilerRecorderHandle.GetAvailable(_handles);
        foreach (ProfilerRecorderHandle h in _handles)
        {
            ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(h);
            if (d.UnitType != ProfilerMarkerDataUnit.TimeNanoseconds) continue;
            string name = d.Category.Name + "/" + d.Name;
            if (!_known.Add(name)) continue;
            var r = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.SumAllSamplesInFrame);
            r.Start();
            _recorders.Add(r);
            _names.Add(d.Name + "  [" + d.Category.Name + "]");
            _last.Add(0);
        }
        _skip = 2; // the scan itself makes a slow frame
    }

    void Update()
    {
        float ms = Time.unscaledDeltaTime * 1000f; // the frame that just finished, like the recorders' LastValue
        _frames++;

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            Scan();
        }

        bool hitch = false;
        float median = Median();
        if (_skip > 0) _skip--;
        else hitch = _recent.Count >= 30 && ms > Mathf.Max(minHitchMs, median * hitchFactor);

        // Only readings that changed since last frame are from the frame that just finished.
        _fresh.Clear();
        for (int i = 0; i < _recorders.Count; i++)
        {
            long v = _recorders[i].LastValue;
            if (v != _last[i]) { _last[i] = v; _fresh.Add(i); }
        }
        _freshTotal += _fresh.Count;
        if (hitch) Record(ms, median);
        _recent.Add(ms);
        if (_recent.Count > 240) _recent.RemoveAt(0);
    }

    float Median()
    {
        if (_recent.Count == 0) return 0f;
        var sorted = new List<float>(_recent);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    void Record(float ms, float median)
    {
        var top = new List<(double ms, string name)>();
        foreach (int i in _fresh)
        {
            double t = _last[i] * 1e-6;
            if (t >= 0.5) top.Add((t, _names[i]));
        }

        if (!_player) _player = FindAnyObjectByType<VirusMovement>();
        string state = !_player ? "no player" : _player.IsFocusMode ? "focus mode" : _player.IsGrounded ? "on a surface" : "flying";

        var sb = new StringBuilder();
        sb.AppendLine($"{ms:0.0} ms at {Time.unscaledTime:0.0} s (median {median:0.0} ms), player {state}:");
        foreach (var p in top.OrderByDescending(p => p.ms).Take(30))
            sb.AppendLine($"    {p.ms,7:0.00} ms  {p.name}");
        _hitches.Add((ms, Time.unscaledTime, sb.ToString()));

        if (_hitches.Count > maxHitches * 2)
        {
            var kept = _hitches.OrderByDescending(h => h.ms).Take(maxHitches).OrderBy(h => h.at).ToList();
            _hitches.Clear();
            _hitches.AddRange(kept);
        }
    }

    // So it's obvious it's running (and how much it has).
    void OnGUI()
    {
        GUI.Label(new Rect(10, Screen.height - 24, 600, 22),
                  $"HitchLogger recording: {_frames} frames, {_hitches.Count} hitches. Leave play mode for the report.");
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Hitches over {_frames} frames, {_recorders.Count} markers recorded. " +
                      $"A hitch is a frame over {minHitchMs} ms and {hitchFactor}x the recent median.");
        sb.AppendLine($"Markers with a new reading per frame: {_freshTotal / (double)Mathf.Max(1, _frames):0.0} on average (near 0 = readings not updating).");
        sb.AppendLine($"Found {_hitches.Count}. Times include children (a parent is listed with the child that made it slow).");
        sb.AppendLine();
        foreach (var h in _hitches) sb.AppendLine(h.text);
        return sb.ToString();
    }
}

using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Catches one-off hitches while you play normally. Tools > Catch Hitches (or creating
/// Temp/ClaudeHitches.request) enters play mode with a HitchLogger running: every frame much
/// slower than the recent median gets a list of the markers that took its time. Leave play mode
/// when done; the report goes to Temp/ClaudeHitches.txt. (Uses ProfilerRecorder rather than the
/// Profiler window, which here wouldn't hand over frames recorded in play mode.)
/// </summary>
[InitializeOnLoad]
static class HitchCatcher
{
    const string RequestPath = "Temp/ClaudeHitches.request", ReportPath = "Temp/ClaudeHitches.txt";
    const string PhaseKey = "HitchCatcher.phase";

    static HitchLogger s_logger;

    static HitchCatcher()
    {
        EditorApplication.update += () =>
        {
            if (!s_logger && File.Exists(RequestPath) && !EditorApplication.isCompiling)
            {
                File.Delete(RequestPath);
                Request();
            }
        };
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetString(PhaseKey, "") == "enter") Begin();
            if (state == PlayModeStateChange.ExitingPlayMode && s_logger) Finish();
        };
    }

    [MenuItem("Tools/Catch Hitches")]
    static void Request()
    {
        if (s_logger) return;
        if (EditorApplication.isPlaying) { Begin(); return; }
        SessionState.SetString(PhaseKey, "enter"); // survives the domain reload on entering play mode
        EditorApplication.isPlaying = true;
    }

    static void Begin()
    {
        SessionState.EraseString(PhaseKey);
        var go = new GameObject("Hitch Logger") { hideFlags = HideFlags.DontSave };
        Object.DontDestroyOnLoad(go);
        s_logger = go.AddComponent<HitchLogger>();
        Debug.Log("HitchCatcher: recording. Do the things that hitch, then leave play mode for the report.");
    }

    static void Finish()
    {
        File.WriteAllText(ReportPath, s_logger.Report());
        s_logger = null;
        Debug.Log($"HitchCatcher: report written to {Path.GetFullPath(ReportPath)}");
    }
}

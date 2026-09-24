using UnityEngine;

/// <summary>
/// World one-shots for any number of emitters from a fixed pool of 3D voices. A call is dropped
/// cheaply (one distance check against SimulationTicker.CameraPosition) when out of earshot, when
/// the frame's start budget is spent, or when every voice is busy with something louder; otherwise
/// the quietest nearly-finished voice is reused. So thousands of creatures stepping cost a
/// distance check each, and at most <see cref="Voices"/> sounds ever play.
/// </summary>
public static class Sfx
{
    public const int Voices = 24;
    public const int MaxStartsPerFrame = 8;

    static AudioSource[] s_voices;
    static float[] s_loudness, s_ends;
    static GameObject s_root;
    static int s_frame = -1, s_starts;

    /// <summary>Plays 'clip' at 'position', heard out to 'range' metres (linear falloff).</summary>
    public static void Play(AudioClip clip, Vector3 position, float volume, float pitch = 1f, float range = 30f)
    {
        if (!clip || volume <= 0f) return;
        float distance = Vector3.Distance(position, SimulationTicker.CameraPosition);
        if (distance >= range) return;
        if (Time.frameCount != s_frame) { s_frame = Time.frameCount; s_starts = 0; }
        if (s_starts >= MaxStartsPerFrame) return;
        Ready();

        float loudness = volume * (1f - distance / range), now = Time.unscaledTime;
        int pick = -1;
        float quietest = float.MaxValue;
        for (int i = 0; i < Voices; i++)
        {
            float left = s_ends[i] - now;
            if (left <= 0f || !s_voices[i].isPlaying) { pick = i; quietest = -1f; break; }
            float still = s_loudness[i] * Mathf.Clamp01(left / 0.25f); // a tail is nearly gone
            if (still < quietest) { quietest = still; pick = i; }
        }
        if (loudness <= quietest) return;

        AudioSource s = s_voices[pick];
        s.transform.position = position;
        s.clip = clip;
        s.volume = volume;
        s.pitch = pitch;
        s.maxDistance = range;
        s.Play();
        s_loudness[pick] = loudness;
        s_ends[pick] = now + clip.length / Mathf.Max(0.05f, pitch);
        s_starts++;
    }

    static void Ready()
    {
        if (s_root) return;
        s_root = new GameObject("Sfx Voices");
        s_voices = new AudioSource[Voices];
        s_loudness = new float[Voices];
        s_ends = new float[Voices];
        for (int i = 0; i < Voices; i++)
        {
            var go = new GameObject("Voice " + i);
            go.transform.SetParent(s_root.transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1f;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.minDistance = 1f;
            s.dopplerLevel = 0f;
            s_voices[i] = s;
        }
    }
}

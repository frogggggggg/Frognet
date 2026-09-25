using UnityEngine;

/// <summary>
/// The hold prompt's ticking (WorldButton, F -> focus): a dense, quiet ratchet of tiny clicks while the key is
/// held (~22/s rising to ~45/s, a touch higher as the ring fills: the "charging up" clicks of hold prompts, not
/// a clock), stopping the moment it's let go or completes (the focus sound takes over from there). Dry, short,
/// a little uneven so it doesn't buzz.
/// Ticks are scheduled on the audio clock (PlayScheduled) a little ahead, so the rhythm stays even whatever
/// the frame rate. Creates itself on play. Cost: a small pool of 2D sources, one scan of WorldButton.All a frame.
/// </summary>
public class HoldTickAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.1f;
    [Tooltip("Seconds between clicks at the start of the hold and at the end.")]
    public Vector2 interval = new Vector2(0.045f, 0.022f);
    [Tooltip("Pitch at the start of the hold and at the end.")]
    public Vector2 pitch = new Vector2(1f, 1.25f);
    [Range(0f, 1f), Tooltip("Level at the start, against the end.")]
    public float startLevel = 0.6f;

    const int Voices = 10;
    const double Ahead = 0.12; // schedule ticks this far ahead of the audio clock

    static AudioClip s_tick, s_tock;
    readonly AudioSource[] _voices = new AudioSource[Voices];
    readonly double[] _at = new double[Voices];
    int _voice, _count;
    double _next = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<HoldTickAudio>()) return;
        new GameObject("Hold Tick Audio").AddComponent<HoldTickAudio>();
    }

    void Ready()
    {
        if (!s_tick) s_tick = Synth.Clip("Hold Tick", BuildTick(3200f, 11));
        if (!s_tock) s_tock = Synth.Clip("Hold Tock", BuildTick(2700f, 23));
        for (int i = 0; i < Voices; i++)
        {
            if (_voices[i]) continue;
            var s = gameObject.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 0f;
            _voices[i] = s;
        }
    }

    void Update()
    {
        WorldButton held = null;
        foreach (WorldButton b in WorldButton.All)
            if (b.IsHolding && b.IsVisible && !b.IsCompleted) { held = b; break; }

        double now = AudioSettings.dspTime;
        if (!held)
        {
            if (_next < 0) return;
            _next = -1;
            for (int i = 0; i < Voices; i++) // cancel ticks scheduled past the let-go
                if (_voices[i] && _at[i] > now) _voices[i].Stop();
            return;
        }

        Ready();
        if (_next < 0) { _next = now + 0.01; _count = 0; }
        float hold = Mathf.Max(held.holdSeconds, 0.01f), progress = held.HoldProgress;
        while (_next < now + Ahead)
        {
            // Progress where this tick lands, so the speed-up follows the ring and not the frame.
            float p = Mathf.Clamp01(progress + (float)(_next - now) / hold);
            if (p >= 1f) { _next = double.MaxValue; break; } // the hold completes first: no tick on top of it
            float e = Mathf.Lerp(p, p * p, 0.5f);
            AudioSource v = _voices[_voice];
            _at[_voice] = _next;
            _voice = (_voice + 1) % Voices;
            v.clip = _count % 2 == 0 ? s_tick : s_tock;
            v.pitch = Mathf.Lerp(pitch.x, pitch.y, e) * (1f + Random.Range(-0.04f, 0.04f));
            v.volume = volume * Mathf.Lerp(startLevel, 1f, p) * Random.Range(0.75f, 1f);
            v.PlayScheduled(_next);
            _count++;
            _next += Mathf.Lerp(interval.x, interval.y, e) * Random.Range(0.85f, 1.15f); // uneven: a ratchet, not a buzz
        }
    }

    // One tiny dry click: a very short band-passed grain and a faint body, no ring, no tail.
    static float[] BuildTick(float clickHz, uint seed)
    {
        float[] buf = Synth.Stereo(0.05f);
        Synth.Grain(buf, 0f, clickHz, 1.6f, 0.0012f, 0.8f, 0f, seed);
        Synth.Grain(buf, 0f, 900f, 1.2f, 0.002f, 0.3f, 0f, seed + 5);
        Synth.Lowpass(buf, 6000f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }
}

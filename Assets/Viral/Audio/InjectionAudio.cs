using UnityEngine;

/// <summary>
/// Sounds for the head view's injection: the strand being *pulled through*, not a reward. Nearly all of it is
/// two loops that follow <see cref="GenomeView.InjectSpeed"/>, so the sound moves exactly when and as fast as the
/// strand does (its legs are expo in-out: still, a rush, still), and stops when it stops:
/// - Rub: dry stick-slip friction of the strand sliding through the head, broad noise with a scratchy grain.
/// - Rungs: soft muffled ticks, the base pairs running past (two timbres, like the A-T / G-C rungs); the
///   loop's pitch rises with speed, so the ticks come faster as it speeds up and ratchet down the drill.
/// Past the tube's mouth (<see cref="GenomeView.InjectStage.IntoTube"/>) it's squeezed: the rub tighter and
/// brighter, the ticks closer. The pump's pull-back and the shot are movement too, so they come out of the
/// same loops. One-shots only where something mechanical happens: a dry unclip as the strand comes off its
/// mount and a muffled stop at the tip. The slam itself is the body's normal landing thump (Pump plays
/// CreatureAudio.Impact), so it isn't doubled here. No notes, no chimes.
/// Creates itself on play. Clips are built once at load. Cost: three 2D sources, nothing per creature.
/// </summary>
public class InjectionAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float rubVolume = 0.3f;
    [Range(0f, 1f)] public float rungVolume = 0.22f;
    [Range(0f, 1f)] public float unclipVolume = 0.3f;
    [Range(0f, 1f)] public float stopVolume = 0.4f;
    [Min(0.1f), Tooltip("Strand speed (sphere radii per second) that gives about 2/3 of the loops' level.")]
    public float referenceSpeed = 6f;
    [Tooltip("Rung loop pitch (= tick rate, 24/s at 1) at rest and at the top of its range.")]
    public Vector2 rungPitch = new Vector2(0.5f, 2.2f);

    const float RungRate = 24f; // ticks per second in the loop at pitch 1

    static AudioClip s_unclip, s_stop, s_rub, s_rungs;
    AudioSource _hits, _rub, _rungs;
    AudioLowPassFilter _rubFilter;
    float _tight;
    GenomeView.InjectStage _stage;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<InjectionAudio>()) return;
        new GameObject("Injection Audio").AddComponent<InjectionAudio>();
    }

    void OnEnable() => GenomeView.Injection += OnStage;
    void OnDisable() => GenomeView.Injection -= OnStage;

    void Ready()
    {
        if (!s_unclip) s_unclip = Synth.Clip("Inject Unclip", BuildUnclip());
        if (!s_stop) s_stop = Synth.Clip("Inject Stop", BuildStop());
        if (!s_rub) s_rub = Synth.Clip("Inject Rub", BuildRub());
        if (!s_rungs) s_rungs = Synth.Clip("Inject Rungs", BuildRungs());
        if (!_hits) _hits = Source(gameObject, null, false);
        if (!_rungs) _rungs = Source(new GameObject("Inject Rungs"), s_rungs, true);
        if (!_rub) // own object, so its low-pass leaves the others alone
        {
            var go = new GameObject("Inject Rub");
            _rub = Source(go, s_rub, true);
            _rubFilter = go.AddComponent<AudioLowPassFilter>();
        }
    }

    AudioSource Source(GameObject go, AudioClip clip, bool loop)
    {
        if (go != gameObject) go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f; // the player's own action, on the head view
        s.clip = clip;
        s.loop = loop;
        s.volume = 0f;
        return s;
    }

    void OnStage(GenomeView.InjectStage stage, bool took)
    {
        Ready();
        _stage = stage;
        switch (stage)
        {
            case GenomeView.InjectStage.Started: _hits.PlayOneShot(s_unclip, unclipVolume); break;
            case GenomeView.InjectStage.Delivered: _hits.PlayOneShot(s_stop, stopVolume); break;
        }
    }

    void Update()
    {
        float speed = GenomeView.InjectSpeed;
        if (speed <= 0f && (!_rub || !_rub.isPlaying)) return;
        Ready();
        float dt = Time.unscaledDeltaTime;
        _tight = Mathf.MoveTowards(_tight, _stage >= GenomeView.InjectStage.IntoTube ? 1f : 0f, dt * 4f);

        float s = 1f - Mathf.Exp(-speed / referenceSpeed);
        // Quick both ways: the sound has to start and stop with the strand.
        Follow(_rub, s * rubVolume, dt);
        Follow(_rungs, s * rungVolume * Mathf.Lerp(0.8f, 1f, _tight), dt);
        _rub.pitch = Mathf.Lerp(0.8f, 1.25f, s) * Mathf.Lerp(1f, 1.12f, _tight);
        _rubFilter.cutoffFrequency = Mathf.Lerp(700f, 2600f, s) * Mathf.Lerp(1f, 1.3f, _tight);
        // The tick rate tracks the speed itself (not the eased level), so the rungs visibly and audibly agree.
        _rungs.pitch = Mathf.Clamp(rungPitch.x + speed / referenceSpeed * 0.35f * Mathf.Lerp(1f, 1.25f, _tight), rungPitch.x, rungPitch.y);
    }

    static void Follow(AudioSource a, float target, float dt)
    {
        a.volume = Mathf.MoveTowards(a.volume, target, dt * (target > a.volume ? 8f : 5f));
        if (a.volume > 0f) { if (!a.isPlaying) a.Play(); }
        else if (a.isPlaying) a.Stop();
    }

    // ---------------- clips ----------------

    // The strand coming off its mount: two dry little clicks (the catch, then the release) and a short scrape.
    static float[] BuildUnclip()
    {
        float[] buf = Synth.Stereo(0.5f);
        Synth.Grain(buf, 0f, 1800f, 1.5f, 0.004f, 0.8f, -0.1f, 11);
        Synth.Grain(buf, 0f, 420f, 1.2f, 0.01f, 0.5f, 0f, 13);
        Synth.Grain(buf, 0.045f, 1400f, 1.5f, 0.005f, 0.6f, 0.1f, 17);
        Synth.Swoosh(buf, 0.03f, 0.12f, 900f, 1400f, 0.15f, 0.8f, 19);
        Synth.Lowpass(buf, 3500f);
        Synth.Reverb(buf, 0.15f, 0.6f, 0.6f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Reaching the tip: the strand's front meeting the end of the drill, a short muffled stop.
    static float[] BuildStop()
    {
        float[] buf = Synth.Stereo(0.6f);
        Synth.Glide(buf, 0f, 0.18f, 85f, 60f, 0.004f, 26f, 0.8f, 0.05f);
        Synth.Grain(buf, 0f, 380f, 0.9f, 0.018f, 0.7f, 0f, 31);
        Synth.Lowpass(buf, 1200f);
        Synth.Reverb(buf, 0.2f, 0.65f, 0.6f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Friction: broad mid noise (a little of the low end) whose level is chopped by fast random stick-slip,
    // so it reads as something dragging through a channel rather than air or water.
    static float[] BuildRub()
    {
        const float Length = 2f, Fade = 0.2f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(401 + ch * 43));
            Synth.Biquad body = default, low = default, slip = default, drift = default;
            body.Bandpass(900f, 0.6f);
            low.Lowpass(260f, 0.7f);
            slip.Lowpass(45f, 0.7f);
            drift.Lowpass(3f, 0.7f);
            for (int i = 0; i < frames; i++)
            {
                float n = rng.Next();
                float grain = Mathf.Clamp01(0.45f + 9f * slip.Process(rng.Next())); // stick-slip
                float swell = 0.75f + 0.25f * Mathf.Clamp(8f * drift.Process(rng.Next()), -1f, 1f);
                buf[i * 2 + ch] = (body.Process(n) * (0.3f + 0.7f * grain * grain) + low.Process(n) * 0.5f) * swell;
            }
        }
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.9f, false);
        return loop;
    }

    // Rung ticks at RungRate, a little uneven, alternating two soft timbres (two base-pair kinds), muffled.
    static float[] BuildRungs()
    {
        const float Length = 2f;
        float[] buf = Synth.Stereo(Length + 0.05f);
        var rng = new Synth.Noise(733);
        int n = Mathf.RoundToInt(Length * RungRate);
        for (int j = 0; j < n; j++)
        {
            float at = (j + rng.Range(-0.12f, 0.12f)) / RungRate;
            if (at < 0f) at += Length;
            bool pair = (rng.Next() > 0f);
            float gain = rng.Range(0.5f, 1f) * (j % 5 == 0 ? 1.2f : 1f); // a turn of the helix every five
            Synth.Grain(buf, at, pair ? 1100f : 1600f, 1.3f, rng.Range(0.003f, 0.005f), gain, rng.Range(-0.3f, 0.3f), (uint)(j * 7 + 1));
            Synth.Grain(buf, at, pair ? 300f : 380f, 1f, 0.006f, gain * 0.35f, 0f, (uint)(j * 7 + 3));
        }
        // Wrap what rang past the end onto the start, so the loop is seamless.
        int frames = Synth.Samples(Length);
        var loop = new float[frames * 2];
        for (int i = 0; i < buf.Length / 2; i++)
        {
            int k = (i % frames) * 2;
            loop[k] += buf[i * 2];
            loop[k + 1] += buf[i * 2 + 1];
        }
        Synth.Lowpass(loop, 3000f);
        Synth.Normalize(loop, 0.8f, false);
        return loop;
    }
}

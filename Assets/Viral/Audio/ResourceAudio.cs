using UnityEngine;

/// <summary>
/// Sounds for resource extraction (ResourceField's events, so the field has no audio code):
/// - Start: a soft suction "plup" gliding up, a bubble, as the virus latches on.
/// - Draining: a quiet wet loop (gentle gulps of low, muffled noise, sparse low bubbles) at the
///   nearest chunk being extracted, a little faster and brighter as it nears empty.
/// - Poof: a breathy underwater puff with bubbles scattering upward and a faint glassy E6 + B6
///   (lydian-ish colour, very quiet) in a big soft reverb: a small musical release, not a bang.
/// Creates itself on play. Clips are built once at load.
/// </summary>
public class ResourceAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float startVolume = 0.5f;
    [Range(0f, 1f)] public float drainVolume = 0.22f;
    [Range(0f, 1f)] public float poofVolume = 0.6f;
    [Min(1f)] public float range = 60f;

    static AudioClip s_start, s_drain, s_poof;
    AudioSource _loop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<ResourceAudio>()) return;
        new GameObject("Resource Audio").AddComponent<ResourceAudio>();
    }

    void OnEnable()
    {
        ResourceField.Started += OnStarted;
        ResourceField.Drained += OnDrained;
    }

    void OnDisable()
    {
        ResourceField.Started -= OnStarted;
        ResourceField.Drained -= OnDrained;
    }

    void Ready()
    {
        if (!s_start) s_start = Synth.Clip("Resource Start", BuildStart(), true);
        if (!s_drain) s_drain = Synth.Clip("Resource Drain", BuildDrain(), true);
        if (!s_poof) s_poof = Synth.Clip("Resource Poof", BuildPoof(), true);
        if (_loop) return;
        _loop = gameObject.AddComponent<AudioSource>();
        _loop.clip = s_drain;
        _loop.loop = true;
        _loop.playOnAwake = false;
        _loop.spatialBlend = 1f;
        _loop.rolloffMode = AudioRolloffMode.Linear;
        _loop.maxDistance = range;
        _loop.dopplerLevel = 0f;
        _loop.volume = 0f;
    }

    void OnStarted(ResourceChunk c, VirusInventory into)
    {
        Ready();
        Sfx.Play(s_start, c.Centre, startVolume, Random.Range(0.92f, 1.08f), range);
    }

    void OnDrained(ResourceChunk c, VirusInventory into)
    {
        Ready();
        // Bigger chunks poof a little lower.
        Sfx.Play(s_poof, c.Centre, poofVolume, Mathf.Clamp(1.2f - c.radius * 0.08f, 0.75f, 1.15f), range);
    }

    void Update()
    {
        ResourceField field = ResourceField.Any ? ResourceField.Instance : null;
        ResourceChunk nearest = null;
        float best = float.MaxValue;
        Vector3 cam = SimulationTicker.CameraPosition;
        if (field)
            foreach (ResourceChunk c in field.Extracting)
            {
                if (!c || c.Blocked) continue;
                float d = (c.Centre - cam).sqrMagnitude;
                if (d < best) { best = d; nearest = c; }
            }
        if (!nearest && (!_loop || !_loop.isPlaying)) return;
        Ready();

        float target = nearest ? drainVolume : 0f;
        _loop.volume = Mathf.MoveTowards(_loop.volume, target, Time.deltaTime * (nearest ? 1.5f : 0.8f));
        if (nearest)
        {
            _loop.transform.position = nearest.Centre;
            _loop.pitch = Mathf.Lerp(0.9f, 1.15f, nearest.Extracted);
            if (!_loop.isPlaying) _loop.Play();
        }
        else if (_loop.volume <= 0f) _loop.Stop();
    }

    // ---------------- clips ----------------

    static float[] BuildStart()
    {
        float[] buf = Synth.Stereo(0.7f);
        double phase = 0;
        int count = Synth.Samples(0.16f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate, u = t / 0.16f;
            phase += 2.0 * System.Math.PI * Mathf.Lerp(170f, 430f, Mathf.SmoothStep(0f, 1f, u)) / Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.012f) * Mathf.Exp(-t * 16f);
            float v = (float)System.Math.Sin(phase) * env;
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Swoosh(buf, 0f, 0.14f, 500f, 1100f, 0.18f, 1.2f, 41);
        Synth.Bubble(buf, 0.09f, 480f, 0.05f, 0.3f, 0.2f);
        Synth.Reverb(buf, 0.25f, 0.7f, 0.5f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }

    static float[] BuildDrain()
    {
        const float Length = 4f, Fade = 0.4f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(911 + ch * 29));
            Synth.Biquad body = default, gulp = default, soft = default;
            body.Bandpass(480f, 1.3f);
            gulp.Lowpass(6f, 0.7f);
            soft.Lowpass(1400f, 0.7f);
            float[] surge = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float g = Mathf.Clamp01(0.5f + 7f * gulp.Process(rng.Next()));
                surge[i] = 0.3f + 0.7f * g * g;
                buf[i * 2 + ch] = soft.Process(body.Process(rng.Next()) * surge[i]);
            }
            // Sparse low bubbles on the surges.
            for (float at = 0f; at < Length + Fade; at += 1f / 14f)
            {
                float start = at + rng.Range(0f, 1f / 14f);
                int from = Synth.Samples(start);
                if (from >= frames || rng.Range(0f, 1f) > surge[from] * 0.6f) continue;
                Synth.Bubble(buf, start, 180f * Mathf.Pow(2f, rng.Range(0f, 1.2f)), rng.Range(0.012f, 0.025f), 0.08f * surge[from], ch == 0 ? -0.4f : 0.4f);
            }
        }
        Synth.Reverb(buf, 0.2f, 0.7f, 0.6f);
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.9f, false);
        return loop;
    }

    static float[] BuildPoof()
    {
        float[] buf = Synth.Stereo(2.4f);
        // The breath: a broad noise band falling, soft at both ends.
        Synth.Swoosh(buf, 0f, 0.45f, 1500f, 350f, 0.55f, 0.8f, 77);
        Synth.Swoosh(buf, 0.02f, 0.35f, 700f, 250f, 0.35f, 1f, 91);
        // Bubbles scattering up, thinning out.
        var rng = new Synth.Noise(313);
        for (int i = 0; i < 9; i++)
        {
            float at = 0.03f + i * 0.035f + rng.Range(0f, 0.03f);
            Synth.Bubble(buf, at, 300f * Mathf.Pow(2f, i * 0.12f + rng.Range(0f, 0.4f)), rng.Range(0.02f, 0.04f), 0.22f * (1f - i / 11f), rng.Range(-0.7f, 0.7f));
        }
        // A faint glassy colour: two detuned sine pairs, soft in, long out.
        float[] notes = { Synth.Midi(88), Synth.Midi(95) }; // E6, B6
        int count = Synth.Samples(1.6f), from = Synth.Samples(0.05f);
        for (int n = 0; n < notes.Length; n++)
            for (int i = 0; i < count && from + i < buf.Length / 2; i++)
            {
                float t = i / (float)Synth.Rate;
                float env = Mathf.SmoothStep(0f, 1f, t / 0.1f) * Mathf.Exp(-t * 2.6f) * 0.045f;
                float v = (Mathf.Sin(2f * Mathf.PI * notes[n] * t) + Mathf.Sin(2f * Mathf.PI * notes[n] * 1.003f * t)) * env;
                buf[(from + i) * 2] += v * (n == 0 ? 1f : 0.7f);
                buf[(from + i) * 2 + 1] += v * (n == 0 ? 0.7f : 1f);
            }
        Synth.Reverb(buf, 0.45f, 0.86f, 0.4f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }
}

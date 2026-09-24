using UnityEngine;

/// <summary>
/// The player's rope sounds, from VirusRope's events (no audio code in the rope):
///  - Anchored: a wet, sticky attach where a web lands on a cell (soft slap, suction "plup", glue
///    stretching, the cell's jelly wobbling). Three variants.
///  - Spooled: two different loops. Paying out = a line being cast (a soft mid swish drifting
///    slowly, warm body, faint sheen, nothing bright); reeling in = a slurp (wet gurgly "aw" in irregular surges,
///    bubbles, sucked air). Nothing tonal or swept, so no buzz or zap. Pitch and brightness follow
///    the spool speed, each in its own range. A soft "squeeze" when it starts moving.
///  - Stowed: a small wet "plp" as the last of the rope pops back in.
/// Clips are synthesized once at load (assign recorded ones to replace them). Creates itself on
/// play if the scene has a VirusRope. Cost: three 2D sources, a few floats a frame.
/// </summary>
public class RopeAudio : MonoBehaviour
{
    [Tooltip("Empty: the scene's VirusRope.")]
    public VirusRope rope;

    [Header("Clips (empty: synthesized at load)")]
    public AudioClip[] anchor;
    public AudioClip squeeze, slime, slurp, stow;

    [Header("Anchor")]
    [Range(0f, 1f)] public float anchorVolume = 0.7f;
    [Min(1f)] public float anchorRange = 40f;

    [Header("Spool")]
    [Range(0f, 1f)] public float slimeVolume = 0.3f;
    [Min(0.1f), Tooltip("Spool speed (m/s) at which the slime is fully up.")] public float fullSlimeSpeed = 15f;
    [Min(0.01f), Tooltip("Seconds the level and pitch take to follow the speed (higher = smoother).")]
    public float smoothing = 0.12f;
    [Range(0f, 1f)] public float squeezeVolume = 0.35f;
    [Range(0f, 1f)] public float stowVolume = 0.5f;

    static AudioClip[] s_anchor;
    static AudioClip s_squeeze, s_slime, s_slurp, s_stow;

    AudioSource _oneShots, _slime;
    AudioLowPassFilter _slimeFilter;
    VirusRope _hooked;
    float _speed, _speedAt, _smooth, _level, _lastSqueeze = -1f, _direction = 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<RopeAudio>() || !FindAnyObjectByType<VirusRope>()) return;
        new GameObject("Rope Audio").AddComponent<RopeAudio>();
    }

    void OnEnable() => Hook();
    void Start() { Hook(); Ready(); }

    void OnDisable()
    {
        if (_hooked)
        {
            _hooked.Anchored -= OnAnchored;
            _hooked.Spooled -= OnSpooled;
            _hooked.Stowed -= OnStowed;
        }
        _hooked = null;
    }

    void Hook()
    {
        VirusRope r = rope ? rope : FindAnyObjectByType<VirusRope>();
        if (r == _hooked) return;
        OnDisable();
        if (!r) return;
        _hooked = r;
        r.Anchored += OnAnchored;
        r.Spooled += OnSpooled;
        r.Stowed += OnStowed;
    }

    void Ready()
    {
        if (s_anchor == null)
        {
            s_anchor = new AudioClip[3];
            for (int i = 0; i < s_anchor.Length; i++) s_anchor[i] = Synth.Clip("Rope Anchor " + i, BuildAnchor((uint)(i + 1)), true);
        }
        if (!s_squeeze) s_squeeze = Synth.Clip("Rope Squeeze", BuildSqueeze());
        if (!s_slime) s_slime = Synth.Clip("Rope Cast", BuildCast());
        if (!s_slurp) s_slurp = Synth.Clip("Rope Slurp", BuildSlurp());
        if (!s_stow) s_stow = Synth.Clip("Rope Stow", BuildStow());
        if (!_oneShots)
        {
            _oneShots = gameObject.AddComponent<AudioSource>();
            _oneShots.playOnAwake = false;
            _oneShots.spatialBlend = 0f;
        }
        if (!_slime) // own object, so its low-pass leaves the one-shots alone
        {
            var go = new GameObject("Rope Slime");
            go.transform.SetParent(transform, false);
            _slime = go.AddComponent<AudioSource>();
            _slime.playOnAwake = false;
            _slime.loop = true;
            _slime.spatialBlend = 0f;
            _slime.volume = 0f;
            _slimeFilter = go.AddComponent<AudioLowPassFilter>();
        }
    }

    void OnAnchored(Vector3 point, Vector3 normal)
    {
        AudioClip[] clips = anchor != null && anchor.Length > 0 ? anchor : s_anchor;
        if (clips == null || clips.Length == 0) return;
        Sfx.Play(clips[Random.Range(0, clips.Length)], point, anchorVolume, Random.Range(0.95f, 1.06f), anchorRange);
    }

    void OnSpooled(float metres)
    {
        if (metres == 0f) return;
        _direction = Mathf.Sign(metres);
        _speed = Mathf.Abs(metres) / Mathf.Max(Time.fixedDeltaTime, 1e-4f);
        _speedAt = Time.time;
    }

    void OnStowed()
    {
        Ready();
        _oneShots.pitch = 1f;
        _oneShots.PlayOneShot(stow ? stow : s_stow, stowVolume);
    }

    void Update()
    {
        if (!_oneShots || !_slime) Ready();
        if (!_hooked) Hook();

        // Speed arrives per physics step and jitters; ease it so everything glides.
        if (Time.time - _speedAt > Time.fixedDeltaTime * 1.5f) _speed = 0f;
        float k = 1f - Mathf.Exp(-Time.deltaTime / smoothing);
        _smooth += (_speed - _smooth) * k;
        float amount = Mathf.Clamp01(_smooth / fullSlimeSpeed);

        // A soft squeeze as the rope starts moving from rest.
        if (_speed > 0.5f && _level < 0.02f && Time.time - _lastSqueeze > 0.3f)
        {
            _lastSqueeze = Time.time;
            _oneShots.pitch = (_direction > 0f ? 0.9f : 1.1f) * Random.Range(0.96f, 1.04f);
            _oneShots.PlayOneShot(squeeze ? squeeze : s_squeeze, squeezeVolume);
        }

        _level = slimeVolume * Mathf.Pow(amount, 0.6f);
        if (_level <= 0.002f) { if (_slime.isPlaying) _slime.Stop(); return; }
        AudioClip clip = _direction > 0f ? (slime ? slime : s_slime) : (slurp ? slurp : s_slurp);
        if (!_slime.isPlaying || _slime.clip != clip)
        {
            _slime.clip = clip;
            _slime.time = Random.Range(0f, clip.length * 0.9f);
            _slime.Play();
        }
        _slime.volume = _level;
        // Faster = a little higher and clearer; the cast lives up high, the slurp low.
        bool cast = _direction > 0f;
        _slime.pitch = cast ? 0.8f + 0.45f * amount : 0.8f + 0.35f * amount;
        if (_slimeFilter) _slimeFilter.cutoffFrequency = cast ? 1500f + 2000f * amount : 900f + 1800f * amount;
    }

    // ---------------- synthesis ----------------

    // Sticking to a cell: a soft wet slap on the membrane, a suction "plup" as it takes hold
    // (a blip gliding up), the glue stretching (a band of noise rising), and the cell's jelly
    // wobbling under it. Clear attack, all wet and rounded (a crisp "ch" didn't suit a living cell).
    static float[] BuildAnchor(uint seed)
    {
        float[] buf = Synth.Stereo(0.5f);
        var rng = new Synth.Noise(seed * 613u);
        Synth.Biquad slap = default, glue = default, soft = default;
        slap.Lowpass(rng.Range(1100f, 1400f), 0.8f);
        soft.Lowpass(2500f, 0.7f);
        float plupF = rng.Range(230f, 280f), jellyF = rng.Range(140f, 175f), wobble = rng.Range(10f, 14f);
        double plup = 0, jelly = 0;
        int count = Synth.Samples(0.35f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate, n = rng.Next();
            float hit = slap.Process(n) * Mathf.Exp(-t * 90f) * Mathf.Min(1f, t * 1500f) * 1.2f;
            float tp = Mathf.Max(0f, t - 0.012f); // takes hold just after contact
            plup += 2.0 * System.Math.PI * plupF * (1f + 0.6f * (1f - Mathf.Exp(-tp * 40f))) / Synth.Rate;
            float suction = t < 0.012f ? 0f : (float)System.Math.Sin(plup) * Mathf.Exp(-tp * 35f) * Mathf.Min(1f, tp * 400f) * 0.8f;
            if ((i & 7) == 0) glue.Bandpass(600f + 700f * Mathf.Min(1f, t / 0.06f), 3f);
            float stretch = glue.Process(n) * Mathf.Exp(-t * 30f) * Mathf.Min(1f, t * 150f) * 0.5f;
            jelly += 2.0 * System.Math.PI * jellyF * (1f + 0.06f * Mathf.Exp(-t * 12f) * Mathf.Sin(2f * Mathf.PI * wobble * t)) / Synth.Rate;
            float body = (float)System.Math.Sin(jelly) * Mathf.Exp(-t * 16f) * Mathf.Min(1f, t * 500f) * 0.5f;
            float v = soft.Process(hit + suction + stretch + body);
            buf[i * 2] = buf[i * 2 + 1] = v;
        }
        Synth.Reverb(buf, 0.12f, 0.55f, 0.6f);
        Synth.Normalize(buf, 0.85f);
        return buf;
    }

    // The start of a squeeze: a thick wet "shlp", a low tone swelling up in pitch under a
    // squelchy band of noise, round and muffled.
    static float[] BuildSqueeze()
    {
        float[] buf = Synth.Stereo(0.35f);
        var rng = new Synth.Noise(29);
        Synth.Biquad squelch = default, soft = default;
        soft.Lowpass(1600f, 0.7f);
        double phase = 0;
        int count = Synth.Samples(0.22f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.025f) * Mathf.Exp(-t * 14f);
            phase += 2.0 * System.Math.PI * (120f + 90f * (1f - Mathf.Exp(-t * 20f))) / Synth.Rate;
            if ((i & 15) == 0) squelch.Bandpass(350f + 500f * Mathf.Min(1f, t / 0.12f), 2.5f);
            float v = soft.Process((float)System.Math.Sin(phase) * 0.6f + squelch.Process(rng.Next()) * 1.1f) * env;
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Reverb(buf, 0.12f, 0.55f, 0.6f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }

    // Paying out: a line being cast, soft. A smooth mid "swish" of line through water (a broad band
    // drifting slowly up and down, two cycles a loop, so it's alive without a beat), a warm body
    // under it and only a faint slick sheen on top, all below ~3 kHz. A bright line hiss (2.5 + 4 kHz)
    // was "unpleasant and hissy"; a pulse train buzzed like a lawnmower; narrow swept resonances and
    // falling blips sounded like a laser.
    static float[] BuildCast()
    {
        const float Length = 4f, Fade = 0.3f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(401 + ch * 13));
            Synth.Biquad swish = default, warm = default, warmCut = default, sheen = default, grain = default, soft = default;
            warm.Lowpass(1300f, 0.7f);
            warmCut.Highpass(280f, 0.7f);
            sheen.Bandpass(2100f, 1.2f);
            grain.Lowpass(40f, 0.7f); // the sheen shimmers a little on slowed noise
            soft.Lowpass(2800f, 0.7f);
            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)Synth.Rate;
                if ((i & 7) == 0) swish.Bandpass(850f * (1f + 0.18f * Mathf.Sin(2f * Mathf.PI * 0.5f * t + ch * 1.3f)), 0.8f);
                float n = rng.Next();
                float shimmer = Mathf.Clamp(0.6f + 3f * Mathf.Abs(grain.Process(rng.Next())), 0f, 1.2f);
                float v = swish.Process(n) + warmCut.Process(warm.Process(n)) * 0.35f + sheen.Process(n) * 0.12f * shimmer;
                buf[i * 2 + ch] = soft.Process(v);
            }
        }
        Synth.Reverb(buf, 0.16f, 0.6f, 0.55f);
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.7f, false);
        return loop;
    }

    // Reeling in: a slurp. A broad wet "aw" (noise through two soft formants) broken into irregular
    // liquid gulps (its level follows slowed noise, squared, so it comes in wet surges ~10-20 a
    // second, evenly dense but never a beat), low bubbles popping on the surges, and a little sucked
    // air on top. Low and gurgly, the opposite of the cast.
    static float[] BuildSlurp()
    {
        const float Length = 4f, Fade = 0.3f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(733 + ch * 17));
            Synth.Biquad f1 = default, f2 = default, suck = default, gulp = default, soft = default;
            f1.Bandpass(650f, 2f);
            f2.Bandpass(1100f, 2.5f);
            suck.Bandpass(2000f, 1f);
            gulp.Lowpass(14f, 0.7f);
            soft.Lowpass(2600f, 0.7f);
            float[] surge = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float g = Mathf.Clamp01(0.5f + 9f * gulp.Process(rng.Next()));
                surge[i] = 0.15f + 0.85f * g * g;
                float n = rng.Next();
                float wet = (f1.Process(n) * 1.4f + f2.Process(n) * 0.8f) * surge[i];
                buf[i * 2 + ch] = soft.Process(wet + suck.Process(n) * 0.2f * (0.4f + 0.6f * surge[i]));
            }

            // Bubbles, denser and bigger where the slurp surges.
            for (float at = 0f; at < Length + Fade; at += 1f / 120f)
            {
                float start = at + rng.Range(0f, 1f / 120f);
                int from = Synth.Samples(start);
                if (from >= frames || rng.Range(0f, 1f) > surge[from]) continue;
                float f = 170f * Mathf.Pow(2.5f, rng.Range(0f, 1f)), len = rng.Range(0.006f, 0.014f);
                float gain = rng.Range(0.3f, 1f) * 0.12f * surge[from];
                int count = Synth.Samples(len * 4f);
                double phase = 0;
                for (int k = 0; k < count && from + k < frames; k++)
                {
                    float tk = k / (float)Synth.Rate;
                    phase += 2.0 * System.Math.PI * f * (1f + 0.3f * tk / (len * 4f)) / Synth.Rate;
                    float env = Mathf.SmoothStep(0f, 1f, tk / 0.002f) * Mathf.Exp(-tk / len);
                    buf[(from + k) * 2 + ch] += (float)System.Math.Sin(phase) * env * gain;
                }
            }
        }
        Synth.Reverb(buf, 0.16f, 0.6f, 0.6f);
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.95f, false); // its bubbles set the peak: match the cast's loudness
        return loop;
    }

    // "plp": the end popping back into the mouth: a quick falling blip and a bubble.
    static float[] BuildStow()
    {
        float[] buf = Synth.Stereo(0.4f);
        double phase = 0;
        int count = Synth.Samples(0.12f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            phase += 2.0 * System.Math.PI * (150f + 180f * Mathf.Exp(-t * 35f)) / Synth.Rate;
            float v = (float)System.Math.Sin(phase) * Mathf.Exp(-t * 30f) * Mathf.SmoothStep(0f, 1f, t / 0.004f);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Bubble(buf, 0.05f, 520f, 0.05f, 0.35f, 0f);
        Synth.Reverb(buf, 0.12f, 0.55f, 0.6f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }
}

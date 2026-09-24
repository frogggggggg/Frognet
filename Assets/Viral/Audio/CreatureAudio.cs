using UnityEngine;

/// <summary>
/// Creature sounds: steps, landings, dashes (any creature, through the <see cref="Sfx"/> pool) and
/// the player's flight (a loop that opens up with speed). Inside a body, so all wet and soft:
///  - Step: a soft round "bloo", a spider walking on a slime ball: a low tone gliding down into its
///    note with a gentle onset and a slight gel wobble, a breath of wet noise. (A click, then a
///    thumpy squelch, both read as too sharp.) Every step of the player's and of the `voicedWalkers`
///    nearest other creatures crawling within `nearStepRange` (whole gaits, so they keep their
///    rhythm; random single legs from many sounded erratic). Everyone else is the crowd bed: one
///    smooth 2D loop of soft low patter whose level follows how many creatures near the camera are
///    actually crawling, and how fast (a scan of Organism.All every `scanInterval`). Driven by
///    motion, not steps: after stopping, feet keep taking settling steps for a second or two, and a
///    step-driven bed trailed off with them. Six variants, pitched by leg size and at random.
///  - Impact (landing): a trampoline bounce: a stretchy "sproing" (pitch glides up, wobbles) and
///    rebounds coming quicker and quieter like a ball settling. Built at ImpactTiers strengths:
///    a harder landing picks a harder tier (lower, deeper, more and longer bounces) and plays louder.
///  - Burst (dash): an underwater whoosh with a trail of bubbles.
///  - Flight: gliding through water: a smooth, soft, gently swelling rush (no bubbles, no
///    resonant gurgle: both were shrill / unpleasant; rumble under 140 Hz cut), kept in the
///    background: quiet, and only really up near full speed (`flightCurve`).
///
/// Callers: SpiderLegWalker (a foot plants), the ImpactSound / BurstSound effects on Landing /
/// Charging. Clips are synthesized once at load (assign recorded ones to replace them). Creates
/// itself on play if the scene has none. Cost per event: a distance check (Sfx), bounded by the
/// pool; the flight loop is one 2D source with a low-pass filter.
/// </summary>
public class CreatureAudio : MonoBehaviour
{
    public static CreatureAudio Instance { get; private set; }

    [Header("Clips (empty: synthesized at load)")]
    public AudioClip[] steps;
    public AudioClip[] impacts;
    public AudioClip burst;
    public AudioClip flight;
    public AudioClip crowd;

    [Header("Steps")]
    [Range(0f, 1f)] public float stepVolume = 0.2f;
    [Min(1f), Tooltip("Metres from the camera a step can be heard.")] public float stepRange = 25f;
    [Tooltip("Leg length (SpiderLegWalker footDistance) that plays at the clips' own pitch; longer legs are lower.")]
    public float referenceLeg = 1.5f;

    [Header("Crowd")]
    [Range(0, 8), Tooltip("How many of the nearest other crawling creatures get their own steps.")]
    public int voicedWalkers = 2;
    [Range(0f, 1f), Tooltip("Their steps' volume, relative to Step Volume.")]
    public float nearStepVolume = 0.5f;
    [Min(0f), Tooltip("Metres from the camera within which another creature's steps may be voiced.")]
    public float nearStepRange = 10f;
    [Range(0f, 1f)] public float crowdVolume = 0.15f;
    [Min(1f), Tooltip("Crawling creatures within this many metres of the camera feed the crowd bed (nearer count more).")]
    public float crowdRange = 40f;
    [Min(0.1f), Tooltip("Weighted crawlers (1 = one at full speed right at the camera) at which the bed is fully up.")]
    public float fullCrowd = 8f;
    [Min(0.02f), Tooltip("Seconds between scans of the crowd (O(creatures) each).")]
    public float scanInterval = 0.1f;
    [Min(0.01f), Tooltip("Seconds for the crowd bed to fade out once they stop.")]
    public float crowdFade = 0.3f;

    [Header("Landing")]
    [Range(0f, 1f)] public float impactVolume = 0.8f;
    [Min(1f)] public float impactRange = 45f;
    [Min(0.1f), Tooltip("Landing speed (m/s) that plays at full volume.")] public float fullImpactSpeed = 15f;

    [Header("Dash")]
    [Range(0f, 1f)] public float burstVolume = 0.6f;
    [Min(1f)] public float burstRange = 40f;

    [Header("Flight (player)")]
    [Range(0f, 1f)] public float flightVolume = 0.15f;
    [Min(1f), Tooltip("Higher keeps it out of the way at cruising speed and only brings it up near full speed.")]
    public float flightCurve = 1.8f;
    [Min(0.1f), Tooltip("Speed (m/s) at which the flight sound is fully open.")] public float fullFlightSpeed = 25f;
    [Min(0.01f), Tooltip("Seconds to ease the loop toward the current speed.")] public float flightEase = 0.25f;

    static AudioClip[] s_steps;
    static AudioClip[][] s_impacts; // [strength tier][variant]
    const int ImpactTiers = 4, ImpactVariants = 2;
    static AudioClip s_burst, s_flight, s_crowd;

    AudioSource _flight, _crowd;
    float _crowdTarget, _crowdLevel, _nextScan;
    readonly Organism[] _voiced = new Organism[8];
    readonly float[] _voicedDistance = new float[8];
    AudioLowPassFilter _flightFilter;
    Organism _player;
    float _open;

    AudioClip[] Steps => steps != null && steps.Length > 0 ? steps : s_steps;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<CreatureAudio>()) return;
        new GameObject("Creature Audio").AddComponent<CreatureAudio>();
    }

    /// <summary>The player: the VirusMovement without a VirusAI.</summary>
    public static VirusMovement FindPlayer()
    {
        foreach (var m in FindObjectsByType<VirusMovement>(FindObjectsSortMode.None))
            if (!m.TryGetComponent<VirusAI>(out _)) return m;
        return null;
    }

    void OnEnable() => Instance = this;
    void OnDisable() { if (Instance == this) Instance = null; }

    void Start() => Ready();

    void Ready()
    {
        if (s_steps == null)
        {
            s_steps = new AudioClip[6];
            for (int i = 0; i < s_steps.Length; i++) s_steps[i] = Synth.Clip("Step " + i, BuildStep((uint)(i + 1)), true);
        }
        if (s_impacts == null)
        {
            s_impacts = new AudioClip[ImpactTiers][];
            for (int t = 0; t < ImpactTiers; t++)
            {
                s_impacts[t] = new AudioClip[ImpactVariants];
                for (int v = 0; v < ImpactVariants; v++)
                    s_impacts[t][v] = Synth.Clip($"Impact {t}.{v}", BuildImpact((uint)(t * 10 + v + 11), t / (ImpactTiers - 1f)), true);
            }
        }
        if (!s_burst) s_burst = Synth.Clip("Burst", BuildBurst(), true);
        if (!s_flight) s_flight = Synth.Clip("Flight", BuildFlight());
        if (!s_crowd) s_crowd = Synth.Clip("Crowd Patter", BuildCrowd());
        if (!_crowd)
        {
            var go = new GameObject("Crowd Bed"); // own object: the flight's low-pass filter would muffle it
            go.transform.SetParent(transform, false);
            _crowd = go.AddComponent<AudioSource>();
            _crowd.loop = true;
            _crowd.playOnAwake = false;
            _crowd.spatialBlend = 0f;
            _crowd.volume = 0f;
        }
        if (!_flight)
        {
            _flight = gameObject.AddComponent<AudioSource>();
            _flight.loop = true;
            _flight.playOnAwake = false;
            _flight.spatialBlend = 0f;
            _flight.volume = 0f;
            _flightFilter = gameObject.AddComponent<AudioLowPassFilter>();
        }
    }

    // ---------------- events (any creature) ----------------

    /// <summary>A foot planted. 'leg' = the leg's length (bigger legs sound lower); 'owner' = whose.</summary>
    public static void Step(Vector3 position, float leg, ISurfaceContact owner)
    {
        var a = Instance;
        if (!a || a.stepVolume <= 0f) return;
        float volume = a.stepVolume;
        if (!ReferenceEquals(owner, a._player))
        {
            if (!a.Voiced(owner)) return; // the crowd bed covers it
            volume *= a.nearStepVolume;
        }
        AudioClip[] clips = a.Steps;
        if (clips == null || clips.Length == 0) return;
        float pitch = Mathf.Clamp(Mathf.Sqrt(a.referenceLeg / Mathf.Max(0.05f, leg)), 0.5f, 2f) * Random.Range(0.93f, 1.08f);
        Sfx.Play(clips[Random.Range(0, clips.Length)], position, volume * Random.Range(0.75f, 1f), pitch, a.stepRange);
    }

    /// <summary>Touched down at 'speed' m/s.</summary>
    public static void Impact(Vector3 position, float speed)
    {
        var a = Instance;
        if (!a || a.impactVolume <= 0f) return;
        float hard = Mathf.Clamp01(speed / a.fullImpactSpeed);
        float volume = a.impactVolume * Mathf.Lerp(0.3f, 1f, Mathf.Sqrt(hard));
        if (a.impacts != null && a.impacts.Length > 0) // recorded clips: one set, scaled by volume and pitch
        {
            Sfx.Play(a.impacts[Random.Range(0, a.impacts.Length)], position, volume, Mathf.Lerp(1.15f, 0.85f, hard), a.impactRange);
            return;
        }
        if (s_impacts == null) return;
        AudioClip[] tier = s_impacts[Mathf.Clamp(Mathf.RoundToInt(hard * (ImpactTiers - 1)), 0, ImpactTiers - 1)];
        Sfx.Play(tier[Random.Range(0, tier.Length)], position, volume, Random.Range(0.96f, 1.04f), a.impactRange);
    }

    /// <summary>A dash started.</summary>
    public static void Burst(Vector3 position)
    {
        var a = Instance;
        if (!a || a.burstVolume <= 0f) return;
        Sfx.Play(a.burst ? a.burst : s_burst, position, a.burstVolume, Random.Range(0.95f, 1.08f), a.burstRange);
    }

    // ---------------- player flight ----------------

    void Update()
    {
        if (!_flight) Ready();
        if (!_player)
        {
            VirusMovement m = FindPlayer();
            _player = m ? m.Organism : null;
        }

        UpdateCrowd();

        float target = 0f;
        if (_player && _player.Rb && _player.InState(_player.flying))
            target = Mathf.Clamp01(_player.Rb.linearVelocity.magnitude / fullFlightSpeed);
        _open = Mathf.MoveTowards(_open, target, Time.deltaTime / flightEase);

        float volume = flightVolume * Mathf.Pow(_open, flightCurve); // background: it rises only when really moving
        if (volume <= 0.001f) { if (_flight.isPlaying) _flight.Stop(); return; }
        if (!_flight.isPlaying)
        {
            _flight.clip = flight ? flight : s_flight;
            _flight.time = Random.Range(0f, _flight.clip.length * 0.9f);
            _flight.Play();
        }
        _flight.volume = volume;
        _flight.pitch = 0.92f + 0.16f * _open; // pitching a noise bed much reads as a machine
        _flightFilter.cutoffFrequency = 250f + 1500f * _open * _open; // water: stays muffled
    }

    bool Voiced(ISurfaceContact owner)
    {
        for (int i = 0; i < voicedWalkers && i < _voiced.Length; i++)
            if (_voiced[i] && ReferenceEquals(owner, _voiced[i])) return true;
        return false;
    }

    // Every scanInterval: how much crawling there is near the camera (the bed's level), and the
    // nearest few crawlers (voiced). O(creatures) per scan, a distance and a state check each.
    void ScanCrowd()
    {
        Vector3 cam = SimulationTicker.CameraPosition;
        float sum = 0f, range2 = crowdRange * crowdRange;
        int voiced = Mathf.Min(voicedWalkers, _voiced.Length);
        for (int i = 0; i < _voiced.Length; i++) { _voiced[i] = null; _voicedDistance[i] = float.MaxValue; }

        foreach (Organism o in Organism.All)
        {
            if (!o || o == _player || !o.OnSurface) continue;
            float d2 = (o.transform.position - cam).sqrMagnitude;
            if (d2 >= range2) continue;
            Crawl crawl = o.grounded.moving.crawl;
            float pace = crawl.speed > 0f ? Mathf.Clamp01(crawl.CurrentSpeed / crawl.speed) : 0f;
            if (pace < 0.15f) continue; // standing, or only settling its feet
            float d = Mathf.Sqrt(d2);
            sum += pace * (1f - d / crowdRange);

            if (d > nearStepRange) continue;
            for (int k = 0; k < voiced; k++) // insert into the nearest few
            {
                if (d >= _voicedDistance[k]) continue;
                for (int m = voiced - 1; m > k; m--) { _voiced[m] = _voiced[m - 1]; _voicedDistance[m] = _voicedDistance[m - 1]; }
                _voiced[k] = o;
                _voicedDistance[k] = d;
                break;
            }
        }
        _crowdTarget = sum < 0.3f ? 0f : crowdVolume * Mathf.Pow(Mathf.Clamp01(sum / fullCrowd), 0.6f);
    }

    void UpdateCrowd()
    {
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + scanInterval;
            ScanCrowd();
        }
        float rate = crowdVolume / (_crowdTarget > _crowdLevel ? 0.2f : crowdFade);
        _crowdLevel = Mathf.MoveTowards(_crowdLevel, _crowdTarget, rate * Time.deltaTime);

        if (_crowdLevel <= 0.002f) { if (_crowd.isPlaying) _crowd.Stop(); return; }
        if (!_crowd.isPlaying)
        {
            _crowd.clip = crowd ? crowd : s_crowd;
            _crowd.time = Random.Range(0f, _crowd.clip.length * 0.9f);
            _crowd.Play();
        }
        _crowd.volume = _crowdLevel;
    }

    // ---------------- synthesis ----------------

    static float[] BuildStep(uint seed)
    {
        float[] buf = Synth.Stereo(0.25f);
        var rng = new Synth.Noise(seed * 97u);
        float f0 = rng.Range(170f, 240f);
        Bloo(buf, 0, f0, rng.Range(9f, 14f), 1f, 0f, ref rng);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // "bloo": the pitch starts ~35% high and glides down into f0 over ~40 ms (the "bl" into the
    // "oo"), a soft ~8 ms onset, a faint 2nd harmonic for roundness, a slight gel wobble, and only a
    // breath of low wet noise. Dark (1.2 kHz) and short (~0.15 s).
    static void Bloo(float[] buf, int from, float f0, float wobble, float gain, float pan, ref Synth.Noise rng)
    {
        Synth.Biquad wet = default, soft = default;
        wet.Bandpass(rng.Range(350f, 600f), 1.5f);
        soft.Lowpass(1200f, 0.7f);
        float l = gain * Mathf.Sqrt(1f - pan), r = gain * Mathf.Sqrt(1f + pan);
        int count = Mathf.Min(buf.Length / 2 - from, Synth.Samples(0.2f));
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float f = f0 * (1f + 0.35f * Mathf.Exp(-t * 25f)) * (1f + 0.04f * Mathf.Exp(-t * 20f) * Mathf.Sin(2f * Mathf.PI * wobble * t));
            phase += 2.0 * System.Math.PI * f / Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.008f) * Mathf.Exp(-t * 22f);
            float tone = (float)System.Math.Sin(phase) + 0.15f * (float)System.Math.Sin(2.0 * phase);
            float v = soft.Process((tone + wet.Process(rng.Next()) * 0.25f) * env);
            int k = (from + i) * 2;
            buf[k] += v * l;
            buf[k + 1] += v * r;
        }
    }

    // Trampoline bounce at 'strength' 0 (a light hop) .. 1 (a hard landing): a stretchy "sproing"
    // then rebounds, each gap shorter than the last (a ball settling), each quieter, a little higher
    // and shorter. Harder: lower, a bigger glide, more bounces, longer gaps and rings, and a deep
    // membrane thump under the first. A few bubbles.
    static float[] BuildImpact(uint seed, float strength)
    {
        float[] buf = Synth.Stereo(2f);
        var rng = new Synth.Noise(seed * 131u);
        int bounces = 2 + Mathf.RoundToInt(4f * strength);
        float f0 = Mathf.Lerp(135f, 75f, strength) * rng.Range(0.94f, 1.06f);
        float sweep = Mathf.Lerp(0.45f, 1f, strength);      // how far the pitch glides up (x f0)
        float ring = Mathf.Lerp(8f, 4f, strength);          // decay rate of the first bounce, 1/s
        float gap = Mathf.Lerp(0.13f, 0.28f, strength) * rng.Range(0.92f, 1.08f);
        float wobble = rng.Range(11f, 15f), at = 0f, amp = 1f;
        for (int k = 0; k < bounces; k++)
        {
            Sproing(buf, at, f0 * (1f + 0.08f * k), sweep * (1f - 0.12f * k), amp, ring * (1f + 0.45f * k), wobble, (uint)(seed + k * 7));
            at += gap;
            gap *= 0.68f;
            amp *= Mathf.Lerp(0.45f, 0.6f, strength);
        }
        Membrane(buf, Mathf.Lerp(62f, 48f, strength), 0.6f * strength);
        for (int k = 0, n = 2 + Mathf.RoundToInt(3f * strength); k < n; k++)
            Synth.Bubble(buf, rng.Range(0.05f, 0.4f), rng.Range(350f, 750f), rng.Range(0.04f, 0.08f), 0.1f, rng.Range(-0.3f, 0.3f));
        Lowpass(buf, 3200f);
        Synth.Reverb(buf, 0.15f, 0.6f, 0.6f);
        Synth.Normalize(buf, Mathf.Lerp(0.6f, 0.9f, strength));
        return buf;
    }

    // One bounce, the trampoline "sproing": the pitch glides from 0.7x up by 'sweep' as the skin
    // stretches, a spring vibrato wobbling it and dying away; a touch of 2nd and 3rd harmonic for a
    // rubbery tone; a short soft slap at contact.
    static void Sproing(float[] buf, float start, float f0, float sweep, float amp, float decay, float wobble, uint seed)
    {
        var rng = new Synth.Noise(seed * 977u + 5u);
        Synth.Biquad slap = default;
        slap.Lowpass(1500f, 0.8f);
        int from = Synth.Samples(start), count = Mathf.Min(buf.Length / 2 - from, Synth.Samples(6f / decay));
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float glide = 0.7f + sweep * (1f - Mathf.Exp(-t * 16f));
            float vibrato = 1f + 0.09f * Mathf.Exp(-t * 3.5f) * Mathf.Sin(2f * Mathf.PI * wobble * t);
            phase += 2.0 * System.Math.PI * f0 * glide * vibrato / Synth.Rate;
            float env = amp * Mathf.Exp(-t * decay) * Mathf.Min(1f, t * 300f);
            float body = ((float)System.Math.Sin(phase) + 0.3f * (float)System.Math.Sin(2.0 * phase)
                          + 0.1f * (float)System.Math.Sin(3.0 * phase)) * env;
            float wet = slap.Process(rng.Next()) * amp * 0.25f * Mathf.Exp(-t * 70f);
            int k = (from + i) * 2;
            buf[k] += body + wet;
            buf[k + 1] += body + wet;
        }
    }

    // The trampoline's skin: a deep thump that drops in pitch, under the first bounce.
    static void Membrane(float[] buf, float freq, float amp)
    {
        if (amp <= 0f) return;
        int count = Mathf.Min(buf.Length / 2, Synth.Samples(0.6f));
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            phase += 2.0 * System.Math.PI * freq * (1f + 0.5f * Mathf.Exp(-t * 20f)) / Synth.Rate;
            float v = (float)System.Math.Sin(phase) * amp * Mathf.Exp(-t * 9f) * Mathf.Min(1f, t * 300f);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
    }

    static void Lowpass(float[] buf, float hz)
    {
        Synth.Biquad l = default, r = default;
        l.Lowpass(hz, 0.7f); r.Lowpass(hz, 0.7f);
        for (int i = 0; i < buf.Length / 2; i++) { buf[i * 2] = l.Process(buf[i * 2]); buf[i * 2 + 1] = r.Process(buf[i * 2 + 1]); }
    }

    // Underwater whoosh with a trail of bubbles.
    static float[] BuildBurst()
    {
        float[] buf = Synth.Stereo(1f);
        Synth.Swoosh(buf, 0f, 0.5f, 250f, 1800f, 0.9f, 0.8f, 21);
        var rng = new Synth.Noise(23);
        for (int b = 0; b < 6; b++)
            Synth.Bubble(buf, rng.Range(0.1f, 0.6f), rng.Range(500f, 1200f), rng.Range(0.03f, 0.07f), 0.15f, 0f);
        Lowpass(buf, 2200f);
        Synth.Reverb(buf, 0.2f, 0.6f, 0.5f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }

    // Gliding through water, a seamless 6 s loop: brown noise (deep, soft, no hiss) low-passed,
    // plus a broad, quiet "flow" band a little higher that the speed filter opens up, both swelling
    // slowly and unevenly on each side so it breathes. Nothing narrow or resonant, nothing bright.
    static float[] BuildFlight()
    {
        const float Length = 6f, Fade = 1f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(31 + ch * 17));
            Synth.Biquad deep = default, flow = default;
            deep.Lowpass(320f, 0.6f);
            flow.Bandpass(650f, 0.6f);
            float p1 = rng.Range(0f, 6.28f), p2 = rng.Range(0f, 6.28f);
            float brown = 0f;
            for (int i = 0; i < frames; i++)
            {
                float t = i / (float)Synth.Rate, w = rng.Next();
                brown = (brown + 0.02f * w) / 1.02f; // leaky integrator: brown noise
                float swell = (0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 0.17f * t + p1)) * (0.85f + 0.15f * Mathf.Sin(2f * Mathf.PI * 0.41f * t + p2));
                float flowSwell = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 0.29f * t + p2);
                buf[i * 2 + ch] = deep.Process(brown * 6f) * swell + flow.Process(w) * 0.5f * flowSwell;
            }
        }
        // Thin out the rumble under ~140 Hz: that weight is what made it sit in front of everything.
        Synth.Biquad hl = default, hr = default;
        hl.Highpass(140f, 0.7f); hr.Highpass(140f, 0.7f);
        for (int i = 0; i < frames; i++) { buf[i * 2] = hl.Process(buf[i * 2]); buf[i * 2 + 1] = hr.Process(buf[i * 2 + 1]); }
        Synth.Reverb(buf, 0.15f, 0.7f, 0.7f);
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.8f, false);
        return loop;
    }

    // Crowd bed, a seamless 6 s loop: a soft low wash (brown noise, low-passed) with many quiet,
    // low "bloo" steps too dense to pick out, spread across the stereo field: a texture, not a
    // spray of distinct pitched blips (that sounded like popcorn).
    static float[] BuildCrowd()
    {
        const float Length = 6f, Fade = 0.8f;
        float[] buf = Synth.Stereo(Length + Fade);
        var rng = new Synth.Noise(77);
        for (int e = 0; e < 450; e++)
        {
            int from = Synth.Samples(rng.Range(0f, Length + Fade - 0.2f));
            Bloo(buf, from, rng.Range(110f, 180f), rng.Range(9f, 14f), rng.Range(0.15f, 0.5f), rng.Range(-0.8f, 0.8f), ref rng);
        }
        for (int ch = 0; ch < 2; ch++)
        {
            var n = new Synth.Noise((uint)(55 + ch));
            Synth.Biquad lp = default;
            lp.Lowpass(400f, 0.6f);
            float brown = 0f;
            for (int i = 0; i < buf.Length / 2; i++)
            {
                brown = (brown + 0.02f * n.Next()) / 1.02f;
                buf[i * 2 + ch] += lp.Process(brown * 6f) * 0.5f;
            }
        }
        Lowpass(buf, 900f);
        Synth.Reverb(buf, 0.2f, 0.6f, 0.6f);
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Normalize(loop, 0.8f, false);
        return loop;
    }
}

using UnityEngine;

/// <summary>
/// Sounds for white blood cells (WhiteBloodCells' events + a look at the nearest cells, so the cells have no
/// audio code). Everything wet, low and muffled; tension from intervals and sparseness, not loudness.
/// - Presence (loop): the nearest <see cref="LoopVoices"/> cells within <see cref="presenceRange"/> of the camera
///   churn softly: a slow amoeboid surge of low brown noise, a breathing band and a few deep "blub"s. Louder while
///   hunting / holding something, lower-pitched for bigger cells. You hear one coming before you see it.
/// - Strain (loop, same voices): while a cell reels its catch in, a rubbery stretch (two broad drifting bands in
///   irregular tugs, a faint low moan), louder and higher with the arm's tension.
/// - Noticed (only the player being hunted): a soft low swell, two muffled sines a tritone apart.
/// - Engulfing (the grab): a deep wet gulp. Gulped (each gulp while reeling): a small one.
/// - TornFree: the suction lets go (a short upward "thwup"), the arm whips back, bubbles rise, a faint glassy glint.
/// - Merging: a drop into a pool, a plop and a deep jelly wobble, louder the harder it came in.
/// - Absorbed: sinking away, falling bubbles and a dark tritone under a descending wash.
/// - Respawned (player): a rising wash and bubbles, a faint D6 / A6 shimmer.
/// Creates itself on play. Clips are built once at load and shared. Cost: one pass over the cells every
/// <see cref="scanInterval"/> (O(cells)), <see cref="LoopVoices"/> x 2 looping sources.
/// </summary>
public class WhiteBloodCellAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float noticeVolume = 0.25f;
    [Range(0f, 1f)] public float gulpVolume = 0.6f;
    [Range(0f, 1f)] public float smallGulpVolume = 0.18f;
    [Range(0f, 1f)] public float tearVolume = 0.6f;
    [Range(0f, 1f)] public float mergeVolume = 0.65f;
    [Range(0f, 1f)] public float absorbVolume = 0.55f;
    [Range(0f, 1f)] public float respawnVolume = 0.4f;
    [Min(1f)] public float range = 80f;
    [Range(0f, 1f), Tooltip("One-shots for a catch that isn't the player play this much quieter.")]
    public float otherPreyVolume = 0.45f;
    [Min(0f), Tooltip("The hunt swell plays at most this often (any cell), and a cell re-noticing the player within it stays quiet.")]
    public float noticeCooldown = 12f;
    [Min(0f), Tooltip("Gulps while reeling play at most this often (any cell); the rest are silent.")]
    public float smallGulpGap = 2.2f;

    [Header("Loops")]
    [Range(0f, 1f), Tooltip("Presence loop level for an idle cell (hunting / holding are louder).")]
    public float presenceVolume = 0.12f;
    [Range(0f, 1f)] public float strainVolume = 0.25f;
    [Min(1f), Tooltip("Cells within this (from their surface) of the camera get a presence loop.")]
    public float presenceRange = 45f;
    [Min(0.02f)] public float scanInterval = 0.25f;
    [Min(0.1f), Tooltip("Cells this radius (m) play the loops at pitch 1; bigger ones lower.")]
    public float referenceRadius = 7f;

    public const int LoopVoices = 2;

    static AudioClip s_notice, s_gulp, s_smallGulp, s_tear, s_merge, s_absorb, s_respawn, s_presence, s_strain;

    class Voice
    {
        public WhiteBloodCell cell;
        public AudioSource presence, strain;
    }

    readonly Voice[] _voices = new Voice[LoopVoices];
    readonly WhiteBloodCell[] _nearest = new WhiteBloodCell[LoopVoices];
    float _nextScan, _nextNotice, _nextSmallGulp;
    WhiteBloodCell _lastNoticer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<WhiteBloodCellAudio>()) return;
        new GameObject("White Blood Cell Audio").AddComponent<WhiteBloodCellAudio>();
    }

    void OnEnable()
    {
        WhiteBloodCells.Noticed += OnNoticed;
        WhiteBloodCells.Engulfing += OnEngulfing;
        WhiteBloodCells.Gulped += OnGulped;
        WhiteBloodCells.TornFree += OnTornFree;
        WhiteBloodCells.Merging += OnMerging;
        WhiteBloodCells.Absorbed += OnAbsorbed;
        WhiteBloodCells.Respawned += OnRespawned;
    }

    void OnDisable()
    {
        WhiteBloodCells.Noticed -= OnNoticed;
        WhiteBloodCells.Engulfing -= OnEngulfing;
        WhiteBloodCells.Gulped -= OnGulped;
        WhiteBloodCells.TornFree -= OnTornFree;
        WhiteBloodCells.Merging -= OnMerging;
        WhiteBloodCells.Absorbed -= OnAbsorbed;
        WhiteBloodCells.Respawned -= OnRespawned;
    }

    void Ready()
    {
        if (s_notice) return;
        s_notice = Synth.Clip("White Cell Notice", BuildNotice(), true);
        s_gulp = Synth.Clip("White Cell Gulp", BuildGulp(), true);
        s_smallGulp = Synth.Clip("White Cell Small Gulp", BuildSmallGulp(), true);
        s_tear = Synth.Clip("White Cell Torn Free", BuildTear(), true);
        s_merge = Synth.Clip("White Cell Merge", BuildMerge(), true);
        s_absorb = Synth.Clip("White Cell Absorb", BuildAbsorb(), true);
        s_respawn = Synth.Clip("White Cell Respawn", BuildRespawn(), true);
        s_presence = Synth.Clip("White Cell Presence", BuildPresence(), true);
        s_strain = Synth.Clip("White Cell Strain", BuildStrain(), true);
    }

    static bool IsPlayer(Organism o) => o && o.GetComponent<VirusMovement>();
    float PreyGain(Organism o) => IsPlayer(o) ? 1f : otherPreyVolume;

    // A cell losing and re-finding the player (or several cells at once) would restack the swell every few
    // seconds: one at a time, and never twice from the same cell within the cooldown.
    void OnNoticed(WhiteBloodCell cell, Organism prey)
    {
        if (!IsPlayer(prey) || Time.time < _nextNotice) return;
        if (cell == _lastNoticer && Time.time < _nextNotice + noticeCooldown) return;
        _nextNotice = Time.time + noticeCooldown;
        _lastNoticer = cell;
        Ready();
        Sfx.Play(s_notice, cell.Spot, noticeVolume, Random.Range(0.95f, 1.05f), range);
    }

    void OnEngulfing(WhiteBloodCell cell, Organism prey)
    {
        Ready();
        Sfx.Play(s_gulp, prey ? prey.transform.position : cell.Spot, gulpVolume * PreyGain(prey), Random.Range(0.9f, 1.05f), range);
    }

    // The cell gulps every ~0.9 s while reeling (it shows); heard every one it was a metronome.
    void OnGulped(WhiteBloodCell cell, Organism prey)
    {
        if (Time.time < _nextSmallGulp) return;
        _nextSmallGulp = Time.time + smallGulpGap * Random.Range(0.8f, 1.3f);
        Ready();
        Sfx.Play(s_smallGulp, cell.Spot, smallGulpVolume * PreyGain(prey), Random.Range(0.8f, 1.15f), range * 0.5f);
    }

    void OnTornFree(WhiteBloodCell cell, Organism prey)
    {
        Ready();
        Sfx.Play(s_tear, prey ? prey.transform.position : cell.Spot, tearVolume * PreyGain(prey), Random.Range(0.95f, 1.05f), range);
    }

    void OnMerging(WhiteBloodCell cell, Organism prey, float speed)
    {
        Ready();
        float k = Mathf.Clamp01(speed / 12f); // harder = louder and lower
        Sfx.Play(s_merge, prey ? prey.transform.position : cell.Spot, mergeVolume * PreyGain(prey) * Mathf.Lerp(0.5f, 1f, k),
                 Mathf.Lerp(1.08f, 0.9f, k) * Random.Range(0.97f, 1.03f), range);
    }

    void OnAbsorbed(WhiteBloodCell cell, Organism prey)
    {
        Ready();
        Vector3 at = prey ? prey.transform.position : cell.transform.position;
        Sfx.Play(s_absorb, at, absorbVolume * PreyGain(prey), Random.Range(0.95f, 1.03f), range);
    }

    void OnRespawned(Organism o)
    {
        if (!o) return;
        Ready();
        Sfx.Play(s_respawn, o.transform.position, respawnVolume, 1f, range);
    }

    // ---------------- loops ----------------

    void Update()
    {
        if (WhiteBloodCells.All.Count == 0 && _voices[0] == null) return;
        Ready();
        if (_voices[0] == null) MakeVoices();

        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + scanInterval;
            Scan();
        }

        float dt = Time.deltaTime;
        foreach (Voice v in _voices)
        {
            WhiteBloodCell c = v.cell;
            float presence = 0f, strain = 0f, pitch = v.presence.pitch, tension = 0f;
            if (c)
            {
                v.presence.transform.position = c.transform.position;
                float state = c.Current switch
                {
                    WhiteBloodCell.State.Hunt => 1.3f,
                    WhiteBloodCell.State.Grip => 1.5f,
                    WhiteBloodCell.State.Engulf => 1.5f,
                    WhiteBloodCell.State.Examine => 1.1f,
                    _ => 1f,
                };
                presence = presenceVolume * state;
                pitch = Mathf.Clamp(Mathf.Pow(referenceRadius / Mathf.Max(c.Radius, 0.1f), 0.35f), 0.7f, 1.3f);
                if (c.Current == WhiteBloodCell.State.Grip)
                {
                    tension = c.Tension;
                    strain = strainVolume * Mathf.Lerp(0.3f, 1f, tension);
                }
            }
            v.presence.volume = Mathf.MoveTowards(v.presence.volume, presence, dt * 0.4f);
            v.presence.pitch = pitch;
            v.strain.volume = Mathf.MoveTowards(v.strain.volume, strain, dt * (strain > v.strain.volume ? 1.5f : 0.8f));
            v.strain.pitch = pitch * Mathf.Lerp(0.9f, 1.2f, tension);
        }
    }

    // The nearest cells (by distance from their surface) within presenceRange keep or take a voice. A voice
    // only takes a new cell once it has faded out, so no loop jumps across the world mid-sound.
    void Scan()
    {
        Vector3 cam = SimulationTicker.CameraPosition;
        float d0 = float.MaxValue, d1 = float.MaxValue;
        _nearest[0] = _nearest[1] = null;
        foreach (WhiteBloodCell c in WhiteBloodCells.All)
        {
            if (!c || !c.isActiveAndEnabled) continue;
            float d = Vector3.Distance(c.transform.position, cam) - c.Radius;
            if (d > presenceRange) continue;
            if (d < d0) { d1 = d0; _nearest[1] = _nearest[0]; d0 = d; _nearest[0] = c; }
            else if (d < d1) { d1 = d; _nearest[1] = c; }
        }

        foreach (Voice v in _voices)
            if (v.cell && v.cell != _nearest[0] && v.cell != _nearest[1]) v.cell = null;
        foreach (WhiteBloodCell c in _nearest)
        {
            if (!c || _voices[0].cell == c || _voices[1].cell == c) continue;
            foreach (Voice v in _voices)
                if (!v.cell && v.presence.volume < 0.01f && v.strain.volume < 0.01f) { v.cell = c; break; }
        }
    }

    void MakeVoices()
    {
        for (int i = 0; i < LoopVoices; i++)
        {
            var go = new GameObject("White Cell Loop " + i);
            go.transform.SetParent(transform, false);
            _voices[i] = new Voice { presence = Loop(go, s_presence, i), strain = Loop(go, s_strain, i + 7) };
        }
    }

    AudioSource Loop(GameObject go, AudioClip clip, int seed)
    {
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = true;
        s.playOnAwake = false;
        s.spatialBlend = 1f;
        s.rolloffMode = AudioRolloffMode.Linear;
        s.minDistance = 4f;
        s.maxDistance = presenceRange + 20f;
        s.dopplerLevel = 0f;
        s.volume = 0f;
        s.timeSamples = (int)((seed * 0.37f % 1f) * clip.samples); // the voices don't churn in step
        s.Play();
        return s;
    }

    // ---------------- clips ----------------

    static float[] BuildNotice()
    {
        float[] buf = Synth.Stereo(3.2f);
        float[] notes = { Synth.Midi(50), Synth.Midi(56) }; // D3 + G#3
        int count = Synth.Samples(2f);
        Synth.Biquad soft = default;
        soft.Lowpass(700f, 0.7f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.7f) * Mathf.Exp(-Mathf.Max(0f, t - 0.7f) * 2.2f) * 0.18f;
            float v = 0f;
            for (int n = 0; n < notes.Length; n++)
                v += Mathf.Sin(2f * Mathf.PI * notes[n] * t + Mathf.Sin(t * 1.7f + n) * 0.25f) * (n == 0 ? 1f : 0.7f); // watery wobble
            v = soft.Process(v * env);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Swoosh(buf, 0.05f, 1.4f, 260f, 520f, 0.08f, 0.9f, 57);
        Synth.Reverb(buf, 0.45f, 0.86f, 0.45f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    static float[] BuildGulp()
    {
        float[] buf = Synth.Stereo(2.2f);
        Synth.Glide(buf, 0f, 0.5f, 150f, 62f, 0.02f, 6f, 1f);
        Synth.Swoosh(buf, 0f, 0.4f, 900f, 180f, 0.3f, 1f, 19); // the suck, muffled
        var rng = new Synth.Noise(733);
        for (int i = 0; i < 6; i++)
        {
            float at = 0.12f + i * 0.06f + rng.Range(0f, 0.04f);
            Synth.Bubble(buf, at, 260f * Mathf.Pow(2f, -i * 0.1f + rng.Range(0f, 0.3f)), rng.Range(0.025f, 0.05f), 0.18f * (1f - i / 8f), rng.Range(-0.5f, 0.5f));
        }
        Synth.Reverb(buf, 0.3f, 0.78f, 0.55f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }

    // One swallow while reeling: the big gulp's shape, shorter and softer.
    static float[] BuildSmallGulp()
    {
        float[] buf = Synth.Stereo(1.2f);
        Synth.Glide(buf, 0f, 0.28f, 110f, 75f, 0.03f, 13f, 0.45f);
        Synth.Swoosh(buf, 0f, 0.25f, 550f, 180f, 0.2f, 1f, 41);
        Synth.Bubble(buf, 0.09f, 190f, 0.035f, 0.14f, -0.3f);
        Synth.Bubble(buf, 0.15f, 160f, 0.04f, 0.1f, 0.3f);
        Synth.Reverb(buf, 0.25f, 0.75f, 0.55f);
        Synth.Normalize(buf, 0.75f);
        return buf;
    }

    // The suction lets go: a short elastic "thwup" gliding up, the arm whipping back (a falling wash), bubbles
    // rising out of the mouth, and a faint glassy glint of relief.
    static float[] BuildTear()
    {
        float[] buf = Synth.Stereo(2.4f);
        Synth.Glide(buf, 0f, 0.3f, 85f, 240f, 0.004f, 14f, 1f, 0.08f);
        Synth.Swoosh(buf, 0.02f, 0.45f, 1200f, 280f, 0.28f, 1f, 83);
        var rng = new Synth.Noise(311);
        for (int i = 0; i < 6; i++)
        {
            float u = i / 5f;
            Synth.Bubble(buf, 0.05f + 0.3f * u + rng.Range(0f, 0.03f), Mathf.Lerp(200f, 520f, u) * rng.Range(0.9f, 1.1f),
                         rng.Range(0.03f, 0.05f), 0.14f * (1f - u * 0.5f), rng.Range(-0.6f, 0.6f));
        }
        Synth.Shimmer(buf, 0.14f, 1319f, 0.03f, 0.6f, -0.25f); // E6
        Synth.Shimmer(buf, 0.24f, 1976f, 0.018f, 0.5f, 0.25f); // B6
        Synth.Reverb(buf, 0.4f, 0.82f, 0.45f);
        Synth.Normalize(buf, 0.75f);
        return buf;
    }

    // A drop into a pool: a plop falling fast into a low tone, a muffled thump, then the jelly's deep wobble.
    static float[] BuildMerge()
    {
        float[] buf = Synth.Stereo(2f);
        Synth.Glide(buf, 0f, 0.45f, 230f, 88f, 0.003f, 9f, 1f, 0.06f);

        Synth.Biquad thump = default;
        thump.Lowpass(260f, 0.7f);
        var n = new Synth.Noise(97);
        int count = Synth.Samples(0.12f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float v = thump.Process(n.Next()) * Mathf.Min(1f, t * 400f) * Mathf.Exp(-t * 35f) * 5f;
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }

        double phase = 0;
        int from = Synth.Samples(0.02f);
        count = Synth.Samples(1.2f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            phase += 2.0 * System.Math.PI * 48.0 * (1.0 + 0.04 * System.Math.Sin(2.0 * System.Math.PI * 7.0 * t)) / Synth.Rate;
            float v = (float)System.Math.Sin(phase) * Mathf.Min(1f, t * 60f) * Mathf.Exp(-t * 3.5f) * 0.55f;
            int k = (from + i) * 2;
            buf[k] += v;
            buf[k + 1] += v;
        }

        var rng = new Synth.Noise(505);
        for (int i = 0; i < 4; i++)
            Synth.Bubble(buf, 0.1f + i * 0.08f + rng.Range(0f, 0.04f), rng.Range(120f, 190f), rng.Range(0.04f, 0.06f), 0.12f, rng.Range(-0.5f, 0.5f));
        Synth.Reverb(buf, 0.35f, 0.8f, 0.55f);
        Synth.Normalize(buf, 0.8f);
        return buf;
    }

    // Sinking away: a wash falling into the deep, bubbles sinking lower and fainter, and a dark tritone
    // (A3 + D#4, muffled) swelling under it and fading.
    static float[] BuildAbsorb()
    {
        float[] buf = Synth.Stereo(4f);
        Synth.Swoosh(buf, 0f, 1.8f, 700f, 140f, 0.25f, 0.9f, 29);
        var rng = new Synth.Noise(1201);
        for (int i = 0; i < 10; i++)
        {
            float u = i / 9f;
            Synth.Bubble(buf, 0.1f + 1.4f * u + rng.Range(0f, 0.06f), Mathf.Lerp(320f, 110f, u) * rng.Range(0.9f, 1.1f),
                         rng.Range(0.04f, 0.07f), 0.14f * (1f - u * 0.7f), rng.Range(-0.6f, 0.6f));
        }

        Synth.Biquad soft = default;
        soft.Lowpass(600f, 0.7f);
        float a = Synth.Midi(57), b = Synth.Midi(63);
        int count = Synth.Samples(2.6f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.6f) * Mathf.Exp(-Mathf.Max(0f, t - 0.6f) * 1.8f) * 0.1f;
            float v = Mathf.Sin(2f * Mathf.PI * a * t + Mathf.Sin(t * 2.3f) * 0.5f) + 0.7f * Mathf.Sin(2f * Mathf.PI * b * t + Mathf.Sin(t * 2.9f + 1f) * 0.5f);
            v = soft.Process(v * env);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Reverb(buf, 0.5f, 0.86f, 0.45f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Back again: a wash rising out of the deep, bubbles rising brighter, and the focus sound's faint shimmer.
    static float[] BuildRespawn()
    {
        float[] buf = Synth.Stereo(3f);
        Synth.Swoosh(buf, 0f, 1f, 250f, 1400f, 0.18f, 0.9f, 61);
        var rng = new Synth.Noise(4242);
        for (int i = 0; i < 8; i++)
        {
            float u = i / 7f;
            Synth.Bubble(buf, 0.08f + 0.6f * u + rng.Range(0f, 0.04f), Mathf.Lerp(180f, 520f, u) * rng.Range(0.92f, 1.08f),
                         rng.Range(0.03f, 0.05f), 0.12f, rng.Range(-0.6f, 0.6f));
        }
        Synth.Shimmer(buf, 0.4f, 1175f, 0.035f, 0.8f, -0.3f); // D6
        Synth.Shimmer(buf, 0.55f, 1760f, 0.022f, 0.7f, 0.3f); // A6
        Synth.Reverb(buf, 0.45f, 0.84f, 0.45f);
        Synth.Normalize(buf, 0.65f);
        return buf;
    }

    // The amoeba's churn: low brown noise surging slowly, a breath every 4 s, a few deep blubs. Seamless, 8 s.
    static float[] BuildPresence()
    {
        const float Length = 8f, Fade = 1f;
        float[] buf = Synth.Stereo(Length + Fade + 0.5f);
        int count = buf.Length / 2;
        var nl = new Synth.Noise(17); var nr = new Synth.Noise(907); var nb = new Synth.Noise(55);
        Synth.Biquad ll = default, lr = default, breath = default;
        ll.Lowpass(260f, 0.7f);
        lr.Lowpass(275f, 0.7f);
        breath.Bandpass(420f, 0.7f);
        float bl = 0f, br = 0f;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            bl = (bl + 0.02f * nl.Next()) / 1.02f;
            br = (br + 0.02f * nr.Next()) / 1.02f;
            float surge = 0.55f + 0.25f * Mathf.Sin(2f * Mathf.PI * t / 4f) + 0.15f * Mathf.Sin(2f * Mathf.PI * t / 2.667f + 1.3f);
            float b = Mathf.Max(0f, Mathf.Sin(2f * Mathf.PI * t / 4f - 0.8f));
            float breathe = breath.Process(nb.Next()) * b * b * 0.9f;
            buf[i * 2] += ll.Process(bl) * surge * 6f + breathe;
            buf[i * 2 + 1] += lr.Process(br) * surge * 6f + breathe;
        }
        var rng = new Synth.Noise(808);
        for (int i = 0; i < 7; i++)
            Synth.Bubble(buf, rng.Range(0.2f, Length + Fade - 0.3f), rng.Range(85f, 150f), rng.Range(0.06f, 0.1f), 0.1f, rng.Range(-0.6f, 0.6f));
        Synth.Reverb(buf, 0.35f, 0.8f, 0.55f);
        Synth.Normalize(buf, 0.6f, false);
        return Synth.Loop(buf, Length, Fade);
    }

    // A rubbery stretch: two broad, dark bands drifting slowly, in slow irregular tugs, over a faint low moan
    // (brighter bands and quick tugs were grating). Seamless, 5 s.
    static float[] BuildStrain()
    {
        const float Length = 5f, Fade = 1f;
        float[] buf = Synth.Stereo(Length + Fade + 0.5f);
        int count = buf.Length / 2;
        var n1 = new Synth.Noise(71); var n2 = new Synth.Noise(313); var tug = new Synth.Noise(9);
        Synth.Biquad b1 = default, b2 = default, top = default;
        top.Lowpass(950f, 0.7f);
        float level = 0f, goal = 0.5f;
        double moan = 0;
        int nextTug = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate;
            if ((i & 15) == 0)
            {
                b1.Bandpass(300f * (1f + 0.12f * Mathf.Sin(2f * Mathf.PI * 0.2f * t)), 0.9f);
                b2.Bandpass(540f * (1f + 0.12f * Mathf.Sin(2f * Mathf.PI * 0.33f * t + 2f)), 0.9f);
            }
            if (i >= nextTug) // a new tug every 0.15-0.5 s, eased into
            {
                goal = tug.Range(0.45f, 1f);
                nextTug = i + Synth.Samples(tug.Range(0.35f, 0.9f));
            }
            level += (goal - level) * (1f / (0.25f * Synth.Rate));
            moan += 2.0 * System.Math.PI * 70.0 * (1.0 + 0.03 * System.Math.Sin(2.0 * System.Math.PI * 0.4 * t)) / Synth.Rate;
            float v = (b1.Process(n1.Next()) + 0.6f * b2.Process(n2.Next())) * level + (float)System.Math.Sin(moan) * 0.06f * level;
            v = top.Process(v);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
        Synth.Reverb(buf, 0.3f, 0.78f, 0.55f);
        Synth.Normalize(buf, 0.6f, false);
        return Synth.Loop(buf, Length, Fade);
    }
}

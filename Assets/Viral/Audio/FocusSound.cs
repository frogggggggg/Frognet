using UnityEngine;

/// <summary>
/// Focus mode's sound. On entering focus: a meaty, wet underwater injection (thump dropping in
/// pitch, a squelch, gurgle and bubbles, all muffled), then, as the sweep washes outward into the
/// blue, a soft underwater bloom: a broad wash swelling up, a stream of small bubbles rising with
/// it, and one faint glassy shimmer (an open fifth, slow attack, quiet). Leaving focus fades that
/// out and plays the wash falling back with a few bubbles. No piano, nothing sharp or tonal up front.
///
/// Follows a VirusMovement's focus events (the player's by default). The clips are synthesized once
/// at load (<see cref="Synth"/>, shared by every instance); assign recorded clips to replace any of
/// them. Creates itself on play if the scene has none. Cost: two 2D AudioSources, nothing per frame
/// except easing a fade.
/// </summary>
public class FocusSound : MonoBehaviour
{
    [Tooltip("Whose focus to follow. Empty: the player's (the VirusMovement without a VirusAI).")]
    public VirusMovement movement;

    [Header("Clips (empty: synthesized at load)")]
    public AudioClip inject;
    public AudioClip transition;
    public AudioClip exit;

    [Header("Mix")]
    [Range(0f, 1f)] public float injectVolume = 0.9f;
    [Range(0f, 1f)] public float transitionVolume = 0.6f;
    [Range(0f, 1f), Tooltip("0 = no sound on leaving focus.")] public float exitVolume = 0.4f;
    [Min(0f), Tooltip("Seconds after the injection before the transition starts (the sweep's swoosh outward).")]
    public float transitionDelay = 0.12f;
    [Min(0.01f), Tooltip("Leaving focus fades whatever is left of the transition over this long.")]
    public float fadeTime = 0.25f;

    static AudioClip s_inject, s_transition, s_exit;
    static bool s_resetTried;

    AudioSource _hits, _sweep;
    VirusMovement _hooked;
    bool _fading;

    AudioClip Inject => inject ? inject : s_inject;
    AudioClip Transition => transition ? transition : s_transition;
    AudioClip Exit => exit ? exit : s_exit;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<FocusSound>()) return; // the scene has its own, with its own settings
        new GameObject("Focus Sound").AddComponent<FocusSound>();
    }

    void OnEnable() => Hook();
    void Start() // too: a scene-placed one may enable before VirusAI disables its copies
    {
        Hook();
        Ready();
        if (!_hooked) Debug.LogWarning("FocusSound: no player VirusMovement found, so no focus sounds.", this);
    }

    // Why nothing would be heard (the project has no other sound to notice it by).
    void Diagnose()
    {
#if UNITY_EDITOR
        if (UnityEditor.EditorUtility.audioMasterMute) Debug.LogWarning("FocusSound: the Game view's audio is muted (Mute Audio button).", this);
#endif
        if (AudioListener.volume <= 0f || AudioListener.pause) Debug.LogWarning("FocusSound: AudioListener volume is 0 or paused.", this);
        var listener = FindAnyObjectByType<AudioListener>();
        if (!listener || !listener.isActiveAndEnabled) Debug.LogWarning("FocusSound: no active AudioListener in the scene.", this);
    }

    void OnDisable()
    {
        if (_hooked)
        {
            _hooked.onFocusModeEnter.RemoveListener(Enter);
            _hooked.onFocusModeExit.RemoveListener(Leave);
        }
        _hooked = null;
    }

    void Hook()
    {
        VirusMovement m = movement ? movement : CreatureAudio.FindPlayer();
        if (m == _hooked) return;
        OnDisable();
        if (!m) return;
        _hooked = m;
        m.onFocusModeEnter.AddListener(Enter);
        m.onFocusModeExit.AddListener(Leave);
    }

    void Ready()
    {
        if (!_hits) _hits = Source();
        if (!_sweep) _sweep = Source();
        if (!s_inject && !inject) s_inject = Synth.Clip("Focus Inject", BuildInject());
        if (!s_transition && !transition) s_transition = Synth.Clip("Focus Transition", BuildTransition());
        if (!s_exit && !exit) s_exit = Synth.Clip("Focus Exit", BuildExit());
    }

    // On this object itself (hidden DontSave children made no sound).
    AudioSource Source()
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f; // it's the player's own, screen-wide effect
        return s;
    }

    void Enter()
    {
        Ready();
        Diagnose();
        _fading = false;
        _hits.PlayOneShot(Inject, injectVolume);
        _sweep.Stop();
        _sweep.clip = Transition;
        _sweep.volume = transitionVolume;
        _sweep.PlayScheduled(AudioSettings.dspTime + transitionDelay);
        StartCoroutine(CheckDevice());
    }

    // Unity's audio device can stall (everything "plays", the mixer clock stands still): restart it once.
    System.Collections.IEnumerator CheckDevice()
    {
        if (s_resetTried) yield break;
        double dspStart = AudioSettings.dspTime;
        float realStart = Time.realtimeSinceStartup;
        for (float t = 0f; t < 0.5f; t += Time.unscaledDeltaTime) yield return null;
        if (AudioSettings.dspTime - dspStart > (Time.realtimeSinceStartup - realStart) * 0.5f) yield break;
        s_resetTried = true;
        Debug.LogWarning("FocusSound: Unity's audio device is stalled; restarting it (AudioSettings.Reset). " +
                         "If focus is still silent, restart the editor with your output device connected.", this);
        AudioSettings.Reset(AudioSettings.GetConfiguration());
    }

    void Leave()
    {
        Ready();
        _fading = _sweep.isPlaying;
        if (exitVolume > 0f) _hits.PlayOneShot(Exit, exitVolume);
    }

    void Update()
    {
        if (!_fading || !_sweep) return;
        _sweep.volume = Mathf.MoveTowards(_sweep.volume, 0f, transitionVolume * Time.unscaledDeltaTime / fadeTime);
        if (_sweep.volume > 0f) return;
        _sweep.Stop();
        _fading = false;
    }

    // ---------------- synthesis ----------------

    static float[] BuildInject()
    {
        float[] buf = Synth.Stereo(1.4f);
        var noise = new Synth.Noise(11);
        Synth.Biquad squelch = default, gurgle = default, water = default;
        gurgle.Lowpass(260f, 0.9f);
        water.Lowpass(1100f, 0.8f); // everything heard through water
        double phase = 0;

        int body = Synth.Samples(1f);
        for (int i = 0; i < body; i++)
        {
            float t = i / (float)Synth.Rate;
            // Thump: a pitch drop, saturated for weight.
            phase += 2.0 * System.Math.PI * (40f + 115f * Mathf.Exp(-t * 13f)) / Synth.Rate;
            float thump = (float)System.Math.Tanh(System.Math.Sin(phase) * Mathf.Exp(-t * 6f) * Mathf.Min(1f, t * 300f) * 2.5f) * 0.7f;
            // Squelch: resonant noise sliding down, with a wobble.
            if ((i & 15) == 0) squelch.Bandpass(170f + 1300f * Mathf.Exp(-t * 8f) * (1f + 0.2f * Mathf.Sin(2f * Mathf.PI * 19f * t)), 5f);
            float n = noise.Next();
            float sq = squelch.Process(n) * Mathf.Exp(-t * 7f) * Mathf.Min(1f, t * 120f) * 2.2f;
            // Gurgle: low noise, lumpy.
            float lump = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 7f * t + 3f * Mathf.Sin(2f * Mathf.PI * 2.3f * t));
            float gu = gurgle.Process(n) * lump * Mathf.Exp(-t * 3.5f) * Mathf.Min(1f, t * 20f) * 1.5f;
            float v = water.Process(thump + sq + gu);
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }

        for (int b = 0; b < 9; b++)
        {
            float at = 0.04f + noise.Range(0f, 0.55f);
            Synth.Bubble(buf, at, noise.Range(280f, 800f), noise.Range(0.04f, 0.11f), 0.3f * (1f - at), noise.Range(-0.6f, 0.6f));
        }

        Synth.Reverb(buf, 0.3f, 0.7f, 0.6f);
        Synth.Normalize(buf);
        return buf;
    }

    // The sweep washing outward: a broad band of noise swelling up (300 Hz -> 1.6 kHz, broad, so it
    // reads as water moving, not a whoosh of air), a stream of small bubbles rising in pitch and
    // thinning out as it spreads, and a faint glassy shimmer (D6 + A6, each a slightly detuned pair,
    // slow attack, quiet) under a big soft reverb. The piano gestures it replaces never settled
    // (too positive, too much flourish, too long; see CLAUDE.md).
    static float[] BuildTransition()
    {
        float[] buf = Synth.Stereo(2.2f);
        Synth.Swoosh(buf, 0f, 0.9f, 300f, 1600f, 0.09f, 0.8f, 5);
        var rng = new Synth.Noise(71);
        for (int b = 0; b < 16; b++)
        {
            float u = b / 15f, at = 0.05f + 0.7f * u * u + rng.Range(0f, 0.04f);
            Synth.Bubble(buf, at, 300f + 700f * u + rng.Range(-40f, 40f), rng.Range(0.03f, 0.06f), 0.16f * (1f - 0.6f * u), rng.Range(-0.7f, 0.7f));
        }
        Synth.Shimmer(buf, 0.15f, 1175f, 0.05f, 0.9f, -0.3f);
        Synth.Shimmer(buf, 0.25f, 1760f, 0.035f, 0.8f, 0.3f);
        Synth.Reverb(buf, 0.3f, 0.8f, 0.5f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Back out: the wash falling (1.4 kHz -> 300 Hz), shorter, a few bubbles sinking, and the lower
    // shimmer note fading in and away.
    static float[] BuildExit()
    {
        float[] buf = Synth.Stereo(1.5f);
        Synth.Swoosh(buf, 0f, 0.55f, 1400f, 300f, 0.07f, 0.8f, 9);
        var rng = new Synth.Noise(83);
        for (int b = 0; b < 6; b++)
        {
            float u = b / 5f;
            Synth.Bubble(buf, 0.03f + 0.3f * u + rng.Range(0f, 0.03f), 700f - 350f * u, rng.Range(0.03f, 0.05f), 0.12f, rng.Range(-0.6f, 0.6f));
        }
        Synth.Shimmer(buf, 0.02f, 880f, 0.035f, 0.5f, 0f);
        Synth.Reverb(buf, 0.25f, 0.75f, 0.5f);
        Synth.Normalize(buf, 0.55f);
        return buf;
    }
}

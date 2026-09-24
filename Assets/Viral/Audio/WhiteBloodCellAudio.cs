using UnityEngine;

/// <summary>
/// Sounds for white blood cells (WhiteBloodCells' events, so the cells have no audio code):
/// - Noticed (only when it's the player being hunted): a soft, low swell, two muffled sines a tritone
///   apart under a breath of wet noise. Tension from the interval, not loudness.
/// - Engulfing: a deep wet gulp, a low tone sagging down, a muffled suck of noise and a few bubbles
///   sinking, in a soft reverb.
/// Creates itself on play. Clips are built once at load.
/// </summary>
public class WhiteBloodCellAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float noticeVolume = 0.35f;
    [Range(0f, 1f)] public float gulpVolume = 0.7f;
    [Min(1f)] public float range = 80f;

    static AudioClip s_notice, s_gulp;

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
    }

    void OnDisable()
    {
        WhiteBloodCells.Noticed -= OnNoticed;
        WhiteBloodCells.Engulfing -= OnEngulfing;
    }

    void Ready()
    {
        if (!s_notice) s_notice = Synth.Clip("White Cell Notice", BuildNotice(), true);
        if (!s_gulp) s_gulp = Synth.Clip("White Cell Gulp", BuildGulp(), true);
    }

    void OnNoticed(WhiteBloodCell cell, Organism prey)
    {
        if (!prey || !prey.GetComponent<VirusMovement>()) return;
        Ready();
        Sfx.Play(s_notice, cell.Spot, noticeVolume, Random.Range(0.95f, 1.05f), range);
    }

    void OnEngulfing(WhiteBloodCell cell, Organism prey)
    {
        Ready();
        Sfx.Play(s_gulp, prey ? prey.transform.position : cell.Spot, gulpVolume, Random.Range(0.9f, 1.05f), range);
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
                v += Mathf.Sin(2f * Mathf.PI * notes[n] * t + Mathf.Sin(t * 3.1f + n) * 0.6f) * (n == 0 ? 1f : 0.7f); // watery wobble
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
        double phase = 0;
        int count = Synth.Samples(0.5f);
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Synth.Rate, u = t / 0.5f;
            phase += 2.0 * System.Math.PI * Mathf.Lerp(150f, 62f, Mathf.SmoothStep(0f, 1f, u)) / Synth.Rate;
            float env = Mathf.SmoothStep(0f, 1f, t / 0.02f) * Mathf.Exp(-t * 6f);
            float v = (float)System.Math.Sin(phase) * env;
            buf[i * 2] += v;
            buf[i * 2 + 1] += v;
        }
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
}

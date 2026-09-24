using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Real piano notes for <see cref="Synth"/> buffers: recordings of a grand piano (Salamander Grand,
/// CC-BY, see Resources/Piano/CREDITS.txt) in Resources/Piano, named by note (C5, Ds5, Fs5, A5: a
/// sample every three semitones). A note plays the nearest sample, resampled at most a semitone and
/// a half, so it keeps the real attack, strings and body. Loaded once, on first use.
/// Add samples (same naming) to cover more of the keyboard.
/// </summary>
public static class Piano
{
    struct Sample
    {
        public int note;
        public float[] left, right;
        public int rate;
    }

    static Sample[] s_samples;

    public static bool Loaded => Load() > 0;

    /// <summary>
    /// Adds MIDI note 'note' at 'start' seconds. gain ~1 = as recorded; pan -1..1. After 'hold'
    /// seconds the key is let go and the damper mutes it over ~0.15 s. Samples are trimmed to their
    /// onset, so 'start' is when the note is heard.
    /// </summary>
    public static void Note(float[] buf, float start, float note, float gain, float pan, float hold = 10f)
    {
        if (Load() == 0) return;
        Sample s = s_samples[0];
        foreach (Sample c in s_samples)
            if (Mathf.Abs(c.note - note) < Mathf.Abs(s.note - note)) s = c;

        double step = Mathf.Pow(2f, (note - s.note) / 12f) * s.rate / Synth.Rate;
        int from = Synth.Samples(start), frames = buf.Length / 2, holdAt = Synth.Samples(hold);
        float l = gain * Mathf.Sqrt(1f - pan), r = gain * Mathf.Sqrt(1f + pan); // unity at centre
        float damper = 1f, fall = Mathf.Exp(-25f / Synth.Rate);
        double pos = 0;
        for (int i = from; i < frames; i++, pos += step)
        {
            int j = (int)pos;
            if (j + 1 >= s.left.Length || damper < 1e-4f) break;
            float t = (float)(pos - j);
            float a = s.left[j] + (s.left[j + 1] - s.left[j]) * t;
            float b = s.right[j] + (s.right[j + 1] - s.right[j]) * t;
            if (i - from > holdAt) damper *= fall;
            buf[i * 2] += a * l * damper;
            buf[i * 2 + 1] += b * r * damper;
        }
    }

    static int Load()
    {
        if (s_samples != null) return s_samples.Length;
        var list = new List<Sample>();
        foreach (AudioClip clip in Resources.LoadAll<AudioClip>("Piano"))
        {
            if (!TryParse(clip.name, out int note)) continue;
            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
            {
                Debug.LogWarning($"Piano: can't read '{clip.name}'; set its Load Type to Decompress On Load.", clip);
                continue;
            }
            int onset = Onset(data, clip.channels, clip.frequency), length = clip.samples - onset;
            var sample = new Sample { note = note, rate = clip.frequency, left = new float[length], right = new float[length] };
            for (int i = 0; i < length; i++)
            {
                int k = (onset + i) * clip.channels;
                sample.left[i] = data[k];
                sample.right[i] = data[k + (clip.channels > 1 ? 1 : 0)];
            }
            list.Add(sample);
        }
        if (list.Count == 0) Debug.LogWarning("Piano: no samples in Resources/Piano; piano notes will be silent.");
        s_samples = list.ToArray();
        return s_samples.Length;
    }

    // Where the hammer lands: MP3s start with a little silence (encoder delay + the recording's own),
    // different per file, which put notes late by different amounts and made rhythms feel off.
    // First frame over 5% of the attack's peak, backed off 1 ms to keep the transient whole.
    static int Onset(float[] data, int channels, int rate)
    {
        int frames = data.Length / channels, scan = Mathf.Min(frames, rate / 2);
        float peak = 0f;
        for (int i = 0; i < scan * channels; i++) peak = Mathf.Max(peak, Mathf.Abs(data[i]));
        float threshold = peak * 0.05f;
        for (int i = 0; i < scan; i++)
            for (int c = 0; c < channels; c++)
                if (Mathf.Abs(data[i * channels + c]) > threshold) return Mathf.Max(0, i - rate / 1000);
        return 0;
    }

    // "C5", "Ds5", "Fs5", "A5" -> MIDI (C4 = 60).
    static bool TryParse(string name, out int note)
    {
        note = 0;
        if (name.Length < 2) return false;
        int pc = "C D EF G A B".IndexOf(char.ToUpperInvariant(name[0]));
        if (pc < 0 || name[0] == ' ') return false;
        int i = 1;
        if (name[i] == 's' || name[i] == '#') { pc++; i++; }
        else if (name[i] == 'b') { pc--; i++; }
        if (!int.TryParse(name.Substring(i), out int octave)) return false;
        note = 12 * (octave + 1) + pc;
        return true;
    }
}

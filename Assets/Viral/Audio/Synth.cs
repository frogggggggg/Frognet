using System;
using UnityEngine;

/// <summary>
/// Tiny offline synth: builds sound effects into float buffers once (at load), then hands them to
/// Unity as AudioClips. No runtime DSP, no assets. Buffers are interleaved stereo at <see cref="Rate"/>.
/// Pieces: seeded noise, RBJ biquads (sweepable), swooshes, bubbles, a Freeverb-style reverb,
/// normalise, loops. Real piano notes come from <see cref="Piano"/> (recorded samples). The sound direction (Breath of the Wild / Spore: sparse airy piano, glassy
/// shimmer, soft wet organics) lives in CLAUDE.md > Sound; build new effects from these parts.
/// </summary>
public static class Synth
{
    public const int Rate = 44100;

    public static int Samples(float seconds) => Mathf.CeilToInt(seconds * Rate);
    public static float[] Stereo(float seconds) => new float[Samples(seconds) * 2];
    public static float Midi(float note) => 440f * Mathf.Pow(2f, (note - 69f) / 12f);

    /// <summary>Deterministic white noise in [-1, 1] (xorshift), so every build sounds the same.</summary>
    public struct Noise
    {
        uint _s;
        public Noise(uint seed) { _s = seed == 0 ? 0x9E3779B9u : seed; }
        public float Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (_s & 0xFFFFFF) / (float)0x800000 - 1f;
        }
        public float Range(float a, float b) => a + (Next() * 0.5f + 0.5f) * (b - a);
    }

    /// <summary>RBJ biquad. Set the shape each sample (or every few) to sweep it.</summary>
    public struct Biquad
    {
        float _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public void Lowpass(float freq, float q)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, Rate * 0.45f) / Rate;
            float c = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q), a0 = 1f + alpha;
            _b0 = (1f - c) * 0.5f / a0; _b1 = (1f - c) / a0; _b2 = _b0;
            _a1 = -2f * c / a0; _a2 = (1f - alpha) / a0;
        }

        public void Highpass(float freq, float q)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, Rate * 0.45f) / Rate;
            float c = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q), a0 = 1f + alpha;
            _b0 = (1f + c) * 0.5f / a0; _b1 = -(1f + c) / a0; _b2 = _b0;
            _a1 = -2f * c / a0; _a2 = (1f - alpha) / a0;
        }

        /// <summary>Band-pass with 0 dB at the centre.</summary>
        public void Bandpass(float freq, float q)
        {
            float w = 2f * Mathf.PI * Mathf.Clamp(freq, 10f, Rate * 0.45f) / Rate;
            float c = Mathf.Cos(w), alpha = Mathf.Sin(w) / (2f * q), a0 = 1f + alpha;
            _b0 = alpha / a0; _b1 = 0f; _b2 = -alpha / a0;
            _a1 = -2f * c / a0; _a2 = (1f - alpha) / a0;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    /// <summary>
    /// Adds a band of noise swept from one frequency to another (log, smoothstepped), swelling to
    /// its loudest a third of the way in: the swoosh. Each side gets its own noise, so it's wide.
    /// </summary>
    public static void Swoosh(float[] buf, float start, float length, float fromHz, float toHz, float gain, float q = 1.4f, uint seed = 3)
    {
        int from = Samples(start), count = Mathf.Min(buf.Length / 2 - from, Samples(length));
        var nl = new Noise(seed); var nr = new Noise(seed * 31u + 7u);
        Biquad bl = default, br = default;
        for (int i = 0; i < count; i++)
        {
            float u = i / (float)count;
            if ((i & 15) == 0)
            {
                float f = fromHz * Mathf.Pow(toHz / fromHz, Mathf.SmoothStep(0f, 1f, u));
                bl.Bandpass(f, q);
                br.Bandpass(f * 1.07f, q);
            }
            float s = Mathf.Sin(Mathf.PI * Mathf.Pow(u, 0.6f));
            float env = gain * s * s;
            int k = (from + i) * 2;
            buf[k] += bl.Process(nl.Next()) * env;
            buf[k + 1] += br.Process(nr.Next()) * env;
        }
    }

    /// <summary>Adds one underwater bubble: a sine whose pitch rises as it dies away.</summary>
    public static void Bubble(float[] buf, float start, float freq, float length, float gain, float pan)
    {
        int from = Samples(start), count = Mathf.Min(buf.Length / 2 - from, Samples(length * 3f));
        float left = gain * Mathf.Sqrt(0.5f * (1f - pan)), right = gain * Mathf.Sqrt(0.5f * (1f + pan));
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            phase += 2.0 * Math.PI * freq * (1f + 1.8f * t / length) / Rate;
            float v = (float)Math.Sin(phase) * Mathf.Exp(-t / (length * 0.45f)) * Mathf.Min(1f, t * 600f);
            int k = (from + i) * 2;
            buf[k] += v * left;
            buf[k + 1] += v * right;
        }
    }

    /// <summary>A sine gliding from one pitch to another over 'glide' seconds (smoothstepped; 0 = the whole length),
    /// with an 'attack' s fade-in and an exponential decay (per second).</summary>
    public static void Glide(float[] buf, float start, float length, float fromHz, float toHz, float attack, float decay, float gain, float glide = 0f)
    {
        if (glide <= 0f) glide = length;
        int from = Samples(start), count = Mathf.Min(buf.Length / 2 - from, Samples(length));
        double phase = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            phase += 2.0 * Math.PI * Mathf.Lerp(fromHz, toHz, Mathf.SmoothStep(0f, 1f, t / glide)) / Rate;
            float v = (float)Math.Sin(phase) * Mathf.SmoothStep(0f, 1f, t / attack) * Mathf.Exp(-t * decay) * gain;
            int k = (from + i) * 2;
            buf[k] += v;
            buf[k + 1] += v;
        }
    }

    /// <summary>A soft glassy tone: two sines 0.2% apart (a slow shimmer between them), eased in over 0.12 s
    /// and dying away over `decay` seconds, so it has no attack to hear as a note or a zap.</summary>
    public static void Shimmer(float[] buf, float start, float freq, float gain, float decay, float pan)
    {
        int from = Samples(start), count = Mathf.Min(buf.Length / 2 - from, Samples(decay * 4f));
        float left = gain * Mathf.Sqrt(0.5f * (1f - pan)), right = gain * Mathf.Sqrt(0.5f * (1f + pan));
        double a = 0, b = 0;
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            a += 2.0 * System.Math.PI * freq * 0.999 / Rate;
            b += 2.0 * System.Math.PI * freq * 1.001 / Rate;
            float v = (float)(System.Math.Sin(a) + System.Math.Sin(b)) * 0.5f * Mathf.SmoothStep(0f, 1f, t / 0.12f) * Mathf.Exp(-t / decay);
            int k = (from + i) * 2;
            buf[k] += v * left;
            buf[k + 1] += v * right;
        }
    }

    /// <summary>A short burst of band-passed noise with a fast attack and exponential decay (a tick, a scrape grain).</summary>
    public static void Grain(float[] buf, float start, float hz, float q, float length, float gain, float pan, uint seed)
    {
        int from = Samples(start), count = Mathf.Min(buf.Length / 2 - from, Samples(length * 4f));
        if (count <= 0) return;
        var rng = new Noise(seed);
        Biquad band = default;
        band.Bandpass(hz, q);
        float left = gain * Mathf.Sqrt(0.5f * (1f - pan)), right = gain * Mathf.Sqrt(0.5f * (1f + pan));
        for (int i = 0; i < count; i++)
        {
            float t = i / (float)Rate;
            float v = band.Process(rng.Next()) * Mathf.Min(1f, t * 2000f) * Mathf.Exp(-t / length);
            int k = (from + i) * 2;
            buf[k] += v * left;
            buf[k + 1] += v * right;
        }
    }

    /// <summary>Low-passes a stereo buffer in place.</summary>
    public static void Lowpass(float[] buf, float hz)
    {
        Biquad l = default, r = default;
        l.Lowpass(hz, 0.7f);
        r.Lowpass(hz, 0.7f);
        for (int i = 0; i < buf.Length; i += 2)
        {
            buf[i] = l.Process(buf[i]);
            buf[i + 1] = r.Process(buf[i + 1]);
        }
    }

    /// <summary>
    /// Freeverb-style stereo reverb, in place: 8 damped combs + 4 allpasses per side (the right
    /// side's delays spread a little for width). Leave silence at the end of the buffer for the tail.
    /// </summary>
    public static void Reverb(float[] buf, float wet, float room = 0.84f, float damp = 0.3f)
    {
        int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        int[] passes = { 556, 441, 341, 225 };
        const int Spread = 23;
        int frames = buf.Length / 2;
        var outs = new float[buf.Length];

        for (int ch = 0; ch < 2; ch++)
        {
            int spread = ch * Spread;
            var combBuf = new float[combs.Length][];
            var combIdx = new int[combs.Length];
            var combStore = new float[combs.Length];
            for (int j = 0; j < combs.Length; j++) combBuf[j] = new float[combs[j] + spread];
            var passBuf = new float[passes.Length][];
            var passIdx = new int[passes.Length];
            for (int j = 0; j < passes.Length; j++) passBuf[j] = new float[passes[j] + spread];

            for (int i = 0; i < frames; i++)
            {
                float input = (buf[i * 2] + buf[i * 2 + 1]) * 0.015f, o = 0f;
                for (int j = 0; j < combs.Length; j++)
                {
                    float[] b = combBuf[j];
                    float y = b[combIdx[j]];
                    combStore[j] = y * (1f - damp) + combStore[j] * damp;
                    b[combIdx[j]] = input + combStore[j] * room;
                    if (++combIdx[j] >= b.Length) combIdx[j] = 0;
                    o += y;
                }
                for (int j = 0; j < passes.Length; j++)
                {
                    float[] b = passBuf[j];
                    float y = b[passIdx[j]];
                    b[passIdx[j]] = o + y * 0.5f;
                    if (++passIdx[j] >= b.Length) passIdx[j] = 0;
                    o = y - o;
                }
                outs[i * 2 + ch] = o;
            }
        }
        for (int i = 0; i < buf.Length; i++) buf[i] = buf[i] * (1f - wet * 0.5f) + outs[i] * wet * 3f;
    }

    /// <summary>Scale so the loudest sample is 'peak', and (unless it loops) fade the last few ms to silence (no click).</summary>
    public static void Normalize(float[] buf, float peak = 0.85f, bool fadeEnd = true)
    {
        float max = 1e-6f;
        for (int i = 0; i < buf.Length; i++) max = Mathf.Max(max, Mathf.Abs(buf[i]));
        float k = peak / max;
        int fade = fadeEnd ? Mathf.Min(buf.Length / 2, Samples(0.03f)) : 0;
        for (int i = 0; i < buf.Length; i++)
        {
            int toEnd = buf.Length / 2 - 1 - i / 2;
            buf[i] *= k * (toEnd < fade ? toEnd / (float)fade : 1f);
        }
    }

    /// <summary>
    /// A seamless loop 'seconds' long from a stereo buffer at least 'seconds + crossfade' long:
    /// the tail past the loop point is faded (equal power) into the start.
    /// </summary>
    public static float[] Loop(float[] buf, float seconds, float crossfade)
    {
        int length = Samples(seconds), fade = Mathf.Min(Samples(crossfade), buf.Length / 2 - length); // rounding can ask for one past the end
        var loop = new float[length * 2];
        for (int i = 0; i < length; i++)
        {
            float w = i < fade ? i / (float)fade : 1f, a = Mathf.Sqrt(w), b = Mathf.Sqrt(1f - w);
            for (int ch = 0; ch < 2; ch++)
                loop[i * 2 + ch] = buf[i * 2 + ch] * a + (i < fade ? buf[(length + i) * 2 + ch] * b : 0f);
        }
        return loop;
    }

    /// <summary>A clip from a stereo buffer; mono (sides averaged) for 3D sounds.</summary>
    public static AudioClip Clip(string name, float[] stereo, bool mono = false)
    {
        int frames = stereo.Length / 2;
        var clip = AudioClip.Create(name, frames, mono ? 1 : 2, Rate, false);
        if (!mono) { clip.SetData(stereo, 0); return clip; }
        var data = new float[frames];
        for (int i = 0; i < frames; i++) data[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;
        clip.SetData(data, 0);
        return clip;
    }
}

using UnityEngine;

/// <summary>
/// Sounds for the head view's synthesizer (GenomeView.Synthesis, so the view has no audio code):
/// - Menu open / close: a small rising glassy arpeggio (D6 E6 A6) over a soft bubble, and the same falling back.
/// - Pointing at a recipe: a tiny soft pluck, its pitch from the recipe (a pentatonic step), so each has its note.
/// - Refused (can't make it): a dull muffled "bup-bup" falling a tritone, nothing buzzy.
/// - Page turn: a short wet swish.
/// - Started: the nozzle engaging, a suction "plup". Docked on a store: a small wet latch click.
/// - Drawing (loop while GenomeView.Drawing): sucking through a straw, gulping bandpassed noise + low bubbles.
/// - Dispensing: a squeeze rising as the strand is pushed out; Made: a round bloo + a soft two-note shimmer
///   (one of a few pentatonic pairs, picked by the recipe).
/// All 2D (the player's own head view). Creates itself on play; clips are built once at load.
/// Cost: three 2D sources, nothing per creature.
/// </summary>
public class CraftAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float menuVolume = 0.28f;
    [Range(0f, 1f)] public float pointVolume = 0.12f;
    [Range(0f, 1f)] public float refuseVolume = 0.3f;
    [Range(0f, 1f)] public float pageVolume = 0.2f;
    [Range(0f, 1f)] public float startVolume = 0.35f;
    [Range(0f, 1f)] public float dockVolume = 0.22f;
    [Range(0f, 1f)] public float drawVolume = 0.22f;
    [Range(0f, 1f)] public float dispenseVolume = 0.3f;
    [Range(0f, 1f)] public float madeVolume = 0.35f;

    static readonly float[] Steps = { 1f, 1.125f, 1.25f, 1.5f, 1.6667f, 2f }; // pentatonic, for the pluck
    static readonly Vector2[] MadeNotes = { new Vector2(81, 88), new Vector2(86, 93), new Vector2(79, 86) }; // A5 E6, D6 A6, G5 D6

    static AudioClip s_open, s_close, s_point, s_refuse, s_page, s_start, s_dock, s_draw, s_dispense;
    static AudioClip[] s_made;
    AudioSource _hits, _plucks, _draw;
    float _pointAt = -10f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<CraftAudio>()) return;
        new GameObject("Craft Audio").AddComponent<CraftAudio>();
    }

    void OnEnable() => GenomeView.Synthesis += OnStage;
    void OnDisable() => GenomeView.Synthesis -= OnStage;

    void Ready()
    {
        if (!s_open) s_open = Synth.Clip("Craft Menu Open", BuildArp(new[] { 86f, 88f, 93f }));
        if (!s_close) s_close = Synth.Clip("Craft Menu Close", BuildArp(new[] { 93f, 88f, 86f }));
        if (!s_point) s_point = Synth.Clip("Craft Point", BuildPluck());
        if (!s_refuse) s_refuse = Synth.Clip("Craft Refuse", BuildRefuse());
        if (!s_page) s_page = Synth.Clip("Craft Page", BuildPage());
        if (!s_start) s_start = Synth.Clip("Craft Start", BuildStart());
        if (!s_dock) s_dock = Synth.Clip("Craft Dock", BuildDock());
        if (!s_draw) s_draw = Synth.Clip("Craft Draw", BuildDraw());
        if (!s_dispense) s_dispense = Synth.Clip("Craft Dispense", BuildDispense());
        if (s_made == null || !s_made[0])
        {
            s_made = new AudioClip[MadeNotes.Length];
            for (int i = 0; i < MadeNotes.Length; i++) s_made[i] = Synth.Clip("Craft Made " + i, BuildMade(MadeNotes[i]));
        }
        if (!_hits) _hits = Source(gameObject, null, false);
        if (!_plucks) _plucks = Source(new GameObject("Craft Plucks"), s_point, false);
        if (!_draw) _draw = Source(new GameObject("Craft Draw"), s_draw, true);
    }

    AudioSource Source(GameObject go, AudioClip clip, bool loop)
    {
        if (go != gameObject) go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        s.clip = clip;
        s.loop = loop;
        s.volume = loop ? 0f : 1f;
        return s;
    }

    static int Hash(Crafting.Recipe r) => r == null || string.IsNullOrEmpty(r.name) ? 0 : r.name.GetHashCode() & 0x7fffffff;

    void OnStage(GenomeView.CraftStage stage, Crafting.Recipe r)
    {
        Ready();
        switch (stage)
        {
            case GenomeView.CraftStage.MenuOpened: _hits.PlayOneShot(s_open, menuVolume); break;
            case GenomeView.CraftStage.MenuClosed: _hits.PlayOneShot(s_close, menuVolume * 0.7f); break;
            case GenomeView.CraftStage.Pointed:
                if (Time.unscaledTime - _pointAt < 0.04f) break; // sweeping across a ring: not a rattle
                _pointAt = Time.unscaledTime;
                _plucks.pitch = Steps[Hash(r) % Steps.Length];
                _plucks.PlayOneShot(s_point, pointVolume);
                break;
            case GenomeView.CraftStage.Refused: _hits.PlayOneShot(s_refuse, refuseVolume); break;
            case GenomeView.CraftStage.PageTurned: _hits.PlayOneShot(s_page, pageVolume); break;
            case GenomeView.CraftStage.Started: _hits.PlayOneShot(s_start, startVolume); break;
            case GenomeView.CraftStage.Docked:
                _draw.pitch = Random.Range(0.92f, 1.08f); // each store's draw a little different
                _hits.PlayOneShot(s_dock, dockVolume);
                break;
            case GenomeView.CraftStage.Dispensing: _hits.PlayOneShot(s_dispense, dispenseVolume); break;
            case GenomeView.CraftStage.Made: _hits.PlayOneShot(s_made[Hash(r) % s_made.Length], madeVolume); break;
        }
    }

    void Update()
    {
        if (!_draw && !GenomeView.Drawing) return;
        Ready();
        float target = GenomeView.Drawing ? drawVolume : 0f;
        _draw.volume = Mathf.MoveTowards(_draw.volume, target, Time.unscaledDeltaTime * (target > _draw.volume ? 3f : 4f));
        if (_draw.volume > 0f) { if (!_draw.isPlaying) _draw.Play(); }
        else if (_draw.isPlaying) _draw.Stop();
    }

    // ---------------- clips ----------------

    // A quick rising (or falling) three-note glassy figure over a soft bubble: open / close of the recipes.
    static float[] BuildArp(float[] notes)
    {
        float[] buf = Synth.Stereo(1.6f);
        bool rising = notes[notes.Length - 1] > notes[0];
        Synth.Bubble(buf, 0f, rising ? 420f : 520f, 0.05f, 0.35f, 0f);
        for (int i = 0; i < notes.Length; i++)
        {
            float at = 0.02f + i * 0.055f, pan = (i - 1) * 0.3f;
            Synth.Glide(buf, at, 0.5f, Synth.Midi(notes[i]), Synth.Midi(notes[i]), 0.004f, 9f, 0.22f); // soft felt onset
            Synth.Shimmer(buf, at, Synth.Midi(notes[i]), 0.25f, 0.35f, pan);
        }
        Synth.Lowpass(buf, 5000f);
        Synth.Reverb(buf, 0.35f, 0.85f, 0.4f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // A tiny muffled pluck (A5), pitched per recipe by the source.
    static float[] BuildPluck()
    {
        float[] buf = Synth.Stereo(0.6f);
        Synth.Glide(buf, 0f, 0.4f, Synth.Midi(81), Synth.Midi(81), 0.003f, 16f, 0.5f);
        Synth.Glide(buf, 0f, 0.3f, Synth.Midi(93), Synth.Midi(93), 0.002f, 40f, 0.12f);
        Synth.Grain(buf, 0f, 1800f, 1.5f, 0.003f, 0.25f, 0f, 41);
        Synth.Lowpass(buf, 3200f);
        Synth.Reverb(buf, 0.2f, 0.7f, 0.5f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Can't: two dull low "bup"s, the second a tritone lower, felt more than heard.
    static float[] BuildRefuse()
    {
        float[] buf = Synth.Stereo(0.8f);
        Synth.Glide(buf, 0f, 0.2f, 240f, 220f, 0.004f, 22f, 0.6f, 0.05f);
        Synth.Glide(buf, 0.11f, 0.3f, 170f, 155f, 0.004f, 16f, 0.55f, 0.06f);
        Synth.Grain(buf, 0f, 500f, 1f, 0.01f, 0.3f, 0f, 53);
        Synth.Grain(buf, 0.11f, 420f, 1f, 0.01f, 0.25f, 0f, 59);
        Synth.Lowpass(buf, 900f);
        Synth.Reverb(buf, 0.2f, 0.6f, 0.6f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    static float[] BuildPage()
    {
        float[] buf = Synth.Stereo(0.6f);
        Synth.Swoosh(buf, 0f, 0.16f, 700f, 1500f, 0.4f, 0.9f, 61);
        Synth.Bubble(buf, 0.08f, 600f, 0.04f, 0.2f, 0.2f);
        Synth.Lowpass(buf, 2800f);
        Synth.Reverb(buf, 0.2f, 0.7f, 0.5f);
        Synth.Normalize(buf, 0.6f);
        return buf;
    }

    // The nozzle engaging: a suction "plup" gliding up, a latch click, a bubble.
    static float[] BuildStart()
    {
        float[] buf = Synth.Stereo(0.8f);
        Synth.Glide(buf, 0f, 0.14f, 260f, 620f, 0.004f, 20f, 0.6f, 0.05f);
        Synth.Grain(buf, 0f, 1300f, 1.4f, 0.004f, 0.35f, 0f, 67);
        Synth.Bubble(buf, 0.05f, 480f, 0.05f, 0.3f, -0.2f);
        Synth.Lowpass(buf, 2400f);
        Synth.Reverb(buf, 0.25f, 0.75f, 0.5f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Latching onto a store: a small wet click with a round body.
    static float[] BuildDock()
    {
        float[] buf = Synth.Stereo(0.5f);
        Synth.Grain(buf, 0f, 1100f, 1.5f, 0.004f, 0.6f, 0f, 71);
        Synth.Grain(buf, 0f, 260f, 1f, 0.012f, 0.5f, 0f, 73);
        Synth.Bubble(buf, 0.015f, 380f, 0.035f, 0.25f, 0.1f);
        Synth.Lowpass(buf, 2200f);
        Synth.Reverb(buf, 0.18f, 0.6f, 0.6f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Sucking through a straw: a wet mid band gulping (slowed noise, squared, so it surges), a little air,
    // low bubbles; muffled. Nothing narrow, no pulse train.
    static float[] BuildDraw()
    {
        const float Length = 2f, Fade = 0.2f;
        float[] buf = Synth.Stereo(Length + Fade);
        int frames = buf.Length / 2;
        for (int ch = 0; ch < 2; ch++)
        {
            var rng = new Synth.Noise((uint)(811 + ch * 37));
            Synth.Biquad body = default, air = default, low = default, gulp = default;
            body.Bandpass(720f, 0.8f);
            air.Bandpass(1500f, 1f);
            low.Lowpass(280f, 0.7f);
            gulp.Lowpass(9f, 0.7f);
            for (int i = 0; i < frames; i++)
            {
                float n = rng.Next();
                float level = Mathf.Clamp01(0.4f + 12f * gulp.Process(rng.Next()));
                level *= level;
                buf[i * 2 + ch] = body.Process(n) * (0.25f + level) + air.Process(n) * 0.15f + low.Process(n) * 0.35f * level;
            }
        }
        var bub = new Synth.Noise(907);
        for (int j = 0; j < 26; j++)
            Synth.Bubble(buf, bub.Range(0f, Length), bub.Range(260f, 620f), bub.Range(0.02f, 0.05f), bub.Range(0.08f, 0.18f), bub.Range(-0.4f, 0.4f));
        float[] loop = Synth.Loop(buf, Length, Fade);
        Synth.Lowpass(loop, 2000f);
        Synth.Normalize(loop, 0.9f, false);
        return loop;
    }

    // Pushing the strand out: a soft squeeze rising and easing off, a few bubbles let out along the way.
    static float[] BuildDispense()
    {
        float[] buf = Synth.Stereo(1.4f);
        Synth.Swoosh(buf, 0f, 0.85f, 380f, 1200f, 0.5f, 0.8f, 83);
        Synth.Glide(buf, 0.05f, 0.8f, 180f, 260f, 0.25f, 2.5f, 0.2f);
        for (int j = 0; j < 5; j++) Synth.Bubble(buf, 0.15f + j * 0.13f, 420f + j * 60f, 0.04f, 0.18f, (j % 2 == 0 ? -0.3f : 0.3f));
        Synth.Lowpass(buf, 2400f);
        Synth.Reverb(buf, 0.25f, 0.75f, 0.5f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }

    // Made: a round low bloo and a soft two-note shimmer rising a fifth, in a big gentle reverb.
    static float[] BuildMade(Vector2 notes)
    {
        float[] buf = Synth.Stereo(2.4f);
        Synth.Glide(buf, 0f, 0.35f, 300f, 220f, 0.006f, 10f, 0.4f, 0.08f);
        Synth.Bubble(buf, 0.02f, 520f, 0.05f, 0.2f, 0f);
        Synth.Glide(buf, 0.03f, 0.9f, Synth.Midi(notes.x), Synth.Midi(notes.x), 0.004f, 5f, 0.18f);
        Synth.Glide(buf, 0.15f, 1.0f, Synth.Midi(notes.y), Synth.Midi(notes.y), 0.004f, 4f, 0.15f);
        Synth.Shimmer(buf, 0.03f, Synth.Midi(notes.x), 0.2f, 0.6f, -0.25f);
        Synth.Shimmer(buf, 0.15f, Synth.Midi(notes.y), 0.18f, 0.7f, 0.25f);
        Synth.Lowpass(buf, 5000f);
        Synth.Reverb(buf, 0.4f, 0.88f, 0.4f);
        Synth.Normalize(buf, 0.7f);
        return buf;
    }
}

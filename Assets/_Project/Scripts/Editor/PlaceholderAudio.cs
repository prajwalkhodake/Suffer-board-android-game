using System.Collections.Generic;
using Skyrift.Audio;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>Waveforms the placeholder synthesiser can produce. Four is enough to tell cues apart by ear.</summary>
    internal enum PlaceholderWave
    {
        Sine,
        Triangle,
        Square,
        Noise
    }

    /// <summary>
    /// One synthesised layer of a placeholder sound: a pitch sweep with an attack/decay envelope. Cues are
    /// sums of these, so a new sound is a row of tones rather than new DSP code.
    /// </summary>
    internal sealed class Tone
    {
        public PlaceholderWave wave = PlaceholderWave.Sine;

        /// <summary>Start and end frequency. Equal values = a steady note; ignored for <see cref="PlaceholderWave.Noise"/>.</summary>
        public float fromHz = 440f;
        public float toHz = 440f;

        /// <summary>Offset from the start of the cue, in seconds.</summary>
        public float start;

        public float length = 0.2f;
        public float attack = 0.004f;

        /// <summary>Exponential decay time constant. Larger = longer tail.</summary>
        public float decay = 0.1f;

        public float gain = 0.5f;
    }

    /// <summary>
    /// A placeholder SFX cue: the synthesis recipe plus the authored <see cref="SfxDefinition"/> values that
    /// are not purely artistic (bus, voice cap, cooldown). Keeping them together means the catalog row and
    /// the clip can never disagree about which cue they describe.
    /// </summary>
    internal sealed class CueSpec
    {
        public string id;
        public float seconds = 0.2f;
        public AudioBus bus = AudioBus.Sfx;
        public float volume = 1f;
        public float pitch = 1f;
        public float pitchVariance = 0.04f;
        public int voiceCap = 3;
        public float cooldown;
        public Tone[] tones;
    }

    /// <summary>
    /// A placeholder music stem: a rhythmic pattern of semitone offsets over a fixed loop. The intensity band
    /// (<see cref="fadeInAt"/>/<see cref="fullAt"/>) is the authored data that makes the mix adaptive — the
    /// service only evaluates <c>MusicStem.WeightAt</c>, so re-banding a stem is an edit here, not in code.
    /// </summary>
    internal sealed class StemSpec
    {
        public string name;
        public float fadeInAt;
        public float fullAt = 1f;
        public float volume = 1f;

        public float rootHz = 220f;
        public PlaceholderWave wave = PlaceholderWave.Sine;

        /// <summary>Semitone offsets per step; a negative value is a rest. The pattern repeats to fill the loop.</summary>
        public int[] pattern;

        /// <summary>Extra semitones sounded with each note, for chords. Null = a single voice.</summary>
        public int[] chord;

        public float noteLength = 0.2f;
        public float attack = 0.004f;
        public float decay = 0.1f;
        public float gain = 0.5f;
    }

    /// <summary>
    /// Synthesises the placeholder audio bank (TECH_DOC §4.18). Everything here is original, generated from
    /// maths at authoring time — no sampled or third-party material, which is what the brief's "100% original
    /// assets" rule requires even of placeholders.
    ///
    /// These are deliberately simple tones, not finished sound design. They exist so the run loop is audible
    /// and every cue id resolves to a real clip, and so an audio designer can replace a file at a known path
    /// without touching code or the catalog. M6 owns the real bank.
    ///
    /// Editor-only: the allocation rules in §11.2 apply to the gameplay loop, not to an authoring tool.
    /// </summary>
    internal static class PlaceholderAudio
    {
        private const float TwoPi = 6.28318530718f;

        /// <summary>Tempo of the placeholder music loop. 120 BPM keeps the step maths exact in binary floats.</summary>
        public const float Bpm = 120f;

        public const int StepsPerBar = 16;
        public const int Bars = 4;

        /// <summary>One sixteenth note. 0.125 s at 120 BPM.</summary>
        public static float StepSeconds => 60f / Bpm / 4f;

        /// <summary>
        /// Loop length shared by every stem. They must be identical: <c>AudioService.BindBank</c> schedules all
        /// stems at one dspTime and loops them independently, so a stem even one sample longer drifts out of
        /// phase with the others on every repeat.
        /// </summary>
        public static float LoopSeconds => Bars * StepsPerBar * StepSeconds;

        /// <summary>
        /// The full cue bank, in <see cref="SfxIds"/> order. Every constant in that class has a row here; the
        /// validator reports a missing one as an error so a silent cue cannot ship unnoticed.
        /// </summary>
        public static CueSpec[] Cues()
        {
            return new[]
            {
                // Locomotion — short, bright, and pitched so they read over the music bed.
                new CueSpec { id = SfxIds.Jump, seconds = 0.20f, tones = new[]
                    { T(PlaceholderWave.Sine, 240f, 680f, 0f, 0.18f, 0.8f, 0.10f) } },

                new CueSpec { id = SfxIds.Land, seconds = 0.14f, tones = new[]
                    { T(PlaceholderWave.Sine, 120f, 60f, 0f, 0.12f, 0.9f, 0.05f),
                      T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.06f, 0.35f, 0.02f) } },

                new CueSpec { id = SfxIds.RollStart, seconds = 0.24f, tones = new[]
                    { T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.22f, 0.5f, 0.12f),
                      T(PlaceholderWave.Triangle, 400f, 160f, 0f, 0.22f, 0.3f, 0.12f) } },

                new CueSpec { id = SfxIds.RollEnd, seconds = 0.10f, tones = new[]
                    { T(PlaceholderWave.Triangle, 180f, 320f, 0f, 0.08f, 0.5f, 0.04f) } },

                new CueSpec { id = SfxIds.LaneChange, seconds = 0.12f, tones = new[]
                    { T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.10f, 0.4f, 0.045f),
                      T(PlaceholderWave.Sine, 520f, 760f, 0f, 0.09f, 0.25f, 0.05f) } },

                new CueSpec { id = SfxIds.RampLaunch, seconds = 0.34f, tones = new[]
                    { T(PlaceholderWave.Sine, 300f, 980f, 0f, 0.30f, 0.8f, 0.20f),
                      T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.12f, 0.25f, 0.06f) } },

                // Contact — the two failure cues are the loudest things in the bank on purpose.
                new CueSpec { id = SfxIds.Stumble, seconds = 0.28f, tones = new[]
                    { T(PlaceholderWave.Square, 150f, 110f, 0f, 0.24f, 0.55f, 0.12f),
                      T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.10f, 0.4f, 0.05f) } },

                new CueSpec { id = SfxIds.Crash, seconds = 0.65f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Noise, 0f, 0f, 0f, 0.55f, 0.9f, 0.22f),
                      T(PlaceholderWave.Sine, 180f, 40f, 0f, 0.60f, 0.8f, 0.25f) } },

                new CueSpec { id = SfxIds.Graze, seconds = 0.08f, tones = new[]
                    { T(PlaceholderWave.Sine, 1400f, 1100f, 0f, 0.06f, 0.45f, 0.025f) } },

                // Chaser — capped at one voice and cooled down, because proximity can oscillate around the
                // warning threshold and retrigger the alarm every frame without both of these.
                new CueSpec { id = SfxIds.ChaserWarning, seconds = 0.44f, voiceCap = 1, cooldown = 1.2f, tones = new[]
                    { T(PlaceholderWave.Square, 740f, 740f, 0f, 0.16f, 0.4f, 0.10f),
                      T(PlaceholderWave.Square, 620f, 620f, 0.22f, 0.16f, 0.4f, 0.10f) } },

                new CueSpec { id = SfxIds.ChaserCaught, seconds = 0.55f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Square, 500f, 90f, 0f, 0.50f, 0.7f, 0.25f),
                      T(PlaceholderWave.Noise, 0f, 0f, 0.05f, 0.30f, 0.4f, 0.15f) } },

                // Run lifecycle — one voice each: these fire once per state change, never in a burst.
                new CueSpec { id = SfxIds.RunStart, seconds = 0.45f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Triangle, 440f, 440f, 0f, 0.12f, 0.5f, 0.09f),
                      T(PlaceholderWave.Triangle, 554f, 554f, 0.12f, 0.12f, 0.5f, 0.09f),
                      T(PlaceholderWave.Triangle, 659f, 659f, 0.24f, 0.18f, 0.55f, 0.14f) } },

                new CueSpec { id = SfxIds.RunEnd, seconds = 0.50f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Triangle, 659f, 659f, 0f, 0.14f, 0.5f, 0.10f),
                      T(PlaceholderWave.Triangle, 554f, 554f, 0.14f, 0.14f, 0.5f, 0.10f),
                      T(PlaceholderWave.Triangle, 440f, 440f, 0.28f, 0.20f, 0.5f, 0.16f) } },

                new CueSpec { id = SfxIds.ContinueOffer, seconds = 0.40f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Sine, 880f, 880f, 0f, 0.35f, 0.5f, 0.25f),
                      T(PlaceholderWave.Sine, 1320f, 1320f, 0f, 0.35f, 0.25f, 0.25f) } },

                new CueSpec { id = SfxIds.ContinueGranted, seconds = 0.34f, voiceCap = 1, tones = new[]
                    { T(PlaceholderWave.Sine, 660f, 990f, 0f, 0.30f, 0.6f, 0.18f),
                      T(PlaceholderWave.Sine, 1320f, 1980f, 0.02f, 0.28f, 0.25f, 0.18f) } },

                // Collectibles — a wide voice cap and extra pitch spread, because a coin line is a burst of
                // identical cues and without both it sounds like a machine gun (M2 wires the award path).
                new CueSpec { id = SfxIds.Coin, seconds = 0.14f, voiceCap = 6, pitchVariance = 0.06f, tones = new[]
                    { T(PlaceholderWave.Sine, 1320f, 1320f, 0f, 0.06f, 0.5f, 0.04f),
                      T(PlaceholderWave.Sine, 1760f, 1760f, 0.05f, 0.08f, 0.5f, 0.06f) } },

                new CueSpec { id = SfxIds.Key, seconds = 0.30f, voiceCap = 2, tones = new[]
                    { T(PlaceholderWave.Sine, 1046f, 1046f, 0f, 0.25f, 0.5f, 0.18f),
                      T(PlaceholderWave.Sine, 1568f, 1568f, 0.03f, 0.24f, 0.35f, 0.18f),
                      T(PlaceholderWave.Sine, 2093f, 2093f, 0.06f, 0.22f, 0.25f, 0.18f) } },

                new CueSpec { id = SfxIds.PowerUp, seconds = 0.34f, voiceCap = 2, tones = new[]
                    { T(PlaceholderWave.Square, 330f, 990f, 0f, 0.30f, 0.4f, 0.20f),
                      T(PlaceholderWave.Sine, 660f, 1320f, 0f, 0.30f, 0.35f, 0.20f) } },

                // UI — a separate bus so the pause menu still clicks while gameplay SFX are silenced, and no
                // pitch variance because a menu that changes timbre per click feels broken rather than lively.
                new CueSpec { id = SfxIds.UiClick, seconds = 0.06f, bus = AudioBus.Ui, voiceCap = 4, pitchVariance = 0f,
                    tones = new[] { T(PlaceholderWave.Sine, 950f, 950f, 0f, 0.05f, 0.5f, 0.02f) } },

                new CueSpec { id = SfxIds.UiBack, seconds = 0.08f, bus = AudioBus.Ui, voiceCap = 4, pitchVariance = 0f,
                    tones = new[] { T(PlaceholderWave.Sine, 620f, 480f, 0f, 0.07f, 0.45f, 0.03f) } }
            };
        }

        /// <summary>
        /// The four layers of the default run track, ordered from "always audible" to "only at full intensity".
        /// A minor-key loop in A: bass holds the root, the pad adds the third and fifth, the arp and percussion
        /// arrive as speed and chaser pressure rise.
        /// </summary>
        public static StemSpec[] Stems()
        {
            return new[]
            {
                new StemSpec
                {
                    name = "stem_bass", fadeInAt = 0f, fullAt = 0f, volume = 1f,
                    rootHz = 55f, wave = PlaceholderWave.Triangle,
                    pattern = new[] { 0, -1, -1, -1, 0, -1, -1, -1, 3, -1, -1, -1, 5, -1, -1, -1 },
                    noteLength = 0.42f, attack = 0.006f, decay = 0.22f, gain = 0.85f
                },
                new StemSpec
                {
                    name = "stem_pad", fadeInAt = 0f, fullAt = 0.30f, volume = 0.9f,
                    rootHz = 220f, wave = PlaceholderWave.Sine, chord = new[] { 3, 7 },
                    pattern = new[] { 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1 },
                    noteLength = 2f, attack = 0.35f, decay = 2.6f, gain = 0.30f
                },
                new StemSpec
                {
                    name = "stem_arp", fadeInAt = 0.35f, fullAt = 0.65f, volume = 0.85f,
                    rootHz = 440f, wave = PlaceholderWave.Square,
                    pattern = new[] { 0, 7, 12, 7, 0, 7, 12, 7, 3, 10, 15, 10, 5, 12, 17, 12 },
                    noteLength = 0.11f, attack = 0.002f, decay = 0.055f, gain = 0.22f
                },
                new StemSpec
                {
                    name = "stem_perc", fadeInAt = 0.60f, fullAt = 0.90f, volume = 0.8f,
                    rootHz = 0f, wave = PlaceholderWave.Noise,
                    pattern = new[] { 0, -1, -1, -1, 0, -1, -1, 0, -1, -1, 0, -1, 0, -1, 0, -1 },
                    noteLength = 0.06f, attack = 0.001f, decay = 0.035f, gain = 0.30f
                }
            };
        }

        public static float[] RenderCue(CueSpec cue) => Render(cue.seconds, cue.tones);

        public static float[] RenderStem(StemSpec stem) => Render(LoopSeconds, StemTones(stem));

        /// <summary>Expands a stem's repeating pattern into one tone per sounded step, plus its chord voices.</summary>
        private static Tone[] StemTones(StemSpec stem)
        {
            var tones = new List<Tone>(64);
            if (stem.pattern == null || stem.pattern.Length == 0) return tones.ToArray();

            int steps = Bars * StepsPerBar;
            int chordVoices = stem.chord != null ? stem.chord.Length : 0;

            for (int step = 0; step < steps; step++)
            {
                int offset = stem.pattern[step % stem.pattern.Length];
                if (offset < 0) continue;   // rest

                float start = step * StepSeconds;
                tones.Add(StemNote(stem, Semitone(stem.rootHz, offset), start));

                for (int c = 0; c < chordVoices; c++)
                    tones.Add(StemNote(stem, Semitone(stem.rootHz, offset + stem.chord[c]), start));
            }

            return tones.ToArray();
        }

        private static Tone StemNote(StemSpec stem, float hz, float start)
        {
            return new Tone
            {
                wave = stem.wave,
                fromHz = hz,
                toHz = hz,
                start = start,
                length = stem.noteLength,
                attack = stem.attack,
                decay = stem.decay,
                gain = stem.gain
            };
        }

        /// <summary>
        /// Additive render. Each tone sweeps its own phase accumulator, so a pitch sweep is sample-accurate
        /// rather than a per-block frequency step (which would zipper on a fast sweep like the crash drop).
        /// </summary>
        private static float[] Render(float seconds, Tone[] tones)
        {
            const int sampleRate = WavWriter.SampleRate;
            int total = Mathf.Max(1, Mathf.RoundToInt(seconds * sampleRate));
            var buffer = new float[total];
            if (tones == null) return buffer;

            for (int t = 0; t < tones.Length; t++)
            {
                Tone tone = tones[t];
                if (tone == null || tone.length <= 0f) continue;

                int start = Mathf.RoundToInt(tone.start * sampleRate);
                int count = Mathf.RoundToInt(tone.length * sampleRate);
                if (start < 0) { count += start; start = 0; }
                if (count <= 0) continue;

                float attack = Mathf.Max(0.0005f, tone.attack);
                float decay = Mathf.Max(0.001f, tone.decay);

                // A short release fade at the tail. Without it a tone whose decay outlives its length (the
                // pad) stops mid-waveform, which is an audible click — and on a looping stem, a click every
                // eight seconds. Capped at 20 ms so it never eats a short cue's body.
                float release = Mathf.Min(0.02f, tone.length * 0.25f);
                float releaseStart = tone.length - release;

                // Deterministic per-tone noise seed: re-running the tool must produce byte-identical clips,
                // or every rebuild would show up as a binary diff in version control.
                uint noise = 0x9E3779B9u ^ ((uint)t * 0x85EBCA6Bu);
                if (noise == 0u) noise = 1u;

                float phase = 0f;
                for (int i = 0; i < count; i++)
                {
                    int index = start + i;
                    if (index >= total) break;

                    float time = i / (float)sampleRate;
                    float sweep = count > 1 ? i / (float)(count - 1) : 1f;
                    float hz = Mathf.Lerp(tone.fromHz, tone.toHz, sweep);

                    phase += TwoPi * hz / sampleRate;
                    if (phase > TwoPi) phase -= TwoPi;   // one subtraction is enough: the increment is < 2π

                    float envelope = (time < attack ? time / attack : 1f) * Mathf.Exp(-time / decay);
                    if (time > releaseStart) envelope *= Mathf.Clamp01((tone.length - time) / release);

                    buffer[index] += Wave(tone.wave, phase, ref noise) * envelope * tone.gain;
                }
            }

            Normalise(buffer, 0.9f);
            return buffer;
        }

        private static float Wave(PlaceholderWave wave, float phase, ref uint noise)
        {
            switch (wave)
            {
                case PlaceholderWave.Sine:
                    return Mathf.Sin(phase);

                case PlaceholderWave.Triangle:
                    float x = phase / TwoPi;
                    return 4f * Mathf.Abs(x - 0.5f) - 1f;

                case PlaceholderWave.Square:
                    // Softened: a pure square is full-amplitude odd harmonics and sounds like a buzzsaw on a
                    // phone speaker. Blending the fundamental back in keeps the character without the harshness.
                    return 0.6f * (Mathf.Sin(phase) >= 0f ? 1f : -1f) + 0.4f * Mathf.Sin(phase);

                case PlaceholderWave.Noise:
                    // xorshift32, local to the tone. UnityEngine.Random is never touched: an authoring tool
                    // must not perturb the editor's random state, and the run seed must stay reproducible.
                    noise ^= noise << 13;
                    noise ^= noise >> 17;
                    noise ^= noise << 5;
                    return (noise / (float)uint.MaxValue) * 2f - 1f;

                default:
                    return 0f;
            }
        }

        /// <summary>
        /// Peak-normalises to a fixed headroom so the authored <c>SfxDefinition.volume</c> is the only thing
        /// that decides relative loudness. Without this, a cue summed from three tones would be three times
        /// as loud as a single-tone cue regardless of what the catalog says.
        /// </summary>
        private static void Normalise(float[] buffer, float peak)
        {
            float max = 0f;
            for (int i = 0; i < buffer.Length; i++)
            {
                float magnitude = Mathf.Abs(buffer[i]);
                if (magnitude > max) max = magnitude;
            }

            if (max <= 0.00001f) return;   // silence: nothing to scale, and dividing would produce NaN

            float scale = peak / max;
            for (int i = 0; i < buffer.Length; i++) buffer[i] *= scale;
        }

        private static float Semitone(float rootHz, int offset) => rootHz * Mathf.Pow(2f, offset / 12f);

        private static Tone T(PlaceholderWave wave, float fromHz, float toHz, float start, float length,
            float gain, float decay, float attack = 0.004f)
        {
            return new Tone
            {
                wave = wave,
                fromHz = fromHz,
                toHz = toHz,
                start = start,
                length = length,
                gain = gain,
                decay = decay,
                attack = attack
            };
        }
    }
}
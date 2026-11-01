using System;
using UnityEngine;

namespace Skyrift.Audio
{
    /// <summary>Mixer buses exposed to the settings screen (TECH_DOC §4.18).</summary>
    public enum AudioBus
    {
        Master = 0,
        Music,
        Sfx,
        Ui
    }

    /// <summary>
    /// Group names in the generated AudioMixer. Infrastructure constants, not content: the mixer asset is
    /// produced by the editor setup tool to this exact layout, the same way <c>InputActionNames</c> fixes
    /// the action-map layout. Exposed-parameter names are data (<c>AudioConfig</c>) because settings binds to them.
    /// </summary>
    public static class AudioMixerNames
    {
        public const string Master = "Master";
        public const string Music = "Music";
        public const string Sfx = "SFX";
        public const string Ui = "UI";

        /// <summary>Indexed by <see cref="AudioBus"/>. Allocation-free lookup for the four fixed buses.</summary>
        public static string ForBus(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Music: return Music;
                case AudioBus.Sfx: return Sfx;
                case AudioBus.Ui: return Ui;
                default: return Master;
            }
        }
    }

    /// <summary>
    /// Stable cue id. A value type over a string literal so a call site cannot pass an arbitrary string
    /// and so ids can be authored in data and compared without allocating.
    /// </summary>
    public readonly struct SfxId : IEquatable<SfxId>
    {
        public readonly string Value;

        public SfxId(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public static readonly SfxId None = new SfxId(string.Empty);

        public static implicit operator SfxId(string value) => new SfxId(value);
        public static implicit operator string(SfxId id) => id.Value;

        public bool Equals(SfxId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is SfxId other && Equals(other);
        public override int GetHashCode() => Value != null ? StringComparer.Ordinal.GetHashCode(Value) : 0;
        public override string ToString() => Value;

        public static bool operator ==(SfxId a, SfxId b) => a.Equals(b);
        public static bool operator !=(SfxId a, SfxId b) => !a.Equals(b);
    }

    /// <summary>Stable music track id. Resolved against the music catalog; a world may override it.</summary>
    public readonly struct MusicId : IEquatable<MusicId>
    {
        public readonly string Value;

        public MusicId(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool IsValid => !string.IsNullOrEmpty(Value);

        public static readonly MusicId None = new MusicId(string.Empty);

        public static implicit operator MusicId(string value) => new MusicId(value);
        public static implicit operator string(MusicId id) => id.Value;

        public bool Equals(MusicId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is MusicId other && Equals(other);
        public override int GetHashCode() => Value != null ? StringComparer.Ordinal.GetHashCode(Value) : 0;
        public override string ToString() => Value;

        public static bool operator ==(MusicId a, MusicId b) => a.Equals(b);
        public static bool operator !=(MusicId a, MusicId b) => !a.Equals(b);
    }

    /// <summary>
    /// Per-play overrides for one cue (TECH_DOC §4.18). A readonly struct passed by value so playing a
    /// sound never allocates; defaults mean "use the authored values on the SfxDefinition".
    /// </summary>
    public readonly struct SfxParams
    {
        /// <summary>Multiplier on the authored volume. 1 = authored.</summary>
        public readonly float Volume;

        /// <summary>Multiplier on the authored pitch. 1 = authored.</summary>
        public readonly float Pitch;

        /// <summary>World position, used only when <see cref="IsSpatial"/> is true.</summary>
        public readonly Vector3 Position;

        public readonly bool IsSpatial;

        public SfxParams(float volume, float pitch, Vector3 position, bool isSpatial)
        {
            Volume = volume > 0f ? volume : 1f;
            Pitch = pitch > 0f ? pitch : 1f;
            Position = position;
            IsSpatial = isSpatial;
        }

        public static readonly SfxParams Default = new SfxParams(1f, 1f, Vector3.zero, false);

        /// <summary>Convenience for a 2D cue at a non-default volume (coin pickups, UI clicks).</summary>
        public static SfxParams AtVolume(float volume) => new SfxParams(volume, 1f, Vector3.zero, false);

        /// <summary>Convenience for a world-positioned cue (impacts, footsteps, chaser).</summary>
        public static SfxParams AtPosition(Vector3 position, float volume = 1f) => new SfxParams(volume, 1f, position, true);
    }

    /// <summary>
    /// The only audio entry point (TECH_DOC §4.18). Gameplay pushes into it; Audio never subscribes to a
    /// Gameplay event, because the dependency arrow is Gameplay → Audio and must stay that way (§3.3).
    /// </summary>
    public interface IAudioService
    {
        void PlaySfx(SfxId id, SfxParams p = default);
        void PlayMusic(MusicId id, bool crossfade = true);
        void StopMusic(bool fade = true);

        /// <summary>0 calm → 1 full chase. Cross-fades the authored stem weights.</summary>
        void SetMusicIntensity(float normalized01);

        void SetBusVolume(AudioBus bus, float volume01);
        float GetBusVolume(AudioBus bus);

        /// <summary>Run pause: music ducks, SFX stop. UI keeps playing on the Ui bus.</summary>
        void PauseAll(bool paused);

        /// <summary>Ad break / app background: everything is muted, not merely ducked.</summary>
        void MuteAll(bool muted);

        /// <summary>
        /// Advances crossfades and stem weights on <b>unscaled</b> time, so audio keeps moving while the
        /// run clock is frozen for a crash. Called once per frame by the composition root.
        /// </summary>
        void Tick(float unscaledDeltaTime);
    }

    /// <summary>
    /// Computes the music intensity from run state (TECH_DOC §4.18 extension point). The interface lives
    /// in Audio; the shipped implementation lives in Gameplay because it reads speed and chaser proximity,
    /// which Audio is not allowed to reference. Swapping the model means swapping this object.
    /// </summary>
    public interface IMusicIntensityDriver
    {
        /// <summary>
        /// The eased value last pushed to the audio service, 0 calm → 1 full chase. Read-only diagnostic:
        /// the debug overlay and the M6 VFX intensity channel both need the number the mix is actually at,
        /// not the instantaneous target it is moving toward.
        /// </summary>
        float CurrentIntensity { get; }

        /// <summary>Eases toward the target intensity and pushes it to the audio service. Unscaled time.</summary>
        void Tick(float unscaledDeltaTime);

        /// <summary>Clears speed/proximity state and drops intensity to the calm baseline.</summary>
        void ResetForRun();

        /// <summary>Speed that maps to intensity 1 on the speed axis. Pushed once per run.</summary>
        void SetMaxSpeed(float metresPerSecond);
    }
}
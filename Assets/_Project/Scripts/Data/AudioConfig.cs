using Skyrift.Core.Ids;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// Mixer routing, voice budget and adaptive-music tuning (TECH_DOC §4.18, §5.13). Lives in Data so
    /// Remote Config and the settings screen can drive volumes without touching the audio assembly, and
    /// so the intensity model is a set of authored weights rather than constants in a driver.
    /// </summary>
    [CreateAssetMenu(fileName = "AudioConfig", menuName = "Skyrift/Config/Audio")]
    public sealed class AudioConfig : DefinitionBase
    {
        [Header("Sfx voices")]
        [Tooltip("PoolCatalog key for the pooled audio voice prefab. Voices are created at boot, never mid-run.")]
        public string voicePoolKey = "audio_voice";

        [Tooltip("Soft cap on simultaneous voices. The pool's prewarm count is the hard cap; exceeding it steals the eldest voice.")]
        [Min(1)] public int maxConcurrentVoices = 12;

        [Header("Music")]
        [Tooltip("Track id played when a run starts and the world has no musicId of its own.")]
        public string defaultMusicId = "music_run_default";

        [Min(0f)] public float musicCrossfadeSeconds = 1.2f;
        [Tooltip("How long a stem takes to reach its target weight when intensity changes.")]
        [Min(0f)] public float musicStemFadeSeconds = 0.6f;

        [Header("Adaptive intensity")]
        [Tooltip("Share of intensity driven by player speed. Normalised against the chaser weight at runtime.")]
        [Range(0f, 1f)] public float intensitySpeedWeight = 0.6f;

        [Tooltip("Share of intensity driven by chaser proximity. Normalised against the speed weight at runtime.")]
        [Range(0f, 1f)] public float intensityChaserWeight = 0.4f;

        [Tooltip("Seconds to ease toward the target intensity, so a stumble does not snap the mix.")]
        [Min(0f)] public float intensitySmoothingSeconds = 0.35f;

        [Tooltip("Minimum seconds between mixer writes. Intensity is continuous; the mixer does not need every frame.")]
        [Min(0.01f)] public float intensityUpdateInterval = 0.1f;

        [Header("Buses")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float musicVolume = 0.8f;
        [Range(0f, 1f)] public float sfxVolume = 1f;
        [Range(0f, 1f)] public float uiVolume = 1f;

        [Tooltip("Music level while the run is paused. Ads mute everything instead of ducking.")]
        [Range(0f, 1f)] public float pauseDuckVolume = 0.35f;

        [Header("Mixer exposed parameters")]
        public string masterVolumeParam = "MasterVolume";
        public string musicVolumeParam = "MusicVolume";
        public string sfxVolumeParam = "SfxVolume";
        public string uiVolumeParam = "UiVolume";

        /// <summary>Speed share of the intensity model, normalised so the two weights always sum to 1.</summary>
        public float NormalisedSpeedWeight => Normalise(intensitySpeedWeight);

        /// <summary>Chaser share of the intensity model, normalised so the two weights always sum to 1.</summary>
        public float NormalisedChaserWeight => Normalise(intensityChaserWeight);

        private float Normalise(float weight)
        {
            float total = Mathf.Max(0f, intensitySpeedWeight) + Mathf.Max(0f, intensityChaserWeight);
            // Both weights zero is an authoring error; fall back to speed-only rather than dividing by zero.
            return total > 0f ? Mathf.Clamp01(weight) / total : 1f;
        }

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(AudioConfig);

            if (!StableId.IsValidString(voicePoolKey))
                report.Error(t, Id, "voice_pool_key", "voicePoolKey must be a valid pool id.");

            if (maxConcurrentVoices < 1)
                report.Error(t, Id, "voices", "maxConcurrentVoices must be >= 1.");

            if (intensitySpeedWeight + intensityChaserWeight <= 0f)
                report.Error(t, Id, "intensity_weights", "At least one intensity weight must be > 0.");

            if (!StableId.IsValidString(defaultMusicId))
                report.Error(t, Id, "default_music", "defaultMusicId must be a valid id.");

            if (string.IsNullOrEmpty(masterVolumeParam) || string.IsNullOrEmpty(musicVolumeParam)
                || string.IsNullOrEmpty(sfxVolumeParam) || string.IsNullOrEmpty(uiVolumeParam))
                report.Error(t, Id, "mixer_params", "Every mixer exposed-parameter name must be set.");
        }
    }
}
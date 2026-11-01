using Skyrift.Core.Pooling;
using UnityEngine;

namespace Skyrift.Audio
{
    /// <summary>
    /// One pooled SFX voice (TECH_DOC §4.18, §4.6). Voices are created during the boot prewarm and never
    /// again: a cue that exceeds the budget steals the eldest active voice rather than instantiating.
    ///
    /// Music stems deliberately do <b>not</b> use this pool — a stem must survive for the whole track and
    /// would otherwise be stolen by a coin pickup. They get their own fixed bank in <see cref="AudioService"/>.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioVoice : PooledEntity
    {
        private AudioSource _source;

        /// <summary>Cue currently occupying this voice, or <see cref="SfxId.None"/> when idle.</summary>
        public SfxId CueId { get; private set; }

        public AudioSource Source => _source;

        /// <summary>Authored loudness of the current cue, before bus gain. Kept so a live volume change can be reapplied.</summary>
        public float BaseVolume { get; private set; }

        /// <summary>Bus this voice is routed to, so the service can reapply the right gain without a catalog lookup.</summary>
        public AudioBus Bus { get; private set; }

        public bool IsPlaying => _source != null && _source.isPlaying;

        protected override void Awake()
        {
            base.Awake();

            _source = GetComponent<AudioSource>();
            if (_source == null)
            {
                // Only reachable on a hand-made prefab; the editor setup tool always adds the source.
                // This runs at prewarm, never during a run.
                _source = gameObject.AddComponent<AudioSource>();
            }

            _source.playOnAwake = false;
            _source.loop = false;
            _source.Stop();
        }

        /// <summary>Binds a cue to this voice. Called immediately after the pool hands it over.</summary>
        /// <param name="gain">Bus gain to apply on top of the authored volume (1 when a mixer owns the bus).</param>
        public void Play(SfxDefinition definition, AudioClip clip, AudioMixerGroup group, SfxParams p, float pitch, float gain)
        {
            CueId = definition.id;
            BaseVolume = Mathf.Clamp01(definition.volume * p.Volume);
            Bus = definition.bus;

            _source.clip = clip;
            _source.outputAudioMixerGroup = group;
            _source.loop = false;
            _source.volume = Mathf.Clamp01(BaseVolume * gain);
            _source.pitch = Mathf.Clamp(pitch, 0.1f, 3f);

            bool spatial = p.IsSpatial || definition.spatial;
            _source.spatialBlend = spatial ? 1f : 0f;
            if (spatial)
            {
                _source.minDistance = definition.minDistance;
                _source.maxDistance = definition.maxDistance;
                _source.rolloffMode = AudioRolloffMode.Linear;
                CachedTransform.position = p.Position;
            }

            _source.Play();
        }

        /// <summary>Reapplies the bus gain without restarting the cue, so settings changes are heard immediately.</summary>
        public void SetGain(float gain)
        {
            if (_source != null) _source.volume = Mathf.Clamp01(BaseVolume * gain);
        }

        public override void OnSpawned()
        {
            // Every field the service reads is reset here: a recycled voice must never carry the previous
            // cue's id, clip or spatial settings (ADR-0007).
            CueId = SfxId.None;
            BaseVolume = 1f;
            Bus = AudioBus.Sfx;
            if (_source == null) return;

            _source.Stop();
            _source.clip = null;
            _source.spatialBlend = 0f;
            _source.pitch = 1f;
            _source.volume = 1f;
        }

        public override void OnDespawned()
        {
            CueId = SfxId.None;
            BaseVolume = 1f;
            Bus = AudioBus.Sfx;
            if (_source == null) return;

            _source.Stop();
            _source.clip = null;
        }
    }
}
using System;
using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Pooling;
using Skyrift.Core.Randomization;
using Skyrift.Data;
using UnityEngine;
using UnityEngine.Audio;

namespace Skyrift.Audio
{
    /// <summary>
    /// Mixer routing, pooled SFX and adaptive layered music (TECH_DOC §4.18).
    ///
    /// Two deliberate design points:
    /// <list type="bullet">
    /// <item>Audio never subscribes to a Gameplay event. The dependency arrow is Gameplay → Audio, so
    /// <c>MusicIntensityDriver</c> and <c>RunSfxRouter</c> (both in Gameplay) push into this service. §4.18's
    /// "Consumes PlayerSpeedChangedEvent" is satisfied by those drivers, not by a forbidden reference.</item>
    /// <item>Music stems get a fixed two-bank set of AudioSources created once at boot, while SFX use the
    /// shared voice pool. A stem must survive for the whole track, so it must never be stealable by a coin
    /// pickup; two banks are what make a real crossfade possible without creating objects mid-run.</item>
    /// </list>
    ///
    /// A missing clip or an unregistered voice pool degrades to silence with a single report — placeholder
    /// audio must never block a run or spam the log.
    /// </summary>
    public sealed class AudioService : IAudioService, IDisposable
    {
        private const int BankCount = 2;
        private const int BusCount = 4;

        /// <summary>Scheduling lead so every stem of a track starts on the same DSP sample (§4.18).</summary>
        private const double PlayLeadSeconds = 0.06;

        private readonly AudioConfig _config;
        private readonly AudioCatalog _catalog;
        private readonly IPoolRegistry _pools;
        private readonly IRandomProvider _random;
        private readonly ILog _log;
        private readonly Transform _musicRoot;
        private readonly AudioMixer _mixer;

        private readonly AudioMixerGroup[] _groups = new AudioMixerGroup[BusCount];
        private readonly float[] _busVolume = new float[BusCount];
        private readonly List<AudioVoice> _active = new List<AudioVoice>(16);
        private readonly Dictionary<SfxId, double> _lastPlayed = new Dictionary<SfxId, double>(32);
        private readonly HashSet<SfxId> _reportedSfx = new HashSet<SfxId>();
        private readonly HashSet<MusicId> _reportedMusic = new HashSet<MusicId>();

        private AudioSource[][] _banks;
        private float[][] _stemWeights;
        private MusicTrack[] _bankTrack;
        private readonly float[] _bankEnvelope = new float[BankCount];

        private int _activeBank;
        private int _incomingBank = -1;
        private float _crossfadeRemaining;
        private float _crossfadeDuration;

        private float _musicEnvelope = 1f;
        private float _fadeOutRemaining;
        private float _fadeOutDuration;

        private IObjectPool<PooledEntity> _voicePool;
        private double _time;
        private float _intensity;
        private float _duck = 1f;
        private bool _paused;
        private bool _muted;
        private bool _ready;
        private bool _disposed;
        private bool _poolReported;

        public AudioService(AudioConfig config, AudioCatalog catalog, IPoolRegistry pools, IRandomProvider random,
            ILog log, Transform musicRoot, AudioMixer mixer)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _pools = pools;
            _random = random;
            _log = log;
            _musicRoot = musicRoot;
            _mixer = mixer;

            _busVolume[(int)AudioBus.Master] = Mathf.Clamp01(config.masterVolume);
            _busVolume[(int)AudioBus.Music] = Mathf.Clamp01(config.musicVolume);
            _busVolume[(int)AudioBus.Sfx] = Mathf.Clamp01(config.sfxVolume);
            _busVolume[(int)AudioBus.Ui] = Mathf.Clamp01(config.uiVolume);

            ResolveMixerGroups();
            BuildMusicBanks();
            ResolveVoicePool();

            for (int i = 0; i < BusCount; i++) PushBusToMixer((AudioBus)i);

            _ready = true;
        }

        // ── IAudioService ────────────────────────────────────────────────────────────────────────────────

        public void PlaySfx(SfxId id, SfxParams p = default)
        {
            if (!_ready || _muted || !id.IsValid) return;

            if (!_catalog.TryGetSfx(id, out SfxDefinition def) || def.clip == null)
            {
                ReportMissingSfx(id);
                return;
            }

            // A paused run silences gameplay SFX but not the UI bus: the pause menu still has to click.
            if (_paused && def.bus != AudioBus.Ui) return;

            float gain = BusGain(def.bus);
            if (gain <= 0.0001f) return;

            if (def.cooldown > 0f && _lastPlayed.TryGetValue(id, out double last) && _time - last < def.cooldown)
                return;

            // Per-cue cap: steal the oldest voice of this cue so a burst of coins cannot drown everything else.
            int count = 0;
            int oldest = -1;
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i] == null || _active[i].CueId != id) continue;
                count++;
                if (oldest < 0) oldest = i;
            }
            if (count >= Mathf.Max(1, def.voiceCap) && oldest >= 0) ReleaseAt(oldest);

            // Global soft cap. The pool's prewarm is the hard cap; staying under it means no pool overflow.
            if (_active.Count >= Mathf.Max(1, _config.maxConcurrentVoices)) ReleaseAt(0);

            if (_voicePool == null)
            {
                ReportMissingPool();
                return;
            }

            PooledEntity entity = _voicePool.Get();
            var voice = entity as AudioVoice;
            if (voice == null)
            {
                _voicePool.Release(entity);
                ReportMissingPool();
                return;
            }

            // Defensive: if the pool recycled a voice we still track, drop the stale entry before re-adding.
            _active.Remove(voice);

            float variance = def.pitchVariance > 0f && _random != null ? (_random.Value01() * 2f - 1f) * def.pitchVariance : 0f;
            voice.Play(def, def.clip, _groups[(int)def.bus], p, def.pitch * (1f + variance), gain);

            _active.Add(voice);
            _lastPlayed[id] = _time;
        }

        public void PlayMusic(MusicId id, bool crossfade = true)
        {
            if (!_ready || !id.IsValid) return;

            if (!_catalog.TryGetMusic(id, out MusicTrack track))
            {
                ReportMissingMusic(id);
                return;
            }

            // Already the current track and not mid-transition: nothing to do, so stems stay sample-aligned.
            if (_bankTrack[_activeBank] == track && _incomingBank < 0) return;

            _fadeOutRemaining = 0f;
            _musicEnvelope = 1f;

            bool hasCurrent = _bankTrack[_activeBank] != null;
            int target = crossfade && hasCurrent && _config.musicCrossfadeSeconds > 0f ? 1 - _activeBank : _activeBank;

            BindBank(target, track);

            if (target == _activeBank)
            {
                _bankEnvelope[target] = 1f;
                _incomingBank = -1;
                _crossfadeRemaining = 0f;
                return;
            }

            _incomingBank = target;
            _bankEnvelope[target] = 0f;
            _crossfadeDuration = _config.musicCrossfadeSeconds;
            _crossfadeRemaining = _crossfadeDuration;
        }

        public void StopMusic(bool fade = true)
        {
            if (!_ready) return;

            if (!fade || _config.musicCrossfadeSeconds <= 0f)
            {
                ClearBank(_activeBank);
                if (_incomingBank >= 0) ClearBank(_incomingBank);
                _incomingBank = -1;
                _crossfadeRemaining = 0f;
                _musicEnvelope = 1f;
                return;
            }

            _incomingBank = -1;
            _crossfadeRemaining = 0f;
            _fadeOutDuration = _config.musicCrossfadeSeconds;
            _fadeOutRemaining = _fadeOutDuration;
        }

        public void SetMusicIntensity(float normalized01)
        {
            _intensity = Mathf.Clamp01(normalized01);
        }

        public void SetBusVolume(AudioBus bus, float volume01)
        {
            int index = (int)bus;
            if (index < 0 || index >= BusCount) return;

            _busVolume[index] = Mathf.Clamp01(volume01);
            PushBusToMixer(bus);

            // Without a mixer the gain lives on the sources, so reapply it to everything already playing.
            if (_mixer == null) RefreshVoiceGains();
        }

        public float GetBusVolume(AudioBus bus)
        {
            int index = (int)bus;
            return index >= 0 && index < BusCount ? _busVolume[index] : 0f;
        }

        public void PauseAll(bool paused)
        {
            if (!_ready || _paused == paused) return;
            _paused = paused;

            // A paused run stops its gameplay SFX; only music ducks, so the pause screen is not silent.
            if (paused) ReleaseVoicesExcept(AudioBus.Ui);
        }

        public void MuteAll(bool muted)
        {
            if (!_ready || _muted == muted) return;
            _muted = muted;

            if (muted) ReleaseAllVoices();
            PushBusToMixer(AudioBus.Master);
            if (_mixer == null) RefreshVoiceGains();
        }

        public void Tick(float unscaledDeltaTime)
        {
            if (!_ready || _disposed || unscaledDeltaTime <= 0f) return;

            _time += unscaledDeltaTime;

            UpdateVoices();
            UpdateDuck(unscaledDeltaTime);
            UpdateCrossfade(unscaledDeltaTime);
            UpdateMusicGains(unscaledDeltaTime);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;

            ReleaseAllVoices();

            if (_banks == null) return;
            for (int b = 0; b < _banks.Length; b++)
            {
                if (_banks[b] == null) continue;
                for (int s = 0; s < _banks[b].Length; s++)
                {
                    AudioSource source = _banks[b][s];
                    if (source == null) continue;
                    source.Stop();
                    // Shutdown only. Destroying a boot-created stem source here is not gameplay churn.
                    UnityEngine.Object.Destroy(source.gameObject);
                }
            }
        }

        // ── Internals ────────────────────────────────────────────────────────────────────────────────────

        private void ResolveMixerGroups()
        {
            if (_mixer == null) return;

            for (int i = 0; i < BusCount; i++)
            {
                // FindMatchingGroups allocates; this runs exactly once, at composition time.
                AudioMixerGroup[] matches = _mixer.FindMatchingGroups(AudioMixerNames.ForBus((AudioBus)i));
                if (matches != null && matches.Length > 0) _groups[i] = matches[0];
                else _log?.Warn(LogChannel.Audio, $"Mixer has no '{AudioMixerNames.ForBus((AudioBus)i)}' group; that bus falls back to source gain.");
            }
        }

        private void BuildMusicBanks()
        {
            int maxStems = 1;
            int trackCount = _catalog.music != null ? _catalog.music.Count : 0;
            for (int i = 0; i < trackCount; i++)
            {
                MusicTrack track = _catalog.music[i];
                if (track != null && track.stems != null && track.stems.Count > maxStems) maxStems = track.stems.Count;
            }

            _banks = new AudioSource[BankCount][];
            _stemWeights = new float[BankCount][];
            _bankTrack = new MusicTrack[BankCount];

            for (int b = 0; b < BankCount; b++)
            {
                _banks[b] = new AudioSource[maxStems];
                _stemWeights[b] = new float[maxStems];

                for (int s = 0; s < maxStems; s++)
                {
                    // Created once during boot, sized to the largest authored track. No stem source is ever
                    // created again, so changing tracks or intensity cannot allocate (§4.6, §11.2).
                    var go = new GameObject($"MusicStem_{b}_{s}");
                    if (_musicRoot != null) go.transform.SetParent(_musicRoot, false);

                    AudioSource source = go.AddComponent<AudioSource>();
                    source.playOnAwake = false;
                    source.loop = true;
                    source.spatialBlend = 0f;
                    source.volume = 0f;
                    source.outputAudioMixerGroup = _groups[(int)AudioBus.Music];
                    _banks[b][s] = source;
                }
            }
        }

        private void ResolveVoicePool()
        {
            if (_pools == null) return;
            if (!_pools.TryGetPool(_config.voicePoolKey, out _voicePool)) _voicePool = null;
        }

        private void BindBank(int bank, MusicTrack track)
        {
            AudioSource[] sources = _banks[bank];
            _bankTrack[bank] = track;

            double start = AudioSettings.dspTime + PlayLeadSeconds;

            for (int s = 0; s < sources.Length; s++)
            {
                AudioSource source = sources[s];
                if (source == null) continue;

                source.Stop();
                _stemWeights[bank][s] = 0f;

                MusicStem stem = track.stems != null && s < track.stems.Count ? track.stems[s] : null;
                if (stem == null || stem.clip == null)
                {
                    source.clip = null;
                    source.volume = 0f;
                    continue;
                }

                source.clip = stem.clip;
                source.loop = track.loop;
                source.volume = 0f;
                source.outputAudioMixerGroup = _groups[(int)AudioBus.Music];
                source.PlayScheduled(start);
            }
        }

        private void ClearBank(int bank)
        {
            _bankTrack[bank] = null;
            _bankEnvelope[bank] = 0f;

            AudioSource[] sources = _banks[bank];
            for (int s = 0; s < sources.Length; s++)
            {
                if (sources[s] == null) continue;
                sources[s].Stop();
                sources[s].clip = null;
                sources[s].volume = 0f;
                _stemWeights[bank][s] = 0f;
            }
        }

        private void UpdateVoices()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                AudioVoice voice = _active[i];
                if (voice == null || !voice.IsPlaying)
                {
                    ReleaseAt(i);
                    continue;
                }

                voice.SetGain(BusGain(voice.Bus));
            }
        }

        private void UpdateDuck(float dt)
        {
            float target = _paused ? Mathf.Clamp01(_config.pauseDuckVolume) : 1f;
            float rate = dt / Mathf.Max(0.0001f, _config.musicStemFadeSeconds);
            _duck = Mathf.MoveTowards(_duck, target, rate);
        }

        private void UpdateCrossfade(float dt)
        {
            if (_fadeOutRemaining > 0f)
            {
                _fadeOutRemaining -= dt;
                _musicEnvelope = _fadeOutDuration > 0f ? Mathf.Clamp01(_fadeOutRemaining / _fadeOutDuration) : 0f;

                if (_fadeOutRemaining <= 0f)
                {
                    _fadeOutRemaining = 0f;
                    ClearBank(_activeBank);
                    if (_incomingBank >= 0) ClearBank(_incomingBank);
                    _incomingBank = -1;
                    _musicEnvelope = 1f;
                }
                return;
            }

            if (_incomingBank < 0) return;

            _crossfadeRemaining -= dt;
            float t = _crossfadeDuration > 0f ? 1f - Mathf.Clamp01(_crossfadeRemaining / _crossfadeDuration) : 1f;

            _bankEnvelope[_incomingBank] = t;
            _bankEnvelope[_activeBank] = 1f - t;

            if (_crossfadeRemaining > 0f) return;

            ClearBank(_activeBank);
            _activeBank = _incomingBank;
            _incomingBank = -1;
            _bankEnvelope[_activeBank] = 1f;
            _crossfadeRemaining = 0f;
        }

        private void UpdateMusicGains(float dt)
        {
            float rate = dt / Mathf.Max(0.0001f, _config.musicStemFadeSeconds);
            float busGain = _mixer != null && _groups[(int)AudioBus.Music] != null ? 1f : BusGain(AudioBus.Music);
            float transient = _duck * _musicEnvelope * (_muted ? 0f : 1f);

            for (int b = 0; b < BankCount; b++)
            {
                MusicTrack track = _bankTrack[b];
                if (track == null) continue;

                float envelope = _bankEnvelope[b];
                AudioSource[] sources = _banks[b];

                for (int s = 0; s < sources.Length; s++)
                {
                    AudioSource source = sources[s];
                    if (source == null || source.clip == null) continue;

                    MusicStem stem = track.stems != null && s < track.stems.Count ? track.stems[s] : null;
                    if (stem == null) continue;

                    float target = stem.WeightAt(_intensity);
                    _stemWeights[b][s] = Mathf.MoveTowards(_stemWeights[b][s], target, rate);

                    source.volume = Mathf.Clamp01(track.volume * stem.volume * _stemWeights[b][s] * envelope * transient * busGain);
                }
            }
        }

        private void ReleaseAt(int index)
        {
            if (index < 0 || index >= _active.Count) return;

            AudioVoice voice = _active[index];
            _active.RemoveAt(index);
            if (voice != null && _voicePool != null) _voicePool.Release(voice);
        }

        private void ReleaseAllVoices()
        {
            for (int i = _active.Count - 1; i >= 0; i--) ReleaseAt(i);
        }

        private void ReleaseVoicesExcept(AudioBus keep)
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                AudioVoice voice = _active[i];
                if (voice == null || voice.Bus != keep) ReleaseAt(i);
            }
        }

        private void RefreshVoiceGains()
        {
            for (int i = 0; i < _active.Count; i++)
                if (_active[i] != null) _active[i].SetGain(BusGain(_active[i].Bus));
        }

        /// <summary>
        /// Gain applied on the source itself. With a mixer the bus fader owns the level, so this is 1 and
        /// the mixer parameter carries the volume — applying both would square the setting.
        /// </summary>
        private float BusGain(AudioBus bus)
        {
            bool mixerOwnsBus = _mixer != null && _groups[(int)bus] != null;
            if (mixerOwnsBus) return _muted ? 0f : 1f;
            return _busVolume[(int)bus] * MasterGain();
        }

        private float MasterGain() => _muted ? 0f : _busVolume[(int)AudioBus.Master];

        private void PushBusToMixer(AudioBus bus)
        {
            if (_mixer == null) return;

            string parameter = ParameterFor(bus);
            if (string.IsNullOrEmpty(parameter)) return;

            float value = bus == AudioBus.Master ? MasterGain() : _busVolume[(int)bus];
            _mixer.SetFloat(parameter, ToDecibels(value));
        }

        private string ParameterFor(AudioBus bus)
        {
            switch (bus)
            {
                case AudioBus.Music: return _config.musicVolumeParam;
                case AudioBus.Sfx: return _config.sfxVolumeParam;
                case AudioBus.Ui: return _config.uiVolumeParam;
                default: return _config.masterVolumeParam;
            }
        }

        /// <summary>Unity mixer exposed volumes are in dB; -80 is the engine's silence floor.</summary>
        private static float ToDecibels(float linear)
        {
            return linear <= 0.0001f ? -80f : Mathf.Log10(Mathf.Clamp(linear, 0.0001f, 1f)) * 20f;
        }

        private void ReportMissingSfx(SfxId id)
        {
            if (!_reportedSfx.Add(id)) return;
            _log?.Warn(LogChannel.Audio, $"Sfx '{id.Value}' has no catalog entry or no clip; it will stay silent until authored.");
        }

        private void ReportMissingMusic(MusicId id)
        {
            if (!_reportedMusic.Add(id)) return;
            _log?.Warn(LogChannel.Audio, $"Music '{id.Value}' is not in the AudioCatalog; no track will play.");
        }

        private void ReportMissingPool()
        {
            if (_poolReported) return;
            _poolReported = true;
            _log?.Error(LogChannel.Audio,
                $"Voice pool '{_config.voicePoolKey}' is missing or its prefab is not an AudioVoice. SFX are disabled.");
        }
    }
}
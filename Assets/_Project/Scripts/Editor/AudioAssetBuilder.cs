using System.Collections.Generic;
using System.IO;
using System.Text;
using Skyrift.Audio;
using Skyrift.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace Skyrift.Editor
{
    /// <summary>
    /// Authors the audio infrastructure and the placeholder bank (TECH_DOC §4.18): the AudioMixer with the
    /// four contracted buses, a clip for every <see cref="SfxIds"/> cue, the layered default music track, the
    /// <see cref="AudioCatalog"/> rows that bind them, and the pooled voice prefab.
    ///
    /// <b>Why the mixer is written as YAML.</b> Unity has no public API for authoring mixer groups or exposing
    /// parameters: <c>AudioMixerController</c>, <c>AudioMixerGroupController</c> and
    /// <c>AudioMixerEffectController</c> are all <c>internal</c> to UnityEditor.dll, and the only ways to reach
    /// them are reflection or the mixer window. Reflection into internal editor types breaks silently on a
    /// Unity upgrade, so the asset is written in its native serialized form instead — the same text Unity
    /// itself produces. The GUIDs below are fixed constants, so a rebuild is byte-identical and never churns
    /// version control.
    ///
    /// The mixer is created once and never rewritten: hand-added effects, sends and snapshots live in it.
    /// <see cref="Validate"/> checks it against <see cref="AudioConfig"/> so a renamed exposed parameter is
    /// reported rather than silently falling back to source gain.
    /// </summary>
    public static class AudioAssetBuilder
    {
        public const string MixerName = "GB_Mixer";
        public const string CatalogName = "GB_AudioCatalog";
        public const string VoicePrefabName = "gb_audio_voice";

        public static string MixerPath => SkyriftPaths.AudioMixers + "/" + MixerName + ".mixer";
        public static string CatalogPath => SkyriftPaths.DataAudio + "/" + CatalogName + ".asset";
        public static string VoicePrefabPath => SkyriftPaths.PrefabsAudio + "/" + VoicePrefabName + ".prefab";

        // Native class ids and local file ids. These are Unity's own identifiers for the mixer's sub-assets,
        // not Skyrift constants; they are fixed by the engine's serialization format.
        private const long ControllerFileId = 24100000;
        private const long SnapshotFileId = 24500000;
        private const long GroupFileIdBase = 24300000;
        private const long EffectFileIdBase = 24400000;

        private const string SnapshotGuid = "32a4c4ba4f775f8e84d4b8647d4a088c";

        /// <summary>
        /// Per-bus parameter GUIDs, indexed by <see cref="AudioBus"/>. A volume GUID is what an exposed
        /// parameter names and what a snapshot's float table keys on, so the three uses must be the same
        /// string — which is why they come from one table rather than being written out three times.
        /// </summary>
        private readonly struct BusSpec
        {
            public readonly string GroupGuid;
            public readonly string VolumeGuid;
            public readonly string PitchGuid;
            public readonly string EffectGuid;
            public readonly string MixLevelGuid;

            public BusSpec(string groupGuid, string volumeGuid, string pitchGuid, string effectGuid, string mixLevelGuid)
            {
                GroupGuid = groupGuid;
                VolumeGuid = volumeGuid;
                PitchGuid = pitchGuid;
                EffectGuid = effectGuid;
                MixLevelGuid = mixLevelGuid;
            }
        }

        private static BusSpec[] Buses()
        {
            return new[]
            {
                new BusSpec("06b6eb1b350c5b859059d79b9afac366", "0effa2ecac065da48813d5faa88d3264",
                    "e996f17231ac58409f21df9ab0455c28", "c9f716cc6f835f9e8d0f8a91e0ec102c",
                    "9401e2fb7594552eb918566d963eedfd"),

                new BusSpec("7bc27123b6cc51f585d1c0faed1b8441", "b56d73bbe71f560580ee825861ff0b4b",
                    "30d9b97c588553ab929b699b0bf1b1b4", "5ac5f170289354baa16f4da2764bbd7e",
                    "c7f140afb84e5bb7bda4e4de3e9e6151"),

                new BusSpec("a8f5b98b6a195b968ae0c794a170c3e6", "5a287eee96d055a8be2a6479fca420ea",
                    "a1489bb03748582b9de66a776cd78e36", "f8f9a5d75db55c1a8054d146f3c16f5d",
                    "a12791c4c9f35dfebd643ac27aaf66ea"),

                new BusSpec("d53bd43f30b35fc5b6512f613f6581bf", "6f17f5e29c245b5e8737832411ac33f6",
                    "6a1a9d081d49570eb75363e90d8455d4", "b7eef2d5e4a45d139114f49f08ce749b",
                    "6cb919320b0a5adfab2a8b32385ed863")
            };
        }

        /// <summary>
        /// Builds everything audio. <paramref name="config"/> supplies the exposed-parameter names and the
        /// default track id, so the mixer and the catalog cannot disagree with the data the service reads.
        /// </summary>
        public static void Run(AudioConfig config, bool overwrite = false)
        {
            AudioMixer mixer = BuildMixer(config);
            BuildClips(overwrite);
            BuildCatalog(config, overwrite);
            BuildVoicePrefab(mixer, overwrite);
            AssetAuthoring.SaveAll();

            Debug.Log("[Skyrift] Audio authored (mixer, " + SfxIds.All.Length + " placeholder cues, " +
                      PlaceholderAudio.Stems().Length + " music stems, voice prefab).");
        }

        // ── Mixer ───────────────────────────────────────────────────────────────────────────────────────────

        public static AudioMixer BuildMixer(AudioConfig config)
        {
            SkyriftPaths.EnsureFolder(SkyriftPaths.AudioMixers);

            AudioMixer existing = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (existing != null) return existing;

            string fullPath = AssetAuthoring.FullPath(MixerPath);
            if (fullPath == null)
            {
                Debug.LogError("[Skyrift] Cannot resolve a file-system path for the mixer; audio buses will fall back to source gain.");
                return null;
            }

            try
            {
                File.WriteAllText(fullPath, MixerYaml(config));
            }
            catch (IOException e)
            {
                Debug.LogError($"[Skyrift] Failed to write the mixer: {e.Message}");
                return null;
            }

            AssetDatabase.ImportAsset(MixerPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MixerPath);
            if (mixer == null)
                Debug.LogError("[Skyrift] The mixer was written but did not import. Check the Console for a YAML error.");

            return mixer;
        }

        /// <summary>
        /// Emits the mixer's native YAML: one controller, four groups each with an Attenuation effect, and one
        /// snapshot holding the authored defaults. Field names and the object header are Unity's own format.
        /// </summary>
        private static string MixerYaml(AudioConfig config)
        {
            BusSpec[] buses = Buses();
            var yaml = new StringBuilder(4096);

            yaml.Append("%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");

            yaml.Append("--- !u!241 &").Append(ControllerFileId).Append("\nAudioMixerController:\n");
            AppendHeader(yaml, 0, MixerName);
            yaml.Append("  m_OutputGroup: {fileID: 0}\n");
            yaml.Append("  m_MasterGroup: {fileID: ").Append(GroupFileIdBase).Append("}\n");
            yaml.Append("  m_Snapshots:\n  - {fileID: ").Append(SnapshotFileId).Append("}\n");
            yaml.Append("  m_StartSnapshot: {fileID: ").Append(SnapshotFileId).Append("}\n");
            // Suspend at the silence floor so an idle mixer stops costing DSP time, and Normal update mode:
            // the service writes exposed parameters from the presentation tick, not from a DSP callback.
            yaml.Append("  m_SuspendThreshold: -80\n  m_EnableSuspend: 1\n  m_UpdateMode: 0\n");

            yaml.Append("  m_ExposedParameters:\n");
            for (int i = 0; i < buses.Length; i++)
            {
                yaml.Append("  - guid: ").Append(buses[i].VolumeGuid).Append('\n')
                    .Append("    name: ").Append(ParameterFor(config, (AudioBus)i)).Append('\n');
            }

            // One view containing every group, so the mixer window opens showing the whole routing tree
            // rather than an empty view that looks like a broken asset.
            yaml.Append("  m_AudioMixerGroupViews:\n  - guids:\n");
            for (int i = 0; i < buses.Length; i++) yaml.Append("    - ").Append(buses[i].GroupGuid).Append('\n');
            yaml.Append("    name: View\n");
            yaml.Append("  m_CurrentViewIndex: 0\n");
            yaml.Append("  m_TargetSnapshot: {fileID: ").Append(SnapshotFileId).Append("}\n");

            for (int i = 0; i < buses.Length; i++)
            {
                bool master = i == (int)AudioBus.Master;
                long fileId = GroupFileIdBase + (i * 2);

                yaml.Append("--- !u!243 &").Append(fileId).Append("\nAudioMixerGroupController:\n");
                AppendHeader(yaml, 0, AudioMixerNames.ForBus((AudioBus)i));
                yaml.Append("  m_AudioMixer: {fileID: ").Append(ControllerFileId).Append("}\n");
                yaml.Append("  m_GroupID: ").Append(buses[i].GroupGuid).Append('\n');

                if (master)
                {
                    yaml.Append("  m_Children:\n");
                    for (int c = 1; c < buses.Length; c++)
                        yaml.Append("  - {fileID: ").Append(GroupFileIdBase + (c * 2)).Append("}\n");
                }
                else
                {
                    yaml.Append("  m_Children: []\n");
                }

                yaml.Append("  m_Volume: ").Append(buses[i].VolumeGuid).Append('\n');
                yaml.Append("  m_Pitch: ").Append(buses[i].PitchGuid).Append('\n');
                yaml.Append("  m_Effects:\n  - {fileID: ").Append(EffectFileIdBase + (i * 2)).Append("}\n");
                yaml.Append("  m_UserColorIndex: 0\n  m_Mute: 0\n  m_Solo: 0\n  m_BypassEffects: 0\n");

                AppendAttenuation(yaml, EffectFileIdBase + (i * 2), buses[i]);
            }

            yaml.Append("--- !u!245 &").Append(SnapshotFileId).Append("\nAudioMixerSnapshotController:\n");
            AppendHeader(yaml, 0, "Snapshot");
            yaml.Append("  m_AudioMixer: {fileID: ").Append(ControllerFileId).Append("}\n");
            yaml.Append("  m_SnapshotID: ").Append(SnapshotGuid).Append('\n');
            // 0 dB on every bus: the authored starting point. AudioService pushes the real levels from
            // AudioConfig at composition time, so these are defaults for the editor preview, not the mix.
            // Pitch GUIDs are deliberately absent, exactly as Unity's own mixer writes them: an omitted
            // parameter takes the engine default, which for pitch is 1.
            yaml.Append("  m_FloatValues:\n");
            for (int i = 0; i < buses.Length; i++) yaml.Append("    ").Append(buses[i].VolumeGuid).Append(": 0\n");
            yaml.Append("  m_TransitionOverrides: {}\n");

            return yaml.ToString();
        }

        private static void AppendHeader(StringBuilder yaml, int hideFlags, string name)
        {
            yaml.Append("  m_ObjectHideFlags: ").Append(hideFlags).Append('\n');
            yaml.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
            yaml.Append("  m_PrefabInstance: {fileID: 0}\n");
            yaml.Append("  m_PrefabAsset: {fileID: 0}\n");
            yaml.Append("  m_Name: ").Append(name).Append('\n');
        }

        private static void AppendAttenuation(StringBuilder yaml, long fileId, in BusSpec bus)
        {
            yaml.Append("--- !u!244 &").Append(fileId).Append("\nAudioMixerEffectController:\n");
            // HideFlags 3 (HideInHierarchy | HideInInspector): effects are sub-assets the mixer window draws
            // itself, and Unity marks them hidden so they do not clutter the project browser.
            AppendHeader(yaml, 3, string.Empty);
            yaml.Append("  m_EffectID: ").Append(bus.EffectGuid).Append('\n');
            yaml.Append("  m_EffectName: Attenuation\n");
            yaml.Append("  m_MixLevel: ").Append(bus.MixLevelGuid).Append('\n');
            yaml.Append("  m_Parameters: []\n");
            yaml.Append("  m_SendTarget: {fileID: 0}\n");
            yaml.Append("  m_EnableWetMix: 0\n  m_Bypass: 0\n");
        }

        private static string ParameterFor(AudioConfig config, AudioBus bus)
        {
            if (config == null)
            {
                // Only reachable when the mixer is built on its own. These mirror AudioConfig's defaults.
                switch (bus)
                {
                    case AudioBus.Music: return "MusicVolume";
                    case AudioBus.Sfx: return "SfxVolume";
                    case AudioBus.Ui: return "UiVolume";
                    default: return "MasterVolume";
                }
            }

            switch (bus)
            {
                case AudioBus.Music: return config.musicVolumeParam;
                case AudioBus.Sfx: return config.sfxVolumeParam;
                case AudioBus.Ui: return config.uiVolumeParam;
                default: return config.masterVolumeParam;
            }
        }

        // ── Clips ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Renders and imports every placeholder clip. Existing files are left alone unless a rebuild is
        /// requested, so a designer's replacement WAV at the same path is never overwritten by setup.
        /// </summary>
        private static void BuildClips(bool overwrite)
        {
            CueSpec[] cues = PlaceholderAudio.Cues();
            for (int i = 0; i < cues.Length; i++)
            {
                string path = SkyriftPaths.SfxClip(cues[i].id);
                if (!overwrite && SkyriftPaths.AssetExists(path)) continue;

                AudioClip clip = WavWriter.Write(path, PlaceholderAudio.RenderCue(cues[i]));
                if (clip == null) continue;   // WavWriter already reported the failure
                ConfigureImporter(path, false);
            }

            StemSpec[] stems = PlaceholderAudio.Stems();
            for (int i = 0; i < stems.Length; i++)
            {
                string path = SkyriftPaths.StemClip(stems[i].name);
                if (!overwrite && SkyriftPaths.AssetExists(path)) continue;

                AudioClip clip = WavWriter.Write(path, PlaceholderAudio.RenderStem(stems[i]));
                if (clip == null) continue;
                ConfigureImporter(path, true);
            }
        }

        /// <summary>
        /// Import settings are a performance contract, not a preference. Short SFX are decompressed on load so
        /// a cue never pays a decode cost on the frame it fires; long stems stay compressed in memory because
        /// four looping layers decompressed would be tens of megabytes of the §11.1 budget.
        /// </summary>
        private static void ConfigureImporter(string path, bool music)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[Skyrift] '{path}' has no AudioImporter; import settings were left at their defaults.");
                return;
            }

            importer.forceToMono = true;
            // Loaded synchronously and preloaded: the bank is small and fully known at boot, and a background
            // load would race the first cue of a run (which is exactly when a missing clip is most visible).
            importer.loadInBackground = false;
            importer.preloadAudioData = true;

            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            settings.sampleRateOverride = WavWriter.SampleRate;
            settings.loadType = music ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = music ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.PCM;
            settings.quality = music ? 0.7f : 1f;
            settings.preloadAudioData = true;
            importer.defaultSampleSettings = settings;

            // Android override: ADPCM for SFX. It is a fixed 4:1 compression with a trivial decoder and no
            // seek table, so a stolen-and-restarted voice resumes instantly — Vorbis would add a decode hop
            // to every coin in a line. Music keeps Vorbis, where the ratio is what matters.
            AudioImporterSampleSettings android = settings;
            android.compressionFormat = music ? AudioCompressionFormat.Vorbis : AudioCompressionFormat.ADPCM;
            android.quality = music ? 0.6f : 1f;
            importer.SetOverrideSampleSettings(BuildTargetGroup.Android, android);

            importer.SaveAndReimport();
        }

        // ── Catalog ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes the catalog rows. Additive and idempotent: a missing cue gets a row, an existing row is left
        /// alone unless a rebuild was requested. That means adding a constant to <see cref="SfxIds"/> and
        /// re-running setup completes the bank without discarding anyone's tuning.
        /// </summary>
        public static AudioCatalog BuildCatalog(AudioConfig config, bool overwrite = false)
        {
            AudioCatalog catalog = AssetAuthoring.Resolve<AudioCatalog>(CatalogPath, overwrite);
            if (catalog.sfx == null) catalog.sfx = new List<SfxDefinition>();
            if (catalog.music == null) catalog.music = new List<MusicTrack>();

            CueSpec[] cues = PlaceholderAudio.Cues();
            for (int i = 0; i < cues.Length; i++)
            {
                CueSpec cue = cues[i];
                SfxDefinition row = FindSfx(catalog, cue.id);
                bool fresh = row == null;
                if (fresh)
                {
                    row = new SfxDefinition { id = cue.id };
                    catalog.sfx.Add(row);
                }
                else if (!overwrite)
                {
                    continue;
                }

                AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SkyriftPaths.SfxClip(cue.id));
                if (clip == null && fresh)
                    Debug.LogWarning($"[Skyrift] Cue '{cue.id}' has no clip on disk; the row was authored silent.");

                row.clip = clip;
                row.bus = cue.bus;
                row.volume = cue.volume;
                row.pitch = cue.pitch;
                row.pitchVariance = cue.pitchVariance;
                row.voiceCap = cue.voiceCap;
                row.cooldown = cue.cooldown;

                // M1 is a 2D placeholder bank. Spatial cues arrive with the real art in M6, and the authored
                // distances are left at their defaults so turning one on is a single checkbox.
                row.spatial = false;
            }

            string trackId = config != null ? config.defaultMusicId : "music_run_default";
            MusicTrack track = FindMusic(catalog, trackId);
            bool freshTrack = track == null;
            if (freshTrack)
            {
                track = new MusicTrack { id = trackId };
                catalog.music.Add(track);
            }

            if (freshTrack || overwrite)
            {
                track.volume = 1f;
                track.loop = true;
                track.stems = new List<MusicStem>();

                StemSpec[] stems = PlaceholderAudio.Stems();
                for (int i = 0; i < stems.Length; i++)
                {
                    track.stems.Add(new MusicStem
                    {
                        clip = AssetDatabase.LoadAssetAtPath<AudioClip>(SkyriftPaths.StemClip(stems[i].name)),
                        fadeInAt = stems[i].fadeInAt,
                        fullAt = stems[i].fullAt,
                        volume = stems[i].volume
                    });
                }
            }

            // The indices are lazy and keyed on id; a row written through the serializer would otherwise keep
            // serving the previous lookup for the rest of the editor session.
            catalog.InvalidateIndex();
            AssetAuthoring.Touch(catalog);
            return catalog;
        }

        private static SfxDefinition FindSfx(AudioCatalog catalog, string id)
        {
            for (int i = 0; i < catalog.sfx.Count; i++)
            {
                if (catalog.sfx[i] != null && string.Equals(catalog.sfx[i].id, id, System.StringComparison.Ordinal))
                    return catalog.sfx[i];
            }

            return null;
        }

        private static MusicTrack FindMusic(AudioCatalog catalog, string id)
        {
            for (int i = 0; i < catalog.music.Count; i++)
            {
                if (catalog.music[i] != null && string.Equals(catalog.music[i].id, id, System.StringComparison.Ordinal))
                    return catalog.music[i];
            }

            return null;
        }

        // ── Voice prefab ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The pooled SFX voice. One AudioSource, no renderer and no collider: a voice is invisible and its
        /// position only matters when a cue is spatial, which <c>AudioVoice.Play</c> sets per cue.
        /// </summary>
        public static AudioVoice BuildVoicePrefab(AudioMixer mixer, bool overwrite = false)
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(VoicePrefabPath);
            if (existing != null && !overwrite) return existing.GetComponent<AudioVoice>();

            var instance = new GameObject(VoicePrefabName);

            AudioSource source = instance.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            // No doppler on a runner: the player and every cue move together, so a pitch shift from relative
            // velocity would be an artefact rather than an effect.
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.volume = 1f;
            source.pitch = 1f;

            if (mixer != null)
            {
                // FindMatchingGroups allocates; this is authoring time, and the service resolves its own
                // groups once at composition. SFX is only the inspector default — Play sets the real bus.
                AudioMixerGroup[] groups = mixer.FindMatchingGroups(AudioMixerNames.Sfx);
                if (groups != null && groups.Length > 0) source.outputAudioMixerGroup = groups[0];
            }

            instance.AddComponent<AudioVoice>();

            GameObject prefab = AssetAuthoring.SavePrefab(instance, VoicePrefabPath, overwrite);
            return prefab != null ? prefab.GetComponent<AudioVoice>() : null;
        }

        // ── Validation ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Authoring-time checks the runtime cannot make cheaply: that the mixer really exposes the contracted
        /// buses and parameters, that every cue id has a row, and that the music stems are sample-aligned.
        /// </summary>
        public static void Validate(AudioMixer mixer, AudioCatalog catalog, AudioConfig config, ValidationReport report)
        {
            const string t = nameof(AudioAssetBuilder);

            if (mixer == null)
            {
                report.Error(t, MixerName, "mixer", "No AudioMixer. Run Tools > Skyrift > Build M1 Greybox.");
            }
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    var bus = (AudioBus)i;
                    string groupName = AudioMixerNames.ForBus(bus);

                    // FindMatchingGroups allocates, which is why this is a validator and not a runtime check.
                    AudioMixerGroup[] groups = mixer.FindMatchingGroups(groupName);
                    if (groups == null || groups.Length == 0)
                        report.Error(t, MixerName, "bus", $"Mixer has no '{groupName}' group; that bus cannot be routed.");

                    string parameter = ParameterFor(config, bus);
                    if (string.IsNullOrEmpty(parameter))
                    {
                        report.Error(t, MixerName, "param", $"AudioConfig has an empty exposed-parameter name for the {bus} bus.");
                    }
                    else if (!mixer.GetFloat(parameter, out _))
                    {
                        report.Error(t, MixerName, "param",
                            $"Mixer does not expose '{parameter}'. Delete {MixerPath} and rebuild, or rename the parameter in the mixer window.");
                    }
                }
            }

            if (catalog == null)
            {
                report.Error(t, CatalogName, "catalog", "No AudioCatalog. Run Tools > Skyrift > Build M1 Greybox.");
                return;
            }

            catalog.Validate(report);

            for (int i = 0; i < SfxIds.All.Length; i++)
            {
                if (FindSfx(catalog, SfxIds.All[i]) == null)
                    report.Error(t, SfxIds.All[i], "sfx_coverage",
                        $"'{SfxIds.All[i]} is a contracted cue id but has no catalog row; it would be silent in game.");
            }

            string trackId = config != null ? config.defaultMusicId : "music_run_default";
            MusicTrack track = FindMusic(catalog, trackId);
            if (track == null)
            {
                report.Error(t, trackId, "music_default", $"AudioConfig.defaultMusicId '{trackId}' has no track in the catalog.");
                return;
            }

            ValidateStemAlignment(track, report);

            GameObject voicePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VoicePrefabPath);
            if (voicePrefab == null)
            {
                report.Error(t, VoicePrefabName, "voice_prefab", "The pooled voice prefab is missing; SFX would be disabled.");
            }
            else if (voicePrefab.GetComponent<AudioVoice>() == null || voicePrefab.GetComponent<AudioSource>() == null)
            {
                report.Error(t, VoicePrefabName, "voice_prefab",
                    "The voice prefab needs both AudioVoice and AudioSource; the pool would hand back an unusable entity.");
            }
        }

        /// <summary>
        /// Every stem of a track must be the same length. <c>BindBank</c> schedules them at one dspTime and
        /// loops each independently, so a stem one sample longer slips against the others on every repeat —
        /// a drift that is inaudible in a short editor preview and obvious after a two-minute run.
        /// </summary>
        private static void ValidateStemAlignment(MusicTrack track, ValidationReport report)
        {
            if (track.stems == null || track.stems.Count == 0) return;

            float reference = -1f;
            for (int i = 0; i < track.stems.Count; i++)
            {
                MusicStem stem = track.stems[i];
                if (stem == null || stem.clip == null) continue;

                float length = stem.clip.length;
                if (reference < 0f) { reference = length; continue; }

                if (Mathf.Abs(length - reference) > 0.001f)
                {
                    report.Error(nameof(AudioAssetBuilder), track.id, "stem_alignment",
                        $"stems[{i}] is {length:F4}s but the first stem is {reference:F4}s; looping stems must match exactly.");
                }
            }
        }
    }
}
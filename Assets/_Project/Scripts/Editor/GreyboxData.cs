using System;
using System.Collections.Generic;
using Skyrift.Core.Pooling;
using Skyrift.Data;
using Skyrift.Gameplay;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// The seven config assets. Returned as one bundle because three builders need them and none should
    /// re-load them from disk by guessing paths (<see cref="GreyboxPrefabs"/> needs the lane geometry,
    /// <see cref="AudioAssetBuilder"/> needs the mixer parameter names, <see cref="BuildContent"/> needs the
    /// voice pool key and the default track id).
    /// </summary>
    public sealed class GreyboxConfigBundle
    {
        public BalanceConfig balance;
        public TrackGenerationConfig trackGeneration;
        public InputConfig input;
        public ScoringConfig scoring;
        public ContinueConfig continueConfig;
        public AudioConfig audio;
    }

    /// <summary>What <see cref="GreyboxData.BuildContent"/> produced, for the scene builder and the validator.</summary>
    public sealed class GreyboxContentBundle
    {
        public CatalogManifest manifest;
        public ObstacleBehaviourCatalog behaviours;
        public WorldDefinition world;
        public ChunkDefinition[] chunks;
        public ObstacleDefinition[] obstacles;
    }

    /// <summary>
    /// Authors the M1 data assets from <see cref="GreyboxContent"/> (TECH_DOC §4.20, §5.3–5.5, §5.13).
    ///
    /// <b>Authoring order is a contract, not a preference.</b> Three builders depend on each other's output,
    /// so the setup tool runs them in this sequence and each phase's dependencies are already on disk:
    /// <list type="number">
    /// <item><see cref="BuildConfigs"/> — the configs are leaves; nothing else can be authored without them.</item>
    /// <item><see cref="GreyboxPrefabs.Run"/> — geometry reads <c>laneWidth</c> and the player volume.</item>
    /// <item><see cref="AudioAssetBuilder.Run"/> — the mixer and catalog read <c>AudioConfig</c>, and it builds
    /// the voice prefab the PoolCatalog has to reference.</item>
    /// <item><see cref="BuildContent"/> — definitions reference the prefabs and the audio id by path.</item>
    /// <item>Scene building and validation, which need the manifest to exist.</item>
    /// </list>
    /// Running them out of order does not corrupt anything — every step is idempotent — but it authors rows
    /// against a prefab that is not there yet, and the validator reports a missing reference instead of the
    /// real cause, which is a wasted debugging session.
    ///
    /// <b>Where defaults live.</b> A config asset gets only its id and localization key: every number comes
    /// from the field initialisers on the <see cref="DefinitionBase"/> class in the Data assembly. Duplicating
    /// them here would put the same constant in two places that can disagree, and the Data copy is the one the
    /// runtime reads when an asset is missing. The asset exists so Remote Config has something to override at
    /// run time (§9), not to restate what is already authored.
    ///
    /// <b>Naming.</b> Content assets are named after their <c>id</c> rather than carrying the <c>gb_</c>
    /// prefix, because the id is the address that saves, analytics and other definitions reference — a prefix
    /// would break the "file name matches id" convention for a cosmetic gain. The prefix marks art and scene
    /// objects a human replaces; a greybox <em>data row</em> is retired by deleting it from the manifest, which
    /// is why ownership is declared by the manifest listing rather than by the file name.
    /// </summary>
    public static class GreyboxData
    {
        // ── Paths ───────────────────────────────────────────────────────────────────────────────────────
        // Public so the scene builder and the validator resolve the same asset from the same constant instead
        // of each keeping a copy of the string.

        public const string BalancePath = SkyriftPaths.DataBalance + "/cfg_balance.asset";
        public const string TrackGenerationPath = SkyriftPaths.DataTrack + "/cfg_track_generation.asset";
        public const string InputPath = SkyriftPaths.DataInput + "/cfg_input.asset";
        public const string ScoringPath = SkyriftPaths.DataScoring + "/cfg_scoring.asset";
        public const string ContinuePath = SkyriftPaths.DataContinue + "/cfg_continue.asset";
        public const string AudioPath = SkyriftPaths.DataAudio + "/cfg_audio.asset";
        public const string PoolsPath = SkyriftPaths.DataCatalogs + "/cfg_pools.asset";
        public const string ManifestPath = SkyriftPaths.DataCatalogs + "/cfg_manifest.asset";
        public const string BehaviourCatalogPath = SkyriftPaths.DataCatalogs + "/obstacle_behaviours.asset";

        public static string WorldPath(string id) => SkyriftPaths.DataWorlds + "/" + id + ".asset";
        public static string ChunkPath(string id) => SkyriftPaths.DataChunks + "/" + id + ".asset";
        public static string ObstaclePath(string id) => SkyriftPaths.DataObstacles + "/" + id + ".asset";
        public static string BehaviourPath(string id) => SkyriftPaths.DataBehaviours + "/" + id + ".asset";

        /// <summary>
        /// Late-run difficulty the world adds on top of the distance ramp. Small and deliberate: the ramp is
        /// already authored in <see cref="TrackGenerationConfig"/>, so a large offset here would make the two
        /// systems fight and put the hardest chunk on the player before they have seen the easiest ones.
        /// </summary>
        private const float WorldDifficultyBias = 0.15f;

        // ── Phase 1: configs ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the seven config assets. Existing ones are returned untouched unless
        /// <paramref name="overwrite"/> is set, so a designer tuning lane width in the Inspector survives
        /// every later rebuild of the greybox.
        /// </summary>
        public static GreyboxConfigBundle BuildConfigs(bool overwrite = false)
        {
            SkyriftPaths.EnsureAllFolders();

            var bundle = new GreyboxConfigBundle
            {
                balance = Author<BalanceConfig>(BalancePath, overwrite, "cfg_balance", "config.balance.name", null),
                trackGeneration = Author<TrackGenerationConfig>(TrackGenerationPath, overwrite, "cfg_track_generation", "config.track.name", null),
                input = Author<InputConfig>(InputPath, overwrite, "cfg_input", "config.input.name", null),
                scoring = Author<ScoringConfig>(ScoringPath, overwrite, "cfg_scoring", "config.scoring.name", null),
                continueConfig = Author<ContinueConfig>(ContinuePath, overwrite, "cfg_continue", "config.continue.name", null),
                audio = Author<AudioConfig>(AudioPath, overwrite, "cfg_audio", "config.audio.name", null)
            };

            AssetAuthoring.SaveAll();
            return bundle;
        }

        // ── Phase 4: content ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Authors definitions, behaviours, pools and the manifest. Call after the config, prefab and audio
        /// phases; see the ordering contract on this class.
        /// </summary>
        public static GreyboxContentBundle BuildContent(GreyboxConfigBundle configs, bool overwrite = false)
        {
            if (configs == null || configs.balance == null || configs.audio == null)
            {
                Debug.LogError("[Skyrift] GreyboxData.BuildContent needs the config bundle from BuildConfigs. " +
                               "Run Tools > Skyrift > Build M1 Greybox, which orders all four phases.");
                return null;
            }

            // Pick up anything the earlier phases wrote to disk before we reference it. Without the refresh,
            // an overwrite rebuild can hand LoadAssetAtPath an object the previous phase just replaced, and the
            // PoolCatalog would then hold a reference to a destroyed prefab.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            ObstacleDefinition[] obstacles = BuildObstacles(overwrite);
            ChunkDefinition[] chunks = BuildChunks(configs.balance, overwrite);
            WorldDefinition world = BuildWorld(configs, overwrite);
            ObstacleBehaviourCatalog behaviours = BuildBehaviours(overwrite);
            PoolCatalog pools = BuildPools(configs, overwrite);
            CatalogManifest manifest = BuildManifest(configs, world, chunks, obstacles, pools, overwrite);

            AssetAuthoring.SaveAll();
            Debug.Log($"[Skyrift] Greybox data authored: 1 world, {chunks.Length} chunk(s), {obstacles.Length} " +
                      $"obstacle(s), {pools.entries.Count} pool(s), manifest '{manifest.contentVersion}'.");

            return new GreyboxContentBundle
            {
                manifest = manifest,
                behaviours = behaviours,
                world = world,
                chunks = chunks,
                obstacles = obstacles
            };
        }

        private static ObstacleDefinition[] BuildObstacles(bool overwrite)
        {
            ObstacleSpec[] specs = GreyboxContent.Obstacles();
            var definitions = new ObstacleDefinition[specs.Length];

            for (int i = 0; i < specs.Length; i++)
            {
                ObstacleSpec spec = specs[i];
                definitions[i] = Author<ObstacleDefinition>(
                    ObstaclePath(spec.id), overwrite, spec.id, spec.displayNameKey,
                    def =>
                    {
                        def.poolKey = spec.poolKey;
                        // The address M3 registers this prefab under. It is the asset path because that is
                        // Addressables' default addressing, so the value cannot be wrong when the group is
                        // created — and it is derived from the same constant GreyboxPrefabs wrote to.
                        def.prefabAddress = GreyboxPrefabs.ObstaclePrefabPath(spec.poolKey);
                        def.behaviourId = spec.behaviourId;
                        def.tagSet = spec.tags;
                        def.clearance = spec.clearance;
                        def.footprintMetres = spec.footprint;
                        def.isMoving = spec.isMoving;
                        def.motion = spec.motion;
                        def.launchHeightMultiplier = spec.launchHeightMultiplier;
                        def.impactSfxId = spec.impactSfxId;
                        def.spawnWeightMultiplier = spec.spawnWeightMultiplier;
                        // crashFxKey stays empty until the VFX catalog exists (M6). A missing key is not an
                        // error anywhere in M1, whereas pointing at a catalog that does not exist would be.
                    });
            }

            return definitions;
        }

        private static ChunkDefinition[] BuildChunks(BalanceConfig balance, bool overwrite)
        {
            ChunkSpec[] specs = GreyboxContent.Chunks();
            var definitions = new ChunkDefinition[specs.Length];

            for (int i = 0; i < specs.Length; i++)
            {
                ChunkSpec spec = specs[i];
                definitions[i] = Author<ChunkDefinition>(
                    ChunkPath(spec.id), overwrite, spec.id, spec.displayNameKey,
                    def =>
                    {
                        def.poolKey = spec.poolKey;
                        def.prefabAddress = GreyboxPrefabs.ChunkPrefabPath(spec.poolKey);
                        def.worldTags = new[] { GreyboxContent.WorldTag };
                        def.lengthMetres = spec.length;
                        def.difficultyBand = spec.difficultyBand;
                        def.baseWeight = spec.baseWeight;
                        def.cooldownChunks = spec.cooldownChunks;
                        def.safeFallback = spec.safeFallback;

                        // Wide on purpose. Restricting entry and exit lanes is a real design decision about
                        // how a chunk is entered, and it cannot be derived from where the obstacles sit — a
                        // barrier in lane 0 does not forbid entering in lane 0, it just means you must move.
                        int openMask = OpenLaneMask(balance.laneCount);
                        def.entryLaneMask = openMask;
                        def.exitLaneMask = openMask;

                        def.obstacleSlots = Slots(spec.slots);
                    });
            }

            return definitions;
        }

        private static ObstacleSlotData[] Slots(SlotSpec[] slots)
        {
            if (slots == null) return Array.Empty<ObstacleSlotData>();

            var rows = new ObstacleSlotData[slots.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                rows[i] = new ObstacleSlotData
                {
                    lane = slots[i].lane,
                    zOffset = slots[i].z,
                    obstacleTag = slots[i].tag,
                    required = slots[i].required,
                    spawnChance = slots[i].chance
                };
            }
            return rows;
        }

        /// <summary>Every lane the world has, from <c>laneCount</c> rather than a literal, so a 4-lane world needs no edit here.</summary>
        private static int OpenLaneMask(int laneCount)
        {
            int mask = 0;
            for (int lane = 0; lane < laneCount; lane++) mask |= 1 << lane;
            return mask;
        }

        private static WorldDefinition BuildWorld(GreyboxConfigBundle configs, bool overwrite)
        {
            AudioConfig audio = configs.audio;

            return Author<WorldDefinition>(
                WorldPath(GreyboxContent.WorldId), overwrite, GreyboxContent.WorldId, "world.skyrift.name",
                world =>
                {
                    world.assetPackLabel = GreyboxContent.WorldPackLabel;
                    world.chunkTagFilter = new[] { GreyboxContent.WorldTag };
                    world.difficultyCurve = AnimationCurve.Linear(0f, 0f, 1f, WorldDifficultyBias);

                    // Read from the config instead of repeating the literal: AudioAssetBuilder authors the
                    // track under exactly this id, so a hand-edit to defaultMusicId moves both sides.
                    world.musicId = audio.defaultMusicId;

                    world.fogColor = GreyboxContent.Fog;
                    world.groundColor = GreyboxContent.Ground;
                    world.accentColor = GreyboxContent.Accent;
                    world.ambientColor = GreyboxContent.Ambient;
                });
        }

        // ── Behaviours ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Three behaviour assets and the catalog that maps <c>ObstacleDefinition.behaviourId</c> onto them.
        /// Six archetypes share three behaviours, which is the §7.3 claim made concrete: a fourth barrier
        /// variant is a data row, not a class.
        /// </summary>
        private static ObstacleBehaviourCatalog BuildBehaviours(bool overwrite)
        {
            // Behaviours are plain ScriptableObjects (§4.7: stateless strategy assets), so they carry no id
            // field — the catalog's entry is what binds a behaviourId string to the asset.
            StandardObstacleBehaviour standard = AuthorAsset<StandardObstacleBehaviour>(
                BehaviourPath(GreyboxContent.BehaviourStandard), overwrite, null);
            MovingObstacleBehaviour moving = AuthorAsset<MovingObstacleBehaviour>(
                BehaviourPath(GreyboxContent.BehaviourMoving), overwrite, null);
            RampObstacleBehaviour ramp = AuthorAsset<RampObstacleBehaviour>(
                BehaviourPath(GreyboxContent.BehaviourRamp), overwrite, null);

            ObstacleBehaviourCatalog catalog = AuthorAsset<ObstacleBehaviourCatalog>(
                BehaviourCatalogPath, overwrite, c =>
                {
                    c.entries.Clear();
                    c.entries.Add(new ObstacleBehaviourCatalog.Entry { behaviourId = GreyboxContent.BehaviourStandard, behaviour = standard });
                    c.entries.Add(new ObstacleBehaviourCatalog.Entry { behaviourId = GreyboxContent.BehaviourMoving, behaviour = moving });
                    c.entries.Add(new ObstacleBehaviourCatalog.Entry { behaviourId = GreyboxContent.BehaviourRamp, behaviour = ramp });
                });

            // The catalog caches its id→asset index and only clears it from OnValidate, which a script write
            // does not fire. Without this the first run after a rebuild resolves behaviour ids against the
            // index built from the previous content.
            catalog?.InvalidateIndex();

            return catalog;
        }

        // ── Pools ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One row per pooled prefab. Player and chaser are absent by design — they are scene objects, not
        /// pooled entities (see the note in <see cref="GreyboxContent"/>).
        /// </summary>
        private static PoolCatalog BuildPools(GreyboxConfigBundle configs, bool overwrite)
        {
            return Author<PoolCatalog>(PoolsPath, overwrite, "cfg_pools", "config.pools.name", catalog =>
            {
                catalog.entries.Clear();

                ChunkSpec[] chunks = GreyboxContent.Chunks();
                for (int i = 0; i < chunks.Length; i++)
                {
                    AddEntry(catalog.entries, chunks[i].poolKey,
                        GreyboxPrefabs.ChunkPrefabPath(chunks[i].poolKey), chunks[i].prewarm);
                }

                ObstacleSpec[] obstacles = GreyboxContent.Obstacles();
                for (int i = 0; i < obstacles.Length; i++)
                {
                    AddEntry(catalog.entries, obstacles[i].poolKey,
                        GreyboxPrefabs.ObstaclePrefabPath(obstacles[i].poolKey), obstacles[i].prewarm);
                }

                // The voice pool's prewarm is its hard cap: AudioService treats exhaustion as "steal the
                // eldest voice", so the two counts must agree or the soft cap silently never applies. Read
                // from the config rather than typed, which is the only way it stays true after a tune.
                string voiceKey = configs.audio.voicePoolKey;
                if (string.IsNullOrEmpty(voiceKey)) voiceKey = "audio_voice";
                AddEntry(catalog.entries, voiceKey, AudioAssetBuilder.VoicePrefabPath,
                    Mathf.Max(1, configs.audio.maxConcurrentVoices));
            });
        }

        private static void AddEntry(List<PoolEntry> entries, string key, string prefabPath, int prewarm)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

            // PooledEntity, not GameObject: the pool instantiates whatever component is assigned and every
            // consumer casts the result, so a root without the component is a wiring mistake worth naming.
            PooledEntity entity = prefab != null ? prefab.GetComponent<PooledEntity>() : null;

            if (entity == null)
            {
                Debug.LogError($"[Skyrift] Pool '{key}' has no prefab at '{prefabPath}'. Build the prefabs " +
                               "before the content, or the catalog will fail validation with 'no prefab'.");
                return;
            }

            entries.Add(new PoolEntry { key = key, prefab = entity, prewarm = Mathf.Max(0, prewarm) });
        }

        // ── Manifest ────────────────────────────────────────────────────────────────────────────────────

        private static CatalogManifest BuildManifest(GreyboxConfigBundle configs, WorldDefinition world,
            ChunkDefinition[] chunks, ObstacleDefinition[] obstacles, PoolCatalog pools, bool overwrite)
        {
            return Author<CatalogManifest>(ManifestPath, overwrite, "cfg_manifest", "config.manifest.name", manifest =>
            {
                // 0.1.0 for M1. Bumped when content changes, and reported through Remote Config so live-ops
                // can gate a feature on the catalog a build actually ships (§9).
                manifest.contentVersion = "0.1.0";

                manifest.worlds = new[] { world };
                manifest.chunks = chunks;
                manifest.obstacles = obstacles;

                manifest.balance = configs.balance;
                manifest.trackGeneration = configs.trackGeneration;
                manifest.input = configs.input;
                manifest.scoring = configs.scoring;
                manifest.poolCatalog = pools;
                manifest.continueConfig = configs.continueConfig;
                manifest.audio = configs.audio;
            });
        }

        // ── Authoring primitives ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads or creates a definition and writes its fields. When the asset already exists and
        /// <paramref name="overwrite"/> is false it is returned <b>untouched</b> — the writer callback does not
        /// run at all. That is what makes re-running setup safe after a designer has tuned a value, and it is
        /// the same rule <see cref="GreyboxPrefabs"/> follows for geometry.
        /// </summary>
        private static T Author<T>(string path, bool overwrite, string id, string displayNameKey, Action<T> write)
            where T : DefinitionBase
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null && !overwrite) return existing;

            T asset = AssetAuthoring.Resolve<T>(path, true);

            // Identity first: an id written by the callback would otherwise be overwritten by this. Both
            // fields are private on DefinitionBase by design, so the serialized property is the only route.
            if (!string.IsNullOrEmpty(id)) AssetAuthoring.SetIdentity(asset, id, displayNameKey);

            write?.Invoke(asset);
            AssetAuthoring.Touch(asset);
            return asset;
        }

        /// <summary><see cref="Author{T}"/> for a ScriptableObject that is not a definition, such as the behaviour catalog.</summary>
        private static T AuthorAsset<T>(string path, bool overwrite, Action<T> write) where T : ScriptableObject
        {
            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null && !overwrite) return existing;

            T asset = AssetAuthoring.Resolve<T>(path, true);
            write?.Invoke(asset);
            AssetAuthoring.Touch(asset);
            return asset;
        }
    }
}

using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Randomization;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Track
{
    /// <summary>
    /// Weighted, difficulty-aware chunk generator (TECH_DOC §4.5, ADR-0005).
    ///
    /// Determinism: the only source of randomness is the injected <see cref="IRandomProvider"/>, and every
    /// draw happens in a fixed order (eligibility → weights → pick → populate), so the same seed, catalogs
    /// and Remote Config values always produce the same track.
    ///
    /// Allocation: all scratch buffers are sized once in <c>Initialize</c> (a loading-phase operation).
    /// <c>EnsureCoverage</c>/<c>Recycle</c> only read and write them, so the per-frame path is GC-free.
    /// </summary>
    public sealed class TrackGenerator : ITrackGenerator, IOriginShiftListener
    {
        private readonly List<IChunkEligibilityRule> _eligibility = new List<IChunkEligibilityRule>(8);
        private readonly List<IChunkSelectionWeight> _weights = new List<IChunkSelectionWeight>(8);
        private readonly List<ChunkHandle> _live = new List<ChunkHandle>(16);

        private int[] _candidateIndices;
        private float[] _candidateWeights;
        private int[] _lastUsedSequence;
        private int[] _recent;

        private TrackGenerationConfig _config;
        private WorldDefinition _world;
        private BalanceConfig _balance;
        private IGameDatabase _database;
        private IRandomProvider _random;
        private IPoolRegistry _pools;
        private IEventBus _bus;
        private ILog _log;
        private IWorldOriginShifter _origin;
        private IChunkPopulator _populator;
        private Transform _trackRoot;

        private int _recentHead;
        private int _recentCount;
        private int _sequence;
        private int _generated;
        private int _recycled;
        private int _fallbacks;
        private int _allLanesMask;
        private double _headDistance;
        private float _playerSpeed;
        private float _difficultyScale = 1f;
        private float _lastPlayerDistance;
        private float _targetDifficulty;
        private bool _initialized;
        private bool _missingPoolReported;

        public int LiveCount => _live.Count;

        public TrackDebugState Debug => new TrackDebugState(
            _live.Count, _generated, _recycled, _fallbacks, _targetDifficulty, _headDistance,
            _live.Count > 0 ? _live[_live.Count - 1].Id : StableId.None);

        /// <param name="registerDefaultRules">
        /// False gives an empty pipeline so a project (or a test) can supply its own rules without
        /// inheriting the built-ins. The built-ins are ordinary rule objects, never special cases.
        /// </param>
        public TrackGenerator(bool registerDefaultRules = true)
        {
            if (!registerDefaultRules) return;

            _eligibility.Add(new ChunkEligibilityRules.WorldTag());
            _eligibility.Add(new ChunkEligibilityRules.DifficultyBand());
            _eligibility.Add(new ChunkEligibilityRules.LaneContinuity());
            _eligibility.Add(new ChunkEligibilityRules.Cooldown());

            _weights.Add(new ChunkSelectionWeights.BaseWeight());
            _weights.Add(new ChunkSelectionWeights.RepeatPenalty());
            _weights.Add(new ChunkSelectionWeights.DifficultyFit());
        }

        public void AddEligibilityRule(IChunkEligibilityRule rule)
        {
            if (rule != null) _eligibility.Add(rule);
        }

        public void AddSelectionWeight(IChunkSelectionWeight weight)
        {
            if (weight != null) _weights.Add(weight);
        }

        public void Initialize(in TrackRunContext ctx)
        {
            Shutdown();

            _config = ctx.Config;
            _world = ctx.World;
            _database = ctx.Database;
            _random = ctx.Random;
            _pools = ctx.Pools;
            _bus = ctx.Bus;
            _log = ctx.Log;
            _origin = ctx.Origin;
            _populator = ctx.Populator;
            _trackRoot = ctx.TrackRoot;
            _balance = ctx.Database != null ? ctx.Database.Balance : null;

            if (_config == null || _database == null || _random == null || _pools == null)
            {
                _log?.Error(LogChannel.Track, "TrackGenerator.Initialize needs config, database, random and pools.");
                return;
            }

            int repoCount = Mathf.Max(1, _database.Chunks.Count);
            _candidateIndices = new int[repoCount];
            _candidateWeights = new float[repoCount];
            _lastUsedSequence = new int[repoCount];
            for (int i = 0; i < repoCount; i++) _lastUsedSequence[i] = -1;

            _recent = new int[Mathf.Max(1, _config.repeatWindow)];
            _recentHead = 0;
            _recentCount = 0;

            int laneCount = _balance != null ? _balance.laneCount : 3;
            _allLanesMask = (1 << Mathf.Clamp(laneCount, 1, 30)) - 1;

            _sequence = 0;
            _generated = 0;
            _recycled = 0;
            _fallbacks = 0;
            _playerSpeed = 0f;
            _difficultyScale = 1f;
            _lastPlayerDistance = 0f;
            _targetDifficulty = 0f;
            _missingPoolReported = false;

            _origin?.Configure(_config.originShiftThreshold);
            _origin?.RegisterListener(this);

            // Start the head behind the player so the opening frame already has the configured runway
            // (the chaser needs somewhere to stand). These first chunks are generated at difficulty 0.
            _headDistance = -(double)(_config.chunksBehind * SafeLength());

            _initialized = true;
            EnsureCoverage(0f);
        }

        public void SetPlayerSpeed(float metresPerSecond)
        {
            _playerSpeed = metresPerSecond > 0f ? metresPerSecond : 0f;
        }

        public void SetDifficultyScale(float scale)
        {
            _difficultyScale = scale > 0f ? scale : 1f;
        }

        public ChunkHandle GetLive(int index) => _live[index];

        public ChunkHandle PeekNext(int lookahead = 1)
        {
            if (!_initialized || _live.Count == 0) return default;

            int current = 0;
            while (current < _live.Count && _live[current].EndDistance <= _lastPlayerDistance) current++;

            int index = current + Mathf.Max(1, lookahead) - 1;
            return index < _live.Count ? _live[index] : default;
        }

        public void EnsureCoverage(float playerDistance)
        {
            _lastPlayerDistance = playerDistance;
            if (!_initialized) return;

            int ahead = 0;
            for (int i = 0; i < _live.Count; i++)
                if (_live[i].EndDistance > playerDistance) ahead++;

            // Hard guard: a mis-authored zero-length chunk must never spin the frame loop.
            int guard = _config.chunksAhead + _config.chunksBehind + 4;
            while (ahead < _config.chunksAhead && guard-- > 0)
            {
                ChunkHandle handle = GenerateNext();
                if (!handle.IsValid) break;
                ahead++;
            }
        }

        public void Recycle(float playerDistance)
        {
            if (!_initialized) return;

            // Live chunks are ordered by distance, so everything behind the player is a prefix.
            int behind = 0;
            while (behind < _live.Count && _live[behind].EndDistance <= playerDistance) behind++;

            while (behind > _config.chunksBehind && _live.Count > 0)
            {
                ReleaseAt(0);
                behind--;
            }
        }

        public void Shutdown()
        {
            for (int i = _live.Count - 1; i >= 0; i--) ReleaseAt(i);
            _live.Clear();
            _origin?.UnregisterListener(this);
            _initialized = false;
        }

        /// <summary>
        /// Track roots are not shifted by the origin shifter; instead every live chunk is re-based here so
        /// its local Z stays inside the streaming window and never grows with run distance.
        /// </summary>
        public void OnOriginShifted(double deltaMetres)
        {
            float dz = (float)deltaMetres;
            for (int i = 0; i < _live.Count; i++)
            {
                ChunkRoot root = _live[i].Root;
                if (root == null) continue;

                Vector3 p = root.CachedTransform.localPosition;
                p.z -= dz;
                root.CachedTransform.localPosition = p;
            }
        }

        private ChunkHandle GenerateNext()
        {
            float target = ComputeTargetDifficulty();
            _targetDifficulty = target;

            int previousExit = _live.Count > 0 ? _live[_live.Count - 1].ExitLaneMask : _allLanesMask;
            IReadOnlyList<ChunkDefinition> all = _database.Chunks.All;

            int count = 0;
            float total = 0f;

            for (int i = 0; i < all.Count; i++)
            {
                ChunkDefinition def = all[i];
                if (def == null) continue;

                var ctx = new ChunkCandidateContext(def, i, _world, _config, _balance, target, previousExit,
                    _sequence, _lastUsedSequence[i], UsesInRepeatWindow(i), _playerSpeed);

                bool eligible = true;
                for (int r = 0; r < _eligibility.Count; r++)
                {
                    if (_eligibility[r].IsEligible(in ctx)) continue;
                    eligible = false;
                    break;
                }
                if (!eligible) continue;

                float weight = 1f;
                for (int w = 0; w < _weights.Count; w++)
                {
                    weight *= _weights[w].Weight(in ctx);
                    if (weight <= 0f) break;
                }
                if (weight <= 0f) continue;

                _candidateIndices[count] = i;
                _candidateWeights[count] = weight;
                total += weight;
                count++;
            }

            int chosen = count > 0 && total > 0f ? PickWeighted(count, total) : -1;
            if (chosen < 0) chosen = PickFallback(all, target);

            if (chosen < 0)
            {
                _log?.Error(LogChannel.Track, $"No chunk available for world '{_world?.Id.Value}'. Track cannot continue.");
                return default;
            }

            return Spawn(all[chosen], chosen);
        }

        private float ComputeTargetDifficulty()
        {
            float ramp = _config.difficultyRampMetres > 0f
                ? Mathf.Clamp01(_lastPlayerDistance / _config.difficultyRampMetres)
                : 1f;

            float worldOffset = 0f;
            if (_world != null && _world.difficultyCurve != null && _world.difficultyCurve.length > 0)
                worldOffset = _world.difficultyCurve.Evaluate(ramp);

            float speedTerm = 0f;
            if (_balance != null && _balance.maxSpeed > 0f && _config.speedDifficultyInfluence > 0f)
                speedTerm = Mathf.Clamp01(_playerSpeed / _balance.maxSpeed) * _config.speedDifficultyInfluence;

            // The run scalar shapes the whole ramp rather than offsetting it, so a 0.5 event run reaches
            // the same authored ceiling at twice the distance instead of starting part-way up the curve.
            return Mathf.Clamp01((ramp + worldOffset + speedTerm) * _difficultyScale);
        }

        private int PickWeighted(int count, float total)
        {
            float roll = _random.Value01() * total;
            float accumulated = 0f;

            for (int i = 0; i < count; i++)
            {
                accumulated += _candidateWeights[i];
                if (roll < accumulated) return _candidateIndices[i];
            }

            // Float rounding can leave roll >= total; the last candidate is the correct remainder.
            return _candidateIndices[count - 1];
        }

        /// <summary>
        /// Guaranteed safe set for the active world (TECH_DOC §4.5 step 4). Difficulty bands and cooldowns
        /// are deliberately ignored here: a fallback must always exist, otherwise the run would stall.
        /// </summary>
        private int PickFallback(IReadOnlyList<ChunkDefinition> all, float target)
        {
            _fallbacks++;
            _bus?.Publish(new TrackFallbackEvent(_sequence, target));

            int count = 0;
            for (int i = 0; i < all.Count; i++)
            {
                ChunkDefinition def = all[i];
                if (def != null && def.safeFallback && MatchesWorld(def)) _candidateIndices[count++] = i;
            }

            if (count == 0)
            {
                // No safe chunk authored for this world: any world chunk is better than an empty track.
                for (int i = 0; i < all.Count; i++)
                {
                    ChunkDefinition def = all[i];
                    if (def != null && MatchesWorld(def)) _candidateIndices[count++] = i;
                }
                _log?.Warn(LogChannel.Track, $"World '{_world?.Id.Value}' has no safeFallback chunk; using any world chunk.");
            }

            return count > 0 ? _candidateIndices[_random.Range(0, count)] : -1;
        }

        private ChunkHandle Spawn(ChunkDefinition def, int repositoryIndex)
        {
            if (!_pools.TryGetPool(def.poolKey, out IObjectPool<PooledEntity> pool))
            {
                if (!_missingPoolReported)
                {
                    _missingPoolReported = true;
                    _log?.Error(LogChannel.Track, $"Chunk '{def.Id.Value}' references unregistered pool '{def.poolKey}'.");
                }
                return default;
            }

            PooledEntity entity = pool.Get();
            var root = entity as ChunkRoot;
            if (root == null)
            {
                pool.Release(entity);
                _log?.Error(LogChannel.Track, $"Pool '{def.poolKey}' prefab root is not a ChunkRoot.");
                return default;
            }

            double start = _headDistance;
            root.Bind(def, _sequence, start);

            // Live chunks are organised under TrackRoot so the hierarchy stays readable during a run. The
            // pool creates instances under PoolRoot; neither root is origin-shifted, because this generator
            // re-bases each live chunk's local Z itself (OnOriginShifted) rather than moving a parent.
            if (_trackRoot != null) root.CachedTransform.SetParent(_trackRoot, false);

            double offset = _origin != null ? _origin.OriginOffset : 0d;
            root.CachedTransform.localPosition = new Vector3(0f, 0f, (float)(start - offset));
            root.CachedTransform.localRotation = Quaternion.identity;

            var handle = new ChunkHandle(root, def, _sequence, start, def.entryLaneMask, def.exitLaneMask);
            _populator?.Populate(in handle, _random);

            _live.Add(handle);
            _headDistance = start + def.lengthMetres;
            _lastUsedSequence[repositoryIndex] = _sequence;
            PushRecent(repositoryIndex);

            _sequence++;
            _generated++;

            _bus?.Publish(new ChunkActivatedEvent(def.Id, handle.SequenceIndex, start, def.lengthMetres));
            return handle;
        }

        private void ReleaseAt(int index)
        {
            ChunkHandle handle = _live[index];
            ChunkRoot root = handle.Root;

            _populator?.Clear(in handle);

            // Defensive: a populator that forgot to release something would otherwise leak a pooled
            // instance for the rest of the session. Recover it and report, never silently drop it.
            if (root != null && root.HasLeakedChildren)
            {
                _log?.Error(LogChannel.Track, $"Chunk '{handle.Id.Value}' recycled with {root.ChildCount} unreleased child(ren).");
                for (int i = 0; i < root.ChildCount; i++)
                {
                    PooledEntity child = root.GetChild(i);
                    if (child == null) continue;
                    if (_pools.TryGetPool(child.PoolKey, out IObjectPool<PooledEntity> childPool)) childPool.Release(child);
                }
                root.ClearChildren();
            }

            if (root != null && _pools.TryGetPool(root.PoolKey, out IObjectPool<PooledEntity> pool)) pool.Release(root);

            _live.RemoveAt(index);
            _recycled++;
            _bus?.Publish(new ChunkRecycledEvent(handle.Id, handle.SequenceIndex));
        }

        private void PushRecent(int repositoryIndex)
        {
            _recent[_recentHead] = repositoryIndex;
            _recentHead = (_recentHead + 1) % _recent.Length;
            if (_recentCount < _recent.Length) _recentCount++;
        }

        private int UsesInRepeatWindow(int repositoryIndex)
        {
            int uses = 0;
            for (int i = 0; i < _recentCount; i++)
                if (_recent[i] == repositoryIndex) uses++;
            return uses;
        }

        private bool MatchesWorld(ChunkDefinition def)
        {
            string[] filter = _world != null ? _world.chunkTagFilter : null;
            if (filter == null || def.worldTags == null) return false;

            for (int i = 0; i < filter.Length; i++)
                for (int j = 0; j < def.worldTags.Length; j++)
                    if (filter[i] == def.worldTags[j]) return true;

            return false;
        }

        private float SafeLength()
        {
            IReadOnlyList<ChunkDefinition> all = _database.Chunks.All;
            for (int i = 0; i < all.Count; i++)
            {
                ChunkDefinition def = all[i];
                if (def != null && def.safeFallback && def.lengthMetres > 0f) return def.lengthMetres;
            }
            for (int i = 0; i < all.Count; i++)
            {
                ChunkDefinition def = all[i];
                if (def != null && def.lengthMetres > 0f) return def.lengthMetres;
            }
            return 40f;
        }
    }
}
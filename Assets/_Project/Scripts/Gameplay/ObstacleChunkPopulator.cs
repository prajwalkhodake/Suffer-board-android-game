using System;
using System.Collections.Generic;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Pooling;
using Skyrift.Core.Randomization;
using Skyrift.Data;
using Skyrift.Track;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Fills a chunk's authored obstacle slots from the pool (TECH_DOC §4.5, §4.7).
    ///
    /// This is the seam that keeps the dependency arrow pointing Track ← Gameplay: Track knows only
    /// <see cref="IChunkPopulator"/>, so collectibles (M2) and event decoration (M4) can be added as further
    /// populators without Track ever learning what an obstacle is.
    ///
    /// Allocation: the tag index is built once at composition time and the candidate buffer is reused, so
    /// populating a chunk mid-run does not allocate.
    /// </summary>
    public sealed class ObstacleChunkPopulator : IChunkPopulator
    {
        private readonly Dictionary<string, List<ObstacleDefinition>> _byTag =
            new Dictionary<string, List<ObstacleDefinition>>(32, StringComparer.Ordinal);

        private readonly List<ObstacleDefinition> _candidates = new List<ObstacleDefinition>(8);
        private readonly List<ObstacleInstance> _moving = new List<ObstacleInstance>(16);

        private IGameDatabase _database;
        private IPoolRegistry _pools;
        private ObstacleBehaviourCatalog _behaviours;
        private ObstacleContactIndex _index;
        private ILog _log;
        private ITrackGenerator _generator;
        private Transform _poolParent;
        private bool _missingTagReported;

        public int MovingCount => _moving.Count;

        public void Construct(IGameDatabase database, IPoolRegistry pools, ObstacleBehaviourCatalog behaviours,
            ObstacleContactIndex index, ILog log, ITrackGenerator generator, Transform poolParent)
        {
            _database = database;
            _pools = pools;
            _behaviours = behaviours;
            _index = index;
            _log = log;
            _generator = generator;
            _poolParent = poolParent;
            BuildTagIndex();
        }

        public void Populate(in ChunkHandle chunk, IRandomProvider random)
        {
            ChunkDefinition definition = chunk.Definition;
            if (definition == null || definition.obstacleSlots == null || chunk.Root == null) return;
            if (_database == null || _pools == null || random == null) return;

            ObstacleSlotData[] slots = definition.obstacleSlots;
            for (int i = 0; i < slots.Length; i++)
            {
                ObstacleSlotData slot = slots[i];
                if (slot == null || string.IsNullOrEmpty(slot.obstacleTag)) continue;

                // Optional slots are rolled first so a skipped slot consumes no further draws — keeping the
                // draw sequence (and therefore the run) reproducible from the seed alone.
                if (!slot.required && random.Value01() > slot.spawnChance) continue;

                if (!_byTag.TryGetValue(slot.obstacleTag, out List<ObstacleDefinition> tagged) || tagged.Count == 0)
                {
                    ReportMissingTag(slot.obstacleTag);
                    continue;
                }

                ObstacleDefinition chosen = PickWeighted(tagged, random);
                if (chosen != null) Spawn(chunk, slot, chosen, random);
            }
        }

        public void Clear(in ChunkHandle chunk)
        {
            ChunkRoot root = chunk.Root;
            if (root == null) return;

            for (int i = root.ChildCount - 1; i >= 0; i--)
            {
                PooledEntity child = root.GetChild(i);
                if (child == null) continue;

                if (child is ObstacleInstance obstacle) _moving.Remove(obstacle);

                // Return the instance to the always-active pool parent before releasing it. A released chunk
                // is inactive, so an obstacle left parented to it would stay invisible on its next spawn.
                ReturnToPoolParent(child);

                // PoolKey travels with the instance, so releasing needs no type switch and no lookup table.
                if (_pools != null && _pools.TryGetPool(child.PoolKey, out IObjectPool<PooledEntity> pool))
                    pool.Release(child);
            }

            root.ClearChildren();
        }

        /// <summary>
        /// Releases obstacles whose absolute run distance falls inside a window (TECH_DOC §4.12). Used on a
        /// continue so the player does not respawn inside the obstacle that just killed them. The clearance
        /// length is data (<c>ContinueConfig.respawnClearanceMetres</c>), never a constant here.
        /// </summary>
        /// <returns>How many obstacles were released, for the debug HUD and tests.</returns>
        public int ClearRange(double fromDistance, double toDistance)
        {
            if (_generator == null || _pools == null || toDistance <= fromDistance) return 0;

            int removed = 0;

            for (int c = 0; c < _generator.LiveCount; c++)
            {
                ChunkHandle chunk = _generator.GetLive(c);
                ChunkRoot root = chunk.Root;
                if (root == null) continue;

                // Chunks are an ordered span, so a disjoint chunk can be skipped without touching its children.
                if (chunk.EndDistance <= fromDistance || chunk.StartDistance >= toDistance) continue;

                for (int i = root.ChildCount - 1; i >= 0; i--)
                {
                    PooledEntity child = root.GetChild(i);
                    if (child == null) continue;

                    // A child's local Z is its offset from the chunk's start edge, so this is its absolute
                    // run distance and is unaffected by origin shifts.
                    double distance = chunk.StartDistance + child.CachedTransform.localPosition.z;
                    if (distance < fromDistance || distance > toDistance) continue;

                    if (child is ObstacleInstance obstacle) _moving.Remove(obstacle);

                    ReturnToPoolParent(child);
                    if (_pools.TryGetPool(child.PoolKey, out IObjectPool<PooledEntity> pool)) pool.Release(child);

                    root.RemoveChild(child);
                    removed++;
                }
            }

            return removed;
        }

        private void ReturnToPoolParent(PooledEntity child)
        {
            if (_poolParent != null) child.CachedTransform.SetParent(_poolParent, false);
        }

        /// <summary>Advances authored obstacle motion. One pass over moving obstacles only.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            for (int i = 0; i < _moving.Count; i++) _moving[i].TickMotion(dt);
        }

        private void Spawn(in ChunkHandle chunk, ObstacleSlotData slot, ObstacleDefinition definition, IRandomProvider random)
        {
            // Resolve the behaviour before taking a pool slot: an unresolvable archetype must not consume
            // an instance and then release it, which would show up as pool churn in the profiler.
            if (_behaviours == null || !_behaviours.TryGet(definition.behaviourId, out IObstacleBehaviour behaviour))
            {
                _log?.Error(LogChannel.Gameplay,
                    $"Obstacle '{definition.Id.Value}' has no behaviour for id '{definition.behaviourId}'. Slot skipped.");
                return;
            }

            if (!_pools.TryGetPool(definition.poolKey, out IObjectPool<PooledEntity> pool))
            {
                _log?.Error(LogChannel.Gameplay, $"Obstacle '{definition.Id.Value}' references unregistered pool '{definition.poolKey}'.");
                return;
            }

            PooledEntity entity = pool.Get();
            var obstacle = entity as ObstacleInstance;
            if (obstacle == null)
            {
                pool.Release(entity);
                _log?.Error(LogChannel.Gameplay, $"Pool '{definition.poolKey}' prefab root is not an ObstacleInstance.");
                return;
            }

            BalanceConfig balance = _database.Balance;
            int laneCount = balance != null ? balance.laneCount : 3;
            int lane = Mathf.Clamp(slot.lane, 0, Mathf.Max(0, laneCount - 1));
            float x = balance != null ? balance.LaneX(lane) : 0f;

            obstacle.Construct(_index);

            // Parent to the chunk so the authored slot offset is in chunk space and the obstacle travels,
            // re-bases and recycles with its chunk. worldPositionStays: false because Bind sets localPosition.
            obstacle.CachedTransform.SetParent(chunk.Root.CachedTransform, false);

            // A random phase per spawn stops every train in a run from sweeping in lockstep.
            float phaseStart = definition.isMoving && definition.motion.period > 0f
                ? random.Value01() * definition.motion.period
                : 0f;

            obstacle.Bind(definition, behaviour, lane, new Vector3(x, 0f, slot.zOffset), phaseStart);

            chunk.Root.AddChild(obstacle);
            if (definition.isMoving) _moving.Add(obstacle);
        }

        private ObstacleDefinition PickWeighted(List<ObstacleDefinition> tagged, IRandomProvider random)
        {
            _candidates.Clear();
            float total = 0f;

            for (int i = 0; i < tagged.Count; i++)
            {
                ObstacleDefinition def = tagged[i];
                if (def == null || def.spawnWeightMultiplier <= 0f) continue;
                _candidates.Add(def);
                total += def.spawnWeightMultiplier;
            }

            if (_candidates.Count == 0 || total <= 0f) return null;
            if (_candidates.Count == 1) return _candidates[0];

            float roll = random.Value01() * total;
            float accumulated = 0f;
            for (int i = 0; i < _candidates.Count; i++)
            {
                accumulated += _candidates[i].spawnWeightMultiplier;
                if (roll < accumulated) return _candidates[i];
            }

            return _candidates[_candidates.Count - 1];
        }

        private void BuildTagIndex()
        {
            _byTag.Clear();
            if (_database == null) return;

            IReadOnlyList<ObstacleDefinition> all = _database.Obstacles.All;
            for (int i = 0; i < all.Count; i++)
            {
                ObstacleDefinition def = all[i];
                if (def == null || def.tagSet == null) continue;

                for (int t = 0; t < def.tagSet.Length; t++)
                {
                    string tag = def.tagSet[t];
                    if (string.IsNullOrEmpty(tag)) continue;

                    if (!_byTag.TryGetValue(tag, out List<ObstacleDefinition> list))
                    {
                        list = new List<ObstacleDefinition>(4);
                        _byTag[tag] = list;
                    }
                    list.Add(def);
                }
            }
        }

        private void ReportMissingTag(string tag)
        {
            if (_missingTagReported) return;
            _missingTagReported = true;
            _log?.Error(LogChannel.Gameplay, $"No obstacle definition carries tag '{tag}'. Check the catalog cross-references.");
        }
    }
}
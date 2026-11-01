using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyrift.Core.Pooling
{
    /// <summary>
    /// Data-driven pool catalogue entry. One row per pooled prefab (TECH_DOC §5.14 rule 10).
    /// The prefab reference is a <see cref="PooledEntity"/> so the registry needs no reflection and no
    /// per-type switch: adding a pooled object is a data row, not a code change.
    /// </summary>
    [Serializable]
    public sealed class PoolEntry
    {
        [Tooltip("Stable pool key, snake_case. Referenced by definitions and spawners.")]
        public string key;

        [Tooltip("Prefab whose root carries a PooledEntity-derived component.")]
        public PooledEntity prefab;

        [Tooltip("Instances created during the loading phase. Must cover the worst-case concurrent count.")]
        [Min(0)] public int prewarm = 8;
    }

    /// <summary>
    /// Owns every pool in the game. Registration happens once at boot from a PoolCatalog asset;
    /// gameplay only calls GetPool/Get/Release.
    /// </summary>
    public interface IPoolRegistry
    {
        void Register(PoolEntry entry, Transform parent);
        IObjectPool<PooledEntity> GetPool(string key);
        bool TryGetPool(string key, out IObjectPool<PooledEntity> pool);
        void PrewarmAll();
        void SetGrowthAllowed(bool allowed);
        void ReleaseAll();
        int OverflowCount { get; }
        IReadOnlyList<string> Keys { get; }
    }

    /// <inheritdoc cref="IPoolRegistry"/>
    public sealed class PoolRegistry : IPoolRegistry
    {
        private readonly Dictionary<string, IObjectPool<PooledEntity>> _pools =
            new Dictionary<string, IObjectPool<PooledEntity>>(32);

        private readonly List<string> _keys = new List<string>(32);
        private readonly Dictionary<string, int> _prewarmCounts = new Dictionary<string, int>(32);
        private readonly Action<string> _overflowReporter;
        private int _overflowCount;

        public int OverflowCount => _overflowCount;
        public IReadOnlyList<string> Keys => _keys;

        /// <param name="overflowReporter">Invoked with the pool key on a mid-run overflow (telemetry hook).</param>
        public PoolRegistry(Action<string> overflowReporter = null)
        {
            _overflowReporter = overflowReporter;
        }

        public void Register(PoolEntry entry, Transform parent)
        {
            if (entry == null || string.IsNullOrEmpty(entry.key) || entry.prefab == null)
            {
                Debug.LogError("[PoolRegistry] Invalid pool entry (null key or prefab) skipped.");
                return;
            }

            if (_pools.ContainsKey(entry.key))
            {
                Debug.LogError($"[PoolRegistry] Duplicate pool key '{entry.key}' skipped.");
                return;
            }

            var pool = new ObjectPool<PooledEntity>(entry.key, entry.prefab, parent, entry.prewarm, OnOverflow);
            _pools.Add(entry.key, pool);
            _keys.Add(entry.key);
            _prewarmCounts.Add(entry.key, entry.prewarm);
        }

        public IObjectPool<PooledEntity> GetPool(string key)
        {
            if (_pools.TryGetValue(key, out IObjectPool<PooledEntity> pool)) return pool;
            throw new KeyNotFoundException($"Pool '{key}' is not registered. Add it to the PoolCatalog asset.");
        }

        public bool TryGetPool(string key, out IObjectPool<PooledEntity> pool) => _pools.TryGetValue(key, out pool);

        public void PrewarmAll()
        {
            for (int i = 0; i < _keys.Count; i++)
            {
                // Prewarm counts are authored per entry; re-running Prewarm is idempotent.
                _pools[_keys[i]].Prewarm(PrewarmTarget(_keys[i]));
            }
        }

        public void SetGrowthAllowed(bool allowed)
        {
            for (int i = 0; i < _keys.Count; i++) _pools[_keys[i]].GrowthAllowed = allowed;
        }

        public void ReleaseAll()
        {
            for (int i = 0; i < _keys.Count; i++) _pools[_keys[i]].ReleaseAll();
        }

        private int PrewarmTarget(string key) => _prewarmCounts.TryGetValue(key, out int n) ? n : 0;

        private void OnOverflow(string key)
        {
            _overflowCount++;
            _overflowReporter?.Invoke(key);
        }
    }
}

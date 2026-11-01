using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyrift.Core.Pooling
{
    /// <summary>
    /// Lifecycle hooks for a pooled object. OnSpawned must set <b>every</b> field the object reads
    /// (ADR-0007): pooled objects are reused, so stale state is the main pooling bug class.
    /// </summary>
    public interface IPoolable
    {
        void OnSpawned();
        void OnDespawned();
    }

    /// <summary>
    /// Base class for every pooled runtime entity. Caches its Transform once so gameplay code never
    /// calls GetComponent or walks .gameObject.transform chains (TECH_DOC §11.2 rule 4).
    /// Concrete entities live in Gameplay/Track and are cast from the pool result (no allocation).
    /// </summary>
    public abstract class PooledEntity : MonoBehaviour, IPoolable
    {
        public Transform CachedTransform { get; private set; }

        /// <summary>Pool key this instance was spawned from. Set by the pool on creation.</summary>
        public string PoolKey { get; internal set; }

        protected virtual void Awake()
        {
            CachedTransform = transform;
        }

        public virtual void OnSpawned() { }
        public virtual void OnDespawned() { }
    }

    /// <summary>Allocation-free pool statistics snapshot.</summary>
    public readonly struct PoolStats
    {
        public readonly int Active;
        public readonly int Inactive;
        public readonly int Created;
        public readonly int OverflowCount;

        public PoolStats(int active, int inactive, int created, int overflowCount)
        {
            Active = active;
            Inactive = inactive;
            Created = created;
            OverflowCount = overflowCount;
        }

        public int Total => Active + Inactive;
    }

    /// <summary>
    /// A pool of <see cref="PooledEntity"/> instances. Get/Release never allocate once prewarmed.
    /// Growth is only permitted during a loading phase; mid-run exhaustion recycles the eldest active
    /// item and reports <c>pool_overflow</c> instead of instantiating (TECH_DOC §4.6).
    /// </summary>
    public interface IObjectPool<T> where T : PooledEntity
    {
        string Key { get; }
        bool GrowthAllowed { get; set; }
        PoolStats Stats { get; }
        T Get();
        void Release(T item);
        void ReleaseAll();
        void Prewarm(int count);
    }

    /// <inheritdoc cref="IObjectPool{T}"/>
    public sealed class ObjectPool<T> : IObjectPool<T> where T : PooledEntity
    {
        private readonly T _prefab;
        private readonly Transform _parent;
        private readonly Stack<T> _inactive;
        private readonly List<T> _active;
        private readonly Action<string> _overflowReporter;
        private int _created;
        private int _overflowCount;

        public string Key { get; }
        public bool GrowthAllowed { get; set; } = true;

        public PoolStats Stats => new PoolStats(_active.Count, _inactive.Count, _created, _overflowCount);

        public ObjectPool(string key, T prefab, Transform parent, int capacity, Action<string> overflowReporter = null)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            _prefab = prefab != null ? prefab : throw new ArgumentNullException(nameof(prefab));
            _parent = parent;
            _inactive = new Stack<T>(Mathf.Max(1, capacity));
            _active = new List<T>(Mathf.Max(1, capacity));
            _overflowReporter = overflowReporter;
        }

        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_inactive.Count + _active.Count >= count) break;
                T item = Create();
                item.gameObject.SetActive(false);
                _inactive.Push(item);
            }
        }

        public T Get()
        {
            T item;
            if (_inactive.Count > 0)
            {
                item = _inactive.Pop();
            }
            else if (GrowthAllowed)
            {
                item = Create();
            }
            else if (_active.Count > 0)
            {
                // Mid-run exhaustion: never instantiate. Recycle the eldest active instance.
                _overflowCount++;
                _overflowReporter?.Invoke(Key);
                item = _active[0];
                Release(item);
                item = _inactive.Pop();
            }
            else
            {
                // Nothing exists at all (mis-configured prewarm). Create once so the game cannot stall.
                _overflowCount++;
                _overflowReporter?.Invoke(Key);
                item = Create();
            }

            _active.Add(item);
            item.gameObject.SetActive(true);
            item.OnSpawned();
            return item;
        }

        public void Release(T item)
        {
            if (item == null) return;

            // activeSelf is the spawn flag: a released instance is inactive, so this detects double-release.
            if (!item.gameObject.activeSelf)
            {
                Debug.LogWarning($"[Pool:{Key}] Double release of '{item.name}' ignored.");
                return;
            }

            item.OnDespawned();
            item.gameObject.SetActive(false);
            _active.Remove(item);
            _inactive.Push(item);
        }

        public void ReleaseAll()
        {
            // Backwards iteration because Release mutates _active.
            for (int i = _active.Count - 1; i >= 0; i--) Release(_active[i]);
        }

        private T Create()
        {
            T item = UnityEngine.Object.Instantiate(_prefab, _parent);
            item.PoolKey = Key;
            item.name = $"{Key}_{_created}";
            _created++;
            return item;
        }
    }
}

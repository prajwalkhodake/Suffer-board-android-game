using System.Collections.Generic;
using NUnit.Framework;
using Skyrift.Core.Pooling;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

namespace Skyrift.Tests.PlayMode
{
    /// <summary>
    /// Pool lifetime and the zero-allocation rule (TECH_DOC §4.6, §11.2, §12.1). These tests build their own
    /// pooled template at runtime rather than loading a greybox prefab, which is deliberate: the pooling
    /// contract is a Core behaviour and must be provable before any content exists. The greybox prefabs are
    /// the same mechanism with a different template.
    ///
    /// The template is a live <see cref="GameObject"/> rather than a prefab asset because
    /// <see cref="ObjectPool{T}"/> only ever calls <c>Instantiate</c> on it — a scene object and a loaded
    /// prefab asset are indistinguishable to that call.
    /// </summary>
    [TestFixture]
    public class PoolingLifetimeTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>(8);

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        private GameObject NewGameObject(string name)
        {
            GameObject go = new GameObject(name);
            _spawned.Add(go);
            return go;
        }

        private TestEntity Template(out GameObject holder)
        {
            holder = NewGameObject("template");
            return holder.AddComponent<TestEntity>();
        }

        private static void Configure(IObjectPool<PooledEntity> pool, bool growth)
        {
            pool.GrowthAllowed = growth;
        }

        // ── Creation ────────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Prewarm_CreatesTheAuthoredCount_AndEveryInstanceStartsInactive()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root").transform;

            var pool = new ObjectPool<PooledEntity>("test_chunk", template, parent, capacity: 8);
            pool.Prewarm(8);

            PoolStats stats = pool.Stats;
            Assert.That(stats.Created, Is.EqualTo(8), "prewarm is the only Instantiate the run will ever see");
            Assert.That(stats.Inactive, Is.EqualTo(8));
            Assert.That(stats.Active, Is.EqualTo(0));
            Assert.That(stats.Total, Is.EqualTo(8));

            for (int i = 0; i < parent.childCount; i++)
                Assert.That(parent.GetChild(i).gameObject.activeSelf, Is.False,
                    "a pooled instance waits in the parent, inactive — the mid-run hitch this prevents is a dropped frame");

            Assert.That(template.transform.childCount, Is.EqualTo(0), "clones hang under the pool parent, never the template");
        }

        [Test]
        public void Prewarm_IsIdempotent_SoARepeatedLoadingPassCannotDoubleTheHeap()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_idempotent").transform;

            var pool = new ObjectPool<PooledEntity>("test_voice", template, parent, capacity: 4);
            pool.Prewarm(4);
            pool.Prewarm(4);
            pool.Prewarm(2);

            Assert.That(pool.Stats.Created, Is.EqualTo(4),
                "the target is the total instance count, so a boot step that runs twice does not grow the pool");
        }

        [Test]
        public void PoolKeyIsStampedOnEveryInstance_SoAnObjectKnowsWhereItCameFrom()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_keyed").transform;

            var pool = new ObjectPool<PooledEntity>("test_obstacle", template, parent, capacity: 2);
            pool.Prewarm(2);

            PooledEntity item = pool.Get();
            Assert.That(item.PoolKey, Is.EqualTo("test_obstacle"));
            Assert.That(item.name, Does.StartWith("test_obstacle_"), "the instance name carries the key for the profiler");
            Assert.That(item.CachedTransform, Is.Not.Null, "Awake cached the Transform, so gameplay never calls GetComponent");
        }

        // ── The lifecycle contract ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Hooks_FireOnSpawnAndRelease_WhileTheObjectIsStillUsable()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_hooks").transform;

            var pool = new ObjectPool<PooledEntity>("test_chunked", template, parent, capacity: 2);
            pool.Prewarm(1);

            TestEntity item = (TestEntity)pool.Get();
            Assert.That(item.SpawnCount, Is.EqualTo(1));
            Assert.That(item.SpawnedWhileActive, Is.True,
                "OnSpawned runs after SetActive(true), so a hook can read renderers and colliders");

            pool.Release(item);
            Assert.That(item.DespawnCount, Is.EqualTo(1));
            Assert.That(item.DespawnedWhileActive, Is.True,
                "OnDespawned runs before SetActive(false), so a hook can still reset what it is about to hide");
            Assert.That(item.gameObject.activeSelf, Is.False, "and the object is hidden when the hook returns");

            // A second spawn of the same instance must not inherit anything from the first (ADR-0007).
            pool.Release(item);
            item.transform.position = new Vector3(7f, 2f, 9f);   // a spawner positions it after Get, never before
            item.StaleState = "left behind by the previous run";

            pool.Get();
            Assert.That(item.ResetCalls, Is.EqualTo(2), "OnSpawned is the only place stale state can be cleared");
            Assert.That(item.StaleState, Is.Null);
            Assert.That(item.LastPositionOnSpawn, Is.EqualTo(new Vector3(7f, 2f, 9f)),
                "the hook runs before the spawner positions the instance, so a hook must never depend on a position it does not own");
        }

        // ── Growth, exhaustion and overflow ─────────────────────────────────────────────────────────────

        [Test]
        public void DuringLoading_GetCreatesWhenTheFreeListIsEmpty()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_growth").transform;

            var pool = new ObjectPool<PooledEntity>("test_growth", template, parent, capacity: 1);
            pool.GrowthAllowed = true;

            pool.Get();
            pool.Get();
            pool.Get();

            Assert.That(pool.Stats.Created, Is.EqualTo(3), "growth is allowed while loading");
            Assert.That(pool.Stats.OverflowCount, Is.EqualTo(0), "a legitimate grow is not an overflow");
        }

        [Test]
        public void MidRunExhaustion_RecyclesTheEldestActiveInstanceAndReportsOverflow()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_overflow").transform;

            string overflowedKey = null;
            var pool = new ObjectPool<PooledEntity>("test_overflow", template, parent, capacity: 3,
                overflowReporter: key => overflowedKey = key);

            pool.Prewarm(3);
            Configure(pool, growth: false);

            PooledEntity eldest = pool.Get();
            PooledEntity middle = pool.Get();
            PooledEntity youngest = pool.Get();
            Assert.That(pool.Stats.Active, Is.EqualTo(3));

            PooledEntity stolen = pool.Get();

            Assert.That(stolen, Is.SameAs(eldest),
                "the eldest is sacrificed: it is the instance furthest behind the player, so it is the one the run has least use for");
            Assert.That(stolen, Is.Not.SameAs(middle).And.Not.SameAs(youngest));

            Assert.That(overflowedKey, Is.EqualTo("test_overflow"), "telemetry must be able to see a mis-sized pool");
            Assert.That(pool.Stats.OverflowCount, Is.EqualTo(1));
            Assert.That(pool.Stats.Created, Is.EqualTo(3), "and not one Instantiate happened");
            Assert.That(pool.Stats.Active, Is.EqualTo(3), "the sacrificed instance was re-issued, not duplicated");
            Assert.That(eldest.DespawnCount, Is.EqualTo(1), "recycling runs the release hook, so nothing stale survives");
        }

        [Test]
        public void AnEmptyNonGrowingPool_StillCreatesOnceRatherThanStallingTheGame()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_starved").transform;

            bool reported = false;
            var pool = new ObjectPool<PooledEntity>("test_starved", template, parent, capacity: 2,
                overflowReporter: key => reported = true);
            pool.GrowthAllowed = false;

            PooledEntity item = pool.Get();

            Assert.That(item, Is.Not.Null, "a mis-configured prewarm must not hand back null into the gameplay loop");
            Assert.That(item.gameObject.activeSelf, Is.True);
            Assert.That(reported, Is.True, "but it must be reported, because it is an authoring error");
            Assert.That(pool.Stats.Created, Is.EqualTo(1));
        }

        [Test]
        public void DoubleRelease_IsIgnored_SoTheFreeListCannotGrowOneInstanceTwice()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_double").transform;

            var pool = new ObjectPool<PooledEntity>("test_double", template, parent, capacity: 2);
            pool.Prewarm(2);

            PooledEntity item = pool.Get();
            pool.Release(item);
            pool.Release(item);

            Assert.That(pool.Stats.Total, Is.EqualTo(2), "the second release found the instance already inactive and did nothing");
            Assert.That(pool.Stats.Inactive, Is.EqualTo(2),
                    "which is the whole point: a duplicated free-list entry would hand the same object to two callers");
            Assert.That(pool.Stats.Active, Is.EqualTo(0));
        }

        [Test]
        public void ReleaseAll_EmptiesTheActiveSet_AndIsSafeToRepeat()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_release").transform;

            var pool = new ObjectPool<PooledEntity>("test_release", template, parent, capacity: 4);
            pool.Prewarm(4);
            for (int i = 0; i < 4; i++) pool.Get();

            pool.ReleaseAll();
            Assert.That(pool.Stats.Active, Is.EqualTo(0));
            Assert.That(pool.Stats.Inactive, Is.EqualTo(4));

            Assert.That(() => pool.ReleaseAll(), Throws.Nothing, "a shutdown path may be entered twice");
            Assert.That(pool.Stats.Inactive, Is.EqualTo(4));
        }

        // ── The registry ────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void TheRegistry_HandsOutPoolsByKey_AndReportsWhatItKnows()
        {
            GameObject holderA, holderB;
            TestEntity a = Template(out holderA);
            TestEntity b = Template(out holderB);
            Transform parent = NewGameObject("registry_root").transform;

            PoolRegistry registry = new PoolRegistry();
            registry.Register(new PoolEntry { key = "pool_a", prefab = a, prewarm = 2 }, parent);
            registry.Register(new PoolEntry { key = "pool_b", prefab = b, prewarm = 3 }, parent);

            Assert.That(registry.Keys, Is.EquivalentTo(new[] { "pool_a", "pool_b" }));

            IObjectPool<PooledEntity> poolA;
            Assert.That(registry.TryGetPool("pool_a", out poolA), Is.True);
            Assert.That(poolA, Is.Not.SameAs(registry.GetPool("pool_b")), "one key, one pool");

            registry.PrewarmAll();
            Assert.That(registry.GetPool("pool_a").Stats.Created, Is.EqualTo(2));
            Assert.That(registry.GetPool("pool_b").Stats.Created, Is.EqualTo(3));

            registry.SetGrowthAllowed(false);
            Assert.That(registry.GetPool("pool_a").GrowthAllowed, Is.False);
            Assert.That(registry.GetPool("pool_b").GrowthAllowed, Is.False,
                "the run-start flip has to reach every pool, or one forgotten row instantiates mid-run");

            registry.ReleaseAll();
            Assert.That(registry.GetPool("pool_a").Stats.Active, Is.EqualTo(0));
        }

        [Test]
        public void TheRegistry_ThrowsOnAnUnregisteredKey_NamingTheKeyTheAuthorMissed()
        {
            PoolRegistry registry = new PoolRegistry();

            KeyNotFoundException error = Assert.Throws<KeyNotFoundException>(() => registry.GetPool("pool_missing"));
            Assert.That(error.Message, Does.Contain("pool_missing"),
                "and points at the PoolCatalog, which is where the row belongs (§5.14 rule 10)");
            Assert.That(error.Message, Does.Contain("PoolCatalog"));
        }

        [Test]
        public void TheRegistry_SkipsEntriesThatCannotBePooled()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("registry_root_invalid").transform;

            // A broken row is a data error the validator should have caught; the registry must still boot the
            // rest of the catalog rather than throw during composition. The LogError it prints would fail the
            // test under the default rules, so the assertion is about state, not about the console.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                PoolRegistry registry = new PoolRegistry();
                registry.Register(null, parent);
                registry.Register(new PoolEntry { key = "", prefab = template, prewarm = 1 }, parent);
                registry.Register(new PoolEntry { key = "pool_no_prefab", prefab = null, prewarm = 1 }, parent);
                registry.Register(new PoolEntry { key = "pool_good", prefab = template, prewarm = 1 }, parent);
                registry.Register(new PoolEntry { key = "pool_good", prefab = template, prewarm = 1 }, parent);

                Assert.That(registry.Keys, Is.EquivalentTo(new[] { "pool_good" }),
                    "only the one valid row survived, and the duplicate key was refused rather than replacing it");

                registry.PrewarmAll();
                Assert.That(registry.GetPool("pool_good").Stats.Created, Is.EqualTo(1));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void TheRegistry_CountsOverflowAcrossEveryPool_ForTelemetry()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("registry_root_overflow").transform;

            List<string> reported = new List<string>(4);
            PoolRegistry registry = new PoolRegistry(key => reported.Add(key));
            registry.Register(new PoolEntry { key = "pool_tight", prefab = template, prewarm = 1 }, parent);
            registry.SetGrowthAllowed(false);

            IObjectPool<PooledEntity> pool = registry.GetPool("pool_tight");
            pool.Get();
            pool.Get();

            Assert.That(registry.OverflowCount, Is.EqualTo(1));
            Assert.That(reported, Is.EquivalentTo(new[] { "pool_tight" }), "and the key is carried, not just the count");
        }

        // ── The allocation rule ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// §11.2 rule 3 / §12.1: the gameplay loop allocates nothing. This measures the pool's own Get/Release
        /// pair rather than a whole run, so a regression names the mechanism instead of a vague frame spike.
        ///
        /// "GC Allocated In Frame" is a per-frame counter that resets at the frame boundary, so reading it
        /// before and after the work inside one frame isolates exactly this code path — including whatever the
        /// test harness itself had already allocated that frame, which is why the baseline is subtracted.
        /// </summary>
        [UnityTest]
        public IEnumerator APrewarmedGetAndRelease_AllocatesNoBytesInSteadyState()
        {
            GameObject holder;
            TestEntity template = Template(out holder);
            Transform parent = NewGameObject("pool_root_alloc").transform;

            var pool = new ObjectPool<PooledEntity>("test_alloc", template, parent, capacity: 16);
            pool.Prewarm(16);

            // Warm the path once outside the measured window: the first call to any method can JIT and
            // allocate, and the assertion is about steady state, not about first contact.
            for (int i = 0; i < 16; i++)
            {
                PooledEntity item = pool.Get();
                pool.Release(item);
            }

            ProfilerRecorder recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (!recorder.valid)
            {
                recorder.Dispose();
                Assert.Ignore("The GC Allocated In Frame counter is not available; the profiler module must be enabled.");
            }

            yield return null;

            long baseline = recorder.LastValue;

            for (int frame = 0; frame < 8; frame++)
            {
                for (int i = 0; i < 16; i++)
                {
                    PooledEntity item = pool.Get();
                    item.transform.localPosition = new Vector3(i, 0f, frame * 16f);
                    pool.Release(item);
                }
            }

            long allocated = recorder.LastValue - baseline;
            recorder.Dispose();

            Assert.That(allocated, Is.EqualTo(0L),
                "a prewarmed Get/Release cycle allocated " + allocated + " bytes; look for a LINQ, a params, a boxing " +
                "or a string concatenation on the pool path (§11.2)");
        }

        /// <summary>
        /// A pooled object with hooks that record the state they were called in, so the ordering contract is
        /// observable from the outside rather than asserted from the pool's own code.
        /// </summary>
        private sealed class TestEntity : PooledEntity
        {
            public int SpawnCount;
            public int DespawnCount;
            public int ResetCalls;
            public bool SpawnedWhileActive;
            public bool DespawnedWhileActive;
            public Vector3 LastPositionOnSpawn;
            public string StaleState;

            public override void OnSpawned()
            {
                SpawnCount++;
                ResetCalls++;
                SpawnedWhileActive = gameObject.activeSelf;
                LastPositionOnSpawn = CachedTransform != null ? CachedTransform.position : Vector3.zero;
                StaleState = null;
            }

            public override void OnDespawned()
            {
                DespawnCount++;
                DespawnedWhileActive = gameObject.activeSelf;
            }
        }
    }
}

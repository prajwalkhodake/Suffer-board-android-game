using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Skyrift.Bootstrap;
using Skyrift.Controls;
using Skyrift.Core;
using Skyrift.Core.Events;
using Skyrift.Core.Ids;
using Skyrift.Core.Pooling;
using Skyrift.Core.Services;
using Skyrift.Data;
using Skyrift.Editor;
using Skyrift.Gameplay;
using Skyrift.Track;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Skyrift.Tests.PlayMode
{
    /// <summary>
    /// Brings up the shipped <c>Boot.unity</c> once per test and tears it down again, so a fixture can assert
    /// against the composition root the product owner actually presses Play on (TECH_DOC §4.1, §12.1).
    ///
    /// <b>Why this loads the scene instead of building the systems.</b> <see cref="RunSystems"/> has sixteen
    /// members and <c>ValidateSystems</c> refuses any incomplete bundle, so a hand-wired fixture would test the
    /// run controller against a graph of my own invention — a passing test would not prove that the scene works.
    /// Loading <see cref="SkyriftPaths.BootScene"/> additively is the only way to cover the wiring the setup
    /// tool writes, which is the half nothing else in the pyramid touches.
    ///
    /// It is additive because replacing the test runner's own scene would tear down the harness mid-run, and
    /// every test unloads it again in <see cref="TearDown"/>: a fresh boot per test means no state leaks from one
    /// transition sequence into the next, which is what makes the fixture order-independent.
    ///
    /// <b>Shared by more than one fixture</b> because the run-loop cases and the generation/soak cases need the
    /// identical bring-up, and a second copy of an asynchronous scene load is a second copy of its race
    /// conditions. The members stay <c>protected</c> with the original field names so the derived fixtures read
    /// exactly as they did when this all lived in one file.
    ///
    /// Requires the generated content, so the whole fixture is inconclusive rather than failing until
    /// Tools &gt; Skyrift &gt; Build M1 Greybox has been run.
    /// </summary>
    public abstract class GreyboxSceneFixture
    {
        /// <summary>Frames to wait for the boot pipeline. Generous: the first frame also warps shaders.</summary>
        protected const int BootFrames = 240;

        /// <summary>One simulated step for the direct <see cref="IRunController.Tick"/> calls (60 Hz).</summary>
        protected const float Step = 1f / 60f;

        private Scene _scene;

        /// <summary>
        /// Guard for <see cref="TearDown"/>. A default <see cref="Scene"/> carries handle 0, which really does
        /// name a live scene in the test player — so an unloaded-by-accident handle would take the harness's own
        /// scene down with it. Only a load that finished sets this.
        /// </summary>
        private bool _sceneLoaded;

        private GameRoot _root;

        protected IServiceContainer _services;
        protected IRunController _run;
        protected IPlayerMotor _motor;
        protected IInputCommandBuffer _buffer;
        protected ITrackGenerator _generator;
        protected IPoolRegistry _pools;
        protected IEventBus _bus;
        protected IGameDatabase _database;
        protected StableId _worldId;

        private readonly List<string> _exceptions = new List<string>(4);
        private Application.LogCallback _logCapture;

        /// <summary>
        /// Resolves a service from the booted graph, failing with the reason that is actually true when the
        /// registration is missing. Deliberately not <c>Resolve</c>: an unknown service must name the step that
        /// owns it rather than throw a container exception whose stack starts in the wrong assembly (§4.1).
        /// </summary>
        protected T Require<T>() where T : class
        {
            if (!_services.TryResolve<T>(out T service))
                Assert.Fail($"{typeof(T).Name} is not registered — the §4.1 step that owns it did not run, "
                            + "or BootstrapConfig lost the scene reference the setup tool assigned.");
            return service;
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (!SkyriftPaths.AssetExists(SkyriftPaths.BootScene))
            {
                // Not a failure: nothing on disk can be booted until the setup tool has run. Inconclusive is
                // the honest report, and the message says exactly what to do about it.
                Assert.Ignore("Boot.unity does not exist yet. Run Tools > Skyrift > Build M1 Greybox, then re-run.");
            }

            // A greybox prints placeholder-asset complaints as errors by design (missing art, 2D-only SFX),
            // so message severity cannot be the pass signal here. Exceptions still are, and those are collected
            // and asserted in TearDown instead of being silently swallowed.
            LogAssert.ignoreFailingMessages = true;

            _logCapture = (message, stack, type) => { if (type == LogType.Exception) _exceptions.Add(message); };
            Application.logMessageReceived += _logCapture;

            // The unload in TearDown is asynchronous, and two GameRoots in the scene set would make
            // FindObjectOfType a coin toss. Wait for the previous boot to be fully gone.
            string sceneName = Path.GetFileNameWithoutExtension(SkyriftPaths.BootScene);
            while (SceneManager.GetSceneByName(sceneName).IsValid()) yield return null;

            int indexBefore = SceneManager.sceneCount;
            AsyncOperation load = SceneManager.LoadSceneAsync(SkyriftPaths.BootScene, LoadSceneMode.Additive);
            Assert.That(load, Is.Not.Null, "the additive load never started — the scene asset is unreadable.");

            while (!load.isDone) yield return null;

            Assert.That(SceneManager.sceneCount, Is.EqualTo(indexBefore + 1), "the scene was not added");
            _scene = SceneManager.GetSceneAt(indexBefore);
            _sceneLoaded = true;

            _root = Object.FindObjectOfType<GameRoot>();
            Assert.That(_root, Is.Not.Null, "Boot.unity has no GameRoot — the scene was built by an older setup tool.");

            for (int frame = 0; frame < BootFrames && _root != null && !_root.IsBooted; frame++) yield return null;

            Assert.That(_root.IsBooted, Is.True,
                "boot never completed. Read the console: a step returned a failure Result and GameBootstrap logged "
                + "the step name (§4.1). A hang here is a step awaiting something that never answers.");

            _services = _root.Bootstrap != null ? _root.Bootstrap.Services : null;
            Assert.That(_services, Is.Not.Null, "GameRoot.IsBooted without a bootstrap — the tick would have no graph.");

            _bus = Require<IEventBus>();
            _database = Require<IGameDatabase>();
            _run = Require<IRunController>();
            _motor = Require<IPlayerMotor>();
            _buffer = Require<IInputCommandBuffer>();
            _generator = Require<ITrackGenerator>();
            _pools = Require<IPoolRegistry>();

            Assert.That(_database.Worlds.Count, Is.GreaterThan(0), "a booted database with no worlds cannot start a run.");
            _worldId = _database.Worlds.All[0].Id;
        }

        [TearDown]
        public void TearDown()
        {
            // Asserted before the unload so a failure cannot be lost behind an asynchronous scene teardown.
            if (_exceptions.Count > 0)
            {
                string[] escaped = _exceptions.ToArray();
                _exceptions.Clear();
                Assert.Fail($"{escaped.Length} exception(s) escaped while the greybox was booted. First: {escaped[0]}");
            }

            if (_sceneLoaded && _scene.IsValid()) SceneManager.UnloadSceneAsync(_scene);
            _sceneLoaded = false;
            _scene = default;
            _root = null;
            _services = null;
            _run = null;
            _motor = null;
            _buffer = null;
            _generator = null;
            _pools = null;
            _bus = null;
            _database = null;
            _worldId = StableId.None;

            if (_logCapture != null) Application.logMessageReceived -= _logCapture;
            _logCapture = null;

            LogAssert.ignoreFailingMessages = false;
        }
    }
}

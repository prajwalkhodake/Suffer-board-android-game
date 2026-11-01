using System;
using System.Threading;
using System.Threading.Tasks;
using Skyrift.Audio;
using Skyrift.Controls;
using Skyrift.Core;
using Skyrift.Core.Diagnostics;
using Skyrift.Core.Events;
using Skyrift.Core.Services;
using Skyrift.Data;
using Skyrift.Gameplay;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;

namespace Skyrift.Bootstrap
{
    /// <summary>
    /// Builds the object graph once, in a defined order, and owns the top-level game flow (TECH_DOC §4.1).
    /// Nothing else may call <c>Register</c>.
    /// </summary>
    public interface IGameBootstrap
    {
        Task<Result> InitializeAsync(BootstrapConfig config, CancellationToken ct);
        void Shutdown();
        IServiceContainer Services { get; }
    }

    /// <summary>
    /// The fixed initialization order from TECH_DOC §4.1, as slots rather than as a sequence. Each step
    /// declares its own <see cref="IBootstrapStep.Order"/> and the pipeline sorts them, so adding
    /// SaveService in M3 or the ad adapters in M5 is a new class at an existing slot — never an edit to the
    /// boot sequence. The gaps are deliberate: a step can be inserted between two others without renumbering.
    /// </summary>
    public static class BootstrapOrder
    {
        public const int CorePrimitives = 10;
        public const int SaveService = 20;
        public const int Catalogs = 30;
        public const int RemoteConfig = 40;
        public const int Consent = 50;
        public const int ServiceAdapters = 60;
        public const int Meta = 70;
        public const int Audio = 80;
        public const int Preload = 90;

        /// <summary>
        /// The run object graph. Not one of the ten numbered steps in §4.1 because M1 has no Meta, save or
        /// Addressables layer to hang it off; it sits after content is available and before the first screen,
        /// which is where those milestones will keep it.
        /// </summary>
        public const int RunSystems = 95;

        public const int UiRoot = 100;
    }

    /// <summary>
    /// One boot step (TECH_DOC §4.1 extension point). Steps are fail-fast: a refused step aborts the boot and
    /// publishes <c>BootstrapFailedEvent</c> naming it, so a wiring error is diagnosable without a repro.
    /// </summary>
    public interface IBootstrapStep
    {
        int Order { get; }
        string Name { get; }
        Task<Result> ExecuteAsync(BootstrapContext context, CancellationToken ct);
    }

    /// <summary>
    /// What a step is given. Steps register into and resolve from <see cref="Services"/> — resolution at
    /// composition time is exactly what the container is for (§4.0). <see cref="Bus"/> and <see cref="Log"/>
    /// are carried directly because they must exist before the step that registers them can fail.
    /// </summary>
    public sealed class BootstrapContext
    {
        public IServiceContainer Services { get; }
        public BootstrapConfig Config { get; }
        public IEventBus Bus { get; }
        public ILog Log { get; }

        public BootstrapContext(IServiceContainer services, BootstrapConfig config, IEventBus bus, ILog log)
        {
            Services = services;
            Config = config;
            Bus = bus;
            Log = log;
        }
    }

    /// <summary>
    /// Everything the composition root needs, authored in the Boot scene. Scene references are explicit
    /// fields rather than a runtime search: the editor tool builds the scene once, and a missing reference is
    /// a boot-time error naming the field instead of a null-reference on the first frame of the first run.
    ///
    /// Nothing here is instantiated at runtime. Bootstrap wires what the scene already contains, which is
    /// what keeps the "no runtime Instantiate in gameplay" rule (§11.2 rule 1) true of the boot path as well.
    /// </summary>
    [Serializable]
    public sealed class BootstrapConfig
    {
        [Header("Content")]
        public CatalogManifest catalog;
        public ObstacleBehaviourCatalog obstacleBehaviours;
        public AudioCatalog audioCatalog;
        public InputActionAsset inputActions;

        [Header("Player")]
        [Tooltip("Organisational parent for the player rig. Stays at the identity — PlayerMotor re-bases its " +
                 "own rig on an origin shift instead (§4.6).")]
        public Transform playerRoot;
        public PlayerMotor playerMotor;
        public PlayerContactSensor playerSensor;

        [Header("Chaser")]
        [Tooltip("Organisational parent for the chaser rig. Stays at the identity — the rig's Z follows the " +
                 "motor's forward offset (§4.6).")]
        public Transform chaserRoot;
        public ChaserSystem chaser;

        [Header("World")]
        [Tooltip("Not origin-shifted: the generator re-bases each live chunk instead (§4.6).")]
        public Transform trackRoot;

        [Tooltip("Always-active parent for released pooled objects. Never origin-shifted.")]
        public Transform poolRoot;

        [Tooltip("Origin-shifted root for particles and trails (M6).")]
        public Transform vfxRoot;

        [Header("Audio")]
        public Transform audioRoot;
        [Tooltip("Optional. Without a mixer the service falls back to per-source gain (§4.18).")]
        public AudioMixer mixer;

        [Header("Input")]
        [Tooltip("Activated when useKeyboardInput is false. Authored inactive; Bootstrap enables exactly one source.")]
        public TouchInputSource touchInput;

        [Tooltip("Activated when useKeyboardInput is true. Editor and DEV builds only. Authored inactive.")]
        public KeyboardInputSource keyboardInput;

        [Tooltip("Editor convenience. A shipped build always uses touch, whatever this says (§4.3).")]
        public bool useKeyboardInput = true;

        [Header("Development")]
        [Tooltip("Grants continues for free through DebugContinueOfferPolicy. Forced off for release builds.")]
        public bool allowFreeContinues;

        [Tooltip("Walks Boot → Splash → Loading → Home and starts a run, so the greybox is playable with no UI.")]
        public bool autoStartRun = true;

        [Tooltip("0 derives a fresh seed per run. A non-zero value reproduces the same run every time.")]
        public uint fixedSeed;

        [Tooltip("Dev-only IMGUI overlay. Authored inactive; M3 replaces it with the uGUI HUD.")]
        public DebugHud debugHud;

        public bool showDebugHud = true;
    }
}
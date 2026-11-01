using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Skyrift.Editor
{
    /// <summary>
    /// Authors the platform and engine settings that are contracts rather than content (TECH_DOC §2.3,
    /// §4.7, §11.2). Like <see cref="ProjectSetup"/> this always re-applies: a physics or frame-timing value
    /// that drifts between machines changes the feel of the game and the reproducibility of a seed, so it
    /// is written deterministically instead of being left to whatever a new project defaulted to.
    ///
    /// It does <b>not</b> switch the active build target. Switching reimports every asset and takes minutes;
    /// Android settings persist regardless of the active platform, so they can be authored from a desktop
    /// editor and only the final device build needs the switch.
    /// </summary>
    public static class PlatformSetup
    {
        /// <summary>
        /// Development define. Present in editor and dev builds so the debug HUD, keyboard input and the
        /// fail-fast catalog path are live. <see cref="SettingsEnforcer"/> swaps it for PROD on a release build.
        /// </summary>
        public const string DevDefine = "DEV";

        public const string CompanyName = "Skyrift Studio";
        public const string ProductName = "SKYRIFT";
        public const string ApplicationId = "com.skyriftstudio.skyrift";
        public const string BundleVersion = "0.1.0";

        public static void Run()
        {
            SkyriftLayers.Ensure();
            ApplyPhysics();
            ApplyTime();
            ApplyAudio();
            ApplyAndroidPlayerSettings();
            ApplyBuildScenes();
            AssetAuthoring.SaveAll();

            Debug.Log("[Skyrift] Platform settings authored (layers, physics, time, audio, Android, build scenes).");
        }

        /// <summary>
        /// Contacts come from trigger volumes evaluated by the simulation step, never from a query (§4.7).
        /// </summary>
        private static void ApplyPhysics()
        {
            // The motor writes the rig's Transform directly and its Rigidbody is kinematic, so transform
            // changes only need to reach the physics engine when the simulation runs — which it does anyway.
            // Auto-sync would push every transform write immediately and cost a full broadphase update per
            // lane change for no benefit, because §11.2 rule 6 forbids per-frame Physics.Overlap*/raycasts.
            Physics.autoSyncTransforms = false;

            // Reuses the collision callback buffers instead of allocating a new array per contact. Triggers
            // (OnTriggerEnter(Collider)) do not allocate either way, but this keeps the setting correct for
            // the non-trigger contacts M6's VFX may introduce.
            Physics.reuseCollisionCallbacks = true;

            // Unused by gameplay: vertical motion is closed-form from BalanceConfig's authored jump height
            // and air time (§4.4), and the kinematic Rigidbody ignores gravity. Left at the engine default
            // so any future non-kinematic prop behaves predictably rather than inheriting a game-specific value.
            Physics.gravity = new Vector3(0f, -9.81f, 0f);
        }

        private static void ApplyTime()
        {
            // 50 Hz physics. FixedUpdate runs before Update, so a contact is classified against the rig
            // position from the previous simulation step: at the 30 m/s cap that is 0.6 m of latency, which
            // is what BalanceConfig.clearanceForgiveness and grazeLateralThreshold are tuned to absorb.
            Time.fixedDeltaTime = 0.02f;

            // Bounds catch-up after a hitch. The default 0.333 s allows 16 fixed steps in one frame, which
            // turns a GC pause or a thermal throttle into a visible spiral; 0.1 s caps it at 5.
            Time.maximumDeltaTime = 0.1f;
            Time.maximumParticleDeltaTime = 0.03f;

            // Never Time.timeScale: pause and crash freeze go through RunClock (§4.0) so that audio, UI and
            // the continue countdown keep running on unscaled time while the simulation is stopped.
            Time.timeScale = 1f;
        }

        /// <summary>
        /// AudioManager.asset has no public setters for the buffer and voice counts, so it is edited through
        /// its serialized form. Every write is null-guarded: a renamed field degrades to a warning rather
        /// than aborting the rest of setup.
        /// </summary>
        private static void ApplyAudio()
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/AudioManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[Skyrift] AudioManager.asset not found; audio project settings were skipped.");
                return;
            }

            var serialized = new SerializedObject(assets[0]);

            // 512 frames is the stable choice on Android: 256 ("best latency") underruns on mid-range
            // devices under load, and an underrun is an audible crackle in the music bed.
            SetInt(serialized, "m_DSPBufferSize", 512);
            SetInt(serialized, "m_VirtualVoiceCount", 512);
            // The voice pool prewarms 12 and the music banks hold at most 8 stems, so 32 real voices is
            // headroom rather than a budget the game can actually reach (§4.18).
            SetInt(serialized, "m_RealVoiceCount", 32);
            SetBool(serialized, "m_DisableAudio", false);
            SetBool(serialized, "m_VirtualizeEffects", true);
            SetString(serialized, "m_SpatializerPlugin", string.Empty);
            SetString(serialized, "m_AmbisonicDecoderPlugin", string.Empty);

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ApplyAndroidPlayerSettings()
        {
            const BuildTargetGroup group = BuildTargetGroup.Android;

            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.applicationIdentifier = ApplicationId;
            PlayerSettings.bundleVersion = BundleVersion;
            PlayerSettings.Android.bundleVersionCode = 1;

            // Portrait only, and the safe area is respected by the UI layer (§4.19) rather than by cropping
            // the render target, so a notch never hides a lane.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToPortrait = true;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.Android.renderOutsideSafeArea = true;

            // Linear is required for URP lighting to look as authored; the manifest already sets it, and
            // re-asserting here means a project opened with a different default is corrected rather than
            // silently rendering washed-out greybox materials.
            PlayerSettings.colorSpace = ColorSpace.Linear;

            PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            // Auto tracks the installed platform's newest SDK, which is what Play's target-API requirement
            // needs; pinning a number here would silently fall out of compliance on the next policy change.
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.buildApkPerCpuArchitecture = false;
            PlayerSettings.Android.preferredInstallLocation = AndroidInstallLocation.Auto;
            PlayerSettings.Android.androidTVCompatibility = false;

            // AAB is the Play Store requirement, and Play Asset Delivery is how worlds stay out of the
            // 150 MB base module (§11.1).
            EditorUserBuildSettings.buildAppBundle = true;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;

            // Vulkan first for the lower driver overhead on a 60 FPS runner, GLES3 as the fallback for the
            // devices whose Vulkan driver is unreliable. Order matters: Unity picks the first supported one.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                GraphicsDeviceType.Vulkan,
                GraphicsDeviceType.OpenGLES3
            });

            PlayerSettings.stripEngineCode = true;
            // Low stripping for M1: the codebase is reflection-free, but the vendor SDKs arriving in M5
            // (Firebase, IAP, mediation) rely on reflection and need their preserve lists respected. Raising
            // this is an M7 release-gate decision, made with a device smoke test rather than by default.
            PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.Low);

            // Incremental GC spreads collection across frames instead of one stop-the-world pause, which is
            // what makes §11.1's "no growth across 20 runs" achievable without a manual Collect call.
            PlayerSettings.gcIncremental = true;

            // Swappy frame pacing: presents on the display's cadence rather than as fast as the CPU allows,
            // which removes the micro-stutter a 60 FPS runner shows even when its average frame time is fine.
            PlayerSettings.Android.optimizedFramePacing = true;

            // Needed by analytics, ads, Remote Config and cloud save from M5. Declared now so the manifest
            // does not change between the M1 greybox and the first connected build.
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.Android.forceSDCardPermission = false;

            EnsureDevDefine(group);
        }

        /// <summary>
        /// Adds DEV without disturbing any define already present, and without touching the other platforms'
        /// define sets — a shared string would leak a mobile-only symbol into a desktop build.
        /// </summary>
        private static void EnsureDevDefine(BuildTargetGroup group)
        {
            string existing = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            if (ContainsDefine(existing, DevDefine)) return;

            string updated = string.IsNullOrEmpty(existing) ? DevDefine : existing + ";" + DevDefine;
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, updated);
        }

        /// <summary>Ordinal, whole-token match: "DEVICE" or "MYDEV" must not count as the DEV define.</summary>
        internal static bool ContainsDefine(string defines, string symbol)
        {
            if (string.IsNullOrEmpty(defines)) return false;

            string[] parts = defines.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(), symbol, System.StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Registers Boot.unity as the only scene. M1 has no Main.unity: §3.5's split needs a UI layer and an
        /// Addressables preload step, both of which arrive in M3. Adding a second entry now would ship an
        /// empty scene that the boot flow never loads.
        /// </summary>
        private static void ApplyBuildScenes()
        {
            if (!SkyriftPaths.AssetExists(SkyriftPaths.BootScene))
            {
                Debug.LogWarning("[Skyrift] Boot.unity has not been built yet; build scenes were left unchanged. " +
                                 "Run Tools > Skyrift > Build Greybox Scene, then re-run setup.");
                return;
            }

            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes != null && scenes.Length == 1 && scenes[0].path == SkyriftPaths.BootScene && scenes[0].enabled)
                return;

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SkyriftPaths.BootScene, true) };
        }

        private static void SetInt(SerializedObject serialized, string property, int value)
        {
            SerializedProperty found = serialized.FindProperty(property);
            if (found == null) { WarnMissing(property); return; }
            found.intValue = value;
        }

        private static void SetBool(SerializedObject serialized, string property, bool value)
        {
            SerializedProperty found = serialized.FindProperty(property);
            if (found == null) { WarnMissing(property); return; }
            found.boolValue = value;
        }

        private static void SetString(SerializedObject serialized, string property, string value)
        {
            SerializedProperty found = serialized.FindProperty(property);
            if (found == null) { WarnMissing(property); return; }
            found.stringValue = value;
        }

        private static void WarnMissing(string property)
        {
            Debug.LogWarning($"[Skyrift] Project settings asset has no '{property}'; left at its default.");
        }
    }
}
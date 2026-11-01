using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// The release gate (TECH_DOC §11.2, §12.2). Runs before every player build and refuses to produce one
    /// that contradicts the project's own contracts: an unvalidated catalog, a stripped-down platform setup,
    /// or a shipped player that still has the development defines on.
    ///
    /// <b>Why it refuses instead of fixing.</b> A pre-build hook cannot reliably change scripting defines:
    /// <c>SetScriptingDefineSymbolsForGroup</c> asks for a domain reload, which a build in progress will not
    /// perform, so the player can end up compiled with the defines from <i>before</i> the edit while the log
    /// claims otherwise. The two menu items below do the swap outside a build, where the reload happens
    /// normally, and the hook's job is to make an unprepared project fail loudly instead of shipping something
    /// whose behaviour nobody can reproduce.
    ///
    /// Platform settings are different — those are plain PlayerSettings writes that the build reads after
    /// preprocess, so they are re-asserted rather than merely checked (<see cref="PlatformSetup"/> authors the
    /// same values, and this is the copy that survives someone changing them in the Inspector).
    /// </summary>
    public sealed class SettingsEnforcer : IPreprocessBuildWithReport
    {
        /// <summary>
        /// Release define. Present exactly when <see cref="PlatformSetup.DevDefine"/> is not, so gameplay can
        /// distinguish "shipping" from "neither" instead of inferring it from a missing symbol.
        /// </summary>
        public const string ReleaseDefine = "PROD";

        /// <summary>Negative so this runs before vendor hooks that might also edit player settings.</summary>
        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android)
            {
                // Greybox content and the catalog rules apply to any target, so they are not skipped; only the
                // Android-specific asserts are, because querying them from a desktop target reads the Android
                // group anyway and a failure there would be a false positive.
                Debug.Log($"[Skyrift] Building for {report.summary.platform}; Android player asserts skipped.");
            }
            else
            {
                AssertPlatformContract();
            }

            AssertCatalog();
            AssertDefines(report.summary.options);
        }

        // ── Asserts ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Re-asserts the values that change what ships rather than what is in the editor: the packaging
        /// format, the architecture set, the API floor, the scripting backend and the scene list. Each write is
        /// logged only when it actually differs, so a clean project produces a clean log.
        /// </summary>
        private static void AssertPlatformContract()
        {
            var corrections = new List<string>(4);

            if (!EditorUserBuildSettings.buildAppBundle)
            {
                EditorUserBuildSettings.buildAppBundle = true;
                corrections.Add("buildAppBundle → true (Play requires AAB)");
            }

            if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
            {
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                corrections.Add("targetArchitectures → ARM64 only (§11.1's binary size budget)");
            }

            if (PlayerSettings.Android.minSdkVersion < AndroidSdkVersions.AndroidApiLevel26)
            {
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
                corrections.Add("minSdkVersion → API 26");
            }

            if (PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android) != ScriptingImplementation.IL2CPP)
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                corrections.Add("scripting backend → IL2CPP");
            }

            if (!PlayerSettings.gcIncremental)
            {
                PlayerSettings.gcIncremental = true;
                corrections.Add("gcIncremental → true (§11.1's frame-time budget)");
            }

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                PlayerSettings.colorSpace = ColorSpace.Linear;
                corrections.Add("colorSpace → Linear (URP lighting as authored)");
            }

            if (!HasBootScene())
            {
                // Nothing else can recover from this: a player with no enabled scene boots into an empty
                // runtime, which looks like a crash on the device and nothing like a mistake in the editor.
                if (SkyriftPaths.AssetExists(SkyriftPaths.BootScene))
                {
                    EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(SkyriftPaths.BootScene, true) };
                    corrections.Add($"build scenes → {SkyriftPaths.BootScene}");
                }
                else
                {
                    throw new BuildFailedException(
                        "[Skyrift] No Boot.unity at " + SkyriftPaths.BootScene + ". Run Tools > Skyrift > Build M1 Greybox.");
                }
            }

            if (corrections.Count > 0)
            {
                var sb = new StringBuilder("[Skyrift] Release build corrected platform settings:");
                for (int i = 0; i < corrections.Count; i++) sb.Append("\n  · ").Append(corrections[i]);
                Debug.LogWarning(sb.ToString());
            }
        }

        private static bool HasBootScene()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes == null) return false;

            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].enabled && scenes[i].path == SkyriftPaths.BootScene) return true;
            }

            return false;
        }

        /// <summary>
        /// The content gate. Running the full validator here means an unpassable rooftop, a pool below its
        /// worst case or a prefab that disagrees with its definition cannot reach a device — §12.2's CI step
        /// and a local build share one implementation, so they cannot disagree about what is valid.
        /// </summary>
        private static void AssertCatalog()
        {
            if (CatalogValidator.Run()) return;

            throw new BuildFailedException(
                "[Skyrift] Catalog validation failed. The ERROR lines above name the rule and the asset; " +
                "fix them, or run Tools > Skyrift > Validate Catalog to reproduce them outside a build.");
        }

        /// <summary>
        /// DEV and PROD are mutually exclusive by contract: the first turns on the debug HUD, keyboard input
        /// and free continues (§4.12), the second is what those paths test against. A build whose defines
        /// contradict its development flag would ship behaviour no test ever exercised.
        /// </summary>
        private static void AssertDefines(BuildOptions options)
        {
            bool development = (options & BuildOptions.Development) != 0;
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Android);

            bool hasDev = PlatformSetup.ContainsDefine(defines, PlatformSetup.DevDefine);
            bool hasRelease = PlatformSetup.ContainsDefine(defines, ReleaseDefine);

            if (hasDev && hasRelease)
            {
                throw new BuildFailedException(
                    $"[Skyrift] Both '{PlatformSetup.DevDefine}' and '{ReleaseDefine}' are defined for Android. " +
                    "Run Tools > Skyrift > Set Development Defines or Set Release Defines to pick one.");
            }

            if (!development && hasDev)
            {
                throw new BuildFailedException(
                    $"[Skyrift] A non-development player is being built while '{PlatformSetup.DevDefine}' is still defined. " +
                    "That build would ship the debug HUD path, keyboard input selection and free continues as compiled-in " +
                    $"behaviour. Run Tools > Skyrift > Set Release Defines ('{ReleaseDefine}' on, '{PlatformSetup.DevDefine}' off) " +
                    "and build again.");
            }

            if (development && hasRelease)
            {
                throw new BuildFailedException(
                    $"[Skyrift] A development player is being built while '{ReleaseDefine}' is defined, so none of the " +
                    $"dev paths the profiler session depends on are compiled in. Run Tools > Skyrift > Set Development Defines " +
                    $"('{PlatformSetup.DevDefine}' on, '{ReleaseDefine}' off).");
            }

            // A release build without a development flag and with neither define is legal — §4.12's guards are
            // all "#if !DEV", so absence of DEV is already the correct release state. PROD is authored because
            // it is clearer in a stack trace than a missing symbol, not because anything requires it.
        }

        // ── Define swaps ────────────────────────────────────────────────────────────────────────────────

        [MenuItem("Tools/Skyrift/Set Development Defines")]
        public static void SetDevelopmentDefines()
        {
            WriteDefines(PlatformSetup.DevDefine, ReleaseDefine);
        }

        [MenuItem("Tools/Skyrift/Set Release Defines")]
        public static void SetReleaseDefines()
        {
            WriteDefines(ReleaseDefine, PlatformSetup.DevDefine);
        }

        /// <summary>
        /// One swap: add <paramref name="enable"/> and drop <paramref name="disable"/>, leaving every other
        /// define (a vendor SDK's symbol, a platform tag) exactly where the developer put it. Editing defines
        /// outside a build is what lets the domain reload finish before anything reads the result.
        /// </summary>
        private static void WriteDefines(string enable, string disable)
        {
            const BuildTargetGroup group = BuildTargetGroup.Android;
            string current = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);

            var kept = new List<string>(4);
            if (!string.IsNullOrEmpty(current))
            {
                string[] parts = current.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string part = parts[i].Trim();
                    if (part.Length == 0) continue;
                    if (part == disable) continue;
                    if (part == enable) continue;   // re-added below, so a reorder does not duplicate it.
                    kept.Add(part);
                }
            }

            kept.Add(enable);
            string updated = string.Join(";", kept);
            bool changed = !string.Equals(current ?? string.Empty, updated, System.StringComparison.Ordinal);

            if (changed) PlayerSettings.SetScriptingDefineSymbolsForGroup(group, updated);

            Debug.Log($"[Skyrift] Android defines are now '{updated}'. " +
                      (changed
                          ? (enable == ReleaseDefine
                              ? "Release state: debug HUD, keyboard input and free continues are compiled out. Build with Development Player unticked."
                              : "Development state: debug paths are live again.")
                          : "Unchanged."));
        }
    }
}

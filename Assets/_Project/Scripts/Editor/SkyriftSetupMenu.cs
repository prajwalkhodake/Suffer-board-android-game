using System.Diagnostics;
using Skyrift.Data;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// One-click assembly of the M1 greybox (TECH_DOC §3.1, §12.1). The point is not convenience — it is that
    /// the phases have a dependency order which nothing enforces at runtime, and getting it wrong produces a
    /// confusing error instead of the real cause. So the order lives in exactly one place, this file, and the
    /// per-step items exist for when a developer wants to re-run one phase against that same order.
    ///
    /// <list type="number">
    /// <item><see cref="ProjectSetup"/> — URP pipelines and quality levels; nothing reads them yet, but every
    /// later phase assumes a linear-colour, per-tier pipeline exists.</item>
    /// <item><see cref="PlatformSetup"/> — layers, physics, time, audio, Android player settings. Prefabs are
    /// authored onto the layers this writes, so it must run before them.</item>
    /// <item><see cref="InputAssetBuilder"/> — the action asset the scene wires to both input sources.</item>
    /// <item><see cref="GreyboxData.BuildConfigs"/> — the configs are leaves; three later phases read them.</item>
    /// <item><see cref="GreyboxPrefabs"/> — geometry needs <c>laneWidth</c> and the player volume.</item>
    /// <item><see cref="AudioAssetBuilder"/> — the mixer, cues and the voice prefab the pool references.</item>
    /// <item><see cref="GreyboxData.BuildContent"/> — definitions now have prefabs and an audio id to point at.</item>
    /// <item><see cref="SceneBuilder"/> — wires everything the previous six produced.</item>
    /// <item><see cref="CatalogValidator"/> — proves the result rather than assuming it.</item>
    /// </list>
    ///
    /// <b>Idempotent by design.</b> Without <c>overwrite</c> every phase leaves existing assets untouched, so
    /// re-running the menu after a hand edit does not lose it. The overwrite variant is the explicit
    /// "regenerate from the content table" action, and even it does not reset authored <em>numbers</em>:
    /// configs are written with identity only, so a tuned <see cref="BalanceConfig"/> survives a rebuild and a
    /// reset is a deliberate delete in the Inspector instead (§5.13).
    /// </summary>
    public static class SkyriftSetupMenu
    {
        private const string Root = "Tools/Skyrift/";

        [MenuItem(Root + "Build M1 Greybox", priority = 1)]
        public static void BuildGreybox() => RunAll(false);

        [MenuItem(Root + "Rebuild M1 Greybox (Overwrite)", priority = 2)]
        public static void RebuildGreybox() => RunAll(true);

        [MenuItem(Root + "Step 1 · Project Settings", priority = 20)]
        public static void Step1Project()
        {
            ProjectSetup.Run();
        }

        [MenuItem(Root + "Step 2 · Platform Settings", priority = 21)]
        public static void Step2Platform()
        {
            PlatformSetup.Run();
        }

        [MenuItem(Root + "Step 3 · Input Actions", priority = 22)]
        public static void Step3Input()
        {
            InputAssetBuilder.Build();
        }

        [MenuItem(Root + "Step 4 · Config Assets", priority = 23)]
        public static void Step4Configs()
        {
            // Never an overwrite, even from the rebuild path: the writer callback for configs is null, so an
            // overwrite would only rewrite ids — and a stale id is a broken reference, not a lost tuning.
            GreyboxData.BuildConfigs();
        }

        [MenuItem(Root + "Step 5 · Greybox Prefabs", priority = 24)]
        public static void Step5Prefabs()
        {
            GreyboxConfigBundle configs = LoadConfigs();
            if (configs == null) return;
            GreyboxPrefabs.Run(configs.balance);
        }

        [MenuItem(Root + "Step 6 · Audio Assets", priority = 25)]
        public static void Step6Audio()
        {
            GreyboxConfigBundle configs = LoadConfigs();
            if (configs == null) return;
            AudioAssetBuilder.Run(configs.audio);
        }

        [MenuItem(Root + "Step 7 · Data Assets", priority = 26)]
        public static void Step7Data()
        {
            GreyboxConfigBundle configs = LoadConfigs();
            if (configs == null) return;
            GreyboxData.BuildContent(configs);
        }

        [MenuItem(Root + "Step 8 · Boot Scene", priority = 27)]
        public static void Step8Scene()
        {
            SceneBuilder.Run(false);
        }

        /// <summary>
        /// SceneBuilder verifies rather than mutates an existing scene, because a generated scene is still a
        /// place where a developer tunes lighting and camera by eye. This is the escape hatch that comment
        /// refers to, so it has to exist as an item and not only as a parameter.
        /// </summary>
        [MenuItem(Root + "Rebuild Boot Scene", priority = 28)]
        public static void RebuildScene()
        {
            SceneBuilder.Run(true);
        }

        // ── The full run ────────────────────────────────────────────────────────────────────────────────

        private static void RunAll(bool overwrite)
        {
            Stopwatch watch = Stopwatch.StartNew();
            string label = overwrite ? "rebuild (overwrite)" : "build";
            Debug.Log($"[Skyrift] === M1 greybox {label}: 9 phases in dependency order ===");

            // 1–2. Infrastructure. Both always re-apply: a drift in a physics or frame-timing value changes the
            // feel of the game and the reproducibility of a seed, so it is not content a hand edit owns.
            ProjectSetup.Run();
            PlatformSetup.Run();

            // 3. Input before the scene, which assigns the asset to both sources.
            InputAssetBuilder.Build();

            // 4. Configs. Existing assets keep their numbers either way; overwrite only re-writes identity.
            GreyboxConfigBundle configs = GreyboxData.BuildConfigs(overwrite);
            if (configs == null || configs.balance == null || configs.audio == null)
            {
                Debug.LogError("[Skyrift] Config assets could not be authored; stopping the setup run. " +
                               "The phases below all read them, so continuing would only produce misleading errors.");
                return;
            }

            // 5–6. Art and sound, both of which the data rows reference by path.
            GreyboxPrefabs.Run(configs.balance, overwrite);
            AudioAssetBuilder.Run(configs.audio, overwrite);

            // 7. Data, after the prefabs exist — the PoolCatalog holds direct references to them.
            GreyboxContentBundle content = GreyboxData.BuildContent(configs, overwrite);
            if (content == null || content.manifest == null)
            {
                Debug.LogError("[Skyrift] Data assets could not be authored; the scene and validation were skipped.");
                return;
            }

            // 8. The scene, which is the last thing that can fail for a missing reference.
            SceneBuilder.Run(overwrite);

            // 9. Prove it. Validation is not a formality: it is the only step that reads the geometry the
            // other eight produced, and it is what a CI build gate runs too (§12.2).
            bool clean = CatalogValidator.Run();

            watch.Stop();
            Debug.Log($"[Skyrift] === M1 greybox {label} finished in {watch.Elapsed.TotalSeconds:F1} s · " +
                      (clean
                          ? "catalog clean. Open " + SkyriftPaths.BootScene + " and press Play."
                          : "catalog reported errors — see the ERROR lines above. Play will still boot: the boot " +
                            "path drops invalid entries rather than failing, which is the PROD behaviour (§5.14).") +
                      " If this is a release build, run Tools > Skyrift > Set Release Defines first.");
        }

        // ── Step support ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the config bundle from disk so a single step can run on its own. Paths come from
        /// <see cref="GreyboxData"/> rather than a second copy of the strings, and a missing asset names the
        /// menu item that creates it — the failure a developer sees when they run steps out of order.
        /// </summary>
        private static GreyboxConfigBundle LoadConfigs()
        {
            var bundle = new GreyboxConfigBundle
            {
                balance = AssetDatabase.LoadAssetAtPath<BalanceConfig>(GreyboxData.BalancePath),
                trackGeneration = AssetDatabase.LoadAssetAtPath<TrackGenerationConfig>(GreyboxData.TrackGenerationPath),
                input = AssetDatabase.LoadAssetAtPath<InputConfig>(GreyboxData.InputPath),
                scoring = AssetDatabase.LoadAssetAtPath<ScoringConfig>(GreyboxData.ScoringPath),
                continueConfig = AssetDatabase.LoadAssetAtPath<ContinueConfig>(GreyboxData.ContinuePath),
                audio = AssetDatabase.LoadAssetAtPath<AudioConfig>(GreyboxData.AudioPath)
            };

            if (bundle.balance != null && bundle.audio != null && bundle.input != null
                && bundle.scoring != null && bundle.trackGeneration != null && bundle.continueConfig != null)
                return bundle;

            Debug.LogError("[Skyrift] Missing config asset(s): " + Missing(bundle) +
                           ". Run Tools > Skyrift > Step 4 · Config Assets first — the phases after it read these.");
            return null;
        }

        private static string Missing(GreyboxConfigBundle bundle)
        {
            var sb = new System.Text.StringBuilder(64);
            AppendIfMissing(sb, "cfg_balance", bundle.balance);
            AppendIfMissing(sb, "cfg_track_generation", bundle.trackGeneration);
            AppendIfMissing(sb, "cfg_input", bundle.input);
            AppendIfMissing(sb, "cfg_scoring", bundle.scoring);
            AppendIfMissing(sb, "cfg_continue", bundle.continueConfig);
            AppendIfMissing(sb, "cfg_audio", bundle.audio);
            return sb.Length == 0 ? "none" : sb.ToString();
        }

        private static void AppendIfMissing(System.Text.StringBuilder sb, string name, Object asset)
        {
            if (asset != null) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(name);
        }
    }
}

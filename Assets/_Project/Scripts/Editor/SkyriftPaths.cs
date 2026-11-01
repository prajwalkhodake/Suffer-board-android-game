using UnityEditor;

namespace Skyrift.Editor
{
    /// <summary>
    /// The single source of truth for asset paths (TECH_DOC §3.1). Every editor builder writes through
    /// these constants, so a folder move is one edit here rather than a hunt through six tools, and the
    /// layout on disk always matches the documented one.
    ///
    /// Paths are Unity asset paths ("Assets/..."), never absolute file-system paths: they are what
    /// <c>AssetDatabase</c> and Addressables addresses are keyed on.
    /// </summary>
    public static class SkyriftPaths
    {
        public const string ProjectRoot = "Assets/_Project";

        // ── Art ─────────────────────────────────────────────────────────────────────────────────────────
        public const string Art = ProjectRoot + "/Art";
        public const string Materials = Art + "/Materials";
        public const string Models = Art + "/Models";
        public const string Textures = Art + "/Textures";

        // ── Audio ───────────────────────────────────────────────────────────────────────────────────────
        public const string Audio = ProjectRoot + "/Audio";
        public const string AudioMusic = Audio + "/Music";
        public const string AudioStems = AudioMusic + "/Stems";
        public const string AudioSfx = Audio + "/SFX";
        public const string AudioMixers = Audio + "/Mixers";

        // ── Prefabs ─────────────────────────────────────────────────────────────────────────────────────
        public const string Prefabs = ProjectRoot + "/Prefabs";
        public const string PrefabsPlayer = Prefabs + "/Player";
        public const string PrefabsChunks = Prefabs + "/Chunks";
        public const string PrefabsObstacles = Prefabs + "/Obstacles";
        public const string PrefabsCollectibles = Prefabs + "/Collectibles";
        public const string PrefabsProps = Prefabs + "/Props";
        public const string PrefabsVfx = Prefabs + "/VFX";
        public const string PrefabsAudio = Prefabs + "/Audio";

        // ── Scenes & settings ───────────────────────────────────────────────────────────────────────────
        public const string Scenes = ProjectRoot + "/Scenes";
        public const string Settings = ProjectRoot + "/Settings";
        public const string SettingsUrp = Settings + "/URP";
        public const string SettingsQuality = Settings + "/Quality";
        public const string SettingsInput = Settings + "/Input";
        public const string SettingsAddressables = Settings + "/AddressablesGroups";

        // ── Data ────────────────────────────────────────────────────────────────────────────────────────
        public const string Data = ProjectRoot + "/Data";
        public const string DataWorlds = Data + "/Worlds";
        public const string DataChunks = Data + "/Chunks";
        public const string DataObstacles = Data + "/Obstacles";
        public const string DataBehaviours = Data + "/Obstacles/Behaviours";
        public const string DataCatalogs = Data + "/Catalogs";
        public const string DataBalance = Data + "/Balance";
        public const string DataScoring = Data + "/Scoring";
        public const string DataInput = Data + "/Input";
        public const string DataAudio = Data + "/Audio";
        public const string DataContinue = Data + "/Continue";
        public const string DataTrack = Data + "/Track";

        /// <summary>The only scene M1 ships. §3.5's Boot/Main split arrives with the UI layer in M3.</summary>
        public const string BootScene = Scenes + "/Boot.unity";

        /// <summary>
        /// Creates every folder the builders write into. Idempotent and cheap: <c>AssetDatabase.IsValidFolder</c>
        /// is a lookup, and a folder is only created when it is genuinely missing, so re-running the setup tool
        /// does not churn GUIDs or trigger a reimport of existing content.
        /// </summary>
        public static void EnsureAllFolders()
        {
            string[] folders =
            {
                Art, Materials, Models, Textures,
                Audio, AudioMusic, AudioStems, AudioSfx, AudioMixers,
                Prefabs, PrefabsPlayer, PrefabsChunks, PrefabsObstacles, PrefabsCollectibles,
                PrefabsProps, PrefabsVfx, PrefabsAudio,
                Scenes, Settings, SettingsUrp, SettingsQuality, SettingsInput, SettingsAddressables,
                Data, DataWorlds, DataChunks, DataObstacles, DataBehaviours, DataCatalogs,
                DataBalance, DataScoring, DataInput, DataAudio, DataContinue, DataTrack
            };

            for (int i = 0; i < folders.Length; i++) EnsureFolder(folders[i]);
        }

        /// <summary>
        /// Creates one folder and any missing parent. <c>AssetDatabase.CreateFolder</c> refuses a path whose
        /// parent does not exist, so the walk is top-down rather than a single call.
        /// </summary>
        public static void EnsureFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath)) return;

            int slash = assetPath.LastIndexOf('/');
            if (slash <= 0) return;   // "Assets" itself always exists

            string parent = assetPath.Substring(0, slash);
            string leaf = assetPath.Substring(slash + 1);

            EnsureFolder(parent);
            if (!AssetDatabase.IsValidFolder(assetPath)) AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>Asset path of a generated placeholder clip, named after the cue it stands in for.</summary>
        public static string SfxClip(string cueId) => AudioSfx + "/" + cueId + ".wav";

        /// <summary>Asset path of a generated music stem.</summary>
        public static string StemClip(string stemName) => AudioStems + "/" + stemName + ".wav";

        /// <summary>
        /// True when an asset already exists at this path. The setup menu uses it to report whether the
        /// greybox has been built, without loading the asset just to test for presence.
        /// </summary>
        public static bool AssetExists(string assetPath)
        {
            return !string.IsNullOrEmpty(assetPath)
                   && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null;
        }
    }
}
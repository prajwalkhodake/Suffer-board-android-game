using Skyrift.Audio;
using Skyrift.Bootstrap;
using Skyrift.Controls;
using Skyrift.Data;
using Skyrift.Gameplay;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Skyrift.Editor
{
    /// <summary>
    /// Builds <c>Boot.unity</c> and wires every <see cref="BootstrapConfig"/> reference (TECH_DOC §3.5, §4.12).
    /// This is the step that turns the prefabs and data assets into a playable game: the composition root
    /// refuses to boot when a reference is missing, and it names the field, so this builder has to satisfy
    /// exactly the list below and nothing else.
    ///
    /// <b>What the boot pipeline requires, in step order.</b> Slots 10/30/80/95/100 fail on
    /// <c>poolRoot</c>, <c>catalog</c>, <c>obstacleBehaviours</c>, <c>audioCatalog</c>, <c>trackRoot</c>,
    /// <c>playerMotor</c>, <c>playerSensor</c>, <c>chaser</c>, <c>inputActions</c> and <c>audioRoot</c>.
    /// <c>vfxRoot</c> is optional in M1 (there is no VFX yet) and <c>mixer</c> is optional by contract —
    /// <see cref="AudioService"/> falls back to per-source gain without it. Assigning them anyway is what
    /// makes the greybox a faithful stand-in for the shipped scene graph.
    ///
    /// <b>Scene objects, not instances.</b> Every object here is created once and lives for the session.
    /// The player rig and the chaser rig are prefab <i>instances</i> in the scene rather than pooled objects,
    /// because <c>PlayerMotor</c> and <c>ChaserSystem</c> take injected references at composition time and a
    /// pool would discard them on release (§11.2 rule 1 holds for the boot path too).
    ///
    /// <b>Ownership.</b> The scene is generated, but its lighting and camera settings are the kind of thing a
    /// developer tunes by eye. So a rebuild is explicit: without <c>overwrite</c> an existing Boot scene is
    /// opened and <b>verified</b>, never modified. Partial preservation — references relinked, knobs left
    /// alone — is a promise this tool cannot keep without a compiler to check it, and a silent half-update
    /// is worse than a message telling you which menu item to run.
    /// </summary>
    public static class SceneBuilder
    {
        // ── Names ───────────────────────────────────────────────────────────────────────────────────────
        // Constants rather than literals, because the verification pass has to look the objects up again and
        // a typo in one of the two places would report "missing" forever.
        private const string GameRootName = "GameRoot";
        private const string CameraName = "Main Camera";
        private const string LightName = "Directional Light";
        private const string PlayerRootName = "PlayerRoot";
        private const string ChaserRootName = "ChaserRoot";
        private const string TrackRootName = "TrackRoot";
        private const string PoolRootName = "PoolRoot";
        private const string VfxRootName = "VfxRoot";
        private const string AudioRootName = "AudioRoot";
        private const string InputRootName = "Input";
        private const string TouchName = "TouchInput";
        private const string KeyboardName = "KeyboardInput";
        private const string HudName = "DebugHud";

        /// <summary>
        /// Exponential fog density chosen to hide the track recycle boundary rather than to look atmospheric.
        /// The live window reaches ~290 m while the camera's far clip is 260 m, so transmittance must be
        /// effectively zero before a chunk can appear: at 0.0085 a surface 120 m away is already at 35% and
        /// 260 m is at 7%, while the next obstacle (always within ~60 m) stays at 89%.
        /// </summary>
        private const float FogDensity = 0.0085f;

        public static void Run(bool overwrite = false)
        {
            SkyriftPaths.EnsureAllFolders();

            // Content first: a scene whose references are missing is a boot failure that names a field, and
            // the field is far less informative than "the asset was never built".
            CatalogManifest manifest = AssetDatabase.LoadAssetAtPath<CatalogManifest>(GreyboxData.ManifestPath);
            if (manifest == null)
            {
                Debug.LogError("[Skyrift] No CatalogManifest at " + GreyboxData.ManifestPath +
                               ". Run Tools > Skyrift > Build M1 Greybox, which authors content before the scene.");
                return;
            }

            ObstacleBehaviourCatalog behaviours = AssetDatabase.LoadAssetAtPath<ObstacleBehaviourCatalog>(
                GreyboxData.BehaviourCatalogPath);
            AudioCatalog audioCatalog = AssetDatabase.LoadAssetAtPath<AudioCatalog>(AudioAssetBuilder.CatalogPath);
            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(AudioAssetBuilder.MixerPath);
            InputActionAsset inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputAssetBuilder.AssetPath);
            GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GreyboxPrefabs.PlayerPrefabPath);
            GameObject chaserPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GreyboxPrefabs.ChaserPrefabPath);

            if (behaviours == null || audioCatalog == null || inputActions == null || playerPrefab == null)
            {
                Debug.LogError("[Skyrift] Boot scene cannot be wired: " +
                               (behaviours == null ? "behaviour catalog, " : "") +
                               (audioCatalog == null ? "audio catalog, " : "") +
                               (inputActions == null ? "input asset, " : "") +
                               (playerPrefab == null ? "player prefab, " : "") +
                               "missing. Build the greybox first.");
                return;
            }

            if (AssetDatabase.IsValidFolder(SkyriftPaths.Scenes)
                && SkyriftPaths.AssetExists(SkyriftPaths.BootScene) && !overwrite)
            {
                EditorSceneManager.OpenScene(SkyriftPaths.BootScene, OpenSceneMode.Single);
                int problems = Verify(SceneManager.GetActiveScene(), manifest);
                if (problems == 0)
                    Debug.Log("[Skyrift] Boot scene already authored and fully wired. Left untouched — " +
                              "Tools > Skyrift > Rebuild Boot Scene replaces it.");
                else
                    Debug.LogWarning($"[Skyrift] Boot scene has {problems} unassigned reference(s). " +
                                     "Run Tools > Skyrift > Rebuild Boot Scene to regenerate the scene.");
                return;
            }

            // EmptyScene in both branches — reaching here already means the hierarchy is being replaced.
            // DefaultTemplate would add a second light and camera, and two directional lights in a URP scene
            // is a shader-variant surprise nobody looks for.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            Scene scene = SceneManager.GetActiveScene();

            Transform world = Node("World", null, -1).transform;
            RunCameraRig cameraRig = CameraAndLight(world, manifest);
            PlayerAndChaser(world, playerPrefab, chaserPrefab, manifest, cameraRig);
            TrackRoots(world);

            // AudioRoot is deliberately empty. AudioService creates its own stem sources under this transform
            // at boot and the SFX voices come from the pool, so an authored AudioSource would be a second,
            // unbudgeted source of sound. The one AudioListener in the scene lives on the camera.
            Node(AudioRootName, world, -1);
            InputNodes(world, inputActions, manifest);
            GameObject hud = HudNode(world);

            GameObject gameRoot = Node(GameRootName, null, -1);
            gameRoot.AddComponent<GameRoot>();
            WireBootstrapConfig(gameRoot, world, manifest, behaviours, audioCatalog, mixer, inputActions, hud);

            EditorSceneManager.SaveScene(scene, SkyriftPaths.BootScene);
            EnsureInBuildSettings();
            AssetAuthoring.SaveAll();

            Debug.Log("[Skyrift] Boot scene authored: GameRoot, World (camera, light, player rig, chaser rig, " +
                      "track roots, audio, input) and the dev HUD. Both input sources and the HUD are authored " +
                      "inactive — Bootstrap activates exactly one source and the HUD only when showDebugHud is set.");
        }

        // ── Hierarchy ───────────────────────────────────────────────────────────────────────────────────

        private static GameObject Node(string name, Transform parent, int layer)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            if (layer >= 0) go.layer = layer;
            return go;
        }

        /// <summary>
        /// The camera is authored here rather than left to <see cref="RunCameraRig"/>: its Awake adds a Camera
        /// only as a fallback, and a scene needs a real <c>Camera.main</c> for the editor preview, for
        /// culling-mask debugging and for the particle systems M6 adds.
        /// </summary>
        private static RunCameraRig CameraAndLight(Transform world, CatalogManifest manifest)
        {
            WorldDefinition worldDef = manifest.worlds != null && manifest.worlds.Length > 0 ? manifest.worlds[0] : null;
            Color fog = worldDef != null ? worldDef.fogColor : GreyboxContent.Fog;
            Color ambient = worldDef != null ? worldDef.ambientColor : GreyboxContent.Ambient;

            GameObject camera = Node(CameraName, world, -1);
            camera.tag = "MainCamera";   // Camera.main is the only way other systems find it, and it is a tag lookup

            var rig = camera.AddComponent<RunCameraRig>();
            camera.AddComponent<AudioListener>();

            var serialized = new SerializedObject(rig);
            SerializedProperty background = serialized.FindProperty("backgroundColor");
            if (background != null) background.colorValue = fog;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // target is assigned after the player instance exists, below.

            // URP lights are filtered by rendering layer, and a Light created from script does not reliably
            // get the Default bit that every URP material's "Light Layer Mask" includes. Setting it explicitly
            // is the difference between a lit greybox and a black one, and it is invisible in a diff.
            GameObject lightNode = Node(LightName, world, -1);
            Light light = lightNode.AddComponent<Light>();
            light.type = LightType.Directional;
            light.renderingLayerMask = 1u;
            light.intensity = 1.1f;
            light.color = Color.white;
            // No shadows in the greybox: a 4 GB mid-range device pays the shadow map for no readability gain on
            // flat-colour primitives, and the per-tier shadow settings are M6's decision (§11.1).
            light.shadows = LightShadows.None;
            lightNode.transform.localEulerAngles = new Vector3(42f, -28f, 0f);

            // Solid-colour fog and the camera's clear colour are the same authored value, so the horizon is
            // seamless — which is what lets the fog double as the track's fade-in.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = fog;
            RenderSettings.fogDensity = FogDensity;
            RenderSettings.ambientMode = UnityEngine.AmbientMode.Flat;
            RenderSettings.ambientLight = ambient;

            return rig;
        }

        private static void PlayerAndChaser(Transform world, GameObject playerPrefab, GameObject chaserPrefab,
            CatalogManifest manifest, RunCameraRig cameraRig)
        {
            BalanceConfig balance = manifest.balance;

            GameObject playerRoot = Node(PlayerRootName, world, -1);
            GameObject chaserRoot = Node(ChaserRootName, world, -1);

            // The player starts on the identity: the generator emits its first chunk from z = 0 forward, so
            // "at the origin" is "at the start of the track" with no offset to compensate for (§4.5).
            GameObject player = InstantiatePrefab(playerPrefab, playerRoot.transform);

            GameObject chaser = InstantiatePrefab(chaserPrefab, chaserRoot.transform);
            if (chaser == null)
                Debug.LogWarning("[Skyrift] No chaser prefab at " + GreyboxPrefabs.ChaserPrefabPath +
                                 ". The run will boot with no visible pursuer; proximity and cues still work.");

            if (chaser != null)
            {
                // Behind the player at authoring time so the scene is legible before boot. ChaserSystem writes
                // this transform every tick; the value here is only what the editor shows.
                chaser.transform.localPosition = new Vector3(0f, 0f, -balance.chaserFarDistance);
            }

            // ChaserSystem lives on the root and drives the rig, not the other way round (§4.8).
            var system = chaserRoot.AddComponent<ChaserSystem>();
            var serialized = new SerializedObject(system);
            SerializedProperty rig = serialized.FindProperty("rig");
            if (rig != null) rig.objectReferenceValue = chaser != null ? chaser.transform : null;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // The rig the motor writes, not PlayerRoot: the root stays at the identity and the motor re-bases
            // its own transform on an origin shift (§4.6). Assigned here rather than in CameraAndLight because
            // the target does not exist until the player instance does.
            if (cameraRig != null && player != null) cameraRig.Construct(player.transform);
        }

        private static void TrackRoots(Transform world)
        {
            // Containers, so the layer only matters for what is parented under them. VfxRoot carries the Vfx
            // layer for clarity, but children set their own — Transform.SetParent does not inherit layers,
            // which is exactly why AssetAuthoring.Visual does it explicitly.
            Node(TrackRootName, world, -1);
            Node(PoolRootName, world, -1);
            Node(VfxRootName, world, SkyriftLayers.Vfx);
        }

        private static void InputNodes(Transform world, InputActionAsset inputActions, CatalogManifest manifest)
        {
            GameObject inputRoot = Node(InputRootName, world, -1);

            GameObject touch = Node(TouchName, inputRoot.transform, -1);
            var touchSource = touch.AddComponent<TouchInputSource>();
            WireSerialized(touchSource,
                ("actions", inputActions),
                ("config", manifest.input));

            GameObject keyboard = Node(KeyboardName, inputRoot.transform, -1);
            var keyboardSource = keyboard.AddComponent<KeyboardInputSource>();
            WireSerialized(keyboardSource, ("actions", inputActions));

            // Authored inactive. Both share one "Player" action map, so two enabled sources would disable each
            // other's actions on shutdown (§4.3); Bootstrap activates exactly one and leaves the other off.
            touch.SetActive(false);
            keyboard.SetActive(false);
        }

        private static GameObject HudNode(Transform world)
        {
            GameObject hud = Node(HudName, world, -1);
            hud.AddComponent<DebugHud>();
            // Inactive: UiRootStep activates it, so an authored-active HUD would draw before Construct ran.
            hud.SetActive(false);
            return hud;
        }

        // ── Wiring ──────────────────────────────────────────────────────────────────────────────────────

        private static void WireBootstrapConfig(GameObject gameRoot, Transform world, CatalogManifest manifest,
            ObstacleBehaviourCatalog behaviours, AudioCatalog audioCatalog, AudioMixer mixer,
            InputActionAsset inputActions, GameObject hud)
        {
            var serialized = new SerializedObject(gameRoot);
            SerializedProperty config = serialized.FindProperty("config");
            if (config == null)
            {
                Debug.LogError("[Skyrift] GameRoot has no serializable 'config' property; the scene cannot be wired.");
                return;
            }

            Assign(config, "catalog", manifest);
            Assign(config, "obstacleBehaviours", behaviours);
            Assign(config, "audioCatalog", audioCatalog);
            Assign(config, "inputActions", inputActions);

            // Every scene lookup goes through the transform the caller built, never GameObject.Find: the input
            // sources and the HUD are authored inactive, and Find does not return inactive objects — a null
            // would look like a missing node rather than a disabled one.
            Transform playerRoot = world.Find(PlayerRootName);
            Transform chaserRoot = world.Find(ChaserRootName);
            Transform trackRoot = world.Find(TrackRootName);
            Transform poolRoot = world.Find(PoolRootName);
            Transform vfxRoot = world.Find(VfxRootName);
            Transform audioRoot = world.Find(AudioRootName);
            Transform inputRoot = world.Find(InputRootName);

            Assign(config, "playerRoot", playerRoot);
            Assign(config, "chaserRoot", chaserRoot);
            Assign(config, "trackRoot", trackRoot);
            Assign(config, "poolRoot", poolRoot);
            Assign(config, "vfxRoot", vfxRoot);
            Assign(config, "audioRoot", audioRoot);
            Assign(config, "mixer", mixer);

            // The components live on the scene instances, not on the prefabs: a prefab reference would be the
            // asset's own (uninstantiated) component, which is a different object from the one that ticks.
            Assign(config, "playerMotor", FindComponent<PlayerMotor>(playerRoot));
            Assign(config, "playerSensor", FindComponent<PlayerContactSensor>(playerRoot));
            Assign(config, "chaser", chaserRoot != null ? chaserRoot.GetComponent<ChaserSystem>() : null);

            Assign(config, "touchInput", inputRoot != null ? FindComponent<TouchInputSource>(inputRoot.Find(TouchName)) : null);
            Assign(config, "keyboardInput", inputRoot != null ? FindComponent<KeyboardInputSource>(inputRoot.Find(KeyboardName)) : null);
            Assign(config, "debugHud", hud != null ? hud.GetComponent<DebugHud>() : null);

            // Editor convenience default: the first person to open this project is on a keyboard. A device
            // build gets the opposite answer twice over — SelectInput ignores the flag in a non-DEV build, and
            // SettingsEnforcer refuses to build a release player while DEV is still defined, so a stale scene
            // cannot ship a swipe-less game (§4.3, §12.2).
            Bool(config, "useKeyboardInput", EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android);
            Bool(config, "autoStartRun", true);
            Bool(config, "showDebugHud", true);

            // M1 has no key economy, so the debug policy is the only way to exercise crash → continue →
            // respawn at all — and that path is where the ground-height seam and the respawn clearance are
            // tested (§4.12). A non-DEV build compiles the flag away, independently of what the scene says.
            Bool(config, "allowFreeContinues", true);

            // 0 means "derive a fresh seed per run". Left at the default on purpose: a fixed seed in a shipped
            // scene would make every player's track identical, which is a content bug that looks like a
            // feature. Reproduce a run by setting it in the Inspector, not in the generator.
            Int(config, "fixedSeed", 0);

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(gameRoot);
        }

        /// <summary>Tuples rather than a builder: the field set is the boot pipeline's requirement list.</summary>
        private static void WireSerialized(Object target, params (string name, Object value)[] fields)
        {
            var serialized = new SerializedObject(target);
            for (int i = 0; i < fields.Length; i++)
            {
                SerializedProperty property = serialized.FindProperty(fields[i].name);
                if (property == null)
                {
                    Debug.LogWarning($"[Skyrift] {target.name} has no serialized field '{fields[i].name}'.");
                    continue;
                }
                property.objectReferenceValue = fields[i].value;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Assign(SerializedProperty config, string field, Object value)
        {
            SerializedProperty property = config.FindPropertyRelative(field);
            if (property == null)
            {
                Debug.LogWarning($"[Skyrift] BootstrapConfig has no field '{field}'. The scene builder and the " +
                                 "composition root have drifted — reconcile one to the other.");
                return;
            }
            property.objectReferenceValue = value;
        }

        private static void Bool(SerializedProperty config, string field, bool value)
        {
            SerializedProperty property = config.FindPropertyRelative(field);
            if (property != null) property.boolValue = value;
        }

        private static void Int(SerializedProperty config, string field, int value)
        {
            SerializedProperty property = config.FindPropertyRelative(field);
            if (property != null) property.intValue = value;
        }

        private static GameObject InstantiatePrefab(GameObject prefab, Transform parent)
        {
            if (prefab == null) return null;

            // PrefabUtility.Instantiate, not Object.Instantiate: it keeps the prefab connection, so a later
            // rebuild of a gb_ prefab updates the scene instance and its GUID-stuck references.
            return PrefabUtility.Instantiate(prefab, parent) as GameObject;
        }

        private static T FindComponent<T>(Transform root) where T : Component
        {
            // GetComponentInChildren covers the root itself, and includeInactive is what makes the search work
            // on the authored-inactive input sources. A null here becomes an unassigned BootstrapConfig field,
            // which the boot pipeline reports as a refused step naming the field (§4.1).
            return root != null ? root.GetComponentInChildren<T>(true) : null;
        }

        // ── Verification ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Counts the references the boot pipeline refuses to run without. Read-only by design — this runs on
        /// the "scene already exists" path, where the user's lighting and camera tuning outrank the tool's
        /// opinion about what the scene should contain.
        /// </summary>
        private static int Verify(Scene scene, CatalogManifest manifest)
        {
            GameObject gameRoot = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name != GameRootName) continue;
                gameRoot = root;
                break;
            }

            if (gameRoot == null)
            {
                Debug.LogError($"[Skyrift] Boot scene has no {GameRootName} GameObject.");
                return 1;
            }

            var serialized = new SerializedObject(gameRoot);
            SerializedProperty config = serialized.FindProperty("config");
            if (config == null) return 1;

            string[] required =
            {
                "catalog", "obstacleBehaviours", "audioCatalog", "inputActions",
                "playerRoot", "playerMotor", "playerSensor", "chaserRoot", "chaser",
                "trackRoot", "poolRoot", "audioRoot", "touchInput"
            };

            int problems = 0;
            for (int i = 0; i < required.Length; i++)
            {
                SerializedProperty property = config.FindPropertyRelative(required[i]);
                if (property == null || property.objectReferenceValue == null)
                {
                    Debug.LogError($"[Skyrift] Boot scene: BootstrapConfig.{required[i]} is unassigned, " +
                                   "and the boot pipeline refuses on it (§4.1).");
                    problems++;
                }
            }

            if (manifest.balance == null)
            {
                Debug.LogError("[Skyrift] Boot scene: the manifest has no BalanceConfig.");
                problems++;
            }

            return problems;
        }

        /// <summary>
        /// Puts Boot.unity in the build settings. A scene that exists but is not in the list builds into an
        /// empty player, which is a ten-minute debug session caused by one unclicked checkbox.
        /// </summary>
        private static void EnsureInBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path != SkyriftPaths.BootScene) continue;
                if (scenes[i].enabled) return;

                scenes[i].enabled = true;
                EditorBuildSettings.scenes = scenes;
                return;
            }

            var added = new EditorBuildSettingsScene[scenes.Length + 1];
            for (int i = 0; i < scenes.Length; i++) added[i] = scenes[i];
            added[scenes.Length] = new EditorBuildSettingsScene(SkyriftPaths.BootScene, true);
            EditorBuildSettings.scenes = added;
        }
    }
}

using Skyrift.Bootstrap;
using Skyrift.Data;
using Skyrift.Gameplay;
using Skyrift.Track;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// Builds the M1 greybox prefabs (TECH_DOC §3.1, §4.5, §4.7). Placeholder geometry, entirely original,
    /// generated from <see cref="GreyboxContent"/> so the visuals and the clearance data are authored from one
    /// table and cannot disagree.
    ///
    /// <b>Ownership.</b> Every asset is <c>gb_</c>/<c>GB_</c> prefixed and is replaced in place on a rebuild,
    /// which keeps its GUID and therefore every PoolCatalog and scene reference (<see cref="AssetAuthoring"/>).
    /// An artist replacing a placeholder with real art keeps the path and the GUID, and nothing else changes.
    ///
    /// <b>Two rules the geometry has to obey, both from the collision model rather than from taste:</b>
    /// <list type="number">
    /// <item>An obstacle's trigger lives on the prefab <b>root</b>, because <c>ObstacleInstance.Awake</c> caches
    /// <c>GetComponent&lt;Collider&gt;()</c> and the contact index is keyed on that one collider.</item>
    /// <item>A mountable obstacle's trigger carries <b>headroom above the roof</b>. The volume decides whether
    /// an overlap happens, not the outcome (§4.7), and support is withdrawn on trigger exit — so without
    /// headroom, jumping on the spot on a train roof would leave the volume, drop the support, and put the
    /// player through the roof on the way down.</item>
    /// </list>
    /// </summary>
    public static class GreyboxPrefabs
    {
        public const string PlayerPrefabPath = SkyriftPaths.PrefabsPlayer + "/" + GreyboxContent.PlayerPrefabName + ".prefab";
        public const string ChaserPrefabPath = SkyriftPaths.PrefabsPlayer + "/" + GreyboxContent.ChaserPrefabName + ".prefab";

        /// <summary>Shared with <see cref="GreyboxData"/> so a pool row and its prefab can never point apart.</summary>
        public static string ObstaclePrefabPath(string poolKey) => SkyriftPaths.PrefabsObstacles + "/" + poolKey + ".prefab";

        /// <summary>Shared with <see cref="GreyboxData"/> so a pool row and its prefab can never point apart.</summary>
        public static string ChunkPrefabPath(string poolKey) => SkyriftPaths.PrefabsChunks + "/" + poolKey + ".prefab";

        /// <summary>
        /// A body long enough to read as a vehicle rather than a crate. Purely a presentation heuristic: the
        /// simulation only ever reads <c>footprintMetres</c>.
        /// </summary>
        private const float LongBodyMetres = 8f;

        /// <summary>
        /// Builds materials, obstacles, chunks, the player rig and the chaser rig. Idempotent: without
        /// <paramref name="overwrite"/> an existing prefab is left exactly as it is, hand edits included.
        /// </summary>
        public static void Run(BalanceConfig balance, bool overwrite = false)
        {
            if (balance == null)
            {
                Debug.LogError("[Skyrift] GreyboxPrefabs needs a BalanceConfig; lane width and player volume come from it.");
                return;
            }

            SkyriftPaths.EnsureAllFolders();
            // Infrastructure, so it is re-applied even on a content-only rebuild: a layer renamed by another
            // tool would silently move every collider off the sanctioned contact pairs (§11.2 rule 6).
            SkyriftLayers.Ensure();

            Palette palette = BuildMaterials(overwrite);

            ObstacleSpec[] obstacles = GreyboxContent.Obstacles();
            for (int i = 0; i < obstacles.Length; i++)
            {
                // The cap material is passed in rather than reloaded per obstacle: the roof cap and the lane
                // stripes are deliberately the same asset, so "you may stand here" is one accent everywhere.
                BuildObstacle(obstacles[i], palette.obstacle[i], palette.stripe, balance, overwrite);
            }

            ChunkSpec[] chunks = GreyboxContent.Chunks();
            for (int i = 0; i < chunks.Length; i++)
            {
                BuildChunk(chunks[i], palette, balance, overwrite);
            }

            BuildPlayer(balance, palette, overwrite);
            BuildChaser(balance, palette, overwrite);

            AssetAuthoring.SaveAll();
            Debug.Log($"[Skyrift] Greybox prefabs authored ({obstacles.Length} obstacles, {chunks.Length} chunks, " +
                      "player rig, chaser rig). All placeholder geometry, replaceable at a fixed path.");
        }

        // ── Materials ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One material asset per colour, shared by every instance. §11.1's "no per-instance material clones"
        /// budget depends on this: a renderer that instantiates its own material breaks the SRP Batcher.
        /// </summary>
        private sealed class Palette
        {
            public Material ground;
            public Material rail;
            public Material stripe;
            public Material player;
            public Material chaser;
            public Material hound;
            public Material[] obstacle;
        }

        private static Palette BuildMaterials(bool overwrite)
        {
            ObstacleSpec[] specs = GreyboxContent.Obstacles();
            var materials = new Material[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                materials[i] = AssetAuthoring.GreyboxMaterial("GB_" + specs[i].id, specs[i].color, overwrite);
            }

            return new Palette
            {
                ground = AssetAuthoring.GreyboxMaterial("GB_Ground", GreyboxContent.Ground, overwrite),
                rail = AssetAuthoring.GreyboxMaterial("GB_Rail",
                    Color.Lerp(GreyboxContent.Ground, Color.white, 0.12f), overwrite),
                // Doubles as the roof cap on mountable obstacles: one accent reads as "you may stand here"
                // wherever it appears, which is the colourblind-safe half of the indicator (shape is the other).
                stripe = AssetAuthoring.GreyboxMaterial("GB_LaneStripe",
                    Color.Lerp(GreyboxContent.Accent, GreyboxContent.Ground, 0.35f), overwrite, 0.2f,
                    Color.Lerp(GreyboxContent.Accent, Color.black, 0.8f)),
                player = AssetAuthoring.GreyboxMaterial("GB_Player", new Color(0.92f, 0.93f, 0.95f), overwrite),
                chaser = AssetAuthoring.GreyboxMaterial("GB_Chaser", new Color(0.60f, 0.12f, 0.15f), overwrite),
                hound = AssetAuthoring.GreyboxMaterial("GB_Hound", new Color(0.35f, 0.10f, 0.12f), overwrite),
                obstacle = materials
            };
        }

        // ── Obstacles ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Headroom above a mountable roof, derived from the authored jump rather than guessed: the highest
        /// feet the player can reach above the roof is <c>jumpHeight + jumpModifierBonus</c> (super sneakers),
        /// and one standing height of margin on top keeps the capsule inside the volume at the apex.
        /// </summary>
        private static float MountHeadroom(BalanceConfig balance)
        {
            return balance.jumpHeight + balance.jumpModifierBonus + balance.playerStandHeight;
        }

        private static GameObject BuildObstacle(ObstacleSpec spec, Material material, Material capMaterial,
            BalanceConfig balance, bool overwrite)
        {
            string path = ObstaclePrefabPath(spec.poolKey);
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !overwrite) return existing;

            var root = new GameObject(spec.poolKey);
            root.layer = SkyriftLayers.Obstacle;

            float width = balance.laneWidth * GreyboxContent.ObstacleWidthFactor;
            float height = spec.footprint.y;
            float length = spec.footprint.x;
            float headroom = spec.clearance.mountable ? MountHeadroom(balance) : 0f;

            // The populator places the root at (laneX, 0, zOffset), so the volume sits on the ground plane and
            // grows upward. Its centre is half the *collider* height, not half the footprint — the headroom is
            // invisible to the resolver, which reads footprintMetres.y as the top surface (§4.7).
            var box = root.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = new Vector3(0f, (height + headroom) * 0.5f, 0f);
            box.size = new Vector3(width, height + headroom, length);

            // Kinematic, because ObstacleInstance.TickMotion writes the transform every tick for a moving
            // archetype. A moving *static* collider costs a broadphase re-insertion on every step; kinematic
            // is the cheap pairing and still reports triggers against the player's kinematic body.
            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            root.AddComponent<ObstacleInstance>();
            BuildObstacleVisual(root.transform, spec, material, capMaterial, width, height, length);

            return AssetAuthoring.SavePrefab(root, path, overwrite);
        }

        /// <summary>
        /// Derives the placeholder shape from the clearance data instead of a per-archetype switch, so a new
        /// obstacle row gets geometry that matches what it asks the player to do. Collider-free throughout:
        /// greybox decoration must not fight the analytic contact model (§4.7).
        /// </summary>
        private static void BuildObstacleVisual(Transform root, ObstacleSpec spec, Material material,
            Material capMaterial, float width, float height, float length)
        {
            if (spec.launchHeightMultiplier > 0f)
            {
                BuildRampVisual(root, material, width, height, length);
                return;
            }

            if (spec.clearance.rollable && spec.clearance.underClearance > 0f)
            {
                // A gate: two full-height posts and a crossbar whose underside is the authored underClearance.
                // The gap is visible, but the trigger is a solid box on purpose — a rolling rig overlaps it and
                // is classified Feet by the analytic model, which is what makes the roll work at any speed.
                float opening = Mathf.Clamp(spec.clearance.underClearance, 0.05f, height - 0.05f);
                float post = Mathf.Min(0.25f, width * 0.15f);
                float postX = width * 0.5f - post * 0.5f;

                AssetAuthoring.Visual(root, "gb_post_l", PrimitiveType.Cube,
                    new Vector3(-postX, height * 0.5f, 0f), new Vector3(post, height, length), material);
                AssetAuthoring.Visual(root, "gb_post_r", PrimitiveType.Cube,
                    new Vector3(postX, height * 0.5f, 0f), new Vector3(post, height, length), material);

                float barHeight = height - opening;
                AssetAuthoring.Visual(root, "gb_crossbar", PrimitiveType.Cube,
                    new Vector3(0f, opening + barHeight * 0.5f, 0f),
                    new Vector3(width, barHeight, length), material);
                return;
            }

            // Anything else is a body. Mountable bodies get an accent roof cap so the surface the player can
            // land on is legible from the chase camera, which sits below the roofline.
            AssetAuthoring.Visual(root, "gb_body", PrimitiveType.Cube,
                new Vector3(0f, height * 0.5f, 0f), new Vector3(width, height, length), material);

            if (!spec.clearance.mountable) return;

            AssetAuthoring.Visual(root, "gb_roof_cap", PrimitiveType.Cube,
                new Vector3(0f, height + 0.04f, 0f),
                new Vector3(width * 0.98f, 0.08f, length * 0.98f), capMaterial);

            if (length >= LongBodyMetres)
            {
                // A cab at the leading end, so a train reads as a vehicle with a direction rather than a wall.
                AssetAuthoring.Visual(root, "gb_cab", PrimitiveType.Cube,
                    new Vector3(0f, height + 0.45f, length * 0.5f - 0.9f),
                    new Vector3(width * 0.8f, 0.9f, 1.8f), material);
            }
        }

        /// <summary>
        /// A wedge built from a rotated slab: 12 triangles, no mesh asset, and the slope is exactly
        /// atan(height / length) so the visual crest matches the authored footprint the resolver reads.
        /// </summary>
        private static void BuildRampVisual(Transform root, Material material, float width, float height, float length)
        {
            float slope = Mathf.Sqrt(length * length + height * height);
            float angle = Mathf.Atan2(height, length) * Mathf.Rad2Deg;

            GameObject plank = AssetAuthoring.Visual(root, "gb_plank", PrimitiveType.Cube,
                new Vector3(0f, height * 0.5f, 0f), new Vector3(width, 0.2f, slope), material);
            // Negative X rotation lifts the +Z end, which is the direction of travel: the player runs up it.
            plank.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f);

            AssetAuthoring.Visual(root, "gb_ramp_back", PrimitiveType.Cube,
                new Vector3(0f, height * 0.5f, length * 0.5f - length * 0.125f),
                new Vector3(width, height, length * 0.25f), material);
        }

        // ── Chunks ──────────────────────────────────────────────────────────────────────────────────────

        private static GameObject BuildChunk(ChunkSpec spec, Palette palette, BalanceConfig balance, bool overwrite)
        {
            string path = ChunkPrefabPath(spec.poolKey);
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !overwrite) return existing;

            var root = new GameObject(spec.poolKey);
            root.layer = SkyriftLayers.Track;
            root.AddComponent<ChunkRoot>();

            // The generator positions the root at the chunk's start edge, so geometry runs from z = 0 to
            // z = lengthMetres and is centred on half the length. Obstacle slots use the same origin.
            float length = spec.length;
            float trackWidth = balance.laneCount * balance.laneWidth;
            float groundWidth = trackWidth + 2f * GreyboxContent.ShoulderMetres;
            float halfLength = length * 0.5f;

            // Top face at y = 0: that is the plane PlayerMotor measures feet from, so the slab hangs below it.
            // No collider — the ground is analytic, and a track collider would add broadphase work for nothing.
            AssetAuthoring.Visual(root.transform, "gb_ground", PrimitiveType.Cube,
                new Vector3(0f, -GreyboxContent.GroundThickness * 0.5f, halfLength),
                new Vector3(groundWidth, GreyboxContent.GroundThickness, length), palette.ground);

            if (spec.dressed)
            {
                float railX = trackWidth * 0.5f + GreyboxContent.RailThickness * 0.5f;
                AssetAuthoring.Visual(root.transform, "gb_rail_l", PrimitiveType.Cube,
                    new Vector3(-railX, GreyboxContent.RailHeight * 0.5f, halfLength),
                    new Vector3(GreyboxContent.RailThickness, GreyboxContent.RailHeight, length), palette.rail);
                AssetAuthoring.Visual(root.transform, "gb_rail_r", PrimitiveType.Cube,
                    new Vector3(railX, GreyboxContent.RailHeight * 0.5f, halfLength),
                    new Vector3(GreyboxContent.RailThickness, GreyboxContent.RailHeight, length), palette.rail);

                // One divider per lane boundary, derived from laneCount so a 4-lane world needs no new art.
                for (int i = 0; i < balance.laneCount - 1; i++)
                {
                    float x = balance.LaneX(i) + balance.laneWidth * 0.5f;
                    AssetAuthoring.Visual(root.transform, "gb_lane_" + i, PrimitiveType.Cube,
                        new Vector3(x, 0.01f, halfLength),
                        new Vector3(GreyboxContent.LaneStripeWidth, 0.02f, length), palette.stripe);
                }
            }

            return AssetAuthoring.SavePrefab(root, path, overwrite);
        }

        // ── Player ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The player rig. The root carries the trigger capsule, the kinematic body and both gameplay
        /// components; the visual is a child so squashing it for a roll never touches the collider.
        /// </summary>
        private static GameObject BuildPlayer(BalanceConfig balance, Palette palette, bool overwrite)
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (existing != null && !overwrite) return existing;

            var root = new GameObject(GreyboxContent.PlayerPrefabName);
            root.layer = SkyriftLayers.Player;

            Rigidbody body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            // Interpolation must stay None: RunCameraRig reads the rig in LateUpdate, and an interpolated
            // body would hand it a pose lagging the simulation by up to one fixed step (§3.5, §4.4).
            body.interpolation = RigidbodyInterpolation.None;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            // Stand height even while rolling. Classification is analytic — CurrentHeight decides the outcome
            // — so the taller overlap during a roll is correctly read as Feet under a gate, not as a hit.
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.isTrigger = true;
            capsule.direction = 1;
            capsule.height = balance.playerStandHeight;
            capsule.radius = balance.playerRadius;
            capsule.center = new Vector3(0f, balance.playerStandHeight * 0.5f, 0f);

            root.AddComponent<PlayerMotor>();
            root.AddComponent<PlayerContactSensor>();
            GreyboxPlayerVisual visual = root.AddComponent<GreyboxPlayerVisual>();

            // Unity's capsule primitive is 2 m tall with a 0.5 m radius at scale 1, so the scale that produces
            // the authored volume is (2r, h/2, 2r) — not a uniform scale, which only happens to work at 1.8/0.45.
            GameObject child = AssetAuthoring.Visual(root.transform, "gb_body", PrimitiveType.Capsule,
                new Vector3(0f, balance.playerStandHeight * 0.5f, 0f),
                new Vector3(balance.playerRadius * 2f, balance.playerStandHeight * 0.5f, balance.playerRadius * 2f),
                palette.player);

            var serialized = new SerializedObject(visual);
            serialized.FindProperty("visual").objectReferenceValue = child.transform;
            serialized.FindProperty("standHeight").floatValue = balance.playerStandHeight;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return AssetAuthoring.SavePrefab(root, PlayerPrefabPath, overwrite);
        }

        // ── Chaser ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The pursuer and its hound — original placeholder silhouettes, visuals only. No collider and no
        /// Rigidbody: proximity is presentation and tension, and death comes from the collision model, so the
        /// chaser must never be able to generate a contact (§4.8). <c>ChaserSystem</c> lives on ChaserRoot in
        /// the scene and drives this rig, not the other way round.
        /// </summary>
        private static GameObject BuildChaser(BalanceConfig balance, Palette palette, bool overwrite)
        {
            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(ChaserPrefabPath);
            if (existing != null && !overwrite) return existing;

            var root = new GameObject(GreyboxContent.ChaserPrefabName);
            root.layer = SkyriftLayers.Chaser;

            // A head taller than the player, so the pursuit reads as pressure even at the far distance.
            float wardenHeight = balance.playerStandHeight + 0.2f;
            AssetAuthoring.Visual(root.transform, "gb_warden", PrimitiveType.Capsule,
                new Vector3(0f, wardenHeight * 0.5f, 0f),
                new Vector3(balance.playerRadius * 2.2f, wardenHeight * 0.5f, balance.playerRadius * 2.2f),
                palette.chaser);

            AssetAuthoring.Visual(root.transform, "gb_hound", PrimitiveType.Cube,
                new Vector3(balance.laneWidth * 0.3f, 0.3f, 0.4f),
                new Vector3(0.5f, 0.6f, 1.1f), palette.hound);

            return AssetAuthoring.SavePrefab(root, ChaserPrefabPath, overwrite);
        }
    }
}
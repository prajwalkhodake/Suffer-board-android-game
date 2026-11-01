using System;
using System.Collections.Generic;
using System.Text;
using Skyrift.Audio;
using Skyrift.Core.Pooling;
using Skyrift.Data;
using Skyrift.Gameplay;
using Skyrift.Track;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;

namespace Skyrift.Editor
{
    /// <summary>
    /// The authoring gate for the M1 greybox (TECH_DOC §5.14, §12.2). Everything here is a rule that a
    /// <i>runtime</i> defect would otherwise pay for: an unreachable graze band, a prewarm below the
    /// worst-case concurrent count, a rooftop placed where no jump can land.
    ///
    /// <b>It reuses the runtime rules rather than duplicating them.</b> <see cref="GameDatabase"/> and its
    /// <see cref="GameDatabase.ValidateCrossReferences"/> are run first, so id, uniqueness, pool and tag
    /// references are checked by the same code the shipped game runs. What this file adds is the part a
    /// runtime validator cannot see, because it needs the Editor: prefab↔data agreement, level-design
    /// geometry, and pool sizing against the streaming window.
    ///
    /// <b>Why the geometric rules exist at all.</b> The collision model is analytic (§4.7): a trigger volume
    /// decides <i>whether</i> a contact happens and the resolver decides the outcome from the data. So a
    /// prefab whose box disagrees with its <see cref="ObstacleDefinition.footprintMetres"/> is not a cosmetic
    /// problem — it changes which contacts are even possible, in a way no playtest can localise. Every check
    /// below that compares two representations therefore names the field on both sides.
    ///
    /// <b>Numbers are derived, never typed.</b> The speed range comes from <see cref="BalanceConfig"/>'s
    /// curve, the launch corridor from its gravity and take-off velocity, and the pool bounds from
    /// <see cref="TrackGenerationConfig"/>'s streaming window. Change a jump height and the validator's
    /// verdict on the level design changes with it, which is the point.
    ///
    /// Not run automatically on a script reload: validation loads every prefab and the asset database is
    /// already busy during a reload, so a <c>[DidReloadScripts]</c> hook would either throw or duplicate
    /// output on every compile. It runs at the end of setup, as its own menu item, and as a CI step.
    /// </summary>
    public static class CatalogValidator
    {
        private const string Type = nameof(CatalogValidator);

        /// <summary>
        /// §11.1's cap on active trigger colliders during gameplay. The bound this is compared against is
        /// the worst-case obstacle count in the live window plus the player's own capsule, which is why a
        /// chunk prefab must carry none (see <see cref="CheckChunkPrefabs"/>).
        /// </summary>
        private const int ActiveColliderBudget = 40;

        /// <summary>Prefab geometry is authored to whole centimetres; anything finer is a drift, not a rounding error.</summary>
        private const float GeometryTolerance = 0.02f;

        /// <summary>
        /// How much of a lane the side-scrape band must occupy for a graze to be reachable reliably. Below
        /// this the band is thinner than the lateral distance a lane change covers in one frame near the
        /// threshold, so the two-hit stumble model of §4.7 becomes unreachable in practice.
        /// </summary>
        private const float MinGrazeBandLanes = 0.05f;

        [MenuItem("Tools/Skyrift/Validate Catalog")]
        public static void ValidateFromMenu()
        {
            Run();
        }

        /// <summary>
        /// CI entry point (<c>-executeMethod Skyrift.Editor.CatalogValidator.ValidateForCI</c>). Throwing
        /// rather than returning false is deliberate: Unity's batch mode exits non-zero on an unhandled
        /// exception, and the log line above it names the rule, so the build log is the failure report (§12.2).
        /// </summary>
        public static void ValidateForCI()
        {
            if (!Run())
                throw new InvalidOperationException("Skyrift catalog validation failed. See the ERROR lines above.");
        }

        /// <summary>Runs every rule group. Returns whether the catalog is clean enough to build.</summary>
        public static bool Run()
        {
            var report = new ValidationReport();

            CatalogManifest manifest = Load<CatalogManifest>(GreyboxData.ManifestPath);
            if (manifest == null)
            {
                report.Error(Type, "-", "manifest",
                    "No CatalogManifest at " + GreyboxData.ManifestPath + ". Run Tools > Skyrift > Build M1 Greybox.");
                Print(report, null, null);
                return false;
            }

            // Runtime rules first: they are the contract the boot path enforces too, and running them here
            // means an asset a CI build rejects is one the game would also have dropped at boot.
            var database = new GameDatabase(manifest, report);
            database.ValidateCrossReferences(report);

            ObstacleBehaviourCatalog behaviours = Load<ObstacleBehaviourCatalog>(GreyboxData.BehaviourCatalogPath);
            if (behaviours == null)
                report.Error(Type, "-", "behaviours", "No ObstacleBehaviourCatalog at " + GreyboxData.BehaviourCatalogPath + ".");
            else
                behaviours.Validate(report);

            AudioCatalog audioCatalog = Load<AudioCatalog>(AudioAssetBuilder.CatalogPath);
            if (audioCatalog == null)
            {
                report.Error(Type, "-", "audio_catalog", "No AudioCatalog at " + AudioAssetBuilder.CatalogPath + ".");
            }
            else
            {
                audioCatalog.Validate(report);
                AudioMixer mixer = Load<AudioMixer>(AudioAssetBuilder.MixerPath);
                AudioAssetBuilder.Validate(mixer, audioCatalog, manifest.audio, report);
            }

            InputAssetBuilder.Validate(Load<InputActionAsset>(InputAssetBuilder.AssetPath), report);

            BalanceConfig balance = manifest.balance;
            string budget = null;
            if (balance != null)
            {
                CheckObstaclePrefabs(database, behaviours, balance, report);
                CheckChunkPrefabs(database, report);
                CheckLevelDesign(database, balance, report);
                budget = CheckPoolSizing(database, report);
            }
            else
            {
                // Every remaining rule reads lane geometry, jump physics or the streaming window. Without a
                // balance config they would all report nonsense, so they are skipped rather than guessed at.
                report.Error(Type, "-", "balance", "Manifest has no BalanceConfig; prefab and level rules were skipped.");
            }

            Print(report, database, budget);
            return report.IsValid;
        }

        // ── Prefab ↔ data ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Proves each obstacle prefab is the geometry its definition claims. These are the four properties
        /// the collision model depends on and no runtime check can make: one trigger on the root, the right
        /// size, headroom above a mountable roof, and decoration that cannot generate a contact.
        /// </summary>
        private static void CheckObstaclePrefabs(IGameDatabase database, ObstacleBehaviourCatalog behaviours,
            BalanceConfig balance, ValidationReport report)
        {
            IReadOnlyList<ObstacleDefinition> obstacles = database.Obstacles.All;

            for (int i = 0; i < obstacles.Count; i++)
            {
                ObstacleDefinition def = obstacles[i];
                string id = def.Id.Value;
                const string t = nameof(ObstacleDefinition);

                if (behaviours != null && !behaviours.TryGet(def.behaviourId, out IObstacleBehaviour _))
                {
                    report.Error(t, id, "behaviour_ref",
                        $"behaviourId '{def.behaviourId}' is not in the behaviour catalog, so the populator skips every instance of this obstacle.");
                }

                GameObject prefab = ResolvePrefab(database, def.poolKey);
                if (prefab == null) continue;   // pool_ref / prefab rules already reported by PoolCatalog validation

                CheckAssetPath(def.prefabAddress, prefab, t, id, report);

                if (prefab.GetComponent<ObstacleInstance>() == null)
                    report.Error(t, id, "instance", "Root has no ObstacleInstance, so the pool would refuse it at spawn.");

                if (prefab.layer != SkyriftLayers.Obstacle)
                    report.Error(t, id, "layer",
                        $"Layer {prefab.layer} is not Obstacle ({SkyriftLayers.Obstacle}); the collision matrix only sanctions Player↔Obstacle.");

                Rigidbody body = prefab.GetComponent<Rigidbody>();
                if (body == null)
                    report.Error(t, id, "rigidbody",
                        "No Rigidbody on the root. A trigger needs a body on at least one side of the pair, and a moving collider without a kinematic body re-inserts into the broadphase every step.");
                else if (!body.isKinematic)
                    report.Error(t, id, "rigidbody", "The obstacle Rigidbody must be kinematic: the motor owns the transform and a dynamic body would fight it.");

                Collider[] all = prefab.GetComponentsInChildren<Collider>(true);
                if (all.Length > 1)
                    report.Error(t, id, "colliders",
                            $"Found {all.Length} colliders in the prefab. ObstacleInstance caches GetComponent<Collider>() on the root and the contact index keys on that one instance id, so a second collider is invisible to the resolver but still generates contacts.");
                else if (all.Length == 1 && all[0].transform != prefab.transform)
                    report.Error(t, id, "collider_root",
                        "The trigger is not on the prefab root, so ObstacleInstance.Awake's GetComponent<Collider>() returns null and this obstacle is intangible.");

                BoxCollider box = prefab.GetComponent<BoxCollider>();
                if (box == null)
                {
                    report.Error(t, id, "trigger_shape", "The root collider must be a BoxCollider; the clearance model reads a box.");
                    continue;
                }

                if (!box.isTrigger)
                    report.Error(t, id, "trigger", "The obstacle collider must be a trigger — contacts are classified analytically, never resolved physically.");

                CheckTriggerBox(def, balance, box, t, id, report);
                CheckGrazeBand(def, balance, box, t, id, report);
            }
        }

        private static void CheckTriggerBox(ObstacleDefinition def, BalanceConfig balance, BoxCollider box,
            string t, string id, ValidationReport report)
        {
            float height = def.footprintMetres.y;
            float length = def.footprintMetres.x;

            // Headroom is expected, not a mismatch: support is withdrawn on trigger exit, so a box whose top is
            // flush with the roof would drop the player through it the moment they jumped on the spot (§4.7).
            float headroom = def.clearance.mountable
                ? balance.jumpHeight + balance.jumpModifierBonus + balance.playerStandHeight
                : 0f;
            float boxHeight = height + headroom;

            int claimed = LaneBits(def.clearance.laneOccupancyMask);
            if (box.size.x > balance.laneWidth * claimed + GeometryTolerance)
            {
                report.Error(t, id, "trigger_width",
                    $"Collider is {box.size.x:F2} m wide but clearance.laneOccupancyMask claims {claimed} lane(s) " +
                    $"({balance.laneWidth * claimed:F2} m). A body that straddles a lane it does not claim blocks a " +
                    "lane the generator believes is open, so the run can be unpassable by accident.");
            }

            if (Mathf.Abs(box.size.z - length) > GeometryTolerance)
                report.Error(t, id, "trigger_length",
                    $"Collider depth is {box.size.z:F2} m but footprintMetres.x is {length:F2} m — the populator centres the volume on the slot, so the player is judged against one of the two.");

            if (Mathf.Abs(box.size.y - boxHeight) > GeometryTolerance)
            {
                report.Error(t, id, "trigger_height",
                    $"Collider height is {box.size.y:F2} m, expected {boxHeight:F2} m " +
                    (def.clearance.mountable
                        ? $"(footprint {height:F2} m + {(boxHeight - height):F2} m of mount headroom). Without the headroom a player standing on the roof leaves the volume, loses support and falls through it."
                        : "(footprintMetres.y, no headroom for a non-mountable body)."));
            }

            if (Mathf.Abs(box.center.y - box.size.y * 0.5f) > GeometryTolerance)
                report.Error(t, id, "trigger_ground",
                    $"Collider centre is at y {box.center.y:F2} but its half height is {box.size.y * 0.5f:F2} m. The populator places the root on the ground plane, so the box must sit on it, not float or sink.");

            if (Mathf.Abs(box.center.x) > GeometryTolerance || Mathf.Abs(box.center.z) > GeometryTolerance)
                report.Error(t, id, "trigger_center",
                    $"Collider centre is offset ({box.center.x:F2}, {box.center.z:F2}) from the root. Every placement rule measures a body as slot.z ± footprint/2, so an offset silently moves that boundary.");
        }

        /// <summary>
        /// The lateral band that turns a hit into a scrape must exist, or the first stumble never warns and
        /// the second one never fires: a side contact is the only route to the chaser-closing loop (§4.7, §4.8).
        /// </summary>
        private static void CheckGrazeBand(ObstacleDefinition def, BalanceConfig balance, BoxCollider box,
            string t, string id, ValidationReport report)
        {
            // Overlap starts when |dx| < halfWidth + playerRadius, expressed in lanes for the threshold compare.
            float overlapStartLanes = (box.size.x * 0.5f + balance.playerRadius) / balance.laneWidth;
            float band = overlapStartLanes - balance.grazeLateralThreshold;

            if (band <= 0f)
                report.Error(t, id, "graze_unreachable",
                    $"A contact with this obstacle can never be a side scrape: overlap starts at {overlapStartLanes:F3} lanes, which is not beyond grazeLateralThreshold ({balance.grazeLateralThreshold:F2}). Every hit is head-on.");
            else if (band < MinGrazeBandLanes)
                report.Warn(t, id, "graze_band_thin",
                    $"The graze band is only {band * balance.laneWidth:F1} cm wide ({band:F3} lanes); a lane change crosses it in well under one frame, so scrapes become rare.");
        }

        /// <summary>
        /// A chunk prefab is ground truth for recycling and for the collider budget, so it must be pure
        /// presentation: no collider anywhere, because the §11.1 budget counts obstacles plus one capsule and
        /// a decorative box inside a chunk would break that arithmetic without breaking a leg.
        /// </summary>
        private static void CheckChunkPrefabs(IGameDatabase database, ValidationReport report)
        {
            IReadOnlyList<ChunkDefinition> chunks = database.Chunks.All;

            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkDefinition def = chunks[i];
                string id = def.Id.Value;
                const string t = nameof(ChunkDefinition);

                GameObject prefab = ResolvePrefab(database, def.poolKey);
                if (prefab == null) continue;

                CheckAssetPath(def.prefabAddress, prefab, t, id, report);

                if (prefab.GetComponent<ChunkRoot>() == null)
                    report.Error(t, id, "root", "Root has no ChunkRoot, so the streamer cannot bind or recycle this chunk.");

                if (prefab.layer != SkyriftLayers.Track)
                    report.Error(t, id, "layer",
                        $"Layer {prefab.layer} is not Track ({SkyriftLayers.Track}).");

                Collider[] colliders = prefab.GetComponentsInChildren<Collider>(true);
                if (colliders.Length > 0)
                    report.Error(t, id, "colliders",
                        $"A chunk prefab must carry no colliders; found {colliders.Length}. Each live chunk would add unbudgeted bodies to the broadphase and invalidate §11.1's active-collider count.");

                Rigidbody[] bodies = prefab.GetComponentsInChildren<Rigidbody>(true);
                if (bodies.Length > 0)
                    report.Error(t, id, "rigidbodies",
                        $"A chunk prefab must carry no Rigidbodies; found {bodies.Length}. Chunks are moved by the streamer and re-based by the origin shifter, which a physics body would lag by a step.");
            }
        }

        // ── Level design ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One placement, resolved to the definition that would actually spawn there. The populator picks
        /// among tag candidates by weight, so a slot is only safe when <i>every</i> candidate is — that is why
        /// the extents below take the widest candidate rather than the first.
        /// </summary>
        private struct Placement
        {
            public int lane;
            public float z;
            public float halfLength;
            public ObstacleDefinition def;
        }

        private sealed class Group
        {
            public float z;
            public int laneMask;
            public float frontEdge;
            public float backEdge;
            public readonly List<Placement> placements = new List<Placement>(4);
        }

        /// <summary>
        /// Traversability, from the assets rather than from the content table's comments. Three rules, each of
        /// which is a distance the player's body can or cannot cover: a lane change, a jump, and a launch.
        ///
        /// <b>What is deliberately not modelled.</b> Every chunk is grouped from its slots as if all of them
        /// always spawn, including <c>required = false</c> slots whose <c>spawnChance</c> can roll them away.
        /// That over-estimates occupancy, which is the safe direction for all three rules: a group that is only
        /// sealed when every roll succeeds is still a seal the generator can produce, while an under-estimate
        /// would bless a spacing that only holds on the lucky rolls. The one thing that would make these rules
        /// wrong is a chunk whose <c>entryLaneMask</c>/<c>exitLaneMask</c> contradicts its own placements — the
        /// masks gate chaining in <c>ChunkSelectionRules</c>, and the greybox authors them all-open, so the gate
        /// is currently inert. Narrowing a mask is an authoring change that needs this rule extended to match.
        /// </summary>
        private static void CheckLevelDesign(IGameDatabase database, BalanceConfig balance, ValidationReport report)
        {
            const string t = nameof(ChunkDefinition);

            SpeedRange(balance, out float vMin, out float vMax);

            // The window a run needs between two decisions: a full lane sweep plus a jump's air time, walked at
            // the slowest speed the run ever reaches. Measured between placement centres, because both the
            // body length and a train's sweep are visible well before the decision point matters.
            float reactionWindow = vMin * (balance.laneChangeDuration * (balance.laneCount - 1) + balance.jumpAirTime);

            int openMask = 0;
            for (int lane = 0; lane < balance.laneCount; lane++) openMask |= 1 << lane;

            // Tag → candidates, the same index the populator builds, so a slot is checked against what can
            // really spawn there instead of against a guess.
            var byTag = BuildTagIndex(database);

            float bestLowBand = float.MaxValue;
            float bestHighBand = float.MinValue;

            IReadOnlyList<ChunkDefinition> chunks = database.Chunks.All;
            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkDefinition def = chunks[i];
                string id = def.Id.Value;

                if (def.difficultyBand.x < bestLowBand) bestLowBand = def.difficultyBand.x;
                if (def.difficultyBand.y > bestHighBand) bestHighBand = def.difficultyBand.y;

                // Checked on the slot rather than the resolved group: the populator clamps an out-of-range
                // lane silently, so the obstacle spawns somewhere other than where it was authored and the
                // group's occupancy mask no longer describes the placement at all.
                ObstacleSlotData[] authored = def.obstacleSlots;
                if (authored != null)
                {
                    for (int s = 0; s < authored.Length; s++)
                    {
                        ObstacleSlotData slot = authored[s];
                        if (slot == null) continue;
                        if (slot.lane < 0 || slot.lane >= balance.laneCount)
                            report.Error(t, id, "slot_lane",
                                $"obstacleSlots[{s}].lane is {slot.lane}, outside 0..{balance.laneCount - 1}. The populator clamps it, so this placement does not sit in the lane it was authored in.");
                    }
                }

                List<Group> groups = BuildGroups(def, byTag, report);
                if (groups.Count == 0) continue;

                for (int g = 0; g < groups.Count; g++)
                {
                    Group group = groups[g];

                    if ((group.laneMask & ~openMask) != 0)
                        report.Error(t, id, "mask_lane",
                            $"A placement at z {group.z:F1} occupies a lane beyond laneCount ({balance.laneCount}). The populator clamps it, so the authored intent and the spawn disagree.");

                    if (group.frontEdge < -GeometryTolerance || group.backEdge > def.lengthMetres + GeometryTolerance)
                    {
                        report.Error(t, id, "slot_extent",
                            $"A placement at z {group.z:F1} reaches {group.frontEdge:F1}..{group.backEdge:F1} m, outside the chunk's {def.lengthMetres:F0} m. Its far half would sit in the neighbouring chunk, where neither the populator nor the recycler tracks it.");
                    }

                    bool sealsAllLanes = (group.laneMask & openMask) == openMask;
                    if (sealsAllLanes)
                    {
                        // Every lane is covered. That is legal only when each occupant has a vertical answer:
                        // a full blocker spawning in the last open lane is an unpassable wall, and the
                        // generator has no way to know it picked one until the player crashes into it.
                        bool escapable = true;
                        for (int p = 0; p < group.placements.Count; p++)
                        {
                            ObstacleDefinition od = group.placements[p].def;
                            ClearanceProfile c = od.clearance;
                            if (c.jumpable || c.rollable || c.mountable) continue;
                            escapable = false;
                            report.Error(t, id, "group_sealed",
                                $"Placements at z {group.z:F1} fill every lane and '{od.Id.Value}' offers no jump/roll/mount answer, so this chunk can generate a wall nothing clears.");
                        }

                        if (escapable)
                            report.Warn(t, id, "group_all_lanes",
                                $"Placements at z {group.z:F1} fill all {balance.laneCount} lanes: the only answer is vertical, which is a harsher read than a lane change.");
                    }

                    if (g + 1 < groups.Count)
                    {
                        Group next = groups[g + 1];
                        float gap = next.z - group.z;

                        bool launchPair = HasLaunch(group.placements) && HasMountable(next.placements);
                        if (!launchPair && gap < reactionWindow - GeometryTolerance)
                        {
                            report.Error(t, id, "group_spacing",
                                $"Groups at z {group.z:F1} and {next.z:F1} are {gap:F1} m apart, under the {reactionWindow:F1} m a full lane sweep plus a jump needs at {vMin:F0} m/s. The second group can be entered before the first is answered.");
                        }

                        if (launchPair) CheckLaunchCorridor(def, group, next, balance, vMin, vMax, report);
                    }
                }

                // The boundary case the generator cannot see: this chunk's first group arrives straight after
                // its own last group, because the fallback and the hardest chunk can be neighbours.
                Group head = groups[0];
                Group tail = groups[groups.Count - 1];
                if (groups.Count > 1)
                {
                    float across = head.frontEdge + (def.lengthMetres - tail.backEdge);
                    if (across < reactionWindow - GeometryTolerance)
                    {
                        report.Warn(t, id, "boundary_spacing",
                            $"Only {across:F1} m separates this chunk's last group from its first when it repeats back-to-back, under the {reactionWindow:F1} m reaction window. Legal, but the pair reads as one decision.");
                    }
                }
            }

            if (chunks.Count > 0)
            {
                if (bestLowBand > 0f)
                    report.Warn(t, "-", "band_coverage",
                        $"No chunk is eligible at difficulty 0 (lowest band start {bestLowBand:F2}); the run's opening falls back to the nearest band.");
                if (bestHighBand < 1f)
                    report.Warn(t, "-", "band_coverage",
                        $"No chunk is eligible at difficulty 1 (highest band end {bestHighBand:F2}); late runs repeat the same set forever.");
            }
        }

        /// <summary>
        /// Whether a launch can actually reach the mountable body after it. Both bounds are the same
        /// closed-form arc the motor integrates: feet above the roof between tEnter and tExit, so the near
        /// edge has to fall inside that window at <i>every</i> speed the run can hold — which is what makes a
        /// rooftop landing frame-rate and speed independent rather than a coin flip (§4.4, §4.7).
        /// </summary>
        private static void CheckLaunchCorridor(ChunkDefinition def, Group launch, Group target,
            BalanceConfig balance, float vMin, float vMax, ValidationReport report)
        {
            const string t = nameof(ChunkDefinition);
            string id = def.Id.Value;

            float gravity = balance.Gravity;
            float takeOff = balance.JumpVelocity;
            if (gravity <= 0f || takeOff <= 0f) return;   // BalanceConfig.Validate already reported a zero air time.

            for (int p = 0; p < launch.placements.Count; p++)
            {
                Placement slot = launch.placements[p];
                if (slot.def.launchHeightMultiplier <= 0f) continue;

                float launchVelocity = slot.def.launchHeightMultiplier * takeOff;
                float crest = slot.z + slot.halfLength;

                for (int q = 0; q < target.placements.Count; q++)
                {
                    Placement roof = target.placements[q];
                    if (!roof.def.clearance.mountable) continue;

                    float roofHeight = roof.def.footprintMetres.y;
                    float discriminant = launchVelocity * launchVelocity - 2f * gravity * roofHeight;
                    if (discriminant <= 0f)
                    {
                        report.Error(t, id, "launch_apex",
                            $"'{slot.def.Id.Value}' launches at {launchVelocity:F1} m/s, whose apex is {launchVelocity * launchVelocity / (2f * gravity):F2} m — below '{roof.def.Id.Value}'s {roofHeight:F2} m roof. The gap can never be crossed.");
                        continue;
                    }

                    float root = Mathf.Sqrt(discriminant);
                    float tEnter = (launchVelocity - root) / gravity;
                    float tExit = (launchVelocity + root) / gravity;

                    float nearEdge = roof.z - roof.halfLength;
                    float farEdge = roof.z + roof.halfLength;
                    float gap = nearEdge - crest;

                    // Fastest case: the player reaches the volume before their feet are above the roof, and the
                    // analytic model reads the overlap as a side contact — an instant crash into the body they
                    // were launched at.
                    if (gap < vMax * tEnter - GeometryTolerance)
                    {
                        report.Error(t, id, "launch_still_rising",
                            $"Only {gap:F1} m between the launch's crest ({crest:F1}) and the roof's near edge ({nearEdge:F1}): at {vMax:F0} m/s the player is still below {roofHeight:F1} m when they enter the volume, so the model reads a side hit.");
                    }

                    // Slowest case: they touch down before the roof starts, run along the ground, and then hit
                    // that same side. This is the defect the rule was written for.
                    if (gap > vMin * tExit + GeometryTolerance)
                    {
                        report.Error(t, id, "launch_lands_short",
                            $"The roof's near edge is {gap:F1} m out, but at {vMin:F0} m/s the arc lands after {vMin * tExit:F1} m. The player drops onto the track and then into the roof's side.");
                    }

                    // Flying clean over the whole roof is survivable but pointless, so it is a warning about
                    // intent rather than a defect: the length is the answer, not the placement.
                    if (crest + vMax * tExit > farEdge + GeometryTolerance)
                    {
                        report.Warn(t, id, "launch_overruns",
                            $"At {vMax:F0} m/s the arc lands {crest + vMax * tExit - farEdge:F1} m past '{roof.def.Id.Value}'s far edge ({farEdge:F1} m), so the fastest line never touches the roof.");
                    }
                }
            }
        }

        private static List<Group> BuildGroups(ChunkDefinition def, Dictionary<string, List<ObstacleDefinition>> byTag,
            ValidationReport report)
        {
            var groups = new List<Group>(8);
            ObstacleSlotData[] slots = def.obstacleSlots;
            if (slots == null) return groups;

            for (int i = 0; i < slots.Length; i++)
            {
                ObstacleSlotData slot = slots[i];
                if (slot == null || string.IsNullOrEmpty(slot.obstacleTag)) continue;

                if (!byTag.TryGetValue(slot.obstacleTag, out List<ObstacleDefinition> candidates) || candidates.Count == 0)
                    continue;   // obstacle_ref already reported by the cross-reference pass.

                Group group = null;
                for (int g = 0; g < groups.Count; g++)
                {
                    // Two slots in the same group are answered by one decision, so they share a z. Compared to
                    // a tolerance because these are authored floats, and 1 cm of difference is not a new row.
                    if (Mathf.Abs(groups[g].z - slot.zOffset) <= 0.01f) { group = groups[g]; break; }
                }

                if (group == null)
                {
                    group = new Group { z = slot.zOffset, frontEdge = float.MaxValue, backEdge = float.MinValue };
                    groups.Add(group);
                }

                for (int c = 0; c < candidates.Count; c++)
                {
                    ObstacleDefinition od = candidates[c];
                    float half = od.footprintMetres.x * 0.5f;

                    // A longitudinally moving body's reach is its swept range, which is where the next chunk
                    // would have to start early to avoid it.
                    if (od.isMoving && od.motion.axis == ObstacleMotion.MotionAxis.Longitudinal)
                        half += od.motion.amplitude;

                    // clearance.laneOccupancyMask counts the lanes the <i>body</i> covers, from its own centre
                    // lane; the track lanes a group blocks depend on where the slot puts it. Shifting is exact
                    // for every M1 obstacle because they are all one lane wide — a body wider than a lane is
                    // centred on its slot, so its true coverage needs a convention of its own before a chunk
                    // can rely on this rule.
                    group.laneMask |= ShiftLanes(od.clearance.laneOccupancyMask, slot.lane);
                    group.frontEdge = Mathf.Min(group.frontEdge, slot.zOffset - half);
                    group.backEdge = Mathf.Max(group.backEdge, slot.zOffset + half);

                    group.placements.Add(new Placement
                    {
                        lane = slot.lane,
                        z = slot.zOffset,
                        halfLength = half,
                        def = od
                    });
                }
            }

            groups.Sort((a, b) => a.z.CompareTo(b.z));
            return groups;
        }

        private static bool HasLaunch(List<Placement> placements)
        {
            for (int i = 0; i < placements.Count; i++)
                if (placements[i].def.launchHeightMultiplier > 0f) return true;
            return false;
        }

        private static bool HasMountable(List<Placement> placements)
        {
            for (int i = 0; i < placements.Count; i++)
                if (placements[i].def.clearance.mountable) return true;
            return false;
        }

        /// <summary>The mask's lanes, moved to the absolute lanes a slot at <paramref name="lane"/> blocks.</summary>
        private static int ShiftLanes(int mask, int lane)
        {
            int shifted = 0;
            for (int i = 0; i < 16; i++)
                if ((mask & (1 << i)) != 0) shifted |= 1 << (i + lane);
            return shifted;
        }

        private static Dictionary<string, List<ObstacleDefinition>> BuildTagIndex(IGameDatabase database)
        {
            var byTag = new Dictionary<string, List<ObstacleDefinition>>(StringComparer.Ordinal);
            IReadOnlyList<ObstacleDefinition> all = database.Obstacles.All;

            for (int i = 0; i < all.Count; i++)
            {
                string[] tags = all[i].tagSet;
                if (tags == null) continue;

                for (int t = 0; t < tags.Length; t++)
                {
                    if (string.IsNullOrEmpty(tags[t])) continue;
                    if (!byTag.TryGetValue(tags[t], out List<ObstacleDefinition> list))
                    {
                        list = new List<ObstacleDefinition>(2);
                        byTag[tags[t]] = list;
                    }
                    list.Add(all[i]);
                }
            }

            return byTag;
        }

        // ── Pool sizing ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Prewarm against the worst case the streaming window can produce (§5.14 rule 10). A pool that runs
        /// dry mid-run does not allocate — it recycles the <b>eldest active</b> instance and logs an overflow,
        /// which for a chunk means a hole opening in the track under the player's feet. So the bound is an
        /// error, not a hint.
        ///
        /// The bound is deliberately an over-estimate: it applies each chunk type's own cooldown cap to a
        /// window it fills greedily by density, without checking that the interleaving is jointly satisfiable.
        /// Over-estimating costs a few idle instances; under-estimating costs a crash the test suite would not
        /// reproduce.
        /// </summary>
        private static string CheckPoolSizing(IGameDatabase database, ValidationReport report)
        {
            PoolCatalog pools = database.Pools;
            if (pools == null) return null;

            TrackGenerationConfig track = database.TrackGeneration;
            const string t = nameof(PoolCatalog);

            if (track == null)
            {
                report.Warn(t, pools.Id.Value, "window", "No TrackGenerationConfig; pool sizing was not checked against a streaming window.");
                return null;
            }

            int window = Mathf.Max(1, track.chunksAhead + track.chunksBehind + 1);

            // Per-type caps: a chunk with cooldown c cannot repeat inside its own cooldown, so at most
            // ceil(window / (c + 1)) of the live instances can be it.
            var chunkCaps = new List<TypeLoad>(database.Chunks.Count);
            IReadOnlyList<ChunkDefinition> chunks = database.Chunks.All;
            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkDefinition def = chunks[i];
                int cap = Mathf.CeilToInt(window / (float)(Mathf.Max(0, def.cooldownChunks) + 1));
                chunkCaps.Add(new TypeLoad
                {
                    label = def.Id.Value,
                    count = def.obstacleSlots != null ? def.obstacleSlots.Length : 0,
                    cap = cap
                });
            }

            int jointMax = GreedyBound(chunkCaps, window, out string jointAllocation);

            if (jointMax + 1 > ActiveColliderBudget)
            {
                report.Error(t, pools.Id.Value, "collider_budget",
                    $"{jointAllocation} puts {jointMax} obstacle triggers plus the player's capsule in the live window, over §11.1's {ActiveColliderBudget}. " +
                    "Either drop a placement, raise a chunk's cooldown, or widen the budget with a measured reason.");
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkDefinition def = chunks[i];
                PoolEntry entry = Find(pools, def.poolKey);
                if (entry == null) continue;

                int bound = Mathf.CeilToInt(window / (float)(Mathf.Max(0, def.cooldownChunks) + 1));
                if (entry.prewarm < bound)
                {
                    report.Error(t, entry.key, "chunk_prewarm",
                        $"Pool prewarms {entry.prewarm} but the {window}-chunk live window can hold {bound} instance(s) of '{def.Id.Value}' " +
                        $"(cooldown {def.cooldownChunks}). Exhaustion recycles the eldest active chunk, which deletes track the player can see.");
                }
            }

            IReadOnlyList<ObstacleDefinition> obstacles = database.Obstacles.All;
            for (int i = 0; i < obstacles.Count; i++)
            {
                ObstacleDefinition def = obstacles[i];
                PoolEntry entry = Find(pools, def.poolKey);
                if (entry == null) continue;

                int bound = ObstacleBound(def, chunks, chunkCaps, window, out string allocation);
                if (entry.prewarm < bound)
                {
                    report.Error(t, entry.key, "obstacle_prewarm",
                        $"Pool prewarms {entry.prewarm} but the worst case is {bound} concurrent instance(s): {allocation}. " +
                        "Exhaustion recycles an obstacle the player has not passed yet.");
                }
            }

            AudioConfig audio = database.Audio;
            if (audio != null)
            {
                PoolEntry voice = Find(pools, audio.voicePoolKey);
                if (voice != null)
                {
                    // AudioService treats exhaustion as "steal the eldest voice", which is the intended cap, so
                    // the two numbers must agree exactly — a larger pool is voices that can never be heard.
                    if (voice.prewarm < audio.maxConcurrentVoices)
                        report.Error(t, voice.key, "voice_prewarm",
                            $"'{audio.voicePoolKey}' prewarms {voice.prewarm} but AudioConfig allows {audio.maxConcurrentVoices} voices: the surplus would Instantiate mid-run.");
                    else if (voice.prewarm > audio.maxConcurrentVoices)
                        report.Warn(t, voice.key, "voice_prewarm",
                            $"'{audio.voicePoolKey}' prewarms {voice.prewarm} against a cap of {audio.maxConcurrentVoices}; the extra voices are unreachable.");
                }
            }

            return $"pool window {window} chunks · worst case {jointMax} obstacles + 1 player capsule against a budget of {ActiveColliderBudget} ({jointAllocation})";
        }

        /// <summary>How many instances of one obstacle pool the window can hold at once.</summary>
        private static int ObstacleBound(ObstacleDefinition target, IReadOnlyList<ChunkDefinition> chunks,
            List<TypeLoad> caps, int window, out string allocation)
        {
            var loads = new List<TypeLoad>(chunks.Count);

            for (int i = 0; i < chunks.Count; i++)
            {
                ChunkDefinition chunk = chunks[i];
                ObstacleSlotData[] slots = chunk.obstacleSlots;
                if (slots == null || slots.Length == 0) continue;

                // Every slot this definition could be picked for is counted, because the pick is a weighted
                // roll and the worst case is the roll going the same way everywhere.
                int count = 0;
                for (int s = 0; s < slots.Length; s++)
                {
                    ObstacleSlotData slot = slots[s];
                    if (slot == null || string.IsNullOrEmpty(slot.obstacleTag)) continue;
                    if (!CarriesTag(slot.obstacleTag, target)) continue;
                    count++;
                }

                if (count == 0) continue;
                loads.Add(new TypeLoad { label = chunk.Id.Value, count = count, cap = caps[i].cap });
            }

            return GreedyBound(loads, window, out allocation);
        }

        private static bool CarriesTag(string tag, ObstacleDefinition def)
        {
            string[] tags = def.tagSet;
            if (tags == null) return false;
            for (int i = 0; i < tags.Length; i++) if (tags[i] == tag) return true;
            return false;
        }

        /// <summary>
        /// Fills the window with the densest types first, each up to its own cap. Returns the total and a
        /// human-readable allocation, because "19" tells a designer nothing about which chunk to change.
        ///
        /// Sorts a copy: the caller's list is indexed by chunk (see <see cref="ObstacleBound"/>), so reordering
        /// it here would silently pair every chunk with another chunk's cap and report a bound that is neither
        /// the caller's nor anybody's.
        /// </summary>
        private static int GreedyBound(List<TypeLoad> loads, int window, out string allocation)
        {
            var order = new List<TypeLoad>(loads);
            order.Sort((a, b) => b.count.CompareTo(a.count));

            int remaining = window;
            int total = 0;
            var sb = new StringBuilder(48);

            for (int i = 0; i < order.Count && remaining > 0; i++)
            {
                TypeLoad load = order[i];
                int take = Mathf.Min(remaining, load.cap);
                if (take <= 0) continue;

                total += take * load.count;
                remaining -= take;

                if (sb.Length > 0) sb.Append(" + ");
                sb.Append(take).Append("×").Append(load.label);
            }

            allocation = sb.Length == 0 ? "no placements" : sb.ToString();
            return total;
        }

        private struct TypeLoad
        {
            public string label;
            public int count;
            public int cap;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The speed a run actually holds, read from the authored curve rather than from the literal
        /// <see cref="BalanceConfig.maxSpeed"/>: the cap is a ceiling the curve may never reach, and the
        /// corridor and reaction rules are only true against the speeds the player can really be at.
        /// </summary>
        private static void SpeedRange(BalanceConfig balance, out float vMin, out float vMax)
        {
            vMin = float.MaxValue;
            vMax = float.MinValue;

            float span = Mathf.Max(1f, balance.speedRampMetres);
            for (int i = 0; i <= 4; i++)
            {
                float speed = balance.SpeedAtDistance(span * i * 0.25f, 1f);
                if (speed < vMin) vMin = speed;
                if (speed > vMax) vMax = speed;
            }

            if (vMax > balance.maxSpeed) vMax = balance.maxSpeed;
            if (vMin > vMax) vMin = vMax;   // an inverted curve is BalanceConfig's error to report.
        }

        private static GameObject ResolvePrefab(IGameDatabase database, string poolKey)
        {
            PoolEntry entry = Find(database.Pools, poolKey);
            return entry != null && entry.prefab != null ? entry.prefab.gameObject : null;
        }

        private static PoolEntry Find(PoolCatalog pools, string key)
        {
            if (pools == null || string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < pools.entries.Count; i++)
            {
                PoolEntry e = pools.entries[i];
                if (e != null && e.key == key) return e;
            }
            return null;
        }

        /// <summary>
        /// The address M3 registers the prefab under is a string written at authoring time, while the pool's
        /// reference follows the asset when it moves. They agree today and diverge the first time someone
        /// reorganises the folder — which would ship a catalog that loads a different object than the pool.
        /// </summary>
        private static void CheckAssetPath(string address, GameObject prefab, string t, string id, ValidationReport report)
        {
            if (string.IsNullOrEmpty(address))
            {
                report.Warn(t, id, "address", "prefabAddress is empty; Addressables will have nothing to register.");
                return;
            }

            string actual = AssetDatabase.GetAssetPath(prefab);
            if (!string.Equals(address, actual, StringComparison.Ordinal))
                report.Error(t, id, "address_drift",
                    $"prefabAddress is '{address}' but the pooled prefab lives at '{actual}'. The pool would spawn one object while the catalog loads another.");
        }

        private static int LaneBits(int mask)
        {
            int bits = 0;
            for (int m = mask; m != 0; m >>= 1) bits += m & 1;
            return bits;
        }

        private static T Load<T>(string path) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(path);
        }

        // ── Report ──────────────────────────────────────────────────────────────────────────────────────
        private static void Print(ValidationReport report, IGameDatabase database, string budget)
        {
            int errors = report.ErrorCount;
            int warnings = 0;

            for (int i = 0; i < report.Issues.Count; i++)
            {
                ValidationIssue issue = report.Issues[i];
                if (issue.IsError) Debug.LogError("[Skyrift] " + issue);
                else { warnings++; Debug.LogWarning("[Skyrift] " + issue); }
            }

            int worlds = database != null ? database.Worlds.Count : 0;
            int chunks = database != null ? database.Chunks.Count : 0;
            int obstacles = database != null ? database.Obstacles.Count : 0;

            string summary = $"[Skyrift] Catalog validation: {worlds} world(s), {chunks} chunk(s), {obstacles} obstacle(s) · " +
                             $"{errors} error(s), {warnings} warning(s).";

            if (errors == 0)
                Debug.Log(summary + " Clean." + (budget != null ? " · " + budget : ""));
            else
                Debug.LogError(summary + " Fix the errors above before building a player.");
        }
    }
}

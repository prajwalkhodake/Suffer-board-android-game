using Skyrift.Audio;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// One greybox obstacle archetype. The same row drives the prefab (<see cref="GreyboxPrefabs"/>) and the
    /// <see cref="ObstacleDefinition"/> (<see cref="GreyboxData"/>), so geometry and clearance data cannot
    /// drift apart — which is the failure mode that makes a greybox feel wrong for reasons nobody can find.
    /// </summary>
    internal sealed class ObstacleSpec
    {
        public string id;
        public string poolKey;
        public string behaviourId;
        public string[] tags;
        public string displayNameKey;

        /// <summary>X = length along the track, Y = height of the top surface above the ground plane.</summary>
        public Vector2 footprint;

        public ClearanceProfile clearance;
        public bool isMoving;
        public ObstacleMotion motion;
        public float launchHeightMultiplier;
        public string impactSfxId;
        public float spawnWeightMultiplier = 1f;
        public int prewarm = 8;

        /// <summary>Placeholder albedo. Encodes the action the archetype demands, not the world's theme.</summary>
        public Color color;
    }

    /// <summary>One authored obstacle placement inside a chunk, before it becomes an <see cref="ObstacleSlotData"/>.</summary>
    internal struct SlotSpec
    {
        public int lane;

        /// <summary>
        /// Metres from the chunk's start edge. The obstacle is placed <b>centred</b> on this point, not starting
        /// at it: the populator sets the root to (laneX, 0, zOffset) and the trigger box is centred on its own
        /// root with size z = footprint.x, so the volume spans z ± footprint.x/2. Every placement number below
        /// and every geometric rule in <see cref="CatalogValidator"/> uses that convention — reading it the other
        /// way shifts a long body by half its length, which is how a rooftop ends up overlapping its own ramp.
        /// </summary>
        public float z;
        public string tag;
        public bool required;
        public float chance;

        public SlotSpec(int lane, float z, string tag, bool required = true, float chance = 1f)
        {
            this.lane = lane;
            this.z = z;
            this.tag = tag;
            this.required = required;
            this.chance = chance;
        }
    }

    /// <summary>One hand-authored track segment (TECH_DOC §5.4, ADR-0005).</summary>
    internal sealed class ChunkSpec
    {
        public string id;
        public string poolKey;
        public string displayNameKey;
        public float length;
        public Vector2 difficultyBand;
        public float baseWeight = 1f;
        public int cooldownChunks;
        public bool safeFallback;
        public int prewarm = 5;
        public SlotSpec[] slots;

        /// <summary>Side rails and lane stripes. The fallback chunk keeps them so an empty run still reads.</summary>
        public bool dressed = true;
    }

    /// <summary>
    /// The M1 greybox content table — placeholder level design, entirely original, and the single source of
    /// truth for both the prefab builder and the data builder.
    ///
    /// <b>Everything here is replaceable content, not architecture.</b> Adding an archetype is a row plus a
    /// prefab; adding a chunk is a row. No core system reads this file (it is Editor-only), which is the
    /// property §7.3 and §15 are about: the shipped game reads <see cref="ObstacleDefinition"/> and
    /// <see cref="ChunkDefinition"/> assets, and these rows exist only to author the first set of them.
    ///
    /// <b>Traversability.</b> Every row of placements leaves at least one lane free, and consecutive rows are
    /// at least 12 m apart. At the slowest authored speed (12 m/s) that is a full second, and a lane change
    /// takes <c>BalanceConfig.laneChangeDuration</c> (0.14 s), so even a two-lane shift between rows is
    /// comfortably inside the window. The validator re-checks this from the assets rather than trusting it.
    /// </summary>
    internal static class GreyboxContent
    {
        // ── Identity ────────────────────────────────────────────────────────────────────────────────────
        public const string WorldId = "world_skyrift";
        public const string WorldTag = "skyrift";
        public const string WorldPackLabel = "world.skyrift";

        public const string BehaviourStandard = "standard";
        public const string BehaviourMoving = "moving";
        public const string BehaviourRamp = "ramp";

        public const string PlayerPrefabName = "gb_player";
        public const string ChaserPrefabName = "gb_chaser";

        // The player and chaser rigs are deliberately absent from the PoolCatalog. PoolEntry.prefab is typed
        // PooledEntity, and neither is one: PlayerMotor and ChaserSystem are constructed once by the boot
        // pipeline and reset in place per run (§4.12), so pooling them would discard the injected references
        // that OnSpawned would then have to re-acquire. They live in the scene, not in a pool.

        // ── World palette ───────────────────────────────────────────────────────────────────────────────
        // These are the same values WorldDefinition defaults to. Keeping one copy here means the materials,
        // the world asset and the camera clear colour cannot disagree about what the horizon looks like.
        public static readonly Color Fog = new Color(0.05f, 0.07f, 0.12f);
        public static readonly Color Ground = new Color(0.16f, 0.18f, 0.24f);
        public static readonly Color Accent = new Color(0.25f, 0.85f, 0.95f);
        public static readonly Color Ambient = new Color(0.35f, 0.4f, 0.5f);

        // ── Track geometry ──────────────────────────────────────────────────────────────────────────────
        /// <summary>Ground beyond the outer lane centres, so a lane change never looks like it leaves the world.</summary>
        public const float ShoulderMetres = 1.2f;

        /// <summary>The ground slab's top face is y = 0, which is the plane the motor measures feet from.</summary>
        public const float GroundThickness = 0.5f;

        public const float RailHeight = 1.1f;
        public const float RailThickness = 0.35f;
        public const float LaneStripeWidth = 0.12f;

        /// <summary>
        /// Obstacle trigger width as a fraction of a lane. 0.9 rather than 1.0 is deliberate: overlap starts
        /// at |dx| &lt; laneWidth·0.45 + playerRadius = 1.53 m = 0.6375 lanes, which leaves a 21 cm band above
        /// <c>grazeLateralThreshold</c> (0.55) where a contact is a side scrape rather than a head-on crash.
        /// At 0.8 that band would be 9 cm — thinner than one frame of lateral motion at speed, so scrapes
        /// would almost never happen and the two-hit stumble model would be unreachable (§4.7).
        /// </summary>
        public const float ObstacleWidthFactor = 0.9f;

        // ── Archetypes ──────────────────────────────────────────────────────────────────────────────────
        // Clearance heights are chosen against the authored jump: apex 2.6 m, so anything a player must jump
        // is ≤ 1.0 m (cleared for most of the arc) and anything they must NOT jump is ≥ 2.4 m.
        private static readonly Color JumpAmber = new Color(0.95f, 0.65f, 0.15f);
        private static readonly Color RollCyan = new Color(0.25f, 0.85f, 0.95f);
        private static readonly Color BlockerRed = new Color(0.85f, 0.20f, 0.25f);
        private static readonly Color TrainSteel = new Color(0.35f, 0.45f, 0.60f);
        private static readonly Color RampGreen = new Color(0.30f, 0.80f, 0.40f);
        private static readonly Color RoofViolet = new Color(0.55f, 0.45f, 0.70f);

        /// <summary>
        /// The six archetypes the brief asks for: low barrier (jump), high gate (roll), full blocker (lane
        /// change), moving train, ramp, and a rooftop platform. The first three are one behaviour class with
        /// three clearance rows — that is the data-driven collision model doing its job (§4.7, §7.3).
        ///
        /// <b>Prewarm is the worst-case concurrent count, not a guess.</b> The live window is
        /// <c>chunksAhead + chunksBehind + 1</c> = 9 chunk instances, and a chunk with cooldown c can occupy at
        /// most <c>ceil(9 / (c + 1))</c> of them. An obstacle pool's bound is then the allocation of those 9
        /// instances that maximises its own tag — which is <i>per pool</i>, because a different allocation
        /// stresses each one, and the pools are not competing for the same slots. <see cref="CatalogValidator"/>
        /// recomputes both that per-pool bound and the joint collider budget on every setup run (§5.14 rule 10),
        /// so these six numbers are checked rather than trusted.
        /// </summary>
        public static ObstacleSpec[] Obstacles()
        {
            return new[]
            {
                new ObstacleSpec
                {
                    id = "obs_low_barrier",
                    poolKey = "gb_obs_low_barrier",
                    behaviourId = BehaviourStandard,
                    tags = new[] { "low", "jumpable" },
                    displayNameKey = "obstacle.low_barrier.name",
                    footprint = new Vector2(1.2f, 1.0f),
                    clearance = new ClearanceProfile
                    {
                        jumpable = true,
                        clearanceHeight = 1.0f,
                        laneOccupancyMask = 0b001
                    },
                    impactSfxId = SfxIds.Graze,
                    prewarm = 18,      // 5 hurdle_line + 4 mixed_run instances, 2 low slots each
                    color = JumpAmber
                },
                new ObstacleSpec
                {
                    id = "obs_high_gate",
                    poolKey = "gb_obs_high_gate",
                    behaviourId = BehaviourStandard,
                    tags = new[] { "high", "rollable" },
                    displayNameKey = "obstacle.high_gate.name",
                    footprint = new Vector2(1.2f, 3.0f),
                    clearance = new ClearanceProfile
                    {
                        rollable = true,
                        // playerRollHeight is 0.9, so a rolling rig passes and a standing one (1.8) does not.
                        underClearance = 1.0f,
                        laneOccupancyMask = 0b001
                    },
                    impactSfxId = SfxIds.Graze,
                    prewarm = 19,      // 5 gate_alley instances (3 each) + 4 mixed_run instances (1 each)
                    color = RollCyan
                },
                new ObstacleSpec
                {
                    id = "obs_full_blocker",
                    poolKey = "gb_obs_full_blocker",
                    behaviourId = BehaviourStandard,
                    tags = new[] { "blocker", "full" },
                    displayNameKey = "obstacle.full_blocker.name",
                    footprint = new Vector2(1.6f, 2.4f),
                    // No clearance route at all: 2.4 m is above the jump apex once forgiveness is spent, so
                    // the only answer is a lane change. Validate() reports this as an intentional WARN.
                    clearance = new ClearanceProfile { laneOccupancyMask = 0b001 },
                    impactSfxId = SfxIds.Crash,
                    prewarm = 22,      // 5 blocker_shift (3) + 3 sweep_cross (2) + 1 mixed_run (1)
                    color = BlockerRed
                },
                new ObstacleSpec
                {
                    id = "obs_train",
                    poolKey = "gb_obs_train",
                    behaviourId = BehaviourMoving,
                    tags = new[] { "train", "moving", "mountable" },
                    displayNameKey = "obstacle.train.name",
                    footprint = new Vector2(14f, 3.2f),
                    clearance = new ClearanceProfile
                    {
                        mountable = true,
                        laneOccupancyMask = 0b001
                    },
                    isMoving = true,
                    // Longitudinal sweep, amplitude 3 m about the slot: a 14 m body centred at z 30 spans
                    // 20..40 m, which is what chunk_train_yard is authored around. Peak closing speed is
                    // amplitude·2π/period = 3.77 m/s, always below the player's 12 m/s floor, so the moving
                    // behaviour's "outrunning the player" guard never makes a train free (§4.7).
                    motion = new ObstacleMotion
                    {
                        axis = ObstacleMotion.MotionAxis.Longitudinal,
                        amplitude = 3f,
                        period = 5f,
                        phase = 0f
                    },
                    impactSfxId = SfxIds.Crash,
                    spawnWeightMultiplier = 0.9f,
                    prewarm = 6,       // 3 train_yard instances × 2 slots (the centre one is optional)
                    color = TrainSteel
                },
                new ObstacleSpec
                {
                    id = "obs_ramp",
                    poolKey = "gb_obs_ramp",
                    behaviourId = BehaviourRamp,
                    tags = new[] { "ramp", "launch" },
                    displayNameKey = "obstacle.ramp.name",
                    footprint = new Vector2(4.0f, 1.4f),
                    clearance = new ClearanceProfile
                    {
                        mountable = true,
                        laneOccupancyMask = 0b001
                    },
                    // 1.35 × JumpVelocity (15.76) = 21.3 m/s ⇒ apex v²/2g = 4.75 m, air time 0.89 s. That
                    // clears the 3.2 m roof with margin at every authored speed, and stays far below the
                    // 9.4 m apex a 1.9 multiplier would produce (which would lose the player off-screen).
                    launchHeightMultiplier = 1.35f,
                    impactSfxId = SfxIds.RampLaunch,
                    prewarm = 3,       // 3 ramp_roof instances, the only chunk that places one
                    color = RampGreen
                },
                new ObstacleSpec
                {
                    id = "obs_rooftop",
                    poolKey = "gb_obs_rooftop",
                    behaviourId = BehaviourStandard,
                    tags = new[] { "rooftop", "mountable" },
                    displayNameKey = "obstacle.rooftop.name",
                    // 18 m of roof, not 10: the ramp's landing point moves with speed (z≈21 at 12 m/s, z≈32
                    // at 28 m/s), so a short platform would be landable at one end of the ramp and not the
                    // other. chunk_ramp_roof places it centred at z 27, spanning 18..36, which covers both.
                    footprint = new Vector2(18f, 3.2f),
                    clearance = new ClearanceProfile
                    {
                        mountable = true,
                        laneOccupancyMask = 0b001
                    },
                    impactSfxId = SfxIds.Crash,
                    prewarm = 3,       // 3 ramp_roof instances, the only chunk that places one
                    color = RoofViolet
                }
            };
        }

        /// <summary>
        /// Eight hand-authored chunks. Difficulty bands overlap on purpose so the generator always has a
        /// weighted choice rather than a single eligible row, and <c>chunk_open_run</c> is the mandatory
        /// safe fallback (§5.4). No chunk places more than four obstacles, which keeps the conservative
        /// active-collider bound at 4 × 9 + 1 = 37, inside §11.1's 40.
        /// </summary>
        public static ChunkSpec[] Chunks()
        {
            return new[]
            {
                new ChunkSpec
                {
                    id = "chunk_open_run",
                    poolKey = "gb_chunk_open_run",
                    displayNameKey = "chunk.open_run.name",
                    length = 40f,
                    difficultyBand = new Vector2(0f, 1f),
                    baseWeight = 0.8f,
                    safeFallback = true,
                    // The fallback may repeat freely, so it needs the whole live window to itself.
                    prewarm = 10,
                    slots = new SlotSpec[0]
                },
                new ChunkSpec
                {
                    id = "chunk_hurdle_line",
                    poolKey = "gb_chunk_hurdle_line",
                    displayNameKey = "chunk.hurdle_line.name",
                    length = 40f,
                    difficultyBand = new Vector2(0f, 0.5f),
                    baseWeight = 1.2f,
                    cooldownChunks = 1,
                    // Two single-lane hurdles 14 m apart: the first jump can be taken from any lane, and the
                    // second only punishes a player who drifted left. The gentlest teaching chunk.
                    slots = new[]
                    {
                        new SlotSpec(1, 14f, "low"),
                        new SlotSpec(0, 28f, "low")
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_gate_alley",
                    poolKey = "gb_chunk_gate_alley",
                    displayNameKey = "chunk.gate_alley.name",
                    length = 40f,
                    difficultyBand = new Vector2(0.1f, 0.6f),
                    baseWeight = 1.1f,
                    cooldownChunks = 1,
                    // Outer lanes gated at 12 m ⇒ the centre is the only free lane; the centre is then gated
                    // at 28 m ⇒ roll, or shift out. Both rows are survivable without a roll, which is what
                    // keeps this in the low difficulty band.
                    slots = new[]
                    {
                        new SlotSpec(0, 12f, "high"),
                        new SlotSpec(2, 12f, "high"),
                        new SlotSpec(1, 28f, "high")
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_blocker_shift",
                    poolKey = "gb_chunk_blocker_shift",
                    displayNameKey = "chunk.blocker_shift.name",
                    length = 40f,
                    difficultyBand = new Vector2(0.2f, 0.75f),
                    baseWeight = 1.0f,
                    cooldownChunks = 1,
                    // Left and centre blocked at 10 m ⇒ move right; right blocked at 26 m ⇒ move back. A
                    // two-lane round trip with 16 m between decisions, so it is readable at every speed.
                    slots = new[]
                    {
                        new SlotSpec(0, 10f, "blocker"),
                        new SlotSpec(1, 10f, "blocker"),
                        new SlotSpec(2, 26f, "blocker")
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_mixed_run",
                    poolKey = "gb_chunk_mixed_run",
                    displayNameKey = "chunk.mixed_run.name",
                    length = 48f,
                    difficultyBand = new Vector2(0.25f, 0.85f),
                    baseWeight = 1.15f,
                    cooldownChunks = 1,
                    // One of each answer in sequence: jump at 10, then at 24 roll the left lane or take the
                    // centre (the right is a hard blocker), then an optional jump at 38.
                    slots = new[]
                    {
                        new SlotSpec(1, 10f, "low"),
                        new SlotSpec(0, 24f, "high"),
                        new SlotSpec(2, 24f, "blocker"),
                        new SlotSpec(1, 38f, "low", false, 0.7f)
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_train_yard",
                    poolKey = "gb_chunk_train_yard",
                    displayNameKey = "chunk.train_yard.name",
                    length = 56f,
                    difficultyBand = new Vector2(0.35f, 1f),
                    baseWeight = 0.9f,
                    cooldownChunks = 2,
                    // A gate in the right lane at 14 m, then sweeping trains from 30 m (a 14 m body is centred
                    // there, so it reaches 23..37, and its ±3 m sweep puts it between 20 and 40 m), then a
                    // hurdle in the right lane at 48 m. The right lane is the thread through the whole chunk:
                    // roll, stay right, jump.
                    // Taking a train roof instead is the greedy line and needs a ramp, which this chunk does
                    // not offer.
                    slots = new[]
                    {
                        new SlotSpec(2, 14f, "high"),
                        new SlotSpec(0, 30f, "train"),
                        new SlotSpec(1, 30f, "train", false, 0.6f),
                        new SlotSpec(2, 48f, "low")
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_ramp_roof",
                    poolKey = "gb_chunk_ramp_roof",
                    displayNameKey = "chunk.ramp_roof.name",
                    length = 60f,
                    difficultyBand = new Vector2(0.3f, 1f),
                    baseWeight = 0.85f,
                    cooldownChunks = 2,
                    // The rooftop line: ramp centred at 10 m (crest at 12), roof centred at 27 m (spanning
                    // 18..36), run off the far edge and drop 3.2 m (0.37 s ⇒ 4.4 m at 12 m/s, 10.2 m at
                    // 28 m/s), then an optional hurdle at 54 m.
                    //
                    // The 6 m between the crest and the roof's near edge is derived, not tuned. A 1.35 × jump
                    // velocity launch (v₀ = 15.76 m/s ⇒ 21.3 m/s at g = 47.75) puts the feet above the 3.2 m
                    // roof between t = 0.17 s and t = 0.72 s, so:
                    //   gap ≥ 0.17·vMax(28) = 4.8 m, or the player is still rising when they reach the volume
                    //     and the analytic model reads it as a side contact — an instant crash into the roof;
                    //   gap ≤ 0.72·vMin(12) = 8.6 m, or they land on the ground first and then hit that side.
                    // The roof's far edge must also be beyond 0.72·vMax from the crest (32.2 m ⇒ z 36 or
                    // later), which is what the 18 m length buys. CatalogValidator recomputes all three
                    // bounds from the assets on every setup run (§4.7).
                    //
                    // A z of 16 fails the first bound: centred, the roof would span 7..25 and swallow the ramp.
                    //
                    // Missing the ramp is not fatal — the roof is in the centre lane, so the outer lanes
                    // bypass it.
                    slots = new[]
                    {
                        new SlotSpec(1, 10f, "ramp"),
                        new SlotSpec(1, 27f, "rooftop"),
                        new SlotSpec(1, 54f, "low", false, 0.7f)
                    }
                },
                new ChunkSpec
                {
                    id = "chunk_sweep_cross",
                    poolKey = "gb_chunk_sweep_cross",
                    displayNameKey = "chunk.sweep_cross.name",
                    length = 48f,
                    difficultyBand = new Vector2(0.5f, 1f),
                    baseWeight = 0.8f,
                    cooldownChunks = 2,
                    // The hardest M1 chunk: outers blocked at 12 m ⇒ centre, centre gated at 26 m ⇒ roll or
                    // shift, then hurdles on both outers at 40 m ⇒ the centre is the safe lane again. Three
                    // decisions in 28 m, which is why it only appears above difficulty 0.5.
                    slots = new[]
                    {
                        new SlotSpec(0, 12f, "blocker"),
                        new SlotSpec(2, 12f, "blocker"),
                        new SlotSpec(1, 26f, "high"),
                        new SlotSpec(0, 40f, "low")
                    }
                }
            };
        }
    }
}
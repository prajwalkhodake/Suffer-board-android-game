using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// The shared clearance reading (TECH_DOC §4.7). Every archetype uses the same rules; what differs is
    /// the authored <see cref="ClearanceProfile"/>. That is the whole point of the data-driven collision
    /// model: a low barrier, a roll-under gate and a full blocker are three rows of data, not three classes.
    /// </summary>
    public static class ObstacleClearance
    {
        /// <summary>Feet above the obstacle's clearance height, with the authored vertical forgiveness.</summary>
        public static bool ClearedByJump(in ContactContext ctx, float forgiveness)
        {
            return ctx.Clearance.jumpable && ctx.HeightAboveGround + forgiveness >= ctx.Clearance.clearanceHeight;
        }

        /// <summary>Rolling low enough to pass underneath.</summary>
        public static bool ClearedByRoll(in ContactContext ctx, float forgiveness)
        {
            return ctx.Clearance.rollable
                   && ctx.VerticalState == PlayerVerticalState.Rolling
                   && ctx.PlayerHeight <= ctx.Clearance.underClearance + forgiveness;
        }

        /// <summary>Landed on top of a mountable surface (rooftop, train roof, ramp crest).</summary>
        public static bool ClearedByMount(in ContactContext ctx, float forgiveness)
        {
            return ctx.Clearance.mountable && ctx.HeightAboveGround + forgiveness >= ctx.ObjectHeight;
        }

        /// <summary>
        /// True when the player only clipped the side of the object. The threshold is in lane widths, so
        /// it scales with any lane width a world authors.
        /// </summary>
        public static bool IsSideScrape(in ContactContext ctx, float grazeThreshold)
        {
            return ctx.LateralOffset >= grazeThreshold;
        }

        /// <summary>
        /// Cleared if any authored clearance route was met; otherwise a side scrape stumbles and a head-on
        /// hit crashes. Escalating the second stumble into a crash is the resolver's job, not this one.
        /// </summary>
        public static ContactOutcome Classify(in ContactContext ctx)
        {
            BalanceConfig balance = ctx.Balance;

            // An unconfigured balance degrades to the most forgiving reading (no instant crash). Boot
            // validation rejects a null BalanceConfig, so this branch is unreachable in a shipped build.
            float forgiveness = balance != null ? balance.clearanceForgiveness : 0f;
            float graze = balance != null ? balance.grazeLateralThreshold : 0f;

            if (ClearedByJump(in ctx, forgiveness)) return ContactOutcome.Cleared;
            if (ClearedByRoll(in ctx, forgiveness)) return ContactOutcome.Cleared;
            if (ClearedByMount(in ctx, forgiveness)) return ContactOutcome.Cleared;

            return IsSideScrape(in ctx, graze) ? ContactOutcome.Stumbled : ContactOutcome.Crashed;
        }
    }

    /// <summary>
    /// Default archetype behaviour: low barriers, roll-under gates, full blockers and train bodies are all
    /// this class with different <see cref="ClearanceProfile"/> data (playbook §7.3).
    /// </summary>
    [CreateAssetMenu(fileName = "StandardObstacleBehaviour", menuName = "Skyrift/Gameplay/Obstacle Behaviours/Standard")]
    public sealed class StandardObstacleBehaviour : ObstacleBehaviourAsset
    {
        public override ContactOutcome Resolve(in ContactContext ctx)
        {
            return ObstacleClearance.Classify(in ctx);
        }
    }

    /// <summary>
    /// Moving archetype (trains, sweepers, drones). Adds one rule on top of the standard clearance: an
    /// object that is travelling away at least as fast as the player cannot be hit head-on, so the contact
    /// is ignored rather than unfairly fatal.
    /// </summary>
    [CreateAssetMenu(fileName = "MovingObstacleBehaviour", menuName = "Skyrift/Gameplay/Obstacle Behaviours/Moving")]
    public sealed class MovingObstacleBehaviour : ObstacleBehaviourAsset
    {
        public override ContactOutcome Resolve(in ContactContext ctx)
        {
            if (ctx.RelativeSpeed <= 0f) return ContactOutcome.Ignored;
            return ObstacleClearance.Classify(in ctx);
        }
    }

    /// <summary>
    /// Ramp archetype: touching it is always a successful launch, never a hit. The impulse itself is data
    /// on the obstacle definition and is applied by the contact sensor through <c>IPlayerMotor.RequestLaunch</c>,
    /// which keeps this resolver pure (TECH_DOC §4.7).
    /// </summary>
    [CreateAssetMenu(fileName = "RampObstacleBehaviour", menuName = "Skyrift/Gameplay/Obstacle Behaviours/Ramp")]
    public sealed class RampObstacleBehaviour : ObstacleBehaviourAsset
    {
        public override ContactOutcome Resolve(in ContactContext ctx)
        {
            // Only a ramp approached from the front or below launches; landing on its crest from above is
            // a mount, which the standard clearance already reads correctly.
            if (ctx.Surface == ContactSurface.Top && ctx.Clearance.mountable)
                return ObstacleClearance.Classify(in ctx);

            return ContactOutcome.Cleared;
        }
    }
}
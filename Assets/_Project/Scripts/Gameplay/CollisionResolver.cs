using System;
using Skyrift.Data;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// Turns a classified contact into the outcome the run actually applies (TECH_DOC §4.7 extension
    /// point). Swapping this object replaces the whole stumble model — for example a one-hit mode for a
    /// hard event, or a three-hit mode for a tutorial — without touching the resolver or any behaviour.
    /// </summary>
    public interface IContactOutcomePolicy
    {
        ContactOutcome Apply(ContactOutcome classified, int stumbleCount, BalanceConfig balance);
    }

    /// <summary>
    /// The shipped model: the first side scrape stumbles and warns the chaser, the second crashes.
    /// A head-on hit is always an instant crash (TECH_DOC §4.7 outcome table).
    /// </summary>
    public sealed class TwoHitStumblePolicy : IContactOutcomePolicy
    {
        public ContactOutcome Apply(ContactOutcome classified, int stumbleCount, BalanceConfig balance)
        {
            if (classified != ContactOutcome.Stumbled) return classified;
            return stumbleCount >= balance.maxStumblesBeforeCrash ? ContactOutcome.Crashed : ContactOutcome.Stumbled;
        }
    }

    /// <summary>
    /// Deterministic contact resolution (TECH_DOC §4.7, ADR-0006). No physics, no raycasts, no allocation:
    /// the sensor builds a <see cref="ContactContext"/> and this classifies it.
    ///
    /// Purity: the classification is a pure function of the context. The only state is the stumble counter,
    /// which is exposed and resettable so the escalation can be unit-tested without a running game.
    /// </summary>
    public sealed class CollisionResolver : ICollisionResolver
    {
        private readonly BalanceConfig _balance;
        private readonly IContactOutcomePolicy _policy;

        public int StumbleCount { get; private set; }

        public CollisionResolver(BalanceConfig balance, IContactOutcomePolicy policy = null)
        {
            _balance = balance != null ? balance : throw new ArgumentNullException(nameof(balance));
            _policy = policy ?? new TwoHitStumblePolicy();
        }

        public ContactOutcome Resolve(in ContactContext ctx)
        {
            ContactOutcome classified = Classify(in ctx);

            // Only a stumble consumes the escalation counter; a clear or a graze leaves it untouched.
            if (classified == ContactOutcome.Stumbled) StumbleCount++;

            return _policy.Apply(classified, StumbleCount, _balance);
        }

        public void ResetForRun() => StumbleCount = 0;

        public void ResetStumbles() => StumbleCount = 0;

        private ContactOutcome Classify(in ContactContext ctx)
        {
            ContactOutcome outcome = ctx.Behaviour != null
                ? ctx.Behaviour.Resolve(in ctx)
                : ObstacleClearance.Classify(in ctx);

            // A crash-absorbing power-up only spends a charge on a hit that would actually hurt. Burning a
            // shield on an obstacle the player cleanly jumped would be a silent punishment for playing well.
            if (ctx.HasShield && (outcome == ContactOutcome.Stumbled || outcome == ContactOutcome.Crashed))
                return ContactOutcome.Grazed;

            return outcome;
        }
    }
}
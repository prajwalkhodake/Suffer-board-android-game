using System.Threading;
using System.Threading.Tasks;
using Skyrift.Core;
using Skyrift.Data;

namespace Skyrift.Gameplay
{
    /// <summary>
    /// The shipped M1 policy: continues are not offered (TECH_DOC §4.12). This is a complete, real policy —
    /// not a stub — because "no continue in this build" is a legitimate product state, and it is what keeps
    /// the crash → results path honest while Economy and Ads do not exist yet.
    ///
    /// M2 replaces it with a policy that reads the same <see cref="ContinueConfig"/> and spends keys through
    /// <c>IWallet</c> or grants a rewarded ad through <c>IAdsService</c>. That policy lives outside Gameplay,
    /// because the run loop must never reference Economy or Services (§3.2). Nothing here changes when it lands.
    /// </summary>
    public sealed class NoContinueOfferPolicy : IContinueOfferPolicy
    {
        public bool CanOffer(in RunSummary summary, int continuesUsed) => false;

        public ContinueOffer Build(in RunSummary summary, int continuesUsed) => ContinueOffer.NotAvailable;

        public Task<ContinueResult> GrantAsync(ContinueSource source, in ContinueOffer offer, CancellationToken ct)
        {
            return Task.FromResult(ContinueResult.Fail(ErrorCodes.NotAvailable));
        }
    }

    /// <summary>
    /// Development policy that grants continues for free, so the whole crash → offer → continue → resume
    /// sequence can be exercised and tested before Economy and Ads exist (TECH_DOC §4.12, §12.3).
    ///
    /// Every gate still comes from <see cref="ContinueConfig"/>: the cap, the countdown and the offered cost
    /// are the authored ones, so the UI and the run controller see exactly the shape they will see in M2.
    /// Only the payment is bypassed, and only when <c>allowFreeContinues</c> is explicitly set — a non-DEV
    /// build compiles the flag away at the composition root, and <c>SettingsEnforcer</c> fails a release build
    /// that still has <c>DEV</c> defined, so the two gates are independent (§4.12, §12.2).
    /// </summary>
    public sealed class DebugContinueOfferPolicy : IContinueOfferPolicy
    {
        private readonly ContinueConfig _config;
        private readonly bool _allowFreeContinues;

        public DebugContinueOfferPolicy(ContinueConfig config, bool allowFreeContinues)
        {
            _config = config;
            _allowFreeContinues = allowFreeContinues;
        }

        public bool CanOffer(in RunSummary summary, int continuesUsed)
        {
            if (!_allowFreeContinues || _config == null || !_config.enabled) return false;
            if (continuesUsed < 0) return false;

            // A quit or a system error is not a crash the player can buy their way out of.
            if (summary.Reason != EndRunReason.None && summary.Reason != EndRunReason.Crash) return false;

            return continuesUsed < _config.maxContinuesPerRun;
        }

        public ContinueOffer Build(in RunSummary summary, int continuesUsed)
        {
            if (!CanOffer(in summary, continuesUsed)) return ContinueOffer.NotAvailable;

            // The real key cost is still reported so the offer screen renders the shipped layout; the debug
            // grant simply does not charge it.
            return new ContinueOffer(true, _config.KeyCostFor(continuesUsed), continuesUsed,
                _config.adContinueEnabled, _config.offerSeconds);
        }

        public Task<ContinueResult> GrantAsync(ContinueSource source, in ContinueOffer offer, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(ContinueResult.Fail(ErrorCodes.Cancelled));
            if (!_allowFreeContinues || !offer.IsAvailable) return Task.FromResult(ContinueResult.Fail(ErrorCodes.NotAvailable));
            if (source != ContinueSource.DebugFree) return Task.FromResult(ContinueResult.Fail(ErrorCodes.NotAvailable));

            return Task.FromResult(ContinueResult.Ok(ContinueSource.DebugFree, 0));
        }
    }
}
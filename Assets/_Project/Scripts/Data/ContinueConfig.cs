using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// Crash → continue tuning (TECH_DOC §4.12, §5.13). Every price, cap and timer lives here so the
    /// <c>IContinueOfferPolicy</c> implementation contains no balance numbers and Remote Config can
    /// override the whole asset without a build.
    ///
    /// M1 ships the data and a policy that reads it; the key/ad payment path arrives with Economy in M2.
    /// </summary>
    [CreateAssetMenu(fileName = "ContinueConfig", menuName = "Skyrift/Config/Continue")]
    public sealed class ContinueConfig : DefinitionBase
    {
        [Tooltip("Master gate. Remote Config may switch continues off for a live-ops incident.")]
        public bool enabled = true;

        [Tooltip("Hard cap per run, independent of how many keys the player owns.")]
        [Min(0)] public int maxContinuesPerRun = 2;

        [Tooltip("Escalating key cost. Index = how many continues this run already used; the last entry repeats.")]
        public int[] keyCosts = { 1, 2 };

        [Tooltip("Countdown on the offer screen. Expiry is a decline, never a free continue.")]
        [Min(0.5f)] public float offerSeconds = 8f;

        [Tooltip("Whether a rewarded ad may pay for a continue. Still gated by consent and ad readiness at runtime.")]
        public bool adContinueEnabled = true;

        [Header("Respawn")]
        [Tooltip("Invulnerability granted on respawn so the player is not killed by the obstacle they just hit.")]
        [Min(0f)] public float respawnInvulnerabilitySeconds = 2f;

        [Tooltip("Clear the stumble counter on respawn, so a continue is not one scrape from death.")]
        public bool resetStumblesOnContinue = true;

        [Tooltip("Release obstacles this far ahead of the crash point on respawn.")]
        [Min(0f)] public float respawnClearanceMetres = 30f;

        /// <summary>Cost of the continue at <paramref name="continueIndex"/> (0-based). Clamped to the last authored entry.</summary>
        public int KeyCostFor(int continueIndex)
        {
            if (keyCosts == null || keyCosts.Length == 0) return 0;
            int index = Mathf.Clamp(continueIndex, 0, keyCosts.Length - 1);
            return Mathf.Max(0, keyCosts[index]);
        }

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(ContinueConfig);

            if (maxContinuesPerRun < 0) report.Error(t, Id, "cap", "maxContinuesPerRun must be >= 0.");
            if (offerSeconds <= 0f) report.Error(t, Id, "offer_seconds", "offerSeconds must be > 0.");

            if (!enabled) return;

            if (keyCosts == null || keyCosts.Length == 0)
            {
                report.Error(t, Id, "key_costs", "An enabled continue config needs at least one key cost.");
                return;
            }

            for (int i = 0; i < keyCosts.Length; i++)
            {
                if (keyCosts[i] <= 0)
                    report.Error(t, Id, "key_cost_positive", $"keyCosts[{i}] must be > 0; a free continue is a debug policy, not data.");

                // Escalation is the design rule (§4.12): each continue costs at least as much as the previous one.
                if (i > 0 && keyCosts[i] < keyCosts[i - 1])
                    report.Error(t, Id, "key_cost_escalation", "keyCosts must be non-decreasing.");
            }

            if (keyCosts.Length < maxContinuesPerRun)
                report.Warn(t, Id, "key_cost_coverage", "keyCosts has fewer entries than maxContinuesPerRun; the last cost will repeat.");
        }
    }
}
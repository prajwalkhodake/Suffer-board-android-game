namespace Skyrift.Audio
{
    /// <summary>
    /// Cue ids the shipped game plays (TECH_DOC §4.18). These are infrastructure constants naming rows the
    /// editor setup tool authors into the <see cref="AudioCatalog"/>, exactly as <c>InputActionNames</c>
    /// names the generated action map. A new sound is a new row plus a constant here — no routing code changes.
    ///
    /// Every id must exist in the catalog; the editor validator reports a missing row as an error so a
    /// silent cue cannot ship unnoticed.
    /// </summary>
    public static class SfxIds
    {
        // Locomotion
        public const string Jump = "sfx_jump";
        public const string Land = "sfx_land";
        public const string RollStart = "sfx_roll_start";
        public const string RollEnd = "sfx_roll_end";
        public const string LaneChange = "sfx_lane_change";
        public const string RampLaunch = "sfx_ramp_launch";

        // Contact
        public const string Stumble = "sfx_stumble";
        public const string Crash = "sfx_crash";
        public const string Graze = "sfx_graze";

        // Chaser
        public const string ChaserWarning = "sfx_chaser_warning";
        public const string ChaserCaught = "sfx_chaser_caught";

        // Run lifecycle
        public const string RunStart = "sfx_run_start";
        public const string RunEnd = "sfx_run_end";
        public const string ContinueOffer = "sfx_continue_offer";
        public const string ContinueGranted = "sfx_continue_granted";

        // Collectibles (M2 wires the award path; the cues are authored now so the bank is complete)
        public const string Coin = "sfx_coin";
        public const string Key = "sfx_key";
        public const string PowerUp = "sfx_powerup";

        // UI
        public const string UiClick = "sfx_ui_click";
        public const string UiBack = "sfx_ui_back";

        /// <summary>
        /// Every cue the game plays, in declaration order. The editor validator iterates this to prove the
        /// catalog has a row for each one; keeping the list here rather than in the tool means adding a cue
        /// constant without adding it here is the only way to slip past the check, and that is a one-line
        /// edit in the same file. Allocated once at type initialisation, never per frame (§11.2).
        /// </summary>
        public static readonly string[] All =
        {
            Jump, Land, RollStart, RollEnd, LaneChange, RampLaunch,
            Stumble, Crash, Graze,
            ChaserWarning, ChaserCaught,
            RunStart, RunEnd, ContinueOffer, ContinueGranted,
            Coin, Key, PowerUp,
            UiClick, UiBack
        };
    }
}
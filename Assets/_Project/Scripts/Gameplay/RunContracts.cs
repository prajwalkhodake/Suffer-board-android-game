using System.Threading;
using System.Threading.Tasks;
using Skyrift.Core;
using Skyrift.Core.Ids;

namespace Skyrift.Gameplay
{
    /// <summary>Lifecycle of a single run, owned by <see cref="IRunController"/> (TECH_DOC §4.12).</summary>
    public enum RunState
    {
        Idle = 0,
        Starting,
        Running,
        Paused,
        Crashed,
        ContinueOffer,
        Ending,
        Ended
    }

    /// <summary>Why the run stopped. Distinguishes a crash from a player quit for analytics and rewards.</summary>
    public enum EndRunReason
    {
        None = 0,
        Crash,
        PlayerQuit,
        ContinueDeclined,
        SystemError
    }

    /// <summary>What paid for a continue (TECH_DOC §4.12). Keys and rewarded ads are the shipped sources.</summary>
    public enum ContinueSource
    {
        None = 0,
        Keys,
        RewardedAd,
        DebugFree
    }

    /// <summary>
    /// Immutable bundle that fully determines a run (TECH_DOC §4.12, glossary). Same context + same
    /// catalogs + same Remote Config ⇒ the same run, which is what makes daily challenges and bug repro
    /// possible. Meta resolves the character/board/perks before this is built; Gameplay only consumes it.
    /// </summary>
    public readonly struct RunStartContext
    {
        public readonly uint Seed;
        public readonly StableId WorldId;
        public readonly StableId CharacterId;
        public readonly StableId BoardId;

        /// <summary>Remote Config speed scalar applied to the whole speed curve (1 = authored).</summary>
        public readonly float SpeedScale;

        /// <summary>Remote Config difficulty scalar applied to the track difficulty ramp.</summary>
        public readonly float DifficultyScale;

        /// <summary>Lane to start in, or -1 for the centre lane.</summary>
        public readonly int StartLane;

        public readonly bool IsTutorial;

        public RunStartContext(uint seed, StableId worldId, StableId characterId, StableId boardId,
            float speedScale = 1f, float difficultyScale = 1f, int startLane = -1, bool isTutorial = false)
        {
            Seed = seed;
            WorldId = worldId;
            CharacterId = characterId;
            BoardId = boardId;
            SpeedScale = speedScale > 0f ? speedScale : 1f;
            DifficultyScale = difficultyScale > 0f ? difficultyScale : 1f;
            StartLane = startLane;
            IsTutorial = isTutorial;
        }
    }

    /// <summary>
    /// The offer shown on the Continue screen. Built by <see cref="IContinueOfferPolicy"/> from data and
    /// Remote Config; the controller never computes a price itself.
    /// </summary>
    public readonly struct ContinueOffer
    {
        public readonly bool IsAvailable;
        public readonly int KeyCost;
        public readonly int ContinueIndex;
        public readonly bool AdAvailable;
        public readonly float SecondsRemaining;

        public ContinueOffer(bool isAvailable, int keyCost, int continueIndex, bool adAvailable, float secondsRemaining)
        {
            IsAvailable = isAvailable;
            KeyCost = keyCost;
            ContinueIndex = continueIndex;
            AdAvailable = adAvailable;
            SecondsRemaining = secondsRemaining;
        }

        public static readonly ContinueOffer NotAvailable = new ContinueOffer(false, 0, 0, false, 0f);
    }

    /// <summary>Outcome of a continue attempt. A refused continue is a value, never an exception.</summary>
    public readonly struct ContinueResult
    {
        public readonly bool IsSuccess;
        public readonly string ErrorCode;
        public readonly ContinueSource Source;
        public readonly int KeysSpent;

        private ContinueResult(bool success, string code, ContinueSource source, int keysSpent)
        {
            IsSuccess = success;
            ErrorCode = code ?? string.Empty;
            Source = source;
            KeysSpent = keysSpent;
        }

        public static ContinueResult Ok(ContinueSource source, int keysSpent) => new ContinueResult(true, null, source, keysSpent);
        public static ContinueResult Fail(string code) => new ContinueResult(false, code, ContinueSource.None, 0);
    }

    /// <summary>
    /// Decides whether a continue may be offered, what it costs and whether the grant succeeded
    /// (TECH_DOC §4.12 extension point). Implemented outside Gameplay (it needs Economy and Ads, which
    /// the run loop must never reference) and injected by the composition root.
    /// </summary>
    public interface IContinueOfferPolicy
    {
        bool CanOffer(in RunSummary summary, int continuesUsed);
        ContinueOffer Build(in RunSummary summary, int continuesUsed);
        Task<ContinueResult> GrantAsync(ContinueSource source, in ContinueOffer offer, CancellationToken ct);
    }

    /// <summary>
    /// Final, immutable result of a run. The results screen reads this and nothing else — it must never
    /// recompute a score (TECH_DOC §4.11).
    /// </summary>
    public readonly struct RunSummary
    {
        public readonly ScoreBreakdown Breakdown;
        public readonly float DistanceMetres;
        public readonly double DurationSeconds;
        public readonly uint Seed;
        public readonly StableId WorldId;
        public readonly StableId CharacterId;
        public readonly EndRunReason Reason;
        public readonly CrashCause CrashCause;
        public readonly int ContinuesUsed;
        public readonly int Stumbles;

        public RunSummary(ScoreBreakdown breakdown, float distanceMetres, double durationSeconds, uint seed,
            StableId worldId, StableId characterId, EndRunReason reason, CrashCause crashCause,
            int continuesUsed, int stumbles)
        {
            Breakdown = breakdown;
            DistanceMetres = distanceMetres;
            DurationSeconds = durationSeconds;
            Seed = seed;
            WorldId = worldId;
            CharacterId = characterId;
            Reason = reason;
            CrashCause = crashCause;
            ContinuesUsed = continuesUsed;
            Stumbles = stumbles;
        }

        public long Score => Breakdown.Total;
        public int Coins => Breakdown.Coins;
        public int Keys => Breakdown.Keys;
    }

    /// <summary>
    /// The only place that starts, pauses and ends a run, and the only place that drives the
    /// crash → continue → results sequence (TECH_DOC §4.12).
    /// </summary>
    public interface IRunController
    {
        RunState State { get; }

        /// <summary>
        /// True once <c>Construct</c> accepted a complete system bundle. The composition root checks this
        /// before reporting a successful boot, so a wiring error is a boot failure naming the missing
        /// member rather than a run that silently never moves.
        /// </summary>
        bool IsReady { get; }

        Task<Result> StartRunAsync(RunStartContext ctx, CancellationToken ct);
        void Pause();
        void Resume();
        void EndRun(EndRunReason reason);
        ContinueOffer BuildContinueOffer();
        Task<ContinueResult> TryContinueAsync(ContinueSource source, CancellationToken ct);
        RunSummary Summary { get; }

        /// <summary>
        /// Live countdown on the offer currently on screen, in unscaled seconds; 0 when no offer is up.
        /// Separate from <see cref="BuildContinueOffer"/> because that is a policy query (which may consult
        /// ad readiness) and the offer screen needs a cheap per-frame number.
        /// </summary>
        float ContinueSecondsRemaining { get; }

        /// <summary>Advances every run system by one simulated step. Called by the composition root.</summary>
        void Tick(float dt);
    }
}
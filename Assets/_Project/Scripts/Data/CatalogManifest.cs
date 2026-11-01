using System.Threading;
using System.Threading.Tasks;
using Skyrift.Core;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>
    /// Index of every content definition and config for a build (TECH_DOC §4.20). M1 ships a local
    /// manifest; M3 swaps in an Addressables-backed source through <see cref="ICatalogSource"/> without
    /// touching any consumer (ADR-0012).
    /// </summary>
    [CreateAssetMenu(fileName = "CatalogManifest", menuName = "Skyrift/Catalogs/Catalog Manifest")]
    public sealed class CatalogManifest : DefinitionBase
    {
        [Tooltip("Bumped when content changes; reported through Remote Config for live-ops gating.")]
        public string contentVersion = "0.1.0";

        public WorldDefinition[] worlds;
        public ChunkDefinition[] chunks;
        public ObstacleDefinition[] obstacles;

        [Header("Configs")]
        public BalanceConfig balance;
        public TrackGenerationConfig trackGeneration;
        public InputConfig input;
        public ScoringConfig scoring;
        public PoolCatalog poolCatalog;
        public ContinueConfig continueConfig;
        public AudioConfig audio;

        public override void Validate(ValidationReport report)
        {
            base.Validate(report);
            const string t = nameof(CatalogManifest);

            if (balance == null) report.Error(t, Id, "config", "balance config is missing.");
            if (trackGeneration == null) report.Error(t, Id, "config", "trackGeneration config is missing.");
            if (input == null) report.Error(t, Id, "config", "input config is missing.");
            if (scoring == null) report.Error(t, Id, "config", "scoring config is missing.");
            if (poolCatalog == null) report.Error(t, Id, "config", "poolCatalog is missing.");
            if (continueConfig == null) report.Error(t, Id, "config", "continueConfig is missing.");
            if (audio == null) report.Error(t, Id, "config", "audio config is missing.");
            if (worlds == null || worlds.Length == 0) report.Error(t, Id, "worlds", "At least one world is required.");
            if (chunks == null || chunks.Length == 0) report.Error(t, Id, "chunks", "At least one chunk is required.");

            // Configs are single assets, not repository entries, so nothing else validates them. Running
            // their rules here is what makes a bad authored number a boot-time report instead of a
            // silently wrong game feel.
            balance?.Validate(report);
            trackGeneration?.Validate(report);
            input?.Validate(report);
            scoring?.Validate(report);
            poolCatalog?.Validate(report);
            continueConfig?.Validate(report);
            audio?.Validate(report);
        }
    }

    /// <summary>
    /// Loads the catalog. Extension point: the Addressables/Play Asset Delivery implementation replaces
    /// the local one in M3 with no change to GameDatabase or its consumers.
    /// </summary>
    public interface ICatalogSource
    {
        Task<Result<CatalogManifest>> LoadAsync(CancellationToken ct);
    }

    /// <summary>M1 source: a manifest already referenced by the composition root. Always succeeds offline.</summary>
    public sealed class LocalCatalogSource : ICatalogSource
    {
        private readonly CatalogManifest _manifest;

        public LocalCatalogSource(CatalogManifest manifest)
        {
            _manifest = manifest;
        }

        public Task<Result<CatalogManifest>> LoadAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromResult(Result<CatalogManifest>.Fail(ErrorCodes.Cancelled));
            if (_manifest == null) return Task.FromResult(Result<CatalogManifest>.Fail(ErrorCodes.NotFound, "No CatalogManifest assigned."));
            return Task.FromResult(Result<CatalogManifest>.Ok(_manifest));
        }
    }
}

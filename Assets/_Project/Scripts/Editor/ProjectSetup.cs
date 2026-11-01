using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Skyrift.Editor
{
    /// <summary>
    /// Authors the render pipeline and the three quality tiers (TECH_DOC §2.1, §11.1).
    ///
    /// Unlike the content builders, this tool <b>always re-applies</b> its settings rather than respecting
    /// hand edits. Pipeline and tier values are contracts — §11.2 rule 6 says the layer matrix is authored
    /// once, and the same reasoning applies to the frame budget: a tier that silently drifts is how a
    /// 60 FPS target becomes a 40 FPS release. Re-running setup is therefore deterministic.
    ///
    /// Without this step every material in the project renders magenta, because a project with URP in the
    /// manifest but no pipeline assigned to Graphics Settings still uses the built-in pipeline.
    /// </summary>
    public static class ProjectSetup
    {
        public const string LowTier = "Low";
        public const string MediumTier = "Medium";
        public const string HighTier = "High";

        /// <summary>The tier a fresh project boots into. M6's device detector overrides this at runtime.</summary>
        public const int DefaultTierIndex = 1;

        /// <summary>
        /// One tier's authored budget. A struct rather than three parallel switch statements so a new tier
        /// is one row, and so the Low/Med/High differences are readable side by side in <see cref="Run"/>.
        /// </summary>
        private readonly struct TierSpec
        {
            public readonly string Name;
            public readonly float RenderScale;
            public readonly int Msaa;
            public readonly bool Hdr;
            public readonly ShadowQuality Shadows;
            public readonly float ShadowDistance;
            public readonly int ShadowCascades;
            public readonly int ShadowmapResolution;
            public readonly bool DepthTexture;
            public readonly bool SoftParticles;
            public readonly float LodBias;
            public readonly SkinWeights SkinWeights;
            public readonly int ParticleRaycastBudget;
            public readonly float StreamingMipmapBudgetMb;
            public readonly int AsyncUploadBufferSizeMb;
            public readonly int AsyncUploadTimeSliceMs;

            public TierSpec(string name, float renderScale, int msaa, bool hdr, ShadowQuality shadows,
                float shadowDistance, int shadowCascades, int shadowmapResolution, bool depthTexture,
                bool softParticles, float lodBias, SkinWeights skinWeights, int particleRaycastBudget,
                float streamingMipmapBudgetMb, int asyncUploadBufferSizeMb, int asyncUploadTimeSliceMs)
            {
                Name = name;
                RenderScale = renderScale;
                Msaa = msaa;
                Hdr = hdr;
                Shadows = shadows;
                ShadowDistance = shadowDistance;
                ShadowCascades = shadowCascades;
                ShadowmapResolution = shadowmapResolution;
                DepthTexture = depthTexture;
                SoftParticles = softParticles;
                LodBias = lodBias;
                SkinWeights = skinWeights;
                ParticleRaycastBudget = particleRaycastBudget;
                StreamingMipmapBudgetMb = streamingMipmapBudgetMb;
                AsyncUploadBufferSizeMb = asyncUploadBufferSizeMb;
                AsyncUploadTimeSliceMs = asyncUploadTimeSliceMs;
            }
        }

        /// <summary>
        /// The authored tiers, in quality-level order (Low, Medium, High). One table, read by both the
        /// pipeline builder and the quality builder, so the two can never disagree about a tier's budget.
        /// </summary>
        private static TierSpec[] Tiers()
        {
            // Low trades resolution and shadows for frame time on a 4 GB device; High is the headroom tier
            // for flagships. These are the M1 greybox baselines — M6 re-tunes them against device profiling
            // (§11.3), which is a content change, not a contract change.
            return new[]
            {
                new TierSpec(LowTier, 0.72f, 1, false, ShadowQuality.Disable,
                    0f, 1, 512, false, false, 0.6f, SkinWeights.TwoBones, 16, 96f, 4, 1),

                new TierSpec(MediumTier, 0.9f, 2, false, ShadowQuality.HardOnly,
                    45f, 1, 1024, true, true, 1f, SkinWeights.FourBones, 64, 192f, 8, 2),

                new TierSpec(HighTier, 1f, 4, true, ShadowQuality.All,
                    70f, 2, 2048, true, true, 1.4f, SkinWeights.Unlimited, 256, 320f, 16, 2)
            };
        }

        /// <summary>
        /// Builds the three URP assets, assigns them to Graphics Settings and to the matching quality tier,
        /// and leaves the project on the Medium tier.
        /// </summary>
        public static void Run()
        {
            SkyriftPaths.EnsureAllFolders();

            TierSpec[] tiers = Tiers();
            var pipelines = new UniversalRenderPipelineAsset[tiers.Length];
            for (int i = 0; i < tiers.Length; i++) pipelines[i] = BuildPipeline(tiers[i]);

            // The default pipeline is what the editor uses when no quality tier overrides it, and what a
            // player falls back to if a tier asset fails to load. Medium is the safe middle.
            GraphicsSettings.defaultRenderPipeline = pipelines[DefaultTierIndex];
            GraphicsSettings.useScriptableRenderPipelineBatching = true;

            // URP creates its own global-settings asset the first time a pipeline is instantiated. Assigning
            // the default above is what triggers that, so no explicit creation is needed here.
            BuildQualityTiers(tiers, pipelines);

            AssetAuthoring.SaveAll();
            Debug.Log("[Skyrift] Render pipeline and quality tiers authored (Low / Medium / High).");
        }

        /// <summary>
        /// Creates the renderer data and pipeline asset for one tier, then applies the tier's settings.
        /// The renderer asset is created once and never overwritten: it is where hand-added renderer
        /// features (outlines, decals, a custom blit) live, and clobbering it would delete them.
        /// </summary>
        private static UniversalRenderPipelineAsset BuildPipeline(in TierSpec spec)
        {
            string rendererPath = SkyriftPaths.SettingsUrp + "/GB_Renderer_" + spec.Name + ".asset";
            string pipelinePath = SkyriftPaths.SettingsUrp + "/GB_URP_" + spec.Name + ".asset";

            UniversalRendererData renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(rendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                // Saving first matters: UniversalRendererData is a [ReloadGroup] ISerializationCallbackReceiver,
                // so its shader and post-process references are resolved from the package during serialization.
                // Creating the pipeline from an unsaved instance would hand it a renderer with null shaders.
                AssetDatabase.CreateAsset(renderer, rendererPath);
            }

            UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(pipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(pipeline, pipelinePath);
            }

            ApplyPipelineSettings(pipeline, spec);
            return pipeline;
        }

        private static void ApplyPipelineSettings(UniversalRenderPipelineAsset pipeline, in TierSpec spec)
        {
            if (pipeline == null) return;

            pipeline.renderScale = spec.RenderScale;
            pipeline.msaaSampleCount = spec.Msaa;
            pipeline.supportsHDR = spec.Hdr;
            pipeline.shadowDistance = spec.ShadowDistance;
            pipeline.shadowCascadeCount = spec.ShadowCascades;
            pipeline.supportsCameraDepthTexture = spec.DepthTexture;
            // The opaque copy is only needed by distortion/refraction shaders, which the greybox has none of.
            // Leaving it on costs a full-screen colour copy every frame on a tile-based mobile GPU.
            pipeline.supportsCameraOpaqueTexture = false;
            pipeline.useSRPBatcher = true;
            pipeline.colorGradingMode = spec.Hdr ? ColorGradingMode.HighDynamicRange : ColorGradingMode.LowDynamicRange;

            // These three are get-only on the asset, so the serialized field is the only authoring route.
            // A null check rather than a blind write: if a future URP renames the field, the tool degrades to
            // a warning instead of throwing and aborting the whole setup.
            var serialized = new SerializedObject(pipeline);
            SetInt(serialized, "m_MainLightShadowmapResolution", spec.ShadowmapResolution);
            SetInt(serialized, "m_AdditionalLightsRenderingMode", (int)LightRenderingMode.PerPixel);
            SetInt(serialized, "m_MainLightRenderingMode", (int)LightRenderingMode.PerPixel);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(pipeline);
        }

        private static void SetInt(SerializedObject serialized, string property, int value)
        {
            SerializedProperty found = serialized.FindProperty(property);
            if (found == null)
            {
                Debug.LogWarning($"[Skyrift] URP asset has no '{property}'; left at its default. " +
                                 "Check the URP version if shadows or lighting look wrong.");
                return;
            }

            // The enum-backed fields are serialized as ints; writing integerValue covers both shapes.
            found.intValue = value;
        }

        /// <summary>
        /// Writes the three quality levels. <c>QualitySettings.names</c> is only reassigned when it actually
        /// differs, because the setter rebuilds every level from defaults — doing that on each run would
        /// discard the settings applied below on the previous run before they could be read back.
        /// </summary>
        private static void BuildQualityTiers(TierSpec[] specs, UniversalRenderPipelineAsset[] pipelines)
        {
            string[] wanted = { LowTier, MediumTier, HighTier };
            string[] current = QualitySettings.names;

            bool same = current != null && current.Length == wanted.Length;
            if (same)
            {
                for (int i = 0; i < wanted.Length; i++)
                {
                    if (current[i] != wanted[i]) { same = false; break; }
                }
            }

            if (!same) QualitySettings.names = wanted;

            for (int i = 0; i < specs.Length; i++)
            {
                // applyExpensiveChanges: false — this runs in the editor and must not trigger a device
                // quality change notification or a pipeline rebuild per tier.
                QualitySettings.SetQualityLevel(i, false);
                ApplyQualitySettings(pipelines[i], specs[i]);
            }

            QualitySettings.SetQualityLevel(DefaultTierIndex, false);
        }

        private static void ApplyQualitySettings(UniversalRenderPipelineAsset pipeline, in TierSpec spec)
        {
            QualitySettings.renderPipeline = pipeline;
            QualitySettings.shadows = spec.Shadows;
            QualitySettings.shadowResolution = spec.ShadowmapResolution >= 2048 ? ShadowResolution.VeryHigh
                : spec.ShadowmapResolution >= 1024 ? ShadowResolution.High : ShadowResolution.Medium;
            QualitySettings.shadowCascades = spec.ShadowCascades;
            QualitySettings.shadowDistance = spec.ShadowDistance;
            QualitySettings.shadowProjection = ShadowProjection.StableFit;
            QualitySettings.skinWeights = spec.SkinWeights;
            QualitySettings.lodBias = spec.LodBias;
            QualitySettings.particleRaycastBudget = spec.ParticleRaycastBudget;
            QualitySettings.softParticles = spec.SoftParticles;
            QualitySettings.softVegetation = false;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.billboardsFaceCameraPosition = false;
            QualitySettings.anisotropicFiltering = spec.Name == LowTier ? AnisotropicFiltering.Disable : AnisotropicFiltering.PerTexture;

            // URP takes MSAA from its own asset, but the built-in value is kept in step so the Quality
            // Settings window does not report a number that contradicts what the pipeline actually does.
            QualitySettings.antiAliasing = spec.Msaa;

            // vSync off: §11.1 targets 60 FPS mid-range and 30 FPS low-end, which is a frame-rate cap rather
            // than a display-sync decision. M6's device-tier selector owns Application.targetFrameRate;
            // leaving vSync at 1 would cap a 120 Hz panel at 120 and burn battery for no gameplay benefit.
            QualitySettings.vSyncCount = 0;

            // Streaming mipmaps are what actually hold resident texture memory inside §11.1's 300 MB budget.
            QualitySettings.streamingMipmapsActive = true;
            QualitySettings.streamingMipmapsMemoryBudget = spec.StreamingMipmapBudgetMb;
            QualitySettings.streamingMipmapsMaxLevelReduction = spec.Name == LowTier ? 3 : 2;
            QualitySettings.streamingMipmapsAddAllCameras = true;

            QualitySettings.asyncUploadTimeSlice = spec.AsyncUploadTimeSliceMs;
            QualitySettings.asyncUploadBufferSize = spec.AsyncUploadBufferSizeMb;
            QualitySettings.asyncUploadPersistentBuffer = true;
        }
    }
}
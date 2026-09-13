using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public enum TextureBudgetProfile : uint
    {
        Development = 0,
        HighQuality = 1,
        Cinematic = 2,
        Custom = 3
    }

    public enum RenderFeatureIsolationMode : uint
    {
        FullFrame = 0,
        Geometry = 1,
        Shadows = 2,
        PostProcessing = 3,
        Reflections = 4,
        Animation = 5,
        Particles = 6
    }

    public sealed class RenderDiagnosticsSettings
    {
        /// <summary>
        /// Opts into CPU tile-occupancy estimates on every frame. Runtime-only;
        /// the Light Tiles overlay also requests these statistics automatically.
        /// </summary>
        public bool TiledLightDiagnosticsEnabled { get; set; }

        public bool GpuMeshletCountersEnabled { get; set; }
        public bool DdgiForwardEstimateCountersEnabled { get; set; }
        public bool DirectionalShadowReceiverCountersEnabled { get; set; }

        /// <summary>
        /// Capture-only control for the disabled half of a paired forward-GI
        /// timing run. It suppresses receiver evaluation without disabling DDGI
        /// production, paging, transport, or reflection-capture state. Normal
        /// rendering and every quality preset leave this false.
        /// </summary>
        public bool SuppressForwardGiGatherForBenchmark { get; set; }

        /// <summary>
        /// Capture-only performance switch that forces the approximate
        /// screen-space DDGI receiver cache even when the active quality tier
        /// normally requires the exact per-fragment gather. Normal rendering
        /// and every quality preset leave this false.
        /// </summary>
        public bool ForceForwardGiReceiverCacheForBenchmark { get; set; }

        /// <summary>
        /// Capture-only quality oracle switch. It keeps DDGI enabled but
        /// bypasses the screen-space receiver cache so a settled frame can be
        /// compared against the exact per-fragment gather.
        /// Normal rendering and every quality preset leave this false.
        /// </summary>
        public bool ForceExactForwardGiGatherForBenchmark { get; set; }
    }

    public sealed class AsyncComputeSettings
    {
        public const AsyncComputePreferredPathMask DefaultPreferredPathMask =
            AsyncComputePreferredPathMask.SimpleDdgiUpdate |
            AsyncComputePreferredPathMask.FarFieldClipmapBake;

        /// <summary>
        /// Defaults to <see cref="AsyncComputeMode.Auto"/> for new installations. Auto schedules
        /// preferred production paths immediately after concrete queue/resource validation;
        /// other paths retain isolated timing promotion. Pre-v3 files without a Mode keep their
        /// legacy graphics-only behavior during settings migration.
        /// </summary>
        public AsyncComputeMode Mode { get; set; } = AsyncComputeMode.Auto;

        /// <summary>
        /// Paths that start on a compatible compute queue immediately after
        /// concrete resource-plan validation. Runtime timing may still demote
        /// them and explicit per-path booleans or Disabled mode remain opt-outs.
        /// </summary>
        public AsyncComputePreferredPathMask PreferredPathMask { get; set; } =
            DefaultPreferredPathMask;

        /// <summary>Atomic path explicitly authorized by a validation harness in Force mode.</summary>
        public AsyncComputePath? ForceValidationPath { get; set; }

        /// <summary>
        /// Compatibility shim for settings written before <see cref="Mode"/> existed.  Setting this
        /// to true is intentionally a validation request rather than a production auto-enable: an
        /// old explicit opt-in must never silently become a profitability decision.
        /// </summary>
        [Obsolete("Use Mode. Legacy true maps to ForceEnabledForValidation.")]
        public bool Enabled
        {
            get => Mode != AsyncComputeMode.Disabled;
            set => Mode = value
                ? AsyncComputeMode.ForceEnabledForValidation
                : AsyncComputeMode.Disabled;
        }

        public bool HiZBuildEnabled { get; set; } = true;
        public bool AmbientOcclusionBlurEnabled { get; set; } = true;
        public bool FogEnabled { get; set; } = true;
        public bool BloomEnabled { get; set; } = true;
        public bool SimpleDdgiUpdateEnabled { get; set; } = true;
        public bool FarFieldClipmapBakeEnabled { get; set; } = true;
        public bool GpuParticlesEnabled { get; set; } = true;

        /// <summary>Minimum samples retained for each graphics-only/async timing window.</summary>
        public int AutoMinimumSampleCount { get; set; } = 30;

        /// <summary>Minimum warm-up frames before Auto may promote a path.</summary>
        public int AutoWarmupFrameCount { get; set; } = 60;

        /// <summary>Absolute GPU frame-time benefit required by Auto, in milliseconds.</summary>
        public float AutoMinimumAbsoluteBenefitMilliseconds { get; set; } = 0.25f;

        /// <summary>Relative GPU frame-time benefit required by Auto.</summary>
        public float AutoMinimumRelativeBenefit { get; set; } = 0.03f;

        /// <summary>Frames a path remains in its current decision before Auto may flip it again.</summary>
        public int AutoDecisionCooldownFrames { get; set; } = 180;
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SimpleDdgiTracePass : SimpleDdgiComputePass
    {
        private const uint PrivateUrgentRelightFlag = 1u << 9;
        private readonly RenderSettings _traceSettings;
        private readonly FarFieldClipmapManager _traceFarFieldClipmapManager;
        private readonly AccelerationStructureManager _traceAccelerationStructureManager;
        private readonly SimpleDdgiLightTreeGpuResources _traceLightTreeResources;
        private readonly bool _directionalGuidingTransport;
        private SimpleDdgiTraceVariantSelection _selectedVariant =
            SimpleDdgiTraceVariantSelection.General64;
        private const string LegacySourceTraceShader = "ddgi_simple_trace_legacy_source.comp.spv";
        private const string LegacyReuseTraceShader = "ddgi_simple_trace_legacy_reuse.comp.spv";
        private const string LegacyFinalTraceShader = "ddgi_simple_trace_legacy_final.comp.spv";
        private const string ValidateSourceTraceShader = "ddgi_simple_trace_validate_source.comp.spv";
        private const string ValidateReuseTraceShader = "ddgi_simple_trace_validate_reuse.comp.spv";
        private const string ValidateFinalTraceShader = "ddgi_simple_trace_validate_final.comp.spv";
        private const string PackedSourceTraceShader = "ddgi_simple_trace_packed_source.comp.spv";
        private const string PackedReuseTraceShader = "ddgi_simple_trace_packed_reuse.comp.spv";
        private const string PackedFinalTraceShader = "ddgi_simple_trace_packed_final.comp.spv";
        private const string LegacyGuidedSourceTraceShader = "ddgi_simple_trace_legacy_guided_source.comp.spv";
        private const string LegacyGuidedReuseTraceShader = "ddgi_simple_trace_legacy_guided_reuse.comp.spv";
        private const string LegacyGuidedFinalTraceShader = "ddgi_simple_trace_legacy_guided_final.comp.spv";
        private const string ValidateGuidedSourceTraceShader = "ddgi_simple_trace_validate_guided_source.comp.spv";
        private const string ValidateGuidedReuseTraceShader = "ddgi_simple_trace_validate_guided_reuse.comp.spv";
        private const string ValidateGuidedFinalTraceShader = "ddgi_simple_trace_validate_guided_final.comp.spv";
        private const string PackedGuidedSourceTraceShader = "ddgi_simple_trace_packed_guided_source.comp.spv";
        private const string PackedGuidedReuseTraceShader = "ddgi_simple_trace_packed_guided_reuse.comp.spv";
        private const string PackedGuidedFinalTraceShader = "ddgi_simple_trace_packed_guided_final.comp.spv";

        public SimpleDdgiTracePass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            AccelerationStructureManager accelerationStructureManager,
            SimpleDdgiLightTreeGpuResources lightTreeResources,
            bool directionalGuidingTransport,
            GiPipelineCacheService? pipelineCacheService = null)
            : base("SimpleDdgiTracePass", ValidateSourceTraceShader, context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, accelerationStructureManager, requiresRayQuery: true, pipelineCacheService)
        {
            _traceSettings = settings;
            _traceFarFieldClipmapManager = farFieldClipmapManager;
            _traceAccelerationStructureManager = accelerationStructureManager;
            _traceLightTreeResources = lightTreeResources ??
                throw new ArgumentNullException(nameof(lightTreeResources));
            _directionalGuidingTransport = directionalGuidingTransport;
        }

        protected override SimpleDdgiLocalLightSamplingMode ResolveLocalLightSamplingMode(
            GlobalIlluminationSettings settings) =>
            _traceLightTreeResources.EffectiveSamplingMode;

        // The upper push-flag bits are a trace-private ABI. Cached solve and
        // blend dispatches reuse those bits for sweep/color control, so the
        // many-light payload must never be inherited by another DDGI pass.
        protected override bool UsesContentDependentLocalLightSamplingFlags =>
            true;

        protected override int PipelineDispatchCount => 3;
        // Trace does not consume the generic counter/update-record offsets in
        // the final two push words. Reuse those ABI slots for the scheduler
        // frame and typed dirty-region tables required by segment selection.
        protected override bool UsesTraceDirtyRegionOffsets => true;

        protected override void PrepareFramePipelines(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _traceSettings.GlobalIllumination;
            _selectedVariant = SimpleDdgiTraceVariantSelector.Select(
                new SimpleDdgiTraceContentFacts(
                    gi.SimpleDdgiStoragePackingMode,
                    RendererBuildFeatures.DetailedDdgiDiagnosticsCompiled,
                    _traceAccelerationStructureManager.RayQueryHasAlphaCandidateGeometry,
                    _traceAccelerationStructureManager.RayQueryHasThinTransmissionGeometry,
                    sceneData.DirectionalLightCount,
                    sceneData.LocalLightCount,
                    sceneData.DdgiEmissiveSourceCount,
                    gi.DdgiQualityTier is DdgiQualityTier.DdgiHigh or
                        DdgiQualityTier.DdgiUltra,
                    _traceFarFieldClipmapManager.CoverageReady));
            sceneData.SimpleDdgiTraceContentProfile =
                (int)_selectedVariant.ContentProfile;
            sceneData.SimpleDdgiTraceDistanceProfile =
                (int)_selectedVariant.DistanceProfile;
            sceneData.SimpleDdgiTraceSpecialized =
                _selectedVariant.Specialized ? 1 : 0;
            sceneData.SimpleDdgiTraceWorkgroupSize =
                _selectedVariant.WorkgroupSize;
        }

        protected override string ResolveShaderName(int dispatchIndex)
        {
            if ((uint)dispatchIndex >= 3u)
                throw new ArgumentOutOfRangeException(nameof(dispatchIndex));
            SimpleDdgiStoragePackingMode storageMode = _traceSettings
                .GlobalIllumination.SimpleDdgiStoragePackingMode.Sanitize();
            if (_directionalGuidingTransport)
            {
                return ResolveDirectionalGuidingShaderName(
                    storageMode,
                    dispatchIndex);
            }
            bool reuse = dispatchIndex == 0;
            bool final = dispatchIndex == 2;
            return storageMode switch
            {
                SimpleDdgiStoragePackingMode.Legacy => reuse
                    ? LegacyReuseTraceShader
                    : final
                        ? LegacyFinalTraceShader
                        : LegacySourceTraceShader,
                SimpleDdgiStoragePackingMode.Packed => reuse
                    ? PackedReuseTraceShader
                    : ResolvePackedTraceShader(final),
                _ => reuse
                    ? ValidateReuseTraceShader
                    : final
                        ? ValidateFinalTraceShader
                        : ValidateSourceTraceShader
            };
        }

        internal static string ResolveDirectionalGuidingShaderName(
            SimpleDdgiStoragePackingMode storageMode,
            int dispatchIndex)
        {
            if ((uint)dispatchIndex >= 3u)
                throw new ArgumentOutOfRangeException(nameof(dispatchIndex));
            bool reuse = dispatchIndex == 0;
            bool final = dispatchIndex == 2;
            return storageMode.Sanitize() switch
            {
                SimpleDdgiStoragePackingMode.Legacy => reuse
                    ? LegacyGuidedReuseTraceShader
                    : final
                        ? LegacyGuidedFinalTraceShader
                        : LegacyGuidedSourceTraceShader,
                SimpleDdgiStoragePackingMode.Packed => reuse
                    ? PackedGuidedReuseTraceShader
                    : final
                        ? PackedGuidedFinalTraceShader
                        : PackedGuidedSourceTraceShader,
                _ => reuse
                    ? ValidateGuidedReuseTraceShader
                    : final
                        ? ValidateGuidedFinalTraceShader
                        : ValidateGuidedSourceTraceShader
            };
        }

        private string ResolvePackedTraceShader(bool final)
        {
            string stem = SimpleDdgiTraceVariantSelector.ResolveShaderStem(
                _selectedVariant);
            if (stem == "packed")
                return final ? PackedFinalTraceShader : PackedSourceTraceShader;
            return $"ddgi_simple_trace_{stem}_{(final ? "final" : "source")}.comp.spv";
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            ulong rayCount = checked((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) * (ulong)Math.Max(1, VolumeManager.RaysPerProbe));
            return checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL));
        }

        /// <summary>
        /// Records only the cache-reuse role. Its SPIR-V declares no ray-query
        /// instruction or acceleration-structure descriptor, so the urgent
        /// pre-forward lane cannot acquire a dependency on current-frame BLAS
        /// or TLAS construction. The private flag also suppresses canonical
        /// source-cache radiance writes until the ordinary transaction runs.
        /// </summary>
        public void ExecuteCacheReuseOnly(
            CommandBuffer cmd,
            SceneRenderingData sceneData)
        {
            PrepareFramePipelines(sceneData);
            ExecutePipelineDispatch(
                cmd,
                sceneData,
                dispatchIndex: 0,
                bindAccelerationStructure: false,
                additionalFlags: PrivateUrgentRelightFlag);
        }

        protected override void Dispatch(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            GPUSimpleDdgiPushConstants pushConstants)
        {
            if (IsGpuResidentMode)
            {
                for (int bucket = 0; bucket < SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount; bucket++)
                {
                    pushConstants.SchedulerRayBucketIndex = checked((uint)bucket);
                    pushConstants.DispatchQueueOffset = 0u;
                    pushConstants.DispatchProbeCount = 0u;
                    pushConstants.DispatchRaysPerProbe = 0u;
                    PushConstantsAndDispatchRayBucket(cmd, pushConstants, bucket);
                }
                return;
            }

            ReadOnlySpan<SimpleDdgiRayDispatchBatch> batches =
                VolumeManager.RayDispatchBatches;
            if (batches.IsEmpty)
            {
                base.Dispatch(cmd, sceneData, pushConstants);
                return;
            }

            foreach (ref readonly SimpleDdgiRayDispatchBatch batch in batches)
            {
                pushConstants.DispatchQueueOffset = checked((uint)batch.QueueOffset);
                pushConstants.DispatchProbeCount = checked((uint)batch.ProbeCount);
                pushConstants.DispatchRaysPerProbe = checked((uint)batch.RaysPerProbe);
                ulong rayCount = checked(
                    (ulong)batch.ProbeCount * (ulong)batch.RaysPerProbe);
                PushConstantsAndDispatch(
                    cmd,
                    pushConstants,
                    checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL)));
            }
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (IsGpuResidentMode)
                return base.ShouldExecute(frameIndex, sceneData);

            // The trace is the transaction producer.  If the ray-query resource is
            // unavailable, invalidate this frame's transaction before relocation or
            // blending have a chance to observe an older scratch allocation.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanExecuteTraceTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.TraceUnavailable);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            base.Execute(cmd, frameIndex, sceneData);
            if (!IsGpuResidentMode)
                VolumeManager.MarkTraceExecuted();
        }
    }

    public sealed unsafe class SimpleDdgiBlendPass : SimpleDdgiComputePass
    {
        private const string BaselineShader = "ddgi_simple_blend.comp.spv";
        private const string DirectionalGuidingShader =
            "ddgi_simple_blend_guided.comp.spv";

        public SimpleDdgiBlendPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            bool directionalGuidingTransport = false,
            GiPipelineCacheService? pipelineCacheService = null)
            : base(
                "SimpleDdgiBlendPass",
                ResolveDirectionalGuidingShaderName(
                    directionalGuidingTransport),
                context,
                swapchain,
                bindlessHeap,
                settings,
                volumeManager,
                farFieldClipmapManager,
                null,
                requiresRayQuery: false,
                pipelineCacheService)
        {
        }

        internal static string ResolveDirectionalGuidingShaderName(
            bool directionalGuidingTransport) =>
            directionalGuidingTransport
                ? DirectionalGuidingShader
                : BaselineShader;

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            return checked((uint)Math.Max(1, VolumeManager.ProbesToUpdate));
        }

        protected override SimpleDdgiSchedulerDispatchSlot ResidentDispatchSlot =>
            SimpleDdgiSchedulerDispatchSlot.Blend;

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (VolumeManager.TransportTailAuditPending)
                return false;

            if (VolumeManager.TransportAccelerationSolveActive)
            {
                // The accelerated pass records transport and blend as one
                // ordered cached-sweep transaction.  The legacy blend pass must
                // not consume or publish a partially completed sweep.
                return false;
            }

            if (IsGpuResidentMode)
                return base.ShouldExecute(frameIndex, sceneData);

            // A planner evaluates all three predicates before trace is recorded.
            // Use the schedule-time gate here, then require the strict producer
            // chain immediately before recording the actual consumer dispatch.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleBlendTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.BlendPrerequisite);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (IsGpuResidentMode)
            {
                base.Execute(cmd, frameIndex, sceneData);
                // Source-repair blends are publication work, not cached solver
                // iterations. Counting them hid the actual solve-epoch cost in
                // qualification evidence. Preserve the legacy counter outside
                // the certified V2 state machine.
                bool certifiedV2 = VolumeManager.TransportV2Active &&
                    VolumeManager.TailCertificationEnabled;
                if (!certifiedV2 ||
                    VolumeManager.TransportTailPhase ==
                        SimpleDdgiTransportPhase.AcceleratedSolve)
                {
                    sceneData.SimpleDdgiTransportCachedSweepCount = checked(
                        sceneData.SimpleDdgiTransportCachedSweepCount + 1);
                }
                return;
            }

            if (!VolumeManager.CanExecuteBlendTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.BlendPrerequisite);
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkBlendExecuted();
        }
    }

    /// <summary>
    /// Resolves one explicit recursive DDGI transport iteration from cached
    /// source rays and the last published irradiance field.  It deliberately has
    /// no ray-query dependency: direct/sky/emissive source work remains in the
    /// trace producer and is reused until a source generation changes.
    /// </summary>
    public sealed unsafe class SimpleDdgiTransportPass : SimpleDdgiComputePass
    {
        private const string LegacyShader = "ddgi_simple_transport_legacy.comp.spv";
        private const string ValidateShader = "ddgi_simple_transport_validate.comp.spv";
        private const string PackedShader = "ddgi_simple_transport_packed.comp.spv";
        private const string LegacyGuidedShader =
            "ddgi_simple_transport_guided_legacy.comp.spv";
        private const string ValidateGuidedShader =
            "ddgi_simple_transport_guided_validate.comp.spv";
        private const string PackedGuidedShader =
            "ddgi_simple_transport_guided_packed.comp.spv";
        private readonly RenderSettings _transportSettings;
        private readonly bool _directionalGuidingTransport;

        public SimpleDdgiTransportPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            bool directionalGuidingTransport = false,
            GiPipelineCacheService? pipelineCacheService = null)
            : base("SimpleDdgiTransportPass", "ddgi_simple_transport.comp.spv", context, swapchain, bindlessHeap, settings, volumeManager, farFieldClipmapManager, null, requiresRayQuery: false, pipelineCacheService)
        {
            _transportSettings = settings;
            _directionalGuidingTransport = directionalGuidingTransport;
        }

        protected override string ResolveShaderName(int dispatchIndex)
        {
            if (dispatchIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(dispatchIndex));
            return ResolveTransportShaderName(
                _transportSettings.GlobalIllumination
                    .SimpleDdgiStoragePackingMode.Sanitize(),
                _directionalGuidingTransport);
        }

        internal static string ResolveTransportShaderName(
            SimpleDdgiStoragePackingMode storageMode,
            bool directionalGuidingTransport)
        {
            return (storageMode.Sanitize(), directionalGuidingTransport) switch
            {
                (SimpleDdgiStoragePackingMode.Legacy, false) => LegacyShader,
                (SimpleDdgiStoragePackingMode.Packed, false) => PackedShader,
                (SimpleDdgiStoragePackingMode.Legacy, true) => LegacyGuidedShader,
                (SimpleDdgiStoragePackingMode.Packed, true) => PackedGuidedShader,
                (_, true) => ValidateGuidedShader,
                _ => ValidateShader
            };
        }

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            ulong rayCount = checked((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) * (ulong)Math.Max(1, VolumeManager.RaysPerProbe));
            return checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL));
        }

        protected override void Dispatch(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            GPUSimpleDdgiPushConstants pushConstants)
        {
            if (IsGpuResidentMode)
            {
                for (int bucket = 0; bucket < SimpleDdgiGpuSchedulerLayout.MaxRayBucketCount; bucket++)
                {
                    pushConstants.SchedulerRayBucketIndex = checked((uint)bucket);
                    pushConstants.DispatchQueueOffset = 0u;
                    pushConstants.DispatchProbeCount = 0u;
                    pushConstants.DispatchRaysPerProbe = 0u;
                    PushConstantsAndDispatchRayBucket(cmd, pushConstants, bucket);
                }
                return;
            }

            ReadOnlySpan<SimpleDdgiRayDispatchBatch> batches =
                VolumeManager.RayDispatchBatches;
            if (batches.IsEmpty)
            {
                base.Dispatch(cmd, sceneData, pushConstants);
                return;
            }

            foreach (ref readonly SimpleDdgiRayDispatchBatch batch in batches)
            {
                pushConstants.DispatchQueueOffset = checked((uint)batch.QueueOffset);
                pushConstants.DispatchProbeCount = checked((uint)batch.ProbeCount);
                pushConstants.DispatchRaysPerProbe = checked((uint)batch.RaysPerProbe);
                ulong rayCount = checked(
                    (ulong)batch.ProbeCount * (ulong)batch.RaysPerProbe);
                PushConstantsAndDispatch(
                    cmd,
                    pushConstants,
                    checked((uint)Math.Max(1UL, (rayCount + 63UL) / 64UL)));
            }
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (VolumeManager.TransportTailAuditPending)
                return false;

            if (VolumeManager.TransportAccelerationSolveActive)
            {
                // V2 acceleration owns the complete cached-source transaction,
                // including every intermediate blend.  Leaving this pass
                // schedulable would execute a second transport producer against
                // the same private target.
                return false;
            }

            // V1 has no standalone transport pass. Do not abort its valid
            // trace/relocate/blend transaction when the compatibility path is
            // intentionally selected.
            if (!VolumeManager.TransportV2Active)
                return false;
            if (IsGpuResidentMode)
                return base.ShouldExecute(frameIndex, sceneData);
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleTransportTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.TransportPrerequisite);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (IsGpuResidentMode)
            {
                base.Execute(cmd, frameIndex, sceneData);
                return;
            }

            if (!VolumeManager.CanExecuteTransportTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.TransportPrerequisite);
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            sceneData.SimpleDdgiTransportCachedSweepCount = checked(
                sceneData.SimpleDdgiTransportCachedSweepCount + 1);
            VolumeManager.MarkTransportExecuted();
        }
    }

    public sealed unsafe class SimpleDdgiRelocateClassifyPass : SimpleDdgiComputePass
    {
        private const string BaselineShader =
            "ddgi_simple_relocate_classify.comp.spv";
        private const string DirectionalGuidingShader =
            "ddgi_simple_relocate_classify_guided.comp.spv";

        public SimpleDdgiRelocateClassifyPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            bool directionalGuidingTransport = false,
            GiPipelineCacheService? pipelineCacheService = null)
            : base(
                "SimpleDdgiRelocateClassifyPass",
                ResolveDirectionalGuidingShaderName(
                    directionalGuidingTransport),
                context,
                swapchain,
                bindlessHeap,
                settings,
                volumeManager,
                farFieldClipmapManager,
                null,
                requiresRayQuery: false,
                pipelineCacheService)
        {
        }

        internal static string ResolveDirectionalGuidingShaderName(
            bool directionalGuidingTransport) =>
            directionalGuidingTransport
                ? DirectionalGuidingShader
                : BaselineShader;

        protected override uint CalculateGroupCount(SceneRenderingData sceneData)
        {
            return checked((uint)Math.Max(1UL, ((ulong)Math.Max(0, VolumeManager.ProbesToUpdate) + 63UL) / 64UL));
        }

        protected override SimpleDdgiSchedulerDispatchSlot ResidentDispatchSlot =>
            SimpleDdgiSchedulerDispatchSlot.Relocate;

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            if (IsGpuResidentMode)
                return base.ShouldExecute(frameIndex, sceneData);

            // See SimpleDdgiBlendPass: this must stay schedulable beside trace for
            // async planning, but it may only record after this transaction's trace.
            if (!base.ShouldExecute(frameIndex, sceneData) || !VolumeManager.CanScheduleRelocateClassifyTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.RelocatePrerequisite);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (IsGpuResidentMode)
            {
                base.Execute(cmd, frameIndex, sceneData);
                return;
            }

            if (!VolumeManager.CanExecuteRelocateClassifyTransaction)
            {
                VolumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.RelocatePrerequisite);
                return;
            }

            base.Execute(cmd, frameIndex, sceneData);
            VolumeManager.MarkRelocateClassifyExecuted();
        }

    }

    /// <summary>
    /// Audits a frozen canonical V2 field using cached source rays only. One
    /// two-stage sequence covers a bounded contiguous probe chunk. A transfer
    /// clear initializes fail-closed status words, one invocation per cached
    /// ray validates identity and evaluates the frozen operator into bounded
    /// scratch, and the final dispatch reduces those results against the
    /// canonical field. The compact summary remains resident until the final
    /// chunk is copied to a delayed readback slot.
    /// </summary>
    public sealed unsafe class SimpleDdgiTransportAuditPass : RenderPassBase
    {
        private const string LegacyShader = "ddgi_simple_transport_audit_legacy.comp.spv";
        private const string ValidateShader = "ddgi_simple_transport_audit_validate.comp.spv";
        private const string PackedShader = "ddgi_simple_transport_audit_packed.comp.spv";
        private const string LegacyReduceShader = "ddgi_simple_transport_audit_reduce_legacy.comp.spv";
        private const string ValidateReduceShader = "ddgi_simple_transport_audit_reduce_validate.comp.spv";
        private const string PackedReduceShader = "ddgi_simple_transport_audit_reduce_packed.comp.spv";
        private readonly RenderSettings _settings;
        private readonly SimpleDdgiVolumeManager _volumeManager;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly nint _entryPointName;
        private readonly Dictionary<string, VkPipeline> _pipelines =
            new(StringComparer.Ordinal);
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _rayPipeline;
        private VkPipeline _reducePipeline;

        private enum AuditPipelineRole : byte
        {
            Rays,
            Reduce
        }

        public SimpleDdgiTransportAuditPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            GiPipelineCacheService? pipelineCacheService = null)
            : base("SimpleDdgiTransportAuditPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
            _pipelineCacheService = pipelineCacheService;
            _entryPointName = SilkMarshal.StringToPtr("main");
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute =>
            AsyncComputePassCatalog.IsCorrectnessCertified(AsyncComputePath.SimpleDdgiUpdate);
        public override string AsyncComputeReason =>
            "Frozen Simple DDGI transport audit reads cached source/canonical storage and writes a bounded summary.";

        public override void Initialize()
        {
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            string[] admittedShaders =
            [
                LegacyShader,
                ValidateShader,
                PackedShader,
                LegacyReduceShader,
                ValidateReduceShader,
                PackedReduceShader
            ];
            foreach (string shaderName in admittedShaders)
                _ = GetOrCreatePipeline(shaderName);
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (!_volumeManager.TransportV2Active ||
                !_volumeManager.TailCertificationEnabled ||
                _volumeManager.SchedulerMode != SimpleDdgiSchedulerMode.GpuResident ||
                !gi.EffectiveUseDdgi ||
                !gi.SimpleDdgiStructuredGatherEnabled ||
                !gi.EffectiveUseRayQueryBackend ||
                !_volumeManager.GpuSchedulerFrameExecutionAvailable ||
                !_volumeManager.GpuScheduler.IsReady ||
                _volumeManager.ProbeCount <= 0)
            {
                return false;
            }

            if (!_volumeManager.TransportTailAuditPending &&
                !_volumeManager.TryBeginTransportTailAudit())
            {
                return false;
            }

            if (!_volumeManager.TryGetTransportTailAuditChunk(out _))
                return false;

            _rayPipeline = GetOrCreatePipeline(AuditPipelineRole.Rays);
            _reducePipeline = GetOrCreatePipeline(AuditPipelineRole.Reduce);
            return _rayPipeline.Handle != 0 &&
                   _reducePipeline.Handle != 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (!_volumeManager.TryGetTransportTailAuditChunk(
                    out SimpleDdgiTransportAuditChunkDispatch dispatch))
            {
                return;
            }

            _rayPipeline = GetOrCreatePipeline(AuditPipelineRole.Rays);
            _reducePipeline = GetOrCreatePipeline(AuditPipelineRole.Reduce);

            SimpleDdgiGpuScheduler scheduler = _volumeManager.GpuScheduler;
            SimpleDdgiGpuSchedulerLayout layout = scheduler.Layout ??
                throw new InvalidOperationException("Simple DDGI audit requires a resident scheduler layout.");
            if (dispatch.ChunkIndex == 0u && dispatch.ProbeOffset == 0)
                scheduler.ResetTransportAuditSummary(cmd);
            if (!scheduler.ResetTransportAuditWorkspace(cmd))
            {
                _volumeManager.CancelTransportTailAudit(
                    SimpleDdgiTransportCertificationReason.GenerationsChanged);
                return;
            }

            GPUSimpleDdgiTransportAuditPushConstants pushConstants =
                CreatePushConstants(dispatch, layout);
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _rayPipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiTransportAuditPushConstants>(),
                &pushConstants);
            int dispatchRayCount = checked(
                dispatch.ProbeCount * _volumeManager.RaysPerProbe);
            uint rayGroupCount = SimpleDdgiGpuSchedulerLayout.GroupsFor(
                dispatchRayCount);
            _context.Api.CmdDispatch(cmd, rayGroupCount, 1, 1);
            InsertStorageBarrier(cmd);
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _reducePipeline);
            _context.Api.CmdDispatch(cmd, checked((uint)dispatch.ProbeCount), 1, 1);
            InsertStorageBarrier(cmd);
            sceneData.SimpleDdgiTransportAuditChunkCount = checked(
                sceneData.SimpleDdgiTransportAuditChunkCount + 1);

            if (!_volumeManager.MarkTransportTailAuditChunkSubmitted(dispatch))
            {
                _volumeManager.CancelTransportTailAudit(
                    SimpleDdgiTransportCertificationReason.GenerationsChanged);
                return;
            }

            if (dispatch.IsFinal)
            {
                scheduler.RecordTransportAuditReadback(
                    cmd,
                    frameIndex,
                    _volumeManager.FrameSerial,
                    dispatch.AuditEpoch);
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            foreach (VkPipeline pipeline in _pipelines.Values)
            {
                if (pipeline.Handle != 0)
                    _context.Api.DestroyPipeline(_context.Device, pipeline, null);
            }
            _pipelines.Clear();
            if (_pipelineLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            _rayPipeline = default;
            _reducePipeline = default;
            _pipelineLayout = default;
            _pipelineCache = default;
        }

        private GPUSimpleDdgiTransportAuditPushConstants CreatePushConstants(
            SimpleDdgiTransportAuditChunkDispatch dispatch,
            SimpleDdgiGpuSchedulerLayout layout)
        {
            SimpleDdgiTransportGenerations generations =
                _volumeManager.GetFrozenTransportTailGenerations();

            return new GPUSimpleDdgiTransportAuditPushConstants
            {
                ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
                RayResultScratchBufferIndex = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
                ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
                TransportSourceCacheBufferIndex = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
                TransportReadIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                TransportGeneration = _volumeManager.TransportGeneration,
                DispatchProbeCount = checked((uint)dispatch.ProbeCount),
                DispatchRaysPerProbe = checked((uint)_volumeManager.RaysPerProbe),
                SchedulerArenaBufferIndex = BindlessIndex.SimpleDdgiSchedulerArenaBuffer,
                AuditSummaryBufferIndex = BindlessIndex.SimpleDdgiSchedulerArenaBuffer,
                AuditSummaryBaseWord = layout.AuditSummary.OffsetWords,
                AuditProbeOffset = checked((uint)dispatch.ProbeOffset),
                AuditProbeCount = checked((uint)_volumeManager.ProbeCount),
                AuditExpectedParticipantCount = checked((uint)dispatch.ExpectedParticipantCount),
                AuditExpectedTexelCount = checked((uint)dispatch.ExpectedTexelCount),
                AuditChunkIndex = dispatch.ChunkIndex,
                AuditSchedulerFrameOffsetWords = layout.Frame.OffsetWords,
                AuditVolumeTableGeneration = generations.VolumeTable,
                AuditPhysicalOwnershipGeneration = generations.PhysicalOwnership,
                AuditSourceLightingGeneration = generations.SourceLighting,
                AuditSourceEpochGeneration = generations.SourceEpoch,
                AuditTransportOperatorGeneration = generations.TransportOperator,
                AuditCanonicalFieldGeneration = generations.CanonicalField,
                AuditSolveGeneration = generations.Solve,
                AuditEpochGeneration = generations.Audit,
                AuditQueueGeneration = generations.Queue,
                AuditSchedulerResourceGeneration = generations.SchedulerResources,
                AuditSchedulerProbeStateOffsetWords = layout.ProbeState.OffsetWords,
                AuditSolveEpoch = _volumeManager.TransportTailSolveEpoch,
                AuditWorkspaceBaseWord = layout.AuditWorkspace.OffsetWords,
                AuditWitnessProbeIndex =
                    _volumeManager.TransportAuditWitnessProbeIndex,
                AuditWitnessTexelIndex =
                    _volumeManager.TransportAuditWitnessTexelIndex
            };
        }

        private void InsertStorageBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit |
                               PipelineStageFlags2.TransferBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                                AccessFlags2.ShaderStorageWriteBit |
                                AccessFlags2.TransferReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI transport audit pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            _setLayouts =
            [
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout
            ];
            fixed (DescriptorSetLayout* layouts = _setLayouts)
            {
                var pushRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUSimpleDdgiTransportAuditPushConstants>()
                };
                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = layouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushRange
                };
                Result result = _context.Api.CreatePipelineLayout(
                    _context.Device,
                    &layoutInfo,
                    null,
                    out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create Simple DDGI transport audit pipeline layout", result);
            }
        }

        private VkPipeline GetOrCreatePipeline(AuditPipelineRole role)
        {
            string shaderName = _settings.GlobalIllumination
                .SimpleDdgiStoragePackingMode.Sanitize() switch
            {
                SimpleDdgiStoragePackingMode.Legacy => role switch
                {
                    AuditPipelineRole.Reduce => LegacyReduceShader,
                    _ => LegacyShader
                },
                SimpleDdgiStoragePackingMode.Packed => role switch
                {
                    AuditPipelineRole.Reduce => PackedReduceShader,
                    _ => PackedShader
                },
                _ => role switch
                {
                    AuditPipelineRole.Reduce => ValidateReduceShader,
                    _ => ValidateShader
                }
            };
            return GetOrCreatePipeline(shaderName);
        }

        private VkPipeline GetOrCreatePipeline(string shaderName)
        {
            if (_pipelines.TryGetValue(shaderName, out VkPipeline pipeline))
                return pipeline;
            pipeline = CreatePipeline(shaderName);
            _pipelines.Add(shaderName, pipeline);
            return pipeline;
        }

        private VkPipeline CreatePipeline(string shaderName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(
                    _context,
                    shaderName);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                    BasePipelineIndex = -1
                };
                long pipelineStart = _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateComputePipelines(
                        _context.Device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline);
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(
                        $"{Name}:{shaderName}",
                        pipelineStart);
                }
                if (result != Result.Success)
                    throw new VulkanException(
                        $"Failed to create Simple DDGI transport audit pipeline from '{shaderName}'",
                        result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }
    }

    /// <summary>
    /// Publishes the completed private Jacobi target directly from the GPU-visible
    /// update queue. The optional filtered image mirror is dual-written by a
    /// second compute dispatch; neither path needs CPU sorting or copy regions.
    /// </summary>
    public sealed unsafe class SimpleDdgiPublishPass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private readonly RenderSettings _settings;
        private readonly SimpleDdgiVolumeManager _volumeManager;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly nint _entryPointName;
        private DescriptorSetLayout _sampledAtlasSetLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _sampledAtlasSet;
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _canonicalPipeline;
        private VkPipeline _sampledPipeline;
        private ulong _boundSampledAtlasGeneration;
        private bool _sampledPublicationSupported;

        public SimpleDdgiPublishPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            GiPipelineCacheService? pipelineCacheService = null)
            : base("SimpleDdgiPublishPass", context, swapchain, bindlessHeap)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _volumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
            _pipelineCacheService = pipelineCacheService;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute =>
            AsyncComputePassCatalog.IsCorrectnessCertified(AsyncComputePath.SimpleDdgiUpdate);
        public override string AsyncComputeReason =>
            "Simple DDGI publication consumes the GPU queue and writes canonical probe storage.";

        public override void Initialize()
        {
            PhysicalDeviceProperties properties = default;
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            _sampledPublicationSupported =
                _context.ShaderStorageImageArrayNonUniformIndexingSupported &&
                properties.Limits.MaxPerStageDescriptorStorageImages >=
                2u * SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups;
            _volumeManager.SetSampledAtlasGpuPublicationAvailable(_sampledPublicationSupported);
            CreateSampledAtlasSetLayout();
            CreateDescriptorSet();
            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            _canonicalPipeline = CreatePipeline("ddgi_simple_publish.comp.spv");
            if (_sampledPublicationSupported)
                _sampledPipeline = CreatePipeline("ddgi_simple_publish_sampled.comp.spv");
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (_volumeManager.TransportTailAuditPending)
                return false;
            if (_volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident)
            {
                return _canonicalPipeline.Handle != 0 &&
                    gi.EffectiveUseDdgi &&
                    gi.SimpleDdgiStructuredGatherEnabled &&
                    gi.EffectiveUseRayQueryBackend &&
                    _volumeManager.GpuSchedulerFrameExecutionAvailable &&
                    _volumeManager.ProbeCount > 0 &&
                    _volumeManager.GpuScheduler.IsReady;
            }

            if (_canonicalPipeline.Handle == 0 ||
                !gi.EffectiveUseDdgi ||
                !gi.SimpleDdgiStructuredGatherEnabled ||
                !gi.EffectiveUseRayQueryBackend ||
                _volumeManager.ProbeCount <= 0 ||
                _volumeManager.ProbesToUpdate <= 0 ||
                !_volumeManager.CanSchedulePublishTransaction)
            {
                _volumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.PublishPrerequisite);
                return false;
            }

            return true;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            bool gpuResident = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident;
            if (!gpuResident && !_volumeManager.CanExecutePublishTransaction)
            {
                _volumeManager.AbortUpdateTransaction(
                    SimpleDdgiUpdateTransactionAbortReason.PublishPrerequisite);
                return;
            }

            ExecuteCanonicalOnly(cmd);
            ExecuteSampledOnly(cmd);

            // Capture state only after canonical and optional image publication
            // have been recorded. The transaction is completed at this point.
            if (!gpuResident)
            {
                _volumeManager.RecordProbeStateReadback(cmd, frameIndex);
                _volumeManager.MarkPublishExecuted();
            }
        }

        public void ExecuteCanonicalOnly(CommandBuffer cmd)
        {
            bool gpuResident = _volumeManager.SchedulerMode ==
                SimpleDdgiSchedulerMode.GpuResident;
            GPUSimpleDdgiPublishPushConstants pushConstants =
                CreatePushConstants();

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _canonicalPipeline);
            BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
            PushConstants(cmd, pushConstants);
            if (gpuResident)
            {
                InsertIndirectCommandReadBarrier(cmd);
                _context.Api.CmdDispatchIndirect(
                    cmd,
                    _volumeManager.GpuScheduler.GetArenaVkBuffer(),
                    _volumeManager.GpuScheduler.GetIndirectCommandOffset(
                        SimpleDdgiSchedulerDispatchSlot.Publish));
            }
            else
            {
                uint groupCount = checked((uint)Math.Max(1, _volumeManager.ProbesToUpdate));
                _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
            }
            InsertComputeStorageBarrier(cmd);
        }

        public void ExecuteSampledOnly(CommandBuffer cmd)
        {
            bool gpuResident = _volumeManager.SchedulerMode ==
                SimpleDdgiSchedulerMode.GpuResident;
            if (_sampledPublicationSupported &&
                _sampledPipeline.Handle != 0 &&
                _volumeManager.SampledAtlasActive)
            {
                GPUSimpleDdgiPublishPushConstants pushConstants =
                    CreatePushConstants();
                ulong allocationGeneration = _volumeManager.SampledAtlasAllocationGeneration;
                if (_boundSampledAtlasGeneration != allocationGeneration)
                {
                    _volumeManager.UpdateSampledAtlasGpuPublishDescriptors(_sampledAtlasSet);
                    _boundSampledAtlasGeneration = allocationGeneration;
                }

                _volumeManager.BeginSampledAtlasGpuPublication(cmd);
                _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _sampledPipeline);
                BindBindlessStorageAndTextures(cmd, _pipelineLayout, PipelineBindPoint.Compute);
                DescriptorSet sampledAtlasSet = _sampledAtlasSet;
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    2,
                    1,
                    &sampledAtlasSet,
                    0,
                    null);
                PushConstants(cmd, pushConstants);
                if (gpuResident)
                {
                    InsertIndirectCommandReadBarrier(cmd);
                    _context.Api.CmdDispatchIndirect(
                        cmd,
                        _volumeManager.GpuScheduler.GetArenaVkBuffer(),
                        _volumeManager.GpuScheduler.GetIndirectCommandOffset(
                            SimpleDdgiSchedulerDispatchSlot.Publish));
                }
                else
                {
                    uint groupCount = checked((uint)Math.Max(1, _volumeManager.ProbesToUpdate));
                    _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
                }
                _volumeManager.EndSampledAtlasGpuPublication(cmd);
            }
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            if (_canonicalPipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _canonicalPipeline, null);
            if (_sampledPipeline.Handle != 0)
                _context.Api.DestroyPipeline(_context.Device, _sampledPipeline, null);
            if (_pipelineLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_descriptorPool.Handle != 0)
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
            if (_sampledAtlasSetLayout.Handle != 0)
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _sampledAtlasSetLayout, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            _canonicalPipeline = default;
            _sampledPipeline = default;
            _pipelineLayout = default;
            _pipelineCache = default;
            _descriptorPool = default;
            _sampledAtlasSet = default;
            _sampledAtlasSetLayout = default;
        }

        private GPUSimpleDdgiPublishPushConstants CreatePushConstants() => new()
        {
            ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
            IrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            VisibilityAtlasBufferIndex = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
            ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
            ReceiverProbeBufferIndex = BindlessIndex.SimpleDdgiReceiverProbeBuffer,
            ProbeUpdateQueueBufferIndex = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            TransportIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer,
            PrivateVisibilityAtlasOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuSchedulerPrivateVisibilityOffsetWords
                : 0u,
            SampledAtlasGroupCount = checked((uint)Math.Max(0, _volumeManager.SampledAtlasGroupCount)),
            SampledAtlasLayersPerTexture = checked((uint)Math.Max(0, _volumeManager.SampledAtlasLayersPerTexture)),
            SchedulerArenaBufferIndex = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? checked((uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer)
                : uint.MaxValue,
            SchedulerOutcomesOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.Outcomes.OffsetWords
                : 0u,
            SchedulerCountersOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.Counters.OffsetWords
                : 0u,
            SchedulerFrameOffsetWords = _volumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? _volumeManager.GpuScheduler.Layout!.Frame.OffsetWords
                : 0u
        };

        private void PushConstants(CommandBuffer cmd, GPUSimpleDdgiPublishPushConstants pushConstants)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPublishPushConstants>(),
                &pushConstants);
        }

        private void CreateSampledAtlasSetLayout()
        {
            uint descriptorCount = _sampledPublicationSupported
                ? SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups
                : 1u;
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[2];
            bindings[0] = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            bindings[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount,
                StageFlags = ShaderStageFlags.ComputeBit
            };
            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = bindings
            };
            Result result = _context.Api.CreateDescriptorSetLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _sampledAtlasSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish image layout", result);
        }

        private void CreateDescriptorSet()
        {
            uint descriptorCount = _sampledPublicationSupported
                ? 2u * SimpleDdgiSampledAtlas.MaxGpuPublishTextureGroups
                : 2u;
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.StorageImage,
                DescriptorCount = descriptorCount
            };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1
            };
            Result result = _context.Api.CreateDescriptorPool(
                _context.Device,
                &poolInfo,
                null,
                out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish descriptor pool", result);

            DescriptorSetLayout layout = _sampledAtlasSetLayout;
            var allocationInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };
            result = _context.Api.AllocateDescriptorSets(
                _context.Device,
                &allocationInfo,
                out _sampledAtlasSet);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate Simple DDGI publish descriptor set", result);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[3]
            {
                _bindlessHeap.StorageBufferSetLayout,
                _bindlessHeap.TextureSamplerSetLayout,
                _sampledAtlasSetLayout
            };
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPublishPushConstants>()
            };
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 3,
                PSetLayouts = layouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };
            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _pipelineLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create Simple DDGI publish pipeline layout", result);
        }

        private VkPipeline CreatePipeline(string shaderName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                    BasePipelineIndex = -1
                };
                long pipelineStart = _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateComputePipelines(
                        _context.Device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline);
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(
                        $"{Name}:{shaderName}",
                        pipelineStart);
                }
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create Simple DDGI publish pipeline '{shaderName}'", result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void InsertComputeStorageBarrier(CommandBuffer cmd)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                                AccessFlags2.ShaderStorageWriteBit
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }

        private void InsertIndirectCommandReadBarrier(CommandBuffer cmd)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.DrawIndirectBit,
                DstAccessMask = AccessFlags2.IndirectCommandReadBit
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    public abstract unsafe class SimpleDdgiComputePass : RenderPassBase
    {
        private const string EntryPoint = "main";
        private const uint EnabledFlag = 1u << 0;
        private const uint FarFieldEnabledFlag = 1u << 1;
        private const uint FarFieldForceAllFlag = 1u << 2;
        private const uint SharedMemoryBlendEnabledFlag = 1u << 3;
        private const uint ClassificationSchedulingEnabledFlag = 1u << 4;
        private const uint ReducedBlendEnabledFlag = 1u << 5;
        private const uint CompleteRaySceneFlag = 1u << 6;
        private const uint AlphaMaskTransportEnabledFlag = 1u << 7;
        private const uint ThinSurfaceTransmissionEnabledFlag = 1u << 8;
        // Bits 10..31 are a frozen trace-only extension of the existing push
        // word. No push-constant growth is required (the ABI is already above
        // Vulkan's guaranteed 128-byte minimum on qualified devices).
        private const int LocalLightSamplingModeShift = 10;
        private const int ExactLocalLightThresholdShift = 12;
        private const int UniformMixtureShift = 23;
        private const uint ContentDependentLocalLightSamplingEnabledFlag = 1u << 31;
        private const int TransparencyCandidateLimitShift = 4;
        private const int TransparencyLayerLimitShift = 12;
        private const int DecalCandidateLimitShift = 18;

        private readonly string _shaderName;
        private readonly RenderSettings _settings;
        private readonly FarFieldClipmapManager _farFieldClipmapManager;
        private readonly AccelerationStructureManager? _accelerationStructureManager;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly bool _requiresRayQuery;
        private readonly nint _entryPointName;
        private readonly Dictionary<string, VkPipeline> _pipelines =
            new(StringComparer.Ordinal);
        private DescriptorSetLayout _accelerationStructureSetLayout;
        private DescriptorPool _descriptorPool;
        // The TLAS may rotate with the renderer's frame slot. Descriptor sets
        // are externally synchronized objects and cannot be updated while a
        // previously submitted command buffer still uses them, so retain one
        // binding (and one binding cache) per in-flight frame.
        private readonly DescriptorSet[] _accelerationStructureSets =
            new DescriptorSet[RenderingConstants.FramesInFlight];
        private DescriptorSetLayout[] _setLayouts = Array.Empty<DescriptorSetLayout>();
        private PipelineLayout _pipelineLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _pipeline;
        private readonly AccelerationStructureKHR[] _boundTlases =
            new AccelerationStructureKHR[RenderingConstants.FramesInFlight];

        protected SimpleDdgiComputePass(
            string passName,
            string shaderName,
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            RenderSettings settings,
            SimpleDdgiVolumeManager volumeManager,
            FarFieldClipmapManager farFieldClipmapManager,
            AccelerationStructureManager? accelerationStructureManager,
            bool requiresRayQuery,
            GiPipelineCacheService? pipelineCacheService = null)
            : base(passName, context, swapchain, bindlessHeap)
        {
            _shaderName = shaderName ?? throw new ArgumentNullException(nameof(shaderName));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            VolumeManager = volumeManager ?? throw new ArgumentNullException(nameof(volumeManager));
            _farFieldClipmapManager = farFieldClipmapManager ?? throw new ArgumentNullException(nameof(farFieldClipmapManager));
            _accelerationStructureManager = accelerationStructureManager;
            _requiresRayQuery = requiresRayQuery;
            _pipelineCacheService = pipelineCacheService;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
        }

        protected SimpleDdgiVolumeManager VolumeManager { get; }
        protected bool IsGpuResidentMode =>
            VolumeManager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident;
        protected virtual SimpleDdgiSchedulerDispatchSlot ResidentDispatchSlot =>
            SimpleDdgiSchedulerDispatchSlot.Blend;
        protected virtual bool UsesTraceDirtyRegionOffsets => false;
        protected virtual bool UsesContentDependentLocalLightSamplingFlags =>
            false;
        protected virtual SimpleDdgiLocalLightSamplingMode ResolveLocalLightSamplingMode(
            GlobalIlluminationSettings settings) =>
            settings.SimpleDdgiLocalLightSamplingMode;
        public override bool SupportsSecondaryCommandBuffer => true;
        public override RenderGraphQueueIntent QueueIntent => RenderGraphQueueIntent.Compute;
        public override bool SupportsAsyncCompute =>
            AsyncComputePassCatalog.IsCorrectnessCertified(AsyncComputePath.SimpleDdgiUpdate);
        public override string AsyncComputeReason => "Simple DDGI update work is compute-only and writes probe buffers.";

        public override void Initialize()
        {
            if (_requiresRayQuery && (!_context.RayQuerySupported || _context.KhrAccelerationStructure == null))
                return;

            if (_requiresRayQuery)
            {
                CreateAccelerationStructureSetLayout();
                CreateDescriptorSet();
            }

            if (_pipelineCacheService != null)
                _pipelineCache = _pipelineCacheService.Cache;
            else
                CreatePipelineCache();
            CreatePipelineLayout();
            if (DeferPipelineCreationUntilExecution)
                return;
            foreach (string shaderName in ResolvePrewarmShaderNames())
            {
                _pipeline = GetOrCreatePipeline(shaderName);
            }
            _pipeline = GetOrCreatePipeline(0);
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            PrepareFramePipelines(sceneData);
            if (_pipelineLayout.Handle != 0)
            {
                for (int dispatchIndex = 0;
                     dispatchIndex < PipelineDispatchCount;
                     dispatchIndex++)
                {
                    _pipeline = GetOrCreatePipeline(dispatchIndex);
                }
                _pipeline = GetOrCreatePipeline(0);
            }
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (_pipeline.Handle == 0)
                return false;
            if (VolumeManager.TransportTailAuditPending)
                return false;
            if (IsGpuResidentMode && !VolumeManager.GpuSchedulerFrameExecutionAvailable)
                return false;
            if (!gi.EffectiveUseDdgi ||
                !gi.SimpleDdgiStructuredGatherEnabled ||
                !gi.EffectiveUseRayQueryBackend)
                return false;
            if (_requiresRayQuery && (_accelerationStructureManager?.Active != true))
                return false;
            if (IsGpuResidentMode)
                return VolumeManager.ProbeCount > 0 && VolumeManager.GpuScheduler.IsReady;
            return VolumeManager.ProbeCount > 0 && VolumeManager.ProbesToUpdate > 0;
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            if (_requiresRayQuery)
                UpdateAccelerationStructureDescriptor(sceneData);

            for (int dispatchIndex = 0;
                 dispatchIndex < PipelineDispatchCount;
                 dispatchIndex++)
            {
                ExecutePipelineDispatch(
                    cmd,
                    sceneData,
                    dispatchIndex,
                    bindAccelerationStructure: _requiresRayQuery,
                    additionalFlags: 0u);
            }
        }

        protected void ExecutePipelineDispatch(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            int dispatchIndex,
            bool bindAccelerationStructure,
            uint additionalFlags)
        {
            if ((uint)dispatchIndex >= (uint)PipelineDispatchCount)
                throw new ArgumentOutOfRangeException(nameof(dispatchIndex));
            if (bindAccelerationStructure && !_requiresRayQuery)
            {
                throw new InvalidOperationException(
                    $"{Name} cannot bind an acceleration structure for a non-ray-query pipeline.");
            }

            GPUSimpleDdgiPushConstants pushConstants =
                CreatePushConstants(sceneData);
            pushConstants.Flags |= additionalFlags;
            _pipeline = GetOrCreatePipeline(dispatchIndex);
            _context.Api.CmdBindPipeline(
                cmd,
                PipelineBindPoint.Compute,
                _pipeline);
            BindBindlessStorageAndTextures(
                cmd,
                _pipelineLayout,
                PipelineBindPoint.Compute);

            if (bindAccelerationStructure)
            {
                int descriptorFrameSlot = ResolveDescriptorFrameSlot(sceneData);
                var asSet = _accelerationStructureSets[descriptorFrameSlot];
                _context.Api.CmdBindDescriptorSets(
                    cmd,
                    PipelineBindPoint.Compute,
                    _pipelineLayout,
                    2,
                    1,
                    &asSet,
                    0,
                    null);
            }

            Dispatch(cmd, sceneData, pushConstants);
            InsertWriteBarrier(cmd);
        }

        protected virtual void Dispatch(
            CommandBuffer cmd,
            SceneRenderingData sceneData,
            GPUSimpleDdgiPushConstants pushConstants)
        {
            if (IsGpuResidentMode)
            {
                PushConstantsAndDispatchIndirect(cmd, pushConstants, ResidentDispatchSlot);
                return;
            }

            PushConstantsAndDispatch(cmd, pushConstants, CalculateGroupCount(sceneData));
        }

        protected void PushConstantsAndDispatch(
            CommandBuffer cmd,
            GPUSimpleDdgiPushConstants pushConstants,
            uint groupCount)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>(),
                &pushConstants);
            _context.Api.CmdDispatch(cmd, groupCount, 1, 1);
        }

        protected void PushConstantsAndDispatchIndirect(
            CommandBuffer cmd,
            GPUSimpleDdgiPushConstants pushConstants,
            SimpleDdgiSchedulerDispatchSlot slot)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>(),
                &pushConstants);
            InsertIndirectCommandReadBarrier(cmd);
            _context.Api.CmdDispatchIndirect(
                cmd,
                VolumeManager.GpuScheduler.GetArenaVkBuffer(),
                VolumeManager.GpuScheduler.GetIndirectCommandOffset(slot));
        }

        protected void PushConstantsAndDispatchRayBucket(
            CommandBuffer cmd,
            GPUSimpleDdgiPushConstants pushConstants,
            int bucketIndex)
        {
            _context.Api.CmdPushConstants(
                cmd,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>(),
                &pushConstants);
            InsertIndirectCommandReadBarrier(cmd);
            _context.Api.CmdDispatchIndirect(
                cmd,
                VolumeManager.GpuScheduler.GetArenaVkBuffer(),
                VolumeManager.GpuScheduler.GetRayBucketCommandOffset(bucketIndex));
        }

        public override IEnumerable<DependencyInfo> GetBarriers(int frameIndex)
        {
            yield break;
        }

        public override void Cleanup()
        {
            foreach (VkPipeline pipeline in _pipelines.Values)
            {
                if (pipeline.Handle != 0)
                    _context.Api.DestroyPipeline(_context.Device, pipeline, null);
            }
            _pipelines.Clear();
            _pipeline = default;

            if (_descriptorPool.Handle != 0)
            {
                _context.Api.DestroyDescriptorPool(_context.Device, _descriptorPool, null);
                _descriptorPool = default;
                Array.Clear(_accelerationStructureSets);
                Array.Clear(_boundTlases);
            }

            if (_pipelineLayout.Handle != 0)
            {
                _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
                _pipelineLayout = default;
            }

            if (_accelerationStructureSetLayout.Handle != 0)
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, _accelerationStructureSetLayout, null);
                _accelerationStructureSetLayout = default;
            }

            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
            {
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
                _pipelineCache = default;
            }

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
        }

        protected abstract uint CalculateGroupCount(SceneRenderingData sceneData);

        protected virtual int PipelineDispatchCount => 1;
        protected virtual bool DeferPipelineCreationUntilExecution => false;

        protected virtual void PrepareFramePipelines(SceneRenderingData sceneData)
        {
        }

        /// <summary>
        /// Enumerates only pipelines reachable by the current immutable layout
        /// and dispatch selection. Alternative storage layouts and trace
        /// specializations are intentionally created on first selection and
        /// retained in <see cref="_pipelines"/>. Eagerly creating every compiled
        /// variant both inflated startup substantially and allowed an unused
        /// driver-rejected shader to disable the otherwise valid active path.
        /// </summary>
        protected virtual IEnumerable<string> ResolvePrewarmShaderNames()
        {
            for (int dispatchIndex = 0;
                 dispatchIndex < PipelineDispatchCount;
                 dispatchIndex++)
            {
                yield return ResolveShaderName(dispatchIndex);
            }
        }

        protected virtual string ResolveShaderName(int dispatchIndex)
        {
            if (dispatchIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(dispatchIndex));
            return _shaderName;
        }

        protected GPUSimpleDdgiPushConstants CreatePushConstants(SceneRenderingData sceneData)
        {
            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            uint flags = EnabledFlag;
            if (_farFieldClipmapManager.CoverageReady)
                flags |= FarFieldEnabledFlag;
            if (_farFieldClipmapManager.CoverageReady && gi.FarFieldForceAll)
                flags |= FarFieldForceAllFlag;
            if (gi.SimpleDdgiSharedMemoryBlendEnabled)
                flags |= SharedMemoryBlendEnabledFlag;
            if (gi.SimpleDdgiClassificationSchedulingEnabled)
                flags |= ClassificationSchedulingEnabledFlag;
            if (gi.SimpleDdgiReducedBlendEnabled)
                flags |= ReducedBlendEnabledFlag;
            if (gi.DdgiQualityTier is DdgiQualityTier.DdgiHigh or DdgiQualityTier.DdgiUltra)
                flags |= CompleteRaySceneFlag;
            if (gi.DdgiAlphaMaskedTransportEnabled)
                flags |= AlphaMaskTransportEnabledFlag;
            if (gi.SimpleDdgiThinSurfaceTransmissionEnabled)
                flags |= ThinSurfaceTransmissionEnabledFlag;
            flags |= PackContentDependentLocalLightSamplingFlags(
                UsesContentDependentLocalLightSamplingFlags,
                gi.EffectiveSimpleDdgiManyLightSamplingEnabled,
                ResolveLocalLightSamplingMode(gi),
                gi.SimpleDdgiExactLocalLightThreshold,
                gi.SimpleDdgiLightTreeUniformMixtureProbability);

            return new GPUSimpleDdgiPushConstants
            {
                ParamsBufferIndex = BindlessIndex.SimpleDdgiParamsBuffer,
                IrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                VisibilityAtlasBufferIndex = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
                RayResultScratchBufferIndex = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
                CurrentFrameIndex = sceneData.CurrentFrameIndex,
                LightCount = checked((uint)Math.Max(0, sceneData.LightCount)),
                DirectionalLightCount = checked((uint)Math.Max(0, sceneData.DirectionalLightCount)),
                LocalLightCount = checked((uint)Math.Max(0, sceneData.LocalLightCount)),
                MaxShadedLights = checked((uint)Math.Clamp(sceneData.DdgiEffectiveMaxShadedLights > 0 ? sceneData.DdgiEffectiveMaxShadedLights : gi.DdgiMaxShadedLights, 0, 64)),
                EmissiveSourceCount = checked((uint)Math.Max(0, sceneData.DdgiEmissiveSourceCount)),
                FarFieldParamsBufferIndex = BindlessIndex.FarFieldClipmapParamsBuffer,
                FarFieldVoxelBufferIndex = BindlessIndex.FarFieldClipmapVoxelBuffer,
                FarFieldInstanceBufferIndex = BindlessIndex.FarFieldClipmapInstanceBuffer,
                Flags = flags,
                MaterialTextureMaxCascade = PackTraceMaterialAndGeometryLimits(gi),
                ProbeStateBufferIndex = BindlessIndex.SimpleDdgiProbeStateBuffer,
                ProbeUpdateQueueBufferIndex = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
                RelocationClassificationBufferIndex = BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
                TransportSourceCacheBufferIndex = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
                TransportReadIrradianceAtlasBufferIndex = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
                TransportWriteIrradianceAtlasBufferIndex = gi.SimpleDdgiTransportV2Enabled
                    || IsGpuResidentMode
                    ? checked((uint)BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer)
                    : checked((uint)BindlessIndex.SimpleDdgiIrradianceAtlasBuffer),
                PrivateVisibilityAtlasOffsetWords = IsGpuResidentMode
                    ? VolumeManager.GpuSchedulerPrivateVisibilityOffsetWords
                    : 0u,
                TransportGeneration = VolumeManager.TransportGeneration,
                PrimaryDirectionalLightIndex = sceneData.DdgiPrimaryDirectionalLightIndex < 0
                    ? uint.MaxValue
                    : checked((uint)sceneData.DdgiPrimaryDirectionalLightIndex),
                SchedulerArenaBufferIndex = IsGpuResidentMode
                    ? checked((uint)BindlessIndex.SimpleDdgiSchedulerArenaBuffer)
                    : uint.MaxValue,
                SchedulerRayBucketCommandsOffsetWords = IsGpuResidentMode
                    ? VolumeManager.GpuScheduler.Layout!.RayBucketCommands.OffsetWords
                    : 0u,
                SchedulerRayBucketMetadataOffsetWords = IsGpuResidentMode
                    ? VolumeManager.GpuScheduler.Layout!.RayBucketMetadata.OffsetWords
                    : 0u,
                SchedulerOutcomesOffsetWords = IsGpuResidentMode
                    ? VolumeManager.GpuScheduler.Layout!.Outcomes.OffsetWords
                    : 0u,
                SchedulerCountersOffsetWords = IsGpuResidentMode
                    ? UsesTraceDirtyRegionOffsets
                        ? VolumeManager.GpuScheduler.Layout!.Frame.OffsetWords
                        : VolumeManager.GpuScheduler.Layout!.Counters.OffsetWords
                    : 0u,
                SchedulerUpdateRecordsOffsetWords = IsGpuResidentMode
                    ? UsesTraceDirtyRegionOffsets
                        ? VolumeManager.GpuScheduler.Layout!.DirtyRegions.OffsetWords
                        : VolumeManager.GpuScheduler.Layout!.UpdateRecords.OffsetWords
                    : 0u
            };
        }

        internal static uint PackTraceMaterialAndGeometryLimits(
            GlobalIlluminationSettings gi)
        {
            ArgumentNullException.ThrowIfNull(gi);
            uint cascade = gi.DdgiMaterialTextureMaxCascade < 0
                ? GlobalIlluminationSettings.MaxSimpleDdgiMaterialTextureCascade
                : checked((uint)Math.Clamp(
                    gi.DdgiMaterialTextureMaxCascade,
                    0,
                    GlobalIlluminationSettings
                        .MaxSimpleDdgiMaterialTextureCascade - 1));
            int transparencyCandidates = Math.Clamp(
                gi.DdgiTransparencyCandidateLimit,
                1,
                256);
            uint encodedTransparencyCandidates =
                transparencyCandidates == 256
                    ? 0u
                    : checked((uint)transparencyCandidates);
            int transparencyLayers = Math.Clamp(
                gi.DdgiTransparencyLayerLimit,
                1,
                64);
            uint encodedTransparencyLayers = transparencyLayers == 64
                ? 0u
                : checked((uint)transparencyLayers);
            uint decalCandidates = checked((uint)Math.Clamp(
                gi.DdgiDecalCandidateLimit,
                0,
                DdgiGeometryParticipation.ProductionDecalCandidateLimit));
            return (cascade & 0xFu) |
                (encodedTransparencyCandidates <<
                    TransparencyCandidateLimitShift) |
                (encodedTransparencyLayers << TransparencyLayerLimitShift) |
                (decalCandidates << DecalCandidateLimitShift);
        }

        internal static uint PackContentDependentLocalLightSamplingFlags(
            bool tracePrivateAbi,
            bool samplingEnabled,
            SimpleDdgiLocalLightSamplingMode samplingMode,
            int exactLightThreshold,
            float uniformMixtureProbability)
        {
            // Bits 23..31 deliberately overlap the cached-solve sweep ABI.
            // Returning zero for every non-trace pass is therefore a
            // correctness condition, not merely a small dispatch optimization.
            if (!tracePrivateAbi || !samplingEnabled)
                return 0u;

            float finiteMixture = float.IsFinite(uniformMixtureProbability)
                ? uniformMixtureProbability
                : 0.02f;
            uint quantizedMixture = checked((uint)Math.Clamp(
                (int)MathF.Round(finiteMixture * 1020.0f),
                1,
                255));
            uint threshold = checked((uint)Math.Clamp(
                exactLightThreshold,
                0,
                GlobalIlluminationSettings.MaxSimpleDdgiExactLocalLightThreshold));

            return ContentDependentLocalLightSamplingEnabledFlag |
                (((uint)samplingMode & 0x3u) << LocalLightSamplingModeShift) |
                (threshold << ExactLocalLightThresholdShift) |
                (quantizedMixture << UniformMixtureShift);
        }

        private void CreateAccelerationStructureSetLayout()
        {
            var binding = new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };

            var layoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding
            };

            Result result = _context.Api.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _accelerationStructureSetLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create simple DDGI acceleration-structure descriptor set layout", result);
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo };
            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {Name} pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            _setLayouts = _requiresRayQuery
                ? [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout, _accelerationStructureSetLayout]
                : [_bindlessHeap.StorageBufferSetLayout, _bindlessHeap.TextureSamplerSetLayout];

            fixed (DescriptorSetLayout* setLayouts = _setLayouts)
            {
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.ComputeBit,
                    Offset = 0,
                    Size = (uint)Marshal.SizeOf<GPUSimpleDdgiPushConstants>()
                };

                var layoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = (uint)_setLayouts.Length,
                    PSetLayouts = setLayouts,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new VulkanException($"Failed to create {Name} pipeline layout", result);
            }
        }

        private VkPipeline GetOrCreatePipeline(int dispatchIndex)
        {
            string shaderName = ResolveShaderName(dispatchIndex);
            return GetOrCreatePipeline(shaderName);
        }

        private VkPipeline GetOrCreatePipeline(string shaderName)
        {
            if (string.IsNullOrWhiteSpace(shaderName) ||
                !string.Equals(shaderName, System.IO.Path.GetFileName(shaderName),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{Name} resolved an invalid shader name '{shaderName}'.");
            }
            if (_pipelines.TryGetValue(shaderName, out VkPipeline pipeline))
                return pipeline;

            pipeline = CreatePipeline(shaderName);
            _pipelines.Add(shaderName, pipeline);
            return pipeline;
        }

        private VkPipeline CreatePipeline(string shaderName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = stage,
                    Layout = _pipelineLayout,
                    BasePipelineIndex = -1
                };

                long pipelineStart = _pipelineCacheService?.BeginPipelineCreation() ?? 0L;
                Result result;
                VkPipeline pipeline;
                try
                {
                    result = _context.Api.CreateComputePipelines(_context.Device, _pipelineCache, 1, &pipelineInfo, null, out pipeline);
                }
                finally
                {
                    _pipelineCacheService?.EndPipelineCreation(
                        $"{Name}:{shaderName}",
                        pipelineStart);
                }
                if (result != Result.Success)
                    throw new VulkanException(
                        $"Failed to create {Name} compute pipeline from '{shaderName}'",
                        result);
                return pipeline;
            }
            finally
            {
                if (shaderModule.Handle != 0)
                    _context.Api.DestroyShaderModule(_context.Device, shaderModule, null);
            }
        }

        private void CreateDescriptorSet()
        {
            const uint descriptorSetCount = RenderingConstants.FramesInFlight;
            var poolSize = new DescriptorPoolSize
            {
                Type = DescriptorType.AccelerationStructureKhr,
                DescriptorCount = descriptorSetCount
            };

            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = descriptorSetCount
            };

            Result result = _context.Api.CreateDescriptorPool(_context.Device, &poolInfo, null, out _descriptorPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create simple DDGI descriptor pool", result);

            DescriptorSetLayout* layouts = stackalloc DescriptorSetLayout[RenderingConstants.FramesInFlight];
            for (int frameSlot = 0; frameSlot < RenderingConstants.FramesInFlight; frameSlot++)
                layouts[frameSlot] = _accelerationStructureSetLayout;

            fixed (DescriptorSet* descriptorSets = _accelerationStructureSets)
            {
                var allocInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = descriptorSetCount,
                    PSetLayouts = layouts
                };

                result = _context.Api.AllocateDescriptorSets(
                    _context.Device,
                    &allocInfo,
                    descriptorSets);
            }
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate simple DDGI frame-owned acceleration-structure descriptor sets", result);
        }

        private void UpdateAccelerationStructureDescriptor(SceneRenderingData sceneData)
        {
            if (_accelerationStructureManager == null)
                throw new InvalidOperationException("Simple DDGI trace requires an acceleration structure manager.");

            int descriptorFrameSlot = ResolveDescriptorFrameSlot(sceneData);
            AccelerationStructureKHR tlas = _accelerationStructureManager.TopLevelAccelerationStructureHandle;
            if (_boundTlases[descriptorFrameSlot].Handle == tlas.Handle)
                return;

            var accelerationStructureInfo = new WriteDescriptorSetAccelerationStructureKHR
            {
                SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1,
                PAccelerationStructures = &tlas
            };

            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &accelerationStructureInfo,
                DstSet = _accelerationStructureSets[descriptorFrameSlot],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr
            };

            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
            _boundTlases[descriptorFrameSlot] = tlas;
        }

        private static int ResolveDescriptorFrameSlot(SceneRenderingData sceneData)
        {
            uint frameSlot = sceneData.CurrentFrameIndex;
            if (frameSlot >= RenderingConstants.FramesInFlight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sceneData),
                    frameSlot,
                    $"Simple DDGI frame slot must be below {RenderingConstants.FramesInFlight}.");
            }

            return checked((int)frameSlot);
        }

        private void InsertWriteBarrier(CommandBuffer cmd)
        {
            // This pass may execute on a compute-only queue. Publish only to
            // subsequent compute dispatches here; graphics consumers establish
            // their fragment visibility at the forward-pass boundary.
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit |
                                AccessFlags2.ShaderStorageWriteBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void InsertIndirectCommandReadBarrier(CommandBuffer cmd)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
                SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
                DstStageMask = PipelineStageFlags2.DrawIndirectBit,
                DstAccessMask = AccessFlags2.IndirectCommandReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using Vma;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class RendererDiagnosticsBuffer : IDisposable
    {
        public const int MeshletCounterCount = 9;
        public const int DdgiForwardEstimateCounterBase = MeshletCounterCount;
        public const int DdgiForwardEstimateCounterCount = 46;
        public const int DdgiTraceEnergyCounterBase = DdgiForwardEstimateCounterBase + DdgiForwardEstimateCounterCount;
        public const int DdgiTraceEnergyCounterCount = 11;
        public const int DdgiTraceEarlyOutCounterBase = DdgiTraceEnergyCounterBase + DdgiTraceEnergyCounterCount;
        public const int DdgiTraceEarlyOutCounterCount = 6;
        public const int DdgiBlendEnergyCounterBase = DdgiTraceEarlyOutCounterBase + DdgiTraceEarlyOutCounterCount;
        public const int DdgiBlendEnergyCounterCount = 7;
        public const int DdgiTraceRingMismatchSampleBase = DdgiBlendEnergyCounterBase + DdgiBlendEnergyCounterCount;
        public const int DdgiTraceRingMismatchSampleCount = 20;
        public const int FarFieldCounterBase = DdgiTraceRingMismatchSampleBase + DdgiTraceRingMismatchSampleCount;
        public const int FarFieldCounterCount = 10;
        public const int DdgiInvestigationCounterBase = FarFieldCounterBase + FarFieldCounterCount;
        public const int DdgiInvestigationFixedCounterCount = 38;
        public const int SimpleDdgiVolumeGatherCounterCount = GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount;
        public const int SimpleDdgiVolumePrimaryGatherCounterBase = DdgiInvestigationCounterBase + DdgiInvestigationFixedCounterCount;
        public const int SimpleDdgiVolumeSampledGatherCounterBase = SimpleDdgiVolumePrimaryGatherCounterBase + SimpleDdgiVolumeGatherCounterCount;
        public const int DdgiInvestigationCounterCount = DdgiInvestigationFixedCounterCount + SimpleDdgiVolumeGatherCounterCount * 2;
        public const int CounterCount = MeshletCounterCount + DdgiForwardEstimateCounterCount + DdgiTraceEnergyCounterCount + DdgiTraceEarlyOutCounterCount + DdgiBlendEnergyCounterCount + DdgiTraceRingMismatchSampleCount + FarFieldCounterCount + DdgiInvestigationCounterCount;
        public const float DdgiForwardEstimateWeightScale = 1024.0f;
        public const float DdgiForwardEstimateLuminanceScale = 4096.0f;
        public const ulong CounterBufferSize = CounterCount * sizeof(uint);

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BufferHandle[] _buffers = new BufferHandle[FramesInFlight];
        private readonly GpuMeshletCounters[] _lastCompletedCounters = new GpuMeshletCounters[FramesInFlight];
        private readonly DdgiForwardEstimateCounters[] _lastCompletedDdgiForwardEstimateCounters = new DdgiForwardEstimateCounters[FramesInFlight];
        private readonly DdgiInvestigationCounters[] _lastCompletedDdgiInvestigationCounters = new DdgiInvestigationCounters[FramesInFlight];
        private bool _disposed;

        public RendererDiagnosticsBuffer(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));

            for (int i = 0; i < FramesInFlight; i++)
            {
                _buffers[i] = _bufferManager.CreateBuffer(
                    CounterBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    $"Renderer Diagnostics Buffer Frame {i}",
                    MemoryBudgetCategory.DiagnosticsAndDebug);
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            for (int i = 0; i < FramesInFlight; i++)
            {
                VkBuffer buffer = _bufferManager.GetBuffer(_buffers[i]);
                bindlessHeap.RegisterStorageBuffer(BindlessIndex.RendererDiagnosticsBufferBase + i, buffer, 0, CounterBufferSize);
            }
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            _bufferManager.InvalidateBuffer(_buffers[frameIndex], 0, CounterBufferSize);
            uint* counters = (uint*)_bufferManager.GetMappedPointer(_buffers[frameIndex]);

            _lastCompletedCounters[frameIndex] = new GpuMeshletCounters(
                checked((int)counters[0]),
                checked((int)counters[1]),
                checked((int)counters[2]),
                checked((int)counters[3]),
                checked((int)counters[4]),
                checked((int)counters[5]),
                checked((int)counters[6]),
                checked((int)counters[7]),
                checked((int)counters[8]));

            uint sampleCount = counters[DdgiForwardEstimateCounterBase + 9];
            uint visibilityMomentSampleCount = counters[DdgiForwardEstimateCounterBase + 15];
            uint probeQualitySampleCount = counters[DdgiForwardEstimateCounterBase + 26];
            uint clipmapInfoPrimaryAttemptCount = counters[DdgiForwardEstimateCounterBase + 27];
            uint clipmapInfoPrimaryOkCount = counters[DdgiForwardEstimateCounterBase + 28];
            uint fastGatherAttemptCount = counters[DdgiForwardEstimateCounterBase + 32];
            uint shaderGatherFallbackAttemptCount = counters[DdgiForwardEstimateCounterBase + 38];
            uint sampledProbeCurrentFrustumCount = counters[DdgiForwardEstimateCounterBase + 43];
            uint sampledProbeSideRearCount = counters[DdgiForwardEstimateCounterBase + 44];
            uint sampledProbeStaleAgeCount = counters[DdgiForwardEstimateCounterBase + 45];
            uint traceEnergySampleCount = counters[DdgiTraceEnergyCounterBase + 0];
            uint traceEarlyOutDisabledCount = counters[DdgiTraceEarlyOutCounterBase + 0];
            uint traceEarlyOutBeyondRequestCount = counters[DdgiTraceEarlyOutCounterBase + 1];
            uint traceEarlyOutResolveBoundsCount = counters[DdgiTraceEarlyOutCounterBase + 2];
            uint traceEarlyOutResolveProbeRangeCount = counters[DdgiTraceEarlyOutCounterBase + 3];
            uint traceEarlyOutResolveClipmapCellCount = counters[DdgiTraceEarlyOutCounterBase + 4];
            uint traceEarlyOutResolveClipmapRingCount = counters[DdgiTraceEarlyOutCounterBase + 5];
            uint blendEnergySampleCount = counters[DdgiBlendEnergyCounterBase + 0];
            uint traceRingMismatchSampleValid = counters[DdgiTraceRingMismatchSampleBase + 0];
            uint traceRingMismatchCorrectedCount = counters[DdgiTraceRingMismatchSampleBase + 19];
            uint ddgiInvestigationSampleCount = counters[DdgiInvestigationCounterBase + 0];
            uint simpleVisibilitySampleCount = ddgiInvestigationSampleCount;
            uint skyVisibilitySampleCount = counters[DdgiInvestigationCounterBase + 30];
            bool investigationValid = false;
            for (int i = 0; i < DdgiInvestigationCounterCount; i++)
            {
                if (counters[DdgiInvestigationCounterBase + i] != 0)
                {
                    investigationValid = true;
                    break;
                }
            }

            float invInvestigationSampleCount = ddgiInvestigationSampleCount > 0 ? 1.0f / ddgiInvestigationSampleCount : 0.0f;
            float invSimpleVisibilitySampleCount = simpleVisibilitySampleCount > 0 ? 1.0f / simpleVisibilitySampleCount : 0.0f;
            float invSkyVisibilitySampleCount = skyVisibilitySampleCount > 0 ? 1.0f / skyVisibilitySampleCount : 0.0f;
            uint[] simpleVolumePrimaryGatherCounts = investigationValid
                ? new uint[SimpleDdgiVolumeGatherCounterCount]
                : Array.Empty<uint>();
            uint[] simpleVolumeSampledGatherCounts = investigationValid
                ? new uint[SimpleDdgiVolumeGatherCounterCount]
                : Array.Empty<uint>();
            for (int i = 0; i < simpleVolumePrimaryGatherCounts.Length; i++)
            {
                simpleVolumePrimaryGatherCounts[i] = counters[SimpleDdgiVolumePrimaryGatherCounterBase + i];
                simpleVolumeSampledGatherCounts[i] = counters[SimpleDdgiVolumeSampledGatherCounterBase + i];
            }

            _lastCompletedDdgiInvestigationCounters[frameIndex] = investigationValid
                ? new DdgiInvestigationCounters(
                    ReadbackValid: 1,
                    SimpleForwardSampleCount: counters[DdgiInvestigationCounterBase + 0],
                    LegacyForwardSampleCount: counters[DdgiInvestigationCounterBase + 1],
                    FreshAtlasForwardSampleCount: counters[DdgiInvestigationCounterBase + 2],
                    SimpleZeroIrradianceSampleCount: counters[DdgiInvestigationCounterBase + 3],
                    SimpleNonzeroIrradianceSampleCount: counters[DdgiInvestigationCounterBase + 4],
                    SimpleSampledIrradianceLuminanceAverage: counters[DdgiInvestigationCounterBase + 5] / DdgiForwardEstimateLuminanceScale * invInvestigationSampleCount,
                    SimpleVisibilityAverage: counters[DdgiInvestigationCounterBase + 6] / DdgiForwardEstimateWeightScale * invSimpleVisibilitySampleCount,
                    SimpleLowVisibilitySampleCount: counters[DdgiInvestigationCounterBase + 7],
                    ForwardZeroFinalIndirectCount: counters[DdgiInvestigationCounterBase + 8],
                    ForwardZeroDdgiButNonzeroIblCount: counters[DdgiInvestigationCounterBase + 9],
                    ForwardZeroDdgiAndZeroIblCount: counters[DdgiInvestigationCounterBase + 10],
                    ForwardOutOfGridSampleCount: counters[DdgiInvestigationCounterBase + 11],
                    ForwardClampedProbeSampleCount: counters[DdgiInvestigationCounterBase + 12],
                    ForwardNanOrInfSampleCount: counters[DdgiInvestigationCounterBase + 13],
                    IrradianceAtlasZeroTexelSampleCount: counters[DdgiInvestigationCounterBase + 14],
                    VisibilityAtlasZeroMomentSampleCount: counters[DdgiInvestigationCounterBase + 15],
                    AtlasWriteProbeCount: counters[DdgiInvestigationCounterBase + 16],
                    AtlasWriteTexelCount: counters[DdgiInvestigationCounterBase + 17],
                    BlendZeroRayWeightProbeCount: counters[DdgiInvestigationCounterBase + 18],
                    BlendNonzeroIrradianceProbeCount: counters[DdgiInvestigationCounterBase + 19],
                    BlendPreviousAtlasUsedCount: counters[DdgiInvestigationCounterBase + 20],
                    BlendHysteresisZeroFrameCount: counters[DdgiInvestigationCounterBase + 21],
                    SimpleTraceHitCount: counters[DdgiInvestigationCounterBase + 22],
                    SimpleTraceMissCount: counters[DdgiInvestigationCounterBase + 23],
                    SimpleTraceZeroRadianceHitCount: counters[DdgiInvestigationCounterBase + 24],
                    SimpleTraceDirectLightHitCount: counters[DdgiInvestigationCounterBase + 25],
                    SimpleTraceEmissiveHitCount: counters[DdgiInvestigationCounterBase + 26],
                    SimpleTraceFarFieldHitCount: counters[DdgiInvestigationCounterBase + 27],
                    SimpleTraceFarFieldMissCount: counters[DdgiInvestigationCounterBase + 28],
                    SimpleTraceTlasUnavailableFrameCount: counters[DdgiInvestigationCounterBase + 29],
                    SkyVisibilitySampleCount: skyVisibilitySampleCount,
                    SkyVisibilityAverage: counters[DdgiInvestigationCounterBase + 31] / DdgiForwardEstimateWeightScale * invSkyVisibilitySampleCount,
                    FarSunShadowSampleCount: counters[DdgiInvestigationCounterBase + 32],
                    FarSunShadowOccludedCount: counters[DdgiInvestigationCounterBase + 33],
                    RoughSpecularSampleCount: counters[DdgiInvestigationCounterBase + 34],
                    RoughSpecularNonzeroCount: counters[DdgiInvestigationCounterBase + 35],
                    SimpleGatherCount: counters[DdgiInvestigationCounterBase + 36],
                    SimpleSecondVolumeGatherCount: counters[DdgiInvestigationCounterBase + 37],
                    SimpleVolumePrimaryGatherCounts: simpleVolumePrimaryGatherCounts,
                    SimpleVolumeSampledGatherCounts: simpleVolumeSampledGatherCounts,
                    FarFieldStepBucket0Count: counters[FarFieldCounterBase + 5],
                    FarFieldStepBucket1Count: counters[FarFieldCounterBase + 6],
                    FarFieldStepBucket2Count: counters[FarFieldCounterBase + 7],
                    FarFieldStepBucket3Count: counters[FarFieldCounterBase + 8],
                    FarFieldStepBucket4Count: counters[FarFieldCounterBase + 9])
                : DdgiInvestigationCounters.Empty;
            if (sampleCount > 0 ||
                visibilityMomentSampleCount > 0 ||
                clipmapInfoPrimaryAttemptCount > 0 ||
                fastGatherAttemptCount > 0 ||
                shaderGatherFallbackAttemptCount > 0 ||
                sampledProbeCurrentFrustumCount > 0 ||
                sampledProbeSideRearCount > 0 ||
                sampledProbeStaleAgeCount > 0 ||
                traceEnergySampleCount > 0 ||
                traceEarlyOutDisabledCount > 0 ||
                traceEarlyOutBeyondRequestCount > 0 ||
                traceEarlyOutResolveBoundsCount > 0 ||
                traceEarlyOutResolveProbeRangeCount > 0 ||
                traceEarlyOutResolveClipmapCellCount > 0 ||
                traceEarlyOutResolveClipmapRingCount > 0 ||
                blendEnergySampleCount > 0 ||
                traceRingMismatchSampleValid > 0 ||
                traceRingMismatchCorrectedCount > 0)
            {
                float invSampleCount = sampleCount > 0 ? 1.0f / sampleCount : 0.0f;
                float invVisibilityMomentSampleCount = visibilityMomentSampleCount > 0 ? 1.0f / visibilityMomentSampleCount : 0.0f;
                float invProbeQualitySampleCount = probeQualitySampleCount > 0 ? 1.0f / probeQualitySampleCount : 0.0f;
                float invClipmapInfoPrimaryAttemptCount = clipmapInfoPrimaryAttemptCount > 0 ? 1.0f / clipmapInfoPrimaryAttemptCount : 0.0f;
                float invClipmapInfoPrimaryOkCount = clipmapInfoPrimaryOkCount > 0 ? 1.0f / clipmapInfoPrimaryOkCount : 0.0f;
                float invTraceEnergySampleCount = traceEnergySampleCount > 0 ? 1.0f / traceEnergySampleCount : 0.0f;
                float invBlendEnergySampleCount = blendEnergySampleCount > 0 ? 1.0f / blendEnergySampleCount : 0.0f;
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = new DdgiForwardEstimateCounters(
                    ReadbackValid: sampleCount > 0 || clipmapInfoPrimaryAttemptCount > 0 || traceEnergySampleCount > 0 || traceEarlyOutDisabledCount > 0 || traceEarlyOutBeyondRequestCount > 0 || traceEarlyOutResolveBoundsCount > 0 || traceEarlyOutResolveProbeRangeCount > 0 || traceEarlyOutResolveClipmapCellCount > 0 || traceEarlyOutResolveClipmapRingCount > 0 || blendEnergySampleCount > 0 || traceRingMismatchSampleValid > 0 || traceRingMismatchCorrectedCount > 0 ? 1 : 0,
                    SpatialCoverageAverage: counters[DdgiForwardEstimateCounterBase + 0] / DdgiForwardEstimateWeightScale * invSampleCount,
                    SupportCoverageAverage: counters[DdgiForwardEstimateCounterBase + 1] / DdgiForwardEstimateWeightScale * invSampleCount,
                    DataConfidenceAverage: counters[DdgiForwardEstimateCounterBase + 2] / DdgiForwardEstimateWeightScale * invSampleCount,
                    VisibilityConfidenceAverage: counters[DdgiForwardEstimateCounterBase + 3] / DdgiForwardEstimateWeightScale * invSampleCount,
                    LeakAttenuationAverage: counters[DdgiForwardEstimateCounterBase + 4] / DdgiForwardEstimateWeightScale * invSampleCount,
                    EffectiveWeightAverage: counters[DdgiForwardEstimateCounterBase + 5] / DdgiForwardEstimateWeightScale * invSampleCount,
                    RawDiffuseLuminanceAverage: counters[DdgiForwardEstimateCounterBase + 6] / DdgiForwardEstimateLuminanceScale * invSampleCount,
                    FinalDiffuseLuminanceAverage: counters[DdgiForwardEstimateCounterBase + 7] / DdgiForwardEstimateLuminanceScale * invSampleCount,
                    EnvironmentFallbackWeightAverage: counters[DdgiForwardEstimateCounterBase + 42] / DdgiForwardEstimateWeightScale * invSampleCount,
                    OwnershipConsumedAverage: counters[DdgiForwardEstimateCounterBase + 8] / DdgiForwardEstimateWeightScale * invSampleCount,
                    SampledIrradianceLuminanceAverage: counters[DdgiForwardEstimateCounterBase + 41] / DdgiForwardEstimateLuminanceScale * invSampleCount,
                    SampleCount: sampleCount,
                    ZeroSupportButSpatiallyCoveredCount: counters[DdgiForwardEstimateCounterBase + 10],
                    ZeroEffectiveButSpatiallyCoveredCount: counters[DdgiForwardEstimateCounterBase + 11],
                    VisibilityMomentMeanAverage: counters[DdgiForwardEstimateCounterBase + 12] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityMomentVarianceAverage: counters[DdgiForwardEstimateCounterBase + 13] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityProbeDistanceAverage: counters[DdgiForwardEstimateCounterBase + 14] / DdgiForwardEstimateWeightScale * invVisibilityMomentSampleCount,
                    VisibilityMomentSampleCount: visibilityMomentSampleCount,
                    VisibilityLargeDistanceMarginCount: counters[DdgiForwardEstimateCounterBase + 16],
                    VisibilityZeroTransportCount: counters[DdgiForwardEstimateCounterBase + 17],
                    VisibilityZeroTransportWithIrradianceCount: counters[DdgiForwardEstimateCounterBase + 18],
                    SupportRejectedInactiveCount: counters[DdgiForwardEstimateCounterBase + 19],
                    SupportRejectedZeroIrradianceAlphaCount: counters[DdgiForwardEstimateCounterBase + 20],
                    SupportRejectedLowQualityCount: counters[DdgiForwardEstimateCounterBase + 21],
                    ProbeIrradianceAlphaAverage: counters[DdgiForwardEstimateCounterBase + 22] / DdgiForwardEstimateWeightScale * invProbeQualitySampleCount,
                    ProbeQualityXAverage: counters[DdgiForwardEstimateCounterBase + 23] / DdgiForwardEstimateWeightScale * invProbeQualitySampleCount,
                    ProbeQualityYAverage: counters[DdgiForwardEstimateCounterBase + 24] / DdgiForwardEstimateWeightScale * invProbeQualitySampleCount,
                    ProbeQualityZAverage: counters[DdgiForwardEstimateCounterBase + 25] / DdgiForwardEstimateWeightScale * invProbeQualitySampleCount,
                    ProbeQualitySampleCount: probeQualitySampleCount,
                    SampledProbeCurrentFrustumCount: sampledProbeCurrentFrustumCount,
                    SampledProbeSideRearCount: sampledProbeSideRearCount,
                    SampledProbeStaleAgeCount: sampledProbeStaleAgeCount,
                    ClipmapInfoPrimaryAttemptCount: clipmapInfoPrimaryAttemptCount,
                    ClipmapInfoPrimaryOkCount: clipmapInfoPrimaryOkCount,
                    ClipmapInfoPrimaryFailedCount: counters[DdgiForwardEstimateCounterBase + 29],
                    ClipmapInfoPrimaryEdgeFadeAverage: counters[DdgiForwardEstimateCounterBase + 30] / DdgiForwardEstimateWeightScale * invClipmapInfoPrimaryOkCount,
                    ClipmapInfoPrimaryBlendWeightAverage: counters[DdgiForwardEstimateCounterBase + 31] / DdgiForwardEstimateWeightScale * invClipmapInfoPrimaryAttemptCount,
                    FastGatherAttemptCount: fastGatherAttemptCount,
                    FastGatherAcceptedCount: counters[DdgiForwardEstimateCounterBase + 33],
                    FastGatherRejectedZeroSpatialCount: counters[DdgiForwardEstimateCounterBase + 34],
                    FastGatherRejectedZeroSupportCount: counters[DdgiForwardEstimateCounterBase + 35],
                    FastGatherRejectedZeroDataCount: counters[DdgiForwardEstimateCounterBase + 36],
                    FastGatherRejectedZeroOwnershipCount: counters[DdgiForwardEstimateCounterBase + 37],
                    ShaderGatherFallbackAttemptCount: shaderGatherFallbackAttemptCount,
                    ShaderGatherFallbackAcceptedCount: counters[DdgiForwardEstimateCounterBase + 39],
                    ShaderGatherFallbackEmptyCount: counters[DdgiForwardEstimateCounterBase + 40],
                    TraceEnergySampleCount: traceEnergySampleCount,
                    TraceEnergyHitCount: counters[DdgiTraceEnergyCounterBase + 1],
                    TraceEnergyMissCount: counters[DdgiTraceEnergyCounterBase + 2],
                    TraceEnergyRayLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 3] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEnergyDirectLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 4] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEnergyEmissiveLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 5] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEnergyStableLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 6] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEnergySkyLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 7] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEnergyHitZeroDirectCount: counters[DdgiTraceEnergyCounterBase + 8],
                    TraceEnergyHitWithDirectCount: counters[DdgiTraceEnergyCounterBase + 9],
                    TraceEnergyDirectNoShadowLuminanceAverage: counters[DdgiTraceEnergyCounterBase + 10] / DdgiForwardEstimateLuminanceScale * invTraceEnergySampleCount,
                    TraceEarlyOutDisabledCount: traceEarlyOutDisabledCount,
                    TraceEarlyOutBeyondRequestCount: traceEarlyOutBeyondRequestCount,
                    TraceEarlyOutResolveBoundsCount: traceEarlyOutResolveBoundsCount,
                    TraceEarlyOutResolveProbeRangeCount: traceEarlyOutResolveProbeRangeCount,
                    TraceEarlyOutResolveClipmapCellCount: traceEarlyOutResolveClipmapCellCount,
                    TraceEarlyOutResolveClipmapRingCount: traceEarlyOutResolveClipmapRingCount,
                    TraceRingMismatchSampleValid: traceRingMismatchSampleValid,
                    TraceRingMismatchSampleUpdateIndex: counters[DdgiTraceRingMismatchSampleBase + 1],
                    TraceRingMismatchSampleRequestProbeIndex: counters[DdgiTraceRingMismatchSampleBase + 2],
                    TraceRingMismatchSampleVolumeIndex: counters[DdgiTraceRingMismatchSampleBase + 3],
                    TraceRingMismatchSampleLogicalCellX: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 4]),
                    TraceRingMismatchSampleLogicalCellY: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 5]),
                    TraceRingMismatchSampleLogicalCellZ: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 6]),
                    TraceRingMismatchSampleFirstProbe: counters[DdgiTraceRingMismatchSampleBase + 7],
                    TraceRingMismatchSampleComputedProbeIndex: counters[DdgiTraceRingMismatchSampleBase + 8],
                    TraceRingMismatchSampleGridMinX: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 9]),
                    TraceRingMismatchSampleGridMinY: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 10]),
                    TraceRingMismatchSampleGridMinZ: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 11]),
                    TraceRingMismatchSampleRingOffsetX: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 12]),
                    TraceRingMismatchSampleRingOffsetY: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 13]),
                    TraceRingMismatchSampleRingOffsetZ: DecodeSignedCounter(counters[DdgiTraceRingMismatchSampleBase + 14]),
                    TraceRingMismatchSampleProbeCountX: counters[DdgiTraceRingMismatchSampleBase + 15],
                    TraceRingMismatchSampleProbeCountY: counters[DdgiTraceRingMismatchSampleBase + 16],
                    TraceRingMismatchSampleProbeCountZ: counters[DdgiTraceRingMismatchSampleBase + 17],
                    TraceRingMismatchSampleRequestAgeFrames: counters[DdgiTraceRingMismatchSampleBase + 18],
                    TraceRingMismatchCorrectedCount: traceRingMismatchCorrectedCount,
                    BlendEnergySampleCount: blendEnergySampleCount,
                    BlendEnergyIrradianceLuminanceAverage: counters[DdgiBlendEnergyCounterBase + 1] / DdgiForwardEstimateLuminanceScale * invBlendEnergySampleCount,
                    BlendEnergyConfidenceAverage: counters[DdgiBlendEnergyCounterBase + 2] / DdgiForwardEstimateWeightScale * invBlendEnergySampleCount,
                    BlendEnergyLowConfidenceCount: counters[DdgiBlendEnergyCounterBase + 3],
                    BlendEnergyNonzeroIrradianceCount: counters[DdgiBlendEnergyCounterBase + 4],
                    BlendEnergyNonFiniteIrradianceCount: counters[DdgiBlendEnergyCounterBase + 5],
                    BlendEnergyFireflySuppressedCount: counters[DdgiBlendEnergyCounterBase + 6]);
            }
            else
            {
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = DdgiForwardEstimateCounters.Empty;
            }
        }

        public GpuMeshletCounters GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedCounters[frameIndex];
        }

        public DdgiForwardEstimateCounters GetLastCompletedDdgiForwardEstimateCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiForwardEstimateCounters[frameIndex];
        }

        /// <summary>Returns the current frame's physical diagnostics storage allocation.</summary>
        public BufferHandle GetBufferHandle(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _buffers[frameIndex];
        }

        public DdgiInvestigationCounters GetLastCompletedDdgiInvestigationCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiInvestigationCounters[frameIndex];
        }

        private static int DecodeSignedCounter(uint value)
        {
            return unchecked((int)value);
        }

        public void ResetCounters(CommandBuffer commandBuffer, int frameIndex)
        {
            ValidateFrameIndex(frameIndex);

            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(_buffers[frameIndex]),
                0,
                CounterBufferSize,
                0);

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt | PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_buffers[frameIndex]),
                Offset = 0,
                Size = CounterBufferSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (_buffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_buffers[i]);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GpuMeshletCounters(
        int DepthCandidates,
        int DepthFrustumCulled,
        int DepthEmitted,
        int ForwardCandidates,
        int ForwardFrustumCulled,
        int ForwardOcclusionCulled,
        int ForwardEmitted,
        int ForwardOcclusionTested,
        int SsgiRejectedHistoryPixels);
}

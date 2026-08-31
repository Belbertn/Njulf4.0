using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
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
        // Appended rather than inserted so existing capture counter locations
        // remain stable. V2 uses this sampled family to make source/cache/bounce
        // transport directly observable without production atomic traffic.
        public const int SimpleDdgiTransportCounterBase = DdgiInvestigationCounterBase + DdgiInvestigationCounterCount;
        public const int SimpleDdgiTransportCounterCount = 6;
        // Keep shadow receiver telemetry appended so all pre-existing capture offsets
        // stay stable. Each cascade family is sparsely sampled in forward.frag.
        public const int DirectionalShadowReceiverCounterBase = SimpleDdgiTransportCounterBase + SimpleDdgiTransportCounterCount;
        public const int DirectionalShadowReceiverCascadeCount = ShadowSettings.MaxDirectionalCascades;
        public const int DirectionalShadowReceiverCounterFamilyCount = 16;
        public const int DirectionalShadowReceiverCounterCount = DirectionalShadowReceiverCascadeCount * DirectionalShadowReceiverCounterFamilyCount + 1;
        public const float DirectionalShadowReceiverDepthQuantizationScale = 65535.0f;
        // Appended so every pre-V2 diagnostic capture offset remains stable.
        public const int FarFieldMaterialV2CounterBase =
            DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCounterCount;
        public const int FarFieldMaterialV2CounterCount = 2;
        public const int MaterialGiCounterBase = FarFieldMaterialV2CounterBase + FarFieldMaterialV2CounterCount;
        public const int MaterialGiCounterCount = 10;
        public const int SimpleDdgiGatherRejectionCounterBase = MaterialGiCounterBase + MaterialGiCounterCount;
        public const int SimpleDdgiGatherRoleCount = 3;
        public const int SimpleDdgiGatherRejectionReasonCount = 10;
        public const int SimpleDdgiGatherRejectionCounterCount =
            SimpleDdgiGatherRoleCount * SimpleDdgiGatherRejectionReasonCount;
        public const int SimpleDdgiGatherAllFailedCounterBase =
            SimpleDdgiGatherRejectionCounterBase + SimpleDdgiGatherRejectionCounterCount;
        public const int SimpleDdgiGatherAllFailedCounterCount = SimpleDdgiGatherRoleCount;
        // Append receiver-delivery and shadow-attribution counters so every
        // existing capture offset remains stable.
        public const int DdgiDeliveryFailureCounterBase =
            SimpleDdgiGatherAllFailedCounterBase + SimpleDdgiGatherAllFailedCounterCount;
        public const int DdgiDeliveryFailureCounterCount = 1;
        public const int DdgiShadowVisibilityCounterBase =
            DdgiDeliveryFailureCounterBase + DdgiDeliveryFailureCounterCount;
        public const int DdgiShadowVisibilityCounterCount = 4;
        // Layered receivers share the transparent draw path but must not be
        // folded into the opaque 16x16 diagnostic grid. Keep a compact,
        // appended family with count/irradiance/final-luminance per class.
        public const int DdgiLayeredReceiverCounterBase =
            DdgiShadowVisibilityCounterBase + DdgiShadowVisibilityCounterCount;
        public const int DdgiLayeredReceiverCounterCount = 6;
        public const int ThinSurfaceTransportCounterBase =
            DdgiLayeredReceiverCounterBase + DdgiLayeredReceiverCounterCount;
        public const int ThinSurfaceTransportCounterCount = 18;
        // Appended to preserve every established counter offset. Each volume
        // owns blend, transport, solver-ownership, reverse-face, and analytic
        // shadow-attribution counters.
        public const int SimpleDdgiVolumeEnergyCounterBase =
            ThinSurfaceTransportCounterBase + ThinSurfaceTransportCounterCount;
        public const int SimpleDdgiVolumeEnergyCounterStride = 19;
        public const int SimpleDdgiVolumeEnergyCounterCount =
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
            SimpleDdgiVolumeEnergyCounterStride;
        // Effective diffuse-reflectance evidence. Each class stores a packed
        // luminance sum followed by its hit/sample count.
        public const int DdgiAlbedoCounterBase =
            SimpleDdgiVolumeEnergyCounterBase + SimpleDdgiVolumeEnergyCounterCount;
        public const int DdgiAlbedoCounterCount = 12;
        // Mutually exclusive forward-gather attribution. Appended so all
        // established diagnostics offsets remain capture-compatible.
        public const int SimpleDdgiGatherMultiplicityCounterBase =
            DdgiAlbedoCounterBase + DdgiAlbedoCounterCount;
        public const int SimpleDdgiGatherMultiplicityCounterCount = 9;
        // Sparse full-frame estimates for geometry-decal fragment attribution.
        // Keep this family appended to preserve every established counter ABI.
        public const int DecalFragmentAttributionCounterBase =
            SimpleDdgiGatherMultiplicityCounterBase +
            SimpleDdgiGatherMultiplicityCounterCount;
        public const int DecalFragmentAttributionCounterCount = 6;
        // Packed-storage and compact-mirror qualification. This family is
        // emitted only by detailed DDGI shader variants.
        public const int SimpleDdgiStorageValidationCounterBase =
            DecalFragmentAttributionCounterBase +
            DecalFragmentAttributionCounterCount;
        public const int SimpleDdgiStorageValidationCounterCount = 23;
        // Detailed-only, per-volume distribution/witness evidence. The first
        // 23 words are count/winner/identity metadata and the final 16 words are
        // a logarithmic irradiance-luminance histogram. Keyed atomic maxima make
        // every witness field select the same (luminance, virtual-probe) tuple
        // without a GPU spin lock.
        public const int SimpleDdgiVolumeEnergyEvidenceCounterBase =
            SimpleDdgiStorageValidationCounterBase +
            SimpleDdgiStorageValidationCounterCount;
        public const int SimpleDdgiVolumeEnergyEvidenceHistogramCount = 16;
        public const int SimpleDdgiVolumeEnergyEvidenceCounterStride =
            23 + SimpleDdgiVolumeEnergyEvidenceHistogramCount;
        public const int SimpleDdgiVolumeEnergyEvidenceCounterCount =
            GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount *
            SimpleDdgiVolumeEnergyEvidenceCounterStride;
        // Bounded exact-attribution records for directional-shadow compaction.
        // This family is appended so all existing renderer diagnostic offsets
        // remain capture-compatible.
        public const int DirectionalShadowCasterDiagnosticCounterBase =
            SimpleDdgiVolumeEnergyEvidenceCounterBase +
            SimpleDdgiVolumeEnergyEvidenceCounterCount;
        // The first three words are atomically written by the diagnostic
        // shader.  The remaining words are transfer-initialized once per
        // frame-slot, which binds a completed record bank to the exact CPU
        // shadow-data capture used to evaluate it after its fence signals.
        public const int DirectionalShadowCasterDiagnosticHeaderWordCount = 7;
        public const uint DirectionalShadowCasterDiagnosticFrameMetadataMagic =
            0x44534346u; // "DSCF"
        public const int DirectionalShadowCasterDiagnosticRecordCapacity = 16;
        public const int DirectionalShadowCasterDiagnosticRecordStride = 28;
        public const int DirectionalShadowCasterDiagnosticCounterCount =
            DirectionalShadowCasterDiagnosticHeaderWordCount +
            DirectionalShadowCasterDiagnosticRecordCapacity *
            DirectionalShadowCasterDiagnosticRecordStride;
        // Content-dependent ray-scene participation. Appended so captures made
        // against every earlier counter ABI remain byte-for-byte decodable.
        public const int DdgiGeometryParticipationCounterBase =
            DirectionalShadowCasterDiagnosticCounterBase +
            DirectionalShadowCasterDiagnosticCounterCount;
        public const int DdgiGeometryParticipationCounterCount = 12;
        // Detailed many-light estimator evidence. Quantized PDF statistics are
        // appended to preserve every established capture offset.
        public const int DdgiManyLightCounterBase =
            DdgiGeometryParticipationCounterBase +
            DdgiGeometryParticipationCounterCount;
        public const int DdgiManyLightCounterCount = 16;
        public const float DdgiManyLightPdfScale = 1_048_576.0f;
        public const float DdgiManyLightLogPdfScale = 1_024.0f;
        public const float DdgiManyLightEstimatorWeightScale = 1_024.0f;
        public const int SimpleDdgiNearVisibilityCounterBase =
            DdgiManyLightCounterBase + DdgiManyLightCounterCount;
        public const int SimpleDdgiNearVisibilityCounterCount = 10;
        public const float SimpleDdgiNearVisibilityClampSumScale = 256.0f;
        public const float SimpleDdgiNearVisibilityClampMaximumScale = 65_535.0f;
        // Debug-only probe-overlay results. The vertex pass writes one bounded
        // item per sampled identity or admitted update record. Keeping this
        // family in the existing fence-complete ring avoids a new readback or
        // synchronization path.
        public const int DebugDdgiOverlayCounterBase =
            SimpleDdgiNearVisibilityCounterBase +
            SimpleDdgiNearVisibilityCounterCount;
        public const int DebugDdgiOverlayReasonCounterBase =
            DebugDdgiOverlayCounterBase + 8;
        public const int DebugDdgiOverlayReasonCounterCount = 16;
        public const int DebugDdgiOverlayCounterCount = 27;
        // Runtime control rather than optional telemetry: every visible thick
        // transmission path reserves one task from this frame-local word.
        public const int ThickTransmissionCounterBase =
            DebugDdgiOverlayCounterBase + DebugDdgiOverlayCounterCount;
        public const int ThickTransmissionTaskCounter =
            ThickTransmissionCounterBase;
        public const int ThickTransmissionCounterCount = 1;
        public const int DdgiAreaLightCounterBase =
            ThickTransmissionCounterBase + ThickTransmissionCounterCount;
        public const int DdgiAreaLightCounterCount = 4;
        public const int TransparentReflectionCounterBase =
            DdgiAreaLightCounterBase + DdgiAreaLightCounterCount;
        public const int TransparentReflectionTaskCounter =
            TransparentReflectionCounterBase;
        public const int TransparentReflectionSsrEligibleCounter =
            TransparentReflectionCounterBase + 8;
        public const int TransparentReflectionSsrAdmittedCounter =
            TransparentReflectionCounterBase + 9;
        public const int TransparentReflectionSsrReservedSampleCounter =
            TransparentReflectionCounterBase + 10;
        public const int TransparentReflectionSsrActualSampleCounter =
            TransparentReflectionCounterBase + 11;
        public const int TransparentReflectionSsrExactHitCounter =
            TransparentReflectionCounterBase + 12;
        public const int TransparentReflectionSsrBudgetRejectedCounter =
            TransparentReflectionCounterBase + 13;
        public const int TransparentReflectionRayAdmittedCounter =
            TransparentReflectionCounterBase + 14;
        public const int TransparentReflectionRayBudgetRejectedCounter =
            TransparentReflectionCounterBase + 15;
        public const int TransparentReflectionSsrAllocationCursor =
            TransparentReflectionCounterBase + 16;
        public const int TransparentReflectionRayAllocationCursor =
            TransparentReflectionCounterBase + 17;
        public const int TransparentReflectionCounterCount = 18;
        public const int SimpleDdgiReceiverCacheCounterBase =
            TransparentReflectionCounterBase +
            TransparentReflectionCounterCount;
        public const int SimpleDdgiReceiverCacheCounterCount = 18;
        public const int CounterCount =
            SimpleDdgiReceiverCacheCounterBase +
            SimpleDdgiReceiverCacheCounterCount;
        public const float DdgiForwardEstimateWeightScale = 1024.0f;
        public const float DdgiForwardEstimateLuminanceScale = 4096.0f;
        public const float DdgiShadowHitDistanceScale = 256.0f;
        public const ulong CounterBufferSize = CounterCount * sizeof(uint);
        // Storage qualification remains appended to the public logical counter
        // ABI, but uses a small low-offset physical bank. This keeps bounded
        // detailed atomics isolated from hot renderer counters and avoids making
        // native-driver code generation depend on a growing heterogeneous SSBO.
        public const ulong SimpleDdgiStorageValidationBufferSize = 256;
        private const int SimpleDdgiStorageValidationSentinelWord =
            (int)(SimpleDdgiStorageValidationBufferSize / sizeof(uint)) - 1;
        private const uint SimpleDdgiStorageValidationSentinel = 0x51dda11du;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly BufferHandle[] _buffers = new BufferHandle[FramesInFlight];
        private readonly BufferHandle[] _simpleDdgiStorageValidationBuffers =
            new BufferHandle[FramesInFlight];
        private readonly GpuMeshletCounters[] _lastCompletedCounters = new GpuMeshletCounters[FramesInFlight];
        private readonly DdgiForwardEstimateCounters[] _lastCompletedDdgiForwardEstimateCounters = new DdgiForwardEstimateCounters[FramesInFlight];
        private readonly DdgiInvestigationCounters[] _lastCompletedDdgiInvestigationCounters = new DdgiInvestigationCounters[FramesInFlight];
        private readonly SimpleDdgiVolumeEnergyCounters[] _lastValidSimpleVolumeEnergyCounters =
            new SimpleDdgiVolumeEnergyCounters[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly bool[] _lastValidSimpleVolumeEnergyCounterPresent =
            new bool[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly uint[] _lastValidSimpleVolumeEnergyCounterAge =
            new uint[GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount];
        private readonly DirectionalShadowReceiverCounters[] _lastCompletedDirectionalShadowReceiverCounters = new DirectionalShadowReceiverCounters[FramesInFlight];
        private readonly DirectionalShadowCasterDiagnostics[] _lastCompletedDirectionalShadowCasterDiagnostics =
            new DirectionalShadowCasterDiagnostics[FramesInFlight];
        private readonly FarFieldMaterialV2Counters[] _lastCompletedFarFieldMaterialV2Counters =
            new FarFieldMaterialV2Counters[FramesInFlight];
        private readonly MaterialGiGpuCounters[] _lastCompletedMaterialGiCounters =
            new MaterialGiGpuCounters[FramesInFlight];
        private readonly ThinSurfaceTransportCounters[] _lastCompletedThinSurfaceTransportCounters =
            new ThinSurfaceTransportCounters[FramesInFlight];
        private readonly DdgiGeometryParticipationGpuCounters[]
            _lastCompletedDdgiGeometryParticipationCounters =
                new DdgiGeometryParticipationGpuCounters[FramesInFlight];
        private readonly DdgiManyLightGpuCounters[]
            _lastCompletedDdgiManyLightCounters =
                new DdgiManyLightGpuCounters[FramesInFlight];
        private readonly DdgiAreaLightGpuCounters[]
            _lastCompletedDdgiAreaLightCounters =
                new DdgiAreaLightGpuCounters[FramesInFlight];
        private readonly TransparentReflectionGpuCounters[]
            _lastCompletedTransparentReflectionCounters =
                new TransparentReflectionGpuCounters[FramesInFlight];
        private readonly SimpleDdgiReceiverCacheGpuCounters[]
            _lastCompletedSimpleDdgiReceiverCacheCounters =
                new SimpleDdgiReceiverCacheGpuCounters[FramesInFlight];
        private readonly DebugDdgiOverlayGpuCounters[]
            _lastCompletedDebugDdgiOverlayCounters =
                new DebugDdgiOverlayGpuCounters[FramesInFlight];
        private readonly ulong[] _diagnosticFrameSerials =
            new ulong[FramesInFlight];
        private readonly bool[] _diagnosticFrameSubmitted =
            new bool[FramesInFlight];
        private bool _disposed;

        public RendererDiagnosticsBuffer(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));

            for (int i = 0; i < FramesInFlight; i++)
            {
                _lastCompletedDirectionalShadowReceiverCounters[i] = DirectionalShadowReceiverCounters.Empty;
                _lastCompletedDirectionalShadowCasterDiagnostics[i] = DirectionalShadowCasterDiagnostics.Empty;
                _buffers[i] = _bufferManager.CreateBuffer(
                    CounterBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    $"Renderer Diagnostics Buffer Frame {i}",
                    MemoryBudgetCategory.DiagnosticsAndDebug);
                _simpleDdgiStorageValidationBuffers[i] = _bufferManager.CreateBuffer(
                    SimpleDdgiStorageValidationBufferSize,
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    MemoryUsage.AutoPreferHost,
                    AllocationCreateFlags.MappedBit | AllocationCreateFlags.HostAccessRandomBit,
                    $"Simple DDGI Storage Validation Buffer Frame {i}",
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
                bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.RendererDiagnosticsBufferBase + i,
                    buffer,
                    0,
                    CounterBufferSize);
                VkBuffer storageValidationBuffer = _bufferManager.GetBuffer(
                    _simpleDdgiStorageValidationBuffers[i]);
                bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.SimpleDdgiStorageValidationBufferBase + i,
                    storageValidationBuffer,
                    0,
                    SimpleDdgiStorageValidationBufferSize);
            }
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            _bufferManager.InvalidateBuffer(_buffers[frameIndex], 0, CounterBufferSize);
            uint* counters = (uint*)_bufferManager.GetMappedPointer(_buffers[frameIndex]);
            _bufferManager.InvalidateBuffer(
                _simpleDdgiStorageValidationBuffers[frameIndex],
                0,
                SimpleDdgiStorageValidationBufferSize);
            uint* storageValidationCounters = (uint*)_bufferManager.GetMappedPointer(
                _simpleDdgiStorageValidationBuffers[frameIndex]);

            _lastCompletedDirectionalShadowCasterDiagnostics[frameIndex] =
                DecodeDirectionalShadowCasterDiagnostics(new ReadOnlySpan<uint>(counters, CounterCount));
            _lastCompletedDebugDdgiOverlayCounters[frameIndex] =
                DecodeDebugDdgiOverlayCounters(
                    new ReadOnlySpan<uint>(counters, CounterCount));

            _lastCompletedThinSurfaceTransportCounters[frameIndex] = new ThinSurfaceTransportCounters(
                DetailedHitCount: counters[ThinSurfaceTransportCounterBase + 0],
                CompactHitCount: counters[ThinSurfaceTransportCounterBase + 1],
                FarFieldExcludedCount: counters[ThinSurfaceTransportCounterBase + 2],
                ReflectedDirectLuminance: counters[ThinSurfaceTransportCounterBase + 3] / DdgiForwardEstimateLuminanceScale,
                TransmittedDirectLuminance: counters[ThinSurfaceTransportCounterBase + 4] / DdgiForwardEstimateLuminanceScale,
                ReflectedRecursiveLuminance: counters[ThinSurfaceTransportCounterBase + 5] / DdgiForwardEstimateLuminanceScale,
                TransmittedRecursiveLuminance: counters[ThinSurfaceTransportCounterBase + 6] / DdgiForwardEstimateLuminanceScale,
                ColoredShadowTransmissionRayCount: counters[ThinSurfaceTransportCounterBase + 7],
                TotalThinLayersTraversed: counters[ThinSurfaceTransportCounterBase + 8],
                MaximumThinLayersTraversed: counters[ThinSurfaceTransportCounterBase + 9],
                LayerLimitTerminationCount: counters[ThinSurfaceTransportCounterBase + 10],
                LowTransmittanceTerminationCount: counters[ThinSurfaceTransportCounterBase + 11],
                ZeroRadianceOpaqueHitCount: counters[ThinSurfaceTransportCounterBase + 12],
                ZeroRadianceThinHitCount: counters[ThinSurfaceTransportCounterBase + 13],
                ZeroRadianceUnsupportedHitCount: counters[ThinSurfaceTransportCounterBase + 14],
                UnsupportedTransmissionHitCount: counters[ThinSurfaceTransportCounterBase + 15],
                EnergyClampCount: counters[ThinSurfaceTransportCounterBase + 16],
                InvalidTransmissionCount: counters[ThinSurfaceTransportCounterBase + 17]);

            _lastCompletedFarFieldMaterialV2Counters[frameIndex] = new FarFieldMaterialV2Counters(
                ConflictCount: counters[FarFieldMaterialV2CounterBase + 0],
                StalePublicationRejectCount: counters[FarFieldMaterialV2CounterBase + 1]);
            _lastCompletedMaterialGiCounters[frameIndex] = new MaterialGiGpuCounters(
                EstimatedAlphaCandidateTestCount: counters[MaterialGiCounterBase + 0],
                EstimatedAlphaCandidateRejectCount: counters[MaterialGiCounterBase + 1],
                NonFiniteMaterialOrRadianceCount: counters[MaterialGiCounterBase + 2],
                ClampedMaterialOrRadianceCount: counters[MaterialGiCounterBase + 3],
                AlphaCandidateLimitReachedCount: counters[MaterialGiCounterBase + 4],
                EstimatedDetailedTransportHitCount: counters[MaterialGiCounterBase + 5],
                EstimatedCompactTransportHitCount: counters[MaterialGiCounterBase + 6],
                EstimatedCorrectnessFallbackHitCount: counters[MaterialGiCounterBase + 7],
                EstimatedFarFieldTransportHitCount: counters[MaterialGiCounterBase + 8],
                EstimatedEmissiveSamplingInvocationCount: counters[MaterialGiCounterBase + 9]);
            _lastCompletedDdgiGeometryParticipationCounters[frameIndex] =
                new DdgiGeometryParticipationGpuCounters(
                    TransparentVisibilityLayerCount:
                        counters[DdgiGeometryParticipationCounterBase + 0],
                    TransparentVisibilityLimitCount:
                        counters[DdgiGeometryParticipationCounterBase + 1],
                    DecalCandidateCount:
                        counters[DdgiGeometryParticipationCounterBase + 2],
                    DecalRetainedCount:
                        counters[DdgiGeometryParticipationCounterBase + 3],
                    DecalAssociatedCount:
                        counters[DdgiGeometryParticipationCounterBase + 4],
                    DecalDepthRejectCount:
                        counters[DdgiGeometryParticipationCounterBase + 5],
                    DecalFacingRejectCount:
                        counters[DdgiGeometryParticipationCounterBase + 6],
                    DecalCandidateLimitCount:
                        counters[DdgiGeometryParticipationCounterBase + 7],
                    FoliageProxyHitCount:
                        counters[DdgiGeometryParticipationCounterBase + 8],
                    InvalidRayMetadataCount:
                        counters[DdgiGeometryParticipationCounterBase + 9],
                    StochasticAlphaAcceptCount:
                        counters[DdgiGeometryParticipationCounterBase + 10],
                    StochasticAlphaRejectCount:
                        counters[DdgiGeometryParticipationCounterBase + 11]);
            _lastCompletedDdgiManyLightCounters[frameIndex] =
                new DdgiManyLightGpuCounters(
                    BypassHitCount: counters[DdgiManyLightCounterBase + 0],
                    ExactHitCount: counters[DdgiManyLightCounterBase + 1],
                    TreeAttemptHitCount: counters[DdgiManyLightCounterBase + 2],
                    TreeSuccessHitCount: counters[DdgiManyLightCounterBase + 3],
                    TreeFallbackHitCount: counters[DdgiManyLightCounterBase + 4],
                    SampledLightCount: counters[DdgiManyLightCounterBase + 5],
                    DuplicateDrawCount: counters[DdgiManyLightCounterBase + 6],
                    VisibilityEvaluationCount: counters[DdgiManyLightCounterBase + 7],
                    RejectedZeroTermCount: counters[DdgiManyLightCounterBase + 8],
                    UniformRepairCount: counters[DdgiManyLightCounterBase + 9],
                    InvalidSampleOrPdfCount: counters[DdgiManyLightCounterBase + 10],
                    QuantizedPdfSum: counters[DdgiManyLightCounterBase + 11],
                    QuantizedNegativeLog2PdfSum: counters[DdgiManyLightCounterBase + 12],
                    QuantizedMaximumNegativeLog2Pdf: counters[DdgiManyLightCounterBase + 13],
                    QuantizedMaximumEstimatorWeight: counters[DdgiManyLightCounterBase + 14],
                    ExactLightEvaluationCount: counters[DdgiManyLightCounterBase + 15]);
            _lastCompletedDdgiAreaLightCounters[frameIndex] =
                new DdgiAreaLightGpuCounters(
                    SampleAttemptCount: counters[DdgiAreaLightCounterBase + 0],
                    SampleAcceptCount: counters[DdgiAreaLightCounterBase + 1],
                    InvalidPdfCount: counters[DdgiAreaLightCounterBase + 2],
                    VisibilityRayCount: counters[DdgiAreaLightCounterBase + 3]);
            _lastCompletedTransparentReflectionCounters[frameIndex] =
                new TransparentReflectionGpuCounters(
                    RayRequests:
                        counters[TransparentReflectionCounterBase + 0],
                    EstimatedSsrHits:
                        counters[TransparentReflectionCounterBase + 1],
                    EstimatedRayHits:
                        counters[TransparentReflectionCounterBase + 2],
                    EstimatedRayMisses:
                        counters[TransparentReflectionCounterBase + 3],
                    EstimatedBudgetRejected:
                        counters[TransparentReflectionCounterBase + 4],
                    EstimatedDdgiFallbacks:
                        counters[TransparentReflectionCounterBase + 5],
                    EstimatedProbeFallbacks:
                        counters[TransparentReflectionCounterBase + 6],
                    EstimatedEnvironmentFallbacks:
                        counters[TransparentReflectionCounterBase + 7])
                {
                    ExactSsrEligible =
                        counters[TransparentReflectionCounterBase + 8],
                    ExactSsrAdmitted =
                        counters[TransparentReflectionCounterBase + 9],
                    ExactSsrReservedSamples =
                        counters[TransparentReflectionCounterBase + 10],
                    ExactSsrActualSamples =
                        counters[TransparentReflectionCounterBase + 11],
                    ExactSsrHits =
                        counters[TransparentReflectionCounterBase + 12],
                    ExactSsrBudgetRejected =
                        counters[TransparentReflectionCounterBase + 13],
                    ExactRayAdmitted =
                        counters[TransparentReflectionCounterBase + 14],
                    ExactRayBudgetRejected =
                        counters[TransparentReflectionCounterBase + 15]
                };
            _lastCompletedSimpleDdgiReceiverCacheCounters[frameIndex] =
                DecodeSimpleDdgiReceiverCacheCounters(
                    new ReadOnlySpan<uint>(counters, CounterCount));

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

            bool directionalShadowReceiverCountersValid = false;
            for (int i = 0; i < DirectionalShadowReceiverCounterCount; i++)
            {
                if (counters[DirectionalShadowReceiverCounterBase + i] != 0)
                {
                    directionalShadowReceiverCountersValid = true;
                    break;
                }
            }

            if (directionalShadowReceiverCountersValid)
            {
                uint[] primarySelectionCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] projectionRejectedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] uvDepthRejectedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] fallbackCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] transitionBlendCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] primaryResolvedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] clearDepthFootprintCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] primaryFullyLitCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] primaryPartiallyShadowedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] primaryFullyShadowedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] finalFullyLitCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] finalPartiallyShadowedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                uint[] finalFullyShadowedCounts = new uint[DirectionalShadowReceiverCascadeCount];
                float[] averageReceiverDepths = new float[DirectionalShadowReceiverCascadeCount];
                float[] averageMinimumSampledDepths = new float[DirectionalShadowReceiverCascadeCount];
                float[] averageMaximumSampledDepths = new float[DirectionalShadowReceiverCascadeCount];
                for (int cascade = 0; cascade < DirectionalShadowReceiverCascadeCount; cascade++)
                {
                    primarySelectionCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + cascade];
                    projectionRejectedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount + cascade];
                    uvDepthRejectedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 2 + cascade];
                    fallbackCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 3 + cascade];
                    transitionBlendCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 4 + cascade];
                    primaryResolvedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 5 + cascade];
                    clearDepthFootprintCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 6 + cascade];
                    primaryFullyLitCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 7 + cascade];
                    primaryPartiallyShadowedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 8 + cascade];
                    primaryFullyShadowedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 9 + cascade];
                    finalFullyLitCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 10 + cascade];
                    finalPartiallyShadowedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 11 + cascade];
                    finalFullyShadowedCounts[cascade] = counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 12 + cascade];
                    uint resolvedCount = primaryResolvedCounts[cascade];
                    if (resolvedCount > 0)
                    {
                        float inverseQuantizedCount =
                            1.0f / (resolvedCount * DirectionalShadowReceiverDepthQuantizationScale);
                        averageReceiverDepths[cascade] =
                            counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 13 + cascade] *
                            inverseQuantizedCount;
                        averageMinimumSampledDepths[cascade] =
                            counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 14 + cascade] *
                            inverseQuantizedCount;
                        averageMaximumSampledDepths[cascade] =
                            counters[DirectionalShadowReceiverCounterBase + DirectionalShadowReceiverCascadeCount * 15 + cascade] *
                            inverseQuantizedCount;
                    }
                }

                _lastCompletedDirectionalShadowReceiverCounters[frameIndex] = new DirectionalShadowReceiverCounters(
                    ReadbackValid: 1,
                    PrimarySelectionCounts: primarySelectionCounts,
                    ProjectionRejectedCounts: projectionRejectedCounts,
                    UvDepthRejectedCounts: uvDepthRejectedCounts,
                    FallbackCounts: fallbackCounts,
                    TransitionBlendCounts: transitionBlendCounts,
                    PrimaryResolvedCounts: primaryResolvedCounts,
                    ClearDepthFootprintCounts: clearDepthFootprintCounts,
                    PrimaryFullyLitCounts: primaryFullyLitCounts,
                    PrimaryPartiallyShadowedCounts: primaryPartiallyShadowedCounts,
                    PrimaryFullyShadowedCounts: primaryFullyShadowedCounts,
                    FinalFullyLitCounts: finalFullyLitCounts,
                    FinalPartiallyShadowedCounts: finalPartiallyShadowedCounts,
                    FinalFullyShadowedCounts: finalFullyShadowedCounts,
                    AverageReceiverDepths: averageReceiverDepths,
                    AverageMinimumSampledDepths: averageMinimumSampledDepths,
                    AverageMaximumSampledDepths: averageMaximumSampledDepths,
                    UnresolvedCount: counters[DirectionalShadowReceiverCounterBase +
                        DirectionalShadowReceiverCascadeCount * DirectionalShadowReceiverCounterFamilyCount]);
            }
            else
            {
                _lastCompletedDirectionalShadowReceiverCounters[frameIndex] = DirectionalShadowReceiverCounters.Empty;
            }

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
            uint transparentReceiverSampleCount =
                counters[DdgiLayeredReceiverCounterBase + 0];
            uint decalReceiverSampleCount =
                counters[DdgiLayeredReceiverCounterBase + 3];
            uint traceEnergySampleCount = counters[DdgiTraceEnergyCounterBase + 0];
            uint shadowVisibilityOccludedCount = counters[DdgiShadowVisibilityCounterBase + 1];
            uint traceEarlyOutDisabledCount = counters[DdgiTraceEarlyOutCounterBase + 0];
            uint traceEarlyOutBeyondRequestCount = counters[DdgiTraceEarlyOutCounterBase + 1];
            uint traceEarlyOutResolveBoundsCount = counters[DdgiTraceEarlyOutCounterBase + 2];
            uint traceEarlyOutResolveProbeRangeCount = counters[DdgiTraceEarlyOutCounterBase + 3];
            uint traceEarlyOutResolveClipmapCellCount = counters[DdgiTraceEarlyOutCounterBase + 4];
            uint traceEarlyOutResolveClipmapRingCount = counters[DdgiTraceEarlyOutCounterBase + 5];
            uint blendEnergySampleCount = counters[DdgiBlendEnergyCounterBase + 0];
            uint simpleDdgiTransportSampleCount = counters[SimpleDdgiTransportCounterBase + 0];
            uint receiverAlbedoSampleCount = counters[DdgiAlbedoCounterBase + 1];
            uint traceOneSidedBackFaceCount = counters[DdgiAlbedoCounterBase + 3];
            uint traceOpaqueCount = counters[DdgiAlbedoCounterBase + 5];
            uint traceThinCount = counters[DdgiAlbedoCounterBase + 7];
            uint traceUnsupportedTransmissionCount = counters[DdgiAlbedoCounterBase + 9];
            uint traceReflectDisabledCount = counters[DdgiAlbedoCounterBase + 11];
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
            bool volumeEnergyValid = false;
            for (int i = 0; i < SimpleDdgiVolumeEnergyCounterCount; i++)
            {
                if (counters[SimpleDdgiVolumeEnergyCounterBase + i] != 0)
                {
                    volumeEnergyValid = true;
                    investigationValid = true;
                    break;
                }
            }
            for (int i = 0;
                 i < SimpleDdgiVolumeEnergyEvidenceCounterCount && !volumeEnergyValid;
                 i++)
            {
                if (counters[SimpleDdgiVolumeEnergyEvidenceCounterBase + i] != 0)
                {
                    volumeEnergyValid = true;
                    investigationValid = true;
                }
            }
            if (!volumeEnergyValid)
            {
                for (int i = 0;
                     i < _lastValidSimpleVolumeEnergyCounterPresent.Length;
                     i++)
                {
                    if (_lastValidSimpleVolumeEnergyCounterPresent[i])
                    {
                        volumeEnergyValid = true;
                        break;
                    }
                }
            }
            for (int i = 0; i < DecalFragmentAttributionCounterCount; i++)
            {
                if (counters[DecalFragmentAttributionCounterBase + i] != 0)
                {
                    investigationValid = true;
                    break;
                }
            }
#if NJULF_DETAILED_INVESTIGATION
            // A transfer-written marker distinguishes a valid all-zero result
            // from an unreadable or wrong-frame bank. The last aligned word is
            // outside the shader counter family and is checked after the frame
            // fence before any qualification snapshot is published.
            bool storageValidationValid =
                storageValidationCounters[SimpleDdgiStorageValidationSentinelWord] ==
                    SimpleDdgiStorageValidationSentinel;
#else
            bool storageValidationValid = false;
#endif
            if (storageValidationValid)
                investigationValid = true;

#if DEBUG || NJULF_DETAILED_INVESTIGATION
            // This bank is reset and frame-stamped on the CPU before submission,
            // then read only after the owning frame fence. Unlike the optional
            // shader dispositions, an all-zero B4 result is still valid evidence.
            bool nearVisibilityReadbackValid =
                _diagnosticFrameSubmitted[frameIndex];
#else
            bool nearVisibilityReadbackValid = false;
#endif

            float invInvestigationSampleCount = ddgiInvestigationSampleCount > 0 ? 1.0f / ddgiInvestigationSampleCount : 0.0f;
            float invSimpleVisibilitySampleCount = simpleVisibilitySampleCount > 0 ? 1.0f / simpleVisibilitySampleCount : 0.0f;
            float invSkyVisibilitySampleCount = skyVisibilitySampleCount > 0 ? 1.0f / skyVisibilitySampleCount : 0.0f;
            float invShadowVisibilityOccludedCount = shadowVisibilityOccludedCount > 0
                ? 1.0f / shadowVisibilityOccludedCount
                : 0.0f;
            uint[] simpleVolumePrimaryGatherCounts = investigationValid
                ? new uint[SimpleDdgiVolumeGatherCounterCount]
                : Array.Empty<uint>();
            uint[] simpleVolumeSampledGatherCounts = investigationValid
                ? new uint[SimpleDdgiVolumeGatherCounterCount]
                : Array.Empty<uint>();
            SimpleDdgiVolumeEnergyCounters[] simpleVolumeEnergyCounters = volumeEnergyValid
                ? new SimpleDdgiVolumeEnergyCounters[SimpleDdgiVolumeGatherCounterCount]
                : Array.Empty<SimpleDdgiVolumeEnergyCounters>();
            uint[] simpleGatherPrimaryRejectionCounts = investigationValid
                ? new uint[SimpleDdgiGatherRejectionReasonCount]
                : Array.Empty<uint>();
            uint[] simpleGatherFallbackRejectionCounts = investigationValid
                ? new uint[SimpleDdgiGatherRejectionReasonCount]
                : Array.Empty<uint>();
            uint[] simpleGatherRecoveryRejectionCounts = investigationValid
                ? new uint[SimpleDdgiGatherRejectionReasonCount]
                : Array.Empty<uint>();
            uint[] directionAngularHistogram = storageValidationValid
                ? new uint[8]
                : Array.Empty<uint>();
            for (int i = 0; i < simpleVolumePrimaryGatherCounts.Length; i++)
            {
                simpleVolumePrimaryGatherCounts[i] = counters[SimpleDdgiVolumePrimaryGatherCounterBase + i];
                simpleVolumeSampledGatherCounts[i] = counters[SimpleDdgiVolumeSampledGatherCounterBase + i];
            }
            for (int i = 0; i < simpleVolumeEnergyCounters.Length; i++)
            {
                SimpleDdgiVolumeEnergyCounters current =
                    ReadSimpleDdgiVolumeEnergyCounters(counters, i);
                if (current.EvidenceSampleCount != 0)
                {
                    _lastValidSimpleVolumeEnergyCounters[i] = current;
                    _lastValidSimpleVolumeEnergyCounterPresent[i] = true;
                    _lastValidSimpleVolumeEnergyCounterAge[i] = 0;
                    simpleVolumeEnergyCounters[i] = current;
                }
                else if (_lastValidSimpleVolumeEnergyCounterPresent[i])
                {
                    uint age = _lastValidSimpleVolumeEnergyCounterAge[i] == uint.MaxValue
                        ? uint.MaxValue
                        : _lastValidSimpleVolumeEnergyCounterAge[i] + 1u;
                    _lastValidSimpleVolumeEnergyCounterAge[i] = age;
                    SimpleDdgiVolumeEnergyCounters retained =
                        _lastValidSimpleVolumeEnergyCounters[i] with
                        {
                            EvidenceAgeFrames = age
                        };
                    _lastValidSimpleVolumeEnergyCounters[i] = retained;
                    simpleVolumeEnergyCounters[i] = HasSimpleDdgiVolumeEnergySamples(current)
                        ? MergeSimpleDdgiVolumeEnergyEvidence(current, retained)
                        : retained;
                }
                else
                {
                    simpleVolumeEnergyCounters[i] = current;
                }
            }
            for (int reason = 0; reason < simpleGatherPrimaryRejectionCounts.Length; reason++)
            {
                simpleGatherPrimaryRejectionCounts[reason] =
                    counters[SimpleDdgiGatherRejectionCounterBase + reason];
                simpleGatherFallbackRejectionCounts[reason] =
                    counters[SimpleDdgiGatherRejectionCounterBase +
                        SimpleDdgiGatherRejectionReasonCount + reason];
                simpleGatherRecoveryRejectionCounts[reason] =
                    counters[SimpleDdgiGatherRejectionCounterBase +
                    SimpleDdgiGatherRejectionReasonCount * 2 + reason];
            }
            for (int bucket = 0; bucket < directionAngularHistogram.Length; bucket++)
            {
                directionAngularHistogram[bucket] = storageValidationCounters[13 + bucket];
            }

            _lastCompletedDdgiInvestigationCounters[frameIndex] =
                investigationValid || volumeEnergyValid || nearVisibilityReadbackValid
                ? new DdgiInvestigationCounters(
                    ReadbackValid: investigationValid ? 1 : 0,
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
                    SimpleVolumeEnergyCounters: simpleVolumeEnergyCounters,
                    SimpleGatherPrimaryRejectionCounts: simpleGatherPrimaryRejectionCounts,
                    SimpleGatherFallbackRejectionCounts: simpleGatherFallbackRejectionCounts,
                    SimpleGatherRecoveryRejectionCounts: simpleGatherRecoveryRejectionCounts,
                    SimpleGatherPrimaryAllFailedCount: counters[SimpleDdgiGatherAllFailedCounterBase],
                    SimpleGatherFallbackAllFailedCount: counters[SimpleDdgiGatherAllFailedCounterBase + 1],
                    SimpleGatherRecoveryAllFailedCount: counters[SimpleDdgiGatherAllFailedCounterBase + 2],
                    FarFieldStepBucket0Count: counters[FarFieldCounterBase + 5],
                    FarFieldStepBucket1Count: counters[FarFieldCounterBase + 6],
                    FarFieldStepBucket2Count: counters[FarFieldCounterBase + 7],
                    FarFieldStepBucket3Count: counters[FarFieldCounterBase + 8],
                    FarFieldStepBucket4Count: counters[FarFieldCounterBase + 9])
                {
                    EnergyReadbackValid = volumeEnergyValid ? 1 : 0,
                    GatherMultiplicity = new SimpleDdgiGatherMultiplicityCounters(
                        OneGatherPixelCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 0],
                        TwoGatherPixelCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 1],
                        RecoveryGatherPixelCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 2],
                        RingTransitionBlendCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 3],
                        MissingOrInvalidPrimarySupportCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 4],
                        RecoveryCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 5],
                        CoverageEdgeCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 6],
                        PrimaryOwnershipBelowThresholdCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 7],
                        DebugOrDiagnosticOnlyCount: counters[SimpleDdgiGatherMultiplicityCounterBase + 8]),
                    DecalFragmentAttribution = new DecalFragmentAttributionCounters(
                        EstimatedInvocationCount: counters[DecalFragmentAttributionCounterBase + 0],
                        EstimatedBackFaceKilledCount: counters[DecalFragmentAttributionCounterBase + 1],
                        EstimatedCoverageKilledCount: counters[DecalFragmentAttributionCounterBase + 2],
                        EstimatedSurvivingCount: counters[DecalFragmentAttributionCounterBase + 3],
                        EstimatedDdgiGatherCount: counters[DecalFragmentAttributionCounterBase + 4],
                        EstimatedShadowEvaluationCount: counters[DecalFragmentAttributionCounterBase + 5]),
                    StorageValidation = storageValidationValid
                        ? new SimpleDdgiStorageValidationCounters(
                            ReadbackValid: 1,
                            MirrorInteriorOpportunityCount: storageValidationCounters[0],
                            MirrorImageHitCount: storageValidationCounters[1],
                            MirrorSeamFallbackCount: storageValidationCounters[2],
                            MirrorUnmirroredFallbackCount: storageValidationCounters[3],
                            MirrorInvalidMapFallbackCount: storageValidationCounters[4],
                            CachePackAttemptCount: storageValidationCounters[5],
                            CachePackNonFiniteCount: storageValidationCounters[6],
                            CachePackRadianceSaturationCount: storageValidationCounters[7],
                            CachePackMaximumRadianceError: storageValidationCounters[8] / 1_000_000.0f,
                            CachePackMaximumDistanceError: storageValidationCounters[9] / 1_000_000.0f,
                            DirectionComparisonSampleCount: storageValidationCounters[10],
                            DirectionEpochMismatchCount: storageValidationCounters[11],
                            DirectionMaximumAngularErrorRadians: storageValidationCounters[12] / 1_000_000.0f,
                            DirectionAngularErrorHistogram: directionAngularHistogram,
                            InvalidSourceEpochCount: storageValidationCounters[21],
                            InvalidHitKindCount: storageValidationCounters[22])
                        {
                            FrameSerial = _diagnosticFrameSerials[frameIndex]
                        }
                        : SimpleDdgiStorageValidationCounters.Empty,
                    NearVisibility = DecodeSimpleDdgiNearVisibilityCounters(
                        counters,
                        frameIndex,
                        nearVisibilityReadbackValid)
                }
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
                simpleDdgiTransportSampleCount > 0 ||
                traceRingMismatchSampleValid > 0 ||
                traceRingMismatchCorrectedCount > 0 ||
                transparentReceiverSampleCount > 0 ||
                decalReceiverSampleCount > 0)
            {
                float invSampleCount = sampleCount > 0 ? 1.0f / sampleCount : 0.0f;
                float invVisibilityMomentSampleCount = visibilityMomentSampleCount > 0 ? 1.0f / visibilityMomentSampleCount : 0.0f;
                float invProbeQualitySampleCount = probeQualitySampleCount > 0 ? 1.0f / probeQualitySampleCount : 0.0f;
                float invClipmapInfoPrimaryAttemptCount = clipmapInfoPrimaryAttemptCount > 0 ? 1.0f / clipmapInfoPrimaryAttemptCount : 0.0f;
                float invClipmapInfoPrimaryOkCount = clipmapInfoPrimaryOkCount > 0 ? 1.0f / clipmapInfoPrimaryOkCount : 0.0f;
                float invTraceEnergySampleCount = traceEnergySampleCount > 0 ? 1.0f / traceEnergySampleCount : 0.0f;
                float invBlendEnergySampleCount = blendEnergySampleCount > 0 ? 1.0f / blendEnergySampleCount : 0.0f;
                float invSimpleDdgiTransportSampleCount = simpleDdgiTransportSampleCount > 0
                    ? 1.0f / simpleDdgiTransportSampleCount
                    : 0.0f;
                float invTransparentReceiverSampleCount =
                    transparentReceiverSampleCount > 0
                        ? 1.0f / transparentReceiverSampleCount
                        : 0.0f;
                float invDecalReceiverSampleCount =
                    decalReceiverSampleCount > 0
                        ? 1.0f / decalReceiverSampleCount
                        : 0.0f;
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = new DdgiForwardEstimateCounters(
                    ReadbackValid: sampleCount > 0 || clipmapInfoPrimaryAttemptCount > 0 || traceEnergySampleCount > 0 || traceEarlyOutDisabledCount > 0 || traceEarlyOutBeyondRequestCount > 0 || traceEarlyOutResolveBoundsCount > 0 || traceEarlyOutResolveProbeRangeCount > 0 || traceEarlyOutResolveClipmapCellCount > 0 || traceEarlyOutResolveClipmapRingCount > 0 || blendEnergySampleCount > 0 || simpleDdgiTransportSampleCount > 0 || traceRingMismatchSampleValid > 0 || traceRingMismatchCorrectedCount > 0 || transparentReceiverSampleCount > 0 || decalReceiverSampleCount > 0 ? 1 : 0,
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
                    ReceiverDiffuseReflectanceLuminanceAverage: counters[DdgiAlbedoCounterBase + 0] /
                        DdgiForwardEstimateLuminanceScale *
                        (receiverAlbedoSampleCount > 0 ? 1.0f / receiverAlbedoSampleCount : 0.0f),
                    ReceiverDiffuseReflectanceSampleCount: receiverAlbedoSampleCount,
                    TraceOneSidedBackFaceAlbedoLuminanceAverage: ReadAlbedoAverage(counters, 2, traceOneSidedBackFaceCount),
                    TraceOneSidedBackFaceHitCount: traceOneSidedBackFaceCount,
                    TraceOpaqueAlbedoLuminanceAverage: ReadAlbedoAverage(counters, 4, traceOpaqueCount),
                    TraceOpaqueHitCount: traceOpaqueCount,
                    TraceThinSurfaceAlbedoLuminanceAverage: ReadAlbedoAverage(counters, 6, traceThinCount),
                    TraceThinSurfaceHitCount: traceThinCount,
                    TraceUnsupportedTransmissionAlbedoLuminanceAverage: ReadAlbedoAverage(counters, 8, traceUnsupportedTransmissionCount),
                    TraceUnsupportedTransmissionHitCount: traceUnsupportedTransmissionCount,
                    TraceReflectDisabledAlbedoLuminanceAverage: ReadAlbedoAverage(counters, 10, traceReflectDisabledCount),
                    TraceReflectDisabledHitCount: traceReflectDisabledCount,
                    SampleCount: sampleCount,
                    ZeroSupportButSpatiallyCoveredCount: counters[DdgiForwardEstimateCounterBase + 10],
                    ZeroEffectiveButSpatiallyCoveredCount: counters[DdgiForwardEstimateCounterBase + 11],
                    HighOwnershipLowDeliveredIndirectCount: counters[DdgiDeliveryFailureCounterBase + 0],
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
                    ShadowVisibilityRayCount: counters[DdgiShadowVisibilityCounterBase + 0],
                    ShadowVisibilityOccludedCount: shadowVisibilityOccludedCount,
                    ShadowVisibilityNearHitCount: counters[DdgiShadowVisibilityCounterBase + 2],
                    ShadowVisibilityCommittedHitDistanceAverage:
                        counters[DdgiShadowVisibilityCounterBase + 3] /
                        DdgiShadowHitDistanceScale * invShadowVisibilityOccludedCount,
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
                    BlendEnergyFireflySuppressedCount: counters[DdgiBlendEnergyCounterBase + 6],
                    SimpleDdgiTransportEnergySampleCount: simpleDdgiTransportSampleCount,
                    SimpleDdgiTransportSourceCacheHitCount: counters[SimpleDdgiTransportCounterBase + 1],
                    SimpleDdgiTransportSourceCacheMissCount: counters[SimpleDdgiTransportCounterBase + 2],
                    SimpleDdgiTransportBounceLuminanceAverage: counters[SimpleDdgiTransportCounterBase + 3] / DdgiForwardEstimateLuminanceScale * invSimpleDdgiTransportSampleCount,
                    SimpleDdgiTransportSourceLuminanceAverage: counters[SimpleDdgiTransportCounterBase + 4] / DdgiForwardEstimateLuminanceScale * invSimpleDdgiTransportSampleCount,
                    SimpleDdgiTransportTotalLuminanceAverage: counters[SimpleDdgiTransportCounterBase + 5] / DdgiForwardEstimateLuminanceScale * invSimpleDdgiTransportSampleCount,
                    TransparentReceiverSampleCount: transparentReceiverSampleCount,
                    TransparentReceiverIrradianceLuminanceAverage:
                        counters[DdgiLayeredReceiverCounterBase + 1] /
                        DdgiForwardEstimateLuminanceScale *
                        invTransparentReceiverSampleCount,
                    TransparentReceiverFinalLuminanceAverage:
                        counters[DdgiLayeredReceiverCounterBase + 2] /
                        DdgiForwardEstimateLuminanceScale *
                        invTransparentReceiverSampleCount,
                    DecalReceiverSampleCount: decalReceiverSampleCount,
                    DecalReceiverIrradianceLuminanceAverage:
                        counters[DdgiLayeredReceiverCounterBase + 4] /
                        DdgiForwardEstimateLuminanceScale *
                        invDecalReceiverSampleCount,
                    DecalReceiverFinalLuminanceAverage:
                        counters[DdgiLayeredReceiverCounterBase + 5] /
                        DdgiForwardEstimateLuminanceScale *
                        invDecalReceiverSampleCount);
            }
            else
            {
                _lastCompletedDdgiForwardEstimateCounters[frameIndex] = DdgiForwardEstimateCounters.Empty;
            }

            // A ring slot's submission stamp is one-shot evidence. Keeping it set would make
            // a later read without a matching submission look like a valid all-zero frame.
            _diagnosticFrameSubmitted[frameIndex] = false;
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

        public DirectionalShadowReceiverCounters GetLastCompletedDirectionalShadowReceiverCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDirectionalShadowReceiverCounters[frameIndex];
        }

        public DirectionalShadowCasterDiagnostics GetLastCompletedDirectionalShadowCasterDiagnostics(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDirectionalShadowCasterDiagnostics[frameIndex];
        }

        public FarFieldMaterialV2Counters GetLastCompletedFarFieldMaterialV2Counters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedFarFieldMaterialV2Counters[frameIndex];
        }

        public MaterialGiGpuCounters GetLastCompletedMaterialGiCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedMaterialGiCounters[frameIndex];
        }

        public ThinSurfaceTransportCounters GetLastCompletedThinSurfaceTransportCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedThinSurfaceTransportCounters[frameIndex];
        }

        public DdgiGeometryParticipationGpuCounters
            GetLastCompletedDdgiGeometryParticipationCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiGeometryParticipationCounters[frameIndex];
        }

        public DdgiManyLightGpuCounters GetLastCompletedDdgiManyLightCounters(
            int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiManyLightCounters[frameIndex];
        }

        public DdgiAreaLightGpuCounters GetLastCompletedDdgiAreaLightCounters(
            int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDdgiAreaLightCounters[frameIndex];
        }

        public TransparentReflectionGpuCounters
            GetLastCompletedTransparentReflectionCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedTransparentReflectionCounters[frameIndex];
        }

        public SimpleDdgiReceiverCacheGpuCounters
            GetLastCompletedSimpleDdgiReceiverCacheCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedSimpleDdgiReceiverCacheCounters[frameIndex];
        }

        internal static SimpleDdgiReceiverCacheGpuCounters
            DecodeSimpleDdgiReceiverCacheCounters(
                ReadOnlySpan<uint> counters)
        {
            if (counters.Length < CounterCount)
                throw new ArgumentException(
                    "Renderer diagnostic counter span is incomplete.",
                    nameof(counters));

            int counterBase = SimpleDdgiReceiverCacheCounterBase;
            if (counters[counterBase] == 0u)
                return SimpleDdgiReceiverCacheGpuCounters.Unavailable;

            return new SimpleDdgiReceiverCacheGpuCounters(
                ReadbackValid: 1,
                ResolveCandidateCount: counters[counterBase + 1],
                ResolveValidCount: counters[counterBase + 2],
                ResolveInvalidOrNonFiniteRejectCount: counters[counterBase + 3],
                ResolveDepthOrPositionRejectCount: counters[counterBase + 4],
                ResolvePlaneRejectCount: counters[counterBase + 5],
                ResolveNormalRejectCount: counters[counterBase + 6],
                ResolveInsufficientSupportRejectCount: counters[counterBase + 7],
                ForwardCandidateCount: counters[counterBase + 8],
                ForwardAcceptedCount: counters[counterBase + 9],
                ForwardInvalidOrNonFiniteRejectCount: counters[counterBase + 10],
                ForwardDepthOrPositionRejectCount: counters[counterBase + 11],
                ForwardPlaneRejectCount: counters[counterBase + 12],
                ForwardNormalRejectCount: counters[counterBase + 13],
                ForwardInsufficientSupportRejectCount: counters[counterBase + 14],
                ExactFallbackFragmentCount: counters[counterBase + 15],
                LegacyFragmentCount: counters[counterBase + 16],
                DirectionalCacheEvaluationCount: counters[counterBase + 17]);
        }

        public DebugDdgiOverlayGpuCounters
            GetLastCompletedDebugDdgiOverlayCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _lastCompletedDebugDdgiOverlayCounters[frameIndex];
        }

        internal static DebugDdgiOverlayGpuCounters DecodeDebugDdgiOverlayCounters(
            ReadOnlySpan<uint> counters)
        {
            if (counters.Length < DebugDdgiOverlayCounterBase +
                    DebugDdgiOverlayCounterCount)
            {
                return DebugDdgiOverlayGpuCounters.Empty;
            }

            int baseIndex = DebugDdgiOverlayCounterBase;
            uint encodedMode = counters[baseIndex];
            if (encodedMode == 0u || encodedMode > 24u)
                return DebugDdgiOverlayGpuCounters.Empty;

            int reasons = DebugDdgiOverlayReasonCounterBase;
            var reasonCounts = new DebugDdgiUpdateReasonCounts(
                counters[reasons + 0],
                counters[reasons + 1],
                counters[reasons + 2],
                counters[reasons + 3],
                counters[reasons + 4],
                counters[reasons + 5],
                counters[reasons + 6],
                counters[reasons + 7],
                counters[reasons + 8],
                counters[reasons + 9],
                counters[reasons + 10],
                counters[reasons + 11],
                counters[reasons + 12],
                counters[reasons + 13],
                counters[reasons + 14],
                counters[reasons + 15],
                counters[baseIndex + 7]);
            return new DebugDdgiOverlayGpuCounters(
                Valid: true,
                Mode: (DebugOverlayMode)(encodedMode - 1u),
                DrawnMarkerCount: counters[baseIndex + 1],
                FilteredMarkerCount: counters[baseIndex + 2],
                NonresidentMarkerCount: counters[baseIndex + 3],
                StaleMappingCount: counters[baseIndex + 4],
                StateUnavailableMarkerCount: counters[baseIndex + 5],
                InvalidTransactionCount: counters[baseIndex + 6],
                VolumeTableGeneration: counters[baseIndex + 24],
                SchedulerResourceGeneration: counters[baseIndex + 25],
                ResidencyResourceGeneration: counters[baseIndex + 26],
                UpdateReasons: reasonCounts);
        }

        private static int DecodeSignedCounter(uint value)
        {
            return unchecked((int)value);
        }

        internal static DirectionalShadowCasterDiagnostics DecodeDirectionalShadowCasterDiagnostics(
            ReadOnlySpan<uint> counters)
        {
            const int headerRecordCountOffset = 0;
            const int headerSampledCountOffset = 1;
            const int headerDroppedCountOffset = 2;
            const int headerFrameSerialLowOffset = 3;
            const int headerFrameSerialHighOffset = 4;
            const int headerResourceGenerationOffset = 5;
            const int headerFrameMetadataMagicOffset = 6;
            int requiredLength = DirectionalShadowCasterDiagnosticCounterBase +
                DirectionalShadowCasterDiagnosticCounterCount;
            if (counters.Length < requiredLength)
                throw new ArgumentException("The renderer diagnostics span does not contain the directional-shadow caster bank.", nameof(counters));

            int headerBase = DirectionalShadowCasterDiagnosticCounterBase;
            uint rawRecordCount = counters[headerBase + headerRecordCountOffset];
            uint sampledCandidateCount = counters[headerBase + headerSampledCountOffset];
            uint droppedRecordCount = counters[headerBase + headerDroppedCountOffset];
            ulong gpuFrameSerial = counters[headerBase + headerFrameSerialLowOffset] |
                ((ulong)counters[headerBase + headerFrameSerialHighOffset] << 32);
            uint gpuResourceGeneration = counters[headerBase + headerResourceGenerationOffset];
            bool frameMetadataValid = counters[headerBase + headerFrameMetadataMagicOffset] ==
                DirectionalShadowCasterDiagnosticFrameMetadataMagic;
            if (rawRecordCount == 0u && sampledCandidateCount == 0u && droppedRecordCount == 0u)
                return DirectionalShadowCasterDiagnostics.Empty;

            int recordCount = checked((int)Math.Min(
                rawRecordCount,
                (uint)DirectionalShadowCasterDiagnosticRecordCapacity));
            var records = new DirectionalShadowCasterAttribution[recordCount];
            int recordBase = headerBase + DirectionalShadowCasterDiagnosticHeaderWordCount;
            for (int record = 0; record < recordCount; record++)
            {
                int offset = recordBase + record * DirectionalShadowCasterDiagnosticRecordStride;
                uint rawClass = counters[offset + 4];
                DirectionalShadowCasterClass casterClass = rawClass <= (uint)DirectionalShadowCasterClass.Foliage
                    ? (DirectionalShadowCasterClass)rawClass
                    : DirectionalShadowCasterClass.Unknown;
                uint rejectingPlane = counters[offset + 11];
                var signedDistances = new float[6];
                for (int plane = 0; plane < signedDistances.Length; plane++)
                    signedDistances[plane] = BitConverter.UInt32BitsToSingle(counters[offset + 21 + plane]);

                records[record] = new DirectionalShadowCasterAttribution(
                    ObjectId: counters[offset + 0],
                    InstanceId: counters[offset + 1],
                    MeshletId: counters[offset + 2],
                    SelectedLod: counters[offset + 3],
                    CasterClass: casterClass,
                    CascadeIndex: counters[offset + 5],
                    CandidateIndex: counters[offset + 6],
                    EligibilityFlags: counters[offset + 7],
                    MatrixHash: counters[offset + 8] | ((ulong)counters[offset + 9] << 32),
                    Accepted: counters[offset + 10] != 0u ? 1 : 0,
                    FirstRejectingPlane: rejectingPlane == uint.MaxValue
                        ? -1
                        : checked((int)rejectingPlane),
                    FirstRejectingSignedDistance: BitConverter.UInt32BitsToSingle(counters[offset + 12]),
                    WorldCenter: new Vector3(
                        BitConverter.UInt32BitsToSingle(counters[offset + 13]),
                        BitConverter.UInt32BitsToSingle(counters[offset + 14]),
                        BitConverter.UInt32BitsToSingle(counters[offset + 15])),
                    WorldRadius: BitConverter.UInt32BitsToSingle(counters[offset + 16]),
                    ClipCenter: new Vector4(
                        BitConverter.UInt32BitsToSingle(counters[offset + 17]),
                        BitConverter.UInt32BitsToSingle(counters[offset + 18]),
                        BitConverter.UInt32BitsToSingle(counters[offset + 19]),
                        BitConverter.UInt32BitsToSingle(counters[offset + 20])),
                    SignedPlaneDistances: signedDistances);
            }

            return new DirectionalShadowCasterDiagnostics(
                ReadbackValid: 1,
                SampledCandidateCount: sampledCandidateCount,
                DroppedRecordCount: droppedRecordCount,
                Records: records)
            {
                GpuFrameSerial = gpuFrameSerial,
                GpuResourceGeneration = gpuResourceGeneration,
                FrameMetadataValid = frameMetadataValid ? 1 : 0
            };
        }

        private static unsafe float ReadAlbedoAverage(uint* counters, int relativeSumOffset, uint count)
        {
            return count > 0
                ? counters[DdgiAlbedoCounterBase + relativeSumOffset] /
                    DdgiForwardEstimateLuminanceScale / count
                : 0.0f;
        }

        private SimpleDdgiNearVisibilityGpuCounters DecodeSimpleDdgiNearVisibilityCounters(
            uint* counters,
            int frameIndex,
            bool readbackValid)
        {
            if (!readbackValid)
                return SimpleDdgiNearVisibilityGpuCounters.Unavailable;

            uint receiverEvaluationCount =
                counters[SimpleDdgiNearVisibilityCounterBase + 7];
            float averageClamp = receiverEvaluationCount > 0
                ? counters[SimpleDdgiNearVisibilityCounterBase + 8] /
                  SimpleDdgiNearVisibilityClampSumScale /
                  receiverEvaluationCount
                : 0.0f;

            return new SimpleDdgiNearVisibilityGpuCounters(
                ReadbackValid: 1,
                FrameSerial: _diagnosticFrameSerials[frameIndex],
                CoherentClusterTexelCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 0],
                RejectedClusterTexelCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 1],
                InsufficientConfidenceTapCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 2],
                InvalidDepthTapCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 3],
                NoMomentDiscrepancyTapCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 4],
                ReceiverInFrontTapCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 5],
                AppliedEvaluationCount:
                    counters[SimpleDdgiNearVisibilityCounterBase + 6],
                EvaluationCount: receiverEvaluationCount,
                AverageClamp: averageClamp,
                MaximumClamp:
                    counters[SimpleDdgiNearVisibilityCounterBase + 9] /
                    SimpleDdgiNearVisibilityClampMaximumScale);
        }

        private static SimpleDdgiVolumeEnergyCounters ReadSimpleDdgiVolumeEnergyCounters(
            uint* counters,
            int volumeIndex)
        {
            int counterBase = SimpleDdgiVolumeEnergyCounterBase +
                volumeIndex * SimpleDdgiVolumeEnergyCounterStride;
            uint blendSamples = counters[counterBase + 0];
            uint transportSamples = counters[counterBase + 3];
            uint solverSamples = counters[counterBase + 7];
            uint shadowOccluded = counters[counterBase + 12];
            float invBlend = blendSamples > 0 ? 1.0f / blendSamples : 0.0f;
            float invTransport = transportSamples > 0 ? 1.0f / transportSamples : 0.0f;
            float invSolver = solverSamples > 0 ? 1.0f / solverSamples : 0.0f;
            float invShadowOccluded = shadowOccluded > 0 ? 1.0f / shadowOccluded : 0.0f;
            int evidenceBase = SimpleDdgiVolumeEnergyEvidenceCounterBase +
                volumeIndex * SimpleDdgiVolumeEnergyEvidenceCounterStride;
            uint evidenceSamples = counters[evidenceBase + 0];
            uint winnerKey = counters[evidenceBase + 1];
            bool witnessCoherent = evidenceSamples != 0 && winnerKey != 0;
            uint physicalProbe = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 2, 3, winnerKey, ref witnessCoherent);
            uint virtualPage = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 5, 3, winnerKey, ref witnessCoherent);
            uint physicalPage = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 8, 3, winnerKey, ref witnessCoherent);
            uint sourceGeneration = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 11, 6, winnerKey, ref witnessCoherent);
            uint visibilityMean = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 17, 3, winnerKey, ref witnessCoherent);
            uint visibilitySecond = DecodeSimpleDdgiEnergyEvidenceChunks(
                counters, evidenceBase + 20, 3, winnerKey, ref witnessCoherent);
            uint virtualProbe = winnerKey & 0x7fffu;
            uint luminanceCode = winnerKey >> 15;

            return new SimpleDdgiVolumeEnergyCounters(
                BlendSampleCount: blendSamples,
                BlendIrradianceLuminanceAverage: counters[counterBase + 1] /
                    DdgiForwardEstimateLuminanceScale * invBlend,
                BlendConfidenceAverage: counters[counterBase + 2] /
                    DdgiForwardEstimateWeightScale * invBlend,
                TransportSampleCount: transportSamples,
                TransportSourceLuminanceAverage: counters[counterBase + 4] /
                    DdgiForwardEstimateLuminanceScale * invTransport,
                TransportBounceLuminanceAverage: counters[counterBase + 5] /
                    DdgiForwardEstimateLuminanceScale * invTransport,
                TransportTotalLuminanceAverage: counters[counterBase + 6] /
                    DdgiForwardEstimateLuminanceScale * invTransport,
                SolverGatherSampleCount: solverSamples,
                SolverOwnershipAverage: counters[counterBase + 8] /
                    DdgiForwardEstimateWeightScale * invSolver,
                SolverFallbackWeightAverage: counters[counterBase + 9] /
                    DdgiForwardEstimateWeightScale * invSolver,
                OneSidedBackFaceRayCount: counters[counterBase + 10],
                ShadowVisibilityRayCount: counters[counterBase + 11],
                ShadowVisibilityOccludedCount: shadowOccluded,
                ShadowVisibilityBelowRayTMinCount: counters[counterBase + 13],
                ShadowVisibilityBelowDoubleNormalOffsetCount: counters[counterBase + 14],
                ShadowVisibilityBelowProbeSpacingCount: counters[counterBase + 15],
                ShadowVisibilityBeyondProbeSpacingCount: counters[counterBase + 16],
                ShadowVisibilitySameInstanceCount: counters[counterBase + 17],
                ShadowVisibilityCommittedHitDistanceAverage: counters[counterBase + 18] /
                    DdgiShadowHitDistanceScale * invShadowOccluded,
                EvidenceSampleCount: evidenceSamples,
                IrradianceLuminanceP95: ReadSimpleDdgiEnergyEvidencePercentile(
                    counters,
                    evidenceBase + 23,
                    evidenceSamples,
                    0.95),
                IrradianceLuminanceP99: ReadSimpleDdgiEnergyEvidencePercentile(
                    counters,
                    evidenceBase + 23,
                    evidenceSamples,
                    0.99),
                IrradianceLuminanceMaximum:
                    DecodeSimpleDdgiEnergyEvidenceLuminance(luminanceCode),
                MaximumVirtualProbeIndex: witnessCoherent
                    ? virtualProbe
                    : uint.MaxValue,
                MaximumVirtualPageIndex: witnessCoherent && virtualPage != 0
                    ? virtualPage - 1u
                    : uint.MaxValue,
                MaximumPhysicalProbeIndex: witnessCoherent && physicalProbe != 0
                    ? physicalProbe - 1u
                    : uint.MaxValue,
                MaximumPhysicalPageIndex: witnessCoherent && physicalPage != 0
                    ? physicalPage - 1u
                    : uint.MaxValue,
                MaximumVisibilityMomentMean: visibilityMean / 65535.0f * 256.0f,
                MaximumVisibilityMomentSecond: visibilitySecond / 65535.0f * 65536.0f,
                MaximumSourceLightingGeneration: sourceGeneration,
                MaximumWitnessCoherent: witnessCoherent ? 1 : 0,
                EvidenceAgeFrames: 0u);
        }

        internal static float DecodeSimpleDdgiEnergyEvidenceLuminance(uint code)
        {
            if (code == 0)
                return 0.0f;
            float normalized = (Math.Clamp(code, 1u, 2047u) - 1u) / 2046.0f;
            return (MathF.Pow(65.0f, normalized) - 1.0f);
        }

        private static uint DecodeSimpleDdgiEnergyEvidenceChunks(
            uint* counters,
            int counterBase,
            int chunkCount,
            uint winnerKey,
            ref bool coherent)
        {
            uint value = 0;
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                uint packed = counters[counterBase + chunk];
                coherent &= (packed >> 6) == winnerKey;
                value |= (packed & 0x3fu) << (chunk * 6);
            }
            return value;
        }

        private static float ReadSimpleDdgiEnergyEvidencePercentile(
            uint* counters,
            int histogramBase,
            uint sampleCount,
            double percentile)
        {
            if (sampleCount == 0)
                return 0.0f;

            ulong target = Math.Max(
                1ul,
                (ulong)Math.Ceiling(sampleCount * Math.Clamp(percentile, 0.0, 1.0)));
            ulong cumulative = 0;
            for (int bucket = 0;
                 bucket < SimpleDdgiVolumeEnergyEvidenceHistogramCount;
                 bucket++)
            {
                cumulative += counters[histogramBase + bucket];
                if (cumulative < target)
                    continue;

                // Return the conservative upper edge of the logarithmic bucket.
                float normalized = (bucket + 1.0f) /
                    SimpleDdgiVolumeEnergyEvidenceHistogramCount;
                return MathF.Pow(65.0f, normalized) - 1.0f;
            }

            return 64.0f;
        }

        private static bool HasSimpleDdgiVolumeEnergySamples(
            SimpleDdgiVolumeEnergyCounters value) =>
            value.BlendSampleCount != 0 ||
            value.TransportSampleCount != 0 ||
            value.SolverGatherSampleCount != 0 ||
            value.ShadowVisibilityRayCount != 0;

        private static SimpleDdgiVolumeEnergyCounters MergeSimpleDdgiVolumeEnergyEvidence(
            SimpleDdgiVolumeEnergyCounters current,
            SimpleDdgiVolumeEnergyCounters retained) =>
            current with
            {
                EvidenceSampleCount = retained.EvidenceSampleCount,
                IrradianceLuminanceP95 = retained.IrradianceLuminanceP95,
                IrradianceLuminanceP99 = retained.IrradianceLuminanceP99,
                IrradianceLuminanceMaximum = retained.IrradianceLuminanceMaximum,
                MaximumVirtualProbeIndex = retained.MaximumVirtualProbeIndex,
                MaximumVirtualPageIndex = retained.MaximumVirtualPageIndex,
                MaximumPhysicalProbeIndex = retained.MaximumPhysicalProbeIndex,
                MaximumPhysicalPageIndex = retained.MaximumPhysicalPageIndex,
                MaximumVisibilityMomentMean = retained.MaximumVisibilityMomentMean,
                MaximumVisibilityMomentSecond = retained.MaximumVisibilityMomentSecond,
                MaximumSourceLightingGeneration = retained.MaximumSourceLightingGeneration,
                MaximumWitnessCoherent = retained.MaximumWitnessCoherent,
                EvidenceAgeFrames = retained.EvidenceAgeFrames
            };

        public void ResetCounters(
            CommandBuffer commandBuffer,
            int frameIndex,
            ulong directionalShadowCasterFrameSerial = 0UL,
            uint directionalShadowCasterResourceGeneration = 0u,
            bool directionalShadowCasterFrameMetadataValid = false,
            ulong diagnosticFrameSerial = 0UL)
        {
            ValidateFrameIndex(frameIndex);
            _diagnosticFrameSerials[frameIndex] = diagnosticFrameSerial;
            _diagnosticFrameSubmitted[frameIndex] = true;

            // Do not overlap a fill and an update write to the frame-metadata
            // header. Vulkan does not make the later transfer write win merely
            // because it was recorded later. The two non-overlapping fills keep
            // the entire bank zeroed while CmdUpdateBuffer stamps its ownership.
            ulong directionalShadowCasterHeaderOffset =
                (ulong)DirectionalShadowCasterDiagnosticCounterBase * sizeof(uint);
            ulong directionalShadowCasterHeaderSize =
                (ulong)DirectionalShadowCasterDiagnosticHeaderWordCount * sizeof(uint);
            VkBuffer diagnosticBuffer = _bufferManager.GetBuffer(_buffers[frameIndex]);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                diagnosticBuffer,
                0,
                directionalShadowCasterHeaderOffset,
                0);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                diagnosticBuffer,
                directionalShadowCasterHeaderOffset + directionalShadowCasterHeaderSize,
                CounterBufferSize -
                    directionalShadowCasterHeaderOffset -
                    directionalShadowCasterHeaderSize,
                0);
            Span<uint> directionalShadowCasterHeader =
                stackalloc uint[DirectionalShadowCasterDiagnosticHeaderWordCount];
            directionalShadowCasterHeader[3] = unchecked((uint)directionalShadowCasterFrameSerial);
            directionalShadowCasterHeader[4] = unchecked((uint)(directionalShadowCasterFrameSerial >> 32));
            directionalShadowCasterHeader[5] = directionalShadowCasterResourceGeneration;
            directionalShadowCasterHeader[6] = directionalShadowCasterFrameMetadataValid
                ? DirectionalShadowCasterDiagnosticFrameMetadataMagic
                : 0u;
            _context.Api.CmdUpdateBuffer(
                commandBuffer,
                diagnosticBuffer,
                directionalShadowCasterHeaderOffset,
                directionalShadowCasterHeader);
            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(_simpleDdgiStorageValidationBuffers[frameIndex]),
                0,
                SimpleDdgiStorageValidationBufferSize,
                0);
#if NJULF_DETAILED_INVESTIGATION
            _context.Api.CmdFillBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(_simpleDdgiStorageValidationBuffers[frameIndex]),
                (ulong)SimpleDdgiStorageValidationSentinelWord * sizeof(uint),
                sizeof(uint),
                SimpleDdgiStorageValidationSentinel);
#endif

            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            barriers[0] = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt |
                    PipelineStageFlags2.MeshShaderBitExt |
                    PipelineStageFlags2.VertexShaderBit |
                    PipelineStageFlags2.FragmentShaderBit |
                    PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_buffers[frameIndex]),
                Offset = 0,
                Size = CounterBufferSize
            };
            barriers[1] = barriers[0];
            barriers[1].Buffer = _bufferManager.GetBuffer(
                _simpleDdgiStorageValidationBuffers[frameIndex]);
            barriers[1].Size = SimpleDdgiStorageValidationBufferSize;

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 2,
                PBufferMemoryBarriers = barriers
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
                if (_simpleDdgiStorageValidationBuffers[i].IsValid)
                    _bufferManager.DestroyBuffer(_simpleDdgiStorageValidationBuffers[i]);
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
        int ReservedDiagnosticSlot);
}

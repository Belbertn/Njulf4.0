using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using Njulf.Rendering.Utilities;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Pipeline
{
    public sealed unsafe class SceneOpaqueCompactionPass : RenderPassBase
    {
        private const uint WorkgroupSize = 64;
        private const int MaximumLodTransitionStateCount = 4096;
        private const int LodTransitionOutputCapacityMultiplier = 2;
        private const int MaxValidationSampleCommands = 4096;
        private const int DirectionalShadowCascadeCapacity = ShadowSettings.MaxDirectionalCascades;
        private const int OpaqueIndirectDispatchSlot = 0;
        private const int SimpleOpaqueIndirectDispatchSlot = 1;
        private const int SimpleNormalOpaqueIndirectDispatchSlot = 2;
        private const int FullOpaqueIndirectDispatchSlot = 3;
        private const int SolidDepthIndirectDispatchSlot = 4;
        private const int MaskedDepthIndirectDispatchSlot = 5;
        private const int DirectionalStaticShadowIndirectDispatchSlotBase = 6;
        private const int DirectionalDynamicShadowIndirectDispatchSlotBase =
            DirectionalStaticShadowIndirectDispatchSlotBase + DirectionalShadowCascadeCapacity;
        private const int SimpleOpaqueDoubleSidedIndirectDispatchSlot = 14;
        private const int SimpleNormalOpaqueDoubleSidedIndirectDispatchSlot = 15;
        private const int FullOpaqueDoubleSidedIndirectDispatchSlot = 16;
        private const int SolidDepthDoubleSidedIndirectDispatchSlot = 17;
        private const int MaskedDepthDoubleSidedIndirectDispatchSlot = 18;
        private const int DirectionalStaticShadowDoubleSidedIndirectDispatchSlotBase = 19;
        private const int DirectionalDynamicShadowDoubleSidedIndirectDispatchSlotBase =
            DirectionalStaticShadowDoubleSidedIndirectDispatchSlotBase +
            DirectionalShadowCascadeCapacity;
        private const int IndirectDispatchSlotCount =
            DirectionalDynamicShadowDoubleSidedIndirectDispatchSlotBase +
            DirectionalShadowCascadeCapacity;
        private static readonly ulong DrawCommandStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong CounterStride = (ulong)Marshal.SizeOf<GPUSceneSubmissionCounters>();
        private static readonly ulong IndirectDispatchStride = (ulong)Marshal.SizeOf<GPUFoliageDispatchArgs>();
        private static readonly ulong ValidationReadbackBytes = checked((ulong)MaxValidationSampleCommands * (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>());

        private readonly MeshPipeline _meshPipeline;
        private readonly BufferManager _bufferManager;
        private readonly FenceBasedDeleter _deleter;
        private readonly SynchronizationManager _synchronization;
        private readonly bool _asymmetricSidedStreamsEnabled;
        private readonly RuntimeBuffer[] _compactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _simpleCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _simpleNormalCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _fullCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _solidDepthCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _maskedDepthCompactedDrawBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[,] _directionalStaticShadowCompactedDrawBuffers =
            new RuntimeBuffer[RenderingConstants.FramesInFlight, DirectionalShadowCascadeCapacity];
        private readonly RuntimeBuffer[,] _directionalDynamicShadowCompactedDrawBuffers =
            new RuntimeBuffer[RenderingConstants.FramesInFlight, DirectionalShadowCascadeCapacity];
        private readonly RuntimeBuffer[] _counterBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _indirectDispatchBuffers = new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly RuntimeBuffer[] _lodHistoryBuffers =
            new RuntimeBuffer[RenderingConstants.FramesInFlight];
        private readonly BufferHandle[] _counterReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly BufferHandle[] _validationReadbackBuffers = new BufferHandle[RenderingConstants.FramesInFlight];
        private readonly bool[] _counterReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private readonly bool[] _validationReadbackRecorded = new bool[RenderingConstants.FramesInFlight];
        private Func<SceneRenderingData, uint>? _directionalStaticShadowRefreshMask;
        private ulong _lodHistorySceneRevision;
        private uint _lodHistoryLogicalCapacity;
        private GpuLodSelectionMode _lodHistoryMode;
        private bool _lodHistoryInitialized;
        private readonly ValidationExpectedFrame[] _validationExpectedFrames =
        [
            ValidationExpectedFrame.Invalid,
            ValidationExpectedFrame.Invalid
        ];
        private readonly SceneSubmissionCounterSnapshot[] _completedCounters =
        [
            SceneSubmissionCounterSnapshot.Invalid,
            SceneSubmissionCounterSnapshot.Invalid
        ];
        private readonly SceneSubmissionValidationSnapshot[] _completedValidation =
        [
            SceneSubmissionValidationSnapshot.Invalid,
            SceneSubmissionValidationSnapshot.Invalid
        ];

        public SceneOpaqueCompactionPass(
            VulkanContext context,
            SwapchainManager swapchain,
            BindlessHeap bindlessHeap,
            MeshPipeline meshPipeline,
            BufferManager bufferManager,
            FenceBasedDeleter deleter,
            SynchronizationManager synchronization,
            bool asymmetricSidedStreamsEnabled = true)
            : base("SceneOpaqueCompactionPass", context, swapchain, bindlessHeap)
        {
            _meshPipeline = meshPipeline ?? throw new ArgumentNullException(nameof(meshPipeline));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _deleter = deleter ?? throw new ArgumentNullException(nameof(deleter));
            _synchronization = synchronization ??
                throw new ArgumentNullException(nameof(synchronization));
            _asymmetricSidedStreamsEnabled = asymmetricSidedStreamsEnabled;
        }

        public void SetDirectionalStaticShadowRefreshQuery(Func<SceneRenderingData, uint> refreshMask)
        {
            _directionalStaticShadowRefreshMask = refreshMask ?? throw new ArgumentNullException(nameof(refreshMask));
        }

        public override bool ShouldExecute(int frameIndex, SceneRenderingData sceneData)
        {
            return SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(sceneData).Length == 0;
        }

        public override void Initialize()
        {
        }

        public void ReadCompletedFrame(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            if (!_counterReadbackRecorded[frameIndex] || !_counterReadbackBuffers[frameIndex].IsValid)
            {
                _completedCounters[frameIndex] = SceneSubmissionCounterSnapshot.Invalid;
                _completedValidation[frameIndex] = SceneSubmissionValidationSnapshot.Invalid;
                return;
            }

            _bufferManager.InvalidateBuffer(_counterReadbackBuffers[frameIndex], 0, CounterStride);
            GPUSceneSubmissionCounters* counters =
                (GPUSceneSubmissionCounters*)_bufferManager.GetMappedPointer(_counterReadbackBuffers[frameIndex]);
            _completedCounters[frameIndex] = SceneSubmissionCounterSnapshot.FromCounters(*counters);
            _completedValidation[frameIndex] = ReadCompletedValidation(frameIndex, _completedCounters[frameIndex]);
            _counterReadbackRecorded[frameIndex] = false;
            _validationReadbackRecorded[frameIndex] = false;
        }

        public SceneSubmissionCounterSnapshot GetLastCompletedCounters(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _completedCounters[frameIndex];
        }

        public SceneSubmissionValidationSnapshot GetLastCompletedValidation(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _completedValidation[frameIndex];
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
            int candidateCount = checked(sceneData.SimpleOpaqueMeshletCount +
                sceneData.SimpleNormalOpaqueMeshletCount +
                sceneData.FullOpaqueMeshletCount);
            int solidDepthCandidateCount = sceneData.DepthPrePassEnabled ? sceneData.SolidMeshletCount : 0;
            int maskedDepthCandidateCount = sceneData.DepthPrePassEnabled ? sceneData.MaskedMeshletCount : 0;
            bool compactDirectionalShadows =
                sceneData.SceneSubmissionGpuShadowCompactionEnabled &&
                sceneData.DirectionalShadowPassEnabled &&
                sceneData.DirectionalShadowFramePlan.UsesCascadedShadowMap &&
                sceneData.DirectionalShadowCascadeCount > 0;
            uint activeDirectionalCascadeMask = sceneData.DirectionalShadowCascadeCount >= DirectionalShadowCascadeCapacity
                ? (1u << DirectionalShadowCascadeCapacity) - 1u
                : (1u << Math.Max(0, sceneData.DirectionalShadowCascadeCount)) - 1u;
            uint directionalStaticShadowCascadeMask = compactDirectionalShadows
                ? (_directionalStaticShadowRefreshMask?.Invoke(sceneData) ?? activeDirectionalCascadeMask) &
                  activeDirectionalCascadeMask
                : 0u;
            bool compactDirectionalStaticShadows =
                compactDirectionalShadows &&
                sceneData.DirectionalStaticShadowMeshletCount > 0 &&
                directionalStaticShadowCascadeMask != 0u;
            bool compactDirectionalDynamicShadows =
                compactDirectionalShadows &&
                sceneData.DirectionalDynamicShadowMeshletCount > 0;
            int directionalStaticShadowCandidateCount = compactDirectionalStaticShadows
                ? sceneData.DirectionalStaticShadowMeshletCount
                : 0;
            int directionalDynamicShadowCandidateCount = compactDirectionalDynamicShadows
                ? sceneData.DirectionalDynamicShadowMeshletCount
                : 0;
            bool instanceExpansion = CanUseInstanceExpansion(sceneData);
            sceneData.SceneSubmissionGpuInstanceExpansionActive =
                instanceExpansion;
            sceneData.SceneSubmissionGpuHierarchicalLodActive =
                instanceExpansion &&
                sceneData.SceneSubmissionGpuHierarchicalLodEnabled &&
                sceneData.SceneSubmissionGpuLodSelectionEnabled &&
                sceneData.SceneSubmissionGpuLodSelectionMode ==
                    GpuLodSelectionMode.ScreenSpaceError &&
                !sceneData.SceneSubmissionValidationCompareCpuGpuLists;
            bool lodDitherTransitions = instanceExpansion &&
                sceneData.SceneSubmissionGpuLodSelectionEnabled &&
                sceneData.SceneSubmissionGpuLodDitherTransitionsEnabled &&
                !sceneData.SceneSubmissionValidationCompareCpuGpuLists;
            sceneData.SceneSubmissionGpuLodDitherTransitionsActive =
                lodDitherTransitions;
            int transitionCapacityMultiplier = lodDitherTransitions
                ? LodTransitionOutputCapacityMultiplier
                : 1;
            int opaqueOutputCapacity = checked(
                candidateCount * transitionCapacityMultiplier);
            int simpleOutputCandidateCapacity = checked(
                sceneData.SimpleOpaqueMeshletCount *
                transitionCapacityMultiplier);
            int simpleNormalOutputCandidateCapacity = checked(
                sceneData.SimpleNormalOpaqueMeshletCount *
                transitionCapacityMultiplier);
            int fullOutputCandidateCapacity = checked(
                sceneData.FullOpaqueMeshletCount *
                transitionCapacityMultiplier);
            int perInvocationDispatchCandidateCount = Math.Max(
                Math.Max(candidateCount, Math.Max(solidDepthCandidateCount, maskedDepthCandidateCount)),
                Math.Max(directionalStaticShadowCandidateCount, directionalDynamicShadowCandidateCount));
            if (instanceExpansion)
            {
                perInvocationDispatchCandidateCount = 0;
            }
            uint perInvocationGroupCount = perInvocationDispatchCandidateCount > 0
                ? (checked((uint)perInvocationDispatchCandidateCount) + WorkgroupSize - 1u) /
                  WorkgroupSize
                : 0u;
            uint instanceGroupCount = instanceExpansion
                ? checked((uint)sceneData.SceneInstanceCandidateCount)
                : 0u;
            uint dispatchGroupCount = Math.Max(
                perInvocationGroupCount,
                instanceGroupCount);
            if (dispatchGroupCount == 0u)
            {
                sceneData.SceneSubmissionCompactionSkipReason =
                    "no dispatch candidates for GPU scene submission";
                return;
            }

            bool sidedStreams = CanUseSidedStreams(
                sceneData,
                simpleOutputCandidateCapacity,
                simpleNormalOutputCandidateCapacity,
                fullOutputCandidateCapacity);
            sceneData.SceneSubmissionSidedRasterSpecializationActive =
                sidedStreams;

            bool asymmetricSidedStreams = sidedStreams &&
                _asymmetricSidedStreamsEnabled &&
                SidedStreamCountsAreValid(
                    sceneData,
                    solidDepthCandidateCount,
                    maskedDepthCandidateCount,
                    directionalStaticShadowCandidateCount,
                    directionalDynamicShadowCandidateCount);
            sceneData.SceneSubmissionAsymmetricSidedStreamsActive =
                asymmetricSidedStreams;
            SidedStreamCapacityPlan simpleLayout =
                ResolveSidedStreamCapacityPlan(
                    sceneData.SimpleOpaqueMeshletCount,
                    sceneData.DoubleSidedSimpleOpaqueMeshletCount,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan simpleNormalLayout =
                ResolveSidedStreamCapacityPlan(
                    sceneData.SimpleNormalOpaqueMeshletCount,
                    sceneData.DoubleSidedSimpleNormalOpaqueMeshletCount,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan fullLayout =
                ResolveSidedStreamCapacityPlan(
                    sceneData.FullOpaqueMeshletCount,
                    sceneData.DoubleSidedFullOpaqueMeshletCount,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan solidDepthLayout =
                ResolveSidedStreamCapacityPlan(
                    solidDepthCandidateCount,
                    sceneData.DepthPrePassEnabled
                        ? sceneData.DoubleSidedSolidDepthMeshletCount
                        : 0,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan maskedDepthLayout =
                ResolveSidedStreamCapacityPlan(
                    maskedDepthCandidateCount,
                    sceneData.DepthPrePassEnabled
                        ? sceneData.DoubleSidedMaskedDepthMeshletCount
                        : 0,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan directionalStaticLayout =
                ResolveSidedStreamCapacityPlan(
                    directionalStaticShadowCandidateCount,
                    directionalStaticShadowCandidateCount > 0
                        ? sceneData
                            .DoubleSidedDirectionalStaticShadowMeshletCount
                        : 0,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);
            SidedStreamCapacityPlan directionalDynamicLayout =
                ResolveSidedStreamCapacityPlan(
                    directionalDynamicShadowCandidateCount,
                    directionalDynamicShadowCandidateCount > 0
                        ? sceneData
                            .DoubleSidedDirectionalDynamicShadowMeshletCount
                        : 0,
                    transitionCapacityMultiplier,
                    sidedStreams,
                    asymmetricSidedStreams);

            EnsureRuntimeBuffers(
                frameIndex,
                sceneData.ObjectCount,
                opaqueOutputCapacity,
                simpleLayout,
                simpleNormalLayout,
                fullLayout,
                solidDepthLayout,
                maskedDepthLayout,
                directionalStaticLayout,
                directionalDynamicLayout);
            RuntimeBuffer drawBuffer = _compactedDrawBuffers[frameIndex];
            RuntimeBuffer simpleDrawBuffer = _simpleCompactedDrawBuffers[frameIndex];
            RuntimeBuffer simpleNormalDrawBuffer = _simpleNormalCompactedDrawBuffers[frameIndex];
            RuntimeBuffer fullDrawBuffer = _fullCompactedDrawBuffers[frameIndex];
            RuntimeBuffer solidDepthDrawBuffer = _solidDepthCompactedDrawBuffers[frameIndex];
            RuntimeBuffer maskedDepthDrawBuffer = _maskedDepthCompactedDrawBuffers[frameIndex];
            RuntimeBuffer counterBuffer = _counterBuffers[frameIndex];
            RuntimeBuffer indirectDispatchBuffer = _indirectDispatchBuffers[frameIndex];
            if (!drawBuffer.Handle.IsValid ||
                !simpleDrawBuffer.Handle.IsValid ||
                !simpleNormalDrawBuffer.Handle.IsValid ||
                !fullDrawBuffer.Handle.IsValid ||
                !solidDepthDrawBuffer.Handle.IsValid ||
                !maskedDepthDrawBuffer.Handle.IsValid ||
                !counterBuffer.Handle.IsValid ||
                !indirectDispatchBuffer.Handle.IsValid)
            {
                sceneData.SceneSubmissionCompactionSkipReason =
                    "scene opaque compaction buffers unavailable";
                return;
            }

            sceneData.SceneSubmissionGpuCompactionActive = true;
            sceneData.SceneSubmissionCompactionSkipReason = string.Empty;
            sceneData.SceneSubmissionGpuOpaqueCandidateCount = candidateCount;
            uint simpleOutputCapacity = checked(
                (uint)simpleLayout.OneSidedCapacity);
            uint simpleNormalOutputCapacity = checked(
                (uint)simpleNormalLayout.OneSidedCapacity);
            uint fullOutputCapacity = checked(
                (uint)fullLayout.OneSidedCapacity);
            uint solidDepthOutputCapacity = checked(
                (uint)solidDepthLayout.OneSidedCapacity);
            uint maskedDepthOutputCapacity = checked(
                (uint)maskedDepthLayout.OneSidedCapacity);
            uint directionalStaticOutputCapacity = checked(
                (uint)directionalStaticLayout.OneSidedCapacity);
            uint directionalDynamicOutputCapacity = checked(
                (uint)directionalDynamicLayout.OneSidedCapacity);

            sceneData.SceneSubmissionGpuCompactedOpaqueCapacity = (int)Math.Min(drawBuffer.ElementCapacity, int.MaxValue);
            sceneData.SceneSubmissionGpuCompactedSimpleOpaqueCapacity =
                checked((int)simpleOutputCapacity);
            sceneData.SceneSubmissionGpuCompactedSimpleOpaqueDoubleSidedBase =
                simpleLayout.DoubleSidedBase;
            sceneData.SceneSubmissionGpuCompactedSimpleOpaqueDoubleSidedCapacity =
                simpleLayout.DoubleSidedCapacity;
            sceneData.SceneSubmissionGpuCompactedSimpleNormalOpaqueCapacity =
                checked((int)simpleNormalOutputCapacity);
            sceneData.SceneSubmissionGpuCompactedSimpleNormalOpaqueDoubleSidedBase =
                simpleNormalLayout.DoubleSidedBase;
            sceneData.SceneSubmissionGpuCompactedSimpleNormalOpaqueDoubleSidedCapacity =
                simpleNormalLayout.DoubleSidedCapacity;
            sceneData.SceneSubmissionGpuCompactedFullOpaqueCapacity =
                checked((int)fullOutputCapacity);
            sceneData.SceneSubmissionGpuCompactedFullOpaqueDoubleSidedBase =
                fullLayout.DoubleSidedBase;
            sceneData.SceneSubmissionGpuCompactedFullOpaqueDoubleSidedCapacity =
                fullLayout.DoubleSidedCapacity;
            sceneData.SceneSubmissionGpuDepthSolidCandidateCount = solidDepthCandidateCount;
            sceneData.SceneSubmissionGpuDepthMaskedCandidateCount = maskedDepthCandidateCount;
            sceneData.SceneSubmissionGpuCompactedSolidDepthCapacity =
                checked((int)solidDepthOutputCapacity);
            sceneData.SceneSubmissionGpuCompactedSolidDepthDoubleSidedBase =
                solidDepthLayout.DoubleSidedBase;
            sceneData.SceneSubmissionGpuCompactedSolidDepthDoubleSidedCapacity =
                solidDepthLayout.DoubleSidedCapacity;
            sceneData.SceneSubmissionGpuCompactedMaskedDepthCapacity =
                checked((int)maskedDepthOutputCapacity);
            sceneData.SceneSubmissionGpuCompactedMaskedDepthDoubleSidedBase =
                maskedDepthLayout.DoubleSidedBase;
            sceneData.SceneSubmissionGpuCompactedMaskedDepthDoubleSidedCapacity =
                maskedDepthLayout.DoubleSidedCapacity;
            InitializeDirectionalShadowRuntimeState(
                sceneData,
                directionalStaticShadowCandidateCount,
                directionalDynamicShadowCandidateCount,
                directionalStaticLayout,
                directionalDynamicLayout,
                directionalStaticShadowCascadeMask);
            sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBuffer = drawBuffer.Handle;
            sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBuffer = solidDepthDrawBuffer.Handle;
            sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBuffer = maskedDepthDrawBuffer.Handle;
            sceneData.SceneSubmissionCounterBuffer = counterBuffer.Handle;
            sceneData.SceneSubmissionOpaqueIndirectDispatchBuffer = indirectDispatchBuffer.Handle;
            sceneData.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize = checked(
                drawBuffer.ByteSize +
                simpleDrawBuffer.ByteSize +
                simpleNormalDrawBuffer.ByteSize +
                fullDrawBuffer.ByteSize);
            sceneData.SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize = solidDepthDrawBuffer.ByteSize;
            sceneData.SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize = maskedDepthDrawBuffer.ByteSize;
            sceneData.SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize =
                SumDirectionalShadowBufferBytes(frameIndex);
            sceneData.SceneSubmissionCounterBufferSize = counterBuffer.ByteSize;
            sceneData.SceneSubmissionOpaqueIndirectDispatchBufferSize = indirectDispatchBuffer.ByteSize;

            SceneOpaqueResetPlan resetPlan = SceneOpaqueResetPlan.Create(
                sceneData.SceneSubmissionIndirectMeshletDispatchEnabled,
                sceneData.SceneSubmissionValidationCompareCpuGpuLists,
                sceneData.DirectionalShadowCascadeCount,
                directionalStaticShadowCascadeMask,
                compactDirectionalDynamicShadows);
            (ulong clearedBytes, int resetBarrierCount) = ResetOutputs(
                cmd,
                frameIndex,
                resetPlan,
                drawBuffer,
                simpleDrawBuffer,
                simpleNormalDrawBuffer,
                fullDrawBuffer,
                solidDepthDrawBuffer,
                maskedDepthDrawBuffer,
                counterBuffer,
                indirectDispatchBuffer);
            sceneData.SceneSubmissionCompactionFullPayloadClear =
                resetPlan.ClearPayloads;
            sceneData.SceneSubmissionCompactionClearedBytes = clearedBytes;
            sceneData.SceneSubmissionCompactionResetBarrierCount =
                resetBarrierCount;
            PrepareLodHistory(cmd, sceneData);

            _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _meshPipeline.SceneOpaqueCompactionPipeline);
            var descriptorSets = stackalloc DescriptorSet[2];
            descriptorSets[0] = _bindlessHeap.StorageBufferSet;
            descriptorSets[1] = _bindlessHeap.TextureSamplerSet;
            _context.Api.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Compute,
                _meshPipeline.SceneSubmissionComputeLayout,
                0,
                2,
                descriptorSets,
                0,
                null);

            float gpuLod1DistanceRatio = SceneSubmissionSettings.ClampGpuLod1DistanceRatio(
                sceneData.SceneSubmissionGpuLod1DistanceRatio);
            float gpuLod2DistanceRatio = SceneSubmissionSettings.ClampGpuLod2DistanceRatio(
                sceneData.SceneSubmissionGpuLod2DistanceRatio,
                gpuLod1DistanceRatio);
            var pushConstants = new GPUSceneOpaqueCompactionPushConstants
            {
                CameraPosition = new Njulf.Core.Math.Vector4(
                    sceneData.CameraPosition.X,
                    sceneData.CameraPosition.Y,
                    sceneData.CameraPosition.Z,
                    0.0f),
                CurrentFrameIndex = (uint)frameIndex,
                SimpleCandidateCount = checked((uint)Math.Max(0, sceneData.SimpleOpaqueMeshletCount)),
                SimpleNormalCandidateCount = checked((uint)Math.Max(0, sceneData.SimpleNormalOpaqueMeshletCount)),
                FullCandidateCount = checked((uint)Math.Max(0, sceneData.FullOpaqueMeshletCount)),
                OutputCapacity = drawBuffer.ElementCapacity,
                SolidDepthCandidateCount = checked((uint)Math.Max(0, solidDepthCandidateCount)),
                MaskedDepthCandidateCount = checked((uint)Math.Max(0, maskedDepthCandidateCount)),
                SolidDepthOutputCapacity = solidDepthOutputCapacity,
                MaskedDepthOutputCapacity = maskedDepthOutputCapacity,
                DirectionalShadowCascadeCount = checked((uint)Math.Min(
                    Math.Max(0, sceneData.DirectionalShadowCascadeCount),
                    DirectionalShadowCascadeCapacity)),
                DirectionalStaticShadowCandidateCount = checked((uint)Math.Max(0, directionalStaticShadowCandidateCount)),
                DirectionalDynamicShadowCandidateCount = checked((uint)Math.Max(0, directionalDynamicShadowCandidateCount)),
                DirectionalStaticShadowOutputCapacity =
                    directionalStaticOutputCapacity,
                DirectionalDynamicShadowOutputCapacity =
                    directionalDynamicOutputCapacity,
                OutputBufferBaseIndex = (uint)BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase,
                CounterBufferBaseIndex = (uint)BindlessIndex.SceneSubmissionCounterBufferBase,
                Flags = BuildCompactionFlags(
                    sceneData,
                    compactDirectionalShadows),
                IndirectDispatchBufferBaseIndex = (uint)BindlessIndex.SceneOpaqueIndirectDispatchBufferBase,
                SolidDepthOutputBufferBaseIndex = (uint)BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase,
                MaskedDepthOutputBufferBaseIndex = (uint)BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase,
                SimpleOutputCapacity = simpleOutputCapacity,
                SimpleNormalOutputCapacity = simpleNormalOutputCapacity,
                FullOutputCapacity = fullOutputCapacity,
                SimpleOutputBufferBaseIndex = (uint)BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                SimpleNormalOutputBufferBaseIndex = (uint)BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                FullOutputBufferBaseIndex = (uint)BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                ScreenDimensions = new Njulf.Core.Math.Vector2(sceneData.ScreenWidth, sceneData.ScreenHeight),
                HiZTextureIndex = (uint)BindlessIndex.HiZDepthTexture,
                HiZMipCount = sceneData.HiZMipCount,
                OcclusionCullingEnabled = sceneData.OcclusionCullingEnabled ? (uint)sceneData.HiZTestMode : (uint)HiZTestMode.Off,
                OcclusionBias = sceneData.OcclusionBias,
                PreviousFrameUvPaddingPixels = checked((uint)Math.Max(0, sceneData.PreviousHiZUvPaddingPixels)),
                PreviousHiZFrameValid = sceneData.PreviousHiZFrameValid ? 1u : 0u,
                GpuLod1DistanceRatio = gpuLod1DistanceRatio,
                GpuLod2DistanceRatio = gpuLod2DistanceRatio,
                GpuLodSelectionMode =
                    (uint)sceneData.SceneSubmissionGpuLodSelectionMode,
                GpuLodTargetPixelError =
                    SceneSubmissionSettings.ClampGpuLodTargetPixelError(
                        sceneData.SceneSubmissionGpuLodTargetPixelError),
                GpuLodHysteresisFraction =
                    SceneSubmissionSettings.GpuLodHysteresisFraction,
                GpuLodProjectionScale = ResolveLodProjectionScale(sceneData),
                GpuLodHistoryBufferBaseIndex =
                    (uint)BindlessIndex.SceneGpuLodHistoryBufferBase,
                GpuLodHistoryCapacity = checked((uint)Math.Max(
                    1,
                    Math.Min(
                        sceneData.ObjectCount,
                        MaximumLodTransitionStateCount))),
                GpuShadowLodBias = checked((uint)Math.Clamp(sceneData.SceneSubmissionGpuShadowLodBias, 0, 2)),
                DirectionalStaticShadowCascadeMask = directionalStaticShadowCascadeMask,
                DirectionalShadowLightDirection = new Njulf.Core.Math.Vector4(
                    sceneData.DirectionalShadowLightDirection.X,
                    sceneData.DirectionalShadowLightDirection.Y,
                    sceneData.DirectionalShadowLightDirection.Z,
                    0.0f),
                InstanceCandidateCount = instanceExpansion
                    ? checked((uint)sceneData.SceneInstanceCandidateCount)
                    : 0u,
                InstanceCandidateBufferBaseIndex =
                    (uint)BindlessIndex.SceneInstanceCandidateBufferBase,
                TemporalFrameIndex = sceneData.TemporalSampleIndex,
                LodTransitionFrameCount = lodDitherTransitions
                    ? checked((uint)Math.Clamp(
                        sceneData.SceneSubmissionGpuLodTransitionFrameCount,
                        1,
                        SceneSubmissionSettings
                            .MaximumGpuLodTransitionFrameCount))
                    : 0u,
                SimpleDoubleSidedCapacity = checked(
                    (uint)simpleLayout.DoubleSidedCapacity),
                SimpleNormalDoubleSidedCapacity = checked(
                    (uint)simpleNormalLayout.DoubleSidedCapacity),
                FullDoubleSidedCapacity = checked(
                    (uint)fullLayout.DoubleSidedCapacity),
                SolidDepthDoubleSidedCapacity = checked(
                    (uint)solidDepthLayout.DoubleSidedCapacity),
                MaskedDepthDoubleSidedCapacity = checked(
                    (uint)maskedDepthLayout.DoubleSidedCapacity),
                DirectionalStaticShadowDoubleSidedCapacity = checked(
                    (uint)directionalStaticLayout.DoubleSidedCapacity),
                DirectionalDynamicShadowDoubleSidedCapacity = checked(
                    (uint)directionalDynamicLayout.DoubleSidedCapacity)
            };
            _context.Api.CmdPushConstants(
                cmd,
                _meshPipeline.SceneSubmissionComputeLayout,
                ShaderStageFlags.ComputeBit,
                0,
                (uint)Marshal.SizeOf<GPUSceneOpaqueCompactionPushConstants>(),
                &pushConstants);

            _context.Api.CmdDispatch(cmd, dispatchGroupCount, 1, 1);
            sceneData.SceneSubmissionCompactionOutputBarrierCount =
                RecordOutputBarrier(
                    cmd,
                    frameIndex,
                    resetPlan,
                    drawBuffer,
                    simpleDrawBuffer,
                    simpleNormalDrawBuffer,
                    fullDrawBuffer,
                    solidDepthDrawBuffer,
                    maskedDepthDrawBuffer,
                    counterBuffer,
                    indirectDispatchBuffer,
                    checked((uint)opaqueOutputCapacity),
                    checked((uint)simpleLayout.TotalLogicalCapacity),
                    checked((uint)simpleNormalLayout.TotalLogicalCapacity),
                    checked((uint)fullLayout.TotalLogicalCapacity),
                    checked((uint)solidDepthLayout.TotalLogicalCapacity),
                    checked((uint)maskedDepthLayout.TotalLogicalCapacity),
                    checked((uint)directionalStaticLayout.TotalLogicalCapacity),
                    checked((uint)directionalDynamicLayout.TotalLogicalCapacity),
                    sceneData.ForwardVisibilityCompactionEnabled,
                    sceneData.SceneSubmissionValidationCompareCpuGpuLists);
            RecordLodHistoryBarrier(cmd, frameIndex);
            RecordCounterReadback(cmd, frameIndex, counterBuffer);
            if (sceneData.SceneSubmissionValidationCompareCpuGpuLists)
            {
                CaptureExpectedValidationFrame(frameIndex, sceneData);
                RecordValidationReadback(cmd, frameIndex, drawBuffer);
            }
            else
            {
                _validationExpectedFrames[frameIndex] = ValidationExpectedFrame.Invalid;
                _validationReadbackRecorded[frameIndex] = false;
            }
        }

        private void EnsureRuntimeBuffers(
            int frameIndex,
            int objectCount,
            int candidateCount,
            in SidedStreamCapacityPlan simpleLayout,
            in SidedStreamCapacityPlan simpleNormalLayout,
            in SidedStreamCapacityPlan fullLayout,
            in SidedStreamCapacityPlan solidDepthLayout,
            in SidedStreamCapacityPlan maskedDepthLayout,
            in SidedStreamCapacityPlan directionalStaticShadowLayout,
            in SidedStreamCapacityPlan directionalDynamicShadowLayout)
        {
            ValidateFrameIndex(frameIndex);
            uint required = checked((uint)Math.Max(1, candidateCount));
            EnsureCapacity(
                ref _compactedDrawBuffers[frameIndex],
                required,
                DrawCommandStride,
                $"SceneSubmission.OpaqueCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _simpleCompactedDrawBuffers[frameIndex],
                simpleLayout.RequiredBackingElements,
                DrawCommandStride,
                $"SceneSubmission.SimpleOpaqueCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _simpleNormalCompactedDrawBuffers[frameIndex],
                simpleNormalLayout.RequiredBackingElements,
                DrawCommandStride,
                $"SceneSubmission.SimpleNormalOpaqueCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _fullCompactedDrawBuffers[frameIndex],
                fullLayout.RequiredBackingElements,
                DrawCommandStride,
                $"SceneSubmission.FullOpaqueCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _solidDepthCompactedDrawBuffers[frameIndex],
                solidDepthLayout.RequiredBackingElements,
                DrawCommandStride,
                $"SceneSubmission.SolidDepthCompactedMeshletDraw.Frame{frameIndex}");
            EnsureCapacity(
                ref _maskedDepthCompactedDrawBuffers[frameIndex],
                maskedDepthLayout.RequiredBackingElements,
                DrawCommandStride,
                $"SceneSubmission.MaskedDepthCompactedMeshletDraw.Frame{frameIndex}");
            for (int cascade = 0; cascade < DirectionalShadowCascadeCapacity; cascade++)
            {
                EnsureCapacity(
                    ref _directionalStaticShadowCompactedDrawBuffers[frameIndex, cascade],
                    directionalStaticShadowLayout.RequiredBackingElements,
                    DrawCommandStride,
                    $"SceneSubmission.DirectionalStaticShadowCompacted.Frame{frameIndex}.Cascade{cascade}");
                EnsureCapacity(
                    ref _directionalDynamicShadowCompactedDrawBuffers[frameIndex, cascade],
                    directionalDynamicShadowLayout.RequiredBackingElements,
                    DrawCommandStride,
                    $"SceneSubmission.DirectionalDynamicShadowCompacted.Frame{frameIndex}.Cascade{cascade}");
            }
            EnsureCapacity(
                ref _counterBuffers[frameIndex],
                1u,
                CounterStride,
                $"SceneSubmission.Counter.Frame{frameIndex}");
            EnsureCapacity(
                ref _indirectDispatchBuffers[frameIndex],
                IndirectDispatchSlotCount,
                IndirectDispatchStride,
                $"SceneSubmission.OpaqueIndirectDispatch.Frame{frameIndex}",
                BufferUsageFlags.IndirectBufferBit);
            uint requiredLodHistory = checked((uint)Math.Clamp(
                objectCount,
                1,
                MaximumLodTransitionStateCount));
            int latestSubmittedFrame =
                (frameIndex + RenderingConstants.FramesInFlight - 1) %
                RenderingConstants.FramesInFlight;
            Fence lodHistoryRetirementFence =
                _synchronization.GetInFlightFence(latestSubmittedFrame);
            for (int historyFrame = 0;
                 historyFrame < _lodHistoryBuffers.Length;
                 historyFrame++)
            {
                EnsureCapacity(
                    ref _lodHistoryBuffers[historyFrame],
                    requiredLodHistory,
                    (ulong)Marshal.SizeOf<GPUSceneLodTransitionState>(),
                    $"SceneSubmission.GpuLodHistory.Frame{historyFrame}",
                    retirementFence: lodHistoryRetirementFence);
            }
            UpdateRegisteredBindlessBuffers(frameIndex);
        }

        private static uint BuildCompactionFlags(
            SceneRenderingData sceneData,
            bool compactDirectionalShadows)
        {
            uint flags = 1u;
            if (sceneData.SceneSubmissionGpuLodSelectionEnabled)
                flags |= 1u << 1;
            if (compactDirectionalShadows)
                flags |= 1u << 2;
            if (sceneData.SceneSubmissionSidedRasterSpecializationActive)
                flags |= 1u << 3;
            if (sceneData.SceneSubmissionGpuInstanceExpansionActive)
                flags |= 1u << 4;
            if (sceneData.SceneSubmissionGpuLodDitherTransitionsActive)
                flags |= 1u << 5;
            if (sceneData.SceneSubmissionGpuHierarchicalLodActive)
                flags |= 1u << 6;
            return flags;
        }

        private static bool CanUseInstanceExpansion(
            SceneRenderingData sceneData)
        {
            if (!sceneData.SceneSubmissionGpuInstanceExpansionEnabled ||
                sceneData.SceneInstanceCandidateCount <= 0 ||
                !sceneData.SceneInstanceCandidateBuffer.IsValid)
            {
                return false;
            }

            ulong requiredBytes = checked(
                (ulong)sceneData.SceneInstanceCandidateCount *
                (ulong)Marshal.SizeOf<GPUSceneInstanceCandidate>());
            return sceneData.SceneInstanceCandidateBufferSize >= requiredBytes;
        }

        private static bool CanUseSidedStreams(
            SceneRenderingData sceneData,
            int simpleCandidateCount,
            int simpleNormalCandidateCount,
            int fullCandidateCount)
        {
            if (!sceneData.DepthPrePassEnabled ||
                !sceneData.SceneSubmissionIndirectMeshletDispatchEnabled)
            {
                return false;
            }

            return CanPackForwardStream(
                       BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase,
                       simpleCandidateCount) &&
                   CanPackForwardStream(
                       BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase,
                       simpleNormalCandidateCount) &&
                   CanPackForwardStream(
                       BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase,
                       fullCandidateCount);
        }

        private static bool CanPackForwardStream(
            int bufferBaseIndex,
            int candidateCount) =>
            GPUForwardPushConstants.TryPackTransparentDrawRange(
                checked((uint)bufferBaseIndex),
                checked((uint)Math.Max(1, candidateCount)),
                out _);

        internal static SidedStreamCapacityPlan ResolveSidedStreamCapacityPlan(
            int candidateCount,
            int doubleSidedCandidateCount,
            int maximumEmissionMultiplier,
            bool sidedStreams,
            bool asymmetricRequested)
        {
            int candidates = Math.Max(0, candidateCount);
            int multiplier = Math.Max(1, maximumEmissionMultiplier);
            int logicalCapacity = checked(candidates * multiplier);
            if (!sidedStreams)
            {
                return new SidedStreamCapacityPlan(
                    logicalCapacity,
                    0,
                    0,
                    checked((uint)Math.Max(1, logicalCapacity)),
                    false);
            }

            bool exactCountsValid = doubleSidedCandidateCount >= 0 &&
                                    doubleSidedCandidateCount <= candidates;
            if (!asymmetricRequested || !exactCountsValid)
            {
                return new SidedStreamCapacityPlan(
                    logicalCapacity,
                    logicalCapacity,
                    logicalCapacity,
                    checked((uint)Math.Max(
                        1,
                        checked(logicalCapacity * 2))),
                    false);
            }

            int doubleSidedCapacity = checked(
                doubleSidedCandidateCount * multiplier);
            int oneSidedCapacity = checked(
                (candidates - doubleSidedCandidateCount) * multiplier);
            int doubleSidedBase = oneSidedCapacity;
            int totalCapacity = checked(
                oneSidedCapacity + doubleSidedCapacity);
            return new SidedStreamCapacityPlan(
                oneSidedCapacity,
                doubleSidedBase,
                doubleSidedCapacity,
                checked((uint)Math.Max(1, totalCapacity)),
                true);
        }

        internal static bool SidedStreamCountsAreValid(
            SceneRenderingData sceneData,
            int solidDepthCandidateCount,
            int maskedDepthCandidateCount,
            int directionalStaticShadowCandidateCount,
            int directionalDynamicShadowCandidateCount)
        {
            ArgumentNullException.ThrowIfNull(sceneData);
            if (!sceneData.SidedStreamCandidateCountsValid)
                return false;

            return IsSidedCountValid(
                       sceneData.SimpleOpaqueMeshletCount,
                       sceneData.DoubleSidedSimpleOpaqueMeshletCount) &&
                   IsSidedCountValid(
                       sceneData.SimpleNormalOpaqueMeshletCount,
                       sceneData.DoubleSidedSimpleNormalOpaqueMeshletCount) &&
                   IsSidedCountValid(
                       sceneData.FullOpaqueMeshletCount,
                       sceneData.DoubleSidedFullOpaqueMeshletCount) &&
                   IsSidedCountValid(
                       solidDepthCandidateCount,
                       sceneData.DepthPrePassEnabled
                           ? sceneData.DoubleSidedSolidDepthMeshletCount
                           : 0) &&
                   IsSidedCountValid(
                       maskedDepthCandidateCount,
                       sceneData.DepthPrePassEnabled
                           ? sceneData.DoubleSidedMaskedDepthMeshletCount
                           : 0) &&
                   IsSidedCountValid(
                       directionalStaticShadowCandidateCount,
                       directionalStaticShadowCandidateCount > 0
                           ? sceneData
                               .DoubleSidedDirectionalStaticShadowMeshletCount
                           : 0) &&
                   IsSidedCountValid(
                       directionalDynamicShadowCandidateCount,
                       directionalDynamicShadowCandidateCount > 0
                           ? sceneData
                               .DoubleSidedDirectionalDynamicShadowMeshletCount
                           : 0) &&
                   checked(
                       sceneData.DoubleSidedSimpleOpaqueMeshletCount +
                       sceneData.DoubleSidedSimpleNormalOpaqueMeshletCount +
                       sceneData.DoubleSidedFullOpaqueMeshletCount) ==
                   sceneData.DoubleSidedOpaqueMeshletCount;
        }

        private static bool IsSidedCountValid(
            int candidateCount,
            int doubleSidedCandidateCount) =>
            candidateCount >= 0 &&
            doubleSidedCandidateCount >= 0 &&
            doubleSidedCandidateCount <= candidateCount;

        internal static int ResolveCompactedDrawStreamCapacity(
            int candidateCount,
            int publishedCapacity,
            bool sidedStreams)
        {
            int clampedCandidateCount = Math.Max(0, candidateCount);
            int clampedPublishedCapacity = Math.Max(0, publishedCapacity);
            if (clampedCandidateCount == 0 || clampedPublishedCapacity == 0)
                return 0;

            // Sided compaction stores the double-sided partition at the
            // published logical capacity. LOD dither transitions can make that
            // capacity larger than the source candidate count, so consumers
            // must preserve it as both their draw bound and second-range
            // offset. Unspecialized streams remain dense from element zero.
            return sidedStreams
                ? clampedPublishedCapacity
                : Math.Min(clampedCandidateCount, clampedPublishedCapacity);
        }

        private void EnsureCapacity(
            ref RuntimeBuffer buffer,
            uint requiredElements,
            ulong stride,
            string debugName,
            BufferUsageFlags extraUsage = 0,
            Fence retirementFence = default)
        {
            uint required = Math.Max(1u, requiredElements);
            if (buffer.Handle.IsValid && required <= buffer.ElementCapacity)
                return;

            uint newCapacity = buffer.Handle.IsValid ? buffer.ElementCapacity : 1u;
            while (newCapacity < required)
                newCapacity = checked(newCapacity * 2u);

            BufferHandle previous = buffer.Handle;
            ulong byteSize = checked(newCapacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit | extraUsage,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                $"{debugName} ({newCapacity} elements)");
            try
            {
                _context.SetDebugName(
                    _bufferManager.GetBuffer(handle).Handle,
                    ObjectType.Buffer,
                    debugName);
                if (previous.IsValid)
                {
                    if (retirementFence.Handle != 0)
                    {
                        _deleter.QueueBufferDeletion(
                            retirementFence,
                            previous,
                            _bufferManager);
                    }
                    else
                    {
                        _bufferManager.DestroyBuffer(previous);
                    }
                }
            }
            catch
            {
                _bufferManager.DestroyBuffer(handle);
                throw;
            }
            buffer = new RuntimeBuffer(handle, newCapacity, byteSize);
        }

        private void UpdateRegisteredBindlessBuffers(int frameIndex)
        {
            RegisterStorageBuffer(BindlessIndex.SceneOpaqueCompactedMeshletDrawBufferBase + frameIndex, _compactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneSimpleOpaqueCompactedMeshletDrawBufferBase + frameIndex, _simpleCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneSimpleNormalOpaqueCompactedMeshletDrawBufferBase + frameIndex, _simpleNormalCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneFullOpaqueCompactedMeshletDrawBufferBase + frameIndex, _fullCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneSolidDepthCompactedMeshletDrawBufferBase + frameIndex, _solidDepthCompactedDrawBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneMaskedDepthCompactedMeshletDrawBufferBase + frameIndex, _maskedDepthCompactedDrawBuffers[frameIndex].Handle);
            for (int cascade = 0; cascade < DirectionalShadowCascadeCapacity; cascade++)
            {
                RegisterStorageBuffer(
                    GetDirectionalStaticShadowCompactedBufferBaseIndex(cascade) + frameIndex,
                    _directionalStaticShadowCompactedDrawBuffers[frameIndex, cascade].Handle);
                RegisterStorageBuffer(
                    GetDirectionalDynamicShadowCompactedBufferBaseIndex(cascade) + frameIndex,
                    _directionalDynamicShadowCompactedDrawBuffers[frameIndex, cascade].Handle);
            }
            RegisterStorageBuffer(BindlessIndex.SceneSubmissionCounterBufferBase + frameIndex, _counterBuffers[frameIndex].Handle);
            RegisterStorageBuffer(BindlessIndex.SceneOpaqueIndirectDispatchBufferBase + frameIndex, _indirectDispatchBuffers[frameIndex].Handle);
            for (int historyFrame = 0;
                 historyFrame < _lodHistoryBuffers.Length;
                 historyFrame++)
            {
                RegisterStorageBuffer(
                    BindlessIndex.SceneGpuLodHistoryBufferBase + historyFrame,
                    _lodHistoryBuffers[historyFrame].Handle);
            }
        }

        private void PrepareLodHistory(
            CommandBuffer cmd,
            SceneRenderingData sceneData)
        {
            uint logicalCapacity = checked((uint)Math.Clamp(
                sceneData.ObjectCount,
                1,
                MaximumLodTransitionStateCount));
            bool reset = !_lodHistoryInitialized ||
                         _lodHistorySceneRevision !=
                         sceneData.SceneContentRevision ||
                         _lodHistoryLogicalCapacity < logicalCapacity ||
                         _lodHistoryMode !=
                         sceneData.SceneSubmissionGpuLodSelectionMode;
            if (!reset)
                return;

            Span<BufferMemoryBarrier2> barriers =
                stackalloc BufferMemoryBarrier2[
                    RenderingConstants.FramesInFlight];
            for (int historyFrame = 0;
                 historyFrame < _lodHistoryBuffers.Length;
                 historyFrame++)
            {
                RuntimeBuffer history = _lodHistoryBuffers[historyFrame];
                VkBuffer buffer = _bufferManager.GetBuffer(history.Handle);
                _context.Api.CmdFillBuffer(
                    cmd,
                    buffer,
                    0,
                    history.ByteSize,
                    uint.MaxValue);
                barriers[historyFrame] = BarrierBuilder.BufferBarrier(
                    buffer,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    history.ByteSize);
            }
            ExecuteBarriers(cmd, barriers);

            _lodHistoryInitialized = true;
            _lodHistorySceneRevision = sceneData.SceneContentRevision;
            _lodHistoryLogicalCapacity = logicalCapacity;
            _lodHistoryMode =
                sceneData.SceneSubmissionGpuLodSelectionMode;
        }

        private static float ResolveLodProjectionScale(
            SceneRenderingData sceneData)
        {
            float scale = 0.5f * Math.Max(1u, sceneData.ScreenHeight) *
                          Math.Abs(sceneData.ProjectionMatrix.M22);
            return float.IsFinite(scale) && scale > 0f
                ? scale
                : 0.5f * Math.Max(1u, sceneData.ScreenHeight);
        }

        private void InitializeDirectionalShadowRuntimeState(
            SceneRenderingData sceneData,
            int staticCandidateCount,
            int dynamicCandidateCount,
            in SidedStreamCapacityPlan staticLayout,
            in SidedStreamCapacityPlan dynamicLayout,
            uint staticCascadeMask)
        {
            sceneData.SceneSubmissionGpuDirectionalShadowCandidateCount =
                checked(staticCandidateCount + dynamicCandidateCount);
            for (int cascade = 0; cascade < DirectionalShadowCascadeCapacity; cascade++)
            {
                sceneData.SceneSubmissionGpuDirectionalStaticShadowCandidateCounts[cascade] =
                    (staticCascadeMask & (1u << cascade)) != 0u ? staticCandidateCount : 0;
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowCandidateCounts[cascade] = dynamicCandidateCount;
                sceneData.SceneSubmissionGpuDirectionalStaticShadowCapacities[cascade] =
                    staticLayout.OneSidedCapacity;
                sceneData.SceneSubmissionGpuDirectionalStaticShadowDoubleSidedBases[cascade] =
                    staticLayout.DoubleSidedBase;
                sceneData.SceneSubmissionGpuDirectionalStaticShadowDoubleSidedCapacities[cascade] =
                    staticLayout.DoubleSidedCapacity;
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowCapacities[cascade] =
                    dynamicLayout.OneSidedCapacity;
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowDoubleSidedBases[cascade] =
                    dynamicLayout.DoubleSidedBase;
                sceneData.SceneSubmissionGpuDirectionalDynamicShadowDoubleSidedCapacities[cascade] =
                    dynamicLayout.DoubleSidedCapacity;
            }
        }

        private ulong SumDirectionalShadowBufferBytes(int frameIndex)
        {
            ulong bytes = 0;
            for (int cascade = 0; cascade < DirectionalShadowCascadeCapacity; cascade++)
            {
                bytes = checked(bytes + _directionalStaticShadowCompactedDrawBuffers[frameIndex, cascade].ByteSize);
                bytes = checked(bytes + _directionalDynamicShadowCompactedDrawBuffers[frameIndex, cascade].ByteSize);
            }

            return bytes;
        }

        public static int GetDirectionalStaticShadowCompactedBufferBaseIndex(int cascade)
        {
            return cascade switch
            {
                0 => BindlessIndex.SceneDirectionalStaticShadowCompactedCascade0BufferBase,
                1 => BindlessIndex.SceneDirectionalStaticShadowCompactedCascade1BufferBase,
                2 => BindlessIndex.SceneDirectionalStaticShadowCompactedCascade2BufferBase,
                3 => BindlessIndex.SceneDirectionalStaticShadowCompactedCascade3BufferBase,
                _ => throw new ArgumentOutOfRangeException(nameof(cascade), cascade, "Directional shadow cascade is outside the supported range.")
            };
        }

        public static ulong GetOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(OpaqueIndirectDispatchSlot);
        }

        public static ulong GetSimpleOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(SimpleOpaqueIndirectDispatchSlot);
        }

        public static ulong GetSimpleNormalOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(SimpleNormalOpaqueIndirectDispatchSlot);
        }

        public static ulong GetFullOpaqueIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(FullOpaqueIndirectDispatchSlot);
        }

        public static ulong GetSimpleOpaqueDoubleSidedIndirectDispatchOffset() =>
            GetIndirectDispatchOffset(
                SimpleOpaqueDoubleSidedIndirectDispatchSlot);

        public static ulong GetSimpleNormalOpaqueDoubleSidedIndirectDispatchOffset() =>
            GetIndirectDispatchOffset(
                SimpleNormalOpaqueDoubleSidedIndirectDispatchSlot);

        public static ulong GetFullOpaqueDoubleSidedIndirectDispatchOffset() =>
            GetIndirectDispatchOffset(
                FullOpaqueDoubleSidedIndirectDispatchSlot);

        public static ulong GetSolidDepthIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(SolidDepthIndirectDispatchSlot);
        }

        public static ulong GetMaskedDepthIndirectDispatchOffset()
        {
            return GetIndirectDispatchOffset(MaskedDepthIndirectDispatchSlot);
        }

        public static ulong GetSolidDepthDoubleSidedIndirectDispatchOffset() =>
            GetIndirectDispatchOffset(
                SolidDepthDoubleSidedIndirectDispatchSlot);

        public static ulong GetMaskedDepthDoubleSidedIndirectDispatchOffset() =>
            GetIndirectDispatchOffset(
                MaskedDepthDoubleSidedIndirectDispatchSlot);

        public static ulong GetDirectionalStaticShadowIndirectDispatchOffset(int cascade)
        {
            ValidateDirectionalCascade(cascade);
            return GetIndirectDispatchOffset(DirectionalStaticShadowIndirectDispatchSlotBase + cascade);
        }

        public static ulong GetDirectionalDynamicShadowIndirectDispatchOffset(int cascade)
        {
            ValidateDirectionalCascade(cascade);
            return GetIndirectDispatchOffset(DirectionalDynamicShadowIndirectDispatchSlotBase + cascade);
        }

        public static ulong GetDirectionalStaticShadowDoubleSidedIndirectDispatchOffset(
            int cascade)
        {
            ValidateDirectionalCascade(cascade);
            return GetIndirectDispatchOffset(
                DirectionalStaticShadowDoubleSidedIndirectDispatchSlotBase +
                cascade);
        }

        public static ulong GetDirectionalDynamicShadowDoubleSidedIndirectDispatchOffset(
            int cascade)
        {
            ValidateDirectionalCascade(cascade);
            return GetIndirectDispatchOffset(
                DirectionalDynamicShadowDoubleSidedIndirectDispatchSlotBase +
                cascade);
        }

        private static ulong GetIndirectDispatchOffset(int slot)
        {
            return checked((ulong)slot * IndirectDispatchStride);
        }

        private static void ValidateDirectionalCascade(int cascade)
        {
            if ((uint)cascade >= DirectionalShadowCascadeCapacity)
                throw new ArgumentOutOfRangeException(nameof(cascade), cascade, "Directional shadow cascade is outside the supported range.");
        }

        public static int GetDirectionalDynamicShadowCompactedBufferBaseIndex(int cascade)
        {
            return cascade switch
            {
                0 => BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade0BufferBase,
                1 => BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade1BufferBase,
                2 => BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade2BufferBase,
                3 => BindlessIndex.SceneDirectionalDynamicShadowCompactedCascade3BufferBase,
                _ => throw new ArgumentOutOfRangeException(nameof(cascade), cascade, "Directional shadow cascade is outside the supported range.")
            };
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            if (!handle.IsValid)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _bindlessHeap.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private (ulong ClearedBytes, int BarrierCount) ResetOutputs(
            CommandBuffer cmd,
            int frameIndex,
            SceneOpaqueResetPlan resetPlan,
            RuntimeBuffer drawBuffer,
            RuntimeBuffer simpleDrawBuffer,
            RuntimeBuffer simpleNormalDrawBuffer,
            RuntimeBuffer fullDrawBuffer,
            RuntimeBuffer solidDepthDrawBuffer,
            RuntimeBuffer maskedDepthDrawBuffer,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer)
        {
            VkBuffer draw = _bufferManager.GetBuffer(drawBuffer.Handle);
            VkBuffer simpleDraw = _bufferManager.GetBuffer(simpleDrawBuffer.Handle);
            VkBuffer simpleNormalDraw = _bufferManager.GetBuffer(simpleNormalDrawBuffer.Handle);
            VkBuffer fullDraw = _bufferManager.GetBuffer(fullDrawBuffer.Handle);
            VkBuffer solidDepthDraw = _bufferManager.GetBuffer(solidDepthDrawBuffer.Handle);
            VkBuffer maskedDepthDraw = _bufferManager.GetBuffer(maskedDepthDrawBuffer.Handle);
            VkBuffer counters = _bufferManager.GetBuffer(counterBuffer.Handle);
            VkBuffer indirect = _bufferManager.GetBuffer(indirectDispatchBuffer.Handle);
            _context.Api.CmdFillBuffer(cmd, counters, 0, counterBuffer.ByteSize, 0u);
            ulong clearedBytes = counterBuffer.ByteSize;
            if (resetPlan.ClearPayloads)
            {
                _context.Api.CmdFillBuffer(cmd, draw, 0, drawBuffer.ByteSize, 0xffffffffu);
                _context.Api.CmdFillBuffer(cmd, simpleDraw, 0, simpleDrawBuffer.ByteSize, 0xffffffffu);
                _context.Api.CmdFillBuffer(cmd, simpleNormalDraw, 0, simpleNormalDrawBuffer.ByteSize, 0xffffffffu);
                _context.Api.CmdFillBuffer(cmd, fullDraw, 0, fullDrawBuffer.ByteSize, 0xffffffffu);
                _context.Api.CmdFillBuffer(cmd, solidDepthDraw, 0, solidDepthDrawBuffer.ByteSize, 0xffffffffu);
                _context.Api.CmdFillBuffer(cmd, maskedDepthDraw, 0, maskedDepthDrawBuffer.ByteSize, 0xffffffffu);
                clearedBytes = checked(
                    clearedBytes + drawBuffer.ByteSize +
                    simpleDrawBuffer.ByteSize +
                    simpleNormalDrawBuffer.ByteSize +
                    fullDrawBuffer.ByteSize +
                    solidDepthDrawBuffer.ByteSize +
                    maskedDepthDrawBuffer.ByteSize);
                for (int cascade = 0;
                     cascade < resetPlan.ActiveDirectionalCascadeCount;
                     cascade++)
                {
                    RuntimeBuffer staticShadow =
                        _directionalStaticShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade];
                    RuntimeBuffer dynamicShadow =
                        _directionalDynamicShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade];
                    if (resetPlan.ClearsStaticShadowCascade(cascade))
                    {
                        _context.Api.CmdFillBuffer(
                            cmd,
                            _bufferManager.GetBuffer(staticShadow.Handle),
                            0,
                            staticShadow.ByteSize,
                            0xffffffffu);
                        clearedBytes = checked(
                            clearedBytes + staticShadow.ByteSize);
                    }
                    if (resetPlan.ClearsDynamicShadowCascade(cascade))
                    {
                        _context.Api.CmdFillBuffer(
                            cmd,
                            _bufferManager.GetBuffer(dynamicShadow.Handle),
                            0,
                            dynamicShadow.ByteSize,
                            0xffffffffu);
                        clearedBytes = checked(
                            clearedBytes + dynamicShadow.ByteSize);
                    }
                }
            }
            // Keep X=0, Y=1, Z=1 in one transfer write. Split fills overlap and do not
            // guarantee that the later Y/Z values win without an intervening barrier.
            Span<GPUFoliageDispatchArgs> initialDispatchArgs =
                stackalloc GPUFoliageDispatchArgs[IndirectDispatchSlotCount];
            for (int slot = 0; slot < initialDispatchArgs.Length; slot++)
            {
                initialDispatchArgs[slot] = new GPUFoliageDispatchArgs
                {
                    GroupCountX = 0u,
                    GroupCountY = 1u,
                    GroupCountZ = 1u,
                    Padding0 = 0u
                };
            }
            _context.Api.CmdUpdateBuffer(cmd, indirect, 0, initialDispatchArgs);
            clearedBytes = checked(
                clearedBytes + indirectDispatchBuffer.ByteSize);

            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[16];
            int barrierIndex = 0;
            barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                counters,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                counterBuffer.ByteSize);
            if (resetPlan.ClearPayloads)
            {
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    draw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    drawBuffer.ByteSize);
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    simpleDraw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    simpleDrawBuffer.ByteSize);
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    simpleNormalDraw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    simpleNormalDrawBuffer.ByteSize);
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    fullDraw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    fullDrawBuffer.ByteSize);
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    solidDepthDraw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    solidDepthDrawBuffer.ByteSize);
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    maskedDepthDraw,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferWriteBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageWriteBit,
                    0,
                    maskedDepthDrawBuffer.ByteSize);
                for (int cascade = 0;
                     cascade < resetPlan.ActiveDirectionalCascadeCount;
                     cascade++)
                {
                    RuntimeBuffer staticShadow =
                        _directionalStaticShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade];
                    RuntimeBuffer dynamicShadow =
                        _directionalDynamicShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade];
                    if (resetPlan.ClearsStaticShadowCascade(cascade))
                    {
                        barriers[barrierIndex++] =
                            BarrierBuilder.BufferBarrier(
                                _bufferManager.GetBuffer(
                                    staticShadow.Handle),
                                PipelineStageFlags2.TransferBit,
                                AccessFlags2.TransferWriteBit,
                                PipelineStageFlags2.ComputeShaderBit,
                                AccessFlags2.ShaderStorageWriteBit,
                                0,
                                staticShadow.ByteSize);
                    }
                    if (resetPlan.ClearsDynamicShadowCascade(cascade))
                    {
                        barriers[barrierIndex++] =
                            BarrierBuilder.BufferBarrier(
                                _bufferManager.GetBuffer(
                                    dynamicShadow.Handle),
                                PipelineStageFlags2.TransferBit,
                                AccessFlags2.TransferWriteBit,
                                PipelineStageFlags2.ComputeShaderBit,
                                AccessFlags2.ShaderStorageWriteBit,
                                0,
                                dynamicShadow.ByteSize);
                    }
                }
            }
            barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                indirect,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                0,
                indirectDispatchBuffer.ByteSize);
            ExecuteBarriers(cmd, barriers[..barrierIndex]);
            return (clearedBytes, barrierIndex);
        }

        private int RecordOutputBarrier(
            CommandBuffer cmd,
            int frameIndex,
            SceneOpaqueResetPlan resetPlan,
            RuntimeBuffer drawBuffer,
            RuntimeBuffer simpleDrawBuffer,
            RuntimeBuffer simpleNormalDrawBuffer,
            RuntimeBuffer fullDrawBuffer,
            RuntimeBuffer solidDepthDrawBuffer,
            RuntimeBuffer maskedDepthDrawBuffer,
            RuntimeBuffer counterBuffer,
            RuntimeBuffer indirectDispatchBuffer,
            uint aggregateCapacity,
            uint simpleElementCount,
            uint simpleNormalElementCount,
            uint fullElementCount,
            uint solidDepthElementCount,
            uint maskedDepthElementCount,
            uint directionalStaticElementCount,
            uint directionalDynamicElementCount,
            bool forwardVisibilityCompactionEnabled,
            bool validationReadbackEnabled)
        {
            Span<BufferMemoryBarrier2> barriers = stackalloc BufferMemoryBarrier2[16];
            int barrierIndex = 0;
            PipelineStageFlags2 payloadSourceStages =
                PipelineStageFlags2.ComputeShaderBit |
                (resetPlan.ClearPayloads
                    ? PipelineStageFlags2.TransferBit
                    : PipelineStageFlags2.None);
            AccessFlags2 payloadSourceAccess =
                AccessFlags2.ShaderStorageWriteBit |
                (resetPlan.ClearPayloads
                    ? AccessFlags2.TransferWriteBit
                    : AccessFlags2.None);
            barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(counterBuffer.Handle),
                PipelineStageFlags2.TransferBit |
                    PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.TransferWriteBit |
                    AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit |
                    PipelineStageFlags2.TaskShaderBitExt |
                    PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderStorageReadBit |
                    AccessFlags2.TransferReadBit,
                0,
                counterBuffer.ByteSize);

            if (validationReadbackEnabled && aggregateCapacity != 0u)
            {
                barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                    _bufferManager.GetBuffer(drawBuffer.Handle),
                    payloadSourceStages,
                    payloadSourceAccess,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    0,
                    PayloadBytes(drawBuffer, aggregateCapacity));
            }

            PipelineStageFlags2 opaqueConsumerStages =
                PipelineStageFlags2.MeshShaderBitExt |
                (forwardVisibilityCompactionEnabled
                    ? PipelineStageFlags2.ComputeShaderBit
                    : PipelineStageFlags2.None);
            AppendPayloadBarrier(
                barriers,
                ref barrierIndex,
                payloadSourceStages,
                payloadSourceAccess,
                simpleDrawBuffer,
                simpleElementCount,
                opaqueConsumerStages);
            AppendPayloadBarrier(
                barriers,
                ref barrierIndex,
                payloadSourceStages,
                payloadSourceAccess,
                simpleNormalDrawBuffer,
                simpleNormalElementCount,
                opaqueConsumerStages);
            AppendPayloadBarrier(
                barriers,
                ref barrierIndex,
                payloadSourceStages,
                payloadSourceAccess,
                fullDrawBuffer,
                fullElementCount,
                opaqueConsumerStages);
            AppendPayloadBarrier(
                barriers,
                ref barrierIndex,
                payloadSourceStages,
                payloadSourceAccess,
                solidDepthDrawBuffer,
                solidDepthElementCount,
                PipelineStageFlags2.MeshShaderBitExt);
            AppendPayloadBarrier(
                barriers,
                ref barrierIndex,
                payloadSourceStages,
                payloadSourceAccess,
                maskedDepthDrawBuffer,
                maskedDepthElementCount,
                PipelineStageFlags2.MeshShaderBitExt);

            for (int cascade = 0;
                 cascade < resetPlan.ActiveDirectionalCascadeCount;
                 cascade++)
            {
                if ((resetPlan.StaticShadowCascadeMask &
                     (1u << cascade)) != 0u)
                {
                    AppendPayloadBarrier(
                        barriers,
                        ref barrierIndex,
                        payloadSourceStages,
                        payloadSourceAccess,
                        _directionalStaticShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade],
                        directionalStaticElementCount,
                        PipelineStageFlags2.MeshShaderBitExt);
                }
                if (resetPlan.ClearDynamicShadowPayloads)
                {
                    AppendPayloadBarrier(
                        barriers,
                        ref barrierIndex,
                        payloadSourceStages,
                        payloadSourceAccess,
                        _directionalDynamicShadowCompactedDrawBuffers[
                            frameIndex,
                            cascade],
                        directionalDynamicElementCount,
                        PipelineStageFlags2.MeshShaderBitExt);
                }
            }

            barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(indirectDispatchBuffer.Handle),
                PipelineStageFlags2.TransferBit |
                    PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.TransferWriteBit |
                    AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.DrawIndirectBit,
                AccessFlags2.IndirectCommandReadBit,
                0,
                indirectDispatchBuffer.ByteSize);
            ExecuteBarriers(cmd, barriers[..barrierIndex]);
            return barrierIndex;
        }

        private void AppendPayloadBarrier(
            Span<BufferMemoryBarrier2> barriers,
            ref int barrierIndex,
            PipelineStageFlags2 sourceStages,
            AccessFlags2 sourceAccess,
            RuntimeBuffer buffer,
            uint elementCount,
            PipelineStageFlags2 destinationStages)
        {
            if (elementCount == 0u)
                return;
            barriers[barrierIndex++] = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(buffer.Handle),
                sourceStages,
                sourceAccess,
                destinationStages,
                AccessFlags2.ShaderStorageReadBit,
                0,
                PayloadBytes(buffer, elementCount));
        }

        private static ulong PayloadBytes(
            RuntimeBuffer buffer,
            uint elementCount) =>
            Math.Min(
                buffer.ByteSize,
                checked((ulong)elementCount * DrawCommandStride));

        private void RecordLodHistoryBarrier(
            CommandBuffer cmd,
            int frameIndex)
        {
            RuntimeBuffer history = _lodHistoryBuffers[frameIndex];
            BufferMemoryBarrier2 barrier = BarrierBuilder.BufferBarrier(
                _bufferManager.GetBuffer(history.Handle),
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                0,
                history.ByteSize);
            ExecuteBarrier(cmd, barrier);
        }

        private void EnsureCounterReadbackBuffer(int frameIndex)
        {
            if (_counterReadbackBuffers[frameIndex].IsValid)
                return;

            _counterReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                CounterStride,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"SceneSubmission.CounterReadback.Frame{frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }

        private void EnsureValidationReadbackBuffer(int frameIndex)
        {
            if (_validationReadbackBuffers[frameIndex].IsValid)
                return;

            _validationReadbackBuffers[frameIndex] = _bufferManager.CreateBuffer(
                ValidationReadbackBytes,
                BufferUsageFlags.TransferDstBit,
                Vma.MemoryUsage.AutoPreferHost,
                Vma.AllocationCreateFlags.MappedBit | Vma.AllocationCreateFlags.HostAccessRandomBit,
                $"SceneSubmission.ValidationReadback.Frame{frameIndex}",
                MemoryBudgetCategory.DiagnosticsAndDebug);
        }

        private void RecordCounterReadback(CommandBuffer cmd, int frameIndex, RuntimeBuffer counterBuffer)
        {
            EnsureCounterReadbackBuffer(frameIndex);
            VkBuffer source = _bufferManager.GetBuffer(counterBuffer.Handle);
            VkBuffer destination = _bufferManager.GetBuffer(_counterReadbackBuffers[frameIndex]);

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = CounterStride
            };
            _context.Api.CmdCopyBuffer(cmd, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                CounterStride);
            ExecuteBarrier(cmd, afterCopy);
            _counterReadbackRecorded[frameIndex] = true;
        }

        private void RecordValidationReadback(CommandBuffer cmd, int frameIndex, RuntimeBuffer drawBuffer)
        {
            EnsureValidationReadbackBuffer(frameIndex);
            VkBuffer source = _bufferManager.GetBuffer(drawBuffer.Handle);
            VkBuffer destination = _bufferManager.GetBuffer(_validationReadbackBuffers[frameIndex]);
            ulong copyBytes = Math.Min(drawBuffer.ByteSize, ValidationReadbackBytes);
            if (copyBytes == 0)
                return;

            BufferCopy copy = new()
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = copyBytes
            };
            _context.Api.CmdCopyBuffer(cmd, source, destination, 1, &copy);

            BufferMemoryBarrier2 afterCopy = BarrierBuilder.BufferBarrier(
                destination,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.HostBit,
                AccessFlags2.HostReadBit,
                0,
                copyBytes);
            ExecuteBarrier(cmd, afterCopy);
            _validationReadbackRecorded[frameIndex] = true;
        }

        private void CaptureExpectedValidationFrame(int frameIndex, SceneRenderingData sceneData)
        {
            int cpuCount = checked(sceneData.SimpleOpaqueMeshletCount +
                sceneData.SimpleNormalOpaqueMeshletCount +
                sceneData.FullOpaqueMeshletCount);
            int sampleCount = Math.Min(cpuCount, MaxValidationSampleCommands);
            var expected = new ValidationCommandKey[sampleCount];
            int writeIndex = 0;
            for (int i = 0; i < sceneData.SimpleOpaqueMeshletCount && writeIndex < sampleCount; i++)
            {
                expected[writeIndex++] = CreateValidationKey(
                    sceneData.MeshletDrawCommands[i],
                    sceneData.ObjectData,
                    ValidationPathBucket.SimpleOpaque);
            }

            for (int i = 0; i < sceneData.SimpleNormalOpaqueMeshletDrawCommands.Count && writeIndex < sampleCount; i++)
            {
                expected[writeIndex++] = CreateValidationKey(
                    sceneData.SimpleNormalOpaqueMeshletDrawCommands[i],
                    sceneData.ObjectData,
                    ValidationPathBucket.SimpleNormalOpaque);
            }

            for (int i = 0; i < sceneData.FullOpaqueMeshletDrawCommands.Count && writeIndex < sampleCount; i++)
            {
                expected[writeIndex++] = CreateValidationKey(
                    sceneData.FullOpaqueMeshletDrawCommands[i],
                    sceneData.ObjectData,
                    ValidationPathBucket.FullOpaque);
            }

            _validationExpectedFrames[frameIndex] = new ValidationExpectedFrame(
                true,
                cpuCount,
                sampleCount,
                sceneData.OcclusionCullingEnabled && sceneData.HiZMipCount > 0,
                sceneData.SceneSubmissionGpuLodSelectionEnabled,
                expected);
        }

        private SceneSubmissionValidationSnapshot ReadCompletedValidation(
            int frameIndex,
            SceneSubmissionCounterSnapshot counters)
        {
            ValidationExpectedFrame expectedFrame = _validationExpectedFrames[frameIndex];
            if (!expectedFrame.Valid)
                return SceneSubmissionValidationSnapshot.Invalid;

            if (!_validationReadbackRecorded[frameIndex] || !_validationReadbackBuffers[frameIndex].IsValid)
            {
                return new SceneSubmissionValidationSnapshot(
                    0,
                    "pending",
                    expectedFrame.CpuCount,
                    ClampUIntToInt(counters.EmittedCount),
                    0,
                    0,
                    MaxValidationSampleCommands,
                    "GPU validation readback is not available yet.");
            }

            int gpuCount = ClampUIntToInt(counters.EmittedCount);
            bool compareFullSample = expectedFrame.CpuCount <= MaxValidationSampleCommands &&
                                     gpuCount <= MaxValidationSampleCommands;
            int gpuSampleCount = compareFullSample
                ? Math.Min(gpuCount, expectedFrame.SampleCount)
                : 0;
            _bufferManager.InvalidateBuffer(_validationReadbackBuffers[frameIndex], 0, ValidationReadbackBytes);
            GPUMeshletDrawCommand* gpuCommands =
                (GPUMeshletDrawCommand*)_bufferManager.GetMappedPointer(_validationReadbackBuffers[frameIndex]);

            var gpuKeys = new ValidationCommandKey[gpuSampleCount];
            for (int i = 0; i < gpuSampleCount; i++)
                gpuKeys[i] = CreateValidationKey(gpuCommands[i], expectedFrame.ExpectedCommands, ValidationPathBucket.Unknown);

            return CompareValidationSamples(expectedFrame, counters, gpuKeys, gpuCount, compareFullSample);
        }

        private static SceneSubmissionValidationSnapshot CompareValidationSamples(
            ValidationExpectedFrame expectedFrame,
            SceneSubmissionCounterSnapshot counters,
            ValidationCommandKey[] gpuKeys,
            int gpuCount,
            bool compareFullSample)
        {
            int expectedSampleCount = compareFullSample ? expectedFrame.SampleCount : 0;
            var expectedKeys = new SceneSubmissionValidationCommandKey[expectedSampleCount];
            for (int i = 0; i < expectedKeys.Length; i++)
                expectedKeys[i] = ToDiagnosticsKey(expectedFrame.ExpectedCommands[i]);

            var actualGpuKeys = new SceneSubmissionValidationCommandKey[compareFullSample ? gpuKeys.Length : 0];
            for (int i = 0; i < actualGpuKeys.Length; i++)
                actualGpuKeys[i] = ToDiagnosticsKey(gpuKeys[i]);

            return SceneSubmissionDiagnosticsPolicy.CompareValidationSamples(
                expectedKeys,
                actualGpuKeys,
                expectedFrame.CpuCount,
                gpuCount,
                counters.OverflowCount,
                MaxValidationSampleCommands,
                expectedFrame.HiZEnabled,
                expectedFrame.GpuLodSelectionEnabled,
                compareFullSample);
        }

        private static SceneSubmissionValidationCommandKey ToDiagnosticsKey(ValidationCommandKey key)
        {
            return new SceneSubmissionValidationCommandKey(
                key.MeshletIndex,
                key.InstanceId,
                key.MeshIndex,
                key.MaterialIndex);
        }

        private static ValidationCommandKey CreateValidationKey(
            GPUMeshletDrawCommand command,
            IReadOnlyList<GPUObjectData> objectData,
            ValidationPathBucket bucket)
        {
            uint meshIndex = command.InstanceId < objectData.Count
                ? checked((uint)Math.Max(0, objectData[(int)command.InstanceId].MeshIndex))
                : uint.MaxValue;
            return new ValidationCommandKey(
                NormalizeValidationMeshletAddress(command.MeshletIndex),
                command.InstanceId,
                meshIndex,
                command.MaterialIndex,
                bucket);
        }

        private static ValidationCommandKey CreateValidationKey(
            GPUMeshletDrawCommand command,
            IReadOnlyList<ValidationCommandKey> expectedCommands,
            ValidationPathBucket fallbackBucket)
        {
            uint normalizedMeshletIndex =
                NormalizeValidationMeshletAddress(command.MeshletIndex);
            for (int i = 0; i < expectedCommands.Count; i++)
            {
                ValidationCommandKey expected = expectedCommands[i];
                if (expected.MeshletIndex == normalizedMeshletIndex &&
                    expected.InstanceId == command.InstanceId &&
                    expected.MaterialIndex == command.MaterialIndex)
                {
                    return expected;
                }
            }

            return new ValidationCommandKey(
                normalizedMeshletIndex,
                command.InstanceId,
                uint.MaxValue,
                command.MaterialIndex,
                fallbackBucket);
        }

        private static uint NormalizeValidationMeshletAddress(uint address) =>
            MeshletVirtualAddress.IsResolved(address)
                ? MeshletVirtualAddress.Encode(
                    MeshletVirtualAddress.DecodeResolved(address))
                : address;

        private static int ClampUIntToInt(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private void ExecuteBarrier(CommandBuffer cmd, BufferMemoryBarrier2 barrier)
        {
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
        }

        private void ExecuteBarriers(CommandBuffer cmd, ReadOnlySpan<BufferMemoryBarrier2> barriers)
        {
            fixed (BufferMemoryBarrier2* pBarriers = barriers)
            {
                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = (uint)barriers.Length,
                    PBufferMemoryBarriers = pBarriers
                };
                _context.Api.CmdPipelineBarrier2(cmd, &dependencyInfo);
            }
        }

        public override void Cleanup()
        {
            for (int i = 0; i < _compactedDrawBuffers.Length; i++)
            {
                DestroyIfValid(_compactedDrawBuffers[i].Handle);
                _compactedDrawBuffers[i] = default;
                DestroyIfValid(_simpleCompactedDrawBuffers[i].Handle);
                _simpleCompactedDrawBuffers[i] = default;
                DestroyIfValid(_simpleNormalCompactedDrawBuffers[i].Handle);
                _simpleNormalCompactedDrawBuffers[i] = default;
                DestroyIfValid(_fullCompactedDrawBuffers[i].Handle);
                _fullCompactedDrawBuffers[i] = default;
                DestroyIfValid(_solidDepthCompactedDrawBuffers[i].Handle);
                _solidDepthCompactedDrawBuffers[i] = default;
                DestroyIfValid(_maskedDepthCompactedDrawBuffers[i].Handle);
                _maskedDepthCompactedDrawBuffers[i] = default;
                for (int cascade = 0; cascade < DirectionalShadowCascadeCapacity; cascade++)
                {
                    DestroyIfValid(_directionalStaticShadowCompactedDrawBuffers[i, cascade].Handle);
                    _directionalStaticShadowCompactedDrawBuffers[i, cascade] = default;
                    DestroyIfValid(_directionalDynamicShadowCompactedDrawBuffers[i, cascade].Handle);
                    _directionalDynamicShadowCompactedDrawBuffers[i, cascade] = default;
                }
                DestroyIfValid(_counterBuffers[i].Handle);
                _counterBuffers[i] = default;
                DestroyIfValid(_indirectDispatchBuffers[i].Handle);
                _indirectDispatchBuffers[i] = default;
                DestroyIfValid(_lodHistoryBuffers[i].Handle);
                _lodHistoryBuffers[i] = default;
                DestroyIfValid(_counterReadbackBuffers[i]);
                _counterReadbackBuffers[i] = BufferHandle.Invalid;
                DestroyIfValid(_validationReadbackBuffers[i]);
                _validationReadbackBuffers[i] = BufferHandle.Invalid;
                _counterReadbackRecorded[i] = false;
                _validationReadbackRecorded[i] = false;
                _validationExpectedFrames[i] = ValidationExpectedFrame.Invalid;
                _completedCounters[i] = SceneSubmissionCounterSnapshot.Invalid;
                _completedValidation[i] = SceneSubmissionValidationSnapshot.Invalid;
            }
            _lodHistorySceneRevision = 0;
            _lodHistoryLogicalCapacity = 0;
            _lodHistoryMode = default;
            _lodHistoryInitialized = false;
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private static void ValidateFrameIndex(int frameIndex)
        {
            if ((uint)frameIndex >= RenderingConstants.FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, "Frame index is outside the frames-in-flight range.");
        }

        private readonly struct RuntimeBuffer
        {
            public RuntimeBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
            {
                Handle = handle;
                ElementCapacity = elementCapacity;
                ByteSize = byteSize;
            }

            public BufferHandle Handle { get; }
            public uint ElementCapacity { get; }
            public ulong ByteSize { get; }
        }

        private enum ValidationPathBucket : uint
        {
            Unknown = 0,
            SimpleOpaque = 1,
            SimpleNormalOpaque = 2,
            FullOpaque = 3
        }

        private readonly record struct ValidationExpectedFrame(
            bool Valid,
            int CpuCount,
            int SampleCount,
            bool HiZEnabled,
            bool GpuLodSelectionEnabled,
            ValidationCommandKey[] ExpectedCommands)
        {
            public static ValidationExpectedFrame Invalid { get; } = new(false, 0, 0, false, false, Array.Empty<ValidationCommandKey>());
        }

        private readonly record struct ValidationCommandKey(
            uint MeshletIndex,
            uint InstanceId,
            uint MeshIndex,
            uint MaterialIndex,
            ValidationPathBucket Bucket) : IComparable<ValidationCommandKey>
        {
            public bool CommandEquals(ValidationCommandKey other)
            {
                return MeshletIndex == other.MeshletIndex &&
                       InstanceId == other.InstanceId &&
                       MeshIndex == other.MeshIndex &&
                       MaterialIndex == other.MaterialIndex;
            }

            public int CompareTo(ValidationCommandKey other)
            {
                int meshlet = MeshletIndex.CompareTo(other.MeshletIndex);
                if (meshlet != 0)
                    return meshlet;
                int instance = InstanceId.CompareTo(other.InstanceId);
                if (instance != 0)
                    return instance;
                int mesh = MeshIndex.CompareTo(other.MeshIndex);
                if (mesh != 0)
                    return mesh;
                return MaterialIndex.CompareTo(other.MaterialIndex);
            }

            public override string ToString()
            {
                return $"obj={InstanceId}, mesh={MeshIndex}, meshlet={MeshletIndex}, mat={MaterialIndex}, bucket={Bucket}";
            }
        }
    }

    public readonly record struct SceneSubmissionCounterSnapshot(
        uint CandidateCount,
        uint EmittedCount,
        uint FrustumRejectedCount,
        uint OverflowCount,
        uint HiZTestedCount,
        uint HiZRejectedCount,
        uint Lod0EmittedCount,
        uint Lod1EmittedCount,
        uint Lod2EmittedCount,
        uint MissingLodFallbackCount,
        uint SolidDepthCandidateCount,
        uint SolidDepthEmittedCount,
        uint SolidDepthOverflowCount,
        uint MaskedDepthCandidateCount,
        uint MaskedDepthEmittedCount,
        uint MaskedDepthOverflowCount,
        uint[] DirectionalStaticShadowCandidateCounts,
        uint[] DirectionalStaticShadowEmittedCounts,
        uint[] DirectionalStaticShadowRejectedCounts,
        uint[] DirectionalStaticShadowOverflowCounts,
        uint[] DirectionalDynamicShadowCandidateCounts,
        uint[] DirectionalDynamicShadowEmittedCounts,
        uint[] DirectionalDynamicShadowRejectedCounts,
        uint[] DirectionalDynamicShadowOverflowCounts)
    {
        /// <summary>
        /// Number of directional-shadow LOD requests retained at LOD0 because the
        /// command stream has no topology-safe lower-LOD remapping.
        /// </summary>
        public uint DirectionalShadowLodFallbackCount { get; init; }
        /// <summary>LOD0 candidates deliberately removed by the selected lower-LOD range.</summary>
        public uint OpaqueLodDecimatedCount { get; init; }
        public uint NormalConeCandidateCount { get; init; }
        public uint NormalConeTestedCount { get; init; }
        public uint NormalConeRejectedCount { get; init; }
        public uint NormalConeInvalidCount { get; init; }
        public uint HierarchicalInstanceCount { get; init; }
        public uint HierarchySelectedNodeCount { get; init; }
        public uint HierarchyTraversalFallbackCount { get; init; }

        public bool IsValid =>
            CandidateCount != 0 ||
            EmittedCount != 0 ||
            FrustumRejectedCount != 0 ||
            OverflowCount != 0 ||
            HiZTestedCount != 0 ||
            HiZRejectedCount != 0 ||
            DirectionalShadowLodFallbackCount != 0 ||
            OpaqueLodDecimatedCount != 0 ||
            NormalConeCandidateCount != 0 ||
            NormalConeTestedCount != 0 ||
            NormalConeRejectedCount != 0 ||
            NormalConeInvalidCount != 0 ||
            HierarchicalInstanceCount != 0 ||
            HierarchySelectedNodeCount != 0 ||
            HierarchyTraversalFallbackCount != 0 ||
            SolidDepthCandidateCount != 0 ||
            SolidDepthEmittedCount != 0 ||
            SolidDepthOverflowCount != 0 ||
            MaskedDepthCandidateCount != 0 ||
            MaskedDepthEmittedCount != 0 ||
            MaskedDepthOverflowCount != 0 ||
            HasAny(DirectionalStaticShadowCandidateCounts) ||
            HasAny(DirectionalStaticShadowEmittedCounts) ||
            HasAny(DirectionalStaticShadowRejectedCounts) ||
            HasAny(DirectionalStaticShadowOverflowCounts) ||
            HasAny(DirectionalDynamicShadowCandidateCounts) ||
            HasAny(DirectionalDynamicShadowEmittedCounts) ||
            HasAny(DirectionalDynamicShadowRejectedCounts) ||
            HasAny(DirectionalDynamicShadowOverflowCounts);

        public static SceneSubmissionCounterSnapshot Invalid { get; } = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());

        public static SceneSubmissionCounterSnapshot FromCounters(GPUSceneSubmissionCounters counters)
        {
            return new SceneSubmissionCounterSnapshot(
                counters.CandidateCount,
                counters.EmittedCount,
                counters.FrustumRejectedCount,
                counters.OverflowCount,
                counters.HiZTestedCount,
                counters.HiZRejectedCount,
                counters.Lod0EmittedCount,
                counters.Lod1EmittedCount,
                counters.Lod2EmittedCount,
                counters.MissingLodFallbackCount,
                counters.SolidDepthCandidateCount,
                counters.SolidDepthEmittedCount,
                counters.SolidDepthOverflowCount,
                counters.MaskedDepthCandidateCount,
                counters.MaskedDepthEmittedCount,
                counters.MaskedDepthOverflowCount,
                [
                    counters.DirectionalStaticShadowCascade0CandidateCount,
                    counters.DirectionalStaticShadowCascade1CandidateCount,
                    counters.DirectionalStaticShadowCascade2CandidateCount,
                    counters.DirectionalStaticShadowCascade3CandidateCount
                ],
                [
                    counters.DirectionalStaticShadowCascade0EmittedCount,
                    counters.DirectionalStaticShadowCascade1EmittedCount,
                    counters.DirectionalStaticShadowCascade2EmittedCount,
                    counters.DirectionalStaticShadowCascade3EmittedCount
                ],
                [
                    counters.DirectionalStaticShadowCascade0RejectedCount,
                    counters.DirectionalStaticShadowCascade1RejectedCount,
                    counters.DirectionalStaticShadowCascade2RejectedCount,
                    counters.DirectionalStaticShadowCascade3RejectedCount
                ],
                [
                    counters.DirectionalStaticShadowCascade0OverflowCount,
                    counters.DirectionalStaticShadowCascade1OverflowCount,
                    counters.DirectionalStaticShadowCascade2OverflowCount,
                    counters.DirectionalStaticShadowCascade3OverflowCount
                ],
                [
                    counters.DirectionalDynamicShadowCascade0CandidateCount,
                    counters.DirectionalDynamicShadowCascade1CandidateCount,
                    counters.DirectionalDynamicShadowCascade2CandidateCount,
                    counters.DirectionalDynamicShadowCascade3CandidateCount
                ],
                [
                    counters.DirectionalDynamicShadowCascade0EmittedCount,
                    counters.DirectionalDynamicShadowCascade1EmittedCount,
                    counters.DirectionalDynamicShadowCascade2EmittedCount,
                    counters.DirectionalDynamicShadowCascade3EmittedCount
                ],
                [
                    counters.DirectionalDynamicShadowCascade0RejectedCount,
                    counters.DirectionalDynamicShadowCascade1RejectedCount,
                    counters.DirectionalDynamicShadowCascade2RejectedCount,
                    counters.DirectionalDynamicShadowCascade3RejectedCount
                ],
                [
                    counters.DirectionalDynamicShadowCascade0OverflowCount,
                    counters.DirectionalDynamicShadowCascade1OverflowCount,
                    counters.DirectionalDynamicShadowCascade2OverflowCount,
                    counters.DirectionalDynamicShadowCascade3OverflowCount
                ])
            {
                DirectionalShadowLodFallbackCount = counters.DirectionalShadowLodFallbackCount,
                OpaqueLodDecimatedCount = counters.OpaqueLodDecimatedCount,
                NormalConeCandidateCount = counters.NormalConeCandidateCount,
                NormalConeTestedCount = counters.NormalConeTestedCount,
                NormalConeRejectedCount = counters.NormalConeRejectedCount,
                NormalConeInvalidCount = counters.NormalConeInvalidCount,
                HierarchicalInstanceCount =
                    counters.HierarchicalInstanceCount,
                HierarchySelectedNodeCount =
                    counters.HierarchySelectedNodeCount,
                HierarchyTraversalFallbackCount =
                    counters.HierarchyTraversalFallbackCount
            };
        }

        private static bool HasAny(uint[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != 0)
                    return true;
            }

            return false;
        }
    }

    public readonly record struct SceneSubmissionValidationSnapshot(
        int Valid,
        string Status,
        int CpuOpaqueCount,
        int GpuOpaqueCount,
        int ComparedSampleCount,
        int MismatchCount,
        int SampleLimit,
        string FirstMismatch)
    {
        public static SceneSubmissionValidationSnapshot Invalid { get; } = new(0, string.Empty, 0, 0, 0, 0, 0, string.Empty);
    }

    internal readonly record struct SidedStreamCapacityPlan(
        int OneSidedCapacity,
        int DoubleSidedBase,
        int DoubleSidedCapacity,
        uint RequiredBackingElements,
        bool Asymmetric)
    {
        public int TotalLogicalCapacity => checked(
            DoubleSidedCapacity > 0
                ? DoubleSidedBase + DoubleSidedCapacity
                : OneSidedCapacity);

        public bool HasNonOverlappingRanges =>
            OneSidedCapacity >= 0 &&
            DoubleSidedBase >= OneSidedCapacity &&
            DoubleSidedCapacity >= 0 &&
            (ulong)DoubleSidedBase + (ulong)DoubleSidedCapacity <=
            RequiredBackingElements;
    }
}

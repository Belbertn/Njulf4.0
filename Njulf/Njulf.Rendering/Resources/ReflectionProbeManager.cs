using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Owns the local-reflection cubemap array and the stable mapping from authored probe IDs to
    /// array layers. A layer is never exposed to shaders until its capture/prefilter work has
    /// been explicitly published.
    /// </summary>
    public sealed unsafe class ReflectionProbeManager : IDisposable
    {
        public const int AbsoluteMaxProbeCapacity = 256;
        private static readonly ulong MetadataBufferSize =
            ReflectionProbeData.HeaderSize + ReflectionProbeData.ProbeStride * AbsoluteMaxProbeCapacity;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;
        private readonly Format _captureDepthFormat;
        private readonly GPUReflectionProbe[] _probeScratch = new GPUReflectionProbe[AbsoluteMaxProbeCapacity];
        private readonly Dictionary<Guid, int> _layersByProbeId = new();
        private readonly SortedSet<int> _freeLayers = new();
        private readonly HashSet<Guid> _capturedProbeIds = new();
        private readonly List<(ReflectionProbe Probe, int OriginalIndex)> _selectionScratch =
            new(AbsoluteMaxProbeCapacity);
        private readonly HashSet<Guid> _selectionIds = new(AbsoluteMaxProbeCapacity);
        private readonly List<ReflectionProbe> _selectedActiveProbes = new(AbsoluteMaxProbeCapacity);
        private readonly ReflectionProbeCaptureScheduler _captureScheduler =
            new(AbsoluteMaxProbeCapacity, retryLimit: 3);
        private readonly ReflectionProbeGpuBudgetPlanner _gpuBudgetPlanner = new();
        private readonly ReflectionProbeRecapturePolicy[] _recapturePolicies =
            new ReflectionProbeRecapturePolicy[AbsoluteMaxProbeCapacity];
        private readonly ReflectionProbe?[] _probesByLayer =
            new ReflectionProbe?[AbsoluteMaxProbeCapacity];
        private readonly int[] _deferredRecaptureLayers =
            new int[AbsoluteMaxProbeCapacity];
        private readonly Guid[] _deferredRecaptureProbeIds =
            new Guid[AbsoluteMaxProbeCapacity];
        private readonly byte[] _deferredRecaptureQueued =
            new byte[AbsoluteMaxProbeCapacity];
        private readonly GpuCompletionRetirementQueue _resourceRetirement =
            new(AbsoluteMaxProbeCapacity * 6 + 128);
        private readonly GpuRetirementRecord[] _resourceRetirementScratch =
            new GpuRetirementRecord[AbsoluteMaxProbeCapacity * 6 + 128];

        private BufferHandle _metadataBuffer;
        private GpuAllocator.Allocation* _cubemapArrayAllocation;
        private Image _cubemapArrayImage;
        private ImageView _cubemapArrayView;
        private ImageView _debugCubemapView;
        private ImageView[] _captureFaceViews = [];
        private Image _scratchCaptureImage;
        private GpuAllocator.Allocation* _scratchCaptureAllocation;
        private ImageView _scratchCaptureView;
        private ImageView[] _scratchFaceViews = [];
        private ImageView[] _scratchMipViews = [];
        private ImageLayout[] _scratchFaceLayouts = [];
        private ImageLayout[] _scratchMipLayouts = [];
        private ImageLayout[] _publishedLayerLayouts = [];
        private Image _captureDepthImage;
        private GpuAllocator.Allocation* _captureDepthAllocation;
        private ImageView _captureDepthView;
        private ImageLayout _captureDepthLayout;
        private Sampler _cubemapSampler;
        private BindlessHeap? _registeredBindlessHeap;
        private int _activeProbeCount;
        private int _cubemapArrayCapacity;
        private uint _cubemapArrayResolution;
        private uint _probeMipCount;
        private ulong _estimatedBytes;
        private long _lastUploadMicroseconds;
        private ulong _capturesCompletedTotal;
        private ReflectionProbeCaptureFrameCounters _captureFrameCounters;
        private readonly ReflectionProbeSubmittedFrameRing _submittedCaptureFrames = new();
        private ReflectionProbeSubmittedFrameTelemetry _lastCompletedCaptureFrame;
        private bool _lastCompletedCaptureFrameValid;
        private int _captureFrameSlot = -1;
        private ulong _captureFrameSerial;
        private bool _captureFrameGpuTimingRecorded;
        private bool _captureFrameBegun;
        private uint _lastAuthoredRevision;
        private ulong _lastSelectionSettingsSignature;
        private bool _selectionInitialized;
        private bool _metadataDirty = true;
        private bool _captureOnLoadQueued;
        private bool _descriptorDirty = true;
        private uint _cubemapArrayResourceGeneration = 1U;
        private ulong _resourceFrameSerial;
        private bool _resourceResizeDeferred;
        private ReflectionCaptureVersion _captureVersion;
        private bool _captureVersionInitialized;
        private int _deferredRecaptureHead;
        private int _deferredRecaptureTail;
        private int _deferredRecaptureCount;
        private bool _disposed;

        public ReflectionProbeManager(
            VulkanContext context,
            BufferManager bufferManager,
            RenderSettings settings,
            Format captureDepthFormat = Format.D32Sfloat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (captureDepthFormat == Format.Undefined)
                throw new ArgumentOutOfRangeException(nameof(captureDepthFormat));
            _captureDepthFormat = captureDepthFormat;
            _metadataBuffer = _bufferManager.CreateDeviceBuffer(
                MetadataBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ReflectionProbes,
                "Reflection Probe Metadata Buffer");
            CreateSampler();
            UpdateResourceMetrics();
        }

        public int ActiveProbeCount => _activeProbeCount;
        public int ProbeCapacity => _cubemapArrayCapacity;
        public uint ProbeResolution => _settings.Reflections.ProbeResolution;
        public uint ProbeMipCount => _probeMipCount;
        public ulong EstimatedBytes => _estimatedBytes;
        public ulong MetadataBufferBytes => MetadataBufferSize;
        public ulong CubemapArrayBytes => _cubemapArrayImage.Handle == 0
            ? 0
            : ReflectionProbeData.EstimateCubemapArrayBytes(_cubemapArrayCapacity, ProbeResolution, _probeMipCount);
        public ulong ScratchCaptureBytes => _scratchCaptureImage.Handle == 0
            ? 0UL
            : ReflectionProbeData.EstimateCubemapArrayBytes(1, ProbeResolution, _probeMipCount);
        public ulong CaptureDepthBytes => _captureDepthImage.Handle == 0
            ? 0UL
            : checked((ulong)ProbeResolution * ProbeResolution * CaptureDepthBytesPerPixel);
        public ulong ReflectionResidencyBytes => checked(
            MetadataBufferSize + CubemapArrayBytes + ScratchCaptureBytes + CaptureDepthBytes);
        public long LastUploadMicroseconds => _lastUploadMicroseconds;
        public int CapturesQueued => _captureScheduler.QueueDepth +
            _captureScheduler.ActiveWorkCount +
            _captureScheduler.RetainedCompletionCount;
        public int CapturesStarted =>
            _captureFrameCounters.CapturesStartedThisFrame;
        public int CapturesCompleted =>
            _captureFrameCounters.CapturesCompletedThisFrame;
        public ulong CapturesCompletedTotal => _capturesCompletedTotal;
        public int CaptureFaceUnitsThisFrame =>
            _captureFrameCounters.CaptureFaceUnitsThisFrame;
        public int PrefilterMipUnitsThisFrame =>
            _captureFrameCounters.PrefilterMipUnitsThisFrame;
        public int PublishCopyUnitsThisFrame =>
            _captureFrameCounters.PublishCopyUnitsThisFrame;
        public ulong CaptureFaceUnitsTotal =>
            _captureFrameCounters.CaptureFaceUnitsTotal;
        public ulong PrefilterMipUnitsTotal =>
            _captureFrameCounters.PrefilterMipUnitsTotal;
        public ulong PublishCopyUnitsTotal =>
            _captureFrameCounters.PublishCopyUnitsTotal;
        public ReflectionProbeLifecycleSnapshot CaptureLifecycle =>
            ReflectionProbeLifecycleSnapshotFactory.Create(
                _captureScheduler,
                _capturedProbeIds.Count,
                _capturesCompletedTotal,
                _captureFrameCounters);
        public ReflectionProbeLifecycleFrameSnapshot CurrentCaptureLifecycle =>
            _captureFrameBegun
                ? new ReflectionProbeLifecycleFrameSnapshot(
                    Valid: true,
                    _captureFrameSlot,
                    _captureFrameSerial,
                    _captureFrameGpuTimingRecorded,
                    CaptureLifecycle)
                : default;
        public ReflectionProbeLifecycleFrameSnapshot CompletedCaptureLifecycle =>
            _lastCompletedCaptureFrameValid
                ? _lastCompletedCaptureFrame.ToLifecycleFrameSnapshot()
                : default;
        internal ReflectionProbeSubmittedFrameTelemetry LastCompletedCaptureFrame =>
            _lastCompletedCaptureFrame;
        public ReflectionProbeGpuBudgetSnapshot CaptureGpuBudget => _gpuBudgetPlanner.GetSnapshot();
        public int ReflectionCaptureBudgetExceeded =>
            CaptureGpuBudget.BudgetExhausted ? 1 : 0;
        public int PublishedProbeCount => _capturedProbeIds.Count;
        public Image CaptureImage => _cubemapArrayImage;
        public Image ScratchCaptureImage => _scratchCaptureImage;
        public ImageView ScratchCaptureView => _scratchCaptureView;
        public ImageView CaptureDepthView => _captureDepthView;
        public Sampler ScratchSampler => _cubemapSampler;

        private ulong CaptureDepthBytesPerPixel => _captureDepthFormat switch
        {
            Format.D32SfloatS8Uint => 8UL,
            Format.D24UnormS8Uint => 4UL,
            Format.D16UnormS8Uint => 4UL,
            _ => 4UL
        };

        private ImageAspectFlags CaptureDepthAspectMask => _captureDepthFormat switch
        {
            Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16UnormS8Uint =>
                ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit,
            _ => ImageAspectFlags.DepthBit
        };

        public ImageView GetScratchFaceView(int face)
        {
            if ((uint)face >= (uint)_scratchFaceViews.Length)
                throw new ArgumentOutOfRangeException(nameof(face));
            return _scratchFaceViews[face];
        }

        public ImageView GetScratchMipView(int mip)
        {
            if ((uint)mip >= (uint)_scratchMipViews.Length)
                throw new ArgumentOutOfRangeException(nameof(mip));
            return _scratchMipViews[mip];
        }

        /// <summary>
        /// Transitions one scratch face and the reusable depth target into attachment layouts.
        /// The pass owns the actual dynamic-rendering commands; this manager owns the exact image
        /// subresource state so a failed face cannot accidentally be treated as a complete mip.
        /// </summary>
        public void PrepareCaptureFace(CommandBuffer commandBuffer, in ReflectionProbeWork work)
        {
            ValidateWorkResource(work, ReflectionProbeWorkKind.CaptureFace);
            ImageSubresourceRange faceRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = checked((uint)work.Face),
                LayerCount = 1
            };
            TransitionImage(
                commandBuffer,
                _scratchCaptureImage,
                _scratchFaceLayouts[work.Face],
                ImageLayout.ColorAttachmentOptimal,
                faceRange,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit);
            _scratchFaceLayouts[work.Face] = ImageLayout.ColorAttachmentOptimal;

            ImageSubresourceRange depthRange = new()
            {
                AspectMask = CaptureDepthAspectMask,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            };
            TransitionImage(
                commandBuffer,
                _captureDepthImage,
                _captureDepthLayout,
                ImageLayout.DepthStencilAttachmentOptimal,
                depthRange,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.TransferBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
                AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit);
            _captureDepthLayout = ImageLayout.DepthStencilAttachmentOptimal;
        }

        public void CompleteCaptureFaceRecording(CommandBuffer commandBuffer, in ReflectionProbeWork work)
        {
            ValidateWorkResource(work, ReflectionProbeWorkKind.CaptureFace);
            ImageSubresourceRange faceRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = checked((uint)work.Face),
                LayerCount = 1
            };
            TransitionImage(
                commandBuffer,
                _scratchCaptureImage,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                faceRange,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit);
            _scratchFaceLayouts[work.Face] = ImageLayout.ShaderReadOnlyOptimal;
            if (work.Face == 5)
                _scratchMipLayouts[0] = ImageLayout.ShaderReadOnlyOptimal;
        }

        public void PreparePrefilterMip(CommandBuffer commandBuffer, in ReflectionProbeWork work)
        {
            ValidateWorkResource(work, ReflectionProbeWorkKind.PrefilterMip);
            for (int face = 0; face < _scratchFaceLayouts.Length; face++)
            {
                if (_scratchFaceLayouts[face] != ImageLayout.ShaderReadOnlyOptimal)
                {
                    throw new InvalidOperationException(
                        "Reflection prefilter cannot start until all six capture faces are shader-readable.");
                }
            }

            ImageSubresourceRange mipRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = checked((uint)work.Mip),
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 6
            };
            TransitionImage(
                commandBuffer,
                _scratchCaptureImage,
                _scratchMipLayouts[work.Mip],
                ImageLayout.General,
                mipRange,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit);
            _scratchMipLayouts[work.Mip] = ImageLayout.General;
        }

        public void CompletePrefilterMipRecording(CommandBuffer commandBuffer, in ReflectionProbeWork work)
        {
            ValidateWorkResource(work, ReflectionProbeWorkKind.PrefilterMip);
            ImageSubresourceRange mipRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = checked((uint)work.Mip),
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 6
            };
            TransitionImage(
                commandBuffer,
                _scratchCaptureImage,
                ImageLayout.General,
                ImageLayout.ShaderReadOnlyOptimal,
                mipRange,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit);
            _scratchMipLayouts[work.Mip] = ImageLayout.ShaderReadOnlyOptimal;
        }

        /// <summary>
        /// Records the only write into a published local-probe layer. Logical publication still
        /// waits for the completion token submitted by the caller. The copy covers every face and
        /// every mip, so a completed ticket can never expose a mixed chain.
        /// </summary>
        public void RecordPublishCopy(CommandBuffer commandBuffer, in ReflectionProbeWork work)
        {
            ValidateWorkResource(work, ReflectionProbeWorkKind.PublishCopy);
            if (work.Ticket.Layer < 0 || work.Ticket.Layer >= _publishedLayerLayouts.Length)
                throw new InvalidOperationException("The reflection layer is outside the current resource generation.");
            for (int mip = 0; mip < _scratchMipLayouts.Length; mip++)
            {
                if (_scratchMipLayouts[mip] != ImageLayout.ShaderReadOnlyOptimal)
                {
                    throw new InvalidOperationException(
                        "Reflection publish requires a complete shader-readable scratch mip chain.");
                }
            }

            for (int mip = 0; mip < _scratchMipLayouts.Length; mip++)
            {
                ImageSubresourceRange scratchRange = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = checked((uint)mip),
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 6
                };
                TransitionImage(
                    commandBuffer,
                    _scratchCaptureImage,
                    ImageLayout.ShaderReadOnlyOptimal,
                    ImageLayout.TransferSrcOptimal,
                    scratchRange,
                    PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderSampledReadBit,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit);
                _scratchMipLayouts[mip] = ImageLayout.TransferSrcOptimal;
            }

            ImageSubresourceRange destinationRange = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = _probeMipCount,
                BaseArrayLayer = checked((uint)(work.Ticket.Layer * 6)),
                LayerCount = 6
            };
            TransitionImage(
                commandBuffer,
                _cubemapArrayImage,
                _publishedLayerLayouts[work.Ticket.Layer],
                ImageLayout.TransferDstOptimal,
                destinationRange,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit);
            _publishedLayerLayouts[work.Ticket.Layer] = ImageLayout.TransferDstOptimal;

            ImageCopy* copies = stackalloc ImageCopy[checked((int)_probeMipCount)];
            for (uint mip = 0; mip < _probeMipCount; mip++)
            {
                uint extent = Math.Max(1U, ProbeResolution >> checked((int)mip));
                copies[mip] = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip,
                        BaseArrayLayer = 0,
                        LayerCount = 6
                    },
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mip,
                        BaseArrayLayer = checked((uint)(work.Ticket.Layer * 6)),
                        LayerCount = 6
                    },
                    Extent = new Extent3D { Width = extent, Height = extent, Depth = 1 }
                };
            }
            _context.Api.CmdCopyImage(
                commandBuffer,
                _scratchCaptureImage,
                ImageLayout.TransferSrcOptimal,
                _cubemapArrayImage,
                ImageLayout.TransferDstOptimal,
                _probeMipCount,
                copies);

            TransitionImage(
                commandBuffer,
                _cubemapArrayImage,
                ImageLayout.TransferDstOptimal,
                ImageLayout.ShaderReadOnlyOptimal,
                destinationRange,
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit);
            _publishedLayerLayouts[work.Ticket.Layer] = ImageLayout.ShaderReadOnlyOptimal;
            for (int mip = 0; mip < _scratchMipLayouts.Length; mip++)
            {
                ImageSubresourceRange scratchRange = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = checked((uint)mip),
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 6
                };
                TransitionImage(
                    commandBuffer,
                    _scratchCaptureImage,
                    ImageLayout.TransferSrcOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    scratchRange,
                    PipelineStageFlags2.TransferBit,
                    AccessFlags2.TransferReadBit,
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderSampledReadBit);
                _scratchMipLayouts[mip] = ImageLayout.ShaderReadOnlyOptimal;
            }
        }

        public uint CubemapArrayResourceGeneration => _cubemapArrayResourceGeneration;
        public ReflectionProbeCaptureScheduler CaptureScheduler => _captureScheduler;
        public bool ResourceResizeDeferred => _resourceResizeDeferred;
        public GpuCompletionRetirementSnapshot ResourceRetirementSnapshot =>
            _resourceRetirement.GetSnapshot(_resourceFrameSerial);

        /// <summary>
        /// Answers the conditional-pass question without acquiring or mutating a scheduler ticket.
        /// In particular, a queued face must not cause an empty prefilter/copy timestamp when the
        /// earlier transaction stage still owns the work unit.
        /// </summary>
        public bool HasCaptureWork(ReflectionProbeWorkKind kind)
        {
            if (_cubemapArrayImage.Handle == 0 || _scratchCaptureImage.Handle == 0)
                return false;
            if (kind == ReflectionProbeWorkKind.CaptureFace &&
                _settings.Reflections.CaptureIncludesDdgi &&
                _captureVersion.CompletedDdgiGeneration == 0U)
            {
                return false;
            }
            if (!_gpuBudgetPlanner.CanReserve(kind))
                return false;

            return _captureScheduler.HasWork(
                (int)Math.Max(_probeMipCount, 1U),
                kind,
                _resourceFrameSerial);
        }

        /// <summary>
        /// Establishes the only per-frame reset boundary for capture planning
        /// and lifecycle pulses. It must run after this frame slot's timestamp
        /// queries are read and before completion polling or new work.
        /// </summary>
        public void BeginCaptureFrame(
            int frameSlot,
            ulong frameSerial,
            bool gpuTimingRecorded)
        {
            RenderingConstants.ValidateFrameIndex(frameSlot);
            if (_captureFrameBegun)
            {
                throw new InvalidOperationException(
                    "A reflection capture frame was begun before the previous frame submission was committed.");
            }

            _captureFrameSlot = frameSlot;
            _captureFrameSerial = frameSerial;
            _captureFrameGpuTimingRecorded = gpuTimingRecorded;
            _captureFrameBegun = true;
            _gpuBudgetPlanner.BeginFrame(
                _settings.Reflections.ReflectionCaptureGpuBudgetMicroseconds);
            _captureFrameCounters.BeginCaptureFrame();
        }

        /// <summary>
        /// Polls renderer-owned completion state. The renderer calls this after its normal
        /// non-blocking frame-fence observation; no feature-owned wait is performed here.
        /// </summary>
        public int BeginFrameResourceRetirement(ulong currentFrameSerial, ulong completedFrameSerial)
        {
            _resourceFrameSerial = currentFrameSerial;
            int retired = _resourceRetirement.Poll(
                new GpuCompletionProgress(completedFrameSerial, 0UL, 0UL),
                _resourceRetirementScratch,
                currentFrameSerial);
            for (int index = 0; index < retired; index++)
                DestroyRetiredResource(_resourceRetirementScratch[index]);
            return retired;
        }

        public void UpdateCaptureVersions(in LightingVersionSnapshot versions)
        {
            ReflectionCaptureVersion nextVersion = BuildCaptureVersion(
                versions,
                _settings.Reflections.CaptureIncludesDdgi);
            if (_captureVersionInitialized && nextVersion != _captureVersion)
            {
                ReflectionCaptureReason reasons = ResolveVersionChangeReasons(_captureVersion, nextVersion);
                _captureVersion = nextVersion;
                if (reasons != ReflectionCaptureReason.None)
                    RequestRecaptureAll(reasons);
            }
            else
            {
                _captureVersion = nextVersion;
            }
            _captureVersionInitialized = true;
        }

        internal static ReflectionCaptureVersion BuildCaptureVersion(
            in LightingVersionSnapshot versions,
            bool captureIncludesDdgi) =>
            new(
                versions.SceneRadianceRevision > uint.MaxValue
                    ? uint.MaxValue
                    : (uint)versions.SceneRadianceRevision,
                versions.VisualEnvironmentGeneration,
                captureIncludesDdgi ? versions.AdmittedGiEnvironmentGeneration : 0U,
                captureIncludesDdgi ? versions.StaticGiConvergedGeneration : 0U,
                0U,
                0U,
                versions.PublishedSpecularEnvironmentGeneration);

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            if (!ReferenceEquals(_registeredBindlessHeap, bindlessHeap))
            {
                _registeredBindlessHeap = bindlessHeap;
                bindlessHeap.RegisterStorageBuffer(
                    BindlessIndex.ReflectionProbeBuffer,
                    _bufferManager.GetBuffer(_metadataBuffer),
                    0,
                    MetadataBufferSize);
                _descriptorDirty = true;
            }

            // When no local image exists the environment manager's cube fallback remains bound.
            if (_cubemapArrayView.Handle != 0 && _descriptorDirty)
            {
                bindlessHeap.RegisterTexture(
                    BindlessIndex.ReflectionProbeCubemapArrayTexture,
                    _cubemapArrayView,
                    _cubemapSampler);
                if (_debugCubemapView.Handle != 0)
                    bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeDebugTexture, _debugCubemapView, _cubemapSampler);
                _descriptorDirty = false;
            }
        }

        public void Upload(
            IReadOnlyList<ReflectionProbe> authoredProbes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer) =>
            Upload(authoredProbes, stagingRing, commandBuffer,
                _lastAuthoredRevision == uint.MaxValue ? 1u : _lastAuthoredRevision + 1u);

        public void Upload(
            IReadOnlyList<ReflectionProbe> authoredProbes,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            uint authoredRevision)
        {
            if (authoredProbes == null)
                throw new ArgumentNullException(nameof(authoredProbes));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for reflection probe upload.", nameof(commandBuffer));

            long uploadStart = Stopwatch.GetTimestamp();
            _captureScheduler.RetryLimit = _settings.Reflections.ReflectionCaptureRetryLimit;
            ulong selectionSettingsSignature = CreateSelectionSettingsSignature();
            bool selectionChanged = !_selectionInitialized || authoredRevision != _lastAuthoredRevision ||
                selectionSettingsSignature != _lastSelectionSettingsSignature;
            if (selectionChanged)
            {
                SelectActiveProbes(authoredProbes);
                SynchronizeProbeLayers(_selectedActiveProbes);
                _lastAuthoredRevision = authoredRevision;
                _lastSelectionSettingsSignature = selectionSettingsSignature;
                _selectionInitialized = true;
                _metadataDirty = true;
            }
            _resourceResizeDeferred = !EnsureCubemapArrayStorage(RequiredLayerCapacity());
            RegisterIfNeeded();
            ProcessDeferredRecaptures();

            if (_metadataDirty)
            {
                _activeProbeCount = ReflectionProbeData.BuildProbes(
                    _selectedActiveProbes,
                    _settings.Reflections,
                    _probeScratch.AsSpan(0, AbsoluteMaxProbeCapacity),
                    probe => _layersByProbeId[probe.Id],
                    probe => _cubemapArrayImage.Handle != 0 &&
                            _layersByProbeId.TryGetValue(probe.Id, out int layer) &&
                            _captureScheduler.HasPublishedCapture(layer, probe.Id));
            }
            UpdateResourceMetrics();

            if (_settings.Reflections.CaptureOnLoad && !_captureOnLoadQueued && _activeProbeCount > 0)
            {
                RequestRecaptureAll("load");
                _captureOnLoadQueued = true;
            }

            if (!_metadataDirty)
            {
                _lastUploadMicroseconds = 0;
                return;
            }

            GPUReflectionProbeHeader header = ReflectionProbeData.BuildHeader(
                _activeProbeCount,
                _settings.Reflections,
                BindlessIndex.ReflectionProbeCubemapArrayTexture,
                BindlessIndex.ReflectionProbeDebugTexture,
                _probeMipCount);
            GpuBufferUploader.UploadHeaderAndSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _metadataBuffer,
                header,
                _probeScratch.AsSpan(0, _activeProbeCount),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
            _metadataDirty = false;
        }

        /// <summary>
        /// Consumes the submitted workload stored for the exact completed timestamp frame slot.
        /// Zero or unavailable timestamps are ignored by the planner, but the slot is still
        /// consumed so it cannot be paired with a later frame's timings.
        /// </summary>
        public void UpdateCaptureGpuTimingHistory(
            int completedFrameSlot,
            long captureMicroseconds,
            long prefilterMicroseconds,
            long publishMicroseconds)
        {
            RenderingConstants.ValidateFrameIndex(completedFrameSlot);
            if (!_captureFrameBegun || completedFrameSlot != _captureFrameSlot)
            {
                throw new InvalidOperationException(
                    "Reflection timing history must be consumed at the active capture frame boundary.");
            }

            if (!_submittedCaptureFrames.TryConsume(
                    completedFrameSlot,
                    out ReflectionProbeSubmittedFrameTelemetry submittedFrame))
            {
                _lastCompletedCaptureFrame = default;
                _lastCompletedCaptureFrameValid = false;
                return;
            }

            _lastCompletedCaptureFrame = submittedFrame;
            _lastCompletedCaptureFrameValid = true;
            _gpuBudgetPlanner.RecordTiming(
                submittedFrame,
                captureMicroseconds,
                prefilterMicroseconds,
                publishMicroseconds);
        }

        /// <summary>
        /// Publishes this frame's workload into its slot only after Vulkan has
        /// accepted the terminal graphics submission.
        /// </summary>
        public void CommitCaptureFrameSubmission(
            int frameSlot,
            ulong frameSerial,
            bool gpuTimingRecorded)
        {
            RenderingConstants.ValidateFrameIndex(frameSlot);
            if (!_captureFrameBegun ||
                frameSlot != _captureFrameSlot ||
                frameSerial != _captureFrameSerial ||
                gpuTimingRecorded != _captureFrameGpuTimingRecorded)
            {
                throw new InvalidOperationException(
                    "Reflection capture submission does not match the active frame boundary.");
            }

            ReflectionProbeLifecycleSnapshot lifecycle = CaptureLifecycle;
            _submittedCaptureFrames.MarkSubmitted(
                frameSlot,
                new ReflectionProbeSubmittedFrameTelemetry(
                    frameSlot,
                    frameSerial,
                    _captureFrameCounters.CaptureFaceUnitsThisFrame,
                    _captureFrameCounters.PrefilterMipUnitsThisFrame,
                    _captureFrameCounters.PublishCopyUnitsThisFrame,
                    gpuTimingRecorded,
                    lifecycle));
            _captureFrameBegun = false;
        }

        public void RequestRecaptureAll(string reason)
        {
            _ = reason ?? throw new ArgumentNullException(nameof(reason));
            _ = RequestRecaptureAll(
                ResolveCaptureReason(reason),
                bypassInterval: true);
        }

        /// <summary>
        /// Requests a manual recapture and returns exact scheduler admission
        /// evidence without waiting for GPU submission or completion.
        /// </summary>
        public ReflectionProbeRecaptureRequestSummary
            RequestRecaptureAllWithSummary(string reason)
        {
            _ = reason ?? throw new ArgumentNullException(nameof(reason));
            return RequestRecaptureAll(
                ResolveCaptureReason(reason),
                bypassInterval: true);
        }

        private ReflectionProbeRecaptureRequestSummary RequestRecaptureAll(
            ReflectionCaptureReason reason,
            bool bypassInterval = false)
        {
            if (reason == ReflectionCaptureReason.None)
                return ReflectionProbeRecaptureRequestSummary.Empty;

            ReflectionProbeLifecycleSnapshot before = CaptureLifecycle;
            int requested = 0;
            int admitted = 0;
            int deferred = 0;
            int coalesced = 0;
            int rejected = 0;
            foreach (Guid probeId in _layersByProbeId.Keys)
            {
                requested++;
                ReflectionProbeRecaptureDecision decision = QueueCapture(
                    probeId,
                    reason,
                    bypassInterval);
                if (decision.RequestCapture)
                    admitted++;
                else if (decision.Deferred)
                    deferred++;
                else if (decision.Coalesced)
                    coalesced++;
                else
                    rejected++;
            }

            return new ReflectionProbeRecaptureRequestSummary(
                requested,
                admitted,
                deferred,
                coalesced,
                rejected,
                before,
                CaptureLifecycle);
        }

        /// <summary>
        /// Acquires work within the configured per-frame capture budget. The caller renders all
        /// six faces into <see cref="GetCaptureFaceView"/>, prefilters every mip, and finally
        /// calls <see cref="PublishCapture"/> after recording the shader-read barrier.
        /// </summary>
        public bool TryBeginCapture(out ReflectionProbeCapture capture)
        {
            int budget = _settings.Reflections.MaxProbeCapturesPerFrame;
            if (budget <= 0)
            {
                capture = default;
                return false;
            }

            if (!TryAcquireCaptureWork(ReflectionProbeWorkKind.None, out ReflectionProbeWork work))
            {
                capture = default;
                return false;
            }

            if (_cubemapArrayImage.Handle == 0 || _scratchCaptureImage.Handle == 0)
            {
                FailCaptureWork(work, retry: true);
                capture = default;
                return false;
            }

            capture = new ReflectionProbeCapture(
                work.Ticket.ProbeId,
                work.Ticket.Layer,
                ProbeResolution,
                _probeMipCount,
                work.Ticket.Serial,
                work.Ticket.ResourceGeneration,
                work.Kind,
                work.Face,
                work.Mip);
            return true;
        }

        public bool TryAcquireCaptureWork(out ReflectionProbeWork work)
            => TryAcquireCaptureWork(ReflectionProbeWorkKind.None, out work);

        public bool TryAcquireCaptureFace(out ReflectionProbeWork work) =>
            TryAcquireCaptureWork(ReflectionProbeWorkKind.CaptureFace, out work);

        public bool TryAcquirePrefilterMip(out ReflectionProbeWork work) =>
            TryAcquireCaptureWork(ReflectionProbeWorkKind.PrefilterMip, out work);

        public bool TryAcquirePublishCopy(out ReflectionProbeWork work) =>
            TryAcquireCaptureWork(ReflectionProbeWorkKind.PublishCopy, out work);

        private bool TryAcquireCaptureWork(
            ReflectionProbeWorkKind requiredKind,
            out ReflectionProbeWork work)
        {
            int maxFaces = _settings.Reflections.MaxProbeCaptureFacesPerFrame;
            int maxMips = _settings.Reflections.MaxProbePrefilterMipsPerFrame;
            int budget = _settings.Reflections.MaxProbeCapturesPerFrame;
            if (budget <= 0)
            {
                work = default;
                return false;
            }
            if (_cubemapArrayImage.Handle == 0 || _scratchCaptureImage.Handle == 0)
            {
                work = default;
                return false;
            }
            if (requiredKind == ReflectionProbeWorkKind.CaptureFace &&
                (_captureFrameCounters.CaptureFaceUnitsThisFrame >= maxFaces ||
                 maxFaces <= 0))
            {
                work = default;
                return false;
            }
            if (requiredKind == ReflectionProbeWorkKind.PrefilterMip &&
                (_captureFrameCounters.PrefilterMipUnitsThisFrame >= maxMips ||
                 maxMips <= 0))
            {
                work = default;
                return false;
            }
            if (requiredKind == ReflectionProbeWorkKind.PublishCopy &&
                _captureFrameCounters.PublishCopyUnitsThisFrame >=
                    Math.Max(1, budget))
            {
                work = default;
                return false;
            }
            if (!_gpuBudgetPlanner.CanReserve(requiredKind))
            {
                work = default;
                return false;
            }
            bool acquired = _captureScheduler.TryAcquireWork(
                (int)Math.Max(_probeMipCount, 1U),
                maxFaces <= 0 ? 6 : maxFaces,
                maxMips <= 0 ? 16 : maxMips,
                out work,
                requiredKind,
                _resourceFrameSerial);
            if (acquired)
            {
                if (requiredKind == ReflectionProbeWorkKind.CaptureFace &&
                    _settings.Reflections.CaptureIncludesDdgi &&
                    work.Ticket.Version.CompletedDdgiGeneration == 0U)
                {
                    // A DDGI-inclusive capture is a pinned-generation operation. Keep the
                    // latest ticket queued until the convergence owner exposes a nonzero stable
                    // generation; do not burn retry budget on an expected warm-up condition.
                    _captureScheduler.DeferActive(
                        work,
                        _resourceFrameSerial,
                        deferFrames: 1UL);
                    work = default;
                    return false;
                }
                if (!_gpuBudgetPlanner.TryReserve(work.Kind))
                {
                    _captureScheduler.DeferActive(
                        work,
                        _resourceFrameSerial,
                        deferFrames: 1UL);
                    work = default;
                    return false;
                }
                bool startsCapture = CountsAsCaptureStart(work);
                if (startsCapture)
                    _recapturePolicies[work.Ticket.Layer].MarkStarted(work.Ticket.Version, _resourceFrameSerial);
                _captureFrameCounters.RecordStartedUnit(
                    work.Kind,
                    startsCapture);
            }
            return acquired;
        }

        public void CompleteCaptureWork(in ReflectionProbeWork work) =>
            _captureScheduler.CompleteWork(work);

        public void FailCaptureWork(in ReflectionProbeWork work, bool retry, bool changingScene = false)
        {
            _gpuBudgetPlanner.Release(work.Kind);
            if (changingScene)
            {
                _captureScheduler.DeferActive(
                    work,
                    _resourceFrameSerial,
                    (ulong)Math.Max(1, _settings.Reflections.ReflectionCaptureRetryBackoffFrames));
                return;
            }

            _captureScheduler.FailActive(
                work.Ticket,
                retry,
                _resourceFrameSerial,
                (ulong)Math.Max(0, _settings.Reflections.ReflectionCaptureRetryBackoffFrames));
        }

        public void SubmitCaptureCopy(in ReflectionProbeWork work, ulong completionValue)
        {
            _captureScheduler.MarkCopySubmitted(work, completionValue);
        }

        public int PollCaptureCompletions(ulong completedValue)
        {
            int published = 0;
            while (_captureScheduler.TryRetireCompleted(
                       completedValue,
                       _cubemapArrayResourceGeneration,
                       _captureVersion,
                       out ReflectionProbeCaptureTicket ticket,
                       out bool didPublish))
            {
                if (didPublish)
                {
                    PublishCompletedCapture(ticket);
                    published++;
                }
                else if (!_layersByProbeId.ContainsKey(ticket.ProbeId))
                {
                    _freeLayers.Add(ticket.Layer);
                }
            }
            return published;
        }

        public ImageView GetCaptureFaceView(in ReflectionProbeCapture capture, int faceIndex)
        {
            ValidateCapture(capture);
            if ((uint)faceIndex >= 6u)
                throw new ArgumentOutOfRangeException(nameof(faceIndex));
            // Compatibility callers are still routed into private scratch. The old published
            // layer is never a capture destination, even when this migration API is used.
            return _scratchFaceViews[faceIndex];
        }

        public ImageView GetCaptureFaceView(in ReflectionProbeWork work)
        {
            if (work.Kind != ReflectionProbeWorkKind.CaptureFace)
                throw new ArgumentException("The work item is not a capture face.", nameof(work));
            ReflectionProbeCapture capture = new(
                work.Ticket.ProbeId,
                work.Ticket.Layer,
                ProbeResolution,
                _probeMipCount,
                work.Ticket.Serial,
                work.Ticket.ResourceGeneration,
                work.Kind,
                work.Face,
                work.Mip);
            return GetCaptureFaceView(capture, work.Face);
        }

        public ReflectionCaptureViewContext CreateCaptureViewContext(
            in ReflectionProbeWork work,
            bool includesDdgi)
        {
            if (work.Kind != ReflectionProbeWorkKind.CaptureFace)
                throw new ArgumentException("The work item is not a capture face.", nameof(work));
            return ReflectionCaptureViewFactory.Create(
                work.Ticket.Snapshot,
                work.Face,
                work.Ticket.Layer,
                ProbeResolution,
                work.Ticket.ResourceGeneration,
                work.Ticket.SceneRevision,
                work.Ticket.Version,
                includesDdgi);
        }

        /// <summary>
        /// Publishes a fully rendered and prefiltered capture. Calling this before all six faces
        /// and mips are transitioned to shader-read is a caller error; publication is the sole
        /// point at which the layer becomes visible to forward shading.
        /// </summary>
        public void PublishCapture(in ReflectionProbeCapture capture)
        {
            ValidateCapture(capture);
            throw new InvalidOperationException(
                "PublishCapture is a compatibility entry point; submit the copy completion token " +
                "and call PollCaptureCompletions after the GPU signals it.");
        }

        public void CancelCapture(in ReflectionProbeCapture capture)
        {
            ValidateCapture(capture);
            if (capture.TicketSerial != 0UL)
            {
                ReflectionProbeCaptureTicket ticket = new(
                    capture.TicketSerial,
                    capture.ProbeId,
                    capture.CubemapArrayIndex,
                    capture.ResourceGeneration,
                    _lastAuthoredRevision,
                    _captureVersion,
                    ReflectionCaptureReason.Manual,
                    FindProbeSnapshot(capture.ProbeId),
                    0,
                    1,
                    ReflectionProbeCaptureState.CapturingFaces);
                _captureScheduler.FailActive(ticket, retry: true);
            }
            else
            {
                QueueCapture(capture.ProbeId, ReflectionCaptureReason.Manual);
            }
        }

        private void SelectActiveProbes(IReadOnlyList<ReflectionProbe> authoredProbes)
        {
            _selectedActiveProbes.Clear();
            if (!_settings.Reflections.Enabled ||
                _settings.Reflections.Mode is ReflectionMode.Disabled or ReflectionMode.GlobalEnvironmentOnly ||
                _settings.Reflections.MaxProbes == 0)
                return;

            _selectionScratch.Clear();
            _selectionIds.Clear();
            for (int i = 0; i < authoredProbes.Count; i++)
            {
                ReflectionProbe? probe = authoredProbes[i];
                if (probe == null)
                    continue;
                if (probe.Id == Guid.Empty || !_selectionIds.Add(probe.Id))
                    throw new InvalidOperationException("Each live reflection probe must have a unique, non-empty Id.");
                _selectionScratch.Add((probe, i));
            }

            _selectionScratch.Sort((a, b) =>
            {
                int priority = b.Probe.Priority.CompareTo(a.Probe.Priority);
                if (priority != 0)
                    return priority;
                int name = string.CompareOrdinal(a.Probe.Name, b.Probe.Name);
                return name != 0 ? name : a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            int count = Math.Min(Math.Min(_selectionScratch.Count, _settings.Reflections.MaxProbes), AbsoluteMaxProbeCapacity);
            for (int i = 0; i < count; i++)
                _selectedActiveProbes.Add(_selectionScratch[i].Probe);
        }

        private ulong CreateSelectionSettingsSignature()
        {
            ulong hash = 14695981039346656037UL;
            static ulong Add(ulong current, uint value) => (current ^ value) * 1099511628211UL;
            hash = Add(hash, _settings.Reflections.Enabled ? 1u : 0u);
            hash = Add(hash, (uint)_settings.Reflections.Mode);
            hash = Add(hash, (uint)_settings.Reflections.MaxProbes);
            hash = Add(hash, (uint)_settings.Reflections.MaxProbesPerPixel);
            hash = Add(hash, _settings.Reflections.ProbeResolution);
            hash = Add(hash, BitConverter.SingleToUInt32Bits(_settings.Reflections.Intensity));
            hash = Add(hash, BitConverter.SingleToUInt32Bits(_settings.Reflections.GlobalFallbackIntensity));
            hash = Add(hash, _settings.Reflections.BoxProjectionEnabled ? 1u : 0u);
            hash = Add(hash, _settings.Reflections.ProbeBlendingEnabled ? 1u : 0u);
            return hash;
        }

        private void SynchronizeProbeLayers(IReadOnlyList<ReflectionProbe> activeProbes)
        {
            var liveIds = new HashSet<Guid>();
            for (int i = 0; i < activeProbes.Count; i++)
                liveIds.Add(activeProbes[i].Id);

            if (_layersByProbeId.Count > 0)
            {
                var removed = new List<Guid>();
                foreach ((Guid probeId, int layer) in _layersByProbeId)
                {
                    if (!liveIds.Contains(probeId))
                        removed.Add(probeId);
                }
                foreach (Guid probeId in removed)
                {
                    int layer = _layersByProbeId[probeId];
                    _deferredRecaptureQueued[layer] = 0;
                    _deferredRecaptureProbeIds[layer] = Guid.Empty;
                    _probesByLayer[layer] = null;
                    _recapturePolicies[layer].Reset();
                    _captureScheduler.Unregister(layer, probeId);
                    _layersByProbeId.Remove(probeId);
                    _capturedProbeIds.Remove(probeId);
                    if (!_captureScheduler.IsLayerPinned(layer))
                        _freeLayers.Add(layer);
                    _metadataDirty = true;
                }
            }

            for (int i = 0; i < activeProbes.Count; i++)
            {
                Guid probeId = activeProbes[i].Id;
                if (_layersByProbeId.ContainsKey(probeId))
                    continue;
                int layer = AllocateLayer();
                _layersByProbeId.Add(probeId, layer);
                _probesByLayer[layer] = activeProbes[i];
                _recapturePolicies[layer].Reset();
                _captureScheduler.Register(layer, probeId, hasPublishedCapture: false);
                QueueCapture(probeId, ReflectionCaptureReason.InitialLoad);
                _metadataDirty = true;
            }

            // Refresh the fixed reverse lookup when an authored probe object is replaced without
            // changing its stable ID. This loop runs only after selection changes.
            for (int i = 0; i < activeProbes.Count; i++)
            {
                if (_layersByProbeId.TryGetValue(activeProbes[i].Id, out int layer))
                    _probesByLayer[layer] = activeProbes[i];
            }
        }

        private int AllocateLayer()
        {
            if (_freeLayers.Count > 0)
            {
                while (_freeLayers.Count > 0)
                {
                    int layer = _freeLayers.Min;
                    _freeLayers.Remove(layer);
                    if (!_captureScheduler.IsLayerPinned(layer))
                        return layer;
                }
            }

            // A deleted, copy-committed probe keeps its layer pinned until the renderer observes
            // completion. Do not infer a free layer from the dictionary count: a pinned hole is
            // not reusable and can otherwise make a delete/re-add race alias a live copy.
            for (int layer = 0; layer < AbsoluteMaxProbeCapacity; layer++)
            {
                if (!_layersByProbeId.ContainsValue(layer) && !_captureScheduler.IsLayerPinned(layer))
                    return layer;
            }

            throw new InvalidOperationException(
                "Reflection probe layer capacity is exhausted by live or completion-pinned layers.");
        }

        private int RequiredLayerCapacity()
        {
            int capacity = 0;
            foreach (int layer in _layersByProbeId.Values)
                capacity = Math.Max(capacity, checked(layer + 1));
            return capacity;
        }

        private ReflectionProbeRecaptureDecision QueueCapture(
            Guid probeId,
            ReflectionCaptureReason reason,
            bool bypassInterval = false)
        {
            if (reason == ReflectionCaptureReason.None ||
                !_layersByProbeId.TryGetValue(probeId, out int layer))
                return default;
            ReflectionProbe? probe = _probesByLayer[layer] ?? FindProbe(probeId);
            if (probe == null)
                return default;
            ReflectionCaptureVersion version = _captureVersion;
            if (version == default)
            {
                version = new ReflectionCaptureVersion(
                    _lastAuthoredRevision,
                    0U,
                    0U,
                    0U,
                    0U,
                    0U,
                    1U);
            }

            ulong minimumIntervalFrames = IsRateLimitedLightingRecaptureReason(reason)
                ? SecondsToFrames(_settings.Reflections.MinimumEnvironmentRecaptureIntervalSeconds)
                : 0UL;
            bool ageExceeded = IsRateLimitedLightingRecaptureReason(reason) && MaximumCaptureAgeExceeded(layer);
            ReflectionProbeRecaptureDecision decision = _recapturePolicies[layer].Observe(
                version,
                reason,
                _resourceFrameSerial,
                minimumIntervalFrames,
                bypassInterval || ageExceeded);
            if (decision.Deferred)
                EnqueueDeferredRecapture(layer, probeId);
            if (!decision.RequestCapture)
                return decision;

            SubmitCaptureRequest(layer, probeId, probe, decision);
            return decision;
        }

        private void SubmitCaptureRequest(
            int layer,
            Guid probeId,
            ReflectionProbe probe,
            in ReflectionProbeRecaptureDecision decision)
        {
            ReflectionCaptureVersion version = decision.Version;
            ReflectionProbeCaptureSnapshot snapshot = new(
                probe.Position,
                probe.Rotation,
                probe.Shape,
                probe.BoxExtents,
                probe.Radius);
            _captureScheduler.Request(
                layer,
                probeId,
                version,
                decision.Reasons,
                snapshot,
                _cubemapArrayResourceGeneration,
                version.SceneRadianceRevision);
        }

        private void ProcessDeferredRecaptures()
        {
            int attempts = _deferredRecaptureCount;
            while (attempts-- > 0 && _deferredRecaptureCount > 0)
            {
                int layer = _deferredRecaptureLayers[_deferredRecaptureHead];
                Guid probeId = _deferredRecaptureProbeIds[_deferredRecaptureHead];
                _deferredRecaptureHead = (_deferredRecaptureHead + 1) % _deferredRecaptureLayers.Length;
                _deferredRecaptureCount--;
                _deferredRecaptureQueued[layer] = 0;
                if (!_layersByProbeId.TryGetValue(probeId, out int currentLayer) || currentLayer != layer ||
                    !_recapturePolicies[layer].TryReleaseDeferred(
                        _resourceFrameSerial,
                        out ReflectionProbeRecaptureDecision decision) ||
                    _probesByLayer[layer] == null)
                    continue;

                SubmitCaptureRequest(layer, probeId, _probesByLayer[layer]!, decision);
            }
        }

        private void EnqueueDeferredRecapture(int layer, Guid probeId)
        {
            if ((uint)layer >= AbsoluteMaxProbeCapacity || _deferredRecaptureQueued[layer] != 0 ||
                _deferredRecaptureCount == _deferredRecaptureLayers.Length)
                return;
            _deferredRecaptureLayers[_deferredRecaptureTail] = layer;
            _deferredRecaptureProbeIds[_deferredRecaptureTail] = probeId;
            _deferredRecaptureTail = (_deferredRecaptureTail + 1) % _deferredRecaptureLayers.Length;
            _deferredRecaptureCount++;
            _deferredRecaptureQueued[layer] = 1;
        }

        private bool EnsureCubemapArrayStorage(int requiredProbeCount)
        {
            if (requiredProbeCount <= 0 || (_cubemapArrayCapacity >= requiredProbeCount &&
                _cubemapArrayResolution == ProbeResolution && _cubemapArrayImage.Handle != 0))
                return true;

            int capacity = Math.Min(Math.Max(requiredProbeCount, 1), _settings.Reflections.MaxProbes);
            uint nextMipCount = ReflectionProbeData.CalculateMipCount(ProbeResolution);
            ulong incomingPublishedBytes = ReflectionProbeData.EstimateCubemapArrayBytes(
                capacity,
                ProbeResolution,
                nextMipCount);
            ulong incomingBytes = checked(
                incomingPublishedBytes +
                ReflectionProbeData.EstimateCubemapArrayBytes(1, ProbeResolution, nextMipCount) +
                (ulong)ProbeResolution * ProbeResolution * CaptureDepthBytesPerPixel);
            if (_cubemapArrayImage.Handle != 0 &&
                !RetireCurrentCubemapArrayResources(incomingBytes))
            {
                // Keep the old published image and mappings live until the renderer observes a
                // completion boundary. A resize request is retried on a later frame; no device
                // idle wait is hidden in this feature path.
                return false;
            }

            uint layerCount = checked((uint)capacity * 6u);
            _probeMipCount = nextMipCount;
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
                Extent = new Extent3D { Width = ProbeResolution, Height = ProbeResolution, Depth = 1 },
                MipLevels = _probeMipCount,
                ArrayLayers = layerCount,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit |
                        ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            var allocationInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice,
                Flags = _context.MemoryBudgetExtensionEnabled
                    ? GpuAllocator.AllocationCreateFlags.WithinBudgetBit
                    : default
            };
            GpuAllocator.AllocationInfo allocationResult;
            Image createdImage;
            GpuAllocator.Allocation* createdAllocation;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator, &imageInfo, &allocationInfo,
                &createdImage, &createdAllocation, &allocationResult);
            if (result != Result.Success)
            {
                _cubemapArrayImage = default;
                _cubemapArrayAllocation = null;
                _cubemapArrayCapacity = 0;
                if (_context.IsMemoryBudgetExceeded(result))
                    return false;
                throw new VulkanException("Failed to create reflection probe cubemap array", result);
            }

            _cubemapArrayImage = createdImage;
            _cubemapArrayAllocation = createdAllocation;
            _cubemapArrayCapacity = capacity;
            _cubemapArrayResolution = ProbeResolution;
            _publishedLayerLayouts = new ImageLayout[checked(capacity)];
            try
            {
                _context.SetDebugName(_cubemapArrayImage.Handle, ObjectType.Image, "Reflection Probe Cubemap Array");
                _cubemapArrayView = CreateView(ImageViewType.TypeCubeArray, 0, layerCount, 0, _probeMipCount);
                _debugCubemapView = CreateView(ImageViewType.TypeCube, 0, 6, 0, _probeMipCount);
                _captureFaceViews = new ImageView[layerCount];
                for (uint layer = 0; layer < layerCount; layer++)
                    _captureFaceViews[layer] = CreateView(ImageViewType.Type2D, layer, 1, 0, 1);

                if (!CreateScratchResources(_probeMipCount))
                {
                    DestroyCubemapArrayResources();
                    return false;
                }

                _capturedProbeIds.Clear();
                _metadataDirty = true;
                foreach (Guid probeId in _layersByProbeId.Keys)
                    QueueCapture(probeId, ReflectionCaptureReason.ResourceChanged);
                _descriptorDirty = true;
                return true;
            }
            catch
            {
                // The published image was created by this transaction and has not
                // been exposed to the renderer yet. It is therefore safe to destroy
                // it directly if view or scratch creation fails.
                DestroyCubemapArrayResources();
                throw;
            }
        }

        private bool CreateScratchResources(uint mipCount)
        {
            var allocationInfo = new GpuAllocator.AllocationCreateInfo
            {
                Usage = GpuAllocator.MemoryUsage.AutoPreferDevice,
                Flags = _context.MemoryBudgetExtensionEnabled
                    ? GpuAllocator.AllocationCreateFlags.WithinBudgetBit
                    : default
            };
            var scratchInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ImageType = ImageType.Type2D,
                Format = Format.R16G16B16A16Sfloat,
                Extent = new Extent3D { Width = ProbeResolution, Height = ProbeResolution, Depth = 1 },
                MipLevels = mipCount,
                ArrayLayers = 6,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit |
                        ImageUsageFlags.SampledBit |
                        ImageUsageFlags.StorageBit |
                        ImageUsageFlags.TransferSrcBit |
                        ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };
            Image createdScratchImage;
            GpuAllocator.Allocation* createdScratchAllocation;
            GpuAllocator.AllocationInfo scratchAllocationInfo;
            Result result = GpuAllocator.Apis.CreateImage(
                _context.Allocator,
                &scratchInfo,
                &allocationInfo,
                &createdScratchImage,
                &createdScratchAllocation,
                &scratchAllocationInfo);
            if (result != Result.Success)
            {
                _scratchCaptureImage = default;
                _scratchCaptureAllocation = null;
                if (_context.IsMemoryBudgetExceeded(result))
                    return false;
                throw new VulkanException("Failed to create reflection probe scratch image", result);
            }

            _scratchCaptureImage = createdScratchImage;
            _scratchCaptureAllocation = createdScratchAllocation;
            _scratchFaceLayouts = new ImageLayout[6];
            _scratchMipLayouts = new ImageLayout[mipCount];
            _captureDepthLayout = ImageLayout.Undefined;

            try
            {
                _context.SetDebugName(
                    _scratchCaptureImage.Handle,
                    ObjectType.Image,
                    "Reflection Probe Capture Scratch");
                _scratchCaptureView = CreateView(
                    _scratchCaptureImage,
                    Format.R16G16B16A16Sfloat,
                    ImageViewType.TypeCube,
                    0,
                    6,
                    0,
                    1);
                _scratchFaceViews = new ImageView[6];
                for (uint face = 0; face < 6; face++)
                {
                    _scratchFaceViews[face] = CreateView(
                        _scratchCaptureImage,
                        Format.R16G16B16A16Sfloat,
                        ImageViewType.Type2D,
                        face,
                        1,
                        0,
                        1);
                }
                _scratchMipViews = new ImageView[mipCount];
                for (uint mip = 0; mip < mipCount; mip++)
                {
                    _scratchMipViews[mip] = CreateView(
                        _scratchCaptureImage,
                        Format.R16G16B16A16Sfloat,
                        ImageViewType.Type2DArray,
                        0,
                        6,
                        mip,
                        1);
                }

                var depthInfo = new ImageCreateInfo
                {
                    SType = StructureType.ImageCreateInfo,
                    ImageType = ImageType.Type2D,
                    Format = _captureDepthFormat,
                    Extent = new Extent3D { Width = ProbeResolution, Height = ProbeResolution, Depth = 1 },
                    MipLevels = 1,
                    ArrayLayers = 1,
                    Samples = SampleCountFlags.Count1Bit,
                    Tiling = ImageTiling.Optimal,
                    Usage = ImageUsageFlags.DepthStencilAttachmentBit |
                            ImageUsageFlags.SampledBit |
                            ImageUsageFlags.TransferDstBit,
                    SharingMode = SharingMode.Exclusive,
                    InitialLayout = ImageLayout.Undefined
                };
                Image createdDepthImage;
                GpuAllocator.Allocation* createdDepthAllocation;
                GpuAllocator.AllocationInfo depthAllocationInfo;
                result = GpuAllocator.Apis.CreateImage(
                    _context.Allocator,
                    &depthInfo,
                    &allocationInfo,
                    &createdDepthImage,
                    &createdDepthAllocation,
                    &depthAllocationInfo);
                if (result != Result.Success)
                {
                    if (_context.IsMemoryBudgetExceeded(result))
                    {
                        DestroyScratchResources();
                        return false;
                    }
                    throw new VulkanException("Failed to create reflection probe capture depth image", result);
                }
                _captureDepthImage = createdDepthImage;
                _captureDepthAllocation = createdDepthAllocation;
                _captureDepthView = CreateView(
                    _captureDepthImage,
                    _captureDepthFormat,
                    ImageViewType.Type2D,
                    0,
                    1,
                    0,
                    1,
                    CaptureDepthAspectMask);
                return true;
            }
            catch
            {
                DestroyScratchResources();
                throw;
            }
        }

        private void RegisterIfNeeded()
        {
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        private bool RetireCurrentCubemapArrayResources(ulong incomingBytes)
        {
            if ((_cubemapArrayImage.Handle != 0 && _cubemapArrayAllocation == null) ||
                (_scratchCaptureImage.Handle != 0 && _scratchCaptureAllocation == null) ||
                (_captureDepthImage.Handle != 0 && _captureDepthAllocation == null))
            {
                // An image without its VMA allocation cannot be safely destroyed or retired.
                // Preserve the live generation and retry only after the owning failure path has
                // repaired the inconsistent state.
                return false;
            }

            int viewCount = 0;
            for (int index = 0; index < _captureFaceViews.Length; index++)
                viewCount += _captureFaceViews[index].Handle != 0 ? 1 : 0;
            viewCount += _cubemapArrayView.Handle != 0 ? 1 : 0;
            viewCount += _debugCubemapView.Handle != 0 ? 1 : 0;
            for (int index = 0; index < _scratchFaceViews.Length; index++)
                viewCount += _scratchFaceViews[index].Handle != 0 ? 1 : 0;
            for (int index = 0; index < _scratchMipViews.Length; index++)
                viewCount += _scratchMipViews[index].Handle != 0 ? 1 : 0;
            viewCount += _scratchCaptureView.Handle != 0 ? 1 : 0;
            viewCount += _captureDepthView.Handle != 0 ? 1 : 0;
            int imageCount = (_cubemapArrayImage.Handle != 0 ? 1 : 0) +
                (_scratchCaptureImage.Handle != 0 ? 1 : 0) +
                (_captureDepthImage.Handle != 0 ? 1 : 0);
            int recordCount = viewCount + imageCount;
            if (recordCount == 0)
                return true;
            ulong completionFrame = checked(
                _resourceFrameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL);
            GpuCompletionToken completion = GpuCompletionToken.ForFrameFence(completionFrame);
            Span<GpuRetirementRecord> records = _resourceRetirementScratch.AsSpan(0, recordCount);
            int recordIndex = 0;
            for (int index = 0; index < _captureFaceViews.Length; index++)
            {
                ImageView view = _captureFaceViews[index];
                if (view.Handle == 0)
                    continue;
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.ImageView,
                        view.Handle));
            }

            if (_cubemapArrayView.Handle != 0)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.ImageView,
                        _cubemapArrayView.Handle));
            }
            if (_debugCubemapView.Handle != 0)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.ImageView,
                        _debugCubemapView.Handle));
            }

            for (int index = 0; index < _scratchFaceViews.Length; index++)
            {
                ImageView view = _scratchFaceViews[index];
                if (view.Handle == 0)
                    continue;
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(GpuRetirementResourceKind.ImageView, view.Handle));
            }
            for (int index = 0; index < _scratchMipViews.Length; index++)
            {
                ImageView view = _scratchMipViews[index];
                if (view.Handle == 0)
                    continue;
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(GpuRetirementResourceKind.ImageView, view.Handle));
            }
            if (_scratchCaptureView.Handle != 0)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(GpuRetirementResourceKind.ImageView, _scratchCaptureView.Handle));
            }
            if (_captureDepthView.Handle != 0)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    0UL,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(GpuRetirementResourceKind.ImageView, _captureDepthView.Handle));
            }

            if (_cubemapArrayImage.Handle != 0 && _cubemapArrayAllocation != null)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    CubemapArrayBytes,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.Image,
                        _cubemapArrayImage.Handle,
                        unchecked((ulong)(nuint)_cubemapArrayAllocation)));
            }

            if (_scratchCaptureImage.Handle != 0 && _scratchCaptureAllocation != null)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    ScratchCaptureBytes,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.Image,
                        _scratchCaptureImage.Handle,
                        unchecked((ulong)(nuint)_scratchCaptureAllocation)));
            }
            if (_captureDepthImage.Handle != 0 && _captureDepthAllocation != null)
            {
                records[recordIndex++] = new(
                    _cubemapArrayResourceGeneration,
                    CaptureDepthBytes,
                    _resourceFrameSerial,
                    completion,
                    new GpuRetirementResource(
                        GpuRetirementResourceKind.Image,
                        _captureDepthImage.Handle,
                        unchecked((ulong)(nuint)_captureDepthAllocation)));
            }

            if (recordIndex != recordCount)
                return false;
            if (!_resourceRetirement.TryEnqueueBatch(
                    records,
                    // The batch itself accounts for the old generation. The live-byte argument
                    // accounts for the incoming generation that must coexist until the old
                    // records signal.
                    incomingBytes,
                    out _))
                return false;

            _captureFaceViews = [];
            _cubemapArrayView = default;
            _debugCubemapView = default;
            _cubemapArrayImage = default;
            _cubemapArrayAllocation = null;
            _scratchFaceViews = [];
            _scratchMipViews = [];
            _scratchFaceLayouts = [];
            _scratchMipLayouts = [];
            _scratchCaptureView = default;
            _scratchCaptureImage = default;
            _scratchCaptureAllocation = null;
            _captureDepthView = default;
            _captureDepthLayout = ImageLayout.Undefined;
            _captureDepthImage = default;
            _captureDepthAllocation = null;
            _cubemapArrayCapacity = 0;
            _cubemapArrayResolution = 0;
            _publishedLayerLayouts = [];
            _cubemapArrayResourceGeneration = _cubemapArrayResourceGeneration == uint.MaxValue
                ? 1U
                : _cubemapArrayResourceGeneration + 1U;
            return true;
        }

        private void DestroyRetiredResource(in GpuRetirementRecord record)
        {
            GpuRetirementResource resource = record.Resource;
            switch (resource.Kind)
            {
                case GpuRetirementResourceKind.ImageView:
                    ImageView retiredView = default;
                    retiredView.Handle = resource.Handle;
                    _context.Api.DestroyImageView(
                        _context.Device,
                        retiredView,
                        null);
                    break;
                case GpuRetirementResourceKind.Image:
                    if (resource.AllocationHandle != 0UL)
                    {
                        Image retiredImage = default;
                        retiredImage.Handle = resource.Handle;
                        GpuAllocator.Apis.DestroyImage(
                            _context.Allocator,
                            retiredImage,
                            (GpuAllocator.Allocation*)(nuint)resource.AllocationHandle);
                    }
                    break;
            }
        }

        private ImageView CreateView(ImageViewType viewType, uint baseLayer, uint layerCount, uint baseMip, uint mipCount)
            => CreateView(
                _cubemapArrayImage,
                Format.R16G16B16A16Sfloat,
                viewType,
                baseLayer,
                layerCount,
                baseMip,
                mipCount);

        private ImageView CreateView(
            Image image,
            Format format,
            ImageViewType viewType,
            uint baseLayer,
            uint layerCount,
            uint baseMip,
            uint mipCount,
            ImageAspectFlags aspectMask = ImageAspectFlags.ColorBit)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = viewType,
                Format = format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = aspectMask,
                    BaseMipLevel = baseMip,
                    LevelCount = mipCount,
                    BaseArrayLayer = baseLayer,
                    LayerCount = layerCount
                }
            };
            Result result = _context.Api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);
            if (result != Result.Success)
                throw new VulkanException("Failed to create reflection probe cubemap image view", result);
            return view;
        }

        private void CreateSampler()
        {
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Filter.Linear,
                MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Linear,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MinLod = 0f,
                MaxLod = 32f,
                MaxAnisotropy = 1f
            };
            Result result = _context.Api.CreateSampler(_context.Device, &samplerInfo, null, out _cubemapSampler);
            if (result != Result.Success)
                throw new VulkanException("Failed to create reflection probe cubemap sampler", result);
            _context.SetDebugName(_cubemapSampler.Handle, ObjectType.Sampler, "Reflection Probe Cubemap Sampler");
        }

        private void ValidateCapture(in ReflectionProbeCapture capture)
        {
            bool validTicket = capture.TicketSerial != 0UL &&
                _captureScheduler.GetState(capture.CubemapArrayIndex, capture.ProbeId) !=
                    ReflectionProbeCaptureState.Unregistered;
            if (capture.ProbeId == Guid.Empty || !validTicket ||
                !_layersByProbeId.TryGetValue(capture.ProbeId, out int layer) ||
                layer != capture.CubemapArrayIndex ||
                capture.ResourceGeneration != _cubemapArrayResourceGeneration ||
                _cubemapArrayImage.Handle == 0)
            {
                throw new InvalidOperationException("The reflection probe capture is no longer active or its layer was recycled.");
            }
        }

        private void ValidateWorkResource(in ReflectionProbeWork work, ReflectionProbeWorkKind expectedKind)
        {
            if (work.Kind != expectedKind || work.Ticket.Serial == 0UL ||
                work.Ticket.ResourceGeneration != _cubemapArrayResourceGeneration ||
                _cubemapArrayImage.Handle == 0 || _scratchCaptureImage.Handle == 0)
            {
                throw new InvalidOperationException(
                    "The reflection work item does not belong to the current resource generation.");
            }
            if (expectedKind == ReflectionProbeWorkKind.CaptureFace &&
                (uint)work.Face >= (uint)_scratchFaceLayouts.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(work), "The reflection face is outside the scratch cube.");
            }
            if (expectedKind == ReflectionProbeWorkKind.PrefilterMip &&
                (uint)work.Mip >= (uint)_scratchMipLayouts.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(work), "The reflection mip is outside the scratch cube.");
            }
        }

        private void TransitionImage(
            CommandBuffer commandBuffer,
            Image image,
            ImageLayout oldLayout,
            ImageLayout newLayout,
            in ImageSubresourceRange range,
            PipelineStageFlags2 sourceStage,
            AccessFlags2 sourceAccess,
            PipelineStageFlags2 destinationStage,
            AccessFlags2 destinationAccess)
        {
            if (image.Handle == 0 || oldLayout == newLayout)
                return;

            var barrier = new ImageMemoryBarrier2
            {
                SType = StructureType.ImageMemoryBarrier2,
                SrcStageMask = oldLayout == ImageLayout.Undefined
                    ? PipelineStageFlags2.None
                    : sourceStage,
                SrcAccessMask = oldLayout == ImageLayout.Undefined
                    ? AccessFlags2.None
                    : sourceAccess,
                DstStageMask = destinationStage,
                DstAccessMask = destinationAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = 1,
                PImageMemoryBarriers = &barrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        }

        private void PublishCompletedCapture(in ReflectionProbeCaptureTicket ticket)
        {
            // This line is intentionally kept in the completion path, not the recording path:
            // descriptor readiness changes only after the GPU completion boundary is observed.
            ReflectionProbeCapture capture = new(
                ticket.ProbeId,
                ticket.Layer,
                ProbeResolution,
                _probeMipCount,
                ticket.Serial,
                ticket.ResourceGeneration,
                ReflectionProbeWorkKind.PublishCopy,
                -1,
                -1);
            _capturedProbeIds.Add(capture.ProbeId);
            _captureFrameCounters.RecordCompletedCapture();
            _capturesCompletedTotal++;
            _metadataDirty = true;
        }

        private ReflectionProbe? FindProbe(Guid probeId)
        {
            for (int index = 0; index < _selectedActiveProbes.Count; index++)
            {
                ReflectionProbe probe = _selectedActiveProbes[index];
                if (probe.Id == probeId)
                    return probe;
            }
            return null;
        }

        private ReflectionProbeCaptureSnapshot FindProbeSnapshot(Guid probeId)
        {
            ReflectionProbe? probe = FindProbe(probeId);
            if (probe == null)
                return default;
            return new ReflectionProbeCaptureSnapshot(
                probe.Position,
                probe.Rotation,
                probe.Shape,
                probe.BoxExtents,
                probe.Radius);
        }

        private static ReflectionCaptureReason ResolveCaptureReason(string reason) =>
            reason switch
            {
                "load" => ReflectionCaptureReason.InitialLoad,
                "ddgi-ready" => ReflectionCaptureReason.DdgiChanged,
                "simple-ddgi-dirty" => ReflectionCaptureReason.DdgiChanged,
                "ddgi-dirty" => ReflectionCaptureReason.DdgiChanged,
                _ => ReflectionCaptureReason.Manual
            };

        private static ReflectionCaptureReason ResolveVersionChangeReasons(
            in ReflectionCaptureVersion previous,
            in ReflectionCaptureVersion next)
        {
            ReflectionCaptureReason reasons = ReflectionCaptureReason.None;
            if (previous.SceneRadianceRevision != next.SceneRadianceRevision ||
                previous.AccelerationStructureGeneration != next.AccelerationStructureGeneration)
                reasons |= ReflectionCaptureReason.SceneChanged;
            if (previous.LightRevision != next.LightRevision)
                reasons |= ReflectionCaptureReason.LightChanged;
            if (previous.AdmittedEnvironmentGeneration != next.AdmittedEnvironmentGeneration ||
                previous.ShaderSettingsRevision != next.ShaderSettingsRevision)
                reasons |= ReflectionCaptureReason.EnvironmentChanged;
            if (previous.CompletedDdgiGeneration != next.CompletedDdgiGeneration)
                reasons |= ReflectionCaptureReason.DdgiChanged;
            if (previous.MaterialRevision != next.MaterialRevision)
                reasons |= ReflectionCaptureReason.MaterialChanged;
            return reasons;
        }

        internal static bool IsRateLimitedLightingRecaptureReason(ReflectionCaptureReason reason) =>
            (reason & (ReflectionCaptureReason.EnvironmentChanged |
                       ReflectionCaptureReason.DdgiChanged |
                       ReflectionCaptureReason.LightChanged)) != 0;

        private static ulong SecondsToFrames(float seconds)
        {
            if (float.IsNaN(seconds) || seconds <= 0.0f)
                return 0UL;
            if (float.IsPositiveInfinity(seconds))
                return ulong.MaxValue;

            double frames = Math.Ceiling(seconds * 60.0);
            return frames >= ulong.MaxValue
                ? ulong.MaxValue
                : Math.Max(1UL, (ulong)frames);
        }

        private bool MaximumCaptureAgeExceeded(int layer)
        {
            ulong ageLimit = SecondsToFrames(_settings.Reflections.MaximumEnvironmentCaptureAgeSeconds);
            if (ageLimit == 0UL || !_recapturePolicies[layer].HasActiveVersion)
                return false;
            return _resourceFrameSerial >= _recapturePolicies[layer].LastStartedFrame &&
                   _resourceFrameSerial - _recapturePolicies[layer].LastStartedFrame >= ageLimit;
        }

        internal static bool CountsAsCaptureStart(
            in ReflectionProbeWork work) =>
            work.Kind == ReflectionProbeWorkKind.CaptureFace &&
            work.Face == 0;

        private void UpdateResourceMetrics()
        {
            _probeMipCount = ReflectionProbeData.CalculateMipCount(ProbeResolution);
            // The renderer reports metadata and cubemap residency as separate diagnostics and
            // memory-budget entries; keeping this to metadata avoids double accounting.
            _estimatedBytes = ReflectionResidencyBytes;
        }

        private static long ElapsedMicroseconds(long startTimestamp) =>
            (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);

        private void DestroyCubemapArrayResources()
        {
            DestroyScratchResources();
            foreach (ImageView faceView in _captureFaceViews)
                if (faceView.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, faceView, null);
            _captureFaceViews = [];
            if (_cubemapArrayView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _cubemapArrayView, null);
            if (_debugCubemapView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _debugCubemapView, null);
            _cubemapArrayView = default;
            _debugCubemapView = default;
            if (_cubemapArrayAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(_context.Allocator, _cubemapArrayImage, _cubemapArrayAllocation);
                _cubemapArrayAllocation = null;
                _cubemapArrayImage = default;
            }
            _cubemapArrayCapacity = 0;
            _cubemapArrayResolution = 0;
            _publishedLayerLayouts = [];
        }

        private void DestroyScratchResources()
        {
            foreach (ImageView view in _scratchFaceViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }
            foreach (ImageView view in _scratchMipViews)
            {
                if (view.Handle != 0)
                    _context.Api.DestroyImageView(_context.Device, view, null);
            }
            if (_scratchCaptureView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _scratchCaptureView, null);
            if (_captureDepthView.Handle != 0)
                _context.Api.DestroyImageView(_context.Device, _captureDepthView, null);
            _scratchFaceViews = [];
            _scratchMipViews = [];
            _scratchFaceLayouts = [];
            _scratchMipLayouts = [];
            _scratchCaptureView = default;
            _captureDepthView = default;
            _captureDepthLayout = ImageLayout.Undefined;
            if (_scratchCaptureAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(
                    _context.Allocator,
                    _scratchCaptureImage,
                    _scratchCaptureAllocation);
                _scratchCaptureAllocation = null;
                _scratchCaptureImage = default;
            }
            if (_captureDepthAllocation != null)
            {
                GpuAllocator.Apis.DestroyImage(
                    _context.Allocator,
                    _captureDepthImage,
                    _captureDepthAllocation);
                _captureDepthAllocation = null;
                _captureDepthImage = default;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // Renderer shutdown establishes terminal device idle before disposing feature
            // managers. Drain records after that external boundary; normal resize paths never
            // reach this method and therefore never block the device.
            while (_resourceRetirement.ActiveCount > 0)
            {
                int drained = _resourceRetirement.DrainAfterExternalDeviceIdle(
                    _resourceRetirementScratch);
                for (int index = 0; index < drained; index++)
                    DestroyRetiredResource(_resourceRetirementScratch[index]);
                if (drained == 0)
                    break;
            }
            DestroyCubemapArrayResources();
            if (_cubemapSampler.Handle != 0)
                _context.Api.DestroySampler(_context.Device, _cubemapSampler, null);
            if (_metadataBuffer.IsValid)
                _bufferManager.DestroyBuffer(_metadataBuffer);
        }
    }

    public readonly record struct ReflectionProbeCapture(
        Guid ProbeId,
        int CubemapArrayIndex,
        uint Resolution,
        uint MipCount,
        ulong TicketSerial = 0UL,
        uint ResourceGeneration = 0U,
        ReflectionProbeWorkKind WorkKind = ReflectionProbeWorkKind.None,
        int Face = -1,
        int Mip = -1);
}

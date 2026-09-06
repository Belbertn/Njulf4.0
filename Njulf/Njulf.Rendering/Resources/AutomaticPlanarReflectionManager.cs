using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Vma;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Selects planar receivers without authored helpers and owns their exact
/// VMA-backed capture allocation. Capture images are split into independently
/// allocated levels so every byte remains visible to the central allocation
/// tracker while shaders still receive a complete roughness mip chain.
/// </summary>
public sealed unsafe class AutomaticPlanarReflectionManager : IDisposable
{
    public const uint MetadataMagic = 0x31524c50u; // "PLR1"
    public const uint MetadataVersion = 3u;
    public const int MaximumCaptureCount = 2;
    public const int MetadataBankWordCount = 1024;
    public const int MetadataHeaderWordCount = 16;
    public const int CaptureRecordWordCount = 96;
    public const int CaptureRecordsWordCount =
        MaximumCaptureCount * CaptureRecordWordCount;
    public const int VariableDataWordOffset =
        MetadataHeaderWordCount + CaptureRecordsWordCount;
    public const ulong MetadataBufferBytes =
        2UL * MetadataBankWordCount * sizeof(uint);
    public const uint AutomaticCaptureLayerFlag = 0x1000u;

    internal static readonly RenderTargetDescriptor BaseColorDescriptor = new(
        colorAttachment: true,
        sampled: true,
        storage: true,
        transferSource: true,
        allowDriverCompression: true);

    internal static readonly RenderTargetDescriptor MipColorDescriptor = new(
        colorAttachment: false,
        sampled: true,
        storage: true,
        allowDriverCompression: true);

    internal static readonly RenderTargetDescriptor DepthDescriptor = new(
        colorAttachment: false,
        sampled: true,
        depthAttachment: true);

    internal static readonly RenderTargetDescriptor DepthHistoryDescriptor =
        new(
            colorAttachment: false,
            sampled: true,
            storage: true,
            transferDestination: true);

    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly MeshManager _meshManager;
    private readonly MaterialManager _materialManager;
    private readonly BindlessHeap _bindlessHeap;
    private readonly RenderSettings _settings;
    private readonly AutomaticPlanarExclusionEncodingMode
        _exclusionEncodingMode;
    private readonly Format _depthFormat;
    private readonly BufferHandle _metadataBuffer;
    private readonly List<AutomaticPlanarCaptureResource> _resources = [];
    private readonly AutomaticPlanarSlotState[] _slotStates =
        new AutomaticPlanarSlotState[MaximumCaptureCount];
    private readonly List<AutomaticPlanarPreparedCapture> _prepared = [];
    private readonly AutomaticPlanarSubmittedFrameRing _submittedFrames =
        new();
    private AutomaticPlanarLifecycleFrameSnapshot _currentLifecycle;
    private AutomaticPlanarLifecycleFrameSnapshot _completedLifecycle;
    private bool _frameBegun;
    private ulong _lastSelectionSignature;
    private ulong _lastCameraCutSerial;
    private ulong _lastSceneMutationSerial;
    private uint _captureGeneration;
    private int _metadataBankHighWaterMark;
    private bool _deterministicCapturePhaseResetPending;
    private bool _disposed;

    public AutomaticPlanarReflectionManager(
        VulkanContext context,
        BufferManager bufferManager,
        MeshManager meshManager,
        MaterialManager materialManager,
        BindlessHeap bindlessHeap,
        RenderSettings settings,
        Format depthFormat)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ??
            throw new ArgumentNullException(nameof(bufferManager));
        _meshManager = meshManager ??
            throw new ArgumentNullException(nameof(meshManager));
        _materialManager = materialManager ??
            throw new ArgumentNullException(nameof(materialManager));
        _bindlessHeap = bindlessHeap ??
            throw new ArgumentNullException(nameof(bindlessHeap));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _exclusionEncodingMode = AutomaticPlanarMetadataEncoder.ResolveMode(
            Environment.GetEnvironmentVariable(
                AutomaticPlanarMetadataEncoder
                    .EncodingModeEnvironmentVariable));
        _depthFormat = depthFormat;
        _metadataBuffer = _bufferManager.CreateBuffer(
            MetadataBufferBytes,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.AutoPreferHost,
            AllocationCreateFlags.MappedBit |
            AllocationCreateFlags.HostAccessSequentialWriteBit,
            "Automatic Planar Reflection Metadata",
            MemoryBudgetCategory.RenderTargets);
        new Span<byte>(
            _bufferManager.GetMappedPointer(_metadataBuffer),
            checked((int)MetadataBufferBytes)).Clear();
        _bufferManager.FlushBuffer(
            _metadataBuffer,
            0UL,
            MetadataBufferBytes);
        _bindlessHeap.RegisterStorageBuffer(
            BindlessIndex.AutomaticPlanarReflectionBuffer,
            _bufferManager.GetBuffer(_metadataBuffer),
            0UL,
            MetadataBufferBytes);
    }

    public IReadOnlyList<AutomaticPlanarPreparedCapture> PreparedCaptures =>
        _prepared;

    public bool HasCaptureWork => _prepared.Any(static capture =>
        capture.Action is AutomaticPlanarCaptureAction.Capture or
            AutomaticPlanarCaptureAction.Reproject);

    public uint CaptureGeneration => _captureGeneration;

    public AutomaticPlanarLifecycleFrameSnapshot CurrentLifecycle =>
        _currentLifecycle;

    public AutomaticPlanarLifecycleFrameSnapshot CompletedLifecycle =>
        _completedLifecycle;

    public ulong AllocationBytes => checked(
        _resources.Aggregate(
            _bufferManager.GetBufferAllocationSize(_metadataBuffer),
            static (total, resource) => total + resource.AllocationBytes));

    /// <summary>
    /// Canonicalizes the next prepared capture without touching resources that
    /// may still be referenced by the current submission. Quality tooling calls
    /// this after Draw; the reset is consumed by the following frame.
    /// </summary>
    public void RequestDeterministicCapturePhaseReset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _deterministicCapturePhaseResetPending = true;
    }

    /// <summary>
    /// Consumes the workload previously submitted through this frame slot
    /// after its fence and timestamp queries have completed, then opens the
    /// current frame's submission record.
    /// </summary>
    public void BeginFrame(
        int frameSlot,
        ulong frameSerial,
        bool gpuTimingRecorded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderingConstants.ValidateFrameIndex(frameSlot);
        if (_frameBegun)
        {
            throw new InvalidOperationException(
                "An automatic-planar frame was begun before the previous frame submission was committed.");
        }

        _completedLifecycle = _submittedFrames.TryConsume(
            frameSlot,
            out AutomaticPlanarLifecycleFrameSnapshot completed)
                ? completed
                : default;
        _currentLifecycle = new AutomaticPlanarLifecycleFrameSnapshot(
            Valid: true,
            frameSlot,
            frameSerial,
            gpuTimingRecorded,
            SelectedCount: 0,
            CaptureCount: 0,
            ReprojectionCount: 0,
            BitsetCaptureCount: 0,
            SortedListFallbackCount: 0,
            MetadataCapacityRejectionCount: 0);
        _frameBegun = true;
    }

    /// <summary>
    /// Freezes the prepared CPU workload before command submission and exposes
    /// the completed, timestamp-aligned workload through frame diagnostics.
    /// </summary>
    public void RecordPreparedFrame(SceneRenderingData sceneData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sceneData);
        if (!_frameBegun)
        {
            throw new InvalidOperationException(
                "Automatic-planar workload recording requires an active renderer frame.");
        }

        _currentLifecycle = _currentLifecycle with
        {
            SelectedCount = sceneData.AutomaticPlanarSelectedCount,
            CaptureCount = sceneData.AutomaticPlanarCaptureCount,
            ReprojectionCount = sceneData.AutomaticPlanarReprojectionCount,
            BitsetCaptureCount =
                sceneData.AutomaticPlanarBitsetCaptureCount,
            SortedListFallbackCount =
                sceneData.AutomaticPlanarSortedListFallbackCount,
            MetadataCapacityRejectionCount =
                sceneData.AutomaticPlanarMetadataCapacityRejectionCount
        };
        sceneData.AutomaticPlanarCurrentLifecycle = _currentLifecycle;
        sceneData.AutomaticPlanarCompletedLifecycle = _completedLifecycle;
    }

    /// <summary>
    /// Marks the frozen workload pending only after Vulkan accepts the terminal
    /// graphics submission for the matching frame slot.
    /// </summary>
    public void CommitFrameSubmission(
        int frameSlot,
        ulong frameSerial,
        bool gpuTimingRecorded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenderingConstants.ValidateFrameIndex(frameSlot);
        if (!_frameBegun || !_currentLifecycle.Valid ||
            _currentLifecycle.FrameSlot != frameSlot ||
            _currentLifecycle.FrameSerial != frameSerial)
        {
            throw new InvalidOperationException(
                "Automatic-planar submission does not match the active frame boundary.");
        }

        _submittedFrames.MarkSubmitted(
            frameSlot,
            _currentLifecycle with
            {
                GpuTimingRecorded = gpuTimingRecorded
            });
        _frameBegun = false;
    }

    public void PrepareFrame(Scene scene, SceneRenderingData sceneData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sceneData);
        _prepared.Clear();
        if (_deterministicCapturePhaseResetPending)
        {
            // Invalidating the CPU publication state forces a complete capture
            // into bank zero. The old images remain alive until overwritten by
            // the next command buffer, so in-flight readers are unaffected.
            Array.Clear(_slotStates);
            _lastSelectionSignature = 0UL;
            _deterministicCapturePhaseResetPending = false;
        }

        bool requested = sceneData.EffectiveReflectionMode is
            ReflectionMode.StaticProbesAndPlanar or
            ReflectionMode.HybridRayQuery;
        if (Environment.GetEnvironmentVariable("NJULF_AUTOMATIC_PLANAR_ENABLED") == "0" ||
            !requested || sceneData.EffectiveReflectionImplementation !=
            ReflectionImplementationMode.Adaptive)
        {
            PublishEmptyFrame(sceneData);
            return;
        }

        List<AutomaticPlanarCandidate> candidates =
            CollectCandidates(scene, sceneData, out int rejectedCount,
                out AutomaticPlanarCandidateRejectionReason lastRejection,
                out string lastDetail);
        IReadOnlyList<AutomaticPlanarCluster> ranked =
            AutomaticPlanarClusterer.ClusterAndRank(candidates);
        AutomaticPlanarQualityProfile quality =
            AutomaticPlanarQualityProfile.For(_settings.QualityPreset);
        int requestedCount = Math.Min(
            quality.MaximumCaptures,
            ranked.Count);
        if (requestedCount == 0)
        {
            if (_lastSelectionSignature != 0UL)
            {
                _lastSelectionSignature = 0UL;
                AdvanceGeneration();
            }
            sceneData.AutomaticPlanarCandidateCount = candidates.Count;
            sceneData.AutomaticPlanarRejectedCount = rejectedCount;
            sceneData.AutomaticPlanarRejectionReason = lastRejection;
            sceneData.AutomaticPlanarRejectionDetail = lastDetail;
            PublishEmptyFrame(sceneData, preserveDiagnostics: true);
            return;
        }

        // Hybrid-reflection and probe allocations have independent owners and
        // are already covered by the renderer-wide memory gate. Charging them
        // against this feature-local cap made automatic planar impossible at
        // 1080p whenever the hybrid targets alone exceeded 160 MiB.
        ulong fixedPlanarBytes =
            _bufferManager.GetBufferAllocationSize(_metadataBuffer);
        if (!EnsureResources(
                requestedCount,
                quality.PreferredLinearScale,
                sceneData.ScreenWidth,
                sceneData.ScreenHeight,
                fixedPlanarBytes,
                out AutomaticPlanarCandidateRejectionReason allocationReason,
                out string allocationDetail))
        {
            sceneData.AutomaticPlanarCandidateCount = candidates.Count;
            sceneData.AutomaticPlanarRejectedCount = rejectedCount +
                requestedCount;
            sceneData.AutomaticPlanarRejectionReason = allocationReason;
            sceneData.AutomaticPlanarRejectionDetail = allocationDetail;
            PublishEmptyFrame(sceneData, preserveDiagnostics: true);
            return;
        }

        int selectedCount = Math.Min(requestedCount, _resources.Count);
        int metadataCapacityRejectionCount = 0;
        string metadataCapacityDetail = string.Empty;
        while (selectedCount > 0)
        {
            AutomaticPlanarMetadataBankLayout prospectiveLayout =
                BuildProspectiveMetadataLayout(ranked, selectedCount);
            if (prospectiveLayout.Fits)
                break;
            metadataCapacityDetail = prospectiveLayout.Detail;
            selectedCount--;
            metadataCapacityRejectionCount++;
        }
        if (selectedCount == 0)
        {
            if (_lastSelectionSignature != 0UL)
            {
                _lastSelectionSignature = 0UL;
                AdvanceGeneration();
            }
            Array.Clear(_slotStates);
            sceneData.AutomaticPlanarCandidateCount = candidates.Count;
            sceneData.AutomaticPlanarRejectedCount = checked(
                rejectedCount + ranked.Count);
            sceneData.AutomaticPlanarRejectionReason =
                AutomaticPlanarCandidateRejectionReason.MetadataCapacity;
            sceneData.AutomaticPlanarRejectionDetail =
                metadataCapacityDetail;
            PublishEmptyFrame(
                sceneData,
                preserveDiagnostics: true,
                metadataCapacityRejectionCount);
            return;
        }
        ulong selectionSignature = 1469598103934665603UL;
        for (int slot = 0; slot < selectedCount; slot++)
        {
            AutomaticPlanarCluster cluster = ranked[slot];
            selectionSignature = Hash64(
                selectionSignature,
                ResolveClusterIdentity(cluster));
        }
        bool selectionChanged = selectionSignature != _lastSelectionSignature;
        if (selectionChanged)
        {
            _lastSelectionSignature = selectionSignature;
            AdvanceGeneration();
        }
        bool cameraCut = sceneData.CaptureCameraCutSerial !=
            _lastCameraCutSerial;
        _lastCameraCutSerial = sceneData.CaptureCameraCutSerial;
        bool sceneChanged = scene.MutationSerial != _lastSceneMutationSerial;
        _lastSceneMutationSerial = scene.MutationSerial;

        int captureCount = 0;
        int reprojectionCount = 0;
        uint maximumAge = 0u;
        for (int slot = 0; slot < selectedCount; slot++)
        {
            AutomaticPlanarCluster cluster = ranked[slot];
            AutomaticPlanarPreparedCapture prepared = PrepareCapture(
                slot,
                cluster,
                sceneData,
                cameraCut,
                selectionChanged,
                sceneChanged);
            _prepared.Add(prepared);
            if (prepared.Action == AutomaticPlanarCaptureAction.Capture)
                captureCount++;
            else if (prepared.Action == AutomaticPlanarCaptureAction.Reproject)
                reprojectionCount++;
            maximumAge = Math.Max(maximumAge, prepared.AgeFrames);
        }
        for (int slot = selectedCount; slot < MaximumCaptureCount; slot++)
            _slotStates[slot] = default;

        AutomaticPlanarMetadataBankLayout metadataLayout =
            WriteMetadata(sceneData.CurrentFrameIndex);
        PublishMetadataTelemetry(
            sceneData,
            metadataLayout,
            metadataCapacityRejectionCount);
        sceneData.AutomaticPlanarReflectionActive = _prepared.Count != 0;
        sceneData.AutomaticPlanarCandidateCount = candidates.Count;
        sceneData.AutomaticPlanarSelectedCount = selectedCount;
        sceneData.AutomaticPlanarCaptureCount = captureCount;
        sceneData.AutomaticPlanarReprojectionCount = reprojectionCount;
        sceneData.AutomaticPlanarRejectedCount = checked(
            rejectedCount + Math.Max(0, ranked.Count - selectedCount));
        sceneData.AutomaticPlanarRejectionReason =
            metadataCapacityRejectionCount > 0
                ? AutomaticPlanarCandidateRejectionReason.MetadataCapacity
                : ranked.Count > selectedCount
                    ? AutomaticPlanarCandidateRejectionReason.CaptureLimit
                    : lastRejection;
        sceneData.AutomaticPlanarRejectionDetail =
            metadataCapacityRejectionCount > 0
                ? metadataCapacityDetail
                : ranked.Count > selectedCount
                    ? "Eligible planes exceeded the active quality-tier capture limit."
                    : lastDetail;
        sceneData.AutomaticPlanarCaptureGeneration = _captureGeneration;
        sceneData.AutomaticPlanarEstimatedBytes = AllocationBytes;
        sceneData.AutomaticPlanarResolutionScale = _resources.Count == 0
            ? 0.0f
            : _resources[0].LinearScale;
        sceneData.AutomaticPlanarMaximumCaptureAge = maximumAge;
    }

    private List<AutomaticPlanarCandidate> CollectCandidates(
        Scene scene,
        SceneRenderingData sceneData,
        out int rejectedCount,
        out AutomaticPlanarCandidateRejectionReason lastRejection,
        out string lastDetail)
    {
        var candidates = new List<AutomaticPlanarCandidate>();
        rejectedCount = 0;
        lastRejection = AutomaticPlanarCandidateRejectionReason.None;
        lastDetail = string.Empty;
        uint objectIndex = 0u;

        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (!renderObject.Visible ||
                renderObject.Mesh is not MeshHandle meshHandle ||
                !meshHandle.IsValid)
            {
                continue;
            }
            uint currentObjectIndex = objectIndex++;
            MaterialHandle materialHandle =
                SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name ?? string.Empty);
            AnalyzeInstance(
                meshHandle,
                materialHandle,
                renderObject.WorldMatrix,
                StableGuidHash(renderObject.Id),
                currentObjectIndex,
                renderObject.Revision,
                deforming: renderObject is SkinnedRenderObject
                    { SkinningEnabled: true },
                dynamicOrDirty: !renderObject.IsStatic,
                sceneData,
                candidates,
                ref rejectedCount,
                ref lastRejection,
                ref lastDetail);
        }

        foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
        {
            if (!batch.Visible || batch.Mesh is not MeshHandle meshHandle ||
                !meshHandle.IsValid)
            {
                continue;
            }
            MaterialHandle materialHandle =
                SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                    batch.Material,
                    _materialManager.DefaultMaterialHandle,
                    batch.Name);
            for (int instance = 0; instance < batch.WorldMatrices.Count;
                 instance++)
            {
                uint currentObjectIndex = objectIndex++;
                ulong identity = Hash64(
                    StableGuidHash(batch.Id),
                    checked((ulong)instance + 1UL));
                ulong revision = ((ulong)batch.Revision << 32) |
                    checked((uint)instance + 1u);
                AnalyzeInstance(
                    meshHandle,
                    materialHandle,
                    batch.WorldMatrices[instance],
                    identity,
                    currentObjectIndex,
                    revision,
                    deforming: false,
                    dynamicOrDirty: false,
                    sceneData,
                    candidates,
                    ref rejectedCount,
                    ref lastRejection,
                    ref lastDetail);
            }
        }
        return candidates;
    }

    private void AnalyzeInstance(
        MeshHandle meshHandle,
        MaterialHandle materialHandle,
        Matrix4x4 worldMatrix,
        ulong stableIdentity,
        uint objectIndex,
        ulong contentRevision,
        bool deforming,
        bool dynamicOrDirty,
        SceneRenderingData sceneData,
        List<AutomaticPlanarCandidate> candidates,
        ref int rejectedCount,
        ref AutomaticPlanarCandidateRejectionReason lastRejection,
        ref string lastDetail)
    {
        MaterialDefinition definition =
            _materialManager.GetMaterialDefinition(materialHandle);
        if (!definition.AutomaticPlanarReflectionEnabled)
        {
            rejectedCount++;
            lastRejection = AutomaticPlanarCandidateRejectionReason
                .MaterialOptInDisabled;
            lastDetail =
                "The material has not opted in to automatic planar reflection.";
            return;
        }

        MeshTransportGeometry geometry;
        try
        {
            geometry = _meshManager.GetTransportGeometry(meshHandle);
        }
        catch (InvalidOperationException exception)
        {
            rejectedCount++;
            lastRejection = AutomaticPlanarCandidateRejectionReason
                .InvalidEvidence;
            lastDetail = exception.Message;
            return;
        }

        GiPrimitivePlanarEvidence evidence = geometry
            .PrimitiveTransportProfile?.PlanarEvidence ??
            GiPrimitivePlanarEvidence.NotAnalyzed;
        if (evidence.RejectionReason ==
            GiPrimitivePlanarEvidenceRejectionReason.NotAnalyzed)
        {
            Vector3[] positions = geometry.VertexPositions.Span
                .ToArray()
                .Select(static vertex => new Vector3(
                    vertex.Position.X,
                    vertex.Position.Y,
                    vertex.Position.Z))
                .ToArray();
            evidence = GiPrimitivePlanarEvidenceAnalyzer.Analyze(
                positions,
                geometry.Indices.Span,
                deforming || geometry.IsSkinned);
        }

        GiMaterialTransportProfile materialProfile =
            _materialManager.GetMaterialTransportProfile(materialHandle);
        GPUMaterialData gpuMaterial =
            _materialManager.GetMaterialData(materialHandle);
        int materialIndex =
            _materialManager.ResolveMaterialIndex(materialHandle);
        ResolveMaterialStatistics(
            definition,
            geometry.PrimitiveTransportProfile,
            materialProfile,
            out float meanRoughness,
            out float maximumF0,
            out bool statisticsComplete);
        AutomaticPlanarMaterialSemantic semantic =
            definition.Extensions.OpticalBoundary ==
                OpticalBoundaryKind.WaterSurface
                ? AutomaticPlanarMaterialSemantic.WaterSurface
                : definition.Extensions.CausticCasterPolicy ==
                    GiCausticCasterPolicy.Mirror
                    ? AutomaticPlanarMaterialSemantic.Mirror
                    // The opt-in is the explicit authoring signal for a
                    // non-water/non-mirror planar material. Treat that narrow
                    // case as wet ground; arbitrary generic materials are not
                    // admitted by the analyzer.
                    : AutomaticPlanarMaterialSemantic.WetGround;
        float projectedPixels = ResolveProjectedPixels(
            evidence,
            worldMatrix,
            sceneData.ViewProjectionMatrix,
            sceneData.ScreenWidth,
            sceneData.ScreenHeight);
        Vector3 localOrigin = evidence.LocalOrigin * worldMatrix;
        Vector3 toCamera = sceneData.CameraPosition - localOrigin;
        float distance = toCamera.Length();
        Vector3 localNormal = new(
            evidence.LocalPlane.X,
            evidence.LocalPlane.Y,
            evidence.LocalPlane.Z);
        Vector3 worldNormal = TransformNormal(localNormal, worldMatrix);
        float cosine = distance > 1.0e-6f &&
            worldNormal.LengthSquared() > 1.0e-12f
                ? MathF.Abs(Vector3.Dot(
                    worldNormal.Normalized(),
                    toCamera / distance))
                : 1.0f;
        float fresnel = MathF.Pow(1.0f - Math.Clamp(cosine, 0.0f, 1.0f), 5.0f);
        uint receiverIdentity = AutomaticPlanarReceiverIdentity.Create(
            objectIndex,
            checked((uint)materialIndex),
            gpuMaterial.MaterialRevision);
        ulong completeRevision = Hash64(
            contentRevision,
            gpuMaterial.MaterialRevision);
        AutomaticPlanarCandidateAdmission admission =
            AutomaticPlanarCandidateAnalyzer.Analyze(
                new AutomaticPlanarCandidateInput(
                    stableIdentity,
                    objectIndex,
                    completeRevision,
                    receiverIdentity,
                    evidence,
                    worldMatrix,
                    definition.AutomaticPlanarReflectionEnabled,
                    semantic,
                    meanRoughness,
                    maximumF0,
                    statisticsComplete,
                    Visible: true,
                    deforming || geometry.IsSkinned,
                    projectedPixels,
                    fresnel,
                    distance,
                    dynamicOrDirty),
                sceneData.ScreenWidth,
                sceneData.ScreenHeight);
        if (admission.Admitted)
        {
            float maximumRoughness = Math.Max(definition.RoughnessFactor,
                definition.Extensions.ClearcoatFactor > 0 ? definition.Extensions.ClearcoatRoughness : 0f);
            // Derivative AA can add up to .25 in alpha-squared space.
            if (sceneData.SpecularAntialiasingMode == SpecularAntialiasingMode.GeometricVariance)
                maximumRoughness = MathF.Pow(Math.Min(1f, MathF.Pow(maximumRoughness, 4f) + 0.25f), 0.25f);
            candidates.Add(admission.Candidate with { MaximumSamplingRoughness = maximumRoughness });
            return;
        }
        rejectedCount++;
        lastRejection = admission.RejectionReason;
        lastDetail = admission.Detail;
    }

    private static void ResolveMaterialStatistics(
        MaterialDefinition definition,
        GiPrimitiveTransportProfile? primitiveProfile,
        GiMaterialTransportProfile materialProfile,
        out float meanRoughness,
        out float maximumF0,
        out bool statisticsComplete)
    {
        bool primitiveMetallicRoughness = primitiveProfile is not null &&
            primitiveProfile.Validity.HasFlag(
                GiPrimitiveTransportProfileValidity.MetallicRoughness) &&
            primitiveProfile.Validity.HasFlag(
                GiPrimitiveTransportProfileValidity.Finite);
        bool primitiveTexturesComplete = primitiveProfile is not null &&
            primitiveProfile.Validity.HasFlag(
                GiPrimitiveTransportProfileValidity.TextureSamplingComplete);
        bool materialStatistics = materialProfile.Has(
            GiMaterialTransportFlags.BaseStatisticsValid);
        bool hasMetallicRoughnessTexture =
            definition.MetallicRoughness.IsBound;
        statisticsComplete = !hasMetallicRoughnessTexture ||
            primitiveMetallicRoughness && primitiveTexturesComplete ||
            materialStatistics && materialProfile.Quality >=
                GiTransportProfileQuality.TextureStatistics;
        meanRoughness = primitiveMetallicRoughness
            ? (float)primitiveProfile!.MeanRoughness
            : materialStatistics
                ? materialProfile.MeanRoughness
                : definition.RoughnessFactor;
        float metallic = primitiveMetallicRoughness
            ? (float)primitiveProfile!.MeanMetallic
            : materialStatistics
                ? materialProfile.MeanMetallic
                : definition.MetallicFactor;
        float baseMaximum = Math.Max(
            definition.BaseColorFactor.X,
            Math.Max(
                definition.BaseColorFactor.Y,
                definition.BaseColorFactor.Z));
        float dielectric = 0.04f * definition.Extensions.SpecularFactor *
            Math.Max(
                definition.Extensions.SpecularColorFactor.X,
                Math.Max(
                    definition.Extensions.SpecularColorFactor.Y,
                    definition.Extensions.SpecularColorFactor.Z));
        maximumF0 = hasMetallicRoughnessTexture && statisticsComplete
            ? 1.0f
            : dielectric + (baseMaximum - dielectric) *
                Math.Clamp(metallic, 0.0f, 1.0f);
    }

    private AutomaticPlanarPreparedCapture PrepareCapture(
        int slot,
        AutomaticPlanarCluster cluster,
        SceneRenderingData sceneData,
        bool cameraCut,
        bool selectionChanged,
        bool sceneChanged)
    {
        ulong clusterIdentity = ResolveClusterIdentity(cluster);
        ulong contentSignature = ResolveClusterContentSignature(cluster);
        AutomaticPlanarSlotState previous = _slotStates[slot];
        int sourceBank = previous.Valid ? previous.PublishedBank : 1;
        int destinationBank = 1 - sourceBank;
        AutomaticPlanarCaptureView view = CreateCaptureView(
            slot,
            cluster,
            sceneData,
            _resources[slot]);
        var policyState = new AutomaticPlanarCaptureState(
            previous.Valid,
            previous.ClusterIdentity,
            previous.CaptureGeneration,
            previous.AgeFrames,
            cluster.DynamicOrDirty,
            previous.Confidence,
            view.ViewProjection,
            previous.PublishedViewProjection);
        AutomaticPlanarCaptureAction action =
            AutomaticPlanarCapturePolicy.Resolve(
                policyState,
                clusterIdentity,
                cameraCut,
                selectionChanged || previous.ClusterIdentity != clusterIdentity,
                previous.ContentSignature != contentSignature,
                // Scene mutations include geometry, lighting, environment,
                // static batches, and foliage. Treating the complete serial
                // as the conservative reflected-frustum dirty oracle keeps a
                // live capture correct even when a producer cannot provide a
                // tighter world-space dirty bound.
                dirtyRegionIntersectsReflectedFrustum: sceneChanged);
        SecondaryViewRegion previousRegion = previous.CaptureRegion.Resolve(view.Width, view.Height);
        SecondaryViewRegion requiredRegion = view.Region.Resolve(view.Width, view.Height);
        bool previousCropped = previousRegion.Width != view.Width || previousRegion.Height != view.Height;
        if (previous.Valid && previousCropped &&
            (!previous.CapturedViewProjection.Equals(view.ViewProjection) ||
             requiredRegion.X < previousRegion.X || requiredRegion.Y < previousRegion.Y ||
             requiredRegion.X + requiredRegion.Width > previousRegion.X + previousRegion.Width ||
             requiredRegion.Y + requiredRegion.Height > previousRegion.Y + previousRegion.Height))
        {
            // A cropped capture cannot supply newly exposed directions. Preserve normal
            // stationary reprojection; refresh on camera motion until a tighter coverage oracle exists.
            action = AutomaticPlanarCaptureAction.Capture;
        }
        uint age;
        float confidence;
        Matrix4x4 sampleViewProjection = view.ViewProjection;
        uint captureGeneration = previous.CaptureGeneration;
        if (action == AutomaticPlanarCaptureAction.Capture)
        {
            age = 0u;
            confidence = 1.0f;
            captureGeneration = AdvanceGeneration();
        }
        else
        {
            age = checked(previous.AgeFrames + 1u);
            confidence = AutomaticPlanarCapturePolicy
                .ResolveReprojectedConfidence(
                    previous.Confidence,
                    // The per-pixel reprojection shader carries exact hole
                    // confidence in alpha. This conservative frame-level
                    // estimate prevents stale captures retaining full weight.
                    holeFraction: 0.1f,
                    age);
        }

        ResolveClusterBounds(
            cluster,
            out Vector2 boundsMinimum,
            out Vector2 boundsMaximum);
        uint[] receiverIdentities = cluster.ReceiverIdentities
            .Order()
            .ToArray();
        uint[] objectIndices = cluster.Members
            .Select(static member => member.ObjectIndex)
            .Distinct()
            .Order()
            .ToArray();
        var prepared = new AutomaticPlanarPreparedCapture(
            slot,
            clusterIdentity,
            action,
            view,
            sourceBank,
            destinationBank,
            sampleViewProjection,
            previous.Valid
                ? previous.PublishedViewProjection
                : view.ViewProjection,
            view.ViewProjection.Invert(),
            previous.Valid
                ? previous.PublishedViewProjection.Invert()
                : view.ViewProjection.Invert(),
            cluster.Representative.WorldPlane,
            cluster.Representative.WorldOrigin,
            cluster.Representative.WorldTangent,
            cluster.Representative.WorldBitangent,
            boundsMinimum,
            boundsMaximum,
            cluster.Representative.WorldDiagonal,
            confidence,
            age,
            captureGeneration,
            receiverIdentities,
            objectIndices,
            _resources[slot]);
        _slotStates[slot] = new AutomaticPlanarSlotState(
            true,
            clusterIdentity,
            contentSignature,
            captureGeneration,
            age,
            confidence,
            destinationBank,
            view.ViewProjection)
        {
            CaptureRegion = action == AutomaticPlanarCaptureAction.Capture ? view.Region : previous.CaptureRegion,
            CapturedViewProjection = action == AutomaticPlanarCaptureAction.Capture
                ? view.ViewProjection : previous.CapturedViewProjection
        };
        return prepared;
    }

    private AutomaticPlanarCaptureView CreateCaptureView(
        int slot,
        AutomaticPlanarCluster cluster,
        SceneRenderingData sceneData,
        AutomaticPlanarCaptureResource resource)
    {
        Vector4 plane = cluster.Representative.WorldPlane;
        Vector3 cameraForward = new(
            -sceneData.InverseViewMatrix.M31,
            -sceneData.InverseViewMatrix.M32,
            -sceneData.InverseViewMatrix.M33);
        Vector3 cameraUp = new(
            sceneData.InverseViewMatrix.M21,
            sceneData.InverseViewMatrix.M22,
            sceneData.InverseViewMatrix.M23);
        cameraForward = cameraForward.LengthSquared() > 1.0e-12f
            ? cameraForward.Normalized()
            : Vector3.Forward;
        cameraUp = cameraUp.LengthSquared() > 1.0e-12f
            ? cameraUp.Normalized()
            : Vector3.UnitY;
        Vector3 reflectedPosition = AutomaticPlanarCameraMath.ReflectPoint(
            sceneData.CameraPosition,
            plane);
        Vector3 reflectedForward = AutomaticPlanarCameraMath
            .ReflectDirection(cameraForward, plane).Normalized();
        Vector3 reflectedUp = AutomaticPlanarCameraMath
            .ReflectDirection(cameraUp, plane).Normalized();
        if (Vector3.Cross(reflectedForward, reflectedUp).LengthSquared() <=
            1.0e-8f)
        {
            reflectedUp = MathF.Abs(reflectedForward.Y) < 0.95f
                ? Vector3.UnitY
                : Vector3.UnitX;
        }
        Matrix4x4 view = Matrix4x4.CreateLookAt(
            reflectedPosition,
            reflectedPosition + reflectedForward,
            reflectedUp);
        Matrix4x4 projection = sceneData.ProjectionMatrix;
        float cameraPlaneDistance = plane.X * sceneData.CameraPosition.X +
            plane.Y * sceneData.CameraPosition.Y +
            plane.Z * sceneData.CameraPosition.Z + plane.W;
        Vector4 clipPlane = cameraPlaneDistance < 0.0f ? -plane : plane;
        return new AutomaticPlanarCaptureView(
            slot,
            resource.Width,
            resource.Height,
            view,
            projection,
            view * projection,
            reflectedPosition,
            clipPlane)
        {
            Region = Environment.GetEnvironmentVariable("NJULF_SECONDARY_VIEW_CROP") == "0"
                ? default
                : SecondaryViewFootprint.Compute(cluster, sceneData.ViewProjectionMatrix,
                    view * projection, resource.Width, resource.Height, resource.MipCount)
        };
    }

    private bool EnsureResources(
        int requestedCount,
        float preferredScale,
        uint screenWidth,
        uint screenHeight,
        ulong fixedPlanarBytes,
        out AutomaticPlanarCandidateRejectionReason reason,
        out string detail)
    {
        reason = AutomaticPlanarCandidateRejectionReason.None;
        detail = string.Empty;
        if (_resources.Count >= requestedCount &&
            _resources.All(resource => resource.MatchesExtent(
                screenWidth,
                screenHeight)))
        {
            ulong total = checked(fixedPlanarBytes + _resources.Aggregate(
                0UL,
                static (sum, resource) => sum + resource.AllocationBytes));
            if (total <= AutomaticPlanarMemoryPlanner.HighBudgetBytes)
                return true;
            reason = AutomaticPlanarCandidateRejectionReason.MemoryDenied;
            detail = "Existing exact planar allocations exceed the 160 MiB automatic-planar budget.";
            return false;
        }

        if (_resources.Count != 0)
        {
            // Extent recreation is performed only at the renderer's existing
            // swapchain/device-idle boundary. Until then, fail closed instead
            // of destroying an image that an in-flight frame may still read.
            bool extentMismatch = _resources.Any(resource =>
                !resource.MatchesExtent(screenWidth, screenHeight));
            if (extentMismatch)
            {
                reason = AutomaticPlanarCandidateRejectionReason.Stale;
                detail = "Planar resources are waiting for the swapchain recreation boundary.";
                return false;
            }
        }

        float[] scales = preferredScale >= 0.5f
            ? [0.5f, 0.375f, 0.25f]
            : preferredScale >= 0.375f
                ? [0.375f, 0.25f]
                : [0.25f];
        int existingCount = _resources.Count;
        foreach (float scale in scales)
        {
            var additions = new List<AutomaticPlanarCaptureResource>();
            try
            {
                for (int slot = existingCount; slot < requestedCount; slot++)
                {
                    additions.Add(new AutomaticPlanarCaptureResource(
                        _context,
                        _bindlessHeap,
                        _depthFormat,
                        slot,
                        screenWidth,
                        screenHeight,
                        scale));
                }
                ulong exact = _resources.Aggregate(
                    additions.Aggregate(
                        0UL,
                        static (sum, resource) =>
                            sum + resource.AllocationBytes),
                    static (sum, resource) => sum + resource.AllocationBytes);
                if (checked(fixedPlanarBytes + exact) >
                    AutomaticPlanarMemoryPlanner.HighBudgetBytes)
                {
                    foreach (AutomaticPlanarCaptureResource resource in additions)
                        resource.Dispose();
                    continue;
                }
                _resources.AddRange(additions);
                return true;
            }
            catch (Exception exception) when (
                exception is VulkanException or BufferAllocationException or
                InvalidOperationException)
            {
                foreach (AutomaticPlanarCaptureResource resource in additions)
                    resource.Dispose();
                detail = "Automatic planar allocation failed: " +
                    exception.Message;
            }
        }
        reason = AutomaticPlanarCandidateRejectionReason.MemoryDenied;
        detail = string.IsNullOrWhiteSpace(detail)
            ? "The minimum 0.25-scale exact planar allocation exceeds the 160 MiB automatic-planar budget."
            : detail;
        return false;
    }

    public void ReleaseForSwapchainRecreation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (AutomaticPlanarCaptureResource resource in _resources)
            resource.Dispose();
        _resources.Clear();
        _prepared.Clear();
        Array.Clear(_slotStates);
        _lastSelectionSignature = 0UL;
        _lastCameraCutSerial = 0UL;
        _lastSceneMutationSerial = 0UL;
        AdvanceGeneration();
    }

    private AutomaticPlanarMetadataBankLayout BuildProspectiveMetadataLayout(
        IReadOnlyList<AutomaticPlanarCluster> ranked,
        int selectedCount)
    {
        var inputs = new AutomaticPlanarMetadataCaptureInput[selectedCount];
        for (int slot = 0; slot < selectedCount; slot++)
        {
            AutomaticPlanarCluster cluster = ranked[slot];
            inputs[slot] = new AutomaticPlanarMetadataCaptureInput(
                slot,
                cluster.ReceiverIdentities.Order().ToArray(),
                cluster.Members
                    .Select(static member => member.ObjectIndex)
                    .Distinct()
                    .Order()
                    .ToArray(),
                _resources[slot].GetTextureIndices(0));
        }
        return AutomaticPlanarMetadataEncoder.Build(
            inputs,
            _exclusionEncodingMode,
            MetadataBankWordCount,
            VariableDataWordOffset);
    }

    private AutomaticPlanarMetadataBankLayout BuildPreparedMetadataLayout()
    {
        AutomaticPlanarMetadataCaptureInput[] inputs = _prepared
            .Select(static capture =>
                new AutomaticPlanarMetadataCaptureInput(
                    capture.Slot,
                    capture.ReceiverIdentities,
                    capture.ExcludedObjectIndices,
                    capture.Resource.GetTextureIndices(
                        capture.DestinationBank)))
            .ToArray();
        return AutomaticPlanarMetadataEncoder.Build(
            inputs,
            _exclusionEncodingMode,
            MetadataBankWordCount,
            VariableDataWordOffset);
    }

    private AutomaticPlanarMetadataBankLayout WriteMetadata(uint frameIndex)
    {
        AutomaticPlanarMetadataBankLayout layout =
            BuildPreparedMetadataLayout();
        if (!layout.Fits)
        {
            throw new InvalidOperationException(
                layout.Detail +
                " No mapped metadata memory was modified.");
        }

        int bank = checked((int)(frameIndex % 2u));
        uint* destination = (uint*)_bufferManager.GetMappedPointer(
            _metadataBuffer) + bank * MetadataBankWordCount;
        new Span<uint>(destination, MetadataBankWordCount).Clear();
        destination[0] = MetadataMagic;
        destination[1] = MetadataVersion;
        destination[2] = checked((uint)_prepared.Count);
        destination[3] = _captureGeneration;
        foreach (AutomaticPlanarPreparedCapture capture in _prepared)
        {
            AutomaticPlanarMetadataCaptureLayout captureLayout =
                layout.Captures[capture.Slot];
            int record = MetadataHeaderWordCount +
                capture.Slot * CaptureRecordWordCount;
            WriteVector4(destination, record + 0, capture.WorldPlane);
            WriteVector3(destination, record + 4, capture.WorldOrigin);
            destination[record + 7] = FloatBits(capture.Confidence);
            WriteVector3(destination, record + 8, capture.WorldTangent);
            destination[record + 11] = FloatBits(capture.BoundsMinimum.X);
            WriteVector3(destination, record + 12, capture.WorldBitangent);
            destination[record + 15] = FloatBits(capture.BoundsMinimum.Y);
            destination[record + 16] = FloatBits(capture.BoundsMaximum.X);
            destination[record + 17] = FloatBits(capture.BoundsMaximum.Y);
            destination[record + 18] = FloatBits(capture.WorldDiagonal);
            destination[record + 19] = capture.CaptureGeneration;
            destination[record + 20] = capture.AgeFrames;
            destination[record + 21] = capture.Resource.Width;
            destination[record + 22] = capture.Resource.Height;
            destination[record + 23] = checked((uint)capture.Resource.MipCount);
            WriteMatrix(destination, record + 24, capture.SampleViewProjection);
            WriteMatrix(destination, record + 40,
                capture.PreviousViewProjection);
            WriteMatrix(destination, record + 56,
                capture.CurrentInverseViewProjection);
            WriteMatrix(destination, record + 72,
                capture.PreviousInverseViewProjection);

            destination[record + 88] = checked(
                (uint)captureLayout.ReceiverIdentities.Length);
            destination[record + 89] = captureLayout.ReceiverOffset;
            WritePayload(
                destination,
                captureLayout.ReceiverOffset,
                captureLayout.ReceiverIdentities);
            destination[record + 90] = captureLayout.ExclusionDescriptor;
            destination[record + 91] = captureLayout.ExclusionOffset;
            WritePayload(
                destination,
                captureLayout.ExclusionOffset,
                captureLayout.ExclusionPayload);
            destination[record + 92] = captureLayout.TextureOffset;
            WritePayload(
                destination,
                captureLayout.TextureOffset,
                captureLayout.TextureIndices);
            (uint identityLow, uint identityHigh) =
                SplitClusterIdentity(capture.ClusterIdentity);
            destination[record + 93] = identityLow;
            destination[record + 94] = identityHigh;
            destination[record + 95] = (uint)capture.Action;
        }
        ulong offset = checked((ulong)bank * MetadataBankWordCount * sizeof(uint));
        _bufferManager.FlushBuffer(
            _metadataBuffer,
            offset,
            checked((ulong)MetadataBankWordCount * sizeof(uint)));
        _metadataBankHighWaterMark = Math.Max(
            _metadataBankHighWaterMark,
            layout.WordsUsed);
        return layout;
    }

    private static void WritePayload(
        uint* destination,
        uint offset,
        IReadOnlyList<uint> payload)
    {
        for (int index = 0; index < payload.Count; index++)
            destination[checked((int)offset + index)] = payload[index];
    }

    internal static (uint Low, uint High) SplitClusterIdentity(
        ulong identity) =>
        (unchecked((uint)identity), unchecked((uint)(identity >> 32)));

    private void PublishEmptyFrame(
        SceneRenderingData sceneData,
        bool preserveDiagnostics = false,
        int metadataCapacityRejectionCount = 0)
    {
        _prepared.Clear();
        AutomaticPlanarMetadataBankLayout metadataLayout =
            WriteMetadata(sceneData.CurrentFrameIndex);
        PublishMetadataTelemetry(
            sceneData,
            metadataLayout,
            metadataCapacityRejectionCount);
        sceneData.AutomaticPlanarReflectionActive = false;
        if (!preserveDiagnostics)
        {
            sceneData.AutomaticPlanarCandidateCount = 0;
            sceneData.AutomaticPlanarRejectedCount = 0;
            sceneData.AutomaticPlanarRejectionReason =
                AutomaticPlanarCandidateRejectionReason.None;
            sceneData.AutomaticPlanarRejectionDetail = string.Empty;
        }
        sceneData.AutomaticPlanarSelectedCount = 0;
        sceneData.AutomaticPlanarCaptureCount = 0;
        sceneData.AutomaticPlanarReprojectionCount = 0;
        sceneData.AutomaticPlanarCaptureGeneration = _captureGeneration;
        sceneData.AutomaticPlanarEstimatedBytes = AllocationBytes;
        sceneData.AutomaticPlanarResolutionScale = _resources.Count == 0
            ? 0.0f
            : _resources[0].LinearScale;
        sceneData.AutomaticPlanarMaximumCaptureAge = 0u;
    }

    private void PublishMetadataTelemetry(
        SceneRenderingData sceneData,
        AutomaticPlanarMetadataBankLayout layout,
        int capacityRejectionCount)
    {
        sceneData.AutomaticPlanarExclusionEncodingMode =
            _exclusionEncodingMode;
        sceneData.AutomaticPlanarBitsetCaptureCount =
            layout.BitsetCaptureCount;
        sceneData.AutomaticPlanarSortedListFallbackCount =
            layout.SortedListCaptureCount;
        sceneData.AutomaticPlanarMetadataSlots = layout.Captures
            .Select(static capture =>
                new AutomaticPlanarMetadataSlotTelemetry(
                    capture.Slot,
                    capture.ExcludedObjectCount,
                    capture.BitsetPayloadWords,
                    capture.SortedListPayloadWords))
            .ToArray();
        sceneData.AutomaticPlanarMetadataPayloadWordCount =
            layout.PayloadWordCount;
        sceneData.AutomaticPlanarMetadataWordsUsed = layout.WordsUsed;
        sceneData.AutomaticPlanarMetadataBankHighWaterMark =
            _metadataBankHighWaterMark;
        sceneData.AutomaticPlanarMetadataCapacityRejectionCount =
            capacityRejectionCount;
    }

    private static float ResolveProjectedPixels(
        GiPrimitivePlanarEvidence evidence,
        Matrix4x4 world,
        Matrix4x4 viewProjection,
        uint width,
        uint height)
    {
        if (!evidence.IsValid || width == 0u || height == 0u)
            return 0.0f;
        Span<Vector2> corners = stackalloc Vector2[4]
        {
            evidence.ProjectedBoundsMin,
            new(evidence.ProjectedBoundsMax.X,
                evidence.ProjectedBoundsMin.Y),
            evidence.ProjectedBoundsMax,
            new(evidence.ProjectedBoundsMin.X,
                evidence.ProjectedBoundsMax.Y)
        };
        Vector2 minimum = new(float.PositiveInfinity);
        Vector2 maximum = new(float.NegativeInfinity);
        bool visible = false;
        foreach (Vector2 corner in corners)
        {
            Vector3 local = evidence.LocalOrigin +
                evidence.LocalTangent * corner.X +
                evidence.LocalBitangent * corner.Y;
            Vector3 worldPosition = local * world;
            Vector4 clip = TransformHomogeneous(worldPosition, viewProjection);
            if (!float.IsFinite(clip.W) || clip.W <= 1.0e-6f)
                continue;
            Vector2 ndc = new(
                Math.Clamp(clip.X / clip.W, -1.0f, 1.0f),
                Math.Clamp(clip.Y / clip.W, -1.0f, 1.0f));
            minimum = new Vector2(
                Math.Min(minimum.X, ndc.X),
                Math.Min(minimum.Y, ndc.Y));
            maximum = new Vector2(
                Math.Max(maximum.X, ndc.X),
                Math.Max(maximum.Y, ndc.Y));
            visible = true;
        }
        if (!visible)
            return 0.0f;
        Vector2 extent = maximum - minimum;
        return Math.Max(extent.X, 0.0f) * 0.5f * width *
            Math.Max(extent.Y, 0.0f) * 0.5f * height;
    }

    private static void ResolveClusterBounds(
        AutomaticPlanarCluster cluster,
        out Vector2 minimum,
        out Vector2 maximum)
    {
        AutomaticPlanarCandidate representative = cluster.Representative;
        minimum = new Vector2(float.PositiveInfinity);
        maximum = new Vector2(float.NegativeInfinity);
        Span<Vector2> corners = stackalloc Vector2[4];
        foreach (AutomaticPlanarCandidate member in cluster.Members)
        {
            corners[0] = member.ProjectedBoundsMin;
            corners[1] = new Vector2(
                member.ProjectedBoundsMax.X,
                member.ProjectedBoundsMin.Y);
            corners[2] = member.ProjectedBoundsMax;
            corners[3] = new Vector2(
                member.ProjectedBoundsMin.X,
                member.ProjectedBoundsMax.Y);
            foreach (Vector2 corner in corners)
            {
                Vector3 worldPosition = member.WorldOrigin +
                    member.WorldTangent * corner.X +
                    member.WorldBitangent * corner.Y;
                Vector3 relative = worldPosition - representative.WorldOrigin;
                Vector2 projected = new(
                    Vector3.Dot(relative, representative.WorldTangent),
                    Vector3.Dot(relative, representative.WorldBitangent));
                minimum = new Vector2(
                    Math.Min(minimum.X, projected.X),
                    Math.Min(minimum.Y, projected.Y));
                maximum = new Vector2(
                    Math.Max(maximum.X, projected.X),
                    Math.Max(maximum.Y, projected.Y));
            }
        }
    }

    private static ulong ResolveClusterIdentity(AutomaticPlanarCluster cluster)
    {
        ulong hash = 1469598103934665603UL;
        foreach (AutomaticPlanarCandidate candidate in cluster.Members
                     .OrderBy(static candidate => candidate.StableIdentity))
        {
            hash = Hash64(hash, candidate.StableIdentity);
            hash = Hash64(hash, candidate.ReceiverIdentity);
        }
        return hash;
    }

    private static ulong ResolveClusterContentSignature(
        AutomaticPlanarCluster cluster)
    {
        ulong hash = ResolveClusterIdentity(cluster);
        foreach (AutomaticPlanarCandidate candidate in cluster.Members
                     .OrderBy(static candidate => candidate.StableIdentity))
        {
            hash = Hash64(hash, candidate.ContentRevision);
            hash = Hash64(hash, FloatBits(candidate.WorldPlane.X));
            hash = Hash64(hash, FloatBits(candidate.WorldPlane.Y));
            hash = Hash64(hash, FloatBits(candidate.WorldPlane.Z));
            hash = Hash64(hash, FloatBits(candidate.WorldPlane.W));
        }
        return hash;
    }

    private uint AdvanceGeneration()
    {
        _captureGeneration++;
        if (_captureGeneration == 0u)
            _captureGeneration = 1u;
        return _captureGeneration;
    }

    private static ulong StableGuidHash(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        ulong hash = 1469598103934665603UL;
        foreach (byte item in bytes)
            hash = (hash ^ item) * 1099511628211UL;
        return hash;
    }

    private static ulong Hash64(ulong seed, ulong value)
    {
        seed ^= value + 0x9e3779b97f4a7c15UL +
            (seed << 6) + (seed >> 2);
        seed ^= seed >> 30;
        seed *= 0xbf58476d1ce4e5b9UL;
        seed ^= seed >> 27;
        seed *= 0x94d049bb133111ebUL;
        return seed ^ (seed >> 31);
    }

    private static Vector3 TransformNormal(Vector3 normal, Matrix4x4 world)
    {
        try
        {
            Matrix4x4 inverseTranspose = world.Invert().Transpose();
            return new Vector3(
                normal.X * inverseTranspose.M11 +
                normal.Y * inverseTranspose.M21 +
                normal.Z * inverseTranspose.M31,
                normal.X * inverseTranspose.M12 +
                normal.Y * inverseTranspose.M22 +
                normal.Z * inverseTranspose.M32,
                normal.X * inverseTranspose.M13 +
                normal.Y * inverseTranspose.M23 +
                normal.Z * inverseTranspose.M33);
        }
        catch (InvalidOperationException)
        {
            return Vector3.Zero;
        }
    }

    private static Vector4 TransformHomogeneous(
        Vector3 position,
        Matrix4x4 matrix) => new(
        position.X * matrix.M11 + position.Y * matrix.M21 +
        position.Z * matrix.M31 + matrix.M41,
        position.X * matrix.M12 + position.Y * matrix.M22 +
        position.Z * matrix.M32 + matrix.M42,
        position.X * matrix.M13 + position.Y * matrix.M23 +
        position.Z * matrix.M33 + matrix.M43,
        position.X * matrix.M14 + position.Y * matrix.M24 +
        position.Z * matrix.M34 + matrix.M44);

    private static uint FloatBits(float value) =>
        BitConverter.SingleToUInt32Bits(value);

    private static void WriteVector3(uint* words, int offset, Vector3 value)
    {
        words[offset] = FloatBits(value.X);
        words[offset + 1] = FloatBits(value.Y);
        words[offset + 2] = FloatBits(value.Z);
    }

    private static void WriteVector4(uint* words, int offset, Vector4 value)
    {
        words[offset] = FloatBits(value.X);
        words[offset + 1] = FloatBits(value.Y);
        words[offset + 2] = FloatBits(value.Z);
        words[offset + 3] = FloatBits(value.W);
    }

    private static void WriteMatrix(uint* words, int offset, Matrix4x4 value)
    {
        words[offset + 0] = FloatBits(value.M11);
        words[offset + 1] = FloatBits(value.M12);
        words[offset + 2] = FloatBits(value.M13);
        words[offset + 3] = FloatBits(value.M14);
        words[offset + 4] = FloatBits(value.M21);
        words[offset + 5] = FloatBits(value.M22);
        words[offset + 6] = FloatBits(value.M23);
        words[offset + 7] = FloatBits(value.M24);
        words[offset + 8] = FloatBits(value.M31);
        words[offset + 9] = FloatBits(value.M32);
        words[offset + 10] = FloatBits(value.M33);
        words[offset + 11] = FloatBits(value.M34);
        words[offset + 12] = FloatBits(value.M41);
        words[offset + 13] = FloatBits(value.M42);
        words[offset + 14] = FloatBits(value.M43);
        words[offset + 15] = FloatBits(value.M44);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (AutomaticPlanarCaptureResource resource in _resources)
            resource.Dispose();
        _resources.Clear();
        _bufferManager.DestroyBuffer(_metadataBuffer);
    }
}

public readonly record struct AutomaticPlanarCaptureView(
    int Slot,
    uint Width,
    uint Height,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 ViewProjection,
    Vector3 Position,
    Vector4 ClipPlane)
{
    internal SecondaryViewRegion Region { get; init; }
}

public sealed record AutomaticPlanarPreparedCapture(
    int Slot,
    ulong ClusterIdentity,
    AutomaticPlanarCaptureAction Action,
    AutomaticPlanarCaptureView View,
    int SourceBank,
    int DestinationBank,
    Matrix4x4 SampleViewProjection,
    Matrix4x4 PreviousViewProjection,
    Matrix4x4 CurrentInverseViewProjection,
    Matrix4x4 PreviousInverseViewProjection,
    Vector4 WorldPlane,
    Vector3 WorldOrigin,
    Vector3 WorldTangent,
    Vector3 WorldBitangent,
    Vector2 BoundsMinimum,
    Vector2 BoundsMaximum,
    float WorldDiagonal,
    float Confidence,
    uint AgeFrames,
    uint CaptureGeneration,
    uint[] ReceiverIdentities,
    uint[] ExcludedObjectIndices,
    AutomaticPlanarCaptureResource Resource);

internal readonly record struct AutomaticPlanarSlotState(
    bool Valid,
    ulong ClusterIdentity,
    ulong ContentSignature,
    uint CaptureGeneration,
    uint AgeFrames,
    float Confidence,
    int PublishedBank,
    Matrix4x4 PublishedViewProjection)
{
    internal SecondaryViewRegion CaptureRegion { get; init; }
    internal Matrix4x4 CapturedViewProjection { get; init; }
}

public sealed class AutomaticPlanarCaptureResource : IDisposable
{
    private readonly BindlessHeap _bindlessHeap;
    private bool _disposed;

    internal AutomaticPlanarCaptureResource(
        VulkanContext context,
        BindlessHeap bindlessHeap,
        Format depthFormat,
        int slot,
        uint screenWidth,
        uint screenHeight,
        float linearScale)
    {
        _bindlessHeap = bindlessHeap;
        Slot = slot;
        SourceWidth = screenWidth;
        SourceHeight = screenHeight;
        LinearScale = linearScale;
        Width = Math.Max(1u, checked((uint)MathF.Ceiling(
            screenWidth * linearScale)));
        Height = Math.Max(1u, checked((uint)MathF.Ceiling(
            screenHeight * linearScale)));
        int mipCount = 1 + checked((int)MathF.Floor(MathF.Log2(
            Math.Max(Width, Height))));
        ColorBanks = new RenderTarget[2][];
        TextureIndexBanks = new int[2][];
        for (int bank = 0; bank < 2; bank++)
        {
            ColorBanks[bank] = [];
            TextureIndexBanks[bank] = [];
        }
        try
        {
            for (int bank = 0; bank < 2; bank++)
            {
                ColorBanks[bank] = new RenderTarget[mipCount];
                TextureIndexBanks[bank] = new int[mipCount];
                Array.Fill(TextureIndexBanks[bank], -1);
                uint width = Width;
                uint height = Height;
                for (int mip = 0; mip < mipCount; mip++)
                {
                    ColorBanks[bank][mip] = new RenderTarget(
                        context,
                        $"Automatic Planar {slot} Bank {bank} Color Mip {mip}",
                        Format.R16G16B16A16Sfloat,
                        new Extent2D { Width = width, Height = height },
                        mip == 0
                            ? AutomaticPlanarReflectionManager.BaseColorDescriptor
                            : AutomaticPlanarReflectionManager.MipColorDescriptor);
                    width = Math.Max(1u, width / 2u);
                    height = Math.Max(1u, height / 2u);
                }
            }
            Depth = new RenderTarget(
                context,
                $"Automatic Planar {slot} Depth",
                depthFormat,
                new Extent2D { Width = Width, Height = Height },
                AutomaticPlanarReflectionManager.DepthDescriptor);
            DepthHistory = new RenderTarget[2];
            for (int bank = 0; bank < 2; bank++)
            {
                DepthHistory[bank] = new RenderTarget(
                    context,
                    $"Automatic Planar {slot} Bank {bank} Reverse-Z History",
                    Format.R32Uint,
                    new Extent2D { Width = Width, Height = Height },
                    AutomaticPlanarReflectionManager.DepthHistoryDescriptor);
            }
            AllocationBytes = checked(
                ColorBanks.SelectMany(static bank => bank).Aggregate(
                    Depth.AllocationByteSize +
                    DepthHistory.Aggregate(
                        0UL,
                        static (sum, target) =>
                            sum + target.AllocationByteSize),
                    static (sum, target) => sum + target.AllocationByteSize));
            for (int bank = 0; bank < 2; bank++)
            {
                for (int mip = 0; mip < ColorBanks[bank].Length; mip++)
                {
                    TextureIndexBanks[bank][mip] =
                        _bindlessHeap.AllocateTextureIndex(
                            ColorBanks[bank][mip].View,
                            _bindlessHeap.ScreenSampler);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Slot { get; }
    public uint SourceWidth { get; }
    public uint SourceHeight { get; }
    public uint Width { get; }
    public uint Height { get; }
    public float LinearScale { get; }
    public RenderTarget[][] ColorBanks { get; }
    public RenderTarget Depth { get; private set; } = null!;
    public RenderTarget[] DepthHistory { get; private set; } = [];
    public int[][] TextureIndexBanks { get; }
    public int MipCount => ColorBanks[0].Length;
    public ulong AllocationBytes { get; private set; }

    public RenderTarget[] GetColorMips(int bank) =>
        ColorBanks[ValidateBank(bank)];

    public int[] GetTextureIndices(int bank) =>
        TextureIndexBanks[ValidateBank(bank)];

    public RenderTarget GetDepthHistory(int bank) =>
        DepthHistory[ValidateBank(bank)];

    public bool MatchesExtent(uint width, uint height) =>
        SourceWidth == width && SourceHeight == height;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int bank = 0; bank < TextureIndexBanks.Length; bank++)
        {
            for (int index = 0;
                 index < TextureIndexBanks[bank].Length;
                 index++)
            {
                if (TextureIndexBanks[bank][index] >=
                    BindlessIndex.FirstDynamicTextureIndex)
                {
                    _bindlessHeap.FreeTextureIndex(
                        TextureIndexBanks[bank][index]);
                    TextureIndexBanks[bank][index] = -1;
                }
            }
        }
        foreach (RenderTarget? target in ColorBanks.SelectMany(
                     static bank => bank))
            target?.Dispose();
        foreach (RenderTarget? target in DepthHistory)
            target?.Dispose();
        Depth?.Dispose();
        AllocationBytes = 0UL;
    }

    private static int ValidateBank(int bank)
    {
        if ((uint)bank >= 2u)
            throw new ArgumentOutOfRangeException(nameof(bank));
        return bank;
    }
}

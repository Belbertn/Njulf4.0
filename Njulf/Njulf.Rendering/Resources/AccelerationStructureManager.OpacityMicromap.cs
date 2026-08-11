using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

public sealed unsafe partial class AccelerationStructureManager
{
    private const int MaximumOpacityMicromapBuildStartsPerFrame = 1;
    private const ulong OpacityMicromapScratchAddressAlignment = 256UL;
    private const ulong MaximumOpacityMicromapTransientBytes =
        128UL * 1024UL * 1024UL;
    private const ulong InitialOpacityMicromapRetryDelayFrames = 60UL;
    private const ulong MaximumOpacityMicromapRetryDelayFrames = 4_096UL;
    private const int MaximumOpacityMicromapVariantsPerGeometry = 4;
    private const int MaximumOpacityMicromapVariantsGlobally = 64;

    private readonly Dictionary<MeshHandle,
        OpacityMicromapRuntimeMeshRegistration>
        _synchronizedOpacityMicromapRegistrationsByMesh = new();
    private readonly Dictionary<StaticBlasVariantKey, OpacityMicromapGpuVariant>
        _opacityMicromapGpuVariants = new();
    private readonly Dictionary<StaticBlasVariantKey, OpacityMicromapGpuRetryState>
        _opacityMicromapRetryStates = new();
    private readonly QueryPool[] _opacityMicromapCompactionQueryPools =
        new QueryPool[RenderingConstants.FramesInFlight];
    private readonly QueryPool[] _opacityMicromapBlasCompactionQueryPools =
        new QueryPool[RenderingConstants.FramesInFlight];
    private readonly List<RetiredOpacityMicromapResource>
        _retiredOpacityMicromapResources = new();
    private readonly List<OpacityMicromapVariantRetentionCandidate>
        _opacityMicromapRetentionCandidates =
            new(MaximumOpacityMicromapVariantsGlobally);
    private readonly List<StaticBlasVariantKey>
        _opacityMicromapVariantKeyScratch =
            new(MaximumOpacityMicromapVariantsGlobally);
    private readonly HashSet<BufferHandle>
        _opacityMicromapMemoryCountedBuffers = new();
    private readonly OpacityMicromapExtBuildPolicy
        _opacityMicromapBuildPolicy =
            OpacityMicromapExtBuildPolicy.Default;

    private bool _opacityMicromapGpuRuntimeRequested;
    private bool _opacityMicromapGpuRuntimeEnabled;
    private ulong _opacityMicromapAllocatedBytes;
    private ulong _opacityMicromapPeakAllocatedBytes;
    private ulong _opacityMicromapBuildCount;
    private ulong _opacityMicromapPublicationCount;
    private ulong _opacityMicromapFallbackCount;
    private ulong _opacityMicromapCompactionCount;
    private ulong _opacityMicromapBlasCompactionCount;
    private ulong _opacityMicromapQueryFailureCount;
    private ulong _opacityMicromapVariantCacheHitCount;
    private ulong _opacityMicromapVariantCacheMissCount;
    private ulong _opacityMicromapVariantEvictionCount;
    private ulong _opacityMicromapVariantCapFallbackCount;
    private long _opacityMicromapLastCpuRecordMicroseconds;
    private long _opacityMicromapPeakCpuRecordMicroseconds;
    private ulong _opacityMicromapResidentPeakBytes;
    private ulong _opacityMicromapBuildScratchPeakBytes;
    private ulong _opacityMicromapCompactionHeadroomPeakBytes;
    private OpacityMicromapContentDiagnostics
        _opacityMicromapContentDiagnostics =
            OpacityMicromapContentDiagnostics.Unavailable;
    private string _opacityMicromapGpuRuntimeDetail =
        "opacity-micromap-runtime-not-requested";

    public OpacityMicromapGpuRuntimeSnapshot
        OpacityMicromapGpuRuntimeSnapshot
    {
        get
        {
            int pendingVariantCount = 0;
            int publishedVariantCount = 0;
            foreach (OpacityMicromapGpuVariant variant in
                     _opacityMicromapGpuVariants.Values)
            {
                if (variant.Stage == OpacityMicromapGpuVariantStage.Published)
                    publishedVariantCount++;
                else
                    pendingVariantCount++;
            }

            return new OpacityMicromapGpuRuntimeSnapshot(
                Requested: _opacityMicromapGpuRuntimeRequested,
                Supported: IsOpacityMicromapGpuRuntimeSupported(),
                Enabled: _opacityMicromapGpuRuntimeEnabled,
                RegisteredCandidateCount:
                    _synchronizedOpacityMicromapRegistrationsByMesh.Count,
                PendingVariantCount: pendingVariantCount,
                PublishedVariantCount: publishedVariantCount,
                DeferredRetryCount: _opacityMicromapRetryStates.Count,
                AllocatedBytes: _opacityMicromapAllocatedBytes,
                PeakAllocatedBytes: _opacityMicromapPeakAllocatedBytes,
                RetiredButLiveBytes:
                    CalculateRetiredOpacityMicromapBytes(),
                BuildCount: _opacityMicromapBuildCount,
                PublicationCount: _opacityMicromapPublicationCount,
                FallbackCount: _opacityMicromapFallbackCount,
                MicromapCompactionCount: _opacityMicromapCompactionCount,
                BlasCompactionCount: _opacityMicromapBlasCompactionCount,
                QueryFailureCount: _opacityMicromapQueryFailureCount,
                Detail: _opacityMicromapGpuRuntimeDetail)
            {
                LastCpuRecordMicroseconds =
                    _opacityMicromapLastCpuRecordMicroseconds,
                PeakCpuRecordMicroseconds =
                    _opacityMicromapPeakCpuRecordMicroseconds,
                VariantCacheHitCount = _opacityMicromapVariantCacheHitCount,
                VariantCacheMissCount = _opacityMicromapVariantCacheMissCount,
                VariantEvictionCount = _opacityMicromapVariantEvictionCount,
                VariantCapFallbackCount =
                    _opacityMicromapVariantCapFallbackCount,
                Content = _opacityMicromapContentDiagnostics,
                Memory = CreateOpacityMicromapCentralMemoryPlan()
            };
        }
    }

    private void InitializeOpacityMicromapGpuRuntime(bool requested)
    {
        _context.MarkOpacityMicromapBlasAttachmentIntegrated();
        _opacityMicromapGpuRuntimeRequested = requested;
        _opacityMicromapGpuRuntimeEnabled =
            requested && IsOpacityMicromapGpuRuntimeSupported();
        _opacityMicromapGpuRuntimeDetail = !requested
            ? "opacity-micromap-runtime-not-requested"
            : _opacityMicromapGpuRuntimeEnabled
                ? "opacity-micromap-runtime-ready"
                : ResolveOpacityMicromapGpuRuntimeUnsupportedDetail();
    }

    private bool IsOpacityMicromapGpuRuntimeSupported()
    {
        OpacityMicromapRuntimeCapabilities capabilities =
            _context.OpacityMicromapExtCapability.Capabilities;
        return Supported &&
            _context.OpacityMicromapExtCommandApi is not null &&
            capabilities.SupportsExtFourState &&
            capabilities.CommandBufferBuildAvailable &&
            capabilities.MaximumFourStateSubdivisionLevel != 0U;
    }

    private string ResolveOpacityMicromapGpuRuntimeUnsupportedDetail()
    {
        if (!Supported)
            return "opacity-micromap-runtime-ray-query-backend-unavailable";
        if (_context.OpacityMicromapExtCommandApi is null)
            return string.IsNullOrWhiteSpace(
                _context.OpacityMicromapExtCapability.Detail)
                    ? "opacity-micromap-runtime-native-dispatch-unavailable"
                    : _context.OpacityMicromapExtCapability.Detail;
        return "opacity-micromap-runtime-four-state-capability-incomplete";
    }

    private bool HasPendingOpacityMicromapGpuWork =>
        _opacityMicromapGpuRuntimeEnabled &&
        _opacityMicromapGpuVariants.Values.Any(
            static variant =>
                variant.Stage != OpacityMicromapGpuVariantStage.Published);

    private bool MayRecordOpacityMicromapGpuWork(
        IReadOnlyList<StaticOpaqueInstance> instances)
    {
        if (!_opacityMicromapGpuRuntimeEnabled)
            return false;
        if (HasPendingOpacityMicromapGpuWork)
            return true;
        foreach (StaticOpaqueInstance instance in instances)
        {
            if (!instance.UsesDynamicBlas &&
                _synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                    instance.Mesh,
                    out OpacityMicromapRuntimeMeshRegistration registration) &&
                !_opacityMicromapGpuVariants.ContainsKey(
                    registration.CreateVariantKey()) &&
                CanAttemptOpacityMicromapBuild(registration))
            {
                return true;
            }
        }
        return false;
    }

    private void ReconcileOpacityMicromapGpuRegistrations()
    {
        if (!_opacityMicromapGpuRuntimeEnabled)
        {
            CancelAllOpacityMicromapGpuVariants(
                "opacity-micromap-runtime-disabled");
            _opacityMicromapRetryStates.Clear();
            return;
        }

        _opacityMicromapVariantKeyScratch.Clear();
        foreach (StaticBlasVariantKey key in _opacityMicromapRetryStates.Keys)
            _opacityMicromapVariantKeyScratch.Add(key);
        foreach (StaticBlasVariantKey key in _opacityMicromapVariantKeyScratch)
        {
            if (!_opacityMicromapRetryStates.TryGetValue(
                    key,
                    out OpacityMicromapGpuRetryState retry))
            {
                continue;
            }
            if (!HasRegistrationForRetry(key, retry))
            {
                _opacityMicromapRetryStates.Remove(key);
            }
        }

        _opacityMicromapVariantKeyScratch.Clear();
        foreach (StaticBlasVariantKey key in _opacityMicromapGpuVariants.Keys)
            _opacityMicromapVariantKeyScratch.Add(key);
        foreach (StaticBlasVariantKey key in _opacityMicromapVariantKeyScratch)
        {
            if (!_opacityMicromapGpuVariants.TryGetValue(
                    key,
                    out OpacityMicromapGpuVariant? variant))
            {
                continue;
            }
            if (HasRegistrationForVariant(key, variant))
            {
                continue;
            }

            if (variant.IsWaitingForGpu)
            {
                variant.Cancelled = true;
                variant.Detail =
                    "opacity-micromap-registration-invalidated-in-flight";
            }
            else
            {
                RemoveOpacityMicromapGpuVariant(
                    key,
                    variant,
                    deferPublishedResources: true);
            }
        }
    }

    private bool HasRegistrationForVariant(
        in StaticBlasVariantKey key,
        OpacityMicromapGpuVariant variant)
    {
        // A pending build still references the original mesh buffers. Keep
        // the stronger owner identity until its fence completes. A published
        // BLAS no longer reads build inputs and may be shared by any rigid
        // registration with the same immutable variant key.
        if (variant.IsWaitingForGpu)
        {
            return _synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                    variant.Registration.Mesh,
                    out OpacityMicromapRuntimeMeshRegistration registration) &&
                RegistrationsHaveSameBuildIdentity(
                    registration,
                    variant.Registration);
        }

        foreach (OpacityMicromapRuntimeMeshRegistration registration in
                 _synchronizedOpacityMicromapRegistrationsByMesh.Values)
        {
            if (registration.CreateVariantKey() == key)
                return true;
        }
        return false;
    }

    private bool HasRegistrationForRetry(
        in StaticBlasVariantKey key,
        in OpacityMicromapGpuRetryState retry)
    {
        foreach (OpacityMicromapRuntimeMeshRegistration registration in
                 _synchronizedOpacityMicromapRegistrationsByMesh.Values)
        {
            if (
                registration.CreateVariantKey() == key &&
                RegistrationsHaveSameVariantIdentity(
                    registration,
                    retry.Registration))
            {
                return true;
            }
        }
        return false;
    }

    private void ResolveCompletedOpacityMicromapGpuWork(int frameIndex)
    {
        if (_opacityMicromapGpuVariants.Count == 0)
            return;

        _opacityMicromapVariantKeyScratch.Clear();
        foreach (StaticBlasVariantKey key in _opacityMicromapGpuVariants.Keys)
            _opacityMicromapVariantKeyScratch.Add(key);
        foreach (StaticBlasVariantKey key in _opacityMicromapVariantKeyScratch)
        {
            if (!_opacityMicromapGpuVariants.TryGetValue(
                    key,
                    out OpacityMicromapGpuVariant? variant))
            {
                continue;
            }
            if (!variant.IsWaitingForGpu ||
                variant.CompletionFrameIndex != frameIndex)
            {
                continue;
            }

            switch (variant.Stage)
            {
                case OpacityMicromapGpuVariantStage.WaitingForMicromapBuild:
                    ResolveCompletedMicromapBuild(variant, frameIndex);
                    break;
                case OpacityMicromapGpuVariantStage.WaitingForBlasBuild:
                    ResolveCompletedOpacityBlasBuild(variant, frameIndex);
                    break;
                case OpacityMicromapGpuVariantStage.WaitingForBlasCompaction:
                    ResolveCompletedOpacityBlasCompaction(variant);
                    break;
                default:
                    throw new InvalidOperationException(
                        "An OMM variant carries a completion frame in a non-waiting stage.");
            }

            if (variant.Cancelled && !variant.IsWaitingForGpu)
            {
                RecordOpacityMicromapBuildFailure(
                    variant.Registration,
                    variant.Detail);
                RemoveOpacityMicromapGpuVariant(
                    key,
                    variant,
                    deferPublishedResources: false);
            }
        }
    }

    private void ResolveCompletedMicromapBuild(
        OpacityMicromapGpuVariant variant,
        int frameIndex)
    {
        variant.CompletionFrameIndex = -1;
        DestroyVariantBuffer(ref variant.BuildScratch);
        DestroyVariantBuffer(ref variant.OmmData);
        DestroyVariantBuffer(ref variant.TriangleArray);

        ulong compactedBytes = 0UL;
        if (variant.MicromapCompactionQueryRecorded &&
            !TryReadSingleQuery(
                _opacityMicromapCompactionQueryPools[frameIndex],
                out compactedBytes))
        {
            _opacityMicromapQueryFailureCount++;
            compactedBytes = 0UL;
        }

        variant.CompactedMicromapBytes = compactedBytes;
        variant.CompactMicromap =
            compactedBytes != 0UL &&
            VulkanExtOpacityMicromapNativeCommandRecorder.ShouldCompact(
                _opacityMicromapBuildPolicy,
                variant.BuildSizes,
                compactedBytes,
                out _);
        variant.Stage =
            OpacityMicromapGpuVariantStage.AwaitingFinalBlasBuild;
        variant.Detail = variant.CompactMicromap
            ? "opacity-micromap-awaiting-compaction-and-blas-build"
            : "opacity-micromap-awaiting-blas-build";
    }

    private void ResolveCompletedOpacityBlasBuild(
        OpacityMicromapGpuVariant variant,
        int frameIndex)
    {
        variant.CompletionFrameIndex = -1;
        DestroyVariantBuffer(ref variant.PerPrimitiveIndex);
        DestroySupersededMicromapAfterFinalBlasBuild(variant);

        ulong compactedBytes = 0UL;
        if (variant.BlasCompactionQueryRecorded &&
            !TryReadSingleQuery(
                _opacityMicromapBlasCompactionQueryPools[frameIndex],
                out compactedBytes))
        {
            _opacityMicromapQueryFailureCount++;
            compactedBytes = 0UL;
        }

        if (variant.CandidateBlas is not null &&
            ShouldCompactBottomLevelAccelerationStructure(
                variant.CandidateBlas.Size,
                compactedBytes))
        {
            variant.CompactedBlasBytes = Math.Max(
                MinResourceBufferSize,
                compactedBytes);
            variant.Stage =
                OpacityMicromapGpuVariantStage.AwaitingBlasCompaction;
            variant.Detail =
                "opacity-micromap-awaiting-blas-compaction";
            return;
        }

        PublishOpacityMicromapGpuVariant(variant);
    }

    private void ResolveCompletedOpacityBlasCompaction(
        OpacityMicromapGpuVariant variant)
    {
        variant.CompletionFrameIndex = -1;
        BottomLevelAccelerationStructure? source = variant.CandidateBlas;
        BottomLevelAccelerationStructure? compacted =
            variant.CompactedCandidateBlas;
        if (source is null || compacted is null)
        {
            variant.Cancelled = true;
            variant.Detail =
                "opacity-micromap-blas-compaction-completion-invalid";
            return;
        }

        DestroyAccelerationStructureResource(
            source.Handle,
            source.StorageBuffer);
        RemoveTrackedOpacityMicromapBytes(source.Size);
        variant.CandidateBlas = compacted;
        variant.CompactedCandidateBlas = null;
        _opacityMicromapBlasCompactionCount++;
        PublishOpacityMicromapGpuVariant(variant);
    }

    private void RecordOpacityMicromapGpuWork(
        IReadOnlyList<StaticOpaqueInstance> instances,
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RecordOpacityMicromapGpuWorkCore(
                instances,
                stagingRing,
                commandBuffer,
                frameIndex);
        }
        finally
        {
            long elapsed = (long)(
                (System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                1_000_000.0 /
                System.Diagnostics.Stopwatch.Frequency);
            _opacityMicromapLastCpuRecordMicroseconds = elapsed;
            _opacityMicromapPeakCpuRecordMicroseconds = Math.Max(
                _opacityMicromapPeakCpuRecordMicroseconds,
                elapsed);
            UpdateOpacityMicromapCategoryPeaks();
        }
    }

    private void RecordOpacityMicromapGpuWorkCore(
        IReadOnlyList<StaticOpaqueInstance> instances,
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (!_opacityMicromapGpuRuntimeEnabled ||
            _context.OpacityMicromapExtCommandApi is null)
        {
            return;
        }

        int recordedTransitions = 0;
        foreach (OpacityMicromapGpuVariant variant in
                 _opacityMicromapGpuVariants.Values)
        {
            if (recordedTransitions >=
                MaximumOpacityMicromapBuildStartsPerFrame)
            {
                break;
            }

            bool recorded = variant.Stage switch
            {
                OpacityMicromapGpuVariantStage.AwaitingFinalBlasBuild =>
                    TryRecordFinalOpacityMicromapAndBlasBuild(
                        variant,
                        commandBuffer,
                        frameIndex),
                OpacityMicromapGpuVariantStage.AwaitingBlasCompaction =>
                    TryRecordOpacityBlasCompaction(
                        variant,
                        commandBuffer,
                        frameIndex),
                _ => false
            };
            if (recorded)
                recordedTransitions++;
        }

        if (recordedTransitions >=
            MaximumOpacityMicromapBuildStartsPerFrame)
        {
            return;
        }

        foreach (StaticOpaqueInstance instance in instances)
        {
            if (recordedTransitions >=
                MaximumOpacityMicromapBuildStartsPerFrame)
            {
                break;
            }
            if (instance.UsesDynamicBlas ||
                !_blasCache.ContainsKey(instance.Mesh) ||
                !_synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                    instance.Mesh,
                    out OpacityMicromapRuntimeMeshRegistration registration) ||
                !InstanceUsesOpacityMicromapRegistration(
                    instance,
                    registration) ||
                !CanAttemptOpacityMicromapBuild(registration))
            {
                continue;
            }

            StaticBlasVariantKey variantKey = registration.CreateVariantKey();
            if (_opacityMicromapGpuVariants.ContainsKey(variantKey))
                continue;
            if (!TryAdmitOpacityMicromapVariant(
                    variantKey,
                    out string admissionDetail))
            {
                RecordOpacityMicromapBuildFailure(
                    registration,
                    admissionDetail);
                _opacityMicromapFallbackCount++;
                _opacityMicromapGpuRuntimeDetail = admissionDetail;
                continue;
            }

            if (TryCreateAndRecordOpacityMicromapBuild(
                    registration,
                    stagingRing,
                    commandBuffer,
                    frameIndex,
                    out OpacityMicromapGpuVariant? variant,
                    out string detail))
            {
                _opacityMicromapGpuVariants.Add(
                    variantKey,
                    variant!);
                _opacityMicromapRetryStates.Remove(variantKey);
                _opacityMicromapBuildCount++;
                _opacityMicromapGpuRuntimeDetail = detail;
                recordedTransitions++;
            }
            else
            {
                RecordOpacityMicromapBuildFailure(registration, detail);
                _opacityMicromapFallbackCount++;
                _opacityMicromapGpuRuntimeDetail = detail;
            }
        }
    }

    private bool TryCreateAndRecordOpacityMicromapBuild(
        in OpacityMicromapRuntimeMeshRegistration registration,
        StagingRing stagingRing,
        CommandBuffer commandBuffer,
        int frameIndex,
        out OpacityMicromapGpuVariant? variant,
        out string detail)
    {
        variant = null;
        OpacityMicromapCookedPayload payload = registration.Payload;
        uint maximumDeviceSubdivision = _context
            .OpacityMicromapExtCapability
            .Capabilities
            .MaximumFourStateSubdivisionLevel;
        if (payload.MaximumSubdivisionLevel > maximumDeviceSubdivision)
        {
            detail = "opacity-micromap-subdivision-exceeds-device-limit";
            return false;
        }
        OpacityMicromapExtNativeInputLayout layout =
            OpacityMicromapExtNativeInputLayout.PackedUint32;
        SilkNetExtOpacityMicromapCommandApi? api =
            _context.OpacityMicromapExtCommandApi;
        if (!VulkanExtOpacityMicromapNativeCommandRecorder.TryQueryBuildSizes(
                api,
                _context.Device,
                payload,
                layout,
                _opacityMicromapBuildPolicy,
                out OpacityMicromapExtNativeBuildSizes buildSizes,
                out detail))
        {
            return false;
        }

        ulong initialBytes;
        try
        {
            initialBytes = checked(
                GetOpacityMicromapAllocationSize(
                    (ulong)payload.OmmData.Length) +
                GetOpacityMicromapAllocationSize(
                    (ulong)payload.DescriptorData.Length) +
                GetOpacityMicromapAllocationSize(
                    (ulong)payload.IndexData.Length) +
                GetOpacityMicromapAllocationSize(
                    buildSizes.MicromapStorageBytes) +
                (buildSizes.BuildScratchBytes == 0UL
                    ? 0UL
                    : GetOpacityMicromapAllocationSize(
                        buildSizes.BuildScratchBytes)));
        }
        catch (OverflowException)
        {
            detail = "opacity-micromap-build-allocation-size-overflow";
            return false;
        }
        if (!CanReserveOpacityMicromapBytes(initialBytes))
        {
            detail = "opacity-micromap-build-transient-budget-exceeded";
            return false;
        }

        var created = new OpacityMicromapGpuVariant(registration, buildSizes);
        try
        {
            created.OmmData = CreateOpacityMicromapDeviceBuffer(
                (ulong)payload.OmmData.Length,
                BufferUsageFlags.MicromapBuildInputReadOnlyBitExt |
                    BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                $"OMM Data Mesh {registration.Mesh.Index}");
            created.TriangleArray = CreateOpacityMicromapDeviceBuffer(
                (ulong)payload.DescriptorData.Length,
                BufferUsageFlags.MicromapBuildInputReadOnlyBitExt |
                    BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                $"OMM Triangles Mesh {registration.Mesh.Index}");
            created.PerPrimitiveIndex = CreateOpacityMicromapDeviceBuffer(
                (ulong)payload.IndexData.Length,
                BufferUsageFlags
                    .AccelerationStructureBuildInputReadOnlyBitKhr |
                    BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                $"OMM Primitive Indices Mesh {registration.Mesh.Index}");
            created.SourceMicromapStorage =
                CreateOpacityMicromapDeviceBuffer(
                    buildSizes.MicromapStorageBytes,
                    BufferUsageFlags.MicromapStorageBitExt,
                    requireDeviceAddress: false,
                    $"OMM Storage Mesh {registration.Mesh.Index}");
            if (buildSizes.BuildScratchBytes != 0UL)
            {
                created.BuildScratch = CreateOpacityMicromapDeviceBuffer(
                    buildSizes.BuildScratchBytes,
                    BufferUsageFlags.StorageBufferBit |
                        BufferUsageFlags.ShaderDeviceAddressBit,
                    requireDeviceAddress: true,
                    $"OMM Scratch Mesh {registration.Mesh.Index}");
            }

            OpacityMicromapExtDeviceBufferBinding storage =
                CreateOpacityMicromapBinding(
                    created.SourceMicromapStorage,
                    requireDeviceAddress: false);
            if (!VulkanExtOpacityMicromapNativeCommandRecorder.TryCreateMicromap(
                    api,
                    _context.Device,
                    _bufferManager,
                    storage,
                    buildSizes,
                    out created.SourceMicromap,
                    out detail))
            {
                DestroyOpacityMicromapVariantImmediately(created);
                return false;
            }

            OpacityMicromapExtNativeBuildInputs inputs =
                CreateNativeBuildInputs(created);
            // From the first upload attempt onward a command may reference the
            // allocations. Even a fail-closed result must retain them until
            // this frame slot's fence is observed.
            created.Stage =
                OpacityMicromapGpuVariantStage.WaitingForMicromapBuild;
            created.CompletionFrameIndex = frameIndex;
            if (!VulkanExtOpacityMicromapNativeCommandRecorder.TryUploadInputs(
                    _context,
                    _bufferManager,
                    stagingRing,
                    commandBuffer,
                    payload,
                    layout,
                    inputs,
                    out detail) ||
                !VulkanExtOpacityMicromapNativeCommandRecorder.TryRecordBuild(
                    api,
                    _bufferManager,
                    commandBuffer,
                    payload,
                    layout,
                    buildSizes,
                    inputs,
                    OpacityMicromapScratchAddressAlignment,
                    out detail))
            {
                // Upload commands may already reference the allocations. Keep
                // the generation until this frame slot completes.
                created.Cancelled = true;
                created.Detail = detail;
                variant = created;
                return true;
            }

            if (buildSizes.CompactionAllowed &&
                TryResetOpacityMicromapQueryPool(
                    _opacityMicromapCompactionQueryPools,
                    QueryType.MicromapCompactedSizeExt,
                    frameIndex,
                    commandBuffer,
                    "OMM Compacted Size"))
            {
                VulkanExtOpacityMicromapNativeCommandRecorder
                    .RecordCompactedSizeQuery(
                        _context,
                        api!,
                        commandBuffer,
                        created.SourceMicromap,
                        _opacityMicromapCompactionQueryPools[frameIndex],
                        buildSizes,
                        0U);
                created.MicromapCompactionQueryRecorded = true;
            }
            else
            {
                VulkanExtOpacityMicromapNativeCommandRecorder
                    .RecordMicromapBuildToBlasBarrier(
                        _context,
                        commandBuffer);
            }

            created.Stage =
                OpacityMicromapGpuVariantStage.WaitingForMicromapBuild;
            created.CompletionFrameIndex = frameIndex;
            created.Detail = "opacity-micromap-build-recorded";
            variant = created;
            detail = created.Detail;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException or
                                           VulkanException)
        {
            // If no command references were recorded, immediate cleanup is
            // safe. Otherwise retain until the slot fence and let the normal
            // cancellation path retire the generation.
            if (created.Stage ==
                OpacityMicromapGpuVariantStage.AwaitingMicromapBuild)
            {
                DestroyOpacityMicromapVariantImmediately(created);
                detail = "opacity-micromap-build-setup-failed-" +
                    exception.GetType().Name;
                return false;
            }

            created.Cancelled = true;
            created.CompletionFrameIndex = frameIndex;
            created.Detail = "opacity-micromap-build-record-failed-" +
                exception.GetType().Name;
            variant = created;
            detail = created.Detail;
            return true;
        }
    }

    private bool TryRecordFinalOpacityMicromapAndBlasBuild(
        OpacityMicromapGpuVariant variant,
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (variant.Cancelled)
            return false;

        variant.Stage =
            OpacityMicromapGpuVariantStage.WaitingForBlasBuild;
        variant.CompletionFrameIndex = frameIndex;
        try
        {
            if (variant.CompactMicromap)
            {
                if (!TryCreateAndRecordCompactedMicromap(
                        variant,
                        commandBuffer,
                        out string compactionDetail))
                {
                    // Compaction is optional. The helper returns false only
                    // before recording a copy, so the completed source object
                    // remains a valid attachment and is the safe fallback.
                    variant.CompactMicromap = false;
                    variant.FinalMicromap = variant.SourceMicromap;
                    variant.FinalMicromapStorage =
                        variant.SourceMicromapStorage;
                    variant.Detail = compactionDetail;
                }
                else
                {
                    _opacityMicromapCompactionCount++;
                }
            }
            else
            {
                variant.FinalMicromap = variant.SourceMicromap;
                variant.FinalMicromapStorage =
                    variant.SourceMicromapStorage;
            }

            VulkanExtOpacityMicromapNativeCommandRecorder
                .RecordMicromapBuildToBlasBarrier(
                    _context,
                    commandBuffer);
            if (!TryBuildOpacityMicromapAttachedBlas(
                    variant,
                    commandBuffer,
                    out string buildDetail))
            {
                variant.Cancelled = true;
                variant.Detail = buildDetail;
                return true;
            }

            InsertAccelerationStructureBuildBarrier(commandBuffer);
            if (TryResetOpacityMicromapQueryPool(
                    _opacityMicromapBlasCompactionQueryPools,
                    QueryType.AccelerationStructureCompactedSizeKhr,
                    frameIndex,
                    commandBuffer,
                    "OMM BLAS Compacted Size"))
            {
                AccelerationStructureKHR handle =
                    variant.CandidateBlas!.Handle;
                _khrAccelerationStructure!
                    .CmdWriteAccelerationStructuresProperties(
                        commandBuffer,
                        1,
                        &handle,
                        QueryType.AccelerationStructureCompactedSizeKhr,
                        _opacityMicromapBlasCompactionQueryPools[frameIndex],
                        0U);
                variant.BlasCompactionQueryRecorded = true;
            }

            variant.Detail = "opacity-micromap-attached-blas-build-recorded";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException or
                                           VulkanException)
        {
            variant.Cancelled = true;
            variant.Detail = "opacity-micromap-attached-blas-build-failed-" +
                exception.GetType().Name;
            return true;
        }
    }

    private bool TryCreateAndRecordCompactedMicromap(
        OpacityMicromapGpuVariant variant,
        CommandBuffer commandBuffer,
        out string detail)
    {
        SilkNetExtOpacityMicromapCommandApi? api =
            _context.OpacityMicromapExtCommandApi;
        if (api is null || variant.CompactedMicromapBytes == 0UL)
        {
            detail = "opacity-micromap-compaction-input-invalid";
            return false;
        }
        ulong compactedAllocationBytes =
            GetOpacityMicromapAllocationSize(
                variant.CompactedMicromapBytes);
        if (!CanReserveOpacityMicromapBytes(
                compactedAllocationBytes))
        {
            detail = "opacity-micromap-compaction-headroom-exceeded";
            return false;
        }

        variant.FinalMicromapStorage = CreateOpacityMicromapDeviceBuffer(
            variant.CompactedMicromapBytes,
            BufferUsageFlags.MicromapStorageBitExt,
            requireDeviceAddress: false,
            $"OMM Storage Mesh {variant.Registration.Mesh.Index} Compacted");
        var compactedSizes = variant.BuildSizes with
        {
            MicromapStorageBytes = variant.CompactedMicromapBytes,
            BuildScratchBytes = 0UL,
            CompactionAllowed = false
        };
        if (!VulkanExtOpacityMicromapNativeCommandRecorder.TryCreateMicromap(
                api,
                _context.Device,
                _bufferManager,
                CreateOpacityMicromapBinding(
                    variant.FinalMicromapStorage,
                    requireDeviceAddress: false),
                compactedSizes,
                out variant.FinalMicromap,
                out detail))
        {
            DestroyVariantBuffer(ref variant.FinalMicromapStorage);
            return false;
        }

        VulkanExtOpacityMicromapNativeCommandRecorder.RecordCompactionCopy(
            _context,
            api,
            commandBuffer,
            variant.SourceMicromap,
            variant.FinalMicromap,
            variant.BuildSizes);
        detail = "opacity-micromap-compaction-copy-recorded";
        return true;
    }

    private bool TryBuildOpacityMicromapAttachedBlas(
        OpacityMicromapGpuVariant variant,
        CommandBuffer commandBuffer,
        out string detail)
    {
        OpacityMicromapCookedPayload payload =
            variant.Registration.Payload;
        StaticBlasVariantKey variantKey = new(
            variant.Registration.MeshGeometryKey,
            variant.Registration.RayGeometryPolicy,
            payload.SourceContentHash,
            variant.Registration.AccelerationStructureBuildAbi);
        if (!OpacityMicromapExtStaticBlasAttachment.TryCreate(
                variantKey,
                variant.FinalMicromap,
                payload,
                OpacityMicromapExtNativeInputLayout.PackedUint32,
                CreateOpacityMicromapBinding(
                    variant.PerPrimitiveIndex,
                    requireDeviceAddress: true),
                out OpacityMicromapExtStaticBlasAttachment? attachment,
                out detail))
        {
            return false;
        }

        MeshInfo meshInfo = _meshManager.GetMeshInfo(
            variant.Registration.Mesh);
        uint primitiveCount = meshInfo.IndexCount / 3U;
        BottomLevelAccelerationStructure? built = null;
        attachment!.RecordWithNativeAttachment(nativeAttachment =>
        {
            AccelerationStructureGeometryKHR geometry =
                CreateBottomLevelGeometry(meshInfo);
            geometry.Geometry.Triangles.PNext = nativeAttachment;
            AccelerationStructureBuildGeometryInfoKHR buildInfo =
                CreateBottomLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes =
                QueryBuildSizes(buildInfo, primitiveCount);
            ulong storageBytes = Math.Max(
                MinResourceBufferSize,
                sizes.AccelerationStructureSize);
            if (!CanReserveOpacityMicromapBytes(storageBytes))
            {
                throw new InvalidOperationException(
                    "opacity-micromap-attached-blas-budget-exceeded");
            }

            EnsureScratchCapacity(sizes.BuildScratchSize);
            BufferHandle storage = CreateOpacityMicromapDeviceBuffer(
                storageBytes,
                BufferUsageFlags.AccelerationStructureStorageBitKhr |
                    BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                $"OMM BLAS Mesh {variant.Registration.Mesh.Index}");
            AccelerationStructureKHR handle = default;
            try
            {
                handle = CreateAccelerationStructure(
                    storage,
                    sizes.AccelerationStructureSize,
                    AccelerationStructureTypeKHR.BottomLevelKhr,
                    $"OMM BLAS Mesh {variant.Registration.Mesh.Index}");
                geometry = CreateBottomLevelGeometry(meshInfo);
                geometry.Geometry.Triangles.PNext = nativeAttachment;
                buildInfo = CreateBottomLevelBuildInfo(
                    &geometry,
                    handle,
                    _scratchBufferDeviceAddress);
                var range = new AccelerationStructureBuildRangeInfoKHR
                {
                    PrimitiveCount = primitiveCount
                };
                AccelerationStructureBuildRangeInfoKHR* rangePointer = &range;
                _khrAccelerationStructure!.CmdBuildAccelerationStructures(
                    commandBuffer,
                    1,
                    &buildInfo,
                    &rangePointer);
                built = new BottomLevelAccelerationStructure(
                    handle,
                    storage,
                    sizes.AccelerationStructureSize);
            }
            catch
            {
                DestroyAccelerationStructureResource(handle, storage);
                RemoveTrackedOpacityMicromapBytes(storageBytes);
                throw;
            }
        });

        if (built is null)
        {
            detail = "opacity-micromap-attached-blas-not-created";
            return false;
        }
        variant.CandidateBlas = built;
        detail = "opacity-micromap-attached-blas-built";
        return true;
    }

    private bool TryRecordOpacityBlasCompaction(
        OpacityMicromapGpuVariant variant,
        CommandBuffer commandBuffer,
        int frameIndex)
    {
        if (variant.Cancelled || variant.CandidateBlas is null ||
            variant.CompactedBlasBytes == 0UL)
        {
            return false;
        }
        ulong compactedAllocationBytes =
            GetOpacityMicromapAllocationSize(variant.CompactedBlasBytes);
        if (!CanReserveOpacityMicromapBytes(compactedAllocationBytes))
        {
            // Compaction is optional. Publish the uncompacted, already
            // completed BLAS instead of turning headroom pressure into a hole.
            PublishOpacityMicromapGpuVariant(variant);
            return false;
        }

        BufferHandle storage = CreateOpacityMicromapDeviceBuffer(
            variant.CompactedBlasBytes,
            BufferUsageFlags.AccelerationStructureStorageBitKhr |
                BufferUsageFlags.ShaderDeviceAddressBit,
            requireDeviceAddress: true,
            $"OMM BLAS Mesh {variant.Registration.Mesh.Index} Compacted");
        AccelerationStructureKHR handle = default;
        variant.Stage =
            OpacityMicromapGpuVariantStage.WaitingForBlasCompaction;
        variant.CompletionFrameIndex = frameIndex;
        try
        {
            handle = CreateAccelerationStructure(
                storage,
                variant.CompactedBlasBytes,
                AccelerationStructureTypeKHR.BottomLevelKhr,
                $"OMM BLAS Mesh {variant.Registration.Mesh.Index} Compacted");
            var copy = new CopyAccelerationStructureInfoKHR
            {
                SType = StructureType.CopyAccelerationStructureInfoKhr,
                Src = variant.CandidateBlas.Handle,
                Dst = handle,
                Mode = CopyAccelerationStructureModeKHR.CompactKhr
            };
            variant.CompactedCandidateBlas =
                new BottomLevelAccelerationStructure(
                    handle,
                    storage,
                    variant.CompactedBlasBytes,
                    variant.CandidateBlas.UncompactedSize);
            _khrAccelerationStructure!.CmdCopyAccelerationStructure(
                commandBuffer,
                &copy);
            InsertAccelerationStructureBuildBarrier(commandBuffer);
            variant.Detail =
                "opacity-micromap-blas-compaction-copy-recorded";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException or
                                           VulkanException)
        {
            if (variant.CompactedCandidateBlas is null)
            {
                DestroyAccelerationStructureResource(handle, storage);
                RemoveTrackedOpacityMicromapBytes(
                    variant.CompactedBlasBytes);
            }
            variant.Cancelled = true;
            variant.Detail =
                "opacity-micromap-blas-compaction-record-failed-" +
                exception.GetType().Name;
            return true;
        }
    }

    private void PublishOpacityMicromapGpuVariant(
        OpacityMicromapGpuVariant variant)
    {
        if (variant.Cancelled || variant.CandidateBlas is null ||
            variant.FinalMicromap.Handle == 0UL ||
            !variant.FinalMicromapStorage.IsValid)
        {
            variant.Cancelled = true;
            variant.Detail =
                "opacity-micromap-publication-incomplete";
            return;
        }

        variant.Stage = OpacityMicromapGpuVariantStage.Published;
        variant.CompletionFrameIndex = -1;
        variant.CandidateBlas.LastUsedFrameSerial = _frameSerial;
        variant.Detail = "opacity-micromap-variant-published";
        _opacityMicromapPublicationCount++;
        _opacityMicromapGpuRuntimeDetail = variant.Detail;
        UpdateOpacityMicromapCategoryPeaks();
        InvalidateTlasForOpacityMicromapVariantChange();
        AdvanceResourceGeneration();
        RecalculateAccelerationStructureBytes();
    }

    private bool TryResolveOpacityMicromapBlas(
        in StaticOpaqueInstance instance,
        out BottomLevelAccelerationStructure? blas)
    {
        if (!_opacityMicromapGpuRuntimeEnabled ||
            !_synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                instance.Mesh,
                out OpacityMicromapRuntimeMeshRegistration registration) ||
            !InstanceUsesOpacityMicromapRegistration(instance, registration))
        {
            blas = null;
            return false;
        }

        if (_opacityMicromapGpuVariants.TryGetValue(
                registration.CreateVariantKey(),
                out OpacityMicromapGpuVariant? variant) &&
            variant.Stage == OpacityMicromapGpuVariantStage.Published &&
            !variant.Cancelled && variant.CandidateBlas is not null)
        {
            variant.CandidateBlas.LastUsedFrameSerial = _frameSerial;
            variant.ReuseCount = variant.ReuseCount == ulong.MaxValue
                ? ulong.MaxValue
                : variant.ReuseCount + 1UL;
            _opacityMicromapVariantCacheHitCount =
                _opacityMicromapVariantCacheHitCount == ulong.MaxValue
                    ? ulong.MaxValue
                    : _opacityMicromapVariantCacheHitCount + 1UL;
            blas = variant.CandidateBlas;
            return true;
        }

        _opacityMicromapVariantCacheMissCount =
            _opacityMicromapVariantCacheMissCount == ulong.MaxValue
                ? ulong.MaxValue
                : _opacityMicromapVariantCacheMissCount + 1UL;
        blas = null;
        return false;
    }

    private static bool InstanceUsesOpacityMicromapRegistration(
        in StaticOpaqueInstance instance,
        in OpacityMicromapRuntimeMeshRegistration registration) =>
        !instance.UsesDynamicBlas &&
        instance.Mesh == registration.Mesh &&
        instance.MaterialIndex == checked((uint)registration.Material.Index) &&
        instance.GeometryClass == DdgiRayGeometryClass.AlphaMask;

    private void TouchActiveOpacityMicromapBottomLevelAccelerationStructures()
    {
        foreach (MeshHandle mesh in _activeMeshScratch)
        {
            if (_synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                    mesh,
                    out OpacityMicromapRuntimeMeshRegistration registration) &&
                _opacityMicromapGpuVariants.TryGetValue(
                    registration.CreateVariantKey(),
                    out OpacityMicromapGpuVariant? variant) &&
                variant.Stage == OpacityMicromapGpuVariantStage.Published &&
                variant.CandidateBlas is not null)
            {
                variant.CandidateBlas.LastUsedFrameSerial = _frameSerial;
            }
        }
    }

    private void InvalidateTlasForOpacityMicromapVariantChange()
    {
        _hasReusablePreparation = false;
        _hasTlasInstanceSignature = false;
        _lastTlasInstanceSignature = 0UL;
        _lastTlasInstanceCount = 0;
        Array.Fill(_tlasHasInstanceSignatures, false);
        Array.Fill(_tlasInstanceSignatures, 0UL);
        Array.Fill(_tlasInstanceCounts, 0);
    }

    private bool TryResetOpacityMicromapQueryPool(
        QueryPool[] pools,
        QueryType queryType,
        int frameIndex,
        CommandBuffer commandBuffer,
        string debugName)
    {
        if (pools[frameIndex].Handle == 0UL)
        {
            var createInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = queryType,
                QueryCount = 1U
            };
            Result result = _context.Api.CreateQueryPool(
                _context.Device,
                &createInfo,
                null,
                out QueryPool pool);
            if (result != Result.Success)
            {
                _opacityMicromapQueryFailureCount++;
                return false;
            }
            pools[frameIndex] = pool;
            _context.SetDebugName(
                pool.Handle,
                ObjectType.QueryPool,
                $"{debugName} Frame {frameIndex}");
        }

        _context.Api.CmdResetQueryPool(
            commandBuffer,
            pools[frameIndex],
            0U,
            1U);
        return true;
    }

    private bool TryReadSingleQuery(QueryPool pool, out ulong value)
    {
        value = 0UL;
        if (pool.Handle == 0UL)
            return false;
        fixed (ulong* valuePointer = &value)
        {
            Result result = _context.Api.GetQueryPoolResults(
                _context.Device,
                pool,
                0U,
                1U,
                (nuint)sizeof(ulong),
                valuePointer,
                sizeof(ulong),
                QueryResultFlags.Result64Bit);
            return result == Result.Success;
        }
    }

    private BufferHandle CreateOpacityMicromapDeviceBuffer(
        ulong bytes,
        BufferUsageFlags usage,
        bool requireDeviceAddress,
        string debugName)
    {
        bytes = GetOpacityMicromapAllocationSize(bytes);
        BufferHandle buffer = _bufferManager.CreateDeviceBuffer(
            bytes,
            usage,
            requireDeviceAddress,
            MemoryBudgetCategory.GlobalIllumination,
            debugName);
        AddTrackedOpacityMicromapBytes(bytes);
        return buffer;
    }

    internal static ulong GetOpacityMicromapAllocationSize(ulong bytes) =>
        Math.Max(MinResourceBufferSize, bytes);

    private OpacityMicromapExtDeviceBufferBinding
        CreateOpacityMicromapBinding(
            BufferHandle buffer,
            bool requireDeviceAddress)
    {
        if (!buffer.IsValid)
            return default;
        return new OpacityMicromapExtDeviceBufferBinding(
            buffer,
            requireDeviceAddress
                ? _bufferManager.GetBufferDeviceAddress(buffer)
                : 0UL,
            _bufferManager.GetBufferSize(buffer));
    }

    private OpacityMicromapExtNativeBuildInputs CreateNativeBuildInputs(
        OpacityMicromapGpuVariant variant) => new(
            CreateOpacityMicromapBinding(
                variant.OmmData,
                requireDeviceAddress: true),
            CreateOpacityMicromapBinding(
                variant.TriangleArray,
                requireDeviceAddress: true),
            CreateOpacityMicromapBinding(
                variant.PerPrimitiveIndex,
                requireDeviceAddress: true),
            CreateOpacityMicromapBinding(
                variant.BuildScratch,
                requireDeviceAddress: true),
            variant.SourceMicromap);

    private bool CanReserveOpacityMicromapBytes(ulong additionalBytes)
    {
        if (additionalBytes == 0UL ||
            additionalBytes > MaximumOpacityMicromapTransientBytes)
        {
            return false;
        }
        if (_opacityMicromapAllocatedBytes >
            MaximumOpacityMicromapTransientBytes - additionalBytes)
        {
            return false;
        }

        MemoryHeapBudgetSnapshot budget =
            _context.GetMemoryHeapBudgetSnapshot();
        if (!budget.IsAvailable)
            return true;
        if (budget.PrimaryBudgetBytes <= budget.PrimaryUsageBytes)
            return false;
        return additionalBytes <=
            budget.PrimaryBudgetBytes - budget.PrimaryUsageBytes;
    }

    private bool CanAttemptOpacityMicromapBuild(
        in OpacityMicromapRuntimeMeshRegistration registration)
    {
        StaticBlasVariantKey key = registration.CreateVariantKey();
        if (!_opacityMicromapRetryStates.TryGetValue(
                key,
                out OpacityMicromapGpuRetryState retry))
        {
            return true;
        }
        if (!RegistrationsHaveSameVariantIdentity(
                registration,
                retry.Registration))
        {
            _opacityMicromapRetryStates.Remove(key);
            return true;
        }
        return _frameSerial >= retry.RetryAfterFrameSerial;
    }

    private bool TryAdmitOpacityMicromapVariant(
        in StaticBlasVariantKey incomingKey,
        out string detail)
    {
        int geometryVariantCount = 0;
        foreach (StaticBlasVariantKey existingKey in
                 _opacityMicromapGpuVariants.Keys)
        {
            if (existingKey.MeshGeometryKey == incomingKey.MeshGeometryKey)
                geometryVariantCount++;
        }

        if (geometryVariantCount >=
            MaximumOpacityMicromapVariantsPerGeometry &&
            !TryEvictOpacityMicromapVariant(
                incomingKey.MeshGeometryKey,
                restrictToGeometry: true))
        {
            _opacityMicromapVariantCapFallbackCount = SaturatingIncrement(
                _opacityMicromapVariantCapFallbackCount);
            detail = "opacity-micromap-per-geometry-variant-cap-reached";
            return false;
        }

        if (_opacityMicromapGpuVariants.Count >=
            MaximumOpacityMicromapVariantsGlobally &&
            !TryEvictOpacityMicromapVariant(
                incomingKey.MeshGeometryKey,
                restrictToGeometry: false))
        {
            _opacityMicromapVariantCapFallbackCount = SaturatingIncrement(
                _opacityMicromapVariantCapFallbackCount);
            detail = "opacity-micromap-global-variant-cap-reached";
            return false;
        }

        detail = "opacity-micromap-variant-cap-admitted";
        return true;
    }

    private bool TryEvictOpacityMicromapVariant(
        in OpacityMicromapContentKey geometryKey,
        bool restrictToGeometry)
    {
        _opacityMicromapRetentionCandidates.Clear();
        foreach ((StaticBlasVariantKey key,
                  OpacityMicromapGpuVariant candidate) in
                 _opacityMicromapGpuVariants)
        {
            _opacityMicromapRetentionCandidates.Add(new(
                key,
                candidate.ReuseCount,
                candidate.CandidateBlas?.LastUsedFrameSerial ?? 0UL,
                IsOpacityMicromapVariantActive(key),
                candidate.Stage == OpacityMicromapGpuVariantStage.Published,
                candidate.CandidateBlas is not null));
        }

        if (!OpacityMicromapVariantRetentionPolicy
                .TrySelectEvictionCandidate(
                    _opacityMicromapRetentionCandidates,
                    geometryKey,
                    restrictToGeometry,
                    out StaticBlasVariantKey selectedKey) ||
            !_opacityMicromapGpuVariants.TryGetValue(
                selectedKey,
                out OpacityMicromapGpuVariant? selected))
        {
            return false;
        }

        RemoveOpacityMicromapGpuVariant(
            selectedKey,
            selected,
            deferPublishedResources: true);
        _opacityMicromapVariantEvictionCount = SaturatingIncrement(
            _opacityMicromapVariantEvictionCount);
        _opacityMicromapGpuRuntimeDetail =
            "opacity-micromap-variant-evicted-under-cap-pressure";
        return true;
    }

    private bool IsOpacityMicromapVariantActive(
        in StaticBlasVariantKey key)
    {
        foreach (MeshHandle mesh in _activeMeshScratch)
        {
            if (_synchronizedOpacityMicromapRegistrationsByMesh.TryGetValue(
                    mesh,
                    out OpacityMicromapRuntimeMeshRegistration registration) &&
                registration.CreateVariantKey() == key)
            {
                return true;
            }
        }
        return false;
    }

    private static ulong SaturatingIncrement(ulong value) =>
        value == ulong.MaxValue ? ulong.MaxValue : value + 1UL;

    private static OpacityMicromapContentDiagnostics
        CreateOpacityMicromapContentDiagnostics(
            int registeredMeshCount,
            IReadOnlyDictionary<OpacityMicromapContentKey,
                OpacityMicromapCookedPayload> payloads,
            ulong rejectedRegistrationCount,
            int staleMaterialRegistrationCount,
            int ambiguousContentKeyCount)
    {
        Span<ulong> subdivisionCounts = stackalloc ulong[
            checked((int)OpacityMicromapSubdivisionPolicy
                .AbsoluteMaximumSubdivisionLevel + 1)];
        ulong primitiveCount = 0UL;
        ulong materialContractCount = 0UL;
        ulong ommDataBytes = 0UL;
        ulong indexBytes = 0UL;
        ulong descriptorBytes = 0UL;
        ulong opaque = 0UL;
        ulong transparent = 0UL;
        ulong unknownOpaque = 0UL;
        ulong unknownTransparent = 0UL;
        int classifiedPayloadCount = 0;
        int unclassifiedPayloadCount = 0;
        uint maximumSubdivisionLevel = 0U;

        foreach (OpacityMicromapCookedPayload payload in payloads.Values)
        {
            primitiveCount = SaturatingAdd(
                primitiveCount,
                payload.PrimitiveCount);
            materialContractCount = SaturatingAdd(
                materialContractCount,
                checked((ulong)payload.MaterialContracts.Count));
            ommDataBytes = SaturatingAdd(
                ommDataBytes,
                checked((ulong)payload.OmmData.Length));
            indexBytes = SaturatingAdd(
                indexBytes,
                checked((ulong)payload.IndexData.Length));
            descriptorBytes = SaturatingAdd(
                descriptorBytes,
                checked((ulong)payload.DescriptorData.Length));
            maximumSubdivisionLevel = Math.Max(
                maximumSubdivisionLevel,
                payload.MaximumSubdivisionLevel);

            foreach (OpacityMicromapUsage usage in payload.UsageHistogram)
            {
                if (usage.SubdivisionLevel >=
                    (uint)subdivisionCounts.Length)
                {
                    continue;
                }
                int level = checked((int)usage.SubdivisionLevel);
                subdivisionCounts[level] = SaturatingAdd(
                    subdivisionCounts[level],
                    usage.Count);
            }

            if (payload.ClassificationStatistics is not { } statistics)
            {
                unclassifiedPayloadCount++;
                continue;
            }

            classifiedPayloadCount++;
            opaque = SaturatingAdd(opaque, statistics.Opaque);
            transparent = SaturatingAdd(
                transparent,
                statistics.Transparent);
            unknownOpaque = SaturatingAdd(
                unknownOpaque,
                statistics.UnknownOpaque);
            unknownTransparent = SaturatingAdd(
                unknownTransparent,
                statistics.UnknownTransparent);
        }

        return new OpacityMicromapContentDiagnostics(
            Authoritative: true,
            RegisteredMeshCount: registeredMeshCount,
            UniqueVariantCount: payloads.Count,
            RejectedRegistrationCount: rejectedRegistrationCount,
            StaleMaterialRegistrationCount: staleMaterialRegistrationCount,
            AmbiguousContentKeyCount: ambiguousContentKeyCount,
            PrimitiveCount: primitiveCount,
            MaterialContractCount: materialContractCount,
            OmmDataBytes: ommDataBytes,
            IndexBytes: indexBytes,
            DescriptorBytes: descriptorBytes,
            ClassifiedPayloadCount: classifiedPayloadCount,
            UnclassifiedPayloadCount: unclassifiedPayloadCount,
            OpaqueMicrotriangleCount: opaque,
            TransparentMicrotriangleCount: transparent,
            UnknownOpaqueMicrotriangleCount: unknownOpaque,
            UnknownTransparentMicrotriangleCount: unknownTransparent,
            MaximumSubdivisionLevel: maximumSubdivisionLevel,
            SubdivisionHistogram:
                OpacityMicromapSubdivisionHistogram.Create(
                    subdivisionCounts),
            Detail: "opacity-micromap-content-generation-authoritative");
    }

    private void RecordOpacityMicromapBuildFailure(
        in OpacityMicromapRuntimeMeshRegistration registration,
        string detail)
    {
        StaticBlasVariantKey key = registration.CreateVariantKey();
        bool registrationStillPresent = false;
        foreach (OpacityMicromapRuntimeMeshRegistration current in
                 _synchronizedOpacityMicromapRegistrationsByMesh.Values)
        {
            if (current.CreateVariantKey() == key &&
                RegistrationsHaveSameVariantIdentity(current, registration))
            {
                registrationStillPresent = true;
                break;
            }
        }
        if (!registrationStillPresent)
        {
            return;
        }

        uint failures = 1U;
        if (_opacityMicromapRetryStates.TryGetValue(
                key,
                out OpacityMicromapGpuRetryState previous) &&
            RegistrationsHaveSameVariantIdentity(
                registration,
                previous.Registration))
        {
            failures = previous.ConsecutiveFailures == uint.MaxValue
                ? uint.MaxValue
                : previous.ConsecutiveFailures + 1U;
        }

        ulong delay = CalculateOpacityMicromapRetryDelay(failures);
        ulong retryAfter = _frameSerial > ulong.MaxValue - delay
            ? ulong.MaxValue
            : _frameSerial + delay;
        _opacityMicromapRetryStates[key] = new(
            registration,
            failures,
            retryAfter,
            string.IsNullOrWhiteSpace(detail)
                ? "opacity-micromap-build-failed"
                : detail);
    }

    internal static ulong CalculateOpacityMicromapRetryDelay(
        uint consecutiveFailures)
    {
        if (consecutiveFailures == 0U)
            return 0UL;
        int shift = (int)Math.Min(consecutiveFailures - 1U, 7U);
        return Math.Min(
            MaximumOpacityMicromapRetryDelayFrames,
            InitialOpacityMicromapRetryDelayFrames << shift);
    }

    private static bool RegistrationsHaveSameBuildIdentity(
        in OpacityMicromapRuntimeMeshRegistration left,
        in OpacityMicromapRuntimeMeshRegistration right) =>
        left.Mesh == right.Mesh &&
        left.Material == right.Material &&
        left.MaterialContentRevision == right.MaterialContentRevision &&
        left.MeshGeometryKey == right.MeshGeometryKey &&
        left.Payload.SourceContentHash == right.Payload.SourceContentHash &&
        left.AccelerationStructureBuildAbi ==
            right.AccelerationStructureBuildAbi;

    private static bool RegistrationsHaveSameVariantIdentity(
        in OpacityMicromapRuntimeMeshRegistration left,
        in OpacityMicromapRuntimeMeshRegistration right) =>
        left.CreateVariantKey() == right.CreateVariantKey() &&
        left.Payload.CookAbi == right.Payload.CookAbi &&
        left.Payload.PrimitiveCount == right.Payload.PrimitiveCount &&
        left.Payload.DescriptorCount == right.Payload.DescriptorCount;

    private void AddTrackedOpacityMicromapBytes(ulong bytes)
    {
        _opacityMicromapAllocatedBytes = checked(
            _opacityMicromapAllocatedBytes + bytes);
        _opacityMicromapPeakAllocatedBytes = Math.Max(
            _opacityMicromapPeakAllocatedBytes,
            _opacityMicromapAllocatedBytes);
    }

    private void RemoveTrackedOpacityMicromapBytes(ulong bytes)
    {
        _opacityMicromapAllocatedBytes =
            _opacityMicromapAllocatedBytes >= bytes
                ? _opacityMicromapAllocatedBytes - bytes
                : 0UL;
    }

    private void DestroyVariantBuffer(ref BufferHandle buffer)
    {
        if (!buffer.IsValid)
            return;
        ulong bytes = _bufferManager.GetBufferSize(buffer);
        _bufferManager.DestroyBuffer(buffer);
        RemoveTrackedOpacityMicromapBytes(bytes);
        buffer = BufferHandle.Invalid;
    }

    private void DestroySupersededMicromapAfterFinalBlasBuild(
        OpacityMicromapGpuVariant variant)
    {
        if (!variant.CompactMicromap ||
            variant.SourceMicromap.Handle == 0UL)
        {
            return;
        }
        _context.OpacityMicromapExtCommandApi?.DestroyMicromap(
            _context.Device,
            variant.SourceMicromap);
        variant.SourceMicromap = default;
        DestroyVariantBuffer(ref variant.SourceMicromapStorage);
    }

    private void RemoveOpacityMicromapGpuVariant(
        StaticBlasVariantKey key,
        OpacityMicromapGpuVariant variant,
        bool deferPublishedResources)
    {
        _opacityMicromapGpuVariants.Remove(key);
        if (deferPublishedResources &&
            variant.Stage == OpacityMicromapGpuVariantStage.Published)
        {
            if (variant.CandidateBlas is not null)
            {
                RetireAccelerationStructureResource(
                    variant.CandidateBlas.Handle,
                    variant.CandidateBlas.StorageBuffer,
                    variant.CandidateBlas.Size);
            }
            RetireOpacityMicromapResource(
                variant.FinalMicromap,
                variant.FinalMicromapStorage,
                variant.CandidateBlas?.Size ?? 0UL);
            InvalidateTlasForOpacityMicromapVariantChange();
        }
        else
        {
            DestroyOpacityMicromapVariantImmediately(variant);
        }
        RecalculateAccelerationStructureBytes();
    }

    private void CancelAllOpacityMicromapGpuVariants(string detail)
    {
        _opacityMicromapVariantKeyScratch.Clear();
        foreach (StaticBlasVariantKey key in _opacityMicromapGpuVariants.Keys)
            _opacityMicromapVariantKeyScratch.Add(key);
        foreach (StaticBlasVariantKey key in _opacityMicromapVariantKeyScratch)
        {
            if (!_opacityMicromapGpuVariants.TryGetValue(
                    key,
                    out OpacityMicromapGpuVariant? variant))
            {
                continue;
            }
            variant.Detail = detail;
            if (variant.IsWaitingForGpu)
                variant.Cancelled = true;
            else
                RemoveOpacityMicromapGpuVariant(
                    key,
                    variant,
                    deferPublishedResources: true);
        }
    }

    private void DestroyOpacityMicromapVariantImmediately(
        OpacityMicromapGpuVariant variant)
    {
        if (variant.CompactedCandidateBlas is not null)
        {
            DestroyAccelerationStructureResource(
                variant.CompactedCandidateBlas.Handle,
                variant.CompactedCandidateBlas.StorageBuffer);
            RemoveTrackedOpacityMicromapBytes(
                variant.CompactedCandidateBlas.Size);
            variant.CompactedCandidateBlas = null;
        }
        if (variant.CandidateBlas is not null)
        {
            DestroyAccelerationStructureResource(
                variant.CandidateBlas.Handle,
                variant.CandidateBlas.StorageBuffer);
            RemoveTrackedOpacityMicromapBytes(
                variant.CandidateBlas.Size);
            variant.CandidateBlas = null;
        }

        MicromapEXT final = variant.FinalMicromap;
        MicromapEXT source = variant.SourceMicromap;
        if (final.Handle != 0UL)
        {
            _context.OpacityMicromapExtCommandApi?.DestroyMicromap(
                _context.Device,
                final);
        }
        if (source.Handle != 0UL && source.Handle != final.Handle)
        {
            _context.OpacityMicromapExtCommandApi?.DestroyMicromap(
                _context.Device,
                source);
        }
        variant.FinalMicromap = default;
        variant.SourceMicromap = default;

        var buffers = new HashSet<BufferHandle>();
        DestroyUniqueVariantBuffer(ref variant.BuildScratch, buffers);
        DestroyUniqueVariantBuffer(ref variant.OmmData, buffers);
        DestroyUniqueVariantBuffer(ref variant.TriangleArray, buffers);
        DestroyUniqueVariantBuffer(ref variant.PerPrimitiveIndex, buffers);
        DestroyUniqueVariantBuffer(
            ref variant.FinalMicromapStorage,
            buffers);
        DestroyUniqueVariantBuffer(
            ref variant.SourceMicromapStorage,
            buffers);
    }

    private void DestroyUniqueVariantBuffer(
        ref BufferHandle buffer,
        HashSet<BufferHandle> destroyed)
    {
        if (!buffer.IsValid || !destroyed.Add(buffer))
        {
            buffer = BufferHandle.Invalid;
            return;
        }
        DestroyVariantBuffer(ref buffer);
    }

    private void RetireOpacityMicromapResource(
        MicromapEXT micromap,
        BufferHandle storage,
        ulong associatedBlasBytes)
    {
        ulong storageBytes = storage.IsValid
            ? _bufferManager.GetBufferSize(storage)
            : 0UL;
        RemoveTrackedOpacityMicromapBytes(
            checked(storageBytes + associatedBlasBytes));
        _retiredOpacityMicromapResources.Add(
            new RetiredOpacityMicromapResource(
                micromap,
                storage,
                storageBytes,
                associatedBlasBytes,
                _frameSerial +
                    (ulong)RenderingConstants.FramesInFlight + 1UL));
    }

    private void DrainRetiredOpacityMicromapResources(bool force)
    {
        for (int i = _retiredOpacityMicromapResources.Count - 1;
             i >= 0;
             i--)
        {
            RetiredOpacityMicromapResource retired =
                _retiredOpacityMicromapResources[i];
            if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                continue;
            _context.OpacityMicromapExtCommandApi?.DestroyMicromap(
                _context.Device,
                retired.Micromap);
            if (retired.Storage.IsValid)
                _bufferManager.DestroyBuffer(retired.Storage);
            _retiredOpacityMicromapResources.RemoveAt(i);
        }
    }

    private ulong CalculateRetiredOpacityMicromapBytes()
    {
        ulong bytes = 0UL;
        foreach (RetiredOpacityMicromapResource resource in
                 _retiredOpacityMicromapResources)
        {
            bytes = checked(bytes + resource.Bytes);
        }
        return bytes;
    }

    private SimpleDdgiAdvancedExperimentMemoryPlan
        CreateOpacityMicromapCentralMemoryPlan()
    {
        OpacityMicromapMemoryBreakdown current =
            CalculateOpacityMicromapMemoryBreakdown();
        GiExperimentFallbackReason fallbackReason =
            !_opacityMicromapGpuRuntimeRequested
                ? GiExperimentFallbackReason.None
                : _opacityMicromapGpuRuntimeEnabled
                    ? GiExperimentFallbackReason.None
                    : GiExperimentFallbackReason.ResourceIncomplete;
        if (!_opacityMicromapGpuRuntimeEnabled)
        {
            return SimpleDdgiAdvancedExperimentMemoryPlan.Empty with
            {
                OpacityMicromapResidentData =
                    SimpleDdgiAdvancedMemoryUsage.Zero(
                        SimpleDdgiAdvancedMemoryCategory
                            .OpacityMicromapResidentData,
                        fallbackReason),
                OpacityMicromapBuildScratch =
                    SimpleDdgiAdvancedMemoryUsage.Zero(
                        SimpleDdgiAdvancedMemoryCategory
                            .OpacityMicromapBuildScratch,
                        fallbackReason),
                OpacityMicromapCompactionHeadroom =
                    SimpleDdgiAdvancedMemoryUsage.Zero(
                        SimpleDdgiAdvancedMemoryCategory
                            .OpacityMicromapCompactionHeadroom,
                        fallbackReason)
            };
        }

        ulong retired = CalculateRetiredOpacityMicromapBytes();
        return SimpleDdgiAdvancedExperimentMemoryPlan.Empty with
        {
            OpacityMicromapResidentData = CreateOpacityMicromapMemoryUsage(
                SimpleDdgiAdvancedMemoryCategory
                    .OpacityMicromapResidentData,
                current.ResidentBytes,
                _opacityMicromapResidentPeakBytes,
                retired),
            OpacityMicromapBuildScratch = CreateOpacityMicromapMemoryUsage(
                SimpleDdgiAdvancedMemoryCategory
                    .OpacityMicromapBuildScratch,
                current.BuildScratchBytes,
                _opacityMicromapBuildScratchPeakBytes,
                0UL),
            OpacityMicromapCompactionHeadroom =
                CreateOpacityMicromapMemoryUsage(
                    SimpleDdgiAdvancedMemoryCategory
                        .OpacityMicromapCompactionHeadroom,
                    current.CompactionHeadroomBytes,
                    _opacityMicromapCompactionHeadroomPeakBytes,
                    0UL)
        };
    }

    private static SimpleDdgiAdvancedMemoryUsage
        CreateOpacityMicromapMemoryUsage(
            SimpleDdgiAdvancedMemoryCategory category,
            ulong allocatedBytes,
            ulong peakLiveBytes,
            ulong retiredButLiveBytes)
    {
        ulong admitted = Math.Max(allocatedBytes, peakLiveBytes);
        return new SimpleDdgiAdvancedMemoryUsage(
            category,
            RequestedBytes: admitted,
            RequiredBytes: admitted,
            AdmittedBytes: admitted,
            AllocatedBytes: allocatedBytes,
            PeakLiveBytes: peakLiveBytes,
            RetiredButLiveBytes: retiredButLiveBytes,
            FallbackBytes: 0UL,
            FallbackReason: GiExperimentFallbackReason.None);
    }

    private void UpdateOpacityMicromapCategoryPeaks()
    {
        OpacityMicromapMemoryBreakdown current =
            CalculateOpacityMicromapMemoryBreakdown();
        _opacityMicromapResidentPeakBytes = Math.Max(
            _opacityMicromapResidentPeakBytes,
            current.ResidentBytes);
        _opacityMicromapBuildScratchPeakBytes = Math.Max(
            _opacityMicromapBuildScratchPeakBytes,
            current.BuildScratchBytes);
        _opacityMicromapCompactionHeadroomPeakBytes = Math.Max(
            _opacityMicromapCompactionHeadroomPeakBytes,
            current.CompactionHeadroomBytes);
    }

    private OpacityMicromapMemoryBreakdown
        CalculateOpacityMicromapMemoryBreakdown()
    {
        ulong resident = 0UL;
        ulong buildScratch = 0UL;
        ulong compaction = 0UL;
        HashSet<BufferHandle> counted =
            _opacityMicromapMemoryCountedBuffers;
        counted.Clear();

        foreach (OpacityMicromapGpuVariant variant in
                 _opacityMicromapGpuVariants.Values)
        {
            if (variant.Stage == OpacityMicromapGpuVariantStage.Published)
            {
                AddUniqueOpacityMicromapBufferBytes(
                    variant.FinalMicromapStorage,
                    counted,
                    ref resident);
                AddUniqueOpacityMicromapBlasBytes(
                    variant.CandidateBlas,
                    counted,
                    ref resident);
                continue;
            }

            AddUniqueOpacityMicromapBufferBytes(
                variant.OmmData,
                counted,
                ref buildScratch);
            AddUniqueOpacityMicromapBufferBytes(
                variant.TriangleArray,
                counted,
                ref buildScratch);
            AddUniqueOpacityMicromapBufferBytes(
                variant.PerPrimitiveIndex,
                counted,
                ref buildScratch);
            AddUniqueOpacityMicromapBufferBytes(
                variant.BuildScratch,
                counted,
                ref buildScratch);
            AddUniqueOpacityMicromapBufferBytes(
                variant.SourceMicromapStorage,
                counted,
                ref buildScratch);
            AddUniqueOpacityMicromapBlasBytes(
                variant.CandidateBlas,
                counted,
                ref buildScratch);

            AddUniqueOpacityMicromapBufferBytes(
                variant.FinalMicromapStorage,
                counted,
                ref compaction);
            AddUniqueOpacityMicromapBlasBytes(
                variant.CompactedCandidateBlas,
                counted,
                ref compaction);
        }

        return new OpacityMicromapMemoryBreakdown(
            resident,
            buildScratch,
            compaction);
    }

    private void AddUniqueOpacityMicromapBlasBytes(
        BottomLevelAccelerationStructure? blas,
        HashSet<BufferHandle> counted,
        ref ulong bytes)
    {
        if (blas is null)
            return;
        AddUniqueOpacityMicromapBufferBytes(
            blas.StorageBuffer,
            counted,
            ref bytes);
    }

    private void AddUniqueOpacityMicromapBufferBytes(
        BufferHandle buffer,
        HashSet<BufferHandle> counted,
        ref ulong bytes)
    {
        if (!buffer.IsValid || !counted.Add(buffer))
            return;
        bytes = SaturatingAdd(
            bytes,
            _bufferManager.GetBufferSize(buffer));
    }

    private int GetOpacityMicromapBlasCount()
    {
        int count = 0;
        foreach (OpacityMicromapGpuVariant variant in
                 _opacityMicromapGpuVariants.Values)
        {
            if (variant.CandidateBlas is not null)
                count++;
            if (variant.CompactedCandidateBlas is not null)
                count++;
        }
        return count;
    }

    private void AddOpacityMicromapBlasMemory(
        ref ulong totalBytes,
        ref ulong bottomLevelBytes,
        ref ulong compactedBytesSaved)
    {
        foreach (OpacityMicromapGpuVariant variant in
                 _opacityMicromapGpuVariants.Values)
        {
            BottomLevelAccelerationStructure? candidate =
                variant.CandidateBlas;
            if (candidate is not null)
            {
                bottomLevelBytes = checked(
                    bottomLevelBytes + candidate.Size);
                totalBytes = checked(totalBytes + candidate.Size);
                compactedBytesSaved = checked(
                    compactedBytesSaved +
                    candidate.UncompactedSize - candidate.Size);
            }
            BottomLevelAccelerationStructure? compacted =
                variant.CompactedCandidateBlas;
            if (compacted is not null)
            {
                bottomLevelBytes = checked(
                    bottomLevelBytes + compacted.Size);
                totalBytes = checked(totalBytes + compacted.Size);
                compactedBytesSaved = checked(
                    compactedBytesSaved +
                    compacted.UncompactedSize - compacted.Size);
            }
        }
    }

    private void DisposeOpacityMicromapGpuRuntime()
    {
        foreach (OpacityMicromapGpuVariant variant in
                 _opacityMicromapGpuVariants.Values)
        {
            DestroyOpacityMicromapVariantImmediately(variant);
        }
        _opacityMicromapGpuVariants.Clear();
        _opacityMicromapRetryStates.Clear();
        DrainRetiredOpacityMicromapResources(force: true);

        DestroyQueryPools(_opacityMicromapCompactionQueryPools);
        DestroyQueryPools(_opacityMicromapBlasCompactionQueryPools);
    }

    private void DestroyQueryPools(QueryPool[] pools)
    {
        for (int i = 0; i < pools.Length; i++)
        {
            if (pools[i].Handle == 0UL)
                continue;
            _context.Api.DestroyQueryPool(
                _context.Device,
                pools[i],
                null);
            pools[i] = default;
        }
    }

    private enum OpacityMicromapGpuVariantStage : byte
    {
        AwaitingMicromapBuild = 0,
        WaitingForMicromapBuild,
        AwaitingFinalBlasBuild,
        WaitingForBlasBuild,
        AwaitingBlasCompaction,
        WaitingForBlasCompaction,
        Published
    }

    private sealed class OpacityMicromapGpuVariant
    {
        public OpacityMicromapGpuVariant(
            in OpacityMicromapRuntimeMeshRegistration registration,
            in OpacityMicromapExtNativeBuildSizes buildSizes)
        {
            Registration = registration;
            BuildSizes = buildSizes;
        }

        public readonly OpacityMicromapRuntimeMeshRegistration Registration;
        public readonly OpacityMicromapExtNativeBuildSizes BuildSizes;
        public OpacityMicromapGpuVariantStage Stage;
        public int CompletionFrameIndex = -1;
        public bool Cancelled;
        public bool MicromapCompactionQueryRecorded;
        public bool BlasCompactionQueryRecorded;
        public bool CompactMicromap;
        public ulong CompactedMicromapBytes;
        public ulong CompactedBlasBytes;
        public ulong ReuseCount;
        public BufferHandle OmmData = BufferHandle.Invalid;
        public BufferHandle TriangleArray = BufferHandle.Invalid;
        public BufferHandle PerPrimitiveIndex = BufferHandle.Invalid;
        public BufferHandle BuildScratch = BufferHandle.Invalid;
        public BufferHandle SourceMicromapStorage = BufferHandle.Invalid;
        public BufferHandle FinalMicromapStorage = BufferHandle.Invalid;
        public MicromapEXT SourceMicromap;
        public MicromapEXT FinalMicromap;
        public BottomLevelAccelerationStructure? CandidateBlas;
        public BottomLevelAccelerationStructure? CompactedCandidateBlas;
        public string Detail =
            "opacity-micromap-build-created";

        public bool IsWaitingForGpu => Stage is
            OpacityMicromapGpuVariantStage.WaitingForMicromapBuild or
            OpacityMicromapGpuVariantStage.WaitingForBlasBuild or
            OpacityMicromapGpuVariantStage.WaitingForBlasCompaction;
    }

    private readonly record struct RetiredOpacityMicromapResource(
        MicromapEXT Micromap,
        BufferHandle Storage,
        ulong StorageBytes,
        ulong AssociatedBlasBytes,
        ulong RetireAfterFrameSerial)
    {
        public ulong Bytes => checked(StorageBytes + AssociatedBlasBytes);
    }

    private readonly record struct OpacityMicromapGpuRetryState(
        OpacityMicromapRuntimeMeshRegistration Registration,
        uint ConsecutiveFailures,
        ulong RetryAfterFrameSerial,
        string Detail);

    private readonly record struct OpacityMicromapMemoryBreakdown(
        ulong ResidentBytes,
        ulong BuildScratchBytes,
        ulong CompactionHeadroomBytes);
}

public readonly record struct OpacityMicromapGpuRuntimeSnapshot(
    bool Requested,
    bool Supported,
    bool Enabled,
    int RegisteredCandidateCount,
    int PendingVariantCount,
    int PublishedVariantCount,
    int DeferredRetryCount,
    ulong AllocatedBytes,
    ulong PeakAllocatedBytes,
    ulong RetiredButLiveBytes,
    ulong BuildCount,
    ulong PublicationCount,
    ulong FallbackCount,
    ulong MicromapCompactionCount,
    ulong BlasCompactionCount,
    ulong QueryFailureCount,
    string Detail)
{
    public long LastCpuRecordMicroseconds { get; init; }
    public long PeakCpuRecordMicroseconds { get; init; }
    public long LastGpuBuildMicroseconds { get; init; }
    public ulong VariantCacheHitCount { get; init; }
    public ulong VariantCacheMissCount { get; init; }
    public ulong VariantEvictionCount { get; init; }
    public ulong VariantCapFallbackCount { get; init; }
    public OpacityMicromapContentDiagnostics Content { get; init; } =
        OpacityMicromapContentDiagnostics.Unavailable;
    public SimpleDdgiAdvancedExperimentMemoryPlan Memory { get; init; } =
        SimpleDdgiAdvancedExperimentMemoryPlan.Empty;

    public bool IsValid =>
        RegisteredCandidateCount >= 0 &&
        PendingVariantCount >= 0 &&
        PublishedVariantCount >= 0 &&
        DeferredRetryCount >= 0 &&
        PeakAllocatedBytes >= AllocatedBytes &&
        PublicationCount <= BuildCount &&
        MicromapCompactionCount <= BuildCount &&
        BlasCompactionCount <= BuildCount &&
        LastCpuRecordMicroseconds >= 0 &&
        PeakCpuRecordMicroseconds >= LastCpuRecordMicroseconds &&
        LastGpuBuildMicroseconds >= 0 &&
        Content.IsValid &&
        (!Content.Authoritative ||
         Content.RegisteredMeshCount == RegisteredCandidateCount) &&
        Memory.HasOnlyOpacityMicromapCategories &&
        Memory.IsValid &&
        Memory.AllocatedBytes == AllocatedBytes &&
        Memory.RetiredButLiveBytes == RetiredButLiveBytes &&
        (!Enabled || Requested && Supported) &&
        !string.IsNullOrWhiteSpace(Detail);

    public OpacityMicromapGpuRuntimeSnapshot NormalizeForPersistence()
    {
        OpacityMicromapGpuRuntimeSnapshot normalized = this with
        {
            Detail = Detail?.Trim() ?? string.Empty,
            Content = Content.NormalizeForPersistence(),
            Memory = Memory.NormalizeForPersistence()
        };
        return normalized.IsValid ? normalized : Disabled;
    }

    public static OpacityMicromapGpuRuntimeSnapshot Disabled { get; } = new(
        Requested: false,
        Supported: false,
        Enabled: false,
        RegisteredCandidateCount: 0,
        PendingVariantCount: 0,
        PublishedVariantCount: 0,
        DeferredRetryCount: 0,
        AllocatedBytes: 0UL,
        PeakAllocatedBytes: 0UL,
        RetiredButLiveBytes: 0UL,
        BuildCount: 0UL,
        PublicationCount: 0UL,
        FallbackCount: 0UL,
        MicromapCompactionCount: 0UL,
        BlasCompactionCount: 0UL,
        QueryFailureCount: 0UL,
        Detail: "opacity-micromap-runtime-disabled")
    {
        Content = OpacityMicromapContentDiagnostics.Unavailable
    };
}

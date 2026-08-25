using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Assets.Cooked;
using Njulf.Assets.Validation;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe partial class AccelerationStructureManager : IDisposable
    {
        /// <summary>
        /// Frozen identity of the static triangle BLAS input/layout contract.
        /// OMM variants include this value in their cache and publication key
        /// so a future geometry or flag ABI change cannot reuse stale native
        /// micromap attachments.
        /// </summary>
        public const uint StaticBlasBuildAbi = 1U;

        public const string FoliageDdgiExclusionReason =
            "foliage uses clustered alpha geometry and requires explicit DDGI proxy cards or clusters";
        internal const byte StaticOpaqueInstanceMask = 0x01;
        internal const byte DirectionalShadowInstanceMask = 0x02;
        internal const byte SharedLightingInstanceMask =
            StaticOpaqueInstanceMask | DirectionalShadowInstanceMask;
        private const ulong MinResourceBufferSize = 16;
        private const ulong IndexStride = sizeof(uint);
        private const int MaxBlasCompactionQueriesPerFrame = 4096;
        // Keep the fence-safe source-retirement overlap below the global
        // residency headroom while still draining a large static scene during
        // normal warm-up. One oversized BLAS is allowed to make progress.
        private const ulong MaxBlasCompactionDestinationBytesPerFrame = 32UL * 1024UL * 1024UL;
        private const ulong HashStart = 14695981039346656037UL;
        private const ulong HashPrime = 1099511628211UL;
        private static readonly ulong VertexPositionStride = (ulong)Marshal.SizeOf<GPUVertexPositionStream>();
        private static readonly ulong RayQueryInstanceMetadataStride =
            (ulong)Marshal.SizeOf<GPUDdgiRayQueryInstance>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly MeshManager _meshManager;
        private readonly MaterialManager _materialManager;
        private readonly OpacityMicromapRuntimeRegistrationStore?
            _opacityMicromapRuntimeRegistrations;
        private readonly KhrAccelerationStructure? _khrAccelerationStructure;
        private readonly AccelerationStructureOpacityMicromapNativeLifecycleHost
            _opacityMicromapNativeLifecycleHost;
        private readonly ulong _scratchBufferAddressAlignment;
        private readonly Dictionary<MeshHandle, BottomLevelAccelerationStructure> _blasCache = new();
        private readonly Dictionary<DynamicBlasKey, DynamicBottomLevelAccelerationStructure>
            _dynamicBlasPool = new();
        private readonly List<int> _dynamicAdmissionScratch = new();
        private readonly HashSet<Guid> _activeDynamicObjectScratch = new();
        // Vulkan build-size queries are stable for a mesh-buffer generation but
        // expensive enough to dominate a no-build frame when hundreds of meshes
        // are reconsidered by the residency policy.
        private readonly Dictionary<MeshHandle, ulong> _blasSizeEstimateCache = new();
        private readonly List<AccelerationStructureStorageBuffer> _rayQueryStorageScratch = new();
        private readonly ReadOnlyCollection<AccelerationStructureStorageBuffer> _rayQueryStorageView;
        private readonly List<StaticOpaqueInstance> _instanceScratch = new();
        private readonly List<StaticOpaqueInstance> _preparedInstanceScratch = new();
        private readonly List<StaticOpaqueInstance> _residentInstanceScratch = new();
        private readonly List<StaticOpaqueInstance> _memoryResidentInstanceScratch = new();
        private readonly List<StaticResidencyCandidate> _staticResidencyCandidateScratch = new();
        private readonly HashSet<MeshHandle> _activeMeshScratch = new();
        private readonly HashSet<MeshHandle> _budgetMeshScratch = new();
        private readonly HashSet<MeshHandle> _unavailableMeshScratch = new();
        private readonly List<AccelerationStructureInstanceKHR> _gpuInstanceScratch = new();
        private readonly List<GPUDdgiRayQueryInstance> _rayQueryInstanceScratch = new();
        private readonly List<RetiredAccelerationStructureResource> _retiredAccelerationStructures = new();
        private readonly List<RetiredBufferResource> _retiredBuffers = new();
        private readonly QueryPool[] _blasCompactionQueryPools =
            new QueryPool[RenderingConstants.FramesInFlight];
        private readonly List<PendingBlasCompactionQuery>[] _pendingBlasCompactionQueries =
            new List<PendingBlasCompactionQuery>[RenderingConstants.FramesInFlight];
        private readonly ulong[][] _blasCompactionQueryResults =
            new ulong[RenderingConstants.FramesInFlight][];
        private readonly bool[] _blasCompactionQueryPoolResetThisFrame =
            new bool[RenderingConstants.FramesInFlight];
        private readonly Queue<ReadyBlasCompaction> _readyBlasCompactions = new();
        private readonly Dictionary<OpacityMicromapContentKey,
            OpacityMicromapExtStaticBlasCandidate>
            _synchronizedOpacityMicromapCandidates = new();
        private ulong _synchronizedOpacityMicromapCandidateSetRevision;
        private volatile bool _opacityMicromapMaterialStateDirty;
        private string _opacityMicromapRegistrationDetail =
            "omm-runtime-registration-store-unavailable";
        private ulong _retiredAccelerationStructureBytes;
        private ulong _retiredDynamicAccelerationStructureBytes;
        private ulong _retiredBufferBytes;

        private TopLevelAccelerationStructure _tlas;
        private readonly TopLevelAccelerationStructure[] _tlasFrameSlots =
            new TopLevelAccelerationStructure[RenderingConstants.FramesInFlight];
        private readonly ulong[] _tlasInstanceSignatures =
            new ulong[RenderingConstants.FramesInFlight];
        private readonly bool[] _tlasHasInstanceSignatures =
            new bool[RenderingConstants.FramesInFlight];
        private readonly int[] _tlasInstanceCounts =
            new int[RenderingConstants.FramesInFlight];
        private int _currentTlasFrameSlot = -1;
        private BufferHandle _instanceBuffer;
        private ulong _instanceBufferSize;
        private BufferHandle _rayQueryInstanceBuffer;
        private ulong _rayQueryInstanceBufferSize;
        private BufferHandle _scratchBuffer;
        private ulong _scratchBufferSize;
        private ulong _scratchBufferCapacity;
        private ulong _scratchBufferDeviceAddress;
        private BufferHandle _lastVertexPositionBuffer;
        private BufferHandle _lastIndexBuffer;
        private bool _disposed;
        private BindlessHeap? _registeredBindlessHeap;
        private string _lastFallbackReason = string.Empty;
        private long _lastBuildMicroseconds;
        private long _lastBlasBuildMicroseconds;
        private long _lastTlasBuildMicroseconds;
        private long _lastInstanceUploadMicroseconds;
        private int _lastBlasBuildCount;
        private int _lastTlasBuildCount;
        private int _lastTlasUpdateCount;
        private int _lastTlasSkipCount;
        private ulong _lastInstanceUploadBytes;
        private ulong _lastRayQueryInstanceMetadataUploadBytes;
        private int _lastStaticInstanceCandidateCount;
        private int _lastStaticInstanceResidentCount;
        private int _lastStaticInstanceCulledCount;
        private int _lastBlasEvictionCount;
        private ulong _lastBlasEvictionBytes;
        private int _lastBlasBudgetRejectedCount;
        private long _lastBlasCompactionMicroseconds;
        private int _lastBlasCompactionQueryCount;
        private int _lastBlasCompactionCount;
        private ulong _lastBlasCompactionSourceBytes;
        private ulong _lastBlasCompactionBytesSaved;
        private int _lastBlasCompactionQueryOverflowCount;
        private int _lastBlasCompactionQueryReadbackFailureCount;
        private ulong _bottomLevelAccelerationStructureCompactedBytesSaved;
        private bool _blasCompactionQueriesDisabled;
        private AccelerationStructureResidencyPolicy _residencyPolicy;
        private ulong _lastTlasInstanceSignature;
        private bool _hasTlasInstanceSignature;
        private int _lastTlasInstanceCount;
        private AccelerationStructurePreparationIdentity _lastPreparationIdentity;
        private bool _hasReusablePreparation;
        private ulong _lastPreparationResourceGeneration;
        private int _cachedStaticInstanceCandidateCount;
        private int _cachedStaticInstanceResidentCount;
        private int _cachedStaticInstanceCulledCount;
        private bool _rayQueryHasAlphaCandidateGeometry;
        private bool _rayQueryHasThinTransmissionGeometry;
        private ulong _frameSerial;
        private ulong _resourceGeneration;
        private ulong _raySceneContentEpoch = 1;
        private ulong _lastRaySceneContentSignature;
        private bool _hasRaySceneContentSignature;
        private RaySceneRequirement _preparedRaySceneRequirement;
        private RaySceneGeometryCategory _preparedRaySceneSupportedCategories;
        private string _preparedRaySceneCoverageFailure = string.Empty;
        private RaySceneReadinessSnapshot _readinessSnapshot;
        private PreparedRayScene? _preparedRayScene;
        private StaticOpaqueInstance[] _publishedRaySceneInstances =
            Array.Empty<StaticOpaqueInstance>();
        private CoreBoundingBox _publishedRaySceneBounds;
        private bool _publishedRaySceneBoundsValid;
        private ulong _publishedRaySceneContentRevision;
        private ulong _publishedTlasInstanceSignature;
        private ulong _dynamicBlasBytes;
        private ulong _peakDynamicBlasBytes;
        private int _lastDynamicBlasFullBuildCount;
        private int _lastDynamicBlasRefitCount;
        private int _lastDynamicBlasProxyFallbackCount;
        private int _lastDynamicBlasExcludedCount;
        private int _lastDynamicBlasBudgetDeferredCount;
        private int _lastDynamicBlasTopologyMismatchCount;
        private ulong _lastDynamicBlasScratchBytes;
        private ulong _lastDynamicBlasPrimitiveCount;

        public AccelerationStructureManager(
            VulkanContext context,
            BufferManager bufferManager,
            MeshManager meshManager,
            MaterialManager materialManager,
            OpacityMicromapRuntimeRegistrationStore?
                opacityMicromapRuntimeRegistrations = null,
            bool enableOpacityMicromapRuntime = false)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _opacityMicromapRuntimeRegistrations =
                opacityMicromapRuntimeRegistrations;
            if (_opacityMicromapRuntimeRegistrations is not null)
            {
                _opacityMicromapRegistrationDetail =
                    "omm-runtime-registration-awaiting-frame-boundary";
                _materialManager.MaterialChanged +=
                    OnOpacityMicromapMaterialChanged;
            }
            _rayQueryStorageView = _rayQueryStorageScratch.AsReadOnly();
            for (int i = 0; i < _pendingBlasCompactionQueries.Length; i++)
            {
                _pendingBlasCompactionQueries[i] =
                    new List<PendingBlasCompactionQuery>(256);
                _blasCompactionQueryResults[i] =
                    new ulong[MaxBlasCompactionQueriesPerFrame];
            }
            _khrAccelerationStructure = context.KhrAccelerationStructure;
            _opacityMicromapNativeLifecycleHost =
                new AccelerationStructureOpacityMicromapNativeLifecycleHost(
                    () => _context.OpacityMicromapExtCapability,
                    ResolveOpacityMicromapOrdinaryFallback);
            InitializeOpacityMicromapGpuRuntime(
                enableOpacityMicromapRuntime);
            _scratchBufferAddressAlignment = context.RayQuerySupported && _khrAccelerationStructure != null
                ? QueryScratchBufferAddressAlignment(context)
                : 1UL;
            if (!context.RayQuerySupported)
                _lastFallbackReason = "Ray-query and acceleration-structure features are not supported by the selected Vulkan device.";
            else if (_khrAccelerationStructure == null)
                _lastFallbackReason = "VK_KHR_acceleration_structure could not be loaded.";
            EnsureRayQueryInstanceMetadataCapacity(0);
        }

        public bool Supported => _context.RayQuerySupported && _khrAccelerationStructure != null;
        /// <summary>
        /// The sole C1 native lifecycle boundary owned by this AS manager.
        /// It deliberately remains fail-closed until the renderer provides an
        /// atomic static-BLAS variant submission/publication/retirement path.
        /// </summary>
        public IOpacityMicromapExtNativeLifecycleHost OpacityMicromapNativeLifecycleHost =>
            _opacityMicromapNativeLifecycleHost;

        /// <summary>
        /// Registers a content-keyed static BLAS candidate without allocating
        /// a micromap or changing the ordinary BLAS cache.  Callers must retain
        /// the ordinary candidate-tested path if this returns false.
        /// </summary>
        public bool TryRegisterOpacityMicromapStaticBlasCandidate(
            in OpacityMicromapExtStaticBlasCandidate candidate,
            out string detail) =>
            _opacityMicromapNativeLifecycleHost.TryRegister(candidate, out detail);

        /// <summary>
        /// Removes only the content-to-static-mesh registration.  No native
        /// resource can be removed here because this host cannot publish a
        /// hardware OMM generation without the renderer completion bridge.
        /// </summary>
        public bool TryRemoveOpacityMicromapStaticBlasCandidate(
            OpacityMicromapContentKey contentKey,
            out string detail) =>
            _opacityMicromapNativeLifecycleHost.RemoveRegistration(contentKey, out detail);

        public int RegisteredOpacityMicromapCandidateCount =>
            _synchronizedOpacityMicromapCandidates.Count;

        public string OpacityMicromapRegistrationDetail =>
            _opacityMicromapRegistrationDetail;

        public bool Active => Supported && _tlas.Handle.Handle != 0 && TopLevelInstanceCount > 0 && string.IsNullOrEmpty(_lastFallbackReason);
        public AccelerationStructureKHR TopLevelAccelerationStructureHandle => _tlas.Handle;
        /// <summary>Backing allocation for the TLAS; required for queue ownership handoffs.</summary>
        public BufferHandle TopLevelAccelerationStructureStorageBuffer => _tlas.StorageBuffer;
        public ulong TopLevelAccelerationStructureStorageBufferBytes => _tlas.Size;
        public BufferHandle RayQueryInstanceMetadataBuffer => _rayQueryInstanceBuffer;
        public int BottomLevelCount => checked(
            _blasCache.Count + _dynamicBlasPool.Count +
            GetOpacityMicromapBlasCount());
        public int StaticBottomLevelCount => checked(
            _blasCache.Count + GetOpacityMicromapBlasCount());
        public int DynamicBottomLevelCount => _dynamicBlasPool.Count;
        public ulong DynamicBottomLevelAccelerationStructureBytes => _dynamicBlasBytes;
        public ulong PeakDynamicBottomLevelAccelerationStructureBytes => _peakDynamicBlasBytes;
        public int TopLevelInstanceCount { get; private set; }
        /// <summary>
        /// Immutable facts for the currently published TLAS. These are
        /// computed from the same material policy that chooses Vulkan instance
        /// opacity and are therefore safe for exact shader specialization.
        /// </summary>
        public bool RayQueryHasAlphaCandidateGeometry =>
            _rayQueryHasAlphaCandidateGeometry;
        public bool RayQueryHasThinTransmissionGeometry =>
            _rayQueryHasThinTransmissionGeometry;
        public ulong AccelerationStructureBytes { get; private set; }
        public ulong BottomLevelAccelerationStructureBytes { get; private set; }
        /// <summary>
        /// Bytes removed from the currently resident BLAS working set by
        /// VK_KHR_acceleration_structure compaction. This is active residency,
        /// not a cumulative allocation counter.
        /// </summary>
        public ulong BottomLevelAccelerationStructureCompactedBytesSaved =>
            _bottomLevelAccelerationStructureCompactedBytesSaved;
        public ulong TopLevelAccelerationStructureBytes =>
            CalculateTopLevelFrameSlotBytes();
        /// <summary>
        /// Bytes retained only until all in-flight frames can no longer reference a
        /// replaced BLAS/TLAS. These allocations are physically live and are reported
        /// separately from the current resident working set.
        /// </summary>
        public ulong RetiredAccelerationStructureBytes => _retiredAccelerationStructureBytes;
        /// <summary>
        /// Retired-but-still-live storage owned specifically by the
        /// current-pose dynamic BLAS pool. Static BLAS, TLAS, and C1 variant
        /// retirement are deliberately excluded so the DDGI content-memory
        /// plan cannot double-count another ownership domain.
        /// </summary>
        public ulong RetiredDynamicBottomLevelAccelerationStructureBytes =>
            _retiredDynamicAccelerationStructureBytes;
        /// <summary>
        /// All acceleration-structure-associated resources retained until in-flight
        /// work drains. The frame-stat contract uses this aggregate so deferred
        /// instance/scratch buffers cannot disappear from residency telemetry.
        /// </summary>
        public ulong RetiredResourceBytes => SaturatingAdd(
            _retiredAccelerationStructureBytes,
            _retiredBufferBytes);
        public ulong LiveAccelerationStructureBytes => checked(AccelerationStructureBytes + _retiredAccelerationStructureBytes);
        public ulong ScratchBufferBytes => _scratchBufferSize;
        public ulong InstanceBufferBytes => _instanceBufferSize;
        public ulong RayQueryInstanceMetadataBufferBytes => _rayQueryInstanceBufferSize;
        /// <summary>
        /// Temporary physical residency retained while in-flight frames drain.  This
        /// is intentionally distinct from the active BLAS/TLAS working-set cap.
        /// </summary>
        public ulong TransientBytes => SaturatingAdd(
            SaturatingAdd(_retiredAccelerationStructureBytes, _retiredBufferBytes),
            SaturatingAdd(SaturatingAdd(ScratchBufferBytes, InstanceBufferBytes), RayQueryInstanceMetadataBufferBytes));
        public ulong RetiredBufferBytes => _retiredBufferBytes;
        public ulong TotalBytes => SaturatingAdd(LiveAccelerationStructureBytes, SaturatingAdd(
            SaturatingAdd(ScratchBufferBytes, InstanceBufferBytes),
            SaturatingAdd(RayQueryInstanceMetadataBufferBytes, _retiredBufferBytes)));
        public string LastFallbackReason => _lastFallbackReason;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;
        /// <summary>Changes when a ray-query-visible backing allocation is added or replaced.</summary>
        public ulong ResourceGeneration => _resourceGeneration;
        /// <summary>
        /// Changes when ray-visible content changes, independently of backing
        /// resource recreation and frame-slot rotation.
        /// </summary>
        public ulong RaySceneContentEpoch => _raySceneContentEpoch;
        public RaySceneReadinessSnapshot ReadinessSnapshot => _readinessSnapshot;

        /// <summary>
        /// Extracts authored C4 heroes only from the immutable instance set
        /// that was published with the current TLAS. Missing cooker evidence,
        /// material revisions, or matching BLAS state becomes a typed
        /// rejection and never reaches task allocation.
        /// </summary>
        public bool TryCreateGiCausticHeroSourceSnapshot(
            in GiCausticHeroExtractionProfile profile,
            out GiCausticHeroSourceSnapshot? snapshot,
            out string reason)
        {
            snapshot = null;
            if (!profile.IsValid)
            {
                reason = "caustic-hero-extraction-profile-invalid";
                return false;
            }
            if (!Active || _publishedRaySceneInstances.Length == 0 ||
                _publishedRaySceneContentRevision == 0UL ||
                _publishedTlasInstanceSignature == 0UL)
            {
                reason = "caustic-hero-current-pose-tlas-snapshot-unavailable";
                return false;
            }

            MaterialDefinition[] materials =
                _materialManager.GetMaterialDefinitionSnapshot();
            var candidates = new List<GiCausticHeroSource>();
            var rejections = new List<GiCausticHeroSourceRejection>();
            var stableIdentities = new HashSet<uint>();
            foreach (StaticOpaqueInstance instance in _publishedRaySceneInstances)
            {
                if ((uint)instance.MaterialIndex >= (uint)materials.Length)
                {
                    rejections.Add(new GiCausticHeroSourceRejection(
                        instance.StableInstanceIdentity,
                        instance.MaterialIndex,
                        GiCausticHeroRejectionReason.RevisionUnavailable,
                        "caustic-hero-material-slot-unavailable"));
                    continue;
                }

                MaterialDefinition definition = materials[instance.MaterialIndex];
                MaterialExtensionDefinition extensions =
                    definition.Extensions ?? MaterialExtensionDefinition.None;
                GiCausticCasterPolicy casterPolicy =
                    OpticalMaterialGpuContract.ResolveCasterPolicy(
                        extensions.CausticCasterPolicy,
                        extensions.CausticParticipation);
                bool volumeTransmission =
                    extensions.TransmissionFactor > 0.0f &&
                    extensions.TransmissionPolicy == GiTransmissionPolicy.Volume;
                if (casterPolicy == GiCausticCasterPolicy.Default)
                {
                    casterPolicy = volumeTransmission ||
                                   extensions.OpticalBoundary ==
                                       OpticalBoundaryKind.WaterSurface
                        ? GiCausticCasterPolicy.DielectricPriority
                        : GiCausticCasterPolicy.Disabled;
                }
                if (casterPolicy == GiCausticCasterPolicy.Disabled)
                    continue;
                GiCausticParticipationMode participation =
                    OpticalMaterialGpuContract.ToLegacyParticipation(
                        casterPolicy,
                        extensions.TransmissionPolicy);

                ModelGiCausticHeroTopologyEvidence topology =
                    instance.MeshInfo.CausticTopologyEvidence;
                if (!topology.IsStructurallyValid)
                {
                    rejections.Add(new GiCausticHeroSourceRejection(
                        instance.StableInstanceIdentity,
                        instance.MaterialIndex,
                        GiCausticHeroRejectionReason.TopologyEvidenceUnavailable,
                        "authenticated-cooker-topology-evidence-is-unavailable"));
                    continue;
                }

                MaterialAspectRevisions revisions =
                    _materialManager.GetMaterialAspectRevisions(
                        checked((int)instance.MaterialIndex));
                bool hasCurrentPoseAccelerationStructure = instance.UsesDynamicBlas
                    ? _dynamicBlasPool.ContainsKey(CreateDynamicBlasKey(instance))
                    : _blasCache.ContainsKey(instance.Mesh);
                var material = new GiCausticMaterialContract(
                    participation,
                    definition.RoughnessFactor,
                    extensions.Ior,
                    ComputeCausticAbsorptionCoefficient(extensions),
                    definition.AlphaMode != MaterialAlphaMode.Opaque,
                    extensions.TransmissionFactor > 0.0f &&
                        extensions.TransmissionPolicy is not
                            (GiTransmissionPolicy.None or GiTransmissionPolicy.Volume),
                    extensions.TransmissionFactor > 0.0f &&
                        extensions.TransmissionPolicy == GiTransmissionPolicy.Volume &&
                        extensions.ThicknessFactor > 0.0f)
                {
                    CasterPolicy = casterPolicy,
                    BoundaryKind = extensions.OpticalBoundary,
                    UsesVolumeTransmission = volumeTransmission
                };
                var geometry = new GiCausticHeroGeometryFacts(
                    IsRigidOrQualifiedCurrentPose:
                        topology.Facts.IsStaticOrCurrentPoseQualified ||
                        instance.UsesDynamicBlas,
                    IsClosedManifold: topology.Facts.IsClosedManifold,
                    HasConsistentWinding: topology.Facts.HasConsistentWinding,
                    HasValidGeometricNormals:
                        topology.Facts.HasGeometricNormals,
                    HasUnsupportedNestedMedia:
                        topology.Facts.HasUnsupportedNestedMedium,
                    HasCurrentPoseAccelerationStructure:
                        hasCurrentPoseAccelerationStructure,
                    HasStableRevisions:
                        revisions.Material != 0u &&
                        instance.TransformRevision != 0UL &&
                        instance.RepresentationGeneration != 0u,
                    HasAuthenticatedTopologyEvidence: true);
                GiCausticHeroValidation validation =
                    GiCausticHeroContractValidator.Validate(material, geometry);
                if (!validation.IsEligible)
                {
                    rejections.Add(new GiCausticHeroSourceRejection(
                        instance.StableInstanceIdentity,
                        instance.MaterialIndex,
                        validation.RejectionReason,
                        validation.Detail));
                    continue;
                }
                if (!stableIdentities.Add(instance.StableInstanceIdentity))
                {
                    rejections.Add(new GiCausticHeroSourceRejection(
                        instance.StableInstanceIdentity,
                        instance.MaterialIndex,
                        GiCausticHeroRejectionReason.RevisionUnavailable,
                        "caustic-hero-stable-identity-collision"));
                    continue;
                }

                CoreBoundingBox worldBounds = GetInstanceWorldBounds(instance);
                ulong geometryRevision = HashStart;
                geometryRevision = HashAdd(geometryRevision,
                    unchecked((uint)topology.TopologyHash));
                geometryRevision = HashAdd(geometryRevision,
                    unchecked((uint)(topology.TopologyHash >> 32)));
                geometryRevision = HashAdd(geometryRevision,
                    instance.Mesh.Index);
                geometryRevision = HashAdd(geometryRevision,
                    instance.Mesh.Generation);
                geometryRevision = HashAdd(geometryRevision,
                    instance.RepresentationGeneration);
                if (geometryRevision == 0UL)
                    geometryRevision = 1UL;
                var source = new GiCausticHeroSource(
                    instance.StableInstanceIdentity,
                    revisions.Material,
                    new System.Numerics.Vector3(
                        worldBounds.Min.X,
                        worldBounds.Min.Y,
                        worldBounds.Min.Z),
                    new System.Numerics.Vector3(
                        worldBounds.Max.X,
                        worldBounds.Max.Y,
                        worldBounds.Max.Z),
                    material,
                    geometry,
                    profile.InitialConeRadius,
                    profile.ConeSpread,
                    profile.MaximumPathDistance,
                    profile.ProposalWeight,
                    geometryRevision,
                    instance.TransformRevision);
                if (!source.TryCompile(out _, out string sourceReason))
                {
                    rejections.Add(new GiCausticHeroSourceRejection(
                        instance.StableInstanceIdentity,
                        instance.MaterialIndex,
                        GiCausticHeroRejectionReason.RevisionUnavailable,
                        sourceReason));
                    continue;
                }
                candidates.Add(source);
            }

            candidates.Sort(static (left, right) =>
            {
                int priority = CasterPriority(right.Material.EffectiveCasterPolicy)
                    .CompareTo(CasterPriority(
                        left.Material.EffectiveCasterPolicy));
                return priority != 0
                    ? priority
                    : left.StableHeroId.CompareTo(right.StableHeroId);
            });
            if (candidates.Count > profile.MaximumHeroCount)
            {
                for (int index = profile.MaximumHeroCount;
                     index < candidates.Count;
                     index++)
                {
                    GiCausticHeroSource rejected = candidates[index];
                    rejections.Add(new GiCausticHeroSourceRejection(
                        rejected.StableHeroId,
                        0u,
                        GiCausticHeroRejectionReason.HeroCapacityExceeded,
                        "admitted-caustic-hero-capacity-exceeded"));
                }
                candidates.RemoveRange(
                    profile.MaximumHeroCount,
                    candidates.Count - profile.MaximumHeroCount);
            }

            snapshot = new GiCausticHeroSourceSnapshot(
                candidates.ToArray(),
                rejections.ToArray(),
                _publishedRaySceneContentRevision,
                _raySceneContentEpoch,
                _publishedTlasInstanceSignature);
            reason = string.Empty;
            return true;

            static int CasterPriority(GiCausticCasterPolicy policy) =>
                policy switch
                {
                    GiCausticCasterPolicy.DielectricPriority => 3,
                    GiCausticCasterPolicy.Mirror => 2,
                    GiCausticCasterPolicy.RoughSpecular => 1,
                    _ => 0
                };
        }

        /// <summary>
        /// Resolves every backing allocation traversed by a ray query. A TLAS descriptor reaches
        /// its BLAS children indirectly, but Vulkan queue ownership still applies to each backing
        /// buffer; exposing only the TLAS storage would leave those reads unsynchronized.
        /// The returned read-only view is reused by the manager and is valid until the next call.
        /// </summary>
        public IReadOnlyList<AccelerationStructureStorageBuffer> GetRayQueryStorageBuffers()
        {
            // This is queried once for every async-plan refresh. Reuse a private scratch list so
            // a scene with many BLAS allocations does not create a per-frame managed allocation.
            // Callers consume it synchronously while building the immutable binding snapshot.
            List<AccelerationStructureStorageBuffer> buffers = _rayQueryStorageScratch;
            buffers.Clear();
            int requiredCapacity = checked(_blasCache.Count + _dynamicBlasPool.Count + 1);
            if (buffers.Capacity < requiredCapacity)
                buffers.Capacity = requiredCapacity;
            if (_tlas.StorageBuffer.IsValid && _tlas.Size > 0)
            {
                buffers.Add(new AccelerationStructureStorageBuffer(
                    _tlas.StorageBuffer,
                    _tlas.Size,
                    "TLAS storage"));
            }

            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                if (!blas.StorageBuffer.IsValid || blas.Size == 0)
                    continue;

                buffers.Add(new AccelerationStructureStorageBuffer(
                    blas.StorageBuffer,
                    blas.Size,
                    "BLAS storage"));
            }

            foreach (DynamicBottomLevelAccelerationStructure blas in _dynamicBlasPool.Values)
            {
                if (!blas.StorageBuffer.IsValid || blas.Size == 0)
                    continue;

                buffers.Add(new AccelerationStructureStorageBuffer(
                    blas.StorageBuffer,
                    blas.Size,
                    "Dynamic BLAS storage"));
            }

            return _rayQueryStorageView;
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            RegisterRayQueryInstanceMetadataBuffer();
        }

        public AccelerationStructureFrameStats PrepareFrame(
            Scene scene,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            bool enabled,
            GpuTimestampRecorder? gpuTimestamps = null,
            int frameIndex = 0,
            AccelerationStructureResidencyPolicy? residencyPolicy = null,
            bool alphaMaskedTransportEnabled = true,
            ulong sceneContentRevision = 0)
        {
            PrepareFrameRayScene(
                scene,
                enabled,
                frameIndex,
                residencyPolicy,
                DdgiDynamicRayScenePolicy.LegacyBaseline with
                {
                    AlphaMaskedTransportEnabled = alphaMaskedTransportEnabled
                },
                sceneContentRevision);
            return RecordDynamicRaySceneBuilds(
                stagingRing,
                commandBuffer,
                BufferHandle.Invalid,
                gpuTimestamps,
                frameIndex);
        }

        /// <summary>
        /// Collects and freezes the ray-visible scene without recording Vulkan
        /// build commands. This stage intentionally runs after CPU skinning
        /// preparation but before the compute skinning dispatch.
        /// </summary>
        public RaySceneBuildPlan PrepareFrameRayScene(
            Scene scene,
            bool enabled,
            int frameIndex,
            AccelerationStructureResidencyPolicy? residencyPolicy,
            DdgiDynamicRayScenePolicy dynamicPolicy,
            ulong sceneContentRevision = 0,
            DdgiFoliageProxyFrame? foliageProxyFrame = null,
            RaySceneRequirement requirement = default)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            ValidateCompactionFrameIndex(frameIndex);
            _preparedInstanceScratch.Clear();
            _preparedRaySceneRequirement = requirement;
            _preparedRaySceneSupportedCategories = ResolveSupportedCategories(dynamicPolicy);
            _preparedRaySceneCoverageFailure =
                ResolvePreparedCoverageFailure(
                    scene,
                    requirement,
                    foliageProxyFrame);
            _rayQueryHasAlphaCandidateGeometry = false;
            _rayQueryHasThinTransmissionGeometry = false;
            if (enabled && Supported)
            {
                CollectStaticOpaqueInstances(
                    scene,
                    _preparedInstanceScratch,
                    dynamicPolicy.AlphaMaskedTransportEnabled,
                    dynamicPolicy.TransparentGeometryMode,
                    dynamicPolicy.GeometryDecalsEnabled,
                    dynamicPolicy.SkinnedGeometryMode,
                    frameIndex,
                    dynamicPolicy.FoliageGeometryMode,
                    foliageProxyFrame);
            }

            ulong contentSignature = CreateRaySceneContentSignature(
                enabled && Supported,
                sceneContentRevision,
                dynamicPolicy,
                _preparedInstanceScratch);
            if (_hasRaySceneContentSignature)
            {
                if (contentSignature != _lastRaySceneContentSignature)
                    _raySceneContentEpoch = NextNonZero(_raySceneContentEpoch);
            }
            else
            {
                _hasRaySceneContentSignature = true;
            }
            _lastRaySceneContentSignature = contentSignature;

            var prepared = new PreparedRayScene(
                enabled,
                frameIndex,
                residencyPolicy ?? AccelerationStructureResidencyPolicy.Disabled,
                dynamicPolicy,
                requirement,
                sceneContentRevision,
                _rayQueryHasAlphaCandidateGeometry,
                _rayQueryHasThinTransmissionGeometry,
                _preparedInstanceScratch.ToArray());
            _preparedRayScene = prepared;

            int dynamicCount = 0;
            int decalCount = 0;
            int transparentCount = 0;
            for (int i = 0; i < prepared.Instances.Length; i++)
            {
                StaticOpaqueInstance instance = prepared.Instances[i];
                if (instance.UsesDynamicBlas)
                    dynamicCount++;
                if (instance.GeometryClass == DdgiRayGeometryClass.DecalOverlay)
                    decalCount++;
                if ((instance.GeometryFlags & (DdgiRayGeometryFlags.AlphaBlend |
                    DdgiRayGeometryFlags.ThinTransmission |
                    DdgiRayGeometryFlags.VolumeTransmission)) != 0)
                    transparentCount++;
            }

            return new RaySceneBuildPlan(
                enabled && Supported,
                prepared.Instances.Length,
                dynamicCount,
                transparentCount,
                decalCount,
                frameIndex,
                sceneContentRevision);
        }

        /// <summary>
        /// Records static/dynamic BLAS work, the BLAS-to-TLAS barrier, the TLAS
        /// transaction, and metadata publication. Call only after skinning and
        /// proxy-generation compute writes have completed.
        /// </summary>
        public AccelerationStructureFrameStats RecordDynamicRaySceneBuilds(
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle skinnedVertexBuffer,
            GpuTimestampRecorder? gpuTimestamps = null,
            int frameIndex = 0)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException(
                    "A valid command buffer is required to build acceleration structures.",
                    nameof(commandBuffer));
            PreparedRayScene prepared = _preparedRayScene ??
                throw new InvalidOperationException(
                    "PrepareFrameRayScene must run before dynamic ray-scene build recording.");
            _preparedRayScene = null;
            if (prepared.FrameIndex != frameIndex)
                throw new InvalidOperationException("The prepared ray scene belongs to a different frame slot.");

            return RecordPreparedFrame(
                prepared,
                stagingRing,
                commandBuffer,
                skinnedVertexBuffer,
                gpuTimestamps);
        }

        private AccelerationStructureFrameStats RecordPreparedFrame(
            PreparedRayScene prepared,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            BufferHandle skinnedVertexBuffer,
            GpuTimestampRecorder? gpuTimestamps)
        {
            bool enabled = prepared.Enabled;
            int frameIndex = prepared.FrameIndex;
            ulong sceneContentRevision = prepared.SceneContentRevision;
            SelectTopLevelFrameSlot(frameIndex);

            long buildStart = Stopwatch.GetTimestamp();
            TopLevelInstanceCount = 0;
            ResetFrameDiagnostics();
            BeginFrameResourceRetirement();
            _residencyPolicy = prepared.ResidencyPolicy;
            ValidateCompactionFrameIndex(frameIndex);
            _blasCompactionQueryPoolResetThisFrame[frameIndex] = false;

            // BeginFrame has already observed this frame slot's fence. Query
            // results are therefore read without VK_QUERY_RESULT_WAIT_BIT and
            // cannot introduce a hidden CPU/GPU synchronization point.
            if (Supported)
                ResolveCompletedBlasCompactionQueries(frameIndex);

            SynchronizeOpacityMicromapRuntimeRegistrations();
            ReconcileOpacityMicromapGpuRegistrations();
            ResolveCompletedOpacityMicromapGpuWork(frameIndex);

            if (!enabled)
            {
                _hasReusablePreparation = false;
                _lastFallbackReason = string.Empty;
                ClearPublishedRaySceneInstances();
                return CreateStats(false);
            }

            if (!Supported)
            {
                _hasReusablePreparation = false;
                ClearPublishedRaySceneInstances();
                return CreateStats(false);
            }

            try
            {
                bool meshBuffersChanged = InvalidateCachedStructuresIfMeshBuffersChanged();
                AccelerationStructurePreparationIdentity preparationIdentity =
                    CreatePreparationIdentity(
                        sceneContentRevision,
                        _residencyPolicy,
                        prepared.DynamicPolicy,
                        prepared.Requirement);
                bool hasDynamicBuilds = Array.Exists(
                    prepared.Instances,
                    static instance => instance.UsesDynamicBlas);
                // A stable scene can still have an OMM transaction that was
                // admitted after the ordinary BLAS/TLAS became reusable, or a
                // multi-frame build/compaction transaction that must advance.
                // Never let the fast reuse path starve that work indefinitely.
                bool pendingOpacityMicromapWork =
                    MayRecordOpacityMicromapGpuWork(prepared.Instances);
                if (ShouldReusePreparedRayScene(
                        hasDynamicBuilds,
                        meshBuffersChanged,
                        pendingOpacityMicromapWork,
                        CanReusePreparation(preparationIdentity)))
                {
                    // The scene builder's content revision covers object identity,
                    // visibility, transforms, meshes, materials, and static-batch
                    // revisions.  Preserve the already published TLAS rather than
                    // rescanning and rehashing the same scene every stable frame.
                    // Active BLAS ages still advance so a later residency-policy
                    // transition starts its eviction grace period at the real last
                    // use, not at the frame on which this preparation was cached.
                    TouchActiveBottomLevelAccelerationStructures();
                    TopLevelInstanceCount = _lastTlasInstanceCount;
                    _lastStaticInstanceCandidateCount = _cachedStaticInstanceCandidateCount;
                    _lastStaticInstanceResidentCount = _cachedStaticInstanceResidentCount;
                    _lastStaticInstanceCulledCount = _cachedStaticInstanceCulledCount;
                    _lastTlasSkipCount = 1;
                    _lastFallbackReason = string.Empty;
                    _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                    return CreateStats(Active);
                }

                _hasReusablePreparation = false;
                _rayQueryHasAlphaCandidateGeometry = false;
                _rayQueryHasThinTransmissionGeometry = false;
                _rayQueryHasAlphaCandidateGeometry = prepared.HasAlphaCandidateGeometry;
                _rayQueryHasThinTransmissionGeometry = prepared.HasThinTransmissionGeometry;
                _instanceScratch.Clear();
                _instanceScratch.AddRange(prepared.Instances);
                ApplyDynamicRaySceneBudget(
                    _instanceScratch,
                    prepared.DynamicPolicy,
                    skinnedVertexBuffer);
                ApplyResidencyPolicy(_instanceScratch);
                if (!ApplyMemoryResidencyPolicy(_instanceScratch))
                {
                    ClearPublishedRaySceneInstances();
                    _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                    return CreateStats(false);
                }
                if (_instanceScratch.Count == 0)
                {
                    _lastFallbackReason = "No opaque acceleration-structure instances were submitted.";
                    ClearPublishedRaySceneInstances();
                    return CreateStats(false);
                }

                BuildActiveMeshSet(_instanceScratch);
                PruneUnusedBottomLevelAccelerationStructures();

                bool missingBlas = HasMissingBottomLevelAccelerationStructures(_instanceScratch);
                bool dynamicBlasWork = _instanceScratch.Exists(
                    static instance => instance.UsesDynamicBlas);
                bool pendingBlasCompaction = _readyBlasCompactions.Count > 0;
                bool opacityMicromapWork =
                    MayRecordOpacityMicromapGpuWork(_instanceScratch);
                ulong additionalTlasBudgetReservation = 0;
                if (missingBlas)
                {
                    EnsureInstanceBufferCapacity(_instanceScratch.Count);
                    ulong estimatedTlasBytes = EstimateTopLevelAccelerationStructureBytes(_instanceScratch.Count);
                    additionalTlasBudgetReservation = CalculateAdditionalTopLevelReservation(
                        estimatedTlasBytes,
                        _tlas.Size);
                }
                bool ordinaryBlasWork =
                    missingBlas || dynamicBlasWork || pendingBlasCompaction;
                if (ordinaryBlasWork)
                    gpuTimestamps?.BeginPass(commandBuffer, frameIndex, "AccelerationStructureBlasPass");
                try
                {
                    ProcessReadyBlasCompactions(commandBuffer);
                    EnsureDynamicBottomLevelAccelerationStructures(
                        _instanceScratch,
                        skinnedVertexBuffer,
                        commandBuffer);
                    EnsureBottomLevelAccelerationStructures(
                        _instanceScratch,
                        commandBuffer,
                        additionalTlasBudgetReservation,
                        frameIndex);
                }
                finally
                {
                    if (ordinaryBlasWork)
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                }

                if (opacityMicromapWork)
                    gpuTimestamps?.BeginPass(
                        commandBuffer,
                        frameIndex,
                        "OpacityMicromapBuildPass");
                try
                {
                    RecordOpacityMicromapGpuWork(
                        _instanceScratch,
                        stagingRing,
                        commandBuffer,
                        frameIndex);
                }
                finally
                {
                    if (opacityMicromapWork)
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                }

                if (_unavailableMeshScratch.Count > 0)
                {
                    // A hole-punched TLAS is not a valid lighting representation:
                    // probe and shadow rays would pass through rasterized geometry.
                    // Keep the previous resource alive for in-flight work, but mark
                    // this frame inactive and retry transactionally next frame.
                    _lastFallbackReason =
                        "GI acceleration-structure admission could not build every mesh in the resolved resident set; no partial TLAS was published.";
                    _lastStaticInstanceCulledCount = checked(
                        _lastStaticInstanceCulledCount + _lastStaticInstanceResidentCount);
                    _lastStaticInstanceResidentCount = 0;
                    TopLevelInstanceCount = 0;
                    ClearPublishedRaySceneInstances();
                    _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                    return CreateStats(false);
                }

                ulong instanceSignature = CreateInstanceSignature(_instanceScratch);
                TopLevelAccelerationStructureBuildAction buildAction = SelectTopLevelBuildAction(
                    _tlas.Handle.Handle != 0,
                    _hasTlasInstanceSignature,
                    _lastTlasInstanceCount,
                    _lastTlasInstanceSignature,
                    _instanceScratch.Count,
                    instanceSignature);
                if (buildAction == TopLevelAccelerationStructureBuildAction.Skip)
                {
                    TopLevelInstanceCount = _instanceScratch.Count;
                    _lastTlasSkipCount = 1;
                }
                else
                {
                    gpuTimestamps?.BeginPass(commandBuffer, frameIndex, "AccelerationStructureTlasPass");
                    try
                    {
                        BuildTopLevelAccelerationStructure(_instanceScratch, stagingRing, commandBuffer, buildAction, instanceSignature);
                    }
                    finally
                    {
                        gpuTimestamps?.EndPass(commandBuffer, frameIndex);
                    }
                }

                _lastFallbackReason = string.Empty;
                CacheReusablePreparation(preparationIdentity);
                PublishRaySceneInstances(
                    _instanceScratch,
                    sceneContentRevision,
                    instanceSignature);
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                return CreateStats(Active);
            }
            catch (Exception ex) when (ex is VulkanException or InvalidOperationException or ArgumentException or OverflowException)
            {
                _hasReusablePreparation = false;
                _lastFallbackReason = ex.Message;
                TopLevelInstanceCount = 0;
                ClearPublishedRaySceneInstances();
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                return CreateStats(false);
            }
        }

        internal void CollectStaticOpaqueInstances(
            Scene scene,
            List<StaticOpaqueInstance> instances,
            bool alphaMaskedTransportEnabled = true,
            DdgiTransparentGeometryMode transparentGeometryMode = DdgiTransparentGeometryMode.MaskAndThin,
            bool geometryDecalsEnabled = false,
            DdgiSkinnedGeometryMode skinnedGeometryMode = DdgiSkinnedGeometryMode.ConservativeProxy,
            int frameSlot = 0,
            DdgiFoliageGeometryMode foliageGeometryMode = DdgiFoliageGeometryMode.Excluded,
            DdgiFoliageProxyFrame? foliageProxyFrame = null)
        {
            instances.Clear();

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible || !renderObject.Enabled)
                    continue;
                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                AccelerationStructureGeometryDomain requestedDomain = renderObject.IsStatic
                    ? AccelerationStructureGeometryDomain.Static
                    : AccelerationStructureGeometryDomain.Dynamic;
                if (renderObject is SkinnedRenderObject)
                    requestedDomain = AccelerationStructureGeometryDomain.Skinned;
                if (!TryGetRayQueryMesh(
                    meshHandle,
                    renderObject.Material,
                    renderObject.Name,
                    requestedDomain,
                    out MeshInfo meshInfo,
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    out RayQueryMaterialContract materialContract,
                    alphaMaskedTransportEnabled,
                    transparentGeometryMode,
                    geometryDecalsEnabled,
                    skinnedGeometryMode,
                    foliageGeometryMode))
                    continue;

                AccelerationStructureGeometryDomain domain = meshInfo.IsSkinned
                    ? AccelerationStructureGeometryDomain.Skinned
                    : requestedDomain;

                uint stableIdentity = StableInstanceIdentity(renderObject.Id);
                StaticOpaqueInstance instance = new StaticOpaqueInstance(
                    meshHandle,
                    meshInfo,
                    materialIndex,
                    renderObject.WorldMatrix,
                    domain,
                    instanceFlags)
                {
                    ObjectIdentity = renderObject.Id,
                    StableInstanceIdentity = stableIdentity,
                    MaterialRevision = materialContract.MaterialRevision,
                    TransformRevision = renderObject.Revision,
                    PackedAlpha = materialContract.PackedAlpha,
                    PackedDecalLayerAndOrder = WithStableDecalOrder(
                        materialContract.PackedDecalLayerAndOrder,
                        stableIdentity),
                    DecalDepthTolerance = materialContract.DecalDepthTolerance,
                    DecalDepthBias = materialContract.DecalDepthBias,
                    GeometryClass = materialContract.ResolveGeometryClass(domain),
                    GeometryFlags = materialContract.GeometryFlags,
                    FrameSlot = frameSlot
                };
                if (renderObject is SkinnedRenderObject skinned &&
                    skinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose &&
                    skinned.SkinningEnabled)
                {
                    instance = instance with
                    {
                        UsesDynamicBlas = true,
                        GeometryClass = DdgiRayGeometryClass.SkinnedCurrentPose,
                        GeometryFlags = instance.GeometryFlags |
                            DdgiRayGeometryFlags.DynamicVertexSource,
                        VertexBufferIndex = checked((uint)(BindlessIndex.SkinnedVertexBufferBase + frameSlot)),
                        VertexOffset = skinned.SkinnedVertexOffset,
                        VertexStride = checked((uint)Marshal.SizeOf<GPUVertex>()),
                        VertexFormat = DdgiRayVertexFormat.InterleavedGpuVertex,
                        PositionOffset = 0u,
                        NormalOffset = 16u,
                        TangentOffset = 48u,
                        TexCoord0Offset = 32u,
                        TexCoord1Offset = 40u,
                        ColorOffset = 64u,
                        RepresentationGeneration =
                            FoldContentGeneration(
                                skinned.Animator?.PoseRevision ?? 1UL)
                    };
                }
                instances.Add(instance);
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                if (!batch.Visible)
                    continue;
                if (batch.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;
                if (!TryGetRayQueryMesh(
                    meshHandle,
                    batch.Material,
                    batch.Name,
                    AccelerationStructureGeometryDomain.Static,
                    out MeshInfo meshInfo,
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    out RayQueryMaterialContract materialContract,
                    alphaMaskedTransportEnabled,
                    transparentGeometryMode,
                    geometryDecalsEnabled,
                    skinnedGeometryMode,
                    foliageGeometryMode))
                    continue;

                IReadOnlyList<CoreMatrix4x4> worldMatrices = batch.WorldMatrices;
                for (int i = 0; i < worldMatrices.Count; i++)
                {
                    uint stableIdentity = StableInstanceIdentity(batch.Id, checked((uint)i));
                    instances.Add(new StaticOpaqueInstance(
                        meshHandle,
                        meshInfo,
                        materialIndex,
                        worldMatrices[i],
                        AccelerationStructureGeometryDomain.Static,
                        instanceFlags)
                    {
                        ObjectIdentity = batch.Id,
                        StableInstanceIdentity = stableIdentity,
                        MaterialRevision = materialContract.MaterialRevision,
                        TransformRevision =
                            ((ulong)batch.Revision << 32) |
                            checked((uint)i + 1u),
                        PackedAlpha = materialContract.PackedAlpha,
                        PackedDecalLayerAndOrder = WithStableDecalOrder(
                            materialContract.PackedDecalLayerAndOrder,
                            stableIdentity),
                        DecalDepthTolerance = materialContract.DecalDepthTolerance,
                        DecalDepthBias = materialContract.DecalDepthBias,
                        GeometryClass = materialContract.ResolveGeometryClass(
                            AccelerationStructureGeometryDomain.Static),
                        GeometryFlags = materialContract.GeometryFlags,
                        FrameSlot = frameSlot
                    });
                }
            }

            CollectFoliageProxyInstances(
                foliageProxyFrame,
                instances,
                alphaMaskedTransportEnabled,
                transparentGeometryMode,
                foliageGeometryMode,
                frameSlot);
        }

        private void CollectFoliageProxyInstances(
            DdgiFoliageProxyFrame? foliageProxyFrame,
            List<StaticOpaqueInstance> instances,
            bool alphaMaskedTransportEnabled,
            DdgiTransparentGeometryMode transparentGeometryMode,
            DdgiFoliageGeometryMode foliageGeometryMode,
            int frameSlot)
        {
            if (foliageProxyFrame == null ||
                foliageGeometryMode == DdgiFoliageGeometryMode.Excluded)
                return;
            if (foliageProxyFrame.FrameSlot != frameSlot)
            {
                throw new InvalidOperationException(
                    "The DDGI foliage proxy frame belongs to a different in-flight slot.");
            }

            IReadOnlyList<DdgiFoliageProxyInstance> foliageInstances =
                foliageProxyFrame.Instances;
            for (int index = 0; index < foliageInstances.Count; index++)
            {
                DdgiFoliageProxyInstance foliage = foliageInstances[index];
                if (foliage.Generated)
                {
                    CollectProceduralFoliageProxy(
                        foliage,
                        instances,
                        alphaMaskedTransportEnabled,
                        transparentGeometryMode,
                        foliageGeometryMode,
                        frameSlot);
                }
                else
                {
                    CollectAuthoredFoliageProxy(
                        foliage,
                        instances,
                        alphaMaskedTransportEnabled,
                        transparentGeometryMode,
                        foliageGeometryMode,
                        frameSlot);
                }
            }
        }

        private void CollectProceduralFoliageProxy(
            DdgiFoliageProxyInstance foliage,
            List<StaticOpaqueInstance> instances,
            bool alphaMaskedTransportEnabled,
            DdgiTransparentGeometryMode transparentGeometryMode,
            DdgiFoliageGeometryMode foliageGeometryMode,
            int frameSlot)
        {
            if (!foliage.VertexBuffer.IsValid ||
                !foliage.IndexBuffer.IsValid ||
                foliage.VertexCount == 0 ||
                foliage.IndexCount < 3)
            {
                return;
            }
            if (!TryGetRayQueryMaterial(
                    foliage.Material,
                    $"DDGI foliage proxy {foliage.PatchIdentity}",
                    isSkinned: false,
                    AccelerationStructureGeometryDomain.Foliage,
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    out RayQueryMaterialContract materialContract,
                    alphaMaskedTransportEnabled,
                    transparentGeometryMode,
                    geometryDecalsEnabled: false,
                    DdgiSkinnedGeometryMode.Excluded,
                    foliageGeometryMode))
            {
                return;
            }

            var meshInfo = new MeshInfo
            {
                BoundingBoxMin = new System.Numerics.Vector3(
                    foliage.WorldBounds.Min.X,
                    foliage.WorldBounds.Min.Y,
                    foliage.WorldBounds.Min.Z),
                BoundingBoxMax = new System.Numerics.Vector3(
                    foliage.WorldBounds.Max.X,
                    foliage.WorldBounds.Max.Y,
                    foliage.WorldBounds.Max.Z),
                VertexOffset = foliage.VertexOffset,
                VertexCount = foliage.VertexCount,
                IndexOffset = foliage.IndexOffset,
                IndexCount = foliage.IndexCount,
                HasTangents = true,
                HasUv1 = true,
                HasVertexColor = true
            };
            uint stableIdentity = StableInstanceIdentity(foliage.PatchIdentity);
            instances.Add(new StaticOpaqueInstance(
                foliage.SourceMesh,
                meshInfo,
                materialIndex,
                foliage.WorldMatrix,
                AccelerationStructureGeometryDomain.Foliage,
                instanceFlags)
            {
                ObjectIdentity = foliage.PatchIdentity,
                StableInstanceIdentity = stableIdentity,
                MaterialRevision = materialContract.MaterialRevision,
                PackedAlpha = materialContract.PackedAlpha,
                PackedDecalLayerAndOrder = WithStableDecalOrder(
                    materialContract.PackedDecalLayerAndOrder,
                    stableIdentity),
                DecalDepthTolerance = materialContract.DecalDepthTolerance,
                DecalDepthBias = materialContract.DecalDepthBias,
                RepresentationGeneration = foliage.RepresentationGeneration,
                GeometryClass = DdgiRayGeometryClass.ProceduralFoliageProxy,
                GeometryFlags = materialContract.GeometryFlags |
                    DdgiRayGeometryFlags.Foliage |
                    DdgiRayGeometryFlags.DynamicVertexSource,
                VertexBufferIndex = foliage.VertexBufferIndex,
                VertexOffset = foliage.VertexOffset,
                VertexStride = checked((uint)Marshal.SizeOf<GPUVertex>()),
                VertexFormat = DdgiRayVertexFormat.InterleavedFoliageProxy,
                PositionOffset = 0u,
                NormalOffset = 16u,
                TangentOffset = 48u,
                TexCoord0Offset = 32u,
                TexCoord1Offset = 40u,
                ColorOffset = 64u,
                IndexBufferIndex = foliage.IndexBufferIndex,
                IndexOffset = foliage.IndexOffset,
                GeometryVertexBuffer = foliage.VertexBuffer,
                GeometryIndexBuffer = foliage.IndexBuffer,
                UsesDynamicBlas = true,
                FrameSlot = frameSlot
            });
        }

        private void CollectAuthoredFoliageProxy(
            DdgiFoliageProxyInstance foliage,
            List<StaticOpaqueInstance> instances,
            bool alphaMaskedTransportEnabled,
            DdgiTransparentGeometryMode transparentGeometryMode,
            DdgiFoliageGeometryMode foliageGeometryMode,
            int frameSlot)
        {
            if (!foliage.SourceMesh.IsValid ||
                !TryGetRayQueryMesh(
                    foliage.SourceMesh,
                    foliage.Material,
                    $"DDGI authored foliage {foliage.PatchIdentity}",
                    AccelerationStructureGeometryDomain.Foliage,
                    out MeshInfo meshInfo,
                    out uint materialIndex,
                    out GeometryInstanceFlagsKHR instanceFlags,
                    out RayQueryMaterialContract materialContract,
                    alphaMaskedTransportEnabled,
                    transparentGeometryMode,
                    geometryDecalsEnabled: false,
                    DdgiSkinnedGeometryMode.Excluded,
                    foliageGeometryMode))
            {
                return;
            }

            uint stableIdentity = StableInstanceIdentity(foliage.PatchIdentity);
            instances.Add(new StaticOpaqueInstance(
                foliage.SourceMesh,
                meshInfo,
                materialIndex,
                foliage.WorldMatrix,
                AccelerationStructureGeometryDomain.Foliage,
                instanceFlags)
            {
                ObjectIdentity = foliage.PatchIdentity,
                StableInstanceIdentity = stableIdentity,
                MaterialRevision = materialContract.MaterialRevision,
                PackedAlpha = materialContract.PackedAlpha,
                PackedDecalLayerAndOrder = WithStableDecalOrder(
                    materialContract.PackedDecalLayerAndOrder,
                    stableIdentity),
                DecalDepthTolerance = materialContract.DecalDepthTolerance,
                DecalDepthBias = materialContract.DecalDepthBias,
                RepresentationGeneration = foliage.RepresentationGeneration,
                GeometryClass = DdgiRayGeometryClass.AuthoredFoliage,
                GeometryFlags = materialContract.GeometryFlags |
                    DdgiRayGeometryFlags.Foliage,
                FrameSlot = frameSlot
            });
        }

        /// <summary>
        /// Keeps dynamic objects authoritative while selecting a stable, nearest-first
        /// working set of static batches.  Static geometry is the streamable domain;
        /// a normal render object is deliberately never silently culled from the
        /// detailed ray-query representation by this policy.
        /// </summary>
        private void ApplyResidencyPolicy(List<StaticOpaqueInstance> instances)
        {
            _lastStaticInstanceCandidateCount = 0;
            _lastStaticInstanceResidentCount = 0;
            _lastStaticInstanceCulledCount = 0;

            if (!_residencyPolicy.Enabled)
            {
                for (int i = 0; i < instances.Count; i++)
                {
                    if (instances[i].Domain == AccelerationStructureGeometryDomain.Static)
                        _lastStaticInstanceCandidateCount++;
                }

                _lastStaticInstanceResidentCount = _lastStaticInstanceCandidateCount;
                return;
            }

            if (!RequiresStaticResidencySelection(_residencyPolicy))
            {
                // High-quality tiers keep the complete detailed ray-query set:
                // distance is unbounded and the instance cap is effectively open.
                // Counting still preserves diagnostics, but world-bound transforms
                // and an O(N log N) nearest-first sort cannot change admission.
                for (int i = 0; i < instances.Count; i++)
                {
                    if (instances[i].Domain == AccelerationStructureGeometryDomain.Static)
                        _lastStaticInstanceCandidateCount++;
                }

                _lastStaticInstanceResidentCount = _lastStaticInstanceCandidateCount;
                return;
            }

            _residentInstanceScratch.Clear();
            _staticResidencyCandidateScratch.Clear();
            float residentDistance = Math.Max(0.0f, _residencyPolicy.StaticResidentDistance);
            float residentDistanceSquared = residentDistance >= MathF.Sqrt(float.MaxValue)
                ? float.MaxValue
                : residentDistance * residentDistance;

            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain != AccelerationStructureGeometryDomain.Static)
                {
                    _residentInstanceScratch.Add(instance);
                    continue;
                }

                _lastStaticInstanceCandidateCount++;
                float distanceSquared = DistanceSquaredToBounds(
                    _residencyPolicy.CameraPosition,
                    GetInstanceWorldBounds(instance));
                if (!float.IsFinite(distanceSquared) || distanceSquared > residentDistanceSquared)
                {
                    _lastStaticInstanceCulledCount++;
                    continue;
                }

                _staticResidencyCandidateScratch.Add(new StaticResidencyCandidate(instance, distanceSquared));
            }

            _staticResidencyCandidateScratch.Sort(CompareStaticResidencyCandidates);
            int staticResidentCount = Math.Min(
                _staticResidencyCandidateScratch.Count,
                Math.Max(0, _residencyPolicy.MaximumStaticInstances));
            for (int i = 0; i < staticResidentCount; i++)
                _residentInstanceScratch.Add(_staticResidencyCandidateScratch[i].Instance);

            _lastStaticInstanceResidentCount = staticResidentCount;
            _lastStaticInstanceCulledCount += _staticResidencyCandidateScratch.Count - staticResidentCount;
            instances.Clear();
            instances.AddRange(_residentInstanceScratch);
        }

        internal static bool RequiresStaticResidencySelection(
            AccelerationStructureResidencyPolicy policy)
        {
            if (!policy.Enabled)
                return false;

            float distance = Math.Max(0.0f, policy.StaticResidentDistance);
            bool boundedDistance = float.IsFinite(distance) &&
                distance < MathF.Sqrt(float.MaxValue);
            bool boundedInstanceCount = policy.MaximumStaticInstances < int.MaxValue;
            return boundedDistance || boundedInstanceCount;
        }

        /// <summary>
        /// Resolves the memory cap before any BLAS allocation. High-quality tiers
        /// require the entire distance/count-resident set; constrained tiers may
        /// trim only a coherent nearest-first static tail, which the far-field
        /// representation owns. Dynamic/skinned instances are never memory-culled.
        /// </summary>
        private bool ApplyMemoryResidencyPolicy(List<StaticOpaqueInstance> instances)
        {
            if (!_residencyPolicy.Enabled || instances.Count == 0)
                return true;

            EnsureInstanceBufferCapacity(instances.Count);
            ulong topLevelReservation = EstimateTopLevelAccelerationStructureBytes(instances.Count);
            ulong requiredWorkingSet = EstimateResidentWorkingSetBytes(
                instances,
                topLevelReservation,
                out int missingMeshCount);
            ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
            if (requiredWorkingSet <= budgetBytes)
                return true;

            if (!_residencyPolicy.AllowStaticMemoryCulling)
            {
                _lastBlasBudgetRejectedCount = Math.Max(1, missingMeshCount);
                _lastStaticInstanceCulledCount = checked(
                    _lastStaticInstanceCulledCount + _lastStaticInstanceResidentCount);
                _lastStaticInstanceResidentCount = 0;
                _lastFallbackReason =
                    $"The complete GI ray-query resident set requires at least {requiredWorkingSet} bytes, " +
                    $"but the active quality tier provides {budgetBytes} bytes; no partial TLAS was published.";
                TopLevelInstanceCount = 0;
                return false;
            }

            _memoryResidentInstanceScratch.Clear();
            _budgetMeshScratch.Clear();
            ulong selectedBytes = topLevelReservation;

            // ApplyResidencyPolicy emits mandatory non-static instances first.
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain == AccelerationStructureGeometryDomain.Static)
                    continue;

                ulong additionalBytes = ResolveUniqueMeshAdmissionBytes(instance);
                if (WouldExceedBudget(selectedBytes, additionalBytes, budgetBytes))
                {
                    _lastBlasBudgetRejectedCount = Math.Max(1, missingMeshCount);
                    _lastFallbackReason =
                        "The GI acceleration-structure budget cannot represent the mandatory dynamic/skinned resident set; no partial TLAS was published.";
                    TopLevelInstanceCount = 0;
                    return false;
                }

                selectedBytes = checked(selectedBytes + additionalBytes);
                _memoryResidentInstanceScratch.Add(instance);
            }

            bool staticTailCulled = false;
            int selectedStaticCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.Domain != AccelerationStructureGeometryDomain.Static)
                    continue;

                if (staticTailCulled)
                    continue;

                ulong additionalBytes = ResolveUniqueMeshAdmissionBytes(instance);
                if (WouldExceedBudget(selectedBytes, additionalBytes, budgetBytes))
                {
                    // Stop at the first non-fitting nearest-first candidate. Skipping
                    // holes and admitting cheaper distant meshes would make ray-scene
                    // topology depend on mesh allocation size rather than distance.
                    staticTailCulled = true;
                    continue;
                }

                selectedBytes = checked(selectedBytes + additionalBytes);
                selectedStaticCount++;
                _memoryResidentInstanceScratch.Add(instance);
            }

            int memoryCulledStaticCount = Math.Max(0, _lastStaticInstanceResidentCount - selectedStaticCount);
            _lastStaticInstanceResidentCount = selectedStaticCount;
            _lastStaticInstanceCulledCount = checked(_lastStaticInstanceCulledCount + memoryCulledStaticCount);
            instances.Clear();
            instances.AddRange(_memoryResidentInstanceScratch);
            if (instances.Count > 0)
                return true;

            _lastFallbackReason =
                "The GI acceleration-structure budget cannot hold the nearest static resident mesh; no partial TLAS was published.";
            return false;
        }

        private ulong EstimateResidentWorkingSetBytes(
            IReadOnlyList<StaticOpaqueInstance> instances,
            ulong topLevelReservation,
            out int missingMeshCount)
        {
            _budgetMeshScratch.Clear();
            ulong bytes = topLevelReservation;
            missingMeshCount = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (instance.UsesDynamicBlas)
                    continue;
                if (!_budgetMeshScratch.Add(instance.Mesh))
                    continue;

                if (_blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas))
                {
                    bytes = SaturatingAdd(bytes, cachedBlas.Size);
                    continue;
                }

                bytes = SaturatingAdd(bytes, EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo));
                missingMeshCount++;
            }

            return bytes;
        }

        private ulong ResolveUniqueMeshAdmissionBytes(StaticOpaqueInstance instance)
        {
            if (instance.UsesDynamicBlas)
                return 0;
            if (!_budgetMeshScratch.Add(instance.Mesh))
                return 0;
            return _blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas)
                ? cachedBlas.Size
                : EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo);
        }

        private static int CompareStaticResidencyCandidates(
            StaticResidencyCandidate left,
            StaticResidencyCandidate right)
        {
            int comparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (comparison != 0)
                return comparison;

            comparison = left.Instance.Mesh.Index.CompareTo(right.Instance.Mesh.Index);
            if (comparison != 0)
                return comparison;
            comparison = left.Instance.Mesh.Generation.CompareTo(right.Instance.Mesh.Generation);
            if (comparison != 0)
                return comparison;
            comparison = left.Instance.MaterialIndex.CompareTo(right.Instance.MaterialIndex);
            if (comparison != 0)
                return comparison;

            CoreMatrix4x4 leftMatrix = left.Instance.WorldMatrix;
            CoreMatrix4x4 rightMatrix = right.Instance.WorldMatrix;
            comparison = leftMatrix.M41.CompareTo(rightMatrix.M41);
            if (comparison != 0)
                return comparison;
            comparison = leftMatrix.M42.CompareTo(rightMatrix.M42);
            if (comparison != 0)
                return comparison;
            return leftMatrix.M43.CompareTo(rightMatrix.M43);
        }

        private static CoreBoundingBox GetInstanceWorldBounds(StaticOpaqueInstance instance)
        {
            CoreBoundingBox localBounds = new(
                new CoreVector3(
                    instance.MeshInfo.BoundingBoxMin.X,
                    instance.MeshInfo.BoundingBoxMin.Y,
                    instance.MeshInfo.BoundingBoxMin.Z),
                new CoreVector3(
                    instance.MeshInfo.BoundingBoxMax.X,
                    instance.MeshInfo.BoundingBoxMax.Y,
                    instance.MeshInfo.BoundingBoxMax.Z));
            return CoreBoundingBox.Transform(localBounds, instance.WorldMatrix);
        }

        private static System.Numerics.Vector3
            ComputeCausticAbsorptionCoefficient(
                MaterialExtensionDefinition extensions)
        {
            if (extensions.TransmissionFactor <= 0.0f ||
                float.IsPositiveInfinity(extensions.AttenuationDistance))
            {
                return System.Numerics.Vector3.Zero;
            }
            if (!float.IsFinite(extensions.AttenuationDistance) ||
                extensions.AttenuationDistance <= 0.0f)
            {
                return new System.Numerics.Vector3(float.NaN);
            }

            System.Numerics.Vector3 color = new(
                extensions.AttenuationColor.X,
                extensions.AttenuationColor.Y,
                extensions.AttenuationColor.Z);
            if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) ||
                !float.IsFinite(color.Z) || color.X < 0.0f ||
                color.Y < 0.0f || color.Z < 0.0f ||
                color.X > 1.0f || color.Y > 1.0f || color.Z > 1.0f)
            {
                return new System.Numerics.Vector3(float.NaN);
            }

            const float minimumTransmittance = 1.0e-6f;
            float inverseDistance = 1.0f / extensions.AttenuationDistance;
            return new System.Numerics.Vector3(
                -MathF.Log(Math.Max(color.X, minimumTransmittance)) *
                    inverseDistance,
                -MathF.Log(Math.Max(color.Y, minimumTransmittance)) *
                    inverseDistance,
                -MathF.Log(Math.Max(color.Z, minimumTransmittance)) *
                    inverseDistance);
        }

        private void PublishRaySceneInstances(
            IReadOnlyList<StaticOpaqueInstance> instances,
            ulong sceneContentRevision,
            ulong topLevelInstanceSignature)
        {
            _publishedRaySceneInstances = instances.ToArray();
            _publishedRaySceneBoundsValid = false;
            for (int index = 0; index < _publishedRaySceneInstances.Length; index++)
            {
                CoreBoundingBox instanceBounds =
                    GetInstanceWorldBounds(_publishedRaySceneInstances[index]);
                if (!IsFinite(instanceBounds.Min) || !IsFinite(instanceBounds.Max))
                    continue;
                if (!_publishedRaySceneBoundsValid)
                {
                    _publishedRaySceneBounds = instanceBounds;
                    _publishedRaySceneBoundsValid = true;
                    continue;
                }

                _publishedRaySceneBounds.Min = CoreVector3.Min(
                    _publishedRaySceneBounds.Min,
                    instanceBounds.Min);
                _publishedRaySceneBounds.Max = CoreVector3.Max(
                    _publishedRaySceneBounds.Max,
                    instanceBounds.Max);
            }
            _publishedRaySceneContentRevision = sceneContentRevision;
            _publishedTlasInstanceSignature = topLevelInstanceSignature;
        }

        private void ClearPublishedRaySceneInstances()
        {
            _publishedRaySceneInstances = Array.Empty<StaticOpaqueInstance>();
            _publishedRaySceneBounds = default;
            _publishedRaySceneBoundsValid = false;
            _publishedRaySceneContentRevision = 0UL;
            _publishedTlasInstanceSignature = 0UL;
        }

        private static float DistanceSquaredToBounds(CoreVector3 position, CoreBoundingBox bounds)
        {
            float x = position.X < bounds.Min.X
                ? bounds.Min.X - position.X
                : position.X > bounds.Max.X ? position.X - bounds.Max.X : 0.0f;
            float y = position.Y < bounds.Min.Y
                ? bounds.Min.Y - position.Y
                : position.Y > bounds.Max.Y ? position.Y - bounds.Max.Y : 0.0f;
            float z = position.Z < bounds.Min.Z
                ? bounds.Min.Z - position.Z
                : position.Z > bounds.Max.Z ? position.Z - bounds.Max.Z : 0.0f;
            return x * x + y * y + z * z;
        }

        private void BuildActiveMeshSet(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            _activeMeshScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                if (!instances[i].UsesDynamicBlas)
                    _activeMeshScratch.Add(instances[i].Mesh);
            }
        }

        private bool TryGetRayQueryMesh(
            MeshHandle meshHandle,
            object? material,
            string? ownerName,
            AccelerationStructureGeometryDomain domain,
            out MeshInfo meshInfo,
            out uint materialIndex,
            out GeometryInstanceFlagsKHR instanceFlags,
            out RayQueryMaterialContract materialContract,
            bool alphaMaskedTransportEnabled,
            DdgiTransparentGeometryMode transparentGeometryMode,
            bool geometryDecalsEnabled,
            DdgiSkinnedGeometryMode skinnedGeometryMode,
            DdgiFoliageGeometryMode foliageGeometryMode =
                DdgiFoliageGeometryMode.Excluded)
        {
            meshInfo = default;
            materialIndex = 0;
            instanceFlags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr;
            materialContract = default;
            try
            {
                meshInfo = _meshManager.GetMeshInfo(meshHandle);
                if (meshInfo.VertexCount == 0 || meshInfo.IndexCount < 3)
                    return false;

                return TryGetRayQueryMaterial(
                    material,
                    ownerName,
                    meshInfo.IsSkinned,
                    domain,
                    out materialIndex,
                    out instanceFlags,
                    out materialContract,
                    alphaMaskedTransportEnabled,
                    transparentGeometryMode,
                    geometryDecalsEnabled,
                    skinnedGeometryMode,
                    foliageGeometryMode);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool IsFinite(CoreVector3 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z);

        private bool TryGetRayQueryMaterial(
            object? material,
            string? ownerName,
            bool isSkinned,
            AccelerationStructureGeometryDomain domain,
            out uint materialIndex,
            out GeometryInstanceFlagsKHR instanceFlags,
            out RayQueryMaterialContract materialContract,
            bool alphaMaskedTransportEnabled,
            DdgiTransparentGeometryMode transparentGeometryMode,
            bool geometryDecalsEnabled,
            DdgiSkinnedGeometryMode skinnedGeometryMode,
            DdgiFoliageGeometryMode foliageGeometryMode)
        {
            materialIndex = 0;
            instanceFlags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr;
            materialContract = default;
            try
            {
                MaterialHandle materialHandle =
                    SceneDataBuilder.ResolveRenderObjectMaterialHandle(
                        material,
                        _materialManager.DefaultMaterialHandle,
                        ownerName ?? string.Empty);
                MaterialRenderMetadata metadata =
                    _materialManager.GetMaterialMetadata(materialHandle);
                GPUMaterialData materialData =
                    _materialManager.GetMaterialData(materialHandle);
                DdgiAccelerationStructureGeometryPolicy policy =
                    ResolveGeometryPolicy(
                        isSkinned,
                        metadata.RenderMode,
                        metadata.IsGeometryDecal,
                        domain,
                        metadata.DoubleSided,
                        metadata.TransmissionPolicy,
                        transparentGeometryMode,
                        geometryDecalsEnabled,
                        skinnedGeometryMode,
                        foliageGeometryMode);
                if (!policy.Include)
                    return false;

                materialIndex = checked((uint)Math.Max(materialHandle.Index, 0));
                instanceFlags = policy.InstanceFlags;
                materialContract = RayQueryMaterialContract.Create(
                    metadata,
                    materialData,
                    policy.VisibilityPolicy);
                bool alphaTested =
                    policy.VisibilityPolicy ==
                        DdgiAccelerationStructureVisibilityPolicy.AlphaMaskTested ||
                    policy.VisibilityPolicy ==
                        DdgiAccelerationStructureVisibilityPolicy.SkinnedAlphaMaskTestedProxy;
                _rayQueryHasAlphaCandidateGeometry |=
                    alphaTested && alphaMaskedTransportEnabled ||
                    policy.VisibilityPolicy ==
                        DdgiAccelerationStructureVisibilityPolicy.StochasticAlphaBlend;
                _rayQueryHasThinTransmissionGeometry |=
                    policy.VisibilityPolicy ==
                        DdgiAccelerationStructureVisibilityPolicy.ThinSurfaceCandidateTested;
                if (alphaTested && !alphaMaskedTransportEnabled)
                    instanceFlags |= GeometryInstanceFlagsKHR.ForceOpaqueBitKhr;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private void ApplyDynamicRaySceneBudget(
            List<StaticOpaqueInstance> instances,
            DdgiDynamicRayScenePolicy policy,
            BufferHandle skinnedVertexBuffer)
        {
            _dynamicAdmissionScratch.Clear();
            _activeDynamicObjectScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                if (!instances[i].UsesDynamicBlas)
                    continue;
                _dynamicAdmissionScratch.Add(i);
                _activeDynamicObjectScratch.Add(instances[i].ObjectIdentity);
            }

            PruneInactiveDynamicBottomLevelAccelerationStructures();
            if (_dynamicAdmissionScratch.Count == 0)
                return;

            _dynamicAdmissionScratch.Sort((leftIndex, rightIndex) =>
            {
                StaticOpaqueInstance left = instances[leftIndex];
                StaticOpaqueInstance right = instances[rightIndex];
                float leftDistance = DistanceSquaredToBounds(
                    _residencyPolicy.CameraPosition,
                    GetInstanceWorldBounds(left));
                float rightDistance = DistanceSquaredToBounds(
                    _residencyPolicy.CameraPosition,
                    GetInstanceWorldBounds(right));
                int comparison = leftDistance.CompareTo(rightDistance);
                return comparison != 0
                    ? comparison
                    : left.StableInstanceIdentity.CompareTo(right.StableInstanceIdentity);
            });

            int admittedBuilds = 0;
            ulong admittedPrimitives = 0;
            ulong plannedAdditionalStorage = 0;
            for (int candidate = 0; candidate < _dynamicAdmissionScratch.Count; candidate++)
            {
                int instanceIndex = _dynamicAdmissionScratch[candidate];
                StaticOpaqueInstance instance = instances[instanceIndex];
                uint primitiveCount = instance.MeshInfo.IndexCount / 3u;
                BufferHandle dynamicVertexBuffer =
                    ResolveDynamicVertexBuffer(instance, skinnedVertexBuffer);
                BufferHandle dynamicIndexBuffer =
                    ResolveDynamicIndexBuffer(instance);
                bool canAttempt =
                    dynamicVertexBuffer.IsValid &&
                    dynamicIndexBuffer.IsValid &&
                    primitiveCount > 0 &&
                    admittedBuilds < policy.EffectiveMaximumBuildsPerFrame &&
                    admittedPrimitives + primitiveCount <=
                        (ulong)policy.EffectiveMaximumPrimitivesPerFrame;

                ulong requiredStorage = 0;
                ulong requiredScratch = 0;
                if (canAttempt)
                {
                    AccelerationStructureGeometryKHR geometry =
                        CreateDynamicBottomLevelGeometry(instance, skinnedVertexBuffer);
                    AccelerationStructureBuildGeometryInfoKHR buildInfo =
                        CreateDynamicBottomLevelBuildInfo(
                            &geometry,
                            default,
                            default,
                            0,
                            BuildAccelerationStructureModeKHR.BuildKhr);
                    AccelerationStructureBuildSizesInfoKHR sizes =
                        QueryBuildSizes(buildInfo, primitiveCount);
                    DynamicBlasKey key = CreateDynamicBlasKey(instance);
                    bool hasCompatible = _dynamicBlasPool.TryGetValue(
                        key,
                        out DynamicBottomLevelAccelerationStructure? existing) &&
                        DynamicBuildContractMatches(existing, instance, primitiveCount);
                    requiredStorage = hasCompatible
                        ? 0UL
                        : Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
                    requiredScratch = hasCompatible && sizes.UpdateScratchSize > 0
                        ? sizes.UpdateScratchSize
                        : sizes.BuildScratchSize;
                    canAttempt =
                        requiredScratch <= policy.EffectiveDynamicScratchBudgetBytes &&
                        !WouldExceedBudget(
                            _dynamicBlasBytes,
                            checked(plannedAdditionalStorage + requiredStorage),
                            policy.EffectiveDynamicStorageBudgetBytes);
                }

                if (!canAttempt)
                {
                    if (instance.GeometryClass ==
                        DdgiRayGeometryClass.ProceduralFoliageProxy)
                    {
                        instances[instanceIndex] = instance with
                        {
                            UsesDynamicBlas = false,
                            GeometryClass = DdgiRayGeometryClass.Invalid
                        };
                        _lastDynamicBlasExcludedCount++;
                    }
                    else
                    {
                        instances[instanceIndex] =
                            CreateConservativeSkinnedProxy(instance);
                        _lastDynamicBlasProxyFallbackCount++;
                    }
                    _lastDynamicBlasBudgetDeferredCount++;
                    continue;
                }

                admittedBuilds++;
                admittedPrimitives = checked(admittedPrimitives + primitiveCount);
                plannedAdditionalStorage = checked(plannedAdditionalStorage + requiredStorage);
                _lastDynamicBlasScratchBytes = Math.Max(
                    _lastDynamicBlasScratchBytes,
                    requiredScratch);
            }

            instances.RemoveAll(static instance =>
                instance.GeometryClass == DdgiRayGeometryClass.Invalid);
        }

        private static bool DynamicBuildContractMatches(
            DynamicBottomLevelAccelerationStructure existing,
            StaticOpaqueInstance instance,
            uint primitiveCount) =>
            existing.VertexCount == instance.MeshInfo.VertexCount &&
            existing.PrimitiveCount == primitiveCount &&
            existing.VertexStride == instance.VertexStride &&
            existing.VertexFormat == instance.VertexFormat &&
            existing.InstanceFlags == instance.InstanceFlags;

        private static StaticOpaqueInstance CreateConservativeSkinnedProxy(
            StaticOpaqueInstance instance) =>
            instance with
            {
                UsesDynamicBlas = false,
                GeometryClass = DdgiRayGeometryClass.ConservativeProxy,
                GeometryFlags =
                    (instance.GeometryFlags & ~DdgiRayGeometryFlags.DynamicVertexSource) |
                    DdgiRayGeometryFlags.ConservativeProxy,
                VertexBufferIndex = BindlessIndex.VertexPositionBuffer,
                VertexOffset = instance.MeshInfo.VertexOffset,
                VertexStride = checked((uint)VertexPositionStride),
                VertexFormat = DdgiRayVertexFormat.SplitStatic,
                PositionOffset = 0u,
                NormalOffset = 0u,
                TangentOffset = 16u,
                TexCoord0Offset = 0u,
                TexCoord1Offset = 8u,
                ColorOffset = 16u,
                RepresentationGeneration = 1u
            };

        private void PruneInactiveDynamicBottomLevelAccelerationStructures()
        {
            if (_dynamicBlasPool.Count == 0)
                return;

            List<DynamicBlasKey>? removed = null;
            foreach (KeyValuePair<DynamicBlasKey, DynamicBottomLevelAccelerationStructure> pair
                     in _dynamicBlasPool)
            {
                if (_activeDynamicObjectScratch.Contains(pair.Key.ObjectIdentity))
                    continue;
                removed ??= new List<DynamicBlasKey>();
                removed.Add(pair.Key);
            }
            if (removed == null)
                return;

            for (int i = 0; i < removed.Count; i++)
            {
                DynamicBottomLevelAccelerationStructure resource =
                    _dynamicBlasPool[removed[i]];
                _dynamicBlasPool.Remove(removed[i]);
                RetireAccelerationStructureResource(
                    resource.Handle,
                    resource.StorageBuffer,
                    resource.Size,
                    AccelerationStructureRetirementOwner.Dynamic);
                _dynamicBlasBytes = _dynamicBlasBytes >= resource.Size
                    ? _dynamicBlasBytes - resource.Size
                    : 0;
                AdvanceResourceGeneration();
            }
            RecalculateAccelerationStructureBytes();
        }

        private void EnsureDynamicBottomLevelAccelerationStructures(
            List<StaticOpaqueInstance> instances,
            BufferHandle skinnedVertexBuffer,
            CommandBuffer commandBuffer)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                if (!instance.UsesDynamicBlas)
                    continue;

                DynamicBlasKey key = CreateDynamicBlasKey(instance);
                uint primitiveCount = instance.MeshInfo.IndexCount / 3u;
                AccelerationStructureGeometryKHR geometry =
                    CreateDynamicBottomLevelGeometry(instance, skinnedVertexBuffer);
                AccelerationStructureBuildGeometryInfoKHR queryInfo =
                    CreateDynamicBottomLevelBuildInfo(
                        &geometry,
                        default,
                        default,
                        0,
                        BuildAccelerationStructureModeKHR.BuildKhr);
                AccelerationStructureBuildSizesInfoKHR sizes =
                    QueryBuildSizes(queryInfo, primitiveCount);

                bool update = _dynamicBlasPool.TryGetValue(
                    key,
                    out DynamicBottomLevelAccelerationStructure? resource);
                if (update && !DynamicBuildContractMatches(resource!, instance, primitiveCount))
                {
                    _lastDynamicBlasTopologyMismatchCount++;
                    _dynamicBlasPool.Remove(key);
                    RetireAccelerationStructureResource(
                        resource!.Handle,
                        resource.StorageBuffer,
                        resource.Size,
                        AccelerationStructureRetirementOwner.Dynamic);
                    _dynamicBlasBytes = _dynamicBlasBytes >= resource.Size
                        ? _dynamicBlasBytes - resource.Size
                        : 0;
                    resource = null;
                    update = false;
                }

                ulong scratchSize = update && sizes.UpdateScratchSize > 0
                    ? sizes.UpdateScratchSize
                    : sizes.BuildScratchSize;
                EnsureScratchCapacity(scratchSize);
                _lastDynamicBlasScratchBytes = Math.Max(
                    _lastDynamicBlasScratchBytes,
                    scratchSize);

                if (!update)
                {
                    ulong storageSize = Math.Max(
                        MinResourceBufferSize,
                        sizes.AccelerationStructureSize);
                    BufferHandle storage = _bufferManager.CreateDeviceBuffer(
                        storageSize,
                        BufferUsageFlags.AccelerationStructureStorageBitKhr |
                            BufferUsageFlags.ShaderDeviceAddressBit,
                        requireDeviceAddress: true,
                        MemoryBudgetCategory.GlobalIllumination,
                        $"Dynamic BLAS {instance.StableInstanceIdentity:X8} Frame{instance.FrameSlot}");
                    AccelerationStructureKHR handle = default;
                    try
                    {
                        handle = CreateAccelerationStructure(
                            storage,
                            storageSize,
                            AccelerationStructureTypeKHR.BottomLevelKhr,
                            $"Dynamic BLAS {instance.StableInstanceIdentity:X8} Frame{instance.FrameSlot}");
                        resource = new DynamicBottomLevelAccelerationStructure(
                            handle,
                            storage,
                            storageSize,
                            instance.MeshInfo.VertexCount,
                            primitiveCount,
                            instance.VertexStride,
                            instance.VertexFormat,
                            instance.InstanceFlags,
                            instance.RepresentationGeneration);
                        _dynamicBlasPool.Add(key, resource);
                        _dynamicBlasBytes = checked(_dynamicBlasBytes + storageSize);
                        _peakDynamicBlasBytes = Math.Max(
                            _peakDynamicBlasBytes,
                            _dynamicBlasBytes);
                        AdvanceResourceGeneration();
                    }
                    catch
                    {
                        DestroyAccelerationStructureResource(handle, storage);
                        throw;
                    }
                }

                geometry = CreateDynamicBottomLevelGeometry(instance, skinnedVertexBuffer);
                AccelerationStructureBuildGeometryInfoKHR buildInfo =
                    CreateDynamicBottomLevelBuildInfo(
                        &geometry,
                        resource!.Handle,
                        update ? resource.Handle : default,
                        _scratchBufferDeviceAddress,
                        update
                            ? BuildAccelerationStructureModeKHR.UpdateKhr
                            : BuildAccelerationStructureModeKHR.BuildKhr);
                var range = new AccelerationStructureBuildRangeInfoKHR
                {
                    PrimitiveCount = primitiveCount,
                    PrimitiveOffset = 0,
                    FirstVertex = 0,
                    TransformOffset = 0
                };
                AccelerationStructureBuildRangeInfoKHR* rangePointer = &range;
                _khrAccelerationStructure!.CmdBuildAccelerationStructures(
                    commandBuffer,
                    1,
                    &buildInfo,
                    &rangePointer);
                InsertAccelerationStructureBuildBarrier(commandBuffer);

                resource.LastUsedFrameSerial = _frameSerial;
                resource.RepresentationRevision = instance.RepresentationGeneration;
                uint representationGeneration =
                    DynamicRepresentationGeneration(
                        key,
                        instance.RepresentationGeneration);
                instances[i] = instance with
                {
                    RepresentationGeneration = representationGeneration
                };
                if (update)
                    _lastDynamicBlasRefitCount++;
                else
                    _lastDynamicBlasFullBuildCount++;
                _lastDynamicBlasPrimitiveCount = checked(
                    _lastDynamicBlasPrimitiveCount + primitiveCount);
            }
            RecalculateAccelerationStructureBytes();
        }

        private static uint DynamicRepresentationGeneration(
            DynamicBlasKey key,
            uint sourceGeneration)
        {
            uint generation = StableInstanceIdentity(
                key.ObjectIdentity,
                unchecked((uint)key.FrameSlot));
            generation ^= unchecked((uint)key.Mesh.Index) * 0x9e3779b9u;
            generation ^= unchecked((uint)key.Mesh.Generation) * 0x85ebca6bu;
            generation ^= sourceGeneration * 0xc2b2ae35u;
            return generation == 0u ? 1u : generation;
        }

        private void EnsureBottomLevelAccelerationStructures(
            IReadOnlyList<StaticOpaqueInstance> instances,
            CommandBuffer commandBuffer,
            ulong additionalTlasBudgetReservation,
            int frameIndex)
        {
            _unavailableMeshScratch.Clear();
            foreach (StaticOpaqueInstance instance in instances)
            {
                if (instance.UsesDynamicBlas)
                    continue;
                if (_unavailableMeshScratch.Contains(instance.Mesh))
                    continue;

                if (_blasCache.TryGetValue(instance.Mesh, out BottomLevelAccelerationStructure? cachedBlas))
                {
                    cachedBlas.LastUsedFrameSerial = _frameSerial;
                    continue;
                }

                ulong requiredBytes = EstimateBottomLevelAccelerationStructureBytes(instance.Mesh, instance.MeshInfo);
                if (!EnsureBottomLevelResidencyBudget(requiredBytes, additionalTlasBudgetReservation))
                {
                    _unavailableMeshScratch.Add(instance.Mesh);
                    _lastBlasBudgetRejectedCount++;
                    continue;
                }

                long blasStart = Stopwatch.GetTimestamp();
                BottomLevelAccelerationStructure blas = BuildBottomLevelAccelerationStructure(instance.Mesh, instance.MeshInfo, commandBuffer);
                blas.LastUsedFrameSerial = _frameSerial;
                _lastBlasBuildMicroseconds += ElapsedMicroseconds(blasStart);
                _lastBlasBuildCount++;
                _blasCache.Add(instance.Mesh, blas);
                AdvanceResourceGeneration();
                AccelerationStructureBytes = checked(AccelerationStructureBytes + blas.Size);
                InsertAccelerationStructureBuildBarrier(commandBuffer);
                TrackBottomLevelCompactionQuery(
                    instance.Mesh,
                    blas,
                    commandBuffer,
                    frameIndex);
            }
        }

        private ulong EstimateBottomLevelAccelerationStructureBytes(MeshHandle meshHandle, MeshInfo meshInfo)
        {
            if (_blasSizeEstimateCache.TryGetValue(meshHandle, out ulong cachedSize))
                return cachedSize;

            uint primitiveCount = meshInfo.IndexCount / 3u;
            if (primitiveCount == 0)
                throw new InvalidOperationException("Cannot reserve BLAS memory for a mesh with no triangle primitives.");

            AccelerationStructureGeometryKHR geometry = CreateBottomLevelGeometry(meshInfo);
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateBottomLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            ulong estimatedSize = Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
            _blasSizeEstimateCache[meshHandle] = estimatedSize;
            return estimatedSize;
        }

        private bool EnsureBottomLevelResidencyBudget(
            ulong requiredBytes,
            ulong additionalTlasBudgetReservation)
        {
            if (!_residencyPolicy.Enabled)
                return true;

            ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
            ulong requiredAndReservedBytes = checked(requiredBytes + additionalTlasBudgetReservation);
            if (requiredAndReservedBytes > budgetBytes)
                return false;

            ulong staticResidentBytes = AccelerationStructureBytes >=
                _dynamicBlasBytes
                    ? AccelerationStructureBytes - _dynamicBlasBytes
                    : 0UL;
            while (WouldExceedBudget(
                       staticResidentBytes,
                       requiredAndReservedBytes,
                       budgetBytes))
            {
                if (!TryEvictBottomLevelAccelerationStructure(force: true))
                    return false;
                staticResidentBytes = AccelerationStructureBytes >=
                    _dynamicBlasBytes
                        ? AccelerationStructureBytes - _dynamicBlasBytes
                        : 0UL;
            }

            return true;
        }

        private ulong EstimateTopLevelAccelerationStructureBytes(int instanceCount)
        {
            uint primitiveCount = checked((uint)Math.Max(0, instanceCount));
            AccelerationStructureGeometryKHR geometry = CreateTopLevelGeometry();
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                default,
                default,
                default,
                BuildAccelerationStructureModeKHR.BuildKhr);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            return Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
        }

        internal static ulong CalculateAdditionalTopLevelReservation(
            ulong estimatedTopLevelBytes,
            ulong currentTopLevelBytes) =>
            estimatedTopLevelBytes > currentTopLevelBytes
                ? estimatedTopLevelBytes - currentTopLevelBytes
                : 0;

        private void PruneUnusedBottomLevelAccelerationStructures()
        {
            if (!_residencyPolicy.Enabled || _blasCache.Count == 0)
                return;

            // The hard cap is enforced before every new allocation.  This pass also
            // releases aged streamed chunks when memory pressure is absent, avoiding
            // a cache that is bounded in theory but permanently retains travelled
            // world content in practice.
            while (TryEvictBottomLevelAccelerationStructure(force: false))
            {
            }
        }

        private bool TryEvictBottomLevelAccelerationStructure(bool force)
        {
            if (_blasCache.Count == 0)
                return false;

            MeshHandle selectedMesh = default;
            BottomLevelAccelerationStructure? selectedBlas = null;
            ulong minimumLastUsedFrame = ulong.MaxValue;
            ulong graceFrames = (ulong)Math.Max(0, _residencyPolicy.EvictionGraceFrames);
            foreach (KeyValuePair<MeshHandle, BottomLevelAccelerationStructure> pair in _blasCache)
            {
                if (_activeMeshScratch.Contains(pair.Key))
                    continue;

                BottomLevelAccelerationStructure candidate = pair.Value;
                bool oldEnough = _frameSerial >= candidate.LastUsedFrameSerial &&
                    _frameSerial - candidate.LastUsedFrameSerial >= graceFrames;
                if (!force && !oldEnough)
                    continue;
                if (selectedBlas != null && candidate.LastUsedFrameSerial >= minimumLastUsedFrame)
                    continue;

                selectedMesh = pair.Key;
                selectedBlas = candidate;
                minimumLastUsedFrame = candidate.LastUsedFrameSerial;
            }

            if (selectedBlas == null)
                return false;

            // Eviction removes the BLAS from the active working set immediately,
            // but Vulkan still requires its storage to survive the in-flight
            // frames that may reference the old TLAS. Do not turn an active-cap
            // save into unbounded transient growth.
            if (!CanReserveTransientBytes(selectedBlas.Size))
                return false;

            _blasCache.Remove(selectedMesh);
            AdvanceResourceGeneration();
            RetireAccelerationStructureResource(selectedBlas.Handle, selectedBlas.StorageBuffer, selectedBlas.Size);
            _lastBlasEvictionCount++;
            _lastBlasEvictionBytes = checked(_lastBlasEvictionBytes + selectedBlas.Size);
            RecalculateAccelerationStructureBytes();
            return true;
        }

        private static bool WouldExceedBudget(ulong currentBytes, ulong additionalBytes, ulong budgetBytes)
        {
            return currentBytes > budgetBytes || additionalBytes > budgetBytes - currentBytes;
        }

        internal static bool ShouldCompactBottomLevelAccelerationStructure(
            ulong sourceSize,
            ulong queriedCompactedSize) =>
            queriedCompactedSize > 0 &&
            Math.Max(MinResourceBufferSize, queriedCompactedSize) < sourceSize;

        internal static bool FitsBlasCompactionFrameBudget(
            ulong destinationBytesThisFrame,
            ulong nextDestinationBytes,
            ulong frameBudgetBytes) =>
            // One oversized BLAS must still make forward progress.
            destinationBytesThisFrame == 0 ||
            !WouldExceedBudget(
                destinationBytesThisFrame,
                nextDestinationBytes,
                frameBudgetBytes);

        internal static ulong CalculateScratchMemoryBudgetBytes(ulong activeAccelerationStructureBudgetBytes)
        {
            if (activeAccelerationStructureBudgetBytes == 0)
                return 0;
            if (activeAccelerationStructureBudgetBytes == ulong.MaxValue)
                return ulong.MaxValue;

            // A build scratch buffer is reusable, but it must still have a hard
            // tier-derived limit. Half of the active working-set cap is ample for
            // the supported geometry budget while 128 MiB prevents one large BLAS
            // from silently redefining a tier.
            const ulong minimumBytes = 16UL * 1024UL * 1024UL;
            const ulong maximumBytes = 128UL * 1024UL * 1024UL;
            ulong proportionalBytes = activeAccelerationStructureBudgetBytes / 2UL;
            return Math.Min(maximumBytes, Math.Max(minimumBytes, proportionalBytes));
        }

        internal static ulong CalculateTransientMemoryBudgetBytes(ulong activeAccelerationStructureBudgetBytes)
        {
            if (activeAccelerationStructureBudgetBytes == 0)
                return 0;
            if (activeAccelerationStructureBudgetBytes == ulong.MaxValue)
                return ulong.MaxValue;

            // A content reload may retain one complete previous BLAS/TLAS working
            // set while the next generation is built. Reserve that bounded handoff
            // plus the explicit scratch allowance, then reject further churn until
            // in-flight resources drain.
            return SaturatingAdd(
                activeAccelerationStructureBudgetBytes,
                CalculateScratchMemoryBudgetBytes(activeAccelerationStructureBudgetBytes));
        }

        private static ulong SaturatingAdd(ulong left, ulong right) =>
            ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

        private bool CanReserveTransientBytes(ulong additionalBytes)
        {
            if (!_residencyPolicy.Enabled)
                return true;

            ulong transientBudget = _residencyPolicy.EffectiveTransientMemoryBudgetBytes;
            return !WouldExceedBudget(TransientBytes, additionalBytes, transientBudget);
        }

        private void EnsureTransientAllocationBudget(ulong additionalBytes, string resourceName)
        {
            if (additionalBytes == 0 || !_residencyPolicy.Enabled)
                return;

            ulong transientBudget = _residencyPolicy.EffectiveTransientMemoryBudgetBytes;
            if (CanReserveTransientBytes(additionalBytes))
                return;

            throw new InvalidOperationException(
                $"GI acceleration-structure transient budget ({transientBudget} bytes) cannot accommodate {resourceName} " +
                $"({additionalBytes} bytes) while {TransientBytes} bytes are still live.");
        }

        private bool HasMissingBottomLevelAccelerationStructures(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (!instances[i].UsesDynamicBlas &&
                    !_blasCache.ContainsKey(instances[i].Mesh))
                    return true;
            }

            return false;
        }

        private static AccelerationStructurePreparationIdentity CreatePreparationIdentity(
            ulong sceneContentRevision,
            AccelerationStructureResidencyPolicy residencyPolicy,
            DdgiDynamicRayScenePolicy dynamicPolicy,
            RaySceneRequirement requirement)
        {
            // Camera motion cannot alter the selected instance set when both the
            // distance and count limits are open.  Normalizing it out allows the
            // high-quality tier to retain this fast path during ordinary camera
            // traversal while all settings that can affect memory admission remain
            // part of the exact identity.
            if (!RequiresStaticResidencySelection(residencyPolicy))
                residencyPolicy = residencyPolicy with { CameraPosition = CoreVector3.Zero };

            return new AccelerationStructurePreparationIdentity(
                sceneContentRevision,
                residencyPolicy,
                dynamicPolicy,
                requirement);
        }

        internal static bool ShouldReusePreparedRayScene(
            bool hasDynamicBuilds,
            bool meshBuffersChanged,
            bool pendingOpacityMicromapWork,
            bool preparationIdentityReusable) =>
            !hasDynamicBuilds &&
            !meshBuffersChanged &&
            !pendingOpacityMicromapWork &&
            preparationIdentityReusable;

        private bool CanReusePreparation(AccelerationStructurePreparationIdentity identity)
        {
            return identity.SceneContentRevision != 0 &&
                _hasReusablePreparation &&
                _lastPreparationIdentity.Equals(identity) &&
                _lastPreparationResourceGeneration == _resourceGeneration &&
                _readyBlasCompactions.Count == 0 &&
                _tlas.Handle.Handle != 0 &&
                _hasTlasInstanceSignature &&
                _lastTlasInstanceCount > 0 &&
                _instanceScratch.Count == _lastTlasInstanceCount &&
                string.IsNullOrEmpty(_lastFallbackReason);
        }

        private void CacheReusablePreparation(AccelerationStructurePreparationIdentity identity)
        {
            if (identity.SceneContentRevision == 0 || !Active)
            {
                _hasReusablePreparation = false;
                return;
            }

            _lastPreparationIdentity = identity;
            _lastPreparationResourceGeneration = _resourceGeneration;
            _cachedStaticInstanceCandidateCount = _lastStaticInstanceCandidateCount;
            _cachedStaticInstanceResidentCount = _lastStaticInstanceResidentCount;
            _cachedStaticInstanceCulledCount = _lastStaticInstanceCulledCount;
            _hasReusablePreparation = true;
        }

        private void TouchActiveBottomLevelAccelerationStructures()
        {
            foreach (MeshHandle mesh in _activeMeshScratch)
            {
                if (_blasCache.TryGetValue(mesh, out BottomLevelAccelerationStructure? blas))
                    blas.LastUsedFrameSerial = _frameSerial;
            }
            TouchActiveOpacityMicromapBottomLevelAccelerationStructures();
        }

        private bool InvalidateCachedStructuresIfMeshBuffersChanged()
        {
            BufferHandle vertexPositionBuffer = _meshManager.VertexPositionBuffer;
            BufferHandle indexBuffer = _meshManager.IndexBuffer;
            if (_lastVertexPositionBuffer == vertexPositionBuffer && _lastIndexBuffer == indexBuffer)
                return false;

            _blasSizeEstimateCache.Clear();

            // Replacing every BLAS/TLAS at once temporarily retains the previous
            // generation until all in-flight ray queries have completed. Reserve
            // that physical residency before invalidating anything so a content
            // reload degrades to the safe non-ray-query path instead of silently
            // violating the transient cap.
            EnsureTransientAllocationBudget(
                AccelerationStructureBytes,
                "mesh-buffer acceleration-structure replacement");
            DestroyAllTopLevelAccelerationStructures(defer: true);
            DestroyBottomLevelAccelerationStructures(defer: true);
            DestroyDynamicBottomLevelAccelerationStructures(defer: true);
            RecalculateAccelerationStructureBytes();
            _lastVertexPositionBuffer = vertexPositionBuffer;
            _lastIndexBuffer = indexBuffer;
            _hasReusablePreparation = false;
            return true;
        }

        /// <summary>
        /// Resolves the existing, material-neutral static BLAS as an ordinary
        /// fallback candidate.  The BLAS itself never sets an opaque geometry
        /// flag; candidate confirmation remains a TLAS-instance policy that
        /// the content-key registration must preserve.  This resolver makes no
        /// allocation and intentionally returns an unusable default if the
        /// mesh has been evicted, reloaded, or is no longer live.
        /// </summary>
        private OpacityMicromapExtOrdinaryFallback
            ResolveOpacityMicromapOrdinaryFallback(MeshHandle mesh)
        {
            if (_disposed ||
                !_blasCache.TryGetValue(mesh, out BottomLevelAccelerationStructure? blas) ||
                blas.Handle.Handle == 0UL || !blas.StorageBuffer.IsValid ||
                blas.Size == 0UL)
            {
                return default;
            }

            try
            {
                MeshInfo meshInfo = _meshManager.GetMeshInfo(mesh);
                if (meshInfo.IsSkinned || meshInfo.IndexCount == 0U ||
                    meshInfo.IndexCount % 3U != 0U)
                {
                    return default;
                }

                return new OpacityMicromapExtOrdinaryFallback(
                    Mesh: mesh,
                    PrimitiveCount: meshInfo.IndexCount / 3U,
                    BlasHandle: blas.Handle.Handle,
                    ResidentBytes: blas.Size,
                    IsStaticTriangleGeometry: true,
                    CandidateConfirmationAvailable: true);
            }
            catch (InvalidOperationException)
            {
                // A content reload may invalidate the mesh handle between a
                // registration lookup and this no-allocation plan proof.
                return default;
            }
        }

        private void OnOpacityMicromapMaterialChanged(
            MaterialChangedEvent _)
        {
            // The event can arrive from an asset/editor thread. Defer all
            // manager and native-host access to the renderer's frame boundary.
            _opacityMicromapMaterialStateDirty = true;
        }

        private void SynchronizeOpacityMicromapRuntimeRegistrations()
        {
            OpacityMicromapRuntimeRegistrationStore? store =
                _opacityMicromapRuntimeRegistrations;
            if (store is null)
                return;

            ulong observedRevision = store.CandidateSetRevision;
            if (!_opacityMicromapMaterialStateDirty &&
                observedRevision ==
                    _synchronizedOpacityMicromapCandidateSetRevision)
            {
                return;
            }

            OpacityMicromapRuntimeMeshRegistration[] registrations =
                store.GetRegistrationsSnapshot(out ulong snapshotRevision);
            var desired = new Dictionary<OpacityMicromapContentKey,
                OpacityMicromapExtStaticBlasCandidate>(registrations.Length);
            var desiredByMesh = new Dictionary<MeshHandle,
                OpacityMicromapRuntimeMeshRegistration>(registrations.Length);
            var contentKeyOwners = new Dictionary<OpacityMicromapContentKey,
                List<MeshHandle>>(registrations.Length);
            var contentKeyVariants = new Dictionary<OpacityMicromapContentKey,
                StaticBlasVariantKey>(registrations.Length);
            var desiredPayloads = new Dictionary<OpacityMicromapContentKey,
                OpacityMicromapCookedPayload>(registrations.Length);
            var ambiguousContentKeys =
                new HashSet<OpacityMicromapContentKey>();
            int staleMaterialCount = 0;
            int duplicateContentKeyCount = 0;
            foreach (OpacityMicromapRuntimeMeshRegistration registration in
                     registrations)
            {
                try
                {
                    _ = _materialManager.GetMaterialDefinition(
                        registration.Material);
                    if (_materialManager.GetMaterialContentRevision(
                            registration.Material.Index) !=
                        registration.MaterialContentRevision)
                    {
                        staleMaterialCount++;
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    staleMaterialCount++;
                    continue;
                }

                OpacityMicromapExtStaticBlasCandidate candidate =
                    registration.CreateCandidate();
                StaticBlasVariantKey variantKey =
                    registration.CreateVariantKey();
                if (ambiguousContentKeys.Contains(candidate.ContentKey))
                    continue;
                if (desired.TryGetValue(
                        candidate.ContentKey,
                        out _) &&
                    contentKeyVariants.TryGetValue(
                        candidate.ContentKey,
                        out StaticBlasVariantKey existingVariant) &&
                    existingVariant != variantKey)
                {
                    // One payload hash resolving to different immutable BLAS
                    // identities is ambiguous and fails closed. Identical
                    // variant keys uploaded under different mesh handles are
                    // intentionally shared below.
                    desired.Remove(candidate.ContentKey);
                    contentKeyVariants.Remove(candidate.ContentKey);
                    desiredPayloads.Remove(candidate.ContentKey);
                    if (contentKeyOwners.Remove(
                            candidate.ContentKey,
                            out List<MeshHandle>? previousOwners))
                    {
                        foreach (MeshHandle previousOwner in previousOwners)
                            desiredByMesh.Remove(previousOwner);
                    }
                    ambiguousContentKeys.Add(candidate.ContentKey);
                    duplicateContentKeyCount++;
                    continue;
                }

                if (!desired.ContainsKey(candidate.ContentKey))
                {
                    desired[candidate.ContentKey] = candidate;
                    contentKeyVariants[candidate.ContentKey] = variantKey;
                    desiredPayloads[candidate.ContentKey] =
                        registration.Payload;
                    contentKeyOwners[candidate.ContentKey] =
                        new List<MeshHandle>();
                }
                else if (CompareMeshHandles(
                             candidate.Mesh,
                             desired[candidate.ContentKey].Mesh) < 0)
                {
                    // Native registration needs one ordinary-BLAS owner even
                    // though the live enhanced variant is shared. Choose it
                    // deterministically so dictionary insertion order cannot
                    // churn the host registration across frame boundaries.
                    desired[candidate.ContentKey] = candidate;
                }
                contentKeyOwners[candidate.ContentKey].Add(registration.Mesh);
                desiredByMesh[registration.Mesh] = registration;
            }

            var added = new List<OpacityMicromapContentKey>();
            foreach ((OpacityMicromapContentKey contentKey,
                      OpacityMicromapExtStaticBlasCandidate candidate) in desired)
            {
                if (_synchronizedOpacityMicromapCandidates.TryGetValue(
                        contentKey,
                        out OpacityMicromapExtStaticBlasCandidate current) &&
                    current == candidate)
                {
                    continue;
                }
                if (!_opacityMicromapNativeLifecycleHost.TryRegister(
                        candidate,
                        out string registrationDetail))
                {
                    foreach (OpacityMicromapContentKey addedKey in added)
                    {
                        _opacityMicromapNativeLifecycleHost.RemoveRegistration(
                            addedKey,
                            out _);
                    }
                    _opacityMicromapRegistrationDetail =
                        "omm-runtime-registration-sync-failed-" +
                        registrationDetail;
                    return;
                }

                if (registrationDetail ==
                    "omm-static-blas-registration-added")
                {
                    added.Add(contentKey);
                }
            }

            foreach (OpacityMicromapContentKey staleKey in
                     _synchronizedOpacityMicromapCandidates.Keys
                         .Where(key => !desired.ContainsKey(key))
                         .ToArray())
            {
                if (_opacityMicromapNativeLifecycleHost.RemoveRegistration(
                        staleKey,
                        out string removalDetail) ||
                    removalDetail ==
                        "omm-static-blas-registration-not-found")
                {
                    continue;
                }

                _opacityMicromapRegistrationDetail =
                    "omm-runtime-registration-removal-failed-" +
                    removalDetail;
                return;
            }

            _synchronizedOpacityMicromapCandidates.Clear();
            foreach ((OpacityMicromapContentKey contentKey,
                      OpacityMicromapExtStaticBlasCandidate candidate) in desired)
            {
                _synchronizedOpacityMicromapCandidates.Add(
                    contentKey,
                    candidate);
            }
            _synchronizedOpacityMicromapRegistrationsByMesh.Clear();
            foreach ((MeshHandle mesh,
                      OpacityMicromapRuntimeMeshRegistration registration) in
                     desiredByMesh)
            {
                _synchronizedOpacityMicromapRegistrationsByMesh.Add(
                    mesh,
                    registration);
            }
            _synchronizedOpacityMicromapCandidateSetRevision =
                snapshotRevision;
            _opacityMicromapMaterialStateDirty = false;
            OpacityMicromapRuntimeRegistrationSnapshot storeSnapshot =
                store.GetSnapshot();
            _opacityMicromapContentDiagnostics =
                CreateOpacityMicromapContentDiagnostics(
                    desiredByMesh.Count,
                    desiredPayloads,
                    storeSnapshot.RejectedRegistrationCount,
                    staleMaterialCount,
                    duplicateContentKeyCount);
            ReconcileOpacityMicromapGpuRegistrations();
            _opacityMicromapRegistrationDetail =
                duplicateContentKeyCount > 0
                    ? "omm-runtime-registration-duplicate-content-key-fallback"
                    : staleMaterialCount > 0
                        ? "omm-runtime-registration-stale-material-fallback"
                        : desired.Count == 0
                            ? "omm-runtime-registration-set-empty"
                            : "omm-runtime-registration-synchronized";
        }

        private static int CompareMeshHandles(
            in MeshHandle left,
            in MeshHandle right)
        {
            int comparison = left.Index.CompareTo(right.Index);
            return comparison != 0
                ? comparison
                : left.Generation.CompareTo(right.Generation);
        }

        private BottomLevelAccelerationStructure BuildBottomLevelAccelerationStructure(
            MeshHandle meshHandle,
            MeshInfo meshInfo,
            CommandBuffer commandBuffer)
        {
            uint primitiveCount = meshInfo.IndexCount / 3u;
            if (primitiveCount == 0)
                throw new InvalidOperationException($"Mesh {meshHandle.Index} does not contain triangle primitives for BLAS build.");

            AccelerationStructureGeometryKHR geometry = CreateBottomLevelGeometry(meshInfo);
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateBottomLevelBuildInfo(&geometry, default, default);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);

            // Reserve reusable scratch before allocating the BLAS storage.  A
            // transient-cap failure therefore cannot leak a partially-created
            // BLAS allocation or leave a half-built cache entry behind.
            EnsureScratchCapacity(sizes.BuildScratchSize);

            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize),
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                $"BLAS Mesh {meshHandle.Index}");
            AccelerationStructureKHR accelerationStructure = default;
            try
            {
                accelerationStructure = CreateAccelerationStructure(
                    storageBuffer,
                    sizes.AccelerationStructureSize,
                    AccelerationStructureTypeKHR.BottomLevelKhr,
                    $"BLAS Mesh {meshHandle.Index}");

                geometry = CreateBottomLevelGeometry(meshInfo);
                buildInfo = CreateBottomLevelBuildInfo(
                    &geometry,
                    accelerationStructure,
                    _scratchBufferDeviceAddress);

                var range = new AccelerationStructureBuildRangeInfoKHR
                {
                    PrimitiveCount = primitiveCount,
                    PrimitiveOffset = 0,
                    FirstVertex = 0,
                    TransformOffset = 0
                };
                AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
                _khrAccelerationStructure!.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &rangePtr);

                return new BottomLevelAccelerationStructure(accelerationStructure, storageBuffer, sizes.AccelerationStructureSize);
            }
            catch
            {
                DestroyAccelerationStructureResource(accelerationStructure, storageBuffer);
                throw;
            }
        }

        private void TrackBottomLevelCompactionQuery(
            MeshHandle mesh,
            BottomLevelAccelerationStructure blas,
            CommandBuffer commandBuffer,
            int frameIndex)
        {
            if (_blasCompactionQueriesDisabled)
                return;

            List<PendingBlasCompactionQuery> pending =
                _pendingBlasCompactionQueries[frameIndex];
            if (pending.Count >= MaxBlasCompactionQueriesPerFrame)
            {
                _lastBlasCompactionQueryOverflowCount++;
                return;
            }

            // A non-empty slot at this point means its completed results could
            // not be read. Never reset or overwrite unresolved queries; the
            // BLAS remains valid and simply stays uncompacted.
            if (!_blasCompactionQueryPoolResetThisFrame[frameIndex] &&
                pending.Count != 0)
            {
                _lastBlasCompactionQueryOverflowCount++;
                return;
            }

            if (!EnsureBlasCompactionQueryPool(frameIndex))
                return;

            QueryPool queryPool = _blasCompactionQueryPools[frameIndex];
            if (!_blasCompactionQueryPoolResetThisFrame[frameIndex])
            {
                _context.Api.CmdResetQueryPool(
                    commandBuffer,
                    queryPool,
                    0,
                    MaxBlasCompactionQueriesPerFrame);
                _blasCompactionQueryPoolResetThisFrame[frameIndex] = true;
            }

            uint queryIndex = checked((uint)pending.Count);
            AccelerationStructureKHR accelerationStructure = blas.Handle;
            _khrAccelerationStructure!.CmdWriteAccelerationStructuresProperties(
                commandBuffer,
                1,
                &accelerationStructure,
                QueryType.AccelerationStructureCompactedSizeKhr,
                queryPool,
                queryIndex);
            pending.Add(new PendingBlasCompactionQuery(mesh, blas));
            _lastBlasCompactionQueryCount++;
        }

        private bool EnsureBlasCompactionQueryPool(int frameIndex)
        {
            if (_blasCompactionQueryPools[frameIndex].Handle != 0)
                return true;

            var createInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.AccelerationStructureCompactedSizeKhr,
                QueryCount = MaxBlasCompactionQueriesPerFrame
            };
            Result result = _context.Api.CreateQueryPool(
                _context.Device,
                &createInfo,
                null,
                out QueryPool queryPool);
            if (result != Result.Success)
            {
                // Compaction is a quality-neutral residency optimization. Query
                // allocation failure must not disable the authoritative ray-
                // query representation itself.
                _blasCompactionQueriesDisabled = true;
                _lastBlasCompactionQueryOverflowCount++;
                return false;
            }

            _blasCompactionQueryPools[frameIndex] = queryPool;
            _context.SetDebugName(
                queryPool.Handle,
                ObjectType.QueryPool,
                $"BLAS Compacted Size Query Pool Frame {frameIndex}");
            return true;
        }

        private void ResolveCompletedBlasCompactionQueries(int frameIndex)
        {
            List<PendingBlasCompactionQuery> pending =
                _pendingBlasCompactionQueries[frameIndex];
            if (pending.Count == 0)
                return;

            QueryPool queryPool = _blasCompactionQueryPools[frameIndex];
            if (queryPool.Handle == 0)
            {
                _lastBlasCompactionQueryReadbackFailureCount++;
                return;
            }

            ulong[] results = _blasCompactionQueryResults[frameIndex];
            fixed (ulong* resultPtr = results)
            {
                Result result = _context.Api.GetQueryPoolResults(
                    _context.Device,
                    queryPool,
                    0,
                    checked((uint)pending.Count),
                    checked((nuint)(pending.Count * sizeof(ulong))),
                    resultPtr,
                    sizeof(ulong),
                    QueryResultFlags.Result64Bit);
                if (result != Result.Success)
                {
                    _lastBlasCompactionQueryReadbackFailureCount++;
                    return;
                }
            }

            for (int i = 0; i < pending.Count; i++)
            {
                PendingBlasCompactionQuery query = pending[i];
                if (!ShouldCompactBottomLevelAccelerationStructure(
                        query.Source.Size,
                        results[i]))
                {
                    continue;
                }

                ulong compactedSize = Math.Max(MinResourceBufferSize, results[i]);

                _readyBlasCompactions.Enqueue(new ReadyBlasCompaction(
                    query.Mesh,
                    query.Source,
                    compactedSize));
            }

            pending.Clear();
        }

        private void ProcessReadyBlasCompactions(CommandBuffer commandBuffer)
        {
            if (_readyBlasCompactions.Count == 0)
                return;

            long start = Stopwatch.GetTimestamp();
            ulong destinationBytesThisFrame = 0;
            bool replacedAny = false;
            while (_readyBlasCompactions.Count > 0)
            {
                ReadyBlasCompaction candidate = _readyBlasCompactions.Peek();
                if (!_blasCache.TryGetValue(
                        candidate.Mesh,
                        out BottomLevelAccelerationStructure? current) ||
                    !ReferenceEquals(current, candidate.Source))
                {
                    _readyBlasCompactions.Dequeue();
                    continue;
                }

                ulong compactedSize = candidate.CompactedSize;
                if (compactedSize >= current.Size)
                {
                    _readyBlasCompactions.Dequeue();
                    continue;
                }

                bool exceedsFrameBudget =
                    !FitsBlasCompactionFrameBudget(
                        destinationBytesThisFrame,
                        compactedSize,
                        MaxBlasCompactionDestinationBytesPerFrame);
                if (exceedsFrameBudget)
                    break;

                // The copy destination becomes active residency. The old source
                // then enters the fence-safe retirement ledger, so admission is
                // governed by the larger source size rather than only by the
                // compact destination allocation.
                if (!CanReserveTransientBytes(current.Size))
                    break;

                _readyBlasCompactions.Dequeue();
                BufferHandle compactStorage = _bufferManager.CreateDeviceBuffer(
                    compactedSize,
                    BufferUsageFlags.AccelerationStructureStorageBitKhr |
                        BufferUsageFlags.ShaderDeviceAddressBit,
                    requireDeviceAddress: true,
                    MemoryBudgetCategory.GlobalIllumination,
                    $"BLAS Mesh {candidate.Mesh.Index} Compacted");
                AccelerationStructureKHR compactHandle = default;
                try
                {
                    compactHandle = CreateAccelerationStructure(
                        compactStorage,
                        compactedSize,
                        AccelerationStructureTypeKHR.BottomLevelKhr,
                        $"BLAS Mesh {candidate.Mesh.Index} Compacted");
                    var copyInfo = new CopyAccelerationStructureInfoKHR
                    {
                        SType = StructureType.CopyAccelerationStructureInfoKhr,
                        Src = current.Handle,
                        Dst = compactHandle,
                        Mode = CopyAccelerationStructureModeKHR.CompactKhr
                    };
                    _khrAccelerationStructure!.CmdCopyAccelerationStructure(
                        commandBuffer,
                        &copyInfo);

                    var replacement = new BottomLevelAccelerationStructure(
                        compactHandle,
                        compactStorage,
                        compactedSize,
                        current.UncompactedSize)
                    {
                        LastUsedFrameSerial = current.LastUsedFrameSerial
                    };
                    _blasCache[candidate.Mesh] = replacement;
                    RetireAccelerationStructureResource(
                        current.Handle,
                        current.StorageBuffer,
                        current.Size);
                    destinationBytesThisFrame = checked(
                        destinationBytesThisFrame + compactedSize);
                    _lastBlasCompactionCount++;
                    _lastBlasCompactionSourceBytes = checked(
                        _lastBlasCompactionSourceBytes + current.Size);
                    _lastBlasCompactionBytesSaved = checked(
                        _lastBlasCompactionBytesSaved + current.Size - compactedSize);
                    replacedAny = true;
                    AdvanceResourceGeneration();
                    InsertAccelerationStructureBuildBarrier(commandBuffer);
                }
                catch
                {
                    DestroyAccelerationStructureResource(
                        compactHandle,
                        compactStorage);
                    throw;
                }
            }

            if (replacedAny)
            {
                // BLAS device addresses changed. Even if instance transforms are
                // identical, the TLAS instance array must be uploaded again and
                // the TLAS rebuilt against the compacted children.
                _hasTlasInstanceSignature = false;
                _lastTlasInstanceSignature = 0;
                _lastTlasInstanceCount = 0;
                RecalculateAccelerationStructureBytes();
            }

            _lastBlasCompactionMicroseconds += ElapsedMicroseconds(start);
        }

        private static void ValidateCompactionFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= RenderingConstants.FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        private void SelectTopLevelFrameSlot(int frameIndex)
        {
            ValidateCompactionFrameIndex(frameIndex);
            _currentTlasFrameSlot = frameIndex;
            _tlas = _tlasFrameSlots[frameIndex];
            _lastTlasInstanceSignature = _tlasInstanceSignatures[frameIndex];
            _hasTlasInstanceSignature = _tlasHasInstanceSignatures[frameIndex];
            _lastTlasInstanceCount = _tlasInstanceCounts[frameIndex];
        }

        private void PersistTopLevelFrameSlot()
        {
            if ((uint)_currentTlasFrameSlot >=
                (uint)RenderingConstants.FramesInFlight)
            {
                return;
            }

            _tlasFrameSlots[_currentTlasFrameSlot] = _tlas;
            _tlasInstanceSignatures[_currentTlasFrameSlot] =
                _lastTlasInstanceSignature;
            _tlasHasInstanceSignatures[_currentTlasFrameSlot] =
                _hasTlasInstanceSignature;
            _tlasInstanceCounts[_currentTlasFrameSlot] =
                _lastTlasInstanceCount;
        }

        private ulong CalculateTopLevelFrameSlotBytes()
        {
            ulong bytes = 0;
            for (int i = 0; i < _tlasFrameSlots.Length; i++)
            {
                TopLevelAccelerationStructure slot =
                    i == _currentTlasFrameSlot
                        ? _tlas
                        : _tlasFrameSlots[i];
                bytes = checked(bytes + slot.Size);
            }
            return bytes;
        }

        private AccelerationStructureGeometryKHR CreateBottomLevelGeometry(MeshInfo meshInfo)
        {
            ulong vertexAddress = checked(_bufferManager.GetBufferDeviceAddress(_meshManager.VertexPositionBuffer) +
                (ulong)meshInfo.VertexOffset * VertexPositionStride);
            ulong indexAddress = checked(_bufferManager.GetBufferDeviceAddress(_meshManager.IndexBuffer) +
                (ulong)meshInfo.IndexOffset * IndexStride);

            var triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
                VertexStride = VertexPositionStride,
                MaxVertex = meshInfo.VertexCount - 1u,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = indexAddress },
                TransformData = default
            };

            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
                // Opaqueness is selected per TLAS instance.  Opaque instances use
                // ForceOpaqueBitKhr, while alpha-mask instances expose candidates
                // to the DDGI shader for the material cutoff test.
                Flags = default
            };
        }

        private AccelerationStructureGeometryKHR CreateDynamicBottomLevelGeometry(
            StaticOpaqueInstance instance,
            BufferHandle skinnedVertexBuffer)
        {
            BufferHandle vertexBuffer =
                ResolveDynamicVertexBuffer(instance, skinnedVertexBuffer);
            BufferHandle indexBuffer = ResolveDynamicIndexBuffer(instance);
            if (!vertexBuffer.IsValid || !indexBuffer.IsValid)
                throw new InvalidOperationException(
                    "Dynamic DDGI geometry requires valid frame-slot vertex and index buffers.");
            if (instance.VertexStride != (uint)Marshal.SizeOf<GPUVertex>() ||
                instance.PositionOffset != 0u)
            {
                throw new InvalidOperationException(
                    "The current-pose BLAS contract requires GPUVertex position at byte offset zero and the exact GPUVertex stride.");
            }

            ulong vertexAddress = checked(
                _bufferManager.GetBufferDeviceAddress(vertexBuffer) +
                (ulong)instance.VertexOffset * instance.VertexStride +
                instance.PositionOffset);
            ulong indexAddress = checked(
                _bufferManager.GetBufferDeviceAddress(indexBuffer) +
                (ulong)instance.IndexOffset * IndexStride);
            var triangles = new AccelerationStructureGeometryTrianglesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                VertexFormat = Format.R32G32B32Sfloat,
                VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = vertexAddress },
                VertexStride = instance.VertexStride,
                MaxVertex = instance.MeshInfo.VertexCount - 1u,
                IndexType = IndexType.Uint32,
                IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = indexAddress },
                TransformData = default
            };
            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Triangles = triangles },
                Flags = default
            };
        }

        private static BufferHandle ResolveDynamicVertexBuffer(
            StaticOpaqueInstance instance,
            BufferHandle skinnedVertexBuffer) =>
            instance.GeometryVertexBuffer.IsValid
                ? instance.GeometryVertexBuffer
                : skinnedVertexBuffer;

        private BufferHandle ResolveDynamicIndexBuffer(
            StaticOpaqueInstance instance) =>
            instance.GeometryIndexBuffer.IsValid
                ? instance.GeometryIndexBuffer
                : _meshManager.IndexBuffer;

        private static AccelerationStructureBuildGeometryInfoKHR CreateBottomLevelBuildInfo(
            AccelerationStructureGeometryKHR* geometry,
            AccelerationStructureKHR destination,
            ulong scratchAddress)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr |
                    BuildAccelerationStructureFlagsKHR.AllowCompactionBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                DstAccelerationStructure = destination,
                GeometryCount = 1,
                PGeometries = geometry,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress }
            };
        }

        private static AccelerationStructureBuildGeometryInfoKHR CreateDynamicBottomLevelBuildInfo(
            AccelerationStructureGeometryKHR* geometry,
            AccelerationStructureKHR destination,
            AccelerationStructureKHR source,
            ulong scratchAddress,
            BuildAccelerationStructureModeKHR mode)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.BottomLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr |
                    BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr,
                Mode = mode,
                SrcAccelerationStructure = source,
                DstAccelerationStructure = destination,
                GeometryCount = 1,
                PGeometries = geometry,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress }
            };
        }

        private void BuildTopLevelAccelerationStructure(
            IReadOnlyList<StaticOpaqueInstance> instances,
            StagingRing stagingRing,
            CommandBuffer commandBuffer,
            TopLevelAccelerationStructureBuildAction requestedAction,
            ulong instanceSignature)
        {
            long tlasStart = Stopwatch.GetTimestamp();
            _gpuInstanceScratch.Clear();
            _rayQueryInstanceScratch.Clear();
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                AccelerationStructureKHR blasHandle;
                if (instance.UsesDynamicBlas)
                {
                    DynamicBlasKey key = CreateDynamicBlasKey(instance);
                    if (!_dynamicBlasPool.TryGetValue(
                            key,
                            out DynamicBottomLevelAccelerationStructure? dynamicBlas))
                    {
                        throw new InvalidOperationException(
                            "A current-pose instance reached TLAS publication without a complete frame-slot BLAS.");
                    }
                    blasHandle = dynamicBlas.Handle;
                }
                else
                {
                    blasHandle = TryResolveOpacityMicromapBlas(
                            instance,
                            out BottomLevelAccelerationStructure?
                                opacityMicromapBlas)
                        ? opacityMicromapBlas!.Handle
                        : _blasCache[instance.Mesh].Handle;
                }
                ulong blasAddress = GetAccelerationStructureDeviceAddress(blasHandle);
                _gpuInstanceScratch.Add(CreateInstance(
                    instance.WorldMatrix,
                    blasAddress,
                    (uint)i,
                    ResolveSharedInstanceMask(instance),
                    instance.InstanceFlags));
                _rayQueryInstanceScratch.Add(CreateRayQueryInstanceMetadata(instance));
            }

            EnsureInstanceBufferCapacity(_gpuInstanceScratch.Count);
            EnsureRayQueryInstanceMetadataCapacity(_rayQueryInstanceScratch.Count);
            _lastInstanceUploadBytes = checked((ulong)_gpuInstanceScratch.Count * (ulong)sizeof(AccelerationStructureInstanceKHR));
            _lastRayQueryInstanceMetadataUploadBytes = checked((ulong)_rayQueryInstanceScratch.Count * RayQueryInstanceMetadataStride);
            long uploadStart = Stopwatch.GetTimestamp();
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _instanceBuffer,
                CollectionsMarshal.AsSpan(_gpuInstanceScratch),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                    AccessFlags2.ShaderReadBit |
                    AccessFlags2.AccelerationStructureReadBitKhr));
            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _rayQueryInstanceBuffer,
                CollectionsMarshal.AsSpan(_rayQueryInstanceScratch),
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.ComputeShaderBit,
                    AccessFlags2.ShaderStorageReadBit));
            _lastInstanceUploadMicroseconds = ElapsedMicroseconds(uploadStart);

            uint primitiveCount = (uint)_gpuInstanceScratch.Count;
            AccelerationStructureGeometryKHR geometry = CreateTopLevelGeometry();
            AccelerationStructureBuildGeometryInfoKHR buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                default,
                default,
                default,
                BuildAccelerationStructureModeKHR.BuildKhr);
            AccelerationStructureBuildSizesInfoKHR sizes = QueryBuildSizes(buildInfo, primitiveCount);
            bool willRecreateTlas = _tlas.Handle.Handle == 0 || _tlas.Size < Math.Max(MinResourceBufferSize, sizes.AccelerationStructureSize);
            bool useUpdate = requestedAction == TopLevelAccelerationStructureBuildAction.Update && !willRecreateTlas;
            ulong scratchSize = useUpdate && sizes.UpdateScratchSize > 0 ? sizes.UpdateScratchSize : sizes.BuildScratchSize;
            EnsureScratchCapacity(scratchSize);
            bool tlasRecreated = EnsureTopLevelAccelerationStructure(sizes.AccelerationStructureSize);
            // The allocation predicate above is intentionally mirrored here. A
            // mismatch would mean the selected update scratch size is unsafe.
            if (tlasRecreated != willRecreateTlas)
                throw new InvalidOperationException("TLAS allocation state changed while resolving the acceleration-structure build scratch requirement.");

            geometry = CreateTopLevelGeometry();
            AccelerationStructureKHR source = useUpdate ? _tlas.Handle : default;
            buildInfo = CreateTopLevelBuildInfo(
                &geometry,
                _tlas.Handle,
                source,
                _scratchBufferDeviceAddress,
                useUpdate ? BuildAccelerationStructureModeKHR.UpdateKhr : BuildAccelerationStructureModeKHR.BuildKhr);
            var range = new AccelerationStructureBuildRangeInfoKHR
            {
                PrimitiveCount = primitiveCount,
                PrimitiveOffset = 0,
                FirstVertex = 0,
                TransformOffset = 0
            };
            AccelerationStructureBuildRangeInfoKHR* rangePtr = &range;
            _khrAccelerationStructure!.CmdBuildAccelerationStructures(commandBuffer, 1, &buildInfo, &rangePtr);
            InsertAccelerationStructureBuildBarrier(commandBuffer);
            TopLevelInstanceCount = _gpuInstanceScratch.Count;
            if (useUpdate)
                _lastTlasUpdateCount = 1;
            else
                _lastTlasBuildCount = 1;
            _lastTlasInstanceSignature = instanceSignature;
            _hasTlasInstanceSignature = true;
            _lastTlasInstanceCount = _gpuInstanceScratch.Count;
            _lastTlasBuildMicroseconds = ElapsedMicroseconds(tlasStart);
        }

        private AccelerationStructureGeometryKHR CreateTopLevelGeometry()
        {
            ulong instanceAddress = _instanceBuffer.IsValid ? _bufferManager.GetBufferDeviceAddress(_instanceBuffer) : 0;
            var instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instanceAddress }
            };

            return new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.InstancesKhr,
                Geometry = new AccelerationStructureGeometryDataKHR { Instances = instances },
                Flags = GeometryFlagsKHR.OpaqueBitKhr
            };
        }

        private static AccelerationStructureBuildGeometryInfoKHR CreateTopLevelBuildInfo(
            AccelerationStructureGeometryKHR* geometry,
            AccelerationStructureKHR destination,
            AccelerationStructureKHR source,
            ulong scratchAddress,
            BuildAccelerationStructureModeKHR mode)
        {
            return new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = AccelerationStructureTypeKHR.TopLevelKhr,
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr | BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr,
                Mode = mode,
                SrcAccelerationStructure = source,
                DstAccelerationStructure = destination,
                GeometryCount = 1,
                PGeometries = geometry,
                ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress }
            };
        }

        private AccelerationStructureBuildSizesInfoKHR QueryBuildSizes(
            AccelerationStructureBuildGeometryInfoKHR buildInfo,
            uint primitiveCount)
        {
            var sizes = new AccelerationStructureBuildSizesInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildSizesInfoKhr
            };
            _khrAccelerationStructure!.GetAccelerationStructureBuildSizes(
                _context.Device,
                AccelerationStructureBuildTypeKHR.DeviceKhr,
                &buildInfo,
                &primitiveCount,
                &sizes);
            return sizes;
        }

        private AccelerationStructureKHR CreateAccelerationStructure(
            BufferHandle storageBuffer,
            ulong size,
            AccelerationStructureTypeKHR type,
            string debugName)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(storageBuffer);
            var createInfo = new AccelerationStructureCreateInfoKHR
            {
                SType = StructureType.AccelerationStructureCreateInfoKhr,
                Buffer = buffer,
                Size = size,
                Type = type
            };

            Result result = _khrAccelerationStructure!.CreateAccelerationStructure(
                _context.Device,
                &createInfo,
                null,
                out AccelerationStructureKHR accelerationStructure);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}.", result);

            _context.SetDebugName(accelerationStructure.Handle, ObjectType.AccelerationStructureKhr, debugName);
            return accelerationStructure;
        }

        private ulong GetAccelerationStructureDeviceAddress(AccelerationStructureKHR accelerationStructure)
        {
            var addressInfo = new AccelerationStructureDeviceAddressInfoKHR
            {
                SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                AccelerationStructure = accelerationStructure
            };
            return _khrAccelerationStructure!.GetAccelerationStructureDeviceAddress(_context.Device, &addressInfo);
        }

        private bool EnsureTopLevelAccelerationStructure(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_tlas.Handle.Handle != 0 && _tlas.Size >= requiredSize)
                return false;

            if (_residencyPolicy.Enabled)
            {
                ulong budgetBytes = _residencyPolicy.EffectiveMemoryBudgetBytes;
                ulong existingTlasBytes = _tlas.Size;
                ulong activeBytesWithoutTlas = AccelerationStructureBytes >= existingTlasBytes
                    ? AccelerationStructureBytes - existingTlasBytes
                    : 0;
                activeBytesWithoutTlas = activeBytesWithoutTlas >=
                    _dynamicBlasBytes
                        ? activeBytesWithoutTlas - _dynamicBlasBytes
                        : 0UL;
                while (WouldExceedBudget(activeBytesWithoutTlas, requiredSize, budgetBytes))
                {
                    if (!TryEvictBottomLevelAccelerationStructure(force: true))
                    {
                        throw new InvalidOperationException(
                            $"GI acceleration-structure residency budget ({budgetBytes} bytes) cannot accommodate the active TLAS and BLAS working set.");
                    }

                    activeBytesWithoutTlas = AccelerationStructureBytes >= existingTlasBytes
                        ? AccelerationStructureBytes - existingTlasBytes
                        : 0;
                    activeBytesWithoutTlas = activeBytesWithoutTlas >=
                        _dynamicBlasBytes
                            ? activeBytesWithoutTlas - _dynamicBlasBytes
                            : 0UL;
                }

                // Replacing a TLAS defers the old storage through the frames in
                // flight. Budget that transition before mutating the active TLAS
                // so a resize cannot escape the separately declared transient cap.
                EnsureTransientAllocationBudget(existingTlasBytes, "retired top-level acceleration structure");
            }

            DestroyTopLevelAccelerationStructure(defer: true);
            BufferHandle storageBuffer = _bufferManager.CreateDeviceBuffer(
                requiredSize,
                BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Top Level Acceleration Structure");
            AccelerationStructureKHR tlas = default;
            try
            {
                tlas = CreateAccelerationStructure(
                    storageBuffer,
                    requiredSize,
                    AccelerationStructureTypeKHR.TopLevelKhr,
                    "Top Level Acceleration Structure");
                _tlas = new TopLevelAccelerationStructure(tlas, storageBuffer, requiredSize);
                AdvanceResourceGeneration();
            }
            catch
            {
                DestroyAccelerationStructureResource(tlas, storageBuffer);
                throw;
            }
            RecalculateAccelerationStructureBytes();
            return true;
        }

        private void EnsureScratchCapacity(ulong requiredSize)
        {
            requiredSize = Math.Max(MinResourceBufferSize, requiredSize);
            if (_scratchBuffer.IsValid && _scratchBufferCapacity >= requiredSize)
                return;

            ulong allocationSize = CalculateScratchBufferAllocationSize(
                requiredSize,
                _scratchBufferAddressAlignment);
            ulong scratchBudget = _residencyPolicy.EffectiveScratchMemoryBudgetBytes;
            if (allocationSize > scratchBudget)
            {
                throw new InvalidOperationException(
                    $"GI acceleration-structure scratch budget ({scratchBudget} bytes) cannot accommodate the requested build scratch ({allocationSize} bytes).");
            }
            EnsureTransientAllocationBudget(allocationSize, "acceleration-structure scratch buffer");

            if (_scratchBuffer.IsValid)
                RetireBufferResource(_scratchBuffer);

            _scratchBufferCapacity = requiredSize;
            _scratchBufferSize = allocationSize;
            _scratchBuffer = _bufferManager.CreateDeviceBuffer(
                _scratchBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "Acceleration Structure Scratch Buffer");

            ulong baseAddress = _bufferManager.GetBufferDeviceAddress(_scratchBuffer);
            _scratchBufferDeviceAddress = AlignScratchBufferAddress(
                baseAddress,
                _scratchBufferAddressAlignment);
            ulong alignedOffset = checked(_scratchBufferDeviceAddress - baseAddress);
            if (alignedOffset > _scratchBufferSize || _scratchBufferSize - alignedOffset < requiredSize)
            {
                throw new InvalidOperationException(
                    "The aligned acceleration-structure scratch address does not leave enough usable buffer capacity.");
            }
        }

        private static ulong QueryScratchBufferAddressAlignment(VulkanContext context)
        {
            var accelerationStructureProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr
            };
            var properties = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &accelerationStructureProperties
            };
            context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties);
            return Math.Max(
                1UL,
                accelerationStructureProperties.MinAccelerationStructureScratchOffsetAlignment);
        }

        internal static ulong AlignScratchBufferAddress(ulong address, ulong alignment)
        {
            if (alignment == 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));

            ulong remainder = address % alignment;
            return remainder == 0
                ? address
                : checked(address + alignment - remainder);
        }

        internal static ulong CalculateScratchBufferAllocationSize(ulong requiredSize, ulong alignment)
        {
            if (alignment == 0)
                throw new ArgumentOutOfRangeException(nameof(alignment));

            return checked(requiredSize + alignment - 1UL);
        }

        private void EnsureInstanceBufferCapacity(int instanceCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Max(0, instanceCount) * (ulong)sizeof(AccelerationStructureInstanceKHR)));
            if (_instanceBuffer.IsValid && _instanceBufferSize >= requiredSize)
                return;

            EnsureTransientAllocationBudget(requiredSize, "TLAS instance buffer");
            if (_instanceBuffer.IsValid)
                RetireBufferResource(_instanceBuffer);

            _instanceBufferSize = requiredSize;
            _instanceBuffer = _bufferManager.CreateDeviceBuffer(
                _instanceBufferSize,
                BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                requireDeviceAddress: true,
                MemoryBudgetCategory.GlobalIllumination,
                "TLAS Instance Buffer");
        }

        private void EnsureRayQueryInstanceMetadataCapacity(int instanceCount)
        {
            ulong requiredSize = Math.Max(
                MinResourceBufferSize,
                checked((ulong)Math.Max(0, instanceCount) * RayQueryInstanceMetadataStride));
            if (_rayQueryInstanceBuffer.IsValid && _rayQueryInstanceBufferSize >= requiredSize)
                return;

            EnsureTransientAllocationBudget(requiredSize, "ray-query instance metadata buffer");
            if (_rayQueryInstanceBuffer.IsValid)
                RetireBufferResource(_rayQueryInstanceBuffer);

            _rayQueryInstanceBufferSize = requiredSize;
            _rayQueryInstanceBuffer = _bufferManager.CreateDeviceBuffer(
                _rayQueryInstanceBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.GlobalIllumination,
                "DDGI Ray Query Instance Metadata Buffer");
            AdvanceResourceGeneration();
            RegisterRayQueryInstanceMetadataBuffer();
        }

        private void AdvanceResourceGeneration()
        {
            _resourceGeneration++;
            if (_resourceGeneration == 0)
                _resourceGeneration = 1;
        }

        private void RegisterRayQueryInstanceMetadataBuffer()
        {
            if (_registeredBindlessHeap == null || !_rayQueryInstanceBuffer.IsValid)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(
                BindlessIndex.SimpleDdgiRayQueryInstanceBuffer,
                _bufferManager.GetBuffer(_rayQueryInstanceBuffer),
                0,
                _rayQueryInstanceBufferSize);
        }

        private void InsertAccelerationStructureBuildBarrier(CommandBuffer commandBuffer)
        {
            var memoryBarrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                SrcAccessMask = AccessFlags2.AccelerationStructureWriteBitKhr,
                DstStageMask = PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.AccelerationStructureWriteBitKhr | AccessFlags2.ShaderReadBit
            };
            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &memoryBarrier
            };
            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        internal static AccelerationStructureInstanceKHR CreateInstance(
            CoreMatrix4x4 worldMatrix,
            ulong blasAddress,
            uint instanceCustomIndex,
            byte mask,
            GeometryInstanceFlagsKHR flags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr)
        {
            if (worldMatrix.Determinant() < 0.0f)
                flags |= GeometryInstanceFlagsKHR.TriangleFlipFacingBitKhr;
            return new AccelerationStructureInstanceKHR
            {
                Transform = CreateTransform(worldMatrix),
                InstanceCustomIndex = instanceCustomIndex & 0x00FF_FFFFu,
                Mask = mask,
                InstanceShaderBindingTableRecordOffset = 0,
                Flags = flags,
                AccelerationStructureReference = blasAddress
            };
        }

        internal static GPUDdgiRayQueryInstance CreateRayQueryInstanceMetadata(StaticOpaqueInstance instance)
        {
            return new GPUDdgiRayQueryInstance
            {
                AbiVersion = DdgiRayQueryInstanceAbi.Version2,
                GeometryClass = (uint)instance.GeometryClass,
                GeometryFlags = (uint)instance.GeometryFlags,
                StableInstanceIdentity = instance.StableInstanceIdentity,
                VertexBufferIndex = instance.VertexBufferIndex,
                VertexOffset = instance.VertexOffset,
                VertexStride = instance.VertexStride,
                VertexFormat = (uint)instance.VertexFormat,
                PositionOffset = instance.PositionOffset,
                NormalOffset = instance.NormalOffset,
                TangentOffset = instance.TangentOffset,
                TexCoord0Offset = instance.TexCoord0Offset,
                TexCoord1Offset = instance.TexCoord1Offset,
                ColorOffset = instance.ColorOffset,
                IndexBufferIndex = instance.IndexBufferIndex,
                IndexOffset = instance.IndexOffset,
                IndexType = DdgiRayQueryInstanceAbi.Uint32IndexType,
                MaterialIndex = instance.MaterialIndex,
                MaterialRevision = instance.MaterialRevision,
                PackedAlpha = instance.PackedAlpha,
                PackedDecalLayerAndOrder = instance.PackedDecalLayerAndOrder,
                DecalDepthTolerance = instance.DecalDepthTolerance,
                DecalDepthBias = instance.DecalDepthBias,
                RepresentationGeneration = Math.Max(1u, instance.RepresentationGeneration),
                WorldMatrixInverseTranspose = instance.WorldMatrix.Invert().Transpose()
            };
        }

        internal static DdgiAccelerationStructureGeometryPolicy ResolveGeometryPolicy(
            bool isSkinned,
            MaterialRenderMode renderMode,
            bool isGeometryDecal,
            AccelerationStructureGeometryDomain domain,
            bool doubleSided = false,
            GiTransmissionPolicy transmissionPolicy = GiTransmissionPolicy.None,
            DdgiTransparentGeometryMode transparentGeometryMode = DdgiTransparentGeometryMode.MaskAndThin,
            bool geometryDecalsEnabled = false,
            DdgiSkinnedGeometryMode skinnedGeometryMode = DdgiSkinnedGeometryMode.ConservativeProxy,
            DdgiFoliageGeometryMode foliageGeometryMode =
                DdgiFoliageGeometryMode.Excluded)
        {
            GeometryInstanceFlagsKHR sidednessFlags = doubleSided
                ? GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr
                : default;
            if (isGeometryDecal)
            {
                if (geometryDecalsEnabled)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        true,
                        StaticOpaqueInstanceMask,
                        sidednessFlags,
                        DdgiAccelerationStructureVisibilityPolicy.DecalOverlayCandidate,
                        "geometry decals participate as non-occluding DDGI overlay candidates");
                }
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.ExcludedGeometryDecal,
                    "geometry decals are excluded from DDGI ray-query visibility");
            }

            if (domain == AccelerationStructureGeometryDomain.Foliage)
            {
                if (foliageGeometryMode == DdgiFoliageGeometryMode.Excluded)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        false,
                        0,
                        default,
                        DdgiAccelerationStructureVisibilityPolicy.FoliageProxyPending,
                        FoliageDdgiExclusionReason);
                }
                // Qualified foliage continues through the ordinary material
                // policy below: alpha-mask, thin, and stochastic blend retain
                // their distinct candidate semantics on the proxy geometry.
            }

            bool thinSurface = transmissionPolicy == GiTransmissionPolicy.ThinSurface;
            bool volumeSurface = transmissionPolicy == GiTransmissionPolicy.Volume;
            if (volumeSurface)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.VolumeBoundaryCandidateTested,
                    "closed-volume and water boundaries remain candidate-tested for bounded dielectric transport");
            }
            if (renderMode == MaterialRenderMode.Blend && !thinSurface)
            {
                if (transparentGeometryMode == DdgiTransparentGeometryMode.StochasticBlend)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        true,
                        StaticOpaqueInstanceMask,
                        sidednessFlags,
                        DdgiAccelerationStructureVisibilityPolicy.StochasticAlphaBlend,
                        "ordinary blended geometry uses stable stochastic coverage for DDGI primary rays");
                }
                return new DdgiAccelerationStructureGeometryPolicy(
                    false,
                    0,
                    default,
                    DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent,
                    "transparent blended materials are excluded from DDGI ray-query occlusion");
            }

            if (thinSurface)
            {
                if (transparentGeometryMode == DdgiTransparentGeometryMode.MaskOnly)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        false,
                        0,
                        default,
                        DdgiAccelerationStructureVisibilityPolicy.ExcludedTransparent,
                        "the active DDGI transparency mode excludes thin-transmission geometry");
                }
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.ThinSurfaceCandidateTested,
                    renderMode == MaterialRenderMode.Blend
                        ? "explicit blended thin surfaces participate in DDGI candidate transport"
                        : "thin surfaces remain candidate-tested for reflected/transmitted transport");
            }

            if (isSkinned || domain == AccelerationStructureGeometryDomain.Skinned)
            {
                if (skinnedGeometryMode == DdgiSkinnedGeometryMode.Excluded)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        false,
                        0,
                        default,
                        DdgiAccelerationStructureVisibilityPolicy.ExcludedSkinned,
                        "the active DDGI skinned-geometry mode excludes animated meshes");
                }
                if (renderMode == MaterialRenderMode.Mask)
                {
                    return new DdgiAccelerationStructureGeometryPolicy(
                        true,
                        StaticOpaqueInstanceMask,
                        sidednessFlags,
                        DdgiAccelerationStructureVisibilityPolicy.SkinnedAlphaMaskTestedProxy,
                        "skinned alpha-masked meshes use bind-pose triangles while preserving authored alpha coverage");
                }

                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    GeometryInstanceFlagsKHR.ForceOpaqueBitKhr | sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.SkinnedBindPoseProxy,
                    "skinned meshes contribute a bind-pose triangle proxy until animated proxy geometry is available");
            }

            if (renderMode == MaterialRenderMode.Mask)
            {
                return new DdgiAccelerationStructureGeometryPolicy(
                    true,
                    StaticOpaqueInstanceMask,
                    sidednessFlags,
                    DdgiAccelerationStructureVisibilityPolicy.AlphaMaskTested,
                    "alpha-masked geometry is evaluated at ray-query candidates using the glTF cutoff");
            }

            return new DdgiAccelerationStructureGeometryPolicy(
                true,
                StaticOpaqueInstanceMask,
                GeometryInstanceFlagsKHR.ForceOpaqueBitKhr | sidednessFlags,
                DdgiAccelerationStructureVisibilityPolicy.OpaqueTriangles,
                domain == AccelerationStructureGeometryDomain.Dynamic
                    ? "dynamic opaque geometry participates with TLAS updates"
                    : "static opaque geometry participates with cached BLAS/TLAS");
        }

        internal static TopLevelAccelerationStructureBuildAction SelectTopLevelBuildAction(
            bool hasTopLevelAccelerationStructure,
            bool hasPreviousSignature,
            int previousInstanceCount,
            ulong previousSignature,
            int currentInstanceCount,
            ulong currentSignature)
        {
            if (!hasTopLevelAccelerationStructure || !hasPreviousSignature)
                return TopLevelAccelerationStructureBuildAction.Build;

            if (previousInstanceCount == currentInstanceCount && previousSignature == currentSignature)
                return TopLevelAccelerationStructureBuildAction.Skip;

            if (previousInstanceCount == currentInstanceCount)
                return TopLevelAccelerationStructureBuildAction.Update;

            return TopLevelAccelerationStructureBuildAction.Build;
        }

        internal static ulong CreateInstanceSignature(IReadOnlyList<StaticOpaqueInstance> instances)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                hash = HashAdd(hash, instance.Mesh.Index);
                hash = HashAdd(hash, instance.Mesh.Generation);
                hash = HashAdd(hash, instance.MeshInfo.VertexOffset);
                hash = HashAdd(hash, instance.MeshInfo.IndexOffset);
                hash = HashAdd(hash, instance.MeshInfo.VertexCount);
                hash = HashAdd(hash, instance.MeshInfo.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                hash = HashAdd(hash, instance.MaterialRevision);
                hash = HashAdd(hash,
                    unchecked((uint)instance.TransformRevision));
                hash = HashAdd(hash,
                    unchecked((uint)(instance.TransformRevision >> 32)));
                hash = HashAdd(hash,
                    unchecked((uint)instance.MeshInfo
                        .CausticTopologyEvidence.TopologyHash));
                hash = HashAdd(hash,
                    unchecked((uint)(instance.MeshInfo
                        .CausticTopologyEvidence.TopologyHash >> 32)));
                hash = HashAdd(hash, (int)instance.Domain);
                hash = HashAdd(hash, (uint)instance.InstanceFlags);
                hash = HashAdd(hash, instance.StableInstanceIdentity);
                hash = HashAdd(hash, (uint)instance.GeometryClass);
                hash = HashAdd(hash, (uint)instance.GeometryFlags);
                hash = HashAdd(hash, instance.UsesDynamicBlas ? 1u : 0u);
                hash = HashAdd(hash, instance.VertexBufferIndex);
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.VertexStride);
                hash = HashAdd(hash, unchecked((uint)instance.FrameSlot));
                hash = HashAdd(hash, instance.RepresentationGeneration);
                hash = HashAdd(hash, instance.WorldMatrix);
            }

            return hash;
        }

        /// <summary>
        /// Stable content identity for regional DDGI invalidation. Backing
        /// buffer handles, bindless indices, and frame slots are excluded so a
        /// resource transaction cannot masquerade as a pose/material edit.
        /// </summary>
        internal static ulong CreateRaySceneContentSignature(
            bool enabled,
            ulong sceneContentRevision,
            in DdgiDynamicRayScenePolicy policy,
            IReadOnlyList<StaticOpaqueInstance> instances)
        {
            ulong hash = HashStart;
            hash = HashAdd(hash, enabled ? 1u : 0u);
            hash = HashAdd(hash, (uint)sceneContentRevision);
            hash = HashAdd(hash, (uint)(sceneContentRevision >> 32));
            hash = HashAdd(hash, (uint)policy.SkinnedGeometryMode);
            hash = HashAdd(hash, (uint)policy.TransparentGeometryMode);
            hash = HashAdd(hash, (uint)policy.FoliageGeometryMode);
            hash = HashAdd(hash, policy.GeometryDecalsEnabled ? 1u : 0u);
            hash = HashAdd(hash, policy.AlphaMaskedTransportEnabled ? 1u : 0u);
            hash = HashAdd(hash, instances.Count);
            for (int i = 0; i < instances.Count; i++)
            {
                StaticOpaqueInstance instance = instances[i];
                hash = HashAdd(hash, instance.ObjectIdentity);
                hash = HashAdd(hash, instance.Mesh.Index);
                hash = HashAdd(hash, instance.Mesh.Generation);
                hash = HashAdd(hash, instance.MeshInfo.VertexOffset);
                hash = HashAdd(hash, instance.MeshInfo.IndexOffset);
                hash = HashAdd(hash, instance.MeshInfo.VertexCount);
                hash = HashAdd(hash, instance.MeshInfo.IndexCount);
                hash = HashAdd(hash, instance.MaterialIndex);
                hash = HashAdd(hash, instance.MaterialRevision);
                hash = HashAdd(hash,
                    unchecked((uint)instance.TransformRevision));
                hash = HashAdd(hash,
                    unchecked((uint)(instance.TransformRevision >> 32)));
                hash = HashAdd(hash,
                    unchecked((uint)instance.MeshInfo
                        .CausticTopologyEvidence.TopologyHash));
                hash = HashAdd(hash,
                    unchecked((uint)(instance.MeshInfo
                        .CausticTopologyEvidence.TopologyHash >> 32)));
                hash = HashAdd(hash, (int)instance.Domain);
                hash = HashAdd(hash, (uint)instance.InstanceFlags);
                hash = HashAdd(hash, instance.StableInstanceIdentity);
                hash = HashAdd(hash, instance.PackedAlpha);
                hash = HashAdd(hash, instance.PackedDecalLayerAndOrder);
                hash = HashAdd(hash, instance.DecalDepthTolerance);
                hash = HashAdd(hash, instance.DecalDepthBias);
                hash = HashAdd(hash, instance.RepresentationGeneration);
                hash = HashAdd(hash, (uint)instance.GeometryClass);
                hash = HashAdd(hash, (uint)instance.GeometryFlags);
                hash = HashAdd(hash, instance.UsesDynamicBlas ? 1u : 0u);
                hash = HashAdd(hash, instance.VertexOffset);
                hash = HashAdd(hash, instance.VertexStride);
                hash = HashAdd(hash, (uint)instance.VertexFormat);
                hash = HashAdd(hash, instance.IndexOffset);
                hash = HashAdd(hash, instance.WorldMatrix);
            }

            return hash;
        }

        private static ulong HashAdd(ulong hash, int value)
        {
            return HashAdd(hash, unchecked((uint)value));
        }

        private static ulong HashAdd(ulong hash, uint value)
        {
            hash ^= value;
            return hash * HashPrime;
        }

        private static ulong HashAdd(ulong hash, Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!value.TryWriteBytes(bytes))
                throw new InvalidOperationException(
                    "Could not encode the ray-instance GUID.");
            for (int index = 0; index < bytes.Length; index++)
                hash = (hash ^ bytes[index]) * HashPrime;
            return hash;
        }

        private static ulong HashAdd(ulong hash, float value)
        {
            return HashAdd(hash, BitConverter.SingleToUInt32Bits(value));
        }

        private static ulong HashAdd(ulong hash, CoreMatrix4x4 matrix)
        {
            hash = HashAdd(hash, matrix.M11);
            hash = HashAdd(hash, matrix.M12);
            hash = HashAdd(hash, matrix.M13);
            hash = HashAdd(hash, matrix.M14);
            hash = HashAdd(hash, matrix.M21);
            hash = HashAdd(hash, matrix.M22);
            hash = HashAdd(hash, matrix.M23);
            hash = HashAdd(hash, matrix.M24);
            hash = HashAdd(hash, matrix.M31);
            hash = HashAdd(hash, matrix.M32);
            hash = HashAdd(hash, matrix.M33);
            hash = HashAdd(hash, matrix.M34);
            hash = HashAdd(hash, matrix.M41);
            hash = HashAdd(hash, matrix.M42);
            hash = HashAdd(hash, matrix.M43);
            hash = HashAdd(hash, matrix.M44);
            return hash;
        }

        internal static uint StableInstanceIdentity(Guid identity, uint ordinal = 0u)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (!identity.TryWriteBytes(bytes))
                throw new InvalidOperationException("Could not encode the ray-instance GUID.");

            ulong hash = HashStart;
            for (int i = 0; i < bytes.Length; i++)
                hash = (hash ^ bytes[i]) * HashPrime;
            hash = HashAdd(hash, ordinal);
            uint folded = (uint)hash ^ (uint)(hash >> 32);
            return folded == 0u ? 1u : folded;
        }

        internal static byte ResolveSharedInstanceMask(
            in StaticOpaqueInstance instance)
        {
            bool directionalBlocker =
                instance.GeometryClass != DdgiRayGeometryClass.DecalOverlay &&
                (instance.GeometryFlags &
                    (DdgiRayGeometryFlags.AlphaBlend |
                     DdgiRayGeometryFlags.ThinTransmission |
                     DdgiRayGeometryFlags.VolumeTransmission |
                     DdgiRayGeometryFlags.WaterSurface |
                     DdgiRayGeometryFlags.DecalOverlay)) == 0;
            return directionalBlocker
                ? SharedLightingInstanceMask
                : StaticOpaqueInstanceMask;
        }

        private static ulong NextNonZero(ulong value) =>
            value == ulong.MaxValue ? 1UL : value + 1UL;

        internal static uint WithStableDecalOrder(
            uint packedLayerAndOrder,
            uint stableInstanceIdentity) =>
            (packedLayerAndOrder & 0x0000_FFFFu) |
            ((stableInstanceIdentity & 0xFFFFu) << 16);

        internal static uint FoldContentGeneration(ulong generation)
        {
            uint folded = unchecked((uint)generation) ^
                unchecked((uint)(generation >> 32));
            return folded == 0u ? 1u : folded;
        }

        private static DynamicBlasKey CreateDynamicBlasKey(
            StaticOpaqueInstance instance)
        {
            if (!instance.UsesDynamicBlas || instance.ObjectIdentity == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Dynamic BLAS identity requires an admitted object GUID.");
            }
            return new DynamicBlasKey(
                instance.ObjectIdentity,
                instance.Mesh,
                instance.FrameSlot);
        }

        internal static TransformMatrixKHR CreateTransform(CoreMatrix4x4 matrix)
        {
            TransformMatrixKHR transform = default;
            float* values = transform.Matrix;
            values[0] = matrix.M11;
            values[1] = matrix.M21;
            values[2] = matrix.M31;
            values[3] = matrix.M41;
            values[4] = matrix.M12;
            values[5] = matrix.M22;
            values[6] = matrix.M32;
            values[7] = matrix.M42;
            values[8] = matrix.M13;
            values[9] = matrix.M23;
            values[10] = matrix.M33;
            values[11] = matrix.M43;

            return transform;
        }

        private AccelerationStructureFrameStats CreateStats(bool active)
        {
            PersistTopLevelFrameSlot();
            UpdateReadinessSnapshot(active);
            return new AccelerationStructureFrameStats(
                Supported,
                active,
                BottomLevelCount,
                TopLevelInstanceCount,
                AccelerationStructureBytes,
                ScratchBufferBytes,
                InstanceBufferBytes,
                RayQueryInstanceMetadataBufferBytes,
                _lastBuildMicroseconds,
                _lastBlasBuildMicroseconds,
                _lastTlasBuildMicroseconds,
                _lastInstanceUploadMicroseconds,
                _lastBlasBuildCount,
                _lastTlasBuildCount,
                _lastTlasUpdateCount,
                _lastTlasSkipCount,
                _lastInstanceUploadBytes,
                _lastRayQueryInstanceMetadataUploadBytes,
                _lastStaticInstanceCandidateCount,
                _lastStaticInstanceResidentCount,
                _lastStaticInstanceCulledCount,
                _lastBlasEvictionCount,
                _lastBlasEvictionBytes,
                _lastBlasBudgetRejectedCount,
                BottomLevelAccelerationStructureBytes,
                TopLevelAccelerationStructureBytes,
                // The persisted frame-stat field predates deferred buffer
                // tracking. Keep its shape stable, but report every retired
                // acceleration-structure-associated allocation through it.
                RetiredResourceBytes,
                _lastBlasCompactionMicroseconds,
                _lastBlasCompactionQueryCount,
                _lastBlasCompactionCount,
                _lastBlasCompactionSourceBytes,
                _lastBlasCompactionBytesSaved,
                BottomLevelAccelerationStructureCompactedBytesSaved,
                GetPendingBlasCompactionCount(),
                _lastBlasCompactionQueryOverflowCount,
                _lastBlasCompactionQueryReadbackFailureCount,
                DynamicBottomLevelCount,
                DynamicBottomLevelAccelerationStructureBytes,
                PeakDynamicBottomLevelAccelerationStructureBytes,
                _lastDynamicBlasFullBuildCount,
                _lastDynamicBlasRefitCount,
                _lastDynamicBlasProxyFallbackCount,
                _lastDynamicBlasExcludedCount,
                _lastDynamicBlasBudgetDeferredCount,
                _lastDynamicBlasTopologyMismatchCount,
                _lastDynamicBlasScratchBytes,
                _lastDynamicBlasPrimitiveCount,
                _lastFallbackReason);
        }

        private void UpdateReadinessSnapshot(bool active)
        {
            RaySceneConsumer requested = _preparedRaySceneRequirement.Consumers;
            if (requested == RaySceneConsumer.None)
            {
                _readinessSnapshot = RaySceneReadinessSnapshot.Unavailable(
                    requested,
                    active ? string.Empty : _lastFallbackReason);
                return;
            }

            RaySceneGeometryCategory admitted = active
                ? _preparedRaySceneSupportedCategories
                : RaySceneGeometryCategory.None;
            RaySceneGeometryCategory complete = active
                ? admitted & _preparedRaySceneRequirement.RequiredCategories
                : RaySceneGeometryCategory.None;
            string coverageFailure = BuildRaySceneCoverageFailureDetail(
                _preparedRaySceneRequirement,
                _lastStaticInstanceCulledCount,
                _lastBlasBudgetRejectedCount,
                _lastDynamicBlasProxyFallbackCount,
                _lastDynamicBlasExcludedCount,
                _lastDynamicBlasBudgetDeferredCount,
                _preparedRaySceneCoverageFailure);
            bool requirementsComplete = active &&
                (complete & _preparedRaySceneRequirement.RequiredCategories) ==
                _preparedRaySceneRequirement.RequiredCategories &&
                string.IsNullOrEmpty(coverageFailure);
            string failureDetail = requirementsComplete
                ? string.Empty
                : !string.IsNullOrWhiteSpace(_lastFallbackReason)
                    ? _lastFallbackReason
                    : !string.IsNullOrEmpty(coverageFailure)
                        ? coverageFailure
                    : $"ray-scene geometry categories are unqualified: required={_preparedRaySceneRequirement.RequiredCategories}, supported={_preparedRaySceneSupportedCategories}";
            RaySceneGeometryCategory proxyCategories = admitted &
                (RaySceneGeometryCategory.FoliageOpaque |
                 RaySceneGeometryCategory.FoliageAlphaTested);
            _readinessSnapshot = new RaySceneReadinessSnapshot(
                requested,
                requirementsComplete ? requested : RaySceneConsumer.None,
                admitted,
                complete,
                requirementsComplete ? unchecked((uint)Math.Max(1UL, _resourceGeneration)) : 0u,
                requirementsComplete ? _raySceneContentEpoch : 0UL,
                failureDetail)
            {
                CoverageMinimum = requirementsComplete &&
                    _publishedRaySceneBoundsValid
                        ? _publishedRaySceneBounds.Min
                        : default,
                CoverageMaximum = requirementsComplete &&
                    _publishedRaySceneBoundsValid
                        ? _publishedRaySceneBounds.Max
                        : default,
                ExactCategories = requirementsComplete
                    ? admitted & ~proxyCategories
                    : RaySceneGeometryCategory.None,
                ProxyCategories = requirementsComplete
                    ? proxyCategories
                    : RaySceneGeometryCategory.None
            };
        }

        internal static string BuildRaySceneCoverageFailureDetail(
            in RaySceneRequirement requirement,
            int staticCulledCount,
            int blasBudgetRejectedCount,
            int dynamicProxyFallbackCount,
            int dynamicExcludedCount,
            int dynamicBudgetDeferredCount,
            string preparedCoverageFailure = "")
        {
            List<string> failures = [];
            if (!string.IsNullOrWhiteSpace(preparedCoverageFailure))
                failures.Add(preparedCoverageFailure.Trim());
            if (staticCulledCount > 0)
                failures.Add($"{staticCulledCount} static instances are not resident");
            if (blasBudgetRejectedCount > 0)
                failures.Add($"{blasBudgetRejectedCount} BLAS allocations were budget-rejected");
            if (requirement.RequiresCurrentPose && dynamicProxyFallbackCount > 0)
            {
                failures.Add(
                    $"{dynamicProxyFallbackCount} current-pose instances fell back to conservative proxies");
            }
            bool foliageRequired =
                (requirement.RequiredCategories &
                    (RaySceneGeometryCategory.FoliageOpaque |
                     RaySceneGeometryCategory.FoliageAlphaTested)) != 0;
            if (foliageRequired && dynamicExcludedCount > 0)
                failures.Add($"{dynamicExcludedCount} foliage instances were excluded");
            if ((requirement.RequiresCurrentPose || foliageRequired) &&
                dynamicBudgetDeferredCount > 0)
            {
                failures.Add(
                    $"{dynamicBudgetDeferredCount} dynamic BLAS builds were budget-deferred");
            }

            return string.Join("; ", failures);
        }

        private static string ResolvePreparedCoverageFailure(
            Scene scene,
            in RaySceneRequirement requirement,
            DdgiFoliageProxyFrame? foliageProxyFrame)
        {
            bool foliageRequired =
                (requirement.RequiredCategories &
                    (RaySceneGeometryCategory.FoliageOpaque |
                     RaySceneGeometryCategory.FoliageAlphaTested)) != 0;
            if (!foliageRequired || scene.FoliagePatches.Count == 0)
                return string.Empty;
            if (foliageProxyFrame == null)
                return "required foliage proxy coverage was not prepared";
            if (!string.IsNullOrWhiteSpace(foliageProxyFrame.FallbackReason))
                return foliageProxyFrame.FallbackReason.Trim();
            if (foliageProxyFrame.DroppedTriangleCount > 0 ||
                foliageProxyFrame.ExcludedPatchCount > 0)
            {
                return $"foliage proxy coverage is incomplete: " +
                    $"droppedTriangles={foliageProxyFrame.DroppedTriangleCount}, " +
                    $"excludedPatches={foliageProxyFrame.ExcludedPatchCount}";
            }
            if (foliageProxyFrame.Instances.Count == 0)
                return "required foliage proxy coverage contains no instances";
            return string.Empty;
        }

        private static RaySceneGeometryCategory ResolveSupportedCategories(
            in DdgiDynamicRayScenePolicy policy)
        {
            RaySceneGeometryCategory categories =
                RaySceneGeometryCategory.StaticOpaque |
                RaySceneGeometryCategory.DynamicOpaque |
                RaySceneGeometryCategory.AlphaTested |
                RaySceneGeometryCategory.DoubleSided |
                RaySceneGeometryCategory.VolumeTransmission |
                RaySceneGeometryCategory.WaterSurface;
            if (policy.SkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose)
                categories |= RaySceneGeometryCategory.SkinnedCurrentPose;
            if (policy.FoliageGeometryMode != DdgiFoliageGeometryMode.Excluded)
            {
                categories |= RaySceneGeometryCategory.FoliageOpaque |
                    RaySceneGeometryCategory.FoliageAlphaTested;
            }
            if (policy.TransparentGeometryMode != DdgiTransparentGeometryMode.MaskOnly)
                categories |= RaySceneGeometryCategory.ThinTransmission;
            if (policy.TransparentGeometryMode == DdgiTransparentGeometryMode.StochasticBlend)
                categories |= RaySceneGeometryCategory.AlphaBlend;
            return categories;
        }

        private void ResetFrameDiagnostics()
        {
            _lastBuildMicroseconds = 0;
            _lastBlasBuildMicroseconds = 0;
            _lastTlasBuildMicroseconds = 0;
            _lastInstanceUploadMicroseconds = 0;
            _lastBlasBuildCount = 0;
            _lastTlasBuildCount = 0;
            _lastTlasUpdateCount = 0;
            _lastTlasSkipCount = 0;
            _lastInstanceUploadBytes = 0;
            _lastRayQueryInstanceMetadataUploadBytes = 0;
            _lastStaticInstanceCandidateCount = 0;
            _lastStaticInstanceResidentCount = 0;
            _lastStaticInstanceCulledCount = 0;
            _lastBlasEvictionCount = 0;
            _lastBlasEvictionBytes = 0;
            _lastBlasBudgetRejectedCount = 0;
            _lastBlasCompactionMicroseconds = 0;
            _lastBlasCompactionQueryCount = 0;
            _lastBlasCompactionCount = 0;
            _lastBlasCompactionSourceBytes = 0;
            _lastBlasCompactionBytesSaved = 0;
            _lastBlasCompactionQueryOverflowCount = 0;
            _lastBlasCompactionQueryReadbackFailureCount = 0;
            _lastDynamicBlasFullBuildCount = 0;
            _lastDynamicBlasRefitCount = 0;
            _lastDynamicBlasProxyFallbackCount = 0;
            _lastDynamicBlasExcludedCount = 0;
            _lastDynamicBlasBudgetDeferredCount = 0;
            _lastDynamicBlasTopologyMismatchCount = 0;
            _lastDynamicBlasScratchBytes = 0;
            _lastDynamicBlasPrimitiveCount = 0;
        }

        private void RecalculateAccelerationStructureBytes()
        {
            ulong bytes = CalculateTopLevelFrameSlotBytes();
            ulong bottomLevelBytes = 0;
            ulong compactedBytesSaved = 0;
            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                bottomLevelBytes = checked(bottomLevelBytes + blas.Size);
                bytes = checked(bytes + blas.Size);
                compactedBytesSaved = checked(
                    compactedBytesSaved + blas.UncompactedSize - blas.Size);
            }
            AddOpacityMicromapBlasMemory(
                ref bytes,
                ref bottomLevelBytes,
                ref compactedBytesSaved);
            ulong dynamicBytes = 0;
            foreach (DynamicBottomLevelAccelerationStructure blas in _dynamicBlasPool.Values)
            {
                dynamicBytes = checked(dynamicBytes + blas.Size);
                bottomLevelBytes = checked(bottomLevelBytes + blas.Size);
                bytes = checked(bytes + blas.Size);
            }
            _dynamicBlasBytes = dynamicBytes;
            BottomLevelAccelerationStructureBytes = bottomLevelBytes;
            _bottomLevelAccelerationStructureCompactedBytesSaved =
                compactedBytesSaved;
            AccelerationStructureBytes = bytes;
        }

        private int GetPendingBlasCompactionCount()
        {
            int count = _readyBlasCompactions.Count;
            for (int i = 0; i < _pendingBlasCompactionQueries.Length; i++)
                count = checked(count + _pendingBlasCompactionQueries[i].Count);
            return count;
        }

        private void DestroyTopLevelAccelerationStructure(bool defer)
        {
            if (_tlas.Handle.Handle == 0)
                return;

            if (defer)
                RetireAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer, _tlas.Size);
            else
                DestroyAccelerationStructureResource(_tlas.Handle, _tlas.StorageBuffer);
            _tlas = default;
            if ((uint)_currentTlasFrameSlot <
                (uint)RenderingConstants.FramesInFlight)
            {
                _tlasFrameSlots[_currentTlasFrameSlot] = default;
            }
            AdvanceResourceGeneration();
            _hasTlasInstanceSignature = false;
            _lastTlasInstanceSignature = 0;
            _lastTlasInstanceCount = 0;
        }

        private void DestroyAllTopLevelAccelerationStructures(bool defer)
        {
            PersistTopLevelFrameSlot();
            for (int i = 0; i < _tlasFrameSlots.Length; i++)
            {
                TopLevelAccelerationStructure slot = _tlasFrameSlots[i];
                if (slot.Handle.Handle != 0)
                {
                    if (defer)
                        RetireAccelerationStructureResource(
                            slot.Handle,
                            slot.StorageBuffer,
                            slot.Size);
                    else
                        DestroyAccelerationStructureResource(
                            slot.Handle,
                            slot.StorageBuffer);
                }
                _tlasFrameSlots[i] = default;
                _tlasInstanceSignatures[i] = 0;
                _tlasHasInstanceSignatures[i] = false;
                _tlasInstanceCounts[i] = 0;
            }
            _tlas = default;
            _lastTlasInstanceSignature = 0;
            _hasTlasInstanceSignature = false;
            _lastTlasInstanceCount = 0;
            if (_currentTlasFrameSlot >= 0)
                AdvanceResourceGeneration();
        }

        private void DestroyBottomLevelAccelerationStructures(bool defer)
        {
            if (_blasCache.Count == 0)
                return;

            foreach (BottomLevelAccelerationStructure blas in _blasCache.Values)
            {
                if (defer)
                    RetireAccelerationStructureResource(blas.Handle, blas.StorageBuffer, blas.Size);
                else
                    DestroyAccelerationStructureResource(blas.Handle, blas.StorageBuffer);
            }
            _blasCache.Clear();
            AdvanceResourceGeneration();
        }

        private void DestroyDynamicBottomLevelAccelerationStructures(bool defer)
        {
            if (_dynamicBlasPool.Count == 0)
                return;

            foreach (DynamicBottomLevelAccelerationStructure blas in _dynamicBlasPool.Values)
            {
                if (defer)
                    RetireAccelerationStructureResource(
                        blas.Handle,
                        blas.StorageBuffer,
                        blas.Size,
                        AccelerationStructureRetirementOwner.Dynamic);
                else
                    DestroyAccelerationStructureResource(blas.Handle, blas.StorageBuffer);
            }
            _dynamicBlasPool.Clear();
            _dynamicBlasBytes = 0;
            AdvanceResourceGeneration();
        }

        private void BeginFrameResourceRetirement()
        {
            _frameSerial++;
            DrainRetiredResources(force: false);
        }

        private void RetireAccelerationStructureResource(
            AccelerationStructureKHR accelerationStructure,
            BufferHandle storageBuffer,
            ulong size,
            AccelerationStructureRetirementOwner owner =
                AccelerationStructureRetirementOwner.General)
        {
            _retiredAccelerationStructures.Add(new RetiredAccelerationStructureResource(
                accelerationStructure,
                storageBuffer,
                size,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL,
                owner));
            _retiredAccelerationStructureBytes = checked(_retiredAccelerationStructureBytes + size);
            if (owner == AccelerationStructureRetirementOwner.Dynamic)
            {
                _retiredDynamicAccelerationStructureBytes = checked(
                    _retiredDynamicAccelerationStructureBytes + size);
            }
        }

        private void RetireBufferResource(BufferHandle buffer)
        {
            ulong size = 0;
            if (buffer.IsValid)
                size = _bufferManager.GetBufferSize(buffer);
            _retiredBuffers.Add(new RetiredBufferResource(
                buffer,
                size,
                _frameSerial + (ulong)RenderingConstants.FramesInFlight + 1UL));
            _retiredBufferBytes = SaturatingAdd(_retiredBufferBytes, size);
        }

        private void DrainRetiredResources(bool force)
        {
            for (int i = _retiredAccelerationStructures.Count - 1; i >= 0; i--)
            {
                RetiredAccelerationStructureResource retired = _retiredAccelerationStructures[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                DestroyAccelerationStructureResource(retired.AccelerationStructure, retired.StorageBuffer);
                _retiredAccelerationStructureBytes = _retiredAccelerationStructureBytes >= retired.Size
                    ? _retiredAccelerationStructureBytes - retired.Size
                    : 0;
                if (retired.Owner ==
                    AccelerationStructureRetirementOwner.Dynamic)
                {
                    _retiredDynamicAccelerationStructureBytes =
                        _retiredDynamicAccelerationStructureBytes >= retired.Size
                            ? _retiredDynamicAccelerationStructureBytes - retired.Size
                            : 0UL;
                }
                _retiredAccelerationStructures.RemoveAt(i);
            }

            for (int i = _retiredBuffers.Count - 1; i >= 0; i--)
            {
                RetiredBufferResource retired = _retiredBuffers[i];
                if (!force && retired.RetireAfterFrameSerial > _frameSerial)
                    continue;

                if (retired.Buffer.IsValid)
                    _bufferManager.DestroyBuffer(retired.Buffer);
                _retiredBufferBytes = _retiredBufferBytes >= retired.Size
                    ? _retiredBufferBytes - retired.Size
                    : 0;
                _retiredBuffers.RemoveAt(i);
            }
            // OMM retirement is ordered after matching BLAS retirement so no
            // acceleration structure can outlive the micromap it consumed.
            DrainRetiredOpacityMicromapResources(force);
        }

        private void DestroyAccelerationStructureResource(AccelerationStructureKHR accelerationStructure, BufferHandle storageBuffer)
        {
            if (accelerationStructure.Handle != 0)
                _khrAccelerationStructure?.DestroyAccelerationStructure(_context.Device, accelerationStructure, null);
            if (storageBuffer.IsValid)
                _bufferManager.DestroyBuffer(storageBuffer);
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000.0 / Stopwatch.Frequency);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_opacityMicromapRuntimeRegistrations is not null)
            {
                _materialManager.MaterialChanged -=
                    OnOpacityMicromapMaterialChanged;
            }
            DisposeOpacityMicromapGpuRuntime();
            _opacityMicromapNativeLifecycleHost.Dispose();
            ClearPublishedRaySceneInstances();
            DestroyAllTopLevelAccelerationStructures(defer: false);
            DestroyBottomLevelAccelerationStructures(defer: false);
            DestroyDynamicBottomLevelAccelerationStructures(defer: false);
            if (_scratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_scratchBuffer);
            if (_instanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_instanceBuffer);
            if (_rayQueryInstanceBuffer.IsValid)
                _bufferManager.DestroyBuffer(_rayQueryInstanceBuffer);
            DrainRetiredResources(force: true);
            for (int i = 0; i < _blasCompactionQueryPools.Length; i++)
            {
                if (_blasCompactionQueryPools[i].Handle != 0)
                {
                    _context.Api.DestroyQueryPool(
                        _context.Device,
                        _blasCompactionQueryPools[i],
                        null);
                    _blasCompactionQueryPools[i] = default;
                }
            }

        }

        internal readonly record struct StaticOpaqueInstance
        {
            public StaticOpaqueInstance(
                MeshHandle mesh,
                MeshInfo meshInfo,
                uint materialIndex,
                CoreMatrix4x4 worldMatrix,
                AccelerationStructureGeometryDomain domain = AccelerationStructureGeometryDomain.Static,
                GeometryInstanceFlagsKHR instanceFlags = GeometryInstanceFlagsKHR.ForceOpaqueBitKhr)
            {
                Mesh = mesh;
                MeshInfo = meshInfo;
                MaterialIndex = materialIndex;
                WorldMatrix = worldMatrix;
                Domain = domain;
                InstanceFlags = instanceFlags;
                ObjectIdentity = Guid.Empty;
                StableInstanceIdentity = 1u;
                MaterialRevision = 1u;
                TransformRevision = 1UL;
                PackedAlpha = 0u;
                PackedDecalLayerAndOrder = 0u;
                DecalDepthTolerance = 0.002f;
                DecalDepthBias = 0.0f;
                RepresentationGeneration = 1u;
                GeometryClass = domain switch
                {
                    AccelerationStructureGeometryDomain.Dynamic => DdgiRayGeometryClass.RigidOpaque,
                    AccelerationStructureGeometryDomain.Skinned => DdgiRayGeometryClass.ConservativeProxy,
                    AccelerationStructureGeometryDomain.Foliage => DdgiRayGeometryClass.AuthoredFoliage,
                    _ => DdgiRayGeometryClass.StaticOpaque
                };
                GeometryFlags = domain == AccelerationStructureGeometryDomain.Skinned
                    ? DdgiRayGeometryFlags.ConservativeProxy
                    : DdgiRayGeometryFlags.None;
                VertexBufferIndex = BindlessIndex.VertexPositionBuffer;
                VertexOffset = meshInfo.VertexOffset;
                VertexStride = checked((uint)VertexPositionStride);
                VertexFormat = DdgiRayVertexFormat.SplitStatic;
                PositionOffset = 0u;
                NormalOffset = 0u;
                TangentOffset = 16u;
                TexCoord0Offset = 0u;
                TexCoord1Offset = 8u;
                ColorOffset = 16u;
                IndexBufferIndex = BindlessIndex.IndexBuffer;
                IndexOffset = meshInfo.IndexOffset;
                GeometryVertexBuffer = BufferHandle.Invalid;
                GeometryIndexBuffer = BufferHandle.Invalid;
                UsesDynamicBlas = false;
                FrameSlot = 0;
            }

            public MeshHandle Mesh { get; init; }
            public MeshInfo MeshInfo { get; init; }
            public uint MaterialIndex { get; init; }
            public CoreMatrix4x4 WorldMatrix { get; init; }
            public AccelerationStructureGeometryDomain Domain { get; init; }
            public GeometryInstanceFlagsKHR InstanceFlags { get; init; }
            public Guid ObjectIdentity { get; init; }
            public uint StableInstanceIdentity { get; init; }
            public uint MaterialRevision { get; init; }
            public ulong TransformRevision { get; init; }
            public uint PackedAlpha { get; init; }
            public uint PackedDecalLayerAndOrder { get; init; }
            public float DecalDepthTolerance { get; init; }
            public float DecalDepthBias { get; init; }
            public uint RepresentationGeneration { get; init; }
            public DdgiRayGeometryClass GeometryClass { get; init; }
            public DdgiRayGeometryFlags GeometryFlags { get; init; }
            public uint VertexBufferIndex { get; init; }
            public uint VertexOffset { get; init; }
            public uint VertexStride { get; init; }
            public DdgiRayVertexFormat VertexFormat { get; init; }
            public uint PositionOffset { get; init; }
            public uint NormalOffset { get; init; }
            public uint TangentOffset { get; init; }
            public uint TexCoord0Offset { get; init; }
            public uint TexCoord1Offset { get; init; }
            public uint ColorOffset { get; init; }
            public uint IndexBufferIndex { get; init; }
            public uint IndexOffset { get; init; }
            public BufferHandle GeometryVertexBuffer { get; init; }
            public BufferHandle GeometryIndexBuffer { get; init; }
            public bool UsesDynamicBlas { get; init; }
            public int FrameSlot { get; init; }
        }

        private readonly record struct AccelerationStructurePreparationIdentity(
            ulong SceneContentRevision,
            AccelerationStructureResidencyPolicy ResidencyPolicy,
            DdgiDynamicRayScenePolicy DynamicPolicy,
            RaySceneRequirement Requirement);

        private sealed record PreparedRayScene(
            bool Enabled,
            int FrameIndex,
            AccelerationStructureResidencyPolicy ResidencyPolicy,
            DdgiDynamicRayScenePolicy DynamicPolicy,
            RaySceneRequirement Requirement,
            ulong SceneContentRevision,
            bool HasAlphaCandidateGeometry,
            bool HasThinTransmissionGeometry,
            StaticOpaqueInstance[] Instances);

        private sealed class BottomLevelAccelerationStructure
        {
            public BottomLevelAccelerationStructure(
                AccelerationStructureKHR handle,
                BufferHandle storageBuffer,
                ulong size)
                : this(handle, storageBuffer, size, size)
            {
            }

            public BottomLevelAccelerationStructure(
                AccelerationStructureKHR handle,
                BufferHandle storageBuffer,
                ulong size,
                ulong uncompactedSize)
            {
                Handle = handle;
                StorageBuffer = storageBuffer;
                Size = size;
                UncompactedSize = Math.Max(size, uncompactedSize);
            }

            public AccelerationStructureKHR Handle { get; }
            public BufferHandle StorageBuffer { get; }
            public ulong Size { get; }
            public ulong UncompactedSize { get; }
            public ulong LastUsedFrameSerial { get; set; }
        }

        private readonly record struct DynamicBlasKey(
            Guid ObjectIdentity,
            MeshHandle Mesh,
            int FrameSlot);

        private sealed class DynamicBottomLevelAccelerationStructure
        {
            public DynamicBottomLevelAccelerationStructure(
                AccelerationStructureKHR handle,
                BufferHandle storageBuffer,
                ulong size,
                uint vertexCount,
                uint primitiveCount,
                uint vertexStride,
                DdgiRayVertexFormat vertexFormat,
                GeometryInstanceFlagsKHR instanceFlags,
                ulong representationRevision)
            {
                Handle = handle;
                StorageBuffer = storageBuffer;
                Size = size;
                VertexCount = vertexCount;
                PrimitiveCount = primitiveCount;
                VertexStride = vertexStride;
                VertexFormat = vertexFormat;
                InstanceFlags = instanceFlags;
                RepresentationRevision = representationRevision;
            }

            public AccelerationStructureKHR Handle { get; }
            public BufferHandle StorageBuffer { get; }
            public ulong Size { get; }
            public uint VertexCount { get; }
            public uint PrimitiveCount { get; }
            public uint VertexStride { get; }
            public DdgiRayVertexFormat VertexFormat { get; }
            public GeometryInstanceFlagsKHR InstanceFlags { get; }
            public ulong RepresentationRevision { get; set; }
            public ulong LastUsedFrameSerial { get; set; }
        }

        private readonly record struct PendingBlasCompactionQuery(
            MeshHandle Mesh,
            BottomLevelAccelerationStructure Source);

        private readonly record struct ReadyBlasCompaction(
            MeshHandle Mesh,
            BottomLevelAccelerationStructure Source,
            ulong CompactedSize);

        private readonly record struct StaticResidencyCandidate(
            StaticOpaqueInstance Instance,
            float DistanceSquared);

        private readonly record struct RayQueryMaterialContract(
            uint MaterialRevision,
            uint PackedAlpha,
            uint PackedDecalLayerAndOrder,
            float DecalDepthTolerance,
            float DecalDepthBias,
            DdgiRayGeometryFlags GeometryFlags,
            DdgiAccelerationStructureVisibilityPolicy VisibilityPolicy)
        {
            public static RayQueryMaterialContract Create(
                MaterialRenderMetadata metadata,
                GPUMaterialData material,
                DdgiAccelerationStructureVisibilityPolicy visibilityPolicy)
            {
                DdgiRayGeometryFlags flags = DdgiRayGeometryFlags.None;
                if (metadata.RenderMode == MaterialRenderMode.Mask)
                    flags |= DdgiRayGeometryFlags.AlphaMask;
                if (metadata.RenderMode == MaterialRenderMode.Blend)
                    flags |= DdgiRayGeometryFlags.AlphaBlend;
                if (metadata.BlendMode == MaterialBlendMode.PremultipliedAlpha)
                    flags |= DdgiRayGeometryFlags.PremultipliedAlpha;
                if (metadata.TransmissionPolicy == GiTransmissionPolicy.ThinSurface)
                    flags |= DdgiRayGeometryFlags.ThinTransmission;
                if (metadata.TransmissionPolicy == GiTransmissionPolicy.Volume)
                    flags |= DdgiRayGeometryFlags.VolumeTransmission;
                if (metadata.OpticalBoundary == OpticalBoundaryKind.WaterSurface)
                    flags |= DdgiRayGeometryFlags.WaterSurface;
                if (metadata.DoubleSided)
                    flags |= DdgiRayGeometryFlags.TwoSided;
                if (metadata.IsGeometryDecal)
                    flags |= DdgiRayGeometryFlags.DecalOverlay;

                float depthBias = float.IsFinite(metadata.DecalDepthBias)
                    ? metadata.DecalDepthBias
                    : 0.0f;
                float depthTolerance = Math.Max(0.002f, MathF.Abs(depthBias) * 2.0f);
                return new RayQueryMaterialContract(
                    Math.Max(1u, material.MaterialRevision),
                    DdgiRayQueryInstanceAbi.PackAlpha(metadata.BlendMode, metadata.AlphaCutoff),
                    DdgiRayQueryInstanceAbi.PackDecalLayerAndOrder(
                        metadata.DecalLayer,
                        material.MaterialRevision),
                    depthTolerance,
                    depthBias,
                    flags,
                    visibilityPolicy);
            }

            public DdgiRayGeometryClass ResolveGeometryClass(
                AccelerationStructureGeometryDomain domain)
            {
                if (VisibilityPolicy == DdgiAccelerationStructureVisibilityPolicy.DecalOverlayCandidate)
                    return DdgiRayGeometryClass.DecalOverlay;
                if ((GeometryFlags & DdgiRayGeometryFlags.ThinTransmission) != 0)
                    return DdgiRayGeometryClass.ThinTransmission;
                if ((GeometryFlags & DdgiRayGeometryFlags.WaterSurface) != 0)
                    return DdgiRayGeometryClass.WaterSurface;
                if ((GeometryFlags & DdgiRayGeometryFlags.VolumeTransmission) != 0)
                    return DdgiRayGeometryClass.VolumeTransmission;
                if ((GeometryFlags & DdgiRayGeometryFlags.AlphaBlend) != 0)
                    return DdgiRayGeometryClass.AlphaBlend;
                if ((GeometryFlags & DdgiRayGeometryFlags.AlphaMask) != 0)
                    return DdgiRayGeometryClass.AlphaMask;
                return domain switch
                {
                    AccelerationStructureGeometryDomain.Dynamic => DdgiRayGeometryClass.RigidOpaque,
                    AccelerationStructureGeometryDomain.Skinned => DdgiRayGeometryClass.ConservativeProxy,
                    AccelerationStructureGeometryDomain.Foliage => DdgiRayGeometryClass.AuthoredFoliage,
                    _ => DdgiRayGeometryClass.StaticOpaque
                };
            }
        }

        private readonly record struct TopLevelAccelerationStructure(
            AccelerationStructureKHR Handle,
            BufferHandle StorageBuffer,
            ulong Size);

        private readonly record struct RetiredAccelerationStructureResource(
            AccelerationStructureKHR AccelerationStructure,
            BufferHandle StorageBuffer,
            ulong Size,
            ulong RetireAfterFrameSerial,
            AccelerationStructureRetirementOwner Owner);

        private enum AccelerationStructureRetirementOwner : byte
        {
            General = 0,
            Dynamic = 1
        }

        private readonly record struct RetiredBufferResource(
            BufferHandle Buffer,
            ulong Size,
            ulong RetireAfterFrameSerial);
    }

    /// <summary>One acceleration-structure backing allocation used by ray queries.</summary>
    public readonly record struct AccelerationStructureStorageBuffer(
        BufferHandle Handle,
        ulong ByteSize,
        string DebugName);

    /// <summary>
    /// Bounds the streamable static portion of the GI ray-query representation.
    /// Dynamic render objects bypass distance trimming so their detailed geometry
    /// remains authoritative while they are present in the scene.
    /// </summary>
    public readonly record struct AccelerationStructureResidencyPolicy(
        bool Enabled,
        Njulf.Core.Math.Vector3 CameraPosition,
        ulong MemoryBudgetBytes,
        float StaticResidentDistance,
        int MaximumStaticInstances,
        int EvictionGraceFrames,
        bool AllowStaticMemoryCulling = true)
    {
        public static AccelerationStructureResidencyPolicy Disabled => new(
            false,
            Njulf.Core.Math.Vector3.Zero,
            ulong.MaxValue,
            float.MaxValue,
            int.MaxValue,
            0,
            false);

        internal ulong EffectiveMemoryBudgetBytes => Enabled
            ? Math.Max(MemoryBudgetBytes, 16UL)
            : ulong.MaxValue;

        internal ulong EffectiveTransientMemoryBudgetBytes => Enabled
            ? AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(EffectiveMemoryBudgetBytes)
            : ulong.MaxValue;

        internal ulong EffectiveScratchMemoryBudgetBytes => Enabled
            ? AccelerationStructureManager.CalculateScratchMemoryBudgetBytes(EffectiveMemoryBudgetBytes)
            : ulong.MaxValue;
    }

    /// <summary>
    /// Content-dependent geometry policy and hard per-frame dynamic-AS caps.
    /// The static AS residency budget remains independently owned by
    /// <see cref="AccelerationStructureResidencyPolicy"/>.
    /// </summary>
    public readonly record struct DdgiDynamicRayScenePolicy(
        DdgiSkinnedGeometryMode SkinnedGeometryMode,
        DdgiTransparentGeometryMode TransparentGeometryMode,
        DdgiFoliageGeometryMode FoliageGeometryMode,
        bool GeometryDecalsEnabled,
        bool AlphaMaskedTransportEnabled,
        ulong DynamicStorageBudgetBytes,
        ulong DynamicScratchBudgetBytes,
        int MaximumBuildsPerFrame,
        int MaximumPrimitivesPerFrame,
        int DecalCandidateLimit)
    {
        public static DdgiDynamicRayScenePolicy LegacyBaseline => new(
            DdgiSkinnedGeometryMode.ConservativeProxy,
            DdgiTransparentGeometryMode.MaskAndThin,
            DdgiFoliageGeometryMode.Excluded,
            GeometryDecalsEnabled: false,
            AlphaMaskedTransportEnabled: true,
            DynamicStorageBudgetBytes: 0,
            DynamicScratchBudgetBytes: 0,
            MaximumBuildsPerFrame: 0,
            MaximumPrimitivesPerFrame: 0,
            DecalCandidateLimit: 0);

        internal ulong EffectiveDynamicStorageBudgetBytes =>
            SkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose ||
            FoliageGeometryMode ==
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                ? DynamicStorageBudgetBytes
                : 0UL;
        internal ulong EffectiveDynamicScratchBudgetBytes =>
            SkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose ||
            FoliageGeometryMode ==
                DdgiFoliageGeometryMode.AuthoredAndProceduralProxy
                ? DynamicScratchBudgetBytes
                : 0UL;
        internal int EffectiveMaximumBuildsPerFrame => Math.Max(0, MaximumBuildsPerFrame);
        internal int EffectiveMaximumPrimitivesPerFrame => Math.Max(0, MaximumPrimitivesPerFrame);
        internal int EffectiveDecalCandidateLimit => Math.Clamp(DecalCandidateLimit, 0, 64);
    }

    public readonly record struct RaySceneBuildPlan(
        bool Enabled,
        int InstanceCount,
        int CurrentPoseInstanceCount,
        int TransparentInstanceCount,
        int DecalInstanceCount,
        int FrameIndex,
        ulong SceneContentRevision);

    internal enum AccelerationStructureGeometryDomain
    {
        Static = 0,
        Dynamic = 1,
        Skinned = 2,
        Foliage = 3
    }

    internal enum DdgiAccelerationStructureVisibilityPolicy
    {
        OpaqueTriangles = 0,
        AlphaMaskTested = 1,
        ExcludedTransparent = 2,
        ExcludedGeometryDecal = 3,
        SkinnedBindPoseProxy = 4,
        FoliageProxyPending = 5,
        SkinnedAlphaMaskTestedProxy = 6,
        ThinSurfaceCandidateTested = 7,
        StochasticAlphaBlend = 8,
        DecalOverlayCandidate = 9,
        CurrentPoseSkinned = 10,
        ExcludedSkinned = 11,
        VolumeBoundaryCandidateTested = 12
    }

    internal enum TopLevelAccelerationStructureBuildAction
    {
        Build = 0,
        Update = 1,
        Skip = 2
    }

    internal readonly record struct DdgiAccelerationStructureGeometryPolicy(
        bool Include,
        byte InstanceMask,
        GeometryInstanceFlagsKHR InstanceFlags,
        DdgiAccelerationStructureVisibilityPolicy VisibilityPolicy,
        string Reason);

    public readonly record struct AccelerationStructureFrameStats(
        bool Supported,
        bool Active,
        int BottomLevelCount,
        int TopLevelInstanceCount,
        ulong AccelerationStructureBytes,
        ulong ScratchBufferBytes,
        ulong InstanceBufferBytes,
        ulong RayQueryInstanceMetadataBufferBytes,
        long BuildMicroseconds,
        long BlasBuildMicroseconds,
        long TlasBuildMicroseconds,
        long InstanceUploadMicroseconds,
        int BlasBuildCount,
        int TlasBuildCount,
        int TlasUpdateCount,
        int TlasSkipCount,
        ulong InstanceUploadBytes,
        ulong RayQueryInstanceMetadataUploadBytes,
        int StaticInstanceCandidateCount,
        int StaticInstanceResidentCount,
        int StaticInstanceCulledCount,
        int BlasEvictionCount,
        ulong BlasEvictionBytes,
        int BlasBudgetRejectedCount,
        ulong BottomLevelAccelerationStructureBytes,
        ulong TopLevelAccelerationStructureBytes,
        ulong RetiredAccelerationStructureBytes,
        long BlasCompactionMicroseconds,
        int BlasCompactionQueryCount,
        int BlasCompactionCount,
        ulong BlasCompactionSourceBytes,
        ulong BlasCompactionBytesSaved,
        ulong BottomLevelAccelerationStructureCompactedBytesSaved,
        int PendingBlasCompactionCount,
        int BlasCompactionQueryOverflowCount,
        int BlasCompactionQueryReadbackFailureCount,
        int DynamicBottomLevelCount,
        ulong DynamicBottomLevelBytes,
        ulong PeakDynamicBottomLevelBytes,
        int DynamicFullBuildCount,
        int DynamicRefitCount,
        int DynamicProxyFallbackCount,
        int DynamicExcludedCount,
        int DynamicBudgetDeferredCount,
        int DynamicTopologyMismatchCount,
        ulong DynamicScratchBytes,
        ulong DynamicPrimitiveCount,
        string FallbackReason);
}

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Assets.Validation;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public struct MeshInfo
    {
        public Vector3 BoundingBoxMin;
        public Vector3 BoundingBoxMax;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint GpuIndexCount;
        public uint CoarseRayProxyIndexOffset;
        public uint CoarseRayProxyIndexCount;
        public uint MeshMetadataOffset;
        /// <summary>
        /// Runtime address consumed by submission. Managed-residency meshes
        /// carry the virtual-address high bit here.
        /// </summary>
        public uint MeshletOffset;
        /// <summary>
        /// Direct record-buffer offset used only for allocation/lifetime.
        /// </summary>
        public uint PhysicalMeshletOffset;
        public uint MeshletCount;
        public uint MeshletLod1Offset;
        public uint MeshletLod1Count;
        public uint MeshletLod2Offset;
        public uint MeshletLod2Count;
        public uint MeshletLodGeneratedCount;
        public uint GpuMeshletRecordCount;
        public uint HierarchyNodeOffset;
        public uint HierarchyNodeCount;
        public uint HierarchyRootNode;
        public float MeshletLod1SimplificationError;
        public float MeshletLod2SimplificationError;
        public uint LocalVertexIndexOffset;
        public uint LocalVertexIndexCount;
        public uint LocalTriangleIndexOffset;
        public uint LocalTriangleIndexCount;
        public uint MeshletTriangleSum;
        public uint MeshletVertexSum;
        public uint SmallMeshletsUnder16Triangles;
        public uint SmallMeshletsUnder32Triangles;
        public uint SkinningDataOffset;
        public uint SkinningDataCount;
        public bool IsSkinned;
        public bool HasVertexColor;
        public bool HasUv1;
        public bool HasTangents;
        public bool UsesManagedPhysicalResidency;
        public uint StreamingRangeIndex;
        public GpuMeshResidencyFlags ResidencyFlags;
        public ModelGiCausticHeroTopologyEvidence CausticTopologyEvidence;

        public readonly uint EffectiveGpuMeshletRecordCount =>
            !UsesManagedPhysicalResidency && GpuMeshletRecordCount == 0
                ? MeshletLodGeneratedCount
                : GpuMeshletRecordCount;
        public readonly uint EffectivePhysicalMeshletOffset =>
            UsesManagedPhysicalResidency
                ? PhysicalMeshletOffset
                : MeshletOffset;
        public readonly uint EffectiveGpuIndexCount =>
            GpuIndexCount == 0 ? IndexCount : GpuIndexCount;
        public readonly bool UsesCoarseRayProxy =>
            !IsSkinned && CoarseRayProxyIndexCount >= 3;
    }

    public sealed unsafe class MeshManager : IDisposable
    {
        public const long MaximumRuntimeEmissiveTriangleBytes = 16L * 1024L * 1024L;
        public const ulong MaximumRetainedDeadMeshBytes =
            1UL * 1024UL * 1024UL * 1024UL;
        // The task/mesh shader contract permits up to 64 vertices. LOD0 deliberately
        // uses a tighter cap for work distribution, while range validation retains the
        // shader-wide limit.
        private const int MaxVerticesPerMeshlet = 64;
        private const int Lod0MaxVerticesPerMeshlet = 48;
        private const int Lod0MaxTrianglesPerMeshlet = 64;
        private const int GreedyFallbackTriangleSearchWindow = 512;
        private const ulong InitialIndexBufferSize = 16 * 1024 * 1024;
        private const ulong InitialMeshMetadataBufferSize = 1 * 1024 * 1024;
        private const ulong InitialMeshletBufferSize = 4 * 1024 * 1024;
        private const ulong InitialMeshletVertexIndexBufferSize = 4 * 1024 * 1024;
        private const ulong InitialMeshletTriangleIndexBufferSize = 4 * 1024 * 1024;
        private const ulong InitialSkinningDataBufferSize = 1 * 1024 * 1024;
        private const ulong InitialVertexPositionBufferSize = 4 * 1024 * 1024;
        private const ulong InitialVertexNormalTangentBufferSize = 8 * 1024 * 1024;
        private const ulong InitialVertexUvColorBufferSize = 8 * 1024 * 1024;
        private const ulong BufferGrowthFactor = 2;
        private const ulong UploadStagingAlignment = StagingRing.DefaultMinAlignment;
        private const ulong ReusableUploadStagingGranularity =
            1UL * 1024UL * 1024UL;

        private static readonly ulong VertexPositionStride = (ulong)Marshal.SizeOf<GPUVertexPositionStream>();
        private static readonly ulong VertexNormalTangentStride = (ulong)Marshal.SizeOf<GPUVertexNormalTangentStream>();
        private static readonly ulong VertexUvColorStride = (ulong)Marshal.SizeOf<GPUVertexUvColorStream>();
        private static readonly ulong IndexStride = sizeof(uint);
        private static readonly ulong MeshMetadataStride = (ulong)Marshal.SizeOf<GPUMeshInfo>();
        private static readonly ulong MeshletStride =
            (ulong)Marshal.SizeOf<GPUPackedMeshlet>();
        private static readonly ulong SkinningDataStride = (ulong)Marshal.SizeOf<GPUVertexSkinningData>();
        internal const BufferUsageFlags AccelerationStructureGeometryInputUsage =
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
        internal const BufferUsageFlags VertexPositionBufferUsage =
            BufferUsageFlags.StorageBufferBit |
            AccelerationStructureGeometryInputUsage;
        internal const BufferUsageFlags IndexBufferUsage =
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.IndexBufferBit |
            AccelerationStructureGeometryInputUsage;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing? _stagingRing;
        private readonly FenceBasedDeleter? _deleter;
        private readonly SynchronizationManager? _synchronizationManager;
        private readonly object _lock = new object();

        private BufferHandle _indexBuffer;
        private BufferHandle _meshMetadataBuffer;
        private BufferHandle _meshletBuffer;
        private BufferHandle _meshletVertexIndexBuffer;
        private BufferHandle _meshletTriangleIndexBuffer;
        private BufferHandle _skinningDataBuffer;
        private BufferHandle _vertexPositionBuffer;
        private BufferHandle _vertexNormalTangentBuffer;
        private BufferHandle _vertexUvColorBuffer;
        private BufferHandle _reusableUploadStagingBuffer;
        private ulong _reusableUploadStagingBufferSize;

        private ulong _vertexPositionBytesUsed;
        private ulong _vertexNormalTangentBytesUsed;
        private ulong _vertexUvColorBytesUsed;
        private ulong _indexBytesUsed;
        private ulong _meshMetadataBytesUsed;
        private ulong _meshletBytesUsed;
        private ulong _meshletVertexIndexBytesUsed;
        private ulong _meshletTriangleIndexBytesUsed;
        private ulong _skinningDataBytesUsed;
        private long _runtimeEmissiveTriangleBytes;
        // Meshlet quality is an inventory diagnostic. Cache its sorted view
        // until a mesh upload or final release changes the authoritative set.
        private bool _meshletQualityDiagnosticsDirty = true;
        private int _cachedMeshletQualityEntryLimit = -1;
        private IReadOnlyList<MeshletQualityEntry>
            _cachedMeshletQualityEntries =
                Array.Empty<MeshletQualityEntry>();

        private readonly List<MeshInfo> _meshes = new List<MeshInfo>();
        private readonly List<Meshlet> _meshlets = new List<Meshlet>();
        private readonly ManagedCpuMeshletCache _managedCpuMeshlets = new();
        // Retained, immutable triangle inputs for bounded CPU-built transport
        // tables (currently emissive-mesh importance sampling). Raster/AS data
        // remains authoritative on the GPU; this cache avoids readback stalls.
        private readonly List<MeshTransportGeometry> _transportGeometry = new();
        private readonly MeshSlotLifetimeTable _meshLifetimes = new();
        // A descriptor rollback failure makes it unsafe to destroy candidate
        // buffers immediately. Retain those rare resources until the bindless
        // heap has been disposed during renderer shutdown.
        private readonly List<BufferHandle> _quarantinedUploadBuffers = new();
        private readonly List<Fence> _quarantinedUploadFences = new();
        private long _postCommitCleanupFailureCount;
        private Exception? _lastPostCommitCleanupFailure;
        private long _retainedDeadMeshBudgetRejectionCount;
        private long _meshBufferGrowthRetryCount;
        private long _meshBufferGrowthRetrySuccessCount;
        private long _meshBufferCompactionOutOfDeviceMemorySkipCount;
        private BindlessHeap? _registeredBindlessHeap;
        private BufferHandle _registeredVertexBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredIndexBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredMeshMetadataBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredMeshletBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredMeshletVertexIndexBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredMeshletTriangleIndexBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredSkinningDataBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredVertexPositionBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredVertexNormalTangentBuffer = BufferHandle.Invalid;
        private BufferHandle _registeredVertexUvColorBuffer = BufferHandle.Invalid;
        private MeshRegistrationUpload? _activeRegistrationUpload;
        private bool _disposed;
        private bool _disposeCompleted;
        internal Action<MeshManagerDisposalResource>?
            DisposalFaultInjector
        {
            get;
            set;
        }

        public sealed class MeshRegistrationData
        {
            private MeshTransportGeometry? _preparedTransportGeometry;

            public MeshRegistrationData(
                GPUVertex[] vertices,
                uint[] indices,
                bool generateMeshlets = true,
                GPUVertexSkinningData[]? skinningData = null,
                GiPrimitiveTransportProfile? primitiveTransportProfile = null,
                ModelGiCausticHeroTopologyEvidence causticTopologyEvidence = default)
            {
                Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
                Indices = indices ?? throw new ArgumentNullException(nameof(indices));
                Positions = ExtractPositions(vertices);
                VertexPositions = BuildVertexPositionStream(vertices);
                VertexNormalTangents = BuildVertexNormalTangentStream(vertices);
                VertexUvColors = BuildVertexUvColorStream(vertices);
                GenerateMeshlets = generateMeshlets;
                SkinningData = skinningData ?? Array.Empty<GPUVertexSkinningData>();
                Meshlets = Array.Empty<Meshlet>();
                LocalVertexIndices = Array.Empty<uint>();
                LocalTriangleIndices = Array.Empty<uint>();
                PrimitiveTransportProfile = ValidateAndCloneTransportProfile(
                    primitiveTransportProfile,
                    indices.Length / 3);
                CausticTopologyEvidence = ValidateCausticTopologyEvidence(
                    Positions,
                    indices,
                    SkinningData.Length > 0,
                    causticTopologyEvidence);
                PrepareRegistrationMetadata();
            }

            internal MeshRegistrationData(
                GPUVertex[] vertices,
                Vector3[] positions,
                uint[] indices,
                bool generateMeshlets,
                GPUVertexSkinningData[]? skinningData = null,
                GiPrimitiveTransportProfile? primitiveTransportProfile = null,
                ModelGiCausticHeroTopologyEvidence causticTopologyEvidence = default)
            {
                Vertices = vertices;
                Positions = positions;
                Indices = indices;
                VertexPositions = BuildVertexPositionStream(vertices);
                VertexNormalTangents = BuildVertexNormalTangentStream(vertices);
                VertexUvColors = BuildVertexUvColorStream(vertices);
                GenerateMeshlets = generateMeshlets;
                SkinningData = skinningData ?? Array.Empty<GPUVertexSkinningData>();
                Meshlets = Array.Empty<Meshlet>();
                LocalVertexIndices = Array.Empty<uint>();
                LocalTriangleIndices = Array.Empty<uint>();
                PrimitiveTransportProfile = ValidateAndCloneTransportProfile(
                    primitiveTransportProfile,
                    indices.Length / 3);
                CausticTopologyEvidence = ValidateCausticTopologyEvidence(
                    Positions,
                    indices,
                    SkinningData.Length > 0,
                    causticTopologyEvidence);
                PrepareRegistrationMetadata();
            }

            public MeshRegistrationData(
                GPUVertex[] vertices,
                uint[] indices,
                Meshlet[] meshlets,
                uint[] localVertexIndices,
                uint[] localTriangleIndices,
                int lod0MeshletCount,
                int lod1MeshletCount,
                int lod2MeshletCount,
                GPUVertexSkinningData[]? skinningData = null,
                GiPrimitiveTransportProfile? primitiveTransportProfile = null,
                ModelGiCausticHeroTopologyEvidence causticTopologyEvidence = default,
                float lod1SimplificationError = -1f,
                float lod2SimplificationError = -1f,
                MeshletHierarchyNode[]? hierarchyNodes = null,
                int hierarchyRootNode = -1,
                uint[]? coarseRayProxyIndices = null)
            {
                Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
                Indices = indices ?? throw new ArgumentNullException(nameof(indices));
                Positions = ExtractPositions(vertices);
                VertexPositions = BuildVertexPositionStream(vertices);
                VertexNormalTangents = BuildVertexNormalTangentStream(vertices);
                VertexUvColors = BuildVertexUvColorStream(vertices);
                Meshlets = meshlets ?? throw new ArgumentNullException(nameof(meshlets));
                LocalVertexIndices = localVertexIndices ?? throw new ArgumentNullException(nameof(localVertexIndices));
                LocalTriangleIndices = localTriangleIndices ?? throw new ArgumentNullException(nameof(localTriangleIndices));
                int flatMeshletCount = checked(
                    lod0MeshletCount + lod1MeshletCount +
                    lod2MeshletCount);
                if (lod0MeshletCount <= 0 || lod1MeshletCount <= 0 || lod2MeshletCount <= 0 ||
                    flatMeshletCount > meshlets.Length)
                    throw new ArgumentException("Cooked meshlet LOD counts must describe three non-empty contiguous ranges at the start of the geometry stream.", nameof(meshlets));
                Lod0MeshletCount = lod0MeshletCount;
                Lod1MeshletCount = lod1MeshletCount;
                Lod2MeshletCount = lod2MeshletCount;
                HierarchyNodes = hierarchyNodes is null
                    ? Array.Empty<MeshletHierarchyNode>()
                    : (MeshletHierarchyNode[])hierarchyNodes.Clone();
                HierarchyRootNode = hierarchyRootNode;
                CoarseRayProxyIndices = coarseRayProxyIndices is null
                    ? Array.Empty<uint>()
                    : (uint[])coarseRayProxyIndices.Clone();
                if (HierarchyNodes.Length == 0 &&
                    flatMeshletCount != meshlets.Length)
                {
                    throw new ArgumentException(
                        "Hierarchy geometry requires hierarchy nodes.",
                        nameof(meshlets));
                }
                Lod1SimplificationError =
                    ValidateSimplificationError(lod1SimplificationError);
                Lod2SimplificationError =
                    ValidateSimplificationError(lod2SimplificationError);
                GenerateMeshlets = false;
                HasPrebuiltMeshlets = true;
                SkinningData = skinningData ?? Array.Empty<GPUVertexSkinningData>();
                PrimitiveTransportProfile = ValidateAndCloneTransportProfile(
                    primitiveTransportProfile,
                    indices.Length / 3);
                CausticTopologyEvidence = ValidateCausticTopologyEvidence(
                    Positions,
                    indices,
                    SkinningData.Length > 0,
                    causticTopologyEvidence);
                PrepareRegistrationMetadata();
                ValidateCookedRegistration();
            }

            public MeshRegistrationData(
                GPUVertexPositionStream[] vertexPositions,
                GPUVertexNormalTangentStream[] vertexNormalTangents,
                GPUVertexUvColorStream[] vertexUvColors,
                uint[] indices,
                Meshlet[] meshlets,
                uint[] localVertexIndices,
                uint[] localTriangleIndices,
                int lod0MeshletCount,
                int lod1MeshletCount,
                int lod2MeshletCount,
                GPUVertexSkinningData[]? skinningData = null,
                GiPrimitiveTransportProfile? primitiveTransportProfile = null,
                ModelGiCausticHeroTopologyEvidence causticTopologyEvidence = default,
                float lod1SimplificationError = -1f,
                float lod2SimplificationError = -1f,
                MeshletHierarchyNode[]? hierarchyNodes = null,
                int hierarchyRootNode = -1,
                uint[]? coarseRayProxyIndices = null)
            {
                VertexPositions = vertexPositions ?? throw new ArgumentNullException(nameof(vertexPositions));
                VertexNormalTangents = vertexNormalTangents ?? throw new ArgumentNullException(nameof(vertexNormalTangents));
                VertexUvColors = vertexUvColors ?? throw new ArgumentNullException(nameof(vertexUvColors));
                if (vertexNormalTangents.Length != vertexPositions.Length || vertexUvColors.Length != vertexPositions.Length)
                    throw new ArgumentException("Cooked split vertex streams must have matching lengths.", nameof(vertexPositions));
                Vertices = Array.Empty<GPUVertex>();
                Positions = ExtractPositions(vertexPositions);
                Indices = indices ?? throw new ArgumentNullException(nameof(indices));
                Meshlets = meshlets ?? throw new ArgumentNullException(nameof(meshlets));
                LocalVertexIndices = localVertexIndices ?? throw new ArgumentNullException(nameof(localVertexIndices));
                LocalTriangleIndices = localTriangleIndices ?? throw new ArgumentNullException(nameof(localTriangleIndices));
                int flatMeshletCount = checked(
                    lod0MeshletCount + lod1MeshletCount +
                    lod2MeshletCount);
                if (lod0MeshletCount <= 0 || lod1MeshletCount <= 0 || lod2MeshletCount <= 0 ||
                    flatMeshletCount > meshlets.Length)
                    throw new ArgumentException("Cooked meshlet LOD counts must describe three non-empty contiguous ranges at the start of the geometry stream.", nameof(meshlets));
                Lod0MeshletCount = lod0MeshletCount;
                Lod1MeshletCount = lod1MeshletCount;
                Lod2MeshletCount = lod2MeshletCount;
                HierarchyNodes = hierarchyNodes is null
                    ? Array.Empty<MeshletHierarchyNode>()
                    : (MeshletHierarchyNode[])hierarchyNodes.Clone();
                HierarchyRootNode = hierarchyRootNode;
                CoarseRayProxyIndices = coarseRayProxyIndices is null
                    ? Array.Empty<uint>()
                    : (uint[])coarseRayProxyIndices.Clone();
                if (HierarchyNodes.Length == 0 &&
                    flatMeshletCount != meshlets.Length)
                {
                    throw new ArgumentException(
                        "Hierarchy geometry requires hierarchy nodes.",
                        nameof(meshlets));
                }
                Lod1SimplificationError =
                    ValidateSimplificationError(lod1SimplificationError);
                Lod2SimplificationError =
                    ValidateSimplificationError(lod2SimplificationError);
                GenerateMeshlets = false;
                HasPrebuiltMeshlets = true;
                SkinningData = skinningData ?? Array.Empty<GPUVertexSkinningData>();
                PrimitiveTransportProfile = ValidateAndCloneTransportProfile(
                    primitiveTransportProfile,
                    indices.Length / 3);
                CausticTopologyEvidence = ValidateCausticTopologyEvidence(
                    Positions,
                    indices,
                    SkinningData.Length > 0,
                    causticTopologyEvidence);
                PrepareRegistrationMetadata();
                ValidateCookedRegistration();
            }

            internal GPUVertex[] Vertices { get; }
            internal GPUVertexPositionStream[] VertexPositions { get; }
            internal GPUVertexNormalTangentStream[] VertexNormalTangents { get; }
            internal GPUVertexUvColorStream[] VertexUvColors { get; }
            internal Vector3[] Positions { get; }
            internal uint[] Indices { get; }
            internal bool GenerateMeshlets { get; }
            internal GPUVertexSkinningData[] SkinningData { get; }
            internal bool IsSkinned => SkinningData.Length > 0;
            internal Meshlet[] Meshlets { get; }
            internal uint[] LocalVertexIndices { get; }
            internal uint[] LocalTriangleIndices { get; }
            internal bool HasPrebuiltMeshlets { get; }
            internal int Lod0MeshletCount { get; }
            internal int Lod1MeshletCount { get; }
            internal int Lod2MeshletCount { get; }
            internal MeshletHierarchyNode[] HierarchyNodes { get; } =
                Array.Empty<MeshletHierarchyNode>();
            internal int HierarchyRootNode { get; } = -1;
            internal uint[] CoarseRayProxyIndices { get; } =
                Array.Empty<uint>();
            internal float Lod1SimplificationError { get; }
            internal float Lod2SimplificationError { get; }
            internal GiPrimitiveTransportProfile? PrimitiveTransportProfile { get; }
            internal ModelGiCausticHeroTopologyEvidence CausticTopologyEvidence { get; }
            internal Vector3 BoundingBoxMin { get; private set; }
            internal Vector3 BoundingBoxMax { get; private set; }
            internal bool HasVertexColor { get; private set; }
            internal bool HasUv1 { get; private set; }
            internal bool HasTangents { get; private set; }
            internal bool CookedValidationCompleted { get; private set; }
            internal uint MeshletTriangleSum { get; private set; }
            internal uint MeshletVertexSum { get; private set; }
            internal uint SmallMeshletsUnder16Triangles { get; private set; }
            internal uint SmallMeshletsUnder32Triangles { get; private set; }
            internal MeshletStreamingSubMeshGpuBinding?
                ManagedResidencyBinding { get; private set; }
            internal uint? RegisteredVertexOffset { get; set; }

            internal void EnableManagedPhysicalResidency(
                MeshletStreamingSubMeshGpuBinding binding)
            {
                ArgumentNullException.ThrowIfNull(binding);
                if (IsSkinned)
                {
                    throw new InvalidOperationException(
                        "Skinned meshes cannot use managed physical residency.");
                }
                if (!HasPrebuiltMeshlets ||
                    binding.Lod0MeshletCount != Lod0MeshletCount ||
                    binding.Lod1MeshletCount != Lod1MeshletCount ||
                    binding.Lod2MeshletCount != Lod2MeshletCount ||
                    binding.HierarchyMeshletCount !=
                        Meshlets.Length -
                        (Lod0MeshletCount + Lod1MeshletCount +
                         Lod2MeshletCount))
                {
                    throw new InvalidOperationException(
                        "The streaming manifest does not match the cooked meshlet ranges.");
                }
                ManagedResidencyBinding = binding;
            }

            private void PrepareRegistrationMetadata()
            {
                if (Positions.Length == 0)
                    return;

                BoundingBoxMin = Positions[0];
                BoundingBoxMax = Positions[0];
                for (int i = 1; i < Positions.Length; i++)
                {
                    BoundingBoxMin = Vector3.Min(
                        BoundingBoxMin,
                        Positions[i]);
                    BoundingBoxMax = Vector3.Max(
                        BoundingBoxMax,
                        Positions[i]);
                }

                const float epsilon = 0.0001f;
                for (int i = 0; i < VertexUvColors.Length; i++)
                {
                    GPUVertexUvColorStream uvColor =
                        VertexUvColors[i];
                    GPUVertexNormalTangentStream normalTangent =
                        VertexNormalTangents[i];
                    HasVertexColor |=
                        Math.Abs(uvColor.Color.X - 1f) > epsilon ||
                        Math.Abs(uvColor.Color.Y - 1f) > epsilon ||
                        Math.Abs(uvColor.Color.Z - 1f) > epsilon ||
                        Math.Abs(uvColor.Color.W - 1f) > epsilon;
                    HasUv1 |=
                        Math.Abs(uvColor.TexCoord2.X) > epsilon ||
                        Math.Abs(uvColor.TexCoord2.Y) > epsilon;
                    HasTangents |=
                        Math.Abs(normalTangent.Tangent.X - 1f) > epsilon ||
                        Math.Abs(normalTangent.Tangent.Y) > epsilon ||
                        Math.Abs(normalTangent.Tangent.Z) > epsilon ||
                        Math.Abs(normalTangent.Tangent.W - 1f) > epsilon;
                }

                for (int i = 0; i < Meshlets.Length; i++)
                {
                    Meshlet meshlet = Meshlets[i];
                    MeshletTriangleSum = CheckedAdd(
                        MeshletTriangleSum,
                        meshlet.LocalTriangleCount);
                    MeshletVertexSum = CheckedAdd(
                        MeshletVertexSum,
                        meshlet.LocalVertexCount);
                    if (meshlet.LocalTriangleCount < 16)
                        SmallMeshletsUnder16Triangles++;
                    if (meshlet.LocalTriangleCount < 32)
                        SmallMeshletsUnder32Triangles++;
                }
            }

            private void ValidateCookedRegistration()
            {
                ValidateMeshInput(Positions, Indices);
                if (SkinningData.Length != 0 &&
                    SkinningData.Length != Positions.Length)
                {
                    throw new ArgumentException(
                        "Cooked skinning data must match the vertex count.",
                        nameof(SkinningData));
                }
                for (int i = 0; i < Meshlets.Length; i++)
                {
                    Meshlet meshlet = Meshlets[i];
                    if (checked((ulong)meshlet.VertexOffset +
                                meshlet.VertexCount) >
                        (ulong)Positions.Length)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet vertex range exceeds its mesh vertex stream.",
                            nameof(Meshlets));
                    }
                    if (checked((ulong)meshlet.IndexOffset +
                                meshlet.IndexCount) >
                        (ulong)Indices.Length)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet index range exceeds its mesh index stream.",
                            nameof(Meshlets));
                    }
                    if (checked((ulong)meshlet.LocalVertexOffset +
                                meshlet.LocalVertexCount) >
                        (ulong)LocalVertexIndices.Length)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet local-vertex range exceeds its stream.",
                            nameof(Meshlets));
                    }
                    if (checked((ulong)meshlet.LocalTriangleOffset +
                                (ulong)meshlet.LocalTriangleCount * 3UL) >
                        (ulong)LocalTriangleIndices.Length)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet local-triangle range exceeds its stream.",
                            nameof(Meshlets));
                    }
                }

                for (int i = 0; i < LocalVertexIndices.Length; i++)
                {
                    if (LocalVertexIndices[i] >= Positions.Length)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet local-vertex index exceeds its mesh vertex stream.",
                            nameof(LocalVertexIndices));
                    }
                }

                for (int i = 0; i < LocalTriangleIndices.Length; i++)
                {
                    if (LocalTriangleIndices[i] >= MaxVerticesPerMeshlet)
                    {
                        throw new ArgumentException(
                            "Cooked meshlet local-triangle index exceeds the meshlet vertex limit.",
                            nameof(LocalTriangleIndices));
                    }
                }

                if (CoarseRayProxyIndices.Length % 3 != 0 ||
                    (IsSkinned && CoarseRayProxyIndices.Length != 0))
                {
                    throw new ArgumentException(
                        "A coarse ray proxy must be a static triangle list.",
                        nameof(CoarseRayProxyIndices));
                }
                for (int i = 0;
                     i < CoarseRayProxyIndices.Length;
                     i++)
                {
                    if (CoarseRayProxyIndices[i] >= Positions.Length)
                    {
                        throw new ArgumentException(
                            "A coarse ray-proxy index exceeds the source vertex stream.",
                            nameof(CoarseRayProxyIndices));
                    }
                }

                ValidateHierarchyRegistration();
                CookedValidationCompleted = true;
            }

            private void ValidateHierarchyRegistration()
            {
                int flatMeshletCount = checked(
                    Lod0MeshletCount + Lod1MeshletCount +
                    Lod2MeshletCount);
                if (HierarchyNodes.Length == 0)
                {
                    if (HierarchyRootNode != -1 ||
                        flatMeshletCount != Meshlets.Length)
                    {
                        throw new ArgumentException(
                            "Cooked hierarchy metadata is incomplete.",
                            nameof(HierarchyNodes));
                    }
                    return;
                }

                if ((uint)HierarchyRootNode >=
                    (uint)HierarchyNodes.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(HierarchyRootNode),
                        "Cooked hierarchy root is outside its node stream.");
                }

                var visited = new bool[HierarchyNodes.Length];
                var lod0Coverage = new byte[Lod0MeshletCount];
                var hierarchyGeometryCoverage = new byte[
                    Meshlets.Length - flatMeshletCount];
                var stack = new Stack<int>();
                stack.Push(HierarchyRootNode);
                while (stack.Count > 0)
                {
                    int nodeIndex = stack.Pop();
                    if (visited[nodeIndex])
                    {
                        throw new ArgumentException(
                            "Cooked hierarchy contains a cycle or a multiply-owned node.",
                            nameof(HierarchyNodes));
                    }
                    visited[nodeIndex] = true;
                    MeshletHierarchyNode node =
                        HierarchyNodes[nodeIndex];
                    if (!float.IsFinite(node.BoundingSphereCenter.X) ||
                        !float.IsFinite(node.BoundingSphereCenter.Y) ||
                        !float.IsFinite(node.BoundingSphereCenter.Z) ||
                        !float.IsFinite(node.BoundingSphereRadius) ||
                        node.BoundingSphereRadius < 0f ||
                        !float.IsFinite(node.GeometricError) ||
                        node.GeometricError < 0f ||
                        node.ChildCount >
                            RendererMeshletLodBuilder.HierarchyFanout ||
                        node.Depth >
                            RendererMeshletLodBuilder.HierarchyMaximumDepth)
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} has invalid bounds, error, fanout, or depth.",
                            nameof(HierarchyNodes));
                    }
                    const MeshletHierarchyNodeFlags knownFlags =
                        MeshletHierarchyNodeFlags.Leaf |
                        MeshletHierarchyNodeFlags.ForceRefine;
                    if ((node.Flags & ~knownFlags) != 0)
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} contains unknown flags.",
                            nameof(HierarchyNodes));
                    }

                    bool leaf = (node.Flags &
                        MeshletHierarchyNodeFlags.Leaf) != 0;
                    bool forceRefine = (node.Flags &
                        MeshletHierarchyNodeFlags.ForceRefine) != 0;
                    if (leaf != (node.ChildCount == 0) ||
                        (leaf && forceRefine) ||
                        (forceRefine && node.MeshletCount != 0) ||
                        (!leaf && !forceRefine &&
                         node.MeshletCount == 0))
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} has inconsistent flags and geometry.",
                            nameof(HierarchyNodes));
                    }

                    ulong meshletEnd = (ulong)node.MeshletOffset +
                        node.MeshletCount;
                    if (meshletEnd > (ulong)Meshlets.Length)
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} has an out-of-range meshlet slice.",
                            nameof(HierarchyNodes));
                    }
                    if (leaf)
                    {
                        if (node.MeshletCount == 0 ||
                            node.MeshletOffset >= Lod0MeshletCount ||
                            meshletEnd > (ulong)Lod0MeshletCount)
                        {
                            throw new ArgumentException(
                                $"Cooked hierarchy leaf {nodeIndex} must reference only LOD0 geometry.",
                                nameof(HierarchyNodes));
                        }
                        MarkUniqueCoverage(
                            lod0Coverage,
                            checked((int)node.MeshletOffset),
                            checked((int)node.MeshletCount),
                            "LOD0",
                            nodeIndex);
                    }
                    else if (node.MeshletCount > 0)
                    {
                        if (node.MeshletOffset < flatMeshletCount)
                        {
                            throw new ArgumentException(
                                $"Cooked hierarchy parent {nodeIndex} aliases a flat LOD range.",
                                nameof(HierarchyNodes));
                        }
                        MarkUniqueCoverage(
                            hierarchyGeometryCoverage,
                            checked((int)node.MeshletOffset) -
                                flatMeshletCount,
                            checked((int)node.MeshletCount),
                            "hierarchy geometry",
                            nodeIndex);
                    }

                    if (node.ChildCount == 0)
                        continue;
                    ulong childEnd = (ulong)node.FirstChild +
                        node.ChildCount;
                    if (childEnd > (ulong)HierarchyNodes.Length)
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} has an out-of-range child slice.",
                            nameof(HierarchyNodes));
                    }
                    for (uint childOffset = 0;
                         childOffset < node.ChildCount;
                         childOffset++)
                    {
                        int childIndex = checked(
                            (int)(node.FirstChild + childOffset));
                        MeshletHierarchyNode child =
                            HierarchyNodes[childIndex];
                        float containmentTolerance = MathF.Max(
                            1e-4f,
                            node.BoundingSphereRadius * 1e-4f);
                        float centerDistance =
                            (node.BoundingSphereCenter -
                             child.BoundingSphereCenter).Length();
                        if (child.ParentIndex != (uint)nodeIndex ||
                            node.Depth != child.Depth + 1u ||
                            node.GeometricError + 1e-6f <
                                child.GeometricError ||
                            centerDistance + child.BoundingSphereRadius >
                                node.BoundingSphereRadius +
                                containmentTolerance)
                        {
                            throw new ArgumentException(
                                $"Cooked hierarchy parent/child contract fails for nodes {nodeIndex} and {childIndex}.",
                                nameof(HierarchyNodes));
                        }
                        stack.Push(childIndex);
                    }
                }

                var descendantLeafMeshlets =
                    new int[HierarchyNodes.Length];
                for (uint depth = 0;
                     depth <=
                         RendererMeshletLodBuilder.HierarchyMaximumDepth;
                     depth++)
                {
                    for (int nodeIndex = 0;
                         nodeIndex < HierarchyNodes.Length;
                         nodeIndex++)
                    {
                        MeshletHierarchyNode node =
                            HierarchyNodes[nodeIndex];
                        if (node.Depth != depth)
                            continue;
                        int leafMeshletCount;
                        if (node.ChildCount == 0)
                        {
                            leafMeshletCount = checked(
                                (int)node.MeshletCount);
                        }
                        else
                        {
                            leafMeshletCount = 0;
                            for (uint child = 0;
                                 child < node.ChildCount;
                                 child++)
                            {
                                leafMeshletCount = checked(
                                    leafMeshletCount +
                                    descendantLeafMeshlets[checked(
                                        (int)(node.FirstChild +
                                              child))]);
                            }
                        }
                        if (node.MeshletCount >
                            (uint)leafMeshletCount)
                        {
                            throw new ArgumentException(
                                $"Cooked hierarchy node {nodeIndex} can emit more meshlets than its descendant LOD0 leaves, violating output-capacity bounds.",
                                nameof(HierarchyNodes));
                        }
                        descendantLeafMeshlets[nodeIndex] =
                            leafMeshletCount;
                    }
                }

                if (HierarchyNodes[HierarchyRootNode].ParentIndex !=
                    uint.MaxValue ||
                    descendantLeafMeshlets[HierarchyRootNode] !=
                        Lod0MeshletCount ||
                    visited.Any(static value => !value) ||
                    lod0Coverage.Any(static value => value != 1) ||
                    hierarchyGeometryCoverage.Any(
                        static value => value != 1))
                {
                    throw new ArgumentException(
                        "Cooked hierarchy is disconnected or does not uniquely cover its geometry.",
                        nameof(HierarchyNodes));
                }
            }

            private static void MarkUniqueCoverage(
                byte[] coverage,
                int offset,
                int count,
                string rangeName,
                int nodeIndex)
            {
                if (offset < 0 || count < 0 ||
                    offset > coverage.Length ||
                    count > coverage.Length - offset)
                {
                    throw new ArgumentException(
                        $"Cooked hierarchy node {nodeIndex} has an out-of-range {rangeName} slice.",
                        nameof(HierarchyNodes));
                }
                for (int index = offset;
                     index < offset + count;
                     index++)
                {
                    if (coverage[index] != 0)
                    {
                        throw new ArgumentException(
                            $"Cooked hierarchy node {nodeIndex} overlaps another {rangeName} slice.",
                            nameof(HierarchyNodes));
                    }
                    coverage[index] = 1;
                }
            }

            internal void PrepareTransportGeometry()
            {
                _ = GetOrCreateTransportGeometry();
            }

            internal MeshTransportGeometry GetOrCreateTransportGeometry()
            {
                if (_preparedTransportGeometry is { } prepared)
                    return prepared;

                MeshTransportGeometry created = CreateTransportGeometry(
                    VertexPositions,
                    VertexUvColors,
                    Indices,
                    IsSkinned,
                    PrimitiveTransportProfile,
                    CausticTopologyEvidence);
                _preparedTransportGeometry = created;
                return created;
            }

            /// <summary>
            /// Conservative staging footprint used by <see cref="RegisterMeshes"/>
            /// for an already-cooked mesh. Cooperative model uploads use this
            /// to stop a render-thread slice before admitting the next mesh.
            /// </summary>
            internal ulong EstimateCookedUploadStagingBytes()
            {
                if (!HasPrebuiltMeshlets)
                {
                    throw new InvalidOperationException(
                        "Only cooked, prebuilt mesh streams have a stable upload estimate.");
                }

                ulong bytes = 0;
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        VertexPositions.Length,
                        VertexPositionStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        VertexNormalTangents.Length,
                        VertexNormalTangentStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        VertexUvColors.Length,
                        VertexUvColorStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(Indices.Length, IndexStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        CoarseRayProxyIndices.Length,
                        IndexStride));
                bytes = AddUploadStagingBytes(bytes, MeshMetadataStride);
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        checked(Meshlets.Length +
                                HierarchyNodes.Length),
                        MeshletStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        LocalVertexIndices.Length,
                        IndexStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        LocalTriangleIndices.Length,
                        IndexStride));
                bytes = AddUploadStagingBytes(
                    bytes,
                    CheckedByteSize(
                        SkinningData.Length,
                        SkinningDataStride));
                return AlignUp(bytes, UploadStagingAlignment);
            }

            private static float ValidateSimplificationError(float value) =>
                float.IsFinite(value) && value >= 0f ? value : -1f;

            private static GiPrimitiveTransportProfile? ValidateAndCloneTransportProfile(
                GiPrimitiveTransportProfile? profile,
                int triangleCount)
            {
                if (profile is null)
                    return null;
                IReadOnlyList<string> errors = profile.Validate();
                if (errors.Count > 0)
                {
                    throw new ArgumentException(
                        $"Primitive transport profile is malformed: {string.Join(" ", errors)}",
                        nameof(profile));
                }
                if (profile.EmissiveSourceTriangleCount != triangleCount)
                {
                    throw new ArgumentException(
                        $"Primitive transport profile describes {profile.EmissiveSourceTriangleCount} source " +
                        $"triangles, but registration contains {triangleCount}.",
                        nameof(profile));
                }
                return profile with
                {
                    TextureSourceHashes = (ulong[])profile.TextureSourceHashes.Clone(),
                    EmissiveTriangles = profile.EmissiveTriangles
                        .Select(static record => record with { })
                        .ToArray(),
                    BaseColorSamplingBinding = profile.BaseColorSamplingBinding with { },
                    EmissiveSamplingBinding = profile.EmissiveSamplingBinding with { },
                    PlanarEvidence = profile.PlanarEvidence with { }
                };
            }

            private static ModelGiCausticHeroTopologyEvidence
                ValidateCausticTopologyEvidence(
                    Vector3[] positions,
                    uint[] indices,
                    bool isSkinned,
                    in ModelGiCausticHeroTopologyEvidence evidence)
            {
                if (evidence == default)
                    return default;
                if (!ModelGiCausticHeroTopologyAnalyzer.Matches(
                        positions,
                        indices,
                        isSkinned,
                        evidence,
                        out string reason))
                {
                    throw new ArgumentException(
                        $"C4 topology evidence does not match the registered mesh ({reason}).",
                        nameof(evidence));
                }
                return evidence;
            }
        }

        public MeshManager(VulkanContext context, BufferManager bufferManager)
            : this(
                context,
                bufferManager,
                stagingRing: null,
                deleter: null,
                synchronizationManager: null,
                allowMissingUploadServices: true)
        {
        }

        public MeshManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            FenceBasedDeleter deleter)
            : this(
                context,
                bufferManager,
                stagingRing,
                deleter,
                synchronizationManager: null,
                allowMissingUploadServices: false)
        {
        }

        public MeshManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            FenceBasedDeleter deleter,
            SynchronizationManager synchronizationManager)
            : this(
                context,
                bufferManager,
                stagingRing,
                deleter,
                synchronizationManager,
                allowMissingUploadServices: false)
        {
        }

        private MeshManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing? stagingRing,
            FenceBasedDeleter? deleter,
            SynchronizationManager? synchronizationManager,
            bool allowMissingUploadServices)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing;
            _deleter = deleter;
            _synchronizationManager = synchronizationManager;

            CreateConsolidatedBuffers();
            System.Diagnostics.Debug.WriteLine("Mesh manager created");
        }

        private void CreateConsolidatedBuffers()
        {
            _vertexPositionBuffer = CreateMeshBuffer(InitialVertexPositionBufferSize, VertexPositionBufferUsage, "Mesh Vertex Position Storage Buffer");
            _vertexNormalTangentBuffer = CreateMeshBuffer(InitialVertexNormalTangentBufferSize, BufferUsageFlags.StorageBufferBit, "Mesh Vertex Normal/Tangent Storage Buffer");
            _vertexUvColorBuffer = CreateMeshBuffer(InitialVertexUvColorBufferSize, BufferUsageFlags.StorageBufferBit, "Mesh Vertex UV/Color Storage Buffer");
            _indexBuffer = CreateMeshBuffer(InitialIndexBufferSize, IndexBufferUsage, "Mesh Index Storage Buffer");
            _meshMetadataBuffer = CreateMeshBuffer(InitialMeshMetadataBufferSize, BufferUsageFlags.StorageBufferBit, "Mesh Metadata Storage Buffer");
            _meshletBuffer = CreateMeshBuffer(InitialMeshletBufferSize, BufferUsageFlags.StorageBufferBit, "Meshlet Storage Buffer");
            _meshletVertexIndexBuffer = CreateMeshBuffer(InitialMeshletVertexIndexBufferSize, BufferUsageFlags.StorageBufferBit, "Meshlet Vertex Index Storage Buffer");
            _meshletTriangleIndexBuffer = CreateMeshBuffer(InitialMeshletTriangleIndexBufferSize, BufferUsageFlags.StorageBufferBit, "Meshlet Triangle Index Storage Buffer");
            _skinningDataBuffer = CreateMeshBuffer(InitialSkinningDataBufferSize, BufferUsageFlags.StorageBufferBit, "Mesh Skinning Data Storage Buffer");
        }

        private BufferHandle CreateMeshBuffer(ulong size, BufferUsageFlags usage, string debugName)
        {
            return _bufferManager.CreateDeviceBuffer(
                size,
                usage | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                true,
                MemoryBudgetCategory.MeshBuffers,
                $"{debugName} ({size} bytes)");
        }

        public MeshHandle RegisterMesh(
            Vector3[] vertices,
            uint[] indices,
            bool generateMeshlets = true)
        {
            ThrowIfDisposed();
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            ValidateMeshInput(vertices, indices);
            GPUVertex[] gpuVertices = BuildGpuVertices(vertices, indices);
            return RegisterMeshInternal(gpuVertices, vertices, indices, generateMeshlets);
        }

        public MeshHandle RegisterMesh(
            GPUVertex[] vertices,
            uint[] indices,
            bool generateMeshlets = true)
        {
            ThrowIfDisposed();
            if (vertices == null)
                throw new ArgumentNullException(nameof(vertices));
            if (indices == null)
                throw new ArgumentNullException(nameof(indices));

            Vector3[] positions = ExtractPositions(vertices);
            ValidateMeshInput(positions, indices);
            return RegisterMeshInternal(vertices, positions, indices, generateMeshlets);
        }

        private MeshHandle RegisterMeshInternal(
            GPUVertex[] gpuVertices,
            Vector3[] positions,
            uint[] indices,
            bool generateMeshlets)
        {
            return RegisterMeshes(new[]
            {
                new MeshRegistrationData(gpuVertices, positions, indices, generateMeshlets)
            })[0];
        }

        public MeshHandle[] RegisterMeshes(IReadOnlyList<MeshRegistrationData> meshes)
        {
            using IModelMeshUpload upload =
                BeginRegistrationUpload(meshes);
            upload.CompleteGpuWork();
            return upload.Handles as MeshHandle[] ??
                   upload.Handles.ToArray();
        }

        internal IModelMeshUpload BeginRegistrationUpload(
            IReadOnlyList<MeshRegistrationData> meshes) =>
            BeginRegistrationUpload(meshes, capacityRegistrations: null);

        internal IModelMeshUpload BeginRegistrationUpload(
            IReadOnlyList<MeshRegistrationData> meshes,
            IReadOnlyList<MeshRegistrationData>? capacityRegistrations)
        {
            ThrowIfDisposed();
            if (meshes == null)
                throw new ArgumentNullException(nameof(meshes));
            if (meshes.Count == 0)
            {
                return new CompletedModelMeshUpload(
                    Array.Empty<MeshHandle>());
            }

            for (int i = 0; i < meshes.Count; i++)
            {
                MeshRegistrationData mesh = meshes[i] ?? throw new ArgumentException("Mesh registration data cannot contain null entries.", nameof(meshes));
                if (!mesh.CookedValidationCompleted)
                {
                    ValidateMeshInput(mesh.Positions, mesh.Indices);
                    if (mesh.VertexPositions.Length != mesh.Positions.Length ||
                        mesh.VertexNormalTangents.Length != mesh.Positions.Length ||
                        mesh.VertexUvColors.Length != mesh.Positions.Length)
                        throw new ArgumentException("Mesh registration vertex streams must have matching lengths.", nameof(meshes));
                    if (mesh.SkinningData.Length != 0 && mesh.SkinningData.Length != mesh.Positions.Length)
                        throw new ArgumentException("Skinned mesh registration data must match the vertex count.", nameof(meshes));
                }
                if (mesh.HasPrebuiltMeshlets && mesh.Meshlets.Length == 0)
                    throw new ArgumentException("Cooked mesh registration requires at least one prebuilt meshlet.", nameof(meshes));
            }

            lock (_lock)
            {
                long registrationStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                ThrowIfDisposedLocked();
                ThrowIfRegistrationUploadActiveLocked();
                long requestedEmissiveBytes = 0;
                for (int i = 0; i < meshes.Count; i++)
                {
                    requestedEmissiveBytes = checked(
                        requestedEmissiveBytes +
                        (long)(meshes[i].PrimitiveTransportProfile?.EmissiveTriangles.Length ?? 0) *
                        GiPrimitiveTransportProfile.EstimatedEmissiveTriangleRecordBytes);
                }
                long finalEmissiveBytes = checked(_runtimeEmissiveTriangleBytes + requestedEmissiveBytes);
                if (finalEmissiveBytes > MaximumRuntimeEmissiveTriangleBytes)
                {
                    throw new InvalidOperationException(
                        $"Registering cooked emissive triangle records requires {finalEmissiveBytes} retained CPU bytes, " +
                        $"exceeding the hard runtime cap {MaximumRuntimeEmissiveTriangleBytes}.");
                }

                var pendingUploads = new List<PendingMeshUpload>(meshes.Count);
                var handles = new MeshHandle[meshes.Count];
                int[] availableFreeMeshIndices =
                    _meshLifetimes.CaptureAvailableFreeIndices();
                MeshUploadCapacityTargets? reservedCapacity =
                    capacityRegistrations == null
                        ? null
                        : CalculateRegistrationCapacityTargets(
                            capacityRegistrations,
                            availableFreeMeshIndices.Length);
                ulong finalVertexPositionBytesUsed = _vertexPositionBytesUsed;
                ulong finalVertexNormalTangentBytesUsed = _vertexNormalTangentBytesUsed;
                ulong finalVertexUvColorBytesUsed = _vertexUvColorBytesUsed;
                ulong finalIndexBytesUsed = _indexBytesUsed;
                ulong finalMeshMetadataBytesUsed = _meshMetadataBytesUsed;
                ulong finalMeshletBytesUsed = _meshletBytesUsed;
                ulong finalMeshletVertexIndexBytesUsed = _meshletVertexIndexBytesUsed;
                ulong finalMeshletTriangleIndexBytesUsed = _meshletTriangleIndexBytesUsed;
                ulong finalSkinningDataBytesUsed = _skinningDataBytesUsed;
                ulong uploadStagingBytes = 0;
                ulong lastPendingMeshBytes = 0;
                if (_meshLifetimes.Count != _meshes.Count)
                {
                    throw new InvalidOperationException(
                        "Mesh slot lifetime state diverged from the authoritative mesh table.");
                }
                int nextAppendMeshIndex = _meshes.Count;

                for (int uploadIndex = 0; uploadIndex < meshes.Count; uploadIndex++)
                {
                    MeshRegistrationData mesh = meshes[uploadIndex];
                    int meshIndex = uploadIndex < availableFreeMeshIndices.Length
                        ? availableFreeMeshIndices[uploadIndex]
                        : nextAppendMeshIndex++;
                    uint generation =
                        _meshLifetimes.GetNextGeneration(meshIndex);

                    var meshInfo = CreateMeshInfo(
                        meshIndex,
                        mesh.Positions.Length,
                        mesh.Indices.Length,
                        mesh.BoundingBoxMin,
                        mesh.BoundingBoxMax,
                        finalVertexPositionBytesUsed,
                        finalIndexBytesUsed,
                        finalMeshletBytesUsed,
                        finalMeshletVertexIndexBytesUsed,
                        finalMeshletTriangleIndexBytesUsed,
                        finalSkinningDataBytesUsed,
                        mesh.SkinningData.Length);
                    mesh.RegisteredVertexOffset = meshInfo.VertexOffset;
                    meshInfo.CausticTopologyEvidence =
                        mesh.CausticTopologyEvidence;
                    meshInfo.HasVertexColor = mesh.HasVertexColor;
                    meshInfo.HasUv1 = mesh.HasUv1;
                    meshInfo.HasTangents = mesh.HasTangents;
                    uint[] gpuIndices = BuildGpuIndexStream(
                        mesh.Indices,
                        mesh.CoarseRayProxyIndices);
                    ConfigureCoarseRayProxy(
                        ref meshInfo,
                        mesh.Indices.Length,
                        mesh.CoarseRayProxyIndices.Length);
                    Meshlet[] meshlets;
                    MeshletHierarchyNode[] hierarchyNodes;
                    int hierarchyRootNode;
                    uint[] localVertexIndices;
                    uint[] localTriangleIndices;

                    if (mesh.HasPrebuiltMeshlets)
                    {
                        meshlets = (Meshlet[])mesh.Meshlets.Clone();
                        hierarchyNodes =
                            (MeshletHierarchyNode[])mesh.HierarchyNodes.Clone();
                        hierarchyRootNode = mesh.HierarchyRootNode;
                        localVertexIndices = mesh.LocalVertexIndices;
                        localTriangleIndices = mesh.LocalTriangleIndices;
                        meshInfo.MeshletCount = CheckedCount(mesh.Lod0MeshletCount);
                        meshInfo.MeshletLod1Offset = CheckedAdd(meshInfo.MeshletOffset, meshInfo.MeshletCount);
                        meshInfo.MeshletLod1Count = CheckedCount(mesh.Lod1MeshletCount);
                        meshInfo.MeshletLod2Offset = CheckedAdd(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count);
                        meshInfo.MeshletLod2Count = CheckedCount(mesh.Lod2MeshletCount);
                        meshInfo.MeshletLod1SimplificationError =
                            mesh.Lod1SimplificationError;
                        meshInfo.MeshletLod2SimplificationError =
                            mesh.Lod2SimplificationError;
                        meshInfo.MeshletLodGeneratedCount = CheckedCount(meshlets.Length);
                        if (mesh.CookedValidationCompleted)
                        {
                            meshInfo.MeshletTriangleSum =
                                mesh.MeshletTriangleSum;
                            meshInfo.MeshletVertexSum =
                                mesh.MeshletVertexSum;
                            meshInfo.SmallMeshletsUnder16Triangles =
                                mesh.SmallMeshletsUnder16Triangles;
                            meshInfo.SmallMeshletsUnder32Triangles =
                                mesh.SmallMeshletsUnder32Triangles;
                        }
                        else
                        {
                            ApplyMeshletQualityStats(
                                ref meshInfo,
                                meshlets);
                        }
                        if (mesh.ManagedResidencyBinding is null)
                        {
                            ApplyGlobalMeshletOffsets(meshlets, meshInfo);
                            if (mesh.CookedValidationCompleted)
                            {
                                meshInfo.LocalVertexIndexCount =
                                    CheckedCount(localVertexIndices.Length);
                                meshInfo.LocalTriangleIndexCount =
                                    CheckedCount(localTriangleIndices.Length);
                            }
                            else
                            {
                                ValidateMeshletRanges(
                                    ref meshInfo,
                                    meshlets,
                                    localVertexIndices,
                                    localTriangleIndices);
                            }
                        }
                        else
                        {
                            ConfigureManagedPhysicalResidency(
                                ref meshInfo,
                                mesh.ManagedResidencyBinding,
                                meshlets.Length);
                        }
                    }
                    else if (mesh.GenerateMeshlets)
                    {
                        var generatedMeshlets = new List<Meshlet>();
                        var generatedLocalVertexIndices = new List<uint>();
                        var generatedLocalTriangleIndices = new List<uint>();
                        BuildMeshletLods(
                            ref meshInfo,
                            mesh.Positions,
                            mesh.Indices,
                            generatedMeshlets,
                            generatedLocalVertexIndices,
                            generatedLocalTriangleIndices,
                            out hierarchyNodes,
                            out hierarchyRootNode);
                        ApplyMeshletQualityStats(
                            ref meshInfo,
                            generatedMeshlets);
                        ApplyGlobalMeshletOffsets(
                            generatedMeshlets,
                            meshInfo);
                        ValidateMeshletRanges(
                            ref meshInfo,
                            generatedMeshlets,
                            generatedLocalVertexIndices,
                            generatedLocalTriangleIndices);
                        meshlets = generatedMeshlets.ToArray();
                        localVertexIndices =
                            generatedLocalVertexIndices.ToArray();
                        localTriangleIndices =
                            generatedLocalTriangleIndices.ToArray();
                    }
                    else
                    {
                        meshlets = Array.Empty<Meshlet>();
                        hierarchyNodes =
                            Array.Empty<MeshletHierarchyNode>();
                        hierarchyRootNode = -1;
                        localVertexIndices = Array.Empty<uint>();
                        localTriangleIndices = Array.Empty<uint>();
                    }
                    bool managedResidency =
                        mesh.ManagedResidencyBinding is not null;
                    if (managedResidency)
                    {
                        ConfigureManagedHierarchyMeshInfo(
                            ref meshInfo,
                            hierarchyNodes,
                            hierarchyRootNode);
                    }
                    else
                    {
                        ConfigureHierarchyMeshInfo(
                            ref meshInfo,
                            meshlets.Length,
                            hierarchyNodes,
                            hierarchyRootNode);
                    }
                    GPUPackedMeshlet[] gpuMeshlets = managedResidency
                        ? PackGpuMeshlets(
                            ReadOnlySpan<Meshlet>.Empty,
                            hierarchyNodes,
                            meshInfo)
                        : PackGpuMeshlets(
                            meshlets,
                            hierarchyNodes,
                            meshInfo);
                    // Managed submeshes omit duplicate GPU geometry records,
                    // but CPU culling, sorting, validation, and debug tooling
                    // still require the immutable authored meshlets.
                    Meshlet[] retainedCpuMeshlets = meshlets;
                    uint[] uploadedLocalVertexIndices = managedResidency
                        ? Array.Empty<uint>()
                        : localVertexIndices;
                    uint[] uploadedLocalTriangleIndices = managedResidency
                        ? Array.Empty<uint>()
                        : localTriangleIndices;

                    var meshMetadata = CreateGpuMeshInfo(meshInfo);
                    if (CheckedElementOffset(finalVertexPositionBytesUsed, VertexPositionStride) != meshInfo.VertexOffset ||
                        CheckedElementOffset(finalVertexNormalTangentBytesUsed, VertexNormalTangentStride) != meshInfo.VertexOffset ||
                        CheckedElementOffset(finalVertexUvColorBytesUsed, VertexUvColorStride) != meshInfo.VertexOffset)
                    {
                        throw new InvalidOperationException("Split vertex stream offsets diverged from the canonical vertex offset.");
                    }

                    GPUVertexPositionStream[] vertexPositions = mesh.VertexPositions;
                    GPUVertexNormalTangentStream[] vertexNormalTangents = mesh.VertexNormalTangents;
                    GPUVertexUvColorStream[] vertexUvColors = mesh.VertexUvColors;
                    ulong vertexPositionBytes = CheckedByteSize(vertexPositions.Length, VertexPositionStride);
                    ulong vertexNormalTangentBytes = CheckedByteSize(vertexNormalTangents.Length, VertexNormalTangentStride);
                    ulong vertexUvColorBytes = CheckedByteSize(vertexUvColors.Length, VertexUvColorStride);
                    ulong indexBytes = CheckedByteSize(
                        gpuIndices.Length,
                        IndexStride);
                    ulong meshletBytes = CheckedByteSize(
                        gpuMeshlets.Length,
                        MeshletStride);
                    ulong localVertexIndexBytes = CheckedByteSize(uploadedLocalVertexIndices.Length, IndexStride);
                    ulong localTriangleIndexBytes = CheckedByteSize(uploadedLocalTriangleIndices.Length, IndexStride);
                    ulong skinningDataBytes = CheckedByteSize(mesh.SkinningData.Length, SkinningDataStride);

                    lastPendingMeshBytes = checked(
                        vertexPositionBytes +
                        vertexNormalTangentBytes +
                        vertexUvColorBytes +
                        indexBytes +
                        meshletBytes +
                        localVertexIndexBytes +
                        localTriangleIndexBytes +
                        skinningDataBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, vertexPositionBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, vertexNormalTangentBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, vertexUvColorBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, indexBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, MeshMetadataStride);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, meshletBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, localVertexIndexBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, localTriangleIndexBytes);
                    uploadStagingBytes = AddUploadStagingBytes(uploadStagingBytes, skinningDataBytes);
                    finalVertexPositionBytesUsed = checked(finalVertexPositionBytesUsed + vertexPositionBytes);
                    finalVertexNormalTangentBytesUsed = checked(finalVertexNormalTangentBytesUsed + vertexNormalTangentBytes);
                    finalVertexUvColorBytesUsed = checked(finalVertexUvColorBytesUsed + vertexUvColorBytes);
                    finalIndexBytesUsed = checked(finalIndexBytesUsed + indexBytes);
                    finalMeshMetadataBytesUsed = Math.Max(finalMeshMetadataBytesUsed, ((ulong)meshIndex + 1) * MeshMetadataStride);
                    finalMeshletBytesUsed = checked(finalMeshletBytesUsed + meshletBytes);
                    finalMeshletVertexIndexBytesUsed = checked(finalMeshletVertexIndexBytesUsed + localVertexIndexBytes);
                    finalMeshletTriangleIndexBytesUsed = checked(finalMeshletTriangleIndexBytesUsed + localTriangleIndexBytes);
                    finalSkinningDataBytesUsed = checked(finalSkinningDataBytesUsed + skinningDataBytes);

                    MeshTransportGeometry transportGeometry =
                        mesh.GetOrCreateTransportGeometry();
                    pendingUploads.Add(new PendingMeshUpload(
                        meshIndex,
                        generation,
                        vertexPositions,
                        vertexNormalTangents,
                        vertexUvColors,
                        gpuIndices,
                        meshInfo,
                        meshMetadata,
                        retainedCpuMeshlets,
                        gpuMeshlets,
                        uploadedLocalVertexIndices,
                        uploadedLocalTriangleIndices,
                        mesh.SkinningData,
                        transportGeometry));
                    handles[uploadIndex] = new MeshHandle(meshIndex, generation);
                }

                long registrationPrepared =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                ulong finalRetainedMeshBytes = checked(
                    finalVertexPositionBytesUsed +
                    finalVertexNormalTangentBytesUsed +
                    finalVertexUvColorBytesUsed +
                    finalIndexBytesUsed +
                    finalMeshletBytesUsed +
                    finalMeshletVertexIndexBytesUsed +
                    finalMeshletTriangleIndexBytesUsed +
                    finalSkinningDataBytesUsed);
                // Everything below the final appended mesh can become an
                // interior hole while that tail remains live. Capping that
                // prefix guarantees adversarial unload/reload order cannot
                // grow retained stream high-water marks without bound.
                ulong maximumPotentialDeadMeshBytes =
                    checked(
                        finalRetainedMeshBytes -
                        lastPendingMeshBytes);
                if (!MeshRetentionBudget.CanRetainBelowTail(
                        maximumPotentialDeadMeshBytes,
                        MaximumRetainedDeadMeshBytes))
                {
                    _retainedDeadMeshBudgetRejectionCount =
                        checked(
                            _retainedDeadMeshBudgetRejectionCount +
                            1);
                    throw new InvalidOperationException(
                        $"Registering meshes would place {maximumPotentialDeadMeshBytes} stream bytes below the newest tail allocation, " +
                        $"exceeding the hard fragmentation cap {MaximumRetainedDeadMeshBytes}. " +
                        "Unload a tail allocation or rebuild the mesh manager before retrying.");
                }

                PrepareMeshStateCommit(pendingUploads, finalMeshletBytesUsed);
                var stateSnapshot =
                    MeshUploadStateSnapshot.Capture(this, pendingUploads);
                RegisteredMeshBufferHandles registeredBufferSnapshot =
                    CaptureRegisteredMeshBufferHandles();
                var reservedFreeMeshIndices = new List<int>(
                    Math.Min(availableFreeMeshIndices.Length, meshes.Count));
                var uploadAttempt = new MeshGpuUploadAttempt(
                    CaptureMeshBufferHandles());
                var committedCapacity = new MeshUploadCapacityTargets(
                    finalVertexPositionBytesUsed,
                    finalVertexNormalTangentBytesUsed,
                    finalVertexUvColorBytesUsed,
                    finalIndexBytesUsed,
                    finalMeshMetadataBytesUsed,
                    finalMeshletBytesUsed,
                    finalMeshletVertexIndexBytesUsed,
                    finalMeshletTriangleIndexBytesUsed,
                    finalSkinningDataBytesUsed);
                MeshUploadCapacityTargets uploadCapacity =
                    reservedCapacity?.AtLeast(committedCapacity) ??
                    committedCapacity;
                long transactionPrepared =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                // Submission is transactional, but publication is deferred
                // until a later render-thread poll observes the fence. This
                // keeps the authoritative buffers and descriptor bindings on
                // the old, renderable state while the candidate buffers are
                // populated asynchronously on the graphics queue.
                MeshUploadTransaction.Execute(
                    completeGpuUpload: () =>
                        CompleteMeshGpuUpload(
                            uploadAttempt,
                            pendingUploads,
                            Math.Max(
                                uploadStagingBytes,
                                UploadStagingAlignment),
                            uploadCapacity),
                    publishCandidateBindings: static () => { },
                    commitAuthoritativeState: static () => { },
                    cleanupGpuUpload: () =>
                        CleanupMeshGpuUpload(uploadAttempt),
                    restoreAuthoritativeState: static () => { },
                    restoreAuthoritativeBindings: static () => { },
                    destroyCandidateResources: () =>
                        DestroyCandidateUploadBuffers(uploadAttempt),
                    quarantineCandidateResources: () =>
                        QuarantineCandidateUploadBuffers(uploadAttempt),
                    restoreReservations: () =>
                        RestoreReservedMeshIndices(
                            reservedFreeMeshIndices));
                long gpuSubmissionCompleted =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                double registrationTotalMilliseconds =
                    System.Diagnostics.Stopwatch
                        .GetElapsedTime(registrationStarted)
                        .TotalMilliseconds;
                if (registrationTotalMilliseconds > 33.0)
                {
                    Console.WriteLine(
                        $"Mesh registration breakdown: " +
                        $"total={registrationTotalMilliseconds:F3}ms, " +
                        $"prepare={System.Diagnostics.Stopwatch.GetElapsedTime(registrationStarted, registrationPrepared).TotalMilliseconds:F3}ms, " +
                        $"transaction={System.Diagnostics.Stopwatch.GetElapsedTime(registrationPrepared, transactionPrepared).TotalMilliseconds:F3}ms, " +
                        $"gpu={System.Diagnostics.Stopwatch.GetElapsedTime(transactionPrepared, gpuSubmissionCompleted).TotalMilliseconds:F3}ms, " +
                        $"meshes={meshes.Count}, " +
                        $"staged={uploadStagingBytes / (1024.0 * 1024.0):F1}MiB.");
                }

                var upload = new MeshRegistrationUpload(
                    this,
                    handles,
                    pendingUploads,
                    stateSnapshot,
                    registeredBufferSnapshot,
                    availableFreeMeshIndices,
                    reservedFreeMeshIndices,
                    uploadAttempt,
                    new MeshUploadCommitState(
                        finalVertexPositionBytesUsed,
                        finalVertexNormalTangentBytesUsed,
                        finalVertexUvColorBytesUsed,
                        finalIndexBytesUsed,
                        finalMeshMetadataBytesUsed,
                        finalMeshletBytesUsed,
                        finalMeshletVertexIndexBytesUsed,
                        finalMeshletTriangleIndexBytesUsed,
                        finalSkinningDataBytesUsed,
                        finalEmissiveBytes));
                _activeRegistrationUpload = upload;
                return upload;
            }
        }

        private bool AdvanceRegistrationUpload(
            MeshRegistrationUpload upload,
            bool cancel,
            bool wait)
        {
            lock (_lock)
            {
                if (upload.Terminal)
                    return true;
                if (!ReferenceEquals(
                        _activeRegistrationUpload,
                        upload))
                {
                    throw new InvalidOperationException(
                        "Mesh registration upload ownership changed before completion.");
                }

                UploadCommandContext commands =
                    upload.UploadAttempt.Upload ??
                    throw new InvalidOperationException(
                        "The active mesh registration has no upload commands.");
                if (wait)
                {
                    CompleteUploadCommands(
                        commands,
                        upload.UploadAttempt.UploadFence);
                }
                else if (!TryCompleteUploadCommands(
                             commands,
                             upload.UploadAttempt.UploadFence))
                {
                    return false;
                }

                upload.UploadAttempt.Upload = null;
                if (cancel)
                    CancelCompletedRegistrationUploadLocked(upload);
                else
                    PublishCompletedRegistrationUploadLocked(upload);
                return true;
            }
        }

        private void PublishCompletedRegistrationUploadLocked(
            MeshRegistrationUpload upload)
        {
            try
            {
                MeshUploadCommitState state = upload.CommitState;
                MeshUploadTransaction.Execute(
                    completeGpuUpload: static () => { },
                    publishCandidateBindings: () =>
                        UpdateRegisteredBindlessBuffers(
                            upload.UploadAttempt.CandidateBuffers),
                    commitAuthoritativeState: () =>
                        CommitMeshUploadState(
                            upload.UploadAttempt.CandidateBuffers,
                            upload.PendingUploads,
                            upload.AvailableFreeMeshIndices,
                            upload.ReservedFreeMeshIndices,
                            state.VertexPositionBytesUsed,
                            state.VertexNormalTangentBytesUsed,
                            state.VertexUvColorBytesUsed,
                            state.IndexBytesUsed,
                            state.MeshMetadataBytesUsed,
                            state.MeshletBytesUsed,
                            state.MeshletVertexIndexBytesUsed,
                            state.MeshletTriangleIndexBytesUsed,
                            state.SkinningDataBytesUsed,
                            state.RuntimeEmissiveTriangleBytes),
                    cleanupGpuUpload: () =>
                        CleanupMeshGpuUpload(upload.UploadAttempt),
                    restoreAuthoritativeState: () =>
                        upload.StateSnapshot.Restore(this),
                    restoreAuthoritativeBindings: () =>
                        RestoreRegisteredBindlessBuffers(
                            upload.RegisteredBufferSnapshot),
                    destroyCandidateResources: () =>
                        DestroyCandidateUploadBuffers(
                            upload.UploadAttempt),
                    quarantineCandidateResources: () =>
                        QuarantineCandidateUploadBuffers(
                            upload.UploadAttempt),
                    restoreReservations: () =>
                        RestoreReservedMeshIndices(
                            upload.ReservedFreeMeshIndices));

                upload.MarkCommitted();
                FinalizeCommittedMeshUpload(upload.UploadAttempt);
            }
            finally
            {
                EndRegistrationUploadLocked(upload);
            }
        }

        private void CancelCompletedRegistrationUploadLocked(
            MeshRegistrationUpload upload)
        {
            List<Exception>? failures = null;
            bool candidatesCanBeDestroyed = true;
            try
            {
                CleanupMeshGpuUpload(upload.UploadAttempt);
            }
            catch (Exception cleanupFailure)
            {
                candidatesCanBeDestroyed = false;
                (failures ??= []).Add(cleanupFailure);
            }

            if (candidatesCanBeDestroyed)
            {
                try
                {
                    DestroyCandidateUploadBuffers(
                        upload.UploadAttempt);
                }
                catch (Exception cleanupFailure)
                {
                    candidatesCanBeDestroyed = false;
                    (failures ??= []).Add(cleanupFailure);
                }
            }

            if (!candidatesCanBeDestroyed)
            {
                try
                {
                    QuarantineCandidateUploadBuffers(
                        upload.UploadAttempt);
                }
                catch (Exception cleanupFailure)
                {
                    (failures ??= []).Add(cleanupFailure);
                }
            }

            try
            {
                RestoreReservedMeshIndices(
                    upload.ReservedFreeMeshIndices);
            }
            catch (Exception cleanupFailure)
            {
                (failures ??= []).Add(cleanupFailure);
            }
            finally
            {
                EndRegistrationUploadLocked(upload);
            }

            if (failures is { Count: > 0 })
            {
                throw new AggregateException(
                    "Failed to cancel an in-flight mesh registration upload.",
                    failures);
            }
        }

        private void EndRegistrationUploadLocked(
            MeshRegistrationUpload upload)
        {
            upload.MarkTerminal();
            if (ReferenceEquals(_activeRegistrationUpload, upload))
                _activeRegistrationUpload = null;
        }

        private MeshInfo CreateMeshInfo(int meshIndex, Vector3[] vertices, uint[] indices)
        {
            Vector3 boundingBoxMin = vertices[0];
            Vector3 boundingBoxMax = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                boundingBoxMin = Vector3.Min(
                    boundingBoxMin,
                    vertices[i]);
                boundingBoxMax = Vector3.Max(
                    boundingBoxMax,
                    vertices[i]);
            }

            return CreateMeshInfo(
                meshIndex,
                vertices.Length,
                indices.Length,
                boundingBoxMin,
                boundingBoxMax,
                _vertexPositionBytesUsed,
                _indexBytesUsed,
                _meshletBytesUsed,
                _meshletVertexIndexBytesUsed,
                _meshletTriangleIndexBytesUsed,
                _skinningDataBytesUsed,
                skinningDataCount: 0);
        }

        private static MeshInfo CreateMeshInfo(
            int meshIndex,
            int vertexCount,
            int indexCount,
            Vector3 boundingBoxMin,
            Vector3 boundingBoxMax,
            ulong vertexPositionBytesUsed,
            ulong indexBytesUsed,
            ulong meshletBytesUsed,
            ulong meshletVertexIndexBytesUsed,
            ulong meshletTriangleIndexBytesUsed,
            ulong skinningDataBytesUsed,
            int skinningDataCount)
        {
            if (vertexPositionBytesUsed % VertexPositionStride != 0 ||
                indexBytesUsed % IndexStride != 0 ||
                meshletBytesUsed % MeshletStride != 0 ||
                meshletVertexIndexBytesUsed % IndexStride != 0 ||
                meshletTriangleIndexBytesUsed % IndexStride != 0 ||
                skinningDataBytesUsed % SkinningDataStride != 0)
            {
                throw new InvalidOperationException("Mesh buffer append offsets are not aligned to their element strides.");
            }

            var meshInfo = new MeshInfo
            {
                VertexOffset = CheckedElementOffset(vertexPositionBytesUsed, VertexPositionStride),
                VertexCount = CheckedCount(vertexCount),
                IndexOffset = CheckedElementOffset(indexBytesUsed, IndexStride),
                IndexCount = CheckedCount(indexCount),
                MeshMetadataOffset = CheckedCount(meshIndex),
                MeshletOffset = CheckedElementOffset(meshletBytesUsed, MeshletStride),
                PhysicalMeshletOffset = CheckedElementOffset(
                    meshletBytesUsed,
                    MeshletStride),
                HierarchyRootNode = uint.MaxValue,
                LocalVertexIndexOffset = CheckedElementOffset(meshletVertexIndexBytesUsed, IndexStride),
                LocalTriangleIndexOffset = CheckedElementOffset(meshletTriangleIndexBytesUsed, IndexStride),
                SkinningDataOffset = CheckedElementOffset(skinningDataBytesUsed, SkinningDataStride),
                SkinningDataCount = CheckedCount(skinningDataCount),
                IsSkinned = skinningDataCount > 0
            };

            meshInfo.BoundingBoxMin = boundingBoxMin;
            meshInfo.BoundingBoxMax = boundingBoxMax;

            return meshInfo;
        }

        private static GPUMeshInfo CreateGpuMeshInfo(MeshInfo meshInfo)
        {
            Vector3 center = (meshInfo.BoundingBoxMin + meshInfo.BoundingBoxMax) * 0.5f;
            float radius = Vector3.Distance(center, meshInfo.BoundingBoxMin);

            return new GPUMeshInfo
            {
                BoundingSphere = new CoreVector4(center.X, center.Y, center.Z, radius),
                SkinningDataOffset = meshInfo.SkinningDataOffset,
                SkinningDataCount = meshInfo.SkinningDataCount,
                Flags = meshInfo.IsSkinned ? 1u : 0u,
                MeshletOffset = meshInfo.MeshletOffset,
                MeshletCount = meshInfo.MeshletCount,
                MeshletLod1Offset = meshInfo.MeshletLod1Offset,
                MeshletLod1Count = meshInfo.MeshletLod1Count,
                MeshletLod2Offset = meshInfo.MeshletLod2Offset,
                MeshletLod2Count = meshInfo.MeshletLod2Count,
                MeshletLodGeneratedCount = meshInfo.MeshletLodGeneratedCount,
                MeshletLod1ErrorBits = unchecked((uint)
                    BitConverter.SingleToInt32Bits(
                        meshInfo.MeshletLod1SimplificationError)),
                MeshletLod2ErrorBits = unchecked((uint)
                    BitConverter.SingleToInt32Bits(
                        meshInfo.MeshletLod2SimplificationError)),
                GpuMeshletRecordCount =
                    meshInfo.EffectiveGpuMeshletRecordCount,
                HierarchyNodeOffset = meshInfo.HierarchyNodeOffset,
                HierarchyNodeCount = meshInfo.HierarchyNodeCount,
                HierarchyRootNode = meshInfo.HierarchyRootNode,
                StreamingRangeIndex = meshInfo.StreamingRangeIndex,
                ResidencyFlags = meshInfo.ResidencyFlags
            };
        }

        public MeshHandle[] RegisterProcessedMeshes(ProcessedMeshAsset asset, bool generateRendererMeshlets = true)
        {
            ThrowIfDisposed();
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            if (asset.SubMeshes.Count == 0)
                return Array.Empty<MeshHandle>();

            var registrations = new MeshRegistrationData[asset.SubMeshes.Count];
            for (int i = 0; i < registrations.Length; i++)
            {
                ProcessedSubMeshAsset subMesh = asset.SubMeshes[i];
                GPUVertex[] vertices = BuildGpuVertices(subMesh);
                GPUVertexSkinningData[] skinningData = BuildGpuSkinningData(subMesh);
                if (generateRendererMeshlets &&
                    subMesh.LodRanges.Count == 3 &&
                    subMesh.Meshlets.Length > 0)
                {
                    ProcessedMeshLodRange lod0 =
                        subMesh.LodRanges.Single(
                            static range => range.Level == 0);
                    ProcessedMeshLodRange lod1 =
                        subMesh.LodRanges.Single(
                            static range => range.Level == 1);
                    ProcessedMeshLodRange lod2 =
                        subMesh.LodRanges.Single(
                            static range => range.Level == 2);
                    registrations[i] = new MeshRegistrationData(
                        vertices,
                        subMesh.Indices,
                        subMesh.Meshlets,
                        subMesh.MeshletVertices,
                        subMesh.MeshletTriangles,
                        lod0.MeshletCount,
                        lod1.MeshletCount,
                        lod2.MeshletCount,
                        skinningData.Length == 0
                            ? null
                            : skinningData,
                        causticTopologyEvidence:
                            subMesh.CausticTopologyEvidence,
                        lod1SimplificationError:
                            lod1.SimplificationError,
                        lod2SimplificationError:
                            lod2.SimplificationError,
                        hierarchyNodes: subMesh.HierarchyNodes,
                        hierarchyRootNode:
                            subMesh.HierarchyRootNode);
                }
                else
                {
                    registrations[i] = new MeshRegistrationData(
                        vertices,
                        subMesh.Indices,
                        generateMeshlets: false,
                        skinningData: skinningData.Length == 0
                            ? null
                            : skinningData,
                        causticTopologyEvidence:
                            subMesh.CausticTopologyEvidence);
                }
            }

            return RegisterMeshes(registrations);
        }

        private static void ApplyVertexAttributeFlags(
            ref MeshInfo meshInfo,
            GPUVertexNormalTangentStream[] normalTangents,
            GPUVertexUvColorStream[] uvColors)
        {
            const float epsilon = 0.0001f;
            bool hasVertexColor = false;
            bool hasUv1 = false;
            bool hasTangents = false;

            for (int i = 0; i < uvColors.Length; i++)
            {
                GPUVertexUvColorStream uvColor = uvColors[i];
                GPUVertexNormalTangentStream normalTangent = normalTangents[i];
                hasVertexColor |=
                    Math.Abs(uvColor.Color.X - 1f) > epsilon ||
                    Math.Abs(uvColor.Color.Y - 1f) > epsilon ||
                    Math.Abs(uvColor.Color.Z - 1f) > epsilon ||
                    Math.Abs(uvColor.Color.W - 1f) > epsilon;
                hasUv1 |=
                    Math.Abs(uvColor.TexCoord2.X) > epsilon ||
                    Math.Abs(uvColor.TexCoord2.Y) > epsilon;
                hasTangents |=
                    Math.Abs(normalTangent.Tangent.X - 1f) > epsilon ||
                    Math.Abs(normalTangent.Tangent.Y) > epsilon ||
                    Math.Abs(normalTangent.Tangent.Z) > epsilon ||
                    Math.Abs(normalTangent.Tangent.W - 1f) > epsilon;
            }

            meshInfo.HasVertexColor = hasVertexColor;
            meshInfo.HasUv1 = hasUv1;
            meshInfo.HasTangents = hasTangents;
        }

        private static GPUVertex[] BuildGpuVertices(Vector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 edge0 = positions[i1] - positions[i0];
                Vector3 edge1 = positions[i2] - positions[i0];
                Vector3 faceNormal = Vector3.Cross(edge0, edge1);
                if (faceNormal.LengthSquared() > 0f)
                    faceNormal = Vector3.Normalize(faceNormal);

                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }

            var vertices = new GPUVertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 normal = normals[i].LengthSquared() > 0f
                    ? Vector3.Normalize(normals[i])
                    : Vector3.UnitZ;

                vertices[i] = new GPUVertex
                {
                    Position = ToCoreVector(positions[i]),
                    Padding0 = 0f,
                    Normal = ToCoreVector(normal),
                    Padding1 = 0f,
                    TexCoord = Njulf.Core.Math.Vector2.Zero,
                    TexCoord2 = Njulf.Core.Math.Vector2.Zero,
                    Tangent = new CoreVector4(1f, 0f, 0f, 1f),
                    Color = GPUVertex.DefaultColor
                };
            }

            return vertices;
        }

        private static GPUVertex[] BuildGpuVertices(ProcessedSubMeshAsset subMesh)
        {
            Vector3[] fallbackNormals = subMesh.Normals.Length == subMesh.Vertices.Length
                ? Array.Empty<Vector3>()
                : ComputeNormals(subMesh.Vertices, subMesh.Indices);

            var vertices = new GPUVertex[subMesh.Vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector3 normal = subMesh.Normals.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Normals[i], new CoreVector3(0f, 0f, 1f))
                    : ToCoreVector(fallbackNormals[i]);

                CoreVector3 tangent = subMesh.Tangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Tangents[i], new CoreVector3(1f, 0f, 0f))
                    : new CoreVector3(1f, 0f, 0f);
                CoreVector3 bitangent = subMesh.Bitangents.Length == subMesh.Vertices.Length
                    ? NormalizeOrDefault(subMesh.Bitangents[i], CoreVector3.Zero)
                    : CoreVector3.Zero;
                float tangentHandedness = CalculateTangentHandedness(normal, tangent, bitangent);

                CoreVector2 texCoord = subMesh.TexCoords.Length == subMesh.Vertices.Length
                    ? subMesh.TexCoords[i]
                    : CoreVector2.Zero;
                CoreVector2 texCoord1 = subMesh.TexCoords1.Length == subMesh.Vertices.Length
                    ? subMesh.TexCoords1[i]
                    : CoreVector2.Zero;
                CoreVector4 color = subMesh.VertexColors.Length == subMesh.Vertices.Length
                    ? subMesh.VertexColors[i]
                    : GPUVertex.DefaultColor;

                vertices[i] = new GPUVertex
                {
                    Position = subMesh.Vertices[i],
                    Padding0 = 0f,
                    Normal = normal,
                    Padding1 = 0f,
                    TexCoord = texCoord,
                    TexCoord2 = texCoord1,
                    Tangent = new CoreVector4(tangent.X, tangent.Y, tangent.Z, tangentHandedness),
                    Color = color
                };
            }

            return vertices;
        }

        private static GPUVertexSkinningData[] BuildGpuSkinningData(ProcessedSubMeshAsset subMesh)
        {
            if (subMesh.SkinIndex < 0)
                return Array.Empty<GPUVertexSkinningData>();
            if (subMesh.JointIndices0.Length != subMesh.Vertices.Length || subMesh.JointWeights0.Length != subMesh.Vertices.Length)
                throw new InvalidOperationException(
                    $"Processed skinned submesh '{subMesh.Name}' must provide JOINTS_0 and WEIGHTS_0 streams for every vertex.");

            var skinningData = new GPUVertexSkinningData[subMesh.Vertices.Length];
            for (int i = 0; i < skinningData.Length; i++)
            {
                VertexJointIndices joints = subMesh.JointIndices0[i];
                VertexJointWeights weights = subMesh.JointWeights0[i].Normalized();
                skinningData[i] = new GPUVertexSkinningData
                {
                    Joint0 = joints.X,
                    Joint1 = joints.Y,
                    Joint2 = joints.Z,
                    Joint3 = joints.W,
                    Weight0 = weights.X,
                    Weight1 = weights.Y,
                    Weight2 = weights.Z,
                    Weight3 = weights.W
                };
            }

            return skinningData;
        }

        private static Vector3[] ComputeNormals(CoreVector3[] positions, uint[] indices)
        {
            var normals = new Vector3[positions.Length];

            for (int i = 0; i < indices.Length; i += 3)
            {
                uint i0 = indices[i + 0];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 p0 = FromCoreVector(positions[i0]);
                Vector3 p1 = FromCoreVector(positions[i1]);
                Vector3 p2 = FromCoreVector(positions[i2]);
                Vector3 faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
                if (faceNormal.LengthSquared() > 0f)
                    faceNormal = Vector3.Normalize(faceNormal);

                normals[i0] += faceNormal;
                normals[i1] += faceNormal;
                normals[i2] += faceNormal;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = normals[i].LengthSquared() > 0f
                    ? Vector3.Normalize(normals[i])
                    : Vector3.UnitZ;
            }

            return normals;
        }

        private static CoreVector3 NormalizeOrDefault(CoreVector3 value, CoreVector3 fallback)
        {
            float lengthSquared = value.X * value.X + value.Y * value.Y + value.Z * value.Z;
            if (lengthSquared <= float.Epsilon)
                return fallback;

            float inverseLength = 1f / MathF.Sqrt(lengthSquared);
            return new CoreVector3(value.X * inverseLength, value.Y * inverseLength, value.Z * inverseLength);
        }

        private static float CalculateTangentHandedness(CoreVector3 normal, CoreVector3 tangent, CoreVector3 bitangent)
        {
            if (bitangent.X * bitangent.X + bitangent.Y * bitangent.Y + bitangent.Z * bitangent.Z <= float.Epsilon)
                return 1f;

            CoreVector3 derivedBitangent = CoreVector3.Cross(normal, tangent);
            float sign = CoreVector3.Dot(derivedBitangent, bitangent);
            return sign < 0f ? -1f : 1f;
        }

        private static GPUVertexPositionStream[] BuildVertexPositionStream(GPUVertex[] vertices)
        {
            var stream = new GPUVertexPositionStream[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var position = vertices[i].Position;
                stream[i] = new GPUVertexPositionStream
                {
                    Position = new CoreVector4(position.X, position.Y, position.Z, 1f)
                };
            }

            return stream;
        }

        private static GPUVertexNormalTangentStream[] BuildVertexNormalTangentStream(GPUVertex[] vertices)
        {
            var stream = new GPUVertexNormalTangentStream[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var normal = vertices[i].Normal;
                stream[i] = new GPUVertexNormalTangentStream
                {
                    Normal = new CoreVector4(normal.X, normal.Y, normal.Z, 0f),
                    Tangent = vertices[i].Tangent
                };
            }

            return stream;
        }

        private static GPUVertexUvColorStream[] BuildVertexUvColorStream(GPUVertex[] vertices)
        {
            var stream = new GPUVertexUvColorStream[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                stream[i] = new GPUVertexUvColorStream
                {
                    TexCoord = vertices[i].TexCoord,
                    TexCoord2 = vertices[i].TexCoord2,
                    Color = vertices[i].Color
                };
            }

            return stream;
        }

        private static Njulf.Core.Math.Vector3 ToCoreVector(Vector3 value)
        {
            return new Njulf.Core.Math.Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3 FromCoreVector(Njulf.Core.Math.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3[] ExtractPositions(GPUVertex[] vertices)
        {
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                positions[i] = FromCoreVector(vertices[i].Position);

            return positions;
        }

        private static Vector3[] ExtractPositions(GPUVertexPositionStream[] vertices)
        {
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                CoreVector4 position = vertices[i].Position;
                positions[i] = new Vector3(position.X, position.Y, position.Z);
            }
            return positions;
        }

        private static void ApplyGlobalMeshletOffsets(
            IList<Meshlet> meshlets,
            MeshInfo meshInfo)
        {
            for (int i = 0; i < meshlets.Count; i++)
            {
                Meshlet meshlet = meshlets[i];
                meshlet.VertexOffset = CheckedAdd(meshInfo.VertexOffset, meshlet.VertexOffset);
                meshlet.IndexOffset = CheckedAdd(meshInfo.IndexOffset, meshlet.IndexOffset);
                meshlet.LocalVertexOffset = CheckedAdd(meshInfo.LocalVertexIndexOffset, meshlet.LocalVertexOffset);
                meshlet.LocalTriangleOffset = CheckedAdd(meshInfo.LocalTriangleIndexOffset, meshlet.LocalTriangleOffset);
                meshlets[i] = meshlet;
            }
        }

        private void ValidateMeshletRanges(
            ref MeshInfo meshInfo,
            IReadOnlyList<Meshlet> meshlets,
            IReadOnlyList<uint> localVertexIndices,
            IReadOnlyList<uint> localTriangleIndices)
        {
            meshInfo.LocalVertexIndexCount = CheckedCount(localVertexIndices.Count);
            meshInfo.LocalTriangleIndexCount = CheckedCount(localTriangleIndices.Count);

            uint vertexEnd = CheckedAdd(meshInfo.VertexOffset, meshInfo.VertexCount);
            uint indexEnd = CheckedAdd(meshInfo.IndexOffset, meshInfo.IndexCount);
            uint localVertexEnd = CheckedAdd(meshInfo.LocalVertexIndexOffset, meshInfo.LocalVertexIndexCount);
            uint localTriangleEnd = CheckedAdd(meshInfo.LocalTriangleIndexOffset, meshInfo.LocalTriangleIndexCount);

            foreach (Meshlet meshlet in meshlets)
            {
                if (meshlet.VertexOffset < meshInfo.VertexOffset ||
                    CheckedAdd(meshlet.VertexOffset, meshlet.VertexCount) > vertexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet vertex range is outside its mesh vertex range.");
                }

                if (meshlet.IndexOffset < meshInfo.IndexOffset ||
                    CheckedAdd(meshlet.IndexOffset, meshlet.IndexCount) > indexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet index range is outside its mesh index range.");
                }

                if (meshlet.LocalVertexOffset < meshInfo.LocalVertexIndexOffset ||
                    CheckedAdd(meshlet.LocalVertexOffset, meshlet.LocalVertexCount) > localVertexEnd)
                {
                    throw new InvalidOperationException("Generated meshlet local vertex range is outside the local vertex index buffer.");
                }

                uint localTriangleScalarCount = meshlet.LocalTriangleCount * 3;
                if (meshlet.LocalTriangleOffset < meshInfo.LocalTriangleIndexOffset ||
                    CheckedAdd(meshlet.LocalTriangleOffset, localTriangleScalarCount) > localTriangleEnd)
                {
                    throw new InvalidOperationException("Generated meshlet local triangle range is outside the local triangle index buffer.");
                }
            }

            for (int i = 0; i < localVertexIndices.Count; i++)
            {
                if (localVertexIndices[i] >= meshInfo.VertexCount)
                    throw new InvalidOperationException($"Meshlet local vertex index {localVertexIndices[i]} is outside mesh vertex count {meshInfo.VertexCount}.");
            }

            for (int i = 0; i < localTriangleIndices.Count; i++)
            {
                if (localTriangleIndices[i] >= MaxVerticesPerMeshlet)
                    throw new InvalidOperationException($"Meshlet local triangle vertex index {localTriangleIndices[i]} exceeds meshlet vertex limit {MaxVerticesPerMeshlet}.");
            }
        }

        private MeshUploadCapacityTargets
            CalculateRegistrationCapacityTargets(
                IReadOnlyList<MeshRegistrationData> registrations,
                int reusableMeshSlotCount)
        {
            ArgumentNullException.ThrowIfNull(registrations);
            if (reusableMeshSlotCount < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(reusableMeshSlotCount));

            ulong vertexPositionBytes = _vertexPositionBytesUsed;
            ulong vertexNormalTangentBytes =
                _vertexNormalTangentBytesUsed;
            ulong vertexUvColorBytes = _vertexUvColorBytesUsed;
            ulong indexBytes = _indexBytesUsed;
            ulong meshletBytes = _meshletBytesUsed;
            ulong meshletVertexIndexBytes =
                _meshletVertexIndexBytesUsed;
            ulong meshletTriangleIndexBytes =
                _meshletTriangleIndexBytesUsed;
            ulong skinningDataBytes = _skinningDataBytesUsed;
            for (int index = 0; index < registrations.Count; index++)
            {
                MeshRegistrationData registration =
                    registrations[index] ?? throw new ArgumentException(
                        "Capacity registrations cannot contain null entries.",
                        nameof(registrations));
                if (!registration.HasPrebuiltMeshlets)
                {
                    throw new ArgumentException(
                        "A mesh upload capacity reservation requires cooked, prebuilt meshlet streams.",
                        nameof(registrations));
                }

                vertexPositionBytes = checked(
                    vertexPositionBytes + CheckedByteSize(
                        registration.VertexPositions.Length,
                        VertexPositionStride));
                vertexNormalTangentBytes = checked(
                    vertexNormalTangentBytes + CheckedByteSize(
                        registration.VertexNormalTangents.Length,
                        VertexNormalTangentStride));
                vertexUvColorBytes = checked(
                    vertexUvColorBytes + CheckedByteSize(
                        registration.VertexUvColors.Length,
                        VertexUvColorStride));
                indexBytes = checked(
                    indexBytes + CheckedByteSize(
                        checked(registration.Indices.Length +
                                registration.CoarseRayProxyIndices.Length),
                        IndexStride));
                meshletBytes = checked(
                    meshletBytes + CheckedByteSize(
                        checked(registration.Meshlets.Length +
                                registration.HierarchyNodes.Length),
                        MeshletStride));
                meshletVertexIndexBytes = checked(
                    meshletVertexIndexBytes + CheckedByteSize(
                        registration.LocalVertexIndices.Length,
                        IndexStride));
                meshletTriangleIndexBytes = checked(
                    meshletTriangleIndexBytes + CheckedByteSize(
                        registration.LocalTriangleIndices.Length,
                        IndexStride));
                skinningDataBytes = checked(
                    skinningDataBytes + CheckedByteSize(
                        registration.SkinningData.Length,
                        SkinningDataStride));
            }

            int appendedMeshSlots = Math.Max(
                0,
                registrations.Count - reusableMeshSlotCount);
            int finalMeshSlotCount = checked(
                _meshes.Count + appendedMeshSlots);
            ulong meshMetadataBytes = Math.Max(
                _meshMetadataBytesUsed,
                checked((ulong)finalMeshSlotCount *
                        MeshMetadataStride));
            return new MeshUploadCapacityTargets(
                vertexPositionBytes,
                vertexNormalTangentBytes,
                vertexUvColorBytes,
                indexBytes,
                meshMetadataBytes,
                meshletBytes,
                meshletVertexIndexBytes,
                meshletTriangleIndexBytes,
                skinningDataBytes);
        }

        private void CompleteMeshGpuUpload(
            MeshGpuUploadAttempt uploadAttempt,
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            ulong uploadStagingBytes,
            MeshUploadCapacityTargets capacity)
        {
            MeshBufferGrowthRetry.Execute(
                executeAttempt: growthMode =>
                    CompleteMeshGpuUploadAttempt(
                        uploadAttempt,
                        pendingUploads,
                        uploadStagingBytes,
                        capacity,
                        growthMode),
                isRetryable: static failure =>
                    failure is MeshBufferGrowthAttemptException,
                resetForRetry: () =>
                    ResetMeshGpuUploadForRetry(uploadAttempt),
                onRetrying: RecordMeshBufferGrowthRetry,
                onRetrySucceeded:
                    RecordMeshBufferGrowthRetrySuccess);
        }

        private void CompleteMeshGpuUploadAttempt(
            MeshGpuUploadAttempt uploadAttempt,
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            ulong uploadStagingBytes,
            MeshUploadCapacityTargets capacity,
            MeshBufferGrowthMode growthMode)
        {
            long uploadStarted =
                System.Diagnostics.Stopwatch.GetTimestamp();
            MeshBufferGrowthPlan growthPlan =
                CreateMeshBufferGrowthPlan(
                    uploadAttempt.OriginalBuffers,
                    capacity.VertexPositionBytes,
                    capacity.VertexNormalTangentBytes,
                    capacity.VertexUvColorBytes,
                    capacity.IndexBytes,
                    capacity.MeshMetadataBytes,
                    capacity.MeshletBytes,
                    capacity.MeshletVertexIndexBytes,
                    capacity.MeshletTriangleIndexBytes,
                    capacity.SkinningDataBytes,
                    growthMode);

            try
            {
                AllocateMeshBufferGrowthPlan(
                    growthPlan,
                    uploadAttempt);
                long buffersAllocated =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                UploadCommandContext upload =
                    BeginUploadCommands(uploadStagingBytes);
                long commandsStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                uploadAttempt.Upload = upload;
                MeshBufferHandles buffers =
                    uploadAttempt.CandidateBuffers;
                RecordMeshBufferReplacementCopies(
                    uploadAttempt.OriginalBuffers,
                    buffers,
                    upload);

                // Cooked batches append each stream contiguously. Pack the
                // small per-mesh arrays into one staging range per stream and
                // issue one vkCmdCopyBuffer call instead of thousands of tiny
                // driver calls. Mesh metadata can reuse free slot indices, so
                // it uses one multi-region command.
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.VertexPositions,
                    static pending => pending.MeshInfo.VertexOffset *
                                      VertexPositionStride,
                    buffers.VertexPosition,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.VertexNormalTangents,
                    static pending => pending.MeshInfo.VertexOffset *
                                      VertexNormalTangentStride,
                    buffers.VertexNormalTangent,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.VertexUvColors,
                    static pending => pending.MeshInfo.VertexOffset *
                                      VertexUvColorStride,
                    buffers.VertexUvColor,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.Indices,
                    static pending => pending.MeshInfo.IndexOffset *
                                      IndexStride,
                    buffers.Index,
                    upload);
                UploadMeshMetadata(
                    pendingUploads,
                    buffers.MeshMetadata,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.GpuMeshlets,
                    static pending => pending.MeshInfo.EffectivePhysicalMeshletOffset *
                                      MeshletStride,
                    buffers.Meshlet,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.LocalVertexIndices,
                    static pending => pending.MeshInfo.LocalVertexIndexOffset *
                                      IndexStride,
                    buffers.MeshletVertexIndex,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.LocalTriangleIndices,
                    static pending => pending.MeshInfo.LocalTriangleIndexOffset *
                                      IndexStride,
                    buffers.MeshletTriangleIndex,
                    upload);
                UploadConcatenatedArrays(
                    pendingUploads,
                    static pending => pending.SkinningData,
                    static pending => pending.MeshInfo.SkinningDataOffset *
                                      SkinningDataStride,
                    buffers.SkinningData,
                    upload);
                long copiesRecorded =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                FlushUploadStaging(upload);
                RecordUploadShaderReadBarriers(upload);
                long stagingFlushed =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                uploadAttempt.UploadFence =
                    SubmitUploadCommands(upload);
                long submitted =
                    System.Diagnostics.Stopwatch.GetTimestamp();
                double totalMilliseconds =
                    System.Diagnostics.Stopwatch
                        .GetElapsedTime(uploadStarted, submitted)
                        .TotalMilliseconds;
                if (totalMilliseconds > 33.0)
                {
                    Console.WriteLine(
                        $"Mesh GPU submission breakdown: " +
                        $"total={totalMilliseconds:F3}ms, " +
                        $"allocate={System.Diagnostics.Stopwatch.GetElapsedTime(uploadStarted, buffersAllocated).TotalMilliseconds:F3}ms, " +
                        $"commands={System.Diagnostics.Stopwatch.GetElapsedTime(buffersAllocated, commandsStarted).TotalMilliseconds:F3}ms, " +
                        $"copies={System.Diagnostics.Stopwatch.GetElapsedTime(commandsStarted, copiesRecorded).TotalMilliseconds:F3}ms, " +
                        $"flush={System.Diagnostics.Stopwatch.GetElapsedTime(copiesRecorded, stagingFlushed).TotalMilliseconds:F3}ms, " +
                        $"submit={System.Diagnostics.Stopwatch.GetElapsedTime(stagingFlushed, submitted).TotalMilliseconds:F3}ms, " +
                        $"staged={uploadStagingBytes / (1024.0 * 1024.0):F1}MiB.");
                }
            }
            catch (BufferAllocationException failure) when (
                failure.Result == Result.ErrorOutOfDeviceMemory)
            {
                throw new MeshBufferGrowthAttemptException(
                    growthPlan,
                    failure);
            }
        }

        private MeshBufferGrowthPlan CreateMeshBufferGrowthPlan(
            MeshBufferHandles buffers,
            ulong finalVertexPositionBytesUsed,
            ulong finalVertexNormalTangentBytesUsed,
            ulong finalVertexUvColorBytesUsed,
            ulong finalIndexBytesUsed,
            ulong finalMeshMetadataBytesUsed,
            ulong finalMeshletBytesUsed,
            ulong finalMeshletVertexIndexBytesUsed,
            ulong finalMeshletTriangleIndexBytesUsed,
            ulong finalSkinningDataBytesUsed,
            MeshBufferGrowthMode growthMode)
        {
            MeshBufferGrowthInput[] inputs =
            {
                new(
                    MeshBufferStream.VertexPosition,
                    _bufferManager.GetBufferSize(
                        buffers.VertexPosition),
                    finalVertexPositionBytesUsed),
                new(
                    MeshBufferStream.VertexNormalTangent,
                    _bufferManager.GetBufferSize(
                        buffers.VertexNormalTangent),
                    finalVertexNormalTangentBytesUsed),
                new(
                    MeshBufferStream.VertexUvColor,
                    _bufferManager.GetBufferSize(
                        buffers.VertexUvColor),
                    finalVertexUvColorBytesUsed),
                new(
                    MeshBufferStream.Index,
                    _bufferManager.GetBufferSize(buffers.Index),
                    finalIndexBytesUsed),
                new(
                    MeshBufferStream.MeshMetadata,
                    _bufferManager.GetBufferSize(
                        buffers.MeshMetadata),
                    finalMeshMetadataBytesUsed),
                new(
                    MeshBufferStream.Meshlet,
                    _bufferManager.GetBufferSize(buffers.Meshlet),
                    finalMeshletBytesUsed),
                new(
                    MeshBufferStream.MeshletVertexIndex,
                    _bufferManager.GetBufferSize(
                        buffers.MeshletVertexIndex),
                    finalMeshletVertexIndexBytesUsed),
                new(
                    MeshBufferStream.MeshletTriangleIndex,
                    _bufferManager.GetBufferSize(
                        buffers.MeshletTriangleIndex),
                    finalMeshletTriangleIndexBytesUsed),
                new(
                    MeshBufferStream.SkinningData,
                    _bufferManager.GetBufferSize(
                        buffers.SkinningData),
                    finalSkinningDataBytesUsed)
            };

            return MeshBufferGrowthPlanner.Create(
                inputs,
                growthMode,
                BufferGrowthFactor);
        }

        private void AllocateMeshBufferGrowthPlan(
            MeshBufferGrowthPlan growthPlan,
            MeshGpuUploadAttempt uploadAttempt)
        {
            MeshBufferHandles buffers = uploadAttempt.OriginalBuffers;
            foreach (MeshBufferGrowthPlanEntry entry in
                     growthPlan.Entries)
            {
                if (!entry.RequiresReplacement)
                    continue;

                BufferHandle original = GetMeshBufferHandle(
                    buffers,
                    entry.Stream);
                BufferHandle candidate =
                    CreateTrackedMeshBufferCandidate(
                        original,
                        entry.TargetSize,
                        entry.Stream,
                        uploadAttempt);
                buffers = SetMeshBufferHandle(
                    buffers,
                    entry.Stream,
                    candidate);
            }

            uploadAttempt.CandidateBuffers = buffers;
        }

        private BufferHandle CreateTrackedMeshBufferCandidate(
            BufferHandle original,
            ulong targetSize,
            MeshBufferStream stream,
            MeshGpuUploadAttempt uploadAttempt)
        {
            BufferHandle candidate = CreateMeshBuffer(
                targetSize,
                GetMeshBufferUsage(stream),
                GetMeshBufferDebugName(stream));
            try
            {
                uploadAttempt.TrackReplacement(
                    original,
                    candidate);
            }
            catch (Exception ownershipFailure)
            {
                try
                {
                    _bufferManager.DestroyBuffer(candidate);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "A mesh-buffer candidate could not be tracked or destroyed.",
                        ownershipFailure,
                        cleanupFailure);
                }

                throw;
            }

            return candidate;
        }

        private void RecordMeshBufferReplacementCopies(
            MeshBufferHandles original,
            MeshBufferHandles candidate,
            UploadCommandContext upload)
        {
            RecordMeshBufferReplacementCopy(
                original.VertexPosition,
                candidate.VertexPosition,
                _vertexPositionBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.VertexNormalTangent,
                candidate.VertexNormalTangent,
                _vertexNormalTangentBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.VertexUvColor,
                candidate.VertexUvColor,
                _vertexUvColorBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.Index,
                candidate.Index,
                _indexBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.MeshMetadata,
                candidate.MeshMetadata,
                _meshMetadataBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.Meshlet,
                candidate.Meshlet,
                _meshletBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.MeshletVertexIndex,
                candidate.MeshletVertexIndex,
                _meshletVertexIndexBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.MeshletTriangleIndex,
                candidate.MeshletTriangleIndex,
                _meshletTriangleIndexBytesUsed,
                upload);
            RecordMeshBufferReplacementCopy(
                original.SkinningData,
                candidate.SkinningData,
                _skinningDataBytesUsed,
                upload);
        }

        private void RecordMeshBufferReplacementCopy(
            BufferHandle original,
            BufferHandle candidate,
            ulong usedBytes,
            UploadCommandContext upload)
        {
            if (original == candidate || usedBytes == 0)
                return;

            var copy = new BufferCopy
            {
                SrcOffset = 0,
                DstOffset = 0,
                Size = usedBytes
            };

            _context.Api.CmdCopyBuffer(
                upload.CommandBuffer,
                _bufferManager.GetBuffer(original),
                _bufferManager.GetBuffer(candidate),
                1,
                &copy);
            upload.TrackWrittenRange(candidate, 0, usedBytes);
        }

        private static BufferHandle GetMeshBufferHandle(
            MeshBufferHandles buffers,
            MeshBufferStream stream) =>
            stream switch
            {
                MeshBufferStream.VertexPosition =>
                    buffers.VertexPosition,
                MeshBufferStream.VertexNormalTangent =>
                    buffers.VertexNormalTangent,
                MeshBufferStream.VertexUvColor =>
                    buffers.VertexUvColor,
                MeshBufferStream.Index => buffers.Index,
                MeshBufferStream.MeshMetadata =>
                    buffers.MeshMetadata,
                MeshBufferStream.Meshlet => buffers.Meshlet,
                MeshBufferStream.MeshletVertexIndex =>
                    buffers.MeshletVertexIndex,
                MeshBufferStream.MeshletTriangleIndex =>
                    buffers.MeshletTriangleIndex,
                MeshBufferStream.SkinningData =>
                    buffers.SkinningData,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stream))
            };

        private static MeshBufferHandles SetMeshBufferHandle(
            MeshBufferHandles buffers,
            MeshBufferStream stream,
            BufferHandle handle) =>
            stream switch
            {
                MeshBufferStream.VertexPosition =>
                    buffers with { VertexPosition = handle },
                MeshBufferStream.VertexNormalTangent =>
                    buffers with { VertexNormalTangent = handle },
                MeshBufferStream.VertexUvColor =>
                    buffers with { VertexUvColor = handle },
                MeshBufferStream.Index =>
                    buffers with { Index = handle },
                MeshBufferStream.MeshMetadata =>
                    buffers with { MeshMetadata = handle },
                MeshBufferStream.Meshlet =>
                    buffers with { Meshlet = handle },
                MeshBufferStream.MeshletVertexIndex =>
                    buffers with { MeshletVertexIndex = handle },
                MeshBufferStream.MeshletTriangleIndex =>
                    buffers with
                    {
                        MeshletTriangleIndex = handle
                    },
                MeshBufferStream.SkinningData =>
                    buffers with { SkinningData = handle },
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stream))
            };

        private static BufferUsageFlags GetMeshBufferUsage(
            MeshBufferStream stream) =>
            stream switch
            {
                MeshBufferStream.VertexPosition =>
                    VertexPositionBufferUsage,
                MeshBufferStream.Index => IndexBufferUsage,
                MeshBufferStream.VertexNormalTangent or
                MeshBufferStream.VertexUvColor or
                MeshBufferStream.MeshMetadata or
                MeshBufferStream.Meshlet or
                MeshBufferStream.MeshletVertexIndex or
                MeshBufferStream.MeshletTriangleIndex or
                MeshBufferStream.SkinningData =>
                    BufferUsageFlags.StorageBufferBit,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stream))
            };

        private static string GetMeshBufferDebugName(
            MeshBufferStream stream) =>
            stream switch
            {
                MeshBufferStream.VertexPosition =>
                    "Mesh Vertex Position Storage Buffer",
                MeshBufferStream.VertexNormalTangent =>
                    "Mesh Vertex Normal/Tangent Storage Buffer",
                MeshBufferStream.VertexUvColor =>
                    "Mesh Vertex UV/Color Storage Buffer",
                MeshBufferStream.Index =>
                    "Mesh Index Storage Buffer",
                MeshBufferStream.MeshMetadata =>
                    "Mesh Metadata Storage Buffer",
                MeshBufferStream.Meshlet =>
                    "Meshlet Storage Buffer",
                MeshBufferStream.MeshletVertexIndex =>
                    "Meshlet Vertex Index Storage Buffer",
                MeshBufferStream.MeshletTriangleIndex =>
                    "Meshlet Triangle Index Storage Buffer",
                MeshBufferStream.SkinningData =>
                    "Mesh Skinning Data Storage Buffer",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(stream))
            };

        private void ResetMeshGpuUploadForRetry(
            MeshGpuUploadAttempt uploadAttempt)
        {
            CleanupMeshGpuUpload(uploadAttempt);
            DestroyCandidateUploadBuffers(uploadAttempt);
            uploadAttempt.ResetForRetry();
        }

        private void RecordMeshBufferGrowthRetry(
            Exception firstFailure)
        {
            IncrementSaturating(ref _meshBufferGrowthRetryCount);
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "Mesh-buffer geometric growth exhausted device memory; " +
                    $"retrying with exact capacities. {firstFailure}");
            }
            catch
            {
                // Recovery diagnostics are best-effort.
            }
        }

        private void RecordMeshBufferGrowthRetrySuccess()
        {
            IncrementSaturating(
                ref _meshBufferGrowthRetrySuccessCount);
        }

        private void RecordMeshBufferCompactionMemorySkip(
            Exception failure)
        {
            IncrementSaturating(
                ref _meshBufferCompactionOutOfDeviceMemorySkipCount);
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "Mesh-buffer compaction was skipped after device-memory exhaustion and complete transactional rollback. " +
                    failure);
            }
            catch
            {
                // Optional-compaction diagnostics are best-effort.
            }
        }

        private static void IncrementSaturating(ref long value)
        {
            if (value < long.MaxValue)
                value++;
        }

        private bool ShouldCompactBuffer(
            BufferHandle buffer,
            ulong usedBytes,
            ulong minimumSize,
            float headroomFactor)
        {
            ulong currentSize = _bufferManager.GetBufferSize(buffer);
            ulong targetSize = CalculateCompactedBufferSize(usedBytes, minimumSize, headroomFactor);
            return targetSize < currentSize;
        }

        private MeshBufferCompactionTarget[]
            CreateMeshBufferCompactionPlan(
                MeshBufferHandles buffers,
                float headroomFactor)
        {
            return
            [
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.VertexPosition,
                    buffers.VertexPosition,
                    _vertexPositionBytesUsed,
                    InitialVertexPositionBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.VertexNormalTangent,
                    buffers.VertexNormalTangent,
                    _vertexNormalTangentBytesUsed,
                    InitialVertexNormalTangentBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.VertexUvColor,
                    buffers.VertexUvColor,
                    _vertexUvColorBytesUsed,
                    InitialVertexUvColorBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.Index,
                    buffers.Index,
                    _indexBytesUsed,
                    InitialIndexBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.MeshMetadata,
                    buffers.MeshMetadata,
                    _meshMetadataBytesUsed,
                    InitialMeshMetadataBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.Meshlet,
                    buffers.Meshlet,
                    _meshletBytesUsed,
                    InitialMeshletBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.MeshletVertexIndex,
                    buffers.MeshletVertexIndex,
                    _meshletVertexIndexBytesUsed,
                    InitialMeshletVertexIndexBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.MeshletTriangleIndex,
                    buffers.MeshletTriangleIndex,
                    _meshletTriangleIndexBytesUsed,
                    InitialMeshletTriangleIndexBufferSize,
                    headroomFactor),
                CreateMeshBufferCompactionTarget(
                    MeshBufferStream.SkinningData,
                    buffers.SkinningData,
                    _skinningDataBytesUsed,
                    InitialSkinningDataBufferSize,
                    headroomFactor)
            ];
        }

        private MeshBufferCompactionTarget
            CreateMeshBufferCompactionTarget(
                MeshBufferStream stream,
                BufferHandle buffer,
                ulong usedBytes,
                ulong minimumSize,
                float headroomFactor)
        {
            ulong currentSize = _bufferManager.GetBufferSize(buffer);
            ulong targetSize = CalculateCompactedBufferSize(
                usedBytes,
                minimumSize,
                headroomFactor);
            return new MeshBufferCompactionTarget(
                stream,
                currentSize,
                targetSize);
        }

        private void AllocateMeshBufferCompactionPlan(
            IReadOnlyList<MeshBufferCompactionTarget> plan,
            MeshGpuUploadAttempt uploadAttempt)
        {
            MeshBufferHandles buffers = uploadAttempt.OriginalBuffers;
            foreach (MeshBufferCompactionTarget target in plan)
            {
                if (target.TargetSize >= target.CurrentSize)
                    continue;

                BufferHandle original = GetMeshBufferHandle(
                    buffers,
                    target.Stream);
                BufferHandle candidate =
                    CreateTrackedMeshBufferCandidate(
                        original,
                        target.TargetSize,
                        target.Stream,
                        uploadAttempt);
                buffers = SetMeshBufferHandle(
                    buffers,
                    target.Stream,
                    candidate);
            }

            uploadAttempt.CandidateBuffers = buffers;
        }

        private void CompleteMeshBufferCompaction(
            MeshGpuUploadAttempt uploadAttempt,
            float headroomFactor)
        {
            MeshBufferCompactionTarget[] plan =
                CreateMeshBufferCompactionPlan(
                    uploadAttempt.OriginalBuffers,
                    headroomFactor);
            AllocateMeshBufferCompactionPlan(plan, uploadAttempt);

            UploadCommandContext upload =
                BeginUploadCommands(UploadStagingAlignment);
            uploadAttempt.Upload = upload;
            MeshBufferHandles buffers = uploadAttempt.CandidateBuffers;
            RecordMeshBufferReplacementCopies(
                uploadAttempt.OriginalBuffers,
                buffers,
                upload);

            RecordUploadShaderReadBarriers(upload);
            uploadAttempt.UploadFence = EndUploadCommands(upload);
        }

        private void UploadSpan<T>(
            ReadOnlySpan<T> data,
            BufferHandle destination,
            ulong destinationOffset,
            UploadCommandContext upload)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return;

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(T));
            (BufferHandle stagingHandle, ulong stagingOffset) = AllocateUploadStaging(upload, dataSize);

            void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
            fixed (T* source = data)
            {
                global::System.Buffer.MemoryCopy(
                    source,
                    (byte*)mappedData + stagingOffset,
                    dataSize,
                    dataSize);
            }

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = destinationOffset,
                Size = dataSize
            };

            _context.Api.CmdCopyBuffer(
                upload.CommandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(destination),
                1,
                &copy);
            upload.TrackWrittenRange(destination, destinationOffset, dataSize);
        }

        private void UploadConcatenatedArrays<T>(
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            Func<PendingMeshUpload, T[]> select,
            Func<PendingMeshUpload, ulong> selectDestinationOffset,
            BufferHandle destination,
            UploadCommandContext upload)
            where T : unmanaged
        {
            UploadConcatenated(
                pendingUploads,
                pending => select(pending).AsSpan(),
                selectDestinationOffset,
                destination,
                upload);
        }

        private void UploadConcatenatedLists<T>(
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            Func<PendingMeshUpload, List<T>> select,
            Func<PendingMeshUpload, ulong> selectDestinationOffset,
            BufferHandle destination,
            UploadCommandContext upload)
            where T : unmanaged
        {
            UploadConcatenated(
                pendingUploads,
                pending => CollectionsMarshal.AsSpan(select(pending)),
                selectDestinationOffset,
                destination,
                upload);
        }

        private delegate ReadOnlySpan<T> PendingSpanSelector<T>(
            PendingMeshUpload pending)
            where T : unmanaged;

        private void UploadConcatenated<T>(
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            PendingSpanSelector<T> select,
            Func<PendingMeshUpload, ulong> selectDestinationOffset,
            BufferHandle destination,
            UploadCommandContext upload)
            where T : unmanaged
        {
            ulong totalSize = 0;
            ulong destinationOffset = 0;
            bool hasData = false;
            for (int i = 0; i < pendingUploads.Count; i++)
            {
                PendingMeshUpload pending = pendingUploads[i];
                ReadOnlySpan<T> data = select(pending);
                if (data.IsEmpty)
                    continue;

                ulong size = checked((ulong)data.Length * (ulong)sizeof(T));
                ulong currentDestination =
                    selectDestinationOffset(pending);
                if (!hasData)
                {
                    destinationOffset = currentDestination;
                    hasData = true;
                }
                else if (currentDestination !=
                         checked(destinationOffset + totalSize))
                {
                    throw new InvalidOperationException(
                        "A cooked mesh stream upload was not contiguous.");
                }

                totalSize = checked(totalSize + size);
            }

            if (!hasData)
                return;

            (BufferHandle stagingHandle, ulong stagingOffset) =
                AllocateUploadStaging(upload, totalSize);
            void* mappedData =
                _bufferManager.GetMappedPointer(stagingHandle);
            ulong written = 0;
            for (int i = 0; i < pendingUploads.Count; i++)
            {
                ReadOnlySpan<T> data = select(pendingUploads[i]);
                if (data.IsEmpty)
                    continue;

                ulong size = checked((ulong)data.Length * (ulong)sizeof(T));
                fixed (T* source = data)
                {
                    global::System.Buffer.MemoryCopy(
                        source,
                        (byte*)mappedData + stagingOffset + written,
                        totalSize - written,
                        size);
                }
                written = checked(written + size);
            }

            var copy = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = destinationOffset,
                Size = totalSize
            };
            _context.Api.CmdCopyBuffer(
                upload.CommandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(destination),
                1,
                &copy);
            upload.TrackWrittenRange(
                destination,
                destinationOffset,
                totalSize);
        }

        private void UploadMeshMetadata(
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            BufferHandle destination,
            UploadCommandContext upload)
        {
            ulong totalSize = checked(
                (ulong)pendingUploads.Count * MeshMetadataStride);
            if (totalSize == 0)
                return;

            (BufferHandle stagingHandle, ulong stagingOffset) =
                AllocateUploadStaging(upload, totalSize);
            void* mappedData =
                _bufferManager.GetMappedPointer(stagingHandle);
            var regions = new BufferCopy[pendingUploads.Count];
            for (int i = 0; i < pendingUploads.Count; i++)
            {
                PendingMeshUpload pending = pendingUploads[i];
                ulong sourceOffset = checked(
                    stagingOffset + (ulong)i * MeshMetadataStride);
                *(GPUMeshInfo*)((byte*)mappedData + sourceOffset) =
                    pending.MeshMetadata;
                ulong destinationOffset = checked(
                    pending.MeshInfo.MeshMetadataOffset *
                    MeshMetadataStride);
                regions[i] = new BufferCopy
                {
                    SrcOffset = sourceOffset,
                    DstOffset = destinationOffset,
                    Size = MeshMetadataStride
                };
                upload.TrackWrittenRange(
                    destination,
                    destinationOffset,
                    MeshMetadataStride);
            }

            fixed (BufferCopy* copies = regions)
            {
                _context.Api.CmdCopyBuffer(
                    upload.CommandBuffer,
                    _bufferManager.GetBuffer(stagingHandle),
                    _bufferManager.GetBuffer(destination),
                    checked((uint)regions.Length),
                    copies);
            }
        }

        private void FlushUploadStaging(UploadCommandContext upload)
        {
            if (upload.StagingOffset == 0)
                return;
            if (!upload.StagingBuffer.IsValid)
            {
                throw new InvalidOperationException(
                    "Mesh upload staging storage is unavailable.");
            }

            // Every UploadSpan writes into one persistently mapped allocation.
            // Flushing each individual stream turns a large cooked model into
            // thousands of allocator/driver calls. The written prefix is
            // contiguous (with alignment padding), so one flush before queue
            // submission provides the same visibility guarantee.
            _bufferManager.FlushBuffer(
                upload.StagingBuffer,
                0,
                upload.StagingOffset);
        }

        private (BufferHandle Buffer, ulong Offset) AllocateUploadStaging(UploadCommandContext upload, ulong size)
        {
            if (!upload.StagingBuffer.IsValid)
                throw new InvalidOperationException("Mesh upload staging buffer has not been created.");

            ulong offset = AlignUp(upload.StagingOffset, UploadStagingAlignment);
            if (offset + size > upload.StagingBufferSize)
            {
                throw new InvalidOperationException(
                    $"Mesh upload staging overflow: trying to allocate {size} bytes at offset {offset}, " +
                    $"buffer size is {upload.StagingBufferSize}.");
            }

            upload.StagingOffset = offset + size;
            return (upload.StagingBuffer, offset);
        }

        private void RecordUploadShaderReadBarriers(UploadCommandContext upload)
        {
            if (upload.WrittenRanges.Count == 0)
                return;

            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[upload.WrittenRanges.Count];
            uint barrierCount = 0;
            foreach (BufferWriteRange range in upload.WrittenRanges)
            {
                if (!range.IsValid)
                    continue;

                barriers[barrierCount++] = CreateUploadReadBarrier(range.Buffer, range.Offset, range.Size);
            }

            if (barrierCount == 0)
                return;

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = barrierCount,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(upload.CommandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateUploadReadBarrier(BufferHandle handle, ulong offset, ulong size)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.TaskShaderBitExt |
                               PipelineStageFlags2.MeshShaderBitExt |
                               PipelineStageFlags2.VertexShaderBit |
                               PipelineStageFlags2.FragmentShaderBit |
                               PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = offset,
                Size = size
            };
        }

        private UploadCommandContext BeginUploadCommands(ulong stagingBytes)
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _context.GraphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit
            };

            Result result = _context.Api.CreateCommandPool(
                _context.Device,
                &poolInfo,
                null,
                out CommandPool commandPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh upload command pool", result);

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            result = _context.Api.AllocateCommandBuffers(
                _context.Device,
                &allocInfo,
                out CommandBuffer commandBuffer);
            if (result != Result.Success)
            {
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw new VulkanException("Failed to allocate mesh upload command buffer", result);
            }

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };

            result = _context.Api.BeginCommandBuffer(commandBuffer, &beginInfo);
            if (result != Result.Success)
            {
                _context.Api.FreeCommandBuffers(_context.Device, commandPool, 1, &commandBuffer);
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw new VulkanException("Failed to begin mesh upload command buffer", result);
            }

            try
            {
                BufferHandle stagingBuffer =
                    GetOrCreateReusableUploadStaging(stagingBytes);
                return new UploadCommandContext(
                    commandPool,
                    commandBuffer,
                    stagingBuffer,
                    _reusableUploadStagingBufferSize,
                    ownsStagingBuffer: false);
            }
            catch
            {
                _context.Api.FreeCommandBuffers(_context.Device, commandPool, 1, &commandBuffer);
                _context.Api.DestroyCommandPool(_context.Device, commandPool, null);
                throw;
            }
        }

        private Fence EndUploadCommands(UploadCommandContext upload)
        {
            Fence fence = SubmitUploadCommands(upload);
            CompleteUploadCommands(upload, fence);
            return fence;
        }

        private Fence SubmitUploadCommands(UploadCommandContext upload)
        {
            Result result = _context.Api.EndCommandBuffer(upload.CommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end mesh upload command buffer", result);

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo
            };

            result = _context.Api.CreateFence(_context.Device, &fenceInfo, null, out Fence fence);
            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh upload fence", result);

            var commandBuffer = upload.CommandBuffer;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer
            };

            result = _context.Api.QueueSubmit(_context.GraphicsQueue, 1, &submitInfo, fence);
            if (result != Result.Success)
            {
                _context.Api.DestroyFence(_context.Device, fence, null);
                throw new VulkanException("Failed to submit mesh upload commands", result);
            }

            upload.MarkSubmitted();
            return fence;
        }

        private bool TryCompleteUploadCommands(
            UploadCommandContext upload,
            Fence fence)
        {
            if (upload.Completed)
                return true;
            if (!upload.Submitted || fence.Handle == 0)
            {
                throw new InvalidOperationException(
                    "Mesh upload commands have not been submitted.");
            }

            Result result = _context.Api.GetFenceStatus(
                _context.Device,
                fence);
            if (result == Result.NotReady)
                return false;
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to query mesh upload fence",
                    result);
            }

            ReleaseCompletedUploadCommands(upload);
            return true;
        }

        private void CompleteUploadCommands(
            UploadCommandContext upload,
            Fence fence)
        {
            if (upload.Completed)
                return;
            if (!upload.Submitted || fence.Handle == 0)
            {
                throw new InvalidOperationException(
                    "Mesh upload commands have not been submitted.");
            }

            Fence fenceToWait = fence;
            Result result = _context.Api.WaitForFences(
                _context.Device,
                1,
                &fenceToWait,
                true,
                ulong.MaxValue);
            if (result != Result.Success)
            {
                throw new VulkanException(
                    "Failed to wait for mesh upload fence",
                    result);
            }

            ReleaseCompletedUploadCommands(upload);
        }

        private void ReleaseCompletedUploadCommands(
            UploadCommandContext upload)
        {
            if (upload.Completed)
                return;

            if (upload.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBufferToFree = upload.CommandBuffer;
                _context.Api.FreeCommandBuffers(
                    _context.Device,
                    upload.CommandPool,
                    1,
                    &commandBufferToFree);
                upload.CommandBuffer = default;
            }
            if (upload.CommandPool.Handle != 0)
            {
                _context.Api.DestroyCommandPool(
                    _context.Device,
                    upload.CommandPool,
                    null);
                upload.CommandPool = default;
            }
            DestroyUploadStaging(upload);
            upload.MarkCompleted();
        }

        private void CleanupUploadCommands(UploadCommandContext upload)
        {
            if (upload.Completed)
                return;
            if (upload.Submitted)
            {
                throw new InvalidOperationException(
                    "Submitted mesh upload commands must complete before their command and staging resources can be released.");
            }

            if (upload.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBufferToFree = upload.CommandBuffer;
                _context.Api.FreeCommandBuffers(_context.Device, upload.CommandPool, 1, &commandBufferToFree);
                upload.CommandBuffer = default;
            }
            if (upload.CommandPool.Handle != 0)
            {
                _context.Api.DestroyCommandPool(_context.Device, upload.CommandPool, null);
                upload.CommandPool = default;
            }
            DestroyUploadStaging(upload);
            upload.MarkCompleted();
        }

        private void DestroyUploadStaging(UploadCommandContext upload)
        {
            if (upload.StagingBuffer.IsValid)
            {
                if (upload.OwnsStagingBuffer)
                    _bufferManager.DestroyBuffer(upload.StagingBuffer);
                upload.StagingBuffer = default;
                upload.StagingBufferSize = 0;
                upload.StagingOffset = 0;
            }
        }

        private BufferHandle GetOrCreateReusableUploadStaging(
            ulong requiredBytes)
        {
            if (requiredBytes == 0)
                throw new ArgumentOutOfRangeException(nameof(requiredBytes));
            if (_reusableUploadStagingBuffer.IsValid &&
                _reusableUploadStagingBufferSize >= requiredBytes)
            {
                return _reusableUploadStagingBuffer;
            }

            ulong targetBytes = AlignUp(
                requiredBytes,
                ReusableUploadStagingGranularity);
            BufferHandle replacement =
                _bufferManager.CreateStagingBuffer(targetBytes);
            BufferHandle previous = _reusableUploadStagingBuffer;
            _reusableUploadStagingBuffer = replacement;
            _reusableUploadStagingBufferSize = targetBytes;
            if (previous.IsValid)
            {
                try
                {
                    _bufferManager.DestroyBuffer(previous);
                }
                catch (Exception retirementFailure)
                {
                    QuarantineRetiredBuffer(previous);
                    RecordPostCommitCleanupFailure(retirementFailure);
                }
            }

            return replacement;
        }

        private void FinalizeCommittedMeshUpload(
            MeshGpuUploadAttempt uploadAttempt)
        {
            CommittedResourceCleanup.Execute(
                retireReplacedResources: () =>
                    RetireReplacedBuffersFailClosed(
                        uploadAttempt.ReplacedBuffers,
                        uploadAttempt.UploadFence),
                releaseCompletionPrimitive: () =>
                {
                    Fence uploadFence =
                        uploadAttempt.UploadFence;
                    uploadAttempt.UploadFence = default;
                    if (uploadFence.Handle == 0)
                        return;

                    try
                    {
                        DestroyUploadFence(uploadFence);
                    }
                    catch
                    {
                        if (!_quarantinedUploadFences.Contains(
                                uploadFence))
                        {
                            _quarantinedUploadFences.Add(
                                uploadFence);
                        }
                        throw;
                    }
                },
                reportFailure:
                    RecordPostCommitCleanupFailure);
        }

        private void RetireReplacedBuffersFailClosed(
            IReadOnlyList<BufferHandle> retiredBuffers,
            Fence uploadFence)
        {
            if (retiredBuffers.Count == 0)
                return;

            if (_deleter == null)
            {
                foreach (BufferHandle buffer in retiredBuffers)
                {
                    try
                    {
                        _bufferManager.DestroyBuffer(buffer);
                    }
                    catch (Exception retirementFailure)
                    {
                        QuarantineRetiredBuffer(buffer);
                        RecordPostCommitCleanupFailure(
                            retirementFailure);
                    }
                }
                return;
            }

            Fence retirementFence = uploadFence;
            bool usesRendererFrameFence = false;
            if (_synchronizationManager != null)
            {
                // Cooperative uploads are submitted during Update. A render
                // submitted later in that same host frame can still consume
                // the old update-after-bind descriptor after the upload fence
                // has signalled. Retire against the most recently submitted
                // renderer frame instead; same-queue ordering then covers all
                // old-descriptor consumers without blocking publication.
                int currentFrame =
                    _synchronizationManager.GetCurrentFrameIndex();
                int previousFrame =
                    (currentFrame + RenderingConstants.FramesInFlight - 1) %
                    RenderingConstants.FramesInFlight;
                retirementFence =
                    _synchronizationManager.GetInFlightFence(
                        previousFrame);
                usesRendererFrameFence = true;
            }

            foreach (BufferHandle buffer in retiredBuffers)
            {
                try
                {
                    _deleter.QueueBufferDeletion(
                        retirementFence,
                        buffer,
                        _bufferManager);
                }
                catch (Exception retirementFailure)
                {
                    QuarantineRetiredBuffer(buffer);
                    RecordPostCommitCleanupFailure(
                        retirementFailure);
                }
            }

            if (usesRendererFrameFence)
                return;

            try
            {
                _deleter.ProcessCompletedFrame(retirementFence);
            }
            catch (Exception retirementFailure)
            {
                // Successfully queued actions remain owned by the deleter and
                // are retried during its shutdown cleanup.
                RecordPostCommitCleanupFailure(
                    retirementFailure);
            }
        }

        private void QuarantineRetiredBuffer(
            BufferHandle buffer)
        {
            if (buffer.IsValid &&
                !_quarantinedUploadBuffers.Contains(buffer))
            {
                _quarantinedUploadBuffers.Add(buffer);
            }
        }

        private void RecordPostCommitCleanupFailure(
            Exception cleanupFailure)
        {
            _postCommitCleanupFailureCount =
                _postCommitCleanupFailureCount == long.MaxValue
                    ? long.MaxValue
                    : _postCommitCleanupFailureCount + 1;
            _lastPostCommitCleanupFailure = cleanupFailure;
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Mesh post-commit cleanup was deferred: {cleanupFailure}");
            }
            catch
            {
                // Diagnostics are best-effort after publication.
            }
        }

        private void DestroyUploadFence(Fence uploadFence)
        {
            if (uploadFence.Handle != 0)
                _context.Api.DestroyFence(_context.Device, uploadFence, null);
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            UpdateRegisteredBindlessBuffers(CaptureMeshBufferHandles());
        }

        private void UpdateRegisteredBindlessBuffers(
            MeshBufferHandles buffers)
        {
            if (_registeredBindlessHeap == null)
                return;

            RegisterStorageBufferIfChanged(
                BindlessIndex.SceneMeshMetadataBuffer,
                buffers.MeshMetadata,
                ref _registeredMeshMetadataBuffer);
            // Keep the legacy bindless slot valid for third-party shaders while all
            // renderer-owned static vertex reads use the split streams below.
            RegisterStorageBufferIfChanged(
                BindlessIndex.VertexBuffer,
                buffers.VertexPosition,
                ref _registeredVertexBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.VertexPositionBuffer,
                buffers.VertexPosition,
                ref _registeredVertexPositionBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.VertexNormalTangentBuffer,
                buffers.VertexNormalTangent,
                ref _registeredVertexNormalTangentBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.VertexUvColorBuffer,
                buffers.VertexUvColor,
                ref _registeredVertexUvColorBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.IndexBuffer,
                buffers.Index,
                ref _registeredIndexBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.MeshletBuffer,
                buffers.Meshlet,
                ref _registeredMeshletBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.MeshletVertexIndexBuffer,
                buffers.MeshletVertexIndex,
                ref _registeredMeshletVertexIndexBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.MeshletTriangleIndexBuffer,
                buffers.MeshletTriangleIndex,
                ref _registeredMeshletTriangleIndexBuffer);
            RegisterStorageBufferIfChanged(
                BindlessIndex.SkinningVertexDataBuffer,
                buffers.SkinningData,
                ref _registeredSkinningDataBuffer);
        }

        private void RegisterStorageBufferIfChanged(int bindlessIndex, BufferHandle handle, ref BufferHandle registeredHandle)
        {
            if (registeredHandle == handle)
                return;

            RegisterStorageBuffer(_registeredBindlessHeap!, bindlessIndex, handle);
            registeredHandle = handle;
        }

        private RegisteredMeshBufferHandles CaptureRegisteredMeshBufferHandles() =>
            new(
                _registeredVertexBuffer,
                _registeredIndexBuffer,
                _registeredMeshMetadataBuffer,
                _registeredMeshletBuffer,
                _registeredMeshletVertexIndexBuffer,
                _registeredMeshletTriangleIndexBuffer,
                _registeredSkinningDataBuffer,
                _registeredVertexPositionBuffer,
                _registeredVertexNormalTangentBuffer,
                _registeredVertexUvColorBuffer);

        private void RestoreRegisteredBindlessBuffers(
            RegisteredMeshBufferHandles snapshot)
        {
            if (_registeredBindlessHeap == null)
            {
                ApplyRegisteredMeshBufferHandles(snapshot);
                return;
            }

            List<Exception>? failures = null;
            RestoreRegisteredStorageBuffer(
                BindlessIndex.SceneMeshMetadataBuffer,
                snapshot.MeshMetadata,
                ref _registeredMeshMetadataBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.VertexBuffer,
                snapshot.Vertex,
                ref _registeredVertexBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.VertexPositionBuffer,
                snapshot.VertexPosition,
                ref _registeredVertexPositionBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.VertexNormalTangentBuffer,
                snapshot.VertexNormalTangent,
                ref _registeredVertexNormalTangentBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.VertexUvColorBuffer,
                snapshot.VertexUvColor,
                ref _registeredVertexUvColorBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.IndexBuffer,
                snapshot.Index,
                ref _registeredIndexBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.MeshletBuffer,
                snapshot.Meshlet,
                ref _registeredMeshletBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.MeshletVertexIndexBuffer,
                snapshot.MeshletVertexIndex,
                ref _registeredMeshletVertexIndexBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.MeshletTriangleIndexBuffer,
                snapshot.MeshletTriangleIndex,
                ref _registeredMeshletTriangleIndexBuffer,
                ref failures);
            RestoreRegisteredStorageBuffer(
                BindlessIndex.SkinningVertexDataBuffer,
                snapshot.SkinningData,
                ref _registeredSkinningDataBuffer,
                ref failures);

            if (failures != null)
            {
                throw new AggregateException(
                    "Failed to restore one or more authoritative mesh-buffer descriptors.",
                    failures);
            }
        }

        private void RestoreRegisteredStorageBuffer(
            int bindlessIndex,
            BufferHandle authoritativeHandle,
            ref BufferHandle registeredHandle,
            ref List<Exception>? failures)
        {
            try
            {
                // Force the write even when managed tracking still says the old
                // handle is registered: a failure can occur after Vulkan accepted
                // a descriptor write but before tracking was updated.
                RegisterStorageBuffer(
                    _registeredBindlessHeap!,
                    bindlessIndex,
                    authoritativeHandle);
                registeredHandle = authoritativeHandle;
            }
            catch (Exception rollbackFailure)
            {
                (failures ??= new List<Exception>()).Add(rollbackFailure);
            }
        }

        private void ApplyRegisteredMeshBufferHandles(
            RegisteredMeshBufferHandles handles)
        {
            _registeredVertexBuffer = handles.Vertex;
            _registeredIndexBuffer = handles.Index;
            _registeredMeshMetadataBuffer = handles.MeshMetadata;
            _registeredMeshletBuffer = handles.Meshlet;
            _registeredMeshletVertexIndexBuffer = handles.MeshletVertexIndex;
            _registeredMeshletTriangleIndexBuffer =
                handles.MeshletTriangleIndex;
            _registeredSkinningDataBuffer = handles.SkinningData;
            _registeredVertexPositionBuffer = handles.VertexPosition;
            _registeredVertexNormalTangentBuffer =
                handles.VertexNormalTangent;
            _registeredVertexUvColorBuffer = handles.VertexUvColor;
        }

        private static MeshTransportGeometry CreateTransportGeometry(
            GPUVertexPositionStream[] vertexPositions,
            GPUVertexUvColorStream[] vertexUvColors,
            uint[] indices,
            bool isSkinned,
            GiPrimitiveTransportProfile? primitiveTransportProfile,
            ModelGiCausticHeroTopologyEvidence causticTopologyEvidence)
        {
            // Registration accepts caller-owned arrays. Retain private copies so
            // subsequent asset/editor mutation cannot invalidate GI sampling.
            return new MeshTransportGeometry(
                (GPUVertexPositionStream[])vertexPositions.Clone(),
                (GPUVertexUvColorStream[])vertexUvColors.Clone(),
                (uint[])indices.Clone(),
                isSkinned,
                primitiveTransportProfile,
                causticTopologyEvidence,
                ComputeLocalSurfaceArea(vertexPositions, indices));
        }

        private MeshBufferHandles CaptureMeshBufferHandles() =>
            new(
                _vertexPositionBuffer,
                _vertexNormalTangentBuffer,
                _vertexUvColorBuffer,
                _indexBuffer,
                _meshMetadataBuffer,
                _meshletBuffer,
                _meshletVertexIndexBuffer,
                _meshletTriangleIndexBuffer,
                _skinningDataBuffer);

        private void ApplyMeshBufferHandles(MeshBufferHandles buffers)
        {
            _vertexPositionBuffer = buffers.VertexPosition;
            _vertexNormalTangentBuffer = buffers.VertexNormalTangent;
            _vertexUvColorBuffer = buffers.VertexUvColor;
            _indexBuffer = buffers.Index;
            _meshMetadataBuffer = buffers.MeshMetadata;
            _meshletBuffer = buffers.Meshlet;
            _meshletVertexIndexBuffer = buffers.MeshletVertexIndex;
            _meshletTriangleIndexBuffer = buffers.MeshletTriangleIndex;
            _skinningDataBuffer = buffers.SkinningData;
        }

        private ulong GetMeshBufferAllocatedBytes(
            MeshBufferHandles buffers)
        {
            return checked(
                _bufferManager.GetBufferSize(buffers.VertexPosition) +
                _bufferManager.GetBufferSize(buffers.VertexNormalTangent) +
                _bufferManager.GetBufferSize(buffers.VertexUvColor) +
                _bufferManager.GetBufferSize(buffers.Index) +
                _bufferManager.GetBufferSize(buffers.MeshMetadata) +
                _bufferManager.GetBufferSize(buffers.Meshlet) +
                _bufferManager.GetBufferSize(
                    buffers.MeshletVertexIndex) +
                _bufferManager.GetBufferSize(
                    buffers.MeshletTriangleIndex) +
                _bufferManager.GetBufferSize(buffers.SkinningData));
        }

        private void CommitMeshBufferCompaction(
            MeshBufferHandles candidateBuffers,
            ulong savedBytes)
        {
            int finalCompactionCount = MeshBufferCompactionCount;
            ulong finalCompactedBytesSaved =
                MeshBufferCompactedBytesSaved;
            if (savedBytes > 0)
            {
                finalCompactionCount =
                    checked(finalCompactionCount + 1);
                finalCompactedBytesSaved = checked(
                    finalCompactedBytesSaved + savedBytes);
            }

            // Calculate every fallible counter transition before making the
            // candidate handles authoritative.
            ApplyMeshBufferHandles(candidateBuffers);
            MeshBufferCompactionCount = finalCompactionCount;
            MeshBufferCompactedBytesSaved =
                finalCompactedBytesSaved;
        }

        private void PrepareMeshStateCommit(
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            ulong finalMeshletBytesUsed)
        {
            if (finalMeshletBytesUsed % MeshletStride != 0)
            {
                throw new InvalidOperationException(
                    "Final meshlet byte count is not aligned to the CPU meshlet cache.");
            }

            ulong finalMeshletCount64 = finalMeshletBytesUsed / MeshletStride;
            if (finalMeshletCount64 > int.MaxValue)
            {
                throw new InvalidOperationException(
                    "CPU meshlet cache exceeded supported element count.");
            }

            int finalMeshletCount = checked((int)finalMeshletCount64);
            if (finalMeshletCount < _meshlets.Count)
            {
                throw new InvalidOperationException(
                    "Append-only mesh registration cannot shrink the CPU meshlet cache.");
            }

            int finalMeshSlotCount = _meshes.Count;
            var uniqueMeshIndices = new HashSet<int>();
            foreach (PendingMeshUpload pending in pendingUploads)
            {
                if (!uniqueMeshIndices.Add(pending.MeshIndex))
                {
                    throw new InvalidOperationException(
                        $"Mesh upload reserved slot {pending.MeshIndex} more than once.");
                }
                finalMeshSlotCount = Math.Max(
                    finalMeshSlotCount,
                    checked(pending.MeshIndex + 1));

                if (pending.MeshInfo.UsesManagedPhysicalResidency)
                {
                    _managedCpuMeshlets.ValidatePrepared(
                        new MeshHandle(
                            pending.MeshIndex,
                            pending.Generation),
                        pending.MeshInfo,
                        pending.Meshlets);
                }

                if (pending.MeshInfo.EffectiveGpuMeshletRecordCount == 0)
                {
                    if (pending.GpuMeshlets.Length != 0)
                    {
                        throw new InvalidOperationException(
                            "A mesh without physical meshlet records produced a GPU meshlet payload.");
                    }
                    continue;
                }

                ulong meshletStart =
                    pending.MeshInfo.EffectivePhysicalMeshletOffset;
                ulong meshletEnd = checked(
                    meshletStart +
                    pending.MeshInfo.EffectiveGpuMeshletRecordCount);
                if (meshletStart < (ulong)_meshlets.Count ||
                    meshletEnd > finalMeshletCount64 ||
                    pending.GpuMeshlets.Length !=
                        pending.MeshInfo.EffectiveGpuMeshletRecordCount)
                {
                    throw new InvalidOperationException(
                        "Pending meshlets overlap authoritative CPU state or exceed the prepared upload range.");
                }
            }

            // Reserve every managed allocation before GPU work begins. The
            // publication step then consists only of bounded writes and count
            // changes that cannot allocate.
            _meshes.EnsureCapacity(finalMeshSlotCount);
            _meshLifetimes.EnsureCapacity(finalMeshSlotCount);
            _transportGeometry.EnsureCapacity(finalMeshSlotCount);
            _meshlets.EnsureCapacity(finalMeshletCount);
            _managedCpuMeshlets.EnsureCapacity(checked(
                _managedCpuMeshlets.Count +
                pendingUploads.Count(static pending =>
                    pending.MeshInfo.UsesManagedPhysicalResidency)));
            _quarantinedUploadBuffers.EnsureCapacity(
                checked(_quarantinedUploadBuffers.Count + 9));
            _quarantinedUploadFences.EnsureCapacity(
                checked(_quarantinedUploadFences.Count + 1));
        }

        private void CommitMeshUploadState(
            MeshBufferHandles candidateBuffers,
            IReadOnlyList<PendingMeshUpload> pendingUploads,
            IReadOnlyList<int> availableFreeMeshIndices,
            ICollection<int> reservedFreeMeshIndices,
            ulong finalVertexPositionBytesUsed,
            ulong finalVertexNormalTangentBytesUsed,
            ulong finalVertexUvColorBytesUsed,
            ulong finalIndexBytesUsed,
            ulong finalMeshMetadataBytesUsed,
            ulong finalMeshletBytesUsed,
            ulong finalMeshletVertexIndexBytesUsed,
            ulong finalMeshletTriangleIndexBytesUsed,
            ulong finalSkinningDataBytesUsed,
            long finalEmissiveBytes)
        {
            _meshLifetimes.ReservePreparedFreeIndices(
                availableFreeMeshIndices,
                pendingUploads.Count,
                reservedFreeMeshIndices);

            ApplyMeshBufferHandles(candidateBuffers);
            _vertexPositionBytesUsed = finalVertexPositionBytesUsed;
            _vertexNormalTangentBytesUsed =
                finalVertexNormalTangentBytesUsed;
            _vertexUvColorBytesUsed = finalVertexUvColorBytesUsed;
            _indexBytesUsed = finalIndexBytesUsed;
            _meshMetadataBytesUsed = finalMeshMetadataBytesUsed;
            _meshletBytesUsed = finalMeshletBytesUsed;
            _meshletVertexIndexBytesUsed =
                finalMeshletVertexIndexBytesUsed;
            _meshletTriangleIndexBytesUsed =
                finalMeshletTriangleIndexBytesUsed;
            _skinningDataBytesUsed = finalSkinningDataBytesUsed;

            foreach (PendingMeshUpload pending in pendingUploads)
            {
                AppendCpuMeshlets(
                    pending.MeshInfo,
                    pending.MeshInfo.UsesManagedPhysicalResidency
                        ? Array.Empty<Meshlet>()
                        : pending.Meshlets);
                CommitMeshSlot(pending);
            }
            _runtimeEmissiveTriangleBytes = finalEmissiveBytes;
            _meshletQualityDiagnosticsDirty = true;
        }

        private void CommitMeshSlot(PendingMeshUpload pending)
        {
            int meshIndex = pending.MeshIndex;
            if (meshIndex > _meshes.Count)
            {
                throw new InvalidOperationException(
                    $"Prepared mesh slot {meshIndex} is not contiguous with the authoritative mesh table.");
            }

            if (meshIndex == _meshes.Count)
                _meshes.Add(pending.MeshInfo);
            else
                _meshes[meshIndex] = pending.MeshInfo;

            _meshLifetimes.CommitSlot(
                meshIndex,
                pending.Generation);

            if (pending.MeshInfo.UsesManagedPhysicalResidency)
            {
                _managedCpuMeshlets.Commit(
                    new MeshHandle(meshIndex, pending.Generation),
                    pending.MeshInfo,
                    pending.Meshlets);
            }

            while (_transportGeometry.Count < meshIndex)
                _transportGeometry.Add(default);
            if (meshIndex == _transportGeometry.Count)
                _transportGeometry.Add(pending.TransportGeometry);
            else
                _transportGeometry[meshIndex] = pending.TransportGeometry;
        }

        private void CleanupMeshGpuUpload(MeshGpuUploadAttempt uploadAttempt)
        {
            List<Exception>? failures = null;
            if (uploadAttempt.Upload != null)
            {
                try
                {
                    CleanupUploadCommands(uploadAttempt.Upload);
                    uploadAttempt.Upload = null;
                }
                catch (Exception cleanupFailure)
                {
                    (failures ??= new List<Exception>()).Add(cleanupFailure);
                }
            }

            if (uploadAttempt.UploadFence.Handle != 0)
            {
                try
                {
                    DestroyUploadFence(uploadAttempt.UploadFence);
                    uploadAttempt.UploadFence = default;
                }
                catch (Exception cleanupFailure)
                {
                    (failures ??= new List<Exception>()).Add(cleanupFailure);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "Failed to clean up mesh-upload command resources.",
                    failures);
            }
        }

        private void DestroyCandidateUploadBuffers(
            MeshGpuUploadAttempt uploadAttempt)
        {
            List<Exception>? failures = null;
            for (int i = uploadAttempt.Replacements.Count - 1; i >= 0; i--)
            {
                BufferHandle candidate =
                    uploadAttempt.Replacements[i].Candidate;
                if (!uploadAttempt.IsCandidateLive(candidate))
                    continue;

                try
                {
                    _bufferManager.DestroyBuffer(candidate);
                    uploadAttempt.MarkCandidateReleased(candidate);
                }
                catch (Exception cleanupFailure)
                {
                    (failures ??= new List<Exception>()).Add(cleanupFailure);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "Failed to destroy one or more uncommitted mesh buffers.",
                    failures);
            }
        }

        private void QuarantineCandidateUploadBuffers(
            MeshGpuUploadAttempt uploadAttempt)
        {
            foreach (MeshBufferReplacement replacement in
                     uploadAttempt.Replacements)
            {
                BufferHandle candidate = replacement.Candidate;
                if (!uploadAttempt.IsCandidateLive(candidate))
                    continue;
                if (!_quarantinedUploadBuffers.Contains(candidate))
                    _quarantinedUploadBuffers.Add(candidate);
                uploadAttempt.MarkCandidateReleased(candidate);
            }
        }

        private void RestoreReservedMeshIndices(
            IList<int> reservedFreeMeshIndices)
        {
            _meshLifetimes.RestoreReservedFreeIndices(
                reservedFreeMeshIndices);
        }

        private static double ComputeLocalSurfaceArea(
            ReadOnlySpan<GPUVertexPositionStream> vertices,
            ReadOnlySpan<uint> indices)
        {
            double area = 0.0;
            for (int index = 0; index < indices.Length; index += 3)
            {
                CoreVector4 p0 = vertices[(int)indices[index]].Position;
                CoreVector4 p1 = vertices[(int)indices[index + 1]].Position;
                CoreVector4 p2 = vertices[(int)indices[index + 2]].Position;
                var e1 = new CoreVector3(p1.X - p0.X, p1.Y - p0.Y, p1.Z - p0.Z);
                var e2 = new CoreVector3(p2.X - p0.X, p2.Y - p0.Y, p2.Z - p0.Z);
                double triangleArea = 0.5 * CoreVector3.Cross(e1, e2).Length();
                if (double.IsFinite(triangleArea))
                    area += triangleArea;
            }
            return area;
        }

        private void AppendCpuMeshlets(MeshInfo meshInfo, IReadOnlyList<Meshlet> meshlets)
        {
            ulong requiredCount =
                (ulong)meshInfo.EffectivePhysicalMeshletOffset +
                meshInfo.EffectiveGpuMeshletRecordCount;
            if (requiredCount > int.MaxValue)
                throw new InvalidOperationException("CPU meshlet cache exceeded supported element count.");

            int requiredListCount = (int)requiredCount;
            if (_meshlets.Count < requiredListCount)
            {
                _meshlets.EnsureCapacity(requiredListCount);
                CollectionsMarshal.SetCount(_meshlets, requiredListCount);
            }

            for (int i = 0; i < meshlets.Count; i++)
                _meshlets[(int)meshInfo.EffectivePhysicalMeshletOffset + i] =
                    meshlets[i];
            int hierarchyStart = checked(
                (int)meshInfo.EffectivePhysicalMeshletOffset +
                meshlets.Count);
            int hierarchyCount = checked(
                (int)meshInfo.EffectiveGpuMeshletRecordCount -
                meshlets.Count);
            if (hierarchyCount > 0)
            {
                CollectionsMarshal.AsSpan(_meshlets)
                    .Slice(hierarchyStart, hierarchyCount)
                    .Clear();
            }
        }

        internal static GPUPackedMeshlet[] PackGpuMeshlets(
            ReadOnlySpan<Meshlet> meshlets)
        {
            if (meshlets.IsEmpty)
                return Array.Empty<GPUPackedMeshlet>();

            var packed = new GPUPackedMeshlet[meshlets.Length];
            for (int i = 0; i < meshlets.Length; i++)
                packed[i] = GPUPackedMeshlet.Pack(meshlets[i]);
            return packed;
        }

        internal static GPUPackedMeshlet[] PackGpuMeshlets(
            ReadOnlySpan<Meshlet> meshlets,
            ReadOnlySpan<MeshletHierarchyNode> hierarchyNodes,
            in MeshInfo meshInfo)
        {
            int recordCount = checked(
                meshlets.Length + hierarchyNodes.Length);
            if (recordCount == 0)
                return Array.Empty<GPUPackedMeshlet>();
            if (meshInfo.EffectiveGpuMeshletRecordCount !=
                (uint)recordCount)
            {
                throw new InvalidOperationException(
                    "GPU meshlet record count does not match geometry and hierarchy payloads.");
            }

            var packed = new GPUPackedMeshlet[recordCount];
            for (int i = 0; i < meshlets.Length; i++)
                packed[i] = GPUPackedMeshlet.Pack(meshlets[i]);
            for (int i = 0; i < hierarchyNodes.Length; i++)
            {
                packed[meshlets.Length + i] =
                    PackGpuHierarchyNode(hierarchyNodes[i], meshInfo);
            }
            return packed;
        }

        private static GPUPackedMeshlet PackGpuHierarchyNode(
            in MeshletHierarchyNode node,
            in MeshInfo meshInfo)
        {
            const uint hierarchyMarker = 1u << 31;
            if (!float.IsFinite(node.BoundingSphereCenter.X) ||
                !float.IsFinite(node.BoundingSphereCenter.Y) ||
                !float.IsFinite(node.BoundingSphereCenter.Z) ||
                !float.IsFinite(node.BoundingSphereRadius) ||
                node.BoundingSphereRadius < 0f ||
                !float.IsFinite(node.GeometricError) ||
                node.GeometricError < 0f ||
                node.ChildCount >
                    RendererMeshletLodBuilder.HierarchyFanout ||
                node.Depth >
                    RendererMeshletLodBuilder.HierarchyMaximumDepth)
            {
                throw new InvalidOperationException(
                    "Cannot pack an invalid meshlet hierarchy node.");
            }

            uint firstChild = node.ChildCount == 0
                ? uint.MaxValue
                : CheckedAdd(
                    meshInfo.HierarchyNodeOffset,
                    node.FirstChild);
            uint meshletOffset = node.MeshletCount == 0
                ? 0u
                : CheckedAdd(
                    meshInfo.MeshletOffset,
                    node.MeshletOffset);
            uint packedMetadata = hierarchyMarker |
                (node.ChildCount & 0x0fu) |
                ((node.Depth & 0x0fu) << 4) |
                (((uint)node.Flags & 0x03u) << 8);
            return new GPUPackedMeshlet
            {
                BoundingSphere = new CoreVector4(
                    node.BoundingSphereCenter.X,
                    node.BoundingSphereCenter.Y,
                    node.BoundingSphereCenter.Z,
                    node.BoundingSphereRadius),
                VertexOffset = unchecked((uint)
                    BitConverter.SingleToInt32Bits(
                        node.GeometricError)),
                LocalVertexOffset = firstChild,
                LocalTriangleOffset = packedMetadata,
                PackedCounts = meshletOffset,
                PackedNormalCone = node.MeshletCount
            };
        }

        private static void ConfigureHierarchyMeshInfo(
            ref MeshInfo meshInfo,
            int geometryMeshletCount,
            IReadOnlyList<MeshletHierarchyNode> hierarchyNodes,
            int hierarchyRootNode)
        {
            uint geometryCount = CheckedCount(geometryMeshletCount);
            meshInfo.MeshletLodGeneratedCount = geometryCount;
            meshInfo.GpuMeshletRecordCount = CheckedAdd(
                geometryCount,
                CheckedCount(hierarchyNodes.Count));
            if (hierarchyNodes.Count == 0)
            {
                if (hierarchyRootNode != -1)
                {
                    throw new InvalidOperationException(
                        "A hierarchy root cannot exist without hierarchy nodes.");
                }
                meshInfo.HierarchyNodeOffset = 0;
                meshInfo.HierarchyNodeCount = 0;
                meshInfo.HierarchyRootNode = uint.MaxValue;
                return;
            }

            if ((uint)hierarchyRootNode >=
                (uint)hierarchyNodes.Count)
            {
                throw new InvalidOperationException(
                    "Meshlet hierarchy root is outside its node stream.");
            }
            meshInfo.HierarchyNodeOffset = CheckedAdd(
                meshInfo.MeshletOffset,
                geometryCount);
            meshInfo.HierarchyNodeCount =
                CheckedCount(hierarchyNodes.Count);
            meshInfo.HierarchyRootNode = CheckedAdd(
                meshInfo.HierarchyNodeOffset,
                CheckedCount(hierarchyRootNode));
        }

        private static void ConfigureManagedPhysicalResidency(
            ref MeshInfo meshInfo,
            MeshletStreamingSubMeshGpuBinding binding,
            int geometryMeshletCount)
        {
            uint expectedGeometryCount = checked(
                binding.Lod0MeshletCount +
                binding.Lod1MeshletCount +
                binding.Lod2MeshletCount +
                binding.HierarchyMeshletCount);
            if (expectedGeometryCount != CheckedCount(geometryMeshletCount))
            {
                throw new InvalidOperationException(
                    "Managed meshlet geometry and virtual mappings diverged.");
            }
            meshInfo.UsesManagedPhysicalResidency = true;
            meshInfo.MeshletOffset = MeshletVirtualAddress.Encode(
                binding.VirtualMeshletBase);
            meshInfo.MeshletCount = binding.Lod0MeshletCount;
            meshInfo.MeshletLod1Offset = MeshletVirtualAddress.Encode(
                checked(binding.VirtualMeshletBase +
                    binding.Lod0MeshletCount));
            meshInfo.MeshletLod1Count = binding.Lod1MeshletCount;
            meshInfo.MeshletLod2Offset = MeshletVirtualAddress.Encode(
                checked(binding.VirtualMeshletBase +
                    binding.Lod0MeshletCount +
                    binding.Lod1MeshletCount));
            meshInfo.MeshletLod2Count = binding.Lod2MeshletCount;
            meshInfo.MeshletLodGeneratedCount = expectedGeometryCount;
            meshInfo.LocalVertexIndexOffset = 0;
            meshInfo.LocalVertexIndexCount = 0;
            meshInfo.LocalTriangleIndexOffset = 0;
            meshInfo.LocalTriangleIndexCount = 0;
            meshInfo.StreamingRangeIndex = binding.Lod0RangeIndex;
            meshInfo.ResidencyFlags =
                GpuMeshResidencyFlags.ManagedPhysicalResidency |
                GpuMeshResidencyFlags.HasPinnedFallback;
            if (binding.HierarchyMeshletCount != 0)
            {
                meshInfo.ResidencyFlags |=
                    GpuMeshResidencyFlags
                        .HasHierarchyVirtualAddresses;
            }
        }

        private static void ConfigureManagedHierarchyMeshInfo(
            ref MeshInfo meshInfo,
            IReadOnlyList<MeshletHierarchyNode> hierarchyNodes,
            int hierarchyRootNode)
        {
            meshInfo.GpuMeshletRecordCount =
                CheckedCount(hierarchyNodes.Count);
            if (hierarchyNodes.Count == 0)
            {
                if (hierarchyRootNode != -1)
                {
                    throw new InvalidOperationException(
                        "A hierarchy root cannot exist without hierarchy nodes.");
                }
                meshInfo.HierarchyNodeOffset = 0;
                meshInfo.HierarchyNodeCount = 0;
                meshInfo.HierarchyRootNode = uint.MaxValue;
                return;
            }
            if ((uint)hierarchyRootNode >=
                (uint)hierarchyNodes.Count)
            {
                throw new InvalidOperationException(
                    "Meshlet hierarchy root is outside its node stream.");
            }
            meshInfo.HierarchyNodeOffset =
                meshInfo.PhysicalMeshletOffset;
            meshInfo.HierarchyNodeCount =
                CheckedCount(hierarchyNodes.Count);
            meshInfo.HierarchyRootNode = CheckedAdd(
                meshInfo.HierarchyNodeOffset,
                CheckedCount(hierarchyRootNode));
        }

        private static uint[] BuildGpuIndexStream(
            uint[] sourceIndices,
            uint[] coarseRayProxyIndices)
        {
            if (coarseRayProxyIndices.Length == 0)
                return sourceIndices;
            var combined = new uint[checked(
                sourceIndices.Length +
                coarseRayProxyIndices.Length)];
            sourceIndices.CopyTo(combined, 0);
            coarseRayProxyIndices.CopyTo(
                combined,
                sourceIndices.Length);
            return combined;
        }

        private static void ConfigureCoarseRayProxy(
            ref MeshInfo meshInfo,
            int sourceIndexCount,
            int coarseRayProxyIndexCount)
        {
            if (meshInfo.IndexCount != CheckedCount(sourceIndexCount))
            {
                throw new InvalidOperationException(
                    "Mesh source index metadata diverged during ray-proxy setup.");
            }
            if (coarseRayProxyIndexCount < 0 ||
                coarseRayProxyIndexCount % 3 != 0 ||
                coarseRayProxyIndexCount > sourceIndexCount)
            {
                throw new InvalidOperationException(
                    "A coarse ray proxy must be a triangle list no larger than its source mesh.");
            }

            meshInfo.GpuIndexCount = CheckedCount(checked(
                sourceIndexCount + coarseRayProxyIndexCount));
            if (coarseRayProxyIndexCount == 0 || meshInfo.IsSkinned)
            {
                meshInfo.CoarseRayProxyIndexOffset = 0;
                meshInfo.CoarseRayProxyIndexCount = 0;
                return;
            }
            meshInfo.CoarseRayProxyIndexOffset = CheckedAdd(
                meshInfo.IndexOffset,
                meshInfo.IndexCount);
            meshInfo.CoarseRayProxyIndexCount =
                CheckedCount(coarseRayProxyIndexCount);
        }

        private static void ValidateMeshInput(Vector3[] vertices, uint[] indices)
        {
            if (vertices.Length == 0)
                throw new ArgumentException("A mesh must contain at least one vertex.", nameof(vertices));
            if (indices.Length == 0)
                throw new ArgumentException("A mesh must contain at least one index.", nameof(indices));
            if (indices.Length % 3 != 0)
                throw new ArgumentException("Mesh index count must be divisible by 3.", nameof(indices));

            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] >= vertices.Length)
                    throw new ArgumentOutOfRangeException(nameof(indices), $"Index {i} references vertex {indices[i]}, but vertex count is {vertices.Length}.");
            }
        }

        private void BuildMeshletLods(
            ref MeshInfo meshInfo,
            Vector3[] vertices,
            uint[] indices,
            List<Meshlet> meshlets,
            List<uint> localVertexIndices,
            List<uint> localTriangleIndices,
            out MeshletHierarchyNode[] hierarchyNodes,
            out int hierarchyRootNode)
        {
            uint baseMeshletOffset = meshInfo.MeshletOffset;
            var coreVertices = new CoreVector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                coreVertices[i] = ToCoreVector(vertices[i]);
            var builder = new RendererMeshletLodBuilder();
            RendererMeshletLodBuild built = builder.Build(coreVertices, indices);
            int localVertexBase = localVertexIndices.Count;
            int localTriangleBase = localTriangleIndices.Count;
            foreach (Meshlet source in built.Meshlets)
            {
                Meshlet value = source;
                value.LocalVertexOffset = CheckedAdd(CheckedCount(localVertexBase), source.LocalVertexOffset);
                value.LocalTriangleOffset = CheckedAdd(CheckedCount(localTriangleBase), source.LocalTriangleOffset);
                meshlets.Add(value);
            }
            localVertexIndices.AddRange(built.MeshletVertices);
            localTriangleIndices.AddRange(built.MeshletTriangles);

            ProcessedMeshLodRange lod0 = built.Ranges[0];
            ProcessedMeshLodRange lod1 = built.Ranges[1];
            ProcessedMeshLodRange lod2 = built.Ranges[2];
            meshInfo.MeshletOffset = baseMeshletOffset;
            meshInfo.MeshletCount = CheckedCount(lod0.MeshletCount);
            meshInfo.MeshletLod1Offset = CheckedAdd(baseMeshletOffset, CheckedCount(lod1.FirstMeshlet));
            meshInfo.MeshletLod1Count = CheckedCount(lod1.MeshletCount);
            meshInfo.MeshletLod2Offset = CheckedAdd(baseMeshletOffset, CheckedCount(lod2.FirstMeshlet));
            meshInfo.MeshletLod2Count = CheckedCount(lod2.MeshletCount);
            meshInfo.MeshletLod1SimplificationError =
                built.SimplificationErrors[1];
            meshInfo.MeshletLod2SimplificationError =
                built.SimplificationErrors[2];
            meshInfo.MeshletLodGeneratedCount = CheckedCount(built.Meshlets.Length);
            hierarchyNodes = built.HierarchyNodes;
            hierarchyRootNode = built.HierarchyRootNode;
        }

        private void GenerateMeshlets(
            Vector3[] vertices,
            uint[] indices,
            int maxVerticesPerMeshlet,
            int maxTrianglesPerMeshlet,
            List<Meshlet> meshlets,
            List<uint> localVertexIndices,
            List<uint> localTriangleIndices)
        {
            var coreVertices = new CoreVector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                coreVertices[i] = ToCoreVector(vertices[i]);
            var sharedBuilder = new Njulf.Assets.MeshletBuilder(maxVerticesPerMeshlet, maxTrianglesPerMeshlet);
            MeshletMesh built = sharedBuilder.BuildMeshlets(coreVertices, indices);
            int localVertexBase = localVertexIndices.Count;
            int localTriangleBase = localTriangleIndices.Count;
            foreach (Meshlet source in built.Meshlets)
            {
                Meshlet meshlet = source;
                meshlet.VertexOffset = 0;
                meshlet.IndexOffset = 0;
                meshlet.IndexCount = CheckedCount(indices.Length);
                meshlet.LocalVertexOffset = CheckedAdd(CheckedCount(localVertexBase), source.LocalVertexOffset);
                meshlet.LocalTriangleOffset = CheckedAdd(CheckedCount(localTriangleBase), checked(source.LocalTriangleOffset * 3));
                meshlets.Add(meshlet);
            }
            localVertexIndices.AddRange(built.MeshletVertices);
            localTriangleIndices.AddRange(built.MeshletTriangles);
        }

        private static List<int>[] BuildVertexTriangleAdjacency(uint[] indices, int vertexCount)
        {
            var vertexToTriangles = new List<int>[vertexCount];
            int triangleCount = indices.Length / 3;

            for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
            {
                int i0 = (int)indices[triangleIndex * 3 + 0];
                int i1 = (int)indices[triangleIndex * 3 + 1];
                int i2 = (int)indices[triangleIndex * 3 + 2];

                AddTriangleReference(vertexToTriangles, i0, triangleIndex);
                if (i1 != i0)
                    AddTriangleReference(vertexToTriangles, i1, triangleIndex);
                if (i2 != i0 && i2 != i1)
                    AddTriangleReference(vertexToTriangles, i2, triangleIndex);
            }

            return vertexToTriangles;
        }

        private static void AddTriangleReference(List<int>[] vertexToTriangles, int vertexIndex, int triangleIndex)
        {
            List<int>? triangles = vertexToTriangles[vertexIndex];
            if (triangles == null)
            {
                triangles = new List<int>();
                vertexToTriangles[vertexIndex] = triangles;
            }

            triangles.Add(triangleIndex);
        }

        private static void AddTriangleToMeshlet(
            int triangleIndex,
            uint[] indices,
            List<int>[] vertexToTriangles,
            bool[] assignedTriangles,
            Dictionary<int, int> localVertexMap,
            List<int> meshletVertexIndices,
            List<int> meshletTriangles,
            HashSet<int> candidateTriangles,
            int maxVerticesPerMeshlet,
            ref int assignedTriangleCount,
            ref int minTriangle,
            ref int maxTriangle)
        {
            if (assignedTriangles[triangleIndex])
                return;

            int i0 = (int)indices[triangleIndex * 3 + 0];
            int i1 = (int)indices[triangleIndex * 3 + 1];
            int i2 = (int)indices[triangleIndex * 3 + 2];

            AddLocalVertex(i0, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);
            AddLocalVertex(i1, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);
            AddLocalVertex(i2, localVertexMap, meshletVertexIndices, maxVerticesPerMeshlet);

            assignedTriangles[triangleIndex] = true;
            assignedTriangleCount++;
            meshletTriangles.Add(triangleIndex);
            minTriangle = Math.Min(minTriangle, triangleIndex);
            maxTriangle = Math.Max(maxTriangle, triangleIndex);

            AddCandidateTriangles(i0, vertexToTriangles, assignedTriangles, candidateTriangles);
            AddCandidateTriangles(i1, vertexToTriangles, assignedTriangles, candidateTriangles);
            AddCandidateTriangles(i2, vertexToTriangles, assignedTriangles, candidateTriangles);
            candidateTriangles.Remove(triangleIndex);
        }

        private static void AddLocalVertex(
            int vertexIndex,
            Dictionary<int, int> localVertexMap,
            List<int> meshletVertexIndices,
            int maxVerticesPerMeshlet)
        {
            if (localVertexMap.ContainsKey(vertexIndex))
                return;

            if (localVertexMap.Count >= maxVerticesPerMeshlet)
                throw new InvalidOperationException("Generated meshlet exceeded the local vertex limit.");

            localVertexMap.Add(vertexIndex, localVertexMap.Count);
            meshletVertexIndices.Add(vertexIndex);
        }

        private static void AddCandidateTriangles(
            int vertexIndex,
            List<int>[] vertexToTriangles,
            bool[] assignedTriangles,
            HashSet<int> candidateTriangles)
        {
            List<int>? adjacentTriangles = vertexToTriangles[vertexIndex];
            if (adjacentTriangles == null)
                return;

            foreach (int adjacentTriangle in adjacentTriangles)
            {
                if (!assignedTriangles[adjacentTriangle])
                    candidateTriangles.Add(adjacentTriangle);
            }
        }

        private static int SelectBestMeshletCandidate(
            HashSet<int> candidateTriangles,
            bool[] assignedTriangles,
            uint[] indices,
            Dictionary<int, int> localVertexMap,
            int maxVerticesPerMeshlet)
        {
            int bestCandidate = -1;
            int bestScore = int.MinValue;
            Span<int> staleCandidates = stackalloc int[Math.Min(candidateTriangles.Count, 64)];
            int staleCount = 0;

            foreach (int candidate in candidateTriangles)
            {
                if (assignedTriangles[candidate])
                {
                    if (staleCount < staleCandidates.Length)
                        staleCandidates[staleCount++] = candidate;
                    continue;
                }

                int newVertexCount = CountNewTriangleVertices(candidate, indices, localVertexMap);
                if (localVertexMap.Count + newVertexCount > maxVerticesPerMeshlet)
                    continue;

                int sharedVertexCount = 3 - newVertexCount;
                if (sharedVertexCount <= 0)
                    continue;

                int score = sharedVertexCount * 1000 - newVertexCount * 10;
                if (score > bestScore || (score == bestScore && candidate < bestCandidate))
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            for (int i = 0; i < staleCount; i++)
                candidateTriangles.Remove(staleCandidates[i]);

            return bestCandidate;
        }

        private static int SelectBestSequentialFallbackCandidate(
            int seedTriangle,
            bool[] assignedTriangles,
            uint[] indices,
            Dictionary<int, int> localVertexMap,
            int maxVerticesPerMeshlet)
        {
            int triangleCount = assignedTriangles.Length;
            int searchEnd = Math.Min(triangleCount, seedTriangle + GreedyFallbackTriangleSearchWindow);
            int bestCandidate = -1;
            int bestScore = int.MinValue;

            for (int triangleIndex = seedTriangle + 1; triangleIndex < searchEnd; triangleIndex++)
            {
                if (assignedTriangles[triangleIndex])
                    continue;

                int newVertexCount = CountNewTriangleVertices(triangleIndex, indices, localVertexMap);
                if (localVertexMap.Count + newVertexCount > maxVerticesPerMeshlet)
                    continue;

                int sharedVertexCount = 3 - newVertexCount;
                int score = sharedVertexCount * 1000 - newVertexCount * 100 - (triangleIndex - seedTriangle);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = triangleIndex;
                }
            }

            return bestCandidate;
        }

        private static int CountNewTriangleVertices(
            int triangleIndex,
            uint[] indices,
            Dictionary<int, int> localVertexMap)
        {
            int count = 0;
            int i0 = (int)indices[triangleIndex * 3 + 0];
            int i1 = (int)indices[triangleIndex * 3 + 1];
            int i2 = (int)indices[triangleIndex * 3 + 2];

            if (!localVertexMap.ContainsKey(i0))
                count++;
            if (i1 != i0 && !localVertexMap.ContainsKey(i1))
                count++;
            if (i2 != i0 && i2 != i1 && !localVertexMap.ContainsKey(i2))
                count++;

            return count;
        }

        private struct BoundingSphere
        {
            public Vector3 Center;
            public float Radius;
        }

        private static BoundingSphere CalculateBoundingSphere(Vector3[] vertices, List<int> vertexIndices)
        {
            if (vertexIndices.Count == 0)
                return new BoundingSphere { Center = Vector3.Zero, Radius = 0 };

            Vector3 min = vertices[vertexIndices[0]];
            Vector3 max = min;

            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int idx = vertexIndices[i];
                Vector3 v = vertices[idx];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            Vector3 center = (min + max) * 0.5f;
            float radius = 0;

            for (int i = 0; i < vertexIndices.Count; i++)
            {
                int idx = vertexIndices[i];
                float dist = Vector3.Distance(center, vertices[idx]);
                if (dist > radius)
                    radius = dist;
            }

            return new BoundingSphere { Center = center, Radius = radius };
        }

        /// <summary>
        /// Legacy bindless vertex-buffer alias. Static geometry is stored in the split
        /// position, normal/tangent, and UV/color streams; consumers must use those
        /// streams rather than interpreting this buffer as <see cref="GPUVertex"/> data.
        /// </summary>
        [Obsolete("Static vertex data is split across VertexPositionBuffer, VertexNormalTangentBuffer, and VertexUvColorBuffer.")]
        public BufferHandle VertexBuffer => _vertexPositionBuffer;
        public BufferHandle VertexPositionBuffer => _vertexPositionBuffer;
        public BufferHandle VertexNormalTangentBuffer => _vertexNormalTangentBuffer;
        public BufferHandle VertexUvColorBuffer => _vertexUvColorBuffer;
        public BufferHandle IndexBuffer => _indexBuffer;
        public BufferHandle MeshMetadataBuffer => _meshMetadataBuffer;
        public BufferHandle MeshletBuffer => _meshletBuffer;
        public BufferHandle MeshletVertexIndexBuffer => _meshletVertexIndexBuffer;
        public BufferHandle MeshletTriangleIndexBuffer => _meshletTriangleIndexBuffer;
        public BufferHandle SkinningDataBuffer => _skinningDataBuffer;

        /// <summary>Interleaved static vertex storage has been eliminated.</summary>
        public ulong VertexBytesUsed => 0;
        public ulong VertexPositionBytesUsed => _vertexPositionBytesUsed;
        public ulong VertexNormalTangentBytesUsed => _vertexNormalTangentBytesUsed;
        public ulong VertexUvColorBytesUsed => _vertexUvColorBytesUsed;
        public ulong IndexBytesUsed => _indexBytesUsed;
        public ulong MeshMetadataBytesUsed => _meshMetadataBytesUsed;
        public ulong MeshletBytesUsed => _meshletBytesUsed;
        public ulong MeshletVertexIndexBytesUsed => _meshletVertexIndexBytesUsed;
        public ulong MeshletTriangleIndexBytesUsed => _meshletTriangleIndexBytesUsed;
        public ulong SkinningDataBytesUsed => _skinningDataBytesUsed;
        public ulong MeshBufferAllocatedBytes =>
            SafeGetBufferSize(_vertexPositionBuffer) +
            SafeGetBufferSize(_vertexNormalTangentBuffer) +
            SafeGetBufferSize(_vertexUvColorBuffer) +
            SafeGetBufferSize(_indexBuffer) +
            SafeGetBufferSize(_meshMetadataBuffer) +
            SafeGetBufferSize(_meshletBuffer) +
            SafeGetBufferSize(_meshletVertexIndexBuffer) +
            SafeGetBufferSize(_meshletTriangleIndexBuffer) +
            SafeGetBufferSize(_skinningDataBuffer);
        public ulong MeshBufferUsedBytes =>
            _vertexPositionBytesUsed +
            _vertexNormalTangentBytesUsed +
            _vertexUvColorBytesUsed +
            _indexBytesUsed +
            _meshMetadataBytesUsed +
            _meshletBytesUsed +
            _meshletVertexIndexBytesUsed +
            _meshletTriangleIndexBytesUsed +
            _skinningDataBytesUsed;
        public float MeshBufferUtilization => MeshBufferAllocatedBytes == 0
            ? 0f
            : (float)((double)MeshBufferUsedBytes / MeshBufferAllocatedBytes);
        public int MeshBufferCompactionCount { get; private set; }
        public ulong MeshBufferCompactedBytesSaved { get; private set; }
        public long MeshBufferGrowthRetryCount
        {
            get
            {
                lock (_lock)
                    return _meshBufferGrowthRetryCount;
            }
        }
        public long MeshBufferGrowthRetrySuccessCount
        {
            get
            {
                lock (_lock)
                    return _meshBufferGrowthRetrySuccessCount;
            }
        }
        public long MeshBufferCompactionOutOfDeviceMemorySkipCount
        {
            get
            {
                lock (_lock)
                {
                    return
                        _meshBufferCompactionOutOfDeviceMemorySkipCount;
                }
            }
        }
        public long PostCommitCleanupFailureCount
        {
            get
            {
                lock (_lock)
                    return _postCommitCleanupFailureCount;
            }
        }

        public Exception? LastPostCommitCleanupFailure
        {
            get
            {
                lock (_lock)
                    return _lastPostCommitCleanupFailure;
            }
        }

        public MeshBufferCompactionStats CompactStaticBuffers(float headroomFactor = 1.15f)
        {
            ThrowIfDisposed();
            if (!float.IsFinite(headroomFactor) || headroomFactor < 1f)
                throw new ArgumentOutOfRangeException(nameof(headroomFactor), "Compaction headroom must be finite and at least 1.0.");

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ThrowIfRegistrationUploadActiveLocked();
                MeshBufferHandles originalBuffers =
                    CaptureMeshBufferHandles();
                ulong beforeBytes =
                    GetMeshBufferAllocatedBytes(originalBuffers);
                if (!ShouldCompactBuffer(_vertexPositionBuffer, _vertexPositionBytesUsed, InitialVertexPositionBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_vertexNormalTangentBuffer, _vertexNormalTangentBytesUsed, InitialVertexNormalTangentBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_vertexUvColorBuffer, _vertexUvColorBytesUsed, InitialVertexUvColorBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_indexBuffer, _indexBytesUsed, InitialIndexBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_meshMetadataBuffer, _meshMetadataBytesUsed, InitialMeshMetadataBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_meshletBuffer, _meshletBytesUsed, InitialMeshletBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_meshletVertexIndexBuffer, _meshletVertexIndexBytesUsed, InitialMeshletVertexIndexBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_meshletTriangleIndexBuffer, _meshletTriangleIndexBytesUsed, InitialMeshletTriangleIndexBufferSize, headroomFactor) &&
                    !ShouldCompactBuffer(_skinningDataBuffer, _skinningDataBytesUsed, InitialSkinningDataBufferSize, headroomFactor))
                {
                    return new MeshBufferCompactionStats(false, beforeBytes, beforeBytes, 0);
                }

                _quarantinedUploadBuffers.EnsureCapacity(
                    checked(_quarantinedUploadBuffers.Count + 9));
                _quarantinedUploadFences.EnsureCapacity(
                    checked(_quarantinedUploadFences.Count + 1));
                RegisteredMeshBufferHandles registeredBufferSnapshot =
                    CaptureRegisteredMeshBufferHandles();
                var stateSnapshot =
                    MeshBufferCompactionStateSnapshot.Capture(this);
                var uploadAttempt =
                    new MeshGpuUploadAttempt(originalBuffers);
                ulong afterBytes = beforeBytes;
                ulong savedBytes = 0;

                try
                {
                    MeshUploadTransaction.Execute(
                        completeGpuUpload: () =>
                        {
                            CompleteMeshBufferCompaction(
                                uploadAttempt,
                                headroomFactor);
                            afterBytes = GetMeshBufferAllocatedBytes(
                                uploadAttempt.CandidateBuffers);
                            savedBytes = beforeBytes > afterBytes
                                ? beforeBytes - afterBytes
                                : 0;
                        },
                        publishCandidateBindings: () =>
                            UpdateRegisteredBindlessBuffers(
                                uploadAttempt.CandidateBuffers),
                        commitAuthoritativeState: () =>
                            CommitMeshBufferCompaction(
                                uploadAttempt.CandidateBuffers,
                                savedBytes),
                        cleanupGpuUpload: () =>
                            CleanupMeshGpuUpload(uploadAttempt),
                        restoreAuthoritativeState: () =>
                            stateSnapshot.Restore(this),
                        restoreAuthoritativeBindings: () =>
                            RestoreRegisteredBindlessBuffers(
                                registeredBufferSnapshot),
                        destroyCandidateResources: () =>
                            DestroyCandidateUploadBuffers(
                                uploadAttempt),
                        quarantineCandidateResources: () =>
                            QuarantineCandidateUploadBuffers(
                                uploadAttempt),
                        restoreReservations: static () => { });
                }
                catch (Exception failure) when (
                    MeshBufferCompactionFailurePolicy.ShouldSkip(
                        failure))
                {
                    RecordMeshBufferCompactionMemorySkip(failure);
                    return new MeshBufferCompactionStats(
                        false,
                        beforeBytes,
                        beforeBytes,
                        0);
                }

                FinalizeCommittedMeshUpload(uploadAttempt);

                return new MeshBufferCompactionStats(
                    savedBytes > 0,
                    beforeBytes,
                    afterBytes,
                    savedBytes);
            }
        }

        public IReadOnlyList<MeshletQualityEntry> GetMeshletQualityEntries(int maxEntries)
        {
            ThrowIfDisposed();
            if (maxEntries <= 0)
                return Array.Empty<MeshletQualityEntry>();

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (!_meshletQualityDiagnosticsDirty &&
                    _cachedMeshletQualityEntryLimit == maxEntries)
                {
                    return _cachedMeshletQualityEntries;
                }

                var entries = new List<MeshletQualityEntry>();
                for (int i = 0; i < _meshes.Count; i++)
                {
                    MeshInfo meshInfo = _meshes[i];
                    if (meshInfo.MeshletLodGeneratedCount == 0)
                        continue;

                    float averageTriangles = (float)((double)meshInfo.MeshletTriangleSum / meshInfo.MeshletLodGeneratedCount);
                    float averageVertices = (float)((double)meshInfo.MeshletVertexSum / meshInfo.MeshletLodGeneratedCount);
                    entries.Add(new MeshletQualityEntry(
                        i,
                        meshInfo.MeshletLodGeneratedCount,
                        meshInfo.SmallMeshletsUnder16Triangles,
                        meshInfo.SmallMeshletsUnder32Triangles,
                        averageTriangles,
                        averageVertices));
                }

                entries.Sort(static (left, right) =>
                {
                    int smallCompare = right.SmallMeshletsUnder32Triangles.CompareTo(left.SmallMeshletsUnder32Triangles);
                    if (smallCompare != 0)
                        return smallCompare;
                    return right.MeshletCount.CompareTo(left.MeshletCount);
                });

                if (entries.Count > maxEntries)
                    entries.RemoveRange(maxEntries, entries.Count - maxEntries);

                _cachedMeshletQualityEntries = entries.AsReadOnly();
                _cachedMeshletQualityEntryLimit = maxEntries;
                _meshletQualityDiagnosticsDirty = false;
                return _cachedMeshletQualityEntries;
            }
        }

        private ulong SafeGetBufferSize(BufferHandle handle)
        {
            if (!handle.IsValid)
                return 0;

            try
            {
                return _bufferManager.GetBufferSize(handle);
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        public void ValidateMeshInfoRanges(MeshInfo meshInfo)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ValidateElementRange(nameof(meshInfo.VertexOffset), meshInfo.VertexOffset, meshInfo.VertexCount, _vertexPositionBytesUsed / VertexPositionStride);
                ValidateElementRange(nameof(meshInfo.VertexOffset), meshInfo.VertexOffset, meshInfo.VertexCount, _vertexNormalTangentBytesUsed / VertexNormalTangentStride);
                ValidateElementRange(nameof(meshInfo.VertexOffset), meshInfo.VertexOffset, meshInfo.VertexCount, _vertexUvColorBytesUsed / VertexUvColorStride);
                ValidateElementRange(nameof(meshInfo.IndexOffset), meshInfo.IndexOffset, meshInfo.EffectiveGpuIndexCount, _indexBytesUsed / IndexStride);
                ValidateElementRange(nameof(meshInfo.MeshMetadataOffset), meshInfo.MeshMetadataOffset, 1, _meshMetadataBytesUsed / MeshMetadataStride);
                ValidateElementRange(nameof(meshInfo.PhysicalMeshletOffset), meshInfo.EffectivePhysicalMeshletOffset, meshInfo.EffectiveGpuMeshletRecordCount, _meshletBytesUsed / MeshletStride);
                ValidateElementRange(nameof(meshInfo.LocalVertexIndexOffset), meshInfo.LocalVertexIndexOffset, meshInfo.LocalVertexIndexCount, _meshletVertexIndexBytesUsed / IndexStride);
                ValidateElementRange(nameof(meshInfo.LocalTriangleIndexOffset), meshInfo.LocalTriangleIndexOffset, meshInfo.LocalTriangleIndexCount, _meshletTriangleIndexBytesUsed / IndexStride);
                ValidateElementRange(nameof(meshInfo.SkinningDataOffset), meshInfo.SkinningDataOffset, meshInfo.SkinningDataCount, _skinningDataBytesUsed / SkinningDataStride);
            }
        }

        private static void ValidateElementRange(string name, uint offset, uint count, ulong availableCount)
        {
            ulong end = (ulong)offset + count;
            if (end > availableCount)
            {
                throw new InvalidOperationException(
                    $"{name} range [{offset}, {end}) exceeds uploaded mesh buffer element count {availableCount}.");
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            ThrowIfDisposed();
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ThrowIfRegistrationUploadActiveLocked();
                _registeredBindlessHeap = bindlessHeap;

                RegisterStorageBuffer(bindlessHeap, BindlessIndex.SceneMeshMetadataBuffer, _meshMetadataBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.VertexBuffer, _vertexPositionBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.VertexPositionBuffer, _vertexPositionBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.VertexNormalTangentBuffer, _vertexNormalTangentBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.VertexUvColorBuffer, _vertexUvColorBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.IndexBuffer, _indexBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletBuffer, _meshletBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletVertexIndexBuffer, _meshletVertexIndexBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.MeshletTriangleIndexBuffer, _meshletTriangleIndexBuffer);
                RegisterStorageBuffer(bindlessHeap, BindlessIndex.SkinningVertexDataBuffer, _skinningDataBuffer);
                _registeredMeshMetadataBuffer = _meshMetadataBuffer;
                _registeredVertexBuffer = _vertexPositionBuffer;
                _registeredVertexPositionBuffer = _vertexPositionBuffer;
                _registeredVertexNormalTangentBuffer = _vertexNormalTangentBuffer;
                _registeredVertexUvColorBuffer = _vertexUvColorBuffer;
                _registeredIndexBuffer = _indexBuffer;
                _registeredMeshletBuffer = _meshletBuffer;
                _registeredMeshletVertexIndexBuffer = _meshletVertexIndexBuffer;
                _registeredMeshletTriangleIndexBuffer = _meshletTriangleIndexBuffer;
                _registeredSkinningDataBuffer = _skinningDataBuffer;
            }
        }

        private void RegisterStorageBuffer(BindlessHeap bindlessHeap, int bindlessIndex, BufferHandle handle)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            bindlessHeap.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        public int ActiveMeshCount
        {
            get
            {
                lock (_lock)
                    return _meshLifetimes.ActiveCount;
            }
        }

        /// <summary>
        /// Bytes below the current stream high-water marks which are not owned
        /// by a live mesh. Interior holes are intentionally retained because
        /// moving live ranges would invalidate acceleration-structure inputs.
        /// Registration fails closed once the explicit retained-hole budget is
        /// exhausted.
        /// </summary>
        public ulong RetainedDeadMeshBytes
        {
            get
            {
                lock (_lock)
                    return CalculateRetainedDeadMeshBytes();
            }
        }

        public long RetainedDeadMeshBudgetRejectionCount
        {
            get
            {
                lock (_lock)
                    return _retainedDeadMeshBudgetRejectionCount;
            }
        }

        public void RetainMesh(MeshHandle handle)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ThrowIfRegistrationUploadActiveLocked();
                _meshLifetimes.Retain(handle);
            }
        }

        public void ReleaseMesh(MeshHandle handle)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ThrowIfRegistrationUploadActiveLocked();
                int referenceCount =
                    _meshLifetimes.GetReferenceCount(handle);
                if (referenceCount > 1)
                {
                    _meshLifetimes.Release(handle);
                    return;
                }

                if (handle.Index >= _meshes.Count ||
                    handle.Index >= _transportGeometry.Count)
                {
                    throw new InvalidOperationException(
                        "Mesh lifetime state diverged from slot-owned CPU data.");
                }

                MeshInfo meshInfo = _meshes[handle.Index];
                MeshTransportGeometry transportGeometry =
                    _transportGeometry[handle.Index];
                long emissiveBytes = checked(
                    (long)(transportGeometry.PrimitiveTransportProfile
                        ?.EmissiveTriangles.Length ?? 0) *
                    GiPrimitiveTransportProfile
                        .EstimatedEmissiveTriangleRecordBytes);
                long finalRuntimeEmissiveBytes = checked(
                    _runtimeEmissiveTriangleBytes - emissiveBytes);
                if (finalRuntimeEmissiveBytes < 0)
                {
                    throw new InvalidOperationException(
                        "Released mesh emissive transport accounting exceeded the authoritative total.");
                }

                MeshReleaseState releaseState =
                    PrepareMeshReleaseState(
                        handle.Index,
                        meshInfo,
                        finalRuntimeEmissiveBytes);
                if (!_meshLifetimes.Release(handle))
                {
                    throw new InvalidOperationException(
                        "Final mesh release unexpectedly retained a live reference.");
                }

                ApplyMeshReleaseState(
                    handle.Index,
                    releaseState);
            }
        }

        private MeshReleaseState PrepareMeshReleaseState(
            int meshIndex,
            MeshInfo meshInfo,
            long finalRuntimeEmissiveBytes)
        {
            ValidateReleasedRange(
                _vertexPositionBytesUsed,
                meshInfo.VertexOffset,
                meshInfo.VertexCount,
                VertexPositionStride,
                "vertex-position");
            ValidateReleasedRange(
                _vertexNormalTangentBytesUsed,
                meshInfo.VertexOffset,
                meshInfo.VertexCount,
                VertexNormalTangentStride,
                "vertex-normal/tangent");
            ValidateReleasedRange(
                _vertexUvColorBytesUsed,
                meshInfo.VertexOffset,
                meshInfo.VertexCount,
                VertexUvColorStride,
                "vertex-UV/color");
            ValidateReleasedRange(
                _indexBytesUsed,
                meshInfo.IndexOffset,
                meshInfo.EffectiveGpuIndexCount,
                IndexStride,
                "index");
            ValidateReleasedRange(
                _meshletBytesUsed,
                meshInfo.EffectivePhysicalMeshletOffset,
                meshInfo.EffectiveGpuMeshletRecordCount,
                MeshletStride,
                "meshlet");
            ValidateReleasedRange(
                _meshletVertexIndexBytesUsed,
                meshInfo.LocalVertexIndexOffset,
                meshInfo.LocalVertexIndexCount,
                IndexStride,
                "meshlet vertex-index");
            ValidateReleasedRange(
                _meshletTriangleIndexBytesUsed,
                meshInfo.LocalTriangleIndexOffset,
                meshInfo.LocalTriangleIndexCount,
                IndexStride,
                "meshlet triangle-index");
            ValidateReleasedRange(
                _skinningDataBytesUsed,
                meshInfo.SkinningDataOffset,
                meshInfo.SkinningDataCount,
                SkinningDataStride,
                "skinning");

            MeshStreamHighWater remaining =
                CalculateLiveStreamHighWater(meshIndex);
            ValidateRemainingHighWater(remaining);

            ulong metadataEnd = checked(
                ((ulong)meshInfo.MeshMetadataOffset + 1) *
                MeshMetadataStride);
            if (metadataEnd > _meshMetadataBytesUsed)
            {
                throw new InvalidOperationException(
                    "Released mesh metadata range exceeds authoritative storage.");
            }
            int highestRemainingSlot =
                _meshLifetimes.FindHighestLiveSlotExcluding(
                    meshIndex);
            ulong meshMetadataBytesUsed = highestRemainingSlot < 0
                ? 0
                : checked(
                    ((ulong)highestRemainingSlot + 1) *
                    MeshMetadataStride);
            if (meshMetadataBytesUsed > _meshMetadataBytesUsed)
            {
                throw new InvalidOperationException(
                    "Remaining mesh metadata range exceeds authoritative storage.");
            }

            int meshletStart = checked(
                (int)meshInfo.EffectivePhysicalMeshletOffset);
            int meshletCount = checked(
                (int)meshInfo.EffectiveGpuMeshletRecordCount);
            int meshletEnd = checked(meshletStart + meshletCount);
            if (meshletEnd > _meshlets.Count)
            {
                throw new InvalidOperationException(
                    "Released meshlet range exceeds the CPU meshlet cache.");
            }
            _managedCpuMeshlets.ValidateRelease(meshIndex, meshInfo);

            return new MeshReleaseState(
                remaining.VertexElements * VertexPositionStride,
                remaining.VertexElements *
                    VertexNormalTangentStride,
                remaining.VertexElements * VertexUvColorStride,
                remaining.IndexElements * IndexStride,
                meshMetadataBytesUsed,
                remaining.MeshletElements * MeshletStride,
                remaining.MeshletVertexIndexElements * IndexStride,
                remaining.MeshletTriangleIndexElements * IndexStride,
                remaining.SkinningElements * SkinningDataStride,
                finalRuntimeEmissiveBytes,
                meshletStart,
                meshletCount,
                checked((int)remaining.MeshletElements),
                meshInfo.UsesManagedPhysicalResidency);
        }

        private void ApplyMeshReleaseState(
            int meshIndex,
            MeshReleaseState releaseState)
        {
            _vertexPositionBytesUsed =
                releaseState.VertexPositionBytesUsed;
            _vertexNormalTangentBytesUsed =
                releaseState.VertexNormalTangentBytesUsed;
            _vertexUvColorBytesUsed =
                releaseState.VertexUvColorBytesUsed;
            _indexBytesUsed = releaseState.IndexBytesUsed;
            _meshMetadataBytesUsed =
                releaseState.MeshMetadataBytesUsed;
            _meshletBytesUsed = releaseState.MeshletBytesUsed;
            _meshletVertexIndexBytesUsed =
                releaseState.MeshletVertexIndexBytesUsed;
            _meshletTriangleIndexBytesUsed =
                releaseState.MeshletTriangleIndexBytesUsed;
            _skinningDataBytesUsed =
                releaseState.SkinningDataBytesUsed;
            _runtimeEmissiveTriangleBytes =
                releaseState.RuntimeEmissiveTriangleBytes;

            if (releaseState.MeshletCount > 0)
            {
                CollectionsMarshal.AsSpan(_meshlets)
                    .Slice(
                        releaseState.MeshletStart,
                        releaseState.MeshletCount)
                    .Clear();
                if (releaseState.FinalMeshletCount <
                    _meshlets.Count)
                {
                    CollectionsMarshal.SetCount(
                        _meshlets,
                        releaseState.FinalMeshletCount);
                }
            }
            if (releaseState.RemoveManagedCpuMeshlets)
                _managedCpuMeshlets.Release(meshIndex);

            _meshes[meshIndex] = default;
            _transportGeometry[meshIndex] = default;
            _meshletQualityDiagnosticsDirty = true;
        }

        private static void ValidateReleasedRange(
            ulong bytesUsed,
            uint elementOffset,
            uint elementCount,
            ulong stride,
            string rangeName)
        {
            ulong end = checked(
                ((ulong)elementOffset + elementCount) * stride);
            if (end > bytesUsed)
            {
                throw new InvalidOperationException(
                    $"Released mesh {rangeName} range exceeds authoritative storage.");
            }

        }

        private MeshStreamHighWater CalculateLiveStreamHighWater(
            int excludedMeshIndex = -1) =>
            MeshStreamLifetimeMetrics.CalculateHighWater(
                _meshes,
                _meshLifetimes,
                excludedMeshIndex);

        private ulong CalculateLiveMeshStreamBytes()
        {
            ulong liveBytes = 0;
            for (int index = 0; index < _meshes.Count; index++)
            {
                if (!_meshLifetimes.IsSlotLive(index))
                    continue;

                MeshInfo live = _meshes[index];
                liveBytes = checked(
                    liveBytes +
                    (ulong)live.VertexCount *
                        VertexPositionStride +
                    (ulong)live.VertexCount *
                        VertexNormalTangentStride +
                    (ulong)live.VertexCount *
                        VertexUvColorStride +
                    (ulong)live.EffectiveGpuIndexCount * IndexStride +
                    (ulong)live.EffectiveGpuMeshletRecordCount *
                        MeshletStride +
                    (ulong)live.LocalVertexIndexCount *
                        IndexStride +
                    (ulong)live.LocalTriangleIndexCount *
                        IndexStride +
                    (ulong)live.SkinningDataCount *
                        SkinningDataStride);
            }

            return liveBytes;
        }

        private ulong CalculateRetainedDeadMeshBytes()
        {
            ulong retainedBytes = checked(
                _vertexPositionBytesUsed +
                _vertexNormalTangentBytesUsed +
                _vertexUvColorBytesUsed +
                _indexBytesUsed +
                _meshletBytesUsed +
                _meshletVertexIndexBytesUsed +
                _meshletTriangleIndexBytesUsed +
                _skinningDataBytesUsed);
            return MeshRetentionBudget.CalculateDeadBytes(
                retainedBytes,
                CalculateLiveMeshStreamBytes());
        }

        private void ValidateRemainingHighWater(
            MeshStreamHighWater remaining)
        {
            if (remaining.VertexElements * VertexPositionStride >
                    _vertexPositionBytesUsed ||
                remaining.VertexElements *
                    VertexNormalTangentStride >
                    _vertexNormalTangentBytesUsed ||
                remaining.VertexElements * VertexUvColorStride >
                    _vertexUvColorBytesUsed ||
                remaining.IndexElements * IndexStride >
                    _indexBytesUsed ||
                remaining.MeshletElements * MeshletStride >
                    _meshletBytesUsed ||
                remaining.MeshletVertexIndexElements * IndexStride >
                    _meshletVertexIndexBytesUsed ||
                remaining.MeshletTriangleIndexElements * IndexStride >
                    _meshletTriangleIndexBytesUsed ||
                remaining.SkinningElements * SkinningDataStride >
                    _skinningDataBytesUsed)
            {
                throw new InvalidOperationException(
                    "A remaining live mesh range exceeds authoritative stream storage.");
            }
        }

        public MeshInfo GetMeshInfo(MeshHandle handle)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (!_meshLifetimes.IsLive(handle) ||
                    handle.Index >= _meshes.Count)
                    throw new InvalidOperationException("Invalid mesh handle");

                return _meshes[handle.Index];
            }
        }

        public MeshTransportGeometry GetTransportGeometry(MeshHandle handle)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (!_meshLifetimes.IsLive(handle) ||
                    handle.Index >= _transportGeometry.Count)
                    throw new InvalidOperationException("Invalid mesh transport-geometry handle.");

                MeshTransportGeometry geometry = _transportGeometry[handle.Index];
                if (!geometry.IsValid)
                    throw new InvalidOperationException("Mesh transport geometry is unavailable.");
                return geometry;
            }
        }

        public Meshlet GetMeshlet(uint meshletIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (MeshletVirtualAddress.IsVirtual(meshletIndex))
                {
                    throw new InvalidOperationException(
                        $"Virtual meshlet address 0x{meshletIndex:x8} requires its owning mesh handle.");
                }
                if (meshletIndex >= _meshlets.Count)
                    throw new InvalidOperationException(
                        $"Direct meshlet index {meshletIndex} exceeds the CPU cache count {_meshlets.Count}.");

                return _meshlets[(int)meshletIndex];
            }
        }

        internal Meshlet GetMeshlet(
            MeshHandle handle,
            uint meshletAddress)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (!_meshLifetimes.IsLive(handle) ||
                    handle.Index >= _meshes.Count)
                {
                    throw new InvalidOperationException(
                        "Invalid mesh handle for CPU meshlet lookup.");
                }

                MeshInfo meshInfo = _meshes[handle.Index];
                if (MeshletVirtualAddress.IsVirtual(meshletAddress))
                {
                    return _managedCpuMeshlets.Get(
                        handle,
                        meshInfo,
                        meshletAddress);
                }

                ulong geometryStart = meshInfo.MeshletOffset;
                ulong geometryEnd = checked(
                    geometryStart + meshInfo.MeshletLodGeneratedCount);
                if (meshInfo.UsesManagedPhysicalResidency ||
                    meshletAddress < geometryStart ||
                    meshletAddress >= geometryEnd ||
                    meshletAddress >= _meshlets.Count)
                {
                    throw new InvalidOperationException(
                        $"Direct meshlet address {meshletAddress} is outside mesh " +
                        $"{handle.Index}:{handle.Generation} geometry range.");
                }
                return _meshlets[(int)meshletAddress];
            }
        }

        public MeshletQualityStats GetMeshletQualityStats()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                int meshletCount = 0;
                ulong triangleSum = 0;
                ulong vertexSum = 0;
                int smallUnder16 = 0;
                int smallUnder32 = 0;

                foreach (MeshInfo meshInfo in _meshes)
                {
                    meshletCount += checked((int)meshInfo.MeshletLodGeneratedCount);
                    triangleSum += meshInfo.MeshletTriangleSum;
                    vertexSum += meshInfo.MeshletVertexSum;
                    smallUnder16 += checked((int)meshInfo.SmallMeshletsUnder16Triangles);
                    smallUnder32 += checked((int)meshInfo.SmallMeshletsUnder32Triangles);
                }

                return new MeshletQualityStats(meshletCount, triangleSum, vertexSum, smallUnder16, smallUnder32);
            }
        }

        private static void ApplyMeshletQualityStats(ref MeshInfo meshInfo, IReadOnlyList<Meshlet> meshlets)
        {
            uint triangleSum = 0;
            uint vertexSum = 0;
            uint smallUnder16 = 0;
            uint smallUnder32 = 0;

            foreach (Meshlet meshlet in meshlets)
            {
                triangleSum = CheckedAdd(triangleSum, meshlet.LocalTriangleCount);
                vertexSum = CheckedAdd(vertexSum, meshlet.LocalVertexCount);
                if (meshlet.LocalTriangleCount < 16)
                    smallUnder16++;
                if (meshlet.LocalTriangleCount < 32)
                    smallUnder32++;
            }

            meshInfo.MeshletTriangleSum = triangleSum;
            meshInfo.MeshletVertexSum = vertexSum;
            meshInfo.SmallMeshletsUnder16Triangles = smallUnder16;
            meshInfo.SmallMeshletsUnder32Triangles = smallUnder32;
        }

        private static ulong CheckedByteSize(int count, ulong stride)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return checked((ulong)count * stride);
        }

        private static ulong AddUploadStagingBytes(ulong currentOffset, ulong size)
        {
            if (size == 0)
                return currentOffset;

            ulong alignedOffset = AlignUp(currentOffset, UploadStagingAlignment);
            return checked(alignedOffset + size);
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        private static ulong CalculateCompactedBufferSize(ulong usedBytes, ulong minimumSize, float headroomFactor)
        {
            const ulong Granularity = 256 * 1024;
            if (usedBytes == 0)
                return minimumSize;

            double expanded = Math.Ceiling(usedBytes * (double)headroomFactor);
            ulong target = expanded >= ulong.MaxValue ? ulong.MaxValue : (ulong)expanded;
            target = Math.Max(target, usedBytes);
            target = Math.Max(target, minimumSize);
            if (target > ulong.MaxValue - (Granularity - 1))
                return ulong.MaxValue;
            return AlignUp(target, Granularity);
        }

        private static uint CheckedElementOffset(ulong byteOffset, ulong stride)
        {
            if (stride == 0 || byteOffset % stride != 0)
                throw new InvalidOperationException("Byte offset is not aligned to element stride.");

            ulong elementOffset = byteOffset / stride;
            if (elementOffset > uint.MaxValue)
                throw new InvalidOperationException("Mesh buffer element offset exceeds uint range.");

            return (uint)elementOffset;
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        private static uint CheckedAdd(uint left, uint right)
        {
            ulong value = (ulong)left + right;
            if (value > uint.MaxValue)
                throw new InvalidOperationException("Mesh offset arithmetic exceeded uint range.");

            return (uint)value;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposeCompleted)
                return;

            MeshRegistrationUpload? activeUpload;
            lock (_lock)
                activeUpload = _activeRegistrationUpload;
            // Shutdown is exceptional and may block, but command/staging and
            // candidate-buffer ownership must be drained before any backing
            // mesh buffer is destroyed.
            activeUpload?.Dispose();

            List<Exception>? failures = null;
            lock (_lock)
            {
                if (!_disposed)
                {
                    // Publish the terminal lifecycle state before releasing
                    // GPU ownership. Every operational entry point checks this
                    // flag under the same lock, while repeated Dispose calls
                    // continue draining any resources that fail below.
                    _disposed = true;
                    _registeredBindlessHeap = null;
                    _registeredVertexBuffer =
                        BufferHandle.Invalid;
                    _registeredIndexBuffer =
                        BufferHandle.Invalid;
                    _registeredMeshMetadataBuffer =
                        BufferHandle.Invalid;
                    _registeredMeshletBuffer =
                        BufferHandle.Invalid;
                    _registeredMeshletVertexIndexBuffer =
                        BufferHandle.Invalid;
                    _registeredMeshletTriangleIndexBuffer =
                        BufferHandle.Invalid;
                    _registeredSkinningDataBuffer =
                        BufferHandle.Invalid;
                    _registeredVertexPositionBuffer =
                        BufferHandle.Invalid;
                    _registeredVertexNormalTangentBuffer =
                        BufferHandle.Invalid;
                    _registeredVertexUvColorBuffer =
                        BufferHandle.Invalid;
                    _meshes.Clear();
                    _meshlets.Clear();
                    _managedCpuMeshlets.Clear();
                    _transportGeometry.Clear();
                    _runtimeEmissiveTriangleBytes = 0;
                    _meshLifetimes.Clear();
                }

                TryDestroyBuffer(
                    ref _vertexPositionBuffer,
                    MeshManagerDisposalResource.VertexPositionBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _vertexNormalTangentBuffer,
                    MeshManagerDisposalResource.VertexNormalTangentBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _vertexUvColorBuffer,
                    MeshManagerDisposalResource.VertexUvColorBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _indexBuffer,
                    MeshManagerDisposalResource.IndexBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _meshMetadataBuffer,
                    MeshManagerDisposalResource.MeshMetadataBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _meshletBuffer,
                    MeshManagerDisposalResource.MeshletBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _meshletVertexIndexBuffer,
                    MeshManagerDisposalResource.MeshletVertexIndexBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _meshletTriangleIndexBuffer,
                    MeshManagerDisposalResource.MeshletTriangleIndexBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _skinningDataBuffer,
                    MeshManagerDisposalResource.SkinningDataBuffer,
                    ref failures);
                TryDestroyBuffer(
                    ref _reusableUploadStagingBuffer,
                    MeshManagerDisposalResource.UploadStagingBuffer,
                    ref failures);
                if (!_reusableUploadStagingBuffer.IsValid)
                    _reusableUploadStagingBufferSize = 0;

                DurableResourceDestruction.TryDestroyAll(
                    _quarantinedUploadBuffers,
                    static handle => handle.IsValid,
                    handle =>
                    {
                        DisposalFaultInjector?.Invoke(
                            MeshManagerDisposalResource
                                .QuarantinedUploadBuffer);
                        _bufferManager.DestroyBuffer(handle);
                    },
                    ref failures);
                DurableResourceDestruction.TryDestroyAll(
                    _quarantinedUploadFences,
                    static fence => fence.Handle != 0,
                    fence =>
                    {
                        DisposalFaultInjector?.Invoke(
                            MeshManagerDisposalResource
                                .QuarantinedUploadFence);
                        DestroyUploadFence(fence);
                    },
                    ref failures);

                _disposeCompleted =
                    !_vertexPositionBuffer.IsValid &&
                    !_vertexNormalTangentBuffer.IsValid &&
                    !_vertexUvColorBuffer.IsValid &&
                    !_indexBuffer.IsValid &&
                    !_meshMetadataBuffer.IsValid &&
                    !_meshletBuffer.IsValid &&
                    !_meshletVertexIndexBuffer.IsValid &&
                    !_meshletTriangleIndexBuffer.IsValid &&
                    !_skinningDataBuffer.IsValid &&
                    !_reusableUploadStagingBuffer.IsValid &&
                    _quarantinedUploadBuffers.Count == 0 &&
                    _quarantinedUploadFences.Count == 0;
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more mesh-manager resources could not be disposed.",
                    failures);
            }

            if (_disposeCompleted)
            {
                System.Diagnostics.Debug.WriteLine(
                    "Mesh manager disposed.");
            }
        }

        private void TryDestroyBuffer(
            ref BufferHandle handle,
            MeshManagerDisposalResource resource,
            ref List<Exception>? failures)
        {
            Exception? failure =
                DurableResourceDestruction.TryDestroy(
                    ref handle,
                    BufferHandle.Invalid,
                    static candidate => candidate.IsValid,
                    candidate =>
                    {
                        DisposalFaultInjector?.Invoke(resource);
                        _bufferManager.DestroyBuffer(candidate);
                    });
            if (failure != null)
            {
                (failures ??= new List<Exception>())
                    .Add(failure);
            }
        }

        private void ThrowIfDisposed()
        {
            lock (_lock)
                ThrowIfDisposedLocked();
        }

        private void ThrowIfDisposedLocked() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        private void ThrowIfRegistrationUploadActiveLocked()
        {
            if (_activeRegistrationUpload != null)
            {
                throw new InvalidOperationException(
                    "Mesh resources cannot be mutated while a registration upload is awaiting GPU completion.");
            }
        }

        private readonly record struct MeshBufferHandles(
            BufferHandle VertexPosition,
            BufferHandle VertexNormalTangent,
            BufferHandle VertexUvColor,
            BufferHandle Index,
            BufferHandle MeshMetadata,
            BufferHandle Meshlet,
            BufferHandle MeshletVertexIndex,
            BufferHandle MeshletTriangleIndex,
            BufferHandle SkinningData);

        private readonly record struct RegisteredMeshBufferHandles(
            BufferHandle Vertex,
            BufferHandle Index,
            BufferHandle MeshMetadata,
            BufferHandle Meshlet,
            BufferHandle MeshletVertexIndex,
            BufferHandle MeshletTriangleIndex,
            BufferHandle SkinningData,
            BufferHandle VertexPosition,
            BufferHandle VertexNormalTangent,
            BufferHandle VertexUvColor);

        private readonly record struct MeshBufferReplacement(
            BufferHandle Original,
            BufferHandle Candidate);

        private readonly record struct MeshBufferCompactionTarget(
            MeshBufferStream Stream,
            ulong CurrentSize,
            ulong TargetSize);

        private readonly record struct MeshReleaseState(
            ulong VertexPositionBytesUsed,
            ulong VertexNormalTangentBytesUsed,
            ulong VertexUvColorBytesUsed,
            ulong IndexBytesUsed,
            ulong MeshMetadataBytesUsed,
            ulong MeshletBytesUsed,
            ulong MeshletVertexIndexBytesUsed,
            ulong MeshletTriangleIndexBytesUsed,
            ulong SkinningDataBytesUsed,
            long RuntimeEmissiveTriangleBytes,
            int MeshletStart,
            int MeshletCount,
            int FinalMeshletCount,
            bool RemoveManagedCpuMeshlets);

        private readonly record struct MeshBufferCompactionStateSnapshot(
            MeshBufferHandles Buffers,
            int CompactionCount,
            ulong CompactedBytesSaved)
        {
            public static MeshBufferCompactionStateSnapshot Capture(
                MeshManager owner) =>
                new(
                    owner.CaptureMeshBufferHandles(),
                    owner.MeshBufferCompactionCount,
                    owner.MeshBufferCompactedBytesSaved);

            public void Restore(MeshManager owner)
            {
                owner.ApplyMeshBufferHandles(Buffers);
                owner.MeshBufferCompactionCount = CompactionCount;
                owner.MeshBufferCompactedBytesSaved =
                    CompactedBytesSaved;
            }
        }

        private readonly record struct MeshUploadCommitState(
            ulong VertexPositionBytesUsed,
            ulong VertexNormalTangentBytesUsed,
            ulong VertexUvColorBytesUsed,
            ulong IndexBytesUsed,
            ulong MeshMetadataBytesUsed,
            ulong MeshletBytesUsed,
            ulong MeshletVertexIndexBytesUsed,
            ulong MeshletTriangleIndexBytesUsed,
            ulong SkinningDataBytesUsed,
            long RuntimeEmissiveTriangleBytes);

        private readonly record struct MeshUploadCapacityTargets(
            ulong VertexPositionBytes,
            ulong VertexNormalTangentBytes,
            ulong VertexUvColorBytes,
            ulong IndexBytes,
            ulong MeshMetadataBytes,
            ulong MeshletBytes,
            ulong MeshletVertexIndexBytes,
            ulong MeshletTriangleIndexBytes,
            ulong SkinningDataBytes)
        {
            public MeshUploadCapacityTargets AtLeast(
                in MeshUploadCapacityTargets required) =>
                new(
                    Math.Max(VertexPositionBytes,
                        required.VertexPositionBytes),
                    Math.Max(VertexNormalTangentBytes,
                        required.VertexNormalTangentBytes),
                    Math.Max(VertexUvColorBytes,
                        required.VertexUvColorBytes),
                    Math.Max(IndexBytes, required.IndexBytes),
                    Math.Max(MeshMetadataBytes,
                        required.MeshMetadataBytes),
                    Math.Max(MeshletBytes, required.MeshletBytes),
                    Math.Max(MeshletVertexIndexBytes,
                        required.MeshletVertexIndexBytes),
                    Math.Max(MeshletTriangleIndexBytes,
                        required.MeshletTriangleIndexBytes),
                    Math.Max(SkinningDataBytes,
                        required.SkinningDataBytes));
        }

        private sealed class MeshRegistrationUpload : IModelMeshUpload
        {
            private readonly MeshManager _owner;

            public MeshRegistrationUpload(
                MeshManager owner,
                MeshHandle[] handles,
                IReadOnlyList<PendingMeshUpload> pendingUploads,
                MeshUploadStateSnapshot stateSnapshot,
                RegisteredMeshBufferHandles registeredBufferSnapshot,
                int[] availableFreeMeshIndices,
                List<int> reservedFreeMeshIndices,
                MeshGpuUploadAttempt uploadAttempt,
                MeshUploadCommitState commitState)
            {
                _owner = owner;
                Handles = handles;
                PendingUploads = pendingUploads;
                StateSnapshot = stateSnapshot;
                RegisteredBufferSnapshot =
                    registeredBufferSnapshot;
                AvailableFreeMeshIndices =
                    availableFreeMeshIndices;
                ReservedFreeMeshIndices =
                    reservedFreeMeshIndices;
                UploadAttempt = uploadAttempt;
                CommitState = commitState;
            }

            public IReadOnlyList<MeshHandle> Handles { get; }
            public IReadOnlyList<PendingMeshUpload> PendingUploads
            {
                get;
            }
            public MeshUploadStateSnapshot StateSnapshot { get; }
            public RegisteredMeshBufferHandles RegisteredBufferSnapshot
            {
                get;
            }
            public int[] AvailableFreeMeshIndices { get; }
            public List<int> ReservedFreeMeshIndices { get; }
            public MeshGpuUploadAttempt UploadAttempt { get; }
            public MeshUploadCommitState CommitState { get; }
            public bool Committed { get; private set; }
            public bool Terminal { get; private set; }

            public bool TryCompleteGpuWork() =>
                _owner.AdvanceRegistrationUpload(
                    this,
                    cancel: false,
                    wait: false);

            public void CompleteGpuWork()
            {
                _owner.AdvanceRegistrationUpload(
                    this,
                    cancel: false,
                    wait: true);
            }

            public bool TryCancelGpuWork() =>
                _owner.AdvanceRegistrationUpload(
                    this,
                    cancel: true,
                    wait: false);

            public void Dispose()
            {
                _owner.AdvanceRegistrationUpload(
                    this,
                    cancel: true,
                    wait: true);
            }

            public void MarkCommitted()
            {
                Committed = true;
            }

            public void MarkTerminal()
            {
                Terminal = true;
            }
        }

        private sealed class MeshGpuUploadAttempt
        {
            private readonly List<MeshBufferReplacement> _replacements =
                new(9);
            private readonly List<BufferHandle> _replacedBuffers = new(9);
            private readonly HashSet<BufferHandle> _liveCandidates = new(9);

            public MeshGpuUploadAttempt(MeshBufferHandles originalBuffers)
            {
                OriginalBuffers = originalBuffers;
                CandidateBuffers = originalBuffers;
            }

            public MeshBufferHandles OriginalBuffers { get; }
            public MeshBufferHandles CandidateBuffers { get; set; }
            public UploadCommandContext? Upload { get; set; }
            public Fence UploadFence { get; set; }
            public IReadOnlyList<MeshBufferReplacement> Replacements =>
                _replacements;
            public IReadOnlyList<BufferHandle> ReplacedBuffers =>
                _replacedBuffers;

            public void TrackReplacement(
                BufferHandle original,
                BufferHandle candidate)
            {
                if (!original.IsValid || !candidate.IsValid)
                {
                    throw new InvalidOperationException(
                        "Mesh buffer replacement handles must be valid.");
                }
                if (original == candidate)
                {
                    throw new InvalidOperationException(
                        "A mesh buffer replacement must use a distinct candidate handle.");
                }
                if (_replacements.Any(
                        replacement =>
                            replacement.Original == original ||
                            replacement.Candidate == candidate))
                {
                    throw new InvalidOperationException(
                        "A mesh buffer was tracked more than once in one upload transaction.");
                }

                _replacements.Add(
                    new MeshBufferReplacement(original, candidate));
                _replacedBuffers.Add(original);
                _liveCandidates.Add(candidate);
            }

            public bool IsCandidateLive(BufferHandle candidate) =>
                _liveCandidates.Contains(candidate);

            public void MarkCandidateReleased(BufferHandle candidate)
            {
                _liveCandidates.Remove(candidate);
            }

            public void ResetForRetry()
            {
                if (Upload != null || UploadFence.Handle != 0)
                {
                    throw new InvalidOperationException(
                        "Mesh upload command resources must be cleaned before retry reset.");
                }
                if (_liveCandidates.Count != 0)
                {
                    throw new InvalidOperationException(
                        "Every geometric mesh-buffer candidate must be released before retry reset.");
                }

                CandidateBuffers = OriginalBuffers;
                _replacements.Clear();
                _replacedBuffers.Clear();
            }
        }

        private sealed class MeshUploadStateSnapshot
        {
            private readonly MeshBufferHandles _buffers;
            private readonly ulong _vertexPositionBytesUsed;
            private readonly ulong _vertexNormalTangentBytesUsed;
            private readonly ulong _vertexUvColorBytesUsed;
            private readonly ulong _indexBytesUsed;
            private readonly ulong _meshMetadataBytesUsed;
            private readonly ulong _meshletBytesUsed;
            private readonly ulong _meshletVertexIndexBytesUsed;
            private readonly ulong _meshletTriangleIndexBytesUsed;
            private readonly ulong _skinningDataBytesUsed;
            private readonly long _runtimeEmissiveTriangleBytes;
            private readonly int _meshCount;
            private readonly int _meshletCount;
            private readonly int _transportGeometryCount;
            private readonly int[] _pendingMeshIndices;
            private readonly MeshSlotLifetimeTable.RegistrationSnapshot
                _lifetimeSnapshot;
            private readonly MeshSlotSnapshot[] _reusedSlots;

            private MeshUploadStateSnapshot(
                MeshManager owner,
                MeshSlotLifetimeTable.RegistrationSnapshot
                    lifetimeSnapshot,
                MeshSlotSnapshot[] reusedSlots,
                int[] pendingMeshIndices)
            {
                _buffers = owner.CaptureMeshBufferHandles();
                _vertexPositionBytesUsed = owner._vertexPositionBytesUsed;
                _vertexNormalTangentBytesUsed =
                    owner._vertexNormalTangentBytesUsed;
                _vertexUvColorBytesUsed = owner._vertexUvColorBytesUsed;
                _indexBytesUsed = owner._indexBytesUsed;
                _meshMetadataBytesUsed = owner._meshMetadataBytesUsed;
                _meshletBytesUsed = owner._meshletBytesUsed;
                _meshletVertexIndexBytesUsed =
                    owner._meshletVertexIndexBytesUsed;
                _meshletTriangleIndexBytesUsed =
                    owner._meshletTriangleIndexBytesUsed;
                _skinningDataBytesUsed = owner._skinningDataBytesUsed;
                _runtimeEmissiveTriangleBytes =
                    owner._runtimeEmissiveTriangleBytes;
                _meshCount = owner._meshes.Count;
                _meshletCount = owner._meshlets.Count;
                _transportGeometryCount =
                    owner._transportGeometry.Count;
                _pendingMeshIndices = pendingMeshIndices;
                _lifetimeSnapshot = lifetimeSnapshot;
                _reusedSlots = reusedSlots;
            }

            public static MeshUploadStateSnapshot Capture(
                MeshManager owner,
                IReadOnlyList<PendingMeshUpload> pendingUploads)
            {
                var reusedSlots = new List<MeshSlotSnapshot>();
                var pendingIndices = new int[pendingUploads.Count];
                int pendingIndex = 0;
                foreach (PendingMeshUpload pending in pendingUploads)
                {
                    int index = pending.MeshIndex;
                    pendingIndices[pendingIndex++] = index;
                    if (index >= owner._meshes.Count)
                        continue;

                    reusedSlots.Add(new MeshSlotSnapshot(
                        index,
                        owner._meshes[index],
                        index < owner._transportGeometry.Count,
                        index < owner._transportGeometry.Count
                            ? owner._transportGeometry[index]
                            : default));
                }

                return new MeshUploadStateSnapshot(
                    owner,
                    owner._meshLifetimes
                        .CaptureRegistrationSnapshot(
                            pendingIndices),
                    reusedSlots.ToArray(),
                    pendingIndices);
            }

            public void Restore(MeshManager owner)
            {
                owner.ApplyMeshBufferHandles(_buffers);
                owner._vertexPositionBytesUsed =
                    _vertexPositionBytesUsed;
                owner._vertexNormalTangentBytesUsed =
                    _vertexNormalTangentBytesUsed;
                owner._vertexUvColorBytesUsed =
                    _vertexUvColorBytesUsed;
                owner._indexBytesUsed = _indexBytesUsed;
                owner._meshMetadataBytesUsed =
                    _meshMetadataBytesUsed;
                owner._meshletBytesUsed = _meshletBytesUsed;
                owner._meshletVertexIndexBytesUsed =
                    _meshletVertexIndexBytesUsed;
                owner._meshletTriangleIndexBytesUsed =
                    _meshletTriangleIndexBytesUsed;
                owner._skinningDataBytesUsed =
                    _skinningDataBytesUsed;
                owner._runtimeEmissiveTriangleBytes =
                    _runtimeEmissiveTriangleBytes;

                CollectionsMarshal.SetCount(owner._meshes, _meshCount);
                CollectionsMarshal.SetCount(
                    owner._meshlets,
                    _meshletCount);
                CollectionsMarshal.SetCount(
                    owner._transportGeometry,
                    _transportGeometryCount);
                owner._managedCpuMeshlets.RemovePreparedSlots(
                    _pendingMeshIndices);
                owner._meshLifetimes
                    .RestoreRegistrationSnapshot(
                        _lifetimeSnapshot);

                foreach (MeshSlotSnapshot slot in _reusedSlots)
                {
                    owner._meshes[slot.Index] = slot.Mesh;
                    if (slot.HadTransportGeometry)
                    {
                        owner._transportGeometry[slot.Index] =
                            slot.TransportGeometry;
                    }
                }
            }
        }

        private readonly record struct MeshSlotSnapshot(
            int Index,
            MeshInfo Mesh,
            bool HadTransportGeometry,
            MeshTransportGeometry TransportGeometry);

        private sealed class UploadCommandContext
        {
            public UploadCommandContext(
                CommandPool commandPool,
                CommandBuffer commandBuffer,
                BufferHandle stagingBuffer,
                ulong stagingBufferSize,
                bool ownsStagingBuffer = true)
            {
                CommandPool = commandPool;
                CommandBuffer = commandBuffer;
                StagingBuffer = stagingBuffer;
                StagingBufferSize = stagingBufferSize;
                OwnsStagingBuffer = ownsStagingBuffer;
                WrittenRanges = new List<BufferWriteRange>();
            }

            public CommandPool CommandPool;
            public CommandBuffer CommandBuffer;
            public BufferHandle StagingBuffer;
            public ulong StagingBufferSize;
            public ulong StagingOffset;
            public bool OwnsStagingBuffer { get; }
            public List<BufferWriteRange> WrittenRanges { get; }
            public bool Submitted { get; private set; }
            public bool Completed { get; private set; }

            public void TrackWrittenRange(BufferHandle buffer, ulong offset, ulong size)
            {
                if (size == 0)
                    return;

                for (int i = 0; i < WrittenRanges.Count; i++)
                {
                    if (WrittenRanges[i].Buffer != buffer)
                        continue;

                    ulong start = Math.Min(WrittenRanges[i].Offset, offset);
                    ulong end = Math.Max(WrittenRanges[i].End, checked(offset + size));
                    WrittenRanges[i] = new BufferWriteRange(buffer, start, checked(end - start));
                    return;
                }

                WrittenRanges.Add(new BufferWriteRange(buffer, offset, size));
            }

            public void MarkSubmitted()
            {
                if (Completed)
                {
                    throw new InvalidOperationException(
                        "A completed mesh upload cannot be submitted.");
                }
                Submitted = true;
            }

            public void MarkCompleted()
            {
                Completed = true;
            }
        }

        private readonly struct BufferWriteRange
        {
            public BufferWriteRange(BufferHandle buffer, ulong offset, ulong size)
            {
                Buffer = buffer;
                Offset = offset;
                Size = size;
            }

            public BufferHandle Buffer { get; }
            public ulong Offset { get; }
            public ulong Size { get; }
            public ulong End => checked(Offset + Size);
            public bool IsValid => Buffer.IsValid && Size > 0;
        }

        private sealed class PendingMeshUpload
        {
            public PendingMeshUpload(
                int meshIndex,
                uint generation,
                GPUVertexPositionStream[] vertexPositions,
                GPUVertexNormalTangentStream[] vertexNormalTangents,
                GPUVertexUvColorStream[] vertexUvColors,
                uint[] indices,
                MeshInfo meshInfo,
                GPUMeshInfo meshMetadata,
                Meshlet[] meshlets,
                GPUPackedMeshlet[] gpuMeshlets,
                uint[] localVertexIndices,
                uint[] localTriangleIndices,
                GPUVertexSkinningData[] skinningData,
                MeshTransportGeometry transportGeometry)
            {
                MeshIndex = meshIndex;
                Generation = generation;
                VertexPositions = vertexPositions;
                VertexNormalTangents = vertexNormalTangents;
                VertexUvColors = vertexUvColors;
                Indices = indices;
                MeshInfo = meshInfo;
                MeshMetadata = meshMetadata;
                Meshlets = meshlets;
                GpuMeshlets = gpuMeshlets;
                LocalVertexIndices = localVertexIndices;
                LocalTriangleIndices = localTriangleIndices;
                SkinningData = skinningData;
                TransportGeometry = transportGeometry;
            }

            public int MeshIndex { get; }
            public uint Generation { get; }
            public GPUVertexPositionStream[] VertexPositions { get; }
            public GPUVertexNormalTangentStream[] VertexNormalTangents { get; }
            public GPUVertexUvColorStream[] VertexUvColors { get; }
            public uint[] Indices { get; }
            public MeshInfo MeshInfo { get; }
            public GPUMeshInfo MeshMetadata { get; }
            public Meshlet[] Meshlets { get; }
            public GPUPackedMeshlet[] GpuMeshlets { get; }
            public uint[] LocalVertexIndices { get; }
            public uint[] LocalTriangleIndices { get; }
            public GPUVertexSkinningData[] SkinningData { get; }
            public MeshTransportGeometry TransportGeometry { get; }
        }
    }

    internal enum MeshManagerDisposalResource
    {
        VertexPositionBuffer,
        VertexNormalTangentBuffer,
        VertexUvColorBuffer,
        IndexBuffer,
        MeshMetadataBuffer,
        MeshletBuffer,
        MeshletVertexIndexBuffer,
        MeshletTriangleIndexBuffer,
        SkinningDataBuffer,
        UploadStagingBuffer,
        QuarantinedUploadBuffer,
        QuarantinedUploadFence
    }

    public readonly record struct MeshletQualityStats(
        int MeshletCount,
        ulong TriangleSum,
        ulong VertexSum,
        int SmallMeshletsUnder16Triangles,
        int SmallMeshletsUnder32Triangles);

    public readonly record struct MeshBufferCompactionStats(
        bool Compacted,
        ulong BeforeBytes,
        ulong AfterBytes,
        ulong SavedBytes);

    public readonly record struct MeshletQualityEntry(
        int MeshIndex,
        uint MeshletCount,
        uint SmallMeshletsUnder16Triangles,
        uint SmallMeshletsUnder32Triangles,
        float AverageTrianglesPerMeshlet,
        float AverageVerticesPerMeshlet);

    public readonly record struct MeshTransportGeometry(
        ReadOnlyMemory<GPUVertexPositionStream> VertexPositions,
        ReadOnlyMemory<GPUVertexUvColorStream> VertexUvColors,
        ReadOnlyMemory<uint> Indices,
        bool IsSkinned,
        GiPrimitiveTransportProfile? PrimitiveTransportProfile,
        ModelGiCausticHeroTopologyEvidence CausticTopologyEvidence,
        double LocalSurfaceArea)
    {
        public bool IsValid =>
            VertexPositions.Length > 0 &&
            VertexUvColors.Length == VertexPositions.Length &&
            Indices.Length >= 3 &&
            Indices.Length % 3 == 0;
        public int TriangleCount => Indices.Length / 3;
    }
}

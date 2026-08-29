using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Owns scene-level GPU buffers and builds per-frame scene payloads.
    /// This type owns allocation, upload, bindless registration, growth, and synchronization
    /// for the scene buffer contract.
    /// </summary>
    public sealed unsafe class SceneDataBuilder : IDisposable
    {
        private const int TileSize = RenderingConstants.ForwardClusterTileSize;
        private const int ClusterDepthSliceCount =
            RenderingConstants.ForwardClusterDepthSliceCount;
        private const int MaxLightsPerTile =
            RenderingConstants.ForwardClusterMaxLights;

        private const uint InitialObjectCapacity = 4096;
        private const uint InitialInstanceCapacity = 4096;
        private const uint InitialInstanceCandidateCapacity = 4096;
        private const uint InitialOpaqueMeshletDrawCapacity = 65536;
        private const uint InitialDepthMeshletDrawCapacity = 32768;
        private const uint InitialMaskedDepthMeshletDrawCapacity = 8192;
        private const uint InitialTransparentMeshletDrawCapacity = 4096;
        private const uint InitialDirectionalShadowMeshletDrawCapacity = 32768;
        private const uint InitialLocalShadowMeshletDrawCapacity = 8192;
        private const uint CpuMeshletCullingThreshold = 128;
        private const float MeshletLodHysteresisFraction = 0.15f;

        private static readonly ulong ObjectStride = (ulong)Marshal.SizeOf<GPUObjectData>();
        private static readonly ulong InstanceCandidateStride =
            (ulong)Marshal.SizeOf<GPUSceneInstanceCandidate>();
        private static readonly ulong MeshletDrawStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong PackedMeshletDrawStride = (ulong)Marshal.SizeOf<GPUPackedMeshletDrawCommand>();
        private static readonly ulong MeshletTaskFrameDataStride = (ulong)Marshal.SizeOf<GPUMeshletTaskFrameData>();
        private static readonly ulong TiledLightHeaderStride = (ulong)Marshal.SizeOf<GPUTiledLightHeader>();
        private static readonly ulong TiledLightIndexStride = (ulong)Marshal.SizeOf<GPULightIndex>();
        private const uint TiledLightDenseControlHeaderCount = 1u;

        private readonly VulkanContext _context;
        private readonly MeshManager _meshManager;
        private readonly BufferManager _bufferManager;
        private readonly StagingRing _stagingRing;
        private readonly SynchronizationManager _sync;
        private readonly MaterialManager _materialManager;
        private readonly TextureManager? _textureManager;
        private readonly object _lock = new object();

        private SceneBuffer _objectDataBuffer;
        private readonly SceneBuffer[] _instanceBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _instanceCandidateBuffers =
            new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _meshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _simpleNormalOpaqueMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _fullOpaqueMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _solidDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _maskedDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedSimpleNormalOpaqueMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedFullOpaqueMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedSolidDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedMaskedDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _meshletTaskFrameDataBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _transparentMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _directionalShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _localShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _directionalStaticShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _directionalDynamicShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _localStaticShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _localDynamicShadowMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private SceneBuffer _tiledLightHeaderBuffer;
        private SceneBuffer _tiledLightIndexBuffer;

        private readonly List<GPUObjectData> _objectData = new List<GPUObjectData>();
        private readonly List<GPUSceneInstanceCandidate> _instanceCandidates =
            new List<GPUSceneInstanceCandidate>();
        private readonly List<GPUMeshletDrawCommand> _meshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _simpleNormalOpaqueMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _fullOpaqueMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _solidDepthMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _maskedDepthMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedSimpleNormalOpaqueMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedFullOpaqueMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedSolidDepthMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedMaskedDepthMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUMeshletTaskFrameData> _meshletTaskFrameData = new List<GPUMeshletTaskFrameData>(1);
        private readonly List<GPUMeshletDrawCommand> _transparentMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<TransparentMaterialRun> _transparentMaterialRuns =
            new List<TransparentMaterialRun>();
        private readonly List<GPUMeshletDrawCommand> _directionalShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _localShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _directionalStaticShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _directionalDynamicShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _localStaticShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _localDynamicShadowMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly int[] _pointShadowFaceMasks = new int[4];
        private int _directionalShadowSkinnedObjectCount;
        private int _localShadowSkinnedObjectCount;
        private readonly List<ObjectDebugSnapshot> _objectDebugSnapshots = new List<ObjectDebugSnapshot>();
        private readonly List<TransparentMeshletDraw> _transparentSortScratch = new List<TransparentMeshletDraw>();
        private readonly Dictionary<RenderObject, Matrix4x4> _previousRenderObjectMatrices = new();
        private readonly Dictionary<StaticInstanceKey, Matrix4x4> _previousStaticInstanceMatrices = new();
        private readonly Dictionary<RenderObject, int> _previousRenderObjectLods = new();
        private readonly Dictionary<StaticInstanceKey, int> _previousStaticInstanceLods = new();
        private readonly Dictionary<MeshHandle, MeshInfo> _meshInfoCache = new Dictionary<MeshHandle, MeshInfo>();
        private readonly bool[] _instanceUploadDirtyFrames = new bool[FramesInFlight];
        private readonly UploadState[] _instanceUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _instanceCandidateUploadStates =
            new UploadState[FramesInFlight];
        private readonly UploadState[] _meshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _simpleNormalOpaqueMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _fullOpaqueMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _solidDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _maskedDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedSimpleNormalOpaqueMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedFullOpaqueMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedSolidDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedMaskedDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _meshletTaskFrameDataUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _transparentMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _directionalShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _localShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _directionalStaticShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _directionalDynamicShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _localStaticShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _localDynamicShadowMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly SceneBufferStream<GPUMeshletDrawCommand>[] _meshletDrawStreams;
        private readonly SceneBufferStream<GPUPackedMeshletDrawCommand>[] _packedMeshletDrawStreams;
        private readonly SceneBufferStream<GPUSceneInstanceCandidate>
            _instanceCandidateStream;

        private BindlessHeap? _registeredBindlessHeap;
        private UploadState _objectUploadState;
        private StaticScenePayloadSignature _lastStaticPayloadSignature;
        private SceneCullingSignature _lastCullingSignature;
        private DrawPacketSet _drawPacketSet = DrawPacketSet.Empty;
        private Matrix4x4 _lastCameraDependentViewProjection;
        private bool _hasLastCameraDependentViewProjection;
        private Matrix4x4 _lastTransparentViewProjection;
        private bool _hasLastTransparentViewProjection;
        private bool _hasCameraDependentTransparentContent;
        private Matrix4x4 _previousHiZViewProjectionMatrix = Matrix4x4.Identity;
        private Matrix4x4 _previousHiZInverseViewMatrix = Matrix4x4.Identity;
        private bool _hasPreviousHiZFrameData;
        private bool _hasCachedPayload;
        private ulong _sceneContentRevision;
        private uint _lastTileCountX;
        private uint _lastTileCountY;
        private uint _lastClusterCountZ;
        private ulong _lastUploadedBytes;
        private int _lastSceneUploadCount;
        private int _lastSceneUploadSkipped;
        private long _lastBuildMicroseconds;
        private int _opaqueObjectCount;
        private int _maskedObjectCount;
        private int _transparentObjectCount;
        private int _transparentReflectionReceiverObjectCount;
        private int _transparentReflectionReceiverMeshletCount;
        private int _thinGlassObjectCount;
        private int _thinGlassMeshletCount;
        private int _geometryDecalObjectCount;
        private int _geometryDecalMeshletCount;
        private int _blendMaterialCount;
        private int _maskMaterialCount;
        private int _geometryDecalMaterialCount;
        private int _transparentOverflowCount;
        private long _transparentSortMicroseconds;
        private int _staticInstanceBatchCount;
        private int _staticInstanceCount;
        private int _visibleStaticInstanceCount;
        private int _culledStaticInstanceCount;
        private int _staticBatchMeshletDrawCommandCount;
        private long _cpuStaticBatchBuildMicroseconds;
        private int _objectCandidatesCpu;
        private int _objectFrustumCulledCpu;
        private int _meshletCandidatesCpu;
        private int _meshletFrustumCulledCpu;
        private int _meshletLodSkippedCpu;
        private int _meshletLod0SubmittedCpu;
        private int _meshletLod1SubmittedCpu;
        private int _meshletLod2SubmittedCpu;
        private long _lastPayloadSignatureMicroseconds;
        private long _lastObjectCullMicroseconds;
        private long _lastMeshletCullMicroseconds;
        private long _lastUploadMicroseconds;
        private int _lastScenePayloadRebuilt;
        private int _lastCameraDrivenCpuDrawListRebuilt;
        private ulong _lastObjectUploadBytes;
        private ulong _lastInstanceUploadBytes;
        private ulong _lastInstanceCandidateUploadBytes;
        private ulong _lastMeshletDrawUploadBytes;
        private ulong _lastSolidDepthMeshletDrawUploadBytes;
        private ulong _lastMaskedDepthMeshletDrawUploadBytes;
        private ulong _lastPackedMeshletDrawUploadBytes;
        private ulong _lastPackedSolidDepthMeshletDrawUploadBytes;
        private ulong _lastPackedMaskedDepthMeshletDrawUploadBytes;
        private ulong _lastTransparentMeshletDrawUploadBytes;
        private ulong _lastTiledLightHeaderBufferClearBytes;
        private ulong _lastTiledLightIndexBufferClearBytes;
        private int _submittedMeshletCountCpu;
        private ulong _submittedMeshletTriangleSum;
        private ulong _submittedMeshletVertexSum;
        private int _submittedSmallMeshletsUnder16Triangles;
        private int _submittedSmallMeshletsUnder32Triangles;
        private int _normalConeEligibleOpaqueMeshletCount;
        private int _doubleSidedOpaqueMeshletCount;
        private int _instanceCandidateSimpleMeshletCapacity;
        private int _instanceCandidateSimpleNormalMeshletCapacity;
        private int _instanceCandidateFullMeshletCapacity;
        private int _instanceCandidateSolidDepthMeshletCapacity;
        private int _instanceCandidateMaskedDepthMeshletCapacity;
        private int _instanceCandidateDirectionalStaticShadowMeshletCapacity;
        private int _instanceCandidateDirectionalDynamicShadowMeshletCapacity;
        private int _instanceCandidateNormalConeMeshletCount;
        private int _instanceCandidateDoubleSidedMeshletCount;
        private bool _meshletNormalConeCullingEnabled;
        private bool _disposed;

        public bool CaptureCpuSnapshots { get; set; }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync)
            : this(
                context,
                meshManager,
                bufferManager,
                stagingRing,
                sync,
                new MaterialManager(context, bufferManager, stagingRing, sync),
                textureManager: null)
        {
        }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager? textureManager)
            : this(
                context,
                meshManager,
                bufferManager,
                stagingRing,
                sync,
                CreateMaterialManager(context, bufferManager, stagingRing, sync, textureManager),
                textureManager)
        {
        }

        public SceneDataBuilder(
            VulkanContext context,
            MeshManager meshManager,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            MaterialManager materialManager,
            TextureManager? textureManager = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _sync = sync ?? throw new ArgumentNullException(nameof(sync));
            _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
            _textureManager = textureManager;

            _objectDataBuffer = CreateSceneBuffer(InitialObjectCapacity, ObjectStride, "Scene Object Data Buffer");

            for (int i = 0; i < FramesInFlight; i++)
            {
                _instanceBuffers[i] = CreateSceneBuffer(InitialInstanceCapacity, ObjectStride, $"Scene Instance Buffer Frame {i}");
                _instanceCandidateBuffers[i] = CreateSceneBuffer(
                    InitialInstanceCandidateCapacity,
                    InstanceCandidateStride,
                    $"Scene Instance Candidate Buffer Frame {i}");
                _meshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, MeshletDrawStride, $"Opaque Meshlet Draw Buffer Frame {i}");
                _simpleNormalOpaqueMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, MeshletDrawStride, $"Simple Normal Opaque Meshlet Draw Buffer Frame {i}");
                _fullOpaqueMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, MeshletDrawStride, $"Full Opaque Meshlet Draw Buffer Frame {i}");
                _solidDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDepthMeshletDrawCapacity, MeshletDrawStride, $"Solid Depth Meshlet Draw Buffer Frame {i}");
                _maskedDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialMaskedDepthMeshletDrawCapacity, MeshletDrawStride, $"Masked Depth Meshlet Draw Buffer Frame {i}");
                _packedMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Opaque Meshlet Draw Buffer Frame {i}");
                _packedSimpleNormalOpaqueMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Simple Normal Opaque Meshlet Draw Buffer Frame {i}");
                _packedFullOpaqueMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Full Opaque Meshlet Draw Buffer Frame {i}");
                _packedSolidDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDepthMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Solid Depth Meshlet Draw Buffer Frame {i}");
                _packedMaskedDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialMaskedDepthMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Masked Depth Meshlet Draw Buffer Frame {i}");
                _meshletTaskFrameDataBuffers[i] = CreateSceneBuffer(1, MeshletTaskFrameDataStride, $"Meshlet Task Frame Data Buffer Frame {i}");
                _transparentMeshletDrawBuffers[i] = CreateSceneBuffer(InitialTransparentMeshletDrawCapacity, MeshletDrawStride, $"Transparent Meshlet Draw Buffer Frame {i}");
                _directionalShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDirectionalShadowMeshletDrawCapacity, MeshletDrawStride, $"Directional Shadow Meshlet Draw Buffer Frame {i}");
                _localShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialLocalShadowMeshletDrawCapacity, MeshletDrawStride, $"Local Shadow Meshlet Draw Buffer Frame {i}");
                _directionalStaticShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDirectionalShadowMeshletDrawCapacity, MeshletDrawStride, $"Directional Static Shadow Meshlet Draw Buffer Frame {i}");
                _directionalDynamicShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDirectionalShadowMeshletDrawCapacity, MeshletDrawStride, $"Directional Dynamic Shadow Meshlet Draw Buffer Frame {i}");
                _localStaticShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialLocalShadowMeshletDrawCapacity, MeshletDrawStride, $"Local Static Shadow Meshlet Draw Buffer Frame {i}");
                _localDynamicShadowMeshletDrawBuffers[i] = CreateSceneBuffer(InitialLocalShadowMeshletDrawCapacity, MeshletDrawStride, $"Local Dynamic Shadow Meshlet Draw Buffer Frame {i}");
            }

            _meshletDrawStreams =
            [
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _meshletDrawCommands,
                    _meshletDrawBuffers,
                    _meshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _simpleNormalOpaqueMeshletDrawCommands,
                    _simpleNormalOpaqueMeshletDrawBuffers,
                    _simpleNormalOpaqueMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _fullOpaqueMeshletDrawCommands,
                    _fullOpaqueMeshletDrawBuffers,
                    _fullOpaqueMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _solidDepthMeshletDrawCommands,
                    _solidDepthMeshletDrawBuffers,
                    _solidDepthMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.SolidDepthMeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _maskedDepthMeshletDrawCommands,
                    _maskedDepthMeshletDrawBuffers,
                    _maskedDepthMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MaskedDepthMeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _transparentMeshletDrawCommands,
                    _transparentMeshletDrawBuffers,
                    _transparentMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.TransparentMeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _directionalShadowMeshletDrawCommands,
                    _directionalShadowMeshletDrawBuffers,
                    _directionalShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _localShadowMeshletDrawCommands,
                    _localShadowMeshletDrawBuffers,
                    _localShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _directionalStaticShadowMeshletDrawCommands,
                    _directionalStaticShadowMeshletDrawBuffers,
                    _directionalStaticShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _directionalDynamicShadowMeshletDrawCommands,
                    _directionalDynamicShadowMeshletDrawBuffers,
                    _directionalDynamicShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _localStaticShadowMeshletDrawCommands,
                    _localStaticShadowMeshletDrawBuffers,
                    _localStaticShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw),
                new SceneBufferStream<GPUMeshletDrawCommand>(
                    _localDynamicShadowMeshletDrawCommands,
                    _localDynamicShadowMeshletDrawBuffers,
                    _localDynamicShadowMeshletDrawUploadStates,
                    MeshletDrawStride,
                    SceneUploadCategory.MeshletDraw)
            ];

            _packedMeshletDrawStreams =
            [
                new SceneBufferStream<GPUPackedMeshletDrawCommand>(
                    _packedMeshletDrawCommands,
                    _packedMeshletDrawBuffers,
                    _packedMeshletDrawUploadStates,
                    PackedMeshletDrawStride,
                    SceneUploadCategory.PackedMeshletDraw),
                new SceneBufferStream<GPUPackedMeshletDrawCommand>(
                    _packedSimpleNormalOpaqueMeshletDrawCommands,
                    _packedSimpleNormalOpaqueMeshletDrawBuffers,
                    _packedSimpleNormalOpaqueMeshletDrawUploadStates,
                    PackedMeshletDrawStride,
                    SceneUploadCategory.PackedMeshletDraw),
                new SceneBufferStream<GPUPackedMeshletDrawCommand>(
                    _packedFullOpaqueMeshletDrawCommands,
                    _packedFullOpaqueMeshletDrawBuffers,
                    _packedFullOpaqueMeshletDrawUploadStates,
                    PackedMeshletDrawStride,
                    SceneUploadCategory.PackedMeshletDraw),
                new SceneBufferStream<GPUPackedMeshletDrawCommand>(
                    _packedSolidDepthMeshletDrawCommands,
                    _packedSolidDepthMeshletDrawBuffers,
                    _packedSolidDepthMeshletDrawUploadStates,
                    PackedMeshletDrawStride,
                    SceneUploadCategory.PackedSolidDepthMeshletDraw),
                new SceneBufferStream<GPUPackedMeshletDrawCommand>(
                    _packedMaskedDepthMeshletDrawCommands,
                    _packedMaskedDepthMeshletDrawBuffers,
                    _packedMaskedDepthMeshletDrawUploadStates,
                    PackedMeshletDrawStride,
                    SceneUploadCategory.PackedMaskedDepthMeshletDraw)
            ];

            _instanceCandidateStream = new SceneBufferStream<GPUSceneInstanceCandidate>(
                _instanceCandidates,
                _instanceCandidateBuffers,
                _instanceCandidateUploadStates,
                InstanceCandidateStride,
                SceneUploadCategory.InstanceCandidate);

            System.Diagnostics.Debug.WriteLine("Scene data builder created.");
        }

        private static MaterialManager CreateMaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager? textureManager)
        {
            return textureManager == null
                ? new MaterialManager(context, bufferManager, stagingRing, sync)
                : new MaterialManager(context, bufferManager, stagingRing, sync, textureManager);
        }

        public SceneRenderingData Build(Scene scene,
            ICamera camera,
            uint screenWidth,
            uint screenHeight,
            CommandBuffer uploadCommandBuffer,
            bool useTiledLightCulling = true,
            GPUShadowData? directionalShadowData = null,
            int directionalShadowCascadeCount = 0,
            bool buildLocalShadowMeshlets = false,
            ReadOnlySpan<SelectedLocalShadow> selectedPointShadows = default,
            Vector2 projectionJitter = default,
            TransparencySettings? transparencySettings = null,
            DecalSettings? decalSettings = null,
            bool geometryDecalsEnabled = true,
            bool useCameraDependentCpuPayload = false,
            bool useCpuMeshletFrustumCulling = false,
            bool meshletNormalConeCullingEnabled = false,
            bool buildGpuInstanceCandidates = false,
            bool captureSceneSubmissionValidationLists = false,
            float gpuLod1DistanceRatio = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio,
            float gpuLod2DistanceRatio = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (uploadCommandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for scene data upload.", nameof(uploadCommandBuffer));

            gpuLod1DistanceRatio = SceneSubmissionSettings.ClampGpuLod1DistanceRatio(gpuLod1DistanceRatio);
            gpuLod2DistanceRatio = SceneSubmissionSettings.ClampGpuLod2DistanceRatio(
                gpuLod2DistanceRatio,
                gpuLod1DistanceRatio);
            geometryDecalsEnabled &= decalSettings?.GeometryDecalsEnabled ?? true;
            int isolatedDecalMaterialIndex = Math.Max(
                -1,
                decalSettings?.IsolatedMaterialIndex ?? -1);

            lock (_lock)
            {
                _meshletNormalConeCullingEnabled =
                    meshletNormalConeCullingEnabled;
                long buildStart = Stopwatch.GetTimestamp();
                int frameIndex = _stagingRing.CurrentFrameIndex;
                _lastUploadedBytes = 0;
                _lastSceneUploadCount = 0;
                _lastSceneUploadSkipped = 0;
                _lastPayloadSignatureMicroseconds = 0;
                _lastObjectCullMicroseconds = 0;
                _lastMeshletCullMicroseconds = 0;
                _lastUploadMicroseconds = 0;
                _lastScenePayloadRebuilt = 0;
                _lastCameraDrivenCpuDrawListRebuilt = 0;
                _lastObjectUploadBytes = 0;
                _lastInstanceUploadBytes = 0;
                _lastInstanceCandidateUploadBytes = 0;
                _lastMeshletDrawUploadBytes = 0;
                _lastSolidDepthMeshletDrawUploadBytes = 0;
                _lastMaskedDepthMeshletDrawUploadBytes = 0;
                _lastPackedMeshletDrawUploadBytes = 0;
                _lastPackedSolidDepthMeshletDrawUploadBytes = 0;
                _lastPackedMaskedDepthMeshletDrawUploadBytes = 0;
                _lastTransparentMeshletDrawUploadBytes = 0;
                _lastTiledLightHeaderBufferClearBytes = 0;
                _lastTiledLightIndexBufferClearBytes = 0;

                Matrix4x4 viewMatrix = camera.ViewMatrix;
                Matrix4x4 projectionMatrix = ApplyProjectionJitter(camera.ProjectionMatrix, projectionJitter);
                Matrix4x4 viewProjectionMatrix = viewMatrix * projectionMatrix;
                Matrix4x4 transparentViewProjectionMatrix =
                    viewMatrix * camera.ProjectionMatrix;
                Matrix4x4 inverseViewMatrix = viewMatrix.Invert();
                Matrix4x4 inverseProjectionMatrix = projectionMatrix.Invert();
                Matrix4x4 inverseViewProjectionMatrix = viewProjectionMatrix.Invert();
                Frustum frustum = ExtractFrustum(viewProjectionMatrix);
                Frustum transparentFrustum =
                    ExtractFrustum(transparentViewProjectionMatrix);
                Matrix4x4 previousHiZViewProjectionMatrix = _hasPreviousHiZFrameData
                    ? _previousHiZViewProjectionMatrix
                    : viewProjectionMatrix;
                Matrix4x4 previousHiZInverseViewMatrix = _hasPreviousHiZFrameData
                    ? _previousHiZInverseViewMatrix
                    : inverseViewMatrix;
                bool previousHiZFrameValid = _hasPreviousHiZFrameData &&
                    IsPreviousHiZCameraHistoryUsable(viewProjectionMatrix, previousHiZViewProjectionMatrix);
                bool cameraDependentCpuPayload =
                    useCameraDependentCpuPayload ||
                    useCpuMeshletFrustumCulling ||
                    CaptureCpuSnapshots;
                long signatureStart = Stopwatch.GetTimestamp();
                StaticScenePayloadSignature staticPayloadSignature = StaticScenePayloadSignature.Create(
                    scene,
                    _materialManager.MaterialDataRevision);
                SceneCullingSignature cullingSignature = SceneCullingSignature.Create(
                    scene,
                    viewProjectionMatrix,
                    directionalShadowData,
                    directionalShadowCascadeCount,
                    buildLocalShadowMeshlets,
                    selectedPointShadows,
                    transparencySettings,
                    decalSettings,
                    geometryDecalsEnabled,
                    isolatedDecalMaterialIndex,
                    CaptureCpuSnapshots,
                    captureSceneSubmissionValidationLists,
                    cameraDependentCpuPayload,
                    useCpuMeshletFrustumCulling,
                    meshletNormalConeCullingEnabled,
                    buildGpuInstanceCandidates,
                    gpuLod1DistanceRatio,
                    gpuLod2DistanceRatio);
                _lastPayloadSignatureMicroseconds = ElapsedMicroseconds(signatureStart);
                bool staticPayloadChanged = !_hasCachedPayload || !_lastStaticPayloadSignature.Equals(staticPayloadSignature);
                if (staticPayloadChanged)
                {
                    _sceneContentRevision++;
                    if (_sceneContentRevision == 0)
                        _sceneContentRevision = 1;
                }
                bool cullingPayloadChanged = staticPayloadChanged || !_lastCullingSignature.Equals(cullingSignature);
                bool cameraViewProjectionChanged = cameraDependentCpuPayload &&
                    (!_hasLastCameraDependentViewProjection || !_lastCameraDependentViewProjection.Equals(viewProjectionMatrix));
                bool transparentViewProjectionChanged =
                    !_hasLastTransparentViewProjection ||
                    !_lastTransparentViewProjection.Equals(
                        transparentViewProjectionMatrix);
                bool payloadRebuilt = false;
                if (cullingPayloadChanged)
                {
                    if (staticPayloadChanged)
                        _objectData.Clear();
                    _instanceCandidates.Clear();
                    _meshletDrawCommands.Clear();
                    _simpleNormalOpaqueMeshletDrawCommands.Clear();
                    _fullOpaqueMeshletDrawCommands.Clear();
                    _solidDepthMeshletDrawCommands.Clear();
                    _maskedDepthMeshletDrawCommands.Clear();
                    _packedMeshletDrawCommands.Clear();
                    _packedSimpleNormalOpaqueMeshletDrawCommands.Clear();
                    _packedFullOpaqueMeshletDrawCommands.Clear();
                    _packedSolidDepthMeshletDrawCommands.Clear();
                    _packedMaskedDepthMeshletDrawCommands.Clear();
                    _transparentMeshletDrawCommands.Clear();
                    _transparentMaterialRuns.Clear();
                    _directionalShadowMeshletDrawCommands.Clear();
                    _localShadowMeshletDrawCommands.Clear();
                    _directionalStaticShadowMeshletDrawCommands.Clear();
                    _directionalDynamicShadowMeshletDrawCommands.Clear();
                    _localStaticShadowMeshletDrawCommands.Clear();
                    _localDynamicShadowMeshletDrawCommands.Clear();
                    Array.Clear(_pointShadowFaceMasks, 0, _pointShadowFaceMasks.Length);
                    _directionalShadowSkinnedObjectCount = 0;
                    _localShadowSkinnedObjectCount = 0;
                    _objectDebugSnapshots.Clear();
                    _transparentSortScratch.Clear();
                    _opaqueObjectCount = 0;
                    _maskedObjectCount = 0;
                    _transparentObjectCount = 0;
                    _transparentReflectionReceiverObjectCount = 0;
                    _transparentReflectionReceiverMeshletCount = 0;
                    _thinGlassObjectCount = 0;
                    _thinGlassMeshletCount = 0;
                    _geometryDecalObjectCount = 0;
                    _geometryDecalMeshletCount = 0;
                    _blendMaterialCount = 0;
                    _maskMaterialCount = 0;
                    _geometryDecalMaterialCount = 0;
                    _transparentOverflowCount = 0;
                    _transparentSortMicroseconds = 0;
                    _staticInstanceBatchCount = 0;
                    _staticInstanceCount = 0;
                    _visibleStaticInstanceCount = 0;
                    _culledStaticInstanceCount = 0;
                    _staticBatchMeshletDrawCommandCount = 0;
                    _cpuStaticBatchBuildMicroseconds = 0;
                    _objectCandidatesCpu = 0;
                    _objectFrustumCulledCpu = 0;
                    _meshletCandidatesCpu = 0;
                    _meshletFrustumCulledCpu = 0;
                    _meshletLodSkippedCpu = 0;
                    _meshletLod0SubmittedCpu = 0;
                    _meshletLod1SubmittedCpu = 0;
                    _meshletLod2SubmittedCpu = 0;
                    _submittedMeshletCountCpu = 0;
                    _submittedMeshletTriangleSum = 0;
                    _submittedMeshletVertexSum = 0;
                    _submittedSmallMeshletsUnder16Triangles = 0;
                    _submittedSmallMeshletsUnder32Triangles = 0;
                    _normalConeEligibleOpaqueMeshletCount = 0;
                    _doubleSidedOpaqueMeshletCount = 0;
                    _instanceCandidateSimpleMeshletCapacity = 0;
                    _instanceCandidateSimpleNormalMeshletCapacity = 0;
                    _instanceCandidateFullMeshletCapacity = 0;
                    _instanceCandidateSolidDepthMeshletCapacity = 0;
                    _instanceCandidateMaskedDepthMeshletCapacity = 0;
                    _instanceCandidateDirectionalStaticShadowMeshletCapacity = 0;
                    _instanceCandidateDirectionalDynamicShadowMeshletCapacity = 0;
                    _instanceCandidateNormalConeMeshletCount = 0;
                    _instanceCandidateDoubleSidedMeshletCount = 0;

                    BuildCpuScenePayload(
                        scene,
                        camera.Position,
                        frustum,
                        directionalShadowData,
                        directionalShadowCascadeCount,
                        buildLocalShadowMeshlets,
                        selectedPointShadows,
                        rebuildObjectData: staticPayloadChanged,
                        geometryDecalsEnabled: geometryDecalsEnabled,
                        isolatedDecalMaterialIndex: isolatedDecalMaterialIndex,
                        maxTransparentMeshlets:
                            transparencySettings?.MaxTransparentMeshlets ??
                            int.MaxValue,
                        transparencyMode:
                            transparencySettings?.Mode ??
                            TransparencyMode.SortedAlphaBlend,
                        transparentPipelinePartitioningEnabled:
                            transparencySettings?.PipelinePartitioningEnabled ??
                            false,
                        useCameraDependentCpuPayload: cameraDependentCpuPayload,
                        useCpuMeshletFrustumCulling: useCpuMeshletFrustumCulling,
                        buildGpuInstanceCandidates: buildGpuInstanceCandidates,
                        retainCpuOpaqueMeshletCommands:
                            !buildGpuInstanceCandidates ||
                            captureSceneSubmissionValidationLists ||
                            CaptureCpuSnapshots,
                        gpuLod1DistanceRatio: gpuLod1DistanceRatio,
                        gpuLod2DistanceRatio: gpuLod2DistanceRatio);
                    ulong drawPacketRevision = _drawPacketSet.Revision + 1;
                    if (drawPacketRevision == 0)
                        drawPacketRevision = 1;
                    _drawPacketSet = DrawPacketSet.Create(
                        drawPacketRevision,
                        _directionalShadowMeshletDrawCommands,
                        _localShadowMeshletDrawCommands,
                        _directionalStaticShadowMeshletDrawCommands,
                        _directionalDynamicShadowMeshletDrawCommands,
                        _localStaticShadowMeshletDrawCommands,
                        _localDynamicShadowMeshletDrawCommands);
                    _lastStaticPayloadSignature = staticPayloadSignature;
                    _lastCullingSignature = cullingSignature;
                    _hasCachedPayload = true;
                    payloadRebuilt = true;
                    _lastScenePayloadRebuilt = 1;
                    _lastCameraDrivenCpuDrawListRebuilt = !staticPayloadChanged && cameraViewProjectionChanged ? 1 : 0;
                }
                bool transparentPayloadRebuilt = cullingPayloadChanged ||
                    (_hasCameraDependentTransparentContent &&
                     transparentViewProjectionChanged);
                if (transparentPayloadRebuilt)
                {
                    _hasCameraDependentTransparentContent =
                        BuildTransparentScenePayload(
                            scene,
                            camera.Position,
                            transparentFrustum,
                            geometryDecalsEnabled,
                            isolatedDecalMaterialIndex,
                            transparencySettings?.MaxTransparentMeshlets ??
                            int.MaxValue,
                            transparencySettings?.Mode ??
                            TransparencyMode.SortedAlphaBlend,
                            transparencySettings?.PipelinePartitioningEnabled ??
                            false,
                            gpuLod1DistanceRatio,
                            gpuLod2DistanceRatio);
                    _lastTransparentViewProjection =
                        transparentViewProjectionMatrix;
                    _hasLastTransparentViewProjection = true;
                    if (!cullingPayloadChanged)
                    {
                        Array.Clear(
                            _transparentMeshletDrawUploadStates,
                            0,
                            _transparentMeshletDrawUploadStates.Length);
                        _lastCameraDrivenCpuDrawListRebuilt = 1;
                    }
                }
                if (cameraDependentCpuPayload)
                {
                    _lastCameraDependentViewProjection = viewProjectionMatrix;
                    _hasLastCameraDependentViewProjection = true;
                }
                else
                {
                    _hasLastCameraDependentViewProjection = false;
                }

                long materialUploadStart = Stopwatch.GetTimestamp();
                _materialManager.UploadMaterials(uploadCommandBuffer);
                long materialUploadMicroseconds = ElapsedMicroseconds(materialUploadStart);

                uint tileCountX = Math.Max(1u, DivideRoundUp(screenWidth, TileSize));
                uint tileCountY = Math.Max(1u, DivideRoundUp(screenHeight, TileSize));
                uint clusterCountZ = ClusterDepthSliceCount;
                uint totalClusters =
                    RenderingConstants.CalculateForwardClusterCount(
                        tileCountX,
                        tileCountY);
                _lastTileCountX = tileCountX;
                _lastTileCountY = tileCountY;
                _lastClusterCountZ = clusterCountZ;

                long uploadStart = Stopwatch.GetTimestamp();
                if (EnsureCapacity(ref _objectDataBuffer, CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                    _objectUploadState = default;
                if (EnsureCapacity(ref _instanceBuffers[frameIndex], CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                {
                    _instanceUploadStates[frameIndex] = default;
                    _instanceUploadDirtyFrames[frameIndex] = true;
                }
                foreach (SceneBufferStream<GPUMeshletDrawCommand> stream in _meshletDrawStreams)
                    stream.EnsureCapacity(this, frameIndex, uploadCommandBuffer);
                foreach (SceneBufferStream<GPUPackedMeshletDrawCommand> stream in _packedMeshletDrawStreams)
                    stream.EnsureCapacity(this, frameIndex, uploadCommandBuffer);
                _instanceCandidateStream.EnsureCapacity(
                    this,
                    frameIndex,
                    uploadCommandBuffer);
                EnsureCapacity(ref _meshletTaskFrameDataBuffers[frameIndex], 1, MeshletTaskFrameDataStride, uploadCommandBuffer);
                if (useTiledLightCulling)
                    EnsureTiledLightBuffers(totalClusters, uploadCommandBuffer);
                else
                    ReleaseTiledLightBuffers();

                if (payloadRebuilt)
                    InvalidateDrawStreamUploadStates();
                if (staticPayloadChanged)
                    MarkInstanceUploadFramesDirty();

                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _objectDataBuffer, ref _objectUploadState, staticPayloadChanged, uploadCommandBuffer, SceneUploadCategory.Object);
                bool instanceContentChanged = staticPayloadChanged || _instanceUploadDirtyFrames[frameIndex];
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _instanceBuffers[frameIndex], ref _instanceUploadStates[frameIndex], instanceContentChanged, uploadCommandBuffer, SceneUploadCategory.Instance);
                _instanceUploadDirtyFrames[frameIndex] = false;
                foreach (SceneBufferStream<GPUMeshletDrawCommand> stream in _meshletDrawStreams)
                    stream.UploadIfNeeded(this, frameIndex, payloadRebuilt, uploadCommandBuffer);
                foreach (SceneBufferStream<GPUPackedMeshletDrawCommand> stream in _packedMeshletDrawStreams)
                    stream.UploadIfNeeded(this, frameIndex, payloadRebuilt, uploadCommandBuffer);
                _instanceCandidateStream.UploadIfNeeded(
                    this,
                    frameIndex,
                    payloadRebuilt,
                    uploadCommandBuffer);
                _meshletTaskFrameData.Clear();
                _meshletTaskFrameData.Add(CreateMeshletTaskFrameData(
                    frustum,
                    viewProjectionMatrix,
                    inverseViewMatrix,
                    previousHiZViewProjectionMatrix,
                    previousHiZInverseViewMatrix,
                    previousHiZFrameValid,
                    screenWidth,
                    screenHeight));
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_meshletTaskFrameData), _meshletTaskFrameDataBuffers[frameIndex], ref _meshletTaskFrameDataUploadStates[frameIndex], contentChanged: true, uploadCommandBuffer, SceneUploadCategory.MeshletTaskFrameData);
                _previousHiZViewProjectionMatrix = viewProjectionMatrix;
                _previousHiZInverseViewMatrix = inverseViewMatrix;
                _hasPreviousHiZFrameData = true;

                if (useTiledLightCulling)
                    ClearTiledLightBuffers(uploadCommandBuffer, totalClusters);

                RecordUploadReadBarriers(uploadCommandBuffer, frameIndex, useTiledLightCulling);
                UpdateRegisteredBindlessBuffers();
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
                _lastBuildMicroseconds = ElapsedMicroseconds(buildStart);
                MeshletQualityStats globalMeshletStats = _meshManager.GetMeshletQualityStats();
                float avgSubmittedTriangles = _submittedMeshletCountCpu == 0
                    ? 0f
                    : (float)_submittedMeshletTriangleSum / _submittedMeshletCountCpu;
                float avgSubmittedVertices = _submittedMeshletCountCpu == 0
                    ? 0f
                    : (float)_submittedMeshletVertexSum / _submittedMeshletCountCpu;
                ulong materialUploadBytes = _materialManager.LastUploadBytes;
                ulong forwardMaterialUploadBytes =
                    _materialManager.LastForwardUploadBytes;
                ulong materialExtensionUploadBytes = _materialManager.LastExtensionUploadBytes;
                ulong uploadedBytes = _lastUploadedBytes + materialUploadBytes +
                    forwardMaterialUploadBytes + materialExtensionUploadBytes;
                AnimationSceneStats animationStats = CountAnimationSceneStats(scene);
                bool instanceDrivenSubmission = buildGpuInstanceCandidates &&
                    _instanceCandidates.Count > 0;
                int simpleOpaqueMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateSimpleMeshletCapacity
                    : _meshletDrawCommands.Count;
                int simpleNormalOpaqueMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateSimpleNormalMeshletCapacity
                    : _simpleNormalOpaqueMeshletDrawCommands.Count;
                int fullOpaqueMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateFullMeshletCapacity
                    : _fullOpaqueMeshletDrawCommands.Count;
                int opaqueMeshletCount = checked(
                    simpleOpaqueMeshletCount +
                    simpleNormalOpaqueMeshletCount +
                    fullOpaqueMeshletCount);
                int solidDepthMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateSolidDepthMeshletCapacity
                    : _solidDepthMeshletDrawCommands.Count;
                int maskedDepthMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateMaskedDepthMeshletCapacity
                    : _maskedDepthMeshletDrawCommands.Count;
                int directionalStaticShadowMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateDirectionalStaticShadowMeshletCapacity
                    : _drawPacketSet.DirectionalStaticShadowCommands.Length;
                int directionalDynamicShadowMeshletCount = instanceDrivenSubmission
                    ? _instanceCandidateDirectionalDynamicShadowMeshletCapacity
                    : _drawPacketSet.DirectionalDynamicShadowCommands.Length;
                int directionalShadowMeshletCount = checked(
                    directionalStaticShadowMeshletCount +
                    directionalDynamicShadowMeshletCount);

                var sceneData = new SceneRenderingData
                {
                    ObjectCount = _objectData.Count,
                    MeshletCount = checked(
                        opaqueMeshletCount +
                        _transparentMeshletDrawCommands.Count),
                    SceneInstanceCandidateCount = _instanceCandidates.Count,
                    SceneSubmissionGpuInstanceExpansionEnabled =
                        buildGpuInstanceCandidates &&
                        _instanceCandidates.Count > 0,
                    StaticInstanceBatchCount = _staticInstanceBatchCount,
                    StaticInstanceCount = _staticInstanceCount,
                    VisibleStaticInstanceCount = _visibleStaticInstanceCount,
                    CulledStaticInstanceCount = _culledStaticInstanceCount,
                    StaticBatchMeshletDrawCommandCount = _staticBatchMeshletDrawCommandCount,
                    CpuStaticBatchBuildMicroseconds = _cpuStaticBatchBuildMicroseconds,
                    FoliagePatchCount = scene.FoliagePatches.Count,
                    FoliagePrototypeCount = CountFoliagePrototypes(scene),
                    OpaqueObjectCount = _opaqueObjectCount,
                    MaskedObjectCount = _maskedObjectCount,
                    TransparentObjectCount = _transparentObjectCount,
                    TransparentReflectionReceiverObjectCount =
                        _transparentReflectionReceiverObjectCount,
                    ThinGlassObjectCount = _thinGlassObjectCount,
                    SolidObjectCount = _opaqueObjectCount,
                    GeometryDecalObjectCount = _geometryDecalObjectCount,
                    OpaqueMeshletCount = opaqueMeshletCount,
                    SimpleOpaqueMeshletCount = simpleOpaqueMeshletCount,
                    SimpleNormalOpaqueMeshletCount =
                        simpleNormalOpaqueMeshletCount,
                    FullOpaqueMeshletCount = fullOpaqueMeshletCount,
                    SolidMeshletCount = solidDepthMeshletCount,
                    MaskedMeshletCount = maskedDepthMeshletCount,
                    TransparentMeshletCount = _transparentMeshletDrawCommands.Count,
                    TransparentReflectionReceiverMeshletCount =
                        _transparentReflectionReceiverMeshletCount,
                    ThinGlassMeshletCount = _thinGlassMeshletCount,
                    GeometryDecalMeshletCount = _geometryDecalMeshletCount,
                    BlendMaterialCount = _blendMaterialCount,
                    MaskMaterialCount = _maskMaterialCount,
                    GeometryDecalMaterialCount = _geometryDecalMaterialCount,
                    TransparentSortCandidateCount = _transparentSortScratch.Count,
                    TransparentSortMicroseconds = _transparentSortMicroseconds,
                    TransparentOverflowCount = _transparentOverflowCount,
                    MaterialCount = _materialManager.RegisteredMaterialCount,
                    GiTransportMaterialRevision = _materialManager.GiTransportInputRevision,
                    LightCount = 0,
                    TextureCount = _textureManager?.TextureCount ?? 0,
                    AnimationEnabled = animationStats.SkinnedObjectCount > 0,
                    AnimationSkinningMode = animationStats.SkinnedObjectCount > 0 ? AnimationSkinningMode.GpuCompute : AnimationSkinningMode.Disabled,
                    AnimatedModelCount = animationStats.AnimatedModelCount,
                    SkinnedObjectCount = animationStats.SkinnedObjectCount,
                    SkeletonCount = animationStats.SkeletonCount,
                    SkinCount = animationStats.SkinCount,
                    AnimationClipCount = animationStats.AnimationClipCount,
                    ActiveAnimatorCount = animationStats.ActiveAnimatorCount,
                    PlayingAnimatorCount = animationStats.PlayingAnimatorCount,
                    PausedAnimatorCount = animationStats.PausedAnimatorCount,
                    JointMatrixCount = animationStats.JointMatrixCount,
                    AnimatedBoundsMode = animationStats.SkinnedObjectCount > 0 ? "Conservative" : string.Empty,
                    CurrentFrameIndex = (uint)frameIndex,
                    SceneContentRevision = _sceneContentRevision,
                    ViewMatrix = viewMatrix,
                    ProjectionMatrix = projectionMatrix,
                    ViewProjectionMatrix = viewProjectionMatrix,
                    InverseViewMatrix = inverseViewMatrix,
                    InverseProjectionMatrix = inverseProjectionMatrix,
                    InverseViewProjectionMatrix = inverseViewProjectionMatrix,
                    CameraPosition = camera.Position,
                    ScreenWidth = screenWidth,
                    ScreenHeight = screenHeight,
                    TileCountX = tileCountX,
                    TileCountY = tileCountY,
                    ClusterCountZ = clusterCountZ,
                    MaxLightsPerTile = MaxLightsPerTile,
                    UploadedBytes = uploadedBytes,
                    CpuSceneBuildMicroseconds = _lastBuildMicroseconds,
                    CpuPayloadSignatureMicroseconds = _lastPayloadSignatureMicroseconds,
                    CpuObjectCullMicroseconds = _lastObjectCullMicroseconds,
                    CpuMeshletCullMicroseconds = _lastMeshletCullMicroseconds,
                    CpuUploadMicroseconds = _lastUploadMicroseconds,
                    CpuMaterialUploadMicroseconds = materialUploadMicroseconds,
                    SceneUploadCount = _lastSceneUploadCount,
                    SceneUploadSkipped = _lastSceneUploadSkipped,
                    ObjectCandidatesCpu = _objectCandidatesCpu,
                    ObjectFrustumCulledCpu = _objectFrustumCulledCpu,
                    MeshletCandidatesCpu = _meshletCandidatesCpu,
                    MeshletFrustumCulledCpu = _meshletFrustumCulledCpu,
                    MeshletLodSkippedCpu = _meshletLodSkippedCpu,
                    MeshletLod0SubmittedCpu = _meshletLod0SubmittedCpu,
                    MeshletLod1SubmittedCpu = _meshletLod1SubmittedCpu,
                    MeshletLod2SubmittedCpu = _meshletLod2SubmittedCpu,
                    NormalConeEligibleOpaqueMeshletCount =
                        instanceDrivenSubmission
                            ? _instanceCandidateNormalConeMeshletCount
                            : _normalConeEligibleOpaqueMeshletCount,
                    DoubleSidedOpaqueMeshletCount =
                        instanceDrivenSubmission
                            ? _instanceCandidateDoubleSidedMeshletCount
                            : _doubleSidedOpaqueMeshletCount,
                    MeshletNormalConeCullingEnabled =
                        meshletNormalConeCullingEnabled,
                    StableSceneInputUploadBytes = _lastObjectUploadBytes +
                        _lastInstanceUploadBytes +
                        _lastInstanceCandidateUploadBytes +
                        materialUploadBytes +
                        forwardMaterialUploadBytes + materialExtensionUploadBytes,
                    CpuCandidateListUploadBytes = _lastMeshletDrawUploadBytes +
                        _lastSolidDepthMeshletDrawUploadBytes +
                        _lastMaskedDepthMeshletDrawUploadBytes +
                        _lastPackedMeshletDrawUploadBytes +
                        _lastPackedSolidDepthMeshletDrawUploadBytes +
                        _lastPackedMaskedDepthMeshletDrawUploadBytes +
                        _lastTransparentMeshletDrawUploadBytes,
                    CameraDrivenCpuDrawListRebuilt = _lastCameraDrivenCpuDrawListRebuilt,
                    MeshletCountTotal = globalMeshletStats.MeshletCount,
                    MeshletCountSubmittedCpu = _submittedMeshletCountCpu,
                    AvgTrianglesPerSubmittedMeshlet = avgSubmittedTriangles,
                    AvgVerticesPerSubmittedMeshlet = avgSubmittedVertices,
                    SmallMeshletsUnder16Triangles = _submittedSmallMeshletsUnder16Triangles,
                    SmallMeshletsUnder32Triangles = _submittedSmallMeshletsUnder32Triangles,
                    ScenePayloadRebuilt = _lastScenePayloadRebuilt,
                    ObjectUploadBytes = _lastObjectUploadBytes,
                    InstanceUploadBytes = _lastInstanceUploadBytes,
                    SceneInstanceCandidateUploadBytes =
                        _lastInstanceCandidateUploadBytes,
                    MeshletDrawUploadBytes = _lastMeshletDrawUploadBytes,
                    SolidDepthMeshletDrawUploadBytes = _lastSolidDepthMeshletDrawUploadBytes,
                    MaskedDepthMeshletDrawUploadBytes = _lastMaskedDepthMeshletDrawUploadBytes,
                    PackedMeshletDrawUploadBytes = _lastPackedMeshletDrawUploadBytes,
                    PackedSolidDepthMeshletDrawUploadBytes = _lastPackedSolidDepthMeshletDrawUploadBytes,
                    PackedMaskedDepthMeshletDrawUploadBytes = _lastPackedMaskedDepthMeshletDrawUploadBytes,
                    TransparentMeshletDrawUploadBytes = _lastTransparentMeshletDrawUploadBytes,
                    MaterialUploadBytes = materialUploadBytes,
                    ForwardMaterialUploadBytes = forwardMaterialUploadBytes,
                    MaterialExtensionUploadBytes = materialExtensionUploadBytes,
                    ObjectBufferSize = _objectDataBuffer.ByteSize,
                    MaterialBufferSize = _materialManager.MaterialBufferSize,
                    ForwardMaterialBufferSize =
                        _materialManager.ForwardMaterialBufferSize,
                    MaterialExtensionBufferSize = _materialManager.MaterialExtensionBufferSize,
                    InstanceBufferSize = _instanceBuffers[frameIndex].ByteSize,
                    SceneInstanceCandidateBufferSize =
                        _instanceCandidateBuffers[frameIndex].ByteSize,
                    MeshletDrawBufferSize = _meshletDrawBuffers[frameIndex].ByteSize,
                    SimpleNormalOpaqueMeshletDrawBufferSize = _simpleNormalOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    FullOpaqueMeshletDrawBufferSize = _fullOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    SolidDepthMeshletDrawBufferSize = _solidDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    MaskedDepthMeshletDrawBufferSize = _maskedDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedMeshletDrawBufferSize = _packedMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedSimpleNormalOpaqueMeshletDrawBufferSize = _packedSimpleNormalOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedFullOpaqueMeshletDrawBufferSize = _packedFullOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedSolidDepthMeshletDrawBufferSize = _packedSolidDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedMaskedDepthMeshletDrawBufferSize = _packedMaskedDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    MeshletTaskFrameDataBufferSize = _meshletTaskFrameDataBuffers[frameIndex].ByteSize,
                    TransparentMeshletDrawBufferSize = _transparentMeshletDrawBuffers[frameIndex].ByteSize,
                    DirectionalShadowMeshletDrawBufferSize = _directionalShadowMeshletDrawBuffers[frameIndex].ByteSize,
                    LocalShadowMeshletDrawBufferSize = _localShadowMeshletDrawBuffers[frameIndex].ByteSize,
                    TiledLightHeaderBufferSize = _tiledLightHeaderBuffer.ByteSize,
                    TiledLightIndexBufferSize = _tiledLightIndexBuffer.ByteSize,
                    TiledLightHeaderBufferClearBytes = _lastTiledLightHeaderBufferClearBytes,
                    TiledLightIndexBufferClearBytes = _lastTiledLightIndexBufferClearBytes,
                    ObjectDataBuffer = _objectDataBuffer.Handle,
                    MaterialDataBuffer = _materialManager.MaterialBuffer,
                    ForwardMaterialDataBuffer =
                        _materialManager.ForwardMaterialBuffer,
                    MaterialExtensionDataBuffer = _materialManager.MaterialExtensionBuffer,
                    InstanceBuffer = _instanceBuffers[frameIndex].Handle,
                    SceneInstanceCandidateBuffer =
                        _instanceCandidateBuffers[frameIndex].Handle,
                    MeshletDrawBuffer = _meshletDrawBuffers[frameIndex].Handle,
                    SimpleNormalOpaqueMeshletDrawBuffer = _simpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    FullOpaqueMeshletDrawBuffer = _fullOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    SolidDepthMeshletDrawBuffer = _solidDepthMeshletDrawBuffers[frameIndex].Handle,
                    MaskedDepthMeshletDrawBuffer = _maskedDepthMeshletDrawBuffers[frameIndex].Handle,
                    PackedMeshletDrawBuffer = _packedMeshletDrawBuffers[frameIndex].Handle,
                    PackedSimpleNormalOpaqueMeshletDrawBuffer = _packedSimpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    PackedFullOpaqueMeshletDrawBuffer = _packedFullOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    PackedSolidDepthMeshletDrawBuffer = _packedSolidDepthMeshletDrawBuffers[frameIndex].Handle,
                    PackedMaskedDepthMeshletDrawBuffer = _packedMaskedDepthMeshletDrawBuffers[frameIndex].Handle,
                    MeshletTaskFrameDataBuffer = _meshletTaskFrameDataBuffers[frameIndex].Handle,
                    TransparentMeshletDrawBuffer = _transparentMeshletDrawBuffers[frameIndex].Handle,
                    TiledLightHeaderBuffer = _tiledLightHeaderBuffer.Handle,
                    TiledLightIndexBuffer = _tiledLightIndexBuffer.Handle
                };

                sceneData.TransparentMaterialRuns.AddRange(
                    _transparentMaterialRuns);

                if (CaptureCpuSnapshots || captureSceneSubmissionValidationLists)
                {
                    sceneData.HasCpuSnapshots = CaptureCpuSnapshots;
                    sceneData.ObjectData.AddRange(_objectData);
                    sceneData.SceneInstanceCandidates.AddRange(
                        _instanceCandidates);
                    sceneData.MeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.MeshletDrawCommands.AddRange(_simpleNormalOpaqueMeshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_simpleNormalOpaqueMeshletDrawCommands);
                    sceneData.SimpleNormalOpaqueMeshletDrawCommands.AddRange(_simpleNormalOpaqueMeshletDrawCommands);
                    sceneData.MeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    sceneData.FullOpaqueMeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    if (CaptureCpuSnapshots)
                    {
                        sceneData.MaterialData.AddRange(_materialManager.GetMaterialDataSnapshot());
                        sceneData.MaterialExtensionData.AddRange(_materialManager.GetMaterialExtensionDataSnapshot());
                        sceneData.ObjectDebugSnapshots.AddRange(_objectDebugSnapshots);
                        sceneData.SolidDepthMeshletDrawCommands.AddRange(_solidDepthMeshletDrawCommands);
                        sceneData.MaskedDepthMeshletDrawCommands.AddRange(_maskedDepthMeshletDrawCommands);
                        sceneData.PackedMeshletDrawCommands.AddRange(_packedMeshletDrawCommands);
                        sceneData.PackedSimpleNormalOpaqueMeshletDrawCommands.AddRange(_packedSimpleNormalOpaqueMeshletDrawCommands);
                        sceneData.PackedFullOpaqueMeshletDrawCommands.AddRange(_packedFullOpaqueMeshletDrawCommands);
                        sceneData.PackedSolidDepthMeshletDrawCommands.AddRange(_packedSolidDepthMeshletDrawCommands);
                        sceneData.PackedMaskedDepthMeshletDrawCommands.AddRange(_packedMaskedDepthMeshletDrawCommands);
                        sceneData.TransparentMeshletDrawCommands.AddRange(_transparentMeshletDrawCommands);
                    }
                }

                for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
                    sceneData.DirectionalShadowMeshletCounts[cascade] = directionalShadowMeshletCount;
                sceneData.LocalShadowMeshletCount = _drawPacketSet.LocalShadowCommands.Length;
                sceneData.DirectionalStaticShadowMeshletCount = directionalStaticShadowMeshletCount;
                sceneData.DirectionalDynamicShadowMeshletCount = directionalDynamicShadowMeshletCount;
                sceneData.LocalStaticShadowMeshletCount = _drawPacketSet.LocalStaticShadowCommands.Length;
                sceneData.LocalDynamicShadowMeshletCount = _drawPacketSet.LocalDynamicShadowCommands.Length;
                sceneData.DirectionalShadowSkinnedObjectCount = _directionalShadowSkinnedObjectCount;
                sceneData.LocalShadowSkinnedObjectCount = _localShadowSkinnedObjectCount;
                sceneData.DrawPacketRevision = _drawPacketSet.Revision;
                sceneData.DirectionalShadowMeshletDrawSignature = _drawPacketSet.DirectionalShadowSignature;
                sceneData.LocalShadowMeshletDrawSignature = _drawPacketSet.LocalShadowSignature;
                sceneData.DirectionalStaticShadowMeshletDrawSignature = _drawPacketSet.DirectionalStaticShadowSignature;
                sceneData.DirectionalDynamicShadowMeshletDrawSignature = _drawPacketSet.DirectionalDynamicShadowSignature;
                sceneData.LocalStaticShadowMeshletDrawSignature = _drawPacketSet.LocalStaticShadowSignature;
                sceneData.LocalDynamicShadowMeshletDrawSignature = _drawPacketSet.LocalDynamicShadowSignature;
                sceneData.PointShadowFaceMasks = CopyPointShadowFaceMasks(selectedPointShadows.Length);

                if (AdvancePreviousWorldMatrices())
                    MarkInstanceUploadFramesDirty();
                return sceneData;
            }
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffers();
            }
        }

        private Matrix4x4 GetPreviousWorldMatrix(RenderObject renderObject, Matrix4x4 currentWorldMatrix)
        {
            return _previousRenderObjectMatrices.TryGetValue(renderObject, out Matrix4x4 previousWorldMatrix)
                ? previousWorldMatrix
                : currentWorldMatrix;
        }

        private Matrix4x4 GetPreviousWorldMatrix(StaticInstanceBatch batch, int instanceIndex, Matrix4x4 currentWorldMatrix)
        {
            return _previousStaticInstanceMatrices.TryGetValue(new StaticInstanceKey(batch, instanceIndex), out Matrix4x4 previousWorldMatrix)
                ? previousWorldMatrix
                : currentWorldMatrix;
        }

        private bool AdvancePreviousWorldMatrices()
        {
            bool changed = false;
            for (int i = 0; i < _objectData.Count; i++)
            {
                GPUObjectData objectData = _objectData[i];
                if (!objectData.PreviousWorldMatrix.Equals(objectData.WorldMatrix))
                {
                    objectData.PreviousWorldMatrix = objectData.WorldMatrix;
                    _objectData[i] = objectData;
                    changed = true;
                }
            }

            return changed;
        }

        private void BuildCpuScenePayload(Scene scene,
            Vector3 cameraPosition,
            Frustum frustum,
            GPUShadowData? directionalShadowData,
            int directionalShadowCascadeCount,
            bool buildLocalShadowMeshlets,
            ReadOnlySpan<SelectedLocalShadow> selectedPointShadows,
            bool rebuildObjectData,
            bool geometryDecalsEnabled,
            int isolatedDecalMaterialIndex,
            int maxTransparentMeshlets,
            TransparencyMode transparencyMode, bool transparentPipelinePartitioningEnabled,
            bool useCameraDependentCpuPayload,
            bool useCpuMeshletFrustumCulling,
            bool buildGpuInstanceCandidates,
            bool retainCpuOpaqueMeshletCommands,
            float gpuLod1DistanceRatio,
            float gpuLod2DistanceRatio)
        {
            var shadowFrusta = new Frustum[ShadowSettings.MaxDirectionalCascades];
            if (useCameraDependentCpuPayload && directionalShadowData.HasValue && directionalShadowCascadeCount > 0)
            {
                GPUShadowData shadowData = directionalShadowData.GetValueOrDefault();
                for (int cascade = 0; cascade < directionalShadowCascadeCount; cascade++)
                    shadowFrusta[cascade] = ExtractFrustum(GetShadowCascadeMatrix(shadowData, cascade));
            }

            int objectDataIndex = 0;
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                long objectStart = Stopwatch.GetTimestamp();
                _objectCandidatesCpu++;

                if (!renderObject.Visible)
                {
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                    continue;
                }

                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                {
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                    continue;
                }

                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                Matrix4x4 cullingMatrix = GetCullingMatrix(renderObject);
                bool objectFullyInsideFrustum = false;
                bool cameraVisible = !useCameraDependentCpuPayload ||
                                     IsVisible(meshInfo, cullingMatrix, frustum, out objectFullyInsideFrustum);
                if (!cameraVisible)
                    _objectFrustumCulledCpu++;
                if (!useCameraDependentCpuPayload)
                    objectFullyInsideFrustum = false;

                MaterialHandle materialHandle = ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name ?? string.Empty);
                int materialIndex = _materialManager.ResolveMaterialIndex(materialHandle);
                GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                MaterialRenderMode renderMode = metadata.RenderMode;
                MaterialForwardClass forwardClass = MaterialForwardClassifier.Classify(material, metadata);
                bool isGeometryDecal = metadata.IsGeometryDecal;
                bool deferredTransparentPayload =
                    renderMode == MaterialRenderMode.Blend ||
                    isGeometryDecal;
                uint nearFieldStableObjectId =
                    AccelerationStructureManager.StableInstanceIdentity(
                        renderObject.Id);
                uint nearFieldStableMaterialId =
                    CreateNearFieldStableMaterialIdentity(materialHandle);
                uint nearFieldPackedRevisions = PackNearFieldRevisions(
                    renderObject.Revision,
                    _materialManager.GetMaterialContentRevision(materialIndex));
                uint nearFieldFlags = CreateNearFieldSurfaceFlags(
                    renderMode,
                    isGeometryDecal);
                if (CaptureCpuSnapshots)
                {
                    var localBounds = new BoundingBox(
                        ToCoreVector(meshInfo.BoundingBoxMin),
                        ToCoreVector(meshInfo.BoundingBoxMax));
                    _objectDebugSnapshots.Add(new ObjectDebugSnapshot(
                        _objectDebugSnapshots.Count,
                        renderObject.Id,
                        renderObject.Name ?? string.Empty,
                        meshHandle,
                        materialHandle,
                        renderObject.WorldMatrix,
                        TransformBoundingBox(localBounds, cullingMatrix),
                        renderObject.Visible && cameraVisible,
                        !renderObject.Visible || !cameraVisible));
                }

                if (cameraVisible && !deferredTransparentPayload)
                {
                    if (isGeometryDecal)
                    {
                        _geometryDecalObjectCount++;
                    }
                    else
                    {
                        switch (renderMode)
                        {
                            case MaterialRenderMode.Blend:
                                _transparentObjectCount++;
                                if (ReceivesSceneReflections(metadata))
                                {
                                    _transparentReflectionReceiverObjectCount++;
                                }
                                if (forwardClass == MaterialForwardClass.ThinGlass)
                                    _thinGlassObjectCount++;
                                break;
                            case MaterialRenderMode.Mask:
                                _maskedObjectCount++;
                                break;
                            default:
                                _opaqueObjectCount++;
                                break;
                        }
                    }
                }

                Matrix4x4 worldInverseTranspose;
                uint instanceId;
                if (rebuildObjectData)
                {
                    try
                    {
                        worldInverseTranspose = renderObject.WorldMatrix.Invert().Transpose();
                    }
                    catch (InvalidOperationException ex)
                    {
                        throw new InvalidOperationException(
                            $"Render object '{renderObject.Name}' has a non-invertible world matrix and cannot be uploaded.",
                            ex);
                    }

                    _objectData.Add(new GPUObjectData
                    {
                        WorldMatrix = renderObject.WorldMatrix,
                        WorldMatrixInverseTranspose = worldInverseTranspose,
                        MeshIndex = meshHandle.Index,
                        MaterialIndex = materialIndex,
                        SkinnedVertexOffset = renderObject is SkinnedRenderObject skinned && skinned.SkinningEnabled
                            ? checked((int)skinned.SkinnedVertexOffset)
                            : 0,
                        SkinningEnabled = renderObject is SkinnedRenderObject enabledSkinned && enabledSkinned.SkinningEnabled ? 1 : 0,
                        PreviousWorldMatrix = GetPreviousWorldMatrix(renderObject, renderObject.WorldMatrix),
                        NearFieldStableObjectId = nearFieldStableObjectId,
                        NearFieldStableMaterialId = nearFieldStableMaterialId,
                        NearFieldPackedObjectMaterialRevisions =
                            nearFieldPackedRevisions,
                        NearFieldCoverageMotionFlags = nearFieldFlags
                    });
                    instanceId = (uint)(_objectData.Count - 1);
                }
                else
                {
                    if (objectDataIndex >= _objectData.Count)
                        throw new InvalidOperationException("Cached scene object payload is out of sync with the scene signature.");
                    instanceId = (uint)objectDataIndex;
                    GPUObjectData objectData = _objectData[objectDataIndex];
                    objectData.PreviousWorldMatrix = GetPreviousWorldMatrix(renderObject, objectData.WorldMatrix);
                    objectData.NearFieldStableObjectId = nearFieldStableObjectId;
                    objectData.NearFieldStableMaterialId = nearFieldStableMaterialId;
                    objectData.NearFieldPackedObjectMaterialRevisions =
                        nearFieldPackedRevisions;
                    objectData.NearFieldCoverageMotionFlags = nearFieldFlags;
                    _objectData[objectDataIndex] = objectData;
                }
                _previousRenderObjectMatrices[renderObject] = renderObject.WorldMatrix;

                objectDataIndex++;
                if (deferredTransparentPayload)
                {
                    _lastObjectCullMicroseconds +=
                        ElapsedMicroseconds(objectStart);
                    continue;
                }
                Vector3 localCenter = (ToCoreVector(meshInfo.BoundingBoxMin) + ToCoreVector(meshInfo.BoundingBoxMax)) * 0.5f;
                Vector3 worldCenter = TransformPoint(localCenter, cullingMatrix);
                float localRadius = Distance(ToCoreVector(meshInfo.BoundingBoxMin), localCenter);
                float worldRadius = localRadius * GetMaxScale(cullingMatrix);
                float transparentDistanceSquared = 0f;
                if (useCameraDependentCpuPayload && (renderMode == MaterialRenderMode.Blend || isGeometryDecal))
                    transparentDistanceSquared = DistanceSquared(cameraPosition, worldCenter);

                _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);
                long meshletStart = Stopwatch.GetTimestamp();
                int previousLodLevel = _previousRenderObjectLods.TryGetValue(renderObject, out int storedLodLevel)
                    ? storedLodLevel
                    : -1;
                int lodLevel = useCameraDependentCpuPayload
                    ? SelectMeshletLodLevelNormalized(
                        cameraPosition,
                        worldCenter,
                        worldRadius,
                        previousLodLevel,
                        gpuLod1DistanceRatio,
                        gpuLod2DistanceRatio)
                    : 0;
                _previousRenderObjectLods[renderObject] = lodLevel;
                MeshletLodRange meshletRange = GetMeshletLodRange(meshInfo, lodLevel, out int effectiveLodLevel);
                if (cameraVisible && meshInfo.MeshletCount > meshletRange.Count)
                    _meshletLodSkippedCpu += checked((int)(meshInfo.MeshletCount - meshletRange.Count));
                bool castsDirectionalShadow = directionalShadowData.HasValue &&
                                              directionalShadowCascadeCount > 0 &&
                                              renderMode != MaterialRenderMode.Blend &&
                                              !isGeometryDecal;
                bool castsLocalShadow = buildLocalShadowMeshlets &&
                                        renderMode != MaterialRenderMode.Blend &&
                                        !isGeometryDecal;
                bool objectIntersectsShadowCascade = castsDirectionalShadow && !useCameraDependentCpuPayload;
                bool isSkinnedObject = renderObject is SkinnedRenderObject { SkinningEnabled: true };
                uint meshletCommandFlags = CreateMeshletCommandFlags(
                    metadata,
                    cullingMatrix,
                    isSkinnedObject,
                    renderMode,
                    isGeometryDecal);
                if (buildGpuInstanceCandidates &&
                    renderMode != MaterialRenderMode.Blend &&
                    !isGeometryDecal)
                {
                    AddInstanceCandidate(
                        instanceId,
                        checked((uint)materialIndex),
                        meshletCommandFlags,
                        forwardClass,
                        meshInfo,
                        renderMode,
                        castsDirectionalShadow,
                        isSkinnedObject);
                }
                if (castsDirectionalShadow && useCameraDependentCpuPayload)
                {
                    for (int cascade = 0; cascade < directionalShadowCascadeCount; cascade++)
                    {
                        if (IsVisible(meshInfo, cullingMatrix, shadowFrusta[cascade], out _))
                        {
                            objectIntersectsShadowCascade = true;
                            break;
                        }
                    }
                }
                if (isSkinnedObject && castsDirectionalShadow && objectIntersectsShadowCascade)
                    _directionalShadowSkinnedObjectCount++;
                if (isSkinnedObject && castsLocalShadow)
                    _localShadowSkinnedObjectCount++;

                for (uint i = 0; i < meshletRange.Count; i++)
                {
                    if (cameraVisible)
                        _meshletCandidatesCpu++;
                    uint meshletIndex = meshletRange.Offset + i;
                    bool meshletVisibleToCamera = cameraVisible;
                    if (cameraVisible &&
                        useCpuMeshletFrustumCulling &&
                        !objectFullyInsideFrustum &&
                        meshletRange.Count >= CpuMeshletCullingThreshold &&
                        !MeshletIntersectsFrustum(meshletIndex, cullingMatrix, frustum))
                    {
                        _meshletFrustumCulledCpu++;
                        meshletVisibleToCamera = false;
                    }

                    Meshlet meshlet = _meshManager.GetMeshlet(meshletIndex);
                    var command = new GPUMeshletDrawCommand
                    {
                        MeshletIndex = meshletIndex,
                        InstanceId = instanceId,
                        MaterialIndex = (uint)materialIndex,
                        Flags = meshletCommandFlags
                    };

                    if (meshletVisibleToCamera)
                    {
                        RecordSubmittedMeshlet(meshlet);
                        RecordSubmittedMeshletLod(effectiveLodLevel);

                        if (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                        {
                            if (isGeometryDecal)
                                _geometryDecalMeshletCount++;
                            if (!isGeometryDecal ||
                                (geometryDecalsEnabled &&
                                 (isolatedDecalMaterialIndex < 0 ||
                                  isolatedDecalMaterialIndex == materialIndex)))
                                AddTransparentDraw(
                                    command,
                                    transparentDistanceSquared,
                                    metadata.DecalLayer,
                                    forwardClass,
                                    metadata.IsGeometryDecal,
                                    metadata.TransmissionPolicy,
                                    ReceivesSceneReflections(metadata),
                                    maxTransparentMeshlets);
                        }
                        else
                        {
                            if (retainCpuOpaqueMeshletCommands)
                            {
                                GPUPackedMeshletDrawCommand packedCommand = CreatePackedMeshletDrawCommand(
                                    command,
                                    meshlet,
                                    cullingMatrix,
                                    renderMode,
                                    meshletVisibleToCamera,
                                    objectFullyInsideFrustum,
                                    useCpuMeshletFrustumCulling &&
                                    !objectFullyInsideFrustum &&
                                    meshletRange.Count >= CpuMeshletCullingThreshold);
                                AddOpaqueForwardDraw(command, packedCommand, forwardClass, meshInfo);
                                if (renderMode == MaterialRenderMode.Mask)
                                {
                                    _maskedDepthMeshletDrawCommands.Add(command);
                                    _packedMaskedDepthMeshletDrawCommands.Add(packedCommand);
                                }
                                else
                                {
                                    _solidDepthMeshletDrawCommands.Add(command);
                                    _packedSolidDepthMeshletDrawCommands.Add(packedCommand);
                                }
                            }
                        }
                    }

                    if (castsDirectionalShadow &&
                        objectIntersectsShadowCascade &&
                        !buildGpuInstanceCandidates)
                    {
                        _directionalShadowMeshletDrawCommands.Add(command);
                        if (isSkinnedObject)
                            _directionalDynamicShadowMeshletDrawCommands.Add(command);
                        else
                            _directionalStaticShadowMeshletDrawCommands.Add(command);
                    }
                    if (castsLocalShadow)
                    {
                        _localShadowMeshletDrawCommands.Add(command);
                        if (isSkinnedObject)
                            _localDynamicShadowMeshletDrawCommands.Add(command);
                        else
                            _localStaticShadowMeshletDrawCommands.Add(command);
                        AccumulatePointShadowFaceCoverage(meshlet, cullingMatrix, selectedPointShadows);
                    }
                }
                _lastMeshletCullMicroseconds += ElapsedMicroseconds(meshletStart);
            }

            long staticBatchStart = Stopwatch.GetTimestamp();
            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                _staticInstanceBatchCount++;
                _staticInstanceCount += batch.WorldMatrices.Count;
                if (!batch.Visible)
                    continue;
                if (batch.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;

                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                MaterialHandle materialHandle = ResolveRenderObjectMaterialHandle(
                    batch.Material,
                    _materialManager.DefaultMaterialHandle,
                    batch.Name);
                int materialIndex = _materialManager.ResolveMaterialIndex(materialHandle);
                GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMetadata metadata = _materialManager.GetMaterialMetadata(materialHandle);
                MaterialRenderMode renderMode = metadata.RenderMode;
                MaterialForwardClass forwardClass = MaterialForwardClassifier.Classify(material, metadata);
                bool isGeometryDecal = metadata.IsGeometryDecal;
                bool deferredTransparentPayload =
                    renderMode == MaterialRenderMode.Blend ||
                    isGeometryDecal;
                uint nearFieldStableMaterialId =
                    CreateNearFieldStableMaterialIdentity(materialHandle);
                uint nearFieldMaterialRevision =
                    _materialManager.GetMaterialContentRevision(materialIndex);
                uint nearFieldFlags = CreateNearFieldSurfaceFlags(
                    renderMode,
                    isGeometryDecal);

                Vector3 localCenter = (ToCoreVector(meshInfo.BoundingBoxMin) + ToCoreVector(meshInfo.BoundingBoxMax)) * 0.5f;
                float localRadius = Distance(ToCoreVector(meshInfo.BoundingBoxMin), localCenter);
                bool castsDirectionalShadow = directionalShadowData.HasValue &&
                                              directionalShadowCascadeCount > 0 &&
                                              renderMode != MaterialRenderMode.Blend &&
                                              !isGeometryDecal;
                bool castsLocalShadow = buildLocalShadowMeshlets &&
                                        renderMode != MaterialRenderMode.Blend &&
                                        !isGeometryDecal;

                for (int instance = 0; instance < batch.WorldMatrices.Count; instance++)
                {
                    long objectStart = Stopwatch.GetTimestamp();
                    _objectCandidatesCpu++;
                    Matrix4x4 worldMatrix = batch.WorldMatrices[instance];
                    bool objectFullyInsideFrustum = false;
                    bool cameraVisible = !useCameraDependentCpuPayload ||
                                         IsVisible(meshInfo, worldMatrix, frustum, out objectFullyInsideFrustum);
                    if (!cameraVisible)
                    {
                        _objectFrustumCulledCpu++;
                        _culledStaticInstanceCount++;
                    }
                    if (!useCameraDependentCpuPayload)
                        objectFullyInsideFrustum = false;
                    else if (!deferredTransparentPayload)
                    {
                        _visibleStaticInstanceCount++;
                        if (isGeometryDecal)
                        {
                            _geometryDecalObjectCount++;
                        }
                        else
                        {
                            switch (renderMode)
                            {
                                case MaterialRenderMode.Blend:
                                    _transparentObjectCount++;
                                    if (ReceivesSceneReflections(metadata))
                                    {
                                        _transparentReflectionReceiverObjectCount++;
                                    }
                                    if (forwardClass == MaterialForwardClass.ThinGlass)
                                        _thinGlassObjectCount++;
                                    break;
                                case MaterialRenderMode.Mask:
                                    _maskedObjectCount++;
                                    break;
                                default:
                                    _opaqueObjectCount++;
                                    break;
                            }
                        }
                    }

                    Matrix4x4 worldInverseTranspose;
                    uint instanceId;
                    if (rebuildObjectData)
                    {
                        try
                        {
                            worldInverseTranspose = worldMatrix.Invert().Transpose();
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw new InvalidOperationException(
                                $"Static instance batch '{batch.Name}' instance {instance} has a non-invertible world matrix and cannot be uploaded.",
                                ex);
                        }

                        _objectData.Add(new GPUObjectData
                        {
                            WorldMatrix = worldMatrix,
                            WorldMatrixInverseTranspose = worldInverseTranspose,
                            MeshIndex = meshHandle.Index,
                            MaterialIndex = materialIndex,
                            SkinnedVertexOffset = 0,
                            SkinningEnabled = 0,
                            PreviousWorldMatrix = GetPreviousWorldMatrix(batch, instance, worldMatrix),
                            NearFieldStableObjectId =
                                AccelerationStructureManager
                                    .StableInstanceIdentity(
                                        batch.Id,
                                        checked((uint)instance)),
                            NearFieldStableMaterialId =
                                nearFieldStableMaterialId,
                            NearFieldPackedObjectMaterialRevisions =
                                PackNearFieldRevisions(
                                    ((ulong)batch.Revision << 32) |
                                    checked((uint)instance + 1u),
                                    nearFieldMaterialRevision),
                            NearFieldCoverageMotionFlags = nearFieldFlags
                        });
                        instanceId = (uint)(_objectData.Count - 1);
                    }
                    else
                    {
                        if (objectDataIndex >= _objectData.Count)
                            throw new InvalidOperationException("Cached scene object payload is out of sync with the scene signature.");
                        instanceId = (uint)objectDataIndex;
                        GPUObjectData objectData = _objectData[objectDataIndex];
                        objectData.PreviousWorldMatrix = GetPreviousWorldMatrix(batch, instance, objectData.WorldMatrix);
                        objectData.NearFieldStableObjectId =
                            AccelerationStructureManager.StableInstanceIdentity(
                                batch.Id,
                                checked((uint)instance));
                        objectData.NearFieldStableMaterialId =
                            nearFieldStableMaterialId;
                        objectData.NearFieldPackedObjectMaterialRevisions =
                            PackNearFieldRevisions(
                                ((ulong)batch.Revision << 32) |
                                checked((uint)instance + 1u),
                                nearFieldMaterialRevision);
                        objectData.NearFieldCoverageMotionFlags = nearFieldFlags;
                        _objectData[objectDataIndex] = objectData;
                    }
                    _previousStaticInstanceMatrices[new StaticInstanceKey(batch, instance)] = worldMatrix;

                    objectDataIndex++;
                    if (deferredTransparentPayload)
                    {
                        _lastObjectCullMicroseconds +=
                            ElapsedMicroseconds(objectStart);
                        continue;
                    }
                    Vector3 worldCenter = TransformPoint(localCenter, worldMatrix);
                    float worldRadius = localRadius * GetMaxScale(worldMatrix);
                    float transparentDistanceSquared = useCameraDependentCpuPayload && (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                        ? DistanceSquared(cameraPosition, worldCenter)
                        : 0f;
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);

                    long meshletStart = Stopwatch.GetTimestamp();
                    uint meshletCommandFlags = CreateMeshletCommandFlags(
                        metadata,
                        worldMatrix,
                        isSkinned: false,
                        renderMode,
                        isGeometryDecal);
                    if (buildGpuInstanceCandidates &&
                        renderMode != MaterialRenderMode.Blend &&
                        !isGeometryDecal)
                    {
                        AddInstanceCandidate(
                            instanceId,
                            checked((uint)materialIndex),
                            meshletCommandFlags,
                            forwardClass,
                            meshInfo,
                            renderMode,
                            castsDirectionalShadow,
                            dynamicDirectionalShadow: false);
                    }
                    var staticInstanceKey = new StaticInstanceKey(batch, instance);
                    int previousLodLevel = _previousStaticInstanceLods.TryGetValue(staticInstanceKey, out int storedLodLevel)
                        ? storedLodLevel
                        : -1;
                    int lodLevel = useCameraDependentCpuPayload
                        ? SelectMeshletLodLevelNormalized(
                            cameraPosition,
                            worldCenter,
                            worldRadius,
                            previousLodLevel,
                            gpuLod1DistanceRatio,
                            gpuLod2DistanceRatio)
                        : 0;
                    _previousStaticInstanceLods[staticInstanceKey] = lodLevel;
                    MeshletLodRange meshletRange = GetMeshletLodRange(meshInfo, lodLevel, out int effectiveLodLevel);
                    if (cameraVisible && meshInfo.MeshletCount > meshletRange.Count)
                        _meshletLodSkippedCpu += checked((int)(meshInfo.MeshletCount - meshletRange.Count));

                    bool objectIntersectsShadowCascade = castsDirectionalShadow && !useCameraDependentCpuPayload;
                    if (castsDirectionalShadow && useCameraDependentCpuPayload)
                    {
                        for (int cascade = 0; cascade < directionalShadowCascadeCount; cascade++)
                        {
                            if (IsVisible(meshInfo, worldMatrix, shadowFrusta[cascade], out _))
                            {
                                objectIntersectsShadowCascade = true;
                                break;
                            }
                        }
                    }

                    for (uint i = 0; i < meshletRange.Count; i++)
                    {
                        if (cameraVisible)
                            _meshletCandidatesCpu++;
                        uint meshletIndex = meshletRange.Offset + i;
                        bool meshletVisibleToCamera = cameraVisible;
                        if (cameraVisible &&
                            useCpuMeshletFrustumCulling &&
                            !objectFullyInsideFrustum &&
                            meshletRange.Count >= CpuMeshletCullingThreshold &&
                            !MeshletIntersectsFrustum(meshletIndex, worldMatrix, frustum))
                        {
                            _meshletFrustumCulledCpu++;
                            meshletVisibleToCamera = false;
                        }

                        Meshlet meshlet = _meshManager.GetMeshlet(meshletIndex);
                        var command = new GPUMeshletDrawCommand
                        {
                            MeshletIndex = meshletIndex,
                            InstanceId = instanceId,
                            MaterialIndex = (uint)materialIndex,
                            Flags = meshletCommandFlags
                        };

                        if (meshletVisibleToCamera)
                        {
                            RecordSubmittedMeshlet(meshlet);
                            RecordSubmittedMeshletLod(effectiveLodLevel);

                            if (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                            {
                                if (isGeometryDecal)
                                    _geometryDecalMeshletCount++;
                                if (!isGeometryDecal ||
                                    (geometryDecalsEnabled &&
                                     (isolatedDecalMaterialIndex < 0 ||
                                      isolatedDecalMaterialIndex == materialIndex)))
                                    AddTransparentDraw(
                                        command,
                                        transparentDistanceSquared,
                                        metadata.DecalLayer,
                                        forwardClass,
                                        metadata.IsGeometryDecal,
                                        metadata.TransmissionPolicy,
                                        ReceivesSceneReflections(metadata),
                                        maxTransparentMeshlets);
                            }
                            else
                            {
                                _staticBatchMeshletDrawCommandCount++;
                                if (retainCpuOpaqueMeshletCommands)
                                {
                                    GPUPackedMeshletDrawCommand packedCommand = CreatePackedMeshletDrawCommand(
                                        command,
                                        meshlet,
                                        worldMatrix,
                                        renderMode,
                                        meshletVisibleToCamera,
                                        objectFullyInsideFrustum,
                                        useCpuMeshletFrustumCulling &&
                                        !objectFullyInsideFrustum &&
                                        meshletRange.Count >= CpuMeshletCullingThreshold);
                                    AddOpaqueForwardDraw(command, packedCommand, forwardClass, meshInfo);
                                    if (renderMode == MaterialRenderMode.Mask)
                                    {
                                        _maskedDepthMeshletDrawCommands.Add(command);
                                        _packedMaskedDepthMeshletDrawCommands.Add(packedCommand);
                                    }
                                    else
                                    {
                                        _solidDepthMeshletDrawCommands.Add(command);
                                        _packedSolidDepthMeshletDrawCommands.Add(packedCommand);
                                    }
                                }
                            }
                        }

                        if (castsDirectionalShadow &&
                            objectIntersectsShadowCascade &&
                            !buildGpuInstanceCandidates)
                        {
                            _directionalShadowMeshletDrawCommands.Add(command);
                            _directionalStaticShadowMeshletDrawCommands.Add(command);
                        }
                        if (castsLocalShadow)
                        {
                            _localShadowMeshletDrawCommands.Add(command);
                            _localStaticShadowMeshletDrawCommands.Add(command);
                            AccumulatePointShadowFaceCoverage(meshlet, worldMatrix, selectedPointShadows);
                        }
                    }

                    _lastMeshletCullMicroseconds += ElapsedMicroseconds(meshletStart);
                }
            }

            _cpuStaticBatchBuildMicroseconds = ElapsedMicroseconds(staticBatchStart);

            if (!rebuildObjectData && objectDataIndex != _objectData.Count)
                throw new InvalidOperationException("Cached scene object payload count is out of sync with the scene signature.");

            long sortStart = Stopwatch.GetTimestamp();
            _transparentSortScratch.Sort(CompareTransparentMeshlets);
            _transparentSortMicroseconds = ElapsedMicroseconds(sortStart);
            AppendTransparentDrawOrder(
                transparencyMode,
                transparentPipelinePartitioningEnabled);

            foreach (MaterialRenderMetadata metadata in _materialManager.GetMaterialMetadataSnapshot())
            {
                if (metadata.RenderMode == MaterialRenderMode.Blend)
                    _blendMaterialCount++;
                if (metadata.RenderMode == MaterialRenderMode.Mask)
                    _maskMaterialCount++;
                if (metadata.IsGeometryDecal)
                    _geometryDecalMaterialCount++;
            }
        }

        private bool BuildTransparentScenePayload(
            Scene scene,
            Vector3 cameraPosition,
            Frustum frustum,
            bool geometryDecalsEnabled,
            int isolatedDecalMaterialIndex,
            int maxTransparentMeshlets,
            TransparencyMode transparencyMode,
            bool transparentPipelinePartitioningEnabled,
            float gpuLod1DistanceRatio,
            float gpuLod2DistanceRatio)
        {
            _transparentMeshletDrawCommands.Clear();
            _transparentMaterialRuns.Clear();
            _transparentSortScratch.Clear();
            _transparentObjectCount = 0;
            _transparentReflectionReceiverObjectCount = 0;
            _transparentReflectionReceiverMeshletCount = 0;
            _thinGlassObjectCount = 0;
            _thinGlassMeshletCount = 0;
            _geometryDecalObjectCount = 0;
            _geometryDecalMeshletCount = 0;
            _transparentOverflowCount = 0;
            _transparentSortMicroseconds = 0;

            bool hasCameraDependentContent = false;
            int objectDataIndex = 0;
            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (!renderObject.Visible ||
                    renderObject.Mesh is not MeshHandle meshHandle ||
                    !meshHandle.IsValid)
                {
                    continue;
                }

                uint instanceId = checked((uint)objectDataIndex++);
                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                MaterialHandle materialHandle = ResolveRenderObjectMaterialHandle(
                    renderObject.Material,
                    _materialManager.DefaultMaterialHandle,
                    renderObject.Name ?? string.Empty);
                int materialIndex =
                    _materialManager.ResolveMaterialIndex(materialHandle);
                GPUMaterialData material =
                    _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMetadata metadata =
                    _materialManager.GetMaterialMetadata(materialHandle);
                MaterialRenderMode renderMode = metadata.RenderMode;
                bool isGeometryDecal = metadata.IsGeometryDecal;
                if (renderMode != MaterialRenderMode.Blend &&
                    !isGeometryDecal)
                {
                    continue;
                }

                hasCameraDependentContent = true;
                Matrix4x4 cullingMatrix = GetCullingMatrix(renderObject);
                if (!IsVisible(
                        meshInfo,
                        cullingMatrix,
                        frustum,
                        out bool objectFullyInsideFrustum))
                {
                    continue;
                }

                MaterialForwardClass forwardClass =
                    MaterialForwardClassifier.Classify(material, metadata);
                int previousLodLevel = _previousRenderObjectLods.TryGetValue(
                    renderObject,
                    out int storedLodLevel)
                    ? storedLodLevel
                    : -1;
                int lodLevel = AppendTransparentInstancePayload(
                    instanceId,
                    meshInfo,
                    cullingMatrix,
                    cameraPosition,
                    frustum,
                    objectFullyInsideFrustum,
                    checked((uint)materialIndex),
                    metadata,
                    forwardClass,
                    renderMode,
                    isGeometryDecal,
                    renderObject is SkinnedRenderObject
                    {
                        SkinningEnabled: true
                    },
                    previousLodLevel,
                    geometryDecalsEnabled,
                    isolatedDecalMaterialIndex,
                    maxTransparentMeshlets,
                    gpuLod1DistanceRatio,
                    gpuLod2DistanceRatio);
                _previousRenderObjectLods[renderObject] = lodLevel;
            }

            foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
            {
                if (!batch.Visible ||
                    batch.Mesh is not MeshHandle meshHandle ||
                    !meshHandle.IsValid)
                {
                    continue;
                }

                MeshInfo meshInfo = GetValidatedMeshInfo(meshHandle);
                MaterialHandle materialHandle = ResolveRenderObjectMaterialHandle(
                    batch.Material,
                    _materialManager.DefaultMaterialHandle,
                    batch.Name);
                int materialIndex =
                    _materialManager.ResolveMaterialIndex(materialHandle);
                GPUMaterialData material =
                    _materialManager.GetMaterialData(materialHandle);
                MaterialRenderMetadata metadata =
                    _materialManager.GetMaterialMetadata(materialHandle);
                MaterialRenderMode renderMode = metadata.RenderMode;
                bool isGeometryDecal = metadata.IsGeometryDecal;
                bool cameraDependent =
                    renderMode == MaterialRenderMode.Blend ||
                    isGeometryDecal;
                hasCameraDependentContent |= cameraDependent;

                for (int instance = 0;
                     instance < batch.WorldMatrices.Count;
                     instance++)
                {
                    uint instanceId = checked((uint)objectDataIndex++);
                    if (!cameraDependent)
                        continue;

                    Matrix4x4 worldMatrix = batch.WorldMatrices[instance];
                    if (!IsVisible(
                            meshInfo,
                            worldMatrix,
                            frustum,
                            out bool objectFullyInsideFrustum))
                    {
                        continue;
                    }

                    MaterialForwardClass forwardClass =
                        MaterialForwardClassifier.Classify(material, metadata);
                    var key = new StaticInstanceKey(batch, instance);
                    int previousLodLevel = _previousStaticInstanceLods
                        .TryGetValue(key, out int storedLodLevel)
                        ? storedLodLevel
                        : -1;
                    int lodLevel = AppendTransparentInstancePayload(
                        instanceId,
                        meshInfo,
                        worldMatrix,
                        cameraPosition,
                        frustum,
                        objectFullyInsideFrustum,
                        checked((uint)materialIndex),
                        metadata,
                        forwardClass,
                        renderMode,
                        isGeometryDecal,
                        isSkinned: false,
                        previousLodLevel,
                        geometryDecalsEnabled,
                        isolatedDecalMaterialIndex,
                        maxTransparentMeshlets,
                        gpuLod1DistanceRatio,
                        gpuLod2DistanceRatio);
                    _previousStaticInstanceLods[key] = lodLevel;
                }
            }

            if (objectDataIndex != _objectData.Count)
            {
                throw new InvalidOperationException(
                    "Transparent payload instance mapping is out of sync with the stable scene object buffer.");
            }

            long sortStart = Stopwatch.GetTimestamp();
            _transparentSortScratch.Sort(CompareTransparentMeshlets);
            _transparentSortMicroseconds = ElapsedMicroseconds(sortStart);
            AppendTransparentDrawOrder(
                transparencyMode,
                transparentPipelinePartitioningEnabled);
            return hasCameraDependentContent;
        }

        private int AppendTransparentInstancePayload(
            uint instanceId,
            MeshInfo meshInfo,
            Matrix4x4 cullingMatrix,
            Vector3 cameraPosition,
            Frustum frustum,
            bool objectFullyInsideFrustum,
            uint materialIndex,
            MaterialRenderMetadata metadata,
            MaterialForwardClass forwardClass,
            MaterialRenderMode renderMode,
            bool isGeometryDecal,
            bool isSkinned,
            int previousLodLevel,
            bool geometryDecalsEnabled,
            int isolatedDecalMaterialIndex,
            int maxTransparentMeshlets,
            float gpuLod1DistanceRatio,
            float gpuLod2DistanceRatio)
        {
            if (isGeometryDecal)
            {
                _geometryDecalObjectCount++;
            }
            else
            {
                _transparentObjectCount++;
                if (ReceivesSceneReflections(metadata))
                    _transparentReflectionReceiverObjectCount++;
                if (forwardClass == MaterialForwardClass.ThinGlass)
                    _thinGlassObjectCount++;
            }

            Vector3 localCenter =
                (ToCoreVector(meshInfo.BoundingBoxMin) +
                 ToCoreVector(meshInfo.BoundingBoxMax)) * 0.5f;
            Vector3 worldCenter = TransformPoint(localCenter, cullingMatrix);
            float localRadius = Distance(
                ToCoreVector(meshInfo.BoundingBoxMin),
                localCenter);
            float worldRadius = localRadius * GetMaxScale(cullingMatrix);
            float distanceSquared = DistanceSquared(
                cameraPosition,
                worldCenter);
            int lodLevel = SelectMeshletLodLevelNormalized(
                cameraPosition,
                worldCenter,
                worldRadius,
                previousLodLevel,
                gpuLod1DistanceRatio,
                gpuLod2DistanceRatio);
            MeshletLodRange meshletRange = GetMeshletLodRange(
                meshInfo,
                lodLevel,
                out _);
            uint commandFlags = CreateMeshletCommandFlags(
                metadata,
                cullingMatrix,
                isSkinned,
                renderMode,
                isGeometryDecal);
            bool appendGeometryDecal = !isGeometryDecal ||
                (geometryDecalsEnabled &&
                 (isolatedDecalMaterialIndex < 0 ||
                  isolatedDecalMaterialIndex == materialIndex));

            for (uint i = 0; i < meshletRange.Count; i++)
            {
                uint meshletIndex = meshletRange.Offset + i;
                if (!objectFullyInsideFrustum &&
                    meshletRange.Count >= CpuMeshletCullingThreshold &&
                    !MeshletIntersectsFrustum(
                        meshletIndex,
                        cullingMatrix,
                        frustum))
                {
                    continue;
                }

                if (isGeometryDecal)
                    _geometryDecalMeshletCount++;
                if (!appendGeometryDecal)
                    continue;

                AddTransparentDraw(
                    new GPUMeshletDrawCommand
                    {
                        MeshletIndex = meshletIndex,
                        InstanceId = instanceId,
                        MaterialIndex = materialIndex,
                        Flags = commandFlags
                    },
                    distanceSquared,
                    metadata.DecalLayer,
                    forwardClass,
                    isGeometryDecal,
                    metadata.TransmissionPolicy,
                    ReceivesSceneReflections(metadata),
                    maxTransparentMeshlets);
            }

            return lodLevel;
        }

        private void AddTransparentDraw(
            GPUMeshletDrawCommand command,
            float distanceSquared,
            int layer,
            MaterialForwardClass forwardClass,
            bool isGeometryDecal,
            GiTransmissionPolicy transmissionPolicy,
            bool receivesSceneReflections,
            int maxTransparentMeshlets)
        {
            if (_transparentSortScratch.Count >= maxTransparentMeshlets)
            {
                _transparentOverflowCount++;
                return;
            }

            if (forwardClass == MaterialForwardClass.ThinGlass)
                _thinGlassMeshletCount++;
            if (receivesSceneReflections)
                _transparentReflectionReceiverMeshletCount++;
            _transparentSortScratch.Add(new TransparentMeshletDraw(
                command,
                distanceSquared,
                layer,
                new TransparentDrawClassification(
                    TransparentMaterialClassifier.Classify(
                        forwardClass,
                        isGeometryDecal,
                        transmissionPolicy),
                    receivesSceneReflections)));
        }

        private void AppendTransparentDrawOrder(
            TransparencyMode transparencyMode,
            bool pipelinePartitioningEnabled)
        {
            if (!pipelinePartitioningEnabled)
            {
                foreach (TransparentMeshletDraw draw in
                         _transparentSortScratch)
                {
                    _transparentMeshletDrawCommands.Add(draw.Command);
                }

                return;
            }

            if (transparencyMode == TransparencyMode.WeightedBlendedOit)
            {
                // Retain the canonical list as the rollback source for exact
                // feedback and lazy-pipeline failure. Weighted OIT remains
                // deterministic and partitions its adjacent keys without
                // duplicating the GPU command buffer or upload bandwidth.
                foreach (TransparentMeshletDraw draw in
                         _transparentSortScratch)
                    AppendTransparentDraw(draw);

                return;
            }

            foreach (TransparentMeshletDraw draw in _transparentSortScratch)
                AppendTransparentDraw(draw);
        }

        private void AppendTransparentDraw(TransparentMeshletDraw draw)
        {
            int drawIndex = _transparentMeshletDrawCommands.Count;
            _transparentMeshletDrawCommands.Add(draw.Command);
            if (_transparentMaterialRuns.Count > 0)
            {
                int lastIndex = _transparentMaterialRuns.Count - 1;
                TransparentMaterialRun last =
                    _transparentMaterialRuns[lastIndex];
                if (last.Classification == draw.Classification)
                {
                    _transparentMaterialRuns[lastIndex] = last with
                    {
                        DrawCount = checked(last.DrawCount + 1)
                    };
                    return;
                }
            }

            _transparentMaterialRuns.Add(new TransparentMaterialRun(
                drawIndex,
                1,
                draw.Classification));
        }

        internal static bool ReceivesSceneReflections(
            MaterialRenderMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            return !metadata.IsGeometryDecal &&
                   (metadata.BlendMode is MaterialBlendMode.AlphaBlend or
                       MaterialBlendMode.PremultipliedAlpha) &&
                   (metadata.ShadingModel is MaterialShadingModel.Pbr or
                       MaterialShadingModel.ThinGlass or
                       MaterialShadingModel.SubsurfaceApproximation);
        }

        private void AddOpaqueForwardDraw(
            GPUMeshletDrawCommand command,
            GPUPackedMeshletDrawCommand packedCommand,
            MaterialForwardClass forwardClass,
            MeshInfo meshInfo)
        {
            GPUMeshletCommandFlags commandFlags =
                (GPUMeshletCommandFlags)command.Flags;
            if ((commandFlags &
                 GPUMeshletCommandFlags.NormalConeCullEligible) != 0)
            {
                _normalConeEligibleOpaqueMeshletCount++;
            }
            if ((commandFlags &
                 GPUMeshletCommandFlags.MaterialDoubleSided) != 0)
            {
                _doubleSidedOpaqueMeshletCount++;
            }

            if (MaterialForwardClassifier.IsSimpleOpaque(forwardClass) &&
                !meshInfo.HasVertexColor)
            {
                _meshletDrawCommands.Add(command);
                _packedMeshletDrawCommands.Add(packedCommand);
                return;
            }

            if (MaterialForwardClassifier.IsSimpleNormalOpaque(forwardClass) ||
                MaterialForwardClassifier.IsSimpleOpaque(forwardClass))
            {
                _simpleNormalOpaqueMeshletDrawCommands.Add(command);
                _packedSimpleNormalOpaqueMeshletDrawCommands.Add(packedCommand);
                return;
            }

            _fullOpaqueMeshletDrawCommands.Add(command);
            _packedFullOpaqueMeshletDrawCommands.Add(packedCommand);
        }

        private void AddInstanceCandidate(
            uint instanceId,
            uint materialIndex,
            uint commandFlags,
            MaterialForwardClass forwardClass,
            MeshInfo meshInfo,
            MaterialRenderMode renderMode,
            bool castsDirectionalShadow,
            bool dynamicDirectionalShadow)
        {
            GPUSceneInstanceCandidate candidate = CreateInstanceCandidate(
                instanceId,
                materialIndex,
                commandFlags,
                forwardClass,
                meshInfo,
                renderMode,
                castsDirectionalShadow,
                dynamicDirectionalShadow);
            _instanceCandidates.Add(candidate);

            int meshletCount = checked((int)meshInfo.MeshletCount);
            GPUSceneInstanceClassification classification =
                (GPUSceneInstanceClassification)candidate.Classification;
            switch (classification &
                    GPUSceneInstanceClassification.ForwardBucketMask)
            {
                case GPUSceneInstanceClassification.SimpleOpaque:
                    _instanceCandidateSimpleMeshletCapacity = checked(
                        _instanceCandidateSimpleMeshletCapacity +
                        meshletCount);
                    break;
                case GPUSceneInstanceClassification.SimpleNormalOpaque:
                    _instanceCandidateSimpleNormalMeshletCapacity = checked(
                        _instanceCandidateSimpleNormalMeshletCapacity +
                        meshletCount);
                    break;
                default:
                    _instanceCandidateFullMeshletCapacity = checked(
                        _instanceCandidateFullMeshletCapacity +
                        meshletCount);
                    break;
            }

            if ((classification & GPUSceneInstanceClassification.Masked) != 0)
            {
                _instanceCandidateMaskedDepthMeshletCapacity = checked(
                    _instanceCandidateMaskedDepthMeshletCapacity +
                    meshletCount);
            }
            else
            {
                _instanceCandidateSolidDepthMeshletCapacity = checked(
                    _instanceCandidateSolidDepthMeshletCapacity +
                    meshletCount);
            }

            if ((classification &
                 GPUSceneInstanceClassification.CastsDirectionalShadow) != 0)
            {
                if ((classification &
                     GPUSceneInstanceClassification.DynamicDirectionalShadow) != 0)
                {
                    _instanceCandidateDirectionalDynamicShadowMeshletCapacity =
                        checked(
                            _instanceCandidateDirectionalDynamicShadowMeshletCapacity +
                            meshletCount);
                }
                else
                {
                    _instanceCandidateDirectionalStaticShadowMeshletCapacity =
                        checked(
                            _instanceCandidateDirectionalStaticShadowMeshletCapacity +
                            meshletCount);
                }
            }

            GPUMeshletCommandFlags flags =
                (GPUMeshletCommandFlags)commandFlags;
            if ((flags & GPUMeshletCommandFlags.NormalConeCullEligible) != 0)
            {
                _instanceCandidateNormalConeMeshletCount = checked(
                    _instanceCandidateNormalConeMeshletCount +
                    meshletCount);
            }
            if ((flags & GPUMeshletCommandFlags.MaterialDoubleSided) != 0)
            {
                _instanceCandidateDoubleSidedMeshletCount = checked(
                    _instanceCandidateDoubleSidedMeshletCount +
                    meshletCount);
            }
        }

        private static GPUSceneInstanceCandidate CreateInstanceCandidate(
            uint instanceId,
            uint materialIndex,
            uint commandFlags,
            MaterialForwardClass forwardClass,
            MeshInfo meshInfo,
            MaterialRenderMode renderMode,
            bool castsDirectionalShadow,
            bool dynamicDirectionalShadow)
        {
            GPUSceneInstanceClassification classification =
                ClassifyGpuInstanceCandidate(
                    forwardClass,
                    meshInfo.HasVertexColor,
                    renderMode,
                    castsDirectionalShadow,
                    dynamicDirectionalShadow);

            return new GPUSceneInstanceCandidate
            {
                InstanceId = instanceId,
                MaterialIndex = materialIndex,
                CommandFlags = commandFlags,
                Classification = (uint)classification
            };
        }

        internal static GPUSceneInstanceClassification
            ClassifyGpuInstanceCandidate(
                MaterialForwardClass forwardClass,
                bool hasVertexColor,
                MaterialRenderMode renderMode,
                bool castsDirectionalShadow = false,
                bool dynamicDirectionalShadow = false)
        {
            GPUSceneInstanceClassification classification;
            if (MaterialForwardClassifier.IsSimpleOpaque(forwardClass) &&
                !hasVertexColor)
            {
                classification =
                    GPUSceneInstanceClassification.SimpleOpaque;
            }
            else if (MaterialForwardClassifier.IsSimpleNormalOpaque(
                         forwardClass) ||
                     MaterialForwardClassifier.IsSimpleOpaque(forwardClass))
            {
                classification =
                    GPUSceneInstanceClassification.SimpleNormalOpaque;
            }
            else
            {
                classification =
                    GPUSceneInstanceClassification.FullOpaque;
            }

            if (renderMode == MaterialRenderMode.Mask)
                classification |= GPUSceneInstanceClassification.Masked;

            if (castsDirectionalShadow)
            {
                classification |=
                    GPUSceneInstanceClassification.CastsDirectionalShadow;
                if (dynamicDirectionalShadow)
                {
                    classification |= GPUSceneInstanceClassification
                        .DynamicDirectionalShadow;
                }
            }

            return classification;
        }

        private MeshInfo GetValidatedMeshInfo(MeshHandle meshHandle)
        {
            if (_meshInfoCache.TryGetValue(meshHandle, out MeshInfo cached))
                return cached;

            MeshInfo meshInfo = _meshManager.GetMeshInfo(meshHandle);
            _meshManager.ValidateMeshInfoRanges(meshInfo);
            _meshInfoCache.Add(meshHandle, meshInfo);
            return meshInfo;
        }

        private static Matrix4x4 GetCullingMatrix(RenderObject renderObject)
        {
            return renderObject is SkinnedRenderObject skinned
                ? skinned.SkinningBindTransform * renderObject.WorldMatrix
                : renderObject.WorldMatrix;
        }

        private static bool IsVisible(MeshInfo meshInfo, Matrix4x4 cullingMatrix, Frustum frustum, out bool fullyInsideFrustum)
        {
            var localBounds = new BoundingBox(ToCoreVector(meshInfo.BoundingBoxMin), ToCoreVector(meshInfo.BoundingBoxMax));
            BoundingBox worldBounds = TransformBoundingBox(localBounds, cullingMatrix);
            if (!IntersectsFrustum(worldBounds, frustum))
            {
                fullyInsideFrustum = false;
                return false;
            }

            fullyInsideFrustum = ContainsFrustum(worldBounds, frustum);
            return true;
        }

        private bool MeshletIntersectsFrustum(uint meshletIndex, Matrix4x4 worldMatrix, Frustum frustum)
        {
            Meshlet meshlet = _meshManager.GetMeshlet(meshletIndex);
            Vector3 worldCenter = TransformPoint(ToCoreVector(meshlet.BoundingSphereCenter), worldMatrix);
            float worldRadius = meshlet.BoundingSphereRadius * GetMaxScale(worldMatrix);
            return IntersectsFrustum(new BoundingSphere(worldCenter, worldRadius), frustum);
        }

        private static GPUPackedMeshletDrawCommand CreatePackedMeshletDrawCommand(
            GPUMeshletDrawCommand command,
            Meshlet meshlet,
            Matrix4x4 worldMatrix,
            MaterialRenderMode renderMode,
            bool cpuFrustumVisible,
            bool objectFullyInsideFrustum,
            bool cpuMeshletFrustumTested)
        {
            Vector3 worldCenter = TransformPoint(ToCoreVector(meshlet.BoundingSphereCenter), worldMatrix);
            float worldRadius = meshlet.BoundingSphereRadius * GetMaxScale(worldMatrix);
            GPUMeshletDrawFlags flags = GPUMeshletDrawFlags.None;
            if (!objectFullyInsideFrustum && !cpuMeshletFrustumTested)
                flags |= GPUMeshletDrawFlags.NeedsGpuFrustumTest;
            if (cpuFrustumVisible)
                flags |= GPUMeshletDrawFlags.CpuFrustumVisible;
            if (objectFullyInsideFrustum)
                flags |= GPUMeshletDrawFlags.ObjectFullyInsideFrustum;
            if (renderMode == MaterialRenderMode.Mask)
                flags |= GPUMeshletDrawFlags.MaterialMasked;
            if (renderMode == MaterialRenderMode.Blend)
                flags |= GPUMeshletDrawFlags.MaterialBlend;
            if (renderMode != MaterialRenderMode.Blend)
                flags |= GPUMeshletDrawFlags.CanHiZTest;
            GPUMeshletCommandFlags commandFlags =
                (GPUMeshletCommandFlags)command.Flags;
            if ((commandFlags & GPUMeshletCommandFlags.MaterialDoubleSided) != 0)
                flags |= GPUMeshletDrawFlags.MaterialDoubleSided;
            if ((commandFlags & GPUMeshletCommandFlags.NormalConeCullEligible) != 0)
                flags |= GPUMeshletDrawFlags.NormalConeCullEligible;

            return new GPUPackedMeshletDrawCommand
            {
                MeshletIndex = command.MeshletIndex,
                InstanceId = command.InstanceId,
                MaterialIndex = command.MaterialIndex,
                Flags = (uint)flags,
                WorldCenterRadius = new Vector4(worldCenter.X, worldCenter.Y, worldCenter.Z, worldRadius)
            };
        }

        private uint CreateMeshletCommandFlags(MaterialRenderMetadata metadata,
            Matrix4x4 worldMatrix,
            bool isSkinned,
            MaterialRenderMode renderMode,
            bool isGeometryDecal)
        {
            GPUMeshletCommandFlags flags = GPUMeshletCommandFlags.None;
            if (metadata.DoubleSided)
                flags |= GPUMeshletCommandFlags.MaterialDoubleSided;

            // Skinning and anisotropic transforms can widen an object-space
            // normal cone. Restrict whole-meshlet rejection to rigid or
            // uniformly-scaled one-sided instances, where inverse-transpose
            // preserves every cone angle exactly.
            if (_meshletNormalConeCullingEnabled &&
                renderMode == MaterialRenderMode.Opaque &&
                !isGeometryDecal &&
                !metadata.DoubleSided &&
                !isSkinned &&
                IsNormalConePreservingTransform(worldMatrix))
            {
                flags |= GPUMeshletCommandFlags.NormalConeCullEligible;
            }

            return (uint)flags;
        }

        internal static bool IsNormalConePreservingTransform(Matrix4x4 matrix)
        {
            Vector3 basisX = new(matrix.M11, matrix.M12, matrix.M13);
            Vector3 basisY = new(matrix.M21, matrix.M22, matrix.M23);
            Vector3 basisZ = new(matrix.M31, matrix.M32, matrix.M33);
            float xLength = basisX.Length();
            float yLength = basisY.Length();
            float zLength = basisZ.Length();
            float maximumLength = MathF.Max(xLength, MathF.Max(yLength, zLength));
            if (!float.IsFinite(maximumLength) || maximumLength <= 1e-6f)
                return false;

            float scaleTolerance = maximumLength * 1e-3f;
            if (MathF.Abs(xLength - yLength) > scaleTolerance ||
                MathF.Abs(xLength - zLength) > scaleTolerance)
            {
                return false;
            }

            float orthogonalityTolerance = maximumLength * maximumLength * 1e-3f;
            if (MathF.Abs(Vector3.Dot(basisX, basisY)) > orthogonalityTolerance ||
                MathF.Abs(Vector3.Dot(basisX, basisZ)) > orthogonalityTolerance ||
                MathF.Abs(Vector3.Dot(basisY, basisZ)) > orthogonalityTolerance)
            {
                return false;
            }

            // A reflection reverses triangle winding. The inverse-transpose
            // axis alone does not include that orientation sign, so using its
            // object-space cone could reject front-facing mirrored geometry.
            float determinant = Vector3.Dot(
                basisX,
                Vector3.Cross(basisY, basisZ));
            return float.IsFinite(determinant) && determinant > 0.0f;
        }

        private void AccumulatePointShadowFaceCoverage(
            Meshlet meshlet,
            Matrix4x4 worldMatrix,
            ReadOnlySpan<SelectedLocalShadow> selectedPointShadows)
        {
            int count = Math.Min(selectedPointShadows.Length, _pointShadowFaceMasks.Length);
            if (count == 0)
                return;

            Vector3 worldCenter = TransformPoint(ToCoreVector(meshlet.BoundingSphereCenter), worldMatrix);
            float worldRadius = meshlet.BoundingSphereRadius * GetMaxScale(worldMatrix);
            for (int i = 0; i < count; i++)
            {
                SelectedLocalShadow selected = selectedPointShadows[i];
                Vector3 lightPosition = ToCoreVector(selected.Light.Position);
                float range = MathF.Max(0f, selected.Light.Range);
                float rangeWithRadius = range + worldRadius;
                if (DistanceSquared(worldCenter, lightPosition) > rangeWithRadius * rangeWithRadius)
                    continue;

                Vector3 lightToMeshlet = worldCenter - lightPosition;
                _pointShadowFaceMasks[i] |= ClassifyPointShadowFaces(lightToMeshlet, worldRadius);
            }
        }

        private static int ClassifyPointShadowFaces(Vector3 lightToMeshlet, float radius)
        {
            if (DistanceSquared(Vector3.Zero, lightToMeshlet) <= radius * radius)
                return 0x3F;

            int mask = 0;
            if (IntersectsCubeFace(lightToMeshlet.X, lightToMeshlet.Y, lightToMeshlet.Z, radius))
                mask |= 1 << 0;
            if (IntersectsCubeFace(-lightToMeshlet.X, lightToMeshlet.Y, lightToMeshlet.Z, radius))
                mask |= 1 << 1;
            if (IntersectsCubeFace(lightToMeshlet.Y, lightToMeshlet.X, lightToMeshlet.Z, radius))
                mask |= 1 << 2;
            if (IntersectsCubeFace(-lightToMeshlet.Y, lightToMeshlet.X, lightToMeshlet.Z, radius))
                mask |= 1 << 3;
            if (IntersectsCubeFace(lightToMeshlet.Z, lightToMeshlet.X, lightToMeshlet.Y, radius))
                mask |= 1 << 4;
            if (IntersectsCubeFace(-lightToMeshlet.Z, lightToMeshlet.X, lightToMeshlet.Y, radius))
                mask |= 1 << 5;

            return mask;
        }

        private static bool IntersectsCubeFace(float signedAxis, float otherAxisA, float otherAxisB, float radius)
        {
            float opposingExtent = MathF.Max(0f, MathF.Max(MathF.Abs(otherAxisA) - radius, MathF.Abs(otherAxisB) - radius));
            return signedAxis + radius >= opposingExtent;
        }

        private static Matrix4x4 GetShadowCascadeMatrix(GPUShadowData data, int cascade)
        {
            return cascade switch
            {
                0 => data.LightViewProjection0,
                1 => data.LightViewProjection1,
                2 => data.LightViewProjection2,
                _ => data.LightViewProjection3
            };
        }

        private void RecordSubmittedMeshlet(Meshlet meshlet)
        {
            _submittedMeshletCountCpu++;
            _submittedMeshletTriangleSum += meshlet.LocalTriangleCount;
            _submittedMeshletVertexSum += meshlet.LocalVertexCount;
            if (meshlet.LocalTriangleCount < 16)
                _submittedSmallMeshletsUnder16Triangles++;
            if (meshlet.LocalTriangleCount < 32)
                _submittedSmallMeshletsUnder32Triangles++;
        }

        private void RecordSubmittedMeshletLod(int lodLevel)
        {
            switch (lodLevel)
            {
                case 0:
                    _meshletLod0SubmittedCpu++;
                    break;
                case 1:
                    _meshletLod1SubmittedCpu++;
                    break;
                default:
                    _meshletLod2SubmittedCpu++;
                    break;
            }
        }

        internal static int SelectMeshletLodLevel(
            Vector3 cameraPosition,
            Vector3 worldCenter,
            float worldRadius,
            int previousLodLevel = -1,
            float lod1DistanceRatio = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio,
            float lod2DistanceRatio = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio)
        {
            lod1DistanceRatio = SceneSubmissionSettings.ClampGpuLod1DistanceRatio(lod1DistanceRatio);
            lod2DistanceRatio = SceneSubmissionSettings.ClampGpuLod2DistanceRatio(lod2DistanceRatio, lod1DistanceRatio);
            return SelectMeshletLodLevelNormalized(
                cameraPosition,
                worldCenter,
                worldRadius,
                previousLodLevel,
                lod1DistanceRatio,
                lod2DistanceRatio);
        }

        private static int SelectMeshletLodLevelNormalized(
            Vector3 cameraPosition,
            Vector3 worldCenter,
            float worldRadius,
            int previousLodLevel,
            float lod1DistanceRatio,
            float lod2DistanceRatio)
        {
            float effectiveRadius = Math.Max(worldRadius, 1f);
            float distanceFromSurface = Math.Max(0f, Distance(cameraPosition, worldCenter) - effectiveRadius);
            float distanceRatio = distanceFromSurface / effectiveRadius;

            if (previousLodLevel >= 0)
            {
                return SelectMeshletLodLevelNormalized(
                    distanceRatio,
                    previousLodLevel,
                    MeshletLodHysteresisFraction,
                    lod1DistanceRatio,
                    lod2DistanceRatio);
            }

            return SelectMeshletLodLevelNormalized(distanceRatio, lod1DistanceRatio, lod2DistanceRatio);
        }

        internal static int SelectMeshletLodLevel(
            float distanceRatio,
            int previousLodLevel,
            float hysteresisFraction,
            float lod1DistanceRatio = SceneSubmissionSettings.DefaultGpuLod1DistanceRatio,
            float lod2DistanceRatio = SceneSubmissionSettings.DefaultGpuLod2DistanceRatio)
        {
            lod1DistanceRatio = SceneSubmissionSettings.ClampGpuLod1DistanceRatio(lod1DistanceRatio);
            lod2DistanceRatio = SceneSubmissionSettings.ClampGpuLod2DistanceRatio(lod2DistanceRatio, lod1DistanceRatio);
            return SelectMeshletLodLevelNormalized(
                distanceRatio,
                previousLodLevel,
                hysteresisFraction,
                lod1DistanceRatio,
                lod2DistanceRatio);
        }

        private static int SelectMeshletLodLevelNormalized(
            float distanceRatio,
            int previousLodLevel,
            float hysteresisFraction,
            float lod1DistanceRatio,
            float lod2DistanceRatio)
        {
            if (previousLodLevel < 0)
                return SelectMeshletLodLevelNormalized(distanceRatio, lod1DistanceRatio, lod2DistanceRatio);

            float hysteresis = Math.Clamp(hysteresisFraction, 0f, 0.5f);
            return previousLodLevel switch
            {
                0 when distanceRatio < lod1DistanceRatio * (1f + hysteresis) => 0,
                1 when distanceRatio >= lod1DistanceRatio * (1f - hysteresis) &&
                       distanceRatio < lod2DistanceRatio * (1f + hysteresis) => 1,
                2 when distanceRatio >= lod2DistanceRatio * (1f - hysteresis) => 2,
                _ => SelectMeshletLodLevelNormalized(distanceRatio, lod1DistanceRatio, lod2DistanceRatio)
            };
        }

        private static int SelectMeshletLodLevelNormalized(
            float distanceRatio,
            float lod1DistanceRatio,
            float lod2DistanceRatio)
        {
            if (distanceRatio >= lod2DistanceRatio)
                return 2;
            if (distanceRatio >= lod1DistanceRatio)
                return 1;
            return 0;
        }

        private static MeshletLodRange GetMeshletLodRange(MeshInfo meshInfo, int lodLevel, out int effectiveLodLevel)
        {
            MeshletLodRange range = lodLevel switch
            {
                2 => new MeshletLodRange(meshInfo.MeshletLod2Offset, meshInfo.MeshletLod2Count),
                1 => new MeshletLodRange(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count),
                _ => new MeshletLodRange(meshInfo.MeshletOffset, meshInfo.MeshletCount)
            };

            if (range.Count > 0)
            {
                effectiveLodLevel = lodLevel;
                return range;
            }

            if (meshInfo.MeshletCount > 0)
            {
                effectiveLodLevel = 0;
                return new MeshletLodRange(meshInfo.MeshletOffset, meshInfo.MeshletCount);
            }
            if (meshInfo.MeshletLod1Count > 0)
            {
                effectiveLodLevel = 1;
                return new MeshletLodRange(meshInfo.MeshletLod1Offset, meshInfo.MeshletLod1Count);
            }

            effectiveLodLevel = 2;
            return new MeshletLodRange(meshInfo.MeshletLod2Offset, meshInfo.MeshletLod2Count);
        }

        internal static uint CreateNearFieldStableMaterialIdentity(
            MaterialHandle material)
        {
            if (!material.IsValid)
                return 0u;

            uint hash = 2166136261u;
            hash = (hash ^ unchecked((uint)material.Index)) * 16777619u;
            hash = (hash ^ material.Generation) * 16777619u;
            return hash == 0u ? 1u : hash;
        }

        internal static uint PackNearFieldRevisions(
            ulong objectRevision,
            uint materialRevision)
        {
            // Reserve zero for invalid. Modulo 65,535 gives every supported
            // revision a non-zero 16-bit value; SceneContentRevision is also
            // part of C5's structural history identity, so a wrap cannot
            // alias an older history generation.
            uint objectRevision16 = checked((uint)(objectRevision % 65_535UL)) + 1u;
            uint materialRevision16 = materialRevision == 0u
                ? 1u
                : (materialRevision - 1u) % 65_535u + 1u;
            return objectRevision16 | (materialRevision16 << 16);
        }

        internal static uint CreateNearFieldSurfaceFlags(
            MaterialRenderMode renderMode,
            bool isGeometryDecal)
        {
            if (isGeometryDecal || renderMode == MaterialRenderMode.Blend)
                return 0u;

            SimpleDdgiNearFieldSurfaceFlags flags = renderMode ==
                MaterialRenderMode.Mask
                    ? SimpleDdgiNearFieldSurfaceFlags.AlphaMasked
                    : SimpleDdgiNearFieldSurfaceFlags.Opaque;
            flags |= SimpleDdgiNearFieldSurfaceFlags.CoverageValid |
                SimpleDdgiNearFieldSurfaceFlags.MotionVectorsValid;
            return (uint)flags;
        }

        private static Vector3 ToCoreVector(System.Numerics.Vector3 value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3 ToCoreVector(Vector3 value)
        {
            return value;
        }

        private static int CompareTransparentMeshlets(TransparentMeshletDraw left, TransparentMeshletDraw right)
        {
            int distanceComparison = right.DistanceSquared.CompareTo(left.DistanceSquared);
            if (distanceComparison != 0)
                return distanceComparison;

            int layerComparison = left.Layer.CompareTo(right.Layer);
            if (layerComparison != 0)
                return layerComparison;

            int materialComparison = left.Command.MaterialIndex.CompareTo(right.Command.MaterialIndex);
            if (materialComparison != 0)
                return materialComparison;

            int instanceComparison = left.Command.InstanceId.CompareTo(right.Command.InstanceId);
            if (instanceComparison != 0)
                return instanceComparison;

            return left.Command.MeshletIndex.CompareTo(right.Command.MeshletIndex);
        }

        internal static Vector3 TransformPoint(Vector3 position, Matrix4x4 transform)
        {
            return new Vector3(
                position.X * transform.M11 + position.Y * transform.M21 + position.Z * transform.M31 + transform.M41,
                position.X * transform.M12 + position.Y * transform.M22 + position.Z * transform.M32 + transform.M42,
                position.X * transform.M13 + position.Y * transform.M23 + position.Z * transform.M33 + transform.M43);
        }

        internal static float DistanceSquared(Vector3 left, Vector3 right)
        {
            float x = left.X - right.X;
            float y = left.Y - right.Y;
            float z = left.Z - right.Z;
            return x * x + y * y + z * z;
        }

        private static float Distance(Vector3 left, Vector3 right)
        {
            return MathF.Sqrt(DistanceSquared(left, right));
        }

        private static float GetMaxScale(Matrix4x4 matrix)
        {
            Vector3 scale = matrix.Scale;
            return Math.Max(scale.X, Math.Max(scale.Y, scale.Z));
        }

        internal static MaterialHandle ResolveRenderObjectMaterialHandle(
            object? material,
            MaterialHandle defaultMaterialHandle,
            string objectName)
        {
            if (material == null)
                return defaultMaterialHandle;

            if (material is MaterialHandle materialHandle)
                return materialHandle;

            if (material is int materialIndex && materialIndex == 0)
                return defaultMaterialHandle;

            string materialType = material.GetType().FullName ?? material.GetType().Name;
            throw new InvalidOperationException(
                $"Render object '{objectName}' has unsupported material type '{materialType}'. " +
                $"Production scene upload expects {nameof(MaterialHandle)} or null for the default material.");
        }

        public static Frustum ExtractFrustum(Matrix4x4 viewProjection)
        {
            return new Frustum
            {
                Left = NormalizePlane(new Vector4(
                    viewProjection.M11 + viewProjection.M14,
                    viewProjection.M21 + viewProjection.M24,
                    viewProjection.M31 + viewProjection.M34,
                    viewProjection.M41 + viewProjection.M44)),
                Right = NormalizePlane(new Vector4(
                    -viewProjection.M11 + viewProjection.M14,
                    -viewProjection.M21 + viewProjection.M24,
                    -viewProjection.M31 + viewProjection.M34,
                    -viewProjection.M41 + viewProjection.M44)),
                Bottom = NormalizePlane(new Vector4(
                    viewProjection.M12 + viewProjection.M14,
                    viewProjection.M22 + viewProjection.M24,
                    viewProjection.M32 + viewProjection.M34,
                    viewProjection.M42 + viewProjection.M44)),
                Top = NormalizePlane(new Vector4(
                    -viewProjection.M12 + viewProjection.M14,
                    -viewProjection.M22 + viewProjection.M24,
                    -viewProjection.M32 + viewProjection.M34,
                    -viewProjection.M42 + viewProjection.M44)),
                Near = NormalizePlane(new Vector4(
                    viewProjection.M13,
                    viewProjection.M23,
                    viewProjection.M33,
                    viewProjection.M43)),
                Far = NormalizePlane(new Vector4(
                    -viewProjection.M13 + viewProjection.M14,
                    -viewProjection.M23 + viewProjection.M24,
                    -viewProjection.M33 + viewProjection.M34,
                    -viewProjection.M43 + viewProjection.M44))
            };
        }

        private static Matrix4x4 ApplyProjectionJitter(Matrix4x4 projection, Vector2 jitter)
        {
            if (jitter.X == 0.0f && jitter.Y == 0.0f)
                return projection;

            projection.M11 += jitter.X * projection.M14;
            projection.M21 += jitter.X * projection.M24;
            projection.M31 += jitter.X * projection.M34;
            projection.M41 += jitter.X * projection.M44;
            projection.M12 += jitter.Y * projection.M14;
            projection.M22 += jitter.Y * projection.M24;
            projection.M32 += jitter.Y * projection.M34;
            projection.M42 += jitter.Y * projection.M44;
            return projection;
        }

        public static bool IntersectsFrustum(BoundingBox bounds, Frustum frustum)
        {
            return IntersectsPlane(bounds, frustum.Left) &&
                   IntersectsPlane(bounds, frustum.Right) &&
                   IntersectsPlane(bounds, frustum.Bottom) &&
                   IntersectsPlane(bounds, frustum.Top) &&
                   IntersectsPlane(bounds, frustum.Near) &&
                   IntersectsPlane(bounds, frustum.Far);
        }

        public static bool ContainsFrustum(BoundingBox bounds, Frustum frustum)
        {
            return InsidePlane(bounds, frustum.Left) &&
                   InsidePlane(bounds, frustum.Right) &&
                   InsidePlane(bounds, frustum.Bottom) &&
                   InsidePlane(bounds, frustum.Top) &&
                   InsidePlane(bounds, frustum.Near) &&
                   InsidePlane(bounds, frustum.Far);
        }

        public static bool IntersectsFrustum(BoundingSphere sphere, Frustum frustum)
        {
            return IntersectsPlane(sphere, frustum.Left) &&
                   IntersectsPlane(sphere, frustum.Right) &&
                   IntersectsPlane(sphere, frustum.Bottom) &&
                   IntersectsPlane(sphere, frustum.Top) &&
                   IntersectsPlane(sphere, frustum.Near) &&
                   IntersectsPlane(sphere, frustum.Far);
        }

        public static BoundingBox TransformBoundingBox(BoundingBox bounds, Matrix4x4 transform)
        {
            Span<Vector3> corners = stackalloc Vector3[8];
            corners[0] = bounds.Min;
            corners[1] = new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
            corners[2] = new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
            corners[3] = new Vector3(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
            corners[4] = new Vector3(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
            corners[5] = new Vector3(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
            corners[6] = new Vector3(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
            corners[7] = bounds.Max;

            Vector3 min = corners[0] * transform;
            Vector3 max = min;

            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 transformed = corners[i] * transform;
                min = Vector3.Min(min, transformed);
                max = Vector3.Max(max, transformed);
            }

            return new BoundingBox(min, max);
        }

        private static bool IntersectsPlane(BoundingBox bounds, Vector4 plane)
        {
            Vector3 positiveVertex = new Vector3(
                plane.X >= 0f ? bounds.Max.X : bounds.Min.X,
                plane.Y >= 0f ? bounds.Max.Y : bounds.Min.Y,
                plane.Z >= 0f ? bounds.Max.Z : bounds.Min.Z);

            return PlaneDistance(plane, positiveVertex) >= 0f;
        }

        private static bool InsidePlane(BoundingBox bounds, Vector4 plane)
        {
            Vector3 negativeVertex = new Vector3(
                plane.X >= 0f ? bounds.Min.X : bounds.Max.X,
                plane.Y >= 0f ? bounds.Min.Y : bounds.Max.Y,
                plane.Z >= 0f ? bounds.Min.Z : bounds.Max.Z);

            return PlaneDistance(plane, negativeVertex) >= 0f;
        }

        private static bool IntersectsPlane(BoundingSphere sphere, Vector4 plane)
        {
            return PlaneDistance(plane, sphere.Center) >= -sphere.Radius;
        }

        private static float PlaneDistance(Vector4 plane, Vector3 point)
        {
            return plane.X * point.X + plane.Y * point.Y + plane.Z * point.Z + plane.W;
        }

        private static Vector4 NormalizePlane(Vector4 plane)
        {
            float length = new Vector3(plane.X, plane.Y, plane.Z).Length();
            if (length <= float.Epsilon)
                throw new InvalidOperationException("Cannot normalize a frustum plane with a zero-length normal.");

            return plane / length;
        }

        private bool EnsureCapacity(
            ref SceneBuffer buffer,
            uint requiredElementCount,
            ulong stride,
            CommandBuffer commandBuffer)
        {
            if (requiredElementCount <= buffer.ElementCapacity)
                return false;

            WaitForOtherInFlightFrames();

            uint newCapacity = buffer.ElementCapacity;
            while (newCapacity < requiredElementCount)
                newCapacity = checked(newCapacity * 2);

            SceneBuffer oldBuffer = buffer;
            buffer = CreateSceneBuffer(newCapacity, stride, buffer.DebugName);
            _bufferManager.DestroyBuffer(oldBuffer.Handle);
            return true;
        }

        private void EnsureTiledLightBuffers(uint totalClusters, CommandBuffer commandBuffer)
        {
            if (totalClusters == 0)
                throw new ArgumentOutOfRangeException(nameof(totalClusters));

            uint headerCount = checked(
                totalClusters + TiledLightDenseControlHeaderCount);

            if (!_tiledLightHeaderBuffer.Handle.IsValid || !_tiledLightIndexBuffer.Handle.IsValid)
            {
                // The pair is allocated and retired atomically: the compute pass always
                // consumes matching header and index ranges.
                ReleaseTiledLightBuffers();
                _tiledLightHeaderBuffer = CreateSceneBuffer(headerCount, TiledLightHeaderStride, "Tiled Light Header Buffer");
                _tiledLightIndexBuffer = CreateSceneBuffer(
                    checked(totalClusters * MaxLightsPerTile),
                    TiledLightIndexStride,
                    "Tiled Light Index Buffer");
                return;
            }

            EnsureCapacity(ref _tiledLightHeaderBuffer, headerCount, TiledLightHeaderStride, commandBuffer);
            EnsureCapacity(
                ref _tiledLightIndexBuffer,
                checked(totalClusters * MaxLightsPerTile),
                TiledLightIndexStride,
                commandBuffer);
        }

        private void ReleaseTiledLightBuffers()
        {
            if (!_tiledLightHeaderBuffer.Handle.IsValid && !_tiledLightIndexBuffer.Handle.IsValid)
                return;

            // Previous frames can still reference the tiled lists. Wait before
            // destroying them, then rebind the descriptor slots to the always-live
            // object buffer in UpdateRegisteredBindlessBuffers.
            WaitForOtherInFlightFrames();
            DestroyIfValid(_tiledLightHeaderBuffer.Handle);
            DestroyIfValid(_tiledLightIndexBuffer.Handle);
            _tiledLightHeaderBuffer = SceneBuffer.Unallocated("Tiled Light Header Buffer");
            _tiledLightIndexBuffer = SceneBuffer.Unallocated("Tiled Light Index Buffer");
        }

        private void WaitForOtherInFlightFrames()
        {
            int currentFrame = _stagingRing.CurrentFrameIndex;
            for (int i = 0; i < FramesInFlight; i++)
            {
                if (i != currentFrame)
                    _sync.WaitForFence(i);
            }
        }

        private SceneBuffer CreateSceneBuffer(uint elementCapacity, ulong stride, string debugName)
        {
            if (elementCapacity == 0)
                throw new ArgumentOutOfRangeException(nameof(elementCapacity));
            if (stride == 0)
                throw new ArgumentOutOfRangeException(nameof(stride));
            if (string.IsNullOrWhiteSpace(debugName))
                throw new ArgumentException("Scene buffer debug name is required.", nameof(debugName));

            ulong byteSize = checked(elementCapacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                $"{debugName} ({elementCapacity} elements)");

            return new SceneBuffer(handle, elementCapacity, byteSize, debugName);
        }

        private static GPUMeshletTaskFrameData CreateMeshletTaskFrameData(
            Frustum frustum,
            Matrix4x4 viewProjectionMatrix,
            Matrix4x4 inverseViewMatrix,
            Matrix4x4 previousHiZViewProjectionMatrix,
            Matrix4x4 previousHiZInverseViewMatrix,
            bool previousHiZFrameValid,
            uint screenWidth,
            uint screenHeight)
        {
            return new GPUMeshletTaskFrameData
            {
                FrustumPlane0 = frustum.Left,
                FrustumPlane1 = frustum.Right,
                FrustumPlane2 = frustum.Bottom,
                FrustumPlane3 = frustum.Top,
                FrustumPlane4 = frustum.Near,
                FrustumPlane5 = frustum.Far,
                ViewProjectionMatrix = viewProjectionMatrix,
                InverseViewMatrix = inverseViewMatrix,
                PreviousHiZViewProjectionMatrix = previousHiZViewProjectionMatrix,
                PreviousHiZInverseViewMatrix = previousHiZInverseViewMatrix,
                ScreenDimensions = new Vector2(screenWidth, screenHeight),
                PreviousHiZFrameValid = previousHiZFrameValid ? 1u : 0u
            };
        }

        private static bool IsPreviousHiZCameraHistoryUsable(
            Matrix4x4 currentViewProjectionMatrix,
            Matrix4x4 previousViewProjectionMatrix)
        {
            float maxDelta = 0.0f;
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M11 - previousViewProjectionMatrix.M11));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M12 - previousViewProjectionMatrix.M12));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M13 - previousViewProjectionMatrix.M13));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M14 - previousViewProjectionMatrix.M14));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M21 - previousViewProjectionMatrix.M21));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M22 - previousViewProjectionMatrix.M22));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M23 - previousViewProjectionMatrix.M23));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M24 - previousViewProjectionMatrix.M24));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M31 - previousViewProjectionMatrix.M31));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M32 - previousViewProjectionMatrix.M32));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M33 - previousViewProjectionMatrix.M33));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M34 - previousViewProjectionMatrix.M34));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M41 - previousViewProjectionMatrix.M41));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M42 - previousViewProjectionMatrix.M42));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M43 - previousViewProjectionMatrix.M43));
            maxDelta = MathF.Max(maxDelta, MathF.Abs(currentViewProjectionMatrix.M44 - previousViewProjectionMatrix.M44));
            return float.IsFinite(maxDelta) && maxDelta <= 16.0f;
        }

        private ulong UploadSpan<T>(ReadOnlySpan<T> data, SceneBuffer destination, CommandBuffer commandBuffer, SceneUploadCategory category)
            where T : unmanaged
        {
            if (data.IsEmpty)
                return 0;

            ulong dataSize = checked((ulong)data.Length * (ulong)sizeof(T));
            if (dataSize > destination.ByteSize)
                throw new InvalidOperationException("Scene upload exceeds destination buffer capacity.");

            GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                destination.Handle,
                data);

            _lastUploadedBytes += dataSize;
            _lastSceneUploadCount++;
            AddUploadBytes(category, dataSize);
            return dataSize;
        }

        private void UploadSpanIfNeeded<T>(
            ReadOnlySpan<T> data,
            SceneBuffer destination,
            ref UploadState uploadState,
            bool contentChanged,
            CommandBuffer commandBuffer,
            SceneUploadCategory category)
            where T : unmanaged
        {
            if (!contentChanged && uploadState.Matches(data.Length))
            {
                _lastSceneUploadSkipped++;
                return;
            }

            UploadSpan(data, destination, commandBuffer, category);
            uploadState = UploadState.Valid(data.Length);
        }

        private void InvalidateDrawStreamUploadStates()
        {
            foreach (SceneBufferStream<GPUMeshletDrawCommand> stream in _meshletDrawStreams)
                stream.InvalidateAllUploadStates();
            foreach (SceneBufferStream<GPUPackedMeshletDrawCommand> stream in _packedMeshletDrawStreams)
                stream.InvalidateAllUploadStates();
            _instanceCandidateStream.InvalidateAllUploadStates();
        }

        private void MarkInstanceUploadFramesDirty()
        {
            for (int i = 0; i < _instanceUploadDirtyFrames.Length; i++)
                _instanceUploadDirtyFrames[i] = true;
        }

        private void AddUploadBytes(SceneUploadCategory category, ulong dataSize)
        {
            switch (category)
            {
                case SceneUploadCategory.Object:
                    _lastObjectUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.Instance:
                    _lastInstanceUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.InstanceCandidate:
                    _lastInstanceCandidateUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.MeshletDraw:
                    _lastMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.SolidDepthMeshletDraw:
                    _lastSolidDepthMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.MaskedDepthMeshletDraw:
                    _lastMaskedDepthMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.PackedMeshletDraw:
                    _lastPackedMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.PackedSolidDepthMeshletDraw:
                    _lastPackedSolidDepthMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.PackedMaskedDepthMeshletDraw:
                    _lastPackedMaskedDepthMeshletDrawUploadBytes += dataSize;
                    break;
                case SceneUploadCategory.MeshletTaskFrameData:
                    break;
                case SceneUploadCategory.TransparentMeshletDraw:
                    _lastTransparentMeshletDrawUploadBytes += dataSize;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(category), category, null);
            }
        }

        private void ClearTiledLightBuffers(CommandBuffer commandBuffer, uint totalClusters)
        {
            ulong headerBytes = checked(
                (totalClusters + TiledLightDenseControlHeaderCount) *
                TiledLightHeaderStride);
            _lastTiledLightHeaderBufferClearBytes = headerBytes;
            _lastTiledLightIndexBufferClearBytes = 0;

            if (headerBytes > 0)
                _context.Api.CmdFillBuffer(commandBuffer, _bufferManager.GetBuffer(_tiledLightHeaderBuffer.Handle), 0, headerBytes, 0);
        }

        private void RecordUploadReadBarriers(CommandBuffer commandBuffer, int frameIndex, bool includeTiledLightBuffers)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[27];
            barriers[0] = CreateShaderReadBarrier(_objectDataBuffer.Handle);
            barriers[1] = CreateShaderReadBarrier(_instanceBuffers[frameIndex].Handle);
            barriers[2] = CreateShaderReadBarrier(_meshletDrawBuffers[frameIndex].Handle);
            barriers[3] = CreateShaderReadBarrier(_transparentMeshletDrawBuffers[frameIndex].Handle);

            uint barrierCount = 4;
            barriers[barrierCount++] = CreateShaderReadBarrier(_simpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_fullOpaqueMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_solidDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_maskedDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedSimpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedFullOpaqueMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedSolidDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedMaskedDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_meshletTaskFrameDataBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_directionalShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_localShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_directionalStaticShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_directionalDynamicShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_localStaticShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_localDynamicShadowMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(
                _instanceCandidateBuffers[frameIndex].Handle);

            if (includeTiledLightBuffers)
            {
                barriers[barrierCount++] = CreateComputeWriteBarrier(_tiledLightHeaderBuffer.Handle);
                barriers[barrierCount++] = CreateComputeWriteBarrier(_tiledLightIndexBuffer.Handle);
            }

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = barrierCount,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateShaderReadBarrier(BufferHandle handle)
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
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private BufferMemoryBarrier2 CreateComputeWriteBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap == null)
                return;

            RegisterStorageBuffer(BindlessIndex.ObjectDataBuffer, _objectDataBuffer.Handle);
            RegisterStorageBuffer(BindlessIndex.InstanceBufferBase, _instanceBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.InstanceBufferFrame1, _instanceBuffers[1].Handle);
            RegisterStorageBuffer(
                BindlessIndex.SceneInstanceCandidateBufferBase,
                _instanceCandidateBuffers[0].Handle);
            RegisterStorageBuffer(
                BindlessIndex.SceneInstanceCandidateBufferFrame1,
                _instanceCandidateBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferBase, _meshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferFrame1, _meshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferBase, _simpleNormalOpaqueMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SimpleNormalOpaqueMeshletDrawBufferFrame1, _simpleNormalOpaqueMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.FullOpaqueMeshletDrawBufferBase, _fullOpaqueMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.FullOpaqueMeshletDrawBufferFrame1, _fullOpaqueMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.SolidDepthMeshletDrawBufferBase, _solidDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SolidDepthMeshletDrawBufferFrame1, _solidDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.MaskedDepthMeshletDrawBufferBase, _maskedDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MaskedDepthMeshletDrawBufferFrame1, _maskedDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMeshletDrawBufferBase, _packedMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMeshletDrawBufferFrame1, _packedMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferBase, _packedSimpleNormalOpaqueMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedSimpleNormalOpaqueMeshletDrawBufferFrame1, _packedSimpleNormalOpaqueMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedFullOpaqueMeshletDrawBufferBase, _packedFullOpaqueMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedFullOpaqueMeshletDrawBufferFrame1, _packedFullOpaqueMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedSolidDepthMeshletDrawBufferBase, _packedSolidDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedSolidDepthMeshletDrawBufferFrame1, _packedSolidDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMaskedDepthMeshletDrawBufferBase, _packedMaskedDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMaskedDepthMeshletDrawBufferFrame1, _packedMaskedDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletTaskFrameDataBufferBase, _meshletTaskFrameDataBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletTaskFrameDataBufferFrame1, _meshletTaskFrameDataBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.TransparentMeshletDrawBufferBase, _transparentMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.TransparentMeshletDrawBufferFrame1, _transparentMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalShadowMeshletDrawBufferBase, _directionalShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalShadowMeshletDrawBufferBase + 1, _directionalShadowMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalShadowMeshletDrawBufferBase, _localShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalShadowMeshletDrawBufferBase + 1, _localShadowMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalStaticShadowMeshletDrawBufferBase, _directionalStaticShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalStaticShadowMeshletDrawBufferFrame1, _directionalStaticShadowMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferBase, _directionalDynamicShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.DirectionalDynamicShadowMeshletDrawBufferFrame1, _directionalDynamicShadowMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalStaticShadowMeshletDrawBufferBase, _localStaticShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalStaticShadowMeshletDrawBufferFrame1, _localStaticShadowMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalDynamicShadowMeshletDrawBufferBase, _localDynamicShadowMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.LocalDynamicShadowMeshletDrawBufferFrame1, _localDynamicShadowMeshletDrawBuffers[1].Handle);
            // Partially-bound descriptors are legal, but retaining descriptors to
            // released buffers is not. When local-light culling is inactive, bind the
            // slots to an always-live buffer; the forward shader gates all reads on
            // LocalLightCount and the tiled-light pass returns before binding them.
            RegisterStorageBuffer(
                BindlessIndex.TiledLightHeaderBuffer,
                _tiledLightHeaderBuffer.Handle.IsValid ? _tiledLightHeaderBuffer.Handle : _objectDataBuffer.Handle);
            RegisterStorageBuffer(
                BindlessIndex.TiledLightIndicesBuffer,
                _tiledLightIndexBuffer.Handle.IsValid ? _tiledLightIndexBuffer.Handle : _objectDataBuffer.Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _registeredBindlessHeap!.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        private int[] CopyPointShadowFaceMasks(int pointShadowCount)
        {
            int count = Math.Min(Math.Max(0, pointShadowCount), _pointShadowFaceMasks.Length);
            if (count == 0)
                return [];

            var masks = new int[count];
            Array.Copy(_pointShadowFaceMasks, masks, count);
            return masks;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private static AnimationSceneStats CountAnimationSceneStats(Scene scene)
        {
            var animatedModels = new HashSet<object>();
            int skinnedObjectCount = 0;
            int skeletonCount = 0;
            int skinCount = 0;
            int clipCount = 0;
            int activeAnimatorCount = 0;
            int playingAnimatorCount = 0;
            int pausedAnimatorCount = 0;
            int jointMatrixCount = 0;

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (renderObject is not SkinnedRenderObject skinned)
                    continue;

                skinnedObjectCount++;
                animatedModels.Add(skinned.Mesh ?? skinned);

                Animator? animator = skinned.Animator;
                if (animator == null)
                    continue;

                activeAnimatorCount++;
                skeletonCount += animator.Skeleton.Joints.Count > 0 ? 1 : 0;
                skinCount += animator.Skins.Count;
                clipCount += animator.Clips.Count;
                jointMatrixCount += animator.Skeleton.Joints.Count;
                if (animator.IsPlaying)
                    playingAnimatorCount++;
                if (animator.IsPaused)
                    pausedAnimatorCount++;
            }

            return new AnimationSceneStats(
                animatedModels.Count,
                skinnedObjectCount,
                skeletonCount,
                skinCount,
                clipCount,
                activeAnimatorCount,
                playingAnimatorCount,
                pausedAnimatorCount,
                jointMatrixCount);
        }

        private static int CountFoliagePrototypes(Scene scene)
        {
            int count = scene.FoliagePrototypes.Count;
            foreach (var patch in scene.FoliagePatches)
            {
                bool alreadyCounted = false;
                for (int i = 0; i < scene.FoliagePrototypes.Count; i++)
                {
                    if (ReferenceEquals(scene.FoliagePrototypes[i], patch.Prototype))
                    {
                        alreadyCounted = true;
                        break;
                    }
                }

                if (!alreadyCounted)
                    count++;
            }

            return count;
        }

        private static int GetScenePayloadObjectCount(Scene scene)
        {
            return scene.RenderObjects.Count +
                   scene.StaticInstanceBatches.Count +
                   scene.FoliagePrototypes.Count +
                   scene.FoliagePatches.Count;
        }

        public BufferHandle ObjectDataBuffer => _objectDataBuffer.Handle;
        public BufferHandle MaterialDataBuffer => _materialManager.MaterialBuffer;
        public BufferHandle ForwardMaterialDataBuffer =>
            _materialManager.ForwardMaterialBuffer;
        public BufferHandle MaterialExtensionDataBuffer => _materialManager.MaterialExtensionBuffer;
        public BufferHandle TiledLightHeaderBuffer => _tiledLightHeaderBuffer.Handle;
        public BufferHandle TiledLightIndexBuffer => _tiledLightIndexBuffer.Handle;

        public ulong ObjectBufferSize => _objectDataBuffer.ByteSize;
        public ulong MaterialBufferSize => _materialManager.MaterialBufferSize;
        public ulong ForwardMaterialBufferSize =>
            _materialManager.ForwardMaterialBufferSize;
        public ulong MaterialExtensionBufferSize => _materialManager.MaterialExtensionBufferSize;
        public ulong TiledLightHeaderBufferSize => _tiledLightHeaderBuffer.ByteSize;
        public ulong TiledLightIndexBufferSize => _tiledLightIndexBuffer.ByteSize;
        public ulong LastUploadedBytes => _lastUploadedBytes;
        public int LastSceneUploadCount => _lastSceneUploadCount;
        public int LastSceneUploadSkipped => _lastSceneUploadSkipped;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;
        public uint LastTileCountX => _lastTileCountX;
        public uint LastTileCountY => _lastTileCountY;
        public uint LastClusterCountZ => _lastClusterCountZ;

        public BufferHandle GetInstanceBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _instanceBuffers[frameIndex].Handle;
        }

        public BufferHandle GetMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _meshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetFullOpaqueMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _fullOpaqueMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetSimpleNormalOpaqueMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _simpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetSolidDepthMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _solidDepthMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetMaskedDepthMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _maskedDepthMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetPackedMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetPackedFullOpaqueMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedFullOpaqueMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetPackedSimpleNormalOpaqueMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedSimpleNormalOpaqueMeshletDrawBuffers[frameIndex].Handle;
        }

        public BufferHandle GetTransparentMeshletDrawBuffer(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _transparentMeshletDrawBuffers[frameIndex].Handle;
        }

        public ulong GetInstanceBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _instanceBuffers[frameIndex].ByteSize;
        }

        public ulong GetMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _meshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetFullOpaqueMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _fullOpaqueMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetSimpleNormalOpaqueMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _simpleNormalOpaqueMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetSolidDepthMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _solidDepthMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetMaskedDepthMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _maskedDepthMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetPackedMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetPackedFullOpaqueMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedFullOpaqueMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetPackedSimpleNormalOpaqueMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _packedSimpleNormalOpaqueMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public ulong GetTransparentMeshletDrawBufferSize(int frameIndex)
        {
            ValidateFrameIndex(frameIndex);
            return _transparentMeshletDrawBuffers[frameIndex].ByteSize;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;
            _disposed = true;

            lock (_lock)
            {
                DestroyIfValid(_objectDataBuffer.Handle);
                DestroyIfValid(_tiledLightHeaderBuffer.Handle);
                DestroyIfValid(_tiledLightIndexBuffer.Handle);

                for (int i = 0; i < FramesInFlight; i++)
                {
                    DestroyIfValid(_instanceBuffers[i].Handle);
                    DestroyIfValid(_instanceCandidateBuffers[i].Handle);
                    DestroyIfValid(_meshletDrawBuffers[i].Handle);
                    DestroyIfValid(_simpleNormalOpaqueMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_fullOpaqueMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_solidDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_maskedDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedSimpleNormalOpaqueMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedFullOpaqueMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedSolidDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedMaskedDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_meshletTaskFrameDataBuffers[i].Handle);
                    DestroyIfValid(_transparentMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_directionalShadowMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_localShadowMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_directionalStaticShadowMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_directionalDynamicShadowMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_localStaticShadowMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_localDynamicShadowMeshletDrawBuffers[i].Handle);
                }
                _directionalShadowMeshletDrawCommands.Clear();
                _localShadowMeshletDrawCommands.Clear();
                _directionalStaticShadowMeshletDrawCommands.Clear();
                _directionalDynamicShadowMeshletDrawCommands.Clear();
                _localStaticShadowMeshletDrawCommands.Clear();
                _localDynamicShadowMeshletDrawCommands.Clear();
                _drawPacketSet = DrawPacketSet.Empty;

                _objectData.Clear();
                _instanceCandidates.Clear();
                _meshletDrawCommands.Clear();
                _simpleNormalOpaqueMeshletDrawCommands.Clear();
                _fullOpaqueMeshletDrawCommands.Clear();
                _solidDepthMeshletDrawCommands.Clear();
                _maskedDepthMeshletDrawCommands.Clear();
                _packedMeshletDrawCommands.Clear();
                _packedSimpleNormalOpaqueMeshletDrawCommands.Clear();
                _packedFullOpaqueMeshletDrawCommands.Clear();
                _packedSolidDepthMeshletDrawCommands.Clear();
                _packedMaskedDepthMeshletDrawCommands.Clear();
                _meshletTaskFrameData.Clear();
                _transparentMeshletDrawCommands.Clear();
                _transparentMaterialRuns.Clear();
                _transparentSortScratch.Clear();
                _previousRenderObjectLods.Clear();
                _previousStaticInstanceLods.Clear();
                _hasCachedPayload = false;
            }

            System.Diagnostics.Debug.WriteLine("Scene data builder disposed.");
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private readonly struct SceneBuffer
        {
            public static SceneBuffer Unallocated(string debugName)
            {
                return new SceneBuffer(BufferHandle.Invalid, 0, 0, debugName);
            }

            public SceneBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize, string debugName)
            {
                Handle = handle;
                ElementCapacity = elementCapacity;
                ByteSize = byteSize;
                DebugName = debugName;
            }

            public BufferHandle Handle { get; }
            public uint ElementCapacity { get; }
            public ulong ByteSize { get; }
            public string DebugName { get; }
        }

        private sealed class SceneBufferStream<T>
            where T : unmanaged
        {
            private readonly List<T> _items;
            private readonly SceneBuffer[] _buffers;
            private readonly UploadState[] _uploadStates;
            private readonly ulong _stride;
            private readonly SceneUploadCategory _category;

            public SceneBufferStream(
                List<T> items,
                SceneBuffer[] buffers,
                UploadState[] uploadStates,
                ulong stride,
                SceneUploadCategory category)
            {
                _items = items ?? throw new ArgumentNullException(nameof(items));
                _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
                _uploadStates = uploadStates ?? throw new ArgumentNullException(nameof(uploadStates));
                _stride = stride == 0 ? throw new ArgumentOutOfRangeException(nameof(stride)) : stride;
                _category = category;
            }

            public void EnsureCapacity(SceneDataBuilder owner, int frameIndex, CommandBuffer commandBuffer)
            {
                if (owner.EnsureCapacity(ref _buffers[frameIndex], CheckedCount(_items.Count), _stride, commandBuffer))
                    _uploadStates[frameIndex] = default;
            }

            public void InvalidateAllUploadStates()
            {
                Array.Clear(_uploadStates, 0, _uploadStates.Length);
            }

            public void UploadIfNeeded(SceneDataBuilder owner, int frameIndex, bool contentChanged, CommandBuffer commandBuffer)
            {
                owner.UploadSpanIfNeeded(
                    CollectionsMarshal.AsSpan(_items),
                    _buffers[frameIndex],
                    ref _uploadStates[frameIndex],
                    contentChanged,
                    commandBuffer,
                    _category);
            }
        }

        private readonly struct TransparentMeshletDraw
        {
            public TransparentMeshletDraw(
                GPUMeshletDrawCommand command,
                float distanceSquared,
                int layer,
                TransparentDrawClassification classification)
            {
                Command = command;
                DistanceSquared = distanceSquared;
                Layer = layer;
                Classification = classification;
            }

            public GPUMeshletDrawCommand Command { get; }
            public float DistanceSquared { get; }
            public int Layer { get; }
            public TransparentDrawClassification Classification { get; }
        }

        private readonly struct StaticInstanceKey : IEquatable<StaticInstanceKey>
        {
            private readonly StaticInstanceBatch _batch;
            private readonly int _instanceIndex;

            public StaticInstanceKey(StaticInstanceBatch batch, int instanceIndex)
            {
                _batch = batch;
                _instanceIndex = instanceIndex;
            }

            public bool Equals(StaticInstanceKey other)
            {
                return ReferenceEquals(_batch, other._batch) && _instanceIndex == other._instanceIndex;
            }

            public override bool Equals(object? obj)
            {
                return obj is StaticInstanceKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(RuntimeHelpers.GetHashCode(_batch), _instanceIndex);
            }
        }

        private readonly struct MeshletLodRange
        {
            public MeshletLodRange(uint offset, uint count)
            {
                Offset = offset;
                Count = count;
            }

            public uint Offset { get; }
            public uint Count { get; }
        }

        private readonly struct UploadState
        {
            private readonly int _elementCount;
            private readonly bool _valid;

            private UploadState(int elementCount, bool valid)
            {
                _elementCount = elementCount;
                _valid = valid;
            }

            public static UploadState Valid(int elementCount)
            {
                return new UploadState(elementCount, valid: true);
            }

            public bool Matches(int elementCount)
            {
                return _valid && _elementCount == elementCount;
            }
        }

        private enum SceneUploadCategory
        {
            Object,
            Instance,
            InstanceCandidate,
            MeshletDraw,
            SolidDepthMeshletDraw,
            MaskedDepthMeshletDraw,
            PackedMeshletDraw,
            PackedSolidDepthMeshletDraw,
            PackedMaskedDepthMeshletDraw,
            MeshletTaskFrameData,
            TransparentMeshletDraw
        }

        private readonly record struct AnimationSceneStats(
            int AnimatedModelCount,
            int SkinnedObjectCount,
            int SkeletonCount,
            int SkinCount,
            int AnimationClipCount,
            int ActiveAnimatorCount,
            int PlayingAnimatorCount,
            int PausedAnimatorCount,
            int JointMatrixCount);

        private readonly struct StaticScenePayloadSignature : IEquatable<StaticScenePayloadSignature>
        {
            private readonly int _objectCount;
            private readonly int _hash;

            private StaticScenePayloadSignature(int objectCount, int hash)
            {
                _objectCount = objectCount;
                _hash = hash;
            }

            public static StaticScenePayloadSignature Create(
                Scene scene,
                uint materialDataRevision)
            {
                var hash = new HashCode();
                hash.Add(materialDataRevision);
                hash.Add(scene.RenderPayloadRevision);
                hash.Add(scene.RenderObjects.Count);
                hash.Add(scene.StaticInstanceBatches.Count);
                hash.Add(scene.FoliagePrototypes.Count);
                hash.Add(scene.FoliagePatches.Count);

                return new StaticScenePayloadSignature(GetScenePayloadObjectCount(scene), hash.ToHashCode());
            }

            public bool Equals(StaticScenePayloadSignature other)
            {
                return _objectCount == other._objectCount && _hash == other._hash;
            }

            public override bool Equals(object? obj)
            {
                return obj is StaticScenePayloadSignature other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_objectCount, _hash);
            }
        }

        private readonly struct SceneCullingSignature : IEquatable<SceneCullingSignature>
        {
            private readonly int _objectCount;
            private readonly int _hash;

            private SceneCullingSignature(int objectCount, int hash)
            {
                _objectCount = objectCount;
                _hash = hash;
            }

            public static SceneCullingSignature Create(Scene scene,
                Matrix4x4 viewProjection,
                GPUShadowData? directionalShadowData,
                int directionalShadowCascadeCount,
                bool buildLocalShadowMeshlets,
                ReadOnlySpan<SelectedLocalShadow> selectedPointShadows,
                TransparencySettings? transparencySettings,
                DecalSettings? decalSettings,
                bool geometryDecalsEnabled,
                int isolatedDecalMaterialIndex,
                bool captureCpuSnapshots,
                bool captureSceneSubmissionValidationLists,
                bool cameraDependentCpuPayload,
                bool useCpuMeshletFrustumCulling,
                bool meshletNormalConeCullingEnabled,
                bool buildGpuInstanceCandidates,
                float gpuLod1DistanceRatio,
                float gpuLod2DistanceRatio)
            {
                var hash = new HashCode();
                if (cameraDependentCpuPayload)
                    hash.Add(viewProjection);
                hash.Add(directionalShadowCascadeCount);
                hash.Add(buildLocalShadowMeshlets);
                hash.Add(selectedPointShadows.Length);
                if (cameraDependentCpuPayload)
                {
                    for (int i = 0; i < selectedPointShadows.Length; i++)
                    {
                        SelectedLocalShadow selected = selectedPointShadows[i];
                        hash.Add(selected.LightIndex);
                        hash.Add(selected.Light.Position);
                        hash.Add(selected.Light.Range);
                    }
                }
                hash.Add(captureCpuSnapshots);
                hash.Add(captureSceneSubmissionValidationLists);
                hash.Add(cameraDependentCpuPayload);
                hash.Add(useCpuMeshletFrustumCulling);
                hash.Add(meshletNormalConeCullingEnabled);
                hash.Add(buildGpuInstanceCandidates);
                if (cameraDependentCpuPayload)
                {
                    hash.Add(gpuLod1DistanceRatio);
                    hash.Add(gpuLod2DistanceRatio);
                }
                hash.Add(transparencySettings?.MaxTransparentMeshlets ?? int.MaxValue);
                hash.Add(transparencySettings?.SortPerMeshlet ?? true);
                hash.Add(transparencySettings?.Mode ??
                    TransparencyMode.SortedAlphaBlend);
                hash.Add(transparencySettings?.PipelinePartitioningEnabled ??
                    false);
                hash.Add(geometryDecalsEnabled);
                hash.Add(isolatedDecalMaterialIndex);
                if (cameraDependentCpuPayload && directionalShadowData.HasValue)
                {
                    GPUShadowData shadowData = directionalShadowData.Value;
                    hash.Add(shadowData.LightViewProjection0);
                    hash.Add(shadowData.LightViewProjection1);
                    hash.Add(shadowData.LightViewProjection2);
                    hash.Add(shadowData.LightViewProjection3);
                    hash.Add(shadowData.Indices);
                }
                hash.Add(scene.RenderObjects.Count);
                hash.Add(scene.StaticInstanceBatches.Count);
                hash.Add(scene.FoliagePrototypes.Count);
                hash.Add(scene.FoliagePatches.Count);
                hash.Add(scene.RenderPayloadRevision);

                return new SceneCullingSignature(GetScenePayloadObjectCount(scene), hash.ToHashCode());
            }

            public bool Equals(SceneCullingSignature other)
            {
                return _objectCount == other._objectCount && _hash == other._hash;
            }

            public override bool Equals(object? obj)
            {
                return obj is SceneCullingSignature other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_objectCount, _hash);
            }
        }
    }

    public struct Frustum
    {
        public Vector4 Left;
        public Vector4 Right;
        public Vector4 Bottom;
        public Vector4 Top;
        public Vector4 Near;
        public Vector4 Far;
    }
}

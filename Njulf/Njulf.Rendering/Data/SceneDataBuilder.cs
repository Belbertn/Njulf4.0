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
        private const int TileSize = 16;
        private const int MaxLightsPerTile = 128;

        private const uint InitialObjectCapacity = 4096;
        private const uint InitialInstanceCapacity = 4096;
        private const uint InitialOpaqueMeshletDrawCapacity = 65536;
        private const uint InitialDepthMeshletDrawCapacity = 32768;
        private const uint InitialMaskedDepthMeshletDrawCapacity = 8192;
        private const uint InitialTransparentMeshletDrawCapacity = 4096;
        private const uint InitialDirectionalShadowMeshletDrawCapacity = 32768;
        private const uint InitialLocalShadowMeshletDrawCapacity = 8192;
        private const uint InitialTileCapacity = 4096;
        private const uint CpuMeshletCullingThreshold = 128;
        private const float MeshletLod1DistanceRatio = 12f;
        private const float MeshletLod2DistanceRatio = 32f;
        private const float MeshletLodHysteresisFraction = 0.15f;

        private static readonly ulong ObjectStride = (ulong)Marshal.SizeOf<GPUObjectData>();
        private static readonly ulong MeshletDrawStride = (ulong)Marshal.SizeOf<GPUMeshletDrawCommand>();
        private static readonly ulong PackedMeshletDrawStride = (ulong)Marshal.SizeOf<GPUPackedMeshletDrawCommand>();
        private static readonly ulong MeshletTaskFrameDataStride = (ulong)Marshal.SizeOf<GPUMeshletTaskFrameData>();
        private static readonly ulong TiledLightHeaderStride = (ulong)Marshal.SizeOf<GPUTiledLightHeader>();
        private static readonly ulong TiledLightIndexStride = (ulong)Marshal.SizeOf<GPULightIndex>();

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
        private readonly SceneBuffer[] _meshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _fullOpaqueMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _solidDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _maskedDepthMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
        private readonly SceneBuffer[] _packedMeshletDrawBuffers = new SceneBuffer[FramesInFlight];
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
        private readonly List<GPUMeshletDrawCommand> _meshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _fullOpaqueMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _solidDepthMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUMeshletDrawCommand> _maskedDepthMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedFullOpaqueMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedSolidDepthMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUPackedMeshletDrawCommand> _packedMaskedDepthMeshletDrawCommands = new List<GPUPackedMeshletDrawCommand>();
        private readonly List<GPUMeshletTaskFrameData> _meshletTaskFrameData = new List<GPUMeshletTaskFrameData>(1);
        private readonly List<GPUMeshletDrawCommand> _transparentMeshletDrawCommands = new List<GPUMeshletDrawCommand>();
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
        private readonly UploadState[] _instanceUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _meshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _fullOpaqueMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _solidDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _maskedDepthMeshletDrawUploadStates = new UploadState[FramesInFlight];
        private readonly UploadState[] _packedMeshletDrawUploadStates = new UploadState[FramesInFlight];
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

        private BindlessHeap? _registeredBindlessHeap;
        private UploadState _objectUploadState;
        private StaticScenePayloadSignature _lastStaticPayloadSignature;
        private SceneCullingSignature _lastCullingSignature;
        private bool _hasCachedPayload;
        private uint _lastTileCountX;
        private uint _lastTileCountY;
        private ulong _lastUploadedBytes;
        private int _lastSceneUploadCount;
        private int _lastSceneUploadSkipped;
        private long _lastBuildMicroseconds;
        private int _opaqueObjectCount;
        private int _maskedObjectCount;
        private int _transparentObjectCount;
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
        private ulong _lastObjectUploadBytes;
        private ulong _lastInstanceUploadBytes;
        private ulong _lastMeshletDrawUploadBytes;
        private ulong _lastSolidDepthMeshletDrawUploadBytes;
        private ulong _lastMaskedDepthMeshletDrawUploadBytes;
        private ulong _lastPackedMeshletDrawUploadBytes;
        private ulong _lastPackedSolidDepthMeshletDrawUploadBytes;
        private ulong _lastPackedMaskedDepthMeshletDrawUploadBytes;
        private ulong _lastTransparentMeshletDrawUploadBytes;
        private int _submittedMeshletCountCpu;
        private ulong _submittedMeshletTriangleSum;
        private ulong _submittedMeshletVertexSum;
        private int _submittedSmallMeshletsUnder16Triangles;
        private int _submittedSmallMeshletsUnder32Triangles;
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
                _meshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, MeshletDrawStride, $"Opaque Meshlet Draw Buffer Frame {i}");
                _fullOpaqueMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, MeshletDrawStride, $"Full Opaque Meshlet Draw Buffer Frame {i}");
                _solidDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialDepthMeshletDrawCapacity, MeshletDrawStride, $"Solid Depth Meshlet Draw Buffer Frame {i}");
                _maskedDepthMeshletDrawBuffers[i] = CreateSceneBuffer(InitialMaskedDepthMeshletDrawCapacity, MeshletDrawStride, $"Masked Depth Meshlet Draw Buffer Frame {i}");
                _packedMeshletDrawBuffers[i] = CreateSceneBuffer(InitialOpaqueMeshletDrawCapacity, PackedMeshletDrawStride, $"Packed Opaque Meshlet Draw Buffer Frame {i}");
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

            _tiledLightHeaderBuffer = CreateSceneBuffer(InitialTileCapacity, TiledLightHeaderStride, "Tiled Light Header Buffer");
            _tiledLightIndexBuffer = CreateSceneBuffer(InitialTileCapacity * MaxLightsPerTile, TiledLightIndexStride, "Tiled Light Index Buffer");

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

        public SceneRenderingData Build(
            Scene scene,
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
            bool useCameraDependentCpuPayload = false,
            bool useCpuMeshletFrustumCulling = false)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (uploadCommandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for scene data upload.", nameof(uploadCommandBuffer));

            lock (_lock)
            {
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
                _lastObjectUploadBytes = 0;
                _lastInstanceUploadBytes = 0;
                _lastMeshletDrawUploadBytes = 0;
                _lastSolidDepthMeshletDrawUploadBytes = 0;
                _lastMaskedDepthMeshletDrawUploadBytes = 0;
                _lastPackedMeshletDrawUploadBytes = 0;
                _lastPackedSolidDepthMeshletDrawUploadBytes = 0;
                _lastPackedMaskedDepthMeshletDrawUploadBytes = 0;
                _lastTransparentMeshletDrawUploadBytes = 0;

                Matrix4x4 viewMatrix = camera.ViewMatrix;
                Matrix4x4 projectionMatrix = ApplyProjectionJitter(camera.ProjectionMatrix, projectionJitter);
                Matrix4x4 viewProjectionMatrix = viewMatrix * projectionMatrix;
                Matrix4x4 inverseViewMatrix = viewMatrix.Invert();
                Matrix4x4 inverseProjectionMatrix = projectionMatrix.Invert();
                Matrix4x4 inverseViewProjectionMatrix = viewProjectionMatrix.Invert();
                Frustum frustum = ExtractFrustum(viewProjectionMatrix);
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
                    CaptureCpuSnapshots,
                    cameraDependentCpuPayload,
                    useCpuMeshletFrustumCulling);
                _lastPayloadSignatureMicroseconds = ElapsedMicroseconds(signatureStart);
                bool staticPayloadChanged = !_hasCachedPayload || !_lastStaticPayloadSignature.Equals(staticPayloadSignature);
                bool cullingPayloadChanged = staticPayloadChanged || !_lastCullingSignature.Equals(cullingSignature);
                bool payloadRebuilt = false;
                if (cullingPayloadChanged)
                {
                    if (staticPayloadChanged)
                        _objectData.Clear();
                    _meshletDrawCommands.Clear();
                    _fullOpaqueMeshletDrawCommands.Clear();
                    _solidDepthMeshletDrawCommands.Clear();
                    _maskedDepthMeshletDrawCommands.Clear();
                    _packedMeshletDrawCommands.Clear();
                    _packedFullOpaqueMeshletDrawCommands.Clear();
                    _packedSolidDepthMeshletDrawCommands.Clear();
                    _packedMaskedDepthMeshletDrawCommands.Clear();
                    _transparentMeshletDrawCommands.Clear();
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

                    BuildCpuScenePayload(
                        scene,
                        camera.Position,
                        frustum,
                        directionalShadowData,
                        directionalShadowCascadeCount,
                        buildLocalShadowMeshlets,
                        selectedPointShadows,
                        rebuildObjectData: staticPayloadChanged,
                        geometryDecalsEnabled: decalSettings?.GeometryDecalsEnabled ?? true,
                        maxTransparentMeshlets: transparencySettings?.MaxTransparentMeshlets ?? int.MaxValue,
                        useCameraDependentCpuPayload: cameraDependentCpuPayload,
                        useCpuMeshletFrustumCulling: useCpuMeshletFrustumCulling);
                    _lastStaticPayloadSignature = staticPayloadSignature;
                    _lastCullingSignature = cullingSignature;
                    _hasCachedPayload = true;
                    payloadRebuilt = true;
                    _lastScenePayloadRebuilt = 1;
                }

                long materialUploadStart = Stopwatch.GetTimestamp();
                _materialManager.UploadMaterials(uploadCommandBuffer);
                long materialUploadMicroseconds = ElapsedMicroseconds(materialUploadStart);

                uint tileCountX = Math.Max(1u, DivideRoundUp(screenWidth, TileSize));
                uint tileCountY = Math.Max(1u, DivideRoundUp(screenHeight, TileSize));
                uint totalTiles = checked(tileCountX * tileCountY);
                _lastTileCountX = tileCountX;
                _lastTileCountY = tileCountY;

                long uploadStart = Stopwatch.GetTimestamp();
                if (EnsureCapacity(ref _objectDataBuffer, CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                    _objectUploadState = default;
                if (EnsureCapacity(ref _instanceBuffers[frameIndex], CheckedCount(_objectData.Count), ObjectStride, uploadCommandBuffer))
                    _instanceUploadStates[frameIndex] = default;
                foreach (SceneBufferStream<GPUMeshletDrawCommand> stream in _meshletDrawStreams)
                    stream.EnsureCapacity(this, frameIndex, uploadCommandBuffer);
                foreach (SceneBufferStream<GPUPackedMeshletDrawCommand> stream in _packedMeshletDrawStreams)
                    stream.EnsureCapacity(this, frameIndex, uploadCommandBuffer);
                EnsureCapacity(ref _meshletTaskFrameDataBuffers[frameIndex], 1, MeshletTaskFrameDataStride, uploadCommandBuffer);
                if (useTiledLightCulling)
                {
                    EnsureCapacity(ref _tiledLightHeaderBuffer, totalTiles, TiledLightHeaderStride, uploadCommandBuffer);
                    EnsureCapacity(ref _tiledLightIndexBuffer, checked(totalTiles * MaxLightsPerTile), TiledLightIndexStride, uploadCommandBuffer);
                }

                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _objectDataBuffer, ref _objectUploadState, staticPayloadChanged, uploadCommandBuffer, SceneUploadCategory.Object);
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_objectData), _instanceBuffers[frameIndex], ref _instanceUploadStates[frameIndex], contentChanged: true, uploadCommandBuffer, SceneUploadCategory.Instance);
                foreach (SceneBufferStream<GPUMeshletDrawCommand> stream in _meshletDrawStreams)
                    stream.UploadIfNeeded(this, frameIndex, payloadRebuilt, uploadCommandBuffer);
                foreach (SceneBufferStream<GPUPackedMeshletDrawCommand> stream in _packedMeshletDrawStreams)
                    stream.UploadIfNeeded(this, frameIndex, payloadRebuilt, uploadCommandBuffer);
                _meshletTaskFrameData.Clear();
                _meshletTaskFrameData.Add(CreateMeshletTaskFrameData(frustum));
                UploadSpanIfNeeded(CollectionsMarshal.AsSpan(_meshletTaskFrameData), _meshletTaskFrameDataBuffers[frameIndex], ref _meshletTaskFrameDataUploadStates[frameIndex], contentChanged: true, uploadCommandBuffer, SceneUploadCategory.MeshletTaskFrameData);

                if (useTiledLightCulling)
                    ClearTiledLightBuffers(uploadCommandBuffer, totalTiles);

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
                ulong materialExtensionUploadBytes = _materialManager.LastExtensionUploadBytes;
                ulong uploadedBytes = _lastUploadedBytes + materialUploadBytes + materialExtensionUploadBytes;
                AnimationSceneStats animationStats = CountAnimationSceneStats(scene);

                var sceneData = new SceneRenderingData
                {
                    ObjectCount = _opaqueObjectCount + _maskedObjectCount + _transparentObjectCount + _geometryDecalObjectCount,
                    MeshletCount = _meshletDrawCommands.Count + _fullOpaqueMeshletDrawCommands.Count + _transparentMeshletDrawCommands.Count,
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
                    SolidObjectCount = _opaqueObjectCount,
                    GeometryDecalObjectCount = _geometryDecalObjectCount,
                    OpaqueMeshletCount = _meshletDrawCommands.Count + _fullOpaqueMeshletDrawCommands.Count,
                    SimpleOpaqueMeshletCount = _meshletDrawCommands.Count,
                    FullOpaqueMeshletCount = _fullOpaqueMeshletDrawCommands.Count,
                    SolidMeshletCount = _solidDepthMeshletDrawCommands.Count,
                    MaskedMeshletCount = _maskedDepthMeshletDrawCommands.Count,
                    TransparentMeshletCount = _transparentMeshletDrawCommands.Count,
                    GeometryDecalMeshletCount = _geometryDecalMeshletCount,
                    BlendMaterialCount = _blendMaterialCount,
                    MaskMaterialCount = _maskMaterialCount,
                    GeometryDecalMaterialCount = _geometryDecalMaterialCount,
                    TransparentSortCandidateCount = _transparentSortScratch.Count,
                    TransparentSortMicroseconds = _transparentSortMicroseconds,
                    TransparentOverflowCount = _transparentOverflowCount,
                    MaterialCount = _materialManager.RegisteredMaterialCount,
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
                    MeshletCountTotal = globalMeshletStats.MeshletCount,
                    MeshletCountSubmittedCpu = _submittedMeshletCountCpu,
                    AvgTrianglesPerSubmittedMeshlet = avgSubmittedTriangles,
                    AvgVerticesPerSubmittedMeshlet = avgSubmittedVertices,
                    SmallMeshletsUnder16Triangles = _submittedSmallMeshletsUnder16Triangles,
                    SmallMeshletsUnder32Triangles = _submittedSmallMeshletsUnder32Triangles,
                    ScenePayloadRebuilt = _lastScenePayloadRebuilt,
                    ObjectUploadBytes = _lastObjectUploadBytes,
                    InstanceUploadBytes = _lastInstanceUploadBytes,
                    MeshletDrawUploadBytes = _lastMeshletDrawUploadBytes,
                    SolidDepthMeshletDrawUploadBytes = _lastSolidDepthMeshletDrawUploadBytes,
                    MaskedDepthMeshletDrawUploadBytes = _lastMaskedDepthMeshletDrawUploadBytes,
                    PackedMeshletDrawUploadBytes = _lastPackedMeshletDrawUploadBytes,
                    PackedSolidDepthMeshletDrawUploadBytes = _lastPackedSolidDepthMeshletDrawUploadBytes,
                    PackedMaskedDepthMeshletDrawUploadBytes = _lastPackedMaskedDepthMeshletDrawUploadBytes,
                    TransparentMeshletDrawUploadBytes = _lastTransparentMeshletDrawUploadBytes,
                    MaterialUploadBytes = materialUploadBytes,
                    MaterialExtensionUploadBytes = materialExtensionUploadBytes,
                    ObjectBufferSize = _objectDataBuffer.ByteSize,
                    MaterialBufferSize = _materialManager.MaterialBufferSize,
                    MaterialExtensionBufferSize = _materialManager.MaterialExtensionBufferSize,
                    InstanceBufferSize = _instanceBuffers[frameIndex].ByteSize,
                    MeshletDrawBufferSize = _meshletDrawBuffers[frameIndex].ByteSize,
                    FullOpaqueMeshletDrawBufferSize = _fullOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    SolidDepthMeshletDrawBufferSize = _solidDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    MaskedDepthMeshletDrawBufferSize = _maskedDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedMeshletDrawBufferSize = _packedMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedFullOpaqueMeshletDrawBufferSize = _packedFullOpaqueMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedSolidDepthMeshletDrawBufferSize = _packedSolidDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    PackedMaskedDepthMeshletDrawBufferSize = _packedMaskedDepthMeshletDrawBuffers[frameIndex].ByteSize,
                    MeshletTaskFrameDataBufferSize = _meshletTaskFrameDataBuffers[frameIndex].ByteSize,
                    TransparentMeshletDrawBufferSize = _transparentMeshletDrawBuffers[frameIndex].ByteSize,
                    DirectionalShadowMeshletDrawBufferSize = _directionalShadowMeshletDrawBuffers[frameIndex].ByteSize,
                    LocalShadowMeshletDrawBufferSize = _localShadowMeshletDrawBuffers[frameIndex].ByteSize,
                    TiledLightHeaderBufferSize = _tiledLightHeaderBuffer.ByteSize,
                    TiledLightIndexBufferSize = _tiledLightIndexBuffer.ByteSize,
                    ObjectDataBuffer = _objectDataBuffer.Handle,
                    MaterialDataBuffer = _materialManager.MaterialBuffer,
                    MaterialExtensionDataBuffer = _materialManager.MaterialExtensionBuffer,
                    InstanceBuffer = _instanceBuffers[frameIndex].Handle,
                    MeshletDrawBuffer = _meshletDrawBuffers[frameIndex].Handle,
                    FullOpaqueMeshletDrawBuffer = _fullOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    SolidDepthMeshletDrawBuffer = _solidDepthMeshletDrawBuffers[frameIndex].Handle,
                    MaskedDepthMeshletDrawBuffer = _maskedDepthMeshletDrawBuffers[frameIndex].Handle,
                    PackedMeshletDrawBuffer = _packedMeshletDrawBuffers[frameIndex].Handle,
                    PackedFullOpaqueMeshletDrawBuffer = _packedFullOpaqueMeshletDrawBuffers[frameIndex].Handle,
                    PackedSolidDepthMeshletDrawBuffer = _packedSolidDepthMeshletDrawBuffers[frameIndex].Handle,
                    PackedMaskedDepthMeshletDrawBuffer = _packedMaskedDepthMeshletDrawBuffers[frameIndex].Handle,
                    MeshletTaskFrameDataBuffer = _meshletTaskFrameDataBuffers[frameIndex].Handle,
                    TransparentMeshletDrawBuffer = _transparentMeshletDrawBuffers[frameIndex].Handle,
                    TiledLightHeaderBuffer = _tiledLightHeaderBuffer.Handle,
                    TiledLightIndexBuffer = _tiledLightIndexBuffer.Handle
                };

                if (CaptureCpuSnapshots)
                {
                    sceneData.HasCpuSnapshots = true;
                    sceneData.ObjectData.AddRange(_objectData);
                    sceneData.MaterialData.AddRange(_materialManager.GetMaterialDataSnapshot());
                    sceneData.MaterialExtensionData.AddRange(_materialManager.GetMaterialExtensionDataSnapshot());
                    sceneData.ObjectDebugSnapshots.AddRange(_objectDebugSnapshots);
                    sceneData.MeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_meshletDrawCommands);
                    sceneData.MeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    sceneData.OpaqueMeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    sceneData.FullOpaqueMeshletDrawCommands.AddRange(_fullOpaqueMeshletDrawCommands);
                    sceneData.SolidDepthMeshletDrawCommands.AddRange(_solidDepthMeshletDrawCommands);
                    sceneData.MaskedDepthMeshletDrawCommands.AddRange(_maskedDepthMeshletDrawCommands);
                    sceneData.PackedMeshletDrawCommands.AddRange(_packedMeshletDrawCommands);
                    sceneData.PackedFullOpaqueMeshletDrawCommands.AddRange(_packedFullOpaqueMeshletDrawCommands);
                    sceneData.PackedSolidDepthMeshletDrawCommands.AddRange(_packedSolidDepthMeshletDrawCommands);
                    sceneData.PackedMaskedDepthMeshletDrawCommands.AddRange(_packedMaskedDepthMeshletDrawCommands);
                    sceneData.TransparentMeshletDrawCommands.AddRange(_transparentMeshletDrawCommands);
                }

                for (int cascade = 0; cascade < ShadowSettings.MaxDirectionalCascades; cascade++)
                    sceneData.DirectionalShadowMeshletCounts[cascade] = _directionalShadowMeshletDrawCommands.Count;
                sceneData.LocalShadowMeshletCount = _localShadowMeshletDrawCommands.Count;
                sceneData.DirectionalStaticShadowMeshletCount = _directionalStaticShadowMeshletDrawCommands.Count;
                sceneData.DirectionalDynamicShadowMeshletCount = _directionalDynamicShadowMeshletDrawCommands.Count;
                sceneData.LocalStaticShadowMeshletCount = _localStaticShadowMeshletDrawCommands.Count;
                sceneData.LocalDynamicShadowMeshletCount = _localDynamicShadowMeshletDrawCommands.Count;
                sceneData.DirectionalShadowSkinnedObjectCount = _directionalShadowSkinnedObjectCount;
                sceneData.LocalShadowSkinnedObjectCount = _localShadowSkinnedObjectCount;
                sceneData.DirectionalShadowMeshletDrawSignature = HashMeshletDrawCommands(_directionalShadowMeshletDrawCommands);
                sceneData.LocalShadowMeshletDrawSignature = HashMeshletDrawCommands(_localShadowMeshletDrawCommands);
                sceneData.DirectionalStaticShadowMeshletDrawSignature = HashMeshletDrawCommands(_directionalStaticShadowMeshletDrawCommands);
                sceneData.DirectionalDynamicShadowMeshletDrawSignature = HashMeshletDrawCommands(_directionalDynamicShadowMeshletDrawCommands);
                sceneData.LocalStaticShadowMeshletDrawSignature = HashMeshletDrawCommands(_localStaticShadowMeshletDrawCommands);
                sceneData.LocalDynamicShadowMeshletDrawSignature = HashMeshletDrawCommands(_localDynamicShadowMeshletDrawCommands);
                sceneData.PointShadowFaceMasks = CopyPointShadowFaceMasks(selectedPointShadows.Length);

                AdvancePreviousWorldMatrices();
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

        private void AdvancePreviousWorldMatrices()
        {
            for (int i = 0; i < _objectData.Count; i++)
            {
                GPUObjectData objectData = _objectData[i];
                objectData.PreviousWorldMatrix = objectData.WorldMatrix;
                _objectData[i] = objectData;
            }
        }

        private void BuildCpuScenePayload(
            Scene scene,
            Vector3 cameraPosition,
            Frustum frustum,
            GPUShadowData? directionalShadowData,
            int directionalShadowCascadeCount,
            bool buildLocalShadowMeshlets,
            ReadOnlySpan<SelectedLocalShadow> selectedPointShadows,
            bool rebuildObjectData,
            bool geometryDecalsEnabled,
            int maxTransparentMeshlets,
            bool useCameraDependentCpuPayload,
            bool useCpuMeshletFrustumCulling)
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
                if (CaptureCpuSnapshots)
                {
                    var localBounds = new BoundingBox(
                        ToCoreVector(meshInfo.BoundingBoxMin),
                        ToCoreVector(meshInfo.BoundingBoxMax));
                    _objectDebugSnapshots.Add(new ObjectDebugSnapshot(
                        _objectDebugSnapshots.Count,
                        renderObject.Name ?? string.Empty,
                        meshHandle,
                        materialHandle,
                        renderObject.WorldMatrix,
                        TransformBoundingBox(localBounds, cullingMatrix),
                        renderObject.Visible && cameraVisible,
                        !renderObject.Visible || !cameraVisible));
                }

                if (cameraVisible)
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
                        PreviousWorldMatrix = GetPreviousWorldMatrix(renderObject, renderObject.WorldMatrix)
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
                    _objectData[objectDataIndex] = objectData;
                }
                _previousRenderObjectMatrices[renderObject] = renderObject.WorldMatrix;

                objectDataIndex++;
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
                    ? SelectMeshletLodLevel(cameraPosition, worldCenter, worldRadius, previousLodLevel)
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
                        Padding = 0
                    };

                    if (meshletVisibleToCamera)
                    {
                        RecordSubmittedMeshlet(meshlet);
                        RecordSubmittedMeshletLod(effectiveLodLevel);

                        if (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                        {
                            if (isGeometryDecal)
                                _geometryDecalMeshletCount++;
                            if (!isGeometryDecal || geometryDecalsEnabled)
                                AddTransparentDraw(command, transparentDistanceSquared, metadata.DecalLayer, maxTransparentMeshlets);
                        }
                        else
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

                    if (castsDirectionalShadow && objectIntersectsShadowCascade)
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
                    else
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
                            PreviousWorldMatrix = GetPreviousWorldMatrix(batch, instance, worldMatrix)
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
                        _objectData[objectDataIndex] = objectData;
                    }
                    _previousStaticInstanceMatrices[new StaticInstanceKey(batch, instance)] = worldMatrix;

                    objectDataIndex++;
                    Vector3 worldCenter = TransformPoint(localCenter, worldMatrix);
                    float worldRadius = localRadius * GetMaxScale(worldMatrix);
                    float transparentDistanceSquared = useCameraDependentCpuPayload && (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                        ? DistanceSquared(cameraPosition, worldCenter)
                        : 0f;
                    _lastObjectCullMicroseconds += ElapsedMicroseconds(objectStart);

                    long meshletStart = Stopwatch.GetTimestamp();
                    var staticInstanceKey = new StaticInstanceKey(batch, instance);
                    int previousLodLevel = _previousStaticInstanceLods.TryGetValue(staticInstanceKey, out int storedLodLevel)
                        ? storedLodLevel
                        : -1;
                    int lodLevel = useCameraDependentCpuPayload
                        ? SelectMeshletLodLevel(cameraPosition, worldCenter, worldRadius, previousLodLevel)
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
                            Padding = 0
                        };

                        if (meshletVisibleToCamera)
                        {
                            RecordSubmittedMeshlet(meshlet);
                            RecordSubmittedMeshletLod(effectiveLodLevel);

                            if (renderMode == MaterialRenderMode.Blend || isGeometryDecal)
                            {
                                if (isGeometryDecal)
                                    _geometryDecalMeshletCount++;
                                if (!isGeometryDecal || geometryDecalsEnabled)
                                    AddTransparentDraw(command, transparentDistanceSquared, metadata.DecalLayer, maxTransparentMeshlets);
                            }
                            else
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
                                _staticBatchMeshletDrawCommandCount++;
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

                        if (castsDirectionalShadow && objectIntersectsShadowCascade)
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
            foreach (TransparentMeshletDraw draw in _transparentSortScratch)
                _transparentMeshletDrawCommands.Add(draw.Command);

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

        private void AddTransparentDraw(
            GPUMeshletDrawCommand command,
            float distanceSquared,
            int layer,
            int maxTransparentMeshlets)
        {
            if (_transparentSortScratch.Count >= maxTransparentMeshlets)
            {
                _transparentOverflowCount++;
                return;
            }

            _transparentSortScratch.Add(new TransparentMeshletDraw(command, distanceSquared, layer));
        }

        private void AddOpaqueForwardDraw(
            GPUMeshletDrawCommand command,
            GPUPackedMeshletDrawCommand packedCommand,
            MaterialForwardClass forwardClass,
            MeshInfo meshInfo)
        {
            if (MaterialForwardClassifier.IsSimpleOpaque(forwardClass) &&
                !meshInfo.HasVertexColor)
            {
                _meshletDrawCommands.Add(command);
                _packedMeshletDrawCommands.Add(packedCommand);
                return;
            }

            _fullOpaqueMeshletDrawCommands.Add(command);
            _packedFullOpaqueMeshletDrawCommands.Add(packedCommand);
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

            return new GPUPackedMeshletDrawCommand
            {
                MeshletIndex = command.MeshletIndex,
                InstanceId = command.InstanceId,
                MaterialIndex = command.MaterialIndex,
                Flags = (uint)flags,
                WorldCenterRadius = new Vector4(worldCenter.X, worldCenter.Y, worldCenter.Z, worldRadius)
            };
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
            int previousLodLevel = -1)
        {
            float effectiveRadius = Math.Max(worldRadius, 1f);
            float distanceFromSurface = Math.Max(0f, Distance(cameraPosition, worldCenter) - effectiveRadius);
            float distanceRatio = distanceFromSurface / effectiveRadius;

            if (previousLodLevel >= 0)
                return SelectMeshletLodLevel(distanceRatio, previousLodLevel, MeshletLodHysteresisFraction);

            return SelectMeshletLodLevel(distanceRatio);
        }

        internal static int SelectMeshletLodLevel(float distanceRatio, int previousLodLevel, float hysteresisFraction)
        {
            if (previousLodLevel < 0)
                return SelectMeshletLodLevel(distanceRatio);

            float hysteresis = Math.Clamp(hysteresisFraction, 0f, 0.5f);
            return previousLodLevel switch
            {
                0 when distanceRatio < MeshletLod1DistanceRatio * (1f + hysteresis) => 0,
                1 when distanceRatio >= MeshletLod1DistanceRatio * (1f - hysteresis) &&
                       distanceRatio < MeshletLod2DistanceRatio * (1f + hysteresis) => 1,
                2 when distanceRatio >= MeshletLod2DistanceRatio * (1f - hysteresis) => 2,
                _ => SelectMeshletLodLevel(distanceRatio)
            };
        }

        private static int SelectMeshletLodLevel(float distanceRatio)
        {
            if (distanceRatio >= MeshletLod2DistanceRatio)
                return 2;
            if (distanceRatio >= MeshletLod1DistanceRatio)
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

        private static GPUMeshletTaskFrameData CreateMeshletTaskFrameData(Frustum frustum)
        {
            return new GPUMeshletTaskFrameData
            {
                FrustumPlane0 = frustum.Left,
                FrustumPlane1 = frustum.Right,
                FrustumPlane2 = frustum.Bottom,
                FrustumPlane3 = frustum.Top,
                FrustumPlane4 = frustum.Near,
                FrustumPlane5 = frustum.Far
            };
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

        private void ClearTiledLightBuffers(CommandBuffer commandBuffer, uint totalTiles)
        {
            ulong headerBytes = checked(totalTiles * TiledLightHeaderStride);
            ulong indexBytes = checked(totalTiles * MaxLightsPerTile * TiledLightIndexStride);

            if (headerBytes > 0)
                _context.Api.CmdFillBuffer(commandBuffer, _bufferManager.GetBuffer(_tiledLightHeaderBuffer.Handle), 0, headerBytes, 0);
            if (indexBytes > 0)
                _context.Api.CmdFillBuffer(commandBuffer, _bufferManager.GetBuffer(_tiledLightIndexBuffer.Handle), 0, indexBytes, 0);
        }

        private void RecordUploadReadBarriers(CommandBuffer commandBuffer, int frameIndex, bool includeTiledLightBuffers)
        {
            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[24];
            barriers[0] = CreateShaderReadBarrier(_objectDataBuffer.Handle);
            barriers[1] = CreateShaderReadBarrier(_instanceBuffers[frameIndex].Handle);
            barriers[2] = CreateShaderReadBarrier(_meshletDrawBuffers[frameIndex].Handle);
            barriers[3] = CreateShaderReadBarrier(_transparentMeshletDrawBuffers[frameIndex].Handle);

            uint barrierCount = 4;
            barriers[barrierCount++] = CreateShaderReadBarrier(_fullOpaqueMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_solidDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_maskedDepthMeshletDrawBuffers[frameIndex].Handle);
            barriers[barrierCount++] = CreateShaderReadBarrier(_packedMeshletDrawBuffers[frameIndex].Handle);
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
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferBase, _meshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MeshletDrawBufferFrame1, _meshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.FullOpaqueMeshletDrawBufferBase, _fullOpaqueMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.FullOpaqueMeshletDrawBufferFrame1, _fullOpaqueMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.SolidDepthMeshletDrawBufferBase, _solidDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.SolidDepthMeshletDrawBufferFrame1, _solidDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.MaskedDepthMeshletDrawBufferBase, _maskedDepthMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.MaskedDepthMeshletDrawBufferFrame1, _maskedDepthMeshletDrawBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMeshletDrawBufferBase, _packedMeshletDrawBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.PackedMeshletDrawBufferFrame1, _packedMeshletDrawBuffers[1].Handle);
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
            RegisterStorageBuffer(BindlessIndex.TiledLightHeaderBuffer, _tiledLightHeaderBuffer.Handle);
            RegisterStorageBuffer(BindlessIndex.TiledLightIndicesBuffer, _tiledLightIndexBuffer.Handle);
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

        private static ulong HashMeshletDrawCommands(IReadOnlyList<GPUMeshletDrawCommand> commands)
        {
            const ulong OffsetBasis = 14695981039346656037UL;
            const ulong Prime = 1099511628211UL;

            ulong hash = OffsetBasis;
            hash = (hash ^ (uint)commands.Count) * Prime;
            for (int i = 0; i < commands.Count; i++)
            {
                GPUMeshletDrawCommand command = commands[i];
                hash = (hash ^ command.MeshletIndex) * Prime;
                hash = (hash ^ command.InstanceId) * Prime;
                hash = (hash ^ command.MaterialIndex) * Prime;
            }

            return hash;
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

        private static void AddFoliageSceneSignature(Scene scene, ref HashCode hash)
        {
            foreach (var prototype in scene.FoliagePrototypes)
            {
                hash.Add(RuntimeHelpers.GetHashCode(prototype));
                hash.Add(prototype.Name);
                hash.Add(prototype.Mesh);
                hash.Add(prototype.Material);
                hash.Add(prototype.GeometryMode);
                hash.Add(prototype.Revision);
            }

            foreach (var patch in scene.FoliagePatches)
            {
                hash.Add(RuntimeHelpers.GetHashCode(patch));
                hash.Add(RuntimeHelpers.GetHashCode(patch.Prototype));
                hash.Add(patch.Name);
                hash.Add(patch.Bounds);
                hash.Add(patch.Density);
                hash.Add(patch.Seed);
                hash.Add(patch.Visible);
                hash.Add(patch.DensityTexture);
                hash.Add(patch.ContentRevision);
            }
        }

        public BufferHandle ObjectDataBuffer => _objectDataBuffer.Handle;
        public BufferHandle MaterialDataBuffer => _materialManager.MaterialBuffer;
        public BufferHandle MaterialExtensionDataBuffer => _materialManager.MaterialExtensionBuffer;
        public BufferHandle TiledLightHeaderBuffer => _tiledLightHeaderBuffer.Handle;
        public BufferHandle TiledLightIndexBuffer => _tiledLightIndexBuffer.Handle;

        public ulong ObjectBufferSize => _objectDataBuffer.ByteSize;
        public ulong MaterialBufferSize => _materialManager.MaterialBufferSize;
        public ulong MaterialExtensionBufferSize => _materialManager.MaterialExtensionBufferSize;
        public ulong TiledLightHeaderBufferSize => _tiledLightHeaderBuffer.ByteSize;
        public ulong TiledLightIndexBufferSize => _tiledLightIndexBuffer.ByteSize;
        public ulong LastUploadedBytes => _lastUploadedBytes;
        public int LastSceneUploadCount => _lastSceneUploadCount;
        public int LastSceneUploadSkipped => _lastSceneUploadSkipped;
        public long LastBuildMicroseconds => _lastBuildMicroseconds;
        public uint LastTileCountX => _lastTileCountX;
        public uint LastTileCountY => _lastTileCountY;

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
                    DestroyIfValid(_meshletDrawBuffers[i].Handle);
                    DestroyIfValid(_fullOpaqueMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_solidDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_maskedDepthMeshletDrawBuffers[i].Handle);
                    DestroyIfValid(_packedMeshletDrawBuffers[i].Handle);
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

                _objectData.Clear();
                _meshletDrawCommands.Clear();
                _fullOpaqueMeshletDrawCommands.Clear();
                _solidDepthMeshletDrawCommands.Clear();
                _maskedDepthMeshletDrawCommands.Clear();
                _packedMeshletDrawCommands.Clear();
                _packedFullOpaqueMeshletDrawCommands.Clear();
                _packedSolidDepthMeshletDrawCommands.Clear();
                _packedMaskedDepthMeshletDrawCommands.Clear();
                _meshletTaskFrameData.Clear();
                _transparentMeshletDrawCommands.Clear();
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
            public TransparentMeshletDraw(GPUMeshletDrawCommand command, float distanceSquared, int layer)
            {
                Command = command;
                DistanceSquared = distanceSquared;
                Layer = layer;
            }

            public GPUMeshletDrawCommand Command { get; }
            public float DistanceSquared { get; }
            public int Layer { get; }
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
                hash.Add(scene.RenderObjects.Count);
                hash.Add(scene.StaticInstanceBatches.Count);
                hash.Add(scene.FoliagePrototypes.Count);
                hash.Add(scene.FoliagePatches.Count);

                foreach (RenderObject renderObject in scene.RenderObjects)
                {
                    hash.Add(RuntimeHelpers.GetHashCode(renderObject));
                    hash.Add(renderObject.Visible);
                    hash.Add(renderObject.WorldMatrix);
                    hash.Add(renderObject.Mesh);
                    hash.Add(renderObject.Material);
                    if (renderObject is SkinnedRenderObject skinned)
                    {
                        hash.Add(skinned.SkinningEnabled);
                        hash.Add(skinned.SkinnedVertexOffset);
                    }
                }

                foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
                {
                    hash.Add(RuntimeHelpers.GetHashCode(batch));
                    hash.Add(batch.Visible);
                    hash.Add(batch.Mesh);
                    hash.Add(batch.Material);
                    hash.Add(batch.WorldMatrices.Count);
                    hash.Add(batch.Revision);
                    foreach (Matrix4x4 worldMatrix in batch.WorldMatrices)
                        hash.Add(worldMatrix);
                }

                AddFoliageSceneSignature(scene, ref hash);

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

            public static SceneCullingSignature Create(
                Scene scene,
                Matrix4x4 viewProjection,
                GPUShadowData? directionalShadowData,
                int directionalShadowCascadeCount,
                bool buildLocalShadowMeshlets,
                ReadOnlySpan<SelectedLocalShadow> selectedPointShadows,
                TransparencySettings? transparencySettings,
                DecalSettings? decalSettings,
                bool captureCpuSnapshots,
                bool cameraDependentCpuPayload,
                bool useCpuMeshletFrustumCulling)
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
                hash.Add(cameraDependentCpuPayload);
                hash.Add(useCpuMeshletFrustumCulling);
                hash.Add(transparencySettings?.MaxTransparentMeshlets ?? int.MaxValue);
                hash.Add(transparencySettings?.SortPerMeshlet ?? true);
                hash.Add(decalSettings?.GeometryDecalsEnabled ?? true);
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

                foreach (RenderObject renderObject in scene.RenderObjects)
                {
                    hash.Add(RuntimeHelpers.GetHashCode(renderObject));
                    hash.Add(renderObject.Visible);
                    hash.Add(renderObject.WorldMatrix);
                    hash.Add(renderObject.Mesh);
                    hash.Add(renderObject.Material);
                    if (renderObject is SkinnedRenderObject skinned)
                        hash.Add(skinned.SkinningBindTransform);
                }

                foreach (StaticInstanceBatch batch in scene.StaticInstanceBatches)
                {
                    hash.Add(RuntimeHelpers.GetHashCode(batch));
                    hash.Add(batch.Visible);
                    hash.Add(batch.Mesh);
                    hash.Add(batch.Material);
                    hash.Add(batch.WorldMatrices.Count);
                    hash.Add(batch.Revision);
                    foreach (Matrix4x4 worldMatrix in batch.WorldMatrices)
                        hash.Add(worldMatrix);
                }

                AddFoliageSceneSignature(scene, ref hash);

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

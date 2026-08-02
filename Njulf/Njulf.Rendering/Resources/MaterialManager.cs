using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class MaterialManager : IDisposable
    {
        private const uint InitialMaterialCapacity = 1024;
        private static readonly ulong MaterialStride = (ulong)Marshal.SizeOf<GPUMaterialData>();
        private static readonly ulong MaterialExtensionStride = (ulong)Marshal.SizeOf<GPUMaterialExtensionData>();
        public const ulong MaximumPrimitiveProfileGpuBytes = 32UL * 1024UL * 1024UL;
        public static ulong PrimitiveProfileGpuStrideBytes => MaterialStride;
        private const int LatencyWindowSize = 256;
        private const int LatencyOverflowBucketMicroseconds = 4_096;
        private const int MaximumCompilationPublishAttempts = 4;

        private readonly VulkanContext? _context;
        private readonly BufferManager? _bufferManager;
        private readonly StagingRing? _stagingRing;
        private readonly SynchronizationManager? _sync;
        private readonly TextureManager? _textureManager;
        private readonly ITextureReferenceManager? _textureReferences;
        private readonly Func<
            MaterialTextureBinding,
            MaterialTextureSemantic,
            float,
            MaterialTextureTransportInput>? _textureResolverOverride;
        private readonly object _lock = new object();
        private readonly List<MaterialSlot> _materials = new List<MaterialSlot>();
        private readonly List<GPUMaterialExtensionData> _materialExtensions = new List<GPUMaterialExtensionData>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private readonly Stack<int> _freeMaterialExtensionIndices = new Stack<int>();
        private readonly Dictionary<MaterialRegistrationKey, MaterialHandle> _deduplicatedMaterials =
            new Dictionary<MaterialRegistrationKey, MaterialHandle>(new MaterialRegistrationKeyComparer());
        private readonly Dictionary<TextureHandle, HashSet<int>> _textureDependents = new();
        private readonly HashSet<TextureHandle>
            _pendingTextureFanoutRetries = new();
        private readonly Dictionary<TextureHandle, int> _pendingTextureReleases = new();
        private readonly List<PendingTextureRelease> _retiredTextureReleases = new();
        private readonly List<BufferHandle>
            _retiredMaterialBuffers = new();
        private readonly List<BufferHandle>
            _quarantinedMaterialBuffers = new();
        private int _retiredTextureReleaseHead;
        private bool _drainingTextureReleases;
        private int _textureReleaseDrainThreadId;
        private long _textureReleaseFailureCount;
        private Exception? _lastTextureReleaseFailure;
        private long _textureFanoutFailureCount;
        private Exception? _lastTextureFanoutFailure;
        private long _retiredBufferCleanupFailureCount;
        private Exception? _lastRetiredBufferCleanupFailure;
        private bool _materialBindingRepairRequired;
        private Exception? _lastMaterialBindingPublicationFailure;

        private BufferHandle _materialBuffer = BufferHandle.Invalid;
        private BufferHandle _materialExtensionBuffer = BufferHandle.Invalid;
        private uint _materialBufferCapacity;
        private uint _materialExtensionBufferCapacity;
        private uint _materialDataRevision;
        private uint _ssgiInputRevision = 1;
        private uint _materialContentRevisionSerial;
        private uint _textureContentRevisionSerial;
        private uint _referencedTextureCacheRevision = uint.MaxValue;
        private IReadOnlyList<TextureHandle> _referencedTextureCache = Array.Empty<TextureHandle>();
        private ulong _referencedTextureSetGeneration;
        private bool _gpuUploadDirty = true;
        private ulong _lastUploadBytes;
        private ulong _lastExtensionUploadBytes;
        private long _lastUploadMicroseconds;
        private long _lastCompileMicroseconds;
        private long _totalCompileMicroseconds;
        private long _materialCompileCount;
        private readonly RollingLatencyHistogram _compileLatencies =
            new(LatencyWindowSize, LatencyOverflowBucketMicroseconds);
        private readonly RollingLatencyHistogram _uploadLatencies =
            new(LatencyWindowSize, LatencyOverflowBucketMicroseconds);
        private long _legacyV1FallbackCount;
        private long _invalidStatisticsCompileCount;
        private int _activeLegacyV1FallbackCount;
        private int _activeInvalidProfileCount;
        private int _activePrimitiveProfileCount;
        private ulong _primitiveProfileGpuBudgetBytes = MaximumPrimitiveProfileGpuBytes;
        private BindlessHeap? _registeredBindlessHeap;
        // Direct manager use is fail-closed. The renderer may enable V2 only
        // from GlobalIlluminationSettings' policy-authorized effective switch.
        private bool _transportV2Enabled;
        private bool _disposed;
        private bool _disposePrepared;
        private bool _disposeCompleted;
        internal Action<MaterialRegistrationPublicationStage>?
            RegistrationPublicationFaultInjector
        {
            get;
            set;
        }
        internal Action? DisposalPreflightFaultInjector
        {
            get;
            set;
        }
        internal Action<MaterialBufferBindingPublicationStage>?
            BufferBindingPublicationFaultInjector
        {
            get;
            set;
        }

        public event Action<MaterialChangedEvent>? MaterialChanged;

        public MaterialManager()
            : this(context: null, bufferManager: null, stagingRing: null, sync: null, textureManager: null, cpuOnly: true)
        {
        }

        internal MaterialManager(
            ITextureReferenceManager textureReferences,
            Func<
                MaterialTextureBinding,
                MaterialTextureSemantic,
                float,
                MaterialTextureTransportInput>? textureResolverOverride = null)
            : this(context: null, bufferManager: null, stagingRing: null, sync: null, textureManager: null, cpuOnly: true)
        {
            _textureReferences = textureReferences ??
                throw new ArgumentNullException(nameof(textureReferences));
            _textureResolverOverride = textureResolverOverride;
        }

        public MaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync)
            : this(
                context ?? throw new ArgumentNullException(nameof(context)),
                bufferManager ?? throw new ArgumentNullException(nameof(bufferManager)),
                stagingRing ?? throw new ArgumentNullException(nameof(stagingRing)),
                sync ?? throw new ArgumentNullException(nameof(sync)),
                textureManager: null,
                cpuOnly: false)
        {
        }

        public MaterialManager(
            VulkanContext context,
            BufferManager bufferManager,
            StagingRing stagingRing,
            SynchronizationManager sync,
            TextureManager textureManager)
            : this(
                context ?? throw new ArgumentNullException(nameof(context)),
                bufferManager ?? throw new ArgumentNullException(nameof(bufferManager)),
                stagingRing ?? throw new ArgumentNullException(nameof(stagingRing)),
                sync ?? throw new ArgumentNullException(nameof(sync)),
                textureManager ?? throw new ArgumentNullException(nameof(textureManager)),
                cpuOnly: false)
        {
        }

        private MaterialManager(
            VulkanContext? context,
            BufferManager? bufferManager,
            StagingRing? stagingRing,
            SynchronizationManager? sync,
            TextureManager? textureManager,
            bool cpuOnly)
        {
            _context = context;
            _bufferManager = bufferManager;
            _stagingRing = stagingRing;
            _sync = sync;
            _textureManager = textureManager;
            _textureReferences = textureManager;
            _textureResolverOverride = null;

            CompiledMaterialTransport defaultMaterial = MaterialTransportCompiler.Compile(MaterialDefinition.Default);
            DefaultMaterialHandle = RegisterCompiledMaterialInternal(defaultMaterial, permanent: true);
            if (_textureManager != null)
                _textureManager.TextureContentChanged += OnTextureContentChanged;

            if (HasGpuServices)
            {
                _materialBuffer = CreateMaterialBuffer(InitialMaterialCapacity);
                _materialBufferCapacity =
                    InitialMaterialCapacity;
                _materialExtensionBuffer = CreateMaterialExtensionBuffer(1);
                _materialExtensionBufferCapacity = 1;
            }
        }

        public MaterialHandle DefaultMaterialHandle { get; }

        public BufferHandle MaterialBuffer => _materialBuffer;

        public BufferHandle MaterialExtensionBuffer => _materialExtensionBuffer;

        public int PendingTextureReleaseCount
        {
            get
            {
                lock (_lock)
                {
                    int count = _pendingTextureReleases.Values.Sum();
                    for (int index =
                             _retiredTextureReleaseHead;
                         index <
                             _retiredTextureReleases.Count;
                         index++)
                    {
                        PendingTextureRelease pending =
                            _retiredTextureReleases[index];
                        count = checked(
                            count + pending.RemainingCount);
                    }
                    return count;
                }
            }
        }

        public long TextureReleaseFailureCount
        {
            get
            {
                lock (_lock)
                    return _textureReleaseFailureCount;
            }
        }

        public Exception? LastTextureReleaseFailure
        {
            get
            {
                lock (_lock)
                    return _lastTextureReleaseFailure;
            }
        }

        public int PendingTextureFanoutCount
        {
            get
            {
                lock (_lock)
                    return _pendingTextureFanoutRetries.Count;
            }
        }

        public long TextureFanoutFailureCount
        {
            get
            {
                lock (_lock)
                    return _textureFanoutFailureCount;
            }
        }

        public Exception? LastTextureFanoutFailure
        {
            get
            {
                lock (_lock)
                    return _lastTextureFanoutFailure;
            }
        }

        public int PendingRetiredBufferCount
        {
            get
            {
                lock (_lock)
                    return _retiredMaterialBuffers.Count;
            }
        }

        public int QuarantinedMaterialBufferCount
        {
            get
            {
                lock (_lock)
                    return _quarantinedMaterialBuffers.Count;
            }
        }

        public long RetiredBufferCleanupFailureCount
        {
            get
            {
                lock (_lock)
                    return _retiredBufferCleanupFailureCount;
            }
        }

        public Exception? LastRetiredBufferCleanupFailure
        {
            get
            {
                lock (_lock)
                    return _lastRetiredBufferCleanupFailure;
            }
        }

        /// <summary>
        /// Prevents rendering while a newly published texture still has stale
        /// dependent material payloads. TextureManager retains failed
        /// notifications and a successful retry clears this gate.
        /// </summary>
        public void EnsureTextureFanoutReady()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (_materialBindingRepairRequired)
                {
                    throw new InvalidOperationException(
                        "Material buffer descriptor publication is incomplete. Rendering is blocked until authoritative bindings are repaired.",
                        _lastMaterialBindingPublicationFailure);
                }
                if (_pendingTextureFanoutRetries.Count == 0)
                    return;

                throw new InvalidOperationException(
                    $"{_pendingTextureFanoutRetries.Count} texture-content publication(s) have incomplete material fan-out. Rendering is blocked until notification retry succeeds.",
                    _lastTextureFanoutFailure);
            }
        }

        public ulong MaterialBufferSize
        {
            get
            {
                lock (_lock)
                    return _materialBufferCapacity * MaterialStride;
            }
        }

        public ulong MaterialExtensionBufferSize
        {
            get
            {
                lock (_lock)
                    return _materialExtensionBufferCapacity * MaterialExtensionStride;
            }
        }

        public float MaterialBufferUtilization
        {
            get
            {
                lock (_lock)
                {
                    if (_materialBufferCapacity == 0)
                        return 0f;

                    return (float)_materials.Count / _materialBufferCapacity;
                }
            }
        }

        public int MaterialExtensionDataCount
        {
            get
            {
                lock (_lock)
                    return _materialExtensions.Count;
            }
        }

        public int RegisteredMaterialCount
        {
            get
            {
                lock (_lock)
                {
                    int count = 0;
                    foreach (MaterialSlot slot in _materials)
                    {
                        if (slot.Active)
                            count++;
                    }

                    return count;
                }
            }
        }

        public int UploadedMaterialCount
        {
            get
            {
                lock (_lock)
                    return _materials.Count;
            }
        }

        public uint MaterialDataRevision
        {
            get
            {
                lock (_lock)
                    return _materialDataRevision;
            }
        }

        /// <summary>
        /// Monotonic revision of material channels consumed by SSGI tracing
        /// and composition. Far-field-only and raster-only edits do not
        /// invalidate SSGI history.
        /// </summary>
        public uint SsgiInputRevision
        {
            get
            {
                lock (_lock)
                    return _ssgiInputRevision;
            }
        }

        public bool TransportV2Enabled
        {
            get
            {
                lock (_lock)
                    return _transportV2Enabled;
            }
        }

        /// <summary>
        /// Atomically switches authored materials between canonical V2 and
        /// the one-release V1 interpretation. Canonical definitions and
        /// transport profiles are retained so re-enabling V2 is lossless.
        /// Raw V1 registrations are never promoted by this switch.
        /// Callers must pass a rollout-policy-authorized value, never a raw
        /// persisted render-setting switch.
        /// </summary>
        public void SetTransportV2Enabled(bool enabled)
        {
            MaterialChangedEvent[] changes;
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (_transportV2Enabled == enabled)
                    return;

                _transportV2Enabled = enabled;
                var published = new List<MaterialChangedEvent>();
                for (int index = 0; index < _materials.Count; index++)
                {
                    MaterialSlot slot = _materials[index];
                    if (!slot.Active || !slot.SupportsTransportV2)
                        continue;

                    RemoveActiveProfileClassificationLocked(slot);
                    GPUMaterialData data = slot.Data;
                    ApplyTransportInterpretation(
                        ref data,
                        supportsTransportV2: true,
                        enabled: enabled);
                    if (data.TransportFlags == slot.Data.TransportFlags)
                    {
                        AddActiveProfileClassificationLocked(slot);
                        continue;
                    }

                    slot.ContentRevision = NextMaterialContentRevisionLocked();
                    data.MaterialRevision = slot.ContentRevision;
                    slot.Data = data;
                    slot.AspectRevisions = AdvanceAspectRevisions(
                        slot.AspectRevisions,
                        MaterialChangeMask.All,
                        slot.ContentRevision);
                    _materials[index] = slot;
                    published.Add(new MaterialChangedEvent(
                        new MaterialHandle(index, slot.Generation),
                        MaterialChangeMask.All,
                        slot.AspectRevisions));
                    AddActiveProfileClassificationLocked(slot);
                }

                if (published.Count > 0)
                {
                    MarkMaterialDataDirtyLocked();
                    AdvanceSsgiInputRevisionLocked(MaterialChangeMask.All);
                }
                changes = published.ToArray();
            }

            foreach (MaterialChangedEvent changed in changes)
                MaterialChanged?.Invoke(changed);
        }

        /// <summary>
        /// Gets the revision of the material payload at a shader-visible material index.
        /// This is per slot rather than the global upload revision so paged consumers can
        /// invalidate only content that actually references the changed material.
        /// </summary>
        public uint GetMaterialContentRevision(int materialIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if ((uint)materialIndex >= (uint)_materials.Count)
                    return 0;

                MaterialSlot slot = _materials[materialIndex];
                return slot.Active ? slot.ContentRevision : 0;
            }
        }

        public uint GetMaterialTransportProfileRevision(int materialIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if ((uint)materialIndex >= (uint)_materials.Count)
                    return 0;
                MaterialSlot slot = _materials[materialIndex];
                return slot.Active ? slot.Data.TransportProfileRevision : 0;
            }
        }

        /// <summary>
        /// Gets the texture-content revision independently of the material
        /// publication and transport-profile revisions. Zero is the immutable
        /// registration baseline and is also returned for an inactive index.
        /// </summary>
        public uint GetMaterialTextureContentRevision(int materialIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if ((uint)materialIndex >= (uint)_materials.Count)
                    return 0;
                MaterialSlot slot = _materials[materialIndex];
                return slot.Active ? slot.Data.TextureContentRevision : 0;
            }
        }

        public MaterialManagerDiagnostics Diagnostics
        {
            get
            {
                lock (_lock)
                    return new MaterialManagerDiagnostics(
                        RegisteredMaterialCount,
                        UploadedMaterialCount,
                        _materialExtensions.Count,
                        _materialCompileCount,
                        _lastCompileMicroseconds,
                        _totalCompileMicroseconds,
                        _legacyV1FallbackCount,
                        _invalidStatisticsCompileCount,
                        _textureDependents.Count,
                        _compileLatencies.GetPercentile(0.95),
                        _compileLatencies.Count,
                        _uploadLatencies.GetPercentile(0.95),
                        _uploadLatencies.Count,
                        _activeLegacyV1FallbackCount,
                        _activeInvalidProfileCount,
                        _materialContentRevisionSerial,
                        _textureContentRevisionSerial,
                        GetMaximumActiveTransportProfileRevisionLocked(),
                        _activePrimitiveProfileCount,
                        checked((ulong)_activePrimitiveProfileCount * MaterialStride),
                        _primitiveProfileGpuBudgetBytes,
                        _pendingTextureFanoutRetries.Count,
                        _textureFanoutFailureCount,
                        _retiredMaterialBuffers.Count,
                        _quarantinedMaterialBuffers.Count,
                        _retiredBufferCleanupFailureCount,
                        _materialBindingRepairRequired);
            }
        }

        /// <summary>
        /// Configures the hard admission cap for authoritative primitive
        /// transport profiles. Lowering the cap is atomic and rejected when
        /// existing profiles would exceed it, so a quality-tier transition can
        /// never publish an internally over-budget state.
        /// </summary>
        public void SetPrimitiveProfileGpuBudgetBytes(ulong budgetBytes)
        {
            ThrowIfDisposed();
            if (budgetBytes == 0 || budgetBytes > MaximumPrimitiveProfileGpuBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(budgetBytes),
                    budgetBytes,
                    $"Primitive transport profile budget must be in [1, {MaximumPrimitiveProfileGpuBytes}] bytes.");
            }

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                ulong activeBytes = checked((ulong)_activePrimitiveProfileCount * MaterialStride);
                if (activeBytes > budgetBytes)
                {
                    throw new InvalidOperationException(
                        $"Cannot lower the primitive transport profile GPU budget to {budgetBytes} bytes because " +
                        $"{_activePrimitiveProfileCount} active profiles require {activeBytes} bytes.");
                }

                _primitiveProfileGpuBudgetBytes = budgetBytes;
            }
        }

        public ulong LastUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastUploadBytes;
            }
        }

        public ulong LastExtensionUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastExtensionUploadBytes;
            }
        }

        public long LastUploadMicroseconds
        {
            get
            {
                lock (_lock)
                    return _lastUploadMicroseconds;
            }
        }

        private bool HasGpuServices =>
            _context != null &&
            _bufferManager != null &&
            _stagingRing != null &&
            _sync != null;

        /// <summary>
        /// Registers an authored material. Ownership of one texture reference
        /// per bound occurrence is transferred to the returned logical
        /// material reference. This remains true when registration
        /// deduplicates to an existing handle.
        /// </summary>
        public MaterialHandle RegisterMaterialDefinition(
            MaterialDefinition definition,
            MaterialCompilationContext? context = null)
        {
            ThrowIfDisposed();
            MaterialCompilationContext compilationContext =
                context ?? CreateCompilationContext(
                    profileRevision: 1,
                    alphaCutoff: definition.AlphaCutoff);
            long compileStart = Stopwatch.GetTimestamp();
            CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
                definition,
                compilationContext);
            long compileMicroseconds = ElapsedMicroseconds(compileStart);

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                RecordCompileDiagnosticsLocked(compiled, compileMicroseconds);
                GPUMaterialData keyMaterial = compiled.GpuMaterial;
                keyMaterial.ExtensionDataIndex = -1;
                var key = new MaterialRegistrationKey(
                    keyMaterial,
                    compiled.ExtensionData,
                    compiled.Metadata,
                    compiled.Definition,
                    compiled.TransportProfile);
                if (_deduplicatedMaterials.TryGetValue(key, out MaterialHandle existingHandle))
                {
                    MaterialSlot existing = GetValidatedSlotLocked(existingHandle);
                    existing.ReferenceCount =
                        CheckedIncrementReferenceCount(
                            existing.ReferenceCount);
                    _materials[existingHandle.Index] = existing;
                    return existingHandle;
                }

                return RegisterCompiledMaterialInternal(
                    compiled,
                    permanent: false,
                    primitiveProfileInput: compilationContext.PrimitiveProfile);
            }
        }

        /// <summary>
        /// Registers one primitive/material pairing while preserving the
        /// shared authored material. The primitive profile remains a distinct
        /// deduplication key even when compact float payloads happen to match.
        /// </summary>
        public MaterialHandle RegisterMaterialDefinition(
            MaterialDefinition definition,
            GiMaterialTransportProfile primitiveProfile)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(primitiveProfile);
            return RegisterMaterialDefinition(
                definition,
                CreateCompilationContext(
                    profileRevision: 1,
                    alphaCutoff: definition.AlphaCutoff,
                    primitiveProfile));
        }

        public MaterialDefinition GetMaterialDefinition(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).Definition;
        }

        public GiMaterialTransportProfile GetMaterialTransportProfile(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).TransportProfile;
        }

        public IReadOnlyList<string> GetMaterialCompileDiagnostics(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).CompileDiagnostics.ToArray();
        }

        public MaterialDefinition[] GetMaterialDefinitionSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                var snapshot = new MaterialDefinition[_materials.Count];
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active ? _materials[i].Definition : MaterialDefinition.Default;
                return snapshot;
            }
        }

        public GiMaterialTransportProfile[] GetMaterialTransportProfileSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                var snapshot = new GiMaterialTransportProfile[_materials.Count];
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active
                        ? _materials[i].TransportProfile
                        : GiMaterialTransportProfile.Invalid;
                return snapshot;
            }
        }

        public MaterialAspectRevisions GetMaterialAspectRevisions(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).AspectRevisions;
        }

        public MaterialAspectRevisions GetMaterialAspectRevisions(int materialIndex)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if ((uint)materialIndex >= (uint)_materials.Count || !_materials[materialIndex].Active)
                    return default;
                return _materials[materialIndex].AspectRevisions;
            }
        }

        /// <summary>
        /// Atomically recompiles an authored material. Texture handles in
        /// <paramref name="definition"/> are borrowed: the manager acquires
        /// the references needed by every logical alias before publishing the
        /// new payload. Superseded references are released only after the GPU
        /// upload that stops referencing their descriptor slots.
        /// </summary>
        public MaterialChangedEvent UpdateMaterialDefinition(
            MaterialHandle handle,
            MaterialDefinition definition,
            MaterialCompilationContext? context = null)
        {
            MaterialDefinition before;
            lock (_lock)
            {
                MaterialSlot existing = GetValidatedSlotLocked(handle);
                if (existing.Permanent)
                    throw new InvalidOperationException("The permanent default material cannot be edited in place.");
                before = existing.Definition;
            }

            MaterialChangeMask changeMask = MaterialTransportCompiler.ClassifyChanges(before, definition);
            if (changeMask == MaterialChangeMask.None)
            {
                lock (_lock)
                {
                    MaterialSlot unchanged = GetValidatedSlotLocked(handle);
                    if (!ReferenceEquals(unchanged.Definition, before) && unchanged.Definition != before)
                    {
                        throw new InvalidOperationException(
                            "Material changed concurrently while an authored update was being evaluated.");
                    }
                    return new MaterialChangedEvent(handle, MaterialChangeMask.None, unchanged.AspectRevisions);
                }
            }

            for (int attempt = 1; attempt <= MaximumCompilationPublishAttempts; attempt++)
            {
                MaterialCompilationSnapshot snapshot;
                lock (_lock)
                {
                    MaterialSlot current = GetValidatedSlotLocked(handle);
                    if (!ReferenceEquals(current.Definition, before) && current.Definition != before)
                    {
                        throw new InvalidOperationException(
                            "Material changed concurrently while an authored update was being compiled.");
                    }
                    snapshot = MaterialCompilationSnapshot.Capture(current);
                }

                GiMaterialTransportProfile? selectedPrimitiveProfile = context?.PrimitiveProfile ??
                    InvalidatePrimitiveProfile(snapshot.PrimitiveProfileInput, changeMask);
                uint nextProfileRevision = NextNonZero(snapshot.TransportProfileRevision);
                MaterialCompilationContext compilationContext = context == null
                    ? CreateCompilationContext(
                        nextProfileRevision,
                        definition.AlphaCutoff,
                        selectedPrimitiveProfile)
                    : context with
                    {
                        ProfileRevision = nextProfileRevision,
                        PrimitiveProfile = selectedPrimitiveProfile
                    };
                long compileStart = Stopwatch.GetTimestamp();
                CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(
                    definition,
                    compilationContext);
                long compileMicroseconds = ElapsedMicroseconds(compileStart);
                ValidateCompiledMaterial(compiled);

                MaterialChangedEvent? changed = null;
                bool drainImmediateReleases = false;
                lock (_lock)
                {
                    MaterialSlot slot = GetValidatedSlotLocked(handle);
                    if (!ReferenceEquals(slot.Definition, before) && slot.Definition != before)
                    {
                        throw new InvalidOperationException(
                            "Material changed concurrently while an authored update was being compiled.");
                    }
                    if (!snapshot.Matches(slot))
                        continue;

                    EnsurePrimitiveProfileAdmissionLocked(
                        slot.PrimitiveProfileInput,
                        selectedPrimitiveProfile);

                    TextureOwnershipDelta ownershipDelta = ComputeTextureOwnershipDelta(
                        slot.TextureHandles,
                        compiled.TextureDependencies,
                        slot.ReferenceCount);
                    if (HasGpuServices)
                    {
                        PreparePendingTextureReleasesLocked(
                            ownershipDelta.Releases);
                    }
                    else
                    {
                        PrepareRetiredTextureReleasesLocked(
                            ownershipDelta.Releases);
                    }
                    RetainTextureReferencesTransactional(ownershipDelta.Retains);

                    RecordCompileDiagnosticsLocked(compiled, compileMicroseconds);
                    RemoveDeduplicationLocked(handle, slot);
                    RemoveTextureDependenciesLocked(handle.Index, slot.TextureHandles);
                    RemoveActiveProfileClassificationLocked(slot);
                    int retiredExtensionIndex =
                        ApplyCompiledPayloadLocked(ref slot, compiled);
                    ApplyTransportInterpretation(
                        ref slot.Data,
                        slot.SupportsTransportV2,
                        _transportV2Enabled);
                    slot.PrimitiveProfileInput = selectedPrimitiveProfile;
                    slot.ContentRevision = NextMaterialContentRevisionLocked();
                    slot.Data.MaterialRevision = slot.ContentRevision;
                    slot.AspectRevisions = AdvanceAspectRevisions(
                        slot.AspectRevisions,
                        changeMask,
                        slot.ContentRevision);
                    slot.RegistrationKey = default;
                    _materials[handle.Index] = slot;
                    AddActiveProfileClassificationLocked(slot);
                    AddTextureDependenciesLocked(handle.Index, slot.TextureHandles);
                    MarkMaterialDataDirtyLocked();
                    AdvanceSsgiInputRevisionLocked(changeMask);
                    ReleaseMaterialExtensionDataLocked(retiredExtensionIndex);
                    changed = new MaterialChangedEvent(handle, changeMask, slot.AspectRevisions);

                    if (HasGpuServices)
                        QueuePendingTextureReleasesLocked(ownershipDelta.Releases);
                    else
                    {
                        QueueRetiredTextureReleasesLocked(
                            ownershipDelta.Releases,
                            default);
                        drainImmediateReleases = true;
                    }
                }

                if (drainImmediateReleases)
                    DrainTextureReleasesBestEffort();
                if (changed == null)
                    throw new InvalidOperationException("Authored material publication completed without a change event.");
                MaterialChanged?.Invoke(changed);
                return changed;
            }

            throw new InvalidOperationException(
                $"Material {handle} changed during each of {MaximumCompilationPublishAttempts} " +
                "authored compilation attempts. No stale payload was published.");
        }

        /// <summary>
        /// Atomically edits the material bound to one render object. A shared
        /// or permanent registration is compiled into a private material
        /// before one logical ownership reference and the object binding are
        /// transferred together. Compilation, texture acquisition, or manager
        /// publication failures leave the source binding and reference counts
        /// unchanged.
        /// </summary>
        public MaterialChangedEvent UpdateRenderObjectMaterialDefinition(
            RenderObject renderObject,
            MaterialDefinition definition,
            MaterialCompilationContext? context = null)
        {
            ArgumentNullException.ThrowIfNull(renderObject);
            ArgumentNullException.ThrowIfNull(definition);

            object expectedMaterial = renderObject.Material ??
                throw new InvalidOperationException(
                    $"Scene object '{renderObject.Name}' ({renderObject.Id}) has no material.");
            if (expectedMaterial is not MaterialHandle sourceHandle)
            {
                throw new InvalidOperationException(
                    $"Scene object '{renderObject.Name}' ({renderObject.Id}) has no material handle.");
            }

            MaterialDefinition normalized =
                MaterialDefinitionValidator.ValidateAndNormalize(
                    definition);
            MaterialCompilationSnapshot snapshot;
            bool requiresCopy;
            MaterialChangeMask changeMask;
            lock (_lock)
            {
                MaterialSlot source =
                    GetValidatedSlotLocked(sourceHandle);
                snapshot =
                    MaterialCompilationSnapshot.Capture(source);
                requiresCopy =
                    source.Permanent ||
                    source.ReferenceCount > 1;
                changeMask =
                    MaterialTransportCompiler.ClassifyChanges(
                        source.Definition,
                        normalized);
            }

            if (changeMask == MaterialChangeMask.None)
            {
                MaterialAspectRevisions revisions = default;
                renderObject.TransferMaterialOwnership(
                    expectedMaterial,
                    () =>
                    {
                        lock (_lock)
                        {
                            MaterialSlot source =
                                GetValidatedSlotLocked(
                                    sourceHandle);
                            if (!snapshot.Matches(source))
                            {
                                throw CreateConcurrentRenderObjectEditException(
                                    sourceHandle);
                            }
                            revisions = source.AspectRevisions;
                            return expectedMaterial;
                        }
                    });
                return new MaterialChangedEvent(
                    sourceHandle,
                    MaterialChangeMask.None,
                    revisions);
            }

            if (!requiresCopy)
            {
                MaterialChangedEvent? changed = null;
                renderObject.TransferMaterialOwnership(
                    expectedMaterial,
                    () =>
                    {
                        // Keep the manager gate recursively held across the
                        // existing in-place compiler/publisher. A concurrent
                        // retain cannot turn this unique material into a shared
                        // registration between the ownership check and publish.
                        lock (_lock)
                        {
                            MaterialSlot source =
                                GetValidatedSlotLocked(
                                    sourceHandle);
                            if (!snapshot.Matches(source) ||
                                source.Permanent ||
                                source.ReferenceCount != 1)
                            {
                                throw CreateConcurrentRenderObjectEditException(
                                    sourceHandle);
                            }
                            changed =
                                UpdateMaterialDefinition(
                                    sourceHandle,
                                    normalized,
                                    context);
                            return expectedMaterial;
                        }
                    });
                return changed ??
                    throw new InvalidOperationException(
                        "An in-place render-object material edit completed without a change event.");
            }

            GiMaterialTransportProfile? selectedPrimitiveProfile =
                context?.PrimitiveProfile ??
                InvalidatePrimitiveProfile(
                    snapshot.PrimitiveProfileInput,
                    changeMask);
            uint nextProfileRevision =
                NextNonZero(snapshot.TransportProfileRevision);
            MaterialCompilationContext compilationContext =
                context == null
                    ? CreateCompilationContext(
                        nextProfileRevision,
                        normalized.AlphaCutoff,
                        selectedPrimitiveProfile)
                    : context with
                    {
                        ProfileRevision = nextProfileRevision,
                        PrimitiveProfile =
                            selectedPrimitiveProfile
                    };
            long compileStart = Stopwatch.GetTimestamp();
            CompiledMaterialTransport compiled =
                MaterialTransportCompiler.Compile(
                    normalized,
                    compilationContext);
            long compileMicroseconds =
                ElapsedMicroseconds(compileStart);
            ValidateCompiledMaterial(compiled);

            MaterialHandle publishedHandle = MaterialHandle.Invalid;
            MaterialAspectRevisions publishedRevisions = default;
            bool drainTextureReleases = false;
            try
            {
                renderObject.TransferMaterialOwnership(
                    expectedMaterial,
                    () =>
                    {
                        lock (_lock)
                        {
                            MaterialSlot source =
                                GetValidatedSlotLocked(
                                    sourceHandle);
                            if (!snapshot.Matches(source) ||
                                (!source.Permanent &&
                                 source.ReferenceCount <= 1))
                            {
                                throw CreateConcurrentRenderObjectEditException(
                                    sourceHandle);
                            }

                            IReadOnlyList<TextureHandle>
                                transferredSourceTextures =
                                    source.Permanent
                                        ? Array.Empty<TextureHandle>()
                                        : source.TextureHandles;
                            TextureOwnershipDelta ownership =
                                ComputeTextureOwnershipDelta(
                                    transferredSourceTextures,
                                    compiled.TextureDependencies,
                                    logicalReferenceCount: 1);
                            if (HasGpuServices)
                            {
                                PreparePendingTextureReleasesLocked(
                                    ownership.Releases);
                            }
                            else
                            {
                                PrepareRetiredTextureReleasesLocked(
                                    ownership.Releases);
                            }
                            // Registration failure rollback may itself need a
                            // durable physical-release record. Reserve it
                            // before acquiring any new texture references.
                            PrepareRetiredTextureReleasesLocked(
                                ownership.Retains);

                            int anticipatedIndex =
                                _freeIndices.Count > 0
                                    ? _freeIndices.Peek()
                                    : _materials.Count;
                            var anticipatedHandle =
                                new MaterialHandle(
                                    anticipatedIndex,
                                    AllocateGeneration(
                                        anticipatedIndex));
                            object boxedReplacement =
                                anticipatedHandle;

                            try
                            {
                                RetainTextureReferencesTransactional(
                                    ownership.Retains);
                            }
                            catch
                            {
                                // The acquisition helper durably records any
                                // rollback release that could not finish.
                                drainTextureReleases = true;
                                throw;
                            }
                            try
                            {
                                publishedHandle =
                                    RegisterCompiledMaterialInternal(
                                        compiled,
                                        permanent: false,
                                        addToDeduplication: false,
                                        primitiveProfileInput:
                                            selectedPrimitiveProfile,
                                        supportsTransportV2:
                                            source.SupportsTransportV2);
                            }
                            catch (Exception publicationFailure)
                            {
                                Exception? rollbackFailure =
                                    ReleaseRetainedTextureReferencesOrRetireLocked(
                                        ownership.Retains);
                                drainTextureReleases = true;
                                if (rollbackFailure != null)
                                {
                                    throw new AggregateException(
                                        "Material copy-on-write publication and texture rollback both failed.",
                                        publicationFailure,
                                        rollbackFailure);
                                }
                                throw;
                            }

                            if (publishedHandle !=
                                anticipatedHandle)
                            {
                                throw new InvalidOperationException(
                                    "Prepared material handle changed during copy-on-write publication.");
                            }

                            if (!source.Permanent)
                            {
                                source.ReferenceCount--;
                                _materials[sourceHandle.Index] =
                                    source;
                            }
                            RecordCompileDiagnosticsLocked(
                                compiled,
                                compileMicroseconds);
                            AdvanceSsgiInputRevisionLocked(
                                changeMask);
                            if (HasGpuServices)
                            {
                                QueuePendingTextureReleasesLocked(
                                    ownership.Releases);
                            }
                            else
                            {
                                QueueRetiredTextureReleasesLocked(
                                    ownership.Releases,
                                    default);
                                drainTextureReleases =
                                    ownership.Releases.Length >
                                    0;
                            }

                            MaterialSlot published =
                                GetValidatedSlotLocked(
                                    publishedHandle);
                            publishedRevisions =
                                published.AspectRevisions;
                            return boxedReplacement;
                        }
                    });
            }
            finally
            {
                if (drainTextureReleases)
                    DrainTextureReleasesBestEffort();
            }

            var result = new MaterialChangedEvent(
                publishedHandle,
                changeMask,
                publishedRevisions);
            MaterialChanged?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Returns a unique handle suitable for per-object editing. Shared
        /// registrations are split without changing texture ownership counts;
        /// the caller must replace its old handle with the returned handle.
        /// </summary>
        public MaterialHandle CreateEditableMaterialCopy(MaterialHandle handle)
        {
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (!slot.Permanent && slot.ReferenceCount <= 1)
                    return handle;

                var compiled = new CompiledMaterialTransport(
                    slot.Definition,
                    slot.Data,
                    GetExtensionDataLocked(slot.Data),
                    slot.TransportProfile,
                    slot.Metadata,
                    slot.TextureHandles,
                    slot.Data.TransportProfileRevision,
                    slot.CompileDiagnostics);
                MaterialHandle copy = RegisterCompiledMaterialInternal(
                    compiled,
                    permanent: false,
                    addToDeduplication: false,
                    primitiveProfileInput: slot.PrimitiveProfileInput,
                    supportsTransportV2: slot.SupportsTransportV2);

                // Commit the logical ownership transfer only after copy
                // registration succeeds. A failed allocation therefore leaves
                // both the source reference count and texture ownership intact.
                if (!slot.Permanent)
                {
                    slot.ReferenceCount--;
                    _materials[handle.Index] = slot;
                }

                // Registration did not load or retain textures, and the old
                // logical reference was transferred rather than released, so
                // physical texture reference counts stay balanced.
                return copy;
            }
        }

        /// <summary>
        /// Recompiles all materials that depend on a changed texture. Texture
        /// statistics and descriptor contents are resolved before each atomic
        /// publish; zero-valued statistics remain valid.
        /// </summary>
        public IReadOnlyList<MaterialChangedEvent> NotifyTextureContentChanged(TextureHandle texture)
        {
            ThrowIfDisposed();
            if (!texture.IsValid)
                throw new ArgumentException(
                    "A valid changed texture handle is required.",
                    nameof(texture));

            for (int attempt = 1;
                 attempt <= MaximumCompilationPublishAttempts;
                 attempt++)
            {
                TextureFanoutSnapshot[] snapshots;
                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    _pendingTextureFanoutRetries.EnsureCapacity(
                        checked(
                            _pendingTextureFanoutRetries.Count +
                            1));
                    if (!_textureDependents.TryGetValue(
                            texture,
                            out HashSet<int>? indices) ||
                        indices.Count == 0)
                    {
                        return Array.Empty<MaterialChangedEvent>();
                    }

                    snapshots = indices
                        .Where(index =>
                            (uint)index <
                                (uint)_materials.Count &&
                            _materials[index].Active)
                        .OrderBy(index => index)
                        .Select(index =>
                        {
                            MaterialSlot slot = _materials[index];
                            return new TextureFanoutSnapshot(
                                new MaterialHandle(
                                    index,
                                    slot.Generation),
                                MaterialCompilationSnapshot.Capture(
                                    slot));
                        })
                        .ToArray();
                }

                if (snapshots.Length == 0)
                    return Array.Empty<MaterialChangedEvent>();

                var compiledFanout =
                    new TextureFanoutCompilation[
                        snapshots.Length];
                for (int index = 0;
                     index < snapshots.Length;
                     index++)
                {
                    TextureFanoutSnapshot fanout =
                        snapshots[index];
                    MaterialChangeMask mask =
                        ClassifyTextureDependencyChange(
                            fanout.Snapshot.Definition,
                            texture);
                    GiMaterialTransportProfile?
                        selectedPrimitiveProfile =
                            InvalidatePrimitiveProfile(
                                fanout.Snapshot
                                    .PrimitiveProfileInput,
                                mask);
                    uint profileRevision = NextNonZero(
                        fanout.Snapshot
                            .TransportProfileRevision);
                    long compileStart =
                        Stopwatch.GetTimestamp();
                    CompiledMaterialTransport compiled =
                        MaterialTransportCompiler.Compile(
                            fanout.Snapshot.Definition,
                            CreateCompilationContext(
                                profileRevision,
                                fanout.Snapshot.Definition
                                    .AlphaCutoff,
                                selectedPrimitiveProfile));
                    long compileMicroseconds =
                        ElapsedMicroseconds(compileStart);
                    ValidateCompiledMaterial(compiled);
                    compiledFanout[index] =
                        new TextureFanoutCompilation(
                            fanout,
                            mask,
                            selectedPrimitiveProfile,
                            compiled,
                            compiled.TextureDependencies
                                .ToArray(),
                            compiled.Diagnostics.ToArray(),
                            compileMicroseconds);
                }

                MaterialChangedEvent[] changes;
                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    bool snapshotsStillCurrent = true;
                    foreach (TextureFanoutCompilation item in
                             compiledFanout)
                    {
                        if (!TryGetValidatedSlotLocked(
                                item.Fanout.Handle,
                                out MaterialSlot current) ||
                            !item.Fanout.Snapshot.Matches(
                                current) ||
                            Array.IndexOf(
                                current.TextureHandles,
                                texture) < 0)
                        {
                            snapshotsStillCurrent = false;
                            break;
                        }

                        if (!current.TextureHandles.SequenceEqual(
                                item.TextureDependencies))
                        {
                            throw new InvalidOperationException(
                                $"Texture recompilation for material {item.Fanout.Handle} changed dependency identity. " +
                                "Content-change fan-out may update derived payloads only.");
                        }
                    }

                    if (!snapshotsStillCurrent)
                        continue;

                    PreflightTextureFanoutPublicationLocked(
                        compiledFanout);
                    changes = PublishTextureFanoutLocked(
                        compiledFanout);
                }

                foreach (MaterialChangedEvent changed in changes)
                    MaterialChanged?.Invoke(changed);
                return changes;
            }

            throw new InvalidOperationException(
                $"Texture-dependent materials changed during each of {MaximumCompilationPublishAttempts} " +
                $"atomic recompilation attempts for texture {texture}. No partial fan-out was published.");
        }

        internal MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            return RegisterMaterial(material, extensionData: null, MaterialRenderMetadata.FromGpuMaterial(material), textureHandles);
        }

        internal MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            return RegisterMaterial(material, extensionData: null, metadata, textureHandles);
        }

        internal MaterialHandle RegisterMaterial(
            GPUMaterialData material,
            GPUMaterialExtensionData? extensionData,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles = null)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (metadata == null)
                    throw new ArgumentNullException(nameof(metadata));
                ValidateMaterialTextureIndices(material);
                MaterialFeatureFlags featureFlags = (MaterialFeatureFlags)material.FeatureFlags;
                if (featureFlags.RequiresExtensionData() && extensionData == null)
                    throw new InvalidOperationException("Materials with feature flags must provide material extension data.");
                if (!featureFlags.RequiresExtensionData() && extensionData.HasValue)
                    throw new InvalidOperationException("Extension data cannot be registered when FeatureFlags does not require an extension payload.");
                if (extensionData.HasValue)
                    ValidateMaterialExtensionTextureIndices(extensionData.Value);

                GPUMaterialData keyMaterial = material;
                keyMaterial.ExtensionDataIndex = -1;
                MaterialDefinition definition = MaterialDefinitionV1Adapter.FromGpuMaterial(
                    material,
                    extensionData,
                    metadata,
                    textureHandles);
                GiMaterialTransportProfile transportProfile =
                    MaterialDefinitionV1Adapter.CreateTransportProfile(material);
                var key = new MaterialRegistrationKey(
                    keyMaterial,
                    extensionData,
                    metadata,
                    definition,
                    transportProfile);
                if (_deduplicatedMaterials.TryGetValue(key, out MaterialHandle existingHandle))
                {
                    MaterialSlot existing = GetValidatedSlotLocked(existingHandle);
                    existing.ReferenceCount =
                        CheckedIncrementReferenceCount(
                            existing.ReferenceCount);
                    _materials[existingHandle.Index] = existing;
                    return existingHandle;
                }

                _legacyV1FallbackCount++;
                return RegisterMaterialInternal(
                    material,
                    extensionData,
                    metadata,
                    textureHandles,
                    permanent: false,
                    definition,
                    transportProfile);
            }
        }

        public int ResolveMaterialIndex(MaterialHandle handle)
        {
            lock (_lock)
            {
                GetValidatedSlotLocked(handle);
                return handle.Index;
            }
        }

        public GPUMaterialData GetMaterialData(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).Data;
        }

        /// <summary>
        /// Updates a material in place for live editing. An edited material is deliberately removed
        /// from content-addressed deduplication so future registrations cannot alias this handle.
        /// Texture ownership and reference counting remain unchanged.
        /// </summary>
        internal void UpdateMaterial(MaterialHandle handle, in GPUMaterialData material)
        {
            MaterialChangedEvent changed;
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    throw new InvalidOperationException("The permanent default material cannot be edited in place.");

                ValidateMaterialTextureIndices(material);
                GPUMaterialData updated = material;
                updated.ExtensionDataIndex = slot.Data.ExtensionDataIndex;
                updated.TextureContentRevision = slot.Data.TextureContentRevision;
                updated.PackedMeanGiDirectionalDiffuseBaseRg = 0;
                updated.PackedMeanGiDirectionalDiffuseBaseBAndF0R = 0;
                updated.PackedMeanGiDielectricF0Gb = 0;
                updated.DdgiAverageTransmission = Vector4.Zero;
                if (updated.FeatureFlags != slot.Data.FeatureFlags)
                {
                    throw new InvalidOperationException(
                        "Changing material feature flags requires registering a material with matching extension data.");
                }

                if (_deduplicatedMaterials.TryGetValue(slot.RegistrationKey, out MaterialHandle mappedHandle) && mappedHandle == handle)
                    _deduplicatedMaterials.Remove(slot.RegistrationKey);

                RemoveActiveProfileClassificationLocked(slot);
                slot.Data = updated;
                // Raw GPU-payload editing is the V1 compatibility surface. It
                // cannot remain classified as authored V2 merely because the
                // handle was originally produced by the canonical compiler.
                slot.SupportsTransportV2 = false;
                ApplyTransportInterpretation(
                    ref slot.Data,
                    slot.SupportsTransportV2,
                    _transportV2Enabled);
                slot.ContentRevision = NextMaterialContentRevisionLocked();
                slot.Data.MaterialRevision = slot.ContentRevision;
                slot.Definition = MaterialDefinitionV1Adapter.FromGpuMaterial(
                    updated,
                    GetExtensionDataLocked(updated),
                    MaterialRenderMetadata.FromGpuMaterial(updated),
                    slot.TextureHandles);
                slot.TransportProfile = MaterialDefinitionV1Adapter.CreateTransportProfile(updated);
                slot.Metadata = MaterialRenderMetadata.FromGpuMaterial(updated);
                slot.AspectRevisions = AdvanceAspectRevisions(
                    slot.AspectRevisions,
                    MaterialChangeMask.All,
                    slot.ContentRevision);
                slot.RegistrationKey = default;
                _materials[handle.Index] = slot;
                AddActiveProfileClassificationLocked(slot);
                _legacyV1FallbackCount++;
                MarkMaterialDataDirtyLocked();
                AdvanceSsgiInputRevisionLocked(MaterialChangeMask.All);
                changed = new MaterialChangedEvent(handle, MaterialChangeMask.All, slot.AspectRevisions);
            }
            MaterialChanged?.Invoke(changed);
        }

        public GPUMaterialExtensionData? GetMaterialExtensionData(MaterialHandle handle)
        {
            lock (_lock)
            {
                GPUMaterialData data = GetValidatedSlotLocked(handle).Data;
                return data.ExtensionDataIndex >= 0 && data.ExtensionDataIndex < _materialExtensions.Count
                    ? _materialExtensions[data.ExtensionDataIndex]
                    : null;
            }
        }

        public IReadOnlyList<TextureHandle> GetMaterialTextures(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).TextureHandles.ToArray();
        }

        /// <summary>
        /// Returns the live material texture set once per physical texture handle. This is used
        /// by render-graph imports; descriptor slots alone are not sufficient to synchronize a
        /// DDGI ray-query pass with later graphics sampling.
        /// </summary>
        public IReadOnlyList<TextureHandle> GetReferencedTextureHandles()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                EnsureReferencedTextureCacheLocked();
                return _referencedTextureCache;
            }
        }

        /// <summary>
        /// Changes only when the set of physical texture handles referenced by live materials
        /// changes. Scalar material edits therefore do not invalidate a resource plan.
        /// </summary>
        public ulong ReferencedTextureSetGeneration
        {
            get
            {
                lock (_lock)
                {
                    ThrowIfDisposedLocked();
                    EnsureReferencedTextureCacheLocked();
                    return _referencedTextureSetGeneration;
                }
            }
        }

        private void EnsureReferencedTextureCacheLocked()
        {
            if (_referencedTextureCacheRevision == _materialDataRevision)
                return;

            var handles = new HashSet<TextureHandle>();
            foreach (MaterialSlot slot in _materials)
            {
                if (!slot.Active)
                    continue;

                foreach (TextureHandle handle in slot.TextureHandles)
                {
                    if (handle.IsValid)
                        handles.Add(handle);
                }
            }

            TextureHandle[] ordered = handles
                .OrderBy(static handle => handle.Index)
                .ThenBy(static handle => handle.Generation)
                .ToArray();
            bool changed = _referencedTextureCacheRevision == uint.MaxValue ||
                _referencedTextureCache.Count != ordered.Length;
            if (!changed)
            {
                for (int index = 0; index < ordered.Length; index++)
                {
                    if (_referencedTextureCache[index] == ordered[index])
                        continue;
                    changed = true;
                    break;
                }
            }

            if (changed)
            {
                _referencedTextureCache = ordered.Length == 0
                    ? Array.Empty<TextureHandle>()
                    : Array.AsReadOnly(ordered);
                _referencedTextureSetGeneration++;
                if (_referencedTextureSetGeneration == 0)
                    _referencedTextureSetGeneration = 1;
            }

            _referencedTextureCacheRevision = _materialDataRevision;
        }

        public MaterialRenderMetadata GetMaterialMetadata(MaterialHandle handle)
        {
            lock (_lock)
                return GetValidatedSlotLocked(handle).Metadata;
        }

        public GPUMaterialData[] GetMaterialDataSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                var snapshot = new GPUMaterialData[_materials.Count];
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active ? _materials[i].Data : CreateDefaultMaterial();

                return snapshot;
            }
        }

        public GPUMaterialExtensionData[] GetMaterialExtensionDataSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                return _materialExtensions.ToArray();
            }
        }

        public MaterialRenderMetadata[] GetMaterialMetadataSnapshot()
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                var snapshot = new MaterialRenderMetadata[_materials.Count];
                MaterialRenderMetadata defaultMetadata = MaterialRenderMetadata.FromGpuMaterial(CreateDefaultMaterial());
                for (int i = 0; i < _materials.Count; i++)
                    snapshot[i] = _materials[i].Active ? _materials[i].Metadata : defaultMetadata;

                return snapshot;
            }
        }

        public void ReleaseMaterial(MaterialHandle handle, Fence retireFence = default)
        {
            bool drainTextureReleases = false;
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    return;

                if (slot.ReferenceCount > 1)
                {
                    PrepareMaterialTextureReleasesLocked(
                        slot,
                        logicalReferenceCount: 1);
                    slot.ReferenceCount--;
                    _materials[handle.Index] = slot;
                    QueueMaterialTextureReleasesLocked(
                        slot,
                        retireFence,
                        logicalReferenceCount: 1);
                }
                else
                {
                    DestroyMaterialSlotLocked(
                        handle,
                        slot,
                        retireFence,
                        logicalReferenceCount: 1);
                }
                drainTextureReleases = true;
            }

            if (drainTextureReleases)
                DrainTextureReleasesBestEffort();
        }

        /// <summary>
        /// Acquires one logical material reference and one physical texture
        /// reference per bound occurrence. This is used when a model instance
        /// shares a template's immutable material handle.
        /// </summary>
        public void RetainMaterial(MaterialHandle handle)
        {
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    return;

                int finalReferenceCount =
                    checked(slot.ReferenceCount + 1);
                TextureOwnershipDelta ownership =
                    ComputeTextureOwnershipDelta(
                        Array.Empty<TextureHandle>(),
                        slot.TextureHandles,
                        logicalReferenceCount: 1);
                RetainTextureReferencesTransactional(
                    ownership.Retains);

                slot.ReferenceCount = finalReferenceCount;
                _materials[handle.Index] = slot;
            }
        }

        public void DestroyMaterial(MaterialHandle handle, Fence retireFence = default)
        {
            bool drainTextureReleases = false;
            lock (_lock)
            {
                MaterialSlot slot = GetValidatedSlotLocked(handle);
                if (slot.Permanent)
                    throw new InvalidOperationException("The canonical default material cannot be destroyed.");

                DestroyMaterialSlotLocked(
                    handle,
                    slot,
                    retireFence,
                    slot.ReferenceCount);
                drainTextureReleases = true;
            }

            if (drainTextureReleases)
                DrainTextureReleasesBestEffort();
        }

        public void UploadMaterials(CommandBuffer commandBuffer)
        {
            ThrowIfDisposed();
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for material upload.", nameof(commandBuffer));
            if (!HasGpuServices)
                throw new InvalidOperationException("Material GPU upload requires renderer GPU services.");

            bool drainTextureReleases = false;
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                RepairMaterialBindingsLocked();
                DrainRetiredMaterialBuffersBestEffortLocked();
                long uploadStart = Stopwatch.GetTimestamp();
                _lastUploadBytes = 0;
                _lastExtensionUploadBytes = 0;
                EnsureMaterialBufferCapacityLocked((uint)Math.Max(1, _materials.Count));
                EnsureMaterialExtensionBufferCapacityLocked((uint)Math.Max(1, _materialExtensions.Count));

                bool uploadedMaterialData = _gpuUploadDirty;
                if (_gpuUploadDirty)
                {
                    GPUMaterialData[] snapshot = GetMaterialDataSnapshotLocked();
                    _lastUploadBytes = UploadMaterialSpan(snapshot, commandBuffer);
                    GPUMaterialExtensionData[] extensionSnapshot = GetMaterialExtensionDataSnapshotLocked();
                    _lastExtensionUploadBytes = UploadMaterialExtensionSpan(extensionSnapshot, commandBuffer);
                    _gpuUploadDirty = false;
                }

                RecordMaterialReadBarrier(commandBuffer);
                RecordMaterialExtensionReadBarrier(commandBuffer);
                UpdateRegisteredBindlessBuffer();
                _lastUploadMicroseconds = ElapsedMicroseconds(uploadStart);
                if (uploadedMaterialData)
                    _uploadLatencies.Add(_lastUploadMicroseconds);

                if (_pendingTextureReleases.Count > 0)
                {
                    Fence retireFence =
                        _sync!.GetInFlightFence(
                            _stagingRing!.CurrentFrameIndex);
                    MovePendingTextureReleasesToRetiredLocked(
                        retireFence);
                    drainTextureReleases = true;
                }
            }

            // This fence covers the material-buffer update above and every
            // submitted draw that can still observe the previous descriptor.
            if (drainTextureReleases)
                DrainTextureReleasesBestEffort();
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            ThrowIfDisposed();
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (!_materialBuffer.IsValid)
                throw new InvalidOperationException("Material GPU buffer has not been created.");
            if (!_materialExtensionBuffer.IsValid)
                throw new InvalidOperationException("Material extension GPU buffer has not been created.");

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffer();
            }
        }

        private MaterialHandle RegisterMaterialInternal(
            GPUMaterialData material,
            GPUMaterialExtensionData? extensionData,
            MaterialRenderMetadata metadata,
            IReadOnlyList<TextureHandle>? textureHandles,
            bool permanent,
            MaterialDefinition definition,
            GiMaterialTransportProfile? transportProfile = null,
            GiMaterialTransportProfile? primitiveProfileInput = null,
            IReadOnlyList<string>? compileDiagnostics = null,
            bool addToDeduplication = true,
            bool supportsTransportV2 = false)
        {
            ThrowIfDisposedLocked();
            EnsurePrimitiveProfileAdmissionLocked(
                previousProfile: null,
                nextProfile: primitiveProfileInput);
            bool reuseMaterialSlot =
                _freeIndices.Count > 0;
            int index = reuseMaterialSlot
                ? _freeIndices.Peek()
                : _materials.Count;
            uint generation = AllocateGeneration(index);
            TextureHandle[] textureHandleArray = CopyTextureHandles(textureHandles);
            string[] diagnosticArray =
                compileDiagnostics?.ToArray() ??
                Array.Empty<string>();
            GPUMaterialData storedMaterial = material;
            storedMaterial.ExtensionDataIndex = -1;
            if (!supportsTransportV2)
            {
                // Raw/V1 registrations do not carry the six-half directional
                // transport profile. Keep those compatibility payloads
                // explicitly zero instead of interpreting old reserved bytes.
                storedMaterial.PackedMeanGiDirectionalDiffuseBaseRg = 0;
                storedMaterial.PackedMeanGiDirectionalDiffuseBaseBAndF0R = 0;
                storedMaterial.PackedMeanGiDielectricF0Gb = 0;
                storedMaterial.DdgiAverageTransmission = Vector4.Zero;
            }
            ApplyTransportInterpretation(
                ref storedMaterial,
                supportsTransportV2,
                _transportV2Enabled);

            bool reuseExtensionSlot =
                extensionData.HasValue &&
                _freeMaterialExtensionIndices.Count > 0;
            int extensionIndex = extensionData.HasValue
                ? reuseExtensionSlot
                    ? _freeMaterialExtensionIndices.Peek()
                    : _materialExtensions.Count
                : -1;
            storedMaterial.ExtensionDataIndex =
                extensionIndex;

            GPUMaterialData keyMaterial = material;
            keyMaterial.ExtensionDataIndex = -1;
            GiMaterialTransportProfile resolvedTransportProfile =
                transportProfile ?? MaterialDefinitionV1Adapter.CreateTransportProfile(storedMaterial);
            var registrationKey = new MaterialRegistrationKey(
                keyMaterial,
                extensionData,
                metadata,
                definition,
                resolvedTransportProfile);
            uint contentRevision = NextNonZero(
                _materialContentRevisionSerial);
            storedMaterial.MaterialRevision = contentRevision;

            var slot = new MaterialSlot
            {
                Data = storedMaterial,
                Definition = definition,
                TransportProfile = resolvedTransportProfile,
                PrimitiveProfileInput = primitiveProfileInput,
                SupportsTransportV2 = supportsTransportV2,
                CompileDiagnostics = diagnosticArray,
                AspectRevisions = new MaterialAspectRevisions(
                    contentRevision,
                    contentRevision,
                    contentRevision,
                    contentRevision,
                    contentRevision,
                    contentRevision,
                    contentRevision),
                Generation = generation,
                ContentRevision = contentRevision,
                Active = true,
                Permanent = permanent,
                ReferenceCount = 1,
                TextureHandles = textureHandleArray,
                Metadata = metadata,
                RegistrationKey = addToDeduplication ? registrationKey : default
            };

            var handle = new MaterialHandle(index, generation);
            TextureHandle[] uniqueDependencies =
                textureHandleArray
                    .Where(texture => texture.IsValid)
                    .Distinct()
                    .ToArray();
            var newDependencySets =
                new Dictionary<TextureHandle, HashSet<int>>();

            // Allocate every managed publication input before consuming free
            // slots or making the material visible.
            _materials.EnsureCapacity(
                checked(index + 1));
            if (extensionData.HasValue &&
                !reuseExtensionSlot)
            {
                _materialExtensions.EnsureCapacity(
                    checked(_materialExtensions.Count + 1));
            }
            if (addToDeduplication)
            {
                if (_deduplicatedMaterials.TryGetValue(
                        registrationKey,
                        out MaterialHandle existing))
                {
                    throw new InvalidOperationException(
                        $"Material registration key is already owned by {existing}.");
                }
                _deduplicatedMaterials.EnsureCapacity(
                    checked(
                        _deduplicatedMaterials.Count + 1));
            }
            foreach (TextureHandle texture in
                     uniqueDependencies)
            {
                if (_textureDependents.TryGetValue(
                        texture,
                        out HashSet<int>? dependents))
                {
                    if (dependents.Contains(index))
                    {
                        throw new InvalidOperationException(
                            $"Inactive material slot {index} is still tracked for texture {texture}.");
                    }
                    dependents.EnsureCapacity(
                        checked(dependents.Count + 1));
                }
                else
                {
                    var prepared = new HashSet<int>(1)
                    {
                        index
                    };
                    newDependencySets.Add(
                        texture,
                        prepared);
                }
            }
            _textureDependents.EnsureCapacity(
                checked(
                    _textureDependents.Count +
                    newDependencySets.Count));
            RegistrationPublicationFaultInjector?.Invoke(
                MaterialRegistrationPublicationStage
                    .AfterPreflight);

            MaterialSlot previousSlot =
                reuseMaterialSlot
                    ? _materials[index]
                    : default;
            GPUMaterialExtensionData previousExtension =
                reuseExtensionSlot
                    ? _materialExtensions[extensionIndex]
                    : default;
            int previousMaterialCount = _materials.Count;
            int previousExtensionCount =
                _materialExtensions.Count;
            uint previousContentRevision =
                _materialContentRevisionSerial;
            uint previousDataRevision =
                _materialDataRevision;
            bool previousUploadDirty = _gpuUploadDirty;
            int previousPrimitiveCount =
                _activePrimitiveProfileCount;
            int previousLegacyCount =
                _activeLegacyV1FallbackCount;
            int previousInvalidCount =
                _activeInvalidProfileCount;
            bool materialFreeSlotPopped = false;
            bool extensionFreeSlotPopped = false;
            bool deduplicationAdded = false;
            var dependencyPublications =
                new List<TextureHandle>(
                    uniqueDependencies.Length);

            try
            {
                if (reuseMaterialSlot)
                {
                    int actual = _freeIndices.Pop();
                    materialFreeSlotPopped = true;
                    if (actual != index)
                    {
                        throw new InvalidOperationException(
                            "Prepared material free slot changed before publication.");
                    }
                }
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterFreeSlotReservation);
                if (extensionData.HasValue)
                {
                    if (reuseExtensionSlot)
                    {
                        int actual =
                            _freeMaterialExtensionIndices.Pop();
                        extensionFreeSlotPopped = true;
                        if (actual != extensionIndex)
                        {
                            throw new InvalidOperationException(
                                "Prepared extension free slot changed before publication.");
                        }
                        _materialExtensions[extensionIndex] =
                            extensionData.Value;
                    }
                    else
                    {
                        _materialExtensions.Add(
                            extensionData.Value);
                    }
                }
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterExtensionPublication);

                if (reuseMaterialSlot)
                    _materials[index] = slot;
                else
                    _materials.Add(slot);
                _materialContentRevisionSerial =
                    contentRevision;
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterSlotPublication);

                if (addToDeduplication)
                {
                    _deduplicatedMaterials.Add(
                        registrationKey,
                        handle);
                    deduplicationAdded = true;
                }
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterDeduplicationPublication);
                foreach (TextureHandle texture in
                         uniqueDependencies)
                {
                    dependencyPublications.Add(texture);
                    if (newDependencySets.TryGetValue(
                            texture,
                            out HashSet<int>? prepared))
                    {
                        _textureDependents.Add(
                            texture,
                            prepared);
                    }
                    else
                    {
                        HashSet<int> dependents =
                            _textureDependents[texture];
                        if (!dependents.Add(index))
                        {
                            throw new InvalidOperationException(
                                $"Material slot {index} is already tracked for texture {texture}.");
                        }
                    }
                }
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterDependencyPublication);

                AddActiveProfileClassificationLocked(slot);
                RegistrationPublicationFaultInjector?.Invoke(
                    MaterialRegistrationPublicationStage
                        .AfterClassificationPublication);
                MarkMaterialDataDirtyLocked();
                return handle;
            }
            catch
            {
                for (int dependencyIndex =
                         dependencyPublications.Count - 1;
                     dependencyIndex >= 0;
                     dependencyIndex--)
                {
                    TextureHandle texture =
                        dependencyPublications[
                            dependencyIndex];
                    if (_textureDependents.TryGetValue(
                            texture,
                            out HashSet<int>? dependents))
                    {
                        dependents.Remove(index);
                        if (dependents.Count == 0)
                            _textureDependents.Remove(texture);
                    }
                }
                if (deduplicationAdded)
                    _deduplicatedMaterials.Remove(registrationKey);

                if (reuseMaterialSlot)
                    _materials[index] = previousSlot;
                else
                    CollectionsMarshal.SetCount(
                        _materials,
                        previousMaterialCount);
                if (extensionData.HasValue)
                {
                    if (reuseExtensionSlot)
                        _materialExtensions[extensionIndex] =
                            previousExtension;
                    else
                        CollectionsMarshal.SetCount(
                            _materialExtensions,
                            previousExtensionCount);
                }
                if (extensionFreeSlotPopped)
                    _freeMaterialExtensionIndices.Push(
                        extensionIndex);
                if (materialFreeSlotPopped)
                    _freeIndices.Push(index);

                _materialContentRevisionSerial =
                    previousContentRevision;
                _materialDataRevision =
                    previousDataRevision;
                _gpuUploadDirty = previousUploadDirty;
                _activePrimitiveProfileCount =
                    previousPrimitiveCount;
                _activeLegacyV1FallbackCount =
                    previousLegacyCount;
                _activeInvalidProfileCount =
                    previousInvalidCount;
                throw;
            }
        }

        private MaterialHandle RegisterCompiledMaterialInternal(
            CompiledMaterialTransport compiled,
            bool permanent,
            bool addToDeduplication = true,
            GiMaterialTransportProfile? primitiveProfileInput = null,
            bool supportsTransportV2 = true)
        {
            ValidateCompiledMaterial(compiled);
            return RegisterMaterialInternal(
                compiled.GpuMaterial,
                compiled.ExtensionData,
                compiled.Metadata,
                compiled.TextureDependencies,
                permanent,
                compiled.Definition,
                compiled.TransportProfile,
                primitiveProfileInput,
                compiled.Diagnostics,
                addToDeduplication,
                supportsTransportV2);
        }

        private void DestroyMaterialSlotLocked(
            MaterialHandle handle,
            MaterialSlot slot,
            Fence retireFence,
            int logicalReferenceCount)
        {
            if (logicalReferenceCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(logicalReferenceCount));
            int retiredExtensionIndex = slot.Data.ExtensionDataIndex;
            PrepareMaterialExtensionRetirementLocked(retiredExtensionIndex);
            PrepareMaterialTextureReleasesLocked(
                slot,
                logicalReferenceCount);
            _freeIndices.EnsureCapacity(
                checked(_freeIndices.Count + 1));
            RemoveActiveProfileClassificationLocked(slot);
            _deduplicatedMaterials.Remove(slot.RegistrationKey);
            RemoveTextureDependenciesLocked(handle.Index, slot.TextureHandles);
            QueueMaterialTextureReleasesLocked(
                slot,
                retireFence,
                logicalReferenceCount);

            slot.Active = false;
            slot.ReferenceCount = 0;
            slot.Generation = NextGeneration(slot.Generation);
            slot.TextureHandles = Array.Empty<TextureHandle>();
            slot.Data = CreateDefaultMaterial();
            slot.Definition = MaterialDefinition.Default;
            slot.TransportProfile = GiMaterialTransportProfile.Invalid;
            slot.PrimitiveProfileInput = null;
            slot.SupportsTransportV2 = false;
            slot.CompileDiagnostics = Array.Empty<string>();
            slot.ContentRevision = NextMaterialContentRevisionLocked();
            slot.Data.MaterialRevision = slot.ContentRevision;
            slot.AspectRevisions = default;
            slot.Metadata = MaterialRenderMetadata.FromGpuMaterial(slot.Data);
            slot.RegistrationKey = default;
            _materials[handle.Index] = slot;
            _freeIndices.Push(handle.Index);
            ReleaseMaterialExtensionDataLocked(retiredExtensionIndex);
            MarkMaterialDataDirtyLocked();
        }

        private void PrepareMaterialTextureReleasesLocked(
            MaterialSlot slot,
            int logicalReferenceCount = 1)
        {
            if (_textureReferences == null)
                return;
            if (logicalReferenceCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(logicalReferenceCount));

            int validTextureCount = 0;
            foreach (TextureHandle textureHandle in slot.TextureHandles)
            {
                if (textureHandle.IsValid)
                    validTextureCount++;
            }
            _retiredTextureReleases.EnsureCapacity(
                checked(
                    _retiredTextureReleases.Count +
                    validTextureCount));
        }

        private void QueueMaterialTextureReleasesLocked(
            MaterialSlot slot,
            Fence retireFence,
            int logicalReferenceCount = 1)
        {
            if (_textureReferences == null)
                return;

            foreach (TextureHandle textureHandle in slot.TextureHandles)
            {
                if (!textureHandle.IsValid)
                    continue;
                _retiredTextureReleases.Add(
                    new PendingTextureRelease(
                        textureHandle,
                        retireFence,
                        logicalReferenceCount));
            }
        }

        /// <summary>
        /// Retries every durable physical texture release. Successful
        /// occurrences are removed immediately, so a later retry cannot
        /// release them twice.
        /// </summary>
        public void FlushTextureReleases()
        {
            Exception? failure = DrainTextureReleases();
            if (failure != null)
            {
                throw new InvalidOperationException(
                    "One or more retired material texture references remain pending.",
                    failure);
            }
        }

        private void DrainTextureReleasesBestEffort()
        {
            _ = DrainTextureReleases();
        }

        private Exception? DrainTextureReleases()
        {
            if (_textureReferences == null)
                return null;

            lock (_lock)
            {
                int currentThreadId =
                    Environment.CurrentManagedThreadId;
                while (_drainingTextureReleases)
                {
                    if (_textureReleaseDrainThreadId ==
                        currentThreadId)
                    {
                        return new InvalidOperationException(
                            "A material texture-release flush cannot re-enter its active drain.");
                    }
                    Monitor.Wait(_lock);
                }
                _drainingTextureReleases = true;
                _textureReleaseDrainThreadId =
                    currentThreadId;
            }

            try
            {
                while (true)
                {
                    PendingTextureRelease pending;
                    lock (_lock)
                    {
                        while (_retiredTextureReleaseHead <
                                   _retiredTextureReleases.Count &&
                               _retiredTextureReleases[
                                       _retiredTextureReleaseHead]
                                   .RemainingCount == 0)
                        {
                            _retiredTextureReleaseHead++;
                        }
                        if (_retiredTextureReleaseHead ==
                            _retiredTextureReleases.Count)
                        {
                            _retiredTextureReleases.Clear();
                            _retiredTextureReleaseHead = 0;
                            return null;
                        }
                        pending = _retiredTextureReleases[
                            _retiredTextureReleaseHead];
                    }

                    try
                    {
                        _textureReferences.ReleaseTexture(
                            pending.Handle,
                            pending.RetireFence);
                    }
                    catch (Exception releaseFailure)
                    {
                        lock (_lock)
                        {
                            _textureReleaseFailureCount =
                                _textureReleaseFailureCount ==
                                long.MaxValue
                                    ? long.MaxValue
                                    : _textureReleaseFailureCount + 1;
                            _lastTextureReleaseFailure =
                                releaseFailure;
                            CompactRetiredTextureReleasePrefixLocked();
                        }
                        return releaseFailure;
                    }

                    lock (_lock)
                    {
                        PendingTextureRelease current =
                            _retiredTextureReleases[
                                _retiredTextureReleaseHead];
                        if (current != pending)
                        {
                            throw new InvalidOperationException(
                                "Material texture retirement order changed while a release was in flight.");
                        }

                        current = current with
                        {
                            RemainingCount =
                                current.RemainingCount - 1
                        };
                        if (current.RemainingCount == 0)
                            _retiredTextureReleaseHead++;
                        else
                        {
                            _retiredTextureReleases[
                                _retiredTextureReleaseHead] =
                                current;
                        }
                        _lastTextureReleaseFailure = null;
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _drainingTextureReleases = false;
                    _textureReleaseDrainThreadId = 0;
                    Monitor.PulseAll(_lock);
                }
            }
        }

        private void CompactRetiredTextureReleasePrefixLocked()
        {
            if (_retiredTextureReleaseHead <= 0)
                return;
            if (_retiredTextureReleaseHead >=
                _retiredTextureReleases.Count)
            {
                _retiredTextureReleases.Clear();
                _retiredTextureReleaseHead = 0;
                return;
            }

            _retiredTextureReleases.RemoveRange(
                0,
                _retiredTextureReleaseHead);
            _retiredTextureReleaseHead = 0;
        }

        private MaterialSlot GetValidatedSlotLocked(MaterialHandle handle)
        {
            ThrowIfDisposedLocked();
            if (!handle.IsValid)
                throw new InvalidOperationException("Invalid material handle.");
            if (handle.Index >= _materials.Count)
                throw new InvalidOperationException(
                    $"Material handle index {handle.Index} is outside the registered material table.");

            MaterialSlot slot = _materials[handle.Index];
            if (!slot.Active)
                throw new InvalidOperationException($"Material handle {handle} references a destroyed material.");
            if (slot.Generation != handle.Generation)
            {
                throw new InvalidOperationException(
                    $"Material handle generation mismatch for index {handle.Index}: " +
                    $"handle generation {handle.Generation}, current generation {slot.Generation}.");
            }

            return slot;
        }

        private bool TryGetValidatedSlotLocked(MaterialHandle handle, out MaterialSlot slot)
        {
            ThrowIfDisposedLocked();
            slot = default;
            if (!handle.IsValid || (uint)handle.Index >= (uint)_materials.Count)
                return false;

            MaterialSlot candidate = _materials[handle.Index];
            if (!candidate.Active || candidate.Generation != handle.Generation)
                return false;

            slot = candidate;
            return true;
        }

        private static InvalidOperationException
            CreateConcurrentRenderObjectEditException(
                MaterialHandle handle) =>
            new(
                $"Material {handle} or its render-object ownership changed while an authored edit was being prepared. No stale edit was published.");

        private void ThrowIfDisposed()
        {
            lock (_lock)
                ThrowIfDisposedLocked();
        }

        private void ThrowIfDisposedLocked() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        internal static int CheckedIncrementReferenceCount(
            int currentReferenceCount)
        {
            if (currentReferenceCount <= 0)
            {
                throw new InvalidOperationException(
                    "An active material must have a positive reference count.");
            }

            return checked(currentReferenceCount + 1);
        }

        private void EnsureMaterialBufferCapacityLocked(uint requiredMaterialCount)
        {
            if (_bufferManager == null || requiredMaterialCount <= _materialBufferCapacity)
                return;

            WaitForOtherInFlightFrames();

            uint newCapacity = _materialBufferCapacity == 0 ? InitialMaterialCapacity : _materialBufferCapacity;
            while (newCapacity < requiredMaterialCount)
                newCapacity = checked(newCapacity * 2);
            ReplaceMaterialBufferLocked(
                newCapacity,
                replaceExtensionBuffer: false);
        }

        private void EnsureMaterialExtensionBufferCapacityLocked(uint requiredExtensionCount)
        {
            if (_bufferManager == null || requiredExtensionCount <= _materialExtensionBufferCapacity)
                return;

            WaitForOtherInFlightFrames();

            uint newCapacity = _materialExtensionBufferCapacity == 0 ? 1u : _materialExtensionBufferCapacity;
            while (newCapacity < requiredExtensionCount)
                newCapacity = checked(newCapacity * 2);
            ReplaceMaterialBufferLocked(
                newCapacity,
                replaceExtensionBuffer: true);
        }

        private void ReplaceMaterialBufferLocked(
            uint newCapacity,
            bool replaceExtensionBuffer)
        {
            if (_bufferManager == null)
            {
                throw new InvalidOperationException(
                    "Material buffer replacement requires a BufferManager.");
            }

            _retiredMaterialBuffers.EnsureCapacity(
                checked(
                    _retiredMaterialBuffers.Count +
                    _quarantinedMaterialBuffers.Count +
                    1));
            _quarantinedMaterialBuffers.EnsureCapacity(
                checked(
                    _quarantinedMaterialBuffers.Count +
                    1));

            BufferHandle oldMaterialBuffer =
                _materialBuffer;
            BufferHandle oldExtensionBuffer =
                _materialExtensionBuffer;
            uint oldMaterialCapacity =
                _materialBufferCapacity;
            uint oldExtensionCapacity =
                _materialExtensionBufferCapacity;
            BufferHandle candidate =
                replaceExtensionBuffer
                    ? CreateMaterialExtensionBuffer(
                        newCapacity)
                    : CreateMaterialBuffer(newCapacity);
            BufferHandle candidateMaterialBuffer =
                replaceExtensionBuffer
                    ? oldMaterialBuffer
                    : candidate;
            BufferHandle candidateExtensionBuffer =
                replaceExtensionBuffer
                    ? candidate
                    : oldExtensionBuffer;

            try
            {
                MaterialBufferReplacementTransaction.Execute(
                    publishCandidateBinding: () =>
                        PublishRegisteredBindlessBuffers(
                            candidateMaterialBuffer,
                            candidateExtensionBuffer,
                            restoringAuthoritativeBinding:
                                false),
                    commitAuthoritativeState: () =>
                    {
                        if (replaceExtensionBuffer)
                        {
                            _materialExtensionBuffer =
                                candidate;
                            _materialExtensionBufferCapacity =
                                newCapacity;
                        }
                        else
                        {
                            _materialBuffer = candidate;
                            _materialBufferCapacity =
                                newCapacity;
                        }

                        MarkMaterialDataDirtyLocked();
                    },
                    restoreAuthoritativeBinding: () =>
                    {
                        try
                        {
                            PublishRegisteredBindlessBuffers(
                                oldMaterialBuffer,
                                oldExtensionBuffer,
                                restoringAuthoritativeBinding:
                                    true);
                        }
                        catch (Exception restorationFailure)
                        {
                            _materialBindingRepairRequired =
                                true;
                            _lastMaterialBindingPublicationFailure =
                                restorationFailure;
                            throw;
                        }
                    },
                    destroyCandidate: () =>
                        _bufferManager.DestroyBuffer(
                            candidate),
                    retireCandidate: () =>
                        _retiredMaterialBuffers.Add(
                            candidate),
                    quarantineCandidate: () =>
                    {
                        _quarantinedMaterialBuffers.Add(
                            candidate);
                        _materialBindingRepairRequired =
                            true;
                    },
                    reportDeferredCandidateCleanup:
                        RecordRetiredBufferCleanupFailureLocked);
            }
            catch (Exception publicationFailure)
            {
                // Authoritative fields are committed only after descriptor
                // publication. This defensive assignment also covers a future
                // commit hook that might throw after a partial field update.
                _materialBuffer =
                    oldMaterialBuffer;
                _materialExtensionBuffer =
                    oldExtensionBuffer;
                _materialBufferCapacity =
                    oldMaterialCapacity;
                _materialExtensionBufferCapacity =
                    oldExtensionCapacity;
                if (_materialBindingRepairRequired)
                {
                    _lastMaterialBindingPublicationFailure =
                        publicationFailure;
                }

                throw;
            }

            BufferHandle retired = replaceExtensionBuffer
                ? oldExtensionBuffer
                : oldMaterialBuffer;
            if (retired.IsValid)
                _retiredMaterialBuffers.Add(retired);
            DrainRetiredMaterialBuffersBestEffortLocked();
        }

        private void MarkMaterialDataDirtyLocked()
        {
            _gpuUploadDirty = true;
            _materialDataRevision++;
            if (_materialDataRevision == 0)
                _materialDataRevision = 1;
        }

        private static void ApplyTransportInterpretation(
            ref GPUMaterialData data,
            bool supportsTransportV2,
            bool enabled)
        {
            const uint legacyFlag = (uint)GiMaterialTransportFlags.LegacyV1Fallback;
            if (supportsTransportV2 && enabled)
                data.TransportFlags &= ~legacyFlag;
            else
                data.TransportFlags |= legacyFlag;
        }

        private void AdvanceSsgiInputRevisionLocked(MaterialChangeMask changeMask)
        {
            if (!AffectsSsgiInputs(changeMask))
                return;

            _ssgiInputRevision = NextNonZero(_ssgiInputRevision);
        }

        internal static bool AffectsSsgiInputs(MaterialChangeMask changeMask)
        {
            const MaterialChangeMask ssgiInputs =
                MaterialChangeMask.DiffuseTransport |
                MaterialChangeMask.Emission |
                MaterialChangeMask.AlphaCoverage |
                MaterialChangeMask.ShadingModel;
            return (changeMask & ssgiInputs) != 0;
        }

        private uint NextMaterialContentRevisionLocked()
        {
            _materialContentRevisionSerial++;
            if (_materialContentRevisionSerial == 0)
                _materialContentRevisionSerial = 1;
            return _materialContentRevisionSerial;
        }

        private uint NextTextureContentRevisionLocked()
        {
            _textureContentRevisionSerial++;
            if (_textureContentRevisionSerial == 0)
                _textureContentRevisionSerial = 1;
            return _textureContentRevisionSerial;
        }

        private BufferHandle CreateMaterialBuffer(uint materialCapacity)
        {
            if (_bufferManager == null)
                throw new InvalidOperationException("Material GPU buffer creation requires a BufferManager.");

            return _bufferManager.CreateDeviceBuffer(
                checked(materialCapacity * MaterialStride),
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true,
                MemoryBudgetCategory.MaterialBuffers,
                "Material Data Buffer");
        }

        private BufferHandle CreateMaterialExtensionBuffer(uint extensionCapacity)
        {
            if (_bufferManager == null)
                throw new InvalidOperationException("Material extension GPU buffer creation requires a BufferManager.");

            return _bufferManager.CreateDeviceBuffer(
                checked(extensionCapacity * MaterialExtensionStride),
                BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.TransferSrcBit,
                true,
                MemoryBudgetCategory.MaterialBuffers,
                "Material Extension Data Buffer");
        }

        private void WaitForOtherInFlightFrames()
        {
            if (_sync == null || _stagingRing == null)
                return;

            int currentFrame = _stagingRing.CurrentFrameIndex;
            for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            {
                if (i != currentFrame)
                    _sync.WaitForFence(i);
            }
        }

        private ulong UploadMaterialSpan(ReadOnlySpan<GPUMaterialData> data, CommandBuffer commandBuffer)
        {
            if (data.IsEmpty || _bufferManager == null || _stagingRing == null || _context == null)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _materialBuffer,
                data).ByteCount;
        }

        private ulong UploadMaterialExtensionSpan(ReadOnlySpan<GPUMaterialExtensionData> data, CommandBuffer commandBuffer)
        {
            if (data.IsEmpty || _bufferManager == null || _stagingRing == null || _context == null)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                _materialExtensionBuffer,
                data).ByteCount;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private void RecordMaterialReadBarrier(CommandBuffer commandBuffer)
        {
            if (_context == null || _bufferManager == null || !_materialBuffer.IsValid)
                return;

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.MeshShaderBitExt |
                               PipelineStageFlags2.FragmentShaderBit |
                               PipelineStageFlags2.ComputeShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_materialBuffer),
                Offset = 0,
                Size = Vk.WholeSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void RecordMaterialExtensionReadBarrier(CommandBuffer commandBuffer)
        {
            if (_context == null || _bufferManager == null || !_materialExtensionBuffer.IsValid)
                return;

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_materialExtensionBuffer),
                Offset = 0,
                Size = Vk.WholeSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private void UpdateRegisteredBindlessBuffer()
        {
            PublishRegisteredBindlessBuffers(
                _materialBuffer,
                _materialExtensionBuffer,
                restoringAuthoritativeBinding: true);
        }

        private void PublishRegisteredBindlessBuffers(
            BufferHandle materialBuffer,
            BufferHandle extensionBuffer,
            bool restoringAuthoritativeBinding)
        {
            if (_registeredBindlessHeap == null ||
                _bufferManager == null ||
                !materialBuffer.IsValid ||
                !extensionBuffer.IsValid)
            {
                return;
            }

            BufferBindingPublicationFaultInjector?.Invoke(
                restoringAuthoritativeBinding
                    ? MaterialBufferBindingPublicationStage
                        .BeforeAuthoritativeRestore
                    : MaterialBufferBindingPublicationStage
                        .BeforeCandidatePublication);

            VkBuffer buffer =
                _bufferManager.GetBuffer(materialBuffer);
            _registeredBindlessHeap.RegisterStorageBuffer(BindlessIndex.MaterialDataBuffer, buffer, 0, Vk.WholeSize);
            BufferBindingPublicationFaultInjector?.Invoke(
                restoringAuthoritativeBinding
                    ? MaterialBufferBindingPublicationStage
                        .AfterAuthoritativeMaterialBinding
                    : MaterialBufferBindingPublicationStage
                        .AfterCandidateMaterialBinding);

            VkBuffer extension =
                _bufferManager.GetBuffer(extensionBuffer);
            _registeredBindlessHeap.RegisterStorageBuffer(BindlessIndex.MaterialExtensionDataBuffer, extension, 0, Vk.WholeSize);
            BufferBindingPublicationFaultInjector?.Invoke(
                restoringAuthoritativeBinding
                    ? MaterialBufferBindingPublicationStage
                        .AfterAuthoritativeExtensionBinding
                    : MaterialBufferBindingPublicationStage
                        .AfterCandidateExtensionBinding);
        }

        private void RepairMaterialBindingsLocked()
        {
            if (!_materialBindingRepairRequired)
                return;

            try
            {
                PublishRegisteredBindlessBuffers(
                    _materialBuffer,
                    _materialExtensionBuffer,
                    restoringAuthoritativeBinding: true);
            }
            catch (Exception repairFailure)
            {
                _lastMaterialBindingPublicationFailure =
                    repairFailure;
                throw new InvalidOperationException(
                    "Authoritative material buffer descriptors could not be repaired.",
                    repairFailure);
            }

            _materialBindingRepairRequired = false;
            _lastMaterialBindingPublicationFailure = null;
            if (_quarantinedMaterialBuffers.Count > 0)
            {
                _retiredMaterialBuffers.AddRange(
                    _quarantinedMaterialBuffers);
                _quarantinedMaterialBuffers.Clear();
            }

            DrainRetiredMaterialBuffersBestEffortLocked();
        }

        private void DrainRetiredMaterialBuffersBestEffortLocked()
        {
            List<Exception>? failures = null;
            DurableResourceDestruction.TryDestroyAll(
                _retiredMaterialBuffers,
                static handle => handle.IsValid,
                handle => _bufferManager!.DestroyBuffer(
                    handle),
                ref failures);
            if (failures == null)
            {
                _lastRetiredBufferCleanupFailure = null;
                return;
            }

            foreach (Exception failure in failures)
                RecordRetiredBufferCleanupFailureLocked(failure);
        }

        private Exception? DrainRetiredMaterialBuffersLocked()
        {
            List<Exception>? failures = null;
            DurableResourceDestruction.TryDestroyAll(
                _retiredMaterialBuffers,
                static handle => handle.IsValid,
                handle => _bufferManager!.DestroyBuffer(
                    handle),
                ref failures);
            if (failures == null)
            {
                _lastRetiredBufferCleanupFailure = null;
                return null;
            }

            foreach (Exception failure in failures)
                RecordRetiredBufferCleanupFailureLocked(failure);
            return new AggregateException(
                "One or more retired material buffers remain pending.",
                failures);
        }

        private void RecordRetiredBufferCleanupFailureLocked(
            Exception cleanupFailure)
        {
            _retiredBufferCleanupFailureCount =
                _retiredBufferCleanupFailureCount ==
                    long.MaxValue
                    ? long.MaxValue
                    : _retiredBufferCleanupFailureCount +
                      1;
            _lastRetiredBufferCleanupFailure =
                cleanupFailure;
        }

        private GPUMaterialData[] GetMaterialDataSnapshotLocked()
        {
            var snapshot = new GPUMaterialData[_materials.Count];
            for (int i = 0; i < _materials.Count; i++)
                snapshot[i] = _materials[i].Active ? _materials[i].Data : CreateDefaultMaterial();

            return snapshot;
        }

        private GPUMaterialExtensionData[] GetMaterialExtensionDataSnapshotLocked()
        {
            return _materialExtensions.Count == 0
                ? Array.Empty<GPUMaterialExtensionData>()
                : _materialExtensions.ToArray();
        }

        private uint AllocateGeneration(int index)
        {
            if (index == _materials.Count)
                return 1;

            return NextGeneration(_materials[index].Generation);
        }

        private static uint NextGeneration(uint generation)
        {
            generation++;
            return generation == 0 ? 1 : generation;
        }

        private static TextureHandle[] CopyTextureHandles(IReadOnlyList<TextureHandle>? textureHandles)
        {
            if (textureHandles == null || textureHandles.Count == 0)
                return Array.Empty<TextureHandle>();

            var copy = new TextureHandle[textureHandles.Count];
            for (int i = 0; i < textureHandles.Count; i++)
                copy[i] = textureHandles[i];

            return copy;
        }

        private int AllocateMaterialExtensionDataLocked(
            GPUMaterialExtensionData extensionData)
        {
            if (_freeMaterialExtensionIndices.Count == 0)
            {
                int appendedIndex = _materialExtensions.Count;
                _materialExtensions.Add(extensionData);
                return appendedIndex;
            }

            int reusedIndex = _freeMaterialExtensionIndices.Pop();
            if ((uint)reusedIndex >= (uint)_materialExtensions.Count)
            {
                throw new InvalidOperationException(
                    $"The free material-extension slot {reusedIndex} is outside the " +
                    $"{_materialExtensions.Count}-entry extension table.");
            }

            _materialExtensions[reusedIndex] = extensionData;
            return reusedIndex;
        }

        private void PrepareMaterialExtensionRetirementLocked(int extensionIndex)
        {
            if (extensionIndex < 0)
                return;
            if ((uint)extensionIndex >= (uint)_materialExtensions.Count)
            {
                throw new InvalidOperationException(
                    $"Material extension index {extensionIndex} is outside the " +
                    $"{_materialExtensions.Count}-entry extension table.");
            }
            if (_freeMaterialExtensionIndices.Contains(extensionIndex))
            {
                throw new InvalidOperationException(
                    $"Material extension index {extensionIndex} was already retired.");
            }

            // Reserve the stack storage before material publication. The
            // corresponding Push can then complete without allocating after
            // the new material payload has become authoritative.
            _freeMaterialExtensionIndices.EnsureCapacity(
                checked(_freeMaterialExtensionIndices.Count + 1));
        }

        private void ReleaseMaterialExtensionDataLocked(int extensionIndex)
        {
            if (extensionIndex < 0)
                return;

            _materialExtensions[extensionIndex] = default;
            _freeMaterialExtensionIndices.Push(extensionIndex);
        }

        private static void ValidateCompiledMaterial(CompiledMaterialTransport compiled)
        {
            ArgumentNullException.ThrowIfNull(compiled);
            ValidateMaterialTextureIndices(compiled.GpuMaterial);
            if (compiled.ExtensionData.HasValue)
                ValidateMaterialExtensionTextureIndices(compiled.ExtensionData.Value);
        }

        internal static TextureOwnershipDelta ComputeTextureOwnershipDelta(
            IReadOnlyList<TextureHandle> previous,
            IReadOnlyList<TextureHandle> next,
            int logicalReferenceCount)
        {
            ArgumentNullException.ThrowIfNull(previous);
            ArgumentNullException.ThrowIfNull(next);
            if (logicalReferenceCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(logicalReferenceCount),
                    "An active material must have at least one logical reference.");
            }

            var occurrenceDelta = new Dictionary<TextureHandle, long>();
            AddTextureOccurrenceDelta(previous, -1, occurrenceDelta);
            AddTextureOccurrenceDelta(next, 1, occurrenceDelta);

            var retains = new List<TextureReferenceAdjustment>();
            var releases = new List<TextureReferenceAdjustment>();
            foreach ((TextureHandle texture, long occurrences) in occurrenceDelta)
            {
                if (occurrences == 0)
                    continue;

                int referenceDelta = checked((int)(Math.Abs(occurrences) * logicalReferenceCount));
                var adjustment = new TextureReferenceAdjustment(texture, referenceDelta);
                if (occurrences > 0)
                    retains.Add(adjustment);
                else
                    releases.Add(adjustment);
            }

            return new TextureOwnershipDelta(retains.ToArray(), releases.ToArray());
        }

        private static void AddTextureOccurrenceDelta(
            IReadOnlyList<TextureHandle> handles,
            long direction,
            Dictionary<TextureHandle, long> occurrenceDelta)
        {
            foreach (TextureHandle texture in handles)
            {
                if (!texture.IsValid)
                    continue;

                occurrenceDelta.TryGetValue(texture, out long current);
                occurrenceDelta[texture] = checked(current + direction);
            }
        }

        private void RetainTextureReferencesTransactional(
            IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            if (_textureReferences == null || adjustments.Count == 0)
                return;

            PrepareRetainedTextureRollbackLocked(adjustments);
            int totalRetains = 0;
            foreach (TextureReferenceAdjustment adjustment in adjustments)
                totalRetains = checked(totalRetains + adjustment.Count);
            var retained = new TextureHandle[totalRetains];
            int retainedCount = 0;
            try
            {
                foreach (TextureReferenceAdjustment adjustment in adjustments)
                {
                    for (int i = 0; i < adjustment.Count; i++)
                    {
                        _textureReferences.RetainTexture(adjustment.Handle);
                        retained[retainedCount++] = adjustment.Handle;
                    }
                }
            }
            catch (Exception retainFailure)
            {
                Exception? rollbackFailure = null;
                for (int i = retainedCount - 1; i >= 0; i--)
                {
                    try
                    {
                        _textureReferences.ReleaseTexture(retained[i]);
                    }
                    catch (Exception exception)
                    {
                        _retiredTextureReleases.Add(
                            new PendingTextureRelease(
                                retained[i],
                                default,
                                1));
                        rollbackFailure ??= exception;
                    }
                }

                if (rollbackFailure != null)
                {
                    throw new AggregateException(
                        "Texture ownership acquisition and its rollback both failed.",
                        retainFailure,
                        rollbackFailure);
                }
                throw;
            }
        }

        private Exception?
            ReleaseRetainedTextureReferencesOrRetireLocked(
                IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            if (_textureReferences == null ||
                adjustments.Count == 0)
            {
                return null;
            }

            List<Exception>? failures = null;
            foreach (TextureReferenceAdjustment adjustment in
                     adjustments)
            {
                int failedReleases = 0;
                for (int occurrence = 0;
                     occurrence < adjustment.Count;
                     occurrence++)
                {
                    try
                    {
                        _textureReferences.ReleaseTexture(
                            adjustment.Handle);
                    }
                    catch (Exception releaseFailure)
                    {
                        failedReleases++;
                        (failures ??= new List<Exception>())
                            .Add(releaseFailure);
                    }
                }

                if (failedReleases > 0)
                {
                    _retiredTextureReleases.Add(
                        new PendingTextureRelease(
                            adjustment.Handle,
                            default,
                            failedReleases));
                }
            }

            return failures switch
            {
                null => null,
                { Count: 1 } => failures[0],
                _ => new AggregateException(
                    "One or more retained texture references could not be rolled back immediately.",
                    failures)
            };
        }

        private void PreparePendingTextureReleasesLocked(
            IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            if (_textureReferences == null ||
                adjustments.Count == 0)
            {
                return;
            }

            _pendingTextureReleases.EnsureCapacity(
                checked(
                    _pendingTextureReleases.Count +
                    adjustments.Count));
            foreach (TextureReferenceAdjustment adjustment in
                     adjustments)
            {
                if (adjustment.Count <= 0)
                {
                    throw new InvalidOperationException(
                        "A pending texture release must have a positive occurrence count.");
                }
                _pendingTextureReleases.TryGetValue(
                    adjustment.Handle,
                    out int pending);
                _ = checked(pending + adjustment.Count);
            }
        }

        private void QueuePendingTextureReleasesLocked(
            IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            foreach (TextureReferenceAdjustment adjustment in adjustments)
            {
                _pendingTextureReleases.TryGetValue(adjustment.Handle, out int pending);
                _pendingTextureReleases[adjustment.Handle] = checked(pending + adjustment.Count);
            }
        }

        private void PrepareRetiredTextureReleasesLocked(
            IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            if (_textureReferences == null ||
                adjustments.Count == 0)
            {
                return;
            }

            int additional = 0;
            foreach (TextureReferenceAdjustment adjustment in
                     adjustments)
            {
                if (adjustment.Count <= 0)
                {
                    throw new InvalidOperationException(
                        "A retired texture release must have a positive occurrence count.");
                }
                additional++;
            }
            _retiredTextureReleases.EnsureCapacity(
                checked(
                    _retiredTextureReleases.Count +
                    additional));
        }

        private void PrepareRetainedTextureRollbackLocked(
            IReadOnlyList<TextureReferenceAdjustment> adjustments)
        {
            if (_textureReferences == null ||
                adjustments.Count == 0)
            {
                return;
            }

            int additional = 0;
            foreach (TextureReferenceAdjustment adjustment in
                     adjustments)
            {
                if (adjustment.Count <= 0)
                {
                    throw new InvalidOperationException(
                        "A retained texture rollback must have a positive occurrence count.");
                }
                additional = checked(
                    additional +
                    adjustment.Count);
            }
            _retiredTextureReleases.EnsureCapacity(
                checked(
                    _retiredTextureReleases.Count +
                    additional));
        }

        private void QueueRetiredTextureReleasesLocked(
            IReadOnlyList<TextureReferenceAdjustment> adjustments,
            Fence retireFence = default)
        {
            if (_textureReferences == null)
                return;

            foreach (TextureReferenceAdjustment adjustment in adjustments)
            {
                _retiredTextureReleases.Add(
                    new PendingTextureRelease(
                        adjustment.Handle,
                        retireFence,
                        adjustment.Count));
            }
        }

        private void MovePendingTextureReleasesToRetiredLocked(
            Fence retireFence)
        {
            if (_pendingTextureReleases.Count == 0)
                return;

            _retiredTextureReleases.EnsureCapacity(
                checked(
                    _retiredTextureReleases.Count +
                    _pendingTextureReleases.Count));
            foreach ((TextureHandle handle, int count) in
                     _pendingTextureReleases)
            {
                if (count <= 0)
                {
                    throw new InvalidOperationException(
                        "A deferred texture release must have a positive occurrence count.");
                }
                _retiredTextureReleases.Add(
                    new PendingTextureRelease(
                        handle,
                        retireFence,
                        count));
            }
            _pendingTextureReleases.Clear();
        }

        private MaterialCompilationContext CreateCompilationContext(
            uint profileRevision,
            float alphaCutoff,
            GiMaterialTransportProfile? primitiveProfile = null)
        {
            return new MaterialCompilationContext
            {
                ProfileRevision = profileRevision,
                PrimitiveProfile = primitiveProfile,
                ResolveTexture = (binding, semantic) =>
                    ResolveTextureForCompilation(binding, semantic, alphaCutoff)
            };
        }

        private MaterialTextureTransportInput ResolveTextureForCompilation(
            MaterialTextureBinding binding,
            MaterialTextureSemantic semantic,
            float alphaCutoff)
        {
            if (_textureResolverOverride != null)
                return _textureResolverOverride(binding, semantic, alphaCutoff);
            if (_textureManager == null)
                throw new InvalidOperationException("A texture-backed material requires a TextureManager.");

            int bindlessIndex = _textureManager.GetBindlessTextureIndex(binding.Texture);
            Vector4 mean = default;
            bool statisticsAvailable =
                _textureManager.TryGetTextureTransportStatistics(
                    binding.Texture,
                    out TextureTransportStatistics statistics);
            bool meanValid = statisticsAvailable && statistics.TryGetLinearMean(out mean);
            if (!meanValid)
                mean = Vector4.One;
            bool alphaCoverageValid =
                statisticsAvailable &&
                statistics.Status == TextureTransportStatisticsStatus.Valid &&
                statistics.Validity.HasFlag(TextureTransportStatisticsValidity.AlphaHistogram);
            float alphaCoverage = alphaCoverageValid
                ? (float)statistics.GetAlphaCoverage(alphaCutoff)
                : 0f;
            bool normalVarianceValid =
                semantic != MaterialTextureSemantic.Normal ||
                statisticsAvailable &&
                statistics.Validity.HasFlag(TextureTransportStatisticsValidity.NormalVariance);
            return new MaterialTextureTransportInput(
                bindlessIndex,
                meanValid,
                mean,
                alphaCoverageValid,
                alphaCoverage,
                normalVarianceValid,
                normalVarianceValid && statisticsAvailable
                    ? (float)statistics.NormalVariance
                    : 0f,
                statisticsAvailable ? statistics.SourceContentHash : 0);
        }

        private void PreflightTextureFanoutPublicationLocked(
            IReadOnlyList<TextureFanoutCompilation> fanout)
        {
            int extensionAppends = 0;
            int extensionRetirements = 0;
            var retiredExtensionIndices = new HashSet<int>();
            foreach (TextureFanoutCompilation item in fanout)
            {
                MaterialSlot slot =
                    GetValidatedSlotLocked(item.Fanout.Handle);
                EnsurePrimitiveProfileAdmissionLocked(
                    slot.PrimitiveProfileInput,
                    item.SelectedPrimitiveProfile);

                int extensionIndex =
                    slot.Data.ExtensionDataIndex;
                if (item.Compiled.ExtensionData.HasValue)
                {
                    if (extensionIndex < 0 ||
                        extensionIndex >=
                            _materialExtensions.Count)
                    {
                        extensionAppends++;
                    }
                }
                else if (extensionIndex >= 0)
                {
                    if (!retiredExtensionIndices.Add(
                            extensionIndex))
                    {
                        throw new InvalidOperationException(
                            $"Material extension slot {extensionIndex} is shared by multiple fan-out dependents.");
                    }
                    extensionRetirements++;
                }
            }

            _materialExtensions.EnsureCapacity(
                checked(
                    _materialExtensions.Count +
                    extensionAppends));
            _freeMaterialExtensionIndices.EnsureCapacity(
                checked(
                    _freeMaterialExtensionIndices.Count +
                    extensionRetirements));
        }

        private MaterialChangedEvent[]
            PublishTextureFanoutLocked(
                IReadOnlyList<TextureFanoutCompilation> fanout)
        {
            var changes =
                new MaterialChangedEvent[fanout.Count];
            uint textureRevision =
                NextTextureContentRevisionLocked();
            MaterialChangeMask combinedMask =
                MaterialChangeMask.None;

            for (int index = 0;
                 index < fanout.Count;
                 index++)
            {
                TextureFanoutCompilation item =
                    fanout[index];
                MaterialHandle handle =
                    item.Fanout.Handle;
                MaterialSlot slot =
                    GetValidatedSlotLocked(handle);

                RemoveDeduplicationLocked(handle, slot);
                RemoveActiveProfileClassificationLocked(slot);
                int retiredExtensionIndex =
                    ApplyCompiledPayloadLocked(
                        ref slot,
                        item.Compiled,
                        item.TextureDependencies,
                        item.Diagnostics);
                ApplyTransportInterpretation(
                    ref slot.Data,
                    slot.SupportsTransportV2,
                    _transportV2Enabled);
                slot.PrimitiveProfileInput =
                    item.SelectedPrimitiveProfile;
                slot.ContentRevision =
                    NextMaterialContentRevisionLocked();
                slot.Data.MaterialRevision =
                    slot.ContentRevision;
                slot.Data.TextureContentRevision =
                    textureRevision;
                slot.AspectRevisions =
                    AdvanceAspectRevisions(
                        slot.AspectRevisions,
                        item.Mask,
                        slot.ContentRevision);
                slot.RegistrationKey = default;
                _materials[handle.Index] = slot;
                AddActiveProfileClassificationLocked(slot);
                ReleaseMaterialExtensionDataLocked(
                    retiredExtensionIndex);
                RecordCompileDiagnosticsLocked(
                    item.Compiled,
                    item.CompileMicroseconds);
                combinedMask |= item.Mask;
                changes[index] = new MaterialChangedEvent(
                    handle,
                    item.Mask,
                    slot.AspectRevisions);
            }

            MarkMaterialDataDirtyLocked();
            AdvanceSsgiInputRevisionLocked(combinedMask);
            return changes;
        }

        private static MaterialChangeMask ClassifyTextureDependencyChange(
            MaterialDefinition definition,
            TextureHandle texture)
        {
            MaterialChangeMask mask = MaterialChangeMask.RasterAppearance |
                                      MaterialChangeMask.TextureDependencies;
            if (definition.BaseColor.Texture == texture ||
                definition.MetallicRoughness.Texture == texture ||
                definition.Occlusion.Texture == texture ||
                definition.Normal.Texture == texture)
            {
                mask |= MaterialChangeMask.DiffuseTransport | MaterialChangeMask.FarField;
            }
            if (definition.BaseColor.Texture == texture)
            {
                mask |= MaterialChangeMask.AlphaCoverage |
                        MaterialChangeMask.AccelerationStructure |
                        MaterialChangeMask.FarField;
            }
            if (definition.Emissive.Texture == texture)
                mask |= MaterialChangeMask.Emission | MaterialChangeMask.FarField;
            if (mask == (MaterialChangeMask.RasterAppearance | MaterialChangeMask.TextureDependencies))
                mask |= MaterialChangeMask.DiffuseTransport | MaterialChangeMask.FarField;
            return mask;
        }

        /// <summary>
        /// Removes only the baked primitive channels whose authored or texture
        /// inputs changed. Remaining channels can still provide exact compact
        /// data, while the compiler derives invalidated channels from current
        /// factors/statistics and reports the resulting lower overall quality.
        /// </summary>
        private static GiMaterialTransportProfile? InvalidatePrimitiveProfile(
            GiMaterialTransportProfile? profile,
            MaterialChangeMask changeMask)
        {
            if (profile is null ||
                profile.Quality != GiTransportProfileQuality.PrimitiveSurfaceSampling)
            {
                return null;
            }

            GiMaterialTransportFlags flags = profile.Flags;
            if ((changeMask & (MaterialChangeMask.DiffuseTransport |
                               MaterialChangeMask.ShadingModel)) != 0)
            {
                flags &= ~(GiMaterialTransportFlags.DiffuseProfileValid |
                           GiMaterialTransportFlags.BaseStatisticsValid |
                           GiMaterialTransportFlags.NormalProfileValid);
            }
            if ((changeMask & (MaterialChangeMask.Emission |
                               MaterialChangeMask.ShadingModel)) != 0)
            {
                flags &= ~GiMaterialTransportFlags.EmissionProfileValid;
            }
            if ((changeMask & MaterialChangeMask.AlphaCoverage) != 0)
                flags &= ~GiMaterialTransportFlags.AlphaProfileValid;

            const GiMaterialTransportFlags channelValidity =
                GiMaterialTransportFlags.BaseStatisticsValid |
                GiMaterialTransportFlags.DiffuseProfileValid |
                GiMaterialTransportFlags.EmissionProfileValid |
                GiMaterialTransportFlags.AlphaProfileValid |
                GiMaterialTransportFlags.NormalProfileValid;
            return (flags & channelValidity) == 0
                ? null
                : profile with { Flags = flags };
        }

        private void OnTextureContentChanged(TextureContentChangedEvent changed)
        {
            if (_disposed)
                return;
            try
            {
                ProcessTextureContentChanged(changed);
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Disposal may win after the event publisher captured this
                // subscriber. The manager no longer owns dependent state, so
                // the retired callback is complete rather than retryable.
            }
        }

        internal void ProcessTextureContentChanged(
            TextureContentChangedEvent changed)
        {
            try
            {
                NotifyTextureContentChanged(changed.Handle);
            }
            catch (Exception fanoutFailure)
            {
                lock (_lock)
                {
                    if (_disposed)
                        throw;
                    _pendingTextureFanoutRetries.Add(
                        changed.Handle);
                    _textureFanoutFailureCount =
                        _textureFanoutFailureCount ==
                            long.MaxValue
                            ? long.MaxValue
                            : _textureFanoutFailureCount + 1;
                    _lastTextureFanoutFailure =
                        fanoutFailure;
                }

                throw;
            }

            lock (_lock)
            {
                ThrowIfDisposedLocked();
                _pendingTextureFanoutRetries.Remove(
                    changed.Handle);
                if (_pendingTextureFanoutRetries.Count == 0)
                    _lastTextureFanoutFailure = null;
            }
        }

        private int ApplyCompiledPayloadLocked(
            ref MaterialSlot slot,
            CompiledMaterialTransport compiled,
            TextureHandle[]? preparedTextureHandles = null,
            string[]? preparedDiagnostics = null)
        {
            uint textureContentRevision = slot.Data.TextureContentRevision;
            int previousExtensionIndex = slot.Data.ExtensionDataIndex;
            int retiredExtensionIndex = -1;
            GPUMaterialData data = compiled.GpuMaterial;
            // This revision describes runtime texture publication, not
            // compiler output. Preserve it through authored recompiles; the
            // texture-change path advances it after this payload is applied.
            data.TextureContentRevision = textureContentRevision;
            if (compiled.ExtensionData.HasValue)
            {
                if (slot.Data.ExtensionDataIndex >= 0 &&
                    slot.Data.ExtensionDataIndex < _materialExtensions.Count)
                {
                    _materialExtensions[slot.Data.ExtensionDataIndex] = compiled.ExtensionData.Value;
                    data.ExtensionDataIndex = slot.Data.ExtensionDataIndex;
                }
                else
                {
                    data.ExtensionDataIndex =
                        AllocateMaterialExtensionDataLocked(compiled.ExtensionData.Value);
                }
            }
            else
            {
                data.ExtensionDataIndex = -1;
                retiredExtensionIndex = previousExtensionIndex;
                PrepareMaterialExtensionRetirementLocked(retiredExtensionIndex);
            }

            ValidateMaterialTextureIndices(data);
            if (compiled.ExtensionData.HasValue)
                ValidateMaterialExtensionTextureIndices(compiled.ExtensionData.Value);
            slot.Data = data;
            slot.Definition = compiled.Definition;
            slot.TransportProfile = compiled.TransportProfile;
            slot.CompileDiagnostics =
                preparedDiagnostics ??
                compiled.Diagnostics.ToArray();
            slot.TextureHandles =
                preparedTextureHandles ??
                CopyTextureHandles(
                    compiled.TextureDependencies);
            slot.Metadata = compiled.Metadata;
            return retiredExtensionIndex;
        }

        private uint GetMaximumActiveTransportProfileRevisionLocked()
        {
            uint maximum = 0;
            foreach (MaterialSlot slot in _materials)
            {
                if (slot.Active)
                    maximum = Math.Max(maximum, slot.Data.TransportProfileRevision);
            }
            return maximum;
        }

        private static MaterialAspectRevisions AdvanceAspectRevisions(
            MaterialAspectRevisions current,
            MaterialChangeMask mask,
            uint revision)
        {
            return new MaterialAspectRevisions(
                revision,
                mask.HasFlag(MaterialChangeMask.DiffuseTransport) ? revision : current.DiffuseTransport,
                mask.HasFlag(MaterialChangeMask.Emission) ? revision : current.Emission,
                mask.HasFlag(MaterialChangeMask.AlphaCoverage) ? revision : current.AlphaCoverage,
                mask.HasFlag(MaterialChangeMask.Sidedness) ? revision : current.Sidedness,
                mask.HasFlag(MaterialChangeMask.ShadingModel) ? revision : current.ShadingModel,
                mask.HasFlag(MaterialChangeMask.FarField) ? revision : current.FarField);
        }

        private static uint NextNonZero(uint value)
        {
            value++;
            return value == 0 ? 1 : value;
        }

        private GPUMaterialExtensionData? GetExtensionDataLocked(GPUMaterialData data)
        {
            return data.ExtensionDataIndex >= 0 && data.ExtensionDataIndex < _materialExtensions.Count
                ? _materialExtensions[data.ExtensionDataIndex]
                : null;
        }

        private void RemoveDeduplicationLocked(MaterialHandle handle, MaterialSlot slot)
        {
            if (_deduplicatedMaterials.TryGetValue(slot.RegistrationKey, out MaterialHandle mapped) &&
                mapped == handle)
            {
                _deduplicatedMaterials.Remove(slot.RegistrationKey);
            }
        }

        private void AddTextureDependenciesLocked(int materialIndex, IReadOnlyList<TextureHandle> handles)
        {
            foreach (TextureHandle texture in handles)
            {
                if (!texture.IsValid)
                    continue;
                if (!_textureDependents.TryGetValue(texture, out HashSet<int>? dependents))
                {
                    dependents = new HashSet<int>();
                    _textureDependents.Add(texture, dependents);
                }
                dependents.Add(materialIndex);
            }
        }

        private void RemoveTextureDependenciesLocked(int materialIndex, IReadOnlyList<TextureHandle> handles)
        {
            foreach (TextureHandle texture in handles)
            {
                if (!_textureDependents.TryGetValue(texture, out HashSet<int>? dependents))
                    continue;
                dependents.Remove(materialIndex);
                if (dependents.Count == 0)
                    _textureDependents.Remove(texture);
            }
        }

        private void RecordCompileDiagnosticsLocked(
            CompiledMaterialTransport compiled,
            long compileMicroseconds)
        {
            _materialCompileCount++;
            _lastCompileMicroseconds = compileMicroseconds;
            _totalCompileMicroseconds += compileMicroseconds;
            _compileLatencies.Add(compileMicroseconds);
            if (compiled.TransportProfile.Quality == GiTransportProfileQuality.Invalid)
                _invalidStatisticsCompileCount++;
        }

        private void AddActiveProfileClassificationLocked(MaterialSlot slot)
        {
            if (!slot.Active)
                return;
            if (IsPrimitiveProfileInput(slot.PrimitiveProfileInput))
                _activePrimitiveProfileCount++;
            if ((((GiMaterialTransportFlags)slot.Data.TransportFlags) &
                 GiMaterialTransportFlags.LegacyV1Fallback) != 0)
            {
                _activeLegacyV1FallbackCount++;
            }
            if (IsActiveProfileInvalid(slot))
                _activeInvalidProfileCount++;
        }

        private void RemoveActiveProfileClassificationLocked(MaterialSlot slot)
        {
            if (!slot.Active)
                return;
            if (IsPrimitiveProfileInput(slot.PrimitiveProfileInput))
            {
                if (_activePrimitiveProfileCount <= 0)
                    throw new InvalidOperationException("Active primitive-profile gauge underflow.");
                _activePrimitiveProfileCount--;
            }
            if ((((GiMaterialTransportFlags)slot.Data.TransportFlags) &
                 GiMaterialTransportFlags.LegacyV1Fallback) != 0)
            {
                if (_activeLegacyV1FallbackCount <= 0)
                    throw new InvalidOperationException("Active V1 fallback gauge underflow.");
                _activeLegacyV1FallbackCount--;
            }
            if (IsActiveProfileInvalid(slot))
            {
                if (_activeInvalidProfileCount <= 0)
                    throw new InvalidOperationException("Active invalid-profile gauge underflow.");
                _activeInvalidProfileCount--;
            }
        }

        private static bool IsActiveProfileInvalid(MaterialSlot slot) =>
            slot.TransportProfile.Quality == GiTransportProfileQuality.Invalid ||
            ((((GiMaterialTransportFlags)slot.Data.TransportFlags) &
              GiMaterialTransportFlags.CompactTextureFallback) != 0);

        private static bool IsPrimitiveProfileInput(GiMaterialTransportProfile? profile) =>
            profile?.Quality == GiTransportProfileQuality.PrimitiveSurfaceSampling;

        private void EnsurePrimitiveProfileAdmissionLocked(
            GiMaterialTransportProfile? previousProfile,
            GiMaterialTransportProfile? nextProfile)
        {
            if (IsPrimitiveProfileInput(previousProfile) ||
                !IsPrimitiveProfileInput(nextProfile))
            {
                return;
            }

            ulong requestedBytes = checked(
                (ulong)(_activePrimitiveProfileCount + 1) * MaterialStride);
            if (!CanAdmitPrimitiveProfile(
                    _activePrimitiveProfileCount,
                    _primitiveProfileGpuBudgetBytes))
            {
                throw new InvalidOperationException(
                    $"Primitive transport profile admission requires {requestedBytes} GPU bytes; " +
                    $"the active quality-tier cap is {_primitiveProfileGpuBudgetBytes} bytes " +
                    $"and the absolute process cap is {MaximumPrimitiveProfileGpuBytes} bytes.");
            }
        }

        internal static bool CanAdmitPrimitiveProfile(int activePrimitiveProfileCount)
        {
            if (activePrimitiveProfileCount < 0)
                throw new ArgumentOutOfRangeException(nameof(activePrimitiveProfileCount));
            return CanAdmitPrimitiveProfile(
                activePrimitiveProfileCount,
                MaximumPrimitiveProfileGpuBytes);
        }

        internal static bool CanAdmitPrimitiveProfile(
            int activePrimitiveProfileCount,
            ulong budgetBytes)
        {
            if (activePrimitiveProfileCount < 0)
                throw new ArgumentOutOfRangeException(nameof(activePrimitiveProfileCount));
            if (budgetBytes == 0 || budgetBytes > MaximumPrimitiveProfileGpuBytes)
                throw new ArgumentOutOfRangeException(nameof(budgetBytes));

            ulong requestedBytes = checked(
                ((ulong)activePrimitiveProfileCount + 1UL) * MaterialStride);
            return requestedBytes <= budgetBytes;
        }

        public static GPUMaterialData CreateDefaultMaterial()
        {
            return new GPUMaterialData
            {
                Albedo = new Vector4(1f, 1f, 1f, 1f),
                Emissive = Vector4.Zero,
                NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f),
                MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
                BaseColorOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                NormalOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                MetallicRoughnessOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                OcclusionOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                EmissiveOffsetScale = new Vector4(0f, 0f, 1f, 1f),
                TextureRotations = Vector4.Zero,
                TextureTexCoordSets = Vector4.Zero,
                OcclusionBinding = Vector4.Zero,
                AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
                NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
                MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
                OcclusionTextureIndex = BindlessIndex.DefaultWhiteTexture,
                // Emissive is a multiplicative texture/factor pair.  A missing texture must
                // therefore contribute white so an authored emissive factor remains visible.
                // The black factor above is what makes the default material non-emissive.
                EmissiveTextureIndex = BindlessIndex.DefaultWhiteTexture,
                FeatureFlags = 0u,
                ExtensionDataIndex = -1,
                TransportFlags = (uint)(
                    GiMaterialTransportFlags.BaseStatisticsValid |
                    GiMaterialTransportFlags.DiffuseProfileValid |
                    GiMaterialTransportFlags.EmissionProfileValid |
                    GiMaterialTransportFlags.AlphaProfileValid |
                    GiMaterialTransportFlags.NormalProfileValid |
                    GiMaterialTransportFlags.ReceivesIndirectDiffuse |
                    GiMaterialTransportFlags.ReflectsIndirectDiffuse |
                    (GiMaterialTransportFlags)((uint)GiTransportProfileQuality.MaterialFactors <<
                        (int)GiMaterialTransportFlags.QualityShift)),
                TransportProfileRevision = 1u,
                PackedMeanMetallicRoughness = 0u,
                TransportProfileQuality = 0u,
                MaterialRevision = 0u,
                DdgiAverageAlbedo = new Vector4(1f, 1f, 1f, 1f),
                DdgiAverageEmissive = Vector4.Zero,
                DdgiMaterialPolicy = new Vector4(0f, 0f, 0f, 0f)
            };
        }

        public static void ValidateMaterialTextureIndices(GPUMaterialData material)
        {
            ValidateTextureIndex(material.AlbedoTextureIndex, nameof(GPUMaterialData.AlbedoTextureIndex));
            ValidateTextureIndex(material.NormalTextureIndex, nameof(GPUMaterialData.NormalTextureIndex));
            ValidateTextureIndex(material.MetallicRoughnessTextureIndex, nameof(GPUMaterialData.MetallicRoughnessTextureIndex));
            ValidateTextureIndex(material.OcclusionTextureIndex, nameof(GPUMaterialData.OcclusionTextureIndex));
            ValidateTextureIndex(material.EmissiveTextureIndex, nameof(GPUMaterialData.EmissiveTextureIndex));
            if (material.ExtensionDataIndex < -1)
                throw new InvalidOperationException($"{nameof(GPUMaterialData.ExtensionDataIndex)} must be -1 or a non-negative extension payload index.");
            if (material.FeatureFlags == 0u && material.ExtensionDataIndex != -1)
                throw new InvalidOperationException("ExtensionDataIndex must be -1 when FeatureFlags is zero.");
        }

        public static void ValidateMaterialExtensionTextureIndices(GPUMaterialExtensionData extensionData)
        {
            ValidateTextureIndex(extensionData.ClearcoatTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatTextureIndex));
            ValidateTextureIndex(extensionData.ClearcoatRoughnessTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatRoughnessTextureIndex));
            ValidateTextureIndex(extensionData.ClearcoatNormalTextureIndex, nameof(GPUMaterialExtensionData.ClearcoatNormalTextureIndex));
            ValidateTextureIndex(extensionData.SheenColorTextureIndex, nameof(GPUMaterialExtensionData.SheenColorTextureIndex));
            ValidateTextureIndex(extensionData.SheenRoughnessTextureIndex, nameof(GPUMaterialExtensionData.SheenRoughnessTextureIndex));
            ValidateTextureIndex(extensionData.AnisotropyTextureIndex, nameof(GPUMaterialExtensionData.AnisotropyTextureIndex));
            ValidateTextureIndex(extensionData.TransmissionTextureIndex, nameof(GPUMaterialExtensionData.TransmissionTextureIndex));
            ValidateTextureIndex(extensionData.ThicknessTextureIndex, nameof(GPUMaterialExtensionData.ThicknessTextureIndex));
            ValidateTextureIndex(extensionData.SubsurfaceTextureIndex, nameof(GPUMaterialExtensionData.SubsurfaceTextureIndex));
            ValidateTextureIndex(extensionData.SpecularTextureIndex, nameof(GPUMaterialExtensionData.SpecularTextureIndex));
            ValidateTextureIndex(extensionData.SpecularColorTextureIndex, nameof(GPUMaterialExtensionData.SpecularColorTextureIndex));
            ValidateTextureIndex(extensionData.IridescenceTextureIndex, nameof(GPUMaterialExtensionData.IridescenceTextureIndex));
            ValidateTextureIndex(extensionData.IridescenceThicknessTextureIndex, nameof(GPUMaterialExtensionData.IridescenceThicknessTextureIndex));
        }

        private static void ValidateTextureIndex(int textureIndex, string fieldName)
        {
            if (!BindlessIndex.IsTextureIndex(textureIndex))
            {
                throw new InvalidOperationException(
                    $"{fieldName} contains invalid bindless texture index {textureIndex}. " +
                    $"Expected a value in [{BindlessIndex.FirstTextureIndex}, {BindlessIndex.MaxTextures - 1}].");
            }
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

            List<Exception>? failures = null;
            lock (_lock)
            {
                if (!_disposePrepared)
                {
                    PreflightMaterialDisposalLocked();
                    _disposed = true;
                    if (_textureManager != null)
                    {
                        _textureManager.TextureContentChanged -=
                            OnTextureContentChanged;
                    }
                    // Rendering has stopped and the device is idle before
                    // manager shutdown. Relinquish this manager's logical
                    // descriptor registration before its backing buffers are
                    // destroyed; the owning BindlessHeap is disposed only
                    // after every resource manager has retired.
                    _registeredBindlessHeap = null;
                    _materialBindingRepairRequired = false;
                    _lastMaterialBindingPublicationFailure =
                        null;
                    // A candidate whose descriptor restoration failed must
                    // outlive the bindless heap. BufferManager remains its
                    // terminal owner and destroys it after BindlessHeap.
                    _quarantinedMaterialBuffers.Clear();

                    MovePendingTextureReleasesToRetiredLocked(
                        default);
                    foreach (MaterialSlot slot in _materials)
                    {
                        if (!slot.Active ||
                            slot.ReferenceCount <= 0)
                        {
                            continue;
                        }

                        QueueMaterialTextureReleasesLocked(
                            slot,
                            default,
                            slot.ReferenceCount);
                    }

                    _materials.Clear();
                    _materialExtensions.Clear();
                    _freeIndices.Clear();
                    _freeMaterialExtensionIndices.Clear();
                    _deduplicatedMaterials.Clear();
                    _textureDependents.Clear();
                    _pendingTextureReleases.Clear();
                    _pendingTextureFanoutRetries.Clear();
                    _activeLegacyV1FallbackCount = 0;
                    _activeInvalidProfileCount = 0;
                    _activePrimitiveProfileCount = 0;
                    _disposePrepared = true;
                }

                if (_materialBuffer.IsValid && _bufferManager != null)
                {
                    Exception? disposeFailure =
                        DurableResourceDestruction.TryDestroy(
                            ref _materialBuffer,
                            BufferHandle.Invalid,
                            static handle => handle.IsValid,
                            _bufferManager.DestroyBuffer);
                    if (disposeFailure != null)
                    {
                        (failures ??= new List<Exception>())
                            .Add(disposeFailure);
                    }
                }
                if (_materialExtensionBuffer.IsValid && _bufferManager != null)
                {
                    Exception? disposeFailure =
                        DurableResourceDestruction.TryDestroy(
                            ref _materialExtensionBuffer,
                            BufferHandle.Invalid,
                            static handle => handle.IsValid,
                            _bufferManager.DestroyBuffer);
                    if (disposeFailure != null)
                    {
                        (failures ??= new List<Exception>())
                            .Add(disposeFailure);
                    }
                }

                if (_bufferManager != null)
                {
                    Exception? retiredBufferFailure =
                        DrainRetiredMaterialBuffersLocked();
                    if (retiredBufferFailure != null)
                    {
                        (failures ??= new List<Exception>())
                            .Add(retiredBufferFailure);
                    }
                }
            }

            // Renderer shutdown waits for device idleness before service
            // disposal. CPU-only tests have no in-flight work, so immediate
            // release is also correct there.
            try
            {
                FlushTextureReleases();
            }
            catch (Exception releaseFailure)
            {
                (failures ??= new List<Exception>())
                    .Add(releaseFailure);
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more material-manager resources could not be disposed.",
                    failures);
            }

            lock (_lock)
            {
                _disposeCompleted =
                    !_materialBuffer.IsValid &&
                    !_materialExtensionBuffer.IsValid &&
                    _retiredTextureReleases.Count == 0 &&
                    _retiredMaterialBuffers.Count == 0;
            }
        }

        private void PreflightMaterialDisposalLocked()
        {
            int additionalReleases =
                _pendingTextureReleases.Count;
            foreach (MaterialSlot slot in _materials)
            {
                if (!slot.Active ||
                    slot.ReferenceCount <= 0)
                {
                    continue;
                }

                foreach (TextureHandle texture in
                         slot.TextureHandles)
                {
                    if (texture.IsValid)
                    {
                        additionalReleases = checked(
                            additionalReleases + 1);
                    }
                }
            }

            _retiredTextureReleases.EnsureCapacity(
                checked(
                    _retiredTextureReleases.Count +
                    additionalReleases));
            DisposalPreflightFaultInjector?.Invoke();
        }

        private readonly record struct MaterialCompilationSnapshot(
            MaterialDefinition Definition,
            GiMaterialTransportProfile? PrimitiveProfileInput,
            uint ContentRevision,
            uint TextureContentRevision,
            uint TransportProfileRevision)
        {
            public static MaterialCompilationSnapshot Capture(MaterialSlot slot) => new(
                slot.Definition,
                slot.PrimitiveProfileInput,
                slot.ContentRevision,
                slot.Data.TextureContentRevision,
                slot.Data.TransportProfileRevision);

            public bool Matches(MaterialSlot slot) =>
                (ReferenceEquals(slot.Definition, Definition) || slot.Definition == Definition) &&
                object.Equals(slot.PrimitiveProfileInput, PrimitiveProfileInput) &&
                slot.ContentRevision == ContentRevision &&
                slot.Data.TextureContentRevision == TextureContentRevision &&
                slot.Data.TransportProfileRevision == TransportProfileRevision;
        }

        private readonly record struct TextureFanoutSnapshot(
            MaterialHandle Handle,
            MaterialCompilationSnapshot Snapshot);

        private readonly record struct TextureFanoutCompilation(
            TextureFanoutSnapshot Fanout,
            MaterialChangeMask Mask,
            GiMaterialTransportProfile? SelectedPrimitiveProfile,
            CompiledMaterialTransport Compiled,
            TextureHandle[] TextureDependencies,
            string[] Diagnostics,
            long CompileMicroseconds);

        private readonly record struct PendingTextureRelease(
            TextureHandle Handle,
            Fence RetireFence,
            int RemainingCount);

        private struct MaterialSlot
        {
            public GPUMaterialData Data;
            public MaterialDefinition Definition;
            public GiMaterialTransportProfile TransportProfile;
            public GiMaterialTransportProfile? PrimitiveProfileInput;
            public bool SupportsTransportV2;
            public MaterialAspectRevisions AspectRevisions;
            public string[] CompileDiagnostics;
            public uint Generation;
            public uint ContentRevision;
            public bool Active;
            public bool Permanent;
            public int ReferenceCount;
            public TextureHandle[] TextureHandles;
            public MaterialRenderMetadata Metadata;
            public MaterialRegistrationKey RegistrationKey;
        }

        private readonly record struct MaterialRegistrationKey(
            GPUMaterialData Material,
            GPUMaterialExtensionData? ExtensionData,
            MaterialRenderMetadata Metadata,
            MaterialDefinition Definition,
            GiMaterialTransportProfile TransportProfile);

        internal readonly record struct TextureReferenceAdjustment(
            TextureHandle Handle,
            int Count);

        internal readonly record struct TextureOwnershipDelta(
            TextureReferenceAdjustment[] Retains,
            TextureReferenceAdjustment[] Releases);

        public sealed class MaterialDataComparer : IEqualityComparer<GPUMaterialData>
        {
            public bool Equals(GPUMaterialData x, GPUMaterialData y)
            {
                return x.Albedo.Equals(y.Albedo) &&
                       x.Emissive.Equals(y.Emissive) &&
                       x.NormalScaleBias.Equals(y.NormalScaleBias) &&
                       x.MetallicRoughnessAO.Equals(y.MetallicRoughnessAO) &&
                       x.BaseColorOffsetScale.Equals(y.BaseColorOffsetScale) &&
                       x.NormalOffsetScale.Equals(y.NormalOffsetScale) &&
                       x.MetallicRoughnessOffsetScale.Equals(y.MetallicRoughnessOffsetScale) &&
                       x.OcclusionOffsetScale.Equals(y.OcclusionOffsetScale) &&
                       x.EmissiveOffsetScale.Equals(y.EmissiveOffsetScale) &&
                       x.TextureRotations.Equals(y.TextureRotations) &&
                       x.TextureTexCoordSets.Equals(y.TextureTexCoordSets) &&
                       x.OcclusionBinding.Equals(y.OcclusionBinding) &&
                       x.AlbedoTextureIndex == y.AlbedoTextureIndex &&
                       x.NormalTextureIndex == y.NormalTextureIndex &&
                       x.MetallicRoughnessTextureIndex == y.MetallicRoughnessTextureIndex &&
                       x.OcclusionTextureIndex == y.OcclusionTextureIndex &&
                       x.EmissiveTextureIndex == y.EmissiveTextureIndex &&
                       x.FeatureFlags == y.FeatureFlags &&
                       x.ExtensionDataIndex == y.ExtensionDataIndex &&
                       x.TransportFlags == y.TransportFlags &&
                       x.TransportProfileRevision == y.TransportProfileRevision &&
                       x.PackedMeanMetallicRoughness == y.PackedMeanMetallicRoughness &&
                       x.TransportProfileQuality == y.TransportProfileQuality &&
                       x.PackedMeanGiDirectionalDiffuseBaseRg ==
                           y.PackedMeanGiDirectionalDiffuseBaseRg &&
                       x.PackedMeanGiDirectionalDiffuseBaseBAndF0R ==
                           y.PackedMeanGiDirectionalDiffuseBaseBAndF0R &&
                       x.PackedMeanGiDielectricF0Gb ==
                           y.PackedMeanGiDielectricF0Gb &&
                       x.DdgiAverageTransmission.Equals(y.DdgiAverageTransmission) &&
                       x.DdgiAverageAlbedo.Equals(y.DdgiAverageAlbedo) &&
                       x.DdgiAverageEmissive.Equals(y.DdgiAverageEmissive) &&
                       x.DdgiMaterialPolicy.Equals(y.DdgiMaterialPolicy);
            }

            public int GetHashCode(GPUMaterialData obj)
            {
                var hash = new HashCode();
                hash.Add(obj.Albedo);
                hash.Add(obj.Emissive);
                hash.Add(obj.NormalScaleBias);
                hash.Add(obj.MetallicRoughnessAO);
                hash.Add(obj.BaseColorOffsetScale);
                hash.Add(obj.NormalOffsetScale);
                hash.Add(obj.MetallicRoughnessOffsetScale);
                hash.Add(obj.OcclusionOffsetScale);
                hash.Add(obj.EmissiveOffsetScale);
                hash.Add(obj.TextureRotations);
                hash.Add(obj.TextureTexCoordSets);
                hash.Add(obj.OcclusionBinding);
                hash.Add(obj.AlbedoTextureIndex);
                hash.Add(obj.NormalTextureIndex);
                hash.Add(obj.MetallicRoughnessTextureIndex);
                hash.Add(obj.OcclusionTextureIndex);
                hash.Add(obj.EmissiveTextureIndex);
                hash.Add(obj.FeatureFlags);
                hash.Add(obj.ExtensionDataIndex);
                hash.Add(obj.TransportFlags);
                hash.Add(obj.TransportProfileRevision);
                hash.Add(obj.PackedMeanMetallicRoughness);
                hash.Add(obj.TransportProfileQuality);
                hash.Add(obj.PackedMeanGiDirectionalDiffuseBaseRg);
                hash.Add(obj.PackedMeanGiDirectionalDiffuseBaseBAndF0R);
                hash.Add(obj.PackedMeanGiDielectricF0Gb);
                hash.Add(obj.DdgiAverageTransmission);
                hash.Add(obj.DdgiAverageAlbedo);
                hash.Add(obj.DdgiAverageEmissive);
                hash.Add(obj.DdgiMaterialPolicy);
                return hash.ToHashCode();
            }
        }

        private sealed class MaterialRegistrationKeyComparer : IEqualityComparer<MaterialRegistrationKey>
        {
            private static readonly MaterialDataComparer MaterialComparer = new MaterialDataComparer();

            public bool Equals(MaterialRegistrationKey x, MaterialRegistrationKey y)
            {
                return MaterialComparer.Equals(x.Material, y.Material) &&
                       Nullable.Equals(x.ExtensionData, y.ExtensionData) &&
                       x.Metadata.Equals(y.Metadata) &&
                       x.Definition.Equals(y.Definition) &&
                       x.TransportProfile.Equals(y.TransportProfile);
            }

            public int GetHashCode(MaterialRegistrationKey obj)
            {
                return HashCode.Combine(
                    MaterialComparer.GetHashCode(obj.Material),
                    obj.ExtensionData,
                    obj.Metadata,
                    obj.Definition,
                    obj.TransportProfile);
            }
        }
    }

    internal enum MaterialRegistrationPublicationStage
    {
        AfterPreflight,
        AfterFreeSlotReservation,
        AfterExtensionPublication,
        AfterSlotPublication,
        AfterDeduplicationPublication,
        AfterDependencyPublication,
        AfterClassificationPublication
    }

    internal enum MaterialBufferBindingPublicationStage
    {
        BeforeCandidatePublication,
        AfterCandidateMaterialBinding,
        AfterCandidateExtensionBinding,
        BeforeAuthoritativeRestore,
        AfterAuthoritativeMaterialBinding,
        AfterAuthoritativeExtensionBinding
    }

    public sealed record MaterialManagerDiagnostics(
        int RegisteredMaterialCount,
        int UploadedMaterialCount,
        int MaterialExtensionDataCount = 0,
        long MaterialCompileCount = 0,
        long LastCompileMicroseconds = 0,
        long TotalCompileMicroseconds = 0,
        long LegacyV1FallbackCount = 0,
        long InvalidStatisticsCompileCount = 0,
        int TrackedTextureDependencyCount = 0,
        long CompileP95Microseconds = 0,
        int CompileTimingSampleCount = 0,
        long UploadP95Microseconds = 0,
        int UploadTimingSampleCount = 0,
        int ActiveLegacyV1FallbackCount = 0,
        int ActiveInvalidProfileCount = 0,
        uint MaterialRevision = 0,
        uint TextureContentRevision = 0,
        uint MaximumTransportProfileRevision = 0,
        int ActivePrimitiveProfileCount = 0,
        ulong PrimitiveProfileGpuBytes = 0,
        ulong PrimitiveProfileGpuBudgetBytes = MaterialManager.MaximumPrimitiveProfileGpuBytes,
        int PendingTextureFanoutCount = 0,
        long TextureFanoutFailureCount = 0,
        int PendingRetiredBufferCount = 0,
        int QuarantinedBufferCount = 0,
        long RetiredBufferCleanupFailureCount = 0,
        bool MaterialBindingRepairPending = false);
}

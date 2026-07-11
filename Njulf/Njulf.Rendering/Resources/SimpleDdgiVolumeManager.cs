using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed class SimpleDdgiVolumeManager : IDisposable
    {
        public const int IrradianceTexelsPerProbe = 8;
        public const int VisibilityTexelsPerProbe = 16;

        private const ulong MinBufferSize = 16;
        private static readonly ulong ParamsSize = (ulong)Marshal.SizeOf<GPUSimpleDdgiParams>();
        private static readonly ulong RayResultStride = (ulong)Marshal.SizeOf<GPUSimpleDdgiRayResult>();
        private static readonly ulong AtlasTexelStride = 8;

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly RenderSettings _settings;

        private BufferHandle _paramsBuffer;
        private BufferHandle _irradianceAtlasBuffer;
        private BufferHandle _visibilityAtlasBuffer;
        private BufferHandle _rayResultScratchBuffer;
        private ulong _irradianceAtlasBytes;
        private ulong _visibilityAtlasBytes;
        private ulong _rayScratchBytes;
        private BindlessHeap? _registeredBindlessHeap;
        private GPUSimpleDdgiParams _lastParams;
        private int _probeCount;
        private int _probeCountX;
        private int _probeCountY;
        private int _probeCountZ;
        private int _raysPerProbe;
        private int _updateStartProbe;
        private int _probesToUpdate;
        private uint _frameIndex;
        private bool _disposed;

        public SimpleDdgiVolumeManager(VulkanContext context, BufferManager bufferManager, RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _paramsBuffer = _bufferManager.CreateDeviceBuffer(
                Math.Max(MinBufferSize, ParamsSize),
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: "Simple DDGI Params");
            EnsureCapacity(0, 1);
        }

        public int ProbeCount => _probeCount;
        public int ProbeCountX => _probeCountX;
        public int ProbeCountY => _probeCountY;
        public int ProbeCountZ => _probeCountZ;
        public int RaysPerProbe => _raysPerProbe;
        public int UpdateStartProbe => _updateStartProbe;
        public int ProbesToUpdate => _probesToUpdate;
        public ulong BufferBytes => ParamsSize + _irradianceAtlasBytes + _visibilityAtlasBytes + _rayScratchBytes;
        public GPUSimpleDdgiParams LastParams => _lastParams;

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _registeredBindlessHeap = bindlessHeap;
            bindlessHeap.RegisterStorageBuffer(BindlessIndex.SimpleDdgiParamsBuffer, _bufferManager.GetBuffer(_paramsBuffer), 0, Math.Max(MinBufferSize, ParamsSize));
            RegisterIfValid(BindlessIndex.SimpleDdgiIrradianceAtlasBuffer, _irradianceAtlasBuffer, _irradianceAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiVisibilityAtlasBuffer, _visibilityAtlasBuffer, _visibilityAtlasBytes);
            RegisterIfValid(BindlessIndex.SimpleDdgiRayResultScratchBuffer, _rayResultScratchBuffer, _rayScratchBytes);
        }

        public void Upload(Scene scene, StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required.", nameof(commandBuffer));

            GlobalIlluminationSettings gi = _settings.GlobalIllumination;
            if (!gi.EffectiveUseSimpleDdgi)
            {
                _probeCount = 0;
                _probesToUpdate = 0;
                return;
            }

            BoundingBox bounds = ExpandBounds(DdgiFrameLayoutBuilder.EstimateSceneProbeBounds(scene), gi.SimpleDdgiProbeSpacing * 1.5f);
            Vector3 size = bounds.Max - bounds.Min;
            float spacing = gi.SimpleDdgiProbeSpacing;
            _probeCountX = Math.Clamp((int)MathF.Ceiling(size.X / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
            _probeCountY = Math.Clamp((int)MathF.Ceiling(size.Y / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
            _probeCountZ = Math.Clamp((int)MathF.Ceiling(size.Z / spacing) + 1, 2, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
            _probeCount = checked(_probeCountX * _probeCountY * _probeCountZ);
            _raysPerProbe = Math.Clamp(gi.SimpleDdgiRaysPerProbe, 1, GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);

            int updateBudget = gi.SimpleDdgiProbeUpdatesPerFrame <= 0
                ? _probeCount
                : Math.Min(_probeCount, gi.SimpleDdgiProbeUpdatesPerFrame);
            _updateStartProbe = updateBudget >= _probeCount ? 0 : (int)(_frameIndex % (uint)Math.Max(1, _probeCount));
            _probesToUpdate = updateBudget;

            EnsureCapacity(_probeCount, _raysPerProbe);

            float environmentIntensity = _settings.Environment.Enabled ? _settings.Environment.DiffuseIntensity : 0.0f;
            _lastParams = new GPUSimpleDdgiParams
            {
                GridOriginAndSpacing = new Vector4(bounds.Min.X, bounds.Min.Y, bounds.Min.Z, spacing),
                GridCountsAndProbeCount = new Vector4(_probeCountX, _probeCountY, _probeCountZ, _probeCount),
                AtlasTexelsAndRayCount = new Vector4(IrradianceTexelsPerProbe, VisibilityTexelsPerProbe, _raysPerProbe, gi.FarFieldClipmapResolution),
                HysteresisFrameAndFlags = new Vector4(gi.SimpleDdgiHysteresis, _frameIndex, BuildFlags(gi), gi.FarFieldStartDistance),
                EnvironmentRadianceAndIntensity = new Vector4(
                    0.0f,
                    0.0f,
                    0.0f,
                    environmentIntensity),
                ProbeUpdateRange = new Vector4(_updateStartProbe, _probesToUpdate, 0.0f, 0.0f),
                DebugAndBias = new Vector4((float)gi.DebugView, gi.DdgiSelfShadowBiasScale, gi.IndirectIntensity, gi.FarFieldMaxTraceSteps),
                Reserved0 = Vector4.Zero
            };

            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _paramsBuffer,
                _lastParams,
                barrierDescription: new UploadBarrierDescription(PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit));

            _frameIndex++;
        }

        private void EnsureCapacity(int probeCount, int raysPerProbe)
        {
            ulong irradianceBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * IrradianceTexelsPerProbe * IrradianceTexelsPerProbe * AtlasTexelStride));
            ulong visibilityBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * VisibilityTexelsPerProbe * VisibilityTexelsPerProbe * AtlasTexelStride));
            ulong rayBytes = checked(Math.Max(MinBufferSize, (ulong)Math.Max(1, probeCount) * (ulong)Math.Max(1, raysPerProbe) * RayResultStride));

            EnsureBuffer(ref _irradianceAtlasBuffer, ref _irradianceAtlasBytes, irradianceBytes, "Simple DDGI Irradiance Atlas");
            EnsureBuffer(ref _visibilityAtlasBuffer, ref _visibilityAtlasBytes, visibilityBytes, "Simple DDGI Visibility Atlas");
            EnsureBuffer(ref _rayResultScratchBuffer, ref _rayScratchBytes, rayBytes, "Simple DDGI Ray Scratch");
        }

        private void EnsureBuffer(ref BufferHandle handle, ref ulong currentBytes, ulong requiredBytes, string debugName)
        {
            if (handle.IsValid && currentBytes >= requiredBytes)
                return;

            if (handle.IsValid)
                _bufferManager.DestroyBuffer(handle);

            handle = _bufferManager.CreateDeviceBuffer(
                requiredBytes,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                category: MemoryBudgetCategory.GlobalIllumination,
                debugName: debugName);
            currentBytes = requiredBytes;
            if (_registeredBindlessHeap != null)
                Register(_registeredBindlessHeap);
        }

        private void RegisterIfValid(int index, BufferHandle handle, ulong size)
        {
            if (_registeredBindlessHeap == null || !handle.IsValid)
                return;

            _registeredBindlessHeap.RegisterStorageBuffer(index, _bufferManager.GetBuffer(handle), 0, Math.Max(MinBufferSize, size));
        }

        private static BoundingBox ExpandBounds(BoundingBox bounds, float padding)
        {
            Vector3 p = new(Math.Max(padding, 0.0f));
            return new BoundingBox(bounds.Min - p, bounds.Max + p);
        }

        private static uint BuildFlags(GlobalIlluminationSettings settings)
        {
            uint flags = 1u;
            if (settings.FarFieldClipmapEnabled)
                flags |= 1u << 1;
            if (settings.FarFieldForceAll)
                flags |= 1u << 2;
            return flags;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_paramsBuffer.IsValid)
                _bufferManager.DestroyBuffer(_paramsBuffer);
            if (_irradianceAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_irradianceAtlasBuffer);
            if (_visibilityAtlasBuffer.IsValid)
                _bufferManager.DestroyBuffer(_visibilityAtlasBuffer);
            if (_rayResultScratchBuffer.IsValid)
                _bufferManager.DestroyBuffer(_rayResultScratchBuffer);
        }
    }
}

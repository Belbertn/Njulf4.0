using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed unsafe class EnvironmentManager : IDisposable
    {
        private const Format EnvironmentFormat = Format.R32G32B32A32Sfloat;
        private static readonly ulong EnvironmentDataSize = (ulong)Marshal.SizeOf<GPUEnvironmentData>();

        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly TextureManager _textureManager;
        private readonly RenderSettings _settings;

        private BufferHandle _environmentBuffer;
        private TextureHandle _environmentCubemap;
        private TextureHandle _irradianceCubemap;
        private TextureHandle _prefilteredCubemap;
        private TextureHandle _brdfLut;
        private ResourceSignature _resourceSignature;
        private uint _prefilteredMipCount;
        private ulong _estimatedBytes;
        private bool _usesFallback = true;
        private bool _disposed;

        public EnvironmentManager(
            VulkanContext context,
            BufferManager bufferManager,
            TextureManager textureManager,
            RenderSettings settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _environmentBuffer = _bufferManager.CreateDeviceBuffer(
                EnvironmentDataSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.EnvironmentMaps,
                "Environment Data Buffer");

            RecreateResources(CreateResourceSignature());
        }

        public bool UsesFallback => _usesFallback;
        public uint EnvironmentSize => _settings.Environment.EnvironmentSize;
        public uint IrradianceSize => _settings.Environment.IrradianceSize;
        public uint PrefilteredSize => _settings.Environment.PrefilteredSize;
        public uint PrefilteredMipCount => _prefilteredMipCount;
        public uint BrdfLutSize => _settings.Environment.BrdfLutSize;
        public ulong EstimatedBytes => _estimatedBytes;
        public ulong EnvironmentMapBytes => EstimateCubeBytes(EnvironmentSize, 1);
        public ulong IrradianceMapBytes => EstimateCubeBytes(IrradianceSize, 1);
        public ulong PrefilteredEnvironmentBytes => EstimateCubeBytes(PrefilteredSize, _prefilteredMipCount);
        public ulong BrdfLutBytes => checked((ulong)BrdfLutSize * BrdfLutSize * 16UL);

        public void EnsureResourcesCurrent(BindlessHeap? bindlessHeap = null)
        {
            ResourceSignature signature = CreateResourceSignature();
            if (signature.Equals(_resourceSignature))
                return;

            _context.WaitIdle();
            RecreateResources(signature);
            if (bindlessHeap != null)
                RegisterReflectionProbeFallback(bindlessHeap);
        }

        public void Register(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                BindlessIndex.EnvironmentDataBuffer,
                _bufferManager.GetBuffer(_environmentBuffer),
                0,
                EnvironmentDataSize);
        }

        public void RegisterReflectionProbeFallback(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            ImageView prefilteredView = _textureManager.GetTextureView(_prefilteredCubemap);
            bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeCubemapArrayTexture, prefilteredView);
            bindlessHeap.RegisterTexture(BindlessIndex.ReflectionProbeDebugTexture, prefilteredView);
        }

        public void Upload(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for environment upload.", nameof(commandBuffer));

            _settings.Environment.ClampDebugMipLevel(_prefilteredMipCount);
            GPUEnvironmentData data = CreateGpuData();
            GpuBufferUploader.UploadValueToBuffer(
                _context,
                _bufferManager,
                stagingRing,
                commandBuffer,
                _environmentBuffer,
                data,
                barrierDescription: new UploadBarrierDescription(
                    PipelineStageFlags2.FragmentShaderBit,
                    AccessFlags2.ShaderStorageReadBit,
                    size: EnvironmentDataSize));
        }

        private GPUEnvironmentData CreateGpuData()
        {
            return new GPUEnvironmentData
            {
                EnvironmentTextureIndex = BindlessIndex.EnvironmentCubemapTexture,
                IrradianceTextureIndex = BindlessIndex.IrradianceCubemapTexture,
                PrefilteredTextureIndex = BindlessIndex.PrefilteredEnvironmentTexture,
                BrdfLutTextureIndex = BindlessIndex.BrdfLutTexture,
                SkyIntensity = _settings.Environment.SkyIntensity,
                DiffuseIntensity = _settings.Environment.DiffuseIntensity,
                SpecularIntensity = _settings.Environment.SpecularIntensity,
                RotationRadians = _settings.Environment.RotationRadians,
                PrefilteredMipCount = _prefilteredMipCount,
                Enabled = _settings.Environment.Enabled ? 1u : 0u,
                DebugView = (uint)_settings.Environment.DebugView,
                DebugMipLevel = (uint)_settings.Environment.DebugMipLevel
            };
        }

        private void RecreateResources(ResourceSignature signature)
        {
            DestroyEnvironmentTextures();

            uint environmentSize = signature.EnvironmentSize;
            uint irradianceSize = signature.IrradianceSize;
            uint prefilteredSize = signature.PrefilteredSize;
            uint brdfSize = signature.BrdfLutSize;
            _prefilteredMipCount = CalculateMipLevels(prefilteredSize, prefilteredSize);
            EnvironmentPayload payload = CreateEnvironmentPayload(signature, _prefilteredMipCount);

            _environmentCubemap = _textureManager.CreateCubemap(
                environmentSize,
                EnvironmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.EnvironmentCubemapTexture,
                debugName: "Environment Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _environmentCubemap,
                payload.EnvironmentCubemap,
                environmentSize,
                environmentSize,
                EnvironmentFormat);

            _irradianceCubemap = _textureManager.CreateCubemap(
                irradianceSize,
                EnvironmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.IrradianceCubemapTexture,
                debugName: "Diffuse Irradiance Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _irradianceCubemap,
                payload.IrradianceCubemap,
                irradianceSize,
                irradianceSize,
                EnvironmentFormat);

            _prefilteredCubemap = _textureManager.CreateCubemap(
                prefilteredSize,
                EnvironmentFormat,
                mipLevels: _prefilteredMipCount,
                bindlessIndex: BindlessIndex.PrefilteredEnvironmentTexture,
                debugName: "Prefiltered Environment Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _prefilteredCubemap,
                payload.PrefilteredCubemap,
                prefilteredSize,
                prefilteredSize,
                EnvironmentFormat);

            _brdfLut = _textureManager.CreateTexture(
                brdfSize,
                brdfSize,
                EnvironmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.BrdfLutTexture);
            _textureManager.UploadTextureData(
                _brdfLut,
                GenerateBrdfLut(brdfSize),
                brdfSize,
                brdfSize,
                EnvironmentFormat);

            _estimatedBytes =
                EstimateCubeBytes(environmentSize, 1) +
                EstimateCubeBytes(irradianceSize, 1) +
                EstimateCubeBytes(prefilteredSize, _prefilteredMipCount) +
                checked((ulong)brdfSize * brdfSize * 16UL);

            _resourceSignature = signature;
            _usesFallback = payload.UsesFallback;
        }

        private EnvironmentPayload CreateEnvironmentPayload(ResourceSignature signature, uint prefilteredMipCount)
        {
            if (signature.SourceKind == EnvironmentSourceKind.HdrEquirectangular &&
                !string.IsNullOrWhiteSpace(signature.ResolvedSourcePath))
            {
                try
                {
                    HdrEquirectangularImage hdr = EnvironmentMapProcessor.LoadRadianceHdr(signature.ResolvedSourcePath);
                    return new EnvironmentPayload(
                        EnvironmentMapProcessor.ConvertEquirectangularToCubemap(hdr, signature.EnvironmentSize),
                        EnvironmentMapProcessor.GenerateIrradianceCubemap(hdr, signature.IrradianceSize),
                        EnvironmentMapProcessor.GeneratePrefilteredEnvironmentCubemap(hdr, signature.PrefilteredSize, prefilteredMipCount),
                        UsesFallback: false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    System.Diagnostics.Debug.WriteLine($"Environment HDR load failed for '{signature.ResolvedSourcePath}': {ex.Message}. Using procedural fallback.");
                }
            }

            return new EnvironmentPayload(
                EnvironmentMapProcessor.GenerateProceduralSkyCubemap(signature.EnvironmentSize, 1, blur: 0.0f),
                EnvironmentMapProcessor.GenerateProceduralSkyCubemap(signature.IrradianceSize, 1, blur: 0.85f),
                EnvironmentMapProcessor.GenerateProceduralSkyCubemap(signature.PrefilteredSize, prefilteredMipCount, blur: 0.0f),
                UsesFallback: true);
        }

        private static byte[] GenerateBrdfLut(uint size)
        {
            float[] values = new float[checked((int)(size * size * 4u))];
            int offset = 0;
            for (uint y = 0; y < size; y++)
            {
                float roughness = (y + 0.5f) / size;
                for (uint x = 0; x < size; x++)
                {
                    float nDotV = (x + 0.5f) / size;
                    float scale = 1.0f - 0.5f * roughness * roughness;
                    float bias = 0.04f * (1.0f - nDotV) * (1.0f - roughness);
                    values[offset++] = scale;
                    values[offset++] = bias;
                    values[offset++] = 0.0f;
                    values[offset++] = 1.0f;
                }
            }

            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        }

        private ResourceSignature CreateResourceSignature()
        {
            string sourcePath = ResolveEnvironmentSourcePath(_settings.Environment.SourcePath) ?? string.Empty;
            return new ResourceSignature(
                _settings.Environment.SourceKind,
                sourcePath,
                _settings.Environment.EnvironmentSize,
                _settings.Environment.IrradianceSize,
                _settings.Environment.PrefilteredSize,
                _settings.Environment.BrdfLutSize);
        }

        private static string? ResolveEnvironmentSourcePath(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;

            if (Path.IsPathRooted(sourcePath))
                return Path.GetFullPath(sourcePath);

            string currentDirectoryPath = Path.GetFullPath(sourcePath);
            if (File.Exists(currentDirectoryPath))
                return currentDirectoryPath;

            string appDirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, sourcePath));
            return File.Exists(appDirectoryPath) ? appDirectoryPath : currentDirectoryPath;
        }

        private static uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint maxDimension = Math.Max(width, height);
            while (maxDimension > 1)
            {
                maxDimension /= 2;
                levels++;
            }

            return levels;
        }

        private static ulong EstimateCubeBytes(uint size, uint mipLevels)
        {
            ulong total = 0;
            uint mipSize = size;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                total = checked(total + (ulong)mipSize * mipSize * 6UL * 16UL);
                mipSize = Math.Max(1u, mipSize / 2u);
            }

            return total;
        }

        private void DestroyEnvironmentTextures()
        {
            if (_environmentCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_environmentCubemap);
                _environmentCubemap = TextureHandle.Invalid;
            }

            if (_irradianceCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_irradianceCubemap);
                _irradianceCubemap = TextureHandle.Invalid;
            }

            if (_prefilteredCubemap.IsValid)
            {
                _textureManager.DestroyTexture(_prefilteredCubemap);
                _prefilteredCubemap = TextureHandle.Invalid;
            }

            if (_brdfLut.IsValid)
            {
                _textureManager.DestroyTexture(_brdfLut);
                _brdfLut = TextureHandle.Invalid;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            DestroyEnvironmentTextures();
            if (_environmentBuffer.IsValid)
                _bufferManager.DestroyBuffer(_environmentBuffer);
        }

        private readonly record struct EnvironmentPayload(
            byte[] EnvironmentCubemap,
            byte[] IrradianceCubemap,
            byte[] PrefilteredCubemap,
            bool UsesFallback);

        private readonly record struct ResourceSignature(
            EnvironmentSourceKind SourceKind,
            string ResolvedSourcePath,
            uint EnvironmentSize,
            uint IrradianceSize,
            uint PrefilteredSize,
            uint BrdfLutSize);
    }
}

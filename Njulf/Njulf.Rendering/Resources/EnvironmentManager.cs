using System;
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

            RecreateResources();
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
            var (stagingHandle, stagingOffset) = stagingRing.Allocate(EnvironmentDataSize);
            void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);

            System.Buffer.MemoryCopy(&data, (byte*)mappedData + stagingOffset, EnvironmentDataSize, EnvironmentDataSize);
            _bufferManager.FlushBuffer(stagingHandle, stagingOffset, EnvironmentDataSize);

            var region = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = 0,
                Size = EnvironmentDataSize
            };

            _context.Api.CmdCopyBuffer(
                commandBuffer,
                _bufferManager.GetBuffer(stagingHandle),
                _bufferManager.GetBuffer(_environmentBuffer),
                1,
                &region);

            var barrier = new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager.GetBuffer(_environmentBuffer),
                Offset = 0,
                Size = EnvironmentDataSize
            };

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = 1,
                PBufferMemoryBarriers = &barrier
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
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

        private void RecreateResources()
        {
            uint environmentSize = _settings.Environment.EnvironmentSize;
            uint irradianceSize = _settings.Environment.IrradianceSize;
            uint prefilteredSize = _settings.Environment.PrefilteredSize;
            uint brdfSize = _settings.Environment.BrdfLutSize;
            _prefilteredMipCount = CalculateMipLevels(prefilteredSize, prefilteredSize);

            _environmentCubemap = _textureManager.CreateCubemap(
                environmentSize,
                EnvironmentFormat,
                mipLevels: 1,
                bindlessIndex: BindlessIndex.EnvironmentCubemapTexture,
                debugName: "Environment Cubemap");
            _textureManager.UploadTextureDataAllMipsAndLayers(
                _environmentCubemap,
                GenerateSkyCubemap(environmentSize, 1, blur: 0.0f),
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
                GenerateSkyCubemap(irradianceSize, 1, blur: 0.85f),
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
                GenerateSkyCubemap(prefilteredSize, _prefilteredMipCount, blur: 0.0f),
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
        }

        private static byte[] GenerateSkyCubemap(uint baseSize, uint mipLevels, float blur)
        {
            ulong totalFloats = 0;
            uint size = baseSize;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                totalFloats = checked(totalFloats + (ulong)size * size * 6UL * 4UL);
                size = Math.Max(1u, size / 2u);
            }

            if (totalFloats > int.MaxValue)
                throw new InvalidOperationException("Generated environment texture is too large for a single upload.");

            float[] values = new float[checked((int)totalFloats)];
            int offset = 0;
            size = baseSize;
            for (uint mip = 0; mip < mipLevels; mip++)
            {
                float mipBlur = Math.Clamp(blur + (mipLevels <= 1 ? 0f : mip / (float)(mipLevels - 1)), 0f, 1f);
                for (uint face = 0; face < 6; face++)
                {
                    for (uint y = 0; y < size; y++)
                    {
                        for (uint x = 0; x < size; x++)
                        {
                            var direction = CubeDirection(face, (x + 0.5f) / size, (y + 0.5f) / size);
                            var color = ProceduralSky(direction.X, direction.Y, direction.Z, mipBlur);
                            values[offset++] = color.R;
                            values[offset++] = color.G;
                            values[offset++] = color.B;
                            values[offset++] = 1.0f;
                        }
                    }
                }

                size = Math.Max(1u, size / 2u);
            }

            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
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

        private static (float X, float Y, float Z) CubeDirection(uint face, float u, float v)
        {
            float a = 2.0f * u - 1.0f;
            float b = 1.0f - 2.0f * v;
            (float x, float y, float z) = face switch
            {
                0 => (1.0f, b, -a),
                1 => (-1.0f, b, a),
                2 => (a, 1.0f, -b),
                3 => (a, -1.0f, b),
                4 => (a, b, 1.0f),
                _ => (-a, b, -1.0f)
            };

            float length = MathF.Sqrt(x * x + y * y + z * z);
            return (x / length, y / length, z / length);
        }

        private static (float R, float G, float B) ProceduralSky(float x, float y, float z, float blur)
        {
            float t = Math.Clamp(y * 0.5f + 0.5f, 0.0f, 1.0f);
            var ground = (R: 0.03f, G: 0.035f, B: 0.04f);
            var horizon = (R: 0.85f, G: 0.92f, B: 1.0f);
            var zenith = (R: 0.12f, G: 0.35f, B: 0.85f);
            var sky = Lerp(horizon, zenith, t * t);
            var color = y < 0.0f ? Lerp(ground, horizon, Math.Clamp((y + 1.0f) * 0.35f, 0.0f, 1.0f)) : sky;

            float sunDot = Math.Clamp(x * -0.3f + y * 0.72f + z * 0.62f, 0.0f, 1.0f);
            float sun = MathF.Pow(sunDot, 512.0f) * (1.0f - blur) * 8.0f;
            color = (color.R + sun, color.G + sun * 0.86f, color.B + sun * 0.62f);

            var average = (R: 0.34f, G: 0.45f, B: 0.58f);
            return Lerp(color, average, blur * 0.8f);
        }

        private static (float R, float G, float B) Lerp((float R, float G, float B) a, (float R, float G, float B) b, float t)
        {
            return (
                a.R + (b.R - a.R) * t,
                a.G + (b.G - a.G) * t,
                a.B + (b.B - a.B) * t);
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

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_environmentCubemap.IsValid)
                _textureManager.DestroyTexture(_environmentCubemap);
            if (_irradianceCubemap.IsValid)
                _textureManager.DestroyTexture(_irradianceCubemap);
            if (_prefilteredCubemap.IsValid)
                _textureManager.DestroyTexture(_prefilteredCubemap);
            if (_brdfLut.IsValid)
                _textureManager.DestroyTexture(_brdfLut);
            if (_environmentBuffer.IsValid)
                _bufferManager.DestroyBuffer(_environmentBuffer);
        }
    }
}

using Njulf.Rendering.Data;
using Njulf.Shaders;
using NUnit.Framework;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

[TestFixture]
public sealed unsafe class GtaoTemporalGpuTests
{
    [Test, Category("GPU")]
    public void ValidHistoryDoesNotResetAtTheAgeLimit()
    {
        using var gpu = new TemporalGpu();
        gpu.Initialize();
        var young = gpu.Run(1);
        foreach (uint age in new uint[] { 31, 32, 33, 255 })
        {
            var result = gpu.Run(age);
            Assert.That(result.History, Is.EqualTo(young.History), $"Valid bent direction must not jump at age {age}.");
            for (int pixel = 0; pixel < 64; pixel++)
            {
                uint state = BitConverter.ToUInt32(result.Geometry, pixel * 8 + 4);
                Assert.That((state >> 16) & 255, Is.EqualTo(32));
                Assert.That((state >> 24) & 127, Is.Zero);
            }
        }
        foreach (var result in new[] { gpu.Run(32, valid: false), gpu.Run(32, previousDepth: 3) })
        {
            Assert.That((float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(result.History)), Is.EqualTo(.2f).Within(.001f),
                "Invalid epochs and disocclusions must return to the current bent direction.");
            uint state = BitConverter.ToUInt32(result.Geometry, 4);
            Assert.That((state >> 16) & 255, Is.Zero);
            Assert.That((state >> 24) & 127, Is.Not.Zero);
        }
    }

    [Test, Category("GPU")]
    public void SubpixelReprojectionAcrossDepthEdge_DoesNotBlendFarHistory()
    {
        using var gpu = new TemporalGpu();
        gpu.Initialize();

        // Column 3 reprojects subpixel-straddling the x == 4 depth edge:
        // previousUv lands exactly at 4 / 8 so production-equivalent linear
        // sampling of the history blends the near texel at x == 3 and the
        // far texel at x == 4 with 0.5 / 0.5 weights. The far tap must be
        // rejected, so the result has to match a control run whose history
        // is near-side everywhere.
        byte[] nearHistory = new byte[512];
        byte[] edgeHistory = new byte[512];
        byte[] nearGeometry = new byte[512];
        byte[] edgeGeometry = new byte[512];
        byte[] motion = new byte[512];
        for (int pixel = 0; pixel < 64; pixel++)
        {
            bool farSide = pixel % 8 >= 4;
            TemporalGpu.GeometryPixel(0, 1f).CopyTo(nearGeometry, pixel * 8);
            TemporalGpu.GeometryPixel(0, farSide ? 5f : 1f).CopyTo(edgeGeometry, pixel * 8);
            TemporalGpu.HalfPixel(-.2f, 0, .1f, 1).CopyTo(nearHistory, pixel * 8);
            TemporalGpu.HalfPixel(farSide ? .9f : -.2f, 0, .5f, 1).CopyTo(edgeHistory, pixel * 8);
            if (pixel % 8 == 3)
                BitConverter.GetBytes(-0.0625f).CopyTo(motion, pixel * 8);
        }

        var edge = gpu.Run(0,
            previousPixels: edgeHistory,
            previousGeometryPixels: edgeGeometry,
            motionPixels: motion);
        var control = gpu.Run(0,
            previousPixels: nearHistory,
            previousGeometryPixels: nearGeometry,
            motionPixels: motion);

        for (int y = 0; y < 8; y++)
        {
            int offset = (y * 8 + 3) * 8;
            for (int b = 0; b < 8; b++)
            {
                Assert.That(edge.History[offset + b],
                    Is.EqualTo(control.History[offset + b]),
                    $"History byte {b} of straddling pixel (3, {y}) must match the near-side-only result.");
                Assert.That(edge.Geometry[offset + b],
                    Is.EqualTo(control.Geometry[offset + b]),
                    $"Geometry byte {b} of straddling pixel (3, {y}) must match the near-side-only result.");
            }
        }
    }

    // Dispatches the unmodified production temporal shader with real images.
    private sealed class TemporalGpu : IDisposable
    {
        private readonly Vk _vk = Vk.GetApi();
        private Instance _instance;
        private PhysicalDevice _physical;
        private PhysicalDeviceMemoryProperties _memory;
        private Device _device;
        private Queue _queue;
        private CommandPool _pool;
        private DescriptorSetLayout _setLayout;
        private DescriptorPool _descriptorPool;
        private DescriptorSet _set;
        private PipelineLayout _layout;
        private VkPipeline _pipeline;
        private Sampler _sampler;
        private Sampler _linearSampler;

        public void Initialize()
        {
            var application = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version13 };
            var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &application };
            Check(_vk.CreateInstance(&instanceInfo, null, out _instance));
            uint count = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &count, null));
            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pointer = devices)
                Check(_vk.EnumeratePhysicalDevices(_instance, &count, pointer));
            uint family = uint.MaxValue;
            foreach (PhysicalDevice candidate in devices)
            {
                const FormatFeatureFlags required = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.StorageImageBit;
                bool supported = true;
                foreach (Format imageFormat in new[] { Format.R16G16B16A16Sfloat, Format.R32G32Uint, Format.R32G32Sfloat })
                {
                    _vk.GetPhysicalDeviceFormatProperties(candidate, imageFormat, out FormatProperties format);
                    var needed = imageFormat == Format.R32G32Sfloat ? FormatFeatureFlags.SampledImageBit : required;
                    supported &= (format.LinearTilingFeatures & needed) == needed;
                }
                _vk.GetPhysicalDeviceFeatures(candidate, out PhysicalDeviceFeatures availableFeatures);
                if (!supported || !availableFeatures.ShaderStorageImageExtendedFormats)
                    continue;
                _vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties properties);
                if (properties.ApiVersion < Vk.Version13)
                    continue;
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* pointer = queues)
                    _vk.GetPhysicalDeviceQueueFamilyProperties(candidate, &queueCount, pointer);
                for (uint index = 0; index < queueCount; index++)
                {
                    if ((queues[index].QueueFlags & QueueFlags.ComputeBit) == 0)
                        continue;
                    _physical = candidate;
                    family = index;
                    TestContext.Progress.WriteLine($"GTAO GPU: {SilkMarshal.PtrToString((nint)properties.DeviceName)}");
                    break;
                }
                if (family != uint.MaxValue)
                    break;
            }
            if (family == uint.MaxValue)
                Assert.Ignore("No Vulkan 1.3 compute device supports linear RGBA16F sampled/storage images.");
            _vk.GetPhysicalDeviceMemoryProperties(_physical, out _memory);
            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = family,
                QueueCount = 1, PQueuePriorities = &priority
            };
            var features = new PhysicalDeviceVulkan13Features
            {
                SType = StructureType.PhysicalDeviceVulkan13Features, Maintenance4 = true
            };
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo, PNext = &features,
                QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo
            };
            var coreFeatures = new PhysicalDeviceFeatures { ShaderStorageImageExtendedFormats = true };
            deviceInfo.PEnabledFeatures = &coreFeatures;
            Check(_vk.CreateDevice(_physical, &deviceInfo, null, out _device));
            _vk.GetDeviceQueue(_device, family, 0, out _queue);
            var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = family };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _pool));
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MinFilter = Filter.Nearest, MagFilter = Filter.Nearest,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge
            };
            Check(_vk.CreateSampler(_device, &samplerInfo, null, out _sampler));
            var linearSamplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MinFilter = Filter.Linear, MagFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge
            };
            Check(_vk.CreateSampler(_device, &linearSamplerInfo, null, out _linearSampler));
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[7];
            for (uint i = 0; i < 7; i++)
                bindings[i] = new(i, i < 5 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit);
            var setInfo = new DescriptorSetLayoutCreateInfo
            { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 7, PBindings = bindings };
            Check(_vk.CreateDescriptorSetLayout(_device, &setInfo, null, out _setLayout));
            DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[2];
            sizes[0] = new(DescriptorType.CombinedImageSampler, 5);
            sizes[1] = new(DescriptorType.StorageImage, 2);
            var descriptorInfo = new DescriptorPoolCreateInfo
            { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 2, PPoolSizes = sizes };
            Check(_vk.CreateDescriptorPool(_device, &descriptorInfo, null, out _descriptorPool));
            DescriptorSetLayout setLayout = _setLayout;
            var allocateSet = new DescriptorSetAllocateInfo
            { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _descriptorPool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Check(_vk.AllocateDescriptorSets(_device, &allocateSet, out _set));
            var range = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, 48);
            var layoutInfo = new PipelineLayoutCreateInfo
            { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = 1, PPushConstantRanges = &range };
            Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out _layout));

            // Optional one-run baseline bytecode permits failure sensitivity
            // checks without reverting production source or replacing its DLL.
            string? baselineShader = Environment.GetEnvironmentVariable("NJULF_GTAO_TEST_SHADER");
            using Stream shader = string.IsNullOrEmpty(baselineShader)
                ? typeof(ShaderLibrary).Assembly.GetManifestResourceStream("Njulf.Shaders.gtao_temporal.comp")!
                : File.OpenRead(baselineShader);
            Assert.That(shader, Is.Not.Null, "Production GTAO shader resource is missing.");
            using var bytes = new MemoryStream();
            shader.CopyTo(bytes);
            fixed (byte* code = bytes.ToArray())
            {
                var moduleInfo = new ShaderModuleCreateInfo
                { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)bytes.Length, PCode = (uint*)code };
                Check(_vk.CreateShaderModule(_device, &moduleInfo, null, out ShaderModule module));
                try
                {
                    byte* entry = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
                    var pipelineInfo = new ComputePipelineCreateInfo
                    {
                        SType = StructureType.ComputePipelineCreateInfo, Layout = _layout, BasePipelineIndex = -1,
                        Stage = new PipelineShaderStageCreateInfo
                        { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = entry }
                    };
                    Check(_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out _pipeline));
                }
                finally { _vk.DestroyShaderModule(_device, module, null); }
            }
        }

        public (byte[] History, byte[] Geometry) Run(
            uint age,
            bool valid = true,
            float previousDepth = 1f,
            byte[]? previousPixels = null,
            byte[]? previousGeometryPixels = null,
            byte[]? motionPixels = null)
        {
            using var raw = new HostImage(this, Format.R16G16B16A16Sfloat, ImageUsageFlags.SampledBit);
            using var geometry = new HostImage(this, Format.R32G32Uint, ImageUsageFlags.SampledBit);
            using var motion = new HostImage(this, Format.R32G32Sfloat, ImageUsageFlags.SampledBit);
            using var previous = new HostImage(this, Format.R16G16B16A16Sfloat, ImageUsageFlags.SampledBit);
            using var previousGeometry = new HostImage(this, Format.R32G32Uint, ImageUsageFlags.SampledBit);
            using var output = new HostImage(this, Format.R16G16B16A16Sfloat, ImageUsageFlags.StorageBit);
            using var outputGeometry = new HostImage(this, Format.R32G32Uint, ImageUsageFlags.StorageBit);
            raw.Fill(HalfPixel(.2f, 0, .5f, 1));
            geometry.Fill(GeometryPixel(0, 1));
            if (previousPixels is null)
                previous.Fill(HalfPixel(-.2f, 0, .5f, 1));
            else
                previous.FillRaw(previousPixels);
            if (previousGeometryPixels is null)
                previousGeometry.Fill(GeometryPixel(age, previousDepth));
            else
                previousGeometry.FillRaw(previousGeometryPixels);
            if (motionPixels is null)
                motion.Fill(new byte[8]);
            else
                motion.FillRaw(motionPixels);
            HostImage[] images = [raw, geometry, motion, previous, previousGeometry, output, outputGeometry];
            DescriptorImageInfo* infos = stackalloc DescriptorImageInfo[7];
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[7];
            for (uint i = 0; i < 7; i++)
            {
                // Binding 3 (PreviousHistory) uses production-equivalent
                // linear sampling; every other sampled binding is nearest.
                infos[i] = new(
                    i == 3 ? _linearSampler : i < 5 ? _sampler : default,
                    images[i].View, ImageLayout.General);
                writes[i] = new WriteDescriptorSet
                { SType = StructureType.WriteDescriptorSet, DstSet = _set, DstBinding = i, DescriptorCount = 1,
                  DescriptorType = i < 5 ? DescriptorType.CombinedImageSampler : DescriptorType.StorageImage, PImageInfo = &infos[i] };
            }
            _vk.UpdateDescriptorSets(_device, 7, writes, 0, null);
            var allocate = new CommandBufferAllocateInfo
            { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer command;
            Check(_vk.AllocateCommandBuffers(_device, &allocate, &command));
            try
            {
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                Check(_vk.BeginCommandBuffer(command, &begin));
                ImageMemoryBarrier* barriers = stackalloc ImageMemoryBarrier[7];
                for (int i = 0; i < 7; i++) barriers[i] = Barrier(images[i].Image, i < 5 ? AccessFlags.ShaderReadBit : AccessFlags.ShaderWriteBit);
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.HostBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 7, barriers);
                _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, _pipeline);
                DescriptorSet set = _set;
                _vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
                var push = new GPUGtaoTemporalPushConstants
                {
                    Dimensions = new(8, 8), SceneDimensions = new(8, 8), HistoryValid = valid ? 1u : 0u,
                    MaximumHistoryAge = 32, DepthThresholdScale = .03f, NormalThreshold = .85f,
                    StableHistoryWeight = .92f, MotionRejectionScale = .15f
                };
                _vk.CmdPushConstants(command, _layout, ShaderStageFlags.ComputeBit, 0, 48, &push);
                _vk.CmdDispatch(command, 1, 1, 1);
                var readback = new MemoryBarrier
                { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
                _vk.CmdPipelineBarrier(command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &readback, 0, null, 0, null);
                Check(_vk.EndCommandBuffer(command));
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &command };
                Check(_vk.QueueSubmit(_queue, 1, &submit, default));
                Check(_vk.QueueWaitIdle(_queue));
                return (output.Read(), outputGeometry.Read());
            }
            finally { _vk.FreeCommandBuffers(_device, _pool, 1, &command); }
        }
        internal static byte[] HalfPixel(params float[] values) => values.SelectMany(v => BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)v))).ToArray();
        internal static byte[] GeometryPixel(uint age, float depth)
        {
            uint state = BitConverter.HalfToUInt16Bits((Half)MathF.Log2(1 + depth)) | (age << 16) | 0x80000000u;
            return BitConverter.GetBytes(0u).Concat(BitConverter.GetBytes(state)).ToArray();
        }

        private static ImageMemoryBarrier Barrier(Image image, AccessFlags access) => new()
        {
            SType = StructureType.ImageMemoryBarrier, Image = image,
            OldLayout = ImageLayout.Preinitialized, NewLayout = ImageLayout.General,
            SrcAccessMask = AccessFlags.HostWriteBit, DstAccessMask = access,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
        };

        public void Dispose()
        {
            if (_device.Handle != 0)
            {
                _vk.DeviceWaitIdle(_device);
                if (_pipeline.Handle != 0) _vk.DestroyPipeline(_device, _pipeline, null);
                if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_device, _layout, null);
                if (_descriptorPool.Handle != 0) _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
                if (_setLayout.Handle != 0) _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
                if (_sampler.Handle != 0) _vk.DestroySampler(_device, _sampler, null);
                if (_linearSampler.Handle != 0) _vk.DestroySampler(_device, _linearSampler, null);
                if (_pool.Handle != 0) _vk.DestroyCommandPool(_device, _pool, null);
                _vk.DestroyDevice(_device, null);
            }
            if (_instance.Handle != 0) _vk.DestroyInstance(_instance, null);
            _vk.Dispose();
        }

        private sealed class HostImage : IDisposable
        {
            private readonly TemporalGpu _gpu;
            private const uint _width = 8, _height = 8;
            private DeviceMemory _allocation;
            private SubresourceLayout _subresource;
            public Image Image;
            public ImageView View;

            public HostImage(TemporalGpu gpu, Format format, ImageUsageFlags usage)
            {
                _gpu = gpu;
                try
                {
                    var info = new ImageCreateInfo
                    {
                        SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
                        Format = format, Extent = new(_width, _height, 1), MipLevels = 1, ArrayLayers = 1,
                        Samples = SampleCountFlags.Count1Bit, Tiling = ImageTiling.Linear,
                        Usage = usage, InitialLayout = ImageLayout.Preinitialized, SharingMode = SharingMode.Exclusive
                    };
                    Check(gpu._vk.CreateImage(gpu._device, &info, null, out Image));
                    gpu._vk.GetImageMemoryRequirements(gpu._device, Image, out MemoryRequirements requirements);
                    uint memoryType = uint.MaxValue;
                    const MemoryPropertyFlags required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    for (uint index = 0; index < gpu._memory.MemoryTypeCount; index++)
                        if ((requirements.MemoryTypeBits & (1u << (int)index)) != 0 &&
                            (gpu._memory.MemoryTypes[(int)index].PropertyFlags & required) == required)
                        { memoryType = index; break; }
                    if (memoryType == uint.MaxValue)
                        Assert.Ignore("Linear test images have no host-visible coherent memory type.");
                    var allocate = new MemoryAllocateInfo
                    { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = memoryType };
                    Check(gpu._vk.AllocateMemory(gpu._device, &allocate, null, out _allocation));
                    Check(gpu._vk.BindImageMemory(gpu._device, Image, _allocation, 0));
                    var view = new ImageViewCreateInfo
                    { SType = StructureType.ImageViewCreateInfo, Image = Image, ViewType = ImageViewType.Type2D, Format = format, SubresourceRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
                    Check(gpu._vk.CreateImageView(gpu._device, &view, null, out View));
                    var subresource = new ImageSubresource(ImageAspectFlags.ColorBit, 0, 0);
                    gpu._vk.GetImageSubresourceLayout(gpu._device, Image, &subresource, out _subresource);
                }
                catch { Dispose(); throw; }
            }

            public void Fill(byte[] pixel)
            {
                var values = new byte[8 * 8 * 8];
                for (int i = 0; i < 64; i++) pixel.CopyTo(values, i * 8);
                Copy(values, true);
            }
            public void FillRaw(byte[] values) => Copy(values, true);
            public byte[] Read() { var values = new byte[8 * 8 * 8]; Copy(values, false); return values; }
            private void Copy(byte[] values, bool write)
            {
                void* pointer;
                Check(_gpu._vk.MapMemory(_gpu._device, _allocation, 0, Vk.WholeSize, 0, &pointer));
                try
                {
                    for (int y = 0; y < 8; y++)
                    {
                        var row = new Span<byte>((byte*)pointer + _subresource.Offset + (ulong)y * _subresource.RowPitch, 64);
                        Span<byte> data = values.AsSpan(y * 64, 64);
                        if (write) data.CopyTo(row); else row.CopyTo(data);
                    }
                }
                finally { _gpu._vk.UnmapMemory(_gpu._device, _allocation); }
            }
            public void Dispose()
            {
                if (View.Handle != 0) _gpu._vk.DestroyImageView(_gpu._device, View, null);
                if (Image.Handle != 0) _gpu._vk.DestroyImage(_gpu._device, Image, null);
                if (_allocation.Handle != 0) _gpu._vk.FreeMemory(_gpu._device, _allocation, null);
            }
        }

        private static void Check(Result result) => Assert.That(result, Is.EqualTo(Result.Success), "Vulkan GTAO dispatch failed.");
    }
}

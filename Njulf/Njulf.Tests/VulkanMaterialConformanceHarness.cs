using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Tests;

/// <summary>
/// Minimal surface-free Vulkan compute harness used by the executable
/// material oracle. It deliberately avoids the renderer's window, swapchain,
/// bindless, and optional feature requirements so the conformance dispatch can
/// run on CI compute devices and software Vulkan implementations.
/// </summary>
internal sealed unsafe class VulkanMaterialConformanceHarness : IDisposable
{
    private const uint DescriptorBindingCount = 6;
    private const uint ShaderLocalSizeX = 64;

    private Vk _vk = null!;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _computeQueue;
    private uint _computeQueueFamilyIndex;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private VkPipeline _pipeline;
    private CommandPool _commandPool;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private bool _disposed;

    private VulkanMaterialConformanceHarness()
    {
    }

    public string DeviceName { get; private set; } = string.Empty;

    public uint DeviceApiVersion { get; private set; }

    public uint DriverVersion { get; private set; }

    public static bool TryCreate(
        out VulkanMaterialConformanceHarness? harness,
        out string unavailableReason)
    {
        var candidate = new VulkanMaterialConformanceHarness();
        try
        {
            candidate.Initialize();
            harness = candidate;
            unavailableReason = string.Empty;
            return true;
        }
        catch (VulkanConformanceUnavailableException exception)
        {
            candidate.Dispose();
            harness = null;
            unavailableReason = exception.Message;
            return false;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public VulkanMaterialConformanceOutput Run(
        ReadOnlySpan<GPUMaterialData> materials,
        ReadOnlySpan<GPUGiMaterialExtensionConformanceElement> extensions,
        ReadOnlySpan<GPUGiMaterialConformanceCase> cases)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (materials.Length == 0)
            throw new ArgumentException("At least one material conformance case is required.", nameof(materials));
        if (extensions.Length != materials.Length || cases.Length != materials.Length)
        {
            throw new ArgumentException(
                "Material, extension, and sampled-input arrays must have identical lengths.");
        }

        using HostStorageBuffer materialInput = CreateInitializedBuffer(materials);
        using HostStorageBuffer extensionInput = CreateInitializedBuffer(extensions);
        using HostStorageBuffer caseInput = CreateInitializedBuffer(cases);
        using HostStorageBuffer resultOutput =
            CreateOutputBuffer<GPUGiMaterialConformanceResult>(materials.Length);
        using HostStorageBuffer materialRoundTrip =
            CreateOutputBuffer<GPUMaterialData>(materials.Length);
        using HostStorageBuffer extensionRoundTrip =
            CreateOutputBuffer<GPUGiMaterialExtensionConformanceElement>(materials.Length);

        HostStorageBuffer[] buffers =
        [
            materialInput,
            extensionInput,
            caseInput,
            resultOutput,
            materialRoundTrip,
            extensionRoundTrip
        ];

        UpdateDescriptorSet(buffers);
        Execute((uint)materials.Length);

        return new VulkanMaterialConformanceOutput(
            resultOutput.ReadArray<GPUGiMaterialConformanceResult>(materials.Length),
            materialRoundTrip.ReadArray<GPUMaterialData>(materials.Length),
            extensionRoundTrip.ReadArray<GPUGiMaterialExtensionConformanceElement>(materials.Length),
            DeviceName,
            DeviceApiVersion,
            DriverVersion);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_vk != null && _device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            if (_commandPool.Handle != 0)
                _vk.DestroyCommandPool(_device, _commandPool, null);
            if (_pipeline.Handle != 0)
                _vk.DestroyPipeline(_device, _pipeline, null);
            if (_pipelineLayout.Handle != 0)
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            if (_descriptorPool.Handle != 0)
                _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            if (_descriptorSetLayout.Handle != 0)
                _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_vk != null && _instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        (_vk as IDisposable)?.Dispose();
        _device = default;
        _instance = default;
    }

    private void Initialize()
    {
        try
        {
            _vk = Vk.GetApi();
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            BadImageFormatException or
            EntryPointNotFoundException or
            PlatformNotSupportedException or
            TypeInitializationException)
        {
            throw new VulkanConformanceUnavailableException(
                $"Vulkan loader is unavailable: {exception.GetBaseException().Message}");
        }

        CreateInstance();
        SelectPhysicalDevice();
        CreateDevice();
        CreateDescriptorResources();
        CreatePipeline();
        CreateCommandPool();

        // Resolve the memory requirement up front so a missing host-visible
        // storage-buffer memory type is reported as an explicit capability
        // skip instead of masking a dispatch or shader failure.
        using HostStorageBuffer _ = CreateHostStorageBuffer(sizeof(uint));
    }

    private void CreateInstance()
    {
        var applicationInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13
        };
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo
        };

        Result result = _vk.CreateInstance(&createInfo, null, out _instance);
        if (result == Result.ErrorIncompatibleDriver)
        {
            throw new VulkanConformanceUnavailableException(
                "The installed Vulkan loader cannot create a Vulkan 1.3 instance.");
        }

        RequireSuccess(result, "create the headless Vulkan 1.3 instance");
    }

    private void SelectPhysicalDevice()
    {
        uint count = 0;
        Result result = _vk.EnumeratePhysicalDevices(_instance, &count, null);
        RequireSuccess(result, "enumerate Vulkan physical devices");
        if (count == 0)
            throw new VulkanConformanceUnavailableException("No Vulkan physical device is available.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* devicesPointer = devices)
        {
            result = _vk.EnumeratePhysicalDevices(_instance, &count, devicesPointer);
            RequireSuccess(result, "enumerate Vulkan physical devices");
        }

        int selectedScore = int.MinValue;
        string rejectionSummary = "no Vulkan 1.3 device with a compute queue";
        foreach (PhysicalDevice candidate in devices)
        {
            var properties = new PhysicalDeviceProperties();
            _vk.GetPhysicalDeviceProperties(candidate, &properties);
            string candidateName =
                SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unnamed Vulkan device";

            if (properties.ApiVersion < Vk.Version13)
            {
                rejectionSummary =
                    $"{candidateName} exposes Vulkan {FormatApiVersion(properties.ApiVersion)}, below 1.3";
                continue;
            }

            if (properties.Limits.MaxPerStageDescriptorStorageBuffers < DescriptorBindingCount ||
                properties.Limits.MaxDescriptorSetStorageBuffers < DescriptorBindingCount)
            {
                rejectionSummary =
                    $"{candidateName} exposes fewer than {DescriptorBindingCount} storage-buffer descriptors";
                continue;
            }

            if (properties.Limits.MaxComputeWorkGroupInvocations < ShaderLocalSizeX ||
                properties.Limits.MaxComputeWorkGroupSize[0] < ShaderLocalSizeX)
            {
                rejectionSummary =
                    $"{candidateName} cannot execute a {ShaderLocalSizeX}-thread compute workgroup";
                continue;
            }

            uint queueFamilyIndex = FindComputeQueueFamily(candidate);
            if (queueFamilyIndex == uint.MaxValue)
            {
                rejectionSummary = $"{candidateName} does not expose a compute queue";
                continue;
            }

            var memoryProperties = new PhysicalDeviceMemoryProperties();
            _vk.GetPhysicalDeviceMemoryProperties(candidate, &memoryProperties);
            if (!HasHostVisibleMemory(memoryProperties))
            {
                rejectionSummary = $"{candidateName} does not expose host-visible memory";
                continue;
            }

            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 400,
                PhysicalDeviceType.IntegratedGpu => 300,
                PhysicalDeviceType.VirtualGpu => 200,
                PhysicalDeviceType.Cpu => 100,
                _ => 0
            };
            if (score <= selectedScore)
                continue;

            selectedScore = score;
            _physicalDevice = candidate;
            _computeQueueFamilyIndex = queueFamilyIndex;
            _memoryProperties = memoryProperties;
            DeviceName = candidateName;
            DeviceApiVersion = properties.ApiVersion;
            DriverVersion = properties.DriverVersion;
        }

        if (_physicalDevice.Handle == 0)
        {
            throw new VulkanConformanceUnavailableException(
                $"No compatible headless material-conformance device is available: {rejectionSummary}.");
        }
    }

    private uint FindComputeQueueFamily(PhysicalDevice physicalDevice)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, null);
        if (count == 0)
            return uint.MaxValue;

        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* familiesPointer = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, familiesPointer);

        uint fallback = uint.MaxValue;
        for (uint index = 0; index < count; index++)
        {
            QueueFamilyProperties family = families[index];
            if (family.QueueCount == 0 || (family.QueueFlags & QueueFlags.ComputeBit) == 0)
                continue;

            if ((family.QueueFlags & QueueFlags.GraphicsBit) == 0)
                return index;
            fallback = index;
        }

        return fallback;
    }

    private static bool HasHostVisibleMemory(PhysicalDeviceMemoryProperties properties)
    {
        for (uint index = 0; index < properties.MemoryTypeCount; index++)
        {
            if ((properties.MemoryTypes[(int)index].PropertyFlags &
                 MemoryPropertyFlags.HostVisibleBit) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _computeQueueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &priority
        };
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo
        };

        RequireSuccess(
            _vk.CreateDevice(_physicalDevice, &createInfo, null, out _device),
            $"create a logical compute device for {DeviceName}");
        _vk.GetDeviceQueue(_device, _computeQueueFamilyIndex, 0, out _computeQueue);
    }

    private void CreateDescriptorResources()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[(int)DescriptorBindingCount];
        for (uint index = 0; index < DescriptorBindingCount; index++)
        {
            bindings[index] = new DescriptorSetLayoutBinding
            {
                Binding = index,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = DescriptorBindingCount,
            PBindings = bindings
        };
        RequireSuccess(
            _vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _descriptorSetLayout),
            "create the material-conformance descriptor layout");

        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.StorageBuffer,
            DescriptorCount = DescriptorBindingCount
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize
        };
        RequireSuccess(
            _vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool),
            "create the material-conformance descriptor pool");

        DescriptorSetLayout layout = _descriptorSetLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        RequireSuccess(
            _vk.AllocateDescriptorSets(_device, &allocateInfo, out _descriptorSet),
            "allocate the material-conformance descriptor set");

        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0,
            Size = sizeof(uint)
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &layout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        RequireSuccess(
            _vk.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout),
            "create the material-conformance pipeline layout");
    }

    private void CreatePipeline()
    {
        byte[] spirv = LoadShaderBytes();
        ShaderModule shaderModule = default;
        try
        {
            fixed (byte* code = spirv)
            {
                var moduleInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)spirv.Length,
                    PCode = (uint*)code
                };
                RequireSuccess(
                    _vk.CreateShaderModule(_device, &moduleInfo, null, out shaderModule),
                    "create the material-conformance shader module");
            }

            byte* entryPoint = stackalloc byte[5];
            entryPoint[0] = (byte)'m';
            entryPoint[1] = (byte)'a';
            entryPoint[2] = (byte)'i';
            entryPoint[3] = (byte)'n';
            entryPoint[4] = 0;
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = shaderModule,
                PName = entryPoint
            };
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout,
                BasePipelineIndex = -1
            };
            RequireSuccess(
                _vk.CreateComputePipelines(
                    _device,
                    default,
                    1,
                    &pipelineInfo,
                    null,
                    out _pipeline),
                "create the material-conformance compute pipeline");
        }
        finally
        {
            if (shaderModule.Handle != 0)
                _vk.DestroyShaderModule(_device, shaderModule, null);
        }
    }

    private static byte[] LoadShaderBytes()
    {
        string resourceName =
            $"Njulf.Shaders.{GiMaterialGpuConformanceContract.ShaderResourceName}";
        using Stream stream = typeof(ShaderLibrary).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded material-conformance shader '{resourceName}' was not found.");
        if (stream.Length <= 0 || stream.Length % sizeof(uint) != 0)
            throw new InvalidOperationException($"Embedded shader '{resourceName}' is not valid SPIR-V.");

        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (bytes.Length < sizeof(uint) || BitConverter.ToUInt32(bytes, 0) != 0x0723_0203u)
            throw new InvalidOperationException($"Embedded shader '{resourceName}' has an invalid SPIR-V header.");
        return bytes;
    }

    private void CreateCommandPool()
    {
        var createInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = _computeQueueFamilyIndex
        };
        RequireSuccess(
            _vk.CreateCommandPool(_device, &createInfo, null, out _commandPool),
            "create the material-conformance command pool");
    }

    private HostStorageBuffer CreateInitializedBuffer<T>(ReadOnlySpan<T> values)
        where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        HostStorageBuffer buffer = CreateHostStorageBuffer((ulong)bytes.Length);
        try
        {
            buffer.Write(bytes);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private HostStorageBuffer CreateOutputBuffer<T>(int elementCount)
        where T : unmanaged
    {
        ulong size = checked((ulong)(elementCount * sizeof(T)));
        HostStorageBuffer buffer = CreateHostStorageBuffer(size);
        buffer.Clear();
        return buffer;
    }

    private HostStorageBuffer CreateHostStorageBuffer(ulong byteSize)
    {
        if (byteSize == 0)
            throw new ArgumentOutOfRangeException(nameof(byteSize));

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = byteSize,
            Usage = BufferUsageFlags.StorageBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        RequireSuccess(
            _vk.CreateBuffer(_device, &bufferInfo, null, out VkBuffer buffer),
            "create a host-visible material-conformance buffer");

        DeviceMemory memory = default;
        void* mapped = null;
        try
        {
            var requirements = new MemoryRequirements();
            _vk.GetBufferMemoryRequirements(_device, buffer, &requirements);
            if (!TryFindHostMemoryType(
                    requirements.MemoryTypeBits,
                    out uint memoryTypeIndex,
                    out bool hostCoherent))
            {
                throw new VulkanConformanceUnavailableException(
                    $"{DeviceName} has no host-visible memory type compatible with storage buffers.");
            }

            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = memoryTypeIndex
            };
            RequireSuccess(
                _vk.AllocateMemory(_device, &allocationInfo, null, out memory),
                "allocate material-conformance buffer memory");
            RequireSuccess(
                _vk.BindBufferMemory(_device, buffer, memory, 0),
                "bind material-conformance buffer memory");
            RequireSuccess(
                _vk.MapMemory(_device, memory, 0, requirements.Size, 0, &mapped),
                "map material-conformance buffer memory");

            return new HostStorageBuffer(
                this,
                buffer,
                memory,
                mapped,
                byteSize,
                requirements.Size,
                hostCoherent);
        }
        catch
        {
            if (mapped != null)
                _vk.UnmapMemory(_device, memory);
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
            _vk.DestroyBuffer(_device, buffer, null);
            throw;
        }
    }

    private bool TryFindHostMemoryType(
        uint compatibleTypes,
        out uint memoryTypeIndex,
        out bool hostCoherent)
    {
        uint nonCoherentFallback = uint.MaxValue;
        for (uint index = 0; index < _memoryProperties.MemoryTypeCount; index++)
        {
            if ((compatibleTypes & (1u << (int)index)) == 0)
                continue;

            MemoryPropertyFlags flags = _memoryProperties.MemoryTypes[(int)index].PropertyFlags;
            if ((flags & MemoryPropertyFlags.HostVisibleBit) == 0)
                continue;

            if ((flags & MemoryPropertyFlags.HostCoherentBit) != 0)
            {
                memoryTypeIndex = index;
                hostCoherent = true;
                return true;
            }

            nonCoherentFallback = index;
        }

        memoryTypeIndex = nonCoherentFallback;
        hostCoherent = false;
        return nonCoherentFallback != uint.MaxValue;
    }

    private void UpdateDescriptorSet(IReadOnlyList<HostStorageBuffer> buffers)
    {
        if (buffers.Count != DescriptorBindingCount)
            throw new ArgumentException($"Exactly {DescriptorBindingCount} buffers are required.", nameof(buffers));

        var bufferInfos = stackalloc DescriptorBufferInfo[(int)DescriptorBindingCount];
        var writes = stackalloc WriteDescriptorSet[(int)DescriptorBindingCount];
        for (uint index = 0; index < DescriptorBindingCount; index++)
        {
            HostStorageBuffer buffer = buffers[(int)index];
            bufferInfos[index] = new DescriptorBufferInfo
            {
                Buffer = buffer.Buffer,
                Offset = 0,
                Range = buffer.ByteSize
            };
            writes[index] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _descriptorSet,
                DstBinding = index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfos[index]
            };
        }

        _vk.UpdateDescriptorSets(_device, DescriptorBindingCount, writes, 0, null);
    }

    private void Execute(uint caseCount)
    {
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        try
        {
            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            RequireSuccess(
                _vk.AllocateCommandBuffers(_device, &allocateInfo, &commandBuffer),
                "allocate the material-conformance command buffer");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            RequireSuccess(
                _vk.BeginCommandBuffer(commandBuffer, &beginInfo),
                "begin the material-conformance command buffer");
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _pipeline);
            DescriptorSet descriptorSet = _descriptorSet;
            _vk.CmdBindDescriptorSets(
                commandBuffer,
                PipelineBindPoint.Compute,
                _pipelineLayout,
                0,
                1,
                &descriptorSet,
                0,
                null);
            _vk.CmdPushConstants(
                commandBuffer,
                _pipelineLayout,
                ShaderStageFlags.ComputeBit,
                0,
                sizeof(uint),
                &caseCount);
            _vk.CmdDispatch(commandBuffer, (caseCount + ShaderLocalSizeX - 1) / ShaderLocalSizeX, 1, 1);

            var memoryBarrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.HostReadBit
            };
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.HostBit,
                0,
                1,
                &memoryBarrier,
                0,
                null,
                0,
                null);
            RequireSuccess(
                _vk.EndCommandBuffer(commandBuffer),
                "end the material-conformance command buffer");

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            RequireSuccess(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "create the material-conformance completion fence");
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer
            };
            RequireSuccess(
                _vk.QueueSubmit(_computeQueue, 1, &submitInfo, fence),
                "submit the material-conformance dispatch");
            RequireSuccess(
                _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                "wait for the material-conformance dispatch");
        }
        finally
        {
            if (fence.Handle != 0)
                _vk.DestroyFence(_device, fence, null);
            if (commandBuffer.Handle != 0)
                _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }
    }

    private static string FormatApiVersion(uint version) =>
        $"{version >> 22}.{version >> 12 & 0x3ffu}.{version & 0xfffu}";

    private static void RequireSuccess(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: Vulkan returned {result}.");
    }

    private sealed class VulkanConformanceUnavailableException(string message) : Exception(message);

    private sealed unsafe class HostStorageBuffer : IDisposable
    {
        private readonly VulkanMaterialConformanceHarness _owner;
        private readonly DeviceMemory _memory;
        private readonly void* _mapped;
        private readonly ulong _allocationSize;
        private readonly bool _hostCoherent;
        private bool _disposed;

        public HostStorageBuffer(
            VulkanMaterialConformanceHarness owner,
            VkBuffer buffer,
            DeviceMemory memory,
            void* mapped,
            ulong byteSize,
            ulong allocationSize,
            bool hostCoherent)
        {
            _owner = owner;
            Buffer = buffer;
            _memory = memory;
            _mapped = mapped;
            ByteSize = byteSize;
            _allocationSize = allocationSize;
            _hostCoherent = hostCoherent;
        }

        public VkBuffer Buffer { get; }

        public ulong ByteSize { get; }

        public void Write(ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((ulong)source.Length != ByteSize)
                throw new ArgumentException("Source size does not match the mapped Vulkan buffer.", nameof(source));

            source.CopyTo(new Span<byte>(_mapped, source.Length));
            Flush();
        }

        public void Clear()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            new Span<byte>(_mapped, checked((int)ByteSize)).Clear();
            Flush();
        }

        public T[] ReadArray<T>(int elementCount)
            where T : unmanaged
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int byteCount = checked(elementCount * sizeof(T));
            if ((ulong)byteCount != ByteSize)
                throw new ArgumentException("Requested output shape does not match the mapped Vulkan buffer.");

            Invalidate();
            var output = new T[elementCount];
            new ReadOnlySpan<byte>(_mapped, byteCount).CopyTo(MemoryMarshal.AsBytes(output.AsSpan()));
            return output;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _owner._vk.UnmapMemory(_owner._device, _memory);
            _owner._vk.FreeMemory(_owner._device, _memory, null);
            _owner._vk.DestroyBuffer(_owner._device, Buffer, null);
        }

        private void Flush()
        {
            if (_hostCoherent)
                return;

            var range = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = _memory,
                Offset = 0,
                Size = _allocationSize
            };
            RequireSuccess(
                _owner._vk.FlushMappedMemoryRanges(_owner._device, 1, &range),
                "flush material-conformance buffer memory");
        }

        private void Invalidate()
        {
            if (_hostCoherent)
                return;

            var range = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = _memory,
                Offset = 0,
                Size = _allocationSize
            };
            RequireSuccess(
                _owner._vk.InvalidateMappedMemoryRanges(_owner._device, 1, &range),
                "invalidate material-conformance buffer memory");
        }
    }
}

internal sealed record VulkanMaterialConformanceOutput(
    GPUGiMaterialConformanceResult[] Results,
    GPUMaterialData[] MaterialRoundTrip,
    GPUGiMaterialExtensionConformanceElement[] ExtensionRoundTrip,
    string DeviceName,
    uint DeviceApiVersion,
    uint DriverVersion);

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Diagnostics;

/// <summary>
/// Surface-free Vulkan workload that compares real fragment-raster coverage
/// against real ray-query candidate visibility for one alpha-masked card.
/// This is deliberately separate from the game renderer so qualification can
/// run quickly and cannot accidentally substitute a lighting/radiance signal.
/// </summary>
public sealed unsafe class AlphaVisibilityVulkanHarness : IDisposable
{
    internal const string LoaderLayersDisableEnvironmentVariable =
        "VK_LOADER_LAYERS_DISABLE";
    internal const string LoaderImplicitLayerPathEnvironmentVariable =
        "VK_IMPLICIT_LAYER_PATH";
    internal const string DisableImplicitLoaderLayersFilter = "~implicit~";

    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";
    private const string DebugUtilsExtension = "VK_EXT_debug_utils";
    private const string DeferredHostOperationsExtension = "VK_KHR_deferred_host_operations";
    private const string AccelerationStructureExtension = "VK_KHR_acceleration_structure";
    private const string RayQueryExtension = "VK_KHR_ray_query";
    private const string Spirv14Extension = "VK_KHR_spirv_1_4";
    private const string ShaderFloatControlsExtension = "VK_KHR_shader_float_controls";
    private const uint DescriptorBindingCount = 5;
    private const uint ComputeLocalSize = 8;
    private const ShaderStageFlags ConformanceShaderStages =
        ShaderStageFlags.VertexBit |
        ShaderStageFlags.FragmentBit |
        ShaderStageFlags.ComputeBit;
    private static readonly object LoaderEnvironmentLock = new();

    private static readonly string[] RequiredDeviceExtensions =
    [
        DeferredHostOperationsExtension,
        AccelerationStructureExtension,
        RayQueryExtension,
        Spirv14Extension,
        ShaderFloatControlsExtension
    ];

    private readonly AlphaVisibilityValidationMessageState _validationMessages = new();
    private readonly List<OwnedBuffer> _buffers = [];
    private Vk _vk = null!;
    private Instance _instance;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private GCHandle _validationCallbackHandle;
    private PhysicalDevice _physicalDevice;
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private Device _device;
    private Queue _queue;
    private uint _queueFamilyIndex;
    private KhrAccelerationStructure? _accelerationStructure;
    private CommandPool _commandPool;
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private PipelineLayout _pipelineLayout;
    private VkPipeline _graphicsPipeline;
    private VkPipeline _computePipeline;
    private Image _textureImage;
    private DeviceMemory _textureMemory;
    private ImageView _textureView;
    private Sampler _textureSampler;
    private AccelerationStructureKHR _blas;
    private AccelerationStructureKHR _tlas;
    private uint _scratchAlignment = 256;
    private bool _disposed;

    private AlphaVisibilityVulkanHarness()
    {
    }

    public static AlphaVisibilityHardwareOutput Run()
    {
        // Loader layer filters are process-scoped. Serialize this standalone
        // qualification workload so its temporary isolation cannot race
        // another invocation in the same process.
        lock (LoaderEnvironmentLock)
        {
            using IDisposable loaderIsolation = BeginLoaderLayerIsolation();
            using var harness = new AlphaVisibilityVulkanHarness();
            harness.Initialize();
            return harness.Execute();
        }
    }

    internal static IDisposable BeginLoaderLayerIsolation()
    {
        return new VulkanLoaderIsolationScope();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_vk != null && _device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            if (_graphicsPipeline.Handle != 0)
                _vk.DestroyPipeline(_device, _graphicsPipeline, null);
            if (_computePipeline.Handle != 0)
                _vk.DestroyPipeline(_device, _computePipeline, null);
            if (_pipelineLayout.Handle != 0)
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
            if (_descriptorPool.Handle != 0)
                _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
            if (_descriptorSetLayout.Handle != 0)
                _vk.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
            if (_textureSampler.Handle != 0)
                _vk.DestroySampler(_device, _textureSampler, null);
            if (_textureView.Handle != 0)
                _vk.DestroyImageView(_device, _textureView, null);
            if (_textureImage.Handle != 0)
                _vk.DestroyImage(_device, _textureImage, null);
            if (_textureMemory.Handle != 0)
                _vk.FreeMemory(_device, _textureMemory, null);
            if (_tlas.Handle != 0)
                _accelerationStructure?.DestroyAccelerationStructure(_device, _tlas, null);
            if (_blas.Handle != 0)
                _accelerationStructure?.DestroyAccelerationStructure(_device, _blas, null);
            for (int index = _buffers.Count - 1; index >= 0; index--)
                DestroyBuffer(_buffers[index]);
            _buffers.Clear();
            if (_commandPool.Handle != 0)
                _vk.DestroyCommandPool(_device, _commandPool, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_debugUtils != null && _debugMessenger.Handle != 0 && _instance.Handle != 0)
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        if (_validationCallbackHandle.IsAllocated)
            _validationCallbackHandle.Free();
        if (_vk != null && _instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        (_debugUtils as IDisposable)?.Dispose();
        (_accelerationStructure as IDisposable)?.Dispose();
        (_vk as IDisposable)?.Dispose();
        _device = default;
        _instance = default;
    }

    private void Initialize()
    {
        _vk = Vk.GetApi();
        CreateInstance();
        CreateDebugMessenger();
        SelectPhysicalDevice();
        CreateDevice();
        CreateCommandPool();
    }

    private void CreateInstance()
    {
        RequireInstanceLayer(ValidationLayer);
        RequireInstanceExtension(DebugUtilsExtension);
        _validationCallbackHandle = GCHandle.Alloc(_validationMessages, GCHandleType.Normal);

        var applicationInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13
        };
        var debugCreateInfo = CreateDebugMessengerInfo();
        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &applicationInfo,
            PNext = &debugCreateInfo,
            EnabledLayerCount = 1,
            EnabledExtensionCount = 1
        };
        nint layerPointer = SilkMarshal.StringArrayToPtr([ValidationLayer]);
        nint extensionPointer = SilkMarshal.StringArrayToPtr([DebugUtilsExtension]);
        try
        {
            createInfo.PpEnabledLayerNames = (byte**)layerPointer;
            createInfo.PpEnabledExtensionNames = (byte**)extensionPointer;
            RequireSuccess(
                _vk.CreateInstance(&createInfo, null, out _instance),
                "create the alpha-visibility Vulkan 1.3 instance");
        }
        finally
        {
            SilkMarshal.Free(layerPointer);
            SilkMarshal.Free(extensionPointer);
        }

        if (!_vk.TryGetInstanceExtension(_instance, out _debugUtils))
            throw new InvalidOperationException("VK_EXT_debug_utils was enabled but could not be loaded.");
    }

    private void CreateDebugMessenger()
    {
        DebugUtilsMessengerCreateInfoEXT info = CreateDebugMessengerInfo();
        RequireSuccess(
            _debugUtils!.CreateDebugUtilsMessenger(
                _instance,
                &info,
                null,
                out _debugMessenger),
            "create the alpha-visibility validation messenger");
    }

    private DebugUtilsMessengerCreateInfoEXT CreateDebugMessengerInfo()
    {
        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity =
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType =
                DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&DebugCallback),
            PUserData = (void*)GCHandle.ToIntPtr(_validationCallbackHandle)
        };
    }

    private void SelectPhysicalDevice()
    {
        uint count = 0;
        RequireSuccess(
            _vk.EnumeratePhysicalDevices(_instance, &count, null),
            "enumerate Vulkan devices");
        if (count == 0)
            throw new InvalidOperationException("No Vulkan physical device is available.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
        {
            RequireSuccess(
                _vk.EnumeratePhysicalDevices(_instance, &count, pointer),
                "enumerate Vulkan devices");
        }

        int bestScore = int.MinValue;
        string rejection = "no compatible Vulkan 1.3 graphics/compute ray-query device";
        foreach (PhysicalDevice candidate in devices)
        {
            var properties = new PhysicalDeviceProperties();
            _vk.GetPhysicalDeviceProperties(candidate, &properties);
            string name = SilkMarshal.PtrToString((nint)properties.DeviceName) ??
                "unnamed Vulkan device";
            if (properties.ApiVersion < Vk.Version13)
            {
                rejection = $"{name} exposes less than Vulkan 1.3";
                continue;
            }
            HashSet<string> extensions = EnumerateDeviceExtensions(candidate);
            string? missingExtension = RequiredDeviceExtensions.FirstOrDefault(
                extension => !extensions.Contains(extension));
            if (missingExtension is not null)
            {
                rejection = $"{name} lacks {missingExtension}";
                continue;
            }
            if (!SupportsRequiredFeatures(candidate))
            {
                rejection =
                    $"{name} lacks buffer-device-address, dynamic-rendering, " +
                    "synchronization2, shader-demote, fragment-store, " +
                    "acceleration-structure, or ray-query features";
                continue;
            }
            uint queueFamily = FindGraphicsComputeQueue(candidate);
            if (queueFamily == uint.MaxValue)
            {
                rejection = $"{name} lacks a graphics+compute queue";
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
            if (score <= bestScore)
                continue;

            bestScore = score;
            _physicalDevice = candidate;
            _queueFamilyIndex = queueFamily;
            _vk.GetPhysicalDeviceMemoryProperties(candidate, out _memoryProperties);
        }
        if (_physicalDevice.Handle == 0)
            throw new InvalidOperationException($"No alpha-visibility conformance device is available: {rejection}.");

        var accelerationProperties = new PhysicalDeviceAccelerationStructurePropertiesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &accelerationProperties
        };
        _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        _scratchAlignment = Math.Max(
            accelerationProperties.MinAccelerationStructureScratchOffsetAlignment,
            1u);
    }

    private bool SupportsRequiredFeatures(PhysicalDevice device)
    {
        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features
        };
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            PNext = &vulkan13
        };
        var acceleration = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &rayQuery
        };
        var bufferAddress = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = &acceleration
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &bufferAddress
        };
        _vk.GetPhysicalDeviceFeatures2(device, &features);
        return bufferAddress.BufferDeviceAddress &&
            acceleration.AccelerationStructure &&
            rayQuery.RayQuery &&
            vulkan13.DynamicRendering &&
            vulkan13.Synchronization2 &&
            vulkan13.ShaderDemoteToHelperInvocation &&
            features.Features.FragmentStoresAndAtomics;
    }

    private void CreateDevice()
    {
        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamilyIndex,
            QueueCount = 1,
            PQueuePriorities = &priority
        };
        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            ShaderDemoteToHelperInvocation = true
        };
        var rayQuery = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            RayQuery = true,
            PNext = &vulkan13
        };
        var acceleration = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            AccelerationStructure = true,
            PNext = &rayQuery
        };
        var bufferAddress = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            BufferDeviceAddress = true,
            PNext = &acceleration
        };
        var coreFeatures = new PhysicalDeviceFeatures
        {
            FragmentStoresAndAtomics = true
        };
        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &bufferAddress,
            PEnabledFeatures = &coreFeatures,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo
        };
        nint extensionPointer =
            SilkMarshal.StringArrayToPtr(RequiredDeviceExtensions);
        try
        {
            createInfo.EnabledExtensionCount =
                checked((uint)RequiredDeviceExtensions.Length);
            createInfo.PpEnabledExtensionNames = (byte**)extensionPointer;
            RequireSuccess(
                _vk.CreateDevice(_physicalDevice, &createInfo, null, out _device),
                "create the alpha-visibility Vulkan device");
        }
        finally
        {
            SilkMarshal.Free(extensionPointer);
        }
        _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
        if (!_vk.TryGetDeviceExtension(
                _instance,
                _device,
                out _accelerationStructure))
        {
            throw new InvalidOperationException(
                "VK_KHR_acceleration_structure was enabled but could not be loaded.");
        }
    }

    private void CreateCommandPool()
    {
        var info = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamilyIndex,
            Flags = CommandPoolCreateFlags.TransientBit
        };
        RequireSuccess(
            _vk.CreateCommandPool(_device, &info, null, out _commandPool),
            "create the alpha-visibility command pool");
    }

    private AlphaVisibilityHardwareOutput Execute()
    {
        AlphaVisibilityTextureData texture =
            AlphaVisibilityConformanceContract.CreateTextureData();
        OwnedBuffer textureStaging = CreateBuffer(
            checked((ulong)texture.Pixels.LongLength),
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit,
            requireDeviceAddress: false);
        WriteBuffer(textureStaging, texture.Pixels);

        CreateTexture();
        OwnedBuffer positions = CreateInitializedBuffer(
            new Position[]
            {
                new(-1.0f, -1.0f, 0.0f, 0.0f),
                new( 1.0f, -1.0f, 0.0f, 0.0f),
                new( 1.0f,  1.0f, 0.0f, 0.0f),
                new(-1.0f,  1.0f, 0.0f, 0.0f)
            },
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit,
            requireDeviceAddress: true);
        OwnedBuffer texCoords = CreateInitializedBuffer(
            new TexCoord[]
            {
                new(0.0f, 0.0f),
                new(1.0f, 0.0f),
                new(1.0f, 1.0f),
                new(0.0f, 1.0f)
            },
            BufferUsageFlags.StorageBufferBit,
            requireDeviceAddress: false);
        OwnedBuffer indices = CreateInitializedBuffer(
            new uint[] { 0, 1, 2, 0, 2, 3 },
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit |
            BufferUsageFlags.StorageBufferBit,
            requireDeviceAddress: true);
        OwnedBuffer results = CreateBuffer(
            checked((ulong)AlphaVisibilityConformanceContract.ResultWordCount * sizeof(uint)),
            BufferUsageFlags.StorageBufferBit,
            MemoryPropertyFlags.HostVisibleBit,
            requireDeviceAddress: false);
        ClearBuffer(results);

        AccelerationBuild blasBuild =
            CreateBottomLevelAccelerationStructure(positions, indices);
        ulong blasAddress = GetAccelerationStructureAddress(_blas);
        AccelerationStructureInstanceKHR instance =
            AccelerationStructureManager.CreateInstance(
                Matrix4x4.Identity,
                blasAddress,
                instanceCustomIndex: 0,
                mask: 0xff,
                GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr);
        OwnedBuffer instances = CreateInitializedBuffer(
            MemoryMarshal.CreateReadOnlySpan(ref instance, 1),
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit,
            requireDeviceAddress: true);
        AccelerationBuild tlasBuild =
            CreateTopLevelAccelerationStructure(instances);

        CreateDescriptorResources(results, texCoords, indices);
        CreatePipelines();
        CommandBuffer commandBuffer = AllocateCommandBuffer();
        RecordCommands(
            commandBuffer,
            texture,
            textureStaging,
            blasBuild,
            tlasBuild);
        SubmitAndWait(commandBuffer);

        uint[] words =
            ReadBuffer<uint>(
                results,
                AlphaVisibilityConformanceContract.ResultWordCount);
        AlphaVisibilityValidationMessageSnapshot validation =
            _validationMessages.Snapshot();
        var properties = new PhysicalDeviceProperties();
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);
        string deviceName =
            SilkMarshal.PtrToString((nint)properties.DeviceName) ??
            "unnamed Vulkan device";
        return new AlphaVisibilityHardwareOutput(
            deviceName,
            properties.ApiVersion,
            properties.DriverVersion,
            ValidationEnabled: true,
            validation.WarningCount,
            validation.ErrorCount,
            validation.FirstErrorMessage,
            validation.Messages,
            validation.MessagesTruncated,
            words);
    }

    private void CreateTexture()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D(
                AlphaVisibilityConformanceContract.TextureWidth,
                AlphaVisibilityConformanceContract.TextureHeight,
                1),
            MipLevels = AlphaVisibilityConformanceContract.TextureMipLevelCount,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        RequireSuccess(
            _vk.CreateImage(_device, &imageInfo, null, out _textureImage),
            "create the alpha-visibility texture");
        _vk.GetImageMemoryRequirements(
            _device,
            _textureImage,
            out MemoryRequirements requirements);
        uint memoryType = FindMemoryType(
            requirements.MemoryTypeBits,
            MemoryPropertyFlags.DeviceLocalBit,
            MemoryPropertyFlags.DeviceLocalBit);
        var allocationInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryType
        };
        RequireSuccess(
            _vk.AllocateMemory(_device, &allocationInfo, null, out _textureMemory),
            "allocate the alpha-visibility texture");
        RequireSuccess(
            _vk.BindImageMemory(_device, _textureImage, _textureMemory, 0),
            "bind the alpha-visibility texture");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _textureImage,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                AlphaVisibilityConformanceContract.TextureMipLevelCount,
                0,
                1)
        };
        RequireSuccess(
            _vk.CreateImageView(_device, &viewInfo, null, out _textureView),
            "create the alpha-visibility texture view");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0.0f,
            MaxLod = AlphaVisibilityConformanceContract.TextureMipLevelCount - 1,
            MaxAnisotropy = 1.0f
        };
        RequireSuccess(
            _vk.CreateSampler(_device, &samplerInfo, null, out _textureSampler),
            "create the alpha-visibility texture sampler");
    }

    private AccelerationBuild CreateBottomLevelAccelerationStructure(
        OwnedBuffer positions,
        OwnedBuffer indices)
    {
        var triangles = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = Format.R32G32B32Sfloat,
            VertexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = GetBufferAddress(positions)
            },
            VertexStride = checked((ulong)Marshal.SizeOf<Position>()),
            MaxVertex = 3,
            IndexType = IndexType.Uint32,
            IndexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = GetBufferAddress(indices)
            }
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Triangles = triangles
            }
        };
        AccelerationStructureBuildGeometryInfoKHR buildInfo =
            CreateBuildInfo(
                AccelerationStructureTypeKHR.BottomLevelKhr,
                &geometry);
        AccelerationStructureBuildSizesInfoKHR sizes =
            QueryBuildSizes(buildInfo, primitiveCount: 2);
        OwnedBuffer storage = CreateBuffer(
            sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit,
            requireDeviceAddress: true);
        _blas = CreateAccelerationStructure(
            storage,
            sizes.AccelerationStructureSize,
            AccelerationStructureTypeKHR.BottomLevelKhr);
        OwnedBuffer scratch = CreateScratchBuffer(sizes.BuildScratchSize);
        ulong scratchAddress = AlignUp(
            GetBufferAddress(scratch),
            _scratchAlignment);
        geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.TrianglesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Triangles = triangles
            }
        };
        buildInfo = CreateBuildInfo(
            AccelerationStructureTypeKHR.BottomLevelKhr,
            &geometry,
            _blas,
            scratchAddress);
        return new AccelerationBuild(geometry, buildInfo, PrimitiveCount: 2);
    }

    private AccelerationBuild CreateTopLevelAccelerationStructure(
        OwnedBuffer instances)
    {
        var instanceData = new AccelerationStructureGeometryInstancesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
            ArrayOfPointers = false,
            Data = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = GetBufferAddress(instances)
            }
        };
        var geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = instanceData
            }
        };
        AccelerationStructureBuildGeometryInfoKHR buildInfo =
            CreateBuildInfo(
                AccelerationStructureTypeKHR.TopLevelKhr,
                &geometry);
        AccelerationStructureBuildSizesInfoKHR sizes =
            QueryBuildSizes(buildInfo, primitiveCount: 1);
        OwnedBuffer storage = CreateBuffer(
            sizes.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr |
            BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit,
            requireDeviceAddress: true);
        _tlas = CreateAccelerationStructure(
            storage,
            sizes.AccelerationStructureSize,
            AccelerationStructureTypeKHR.TopLevelKhr);
        OwnedBuffer scratch = CreateScratchBuffer(sizes.BuildScratchSize);
        ulong scratchAddress = AlignUp(
            GetBufferAddress(scratch),
            _scratchAlignment);
        geometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = instanceData
            }
        };
        buildInfo = CreateBuildInfo(
            AccelerationStructureTypeKHR.TopLevelKhr,
            &geometry,
            _tlas,
            scratchAddress);
        return new AccelerationBuild(geometry, buildInfo, PrimitiveCount: 1);
    }

    private static AccelerationStructureBuildGeometryInfoKHR CreateBuildInfo(
        AccelerationStructureTypeKHR type,
        AccelerationStructureGeometryKHR* geometry,
        AccelerationStructureKHR destination = default,
        ulong scratchAddress = 0)
    {
        return new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = type,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
            Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            DstAccelerationStructure = destination,
            GeometryCount = 1,
            PGeometries = geometry,
            ScratchData = new DeviceOrHostAddressKHR
            {
                DeviceAddress = scratchAddress
            }
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
        _accelerationStructure!.GetAccelerationStructureBuildSizes(
            _device,
            AccelerationStructureBuildTypeKHR.DeviceKhr,
            &buildInfo,
            &primitiveCount,
            &sizes);
        if (sizes.AccelerationStructureSize == 0 || sizes.BuildScratchSize == 0)
            throw new InvalidOperationException("Vulkan returned empty acceleration-structure build sizes.");
        return sizes;
    }

    private AccelerationStructureKHR CreateAccelerationStructure(
        OwnedBuffer storage,
        ulong size,
        AccelerationStructureTypeKHR type)
    {
        var info = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = storage.Buffer,
            Size = size,
            Type = type
        };
        RequireSuccess(
            _accelerationStructure!.CreateAccelerationStructure(
                _device,
                &info,
                null,
                out AccelerationStructureKHR result),
            $"create the {type} acceleration structure");
        return result;
    }

    private OwnedBuffer CreateScratchBuffer(ulong requiredBytes)
    {
        return CreateBuffer(
            checked(requiredBytes + _scratchAlignment),
            BufferUsageFlags.StorageBufferBit |
            BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit,
            requireDeviceAddress: true);
    }

    private ulong GetAccelerationStructureAddress(
        AccelerationStructureKHR accelerationStructure)
    {
        var info = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = accelerationStructure
        };
        ulong address =
            _accelerationStructure!.GetAccelerationStructureDeviceAddress(
                _device,
                &info);
        if (address == 0)
            throw new InvalidOperationException("Vulkan returned an empty acceleration-structure address.");
        return address;
    }

    private void CreateDescriptorResources(
        OwnedBuffer results,
        OwnedBuffer texCoords,
        OwnedBuffer indices)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[(int)DescriptorBindingCount];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit
        };
        bindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        bindings[3] = new DescriptorSetLayoutBinding
        {
            Binding = 3,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        bindings[4] = new DescriptorSetLayoutBinding
        {
            Binding = 4,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = DescriptorBindingCount,
            PBindings = bindings
        };
        RequireSuccess(
            _vk.CreateDescriptorSetLayout(
                _device,
                &layoutInfo,
                null,
                out _descriptorSetLayout),
            "create the alpha-visibility descriptor layout");

        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize(
            DescriptorType.CombinedImageSampler,
            1);
        poolSizes[1] = new DescriptorPoolSize(
            DescriptorType.StorageBuffer,
            3);
        poolSizes[2] = new DescriptorPoolSize(
            DescriptorType.AccelerationStructureKhr,
            1);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes
        };
        RequireSuccess(
            _vk.CreateDescriptorPool(
                _device,
                &poolInfo,
                null,
                out _descriptorPool),
            "create the alpha-visibility descriptor pool");
        DescriptorSetLayout layout = _descriptorSetLayout;
        var allocateInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        RequireSuccess(
            _vk.AllocateDescriptorSets(
                _device,
                &allocateInfo,
                out _descriptorSet),
            "allocate the alpha-visibility descriptor set");

        var imageInfo = new DescriptorImageInfo(
            _textureSampler,
            _textureView,
            ImageLayout.ShaderReadOnlyOptimal);
        var resultInfo = new DescriptorBufferInfo(
            results.Buffer,
            0,
            results.Size);
        var texCoordInfo = new DescriptorBufferInfo(
            texCoords.Buffer,
            0,
            texCoords.Size);
        var indexInfo = new DescriptorBufferInfo(
            indices.Buffer,
            0,
            indices.Size);
        AccelerationStructureKHR tlas = _tlas;
        var accelerationInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &tlas
        };
        var writes = stackalloc WriteDescriptorSet[(int)DescriptorBindingCount];
        writes[0] = CreateDescriptorWrite(
            0,
            DescriptorType.CombinedImageSampler);
        writes[0].PImageInfo = &imageInfo;
        writes[1] = CreateDescriptorWrite(1, DescriptorType.StorageBuffer);
        writes[1].PBufferInfo = &resultInfo;
        writes[2] = CreateDescriptorWrite(
            2,
            DescriptorType.AccelerationStructureKhr);
        writes[2].PNext = &accelerationInfo;
        writes[3] = CreateDescriptorWrite(3, DescriptorType.StorageBuffer);
        writes[3].PBufferInfo = &texCoordInfo;
        writes[4] = CreateDescriptorWrite(4, DescriptorType.StorageBuffer);
        writes[4].PBufferInfo = &indexInfo;
        _vk.UpdateDescriptorSets(
            _device,
            DescriptorBindingCount,
            writes,
            0,
            null);

        var pushConstant = new PushConstantRange
        {
            StageFlags = ConformanceShaderStages,
            Offset = 0,
            Size = checked((uint)Marshal.SizeOf<AlphaVisibilityPushConstants>())
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &layout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstant
        };
        RequireSuccess(
            _vk.CreatePipelineLayout(
                _device,
                &pipelineLayoutInfo,
                null,
                out _pipelineLayout),
            "create the alpha-visibility pipeline layout");
    }

    private WriteDescriptorSet CreateDescriptorWrite(
        uint binding,
        DescriptorType descriptorType)
    {
        return new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = binding,
            DescriptorCount = 1,
            DescriptorType = descriptorType
        };
    }

    private void CreatePipelines()
    {
        ShaderModule vertex = CreateShaderModule(
            AlphaVisibilityConformanceContract.VertexShaderResourceName);
        ShaderModule fragment = CreateShaderModule(
            AlphaVisibilityConformanceContract.FragmentShaderResourceName);
        ShaderModule compute = CreateShaderModule(
            AlphaVisibilityConformanceContract.RayQueryShaderResourceName);
        nint entryPoint = SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = (byte*)entryPoint
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = (byte*)entryPoint
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };
            var viewport = new Viewport(
                0,
                0,
                AlphaVisibilityConformanceContract.Width,
                AlphaVisibilityConformanceContract.Height,
                0,
                1);
            var scissor = new Rect2D(
                new Offset2D(0, 0),
                new Extent2D(
                    AlphaVisibilityConformanceContract.Width,
                    AlphaVisibilityConformanceContract.Height));
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor
            };
            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f
            };
            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 0,
                PAttachments = null
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 0
            };
            var graphicsInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PColorBlendState = &colorBlend,
                Layout = _pipelineLayout
            };
            RequireSuccess(
                _vk.CreateGraphicsPipelines(
                    _device,
                    default,
                    1,
                    &graphicsInfo,
                    null,
                    out _graphicsPipeline),
                "create the alpha-visibility raster pipeline");

            var computeStage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = compute,
                PName = (byte*)entryPoint
            };
            var computeInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = computeStage,
                Layout = _pipelineLayout
            };
            RequireSuccess(
                _vk.CreateComputePipelines(
                    _device,
                    default,
                    1,
                    &computeInfo,
                    null,
                    out _computePipeline),
                "create the alpha-visibility ray-query pipeline");
        }
        finally
        {
            SilkMarshal.Free(entryPoint);
            _vk.DestroyShaderModule(_device, vertex, null);
            _vk.DestroyShaderModule(_device, fragment, null);
            _vk.DestroyShaderModule(_device, compute, null);
        }
    }

    private ShaderModule CreateShaderModule(string resourceName)
    {
        byte[] spirv =
            AlphaVisibilityConformanceContract.LoadShaderBytes(resourceName);
        fixed (byte* pointer = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = checked((nuint)spirv.Length),
                PCode = (uint*)pointer
            };
            RequireSuccess(
                _vk.CreateShaderModule(
                    _device,
                    &info,
                    null,
                    out ShaderModule module),
                $"create shader module '{resourceName}'");
            return module;
        }
    }

    private CommandBuffer AllocateCommandBuffer()
    {
        var info = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        RequireSuccess(
            _vk.AllocateCommandBuffers(_device, &info, out CommandBuffer commandBuffer),
            "allocate the alpha-visibility command buffer");
        return commandBuffer;
    }

    private void RecordCommands(
        CommandBuffer commandBuffer,
        AlphaVisibilityTextureData texture,
        OwnedBuffer textureStaging,
        AccelerationBuild blasBuild,
        AccelerationBuild tlasBuild)
    {
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        RequireSuccess(
            _vk.BeginCommandBuffer(commandBuffer, &begin),
            "begin the alpha-visibility command buffer");
        RecordTextureUpload(commandBuffer, texture, textureStaging);
        RecordAccelerationBuild(commandBuffer, blasBuild);
        RecordAccelerationBuildBarrier(
            commandBuffer,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            AccessFlags2.AccelerationStructureWriteBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr);
        RecordAccelerationBuild(commandBuffer, tlasBuild);
        RecordAccelerationBuildBarrier(
            commandBuffer,
            PipelineStageFlags2.AccelerationStructureBuildBitKhr,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.AccelerationStructureWriteBitKhr,
            AccessFlags2.AccelerationStructureReadBitKhr);

        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(
                new Offset2D(0, 0),
                new Extent2D(
                    AlphaVisibilityConformanceContract.Width,
                    AlphaVisibilityConformanceContract.Height)),
            LayerCount = 1
        };
        _vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        _vk.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Graphics,
            _graphicsPipeline);
        DescriptorSet descriptorSet = _descriptorSet;
        _vk.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Graphics,
            _pipelineLayout,
            0,
            1,
            &descriptorSet,
            0,
            null);
        for (int distanceIndex = 0;
             distanceIndex < AlphaVisibilityConformanceContract.Distances.Count;
             distanceIndex++)
        {
            AlphaVisibilityPushConstants push =
                CreatePushConstants(distanceIndex);
            _vk.CmdPushConstants(
                commandBuffer,
                _pipelineLayout,
                ConformanceShaderStages,
                0,
                checked((uint)Marshal.SizeOf<AlphaVisibilityPushConstants>()),
                &push);
            _vk.CmdDraw(commandBuffer, 6, 1, 0, 0);
        }
        _vk.CmdEndRendering(commandBuffer);

        _vk.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Compute,
            _computePipeline);
        _vk.CmdBindDescriptorSets(
            commandBuffer,
            PipelineBindPoint.Compute,
            _pipelineLayout,
            0,
            1,
            &descriptorSet,
            0,
            null);
        uint groupCountX =
            (AlphaVisibilityConformanceContract.Width + ComputeLocalSize - 1) /
            ComputeLocalSize;
        uint groupCountY =
            (AlphaVisibilityConformanceContract.Height + ComputeLocalSize - 1) /
            ComputeLocalSize;
        for (int distanceIndex = 0;
             distanceIndex < AlphaVisibilityConformanceContract.Distances.Count;
             distanceIndex++)
        {
            AlphaVisibilityPushConstants push =
                CreatePushConstants(distanceIndex);
            _vk.CmdPushConstants(
                commandBuffer,
                _pipelineLayout,
                ConformanceShaderStages,
                0,
                checked((uint)Marshal.SizeOf<AlphaVisibilityPushConstants>()),
                &push);
            _vk.CmdDispatch(commandBuffer, groupCountX, groupCountY, 1);
        }

        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask =
                PipelineStageFlags2.FragmentShaderBit |
                PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memoryBarrier
        };
        _vk.CmdPipelineBarrier2(commandBuffer, &dependency);
        RequireSuccess(
            _vk.EndCommandBuffer(commandBuffer),
            "end the alpha-visibility command buffer");
    }

    private static AlphaVisibilityPushConstants CreatePushConstants(
        int distanceIndex)
    {
        return new AlphaVisibilityPushConstants
        {
            DistanceIndex = checked((uint)distanceIndex),
            Width = AlphaVisibilityConformanceContract.Width,
            Height = AlphaVisibilityConformanceContract.Height,
            Distance =
                AlphaVisibilityConformanceContract.Distances[distanceIndex],
            RayTextureLod =
                AlphaVisibilityConformanceContract.RayTextureLod,
            AlphaCutoff =
                AlphaVisibilityConformanceContract.AlphaCutoff,
            SampleCount =
                checked((uint)AlphaVisibilityConformanceContract.SamplesPerDistance),
            DistanceCount =
                checked((uint)AlphaVisibilityConformanceContract.Distances.Count)
        };
    }

    private void RecordTextureUpload(
        CommandBuffer commandBuffer,
        AlphaVisibilityTextureData texture,
        OwnedBuffer staging)
    {
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _textureImage,
            SubresourceRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                AlphaVisibilityConformanceContract.TextureMipLevelCount,
                0,
                1)
        };
        _vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        var copies =
            stackalloc BufferImageCopy[AlphaVisibilityConformanceContract.TextureMipLevelCount];
        for (int index = 0; index < texture.MipLevels.Count; index++)
        {
            AlphaVisibilityTextureMip mip = texture.MipLevels[index];
            copies[index] = new BufferImageCopy
            {
                BufferOffset = checked((ulong)mip.ByteOffset),
                ImageSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    checked((uint)mip.Level),
                    0,
                    1),
                ImageExtent = new Extent3D(
                    checked((uint)mip.Width),
                    checked((uint)mip.Height),
                    1)
            };
        }
        _vk.CmdCopyBufferToImage(
            commandBuffer,
            staging.Buffer,
            _textureImage,
            ImageLayout.TransferDstOptimal,
            checked((uint)texture.MipLevels.Count),
            copies);
        var toShaderRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _textureImage,
            SubresourceRange = new ImageSubresourceRange(
                ImageAspectFlags.ColorBit,
                0,
                AlphaVisibilityConformanceContract.TextureMipLevelCount,
                0,
                1)
        };
        _vk.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit |
            PipelineStageFlags.ComputeShaderBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toShaderRead);
    }

    private void RecordAccelerationBuild(
        CommandBuffer commandBuffer,
        AccelerationBuild build)
    {
        AccelerationStructureGeometryKHR geometry = build.Geometry;
        AccelerationStructureBuildGeometryInfoKHR buildInfo = build.BuildInfo;
        buildInfo.PGeometries = &geometry;
        var range = new AccelerationStructureBuildRangeInfoKHR
        {
            PrimitiveCount = build.PrimitiveCount
        };
        AccelerationStructureBuildRangeInfoKHR* rangePointer = &range;
        _accelerationStructure!.CmdBuildAccelerationStructures(
            commandBuffer,
            1,
            &buildInfo,
            &rangePointer);
    }

    private void RecordAccelerationBuildBarrier(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 sourceStage,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 sourceAccess,
        AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier
        };
        _vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void SubmitAndWait(CommandBuffer commandBuffer)
    {
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer
        };
        RequireSuccess(
            _vk.QueueSubmit(_queue, 1, &submit, default),
            "submit alpha-visibility conformance work");
        RequireSuccess(
            _vk.QueueWaitIdle(_queue),
            "wait for alpha-visibility conformance work");
    }

    private OwnedBuffer CreateInitializedBuffer<T>(
        ReadOnlySpan<T> values,
        BufferUsageFlags usage,
        bool requireDeviceAddress)
        where T : unmanaged
    {
        if (values.IsEmpty)
            throw new ArgumentException("Initialized Vulkan buffers cannot be empty.", nameof(values));
        ulong size = checked((ulong)values.Length * (ulong)sizeof(T));
        OwnedBuffer buffer = CreateBuffer(
            size,
            usage,
            MemoryPropertyFlags.HostVisibleBit,
            requireDeviceAddress);
        WriteBuffer(buffer, MemoryMarshal.AsBytes(values));
        return buffer;
    }

    private OwnedBuffer CreateBuffer(
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags requiredProperties,
        bool requireDeviceAddress)
    {
        if (size == 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        RequireSuccess(
            _vk.CreateBuffer(_device, &info, null, out VkBuffer buffer),
            "create an alpha-visibility buffer");
        _vk.GetBufferMemoryRequirements(
            _device,
            buffer,
            out MemoryRequirements requirements);
        MemoryPropertyFlags preferred =
            requiredProperties |
            ((requiredProperties & MemoryPropertyFlags.HostVisibleBit) != 0
                ? MemoryPropertyFlags.HostCoherentBit
                : MemoryPropertyFlags.DeviceLocalBit);
        uint memoryType = FindMemoryType(
            requirements.MemoryTypeBits,
            requiredProperties,
            preferred);
        MemoryPropertyFlags actualProperties =
            _memoryProperties.MemoryTypes[(int)memoryType].PropertyFlags;
        var addressFlags = new MemoryAllocateFlagsInfo
        {
            SType = StructureType.MemoryAllocateFlagsInfo,
            Flags = MemoryAllocateFlags.DeviceAddressBit
        };
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = requireDeviceAddress ? &addressFlags : null,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = memoryType
        };
        DeviceMemory memory = default;
        try
        {
            RequireSuccess(
                _vk.AllocateMemory(_device, &allocation, null, out memory),
                "allocate an alpha-visibility buffer");
            RequireSuccess(
                _vk.BindBufferMemory(_device, buffer, memory, 0),
                "bind an alpha-visibility buffer");
            void* mapped = null;
            if ((actualProperties & MemoryPropertyFlags.HostVisibleBit) != 0)
            {
                RequireSuccess(
                    _vk.MapMemory(_device, memory, 0, size, 0, &mapped),
                    "map an alpha-visibility buffer");
            }
            var owned = new OwnedBuffer(
                buffer,
                memory,
                size,
                mapped,
                actualProperties);
            _buffers.Add(owned);
            return owned;
        }
        catch
        {
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
            _vk.DestroyBuffer(_device, buffer, null);
            throw;
        }
    }

    private void DestroyBuffer(OwnedBuffer buffer)
    {
        if (buffer.Mapped != null)
            _vk.UnmapMemory(_device, buffer.Memory);
        if (buffer.Buffer.Handle != 0)
            _vk.DestroyBuffer(_device, buffer.Buffer, null);
        if (buffer.Memory.Handle != 0)
            _vk.FreeMemory(_device, buffer.Memory, null);
    }

    private void WriteBuffer(OwnedBuffer buffer, ReadOnlySpan<byte> bytes)
    {
        if (buffer.Mapped == null)
            throw new InvalidOperationException("Cannot write an unmapped Vulkan buffer.");
        if ((ulong)bytes.Length > buffer.Size)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Buffer write exceeds its allocation.");
        fixed (byte* source = bytes)
            System.Buffer.MemoryCopy(
                source,
                buffer.Mapped,
                checked((long)buffer.Size),
                bytes.Length);
        FlushIfNeeded(buffer);
    }

    private void ClearBuffer(OwnedBuffer buffer)
    {
        if (buffer.Mapped == null)
            throw new InvalidOperationException("Cannot clear an unmapped Vulkan buffer.");
        NativeMemory.Clear(buffer.Mapped, checked((nuint)buffer.Size));
        FlushIfNeeded(buffer);
    }

    private T[] ReadBuffer<T>(OwnedBuffer buffer, int count)
        where T : unmanaged
    {
        if (buffer.Mapped == null)
            throw new InvalidOperationException("Cannot read an unmapped Vulkan buffer.");
        ulong bytes = checked((ulong)count * (ulong)sizeof(T));
        if (bytes > buffer.Size)
            throw new ArgumentOutOfRangeException(nameof(count));
        if ((buffer.MemoryProperties & MemoryPropertyFlags.HostCoherentBit) == 0)
        {
            var range = new MappedMemoryRange
            {
                SType = StructureType.MappedMemoryRange,
                Memory = buffer.Memory,
                Offset = 0,
                Size = Vk.WholeSize
            };
            RequireSuccess(
                _vk.InvalidateMappedMemoryRanges(_device, 1, &range),
                "invalidate alpha-visibility readback memory");
        }
        var output = new T[count];
        fixed (T* destination = output)
            System.Buffer.MemoryCopy(
                buffer.Mapped,
                destination,
                checked((long)bytes),
                checked((long)bytes));
        return output;
    }

    private void FlushIfNeeded(OwnedBuffer buffer)
    {
        if ((buffer.MemoryProperties & MemoryPropertyFlags.HostCoherentBit) != 0)
            return;
        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = buffer.Memory,
            Offset = 0,
            Size = Vk.WholeSize
        };
        RequireSuccess(
            _vk.FlushMappedMemoryRanges(_device, 1, &range),
            "flush alpha-visibility upload memory");
    }

    private ulong GetBufferAddress(OwnedBuffer buffer)
    {
        var info = new BufferDeviceAddressInfo
        {
            SType = StructureType.BufferDeviceAddressInfo,
            Buffer = buffer.Buffer
        };
        ulong address = _vk.GetBufferDeviceAddress(_device, &info);
        if (address == 0)
            throw new InvalidOperationException("Vulkan returned an empty buffer device address.");
        return address;
    }

    private uint FindMemoryType(
        uint typeBits,
        MemoryPropertyFlags required,
        MemoryPropertyFlags preferred)
    {
        uint fallback = uint.MaxValue;
        for (uint index = 0; index < _memoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << checked((int)index))) == 0)
                continue;
            MemoryPropertyFlags flags =
                _memoryProperties.MemoryTypes[(int)index].PropertyFlags;
            if ((flags & required) != required)
                continue;
            fallback = fallback == uint.MaxValue ? index : fallback;
            if ((flags & preferred) == preferred)
                return index;
        }
        if (fallback != uint.MaxValue)
            return fallback;
        throw new InvalidOperationException(
            $"No Vulkan memory type satisfies required properties {required}.");
    }

    private HashSet<string> EnumerateDeviceExtensions(PhysicalDevice device)
    {
        uint count = 0;
        RequireSuccess(
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null),
            "enumerate Vulkan device extensions");
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            RequireSuccess(
                _vk.EnumerateDeviceExtensionProperties(
                    device,
                    (byte*)null,
                    &count,
                    pointer),
                "enumerate Vulkan device extensions");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        fixed (ExtensionProperties* pointer = properties)
        {
            for (int index = 0; index < properties.Length; index++)
            {
                names.Add(
                    SilkMarshal.PtrToString(
                        (nint)pointer[index].ExtensionName) ??
                    string.Empty);
            }
        }
        return names;
    }

    private uint FindGraphicsComputeQueue(PhysicalDevice device)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        if (count == 0)
            return uint.MaxValue;
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);
        for (uint index = 0; index < count; index++)
        {
            QueueFamilyProperties family = families[index];
            if (family.QueueCount > 0 &&
                (family.QueueFlags &
                 (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) ==
                (QueueFlags.GraphicsBit | QueueFlags.ComputeBit))
            {
                return index;
            }
        }
        return uint.MaxValue;
    }

    private void RequireInstanceLayer(string required)
    {
        uint count = 0;
        RequireSuccess(
            _vk.EnumerateInstanceLayerProperties(&count, null),
            "enumerate Vulkan instance layers");
        var properties = new LayerProperties[count];
        fixed (LayerProperties* pointer = properties)
        {
            RequireSuccess(
                _vk.EnumerateInstanceLayerProperties(&count, pointer),
                "enumerate Vulkan instance layers");
            bool available = false;
            for (int index = 0; index < properties.Length; index++)
            {
                string name =
                    SilkMarshal.PtrToString((nint)pointer[index].LayerName) ??
                    string.Empty;
                if (string.Equals(name, required, StringComparison.Ordinal))
                {
                    available = true;
                    break;
                }
            }
            if (!available)
                throw new InvalidOperationException($"Required Vulkan layer '{required}' is unavailable.");
        }
    }

    private void RequireInstanceExtension(string required)
    {
        uint count = 0;
        RequireSuccess(
            _vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null),
            "enumerate Vulkan instance extensions");
        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            RequireSuccess(
                _vk.EnumerateInstanceExtensionProperties(
                    (byte*)null,
                    &count,
                    pointer),
                "enumerate Vulkan instance extensions");
            bool available = false;
            for (int index = 0; index < properties.Length; index++)
            {
                string name =
                    SilkMarshal.PtrToString(
                        (nint)pointer[index].ExtensionName) ??
                    string.Empty;
                if (string.Equals(name, required, StringComparison.Ordinal))
                {
                    available = true;
                    break;
                }
            }
            if (!available)
                throw new InvalidOperationException($"Required Vulkan extension '{required}' is unavailable.");
        }
    }

    private static ulong AlignUp(ulong value, uint alignment)
    {
        ulong mask = checked((ulong)alignment - 1UL);
        if (((ulong)alignment & mask) != 0)
            throw new InvalidOperationException("Vulkan scratch alignment is not a power of two.");
        return checked((value + mask) & ~mask);
    }

    private static void RequireSuccess(Result result, string action)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {action}: {result}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData)
    {
        if (userData == null)
            return Vk.False;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr((nint)userData);
            if (handle.Target is AlphaVisibilityValidationMessageState messages)
            {
                string message =
                    callbackData == null
                        ? "Vulkan validation supplied no callback data."
                        : SilkMarshal.PtrToString((nint)callbackData->PMessage) ??
                          "Vulkan validation supplied no message.";
                RendererValidationMessageSeverity mapped =
                    (severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0
                        ? RendererValidationMessageSeverity.Error
                        : RendererValidationMessageSeverity.Warning;
                string messageIdName =
                    callbackData == null
                        ? string.Empty
                        : SilkMarshal.PtrToString(
                            (nint)callbackData->PMessageIdName) ?? string.Empty;
                int messageIdNumber =
                    callbackData == null ? 0 : callbackData->MessageIdNumber;
                messages.Record(
                    mapped,
                    (uint)type,
                    messageIdNumber,
                    messageIdName,
                    message);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Alpha-visibility validation callback failed: {exception.Message}");
        }
        return Vk.False;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct Position(float X, float Y, float Z, float W);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct TexCoord(float X, float Y);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct AlphaVisibilityPushConstants
    {
        public uint DistanceIndex;
        public uint Width;
        public uint Height;
        public float Distance;
        public float RayTextureLod;
        public float AlphaCutoff;
        public uint SampleCount;
        public uint DistanceCount;
    }

    private sealed class VulkanLoaderIsolationScope : IDisposable
    {
        private readonly string _emptyImplicitLayerPath;
        private LoaderEnvironmentVariableScope? _implicitLayerPathScope;
        private LoaderEnvironmentVariableScope? _layersDisableScope;
        private bool _disposed;

        public VulkanLoaderIsolationScope()
        {
            DirectoryInfo emptyDirectory =
                Directory.CreateTempSubdirectory(
                    "njulf-alpha-visibility-vulkan-layers-");
            _emptyImplicitLayerPath = emptyDirectory.FullName;
            try
            {
                // VK_LOADER_LAYERS_DISABLE is the loader's documented way to
                // keep implicit overlays out of a deterministic workload.
                // A broken Windows registry manifest can be opened before its
                // layer name is available to that filter, so override implicit
                // discovery with a unique empty directory as well.
                _implicitLayerPathScope = new LoaderEnvironmentVariableScope(
                    LoaderImplicitLayerPathEnvironmentVariable,
                    _emptyImplicitLayerPath);
                _layersDisableScope = new LoaderEnvironmentVariableScope(
                    LoaderLayersDisableEnvironmentVariable,
                    DisableImplicitLoaderLayersFilter);
            }
            catch
            {
                _layersDisableScope?.Dispose();
                _implicitLayerPathScope?.Dispose();
                DeleteEmptyDirectory();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _layersDisableScope?.Dispose();
            }
            finally
            {
                try
                {
                    _implicitLayerPathScope?.Dispose();
                }
                finally
                {
                    DeleteEmptyDirectory();
                }
            }
        }

        private void DeleteEmptyDirectory()
        {
            if (Directory.Exists(_emptyImplicitLayerPath))
                Directory.Delete(_emptyImplicitLayerPath, recursive: false);
        }
    }

    private sealed class LoaderEnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;
        private bool _disposed;

        public LoaderEnvironmentVariableScope(string name, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _name = name;
            _originalValue =
                Environment.GetEnvironmentVariable(
                    name,
                    EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                name,
                value,
                EnvironmentVariableTarget.Process);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Environment.SetEnvironmentVariable(
                _name,
                _originalValue,
                EnvironmentVariableTarget.Process);
        }
    }

    private sealed class AlphaVisibilityValidationMessageState
    {
        private const int MaximumRetainedMessages = 128;
        private const int MaximumRetainedTextLength = 4096;

        private readonly object _sync = new();
        private readonly List<AlphaVisibilityValidationMessage> _messages = [];
        private int _warningCount;
        private int _errorCount;
        private bool _messagesTruncated;

        public void Record(
            RendererValidationMessageSeverity severity,
            uint messageTypes,
            int messageIdNumber,
            string messageIdName,
            string message)
        {
            lock (_sync)
            {
                switch (severity)
                {
                    case RendererValidationMessageSeverity.Warning:
                        _warningCount = SaturatingIncrement(_warningCount);
                        break;
                    case RendererValidationMessageSeverity.Error:
                        _errorCount = SaturatingIncrement(_errorCount);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(severity),
                            severity,
                            "The alpha-visibility gate records only warning and error callbacks.");
                }

                if (_messages.Count >= MaximumRetainedMessages)
                {
                    _messagesTruncated = true;
                    return;
                }

                string retainedId = Retain(messageIdName, out bool idTruncated);
                string retainedMessage = Retain(message, out bool messageTruncated);
                _messages.Add(new AlphaVisibilityValidationMessage(
                    severity == RendererValidationMessageSeverity.Error
                        ? "Error"
                        : "Warning",
                    messageTypes,
                    messageIdNumber,
                    retainedId,
                    retainedMessage,
                    idTruncated || messageTruncated));
            }
        }

        public AlphaVisibilityValidationMessageSnapshot Snapshot()
        {
            lock (_sync)
            {
                AlphaVisibilityValidationMessage[] messages = [.. _messages];
                string firstError =
                    messages.FirstOrDefault(
                        static message =>
                            string.Equals(
                                message.Severity,
                                "Error",
                                StringComparison.Ordinal))?.Message ??
                    string.Empty;
                return new AlphaVisibilityValidationMessageSnapshot(
                    _warningCount,
                    _errorCount,
                    firstError,
                    messages,
                    _messagesTruncated);
            }
        }

        private static int SaturatingIncrement(int value)
        {
            return value == int.MaxValue ? int.MaxValue : value + 1;
        }

        private static string Retain(string? value, out bool truncated)
        {
            string normalized = value ?? string.Empty;
            truncated = normalized.Length > MaximumRetainedTextLength;
            return truncated
                ? normalized[..MaximumRetainedTextLength]
                : normalized;
        }
    }

    private sealed record AlphaVisibilityValidationMessageSnapshot(
        int WarningCount,
        int ErrorCount,
        string FirstErrorMessage,
        IReadOnlyList<AlphaVisibilityValidationMessage> Messages,
        bool MessagesTruncated);

    private sealed class OwnedBuffer
    {
        public OwnedBuffer(
            VkBuffer buffer,
            DeviceMemory memory,
            ulong size,
            void* mapped,
            MemoryPropertyFlags memoryProperties)
        {
            Buffer = buffer;
            Memory = memory;
            Size = size;
            Mapped = mapped;
            MemoryProperties = memoryProperties;
        }

        public VkBuffer Buffer { get; }
        public DeviceMemory Memory { get; }
        public ulong Size { get; }
        public void* Mapped { get; }
        public MemoryPropertyFlags MemoryProperties { get; }
    }

    private readonly record struct AccelerationBuild(
        AccelerationStructureGeometryKHR Geometry,
        AccelerationStructureBuildGeometryInfoKHR BuildInfo,
        uint PrimitiveCount);
}

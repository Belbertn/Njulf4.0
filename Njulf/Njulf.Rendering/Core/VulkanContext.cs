using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using GpuAllocator = Vma;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Central Vulkan context managing instance, device, queues, and memory allocator.
    /// </summary>
    public unsafe class VulkanContext : IDisposable
    {
        private static readonly string[] ValidationLayers = { "VK_LAYER_KHRONOS_validation" };

        private static readonly string[] RequiredDeviceExtensions =
        {
            "VK_KHR_swapchain",
            "VK_KHR_dynamic_rendering",
            "VK_KHR_synchronization2",
            "VK_EXT_mesh_shader",
            "VK_KHR_buffer_device_address",
            "VK_EXT_descriptor_indexing",
            "VK_KHR_deferred_host_operations"
        };

        private readonly bool _debug;
        private readonly IWindow _window;
        
        private Instance _instance;
        private PhysicalDevice _physicalDevice;
        private Device _device;
        private GpuAllocator.Allocator* _allocator;
        
        private uint _graphicsQueueFamilyIndex;
        private uint _transferQueueFamilyIndex;
        private Queue _graphicsQueue;
        private Queue _transferQueue;
        private bool _hasDedicatedTransferQueue;
        
        private Vk _vk = null!;
        private KhrSurface _khrSurface = null!;
        private KhrSwapchain _khrSwapchain = null!;
        private ExtMeshShader _extMeshShader = null!;
        private KhrDynamicRendering _khrDynamicRendering = null!;
        private KhrSynchronization2 _khrSync2 = null!;
        private KhrDeferredHostOperations? _khrDeferredHostOps;
        private ExtDebugUtils? _extDebugUtils;
        private DebugUtilsMessengerEXT _debugMessenger;
        
        private bool _disposed;
        
        public Vk Api => _vk;
        public Instance Instance => _instance;
        public PhysicalDevice PhysicalDevice => _physicalDevice;
        public Device Device => _device;
        public GpuAllocator.Allocator* Allocator => _allocator;
        public uint GraphicsQueueFamilyIndex => _graphicsQueueFamilyIndex;
        public uint TransferQueueFamilyIndex => _transferQueueFamilyIndex;
        public Queue GraphicsQueue => _graphicsQueue;
        public Queue TransferQueue => _transferQueue;
        public bool HasDedicatedTransferQueue => _hasDedicatedTransferQueue;
        public KhrSurface KhrSurface => _khrSurface;
        public KhrSwapchain KhrSwapchain => _khrSwapchain;
        public ExtMeshShader ExtMeshShader => _extMeshShader;
        public KhrDynamicRendering KhrDynamicRendering => _khrDynamicRendering;
        public KhrSynchronization2 KhrSync2 => _khrSync2;
        public KhrDeferredHostOperations? KhrDeferredHostOps => _khrDeferredHostOps;
        
        public VulkanContext(IWindow window, bool debug = true)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _debug = debug;
            
            CreateInstance();
            PickPhysicalDevice();
            CreateLogicalDevice();
            CreateAllocator();
            LoadExtensions();
            
            if (_debug)
                SetupDebugMessenger();
        }
        
        private void CreateInstance()
        {
            _vk = Vk.GetApi();
            
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)SilkMarshal.StringToPtr("Njulf"),
                ApplicationVersion = Vk.MakeVersion(0, 1, 0),
                PEngineName = (byte*)SilkMarshal.StringToPtr("Njulf"),
                EngineVersion = Vk.MakeVersion(0, 1, 0),
                ApiVersion = Vk.Version13
            };
            
            List<string> instanceExtensions = GetRequiredInstanceExtensions();
            string[] validationLayers = _debug ? ValidationLayers : Array.Empty<string>();
            if (_debug)
                ValidateInstanceLayers(validationLayers);
            
            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledLayerCount = (uint)validationLayers.Length,
                PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers),
                EnabledExtensionCount = (uint)instanceExtensions.Count,
                PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(instanceExtensions.ToArray())
            };
            
            try
            {
                Result result = _vk.CreateInstance(&instanceCreateInfo, null, out _instance);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create Vulkan instance", result);
                
                Console.WriteLine(_debug 
                    ? "Vulkan instance created with validation layers enabled." 
                    : "Vulkan instance created.");
            }
            finally
            {
                if (instanceCreateInfo.PpEnabledLayerNames != null)
                    SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledLayerNames);
                if (instanceCreateInfo.PpEnabledExtensionNames != null)
                    SilkMarshal.Free((nint)instanceCreateInfo.PpEnabledExtensionNames);
                SilkMarshal.Free((nint)appInfo.PApplicationName);
                SilkMarshal.Free((nint)appInfo.PEngineName);
            }
        }
        
        private List<string> GetRequiredInstanceExtensions()
        {
            var extensions = new HashSet<string>(StringComparer.Ordinal);

            var vkSurface = _window.VkSurface;
            if (vkSurface == null)
                throw new VulkanException("The active window backend does not expose a Vulkan surface source.");

            uint surfaceExtensionCount = 0;
            byte** surfaceExtensions = vkSurface.GetRequiredExtensions(out surfaceExtensionCount);
            if (surfaceExtensions == null || surfaceExtensionCount == 0)
                throw new VulkanException("The active window backend did not report any required Vulkan surface extensions.");

            for (uint i = 0; i < surfaceExtensionCount; i++)
            {
                string? extension = SilkMarshal.PtrToString((nint)surfaceExtensions[i]);
                if (!string.IsNullOrWhiteSpace(extension))
                    extensions.Add(extension);
            }

            if (_debug)
                extensions.Add("VK_EXT_debug_utils");

            ValidateInstanceExtensions(extensions);
            return new List<string>(extensions);
        }

        private void ValidateInstanceExtensions(IReadOnlyCollection<string> requiredExtensions)
        {
            uint extensionCount = 0;
            Result result = _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null);
            if (result != Result.Success)
                throw new VulkanException("Failed to enumerate Vulkan instance extensions", result);

            var availableExtensions = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                result = _vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, availableExtensionsPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to enumerate Vulkan instance extensions", result);
            }

            var availableNames = new HashSet<string>(StringComparer.Ordinal);
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                for (uint i = 0; i < extensionCount; i++)
                    availableNames.Add(SilkMarshal.PtrToString((nint)availableExtensionsPtr[i].ExtensionName) ?? string.Empty);
            }

            foreach (string extension in requiredExtensions)
            {
                if (!availableNames.Contains(extension))
                    throw new VulkanException($"Required Vulkan instance extension '{extension}' is not available.");
            }
        }

        private void ValidateInstanceLayers(IReadOnlyCollection<string> requiredLayers)
        {
            uint layerCount = 0;
            Result result = _vk.EnumerateInstanceLayerProperties(&layerCount, null);
            if (result != Result.Success)
                throw new VulkanException("Failed to enumerate Vulkan instance layers", result);

            var availableLayers = new LayerProperties[layerCount];
            fixed (LayerProperties* availableLayersPtr = availableLayers)
            {
                result = _vk.EnumerateInstanceLayerProperties(&layerCount, availableLayersPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to enumerate Vulkan instance layers", result);
            }

            var availableNames = new HashSet<string>(StringComparer.Ordinal);
            fixed (LayerProperties* availableLayersPtr = availableLayers)
            {
                for (uint i = 0; i < layerCount; i++)
                    availableNames.Add(SilkMarshal.PtrToString((nint)availableLayersPtr[i].LayerName) ?? string.Empty);
            }

            foreach (string layer in requiredLayers)
            {
                if (!availableNames.Contains(layer))
                    throw new VulkanException($"Required Vulkan validation layer '{layer}' is not available.");
            }
        }
        
        private void PickPhysicalDevice()
        {
            uint deviceCount = 0;
            Result result = _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);
            if (result != Result.Success || deviceCount == 0)
                throw new VulkanException("No Vulkan physical devices found", result);
            
            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicesPtr = devices)
            {
                result = _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicesPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to enumerate physical devices", result);
            }
            
            PhysicalDevice? selectedDevice = null;
            uint graphicsFamily = uint.MaxValue;
            uint transferFamily = uint.MaxValue;
            
            foreach (var device in devices)
            {
                var properties = new PhysicalDeviceProperties();
                _vk.GetPhysicalDeviceProperties(device, &properties);
                
                if (properties.ApiVersion < Vk.Version13)
                    continue;
                
                if (!TryGetDeviceRequirements(device, out DeviceRequirements requirements))
                    continue;

                if (!requirements.IsSupported)
                    continue;
                
                // Check queue families
                uint queueFamilyCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);
                var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
                fixed (QueueFamilyProperties* familiesPtr = queueFamilies)
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, familiesPtr);
                
                uint graphicsIndex = uint.MaxValue;
                uint transferIndex = uint.MaxValue;
                
                for (uint i = 0; i < queueFamilyCount; i++)
                {
                    if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                    {
                        if (graphicsIndex == uint.MaxValue)
                            graphicsIndex = i;
                        if ((queueFamilies[i].QueueFlags & QueueFlags.TransferBit) != 0 &&
                            transferIndex == uint.MaxValue)
                            transferIndex = i;
                    }
                    else if ((queueFamilies[i].QueueFlags & QueueFlags.TransferBit) != 0 &&
                             transferIndex == uint.MaxValue)
                    {
                        transferIndex = i;
                    }
                }
                
                if (graphicsIndex == uint.MaxValue)
                    continue;
                
                if (transferIndex == uint.MaxValue)
                    transferIndex = graphicsIndex;
                
                // Prefer device with dedicated transfer queue
                if (selectedDevice == null || transferIndex != graphicsIndex)
                {
                    selectedDevice = device;
                    graphicsFamily = graphicsIndex;
                    transferFamily = transferIndex;
                }
            }
            
            if (selectedDevice == null)
                throw new VulkanException(
                    "No suitable Vulkan physical device found. Required: Vulkan 1.3, graphics queue, " +
                    string.Join(", ", RequiredDeviceExtensions) +
                    ", mesh/task shader, descriptor indexing, buffer device address, synchronization2, " +
                    "dynamic rendering, sampler anisotropy, and maintenance4.");
            
            _physicalDevice = selectedDevice.Value;
            _graphicsQueueFamilyIndex = graphicsFamily;
            _transferQueueFamilyIndex = transferFamily;
            _hasDedicatedTransferQueue = graphicsFamily != transferFamily;
            
            Console.WriteLine("Physical device selected with mesh shader support.");
        }
        
        private bool TryGetDeviceRequirements(PhysicalDevice device, out DeviceRequirements requirements)
        {
            requirements = default;
            if (!ValidateDeviceExtensions(device, RequiredDeviceExtensions, out string missingExtension))
            {
                requirements = DeviceRequirements.Missing($"device extension {missingExtension}");
                return true;
            }

            var features2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2
            };
            
            var meshShaderFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt
            };
            
            var dynamicRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
            {
                SType = StructureType.PhysicalDeviceDynamicRenderingFeatures
            };
            
            var sync2Features = new PhysicalDeviceSynchronization2Features
            {
                SType = StructureType.PhysicalDeviceSynchronization2Features
            };
            
            var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
            {
                SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
                BufferDeviceAddress = true
            };

            var maintenance4Features = new PhysicalDeviceMaintenance4Features
            {
                SType = StructureType.PhysicalDeviceMaintenance4Features
            };

            var shaderDemoteFeatures = new PhysicalDeviceShaderDemoteToHelperInvocationFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceShaderDemoteToHelperInvocationFeaturesExt
            };
            
            var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                DescriptorBindingSampledImageUpdateAfterBind = true,
                DescriptorBindingStorageBufferUpdateAfterBind = true,
                DescriptorBindingPartiallyBound = true,
                DescriptorBindingVariableDescriptorCount = true,
                RuntimeDescriptorArray = true,
                ShaderSampledImageArrayNonUniformIndexing = true,
                ShaderStorageBufferArrayNonUniformIndexing = true,
                ShaderUniformBufferArrayNonUniformIndexing = true
            };
            
            // Chain all features
            descriptorIndexingFeatures.PNext = &bufferDeviceAddressFeatures;
            bufferDeviceAddressFeatures.PNext = &sync2Features;
            sync2Features.PNext = &dynamicRenderingFeatures;
            dynamicRenderingFeatures.PNext = &meshShaderFeatures;
            meshShaderFeatures.PNext = &maintenance4Features;
            maintenance4Features.PNext = &shaderDemoteFeatures;
            shaderDemoteFeatures.PNext = features2.PNext;
            features2.PNext = &descriptorIndexingFeatures;
            
            _vk.GetPhysicalDeviceFeatures2(device, &features2);

            List<string> missingFeatures = new();
            if (!meshShaderFeatures.MeshShader)
                missingFeatures.Add("meshShader");
            if (!meshShaderFeatures.TaskShader)
                missingFeatures.Add("taskShader");
            if (!dynamicRenderingFeatures.DynamicRendering)
                missingFeatures.Add("dynamicRendering");
            if (!sync2Features.Synchronization2)
                missingFeatures.Add("synchronization2");
            if (!bufferDeviceAddressFeatures.BufferDeviceAddress)
                missingFeatures.Add("bufferDeviceAddress");
            if (!maintenance4Features.Maintenance4)
                missingFeatures.Add("maintenance4");
            if (!shaderDemoteFeatures.ShaderDemoteToHelperInvocation)
                missingFeatures.Add("shaderDemoteToHelperInvocation");
            if (!features2.Features.SamplerAnisotropy)
                missingFeatures.Add("samplerAnisotropy");
            if (!descriptorIndexingFeatures.DescriptorBindingSampledImageUpdateAfterBind)
                missingFeatures.Add("descriptorBindingSampledImageUpdateAfterBind");
            if (!descriptorIndexingFeatures.DescriptorBindingStorageBufferUpdateAfterBind)
                missingFeatures.Add("descriptorBindingStorageBufferUpdateAfterBind");
            if (!descriptorIndexingFeatures.DescriptorBindingPartiallyBound)
                missingFeatures.Add("descriptorBindingPartiallyBound");
            if (!descriptorIndexingFeatures.DescriptorBindingVariableDescriptorCount)
                missingFeatures.Add("descriptorBindingVariableDescriptorCount");
            if (!descriptorIndexingFeatures.RuntimeDescriptorArray)
                missingFeatures.Add("runtimeDescriptorArray");
            if (!descriptorIndexingFeatures.ShaderSampledImageArrayNonUniformIndexing)
                missingFeatures.Add("shaderSampledImageArrayNonUniformIndexing");
            if (!descriptorIndexingFeatures.ShaderStorageBufferArrayNonUniformIndexing)
                missingFeatures.Add("shaderStorageBufferArrayNonUniformIndexing");

            requirements = missingFeatures.Count == 0
                ? DeviceRequirements.Supported
                : DeviceRequirements.Missing(string.Join(", ", missingFeatures));
            return true;
        }

        private bool ValidateDeviceExtensions(
            PhysicalDevice device,
            IReadOnlyCollection<string> requiredExtensions,
            out string missingExtension)
        {
            missingExtension = string.Empty;
            uint extensionCount = 0;
            Result result = _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);
            if (result != Result.Success)
                throw new VulkanException("Failed to enumerate Vulkan device extensions", result);

            var availableExtensions = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                result = _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, availableExtensionsPtr);
                if (result != Result.Success)
                    throw new VulkanException("Failed to enumerate Vulkan device extensions", result);
            }

            var availableNames = new HashSet<string>(StringComparer.Ordinal);
            fixed (ExtensionProperties* availableExtensionsPtr = availableExtensions)
            {
                for (uint i = 0; i < extensionCount; i++)
                    availableNames.Add(SilkMarshal.PtrToString((nint)availableExtensionsPtr[i].ExtensionName) ?? string.Empty);
            }

            foreach (string extension in requiredExtensions)
            {
                if (!availableNames.Contains(extension))
                {
                    missingExtension = extension;
                    return false;
                }
            }

            return true;
        }
        
        private void CreateLogicalDevice()
        {
            var queuePriorities = stackalloc float[1] { 1.0f };
            
            var graphicsQueueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _graphicsQueueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = queuePriorities
            };
            
            var queueCreateInfos = new List<DeviceQueueCreateInfo> { graphicsQueueInfo };
            
            if (_hasDedicatedTransferQueue)
            {
                var transferQueueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = _transferQueueFamilyIndex,
                    QueueCount = 1,
                    PQueuePriorities = queuePriorities
                };
                queueCreateInfos.Add(transferQueueInfo);
            }
            
            // Device features chain
            var deviceFeatures2 = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                Features = new PhysicalDeviceFeatures
                {
                    VertexPipelineStoresAndAtomics = true,
                    FragmentStoresAndAtomics = true,
                    ShaderStorageImageExtendedFormats = true,
                    SamplerAnisotropy = true
                }
            };
            
            var meshShaderFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
                MeshShader = true,
                TaskShader = true
            };
            
            var dynamicRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
            {
                SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
                DynamicRendering = true
            };
            
            var sync2Features = new PhysicalDeviceSynchronization2Features
            {
                SType = StructureType.PhysicalDeviceSynchronization2Features,
                Synchronization2 = true
            };
            
            var bufferDeviceAddressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
            {
                SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
                BufferDeviceAddress = true
            };

            var maintenance4Features = new PhysicalDeviceMaintenance4Features
            {
                SType = StructureType.PhysicalDeviceMaintenance4Features,
                Maintenance4 = true
            };

            var shaderDemoteFeatures = new PhysicalDeviceShaderDemoteToHelperInvocationFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceShaderDemoteToHelperInvocationFeaturesExt,
                ShaderDemoteToHelperInvocation = true
            };
            
            var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                DescriptorBindingSampledImageUpdateAfterBind = true,
                DescriptorBindingStorageBufferUpdateAfterBind = true,
                DescriptorBindingPartiallyBound = true,
                DescriptorBindingVariableDescriptorCount = true,
                RuntimeDescriptorArray = true,
                ShaderSampledImageArrayNonUniformIndexing = true,
                ShaderStorageBufferArrayNonUniformIndexing = true,
                ShaderUniformBufferArrayNonUniformIndexing = true
            };
            
            // Chain: descriptorIndexing -> bufferDeviceAddress -> sync2 -> dynamicRendering -> meshShader -> maintenance4 -> shaderDemote
            descriptorIndexingFeatures.PNext = &bufferDeviceAddressFeatures;
            bufferDeviceAddressFeatures.PNext = &sync2Features;
            sync2Features.PNext = &dynamicRenderingFeatures;
            dynamicRenderingFeatures.PNext = &meshShaderFeatures;
            meshShaderFeatures.PNext = &maintenance4Features;
            maintenance4Features.PNext = &shaderDemoteFeatures;
            shaderDemoteFeatures.PNext = deviceFeatures2.PNext;
            deviceFeatures2.PNext = &descriptorIndexingFeatures;
            
            if (!TryGetDeviceRequirements(_physicalDevice, out DeviceRequirements requirements) || !requirements.IsSupported)
            {
                throw new VulkanException(
                    $"Selected Vulkan device does not support required rendering features: {requirements.MissingRequirements}.");
            }
            
            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueCreateInfos.Count,
                PQueueCreateInfos = null,
                PEnabledFeatures = null,
                PNext = &deviceFeatures2,
                EnabledExtensionCount = (uint)RequiredDeviceExtensions.Length,
                PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(RequiredDeviceExtensions)
            };
            
            try
            {
                var queueCreateInfoArray = queueCreateInfos.ToArray();
                fixed (DeviceQueueCreateInfo* queueCreateInfosPtr = queueCreateInfoArray)
                {
                    deviceCreateInfo.PQueueCreateInfos = queueCreateInfosPtr;
                    Result result = _vk.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device);
                    if (result != Result.Success)
                        throw new VulkanException("Failed to create logical device", result);
                }
                
                _vk.GetDeviceQueue(_device, _graphicsQueueFamilyIndex, 0, out _graphicsQueue);
                
                if (_hasDedicatedTransferQueue)
                    _vk.GetDeviceQueue(_device, _transferQueueFamilyIndex, 0, out _transferQueue);
                else
                    _transferQueue = _graphicsQueue;
                
                Console.WriteLine("Logical device created with required features.");
            }
            finally
            {
                if (deviceCreateInfo.PpEnabledExtensionNames != null)
                    SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames);
            }
        }

        private readonly struct DeviceRequirements
        {
            private DeviceRequirements(bool isSupported, string missingRequirements)
            {
                IsSupported = isSupported;
                MissingRequirements = missingRequirements;
            }

            public bool IsSupported { get; }
            public string MissingRequirements { get; }
            public static DeviceRequirements Supported { get; } = new(true, string.Empty);
            public static DeviceRequirements Missing(string missingRequirements) => new(false, missingRequirements);
        }
        
        private void CreateAllocator()
        {
            var allocatorCreateInfo = new GpuAllocator.AllocatorCreateInfo
            {
                VulkanApiVersion = Vk.Version13,
                PhysicalDevice = _physicalDevice,
                Device = _device,
                Instance = _instance,
                Flags = GpuAllocator.AllocatorCreateFlags.BufferDeviceAddressBit
            };
            
            GpuAllocator.Allocator* allocator;
            Result result = GpuAllocator.Apis.CreateAllocator(&allocatorCreateInfo, &allocator);
            if (result != Result.Success)
                throw new VulkanException("Failed to create VMA allocator", result);
            _allocator = allocator;
            
            Console.WriteLine("VMA allocator created with BufferDeviceAddress support.");
        }
        
        private void LoadExtensions()
        {
            _khrSurface = _vk.TryGetInstanceExtension(_instance, out KhrSurface ext) ? ext 
                : throw new VulkanException("KHR_surface extension not available");
            
            if (_debug)
                _vk.TryGetInstanceExtension(_instance, out _extDebugUtils);
            
            _khrSwapchain = _vk.TryGetDeviceExtension(_instance, _device, out KhrSwapchain ext2) ? ext2
                : throw new VulkanException("KHR_swapchain extension not available");
            
            _extMeshShader = _vk.TryGetDeviceExtension(_instance, _device, out ExtMeshShader ext3) ? ext3
                : throw new VulkanException("EXT_mesh_shader extension not available");
            
            _khrDynamicRendering = _vk.TryGetDeviceExtension(_instance, _device, out KhrDynamicRendering ext4) ? ext4
                : throw new VulkanException("KHR_dynamic_rendering extension not available");
            
            _khrSync2 = _vk.TryGetDeviceExtension(_instance, _device, out KhrSynchronization2 ext5) ? ext5
                : throw new VulkanException("KHR_synchronization2 extension not available");
            
            _vk.TryGetDeviceExtension(_instance, _device, out _khrDeferredHostOps);
            
            Console.WriteLine("All required Vulkan extensions loaded.");
        }
        
        private void SetupDebugMessenger()
        {
            var debugMessengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt | 
                                   DebugUtilsMessageSeverityFlagsEXT.InfoBitExt |
                                   DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                   DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                               DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                               DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&DebugCallback),
                PUserData = null
            };
            
            Result result = _extDebugUtils?.CreateDebugUtilsMessenger(_instance, &debugMessengerInfo, null, out _debugMessenger) ?? Result.ErrorExtensionNotPresent;
            if (result != Result.Success)
                Console.WriteLine("Warning: Failed to create debug messenger");
            else
                Console.WriteLine("Debug messenger created.");
        }
        
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static Silk.NET.Core.Bool32 DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* pCallbackData,
            void* pUserData)
        {
            string message = SilkMarshal.PtrToString((nint)pCallbackData->PMessage) ?? string.Empty;
            string prefix = "[Vulkan Debug]";
            
            if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
                Console.Error.WriteLine($"{prefix} ERROR: {message}");
            else if ((severity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
                Console.WriteLine($"{prefix} WARNING: {message}");
            else
                Console.WriteLine($"{prefix} INFO: {message}");
            
            return Vk.False;
        }
        
        public void WaitIdle()
        {
            _vk.DeviceWaitIdle(_device);
        }
        
        public struct SingleTimeCommandContext
        {
            public CommandBuffer CommandBuffer;
            public CommandPool CommandPool;
        }
        
        public SingleTimeCommandContext BeginSingleTimeCommands()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _graphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit
            };
            
            Result result = _vk.CreateCommandPool(_device, &poolInfo, null, out CommandPool pool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create command pool for single-time commands", result);
            
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            
            result = _vk.AllocateCommandBuffers(_device, &allocInfo, out CommandBuffer cmd);
            if (result != Result.Success)
            {
                _vk.DestroyCommandPool(_device, pool, null);
                throw new VulkanException("Failed to allocate command buffer for single-time commands", result);
            }
            
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            
            result = _vk.BeginCommandBuffer(cmd, &beginInfo);
            if (result != Result.Success)
            {
                _vk.FreeCommandBuffers(_device, pool, 1, &cmd);
                _vk.DestroyCommandPool(_device, pool, null);
                throw new VulkanException("Failed to begin single-time command buffer", result);
            }
            
            return new SingleTimeCommandContext { CommandBuffer = cmd, CommandPool = pool };
        }
        
        public void EndSingleTimeCommands(SingleTimeCommandContext ctx)
        {
            Result result = _vk.EndCommandBuffer(ctx.CommandBuffer);
            if (result != Result.Success)
                throw new VulkanException("Failed to end single-time command buffer", result);
            
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &ctx.CommandBuffer
            };
            
            result = _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, default);
            if (result != Result.Success)
                throw new VulkanException("Failed to submit single-time commands", result);
            
            result = _vk.QueueWaitIdle(_graphicsQueue);
            if (result != Result.Success)
                throw new VulkanException("Failed to wait for queue idle", result);
            
            _vk.FreeCommandBuffers(_device, ctx.CommandPool, 1, &ctx.CommandBuffer);
            _vk.DestroyCommandPool(_device, ctx.CommandPool, null);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            if (_debug && _debugMessenger.Handle != 0)
                _extDebugUtils?.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            
            if (_allocator != null)
                GpuAllocator.Apis.DestroyAllocator(_allocator);
            
            if (_device.Handle != 0)
                _vk.DestroyDevice(_device, null);
            
            if (_instance.Handle != 0)
                _vk.DestroyInstance(_instance, null);
            
            Console.WriteLine("Vulkan context disposed.");
        }
        
        ~VulkanContext()
        {
            Dispose(false);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Rendering.Diagnostics;
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
        private readonly RendererValidationSettings _validationSettings;
        private readonly RendererStartupLog? _startupLog;
        private readonly DeviceRequirementOverride _requirementOverride;
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
        private CommandPool _singleTimeCommandPool;
        
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
        public bool DebugUtilsAvailable => _debug && _extDebugUtils != null;
        public RendererValidationSettings ValidationSettings => _validationSettings;
        public DeviceRequirementReport? SelectedDeviceRequirementReport { get; private set; }
        
        public VulkanContext(IWindow window, bool debug = true)
            : this(window, RendererValidationSettings.Default with
            {
                Mode = debug ? RendererValidationMode.Standard : RendererValidationMode.Off
            })
        {
        }

        public VulkanContext(
            IWindow window,
            RendererValidationSettings validationSettings,
            RendererStartupLog? startupLog = null,
            DeviceRequirementOverride? requirementOverride = null)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _validationSettings = validationSettings ?? throw new ArgumentNullException(nameof(validationSettings));
            _debug = _validationSettings.EnableValidation;
            _startupLog = startupLog;
            _requirementOverride = requirementOverride ?? DeviceRequirementOverride.FromEnvironment();
            
            RunStartupStep("VulkanContext.CreateInstance", CreateInstance);
            RunStartupStep("VulkanContext.PickPhysicalDevice", PickPhysicalDevice);
            RunStartupStep("VulkanContext.CreateLogicalDevice", CreateLogicalDevice);
            RunStartupStep("VulkanContext.CreateAllocator", CreateAllocator);
            RunStartupStep("VulkanContext.LoadExtensions", LoadExtensions);
            RunStartupStep("VulkanContext.CreateSingleTimeCommandPool", CreateSingleTimeCommandPool);
            
            if (_debug)
                RunStartupStep("VulkanContext.SetupDebugMessenger", SetupDebugMessenger);
        }

        private void RunStartupStep(string name, Action action)
        {
            _startupLog?.StepStarted(name);
            try
            {
                action();
                _startupLog?.StepSucceeded(name);
            }
            catch (Exception ex)
            {
                _startupLog?.StepFailed(name, ex);
                throw;
            }
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
                RunStartupStep("VulkanContext.ValidateInstanceLayers", () => ValidateInstanceLayers(validationLayers));
            
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
                
                System.Diagnostics.Debug.WriteLine(_debug 
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

            RunStartupStep("VulkanContext.ValidateInstanceExtensions", () => ValidateInstanceExtensions(extensions));
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
            DeviceRequirementReport? bestRejectedDevice = null;
            uint graphicsFamily = uint.MaxValue;
            uint transferFamily = uint.MaxValue;
            
            foreach (var device in devices)
            {
                var properties = new PhysicalDeviceProperties();
                _vk.GetPhysicalDeviceProperties(device, &properties);
                
                if (properties.ApiVersion < Vk.Version13)
                {
                    bestRejectedDevice ??= BuildDeviceRequirementReport(
                        properties,
                        Array.Empty<string>(),
                        new[] { "vulkanApiVersion>=1.3" },
                        Array.Empty<string>(),
                        isSupported: false);
                    continue;
                }
                
                if (!TryGetDeviceRequirements(device, out DeviceRequirements requirements))
                    continue;

                if (!requirements.IsSupported)
                {
                    bestRejectedDevice ??= BuildDeviceRequirementReport(
                        properties,
                        requirements.MissingDeviceExtensions,
                        requirements.MissingFeatures,
                        requirements.MissingQueueFamilies,
                        isSupported: false);
                    continue;
                }
                
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
                
                var missingQueues = new List<string>();
                if (graphicsIndex == uint.MaxValue)
                    missingQueues.Add("graphics");
                if (_requirementOverride.MissingQueueFamilies.Count > 0)
                    missingQueues.AddRange(_requirementOverride.MissingQueueFamilies);

                if (missingQueues.Count != 0)
                {
                    bestRejectedDevice ??= BuildDeviceRequirementReport(
                        properties,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        missingQueues,
                        isSupported: false);
                    continue;
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
            {
                if (bestRejectedDevice != null)
                    throw new VulkanException(bestRejectedDevice.FormatSummary());

                throw new VulkanException(
                    "No suitable Vulkan physical device found. Required: Vulkan 1.3, graphics queue, " +
                    string.Join(", ", RequiredDeviceExtensions) +
                    ", mesh/task shader, descriptor indexing, buffer device address, synchronization2, " +
                    "dynamic rendering, sampler anisotropy, and maintenance4.");
            }
            
            _physicalDevice = selectedDevice.Value;
            _graphicsQueueFamilyIndex = graphicsFamily;
            _transferQueueFamilyIndex = transferFamily;
            _hasDedicatedTransferQueue = graphicsFamily != transferFamily;

            var selectedProperties = new PhysicalDeviceProperties();
            _vk.GetPhysicalDeviceProperties(_physicalDevice, &selectedProperties);
            SelectedDeviceRequirementReport = BuildDeviceRequirementReport(
                selectedProperties,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                isSupported: true);
            _startupLog?.DeviceSelected(SelectedDeviceRequirementReport);
            
            System.Diagnostics.Debug.WriteLine("Physical device selected with mesh shader support.");
        }
        
        private bool TryGetDeviceRequirements(PhysicalDevice device, out DeviceRequirements requirements)
        {
            requirements = default;
            if (!ValidateDeviceExtensions(device, RequiredDeviceExtensions, out List<string> missingExtensions))
            {
                requirements = DeviceRequirements.Missing(missingDeviceExtensions: missingExtensions);
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
            if (!features2.Features.ImageCubeArray)
                missingFeatures.Add("imageCubeArray");
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

            if (_requirementOverride.HasOverrides)
            {
                missingExtensions.AddRange(_requirementOverride.MissingDeviceExtensions);
                missingFeatures.AddRange(_requirementOverride.MissingFeatures);
            }

            requirements = missingFeatures.Count == 0 && missingExtensions.Count == 0
                ? DeviceRequirements.Supported
                : DeviceRequirements.Missing(missingExtensions, missingFeatures);
            return true;
        }

        private static DeviceRequirementReport BuildDeviceRequirementReport(
            PhysicalDeviceProperties properties,
            IReadOnlyList<string> missingDeviceExtensions,
            IReadOnlyList<string> missingFeatures,
            IReadOnlyList<string> missingQueueFamilies,
            bool isSupported)
        {
            return new DeviceRequirementReport(
                GetDeviceName(properties),
                properties.VendorID,
                properties.DeviceID,
                FormatVulkanVersion(properties.ApiVersion),
                FormatVulkanVersion(properties.DriverVersion),
                Array.Empty<string>(),
                Array.Empty<string>(),
                missingDeviceExtensions,
                missingFeatures,
                missingQueueFamilies,
                isSupported);
        }

        private static string FormatVulkanVersion(uint version)
        {
            uint major = version >> 22;
            uint minor = (version >> 12) & 0x3ff;
            uint patch = version & 0xfff;
            return $"{major}.{minor}.{patch}";
        }

        private static string GetDeviceName(PhysicalDeviceProperties properties)
        {
            return SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
        }

        private bool ValidateDeviceExtensions(
            PhysicalDevice device,
            IReadOnlyCollection<string> requiredExtensions,
            out List<string> missingExtensions)
        {
            var missing = new List<string>();
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
                    missing.Add(extension);
            }

            missingExtensions = missing;
            return missing.Count == 0;
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
                    ImageCubeArray = true,
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
                
                System.Diagnostics.Debug.WriteLine("Logical device created with required features.");
            }
            finally
            {
                if (deviceCreateInfo.PpEnabledExtensionNames != null)
                    SilkMarshal.Free((nint)deviceCreateInfo.PpEnabledExtensionNames);
            }
        }

        private readonly struct DeviceRequirements
        {
            private DeviceRequirements(
                bool isSupported,
                IReadOnlyList<string> missingDeviceExtensions,
                IReadOnlyList<string> missingFeatures,
                IReadOnlyList<string> missingQueueFamilies)
            {
                IsSupported = isSupported;
                MissingDeviceExtensions = missingDeviceExtensions;
                MissingFeatures = missingFeatures;
                MissingQueueFamilies = missingQueueFamilies;
            }

            public bool IsSupported { get; }
            public IReadOnlyList<string> MissingDeviceExtensions { get; }
            public IReadOnlyList<string> MissingFeatures { get; }
            public IReadOnlyList<string> MissingQueueFamilies { get; }
            public string MissingRequirements => string.Join(", ", MissingDeviceExtensions)
                + (MissingFeatures.Count == 0 ? string.Empty : (MissingDeviceExtensions.Count == 0 ? string.Empty : ", ") + string.Join(", ", MissingFeatures))
                + (MissingQueueFamilies.Count == 0 ? string.Empty : ", " + string.Join(", ", MissingQueueFamilies));

            public static DeviceRequirements Supported { get; } = new(
                true,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());

            public static DeviceRequirements Missing(
                IReadOnlyList<string>? missingDeviceExtensions = null,
                IReadOnlyList<string>? missingFeatures = null,
                IReadOnlyList<string>? missingQueueFamilies = null)
            {
                return new DeviceRequirements(
                    false,
                    missingDeviceExtensions ?? Array.Empty<string>(),
                    missingFeatures ?? Array.Empty<string>(),
                    missingQueueFamilies ?? Array.Empty<string>());
            }
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
            
            System.Diagnostics.Debug.WriteLine("VMA allocator created with BufferDeviceAddress support.");
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
            
            System.Diagnostics.Debug.WriteLine("All required Vulkan extensions loaded.");
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
                System.Diagnostics.Debug.WriteLine("Warning: Failed to create debug messenger");
            else
                System.Diagnostics.Debug.WriteLine("Debug messenger created.");
        }

        public void SetDebugName(ulong objectHandle, ObjectType objectType, string name)
        {
            if (!DebugUtilsAvailable || objectHandle == 0 || string.IsNullOrWhiteSpace(name))
                return;

            nint namePtr = SilkMarshal.StringToPtr(name);
            try
            {
                var nameInfo = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = (byte*)namePtr
                };

                Result result = _extDebugUtils!.SetDebugUtilsObjectName(_device, &nameInfo);
                if (result != Result.Success)
                    System.Diagnostics.Debug.WriteLine($"Failed to set Vulkan debug name '{name}': {result}");
            }
            finally
            {
                SilkMarshal.Free(namePtr);
            }
        }

        public void SetDebugName(nint objectHandle, ObjectType objectType, string name)
        {
            SetDebugName(unchecked((ulong)objectHandle), objectType, name);
        }

        public void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (!DebugUtilsAvailable || commandBuffer.Handle == 0 || string.IsNullOrWhiteSpace(name))
                return;

            nint namePtr = SilkMarshal.StringToPtr(name);
            try
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = (byte*)namePtr
                };

                _extDebugUtils!.CmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
            finally
            {
                SilkMarshal.Free(namePtr);
            }
        }

        public void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (!DebugUtilsAvailable || commandBuffer.Handle == 0)
                return;

            _extDebugUtils!.CmdEndDebugUtilsLabel(commandBuffer);
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
                System.Diagnostics.Debug.WriteLine($"{prefix} WARNING: {message}");
            else
                System.Diagnostics.Debug.WriteLine($"{prefix} INFO: {message}");
            
            return Vk.False;
        }
        
        public void WaitIdle()
        {
            _vk.DeviceWaitIdle(_device);
        }

        private void CreateSingleTimeCommandPool()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = _graphicsQueueFamilyIndex,
                Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit
            };

            Result result = _vk.CreateCommandPool(_device, &poolInfo, null, out _singleTimeCommandPool);
            if (result != Result.Success)
                throw new VulkanException("Failed to create single-time command pool", result);
            SetDebugName(_singleTimeCommandPool.Handle, ObjectType.CommandPool, "Single Time Command Pool");
        }
        
        public struct SingleTimeCommandContext
        {
            public CommandBuffer CommandBuffer;
            public CommandPool CommandPool;
        }
        
        public SingleTimeCommandContext BeginSingleTimeCommands()
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _singleTimeCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            
            Result result = _vk.AllocateCommandBuffers(_device, &allocInfo, out CommandBuffer cmd);
            if (result != Result.Success)
                throw new VulkanException("Failed to allocate command buffer for single-time commands", result);
            SetDebugName(cmd.Handle, ObjectType.CommandBuffer, "Single Time Command Buffer");
            
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            
            result = _vk.BeginCommandBuffer(cmd, &beginInfo);
            if (result != Result.Success)
            {
                _vk.FreeCommandBuffers(_device, _singleTimeCommandPool, 1, &cmd);
                throw new VulkanException("Failed to begin single-time command buffer", result);
            }
            
            return new SingleTimeCommandContext { CommandBuffer = cmd, CommandPool = _singleTimeCommandPool };
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

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo
            };

            result = _vk.CreateFence(_device, &fenceInfo, null, out Fence fence);
            if (result != Result.Success)
                throw new VulkanException("Failed to create single-time command fence", result);
            
            result = _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, fence);
            if (result != Result.Success)
            {
                _vk.DestroyFence(_device, fence, null);
                throw new VulkanException("Failed to submit single-time commands", result);
            }
            
            result = _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue);
            if (result != Result.Success)
            {
                _vk.DestroyFence(_device, fence, null);
                throw new VulkanException("Failed to wait for single-time command fence", result);
            }

            _vk.DestroyFence(_device, fence, null);
            
            _vk.FreeCommandBuffers(_device, ctx.CommandPool, 1, &ctx.CommandBuffer);
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

            if (_singleTimeCommandPool.Handle != 0)
                _vk.DestroyCommandPool(_device, _singleTimeCommandPool, null);
            
            if (_device.Handle != 0)
                _vk.DestroyDevice(_device, null);
            
            if (_instance.Handle != 0)
                _vk.DestroyInstance(_instance, null);
            
            System.Diagnostics.Debug.WriteLine("Vulkan context disposed.");
        }
    }
}

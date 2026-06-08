using System;
using System.Collections.Generic;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using GpuAllocator = GpuMemoryAllocator.Vulkan;

namespace Njulf.Rendering.Core
{
    /// <summary>
    /// Central Vulkan context managing instance, device, queues, and memory allocator.
    /// </summary>
    public class VulkanContext : IDisposable
    {
        private readonly bool _debug;
        private readonly IWindow _window;
        
        private Instance _instance;
        private PhysicalDevice _physicalDevice;
        private Device _device;
        private GpuAllocator.Allocator _allocator;
        
        private uint _graphicsQueueFamilyIndex;
        private uint _transferQueueFamilyIndex;
        private Queue _graphicsQueue;
        private Queue _transferQueue;
        private bool _hasDedicatedTransferQueue;
        
        private Vk _vk;
        private KhrSurface _khrSurface;
        private KhrSwapchain _khrSwapchain;
        private ExtMeshShader _extMeshShader;
        private KhrDynamicRendering _khrDynamicRendering;
        private KhrSynchronization2 _khrSync2;
        private KhrDeferredHostOperations? _khrDeferredHostOps;
        private ExtDebugUtils? _extDebugUtils;
        private DebugUtilsMessengerEXT _debugMessenger;
        
        private bool _disposed;
        
        public Vk Api => _vk;
        public Instance Instance => _instance;
        public PhysicalDevice PhysicalDevice => _physicalDevice;
        public Device Device => _device;
        public GpuAllocator.Allocator Allocator => _allocator;
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
                PApplicationName = SilkMarshal.StringToPtr("Njulf"),
                ApplicationVersion = Vk.MakeApiVersion(0, 1, 0, 0),
                PEngineName = SilkMarshal.StringToPtr("Njulf"),
                EngineVersion = Vk.MakeApiVersion(0, 1, 0, 0),
                ApiVersion = Vk.ApiVersion13
            };
            
            List<string> instanceExtensions = GetRequiredInstanceExtensions();
            string[] validationLayers = _debug ? new[] { "VK_LAYER_KHRONOS_validation" } : Array.Empty<string>();
            
            var instanceCreateInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledLayerCount = (uint)validationLayers.Length,
                PPEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers),
                EnabledExtensionCount = (uint)instanceExtensions.Count,
                PPEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(instanceExtensions.ToArray())
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
                if (instanceCreateInfo.PPEnabledLayerNames != null)
                    SilkMarshal.FreeStringArray((nint)instanceCreateInfo.PPEnabledLayerNames, validationLayers);
                if (instanceCreateInfo.PPEnabledExtensionNames != null)
                    SilkMarshal.FreeStringArray((nint)instanceCreateInfo.PPEnabledExtensionNames, instanceExtensions.ToArray());
                SilkMarshal.Free((nint)appInfo.PApplicationName);
                SilkMarshal.Free((nint)appInfo.PEngineName);
            }
        }
        
        private List<string> GetRequiredInstanceExtensions()
        {
            var extensions = new List<string>();
            
            // Platform-specific surface extension
            if (OperatingSystem.IsWindows())
                extensions.Add("VK_KHR_win32_surface");
            else if (OperatingSystem.IsLinux())
                extensions.Add("VK_KHR_xcb_surface");
            
            // Core surface extensions
            extensions.Add("VK_KHR_surface");
            extensions.Add("VK_KHR_get_surface_capabilities_2");
            
            // Debug utilities in debug mode
            if (_debug)
                extensions.Add("VK_EXT_debug_utils");
            
            return extensions;
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
                
                if (properties.ApiVersion < Vk.ApiVersion13)
                    continue;
                
                // Check required features
                if (!CheckDeviceFeatures(device))
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
                throw new VulkanException("No suitable physical device found with Vulkan 1.3+ and mesh shader support");
            
            _physicalDevice = selectedDevice.Value;
            _graphicsQueueFamilyIndex = graphicsFamily;
            _transferQueueFamilyIndex = transferFamily;
            _hasDedicatedTransferQueue = graphicsFamily != transferFamily;
            
            Console.WriteLine("Physical device selected with mesh shader support.");
        }
        
        private bool CheckDeviceFeatures(PhysicalDevice device)
        {
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
            
            var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                DescriptorBindingPartiallyBound = true,
                DescriptorBindingVariableDescriptorCount = true,
                RuntimeDescriptorArray = true,
                ShaderStorageBufferArrayDynamicIndexing = true,
                ShaderSampledImageArrayDynamicIndexing = true
            };
            
            // Chain all features
            descriptorIndexingFeatures.PNext = &bufferDeviceAddressFeatures;
            bufferDeviceAddressFeatures.PNext = &sync2Features;
            sync2Features.PNext = &dynamicRenderingFeatures;
            dynamicRenderingFeatures.PNext = &meshShaderFeatures;
            meshShaderFeatures.PNext = features2.PNext;
            features2.PNext = &descriptorIndexingFeatures;
            
            _vk.GetPhysicalDeviceFeatures2(device, &features2);
            
            return meshShaderFeatures.MeshShader && 
                   meshShaderFeatures.TaskShader &&
                   dynamicRenderingFeatures.DynamicRendering &&
                   sync2Features.Synchronization2 &&
                   bufferDeviceAddressFeatures.BufferDeviceAddress;
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
                    ShaderStorageImageExtendedFormats = true
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
            
            var descriptorIndexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                DescriptorBindingPartiallyBound = true,
                DescriptorBindingVariableDescriptorCount = true,
                RuntimeDescriptorArray = true,
                ShaderStorageBufferArrayDynamicIndexing = true,
                ShaderSampledImageArrayDynamicIndexing = true
            };
            
            // Chain: descriptorIndexing -> bufferDeviceAddress -> sync2 -> dynamicRendering -> meshShader
            descriptorIndexingFeatures.PNext = &bufferDeviceAddressFeatures;
            bufferDeviceAddressFeatures.PNext = &sync2Features;
            sync2Features.PNext = &dynamicRenderingFeatures;
            dynamicRenderingFeatures.PNext = &meshShaderFeatures;
            meshShaderFeatures.PNext = deviceFeatures2.PNext;
            deviceFeatures2.PNext = &descriptorIndexingFeatures;
            
            string[] deviceExtensions = {
                "VK_KHR_swapchain",
                "VK_KHR_dynamic_rendering",
                "VK_KHR_synchronization2",
                "VK_EXT_mesh_shader",
                "VK_KHR_buffer_device_address",
                "VK_EXT_descriptor_indexing",
                "VK_KHR_deferred_host_operations"
            };
            
            var deviceCreateInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueCreateInfos.Count,
                PQueueCreateInfos = queueCreateInfos.ToArray(),
                PEnabledFeatures = null,
                PNext = &deviceFeatures2,
                EnabledExtensionCount = (uint)deviceExtensions.Length,
                PPEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions)
            };
            
            try
            {
                Result result = _vk.CreateDevice(_physicalDevice, &deviceCreateInfo, null, out _device);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create logical device", result);
                
                _vk.GetDeviceQueue(_device, _graphicsQueueFamilyIndex, 0, out _graphicsQueue);
                
                if (_hasDedicatedTransferQueue)
                    _vk.GetDeviceQueue(_device, _transferQueueFamilyIndex, 0, out _transferQueue);
                else
                    _transferQueue = _graphicsQueue;
                
                Console.WriteLine("Logical device created with required features.");
            }
            finally
            {
                if (deviceCreateInfo.PPEnabledExtensionNames != null)
                    SilkMarshal.FreeStringArray((nint)deviceCreateInfo.PPEnabledExtensionNames, deviceExtensions);
            }
        }
        
        private void CreateAllocator()
        {
            var allocatorCreateInfo = new GpuAllocator.AllocatorCreateInfo
            {
                VulkanApiVersion = Vk.ApiVersion13,
                PhysicalDevice = _physicalDevice,
                Device = _device,
                Instance = _instance,
                Flags = GpuAllocator.AllocatorCreateFlags.BitBufferDeviceAddress
            };
            
            Result result = GpuAllocator.Apis.CreateAllocator(&allocatorCreateInfo, out _allocator);
            if (result != Result.Success)
                throw new VulkanException("Failed to create VMA allocator", result);
            
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
                PfnUserCallback = DebugCallback,
                PUserData = null
            };
            
            Result result = _vk.CreateDebugUtilsMessenger(_instance, &debugMessengerInfo, null, out _debugMessenger);
            if (result != Result.Success)
                Console.WriteLine("Warning: Failed to create debug messenger");
            else
                Console.WriteLine("Debug messenger created.");
        }
        
        private static uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* pCallbackData,
            void* pUserData)
        {
            string message = SilkMarshal.PtrToString((nint)pCallbackData->PMessage);
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
                _vk.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            
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
    
    public class VulkanException : Exception
    {
        public Result Result { get; }
        
        public VulkanException(string message, Result result) : base($"{message}: {result}")
        {
            Result = result;
        }
        
        public VulkanException(string message) : base(message)
        {
            Result = Result.ErrorUnknown;
        }
    }
}

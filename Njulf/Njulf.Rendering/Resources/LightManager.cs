using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public enum LightType : int
    {
        Point = 0,
        Directional = 1,
        Spot = 2
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Light
    {
        public Vector3 Position;
        public float Intensity;
        public Vector3 Color;
        public float Range;
        public Vector3 Direction;
        public float SpotAngle;
        public LightType Type;
        public int Padding0;
        public int Padding1;
        public int Padding2;
    }
    
    public sealed unsafe class LightManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly object _lock = new object();
        
        private BufferHandle _lightBuffer;
        private Light[] _cpuLights;
        private int _lightCount;
        private bool _needsUpload;
        private ulong _lastUploadBytes;
        private bool _disposed;
        
        public const int MaxLights = 1024;
        private static readonly ulong LightStride = (ulong)Marshal.SizeOf<GPULight>();
        private static readonly ulong LightBufferSize = checked((ulong)MaxLights * (ulong)Marshal.SizeOf<GPULight>());
        
        public LightManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            if (Marshal.SizeOf<Light>() != Marshal.SizeOf<GPULight>())
            {
                throw new InvalidOperationException(
                    $"{nameof(Light)} must stay binary-compatible with {nameof(GPULight)} for GPU upload.");
            }
            
            _cpuLights = new Light[MaxLights];
            _lightCount = 0;
            _needsUpload = false;
            
            _lightBuffer = _bufferManager.CreateDeviceBuffer(
                LightBufferSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            Console.WriteLine("Light manager created");
        }
        
        public int AddLight(Light light)
        {
            lock (_lock)
            {
                if (_lightCount >= MaxLights)
                    throw new InvalidOperationException($"Forward+ supports at most {MaxLights} lights.");
                
                int index = _lightCount++;
                _cpuLights[index] = light;
                _needsUpload = true;
                return index;
            }
        }
        
        public void RemoveLight(int index)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _lightCount)
                    return;
                
                _lightCount--;
                if (index < _lightCount)
                {
                    _cpuLights[index] = _cpuLights[_lightCount];
                }
                _needsUpload = true;
            }
        }
        
        public void UpdateLight(int index, Light light)
        {
            lock (_lock)
            {
                if (index < 0 || index >= _lightCount)
                    return;
                
                _cpuLights[index] = light;
                _needsUpload = true;
            }
        }
        
        public void ClearLights()
        {
            lock (_lock)
            {
                _lightCount = 0;
                _needsUpload = true;
            }
        }
        
        public BufferHandle LightBuffer => _lightBuffer;
        public int LightCount => _lightCount;
        public int MaxLightCount => MaxLights;
        public ulong LastUploadBytes
        {
            get
            {
                lock (_lock)
                    return _lastUploadBytes;
            }
        }

        public int DirectionalLightCount => CountLights(LightType.Directional);
        public int LocalLightCount
        {
            get
            {
                lock (_lock)
                    return _lightCount - CountLightsUnsafe(LightType.Directional);
            }
        }

        private int CountLights(LightType type)
        {
            lock (_lock)
                return CountLightsUnsafe(type);
        }

        private int CountLightsUnsafe(LightType type)
        {
            int count = 0;
            for (int i = 0; i < _lightCount; i++)
            {
                if (_cpuLights[i].Type == type)
                    count++;
            }

            return count;
        }

        public void RegisterBuffer(BindlessHeap bindlessHeap, int bindlessIndex)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            bindlessHeap.RegisterStorageBuffer(
                bindlessIndex,
                _bufferManager.GetBuffer(_lightBuffer),
                0,
                Vk.WholeSize);
        }
        
        public void UploadToGPU(StagingRing stagingRing, CommandBuffer commandBuffer)
        {
            if (stagingRing == null)
                throw new ArgumentNullException(nameof(stagingRing));
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for light upload.", nameof(commandBuffer));

            if (!_needsUpload)
            {
                lock (_lock)
                    _lastUploadBytes = 0;
                return;
            }
            
            lock (_lock)
            {
                _lastUploadBytes = 0;
                if (_lightCount == 0)
                {
                    _needsUpload = false;
                    return;
                }

                ulong dataSize = checked((ulong)_lightCount * LightStride);
                var (stagingHandle, stagingOffset) = stagingRing.Allocate(dataSize);
                void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);

                fixed (Light* source = _cpuLights)
                {
                    System.Buffer.MemoryCopy(
                        source,
                        (byte*)mappedData + stagingOffset,
                        dataSize,
                        dataSize);
                }

                _bufferManager.FlushBuffer(stagingHandle, stagingOffset, dataSize);

                var region = new BufferCopy
                {
                    SrcOffset = stagingOffset,
                    DstOffset = 0,
                    Size = dataSize
                };

                _context.Api.CmdCopyBuffer(
                    commandBuffer,
                    _bufferManager.GetBuffer(stagingHandle),
                    _bufferManager.GetBuffer(_lightBuffer),
                    1,
                    &region);

                var barrier = new BufferMemoryBarrier2
                {
                    SType = StructureType.BufferMemoryBarrier2,
                    SrcStageMask = PipelineStageFlags2.TransferBit,
                    SrcAccessMask = AccessFlags2.TransferWriteBit,
                    DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
                    DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = _bufferManager.GetBuffer(_lightBuffer),
                    Offset = 0,
                    Size = dataSize
                };

                var dependencyInfo = new DependencyInfo
                {
                    SType = StructureType.DependencyInfo,
                    BufferMemoryBarrierCount = 1,
                    PBufferMemoryBarriers = &barrier
                };

                _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
                _lastUploadBytes = dataSize;
                _needsUpload = false;
            }
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                if (_lightBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_lightBuffer);
            }
            
            Console.WriteLine("Light manager disposed.");
        }
        
        ~LightManager()
        {
            Dispose(false);
        }
    }
}

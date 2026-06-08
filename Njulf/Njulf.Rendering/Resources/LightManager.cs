using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Rendering.Core;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using GpuAllocator = Vma;
using Vma;

namespace Njulf.Rendering.Resources
{
    public enum LightType : int
    {
        Point = 0,
        Directional = 1,
        Spot = 2
    }
    
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
    
    public sealed class LightManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly object _lock = new object();
        
        private BufferHandle _lightBuffer;
        private Light[] _cpuLights;
        private int _lightCount;
        private bool _needsUpload;
        private bool _disposed;
        
        private const int MaxLights = 1024;
        private const ulong LightBufferSize = MaxLights * 64; // 64 bytes per light
        
        public LightManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            
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
                    return -1;
                
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
        
        public unsafe void UploadToGPU()
        {
            if (!_needsUpload || _lightCount == 0)
                return;
            
            lock (_lock)
            {
                // Create staging buffer
                ulong dataSize = (ulong)_lightCount * 64;
                var stagingHandle = _bufferManager.CreateStagingBuffer(dataSize);
                var stagingBuffer = _bufferManager.GetBuffer(stagingHandle);
                
                // Map staging buffer and copy light data
                void* mappedData = _bufferManager.GetMappedPointer(stagingHandle);
                for (int i = 0; i < _lightCount; i++)
                {
                    var light = _cpuLights[i];
                    int offset = i * 64;
                    
                    // Copy Position
                    ((float*)mappedData)[offset / 4 + 0] = light.Position.X;
                    ((float*)mappedData)[offset / 4 + 1] = light.Position.Y;
                    ((float*)mappedData)[offset / 4 + 2] = light.Position.Z;
                    ((float*)mappedData)[offset / 4 + 3] = light.Intensity;
                    
                    // Copy Color
                    ((float*)mappedData)[offset / 4 + 4] = light.Color.X;
                    ((float*)mappedData)[offset / 4 + 5] = light.Color.Y;
                    ((float*)mappedData)[offset / 4 + 6] = light.Color.Z;
                    ((float*)mappedData)[offset / 4 + 7] = light.Range;
                    
                    // Copy Direction
                    ((float*)mappedData)[offset / 4 + 8] = light.Direction.X;
                    ((float*)mappedData)[offset / 4 + 9] = light.Direction.Y;
                    ((float*)mappedData)[offset / 4 + 10] = light.Direction.Z;
                    ((float*)mappedData)[offset / 4 + 11] = light.SpotAngle;
                    
                    // Copy Type
                    ((int*)mappedData)[offset / 4 + 12] = (int)light.Type;
                    ((int*)mappedData)[offset / 4 + 13] = light.Padding0;
                    ((int*)mappedData)[offset / 4 + 14] = light.Padding1;
                    ((int*)mappedData)[offset / 4 + 15] = light.Padding2;
                }
                
                // Flush staging buffer
                _bufferManager.FlushBuffer(stagingHandle, 0, dataSize);
                
                // Use single-time commands to copy
                var ctx = _context.BeginSingleTimeCommands();
                var cmd = ctx.CommandBuffer;
                
                // Copy buffer
                var region = new BufferCopy
                {
                    SrcOffset = 0,
                    DstOffset = 0,
                    Size = dataSize
                };
                
                var targetBuffer = _bufferManager.GetBuffer(_lightBuffer);
                var stagingTargetBuffer = _bufferManager.GetBuffer(stagingHandle);
                
                _context.Api.CmdCopyBuffer(cmd, stagingTargetBuffer, targetBuffer, 1, &region);
                
                _context.EndSingleTimeCommands(ctx);
                
                // Clean up staging buffer
                _bufferManager.DestroyBuffer(stagingHandle);
                
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

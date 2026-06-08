using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// Builds scene data for GPU submission.
    /// Responsibilities:
    /// - CPU frustum culling of visible objects
    /// - Generate per-meshlet draw commands
    /// - Deduplicate materials and meshes
    /// - Upload scene data via staging ring
    /// - Validate offsets against buffer sizes
    /// </summary>
    public sealed class SceneDataBuilder : IDisposable
    {
        private readonly Resources.MeshManager _meshManager;
        private readonly Memory.BufferManager _bufferManager;
        private readonly Memory.StagingRing _stagingRing;
        
        private bool _disposed;
        
        // Scene data buffers
        private Memory.BufferHandle _objectDataBuffer;
        private Memory.BufferHandle _meshletDrawBuffer;
        
        // Current scene state
        private readonly List<ObjectData> _objectData = new List<ObjectData>();
        private readonly List<MeshletDrawCommand> _meshletDrawCommands = new List<MeshletDrawCommand>();
        
        // Per-frame buffer handles
        private readonly Memory.BufferHandle[] _perFrameObjectBuffers = new Memory.BufferHandle[RenderingConstants.FramesInFlight];
        private readonly Memory.BufferHandle[] _perFrameDrawBuffers = new Memory.BufferHandle[RenderingConstants.FramesInFlight];
        
        public SceneDataBuilder(
            Resources.MeshManager meshManager,
            Memory.BufferManager bufferManager,
            Memory.StagingRing stagingRing)
        {
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            
            // Create per-frame buffers
            for (int i = 0; i < RenderingConstants.FramesInFlight; i++)
            {
                _perFrameObjectBuffers[i] = _bufferManager.CreateDeviceBuffer(
                    64 * 1024 * 1024, // 64MB
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    true);
                
                _perFrameDrawBuffers[i] = _bufferManager.CreateDeviceBuffer(
                    16 * 1024 * 1024, // 16MB
                    BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                    true);
            }
            
            // Register buffers in bindless heap
            // Index 8: InstanceBufferBase
            // Index 9: InstanceBufferFrame1
            // Index 10: MeshletDrawBufferBase
            // Index 11: MeshletDrawBufferFrame1
            
            Console.WriteLine("Scene data builder created.");
        }
        
        public struct ObjectData
        {
            public Matrix4x4 WorldMatrix;
            public Matrix4x4 WorldMatrixInverseTranspose;
            public int MeshIndex;
            public int MaterialIndex;
        }
        
        public struct MeshletDrawCommand
        {
            public uint MeshletIndex;
            public uint InstanceId;
            public uint MaterialIndex;
            public uint Padding;
        }
        
        public SceneRenderingData Build(Scene scene, ICamera camera, uint screenWidth, uint screenHeight)
        {
            int frameIndex = _stagingRing.CurrentFrameIndex;

            _objectData.Clear();
            _meshletDrawCommands.Clear();
            
            // Calculate view and projection matrices
            var viewMatrix = camera.ViewMatrix;
            var projectionMatrix = camera.ProjectionMatrix;
            var viewProjectionMatrix = camera.ViewProjectionMatrix;
            
            // Frustum culling
            var frustum = CalculateFrustum(viewProjectionMatrix);
            
            // Process visible objects
            foreach (var renderObject in scene.RenderObjects)
            {
                if (!IsVisible(renderObject, frustum))
                    continue;
                
                // Get mesh info
                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                    continue;

                var meshInfo = _meshManager.GetMeshInfo(meshHandle);
                int materialIndex = renderObject.Material is int index ? index : 0;
                
                // Add object data
                _objectData.Add(new ObjectData
                {
                    WorldMatrix = renderObject.WorldMatrix,
                    WorldMatrixInverseTranspose = renderObject.WorldMatrix.Invert().Transpose(),
                    MeshIndex = meshHandle.Index,
                    MaterialIndex = materialIndex
                });
                
                // Add meshlet draw commands
                for (uint i = 0; i < meshInfo.MeshletCount; i++)
                {
                    _meshletDrawCommands.Add(new MeshletDrawCommand
                    {
                        MeshletIndex = meshInfo.MeshletOffset + i,
                        InstanceId = (uint)(_objectData.Count - 1),
                        MaterialIndex = (uint)materialIndex,
                        Padding = 0
                    });
                }
            }
            
            // Upload data to GPU
            UploadSceneData();
            
            // Return scene data
            return new SceneRenderingData
            {
                ObjectCount = _objectData.Count,
                MeshletCount = _meshletDrawCommands.Count,
                LightCount = 0,
                CurrentFrameIndex = (uint)frameIndex,
                ViewMatrix = viewMatrix,
                ProjectionMatrix = projectionMatrix,
                ViewProjectionMatrix = viewProjectionMatrix,
                CameraPosition = camera.Position,
                ScreenWidth = screenWidth,
                ScreenHeight = screenHeight
            };
        }
        
        private unsafe void UploadSceneData()
        {
            // Upload object data
            if (_objectData.Count > 0)
            {
                ulong objectDataSize = (ulong)_objectData.Count * 128; // 128 bytes per object
                var (stagingBuffer, offset) = _stagingRing.Allocate(objectDataSize);
                
                // Map and copy
                void* mappedData = _bufferManager.GetMappedPointer(stagingBuffer);
                for (int i = 0; i < _objectData.Count; i++)
                {
                    var obj = _objectData[i];
                    // Copy to mapped memory
                    // TODO: Implement proper memory copy
                }
                
                _bufferManager.FlushBuffer(stagingBuffer, offset, objectDataSize);
                
                // Copy to device buffer
                // TODO: Implement copy command
            }
            
            // Upload meshlet draw commands
            if (_meshletDrawCommands.Count > 0)
            {
                ulong drawDataSize = (ulong)_meshletDrawCommands.Count * 16; // 16 bytes per command
                var (stagingBuffer, offset) = _stagingRing.Allocate(drawDataSize);
                
                // Map and copy
                void* mappedData = _bufferManager.GetMappedPointer(stagingBuffer);
                for (int i = 0; i < _meshletDrawCommands.Count; i++)
                {
                    var cmd = _meshletDrawCommands[i];
                    // Copy to mapped memory
                    // TODO: Implement proper memory copy
                }
                
                _bufferManager.FlushBuffer(stagingBuffer, offset, drawDataSize);
                
                // Copy to device buffer
                // TODO: Implement copy command
            }
        }
        
        private Frustum CalculateFrustum(Matrix4x4 viewProjection)
        {
            // Extract frustum planes from view-projection matrix
            // This is a placeholder implementation
            return new Frustum();
        }
        
        private bool IsVisible(RenderObject renderObject, Frustum frustum)
        {
            // Check if render object is visible in frustum
            // This is a placeholder implementation
            return true;
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
            
            foreach (var buffer in _perFrameObjectBuffers)
                if (buffer.IsValid) _bufferManager.DestroyBuffer(buffer);
            
            foreach (var buffer in _perFrameDrawBuffers)
                if (buffer.IsValid) _bufferManager.DestroyBuffer(buffer);
            
            Console.WriteLine("Scene data builder disposed.");
        }
        
        ~SceneDataBuilder()
        {
            Dispose(false);
        }
    }
    
    public struct Frustum
    {
        // Frustum plane equations
        public Vector4 Left;
        public Vector4 Right;
        public Vector4 Bottom;
        public Vector4 Top;
        public Vector4 Near;
        public Vector4 Far;
    }
    
}

using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Core.Scene;
using Silk.NET.Vulkan;

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
        private readonly Memory.BufferHandle[] _perFrameObjectBuffers = new Memory.BufferHandle[2];
        private readonly Memory.BufferHandle[] _perFrameDrawBuffers = new Memory.BufferHandle[2];
        
        public SceneDataBuilder(
            Resources.MeshManager meshManager,
            Memory.BufferManager bufferManager,
            Memory.StagingRing stagingRing)
        {
            _meshManager = meshManager ?? throw new ArgumentNullException(nameof(meshManager));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            
            // Create per-frame buffers
            for (int i = 0; i < 2; i++)
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
        
        public SceneRenderingData Build(Scene scene, ICamera camera)
        {
            _objectData.Clear();
            _meshletDrawCommands.Clear();
            
            // Calculate view and projection matrices
            var viewMatrix = camera.GetViewMatrix();
            var projectionMatrix = camera.GetProjectionMatrix();
            var viewProjectionMatrix = viewMatrix * projectionMatrix;
            
            // Frustum culling
            var frustum = CalculateFrustum(viewProjectionMatrix);
            
            // Process visible objects
            foreach (var renderObject in scene.RenderObjects)
            {
                if (!IsVisible(renderObject, frustum))
                    continue;
                
                // Get mesh info
                var meshHandle = renderObject.MeshHandle;
                var meshInfo = _meshManager.GetMeshInfo(meshHandle);
                
                // Add object data
                _objectData.Add(new ObjectData
                {
                    WorldMatrix = renderObject.Transform,
                    WorldMatrixInverseTranspose = Matrix4x4.Transpose(Matrix4x4.Invert(renderObject.Transform)),
                    MeshIndex = meshHandle.Index,
                    MaterialIndex = renderObject.MaterialIndex
                });
                
                // Add meshlet draw commands
                for (uint i = 0; i < meshInfo.MeshletCount; i++)
                {
                    _meshletDrawCommands.Add(new MeshletDrawCommand
                    {
                        MeshletIndex = meshInfo.MeshletOffset + i,
                        InstanceId = (uint)(_objectData.Count - 1),
                        MaterialIndex = (uint)renderObject.MaterialIndex,
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
                LightCount = scene.LightCount,
                CurrentFrameIndex = (uint)(_stagingRing.GetCurrentStagingBuffer().Index % 2),
                ViewMatrix = viewMatrix,
                ProjectionMatrix = projectionMatrix,
                ViewProjectionMatrix = viewProjectionMatrix,
                ScreenWidth = (uint)camera.ViewportWidth,
                ScreenHeight = (uint)camera.ViewportHeight
            };
        }
        
        private unsafe void UploadSceneData()
        {
            int frameIndex = (int)(_stagingRing.GetCurrentStagingBuffer().Index % 2);
            
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
            
            _stagingRing.AdvanceFrame();
        }
        
        private Frustum CalculateFrustum(Matrix4x4 viewProjection)
        {
            // Extract frustum planes from view-projection matrix
            // This is a placeholder implementation
            return new Frustum();
        }
        
        private bool IsVisible(Scene.RenderObject renderObject, Frustum frustum)
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
    
    public interface ICamera
    {
        Matrix4x4 GetViewMatrix();
        Matrix4x4 GetProjectionMatrix();
        int ViewportWidth { get; }
        int ViewportHeight { get; }
    }
}

// Extension methods for Matrix4x4
public static class Matrix4x4Extensions
{
    public static Matrix4x4 Invert(Matrix4x4 matrix)
    {
        // Proper matrix inversion
        float a = matrix.M11, b = matrix.M12, c = matrix.M13, d = matrix.M14;
        float e = matrix.M21, f = matrix.M22, g = matrix.M23, h = matrix.M24;
        float i = matrix.M31, j = matrix.M32, k = matrix.M33, l = matrix.M34;
        float m = matrix.M41, n = matrix.M42, o = matrix.M43, p = matrix.M44;
        
        float det = a * (f * (k * p - l * o) - g * (j * p - l * n) + h * (j * o - k * n)) -
                    b * (e * (k * p - l * o) - g * (i * p - l * m) + h * (i * o - k * m)) +
                    c * (e * (j * p - l * n) - f * (i * p - l * m) + h * (i * n - j * m)) -
                    d * (e * (j * o - k * n) - f * (i * o - k * m) + g * (i * n - j * m));
        
        if (Math.Abs(det) < float.Epsilon)
            return Matrix4x4.Identity;
        
        float invDet = 1.0f / det;
        
        return new Matrix4x4
        {
            M11 = invDet * (f * (k * p - l * o) - g * (j * p - l * n) + h * (j * o - k * n)),
            M12 = invDet * (-b * (k * p - l * o) + c * (j * p - l * n) - d * (j * o - k * n)),
            M13 = invDet * (b * (g * p - h * o) - c * (e * p - h * m) + d * (e * o - g * m)),
            M14 = invDet * (-b * (g * l - h * k) + c * (e * l - h * i) - d * (e * k - g * i)),
            
            M21 = invDet * (-e * (k * p - l * o) + g * (i * p - l * m) - h * (i * o - k * m)),
            M22 = invDet * (a * (k * p - l * o) - c * (i * p - l * m) + d * (i * o - k * m)),
            M23 = invDet * (-a * (g * p - h * o) + c * (e * p - h * m) - d * (e * o - g * m)),
            M24 = invDet * (a * (g * l - h * k) - c * (e * l - h * i) + d * (e * k - g * i)),
            
            M31 = invDet * (e * (j * p - l * n) - f * (i * p - l * m) + h * (i * n - j * m)),
            M32 = invDet * (-a * (j * p - l * n) + b * (i * p - l * m) - d * (i * n - j * m)),
            M33 = invDet * (a * (f * p - h * n) - b * (e * p - h * m) + d * (e * n - f * m)),
            M34 = invDet * (-a * (f * l - h * j) + b * (e * l - h * i) - d * (e * j - f * i)),
            
            M41 = invDet * (-e * (j * o - k * n) + f * (i * o - k * m) - g * (i * n - j * m)),
            M42 = invDet * (a * (j * o - k * n) - b * (i * o - k * m) + c * (i * n - j * m)),
            M43 = invDet * (-a * (f * o - h * n) + b * (e * o - h * m) - c * (e * n - f * m)),
            M44 = invDet * (a * (f * k - h * j) - b * (e * k - h * i) + c * (e * j - f * i))
        };
    }
    
    public static Vector3 Translation(this Matrix4x4 matrix)
    {
        return new Vector3(matrix.M41, matrix.M42, matrix.M43);
    }
}

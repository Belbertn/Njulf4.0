using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using GpuAllocator = GpuMemoryAllocator.Vulkan;
using GpuMemoryAllocator;

namespace Njulf.Rendering.Resources
{
    public struct Meshlet
    {
        public Vector3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint LocalVertexOffset;
        public uint LocalVertexCount;
        public uint LocalTriangleOffset;
        public uint LocalTriangleCount;
    }
    
    public struct MeshInfo
    {
        public Vector3 BoundingBoxMin;
        public Vector3 BoundingBoxMax;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint MeshletOffset;
        public uint MeshletCount;
        public uint LocalVertexIndexOffset;
        public uint LocalVertexIndexCount;
        public uint LocalTriangleIndexOffset;
        public uint LocalTriangleIndexCount;
    }
    
    public sealed class MeshManager : IDisposable
    {
        private readonly VulkanContext _context;
        private readonly BufferManager _bufferManager;
        private readonly object _lock = new object();
        
        private BufferHandle _vertexBuffer;
        private BufferHandle _indexBuffer;
        private BufferHandle _meshletBuffer;
        private BufferHandle _meshletVertexIndexBuffer;
        private BufferHandle _meshletTriangleIndexBuffer;
        
        private readonly List<MeshInfo> _meshes = new List<MeshInfo>();
        private readonly Stack<int> _freeIndices = new Stack<int>();
        private bool _disposed;
        
        private const int MaxVerticesPerMeshlet = 64;
        private const int MaxTrianglesPerMeshlet = 126;
        private const ulong InitialBufferSize = 16 * 1024 * 1024;
        private const ulong BufferGrowthFactor = 2;
        
        public MeshManager(VulkanContext context, BufferManager bufferManager)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            
            CreateConsolidatedBuffers(InitialBufferSize);
            Console.WriteLine("Mesh manager created");
        }
        
        private void CreateConsolidatedBuffers(ulong size)
        {
            _vertexBuffer = _bufferManager.CreateDeviceBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                true);
            
            _indexBuffer = _bufferManager.CreateDeviceBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
                true);
            
            _meshletBuffer = _bufferManager.CreateDeviceBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            _meshletVertexIndexBuffer = _bufferManager.CreateDeviceBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            _meshletTriangleIndexBuffer = _bufferManager.CreateDeviceBuffer(
                size,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
        }
        
        private void EnsureBufferCapacity(ulong requiredSize)
        {
            ulong currentSize = _bufferManager.GetBufferSize(_vertexBuffer);
            if (requiredSize <= currentSize)
                return;
            
            // Grow buffer
            ulong newSize = Math.Max(currentSize * BufferGrowthFactor, requiredSize);
            
            // Create new buffers
            var newVertexBuffer = _bufferManager.CreateDeviceBuffer(
                newSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit,
                true);
            
            var newIndexBuffer = _bufferManager.CreateDeviceBuffer(
                newSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit,
                true);
            
            var newMeshletBuffer = _bufferManager.CreateDeviceBuffer(
                newSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            var newMeshletVertexIndexBuffer = _bufferManager.CreateDeviceBuffer(
                newSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            var newMeshletTriangleIndexBuffer = _bufferManager.CreateDeviceBuffer(
                newSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                true);
            
            // Copy old data to new buffers (TODO: implement copy)
            // For now, just replace
            _vertexBuffer = newVertexBuffer;
            _indexBuffer = newIndexBuffer;
            _meshletBuffer = newMeshletBuffer;
            _meshletVertexIndexBuffer = newMeshletVertexIndexBuffer;
            _meshletTriangleIndexBuffer = newMeshletTriangleIndexBuffer;
        }
        
        public MeshHandle RegisterMesh(
            Vector3[] vertices,
            uint[] indices,
            bool generateMeshlets = true)
        {
            lock (_lock)
            {
                int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _meshes.Count;
                
                // Calculate required sizes
                ulong vertexBufferSize = (ulong)vertices.Length * 12; // Vector3 = 12 bytes
                ulong indexBufferSize = (ulong)indices.Length * 4; // uint = 4 bytes
                ulong totalRequired = vertexBufferSize + indexBufferSize;
                
                EnsureBufferCapacity(totalRequired);
                
                // Get current buffer sizes and offsets
                ulong vertexBufferCurrentSize = _bufferManager.GetBufferSize(_vertexBuffer);
                ulong indexBufferCurrentSize = _bufferManager.GetBufferSize(_indexBuffer);
                ulong meshletBufferCurrentSize = _bufferManager.GetBufferSize(_meshletBuffer);
                ulong meshletVertexIndexCurrentSize = _bufferManager.GetBufferSize(_meshletVertexIndexBuffer);
                ulong meshletTriangleIndexCurrentSize = _bufferManager.GetBufferSize(_meshletTriangleIndexBuffer);
                
                // For now, store vertices and indices in consolidated buffers
                // TODO: Implement actual data upload
                
                var meshInfo = new MeshInfo
                {
                    VertexOffset = (uint)(vertexBufferCurrentSize / 12),
                    VertexCount = (uint)vertices.Length,
                    IndexOffset = (uint)(indexBufferCurrentSize / 4),
                    IndexCount = (uint)indices.Length
                };
                
                // Calculate bounding box
                if (vertices.Length > 0)
                {
                    meshInfo.BoundingBoxMin = vertices[0];
                    meshInfo.BoundingBoxMax = vertices[0];
                    for (int i = 1; i < vertices.Length; i++)
                    {
                        meshInfo.BoundingBoxMin = Vector3.Min(meshInfo.BoundingBoxMin, vertices[i]);
                        meshInfo.BoundingBoxMax = Vector3.Max(meshInfo.BoundingBoxMax, vertices[i]);
                    }
                }
                
                // Generate meshlets if requested
                if (generateMeshlets)
                {
                    var meshlets = GenerateMeshlets(vertices, indices, out var localVertexIndices, out var localTriangleIndices);
                    
                    meshInfo.MeshletOffset = 0; // TODO: track offset
                    meshInfo.MeshletCount = (uint)meshlets.Count;
                    meshInfo.LocalVertexIndexOffset = 0; // TODO: track offset
                    meshInfo.LocalVertexIndexCount = (uint)localVertexIndices.Count;
                    meshInfo.LocalTriangleIndexOffset = 0; // TODO: track offset
                    meshInfo.LocalTriangleIndexCount = (uint)localTriangleIndices.Count;
                }
                
                if (index == _meshes.Count)
                    _meshes.Add(meshInfo);
                else
                    _meshes[index] = meshInfo;
                
                return new MeshHandle(index, (uint)(_meshes.Count));
            }
        }
        
        private List<Meshlet> GenerateMeshlets(
            Vector3[] vertices,
            uint[] indices,
            out List<uint> localVertexIndices,
            out List<uint> localTriangleIndices)
        {
            localVertexIndices = new List<uint>();
            localTriangleIndices = new List<uint>();
            var meshlets = new List<Meshlet>();
            
            // Simple meshlet generation - group triangles into meshlets
            // This is a simplified version - a proper implementation would use a more sophisticated algorithm
            
            int triangleCount = indices.Length / 3;
            int vertexCount = vertices.Length;
            
            if (triangleCount == 0 || vertexCount == 0)
                return meshlets;
            
            // Temporary: Create one meshlet per 64 vertices or 126 triangles
            int meshletIndex = 0;
            int startTriangle = 0;
            
            while (startTriangle < triangleCount)
            {
                int endTriangle = Math.Min(startTriangle + MaxTrianglesPerMeshlet, triangleCount);
                int actualTriangleCount = endTriangle - startTriangle;
                
                // Collect unique vertices for this meshlet
                var uniqueVertices = new HashSet<int>();
                for (int t = startTriangle; t < endTriangle; t++)
                {
                    int i0 = (int)indices[t * 3 + 0];
                    int i1 = (int)indices[t * 3 + 1];
                    int i2 = (int)indices[t * 3 + 2];
                    uniqueVertices.Add(i0);
                    uniqueVertices.Add(i1);
                    uniqueVertices.Add(i2);
                }
                
                if (uniqueVertices.Count > MaxVerticesPerMeshlet)
                {
                    // Need to split - find a smaller group
                    endTriangle = startTriangle + Math.Max(1, actualTriangleCount / 2);
                    continue;
                }
                
                // Calculate bounding sphere
                var boundingSphere = CalculateBoundingSphere(vertices, uniqueVertices);
                
                // Create local vertex mapping
                var vertexMapping = new Dictionary<int, int>();
                int localIndex = 0;
                foreach (int globalIndex in uniqueVertices)
                {
                    vertexMapping[globalIndex] = localIndex++;
                }
                
                // Store local vertex indices
                int localVertexStart = localVertexIndices.Count;
                foreach (int globalIndex in uniqueVertices)
                {
                    localVertexIndices.Add((uint)globalIndex);
                }
                
                // Store local triangle indices
                int localTriangleStart = localTriangleIndices.Count;
                for (int t = startTriangle; t < endTriangle; t++)
                {
                    int i0 = (int)indices[t * 3 + 0];
                    int i1 = (int)indices[t * 3 + 1];
                    int i2 = (int)indices[t * 3 + 2];
                    
                    localTriangleIndices.Add((uint)vertexMapping[i0]);
                    localTriangleIndices.Add((uint)vertexMapping[i1]);
                    localTriangleIndices.Add((uint)vertexMapping[i2]);
                }
                
                // Create meshlet
                var meshlet = new Meshlet
                {
                    BoundingSphereCenter = boundingSphere.Center,
                    BoundingSphereRadius = boundingSphere.Radius,
                    VertexOffset = 0, // Will be set when uploading
                    VertexCount = (uint)uniqueVertices.Count,
                    IndexOffset = 0, // Will be set when uploading
                    IndexCount = (uint)(endTriangle - startTriangle) * 3,
                    LocalVertexOffset = (uint)localVertexStart,
                    LocalVertexCount = (uint)(localVertexIndices.Count - localVertexStart),
                    LocalTriangleOffset = (uint)localTriangleStart,
                    LocalTriangleCount = (uint)(localTriangleIndices.Count - localTriangleStart) / 3
                };
                
                meshlets.Add(meshlet);
                startTriangle = endTriangle;
            }
            
            return meshlets;
        }
        
        private struct BoundingSphere
        {
            public Vector3 Center;
            public float Radius;
        }
        
        private BoundingSphere CalculateBoundingSphere(Vector3[] vertices, HashSet<int> vertexIndices)
        {
            if (vertexIndices.Count == 0)
                return new BoundingSphere { Center = Vector3.Zero, Radius = 0 };
            
            // Calculate bounding box first
            var min = vertices[(int)vertexIndices.ToArray()[0]];
            var max = min;
            
            foreach (int idx in vertexIndices)
            {
                var v = vertices[idx];
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            
            // Bounding sphere from AABB
            Vector3 center = (min + max) * 0.5f;
            float radius = 0;
            
            foreach (int idx in vertexIndices)
            {
                var v = vertices[idx];
                float dist = Vector3.Distance(center, v);
                if (dist > radius)
                    radius = dist;
            }
            
            return new BoundingSphere { Center = center, Radius = radius };
        }
        
        public BufferHandle VertexBuffer => _vertexBuffer;
        public BufferHandle IndexBuffer => _indexBuffer;
        public BufferHandle MeshletBuffer => _meshletBuffer;
        public BufferHandle MeshletVertexIndexBuffer => _meshletVertexIndexBuffer;
        public BufferHandle MeshletTriangleIndexBuffer => _meshletTriangleIndexBuffer;
        
        public MeshInfo GetMeshInfo(MeshHandle handle)
        {
            lock (_lock)
            {
                if (!handle.IsValid || handle.Index >= _meshes.Count)
                    throw new InvalidOperationException("Invalid mesh handle");
                
                var meshInfo = _meshes[handle.Index];
                return meshInfo;
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
                if (_vertexBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_vertexBuffer);
                if (_indexBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_indexBuffer);
                if (_meshletBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_meshletBuffer);
                if (_meshletVertexIndexBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_meshletVertexIndexBuffer);
                if (_meshletTriangleIndexBuffer.IsValid)
                    _bufferManager.DestroyBuffer(_meshletTriangleIndexBuffer);
                
                _meshes.Clear();
                _freeIndices.Clear();
            }
            
            Console.WriteLine("Mesh manager disposed.");
        }
        
        ~MeshManager()
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

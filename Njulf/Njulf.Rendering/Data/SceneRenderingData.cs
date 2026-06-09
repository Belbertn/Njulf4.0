using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Memory;

namespace Njulf.Rendering.Data
{
    public class SceneRenderingData : IDisposable
    {
        public int FrameIndex { get; set; }
        public uint ImageIndex { get; set; }
        public Vector4 ClearColor { get; set; } = new(0.2f, 0.2f, 0.2f, 1f);
        public Matrix4x4 ViewMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 ViewProjectionMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 CameraPosition { get; set; } = Vector3.Zero;
        public int ObjectCount { get; set; }
        public int MeshletCount { get; set; }
        public int OpaqueObjectCount { get; set; }
        public int MaskedObjectCount { get; set; }
        public int TransparentObjectCount { get; set; }
        public int OpaqueMeshletCount { get; set; }
        public int TransparentMeshletCount { get; set; }
        public int BlendMaterialCount { get; set; }
        public int MaterialCount { get; set; }
        public int LightCount { get; set; }
        public int DirectionalLightCount { get; set; }
        public int LocalLightCount { get; set; }
        public int TextureCount { get; set; }
        public uint CurrentFrameIndex { get; set; }
        public uint ScreenWidth { get; set; }
        public uint ScreenHeight { get; set; }
        public uint TileCountX { get; set; }
        public uint TileCountY { get; set; }
        public uint HiZMipCount { get; set; }
        public bool OcclusionCullingEnabled { get; set; } = true;
        public float OcclusionBias { get; set; } = 0.0005f;
        public int MaxLightsPerTile { get; set; }
        public ulong UploadedBytes { get; set; }
        public ulong ObjectBufferSize { get; set; }
        public ulong MaterialBufferSize { get; set; }
        public ulong InstanceBufferSize { get; set; }
        public ulong MeshletDrawBufferSize { get; set; }
        public ulong TransparentMeshletDrawBufferSize { get; set; }
        public ulong TiledLightHeaderBufferSize { get; set; }
        public ulong TiledLightIndexBufferSize { get; set; }
        public BufferHandle ObjectDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MaterialDataBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle InstanceBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle MeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TransparentMeshletDrawBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightHeaderBuffer { get; set; } = BufferHandle.Invalid;
        public BufferHandle TiledLightIndexBuffer { get; set; } = BufferHandle.Invalid;
        public float Time { get; set; }
        
        public List<GPUMeshletDrawCommand> MeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> OpaqueMeshletDrawCommands { get; } = new();
        public List<GPUMeshletDrawCommand> TransparentMeshletDrawCommands { get; } = new();
        public List<GPUObjectData> ObjectData { get; } = new();
        public List<GPUMaterialData> MaterialData { get; } = new();
        
        private bool _disposed = false;
        
        public void Clear()
        {
            MeshletDrawCommands.Clear();
            OpaqueMeshletDrawCommands.Clear();
            TransparentMeshletDrawCommands.Clear();
            ObjectData.Clear();
            MaterialData.Clear();
            ObjectCount = 0;
            MeshletCount = 0;
            OpaqueObjectCount = 0;
            MaskedObjectCount = 0;
            TransparentObjectCount = 0;
            OpaqueMeshletCount = 0;
            TransparentMeshletCount = 0;
            BlendMaterialCount = 0;
            MaterialCount = 0;
            LightCount = 0;
            DirectionalLightCount = 0;
            LocalLightCount = 0;
            TextureCount = 0;
            UploadedBytes = 0;
        }
        
        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _disposed = true;
            }
        }
    }
}

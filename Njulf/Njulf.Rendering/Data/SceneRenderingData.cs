using System;
using System.Collections.Generic;
using Njulf.Core.Math;

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
        public int LightCount { get; set; }
        public int TextureCount { get; set; }
        public uint CurrentFrameIndex { get; set; }
        public uint ScreenWidth { get; set; }
        public uint ScreenHeight { get; set; }
        public float Time { get; set; }
        
        public List<GPUMeshletDrawCommand> MeshletDrawCommands { get; } = new();
        public List<GPUObjectData> ObjectData { get; } = new();
        public List<GPUMaterialData> MaterialData { get; } = new();
        
        private bool _disposed = false;
        
        public void Clear()
        {
            MeshletDrawCommands.Clear();
            ObjectData.Clear();
            MaterialData.Clear();
            ObjectCount = 0;
            MeshletCount = 0;
            LightCount = 0;
            TextureCount = 0;
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

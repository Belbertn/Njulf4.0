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
    
    [Serializable]
    public struct GPUMeshletDrawCommand
    {
        public uint MeshletIndex;
        public uint ObjectIndex;
        public uint MaterialIndex;
    }
    
    [Serializable]
    public struct GPUObjectData
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 WorldMatrixInverseTranspose;
        public int MeshIndex;
        public int MaterialIndex;
    }
    
    [Serializable]
    public struct GPUMaterialData
    {
        public Vector4 Albedo;
        public Vector4 Emissive;
        public float Metallic;
        public float Roughness;
        public float Ao;
        public int AlbedoTextureIndex;
        public int NormalTextureIndex;
        public int MetallicRoughnessTextureIndex;
        public int EmissiveTextureIndex;
    }
}

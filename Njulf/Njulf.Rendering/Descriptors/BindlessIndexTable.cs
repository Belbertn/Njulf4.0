using System;

namespace Njulf.Rendering.Descriptors
{
    /// <summary>
    /// Compile-time bindless index table that MUST match shader bindings exactly.
    /// This is a critical invariant - indices here must match the bindings in the shaders.
    /// </summary>
    public static class BindlessIndex
    {
        // ============================================
        // STORAGE BUFFER HEAP INDICES (0-14 reserved)
        // ============================================
        
        /// <summary>Object data buffer - per-object transforms and metadata</summary>
        public const int ObjectDataBuffer = 0;
        
        /// <summary>Material data buffer - PBR material parameters</summary>
        public const int MaterialDataBuffer = 1;
        
        /// <summary>Scene mesh metadata buffer</summary>
        public const int SceneMeshMetadataBuffer = 2;
        
        /// <summary>Consolidated vertex buffer</summary>
        public const int VertexBuffer = 3;
        
        /// <summary>Consolidated index buffer</summary>
        public const int IndexBuffer = 4;
        
        /// <summary>Meshlet descriptor buffer</summary>
        public const int MeshletBuffer = 5;
        
        /// <summary>Meshlet local vertex index buffer</summary>
        public const int MeshletVertexIndexBuffer = 6;
        
        /// <summary>Meshlet local triangle index buffer</summary>
        public const int MeshletTriangleIndexBuffer = 7;
        
        /// <summary>Instance buffer for frame 0 (base)</summary>
        public const int InstanceBufferBase = 8;
        
        /// <summary>Instance buffer for the second in-flight frame</summary>
        public const int InstanceBufferFrame1 = 9;
        
        /// <summary>Meshlet draw command buffer for frame 0 (base)</summary>
        public const int MeshletDrawBufferBase = 10;
        
        /// <summary>Meshlet draw command buffer for the second in-flight frame</summary>
        public const int MeshletDrawBufferFrame1 = 11;

        /// <summary>Transparent meshlet draw command buffer for frame 0 (base)</summary>
        public const int TransparentMeshletDrawBufferBase = 12;

        /// <summary>Transparent meshlet draw command buffer for the second in-flight frame</summary>
        public const int TransparentMeshletDrawBufferFrame1 = 13;
        
        /// <summary>GPU light buffer</summary>
        public const int LightBuffer = 14;
        
        /// <summary>Tiled light culling header buffer</summary>
        public const int TiledLightHeaderBuffer = 15;
        
        /// <summary>Tiled light culling indices buffer</summary>
        public const int TiledLightIndicesBuffer = 16;
        
        // ============================================
        // TEXTURE HEAP INDICES (dynamic allocation)
        // ============================================
        
        /// <summary>First available texture index</summary>
        public const int FirstTextureIndex = 0;

        /// <summary>Default white texture used for missing albedo textures</summary>
        public const int DefaultWhiteTexture = 0;

        /// <summary>Default normal texture used for missing normal maps</summary>
        public const int DefaultNormalTexture = 1;

        /// <summary>Default black texture used for missing ORM/emissive textures</summary>
        public const int DefaultBlackTexture = 2;

        /// <summary>Depth prepass texture sampled by Forward+ light culling</summary>
        public const int DepthTexture = 3;

        /// <summary>Reverse-Z Hi-Z depth pyramid sampled by forward task culling</summary>
        public const int HiZDepthTexture = 4;

        /// <summary>First dynamically allocated material texture index</summary>
        public const int FirstDynamicTextureIndex = 5;
        
        /// <summary>Maximum number of textures</summary>
        public const int MaxTextures = 65536;
        
        // ============================================
        // STATIC BUFFER COUNT (for validation)
        // ============================================
        
        /// <summary>Number of static (fixed-index) buffers</summary>
        public const int StaticBufferCount = 17;
        
        // ============================================
        // UTILITY METHODS
        // ============================================
        
        /// <summary>Validates that an index is a static buffer index</summary>
        public static bool IsStaticBufferIndex(int index)
        {
            return index >= 0 && index < StaticBufferCount;
        }
        
        /// <summary>Validates that an index is a texture index</summary>
        public static bool IsTextureIndex(int index)
        {
            return index >= FirstTextureIndex && index < FirstTextureIndex + MaxTextures;
        }
        
        /// <summary>
        /// Gets the name of a storage-buffer bindless index.
        /// Texture indices live in a separate descriptor set and can overlap these values.
        /// </summary>
        public static string GetIndexName(int index)
        {
            if (index >= 0 && index < StaticBufferCount)
            {
                return index switch
                {
                    ObjectDataBuffer => nameof(ObjectDataBuffer),
                    MaterialDataBuffer => nameof(MaterialDataBuffer),
                    SceneMeshMetadataBuffer => nameof(SceneMeshMetadataBuffer),
                    VertexBuffer => nameof(VertexBuffer),
                    IndexBuffer => nameof(IndexBuffer),
                    MeshletBuffer => nameof(MeshletBuffer),
                    MeshletVertexIndexBuffer => nameof(MeshletVertexIndexBuffer),
                    MeshletTriangleIndexBuffer => nameof(MeshletTriangleIndexBuffer),
                    InstanceBufferBase => nameof(InstanceBufferBase),
                    InstanceBufferFrame1 => nameof(InstanceBufferFrame1),
                    MeshletDrawBufferBase => nameof(MeshletDrawBufferBase),
                    MeshletDrawBufferFrame1 => nameof(MeshletDrawBufferFrame1),
                    TransparentMeshletDrawBufferBase => nameof(TransparentMeshletDrawBufferBase),
                    TransparentMeshletDrawBufferFrame1 => nameof(TransparentMeshletDrawBufferFrame1),
                    LightBuffer => nameof(LightBuffer),
                    TiledLightHeaderBuffer => nameof(TiledLightHeaderBuffer),
                    TiledLightIndicesBuffer => nameof(TiledLightIndicesBuffer),
                    _ => "Unknown"
                };
            }

            if (IsTextureIndex(index))
            {
                return $"Texture {index - FirstTextureIndex}";
            }
            
            return "Unknown";
        }

        /// <summary>Gets the name of a texture bindless index.</summary>
        public static string GetTextureIndexName(int index)
        {
            return IsTextureIndex(index)
                ? $"Texture {index - FirstTextureIndex}"
                : "Unknown";
        }
    }
}

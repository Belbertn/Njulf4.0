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

        /// <summary>Renderer diagnostics counters for frame 0</summary>
        public const int RendererDiagnosticsBufferBase = 17;

        /// <summary>Renderer diagnostics counters for the second in-flight frame</summary>
        public const int RendererDiagnosticsBufferFrame1 = 18;

        /// <summary>Directional shadow matrices and settings</summary>
        public const int DirectionalShadowDataBuffer = 19;

        /// <summary>Directional shadow meshlet draw buffer for frame 0</summary>
        public const int DirectionalShadowMeshletDrawBufferBase = 20;

        public const int DirectionalShadowMeshletDrawBufferCount = 2;

        /// <summary>Spot light shadow matrices and atlas metadata</summary>
        public const int SpotShadowDataBuffer = DirectionalShadowMeshletDrawBufferBase + DirectionalShadowMeshletDrawBufferCount;

        /// <summary>Point light shadow matrices and cubemap metadata</summary>
        public const int PointShadowDataBuffer = SpotShadowDataBuffer + 1;

        /// <summary>Per-light mapping to local shadow metadata</summary>
        public const int LocalLightShadowIndexBuffer = PointShadowDataBuffer + 1;

        /// <summary>Local shadow meshlet draw buffer for frame 0</summary>
        public const int LocalShadowMeshletDrawBufferBase = LocalLightShadowIndexBuffer + 1;

        public const int LocalShadowMeshletDrawBufferCount = 2;

        /// <summary>Global environment settings and texture indices</summary>
        public const int EnvironmentDataBuffer = LocalShadowMeshletDrawBufferBase + LocalShadowMeshletDrawBufferCount;
        
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

        /// <summary>HDR scene color sampled by the final composite pass</summary>
        public const int HdrSceneColorTexture = 5;

        /// <summary>First fixed bloom mip texture sampled by the composite pass</summary>
        public const int BloomMipTextureBase = 6;

        /// <summary>Maximum number of fixed bloom mip textures</summary>
        public const int MaxBloomMipTextures = 8;

        /// <summary>First fixed directional shadow cascade texture</summary>
        public const int DirectionalShadowTextureBase = BloomMipTextureBase + MaxBloomMipTextures;

        /// <summary>Maximum number of fixed directional shadow cascade textures</summary>
        public const int MaxDirectionalShadowTextures = 4;

        /// <summary>Fixed sampled spot shadow atlas texture</summary>
        public const int SpotShadowAtlasTexture = DirectionalShadowTextureBase + MaxDirectionalShadowTextures;

        /// <summary>Fixed sampled point shadow cubemap-array texture</summary>
        public const int PointShadowCubemapArrayTexture = SpotShadowAtlasTexture + 1;

        /// <summary>Fixed sampled sky/environment cubemap texture</summary>
        public const int EnvironmentCubemapTexture = PointShadowCubemapArrayTexture + 1;

        /// <summary>Fixed sampled diffuse irradiance cubemap texture</summary>
        public const int IrradianceCubemapTexture = EnvironmentCubemapTexture + 1;

        /// <summary>Fixed sampled roughness-prefiltered environment cubemap texture</summary>
        public const int PrefilteredEnvironmentTexture = IrradianceCubemapTexture + 1;

        /// <summary>Fixed sampled split-sum BRDF integration LUT texture</summary>
        public const int BrdfLutTexture = PrefilteredEnvironmentTexture + 1;

        /// <summary>Fixed sampled raw ambient occlusion texture</summary>
        public const int AmbientOcclusionRawTexture = BrdfLutTexture + 1;

        /// <summary>Fixed sampled blurred ambient occlusion texture</summary>
        public const int AmbientOcclusionBlurredTexture = AmbientOcclusionRawTexture + 1;

        /// <summary>Reserved fixed sampled scene normal texture</summary>
        public const int SceneNormalTexture = AmbientOcclusionBlurredTexture + 1;

        /// <summary>Fixed sampled post-tone-map LDR scene color texture</summary>
        public const int LdrSceneColorTexture = SceneNormalTexture + 1;

        /// <summary>Fixed sampled SMAA edge mask texture</summary>
        public const int SmaaEdgesTexture = LdrSceneColorTexture + 1;

        /// <summary>Fixed sampled SMAA blend weights texture</summary>
        public const int SmaaBlendWeightsTexture = SmaaEdgesTexture + 1;

        /// <summary>Fixed sampled SMAA area lookup texture</summary>
        public const int SmaaAreaTexture = SmaaBlendWeightsTexture + 1;

        /// <summary>Fixed sampled SMAA search lookup texture</summary>
        public const int SmaaSearchTexture = SmaaAreaTexture + 1;

        /// <summary>Fixed sampled motion vector texture reserved for temporal AA</summary>
        public const int MotionVectorTexture = SmaaSearchTexture + 1;

        /// <summary>Fixed sampled TAA history texture reserved for temporal AA</summary>
        public const int TaaHistoryTexture = MotionVectorTexture + 1;

        /// <summary>First dynamically allocated material texture index</summary>
        public const int FirstDynamicTextureIndex = TaaHistoryTexture + 1;
        
        /// <summary>Maximum number of textures</summary>
        public const int MaxTextures = 65536;
        
        // ============================================
        // STATIC BUFFER COUNT (for validation)
        // ============================================
        
        /// <summary>Number of static (fixed-index) buffers</summary>
        public const int StaticBufferCount = EnvironmentDataBuffer + 1;
        
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
                    RendererDiagnosticsBufferBase => nameof(RendererDiagnosticsBufferBase),
                    RendererDiagnosticsBufferFrame1 => nameof(RendererDiagnosticsBufferFrame1),
                    DirectionalShadowDataBuffer => nameof(DirectionalShadowDataBuffer),
                    >= DirectionalShadowMeshletDrawBufferBase and < SpotShadowDataBuffer => nameof(DirectionalShadowMeshletDrawBufferBase),
                    SpotShadowDataBuffer => nameof(SpotShadowDataBuffer),
                    PointShadowDataBuffer => nameof(PointShadowDataBuffer),
                    LocalLightShadowIndexBuffer => nameof(LocalLightShadowIndexBuffer),
                    >= LocalShadowMeshletDrawBufferBase and < EnvironmentDataBuffer => nameof(LocalShadowMeshletDrawBufferBase),
                    EnvironmentDataBuffer => nameof(EnvironmentDataBuffer),
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

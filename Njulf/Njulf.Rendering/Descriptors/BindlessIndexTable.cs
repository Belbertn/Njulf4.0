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

        /// <summary>Optional material extension payload buffer</summary>
        public const int MaterialExtensionDataBuffer = 44;
        
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

        /// <summary>Reflection probe settings and local probe metadata</summary>
        public const int ReflectionProbeBuffer = EnvironmentDataBuffer + 1;

        /// <summary>Solid-only depth meshlet draw buffer for frame 0</summary>
        public const int SolidDepthMeshletDrawBufferBase = ReflectionProbeBuffer + 1;

        /// <summary>Solid-only depth meshlet draw buffer for the second in-flight frame</summary>
        public const int SolidDepthMeshletDrawBufferFrame1 = SolidDepthMeshletDrawBufferBase + 1;

        /// <summary>Masked alpha-test depth meshlet draw buffer for frame 0</summary>
        public const int MaskedDepthMeshletDrawBufferBase = SolidDepthMeshletDrawBufferFrame1 + 1;

        /// <summary>Masked alpha-test depth meshlet draw buffer for the second in-flight frame</summary>
        public const int MaskedDepthMeshletDrawBufferFrame1 = MaskedDepthMeshletDrawBufferBase + 1;

        /// <summary>Per-source-vertex joint and weight data for skinned meshes</summary>
        public const int SkinningVertexDataBuffer = MaskedDepthMeshletDrawBufferFrame1 + 1;

        /// <summary>Skinning matrix buffer for frame 0</summary>
        public const int SkinMatrixBufferBase = SkinningVertexDataBuffer + 1;

        /// <summary>Skinning matrix buffer for the second in-flight frame</summary>
        public const int SkinMatrixBufferFrame1 = SkinMatrixBufferBase + 1;

        /// <summary>Compute-skinned vertex output buffer for frame 0</summary>
        public const int SkinnedVertexBufferBase = SkinMatrixBufferFrame1 + 1;

        /// <summary>Compute-skinned vertex output buffer for the second in-flight frame</summary>
        public const int SkinnedVertexBufferFrame1 = SkinnedVertexBufferBase + 1;

        /// <summary>Skinning dispatch records for frame 0</summary>
        public const int SkinningDispatchBufferBase = SkinnedVertexBufferFrame1 + 1;

        /// <summary>Skinning dispatch records for the second in-flight frame</summary>
        public const int SkinningDispatchBufferFrame1 = SkinningDispatchBufferBase + 1;

        /// <summary>Particle instance buffer for frame 0</summary>
        public const int ParticleInstanceBufferBase = SkinningDispatchBufferFrame1 + 1;

        /// <summary>Particle instance buffer for the second in-flight frame</summary>
        public const int ParticleInstanceBufferFrame1 = ParticleInstanceBufferBase + 1;

        /// <summary>Particle batch buffer for frame 0</summary>
        public const int ParticleBatchBufferBase = ParticleInstanceBufferFrame1 + 1;

        /// <summary>Particle batch buffer for the second in-flight frame</summary>
        public const int ParticleBatchBufferFrame1 = ParticleBatchBufferBase + 1;

        /// <summary>Auto-exposure luminance histogram buffer for frame 0</summary>
        public const int AutoExposureHistogramBufferBase = MaterialExtensionDataBuffer + 1;

        /// <summary>Auto-exposure luminance histogram buffer for the second in-flight frame</summary>
        public const int AutoExposureHistogramBufferFrame1 = AutoExposureHistogramBufferBase + 1;

        /// <summary>Auto-exposure adapted exposure state buffer for frame 0</summary>
        public const int AutoExposureStateBufferBase = AutoExposureHistogramBufferFrame1 + 1;

        /// <summary>Auto-exposure adapted exposure state buffer for the second in-flight frame</summary>
        public const int AutoExposureStateBufferFrame1 = AutoExposureStateBufferBase + 1;

        /// <summary>Packed opaque meshlet task-culling draw data for frame 0</summary>
        public const int PackedMeshletDrawBufferBase = AutoExposureStateBufferFrame1 + 1;

        /// <summary>Packed opaque meshlet task-culling draw data for the second in-flight frame</summary>
        public const int PackedMeshletDrawBufferFrame1 = PackedMeshletDrawBufferBase + 1;

        /// <summary>Packed solid depth meshlet task-culling draw data for frame 0</summary>
        public const int PackedSolidDepthMeshletDrawBufferBase = PackedMeshletDrawBufferFrame1 + 1;

        /// <summary>Packed solid depth meshlet task-culling draw data for the second in-flight frame</summary>
        public const int PackedSolidDepthMeshletDrawBufferFrame1 = PackedSolidDepthMeshletDrawBufferBase + 1;

        /// <summary>Packed masked depth meshlet task-culling draw data for frame 0</summary>
        public const int PackedMaskedDepthMeshletDrawBufferBase = PackedSolidDepthMeshletDrawBufferFrame1 + 1;

        /// <summary>Packed masked depth meshlet task-culling draw data for the second in-flight frame</summary>
        public const int PackedMaskedDepthMeshletDrawBufferFrame1 = PackedMaskedDepthMeshletDrawBufferBase + 1;

        /// <summary>Per-frame task-culling constants for frame 0</summary>
        public const int MeshletTaskFrameDataBufferBase = PackedMaskedDepthMeshletDrawBufferFrame1 + 1;

        /// <summary>Per-frame task-culling constants for the second in-flight frame</summary>
        public const int MeshletTaskFrameDataBufferFrame1 = MeshletTaskFrameDataBufferBase + 1;

        /// <summary>Full opaque meshlet draw command buffer for frame 0</summary>
        public const int FullOpaqueMeshletDrawBufferBase = MeshletTaskFrameDataBufferFrame1 + 1;

        /// <summary>Full opaque meshlet draw command buffer for the second in-flight frame</summary>
        public const int FullOpaqueMeshletDrawBufferFrame1 = FullOpaqueMeshletDrawBufferBase + 1;

        /// <summary>Packed full opaque meshlet task-culling draw data for frame 0</summary>
        public const int PackedFullOpaqueMeshletDrawBufferBase = FullOpaqueMeshletDrawBufferFrame1 + 1;

        /// <summary>Packed full opaque meshlet task-culling draw data for the second in-flight frame</summary>
        public const int PackedFullOpaqueMeshletDrawBufferFrame1 = PackedFullOpaqueMeshletDrawBufferBase + 1;

        /// <summary>Split static vertex position stream</summary>
        public const int VertexPositionBuffer = PackedFullOpaqueMeshletDrawBufferFrame1 + 1;

        /// <summary>Split static vertex normal/tangent stream</summary>
        public const int VertexNormalTangentBuffer = VertexPositionBuffer + 1;

        /// <summary>Split static vertex UV/color stream</summary>
        public const int VertexUvColorBuffer = VertexNormalTangentBuffer + 1;

        /// <summary>Static directional shadow meshlet draw buffer for frame 0</summary>
        public const int DirectionalStaticShadowMeshletDrawBufferBase = VertexUvColorBuffer + 1;

        /// <summary>Static directional shadow meshlet draw buffer for the second in-flight frame</summary>
        public const int DirectionalStaticShadowMeshletDrawBufferFrame1 = DirectionalStaticShadowMeshletDrawBufferBase + 1;

        /// <summary>Dynamic directional shadow meshlet draw buffer for frame 0</summary>
        public const int DirectionalDynamicShadowMeshletDrawBufferBase = DirectionalStaticShadowMeshletDrawBufferFrame1 + 1;

        /// <summary>Dynamic directional shadow meshlet draw buffer for the second in-flight frame</summary>
        public const int DirectionalDynamicShadowMeshletDrawBufferFrame1 = DirectionalDynamicShadowMeshletDrawBufferBase + 1;

        /// <summary>Static local shadow meshlet draw buffer for frame 0</summary>
        public const int LocalStaticShadowMeshletDrawBufferBase = DirectionalDynamicShadowMeshletDrawBufferFrame1 + 1;

        /// <summary>Static local shadow meshlet draw buffer for the second in-flight frame</summary>
        public const int LocalStaticShadowMeshletDrawBufferFrame1 = LocalStaticShadowMeshletDrawBufferBase + 1;

        /// <summary>Dynamic local shadow meshlet draw buffer for frame 0</summary>
        public const int LocalDynamicShadowMeshletDrawBufferBase = LocalStaticShadowMeshletDrawBufferFrame1 + 1;

        /// <summary>Dynamic local shadow meshlet draw buffer for the second in-flight frame</summary>
        public const int LocalDynamicShadowMeshletDrawBufferFrame1 = LocalDynamicShadowMeshletDrawBufferBase + 1;

        /// <summary>Particle frame constants for frame 0</summary>
        public const int ParticleFrameDataBufferBase = LocalDynamicShadowMeshletDrawBufferFrame1 + 1;

        /// <summary>Particle frame constants for the second in-flight frame</summary>
        public const int ParticleFrameDataBufferFrame1 = ParticleFrameDataBufferBase + 1;

        /// <summary>GPU particle simulation state buffer for frame 0</summary>
        public const int GpuParticleStateBufferBase = ParticleFrameDataBufferFrame1 + 1;

        /// <summary>GPU particle simulation state buffer for the second in-flight frame</summary>
        public const int GpuParticleStateBufferFrame1 = GpuParticleStateBufferBase + 1;

        /// <summary>GPU particle alive-index buffer for frame 0</summary>
        public const int GpuParticleAliveIndexBufferBase = GpuParticleStateBufferFrame1 + 1;

        /// <summary>GPU particle alive-index buffer for the second in-flight frame</summary>
        public const int GpuParticleAliveIndexBufferFrame1 = GpuParticleAliveIndexBufferBase + 1;

        /// <summary>GPU particle free/dead index stack</summary>
        public const int GpuParticleDeadIndexBuffer = GpuParticleAliveIndexBufferFrame1 + 1;

        /// <summary>GPU particle emitter buffer for frame 0</summary>
        public const int GpuParticleEmitterBufferBase = GpuParticleDeadIndexBuffer + 1;

        /// <summary>GPU particle emitter buffer for the second in-flight frame</summary>
        public const int GpuParticleEmitterBufferFrame1 = GpuParticleEmitterBufferBase + 1;

        /// <summary>GPU particle counter buffer for frame 0</summary>
        public const int GpuParticleCounterBufferBase = GpuParticleEmitterBufferFrame1 + 1;

        /// <summary>GPU particle counter buffer for the second in-flight frame</summary>
        public const int GpuParticleCounterBufferFrame1 = GpuParticleCounterBufferBase + 1;

        /// <summary>GPU-built particle render instance buffer for frame 0</summary>
        public const int GpuParticleRenderInstanceBufferBase = GpuParticleCounterBufferFrame1 + 1;

        /// <summary>GPU-built particle render instance buffer for the second in-flight frame</summary>
        public const int GpuParticleRenderInstanceBufferFrame1 = GpuParticleRenderInstanceBufferBase + 1;

        /// <summary>GPU-built particle indirect draw buffer for frame 0</summary>
        public const int GpuParticleIndirectDrawBufferBase = GpuParticleRenderInstanceBufferFrame1 + 1;

        /// <summary>GPU-built particle indirect draw buffer for the second in-flight frame</summary>
        public const int GpuParticleIndirectDrawBufferFrame1 = GpuParticleIndirectDrawBufferBase + 1;

        /// <summary>GPU particle curve/gradient sample buffer for frame 0</summary>
        public const int GpuParticleCurveSampleBufferBase = GpuParticleIndirectDrawBufferFrame1 + 1;

        /// <summary>GPU particle curve/gradient sample buffer for the second in-flight frame</summary>
        public const int GpuParticleCurveSampleBufferFrame1 = GpuParticleCurveSampleBufferBase + 1;

        /// <summary>GPU particle unsorted render instance buffer for frame 0</summary>
        public const int GpuParticleUnsortedRenderInstanceBufferBase = GpuParticleCurveSampleBufferFrame1 + 1;

        /// <summary>GPU particle unsorted render instance buffer for the second in-flight frame</summary>
        public const int GpuParticleUnsortedRenderInstanceBufferFrame1 = GpuParticleUnsortedRenderInstanceBufferBase + 1;

        /// <summary>GPU particle sort key/index buffer for frame 0</summary>
        public const int GpuParticleSortKeyBufferBase = GpuParticleUnsortedRenderInstanceBufferFrame1 + 1;

        /// <summary>GPU particle sort key/index buffer for the second in-flight frame</summary>
        public const int GpuParticleSortKeyBufferFrame1 = GpuParticleSortKeyBufferBase + 1;
        
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

        /// <summary>Fixed sampled HDR scene color after analytic fog composition</summary>
        public const int FoggedSceneColorTexture = TaaHistoryTexture + 1;

        /// <summary>Fixed sampled local reflection probe cubemap array texture</summary>
        public const int ReflectionProbeCubemapArrayTexture = FoggedSceneColorTexture + 1;

        /// <summary>Fixed sampled reflection debug preview texture</summary>
        public const int ReflectionProbeDebugTexture = ReflectionProbeCubemapArrayTexture + 1;

        /// <summary>First dynamically allocated material texture index</summary>
        public const int FirstDynamicTextureIndex = ReflectionProbeDebugTexture + 1;
        
        /// <summary>Maximum number of textures</summary>
        public const int MaxTextures = 65536;
        
        // ============================================
        // STATIC BUFFER COUNT (for validation)
        // ============================================
        
        /// <summary>Number of static (fixed-index) buffers</summary>
        public const int StaticBufferCount = GpuParticleSortKeyBufferFrame1 + 1;
        
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
                    MaterialExtensionDataBuffer => nameof(MaterialExtensionDataBuffer),
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
                    ReflectionProbeBuffer => nameof(ReflectionProbeBuffer),
                    SolidDepthMeshletDrawBufferBase => nameof(SolidDepthMeshletDrawBufferBase),
                    SolidDepthMeshletDrawBufferFrame1 => nameof(SolidDepthMeshletDrawBufferFrame1),
                    MaskedDepthMeshletDrawBufferBase => nameof(MaskedDepthMeshletDrawBufferBase),
                    MaskedDepthMeshletDrawBufferFrame1 => nameof(MaskedDepthMeshletDrawBufferFrame1),
                    SkinningVertexDataBuffer => nameof(SkinningVertexDataBuffer),
                    SkinMatrixBufferBase => nameof(SkinMatrixBufferBase),
                    SkinMatrixBufferFrame1 => nameof(SkinMatrixBufferFrame1),
                    SkinnedVertexBufferBase => nameof(SkinnedVertexBufferBase),
                    SkinnedVertexBufferFrame1 => nameof(SkinnedVertexBufferFrame1),
                    SkinningDispatchBufferBase => nameof(SkinningDispatchBufferBase),
                    SkinningDispatchBufferFrame1 => nameof(SkinningDispatchBufferFrame1),
                    ParticleInstanceBufferBase => nameof(ParticleInstanceBufferBase),
                    ParticleInstanceBufferFrame1 => nameof(ParticleInstanceBufferFrame1),
                    ParticleBatchBufferBase => nameof(ParticleBatchBufferBase),
                    ParticleBatchBufferFrame1 => nameof(ParticleBatchBufferFrame1),
                    AutoExposureHistogramBufferBase => nameof(AutoExposureHistogramBufferBase),
                    AutoExposureHistogramBufferFrame1 => nameof(AutoExposureHistogramBufferFrame1),
                    AutoExposureStateBufferBase => nameof(AutoExposureStateBufferBase),
                    AutoExposureStateBufferFrame1 => nameof(AutoExposureStateBufferFrame1),
                    PackedMeshletDrawBufferBase => nameof(PackedMeshletDrawBufferBase),
                    PackedMeshletDrawBufferFrame1 => nameof(PackedMeshletDrawBufferFrame1),
                    PackedSolidDepthMeshletDrawBufferBase => nameof(PackedSolidDepthMeshletDrawBufferBase),
                    PackedSolidDepthMeshletDrawBufferFrame1 => nameof(PackedSolidDepthMeshletDrawBufferFrame1),
                    PackedMaskedDepthMeshletDrawBufferBase => nameof(PackedMaskedDepthMeshletDrawBufferBase),
                    PackedMaskedDepthMeshletDrawBufferFrame1 => nameof(PackedMaskedDepthMeshletDrawBufferFrame1),
                    MeshletTaskFrameDataBufferBase => nameof(MeshletTaskFrameDataBufferBase),
                    MeshletTaskFrameDataBufferFrame1 => nameof(MeshletTaskFrameDataBufferFrame1),
                    FullOpaqueMeshletDrawBufferBase => nameof(FullOpaqueMeshletDrawBufferBase),
                    FullOpaqueMeshletDrawBufferFrame1 => nameof(FullOpaqueMeshletDrawBufferFrame1),
                    PackedFullOpaqueMeshletDrawBufferBase => nameof(PackedFullOpaqueMeshletDrawBufferBase),
                    PackedFullOpaqueMeshletDrawBufferFrame1 => nameof(PackedFullOpaqueMeshletDrawBufferFrame1),
                    VertexPositionBuffer => nameof(VertexPositionBuffer),
                    VertexNormalTangentBuffer => nameof(VertexNormalTangentBuffer),
                    VertexUvColorBuffer => nameof(VertexUvColorBuffer),
                    DirectionalStaticShadowMeshletDrawBufferBase => nameof(DirectionalStaticShadowMeshletDrawBufferBase),
                    DirectionalStaticShadowMeshletDrawBufferFrame1 => nameof(DirectionalStaticShadowMeshletDrawBufferFrame1),
                    DirectionalDynamicShadowMeshletDrawBufferBase => nameof(DirectionalDynamicShadowMeshletDrawBufferBase),
                    DirectionalDynamicShadowMeshletDrawBufferFrame1 => nameof(DirectionalDynamicShadowMeshletDrawBufferFrame1),
                    LocalStaticShadowMeshletDrawBufferBase => nameof(LocalStaticShadowMeshletDrawBufferBase),
                    LocalStaticShadowMeshletDrawBufferFrame1 => nameof(LocalStaticShadowMeshletDrawBufferFrame1),
                    LocalDynamicShadowMeshletDrawBufferBase => nameof(LocalDynamicShadowMeshletDrawBufferBase),
                    LocalDynamicShadowMeshletDrawBufferFrame1 => nameof(LocalDynamicShadowMeshletDrawBufferFrame1),
                    ParticleFrameDataBufferBase => nameof(ParticleFrameDataBufferBase),
                    ParticleFrameDataBufferFrame1 => nameof(ParticleFrameDataBufferFrame1),
                    GpuParticleStateBufferBase => nameof(GpuParticleStateBufferBase),
                    GpuParticleStateBufferFrame1 => nameof(GpuParticleStateBufferFrame1),
                    GpuParticleAliveIndexBufferBase => nameof(GpuParticleAliveIndexBufferBase),
                    GpuParticleAliveIndexBufferFrame1 => nameof(GpuParticleAliveIndexBufferFrame1),
                    GpuParticleDeadIndexBuffer => nameof(GpuParticleDeadIndexBuffer),
                    GpuParticleEmitterBufferBase => nameof(GpuParticleEmitterBufferBase),
                    GpuParticleEmitterBufferFrame1 => nameof(GpuParticleEmitterBufferFrame1),
                    GpuParticleCounterBufferBase => nameof(GpuParticleCounterBufferBase),
                    GpuParticleCounterBufferFrame1 => nameof(GpuParticleCounterBufferFrame1),
                    GpuParticleRenderInstanceBufferBase => nameof(GpuParticleRenderInstanceBufferBase),
                    GpuParticleRenderInstanceBufferFrame1 => nameof(GpuParticleRenderInstanceBufferFrame1),
                    GpuParticleIndirectDrawBufferBase => nameof(GpuParticleIndirectDrawBufferBase),
                    GpuParticleIndirectDrawBufferFrame1 => nameof(GpuParticleIndirectDrawBufferFrame1),
                    GpuParticleCurveSampleBufferBase => nameof(GpuParticleCurveSampleBufferBase),
                    GpuParticleCurveSampleBufferFrame1 => nameof(GpuParticleCurveSampleBufferFrame1),
                    GpuParticleUnsortedRenderInstanceBufferBase => nameof(GpuParticleUnsortedRenderInstanceBufferBase),
                    GpuParticleUnsortedRenderInstanceBufferFrame1 => nameof(GpuParticleUnsortedRenderInstanceBufferFrame1),
                    GpuParticleSortKeyBufferBase => nameof(GpuParticleSortKeyBufferBase),
                    GpuParticleSortKeyBufferFrame1 => nameof(GpuParticleSortKeyBufferFrame1),
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

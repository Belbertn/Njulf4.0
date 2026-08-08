using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// GPU structs that MUST match shader definitions exactly.
    /// These are laid out for 4-byte alignment and used in bindless resource access.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUVertex
    {
        public static Vector4 DefaultColor => new Vector4(1f, 1f, 1f, 1f);

        public Vector3 Position;
        public float Padding0;
        public Vector3 Normal;
        public float Padding1;
        public Vector2 TexCoord;
        public Vector2 TexCoord2;
        public Vector4 Tangent;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUVertexPositionStream
    {
        public Vector4 Position;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUVertexNormalTangentStream
    {
        public Vector4 Normal;
        public Vector4 Tangent;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUVertexUvColorStream
    {
        public Vector2 TexCoord;
        public Vector2 TexCoord2;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMeshInfo
    {
        public Vector4 BoundingSphere;
        public uint SkinningDataOffset;
        public uint SkinningDataCount;
        public uint Flags;
        public uint MeshletOffset;
        public uint MeshletCount;
        public uint MeshletLod1Offset;
        public uint MeshletLod1Count;
        public uint MeshletLod2Offset;
        public uint MeshletLod2Count;
        public uint MeshletLodGeneratedCount;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUVertexSkinningData
    {
        public uint Joint0;
        public uint Joint1;
        public uint Joint2;
        public uint Joint3;
        public float Weight0;
        public float Weight1;
        public float Weight2;
        public float Weight3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSkinningDispatch
    {
        public uint SourceVertexOffset;
        public uint SourceSkinningDataOffset;
        public uint DestinationVertexOffset;
        public uint VertexCount;
        public uint SkinMatrixOffset;
        public uint ObjectIndex;
        public uint SourceMeshMetadataIndex;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSkinningPushConstants
    {
        public uint DispatchIndex;
        public uint CurrentFrameIndex;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleInstance
    {
        public Vector4 PositionSize;
        public Vector4 VelocityRotation;
        public Vector4 Color;
        public Vector4 EmissiveLifetimeSoftClip;
        public uint TextureIndex;
        public uint FlipbookFrame;
        public uint FlipbookColumns;
        public uint FlipbookRows;
        public uint BlendMode;
        public uint BillboardMode;
        public uint DebugId;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleBatch
    {
        public uint Start;
        public uint Count;
        public uint BlendMode;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleFrameData
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 InverseProjectionMatrix;
        public Vector3 CameraPosition;
        public float GlobalSoftParticleDistance;
        public Vector2 ScreenDimensions;
        public Vector2 Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticlePushConstants
    {
        public uint CurrentFrameIndex;
        public uint ParticleInstanceBufferBaseIndex;
        public uint ParticleFrameDataBufferBaseIndex;
        public uint DepthTextureIndex;
        public uint DebugView;
        public uint SoftParticlesEnabled;
        public uint InstanceOffset;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleEmitter
    {
        public Matrix4x4 WorldMatrix;
        public Vector4 SpawnShape0;
        public Vector4 SpawnShape1;
        public Vector4 InitialVelocityMin;
        public Vector4 InitialVelocityMax;
        public Vector4 AccelerationDrag;
        public Vector4 LifetimeSize;
        public Vector4 Color;
        public uint MaterialIndex;
        public uint MaxParticles;
        public uint RandomSeed;
        public uint Flags;
        public Vector4 ColorEnd;
        public Vector4 EmissiveAngularVelocity;
        public Vector4 RotationParams;
        public Vector4 TimingParams;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleCurveSample
    {
        public Vector4 Color;
        public Vector4 Properties;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleState
    {
        public Vector4 PositionAge;
        public Vector4 VelocityLifetime;
        public Vector4 Color;
        public Vector4 SizeRotation;
        public uint EmitterIndex;
        public uint StableId;
        public uint RandomSeed;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleCounters
    {
        public uint AliveCount;
        public uint DeadCount;
        public uint SpawnedCount;
        public uint KilledCount;
        public uint CulledCount;
        public uint RenderedCount;
        public uint DroppedSpawnCount;
        public uint BlendBucket0Count;
        public uint BlendBucket1Count;
        public uint BlendBucket2Count;
        public uint BlendBucket3Count;
        public uint BlendBucket4Count;
        public uint BlendBucket0WriteCount;
        public uint BlendBucket1WriteCount;
        public uint BlendBucket2WriteCount;
        public uint BlendBucket3WriteCount;
        public uint BlendBucket4WriteCount;
        public uint BlendBucket0Offset;
        public uint BlendBucket1Offset;
        public uint BlendBucket2Offset;
        public uint BlendBucket3Offset;
        public uint BlendBucket4Offset;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleDrawCommand
    {
        public uint VertexCount;
        public uint InstanceCount;
        public uint FirstVertex;
        public uint FirstInstance;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleSortKey
    {
        public uint Key;
        public uint InstanceIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleResetPushConstants
    {
        public uint CurrentFrameIndex;
        public uint ParticleCapacity;
        public uint DrawCapacity;
        public uint Flags;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
        public uint Padding3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleSortPushConstants
    {
        public uint CurrentFrameIndex;
        public uint ParticleCapacity;
        public uint Mode;
        public uint Bucket;
        public uint SortLevel;
        public uint SortStage;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUParticleSimulatePushConstants
    {
        public uint CurrentFrameIndex;
        public uint ParticleCapacity;
        public uint EmitterCount;
        public uint MaxSpawnPerEmitter;
        public float DeltaSeconds;
        public float TimeSeconds;
        public float SoftParticleDistance;
        public uint Flags;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
        public uint Padding3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMeshlet
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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUObjectData
    {
        public Matrix4x4 WorldMatrix;
        public Matrix4x4 WorldMatrixInverseTranspose;
        public int MeshIndex;
        public int MaterialIndex;
        public int SkinnedVertexOffset;
        public int SkinningEnabled;
        public Matrix4x4 PreviousWorldMatrix;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDebugLineVertex
    {
        public Vector3 Position;
        public float Padding0;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMaterialData
    {
        public Vector4 Albedo;
        public Vector4 Emissive;
        public Vector4 NormalScaleBias;
        public Vector4 MetallicRoughnessAO;
        public Vector4 BaseColorOffsetScale;
        public Vector4 NormalOffsetScale;
        public Vector4 MetallicRoughnessOffsetScale;
        public Vector4 OcclusionOffsetScale;
        public Vector4 EmissiveOffsetScale;
        public Vector4 TextureRotations;
        public Vector4 TextureTexCoordSets;
        // x = occlusion rotation, y = occlusion UV set, z/w reserved.
        public Vector4 OcclusionBinding;
        public int AlbedoTextureIndex;
        public int NormalTextureIndex;
        public int MetallicRoughnessTextureIndex;
        public int OcclusionTextureIndex;
        public int EmissiveTextureIndex;
        public uint FeatureFlags;
        public int ExtensionDataIndex;
        public uint TransportFlags;
        public uint TransportProfileRevision;
        // IEEE-754 half2: low 16 bits mean metallic, high 16 bits mean roughness.
        public uint PackedMeanMetallicRoughness;
        public uint TransportProfileQuality;
        /// <summary>
        /// Monotonic material publication revision. This remains the
        /// authoritative invalidation key for consumers that need to rebuild
        /// whenever any shader-visible material state changes.
        /// </summary>
        public uint MaterialRevision;
        /// <summary>
        /// Monotonic texture-content publication revision. A value of zero is
        /// the registered baseline; only runtime texture-content changes
        /// advance it. Keeping it separate from <see cref="MaterialRevision"/>
        /// makes a stale texture recompile distinguishable from an authored
        /// material edit and from <see cref="TransportProfileRevision"/>.
        /// </summary>
        public uint TextureContentRevision;
        // Six binary16 transport values occupy the existing 12-byte std430
        // alignment region before the following vec4. Low/high half order:
        // diffuse base R/G, diffuse base B/F0 R, and F0 G/B. The appended
        // transmission statistic intentionally advances the material ABI to
        // 320 bytes while retaining the established offsets above this block.
        public uint PackedMeanGiDirectionalDiffuseBaseRg;
        public uint PackedMeanGiDirectionalDiffuseBaseBAndF0R;
        public uint PackedMeanGiDielectricF0Gb;
        public Vector4 DdgiAverageAlbedo;
        public Vector4 DdgiAverageEmissive;
        public Vector4 DdgiAverageTransmission;
        public Vector4 DdgiMaterialPolicy;

        [Obsolete("Use TransportFlags. This compatibility alias has no GPU storage of its own.")]
        public uint Reserved0
        {
            readonly get => TransportFlags;
            set => TransportFlags = value;
        }

        [Obsolete("Use TransportProfileRevision. This compatibility alias has no GPU storage of its own.")]
        public uint Reserved1
        {
            readonly get => TransportProfileRevision;
            set => TransportProfileRevision = value;
        }

        [Obsolete("Use PackedMeanMetallicRoughness. This compatibility alias has no GPU storage of its own.")]
        public uint Reserved2
        {
            readonly get => PackedMeanMetallicRoughness;
            set => PackedMeanMetallicRoughness = value;
        }

        [Obsolete("Use TransportProfileQuality. This compatibility alias has no GPU storage of its own.")]
        public uint Reserved3
        {
            readonly get => TransportProfileQuality;
            set => TransportProfileQuality = value;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMaterialExtensionData
    {
        public Vector4 Clearcoat;
        public Vector4 SheenColor;
        public Vector4 Anisotropy;
        public Vector4 Transmission;
        public Vector4 AttenuationColor;
        public Vector4 Subsurface;
        public Vector4 SpecularColor;
        public Vector4 Iridescence;
        public Vector4 Dispersion;
        public Vector4 ClearcoatOffsetScale;
        public Vector4 ClearcoatRoughnessOffsetScale;
        public Vector4 ClearcoatNormalOffsetScale;
        public Vector4 SheenColorOffsetScale;
        public Vector4 SheenRoughnessOffsetScale;
        public Vector4 AnisotropyOffsetScale;
        public Vector4 TransmissionOffsetScale;
        public Vector4 ThicknessOffsetScale;
        public Vector4 SpecularOffsetScale;
        public Vector4 SpecularColorOffsetScale;
        public Vector4 IridescenceOffsetScale;
        public Vector4 IridescenceThicknessOffsetScale;
        public Vector4 SubsurfaceOffsetScale;
        public Vector4 ExtensionTextureRotations0;
        public Vector4 ExtensionTextureRotations1;
        public Vector4 ExtensionTextureRotations2;
        public Vector4 ExtensionTextureRotations3;
        public Vector4 ExtensionTextureTexCoordSets0;
        public Vector4 ExtensionTextureTexCoordSets1;
        public Vector4 ExtensionTextureTexCoordSets2;
        public Vector4 ExtensionTextureTexCoordSets3;
        public int ClearcoatTextureIndex;
        public int ClearcoatRoughnessTextureIndex;
        public int ClearcoatNormalTextureIndex;
        public int SheenColorTextureIndex;
        public int SheenRoughnessTextureIndex;
        public int AnisotropyTextureIndex;
        public int TransmissionTextureIndex;
        public int ThicknessTextureIndex;
        public int SubsurfaceTextureIndex;
        public int SpecularTextureIndex;
        public int SpecularColorTextureIndex;
        public int IridescenceTextureIndex;
        public int IridescenceThicknessTextureIndex;
        public int Padding0;
        public int Padding1;
        public int Padding2;
        public int Padding3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULight
    {
        public const int CastsShadowsFlag = 1 << 0;

        public Vector3 Position;
        public float Intensity;
        public Vector3 Color;
        public float Range;
        public Vector3 Direction;
        public float SpotAngle;
        public int Type;
        public int ShadowFlags;
        public float ShadowStrength;
        public int Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSceneData
    {
        public Matrix4x4 ViewMatrix;
        public Matrix4x4 ProjectionMatrix;
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 InverseProjectionMatrix;
        public Vector3 CameraPosition;
        // Opaque receiver-cache variants reinterpret this otherwise-unused
        // slot's bits as their power-of-two cache row-pitch shift. Other variants
        // retain authored time, preserving the shared 256-byte ABI.
        public float Time;
        public Vector4 ScreenDimensions;
        public Vector4 NearFarPlanes;
        public Vector4 AmbientLight;
        public int LightCount;
        public int Padding0;
        public int Padding1;
        public int Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMeshletDrawCommand
    {
        public uint MeshletIndex;
        public uint InstanceId;
        public uint MaterialIndex;
        public uint Padding;
    }

    [Flags]
    public enum GPUMeshletDrawFlags : uint
    {
        None = 0,
        NeedsGpuFrustumTest = 1u << 0,
        CpuFrustumVisible = 1u << 1,
        ObjectFullyInsideFrustum = 1u << 2,
        MaterialMasked = 1u << 3,
        MaterialBlend = 1u << 4,
        CanHiZTest = 1u << 5
    }

    public enum HiZTestMode : uint
    {
        Off = 0,
        Bounds4Tap = 1,
        Full6Point5Tap = 2
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUPackedMeshletDrawCommand
    {
        public uint MeshletIndex;
        public uint InstanceId;
        public uint MaterialIndex;
        public uint Flags;
        public Vector4 WorldCenterRadius;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMeshletTaskFrameData
    {
        public Vector4 FrustumPlane0;
        public Vector4 FrustumPlane1;
        public Vector4 FrustumPlane2;
        public Vector4 FrustumPlane3;
        public Vector4 FrustumPlane4;
        public Vector4 FrustumPlane5;
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 PreviousHiZViewProjectionMatrix;
        public Matrix4x4 PreviousHiZInverseViewMatrix;
        public Vector2 ScreenDimensions;
        public uint PreviousHiZFrameValid;
        public uint Padding0;
        public Vector2 Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliagePrototype
    {
        public uint MeshMetadataIndex;
        public uint MeshletOffset;
        public uint MeshletCount;
        public uint MeshletLod1Offset;
        public uint MeshletLod1Count;
        public uint MeshletLod2Offset;
        public uint MeshletLod2Count;
        public uint MaterialIndex;
        public uint GeometryMode;
        public uint Flags;
        public float BladeHeight;
        public float BladeWidth;
        public Vector4 LodDistances;
        public Vector4 WindParams;
        public Vector4 LightingParams;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliagePatch
    {
        public Vector4 BoundsMinDensity;
        public Vector4 BoundsMaxSeed;
        public uint PrototypeIndex;
        public uint ClusterOffset;
        public uint ClusterCount;
        public uint DensityTextureIndex;
        public uint Seed;
        public uint Flags;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageCluster
    {
        public Vector4 WorldCenterRadius;
        public Vector4 BoundsMinDensity;
        public Vector4 BoundsMaxLod;
        public uint PatchIndex;
        public uint FirstInstance;
        public uint InstanceCount;
        public uint RandomSeed;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageInstance
    {
        public Vector4 PositionScale;
        public Vector4 RotationWind;
        public Vector4 ColorVariation;
        public uint PrototypeIndex;
        public uint PatchIndex;
        public uint ClusterIndex;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageMeshletDrawCommand
    {
        public uint MeshletIndex;
        public uint InstanceIndex;
        public uint PrototypeIndex;
        public uint MaterialIndex;
        public Vector4 WorldCenterRadius;
        public uint Flags;
        public uint LodLevel;
        public uint ClusterIndex;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageCounters
    {
        public uint VisibleClusterCount;
        public uint CulledClusterCount;
        public uint Lod0VisibleCount;
        public uint Lod1VisibleCount;
        public uint Lod2VisibleCount;
        public uint HiZTestedCount;
        public uint HiZRejectedCount;
        public uint VisibleMeshletDrawCount;
        public uint MeshletDrawOverflowCount;
        public uint FarImpostorVisibleCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageDispatchArgs
    {
        public uint GroupCountX;
        public uint GroupCountY;
        public uint GroupCountZ;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSceneSubmissionCounters
    {
        public uint CandidateCount;
        public uint EmittedCount;
        public uint FrustumRejectedCount;
        public uint OverflowCount;
        public uint HiZTestedCount;
        public uint HiZRejectedCount;
        public uint AppendCount;
        public uint Lod0EmittedCount;
        public uint Lod1EmittedCount;
        public uint Lod2EmittedCount;
        public uint MissingLodFallbackCount;
        public uint SolidDepthCandidateCount;
        public uint SolidDepthEmittedCount;
        public uint SolidDepthOverflowCount;
        public uint MaskedDepthCandidateCount;
        public uint MaskedDepthEmittedCount;
        public uint MaskedDepthOverflowCount;
        public uint SolidDepthAppendCount;
        public uint MaskedDepthAppendCount;
        public uint DirectionalStaticShadowCascade0CandidateCount;
        public uint DirectionalStaticShadowCascade0EmittedCount;
        public uint DirectionalStaticShadowCascade0RejectedCount;
        public uint DirectionalStaticShadowCascade0OverflowCount;
        public uint DirectionalStaticShadowCascade0AppendCount;
        public uint DirectionalStaticShadowCascade1CandidateCount;
        public uint DirectionalStaticShadowCascade1EmittedCount;
        public uint DirectionalStaticShadowCascade1RejectedCount;
        public uint DirectionalStaticShadowCascade1OverflowCount;
        public uint DirectionalStaticShadowCascade1AppendCount;
        public uint DirectionalStaticShadowCascade2CandidateCount;
        public uint DirectionalStaticShadowCascade2EmittedCount;
        public uint DirectionalStaticShadowCascade2RejectedCount;
        public uint DirectionalStaticShadowCascade2OverflowCount;
        public uint DirectionalStaticShadowCascade2AppendCount;
        public uint DirectionalStaticShadowCascade3CandidateCount;
        public uint DirectionalStaticShadowCascade3EmittedCount;
        public uint DirectionalStaticShadowCascade3RejectedCount;
        public uint DirectionalStaticShadowCascade3OverflowCount;
        public uint DirectionalStaticShadowCascade3AppendCount;
        public uint DirectionalDynamicShadowCascade0CandidateCount;
        public uint DirectionalDynamicShadowCascade0EmittedCount;
        public uint DirectionalDynamicShadowCascade0RejectedCount;
        public uint DirectionalDynamicShadowCascade0OverflowCount;
        public uint DirectionalDynamicShadowCascade0AppendCount;
        public uint DirectionalDynamicShadowCascade1CandidateCount;
        public uint DirectionalDynamicShadowCascade1EmittedCount;
        public uint DirectionalDynamicShadowCascade1RejectedCount;
        public uint DirectionalDynamicShadowCascade1OverflowCount;
        public uint DirectionalDynamicShadowCascade1AppendCount;
        public uint DirectionalDynamicShadowCascade2CandidateCount;
        public uint DirectionalDynamicShadowCascade2EmittedCount;
        public uint DirectionalDynamicShadowCascade2RejectedCount;
        public uint DirectionalDynamicShadowCascade2OverflowCount;
        public uint DirectionalDynamicShadowCascade2AppendCount;
        public uint DirectionalDynamicShadowCascade3CandidateCount;
        public uint DirectionalDynamicShadowCascade3EmittedCount;
        public uint DirectionalDynamicShadowCascade3RejectedCount;
        public uint DirectionalDynamicShadowCascade3OverflowCount;
        public uint DirectionalDynamicShadowCascade3AppendCount;
        public uint SimpleOpaqueAppendCount;
        public uint SimpleOpaqueEmittedCount;
        public uint SimpleOpaqueOverflowCount;
        public uint SimpleNormalOpaqueAppendCount;
        public uint SimpleNormalOpaqueEmittedCount;
        public uint SimpleNormalOpaqueOverflowCount;
        public uint FullOpaqueAppendCount;
        public uint FullOpaqueEmittedCount;
        public uint FullOpaqueOverflowCount;
        /// <summary>
        /// Number of directional-shadow candidates retained at LOD0 because the requested
        /// lower-LOD range cannot be mapped one-to-one without dropping caster coverage.
        /// </summary>
        public uint DirectionalShadowLodFallbackCount;
        /// <summary>LOD0 candidate commands intentionally removed by lower-LOD decimation.</summary>
        public uint OpaqueLodDecimatedCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSceneOpaqueCompactionPushConstants
    {
        public Vector4 CameraPosition;
        public uint CurrentFrameIndex;
        public uint SimpleCandidateCount;
        public uint SimpleNormalCandidateCount;
        public uint FullCandidateCount;
        public uint OutputCapacity;
        public uint SolidDepthCandidateCount;
        public uint MaskedDepthCandidateCount;
        public uint SolidDepthOutputCapacity;
        public uint MaskedDepthOutputCapacity;
        public uint DirectionalShadowCascadeCount;
        public uint DirectionalStaticShadowCandidateCount;
        public uint DirectionalDynamicShadowCandidateCount;
        public uint DirectionalStaticShadowOutputCapacity;
        public uint DirectionalDynamicShadowOutputCapacity;
        public uint OutputBufferBaseIndex;
        public uint CounterBufferBaseIndex;
        public uint Flags;
        public uint IndirectDispatchBufferBaseIndex;
        public uint SolidDepthOutputBufferBaseIndex;
        public uint MaskedDepthOutputBufferBaseIndex;
        public uint SimpleOutputCapacity;
        public uint SimpleNormalOutputCapacity;
        public uint FullOutputCapacity;
        public uint SimpleOutputBufferBaseIndex;
        public uint SimpleNormalOutputBufferBaseIndex;
        public uint FullOutputBufferBaseIndex;
        public Vector2 ScreenDimensions;
        public uint HiZTextureIndex;
        public uint HiZMipCount;
        public uint OcclusionCullingEnabled;
        public float OcclusionBias;
        public uint PreviousFrameUvPaddingPixels;
        public uint PreviousHiZFrameValid;
        public float GpuLod1DistanceRatio;
        public float GpuLod2DistanceRatio;
        public uint GpuShadowLodBias;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUForwardVisibilityCompactionPushConstants
    {
        public uint CurrentFrameIndex;
        public uint SimpleInputCapacity;
        public uint SimpleNormalInputCapacity;
        public uint FullInputCapacity;
        public uint SimpleOutputCapacity;
        public uint SimpleNormalOutputCapacity;
        public uint FullOutputCapacity;
        public uint InputCounterBufferBaseIndex;
        public uint OutputCounterBufferBaseIndex;
        public uint InputSimpleBufferBaseIndex;
        public uint InputSimpleNormalBufferBaseIndex;
        public uint InputFullBufferBaseIndex;
        public uint OutputSimpleBufferBaseIndex;
        public uint OutputSimpleNormalBufferBaseIndex;
        public uint OutputFullBufferBaseIndex;
        public uint IndirectDispatchBufferBaseIndex;
        public Vector2 ScreenDimensions;
        public uint HiZTextureIndex;
        public uint HiZMipCount;
        public uint OcclusionCullingEnabled;
        public float OcclusionBias;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageCullPushConstants
    {
        public Vector4 CameraPositionMaxDistance;
        public uint CurrentFrameIndex;
        public uint ClusterCount;
        public uint VisibleClusterCapacity;
        public uint MeshletDrawCapacity;
        public uint IndirectDispatchBufferBaseIndex;
        public uint Flags;
        public uint AuthoredMeshletWorkItemCount;
        public uint FirstAuthoredClusterIndex;
        public uint AuthoredClusterCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFoliageDrawPushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Vector4 CameraPositionTime;
        public Vector4 ScreenDimensions;
        public uint CurrentFrameIndex;
        public uint ClusterDrawCount;
        public uint VisibleClusterBufferBaseIndex;
        public uint Flags;
        public uint DebugView;
        public float ShadowDensityScale;
        public uint Padding1;
        public uint Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUTiledLightHeader
    {
        public uint LightCount;
        public uint LightOffset;
        /// <summary>Number of eligible local lights omitted because the tile list reached capacity.</summary>
        public uint OverflowCount;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULightIndex
    {
        public uint LightIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUScreenToViewParams
    {
        public Vector2 ScreenDimensions;
        public Vector2 InvScreenDimensions;
        public Vector2 TileSize;
        public Vector2 InvTileSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULightCullingParams
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector3 CameraPosition;
        public float Padding0;
        public Vector4 ScreenDimensions;
        public Vector4 NearFarPlanes;
        public uint LightCount;
        public uint MaxLightsPerTile;
        public uint TileCountX;
        public uint TileCountY;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDepthPushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Vector2 ScreenDimensions;
        public uint CurrentFrameIndex;
        public uint MeshletDrawCount;
        public uint MeshletDrawBufferBaseIndex;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUForwardPushConstants
    {
        private const uint DebugViewModeMask = 0xFFu;
        private const int AmbientOcclusionEnabledShift = 8;
        private const int AmbientOcclusionDebugViewShift = 16;
        private const int TransparentReceiveShadowsShift = 24;
        private const int TransparencyDebugViewShift = 25;
        private const int ScreenSpaceGlobalIlluminationEnabledShift = 28;
        private const int AmbientOcclusionForwardSamplingModeShift = 29;
        private const int GlobalIlluminationEnabledShift = 31;
        private const uint DdgiForwardEstimateCountersEnabledFlag = 1u << 0;
        private const uint DdgiClipmapCoverageCountersEnabledFlag = 1u << 1;
        private const uint DirectionalShadowReceiverCountersEnabledFlag = 1u << 2;
        private const uint MaterialTransportProvenanceEnabledFlag = 1u << 3;
        private const uint DecalGlobalIlluminationEnabledFlag = 1u << 4;
        private const uint DdgiLayeredReceiverCountersEnabledFlag = 1u << 5;
        private const uint DecalReceiveShadowsFlag = 1u << 6;
        // Keep the forward push-constant ABI at 256 bytes. The low diagnostic
        // bits are already part of the shader contract. Bit 30 selects the
        // frame-local opaque DDGI receiver cache and bit 31 is reserved for the
        // capture-only path exposed through the property below.
        private const uint DdgiReceiverCacheEnabledFlag = 1u << 30;
        private const uint ReflectionCaptureEnabledFlag = 1u << 31;
        private const int DirectionalShadowPreviewCascadeShift = 8;
        private const uint DirectionalShadowPreviewCascadeMask = 0x03u;

        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 InverseProjectionMatrix;
        public Vector3 CameraPosition;
        public float Time;
        public Vector2 ScreenDimensions;
        public uint CurrentFrameIndex;
        public uint MeshletDrawCount;
        public uint MeshletDrawBufferBaseIndex;
        public uint LightCount;
        public uint LocalLightCount;
        public uint HiZMipCount;
        public uint OcclusionCullingEnabled;
        public float OcclusionBias;
        public uint DebugAndAoFlags;
        public uint DiagnosticFlags;

        public uint CaptureFlags
        {
            get => DiagnosticFlags & ReflectionCaptureEnabledFlag;
            set
            {
                DiagnosticFlags = (DiagnosticFlags & ~ReflectionCaptureEnabledFlag) |
                                  (value & ReflectionCaptureEnabledFlag);
            }
        }

        public static uint PackCaptureFlags(bool reflectionCaptureEnabled) =>
            reflectionCaptureEnabled ? ReflectionCaptureEnabledFlag : 0u;

        public static uint PackDebugAndAoFlags(
            uint debugViewMode,
            bool ambientOcclusionEnabled,
            uint ambientOcclusionDebugView,
            bool transparentReceiveShadows = true,
            uint transparencyDebugView = 0u,
            uint ambientOcclusionForwardSamplingMode = 0u,
            bool globalIlluminationEnabled = false,
            bool screenSpaceGlobalIlluminationEnabled = false)
        {
            return (debugViewMode & DebugViewModeMask) |
                   (ambientOcclusionEnabled ? 1u << AmbientOcclusionEnabledShift : 0u) |
                   ((ambientOcclusionDebugView & DebugViewModeMask) << AmbientOcclusionDebugViewShift) |
                   (transparentReceiveShadows ? 1u << TransparentReceiveShadowsShift : 0u) |
                   ((transparencyDebugView & 0x07u) << TransparencyDebugViewShift) |
                   (screenSpaceGlobalIlluminationEnabled ? 1u << ScreenSpaceGlobalIlluminationEnabledShift : 0u) |
                   ((ambientOcclusionForwardSamplingMode & 0x03u) << AmbientOcclusionForwardSamplingModeShift) |
                   (globalIlluminationEnabled ? 1u << GlobalIlluminationEnabledShift : 0u);
        }

        public static uint PackDiagnosticFlags(
            bool ddgiForwardEstimateCountersEnabled,
            bool ddgiClipmapCoverageCountersEnabled = false,
            bool directionalShadowReceiverCountersEnabled = false,
            uint directionalShadowPreviewCascade = 0u,
            bool materialTransportProvenanceEnabled = false,
            bool decalGlobalIlluminationEnabled = false,
            bool ddgiLayeredReceiverCountersEnabled = false,
            bool decalReceiveShadows = false,
            bool ddgiReceiverCacheEnabled = false)
        {
            return (ddgiForwardEstimateCountersEnabled ? DdgiForwardEstimateCountersEnabledFlag : 0u) |
                   (ddgiClipmapCoverageCountersEnabled ? DdgiClipmapCoverageCountersEnabledFlag : 0u) |
                   (directionalShadowReceiverCountersEnabled ? DirectionalShadowReceiverCountersEnabledFlag : 0u) |
                   (materialTransportProvenanceEnabled ? MaterialTransportProvenanceEnabledFlag : 0u) |
                   (decalGlobalIlluminationEnabled ? DecalGlobalIlluminationEnabledFlag : 0u) |
                   (ddgiLayeredReceiverCountersEnabled ? DdgiLayeredReceiverCountersEnabledFlag : 0u) |
                   (decalReceiveShadows ? DecalReceiveShadowsFlag : 0u) |
                   (ddgiReceiverCacheEnabled ? DdgiReceiverCacheEnabledFlag : 0u) |
                   ((directionalShadowPreviewCascade & DirectionalShadowPreviewCascadeMask) <<
                    DirectionalShadowPreviewCascadeShift);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUMotionVectorPushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 PreviousViewProjectionMatrix;
        public Vector2 ScreenDimensions;
        public uint CurrentFrameIndex;
        public uint MeshletDrawCount;
        public uint MeshletDrawBufferBaseIndex;
        public uint PreviousFrameValid;
        public float Time;
        public float PreviousTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULightCullPushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector3 CameraPosition;
        public float Padding0;
        /// <summary>Normalized world-space camera forward vector, used to reconstruct view depth.</summary>
        public Vector3 CameraForward;
        public float PaddingCameraForward;
        public Vector2 ScreenDimensions;
        public float NearPlane;
        public float FarPlane;
        public uint LightCount;
        public uint MaxLightsPerTile;
        public uint TileCountX;
        public uint TileCountY;
        public uint DepthTextureIndex;
        public uint Padding1;
        public uint Padding2;
        public uint Padding3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUShadowData
    {
        public Matrix4x4 LightViewProjection0;
        public Matrix4x4 LightViewProjection1;
        public Matrix4x4 LightViewProjection2;
        public Matrix4x4 LightViewProjection3;
        public Vector4 CascadeSplits;
        public Vector4 Settings;
        public Vector4 Indices;
        // x = transition fraction, y = effective camera near, z = effective shadow far.
        // Kept separate from Settings so the directional-shadow ABI remains explicit.
        public Vector4 CascadeTransitionData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSpotShadow
    {
        public Matrix4x4 LightViewProjection;
        public Vector4 AtlasScaleOffset;
        public Vector4 BiasStrengthTexelSize;
        public int LightIndex;
        public int AtlasTile;
        public int PcfRadius;
        public int Enabled;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUPointShadow
    {
        public Matrix4x4 FaceViewProjection0;
        public Matrix4x4 FaceViewProjection1;
        public Matrix4x4 FaceViewProjection2;
        public Matrix4x4 FaceViewProjection3;
        public Matrix4x4 FaceViewProjection4;
        public Matrix4x4 FaceViewProjection5;
        public Vector4 PositionRange;
        public Vector4 BiasStrengthTexelSize;
        public int LightIndex;
        public int CubemapIndex;
        public int PcfRadius;
        public int Enabled;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULocalLightShadowIndex
    {
        public int SpotShadowIndex;
        public int PointShadowIndex;
        public int Padding0;
        public int Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUEnvironmentData
    {
        // Words 0..11 are the established environment ABI. Keep them stable so
        // HDR environments and external shader tooling remain compatible.
        public int EnvironmentTextureIndex;
        public int IrradianceTextureIndex;
        public int PrefilteredTextureIndex;
        public int BrdfLutTextureIndex;
        public float SkyIntensity;
        public float DiffuseIntensity;
        public float SpecularIntensity;
        public float RotationRadians;
        public uint PrefilteredMipCount;
        public uint Enabled;
        public uint DebugView;
        public uint DebugMipLevel;

        public int NextPrefilteredTextureIndex;
        public uint SourceKind;
        public uint AtmosphereFlags;
        public float PrefilteredBlend;

        public Vector4 SunDirectionAndAngularRadius;
        public Vector4 SunRadianceAndElevation;
        public Vector4 MoonDirectionAndAngularRadius;
        public Vector4 MoonRadianceAndNightBlend;
        public Vector4 GroundAlbedoAndTurbidity;
        public Vector4 AtmosphereParameters;
        public Vector4 GroundRadianceAndAirglow;

        public Vector4 HosekParametersR0;
        public Vector4 HosekParametersR1;
        public Vector4 HosekParametersR2;
        public Vector4 HosekParametersG0;
        public Vector4 HosekParametersG1;
        public Vector4 HosekParametersG2;
        public Vector4 HosekParametersB0;
        public Vector4 HosekParametersB1;
        public Vector4 HosekParametersB2;
        public Vector4 HosekRadiances;

        public Vector4 DiffuseIrradianceSh0;
        public Vector4 DiffuseIrradianceSh1;
        public Vector4 DiffuseIrradianceSh2;
        public Vector4 DiffuseIrradianceSh3;
        public Vector4 DiffuseIrradianceSh4;
        public Vector4 DiffuseIrradianceSh5;
        public Vector4 DiffuseIrradianceSh6;
        public Vector4 DiffuseIrradianceSh7;
        public Vector4 DiffuseIrradianceSh8;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUReflectionProbeHeader
    {
        public int ProbeCount;
        public int MaxProbesPerPixel;
        public int ProbeCubemapArrayTextureIndex;
        public int DebugTextureIndex;
        public float Intensity;
        public float GlobalFallbackIntensity;
        public uint ProbeMipCount;
        public uint Flags;
        public uint DebugView;
        public int DebugProbeIndex;
        public int DebugCubemapFace;
        public int DebugMipLevel;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUReflectionProbe
    {
        public Matrix4x4 WorldToProbe;
        public Vector4 PositionAndRadius;
        public Vector4 BoxMin;
        public Vector4 BoxMax;
        public Vector4 BlendParams;
        public int CubemapArrayIndex;
        public int Shape;
        public int Flags;
        public int Priority;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiProbeVolumeHeader
    {
        public int VolumeCount;
        public int ProbeCount;
        public int ActiveProbeCount;
        public int RaysPerProbe;
        public int MaxProbeUpdatesPerFrame;
        public int IrradianceTextureIndex;
        public int VisibilityTextureIndex;
        public int ProbeStateBufferIndex;
        public uint Flags;
        public uint DebugView;
        public uint IrradianceTexelsPerProbe;
        public uint VisibilityTexelsPerProbe;
        public float Intensity;
        public float EnvironmentFallbackIntensity;
        public float ThinWallLeakClampStrength;
        public float ThinWallProxyThickness;
        public uint CacheGeneration;
        public uint LastUpdatedFrameSerial;
        public uint CacheWarmupState;
        public uint CacheFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiProbeVolume
    {
        public Vector4 OriginAndFirstProbeIndex;
        public Vector4 SizeAndProbeCountX;
        public Vector4 ProbeSpacingAndProbeCountY;
        public Vector4 BiasAndProbeCountZ;
        public Vector4 RayAndUpdateParams;
        public Vector4 DebugColorAndFlags;
        public Vector4 ClipmapGridMinAndKind;
        public Vector4 ClipmapRingOffsetAndCascade;
        public Vector4 ClipmapBlendAndFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiProbeState
    {
        public Vector4 Irradiance;
        public Vector4 Visibility;
        public Vector4 RelocationAndClassification;
        public Vector4 QualityAndReason;
        public Vector4 UpdateMetadata;
        public Vector4 RepresentationMetadata;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiProbeUpdateRequest
    {
        public uint ProbeIndex;
        public uint VolumeIndex;
        public uint Flags;
        public uint Priority;
        public int LogicalCellX;
        public int LogicalCellY;
        public int LogicalCellZ;
        public uint RequestFrameSerial;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiProbeRelocationClassification
    {
        public Vector4 Relocation;
        public Vector4 Classification;
        public Vector4 Statistics;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiRayQueryInstance
    {
        public uint VertexOffset;
        public uint IndexOffset;
        public uint MaterialIndex;
        public uint Padding0;
        public Matrix4x4 WorldMatrixInverseTranspose;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiEmissiveSource
    {
        // Triangle representation: v0.xyz + area, edge1.xyz + alias threshold,
        // edge2.xyz + uintBits(alias index | flags), radiance + selection PDF.
        public Vector4 Vertex0Area;
        public Vector4 Edge1AliasProbability;
        public Vector4 Edge2AliasFlags;
        public Vector4 RadianceSelectionProbability;
    }

    // 240 bytes. Fixed-grid DDGI params, mirrored by ddgi_simple_shared.glsl.
    // The final two vectors are the V2 transport contract.  They deliberately
    // live in the params header instead of pass-local state so every shader that
    // samples DDGI can identify the published (receiver-visible) atlas.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiParams
    {
        public Vector4 GridOriginAndSpacing;
        public Vector4 GridCountsAndProbeCount;
        public Vector4 AtlasTexelsAndRayCount;
        public Vector4 HysteresisFrameAndFlags;
        public Vector4 EnvironmentRadianceAndIntensity;
        public Vector4 ProbeUpdateRange;
        public Vector4 DebugAndBias;
        public Vector4 RotationQuaternion;
        public Vector4 BiasAndPadding;
        public Vector4 Reserved0;
        // X = absolute world-space cap for the combined normal/view bias.
        // Y = conservative architectural-thickness proxy. The shader uses a
        // fraction of it as an additional cap, preventing coarse rings from
        // looking through thin walls. Z = thin-wall leak-clamp strength for the
        // Simple-DDGI receiver and recursive-bounce paths. W = exact provisioned
        // sampled-atlas probe-layer capacity (zero while the mirror is inactive).
        public Vector4 BiasLimitsAndPadding;
        // X/Y/Z/W = published irradiance atlas, private transport target,
        // persistent source-cache bindless indices, transport generation.
        public Vector4 TransportAndAtlasIndices;
        // X = solver relaxation, Y = diffuse-albedo clamp, Z = tail-relative
        // tolerance, W = bounded cached accelerated sweep count.
        public Vector4 TransportControls;
        // X = residency arena bindless index, Y = virtual page count,
        // Z = physical payload probe capacity, W = residency resource
        // generation. Integer values use PackHeaderWord.
        public Vector4 ResidencyAndCounts;
        // X = SimpleDdgiProbeResidencyMode, Y = dense physical probe count,
        // Z = sparse physical page capacity, W = residency feature flags.
        public Vector4 ResidencyControls;
    }

    // 112 bytes. Appended after GPUSimpleDdgiParams in the simple DDGI params buffer.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiVolume
    {
        public Vector4 OriginAndSpacing;
        public Vector4 GridCountsAndFirstProbe;
        public Vector4 WorldMinAndEdgeFade;
        public Vector4 WorldMaxAndKind;
        // X/Y = queue start/count, Z = required complete source-ray
        // cardinality, W reserved.
        public Vector4 UpdateStartAndCount;
        public Vector4 RaysAndReserved;
        // Integer payload encoded with bit-preserving uint/float conversion:
        // x = source-cache base word, y = cache stride words,
        // z = compact sampled-atlas first layer + 1 (zero = unmirrored),
        // w = source format, mirror payload, storage ABI, and codebook version.
        public Vector4 CacheLayout;
    }

    // 32 bytes. Parallel to GPUSimpleDdgiVolume and mirrored by
    // ddgi_simple_page_shared.glsl. All values are integer identities; keeping
    // them out of float-packed volume metadata avoids precision loss at the
    // hard virtual-probe limit.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiVolumePaging
    {
        public uint VirtualFirstProbe;
        public uint PageTableFirst;
        public uint DensePhysicalFirstProbe;
        public uint ResidencyMode;
        public uint PageGridX;
        public uint PageGridY;
        public uint PageGridZ;
        public uint SparsePoolFirstProbe;
    }

    // 64-byte fixed residency transaction header. Frame serial and resource
    // generation stamp every delayed feedback record; MappingGenerationCounter
    // is the one monotonic, non-zero allocator for page owner tokens.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiResidencyHeader
    {
        public uint FrameSerialLow;
        public uint FrameSerialHigh;
        public uint ResidencyResourceGeneration;
        public uint MappingGenerationCounter;
        public uint ResidencyMode;
        public uint VirtualProbeCount;
        public uint VirtualPageCount;
        public uint DensePhysicalProbeCount;
        public uint SparsePhysicalPageCapacity;
        public uint PhysicalProbeCapacity;
        public uint VolumeCount;
        public uint RetentionFrames;
        public uint MaximumAdmissionsPerFrame;
        public uint MaximumReceiverFeedbackRequests;
        public uint InactiveRetryFrames;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageTableEntry
    {
        public uint PhysicalPagePlusOne;
        public uint MappingGeneration;
        public uint Flags;
        // Shadow-only packed opaque gather-oracle epoch. It is not mapping
        // identity and remains zero/ignored in authoritative sparse mode.
        public uint OpaqueGatherOracleStamp;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageHistory
    {
        public uint VisibleDemandEpoch;
        public uint ReceiverDemandEpoch;
        public uint LastRelevantFrame;
        public uint FlagsAndGeometryRevision;
    }

    // Explicit development-only command embedded in the fixed demand-counter
    // region. Shipping demand producers never write this record.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageDevelopmentControl
    {
        public uint CommandSerial;
        public uint VirtualPagePlusOne;
        public uint Flags;
        public uint Reserved0;
    }

    // 48 bytes. Full reverse identity is checked before any sparse payload
    // access. Frame values are diagnostic/retention metadata and never serve as
    // completion tokens for resource destruction.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPhysicalPageMetadata
    {
        public uint OwnerVirtualPagePlusOne;
        public uint MappingGeneration;
        public uint ResidencyResourceGeneration;
        public uint FlagsAndDemandClass;
        public uint LastRelevantFrame;
        public uint LastPublishedFrame;
        public uint SourceGeneration;
        public uint CohortGeneration;
        // Encoded as frame + 1 so frame zero remains distinguishable from an
        // event that has not happened. These stamps are diagnostic witnesses;
        // resource retirement continues to use completion tokens exclusively.
        public uint AllocationFramePlusOne;
        public uint FirstScheduleFramePlusOne;
        public uint FirstPublicationFramePlusOne;
        // SIMPLE_DDGI_PHYSICAL_PAGE_ALLOCATION_* flags plus the immutable
        // allocation demand class in bits 8..15. The resident scheduler uses
        // this snapshot to finish a visible page even if sampled demand moves.
        public uint AllocationFlagsAndDemandClass;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageInitWork
    {
        public uint VirtualPageIndex;
        public uint PhysicalPageIndex;
        public uint MappingGeneration;
        public uint Flags;
    }

    // The shipping copy is the layout's fixed 1 KiB feedback region. The
    // stable scalar prefix occupies 256 bytes and future fields can be appended
    // without changing copy size or permitting a page-table readback.
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 1024)]
    public struct GPUSimpleDdgiResidencyFeedback
    {
        public uint FrameSerialLow;
        public uint FrameSerialHigh;
        public uint ResidencyResourceGeneration;
        public uint MappingGenerationCounter;
        public uint VirtualProbeCount;
        public uint VirtualPageCount;
        public uint DensePhysicalProbeCount;
        public uint SparsePhysicalPageCapacity;
        public uint PhysicalProbeCapacity;
        public uint ResidentPageCount;
        public uint FreePageCount;
        public uint InitializingPageCount;
        public uint PublishedPageCount;
        public uint SuppressedPageCount;
        public uint VisibleDemandPageCount;
        public uint ReceiverDemandPageCount;
        public uint RetainedPageCount;
        public uint AdmissionCount;
        public uint EvictionCount;
        public uint FailedAdmissionCount;
        public uint PoolPressureFrameCount;
        public uint ConsecutivePressureFrames;
        public uint MaximumConsecutivePressureFrames;
        public uint PageTableReverseDisagreementCount;
        public uint DuplicateVirtualOwnerCount;
        public uint DuplicatePhysicalOwnerCount;
        public uint StaleVirtualRequestCount;
        public uint StaleMappingRequestCount;
        public uint StaleResourceRequestCount;
        public uint OutOfRangeRequestCount;
        public uint NonResidentGatherRejectionCount;
        public uint CoarserFallbackCount;
        public uint SuppressionCount;
        public uint RetryCount;
        public uint AllocationToScheduleP50;
        public uint AllocationToScheduleP95;
        public uint AllocationToScheduleMax;
        public uint AllocationToPublicationP50;
        public uint AllocationToPublicationP95;
        public uint AllocationToPublicationMax;
        public uint Flags;
        public uint EventSourceGeneration;
        public uint EventCohortGeneration;
        public uint AdmissionProbeCount;
        public uint EvictionProbeCount;
        public uint OtherGenerationEvictionProbeCount;
        public uint ReceiverRequestOverflowCount;
        public uint NonResidentVirtualProbeCount;
        public uint InactiveResidentProbeCount;
        public uint ActiveResidentProbeCount;
        public uint ResidentProbeCount;
        public uint ConvergedResidentProbeCount;
        public uint VisibleDemandResidentHitPageCount;
        public uint VisibleDemandMissingPageCount;
        public uint RetainedAge0To15PageCount;
        public uint RetainedAge16To63PageCount;
        public uint RetainedAge64To255PageCount;
        public uint RetainedAge256PlusPageCount;
        public uint DemandEpoch;
        public uint ReceiverRequestCount;
        public uint PredictorFalseNegativePageCount;
        public uint PredictorFalsePositivePageCount;
        public uint OpaqueGatherDemandPageCount;
        public uint PredictorTruePositivePageCount;
        public uint NearVirtualProbeCount;
        public uint NearResidentProbeCount;
        public uint NearActiveResidentProbeCount;
        public uint NearInactiveResidentProbeCount;
        public uint NearDemandedPageCount;
        public uint NearConvergedResidentProbeCount;
        public uint MidVirtualProbeCount;
        public uint MidResidentProbeCount;
        public uint MidActiveResidentProbeCount;
        public uint MidInactiveResidentProbeCount;
        public uint MidDemandedPageCount;
        public uint MidConvergedResidentProbeCount;
        public uint FarVirtualProbeCount;
        public uint FarResidentProbeCount;
        public uint FarActiveResidentProbeCount;
        public uint FarInactiveResidentProbeCount;
        public uint FarDemandedPageCount;
        public uint FarConvergedResidentProbeCount;
        public uint OrdinaryAllocationToPublicationP50;
        public uint OrdinaryAllocationToPublicationP95;
        public uint OrdinaryAllocationToPublicationMax;
        public uint CutAllocationToPublicationP50;
        public uint CutAllocationToPublicationP95;
        public uint CutAllocationToPublicationMax;
        public uint DevelopmentPinnedPageCount;
    }

    // 112-byte depth-demand ABI. One shader invocation predicts one 8x8
    // receiver tile and stamps a packed epoch/distance demand record.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageDemandPushConstants
    {
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector4 CameraPositionAndPadding;
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint ParamsBufferIndex;
        public uint DepthTextureIndex;
        public uint DemandEpoch;
        public uint SampleCount;
        public uint Flags;
        public uint Reserved0;
    }

    // 112-byte ABI for the frame-local opaque receiver cache. One compute
    // invocation evaluates a complete Simple-DDGI gather for one 12x12 screen
    // block, then writes a packed 16-byte gather-lattice sample.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiReceiverCachePushConstants
    {
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector4 CameraPositionAndPadding;
        public uint ScreenWidth;
        public uint ScreenHeight;
        public uint CacheWidth;
        public uint CacheHeight;
        public uint ParamsBufferIndex;
        public uint DepthTextureIndex;
        public uint CacheBufferIndex;
        public uint ReceiverScale;
    }

    // 24-byte ABI for publishing the reduced gather lattice to a frame-local
    // aligned FP16 cache buffer. Keeping this separate from the gather constants
    // prevents resolve invocations from carrying matrices or DDGI state.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiReceiverCacheResolvePushConstants
    {
        public uint GatherWidth;
        public uint GatherHeight;
        public uint CacheWidth;
        public uint CacheHeight;
        public uint GatherBufferIndex;
        public uint PackedScaleAndEdgeExtents;
    }

    // 80-byte common ABI for reset/classify/prefix/reconcile/initialize/
    // feedback. CurrentFrame remains the virtual-state age clock; the two
    // publication fields bound the visible partial-page cohort in probe units.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPageResidencyPushConstants
    {
        public uint ParamsBufferIndex;
        public uint ProbeStateBufferIndex;
        public uint RelocationClassificationBufferIndex;
        public uint ReceiverProbeBufferIndex;
        public uint TransportSourceCacheBufferIndex;
        public uint SchedulerArenaBufferIndex;
        public uint SchedulerProbeStateOffsetWords;
        public uint SchedulerActiveProbeCount;
        public uint CurrentFrame;
        public uint DemandEpoch;
        public uint ResourceGeneration;
        public uint GeometryGeneration;
        public uint SourceGeneration;
        public uint CohortGeneration;
        // SimpleDdgiProbePageLayout.PhysicalPageAllocation* flags. Admission
        // snapshots this classification into the physical-page metadata so a
        // cold bootstrap can never pollute ordinary-motion or cut latency.
        public uint AllocationFlags;
        public uint VisiblePublicationProbeBudget;
        public uint PublicationLatencyTargetFrames;
        public uint Stage;
        public uint FrameSerialLow;
        public uint FrameSerialHigh;
    }

    // 20 bytes. RadianceDistance.w retains full-precision surface distance.
    // PackedVisibilityHitEpoch contains FP16 visibility distance in bits 0..15,
    // exact hit kind in bits 16..18, direction epoch in bits 19..23, and
    // validity/reserved flags in bits 24..31. Direction is reconstructed from
    // probe/ray identity and the checked-in rotation codebook.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiRayResult
    {
        public Vector4 RadianceDistance;
        public uint PackedVisibilityHitEpoch;
    }

    // 32-byte validation/rollback scratch record. It is never reinterpreted as
    // GPUSimpleDdgiRayResult; changing modes recreates the scratch allocation.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiLegacyRayResult
    {
        public Vector4 RadianceDistance;
        public Vector4 DirectionHitFlags;
    }

    // 36-byte validation/rollback source-cache ABI. Radiance and source hit
    // distance remain IEEE binary32, and the octahedral ray direction remains
    // stored so Validate mode can shadow-compare codebook reconstruction.
    // The high byte of PackedTransmission stores the complete source sequence
    // cardinality; zero encodes the supported maximum of 256 and its RGB bytes
    // remain the packed transmission lobe. The final word stores the full
    // 24-bit physical generation, a combined three-bit validity/exact-hit-kind
    // code, and the five-bit direction epoch.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiTransportRayCache
    {
        public Vector4 SourceRadianceDistance;
        public uint PackedDirection;
        public uint PackedNormal;
        public uint PackedAlbedo;
        public uint PackedTransmission;
        public uint GenerationFlagsAndDirectionEpoch;
    }

    // Exact Compact-28 persistent source-cache ABI.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiTransportRayCacheCompact28
    {
        public uint PackedSourceRadianceXY;
        public uint PackedSourceRadianceZReserved;
        public float SourceDistance;
        public uint PackedNormal;
        public uint PackedAlbedo;
        public uint PackedTransmission;
        public uint GenerationFlagsAndDirectionEpoch;
    }

    // Exact Compact-24 persistent source-cache ABI.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiTransportRayCacheCompact24
    {
        public uint PackedSourceRadianceXY;
        public uint PackedSourceRadianceZDistance;
        public uint PackedNormal;
        public uint PackedAlbedo;
        public uint PackedTransmission;
        public uint GenerationFlagsAndDirectionEpoch;
    }

    // 32 bytes. Simple DDGI per-probe state: relocation.xyz/active, then flags/age/classification/debug.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiProbeState
    {
        public Vector4 RelocationAndActive;
        public uint Flags;
        public uint Age;
        public uint Classification;
        public uint Reserved0;
    }

    // 16 bytes. Receiver-only projection of the authoritative 32-byte probe
    // state. The first two words pack spacing-relative relocation and active
    // weight, the third word contains receiver rejection/publication flags,
    // and the final word is the canonical atlas probe address. Compute and
    // scheduler shaders must continue to use GPUSimpleDdgiProbeState.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiReceiverProbe
    {
        public uint PackedRelocationXY;
        public uint PackedRelocationZWeight;
        public uint Flags;
        public uint AtlasProbeAddress;
    }

    // 48 bytes. Simple DDGI update queue entry. The predecessor scheduler
    // consumes the original words 6/7 for outcome identity and exact source
    // epoch, so sparse physical identity is appended instead of aliasing either
    // correctness field. Twelve words retain 16-byte record alignment.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiProbeUpdate
    {
        public uint ProbeIndex;
        public uint VolumeIndex;
        public uint Flags;
        public uint Reserved0;
        // Full source sequence cardinality used by V2 cache lookup. It remains
        // valid when Flags carries a smaller maintenance-ray count.
        public uint SourceRayCount;
        // Metadata for the lighting generation that produced the source cache.
        public uint SourceLightingGeneration;
        public uint OutcomeIndex;
        // Exact source epoch expected after this transaction. Cached-only work
        // carries the already committed epoch; a source refresh carries the
        // next epoch that will become visible at commit.
        public uint SourceEpoch;
        public uint PhysicalProbeIndex;
        public uint PageMappingGeneration;
        // Full residency-arena transaction identity. This complements the
        // per-page mapping generation and closes ABA across arena replacement.
        public uint ResidencyResourceGeneration;
        // Low 31 bits contain the first source-cache word owned by
        // PhysicalProbeIndex, plus one. Zero is invalid; bit 31 is a transient
        // split-trace cache-fallback handshake. Publishing the base once avoids
        // rebuilding sparse/dense region ownership for every dispatched ray.
        public uint CacheProbeBaseWordPlusOne;
    }

    // 48 bytes. Mirrors SIMPLE_DDGI_RELOCATION_CLASSIFICATION_STRIDE_WORDS.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiRelocationClassification
    {
        public Vector4 RelocationDistance;
        public Vector4 Classification;
        public Vector4 Statistics;
    }

    // 160 bytes. Coarse far-field parameters, mirrored by farfield_clipmap.glsl.
    // The first six vectors retain the legacy single-clipmap layout.  The final
    // four vectors describe the bounded virtual-page cache and the independently
    // versioned material payload used by production tracing and page baking.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFarFieldClipmapParams
    {
        public Vector4 OriginAndVoxelSize;
        public Vector4 ResolutionAndExtent;
        public Vector4 TraceParams;
        public Vector4 BakeParams;
        public Vector4 Diagnostics;
        public Vector4 Reserved0;
        public Vector4 PagingParams;
        public Vector4 PagingLayout;
        public Vector4 CameraAndBakePage;
        // x = payload version, y = words per logical voxel, z/w reserved.
        public Vector4 MaterialPayload;
    }

    // 32 bytes. Open-addressed virtual far-field page-table entry.  The world
    // page coordinate is stable; the physical-page index is bounded by the
    // selected quality tier and is never inferred from world size.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFarFieldPageTableEntry
    {
        public int WorldPageX;
        public int WorldPageY;
        public int WorldPageZ;
        public uint CascadeAndFlags;
        public uint PhysicalPageIndex;
        public uint Generation;
        public uint Reserved0;
        public uint Reserved1;
    }

    // 96 bytes. Static opaque instance metadata for far-field voxelization.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFarFieldInstance
    {
        public uint VertexOffset;
        public uint IndexOffset;
        public uint IndexCount;
        public uint MaterialIndex;
        public Matrix4x4 World;
        // Stable, dense primitive key range used by V2's final tie-break pass.
        // UInt32.MaxValue remains the empty-voxel sentinel.
        public uint PrimitiveKeyBase;
        public uint MaterialRevision;
        public uint FarFieldRevision;
        // Transport-profile revision captured with the page source.
        public uint Reserved0;
    }

    // 32 bytes / eight uint words. The V2 far-field payload is deliberately a
    // separate ABI from the one-word V1 occupancy/RGB8 encoding.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFarFieldMaterialVoxelV2
    {
        public uint WinnerKey;
        public uint CoverageConeAndFlags;
        public uint DiffuseRgb10;
        public uint EmissionRg16;
        // Low half = emissive B. High half = material AO in payload v4+;
        // versions 2-3 leave the high half reserved and decode it as neutral.
        public uint EmissionBAndOcclusion16;
        public uint GeometricNormalOct16;
        public uint MaterialRevision;
        public uint TransportProfileRevision;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPushConstants
    {
        public uint ParamsBufferIndex;
        public uint IrradianceAtlasBufferIndex;
        public uint VisibilityAtlasBufferIndex;
        public uint RayResultScratchBufferIndex;
        public uint CurrentFrameIndex;
        public uint LightCount;
        public uint DirectionalLightCount;
        public uint LocalLightCount;
        public uint MaxShadedLights;
        public uint EmissiveSourceCount;
        public uint FarFieldParamsBufferIndex;
        public uint FarFieldVoxelBufferIndex;
        public uint FarFieldInstanceBufferIndex;
        public uint Flags;
        public uint MaterialTextureMaxCascade;
        public uint ProbeStateBufferIndex;
        public uint ProbeUpdateQueueBufferIndex;
        public uint RelocationClassificationBufferIndex;
        public uint TransportSourceCacheBufferIndex;
        public uint TransportReadIrradianceAtlasBufferIndex;
        public uint TransportWriteIrradianceAtlasBufferIndex;
        public uint PrivateVisibilityAtlasOffsetWords;
        public uint TransportGeneration;
        public uint PrimaryDirectionalLightIndex;
        public uint DispatchQueueOffset;
        public uint DispatchProbeCount;
        public uint DispatchRaysPerProbe;
        public uint SchedulerArenaBufferIndex;
        public uint SchedulerRayBucketIndex;
        public uint SchedulerRayBucketCommandsOffsetWords;
        public uint SchedulerRayBucketMetadataOffsetWords;
        public uint SchedulerOutcomesOffsetWords;
        public uint SchedulerCountersOffsetWords;
        public uint SchedulerUpdateRecordsOffsetWords;
    }

    /// <summary>
    /// Audit-only push constants. This purpose-built ABI stays within Vulkan's
    /// guaranteed 128-byte push-constant capacity while selecting a chunk of
    /// the frozen participant list, its bounded workspace, and atomic summary.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiTransportAuditPushConstants
    {
        public uint ParamsBufferIndex;
        public uint RayResultScratchBufferIndex;
        public uint ProbeStateBufferIndex;
        public uint TransportSourceCacheBufferIndex;
        public uint TransportReadIrradianceAtlasBufferIndex;
        public uint TransportGeneration;
        public uint DispatchProbeCount;
        public uint DispatchRaysPerProbe;
        public uint SchedulerArenaBufferIndex;
        public uint AuditSummaryBufferIndex;
        public uint AuditSummaryBaseWord;
        public uint AuditProbeOffset;
        public uint AuditProbeCount;
        public uint AuditExpectedParticipantCount;
        public uint AuditExpectedTexelCount;
        public uint AuditChunkIndex;
        public uint AuditSchedulerFrameOffsetWords;
        public uint AuditVolumeTableGeneration;
        public uint AuditPhysicalOwnershipGeneration;
        public uint AuditSourceLightingGeneration;
        public uint AuditSourceEpochGeneration;
        public uint AuditTransportOperatorGeneration;
        public uint AuditCanonicalFieldGeneration;
        public uint AuditSolveGeneration;
        public uint AuditEpochGeneration;
        public uint AuditQueueGeneration;
        public uint AuditSchedulerResourceGeneration;
        public uint AuditSchedulerProbeStateOffsetWords;
        public uint AuditSolveEpoch;
        public uint AuditWorkspaceBaseWord;
        // Optional witness selected from the preceding complete audit. The
        // pair consumes the final eight bytes of Vulkan's guaranteed 128-byte
        // push-constant capacity.
        public uint AuditWitnessProbeIndex;
        public uint AuditWitnessTexelIndex;
    }

    /// <summary>
    /// The first words of the GPU-resident audit reduction. Float values are
    /// stored as uint bit patterns because the shader uses atomicMax on the
    /// non-negative FP32 domain. The scheduler arena reserves 1 KiB around
    /// this header for future checked counters without changing this ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiTransportAuditSummary
    {
        public uint FixedPointDefectBits;
        public uint FieldMagnitudeBits;
        public uint ExpectedParticipantCount;
        public uint AuditedParticipantCount;
        public uint ExpectedTexelCount;
        public uint AuditedTexelCount;
        public uint NonFiniteCount;
        public uint InvalidCacheCount;
        public uint LastChunkIndex;
        public uint ObservedContractionBits;
        // Maximum half-storage rounding interval observed in the frozen
        // canonical field. The audit keeps this separate from D so the host
        // can report a quantization-limited certificate instead of silently
        // loosening the authored tail tolerance.
        public uint CanonicalQuantizationFloorBits;
        public uint ExcludedInactiveCount;
        // Virtual probes outside the frozen resident/published participant
        // domain. A non-zero value is normal with sparse residency.
        public uint ExcludedNotVisibleCount;
        public uint ExcludedStaleSourceCount;
        // Set when any checked audit counter would have wrapped. The host
        // rejects the summary even if the wrapped value happens to match the
        // expected population.
        public uint CounterOverflow;
        // Fail-closed cache rejection attribution. These counts are per probe,
        // not per ray, and may overlap when one probe violates multiple fields.
        public uint CacheIdentityFailureCount;
        public uint CacheCardinalityFailureCount;
        public uint CacheSourceGenerationFailureCount;
        public uint CacheSourceEpochFailureCount;
        public uint CachePhysicalGenerationFailureCount;
        // Upper twelve bits: monotonic FP32 defect bucket. Lower twenty bits:
        // probeIndex * 64 + irradiance texel. This is a diagnostic witness;
        // FixedPointDefectBits remains the exact certificate maximum.
        public uint MaximumDefectWitnessKey;
        public uint DetailedWitnessValid;
        public uint DetailedWitnessProbeIndex;
        public uint DetailedWitnessTexelIndex;
        public uint DetailedWitnessWeightSumBits;
        public uint DetailedWitnessCandidateRBits;
        public uint DetailedWitnessCandidateGBits;
        public uint DetailedWitnessCandidateBBits;
        public uint DetailedWitnessCanonicalRBits;
        public uint DetailedWitnessCanonicalGBits;
        public uint DetailedWitnessCanonicalBBits;
        public uint DetailedWitnessProbeResidualBits;
        public uint DetailedWitnessSourceRayCount;
        public uint DetailedWitnessPrivateRBits;
        public uint DetailedWitnessPrivateGBits;
        public uint DetailedWitnessPrivateBBits;
        // First race-winning identity for each bounded mismatch class. Low and
        // high 16-bit halves store virtual+1 and physical+1 respectively;
        // zero is the absent sentinel.
        public uint FirstNotResidentIdentity;
        public uint FirstStaleSourceIdentity;
        public uint FirstInvalidCacheIdentity;
        public uint FirstNonFiniteIdentity;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiPublishPushConstants
    {
        public uint ParamsBufferIndex;
        public uint IrradianceAtlasBufferIndex;
        public uint VisibilityAtlasBufferIndex;
        public uint ProbeStateBufferIndex;
        public uint ReceiverProbeBufferIndex;
        public uint ProbeUpdateQueueBufferIndex;
        public uint TransportIrradianceAtlasBufferIndex;
        public uint PrivateVisibilityAtlasOffsetWords;
        public uint SampledAtlasGroupCount;
        public uint SampledAtlasLayersPerTexture;
        public uint SchedulerArenaBufferIndex;
        public uint SchedulerOutcomesOffsetWords;
        public uint SchedulerCountersOffsetWords;
        public uint SchedulerFrameOffsetWords;
    }

    // 160 bytes.  This is the only CPU-authored scheduler record that changes
    // on a normal frame.  Generation/frame ownership is represented by uints,
    // never float bitcasts.  Ray targets are bounded by the authored DDGI
    // budgets and therefore fit in uint without lossy conversion.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerFrame
    {
        public uint ActiveProbeCount;
        public uint ActiveVolumeCount;
        public uint CandidateCapacity;
        public uint RequestCapacity;
        public uint ConfiguredRequestBudget;
        public uint EffectiveRequestBudget;
        public uint PrimaryRayBudget;
        public uint SourceThroughputProbeTarget;
        public uint SourceThroughputRayTarget;
        public uint SourceThroughputRayCapacity;
        public uint FrameIndex;
        // When PeriodicSourceRefreshWave is set this is the frozen cohort
        // cutoff. Otherwise it is the earliest frame at which a certified
        // field may open its next periodic source cohort. The deterministic
        // scheduling policy is already expressed by fixed CPU-authored
        // budgets and never consumed this word on the GPU.
        public uint PeriodicSourceRefreshControlFrame;
        public uint FrameSerialLow;
        public uint FrameSerialHigh;
        public uint VolumeTableGeneration;
        public uint SchedulerResourceGeneration;
        public uint QueueTransactionGeneration;
        public uint SourceLightingGeneration;
        public uint TransportGeneration;
        public uint GlobalConvergenceGeneration;
        public Vector4 CameraPositionAndNearProximity;
        public uint DirtyRegionCount;
        public uint DirtyRegionCapacity;
        public uint DirtyReasonFlags;
        public uint FeatureFlags;
        public uint ClassificationRetryFrames;
        public uint SourceRefreshIntervalFrames;
        public uint StableGenerationRequirement;
        public uint SourceEpoch;
        public uint RayBucket0;
        public uint RayBucket1;
        public uint RayBucket2;
        public uint RayBucket3;
        public uint RayBucket4;
        public uint RayBucket5;
        public uint InvalidationMarkerGeneration;
        public uint Reserved0;
    }

    // 176 bytes.  Volume policy is uploaded only when topology, quality, or
    // scheduler policy changes.  Current/previous origins and toroidal offsets
    // are explicit so a shader can fail closed on incompatible remaps.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerVolumePolicy
    {
        public uint FirstProbe;
        public uint ProbeCount;
        public uint VolumeKind;
        public uint RingIndex;
        public uint SourceOrdinal;
        public uint Purpose;
        public uint LayoutGeneration;
        public uint PreviousLayoutGeneration;
        public Vector4 CurrentOriginAndSpacing;
        public Vector4 PreviousOriginAndSpacing;
        public uint CurrentCountX;
        public uint CurrentCountY;
        public uint CurrentCountZ;
        public uint PreviousCountX;
        public uint PreviousCountY;
        public uint PreviousCountZ;
        public uint PhysicalOffsetX;
        public uint PhysicalOffsetY;
        public uint PhysicalOffsetZ;
        public uint LayoutFlags;
        public uint MinimumQuota;
        public uint PreferredMaximumQuota;
        public uint SchedulingWeight;
        public uint Priority;
        public uint FullRaysPerProbe;
        public uint MaintenanceRaysPerProbe;
        public uint MaterialTextureMaxCascade;
        public uint MaxShadedLights;
        public uint SequenceStride;
        // Cache region metadata comes from the authoritative storage compiler.
        // Admission resolves one per-probe base in parallel and stores it in the
        // private update record for the serial emit stage.
        public uint CacheBaseWord;
        public int CellDeltaX;
        public int CellDeltaY;
        public int CellDeltaZ;
        public uint DirtyGeneration;
        // Mirrors the CPU visibility-candidate radius for authored volumes.
        // Ring volumes use their fixed ring radius and ignore this padding.
        public float ProximityRadiusPadding;
        public uint CacheWordsPerProbe;
        // Low/high 16 bits are physical-first/count. The hard Simple-DDGI
        // capacity is 32,768, so both authoritative compiler values fit.
        public uint CachePhysicalFirstAndCount;
        public uint CacheLayoutFlags;
    }

    // 48 bytes.  Dirty-region records are bounded and coalesced by the CPU;
    // generation and reason remain integer fields so overlap handling cannot
    // accidentally reinterpret ownership metadata as a float.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerDirtyRegion
    {
        public Vector4 Minimum;
        public Vector4 Maximum;
        public uint ReasonFlags;
        public uint Generation;
        public uint Reserved0;
        public uint Reserved1;
    }

    // 44 bytes / eleven uint words.  The packed word has documented,
    // unit-tested bounds in SimpleDdgiSchedulerProbeStatePacking.  The private
    // scheduler ABI keeps dirty-latency start and the applied invalidation
    // marker in separate words; neither is allowed to alias the other.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerProbeState
    {
        public uint LastCommittedUpdateFrame;
        public uint LastCommittedSourceRefreshFrame;
        public uint CommittedSourceLightingGeneration;
        public uint SourceEpoch;
        public uint OwningVolumeTableGeneration;
        public uint DirtyReasonFlags;
        public uint DirtyStartFrame;
        public uint PackedTransportAndLifecycle;
        public uint AppliedInvalidationMarker;
        public uint Reserved0;
        // One-based first cache word for this probe. Commit publishes this
        // only after validating the same address carried by the producer
        // transaction; zero remains the invalid/uncommitted sentinel.
        public uint CacheProbeBaseWordPlusOne;
    }

    // 32 bytes.  Invalid candidates use ProbeIndex == uint.MaxValue.  The
    // remaining words are packed only from bounded integer enums/counts.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerCandidate
    {
        public uint ProbeIndex;
        public uint VolumeIndex;
        public uint ExpectedPhysicalGeneration;
        public uint SequenceOrdinal;
        public uint WorkClassAndTransport;
        public uint RayTierAndReasonFlags;
        public uint ActiveRayCount;
        public uint SourceRayCount;

        public bool IsValid => ProbeIndex != uint.MaxValue;
    }

    // 60 bytes / fifteen uint words.  Update producers write transaction-private outcomes; commit
    // is the first stage allowed to mutate receiver-visible lifecycle state.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiUpdateOutcome
    {
        public uint QueueTransactionGeneration;
        public uint SchedulerResourceGeneration;
        public uint VolumeTableGeneration;
        public uint SourceLightingGeneration;
        public uint TransportGeneration;
        public uint ProbeIndex;
        public uint ExpectedPhysicalGeneration;
        public uint RequiredCompletionMask;
        public uint CompletionMask;
        public uint FailureReason;
        public uint UpdateFlags;
        public uint ExpectedRayInvocationCount;
        public uint TraceInvocationCount;
        public uint TransportInvocationCount;
        public uint ResidualBits;
    }

    // A Vulkan dispatch command occupies 12 bytes.  Arena slots are 16 bytes
    // so every command begins at the same alignment and a failed schedule can
    // reset x without touching neighbouring metadata.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiDispatchIndirectCommand
    {
        public uint GroupCountX;
        public uint GroupCountY;
        public uint GroupCountZ;
        public uint Reserved;
    }

    // Fixed, compact feedback header.  The arena reserves the full 4 KiB
    // shipping summary; this header is the portion consumed by CPU admission
    // and stale-generation validation.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulerFeedback
    {
        public uint FrameSerialLow;
        public uint FrameSerialHigh;
        public uint VolumeTableGeneration;
        public uint SchedulerResourceGeneration;
        public uint QueueTransactionGeneration;
        public uint SourceLightingGeneration;
        public uint TransportGeneration;
        public uint StatusFlags;
        public uint ConsideredCount;
        public uint EligibleCount;
        public uint AcceptedCount;
        public uint CommittedCount;
        public uint RejectedCount;
        public uint RequestBudget;
        public uint RequestUsed;
        public uint PrimaryRayBudget;
        public uint PrimaryRayUsed;
        public uint SourceTargetRays;
        public uint SourceAchievedRays;
        public uint SourceCapacityShortfall;
        public uint InvalidGenerationCount;
        // Total admitted ray evaluations, including complete source sequences
        // and cached transport-solver evaluations.
        public uint TransportRayUsed;
        public uint OverflowCount;
        public uint FailedCommitCount;
        public uint PendingFreshCount;
        public uint PendingExposedCount;
        public uint PendingRelocationCount;
        public uint PendingSourceCount;
        public uint PendingSolverCount;
        public uint ConvergedCount;
        public uint MaximumFreshAge;
        public uint MaximumExposedAge;
        public uint MaximumRelocationAge;
        public uint MaximumUnpublishedAge;
        public uint MaximumVisibleUnsupportedAge;
        public uint SourceCohortStartFrame;
        public uint SourceCohortCompletionFrame;
        public uint SourceCohortStartCount;
        public uint SourceCohortCompletionCount;
        public uint PropagationGeneration;
        public uint PublishedPropagationGeneration;
        public uint StaticConvergedGeneration;
        public uint StaticConvergencePending;
        public uint HardSourceProbeUsed;
        public uint RoutineSourceProbeUsed;
        public uint CachedSolverProbeUsed;
        // Low/high 16-bit source-repair attribution pairs. The resident field
        // is capped at 32,768 probes, so every exact cause count fits without
        // expanding the fixed 256-byte feedback/readback ABI.
        public uint PackedPendingSourceInvalidAndCardinalityCounts;
        public uint PackedPendingSourceRepairAndGenerationCounts;
        public uint RayBucket0Count;
        public uint RayBucket1Count;
        public uint RayBucket2Count;
        public uint RayBucket3Count;
        public uint RayBucket4Count;
        public uint RayBucket5Count;
        public uint DispatchedLaneCount;
        public uint NoOpLaneCount;
        public uint VisiblePriorityParticipatingProbeCount;
        public uint VisiblePrioritySourceReadyProbeCount;
        public uint VisiblePriorityPublishedProbeCount;
        // GPU-resident tail-certification witness. These occupy the existing
        // feedback words so the fixed 256-byte header and all V1/V2 offsets
        // remain unchanged.
        public uint SolveEpochParticipantCount;
        public uint SolveEpochVisitedCount;
        public uint SolveEpoch;
        // Number of admitted transactions which execute a complete source-ray
        // sequence. This is distinct from AcceptedCount, which also includes
        // cached solver-only work.
        public uint SourceProbeUsed;
        // Number of resident transactions that copied the private transport
        // atlas to the receiver-visible canonical atlas this frame.
        public uint PublishedCount;
    }

    // Kept below the common 128-byte Vulkan minimum push-constant range. The
    // frame header carries mutable counts/generations; this block carries only
    // bindless indices, immutable arena offsets, and the current scheduler
    // stage. That keeps stage dispatch cheap without uploading a second header
    // for every pass.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSimpleDdgiSchedulePushConstants
    {
        public uint ArenaBufferIndex;
        public uint ParamsBufferIndex;
        public uint ProbeStateBufferIndex;
        public uint UpdateQueueBufferIndex;
        public uint RelocationBufferIndex;
        public uint FrameOffsetWords;
        public uint VolumePolicyOffsetWords;
        public uint PreviousVolumePolicyOffsetWords;
        public uint DirtyRegionOffsetWords;
        public uint SchedulerProbeStateOffsetWords;
        public uint CandidateInputOffsetWords;
        public uint CandidateGroupLaneCountsOffsetWords;
        public uint CandidateOutputOffsetWords;
        public uint LaneCandidateCountsOffsetWords;
        public uint LanePrefixesOffsetWords;
        public uint LaneTotalsOffsetWords;
        public uint LaneCursorsOffsetWords;
        public uint LaneAdmissionOffsetWords;
        public uint CountersOffsetWords;
        public uint UpdateRecordsOffsetWords;
        public uint RayBucketCommandsOffsetWords;
        public uint RayBucketMetadataOffsetWords;
        public uint IndirectCommandsOffsetWords;
        public uint OutcomesOffsetWords;
        public uint FeedbackOffsetWords;
        public uint IrradianceAtlasBufferIndex;
        public uint VisibilityAtlasBufferIndex;
        public uint TransportIrradianceAtlasBufferIndex;
        public uint PrivateVisibilityAtlasOffsetWords;
        public uint Stage;
        // Reuses the final reserved word so this ABI remains within Vulkan's
        // guaranteed 128-byte push-constant range.
        public uint ReceiverProbeBufferIndex;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFarFieldVoxelizePushConstants
    {
        public uint ParamsBufferIndex;
        public uint VoxelBufferIndex;
        public uint InstanceBufferIndex;
        public uint InstanceIndex;
        public uint Mode;
        public uint TriangleCount;
        // Pass-specific auxiliary descriptor: voxel material dominance scratch
        // during voxelization, packed distance output during jump flooding.
        public uint AuxiliaryBufferIndex;
        public uint CurrentFrameIndex;
        public uint PageVoxelOffset;
        public uint PageDistanceWordOffset;
        public uint PageTableBufferIndex;
        public uint PageTableEntryIndex;
        public uint PageGeneration;
        public uint DiagnosticFlags;
        public uint PageSourceRevisionLow;
        public uint PageSourceRevisionHigh;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUSkyboxPushConstants
    {
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 InverseProjectionMatrix;
        public uint EnvironmentTextureIndex;
        public float SkyIntensity;
        public float RotationRadians;
        public uint DebugView;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUHiZBuildPushConstants
    {
        public Vector2 SourceDimensions;
        public Vector2 DestinationDimensions;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUBloomPushConstants
    {
        public Vector2 SourceDimensions;
        public Vector2 DestinationDimensions;
        public float Threshold;
        public float Knee;
        public float Radius;
        public uint Mode;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUCompositePushConstants
    {
        public uint SceneColorTextureIndex;
        public uint BloomTextureIndex;
        public uint BloomDebugTextureIndex;
        public uint BloomEnabled;
        public float Exposure;
        public float BloomIntensity;
        public uint ToneMapper;
        public uint DebugViewMode;
        public uint OutputToSrgb;
        public uint EnvironmentDebugView;
        public uint EnvironmentDebugMipLevel;
        public uint AmbientOcclusionDebugTextureIndex;
        public uint AutoExposureEnabled;
        public uint AutoExposureStateBufferIndex;
        public uint Padding0;
        public uint Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUWeightedOitCompositePushConstants
    {
        public uint AccumulationTextureIndex;
        public uint RevealageTextureIndex;
        public uint DebugView;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUAutoExposurePushConstants
    {
        public Vector2 SourceDimensions;
        public uint SceneColorTextureIndex;
        public uint HistogramBufferIndex;
        public uint ExposureStateBufferIndex;
        public float MinLogLuminance;
        public float LogLuminanceRange;
        public float TargetLuminance;
        public float PreviousExposure;
        public float DeltaTime;
        public float AdaptationSpeed;
        public float MinExposure;
        public float MaxExposure;
        public uint Mode;
        public uint SamplingStride;
        public uint HistogramBinCount;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUFogPushConstants
    {
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector4 CameraPositionAndTime;
        public Vector4 ScreenDimensions;
        public Vector4 FogColorAndDensity;
        public Vector4 FogHeightParams;
        public Vector4 FogDistanceParams;
        public Vector4 DirectionalInscatteringColorAndIntensity;
        public Vector4 DirectionalInscatteringDirectionAndExponent;
        public Vector4 SkyColorAndBlend;
        public uint SceneColorTextureIndex;
        public uint DepthTextureIndex;
        public uint EnvironmentTextureIndex;
        public uint Mode;
        public uint ColorMode;
        public uint DebugView;
        public uint DirectionalInscatteringEnabled;
        public uint Padding0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUAntiAliasingPushConstants
    {
        public Vector2 SourceDimensions;
        public Vector2 InvSourceDimensions;
        public uint InputTextureIndex;
        public uint SmaaEdgesTextureIndex;
        public uint SmaaBlendWeightsTextureIndex;
        public uint SmaaAreaTextureIndex;
        public uint SmaaSearchTextureIndex;
        public float FxaaContrastThreshold;
        public float FxaaRelativeThreshold;
        public float FxaaSubpixelBlending;
        public float SmaaThreshold;
        public uint SmaaMaxSearchSteps;
        public uint SmaaMaxSearchStepsDiagonal;
        public float SmaaCornerRounding;
        public uint DebugView;
        public uint OutputToSrgb;
        public uint SmaaQuality;
        public uint SmaaDiagonalEnabled;
        public uint SmaaCornerEnabled;
        public float TaaFeedbackMin;
        public float TaaFeedbackMax;
        public float TaaVelocityRejectionScale;
        public uint TaaHistoryValid;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUAmbientOcclusionPushConstants
    {
        public Matrix4x4 InverseProjectionMatrix;
        public Matrix4x4 ProjectionMatrix;
        public Vector2 SourceDimensions;
        public Vector2 DestinationDimensions;
        public float Radius;
        public float Intensity;
        public float Bias;
        public float Power;
        public uint SampleCount;
        public uint FrameIndex;
        public uint UseSceneNormals;
        public uint Mode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUAmbientOcclusionBlurPushConstants
    {
        public Matrix4x4 InverseProjectionMatrix;
        public Vector2 Dimensions;
        public Vector2 Direction;
        public uint Radius;
        public float DepthSigma;
        public float NormalSigma;
        public uint UseSceneNormals;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUDdgiUpdatePushConstants
    {
        public Vector4 EnvironmentRadianceAndIntensity;
        public Vector4 RelocationParams;
        public uint ProbeCount;
        public uint VolumeCount;
        public uint StartProbeIndex;
        public uint ProbesToUpdate;
        public uint RaysPerProbe;
        public uint FrameIndex;
        public uint IrradianceTexelsPerProbe;
        public uint VisibilityTexelsPerProbe;
        public uint ProbeStateBufferIndex;
        public uint ProbeUpdateQueueBufferIndex;
        public uint RelocationClassificationBufferIndex;
        public uint IrradianceAtlasBufferIndex;
        public uint VisibilityAtlasBufferIndex;
        public uint RayResultScratchBufferIndex;
        public uint RayCapacityPerProbe;
        public uint CurrentFrameIndex;
        public uint Flags;
        public uint LightCount;
        public uint MaxShadedLights;
        public uint DirectionalLightCount;
        public uint LocalLightCount;
        public uint LightSelectionMode;
        public uint PrimaryDirectionalLightIndex;
        public uint SelectedLocalLightIndex;
        public float SelectedLocalLightEnergyScale;
        public uint EmissiveSourceCount;
        public uint EmissiveSourceRevision;
        public uint MaterialTextureMaxCascade;
        public uint FrameSerial;
    }

}

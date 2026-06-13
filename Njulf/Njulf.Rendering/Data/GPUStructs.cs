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
    public struct GPUMeshInfo
    {
        public Vector4 BoundingSphere;
        public uint SkinningDataOffset;
        public uint SkinningDataCount;
        public uint Flags;
        public uint Padding0;
        public Vector4 Padding1;
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
    public struct GPUParticlePushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewMatrix;
        public Matrix4x4 InverseProjectionMatrix;
        public Vector3 CameraPosition;
        public float GlobalSoftParticleDistance;
        public Vector2 ScreenDimensions;
        public uint CurrentFrameIndex;
        public uint ParticleInstanceBufferBaseIndex;
        public uint DepthTextureIndex;
        public uint DebugView;
        public uint SoftParticlesEnabled;
        public uint InstanceOffset;
        public uint Padding0;
        public uint Padding1;
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
        public Vector4 EmissiveOffsetScale;
        public Vector4 TextureRotations;
        public Vector4 TextureTexCoordSets;
        public int AlbedoTextureIndex;
        public int NormalTextureIndex;
        public int MetallicRoughnessTextureIndex;
        public int EmissiveTextureIndex;
        public uint FeatureFlags;
        public int ExtensionDataIndex;
        public uint Reserved0;
        public uint Reserved1;
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
        public int ClearcoatTextureIndex;
        public int ClearcoatRoughnessTextureIndex;
        public int ClearcoatNormalTextureIndex;
        public int SheenColorTextureIndex;
        public int SheenRoughnessTextureIndex;
        public int AnisotropyTextureIndex;
        public int TransmissionTextureIndex;
        public int ThicknessTextureIndex;
        public int SubsurfaceTextureIndex;
        public int Padding0;
        public int Padding1;
        public int Padding2;
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULight
    {
        public Vector3 Position;
        public float Intensity;
        public Vector3 Color;
        public float Range;
        public Vector3 Direction;
        public float SpotAngle;
        public int Type;
        public int Padding0;
        public int Padding1;
        public int Padding2;
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
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPUTiledLightHeader
    {
        public uint LightCount;
        public uint LightOffset;
        public uint Padding0;
        public uint Padding1;
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULightIndex
    {
        public uint LightIndex;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
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
        public uint HiZTextureIndex;
        public uint HiZMipCount;
        public uint OcclusionCullingEnabled;
        public float OcclusionBias;
        public uint DebugAndAoFlags;

        public static uint PackDebugAndAoFlags(
            uint debugViewMode,
            bool ambientOcclusionEnabled,
            uint ambientOcclusionDebugView,
            bool transparentReceiveShadows = true,
            uint transparencyDebugView = 0u)
        {
            return (debugViewMode & DebugViewModeMask) |
                   (ambientOcclusionEnabled ? 1u << AmbientOcclusionEnabledShift : 0u) |
                   ((ambientOcclusionDebugView & DebugViewModeMask) << AmbientOcclusionDebugViewShift) |
                   (transparentReceiveShadows ? 1u << TransparentReceiveShadowsShift : 0u) |
                   ((transparencyDebugView & 0x7Fu) << TransparencyDebugViewShift);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GPULightCullPushConstants
    {
        public Matrix4x4 ViewProjectionMatrix;
        public Matrix4x4 InverseViewProjectionMatrix;
        public Vector3 CameraPosition;
        public float Padding0;
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
}

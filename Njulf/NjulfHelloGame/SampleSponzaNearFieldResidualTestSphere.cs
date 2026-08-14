using System;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

/// <summary>
/// A deliberately obvious screen-visible emitter for comparing canonical
/// DDGI+B3 against the optional C5 near-field residual in Sponza.
/// </summary>
internal static class SampleSponzaNearFieldResidualTestSphere
{
    public const string ObjectName = "Sponza.C5EmissiveTestSphere";
    public const string MaterialName = "Sponza.C5EmissiveTestSphere.Emissive";
    public const string EmissionTextureName = "Sponza.C5EmissiveTestSphere.Checker";
    public const string EmissionTextureSchema = "sponza-c5-emissive-checker/v1";
    public const int EmissionTextureWidth = 16;
    public const int EmissionTextureHeight = 8;
    public const float Radius = 0.45f;
    public const float EmissiveStrength = 12.0f;

    public static CoreVector3 Position { get; } = new(1.25f, 0.58f, 2.0f);
    public static CoreVector3 EmissiveColor { get; } = new(1.0f, 0.24f, 0.04f);

    public static RenderObject Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager,
        TextureManager textureManager)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(meshManager);
        ArgumentNullException.ThrowIfNull(materialManager);
        ArgumentNullException.ThrowIfNull(textureManager);

        MeshHandle mesh = meshManager.RegisterMesh(
            SampleUvSphereMesh.CreateVertices(),
            SampleUvSphereMesh.CreateIndices());
        TextureHandle emissionTexture = CreateEmissionTexture(textureManager);
        MaterialHandle material = materialManager.RegisterMaterialDefinition(
            CreateMaterialDefinition(emissionTexture));
        var sphere = new RenderObject(mesh, material)
        {
            Id = new Guid("c5000001-0000-4000-8000-000000000001"),
            Name = ObjectName,
            WorldMatrix = CoreMatrix4x4.CreateScale(new CoreVector3(Radius)) *
                CoreMatrix4x4.CreateTranslation(Position),
            Visible = true,
            IsStatic = true,
            PersistInSceneDocument = false
        };
        scene.Add(sphere);
        return sphere;
    }

    internal static MaterialDefinition CreateMaterialDefinition(
        TextureHandle emissionTexture)
    {
        if (!emissionTexture.IsValid)
            throw new ArgumentException("Emission texture must be valid.", nameof(emissionTexture));

        return new MaterialDefinition
        {
            Name = MaterialName,
            BaseColorFactor = new CoreVector4(0.03f, 0.008f, 0.003f, 1.0f),
            EmissiveFactor = EmissiveColor,
            EmissiveStrength = EmissiveStrength,
            Emissive = new MaterialTextureBinding
            {
                Texture = emissionTexture,
                Sampler = TextureSamplerDescription.Default,
                TexCoordSet = 0,
                Offset = CoreVector2.Zero,
                Scale = CoreVector2.One,
                RotationRadians = 0f
            },
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.4f,
            AlphaMode = MaterialAlphaMode.Opaque,
            FeatureFlags = MaterialFeatureFlags.EmissiveStrength,
            EmissionGiParticipation = GiParticipationOverride.Enabled
        };
    }

    internal static byte[] CreateEmissionPattern()
    {
        byte[] pixels = new byte[EmissionTextureWidth * EmissionTextureHeight * 4];
        for (int y = 0; y < EmissionTextureHeight; y++)
        {
            for (int x = 0; x < EmissionTextureWidth; x++)
            {
                byte value = ((x + y) & 1) == 0 ? (byte)255 : (byte)24;
                int offset = (y * EmissionTextureWidth + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }

    private static TextureHandle CreateEmissionTexture(TextureManager textureManager)
    {
        const uint mipLevels = 5;
        byte[] pixels = CreateEmissionPattern();
        TextureHandle texture = textureManager.CreateTexture(
            EmissionTextureWidth,
            EmissionTextureHeight,
            Format.R8G8B8A8Srgb,
            mipLevels,
            debugName: EmissionTextureName);
        textureManager.UploadTextureData(
            texture,
            pixels,
            EmissionTextureWidth,
            EmissionTextureHeight,
            Format.R8G8B8A8Srgb,
            generateMipmaps: true);
        TextureTransportStatistics statistics = TextureTransportImage.FromRgba8(
            pixels,
            EmissionTextureWidth,
            EmissionTextureHeight,
            TextureColorSpace.Srgb,
            TextureSemantic.Color,
            CookedHash.Bytes(pixels),
            EmissionTextureSchema).Statistics;
        textureManager.PublishTextureTransportStatistics(texture, statistics);
        return texture;
    }
}

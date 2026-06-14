using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Njulf.Core.Math;

namespace Njulf.Assets;

public sealed class ImpostorGenerator
{
    private readonly IImpostorRenderBackend _backend;

    public ImpostorGenerator(IImpostorRenderBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public ImpostorAssetMetadata Generate(ModelMesh source, ImpostorGenerationSettings settings)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (settings.ViewDirectionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Impostor view direction count must be positive.");
        if (settings.AtlasWidth <= 0 || settings.AtlasHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(settings), "Impostor atlas dimensions must be positive.");
        if (string.IsNullOrWhiteSpace(settings.AtlasAssetId))
            throw new ArgumentException("Impostor atlas asset ID is required.", nameof(settings));

        ImpostorRenderResult result = _backend.Render(source, settings);
        ValidateRenderResult(settings, result);

        return new ImpostorAssetMetadata(
            settings.AtlasAssetId,
            settings.AtlasWidth,
            settings.AtlasHeight,
            settings.ViewDirectionCount,
            settings.Type,
            result.Textures,
            source.BoundingBox,
            settings.Pivot ?? source.BoundingBox.Center,
            result.AlphaCoverage,
            result.DepthScale,
            result.DepthBias,
            result.NormalEncoding);
    }

    private static void ValidateRenderResult(ImpostorGenerationSettings settings, ImpostorRenderResult result)
    {
        if (result.Textures.Count == 0)
            throw new InvalidDataException("Impostor render backend produced no atlas textures.");
        if (!float.IsFinite(result.AlphaCoverage) || result.AlphaCoverage <= 0f || result.AlphaCoverage > 1f)
            throw new InvalidDataException($"Impostor alpha coverage {result.AlphaCoverage} is invalid.");
        if (!float.IsFinite(result.DepthScale) || !float.IsFinite(result.DepthBias))
            throw new InvalidDataException("Impostor depth reconstruction output is invalid.");

        bool hasAlbedo = false;
        foreach (ImpostorAtlasTexture texture in result.Textures)
        {
            if (string.IsNullOrWhiteSpace(texture.AssetId))
                throw new InvalidDataException($"Impostor backend produced a {texture.Kind} texture without an asset ID.");
            if (texture.Kind == ImpostorTextureKind.Albedo)
                hasAlbedo = true;
        }

        if (!hasAlbedo)
            throw new InvalidDataException("Impostor render backend must produce an albedo atlas.");
    }
}

public interface IImpostorRenderBackend
{
    ImpostorRenderResult Render(ModelMesh source, ImpostorGenerationSettings settings);
}

public sealed record ImpostorGenerationSettings
{
    public string AtlasAssetId { get; init; } = string.Empty;
    public int AtlasWidth { get; init; } = 512;
    public int AtlasHeight { get; init; } = 512;
    public int ViewDirectionCount { get; init; } = 16;
    public ImpostorType Type { get; init; } = ImpostorType.Octahedral;
    public Vector3? Pivot { get; init; }
    public bool PreserveAlphaCoverage { get; init; } = true;
    public bool IncludeNormalAtlas { get; init; } = true;
    public bool IncludeDepthAtlas { get; init; } = true;
    public bool IncludeRoughnessMetalnessAtlas { get; init; }
}

public sealed record ImpostorRenderResult(
    IReadOnlyList<ImpostorAtlasTexture> Textures,
    float AlphaCoverage,
    float DepthScale,
    float DepthBias,
    string NormalEncoding);

public sealed class SoftwareImpostorRenderBackend : IImpostorRenderBackend
{
    private readonly string _outputDirectory;

    public SoftwareImpostorRenderBackend(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

        _outputDirectory = outputDirectory;
    }

    public ImpostorRenderResult Render(ModelMesh source, ImpostorGenerationSettings settings)
    {
        Directory.CreateDirectory(_outputDirectory);
        string safeName = SanitizeAssetId(settings.AtlasAssetId);
        var textures = new List<ImpostorAtlasTexture>();

        string albedoPath = Path.Combine(_outputDirectory, $"{safeName}_albedo.ppm");
        WriteAtlas(albedoPath, settings.AtlasWidth, settings.AtlasHeight, source, AtlasChannel.Albedo);
        textures.Add(new ImpostorAtlasTexture(ImpostorTextureKind.Albedo, albedoPath, TextureColorSpace.Srgb));

        if (settings.IncludeNormalAtlas)
        {
            string normalPath = Path.Combine(_outputDirectory, $"{safeName}_normal.ppm");
            WriteAtlas(normalPath, settings.AtlasWidth, settings.AtlasHeight, source, AtlasChannel.Normal);
            textures.Add(new ImpostorAtlasTexture(ImpostorTextureKind.Normal, normalPath, TextureColorSpace.Linear));
        }

        if (settings.IncludeDepthAtlas)
        {
            string depthPath = Path.Combine(_outputDirectory, $"{safeName}_depth.ppm");
            WriteAtlas(depthPath, settings.AtlasWidth, settings.AtlasHeight, source, AtlasChannel.Depth);
            textures.Add(new ImpostorAtlasTexture(ImpostorTextureKind.Depth, depthPath, TextureColorSpace.Linear));
        }

        if (settings.IncludeRoughnessMetalnessAtlas)
        {
            string materialPath = Path.Combine(_outputDirectory, $"{safeName}_roughness_metalness.ppm");
            WriteAtlas(materialPath, settings.AtlasWidth, settings.AtlasHeight, source, AtlasChannel.RoughnessMetalness);
            textures.Add(new ImpostorAtlasTexture(ImpostorTextureKind.RoughnessMetalness, materialPath, TextureColorSpace.Linear));
        }

        float depthScale = MathF.Max(0.0001f, source.BoundingBox.Size.Length());
        return new ImpostorRenderResult(textures, EstimateAlphaCoverage(source), depthScale, 0f, "octahedral");
    }

    private static void WriteAtlas(string path, int width, int height, ModelMesh source, AtlasChannel channel)
    {
        byte[] pixels = new byte[checked(width * height * 3)];
        BoundingBox bounds = source.BoundingBox;
        Vector3 size = bounds.Size;
        float invW = width > 1 ? 1f / (width - 1) : 0f;
        float invH = height > 1 ? 1f / (height - 1) : 0f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = x * invW;
                float v = y * invH;
                int offset = (y * width + x) * 3;
                WritePixel(pixels, offset, channel, u, v, bounds, size);
            }
        }

        using FileStream stream = File.Create(path);
        byte[] header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        stream.Write(pixels);
    }

    private static void WritePixel(byte[] pixels, int offset, AtlasChannel channel, float u, float v, BoundingBox bounds, Vector3 size)
    {
        switch (channel)
        {
            case AtlasChannel.Normal:
                pixels[offset + 0] = 128;
                pixels[offset + 1] = 128;
                pixels[offset + 2] = 255;
                break;
            case AtlasChannel.Depth:
                byte depth = (byte)Math.Clamp((int)(v * 255f), 0, 255);
                pixels[offset + 0] = depth;
                pixels[offset + 1] = depth;
                pixels[offset + 2] = depth;
                break;
            case AtlasChannel.RoughnessMetalness:
                pixels[offset + 0] = 204;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 255;
                break;
            default:
                pixels[offset + 0] = (byte)Math.Clamp((int)(u * 255f), 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp((int)(v * 255f), 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp((int)(Math.Clamp(size.Length(), 0f, 16f) / 16f * 255f), 0, 255);
                break;
        }
    }

    private static float EstimateAlphaCoverage(ModelMesh source)
    {
        if (source.Vertices.Length == 0)
            return 1f;

        BoundingBox bounds = source.BoundingBox;
        float area = MathF.Max(0.0001f, bounds.Size.X * bounds.Size.Y);
        float projectedArea = MathF.Max(0.0001f, bounds.Extents.X * bounds.Extents.Y * 4f);
        return Math.Clamp(projectedArea / area, 0.05f, 1f);
    }

    private static string SanitizeAssetId(string assetId)
    {
        var builder = new StringBuilder(assetId.Length);
        foreach (char c in assetId)
            builder.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return builder.ToString();
    }

    private enum AtlasChannel
    {
        Albedo,
        Normal,
        Depth,
        RoughnessMetalness
    }
}

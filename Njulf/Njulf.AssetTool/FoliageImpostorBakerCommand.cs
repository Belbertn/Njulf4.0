using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Foliage;
using Njulf.Rendering.Debug;
using StbImageSharp;

namespace Njulf.AssetTool;

/// <summary>
/// Deterministically packs source-rendered foliage views into the three
/// runtime atlases. Rendering the views remains an offline content step; this
/// command validates and bakes those captures into the exact runtime contract.
/// </summary>
internal static class FoliageImpostorBakerCommand
{
    private const int SchemaVersion = 1;
    private const int MaximumAtlasDimension = 16_384;
    private const long MaximumAtlasPixels = 64L * 1024L * 1024L;
    private const long MaximumEncodedInputBytes = 256L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public static int Run(string[] args)
    {
        if (args.Length < 2 ||
            !string.Equals(args[0], "bake", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "foliage-impostor requires 'bake <manifest.json> --out <folder> [--name <asset-name>]'.");
        }

        string manifestPath = args[1];
        string? outputDirectory = null;
        string? requestedName = null;
        for (int index = 2; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--out":
                    outputDirectory = RequireValue(args, ref index, "--out");
                    break;
                case "--name":
                    requestedName = RequireValue(args, ref index, "--name");
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown foliage-impostor option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("foliage-impostor bake requires --out <folder>.");

        BakeResult result = Bake(manifestPath, outputDirectory, requestedName);
        Console.WriteLine(
            $"Baked {result.ViewCount} foliage impostor views into " +
            $"{result.AtlasWidth}x{result.AtlasHeight} atlases: " +
            $"'{result.MetadataPath}' (sha256={result.ContentHash}).");
        return 0;
    }

    internal static BakeResult Bake(
        string manifestPath,
        string outputDirectory,
        string? requestedName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string fullManifestPath = Path.GetFullPath(manifestPath);
        string manifestDirectory = Path.GetDirectoryName(fullManifestPath)
            ?? throw new InvalidDataException(
                $"Impostor manifest '{fullManifestPath}' has no parent directory.");
        FoliageImpostorBakeManifest manifest = JsonSerializer.Deserialize<
            FoliageImpostorBakeManifest>(
                ReadBoundedFile(fullManifestPath, 4L * 1024L * 1024L),
                JsonOptions)
            ?? throw new InvalidDataException(
                $"Impostor manifest '{fullManifestPath}' is empty or invalid.");
        ValidateManifest(manifest);

        string assetName = SanitizeAssetName(
            string.IsNullOrWhiteSpace(requestedName)
                ? manifest.Name
                : requestedName);
        int viewCount = manifest.Views.Count;
        var decodedViews = new DecodedView[viewCount];
        int viewWidth = 0;
        int viewHeight = 0;
        for (int index = 0; index < viewCount; index++)
        {
            FoliageImpostorBakeView view = manifest.Views[index];
            DecodedImage albedo = DecodeRgba(
                ResolveInputPath(manifestDirectory, view.AlbedoOpacity),
                "albedo/opacity");
            DecodedImage normal = DecodeRgba(
                ResolveInputPath(manifestDirectory, view.Normal),
                "normal");
            DecodedImage depth = DecodeRgba(
                ResolveInputPath(manifestDirectory, view.Depth),
                "depth");
            if (normal.Width != albedo.Width || normal.Height != albedo.Height ||
                depth.Width != albedo.Width || depth.Height != albedo.Height)
            {
                throw new InvalidDataException(
                    $"Impostor view {index} channel dimensions do not match.");
            }
            if (index == 0)
            {
                viewWidth = albedo.Width;
                viewHeight = albedo.Height;
            }
            else if (albedo.Width != viewWidth || albedo.Height != viewHeight)
            {
                throw new InvalidDataException(
                    $"Impostor view {index} is {albedo.Width}x{albedo.Height}; " +
                    $"all views must match the first view's {viewWidth}x{viewHeight} dimensions.");
            }

            BakeVector3 direction = Normalize(view.Direction);
            decodedViews[index] = new DecodedView(
                direction,
                albedo.Pixels,
                normal.Pixels,
                depth.Pixels);
        }

        (int columns, int rows) = ResolveGrid(viewCount, viewWidth, viewHeight);
        int atlasWidth = checked(columns * viewWidth);
        int atlasHeight = checked(rows * viewHeight);
        long atlasPixels = checked((long)atlasWidth * atlasHeight);
        if (atlasPixels > MaximumAtlasPixels)
        {
            throw new InvalidDataException(
                $"The {atlasWidth}x{atlasHeight} impostor atlas exceeds the " +
                $"{MaximumAtlasPixels:N0}-pixel offline bake limit.");
        }

        int atlasBytes = checked((int)(atlasPixels * 4L));
        byte[] albedoAtlas = new byte[atlasBytes];
        byte[] normalAtlas = new byte[atlasBytes];
        byte[] depthAtlas = new byte[atlasBytes];
        var bakedViews = new List<FoliageImpostorBakedView>(viewCount);
        for (int index = 0; index < viewCount; index++)
        {
            int column = index % columns;
            int row = index / columns;
            int x = checked(column * viewWidth);
            int y = checked(row * viewHeight);
            CopyView(decodedViews[index].AlbedoOpacity, albedoAtlas,
                viewWidth, viewHeight, atlasWidth, x, y);
            CopyView(decodedViews[index].Normal, normalAtlas,
                viewWidth, viewHeight, atlasWidth, x, y);
            CopyView(decodedViews[index].Depth, depthAtlas,
                viewWidth, viewHeight, atlasWidth, x, y);
            bakedViews.Add(new FoliageImpostorBakedView
            {
                Direction = decodedViews[index].Direction,
                AtlasRectangle = new BakeVector4(
                    (float)x / atlasWidth,
                    (float)y / atlasHeight,
                    (float)viewWidth / atlasWidth,
                    (float)viewHeight / atlasHeight)
            });
        }

        string contentHash = ComputeContentHash(
            manifest,
            bakedViews,
            atlasWidth,
            atlasHeight,
            albedoAtlas,
            normalAtlas,
            depthAtlas);
        string hashPrefix = contentHash[..16];
        string albedoFileName =
            $"{assetName}.{hashPrefix}.albedo-opacity.png";
        string normalFileName = $"{assetName}.{hashPrefix}.normal.png";
        string depthFileName = $"{assetName}.{hashPrefix}.depth.png";
        string metadataFileName = $"{assetName}.foliage-impostor.json";
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);

        PngScreenshotEncoder.WriteAtomic(
            Path.Combine(fullOutputDirectory, albedoFileName),
            albedoAtlas,
            atlasWidth,
            atlasHeight,
            ScreenshotPixelFormat.Rgba8);
        PngScreenshotEncoder.WriteAtomic(
            Path.Combine(fullOutputDirectory, normalFileName),
            normalAtlas,
            atlasWidth,
            atlasHeight,
            ScreenshotPixelFormat.Rgba8);
        PngScreenshotEncoder.WriteAtomic(
            Path.Combine(fullOutputDirectory, depthFileName),
            depthAtlas,
            atlasWidth,
            atlasHeight,
            ScreenshotPixelFormat.Rgba8);

        var metadata = new FoliageImpostorBakedAsset
        {
            SchemaVersion = SchemaVersion,
            AlbedoOpacityAtlasPath = albedoFileName,
            NormalAtlasPath = normalFileName,
            DepthAtlasPath = depthFileName,
            ViewCount = viewCount,
            AtlasWidth = atlasWidth,
            AtlasHeight = atlasHeight,
            Views = bakedViews,
            SourceBounds = manifest.SourceBounds,
            Pivot = manifest.Pivot,
            Scale = manifest.Scale,
            ContentHash = contentHash
        };
        string metadataPath = Path.Combine(
            fullOutputDirectory,
            metadataFileName);
        WriteJsonAtomic(metadataPath, metadata);
        return new BakeResult(
            metadataPath,
            contentHash,
            viewCount,
            atlasWidth,
            atlasHeight);
    }

    private static void ValidateManifest(FoliageImpostorBakeManifest manifest)
    {
        if (manifest.Views.Count is <= 0 or > FoliageImpostorAsset.MaximumViewCount)
        {
            throw new InvalidDataException(
                $"Impostor manifests require 1..{FoliageImpostorAsset.MaximumViewCount} views.");
        }
        if (!float.IsFinite(manifest.Scale) || manifest.Scale <= 0f ||
            !IsFinite(manifest.Pivot) ||
            !IsFinite(manifest.SourceBounds.Min) ||
            !IsFinite(manifest.SourceBounds.Max) ||
            manifest.SourceBounds.Max.X <= manifest.SourceBounds.Min.X ||
            manifest.SourceBounds.Max.Y <= manifest.SourceBounds.Min.Y ||
            manifest.SourceBounds.Max.Z <= manifest.SourceBounds.Min.Z)
        {
            throw new InvalidDataException(
                "Impostor source bounds, pivot, and scale must be finite and non-degenerate.");
        }
        for (int index = 0; index < manifest.Views.Count; index++)
        {
            FoliageImpostorBakeView view = manifest.Views[index];
            if (!IsFinite(view.Direction) || LengthSquared(view.Direction) <= 1e-8f ||
                string.IsNullOrWhiteSpace(view.AlbedoOpacity) ||
                string.IsNullOrWhiteSpace(view.Normal) ||
                string.IsNullOrWhiteSpace(view.Depth))
            {
                throw new InvalidDataException(
                    $"Impostor view {index} requires a finite non-zero direction and all three source images.");
            }
        }
    }

    private static (int Columns, int Rows) ResolveGrid(
        int viewCount,
        int viewWidth,
        int viewHeight)
    {
        int maximumColumns = MaximumAtlasDimension / viewWidth;
        int maximumRows = MaximumAtlasDimension / viewHeight;
        if (maximumColumns <= 0 || maximumRows <= 0 ||
            (long)maximumColumns * maximumRows < viewCount)
        {
            throw new InvalidDataException(
                $"The {viewWidth}x{viewHeight} source views cannot fit in a " +
                $"{MaximumAtlasDimension}x{MaximumAtlasDimension} atlas.");
        }

        int columns = Math.Min(
            maximumColumns,
            Math.Max(1, (int)Math.Ceiling(Math.Sqrt(viewCount))));
        int rows = (viewCount + columns - 1) / columns;
        while (rows > maximumRows)
        {
            columns++;
            rows = (viewCount + columns - 1) / columns;
        }
        return (columns, rows);
    }

    private static DecodedImage DecodeRgba(string path, string semantic)
    {
        byte[] encoded = ReadBoundedFile(path, MaximumEncodedInputBytes);
        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(
                encoded,
                ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                IndexOutOfRangeException)
        {
            throw new InvalidDataException(
                $"Could not decode impostor {semantic} image '{path}'.",
                exception);
        }
        if (image.Width <= 0 || image.Height <= 0 ||
            image.Data.Length != checked(image.Width * image.Height * 4))
        {
            throw new InvalidDataException(
                $"Impostor {semantic} image '{path}' did not decode to complete RGBA8 pixels.");
        }
        return new DecodedImage(image.Width, image.Height, image.Data);
    }

    private static byte[] ReadBoundedFile(string path, long maximumBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"Required impostor input '{path}' does not exist.", path);
        if (info.Length is <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Impostor input '{path}' is {info.Length} bytes; expected 1..{maximumBytes} bytes.");
        }
        return File.ReadAllBytes(path);
    }

    private static string ResolveInputPath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path)
            ? path
            : Path.Combine(baseDirectory, path));

    private static void CopyView(
        byte[] source,
        byte[] destination,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationX,
        int destinationY)
    {
        int sourceStride = checked(sourceWidth * 4);
        int destinationStride = checked(destinationWidth * 4);
        for (int row = 0; row < sourceHeight; row++)
        {
            Buffer.BlockCopy(
                source,
                row * sourceStride,
                destination,
                checked((destinationY + row) * destinationStride +
                    destinationX * 4),
                sourceStride);
        }
    }

    private static string ComputeContentHash(
        FoliageImpostorBakeManifest manifest,
        IReadOnlyList<FoliageImpostorBakedView> views,
        int atlasWidth,
        int atlasHeight,
        byte[] albedo,
        byte[] normal,
        byte[] depth)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes("NJULF-FOLIAGE-IMPOSTOR\0"));
        AppendInt32(hash, SchemaVersion);
        AppendInt32(hash, atlasWidth);
        AppendInt32(hash, atlasHeight);
        AppendVector3(hash, manifest.SourceBounds.Min);
        AppendVector3(hash, manifest.SourceBounds.Max);
        AppendVector3(hash, manifest.Pivot);
        AppendSingle(hash, manifest.Scale);
        AppendInt32(hash, views.Count);
        foreach (FoliageImpostorBakedView view in views)
        {
            AppendVector3(hash, view.Direction);
            AppendSingle(hash, view.AtlasRectangle.X);
            AppendSingle(hash, view.AtlasRectangle.Y);
            AppendSingle(hash, view.AtlasRectangle.Z);
            AppendSingle(hash, view.AtlasRectangle.W);
        }
        hash.AppendData(albedo);
        hash.AppendData(normal);
        hash.AppendData(depth);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendVector3(
        IncrementalHash hash,
        BakeVector3 value)
    {
        AppendSingle(hash, value.X);
        AppendSingle(hash, value.Y);
        AppendSingle(hash, value.Z);
    }

    private static void AppendSingle(IncrementalHash hash, float value) =>
        AppendInt32(hash, BitConverter.SingleToInt32Bits(value));

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void WriteJsonAtomic(
        string destinationPath,
        FoliageImpostorBakedAsset metadata)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(metadata, JsonOptions) + Environment.NewLine);
        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Impostor metadata path '{destinationPath}' has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.WriteThrough))
            {
                output.Write(payload);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(destinationPath))
                File.Replace(temporaryPath, destinationPath, null, true);
            else
                File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string SanitizeAssetName(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "foliage" : value.Trim();
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException(
                $"Impostor asset name '{name}' is not a valid file name.");
        }
        return name;
    }

    private static BakeVector3 Normalize(BakeVector3 value)
    {
        float inverseLength = 1f / MathF.Sqrt(LengthSquared(value));
        return new BakeVector3(
            value.X * inverseLength,
            value.Y * inverseLength,
            value.Z * inverseLength);
    }

    private static float LengthSquared(BakeVector3 value) =>
        value.X * value.X + value.Y * value.Y + value.Z * value.Z;

    private static bool IsFinite(BakeVector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static string RequireValue(
        string[] args,
        ref int index,
        string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        index++;
        return args[index];
    }

    internal readonly record struct BakeResult(
        string MetadataPath,
        string ContentHash,
        int ViewCount,
        int AtlasWidth,
        int AtlasHeight);

    private readonly record struct DecodedImage(
        int Width,
        int Height,
        byte[] Pixels);

    private readonly record struct DecodedView(
        BakeVector3 Direction,
        byte[] AlbedoOpacity,
        byte[] Normal,
        byte[] Depth);
}

internal sealed class FoliageImpostorBakeManifest
{
    public string? Name { get; init; }
    public BakeBounds SourceBounds { get; init; } = new();
    public BakeVector3 Pivot { get; init; }
    public float Scale { get; init; } = 1f;
    public List<FoliageImpostorBakeView> Views { get; init; } = [];
}

internal sealed class FoliageImpostorBakeView
{
    public BakeVector3 Direction { get; init; }
    public string AlbedoOpacity { get; init; } = string.Empty;
    public string Normal { get; init; } = string.Empty;
    public string Depth { get; init; } = string.Empty;
}

internal sealed class FoliageImpostorBakedAsset
{
    public int SchemaVersion { get; init; }
    public string AlbedoOpacityAtlasPath { get; init; } = string.Empty;
    public string NormalAtlasPath { get; init; } = string.Empty;
    public string DepthAtlasPath { get; init; } = string.Empty;
    public int ViewCount { get; init; }
    public int AtlasWidth { get; init; }
    public int AtlasHeight { get; init; }
    public List<FoliageImpostorBakedView> Views { get; init; } = [];
    public BakeBounds SourceBounds { get; init; } = new();
    public BakeVector3 Pivot { get; init; }
    public float Scale { get; init; }
    public string ContentHash { get; init; } = string.Empty;
}

internal sealed class FoliageImpostorBakedView
{
    public BakeVector3 Direction { get; init; }
    public BakeVector4 AtlasRectangle { get; init; }
}

internal sealed class BakeBounds
{
    public BakeVector3 Min { get; init; }
    public BakeVector3 Max { get; init; }
}

internal readonly record struct BakeVector3(float X, float Y, float Z);
internal readonly record struct BakeVector4(float X, float Y, float Z, float W);

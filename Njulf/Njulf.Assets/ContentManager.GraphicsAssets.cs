using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Assets.Scenes;
using StbImageSharp;

namespace Njulf.Assets;

public partial class ContentManager
{
    private sealed record FontDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public string Atlas { get; init; } = "";
        public float LineHeight { get; init; }
        public int FallbackCodePoint { get; init; } = 63;
        public SpriteGlyph[] Glyphs { get; init; } = [];
        public SpriteKerning[] Kerning { get; init; } = [];
        [JsonIgnore] public Texture? LoadedAtlas { get; init; }
    }
    private static readonly JsonSerializerOptions FontJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static string FontAtlasPath(FontDocument font, string path)
    {
        if (string.IsNullOrWhiteSpace(font.Atlas) || Path.IsPathRooted(font.Atlas) || !font.Atlas.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A bitmap font requires a relative PNG atlas path.");
        return Path.GetFullPath(font.Atlas, Path.GetDirectoryName(path)!);
    }
    private static readonly JsonSerializerOptions MaterialJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record MaterialDocument
    {
        public int SchemaVersion { get; init; } = 1;
        public string Name { get; init; } = "DefaultMaterial";
        public SceneColor BaseColor { get; init; } = new(1, 1, 1, 1);
        public float Metallic { get; init; }
        public float Roughness { get; init; } = 1;
        public SceneVector3 Emissive { get; init; }
        public float EmissiveStrength { get; init; } = 1;
        public MaterialAlphaMode AlphaMode { get; init; }
        public float AlphaCutoff { get; init; } = .5f;
        public bool DoubleSided { get; init; }
        public string? BaseColorTexturePath { get; init; }
        public string? NormalTexturePath { get; init; }
        public string? MetallicRoughnessTexturePath { get; init; }
        public string? OcclusionTexturePath { get; init; }
        public string? EmissiveTexturePath { get; init; }

        public MaterialDefinition Definition => MaterialDefinition.Default with
        {
            Name = Name, BaseColorFactor = new Vector4(BaseColor.R, BaseColor.G, BaseColor.B, BaseColor.A),
            MetallicFactor = Metallic, RoughnessFactor = Roughness,
            EmissiveFactor = new Vector3(Emissive.X, Emissive.Y, Emissive.Z), EmissiveStrength = EmissiveStrength,
            AlphaMode = AlphaMode, AlphaCutoff = AlphaCutoff, DoubleSided = DoubleSided
        };

        public IEnumerable<(MaterialTextureSlot Slot, string Path)> Textures(string materialPath)
        {
            string?[] paths = [BaseColorTexturePath, NormalTexturePath, MetallicRoughnessTexturePath, OcclusionTexturePath, EmissiveTexturePath];
            for (int i = 0; i < paths.Length; i++)
                if (!string.IsNullOrWhiteSpace(paths[i]))
                    yield return ((MaterialTextureSlot)i, Path.GetFullPath(paths[i]!, Path.GetDirectoryName(materialPath)!));
        }
    }

    private static ContentLoadOptions TextureOptions(MaterialTextureSlot slot) => new()
    {
        TextureColorSpace = slot is MaterialTextureSlot.BaseColor or MaterialTextureSlot.Emissive
            ? TextureColorSpace.Srgb : TextureColorSpace.Linear
    };

    private static byte[] ReadContentSnapshot(string path, int maximumBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException($"Content file '{path}' must contain between 1 and {maximumBytes} bytes.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static object PrepareGraphicsAsset<T>(string path)
    {
        if (typeof(T) == typeof(SpriteFont))
        {
            if (!path.EndsWith(".njfont.json", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Bitmap fonts use .njfont.json files.");
            var font = JsonSerializer.Deserialize<FontDocument>(ReadContentSnapshot(path, 4 * 1024 * 1024), FontJsonOptions)
                ?? throw new InvalidDataException("Empty bitmap font.");
            if (font.SchemaVersion != 1 || font.Glyphs == null || font.Kerning == null) throw new InvalidDataException("Invalid bitmap font schema.");
            _ = FontAtlasPath(font, path);
            return font;
        }
        if (typeof(T) == typeof(Material))
        {
            if (!path.EndsWith(".njmaterial.json", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Material assets use .njmaterial.json files.");
            var document = JsonSerializer.Deserialize<MaterialDocument>(ReadContentSnapshot(path, 1024 * 1024), MaterialJsonOptions)
                ?? throw new InvalidDataException($"Material '{path}' is empty.");
            if (document.SchemaVersion != 1) throw new InvalidDataException($"Unsupported material schema {document.SchemaVersion}.");
            return document;
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
            throw new NotSupportedException("Standalone textures currently support PNG and JPEG.");
        byte[] bytes = ReadContentSnapshot(path, 256 * 1024 * 1024);
        bool png = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        bool jpeg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpeg) throw new InvalidDataException($"Texture '{path}' is not PNG or JPEG image data.");
        using var headerStream = new MemoryStream(bytes, writable: false);
        ImageInfo info = ImageInfo.FromStream(headerStream) ?? throw new InvalidDataException($"Invalid image header: '{path}'.");
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 64L * 1024 * 1024)
            throw new InvalidDataException($"Texture '{path}' exceeds the 64-megapixel decode limit or has invalid dimensions.");
        ImageResult image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
        if (image.Width != info.Width || image.Height != info.Height || image.Data.LongLength != (long)info.Width * info.Height * 4)
            throw new InvalidDataException($"Texture '{path}' decoded to inconsistent dimensions.");
        return image;
    }

    private string GraphicsAssetKey<T>(string path, ContentLoadOptions options)
    {
        if (typeof(T) == typeof(Texture) && options.TextureColorSpace is not (TextureColorSpace.Srgb or TextureColorSpace.Linear))
            throw new ArgumentOutOfRangeException(nameof(options.TextureColorSpace));
        return typeof(T).FullName + "|" + path + (typeof(T) == typeof(Texture) ? "|" + options.TextureColorSpace : "");
    }

    private T LoadGraphicsAsset<T>(string path, ContentLoadOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(GetFullPath(path));
        string key = GraphicsAssetKey<T>(fullPath, options);
        lock (_stateLock) { ThrowIfDisposed(); if (TryGetCachedAsset(key, out var cached)) return (T)cached; }
        object prepared = PrepareGraphicsAsset<T>(fullPath);
        IContentScope? dependencies = null;
        try
        {
            var assignments = new List<MaterialTextureAssignment>();
            if (prepared is FontDocument font)
            {
                dependencies = CreateScope();
                prepared = font with { LoadedAtlas = dependencies.Load<Texture>(FontAtlasPath(font, fullPath), new ContentLoadOptions { TextureColorSpace = TextureColorSpace.Linear }) };
            }
            if (prepared is MaterialDocument document)
            {
                dependencies = CreateScope();
                foreach (var (slot, texturePath) in document.Textures(fullPath))
                    assignments.Add(new(slot, dependencies.Load<Texture>(texturePath, TextureOptions(slot))));
            }
            return PublishGraphicsAsset<T>(key, prepared, options, assignments.ToArray(), dependencies);
        }
        catch (Exception failure)
        {
            try { ReleaseUnusedDependencies(dependencies); }
            catch (Exception cleanup) { throw new AggregateException("Material loading and dependency rollback failed.", failure, cleanup); }
            throw;
        }
    }

    private async Task<T> LoadGraphicsAssetAsync<T>(string path, ContentLoadOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(GetFullPath(path));
        string key = GraphicsAssetKey<T>(fullPath, options);
        lock (_stateLock) { ThrowIfDisposed(); if (TryGetCachedAsset(key, out var cached)) return (T)cached; }
        var request = new ContentPreloadRequest(path);
        ReportContentProgress(options.Progress, request, ContentLoadStage.Preparing, null);
        object prepared = await Task.Run(() => PrepareGraphicsAsset<T>(fullPath), cancellationToken).ConfigureAwait(false);
        IContentScope? dependencies = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignments = new List<MaterialTextureAssignment>();
            if (prepared is FontDocument font)
            {
                dependencies = CreateScope();
                prepared = font with { LoadedAtlas = await dependencies.LoadAsync<Texture>(FontAtlasPath(font, fullPath), new ContentLoadOptions { TextureColorSpace = TextureColorSpace.Linear }, cancellationToken).ConfigureAwait(false) };
            }
            if (prepared is MaterialDocument document)
            {
                dependencies = CreateScope();
                foreach (var (slot, texturePath) in document.Textures(fullPath))
                    assignments.Add(new(slot, await dependencies.LoadAsync<Texture>(texturePath, TextureOptions(slot), cancellationToken).ConfigureAwait(false)));
            }
            ReportContentProgress(options.Progress, request, ContentLoadStage.WaitingForUpload, null);
            return await _contentUploadDispatcher!.DispatchAsync(() =>
            {
                ReportContentProgress(options.Progress, request, ContentLoadStage.Uploading, null);
                return PublishGraphicsAsset<T>(key, prepared, options, assignments.ToArray(), dependencies);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            try
            {
                if (dependencies != null)
                    await ReleaseAfterFailedLoadAsync(() => ReleaseUnusedDependencies(dependencies)).ConfigureAwait(false);
            }
            catch (Exception cleanup) { throw new AggregateException("Material loading and dependency rollback failed.", failure, cleanup); }
            throw;
        }
    }

    private T PublishGraphicsAsset<T>(string key, object prepared, ContentLoadOptions options,
        MaterialTextureAssignment[] assignments, IContentScope? dependencies)
    {
        lock (_uploadLock)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (TryGetCachedAsset(key, out var cached)) { dependencies?.Dispose(); return (T)cached; }
                ValidateAcquisition(_currentAcquisition.Value!);
            }
            GraphicsDevice device = GraphicsDeviceProvider?.Invoke()
                ?? throw new InvalidOperationException("Standalone graphics assets require a GraphicsDeviceProvider. Register rendering services first.");
            object asset = prepared switch
            {
                ImageResult image => device.CreateTexture2D(image.Width, image.Height, image.Data, options.TextureColorSpace),
                FontDocument font => device.CreateSpriteFont(font.LoadedAtlas!, font.LineHeight, font.FallbackCodePoint, font.Glyphs, font.Kerning),
                MaterialDocument material => device.CreateMaterial(material.Definition, assignments),
                _ => throw new InvalidOperationException("Unknown graphics asset.")
            };
            lock (_stateLock)
            {
                PublishWithDependencies(key, asset, dependencies);
            }
            return (T)asset;
        }
    }
}

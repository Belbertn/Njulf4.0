using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Assets.Validation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

public sealed record SampleKhronosMaterialGiRenderedGateOptions(
    string ManifestPath,
    string GateReportPath,
    string CookedRoot,
    string CapturePath,
    string ReportPath)
{
    public static SampleKhronosMaterialGiRenderedGateOptions Create(
        string manifestPath,
        string gateReportPath,
        string cookedRoot,
        string capturePath,
        string reportPath)
    {
        string manifest = NormalizePath(manifestPath, nameof(manifestPath));
        string gateReport = NormalizePath(gateReportPath, nameof(gateReportPath));
        string cooked = NormalizePath(cookedRoot, nameof(cookedRoot));
        string capture = NormalizePath(capturePath, nameof(capturePath));
        string report = NormalizePath(reportPath, nameof(reportPath));

        RequireExtension(manifest, ".json", nameof(manifestPath));
        RequireExtension(gateReport, ".json", nameof(gateReportPath));
        RequireExtension(capture, ".pfm", nameof(capturePath));
        RequireExtension(report, ".json", nameof(reportPath));

        string[] paths = [manifest, gateReport, cooked, capture, report];
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ArgumentException("Khronos rendered-gate input and output paths must be distinct.");

        string cookedPrefix = cooked.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (capture.StartsWith(cookedPrefix, StringComparison.OrdinalIgnoreCase) ||
            report.StartsWith(cookedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Khronos rendered-gate outputs cannot be written inside the authenticated cooked root.");
        }

        return new SampleKhronosMaterialGiRenderedGateOptions(
            manifest,
            gateReport,
            cooked,
            capture,
            report);
    }

    private static string NormalizePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A non-empty path is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static void RequireExtension(string path, string extension, string parameterName)
    {
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Path '{path}' must use the '{extension}' extension.",
                parameterName);
        }
    }
}

public sealed record SampleKhronosMaterialGiLayoutItem(
    string Name,
    BoundingBox Bounds,
    int RenderObjectCount);

public sealed record SampleKhronosMaterialGiLayoutPlacement(
    string Name,
    float UniformScale,
    Vector3 Translation,
    Guid StableBaseId);

/// <summary>
/// Pure deterministic layout used by both the runtime gate and unit tests.
/// Every source is normalized independently so small Khronos fixtures remain
/// visible without allowing their source-space scale to alter the framing.
/// </summary>
public static class SampleKhronosMaterialGiLayout
{
    public const float TargetMaximumDimension = 2.35f;
    public const float HorizontalSpacing = 3.2f;
    public const float GroundHeight = 0.15f;

    public static IReadOnlyList<SampleKhronosMaterialGiLayoutPlacement> Create(
        IReadOnlyList<SampleKhronosMaterialGiLayoutItem> items,
        string commit)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        if (items.Count == 0)
            throw new ArgumentException("At least one Khronos model is required.", nameof(items));

        var placements = new SampleKhronosMaterialGiLayoutPlacement[items.Count];
        float half = (items.Count - 1) * HorizontalSpacing * 0.5f;
        for (int index = 0; index < items.Count; index++)
        {
            SampleKhronosMaterialGiLayoutItem item = items[index] ??
                throw new ArgumentException("Layout items cannot be null.", nameof(items));
            if (string.IsNullOrWhiteSpace(item.Name) || item.RenderObjectCount <= 0)
                throw new ArgumentException("Layout items require a name and render objects.", nameof(items));

            ValidateBounds(item.Bounds, item.Name);
            Vector3 size = item.Bounds.Size;
            float maximumDimension = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            float scale = TargetMaximumDimension / maximumDimension;
            Vector3 center = item.Bounds.Center;
            float targetX = index * HorizontalSpacing - half;
            var translation = new Vector3(
                targetX - center.X * scale,
                GroundHeight - item.Bounds.Min.Y * scale,
                -center.Z * scale);
            placements[index] = new SampleKhronosMaterialGiLayoutPlacement(
                item.Name,
                scale,
                translation,
                CreateStableId(commit, item.Name, -1));
        }

        return placements;
    }

    public static Guid CreateStableId(string commit, string assetName, int renderObjectIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"khronos-material-gi-rendered/v1\0{commit}\0{assetName}\0{renderObjectIndex}"));
        Span<byte> guidBytes = digest.AsSpan(0, 16);
        // RFC 4122 version/variant bits make the textual value conventional
        // while retaining deterministic identity.
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    private static void ValidateBounds(BoundingBox bounds, string name)
    {
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;
        if (!IsFinite(min) || !IsFinite(max) ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z)
        {
            throw new InvalidDataException($"Khronos model '{name}' has invalid bounds {bounds}.");
        }

        Vector3 size = bounds.Size;
        float maximumDimension = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        if (!float.IsFinite(maximumDimension) || maximumDimension <= 1e-5f)
            throw new InvalidDataException($"Khronos model '{name}' has degenerate bounds {bounds}.");
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public sealed record SampleKhronosRuntimeMaterialEvidence(
    int Index,
    uint Generation,
    string Name,
    MaterialShadingModel ShadingModel);

public sealed record SampleKhronosUnlitRenderObjectEvidence(
    Guid RenderObjectId,
    string RenderObjectName,
    int MaterialIndex,
    string MaterialName);

public sealed record SampleKhronosMaterialGiRenderedAssetEvidence(
    string Name,
    string SourceSha256,
    long SourceBytes,
    string PackagePath,
    string PackageSha256,
    long PackageBytes,
    int SemanticMaterialCount,
    int SemanticSubMeshCount,
    int PrimitiveProfileCount,
    int RuntimeMaterialCount,
    int RuntimeSubMeshCount,
    int RuntimeUnlitMaterialCount,
    int RuntimeUnlitRenderObjectCount,
    int RenderObjectCount,
    float UniformScale,
    Vector3 Translation,
    bool LoadedThroughShippingContentManager,
    IReadOnlyList<SampleKhronosRuntimeMaterialEvidence> RuntimeMaterials,
    IReadOnlyList<SampleKhronosUnlitRenderObjectEvidence> UnlitRenderObjects);

public sealed record SampleKhronosMaterialGiRenderedSceneBuild(
    DateTimeOffset StartedAtUtc,
    SampleKhronosMaterialGiRenderedGateOptions Options,
    KhronosMaterialGiAuthenticatedGate AuthenticatedGate,
    string PackageSha256,
    IReadOnlyList<SampleKhronosMaterialGiRenderedAssetEvidence> Assets,
    int RuntimeMaterialCount,
    int RuntimeSubMeshCount,
    int RuntimeUnlitMaterialCount,
    int RuntimeUnlitRenderObjectCount,
    int RenderObjectCount);

public static class SampleKhronosMaterialGiRenderedSceneBuilder
{
    private const long MaximumModelPackageBytes =
        CookedPackage.MaximumModelPackageSnapshotBytes;

    public static SampleKhronosMaterialGiRenderedSceneBuild Build(
        SampleKhronosMaterialGiRenderedGateOptions options,
        Scene scene,
        ContentManager content,
        MaterialManager materialManager)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(materialManager);

        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        if (!CookedRuntimePolicy.Strict)
        {
            throw new InvalidOperationException(
                $"{CookedRuntimePolicy.StrictVariable}=true is required by the Khronos rendered gate.");
        }
        if (CookedRuntimePolicy.AllowSourceFallback)
        {
            throw new InvalidOperationException(
                $"{CookedRuntimePolicy.AllowSourceFallbackVariable} must be disabled for the Khronos rendered gate.");
        }
        if (!Directory.Exists(options.CookedRoot))
            throw new DirectoryNotFoundException($"Cooked root '{options.CookedRoot}' was not found.");

        KhronosMaterialGiAuthenticatedGate authenticated =
            KhronosMaterialGiConformance.AuthenticatePassedGate(
                options.ManifestPath,
                options.GateReportPath);
        int diagnosticsStart = content.CookedDiagnostics.Entries.Count;
        var preflight = new List<PreflightAsset>(authenticated.Manifest.Assets.Count);
        foreach (KhronosMaterialGiAsset asset in authenticated.Manifest.Assets)
        {
            KhronosMaterialGiGateEntry gateEntry = authenticated.GateReport.Entries.Single(
                entry => string.Equals(entry.Name, asset.Name, StringComparison.Ordinal));
            string packagePath = ResolveContainedPackagePath(options.CookedRoot, asset.Name);
            CookedModelPackageSnapshot snapshot = CookedPackage.CaptureModelSnapshot(
                packagePath,
                MaximumModelPackageBytes);
            if (snapshot.ByteLength <= 0)
            {
                throw new InvalidDataException(
                    $"Cooked Khronos model '{asset.Name}' is {snapshot.ByteLength} bytes; " +
                    $"expected a size in (0, {MaximumModelPackageBytes}].");
            }

            CookedModelSnapshotLoadResult loaded = content.LoadCookedModelSnapshot(
                snapshot,
                cooked =>
                {
                    IReadOnlyList<string> cookedErrors =
                        KhronosMaterialGiConformance.ValidateCooked(asset, cooked);
                    if (cookedErrors.Count != 0)
                    {
                        throw new InvalidDataException(
                            $"Cooked Khronos model '{asset.Name}' failed semantic revalidation: " +
                            string.Join(" ", cookedErrors));
                    }
                    if (cooked.Materials.Materials.Count != gateEntry.MaterialCount ||
                        cooked.Mesh.SubMeshes.Count != gateEntry.SubMeshCount ||
                        cooked.Materials.PrimitiveTransportProfiles.Count !=
                        gateEntry.PrimitiveProfileCount)
                    {
                        throw new InvalidDataException(
                            $"Cooked Khronos model '{asset.Name}' no longer matches the authenticated gate counts.");
                    }
                });

            preflight.Add(new PreflightAsset(
                asset,
                gateEntry,
                loaded));
        }

        IReadOnlyList<SampleKhronosMaterialGiLayoutPlacement> placements =
            SampleKhronosMaterialGiLayout.Create(
                preflight.Select(static asset => new SampleKhronosMaterialGiLayoutItem(
                    asset.Asset.Name,
                    asset.Bounds,
                    asset.GateEntry.SubMeshCount)).ToArray(),
                authenticated.Manifest.Commit);

        var evidence = new List<SampleKhronosMaterialGiRenderedAssetEvidence>(preflight.Count);
        var allMaterials = new HashSet<MaterialHandle>();
        var allUnlitMaterials = new HashSet<MaterialHandle>();
        int totalUnlitRenderObjects = 0;
        int totalRenderObjects = 0;

        for (int assetIndex = 0; assetIndex < preflight.Count; assetIndex++)
        {
            PreflightAsset source = preflight[assetIndex];
            SampleKhronosMaterialGiLayoutPlacement placement = placements[assetIndex];
            Model instance = source.RuntimeModel.CreateInstance();
            if (instance.RenderObjects.Count != source.GateEntry.SubMeshCount)
            {
                throw new InvalidDataException(
                    $"Runtime Khronos model '{source.Asset.Name}' contains {instance.RenderObjects.Count} " +
                    $"render objects; the authenticated gate requires {source.GateEntry.SubMeshCount}.");
            }

            var runtimeMaterials = new Dictionary<MaterialHandle, MaterialDefinition>();
            var unlitObjects = new List<SampleKhronosUnlitRenderObjectEvidence>();
            Matrix4x4 world =
                Matrix4x4.CreateScale(new Vector3(placement.UniformScale)) *
                Matrix4x4.CreateTranslation(placement.Translation);
            for (int renderObjectIndex = 0;
                 renderObjectIndex < instance.RenderObjects.Count;
                 renderObjectIndex++)
            {
                RenderObject renderObject = instance.RenderObjects[renderObjectIndex];
                if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                {
                    throw new InvalidDataException(
                        $"Runtime Khronos model '{source.Asset.Name}' object {renderObjectIndex} has no valid mesh.");
                }
                if (renderObject.Material is not MaterialHandle materialHandle ||
                    !materialHandle.IsValid)
                {
                    throw new InvalidDataException(
                        $"Runtime Khronos model '{source.Asset.Name}' object {renderObjectIndex} has no valid material.");
                }

                MaterialDefinition definition = materialManager.GetMaterialDefinition(materialHandle);
                runtimeMaterials.TryAdd(materialHandle, definition);
                allMaterials.Add(materialHandle);
                if (definition.ShadingModel == MaterialShadingModel.Unlit)
                {
                    allUnlitMaterials.Add(materialHandle);
                    unlitObjects.Add(new SampleKhronosUnlitRenderObjectEvidence(
                        SampleKhronosMaterialGiLayout.CreateStableId(
                            authenticated.Manifest.Commit,
                            source.Asset.Name,
                            renderObjectIndex),
                        renderObject.Name,
                        materialHandle.Index,
                        definition.Name));
                }

                renderObject.Id = SampleKhronosMaterialGiLayout.CreateStableId(
                    authenticated.Manifest.Commit,
                    source.Asset.Name,
                    renderObjectIndex);
                renderObject.Name =
                    $"Khronos.{source.Asset.Name}.{renderObjectIndex:D3}.{renderObject.Name}";
                renderObject.AssetReference = new SceneAssetReference
                {
                    Path = source.PackagePath,
                    SubObject = renderObjectIndex.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                };
                renderObject.WorldMatrix = world;
                renderObject.Visible = true;
                renderObject.Enabled = true;
                renderObject.IsStatic = renderObject is not SkinnedRenderObject;
                scene.Add(renderObject);
            }

            int runtimeUnlitMaterialCount = runtimeMaterials.Count(
                static entry => entry.Value.ShadingModel == MaterialShadingModel.Unlit);
            if (source.Asset.Expectations.MinimumUnlitCount > 0 &&
                (runtimeUnlitMaterialCount < source.Asset.Expectations.MinimumUnlitCount ||
                 unlitObjects.Count < source.Asset.Expectations.MinimumUnlitCount))
            {
                throw new InvalidDataException(
                    $"Runtime Khronos model '{source.Asset.Name}' retained " +
                    $"{runtimeUnlitMaterialCount} unlit materials and {unlitObjects.Count} unlit render objects; " +
                    $"at least {source.Asset.Expectations.MinimumUnlitCount} of each are required.");
            }

            SampleKhronosRuntimeMaterialEvidence[] materialEvidence = runtimeMaterials
                .OrderBy(static entry => entry.Key.Index)
                .ThenBy(static entry => entry.Key.Generation)
                .Select(static entry => new SampleKhronosRuntimeMaterialEvidence(
                    entry.Key.Index,
                    entry.Key.Generation,
                    entry.Value.Name,
                    entry.Value.ShadingModel))
                .ToArray();
            evidence.Add(new SampleKhronosMaterialGiRenderedAssetEvidence(
                source.Asset.Name,
                source.Asset.Sha256,
                source.Asset.Bytes,
                source.PackagePath,
                source.PackageSha256,
                source.PackageBytes,
                source.GateEntry.MaterialCount,
                source.GateEntry.SubMeshCount,
                source.GateEntry.PrimitiveProfileCount,
                runtimeMaterials.Count,
                instance.RenderObjects.Count,
                runtimeUnlitMaterialCount,
                unlitObjects.Count,
                instance.RenderObjects.Count,
                placement.UniformScale,
                placement.Translation,
                true,
                materialEvidence,
                unlitObjects));
            totalUnlitRenderObjects += unlitObjects.Count;
            totalRenderObjects += instance.RenderObjects.Count;
        }

        CookedContentDiagnosticEntry[] loadEntries = content.CookedDiagnostics.Entries
            .Skip(diagnosticsStart)
            .ToArray();
        if (loadEntries.Length != preflight.Count ||
            loadEntries.Any(static entry => !entry.UsedCooked))
        {
            throw new InvalidDataException(
                "Every Khronos model must produce exactly one cooked ContentManager load diagnostic.");
        }
        foreach (PreflightAsset asset in preflight)
        {
            int matchingLoads = loadEntries.Count(entry =>
                string.Equals(
                    Path.GetFullPath(entry.RequestedPath),
                    asset.PackagePath,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.GetFullPath(entry.PackagePath ?? string.Empty),
                    asset.PackagePath,
                    StringComparison.OrdinalIgnoreCase));
            if (matchingLoads != 1)
            {
                throw new InvalidDataException(
                    $"Cooked ContentManager provenance for '{asset.Asset.Name}' was missing or ambiguous.");
            }
        }

        scene.Name = "Official Khronos Material/GI Rendered Conformance";
        scene.AmbientLight = new Color(0.035f, 0.035f, 0.035f, 1f);
        return new SampleKhronosMaterialGiRenderedSceneBuild(
            startedAtUtc,
            options,
            authenticated,
            ComputePackageSetSha256(preflight),
            evidence,
            allMaterials.Count,
            totalRenderObjects,
            allUnlitMaterials.Count,
            totalUnlitRenderObjects,
            totalRenderObjects);
    }

    public static string ComputePackageSetSha256(
        IEnumerable<(string Name, string Sha256, long Bytes)> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        byte[] size = new byte[sizeof(long)];
        foreach ((string name, string sha256, long bytes) in packages)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
            if (sha256.Length != 64 || sha256.Any(static character => !Uri.IsHexDigit(character)))
                throw new ArgumentException($"Package '{name}' has a malformed SHA-256 digest.");
            if (bytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(packages), "Package sizes must be positive.");

            AppendFramed(hash, Encoding.UTF8.GetBytes(name));
            AppendFramed(hash, Convert.FromHexString(sha256));
            BinaryPrimitives.WriteInt64LittleEndian(size, bytes);
            AppendFramed(hash, size);
            count++;
        }
        if (count == 0)
            throw new ArgumentException("At least one package is required.", nameof(packages));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputePackageSetSha256(IReadOnlyList<PreflightAsset> packages) =>
        ComputePackageSetSha256(packages.Select(static package =>
            (package.Asset.Name, package.PackageSha256, package.PackageBytes)));

    private static void AppendFramed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static string ResolveContainedPackagePath(string cookedRoot, string assetName)
    {
        string modelsRoot = Path.GetFullPath(Path.Combine(cookedRoot, "models"));
        string packagePath = Path.GetFullPath(Path.Combine(modelsRoot, assetName + ".njmodel"));
        string prefix = modelsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!packagePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cooked package path for '{assetName}' escaped the models root.");
        return packagePath;
    }

    private sealed record PreflightAsset(
        KhronosMaterialGiAsset Asset,
        KhronosMaterialGiGateEntry GateEntry,
        CookedModelSnapshotLoadResult Loaded)
    {
        public string PackagePath => Loaded.Snapshot.PackagePath;
        public string PackageSha256 => Loaded.Snapshot.Sha256;
        public long PackageBytes => Loaded.Snapshot.ByteLength;
        public BoundingBox Bounds => Loaded.CookedAsset.Manifest.BoundingBox;
        public Model RuntimeModel => Loaded.RuntimeModel;
    }
}

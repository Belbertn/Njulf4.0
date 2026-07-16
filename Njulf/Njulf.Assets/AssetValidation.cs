using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Njulf.Assets;

public enum AssetValidationStatus
{
    Accepted,
    AcceptedWithWarnings,
    RejectedUnsupported,
    RejectedInvalid,
    RejectedCrashed,
    RejectedTimeout,
    RejectedTooLarge
}

public enum AssetValidationProcessKind
{
    InProcess,
    ChildProcess
}

public enum AssetValidationChildProcessMode
{
    AssimpOnly,
    Always,
    Never
}

public enum AssetValidationPolicy
{
    Strict,
    GameDefault,
    Permissive
}

public enum AssetImportClassification
{
    Opaque,
    Masked,
    Blended,
    Billboard,
    FoliageCandidate,
    HighTextureMemory,
    UnsupportedRequiredFeature
}

public sealed record AssetValidationReport(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string RootPath,
    IReadOnlyList<AssetValidationEntry> Entries,
    AssetValidationSummary Summary);

public sealed record AssetValidationSummary(
    int TotalCount,
    int AcceptedCount,
    int AcceptedWithWarningsCount,
    int RejectedUnsupportedCount,
    int RejectedInvalidCount,
    int RejectedCrashedCount,
    int RejectedTimeoutCount,
    int RejectedTooLargeCount)
{
    public int RejectedCount =>
        RejectedUnsupportedCount +
        RejectedInvalidCount +
        RejectedCrashedCount +
        RejectedTimeoutCount +
        RejectedTooLargeCount;
}

public sealed record AssetValidationEntry(
    string AssetPath,
    string RelativePath,
    AssetValidationStatus Status,
    ModelImportBackend Backend,
    string BackendName,
    string BackendVersion,
    AssetValidationProcessKind ProcessKind,
    long ElapsedMilliseconds,
    long FileSizeBytes,
    AssetValidationMetrics Metrics,
    IReadOnlyList<AssetImportMessage> Diagnostics,
    string? FailureType,
    string? FailureMessage,
    int? ProcessExitCode,
    bool TimedOut,
    bool Crashed)
{
    public IReadOnlyList<AssetImportClassification> Classifications { get; init; } = Array.Empty<AssetImportClassification>();
    public IReadOnlyList<string> Decisions { get; init; } = Array.Empty<string>();
}

public sealed record AssetValidationMetrics(
    int MeshCount,
    int SubMeshCount,
    int VertexCount,
    int IndexCount,
    int TriangleCount,
    int MaterialCount,
    int LoadedTextureCount,
    ulong EstimatedTextureBytes,
    int AnimationClipCount,
    int SkinCount,
    int SkeletonCount,
    int UsedExtensionCount,
    int RequiredExtensionCount,
    IReadOnlyList<string> UsedExtensions,
    IReadOnlyList<string> RequiredExtensions)
{
    public static AssetValidationMetrics Empty { get; } = new(
        MeshCount: 0,
        SubMeshCount: 0,
        VertexCount: 0,
        IndexCount: 0,
        TriangleCount: 0,
        MaterialCount: 0,
        LoadedTextureCount: 0,
        EstimatedTextureBytes: 0,
        AnimationClipCount: 0,
        SkinCount: 0,
        SkeletonCount: 0,
        UsedExtensionCount: 0,
        RequiredExtensionCount: 0,
        UsedExtensions: Array.Empty<string>(),
        RequiredExtensions: Array.Empty<string>());
}

public sealed record AssetTextureBudget(
    string Source,
    uint Width,
    uint Height,
    uint MipLevels,
    ulong EstimatedBytes,
    bool WasDownscaled,
    bool IsCompressed);

public sealed class AssetValidationOptions
{
    public ImporterOptions ImporterOptions { get; init; } = ImporterOptions.Default;
    public AssetValidationPolicy Policy { get; init; } = AssetValidationPolicy.GameDefault;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxAssetBytes { get; init; } = 1L * 1024L * 1024L * 1024L;
    public ulong HighTextureMemoryBytes { get; init; } = 256UL * 1024UL * 1024UL;
    public AssetValidationChildProcessMode ChildProcessMode { get; init; } = AssetValidationChildProcessMode.AssimpOnly;
    public string? ChildProcessExecutablePath { get; init; }
    public IReadOnlyList<string> ChildProcessArgumentTemplate { get; init; } =
    [
        "--child-import",
        "{path}",
        "--backend",
        "{backend}",
        "--json",
        "-"
    ];
    public Func<ModelTextureSource, AssetTextureBudget?>? TextureBudgetInspector { get; init; }
}

public sealed class AssetValidator
{
    private static readonly HashSet<string> SupportedModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf",
        ".glb",
        ".obj",
        ".fbx"
    };

    public async Task<AssetValidationReport> ValidateAsync(
        string path,
        AssetValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Validation path cannot be empty.", nameof(path));

        options ??= new AssetValidationOptions();
        string fullPath = Path.GetFullPath(path);
        string rootPath = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;

        IReadOnlyList<string> assets = DiscoverAssets(fullPath);
        var entries = new List<AssetValidationEntry>(assets.Count);
        foreach (string asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(await ValidateAssetAsync(asset, rootPath, options, cancellationToken).ConfigureAwait(false));
        }

        return new AssetValidationReport(
            SchemaVersion: 1,
            GeneratedUtc: DateTimeOffset.UtcNow,
            RootPath: rootPath,
            Entries: entries,
            Summary: CreateSummary(entries));
    }

    public async Task<AssetValidationEntry> ValidateAssetAsync(
        string path,
        string? rootPath = null,
        AssetValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Validation path cannot be empty.", nameof(path));

        options ??= new AssetValidationOptions();
        string fullPath = Path.GetFullPath(path);
        rootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.GetDirectoryName(fullPath)
            : Path.GetFullPath(rootPath);

        var stopwatch = Stopwatch.StartNew();
        ModelImportBackend backend = ResolveValidationBackend(fullPath, options.ImporterOptions);
        long fileSize = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0L;

        if (!File.Exists(fullPath))
        {
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.InProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                AssetValidationStatus.RejectedInvalid,
                nameof(FileNotFoundException),
                $"Model file was not found: {fullPath}",
                processExitCode: null,
                timedOut: false,
                crashed: false);
        }

        if (fileSize > options.MaxAssetBytes)
        {
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.InProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                AssetValidationStatus.RejectedTooLarge,
                "AssetTooLarge",
                $"Asset '{fullPath}' is {fileSize.ToString(CultureInfo.InvariantCulture)} bytes, exceeding the configured limit of {options.MaxAssetBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
                processExitCode: null,
                timedOut: false,
                crashed: false);
        }

        if (ShouldUseChildProcess(backend, options))
        {
            return await ValidateWithChildProcessAsync(
                fullPath,
                rootPath,
                backend,
                fileSize,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        return ValidateInProcess(fullPath, rootPath, backend, fileSize, options);
    }

    public AssetValidationEntry ValidateInProcess(
        string path,
        string? rootPath,
        ModelImportBackend backend,
        long fileSize,
        AssetValidationOptions? options = null)
    {
        options ??= new AssetValidationOptions();
        string fullPath = Path.GetFullPath(path);
        var stopwatch = Stopwatch.StartNew();
        var importOptions = CloneImporterOptions(options.ImporterOptions);
        importOptions.Backend = backend;

        try
        {
            using var importer = new ModelImporter();
            ModelImportResult result = importer.ImportDetailed(fullPath, importOptions);
            stopwatch.Stop();
            return CreateEntryFromImportResult(
                result,
                fullPath,
                rootPath,
                AssetValidationProcessKind.InProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                processExitCode: null,
                timedOut: false,
                crashed: false,
                options);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.InProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                AssetValidationStatus.RejectedInvalid,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message,
                processExitCode: null,
                timedOut: false,
                crashed: false);
        }
    }

    private static async Task<AssetValidationEntry> ValidateWithChildProcessAsync(
        string fullPath,
        string? rootPath,
        ModelImportBackend backend,
        long fileSize,
        AssetValidationOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.ChildProcessExecutablePath))
        {
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.ChildProcess,
                elapsedMilliseconds: 0,
                fileSize,
                AssetValidationStatus.RejectedInvalid,
                "ChildProcessNotConfigured",
                "Child-process validation was requested, but no child process executable path was configured.",
                processExitCode: null,
                timedOut: false,
                crashed: false);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ChildProcessExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (string argument in options.ChildProcessArgumentTemplate)
            process.StartInfo.ArgumentList.Add(ExpandChildArgument(argument, fullPath, backend));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            AssetValidationEntry? parsed = TryReadEntry(stdout);
            if (parsed != null)
            {
                return parsed with
                {
                    AssetPath = fullPath,
                    RelativePath = CreateRelativePath(rootPath, fullPath),
                    ProcessKind = AssetValidationProcessKind.ChildProcess,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    FileSizeBytes = fileSize,
                    ProcessExitCode = process.ExitCode,
                    Crashed = parsed.Crashed || (process.ExitCode != 0 && parsed.Status == AssetValidationStatus.Accepted)
                };
            }

            bool crashed = process.ExitCode != 0;
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.ChildProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                crashed ? AssetValidationStatus.RejectedCrashed : AssetValidationStatus.RejectedInvalid,
                crashed ? "ChildProcessNonZeroExit" : "ChildProcessInvalidOutput",
                string.IsNullOrWhiteSpace(stderr)
                    ? $"Child process exited with code {process.ExitCode} and did not produce a validation entry."
                    : stderr.Trim(),
                process.ExitCode,
                timedOut: false,
                crashed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            TryKill(process);
            return CreateFailureEntry(
                fullPath,
                rootPath,
                backend,
                AssetValidationProcessKind.ChildProcess,
                stopwatch.ElapsedMilliseconds,
                fileSize,
                AssetValidationStatus.RejectedTimeout,
                "ChildProcessTimeout",
                $"Child process validation exceeded {options.Timeout.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms.",
                process.HasExited ? process.ExitCode : null,
                timedOut: true,
                crashed: true);
        }
    }

    private static AssetValidationEntry CreateEntryFromImportResult(
        ModelImportResult result,
        string fullPath,
        string? rootPath,
        AssetValidationProcessKind processKind,
        long elapsedMilliseconds,
        long fileSize,
        int? processExitCode,
        bool timedOut,
        bool crashed,
        AssetValidationOptions options)
    {
        AssetValidationMetrics metrics = CreateMetrics(result, options.TextureBudgetInspector);
        ClassificationResult classification = Classify(result, fullPath, metrics, options);
        IReadOnlyList<AssetImportMessage> diagnostics = result.Diagnostics.Messages
            .Concat(classification.Diagnostics)
            .ToArray();

        AssetValidationStatus status = result.Status switch
        {
            ModelImportStatus.Imported when options.Policy == AssetValidationPolicy.Strict && diagnostics.Any(IsPolicyFailureWarning) => AssetValidationStatus.RejectedInvalid,
            ModelImportStatus.Imported when diagnostics.Any(message => message.Severity == AssetImportSeverity.Warning) => AssetValidationStatus.AcceptedWithWarnings,
            ModelImportStatus.Imported => AssetValidationStatus.Accepted,
            ModelImportStatus.Unsupported when IsUnsupportedFeatureResult(result) => AssetValidationStatus.RejectedUnsupported,
            _ => AssetValidationStatus.RejectedInvalid
        };

        return new AssetValidationEntry(
            AssetPath: fullPath,
            RelativePath: CreateRelativePath(rootPath, fullPath),
            Status: status,
            Backend: result.Backend,
            BackendName: result.BackendName,
            BackendVersion: result.BackendVersion,
            ProcessKind: processKind,
            ElapsedMilliseconds: elapsedMilliseconds,
            FileSizeBytes: fileSize,
            Metrics: metrics,
            Diagnostics: diagnostics,
            FailureType: result.FailureType,
            FailureMessage: result.FailureMessage,
            ProcessExitCode: processExitCode,
            TimedOut: timedOut,
            Crashed: crashed)
        {
            Classifications = classification.Classifications,
            Decisions = classification.Decisions
        };
    }

    private static AssetValidationEntry CreateFailureEntry(
        string fullPath,
        string? rootPath,
        ModelImportBackend backend,
        AssetValidationProcessKind processKind,
        long elapsedMilliseconds,
        long fileSize,
        AssetValidationStatus status,
        string failureType,
        string failureMessage,
        int? processExitCode,
        bool timedOut,
        bool crashed)
    {
        AssetImportMessageCode? code = status switch
        {
            AssetValidationStatus.RejectedCrashed => AssetImportMessageCode.NativeImporterCrash,
            AssetValidationStatus.RejectedTimeout => AssetImportMessageCode.ChildProcessTimeout,
            _ => null
        };
        IReadOnlyList<AssetImportMessage> diagnostics = code.HasValue
            ? [new AssetImportMessage(AssetImportSeverity.Error, code.Value, fullPath, null, failureMessage)]
            : Array.Empty<AssetImportMessage>();

        return new AssetValidationEntry(
            AssetPath: fullPath,
            RelativePath: CreateRelativePath(rootPath, fullPath),
            Status: status,
            Backend: backend,
            BackendName: backend.ToString(),
            BackendVersion: string.Empty,
            ProcessKind: processKind,
            ElapsedMilliseconds: elapsedMilliseconds,
            FileSizeBytes: fileSize,
            Metrics: AssetValidationMetrics.Empty,
            Diagnostics: diagnostics,
            FailureType: failureType,
            FailureMessage: failureMessage,
            ProcessExitCode: processExitCode,
            TimedOut: timedOut,
            Crashed: crashed)
        {
            Decisions = [failureMessage]
        };
    }

    private sealed record ClassificationResult(
        IReadOnlyList<AssetImportClassification> Classifications,
        IReadOnlyList<AssetImportMessage> Diagnostics,
        IReadOnlyList<string> Decisions);

    private static ClassificationResult Classify(
        ModelImportResult result,
        string fullPath,
        AssetValidationMetrics metrics,
        AssetValidationOptions options)
    {
        var classifications = new HashSet<AssetImportClassification>();
        var diagnostics = new List<AssetImportMessage>();
        var decisions = new List<string>();

        if (IsUnsupportedFeatureResult(result))
        {
            classifications.Add(AssetImportClassification.UnsupportedRequiredFeature);
            decisions.Add("Rejected because the asset requires an importer or renderer feature that is not supported.");
        }

        ModelMesh? mesh = result.Mesh;
        if (mesh == null)
            return new ClassificationResult(classifications.Order().ToArray(), diagnostics, decisions);

        bool anyBlend = mesh.Materials.Any(material => material.AlphaMode == ModelAlphaMode.Blend);
        bool anyMask = mesh.Materials.Any(material => material.AlphaMode == ModelAlphaMode.Mask);
        if (anyBlend)
            classifications.Add(AssetImportClassification.Blended);
        if (anyMask)
            classifications.Add(AssetImportClassification.Masked);
        if (!anyBlend && !anyMask)
            classifications.Add(AssetImportClassification.Opaque);

        bool foliageCandidate = IsFoliageCandidate(fullPath, mesh);
        if (foliageCandidate)
        {
            classifications.Add(AssetImportClassification.FoliageCandidate);
            decisions.Add("Classified as a foliage candidate based on asset, mesh, material, or texture naming.");
        }

        bool billboard = ContainsContentToken(fullPath, "billboard") ||
            mesh.SubMeshes.Any(subMesh => ContainsContentToken(subMesh.Name, "billboard")) ||
            mesh.Materials.Any(material => ContainsContentToken(material.Name, "billboard") ||
                EnumerateSlots(material).Any(slot => ContainsContentToken(slot?.Source?.DebugName, "billboard") ||
                    ContainsContentToken(slot?.Source?.FilePath, "billboard")));
        if (billboard)
            classifications.Add(AssetImportClassification.Billboard);

        if (metrics.EstimatedTextureBytes >= options.HighTextureMemoryBytes && metrics.EstimatedTextureBytes > 0)
        {
            classifications.Add(AssetImportClassification.HighTextureMemory);
            diagnostics.Add(new AssetImportMessage(
                AssetImportSeverity.Warning,
                AssetImportMessageCode.ExcessiveTextureMemory,
                fullPath,
                "/textures",
                $"Estimated texture memory is {metrics.EstimatedTextureBytes.ToString(CultureInfo.InvariantCulture)} bytes, exceeding the configured threshold of {options.HighTextureMemoryBytes.ToString(CultureInfo.InvariantCulture)} bytes."));
            decisions.Add("Texture memory exceeds the configured validation threshold.");
        }

        for (int i = 0; i < mesh.Materials.Count; i++)
        {
            ModelMaterial material = mesh.Materials[i];
            string source = $"/materials/{i}";
            if (foliageCandidate && material.AlphaMode == ModelAlphaMode.Blend)
            {
                diagnostics.Add(new AssetImportMessage(
                    AssetImportSeverity.Warning,
                    AssetImportMessageCode.FoliageBlendAlphaWarning,
                    fullPath,
                    source,
                    $"Foliage material '{material.Name}' uses BLEND alpha. Dense foliage should prefer MASK to reduce sorting and overdraw cost."));
                decisions.Add("Not selected by default: foliage candidate uses BLEND alpha.");
            }

            if (foliageCandidate && material.AlphaMode == ModelAlphaMode.Mask && material.AlphaCutoff <= 0f)
            {
                diagnostics.Add(new AssetImportMessage(
                    AssetImportSeverity.Warning,
                    AssetImportMessageCode.FoliageAlphaCutoffWarning,
                    fullPath,
                    source,
                    $"Foliage material '{material.Name}' is MASK but has no positive alpha cutoff."));
            }

            if (foliageCandidate && !material.DoubleSided)
            {
                diagnostics.Add(new AssetImportMessage(
                    AssetImportSeverity.Warning,
                    AssetImportMessageCode.FoliageDoubleSidedWarning,
                    fullPath,
                    source,
                    $"Foliage material '{material.Name}' is not double-sided; leaf and grass cards usually need double-sided rendering."));
            }

            if (foliageCandidate && (material.TransmissionFactor > 0f || material.ThicknessFactor > 0f || material.SubsurfaceStrength > 0f))
            {
                diagnostics.Add(new AssetImportMessage(
                    AssetImportSeverity.Warning,
                    AssetImportMessageCode.UnsupportedTranslucencyWarning,
                    fullPath,
                    source,
                    $"Foliage material '{material.Name}' uses translucency, volume, or subsurface properties that are not part of the default foliage path."));
            }
        }

        if (foliageCandidate && anyMask && !anyBlend)
            decisions.Add("Preferred foliage candidate: uses MASK alpha without BLEND materials.");
        else if (!foliageCandidate)
            decisions.Add("No foliage-specific selection rules applied.");

        return new ClassificationResult(
            classifications.Order().ToArray(),
            diagnostics,
            decisions.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsPolicyFailureWarning(AssetImportMessage message)
    {
        if (message.Severity != AssetImportSeverity.Warning)
            return false;

        return message.Code is
            AssetImportMessageCode.TextureFallbackUsed or
            AssetImportMessageCode.UnsupportedOptionalExtension or
            AssetImportMessageCode.AlphaModePerformanceWarning or
            AssetImportMessageCode.ExcessiveTextureMemory or
            AssetImportMessageCode.FoliageBlendAlphaWarning or
            AssetImportMessageCode.FoliageAlphaCutoffWarning or
            AssetImportMessageCode.FoliageDoubleSidedWarning or
            AssetImportMessageCode.UnsupportedTranslucencyWarning;
    }

    private static bool IsUnsupportedFeatureResult(ModelImportResult result)
    {
        return result.Diagnostics.UnsupportedRequiredExtensionCount > 0 ||
            result.Diagnostics.UnsupportedCompressedTextureCount > 0 ||
            result.Diagnostics.UnsupportedMorphTargetMeshCount > 0 ||
            result.Diagnostics.Messages.Any(message =>
                message.Code is
                    AssetImportMessageCode.UnsupportedRequiredExtension or
                    AssetImportMessageCode.CompressedTextureUnsupported or
                    AssetImportMessageCode.UnsupportedCompressedMesh or
                    AssetImportMessageCode.UnsupportedPrimitiveMode or
                    AssetImportMessageCode.MorphTargetsUnsupported or
                    AssetImportMessageCode.UnsupportedAssetFormat);
    }

    private static bool IsFoliageCandidate(string fullPath, ModelMesh mesh)
    {
        return ContainsFoliageToken(fullPath) ||
            ContainsFoliageToken(mesh.Name) ||
            mesh.SubMeshes.Any(subMesh => ContainsFoliageToken(subMesh.Name)) ||
            mesh.Materials.Any(material =>
                ContainsFoliageToken(material.Name) ||
                EnumerateSlots(material).Any(slot =>
                    ContainsFoliageToken(slot?.Source?.DebugName) ||
                    ContainsFoliageToken(slot?.Source?.FilePath)));
    }

    private static bool ContainsFoliageToken(string? text)
    {
        return ContainsContentToken(text, "foliage") ||
            ContainsContentToken(text, "grass") ||
            ContainsContentToken(text, "leaf") ||
            ContainsContentToken(text, "leaves") ||
            ContainsContentToken(text, "tree") ||
            ContainsContentToken(text, "ivy") ||
            ContainsContentToken(text, "billboard");
    }

    private static bool ContainsContentToken(string? text, string token)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static AssetValidationMetrics CreateMetrics(
        ModelImportResult result,
        Func<ModelTextureSource, AssetTextureBudget?>? textureBudgetInspector)
    {
        ModelMesh? mesh = result.Mesh;
        IReadOnlyList<string> usedExtensions = result.SharpGltfCapability?.Document?.ExtensionsUsed ?? Array.Empty<string>();
        IReadOnlyList<string> requiredExtensions = result.SharpGltfCapability?.Document?.ExtensionsRequired ?? Array.Empty<string>();
        if (mesh == null)
        {
            return AssetValidationMetrics.Empty with
            {
                UsedExtensionCount = usedExtensions.Count,
                RequiredExtensionCount = requiredExtensions.Count,
                UsedExtensions = usedExtensions,
                RequiredExtensions = requiredExtensions
            };
        }

        IReadOnlyList<ModelTextureSource> textureSources = GetTextureSources(mesh).ToArray();
        ulong estimatedTextureBytes = 0;
        if (textureBudgetInspector != null)
        {
            foreach (ModelTextureSource source in textureSources)
            {
                try
                {
                    AssetTextureBudget? budget = textureBudgetInspector(source);
                    if (budget != null)
                        estimatedTextureBytes += budget.EstimatedBytes;
                }
                catch
                {
                    // Import validation should still report the asset result if an optional budget probe fails.
                }
            }
        }

        return new AssetValidationMetrics(
            MeshCount: mesh.SubMeshes.Count > 0 ? 1 : 0,
            SubMeshCount: mesh.SubMeshes.Count,
            VertexCount: mesh.Vertices.Length,
            IndexCount: mesh.Indices.Length,
            TriangleCount: mesh.Indices.Length / 3,
            MaterialCount: mesh.Materials.Count,
            LoadedTextureCount: textureSources.Count,
            EstimatedTextureBytes: estimatedTextureBytes,
            AnimationClipCount: mesh.AnimationClips.Count,
            SkinCount: mesh.Skins.Count,
            SkeletonCount: mesh.Skeletons.Count,
            UsedExtensionCount: usedExtensions.Count,
            RequiredExtensionCount: requiredExtensions.Count,
            UsedExtensions: usedExtensions,
            RequiredExtensions: requiredExtensions);
    }

    private static IEnumerable<ModelTextureSource> GetTextureSources(ModelMesh mesh)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModelMaterial material in mesh.Materials)
        {
            foreach (ModelTextureSlot? slot in EnumerateSlots(material))
            {
                ModelTextureSource? source = slot?.Source;
                if (source == null)
                    continue;

                string key = !string.IsNullOrWhiteSpace(source.CacheIdentity)
                    ? source.CacheIdentity
                    : !string.IsNullOrWhiteSpace(source.FilePath)
                        ? Path.GetFullPath(source.FilePath)
                        : source.DebugName;
                if (seen.Add(key))
                    yield return source;
            }
        }
    }

    private static IEnumerable<ModelTextureSlot?> EnumerateSlots(ModelMaterial material)
    {
        yield return material.BaseColorTexture;
        yield return material.NormalTexture;
        yield return material.MetallicRoughnessTexture;
        yield return material.OcclusionTexture;
        yield return material.EmissiveTexture;
        yield return material.ClearcoatTexture;
        yield return material.ClearcoatRoughnessTexture;
        yield return material.ClearcoatNormalTexture;
        yield return material.SheenColorTexture;
        yield return material.SheenRoughnessTexture;
        yield return material.AnisotropyTexture;
        yield return material.TransmissionTexture;
        yield return material.ThicknessTexture;
        yield return material.SpecularTexture;
        yield return material.SpecularColorTexture;
        yield return material.IridescenceTexture;
        yield return material.IridescenceThicknessTexture;
        yield return material.SubsurfaceTexture;
    }

    private static bool ShouldUseChildProcess(ModelImportBackend backend, AssetValidationOptions options)
    {
        return options.ChildProcessMode switch
        {
            AssetValidationChildProcessMode.Always => true,
            AssetValidationChildProcessMode.Never => false,
            _ => backend == ModelImportBackend.Assimp
        };
    }

    private static ModelImportBackend ResolveValidationBackend(string fullPath, ImporterOptions options)
    {
        if (options.Backend != ModelImportBackend.Auto)
            return options.Backend;

        string extension = Path.GetExtension(fullPath).ToLowerInvariant();
        return extension is ".gltf" or ".glb"
            ? ModelImportBackend.SharpGltf
            : ModelImporter.ResolveBackend(fullPath, options);
    }

    private static IReadOnlyList<string> DiscoverAssets(string path)
    {
        if (File.Exists(path))
            return [path];
        if (!Directory.Exists(path))
            return [path];

        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutputPath(path, file))
            .Where(file => SupportedModelExtensions.Contains(Path.GetExtension(file)))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsBuildOutputPath(string rootPath, string filePath)
    {
        string relativePath = Path.GetRelativePath(rootPath, filePath);
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part =>
                string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static AssetValidationSummary CreateSummary(IReadOnlyList<AssetValidationEntry> entries)
    {
        return new AssetValidationSummary(
            TotalCount: entries.Count,
            AcceptedCount: entries.Count(entry => entry.Status == AssetValidationStatus.Accepted),
            AcceptedWithWarningsCount: entries.Count(entry => entry.Status == AssetValidationStatus.AcceptedWithWarnings),
            RejectedUnsupportedCount: entries.Count(entry => entry.Status == AssetValidationStatus.RejectedUnsupported),
            RejectedInvalidCount: entries.Count(entry => entry.Status == AssetValidationStatus.RejectedInvalid),
            RejectedCrashedCount: entries.Count(entry => entry.Status == AssetValidationStatus.RejectedCrashed),
            RejectedTimeoutCount: entries.Count(entry => entry.Status == AssetValidationStatus.RejectedTimeout),
            RejectedTooLargeCount: entries.Count(entry => entry.Status == AssetValidationStatus.RejectedTooLarge));
    }

    private static string ExpandChildArgument(string argument, string path, ModelImportBackend backend)
    {
        return argument
            .Replace("{path}", path, StringComparison.Ordinal)
            .Replace("{backend}", backend.ToString(), StringComparison.Ordinal);
    }

    private static AssetValidationEntry? TryReadEntry(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        string trimmed = stdout.Trim();
        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end < start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<AssetValidationEntry>(
                trimmed[start..(end + 1)],
                AssetValidationJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string CreateRelativePath(string? rootPath, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Path.GetFileName(fullPath);

        try
        {
            return Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }

    private static ImporterOptions CloneImporterOptions(ImporterOptions source)
    {
        return new ImporterOptions
        {
            FlipUVs = source.FlipUVs,
            GenerateNormals = source.GenerateNormals,
            GenerateTangents = source.GenerateTangents,
            Triangulate = source.Triangulate,
            JoinIdenticalVertices = source.JoinIdenticalVertices,
            SortByPrimitiveType = source.SortByPrimitiveType,
            CalculateBoundingBoxes = source.CalculateBoundingBoxes,
            GlobalScale = source.GlobalScale,
            FlipWindingOrder = source.FlipWindingOrder,
            PreferredFormat = source.PreferredFormat,
            Backend = source.Backend
        };
    }
}

public static class AssetValidationJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    static AssetValidationJson()
    {
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public static void WriteReport(string path, AssetValidationReport report)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, Options));
    }

    public static AssetValidationReport ReadReport(string path)
    {
        return JsonSerializer.Deserialize<AssetValidationReport>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"Asset validation report '{path}' could not be read.");
    }
}

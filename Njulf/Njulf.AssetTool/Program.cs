using System;
using System.Globalization;
using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Resources;

namespace Njulf.AssetTool;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "validate" => await RunValidate(args[1..], writeJson: false).ConfigureAwait(false),
                "import" => await RunValidate(args[1..], writeJson: false, singleAsset: true).ConfigureAwait(false),
                "report" => await RunValidate(args[1..], writeJson: true).ConfigureAwait(false),
                "cook" => RunCook(args[1..]),
                "clean-stale" => RunCleanStale(args[1..]),
                "migrate" => RunMigrate(args[1..]),
                "keygen" => RunKeygen(args[1..]),
                "khronos-material-gi" => await KhronosMaterialGiGateCommand.RunAsync(args[1..]).ConfigureAwait(false),
                "alpha-visibility-gate" => AlphaVisibilityGateCommand.Run(args[1..]),
                "material-gi-test-matrix" => MaterialGiTestMatrixCommand.Run(args[1..]),
                "material-gi-evidence" => MaterialGiEvidenceCommand.Run(args[1..]),
                "--child-import" => await RunChildImport(args[1..]).ConfigureAwait(false),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunValidate(string[] args, bool writeJson, bool singleAsset = false)
    {
        if (args.Length == 0)
            throw new ArgumentException("A path is required.");

        string path = args[0];
        string? jsonPath = null;
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        long maxBytes = 1L * 1024L * 1024L * 1024L;
        ulong highTextureBytes = 256UL * 1024UL * 1024UL;
        bool forceChild = false;
        ModelImportBackend backend = ModelImportBackend.Auto;
        AssetValidationPolicy policy = AssetValidationPolicy.GameDefault;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    jsonPath = RequireValue(args, ref i, "--json");
                    break;
                case "--timeout-ms":
                    timeout = TimeSpan.FromMilliseconds(int.Parse(RequireValue(args, ref i, "--timeout-ms"), CultureInfo.InvariantCulture));
                    break;
                case "--max-bytes":
                    maxBytes = long.Parse(RequireValue(args, ref i, "--max-bytes"), CultureInfo.InvariantCulture);
                    break;
                case "--high-texture-bytes":
                    highTextureBytes = ulong.Parse(RequireValue(args, ref i, "--high-texture-bytes"), CultureInfo.InvariantCulture);
                    break;
                case "--child-process-all":
                    forceChild = true;
                    break;
                case "--backend":
                    backend = Enum.Parse<ModelImportBackend>(RequireValue(args, ref i, "--backend"), ignoreCase: true);
                    break;
                case "--policy":
                    policy = Enum.Parse<AssetValidationPolicy>(RequireValue(args, ref i, "--policy"), ignoreCase: true);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'.");
            }
        }

        if (writeJson && string.IsNullOrWhiteSpace(jsonPath))
            throw new ArgumentException("The report command requires --json <output-path>.");

        var validator = new AssetValidator();
        AssetValidationOptions options = CreateOptions(timeout, maxBytes, highTextureBytes, forceChild, backend, policy);
        AssetValidationReport report = await validator.ValidateAsync(path, options).ConfigureAwait(false);
        PrintSummary(report, singleAsset);

        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            if (jsonPath == "-")
                Console.WriteLine(JsonSerializer.Serialize(report, AssetValidationJson.Options));
            else
                AssetValidationJson.WriteReport(jsonPath, report);
        }

        return report.Summary.RejectedCount == 0 ? 0 : 1;
    }

    private static async Task<int> RunChildImport(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("A child import path is required.");

        string path = args[0];
        ModelImportBackend backend = ModelImportBackend.Assimp;
        bool writeJson = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--backend":
                    backend = Enum.Parse<ModelImportBackend>(RequireValue(args, ref i, "--backend"), ignoreCase: true);
                    break;
                case "--json":
                    string target = RequireValue(args, ref i, "--json");
                    writeJson = target == "-";
                    if (!writeJson)
                        throw new ArgumentException("Child import supports only --json -.");
                    break;
                default:
                    throw new ArgumentException($"Unknown child option '{args[i]}'.");
            }
        }

        var validator = new AssetValidator();
        AssetValidationEntry entry = validator.ValidateInProcess(
            path,
            Path.GetDirectoryName(Path.GetFullPath(path)),
            backend,
            File.Exists(path) ? new FileInfo(path).Length : 0,
            new AssetValidationOptions
            {
                Timeout = TimeSpan.FromSeconds(30),
                MaxAssetBytes = 1L * 1024L * 1024L * 1024L,
                ChildProcessMode = AssetValidationChildProcessMode.Never,
                TextureBudgetInspector = CreateOptions(
                    TimeSpan.FromSeconds(30),
                    1L * 1024L * 1024L * 1024L,
                    256UL * 1024UL * 1024UL,
                    forceChild: false,
                    ModelImportBackend.Auto,
                    AssetValidationPolicy.GameDefault).TextureBudgetInspector
            });

        if (writeJson)
            Console.WriteLine(JsonSerializer.Serialize(entry, AssetValidationJson.Options));

        await Console.Out.FlushAsync().ConfigureAwait(false);
        return entry.Status is AssetValidationStatus.Accepted or AssetValidationStatus.AcceptedWithWarnings ? 0 : 2;
    }

    private static int RunCook(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Cook requires 'model <source>', 'folder <source-folder>', 'changed <source-folder>', or 'clean-stale --out <folder>'.");

        string mode = args[0].ToLowerInvariant();
        if (mode == "clean-stale")
            return RunCleanStale(args[1..]);

        string source = args[1];
        string? output = null;
        ModelImportBackend backend = ModelImportBackend.Auto;
        int maxDimension = 2048;
        bool force = false;
        string platform = CookedPlatform.Current;
        string? signingKey = null;
        TextureTargetFormatPolicy textureFormat = TextureTargetFormatPolicy.AutoBc;
        string? opacityMicromapBridgePath = null;
        string? opacityMicromapProvenancePath = null;
        uint opacityMicromapSubdivision = 4U;
        uint opacityMicromapMaximumSubdivision = 8U;
        ulong opacityMicromapMaximumWorkload = 1UL << 28;
        uint opacityMicromapMaximumArrayBytes = 256U * 1024U * 1024U;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    output = RequireValue(args, ref i, "--out");
                    break;
                case "--backend":
                    backend = Enum.Parse<ModelImportBackend>(RequireValue(args, ref i, "--backend"), ignoreCase: true);
                    break;
                case "--max-texture-dimension":
                    maxDimension = int.Parse(RequireValue(args, ref i, "--max-texture-dimension"), CultureInfo.InvariantCulture);
                    if (maxDimension <= 0)
                        throw new ArgumentOutOfRangeException(nameof(maxDimension), "Maximum texture dimension must be positive.");
                    break;
                case "--force":
                    force = true;
                    break;
                case "--platform":
                    platform = CookedPlatform.Normalize(RequireValue(args, ref i, "--platform"));
                    break;
                case "--signing-key":
                    signingKey = RequireValue(args, ref i, "--signing-key");
                    break;
                case "--texture-format":
                    textureFormat = Enum.Parse<TextureTargetFormatPolicy>(RequireValue(args, ref i, "--texture-format"), ignoreCase: true);
                    break;
                case "--omm-bridge":
                    opacityMicromapBridgePath = RequireValue(args, ref i, "--omm-bridge");
                    break;
                case "--omm-provenance":
                    opacityMicromapProvenancePath = RequireValue(args, ref i, "--omm-provenance");
                    break;
                case "--omm-subdivision":
                    opacityMicromapSubdivision = uint.Parse(
                        RequireValue(args, ref i, "--omm-subdivision"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--omm-max-subdivision":
                    opacityMicromapMaximumSubdivision = uint.Parse(
                        RequireValue(args, ref i, "--omm-max-subdivision"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--omm-max-workload":
                    opacityMicromapMaximumWorkload = ulong.Parse(
                        RequireValue(args, ref i, "--omm-max-workload"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--omm-max-array-bytes":
                    opacityMicromapMaximumArrayBytes = uint.Parse(
                        RequireValue(args, ref i, "--omm-max-array-bytes"),
                        CultureInfo.InvariantCulture);
                    break;
                case "--policy":
                case "--timeout-ms":
                    _ = RequireValue(args, ref i, args[i]); // Accepted for parity with validation/import automation.
                    break;
                default:
                    throw new ArgumentException($"Unknown cook option '{args[i]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("Cook requires --out <folder>.");

        if (string.IsNullOrWhiteSpace(opacityMicromapBridgePath) !=
            string.IsNullOrWhiteSpace(opacityMicromapProvenancePath))
        {
            throw new ArgumentException(
                "C1 cooking requires both --omm-bridge and --omm-provenance; neither option enables it alone.");
        }

        using PinnedOpacityMicromapBakeBridge? opacityMicromapBridge =
            string.IsNullOrWhiteSpace(opacityMicromapBridgePath)
                ? null
                : new PinnedOpacityMicromapBakeBridge(
                    OpacityMicromapBridgeProvenanceManifest.LoadOptions(
                        opacityMicromapProvenancePath!,
                        opacityMicromapBridgePath!));
        IOpacityMicromapModelPayloadProducer? opacityMicromapProducer =
            opacityMicromapBridge is null
                ? null
                : new NvidiaOpacityMicromapModelPayloadProducer(
                    opacityMicromapBridge,
                    new NvidiaOpacityMicromapCookPolicy
                    {
                        RequestedSubdivisionLevel = opacityMicromapSubdivision,
                        MaximumSubdivisionLevel = opacityMicromapMaximumSubdivision,
                        MaximumWorkloadSize = opacityMicromapMaximumWorkload,
                        MaximumArrayDataBytes = opacityMicromapMaximumArrayBytes
                    });

        var options = new ModelCookOptions
        {
            ImporterOptions = new ImporterOptions { Backend = backend },
            TextureOptions = new TextureCookOptions(MaxDimension: maxDimension, TargetFormatPolicy: textureFormat),
            Force = force,
            Platform = platform,
            SigningPrivateKey = signingKey,
            OpacityMicromapPayloadProducer = opacityMicromapProducer
        };
        using var cooker = new ModelAssetCooker();
        if (mode == "model")
        {
            AssetCookResult result = cooker.CookModel(source, output, options);
            PrintCookResult(result);
            return result.Report.Status == "Succeeded" ? 0 : 1;
        }
        if (mode is "folder" or "changed")
        {
            IReadOnlyList<AssetCookResult> results = cooker.CookFolder(source, output, options with { Force = mode == "folder" && force });
            foreach (AssetCookResult result in results)
                PrintCookResult(result);
            Console.WriteLine($"Cooked {results.Count(result => !result.Skipped)} asset(s); skipped {results.Count(result => result.Skipped)} unchanged asset(s).");
            return results.All(result => result.Report.Status == "Succeeded") ? 0 : 1;
        }
        throw new ArgumentException($"Unknown cook mode '{mode}'.");
    }

    private static int RunCleanStale(string[] args)
    {
        string? output = null;
        string platform = CookedPlatform.Current;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": output = RequireValue(args, ref i, "--out"); break;
                case "--platform": platform = CookedPlatform.Normalize(RequireValue(args, ref i, "--platform")); break;
                default: throw new ArgumentException($"Unknown clean-stale option '{args[i]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("clean-stale requires --out <folder>.");
        using var cooker = new ModelAssetCooker();
        int deleted = cooker.CleanStale(output, platform);
        Console.WriteLine($"Deleted {deleted} stale cooked output(s).");
        return 0;
    }

    private static void PrintCookResult(AssetCookResult result)
    {
        AssetCookReport report = result.Report;
        Console.WriteLine(
            $"{(result.Skipped ? "Unchanged" : report.Status)}: {report.SourcePath} " +
            $"meshes={report.SubMeshCount} vertices={report.VertexCount} indices={report.IndexCount} " +
            $"meshlets={report.MeshletCount}/{report.MeshletLod1Count}/{report.MeshletLod2Count} materials={report.MaterialCount} textures={report.TextureCount} " +
            $"import={report.ImportMilliseconds}ms cook={report.MeshMilliseconds + report.TextureMilliseconds + report.SerializationMilliseconds}ms");
        foreach (string warning in report.Warnings.Take(8))
            Console.WriteLine($"  warning: {warning}");
    }

    private static int RunMigrate(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("migrate requires a cooked input folder.");
        string source = args[0];
        string? output = null;
        string? signingKey = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": output = RequireValue(args, ref i, "--out"); break;
                case "--signing-key": signingKey = RequireValue(args, ref i, "--signing-key"); break;
                default: throw new ArgumentException($"Unknown migrate option '{args[i]}'.");
            }
        }
        output ??= source;
        CookedMigrationReport report = CookedAssetMigrator.MigrateTree(source, output, signingKey);
        Console.WriteLine($"Migrated {report.MigratedFiles} cooked binaries and copied {report.CopiedFiles} companion files to '{report.OutputRoot}'.");
        return 0;
    }

    private static int RunKeygen(string[] args)
    {
        string? privatePath = null;
        string? publicPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--private": privatePath = RequireValue(args, ref i, "--private"); break;
                case "--public": publicPath = RequireValue(args, ref i, "--public"); break;
                default: throw new ArgumentException($"Unknown keygen option '{args[i]}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(privatePath) || string.IsNullOrWhiteSpace(publicPath))
            throw new ArgumentException("keygen requires --private <pem> and --public <pem>.");
        CookedPackageSigner.GenerateKeyPair(privatePath, publicPath);
        Console.WriteLine($"Generated ECDSA P-256 cooked-asset signing keys: '{privatePath}', '{publicPath}'.");
        return 0;
    }

    private static AssetValidationOptions CreateOptions(
        TimeSpan timeout,
        long maxBytes,
        ulong highTextureBytes,
        bool forceChild,
        ModelImportBackend backend,
        AssetValidationPolicy policy)
    {
        return new AssetValidationOptions
        {
            ImporterOptions = new ImporterOptions { Backend = backend },
            Policy = policy,
            Timeout = timeout,
            MaxAssetBytes = maxBytes,
            HighTextureMemoryBytes = highTextureBytes,
            ChildProcessMode = forceChild ? AssetValidationChildProcessMode.Always : AssetValidationChildProcessMode.AssimpOnly,
            ChildProcessExecutablePath = Environment.ProcessPath,
            TextureBudgetInspector = source =>
            {
                var entry = TextureManager.InspectTextureSourceBudget(source);
                return new AssetTextureBudget(
                    entry.SourcePath,
                    entry.Width,
                    entry.Height,
                    entry.MipLevels,
                    entry.EstimatedBytes,
                    entry.WasDownscaled,
                    entry.IsCompressed);
            }
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        index++;
        return args[index];
    }

    private static void PrintSummary(AssetValidationReport report, bool singleAsset)
    {
        if (singleAsset && report.Entries.Count == 1)
        {
            AssetValidationEntry entry = report.Entries[0];
            Console.WriteLine($"{entry.Status}: {entry.RelativePath} backend={entry.BackendName} vertices={entry.Metrics.VertexCount} triangles={entry.Metrics.TriangleCount}");
            if (!string.IsNullOrWhiteSpace(entry.FailureMessage))
                Console.WriteLine(entry.FailureMessage);
            return;
        }

        Console.WriteLine(
            $"Validated {report.Summary.TotalCount} asset(s): " +
            $"accepted={report.Summary.AcceptedCount + report.Summary.AcceptedWithWarningsCount}, " +
            $"rejected={report.Summary.RejectedCount}, " +
            $"crashed={report.Summary.RejectedCrashedCount}, " +
            $"timeout={report.Summary.RejectedTimeoutCount}");

        var classificationCounts = report.Entries
            .SelectMany(entry => entry.Classifications)
            .GroupBy(classification => classification)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}")
            .ToArray();
        if (classificationCounts.Length > 0)
            Console.WriteLine("Classifications: " + string.Join(", ", classificationCounts));

        foreach (AssetValidationEntry entry in report.Entries.Where(entry => entry.Decisions.Count > 0).Take(8))
            Console.WriteLine($"{entry.RelativePath}: {string.Join(" ", entry.Decisions)}");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  Njulf.AssetTool validate <path-or-folder> [--json <output>] [--backend <auto|assimp|sharpgltf>] [--policy <strict|gameDefault|permissive>] [--timeout-ms <ms>] [--max-bytes <bytes>] [--high-texture-bytes <bytes>] [--child-process-all]");
        Console.WriteLine("  Njulf.AssetTool import <path> [--json <output>] [--backend <auto|assimp|sharpgltf>] [--policy <strict|gameDefault|permissive>]");
        Console.WriteLine("  Njulf.AssetTool report <path-or-folder> --json <output> [--backend <auto|assimp|sharpgltf>] [--policy <strict|gameDefault|permissive>]");
        Console.WriteLine("  Njulf.AssetTool cook model <source> --out <folder> [--platform <rid>] [--texture-format <autoBc|rgba8|bc7|bc5|bc4|bc6h>] [--signing-key <pem>] [--backend <auto|assimp|sharpgltf>] [--max-texture-dimension <pixels>] [--force] [--omm-bridge <native-library> --omm-provenance <json> --omm-subdivision <0..12> --omm-max-subdivision <1..12> --omm-max-workload <count> --omm-max-array-bytes <bytes>]");
        Console.WriteLine("  Njulf.AssetTool cook folder|changed <source-folder> --out <folder> [--platform <rid>] [--texture-format <format>] [--signing-key <pem>] [--force] [--omm-bridge <native-library> --omm-provenance <json>]");
        Console.WriteLine("  Njulf.AssetTool clean-stale --out <folder> [--platform <rid>]");
        Console.WriteLine("  Njulf.AssetTool migrate <cooked-folder> [--out <folder>] [--signing-key <pem>]");
        Console.WriteLine("  Njulf.AssetTool keygen --private <pem> --public <pem>");
        Console.WriteLine("  Njulf.AssetTool khronos-material-gi --cache <folder> --out <folder> --report <json> [--manifest <json>] [--offline]");
        Console.WriteLine("  Njulf.AssetTool alpha-visibility-gate --report <json> --evidence <bin> [--verify]");
        Console.WriteLine("  Njulf.AssetTool material-gi-test-matrix --out <json> --build-commit <sha> --shader-fingerprint <sha256> --settings-fingerprint <sha256> --device-id <id> --gpu-name <name> --driver-version <version> --attest-release-build --trx <CpuOracle|GpuOracle|ReleaseTests>=<trx>");
        Console.WriteLine("  Njulf.AssetTool material-gi-evidence assemble --root <folder> --request <json> --bundle <relative-json>");
        Console.WriteLine("  Njulf.AssetTool material-gi-evidence pin-manifest --root <folder> --manifest <relative-json> --bundle <relative-json> --alpha-report <relative-json> --alpha-evidence <relative-bin> --approval-id <id> --approved-at-utc <timestamp> --qualified-device <id> [--qualified-device <id> ...]");
        Console.WriteLine("  Njulf.AssetTool material-gi-evidence verify-manifest --manifest <json> [--evaluation-date <yyyy-MM-dd>]");
    }
}

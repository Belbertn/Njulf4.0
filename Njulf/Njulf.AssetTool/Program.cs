using System;
using System.Globalization;
using System.Text.Json;
using Njulf.Assets;
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
    }
}

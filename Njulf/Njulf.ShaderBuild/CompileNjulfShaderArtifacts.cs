using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MsBuildTask = Microsoft.Build.Utilities.Task;

namespace Njulf.ShaderBuild;

/// <summary>
/// Builds independent GLSL-to-SPIR-V artifacts concurrently and retains a
/// content-addressed cache outside <c>obj</c>. The task owns incremental state
/// because one shader source can intentionally produce many native variants.
/// </summary>
public sealed class CompileNjulfShaderArtifacts : MsBuildTask
{
    private const int SchemaVersion = 1;
    private const int MaximumShaderModuleBytes = 16 * 1024 * 1024;
    private const int MaximumStateFileBytes = 4 * 1024 * 1024;
    private const int MaximumDependencyCount = 4096;
    private const int PublicationRetryCount = 8;
    private readonly ConcurrentDictionary<string, Lazy<string>> _inputHashes =
        new(StringComparer.OrdinalIgnoreCase);

    [Required]
    public ITaskItem[] Artifacts { get; set; } = [];

    [Required]
    public string ShaderRoot { get; set; } = string.Empty;

    [Required]
    public string IntermediateShaderDirectory { get; set; } = string.Empty;

    [Required]
    public string CacheDirectory { get; set; } = string.Empty;

    public string CompilerPath { get; set; } = "glslangValidator";

    public string GlobalCompileOptions { get; set; } = string.Empty;

    public int MaxParallelism { get; set; }

    public string CacheMode { get; set; } = "ReadWrite";

    public string BuildMode { get; set; } = "Compile";

    [Output]
    public string BundleManifestPath { get; private set; } = string.Empty;

    [Output]
    public int CompiledCount { get; private set; }

    [Output]
    public int CacheHitCount { get; private set; }

    [Output]
    public int UpToDateCount { get; private set; }

    public override bool Execute()
    {
        try
        {
            return ExecuteCore();
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    private bool ExecuteCore()
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        string shaderRoot = Path.GetFullPath(ShaderRoot);
        string outputDirectory = Path.GetFullPath(IntermediateShaderDirectory);
        string cacheDirectory = Path.GetFullPath(CacheDirectory);
        ShaderRoot = shaderRoot;
        IntermediateShaderDirectory = outputDirectory;
        CacheDirectory = cacheDirectory;
        bool cacheEnabled = string.Equals(CacheMode, "ReadWrite", StringComparison.OrdinalIgnoreCase);
        bool useExisting = string.Equals(BuildMode, "UseExisting", StringComparison.OrdinalIgnoreCase);

        if (!cacheEnabled && !string.Equals(CacheMode, "Off", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported NjulfShaderCacheMode '{CacheMode}'. Use ReadWrite or Off.");
        if (!useExisting && !string.Equals(BuildMode, "Compile", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported NjulfShaderBuildMode '{BuildMode}'. Use Compile or UseExisting.");
        if (!Directory.Exists(shaderRoot))
            throw new DirectoryNotFoundException($"Shader root '{shaderRoot}' does not exist.");

        List<Artifact> artifacts = BuildArtifacts(shaderRoot, outputDirectory);

        if (useExisting)
        {
            List<string> missing = artifacts
                .Where(artifact => !IsValidSpirvFile(artifact.OutputPath))
                .Select(artifact => artifact.OutputName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (missing.Count != 0)
            {
                Log.LogError(
                    "NjulfShaderBuildMode=UseExisting requires valid existing SPIR-V output(s): {0}",
                    string.Join(", ", missing));
                return false;
            }

            WarnForOlderExistingOutputs(artifacts);
            UpToDateCount = artifacts.Count;
            BundleManifestPath = WriteBundleManifest(outputDirectory, artifacts);
            Log.LogMessage(MessageImportance.High,
                "Njulf shaders: reused {0} existing artifact(s) in {1:F1}s; no compiler or cache access occurred.",
                artifacts.Count,
                totalStopwatch.Elapsed.TotalSeconds);
            return true;
        }

        Directory.CreateDirectory(outputDirectory);
        string localStateDirectory = Path.Combine(outputDirectory, ".state");
        Directory.CreateDirectory(localStateDirectory);
        if (cacheEnabled)
        {
            Directory.CreateDirectory(Path.Combine(cacheDirectory, "index"));
            Directory.CreateDirectory(Path.Combine(cacheDirectory, "objects"));
        }
        RemoveObsoleteActiveOutputs(outputDirectory, artifacts);

        Toolchain toolchain = ResolveToolchain(CompilerPath);
        string globalOptions = NormalizeArgumentText(GlobalCompileOptions);
        List<ArtifactWork> work = artifacts
            .Select(artifact => CreateWork(artifact, toolchain, globalOptions, localStateDirectory, cacheDirectory, cacheEnabled))
            .ToList();

        var misses = new List<ArtifactWork>();
        foreach (ArtifactWork item in work)
        {
            WorkDisposition disposition = TryMaterializeOrReuse(item);
            switch (disposition)
            {
                case WorkDisposition.UpToDate:
                    UpToDateCount++;
                    break;
                case WorkDisposition.CacheHit:
                    CacheHitCount++;
                    break;
                default:
                    misses.Add(item);
                    break;
            }
        }

        if (misses.Count != 0)
        {
            int parallelism = ResolveParallelism(MaxParallelism);
            var failures = new ConcurrentBag<ArtifactFailure>();
            var timings = new ConcurrentBag<ArtifactTiming>();
            var cancellation = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellation.Token
            };

            try
            {
                Parallel.ForEach(misses, options, item =>
                {
                    if (cancellation.IsCancellationRequested)
                        return;

                    try
                    {
                        Stopwatch artifactStopwatch = Stopwatch.StartNew();
                        CompileAndPublish(item);
                        timings.Add(new ArtifactTiming(
                            item.Artifact.OutputName,
                            artifactStopwatch.Elapsed));
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new ArtifactFailure(item.Artifact.OutputName, exception.Message));
                        cancellation.Cancel();
                    }
                });
            }
            catch (OperationCanceledException) when (!failures.IsEmpty)
            {
                // A compiler failure is reported below with artifact-specific diagnostics.
            }

            if (!failures.IsEmpty)
            {
                foreach (ArtifactFailure failure in failures.OrderBy(failure => failure.OutputName, StringComparer.Ordinal))
                    Log.LogError("Shader '{0}' failed:{1}{2}", failure.OutputName, Environment.NewLine, failure.Message);
                return false;
            }

            CompiledCount = misses.Count;
            Log.LogMessage(MessageImportance.High,
                "Njulf shaders: compiled {0} artifact(s) with parallelism {1} in {2:F1}s.",
                misses.Count,
                parallelism,
                stopwatch.Elapsed.TotalSeconds);

            foreach (ArtifactTiming timing in timings
                         .OrderByDescending(timing => timing.Elapsed)
                         .ThenBy(timing => timing.OutputName, StringComparer.Ordinal)
                         .Take(10))
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    "Njulf shader timing: {0} {1:F3}s",
                    timing.OutputName,
                    timing.Elapsed.TotalSeconds);
            }
        }

        BundleManifestPath = WriteBundleManifest(outputDirectory, artifacts);
        Log.LogMessage(MessageImportance.High,
            "Njulf shaders: {0} compiled, {1} cache hit, {2} up-to-date in {3:F1}s.",
            CompiledCount,
            CacheHitCount,
            UpToDateCount,
            totalStopwatch.Elapsed.TotalSeconds);
        return true;
    }

    private List<Artifact> BuildArtifacts(string shaderRoot, string outputDirectory)
    {
        var artifacts = new List<Artifact>(Artifacts.Length);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ITaskItem item in Artifacts)
        {
            string identity = item.ItemSpec.Trim();
            if (string.IsNullOrWhiteSpace(identity))
                throw new InvalidOperationException("A shader artifact has an empty identity.");
            if (identity.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || identity.Contains(Path.DirectorySeparatorChar) || identity.Contains(Path.AltDirectorySeparatorChar))
                throw new InvalidOperationException($"Shader artifact '{identity}' must be an unqualified output name.");
            if (!names.Add(identity))
                throw new InvalidOperationException($"Shader artifact output '{identity}.spv' was declared more than once.");

            string sourceMetadata = item.GetMetadata("Source");
            if (string.IsNullOrWhiteSpace(sourceMetadata))
                throw new InvalidOperationException($"Shader artifact '{identity}' has no Source metadata.");
            string sourcePath = Path.GetFullPath(Path.IsPathFullyQualified(sourceMetadata)
                ? sourceMetadata
                : Path.Combine(shaderRoot, sourceMetadata));
            if (!sourcePath.StartsWith(shaderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourcePath, shaderRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Shader artifact '{identity}' source '{sourcePath}' is outside '{shaderRoot}'.");
            }
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Shader artifact '{identity}' source does not exist.", sourcePath);

            artifacts.Add(new Artifact(
                identity,
                identity + ".spv",
                sourcePath,
                NormalizeArgumentText(item.GetMetadata("Defines")),
                NormalizeArgumentText(item.GetMetadata("CompileOptions")),
                NormalizeArgumentText(item.GetMetadata("AdditionalCompileOptions")),
                Path.Combine(outputDirectory, identity + ".spv")));
        }

        if (artifacts.Count == 0)
            throw new InvalidOperationException("No Njulf shader artifacts were declared.");
        return artifacts.OrderBy(artifact => artifact.OutputName, StringComparer.Ordinal).ToList();
    }

    private ArtifactWork CreateWork(
        Artifact artifact,
        Toolchain toolchain,
        string globalOptions,
        string localStateDirectory,
        string cacheDirectory,
        bool cacheEnabled)
    {
        string effectiveCompileOptions = string.IsNullOrWhiteSpace(artifact.CompileOptions)
            ? globalOptions
            : artifact.CompileOptions;
        string relativeSource = NormalizeRelativePath(ShaderRoot, artifact.SourcePath);
        string recipe = string.Join("\n", [
            $"schema={SchemaVersion}",
            $"output={artifact.OutputName}",
            $"source={relativeSource}",
            $"global={effectiveCompileOptions}",
            $"additional={artifact.AdditionalCompileOptions}",
            $"defines={artifact.Defines}",
            "target=vulkan1.3",
            $"tool={toolchain.Fingerprint}"
        ]);
        string recipeKey = HashText(recipe);
        string localStatePath = Path.Combine(localStateDirectory, recipeKey + ".json");
        string cacheIndexPath = Path.Combine(cacheDirectory, "index", recipeKey + ".json");
        return new ArtifactWork(
            artifact,
            toolchain,
            effectiveCompileOptions,
            recipeKey,
            localStatePath,
            cacheIndexPath,
            cacheDirectory,
            cacheEnabled);
    }

    private WorkDisposition TryMaterializeOrReuse(ArtifactWork work)
    {
        ArtifactState? state = GetReusableState(work.LocalStatePath, work);
        if (state == null && work.CacheEnabled)
            state = GetReusableState(work.CacheIndexPath, work);
        if (state == null)
            return WorkDisposition.Compile;

        if (IsValidSpirvFile(work.Artifact.OutputPath) &&
            string.Equals(HashFile(work.Artifact.OutputPath), state.OutputHash, StringComparison.Ordinal))
        {
            if (!string.Equals(work.LocalStatePath, work.CacheIndexPath, StringComparison.OrdinalIgnoreCase))
                WriteStateIfChanged(work.LocalStatePath, state);
            return WorkDisposition.UpToDate;
        }

        if (!work.CacheEnabled || string.IsNullOrWhiteSpace(state.ContentKey))
            return WorkDisposition.Compile;
        string objectPath = GetCacheObjectPath(work.CacheDirectory, state.ContentKey);
        if (!IsValidSpirvFile(objectPath) ||
            !string.Equals(HashFile(objectPath), state.OutputHash, StringComparison.Ordinal))
        {
            return WorkDisposition.Compile;
        }

        CopyAtomically(objectPath, work.Artifact.OutputPath);
        WriteStateIfChanged(work.LocalStatePath, state);
        return WorkDisposition.CacheHit;
    }

    private ArtifactState? GetReusableState(string path, ArtifactWork work)
    {
        ArtifactState? state = ReadState(path);
        if (state == null || state.SchemaVersion != SchemaVersion ||
            !IsSha256(state.RecipeKey) ||
            !IsSha256(state.ContentKey) ||
            !IsSha256(state.OutputHash) ||
            !string.Equals(state.RecipeKey, work.RecipeKey, StringComparison.Ordinal) ||
            !DependenciesMatch(
                state.Dependencies,
                work.Artifact.SourcePath,
                work.RecipeKey,
                out string expectedContentKey) ||
            !string.Equals(state.ContentKey, expectedContentKey, StringComparison.Ordinal))
        {
            return null;
        }

        return state;
    }

    private void CompileAndPublish(ArtifactWork work)
    {
        string outputDirectory = Path.GetDirectoryName(work.Artifact.OutputPath)!;
        string token = Guid.NewGuid().ToString("N");
        // Artifact identities can be long specialization names. Repeating one
        // in a temporary filename can push an otherwise valid hermetic output
        // directory over native Windows tool path limits. The random token is
        // sufficient for publication isolation; diagnostics already carry the
        // owning artifact identity.
        string temporaryStem = ".njulf-" + token;
        string temporaryOutput = Path.Combine(outputDirectory, temporaryStem + ".spv.tmp");
        string temporaryDepfile = Path.Combine(outputDirectory, temporaryStem + ".d.tmp");
        try
        {
            ProcessResult result = RunCompiler(work, temporaryOutput, temporaryDepfile);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"glslangValidator exited with code {result.ExitCode}.{Environment.NewLine}{result.Output}");
            }
            if (!IsValidSpirvFile(temporaryOutput))
                throw new InvalidOperationException("glslangValidator did not produce a valid bounded SPIR-V word stream.");

            List<DependencyState> dependencies = ParseDependencies(temporaryDepfile, work.Artifact.SourcePath);
            string outputHash = HashFile(temporaryOutput);
            string contentKey = CreateContentKey(work.RecipeKey, dependencies);
            var state = new ArtifactState(SchemaVersion, work.RecipeKey, contentKey, outputHash, dependencies);

            PublishFile(temporaryOutput, work.Artifact.OutputPath);
            temporaryOutput = string.Empty;
            WriteStateIfChanged(work.LocalStatePath, state);
            if (work.CacheEnabled)
            {
                string objectPath = GetCacheObjectPath(work.CacheDirectory, contentKey);
                if (!IsValidSpirvFile(objectPath) ||
                    !string.Equals(HashFile(objectPath), outputHash, StringComparison.Ordinal))
                {
                    CopyAtomically(work.Artifact.OutputPath, objectPath);
                }
                WriteStateIfChanged(work.CacheIndexPath, state);
            }
        }
        finally
        {
            DeleteIfExists(temporaryOutput);
            DeleteIfExists(temporaryDepfile);
        }
    }

    private ProcessResult RunCompiler(ArtifactWork work, string temporaryOutput, string temporaryDepfile)
    {
        var start = new ProcessStartInfo
        {
            FileName = work.Toolchain.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = ShaderRoot
        };
        start.ArgumentList.Add("-V");
        start.ArgumentList.Add("--target-env");
        start.ArgumentList.Add("vulkan1.3");
        AddArguments(start.ArgumentList, work.GlobalOptions);
        AddArguments(start.ArgumentList, work.Artifact.AdditionalCompileOptions);
        AddArguments(start.ArgumentList, work.Artifact.Defines);
        start.ArgumentList.Add("-I" + ShaderRoot);
        start.ArgumentList.Add("--depfile");
        start.ArgumentList.Add(temporaryDepfile);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(temporaryOutput);
        start.ArgumentList.Add(work.Artifact.SourcePath);

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException($"Could not start '{work.Toolchain.ExecutablePath}'.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        string output = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text)));
        return new ProcessResult(process.ExitCode, output);
    }

    private List<DependencyState> ParseDependencies(string depfilePath, string sourcePath)
    {
        if (!File.Exists(depfilePath))
            throw new InvalidOperationException($"glslangValidator did not produce depfile '{depfilePath}'.");

        string text = File.ReadAllText(depfilePath)
            .Replace("\\\r\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);
        int separator = FindDepfileSeparator(text);
        if (separator < 0)
            throw new InvalidOperationException($"Could not parse depfile '{depfilePath}'.");
        string dependencyText = text[(separator + 1)..];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in SplitDepfileTokens(dependencyText))
        {
            AddDependencyPath(paths, token);
        }
        AddDependencyPath(paths, sourcePath);

        var result = new List<DependencyState>(paths.Count);
        foreach (string fullPath in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"A shader depfile references a missing file: '{fullPath}'.", fullPath);
            result.Add(new DependencyState(
                NormalizeRelativePath(ShaderRoot, fullPath),
                HashInputFile(fullPath)));
        }
        return result.OrderBy(dependency => dependency.Path, StringComparer.Ordinal).ToList();
    }

    private void AddDependencyPath(HashSet<string> paths, string path)
    {
        string fullPath = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(ShaderRoot, path));
        if (!fullPath.StartsWith(ShaderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, ShaderRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Shader dependency '{fullPath}' is outside shader root '{ShaderRoot}'.");
        }
        paths.Add(fullPath);
    }

    private bool DependenciesMatch(
        IReadOnlyList<DependencyState>? dependencies,
        string sourcePath,
        string recipeKey,
        out string contentKey)
    {
        contentKey = string.Empty;
        if (dependencies == null ||
            dependencies.Count == 0 ||
            dependencies.Count > MaximumDependencyCount)
        {
            return false;
        }

        string relativeSource;
        try
        {
            relativeSource = NormalizeRelativePath(ShaderRoot, sourcePath);
        }
        catch (Exception exception) when (IsRecoverableStateException(exception))
        {
            return false;
        }

        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        bool containsSource = false;
        foreach (DependencyState? dependency in dependencies)
        {
            if (dependency == null ||
                string.IsNullOrWhiteSpace(dependency.Path) ||
                !IsSha256(dependency.Hash))
            {
                return false;
            }

            try
            {
                if (Path.IsPathFullyQualified(dependency.Path))
                    return false;

                string path = Path.GetFullPath(Path.Combine(
                    ShaderRoot,
                    dependency.Path.Replace('/', Path.DirectorySeparatorChar)));
                string normalizedPath = NormalizeRelativePath(ShaderRoot, path);
                if (!string.Equals(normalizedPath, dependency.Path, StringComparison.Ordinal) ||
                    !uniquePaths.Add(normalizedPath) ||
                    previousPath != null && string.CompareOrdinal(previousPath, normalizedPath) >= 0)
                {
                    return false;
                }

                if (!File.Exists(path) ||
                    !string.Equals(HashInputFile(path), dependency.Hash, StringComparison.Ordinal))
                {
                    return false;
                }

                previousPath = normalizedPath;
                containsSource |= string.Equals(
                    normalizedPath,
                    relativeSource,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (IsRecoverableStateException(exception))
            {
                return false;
            }
        }

        if (!containsSource)
            return false;

        contentKey = CreateContentKey(recipeKey, dependencies);
        return true;
    }

    private string WriteBundleManifest(string outputDirectory, IReadOnlyList<Artifact> artifacts)
    {
        var manifest = new BundleManifest(
            SchemaVersion,
            artifacts.Select(artifact => new BundleArtifact(artifact.OutputName, HashFile(artifact.OutputPath))).ToList());
        string path = Path.Combine(outputDirectory, "njulf-shaders.manifest.json");
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        WriteTextIfChanged(path, json);
        return path;
    }

    private void RemoveObsoleteActiveOutputs(string outputDirectory, IReadOnlyList<Artifact> artifacts)
    {
        var expected = new HashSet<string>(artifacts.Select(artifact => artifact.OutputName), StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*.spv", SearchOption.TopDirectoryOnly))
        {
            if (!expected.Contains(Path.GetFileName(path)))
                File.Delete(path);
        }
    }

    private void WarnForOlderExistingOutputs(IReadOnlyList<Artifact> artifacts)
    {
        List<string> stale = artifacts
            .Where(artifact =>
                File.GetLastWriteTimeUtc(artifact.OutputPath) <
                File.GetLastWriteTimeUtc(artifact.SourcePath))
            .Select(artifact => artifact.OutputName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (stale.Count != 0)
        {
            Log.LogWarning(
                "NjulfShaderBuildMode=UseExisting accepted {0} output(s) older than their direct shader source: {1}",
                stale.Count,
                string.Join(", ", stale));
        }
    }

    private static Toolchain ResolveToolchain(string compilerPath)
    {
        string executablePath = ResolveExecutablePath(compilerPath);
        ProcessResult result = RunProcess(executablePath, ["--version"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not query '{executablePath} --version': {result.Output}");
        string binaryHash = File.Exists(executablePath) ? HashFile(executablePath) : "path:" + executablePath;
        return new Toolchain(executablePath, HashText(binaryHash + "\n" + result.Output.Trim()));
    }

    private static string ResolveExecutablePath(string compilerPath)
    {
        if (Path.IsPathFullyQualified(compilerPath) || compilerPath.Contains(Path.DirectorySeparatorChar) || compilerPath.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(compilerPath);
        string[] candidates = OperatingSystem.IsWindows() && Path.GetExtension(compilerPath).Length == 0
            ? [compilerPath, compilerPath + ".exe"]
            : [compilerPath];
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path != null)
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string candidate in candidates)
                {
                    string fullPath = Path.Combine(directory.Trim(), candidate);
                    if (File.Exists(fullPath))
                        return Path.GetFullPath(fullPath);
                }
            }
        }
        return compilerPath;
    }

    private static ProcessResult RunProcess(string executablePath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text))));
    }

    private static int ResolveParallelism(int requested) =>
        requested > 0 ? requested : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    private static bool IsValidSpirvFile(string path)
    {
        if (!File.Exists(path))
            return false;
        FileInfo info = new(path);
        if (info.Length < sizeof(uint) || info.Length > MaximumShaderModuleBytes || info.Length % sizeof(uint) != 0)
            return false;
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[sizeof(uint)];
        if (stream.Read(magic) != magic.Length)
            return false;
        return BitConverter.ToUInt32(magic) == 0x07230203;
    }

    private static ArtifactState? ReadState(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaximumStateFileBytes)
                return null;
            return JsonSerializer.Deserialize<ArtifactState>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteStateIfChanged(string path, ArtifactState state)
    {
        string json = JsonSerializer.Serialize(state, JsonOptions);
        WriteTextIfChanged(path, json);
    }

    private static void WriteTextIfChanged(string path, string contents)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
            return;
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporaryPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        PublishFile(temporaryPath, path);
    }

    private static void CopyAtomically(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporaryPath = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporaryPath, overwrite: true);
            PublishFile(temporaryPath, destination);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void PublishFile(string temporaryPath, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException &&
                attempt + 1 < PublicationRetryCount)
            {
                // Separate MSBuild nodes and worktrees can publish the same
                // content-addressed object or state entry at the same time.
                // Windows exposes the atomic replacement as a brief sharing
                // violation; bounded retries retain last-writer semantics.
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }

    private static string GetCacheObjectPath(string cacheDirectory, string contentKey) =>
        Path.Combine(cacheDirectory, "objects", contentKey[..2], contentKey + ".spv");

    private static string NormalizeRelativePath(string root, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{path}' is outside root '{root}'.");
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool IsRecoverableStateException(Exception exception) =>
        exception is ArgumentException or IOException or InvalidOperationException or
            NotSupportedException or UnauthorizedAccessException;

    private static int FindDepfileSeparator(string text)
    {
        for (int index = 0; index < text.Length - 1; index++)
        {
            if (text[index] == ':' && char.IsWhiteSpace(text[index + 1]))
                return index;
        }
        return -1;
    }

    /// <summary>
    /// glslang emits makefile-style depfiles. On Windows it escapes the drive
    /// colon, hash character, and every path separator, so a whitespace split
    /// followed by a couple of string replacements is not sufficient.
    /// </summary>
    private static IEnumerable<string> SplitDepfileTokens(string text)
    {
        var current = new StringBuilder();
        bool escaped = false;
        foreach (char character in text)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length != 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (escaped)
            current.Append('\\');
        if (current.Length != 0)
            yield return current.ToString();
    }

    private static string NormalizeArgumentText(string value) => string.Join(' ', SplitArguments(value));

    private static void AddArguments(IList<string> target, string value)
    {
        foreach (string argument in SplitArguments(value))
            target.Add(argument);
    }

    private static IReadOnlyList<string> SplitArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var values = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        foreach (char character in value)
        {
            if ((character == '\'' || character == '"'))
            {
                if (quote == '\0')
                {
                    quote = character;
                    continue;
                }
                if (quote == character)
                {
                    quote = '\0';
                    continue;
                }
            }
            if (char.IsWhiteSpace(character) && quote == '\0')
            {
                if (current.Length != 0)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (quote != '\0')
            throw new InvalidOperationException($"Unterminated quote in shader compiler argument text '{value}'.");
        if (current.Length != 0)
            values.Add(current.ToString());
        return values;
    }

    private static string HashText(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string CreateContentKey(
        string recipeKey,
        IEnumerable<DependencyState> dependencies) =>
        HashText(recipeKey + "\n" + string.Join(
            "\n",
            dependencies.Select(dependency => dependency.Path + "=" + dependency.Hash)));

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private string HashInputFile(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return _inputHashes.GetOrAdd(
            fullPath,
            static value => new Lazy<string>(
                () => HashFile(value),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed record Artifact(
        string Identity,
        string OutputName,
        string SourcePath,
        string Defines,
        string CompileOptions,
        string AdditionalCompileOptions,
        string OutputPath);

    private sealed record ArtifactWork(
        Artifact Artifact,
        Toolchain Toolchain,
        string GlobalOptions,
        string RecipeKey,
        string LocalStatePath,
        string CacheIndexPath,
        string CacheDirectory,
        bool CacheEnabled);

    private sealed record Toolchain(string ExecutablePath, string Fingerprint);

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed record ArtifactFailure(string OutputName, string Message);

    private sealed record ArtifactTiming(string OutputName, TimeSpan Elapsed);

    private sealed record DependencyState(string Path, string Hash);

    private sealed record ArtifactState(
        int SchemaVersion,
        string RecipeKey,
        string ContentKey,
        string OutputHash,
        List<DependencyState>? Dependencies);

    private sealed record BundleArtifact(string Name, string Hash);

    private sealed record BundleManifest(int SchemaVersion, List<BundleArtifact> Artifacts);

    private enum WorkDisposition
    {
        Compile,
        CacheHit,
        UpToDate
    }
}

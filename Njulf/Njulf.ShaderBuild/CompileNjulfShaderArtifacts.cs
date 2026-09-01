using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MsBuildTask = Microsoft.Build.Utilities.Task;

[assembly: InternalsVisibleTo("Njulf.ShaderBuild.Tests")]

namespace Njulf.ShaderBuild;

/// <summary>
/// Builds independent GLSL-to-SPIR-V artifacts concurrently and retains a
/// content-addressed cache outside <c>obj</c>. The task owns incremental state
/// because one shader source can intentionally produce many native variants.
/// </summary>
public sealed class CompileNjulfShaderArtifacts : MsBuildTask, ICancelableTask
{
    private const int SchemaVersion = 1;
    private const int MaximumShaderModuleBytes = 16 * 1024 * 1024;
    private const int MaximumStateFileBytes = 4 * 1024 * 1024;
    private const int MaximumDependencyCount = 4096;
    private const int PublicationRetryCount = 8;
    private const int DefaultCompilerTimeoutSeconds = 15 * 60;
    private const int CompilerProbeTimeoutSeconds = 30;
    private const int ProgressHeartbeatSeconds = 60;
    private const int RecipeLockRetryMilliseconds = 100;
    private const int RecipeLockTimeoutGraceSeconds = 60;
    private readonly ConcurrentDictionary<string, Lazy<string>> _inputHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cancellation = new();
    private int _externalCancellationRequested;

    internal ICompilerProcessRunner ProcessRunner { get; set; } =
        new CompilerProcessRunner();

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

    public int CompilerTimeoutSeconds { get; set; } = DefaultCompilerTimeoutSeconds;

    public string CacheMode { get; set; } = "ReadWrite";

    public string BuildMode { get; set; } = "Compile";

    [Output]
    public string BundleManifestPath { get; private set; } = string.Empty;

    [Output]
    public string BundleHash { get; private set; } = string.Empty;

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
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            Log.LogMessage(
                MessageImportance.High,
                "Njulf shader compilation was canceled; active compiler process trees were terminated.");
            return false;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: true);
            return false;
        }
    }

    public void Cancel()
    {
        Interlocked.Exchange(ref _externalCancellationRequested, 1);
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        _cancellation.Cancel();
        ProcessRunner.CancelAll();
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
        if (CompilerTimeoutSeconds < 0)
            throw new InvalidOperationException("NjulfShaderCompilerTimeoutSeconds cannot be negative. Use 0 to disable the timeout.");
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
            Directory.CreateDirectory(Path.Combine(cacheDirectory, "locks"));
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
            var dispositions = new ConcurrentBag<WorkDisposition>();
            var stopwatch = Stopwatch.StartNew();
            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = _cancellation.Token
            };

            try
            {
                Parallel.ForEach(misses, options, item =>
                {
                    try
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        Stopwatch artifactStopwatch = Stopwatch.StartNew();
                        WorkDisposition disposition = ResolveMiss(item);
                        dispositions.Add(disposition);
                        if (disposition == WorkDisposition.Compiled)
                        {
                            timings.Add(new ArtifactTiming(
                                item.Artifact.OutputName,
                                artifactStopwatch.Elapsed));
                        }
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        // The initiating failure or external cancellation is
                        // reported once after all active compiler trees stop.
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new ArtifactFailure(item.Artifact.OutputName, exception.Message));
                        RequestCancellation();
                    }
                });
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // The initiating failure or external cancellation is reported below.
            }

            if (!failures.IsEmpty)
            {
                foreach (ArtifactFailure failure in failures.OrderBy(failure => failure.OutputName, StringComparer.Ordinal))
                    Log.LogError("Shader '{0}' failed:{1}{2}", failure.OutputName, Environment.NewLine, failure.Message);
                return false;
            }

            if (Volatile.Read(ref _externalCancellationRequested) != 0)
            {
                Log.LogMessage(
                    MessageImportance.High,
                    "Njulf shader compilation was canceled; no partial compiler output was published.");
                return false;
            }

            CompiledCount += dispositions.Count(disposition => disposition == WorkDisposition.Compiled);
            CacheHitCount += dispositions.Count(disposition => disposition == WorkDisposition.CacheHit);
            UpToDateCount += dispositions.Count(disposition => disposition == WorkDisposition.UpToDate);
            Log.LogMessage(MessageImportance.High,
                "Njulf shaders: resolved {0} initial miss(es) with parallelism {1}: {2} compiled, {3} cache hit, {4} up-to-date in {5:F1}s.",
                misses.Count,
                parallelism,
                dispositions.Count(disposition => disposition == WorkDisposition.Compiled),
                dispositions.Count(disposition => disposition == WorkDisposition.CacheHit),
                dispositions.Count(disposition => disposition == WorkDisposition.UpToDate),
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

    private WorkDisposition ResolveMiss(ArtifactWork work)
    {
        _cancellation.Token.ThrowIfCancellationRequested();
        if (!work.CacheEnabled)
        {
            CompileAndPublish(work);
            return WorkDisposition.Compiled;
        }

        using FileStream recipeLock = AcquireRecipeLock(work);
        _cancellation.Token.ThrowIfCancellationRequested();

        WorkDisposition disposition = TryMaterializeOrReuse(work);
        if (disposition != WorkDisposition.Compile)
        {
            Log.LogMessage(
                MessageImportance.Normal,
                "Njulf shader single-flight reuse: {0} became {1} after waiting for recipe {2}.",
                work.Artifact.OutputName,
                disposition == WorkDisposition.CacheHit ? "a cache hit" : "up-to-date",
                work.RecipeKey);
            return disposition;
        }

        CompileAndPublish(work);
        return WorkDisposition.Compiled;
    }

    private FileStream AcquireRecipeLock(ArtifactWork work)
    {
        string lockPath = Path.Combine(
            work.CacheDirectory,
            "locks",
            work.RecipeKey + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan nextHeartbeat = TimeSpan.FromSeconds(ProgressHeartbeatSeconds);
        TimeSpan? timeout = CompilerTimeoutSeconds == 0
            ? null
            : TimeSpan.FromSeconds(
                (long)CompilerTimeoutSeconds + RecipeLockTimeoutGraceSeconds);
        IOException? lastFailure = null;
        bool loggedContention = false;

        while (true)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                if (loggedContention)
                {
                    Log.LogMessage(
                        MessageImportance.Normal,
                        "Njulf shader single-flight acquired: {0} after {1:F1}s.",
                        work.Artifact.OutputName,
                        stopwatch.Elapsed.TotalSeconds);
                }
                return stream;
            }
            catch (IOException exception)
            {
                lastFailure = exception;
            }

            if (!loggedContention)
            {
                loggedContention = true;
                Log.LogMessage(
                    MessageImportance.High,
                    "Njulf shader single-flight waiting: {0} is already being built by another task (recipe {1}).",
                    work.Artifact.OutputName,
                    work.RecipeKey);
            }

            if (timeout.HasValue && stopwatch.Elapsed >= timeout.Value)
            {
                throw new TimeoutException(
                    $"Timed out after {stopwatch.Elapsed.TotalSeconds:F1}s waiting for the shared shader-cache recipe lock " +
                    $"'{lockPath}' for '{work.Artifact.OutputName}'.",
                    lastFailure);
            }

            if (stopwatch.Elapsed >= nextHeartbeat)
            {
                Log.LogMessage(
                    MessageImportance.High,
                    "Njulf shader single-flight still waiting: {0}, elapsed {1:F0}s{2}.",
                    work.Artifact.OutputName,
                    stopwatch.Elapsed.TotalSeconds,
                    timeout.HasValue
                        ? $", timeout {timeout.Value.TotalSeconds:F0}s"
                        : ", timeout disabled");
                do
                {
                    nextHeartbeat += TimeSpan.FromSeconds(ProgressHeartbeatSeconds);
                }
                while (stopwatch.Elapsed >= nextHeartbeat);
            }

            if (_cancellation.Token.WaitHandle.WaitOne(RecipeLockRetryMilliseconds))
                _cancellation.Token.ThrowIfCancellationRequested();
        }
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
            CompilerProcessResult result = RunCompiler(
                work,
                temporaryOutput,
                temporaryDepfile);
            _cancellation.Token.ThrowIfCancellationRequested();
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

            _cancellation.Token.ThrowIfCancellationRequested();
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

    private CompilerProcessResult RunCompiler(
        ArtifactWork work,
        string temporaryOutput,
        string temporaryDepfile)
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

        TimeSpan? timeout = CompilerTimeoutSeconds == 0
            ? null
            : TimeSpan.FromSeconds(CompilerTimeoutSeconds);
        try
        {
            return ProcessRunner.Run(
                start,
                timeout,
                TimeSpan.FromSeconds(ProgressHeartbeatSeconds),
                _cancellation.Token,
                heartbeat => Log.LogMessage(
                    MessageImportance.High,
                    "Njulf shader compiler still running: {0}, PID {1}, elapsed {2:F0}s{3}.",
                    work.Artifact.OutputName,
                    heartbeat.ProcessId,
                    heartbeat.Elapsed.TotalSeconds,
                    timeout.HasValue
                        ? $", timeout {timeout.Value.TotalSeconds:F0}s"
                        : ", timeout disabled"));
        }
        catch (CompilerProcessTimeoutException exception)
        {
            string output = string.IsNullOrWhiteSpace(exception.Output)
                ? string.Empty
                : Environment.NewLine + exception.Output;
            throw new TimeoutException(
                $"glslangValidator timed out compiling '{work.Artifact.OutputName}' after " +
                $"{exception.Elapsed.TotalSeconds:F1}s (PID {exception.ProcessId}); the compiler process tree was terminated." +
                output,
                exception);
        }
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
        List<BundleArtifact> bundleArtifacts = artifacts
            .Select(artifact => new BundleArtifact(
                artifact.OutputName,
                HashFile(artifact.OutputPath)))
            .OrderBy(artifact => artifact.Name, StringComparer.Ordinal)
            .ToList();
        BundleHash = ComputeBundleHash(bundleArtifacts);
        var manifest = new BundleManifest(
            SchemaVersion,
            BundleHash,
            bundleArtifacts);
        string path = Path.Combine(outputDirectory, "njulf-shaders.manifest.json");
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        WriteTextIfChanged(path, json);
        return path;
    }

    private static string ComputeBundleHash(
        IReadOnlyList<BundleArtifact> artifacts)
    {
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBundleHashText(hash, "njulf-effective-shader-bundle-v1");
        foreach (BundleArtifact artifact in artifacts)
        {
            AppendBundleHashText(hash, artifact.Name);
            hash.AppendData(Convert.FromHexString(artifact.Hash));
        }
        return "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendBundleHashText(
        IncrementalHash hash,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
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

    private Toolchain ResolveToolchain(string compilerPath)
    {
        string executablePath = ResolveExecutablePath(compilerPath);
        CompilerProcessResult result;
        try
        {
            result = RunProcess(
                executablePath,
                ["--version"],
                TimeSpan.FromSeconds(CompilerProbeTimeoutSeconds));
        }
        catch (CompilerProcessTimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out after {exception.Elapsed.TotalSeconds:F1}s querying " +
                $"'{executablePath} --version' (PID {exception.ProcessId}); the process tree was terminated.",
                exception);
        }
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

    private CompilerProcessResult RunProcess(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
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
        return ProcessRunner.Run(
            start,
            timeout,
            TimeSpan.Zero,
            _cancellation.Token,
            heartbeat: null);
    }

    private static int ResolveParallelism(int requested) =>
        requested > 0 ? requested : Math.Clamp(Environment.ProcessorCount, 1, 8);

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

    private sealed record BundleManifest(
        int SchemaVersion,
        string BundleHash,
        List<BundleArtifact> Artifacts);

    private enum WorkDisposition
    {
        Compile,
        Compiled,
        CacheHit,
        UpToDate
    }
}

internal interface ICompilerProcessRunner
{
    CompilerProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan? timeout,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken,
        Action<CompilerProcessHeartbeat>? heartbeat);

    void CancelAll();
}

internal sealed record CompilerProcessResult(
    int ExitCode,
    string Output,
    int ProcessId,
    TimeSpan Elapsed);

internal readonly record struct CompilerProcessHeartbeat(
    int ProcessId,
    TimeSpan Elapsed);

internal sealed class CompilerProcessTimeoutException : TimeoutException
{
    public CompilerProcessTimeoutException(
        int processId,
        TimeSpan elapsed,
        string output)
        : base($"Process {processId} timed out after {elapsed.TotalSeconds:F1}s.")
    {
        ProcessId = processId;
        Elapsed = elapsed;
        Output = output;
    }

    public int ProcessId { get; }

    public TimeSpan Elapsed { get; }

    public string Output { get; }
}

internal sealed class CompilerProcessRunner : ICompilerProcessRunner
{
    private const int WaitSliceMilliseconds = 250;
    private const int TerminationWaitMilliseconds = 5_000;
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    public CompilerProcessResult Run(
        ProcessStartInfo startInfo,
        TimeSpan? timeout,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken,
        Action<CompilerProcessHeartbeat>? heartbeat)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (heartbeatInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start '{startInfo.FileName}'.");

        int processId = process.Id;
        if (!_activeProcesses.TryAdd(processId, process))
        {
            TryKillProcessTree(process);
            throw new InvalidOperationException(
                $"Compiler process ID {processId} was already active.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Stopwatch stopwatch = Stopwatch.StartNew();
        TimeSpan nextHeartbeat = heartbeatInterval;
        bool completed = false;
        try
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    bool terminated = TerminateAndWait(process);
                    string output = terminated
                        ? DrainOutput(stdoutTask, stderrTask)
                        : string.Empty;
                    throw new OperationCanceledException(
                        $"Compiler process {processId} was canceled" +
                        (terminated
                            ? "."
                            : " but did not exit within the termination grace period.") +
                        FormatOutput(output),
                        cancellationToken);
                }

                if (process.WaitForExit(WaitSliceMilliseconds))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        string canceledOutput = DrainOutput(
                            stdoutTask,
                            stderrTask);
                        throw new OperationCanceledException(
                            $"Compiler process {processId} was canceled." +
                            FormatOutput(canceledOutput),
                            cancellationToken);
                    }
                    break;
                }

                TimeSpan elapsed = stopwatch.Elapsed;
                if (timeout.HasValue && elapsed >= timeout.Value)
                {
                    bool terminated = TerminateAndWait(process);
                    string output = terminated
                        ? DrainOutput(stdoutTask, stderrTask)
                        : string.Empty;
                    throw new CompilerProcessTimeoutException(
                        processId,
                        elapsed,
                        output + (terminated
                            ? string.Empty
                            : Environment.NewLine +
                              "The process tree did not exit within the termination grace period."));
                }

                if (heartbeat != null &&
                    heartbeatInterval > TimeSpan.Zero &&
                    elapsed >= nextHeartbeat)
                {
                    heartbeat(new CompilerProcessHeartbeat(processId, elapsed));
                    do
                    {
                        nextHeartbeat += heartbeatInterval;
                    }
                    while (elapsed >= nextHeartbeat);
                }
            }

            string processOutput = DrainOutput(stdoutTask, stderrTask);
            completed = true;
            return new CompilerProcessResult(
                process.ExitCode,
                processOutput,
                processId,
                stopwatch.Elapsed);
        }
        finally
        {
            if (!completed)
                TerminateAndWait(process);
            _activeProcesses.TryRemove(processId, out _);
        }
    }

    public void CancelAll()
    {
        foreach (Process process in _activeProcesses.Values)
            TryKillProcessTree(process);
    }

    private static string DrainOutput(
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        return string.Join(
            Environment.NewLine,
            new[] { stdout, stderr }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string FormatOutput(string output) =>
        string.IsNullOrWhiteSpace(output)
            ? string.Empty
            : Environment.NewLine + output;

    private static bool TerminateAndWait(Process process)
    {
        TryKillProcessTree(process);
        try
        {
            return process.HasExited ||
                   process.WaitForExit(TerminationWaitMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and the kill request.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The owning Run call reports a failed termination after its wait.
        }
        catch (NotSupportedException)
        {
            // The owning Run call reports a failed termination after its wait.
        }
    }
}

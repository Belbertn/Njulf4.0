using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using Njulf.ShaderBuild;
using NUnit.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Njulf.ShaderBuild.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CompileNjulfShaderArtifactsTests
{
    private string _testDirectory = null!;
    private string _shaderRoot = null!;
    private string _outputDirectory = null!;
    private string _cacheDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _testDirectory = Path.Combine(
            Path.GetTempPath(),
            "njulf-shader-build-tests",
            Guid.NewGuid().ToString("N"));
        _shaderRoot = Path.Combine(_testDirectory, "shaders");
        _outputDirectory = Path.Combine(_testDirectory, "obj");
        _cacheDirectory = Path.Combine(_testDirectory, "cache");
        Directory.CreateDirectory(_shaderRoot);
    }

    [TearDown]
    public void TearDown()
    {
        string fullPath = Path.GetFullPath(_testDirectory);
        string safeRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "njulf-shader-build-tests"));
        if (fullPath.StartsWith(safeRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    [Test]
    public void FingerprintsDependenciesOptionsAndDefinesAndKeepsManifestStable()
    {
        string includePath = Path.Combine(_shaderRoot, "value.glsl");
        string sourcePath = WriteShader(includePath, 1);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath, "-DARTIFACT_VALUE=1");

        CompileNjulfShaderArtifacts first = Run(artifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(first.CompiledCount, Is.EqualTo(1));
        Assert.That(first.CacheHitCount, Is.Zero);
        Assert.That(first.UpToDateCount, Is.Zero);
        AssertValidSpirv(Path.Combine(_outputDirectory, "sample.comp.spv"));
        AssertManifest(first.BundleManifestPath, "sample.comp.spv");

        DateTime manifestWriteTime = File.GetLastWriteTimeUtc(first.BundleManifestPath);
        CompileNjulfShaderArtifacts noOp = Run(artifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(noOp.CompiledCount, Is.Zero);
        Assert.That(noOp.CacheHitCount, Is.Zero);
        Assert.That(noOp.UpToDateCount, Is.EqualTo(1));
        Assert.That(File.GetLastWriteTimeUtc(noOp.BundleManifestPath), Is.EqualTo(manifestWriteTime));

        File.Delete(Path.Combine(_outputDirectory, "sample.comp.spv"));
        CompileNjulfShaderArtifacts restored = Run(artifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(restored.CompiledCount, Is.Zero);
        Assert.That(restored.CacheHitCount, Is.EqualTo(1));

        File.WriteAllText(includePath, "#define INCLUDED_VALUE 2u\n");
        CompileNjulfShaderArtifacts includeChanged = Run(artifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(includeChanged.CompiledCount, Is.EqualTo(1));

        File.AppendAllText(sourcePath, "\n// source fingerprint change\n");
        CompileNjulfShaderArtifacts sourceChanged = Run(artifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(sourceChanged.CompiledCount, Is.EqualTo(1));

        TaskItem defineChangedArtifact = CreateArtifact("sample.comp", sourcePath, "-DARTIFACT_VALUE=2");
        CompileNjulfShaderArtifacts defineChanged = Run(defineChangedArtifact, globalOptions: "-DGLOBAL_VALUE=1");
        Assert.That(defineChanged.CompiledCount, Is.EqualTo(1));

        CompileNjulfShaderArtifacts globalChanged = Run(defineChangedArtifact, globalOptions: "-DGLOBAL_VALUE=2");
        Assert.That(globalChanged.CompiledCount, Is.EqualTo(1));

        defineChangedArtifact.SetMetadata("AdditionalCompileOptions", "-Od");
        CompileNjulfShaderArtifacts additionalOptionsChanged = Run(
            defineChangedArtifact,
            globalOptions: "-DGLOBAL_VALUE=2");
        Assert.That(additionalOptionsChanged.CompiledCount, Is.EqualTo(1));
    }

    [Test]
    public void CorruptOutputsAndCacheObjectsAreRecovered()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 3);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        CompileNjulfShaderArtifacts first = Run(artifact);
        Assert.That(first.CompiledCount, Is.EqualTo(1));

        string outputPath = Path.Combine(_outputDirectory, "sample.comp.spv");
        string cacheObject = Directory.GetFiles(
            Path.Combine(_cacheDirectory, "objects"),
            "*.spv",
            SearchOption.AllDirectories).Single();
        byte[] expected = File.ReadAllBytes(outputPath);

        File.WriteAllBytes(outputPath, [0x03, 0x02, 0x23, 0x07]);
        CompileNjulfShaderArtifacts outputRecovered = Run(artifact);
        Assert.That(outputRecovered.CompiledCount, Is.Zero);
        Assert.That(outputRecovered.CacheHitCount, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(expected));

        File.WriteAllBytes(outputPath, [0x03, 0x02, 0x23, 0x07]);
        File.WriteAllBytes(cacheObject, [0x03, 0x02, 0x23, 0x07]);
        CompileNjulfShaderArtifacts cacheRecovered = Run(artifact);
        Assert.That(cacheRecovered.CompiledCount, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(expected));
        Assert.That(File.ReadAllBytes(cacheObject), Is.EqualTo(expected));

        string localState = Directory.GetFiles(
            Path.Combine(_outputDirectory, ".state"),
            "*.json").Single();
        string cacheIndex = Directory.GetFiles(
            Path.Combine(_cacheDirectory, "index"),
            "*.json").Single();
        File.WriteAllText(localState, "{}");
        File.Delete(outputPath);
        CompileNjulfShaderArtifacts stateFallback = Run(artifact);
        Assert.That(stateFallback.CacheHitCount, Is.EqualTo(1));

        File.WriteAllText(localState, "{}");
        File.WriteAllText(cacheIndex, "{}");
        File.Delete(outputPath);
        CompileNjulfShaderArtifacts stateRecovered = Run(artifact);
        Assert.That(stateRecovered.CompiledCount, Is.EqualTo(1));
        Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(expected));
    }

    [Test]
    public void StructurallyInvalidStateEntriesAreCacheMisses()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 31);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        CompileNjulfShaderArtifacts first = Run(artifact);
        Assert.That(first.CompiledCount, Is.EqualTo(1));

        string outputPath = Path.Combine(_outputDirectory, "sample.comp.spv");
        byte[] expected = File.ReadAllBytes(outputPath);
        string localState = Directory.GetFiles(
            Path.Combine(_outputDirectory, ".state"),
            "*.json").Single();
        string cacheIndex = Directory.GetFiles(
            Path.Combine(_cacheDirectory, "index"),
            "*.json").Single();

        AssertStateRejected(artifact, localState, cacheIndex, outputPath, expected, _ => "{}");
        AssertStateRejected(
            artifact,
            localState,
            cacheIndex,
            outputPath,
            expected,
            json => RewriteState(json, (state, _) => state["contentKey"] = new string('0', 64)));
        AssertStateRejected(
            artifact,
            localState,
            cacheIndex,
            outputPath,
            expected,
            json => RewriteState(json, (_, dependencies) => dependencies[0] = null));

        string outsidePath = Path.Combine(_testDirectory, "outside.glsl");
        File.WriteAllText(outsidePath, "#define OUTSIDE_VALUE 99u\n");
        string outsideHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(outsidePath)));
        string relativeSource = Path.GetRelativePath(_shaderRoot, sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        AssertStateRejected(
            artifact,
            localState,
            cacheIndex,
            outputPath,
            expected,
            json => RewriteState(
                json,
                (_, dependencies) =>
                {
                    JsonObject include = dependencies
                        .Select(node => node!.AsObject())
                        .Single(node => !string.Equals(
                            node["path"]!.GetValue<string>(),
                            relativeSource,
                            StringComparison.OrdinalIgnoreCase));
                    include["path"] = "../outside.glsl";
                    include["hash"] = outsideHash;
                },
                recomputeContentKey: true));
        AssertStateRejected(
            artifact,
            localState,
            cacheIndex,
            outputPath,
            expected,
            json => RewriteState(
                json,
                (_, dependencies) =>
                {
                    JsonNode source = dependencies.Single(node => string.Equals(
                        node!["path"]!.GetValue<string>(),
                        relativeSource,
                        StringComparison.OrdinalIgnoreCase))!;
                    dependencies.Remove(source);
                },
                recomputeContentKey: true));
        AssertStateRejected(
            artifact,
            localState,
            cacheIndex,
            outputPath,
            expected,
            _ => new string(' ', (4 * 1024 * 1024) + 1));
    }

    [Test]
    public void DependencyInvalidationIsLimitedToAffectedArtifacts()
    {
        string sharedInclude = Path.Combine(_shaderRoot, "shared.glsl");
        string leafInclude = Path.Combine(_shaderRoot, "first-value.glsl");
        File.WriteAllText(sharedInclude, "#define SHARED_VALUE 1u\n");
        File.WriteAllText(leafInclude, "#define LEAF_VALUE 2u\n");
        string firstSource = Path.Combine(_shaderRoot, "first.comp");
        string secondSource = Path.Combine(_shaderRoot, "second.comp");
        File.WriteAllText(
            firstSource,
            CreateStorageShader("#include \"shared.glsl\"\n#include \"first-value.glsl\"", "SHARED_VALUE + LEAF_VALUE"));
        File.WriteAllText(
            secondSource,
            CreateStorageShader("#include \"shared.glsl\"", "SHARED_VALUE"));
        ITaskItem[] artifacts =
        [
            CreateArtifact("first.comp", firstSource),
            CreateArtifact("second.comp", secondSource)
        ];

        CompileNjulfShaderArtifacts initial = Run(artifacts);
        Assert.That(initial.CompiledCount, Is.EqualTo(2));

        CompileNjulfShaderArtifacts noOp = Run(artifacts);
        Assert.That(noOp.UpToDateCount, Is.EqualTo(2));

        File.WriteAllText(leafInclude, "#define LEAF_VALUE 3u\n");
        CompileNjulfShaderArtifacts leafChanged = Run(artifacts);
        Assert.Multiple(() =>
        {
            Assert.That(leafChanged.CompiledCount, Is.EqualTo(1));
            Assert.That(leafChanged.UpToDateCount, Is.EqualTo(1));
        });

        File.WriteAllText(sharedInclude, "#define SHARED_VALUE 4u\n");
        CompileNjulfShaderArtifacts sharedChanged = Run(artifacts);
        Assert.That(sharedChanged.CompiledCount, Is.EqualTo(2));
    }

    [Test]
    public void AutomaticParallelismUsesAvailableLogicalProcessorsWithinCap()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 41);
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine);
        task.MaxParallelism = 0;

        Assert.That(task.Execute(), Is.True, engine.FormatErrors());

        int expected = Math.Clamp(Environment.ProcessorCount, 1, 8);
        Assert.That(
            engine.Messages.Select(message => message.Message),
            Has.Some.Contains($"with parallelism {expected}"));
    }

    [Test]
    public void NegativeCompilerTimeoutIsRejectedBeforeCompilerResolution()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 42);
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine,
            compilerPath: "compiler-that-must-not-be-resolved",
            compilerTimeoutSeconds: -1);

        Assert.That(task.Execute(), Is.False);
        Assert.That(
            engine.FormatErrors(),
            Does.Contain("NjulfShaderCompilerTimeoutSeconds cannot be negative"));
        Assert.That(Directory.Exists(_cacheDirectory), Is.False);
    }

    [Test]
    public void UseExistingNeverResolvesCompilerOrCreatesCache()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 4);
        Directory.CreateDirectory(_outputDirectory);
        string outputPath = Path.Combine(_outputDirectory, "sample.comp.spv");
        File.WriteAllBytes(outputPath, [0x03, 0x02, 0x23, 0x07]);
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(sourcePath, DateTime.UtcNow);

        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine,
            compilerPath: "compiler-that-must-not-be-resolved",
            buildMode: "UseExisting");

        Assert.That(task.Execute(), Is.True, engine.FormatErrors());
        Assert.That(task.UpToDateCount, Is.EqualTo(1));
        Assert.That(engine.Warnings, Has.Count.EqualTo(1));
        Assert.That(Directory.Exists(_cacheDirectory), Is.False);
        Assert.That(File.Exists(task.BundleManifestPath), Is.True);

        var missingEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts missingTask = CreateTask(
            [
                CreateArtifact("missing-b.comp", sourcePath),
                CreateArtifact("missing-a.comp", sourcePath)
            ],
            missingEngine,
            compilerPath: "compiler-that-must-not-be-resolved",
            buildMode: "UseExisting");

        Assert.That(missingTask.Execute(), Is.False);
        Assert.That(missingEngine.Errors, Has.Count.EqualTo(1));
        Assert.That(missingEngine.Errors[0].Message, Does.Contain("missing-a.comp.spv"));
        Assert.That(missingEngine.Errors[0].Message, Does.Contain("missing-b.comp.spv"));
        Assert.That(Directory.Exists(_cacheDirectory), Is.False);
    }

    [Test]
    public void CompilerFailureDoesNotReplacePublishedOutputOrLeaveTemporaryFiles()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 5);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        CompileNjulfShaderArtifacts first = Run(artifact);
        Assert.That(first.CompiledCount, Is.EqualTo(1));
        string outputPath = Path.Combine(_outputDirectory, "sample.comp.spv");
        byte[] published = File.ReadAllBytes(outputPath);

        File.WriteAllText(sourcePath, "#version 460\n#error deliberate test failure\n");
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts failed = CreateTask([artifact], engine);

        Assert.That(failed.Execute(), Is.False);
        Assert.That(engine.Errors, Is.Not.Empty);
        Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(published));
        Assert.That(
            Directory.GetFiles(_outputDirectory, ".*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".d", StringComparison.OrdinalIgnoreCase)),
            Is.Empty);
    }

    [Test]
    public void LongHermeticOutputPathUsesBoundedTemporaryNames()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 51);
        const string artifactName =
            "forward_opaque_simple_full_input_ddgi_near_field_direct_source_cache_required.frag";
        string outputDirectory = Path.Combine(_testDirectory, "hermetic-output");
        while (outputDirectory.Length < 160)
            outputDirectory = Path.Combine(outputDirectory, "path-segment");

        string finalOutput = Path.Combine(outputDirectory, artifactName + ".spv");
        string legacyTemporaryOutput = Path.Combine(
            outputDirectory,
            "." + artifactName + "." + new string('0', 32) + ".tmp");
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact(artifactName, sourcePath)],
            engine,
            outputDirectory: outputDirectory);

        Assert.That(finalOutput.Length, Is.LessThan(260));
        Assert.That(legacyTemporaryOutput.Length, Is.GreaterThanOrEqualTo(260));
        Assert.That(task.Execute(), Is.True, engine.FormatErrors());
        AssertValidSpirv(finalOutput);
        Assert.That(
            Directory.GetFiles(outputDirectory, ".njulf-*", SearchOption.TopDirectoryOnly),
            Is.Empty);
    }

    [Test]
    public void DuplicateOutputsAndSourcesOutsideRootAreRejected()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 6);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        var duplicateEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts duplicate = CreateTask([artifact, artifact], duplicateEngine);

        Assert.That(duplicate.Execute(), Is.False);
        Assert.That(duplicateEngine.FormatErrors(), Does.Contain("declared more than once"));

        string outsideSource = Path.Combine(_testDirectory, "outside.comp");
        File.WriteAllText(outsideSource, "#version 460\nvoid main() {}\n");
        var outsideEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts outside = CreateTask(
            [CreateArtifact("outside.comp", outsideSource)],
            outsideEngine);

        Assert.That(outside.Execute(), Is.False);
        Assert.That(outsideEngine.FormatErrors(), Does.Contain("outside"));
    }

    [Test]
    public async Task ConcurrentCacheMissesCompileOnceAndPublishAReusableObject()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 7);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        string firstOutput = Path.Combine(_testDirectory, "obj-a");
        string secondOutput = Path.Combine(_testDirectory, "obj-b");
        var firstEngine = new RecordingBuildEngine();
        var secondEngine = new RecordingBuildEngine();
        var runner = new ControlledCompilerProcessRunner(blockCompilation: true);
        CompileNjulfShaderArtifacts first = CreateTask(
            [artifact],
            firstEngine,
            outputDirectory: firstOutput,
            processRunner: runner);
        CompileNjulfShaderArtifacts second = CreateTask(
            [artifact],
            secondEngine,
            outputDirectory: secondOutput,
            processRunner: runner);

        Task<bool> firstExecution = Task.Run(first.Execute);
        Assert.That(
            runner.CompileStarted.Wait(TimeSpan.FromSeconds(5)),
            Is.True,
            "The first synthetic compiler invocation did not start.");
        Task<bool> secondExecution = Task.Run(second.Execute);
        Assert.That(
            SpinWait.SpinUntil(
                () => secondEngine.ContainsMessage("single-flight waiting"),
                TimeSpan.FromSeconds(5)),
            Is.True,
            "The second task did not reach the shared recipe lock.");

        runner.ReleaseCompilation.Set();
        bool[] results = await Task.WhenAll(firstExecution, secondExecution);

        Assert.That(
            results,
            Is.All.True,
            firstEngine.FormatErrors() + Environment.NewLine + secondEngine.FormatErrors());
        AssertValidSpirv(Path.Combine(firstOutput, "sample.comp.spv"));
        AssertValidSpirv(Path.Combine(secondOutput, "sample.comp.spv"));
        Assert.Multiple(() =>
        {
            Assert.That(runner.CompileCallCount, Is.EqualTo(1));
            Assert.That(first.CompiledCount, Is.EqualTo(1));
            Assert.That(second.CompiledCount, Is.Zero);
            Assert.That(second.CacheHitCount, Is.EqualTo(1));
        });

        string thirdOutput = Path.Combine(_testDirectory, "obj-c");
        CompileNjulfShaderArtifacts third = CreateTask(
            [artifact],
            new RecordingBuildEngine(),
            outputDirectory: thirdOutput,
            processRunner: runner);
        Assert.That(third.Execute(), Is.True);
        Assert.That(third.CompiledCount, Is.Zero);
        Assert.That(third.CacheHitCount, Is.EqualTo(1));
        Assert.That(runner.CompileCallCount, Is.EqualTo(1));
        AssertValidSpirv(Path.Combine(thirdOutput, "sample.comp.spv"));
    }

    [Test]
    public void CompilerTimeoutDoesNotPublishOrLeaveTemporaryFiles()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 8);
        var engine = new RecordingBuildEngine();
        var runner = new ControlledCompilerProcessRunner(
            outcome: ControlledCompileOutcome.Timeout,
            createTemporaryFilesBeforeCompletion: true);
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine,
            compilerTimeoutSeconds: 1,
            processRunner: runner);

        Assert.That(task.Execute(), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(engine.FormatErrors(), Does.Contain("timed out compiling 'sample.comp.spv'"));
            Assert.That(File.Exists(Path.Combine(_outputDirectory, "sample.comp.spv")), Is.False);
            Assert.That(
                Directory.GetFiles(_outputDirectory, ".njulf-*", SearchOption.TopDirectoryOnly),
                Is.Empty);
            Assert.That(
                Directory.GetFiles(Path.Combine(_outputDirectory, ".state"), "*.json"),
                Is.Empty);
            Assert.That(
                Directory.GetFiles(Path.Combine(_cacheDirectory, "index"), "*.json"),
                Is.Empty);
        });
    }

    [Test]
    public async Task CancelStopsCompilationWithoutPublishingPartialState()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 9);
        var engine = new RecordingBuildEngine();
        var runner = new ControlledCompilerProcessRunner(
            blockCompilation: true,
            createTemporaryFilesBeforeCompletion: true);
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine,
            processRunner: runner);

        Task<bool> execution = Task.Run(task.Execute);
        Assert.That(
            runner.CompileStarted.Wait(TimeSpan.FromSeconds(5)),
            Is.True,
            "The synthetic compiler invocation did not start.");
        task.Cancel();

        Assert.That(await execution.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(runner.CancelAllCallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(File.Exists(Path.Combine(_outputDirectory, "sample.comp.spv")), Is.False);
            Assert.That(
                Directory.GetFiles(_outputDirectory, ".njulf-*", SearchOption.TopDirectoryOnly),
                Is.Empty);
            Assert.That(
                Directory.GetFiles(Path.Combine(_outputDirectory, ".state"), "*.json"),
                Is.Empty);
            Assert.That(
                Directory.GetFiles(Path.Combine(_cacheDirectory, "index"), "*.json"),
                Is.Empty);
        });
    }

    [Test]
    public async Task FailedRecipeOwnerReleasesLockForWaitingBuild()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 10);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        var ownerEngine = new RecordingBuildEngine();
        var waiterEngine = new RecordingBuildEngine();
        var ownerRunner = new ControlledCompilerProcessRunner(
            blockCompilation: true,
            outcome: ControlledCompileOutcome.Failure,
            createTemporaryFilesBeforeCompletion: true);
        var waiterRunner = new ControlledCompilerProcessRunner();
        CompileNjulfShaderArtifacts owner = CreateTask(
            [artifact],
            ownerEngine,
            outputDirectory: Path.Combine(_testDirectory, "owner-obj"),
            processRunner: ownerRunner);
        CompileNjulfShaderArtifacts waiter = CreateTask(
            [artifact],
            waiterEngine,
            outputDirectory: Path.Combine(_testDirectory, "waiter-obj"),
            processRunner: waiterRunner);

        Task<bool> ownerExecution = Task.Run(owner.Execute);
        Assert.That(
            ownerRunner.CompileStarted.Wait(TimeSpan.FromSeconds(5)),
            Is.True);
        Task<bool> waiterExecution = Task.Run(waiter.Execute);
        Assert.That(
            SpinWait.SpinUntil(
                () => waiterEngine.ContainsMessage("single-flight waiting"),
                TimeSpan.FromSeconds(5)),
            Is.True);

        ownerRunner.ReleaseCompilation.Set();
        bool[] results = await Task.WhenAll(ownerExecution, waiterExecution);

        Assert.Multiple(() =>
        {
            Assert.That(results[0], Is.False, ownerEngine.FormatErrors());
            Assert.That(results[1], Is.True, waiterEngine.FormatErrors());
            Assert.That(waiter.CompiledCount, Is.EqualTo(1));
            Assert.That(waiterRunner.CompileCallCount, Is.EqualTo(1));
            AssertValidSpirv(Path.Combine(
                _testDirectory,
                "waiter-obj",
                "sample.comp.spv"));
        });
    }

    [Test]
    public void ProcessRunnerTimeoutTerminatesCompleteProcessTree()
    {
        string processIdsPath = Path.Combine(_testDirectory, "timeout-processes.txt");
        ProcessStartInfo startInfo = CreateProcessTreeStartInfo(processIdsPath);
        var runner = new CompilerProcessRunner();
        int heartbeatCount = 0;

        CompilerProcessTimeoutException? exception = Assert.Throws<CompilerProcessTimeoutException>(() =>
            runner.Run(
                startInfo,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None,
                _ => Interlocked.Increment(ref heartbeatCount)));

        Assert.That(exception, Is.Not.Null);
        Assert.That(File.Exists(processIdsPath), Is.True);
        int[] processIds = ReadProcessIds(processIdsPath);
        Assert.Multiple(() =>
        {
            Assert.That(processIds, Has.Length.EqualTo(2));
            Assert.That(processIds[0], Is.EqualTo(exception!.ProcessId));
            Assert.That(heartbeatCount, Is.GreaterThan(0));
            foreach (int processId in processIds)
            {
                Assert.That(
                    WaitForProcessExit(processId),
                    Is.True,
                    $"Process {processId} survived timeout cleanup.");
            }
        });
    }

    [Test]
    public async Task ProcessRunnerCancellationTerminatesCompleteProcessTree()
    {
        string processIdsPath = Path.Combine(_testDirectory, "cancel-processes.txt");
        ProcessStartInfo startInfo = CreateProcessTreeStartInfo(processIdsPath);
        var runner = new CompilerProcessRunner();
        using var cancellation = new CancellationTokenSource();

        Task<CompilerProcessResult> execution = Task.Run(() => runner.Run(
            startInfo,
            timeout: null,
            TimeSpan.Zero,
            cancellation.Token,
            heartbeat: null));
        Assert.That(
            SpinWait.SpinUntil(
                () => File.Exists(processIdsPath),
                TimeSpan.FromSeconds(5)),
            Is.True,
            "The process-tree fixture did not publish its process IDs.");

        cancellation.Cancel();
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("The process runner completed normally after cancellation.");
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation result.
        }

        foreach (int processId in ReadProcessIds(processIdsPath))
        {
            Assert.That(
                WaitForProcessExit(processId),
                Is.True,
                $"Process {processId} survived cancellation cleanup.");
        }
    }

    private static ProcessStartInfo CreateProcessTreeStartInfo(
        string processIdsPath)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            string systemDirectory = Environment.GetFolderPath(
                Environment.SpecialFolder.System);
            string powershell = Path.Combine(
                systemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powershell))
                powershell = "powershell.exe";
            string escapedPath = processIdsPath.Replace("'", "''", StringComparison.Ordinal);
            string script =
                "$child = Start-Process " +
                "-FilePath (Join-Path $env:SystemRoot 'System32\\PING.EXE') " +
                "-ArgumentList @('-t','127.0.0.1') -WindowStyle Hidden -PassThru; " +
                $"[IO.File]::WriteAllLines('{escapedPath}', @([string]$PID, [string]$child.Id)); " +
                "Wait-Process -Id $child.Id";
            start.FileName = powershell;
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(
                Encoding.Unicode.GetBytes(script)));
            return start;
        }

        start.FileName = "/bin/sh";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            "sleep 300 & child=$!; " +
            $"printf '%s\\n%s\\n' $$ $child > {QuotePosix(processIdsPath)}; " +
            "wait $child");
        return start;
    }

    private static string QuotePosix(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static int[] ReadProcessIds(string path) =>
        File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => int.Parse(line.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    private static bool WaitForProcessExit(int processId) =>
        SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    using Process process = Process.GetProcessById(processId);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(5));

    private string WriteShader(string includePath, uint value)
    {
        File.WriteAllText(includePath, $"#define INCLUDED_VALUE {value}u\n");
        string sourcePath = Path.Combine(_shaderRoot, "sample.comp");
        File.WriteAllText(
            sourcePath,
            """
            #version 460
            #extension GL_GOOGLE_include_directive : require
            #include "value.glsl"
            #ifndef ARTIFACT_VALUE
            #define ARTIFACT_VALUE 0
            #endif
            #ifndef GLOBAL_VALUE
            #define GLOBAL_VALUE 0
            #endif
            layout(local_size_x = 1) in;
            layout(set = 0, binding = 0, std430) buffer OutputBuffer { uint value; } outputBuffer;
            void main() { outputBuffer.value = INCLUDED_VALUE + ARTIFACT_VALUE + GLOBAL_VALUE; }
            """);
        return sourcePath;
    }

    private static string CreateStorageShader(string includes, string expression) =>
        $$"""
        #version 460
        #extension GL_GOOGLE_include_directive : require
        {{includes}}
        layout(local_size_x = 1) in;
        layout(set = 0, binding = 0, std430) buffer OutputBuffer { uint value; } outputBuffer;
        void main() { outputBuffer.value = {{expression}}; }
        """;

    private CompileNjulfShaderArtifacts Run(TaskItem artifact, string globalOptions = "")
        => Run([artifact], globalOptions);

    private CompileNjulfShaderArtifacts Run(ITaskItem[] artifacts, string globalOptions = "")
    {
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(artifacts, engine, globalOptions: globalOptions);
        Assert.That(task.Execute(), Is.True, engine.FormatErrors());
        return task;
    }

    private CompileNjulfShaderArtifacts CreateTask(
        ITaskItem[] artifacts,
        RecordingBuildEngine engine,
        string compilerPath = "glslangValidator",
        string buildMode = "Compile",
        string? outputDirectory = null,
        string globalOptions = "",
        int compilerTimeoutSeconds = 900,
        ICompilerProcessRunner? processRunner = null)
    {
        var task = new CompileNjulfShaderArtifacts
        {
            BuildEngine = engine,
            Artifacts = artifacts,
            ShaderRoot = _shaderRoot,
            IntermediateShaderDirectory = outputDirectory ?? _outputDirectory,
            CacheDirectory = _cacheDirectory,
            CompilerPath = compilerPath,
            BuildMode = buildMode,
            CacheMode = "ReadWrite",
            GlobalCompileOptions = globalOptions,
            MaxParallelism = 2,
            CompilerTimeoutSeconds = compilerTimeoutSeconds
        };
        if (processRunner != null)
            task.ProcessRunner = processRunner;
        return task;
    }

    private static TaskItem CreateArtifact(string name, string sourcePath, string defines = "")
    {
        var artifact = new TaskItem(name);
        artifact.SetMetadata("Source", sourcePath);
        artifact.SetMetadata("Defines", defines);
        return artifact;
    }

    private void AssertStateRejected(
        TaskItem artifact,
        string localState,
        string cacheIndex,
        string outputPath,
        byte[] expected,
        Func<string, string> corrupt)
    {
        string corrupted = corrupt(File.ReadAllText(localState));
        File.WriteAllText(localState, corrupted);
        File.WriteAllText(cacheIndex, corrupted);

        CompileNjulfShaderArtifacts recovered = Run(artifact);
        Assert.Multiple(() =>
        {
            Assert.That(recovered.CompiledCount, Is.EqualTo(1));
            Assert.That(recovered.CacheHitCount, Is.Zero);
            Assert.That(recovered.UpToDateCount, Is.Zero);
            Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(expected));
        });
    }

    private static string RewriteState(
        string json,
        Action<JsonObject, JsonArray> rewrite,
        bool recomputeContentKey = false)
    {
        JsonObject state = JsonNode.Parse(json)?.AsObject() ??
            throw new InvalidOperationException("Expected shader state JSON object.");
        JsonArray dependencies = state["dependencies"]?.AsArray() ??
            throw new InvalidOperationException("Expected shader dependency array.");
        rewrite(state, dependencies);
        if (recomputeContentKey)
        {
            string recipeKey = state["recipeKey"]!.GetValue<string>();
            string dependencyText = string.Join(
                "\n",
                dependencies.Select(node =>
                {
                    JsonObject dependency = node!.AsObject();
                    return dependency["path"]!.GetValue<string>() + "=" +
                           dependency["hash"]!.GetValue<string>();
                }));
            state["contentKey"] = Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(recipeKey + "\n" + dependencyText)));
        }
        return state.ToJsonString();
    }

    private static void AssertManifest(string path, string expectedName)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement artifacts = document.RootElement.GetProperty("artifacts");
        Assert.That(artifacts.GetArrayLength(), Is.EqualTo(1));
        Assert.That(artifacts[0].GetProperty("name").GetString(), Is.EqualTo(expectedName));
        Assert.That(artifacts[0].GetProperty("hash").GetString(), Has.Length.EqualTo(64));
    }

    private static void AssertValidSpirv(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Multiple(() =>
        {
            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(sizeof(uint)));
            Assert.That(bytes.Length % sizeof(uint), Is.Zero);
            Assert.That(BitConverter.ToUInt32(bytes), Is.EqualTo(0x07230203));
            Assert.That(Convert.ToHexString(SHA256.HashData(bytes)), Has.Length.EqualTo(64));
        });
    }

    private enum ControlledCompileOutcome
    {
        Success,
        Failure,
        Timeout
    }

    private sealed class ControlledCompilerProcessRunner : ICompilerProcessRunner
    {
        private readonly bool _blockCompilation;
        private readonly ControlledCompileOutcome _outcome;
        private readonly bool _createTemporaryFilesBeforeCompletion;
        private int _compileCallCount;
        private int _cancelAllCallCount;

        public ControlledCompilerProcessRunner(
            bool blockCompilation = false,
            ControlledCompileOutcome outcome = ControlledCompileOutcome.Success,
            bool createTemporaryFilesBeforeCompletion = false)
        {
            _blockCompilation = blockCompilation;
            _outcome = outcome;
            _createTemporaryFilesBeforeCompletion =
                createTemporaryFilesBeforeCompletion;
        }

        public ManualResetEventSlim CompileStarted { get; } = new(false);

        public ManualResetEventSlim ReleaseCompilation { get; } = new(false);

        public int CompileCallCount => Volatile.Read(ref _compileCallCount);

        public int CancelAllCallCount => Volatile.Read(ref _cancelAllCallCount);

        public CompilerProcessResult Run(
            ProcessStartInfo startInfo,
            TimeSpan? timeout,
            TimeSpan heartbeatInterval,
            CancellationToken cancellationToken,
            Action<CompilerProcessHeartbeat>? heartbeat)
        {
            if (startInfo.ArgumentList.Contains("--version"))
            {
                return new CompilerProcessResult(
                    0,
                    "Njulf controlled compiler 1.0",
                    10_000,
                    TimeSpan.Zero);
            }

            int call = Interlocked.Increment(ref _compileCallCount);
            string outputPath = GetArgumentValue(startInfo, "-o");
            string depfilePath = GetArgumentValue(startInfo, "--depfile");
            string sourcePath = startInfo.ArgumentList[^1];
            if (_createTemporaryFilesBeforeCompletion)
            {
                WriteSyntheticCompilerOutputs(
                    outputPath,
                    depfilePath,
                    sourcePath);
            }

            CompileStarted.Set();
            if (_blockCompilation)
                ReleaseCompilation.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            switch (_outcome)
            {
                case ControlledCompileOutcome.Failure:
                    throw new InvalidOperationException(
                        "Controlled compiler failure.");
                case ControlledCompileOutcome.Timeout:
                    throw new CompilerProcessTimeoutException(
                        10_000 + call,
                        timeout ?? TimeSpan.FromSeconds(1),
                        "Controlled compiler timeout.");
            }

            if (!_createTemporaryFilesBeforeCompletion)
            {
                WriteSyntheticCompilerOutputs(
                    outputPath,
                    depfilePath,
                    sourcePath);
            }
            return new CompilerProcessResult(
                0,
                string.Empty,
                10_000 + call,
                TimeSpan.FromMilliseconds(1));
        }

        public void CancelAll()
        {
            Interlocked.Increment(ref _cancelAllCallCount);
            ReleaseCompilation.Set();
        }

        private static string GetArgumentValue(
            ProcessStartInfo startInfo,
            string option)
        {
            int index = startInfo.ArgumentList.IndexOf(option);
            if (index < 0 || index + 1 >= startInfo.ArgumentList.Count)
            {
                throw new InvalidOperationException(
                    $"Controlled compiler invocation did not contain '{option}'.");
            }
            return startInfo.ArgumentList[index + 1];
        }

        private static void WriteSyntheticCompilerOutputs(
            string outputPath,
            string depfilePath,
            string sourcePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(depfilePath)!);
            File.WriteAllBytes(outputPath, [0x03, 0x02, 0x23, 0x07]);
            File.WriteAllText(
                depfilePath,
                $"{Path.GetFileName(outputPath)}: " +
                $"{Path.GetFileName(sourcePath)} value.glsl{Environment.NewLine}");
        }
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        private readonly object _sync = new();

        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public List<BuildMessageEventArgs> Messages { get; } = [];

        public int ColumnNumberOfTaskNode => 0;

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => string.Empty;

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs) => false;

        public void LogCustomEvent(CustomBuildEventArgs eventArgs)
        {
        }

        public void LogErrorEvent(BuildErrorEventArgs eventArgs)
        {
            lock (_sync)
                Errors.Add(eventArgs);
        }

        public void LogMessageEvent(BuildMessageEventArgs eventArgs)
        {
            lock (_sync)
                Messages.Add(eventArgs);
        }

        public void LogWarningEvent(BuildWarningEventArgs eventArgs)
        {
            lock (_sync)
                Warnings.Add(eventArgs);
        }

        public bool ContainsMessage(string text)
        {
            lock (_sync)
            {
                return Messages.Any(message =>
                    message.Message?.Contains(text, StringComparison.Ordinal) == true);
            }
        }

        public string FormatErrors()
        {
            lock (_sync)
            {
                return string.Join(
                    Environment.NewLine,
                    Errors.Select(error => error.Message));
            }
        }
    }
}

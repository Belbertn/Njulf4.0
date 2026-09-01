using System.Collections;
using System.Buffers.Binary;
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
    public void FunctionPreserverMarksOnlyLargeNonEntryFunctions()
    {
        string path = Path.Combine(_testDirectory, "functions.spv");
        var words = new List<uint>
        {
            0x07230203,
            0x00010000,
            0,
            64,
            0,
            Instruction(15, 4), 5, 10, 0
        };
        AddFunction(words, resultId: 10, nopCount: 3);
        AddFunction(words, resultId: 20, nopCount: 1);
        AddFunction(words, resultId: 30, nopCount: 4);
        WriteSpirvWords(path, words);

        int marked = CompileNjulfShaderArtifacts.MarkLargeFunctionsDontInline(
            path,
            instructionThreshold: 5);
        IReadOnlyDictionary<uint, uint> controls = ReadFunctionControls(path);

        Assert.Multiple(() =>
        {
            Assert.That(marked, Is.EqualTo(1));
            Assert.That(controls[10], Is.Zero, "entry points must never be marked");
            Assert.That(controls[20], Is.Zero, "small helpers must remain unchanged");
            Assert.That(controls[30], Is.EqualTo(0x2), "large helpers must be DontInline");
            Assert.That(
                CompileNjulfShaderArtifacts.MarkLargeFunctionsDontInline(path, 5),
                Is.Zero,
                "marking must be idempotent");
        });
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase("4194305")]
    public void InvalidFunctionPreservingThresholdIsRejected(string threshold)
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 52);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        artifact.SetMetadata("FunctionPreservingInstructionThreshold", threshold);
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [artifact],
            engine,
            compilerPath: "compiler-that-must-not-be-resolved",
            optimizerPath: "optimizer-that-must-not-be-resolved");

        Assert.That(task.Execute(), Is.False);
        Assert.That(engine.FormatErrors(), Does.Contain("FunctionPreservingInstructionThreshold"));
    }

    [Test]
    public void OrdinaryArtifactsNeverResolveOptimizer()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 53);
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts task = CreateTask(
            [CreateArtifact("sample.comp", sourcePath)],
            engine,
            optimizerPath: "optimizer-that-must-not-be-resolved");

        Assert.That(task.Execute(), Is.True, engine.FormatErrors());
        Assert.That(task.CompiledCount, Is.EqualTo(1));
    }

    [Test]
    public void MissingOptimizerFailsClearlyWithoutReplacingPublishedOutput()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 54);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        CompileNjulfShaderArtifacts first = Run(artifact);
        string outputPath = Path.Combine(_outputDirectory, "sample.comp.spv");
        byte[] published = File.ReadAllBytes(outputPath);

        artifact.SetMetadata("AdditionalCompileOptions", "-Od");
        artifact.SetMetadata("FunctionPreservingInstructionThreshold", "2");
        var engine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts failed = CreateTask(
            [artifact],
            engine,
            optimizerPath: Path.Combine(_testDirectory, "missing-spirv-opt.exe"));

        Assert.Multiple(() =>
        {
            Assert.That(failed.Execute(), Is.False);
            Assert.That(engine.FormatErrors(), Does.Contain("SPIR-V optimizer"));
            Assert.That(engine.FormatErrors(), Does.Contain("NjulfSpirvOptimizer"));
            Assert.That(File.ReadAllBytes(outputPath), Is.EqualTo(published));
            Assert.That(
                Directory.GetFiles(_outputDirectory, ".njulf-*", SearchOption.TopDirectoryOnly),
                Is.Empty);
        });
    }

    [Test]
    public void OptimizedArtifactRetainsFunctionsAndThresholdInvalidatesRecipe()
    {
        string sourcePath = WriteMultiFunctionShader();
        TaskItem artifact = CreateArtifact("functions.comp", sourcePath);
        artifact.SetMetadata("AdditionalCompileOptions", "-Od");
        artifact.SetMetadata("FunctionPreservingInstructionThreshold", "2");

        CompileNjulfShaderArtifacts first = Run(artifact);
        string outputPath = Path.Combine(_outputDirectory, "functions.comp.spv");
        Assert.That(CountInstructions(outputPath, opcode: 54), Is.GreaterThan(1));

        CompileNjulfShaderArtifacts noOp = Run(artifact);
        Assert.That(noOp.UpToDateCount, Is.EqualTo(1));

        artifact.SetMetadata("FunctionPreservingInstructionThreshold", "3");
        CompileNjulfShaderArtifacts thresholdChanged = Run(artifact);
        Assert.That(thresholdChanged.CompiledCount, Is.EqualTo(1));
    }

    [Test]
    public void OptimizerBinaryFingerprintInvalidatesRecipe()
    {
        string installedOptimizer = ResolveExecutableForTest("spirv-opt");
        string copiedOptimizer = Path.Combine(
            _testDirectory,
            "copied-spirv-opt" + Path.GetExtension(installedOptimizer));
        File.Copy(installedOptimizer, copiedOptimizer);

        string sourcePath = WriteMultiFunctionShader();
        TaskItem artifact = CreateArtifact("functions.comp", sourcePath);
        artifact.SetMetadata("AdditionalCompileOptions", "-Od");
        artifact.SetMetadata("FunctionPreservingInstructionThreshold", "2");
        var firstEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts first = CreateTask(
            [artifact],
            firstEngine,
            optimizerPath: copiedOptimizer);
        Assert.That(first.Execute(), Is.True, firstEngine.FormatErrors());

        var noOpEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts noOp = CreateTask(
            [artifact],
            noOpEngine,
            optimizerPath: copiedOptimizer);
        Assert.That(noOp.Execute(), Is.True, noOpEngine.FormatErrors());
        Assert.That(noOp.UpToDateCount, Is.EqualTo(1));

        using (FileStream stream = new(copiedOptimizer, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.WriteByte(0);

        var changedEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts changed = CreateTask(
            [artifact],
            changedEngine,
            optimizerPath: copiedOptimizer);
        Assert.That(changed.Execute(), Is.True, changedEngine.FormatErrors());
        Assert.That(changed.CompiledCount, Is.EqualTo(1));
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
    public async Task ConcurrentCacheWritersPublishAReusableObject()
    {
        string sourcePath = WriteShader(Path.Combine(_shaderRoot, "value.glsl"), 7);
        TaskItem artifact = CreateArtifact("sample.comp", sourcePath);
        string firstOutput = Path.Combine(_testDirectory, "obj-a");
        string secondOutput = Path.Combine(_testDirectory, "obj-b");
        var firstEngine = new RecordingBuildEngine();
        var secondEngine = new RecordingBuildEngine();
        CompileNjulfShaderArtifacts first = CreateTask(
            [artifact],
            firstEngine,
            outputDirectory: firstOutput);
        CompileNjulfShaderArtifacts second = CreateTask(
            [artifact],
            secondEngine,
            outputDirectory: secondOutput);

        bool[] results = await Task.WhenAll(
            Task.Run(first.Execute),
            Task.Run(second.Execute));

        Assert.That(
            results,
            Is.All.True,
            firstEngine.FormatErrors() + Environment.NewLine + secondEngine.FormatErrors());
        AssertValidSpirv(Path.Combine(firstOutput, "sample.comp.spv"));
        AssertValidSpirv(Path.Combine(secondOutput, "sample.comp.spv"));

        string thirdOutput = Path.Combine(_testDirectory, "obj-c");
        CompileNjulfShaderArtifacts third = CreateTask(
            [artifact],
            new RecordingBuildEngine(),
            outputDirectory: thirdOutput);
        Assert.That(third.Execute(), Is.True);
        Assert.That(third.CompiledCount, Is.Zero);
        Assert.That(third.CacheHitCount, Is.EqualTo(1));
        AssertValidSpirv(Path.Combine(thirdOutput, "sample.comp.spv"));
    }

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

    private string WriteMultiFunctionShader()
    {
        string sourcePath = Path.Combine(_shaderRoot, "functions.comp");
        File.WriteAllText(
            sourcePath,
            """
            #version 460
            layout(local_size_x = 1) in;
            layout(set = 0, binding = 0, std430) buffer OutputBuffer { uint value; } outputBuffer;
            uint mixValue(uint value) { return (value * 1664525u) + 1013904223u; }
            void main() { outputBuffer.value = mixValue(outputBuffer.value); }
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
        string optimizerPath = "spirv-opt",
        string buildMode = "Compile",
        string? outputDirectory = null,
        string globalOptions = "") =>
        new()
        {
            BuildEngine = engine,
            Artifacts = artifacts,
            ShaderRoot = _shaderRoot,
            IntermediateShaderDirectory = outputDirectory ?? _outputDirectory,
            CacheDirectory = _cacheDirectory,
            CompilerPath = compilerPath,
            OptimizerPath = optimizerPath,
            BuildMode = buildMode,
            CacheMode = "ReadWrite",
            GlobalCompileOptions = globalOptions,
            MaxParallelism = 2
        };

    private static TaskItem CreateArtifact(string name, string sourcePath, string defines = "")
    {
        var artifact = new TaskItem(name);
        artifact.SetMetadata("Source", sourcePath);
        artifact.SetMetadata("Defines", defines);
        return artifact;
    }

    private static uint Instruction(ushort opcode, ushort wordCount) =>
        ((uint)wordCount << 16) | opcode;

    private static void AddFunction(List<uint> words, uint resultId, int nopCount)
    {
        words.Add(Instruction(54, 5));
        words.Add(1);
        words.Add(resultId);
        words.Add(0);
        words.Add(2);
        for (int index = 0; index < nopCount; index++)
            words.Add(Instruction(0, 1));
        words.Add(Instruction(56, 1));
    }

    private static void WriteSpirvWords(string path, IReadOnlyList<uint> words)
    {
        byte[] bytes = new byte[words.Count * sizeof(uint)];
        for (int index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                words[index]);
        }
        File.WriteAllBytes(path, bytes);
    }

    private static IReadOnlyDictionary<uint, uint> ReadFunctionControls(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var controls = new Dictionary<uint, uint>();
        for (int offset = 5; offset < bytes.Length / sizeof(uint);)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset * sizeof(uint), sizeof(uint)));
            int wordCount = (int)(instruction >> 16);
            ushort opcode = (ushort)instruction;
            if (opcode == 54)
            {
                uint resultId = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((offset + 2) * sizeof(uint), sizeof(uint)));
                uint control = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan((offset + 3) * sizeof(uint), sizeof(uint)));
                controls.Add(resultId, control);
            }
            offset += wordCount;
        }
        return controls;
    }

    private static int CountInstructions(string path, ushort opcode)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int count = 0;
        for (int offset = 5; offset < bytes.Length / sizeof(uint);)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset * sizeof(uint), sizeof(uint)));
            if ((ushort)instruction == opcode)
                count++;
            offset += (int)(instruction >> 16);
        }
        return count;
    }

    private static string ResolveExecutableForTest(string name)
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [name + ".exe", name]
            : [name];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidate in candidates)
            {
                string path = Path.Combine(directory.Trim(), candidate);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
        }
        throw new FileNotFoundException($"Could not locate test tool '{name}' on PATH.");
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

    private sealed class RecordingBuildEngine : IBuildEngine
    {
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

        public void LogErrorEvent(BuildErrorEventArgs eventArgs) => Errors.Add(eventArgs);

        public void LogMessageEvent(BuildMessageEventArgs eventArgs) => Messages.Add(eventArgs);

        public void LogWarningEvent(BuildWarningEventArgs eventArgs) => Warnings.Add(eventArgs);

        public string FormatErrors() => string.Join(Environment.NewLine, Errors.Select(error => error.Message));
    }
}

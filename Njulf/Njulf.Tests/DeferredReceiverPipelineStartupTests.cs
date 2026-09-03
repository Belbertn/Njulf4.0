using System.Xml.Linq;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DeferredReceiverPipelineStartupTests
{
    [Test]
    public void ExactAttributionArtifactsUseFunctionPreservingOptimization()
    {
        string projectPath = FindRepoFile(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement[] exactVariants = project.Descendants()
            .Where(element =>
            {
                string defines = element.Element("Defines")?.Value ??
                    string.Empty;
                return defines.Contains(
                           "NJULF_SIMPLE_DDGI_EXACT_FEEDBACK_ATTRIBUTION=1",
                           StringComparison.Ordinal) ||
                       defines.Contains(
                           "NJULF_SIMPLE_DDGI_FOLIAGE_FRAGMENT_FEEDBACK=1",
                           StringComparison.Ordinal);
            })
            .ToArray();

        Assert.That(exactVariants, Has.Length.GreaterThanOrEqualTo(20));
        Assert.Multiple(() =>
        {
            Assert.That(
                project.Descendants("NjulfReceiverAttributionOptimizationOptions")
                    .Single().Value,
                Is.EqualTo("-Od"));
            Assert.That(
                project.Descendants("NjulfForwardRayQueryOptimizationOptions")
                    .Single().Value,
                Is.EqualTo("-Od"));
            foreach (XElement variant in exactVariants)
            {
                string include = variant.Attribute("Include")?.Value ??
                    variant.Name.LocalName;
                string options =
                    variant.Element("AdditionalCompileOptions")?.Value ??
                    string.Empty;
                Assert.That(
                    options,
                    Does.Contain("OptimizationOptions"),
                    $"Exact-attribution artifact '{include}' lost its trailing -Od recipe.");
            }
        });
    }

    [TestCase("ddgi_simple_receiver_cache_b1.comp.spv")]
    [TestCase("ddgi_simple_receiver_cache_adaptive_b1.comp.spv")]
    [TestCase("ddgi_simple_receiver_cache_adaptive_b1_missing.comp.spv")]
    [TestCase("forward_opaque_ddgi_b1.frag.spv")]
    [TestCase("forward_transparent_ddgi_b1.frag.spv")]
    [TestCase("forward_weighted_oit_ddgi_b1.frag.spv")]
    [TestCase("foliage_forward_ddgi_b1.frag.spv")]
    [TestCase("foliage_grass_b1_compacted.mesh.spv")]
    [TestCase("foliage_mesh_b1_compacted.mesh.spv")]
    [TestCase("fog_b1.comp.spv")]
    [TestCase("particle_b1.vert.spv")]
    public void ExactAttributionArtifactRetainsMultipleFunctions(string artifact)
    {
        string path = FindShaderArtifact(artifact);
        uint[] words = File.ReadAllBytes(path)
            .Chunk(sizeof(uint))
            .Select(bytes => BitConverter.ToUInt32(bytes))
            .ToArray();
        Assert.That(words, Has.Length.GreaterThan(5));
        Assert.That(words[0], Is.EqualTo(0x07230203u), "SPIR-V magic");

        const uint opFunction = 54u;
        int functionCount = 0;
        for (int index = 5; index < words.Length;)
        {
            uint instruction = words[index];
            int wordCount = checked((int)(instruction >> 16));
            Assert.That(wordCount, Is.GreaterThan(0), $"Malformed SPIR-V at word {index}.");
            if ((instruction & 0xffffu) == opFunction)
                functionCount++;
            index = checked(index + wordCount);
        }

        Assert.That(
            functionCount,
            Is.GreaterThan(1),
            $"{artifact} was flattened into one pathological native function.");
    }

    [Test]
    public void ReceiverFeedbackCommandRecordingCannotCreatePipelines()
    {
        string forward = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string adaptive = ReadRepoText(
            "Njulf.Rendering", "Pipeline",
            "ForwardPlusPass.AdaptiveReceiverCache.cs");
        string fog = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "FogPass.cs");
        string sorted = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "TransparentForwardPass.cs");
        string weighted = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "WeightedTransparentPass.cs");
        string feedbackRuntime = ReadRepoText(
            "Njulf.Rendering", "Resources",
            "SimpleDdgiReceiverFeedbackVulkanRuntime.cs");

        string beginCapture = ExtractMethod(
            forward,
            "private bool TryBeginSimpleDdgiReceiverFeedbackCapture(");
        string adaptiveDispatch = ExtractMethod(
            adaptive,
            "private bool DispatchSimpleDdgiReceiverCacheAdaptive(");
        string adaptiveSeed = ExtractMethod(
            adaptive,
            "private bool SeedSimpleDdgiReceiverCacheAdaptiveHistory(");
        string fogExecute = ExtractMethod(
            fog,
            "public override void Execute(");
        string sortedFeedbackSelection = ExtractMethod(
            sorted,
            "private bool TrySelectExactFeedbackPipeline(");
        string weightedFeedbackSelection = ExtractMethod(
            weighted,
            "private bool TrySelectExactFeedbackPipeline(");
        string sortedExecute = ExtractMethod(
            sorted,
            "public override void Execute(");
        string weightedExecute = ExtractMethod(
            weighted,
            "public override void Execute(");
        string configureFeedbackRuntime = ExtractMethod(
            feedbackRuntime,
            "private bool TryConfigureCore(");

        Assert.Multiple(() =>
        {
            foreach (string body in new[]
                     {
                         beginCapture,
                         adaptiveDispatch,
                         adaptiveSeed,
                         fogExecute,
                         sortedFeedbackSelection,
                         weightedFeedbackSelection
                     })
            {
                Assert.That(body, Does.Not.Contain("CreateComputePipeline"));
                Assert.That(body, Does.Not.Contain("CreateGraphicsPipeline"));
                Assert.That(body, Does.Not.Contain("EnsureSimpleDdgi"));
                Assert.That(body, Does.Not.Contain("PrepareReceiverFeedback"));
                Assert.That(body, Does.Not.Contain("PreparePipelines()"));
            }

            Assert.That(beginCapture, Does.Contain("pipelineBank is null"));
            Assert.That(adaptiveDispatch,
                Does.Contain("IsSimpleDdgiReceiverCacheAdaptiveReady(pipelineBank)"));
            Assert.That(adaptiveSeed,
                Does.Contain("IsSimpleDdgiReceiverCacheAdaptiveReady(pipelineBank)"));
            Assert.That(sortedExecute, Does.Not.Contain("TryEnsure"));
            Assert.That(weightedExecute, Does.Not.Contain("TryEnsure"));
            Assert.That(configureFeedbackRuntime,
                Does.Not.Contain("EnsurePipelinesNoLock"));
            Assert.That(configureFeedbackRuntime,
                Does.Not.Contain("new SimpleDdgiReceiverFeedbackGpuPass"));
        });
    }

    [Test]
    public void ReceiverPipelineBankPublishesOnlyWhenCompleteAndDrainsAtShutdown()
    {
        string forward = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "ForwardPlusPass.cs");
        string renderer = ReadRepoText(
            "Njulf.Rendering", "VulkanRenderer.cs");
        string disposal = ExtractMethod(
            renderer,
            "private StagedDisposalPlan CreateResourceDisposalPlan()");
        string preparation = ExtractMethod(
            forward,
            "internal bool PrepareSimpleDdgiReceiverPipelineBank(");
        string cleanup = ExtractMethod(
            forward,
            "private void CleanupSimpleDdgiReceiverCache()");
        string execute = ExtractMethod(
            forward,
            "private void ExecuteInternal(");

        int completenessCheck = preparation.IndexOf(
            "bank.IsComplete(", StringComparison.Ordinal);
        int publish = preparation.IndexOf(
            "ref _simpleDdgiReceiverPipelineBank,",
            completenessCheck,
            StringComparison.Ordinal);
        int disposeGate = cleanup.IndexOf(
            "_simpleDdgiReceiverPipelineBankDisposing = true;",
            StringComparison.Ordinal);
        int unpublish = cleanup.IndexOf(
            "ref _simpleDdgiReceiverPipelineBank,",
            disposeGate,
            StringComparison.Ordinal);
        int destroy = cleanup.IndexOf(
            "CleanupSimpleDdgiReceiverCacheAdaptive(",
            unpublish,
            StringComparison.Ordinal);
        int cancel = disposal.IndexOf(
            ".CancelPending();",
            StringComparison.Ordinal);
        int drain = disposal.IndexOf(
            "CompilationScheduler\n                                .WaitForAll();",
            StringComparison.Ordinal);
        if (drain < 0)
        {
            drain = disposal.IndexOf(
                "CompilationScheduler\r\n                                .WaitForAll();",
                StringComparison.Ordinal);
        }
        int deviceIdle = disposal.IndexOf(
            "_context.Api.DeviceWaitIdle(", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(completenessCheck, Is.GreaterThanOrEqualTo(0));
            Assert.That(publish, Is.GreaterThan(completenessCheck));
            Assert.That(preparation,
                Does.Contain("DestroySimpleDdgiReceiverPipelineBank(bank);"));
            Assert.That(preparation,
                Does.Contain("_simpleDdgiReceiverPipelineBankDisposing"));
            Assert.That(preparation,
                Does.Contain("if (receiverFeedbackRequired)"));
            Assert.That(preparation,
                Does.Contain("if (requiresAdaptive)"));
            Assert.That(disposeGate, Is.GreaterThanOrEqualTo(0));
            Assert.That(unpublish, Is.GreaterThan(disposeGate));
            Assert.That(destroy, Is.GreaterThan(unpublish));
            Assert.That(execute,
                Does.Contain("Volatile.Read(ref _simpleDdgiReceiverPipelineBank)"));
            Assert.That(execute,
                Does.Contain("receiverPipelineBank is not null"));
            Assert.That(cancel, Is.GreaterThanOrEqualTo(0));
            Assert.That(drain, Is.GreaterThan(cancel));
            Assert.That(drain, Is.GreaterThanOrEqualTo(0));
            Assert.That(deviceIdle, Is.GreaterThan(drain));
        });
    }

    private static string ExtractMethod(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.That(signatureStart, Is.GreaterThanOrEqualTo(0), signature);
        int bodyStart = source.IndexOf('{', signatureStart);
        Assert.That(bodyStart, Is.GreaterThan(signatureStart), signature);
        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[bodyStart..(index + 1)];
                    break;
            }
        }

        Assert.Fail($"Could not find the end of method '{signature}'.");
        return string.Empty;
    }

    private static string FindShaderArtifact(string artifact)
    {
        string root = FindRepositoryRoot();
        string objectRoot = Path.Combine(root, "Njulf.Shaders", "obj");
        string[] matches = Directory.GetFiles(
            objectRoot,
            artifact,
            SearchOption.AllDirectories);
        Assert.That(matches, Is.Not.Empty, artifact);

        string? configuration = new DirectoryInfo(
            TestContext.CurrentContext.TestDirectory).Parent?.Name;
        return matches.FirstOrDefault(path =>
                   configuration != null &&
                   path.Contains(
                       $"{Path.DirectorySeparatorChar}{configuration}{Path.DirectorySeparatorChar}",
                       StringComparison.OrdinalIgnoreCase)) ??
               matches[0];
    }

    private static string ReadRepoText(params string[] segments) =>
        File.ReadAllText(FindRepoFile(segments));

    private static string FindRepoFile(params string[] segments)
    {
        string path = FindRepositoryRoot();
        foreach (string segment in segments)
            path = Path.Combine(path, segment);
        if (File.Exists(path))
            return path;
        throw new FileNotFoundException($"Could not locate '{path}'.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(
            TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

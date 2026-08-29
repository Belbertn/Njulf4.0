using System.Text.RegularExpressions;
using System.Xml.Linq;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BuildConfigurationContractTests
{
    [Test]
    public void RendererStartupPolicyUsesActiveSceneAndReportingDefaults()
    {
        Assert.That(RendererBuildConfiguration.FastPipelineStartup, Is.True);
        Assert.That(
            RendererBuildConfiguration.PipelineStartupMode,
            Is.EqualTo(RendererPipelineStartupMode.ActiveScene));
        Assert.That(
            RendererBuildConfiguration.EnforceStartupLatencyByDefault,
            Is.False);
        Assert.That(
            RendererBuildConfiguration.ResolveStartupLatencyGateMode(
                requested: null,
                enforceByDefault:
                    RendererBuildConfiguration.EnforceStartupLatencyByDefault),
            Is.EqualTo(RendererStartupLatencyGateMode.TimingOnly));
    }

    [TestCase(null, false, 1)]
    [TestCase(null, true, 2)]
    [TestCase("off", true, 0)]
    [TestCase("timing", true, 1)]
    [TestCase("enforce", false, 2)]
    public void StartupLatencyGateModeSupportsTierDefaultsAndExplicitOverrides(
        string? requested,
        bool enforceByDefault,
        int expected)
    {
        Assert.That(
            RendererBuildConfiguration.ResolveStartupLatencyGateMode(
                requested,
                enforceByDefault),
            Is.EqualTo((RendererStartupLatencyGateMode)expected));
    }

    [TestCase(null, 0)]
    [TestCase("auto", 0)]
    [TestCase("off", 1)]
    [TestCase("disabled", 1)]
    [TestCase("require", 2)]
    [TestCase("verify", 2)]
    public void PipelineBinaryCacheModeSupportsDeploymentAndVerification(
        string? requested,
        int expected)
    {
        Assert.That(
            RendererBuildConfiguration.ResolvePipelineBinaryCacheMode(
                requested),
            Is.EqualTo((RendererPipelineBinaryCacheMode)expected));
    }

    [Test]
    public void InvalidPipelineBinaryCacheMode_IsRejected()
    {
        Assert.That(
            () => RendererBuildConfiguration.ResolvePipelineBinaryCacheMode(
                "sometimes"),
            Throws.InvalidOperationException);
    }

    [Test]
    public void PerformanceCaptureReportsTheActualBuildTier()
    {
        PerformanceCaptureStartupIdentity identity =
            new PerformanceCaptureHostIdentityResolver(
                    typeof(BuildConfigurationContractTests).Assembly,
                    typeof(RendererBuildConfiguration).Assembly)
                .ResolveStartupIdentity();

#if NJULF_SHIPPING_PERFORMANCE
        const string expected = "ShippingPerformance";
#elif NJULF_PROFILE_SYMBOLS
        const string expected = "ProfileSymbols";
#elif NJULF_DETAILED_INVESTIGATION
        const string expected = "DetailedInvestigation";
#elif NJULF_DEVELOPMENT
        const string expected = "Development";
#elif DEBUG
        const string expected = "Debug";
#else
        const string expected = "Release";
#endif

        Assert.That(identity.CompileConfiguration, Is.EqualTo(expected));
    }

    [Test]
    public void FastStartupDefersRayAndReceiverFeedbackFamiliesBehindFirstUseGates()
    {
        string root = FindRepositoryRoot();
        string meshPipeline = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "MeshPipeline.cs"));
        string forwardPass = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs"));
        string transparentPass = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "TransparentForwardPass.cs"));
        string weightedPass = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "WeightedTransparentPass.cs"));

        string rayAdmission = SliceBetween(
            meshPipeline,
            "private void AdmitRayTransparentPipelines()",
            "internal bool TryEnsureRayTransparentPipelines()");
        string feedbackAdmission = SliceBetween(
            meshPipeline,
            "private void CreateReceiverFeedbackPipelines(",
            "internal bool TryEnsureAlphaMaskReceiverFeedbackPipelines()");
        string transparentProperty = SliceBetween(
            meshPipeline,
            "public VkPipeline TransparentForwardPipeline",
            "public VkPipeline ThinGlassForwardPipeline");
        string pipelineCreation = SliceBetween(
            meshPipeline,
            "private void CreatePipelines(Format colorFormat, Format depthFormat)",
            "private void EnsureTransparentForwardPipeline()");

        Assert.Multiple(() =>
        {
            Assert.That(transparentProperty,
                Does.Contain("EnsureTransparentForwardPipeline();"));
            Assert.That(pipelineCreation,
                Does.Match(
                    "if \\(!RendererBuildConfiguration\\.FastPipelineStartup\\)\\s+\\{[\\s\\S]*?EnsureTransparentForwardPipeline\\(\\);[\\s\\S]*?AdmitRayTransparentPipelines\\(\\);"));
            Assert.That(rayAdmission,
                Does.Match(
                    "if \\(RendererBuildConfiguration\\.FastPipelineStartup\\)\\s+return;\\s+\\r?\\n?\\s*if \\(TryEnsureRayTransparentPipelines\\(\\)"));
            Assert.That(feedbackAdmission,
                Does.Match(
                    "if \\(RendererBuildConfiguration\\.FastPipelineStartup\\)\\s+\\{[\\s\\S]*?return;[\\s\\S]*?CreateOpaqueSpecializedPipelineSet\\("));
            Assert.That(meshPipeline,
                Does.Contain("DeferredPipelineState.Failed"));
            Assert.That(forwardPass,
                Does.Contain("TryEnsureAlphaMaskReceiverFeedbackPipelines()"));
            Assert.That(transparentPass,
                Does.Contain("TryEnsureRayTransparentPipelines()"));
            Assert.That(transparentPass,
                Does.Contain("TryEnsureTransparentReceiverFeedbackPipeline("));
            Assert.That(weightedPass,
                Does.Contain("TryEnsureRayWeightedOitTransparentPipeline()"));
            Assert.That(weightedPass,
                Does.Contain("TryEnsureWeightedOitReceiverFeedbackPipeline()"));
            Assert.That(meshPipeline,
                Does.Contain("PrepareScenePipelineManifest("));
        });
    }

    [Test]
    public void Development_IsAnOptimizedEditorConfigurationWithoutDetailedGpuCounters()
    {
        string root = FindRepositoryRoot();
        XDocument buildProps = XDocument.Load(
            Path.Combine(root, "Directory.Build.props"));
        XElement[] propertyGroups = buildProps.Descendants("PropertyGroup").ToArray();
        string[] configurations = buildProps.Descendants("Configurations")
            .Single()
            .Value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        XElement developmentGroup = propertyGroups.Single(group =>
            string.Equals(
                (string?)group.Attribute("Condition"),
                "'$(Configuration)' == 'Development'",
                StringComparison.Ordinal));
        XElement optimizedGroup = propertyGroups.Single(group =>
            string.Equals(
                group.Element("Optimize")?.Value.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase) &&
            ((string?)group.Attribute("Condition"))?.Contains(
                "'$(Configuration)' == 'Development'",
                StringComparison.Ordinal) == true);

        XDocument shaderProject = XDocument.Load(
            Path.Combine(root, "Njulf.Shaders", "Njulf.Shaders.csproj"));
        XElement shaderOptimization = shaderProject
            .Descendants("NjulfShaderOptimizationOptions")
            .Single();
        XElement detailedCounters = shaderProject
            .Descendants("NjulfShaderDetailedDiagnosticsOptions")
            .Single(element => element.Value.Trim().EndsWith("=1", StringComparison.Ordinal));
        XElement visualDebugViews = shaderProject
            .Descendants("NjulfShaderVisualDebugOptions")
            .Single(element => element.Value.Trim().EndsWith("=1", StringComparison.Ordinal));
        XElement shadowDetailedCounters = shaderProject
            .Descendants("NjulfDirectionalShadowDetailedCountersOptions")
            .Single(element => element.Value.Trim().EndsWith("=1", StringComparison.Ordinal));

        XDocument helloGame = XDocument.Load(
            Path.Combine(root, "NjulfHelloGame", "NjulfHelloGame.csproj"));
        XElement cookedAssetsOnly = helloGame.Descendants("CookedAssetsOnly").Single();
        XElement editorReference = helloGame.Descendants("ProjectReference").Single(element =>
            string.Equals(
                (string?)element.Attribute("Include"),
                "..\\Njulf.Editor\\Njulf.Editor.csproj",
                StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(configurations, Does.Contain("Development"));
            Assert.That(optimizedGroup.Element("DebugType")?.Value.Trim(), Is.EqualTo("portable"));
            Assert.That(optimizedGroup.Element("DebugSymbols")?.Value.Trim(), Is.EqualTo("true"));
            Assert.That(
                developmentGroup.Element("DefineConstants")?.Value,
                Does.Contain("NJULF_DEVELOPMENT"));
            Assert.That(shaderOptimization.Value.Trim(), Is.EqualTo("-Os"));
            Assert.That(
                (string?)shaderOptimization.Attribute("Condition"),
                Does.Contain("'$(Configuration)' == 'Development'"));
            Assert.That(
                (string?)detailedCounters.Attribute("Condition"),
                Does.Not.Contain("Development"));
            Assert.That(
                (string?)visualDebugViews.Attribute("Condition"),
                Does.Contain("'$(Configuration)' == 'Development'"));
            Assert.That(
                (string?)shadowDetailedCounters.Attribute("Condition"),
                Does.Not.Contain("Development"));
            Assert.That(
                (string?)cookedAssetsOnly.Attribute("Condition"),
                Does.Not.Contain("Development"));
            Assert.That(
                (string?)editorReference.Attribute("Condition"),
                Is.EqualTo("'$(CookedAssetsOnly)' != 'true'"));
        });
    }

    [Test]
    public void Vulkan13PipelineCacheControlUsesOnlyTheCoreFeatureCarrier()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Njulf.Rendering",
            "Core",
            "VulkanContext.cs"));
        string querySupport = SliceBetween(
            source,
            "QueryPipelineOptimizationDeviceSupport(PhysicalDevice device)",
            "private readonly record struct FragmentShadingRateDeviceSupport");
        string createDevice = SliceBetween(
            source,
            "private void CreateLogicalDevice()",
            "private void CreateAllocator()");

        Assert.Multiple(() =>
        {
            Assert.That(querySupport,
                Does.Contain("new PhysicalDeviceVulkan13Features"));
            Assert.That(querySupport,
                Does.Contain("vulkan13Features.PipelineCreationCacheControl"));
            Assert.That(createDevice,
                Does.Match(
                    "PhysicalDeviceVulkan13Features[\\s\\S]*?" +
                    "PipelineCreationCacheControl\\s*=\\s*" +
                    "enablePipelineCreationCacheControl"));
            Assert.That(source,
                Does.Not.Contain(
                    "PhysicalDevicePipelineCreationCacheControlFeatures"));
            Assert.That(source,
                Does.Not.Contain(
                    "PipelineCreationCacheControlExtensionName"));
        });
    }

    [Test]
    public void SolutionMapsEveryProjectToDevelopment()
    {
        string solution = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Njulf.sln"));
        string[] projectIds = Regex.Matches(
                solution,
                "^Project\\(.*?\\) = .*?, .*?, \\\"(?<id>\\{[0-9A-F-]+\\})\\\"\\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["id"].Value)
            .ToArray();

        Assert.That(solution, Does.Contain("Development|Any CPU = Development|Any CPU"));
        Assert.That(projectIds, Has.Length.EqualTo(11));
        Assert.Multiple(() =>
        {
            foreach (string projectId in projectIds)
            {
                Assert.That(
                    solution,
                    Does.Contain($"{projectId}.Development|Any CPU.ActiveCfg = Development|Any CPU"));
                Assert.That(
                    solution,
                    Does.Contain($"{projectId}.Development|Any CPU.Build.0 = Development|Any CPU"));
            }
        });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Njulf repository root from the test directory.");
    }

    private static string SliceBetween(
        string source,
        string startMarker,
        string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), startMarker);
        Assert.That(end, Is.GreaterThan(start), endMarker);
        return source[start..end];
    }
}

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BuildConfigurationContractTests
{
    [Test]
    public void RendererStartupPolicyMatchesBuildTier()
    {
#if NJULF_DEVELOPMENT
        Assert.That(RendererBuildConfiguration.FastPipelineStartup, Is.True);
#else
        Assert.That(RendererBuildConfiguration.FastPipelineStartup, Is.False);
#endif
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
}

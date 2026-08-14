using System.Xml.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTransportAuditVariantTests
{
    [TestCase(SimpleDdgiStoragePackingMode.Legacy, "legacy")]
    [TestCase(SimpleDdgiStoragePackingMode.Validate, "validate")]
    [TestCase(SimpleDdgiStoragePackingMode.Packed, "packed")]
    public void PrewarmResolver_WithoutGuiding_SelectsOnlyActiveStoragePair(
        SimpleDdgiStoragePackingMode storagePackingMode,
        string storageStem)
    {
        IReadOnlyList<string> shaders =
            SimpleDdgiTransportAuditPass.ResolvePrewarmShaderNames(
                storagePackingMode,
                prewarmDirectionalGuiding: false);

        Assert.Multiple(() =>
        {
            Assert.That(shaders, Is.EqualTo(new[]
            {
                $"ddgi_simple_transport_audit_{storageStem}.comp.spv",
                $"ddgi_simple_transport_audit_reduce_{storageStem}.comp.spv"
            }));
            Assert.That(shaders.Distinct().Count(), Is.EqualTo(2));
        });
    }

    [TestCase(SimpleDdgiStoragePackingMode.Legacy, "legacy")]
    [TestCase(SimpleDdgiStoragePackingMode.Validate, "validate")]
    [TestCase(SimpleDdgiStoragePackingMode.Packed, "packed")]
    public void PrewarmResolver_WithGuiding_SelectsOnlyActiveStoragePairs(
        SimpleDdgiStoragePackingMode storagePackingMode,
        string storageStem)
    {
        IReadOnlyList<string> shaders =
            SimpleDdgiTransportAuditPass.ResolvePrewarmShaderNames(
                storagePackingMode,
                prewarmDirectionalGuiding: true);

        Assert.Multiple(() =>
        {
            Assert.That(shaders, Is.EqualTo(new[]
            {
                $"ddgi_simple_transport_audit_{storageStem}.comp.spv",
                $"ddgi_simple_transport_audit_reduce_{storageStem}.comp.spv",
                $"ddgi_simple_transport_audit_{storageStem}_guided.comp.spv",
                $"ddgi_simple_transport_audit_reduce_{storageStem}_guided.comp.spv"
            }));
            Assert.That(shaders.Distinct().Count(), Is.EqualTo(4));
        });
    }

    [Test]
    public void RuntimeResolver_UsesCurrentStorageGuidingAndRole()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiTransportAuditPass.ResolveShaderName(
                    SimpleDdgiStoragePackingMode.Legacy,
                    directionalGuidingTransport: false,
                    SimpleDdgiTransportAuditPass.AuditPipelineRole.Rays),
                Is.EqualTo("ddgi_simple_transport_audit_legacy.comp.spv"));
            Assert.That(
                SimpleDdgiTransportAuditPass.ResolveShaderName(
                    SimpleDdgiStoragePackingMode.Validate,
                    directionalGuidingTransport: true,
                    SimpleDdgiTransportAuditPass.AuditPipelineRole.Reduce),
                Is.EqualTo(
                    "ddgi_simple_transport_audit_reduce_validate_guided.comp.spv"));
            Assert.That(
                SimpleDdgiTransportAuditPass.ResolveShaderName(
                    SimpleDdgiStoragePackingMode.Packed,
                    directionalGuidingTransport: true,
                    SimpleDdgiTransportAuditPass.AuditPipelineRole.Rays),
                Is.EqualTo(
                    "ddgi_simple_transport_audit_packed_guided.comp.spv"));
        });
    }

    [Test]
    public void ShaderProject_OptimizesOnlyDebugAuditVariants_WithCountersEnabled()
    {
        XDocument project = XDocument.Load(
            FindRepoFile("Njulf.Shaders", "Njulf.Shaders.csproj"));
        XElement auditOptimization = project
            .Descendants("NjulfSimpleDdgiAuditOptimizationOptions")
            .Single();
        XElement debugDiagnostics = project
            .Descendants("NjulfShaderDetailedDiagnosticsOptions")
            .Single(element =>
                ((string?)element.Attribute("Condition"))?.Contains(
                    "Debug",
                    StringComparison.Ordinal) == true);
        XElement auditCompile = project
            .Descendants("Exec")
            .Single(element =>
                ((string?)element.Attribute("Command"))?.Contains(
                    "%(SimpleDdgiAuditShaderVariant.Identity)",
                    StringComparison.Ordinal) == true);
        XElement[] otherCompiles = project
            .Descendants("Exec")
            .Where(element => element != auditCompile)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(auditOptimization.Value.Trim(), Is.EqualTo("-Os"));
            Assert.That(
                (string?)auditOptimization.Attribute("Condition"),
                Does.Contain("'$(Configuration)' == 'Debug'"));
            Assert.That(
                debugDiagnostics.Value.Trim(),
                Is.EqualTo("-DNJULF_DDGI_DETAILED_COUNTERS=1"));
            Assert.That(
                (string?)auditCompile.Attribute("Command"),
                Does.Contain("$(NjulfSimpleDdgiAuditOptimizationOptions)"));
            Assert.That(
                otherCompiles.Select(element =>
                    (string?)element.Attribute("Command") ?? string.Empty),
                Has.None.Contains("$(NjulfSimpleDdgiAuditOptimizationOptions)"));
        });
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(pathParts));
    }
}

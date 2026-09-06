using System.Reflection;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Shaders;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

internal static class LoadedShaderTestEvidence
{
    internal static LoadedShaderIdentity Identity { get; } = JsonSerializer.Deserialize<LoadedShaderIdentity>(
        File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory,
            "Fixtures", "loaded-shader-identity-v1.json")))!;
    internal static LoadedShaderMeasurementEvidence Measurement { get; } = new(
        Identity.Fingerprint, Identity.Generation, Identity.Fingerprint, Identity.Generation);
}

[TestFixture]
public sealed class LoadedShaderIdentityTests
{
    [Test]
    public void InventoryMatchesIndependentGoldenVectorAndRoundTrips()
    {
        LoadedShaderIdentity identity = LoadedShaderTestEvidence.Identity;
        var roundTrip = JsonSerializer.Deserialize<LoadedShaderIdentity>(JsonSerializer.Serialize(identity));
        Assert.Multiple(() =>
        {
            Assert.That(LoadedShaderIdentity.ComputeFingerprint(identity.Modules), Is.EqualTo(identity.Fingerprint));
            Assert.That(LoadedShaderIdentity.Validate(roundTrip), Is.Null);
            Assert.That(LoadedShaderMeasurementEvidence.Validate(roundTrip, LoadedShaderTestEvidence.Measurement), Is.Null);
            Assert.That(LoadedShaderIdentity.Validate(identity with { Fingerprint = "sha256:" + new string('0', 64) }), Is.Not.Null);
            Assert.That(LoadedShaderIdentity.Validate(identity with { Schema = "future" }), Is.Not.Null);
            Assert.That(LoadedShaderIdentity.Validate(null), Does.Contain("recapture"));
        });
        var legacy = JsonSerializer.Deserialize<PerformanceCaptureRunMetadata>(
            "{\"SceneKind\":\"test\",\"Scenario\":\"test\",\"BuildConfiguration\":\"Release\",\"ApplicationVersion\":\"1\",\"Commit\":\"test\",\"ShaderBundleHash\":\"old\",\"SettingsSchemaVersion\":1}");
        Assert.That(legacy!.LoadedShaderIdentity, Is.Null);
        Assert.That(LoadedShaderMeasurementEvidence.Validate(legacy.LoadedShaderIdentity, null), Is.Not.Null);
    }

    [Test]
    public void RegistryIsDeterministicConcurrentAndIndependentOfSources()
    {
        var registry = new ShaderModuleIdentityRegistry();
        var entries = LoadedShaderTestEvidence.Identity.Modules;
        Parallel.For(0, 200, i => registry.Record(entries[i % 2]));
        LoadedShaderIdentity snapshot = registry.Snapshot();
        registry.Record(entries[0] with { SourceKind = "override", SourceIdentity = "another/path" });
        var reversed = new ShaderModuleIdentityRegistry();
        reversed.Record(entries[1]);
        reversed.Record(entries[0]);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Generation, Is.EqualTo(2));
            Assert.That(registry.Snapshot(), Is.SameAs(snapshot));
            Assert.That(snapshot.Fingerprint, Is.EqualTo(LoadedShaderTestEvidence.Identity.Fingerprint));
            Assert.That(reversed.Snapshot().Fingerprint, Is.EqualTo(snapshot.Fingerprint));
            Assert.That(new ShaderModuleIdentityRegistry().Snapshot().Modules, Is.Empty);
        });
        registry.Record(entries[0] with { Sha256 = entries[1].Sha256 });
        Assert.Multiple(() =>
        {
            Assert.That(registry.Snapshot().Modules, Has.Count.EqualTo(3));
            Assert.That(LoadedShaderIdentity.Validate(registry.Snapshot()), Does.Contain("different bytes"));
            Assert.That(snapshot.Modules, Has.Count.EqualTo(2), "Published snapshots must remain immutable.");
            Assert.That(reversed.Snapshot().Modules, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void ComparisonsDistinguishRepeatabilityFromFeatureIsolation()
    {
        LoadedShaderIdentity identity = LoadedShaderTestEvidence.Identity;
        var subsetRegistry = new ShaderModuleIdentityRegistry();
        subsetRegistry.Record(identity.Modules[0]);
        LoadedShaderIdentity subset = subsetRegistry.Snapshot();
        var changedRegistry = new ShaderModuleIdentityRegistry();
        changedRegistry.Record(identity.Modules[0] with { Sha256 = identity.Modules[1].Sha256 });
        Assert.Multiple(() =>
        {
            Assert.That(LoadedShaderIdentity.Compare(identity, subset, false), Is.Null);
            Assert.That(LoadedShaderIdentity.Compare(identity, subset, true), Is.Not.Null);
            Assert.That(LoadedShaderIdentity.Compare(identity, changedRegistry.Snapshot(), false), Is.Not.Null);
            Assert.That(LoadedShaderMeasurementEvidence.Validate(identity,
                LoadedShaderTestEvidence.Measurement with { EndGeneration = 3 }), Does.Contain("changed"));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnalyzerChecksLiveEndBoundaryEvenWhenDiagnosticsHaveNotCaughtUp(bool drift)
    {
        LoadedShaderIdentity identity = LoadedShaderTestEvidence.Identity;
        var analyzer = new SampleBenchmarkAnalyzer();
        analyzer.SetLoadedShaderMeasurementStart(identity);
        analyzer.SetLoadedShaderMeasurementEnd(drift ? identity with { Generation = 3 } : identity);
        analyzer.AddSample(RendererDiagnostics.Empty with
        {
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with { LoadedShaderIdentity = identity }
        }, RenderBudgetSnapshot.Empty);
        var report = analyzer.CreateReport(new SampleBenchmarkOptions(true, 0, 1, null),
            SamplePerformanceScenario.Normal, 0, 1, 0, 0);
        Assert.That(report.CaptureContract.Mismatches.Any(m => m.Contains("Loaded shader", StringComparison.Ordinal)),
            Is.EqualTo(drift));
        Assert.That(report.CaptureContract.LoadedShaders!.EndGeneration, Is.EqualTo(drift ? 3 : 2));
    }
}

[TestFixture, NonParallelizable]
public sealed class ShaderArtifactResolverTests
{
    [Test]
    public void RuntimeAndBundleHashSelectSpvOverrideAndIgnoreExtensionlessAlias()
    {
        string directory = Path.Combine(Path.GetTempPath(), "njulf-shader-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string variable = PerformanceCaptureHostIdentityResolver.ShaderOverrideDirectoryEnvironmentVariable;
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Assembly assembly = typeof(ShaderLibrary).Assembly;
            Environment.SetEnvironmentVariable(variable, null);
            string embeddedHash = PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly);
            byte[] embedded = ShaderModuleLoader.LoadBytes("composite.vert.spv");
            Environment.SetEnvironmentVariable(variable, directory);
            string emptyOverrideHash = PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly);
            File.WriteAllBytes(Path.Combine(directory, "composite.vert"), [5, 6, 7, 8]);
            Assert.That(ShaderModuleLoader.LoadBytes("composite.vert.spv"), Is.EqualTo(embedded));
            Assert.That(PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly), Is.EqualTo(emptyOverrideHash));
            string runtimePath = Path.Combine(directory, "composite.vert.spv");
            File.WriteAllBytes(runtimePath, [1, 2, 3, 4]);
            var snapshot = ShaderArtifactResolver.Resolve(assembly, "composite.vert", directory, directory);
            var captured = LoadedShaderModuleIdentity.Capture(snapshot);
            string firstHash = PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly);
            Assert.That(ShaderModuleLoader.LoadBytes("composite.vert.spv"), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            File.WriteAllBytes(runtimePath, [9, 10, 11, 12]);
            Assert.Multiple(() =>
            {
                Assert.That(firstHash, Is.Not.EqualTo(emptyOverrideHash));
                Assert.That(PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly), Is.Not.EqualTo(firstHash));
                Assert.That(snapshot.Bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
                Assert.That(captured.Sha256, Is.EqualTo(LoadedShaderTestEvidence.Identity.Modules[0].Sha256));
            });
            Environment.SetEnvironmentVariable(variable, null);
            Assert.That(PerformanceCaptureHostIdentityResolver.ResolveShaderBundleHash(assembly), Is.EqualTo(embeddedHash));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void MissingOverridesAndResourcesUseRuntimeDeploymentPrecedence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "njulf-shader-fallback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Shaders"));
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "Shaders", "fixture.comp.spv"), [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(directory, "fixture.comp.spv"), [5, 6, 7, 8]);
            var artifact = ShaderArtifactResolver.Resolve(typeof(ShaderArtifactResolverTests).Assembly,
                "fixture.comp.spv", Path.Combine(directory, "absent"), directory);
            Assert.That(artifact.Bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(artifact.SourceKind, Is.EqualTo("deployment"));
            Assert.Throws<ArgumentException>(() => ShaderArtifactResolver.RuntimeFileName("../fixture.comp.spv"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceSnapshotWriterTests
{
    [Test]
    public void PerformanceSnapshotWriter_IncludesFoliageSummary()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "performance-snapshot-tests");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            FoliagePatchCount = 1,
            FoliageClusterCount = 32,
            FoliageVisibleClusterCount = 24,
            FoliageVisibleMeshletDrawCount = 96,
            FoliageInstanceBufferBytes = 1024,
            GpuFoliageForwardMicroseconds = 250
        };
        RenderBudgetProfile profile = RenderBudgetProfile.Development;
        RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
            profile,
            diagnostics,
            MemoryBudgetSnapshot.Empty,
            new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
            new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        string path = new PerformanceSnapshotWriter().Write(directory, diagnostics, budget);
        string json = File.ReadAllText(path);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"Foliage\""));
            Assert.That(json, Does.Contain("\"VisibleMeshletDrawCount\": 96"));
            Assert.That(json, Does.Contain("\"BufferBytes\": 1024"));
            Assert.That(json, Does.Contain("\"LikelyBottleneck\": \"fragment-alpha-overdraw-or-forward-shading\""));
        });
    }
}

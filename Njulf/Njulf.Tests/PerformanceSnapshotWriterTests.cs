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
            GpuFoliageForwardMicroseconds = 250,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationMode = GlobalIlluminationMode.Hybrid,
            GlobalIlluminationDebugView = GlobalIlluminationDebugView.FinalIndirect,
            SsgiWidth = 960,
            SsgiHeight = 540,
            SsgiResolutionScale = 0.5f,
            SsgiRayCount = 6,
            DdgiProbeCount = 128,
            DdgiActiveProbeCount = 96,
            DdgiProbesUpdated = 8,
            GlobalIlluminationRenderTargetBytes = 2048,
            GpuSsgiTraceMicroseconds = 350,
            GpuSsgiDenoiseMicroseconds = 150,
            Graph = new RenderGraphDiagnostics(
                ResourceCount: 2,
                PassCount: 1,
                PlannedBarrierCount: 3,
                ExecutedBarrierCount: 3,
                TransientResourceCount: 1,
                PersistentResourceCount: 1,
                AliasableResourceCount: 1,
                ImportedResourceCount: 1,
                OwnedRenderTargetCount: 1,
                AsyncComputeCandidatePassCount: 1,
                AsyncComputeEnabledPassCount: 0,
                QueueOwnershipTransitionCount: 1,
                ResourceMemoryEstimateBytes: 4096,
                Resources:
                [
                    new RenderGraphResourceDiagnostics(
                        "LdrSceneColor",
                        "LDR scene color",
                        "Image",
                        "R16G16B16A16Sfloat",
                        "Swapchain",
                        "Persistent",
                        true,
                        true,
                        1,
                        4096)
                ],
                Passes:
                [
                    new RenderGraphPassDiagnostics(
                        "ToneMapCompositePass",
                        EnabledByFeatureIsolation: true,
                        QueueIntent: "Graphics",
                        AsyncComputeCandidate: false,
                        AsyncComputeEnabled: false,
                        AsyncComputeReason: "Pass is not marked safe for async compute scheduling.",
                        Reads: ["SceneColor"],
                        Writes: ["LdrSceneColor"],
                        ReadWrites: [])
                ],
                Barriers:
                [
                    new RenderGraphBarrierDiagnostics(
                        "ToneMapCompositePass",
                        "LdrSceneColor",
                        "Read",
                        "Write",
                        "ShaderReadOnlyOptimal",
                        "ColorAttachmentOptimal",
                        "FragmentShaderBit",
                        "ShaderSampledReadBit",
                        "ColorAttachmentOutputBit",
                        "ColorAttachmentWriteBit",
                        "Compute",
                        "Graphics",
                        QueueOwnershipTransition: true,
                        Executed: true)
                ])
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
            Assert.That(json, Does.Contain("\"GlobalIllumination\""));
            Assert.That(json, Does.Contain("\"Graph\""));
            Assert.That(json, Does.Contain("\"ResourceCount\": 2"));
            Assert.That(json, Does.Contain("\"LdrSceneColor\""));
            Assert.That(json, Does.Contain("\"VisibleMeshletDrawCount\": 96"));
            Assert.That(json, Does.Contain("\"BufferBytes\": 1024"));
            Assert.That(json, Does.Contain("\"SsgiWidth\": 960"));
            Assert.That(json, Does.Contain("\"SsgiRayCount\": 6"));
            Assert.That(json, Does.Contain("\"GpuMicroseconds\": 500"));
            Assert.That(json, Does.Contain("\"LikelyBottleneck\": \"fragment-alpha-overdraw-or-forward-shading\""));
        });
    }
}

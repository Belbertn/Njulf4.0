using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class RendererDiagnosticsSchemaTests
    {
        [Test]
        public void Build_EmitsEveryFirstClassCategoryWithCurrentVersion()
        {
            RendererDiagnosticsSnapshot snapshot = RendererDiagnosticsSchema.Build(RendererDiagnostics.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion, Is.EqualTo(RendererDiagnosticsSchema.CurrentVersion));
                Assert.That(snapshot.Categories, Has.Count.EqualTo(Enum.GetValues<RendererDiagnosticsCategory>().Length));
                Assert.That(snapshot.Categories.Select(c => c.Category), Is.Unique);
                Assert.That(snapshot.Categories.All(c => c.SchemaVersion == RendererDiagnosticsSchema.CurrentVersion), Is.True);
            });
        }

        [Test]
        public void Build_PropagatesBudgetStatusesToTimingMemoryAndUploadCategories()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                CpuTotalDrawSceneMicroseconds = 99_000,
                GpuFrameMicroseconds = 99_000,
                GpuTimingValid = 1,
                TrackedGpuMemoryBytes = profile.GpuMemoryBudgetBytes + 1,
                UploadedBytes = profile.UploadBudgetBytesPerFrame + 1,
                CpuFrameBudgetStatus = RenderBudgetStatus.OverBudget,
                GpuFrameBudgetStatus = RenderBudgetStatus.OverBudget,
                GpuMemoryBudgetStatus = RenderBudgetStatus.OverBudget,
                UploadBudgetStatus = RenderBudgetStatus.OverBudget
            };

            RenderBudgetSnapshot budget = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                new MemoryBudgetSnapshot(
                    profile.GpuMemoryBudgetBytes + 1,
                    profile.GpuMemoryBudgetBytes,
                    [],
                    MemoryHeapBudgetSnapshot.Unavailable),
                new UploadBudgetSnapshot(
                    profile.UploadBudgetBytesPerFrame + 1,
                    profile.UploadBudgetBytesPerFrame,
                    0,
                    1,
                    [],
                    RenderBudgetStatus.OverBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            RendererDiagnosticsSnapshot snapshot = RendererDiagnosticsSchema.Build(diagnostics, budget);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.GetCategory(RendererDiagnosticsCategory.FrameTiming).Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(snapshot.GetCategory(RendererDiagnosticsCategory.GpuMemory).Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(snapshot.GetCategory(RendererDiagnosticsCategory.UploadStaging).Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void Build_FlagsImpossibleVisibilityCounterCombinations()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                ForwardMeshletCandidates = 10,
                ForwardMeshletVisibleAfterOcclusion = 11,
                ForwardOcclusionTestedMeshletsGpu = 3,
                ForwardGpuOcclusionRejectedMeshlets = 4,
                StaticInstanceCount = 4,
                VisibleStaticInstanceCount = 3,
                CulledStaticInstanceCount = 2
            };

            RendererDiagnosticsCategorySnapshot visibility =
                RendererDiagnosticsSchema.Build(diagnostics).GetCategory(RendererDiagnosticsCategory.VisibilityCulling);

            Assert.Multiple(() =>
            {
                Assert.That(visibility.Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(visibility.Warnings, Has.Count.EqualTo(3));
            });
        }

        [Test]
        public void Build_ReportsGpuVisibilityCapacityAndGrowth()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GpuDrivenVisibilityEnabled = 1,
                GpuVisibilityDrawCapacity = 131072,
                GpuVisibilityResizeCount = 2,
                GpuVisibilityAllocatedBytes = 4 * 1024 * 1024
            };

            RendererDiagnosticsSnapshot snapshot = RendererDiagnosticsSchema.Build(diagnostics);
            RendererDiagnosticsCategorySnapshot gpuScene =
                snapshot.GetCategory(RendererDiagnosticsCategory.GpuScene);
            RendererDiagnosticsCategorySnapshot visibility =
                snapshot.GetCategory(RendererDiagnosticsCategory.VisibilityCulling);

            Assert.Multiple(() =>
            {
                Assert.That(gpuScene.Metrics.Single(metric => metric.Name == "GpuDrivenVisibilityEnabled").Value, Is.EqualTo(1));
                Assert.That(gpuScene.Metrics.Single(metric => metric.Name == "GpuVisibilityDrawCapacity").Value, Is.EqualTo(131072));
                Assert.That(gpuScene.Metrics.Single(metric => metric.Name == "GpuVisibilityResizeCount").Value, Is.EqualTo(2));
                Assert.That(gpuScene.Metrics.Single(metric => metric.Name == "GpuVisibilityAllocatedBytes").Value, Is.EqualTo(4 * 1024 * 1024));
                Assert.That(visibility.Metrics.Single(metric => metric.Name == "GpuDrivenVisibilityEnabled").Value, Is.EqualTo(1));
                Assert.That(visibility.Metrics.Single(metric => metric.Name == "GpuVisibilityDrawCapacity").Value, Is.EqualTo(131072));
                Assert.That(visibility.Metrics.Single(metric => metric.Name == "GpuVisibilityResizeCount").Value, Is.EqualTo(2));
            });
        }

        [Test]
        public void ReadMetadata_ReportsLegacySnapshotWithoutSchemaVersions()
        {
            const string legacyJson = """
                                      {
                                        "CapturedAt": "2026-06-13T14:23:43+02:00",
                                        "Diagnostics": {
                                          "VisibleObjectCount": 1
                                        }
                                      }
                                      """;

            PerformanceSnapshotMetadata metadata = RendererDiagnosticsSchema.ReadMetadata(legacyJson);

            Assert.Multiple(() =>
            {
                Assert.That(metadata.SchemaVersion, Is.EqualTo(0));
                Assert.That(metadata.RendererDiagnosticsSchemaVersion, Is.EqualTo(0));
                Assert.That(metadata.CompatibilityWarnings, Has.Count.EqualTo(2));
            });
        }

        [Test]
        public void PerformanceSnapshotWriter_ExportsStructuredDiagnostics()
        {
            string directory = Path.Combine(Path.GetTempPath(), "njulf-diagnostics-schema-tests", Guid.NewGuid().ToString("N"));
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                RendererDiagnostics.Empty with
                {
                    CpuTotalDrawSceneMicroseconds = 4_000,
                    CpuFrameBudgetStatus = RenderBudgetStatus.WithinBudget,
                    GpuFrameMicroseconds = 6_000,
                    GpuTimingSupported = 1,
                    GpuTimingEnabled = 1,
                    GpuTimingValid = 1,
                    GpuFrameBudgetStatus = RenderBudgetStatus.WithinBudget,
                    CpuGraphicsQueueSubmitMicroseconds = 25,
                    CpuComputeQueueSubmitMicroseconds = 7,
                    CpuTransferQueueSubmitMicroseconds = 3,
                    UploadedBytes = 1024,
                    UploadBudgetBytesPerFrame = 4096,
                    UploadBudgetStatus = RenderBudgetStatus.WithinBudget
                },
                RenderBudgetSnapshot.Empty,
                new RenderGraphResourceInventorySnapshot(
                    ["Depth", "HiZ", "Forward"],
                    [],
                    [
                        new RenderGraphBufferResourceInventory(
                            "GpuScene.Objects",
                            1024,
                            64,
                            16,
                            "Storage",
                            "External",
                            "Frame",
                            [],
                            ["Forward"])
                    ],
                    0,
                    1024));

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            Assert.Multiple(() =>
            {
                Assert.That(root.GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(RendererDiagnosticsSchema.CurrentVersion));
                Assert.That(root.GetProperty("Diagnostics").GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(RendererDiagnosticsSchema.CurrentVersion));
                Assert.That(root.GetProperty("StructuredDiagnostics").GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(RendererDiagnosticsSchema.CurrentVersion));
                Assert.That(root.GetProperty("StructuredDiagnostics").GetProperty("Categories").GetArrayLength(), Is.EqualTo(Enum.GetValues<RendererDiagnosticsCategory>().Length));
                Assert.That(root.GetProperty("OverlayData").GetProperty("SchemaVersion").GetInt32(), Is.EqualTo(RendererDiagnosticsSchema.CurrentVersion));
                Assert.That(root.GetProperty("OverlayData").GetProperty("FrameTiming").GetArrayLength(), Is.GreaterThanOrEqualTo(8));
                Assert.That(root.GetProperty("OverlayData").GetProperty("PassTimings").GetArrayLength(), Is.GreaterThan(0));
                Assert.That(root.GetProperty("OverlayData").GetProperty("Graph").GetProperty("PassCount").GetInt32(), Is.EqualTo(3));
            });
        }

        [Test]
        public void OverlayBuilder_UsesRealQueueSubmitAndUploadCounters()
        {
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                CpuGraphicsQueueSubmitMicroseconds = 11,
                CpuComputeQueueSubmitMicroseconds = 22,
                CpuTransferQueueSubmitMicroseconds = 33,
                UploadedBytes = 1024,
                UploadBudgetBytesPerFrame = 2048,
                UploadBudgetStatus = RenderBudgetStatus.WithinBudget,
                ObjectUploadBytes = 256,
                LightUploadBytes = 128
            };

            RendererDiagnosticsOverlaySnapshot overlay =
                RendererDiagnosticsOverlayBuilder.Build(diagnostics, RenderBudgetSnapshot.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(overlay.FrameTiming.Single(bar => bar.Name == "Graphics submit").Value, Is.EqualTo(11));
                Assert.That(overlay.FrameTiming.Single(bar => bar.Name == "Compute submit").Value, Is.EqualTo(22));
                Assert.That(overlay.FrameTiming.Single(bar => bar.Name == "Transfer submit").Value, Is.EqualTo(33));
                Assert.That(overlay.Uploads.Single(bar => bar.Name == "Total upload").BudgetFraction, Is.EqualTo(0.5d));
                Assert.That(overlay.Uploads.Single(bar => bar.Name == "Object upload").Value, Is.EqualTo(256));
                Assert.That(overlay.Uploads.Single(bar => bar.Name == "Light upload").Value, Is.EqualTo(128));
            });
        }
    }
}

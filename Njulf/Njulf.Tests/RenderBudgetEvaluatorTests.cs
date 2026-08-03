using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderBudgetEvaluatorTests
    {
        [Test]
        public void RenderBudgetEvaluator_WithinWarningAndOverBudgetThresholds()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderBudgetEvaluator.Classify(84, 100), Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(RenderBudgetEvaluator.Classify(86, 100), Is.EqualTo(RenderBudgetStatus.Warning));
                Assert.That(RenderBudgetEvaluator.Classify(101, 100), Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_UsesActiveProfile()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                CpuTotalDrawSceneMicroseconds = 11_000,
                GpuFrameMicroseconds = 1,
                GpuTimingValid = 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(snapshot.OverallStatus, Is.EqualTo(RenderBudgetStatus.OverBudget));
            Assert.That(snapshot.Profile.Kind, Is.EqualTo(RenderBudgetProfileKind.LowSpec1080p30));
        }

        [Test]
        public void RenderBudgetEvaluator_UnavailableMetricsDoNotFailBudget()
        {
            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                RenderBudgetProfile.Development,
                RendererDiagnostics.Empty,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, RenderBudgetProfile.Development.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(snapshot.OverallStatus, Is.Not.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_SeparatesDriverHeapAndTrackedGpuMemoryBudgets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            var memory = new MemoryBudgetSnapshot(
                profile.GpuMemoryBudgetBytes + 1,
                profile.GpuMemoryBudgetBytes,
                Array.Empty<MemoryBudgetEntry>(),
                new MemoryHeapBudgetSnapshot(
                    true,
                    [
                        new MemoryHeapBudgetEntry(
                            0,
                            true,
                            profile.GpuMemoryBudgetBytes,
                            profile.GpuMemoryBudgetBytes * 2,
                            profile.GpuMemoryBudgetBytes,
                            profile.GpuMemoryBudgetBytes,
                            1,
                            1)
                    ]));

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                RendererDiagnostics.Empty,
                memory,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.EffectiveGpuMemoryMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.TrackedGpuMemoryMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_IncludesFoliageSpecificBudgets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GpuFrameMicroseconds = 1,
                GpuTimingValid = 1,
                FoliageVisibleClusterCount = profile.FoliageClusterBudget + 1,
                FoliageVisibleMeshletDrawCount = profile.FoliageMeshletDrawBudget + 1,
                FoliageGrassBladeEstimate = profile.FoliageGrassBladeBudget + 1,
                FoliageInstanceBufferBytes = profile.FoliageMemoryBudgetBytes + 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "Foliage clusters").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage meshlet draws").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage grass blades").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Foliage memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_IncludesGlobalIlluminationBudgets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.LowSpec1080p30;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Hybrid,
                GpuTimingValid = 1,
                GpuSsgiTraceMicroseconds = 200,
                GpuSsgiTemporalMicroseconds = 100,
                CpuGlobalIlluminationRecordP95Microseconds = 501,
                GlobalIlluminationCpuTimingSampleCount = 1,
                GlobalIlluminationRenderTargetBytes = 2,
                DdgiProbeCount = 32_769
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "GI GPU").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "GI CPU scheduling and upload").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "GI memory").Status, Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(Metric(snapshot, "DDGI probes").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_DoesNotCountInclusiveForwardDrawAsIncrementalGi()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationDdgiActive = 1,
                SimpleDdgiActive = 1,
                GpuTimingValid = 1,
                GpuSsgiTraceMicroseconds = 1_000,
                GpuForwardGiGatherMicroseconds = 100_000,
                GpuForwardGiGatherTimingCoverage = 1,
                GpuForwardGiGatherTimingAttribution = GiTimingAttribution.Inclusive
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "GI GPU").Value, Is.EqualTo(1.0));
                Assert.That(Metric(snapshot, "GI GPU").Status, Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, "GI forward gather (inclusive draw)").Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(Metric(snapshot, "GI forward gather incremental").Status, Is.EqualTo(RenderBudgetStatus.Unavailable));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_UsesAttributedPairedForwardGiTimingWhenAvailable()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationDdgiActive = 1,
                SimpleDdgiActive = 1,
                GpuTimingValid = 1,
                GpuSsgiTraceMicroseconds = 1_000,
                GpuForwardGiGatherMicroseconds = 100_000,
                GpuForwardGiGatherTimingCoverage = 1,
                GpuForwardGiGatherTimingAttribution = GiTimingAttribution.Inclusive,
                GpuForwardGiIncrementalMicroseconds = 500,
                GpuForwardGiIncrementalAttribution = GiTimingAttribution.PairedEstimate
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "GI GPU").Value, Is.EqualTo(1.5));
                Assert.That(Metric(snapshot, "GI GPU").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(Metric(snapshot, "GI forward gather incremental").Value, Is.EqualTo(0.5));
                Assert.That(Metric(snapshot, "GI forward gather incremental").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_ReportsUniqueGiResidencyWithoutAddingTrackedAllocationTwice()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                SimpleDdgiActive = 1,
                SimpleDdgiAtlasBytes = 100,
                DdgiAtlasMemoryBudgetBytes = 128
            };
            var memory = new MemoryBudgetSnapshot(
                100,
                profile.GpuMemoryBudgetBytes,
                [new MemoryBudgetEntry(MemoryBudgetCategory.GlobalIllumination, 100, 1, "tracked simple-ddgi atlas")],
                MemoryHeapBudgetSnapshot.Unavailable);

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                memory,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.GiResidency.UniqueResidentBytes, Is.EqualTo(100));
                Assert.That(snapshot.GiResidency.Components.Single(component => component.Name == "DDGI cache").Bytes, Is.EqualTo(100));
                Assert.That(snapshot.GiResidency.UniqueResidentBytes, Is.Not.EqualTo(200));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_SumsDeclaredGiComponentCapsEvenWhenAComponentIsIdle()
        {
            const ulong mib = 1024UL * 1024UL;
            const ulong ddgiBudget = 192UL * mib;
            const ulong farFieldBudget = 96UL * mib;
            const ulong accelerationStructureBudget = 256UL * mib;
            const ulong accelerationStructureTransientBudget = 384UL * mib;
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                SimpleDdgiActive = 1,
                SimpleDdgiAtlasBytes = 1,
                DdgiAtlasMemoryBudgetBytes = ddgiBudget,
                FarFieldPagedFeatureEnabled = 1,
                FarFieldMemoryBudgetBytes = farFieldBudget,
                StreamedGiAccelerationStructuresFeatureEnabled = 1,
                AccelerationStructureMemoryBudgetBytes = accelerationStructureBudget
            };
            var memory = new MemoryBudgetSnapshot(
                1,
                profile.GpuMemoryBudgetBytes,
                [new MemoryBudgetEntry(MemoryBudgetCategory.GlobalIllumination, 1, 1, "tracked simple-ddgi atlas")],
                MemoryHeapBudgetSnapshot.Unavailable);

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                memory,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.GiResidency.DeclaredComponentBudgetBytes,
                    Is.EqualTo(
                        profile.GlobalIlluminationRenderTargetBudgetBytes +
                        ddgiBudget +
                        farFieldBudget +
                        accelerationStructureBudget +
                        accelerationStructureTransientBudget));
                Assert.That(
                    snapshot.GiResidency.Components.Single(component => component.Name == "DDGI scratch and transient").CountsTowardCombinedBudget,
                    Is.False);
                Assert.That(
                    Metric(snapshot, "GI unique residency").Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_DoesNotClaimAnAggregateCapWhenCustomProfileOmitsRenderTargetCap()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development with
            {
                GlobalIlluminationRenderTargetBudgetBytes = 0
            };
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationRenderTargetBytes = 4,
                SimpleDdgiActive = 1,
                SimpleDdgiAtlasBytes = 1,
                DdgiAtlasMemoryBudgetBytes = 64
            };
            var memory = new MemoryBudgetSnapshot(
                1,
                profile.GpuMemoryBudgetBytes,
                [new MemoryBudgetEntry(MemoryBudgetCategory.GlobalIllumination, 1, 1, "tracked simple-ddgi atlas")],
                MemoryHeapBudgetSnapshot.Unavailable);

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                memory,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.GiResidency.UniqueResidentBytes, Is.EqualTo(5));
                Assert.That(snapshot.GiResidency.DeclaredComponentBudgetBytes, Is.Zero);
                Assert.That(Metric(snapshot, "GI unique residency").Status, Is.EqualTo(RenderBudgetStatus.Unavailable));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_QualifiesSsgiOnlyResidencyAgainstExplicitTierCap()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.MidSpec1080p60;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationSsgiActive = 1,
                GlobalIlluminationRenderTargetBytes = 32UL * 1024UL * 1024UL
            };
            var memory = new MemoryBudgetSnapshot(
                diagnostics.GlobalIlluminationRenderTargetBytes,
                profile.GpuMemoryBudgetBytes,
                [
                    new MemoryBudgetEntry(
                        MemoryBudgetCategory.GlobalIllumination,
                        0,
                        0,
                        "SSGI render targets are accounted separately")
                ],
                MemoryHeapBudgetSnapshot.Unavailable);

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                memory,
                new UploadBudgetSnapshot(
                    0,
                    profile.UploadBudgetBytesPerFrame,
                    0,
                    0,
                    [],
                    RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(
                    0,
                    0,
                    RuntimeStallReason.Unknown,
                    0,
                    []));

            GiResidencyComponent renderTargets =
                snapshot.GiResidency.Components.Single(
                    component => component.Name == "GI render targets");
            Assert.Multiple(() =>
            {
                Assert.That(renderTargets.BudgetBytes,
                    Is.EqualTo(profile.GlobalIlluminationRenderTargetBudgetBytes));
                Assert.That(renderTargets.CountsTowardCombinedBudget, Is.True);
                Assert.That(snapshot.GiResidency.DeclaredComponentBudgetBytes,
                    Is.EqualTo(profile.GlobalIlluminationRenderTargetBudgetBytes));
                Assert.That(Metric(snapshot, "GI unique residency").Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesFarFieldMemoryForLegacyAndFallbackAllocations()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                FarFieldPagedMode = 0,
                FarFieldCacheBytes = 101,
                FarFieldMemoryBudgetBytes = 100
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(Metric(snapshot, "Far-field page cache").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesGlobalIlluminationPerformanceRules()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Hybrid,
                GpuTimingValid = 1,
                SsgiResolutionScale = 0.5f,
                SsgiRayCount = 8,
                DdgiActiveProbeCount = 128,
                DdgiProbesUpdated = 32,
                DdgiGatherTileCount = 64,
                DdgiGatherFallbackTileCount = 0
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "SSGI resolution scale").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(Metric(snapshot, "SSGI rays per pixel").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(Metric(snapshot, "DDGI probes updated").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(Metric(snapshot, "DDGI gather fallback tiles").Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_FailsFullResolutionSsgiAndFullSceneDdgiUpdate()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.RayQueryHybrid,
                GpuTimingValid = 1,
                SsgiResolutionScale = 1.0f,
                SsgiRayCount = 16,
                DdgiActiveProbeCount = 128,
                DdgiProbesUpdated = 128
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "SSGI resolution scale").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "SSGI rays per pixel").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI probes updated").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_FailsDdgiResourceReinitializationDuringOrdinaryCameraMovement()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                DdgiCascadeCount = 3,
                DdgiCameraMovementClass = DdgiCameraMovementClass.Normal,
                DdgiResourceReinitializationCount = 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(Metric(snapshot, "DDGI resource reinitializations").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_FailsResolvedDdgiBudgetViolations()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                DdgiActiveProbeCount = 129,
                DdgiMaxActiveProbeBudget = 128,
                DdgiProbesUpdated = 33,
                DdgiProbeUpdateRequestBudget = 32,
                DdgiTextureBytes = 65,
                DdgiAtlasMemoryBudgetBytes = 64,
                DdgiGatherTileCount = 64,
                DdgiGatherFallbackTileCount = 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "DDGI active probe budget").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI update request budget").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI atlas memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI gather fallback tiles").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_UsesResolvedSimpleDdgiLayoutBudget()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                SimpleDdgiActive = 1,
                DdgiProbeCount = 15_383,
                DdgiMaxActiveProbeBudget = 16_384
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            BudgetMetric metric = Metric(snapshot, "DDGI probes");
            Assert.Multiple(() =>
            {
                Assert.That(metric.FailureThreshold, Is.EqualTo(16_384));
                Assert.That(metric.Status, Is.EqualTo(RenderBudgetStatus.WithinBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesObservedSimpleDdgiDirtyLatencyTargets()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                SimpleDdgiActive = 1,
                SimpleDdgiDirtyFirstUpdateLatencySampleCount = 10,
                SimpleDdgiDirtyFirstUpdateLatencyP95Frames = 2,
                SimpleDdgiDirtyConvergenceLatencySampleCount = 10,
                SimpleDdgiDirtyConvergenceLatencyP95Frames = 9
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "DDGI dirty first-update latency").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "DDGI dirty convergence latency").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_FailsMaterialGiSafetyCounters()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                MaterialNonFiniteValueCount = 1,
                MaterialClampedValueCount = 2,
                MaterialAlphaCandidateLimitReachedCount = 3
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(Metric(snapshot, "Material GI non-finite values").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Material GI clamped values").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(Metric(snapshot, "Material alpha candidate limit").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(snapshot.OverallStatus, Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_AccountsForCanonicalAndSampledSimpleDdgiAtlases()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                SimpleDdgiActive = 1,
                // Canonical SSBO atlas plus optional sampled image mirror.
                SimpleDdgiAtlasBytes = 65,
                DdgiTextureBytes = 32,
                DdgiBufferBytes = 33,
                DdgiAtlasMemoryBudgetBytes = 64
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(Metric(snapshot, "DDGI atlas memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_ChargesPrivateV2TransportStorageToSimpleDdgiBudget()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                GlobalIlluminationMode = GlobalIlluminationMode.Ddgi,
                SimpleDdgiActive = 1,
                SimpleDdgiAtlasBytes = 32,
                SimpleDdgiTransportIrradianceAtlasBytes = 16,
                SimpleDdgiTransportSourceCacheBytes = 32,
                DdgiAtlasMemoryBudgetBytes = 64
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.That(Metric(snapshot, "DDGI atlas memory").Status, Is.EqualTo(RenderBudgetStatus.OverBudget));
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesMaterialGiReleaseQualificationAndActiveZeroGates()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                MaterialGiV2ActiveFeatures = MaterialGiV2Feature.MaterialTransport,
                MaterialGiReleaseQualificationRequired = 1,
                MaterialGiReleaseQualificationFailureCount = 1,
                MaterialActiveLegacyV1FallbackCount = 1,
                MaterialActiveInvalidProfileCount = 1
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiQualificationMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiActiveV1FallbackMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiActiveInvalidProfileMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesEmissiveMeshSamplingZeroGates()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RendererDiagnostics failingDiagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                MaterialGiV2ActiveFeatures = MaterialGiV2Feature.EmissiveMeshSampling,
                DdgiEmissiveTriangleCandidateCount = 4,
                DdgiEmissiveTriangleBudget = 2,
                DdgiEmissiveSkippedEnergyFraction = 0.125f,
                DdgiEmissiveSkippedSkinnedObjectCount = 1,
                DdgiEmissiveSkippedSkinnedImportance = 0.5
            };
            RendererDiagnostics passingDiagnostics = failingDiagnostics with
            {
                DdgiEmissiveTriangleCandidateCount = 2,
                DdgiEmissiveSkippedEnergyFraction = 0.0f,
                DdgiEmissiveSkippedSkinnedObjectCount = 0,
                DdgiEmissiveSkippedSkinnedImportance = 0.0
            };
            RendererDiagnostics numericallyZeroDiagnostics = passingDiagnostics with
            {
                DdgiEmissiveSkippedEnergyFraction = 1.65e-16f
            };
            RendererDiagnostics nonFiniteDiagnostics = passingDiagnostics with
            {
                DdgiEmissiveSkippedEnergyFraction = float.NaN
            };

            RenderBudgetSnapshot failing = Evaluate(profile, failingDiagnostics);
            RenderBudgetSnapshot passing = Evaluate(profile, passingDiagnostics);
            RenderBudgetSnapshot numericallyZero = Evaluate(profile, numericallyZeroDiagnostics);
            RenderBudgetSnapshot nonFinite = Evaluate(profile, nonFiniteDiagnostics);

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(failing, RenderBudgetEvaluator.DdgiEmissiveTruncatedSourceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    Metric(failing, RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    Metric(failing, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedObjectMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    Metric(failing, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedImportanceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(failing.OverallStatus, Is.EqualTo(RenderBudgetStatus.OverBudget));

                Assert.That(
                    Metric(passing, RenderBudgetEvaluator.DdgiEmissiveTruncatedSourceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(passing, RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(passing, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedObjectMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(passing, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedImportanceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));

                BudgetMetric numericallyZeroMetric = Metric(
                    numericallyZero,
                    RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName);
                Assert.That(numericallyZeroMetric.Value, Is.Zero);
                Assert.That(
                    numericallyZeroMetric.Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));

                Assert.That(
                    Metric(nonFinite, RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget),
                    "Non-finite zero-gate telemetry must fail closed instead of becoming unavailable.");
            });
        }

        [Test]
        public void RenderBudgetEvaluator_EnforcesPrimitiveProfileMemoryByQualityTier()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            ulong mediumCap = RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                RenderQualityPreset.Medium);
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                ActiveQualityPreset = RenderQualityPreset.Medium,
                MaterialGiV2ActiveFeatures = MaterialGiV2Feature.MaterialTransport,
                MaterialPrimitiveProfileGpuBytes = mediumCap +
                    MaterialManager.PrimitiveProfileGpuStrideBytes
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiPrimitiveProfileMemoryMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
                Assert.That(
                    RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(RenderQualityPreset.Ultra),
                    Is.EqualTo(MaterialManager.MaximumPrimitiveProfileGpuBytes));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_ChargesCompileAndUploadP95ToExistingGiCpuBudget()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            long budgetMicroseconds =
                (long)(profile.GlobalIlluminationCpuBudgetMilliseconds * 1_000.0);
            RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
            {
                GlobalIlluminationEnabled = 1,
                MaterialGiV2ActiveFeatures = MaterialGiV2Feature.MaterialTransport,
                MaterialCompileP95Microseconds = budgetMicroseconds * 3 / 5,
                MaterialCompileTimingSampleCount = 32,
                MaterialUploadP95Microseconds = budgetMicroseconds * 3 / 5,
                MaterialUploadTimingSampleCount = 256
            };

            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiCompileP95MetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiUploadP95MetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.WithinBudget));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiPipelineP95MetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.OverBudget));
            });
        }

        [Test]
        public void RenderBudgetEvaluator_LeavesMaterialGiGatesUnavailableWhenRolloutIsInactive()
        {
            RenderBudgetProfile profile = RenderBudgetProfile.Development;
            RenderBudgetSnapshot snapshot = new RenderBudgetEvaluator().Evaluate(
                profile,
                RendererDiagnostics.Empty with
                {
                    MaterialActiveLegacyV1FallbackCount = 10,
                    MaterialActiveInvalidProfileCount = 10,
                    MaterialCompileP95Microseconds = 10_000,
                    MaterialCompileTimingSampleCount = 10,
                    MaterialUploadP95Microseconds = 10_000,
                    MaterialUploadTimingSampleCount = 10,
                    DdgiEmissiveTriangleCandidateCount = 10,
                    DdgiEmissiveTriangleBudget = 1,
                    DdgiEmissiveSkippedEnergyFraction = 0.5f,
                    DdgiEmissiveSkippedSkinnedObjectCount = 2,
                    DdgiEmissiveSkippedSkinnedImportance = 3.0
                },
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(0, profile.UploadBudgetBytesPerFrame, 0, 0, [], RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiQualificationMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiActiveV1FallbackMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.MaterialGiPipelineP95MetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.DdgiEmissiveTruncatedSourceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.DdgiEmissiveSkippedEnergyMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedObjectMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
                Assert.That(
                    Metric(snapshot, RenderBudgetEvaluator.DdgiEmissiveUnsupportedSkinnedImportanceMetricName).Status,
                    Is.EqualTo(RenderBudgetStatus.Unavailable));
            });
        }

        private static RenderBudgetSnapshot Evaluate(
            RenderBudgetProfile profile,
            RendererDiagnostics diagnostics) =>
            new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                MemoryBudgetSnapshot.Empty,
                new UploadBudgetSnapshot(
                    0,
                    profile.UploadBudgetBytesPerFrame,
                    0,
                    0,
                    [],
                    RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(0, 0, RuntimeStallReason.Unknown, 0, []));

        private static BudgetMetric Metric(RenderBudgetSnapshot snapshot, string name)
        {
            return snapshot.Metrics.Single(metric => metric.Name == name);
        }
    }
}

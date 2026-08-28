using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceSnapshotWriterTests
{
    [Test]
    public void SnapshotWriter_RemainsAvailableForSimpleDdgiCaptures()
    {
        Assert.That(typeof(PerformanceSnapshotWriter).Assembly.GetName().Name, Is.EqualTo("Njulf.Rendering"));
    }

    [Test]
    public void GlobalIlluminationSnapshot_PreservesCompactReceiverPublicationEvidence()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverProbeBytes = 16UL * 123UL,
            SimpleDdgiReceiverProbeCapacity = 123,
            SimpleDdgiReceiverInvalidationBytes = 48UL,
            SimpleDdgiReceiverInvalidationRangeCount = 2,
            SimpleDdgiReceiverFullClear = 1,
            SimpleDdgiReceiverResourceGeneration = 17u,
            SimpleDdgiReceiverRecordsPublished = 29
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(diagnostics);

        Assert.That(snapshot.SimpleDdgiReceiverProbeBytes, Is.EqualTo(16UL * 123UL));
        Assert.That(snapshot.SimpleDdgiReceiverProbeCapacity, Is.EqualTo(123));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationBytes, Is.EqualTo(48UL));
        Assert.That(snapshot.SimpleDdgiReceiverInvalidationRangeCount, Is.EqualTo(2));
        Assert.That(snapshot.SimpleDdgiReceiverFullClear, Is.True);
        Assert.That(snapshot.SimpleDdgiReceiverResourceGeneration, Is.EqualTo(17u));
        Assert.That(snapshot.SimpleDdgiReceiverRecordsPublished, Is.EqualTo(29));
    }

    [Test]
    public void GlobalIlluminationSnapshot_PreservesReceiverCacheQualificationEvidence()
    {
        SimpleDdgiReceiverCacheDiagnostics receiverCache =
            SimpleDdgiReceiverCacheDiagnostics.Active(
                SimpleDdgiReceiverCacheMode.TemporalAdaptive,
                SimpleDdgiReceiverCacheMode.SurfaceAwareSpatial,
                SimpleDdgiReceiverCacheFallbackReason
                    .TemporalAdaptiveUnavailable,
                "spatial fallback",
                radianceBytes: 4_096,
                surfaceSidecarBytes: 2_048,
                pipelineArtifact: "receiver-cache-surface-v1") with
            {
                CounterReadbackValid = 1,
                ResolveCandidateCount = 100,
                ResolveValidCount = 80,
                ForwardCandidateCount = 1_000,
                ForwardAcceptedCount = 750,
                ForwardNormalRejectCount = 125,
                ExactFallbackFragmentCount = 250
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiReceiverCache = receiverCache
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(
                diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SimpleDdgiReceiverCache,
                Is.EqualTo(receiverCache));
            Assert.That(snapshot.SimpleDdgiReceiverCache.SurfaceAbiVersion,
                Is.EqualTo(SimpleDdgiReceiverSurfaceAbi.Version));
            Assert.That(snapshot.SimpleDdgiReceiverCache.AcceptedPercentage,
                Is.EqualTo(75.0));
            Assert.That(snapshot.SimpleDdgiReceiverCache
                .ExactFallbackPercentage, Is.EqualTo(25.0));
            Assert.That(snapshot.SimpleDdgiReceiverCache.TimingEligible,
                Is.False);
        });
    }

    [Test]
    public void SnapshotAndMemoryAudit_PreserveAuthoritativePackedStorageEvidence()
    {
        SimpleDdgiStorageDiagnostics storage =
            SimpleDdgiStorageDiagnostics.Unavailable with
            {
                IsAvailable = true,
                PackingMode = SimpleDdgiStoragePackingMode.Packed,
                AbiVersion = SimpleDdgiStorageAbiVersion.Packed,
                DirectionCodebookVersion = SimpleDdgiDirectionCodebook.Version,
                CanonicalIrradianceFormat = "RGBA16F",
                CanonicalVisibilityFormat = "RG16F",
                CanonicalIrradianceBytes = 100UL,
                CanonicalVisibilityBytes = 200UL,
                SourceCacheBytes = 400UL,
                SourceCacheCompact28Bytes = 280UL,
                SourceCacheCompact24Bytes = 120UL,
                RayScratchStrideBytes = 20UL,
                RayScratchBytes = 600UL,
                MirrorCoverageMode =
                    SimpleDdgiSampledAtlasCoverageMode.ReceiverRelevant,
                MirrorAdmittedProbeCount = 12,
                MirrorProvisionedProbeCount = 256,
                MirrorTotalBytes = 500UL,
                MirrorAllocatedBytes = 550UL,
                StorageLayoutFingerprint = 17UL,
                MirrorLayoutFingerprint = 19UL,
                MirrorAllocationGeneration = 23UL,
                ValidationCounters =
                    SimpleDdgiStorageValidationCounters.Empty with
                    {
                        ReadbackValid = 1,
                        MirrorImageHitCount = 31u
                    }
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiStorage = storage,
            SimpleDdgiAtlasBytes = 99_999UL,
            SimpleDdgiSampledAtlasImageBytes = 88_888UL,
            SimpleDdgiTransportIrradianceAtlasBytes = 300UL,
            SimpleDdgiTransportSourceCacheBytes = 77_777UL
        };

        PerformanceGlobalIlluminationSnapshot snapshot =
            PerformanceSnapshotWriter.CreateGlobalIlluminationSnapshot(diagnostics);
        PerformanceMemoryOwnershipAudit audit =
            PerformanceSnapshotWriter.CreateMemoryOwnershipAudit(
                diagnostics,
                MemoryBudgetSnapshot.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SimpleDdgiStorage, Is.EqualTo(storage));
            Assert.That(snapshot.SimpleDdgiStorage.ValidationCounters.ReadbackValid,
                Is.EqualTo(1));
            Assert.That(snapshot.SimpleDdgiStorage.ValidationCounters.MirrorImageHitCount,
                Is.EqualTo(31u));
            Assert.That(audit.CanonicalDdgiAtlasBytes, Is.EqualTo(300UL));
            Assert.That(audit.SampledAtlasMirrorBytes, Is.EqualTo(550UL));
            Assert.That(audit.TransportBytes, Is.EqualTo(700UL));
        });
    }

    [Test]
    public void SnapshotWriter_RoundTripsContentModesAndFoliageLodEvidence()
    {
        DdgiContentRuntimeSnapshot content =
            DdgiContentRuntimeSnapshot.Disabled with
            {
                ConfiguredFeatures = DdgiContentFeature.ManyLightSampling |
                    DdgiContentFeature.FoliageGeometry |
                    DdgiContentFeature.DirectionalRadiance,
                ActiveFeatures = DdgiContentFeature.ManyLightSampling |
                    DdgiContentFeature.FoliageGeometry,
                RequestedLocalLightSamplingMode =
                    SimpleDdgiLocalLightSamplingMode.LightTree,
                EffectiveLocalLightSamplingMode =
                    SimpleDdgiLocalLightSamplingMode.LightTree,
                RequestedDirectionalRadianceMode =
                    SimpleDdgiDirectionalRadianceMode.L2,
                EffectiveDirectionalRadianceMode =
                    SimpleDdgiDirectionalRadianceMode.Off,
                DirectionalRadianceFallbackReason =
                    "device profile was not qualified",
                RequestedFoliageGeometryMode =
                    DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                EffectiveFoliageGeometryMode =
                    DdgiFoliageGeometryMode.AuthoredAndProceduralProxy,
                LightBufferRevision = 101,
                RaySceneResourceGeneration = 7,
                RaySceneContentEpoch = 23,
                LightTree = SimpleDdgiLightTreeRuntimeDiagnostics.Disabled with
                {
                    PublicationValidationFailureCount = 2,
                    FallbackReason = "completed publication failed checksum"
                }
            };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            ContentDependentDdgi = content,
            DdgiFoliageProxyPatchBufferBytes = 1_280,
            DdgiFoliageProxyRequestedRepresentedInstanceCount = 4_000,
            DdgiFoliageProxyDensityError = 0.015f,
            DdgiFoliageProxyWindAgeSeconds = 0.125f,
            DdgiFoliageProxyNearCardCount = 120,
            DdgiFoliageProxyMidCardCount = 60,
            DdgiFoliageProxyFarCardCount = 15,
            DdgiFoliageProxyExcludedPatchCount = 3,
            DdgiFoliageProxyLodPolicyVersion = 1
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.ContentDependentDdgi, Is.EqualTo(content));
                Assert.That(snapshot.Diagnostics.ContentDependentDdgi,
                    Is.EqualTo(content));
                Assert.That(snapshot.Foliage.DdgiProxyPatchBufferBytes,
                    Is.EqualTo(1_280));
                Assert.That(snapshot.Foliage.DdgiProxyRequestedRepresentedInstanceCount,
                    Is.EqualTo(4_000));
                Assert.That(snapshot.Foliage.DdgiProxyDensityError,
                    Is.EqualTo(0.015f));
                Assert.That(snapshot.Foliage.DdgiProxyWindAgeSeconds,
                    Is.EqualTo(0.125f));
                Assert.That(snapshot.Foliage.DdgiProxyNearCardCount,
                    Is.EqualTo(120));
                Assert.That(snapshot.Foliage.DdgiProxyMidCardCount,
                    Is.EqualTo(60));
                Assert.That(snapshot.Foliage.DdgiProxyFarCardCount,
                    Is.EqualTo(15));
                Assert.That(snapshot.Foliage.DdgiProxyExcludedPatchCount,
                    Is.EqualTo(3));
                Assert.That(snapshot.Foliage.DdgiProxyLodPolicyVersion,
                    Is.EqualTo(1));
                Assert.That(snapshot.Warnings,
                    Has.Some.Contains("publication validation failed"));
                Assert.That(snapshot.Warnings,
                    Has.Some.Contains("directional radiance fell back"));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaThreeMissingAdvancedModeStateToDisabled()
    {
        GiRoadmapExperimentDiagnostics roadmap = new(
            GiExperimentAdmission.Missing("B5", "legacy"),
            GiExperimentAdmission.Missing("C1", "legacy"),
            GiExperimentAdmission.Missing("C2", "legacy"),
            GiExperimentAdmission.Missing("C3", "legacy"),
            GiExperimentAdmission.Missing("C4", "legacy"),
            GiExperimentAdmission.Missing("C5", "legacy"))
        {
            Modes = new GiRoadmapExperimentModeDiagnostics(
                new GiExperimentModeState<SimpleDdgiReceiverFeedbackMode>(
                    SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                    SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                    SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                    SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                    GiExperimentFallbackReason.None,
                    "active",
                    "b1-evidence"),
                GiExperimentModeState<DdgiOpacityMicromapMode>.Disabled(
                    DdgiOpacityMicromapMode.Off),
                GiExperimentModeState<SimpleDdgiDirectionalGuidingMode>.Disabled(
                    SimpleDdgiDirectionalGuidingMode.Off),
                GiExperimentModeState<GiCausticMode>.Disabled(GiCausticMode.Off),
                GiExperimentModeState<SimpleDdgiNearFieldResidualMode>.Disabled(
                    SimpleDdgiNearFieldResidualMode.Off))
        };
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = roadmap
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Expected a snapshot JSON object.");
            RemoveRoadmapModes(root);
            root["SchemaVersion"] = 3;
            root["OriginalSchemaVersion"] = 3;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot = new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(3));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments.DirectionalFog,
                    Is.EqualTo(roadmap.DirectionalFog));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments.Modes,
                    Is.EqualTo(GiRoadmapExperimentModeDiagnostics.Disabled));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments.Modes,
                    Is.EqualTo(GiRoadmapExperimentModeDiagnostics.Disabled));
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.Readback.State,
                    Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.Disabled));
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.Memory,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMemoryTelemetry.Empty));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotWriter_RoundTripsAuthoritativeNearFieldResidualTelemetry()
    {
        SimpleDdgiNearFieldResidualDiagnostics telemetry = CreateNearFieldResidualTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiNearFieldResidual = telemetry
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot = new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .Timings.TotalMicroseconds,
                    Is.EqualTo(41UL));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .Trace.RayHitCount,
                    Is.EqualTo(17UL));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .History.AcceptedHistoryCount,
                    Is.EqualTo(11UL));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .Tiles.CompactedTileCount,
                    Is.EqualTo(7U));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .AdaptiveResolution.ActiveExtent.Scale,
                    Is.EqualTo(SimpleDdgiNearFieldResidualExecutionScale.Quarter));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual
                        .AdaptiveResolution.LastP95Microseconds,
                    Is.EqualTo(400UL));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaFourNearFieldTelemetryToDisabled()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiNearFieldResidual = CreateNearFieldResidualTelemetry()
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Expected a snapshot JSON object.");
            RemoveNearFieldResidualTelemetry(root);
            root["SchemaVersion"] = 4;
            root["OriginalSchemaVersion"] = 4;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot = new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(4));
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.Readback.State,
                    Is.EqualTo(SimpleDdgiNearFieldResidualReadbackState.Disabled));
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.IsAuthoritativeReadback,
                    Is.False);
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.Memory,
                    Is.EqualTo(SimpleDdgiNearFieldResidualMemoryTelemetry.Empty));
                Assert.That(snapshot.Diagnostics.SimpleDdgiNearFieldResidual.Timings,
                    Is.EqualTo(SimpleDdgiNearFieldResidualStageTimings.Empty));
                Assert.That(snapshot.GlobalIllumination.SimpleDdgiNearFieldResidual,
                    Is.EqualTo(snapshot.Diagnostics.SimpleDdgiNearFieldResidual));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotWriter_RoundTripsFenceCompleteOpacityMicromapTelemetry()
    {
        OpacityMicromapGpuRuntimeSnapshot telemetry =
            CreateOpacityMicromapRuntimeTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty with
            {
                AdvancedExperimentMemory = telemetry.Memory
            },
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                OpacityMicromapRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(
                    snapshot.Diagnostics.GiRoadmapExperiments
                        .OpacityMicromapRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(
                    snapshot.GlobalIllumination.GiRoadmapExperiments
                        .OpacityMicromapRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(
                    snapshot.Diagnostics.GiRoadmapExperiments.AllocatedBytes,
                    Is.EqualTo(telemetry.AllocatedBytes));
                Assert.That(
                    snapshot.Diagnostics.SimpleDdgiContentMemory
                        .AdvancedExperimentMemory,
                    Is.EqualTo(telemetry.Memory));
                Assert.That(
                    snapshot.GlobalIllumination.SimpleDdgiContentMemory,
                    Is.EqualTo(snapshot.Diagnostics.SimpleDdgiContentMemory));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaFiveOpacityMicromapTelemetryToDisabled()
    {
        SimpleDdgiNearFieldResidualDiagnostics nearField =
            CreateNearFieldResidualTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiNearFieldResidual = nearField,
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                OpacityMicromapRuntime =
                    CreateOpacityMicromapRuntimeTelemetry()
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            root["SchemaVersion"] = 5;
            root["OriginalSchemaVersion"] = 5;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(5));
                Assert.That(
                    snapshot.Diagnostics.GiRoadmapExperiments
                        .OpacityMicromapRuntime,
                    Is.EqualTo(OpacityMicromapGpuRuntimeSnapshot.Disabled));
                Assert.That(
                    snapshot.GlobalIllumination.GiRoadmapExperiments
                        .OpacityMicromapRuntime,
                    Is.EqualTo(OpacityMicromapGpuRuntimeSnapshot.Disabled));
                Assert.That(
                    snapshot.Diagnostics.SimpleDdgiNearFieldResidual,
                    Is.EqualTo(nearField),
                    "Schema v5 already owns C5 telemetry and migration must preserve it.");
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaSixWithContentEvidenceUnavailable()
    {
        OpacityMicromapGpuRuntimeSnapshot telemetry =
            CreateOpacityMicromapRuntimeTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiContentMemory = SimpleDdgiContentMemoryPlan.Empty with
            {
                AdvancedExperimentMemory = telemetry.Memory
            },
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                OpacityMicromapRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            RemoveOpacityMicromapContentEvidence(root);
            root["SchemaVersion"] = 6;
            root["OriginalSchemaVersion"] = 6;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);
            OpacityMicromapGpuRuntimeSnapshot migrated = snapshot.Diagnostics
                .GiRoadmapExperiments
                .OpacityMicromapRuntime;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(6));
                Assert.That(migrated.BuildCount,
                    Is.EqualTo(telemetry.BuildCount));
                Assert.That(migrated.PublicationCount,
                    Is.EqualTo(telemetry.PublicationCount));
                Assert.That(migrated.Memory, Is.EqualTo(telemetry.Memory));
                Assert.That(migrated.Content,
                    Is.EqualTo(OpacityMicromapContentDiagnostics.Unavailable));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .OpacityMicromapRuntime,
                    Is.EqualTo(migrated));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotWriter_RoundTripsFenceCompleteDirectionalGuidingTelemetry()
    {
        SimpleDdgiDirectionalGuidingDiagnostics telemetry =
            CreateDirectionalGuidingTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                DirectionalGuidingRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .DirectionalGuidingRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .DirectionalGuidingRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .AllocatedBytes,
                    Is.EqualTo(telemetry.Memory.AllocatedBytes));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .DirectionalGuidingRuntime
                        .HasAuthoritativeSampleReadback,
                    Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void DirectionalGuidingTelemetry_RetainsPriorCompletionWhileNewFrameHasNoSamples()
    {
        SimpleDdgiDirectionalGuidingDiagnostics completed =
            CreateDirectionalGuidingTelemetry();
        SimpleDdgiDirectionalGuidingDiagnostics pending = completed with
        {
            State = SimpleDdgiGuidingTelemetryState.PendingGpuReadback,
            Frame = completed.Frame with
            {
                FramePrepared = true,
                FrameSerial = completed.Frame.FrameSerial + 1UL,
                GuidedProbeCount = 0,
                TrainingRecordCount = 0U,
                SampleRequestCount = 0,
                State = "prepared-gpu-resident"
            },
            Reason = "prepared-gpu-resident"
        };

        SimpleDdgiDirectionalGuidingDiagnostics normalized =
            pending.NormalizeForPersistence();

        Assert.Multiple(() =>
        {
            Assert.That(normalized.State,
                Is.EqualTo(SimpleDdgiGuidingTelemetryState.PendingGpuReadback));
            Assert.That(normalized.Reason,
                Is.EqualTo("prepared-gpu-resident"));
            Assert.That(normalized.Frame.CompletedFrameSerial,
                Is.EqualTo(completed.Frame.CompletedFrameSerial));
            Assert.That(normalized.Frame.CompletedSampleCount,
                Is.EqualTo(completed.Frame.CompletedSampleCount));
            Assert.That(normalized.Frame.SampleRequestCount, Is.Zero);
            Assert.That(normalized.Frame.SampleReadbackValid, Is.True);
        });
    }

    [Test]
    public void SnapshotReader_MigratesSchemaSevenDirectionalGuidingTelemetryToDisabled()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                DirectionalGuidingRuntime = CreateDirectionalGuidingTelemetry()
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            RemoveDirectionalGuidingTelemetry(root);
            root["SchemaVersion"] = 7;
            root["OriginalSchemaVersion"] = 7;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(7));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .DirectionalGuidingRuntime,
                    Is.EqualTo(SimpleDdgiDirectionalGuidingDiagnostics.Disabled));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .DirectionalGuidingRuntime,
                    Is.EqualTo(SimpleDdgiDirectionalGuidingDiagnostics.Disabled));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotWriter_RoundTripsFenceValidatedCausticTelemetry()
    {
        GiCausticDiagnostics telemetry =
            GiCausticDiagnosticsTests.CreateDiagnostics();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                CausticRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .CausticRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .CausticRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .AllocatedBytes,
                    Is.EqualTo(telemetry.Memory.AllocatedBytes));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .CausticRuntime.HasAuthoritativePublication,
                    Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaEightCausticTelemetryToDisabled()
    {
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                CausticRuntime = GiCausticDiagnosticsTests.CreateDiagnostics()
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            RemoveCausticTelemetry(root);
            root["SchemaVersion"] = 8;
            root["OriginalSchemaVersion"] = 8;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(8));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .CausticRuntime,
                    Is.EqualTo(GiCausticDiagnostics.Disabled));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .CausticRuntime,
                    Is.EqualTo(GiCausticDiagnostics.Disabled));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotWriter_RoundTripsFenceValidatedReceiverFeedbackTelemetry()
    {
        SimpleDdgiReceiverFeedbackDiagnostics telemetry =
            SimpleDdgiReceiverFeedbackDiagnosticsTests
                .CreateReadableDiagnostics();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                ReceiverFeedbackRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .ReceiverFeedbackRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .ReceiverFeedbackRuntime,
                    Is.EqualTo(telemetry));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .ReceiverFeedbackRuntime.HasAuthoritativePublication,
                    Is.True);
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .AllocatedBytes,
                    Is.EqualTo(telemetry.Memory.AllocatedBytes));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaNineReceiverFeedbackToDisabled()
    {
        SimpleDdgiReceiverFeedbackDiagnostics telemetry =
            SimpleDdgiReceiverFeedbackDiagnosticsTests
                .CreateReadableDiagnostics();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            GiRoadmapExperiments = GiRoadmapExperimentDiagnostics.Disabled with
            {
                Modes = GiRoadmapExperimentModeDiagnostics.Disabled with
                {
                    ReceiverFeedback = new GiExperimentModeState<
                        SimpleDdgiReceiverFeedbackMode>(
                        SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                        SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                        SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                        SimpleDdgiReceiverFeedbackMode.ExactCompacted,
                        GiExperimentFallbackReason.None,
                        "active",
                        string.Empty)
                },
                ReceiverFeedbackRuntime = telemetry
            }
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            RemoveReceiverFeedbackTelemetry(root);
            root["SchemaVersion"] = 9;
            root["OriginalSchemaVersion"] = 9;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(9));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .Modes.ReceiverFeedback.EffectiveMode,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
                Assert.That(snapshot.Diagnostics.GiRoadmapExperiments
                        .ReceiverFeedbackRuntime,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackDiagnostics.Disabled));
                Assert.That(snapshot.GlobalIllumination.GiRoadmapExperiments
                        .ReceiverFeedbackRuntime,
                    Is.EqualTo(SimpleDdgiReceiverFeedbackDiagnostics.Disabled));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void SnapshotReader_MigratesSchemaTenWithoutTrustingC5Savings()
    {
        SimpleDdgiNearFieldResidualDiagnostics telemetry =
            CreateNearFieldResidualTelemetry();
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            SimpleDdgiNearFieldResidual = telemetry
        };
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfPerformanceSnapshotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string path = new PerformanceSnapshotWriter().Write(
                directory,
                diagnostics,
                RenderBudgetSnapshot.Empty);
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new InvalidOperationException(
                    "Expected a snapshot JSON object.");
            root["SchemaVersion"] = 10;
            root["OriginalSchemaVersion"] = 10;
            File.WriteAllText(path, root.ToJsonString());

            PerformanceSnapshot snapshot =
                new PerformanceSnapshotReader().Read(path);
            SimpleDdgiNearFieldResidualMemoryTelemetry memory = snapshot
                .Diagnostics.SimpleDdgiNearFieldResidual.Memory;

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.SchemaVersion,
                    Is.EqualTo(PerformanceSnapshot.CurrentSchemaVersion));
                Assert.That(snapshot.OriginalSchemaVersion, Is.EqualTo(10));
                Assert.That(memory.AllocatedBytes, Is.EqualTo(1_920UL));
                Assert.That(memory.PackedValidityAndNormalBytes, Is.Zero);
                Assert.That(memory.AliasedFilterScratchBytes, Is.Zero);
                Assert.That(memory.PhysicalFilterScratchImageCount, Is.Zero);
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SimpleDdgiDirectionalGuidingDiagnostics
        CreateDirectionalGuidingTelemetry() => new()
        {
            State = SimpleDdgiGuidingTelemetryState.Available,
            Runtime = new SimpleDdgiGuidingGpuRuntimeDiagnostics(
                SimpleDdgiGuidingGpuCapabilityReason.None,
                SourceCacheHandshakeAvailable: true,
                DescriptorContextRegistered: true,
                HeaderReadbackPending: false,
                new SimpleDdgiGuidingRuntimeSnapshot(
                    SimpleDdgiGuidingResourceState.Readable,
                    IsEffectivelyEnabled: true,
                    AllocationEpoch: 9UL,
                    AllocatedBytes: 3_328UL,
                    DescriptorCount: 3U,
                    ReadBankIndex: 0,
                    WriteBankIndex: 1,
                    ReadBankGeneration: 5U,
                    PendingBankGeneration: 0U,
                    PublishedProbeCount: 4,
                    Reason: "guiding-readable"),
                Detail: "guiding-readable"),
            Frame = new SimpleDdgiGuidingFrameCoordinatorDiagnostics(
                Configured: true,
                FramePrepared: false,
                SampleRecorded: true,
                TrainRecorded: true,
                BuildRecorded: true,
                ValidateRecorded: true,
                FrameSerial: 71UL,
                GuidedProbeCount: 4,
                TrainingRecordCount: 64U,
                SampleRequestCount: 32,
                UploadedBytes: 2_048UL,
                WorkspaceBytes: 8_192UL,
                State: "guiding-readback-complete")
            {
                CompletedFrameSerial = 71UL,
                SampleReadbackValid = true,
                CompletedSampleCount = 32,
                SampleValidationCounters = default,
                SampleTelemetry = new SimpleDdgiGuidingSampleTelemetry(
                    RequestCount: 32U,
                    ValidSampleCount: 32U,
                    MaintenanceSampleCount: 8U,
                    MixtureUniformSampleCount: 8U,
                    MixtureGuidedSampleCount: 16U,
                    UniformFallbackSampleCount: 0U,
                    MinimumPdf: 1.0f / 3.5f,
                    MaximumPdf: 0.5f,
                    MinimumInversePdf: 2.0f,
                    P50InversePdfUpperBound: 4.0f,
                    P95InversePdfUpperBound: 4.0f,
                    P99InversePdfUpperBound: 4.0f,
                    MaximumInversePdf: 3.5f,
                    InversePdfHistogram:
                        new SimpleDdgiGuidingInversePdfHistogram(
                            0U, 0U, 0U, 0U,
                            0U, 0U, 0U, 0U,
                            0U, 32U, 0U, 0U,
                            0U, 0U, 0U, 0U)),
                DistributionPublicationSucceeded = true
            },
            Timings = new SimpleDdgiGuidingStageTimings(
                SampleMicroseconds: 7L,
                TrainMicroseconds: 11L,
                BuildMicroseconds: 13L,
                ValidateMicroseconds: 3L,
                AvailableStages: SimpleDdgiGuidingTimedStage.All),
            Memory = new SimpleDdgiGuidingMemoryTelemetry(
                SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingHistoryBanks,
                    requiredBytes: 4_096UL,
                    allocatedBytes: 4_096UL,
                    peakLiveBytes: 4_096UL),
                SimpleDdgiAdvancedMemoryUsage.Admitted(
                    SimpleDdgiAdvancedMemoryCategory
                        .DirectionalGuidingBuildScratch,
                    requiredBytes: 8_192UL,
                    allocatedBytes: 8_192UL,
                    peakLiveBytes: 8_192UL)),
            Reason = "directional-guiding-fence-complete-sample-available"
        };

    private static OpacityMicromapGpuRuntimeSnapshot
        CreateOpacityMicromapRuntimeTelemetry() => new(
            Requested: true,
            Supported: true,
            Enabled: true,
            RegisteredCandidateCount: 3,
            PendingVariantCount: 1,
            PublishedVariantCount: 1,
            DeferredRetryCount: 1,
            AllocatedBytes: 65_536UL,
            PeakAllocatedBytes: 98_304UL,
            RetiredButLiveBytes: 16_384UL,
            BuildCount: 7UL,
            PublicationCount: 4UL,
            FallbackCount: 2UL,
            MicromapCompactionCount: 3UL,
            BlasCompactionCount: 2UL,
            QueryFailureCount: 1UL,
            Detail: "opacity-micromap-variant-published")
        {
            VariantCacheHitCount = 101UL,
            VariantCacheMissCount = 9UL,
            VariantEvictionCount = 3UL,
            VariantCapFallbackCount = 2UL,
            Content = new OpacityMicromapContentDiagnostics(
                Authoritative: true,
                RegisteredMeshCount: 3,
                UniqueVariantCount: 2,
                RejectedRegistrationCount: 5UL,
                StaleMaterialRegistrationCount: 1,
                AmbiguousContentKeyCount: 1,
                PrimitiveCount: 6UL,
                MaterialContractCount: 2UL,
                OmmDataBytes: 128UL,
                IndexBytes: 24UL,
                DescriptorBytes: 48UL,
                ClassifiedPayloadCount: 1,
                UnclassifiedPayloadCount: 1,
                OpaqueMicrotriangleCount: 17UL,
                TransparentMicrotriangleCount: 11UL,
                UnknownOpaqueMicrotriangleCount: 3UL,
                UnknownTransparentMicrotriangleCount: 1UL,
                MaximumSubdivisionLevel: 2U,
                SubdivisionHistogram:
                    new OpacityMicromapSubdivisionHistogram(
                        0UL, 2UL, 4UL, 0UL,
                        0UL, 0UL, 0UL, 0UL,
                        0UL, 0UL, 0UL, 0UL,
                        0UL, 0UL, 0UL, 0UL),
                Detail: "opacity-micromap-content-generation-authoritative"),
            Memory = SimpleDdgiAdvancedExperimentMemoryPlan.Empty with
            {
                OpacityMicromapResidentData =
                    new SimpleDdgiAdvancedMemoryUsage(
                        SimpleDdgiAdvancedMemoryCategory
                            .OpacityMicromapResidentData,
                        RequestedBytes: 65_536UL,
                        RequiredBytes: 65_536UL,
                        AdmittedBytes: 65_536UL,
                        AllocatedBytes: 49_152UL,
                        PeakLiveBytes: 65_536UL,
                        RetiredButLiveBytes: 16_384UL,
                        FallbackBytes: 0UL,
                        FallbackReason: GiExperimentFallbackReason.None),
                OpacityMicromapBuildScratch =
                    new SimpleDdgiAdvancedMemoryUsage(
                        SimpleDdgiAdvancedMemoryCategory
                            .OpacityMicromapBuildScratch,
                        RequestedBytes: 32_768UL,
                        RequiredBytes: 32_768UL,
                        AdmittedBytes: 32_768UL,
                        AllocatedBytes: 16_384UL,
                        PeakLiveBytes: 32_768UL,
                        RetiredButLiveBytes: 0UL,
                        FallbackBytes: 0UL,
                        FallbackReason: GiExperimentFallbackReason.None)
            }
        };

    private static void RemoveRoadmapModes(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            if (objectNode.TryGetPropertyValue("GiRoadmapExperiments", out JsonNode? roadmap) &&
                roadmap is JsonObject roadmapObject)
            {
                roadmapObject.Remove("Modes");
            }
            foreach (JsonNode? child in objectNode.Select(static entry => entry.Value).ToArray())
            {
                if (child != null)
                    RemoveRoadmapModes(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child != null)
                    RemoveRoadmapModes(child);
            }
        }
    }

    private static void RemoveNearFieldResidualTelemetry(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            objectNode.Remove("SimpleDdgiNearFieldResidual");
            foreach (JsonNode? child in objectNode.Select(static entry => entry.Value).ToArray())
            {
                if (child != null)
                    RemoveNearFieldResidualTelemetry(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child != null)
                    RemoveNearFieldResidualTelemetry(child);
            }
        }
    }

    private static void RemoveDirectionalGuidingTelemetry(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            if (objectNode.TryGetPropertyValue(
                    "GiRoadmapExperiments",
                    out JsonNode? roadmap) &&
                roadmap is JsonObject roadmapObject)
            {
                roadmapObject.Remove("DirectionalGuidingRuntime");
            }
            foreach (JsonNode? child in objectNode
                         .Select(static entry => entry.Value)
                         .ToArray())
            {
                if (child != null)
                    RemoveDirectionalGuidingTelemetry(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child != null)
                    RemoveDirectionalGuidingTelemetry(child);
            }
        }
    }

    private static void RemoveCausticTelemetry(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            if (objectNode.TryGetPropertyValue(
                    "GiRoadmapExperiments",
                    out JsonNode? roadmap) &&
                roadmap is JsonObject roadmapObject)
            {
                roadmapObject.Remove("CausticRuntime");
            }
            foreach (JsonNode? child in objectNode
                         .Select(static entry => entry.Value)
                         .ToArray())
            {
                if (child != null)
                    RemoveCausticTelemetry(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child != null)
                    RemoveCausticTelemetry(child);
            }
        }
    }

    private static void RemoveReceiverFeedbackTelemetry(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            if (objectNode.TryGetPropertyValue(
                    "GiRoadmapExperiments",
                    out JsonNode? roadmap) &&
                roadmap is JsonObject roadmapObject)
            {
                roadmapObject.Remove("ReceiverFeedbackRuntime");
            }
            foreach (JsonNode? child in objectNode
                         .Select(static entry => entry.Value)
                         .ToArray())
            {
                if (child != null)
                    RemoveReceiverFeedbackTelemetry(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child != null)
                    RemoveReceiverFeedbackTelemetry(child);
            }
        }
    }

    private static void RemoveOpacityMicromapContentEvidence(JsonNode node)
    {
        if (node is JsonObject objectNode)
        {
            if (objectNode.TryGetPropertyValue(
                    "OpacityMicromapRuntime",
                    out JsonNode? runtimeNode) &&
                runtimeNode is JsonObject runtimeObject)
            {
                runtimeObject.Remove("Content");
            }
            foreach (JsonNode? child in objectNode
                         .Select(static entry => entry.Value)
                         .ToArray())
            {
                if (child is not null)
                    RemoveOpacityMicromapContentEvidence(child);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (child is not null)
                    RemoveOpacityMicromapContentEvidence(child);
            }
        }
    }

    private static SimpleDdgiNearFieldResidualDiagnostics CreateNearFieldResidualTelemetry() =>
        SimpleDdgiNearFieldResidualDiagnostics.CreateAuthoritative(
            completedFrameSerial: 44UL,
            ageFrames: 1U,
            memory: new SimpleDdgiNearFieldResidualMemoryTelemetry(
                2_048UL, 2_048UL, 1_920UL, 2_048UL, 128UL)
            {
                PackedValidityAndNormalBytes = 384UL,
                AliasedFilterScratchBytes = 512UL,
                PhysicalFilterScratchImageCount = 1
            },
            timings: new SimpleDdgiNearFieldResidualStageTimings(
                3UL, 5UL, 7UL, 11UL, 15UL),
            trace: new SimpleDdgiNearFieldResidualTraceTelemetry(
                32UL, 24UL, 17UL, 7UL, 2UL, 1UL, 0UL, 3UL, 5UL, 7UL, 11UL, 13UL, 0UL),
            history: new SimpleDdgiNearFieldResidualHistoryTelemetry(
                16UL, 11UL, 5UL, 0UL, 1UL, 1UL, 2UL, 1UL, 0UL, 0UL, 3UL, 0.25, 0.5),
            residualEnergy: new SimpleDdgiNearFieldResidualEnergyTelemetry(
                32UL, -0.5, 1.5, 2.5, 0.75, 0.125, 0UL),
            tiles: new SimpleDdgiNearFieldResidualTileTelemetry(
                16U, 12U, 7U, 5U, 0U, 112UL),
            captureIdentifiers: new SimpleDdgiNearFieldResidualCaptureIdentifiers(
                "c5-debug-capture", "c5-reference-capture"))
        with
        {
            AdaptiveResolution =
                new SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry(
                    SampledExtent:
                        new SimpleDdgiNearFieldResidualExecutionExtent(
                            480,
                            270,
                            SimpleDdgiNearFieldResidualExecutionScale.Quarter,
                            1U),
                    ActiveExtent:
                        new SimpleDdgiNearFieldResidualExecutionExtent(
                            480,
                            270,
                            SimpleDdgiNearFieldResidualExecutionScale.Quarter,
                            1U),
                    MaximumScale:
                        SimpleDdgiNearFieldResidualExecutionScale.Half,
                    LastP95Microseconds: 400UL,
                    AuthoritativeTimingSampleCount: 240UL,
                    WindowSampleCount: 120U,
                    PromotionWindowStreak: 2U,
                    PromotionCount: 0U,
                    DemotionCount: 0U,
                    ResolutionChangedAfterSample: false)
        };
}

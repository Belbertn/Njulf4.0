using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DebugOverlayCatalogTests
{
    private static readonly DebugOverlayMode[] ExpectedCycle =
    [
        DebugOverlayMode.None,
        DebugOverlayMode.LightTiles,
        DebugOverlayMode.DirectionalShadowCascades,
        DebugOverlayMode.ReflectionProbeVolumes,
        DebugOverlayMode.DdgiProbeVolumes,
        DebugOverlayMode.DdgiProbeSpheres,
        DebugOverlayMode.DdgiProbeActivity,
        DebugOverlayMode.DdgiUpdatedProbes,
        DebugOverlayMode.DdgiProbeRelocation,
        DebugOverlayMode.DdgiProbeAge,
        DebugOverlayMode.DdgiPhysicalSlots,
        DebugOverlayMode.DdgiCascadeBounds,
        DebugOverlayMode.DdgiNewlyExposedCells,
        DebugOverlayMode.DdgiFrustumPriority,
        DebugOverlayMode.DdgiUpdateReasons,
        DebugOverlayMode.DecalVolumes,
        DebugOverlayMode.ObjectBounds,
        DebugOverlayMode.MeshletBounds,
        DebugOverlayMode.SelectedObject
    ];

    [Test]
    public void NumericIdentities_AreStableThroughAppendedProbeSpheres()
    {
        DebugOverlayMode[] values =
        [
            DebugOverlayMode.None,
            DebugOverlayMode.LightTiles,
            DebugOverlayMode.DirectionalShadowCascades,
            DebugOverlayMode.ReflectionProbeVolumes,
            DebugOverlayMode.DdgiProbeVolumes,
            DebugOverlayMode.DecalVolumes,
            DebugOverlayMode.ObjectBounds,
            DebugOverlayMode.MeshletBounds,
            DebugOverlayMode.SelectedObject,
            DebugOverlayMode.MaterialInspection,
            DebugOverlayMode.PassTimings,
            DebugOverlayMode.GpuMemory,
            DebugOverlayMode.DdgiProbeActivity,
            DebugOverlayMode.DdgiUpdatedProbes,
            DebugOverlayMode.DdgiProbeRelocation,
            DebugOverlayMode.DdgiProbeAge,
            DebugOverlayMode.DdgiPhysicalSlots,
            DebugOverlayMode.DdgiCascadeBounds,
            DebugOverlayMode.DdgiNewlyExposedCells,
            DebugOverlayMode.DdgiFrustumPriority,
            DebugOverlayMode.DdgiSafetyRefresh,
            DebugOverlayMode.DdgiCascadeBlend,
            DebugOverlayMode.DdgiUpdateReasons,
            DebugOverlayMode.DdgiProbeSpheres
        ];

        Assert.That(values.Select(static value => (uint)value),
            Is.EqualTo(Enumerable.Range(0, 24).Select(static value => (uint)value)));
    }

    [Test]
    public void ActiveCycle_IsExactUniqueAndHasRegisteredRenderers()
    {
        DebugOverlayDescriptor[] descriptors =
            DebugOverlayCatalog.ActiveCycle.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(descriptors.Select(static descriptor => descriptor.Mode),
                Is.EqualTo(ExpectedCycle));
            Assert.That(descriptors.Select(static descriptor => descriptor.Mode).Distinct().Count(),
                Is.EqualTo(descriptors.Length));
            Assert.That(descriptors.Select(static descriptor => descriptor.CycleOrder),
                Is.EqualTo(Enumerable.Range(0, ExpectedCycle.Length)));
            Assert.That(descriptors.All(static descriptor => descriptor.IsActive), Is.True);
            Assert.That(descriptors.Where(static descriptor =>
                    descriptor.Mode != DebugOverlayMode.None)
                .All(static descriptor =>
                    descriptor.RendererKind != DebugOverlayRendererKind.None), Is.True);
            Assert.That(descriptors.All(static descriptor =>
                !string.IsNullOrWhiteSpace(descriptor.Legend) &&
                !string.IsNullOrWhiteSpace(descriptor.NoDataGuidance)), Is.True);
        });
    }

    [Test]
    public void ForwardAndReverseTraversal_AreExactInversesAndWrapAtNone()
    {
        foreach (DebugOverlayMode mode in ExpectedCycle)
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    DebugOverlayCatalog.Next(
                        DebugOverlayCatalog.Next(mode),
                        reverse: true),
                    Is.EqualTo(mode));
                Assert.That(
                    DebugOverlayCatalog.Next(
                        DebugOverlayCatalog.Next(mode, reverse: true)),
                    Is.EqualTo(mode));
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(DebugOverlayCatalog.Next(DebugOverlayMode.SelectedObject),
                Is.EqualTo(DebugOverlayMode.None));
            Assert.That(DebugOverlayCatalog.Next(DebugOverlayMode.None, reverse: true),
                Is.EqualTo(DebugOverlayMode.SelectedObject));
        });
    }

    [TestCase(DebugOverlayMode.MaterialInspection)]
    [TestCase(DebugOverlayMode.PassTimings)]
    [TestCase(DebugOverlayMode.GpuMemory)]
    [TestCase(DebugOverlayMode.DdgiSafetyRefresh)]
    [TestCase(DebugOverlayMode.DdgiCascadeBlend)]
    public void RetiredModes_ResolveSafelyAndNeverEnterCycle(DebugOverlayMode mode)
    {
        DebugOverlayDescriptor descriptor = DebugOverlayCatalog.Get(mode);

        Assert.Multiple(() =>
        {
            Assert.That(descriptor.IsActive, Is.False);
            Assert.That(descriptor.RetirementReason, Is.Not.Empty);
            Assert.That(DebugOverlayCatalog.ResolveRendererMode(mode),
                Is.EqualTo(DebugOverlayMode.None));
            Assert.That(ExpectedCycle, Does.Not.Contain(mode));
        });
    }

    [Test]
    public void CpuSnapshotRequirements_AreCentralAndExact()
    {
        DebugOverlayMode[] requiringSnapshots = DebugOverlayCatalog.Descriptors
            .Where(static descriptor => descriptor.RequiresCpuSnapshots)
            .Select(static descriptor => descriptor.Mode)
            .ToArray();

        Assert.That(requiringSnapshots, Is.EquivalentTo(new[]
        {
            DebugOverlayMode.DecalVolumes,
            DebugOverlayMode.ObjectBounds,
            DebugOverlayMode.MeshletBounds,
            DebugOverlayMode.SelectedObject
        }));
    }

    [Test]
    public void FrameStatus_DefaultIsShippingSafe()
    {
        DebugOverlayFrameStatus status = default;

        Assert.Multiple(() =>
        {
            Assert.That(status.Mode, Is.EqualTo(DebugOverlayMode.None));
            Assert.That(status.Availability, Is.EqualTo(DebugOverlayAvailability.Disabled));
            Assert.That(status.PrimaryItemCount, Is.Zero);
            Assert.That(status.SecondaryItemCount, Is.Zero);
            Assert.That(status.DroppedItemCount, Is.Zero);
            Assert.That(status.Reason, Is.EqualTo("overlay disabled"));
        });
    }

    [Test]
    public void LightTileHeatmap_ClassifiesAllContractThresholds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DebugLightTileHeatmap.Classify(0, false, 64),
                Is.EqualTo(DebugLightTileHeatClass.Empty));
            Assert.That(DebugLightTileHeatmap.Classify(12, false, 64),
                Is.EqualTo(DebugLightTileHeatClass.Low));
            Assert.That(DebugLightTileHeatmap.Classify(48, false, 64),
                Is.EqualTo(DebugLightTileHeatClass.NearCapacity));
            Assert.That(DebugLightTileHeatmap.Classify(64, false, 64),
                Is.EqualTo(DebugLightTileHeatClass.Saturated));
            Assert.That(DebugLightTileHeatmap.Classify(1, true, 64),
                Is.EqualTo(DebugLightTileHeatClass.Overflow));
        });
    }

    [Test]
    public void CascadeFrustumValidation_RejectsIdentitySingularAndNonFiniteMatrices()
    {
        Matrix4x4 valid = Matrix4x4.CreateScale(new Vector3(2.0f, 3.0f, 4.0f));
        Matrix4x4 nonFinite = valid;
        nonFinite.M23 = float.NaN;

        Assert.Multiple(() =>
        {
            Assert.That(DebugOverlayBuilder.IsValidDebugFrustumMatrix(valid), Is.True);
            Assert.That(DebugOverlayBuilder.IsValidDebugFrustumMatrix(Matrix4x4.Identity), Is.False);
            Assert.That(DebugOverlayBuilder.IsValidDebugFrustumMatrix(Matrix4x4.Zero), Is.False);
            Assert.That(DebugOverlayBuilder.IsValidDebugFrustumMatrix(nonFinite), Is.False);
        });
    }

    [TestCase(0.1f, 0.04f)]
    [TestCase(1.0f, 0.08f)]
    [TestCase(10.0f, 0.20f)]
    public void ProbeSphereIdentity_UsesRadiusClampAndGenerationTags(
        float spacing,
        float expectedRadius)
    {
        GPUDdgiProbeDebugInstance instance = DebugOverlayBuilder.CreateDdgiProbeDebugInstance(
            frameSerial: 0x1122334455667788UL,
            volumeTableGeneration: 7,
            schedulerResourceGeneration: 8,
            residencyResourceGeneration: 9,
            volumeIndex: 2,
            logicalX: 3,
            logicalY: 4,
            logicalZ: 5,
            virtualProbeIndex: 99,
            logicalPosition: new Vector3(1, 2, 3),
            spacing: spacing,
            schedulerPriorityFlags:
                GPUDdgiProbeDebugInstance.SchedulerVisibleFlag);

        Assert.Multiple(() =>
        {
            Assert.That(instance.LogicalPositionAndRadius.W,
                Is.EqualTo(expectedRadius).Within(0.00001f));
            Assert.That(instance.SnapshotFrameSerialLow, Is.EqualTo(0x55667788u));
            Assert.That(instance.SnapshotFrameSerialHigh, Is.EqualTo(0x11223344u));
            Assert.That(instance.VolumeTableGeneration, Is.EqualTo(7u));
            Assert.That(instance.SchedulerResourceGeneration, Is.EqualTo(8u));
            Assert.That(instance.ResidencyResourceGeneration, Is.EqualTo(9u));
            Assert.That(instance.Flags,
                Is.EqualTo(GPUDdgiProbeDebugInstance.SchedulerVisibleFlag));
            Assert.That(Marshal.SizeOf<GPUDdgiProbeDebugInstance>(), Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<SimpleDdgiProbeDebugPass.DebugDdgiProbePushConstants>(),
                Is.EqualTo(128));
        });
    }

    [Test]
    public void ToroidalLogicalAddressing_UsesProductionHelperOnEveryAxis()
    {
        var volume = new GPUSimpleDdgiVolume
        {
            GridCountsAndFirstProbe = new Vector4(4, 3, 2, 100),
            RaysAndReserved = new Vector4(0, 1, 2, 1)
        };

        int local = SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex(
            volume,
            logicalX: 3,
            logicalY: 2,
            logicalZ: 1);
        (int x, int y, int z) =
            SimpleDdgiVolumeManager.CalculateLogicalProbeCoordinate(volume, local);

        Assert.Multiple(() =>
        {
            Assert.That(local, Is.EqualTo(4));
            Assert.That((x, y, z), Is.EqualTo((3, 2, 1)));
        });
    }

    [Test]
    public void PhysicalSlotIdentity_DistinguishesDenseSparseMissingAndStale()
    {
        SimpleDdgiProbeAddress dense = SimpleDdgiProbeAddress.Dense(9);
        SimpleDdgiProbeAddress sparse = new(20, 21, 7, true);
        SimpleDdgiProbeAddress missing = SimpleDdgiProbeAddress.NonResident(20);

        Assert.Multiple(() =>
        {
            Assert.That(DebugDdgiPhysicalSlotIdentity.Resolve(true, dense),
                Is.EqualTo(new DebugDdgiPhysicalSlotIdentity(
                    DebugDdgiPhysicalSlotAvailability.Resident, 9)));
            Assert.That(DebugDdgiPhysicalSlotIdentity.Resolve(true, sparse, 3),
                Is.EqualTo(new DebugDdgiPhysicalSlotIdentity(
                    DebugDdgiPhysicalSlotAvailability.Resident, 29)));
            Assert.That(DebugDdgiPhysicalSlotIdentity.Resolve(true, missing).Availability,
                Is.EqualTo(DebugDdgiPhysicalSlotAvailability.Nonresident));
            Assert.That(DebugDdgiPhysicalSlotIdentity.Resolve(false, sparse, 3).Availability,
                Is.EqualTo(DebugDdgiPhysicalSlotAvailability.StaleGeneration));
        });
    }

    [Test]
    public void UpdateReasonPalette_IsDeterministicAndCountsMultiReasonRecords()
    {
        SimpleDdgiSchedulerCandidateReason reasons =
            SimpleDdgiSchedulerCandidateReason.Visible |
            SimpleDdgiSchedulerCandidateReason.Fresh |
            SimpleDdgiSchedulerCandidateReason.Topology;
        Span<int> counts = stackalloc int[16];

        int reasonCount = DebugDdgiUpdateReasonPalette.CountReasons(reasons, counts);
        int countedReasons = counts.ToArray().Sum();

        Assert.Multiple(() =>
        {
            Assert.That(DebugDdgiUpdateReasonPalette.ResolvePrimary(reasons),
                Is.EqualTo(SimpleDdgiSchedulerCandidateReason.Topology));
            Assert.That(reasonCount, Is.EqualTo(3));
            Assert.That(countedReasons, Is.EqualTo(3));
            Assert.That(DebugDdgiUpdateReasonPalette.Precedence,
                Has.Count.EqualTo(16));
        });
    }

    [Test]
    public void DdgiDebugPass_DeclaresAuthoritativeReadOnlyInputs()
    {
        RenderGraphPassResourceDeclaration declaration =
            ProductionRenderPipelineDeclaration.Instance
                .CreatePassResourceDeclarations()
                .Single(static candidate =>
                    candidate.PassName == "SimpleDdgiProbeDebugPass");
        RenderGraphResourceId[] requiredReads =
        [
            RenderGraphResourceId.SimpleDdgiParameters,
            RenderGraphResourceId.SimpleDdgiProbeState,
            RenderGraphResourceId.SimpleDdgiUpdateQueue,
            RenderGraphResourceId.SimpleDdgiReceiverProbes,
            RenderGraphResourceId.SimpleDdgiScheduler,
            RenderGraphResourceId.SimpleDdgiResidency
        ];

        Assert.Multiple(() =>
        {
            foreach (RenderGraphResourceId resource in requiredReads)
            {
                Assert.That(declaration.Usages.Single(usage =>
                    usage.Resource == resource).Access,
                    Is.EqualTo(RenderGraphResourceAccess.Read),
                    resource.ToString());
            }
            Assert.That(declaration.Usages.Where(static usage =>
                    usage.Access != RenderGraphResourceAccess.Read)
                .Select(static usage => usage.Resource),
                Is.EquivalentTo(new[]
                {
                    RenderGraphResourceId.RendererDiagnosticsBuffer,
                    RenderGraphResourceId.SceneColor
                }));
        });
    }

    [Test]
    public void DdgiOverlayGpuCounters_DecodeAllReasonBitsAndGenerationTags()
    {
        uint[] counters = new uint[RendererDiagnosticsBuffer.CounterCount];
        int overlay = RendererDiagnosticsBuffer.DebugDdgiOverlayCounterBase;
        int reasons = RendererDiagnosticsBuffer.DebugDdgiOverlayReasonCounterBase;
        counters[overlay] = (uint)DebugOverlayMode.DdgiUpdateReasons + 1u;
        counters[overlay + 1] = 7;
        counters[overlay + 2] = 2;
        counters[overlay + 3] = 3;
        counters[overlay + 4] = 4;
        counters[overlay + 5] = 5;
        counters[overlay + 6] = 6;
        counters[overlay + 7] = 2;
        for (int reason = 0; reason < 16; reason++)
            counters[reasons + reason] = checked((uint)(reason + 1));
        counters[overlay + 24] = 101;
        counters[overlay + 25] = 102;
        counters[overlay + 26] = 103;

        DebugDdgiOverlayGpuCounters decoded =
            RendererDiagnosticsBuffer.DecodeDebugDdgiOverlayCounters(counters);

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Valid, Is.True);
            Assert.That(decoded.Mode, Is.EqualTo(DebugOverlayMode.DdgiUpdateReasons));
            Assert.That(decoded.DrawnMarkerCount, Is.EqualTo(7u));
            Assert.That(decoded.FilteredMarkerCount, Is.EqualTo(2u));
            Assert.That(decoded.NonresidentMarkerCount, Is.EqualTo(3u));
            Assert.That(decoded.StaleMappingCount, Is.EqualTo(4u));
            Assert.That(decoded.StateUnavailableMarkerCount, Is.EqualTo(5u));
            Assert.That(decoded.InvalidTransactionCount, Is.EqualTo(6u));
            Assert.That(decoded.UpdateReasons.FreshCount, Is.EqualTo(1u));
            Assert.That(decoded.UpdateReasons.ResidualPropagationCount, Is.EqualTo(16u));
            Assert.That(decoded.UpdateReasons.MultiReasonCount, Is.EqualTo(2u));
            Assert.That(decoded.VolumeTableGeneration, Is.EqualTo(101u));
            Assert.That(decoded.SchedulerResourceGeneration, Is.EqualTo(102u));
            Assert.That(decoded.ResidencyResourceGeneration, Is.EqualTo(103u));
        });
    }

    [Test]
    public void DebugShaders_UseProductionTileAndDdgiAddressingContracts()
    {
        string light = ReadRepoText("Njulf.Shaders", "debug_overlay.frag");
        string probe = ReadRepoText("Njulf.Shaders", "debug_ddgi_probe_shared.glsl");
        string update = ReadRepoText("Njulf.Shaders", "debug_ddgi_update.vert");

        Assert.Multiple(() =>
        {
            Assert.That(light, Does.Contain("pixel / uvec2(16u)"));
            Assert.That(light, Does.Contain("SIZEOF_GPU_TILED_LIGHT_HEADER"));
            Assert.That(probe, Does.Contain("SimpleDdgiProbeIndex(logicalCoord, volume)"));
            Assert.That(probe, Does.Contain("ResolveSimpleDdgiProbeAddress("));
            Assert.That(probe, Does.Contain("ReadSimpleDdgiReceiverProbe("));
            Assert.That(update, Does.Contain("ReadSimpleDdgiProbeUpdate("));
            Assert.That(update, Does.Not.Contain("for (uint queue"));
        });
    }

    [Test]
    public void OptionalOverlayPasses_ExecuteOnlyForMatchingActiveWork()
    {
        using var sceneData = new SceneRenderingData
        {
            DebugToolingEnabled = false,
            DebugOverlayMode = DebugOverlayMode.None,
            DebugOverlayStatus = default
        };

        Assert.Multiple(() =>
        {
            Assert.That(DebugOverlayPass.ShouldExecuteForFrame(sceneData), Is.False);
            Assert.That(SimpleDdgiProbeDebugPass.ShouldExecuteForFrame(sceneData), Is.False);
        });

        sceneData.DebugToolingEnabled = true;
        sceneData.DebugOverlayMode = DebugOverlayMode.LightTiles;
        sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
            DebugOverlayMode.LightTiles,
            1);
        sceneData.LocalLightCount = 1;
        Assert.That(DebugOverlayPass.ShouldExecuteForFrame(sceneData), Is.True);

        sceneData.DebugOverlayMode = DebugOverlayMode.DdgiProbeSpheres;
        sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
            DebugOverlayMode.DdgiProbeSpheres,
            1);
        sceneData.DebugDdgiProbeInstanceCount = 1;
        Assert.That(SimpleDdgiProbeDebugPass.ShouldExecuteForFrame(sceneData), Is.True);

        sceneData.DebugOverlayMode = DebugOverlayMode.DdgiUpdatedProbes;
        sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
            DebugOverlayMode.DdgiUpdatedProbes,
            "no probes admitted for update");
        sceneData.DebugDdgiUpdateRecordCapacity = 768;
        Assert.That(SimpleDdgiProbeDebugPass.ShouldExecuteForFrame(sceneData), Is.True,
            "The bounded live queue still writes an exact zero-item diagnostic header.");
    }

    [Test]
    public void SettingsReference_EnumeratesTheCatalogInExactCycleOrder()
    {
        string reference = ReadRepoText("RendererSettingsReference.md");
        int sectionStart = reference.IndexOf(
            "The active `Ctrl+Keypad9` (`Ctrl+Num9`) cycle is:",
            StringComparison.Ordinal);
        int sectionEnd = reference.IndexOf(
            "`Ctrl+Shift+Keypad9`",
            sectionStart,
            StringComparison.Ordinal);

        Assert.That(sectionStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(sectionEnd, Is.GreaterThan(sectionStart));
        string cycleSection = reference[sectionStart..sectionEnd];
        int previous = -1;
        foreach (DebugOverlayDescriptor descriptor in DebugOverlayCatalog.ActiveCycle)
        {
            int current = cycleSection.IndexOf(
                $"`{descriptor.Mode}`",
                previous + 1,
                StringComparison.Ordinal);
            Assert.That(current, Is.GreaterThan(previous), descriptor.Mode.ToString());
            previous = current;
        }
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }
        Assert.Fail($"Could not find repo file '{Path.Combine(pathParts)}'.");
        return string.Empty;
    }
}

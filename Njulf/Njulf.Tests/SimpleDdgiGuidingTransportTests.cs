using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingTransportTests
{
    [Test]
    public void DisabledSourceCacheDescriptor_CannotLookLikeOneBackedPayload()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiGuidingSourceCacheSidecar.DisabledFallbackRangeBytes,
                Is.GreaterThanOrEqualTo((ulong)(sizeof(uint) * 4)));
            Assert.That(
                SimpleDdgiGuidingSourceCacheSidecar.DisabledFallbackRangeBytes,
                Is.LessThan(
                    (ulong)SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount));
        });
    }

    [Test]
    public void SourceCacheLayout_AdmitsOnlyCompleteProbePrefixes()
    {
        ulong bytesPerProbe = 64UL *
            SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount;
        SimpleDdgiGuidingSourceCacheLayout layout =
            SimpleDdgiGuidingSourceCacheLayoutCompiler.Compile(new(
                Enabled: true,
                TotalPhysicalProbeCapacity: 12,
                RequestedGuidedPhysicalProbeCapacity: 10,
                DirectionSlotsPerProbe: 64,
                MemoryBudgetBytes: bytesPerProbe * 6UL + bytesPerProbe / 2UL));

        Assert.Multiple(() =>
        {
            Assert.That(layout.IsAdmitted, Is.True, layout.Reason);
            Assert.That(layout.AdmittedGuidedPhysicalProbeCapacity,
                Is.EqualTo(6));
            Assert.That(layout.PayloadCapacity, Is.EqualTo(384u));
            Assert.That(layout.AllocatedBytes, Is.EqualTo(bytesPerProbe * 6UL));
            Assert.That(layout.Reason,
                Is.EqualTo("admitted-prefix-reduced-by-direction-sidecar-budget"));
            Assert.That(layout.TryGetPayloadIndex(5, 63, out uint last), Is.True);
            Assert.That(last, Is.EqualTo(383u));
            Assert.That(layout.TryGetPayloadIndex(6, 0, out _), Is.False);
            Assert.That(layout.TryGetPayloadByteOffset(5, 63, out ulong offset),
                Is.True);
            Assert.That(offset,
                Is.EqualTo(383UL * SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount));
        });
    }

    [Test]
    public void ProductionLayout_AccountsManagerAndSourceCacheOwnershipExactly()
    {
        const int probes = 8;
        const int rays = 128;
        ulong sidecarBytes = checked((ulong)probes * rays *
            SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount);
        SimpleDdgiGuidingLayout layout = SimpleDdgiGuidingLayoutCompiler.Compile(
            new SimpleDdgiGuidingLayoutRequest(
                SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                PhysicalProbeCapacity: probes,
                ScheduledGuidedProbeCapacity: 2,
                StorageAlignmentBytes: 16UL,
                AllocateValidationReferenceBank: false)
            {
                DirectionSlotsPerProbe = rays,
                DirectionPdfSidecarBudgetBytes = sidecarBytes
            });

        Assert.Multiple(() =>
        {
            Assert.That(layout.AbiVersion,
                Is.EqualTo(SimpleDdgiGuidingGpuAbi.Version));
            Assert.That(layout.HasTransportSidecar, Is.True);
            Assert.That(layout.DirectionPayloadCapacity,
                Is.EqualTo((uint)(probes * rays)));
            Assert.That(layout.DirectionPdfSidecarBytes,
                Is.EqualTo(sidecarBytes));
            Assert.That(layout.TotalBytes,
                Is.EqualTo(layout.ManagerOwnedBytes + sidecarBytes +
                    layout.TransientWorkspace.TotalBytes));
            Assert.That(layout.ManagerOwnedBytes,
                Is.EqualTo(layout.PersistentDoubleBufferedBytes +
                    layout.ValidationReferenceBankBytes));
        });

        Assert.That(
            () => SimpleDdgiGuidingLayoutCompiler.Compile(
                new SimpleDdgiGuidingLayoutRequest(
                    SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                    PhysicalProbeCapacity: probes,
                    ScheduledGuidedProbeCapacity: 2,
                    StorageAlignmentBytes: 16UL,
                    AllocateValidationReferenceBank: false)
                {
                    DirectionSlotsPerProbe = rays,
                    DirectionPdfSidecarBudgetBytes = sidecarBytes - 1UL
                }),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void CentralMemoryPlan_SeparatesPersistentSidecarFromAliasableScratch()
    {
        const int probes = 4;
        const int rays = 64;
        ulong sidecarBytes = (ulong)probes * rays *
            SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount;
        SimpleDdgiGuidingLayout layout = SimpleDdgiGuidingLayoutCompiler.Compile(
            new SimpleDdgiGuidingLayoutRequest(
                SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                probes,
                ScheduledGuidedProbeCapacity: 2,
                StorageAlignmentBytes: 16UL,
                AllocateValidationReferenceBank: true)
            {
                DirectionSlotsPerProbe = rays,
                DirectionPdfSidecarBudgetBytes = sidecarBytes
            });
        ulong history = layout.PersistentDoubleBufferedBytes +
            layout.ValidationReferenceBankBytes + sidecarBytes;

        SimpleDdgiAdvancedExperimentMemoryPlan plan =
            SimpleDdgiAdvancedExperimentMemoryPlan.CreateDirectionalGuiding(
                layout,
                allocatedHistoryBytes: history,
                allocatedScratchBytes: layout.TransientWorkspace.TotalBytes);

        Assert.Multiple(() =>
        {
            Assert.That(plan.IsValid, Is.True);
            Assert.That(plan.DirectionalGuidingHistoryBanks.RequiredBytes,
                Is.EqualTo(history));
            Assert.That(plan.DirectionalGuidingBuildScratch.RequiredBytes,
                Is.EqualTo(layout.TransientWorkspace.TotalBytes));
            Assert.That(plan.PersistentAllocatedBytes, Is.EqualTo(history));
            Assert.That(plan.ConservativeTransientPeakLiveBytes,
                Is.EqualTo(layout.TransientWorkspace.TotalBytes));
            Assert.That(SimpleDdgiAdvancedExperimentMemoryPlan
                .IsTransientCategory(SimpleDdgiAdvancedMemoryCategory
                    .DirectionalGuidingHistoryBanks), Is.False);
            Assert.That(SimpleDdgiAdvancedExperimentMemoryPlan
                .IsTransientCategory(SimpleDdgiAdvancedMemoryCategory
                    .DirectionalGuidingBuildScratch), Is.True);
        });
    }

    [TestCase(8, 8)]
    [TestCase(24, 8)]
    [TestCase(64, 16)]
    [TestCase(128, 32)]
    [TestCase(256, 64)]
    public void MaintenanceSubset_IsFixedStratifiedAndMatchesSchedulerMapping(
        int totalRayCount,
        int expectedMaintenanceCount)
    {
        int[] maintenanceSlots = Enumerable.Range(0, totalRayCount)
            .Where(slot => SimpleDdgiGuidingTransportEstimator
                .IsMaintenanceSlot(slot, totalRayCount))
            .ToArray();
        int[] schedulerSlots = Enumerable.Range(0, expectedMaintenanceCount)
            .Select(rank => rank * totalRayCount / expectedMaintenanceCount)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiGuidingTransportEstimator
                .ResolveMaintenanceRayCount(totalRayCount),
                Is.EqualTo(expectedMaintenanceCount));
            Assert.That(maintenanceSlots, Is.EqualTo(schedulerSlots));
            Assert.That(maintenanceSlots, Is.Ordered.Ascending);
            Assert.That(maintenanceSlots.Distinct().Count(),
                Is.EqualTo(expectedMaintenanceCount));
        });
    }

    [Test]
    public void BalanceEstimator_ConstantUniformFieldConvergesToPiAndKeepsVisibilityUniform()
    {
        const int rayCount = 256;
        Vector3 incident = new(0.7f, 1.25f, 2.0f);
        var samples = new List<SimpleDdgiGuidingProjectionSample>(rayCount);
        for (int ray = 0; ray < rayCount; ray++)
        {
            double z = 1.0d - 2.0d * (ray + 0.5d) / rayCount;
            double radius = Math.Sqrt(Math.Max(0.0d, 1.0d - z * z));
            double angle = 2.399963229728653d * ray;
            var direction = Vector3.Normalize(new Vector3(
                (float)(Math.Cos(angle) * radius),
                (float)(Math.Sin(angle) * radius),
                (float)z));
            SimpleDdgiDirectionSamplingTechnique technique =
                SimpleDdgiGuidingTransportEstimator.IsMaintenanceSlot(
                    ray,
                    rayCount)
                    ? SimpleDdgiDirectionSamplingTechnique.UniformMaintenance
                    : SimpleDdgiDirectionSamplingTechnique.Mixture;
            samples.Add(new SimpleDdgiGuidingProjectionSample(
                incident,
                direction,
                technique,
                (float)SimpleDdgiGuidingTransportEstimator.UniformSpherePdf));
        }

        SimpleDdgiGuidingProjectionResult result =
            SimpleDdgiGuidingTransportEstimator.ProjectIrradiance(
                Vector3.UnitZ,
                CollectionsMarshalAsSpan(samples));
        Vector3 expected = incident * MathF.PI;

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True, result.Reason);
            Assert.That(result.UniformMaintenanceSampleCount, Is.EqualTo(64));
            Assert.That(result.MixtureSampleCount, Is.EqualTo(192));
            Assert.That(result.Irradiance.X, Is.EqualTo(expected.X).Within(0.02f));
            Assert.That(result.Irradiance.Y, Is.EqualTo(expected.Y).Within(0.02f));
            Assert.That(result.Irradiance.Z, Is.EqualTo(expected.Z).Within(0.02f));
            Assert.That(SimpleDdgiGuidingTransportEstimator.OwnsVisibility(
                SimpleDdgiDirectionSamplingTechnique.UniformMaintenance),
                Is.True);
            Assert.That(SimpleDdgiGuidingTransportEstimator.OwnsVisibility(
                SimpleDdgiDirectionSamplingTechnique.Mixture),
                Is.False);
        });
    }

    [Test]
    public void CanonicalEstimator_PreservesAConstantForAnUnevenGuidedSampleSet()
    {
        Vector3 incident = new(0.25f, 1.0f, 3.0f);
        SimpleDdgiGuidingProjectionSample[] samples =
        [
            new(incident, Vector3.UnitZ,
                SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
                (float)SimpleDdgiGuidingTransportEstimator.UniformSpherePdf),
            new(incident, Vector3.Normalize(new Vector3(1.0f, 0.0f, 0.05f)),
                SimpleDdgiDirectionSamplingTechnique.Mixture, 0.75f),
            new(incident, Vector3.Normalize(new Vector3(-0.2f, 0.1f, 1.0f)),
                SimpleDdgiDirectionSamplingTechnique.Mixture, 0.02f)
        ];

        SimpleDdgiGuidingProjectionResult canonical =
            SimpleDdgiGuidingTransportEstimator.ProjectIrradiance(
                Vector3.UnitZ,
                samples);

        Assert.Multiple(() =>
        {
            Assert.That(canonical.IsValid, Is.True, canonical.Reason);
            Assert.That(canonical.Irradiance.X,
                Is.EqualTo(incident.X * MathF.PI).Within(1.0e-5f));
            Assert.That(canonical.Irradiance.Y,
                Is.EqualTo(incident.Y * MathF.PI).Within(1.0e-5f));
            Assert.That(canonical.Irradiance.Z,
                Is.EqualTo(incident.Z * MathF.PI).Within(1.0e-5f));
        });
    }

    [Test]
    public void RawEstimator_RemainsSeparateFromCanonicalCertificateOperator()
    {
        SimpleDdgiGuidingProjectionSample[] samples =
        [
            new(Vector3.One, Vector3.UnitZ,
                SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
                (float)SimpleDdgiGuidingTransportEstimator.UniformSpherePdf),
            new(Vector3.One, Vector3.UnitZ,
                SimpleDdgiDirectionSamplingTechnique.Mixture, 0.75f)
        ];

        SimpleDdgiGuidingProjectionResult canonical =
            SimpleDdgiGuidingTransportEstimator.ProjectIrradiance(
                Vector3.UnitZ,
                samples);
        SimpleDdgiGuidingProjectionResult raw =
            SimpleDdgiGuidingTransportEstimator.ProjectRawIrradianceReference(
                Vector3.UnitZ,
                samples);

        Assert.Multiple(() =>
        {
            Assert.That(canonical.IsValid, Is.True);
            Assert.That(raw.IsValid, Is.True);
            Assert.That(canonical.Irradiance.X,
                Is.EqualTo(MathF.PI).Within(1.0e-5f));
            Assert.That(raw.Irradiance.X,
                Is.Not.EqualTo(canonical.Irradiance.X).Within(1.0e-3f));
        });
    }

    [Test]
    public void Estimator_RejectsNonFiniteOrFloatOverflowingPublication()
    {
        var invalid = new[]
        {
            new SimpleDdgiGuidingProjectionSample(
                new Vector3(float.MaxValue),
                Vector3.UnitZ,
                SimpleDdgiDirectionSamplingTechnique.UniformMaintenance,
                (float)SimpleDdgiGuidingTransportEstimator.UniformSpherePdf)
        };

        SimpleDdgiGuidingProjectionResult result =
            SimpleDdgiGuidingTransportEstimator.ProjectIrradiance(
                Vector3.UnitZ,
                invalid);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Reason,
                Is.EqualTo("guiding-projection-result-invalid"));
            Assert.That(result.Irradiance, Is.EqualTo(Vector3.Zero));
        });
    }

    private static ReadOnlySpan<SimpleDdgiGuidingProjectionSample>
        CollectionsMarshalAsSpan(
            List<SimpleDdgiGuidingProjectionSample> samples) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples);
}

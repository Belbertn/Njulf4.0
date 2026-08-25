using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DielectricTransportTests
{
    [Test]
    public void NestedBoundaries_EnterAndExitInStrictLifoOrder()
    {
        var stack = new BoundedDielectricMediaStack();
        DielectricBoundary glass = Boundary(11u, 1.5f);
        DielectricBoundary liquid = Boundary(
            22u, 1.33f, OpticalBoundaryKind.WaterSurface);

        Assert.That(Transmit(stack, glass, true, 0f), Is.True);
        Assert.That(stack.CurrentIor, Is.EqualTo(1.5f));
        Assert.That(Transmit(stack, liquid, true, 1f), Is.True);
        Assert.That(stack.CurrentIor, Is.EqualTo(1.33f));
        Assert.That(Transmit(stack, liquid, false, 2f), Is.True);
        Assert.That(stack.CurrentIor, Is.EqualTo(1.5f));
        Assert.That(Transmit(stack, glass, false, 3f), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(stack.Count, Is.Zero);
            Assert.That(stack.InterfaceCount, Is.EqualTo(4));
            Assert.That(stack.FinalizePath(), Is.True);
            Assert.That(stack.FallbackReason,
                Is.EqualTo(DielectricTransportFallbackReason.None));
        });
    }

    [Test]
    public void ReflectionAndTotalInternalReflection_DoNotMutateMediaState()
    {
        var stack = new BoundedDielectricMediaStack();
        DielectricBoundary glass = Boundary(1u, 1.5f);
        Assert.That(stack.TryPrepareInterface(
            glass, true, out DielectricInterface dielectricInterface),
            Is.True);
        Assert.That(stack.Count, Is.Zero, "Preparing a reflected branch must not push.");

        float cosineAtSixtyDegrees = 0.5f;
        float fresnel = DielectricTransportMath.ExactUnpolarizedFresnel(
            cosineAtSixtyDegrees,
            1.5f,
            1f,
            out bool totalInternalReflection);

        Assert.Multiple(() =>
        {
            Assert.That(totalInternalReflection, Is.True);
            Assert.That(fresnel, Is.EqualTo(1f));
            Assert.That(stack.Count, Is.Zero);
            Assert.That(stack.InterfaceCount, Is.Zero);
        });

        Assert.That(stack.CommitReflection(dielectricInterface), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(stack.Count, Is.Zero,
                "A reflected branch must preserve media state.");
            Assert.That(stack.InterfaceCount, Is.EqualTo(1),
                "A reflected branch must still consume one interface budget.");
        });
    }

    [Test]
    public void BeerLambert_ReconstructsAuthoredAttenuationColorAtDistance()
    {
        var color = new Vector3(0.8f, 0.4f, 0.1f);
        Vector3 absorption =
            DielectricTransportMath.AbsorptionCoefficient(color, 2f);
        Vector3 throughput = DielectricTransportMath.BeerLambert(
            absorption, 2f);

        Assert.Multiple(() =>
        {
            Assert.That(throughput.X, Is.EqualTo(color.X).Within(1e-6f));
            Assert.That(throughput.Y, Is.EqualTo(color.Y).Within(1e-6f));
            Assert.That(throughput.Z, Is.EqualTo(color.Z).Within(1e-6f));
        });
    }

    [Test]
    public void BoundedStack_ReportsExactOverflowCandidateAndPartialPathReasons()
    {
        var overflow = new BoundedDielectricMediaStack();
        for (uint index = 0; index < BoundedDielectricMediaStack.MaximumDepth;
             index++)
        {
            Assert.That(Transmit(
                overflow, Boundary(index + 1u, 1.1f + index * 0.1f),
                true, index), Is.True);
        }
        Assert.That(
            overflow.TryPrepareInterface(Boundary(99u, 1.6f), true, out _),
            Is.False);
        Assert.That(overflow.FallbackReason,
            Is.EqualTo(DielectricTransportFallbackReason.StackOverflow));

        var candidates = new BoundedDielectricMediaStack();
        Assert.That(candidates.RegisterCandidateCount(65), Is.False);
        Assert.That(candidates.FallbackReason,
            Is.EqualTo(DielectricTransportFallbackReason.CandidateBudgetExceeded));

        var partial = new BoundedDielectricMediaStack();
        Assert.That(Transmit(partial, Boundary(7u, 1.5f), true, 0f), Is.True);
        Assert.That(partial.FinalizePath(), Is.False);
        Assert.That(partial.FallbackReason,
            Is.EqualTo(DielectricTransportFallbackReason.PartialMediaStack));
    }

    [Test]
    public void DispersionTriplet_UsesCentralGreenAndOrderedRedBlueIors()
    {
        Vector3 iors = DielectricTransportMath.RgbIors(1.5f, 0.4f);

        Assert.Multiple(() =>
        {
            Assert.That(iors.X, Is.LessThan(iors.Y));
            Assert.That(iors.Y, Is.EqualTo(1.5f));
            Assert.That(iors.Z, Is.GreaterThan(iors.Y));
            Assert.That(iors.X, Is.EqualTo(1.495f).Within(1e-6f));
            Assert.That(iors.Z, Is.EqualTo(1.505f).Within(1e-6f));
        });
    }

    [Test]
    public void ModeResolver_DemotesIncompleteRayScenesToApproximation()
    {
        var settings = new TransparencySettings
        {
            ThickTransmissionMode = ThickTransmissionMode.RayQuery
        };
        ThickTransmissionModeResolution fallback =
            ThickTransmissionModeResolver.Resolve(
                settings,
                new ThickTransmissionModeCapabilities(
                    RayQuerySupported: true,
                    AccelerationStructureSupported: true,
                    RaySceneReady: false,
                    RayPipelineAvailable: true));
        ThickTransmissionModeResolution admitted =
            ThickTransmissionModeResolver.Resolve(
                settings,
                new ThickTransmissionModeCapabilities(true, true, true, true));

        Assert.Multiple(() =>
        {
            Assert.That(fallback.Effective,
                Is.EqualTo(ThickTransmissionMode.Approximation));
            Assert.That(fallback.Reason,
                Is.EqualTo(ThickTransmissionFallbackReason.RaySceneIncomplete));
            Assert.That(admitted.Effective,
                Is.EqualTo(ThickTransmissionMode.RayQuery));
            Assert.That(admitted.Reason,
                Is.EqualTo(ThickTransmissionFallbackReason.None));
        });
    }

    [Test]
    public void ModeResolver_DemotesZeroTaskAndOversizedMemoryEnvelopes()
    {
        var settings = new TransparencySettings
        {
            ThickTransmissionMode = ThickTransmissionMode.RayQuery,
            ThickTransmissionRayTaskBudget = 0
        };
        var capabilities = new ThickTransmissionModeCapabilities(
            true, true, true, true);

        ThickTransmissionModeResolution noTasks =
            ThickTransmissionModeResolver.Resolve(settings, capabilities);
        settings.ThickTransmissionRayTaskBudget = 4_194_304;
        settings.ThickTransmissionMemoryBudgetBytes =
            16UL * 1024UL * 1024UL;
        ThickTransmissionModeResolution noMemory =
            ThickTransmissionModeResolver.Resolve(settings, capabilities);

        Assert.Multiple(() =>
        {
            Assert.That(noTasks.Effective,
                Is.EqualTo(ThickTransmissionMode.Approximation));
            Assert.That(noTasks.Reason,
                Is.EqualTo(ThickTransmissionFallbackReason.TaskBudgetExceeded));
            Assert.That(noMemory.Effective,
                Is.EqualTo(ThickTransmissionMode.Approximation));
            Assert.That(noMemory.Reason,
                Is.EqualTo(ThickTransmissionFallbackReason.MemoryBudgetExceeded));
        });
    }

    [Test]
    public void OpticalGpuFlags_RoundTripWaterAndGeneralizedCasterPolicy()
    {
        int packed = OpticalMaterialGpuContract.PackFlags(
            OpticalBoundaryKind.WaterSurface,
            GiCausticCasterPolicy.DielectricPriority,
            volumeTransmission: true);

        Assert.Multiple(() =>
        {
            Assert.That(OpticalMaterialGpuContract.UnpackBoundaryKind(packed),
                Is.EqualTo(OpticalBoundaryKind.WaterSurface));
            Assert.That(OpticalMaterialGpuContract.UnpackCasterPolicy(packed),
                Is.EqualTo(GiCausticCasterPolicy.DielectricPriority));
            Assert.That(packed & OpticalMaterialGpuContract.VolumeTransmissionFlag,
                Is.Not.Zero);
            Assert.That(packed & OpticalMaterialGpuContract.WaterSurfaceFlag,
                Is.Not.Zero);
        });
    }

    [Test]
    public void ForwardPushLimits_PackExactBoundedOpticalBudgets()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionLimits(1, 1, 1),
                Is.Zero);
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionLimits(5, 3, 17),
                Is.EqualTo(532u));
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionLimits(
                    BoundedDielectricMediaStack.MaximumInterfaces,
                    BoundedDielectricMediaStack.MaximumDepth,
                    BoundedDielectricMediaStack
                        .MaximumCandidatesPerInterface),
                Is.EqualTo(2047u));
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionTaskBudget(0),
                Is.Zero);
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionTaskBudget(
                    262_144),
                Is.EqualTo(1_048_576u));
            Assert.That(
                GPUForwardPushConstants.PackThickTransmissionTaskBudget(
                    GPUForwardPushConstants
                        .MaximumThickTransmissionRayTaskBudget),
                Is.EqualTo(16_777_216u));
        });
    }

    private static DielectricBoundary Boundary(
        uint identity,
        float ior,
        OpticalBoundaryKind kind = OpticalBoundaryKind.ClosedVolume) =>
        new(identity, identity + 100u, ior, Vector3.Zero, kind);

    private static bool Transmit(
        BoundedDielectricMediaStack stack,
        in DielectricBoundary boundary,
        bool frontFacing,
        float distance) =>
        stack.TryPrepareInterface(boundary, frontFacing, out var dielectric) &&
        stack.CommitTransmission(boundary, dielectric, distance);
}

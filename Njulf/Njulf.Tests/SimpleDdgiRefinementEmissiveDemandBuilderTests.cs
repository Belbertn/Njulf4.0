using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiRefinementEmissiveDemandBuilderTests
{
    [Test]
    public void Build_AdmitsBrightCompactTriangleAtWorldCentroid()
    {
        GPUDdgiEmissiveSource source = Triangle(
            new Vector3(3f, 2f, 1f),
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(8f),
            area: 1f);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandDiagnostics diagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                [source],
                new(250f, 2f, 8),
                destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Has.Count.EqualTo(1));
            Assert.That(destination[0].Position.X, Is.EqualTo(3f + 2f / 3f).Within(1e-5f));
            Assert.That(destination[0].Position.Y, Is.EqualTo(2f + 1f / 3f).Within(1e-5f));
            Assert.That(destination[0].Position.Z, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(destination[0].Reason, Is.EqualTo(
                SimpleDdgiRefinementDemandReason.CompactEmissive));
            Assert.That(diagnostics.EligibleSourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.AdmittedDemandCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Build_RejectsLargeOrDimTrianglesIndependently()
    {
        GPUDdgiEmissiveSource large = Triangle(
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            new Vector3(10f),
            area: 8f);
        GPUDdgiEmissiveSource dim = Triangle(
            new Vector3(4f, 0f, 0f),
            Vector3.UnitX,
            Vector3.UnitY,
            new Vector3(1f),
            area: 0.5f);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandDiagnostics diagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                [large, dim],
                new(250f, 2f, 8),
                destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Is.Empty);
            Assert.That(diagnostics.RejectedLargeSourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.RejectedDimSourceCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Build_UsesBoundedStableTopKWithoutDependingOnInputOrder()
    {
        GPUDdgiEmissiveSource[] ascending = Enumerable.Range(1, 12)
            .Select(index => Triangle(
                new Vector3(index * 3f, 0f, 0f),
                Vector3.UnitX,
                Vector3.UnitY,
                new Vector3(index),
                area: 0.5f))
            .ToArray();
        GPUDdgiEmissiveSource[] descending = ascending.Reverse().ToArray();
        var left = new List<SimpleDdgiRefinementDemand>();
        var right = new List<SimpleDdgiRefinementDemand>();
        var configuration = new SimpleDdgiRefinementEmissiveDemandConfiguration(
            50f,
            2f,
            4);

        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            ascending,
            configuration,
            left);
        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            descending,
            configuration,
            right);

        Assert.Multiple(() =>
        {
            Assert.That(left, Has.Count.EqualTo(4));
            Assert.That(
                left.Select(static demand => demand.StableSourceId),
                Is.EqualTo(right.Select(static demand => demand.StableSourceId)));
            Assert.That(left[0].Priority, Is.GreaterThanOrEqualTo(left[^1].Priority));
        });
    }

    [Test]
    public void Build_ConvertsIntegratedMacroPowerToAreaNormalizedBrightness()
    {
        var macro = new DdgiVfxMacroEmitter(
            77,
            1,
            DdgiVfxMacroShape.Sphere,
            new Vector3(5f, 6f, 7f),
            Vector3.UnitY,
            new Vector3(0.1f),
            new Vector3(10f),
            new BoundingBox(new Vector3(4.9f, 5.9f, 6.9f), new Vector3(5.1f, 6.1f, 7.1f)),
            new BoundingBox(new Vector3(4.9f, 5.9f, 6.9f), new Vector3(5.1f, 6.1f, 7.1f)),
            AuthoredPower: true);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            [DdgiVfxMacroEmitterReducer.PackSource(macro)],
            new(250f, 1f, 8),
            destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Has.Count.EqualTo(1));
            Assert.That(destination[0].Position, Is.EqualTo(macro.Center));
        });
    }

    [TestCase(false, false, true, true, true)]
    [TestCase(true, false, true, true, false)]
    [TestCase(false, true, true, true, false)]
    [TestCase(false, false, false, true, false)]
    [TestCase(false, false, true, false, false)]
    public void PublicationGate_IsAllOrNothing(
        bool invalidation,
        bool topologyChanged,
        bool certificationEnabled,
        bool certificateCurrent,
        bool expected)
    {
        Assert.That(
            SimpleDdgiRefinementPublication.CanPublishReceiverAuthority(
                invalidation,
                topologyChanged,
                certificationEnabled,
                certificateCurrent),
            Is.EqualTo(expected));
    }

    [TestCase(
        SimpleDdgiTransportRecoveryAction.None,
        SimpleDdgiTransportPhase.AuditFrozen,
        false)]
    [TestCase(
        SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch,
        SimpleDdgiTransportPhase.AcceleratedSolve,
        false)]
    [TestCase(
        SimpleDdgiTransportRecoveryAction.ReconcileParticipants,
        SimpleDdgiTransportPhase.ParticipantReconciliation,
        true)]
    [TestCase(
        SimpleDdgiTransportRecoveryAction.RepairSourceCache,
        SimpleDdgiTransportPhase.SourceRepair,
        true)]
    [TestCase(
        SimpleDdgiTransportRecoveryAction.RebuildPrivateField,
        SimpleDdgiTransportPhase.FailClosedRecovery,
        true)]
    public void PublicationContinuity_RevokesOnlyForDestructiveRecovery(
        SimpleDdgiTransportRecoveryAction action,
        SimpleDdgiTransportPhase phase,
        bool expected)
    {
        Assert.That(
            SimpleDdgiRefinementPublication.RequiresPublicationRevocation(
                action,
                phase),
            Is.EqualTo(expected));
    }

    [Test]
    public void PublicationState_RetainsCertifiedFieldAcrossRoutineRecertification()
    {
        var state = new SimpleDdgiRefinementPublicationState();
        SimpleDdgiTransportGenerations certified = Generations(
            sourceEpoch: 5u,
            canonicalField: 7u,
            solve: 2u,
            audit: 2u);
        SimpleDdgiTransportGenerations routineResample = Generations(
            sourceLighting: 4u,
            sourceEpoch: 6u,
            canonicalField: 8u,
            solve: 4u,
            audit: 4u);

        bool beforeCertificate = state.Resolve(
            false, false, true, false, false, Identity(certified));
        bool certifiedPublished = state.Resolve(
            false, false, true, true, false, Identity(certified));
        bool retainedDuringAudit = state.Resolve(
            false, false, true, false, false, Identity(routineResample));

        Assert.Multiple(() =>
        {
            Assert.That(beforeCertificate, Is.False);
            Assert.That(certifiedPublished, Is.True);
            Assert.That(retainedDuringAudit, Is.True);
            Assert.That(state.IsRetainingCertifiedAuthority, Is.True);
        });
    }

    [TestCase("invalidation")]
    [TestCase("topology")]
    [TestCase("recovery")]
    [TestCase("lighting")]
    [TestCase("source-calibration")]
    public void PublicationState_RevokesContinuityForRealInvalidation(
        string boundary)
    {
        var state = new SimpleDdgiRefinementPublicationState();
        SimpleDdgiTransportGenerations certified = Generations();
        Assert.That(
            state.Resolve(
                false,
                false,
                true,
                true,
                false,
                Identity(certified)),
            Is.True);

        SimpleDdgiTransportGenerations current = certified with
        {
            SourceEpoch = 12u,
            CanonicalField = 13u
        };
        SimpleDdgiRefinementPublicationIdentity currentIdentity =
            boundary == "lighting"
                ? Identity(current, lightingSignature: 99u)
                : boundary == "source-calibration"
                    ? Identity(current, sourceCalibrationSignature: 99u)
                : Identity(current);
        bool authority = state.Resolve(
            transactionHasInvalidation: boundary == "invalidation",
            topologyChangedThisFrame: boundary == "topology",
            tailCertificationEnabled: true,
            currentTailCertificate: false,
            recoveryActive: boundary == "recovery",
            currentIdentity);

        Assert.Multiple(() =>
        {
            Assert.That(authority, Is.False);
            Assert.That(state.IsRetainingCertifiedAuthority, Is.False);
            Assert.That(
                state.Resolve(
                    false,
                    false,
                    true,
                    false,
                    false,
                    currentIdentity),
                Is.False,
                "A revoked field must wait for a new current certificate.");
        });
    }

    private static SimpleDdgiRefinementPublicationIdentity Identity(
        SimpleDdgiTransportGenerations generations,
        ulong lightingSignature = 10u,
        ulong sourceCalibrationSignature = 11u) =>
        SimpleDdgiRefinementPublicationIdentity.From(
            generations,
            lightingSignature,
            sourceCalibrationSignature);

    private static SimpleDdgiTransportGenerations Generations(
        uint sourceLighting = 3u,
        uint sourceEpoch = 5u,
        uint canonicalField = 7u,
        uint solve = 2u,
        uint audit = 2u) =>
        new(
            VolumeTable: 1u,
            PhysicalOwnership: 2u,
            SourceLighting: sourceLighting,
            SourceEpoch: sourceEpoch,
            TransportOperator: 4u,
            CanonicalField: canonicalField,
            Solve: solve,
            Audit: audit,
            Queue: 8u,
            SchedulerResources: 9u);

    private static GPUDdgiEmissiveSource Triangle(
        Vector3 origin,
        Vector3 edge1,
        Vector3 edge2,
        Vector3 radiance,
        float area)
    {
        uint packed = (uint)DdgiEmissiveSourceFlags.Triangle <<
                      DdgiEmissiveTriangleTable.FlagsShift;
        return new GPUDdgiEmissiveSource
        {
            Vertex0Area = new Vector4(origin, area),
            Edge1AliasProbability = new Vector4(edge1, 1f),
            Edge2AliasFlags = new Vector4(
                edge2.X,
                edge2.Y,
                edge2.Z,
                BitConverter.UInt32BitsToSingle(packed)),
            RadianceSelectionProbability = new Vector4(radiance, 1f)
        };
    }
}

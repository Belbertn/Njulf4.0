using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiWarmupStateTests
{
    [Test]
    public void Resolve_ReportsColdStartWithoutAProbeField()
    {
        Assert.That(
            Resolve(0, false, true, true, SimpleDdgiTransportPhase.Certified, Ready()),
            Is.EqualTo(DdgiRuntimeWarmupState.ColdStart));
    }

    [TestCase(SimpleDdgiTransportPhase.SourceRepair)]
    [TestCase(SimpleDdgiTransportPhase.ParticipantReconciliation)]
    [TestCase(SimpleDdgiTransportPhase.FailClosedRecovery)]
    public void Resolve_ReportsRecoveryForDestructiveTransportPhases(
        SimpleDdgiTransportPhase phase)
    {
        Assert.That(
            Resolve(100, true, true, false, phase, Ready()),
            Is.EqualTo(DdgiRuntimeWarmupState.Recovery));
    }

    [Test]
    public void Resolve_ReportsLocalWarmupWhileARefinementBrickFallsBack()
    {
        Assert.That(
            Resolve(
                100,
                true,
                true,
                false,
                SimpleDdgiTransportPhase.AuditFrozen,
                Ready(admitted: 1, ready: 0, fallback: 1)),
            Is.EqualTo(DdgiRuntimeWarmupState.LocalVolumeWarmup));
    }

    [Test]
    public void Resolve_ReportsNearWarmupWhileGlobalTransportIsPending()
    {
        Assert.That(
            Resolve(
                100,
                true,
                true,
                false,
                SimpleDdgiTransportPhase.AuditFrozen,
                Ready()),
            Is.EqualTo(DdgiRuntimeWarmupState.NearCascadeWarmup));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    public void Resolve_DoesNotReportSteadyWithoutACurrentCertificate(
        bool certificationEnabled,
        bool certificateCurrent)
    {
        Assert.That(
            Resolve(
                100,
                false,
                certificationEnabled,
                certificateCurrent,
                SimpleDdgiTransportPhase.Certified,
                Ready()),
            Is.EqualTo(DdgiRuntimeWarmupState.NearCascadeWarmup));
    }

    [Test]
    public void Resolve_ReportsSteadyOnlyForCurrentReadyTransport()
    {
        Assert.That(
            Resolve(
                100,
                false,
                true,
                true,
                SimpleDdgiTransportPhase.Certified,
                Ready(admitted: 1, ready: 1, fallback: 0)),
            Is.EqualTo(DdgiRuntimeWarmupState.SteadyState));
    }

    private static DdgiRuntimeWarmupState Resolve(
        int probes,
        bool pending,
        bool certificationEnabled,
        bool certificateCurrent,
        SimpleDdgiTransportPhase phase,
        SimpleDdgiRefinementBrickDiagnostics refinement) =>
        DdgiFrameDataProjector.ResolveSimpleDdgiWarmupState(
            probes,
            pending,
            certificationEnabled,
            certificateCurrent,
            phase,
            refinement);

    private static SimpleDdgiRefinementBrickDiagnostics Ready(
        int admitted = 0,
        int ready = 0,
        int fallback = 0) => new(
        Requested: true,
        RequestedBrickCount: admitted,
        AdmittedBrickCount: admitted,
        ReceiverReadyBrickCount: ready,
        BaseFallbackBrickCount: fallback,
        AllocatedProbeCount: admitted * 216,
        EvictionCount: 0,
        TopologyChangedThisFrame: false,
        AdmissionStatus: "test");
}
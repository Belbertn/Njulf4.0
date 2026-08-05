using System.Collections.Generic;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleDdgiResidencySwitchSmokeRunnerTests
{
    [Test]
    public void VisitsDenseShadowSparseAndRestoresExactInitialSettings()
    {
        SimpleDdgiProbeResidencyMode mode =
            SimpleDdgiProbeResidencyMode.SparseNearRing;
        const string initialFingerprint = "settings-sparse";
        string fingerprint = initialFingerprint;
        bool exited = false;
        var operations = new List<SampleSmokeOperationResult>();
        var runner = new SampleDdgiResidencySwitchSmokeRunner(
            next =>
            {
                mode = next;
                fingerprint = $"settings-{next}";
            },
            () =>
            {
                mode = SimpleDdgiProbeResidencyMode.SparseNearRing;
                fingerprint = initialFingerprint;
            },
            () => mode,
            () => fingerprint,
            () => "device-1",
            operations.Add,
            () => exited = true);

        runner.OnFrameRendered(0, Diagnostics(
            SimpleDdgiProbeResidencyMode.SparseNearRing,
            feedbackValid: true,
            generation: 1));
        runner.OnFrameRendered(1, Diagnostics(
            SimpleDdgiProbeResidencyMode.Dense,
            feedbackValid: false,
            generation: 1));
        runner.OnFrameRendered(2, Diagnostics(
            SimpleDdgiProbeResidencyMode.Shadow,
            feedbackValid: false,
            generation: 2));
        Assert.That(runner.Observations, Has.Count.EqualTo(1));
        runner.OnFrameRendered(3, Diagnostics(
            SimpleDdgiProbeResidencyMode.Shadow,
            feedbackValid: true,
            generation: 2));
        runner.OnFrameRendered(4, Diagnostics(
            SimpleDdgiProbeResidencyMode.SparseNearRing,
            feedbackValid: true,
            generation: 3));
        runner.OnFrameRendered(5, Diagnostics(
            SimpleDdgiProbeResidencyMode.SparseNearRing,
            feedbackValid: true,
            generation: 3));

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Is.Null);
            Assert.That(exited, Is.True);
            Assert.That(mode, Is.EqualTo(
                SimpleDdgiProbeResidencyMode.SparseNearRing));
            Assert.That(fingerprint, Is.EqualTo(initialFingerprint));
            Assert.That(runner.Observations, Has.Count.EqualTo(4));
            Assert.That(
                runner.Observations[0].Mode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Dense));
            Assert.That(
                runner.Observations[1].Mode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.Shadow));
            Assert.That(
                runner.Observations[2].Mode,
                Is.EqualTo(SimpleDdgiProbeResidencyMode.SparseNearRing));
            Assert.That(operations, Has.Count.EqualTo(1));
            Assert.That(operations[0].Name, Is.EqualTo(
                "ddgi-residency-switch"));
            Assert.That(operations[0].Status, Is.EqualTo("passed"));
        });
    }

    [Test]
    public void DenseModeRejectsStaleSparseFeedbackAuthority()
    {
        SimpleDdgiProbeResidencyMode mode =
            SimpleDdgiProbeResidencyMode.SparseNearRing;
        string fingerprint = "initial";
        var operations = new List<SampleSmokeOperationResult>();
        var runner = new SampleDdgiResidencySwitchSmokeRunner(
            next =>
            {
                mode = next;
                fingerprint = next.ToString();
            },
            () =>
            {
                mode = SimpleDdgiProbeResidencyMode.SparseNearRing;
                fingerprint = "initial";
            },
            () => mode,
            () => fingerprint,
            () => "device-1",
            operations.Add,
            () => { });

        runner.OnFrameRendered(0, Diagnostics(
            SimpleDdgiProbeResidencyMode.SparseNearRing,
            feedbackValid: true,
            generation: 1));
        runner.OnFrameRendered(1, Diagnostics(
            SimpleDdgiProbeResidencyMode.Dense,
            feedbackValid: true,
            generation: 1,
            pageArenaBytes: 128));

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain(
                "Dense mode retained sparse residency authority or feedback"));
            Assert.That(operations, Has.Count.EqualTo(1));
            Assert.That(operations[0].Status, Is.EqualTo("failed"));
            Assert.That(mode, Is.EqualTo(
                SimpleDdgiProbeResidencyMode.SparseNearRing));
            Assert.That(fingerprint, Is.EqualTo("initial"));
        });
    }

    [Test]
    public void MappingIntegrityFailureFailsClosedAndRollsBack()
    {
        SimpleDdgiProbeResidencyMode mode =
            SimpleDdgiProbeResidencyMode.SparseNearRing;
        string fingerprint = "initial";
        var runner = new SampleDdgiResidencySwitchSmokeRunner(
            next =>
            {
                mode = next;
                fingerprint = next.ToString();
            },
            () =>
            {
                mode = SimpleDdgiProbeResidencyMode.SparseNearRing;
                fingerprint = "initial";
            },
            () => mode,
            () => fingerprint,
            () => "device-1",
            _ => { },
            () => { });

        runner.OnFrameRendered(0, Diagnostics(
            SimpleDdgiProbeResidencyMode.SparseNearRing,
            feedbackValid: true,
            generation: 1));
        runner.OnFrameRendered(1, Diagnostics(
            SimpleDdgiProbeResidencyMode.Dense,
            feedbackValid: false,
            generation: 1) with
        {
            SimpleDdgiProbeResidency = Diagnostics(
                SimpleDdgiProbeResidencyMode.Dense,
                feedbackValid: false,
                generation: 1).SimpleDdgiProbeResidency with
            {
                OutOfRangeRequestCount = 1
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(runner.Completed, Is.True);
            Assert.That(runner.Failure, Does.Contain(
                "reported an out-of-range request"));
            Assert.That(mode, Is.EqualTo(
                SimpleDdgiProbeResidencyMode.SparseNearRing));
            Assert.That(fingerprint, Is.EqualTo("initial"));
        });
    }

    private static RendererDiagnostics Diagnostics(
        SimpleDdgiProbeResidencyMode mode,
        bool feedbackValid,
        uint generation,
        ulong pageArenaBytes = 0UL)
    {
        bool collectsDemand = mode.CollectsDemand();
        return RendererDiagnostics.Empty with
        {
            SimpleDdgiProbeResidency =
                new SimpleDdgiProbeResidencyTelemetry(
                    true,
                    mode,
                    mode.UsesSparsePayloads(),
                    string.Empty)
                {
                    CurrentResourceGeneration = generation,
                    FeedbackValid = feedbackValid,
                    FeedbackResourceGeneration = feedbackValid
                        ? generation
                        : 0u,
                    ResidencyStateValid = collectsDemand,
                    PageArenaBytes = pageArenaBytes != 0UL
                        ? pageArenaBytes
                        : collectsDemand
                            ? 128UL
                            : 0UL,
                    FeedbackReadbackBytes = collectsDemand ? 64UL : 0UL,
                    ResidentPageCount = feedbackValid ? 4 : 0
                }
        };
    }
}

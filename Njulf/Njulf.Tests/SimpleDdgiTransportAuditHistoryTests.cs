using System.Diagnostics;
using System.Text.Json;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiTransportAuditHistoryTests
{
    private static SimpleDdgiTransportGenerations Generations => new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

    [Test]
    public void AuditHistory_DistinguishesSubmissionCancellationAndObservedCertification()
    {
        var history = new SimpleDdgiTransportAuditHistory();
        long start = Stopwatch.Frequency;
        history.SetTrigger(SimpleDdgiTransportAuditTrigger.LightingChange,
            SimpleDdgiTransportCertificationReason.SourceRepairRequired);
        history.Begin(Generations, 21, 99, 3, 300, start);
        history.SubmitChunk(100, 1, false, start + Stopwatch.Frequency);
        var firstSubmission = history.Current;
        history.SubmitChunk(100, 2, false, start + Stopwatch.Frequency);
        Assert.That(history.Current, Is.EqualTo(firstSubmission), "Intermediate chunks do not rewrite an existing transition.");
        history.SubmitChunk(101, 3, true, start + 2 * Stopwatch.Frequency);
        var dispatchSnapshot = history.Snapshot();
        Assert.That(history.Current.Kind, Is.EqualTo(SimpleDdgiTransportAuditEventKind.DispatchComplete));

        var changed = Generations with { SourceLighting = 11 };
        history.Finish(SimpleDdgiTransportAuditEventKind.Cancelled,
            SimpleDdgiTransportCertificationReason.GenerationsChanged, changed, 22, 102,
            start + 3 * Stopwatch.Frequency);
        history.Finish(SimpleDdgiTransportAuditEventKind.Cancelled,
            SimpleDdgiTransportCertificationReason.GenerationsChanged, changed, 22, 102,
            start + 3 * Stopwatch.Frequency);
        Assert.Multiple(() =>
        {
            Assert.That(history.Snapshot(), Has.Count.EqualTo(4), "A repeated termination is not another audit event.");
            Assert.That(dispatchSnapshot, Has.Count.EqualTo(3), "Previously captured evidence is immutable.");
            Assert.That(history.Current.FrozenGenerations, Is.EqualTo(Generations));
            Assert.That(history.Current.CurrentGenerations, Is.EqualTo(changed));
            Assert.That(history.Current.AdmissionVolumeTableGeneration, Is.EqualTo(21));
            Assert.That(history.Current.VolumeTableGeneration, Is.EqualTo(22));
            Assert.That(history.Current.FirstSubmissionFrameSerial, Is.EqualTo(100));
            Assert.That(history.Current.FinalSubmissionFrameSerial, Is.EqualTo(101));
            Assert.That(history.Current.FrameSerial, Is.EqualTo(102));
            Assert.That(history.Current.ElapsedMicroseconds, Is.EqualTo(3_000_000));
        });

        var retry = changed with { Solve = 8, Audit = 9 };
        history.Begin(retry, 22, 103, 1, 300, start + 4 * Stopwatch.Frequency);
        history.SubmitChunk(104, 1, true, start + 5 * Stopwatch.Frequency);
        history.Finish(SimpleDdgiTransportAuditEventKind.Certified,
            SimpleDdgiTransportCertificationReason.Certified, retry, 22, 106,
            start + 7 * Stopwatch.Frequency);
        var roundTrip = JsonSerializer.Deserialize<SimpleDdgiTransportAuditEvent>(
            JsonSerializer.Serialize(history.Current));
        Assert.Multiple(() =>
        {
            Assert.That(history.Current.Trigger, Is.EqualTo(SimpleDdgiTransportAuditTrigger.AuditRecovery));
            Assert.That(history.Current.TriggerReason, Is.EqualTo(SimpleDdgiTransportCertificationReason.GenerationsChanged));
            Assert.That(history.Current.Kind, Is.EqualTo(SimpleDdgiTransportAuditEventKind.Certified));
            Assert.That(history.Current.FrameSerial, Is.EqualTo(106));
            Assert.That(history.Current.FinalSubmissionFrameSerial, Is.EqualTo(104));
            Assert.That(roundTrip, Is.EqualTo(history.Current));
        });
    }

    [Test]
    public void AuditHistory_BoundsStorageAndReportsEvictedTransitions()
    {
        var history = new SimpleDdgiTransportAuditHistory();
        for (uint audit = 1; audit <= 40; audit++)
        {
            var generations = Generations with { Audit = audit };
            history.Begin(generations, 1, audit, 1, 1, 0);
            history.Finish(SimpleDdgiTransportAuditEventKind.TimedOut,
                SimpleDdgiTransportCertificationReason.AuditReadbackTimeout, generations, 1, audit, 1);
        }
        var snapshot = history.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Has.Count.EqualTo(SimpleDdgiTransportAuditHistory.Capacity));
            Assert.That(snapshot[0].Sequence, Is.EqualTo(17));
            Assert.That(snapshot[^1].Sequence, Is.EqualTo(80));
            Assert.That(history.DroppedEventCount, Is.EqualTo(16));
            Assert.That(history.Snapshot(), Is.SameAs(snapshot), "Idle polling must not allocate history copies.");
        });
    }
}

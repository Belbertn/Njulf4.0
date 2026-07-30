using System;
using System.Threading.Tasks;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererValidationMessageStateTests
{
    [Test]
    public void SnapshotCountsEverySeverityAndRetainsErrorContext()
    {
        var state = new RendererValidationMessageState();

        state.Record(RendererValidationMessageSeverity.Verbose, "verbose");
        state.Record(RendererValidationMessageSeverity.Information, "info");
        state.Record(RendererValidationMessageSeverity.Warning, "warning");
        state.Record(RendererValidationMessageSeverity.Error, " first error ");
        state.Record(RendererValidationMessageSeverity.Error, "last error");

        RendererValidationMessageSnapshot snapshot = state.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.VerboseCount, Is.EqualTo(1));
            Assert.That(snapshot.InformationCount, Is.EqualTo(1));
            Assert.That(snapshot.WarningCount, Is.EqualTo(1));
            Assert.That(snapshot.ErrorCount, Is.EqualTo(2));
            Assert.That(snapshot.TotalCount, Is.EqualTo(5));
            Assert.That(snapshot.FirstWarningMessage, Is.EqualTo("warning"));
            Assert.That(snapshot.LastWarningMessage, Is.EqualTo("warning"));
            Assert.That(snapshot.FirstErrorMessage, Is.EqualTo("first error"));
            Assert.That(snapshot.LastErrorMessage, Is.EqualTo("last error"));
        });
    }

    [Test]
    public void ConcurrentCallbackUpdatesAreNotLost()
    {
        var state = new RendererValidationMessageState();

        Parallel.For(0, 2_000, index =>
        {
            state.Record(
                index % 2 == 0
                    ? RendererValidationMessageSeverity.Warning
                    : RendererValidationMessageSeverity.Error,
                $"message-{index}");
        });

        RendererValidationMessageSnapshot snapshot = state.Snapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.WarningCount, Is.EqualTo(1_000));
            Assert.That(snapshot.ErrorCount, Is.EqualTo(1_000));
            Assert.That(snapshot.FirstWarningMessage, Is.Not.Empty);
            Assert.That(snapshot.LastWarningMessage, Is.Not.Empty);
            Assert.That(snapshot.FirstErrorMessage, Is.Not.Empty);
            Assert.That(snapshot.LastErrorMessage, Is.Not.Empty);
        });
    }

    [Test]
    public void FailOnErrorIsDeferredToManagedBoundary()
    {
        var state = new RendererValidationMessageState();
        state.Record(RendererValidationMessageSeverity.Error, "VUID-test");

        Assert.DoesNotThrow(() => state.ThrowIfErrorRequested(failOnErrorMessage: false));
        RendererValidationException exception = Assert.Throws<RendererValidationException>(
            () => state.ThrowIfErrorRequested(failOnErrorMessage: true))!;

        Assert.That(exception.Message, Does.Contain("1 error message"));
        Assert.That(exception.Message, Does.Contain("VUID-test"));
    }

    [Test]
    public void RetainedErrorTextIsBounded()
    {
        var state = new RendererValidationMessageState();
        state.Record(RendererValidationMessageSeverity.Error, new string('x', 10_000));

        RendererValidationMessageSnapshot snapshot = state.Snapshot();

        Assert.That(snapshot.FirstErrorMessage.Length, Is.EqualTo(4096));
        Assert.That(snapshot.LastErrorMessage, Is.EqualTo(snapshot.FirstErrorMessage));
    }

    [Test]
    public void RetainedWarningTextIsBounded()
    {
        var state = new RendererValidationMessageState();
        state.Record(RendererValidationMessageSeverity.Warning, new string('w', 10_000));

        RendererValidationMessageSnapshot snapshot = state.Snapshot();

        Assert.That(snapshot.FirstWarningMessage.Length, Is.EqualTo(4096));
        Assert.That(snapshot.LastWarningMessage, Is.EqualTo(snapshot.FirstWarningMessage));
    }

    [Test]
    public void UnknownSeverityIsRejected()
    {
        var state = new RendererValidationMessageState();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => state.Record((RendererValidationMessageSeverity)byte.MaxValue, "bad"));
    }
}

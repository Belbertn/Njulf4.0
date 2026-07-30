using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class CommittedResourceCleanupTests
{
    [Test]
    public void RetirementFailure_IsReported_AndFenceCleanupStillRuns()
    {
        var calls = new List<string>();
        var failures = new List<Exception>();

        Assert.That(
            () => CommittedResourceCleanup.Execute(
                () =>
                {
                    calls.Add("retire");
                    throw new InvalidOperationException(
                        "retirement failed");
                },
                () => calls.Add("fence"),
                failure =>
                {
                    calls.Add("report");
                    failures.Add(failure);
                }),
            Throws.Nothing);

        Assert.Multiple(() =>
        {
            Assert.That(
                calls,
                Is.EqualTo(
                    new[] { "retire", "report", "fence" }));
            Assert.That(failures, Has.Count.EqualTo(1));
            Assert.That(
                failures[0].Message,
                Is.EqualTo("retirement failed"));
        });
    }

    [Test]
    public void EveryCleanupAndReporterFailure_RemainsPostCommitNonThrowing()
    {
        int reportCalls = 0;
        int cleanupCalls = 0;

        Assert.That(
            () => CommittedResourceCleanup.Execute(
                () =>
                {
                    cleanupCalls++;
                    throw new InvalidOperationException(
                        "buffer cleanup");
                },
                () =>
                {
                    cleanupCalls++;
                    throw new InvalidOperationException(
                        "fence cleanup");
                },
                _ =>
                {
                    reportCalls++;
                    throw new InvalidOperationException(
                        "diagnostic sink");
                }),
            Throws.Nothing);

        Assert.Multiple(() =>
        {
            Assert.That(cleanupCalls, Is.EqualTo(2));
            Assert.That(reportCalls, Is.EqualTo(2));
        });
    }
}

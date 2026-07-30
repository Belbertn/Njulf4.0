using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialBufferReplacementTransactionTests
{
    [TestCase(
        (int)MaterialBufferBindingPublicationStage
            .BeforeCandidatePublication)]
    [TestCase(
        (int)MaterialBufferBindingPublicationStage
            .AfterCandidateMaterialBinding)]
    [TestCase(
        (int)MaterialBufferBindingPublicationStage
            .AfterCandidateExtensionBinding)]
    public void CandidateDescriptorFailure_RestoresOldBindingBeforeDestroy(
        int failedStageValue)
    {
        var failedStage =
            (MaterialBufferBindingPublicationStage)
            failedStageValue;
        var calls = new List<string>();
        string authoritative = "old";
        bool candidateAlive = true;

        void Inject(
            MaterialBufferBindingPublicationStage stage)
        {
            calls.Add(stage.ToString());
            if (stage == failedStage)
            {
                throw new InvalidOperationException(
                    $"Injected {stage}.");
            }
        }

        Assert.That(
            () => MaterialBufferReplacementTransaction.Execute(
                publishCandidateBinding: () =>
                {
                    Inject(
                        MaterialBufferBindingPublicationStage
                            .BeforeCandidatePublication);
                    Inject(
                        MaterialBufferBindingPublicationStage
                            .AfterCandidateMaterialBinding);
                    Inject(
                        MaterialBufferBindingPublicationStage
                            .AfterCandidateExtensionBinding);
                },
                commitAuthoritativeState: () =>
                    authoritative = "candidate",
                restoreAuthoritativeBinding: () =>
                {
                    calls.Add("restore-old");
                    authoritative = "old";
                },
                destroyCandidate: () =>
                {
                    calls.Add("destroy-candidate");
                    candidateAlive = false;
                },
                retireCandidate: () =>
                    calls.Add("retire-candidate"),
                quarantineCandidate: () =>
                    calls.Add("quarantine-candidate"),
                reportDeferredCandidateCleanup: _ =>
                    calls.Add("report-cleanup")),
            Throws.TypeOf<InvalidOperationException>());

        Assert.Multiple(() =>
        {
            Assert.That(authoritative, Is.EqualTo("old"));
            Assert.That(candidateAlive, Is.False);
            Assert.That(
                calls.IndexOf("restore-old"),
                Is.LessThan(
                    calls.IndexOf("destroy-candidate")));
            Assert.That(
                calls,
                Does.Not.Contain("retire-candidate"));
            Assert.That(
                calls,
                Does.Not.Contain("quarantine-candidate"));
        });
    }

    [Test]
    public void DescriptorRestoreFailure_QuarantinesLiveCandidate()
    {
        bool destroyed = false;
        bool quarantined = false;

        AggregateException failure =
            Assert.Throws<AggregateException>(
                () =>
                    MaterialBufferReplacementTransaction
                        .Execute(
                            publishCandidateBinding: () =>
                                throw new InvalidOperationException(
                                    "candidate publication"),
                            commitAuthoritativeState:
                                static () => { },
                            restoreAuthoritativeBinding: () =>
                                throw new InvalidOperationException(
                                    "authoritative restore"),
                            destroyCandidate: () =>
                                destroyed = true,
                            retireCandidate:
                                static () => { },
                            quarantineCandidate: () =>
                                quarantined = true,
                            reportDeferredCandidateCleanup:
                                static _ => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                failure.InnerExceptions,
                Has.Count.EqualTo(2));
            Assert.That(quarantined, Is.True);
            Assert.That(destroyed, Is.False);
        });
    }

    [Test]
    public void CandidateDestroyFailure_IsDurablyRetiredWithoutMaskingPublicationFailure()
    {
        var retired = new List<int>();
        var reported = new List<Exception>();
        InvalidOperationException publicationFailure =
            new("candidate publication");

        Exception thrown = Assert.Throws<InvalidOperationException>(
            () => MaterialBufferReplacementTransaction.Execute(
                publishCandidateBinding: () =>
                    throw publicationFailure,
                commitAuthoritativeState:
                    static () => { },
                restoreAuthoritativeBinding:
                    static () => { },
                destroyCandidate: () =>
                    throw new InvalidOperationException(
                        "candidate cleanup"),
                retireCandidate: () =>
                    retired.Add(42),
                quarantineCandidate:
                    static () => { },
                reportDeferredCandidateCleanup:
                    reported.Add))!;

        int destroys = 0;
        List<Exception>? retryFailures = null;
        DurableResourceDestruction.TryDestroyAll(
            retired,
            static _ => true,
            _ => destroys++,
            ref retryFailures);
        DurableResourceDestruction.TryDestroyAll(
            retired,
            static _ => true,
            _ => destroys++,
            ref retryFailures);

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(publicationFailure));
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(retired, Is.Empty);
            Assert.That(retryFailures, Is.Null);
            Assert.That(destroys, Is.EqualTo(1));
        });
    }

    [Test]
    public void SuccessfulPublication_CommitsWithoutRollbackCleanup()
    {
        var calls = new List<string>();

        MaterialBufferReplacementTransaction.Execute(
            publishCandidateBinding: () =>
                calls.Add("publish"),
            commitAuthoritativeState: () =>
                calls.Add("commit"),
            restoreAuthoritativeBinding: () =>
                calls.Add("restore"),
            destroyCandidate: () =>
                calls.Add("destroy"),
            retireCandidate: () =>
                calls.Add("retire"),
            quarantineCandidate: () =>
                calls.Add("quarantine"),
            reportDeferredCandidateCleanup: _ =>
                calls.Add("report"));

        Assert.That(
            calls,
            Is.EqualTo(
                new[] { "publish", "commit" }));
    }
}

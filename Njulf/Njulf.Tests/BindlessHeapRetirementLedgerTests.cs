using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BindlessHeapRetirementLedgerTests
{
    private static readonly BindlessHeapOwnedResource[] AllResources =
        Enum.GetValues<BindlessHeapOwnedResource>();

    [TestCaseSource(nameof(AllResources))]
    public void FailureAtEveryResource_IsRetainedAndRetriedExactlyOnce(
        object failedResourceValue)
    {
        var failedResource =
            (BindlessHeapOwnedResource)failedResourceValue;
        var ledger = CreateCompleteLedger();
        var calls = AllResources.ToDictionary(resource => resource, _ => 0);
        bool injectFailure = true;

        Assert.That(
            () => ledger.Retire(resource =>
            {
                calls[resource]++;
                if (injectFailure && resource == failedResource)
                    throw new InvalidOperationException("injected");
            }),
            Throws.TypeOf<AggregateException>());

        Assert.That(ledger.IsPending(failedResource), Is.True);

        injectFailure = false;
        ledger.Retire(resource => calls[resource]++);
        ledger.Retire(resource => calls[resource]++);

        Assert.Multiple(() =>
        {
            Assert.That(ledger.IsEmpty, Is.True);
            Assert.That(calls[failedResource], Is.EqualTo(2));
            Assert.That(
                calls.Where(pair => pair.Key != failedResource)
                    .Select(pair => pair.Value),
                Is.All.EqualTo(1));
        });
    }

    [Test]
    public void PoolFailures_BlockOnlyTheirDependentsAndIndependentBranchesProgress()
    {
        var ledger = CreateCompleteLedger();
        var attempted = new List<BindlessHeapOwnedResource>();

        Assert.That(
            () => ledger.Retire(resource =>
            {
                attempted.Add(resource);
                if (resource is
                    BindlessHeapOwnedResource.StorageBufferPool or
                    BindlessHeapOwnedResource.TextureSamplerPool)
                {
                    throw new InvalidOperationException("injected");
                }
            }),
            Throws.TypeOf<AggregateException>());

        Assert.Multiple(() =>
        {
            Assert.That(
                attempted,
                Is.EqualTo(
                    new[]
                    {
                        BindlessHeapOwnedResource.StorageBufferPool,
                        BindlessHeapOwnedResource.TextureSamplerPool
                    }));
            Assert.That(
                AllResources.All(ledger.IsPending),
                Is.True);
        });
    }

    [Test]
    public void PartialConstruction_RetiresOnlyAcquiredResources()
    {
        var ledger = new BindlessHeapRetirementLedger();
        ledger.Add(BindlessHeapOwnedResource.StorageBufferSetLayout);
        ledger.Add(BindlessHeapOwnedResource.StorageBufferPool);
        var attempted = new List<BindlessHeapOwnedResource>();

        ledger.Retire(attempted.Add);

        Assert.Multiple(() =>
        {
            Assert.That(
                attempted,
                Is.EqualTo(
                    new[]
                    {
                        BindlessHeapOwnedResource.StorageBufferPool,
                        BindlessHeapOwnedResource.StorageBufferSetLayout
                    }));
            Assert.That(ledger.IsEmpty, Is.True);
        });
    }

    [Test]
    public void DuplicateOwnership_IsRejected()
    {
        var ledger = new BindlessHeapRetirementLedger();
        ledger.Add(BindlessHeapOwnedResource.DefaultSampler);

        Assert.That(
            () => ledger.Add(BindlessHeapOwnedResource.DefaultSampler),
            Throws.TypeOf<InvalidOperationException>());
    }

    private static BindlessHeapRetirementLedger CreateCompleteLedger()
    {
        var ledger = new BindlessHeapRetirementLedger();
        foreach (BindlessHeapOwnedResource resource in AllResources)
            ledger.Add(resource);

        return ledger;
    }
}

using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DurableResourceDestructionTests
{
    [Test]
    public void IndividualResource_IsInvalidatedOnlyAfterSuccessfulDestroy()
    {
        var resource = new TestResource(1);
        int calls = 0;

        Exception? first =
            DurableResourceDestruction.TryDestroy(
                ref resource,
                TestResource.Invalid,
                static value => value.IsValid,
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException(
                        "injected");
                });

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.TypeOf<InvalidOperationException>());
            Assert.That(resource, Is.EqualTo(new TestResource(1)));
            Assert.That(calls, Is.EqualTo(1));
        });

        Exception? retry =
            DurableResourceDestruction.TryDestroy(
                ref resource,
                TestResource.Invalid,
                static value => value.IsValid,
                _ => calls++);
        Exception? completed =
            DurableResourceDestruction.TryDestroy(
                ref resource,
                TestResource.Invalid,
                static value => value.IsValid,
                _ => calls++);

        Assert.Multiple(() =>
        {
            Assert.That(retry, Is.Null);
            Assert.That(completed, Is.Null);
            Assert.That(resource, Is.EqualTo(TestResource.Invalid));
            Assert.That(calls, Is.EqualTo(2));
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void StagedResources_FailureAtEveryPositionRetriesOnlyThatResource(
        int failedPosition)
    {
        var resources = new List<TestResource>
        {
            new(1),
            new(2),
            new(3),
            new(4)
        };
        var callsByResource = new Dictionary<int, int>();
        int call = 0;
        int failedResource = 0;
        List<Exception>? firstFailures = null;

        DurableResourceDestruction.TryDestroyAll(
            resources,
            static value => value.IsValid,
            value =>
            {
                callsByResource.TryGetValue(
                    value.Id,
                    out int count);
                callsByResource[value.Id] = count + 1;
                int position = ++call;
                if (position == failedPosition)
                {
                    failedResource = value.Id;
                    throw new InvalidOperationException(
                        $"Injected disposal failure {position}.");
                }
            },
            ref firstFailures);

        Assert.Multiple(() =>
        {
            Assert.That(firstFailures, Has.Count.EqualTo(1));
            Assert.That(resources, Is.EqualTo(
                new[] { new TestResource(failedResource) }));
        });

        List<Exception>? retryFailures = null;
        DurableResourceDestruction.TryDestroyAll(
            resources,
            static value => value.IsValid,
            value =>
            {
                callsByResource[value.Id]++;
                call++;
            },
            ref retryFailures);
        int callsAfterCompletion = call;
        DurableResourceDestruction.TryDestroyAll(
            resources,
            static value => value.IsValid,
            _ => call++,
            ref retryFailures);

        Assert.Multiple(() =>
        {
            Assert.That(retryFailures, Is.Null);
            Assert.That(resources, Is.Empty);
            Assert.That(callsByResource[failedResource], Is.EqualTo(2));
            Assert.That(
                callsByResource
                    .Where(pair =>
                        pair.Key != failedResource)
                    .Select(pair => pair.Value),
                Is.All.EqualTo(1));
            Assert.That(call, Is.EqualTo(callsAfterCompletion));
        });
    }

    [Test]
    public void StagedResources_AttemptEveryOriginalEntryInStableOrder()
    {
        var resources = new List<MeshManagerDisposalResource>
        {
            MeshManagerDisposalResource.VertexPositionBuffer,
            MeshManagerDisposalResource.IndexBuffer,
            MeshManagerDisposalResource.QuarantinedUploadBuffer,
            MeshManagerDisposalResource.QuarantinedUploadFence
        };
        var attempted = new List<MeshManagerDisposalResource>();
        List<Exception>? failures = null;

        DurableResourceDestruction.TryDestroyAll(
            resources,
            static _ => true,
            resource =>
            {
                attempted.Add(resource);
                if (resource is
                    MeshManagerDisposalResource.IndexBuffer or
                    MeshManagerDisposalResource.QuarantinedUploadFence)
                {
                    throw new InvalidOperationException(
                        "injected");
                }
            },
            ref failures);

        Assert.Multiple(() =>
        {
            Assert.That(
                attempted,
                Is.EqualTo(
                    new[]
                    {
                        MeshManagerDisposalResource.VertexPositionBuffer,
                        MeshManagerDisposalResource.IndexBuffer,
                        MeshManagerDisposalResource.QuarantinedUploadBuffer,
                        MeshManagerDisposalResource.QuarantinedUploadFence
                    }));
            Assert.That(
                resources,
                Is.EqualTo(
                    new[]
                    {
                        MeshManagerDisposalResource.IndexBuffer,
                        MeshManagerDisposalResource.QuarantinedUploadFence
                    }));
            Assert.That(failures, Has.Count.EqualTo(2));
        });
    }

    private readonly record struct TestResource(int Id)
    {
        public static TestResource Invalid { get; } =
            new(0);

        public bool IsValid => Id > 0;
    }
}

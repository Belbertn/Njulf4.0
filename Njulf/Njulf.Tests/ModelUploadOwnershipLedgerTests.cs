using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelUploadOwnershipLedgerTests
{
    [Test]
    public void Rollback_ReleasesPendingTexturesThenCommittedMaterialsInReverseOrder()
    {
        var released = new List<string>();
        var ledger = new ModelUploadOwnershipLedger(
            materialCapacity: 2,
            pendingTextureCapacity: 3,
            material => released.Add($"material:{material.Index}"),
            texture => released.Add($"texture:{texture.Index}"));

        ledger.PendingTextures.Add(new TextureHandle(10, 1));
        ledger.PendingTextures.Add(new TextureHandle(11, 1));
        ledger.CommitPendingTexturesTo(new MaterialHandle(20, 1));
        ledger.PendingTextures.Add(new TextureHandle(12, 1));
        ledger.PendingTextures.Add(new TextureHandle(13, 1));

        Exception? failure = ledger.TryRollback();

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.Null);
            Assert.That(
                released,
                Is.EqualTo(
                new[]
                {
                    "texture:13",
                    "texture:12",
                    "material:20"
                }));
        });
    }

    [Test]
    public void Rollback_AttemptsEveryReleaseAndRetriesOnlyFailures()
    {
        var released = new List<string>();
        var failedOnce = new HashSet<string>();
        var ledger = new ModelUploadOwnershipLedger(
            materialCapacity: 2,
            pendingTextureCapacity: 2,
            material =>
            {
                string key = $"material:{material.Index}";
                released.Add(key);
                if (material.Index == 20 &&
                    failedOnce.Add(key))
                {
                    throw new InvalidOperationException("material release failed");
                }
            },
            texture =>
            {
                string key = $"texture:{texture.Index}";
                released.Add(key);
                if (texture.Index == 11 &&
                    failedOnce.Add(key))
                {
                    throw new InvalidOperationException("texture release failed");
                }
            });

        ledger.PendingTextures.Add(new TextureHandle(10, 1));
        ledger.CommitPendingTexturesTo(new MaterialHandle(20, 1));
        ledger.PendingTextures.Add(new TextureHandle(11, 1));

        Exception? failure = ledger.TryRollback();
        Exception? repeatedFailure = ledger.TryRollback();
        Exception? completedRollback = ledger.TryRollback();

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.TypeOf<AggregateException>());
            Assert.That(((AggregateException)failure!).InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(repeatedFailure, Is.Null);
            Assert.That(completedRollback, Is.Null);
            Assert.That(ledger.PendingTextureCount, Is.Zero);
            Assert.That(ledger.PendingMaterialCount, Is.Zero);
            Assert.That(ledger.RollbackCompleted, Is.True);
            Assert.That(
                released.Count(static item => item == "texture:11"),
                Is.EqualTo(2));
            Assert.That(
                released.Count(static item => item == "material:20"),
                Is.EqualTo(2));
        });
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void RollbackFailureAtEveryPosition_RetryIsExactOnce(
        int failedPosition)
    {
        int call = 0;
        var callsByResource = new Dictionary<string, int>();
        string? failedResource = null;

        void Release(string resource)
        {
            callsByResource.TryGetValue(resource, out int count);
            callsByResource[resource] = count + 1;
            int position = Interlocked.Increment(ref call);
            if (position == failedPosition)
            {
                failedResource = resource;
                throw new InvalidOperationException(
                    $"Injected rollback failure at {position}.");
            }
        }

        var ledger = new ModelUploadOwnershipLedger(
            materialCapacity: 2,
            pendingTextureCapacity: 2,
            material => Release($"material:{material.Index}"),
            texture => Release($"texture:{texture.Index}"));
        ledger.PendingTextures.Add(
            new TextureHandle(10, 1));
        ledger.CommitPendingTexturesTo(
            new MaterialHandle(20, 1));
        ledger.PendingTextures.Add(
            new TextureHandle(11, 1));
        ledger.CommitPendingTexturesTo(
            new MaterialHandle(21, 1));
        ledger.PendingTextures.Add(
            new TextureHandle(12, 1));
        ledger.PendingTextures.Add(
            new TextureHandle(13, 1));

        Exception? first = ledger.TryRollback();
        Exception? retry = ledger.TryRollback();
        int callsAfterCompletion = call;
        Exception? completed = ledger.TryRollback();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.TypeOf<AggregateException>());
            Assert.That(retry, Is.Null);
            Assert.That(completed, Is.Null);
            Assert.That(ledger.RollbackCompleted, Is.True);
            Assert.That(
                ledger.PendingTextureCount +
                ledger.PendingMaterialCount,
                Is.Zero);
            Assert.That(failedResource, Is.Not.Null);
            Assert.That(
                callsByResource[failedResource!],
                Is.EqualTo(2));
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
    public async Task ConcurrentRollback_WaitsAndNeverDuplicatesCompletedWork()
    {
        using var releaseEntered = new ManualResetEventSlim(false);
        using var allowRelease = new ManualResetEventSlim(false);
        var calls = new Dictionary<string, int>();
        object callLock = new();

        void Record(string resource)
        {
            lock (callLock)
            {
                calls.TryGetValue(resource, out int count);
                calls[resource] = count + 1;
            }
        }

        var ledger = new ModelUploadOwnershipLedger(
            materialCapacity: 1,
            pendingTextureCapacity: 2,
            material =>
                Record($"material:{material.Index}"),
            texture =>
            {
                Record($"texture:{texture.Index}");
                releaseEntered.Set();
                if (!allowRelease.Wait(
                        TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Timed out waiting for rollback test release.");
                }
            });
        ledger.PendingTextures.Add(
            new TextureHandle(10, 1));
        ledger.CommitPendingTexturesTo(
            new MaterialHandle(20, 1));
        ledger.PendingTextures.Add(
            new TextureHandle(11, 1));

        Task<Exception?> first = Task.Run(
            ledger.TryRollback);
        Assert.That(
            releaseEntered.Wait(
                TimeSpan.FromSeconds(5)),
            Is.True);
        Task<Exception?> second = Task.Run(
            ledger.TryRollback);
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False);

        allowRelease.Set();
        Exception?[] results =
            await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.All.Null);
            Assert.That(ledger.RollbackCompleted, Is.True);
            Assert.That(calls["texture:11"], Is.EqualTo(1));
            Assert.That(calls["material:20"], Is.EqualTo(1));
        });
    }

    [Test]
    public void RollbackStart_PermanentlyClosesLedgerToNewOwnership()
    {
        var ledger = new ModelUploadOwnershipLedger(
            materialCapacity: 1,
            pendingTextureCapacity: 1,
            _ => { },
            _ => { });

        Assert.That(ledger.TryRollback(), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                () => ledger.PendingTextures.Add(
                    new TextureHandle(10, 1)),
                Throws.InvalidOperationException);
            Assert.That(
                () => ledger.CommitPendingTexturesTo(
                    new MaterialHandle(20, 1)),
                Throws.InvalidOperationException);
        });
    }
}

using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSceneTransitionCoordinatorTests
{
    [Test]
    public void DeferredRequest_DoesNotPrepareUntilLoadingFrameReleased()
    {
        int prepareCount = 0;
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, _, _) =>
            {
                prepareCount++;
                return Task.CompletedTask;
            },
            _ => { });

        long generation = coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: true);

        Assert.That(prepareCount, Is.Zero);
        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(
                SampleSceneTransitionPhase.WaitingForLoadingFrame));

        coordinator.ReleaseLoadingFrame(generation);
        coordinator.Advance();

        Assert.That(prepareCount, Is.EqualTo(1));
        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(SampleSceneTransitionPhase.Completed));
    }

    [Test]
    public void NewestRequest_IsTheOnlyGenerationThatCommits()
    {
        var preparations = new Dictionary<
            SampleSceneKind,
            TaskCompletionSource>();
        var committed = new List<SampleSceneKind>();
        using var coordinator = new SampleSceneTransitionCoordinator(
            (kind, _, _) =>
            {
                var completion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                preparations.Add(kind, completion);
                return completion.Task;
            },
            committed.Add);

        coordinator.Request(
            SampleSceneKind.SponzaPlaza,
            waitForLoadingFrame: false);
        coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: false);
        preparations[SampleSceneKind.SponzaPlaza].SetResult();
        coordinator.Advance();

        Assert.That(committed, Is.Empty);

        preparations[SampleSceneKind.Bistro].SetResult();
        coordinator.Advance();

        Assert.That(committed,
            Is.EqualTo(new[] { SampleSceneKind.Bistro }));
    }

    [Test]
    public void PreparationFailure_DoesNotInvokeCommit()
    {
        bool committed = false;
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, _, _) => Task.FromException(
                new InvalidDataException("bad package")),
            _ => committed = true);

        coordinator.Request(
            SampleSceneKind.SponzaPlaza,
            waitForLoadingFrame: false);
        coordinator.Advance();

        Assert.That(committed, Is.False);
        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(SampleSceneTransitionPhase.Failed));
        Assert.That(coordinator.Snapshot.Failure,
            Is.TypeOf<InvalidDataException>());
    }

    [Test]
    public void HostFailure_TerminatesMatchingDeferredGeneration()
    {
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, _, _) => Task.CompletedTask,
            _ => Assert.Fail("A host-failed transition must not commit."));
        long generation = coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: true);
        var failure = new InvalidOperationException("release failed");

        bool accepted = coordinator.Fail(
            generation,
            failure,
            "previous scene release failed");

        Assert.That(accepted, Is.True);
        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(SampleSceneTransitionPhase.Failed));
        Assert.That(coordinator.Snapshot.Failure, Is.SameAs(failure));
    }

    [Test]
    public void HeartbeatsPreserveProgressAndPreventNoActivityFailure()
    {
        long now = 0;
        IContentLoadProgressSink? sink = null;
        var preparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, progress, _) =>
            {
                sink = progress;
                return preparation.Task;
            },
            _ => Assert.Fail("An incomplete transition must not commit."),
            () => now,
            static (started, ended) =>
                TimeSpan.FromTicks(ended - started));
        coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: false);
        double initialProgress = coordinator.Snapshot.Progress;

        now += TimeSpan.FromSeconds(29).Ticks;
        sink!.Report(new ContentLoadProgressEvent(
            "BistroExterior.fbx",
            ContentLoadPriority.Critical,
            ContentLoadStage.Preparing,
            Message: "source import is still active")
        {
            IsHeartbeat = true
        });
        coordinator.Advance();

        Assert.Multiple(() =>
        {
            Assert.That(coordinator.IsActive, Is.True);
            Assert.That(coordinator.Snapshot.Progress,
                Is.EqualTo(initialProgress));
            Assert.That(coordinator.Snapshot.Detail,
                Does.Contain("no progress"));
        });

        now += TimeSpan.FromSeconds(31).Ticks;
        coordinator.Advance();

        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(SampleSceneTransitionPhase.Failed));
        Assert.That(coordinator.Snapshot.Detail,
            Does.Contain("no observable activity"));
    }

    [Test]
    public void HostActivityPreventsNoActivityFailureWithoutFakingProgress()
    {
        long now = 0;
        var preparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, _, _) => preparation.Task,
            _ => Assert.Fail("An incomplete transition must not commit."),
            () => now,
            static (started, ended) =>
                TimeSpan.FromTicks(ended - started));
        long generation = coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: false);
        double initialProgress = coordinator.Snapshot.Progress;

        now += TimeSpan.FromSeconds(29).Ticks;
        coordinator.ObserveHostActivity(generation);
        now += TimeSpan.FromSeconds(2).Ticks;
        coordinator.Advance();

        Assert.Multiple(() =>
        {
            Assert.That(coordinator.IsActive, Is.True);
            Assert.That(coordinator.Snapshot.Progress,
                Is.EqualTo(initialProgress));
        });
    }

    [Test]
    public void AbsoluteWatchdogFailsEvenWhenHeartbeatsContinue()
    {
        long now = 0;
        IContentLoadProgressSink? sink = null;
        var preparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new SampleSceneTransitionCoordinator(
            (_, progress, _) =>
            {
                sink = progress;
                return preparation.Task;
            },
            _ => Assert.Fail("An incomplete transition must not commit."),
            () => now,
            static (started, ended) =>
                TimeSpan.FromTicks(ended - started));
        coordinator.Request(
            SampleSceneKind.Bistro,
            waitForLoadingFrame: false);

        now = SampleSceneTransitionCoordinator.AbsoluteFailure.Ticks;
        sink!.Report(new ContentLoadProgressEvent(
            "BistroExterior.fbx",
            ContentLoadPriority.Critical,
            ContentLoadStage.Preparing)
        {
            IsHeartbeat = true
        });
        coordinator.Advance();

        Assert.That(coordinator.Snapshot.Phase,
            Is.EqualTo(SampleSceneTransitionPhase.Failed));
        Assert.That(coordinator.Snapshot.Detail,
            Does.Contain("absolute"));
    }

    [TestCase(104_857_600UL, 1_073_741_824UL, 104_857_600UL, true)]
    [TestCase(629_145_600UL, 1_073_741_824UL, 104_857_600UL, false)]
    public void MemoryAdmission_UsesEightyPercentCeiling(
        ulong usage,
        ulong budget,
        ulong target,
        bool expected)
    {
        SampleSceneTransitionMemoryDecision decision =
            SampleSceneTransitionMemoryPolicy.Evaluate(
                usage,
                budget,
                target);

        Assert.That(decision.KeepCurrentScene, Is.EqualTo(expected));
        Assert.That(decision.AdmissionCeilingBytes,
            Is.EqualTo((ulong)Math.Floor(budget * 0.80)));
    }

    [Test]
    public void BistroInitialAdmission_CountsOnlyCriticalExteriorTier()
    {
        ulong full = HelloGame.EstimateSceneResidencyBytes(
            SampleSceneKind.Bistro);
        ulong initial = HelloGame.EstimateTransitionAdmissionBytes(
            SampleSceneKind.Bistro,
            SampleAssetManifest.Bistro);

        Assert.Multiple(() =>
        {
            Assert.That(initial, Is.GreaterThan(0));
            Assert.That(initial, Is.LessThan(full));
        });
    }

    [TestCase(SampleSceneKind.GlobalIlluminationTest, false, 1_000_000L, true)]
    [TestCase(SampleSceneKind.SponzaPlaza, false, 5_000_001L, false)]
    [TestCase(SampleSceneKind.Bistro, false, 5_000_000L, true)]
    [TestCase(SampleSceneKind.Bistro, false, 5_000_001L, false)]
    [TestCase(SampleSceneKind.Bistro, true, 1_000_001L, false)]
    public void LatencyPolicy_AppliesWarmAndColdSceneTargets(
        SampleSceneKind target,
        bool resident,
        long elapsedMicroseconds,
        bool expected)
    {
        SampleSceneTransitionLatencyEvaluation evaluation =
            SampleSceneTransitionLatencyPolicy.Evaluate(
                target,
                resident,
                elapsedMicroseconds);

        Assert.That(evaluation.MeetsTarget, Is.EqualTo(expected));
    }

    [Test]
    public void LatencyPolicy_TracksBistroFullResidencySeparately()
    {
        Assert.That(
            SampleSceneTransitionLatencyPolicy
                .ColdBistroFullResidencyTargetMicroseconds,
            Is.EqualTo(15_000_000L));
    }

    [Test]
    public void ResidencyCache_PromotesFirstViewWithoutReloadingExterior()
    {
        int loadCount = 0;
        var cache = new SampleSceneResidencyCache(
            asset => new Model { Name = $"{++loadCount}:{asset.Path}" },
            _ => { });
        var exterior = new SampleAssetReference(
            "exterior.fbx",
            ModelImportBackend.Assimp,
            LoadTier: SampleAssetLoadTier.Critical);
        var interior = new SampleAssetReference(
            "interior.fbx",
            ModelImportBackend.Assimp,
            LoadTier: SampleAssetLoadTier.Deferred);

        cache.Capture(
            SampleSceneKind.Bistro,
            new[] { exterior },
            2UL * 1024UL * 1024UL * 1024UL,
            SampleSceneResidencyState.FirstViewReady);

        Assert.That(loadCount, Is.EqualTo(1));
        Assert.That(
            cache.GetState(SampleSceneKind.Bistro),
            Is.EqualTo(SampleSceneResidencyState.FirstViewReady));

        cache.Capture(
            SampleSceneKind.Bistro,
            new[] { interior },
            3UL * 1024UL * 1024UL * 1024UL,
            SampleSceneResidencyState.FullyResident);

        Assert.That(loadCount, Is.EqualTo(2));
        Assert.That(
            cache.GetState(SampleSceneKind.Bistro),
            Is.EqualTo(SampleSceneResidencyState.FullyResident));
    }

    [Test]
    public void ResidencyCache_ReleasesActiveModelInBoundedSteps()
    {
        var model = new Model { Name = "stepped-release" };
        model.Add(new RenderObject());
        model.Add(new RenderObject());
        model.Add(new RenderObject());
        int unloadCount = 0;
        var cache = new SampleSceneResidencyCache(
            _ => model,
            released =>
            {
                unloadCount++;
                released.Dispose();
            });
        var asset = new SampleAssetReference(
            "stepped.glb",
            ModelImportBackend.SharpGltf);
        cache.Capture(
            SampleSceneKind.SponzaPlaza,
            CreateManifest(asset),
            100UL * 1024UL * 1024UL);
        cache.MarkActive(SampleSceneKind.SponzaPlaza);

        bool firstStepCompleted = cache.ReleaseActiveAssetsStep(2);

        Assert.Multiple(() =>
        {
            Assert.That(firstStepCompleted, Is.False);
            Assert.That(model.RenderObjects, Has.Count.EqualTo(1));
            Assert.That(unloadCount, Is.Zero);
            Assert.That(cache.Contains(SampleSceneKind.SponzaPlaza), Is.True);
        });

        bool secondStepCompleted = cache.ReleaseActiveAssetsStep(2);

        Assert.Multiple(() =>
        {
            Assert.That(secondStepCompleted, Is.True);
            Assert.That(model.RenderObjects, Is.Empty);
            Assert.That(unloadCount, Is.EqualTo(1));
            Assert.That(cache.Contains(SampleSceneKind.SponzaPlaza), Is.False);
        });
    }

    [Test]
    public void ResidencyCache_SharedAssetIsUnloadedOnlyAfterLastGroupEvicts()
    {
        var sharedModel = new Model { Name = "shared" };
        int loadCount = 0;
        int unloadCount = 0;
        var cache = new SampleSceneResidencyCache(
            _ =>
            {
                loadCount++;
                return sharedModel;
            },
            model =>
            {
                Assert.That(model, Is.SameAs(sharedModel));
                unloadCount++;
            });
        var asset = new SampleAssetReference(
            "shared.glb",
            ModelImportBackend.SharpGltf);
        SampleAssetManifest first = CreateManifest(asset);
        SampleAssetManifest second = CreateManifest(asset);
        cache.Capture(
            SampleSceneKind.SponzaPlaza,
            first,
            100UL * 1024UL * 1024UL);
        cache.Capture(
            SampleSceneKind.Bistro,
            second,
            100UL * 1024UL * 1024UL);
        cache.MarkActive(SampleSceneKind.GlobalIlluminationTest);

        IReadOnlyList<SampleSceneKind> evicted = cache.Trim(
            1024UL * 1024UL * 1024UL,
            750UL * 1024UL * 1024UL);

        Assert.That(evicted, Does.Contain(SampleSceneKind.SponzaPlaza));
        Assert.That(evicted, Does.Contain(SampleSceneKind.Bistro));
        Assert.That(loadCount, Is.EqualTo(1));
        Assert.That(unloadCount, Is.EqualTo(1));
    }

    [Test]
    public void ResidencyCache_CaptureFailureRollsBackEarlierAssets()
    {
        var firstModel = new Model { Name = "first" };
        int loadCount = 0;
        var unloaded = new List<Model>();
        var cache = new SampleSceneResidencyCache(
            _ => ++loadCount == 1
                ? firstModel
                : throw new InvalidDataException("second asset failed"),
            unloaded.Add);
        var first = new SampleAssetReference(
            "first.glb",
            ModelImportBackend.SharpGltf);
        var second = new SampleAssetReference(
            "second.glb",
            ModelImportBackend.SharpGltf);
        var manifest = new SampleAssetManifest(
            first,
            new[] { second },
            Array.Empty<SampleAssetReference>(),
            1,
            Vector3.Zero,
            0,
            Color.Black,
            EnableImportedModelLights: false);

        Assert.That(
            () => cache.Capture(
                SampleSceneKind.SponzaPlaza,
                manifest,
                128UL * 1024UL * 1024UL),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(cache.Contains(SampleSceneKind.SponzaPlaza), Is.False);
        Assert.That(unloaded, Is.EqualTo(new[] { firstModel }));
    }

    [Test]
    public void ResidencyCache_FailedUnloadRemainsRetryable()
    {
        var model = new Model { Name = "retryable" };
        int unloadAttempts = 0;
        var cache = new SampleSceneResidencyCache(
            _ => model,
            _ =>
            {
                if (++unloadAttempts == 1)
                    throw new InvalidOperationException("temporary release failure");
            });
        var asset = new SampleAssetReference(
            "retryable.glb",
            ModelImportBackend.SharpGltf);
        cache.Capture(
            SampleSceneKind.SponzaPlaza,
            CreateManifest(asset),
            100UL * 1024UL * 1024UL);
        cache.MarkActive(SampleSceneKind.GlobalIlluminationTest);

        Assert.That(
            () => cache.Trim(
                1024UL * 1024UL * 1024UL,
                750UL * 1024UL * 1024UL),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(cache.Contains(SampleSceneKind.SponzaPlaza), Is.True);

        IReadOnlyList<SampleSceneKind> evicted = cache.Trim(
            1024UL * 1024UL * 1024UL,
            750UL * 1024UL * 1024UL);

        Assert.That(evicted, Is.EqualTo(new[]
        {
            SampleSceneKind.SponzaPlaza
        }));
        Assert.That(unloadAttempts, Is.EqualTo(2));
        Assert.That(cache.Contains(SampleSceneKind.SponzaPlaza), Is.False);
    }

    [Test]
    public void ResidencyCache_OverlapProtectedSceneSurvivesTrim()
    {
        int unloadCount = 0;
        var cache = new SampleSceneResidencyCache(
            asset => new Model { Name = asset.Path },
            _ => unloadCount++);
        var bistro = new SampleAssetReference(
            "bistro.fbx",
            ModelImportBackend.Assimp);
        cache.Capture(
            SampleSceneKind.Bistro,
            CreateManifest(bistro),
            3UL * 1024UL * 1024UL * 1024UL);
        cache.MarkActive(SampleSceneKind.GlobalIlluminationTest);

        IReadOnlyList<SampleSceneKind> evicted = cache.Trim(
            6UL * 1024UL * 1024UL * 1024UL,
            5UL * 1024UL * 1024UL * 1024UL,
            protectedKind: SampleSceneKind.Bistro);

        Assert.Multiple(() =>
        {
            Assert.That(evicted, Is.Empty);
            Assert.That(cache.Contains(SampleSceneKind.Bistro), Is.True);
            Assert.That(unloadCount, Is.Zero);
        });
    }

    private static SampleAssetManifest CreateManifest(
        SampleAssetReference asset) => new(
        asset,
        Array.Empty<SampleAssetReference>(),
        Array.Empty<SampleAssetReference>(),
        1,
        Vector3.Zero,
        0,
        Color.Black,
        EnableImportedModelLights: false);
}

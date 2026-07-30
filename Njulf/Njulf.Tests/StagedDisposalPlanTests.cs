using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class StagedDisposalPlanTests
{
    [TestCase("model-upload")]
    [TestCase("material")]
    [TestCase("mesh")]
    [TestCase("texture-pending-retirements")]
    [TestCase("deleter")]
    [TestCase("texture")]
    [TestCase("bindless")]
    [TestCase("buffers")]
    public void FailureAtEveryDependencyStage_RetryIsExactOnce(
        string failedStage)
    {
        var calls = new Dictionary<string, int>();
        var failedOnce = new HashSet<string>();

        Action Stage(string name) => () =>
        {
            calls.TryGetValue(name, out int count);
            calls[name] = count + 1;
            if (name == failedStage &&
                failedOnce.Add(name))
            {
                throw new InvalidOperationException(
                    $"Injected {name}.");
            }
        };

        var plan = new StagedDisposalPlan(
            new StagedDisposalStep[]
            {
                new(
                    "model-upload",
                    Stage("model-upload")),
                new(
                    "material",
                    Stage("material"),
                    "model-upload"),
                new(
                    "mesh",
                    Stage("mesh"),
                    "material"),
                new(
                    "texture-pending-retirements",
                    Stage("texture-pending-retirements"),
                    "mesh"),
                new(
                    "deleter",
                    Stage("deleter"),
                    "texture-pending-retirements"),
                new(
                    "texture",
                    Stage("texture"),
                    "deleter"),
                new(
                    "bindless",
                    Stage("bindless"),
                    "texture"),
                new(
                    "buffers",
                    Stage("buffers"),
                    "bindless")
            });

        Exception? first = plan.TryDrain();
        Exception? retry = plan.TryDrain();
        int callCountAfterCompletion =
            calls.Values.Sum();
        Exception? completed = plan.TryDrain();

        Assert.Multiple(() =>
        {
            Assert.That(
                first,
                Is.TypeOf<AggregateException>());
            Assert.That(retry, Is.Null);
            Assert.That(completed, Is.Null);
            Assert.That(plan.IsComplete, Is.True);
            Assert.That(plan.PendingCount, Is.Zero);
            Assert.That(
                calls[failedStage],
                Is.EqualTo(2));
            Assert.That(
                calls.Where(pair =>
                        pair.Key != failedStage)
                    .Select(pair => pair.Value),
                Is.All.EqualTo(1));
            Assert.That(
                calls.Values.Sum(),
                Is.EqualTo(
                    callCountAfterCompletion));
        });
    }

    [Test]
    public void IndependentFailures_AreAggregatedWhileDependentsStayGated()
    {
        var attempted = new List<string>();
        bool failMaterial = true;
        bool failMesh = true;
        var plan = new StagedDisposalPlan(
            new StagedDisposalStep[]
            {
                new(
                    "material",
                    () =>
                    {
                        attempted.Add("material");
                        if (failMaterial)
                        {
                            throw new InvalidOperationException(
                                "material");
                        }
                    }),
                new(
                    "mesh",
                    () =>
                    {
                        attempted.Add("mesh");
                        if (failMesh)
                        {
                            throw new InvalidOperationException(
                                "mesh");
                        }
                    }),
                new(
                    "texture",
                    () => attempted.Add("texture"),
                    "material"),
                new(
                    "bindless",
                    () => attempted.Add("bindless"),
                    "material",
                    "mesh",
                    "texture")
            });

        AggregateException first =
            (AggregateException)plan.TryDrain()!;

        Assert.Multiple(() =>
        {
            Assert.That(
                first.InnerExceptions,
                Has.Count.EqualTo(2));
            Assert.That(
                attempted,
                Is.EqualTo(
                    new[] { "material", "mesh" }));
            Assert.That(plan.PendingCount, Is.EqualTo(4));
        });

        failMaterial = false;
        failMesh = false;
        Assert.That(plan.TryDrain(), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(plan.IsComplete, Is.True);
            Assert.That(
                attempted,
                Is.EqualTo(
                    new[]
                    {
                        "material",
                        "mesh",
                        "material",
                        "mesh",
                        "texture",
                        "bindless"
                    }));
        });
    }

    [Test]
    public async Task ConcurrentDrain_WaitsAndNeverRepeatsCompletedStages()
    {
        using var entered =
            new ManualResetEventSlim(false);
        using var allow =
            new ManualResetEventSlim(false);
        int firstCalls = 0;
        int secondCalls = 0;
        var plan = new StagedDisposalPlan(
            new StagedDisposalStep[]
            {
                new(
                    "first",
                    () =>
                    {
                        Interlocked.Increment(
                            ref firstCalls);
                        entered.Set();
                        if (!allow.Wait(
                                TimeSpan.FromSeconds(5)))
                        {
                            throw new TimeoutException(
                                "Timed out waiting for disposal test.");
                        }
                    }),
                new(
                    "second",
                    () => Interlocked.Increment(
                        ref secondCalls),
                    "first")
            });

        Task<Exception?> first =
            Task.Run(plan.TryDrain);
        Assert.That(
            entered.Wait(
                TimeSpan.FromSeconds(5)),
            Is.True);
        Task<Exception?> second =
            Task.Run(plan.TryDrain);
        await Task.Delay(50);
        Assert.That(second.IsCompleted, Is.False);

        allow.Set();
        Exception?[] results =
            await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(results, Is.All.Null);
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.EqualTo(1));
            Assert.That(plan.IsComplete, Is.True);
        });
    }

    [Test]
    public void Constructor_RejectsForwardOrMissingDependencies()
    {
        Assert.That(
            () => new StagedDisposalPlan(
                new StagedDisposalStep[]
                {
                    new(
                        "dependent",
                        static () => { },
                        "missing")
                }),
            Throws.ArgumentException);
    }
}

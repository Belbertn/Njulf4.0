using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialReleaseDurabilityTests
{
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void TerminalReleaseFailure_RetainsExactRemainingLedger(
        int failedCall)
    {
        var references = new FaultingTextureReferences();
        var manager = new MaterialManager(references);
        TextureHandle[] textures = CreateTextures();
        foreach (TextureHandle texture in textures)
            references.Acquire(texture);
        MaterialHandle material =
            manager.RegisterMaterialDefinition(
                CreateDefinition(textures),
                CreateCompilationContext());
        references.FailCalls.Add(failedCall);

        manager.ReleaseMaterial(material);

        Assert.Multiple(() =>
        {
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.EqualTo(4 - failedCall));
            Assert.That(
                manager.TextureReleaseFailureCount,
                Is.EqualTo(1));
            Assert.That(
                () => manager.GetMaterialDefinition(material),
                Throws.InvalidOperationException);
        });

        references.FailCalls.Clear();
        manager.FlushTextureReleases();
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
            Assert.That(
                references.SuccessfulReleaseCount,
                Is.EqualTo(3));
            Assert.That(
                textures.Select(references.Balance),
                Is.All.Zero);
        });
        manager.Dispose();
    }

    [Test]
    public void SharedAndTerminalFailures_AccumulateWithoutLossOrDoubleRelease()
    {
        var references = new FaultingTextureReferences();
        var manager = new MaterialManager(references);
        TextureHandle texture =
            new(120, 1);
        references.Acquire(texture, 2);
        MaterialDefinition definition =
            CreateDefinition([texture]);
        MaterialHandle first =
            manager.RegisterMaterialDefinition(
                definition,
                CreateCompilationContext());
        MaterialHandle second =
            manager.RegisterMaterialDefinition(
                definition,
                CreateCompilationContext());
        references.FailCalls.UnionWith([1, 2]);

        manager.ReleaseMaterial(first);
        manager.ReleaseMaterial(second);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.EqualTo(2));
            Assert.That(references.Balance(texture), Is.EqualTo(2));
        });

        references.FailCalls.Clear();
        manager.FlushTextureReleases();
        Assert.Multiple(() =>
        {
            Assert.That(references.Balance(texture), Is.Zero);
            Assert.That(
                references.SuccessfulReleaseCount,
                Is.EqualTo(2));
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
        });
        manager.Dispose();
    }

    [Test]
    public void DisposeFailure_IsRetryableOnSecondDispose()
    {
        var references = new FaultingTextureReferences();
        var manager = new MaterialManager(references);
        TextureHandle texture =
            new(121, 1);
        references.Acquire(texture);
        manager.RegisterMaterialDefinition(
            CreateDefinition([texture]),
            CreateCompilationContext());
        references.FailCalls.Add(1);

        Assert.That(
            () => manager.Dispose(),
            Throws.TypeOf<AggregateException>());
        Assert.That(
            manager.PendingTextureReleaseCount,
            Is.EqualTo(1));

        references.FailCalls.Clear();
        manager.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(references.Balance(texture), Is.Zero);
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
        });
    }

    [Test]
    public void DisposePreflightFailure_LeavesLiveOwnershipAndManagerStateUntouched()
    {
        var references = new FaultingTextureReferences();
        var manager = new MaterialManager(references);
        TextureHandle texture =
            new(124, 1);
        references.Acquire(texture);
        MaterialHandle material =
            manager.RegisterMaterialDefinition(
                CreateDefinition([texture]),
                CreateCompilationContext());
        manager.DisposalPreflightFaultInjector =
            static () => throw new InvalidOperationException(
                "injected disposal preflight");

        Assert.That(
            () => manager.Dispose(),
            Throws.InvalidOperationException.With.Message.Contains(
                "preflight"));
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetMaterialDefinition(material),
                Is.Not.Null);
            Assert.That(references.Balance(texture), Is.EqualTo(1));
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
        });

        manager.DisposalPreflightFaultInjector = null;
        manager.Dispose();
        Assert.That(references.Balance(texture), Is.Zero);
    }

    [Test]
    public async Task ConcurrentFlush_WaitsForActiveDrainAndNeverDuplicatesRelease()
    {
        var references = new FaultingTextureReferences
        {
            BlockFirstRelease = true
        };
        var manager = new MaterialManager(references);
        TextureHandle texture =
            new(122, 1);
        references.Acquire(texture);
        MaterialHandle material =
            manager.RegisterMaterialDefinition(
                CreateDefinition([texture]),
                CreateCompilationContext());

        Task release = Task.Run(
            () => manager.ReleaseMaterial(material));
        Assert.That(
            references.ReleaseEntered.Wait(
                TimeSpan.FromSeconds(5)),
            Is.True);
        Task flush = Task.Run(
            manager.FlushTextureReleases);
        await Task.Delay(50);
        Assert.That(flush.IsCompleted, Is.False);

        references.AllowRelease.Set();
        await Task.WhenAll(release, flush);
        Assert.Multiple(() =>
        {
            Assert.That(
                references.SuccessfulReleaseCount,
                Is.EqualTo(1));
            Assert.That(references.Balance(texture), Is.Zero);
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
        });
        manager.Dispose();
    }

    [Test]
    public void ReentrantFlush_FailsClosedWhileOuterDrainRetainsOrdering()
    {
        var references = new FaultingTextureReferences();
        var manager = new MaterialManager(references);
        references.ReentrantFlush =
            manager.FlushTextureReleases;
        TextureHandle texture =
            new(123, 1);
        references.Acquire(texture);
        MaterialHandle material =
            manager.RegisterMaterialDefinition(
                CreateDefinition([texture]),
                CreateCompilationContext());

        manager.ReleaseMaterial(material);

        Assert.Multiple(() =>
        {
            Assert.That(
                references.ReentrantFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(references.Balance(texture), Is.Zero);
            Assert.That(
                manager.PendingTextureReleaseCount,
                Is.Zero);
        });
        manager.Dispose();
    }

    [Test]
    public void DurableBatch_RetriesOnlyFailedMaterialPositions()
    {
        MaterialHandle[] handles =
        [
            new(1, 1),
            new(2, 1),
            new(3, 1)
        ];
        var calls = new Dictionary<MaterialHandle, int>();
        MaterialHandle failOnce = handles[1];
        var batch = new DurableMaterialReleaseBatch(
            handles,
            handle =>
            {
                calls.TryGetValue(handle, out int count);
                calls[handle] = count + 1;
                if (handle == failOnce && count == 0)
                {
                    throw new InvalidOperationException(
                        "injected");
                }
            });

        Assert.That(
            () => batch.ReleaseOutstanding(),
            Throws.TypeOf<AggregateException>());
        Assert.That(batch.PendingCount, Is.EqualTo(1));
        batch.ReleaseOutstanding();

        Assert.Multiple(() =>
        {
            Assert.That(batch.PendingCount, Is.Zero);
            Assert.That(calls[handles[0]], Is.EqualTo(1));
            Assert.That(calls[handles[1]], Is.EqualTo(2));
            Assert.That(calls[handles[2]], Is.EqualTo(1));
        });
    }

    private static TextureHandle[] CreateTextures() =>
    [
        new TextureHandle(110, 1),
        new TextureHandle(111, 1),
        new TextureHandle(112, 1)
    ];

    private static MaterialDefinition CreateDefinition(
        IReadOnlyList<TextureHandle> textures)
    {
        MaterialTextureBinding Binding(int index) =>
            index < textures.Count
                ? new MaterialTextureBinding
                {
                    Texture = textures[index]
                }
                : MaterialTextureBinding.Missing;
        return new MaterialDefinition
        {
            Name = "release-ledger",
            BaseColor = Binding(0),
            Normal = Binding(1),
            Emissive = Binding(2)
        };
    }

    private static MaterialCompilationContext
        CreateCompilationContext() =>
        new()
        {
            ResolveTexture = (binding, _) =>
                MaterialTextureTransportInput.Constant(
                    BindlessIndex.FirstDynamicTextureIndex +
                    binding.Texture.Index,
                    Vector4.One)
        };

    private sealed class FaultingTextureReferences :
        ITextureReferenceManager
    {
        private readonly object _lock = new();
        private readonly Dictionary<TextureHandle, int>
            _balances = new();
        private int _releaseCallCount;

        public HashSet<int> FailCalls { get; } = [];
        public bool BlockFirstRelease { get; init; }
        public ManualResetEventSlim ReleaseEntered { get; } =
            new(false);
        public ManualResetEventSlim AllowRelease { get; } =
            new(false);
        public Action? ReentrantFlush { get; set; }
        public Exception? ReentrantFailure { get; private set; }
        public int SuccessfulReleaseCount { get; private set; }

        public void Acquire(
            TextureHandle texture,
            int count = 1)
        {
            lock (_lock)
            {
                _balances.TryGetValue(
                    texture,
                    out int current);
                _balances[texture] =
                    checked(current + count);
            }
        }

        public int Balance(TextureHandle texture)
        {
            lock (_lock)
            {
                return _balances.TryGetValue(
                    texture,
                    out int count)
                    ? count
                    : 0;
            }
        }

        public void RetainTexture(TextureHandle handle) =>
            Acquire(handle);

        public void ReleaseTexture(
            TextureHandle handle,
            Fence retireFence = default)
        {
            int call = Interlocked.Increment(
                ref _releaseCallCount);
            if (BlockFirstRelease && call == 1)
            {
                ReleaseEntered.Set();
                if (!AllowRelease.Wait(
                        TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Timed out waiting for test release.");
                }
            }

            if (ReentrantFlush != null)
            {
                Action flush = ReentrantFlush;
                ReentrantFlush = null;
                try
                {
                    flush();
                }
                catch (Exception failure)
                {
                    ReentrantFailure = failure;
                }
            }

            lock (_lock)
            {
                if (FailCalls.Contains(call))
                {
                    throw new InvalidOperationException(
                        $"Injected release failure {call}.");
                }
                int final = Balance(handle) - 1;
                if (final < 0)
                {
                    throw new InvalidOperationException(
                        $"Texture {handle} was double-released.");
                }
                _balances[handle] = final;
                SuccessfulReleaseCount++;
            }
        }
    }
}

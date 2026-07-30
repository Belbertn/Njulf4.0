using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TextureManagerRuntimeCompletenessTests
{
    [Test]
    public void RuntimeSourceRead_RejectsOversizedNonWebPFileBeforeAllocation()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"oversized-runtime-texture-{Guid.NewGuid():N}.png");
        try
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(TextureManager.MaximumRuntimeEncodedTextureBytes + 1L);
            }

            var source = new ModelTextureSource
            {
                FilePath = path,
                CacheIdentity = "oversized-runtime-png",
                ContainerKind = TextureContainerKind.StandardImage
            };

            NotSupportedException exception = Assert.Throws<NotSupportedException>(
                () => TextureManager.ReadTextureSourceBytes(source, out _))!;
            Assert.That(
                exception.Message,
                Does.Contain(TextureManager.MaximumRuntimeEncodedTextureBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void RuntimeDecodeDimensions_EnforceThePredecodePixelBudget()
    {
        Assert.Multiple(() =>
        {
            Assert.DoesNotThrow(
                () => TextureManager.EnsureRuntimeTextureDecodeDimensions(
                    4096,
                    4096,
                    "boundary.png"));
            Assert.Throws<NotSupportedException>(
                () => TextureManager.EnsureRuntimeTextureDecodeDimensions(
                    4097,
                    4097,
                    "oversized.png"));
            Assert.Throws<InvalidDataException>(
                () => TextureManager.EnsureRuntimeTextureDecodeDimensions(
                    0,
                    128,
                    "invalid.png"));
        });
    }

    [Test]
    public void CacheIdentity_SeparatesContentSemanticMipPolicyAndSampler()
    {
        string identity = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "runtime-cache-texture.png");
        ulong firstHash = TextureManager.CalculateTextureSourceContentHash(
            [0x89, 0x50, 0x4e, 0x47, 0x01]);
        ulong changedHash = TextureManager.CalculateTextureSourceContentHash(
            [0x89, 0x50, 0x4e, 0x47, 0x02]);
        RuntimeTextureMipPolicy standard = RuntimeTextureMipPolicy.Default;
        RuntimeTextureMipPolicy alphaMask = RuntimeTextureMipPolicy.AlphaMask(0.5f);

        string firstImage = TextureManager.CreateTextureImageCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            sourceContentHash: firstHash,
            semantic: TextureSemantic.Color,
            mipPolicy: standard);
        string changedImage = TextureManager.CreateTextureImageCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            sourceContentHash: changedHash,
            semantic: TextureSemantic.Color,
            mipPolicy: standard);
        string normalImage = TextureManager.CreateTextureImageCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            sourceContentHash: firstHash,
            semantic: TextureSemantic.Normal,
            mipPolicy: standard);
        string maskedImage = TextureManager.CreateTextureImageCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            sourceContentHash: firstHash,
            semantic: TextureSemantic.Color,
            mipPolicy: alphaMask);

        var clampNearest = new TextureSamplerDescription(
            TextureWrapMode.ClampToEdge,
            TextureWrapMode.ClampToEdge,
            TextureFilterMode.Nearest,
            TextureFilterMode.Nearest,
            TextureMipFilterMode.Nearest,
            1f);
        string repeatDescriptor = TextureManager.CreateTextureCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            samplerDescription: TextureSamplerDescription.Default,
            sourceContentHash: firstHash,
            semantic: TextureSemantic.Color,
            mipPolicy: standard);
        string clampDescriptor = TextureManager.CreateTextureCacheKey(
            identity,
            generateMipmaps: true,
            srgb: true,
            samplerDescription: clampNearest,
            sourceContentHash: firstHash,
            semantic: TextureSemantic.Color,
            mipPolicy: standard);

        Assert.Multiple(() =>
        {
            Assert.That(firstImage, Is.Not.EqualTo(changedImage));
            Assert.That(firstHash, Is.Not.EqualTo(changedHash));
            Assert.That(firstImage, Is.Not.EqualTo(normalImage));
            Assert.That(firstImage, Is.Not.EqualTo(maskedImage));
            Assert.That(firstImage, Does.Contain($"content={firstHash:x16}"));
            Assert.That(firstImage, Does.Contain("semantic=Color"));
            Assert.That(maskedImage, Does.Contain("mipPolicy=coverage:0.5"));
            Assert.That(repeatDescriptor, Is.Not.EqualTo(clampDescriptor));
            Assert.That(repeatDescriptor, Does.StartWith(firstImage));
            Assert.That(clampDescriptor, Does.StartWith(firstImage));
            Assert.Throws<ArgumentException>(
                () => TextureManager.CalculateTextureSourceContentHash([]));
        });
    }

    [Test]
    public void RuntimeMipChain_FiltersSrgbColorInLinearSpace()
    {
        byte[] baseLevel =
        [
            0, 0, 0, 255,
            255, 255, 255, 255
        ];

        RuntimeRgbaMipChain chain = TextureManager.BuildRuntimeRgbaMipChain(
            baseLevel,
            width: 2,
            height: 1,
            srgb: true,
            RuntimeTextureMipPolicy.Default);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Levels, Has.Count.EqualTo(2));
            Assert.That(chain.Levels[0].Width, Is.EqualTo(2u));
            Assert.That(chain.Levels[1].Width, Is.EqualTo(1u));
            Assert.That(chain.ContiguousPixels, Has.Length.EqualTo(12));
            Assert.That(chain.Levels[1].Pixels[0], Is.InRange(187, 189));
            Assert.That(chain.Levels[1].Pixels[1], Is.InRange(187, 189));
            Assert.That(chain.Levels[1].Pixels[2], Is.InRange(187, 189));
            Assert.That(chain.Levels[1].Pixels[3], Is.EqualTo(255));
        });
    }

    [Test]
    public void RuntimeMaskedMipChain_PreservesCoverageWithinMipQuantization()
    {
        const uint size = 16;
        const float cutoff = 0.5f;
        var pixels = new byte[size * size * 4];
        for (uint y = 0; y < size; y++)
            for (uint x = 0; x < size; x++)
            {
                int offset = checked((int)((y * size + x) * 4u));
                pixels[offset] = checked((byte)(x * 255u / (size - 1u)));
                pixels[offset + 1] = checked((byte)(y * 255u / (size - 1u)));
                pixels[offset + 2] = 127;
                pixels[offset + 3] = checked((byte)((x * 29u + y * 47u + x * y * 3u) & 255u));
            }

        double sourceCoverage = AlphaCoverageMipGenerator.CalculateCoverage(pixels, cutoff);
        RuntimeRgbaMipChain preserved = TextureManager.BuildRuntimeRgbaMipChain(
            pixels,
            size,
            size,
            srgb: true,
            RuntimeTextureMipPolicy.AlphaMask(cutoff),
            sourceCoverage);
        RuntimeRgbaMipChain ordinary = TextureManager.BuildRuntimeRgbaMipChain(
            pixels,
            size,
            size,
            srgb: true,
            RuntimeTextureMipPolicy.Default);

        Assert.That(preserved.Levels, Has.Count.EqualTo(ordinary.Levels.Count));
        for (int levelIndex = 1; levelIndex < preserved.Levels.Count; levelIndex++)
        {
            RuntimeRgbaMipLevel level = preserved.Levels[levelIndex];
            if (level.Width < 4u || level.Height < 4u)
                continue;

            double preservedCoverage =
                AlphaCoverageMipGenerator.CalculateCoverage(level.Pixels, cutoff);
            double ordinaryCoverage =
                AlphaCoverageMipGenerator.CalculateCoverage(
                    ordinary.Levels[levelIndex].Pixels,
                    cutoff);
            double coverageTolerance = Math.Max(
                0.02,
                1.0 / (level.Width * level.Height));

            Assert.Multiple(() =>
            {
                Assert.That(
                    Math.Abs(preservedCoverage - sourceCoverage),
                    Is.LessThanOrEqualTo(coverageTolerance + 1e-9),
                    $"coverage at mip {levelIndex}");
                Assert.That(
                    Math.Abs(preservedCoverage - sourceCoverage),
                    Is.LessThanOrEqualTo(
                        Math.Abs(ordinaryCoverage - sourceCoverage) + 1e-9),
                    $"preservation must not be worse than an ordinary box mip at {levelIndex}");
            });
        }
    }

    [Test]
    public void RuntimeMipPolicy_PreservesLegalCutoffAboveOneAndRejectsInvalidValues()
    {
        RuntimeTextureMipPolicy aboveOne =
            RuntimeTextureMipPolicy.AlphaMask(1.25f).ValidateAndNormalize();
        System.Globalization.CultureInfo previousCulture =
            System.Globalization.CultureInfo.CurrentCulture;

        try
        {
            var sampler = TextureSamplerDescription.Default with
            {
                MaxAnisotropy = 1.25f
            };
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("en-US");
            string englishKey = TextureManager.CreateTextureCacheKey(
                "culture-stable",
                generateMipmaps: true,
                srgb: true,
                samplerDescription: sampler,
                sourceContentHash: 42,
                mipPolicy: aboveOne);
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("nb-NO");
            string norwegianKey = TextureManager.CreateTextureCacheKey(
                "culture-stable",
                generateMipmaps: true,
                srgb: true,
                samplerDescription: sampler,
                sourceContentHash: 42,
                mipPolicy: aboveOne);

            Assert.Multiple(() =>
            {
                Assert.That(aboveOne.AlphaCutoff, Is.EqualTo(1.25f));
                Assert.That(aboveOne.CacheKey, Is.EqualTo("coverage:1.25"));
                Assert.That(norwegianKey, Is.EqualTo(englishKey));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => RuntimeTextureMipPolicy.AlphaMask(-0.01f).ValidateAndNormalize());
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => RuntimeTextureMipPolicy.AlphaMask(float.NaN).ValidateAndNormalize());
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => RuntimeTextureMipPolicy.AlphaMask(float.PositiveInfinity).ValidateAndNormalize());
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Test]
    public void AlbedoRuntimeMipPolicy_UsesMaterialAlphaCutoffOnlyForCoverageMaterials()
    {
        RuntimeTextureMipPolicy opaque =
            ModelRenderUploadService.ResolveAlbedoRuntimeMipPolicy(
                new ModelMaterial
                {
                    AlphaMode = ModelAlphaMode.Opaque,
                    AlphaCutoff = 0.25f
                });
        RuntimeTextureMipPolicy masked =
            ModelRenderUploadService.ResolveAlbedoRuntimeMipPolicy(
                new ModelMaterial
                {
                    AlphaMode = ModelAlphaMode.Mask,
                    AlphaCutoff = 0.73f
                });
        Assert.Multiple(() =>
        {
            Assert.That(opaque, Is.EqualTo(RuntimeTextureMipPolicy.Default));
            Assert.That(masked.PreserveAlphaCoverage, Is.True);
            Assert.That(masked.AlphaCutoff, Is.EqualTo(0.73f));
            Assert.That(
                () => ModelRenderUploadService.ResolveAlbedoRuntimeMipPolicy(
                    new ModelMaterial
                    {
                        AlphaMode = ModelAlphaMode.Mask,
                        AlphaCutoff = -0.25f
                    }),
                Throws.InstanceOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void RuntimeReloadAndMaskedMaterialEntryPoints_AreExplicit()
    {
        Type manager = typeof(TextureManager);
        Assert.Multiple(() =>
        {
            Assert.That(
                manager.GetMethod(nameof(TextureManager.ReloadTextureContent)),
                Is.Not.Null);
            Assert.That(
                manager.GetEvent(nameof(TextureManager.TextureContentChanged)),
                Is.Not.Null);
            Assert.That(
                typeof(ModelRenderUploadService).GetMethod(
                    nameof(ModelRenderUploadService.RequiresAlphaCoveragePreservingMips)),
                Is.Not.Null);
        });
    }

    [Test]
    public void LogicalTextureRelease_ReachesRetirementExactlyOnce()
    {
        int references = 2;

        bool firstReleaseRetires =
            TextureManager.ReleaseLogicalTextureReference(ref references);
        bool secondReleaseRetires =
            TextureManager.ReleaseLogicalTextureReference(ref references);

        Assert.Multiple(() =>
        {
            Assert.That(firstReleaseRetires, Is.False);
            Assert.That(secondReleaseRetires, Is.True);
            Assert.That(references, Is.Zero);
            Assert.That(
                () => TextureManager.ReleaseLogicalTextureReference(ref references),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(
                    "cannot be released more than once"));
            Assert.That(references, Is.Zero);
        });
    }

    [Test]
    public void LogicalTextureRetain_FailsClosedAtInvalidAndOverflowCounts()
    {
        int references = 1;
        TextureManager.RetainLogicalTextureReference(ref references);
        int retired = 0;
        int exhausted = int.MaxValue;

        Assert.Multiple(() =>
        {
            Assert.That(references, Is.EqualTo(2));
            Assert.That(
                () => TextureManager.RetainLogicalTextureReference(ref retired),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("retired"));
            Assert.That(
                () => TextureManager.RetainLogicalTextureReference(ref exhausted),
                Throws.TypeOf<OverflowException>());
            Assert.That(exhausted, Is.EqualTo(int.MaxValue));
        });
    }

    [Test]
    public void DefaultTextureInitialization_ResumesAtEveryFailedSlot()
    {
        for (int failedStage = 0; failedStage < 3; failedStage++)
        {
            var initialization = new ResumableDefaultTextureInitialization();
            var attempts = new int[3];
            bool injectFailure = true;

            Action Stage(int stage) => () =>
            {
                attempts[stage]++;
                if (injectFailure && stage == failedStage)
                {
                    throw new InvalidOperationException(
                        $"injected default-texture failure at stage {stage}");
                }
            };

            Assert.Throws<InvalidOperationException>(
                () => initialization.Execute(Stage(0), Stage(1), Stage(2)));

            injectFailure = false;
            initialization.Execute(Stage(0), Stage(1), Stage(2));
            initialization.Execute(Stage(0), Stage(1), Stage(2));

            Assert.Multiple(() =>
            {
                Assert.That(initialization.IsComplete, Is.True);
                for (int stage = 0; stage < 3; stage++)
                {
                    int expectedAttempts = stage == failedStage ? 2 : 1;
                    Assert.That(
                        attempts[stage],
                        Is.EqualTo(expectedAttempts),
                        $"slot {stage} after failure at slot {failedStage}");
                }
            });
        }
    }

    [Test]
    public void DefaultTextureInitialization_PostPublicationFailureDoesNotReplayCommittedSlot()
    {
        var initialization = new ResumableDefaultTextureInitialization();
        var attempts = new int[3];
        bool failCheckpoint = true;

        Assert.Throws<InvalidOperationException>(
            () => initialization.Execute(
                () => attempts[0]++,
                () => attempts[1]++,
                () => attempts[2]++,
                checkpoint =>
                {
                    if (failCheckpoint &&
                        checkpoint == TexturePublicationCheckpoint.DefaultNormalPublished)
                    {
                        throw new InvalidOperationException(
                            "injected post-publication failure");
                    }
                }));

        failCheckpoint = false;
        initialization.Execute(
            () => attempts[0]++,
            () => attempts[1]++,
            () => attempts[2]++);

        Assert.Multiple(() =>
        {
            Assert.That(initialization.IsComplete, Is.True);
            Assert.That(attempts, Is.EqualTo(new[] { 1, 1, 1 }));
        });
    }

    [Test]
    public void DurableTextureRetirement_RetriesOnlyIncompleteStages()
    {
        var progress = new DurableTextureRetirementProgress();
        int bindlessAttempts = 0;
        int preparationAttempts = 0;
        int imageViewAttempts = 0;
        int imageAttempts = 0;

        Assert.Throws<InvalidOperationException>(() =>
        {
            progress.ExecuteBindless(() =>
            {
                bindlessAttempts++;
                throw new InvalidOperationException("injected bindless failure");
            });
        });
        Assert.That(
            () => progress.ExecuteResourcePreparation(
                () => preparationAttempts++),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.Contains("bindless descriptor"));

        progress.ExecuteBindless(() => bindlessAttempts++);
        progress.ExecuteResourcePreparation(() => preparationAttempts++);
        progress.ExecuteImageView(() => imageViewAttempts++);
        Assert.Throws<InvalidOperationException>(() =>
        {
            progress.ExecuteImage(() =>
            {
                imageAttempts++;
                throw new InvalidOperationException("injected image failure");
            });
        });
        progress.ExecuteImage(() => imageAttempts++);

        Assert.Multiple(() =>
        {
            Assert.That(progress.IsComplete, Is.True);
            Assert.That(bindlessAttempts, Is.EqualTo(2));
            Assert.That(preparationAttempts, Is.EqualTo(1));
            Assert.That(imageViewAttempts, Is.EqualTo(1));
            Assert.That(imageAttempts, Is.EqualTo(2));
        });
    }

    [Test]
    public void FenceRetirement_BindlessFailureKeepsViewAndImageAliveUntilRetry()
    {
        var queue = new Njulf.Rendering.Memory.DurableFenceDeletionQueue();
        var progress = new DurableTextureRetirementProgress();
        var fence = new Silk.NET.Vulkan.Fence(77);
        bool bindlessSucceeds = false;
        int bindlessAttempts = 0;
        int preparationAttempts = 0;
        int viewAttempts = 0;
        int imageAttempts = 0;

        queue.QueueDeletion(fence, () =>
            TextureManager.ExecuteDependentTextureRetirement(
                progress,
                () =>
                {
                    bindlessAttempts++;
                    if (!bindlessSucceeds)
                    {
                        throw new InvalidOperationException(
                            "injected fence-time descriptor failure");
                    }
                },
                () => preparationAttempts++,
                () => viewAttempts++,
                () => imageAttempts++));

        Assert.That(
            queue.Cleanup,
            Throws.TypeOf<InvalidOperationException>());
        Assert.Multiple(() =>
        {
            Assert.That(bindlessAttempts, Is.EqualTo(1));
            Assert.That(preparationAttempts, Is.Zero);
            Assert.That(viewAttempts, Is.Zero);
            Assert.That(imageAttempts, Is.Zero);
            Assert.That(queue.PendingActionCount, Is.EqualTo(1));
        });

        bindlessSucceeds = true;
        queue.Cleanup();

        Assert.Multiple(() =>
        {
            Assert.That(progress.IsComplete, Is.True);
            Assert.That(bindlessAttempts, Is.EqualTo(2));
            Assert.That(preparationAttempts, Is.EqualTo(1));
            Assert.That(viewAttempts, Is.EqualTo(1));
            Assert.That(imageAttempts, Is.EqualTo(1));
            Assert.That(queue.PendingActionCount, Is.Zero);
        });
    }

    [Test]
    public void TextureGeneration_RejectsExhaustionInsteadOfWrapping()
    {
        uint penultimateDetach = TextureManager.AdvanceTextureGenerationForDetach(
            uint.MaxValue - 1,
            out bool penultimateCanReuse);
        uint exhaustedDetach = TextureManager.AdvanceTextureGenerationForDetach(
            uint.MaxValue,
            out bool exhaustedCanReuse);

        Assert.Multiple(() =>
        {
            Assert.That(
                TextureManager.AdvanceTextureGeneration(41, textureIndex: 7),
                Is.EqualTo(42u));
            Assert.That(
                () => TextureManager.AdvanceTextureGeneration(
                    uint.MaxValue,
                    textureIndex: 7),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("exhausted"));
            Assert.That(penultimateDetach, Is.EqualTo(uint.MaxValue));
            Assert.That(penultimateCanReuse, Is.False);
            Assert.That(exhaustedDetach, Is.EqualTo(uint.MaxValue));
            Assert.That(exhaustedCanReuse, Is.False);
        });
    }

    [Test]
    public void TextureManagerDispose_FailsClosedWhileRetirementLedgerIsPending()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => TextureManager.EnsureTextureRetirementLedgerDrained(0),
                Throws.Nothing);
            Assert.That(
                () => TextureManager.EnsureTextureRetirementLedgerDrained(1),
                Throws.TypeOf<InvalidOperationException>()
                    .With.Message.Contains("retirement work remains incomplete"));
        });
    }

    [Test]
    public void TextureManagerLifecycle_RejectsOperationsAfterDisposalStartsButAllowsCleanupRetry()
    {
        var lifecycle = new TextureManagerLifecycleState();

        Assert.That(lifecycle.IsDisposed, Is.False);
        Assert.That(() => lifecycle.ThrowIfDisposed(), Throws.Nothing);

        lifecycle.BeginDispose();
        lifecycle.BeginDispose();

        Assert.Multiple(() =>
        {
            Assert.That(lifecycle.IsDisposed, Is.False);
            Assert.That(
                () => lifecycle.ThrowIfDisposed(),
                Throws.TypeOf<ObjectDisposedException>()
                    .With.Message.Contains("disposal has started"));
        });

        lifecycle.CompleteDispose();
        Assert.Multiple(() =>
        {
            Assert.That(lifecycle.IsDisposed, Is.True);
            Assert.That(
                () => lifecycle.ThrowIfDisposed(),
                Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void TextureManagerLifecycle_ShutdownGateRejectsOperationThatPassedEarlyPrecheck()
    {
        var lifecycle = new TextureManagerLifecycleState();
        var publicationGate = new object();
        using var precheckPassed = new ManualResetEventSlim();
        bool retirementPublished = false;
        Task operation;

        lock (publicationGate)
        {
            operation = Task.Run(
                () =>
                {
                    // Model a ReleaseTexture invocation that entered before
                    // renderer shutdown but had not yet acquired the
                    // retirement publication lock.
                    lifecycle.ThrowIfDisposed();
                    precheckPassed.Set();
                    lock (publicationGate)
                    {
                        lifecycle.ThrowIfDisposedUnderGate(publicationGate);
                        retirementPublished = true;
                    }
                });

            Assert.That(precheckPassed.Wait(TimeSpan.FromSeconds(5)), Is.True);
            lifecycle.BeginDisposeUnderGate(publicationGate);
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                () => operation.GetAwaiter().GetResult(),
                Throws.TypeOf<ObjectDisposedException>());
            Assert.That(retirementPublished, Is.False);
        });
    }

    [Test]
    public void DurableTextureDisposal_RetriesOnlyFailedDependencyStages()
    {
        var progress = new DurableTextureDisposalProgress();
        int viewAttempts = 0;
        int imageAttempts = 0;

        Assert.Throws<InvalidOperationException>(
            () => progress.ExecuteImage(() => imageAttempts++));
        Assert.Throws<InvalidOperationException>(() =>
        {
            progress.ExecuteView(() =>
            {
                viewAttempts++;
                throw new InvalidOperationException("injected view failure");
            });
        });
        Assert.That(progress.ViewCompleted, Is.False);

        progress.ExecuteView(() => viewAttempts++);
        Assert.Throws<InvalidOperationException>(() =>
        {
            progress.ExecuteImage(() =>
            {
                imageAttempts++;
                throw new InvalidOperationException("injected image failure");
            });
        });

        progress.ExecuteView(() => viewAttempts++);
        progress.ExecuteImage(() => imageAttempts++);
        progress.ExecuteImage(() => imageAttempts++);

        Assert.Multiple(() =>
        {
            Assert.That(progress.IsComplete, Is.True);
            Assert.That(viewAttempts, Is.EqualTo(2));
            Assert.That(imageAttempts, Is.EqualTo(2));
        });
    }

    [Test]
    public void DurableTextureDescriptorDisposal_RetriesWithoutDoubleFree()
    {
        var progress = new DurableTextureDescriptorDisposalProgress();
        int attempts = 0;

        Assert.Throws<InvalidOperationException>(() =>
        {
            progress.Execute(() =>
            {
                attempts++;
                throw new InvalidOperationException(
                    "injected bindless free failure");
            });
        });
        progress.Execute(() => attempts++);
        progress.Execute(() => attempts++);

        Assert.Multiple(() =>
        {
            Assert.That(progress.IsComplete, Is.True);
            Assert.That(attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public void TextureNotificationFanout_RetriesFailedAliasesWithoutRepeatingSuccess()
    {
        var dispatcher = new DurableTextureContentNotificationDispatcher();
        int stableDeliveries = 0;
        int retryableDeliveries = 0;
        bool acceptFanout = false;
        Action<TextureContentChangedEvent> stable = _ => stableDeliveries++;
        Action<TextureContentChangedEvent> retryable = _ =>
        {
            retryableDeliveries++;
            if (!acceptFanout)
                throw new InvalidOperationException("injected material fan-out failure");
        };
        TextureContentChangedEvent[] notifications =
        [
            new(new TextureHandle(3, 1), 2, 30),
            new(new TextureHandle(4, 1), 2, 30)
        ];

        Assert.That(
            () => dispatcher.Dispatch(
                notifications,
                stable + retryable),
            Throws.TypeOf<AggregateException>());
        Assert.Multiple(() =>
        {
            Assert.That(stableDeliveries, Is.EqualTo(2));
            Assert.That(retryableDeliveries, Is.EqualTo(2));
            Assert.That(dispatcher.PendingCount, Is.EqualTo(2));
            Assert.That(dispatcher.FailureCount, Is.EqualTo(2));
        });

        acceptFanout = true;
        int retried = dispatcher.RetryPending();

        Assert.Multiple(() =>
        {
            Assert.That(retried, Is.EqualTo(2));
            Assert.That(stableDeliveries, Is.EqualTo(2));
            Assert.That(retryableDeliveries, Is.EqualTo(4));
            Assert.That(dispatcher.PendingCount, Is.Zero);
            Assert.That(dispatcher.LastFailure, Is.Null);
            Assert.That(dispatcher.RetryPending(), Is.Zero);
        });
    }

    [Test]
    public void TextureNotificationFanout_ConcurrentAndReentrantRetryExecutesOnce()
    {
        var dispatcher = new DurableTextureContentNotificationDispatcher();
        bool accept = false;
        int successfulCalls = 0;
        int nestedRetryResult = -1;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action<TextureContentChangedEvent> handler = _ =>
        {
            if (!accept)
                throw new InvalidOperationException("injected fan-out failure");

            Interlocked.Increment(ref successfulCalls);
            nestedRetryResult = dispatcher.RetryPending();
            entered.Set();
            Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True);
        };
        var notification = new TextureContentChangedEvent(
            new TextureHandle(9, 1),
            2,
            90);
        Assert.Throws<InvalidOperationException>(
            () => dispatcher.Dispatch([notification], handler));

        accept = true;
        Task<int> first = Task.Run(dispatcher.RetryPending);
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Task<int> second = Task.Run(dispatcher.RetryPending);
        release.Set();
        Assert.That(
            Task.WaitAll([first, second], TimeSpan.FromSeconds(5)),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(first.Result + second.Result, Is.EqualTo(1));
            Assert.That(successfulCalls, Is.EqualTo(1));
            Assert.That(nestedRetryResult, Is.Zero);
            Assert.That(dispatcher.PendingCount, Is.Zero);
        });
    }

    [Test]
    public void TextureNotificationFanout_CoalescesFailuresAndLatestRevisionWins()
    {
        var dispatcher = new DurableTextureContentNotificationDispatcher();
        bool accept = false;
        var observedRevisions = new List<uint>();
        Action<TextureContentChangedEvent> handler = notification =>
        {
            observedRevisions.Add(notification.ContentRevision);
            if (!accept)
                throw new InvalidOperationException("injected fan-out failure");
        };
        var handle = new TextureHandle(11, 1);

        for (uint revision = 2; revision <= 4; revision++)
        {
            uint capturedRevision = revision;
            Assert.Throws<InvalidOperationException>(
                () => dispatcher.Dispatch(
                    [new TextureContentChangedEvent(
                        handle,
                        capturedRevision,
                        capturedRevision * 10)],
                    handler));
            Assert.That(dispatcher.PendingCount, Is.EqualTo(1));
        }

        accept = true;
        Assert.That(dispatcher.RetryPending(), Is.EqualTo(1));
        Assert.That(observedRevisions[^1], Is.EqualTo(4u));

        accept = false;
        Assert.Throws<InvalidOperationException>(
            () => dispatcher.Dispatch(
                [new TextureContentChangedEvent(handle, 5, 50)],
                handler));
        accept = true;
        dispatcher.Dispatch(
            [new TextureContentChangedEvent(handle, 6, 60)],
            handler);

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.PendingCount, Is.Zero);
            Assert.That(dispatcher.RetryPending(), Is.Zero);
            Assert.That(observedRevisions[^1], Is.EqualTo(6u));
        });
    }
}

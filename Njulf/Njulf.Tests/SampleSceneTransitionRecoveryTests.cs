using System.IO;
using Njulf.Rendering;
using NjulfHelloGame;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSceneTransitionRecoveryTests
{
    [Test]
    public void Success_DoesNotRunRecovery()
    {
        var calls = new List<string>();

        bool loaded = SampleSceneTransitionRecovery.Execute(
            () => calls.Add("requested"),
            () => calls.Add("cleanup-requested"),
            () => calls.Add("safe"),
            () => calls.Add("cleanup-safe"),
            _ => calls.Add("reported"));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.True);
            Assert.That(calls, Is.EqualTo(new[] { "requested" }));
        });
    }

    [Test]
    public void NestedDeviceOom_CleansThenLoadsSafeScene()
    {
        var calls = new List<string>();

        bool loaded = SampleSceneTransitionRecovery.Execute(
            () =>
            {
                calls.Add("requested");
                throw CreateWrappedDeviceOom();
            },
            () => calls.Add("cleanup-requested"),
            () => calls.Add("safe"),
            () => calls.Add("cleanup-safe"),
            _ => calls.Add("reported"));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "requested",
                    "reported",
                    "cleanup-requested",
                    "safe"
                }));
        });
    }

    [Test]
    public void NonMemoryFailure_RemainsFailFast()
    {
        bool cleanupCalled = false;
        var source = new InvalidDataException("corrupt asset");

        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() =>
                SampleSceneTransitionRecovery.Execute(
                    () => throw source,
                    () => cleanupCalled = true,
                    static () => { },
                    static () => { },
                    static _ => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.SameAs(source));
            Assert.That(cleanupCalled, Is.False);
        });
    }

    [Test]
    public void RecoveryCleanupFailure_AggregatesAndSkipsSafeLoad()
    {
        bool safeLoadCalled = false;

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                SampleSceneTransitionRecovery.Execute(
                    () => throw CreateWrappedDeviceOom(),
                    static () =>
                        throw new InvalidOperationException(
                            "cleanup failed"),
                    () => safeLoadCalled = true,
                    static () => { },
                    static _ => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(safeLoadCalled, Is.False);
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(
                failure.InnerExceptions[1].Message,
                Is.EqualTo("cleanup failed"));
        });
    }

    [Test]
    public void SafeSceneFailure_IsCleanedAndAggregated()
    {
        var calls = new List<string>();

        AggregateException failure =
            Assert.Throws<AggregateException>(() =>
                SampleSceneTransitionRecovery.Execute(
                    () => throw CreateWrappedDeviceOom(),
                    () => calls.Add("cleanup-requested"),
                    () =>
                    {
                        calls.Add("safe");
                        throw new InvalidOperationException(
                            "safe failed");
                    },
                    () => calls.Add("cleanup-safe"),
                    static _ => { }))!;

        Assert.Multiple(() =>
        {
            Assert.That(
                calls,
                Is.EqualTo(new[]
                {
                    "cleanup-requested",
                    "safe",
                    "cleanup-safe"
                }));
            Assert.That(failure.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(
                failure.InnerExceptions[1].Message,
                Is.EqualTo("safe failed"));
        });
    }

    [Test]
    public void ReportingFailure_DoesNotPreventRecovery()
    {
        bool safeLoaded = false;

        bool loaded = SampleSceneTransitionRecovery.Execute(
            () => throw CreateWrappedDeviceOom(),
            static () => { },
            () => safeLoaded = true,
            static () => { },
            static _ =>
                throw new InvalidOperationException(
                    "reporting failed"));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(safeLoaded, Is.True);
        });
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public void PresetPolicy_UsesRequestedLoadResult(
        bool loaded,
        bool expected)
    {
        Assert.That(
            SampleScenePresetPolicy.ShouldApply(loaded),
            Is.EqualTo(expected));
    }

    [Test]
    public void PresetPolicy_PreservesNoCallbackBehavior()
    {
        Assert.That(
            SampleScenePresetPolicy.ShouldApply(null),
            Is.True);
    }

    private static InvalidDataException CreateWrappedDeviceOom()
    {
        return new InvalidDataException(
            "scene load failed",
            new AggregateException(
                new InvalidOperationException(
                    "outer",
                    new VulkanException(
                        "injected allocation failure",
                        Result.ErrorOutOfDeviceMemory))));
    }
}

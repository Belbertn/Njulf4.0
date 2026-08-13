using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalShadowCacheStateTrackerTests
{
    private const uint AllFourCascades = 0b1111u;
    private const ulong SignatureA = 0x1a2b3c4dUL;
    private const ulong SignatureB = 0xaabbccddUL;

    [Test]
    public void EmptyButExplicitlyClearedLayersBecomeReusableAfterSubmission()
    {
        var tracker = new DirectionalShadowCacheStateTracker();

        Assert.That(
            tracker.IsDirty(AllFourCascades, SignatureA, resourceGeneration: 1u, resourcesDefined: false, forceRefresh: false),
            Is.True);

        tracker.BeginRefresh(AllFourCascades);
        // A refresh is valid even when every cascade records only reverse-Z
        // clear operations and emits no static meshlets.
        tracker.RecordRefresh(AllFourCascades, SignatureA, resourceGeneration: 1u);

        Assert.Multiple(() =>
        {
            Assert.That(tracker.ValidMask, Is.Zero);
            Assert.That(tracker.RecordedRefreshMask, Is.EqualTo(AllFourCascades));
            Assert.That(tracker.GetCurrentSubmissionCopyMask(AllFourCascades), Is.EqualTo(AllFourCascades));
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
            Assert.That(
                tracker.IsDirty(AllFourCascades, SignatureA, 1u, resourcesDefined: true, forceRefresh: false),
                Is.True);
            Assert.That(
                tracker.GetLayerState(2, AllFourCascades, refreshMask: 0u),
                Is.EqualTo(DirectionalShadowCacheLayerState.RefreshRecorded));
        });

        tracker.ConfirmRecordedRefreshSubmission();

        Assert.Multiple(() =>
        {
            Assert.That(tracker.ValidMask, Is.EqualTo(AllFourCascades));
            Assert.That(tracker.RecordedRefreshMask, Is.Zero);
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.EqualTo(AllFourCascades));
            Assert.That(
                tracker.IsDirty(AllFourCascades, SignatureA, 1u, resourcesDefined: true, forceRefresh: false),
                Is.False);
            Assert.That(
                tracker.GetLayerState(2, AllFourCascades, refreshMask: 0u),
                Is.EqualTo(DirectionalShadowCacheLayerState.Valid));
        });
    }

    [Test]
    public void SignatureOrResourceGenerationMismatchInvalidatesBeforeCopy()
    {
        var tracker = CreateValidTracker();

        bool signatureDirty = tracker.IsDirty(
            AllFourCascades,
            SignatureB,
            resourceGeneration: 1u,
            resourcesDefined: true,
            forceRefresh: false);

        Assert.Multiple(() =>
        {
            Assert.That(signatureDirty, Is.True);
            Assert.That(tracker.ValidMask, Is.Zero);
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
        });

        tracker.RecordRefresh(AllFourCascades, SignatureB, resourceGeneration: 1u);
        tracker.ConfirmRecordedRefreshSubmission();
        bool generationDirty = tracker.IsDirty(
            AllFourCascades,
            SignatureB,
            resourceGeneration: 2u,
            resourcesDefined: true,
            forceRefresh: false);
        Assert.Multiple(() =>
        {
            Assert.That(generationDirty, Is.True);
            Assert.That(tracker.ValidMask, Is.Zero);
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
        });
    }

    [Test]
    public void AbortedOrPartialRefreshCannotPromoteMissingLayers()
    {
        var tracker = CreateValidTracker();
        tracker.BeginRefresh(AllFourCascades);

        Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
        Assert.That(
            tracker.GetLayerState(0, AllFourCascades, refreshMask: 0u),
            Is.EqualTo(DirectionalShadowCacheLayerState.Invalid));

        tracker.RecordRefresh(0b0011u, SignatureA, resourceGeneration: 1u);
        Assert.Multiple(() =>
        {
            Assert.That(tracker.ValidMask, Is.Zero);
            Assert.That(tracker.RecordedRefreshMask, Is.EqualTo(0b0011u));
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
            Assert.That(tracker.GetCurrentSubmissionCopyMask(AllFourCascades), Is.EqualTo(0b0011u));
            Assert.That(
                tracker.GetLayerState(0, AllFourCascades, refreshMask: 0b0011u),
                Is.EqualTo(DirectionalShadowCacheLayerState.RefreshRecorded));
            Assert.That(
                tracker.GetLayerState(3, AllFourCascades, refreshMask: 0b0011u),
                Is.EqualTo(DirectionalShadowCacheLayerState.Invalid));
        });

        tracker.ConfirmRecordedRefreshSubmission();
        Assert.Multiple(() =>
        {
            Assert.That(tracker.ValidMask, Is.EqualTo(0b0011u));
            Assert.That(tracker.RecordedRefreshMask, Is.Zero);
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.EqualTo(0b0011u));
            Assert.That(
                tracker.GetLayerState(0, AllFourCascades, refreshMask: 0u),
                Is.EqualTo(DirectionalShadowCacheLayerState.Valid));
        });
    }

    [Test]
    public void ForceRefreshLeavesLayersInvalidUntilRefreshIsRecorded()
    {
        var tracker = CreateValidTracker();

        Assert.That(
            tracker.IsDirty(AllFourCascades, SignatureA, 1u, resourcesDefined: true, forceRefresh: true),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(tracker.ValidMask, Is.Zero);
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
        });
    }

    [Test]
    public void RecordedRefreshCannotBeReusedByAnotherFrameUntilSubmissionIsAccepted()
    {
        var tracker = new DirectionalShadowCacheStateTracker();
        tracker.BeginRefresh(AllFourCascades);
        tracker.RecordRefresh(AllFourCascades, SignatureA, resourceGeneration: 1u);

        Assert.Multiple(() =>
        {
            // The recording command buffer may copy its own freshly rendered
            // cache, while a later command buffer must refresh rather than
            // assume those commands were submitted successfully.
            Assert.That(tracker.GetCurrentSubmissionCopyMask(AllFourCascades), Is.EqualTo(AllFourCascades));
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.Zero);
            Assert.That(
                tracker.IsDirty(AllFourCascades, SignatureA, 1u, resourcesDefined: true, forceRefresh: false),
                Is.True);
        });

        tracker.BeginRefresh(AllFourCascades);
        Assert.That(tracker.RecordedRefreshMask, Is.Zero);
        Assert.That(tracker.GetCurrentSubmissionCopyMask(AllFourCascades), Is.Zero);
    }

    [Test]
    public void PerCascadeSignatureChangeInvalidatesOnlyAffectedLayer()
    {
        var tracker = new DirectionalShadowCacheStateTracker();
        ulong[] initial = [11UL, 22UL, 33UL, 44UL];
        tracker.BeginRefresh(AllFourCascades);
        tracker.RecordRefresh(AllFourCascades, initial, resourceGeneration: 7u);
        tracker.ConfirmRecordedRefreshSubmission();

        ulong[] changed = [11UL, 99UL, 33UL, 44UL];
        uint dirtyMask = tracker.GetDirtyMask(
            AllFourCascades,
            changed,
            resourceGeneration: 7u,
            resourcesDefined: true,
            forceRefresh: false);

        Assert.Multiple(() =>
        {
            Assert.That(dirtyMask, Is.EqualTo(0b0010u));
            Assert.That(tracker.ValidMask, Is.EqualTo(0b1101u));
            Assert.That(tracker.GetReusableMask(AllFourCascades), Is.EqualTo(0b1101u));
            Assert.That(tracker.GetSignature(0), Is.EqualTo(11UL));
            Assert.That(tracker.GetSignature(1), Is.Zero);
            Assert.That(tracker.GetSignature(2), Is.EqualTo(33UL));
        });
    }

    private static DirectionalShadowCacheStateTracker CreateValidTracker()
    {
        var tracker = new DirectionalShadowCacheStateTracker();
        tracker.BeginRefresh(AllFourCascades);
        tracker.RecordRefresh(AllFourCascades, SignatureA, resourceGeneration: 1u);
        tracker.ConfirmRecordedRefreshSubmission();
        return tracker;
    }
}

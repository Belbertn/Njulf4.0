using System.Collections.Generic;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalSdfManagerTests
{
    [Test]
    public void CalculateCascadeBrickBudgets_UsesWeightedSplitAndCapsCascadeZero()
    {
        int[] budgets = new int[4];

        GlobalSdfManager.CalculateCascadeBrickBudgets(100, 4, budgets);

        Assert.Multiple(() =>
        {
            Assert.That(budgets, Is.EqualTo(new[] { 40, 30, 20, 10 }));
            Assert.That(budgets[0], Is.LessThanOrEqualTo(50));
            Assert.That(budgets.Sum(), Is.EqualTo(100));
        });
    }

    [Test]
    public void CalculateCascadeBrickBudgets_GivesEveryCascadeWorkWhenBudgetAllows()
    {
        int[] budgets = new int[4];

        GlobalSdfManager.CalculateCascadeBrickBudgets(4, 4, budgets);

        Assert.Multiple(() =>
        {
            Assert.That(budgets, Is.EqualTo(new[] { 1, 1, 1, 1 }));
            Assert.That(budgets.Sum(), Is.EqualTo(4));
        });
    }

    [Test]
    public void CalculateCascadeBrickBudgets_RedistributesCascadeZeroExcessForPartialCascadeCounts()
    {
        int[] budgets = new int[4];

        GlobalSdfManager.CalculateCascadeBrickBudgets(7, 2, budgets);

        Assert.Multiple(() =>
        {
            Assert.That(budgets[0], Is.EqualTo(4));
            Assert.That(budgets[1], Is.EqualTo(3));
            Assert.That(budgets[0], Is.LessThanOrEqualTo(4));
            Assert.That(budgets.Sum(), Is.EqualTo(7));
        });
    }

    [Test]
    public void CalculateIdleRefreshBrickCount_UsesFullRemainingCascadeBudget()
    {
        int refreshCount = GlobalSdfManager.CalculateIdleRefreshBrickCount(512, 4096);

        Assert.That(refreshCount, Is.EqualTo(512));
    }

    [Test]
    public void CalculateIdleRefreshBrickCount_RespectsSmallRemainingBudgetAndTotalBricks()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalSdfManager.CalculateIdleRefreshBrickCount(7, 4096), Is.EqualTo(7));
            Assert.That(GlobalSdfManager.CalculateIdleRefreshBrickCount(512, 9), Is.EqualTo(9));
            Assert.That(GlobalSdfManager.CalculateIdleRefreshBrickCount(0, 4096), Is.Zero);
            Assert.That(GlobalSdfManager.CalculateIdleRefreshBrickCount(512, 0), Is.Zero);
        });
    }

    [Test]
    public void CascadeRuntime_SelectsIdleRefreshBricksNearestCameraFirst()
    {
        var cascade = CreateInitializedCleanCascade();
        var candidates = new List<GlobalSdfManager.IdleRefreshCandidate>();
        var selected = new List<int>();
        Vector3 cameraNearMinimumCorner = cascade.WorldMin + new Vector3(0.1f);

        int selectedCount = cascade.SelectNearestIdleRefreshBricks(
            cameraNearMinimumCorner,
            4,
            candidates,
            selected);

        Assert.Multiple(() =>
        {
            Assert.That(selectedCount, Is.EqualTo(4));
            Assert.That(selected, Is.EqualTo(new[] { 0, 1, 4, 16 }));
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.EqualTo(cascade.TotalBricks - 4));
        });
    }

    [Test]
    public void CascadeRuntime_ConsumesScrollPriorityDirtyWithoutDuplicateNormalDirtyWork()
    {
        var cascade = CreateInitializedCleanCascade();

        cascade.UpdateClipmap(new Vector3(8.0f, 0.0f, 0.0f), 32);
        int priorityStart = cascade.FindNextPriorityDirtyBrick();

        Assert.That(priorityStart, Is.GreaterThanOrEqualTo(0));
        while (priorityStart >= 0)
        {
            cascade.ConsumePriorityDirtyRun(priorityStart, cascade.TotalBricks);
            priorityStart = cascade.FindNextPriorityDirtyBrick();
        }

        Assert.That(cascade.FindNextDirtyBrick(), Is.EqualTo(-1));
    }

    [Test]
    public void CascadeRuntime_FindsScrollPriorityDirtyBeforeGenericDirtyRegion()
    {
        var cascade = CreateInitializedCleanCascade();

        cascade.UpdateClipmap(new Vector3(8.0f, 0.0f, 0.0f), 32);
        cascade.MarkWorldBoundsDirty(new BoundingBox(
            cascade.WorldMin,
            cascade.WorldMin + new Vector3(0.25f)));

        int priorityStart = cascade.FindNextPriorityDirtyBrick();
        int genericStartBeforePriorityDrain = cascade.FindNextDirtyBrick();

        Assert.Multiple(() =>
        {
            Assert.That(priorityStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(genericStartBeforePriorityDrain, Is.GreaterThanOrEqualTo(0));
        });

        cascade.ConsumePriorityDirtyRun(priorityStart, cascade.TotalBricks);

        Assert.That(cascade.FindNextDirtyBrick(), Is.GreaterThanOrEqualTo(0));
    }

    private static GlobalSdfManager.GlobalSdfCascadeRuntime CreateInitializedCleanCascade()
    {
        var cascade = new GlobalSdfManager.GlobalSdfCascadeRuntime(null!, 1.0f, 32);
        cascade.UpdateClipmap(Vector3.Zero, 32);
        DrainAllDirty(cascade);
        return cascade;
    }

    private static void DrainAllDirty(GlobalSdfManager.GlobalSdfCascadeRuntime cascade)
    {
        int start = cascade.FindNextDirtyBrick();
        while (start >= 0)
        {
            cascade.ConsumeDirtyRun(start, cascade.TotalBricks);
            start = cascade.FindNextDirtyBrick();
        }
    }
}

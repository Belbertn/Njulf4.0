using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalSdfManagerTests
{
    [Test]
    public void CalculateCascadeBrickBudgets_UsesWeightedFairShareWhenAllCascadesAreBacklogged()
    {
        int[] budgets = new int[4];
        int[] backlogs = [4096, 4096, 4096, 4096];

        GlobalSdfManager.CalculateCascadeBrickBudgets(100, backlogs, budgets);

        Assert.Multiple(() =>
        {
            Assert.That(budgets, Is.EqualTo(new[] { 40, 30, 20, 10 }));
            Assert.That(budgets.Sum(), Is.EqualTo(100));
        });
    }

    [Test]
    public void CalculateCascadeBrickBudgets_GivesSingleBackloggedCascadeFullBudget()
    {
        int[] budgets = new int[4];
        int[] backlogs = [4096, 0, 0, 0];

        GlobalSdfManager.CalculateCascadeBrickBudgets(100, backlogs, budgets);

        Assert.That(budgets, Is.EqualTo(new[] { 100, 0, 0, 0 }));
    }

    [Test]
    public void CalculateCascadeBrickBudgets_RedistributesFromSatisfiedCascadesToRemainingBacklog()
    {
        int[] budgets = new int[4];
        int[] backlogs = [5, 4096, 0, 0];

        GlobalSdfManager.CalculateCascadeBrickBudgets(100, backlogs, budgets);

        Assert.That(budgets, Is.EqualTo(new[] { 5, 95, 0, 0 }));
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
    public void SelectDirtyBrickJobs_RunsIdleRefreshWhenDirtyQueuesAreDrainedEvenIfCameraMoved()
    {
        var cascade = CreateInitializedCleanCascade();
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 4, cascade.WorldMin + new Vector3(100.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(jobs.Count, Is.EqualTo(4));
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.EqualTo(cascade.TotalBricks - 4));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(4));
        });
    }

    [Test]
    public void SelectDirtyBrickJobs_RunsIdleRefreshThroughSubMillimeterCameraJitter()
    {
        var cascade = CreateInitializedCleanCascade();
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 1, cascade.WorldMin + new Vector3(0.0f, 0.0f, 0.0f));
        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 1, cascade.WorldMin + new Vector3(0.00025f, 0.0f, 0.0f));
        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 1, cascade.WorldMin + new Vector3(0.0005f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(cascade.HasDirtyBricks, Is.False);
            Assert.That(cascade.HasPriorityDirtyBricks, Is.False);
            Assert.That(jobs.Count, Is.EqualTo(3));
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(3));
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.EqualTo(cascade.TotalBricks - 3));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(3));
        });
    }

    [Test]
    public void SelectDirtyBrickJobs_UsesRemainingBudgetForIdleRefreshAfterLastDirtyBrick()
    {
        var cascade = CreateInitializedCleanCascade();
        cascade.MarkWorldBoundsDirty(new BoundingBox(
            cascade.WorldMin,
            cascade.WorldMin + new Vector3(0.25f)));
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 4, cascade.WorldMin);

        Assert.Multiple(() =>
        {
            Assert.That(cascade.HasDirtyBricks, Is.False);
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(4));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(4));
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

    [Test]
    public void CascadeRuntime_MaintainsDirtyBrickCount()
    {
        var cascade = new GlobalSdfManager.GlobalSdfCascadeRuntime(null!, 1.0f, 32);
        cascade.UpdateClipmap(Vector3.Zero, 32);

        int start = cascade.FindNextDirtyBrick();
        int consumed = cascade.ConsumeDirtyRun(start, 7);

        Assert.Multiple(() =>
        {
            Assert.That(consumed, Is.EqualTo(7));
            Assert.That(cascade.DirtyBrickCount, Is.EqualTo(cascade.TotalBricks - 7));
            Assert.That(cascade.HasDirtyBricks, Is.True);
        });

        DrainAllDirty(cascade);

        Assert.Multiple(() =>
        {
            Assert.That(cascade.DirtyBrickCount, Is.Zero);
            Assert.That(cascade.HasDirtyBricks, Is.False);
        });
    }

    [Test]
    public void ApplyDdgiEvents_FastCameraMovementMarksNearCascadeDirty()
    {
        var cascade0 = CreateInitializedCleanCascade();
        var cascade1 = CreateInitializedCleanCascade();
        var cascades = new GlobalSdfManager.GlobalSdfCascadeRuntime?[]
        {
            cascade0,
            cascade1,
            null,
            null
        };
        var manager = (GlobalSdfManager)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSdfManager));
        FieldInfo cascadeField = typeof(GlobalSdfManager).GetField("_cascades", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlobalSdfManager cascade field was not found.");
        cascadeField.SetValue(manager, cascades);
        var layout = new DdgiFrameLayout(
            Array.Empty<Njulf.Core.Scene.GlobalIlluminationProbeVolume>(),
            Array.Empty<DdgiProbeVolumeRuntimeMetadata>(),
            Array.Empty<BoundingBox>(),
            Array.Empty<DdgiDirtyRegion>(),
            Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
            isDdgiActive: true,
            cameraRelativeEnabled: false,
            defaultVolumeIncluded: false,
            authoredVolumeCount: 0,
            cameraRelativeCascadeCount: 0,
            authoredProbeCount: 0,
            cameraRelativeProbeCount: 0,
            totalPhysicalProbeCount: 0,
            movementClass: DdgiCameraMovementClass.Normal,
            fastCameraMovement: true);
        MethodInfo applyDdgiEvents = typeof(GlobalSdfManager).GetMethod("ApplyDdgiEvents", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyDdgiEvents was not found.");

        applyDdgiEvents.Invoke(manager, new object?[] { layout });

        Assert.Multiple(() =>
        {
            Assert.That(cascade0.DirtyBrickCount, Is.EqualTo(cascade0.TotalBricks));
            Assert.That(cascade1.DirtyBrickCount, Is.Zero);
        });
    }

    private static GlobalSdfManager.GlobalSdfCascadeRuntime CreateInitializedCleanCascade()
    {
        var cascade = new GlobalSdfManager.GlobalSdfCascadeRuntime(null!, 1.0f, 32);
        cascade.UpdateClipmap(Vector3.Zero, 32);
        DrainAllDirty(cascade);
        return cascade;
    }

    private static GlobalSdfManager CreateUninitializedManagerForSchedulerTests()
    {
        var manager = (GlobalSdfManager)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSdfManager));
        SetPrivateField(manager, "_idleRefreshCandidateScratch", new List<GlobalSdfManager.IdleRefreshCandidate>());
        SetPrivateField(manager, "_idleRefreshBrickScratch", new List<int>());
        SetPrivateField(manager, "_resolution", 32);
        return manager;
    }

    private static void InvokeSelectDirtyBrickJobs(
        GlobalSdfManager manager,
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        List<GlobalSdfUpdateJob> jobs,
        int budget,
        Vector3 cameraPosition)
    {
        MethodInfo selectDirtyBrickJobs = typeof(GlobalSdfManager).GetMethod("SelectDirtyBrickJobs", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SelectDirtyBrickJobs was not found.");
        selectDirtyBrickJobs.Invoke(manager, new object?[]
        {
            0,
            cascade,
            jobs,
            budget,
            cameraPosition
        });
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was not found.");
        field.SetValue(target, value);
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

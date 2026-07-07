using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
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
    public void CalculateEffectiveBrickUpdateBudget_RaisesNonzeroBudgetWhileDirtyBacklogExists()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(128, 4096), Is.EqualTo(GlobalSdfManager.BacklogBrickUpdateBudgetFloor));
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(2048, 4096), Is.EqualTo(2048));
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(128, 4096, 1536), Is.EqualTo(1536));
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(2048, 4096, 1536), Is.EqualTo(2048));
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(128, 0), Is.EqualTo(128));
            Assert.That(GlobalSdfManager.CalculateEffectiveBrickUpdateBudget(0, 4096), Is.Zero);
        });
    }

    [Test]
    public void CalculateCascadeBrickBudgets_ReservesPriorityDirtyBacklogsBeforeFairShare()
    {
        int[] budgets = new int[4];
        int[] dirtyBacklogs = [1000, 1000, 0, 0];
        int[] priorityDirtyBacklogs = [600, 0, 0, 0];

        GlobalSdfManager.CalculateCascadeBrickBudgets(700, dirtyBacklogs, priorityDirtyBacklogs, budgets);

        Assert.Multiple(() =>
        {
            Assert.That(budgets[0], Is.GreaterThanOrEqualTo(600));
            Assert.That(budgets[1], Is.GreaterThan(0));
            Assert.That(budgets[2], Is.Zero);
            Assert.That(budgets[3], Is.Zero);
            Assert.That(budgets.Sum(), Is.EqualTo(700));
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
        MarkAllIdleRefreshPending(cascade);
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
    public void SelectDirtyBrickJobs_NoDirtyBricksReturnsNoJobsAfterIdlePendingDrains()
    {
        var cascade = CreateInitializedCleanCascade();
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 4, cascade.WorldMin + new Vector3(100.0f, 0.0f, 0.0f));

        Assert.Multiple(() =>
        {
            Assert.That(jobs.Count, Is.Zero);
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.Zero);
            Assert.That(manager.LastFrameBricksUpdated, Is.Zero);
            Assert.That(manager.LastFramePriorityBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameDirtyBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameIdleRefreshBricksUpdated, Is.Zero);
        });
    }

    [Test]
    public void SelectDirtyBrickJobs_RunsExplicitIdleRefreshThroughSubMillimeterCameraJitter()
    {
        var cascade = CreateInitializedCleanCascade();
        MarkAllIdleRefreshPending(cascade);
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
            Assert.That(manager.LastFramePriorityBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameDirtyBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameIdleRefreshBricksUpdated, Is.EqualTo(3));
        });
    }

    [Test]
    public void SelectDirtyBrickJobs_FullDirtyBacklogDrainsNearestCameraFirst()
    {
        var cascade = CreateInitializedCleanCascade();
        cascade.MarkAllDirty();
        Vector3 cameraPosition = cascade.WorldMin + new Vector3(31.9f, 31.9f, 31.9f);
        int[] expectedDirtyOrder = GetNearestDirtyBricks(cascade, cameraPosition, 4);
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 4, cameraPosition);

        Assert.Multiple(() =>
        {
            Assert.That(jobs.Select(job => job.BrickStartIndex), Is.EqualTo(expectedDirtyOrder));
            Assert.That(jobs.Select(job => job.BrickCount), Is.All.EqualTo(1));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(4));
            Assert.That(manager.LastFramePriorityBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameDirtyBricksUpdated, Is.EqualTo(4));
            Assert.That(cascade.DirtyBrickCount, Is.EqualTo(cascade.TotalBricks - 4));
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
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.Zero);
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(1));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(1));
            Assert.That(manager.LastFramePriorityBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameDirtyBricksUpdated, Is.EqualTo(1));
            Assert.That(manager.LastFrameIdleRefreshBricksUpdated, Is.Zero);
        });
    }

    [Test]
    public void SelectDirtyBrickJobs_ReportsPriorityDirtyAndIdleRefreshBricksSeparately()
    {
        var cascade = CreateInitializedCleanCascade();
        cascade.UpdateClipmap(new Vector3(10.0f, 0.0f, 0.0f), 32);
        int pendingBefore = cascade.IdleRefreshPendingBrickCount;
        Vector3 cameraPosition = cascade.WorldMin + new Vector3(0.1f);
        int[] expectedPriorityOrder = GetNearestPriorityDirtyBricks(cascade, cameraPosition, 4);
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, 4, cameraPosition);

        Assert.Multiple(() =>
        {
            Assert.That(jobs.Select(job => job.BrickStartIndex), Is.EqualTo(expectedPriorityOrder));
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(4));
            Assert.That(cascade.IdleRefreshPendingBrickCount, Is.EqualTo(pendingBefore - 4));
            Assert.That(manager.LastFrameBricksUpdated, Is.EqualTo(4));
            Assert.That(manager.LastFramePriorityBricksUpdated, Is.EqualTo(4));
            Assert.That(manager.LastFrameDirtyBricksUpdated, Is.Zero);
            Assert.That(manager.LastFrameIdleRefreshBricksUpdated, Is.Zero);
        });
    }

    [Test]
    public void CascadeRuntime_SelectNearestPriorityDirtyBricks_ConsumesNearestCameraBricksFirst()
    {
        var cascade = CreateInitializedCleanCascade();
        cascade.UpdateClipmap(new Vector3(10.0f, 0.0f, 0.0f), 32);
        Vector3 cameraPosition = cascade.WorldMin + new Vector3(0.1f);
        int[] expectedPriorityOrder = GetNearestPriorityDirtyBricks(cascade, cameraPosition, 3);
        var candidates = new List<GlobalSdfManager.IdleRefreshCandidate>();
        var selected = new List<int>();

        int selectedCount = cascade.SelectNearestPriorityDirtyBricks(
            cameraPosition,
            3,
            candidates,
            selected);

        Assert.Multiple(() =>
        {
            Assert.That(selectedCount, Is.EqualTo(3));
            Assert.That(selected, Is.EqualTo(expectedPriorityOrder));
            Assert.That(expectedPriorityOrder.All(index => !cascade.IsPhysicalBrickDirty(index)), Is.True);
            Assert.That(expectedPriorityOrder.All(index => !cascade.IsPhysicalBrickPriorityDirty(index)), Is.True);
        });
    }

    [Test]
    public void CascadeRuntime_ConsumesScrollPriorityDirtyWithoutDuplicateNormalDirtyWork()
    {
        var cascade = CreateInitializedCleanCascade();

        cascade.UpdateClipmap(new Vector3(10.0f, 0.0f, 0.0f), 32);
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
    public void CascadeRuntime_HysteresisSuppressesBoundaryOscillation()
    {
        var cascade = CreateInitializedCleanCascade();
        DdgiClipmapCell initialGridMin = cascade.LogicalGridMinCell;
        float brickWorldSize = cascade.VoxelSize * GlobalSdfManager.BrickSize;
        float positiveBoundary = (initialGridMin.X + cascade.BricksPerAxis / 2 + 1) * brickWorldSize;
        Vector3[] cameraPositions =
        [
            new Vector3(positiveBoundary - brickWorldSize * 0.1f, 0.0f, 0.0f),
            new Vector3(positiveBoundary + brickWorldSize * 0.1f, 0.0f, 0.0f),
            new Vector3(positiveBoundary - brickWorldSize * 0.1f, 0.0f, 0.0f),
            new Vector3(positiveBoundary + brickWorldSize * 0.1f, 0.0f, 0.0f)
        ];

        foreach (Vector3 cameraPosition in cameraPositions)
        {
            cascade.UpdateClipmap(cameraPosition, 32);

            Assert.Multiple(() =>
            {
                Assert.That(cascade.LogicalGridMinCell, Is.EqualTo(initialGridMin));
                Assert.That(cascade.LastScrollDeltaCells, Is.Zero);
                Assert.That(cascade.LastScrollInvalidatedBricks, Is.Zero);
                Assert.That(cascade.DirtyBrickCount, Is.Zero);
            });
        }
    }

    [Test]
    public void CascadeRuntime_MultiAxisScrollMarksEveryChangedPhysicalBrickDirty()
    {
        var cascade = CreateInitializedCleanCascade();
        DdgiClipmapCell previousGridMin = cascade.LogicalGridMinCell;
        DdgiClipmapCell previousRingOffset = cascade.RingOffset;

        cascade.UpdateClipmap(new Vector3(17.0f, 10.0f, -17.0f), 32);

        int changedPhysicalBricks = 0;
        for (int physical = 0; physical < cascade.TotalBricks; physical++)
        {
            DdgiClipmapCell previousLogical = GlobalSdfManager.GlobalSdfCascadeRuntime.GetLogicalCellForPhysicalBrick(
                physical,
                previousGridMin,
                previousRingOffset,
                cascade.BricksPerAxis);
            DdgiClipmapCell currentLogical = cascade.GetLogicalCellForPhysicalBrick(physical);
            bool contentChanged = previousLogical != currentLogical;
            if (contentChanged)
                changedPhysicalBricks++;

            Assert.That(
                cascade.IsPhysicalBrickDirty(physical),
                Is.EqualTo(contentChanged),
                $"physical brick {physical} previous={previousLogical} current={currentLogical}");
            Assert.That(
                cascade.IsPhysicalBrickPriorityDirty(physical),
                Is.EqualTo(contentChanged),
                $"physical brick {physical} previous={previousLogical} current={currentLogical}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(changedPhysicalBricks, Is.GreaterThan(0));
            Assert.That(changedPhysicalBricks, Is.LessThan(cascade.TotalBricks));
            Assert.That(cascade.DirtyBrickCount, Is.EqualTo(changedPhysicalBricks));
        });
    }

    [Test]
    public void PrepareUpdateJobs_ReportsPerCascadeScrollAndBacklogBeforeAndAfterConsumption()
    {
        var cascade0 = CreateInitializedCascade(1.0f, 32);
        DrainAllDirty(cascade0);
        var cascade1 = CreateInitializedCascade(2.0f, 32);
        DrainAllDirty(cascade1);
        var cascade2 = CreateInitializedCascade(4.0f, 32);
        DrainAllDirty(cascade2);
        var cascade3 = CreateInitializedCascade(8.0f, 32);
        DrainAllDirty(cascade3);
        Vector3 cameraPosition = new(32.0f, 0.0f, 0.0f);
        cascade0.UpdateClipmap(cameraPosition, 32);
        DrainAllDirty(cascade0);
        cascade1.UpdateClipmap(cameraPosition, 32);
        cascade2.UpdateClipmap(cameraPosition, 32);
        DrainAllDirty(cascade2);
        cascade3.UpdateClipmap(cameraPosition, 32);
        DrainAllDirty(cascade3);
        int cascade1BacklogBefore = cascade1.DirtyBrickCount;
        var manager = CreatePreparedManagerForPrepareUpdateJobs([cascade0, cascade1, cascade2, cascade3], 32);

        IReadOnlyList<GlobalSdfUpdateJob> jobs = manager.PrepareUpdateJobs(
            cameraPosition,
            requestedResolution: 32,
            brickBudget: 4);

        Assert.Multiple(() =>
        {
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(cascade1BacklogBefore));
            Assert.That(manager.LastFrameDirtyBrickBacklogBefore, Is.EqualTo(cascade1BacklogBefore));
            Assert.That(manager.LastFrameDirtyBrickBacklogAfter, Is.Zero);
            Assert.That(manager.LastFrameDirtyBrickBacklog, Is.Zero);
            Assert.That(manager.LastFrameCascadeDirtyBrickBacklogBefore[0], Is.Zero);
            Assert.That(manager.LastFrameCascadeDirtyBrickBacklogBefore[1], Is.EqualTo(cascade1BacklogBefore));
            Assert.That(manager.LastFrameCascadeDirtyBrickBacklogAfter[0], Is.Zero);
            Assert.That(manager.LastFrameCascadeDirtyBrickBacklogAfter[1], Is.Zero);
            Assert.That(manager.LastFrameCascadeScrollDeltaCells[0], Is.Zero);
            Assert.That(manager.LastFrameCascadeScrollDeltaCells[1], Is.EqualTo(cascade1.LastScrollDeltaCells));
            Assert.That(manager.LastFrameCascadeScrollInvalidatedBricks[0], Is.Zero);
            Assert.That(manager.LastFrameCascadeScrollInvalidatedBricks[1], Is.EqualTo(cascade1.LastScrollInvalidatedBricks));
        });
    }

    [Test]
    public void CascadeRuntime_ScrollRegenerationMapsExactlyNewlyEnteredWindowCells()
    {
        var cascade = CreateInitializedCleanCascade();
        DdgiClipmapCell previousGridMin = cascade.LogicalGridMinCell;
        DdgiClipmapCell previousRingOffset = cascade.RingOffset;
        HashSet<DdgiClipmapCell> previousWindow = BuildWindowCells(previousGridMin, cascade.BricksPerAxis);

        cascade.UpdateClipmap(new Vector3(17.0f, 10.0f, -17.0f), 32);
        HashSet<DdgiClipmapCell> currentWindow = BuildWindowCells(cascade.LogicalGridMinCell, cascade.BricksPerAxis);
        HashSet<DdgiClipmapCell> newlyEnteredWindowCells = new(currentWindow);
        newlyEnteredWindowCells.ExceptWith(previousWindow);
        var regeneratedWorldRegions = new HashSet<BrickWorldRegion>();
        var tiledWindowCells = new HashSet<DdgiClipmapCell>();
        var manager = CreateUninitializedManagerForSchedulerTests();
        var jobs = new List<GlobalSdfUpdateJob>();

        InvokeSelectDirtyBrickJobs(manager, cascade, jobs, cascade.TotalBricks, cascade.WorldMin + new Vector3(0.1f));

        foreach (GlobalSdfUpdateJob job in jobs)
        {
            for (int physical = job.BrickStartIndex; physical < job.BrickStartIndex + job.BrickCount; physical++)
            {
                BrickWorldRegion currentRegion = CpuEmulateComputeShaderBrickWorldRegion(cascade, physical);
                regeneratedWorldRegions.Add(currentRegion);

                DdgiClipmapCell previousLogical = GlobalSdfManager.GlobalSdfCascadeRuntime.GetLogicalCellForPhysicalBrick(
                    physical,
                    previousGridMin,
                    previousRingOffset,
                    cascade.BricksPerAxis);
                Assert.That(
                    currentRegion.AbsoluteLogicalCell,
                    Is.Not.EqualTo(previousLogical),
                    $"regenerated physical brick {physical} should have changed logical ownership");
                Assert.That(
                    newlyEnteredWindowCells.Contains(currentRegion.AbsoluteLogicalCell),
                    Is.True,
                    $"regenerated physical brick {physical} mapped to {currentRegion.AbsoluteLogicalCell}, not a newly entered cell");
                AssertBrickWorldRegionMatchesCell(cascade, currentRegion);
            }
        }

        for (int physical = 0; physical < cascade.TotalBricks; physical++)
        {
            BrickWorldRegion currentRegion = CpuEmulateComputeShaderBrickWorldRegion(cascade, physical);
            Assert.That(
                tiledWindowCells.Add(currentRegion.AbsoluteLogicalCell),
                Is.True,
                $"duplicate logical cell {currentRegion.AbsoluteLogicalCell} from physical brick {physical}");
            AssertBrickWorldRegionMatchesCell(cascade, currentRegion);
        }

        Assert.Multiple(() =>
        {
            Assert.That(regeneratedWorldRegions.Select(region => region.AbsoluteLogicalCell), Is.EquivalentTo(newlyEnteredWindowCells));
            Assert.That(tiledWindowCells, Is.EquivalentTo(currentWindow));
            Assert.That(tiledWindowCells.Count, Is.EqualTo(cascade.TotalBricks));
            Assert.That(jobs.Sum(job => job.BrickCount), Is.EqualTo(newlyEnteredWindowCells.Count));
            Assert.That(cascade.DirtyBrickCount, Is.Zero);
        });
    }

    [Test]
    public void PrepareUpdateJobs_ReplayedCameraTraversalKeepsOccupiedBricksFreshOrValidlyEmpty()
    {
        const int resolution = 32;
        var cascades = new[]
        {
            CreateInitializedCascade(0.125f, resolution),
            CreateInitializedCascade(0.25f, resolution),
            CreateInitializedCascade(0.5f, resolution),
            CreateInitializedCascade(1.0f, resolution)
        };
        var references = cascades.Select(cascade => new CascadeWriteReference(cascade.TotalBricks)).ToArray();
        var manager = CreatePreparedManagerForPrepareUpdateJobs(cascades, resolution);
        BoundingBox[] meshBounds = CreateGlobalSdfReplayMeshBounds();
        Vector3[] path = CreateGlobalSdfReplayCameraPath();

        for (int frame = 0; frame < path.Length; frame++)
        {
            IReadOnlyList<GlobalSdfUpdateJob> jobs = manager.PrepareUpdateJobs(
                path[frame],
                resolution,
                brickBudget: 4096,
                ddgiLayout: null);
            var jobbedByCascade = new HashSet<int>[cascades.Length];
            for (int i = 0; i < jobbedByCascade.Length; i++)
                jobbedByCascade[i] = new HashSet<int>();

            ApplyGlobalSdfReplayJobs(frame, jobs, cascades, references, meshBounds, jobbedByCascade);
            AssertGlobalSdfReplayOccupiedCellsFresh(frame, cascades, references, meshBounds, path[frame]);
        }
    }

    [Test]
    public void CascadeRuntime_FindsScrollPriorityDirtyBeforeGenericDirtyRegion()
    {
        var cascade = CreateInitializedCleanCascade();

        cascade.UpdateClipmap(new Vector3(10.0f, 0.0f, 0.0f), 32);
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
    public void ApplyDdgiEvents_DirtyProbeRequestsDoNotDirtyStaticGlobalSdfCascades()
    {
        var volume = new GlobalIlluminationProbeVolume
        {
            Origin = Vector3.Zero,
            Size = new Vector3(8.0f, 8.0f, 8.0f),
            ProbeCountX = 4,
            ProbeCountY = 4,
            ProbeCountZ = 4
        };
        var metadata = new DdgiProbeVolumeRuntimeMetadata(
            DdgiProbeVolumeKind.CameraClipmap,
            CascadeIndex: 0,
            LogicalGridMinX: 0,
            LogicalGridMinY: 0,
            LogicalGridMinZ: 0,
            RingOffsetX: 0,
            RingOffsetY: 0,
            RingOffsetZ: 0,
            EdgeBlendFraction: 0.0f,
            Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag,
            PhysicalFirstProbeIndex: 0,
            PhysicalProbeCapacity: volume.ProbeCount);
        var probeRequest = new DdgiFrameLayoutDirtyProbeRequest(
            VolumeIndex: 0,
            CascadeIndex: 0,
            MinCell: new DdgiClipmapCell(0, 0, 0),
            MaxCell: new DdgiClipmapCell(1, 1, 1),
            PhysicalFirstProbeIndex: 0,
            Reason: DdgiClipmapDirtyReason.Scroll);
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
            new[] { volume },
            new[] { metadata },
            Array.Empty<BoundingBox>(),
            Array.Empty<DdgiDirtyRegion>(),
            new[] { probeRequest },
            isDdgiActive: true,
            cameraRelativeEnabled: true,
            defaultVolumeIncluded: false,
            authoredVolumeCount: 0,
            cameraRelativeCascadeCount: 1,
            authoredProbeCount: 0,
            cameraRelativeProbeCount: volume.ProbeCount,
            totalPhysicalProbeCount: volume.ProbeCount,
            movementClass: DdgiCameraMovementClass.Normal,
            fastCameraMovement: true);
        MethodInfo applyDdgiEvents = typeof(GlobalSdfManager).GetMethod("ApplyDdgiEvents", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyDdgiEvents was not found.");

        applyDdgiEvents.Invoke(manager, new object?[] { layout });

        Assert.Multiple(() =>
        {
            Assert.That(cascade0.DirtyBrickCount, Is.Zero);
            Assert.That(cascade1.DirtyBrickCount, Is.Zero);
        });
    }

    [Test]
    public void ApplyDdgiEvents_FiltersRadianceAndStreamInRegionsFromGlobalSdf()
    {
        var cascade = CreateInitializedCleanCascade();
        var manager = CreateManagerWithCascades(cascade);
        BoundingBox dirtyBounds = new(cascade.WorldMin, cascade.WorldMin + new Vector3(0.25f));
        var layout = CreateDdgiFrameLayout(
            new[]
            {
                new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.StreamIn),
                new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.DirectionalLightChanged),
                new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.LocalLightChanged),
                new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.EmissiveChanged),
                new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.MaterialChanged)
            });
        MethodInfo applyDdgiEvents = typeof(GlobalSdfManager).GetMethod("ApplyDdgiEvents", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyDdgiEvents was not found.");

        applyDdgiEvents.Invoke(manager, new object?[] { layout });

        Assert.That(cascade.DirtyBrickCount, Is.Zero);
    }

    [Test]
    public void ApplyDdgiEvents_AppliesGeometryRegionsToGlobalSdf()
    {
        var cascade = CreateInitializedCleanCascade();
        var manager = CreateManagerWithCascades(cascade);
        BoundingBox dirtyBounds = new(cascade.WorldMin, cascade.WorldMin + new Vector3(0.25f));
        var layout = CreateDdgiFrameLayout(new[] { new DdgiDirtyRegion(dirtyBounds, DdgiDirtyReason.GeometryAdded) });
        MethodInfo applyDdgiEvents = typeof(GlobalSdfManager).GetMethod("ApplyDdgiEvents", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ApplyDdgiEvents was not found.");

        applyDdgiEvents.Invoke(manager, new object?[] { layout });

        Assert.That(cascade.DirtyBrickCount, Is.GreaterThan(0));
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

    private static GlobalSdfManager CreateManagerWithCascades(params GlobalSdfManager.GlobalSdfCascadeRuntime?[] cascades)
    {
        var manager = (GlobalSdfManager)RuntimeHelpers.GetUninitializedObject(typeof(GlobalSdfManager));
        var paddedCascades = new GlobalSdfManager.GlobalSdfCascadeRuntime?[4];
        for (int i = 0; i < Math.Min(cascades.Length, paddedCascades.Length); i++)
            paddedCascades[i] = cascades[i];

        SetPrivateField(manager, "_cascades", paddedCascades);
        return manager;
    }

    private static GlobalSdfManager.GlobalSdfCascadeRuntime CreateInitializedCascade(float voxelSize, int resolution)
    {
        var cascade = new GlobalSdfManager.GlobalSdfCascadeRuntime(CreateFakeVolumeTexture(), voxelSize, resolution);
        cascade.UpdateClipmap(Vector3.Zero, resolution);
        return cascade;
    }

    private static VolumeTexture CreateFakeVolumeTexture()
    {
        return (VolumeTexture)RuntimeHelpers.GetUninitializedObject(typeof(VolumeTexture));
    }

    private static GlobalSdfManager CreatePreparedManagerForPrepareUpdateJobs(
        GlobalSdfManager.GlobalSdfCascadeRuntime[] cascades,
        int resolution)
    {
        var manager = CreateManagerWithCascades(cascades);
        SetPrivateField(manager, "_idleRefreshCandidateScratch", new List<GlobalSdfManager.IdleRefreshCandidate>());
        SetPrivateField(manager, "_idleRefreshBrickScratch", new List<int>());
        SetPrivateField(manager, "_cascadeScratch", new GPUGlobalSdfCascade[4]);
        SetPrivateField(manager, "_cascadeBuffer", new BufferHandle(0, 1));
        SetPrivateField(manager, "_resolution", resolution);
        return manager;
    }

    private static BoundingBox[] CreateGlobalSdfReplayMeshBounds()
    {
        return
        [
            new BoundingBox(new Vector3(-10.0f, -0.2f, -12.0f), new Vector3(22.0f, 0.0f, 24.0f)),
            new BoundingBox(new Vector3(-4.0f, 0.0f, -8.0f), new Vector3(-3.75f, 3.0f, 18.0f)),
            new BoundingBox(new Vector3(4.0f, 0.0f, -8.0f), new Vector3(4.25f, 3.0f, 18.0f)),
            new BoundingBox(new Vector3(-6.0f, 0.0f, 10.0f), new Vector3(12.0f, 3.0f, 10.25f)),
            new BoundingBox(new Vector3(8.0f, 0.0f, -6.0f), new Vector3(8.3f, 3.0f, 16.0f)),
            new BoundingBox(new Vector3(-1.0f, 0.0f, 2.0f), new Vector3(1.0f, 2.5f, 4.0f))
        ];
    }

    private static Vector3[] CreateGlobalSdfReplayCameraPath()
    {
        var path = new List<Vector3>();
        const float y = 1.35f;
        for (int i = 0; i <= 72; i++)
            path.Add(new Vector3(0.0f, y, 3.0f + i * (12.0f / 72.0f)));

        for (int i = 0; i < 4; i++)
            path.Add(new Vector3(0.0f, y, 15.0f));

        for (int i = 1; i <= 90; i++)
            path.Add(new Vector3(0.0f, y, 15.0f - i * (18.0f / 90.0f)));

        for (int i = 1; i <= 72; i++)
            path.Add(new Vector3(i * (9.0f / 72.0f), y, -3.0f + i * (15.0f / 72.0f)));

        return path.ToArray();
    }

    private static void ApplyGlobalSdfReplayJobs(
        int frame,
        IReadOnlyList<GlobalSdfUpdateJob> jobs,
        GlobalSdfManager.GlobalSdfCascadeRuntime[] cascades,
        CascadeWriteReference[] references,
        IReadOnlyList<BoundingBox> meshBounds,
        HashSet<int>[] jobbedByCascade)
    {
        foreach (GlobalSdfUpdateJob job in jobs)
        {
            GlobalSdfManager.GlobalSdfCascadeRuntime cascade = cascades[job.CascadeIndex];
            for (int physical = job.BrickStartIndex; physical < job.BrickStartIndex + job.BrickCount; physical++)
            {
                DdgiClipmapCell logicalCell = GlobalSdfManager.GlobalSdfCascadeRuntime.GetLogicalCellForPhysicalBrick(
                    physical,
                    job.LogicalGridMinCell,
                    job.RingOffset,
                    job.BricksPerAxis);
                bool empty = IsLogicalCellEmpty(cascade.VoxelSize, logicalCell, meshBounds);
                references[job.CascadeIndex].LastBakedLogical[physical] = empty ? null : logicalCell;
                references[job.CascadeIndex].LastWriteWasEmptyPattern[physical] = empty;
                Assert.That(
                    jobbedByCascade[job.CascadeIndex].Add(physical),
                    Is.True,
                    $"frame {frame} emitted duplicate job for cascade {job.CascadeIndex} physical {physical}");
            }
        }
    }

    private static void AssertGlobalSdfReplayOccupiedCellsFresh(
        int frame,
        GlobalSdfManager.GlobalSdfCascadeRuntime[] cascades,
        CascadeWriteReference[] references,
        IReadOnlyList<BoundingBox> meshBounds,
        Vector3 cameraPosition)
    {
        for (int cascadeIndex = 0; cascadeIndex < cascades.Length; cascadeIndex++)
        {
            GlobalSdfManager.GlobalSdfCascadeRuntime cascade = cascades[cascadeIndex];
            for (int physical = 0; physical < cascade.TotalBricks; physical++)
            {
                DdgiClipmapCell currentLogical = cascade.GetLogicalCellForPhysicalBrick(physical);
                if (IsLogicalCellEmpty(cascade.VoxelSize, currentLogical, meshBounds))
                    continue;

                Assert.Multiple(() =>
                {
                    Assert.That(
                        references[cascadeIndex].LastBakedLogical[physical],
                        Is.EqualTo(currentLogical),
                        $"frame {frame} camera {cameraPosition} cascade {cascadeIndex} physical {physical} stale occupied logical cell");
                    Assert.That(
                        references[cascadeIndex].LastWriteWasEmptyPattern[physical],
                        Is.False,
                        $"frame {frame} camera {cameraPosition} cascade {cascadeIndex} physical {physical} occupied logical {currentLogical} still has empty-pattern write");
                });
            }
        }
    }

    private static bool IsLogicalCellEmpty(
        float voxelSize,
        DdgiClipmapCell logicalCell,
        IReadOnlyList<BoundingBox> meshBounds)
    {
        float brickWorldSize = voxelSize * GlobalSdfManager.BrickSize;
        Vector3 brickMin = new(
            logicalCell.X * brickWorldSize,
            logicalCell.Y * brickWorldSize,
            logicalCell.Z * brickWorldSize);
        Vector3 brickMax = brickMin + new Vector3(brickWorldSize);
        Vector3 brickPadding = new(voxelSize * 4.0f);
        BoundingBox paddedBrickBounds = new(brickMin - brickPadding, brickMax + brickPadding);
        Vector3 meshPadding = new(voxelSize);

        for (int i = 0; i < meshBounds.Count; i++)
        {
            BoundingBox bounds = meshBounds[i];
            BoundingBox paddedMeshBounds = new(bounds.Min - meshPadding, bounds.Max + meshPadding);
            if (paddedBrickBounds.Intersects(paddedMeshBounds))
                return false;
        }

        return true;
    }

    private static DdgiFrameLayout CreateDdgiFrameLayout(IReadOnlyList<DdgiDirtyRegion> dirtyRegions)
    {
        return new DdgiFrameLayout(
            Array.Empty<GlobalIlluminationProbeVolume>(),
            Array.Empty<DdgiProbeVolumeRuntimeMetadata>(),
            Array.Empty<BoundingBox>(),
            dirtyRegions,
            Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
            isDdgiActive: true,
            cameraRelativeEnabled: true,
            defaultVolumeIncluded: false,
            authoredVolumeCount: 0,
            cameraRelativeCascadeCount: 0,
            authoredProbeCount: 0,
            cameraRelativeProbeCount: 0,
            totalPhysicalProbeCount: 0,
            movementClass: DdgiCameraMovementClass.Normal,
            fastCameraMovement: false);
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

    private static void MarkAllIdleRefreshPending(GlobalSdfManager.GlobalSdfCascadeRuntime cascade)
    {
        MethodInfo markAllIdleRefreshPending = typeof(GlobalSdfManager.GlobalSdfCascadeRuntime).GetMethod(
            "MarkAllIdleRefreshPending",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MarkAllIdleRefreshPending was not found.");
        markAllIdleRefreshPending.Invoke(cascade, Array.Empty<object>());
    }

    private static int[] GetNearestPriorityDirtyBricks(
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        Vector3 cameraPosition,
        int count)
    {
        float brickWorldSize = cascade.VoxelSize * GlobalSdfManager.BrickSize;
        return Enumerable.Range(0, cascade.TotalBricks)
            .Where(cascade.IsPhysicalBrickPriorityDirty)
            .Select(index => new
            {
                Index = index,
                DistanceSquared = Vector3.DistanceSquared(
                    CalculatePhysicalBrickCenter(cascade, index, brickWorldSize),
                    cameraPosition)
            })
            .OrderBy(entry => entry.DistanceSquared)
            .ThenBy(entry => entry.Index)
            .Take(count)
            .Select(entry => entry.Index)
            .ToArray();
    }

    private static int[] GetNearestDirtyBricks(
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        Vector3 cameraPosition,
        int count)
    {
        float brickWorldSize = cascade.VoxelSize * GlobalSdfManager.BrickSize;
        return Enumerable.Range(0, cascade.TotalBricks)
            .Where(cascade.IsPhysicalBrickDirty)
            .Select(index => new
            {
                Index = index,
                DistanceSquared = Vector3.DistanceSquared(
                    CalculatePhysicalBrickCenter(cascade, index, brickWorldSize),
                    cameraPosition)
            })
            .OrderBy(entry => entry.DistanceSquared)
            .ThenBy(entry => entry.Index)
            .Take(count)
            .Select(entry => entry.Index)
            .ToArray();
    }

    private static HashSet<DdgiClipmapCell> BuildWindowCells(DdgiClipmapCell logicalGridMin, int bricksPerAxis)
    {
        var cells = new HashSet<DdgiClipmapCell>();
        for (int z = 0; z < bricksPerAxis; z++)
        {
            for (int y = 0; y < bricksPerAxis; y++)
            {
                for (int x = 0; x < bricksPerAxis; x++)
                    cells.Add(new DdgiClipmapCell(logicalGridMin.X + x, logicalGridMin.Y + y, logicalGridMin.Z + z));
            }
        }

        return cells;
    }

    private static BrickWorldRegion CpuEmulateComputeShaderBrickWorldRegion(
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        int physicalBrickIndex)
    {
        int bricksPerAxis = cascade.BricksPerAxis;
        int physicalZ = physicalBrickIndex / (bricksPerAxis * bricksPerAxis);
        int rem = physicalBrickIndex - physicalZ * bricksPerAxis * bricksPerAxis;
        int physicalY = rem / bricksPerAxis;
        int physicalX = rem - physicalY * bricksPerAxis;
        DdgiClipmapCell ringOffset = cascade.RingOffset;
        int logicalX = DdgiClipmapAddressing.PositiveModulo(physicalX - ringOffset.X, bricksPerAxis);
        int logicalY = DdgiClipmapAddressing.PositiveModulo(physicalY - ringOffset.Y, bricksPerAxis);
        int logicalZ = DdgiClipmapAddressing.PositiveModulo(physicalZ - ringOffset.Z, bricksPerAxis);
        float brickWorldSize = cascade.VoxelSize * GlobalSdfManager.BrickSize;
        var localLogicalBrick = new DdgiClipmapCell(logicalX, logicalY, logicalZ);
        var absoluteLogicalCell = new DdgiClipmapCell(
            cascade.LogicalGridMinCell.X + logicalX,
            cascade.LogicalGridMinCell.Y + logicalY,
            cascade.LogicalGridMinCell.Z + logicalZ);
        Vector3 worldMin = cascade.WorldMin + new Vector3(
            localLogicalBrick.X * brickWorldSize,
            localLogicalBrick.Y * brickWorldSize,
            localLogicalBrick.Z * brickWorldSize);
        return new BrickWorldRegion(absoluteLogicalCell, worldMin, worldMin + new Vector3(brickWorldSize));
    }

    private static void AssertBrickWorldRegionMatchesCell(
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        BrickWorldRegion region)
    {
        float brickWorldSize = cascade.VoxelSize * GlobalSdfManager.BrickSize;
        Vector3 expectedWorldMin = new(
            region.AbsoluteLogicalCell.X * brickWorldSize,
            region.AbsoluteLogicalCell.Y * brickWorldSize,
            region.AbsoluteLogicalCell.Z * brickWorldSize);
        Vector3 expectedWorldMax = expectedWorldMin + new Vector3(brickWorldSize);

        Assert.Multiple(() =>
        {
            Assert.That(region.WorldMin, Is.EqualTo(expectedWorldMin), $"world min for {region.AbsoluteLogicalCell}");
            Assert.That(region.WorldMax, Is.EqualTo(expectedWorldMax), $"world max for {region.AbsoluteLogicalCell}");
        });
    }

    private static Vector3 CalculatePhysicalBrickCenter(
        GlobalSdfManager.GlobalSdfCascadeRuntime cascade,
        int physicalBrickIndex,
        float brickWorldSize)
    {
        DdgiClipmapCell logicalCell = cascade.GetLogicalCellForPhysicalBrick(physicalBrickIndex);
        return cascade.WorldMin + new Vector3(
            (logicalCell.X - cascade.LogicalGridMinCell.X + 0.5f) * brickWorldSize,
            (logicalCell.Y - cascade.LogicalGridMinCell.Y + 0.5f) * brickWorldSize,
            (logicalCell.Z - cascade.LogicalGridMinCell.Z + 0.5f) * brickWorldSize);
    }

    private readonly record struct BrickWorldRegion(
        DdgiClipmapCell AbsoluteLogicalCell,
        Vector3 WorldMin,
        Vector3 WorldMax);

    private sealed class CascadeWriteReference
    {
        public CascadeWriteReference(int totalBricks)
        {
            LastBakedLogical = new DdgiClipmapCell?[totalBricks];
            LastWriteWasEmptyPattern = new bool[totalBricks];
        }

        public DdgiClipmapCell?[] LastBakedLogical { get; }
        public bool[] LastWriteWasEmptyPattern { get; }
    }
}

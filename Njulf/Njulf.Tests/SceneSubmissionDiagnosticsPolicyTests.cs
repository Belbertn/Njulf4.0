using System;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SceneSubmissionDiagnosticsPolicyTests
{
    [Test]
    public void ResolveMode_ReturnsGpuDirectOrIndirectWhenCompactionIsActive()
    {
        var direct = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionGpuCompactionActive = true,
            SceneSubmissionIndirectMeshletDispatchEnabled = false
        };
        var indirect = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionGpuCompactionActive = true,
            SceneSubmissionIndirectMeshletDispatchEnabled = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(direct), Is.EqualTo(SceneSubmissionMode.GpuCompactedDirect));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(indirect), Is.EqualTo(SceneSubmissionMode.GpuCompactedIndirect));
        });
    }

    [Test]
    public void ResolveMode_ReturnsGpuDirectWhenIndirectDispatchIsSkipped()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionGpuCompactionActive = true,
            SceneSubmissionIndirectMeshletDispatchEnabled = true,
            SceneSubmissionIndirectDispatchSkipReason = "scene opaque indirect dispatch buffer unavailable"
        };

        Assert.Multiple(() =>
        {
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData), Is.EqualTo(SceneSubmissionMode.GpuCompactedDirect));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveForwardPath(sceneData), Is.EqualTo(SceneSubmissionDiagnosticsPolicy.ForwardPathGpuCompactedDirect));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedEmit, Is.EqualTo("CompactedEmitTask"));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ForwardTaskShaderCompactedCounter, Is.EqualTo("CompactedCounterTask"));
        });
    }

    [Test]
    public void ResolveMode_ReturnsCpuFallbackWhenEnabledPathHasFallbackReason()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionFallbackReason = "previous CPU/GPU validation mismatch: count cpu=4 gpu=3"
        };

        Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData), Is.EqualTo(SceneSubmissionMode.CpuFallback));
    }

    [Test]
    public void BuildFallbackReason_ReportsDisabledSceneSubmission()
    {
        string reason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            new SceneRenderingData { SceneSubmissionGpuCompactionEnabled = false },
            SceneSubmissionCounterSnapshot.Invalid,
            SceneSubmissionValidationSnapshot.Invalid);

        Assert.That(reason, Is.EqualTo("GPU compaction disabled"));
    }

    [Test]
    public void BuildFallbackReason_ReportsNoEligibleCandidatesWithoutCpuFallbackMode()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true
        };

        string reason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            SceneSubmissionCounterSnapshot.Invalid,
            SceneSubmissionValidationSnapshot.Invalid);
        sceneData.SceneSubmissionFallbackReason = reason;

        Assert.Multiple(() =>
        {
            Assert.That(reason, Is.EqualTo("no eligible opaque/depth/shadow meshlets for GPU scene submission"));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData), Is.EqualTo(SceneSubmissionMode.Cpu));
        });
    }

    [Test]
    public void BuildCompactionSkipReason_MatchesEffectiveExecutionGate()
    {
        var eligible = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            OpaqueMeshletCount = 4
        };
        var disabled = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = false,
            OpaqueMeshletCount = 4
        };
        var fallback = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            OpaqueMeshletCount = 4,
            SceneSubmissionFallbackReason = "previous GPU opaque compaction overflow: emitted=4, overflow=1"
        };

        Assert.Multiple(() =>
        {
            Assert.That(SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(eligible), Is.Empty);
            Assert.That(SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(disabled), Is.EqualTo("GPU compaction disabled"));
            Assert.That(SceneSubmissionDiagnosticsPolicy.BuildCompactionSkipReason(fallback), Is.EqualTo(fallback.SceneSubmissionFallbackReason));
        });
    }

    [Test]
    public void BuildIndirectDispatchSkipReason_ReportsAvailabilityWithoutForcingCpuFallback()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionIndirectMeshletDispatchEnabled = true,
            SceneSubmissionGpuCompactionActive = true,
            SceneSubmissionGpuOpaqueCandidateCount = 16,
            SceneSubmissionGpuCompactedOpaqueCapacity = 16,
            SceneSubmissionOpaqueIndirectDispatchBuffer = new BufferHandle(1, 1),
            SceneSubmissionOpaqueIndirectDispatchBufferSize = 16
        };

        string ready = SceneSubmissionDiagnosticsPolicy.BuildIndirectDispatchSkipReason(sceneData, requiredDispatchBytes: 12);
        string tooSmall = SceneSubmissionDiagnosticsPolicy.BuildIndirectDispatchSkipReason(sceneData, requiredDispatchBytes: 32);

        Assert.Multiple(() =>
        {
            Assert.That(ready, Is.Empty);
            Assert.That(tooSmall, Does.Contain("too small"));
            Assert.That(SceneSubmissionDiagnosticsPolicy.ResolveMode(sceneData), Is.EqualTo(SceneSubmissionMode.GpuCompactedIndirect));
        });
    }

    [Test]
    public void BuildFallbackReason_ReportsOpaqueDepthAndDirectionalShadowOverflow()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            OpaqueMeshletCount = 4
        };

        string opaque = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            CounterSnapshot(overflowCount: 2, emittedCount: 8),
            SceneSubmissionValidationSnapshot.Invalid);
        string depth = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            CounterSnapshot(solidDepthOverflowCount: 3, maskedDepthOverflowCount: 1),
            SceneSubmissionValidationSnapshot.Invalid);
        string shadow = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            CounterSnapshot(directionalStaticShadowOverflowCounts: new uint[] { 0, 2 }),
            SceneSubmissionValidationSnapshot.Invalid);

        Assert.Multiple(() =>
        {
            Assert.That(opaque, Does.Contain("opaque compaction overflow"));
            Assert.That(opaque, Does.Contain("emitted=8"));
            Assert.That(depth, Does.Contain("depth compaction overflow"));
            Assert.That(depth, Does.Contain("solid=3"));
            Assert.That(shadow, Does.Contain("directional shadow compaction overflow"));
            Assert.That(shadow, Does.Contain("overflow=2"));
        });
    }

    [Test]
    public void BuildFallbackReason_PrefersValidationMismatchOverOverflow()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionValidationCompareCpuGpuLists = true,
            OpaqueMeshletCount = 4
        };

        string reason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            CounterSnapshot(overflowCount: 2),
            new SceneSubmissionValidationSnapshot(
                1,
                "mismatch",
                4,
                3,
                3,
                1,
                4096,
                "count cpu=4 gpu=3"));

        Assert.That(reason, Is.EqualTo("previous CPU/GPU validation mismatch: count cpu=4 gpu=3"));
    }

    [Test]
    public void BuildFallbackReason_DoesNotFallbackForHiZExcludedValidationMismatch()
    {
        var sceneData = new SceneRenderingData
        {
            SceneSubmissionGpuCompactionEnabled = true,
            SceneSubmissionValidationCompareCpuGpuLists = true,
            OpaqueMeshletCount = 4
        };

        string reason = SceneSubmissionDiagnosticsPolicy.BuildFallbackReason(
            sceneData,
            SceneSubmissionCounterSnapshot.Invalid,
            new SceneSubmissionValidationSnapshot(
                1,
                "count mismatch; sample over limit; Hi-Z not included",
                4,
                3,
                0,
                1,
                4096,
                "count cpu=4 gpu=3"));

        Assert.That(reason, Is.Empty);
    }

    [Test]
    public void CompareValidationSamples_MatchesOutOfOrderEquivalentLists()
    {
        SceneSubmissionValidationSnapshot result = SceneSubmissionDiagnosticsPolicy.CompareValidationSamples(
            new[]
            {
                new SceneSubmissionValidationCommandKey(4, 1, 8, 2),
                new SceneSubmissionValidationCommandKey(2, 0, 7, 1)
            },
            new[]
            {
                new SceneSubmissionValidationCommandKey(2, 0, 7, 1),
                new SceneSubmissionValidationCommandKey(4, 1, 8, 2)
            },
            cpuCount: 2,
            gpuCount: 2,
            overflowCount: 0,
            sampleLimit: 4096,
            hiZEnabled: false,
            gpuLodSelectionEnabled: false,
            compareFullSample: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo("matched"));
            Assert.That(result.MismatchCount, Is.EqualTo(0));
            Assert.That(result.ComparedSampleCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CompareValidationSamples_ReportsFirstSampleMismatch()
    {
        SceneSubmissionValidationSnapshot result = SceneSubmissionDiagnosticsPolicy.CompareValidationSamples(
            new[] { new SceneSubmissionValidationCommandKey(4, 1, 8, 2) },
            new[] { new SceneSubmissionValidationCommandKey(5, 1, 8, 2) },
            cpuCount: 1,
            gpuCount: 1,
            overflowCount: 0,
            sampleLimit: 4096,
            hiZEnabled: true,
            gpuLodSelectionEnabled: true,
            compareFullSample: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("mismatch; Hi-Z not included; GPU LOD active"));
            Assert.That(result.MismatchCount, Is.EqualTo(1));
            Assert.That(result.FirstMismatch, Does.Contain("sample[0]"));
        });
    }

    [Test]
    public void CompareValidationSamples_ReportsCountOnlyWhenSampleIsOverLimit()
    {
        SceneSubmissionValidationSnapshot result = SceneSubmissionDiagnosticsPolicy.CompareValidationSamples(
            new[] { new SceneSubmissionValidationCommandKey(1, 1, 1, 1) },
            new[] { new SceneSubmissionValidationCommandKey(1, 1, 1, 1) },
            cpuCount: 5000,
            gpuCount: 4999,
            overflowCount: 0,
            sampleLimit: 4096,
            hiZEnabled: false,
            gpuLodSelectionEnabled: false,
            compareFullSample: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo("count mismatch; sample over limit"));
            Assert.That(result.MismatchCount, Is.EqualTo(1));
            Assert.That(result.FirstMismatch, Is.EqualTo("count cpu=5000 gpu=4999"));
            Assert.That(result.ComparedSampleCount, Is.EqualTo(0));
        });
    }

    private static SceneSubmissionCounterSnapshot CounterSnapshot(
        uint emittedCount = 0,
        uint overflowCount = 0,
        uint solidDepthOverflowCount = 0,
        uint maskedDepthOverflowCount = 0,
        uint[]? directionalStaticShadowOverflowCounts = null)
    {
        return new SceneSubmissionCounterSnapshot(
            CandidateCount: 0,
            EmittedCount: emittedCount,
            FrustumRejectedCount: 0,
            OverflowCount: overflowCount,
            HiZTestedCount: 0,
            HiZRejectedCount: 0,
            Lod0EmittedCount: 0,
            Lod1EmittedCount: 0,
            Lod2EmittedCount: 0,
            MissingLodFallbackCount: 0,
            SolidDepthCandidateCount: 0,
            SolidDepthEmittedCount: 0,
            SolidDepthOverflowCount: solidDepthOverflowCount,
            MaskedDepthCandidateCount: 0,
            MaskedDepthEmittedCount: 0,
            MaskedDepthOverflowCount: maskedDepthOverflowCount,
            DirectionalStaticShadowCandidateCounts: Array.Empty<uint>(),
            DirectionalStaticShadowEmittedCounts: Array.Empty<uint>(),
            DirectionalStaticShadowRejectedCounts: Array.Empty<uint>(),
            DirectionalStaticShadowOverflowCounts: directionalStaticShadowOverflowCounts ?? Array.Empty<uint>(),
            DirectionalDynamicShadowCandidateCounts: Array.Empty<uint>(),
            DirectionalDynamicShadowEmittedCounts: Array.Empty<uint>(),
            DirectionalDynamicShadowRejectedCounts: Array.Empty<uint>(),
            DirectionalDynamicShadowOverflowCounts: Array.Empty<uint>());
    }
}

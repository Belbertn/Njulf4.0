using System.Linq;
using System.Reflection;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiFrameDataProjectorTests
{
    [Test]
    public void CoreProjection_MapsCoherentFrameFactsWithoutVolumeState()
    {
        DdgiInvalidationTelemetry invalidation = default(DdgiInvalidationTelemetry) with
        {
            VfxDirtyProbeEventCount = 7,
            LastConsumedSerial = 19UL,
            OutputRegionCount = 3,
            OverflowedThisFrame = 1
        };
        SimpleDdgiFarFieldFrameSnapshot farField = default(SimpleDdgiFarFieldFrameSnapshot) with
        {
            PagedMode = true,
            ResidentPageCount = 11,
            PageCacheBytes = 4_096UL,
            MemoryBudgetBytes = 8_192UL,
            UploadMicroseconds = 17
        };
        DdgiEmissiveContentSnapshot emissiveContent = default(DdgiEmissiveContentSnapshot) with
        {
            Active = true,
            SourceCount = 5,
            SourceRevision = 23u,
            TriangleSampling = true,
            TriangleBudget = 64
        };
        DdgiEmissiveTransportSnapshot emissive = default(DdgiEmissiveTransportSnapshot) with
        {
            Content = emissiveContent
        };
        SimpleDdgiCoreFrameResult core = default(SimpleDdgiCoreFrameResult) with
        {
            Active = true,
            InvalidationTelemetry = invalidation,
            Emissive = emissive,
            FarField = farField,
            FullPageManagementRequired = true,
            SimpleDdgiUploadMicroseconds = 29
        };
        var sceneData = new SceneRenderingData();
        var evidence = new SimpleDdgiFrameEvidenceCoordinator(3);

        DdgiFrameDataProjector.Project(
            sceneData,
            new DdgiFrameProjectionInput(
                core,
                VolumeManager: null,
                new GlobalIlluminationSettings(),
                default,
                default,
                ReceiverCacheBufferBytes: 0UL,
                ReceiverGatherBufferBytes: 0UL,
                evidence));

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.VfxDdgiDirtyProbeEventCount, Is.EqualTo(7));
            Assert.That(
                sceneData.SimpleDdgiMutationJournalLastConsumedSerial,
                Is.EqualTo(19UL));
            Assert.That(
                sceneData.SimpleDdgiMutationJournalOutputRegionCount,
                Is.EqualTo(3));
            Assert.That(
                sceneData.SimpleDdgiMutationJournalOverflowedThisFrame,
                Is.EqualTo(1));
            Assert.That(sceneData.DdgiEmissiveSourceCount, Is.EqualTo(5));
            Assert.That(sceneData.DdgiEmissiveSourceRevision, Is.EqualTo(23u));
            Assert.That(sceneData.FarFieldPagedMode, Is.EqualTo(1));
            Assert.That(sceneData.FarFieldResidentPageCount, Is.EqualTo(11));
            Assert.That(sceneData.FarFieldCacheBytes, Is.EqualTo(4_096UL));
            Assert.That(
                sceneData.FarFieldMemoryBudgetBytes,
                Is.EqualTo(8_192UL));
            Assert.That(
                sceneData.SimpleDdgiPageFullManagementRequired,
                Is.EqualTo(1));
            Assert.That(
                sceneData.CpuSimpleDdgiRecordMicroseconds,
                Is.EqualTo(29));
            Assert.That(
                sceneData.CpuFarFieldRecordMicroseconds,
                Is.EqualTo(17));
        });
    }

    [Test]
    public void AdvancedProjection_PublishesBothFactsAtomically()
    {
        var sceneData = new SceneRenderingData();
        GiRoadmapExperimentDiagnostics roadmap = default(GiRoadmapExperimentDiagnostics) with
        {
            Modes = GiRoadmapExperimentModeDiagnostics.Disabled
        };
        SimpleDdgiContentMemoryPlan memory = default;

        DdgiFrameDataProjector.ProjectAdvancedFrame(
            sceneData,
            new AdvancedGiFrameProjectionInput(roadmap, memory));

        Assert.Multiple(() =>
        {
            Assert.That(sceneData.GiRoadmapExperiments, Is.EqualTo(roadmap));
            Assert.That(sceneData.SimpleDdgiContentMemory, Is.EqualTo(memory));
        });
    }

    [Test]
    public void Projector_IsStaticAndInputHasNoRendererBackreference()
    {
        FieldInfo[] fields = typeof(DdgiFrameDataProjector).GetFields(
            BindingFlags.Static | BindingFlags.Instance |
            BindingFlags.NonPublic | BindingFlags.Public);
        Type[] inputTypes = typeof(DdgiFrameProjectionInput)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(fields, Is.Empty);
            Assert.That(inputTypes, Does.Not.Contain(typeof(VulkanRenderer)));
            Assert.That(inputTypes,
                Does.Not.Contain(typeof(SceneRenderingData)));
        });
    }
}
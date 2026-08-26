using System.Reflection;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiEmissiveTransportCoordinatorTests
{
    [Test]
    public void RendererDelegatesAllMutableEmissiveTransportState()
    {
        FieldInfo[] rendererFields = typeof(VulkanRenderer)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.Name.Contains(
                "ddgiEmissive",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.That(rendererFields, Has.Length.EqualTo(1));
        Assert.That(
            rendererFields[0].FieldType,
            Is.EqualTo(typeof(DdgiEmissiveTransportCoordinator)));
    }

    [Test]
    public void CoordinatorOwnsBothEmissiveBuffersAndMigratedMethodGraph()
    {
        Type coordinator = typeof(DdgiEmissiveTransportCoordinator);
        FieldInfo[] bufferFields = coordinator
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(BufferHandle))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                bufferFields.Select(field => field.Name),
                Is.EquivalentTo(new[]
                {
                    "_ddgiEmissiveSourceBuffer",
                    "_ddgiEmissiveSurfaceBuffer"
                }));
            Assert.That(
                coordinator.GetMethod(
                    "BuildDdgiEmissiveSources",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                coordinator.GetMethod(
                    "BuildDdgiEmissiveTriangleSources",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Not.Null);
            Assert.That(
                typeof(VulkanRenderer).GetMethod(
                    "UploadDdgiEmissiveSources",
                    BindingFlags.Instance | BindingFlags.NonPublic),
                Is.Null);
        });
    }

    [Test]
    public void PerFrameInputsAreBorrowedRatherThanRetained()
    {
        Type[] borrowedTypes =
        [
            typeof(Scene),
            typeof(GlobalIlluminationSettings),
            typeof(StagingRing),
            typeof(CommandBuffer)
        ];
        FieldInfo[] retainedInputs = typeof(DdgiEmissiveTransportCoordinator)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => borrowedTypes.Contains(field.FieldType))
            .ToArray();

        Assert.That(retainedInputs, Is.Empty);
    }

    [Test]
    public void SnapshotCarriesBufferContentAndRefinementContractsByValue()
    {
        var content = new DdgiEmissiveContentSnapshot(
            Active: true,
            TriangleSampling: true,
            TriangleBudget: 64,
            BufferContentValid: true,
            SourceCount: 12,
            HierarchyNodeCount: 7,
            SourceRevision: 9,
            SourceSignature: 11,
            BasePayloadSignature: 13,
            UploadCount: 3);
        var source = new GPUDdgiEmissiveSource();
        var snapshot = new DdgiEmissiveTransportSnapshot(
            new DdgiEmissiveBufferSnapshot(
                BufferHandle.Invalid,
                BufferHandle.Invalid,
                SourceBufferBytes: 1024,
                SurfaceBufferBytes: 2048),
            content,
            default,
            Array.Empty<SimpleDdgiRefinementDemand>(),
            default,
            RefinementSignature: 17,
            new[] { source });

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Content, Is.EqualTo(content));
            Assert.That(snapshot.Buffers.SourceBufferBytes, Is.EqualTo(1024));
            Assert.That(snapshot.Buffers.SurfaceBufferBytes, Is.EqualTo(2048));
            Assert.That(snapshot.RefinementDemands, Is.Empty);
            Assert.That(snapshot.RefinementSignature, Is.EqualTo(17));
            Assert.That(snapshot.Sources.Length, Is.EqualTo(1));
        });
    }
}

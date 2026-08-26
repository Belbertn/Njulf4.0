using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiFrameCoordinatorTests
{
    [Test]
    public void Renderer_OwnsOnlyTheCoreCoordinatorTransitionState()
    {
        FieldInfo[] rendererFields = typeof(VulkanRenderer).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo[] coordinatorFields =
            typeof(SimpleDdgiFrameCoordinator).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Multiple(() =>
        {
            Assert.That(
                rendererFields.Count(field =>
                    field.FieldType == typeof(SimpleDdgiFrameCoordinator)),
                Is.EqualTo(1));
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiRefinementFocusTracker)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiGuidingFrameConfiguration)),
                Is.False);
            Assert.That(
                coordinatorFields.Any(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiRefinementFocusTracker)),
                Is.True);
        });
    }

    [Test]
    public void FrameRequest_IsTypedAndCoordinatorRetainsNoFrameInputs()
    {
        Type[] requestTypes = typeof(SimpleDdgiCoreFrameRequest)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.PropertyType)
            .ToArray();
        Type[] retainedTypes = typeof(SimpleDdgiFrameCoordinator)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(requestTypes,
                Does.Contain(typeof(SimpleDdgiFrameSceneInput)));
            Assert.That(requestTypes,
                Does.Contain(typeof(SimpleDdgiFrameViewInput)));
            Assert.That(requestTypes,
                Does.Contain(typeof(SimpleDdgiFrameIdentity)));
            Assert.That(requestTypes,
                Does.Contain(typeof(SimpleDdgiFrameCapabilities)));
            Assert.That(requestTypes,
                Does.Contain(typeof(SimpleDdgiFrameAdmissionInput)));
            Assert.That(requestTypes,
                Does.Not.Contain(typeof(VulkanRenderer)));
            Assert.That(requestTypes,
                Does.Not.Contain(typeof(SceneRenderingData)));
            Assert.That(requestTypes,
                Does.Not.Contain(typeof(ICamera)));
            Assert.That(retainedTypes,
                Does.Not.Contain(typeof(Scene)));
            Assert.That(retainedTypes,
                Does.Not.Contain(typeof(SceneRenderingData)));
            Assert.That(retainedTypes,
                Does.Not.Contain(typeof(RenderSettings)));
            Assert.That(retainedTypes,
                Does.Not.Contain(typeof(CommandBuffer)));
        });
    }

    [Test]
    public void GuidingPlanner_ExplicitOffFailsClosedWithoutResources()
    {
        var gi = new GlobalIlluminationSettings
        {
            SimpleDdgiDirectionalGuidingMode =
                SimpleDdgiDirectionalGuidingMode.Off
        };

        SimpleDdgiGuidingFrameConfiguration result =
            SimpleDdgiGuidingConfigurationPlanner.Compile(
                new SimpleDdgiGuidingConfigurationRequest(
                    SimpleDdgiActive: true,
                    GraphUsesDirectionalGuiding: true,
                    gi,
                    AdvancedGiRuntimeContentState.Unconfigured,
                    default,
                    TotalPhysicalProbeCapacity: 128,
                    DirectionSlotsPerProbe: 64,
                    CompactTraceProbeCapacity: 128,
                    default,
                    SimpleDdgiGuidingFrameConfiguration.Disabled,
                    MemoryHeadroom: ulong.MaxValue,
                    MinimumStorageBufferOffsetAlignment: 256UL,
                    MaximumStorageBufferRange: uint.MaxValue),
                out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsEnabled, Is.False);
            Assert.That(reason, Is.EqualTo("directional-guiding-disabled"));
        });
    }

    [Test]
    public void CoreSource_PreservesLeafOrderAndRendererOwnsEffectsOnly()
    {
        string coordinator = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiFrameCoordinator.cs"));
        string renderer = File.ReadAllText(FindSourceFile(
            "Njulf.Rendering",
            "VulkanRenderer.cs"));
        int collect = coordinator.IndexOf(
            "_invalidation.CollectFrame(",
            StringComparison.Ordinal);
        int emissive = coordinator.IndexOf(
            "_emissiveTransport.PrepareFrame(",
            collect,
            StringComparison.Ordinal);
        int farField = coordinator.IndexOf(
            "_farField.Upload(",
            emissive,
            StringComparison.Ordinal);
        int identity = coordinator.IndexOf(
            "_invalidation.ResolveFrameIdentity(",
            farField,
            StringComparison.Ordinal);
        int evidence = coordinator.IndexOf(
            "_volumeManager.SetSchedulerCostEstimate(",
            identity,
            StringComparison.Ordinal);
        int upload = coordinator.IndexOf(
            "_volumeManager.Upload(",
            evidence,
            StringComparison.Ordinal);
        int guiding = coordinator.IndexOf(
            "PrepareGuidingFrame(request);",
            upload,
            StringComparison.Ordinal);
        int paging = coordinator.IndexOf(
            "_volumeManager.PrepareProbePageManagement(",
            guiding,
            StringComparison.Ordinal);
        int reflection = coordinator.IndexOf(
            "ResolveReflectionRecaptureIntent(",
            paging,
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(collect, Is.GreaterThanOrEqualTo(0));
            Assert.That(emissive, Is.GreaterThan(collect));
            Assert.That(farField, Is.GreaterThan(emissive));
            Assert.That(identity, Is.GreaterThan(farField));
            Assert.That(evidence, Is.GreaterThan(identity));
            Assert.That(upload, Is.GreaterThan(evidence));
            Assert.That(guiding, Is.GreaterThan(upload));
            Assert.That(paging, Is.GreaterThan(guiding));
            Assert.That(reflection, Is.GreaterThan(paging));
            Assert.That(renderer,
                Does.Contain("coordinator.PrepareFrame("));
            Assert.That(renderer,
                Does.Contain("ApplyReflectionRecaptureIntent("));
            Assert.That(renderer,
                Does.Not.Contain("PrepareDdgiProbeVolumes"));
            Assert.That(renderer,
                Does.Not.Contain("TryReconcileAdvancedGiScratchArena"));
            Assert.That(coordinator,
                Does.Not.Contain("VulkanRenderer"));
        });
    }

    private static string FindSourceFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativeParts)}.");
    }
}
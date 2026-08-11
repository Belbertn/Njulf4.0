using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderGraphResourceDeclarationTests
{
    [Test]
    public void ResourceInventory_ReportsRegisteredResources()
    {
        var graph = new RenderGraph();

        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.SceneColor,
            "Scene color",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));

        Assert.That(graph.ResourceInventory, Has.Count.EqualTo(1));
        Assert.That(graph.ResourceInventory, Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneColor));
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenPassUsesUndeclaredResource()
    {
        var graph = new RenderGraph();
        graph.AddPass(CreateUninitializedPass("TestPass"));
        graph.DeclarePassResources(
            "TestPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.SceneColor, RenderGraphResourceAccess.Read));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("undeclared graph resource"));
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenDeclarationTargetsUnknownPass()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());
        graph.DeclarePassResources(
            "MissingPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.SceneColor, RenderGraphResourceAccess.Read));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("unknown pass"));
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenAddedPassHasNoDeclaration()
    {
        var graph = new RenderGraph();
        graph.AddPass(CreateUninitializedPass("TestPass"));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("no graph resource declaration"));
    }

    [Test]
    public void ValidateResourceDeclarations_PassesForDeclaredPassAndRegisteredResource()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());
        graph.AddPass(CreateUninitializedPass("TestPass"));
        graph.DeclarePassResources(
            "TestPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.SceneColor, RenderGraphResourceAccess.Write));

        Assert.DoesNotThrow(graph.ValidateResourceDeclarations);
        Assert.That(graph.GetPassResourceUsages("TestPass"), Has.Count.EqualTo(1));
    }

    [Test]
    public void DeviceIdleFallback_RemovesOptionalPassAndLogicalResourceAtomically()
    {
        using var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.NearFieldDirectSource,
            "optional near-field source",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Transient,
            Persistent: false));
        graph.AddPass(CreateUninitializedPass("OptionalC5Pass"));
        graph.DeclarePassResources(
            "OptionalC5Pass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.NearFieldDirectSource,
                RenderGraphResourceAccess.Write));

        Assert.That(() => graph.UnregisterResourcesAfterDeviceIdle(
                [RenderGraphResourceId.NearFieldDirectSource]),
            Throws.InvalidOperationException.With.Message.Contains(
                "OptionalC5Pass"));

        int removedPasses = graph.RemovePassesAfterDeviceIdle(
            ["OptionalC5Pass"]);
        int removedResources = graph.UnregisterResourcesAfterDeviceIdle(
            [RenderGraphResourceId.NearFieldDirectSource]);

        Assert.Multiple(() =>
        {
            Assert.That(removedPasses, Is.EqualTo(1));
            Assert.That(removedResources, Is.EqualTo(1));
            Assert.That(graph.PassNames, Does.Not.Contain("OptionalC5Pass"));
            Assert.That(graph.ResourceInventory, Is.Empty);
        });
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenImageLayoutIntentHasNoStageOrAccess()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());
        graph.AddPass(CreateUninitializedPass("TestPass"));
        graph.DeclarePassResources(
            "TestPass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.SceneColor,
                RenderGraphResourceAccess.Read,
                ImageLayout: ImageLayout.ShaderReadOnlyOptimal));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("without stage/access intent"));
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenNonImageResourceDeclaresImageLayout()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.SceneSubmissionBuffers,
            "Scene submission buffers",
            RenderGraphResourceKind.BufferSet,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        graph.AddPass(CreateUninitializedPass("TestPass"));
        graph.DeclarePassResources(
            "TestPass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.SceneSubmissionBuffers,
                RenderGraphResourceAccess.Read,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                ImageLayout.General,
                RenderGraphQueueIntent.Compute));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("non-image graph resource"));
    }

    [Test]
    public void ResourceUsage_CapturesBarrierPlanningIntent()
    {
        var usage = new RenderGraphResourceUsage(
            RenderGraphResourceId.FogOutput,
            RenderGraphResourceAccess.Write,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            ImageLayout.General,
            RenderGraphQueueIntent.Compute);

        Assert.Multiple(() =>
        {
            Assert.That(usage.StageMask, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
            Assert.That(usage.AccessMask, Is.EqualTo(AccessFlags2.ShaderStorageWriteBit));
            Assert.That(usage.ImageLayout, Is.EqualTo(ImageLayout.General));
            Assert.That(usage.QueueIntent, Is.EqualTo(RenderGraphQueueIntent.Compute));
        });
    }

    [Test]
    public void RegisterResource_FailsForDuplicateResourceId()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());

        Assert.That(
            () => graph.RegisterResource(CreateSceneColorDescriptor()),
            Throws.InvalidOperationException.With.Message.Contains("already registered"));
    }

    [Test]
    public void RegisterResource_FailsWhenImageResourceHasNoFormat()
    {
        var graph = new RenderGraph();

        Assert.That(
            () => graph.RegisterResource(new RenderGraphResourceDescriptor(
                RenderGraphResourceId.LdrSceneColor,
                "LDR scene color",
                RenderGraphResourceKind.Image,
                null,
                RenderGraphResourceSizePolicy.Swapchain,
                RenderGraphResourceLifetime.Persistent,
                Persistent: true)),
            Throws.ArgumentException.With.Message.Contains("require a format"));
    }

    [Test]
    public void RegisterResource_FailsWhenBufferResourceDeclaresFormat()
    {
        var graph = new RenderGraph();

        Assert.That(
            () => graph.RegisterResource(new RenderGraphResourceDescriptor(
                RenderGraphResourceId.SceneSubmissionBuffers,
                "Scene submission buffers",
                RenderGraphResourceKind.BufferSet,
                Format.R8Unorm,
                RenderGraphResourceSizePolicy.Dynamic,
                RenderGraphResourceLifetime.Imported,
                Persistent: true)),
            Throws.ArgumentException.With.Message.Contains("Non-image"));
    }

    [Test]
    public void RegisterResource_FailsWhenLifetimeAndPersistenceConflict()
    {
        var graph = new RenderGraph();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => graph.RegisterResource(new RenderGraphResourceDescriptor(
                    RenderGraphResourceId.SceneColor,
                    "Scene color",
                    RenderGraphResourceKind.Image,
                    Format.R16G16B16A16Sfloat,
                    RenderGraphResourceSizePolicy.SceneResolution,
                    RenderGraphResourceLifetime.Imported,
                    Persistent: false)),
                Throws.ArgumentException.With.Message.Contains("Imported"));

            Assert.That(
                () => graph.RegisterResource(new RenderGraphResourceDescriptor(
                    RenderGraphResourceId.TransientIntermediate,
                    "Transient",
                    RenderGraphResourceKind.External,
                    null,
                    RenderGraphResourceSizePolicy.Dynamic,
                    RenderGraphResourceLifetime.Transient,
                    Persistent: true)),
                Throws.ArgumentException.With.Message.Contains("Transient"));
        });
    }

    [Test]
    public void ValidateResourceDeclarations_FailsWhenOwnedResourceIsReadBeforeWrite()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateLdrSceneColorDescriptor());
        graph.AddPass(CreateUninitializedPass("ReadPass"));
        graph.DeclarePassResources(
            "ReadPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.LdrSceneColor, RenderGraphResourceAccess.Read));

        Assert.That(
            graph.ValidateResourceDeclarations,
            Throws.InvalidOperationException.With.Message.Contains("before any prior pass writes"));
    }

    [Test]
    public void ValidateResourceDeclarations_AllowsOwnedResourceReadAfterWrite()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateLdrSceneColorDescriptor());
        graph.AddPass(CreateUninitializedPass("WritePass"));
        graph.AddPass(CreateUninitializedPass("ReadPass"));
        graph.DeclarePassResources(
            "WritePass",
            new RenderGraphResourceUsage(RenderGraphResourceId.LdrSceneColor, RenderGraphResourceAccess.Write));
        graph.DeclarePassResources(
            "ReadPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.LdrSceneColor, RenderGraphResourceAccess.Read));

        Assert.DoesNotThrow(graph.ValidateResourceDeclarations);
    }

    [Test]
    public void CreateDiagnostics_ReportsInventoryPassListsAndFeatureIsolation()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());
        graph.RegisterResource(CreateLdrSceneColorDescriptor());
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.TransientIntermediate,
            "Transient intermediates",
            RenderGraphResourceKind.External,
            null,
            RenderGraphResourceSizePolicy.Dynamic,
            RenderGraphResourceLifetime.Transient,
            Persistent: false));
        graph.AddPass(CreateUninitializedPass("AmbientOcclusionPass"));
        graph.AddPass(CreateUninitializedPass("ToneMapCompositePass"));
        graph.DeclarePassResources(
            "AmbientOcclusionPass",
            new RenderGraphResourceUsage(RenderGraphResourceId.SceneColor, RenderGraphResourceAccess.Read));
        graph.DeclarePassResources(
            "ToneMapCompositePass",
            new RenderGraphResourceUsage(RenderGraphResourceId.LdrSceneColor, RenderGraphResourceAccess.Write),
            new RenderGraphResourceUsage(RenderGraphResourceId.TransientIntermediate, RenderGraphResourceAccess.ReadWrite));

        var diagnostics = graph.CreateDiagnostics(RenderFeatureIsolationMode.Geometry);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.ResourceCount, Is.EqualTo(3));
            Assert.That(diagnostics.PassCount, Is.EqualTo(2));
            Assert.That(diagnostics.TransientResourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.AliasableResourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.ImportedResourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.Resources, Has.Some.Property(nameof(Njulf.Rendering.Diagnostics.RenderGraphResourceDiagnostics.Id)).EqualTo("LdrSceneColor"));
            Assert.That(diagnostics.Passes.Single(pass => pass.Name == "AmbientOcclusionPass").EnabledByFeatureIsolation, Is.False);
            Assert.That(diagnostics.Passes.Single(pass => pass.Name == "ToneMapCompositePass").Writes, Does.Contain("LdrSceneColor"));
            Assert.That(diagnostics.Passes.Single(pass => pass.Name == "ToneMapCompositePass").ReadWrites, Does.Contain("TransientIntermediate"));
        });
    }

    [Test]
    public void CreateOwnedRenderTarget_FailsForImportedResource()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());

        Assert.That(
            () => graph.CreateOwnedRenderTarget(
                RenderGraphResourceId.SceneColor,
                null!,
                "Scene color",
                Format.R16G16B16A16Sfloat,
                new Extent2D { Width = 1, Height = 1 },
                new Njulf.Rendering.Resources.RenderTargetDescriptor(colorAttachment: true, sampled: true)),
            Throws.InvalidOperationException.With.Message.Contains("imported"));
    }

    [Test]
    public void RegisterImportedRenderTarget_TracksImportedImagesForLayoutPlanningWithoutTakingOwnership()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateSceneColorDescriptor());
        var target = (RenderTarget)RuntimeHelpers.GetUninitializedObject(typeof(RenderTarget));

        graph.RegisterImportedRenderTarget(RenderGraphResourceId.SceneColor, target);
        graph.RegisterImportedRenderTarget(RenderGraphResourceId.SceneColor, target);

        Assert.Multiple(() =>
        {
            Assert.That(graph.OwnsResource(RenderGraphResourceId.SceneColor), Is.False);
            Assert.That(graph.GetLayoutTrackedRenderTargets(RenderGraphResourceId.SceneColor), Is.EqualTo(new[] { target }));
        });
    }

    [Test]
    public void RegisterImportedRenderTarget_RejectsGraphOwnedResources()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(CreateLdrSceneColorDescriptor());
        var target = (RenderTarget)RuntimeHelpers.GetUninitializedObject(typeof(RenderTarget));

        Assert.That(
            () => graph.RegisterImportedRenderTarget(RenderGraphResourceId.LdrSceneColor, target),
            Throws.InvalidOperationException.With.Message.Contains("not imported"));
    }

    [Test]
    public void RegisterImportedImageTarget_TracksStandaloneMipChainsWithoutTakingOwnership()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.HiZPyramid,
            "Hi-Z pyramid",
            RenderGraphResourceKind.ImageChain,
            Format.R32Sfloat,
            RenderGraphResourceSizePolicy.HalfResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        var target = new LayoutTrackedImageStub();

        graph.RegisterImportedImageTarget(RenderGraphResourceId.HiZPyramid, target);
        graph.RegisterImportedImageTarget(RenderGraphResourceId.HiZPyramid, target);

        Assert.Multiple(() =>
        {
            Assert.That(graph.OwnsResource(RenderGraphResourceId.HiZPyramid), Is.False);
            Assert.That(graph.GetImportedImageTargets(RenderGraphResourceId.HiZPyramid), Is.EqualTo(new[] { target }));
        });
    }

    [Test]
    public void ImportedImageTarget_ReceivesGraphEntryAndFinalLayouts()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.HiZPyramid,
            "Hi-Z pyramid",
            RenderGraphResourceKind.ImageChain,
            Format.R32Sfloat,
            RenderGraphResourceSizePolicy.HalfResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        graph.DeclarePassResources(
            "HiZBuildPass",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.HiZPyramid,
                RenderGraphResourceAccess.Write,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.General,
                RenderGraphQueueIntent.Compute,
                ImageLayout.ShaderReadOnlyOptimal));
        var target = new LayoutTrackedImageStub();
        graph.RegisterImportedImageTarget(RenderGraphResourceId.HiZPyramid, target);
        var sceneData = new SceneRenderingData();

        ExecuteGraphPlannedBarriers(graph, "HiZBuildPass", sceneData);
        ExecuteGraphFinalBarriers(graph, "HiZBuildPass", sceneData);

        Assert.Multiple(() =>
        {
            Assert.That(target.Layout, Is.EqualTo(ImageLayout.ShaderReadOnlyOptimal));
            Assert.That(target.Transitions, Is.EqualTo(new[]
            {
                ImageLayout.General,
                ImageLayout.ShaderReadOnlyOptimal
            }));
            Assert.That(graph.LastPlannedBarriers.Select(barrier => barrier.NewLayout), Is.EqualTo(new[]
            {
                ImageLayout.General,
                ImageLayout.ShaderReadOnlyOptimal
            }));
        });
    }

    [Test]
    public void CompleteSplitExecution_PublishesOneBarrierSummaryEntryPerBarrierAndPreservesAsyncPlanSummary()
    {
        var graph = new RenderGraph();
        List<RenderGraphPlannedBarrier> plannedBarriers = GetPlannedBarrierList(graph);
        var barriers = new[]
        {
            CreatePlannedBarrier("DepthPrePass", RenderGraphResourceId.SceneDepth),
            CreatePlannedBarrier("HiZBuildPass", RenderGraphResourceId.HiZPyramid),
            CreatePlannedBarrier("ForwardPlusPass", RenderGraphResourceId.SceneColor)
        };

        foreach (RenderGraphPlannedBarrier barrier in barriers)
        {
            plannedBarriers.Add(barrier);
            AppendBarrierSummary(graph, barrier);
        }

        var sceneData = new SceneRenderingData();
        graph.CompleteSplitExecution(sceneData);

        string[] summaryEntries = sceneData.GraphBarrierSummary.Split(new[] { "; " }, StringSplitOptions.None);
        Assert.Multiple(() =>
        {
            Assert.That(summaryEntries, Has.Length.EqualTo(graph.LastPlannedBarriers.Count));
            Assert.That(summaryEntries, Is.EqualTo(new[]
            {
                "DepthPrePass:SceneDepth General->ShaderReadOnlyOptimal",
                "HiZBuildPass:HiZPyramid General->ShaderReadOnlyOptimal",
                "ForwardPlusPass:SceneColor General->ShaderReadOnlyOptimal"
            }));
        });

        sceneData.GraphBarrierSummary = "async plan: 2 graphics segments, 1 compute segments, 3 queue-family handoffs";
        graph.CompleteSplitExecution(sceneData);

        Assert.That(
            sceneData.GraphBarrierSummary,
            Is.EqualTo("async plan: 2 graphics segments, 1 compute segments, 3 queue-family handoffs"));

        var nextFrameSceneData = new SceneRenderingData { GraphBarrierSummary = "stale summary" };
        graph.BeginSplitExecution(nextFrameSceneData);
        graph.CompleteSplitExecution(nextFrameSceneData);

        Assert.Multiple(() =>
        {
            Assert.That(graph.LastPlannedBarriers, Is.Empty);
            Assert.That(nextFrameSceneData.GraphBarrierSummary, Is.Empty);
        });
    }

    [Test]
    public void ConcreteBindings_SelectCurrentAndPreviousHistoryBanksByFrameParity()
    {
        var bindings = new RenderGraphResourceBindings();
        bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                "history bank zero",
                new Silk.NET.Vulkan.Buffer { Handle = 901 },
                byteSize: 64,
                permittedQueueFamilies: new uint[] { 0 },
                initialOwnerQueueFamily: 0,
                historyIndex: 0,
                allocationGeneration: 901),
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                "history bank one",
                new Silk.NET.Vulkan.Buffer { Handle = 902 },
                byteSize: 64,
                permittedQueueFamilies: new uint[] { 0 },
                initialOwnerQueueFamily: 0,
                historyIndex: 1,
                allocationGeneration: 902)
        });

        Assert.Multiple(() =>
        {
            Assert.That(bindings.GetBindings(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                0,
                RenderGraphHistoryBindingSelection.Current).Single().Buffer.Handle,
                Is.EqualTo(901));
            Assert.That(bindings.GetBindings(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                0,
                RenderGraphHistoryBindingSelection.Previous).Single().Buffer.Handle,
                Is.EqualTo(902));
            Assert.That(bindings.GetBindings(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                1,
                RenderGraphHistoryBindingSelection.Current).Single().Buffer.Handle,
                Is.EqualTo(902));
            Assert.That(bindings.GetBindings(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                1,
                RenderGraphHistoryBindingSelection.Previous).Single().Buffer.Handle,
                Is.EqualTo(901));
        });
    }

    [Test]
    public void ConcreteBindings_RejectAliasedHistoryBanks()
    {
        var bindings = new RenderGraphResourceBindings();
        var shared = new Silk.NET.Vulkan.Buffer { Handle = 903 };

        Assert.That(() => bindings.Replace(new[]
        {
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                "aliased history bank zero",
                shared,
                byteSize: 64,
                permittedQueueFamilies: new uint[] { 0 },
                initialOwnerQueueFamily: 0,
                historyIndex: 0,
                allocationGeneration: 903),
            RenderGraphConcreteResourceBinding.ForBuffer(
                RenderGraphResourceId.NearFieldResidualHistoryMetadata,
                "aliased history bank one",
                shared,
                byteSize: 64,
                permittedQueueFamilies: new uint[] { 0 },
                initialOwnerQueueFamily: 0,
                historyIndex: 1,
                allocationGeneration: 903)
        }), Throws.InvalidOperationException.With.Message.Contains("overlap"));
    }

    [Test]
    public void HistorySelectedBarriers_TransitionOnlyTheSelectedPhysicalBank()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.TaaHistory,
            "history chain",
            RenderGraphResourceKind.ImageChain,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        graph.DeclarePassResources(
            "WriteCurrent",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.TaaHistory,
                RenderGraphResourceAccess.Write,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.General,
                RenderGraphQueueIntent.Compute,
                HistoryBinding: RenderGraphHistoryBindingSelection.Current));
        graph.DeclarePassResources(
            "ReadPrevious",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.TaaHistory,
                RenderGraphResourceAccess.Read,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit,
                ImageLayout.ShaderReadOnlyOptimal,
                RenderGraphQueueIntent.Compute,
                HistoryBinding: RenderGraphHistoryBindingSelection.Previous));
        var bank0 = new LayoutTrackedImageStub();
        var bank1 = new LayoutTrackedImageStub();
        graph.RegisterImportedImageTarget(RenderGraphResourceId.TaaHistory, bank0);
        graph.RegisterImportedImageTarget(RenderGraphResourceId.TaaHistory, bank1);
        var sceneData = new SceneRenderingData();

        ExecuteGraphPlannedBarriers(graph, "WriteCurrent", sceneData, frameIndex: 0);
        ExecuteGraphPlannedBarriers(graph, "ReadPrevious", sceneData, frameIndex: 0);

        Assert.Multiple(() =>
        {
            Assert.That(bank0.Transitions, Is.EqualTo(new[] { ImageLayout.General }));
            Assert.That(bank1.Transitions, Is.EqualTo(new[] { ImageLayout.ShaderReadOnlyOptimal }));
            Assert.That(graph.LastPlannedBarriers.Select(static barrier => barrier.HistoryIndex),
                Is.EqualTo(new[] { 0, 1 }));
        });
    }

    [Test]
    public void SameQueueBarriers_PreserveDependenciesAcrossLogicalImageAliases()
    {
        var graph = new RenderGraph();
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.SceneColor,
            "scene color alias",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        graph.RegisterResource(new RenderGraphResourceDescriptor(
            RenderGraphResourceId.NearFieldDirectSource,
            "near-field source alias",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true));
        graph.DeclarePassResources(
            "WriteAlias",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.SceneColor,
                RenderGraphResourceAccess.Write,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageWriteBit,
                ImageLayout.General,
                RenderGraphQueueIntent.Graphics));
        graph.DeclarePassResources(
            "ReadAlias",
            new RenderGraphResourceUsage(
                RenderGraphResourceId.NearFieldDirectSource,
                RenderGraphResourceAccess.Read,
                PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderStorageReadBit,
                ImageLayout.General,
                RenderGraphQueueIntent.Graphics));

        var sharedTarget = new LayoutTrackedImageStub();
        graph.RegisterImportedImageTarget(RenderGraphResourceId.SceneColor, sharedTarget);
        graph.RegisterImportedImageTarget(RenderGraphResourceId.NearFieldDirectSource, sharedTarget);
        var sceneData = new SceneRenderingData();

        ExecuteGraphPlannedBarriers(graph, "WriteAlias", sceneData);
        ExecuteGraphPlannedBarriers(graph, "ReadAlias", sceneData);

        Assert.Multiple(() =>
        {
            Assert.That(sharedTarget.Transitions, Is.EqualTo(new[]
            {
                ImageLayout.General,
                ImageLayout.General
            }));
            Assert.That(sharedTarget.ForcedTransitions, Is.EqualTo(new[] { false, true }));
            Assert.That(graph.LastPlannedBarriers, Has.Count.EqualTo(2));
            Assert.That(graph.LastPlannedBarriers[^1].PreviousAccess, Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(graph.LastPlannedBarriers[^1].NextAccess, Is.EqualTo(RenderGraphResourceAccess.Read));
        });
    }

    private static RenderGraphResourceDescriptor CreateSceneColorDescriptor()
    {
        return new RenderGraphResourceDescriptor(
            RenderGraphResourceId.SceneColor,
            "Scene color",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.SceneResolution,
            RenderGraphResourceLifetime.Imported,
            Persistent: true);
    }

    private sealed class LayoutTrackedImageStub : IRenderGraphLayoutTrackedImage
    {
        public List<ImageLayout> Transitions { get; } = new();
        public List<bool> ForcedTransitions { get; } = new();
        public ImageLayout Layout { get; private set; } = ImageLayout.Undefined;

        public void TransitionToLayout(
            CommandBuffer cmd,
            ImageLayout newLayout,
            PipelineStageFlags2 dstStage,
            AccessFlags2 dstAccess,
            PipelineStageFlags2? srcStage = null,
            AccessFlags2? srcAccess = null,
            bool force = false)
        {
            Layout = newLayout;
            Transitions.Add(newLayout);
            ForcedTransitions.Add(force);
        }
    }

    private static void ExecuteGraphPlannedBarriers(
        RenderGraph graph,
        string passName,
        SceneRenderingData sceneData,
        int frameIndex = 0)
    {
        MethodInfo method = typeof(RenderGraph).GetMethod(
            "ExecuteGraphPlannedBarriers",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RenderGraph).FullName, "ExecuteGraphPlannedBarriers");
        method.Invoke(graph, new object[] { default(CommandBuffer), passName, frameIndex, sceneData, false, false });
    }

    private static void ExecuteGraphFinalBarriers(
        RenderGraph graph,
        string passName,
        SceneRenderingData sceneData)
    {
        MethodInfo method = typeof(RenderGraph).GetMethod(
            "ExecuteGraphFinalBarriers",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RenderGraph).FullName, "ExecuteGraphFinalBarriers");
        method.Invoke(graph, new object[] { default(CommandBuffer), passName, 0, sceneData, false });
    }

    private static RenderGraphResourceDescriptor CreateLdrSceneColorDescriptor()
    {
        return new RenderGraphResourceDescriptor(
            RenderGraphResourceId.LdrSceneColor,
            "LDR scene color",
            RenderGraphResourceKind.Image,
            Format.R16G16B16A16Sfloat,
            RenderGraphResourceSizePolicy.Swapchain,
            RenderGraphResourceLifetime.Persistent,
            Persistent: true);
    }

    private static RenderPassBase CreateUninitializedPass(string name)
    {
        var pass = (NamedTestPass)RuntimeHelpers.GetUninitializedObject(typeof(NamedTestPass));
        FieldInfo field = typeof(RenderPassBase).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderPassBase.Name backing field was not found.");
        field.SetValue(pass, name);
        return pass;
    }

    private static List<RenderGraphPlannedBarrier> GetPlannedBarrierList(RenderGraph graph)
    {
        FieldInfo field = typeof(RenderGraph).GetField("_framePlannedBarriers", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderGraph planned-barrier field was not found.");
        return (List<RenderGraphPlannedBarrier>)field.GetValue(graph)!;
    }

    private static void AppendBarrierSummary(RenderGraph graph, RenderGraphPlannedBarrier barrier)
    {
        MethodInfo method = typeof(RenderGraph).GetMethod("AppendBarrierSummary", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RenderGraph).FullName, "AppendBarrierSummary");
        method.Invoke(graph, new object[] { barrier });
    }

    private static RenderGraphPlannedBarrier CreatePlannedBarrier(string passName, RenderGraphResourceId resource)
    {
        return new RenderGraphPlannedBarrier(
            passName,
            resource,
            RenderGraphResourceAccess.Write,
            RenderGraphResourceAccess.Read,
            ImageLayout.General,
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit,
            RenderGraphQueueIntent.Graphics,
            RenderGraphQueueIntent.Graphics,
            QueueOwnershipTransition: false,
            Executed: true);
    }

    private sealed class NamedTestPass : RenderPassBase
    {
        private NamedTestPass()
            : base("unused", null!, null!, null!)
        {
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, Njulf.Rendering.Data.SceneRenderingData sceneData)
        {
        }
    }

}

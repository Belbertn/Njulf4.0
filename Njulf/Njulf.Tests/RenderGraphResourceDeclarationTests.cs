using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Pipeline;
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

    private static RenderPassBase CreateUninitializedPass(string name)
    {
        var pass = (NamedTestPass)RuntimeHelpers.GetUninitializedObject(typeof(NamedTestPass));
        FieldInfo field = typeof(RenderPassBase).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderPassBase.Name backing field was not found.");
        field.SetValue(pass, name);
        return pass;
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

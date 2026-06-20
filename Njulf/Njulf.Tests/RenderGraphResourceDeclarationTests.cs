using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Data;
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

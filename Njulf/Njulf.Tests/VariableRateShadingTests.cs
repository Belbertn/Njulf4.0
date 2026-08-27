using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class VariableRateShadingTests
{
    [Test]
    public void Presets_EnableOnlyPerformanceOrientedProfiles()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            AssertPreset(settings, RenderQualityPreset.Low,
                VariableRateShadingMode.Auto);
            AssertPreset(settings, RenderQualityPreset.Medium,
                VariableRateShadingMode.Auto);
            AssertPreset(settings, RenderQualityPreset.DdgiHigh,
                VariableRateShadingMode.Auto);
            AssertPreset(settings, RenderQualityPreset.High,
                VariableRateShadingMode.Off);
            AssertPreset(settings, RenderQualityPreset.Ultra,
                VariableRateShadingMode.Off);
        });
    }

    [Test]
    public void CurrentSettingsFile_RoundTripsExplicitMode()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"njulf-vrs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var settings = new RenderSettings();
            settings.Raster.VariableRateShadingMode =
                VariableRateShadingMode.Off;
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.That(
                loaded.Raster.VariableRateShadingMode,
                Is.EqualTo(VariableRateShadingMode.Off));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AttachmentExtent_CeilsEachDimensionByDeviceTexelSize()
    {
        Extent2D extent =
            RenderTargetManager.CalculateVariableRateShadingExtent(
                new Extent2D { Width = 1921, Height = 1081 },
                new Extent2D { Width = 16, Height = 16 });

        Assert.Multiple(() =>
        {
            Assert.That(extent.Width, Is.EqualTo(121));
            Assert.That(extent.Height, Is.EqualTo(68));
        });
    }

    [Test]
    public void PushConstants_HaveExactShaderLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Marshal.SizeOf<GPUVariableRateShadingPushConstants>(),
                Is.EqualTo(120));
            Assert.That(
                Marshal.OffsetOf<GPUVariableRateShadingPushConstants>(
                    nameof(GPUVariableRateShadingPushConstants
                        .AttachmentTexelWidth)).ToInt32(),
                Is.EqualTo(80));
            Assert.That(
                Marshal.OffsetOf<GPUVariableRateShadingPushConstants>(
                    nameof(GPUVariableRateShadingPushConstants
                        .NormalDotThreshold)).ToInt32(),
                Is.EqualTo(104));
            Assert.That(
                Marshal.OffsetOf<GPUVariableRateShadingPushConstants>(
                    nameof(GPUVariableRateShadingPushConstants
                        .CoarseRateEncoding)).ToInt32(),
                Is.EqualTo(112));
        });
    }

    [Test]
    public void Graph_ClassifiesBeforeForwardAndPublishesAttachmentDependency()
    {
        ProductionRenderPipelineDeclaration declaration =
            ProductionRenderPipelineDeclaration.Instance;
        string[] order = declaration.PassOrder.ToArray();
        var resources = declaration.CreatePassResourceDeclarations()
            .ToDictionary(candidate => candidate.PassName);
        RenderGraphResourceUsage write = resources["VariableRateShadingPass"]
            .Usages.Single(usage => usage.Resource ==
                RenderGraphResourceId.VariableRateShading);
        RenderGraphResourceUsage read = resources["ForwardPlusPass"]
            .Usages.Single(usage => usage.Resource ==
                RenderGraphResourceId.VariableRateShading);

        Assert.Multiple(() =>
        {
            Assert.That(
                Array.IndexOf(order, "VariableRateShadingPass"),
                Is.LessThan(Array.IndexOf(order, "ForwardPlusPass")));
            Assert.That(write.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(
                write.StageMask & PipelineStageFlags2.ComputeShaderBit,
                Is.Not.Zero);
            Assert.That(write.FinalImageLayout,
                Is.EqualTo(
                    ImageLayout.FragmentShadingRateAttachmentOptimalKhr));
            Assert.That(
                read.StageMask & PipelineStageFlags2
                    .FragmentShadingRateAttachmentBitKhr,
                Is.Not.Zero);
            Assert.That(
                read.AccessMask & AccessFlags2
                    .FragmentShadingRateAttachmentReadBitKhr,
                Is.Not.Zero);
        });
    }

    [Test]
    public void SafetyPolicy_AdmitsOnlyFullyClassifiableFrames()
    {
        VariableRateShadingDecision admitted = Evaluate();

        Assert.Multiple(() =>
        {
            Assert.That(admitted.IsEnabled, Is.True);
            Assert.That(Evaluate(currentMotionAvailable: false)
                .FallbackReason, Is.EqualTo("current-motion-unavailable"));
            Assert.That(Evaluate(maskedMeshletCount: 1)
                .FallbackReason, Is.EqualTo("alpha-tested-geometry-active"));
            Assert.That(Evaluate(foliageClusterCount: 1)
                .FallbackReason, Is.EqualTo("foliage-geometry-active"));
            Assert.That(Evaluate(localLightCount: 17)
                .FallbackReason, Is.EqualTo("dense-local-lighting"));
            Assert.That(Evaluate(debugOrCaptureOutput: true)
                .FallbackReason,
                Is.EqualTo("debug-or-capture-output-active"));
            Assert.That(Evaluate(incompatiblePerPixelOutput: true)
                .FallbackReason,
                Is.EqualTo("per-pixel-forward-output-active"));
        });
    }

    private static void AssertPreset(
        RenderSettings settings,
        RenderQualityPreset preset,
        VariableRateShadingMode expected)
    {
        settings.ApplyQualityPreset(preset);
        Assert.That(
            settings.Raster.VariableRateShadingMode,
            Is.EqualTo(expected),
            preset.ToString());
    }

    private static VariableRateShadingDecision Evaluate(
        bool currentMotionAvailable = true,
        int maskedMeshletCount = 0,
        int foliageClusterCount = 0,
        int localLightCount = 0,
        bool debugOrCaptureOutput = false,
        bool incompatiblePerPixelOutput = false) =>
        VariableRateShadingPolicy.Evaluate(
            VariableRateShadingMode.Auto,
            runtimeSupported: true,
            fullFrame: true,
            debugOrCaptureOutput,
            incompatiblePerPixelOutput,
            currentMotionAvailable,
            maskedMeshletCount,
            foliageClusterCount,
            localLightCount);
}

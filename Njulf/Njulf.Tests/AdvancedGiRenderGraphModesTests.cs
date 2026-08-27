using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiRenderGraphModesTests
{
    [Test]
    public void DisabledModes_AreExactlyTheShippingGraph()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        AdvancedGiRenderGraphModes modes = AdvancedGiRenderGraphModes.Disabled;

        Assert.Multiple(() =>
        {
            Assert.That(modes.HasGpuFeature, Is.False);
            Assert.That(production.CreatePassOrder(modes), Is.EqualTo(production.PassOrder));
            Assert.That(
                production.CreatePassResourceDeclarations(modes)
                    .Select(static declaration => declaration.PassName),
                Is.EqualTo(production.CreatePassResourceDeclarations()
                    .Select(static declaration => declaration.PassName)));
            Assert.That(
                production.CreateResourceDescriptors(
                    Format.D32Sfloat,
                    Format.B8G8R8A8Srgb,
                    modes).Select(static descriptor => descriptor.Id),
                Is.EqualTo(production.CreateResourceDescriptors(
                    Format.D32Sfloat,
                    Format.B8G8R8A8Srgb).Select(static descriptor => descriptor.Id)));
        });
    }

    [Test]
    public void CpuReferenceModes_DoNotCreateGpuGraphWork()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.LegacyPackedReference,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.CpuOracle,
            GiCausticMode.PhotonReference,
            SimpleDdgiNearFieldResidualMode.Reference);

        Assert.That(modes.HasGpuFeature, Is.False);
    }

    [Test]
    public void ExactReceiverFeedback_IsARealExternalTransactionNotFakeGraphPasses()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.Off);

        IReadOnlyList<string> order = production.CreatePassOrder(modes);
        IReadOnlyList<RenderGraphPassResourceDeclaration> declarations =
            production.CreatePassResourceDeclarations(modes);
        IReadOnlyList<RenderGraphResourceDescriptor> resources =
            production.CreateResourceDescriptors(
                Format.D32Sfloat,
                Format.B8G8R8A8Srgb,
                modes);

        Assert.Multiple(() =>
        {
            Assert.That(modes.UsesExactReceiverFeedback, Is.True);
            Assert.That(modes.HasGpuFeature, Is.False);
            Assert.That(order, Is.EqualTo(production.PassOrder));
            Assert.That(declarations.Select(static item => item.PassName),
                Does.Not.Contain("SimpleDdgiReceiverFeedbackResetPass"));
            Assert.That(declarations.Select(static item => item.PassName),
                Does.Not.Contain("SimpleDdgiReceiverFeedbackReducePass"));
            Assert.That(resources.Select(static item => item.Id),
                Does.Not.Contain(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackRecords));
            Assert.That(resources.Select(static item => item.Id),
                Does.Not.Contain(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackSortScratch));
            Assert.That(resources.Select(static item => item.Id),
                Does.Not.Contain(
                    RenderGraphResourceId.SimpleDdgiReceiverFeedbackSummaries));
        });
    }

    [Test]
    public void EffectiveGpuModes_HaveOrderedAndIsolatedPasses()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.ExactCompacted,
            DdgiOpacityMicromapMode.ExtFourStateExperiment,
            SimpleDdgiDirectionalGuidingMode.PerProbeHistogramExperiment,
            GiCausticMode.WorldCacheExperiment,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        var order = production.CreatePassOrder(modes).ToList();
        var declarations = production.CreatePassResourceDeclarations(modes)
            .ToDictionary(static declaration => declaration.PassName);
        var prelude = production
            .CreateExternallyRecordedPassResourceDeclarations(modes)
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(order, Does.Not.Contain("OpacityMicromapBuildPass"),
                "C1 native AS commands must not be represented by a no-op graph pass.");
            Assert.That(prelude.FindIndex(candidate =>
                    candidate.PassName == "OpacityMicromapBuildPass"),
                Is.GreaterThan(prelude.FindIndex(candidate =>
                    candidate.PassName == "AccelerationStructureBlasPass")));
            Assert.That(prelude.FindIndex(candidate =>
                    candidate.PassName == "OpacityMicromapBuildPass"),
                Is.LessThan(prelude.FindIndex(candidate =>
                    candidate.PassName == "AccelerationStructureTlasPass")));
            Assert.That(order,
                Does.Not.Contain("SimpleDdgiReceiverFeedbackResetPass"));
            Assert.That(order,
                Does.Not.Contain("SimpleDdgiReceiverFeedbackReducePass"));
            Assert.That(order.IndexOf(SimpleDdgiGuidingGpuPassNames.Sample),
                Is.GreaterThan(order.IndexOf("SimpleDdgiSchedulePass")));
            Assert.That(order.IndexOf(SimpleDdgiGuidingGpuPassNames.Sample),
                Is.LessThan(order.IndexOf("SimpleDdgiTracePass")));
            Assert.That(order.IndexOf("SimpleDdgiGuidingTrainPass"),
                Is.GreaterThan(order.IndexOf("SimpleDdgiTracePass")));
            Assert.That(order.IndexOf("SimpleDdgiGuidingValidatePass"),
                Is.LessThan(order.IndexOf("SimpleDdgiRelocateClassifyPass")));
            Assert.That(order.IndexOf("GiCausticTaskPass"),
                Is.GreaterThan(order.IndexOf("SimpleDdgiPageFeedbackPass")));
            Assert.That(order.IndexOf("GiCausticResolvePass"),
                Is.LessThan(order.IndexOf("SimpleDdgiNearFieldResidualTracePass")));
            Assert.That(order.IndexOf("SimpleDdgiNearFieldResidualResetPass"),
                Is.LessThan(order.IndexOf("SimpleDdgiNearFieldResidualTracePass")));
            Assert.That(order.IndexOf("SimpleDdgiNearFieldResidualFilterPass1"),
                Is.GreaterThan(order.IndexOf("SimpleDdgiNearFieldResidualFilterPass")));
            Assert.That(order.IndexOf("SimpleDdgiNearFieldResidualCompositePass"),
                Is.LessThan(order.IndexOf("SkyboxPass")));
            Assert.That(declarations["ForwardPlusPass"].Usages.Any(usage =>
                    usage.Resource == RenderGraphResourceId.NearFieldDirectSource &&
                    usage.Access == RenderGraphResourceAccess.Write),
                Is.True);
            Assert.That(declarations["ForwardPlusPass"].Usages.Any(usage =>
                    usage.Resource == RenderGraphResourceId.GiCausticReceiverPayload &&
                    usage.Access == RenderGraphResourceAccess.Write),
                Is.True);
            Assert.That(declarations["SimpleDdgiTracePass"].Usages.Any(usage =>
                    usage.Resource == RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar &&
                    usage.Access == RenderGraphResourceAccess.Read),
                Is.False);
            Assert.That(declarations["SimpleDdgiTracePass"].Usages.Any(usage =>
                    usage.Resource == RenderGraphResourceId.SimpleDdgiRayScratch &&
                    usage.Access == RenderGraphResourceAccess.ReadWrite),
                Is.True);
            Assert.That(declarations[SimpleDdgiGuidingGpuPassNames.Sample].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiScheduler &&
                        usage.Access == RenderGraphResourceAccess.Read),
                Is.True);
            Assert.That(declarations[SimpleDdgiGuidingGpuPassNames.Sample].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiParameters &&
                        usage.Access == RenderGraphResourceAccess.Read),
                Is.True);
            Assert.That(declarations[SimpleDdgiGuidingGpuPassNames.Sample].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiRayScratch &&
                        usage.Access == RenderGraphResourceAccess.ReadWrite),
                Is.True);
            Assert.That(declarations[SimpleDdgiGuidingGpuPassNames.Train].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiScheduler &&
                        usage.Access == RenderGraphResourceAccess.Read),
                Is.True);
            Assert.That(declarations[SimpleDdgiGuidingGpuPassNames.Train].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiParameters &&
                        usage.Access == RenderGraphResourceAccess.Read),
                Is.True);
        });
    }

    [Test]
    public void CausticAndResidualGraphOwnership_RemainsSeparateFromDdgiPublication()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.WorldCacheExperiment,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations(modes)
            .ToDictionary(static declaration => declaration.PassName);

        RenderGraphResourceId[] forbiddenCausticResources =
        [
            RenderGraphResourceId.SimpleDdgiIrradianceAtlas,
            RenderGraphResourceId.SimpleDdgiTransportAtlas,
            RenderGraphResourceId.SimpleDdgiTransportSourceCache,
            RenderGraphResourceId.SimpleDdgiProbeState,
            RenderGraphResourceId.SimpleDdgiUpdateQueue,
            RenderGraphResourceId.SimpleDdgiReceiverProbes
        ];

        foreach (string pass in new[]
                 {
                     "GiCausticTaskPass", "GiCausticTracePass",
                     "GiCausticCacheBuildPass", "GiCausticResolvePass"
                 })
        {
            Assert.That(declarations[pass].Usages.Any(usage =>
                forbiddenCausticResources.Contains(usage.Resource)), Is.False, pass);
        }

        Assert.Multiple(() =>
        {
            Assert.That(declarations["SimpleDdgiNearFieldResidualTracePass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.SceneColor), Is.False);
            Assert.That(declarations["SimpleDdgiNearFieldResidualTracePass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.NearFieldDirectSource &&
                    usage.Access == RenderGraphResourceAccess.Read), Is.True);
            Assert.That(declarations["SimpleDdgiNearFieldResidualCompositePass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.SceneColor &&
                    usage.Access == RenderGraphResourceAccess.ReadWrite), Is.True);
        });
    }

    [Test]
    public void NearFieldTemporalDeclarations_SelectDistinctPhysicalHistoryBanks()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations(modes)
            .ToDictionary(static declaration => declaration.PassName);

        RenderGraphResourceUsage[] temporal =
            declarations["SimpleDdgiNearFieldResidualTemporalPass"].Usages;
        RenderGraphResourceUsage[] reset =
            declarations["SimpleDdgiNearFieldResidualResetPass"].Usages;
        RenderGraphResourceUsage[] filter =
            declarations["SimpleDdgiNearFieldResidualFilterPass"].Usages;
        RenderGraphResourceUsage[] filter1 =
            declarations["SimpleDdgiNearFieldResidualFilterPass1"].Usages;
        RenderGraphResourceUsage[] frequency =
            declarations["SimpleDdgiNearFieldResidualFrequencySeparationPass"].Usages;
        RenderGraphResourceUsage[] composite =
            declarations["SimpleDdgiNearFieldResidualCompositePass"].Usages;

        RenderGraphResourceId[] temporalHistoryResources =
        [
            RenderGraphResourceId.NearFieldResidualHistory,
            RenderGraphResourceId.NearFieldResidualMoments,
            RenderGraphResourceId.NearFieldResidualValidity,
            RenderGraphResourceId.NearFieldResidualHistoryMetadata,
            RenderGraphResourceId.NearFieldResidualHistoryNormals
        ];

        foreach (RenderGraphResourceId resource in temporalHistoryResources)
        {
            Assert.That(temporal.Any(usage =>
                usage.Resource == resource &&
                usage.Access == RenderGraphResourceAccess.Read &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Previous), Is.True, resource.ToString());
            Assert.That(temporal.Any(usage =>
                usage.Resource == resource &&
                usage.Access == RenderGraphResourceAccess.Write &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Current), Is.True, resource.ToString());
            Assert.That(temporal.Any(usage =>
                usage.Resource == resource &&
                usage.Access == RenderGraphResourceAccess.ReadWrite), Is.False, resource.ToString());
        }

        Assert.Multiple(() =>
        {
            Assert.That(reset.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualRaw &&
                usage.Access == RenderGraphResourceAccess.Write &&
                (usage.StageMask & PipelineStageFlags2.TransferBit) != 0 &&
                (usage.AccessMask & AccessFlags2.TransferWriteBit) != 0), Is.True);
            Assert.That(reset.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualHistory &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Current &&
                (usage.StageMask & PipelineStageFlags2.TransferBit) != 0), Is.True);
            Assert.That(reset.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.All &&
                (usage.AccessMask & AccessFlags2.TransferWriteBit) != 0), Is.True);
            Assert.That(filter.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Write &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank0), Is.True);
            Assert.That(filter1.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Read &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank0), Is.True);
            Assert.That(filter1.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Write &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank1), Is.True);
            Assert.That(frequency.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Read &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank1), Is.True);
            Assert.That(composite.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualRaw &&
                usage.Access == RenderGraphResourceAccess.Read), Is.True);
        });
    }

    [Test]
    public void NearFieldGraph_AcceptsFixedAndAdaptiveProfiles()
    {
        var unbound = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive);
        var quarter = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            new AdvancedGiNearFieldGraphProfile(
                ResolutionScale: 0.25f,
                SourceFormat: SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
                FilterIterationCount: 2));
        var unsupported = quarter with
        {
            NearFieldProfile = new AdvancedGiNearFieldGraphProfile(
                ResolutionScale: 0.3f,
                SourceFormat:
                    SimpleDdgiNearFieldResidualFormat.R16G16B16A16Sfloat,
                FilterIterationCount: 2)
        };
        var supported = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        var autoQuarter = quarter with
        {
            NearFieldResidual = SimpleDdgiNearFieldResidualMode.AutoQualified
        };

        Assert.Multiple(() =>
        {
            Assert.That(unbound.UsesNearFieldHiZResidual, Is.False);
            Assert.That(unsupported.UsesNearFieldHiZResidual, Is.False);
            Assert.That(quarter.UsesNearFieldHiZResidual, Is.True);
            Assert.That(supported.UsesNearFieldHiZResidual, Is.True);
            Assert.That(autoQuarter.UsesNearFieldHiZResidual, Is.True);
            Assert.That(autoQuarter.NearFieldProfile.TraceSizePolicy,
                Is.EqualTo(RenderGraphResourceSizePolicy.QuarterResolution));
        });
    }

    [Test]
    public void AutoQualifiedNearFieldGraph_DeclaresTheExactMeasuredScale()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.AutoQualified,
            AdvancedGiNearFieldGraphProfile.From(
                SimpleDdgiNearFieldResidualProfile.EighthResolutionMemoryBound));
        var descriptors = ProductionRenderPipelineDeclaration.Instance
            .CreateResourceDescriptors(
                Format.D32Sfloat,
                Format.B8G8R8A8Srgb,
                modes)
            .ToDictionary(static descriptor => descriptor.Id);
        var trace = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations(modes)
            .Single(static declaration => declaration.PassName ==
                "SimpleDdgiNearFieldResidualTracePass");

        Assert.Multiple(() =>
        {
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualRaw]
                .SizePolicy, Is.EqualTo(RenderGraphResourceSizePolicy.EighthResolution));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualHistory]
                .SizePolicy, Is.EqualTo(RenderGraphResourceSizePolicy.EighthResolution));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldReceiverPayload]
                .SizePolicy, Is.EqualTo(RenderGraphResourceSizePolicy.EighthResolution));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldDirectSource]
                .SizePolicy, Is.EqualTo(RenderGraphResourceSizePolicy.EighthResolution));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldTraceRasterDepth]
                .SizePolicy, Is.EqualTo(RenderGraphResourceSizePolicy.EighthResolution));
            Assert.That(descriptors.ContainsKey(
                RenderGraphResourceId.NearFieldResidualTraceFrameConstants), Is.True);
            Assert.That(trace.Usages.Any(static usage => usage.Resource ==
                RenderGraphResourceId.NearFieldResidualTraceFrameConstants &&
                usage.Access == RenderGraphResourceAccess.Read), Is.True);
        });
    }

    [Test]
    public void NearFieldGraph_ZeroFilterIterations_OmitsScratchAndReadsTemporalOutput()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference with
            {
                FilterIterationCount = 0
            });
        var production = ProductionRenderPipelineDeclaration.Instance;
        var order = production.CreatePassOrder(modes);
        var declarations = production.CreatePassResourceDeclarations(modes)
            .ToDictionary(static declaration => declaration.PassName);
        var descriptors = production.CreateResourceDescriptors(
                Format.D32Sfloat,
                Format.B8G8R8A8Srgb,
                modes)
            .ToDictionary(static descriptor => descriptor.Id);

        Assert.Multiple(() =>
        {
            Assert.That(modes.UsesNearFieldFiltering, Is.False);
            Assert.That(order.Any(static pass => pass.StartsWith(
                "SimpleDdgiNearFieldResidualFilterPass", System.StringComparison.Ordinal)),
                Is.False);
            Assert.That(descriptors.ContainsKey(
                RenderGraphResourceId.NearFieldResidualFilterScratch), Is.False);
            Assert.That(declarations["SimpleDdgiNearFieldResidualResetPass"].Usages.Any(
                usage => usage.Resource ==
                    RenderGraphResourceId.NearFieldResidualFilterScratch), Is.False);
            Assert.That(declarations["SimpleDdgiNearFieldResidualFrequencySeparationPass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.NearFieldResidualHistory &&
                    usage.Access == RenderGraphResourceAccess.Read &&
                    usage.HistoryBinding == RenderGraphHistoryBindingSelection.Current), Is.True);
            Assert.That(declarations["SimpleDdgiNearFieldResidualCompositePass"].Usages.Any(
                usage => usage.Resource == RenderGraphResourceId.NearFieldResidualRaw &&
                    usage.Access == RenderGraphResourceAccess.Read), Is.True);
        });
    }

    [Test]
    public void NearFieldGraph_UsesExactConfiguredPingPongCount()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference with
            {
                FilterIterationCount = 3
            });
        var production = ProductionRenderPipelineDeclaration.Instance;
        var declarations = production.CreatePassResourceDeclarations(modes)
            .ToDictionary(static declaration => declaration.PassName);
        RenderGraphResourceUsage[] thirdFilter =
            declarations["SimpleDdgiNearFieldResidualFilterPass2"].Usages;
        RenderGraphResourceUsage[] frequency =
            declarations["SimpleDdgiNearFieldResidualFrequencySeparationPass"].Usages;

        Assert.Multiple(() =>
        {
            Assert.That(production.CreatePassOrder(modes).Count(pass =>
                pass.StartsWith("SimpleDdgiNearFieldResidualFilterPass",
                    System.StringComparison.Ordinal)), Is.EqualTo(3));
            Assert.That(thirdFilter.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Read &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank1), Is.True);
            Assert.That(thirdFilter.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Write &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank0), Is.True);
            Assert.That(frequency.Any(usage =>
                usage.Resource == RenderGraphResourceId.NearFieldResidualFilterScratch &&
                usage.Access == RenderGraphResourceAccess.Read &&
                usage.HistoryBinding == RenderGraphHistoryBindingSelection.Bank0), Is.True);
        });
    }

    [Test]
    public void AdvancedResourceDescriptors_ArePresentOnlyForActiveFeatureFamilies()
    {
        var modes = new AdvancedGiRenderGraphModes(
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.WorldCacheExperiment,
            SimpleDdgiNearFieldResidualMode.HiZAdaptive,
            AdvancedGiNearFieldGraphProfile.HalfResolutionReference);
        var descriptors = ProductionRenderPipelineDeclaration.Instance
            .CreateResourceDescriptors(Format.D32Sfloat, Format.B8G8R8A8Srgb, modes)
            .ToDictionary(static descriptor => descriptor.Id);

        Assert.Multiple(() =>
        {
            Assert.That(descriptors.ContainsKey(RenderGraphResourceId.GiCausticCache), Is.True);
            Assert.That(descriptors.ContainsKey(RenderGraphResourceId.NearFieldDirectSource), Is.True);
            Assert.That(descriptors.ContainsKey(RenderGraphResourceId.SimpleDdgiGuidingDistributions), Is.False);
            Assert.That(descriptors.ContainsKey(RenderGraphResourceId.SimpleDdgiGuidingDirectionPayloadSidecar), Is.False);
            Assert.That(descriptors[RenderGraphResourceId.NearFieldDirectSource].Persistent, Is.False);
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualHistory].Persistent, Is.True);
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualFilterScratch].Lifetime,
                Is.EqualTo(RenderGraphResourceLifetime.Transient));
            Assert.That(descriptors.ContainsKey(
                RenderGraphResourceId.NearFieldResidualHitMetadata), Is.False,
                "V13 has no separate trace metadata allocation.");
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualHistory].Kind,
                Is.EqualTo(RenderGraphResourceKind.ImageChain));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualHistoryMetadata].Kind,
                Is.EqualTo(RenderGraphResourceKind.BufferSet));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualHistoryNormals].Kind,
                Is.EqualTo(RenderGraphResourceKind.ImageChain));
            Assert.That(descriptors[RenderGraphResourceId.NearFieldResidualFilterScratch].Kind,
                Is.EqualTo(RenderGraphResourceKind.ImageChain));
        });
    }
}

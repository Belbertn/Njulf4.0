using System;
using System.IO;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverFeedbackVulkanRuntimeTests
{
    [Test]
    public void GraphicsVariants_ExplicitModeBypassesPromotionGateButAutoRequiresIt()
    {
        var settings = new GlobalIlluminationSettings
        {
            Enabled = true,
            UseDdgi = true,
            Mode = GlobalIlluminationMode.Ddgi,
            SimpleDdgiReceiverFeedbackMode =
                SimpleDdgiReceiverFeedbackMode.ExactCompacted
        };
        var passed = new AdvancedGiPrerequisiteGateResult(
            Passed: true,
            QualificationId: new string('a', 64),
            FailureDetail: "valid");
        var rejected = AdvancedGiPrerequisiteGateResult.Missing(
            "prerequisite-missing");

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        passed),
                Is.True);
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        rejected),
                Is.True);

            settings.SimpleDdgiReceiverFeedbackMode =
                SimpleDdgiReceiverFeedbackMode.AutoQualified;
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        rejected),
                Is.False);
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        passed),
                Is.True);

            settings.UseDdgi = false;
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        passed),
                Is.False);
            settings.UseDdgi = true;
            settings.SimpleDdgiReceiverFeedbackMode =
                SimpleDdgiReceiverFeedbackMode.Off;
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator
                    .ShouldCreateGraphicsPipelines(
                        settings,
                        passed),
                Is.False);
        });
    }

    [Test]
    public void ProductionWorkload_AccountsForEveryEnabledProducerClass()
    {
        var settings = new RenderSettings();
        settings.Fog.Enabled = true;
        settings.Particles.Enabled = true;
        settings.Particles.MaxParticles = 123;
        settings.GlobalIllumination.SimpleDdgiParticlesEnabled = true;
        settings.Reflections.Enabled = true;
        settings.Reflections.CaptureIncludesDdgi = true;
        settings.Reflections.ProbeResolution = 64;
        settings.Reflections.MaxProbeCapturesPerFrame = 2;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = 3;

        SimpleDdgiReceiverFeedbackProductionWorkload workload =
            SimpleDdgiReceiverFeedbackCoordinator.CompileProductionWorkload(
                settings,
                new Extent2D(17u, 9u),
                screenTileCount: 6UL);

        Assert.Multiple(() =>
        {
            Assert.That(workload.SourceScreenTileCount, Is.EqualTo(6UL));
            Assert.That(workload.FogWorkgroupCount, Is.EqualTo(6UL));
            Assert.That(workload.MaximumParticleCount, Is.EqualTo(123u));
            Assert.That(workload.ReflectionCaptureTileCount, Is.EqualTo(216UL));
            Assert.That(workload.MaximumTransparentLayersPerTile,
                Is.EqualTo(SimpleDdgiReceiverFeedbackProductionWorkload
                    .DefaultMaximumTransparentLayersPerTile));
        });
    }

    [Test]
    public void ExactCaptureProducerContract_RequiresFrozen48ByteAbiAndExplicitWriteVisibility()
    {
        SimpleDdgiReceiverFeedbackCaptureProducerContract valid = CreateValidContract();
        SimpleDdgiReceiverFeedbackCaptureProducerContract legacyStride = valid with
        {
            CandidateRecordStrideBytes = 16u
        };
        SimpleDdgiReceiverFeedbackCaptureProducerContract readOnly = valid with
        {
            ProducerWriteAccessMask = AccessFlags2.ShaderStorageReadBit
        };
        SimpleDdgiReceiverFeedbackCaptureProducerContract rangeOverflow = valid with
        {
            CandidateBufferDescriptorBytes = 255UL * sizeof(uint)
        };
        SimpleDdgiReceiverFeedbackCaptureProducerContract unknownProducer = valid with
        {
            RequiredProducerMask = 1u << 7
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidate(16u, out string validReason), Is.True, validReason);
            Assert.That(legacyStride.TryValidate(16u, out string legacyReason), Is.False);
            Assert.That(legacyReason, Does.Contain("48-bytes"));
            Assert.That(readOnly.TryValidate(16u, out string readReason), Is.False);
            Assert.That(readReason, Does.Contain("does-not-name-a-write"));
            Assert.That(rangeOverflow.TryValidate(16u, out string rangeReason), Is.False);
            Assert.That(rangeReason, Does.Contain("range-exceeds"));
            Assert.That(unknownProducer.TryValidate(16u, out string maskReason), Is.False);
            Assert.That(maskReason, Does.Contain("required-mask"));
        });
    }

    [Test]
    public void RequiredProducerSet_IsSceneDerivedAndRuntimeOwnedCoverageIsExplicit()
    {
        var opaqueOnly = new SceneRenderingData();
        uint opaqueMask = ForwardPlusPass.ResolveRequiredReceiverFeedbackProducerMask(
            opaqueOnly);

        var layered = new SceneRenderingData
        {
            MaskedMeshletCount = 1,
            FoliageClusterCount = 1,
            TransparentObjectCount = 1,
            TransparentMeshletCount = 2,
            TransparentReceiveGlobalIllumination = true,
            ParticleDdgiSampleCount = 3,
            FogEnabled = true,
            ReflectionProbeCapturesQueued = 2,
            ReflectionProbeCapturesCompleted = 1,
            SimpleDdgiRefinement = new SimpleDdgiRefinementBrickDiagnostics(
                true, 1, 1, 0, 1, 8, 0, false, "active")
        };
        uint layeredMask = ForwardPlusPass.ResolveRequiredReceiverFeedbackProducerMask(
            layered);

        Assert.Multiple(() =>
        {
            Assert.That(opaqueMask,
                Is.EqualTo(SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                    SimpleDdgiReceiverFeedbackProducer.OpaqueForward)));
            Assert.That(
                SimpleDdgiReceiverFeedbackVulkanRuntime.OwnedProducerMask,
                Is.EqualTo(
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.OpaqueForward) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.AlphaMaskOrFoliage) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.Particles) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.Fog) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer.ReflectionCapture) |
                    SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                        SimpleDdgiReceiverFeedbackProducer
                            .RefinementOrBaseFallback)));
            Assert.That(layeredMask,
                Is.EqualTo(SimpleDdgiReceiverFeedbackCaptureSourceAbi.KnownProducerMask));
            Assert.That(layeredMask &
                        ~SimpleDdgiReceiverFeedbackVulkanRuntime.OwnedProducerMask,
                Is.Zero);
        });

        uint reflectionDisabledMask =
            ForwardPlusPass.ResolveRequiredReceiverFeedbackProducerMask(
                layered,
                reflectionCaptureFeedbackEnabled: false);
        Assert.That(
            reflectionDisabledMask &
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                SimpleDdgiReceiverFeedbackProducer.ReflectionCapture),
            Is.Zero);
    }

    [Test]
    public void DecalOnlyDraws_DoNotClaimTheTransparentExactFeedbackProducer()
    {
        var decalOnly = new SceneRenderingData
        {
            TransparentObjectCount = 0,
            TransparentMeshletCount = 481,
            GeometryDecalMeshletCount = 481,
            TransparentReceiveGlobalIllumination = true,
            DecalReceiveGlobalIllumination = true
        };

        uint mask = ForwardPlusPass.ResolveRequiredReceiverFeedbackProducerMask(
            decalOnly,
            fogEnabled: false,
            reflectionCaptureFeedbackEnabled: false);
        uint transparentBit =
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GetProducerBit(
                SimpleDdgiReceiverFeedbackProducer.TransparentWeightedOit);

        Assert.That(mask & transparentBit, Is.Zero);
    }

    [Test]
    public void DecalReceiverCache_IsRestrictedToDepthBackedDecalOnlyDraws()
    {
        var decalOnly = new SceneRenderingData
        {
            TransparentObjectCount = 0,
            TransparentMeshletCount = 481,
            GeometryDecalMeshletCount = 481,
            DecalReceiveGlobalIllumination = true
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                TransparentForwardPass.ShouldUseGeometryDecalOverlay(
                    decalOnly,
                    exactFeedback: false,
                    rayVariant: false,
                    overlayPipelineAvailable: true),
                Is.True);
            Assert.That(
                TransparentForwardPass.ShouldUseGeometryDecalOverlay(
                    decalOnly,
                    exactFeedback: true,
                    rayVariant: false,
                    overlayPipelineAvailable: true),
                Is.False);
            Assert.That(
                TransparentForwardPass.ShouldUseGeometryDecalOverlay(
                    decalOnly,
                    exactFeedback: false,
                    rayVariant: true,
                    overlayPipelineAvailable: true),
                Is.False);
        });

        decalOnly.TransparentObjectCount = 1;
        Assert.That(
            TransparentForwardPass.ShouldUseGeometryDecalOverlay(
                decalOnly,
                exactFeedback: false,
                rayVariant: false,
                overlayPipelineAvailable: true),
            Is.False);
    }

    [Test]
    public void DirectionalOnlyThinGlass_RequiresAnAllGlassProductionDraw()
    {
        var glassOnly = new SceneRenderingData
        {
            TransparentObjectCount = 23,
            ThinGlassObjectCount = 23,
            TransparentMeshletCount = 481,
            ThinGlassMeshletCount = 481
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                TransparentForwardPass.ShouldUseDirectionalOnlyThinGlass(
                    glassOnly,
                    exactFeedback: false,
                    rayVariant: false,
                    pipelineAvailable: true),
                Is.True);
            Assert.That(
                TransparentForwardPass.ShouldUseDirectionalOnlyThinGlass(
                    glassOnly,
                    exactFeedback: true,
                    rayVariant: false,
                    pipelineAvailable: true),
                Is.False);
            Assert.That(
                TransparentForwardPass.ShouldUseDirectionalOnlyThinGlass(
                    glassOnly,
                    exactFeedback: false,
                    rayVariant: true,
                    pipelineAvailable: true),
                Is.False);
        });

        glassOnly.ThinGlassMeshletCount--;
        Assert.That(
            TransparentForwardPass.ShouldUseDirectionalOnlyThinGlass(
                glassOnly,
                exactFeedback: false,
                rayVariant: false,
                pipelineAvailable: true),
            Is.False);
    }

    [Test]
    public void AllThinGlassScene_UsesRayVariantForHybridSceneReflections()
    {
        var glassOnly = new SceneRenderingData
        {
            TransparentObjectCount = 23,
            ThinGlassObjectCount = 23,
            TransparentMeshletCount = 481,
            ThinGlassMeshletCount = 481,
            TransparentReflectionReceiverObjectCount = 23,
            TransparentReflectionReceiverMeshletCount = 481,
            TransparentSampleReflections = true,
            OpaqueSceneColorSnapshotAvailable = true,
            EffectiveReflectionMode = ReflectionMode.HybridRayQuery,
            TransparentSceneReflectionRayTaskBudget = 65_536
        };

        Assert.Multiple(() =>
        {
            Assert.That(
                TransparentForwardPass.RequiresSceneReflectionRayVariant(
                    glassOnly),
                Is.True);
            Assert.That(
                TransparentForwardPass.ShouldUseDirectionalOnlyThinGlass(
                    glassOnly,
                    exactFeedback: false,
                    rayVariant: true,
                    pipelineAvailable: true),
                Is.False);
        });

        glassOnly.OpaqueSceneColorSnapshotAvailable = false;
        Assert.That(
            TransparentForwardPass.RequiresSceneReflectionRayVariant(glassOnly),
            Is.False);
    }

    [Test]
    public void ExactCaptureProducerContract_RejectsUnavailableReservedOrOversizedSources()
    {
        SimpleDdgiReceiverFeedbackCaptureProducerContract unavailable =
            SimpleDdgiReceiverFeedbackCaptureProducerContract.Unavailable;
        SimpleDdgiReceiverFeedbackCaptureProducerContract reserved =
            CreateValidContract() with
            {
                CandidateBufferBindlessIndex =
                SimpleDdgiReceiverFeedbackGpuSortAbi.RecordBindlessSlot
            };
        SimpleDdgiReceiverFeedbackCaptureProducerContract oversized =
            CreateValidContract() with { CandidateRecordCount = 15u };

        Assert.Multiple(() =>
        {
            Assert.That(unavailable.TryValidate(16u, out string unavailableReason), Is.False);
            Assert.That(unavailableReason,
                Is.EqualTo("exact-capture-producer-unavailable"));
            Assert.That(reserved.TryValidate(16u, out string reservedReason), Is.False);
            Assert.That(reservedReason, Does.Contain("buffer-or-static-bindless-index-invalid"));
            Assert.That(oversized.TryValidate(16u, out string capacityReason), Is.False);
            Assert.That(capacityReason, Does.Contain("does-not-match-admitted-capacity"));
        });
    }

    [Test]
    public void DisabledSchedulerBinding_ExposesOnlyTheFixedSummarySlot()
    {
        SimpleDdgiReceiverFeedbackGpuSchedulingBinding binding =
            SimpleDdgiReceiverFeedbackGpuSchedulingBinding.Disabled("not-published");

        Assert.Multiple(() =>
        {
            Assert.That(binding.UseFeedback, Is.False);
            Assert.That(binding.SummaryBufferBindlessSlot,
                Is.EqualTo(SimpleDdgiReceiverFeedbackGpuSortAbi.SummaryBindlessSlot));
            Assert.That(binding.SummaryBankOffsetBytes, Is.Zero);
            Assert.That(binding.Validation.Reason,
                Is.EqualTo(GiExperimentFallbackReason.ResourceIncomplete));
        });
    }

    [Test]
    public void PostUploadReconciliation_DefersOnlyAfterRecordingSummaryBankRead()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator.ShouldReconcileAfterUpload(
                    currentCommandBufferReferencesSummaryBank: false),
                Is.True,
                "Initial activation and rejected bindings may reconcile immediately.");
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator.ShouldReconcileAfterUpload(
                    currentCommandBufferReferencesSummaryBank: true),
                Is.False,
                "A recorded read must keep its allocation alive through submission.");
            Assert.That(
                SimpleDdgiReceiverFeedbackCoordinator.ShouldReconcileAfterUpload(
                    currentCommandBufferReferencesSummaryBank: false),
                Is.True,
                "The following frame may retry the deferred transition.");
        });
    }

    [Test]
    public void VulkanRuntimeSources_UseFullStageOrderingAndDoNotReferenceLegacyGatherEntries()
    {
        string runtime = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiReceiverFeedbackVulkanRuntime.cs");
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiReceiverFeedbackGpuPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("ExactCaptureProducerUnavailable"));
            Assert.That(runtime, Does.Contain("TryReadCompletedFrame"));
            Assert.That(runtime, Does.Contain("CompleteGpuCapture"));
            Assert.That(runtime, Does.Contain("AcquireForScheduling"));
            Assert.That(runtime, Does.Contain("TryRecordPendingOwnedReduction"));
            Assert.That(runtime, Does.Contain("RequiredProducerCoverageIncomplete"));
            Assert.That(runtime, Does.Contain("IsPendingOwnedProducerRequired"));
            Assert.That(runtime, Does.Not.Contain("SimpleDdgiReceiverGather"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_reset.comp.spv"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_capture.comp.spv"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_radix_histogram.comp.spv"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_radix_prefix.comp.spv"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_radix_scatter.comp.spv"));
            Assert.That(pass, Does.Contain("ddgi_receiver_feedback_reduce.comp.spv"));
            Assert.That(pass, Does.Contain("RecordB1StorageBarrier"));
            Assert.That(pass, Does.Contain("RecordProducerToCaptureBarrier"));
            Assert.That(pass, Does.Not.Contain("SimpleDdgiReceiverGather"));
        });
    }

    [Test]
    public void TransparentProducerSources_UseExactVariantsAndLateCompletionWitness()
    {
        string shaderProject = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string shader = ReadRepoText("Njulf.Shaders", "forward.frag");
        string surfaceProducer = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_receiver_feedback_surface_producer.glsl");
        string sortedPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "TransparentForwardPass.cs");
        string weightedPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "WeightedTransparentPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject,
                Does.Contain("forward_transparent_ddgi_b1.frag"));
            Assert.That(shaderProject,
                Does.Contain("forward_weighted_oit_ddgi_b1.frag"));
            Assert.That(shader,
                Does.Contain("EmitSimpleDdgiTransparentReceiverFeedback"));
            Assert.That(shader,
                Does.Contain("ddgi_receiver_feedback_surface_producer.glsl"));
            Assert.That(surfaceProducer,
                Does.Contain("subgroupExclusiveAdd(localOwnerCount)"));
            Assert.That(shader,
                Does.Contain("SimpleDdgiTransparentCompositeWeight"));
            Assert.That(sortedPass,
                Does.Contain("TransparentReceiverFeedbackPipeline"));
            Assert.That(weightedPass,
                Does.Contain("WeightedOitReceiverFeedbackPipeline"));
            Assert.That(sortedPass,
                Does.Contain("TryRecordOwnedProducerCompletion"));
            Assert.That(weightedPass,
                Does.Contain("TryRecordOwnedProducerCompletion"));
        });
    }

    [Test]
    public void FogProducerSources_UseWorkgroupRepresentativeAndSolidAngleMass()
    {
        string shaderProject = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string shader = ReadRepoText("Njulf.Shaders", "fog.comp");
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "FogPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject, Does.Contain("fog_b1.comp"));
            Assert.That(shader,
                Does.Contain("SimpleDdgiFogRepresentativeLane"));
            Assert.That(shader,
                Does.Contain("SimpleDdgiFogWorkgroupSolidAngle"));
            Assert.That(shader,
                Does.Contain("EmitSimpleDdgiFogReceiverFeedback"));
            Assert.That(pass, Does.Contain("fog_b1.comp.spv"));
            Assert.That(pass,
                Does.Contain("TryRecordOwnedProducerCompletion"));
        });
    }

    [Test]
    public void ParticleProducerSources_UseExactFootprintCoverageAndLateCompletion()
    {
        string shaderProject = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string shader = ReadRepoText("Njulf.Shaders", "particle.vert");
        string pipeline = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "ParticlePipeline.cs");
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ParticlePass.cs");
        string runtime = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiReceiverFeedbackVulkanRuntime.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject, Does.Contain("particle_b1.vert"));
            Assert.That(shader,
                Does.Contain("SimpleDdgiParticleProjectedAreaPixels"));
            Assert.That(shader,
                Does.Contain("SimpleDdgiParticleMeanCoverage"));
            Assert.That(shader,
                Does.Contain("EmitSimpleDdgiParticleReceiverFeedback"));
            Assert.That(shader,
                Does.Contain("subgroupExclusiveAdd(localOwnerCount)"));
            Assert.That(pipeline, Does.Contain("particle_b1.vert.spv"));
            Assert.That(pipeline,
                Does.Contain("ReceiverFeedbackPipelinesAvailable"));
            Assert.That(pass,
                Does.Contain("SimpleDdgiReceiverFeedbackProducer.Particles"));
            Assert.That(pass,
                Does.Contain("TryRecordOwnedProducerCompletion"));
            Assert.That(runtime,
                Does.Contain("PipelineStageFlags2.VertexShaderBit"));
        });
    }

    [Test]
    public void AlphaAndFoliageProducer_UsesCoveredLiveGatherAndExactPipelines()
    {
        string shaderProject = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");
        string forwardShader = ReadRepoText(
            "Njulf.Shaders",
            "forward.frag");
        string foliageShader = ReadRepoText(
            "Njulf.Shaders",
            "foliage_forward.frag");
        string surfaceProducer = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_receiver_feedback_surface_producer.glsl");
        string forwardPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");
        string meshPipeline = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "MeshPipeline.cs");
        string foliagePipeline = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "FoliagePipeline.cs");

        Assert.Multiple(() =>
        {
            Assert.That(shaderProject,
                Does.Contain("forward_opaque_ddgi_b1.frag"));
            Assert.That(shaderProject,
                Does.Contain("foliage_forward_ddgi_b1.frag"));
            Assert.That(shaderProject,
                Does.Contain("foliage_grass_b1.mesh"));
            Assert.That(shaderProject,
                Does.Contain("foliage_mesh_b1.mesh"));
            Assert.That(shaderProject,
                Does.Contain("NjulfReceiverFeedbackGraphicsCompileOptions"));
            Assert.That(shaderProject,
                Does.Contain("-DNJULF_DDGI_DETAILED_COUNTERS=0"));
            Assert.That(forwardShader,
                Does.Contain("EmitSimpleDdgiAlphaMaskReceiverFeedback"));
            Assert.That(forwardShader,
                Does.Contain("materialCoverage.Alpha"));
            Assert.That(foliageShader,
                Does.Contain("FoliageCoverageSurvives"));
            Assert.That(foliageShader,
                Does.Contain("exactGather = SampleSimpleDdgiGather"));
            Assert.That(foliageShader,
                Does.Contain("survivingCoverage"));
            Assert.That(surfaceProducer,
                Does.Contain("subgroupExclusiveAdd(localOwnerCount)"));
            Assert.That(surfaceProducer,
                Does.Contain("umulExtended"));
            Assert.That(surfaceProducer,
                Does.Not.Contain("0xffffffffu /"));
            Assert.That(meshPipeline,
                Does.Contain("AlphaMaskReceiverFeedbackPipelinesAvailable"));
            Assert.That(meshPipeline,
                Does.Contain("receiver-feedback-pipeline-creation-failed"));
            Assert.That(foliagePipeline,
                Does.Contain("ReceiverFeedbackPipelinesAvailable"));
            Assert.That(forwardPass,
                Does.Contain("AlphaMaskOrFoliage"));
            Assert.That(forwardPass,
                Does.Contain("receiver-feedback-alpha-foliage-completion-failed"));
            Assert.That(forwardPass,
                Does.Contain("receiverGatherRequired = receiverCacheEligible ||"));
            Assert.That(
                typeof(ForwardPlusPass).GetField(
                    "_simpleDdgiReceiverFeedbackRuntime",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)?.FieldType,
                Is.EqualTo(typeof(ISimpleDdgiReceiverFeedbackCapture)));
            Assert.That(forwardPass,
                Does.Contain("receiverCacheEligible && receiverGatherRecorded"));
        });
    }

    [Test]
    public void ReflectionCaptureProducer_UsesSolidAngleLayerNamespaceAndBatchCompletion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ForwardPlusPass.TryComputeReflectionFeedbackTileNamespace(
                    cubemapArrayLayer: 7,
                    resolution: 128u,
                    out uint tileBase,
                    out string validReason),
                Is.True,
                validReason);
            Assert.That(tileBase, Is.EqualTo(7u * 11u * 11u));
            Assert.That(
                ForwardPlusPass.TryComputeReflectionFeedbackTileNamespace(
                    0,
                    uint.MaxValue,
                    out _,
                    out string overflowReason),
                Is.False);
            Assert.That(overflowReason, Does.Contain("namespace-overflow"));
            Assert.That(
                ForwardPlusPass.TryComputeReflectionFeedbackTileNamespace(
                    -1,
                    128u,
                    out _,
                    out string layerReason),
                Is.False);
            Assert.That(layerReason, Does.Contain("layer-out-of-range"));
        });

        string surfaceProducer = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_receiver_feedback_surface_producer.glsl");
        string forwardShader = ReadRepoText(
            "Njulf.Shaders",
            "forward.frag");
        string foliageShader = ReadRepoText(
            "Njulf.Shaders",
            "foliage_forward.frag");
        string capturePass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ReflectionProbeCapturePass.cs");
        string forwardPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "ForwardPlusPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(surfaceProducer,
                Does.Contain("SimpleDdgiCubemapTexelSolidAngle"));
            Assert.That(surfaceProducer,
                Does.Contain("representativePixel"));
            Assert.That(surfaceProducer,
                Does.Contain("coveredTilePixelCount"));
            Assert.That(surfaceProducer,
                Does.Contain("SimpleDdgiTryComputeCubemapTileNamespace"));
            Assert.That(forwardShader,
                Does.Contain("reflectionFeedback ? 5u : 1u"));
            Assert.That(forwardShader,
                Does.Contain("exactFeedbackRoughDdgiOwnership"));
            Assert.That(foliageShader,
                Does.Contain("reflectionFeedback ? 5u : 1u"));
            Assert.That(capturePass,
                Does.Contain("CompleteCaptureBatch"));
            Assert.That(capturePass,
                Does.Contain("recordedFaceCount++"));
            Assert.That(forwardPass,
                Does.Contain("TryRecordOwnedProducerCompletion"));
            Assert.That(forwardPass,
                Does.Contain("ReflectionCapture"));
        });
    }

    [Test]
    public void RefinementProducer_RoutesLiveOwnershipExactlyOnceAndClosesAtLateBoundary()
    {
        string gather = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_shared.glsl");
        string cache = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_receiver_cache.comp");
        string surface = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_receiver_feedback_surface_producer.glsl");
        string particle = ReadRepoText(
            "Njulf.Shaders",
            "particle.vert");
        string fog = ReadRepoText("Njulf.Shaders", "fog.comp");
        string coordinator = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiReceiverFeedbackCoordinator.cs");

        Assert.Multiple(() =>
        {
            Assert.That(gather,
                Does.Contain("foundContainingRefinement"));
            Assert.That(gather,
                Does.Contain("exactFeedbackRefinementOrBaseFallback"));
            Assert.That(gather,
                Does.Contain("innerAvailableMass / availableMass"));
            Assert.That(gather,
                Does.Contain("outerAvailableMass / availableMass"));
            Assert.That(cache,
                Does.Contain("exactFeedbackRefinementOrBaseFallback != 0u ? 6u : 0u"));
            Assert.That(surface,
                Does.Contain("producerPhase < 2u"));
            Assert.That(surface,
                Does.Contain("laneBelongsToProducer"));
            Assert.That(particle,
                Does.Contain("producerPhase < 2u"));
            Assert.That(fog,
                Does.Contain("exactFeedbackRefinementOrBaseFallback"));
            Assert.That(coordinator,
                Does.Contain("receiver-feedback-refinement-completion-failed"));
            Assert.That(coordinator,
                Does.Contain("RefinementOrBaseFallback"));
        });
    }

    private static SimpleDdgiReceiverFeedbackCaptureProducerContract CreateValidContract() =>
        new(
            IsAvailable: true,
            GpuSortAbiVersion: SimpleDdgiReceiverFeedbackGpuSortAbi.Version,
            CaptureSourceAbiVersion: SimpleDdgiReceiverFeedbackCaptureSourceAbi.Version,
            CandidateBuffer: new BufferHandle(index: 7, generation: 1u),
            CandidateBufferBindlessIndex:
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.CandidateBindlessSlot,
            CandidateBufferDescriptorBytes: 512UL * sizeof(uint),
            CandidateControlOffsetWords:
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords,
            CandidateRecordOffsetWords:
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.GlobalHeaderWords +
            SimpleDdgiReceiverFeedbackCaptureSourceAbi.ControlWords,
            CandidateRecordCount: 16u,
            CandidateRecordStrideBytes:
            SimpleDdgiReceiverFeedbackGpuSortAbi.CaptureCandidateByteCount,
            ScreenSamplingPeriod: 4u,
            ScreenSamplingPhase: 1u,
            MaximumUniqueGatherOwnersPerTile:
            SimpleDdgiReceiverFeedbackCaptureSourceAbi
                .MaximumUniqueGatherOwnersPerTile,
            ProducerWriteStageMask: PipelineStageFlags2.ComputeShaderBit,
            ProducerWriteAccessMask: AccessFlags2.ShaderStorageWriteBit,
            RequiredProducerMask:
            SimpleDdgiReceiverFeedbackVulkanRuntime.OwnedProducerMask);

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}

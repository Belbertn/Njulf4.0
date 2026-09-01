using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AutomaticPlanarReflectionProductionTests
{
    [Test]
    public void DefaultAndLegacyMaterialDefinitions_AreDisabled()
    {
        GPUMaterialData legacyGpu = MaterialManager.CreateDefaultMaterial();
        MaterialDefinition legacy = MaterialDefinitionV1Adapter.FromGpuMaterial(
            legacyGpu,
            extension: null,
            MaterialRenderMetadata.FromGpuMaterial(legacyGpu));

        Assert.Multiple(() =>
        {
            Assert.That(
                MaterialDefinition.Default.AutomaticPlanarReflectionEnabled,
                Is.False);
            Assert.That(legacy.AutomaticPlanarReflectionEnabled, Is.False);
        });
    }

    [Test]
    public void PlanarEvidenceAndAdmission_RequiresExplicitMaterialOptIn()
    {
        GiPrimitivePlanarEvidence evidence = CreateEvidence();
        AutomaticPlanarCandidateInput disabledWater = Input(
            evidence,
            AutomaticPlanarMaterialSemantic.WaterSurface,
            materialOptInEnabled: false);
        AutomaticPlanarCandidateInput enabledGeneric = Input(
            evidence,
            AutomaticPlanarMaterialSemantic.Generic,
            materialOptInEnabled: true);

        AutomaticPlanarCandidateAdmission disabled =
            AutomaticPlanarCandidateAnalyzer.Analyze(
                disabledWater,
                1920,
                1080);
        AutomaticPlanarCandidateAdmission enabled =
            AutomaticPlanarCandidateAnalyzer.Analyze(
                enabledGeneric,
                1920,
                1080);

        Assert.Multiple(() =>
        {
            Assert.That(evidence.IsValid, Is.True);
            Assert.That(evidence.Validate(), Is.Empty);
            Assert.That(disabled.Admitted, Is.False);
            Assert.That(disabled.RejectionReason, Is.EqualTo(
                AutomaticPlanarCandidateRejectionReason
                    .MaterialOptInDisabled));
            Assert.That(enabled.Admitted, Is.True);
            Assert.That(enabled.Candidate.WorldPlane.Y, Is.GreaterThan(0.999f));
            Assert.That(enabled.Candidate.ProjectedPixels, Is.EqualTo(8_192f));
        });
    }

    [TestCase(AutomaticPlanarMaterialSemantic.Generic)]
    [TestCase(AutomaticPlanarMaterialSemantic.Mirror)]
    [TestCase(AutomaticPlanarMaterialSemantic.WaterSurface)]
    public void DisabledMaterial_IsRejectedBeforeSemanticEligibility(
        AutomaticPlanarMaterialSemantic semantic)
    {
        AutomaticPlanarCandidateAdmission admission =
            AutomaticPlanarCandidateAnalyzer.Analyze(
                Input(CreateEvidence(), semantic, materialOptInEnabled: false),
                1920,
                1080);

        Assert.That(admission.RejectionReason, Is.EqualTo(
            AutomaticPlanarCandidateRejectionReason.MaterialOptInDisabled));
    }

    [Test]
    public void EnabledGenericMaterial_IsNotVetoedByTextureStatisticsOrReflectivity()
    {
        AutomaticPlanarCandidateInput input = Input(
            CreateEvidence(),
            AutomaticPlanarMaterialSemantic.Generic,
            materialOptInEnabled: true) with
        {
            MeanRoughness = 1f,
            MaximumF0 = 0f,
            TextureStatisticsComplete = false
        };

        AutomaticPlanarCandidateAdmission admission =
            AutomaticPlanarCandidateAnalyzer.Analyze(input, 1920, 1080);

        Assert.That(admission.Admitted, Is.True, admission.Detail);
    }

    [Test]
    public void Clustering_MergesCoplanarReceiversAndUsesStableSemanticTieBreak()
    {
        AutomaticPlanarCandidate mirror = Candidate(
            identity: 20,
            receiver: 200,
            semantic: AutomaticPlanarMaterialSemantic.Mirror,
            planeOffset: 0f);
        AutomaticPlanarCandidate water = Candidate(
            identity: 10,
            receiver: 100,
            semantic: AutomaticPlanarMaterialSemantic.WaterSurface,
            planeOffset: 0.005f);
        AutomaticPlanarCandidate separate = Candidate(
            identity: 30,
            receiver: 300,
            semantic: AutomaticPlanarMaterialSemantic.Generic,
            planeOffset: 0.25f);

        IReadOnlyList<AutomaticPlanarCluster> clusters =
            AutomaticPlanarClusterer.ClusterAndRank(
                [mirror, water, separate]);

        Assert.Multiple(() =>
        {
            Assert.That(clusters, Has.Count.EqualTo(2));
            Assert.That(clusters[0].Members, Has.Count.EqualTo(2));
            Assert.That(clusters[0].ReceiverIdentities,
                Is.EquivalentTo(new uint[] { 100, 200 }));
            Assert.That(clusters[0].Representative.MaterialSemantic,
                Is.EqualTo(AutomaticPlanarMaterialSemantic.WaterSurface));
        });
    }

    [Test]
    public void MemoryPlanner_ReducesScaleBeforeRejectingCapture()
    {
        const ulong mebibyte = 1024UL * 1024UL;
        AutomaticPlanarMemoryPlan plan = AutomaticPlanarMemoryPlanner.Compile(
            fixedPlanarBytes: 120UL * mebibyte,
            budgetBytes: 160UL * mebibyte,
            requestedCaptureCount: 1,
            preferredScale: 0.5f,
            (_, scale) => scale switch
            {
                >= 0.5f => 60UL * mebibyte,
                >= 0.375f => 45UL * mebibyte,
                _ => 30UL * mebibyte
            });

        Assert.Multiple(() =>
        {
            Assert.That(plan.Admitted, Is.True);
            Assert.That(plan.CaptureCount, Is.EqualTo(1));
            Assert.That(plan.LinearScale, Is.EqualTo(0.25f));
            Assert.That(plan.TotalReflectionBytes,
                Is.EqualTo(150UL * mebibyte));
        });
    }

    [Test]
    public void MemoryPlanner_FeatureLocalBudgetExcludesIndependentReflectionOwners()
    {
        const ulong mebibyte = 1024UL * 1024UL;
        const ulong independentlyOwnedHybridBytes = 184UL * mebibyte;
        const ulong independentlyOwnedProbeBytes = 32UL * mebibyte;
        const ulong planarMetadataBytes = 4UL * mebibyte;

        AutomaticPlanarMemoryPlan plan = AutomaticPlanarMemoryPlanner.Compile(
            fixedPlanarBytes: planarMetadataBytes,
            budgetBytes: AutomaticPlanarMemoryPlanner.HighBudgetBytes,
            requestedCaptureCount: 1,
            preferredScale: 0.5f,
            (_, _) => 20UL * mebibyte);

        Assert.Multiple(() =>
        {
            Assert.That(
                independentlyOwnedHybridBytes + independentlyOwnedProbeBytes,
                Is.GreaterThan(AutomaticPlanarMemoryPlanner.HighBudgetBytes));
            Assert.That(plan.Admitted, Is.True);
            Assert.That(plan.FixedReflectionBytes,
                Is.EqualTo(planarMetadataBytes));
            Assert.That(plan.TotalReflectionBytes,
                Is.EqualTo(24UL * mebibyte));
        });
    }

    [Test]
    public void SubmittedFrameRing_PreservesTheTimestampAlignedWorkload()
    {
        var ring = new AutomaticPlanarSubmittedFrameRing();
        var submitted = new AutomaticPlanarLifecycleFrameSnapshot(
            Valid: true,
            FrameSlot: 1,
            FrameSerial: 42,
            GpuTimingRecorded: true,
            SelectedCount: 1,
            CaptureCount: 1,
            ReprojectionCount: 0,
            BitsetCaptureCount: 1,
            SortedListFallbackCount: 0,
            MetadataCapacityRejectionCount: 0);

        ring.MarkSubmitted(1, submitted);

        Assert.Multiple(() =>
        {
            Assert.That(ring.TryConsume(1, out var completed), Is.True);
            Assert.That(completed, Is.EqualTo(submitted));
            Assert.That(ring.TryConsume(1, out _), Is.False);
        });
    }

    [Test]
    public void CapturePolicy_ReprojectsWithinAgeAndRecapturesDirtyOrStaleState()
    {
        var fresh = new AutomaticPlanarCaptureState(
            Valid: true,
            ClusterIdentity: 7,
            CaptureGeneration: 2,
            AgeFrames: 1,
            DynamicOrDirty: false,
            Confidence: 1f,
            CurrentReflectedViewProjection: Matrix4x4.Identity,
            PreviousReflectedViewProjection: Matrix4x4.Identity);
        var stale = fresh with
        {
            AgeFrames = AutomaticPlanarCapturePolicy.StableMaximumReuseFrames
        };

        Assert.Multiple(() =>
        {
            Assert.That(AutomaticPlanarCapturePolicy.Resolve(
                    fresh, 7, false, false, false, false),
                Is.EqualTo(AutomaticPlanarCaptureAction.Reproject));
            Assert.That(AutomaticPlanarCapturePolicy.Resolve(
                    stale, 7, false, false, false, false),
                Is.EqualTo(AutomaticPlanarCaptureAction.Capture));
            Assert.That(AutomaticPlanarCapturePolicy.Resolve(
                    fresh, 7, false, false, false, true),
                Is.EqualTo(AutomaticPlanarCaptureAction.Capture));
            Assert.That(AutomaticPlanarCapturePolicy.ResolveReprojectedConfidence(
                    0.8f, 0.25f, 2),
                Is.EqualTo(0.486f).Within(0.0001f));
        });
    }

    [Test]
    public void CameraReflectionAndReceiverValidation_MatchTheWorldPlane()
    {
        Vector4 plane = new(0f, 1f, 0f, 0f);
        Vector3 reflected = AutomaticPlanarCameraMath.ReflectPoint(
            new Vector3(2f, 3f, 4f),
            plane);

        Assert.Multiple(() =>
        {
            Assert.That(reflected, Is.EqualTo(new Vector3(2f, -3f, 4f)));
            Assert.That(AutomaticPlanarCameraMath.ReceiverMatches(
                    17, 17, new Vector3(1f, 0.0001f, 1f),
                    Vector3.UnitY, plane, 0.001f),
                Is.True);
            Assert.That(AutomaticPlanarCameraMath.ReceiverMatches(
                    18, 17, Vector3.Zero,
                    Vector3.UnitY, plane, 0.001f),
                Is.False);
        });
    }

    [Test]
    public void ReprojectionAndPrefilterShaders_PerformDepthValidationAndGgxFiltering()
    {
        string reproject = ReadShader("automatic_planar_reproject.comp");
        string prefilter = ReadShader("automatic_planar_prefilter.comp");
        string sampling = ReadShader("automatic_planar_reflection.glsl");
        string exactListBody = sampling[
            sampling.IndexOf(
                "bool AutomaticPlanarExactListContains",
                StringComparison.Ordinal)..sampling.IndexOf(
                "bool AutomaticPlanarExcludedObjectContains",
                StringComparison.Ordinal)];

        Assert.Multiple(() =>
        {
            Assert.That(AutomaticPlanarReflectionManager.MetadataVersion,
                Is.EqualTo(3));
            Assert.That(reproject, Does.Contain("imageAtomicMax"));
            Assert.That(reproject, Does.Contain("depthTolerance"));
            Assert.That(reproject, Does.Contain("environment"));
            Assert.That(prefilter, Does.Contain("ImportanceSampleGgx"));
            Assert.That(prefilter, Does.Contain("roughness"));
            Assert.That(sampling, Does.Contain("filteredSample.a"));
            Assert.That(sampling, Does.Contain(
                "AUTOMATIC_PLANAR_METADATA_VERSION = 3u"));
            Assert.That(sampling, Does.Contain(
                "AutomaticPlanarExcludedObjectContains"));
            Assert.That(sampling, Does.Contain(
                "AUTOMATIC_PLANAR_EXCLUSION_BITSET_FLAG = 0x80000000u"));
            Assert.That(sampling, Does.Contain(
                "wordIndex = objectIndex >> 5u"));
            Assert.That(sampling, Does.Contain(
                "AutomaticPlanarExactListContains"));
            Assert.That(exactListBody, Does.Not.Contain("min(count"));
        });
    }

    [Test]
    public void MetadataIdentityPacking_PreservesAll64Bits()
    {
        const ulong identity = 0xfedcba98_76543210UL;

        (uint low, uint high) =
            AutomaticPlanarReflectionManager.SplitClusterIdentity(identity);

        Assert.Multiple(() =>
        {
            Assert.That(low, Is.EqualTo(0x76543210U));
            Assert.That(high, Is.EqualTo(0xfedcba98U));
            Assert.That(((ulong)high << 32) | low, Is.EqualTo(identity));
        });
    }

    private static GiPrimitivePlanarEvidence CreateEvidence() =>
        GiPrimitivePlanarEvidenceAnalyzer.Analyze(
            new Vector3[]
            {
                new(-1f, 0f, -1f),
                new(1f, 0f, -1f),
                new(1f, 0f, 1f),
                new(-1f, 0f, 1f)
            },
            new uint[] { 0, 2, 1, 0, 3, 2 },
            deforming: false);

    private static AutomaticPlanarCandidateInput Input(
        GiPrimitivePlanarEvidence evidence,
        AutomaticPlanarMaterialSemantic semantic,
        bool materialOptInEnabled) => new(
        StableIdentity: 1,
        ObjectIndex: 2,
        ContentRevision: 3,
        ReceiverIdentity: 4,
        Evidence: evidence,
        WorldMatrix: Matrix4x4.Identity,
        MaterialOptInEnabled: materialOptInEnabled,
        MaterialSemantic: semantic,
        MeanRoughness: 0.1f,
        MaximumF0: 0.04f,
        TextureStatisticsComplete: false,
        Visible: true,
        Deforming: false,
        ProjectedPixels: 8_192f,
        ViewFresnel: 0.5f,
        DistanceToCamera: 10f,
        DynamicOrDirty: false);

    private static AutomaticPlanarCandidate Candidate(
        ulong identity,
        uint receiver,
        AutomaticPlanarMaterialSemantic semantic,
        float planeOffset) => new(
        StableIdentity: identity,
        ObjectIndex: checked((uint)identity),
        ContentRevision: 1,
        ReceiverIdentity: receiver,
        WorldPlane: new Vector4(0f, 1f, 0f, planeOffset),
        WorldOrigin: new Vector3(0f, -planeOffset, 0f),
        WorldTangent: Vector3.UnitX,
        WorldBitangent: Vector3.UnitZ,
        ProjectedBoundsMin: new Vector2(-1f),
        ProjectedBoundsMax: new Vector2(1f),
        WorldDiagonal: 4f,
        MaterialSemantic: semantic,
        MeanRoughness: 0.1f,
        MaximumF0: 0.04f,
        ProjectedPixels: 8_192f,
        ViewFresnel: 0.5f,
        DistanceToCamera: 10f,
        DynamicOrDirty: false);

    private static string ReadShader(string name)
    {
        DirectoryInfo? cursor = new(TestContext.CurrentContext.TestDirectory);
        while (cursor != null)
        {
            string path = Path.Combine(cursor.FullName, "Njulf.Shaders", name);
            if (File.Exists(path))
                return File.ReadAllText(path);
            cursor = cursor.Parent;
        }
        throw new FileNotFoundException($"Could not locate shader '{name}'.");
    }
}

using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using System.Text.Json;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleMaterialGiConformanceSceneTests
{
    [SetUp]
    public void ClearCaptureEnvironment()
    {
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_MODE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SMOKE_FRAMES", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_SCENE", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_PERFORMANCE_SCENARIO", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BASELINE_SNAPSHOT_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_SPONZA_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_MATERIAL_GI_CAPTURE_DIR", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_BENCHMARK_REPORT", null);
        Environment.SetEnvironmentVariable("NJULF_RENDERER_VALIDATION", null);
    }

    [Test]
    public void Layout_InstantiatesEveryOracleCaseExactlyOnceWithStableIdentity()
    {
        IReadOnlyList<SampleMaterialGiSceneFixture> fixtures =
            SampleMaterialGiConformanceCatalog.SceneFixtures;
        string[] expectedCases = SampleMaterialGiConformanceCatalog.Cases
            .Select(static value => value.Name)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] renderedCases = fixtures
            .Where(static value => value.CatalogCaseName != null)
            .Select(static value => value.CatalogCaseName!)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(renderedCases, Is.EqualTo(expectedCases));
            Assert.That(
                fixtures.Select(static value => value.StableId),
                Is.Unique);
            Assert.That(
                fixtures.Select(static value =>
                    SampleMaterialGiConformanceSceneLayout.CreateStableEntityId(value.StableId)),
                Is.Unique);
            Assert.That(
                fixtures.All(static value =>
                    float.IsFinite(value.Transform.Position.X) &&
                    float.IsFinite(value.Transform.Position.Y) &&
                    float.IsFinite(value.Transform.Position.Z) &&
                    float.IsFinite(value.Transform.Scale.X) &&
                    float.IsFinite(value.Transform.Scale.Y) &&
                    float.IsFinite(value.Transform.Scale.Z) &&
                    MathF.Abs(value.Transform.Scale.X) > 1e-6f &&
                    MathF.Abs(value.Transform.Scale.Y) > 1e-6f &&
                    MathF.Abs(value.Transform.Scale.Z) > 1e-6f),
                Is.True);
            Assert.That(SampleMaterialGiConformanceCatalog.SceneFingerprint, Has.Length.EqualTo(64));
            Assert.That(
                SampleMaterialGiConformanceSceneLayout.ComputeFingerprint(
                    fixtures,
                    SampleMaterialGiConformanceCatalog.CaseFingerprint),
                Is.EqualTo(SampleMaterialGiConformanceCatalog.SceneFingerprint));
        });
    }

    [Test]
    public void Layout_CoversEveryPhaseZeroVisualFixtureCategory()
    {
        var categories = SampleMaterialGiConformanceCatalog.SceneFixtures
            .Select(static value => value.Category)
            .ToHashSet();

        Assert.That(
            categories,
            Is.SupersetOf(SampleMaterialGiConformanceSceneLayout.RequiredCategories));

        Assert.Multiple(() =>
        {
            Assert.That(
                Fixtures(SampleMaterialGiSceneFixtureCategory.WhiteDielectricCornellBox),
                Has.Count.GreaterThanOrEqualTo(6));
            Assert.That(
                Fixtures(SampleMaterialGiSceneFixtureCategory.ColoredDielectricCornellBox)
                    .Select(static value => value.MaterialPreset),
                Does.Contain(SampleMaterialGiSceneMaterialPreset.CornellRed)
                    .And.Contain(SampleMaterialGiSceneMaterialPreset.CornellGreen));
            Assert.That(
                CaseNames(SampleMaterialGiSceneFixtureCategory.MetallicSweep),
                Is.EquivalentTo(
                new[]
                {
                    "metallic-0.00",
                    "metallic-0.25",
                    "metallic-0.50",
                    "metallic-0.75",
                    "metallic-1.00"
                }));
            Assert.That(
                CaseNames(SampleMaterialGiSceneFixtureCategory.RoughnessAndDielectricF0Sweep),
                Does.Contain("roughness-0.00")
                    .And.Contain("roughness-1.00")
                    .And.Contain("dielectric-f0-ior-1.0")
                    .And.Contain("dielectric-f0-ior-3.0"));
            Assert.That(
                CaseNames(SampleMaterialGiSceneFixtureCategory.SparseCheckerEmissionSweep),
                Is.EquivalentTo(
                new[]
                {
                    "emission-strength-0.0",
                    "emission-strength-0.5",
                    "emission-strength-1.0",
                    "emission-strength-10.0"
                }));
            Assert.That(
                CaseNames(SampleMaterialGiSceneFixtureCategory.SeparateUvOcclusion),
                Is.EquivalentTo(new[] { "ao-strength-zero", "ao-strength-one" }));
            Assert.That(
                CaseNames(SampleMaterialGiSceneFixtureCategory.UnlitSurface),
                Is.EquivalentTo(new[] { "unlit-visibility-only", "unlit-explicit-emission" }));
        });
    }

    [Test]
    public void Layout_UsesRealCardAndSkinningPathsForCoverageFixtures()
    {
        SampleMaterialGiSceneFixture staticMask = FindCase("alpha-mask-equality-static");
        SampleMaterialGiSceneFixture skinnedMask = FindCase("alpha-mask-equality-skinned");
        SampleMaterialGiSceneFixture cutoffAboveOne = FindCase("alpha-mask-cutoff-above-one");
        SampleMaterialGiSceneFixture singleBack = FindCase("single-sided-back-face");
        SampleMaterialGiSceneFixture doubleBack = FindCase("double-sided-back-face");

        Assert.Multiple(() =>
        {
            Assert.That(staticMask.Primitive, Is.EqualTo(SampleMaterialGiScenePrimitive.Card));
            Assert.That(skinnedMask.Primitive, Is.EqualTo(SampleMaterialGiScenePrimitive.SkinnedCard));
            Assert.That(cutoffAboveOne.Primitive, Is.EqualTo(SampleMaterialGiScenePrimitive.Card));
            Assert.That(singleBack.Primitive, Is.EqualTo(SampleMaterialGiScenePrimitive.Card));
            Assert.That(doubleBack.Primitive, Is.EqualTo(SampleMaterialGiScenePrimitive.Card));
            Assert.That(singleBack.Transform.RotationRadians.Y, Is.EqualTo(MathF.PI));
            Assert.That(doubleBack.Transform.RotationRadians.Y, Is.EqualTo(MathF.PI));
            Assert.That(
                SampleMaterialGiProceduralTextureSet.AlphaMaskRgba
                    .Where((_, index) => index % 4 == 3)
                    .Distinct(),
                Is.EquivalentTo(new byte[] { 0, 128, 255 }));
        });
    }

    [Test]
    public void Layout_HasExplicitMirroredAndSkinnedVisibilityWitnesses()
    {
        SampleMaterialGiSceneFixture mirroredSingleFront =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticMirroredSingleFrontId);
        SampleMaterialGiSceneFixture mirroredSingleBack =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticMirroredSingleBackId);
        SampleMaterialGiSceneFixture mirroredDoubleBack =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticMirroredDoubleBackId);
        SampleMaterialGiSceneFixture skinnedSingleFront =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskSingleFrontId);
        SampleMaterialGiSceneFixture skinnedSingleBack =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskSingleBackId);
        SampleMaterialGiSceneFixture skinnedDoubleBack =
            FindFixture(SampleMaterialGiConformanceSceneLayout.SemanticSkinnedMaskDoubleBackId);

        Assert.Multiple(() =>
        {
            Assert.That(LinearDeterminantSign(mirroredSingleFront), Is.EqualTo(-1));
            Assert.That(LinearDeterminantSign(mirroredSingleBack), Is.EqualTo(-1));
            Assert.That(LinearDeterminantSign(mirroredDoubleBack), Is.EqualTo(-1));
            Assert.That(mirroredSingleFront.Transform.RotationRadians.Y, Is.Zero);
            Assert.That(mirroredSingleBack.Transform.RotationRadians.Y, Is.EqualTo(MathF.PI));
            Assert.That(mirroredDoubleBack.Transform.RotationRadians.Y, Is.EqualTo(MathF.PI));
            Assert.That(
                mirroredSingleFront.MaterialPreset,
                Is.EqualTo(SampleMaterialGiSceneMaterialPreset.SemanticSingleSided));
            Assert.That(
                mirroredDoubleBack.MaterialPreset,
                Is.EqualTo(SampleMaterialGiSceneMaterialPreset.SemanticDoubleSided));
            Assert.That(
                new[] { skinnedSingleFront, skinnedSingleBack, skinnedDoubleBack }
                    .Select(static value => value.Primitive),
                Is.All.EqualTo(SampleMaterialGiScenePrimitive.SkinnedCard));
            Assert.That(
                new[] { skinnedSingleFront, skinnedSingleBack, skinnedDoubleBack }
                    .All(value =>
                    FindFixture($"{value.StableId}.backdrop").MaterialPreset ==
                    SampleMaterialGiSceneMaterialPreset.SemanticBackdrop),
                Is.True);
            Assert.That(
                SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(
                    SampleMaterialGiSceneMaterialPreset.SemanticDoubleSided).DoubleSided,
                Is.True);
            Assert.That(
                SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(
                    SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskSingleSided).AlphaMode,
                Is.EqualTo(MaterialAlphaMode.Mask));
            Assert.That(
                SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(
                    SampleMaterialGiSceneMaterialPreset.SemanticSkinnedMaskDoubleSided).DoubleSided,
                Is.True);
        });
    }

    [Test]
    public void SemanticEvidence_UsesNamedInteriorRegionsWithLockedSignalAndThresholds()
    {
        SampleMaterialGiSemanticEvidenceContract evidence =
            SampleMaterialGiConformanceCatalog.SemanticEvidence;

        Assert.Multiple(() =>
        {
            Assert.That(evidence.Signal, Is.EqualTo(SampleMaterialGiCaptureSignal.MaterialSidedness));
            Assert.That(evidence.Width, Is.EqualTo(SampleMaterialGiConformanceCatalog.LockedWidth));
            Assert.That(evidence.Height, Is.EqualTo(SampleMaterialGiConformanceCatalog.LockedHeight));
            Assert.That(evidence.Fingerprint, Has.Length.EqualTo(64));
            Assert.That(evidence.Regions, Has.Count.EqualTo(9));
            Assert.That(evidence.Regions.Select(static value => value.Name), Is.Unique);
            Assert.That(
                evidence.Regions.Select(static value => value.ExpectedSurface).ToHashSet(),
                Is.EquivalentTo(Enum.GetValues<SampleMaterialGiSemanticSurface>()));
            Assert.That(
                evidence.Regions.All(value =>
                    value.Region.X >= 0 &&
                    value.Region.Y >= 0 &&
                    value.Region.X + value.Region.Width <= evidence.Width &&
                    value.Region.Y + value.Region.Height <= evidence.Height),
                Is.True);
            Assert.That(evidence.Thresholds.MaximumPerComponentError, Is.EqualTo(0.035f));
            Assert.That(evidence.Thresholds.RequiredMatchingPixelFraction, Is.EqualTo(0.98f));
            Assert.That(
                SampleMaterialGiConformanceCatalog.RequiredOutputs.Single(
                    value => value.Signal == evidence.Signal).MaterialDebugView,
                Is.EqualTo(MaterialDebugView.Sidedness));
        });
    }

    [Test]
    public void Layout_LocksTransitionDistancesLiveEditsAndSimpleDdgiReceiver()
    {
        SampleMaterialGiSceneFixture near = FindFixture("material-gi.transition.near");
        SampleMaterialGiSceneFixture compact = FindFixture("material-gi.transition.compact");
        SampleMaterialGiSceneFixture far = FindFixture("material-gi.transition.far");
        IReadOnlyList<SampleMaterialGiSceneFixture> liveEdit =
            Fixtures(SampleMaterialGiSceneFixtureCategory.LiveEditMaterialWall);
        IReadOnlyList<SampleMaterialGiSceneFixture> overlap =
            Fixtures(SampleMaterialGiSceneFixtureCategory.SimpleDdgiReceiver);

        Assert.Multiple(() =>
        {
            Assert.That(near.Transform.Position.Z, Is.EqualTo(3.55f));
            Assert.That(compact.Transform.Position.Z, Is.EqualTo(-12f));
            Assert.That(far.Transform.Position.Z, Is.EqualTo(-55f));
            Assert.That(near.Transform.Position.Z, Is.GreaterThan(compact.Transform.Position.Z));
            Assert.That(compact.Transform.Position.Z, Is.GreaterThan(far.Transform.Position.Z));
            Assert.That(
                liveEdit,
                Has.Count.EqualTo(SampleMaterialGiConformanceSceneLayout.LiveEditSettleUpdateCount));
            Assert.That(
                liveEdit.Select(static value => value.MaterialPreset),
                Is.EquivalentTo(
                new[]
                {
                    SampleMaterialGiSceneMaterialPreset.LiveEditBaseColor,
                    SampleMaterialGiSceneMaterialPreset.LiveEditMetallic,
                    SampleMaterialGiSceneMaterialPreset.LiveEditEmission,
                    SampleMaterialGiSceneMaterialPreset.LiveEditAlphaCoverage
                }));
            Assert.That(overlap.Select(static value => value.StableId), Is.EquivalentTo(
            new[]
            {
                "material-gi.hybrid-overlap.receiver",
                "material-gi.hybrid-overlap.thin-blocker",
                "material-gi.hybrid-overlap.detail-emitter"
            }));
        });

        foreach (SampleMaterialGiSceneFixture fixture in liveEdit)
        {
            MaterialDefinition initial =
                SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(fixture.MaterialPreset);
            MaterialDefinition final =
                SampleMaterialGiConformanceSceneLayout.CreatePresetMaterial(
                    fixture.MaterialPreset,
                    finalLiveEditState: true);
            Assert.That(
                MaterialTransportCompiler.ClassifyChanges(initial, final),
                Is.Not.EqualTo(MaterialChangeMask.None),
                fixture.StableId);
        }
    }

    [Test]
    public void MaterialCaptureCli_SelectsConformanceMatrixWithoutChangingInteractiveShowcase()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NjulfMaterialGiScene",
            Guid.NewGuid().ToString("N"));
        SampleSmokeOptions capture = SampleSmokeOptionsParser.Parse(
            ["--material-gi-capture-dir", directory]);
        SampleSmokeOptions interactive = SampleSmokeOptionsParser.Parse(
            ["--scene", "material-showcase"]);

        Assert.Multiple(() =>
        {
            Assert.That(SampleMaterialGiConformanceScene.IsCaptureSceneRequested(capture), Is.True);
            Assert.That(SampleMaterialGiConformanceScene.IsCaptureSceneRequested(interactive), Is.False);
            Assert.That(capture.SceneKind, Is.EqualTo(SampleMaterialGiConformanceCatalog.SceneKind));
            Assert.That(
                SampleMaterialGiConformanceCatalog.Camera.Position,
                Is.EqualTo(new Njulf.Core.Math.Vector3(0f, 1.65f, 7.8f)));
            Assert.That(SampleMaterialGiConformanceCatalog.Camera.Yaw, Is.Zero);
            Assert.That(SampleMaterialGiConformanceCatalog.Camera.Pitch, Is.EqualTo(-0.11f));
        });
    }

    [Test]
    public void ProceduralTextures_AreDeterministicAndExerciseIndependentSignals()
    {
        byte[] emission = SampleMaterialGiProceduralTextureSet.EmissiveSparseCheckerRgba;
        byte[] occlusion = SampleMaterialGiProceduralTextureSet.SeparateUvOcclusionRgba;
        byte[] alpha = SampleMaterialGiProceduralTextureSet.AlphaMaskRgba;

        Assert.Multiple(() =>
        {
            Assert.That(
                emission,
                Has.Length.EqualTo(
                    SampleMaterialGiProceduralTextureSet.Width *
                    SampleMaterialGiProceduralTextureSet.Height * 4));
            Assert.That(emission.Where((_, index) => index % 4 != 3), Does.Contain((byte)0));
            Assert.That(emission.Where((_, index) => index % 4 != 3), Does.Contain((byte)255));
            Assert.That(occlusion.Where((_, index) => index % 4 == 0).Distinct().Count(), Is.GreaterThan(4));
            Assert.That(alpha.Where((_, index) => index % 4 == 3).Distinct().Count(), Is.EqualTo(3));
            Assert.That(SampleMaterialGiProceduralTextureSet.ContentFingerprint, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void ContractPublication_EmbedsExactSceneManifestAndFingerprint()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-scene-contract",
            Guid.NewGuid().ToString("N"));
        try
        {
            SampleMaterialGiConformanceCatalog.WriteContract(directory);
            string path = Path.Combine(directory, "material-gi-conformance-contract.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;

            Assert.Multiple(() =>
            {
                Assert.That(
                    root.GetProperty("caseFingerprint").GetString(),
                    Is.EqualTo(SampleMaterialGiConformanceCatalog.CaseFingerprint));
                Assert.That(
                    root.GetProperty("sceneFingerprint").GetString(),
                    Is.EqualTo(SampleMaterialGiConformanceCatalog.SceneFingerprint));
                Assert.That(
                    root.GetProperty("sceneSchemaVersion").GetString(),
                    Is.EqualTo(SampleMaterialGiConformanceSceneLayout.CurrentSchemaVersion));
                Assert.That(
                    root.GetProperty("sceneFixtures").GetArrayLength(),
                    Is.EqualTo(SampleMaterialGiConformanceCatalog.SceneFixtures.Count));
                Assert.That(
                    root.GetProperty("probeVolume").GetProperty("stableId").GetString(),
                    Is.EqualTo(SampleMaterialGiConformanceSceneLayout.ProbeVolumeStableId));
                Assert.That(
                    root.GetProperty("semanticEvidence").GetProperty("fingerprint").GetString(),
                    Is.EqualTo(SampleMaterialGiConformanceCatalog.SemanticEvidence.Fingerprint));
            });
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<SampleMaterialGiSceneFixture> Fixtures(
        SampleMaterialGiSceneFixtureCategory category) =>
        SampleMaterialGiConformanceCatalog.SceneFixtures
            .Where(value => value.Category == category)
            .ToArray();

    private static string[] CaseNames(SampleMaterialGiSceneFixtureCategory category) =>
        Fixtures(category)
            .Select(static value => value.CatalogCaseName!)
            .Where(static value => value != null)
            .ToArray();

    private static SampleMaterialGiSceneFixture FindCase(string name) =>
        SampleMaterialGiConformanceCatalog.SceneFixtures.Single(
            value => string.Equals(value.CatalogCaseName, name, StringComparison.Ordinal));

    private static SampleMaterialGiSceneFixture FindFixture(string stableId) =>
        SampleMaterialGiConformanceCatalog.SceneFixtures.Single(
            value => string.Equals(value.StableId, stableId, StringComparison.Ordinal));

    private static int LinearDeterminantSign(SampleMaterialGiSceneFixture fixture) =>
        Math.Sign(
            fixture.Transform.Scale.X *
            fixture.Transform.Scale.Y *
            fixture.Transform.Scale.Z);
}

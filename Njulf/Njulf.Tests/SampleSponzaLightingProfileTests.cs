using System.Numerics;
using System.Text.Json;
using Njulf.Assets.Scenes;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSponzaLightingProfileTests
{
    private const float DirectionTolerance = 0.00001f;

    [Test]
    public void DirectionalKey_RotatesDisabledSourceSunForRightWallView()
    {
        SourceSun sourceSun = ReadSourceSun();
        Vector3 expectedDirection = Vector3.Normalize(new Vector3(
            -sourceSun.Direction.X,
            sourceSun.Direction.Y,
            -sourceSun.Direction.Z));
        Light directionalKey = SampleSponzaLightingProfile.CreateDirectionalKey();

        Assert.Multiple(() =>
        {
            Assert.That(sourceSun.Type, Is.EqualTo("directional"));
            Assert.That(sourceSun.Intensity, Is.Zero);
            AssertDirection(SampleSponzaLightingProfile.SourceSunDirection, sourceSun.Direction);
            Assert.That(directionalKey.Type, Is.EqualTo(LightType.Directional));
            AssertDirection(directionalKey.Direction, expectedDirection);
            Assert.That(directionalKey.Color, Is.EqualTo(new Vector3(1.0f, 0.92f, 0.82f)));
            Assert.That(directionalKey.Intensity, Is.EqualTo(14f));
            Assert.That(directionalKey.Range, Is.EqualTo(10f));
            Assert.That(directionalKey.CastsShadows, Is.True);
            Assert.That(directionalKey.ShadowStrength, Is.EqualTo(1.0f));
            Assert.That(directionalKey.ShadowPriority, Is.EqualTo(10));
        });
    }

    [Test]
    public void SampleSceneDirectionalKey_MatchesRuntimeProfile()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Scenes",
            "SampleScene.njscene.json");
        SceneDocument document = SceneDocumentJson.Read(path);
        SceneLightDocument sceneKey = document.Lights.Single(light =>
            string.Equals(light.Name, "Directional Key", StringComparison.Ordinal) &&
            string.Equals(light.Type, nameof(LightType.Directional), StringComparison.OrdinalIgnoreCase));
        Vector3 sceneDirection = new(
            sceneKey.Direction.X,
            sceneKey.Direction.Y,
            sceneKey.Direction.Z);
        Light runtimeKey = SampleSponzaLightingProfile.CreateDirectionalKey();

        Assert.Multiple(() =>
        {
            AssertDirection(sceneDirection, runtimeKey.Direction);
            Assert.That(
                new Vector3(sceneKey.Color.X, sceneKey.Color.Y, sceneKey.Color.Z),
                Is.EqualTo(runtimeKey.Color));
            Assert.That(sceneKey.Intensity, Is.EqualTo(runtimeKey.Intensity));
            Assert.That(sceneKey.CastsShadows, Is.EqualTo(runtimeKey.CastsShadows));
            Assert.That(sceneKey.ShadowStrength, Is.EqualTo(runtimeKey.ShadowStrength));
            Assert.That(sceneKey.ShadowPriority, Is.EqualTo(runtimeKey.ShadowPriority));
        });
    }

    [Test]
    public void SampleScene_DoesNotAuthorManualReflectionProbes()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Scenes",
            "SampleScene.njscene.json");
        SceneDocument document = SceneDocumentJson.Read(path);

        Assert.That(document.ReflectionProbes, Is.Empty);
    }

    [TestCase(SampleSponzaFixtureMode.Architecture)]
    [TestCase(SampleSponzaFixtureMode.C5ResidualValidation)]
    public void SampleScene_NonAnimationModesExcludeAuthoredStrut(
        SampleSponzaFixtureMode fixtureMode)
    {
        SceneDocument document = ReadSampleScene();

        SampleSceneLoader.ApplySponzaFixtureMode(document, fixtureMode);

        Assert.Multiple(() =>
        {
            Assert.That(
                document.Objects,
                Has.None.Matches<SceneObjectDocument>(item =>
                    string.Equals(
                        item.Model.Path,
                        SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                        StringComparison.Ordinal)));
            Assert.That(
                document.Dependencies,
                Has.None.Matches<SceneAssetDependency>(item =>
                    string.Equals(
                        item.Path,
                        SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                        StringComparison.Ordinal)));
        });
    }

    [Test]
    public void SampleScene_AnimationDemoRetainsCanonicalStrut()
    {
        SceneDocument document = ReadSampleScene();

        SampleSceneLoader.ApplySponzaFixtureMode(
            document,
            SampleSponzaFixtureMode.AnimationDemo);

        SceneObjectDocument[] fixtures = document.Objects
            .Where(item => string.Equals(
                item.Model.Path,
                SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                StringComparison.Ordinal))
            .OrderBy(item => item.Id)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(fixtures, Has.Length.EqualTo(2));
            Assert.That(
                fixtures.Select(static item => item.Id),
                Is.EqualTo(new[]
                {
                    SampleBenchmarkSponzaSceneAnimationContract.JointObjectId,
                    SampleBenchmarkSponzaSceneAnimationContract.SurfaceObjectId
                }));
            Assert.That(
                document.Dependencies.Count(item => string.Equals(
                    item.Path,
                    SampleBenchmarkSponzaSceneAnimationContract.AssetPath,
                    StringComparison.Ordinal)),
                Is.EqualTo(1));
        });
    }

    private static SceneDocument ReadSampleScene()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Scenes",
            "SampleScene.njscene.json");
        return SceneDocumentJson.Read(path);
    }

    private static SourceSun ReadSourceSun()
    {
        string path = ResolveRepositoryFile(
            "NjulfHelloGame",
            "NewSponza_Main_glTF_003.gltf");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        JsonElement nodes = root.GetProperty("nodes");
        Vector3 sun = ReadNodeTranslation(nodes, "SUN");
        Vector3 target = ReadNodeTranslation(nodes, "SUN.Target");
        JsonElement sunNode = nodes.EnumerateArray().Single(candidate =>
            candidate.TryGetProperty("name", out JsonElement value) &&
            string.Equals(value.GetString(), "SUN", StringComparison.Ordinal));
        int lightIndex = sunNode
            .GetProperty("extensions")
            .GetProperty("KHR_lights_punctual")
            .GetProperty("light")
            .GetInt32();
        JsonElement sourceLight = root
            .GetProperty("extensions")
            .GetProperty("KHR_lights_punctual")
            .GetProperty("lights")[lightIndex];

        return new SourceSun(
            Vector3.Normalize(target - sun),
            sourceLight.GetProperty("type").GetString() ?? string.Empty,
            sourceLight.GetProperty("intensity").GetSingle());
    }

    private static string ResolveRepositoryFile(params string[] relativeParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{Path.Combine(relativeParts)}'.");
    }

    private static Vector3 ReadNodeTranslation(JsonElement nodes, string name)
    {
        JsonElement node = nodes.EnumerateArray().Single(candidate =>
            candidate.TryGetProperty("name", out JsonElement value) &&
            string.Equals(value.GetString(), name, StringComparison.Ordinal));
        JsonElement translation = node.GetProperty("translation");
        return new Vector3(
            translation[0].GetSingle(),
            translation[1].GetSingle(),
            translation[2].GetSingle());
    }

    private static void AssertDirection(Vector3 actual, Vector3 expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(DirectionTolerance));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(DirectionTolerance));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(DirectionTolerance));
        });
    }

    private sealed record SourceSun(Vector3 Direction, string Type, float Intensity);
}

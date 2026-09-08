using Njulf.Assets;
using Njulf.Assets.Cooked;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SponzaCookedIntegrationTests
{
    [Test]
    [Explicit("Requires both local New Sponza source assets and their win-x64 cooks.")]
    public void BothSponzaCooks_ResolveUnderExactRuntimeImportContracts()
    {
        string root = FindRepositoryRoot();
        string contentRoot = Path.Combine(root, "NjulfHelloGame");
        var resolver = new CookedContentResolver(contentRoot);

        foreach (SampleAssetReference asset in
                 SampleAssetManifest.NewSponza.EnumerateAssets())
        {
            string sourcePath = Path.GetFullPath(Path.Combine(
                contentRoot,
                asset.Path));
            if (!File.Exists(sourcePath))
            {
                Assert.Ignore(
                    $"The local New Sponza source is required: {sourcePath}");
            }

            ContentLoadOptions loadOptions = asset.CreateLoadOptions();
            ulong expectedImportContract = CookedModelImportContract.Compute(
                sourcePath,
                loadOptions.ImporterOptions ?? ImporterOptions.Default);
            CookedResolution resolution = resolver.ResolveModel(
                asset.Path,
                sourcePath,
                strictSourceHash: true,
                expectedImportContract,
                captureModelSnapshot: true);

            Assert.Multiple(() =>
            {
                Assert.That(
                    resolution.Status,
                    Is.EqualTo(CookedResolutionStatus.Found),
                    $"{asset.Path}: {resolution.Reason}");
                Assert.That(
                    resolution.Header?.ImportSettingsHash,
                    Is.EqualTo(expectedImportContract),
                    asset.Path);
                Assert.That(
                    resolution.Header?.FormatMinor,
                    Is.EqualTo(CookedFormatVersions.Model.Minor),
                    asset.Path);
                Assert.That(resolution.ModelSnapshot, Is.Not.Null, asset.Path);
            });
        }
    }

    [Test]
    [Explicit("Requires the local Sponza cook.")]
    public void MainCook_LanternGlassTransmitsWhileFrameAndBulbStayOpaque()
    {
        string modelPath = Path.Combine(FindRepositoryRoot(), "NjulfHelloGame", "Cooked", "win-x64", "models", "NewSponza_Main_glTF_003.njmodel");
        using var reader = new CookedAssetReader(modelPath, CookedAssetKind.Model);
        var manifest = CookedJson.Deserialize<CookedModelManifest>(
            reader.GetRequiredSection(CookedSectionIds.Manifest).Span, modelPath, "manifest");
        string materialPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modelPath)!, manifest.Material.RelativePath));
        var materials = CookedPackage.LoadMaterials(materialPath, CookedAssetReaderFlags.StrictSourceHash, out _).Materials;
        var glass = materials.Single(x => x.Name == "lamp_glass_01");
        Assert.Multiple(() =>
        {
            Assert.That(glass.IsThinGlass, Is.True);
            Assert.That(glass.AlphaMode, Is.EqualTo(ModelAlphaMode.Blend));
            Assert.That(glass.TransmissionFactor, Is.GreaterThan(.9f));
            Assert.That(glass.SpecularFactor, Is.Zero);
            Assert.That(glass.GiTransmissionPolicy, Is.EqualTo(ModelGiTransmissionPolicy.ThinSurface));
            Assert.That(materials.Single(x => x.Name == "metal_door").AlphaMode, Is.EqualTo(ModelAlphaMode.Opaque));
            Assert.That(materials.Single(x => x.Name == "light_bulb").AlphaMode, Is.EqualTo(ModelAlphaMode.Opaque));
        });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing Njulf.sln.");
    }
}

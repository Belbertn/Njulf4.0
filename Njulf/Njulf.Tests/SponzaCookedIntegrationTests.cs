using Njulf.Assets;
using Njulf.Assets.Cooked;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SponzaCookedIntegrationTests
{
    [TestCase("NewSponza_Main_glTF_003")]
    [TestCase("NewSponza_Curtains_glTF")]
    [TestCase("BistroExterior")]
    [TestCase("BistroInterior")]
    [Explicit("Requires local sample cooked packages.")]
    public void SampleCookContainsEditableHierarchy(string name)
    {
        string path = Path.Combine(FindRepositoryRoot(), "NjulfHelloGame", "Cooked", "win-x64", "models", name + ".njmodel");
        using var reader = new CookedAssetReader(path, CookedAssetKind.Model);
        var manifest = CookedJson.Deserialize<CookedModelManifest>(
            reader.GetRequiredSection(CookedSectionIds.Manifest).Span, path, "manifest");
        Assert.That(manifest.Nodes, Is.Not.Empty, "Rebuild the cooker and recook: this package lacks object origins.");
        var indices = manifest.Nodes.Select(node => node.Index).ToHashSet();
        Assert.That(manifest.SubObjects.Where(item => item.SkinIndex < 0)
            .All(item => indices.Contains(item.NodeIndex)), Is.True);
        TestContext.WriteLine($"{name}: {manifest.Nodes.Count} nodes, {manifest.SubObjects.Count} primitives");
        var meshNodes = manifest.SubObjects.Select(item => item.NodeIndex).Distinct()
            .Select(index => manifest.Nodes.Single(node => node.Index == index)).ToArray();
        TestContext.WriteLine($"Mesh-node origins: {meshNodes.Select(node => node.WorldMatrix.Translation).Distinct().Count()} distinct across {meshNodes.Length} nodes");
        foreach (var node in meshNodes.Take(3))
            TestContext.WriteLine($"{node.Name}: {node.WorldMatrix.Translation}");
    }

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

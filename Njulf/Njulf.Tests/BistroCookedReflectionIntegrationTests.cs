using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BistroCookedReflectionIntegrationTests
{
    [Test]
    [Explicit("Requires the local Amazon Bistro source asset and its win-x64 cook.")]
    public void ExteriorCook_PreservesThinGlassAndImportSemantics()
    {
        string root = FindRepositoryRoot();
        string sourcePath = Path.Combine(
            root,
            "NjulfHelloGame",
            "Assets",
            "Bistro_v5_2",
            "BistroExterior.fbx");
        string modelPath = Path.Combine(
            root,
            "NjulfHelloGame",
            "Cooked",
            "win-x64",
            "models",
            "BistroExterior.njmodel");
        if (!File.Exists(sourcePath) || !File.Exists(modelPath))
        {
            Assert.Ignore(
                "The local Bistro source and win-x64 cooked package are required.");
        }

        var importerOptions = new ImporterOptions
        {
            Backend = ModelImportBackend.Assimp,
            AssimpMaterialTextureConvention =
                AssimpMaterialTextureConvention.AmazonBistro
        };
        ulong expectedImportContract = CookedModelImportContract.Compute(
            sourcePath,
            importerOptions);

        CookedModelManifest manifest;
        CookedAssetHeader header;
        using (var reader = new CookedAssetReader(
                   modelPath,
                   CookedAssetKind.Model,
                   CookedAssetReaderFlags.StrictSourceHash,
                   CookedHash.File(sourcePath)))
        {
            header = reader.Header;
            manifest = CookedJson.Deserialize<CookedModelManifest>(
                reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                modelPath,
                "manifest");
        }

        string materialPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(modelPath)!,
            manifest.Material.RelativePath));
        CookedMaterialTable materials = CookedPackage.LoadMaterials(
            materialPath,
            CookedAssetReaderFlags.StrictSourceHash,
            out _);
        ModelMaterial[] thinGlass = materials.Materials
            .Where(material => material.IsThinGlass)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(header.FormatMajor,
                Is.EqualTo(CookedFormatVersions.Model.Major));
            Assert.That(header.FormatMinor,
                Is.EqualTo(CookedFormatVersions.Model.Minor));
            Assert.That(header.ImportSettingsHash,
                Is.EqualTo(expectedImportContract));
            Assert.That(manifest.ImportSettingsHash,
                Is.EqualTo(expectedImportContract));
            Assert.That(thinGlass, Has.Length.EqualTo(4));
            Assert.That(thinGlass.All(material =>
                    material.AlphaMode == ModelAlphaMode.Blend),
                Is.True);
            Assert.That(thinGlass.All(material => material.DoubleSided),
                Is.True);
            Assert.That(thinGlass.All(material => material.Metallic == 0.0f),
                Is.True);
            Assert.That(thinGlass.All(material =>
                    material.GiTransmissionPolicy ==
                    ModelGiTransmissionPolicy.ThinSurface),
                Is.True);
            Assert.That(thinGlass.Any(material =>
                    material.Roughness <= 0.08f + 1.0e-6f &&
                    material.TransmissionFactor >= 0.94f - 1.0e-6f),
                Is.True,
                "The clear Bistro glass profile must remain a sharp dielectric.");
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

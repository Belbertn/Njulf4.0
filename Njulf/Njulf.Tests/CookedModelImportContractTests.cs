using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class CookedModelImportContractTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            nameof(CookedModelImportContractTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Compute_IsStableForEquivalentOptionsAndChangesWithMaterialConvention()
    {
        string source = Path.Combine(_directory, "BistroExterior.fbx");
        var automatic = new ImporterOptions
        {
            Backend = ModelImportBackend.Auto,
            PreferredFormat = " GLTF ",
            AssimpMaterialTextureConvention =
                AssimpMaterialTextureConvention.AmazonBistro
        };
        var explicitBackend = new ImporterOptions
        {
            Backend = ModelImportBackend.Assimp,
            PreferredFormat = "gltf",
            AssimpMaterialTextureConvention =
                AssimpMaterialTextureConvention.AmazonBistro
        };
        var wrongConvention = new ImporterOptions
        {
            Backend = ModelImportBackend.Assimp,
            PreferredFormat = "gltf",
            AssimpMaterialTextureConvention =
                AssimpMaterialTextureConvention.SpecularGbIsRoughnessMetallic
        };

        ulong automaticHash = CookedModelImportContract.Compute(source, automatic);
        ulong explicitHash = CookedModelImportContract.Compute(source, explicitBackend);
        ulong wrongHash = CookedModelImportContract.Compute(source, wrongConvention);

        Assert.Multiple(() =>
        {
            Assert.That(automaticHash, Is.EqualTo(explicitHash));
            Assert.That(wrongHash, Is.Not.EqualTo(explicitHash));
        });
    }

    [Test]
    public void Resolver_AcceptsMatchingContractAndRejectsDifferentConvention()
    {
        string source = CreateSource("BistroExterior.fbx");
        ImporterOptions correct = BistroOptions();
        string package = WriteCandidatePackage(
            source,
            CookedModelImportContract.Compute(source, correct));
        var resolver = new CookedContentResolver(_directory);

        CookedResolution matching = resolver.ResolveModel(
            "BistroExterior.fbx",
            source,
            strictSourceHash: true,
            CookedModelImportContract.Compute(source, correct));
        CookedResolution mismatched = resolver.ResolveModel(
            "BistroExterior.fbx",
            source,
            strictSourceHash: true,
            CookedModelImportContract.Compute(
                source,
                new ImporterOptions
                {
                    Backend = ModelImportBackend.Assimp,
                    AssimpMaterialTextureConvention =
                        AssimpMaterialTextureConvention.SpecularGbIsRoughnessMetallic
                }));

        Assert.Multiple(() =>
        {
            Assert.That(matching.Status, Is.EqualTo(CookedResolutionStatus.Found));
            Assert.That(matching.PackagePath, Is.EqualTo(package));
            Assert.That(mismatched.Status, Is.EqualTo(CookedResolutionStatus.Invalid));
            Assert.That(mismatched.Reason, Does.Contain("import contract mismatch"));
            Assert.That(mismatched.Reason, Does.Contain("recook"));
        });
    }

    [Test]
    public void Resolver_RejectsLegacyModelAtTheV2HardBoundary()
    {
        string source = CreateSource("BistroExterior.fbx");
        ImporterOptions options = BistroOptions();
        string package = WriteCandidatePackage(
            source,
            CookedModelImportContract.Compute(source, options));
        PatchModelMinorVersion(package, 3);
        var resolver = new CookedContentResolver(_directory);

        CookedResolution sourceRequest = resolver.ResolveModel(
            "BistroExterior.fbx",
            source,
            strictSourceHash: true,
            CookedModelImportContract.Compute(source, options));
        CookedResolution directRequest = resolver.ResolveModel(
            package,
            package,
            strictSourceHash: true,
            expectedImportContractHash: ulong.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(sourceRequest.Status, Is.EqualTo(CookedResolutionStatus.Invalid));
            Assert.That(sourceRequest.Reason, Does.Contain("format major"));
            Assert.That(directRequest.Status, Is.EqualTo(CookedResolutionStatus.Invalid));
            Assert.That(directRequest.ExpectedImportContractHash, Is.Null);
        });
    }

    [Test]
    public void Migrator_RequiresLegacyModelsToBeRecooked()
    {
        string source = CreateSource("legacy.fbx");
        string package = WriteCandidatePackage(source, 42UL);
        PatchModelMinorVersion(package, 3);

        Assert.That(
            () => CookedAssetMigrator.MigrateFile(
                package,
                Path.Combine(_directory, "migrated.njmodel")),
            Throws.TypeOf<NotSupportedException>()
                .With.Message.Contains("Recook"));
    }

    private ImporterOptions BistroOptions() => new()
    {
        Backend = ModelImportBackend.Assimp,
        AssimpMaterialTextureConvention =
            AssimpMaterialTextureConvention.AmazonBistro
    };

    private string CreateSource(string fileName)
    {
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "semantic-contract-source");
        return path;
    }

    private string WriteCandidatePackage(string source, ulong importContractHash)
    {
        string modelDirectory = Path.Combine(
            _directory,
            "Cooked",
            CookedPlatform.Current,
            "models");
        Directory.CreateDirectory(modelDirectory);
        string path = Path.Combine(
            modelDirectory,
            Path.GetFileNameWithoutExtension(source) + ".njmodel");
        using var writer = new CookedAssetWriter(
            path,
            CookedAssetKind.Model,
            sourceHash: CookedHash.File(source),
            importSettingsHash: importContractHash);
        writer.Complete();
        return path;
    }

    private static void PatchModelMinorVersion(string packagePath, ushort minor)
    {
        byte[] bytes = File.ReadAllBytes(packagePath);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), minor);
        File.WriteAllBytes(packagePath, bytes);
    }
}

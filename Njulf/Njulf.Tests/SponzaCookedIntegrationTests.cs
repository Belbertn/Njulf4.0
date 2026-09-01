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

using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class CookedAssetMigratorTransactionTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "cooked-migrator-transaction-tests",
            TestContext.CurrentContext.Test.ID,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Test]
    public void MigrateTree_WhenStagingFails_PreservesExistingOutputTree()
    {
        string source = Path.Combine(_directory, "source");
        string output = Path.Combine(_directory, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        WriteCookedAsset(
            Path.Combine(source, "a-valid.njtex"));
        File.WriteAllBytes(
            Path.Combine(source, "z-invalid.njmesh"),
            [1, 2, 3, 4]);
        string marker = Path.Combine(output, "published.marker");
        File.WriteAllText(marker, "previous-generation");

        Assert.That(
            () => CookedAssetMigrator.MigrateTree(
                source,
                output),
            Throws.Exception);

        Assert.Multiple(() =>
        {
            Assert.That(
                File.ReadAllText(marker),
                Is.EqualTo("previous-generation"));
            Assert.That(
                File.Exists(Path.Combine(output, "a-valid.njtex")),
                Is.False);
            Assert.That(
                Directory.Exists(
                    Path.Combine(
                        _directory,
                        ".output.migration-staging")),
                Is.False);
            Assert.That(
                Directory.Exists(
                    Path.Combine(
                        _directory,
                        ".output.migration-backup")),
                Is.False);
        });
    }

    [Test]
    public void MigrateTree_SuccessfullyReplacesExistingTreeAsOnePublishedGeneration()
    {
        string source = Path.Combine(_directory, "source");
        string output = Path.Combine(_directory, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        WriteCookedAsset(
            Path.Combine(source, "asset.njtex"));
        File.WriteAllText(
            Path.Combine(source, "metadata.txt"),
            "new-generation");
        File.WriteAllText(
            Path.Combine(output, "published.marker"),
            "previous-generation");

        CookedMigrationReport report =
            CookedAssetMigrator.MigrateTree(
                source,
                output);

        using var migrated = new CookedAssetReader(
            Path.Combine(output, "asset.njtex"),
            CookedAssetKind.Texture);
        Assert.Multiple(() =>
        {
            Assert.That(report.MigratedFiles, Is.EqualTo(1));
            Assert.That(report.CopiedFiles, Is.EqualTo(1));
            Assert.That(
                File.ReadAllText(
                    Path.Combine(output, "metadata.txt")),
                Is.EqualTo("new-generation"));
            Assert.That(
                File.Exists(
                    Path.Combine(output, "published.marker")),
                Is.False);
            Assert.That(
                Directory.Exists(
                    Path.Combine(
                        _directory,
                        ".output.migration-staging")),
                Is.False);
            Assert.That(
                Directory.Exists(
                    Path.Combine(
                        _directory,
                        ".output.migration-backup")),
                Is.False);
        });
    }

    private static void WriteCookedAsset(string path)
    {
        using var writer = new CookedAssetWriter(
            path,
            CookedAssetKind.Texture);
        writer.WriteSection(
            CookedSectionIds.Metadata,
            CookedSectionFlags.Required,
            new byte[] { 1, 2, 3, 4 });
        writer.Complete();
    }
}

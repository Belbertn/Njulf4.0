using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AssetArtifactFileIoTests
{
    [Test]
    public void JsonArtifactWriters_ReplaceDurablyAndLeaveNoTemporaryFiles()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string databasePath = Path.Combine(
                directory,
                "assetdb.njassetdb");
            var database = new CookedAssetDatabase();
            database.Assets["model.gltf"] = new CookedAssetDatabaseEntry
            {
                SourcePath = "model.gltf",
                Status = "First"
            };
            database.SaveAtomic(databasePath);
            database.Assets["model.gltf"] =
                database.Assets["model.gltf"] with { Status = "Succeeded" };
            database.SaveAtomic(databasePath);

            string cookReportPath = Path.Combine(
                directory,
                "model.cook-report.json");
            AssetCookReportJson.WriteAtomic(
                cookReportPath,
                CreateCookReport("First"));
            AssetCookReportJson.WriteAtomic(
                cookReportPath,
                CreateCookReport("Succeeded"));

            string validationPath = Path.Combine(
                directory,
                "asset-validation.json");
            AssetValidationJson.WriteReport(
                validationPath,
                CreateValidationReport("first"));
            AssetValidationJson.WriteReport(
                validationPath,
                CreateValidationReport("second"));

            Assert.Multiple(() =>
            {
                Assert.That(
                    CookedAssetDatabase.Load(databasePath)
                        .Assets["model.gltf"].Status,
                    Is.EqualTo("Succeeded"));
                Assert.That(
                    AssetCookReportJson.Read(cookReportPath).Status,
                    Is.EqualTo("Succeeded"));
                Assert.That(
                    AssetValidationJson.ReadReport(validationPath).RootPath,
                    Is.EqualTo("second"));
                Assert.That(
                    Directory.EnumerateFiles(
                        directory,
                        "*.tmp",
                        SearchOption.TopDirectoryOnly),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void JsonArtifactReaders_RejectOversizedFilesBeforeParsing()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string databasePath = Path.Combine(
                directory,
                "oversized.njassetdb");
            string cookReportPath = Path.Combine(
                directory,
                "oversized.cook-report.json");
            string validationPath = Path.Combine(
                directory,
                "oversized-validation.json");
            CreateSparseFile(
                databasePath,
                CookedAssetDatabase.MaximumDatabaseBytes + 1L);
            CreateSparseFile(
                cookReportPath,
                AssetCookReportJson.MaximumReportBytes + 1L);
            CreateSparseFile(
                validationPath,
                AssetValidationJson.MaximumReportBytes + 1L);

            Assert.Multiple(() =>
            {
                Assert.That(
                    () => CookedAssetDatabase.Load(databasePath),
                    Throws.TypeOf<InvalidDataException>()
                        .With.Message.Contains("exceeding"));
                Assert.That(
                    () => AssetCookReportJson.Read(cookReportPath),
                    Throws.TypeOf<InvalidDataException>()
                        .With.Message.Contains("exceeding"));
                Assert.That(
                    () => AssetValidationJson.ReadReport(validationPath),
                    Throws.TypeOf<InvalidDataException>()
                        .With.Message.Contains("exceeding"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TextureCooker_RejectsOversizedFileSourceBeforeAllocation()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "oversized.png");
            CreateSparseFile(
                path,
                AssetArtifactFileIo.MaximumCookSourceBytes + 1L);
            var source = new ModelTextureSource
            {
                FilePath = path,
                CacheIdentity = path
            };

            Assert.That(
                () => TextureCooker.AnalyzeTransportStatistics(
                    source,
                    new TextureCookOptions()),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("exceeding"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AtomicCopy_CopiesExactlyTheAdmittedSnapshotAndHonorsItsLimit()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string sourcePath = Path.Combine(directory, "source.bin");
            string destinationPath = Path.Combine(directory, "destination.bin");
            byte[] source = new byte[384 * 1024 + 17];
            for (int index = 0; index < source.Length; index++)
                source[index] = unchecked((byte)(index * 31 + 7));
            File.WriteAllBytes(sourcePath, source);
            File.WriteAllBytes(destinationPath, [9, 8, 7]);

            AssetArtifactFileIo.CopyAtomic(
                sourcePath,
                destinationPath,
                source.Length,
                "Test artifact");

            Assert.Multiple(() =>
            {
                Assert.That(
                    File.ReadAllBytes(destinationPath),
                    Is.EqualTo(source));
                Assert.That(
                    Directory.EnumerateFiles(directory)
                        .Select(Path.GetFileName)
                        .Where(name => name is not null &&
                            name.Contains(".copy", StringComparison.Ordinal)),
                    Is.Empty);
            });

            File.WriteAllBytes(destinationPath, [1, 2, 3, 4]);
            Assert.That(
                () => AssetArtifactFileIo.CopyAtomic(
                    sourcePath,
                    destinationPath,
                    source.Length - 1L,
                    "Test artifact"),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("exceeding"));
            Assert.That(
                File.ReadAllBytes(destinationPath),
                Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase("unknown")]
    [TestCase("case")]
    [TestCase("duplicate")]
    public void AssetValidationReport_RejectsAmbiguousJsonSchema(
        string mutation)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "validation.json");
            AssetValidationJson.WriteReport(
                path,
                CreateValidationReport("root"));
            string valid = File.ReadAllText(path);
            string invalid = mutation switch
            {
                "unknown" => valid.Replace(
                    "{",
                    "{\"unknown\":true,",
                    StringComparison.Ordinal),
                "case" => valid.Replace(
                    "\"schemaVersion\"",
                    "\"SchemaVersion\"",
                    StringComparison.Ordinal),
                "duplicate" => valid.Replace(
                    "\"schemaVersion\": 1",
                    "\"schemaVersion\": 1, \"schemaVersion\": 1",
                    StringComparison.Ordinal),
                _ => throw new AssertionException(
                    $"Unknown mutation '{mutation}'.")
            };
            File.WriteAllText(path, invalid);

            Assert.That(
                () => AssetValidationJson.ReadReport(path),
                Throws.TypeOf<InvalidDataException>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AssetValidationReport_UsesIsolatedOptionsAndRejectsFalseSummary()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            JsonSerializerOptions callerOptions =
                AssetValidationJson.Options;
            callerOptions.PropertyNameCaseInsensitive = true;
            callerOptions.UnmappedMemberHandling =
                System.Text.Json.Serialization
                    .JsonUnmappedMemberHandling.Skip;

            string path = Path.Combine(directory, "validation.json");
            AssetValidationJson.WriteReport(
                path,
                CreateValidationReport("root"));
            string invalid = File.ReadAllText(path).Replace(
                "\"totalCount\": 0",
                "\"totalCount\": 1",
                StringComparison.Ordinal);
            File.WriteAllText(path, invalid);

            Assert.That(
                () => AssetValidationJson.ReadReport(path),
                Throws.TypeOf<InvalidDataException>()
                    .With.Message.Contains("summary"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AssetCookReport CreateCookReport(string status) => new(
        "model.gltf",
        Guid.NewGuid(),
        status,
        ModelImportBackend.Auto,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<CookedTextureReport>(),
        Array.Empty<string>(),
        new Dictionary<string, ulong>());

    private static AssetValidationReport CreateValidationReport(
        string rootPath) => new(
        1,
        DateTimeOffset.UtcNow,
        rootPath,
        Array.Empty<AssetValidationEntry>(),
        new AssetValidationSummary(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0));

    private static void CreateSparseFile(string path, long length)
    {
        using FileStream stream = File.Create(path);
        stream.SetLength(length);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-artifact-io-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

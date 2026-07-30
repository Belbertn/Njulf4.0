using System.Security.Cryptography;
using System.Text;
using Njulf.Assets;
using Njulf.Assets.Validation;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class KhronosMaterialGiConformanceTests
{
    [Test]
    public void SourceControlledManifest_PinsOfficialCommitHashesLicensesAndSemantics()
    {
        string path = FindRepoFile("Njulf.AssetTool", "khronos-material-gi-assets.json");
        KhronosMaterialGiManifest manifest = KhronosMaterialGiConformance.LoadManifest(path);

        Assert.Multiple(() =>
        {
            Assert.That(manifest.SchemaVersion, Is.EqualTo(KhronosMaterialGiConformance.CurrentSchemaVersion));
            Assert.That(manifest.Repository, Is.EqualTo(KhronosMaterialGiConformance.OfficialRepository));
            Assert.That(manifest.Commit, Has.Length.EqualTo(40));
            Assert.That(manifest.Assets.Select(static asset => asset.Name), Is.EquivalentTo(new[]
            {
                "UnlitTest",
                "EmissiveStrengthTest",
                "AlphaBlendModeTest"
            }));
            Assert.That(manifest.Assets, Has.All.Matches<KhronosMaterialGiAsset>(static asset =>
                asset.Sha256.Length == 64 &&
                asset.Bytes > 0 &&
                asset.License.Contains("CC BY 4.0", StringComparison.Ordinal)));
            Assert.That(manifest.Assets.Select(asset =>
                    KhronosMaterialGiConformance.BuildDownloadUrl(manifest, asset)),
                Has.All.Contains(manifest.Commit));
        });
    }

    [Test]
    public void PayloadAuthentication_IsLengthAndSha256FailClosed()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        var asset = new KhronosMaterialGiAsset
        {
            Name = "fixture",
            RelativePath = "Models/fixture/glTF-Binary/fixture.glb",
            Bytes = payload.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            License = "test"
        };

        Assert.DoesNotThrow(() => KhronosMaterialGiConformance.VerifyPayload(asset, payload));
        InvalidDataException changed = Assert.Throws<InvalidDataException>(() =>
            KhronosMaterialGiConformance.VerifyPayload(asset, new byte[] { 1, 2, 3, 4, 6 }))!;
        InvalidDataException truncated = Assert.Throws<InvalidDataException>(() =>
            KhronosMaterialGiConformance.VerifyPayload(asset, payload[..^1]))!;

        Assert.Multiple(() =>
        {
            Assert.That(changed.Message, Does.Contain("SHA-256"));
            Assert.That(truncated.Message, Does.Contain("expected 5"));
        });
    }

    [Test]
    public void ImportedSemanticGate_RejectsMissingOfficialMaterialMeaning()
    {
        var asset = new KhronosMaterialGiAsset
        {
            Name = "semantics",
            Expectations = new KhronosMaterialGiExpectations
            {
                MinimumMaterialCount = 3,
                MinimumUnlitCount = 1,
                MinimumEmissiveStrengthCount = 1,
                MinimumMaximumEmissiveStrength = 10f,
                MinimumMaskCount = 1,
                MinimumBlendCount = 1,
                MinimumDoubleSidedCount = 2
            }
        };
        var invalid = new ModelMesh();
        invalid.Materials.Add(new ModelMaterial());
        var valid = new ModelMesh();
        valid.Materials.Add(new ModelMaterial { Unlit = true });
        valid.Materials.Add(new ModelMaterial
        {
            FeatureFlags = 1u << 14,
            EmissiveStrength = 10f,
            AlphaMode = ModelAlphaMode.Mask,
            DoubleSided = true
        });
        valid.Materials.Add(new ModelMaterial
        {
            AlphaMode = ModelAlphaMode.Blend,
            DoubleSided = true
        });

        IReadOnlyList<string> errors = KhronosMaterialGiConformance.ValidateImported(asset, invalid);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Not.Empty);
            Assert.That(errors, Has.Some.Contains("unlit"));
            Assert.That(errors, Has.Some.Contains("emissive-strength"));
            Assert.That(errors, Has.Some.Contains("masked"));
            Assert.That(KhronosMaterialGiConformance.ValidateImported(asset, valid), Is.Empty);
        });
    }

    [Test]
    public void ManifestValidation_RejectsMovingBranchesAndTraversal()
    {
        var manifest = new KhronosMaterialGiManifest
        {
            SchemaVersion = 1,
            Repository = KhronosMaterialGiConformance.OfficialRepository,
            Commit = "main",
            Assets =
            [
                new KhronosMaterialGiAsset
                {
                    Name = "bad",
                    RelativePath = "../bad.glb",
                    Sha256 = new string('0', 64),
                    Bytes = 1,
                    License = "test"
                }
            ]
        };

        IReadOnlyList<string> errors = KhronosMaterialGiConformance.ValidateManifest(manifest);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Some.Contains("40-character"));
            Assert.That(errors, Has.Some.Contains("unsafe"));
        });
    }

    [Test]
    public void ManifestLoader_IsBoundedStrictAndReturnsExactSnapshotIdentity()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "khronos-manifest-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string validPath = FindRepoFile(
                "Njulf.AssetTool",
                "khronos-material-gi-assets.json");
            KhronosMaterialGiManifestSnapshot snapshot =
                KhronosMaterialGiConformance.LoadManifestSnapshot(validPath);
            byte[] exactBytes = File.ReadAllBytes(validPath);
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Path, Is.EqualTo(Path.GetFullPath(validPath)));
                Assert.That(
                    snapshot.Sha256,
                    Is.EqualTo(
                        Convert.ToHexString(SHA256.HashData(exactBytes))
                            .ToLowerInvariant()));
                Assert.That(snapshot.Manifest.Assets, Is.Not.Empty);
            });

            string unknownPath = Path.Combine(directory, "unknown.json");
            File.WriteAllText(
                unknownPath,
                """
                {
                  "schemaVersion": 1,
                  "repository": "https://github.com/KhronosGroup/glTF-Sample-Assets",
                  "commit": "0000000000000000000000000000000000000000",
                  "assets": [],
                  "unexpected": true
                }
                """,
                Encoding.UTF8);
            InvalidDataException unknown = Assert.Throws<InvalidDataException>(
                () => KhronosMaterialGiConformance.LoadManifest(unknownPath))!;

            string oversizedPath = Path.Combine(directory, "oversized.json");
            using (var stream = new FileStream(
                       oversizedPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.SetLength(
                    KhronosMaterialGiConformance.MaximumManifestBytes + 1L);
            }
            InvalidDataException oversized = Assert.Throws<InvalidDataException>(
                () => KhronosMaterialGiConformance.LoadManifest(oversizedPath))!;

            Assert.Multiple(() =>
            {
                Assert.That(unknown.Message, Does.Contain("not valid JSON"));
                Assert.That(oversized.Message, Does.Contain("expected a size"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ManifestValidation_RejectsUnboundedOrInvalidSemanticMetadata()
    {
        KhronosMaterialGiAsset asset = new()
        {
            Name = "fixture",
            RelativePath = "Models/fixture/glTF-Binary/fixture.glb",
            Sha256 = new string('0', 64),
            Bytes = 1,
            License = "test",
            Expectations = new KhronosMaterialGiExpectations
            {
                MinimumMaterialCount = -1,
                MinimumMaximumEmissiveStrength = float.NaN
            }
        };
        var manifest = new KhronosMaterialGiManifest
        {
            SchemaVersion = KhronosMaterialGiConformance.CurrentSchemaVersion,
            Repository = KhronosMaterialGiConformance.OfficialRepository,
            Commit = new string('0', 40),
            Assets = Enumerable.Repeat(
                    asset,
                    KhronosMaterialGiConformance.MaximumAssetCount + 1)
                .ToArray()
        };

        IReadOnlyList<string> tooMany =
            KhronosMaterialGiConformance.ValidateManifest(manifest);
        IReadOnlyList<string> invalidExpectations =
            KhronosMaterialGiConformance.ValidateManifest(
                manifest with { Assets = [asset] });

        Assert.Multiple(() =>
        {
            Assert.That(tooMany, Has.Some.Contains("between 1 and"));
            Assert.That(
                invalidExpectations,
                Has.Some.Contains("semantic expectations"));
        });
    }

    private static string FindRepoFile(params string[] relative)
    {
        string? current = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string candidate = Path.Combine(new[] { current }.Concat(relative).ToArray());
            if (File.Exists(candidate))
                return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relative)}'.");
    }
}

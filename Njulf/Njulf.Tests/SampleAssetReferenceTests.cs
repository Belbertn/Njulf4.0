using System.Text.Json;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class SampleAssetReferenceTests
    {
        [Test]
        public void SampleGltfTextureUris_PointToExistingFiles()
        {
            string sampleDirectory = FindSampleDirectory();
            foreach (string gltfPath in EnumerateActiveSampleGltfs(sampleDirectory))
            {
                using FileStream stream = File.OpenRead(gltfPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                if (!document.RootElement.TryGetProperty("images", out JsonElement images) ||
                    images.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement image in images.EnumerateArray())
                {
                    if (!image.TryGetProperty("uri", out JsonElement uriElement))
                        continue;

                    string? uri = uriElement.GetString();
                    if (string.IsNullOrWhiteSpace(uri) ||
                        uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gltfPath)!, uri.Replace('/', Path.DirectorySeparatorChar)));
                    Assert.That(File.Exists(resolvedPath), Is.True, $"{Path.GetFileName(gltfPath)} references missing texture '{uri}'.");
                }
            }
        }

        [Test]
        public void SampleAssets_DoNotKeepKnownExactDuplicateFiles()
        {
            string sampleDirectory = FindSampleDirectory();

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(Path.Combine(sampleDirectory, "Old", "vintage_video_camera_2k.gltf")), Is.False);
                Assert.That(File.Exists(Path.Combine(sampleDirectory, "vintage_video_camera_2k.gltf")), Is.False);
                Assert.That(File.Exists(Path.Combine(sampleDirectory, "textures", "col_brickwall_01_Roughnesscolumn_brickwall_01_Metalness.png")), Is.False);
            });
        }

        [Test]
        public void SampleFoliage_UsesValidatedNonUnrealGrassVariant()
        {
            string sampleDirectory = FindSampleDirectory();

            Assert.Multiple(() =>
            {
                Assert.That(
                    File.Exists(Path.Combine(sampleDirectory, "Assets", "ribbon_grass_tbdpec3r_ue_low", "standard", "tbdpec3r_tier_3_nonUE.gltf")),
                    Is.True);
                Assert.That(
                    EnumerateActiveSampleGltfs(sampleDirectory),
                    Does.Contain(Path.Combine(sampleDirectory, "Assets", "ribbon_grass_tbdpec3r_ue_low", "standard", "tbdpec3r_tier_3_nonUE.gltf")));
                Assert.That(
                    EnumerateActiveSampleGltfs(sampleDirectory),
                    Does.Not.Contain(Path.Combine(sampleDirectory, "Assets", "ribbon_grass_tbdpec3r_ue_low", "tbdpec3r_tier_3.gltf")));
            });
        }

        private static IEnumerable<string> EnumerateActiveSampleGltfs(string sampleDirectory)
        {
            yield return Path.Combine(sampleDirectory, "NewSponza_Main_glTF_003.gltf");
            yield return Path.Combine(sampleDirectory, "NewSponza_Curtains_glTF.gltf");
            yield return Path.Combine(sampleDirectory, "Assets", "ribbon_grass_tbdpec3r_ue_low", "standard", "tbdpec3r_tier_3_nonUE.gltf");
        }

        private static string FindSampleDirectory()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "NjulfHelloGame");
                if (Directory.Exists(candidate))
                    return candidate;

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate NjulfHelloGame from the test output directory.");
        }
    }
}

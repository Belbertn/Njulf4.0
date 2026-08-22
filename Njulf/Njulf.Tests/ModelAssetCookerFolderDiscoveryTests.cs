using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ModelAssetCookerFolderDiscoveryTests
{
    [Test]
    public void Discovery_IgnoresCopiedModelsUnderBinAndObj()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            nameof(ModelAssetCookerFolderDiscoveryTests),
            Guid.NewGuid().ToString("N"));
        try
        {
            string authoredModel = WriteModelPlaceholder(root, "scene.gltf");
            string nestedModel = WriteModelPlaceholder(
                root,
                Path.Combine("Assets", "environment.fbx"));
            WriteModelPlaceholder(
                root,
                Path.Combine("bin", "Development", "scene.gltf"));
            WriteModelPlaceholder(
                root,
                Path.Combine("OBJ", "Development", "environment.fbx"));

            string[] discovered =
                ModelAssetCooker.DiscoverFolderSources(root);

            Assert.That(discovered, Is.EqualTo(new[]
            {
                nestedModel,
                authoredModel
            }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string WriteModelPlaceholder(
        string root,
        string relativePath)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}

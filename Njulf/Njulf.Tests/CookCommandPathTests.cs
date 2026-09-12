using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class CookCommandPathTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void BuildOutputUsesOriginalSourceOnlyWhenItExists(bool originalExists)
    {
        string project = Path.Combine(TestContext.CurrentContext.TestDirectory, "cook-command-tests", Guid.NewGuid().ToString("N"));
        string runtime = Path.Combine(project, "bin", "Development", "net10.0");
        string relative = Path.Combine("Assets", "Bistro", "BistroExterior.fbx");
        try
        {
            Directory.CreateDirectory(Path.Combine(runtime, "Assets", "Bistro"));
            Directory.CreateDirectory(Path.Combine(project, "Assets", "Bistro"));
            File.WriteAllText(Path.Combine(project, "Game.csproj"), "<Project />");
            string copy = Path.Combine(runtime, relative);
            File.WriteAllText(copy, "copied asset");
            string original = Path.Combine(project, relative);
            if (originalExists) File.WriteAllText(original, "source asset");

            var result = ContentManager.ResolveCookCommandPaths(copy, runtime);
            Assert.That(result.Source, Is.EqualTo(originalExists ? original : copy));
            Assert.That(result.Root, Is.EqualTo(originalExists ? project : runtime));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }
}

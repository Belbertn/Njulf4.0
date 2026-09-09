using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Njulf.Core;
using Njulf.Graphics;
using Njulf.Input;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class FrameworkArchitectureTests
{
    [Test]
    public void FoundationAndGraphics_HaveOnlyTheirAllowedDependencies()
    {
        AssertReferences(typeof(GameTime).Assembly, []);
        AssertReferences(typeof(GraphicsDevice).Assembly, ["Njulf.Core"]);
        AssertPackages("Njulf.Core", []);
        AssertPackages("Njulf.Graphics", ["Njulf.Core"]);
    }

    [Test]
    public void RuntimeClosure_ExcludesEditorAndOfflineTools()
    {
        var visited = new HashSet<string>();
        Visit(typeof(Game).Assembly);
        Assert.That(visited, Does.Not.Contain("Njulf.Editor"));
        Assert.That(visited, Does.Not.Contain("Njulf.Assets.Tooling"));
        Assert.That(visited, Does.Not.Contain("Njulf.AssetTool"));
        Assert.That(visited, Does.Not.Contain("Njulf.ShaderBuild"));
        Assert.That(typeof(Njulf.Assets.Cooked.ModelAssetCooker).Assembly.GetName().Name,
            Is.EqualTo("Njulf.Assets.Tooling"));
        Assert.That(typeof(Njulf.Assets.Cooked.TextureSourceDecoder).Assembly.GetName().Name,
            Is.EqualTo("Njulf.Assets"));

        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.GetName().Name!)) return;
            foreach (var reference in assembly.GetReferencedAssemblies())
                if (reference.Name!.StartsWith("Njulf", StringComparison.Ordinal)) Visit(Assembly.Load(reference));
        }
    }

    [Test]
    public void PublicResources_AreContractsAndNativeImplementationsStayInternal()
    {
        Type[] contracts = [typeof(GraphicsDevice), typeof(Mesh), typeof(Material), typeof(Texture),
            typeof(RenderTarget2D), typeof(GraphicsBuffer), typeof(GraphicsSettingsController)];
        foreach (var contract in contracts)
        {
            Assert.That(contract.Assembly.GetName().Name, Is.EqualTo("Njulf.Graphics"));
            Assert.That(contract.IsAbstract && contract.IsPublic, Is.True, contract.Name);
            Assert.That(contract.GetConstructors(), Is.Empty, contract.Name);
            var backend = typeof(Njulf.Rendering.VulkanRenderer).Assembly.GetType("Njulf.Graphics.Vulkan" + contract.Name);
            Assert.That(backend, Is.Not.Null, contract.Name);
            Assert.That(backend!.IsVisible, Is.False, contract.Name);
        }
        Assert.That(typeof(Njulf.Rendering.RenderingOptions).Namespace, Is.EqualTo("Njulf.Rendering"));
        Assert.That(typeof(InputAction).Assembly.GetType("Njulf.Input.Action"), Is.Null);
        Assert.That(typeof(Njulf.Core.Scene.RenderObject).GetProperty("Mesh")!.PropertyType, Is.EqualTo(typeof(IMesh)));
        Assert.That(typeof(Njulf.Core.Scene.RenderObject).GetProperty("Material")!.PropertyType, Is.EqualTo(typeof(IMaterial)));
        Assert.That((int)TextureColorSpace.Linear, Is.Zero);
        Assert.That((int)TextureColorSpace.Srgb, Is.EqualTo(1));
        Assert.That((int)TextureColorSpace.HdrLinear, Is.EqualTo(2));
    }

    [Test]
    public void CriticalIntelliSense_IsEmittedBesideAssemblies()
    {
        CheckDocs(typeof(GameTime).Assembly, ["T:Njulf.Core.GameTime", "T:Njulf.Core.Math.Matrix4x4"]);
        CheckDocs(typeof(Game).Assembly, ["T:Njulf.Core.Game", "P:Njulf.Core.Game.GraphicsDevice",
            "P:Njulf.Core.Game.ContentUploadCpuBudget", "M:Njulf.Core.Game.Run", "M:Njulf.Core.Game.LoadAsync(System.Threading.CancellationToken)"]);
        CheckDocs(typeof(InputAction).Assembly, ["T:Njulf.Input.InputAction", "P:Njulf.Input.InputAction.WasPressed",
            "T:Njulf.Input.InputBinding", "M:Njulf.Input.IInputManager.CreateAction(System.String)"]);
        CheckDocs(typeof(GraphicsDevice).Assembly, ["T:Njulf.Graphics.GraphicsDevice", "T:Njulf.Graphics.Mesh",
            "M:Njulf.Graphics.Mesh.Dispose", "P:Njulf.Graphics.ITexture.Width", "T:Njulf.Graphics.GraphicsSettingsChange"]);
        CheckDocs(typeof(Njulf.Assets.IContentManager).Assembly, ["T:Njulf.Assets.IContentManager",
            "M:Njulf.Assets.IContentManager.Load``1(System.String)"]);
    }

    private static void CheckDocs(Assembly assembly, string[] names)
    {
        string file = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.That(File.Exists(file), Is.True, file);
        var members = XDocument.Load(file).Descendants("member").ToDictionary(x => (string)x.Attribute("name")!);
        foreach (string name in names)
        {
            Assert.That(members.ContainsKey(name), Is.True, name);
            Assert.That(members[name].Element("summary")?.Value.Trim(), Is.Not.Null.And.Not.Empty, name);
        }
    }

    private static void AssertReferences(Assembly assembly, string[] allowed)
    {
        var external = assembly.GetReferencedAssemblies().Select(x => x.Name!)
            .Where(x => !x.StartsWith("System", StringComparison.Ordinal) && x != "netstandard");
        Assert.That(external, Is.EquivalentTo(allowed));
    }

    // Evaluated restore graphs catch unused PackageReferences that assembly reflection cannot see.
    private static void AssertPackages(string project, string[] allowed)
    {
        var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Njulf.sln"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root!.FullName, project, "obj", "project.assets.json")));
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().Select(x => x.Name.Split('/')[0]);
        Assert.That(libraries, Is.EquivalentTo(allowed));
    }
}

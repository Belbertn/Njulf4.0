using System.Text.Json;
using Njulf.Assets;
using Njulf.Assets.Scenes;
using Njulf.Graphics;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class ContentDiagnosticTests
{
    [TestCase("broken.njscene.json")]
    [TestCase("broken.njeffect.json")]
    public void MalformedJsonReportsFileAndParserPositionWithoutLosingException(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + " " + name);
        try
        {
            File.WriteAllText(path, "{\n  !\n}");
            Exception error;
            if (name.EndsWith(".njscene.json"))
                error = Assert.Throws<JsonException>(() => SceneDocumentJson.Read(path))!;
            else
            {
                using var content = new ContentManager(Path.GetDirectoryName(path)!);
                error = Assert.Throws<InvalidDataException>(() => content.Load<ShaderEffectAsset>(path))!;
            }
            Assert.That(error.Message, Does.Contain(path + "(2,3): error NJCONTENT:"));
            Assert.That(error.InnerException, Is.InstanceOf<JsonException>());
            Assert.That(((JsonException)error.InnerException!).LineNumber, Is.EqualTo(1));
        }
        finally { File.Delete(path); }
    }
}

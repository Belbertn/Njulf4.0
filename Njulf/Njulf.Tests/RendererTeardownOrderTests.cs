using NUnit.Framework;
using System.Text.RegularExpressions;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererTeardownOrderTests
{
    [Test]
    public void OwnedResourceManagers_RetireBeforeBindlessAndBufferDependencies()
    {
        string source = File.ReadAllText(
            FindSourceFile(
                "Njulf.Rendering",
                "VulkanRenderer.cs"));
        int material =
            source.IndexOf(
                "_materialManager.Dispose",
                StringComparison.Ordinal);
        int mesh =
            source.IndexOf(
                "_meshManager.Dispose",
                StringComparison.Ordinal);
        int pendingTextureRetirements =
            source.IndexOf(
                "_textureManager.FlushPendingTextureRetirements",
                StringComparison.Ordinal);
        int deleter =
            source.IndexOf(
                "_deleter.Dispose",
                StringComparison.Ordinal);
        int texture =
            source.IndexOf(
                "_textureManager.Dispose",
                StringComparison.Ordinal);
        int bindless =
            source.IndexOf(
                "_bindlessHeap.Dispose",
                StringComparison.Ordinal);
        int buffers =
            source.IndexOf(
                "_bufferManager.Dispose",
                StringComparison.Ordinal);
        string normalizedSource =
            Regex.Replace(
                source,
                @"\s+",
                " ");

        Assert.Multiple(() =>
        {
            Assert.That(material, Is.GreaterThanOrEqualTo(0));
            Assert.That(mesh, Is.GreaterThan(material));
            Assert.That(
                pendingTextureRetirements,
                Is.GreaterThan(mesh));
            Assert.That(
                deleter,
                Is.GreaterThan(
                    pendingTextureRetirements));
            Assert.That(texture, Is.GreaterThan(deleter));
            Assert.That(bindless, Is.GreaterThan(texture));
            Assert.That(buffers, Is.GreaterThan(bindless));
            Assert.That(
                normalizedSource,
                Does.Contain(
                    "AddResourceStage( \"material-manager\", _materialManager.Dispose, \"model-upload-service\");"));
            Assert.That(
                normalizedSource,
                Does.Contain(
                    "AddResourceStage( \"mesh-manager\", _meshManager.Dispose, \"material-manager\");"));
            Assert.That(
                normalizedSource,
                Does.Contain(
                    "AddResourceStage( \"texture-pending-retirements\", _textureManager.FlushPendingTextureRetirements, \"mesh-manager\");"));
            Assert.That(
                normalizedSource,
                Does.Contain(
                    "AddResourceStage( \"deferred-deleter\", _deleter.Dispose, \"texture-pending-retirements\");"));
            Assert.That(
                normalizedSource,
                Does.Contain(
                    "AddResourceStage( \"texture-manager\", _textureManager.Dispose, \"deferred-deleter\");"));
            Assert.That(
                source,
                Does.Not.Contain("_deleter.Cleanup"));
        });
    }

    [Test]
    public void PublicOperationalMethods_FailClosedAfterDisposalStarts()
    {
        string source = File.ReadAllText(
            FindSourceFile(
                "Njulf.Rendering",
                "VulkanRenderer.cs"));
        string[] signatures =
        {
            "public void QueueOverlayDrawData(",
            "public int CreateOverlayTexture(",
            "public void RequestScreenshot(",
            "public bool RequestLinearHdrCapture(",
            "public LinearHdrCaptureResult GetLinearHdrCaptureResult(",
            "public void RequestRenderDocCapture(",
            "public string ExportPerformanceSnapshot(",
            "public bool TryFindObjectByName(",
            "public bool TryFindObjectById(",
            "public bool TryInspectObject(",
            "public void Initialize(",
            "public bool BeginFrame(",
            "public void EndFrame(",
            "public unsafe void Clear(",
            "public void DrawScene(",
            "public void Resize("
        };

        Assert.Multiple(() =>
        {
            foreach (string signature in signatures)
            {
                AssertMethodStartsWithDisposalGuard(
                    source,
                    signature);
            }
        });

        int planPrepared =
            source.IndexOf(
                "StagedDisposalPlan preparedPlan",
                StringComparison.Ordinal);
        int planCreated =
            planPrepared < 0
                ? -1
                : source.IndexOf(
                    "CreateDisposalPlan();",
                    planPrepared,
                    StringComparison.Ordinal);
        int disposalStarted =
            planCreated < 0
                ? -1
                : source.IndexOf(
                    "_disposeStarted = true;",
                    planCreated,
                    StringComparison.Ordinal);
        int eventUnsubscribe =
            disposalStarted < 0
                ? -1
                : source.IndexOf(
                    "Settings.QualityPresetChanging -=",
                    disposalStarted,
                    StringComparison.Ordinal);
        int planPublished =
            disposalStarted < 0
                ? -1
                : source.IndexOf(
                    "_disposalPlan = preparedPlan;",
                    disposalStarted,
                    StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(planPrepared, Is.GreaterThanOrEqualTo(0));
            Assert.That(planCreated, Is.GreaterThan(planPrepared));
            Assert.That(disposalStarted, Is.GreaterThan(planCreated));
            Assert.That(eventUnsubscribe, Is.GreaterThan(disposalStarted));
            Assert.That(planPublished, Is.GreaterThan(disposalStarted));
        });
    }

    private static void AssertMethodStartsWithDisposalGuard(
        string source,
        string signature)
    {
        int method =
            source.IndexOf(
                signature,
                StringComparison.Ordinal);
        Assert.That(
            method,
            Is.GreaterThanOrEqualTo(0),
            $"Missing method signature '{signature}'.");
        if (method < 0)
            return;

        int body =
            source.IndexOf(
                '{',
                method);
        Assert.That(
            body,
            Is.GreaterThan(method),
            $"Missing body for '{signature}'.");
        if (body <= method)
            return;

        string bodyStart =
            source[(body + 1)..]
                .TrimStart();
        Assert.That(
            bodyStart,
            Does.StartWith(
                "ThrowIfDisposalStarted();"),
            $"'{signature}' must fail closed before any work.");
    }

    private static string FindSourceFile(
        params string[] pathParts)
    {
        foreach (string start in new[]
                 {
                     TestContext.CurrentContext
                         .TestDirectory,
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory
                 })
        {
            DirectoryInfo? directory =
                new DirectoryInfo(start);
            while (directory != null)
            {
                string candidate =
                    Path.Combine(
                        new[] { directory.FullName }
                            .Concat(pathParts)
                            .ToArray());
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate source file '{Path.Combine(pathParts)}'.");
    }
}

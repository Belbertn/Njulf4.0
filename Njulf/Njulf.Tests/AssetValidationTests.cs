using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AssetValidationTests
{
    [Test]
    public async Task ValidateAssetAsync_MissingFile_ReturnsRejectedInvalid()
    {
        string path = Path.Combine(CreateTestDirectory(), "missing.gltf");
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: new AssetValidationOptions { ChildProcessMode = AssetValidationChildProcessMode.Never });

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedInvalid));
            Assert.That(entry.FailureType, Is.EqualTo(nameof(FileNotFoundException)));
            Assert.That(entry.FailureMessage, Does.Contain(Path.GetFullPath(path)));
            Assert.That(entry.ProcessKind, Is.EqualTo(AssetValidationProcessKind.InProcess));
        });
    }

    [Test]
    public async Task ValidateAssetAsync_InvalidGltfJson_ReturnsRejectedInvalid()
    {
        string path = Path.Combine(CreateTestDirectory(), "invalid.gltf");
        File.WriteAllText(path, "{ invalid json");
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: SharpGltfInProcessOptions());

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedInvalid));
            Assert.That(entry.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(entry.FailureType, Is.Not.Empty);
            Assert.That(entry.FailureMessage, Is.Not.Empty);
        });
    }

    [Test]
    public async Task ValidateAssetAsync_UnsupportedRequiredExtension_ReturnsRejectedUnsupported()
    {
        string path = CreateUnsupportedRequiredExtensionGltf();
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: SharpGltfInProcessOptions());

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedUnsupported));
            Assert.That(entry.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(entry.FailureMessage, Does.Contain("VENDOR_required_unknown"));
            Assert.That(entry.Diagnostics, Has.Some.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.UnsupportedRequiredExtension));
        });
    }

    [Test]
    public async Task ValidateAssetAsync_ChildProcessNonZeroExit_ReturnsRejectedCrashed()
    {
        string path = WriteTriangleObj();
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: new AssetValidationOptions
            {
                ChildProcessMode = AssetValidationChildProcessMode.Always,
                ChildProcessExecutablePath = "powershell.exe",
                ChildProcessArgumentTemplate = ["-NoProfile", "-Command", "exit 42"]
            });

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedCrashed));
            Assert.That(entry.ProcessKind, Is.EqualTo(AssetValidationProcessKind.ChildProcess));
            Assert.That(entry.ProcessExitCode, Is.EqualTo(42));
            Assert.That(entry.Crashed, Is.True);
        });
    }

    [Test]
    public async Task ValidateAssetAsync_ChildProcessTimeout_ReturnsRejectedTimeout()
    {
        string path = WriteTriangleObj();
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: new AssetValidationOptions
            {
                Timeout = TimeSpan.FromMilliseconds(100),
                ChildProcessMode = AssetValidationChildProcessMode.Always,
                ChildProcessExecutablePath = "powershell.exe",
                ChildProcessArgumentTemplate = ["-NoProfile", "-Command", "Start-Sleep -Seconds 5"]
            });

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedTimeout));
            Assert.That(entry.ProcessKind, Is.EqualTo(AssetValidationProcessKind.ChildProcess));
            Assert.That(entry.TimedOut, Is.True);
            Assert.That(entry.Crashed, Is.True);
        });
    }

    [Test]
    public async Task ValidateAsync_FolderWritesSuccessfulJsonReport()
    {
        string directory = CreateTestDirectory();
        CreateMinimalExternalGltf(directory, "accepted");
        var validator = new AssetValidator();

        AssetValidationReport report = await validator.ValidateAsync(
            directory,
            SharpGltfInProcessOptions());
        string reportPath = Path.Combine(directory, "asset-report.json");
        AssetValidationJson.WriteReport(reportPath, report);

        AssetValidationReport read = AssetValidationJson.ReadReport(reportPath);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(reportPath), Is.True);
            Assert.That(read.Summary.TotalCount, Is.EqualTo(1));
            Assert.That(read.Summary.RejectedCount, Is.EqualTo(0));
            Assert.That(read.Entries.Single().Status, Is.EqualTo(AssetValidationStatus.Accepted));
            Assert.That(read.Entries.Single().Metrics.TriangleCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AssetTool_ReportCommand_WritesJsonReport()
    {
        string directory = CreateTestDirectory();
        WriteTriangleObj(directory, "tool");
        string reportPath = Path.Combine(directory, "tool-report.json");
        string projectPath = FindRepoFile("Njulf.AssetTool", "Njulf.AssetTool.csproj");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }.WithArguments(
            "run",
            "--project",
            projectPath,
            "--",
            "report",
            directory,
            "--json",
            reportPath,
            "--timeout-ms",
            "30000"));

        Assert.That(process, Is.Not.Null);
        string stdout = await process!.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.EqualTo(0), stdout + stderr);
            Assert.That(File.Exists(reportPath), Is.True);
            AssetValidationReport report = AssetValidationJson.ReadReport(reportPath);
            Assert.That(report.Summary.TotalCount, Is.EqualTo(1));
            Assert.That(report.Summary.RejectedCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ValidateAsync_FoliageFolder_ClassifiesBlendedAndMaskedVariants()
    {
        string directory = CreateTestDirectory();
        string blendedPath = CreateFoliageGltf(directory, "tbdpec3r_tier_1", "BLEND", doubleSided: true, withTexture: false);
        string standardDirectory = Path.Combine(directory, "standard");
        string maskedPath = CreateFoliageGltf(standardDirectory, "tbdpec3r_tier_1_nonUE", "MASK", doubleSided: true, withTexture: false);
        var validator = new AssetValidator();

        AssetValidationReport report = await validator.ValidateAsync(
            directory,
            SharpGltfInProcessOptions());

        AssetValidationEntry blended = report.Entries.Single(entry => entry.AssetPath == blendedPath);
        AssetValidationEntry masked = report.Entries.Single(entry => entry.AssetPath == maskedPath);

        Assert.Multiple(() =>
        {
            Assert.That(blended.Status, Is.EqualTo(AssetValidationStatus.AcceptedWithWarnings));
            Assert.That(blended.Classifications, Does.Contain(AssetImportClassification.FoliageCandidate));
            Assert.That(blended.Classifications, Does.Contain(AssetImportClassification.Blended));
            Assert.That(blended.Decisions, Has.Some.Contains("Not selected by default"));
            Assert.That(blended.Diagnostics, Has.Some.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.FoliageBlendAlphaWarning &&
                    message.Source == "/materials/0"));

            Assert.That(masked.Status, Is.EqualTo(AssetValidationStatus.Accepted));
            Assert.That(masked.Classifications, Does.Contain(AssetImportClassification.FoliageCandidate));
            Assert.That(masked.Classifications, Does.Contain(AssetImportClassification.Masked));
            Assert.That(masked.Decisions, Has.Some.Contains("Preferred foliage candidate"));
            Assert.That(masked.Diagnostics, Has.None.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.FoliageBlendAlphaWarning));
        });
    }

    [Test]
    public async Task ValidateAssetAsync_StrictPolicy_RejectsFoliageBlendWarning()
    {
        string path = CreateFoliageGltf(CreateTestDirectory(), "tbdpec3r_tier_1", "BLEND", doubleSided: true, withTexture: false);
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: new AssetValidationOptions
            {
                Policy = AssetValidationPolicy.Strict,
                ChildProcessMode = AssetValidationChildProcessMode.Never,
                ImporterOptions = new ImporterOptions { Backend = ModelImportBackend.SharpGltf }
            });

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.RejectedInvalid));
            Assert.That(entry.Classifications, Does.Contain(AssetImportClassification.Blended));
            Assert.That(entry.Diagnostics, Has.Some.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.FoliageBlendAlphaWarning));
        });
    }

    [Test]
    public async Task ValidateAssetAsync_TextureBudgetThreshold_ClassifiesHighTextureMemory()
    {
        string path = CreateFoliageGltf(CreateTestDirectory(), "tbdpec3r_tier_1_nonUE", "MASK", doubleSided: true, withTexture: true);
        var validator = new AssetValidator();

        AssetValidationEntry entry = await validator.ValidateAssetAsync(
            path,
            options: new AssetValidationOptions
            {
                ChildProcessMode = AssetValidationChildProcessMode.Never,
                ImporterOptions = new ImporterOptions { Backend = ModelImportBackend.SharpGltf },
                HighTextureMemoryBytes = 1,
                TextureBudgetInspector = source => new AssetTextureBudget(
                    source.DebugName,
                    4096,
                    4096,
                    13,
                    512UL * 1024UL * 1024UL,
                    WasDownscaled: false,
                    IsCompressed: false)
            });

        Assert.Multiple(() =>
        {
            Assert.That(entry.Status, Is.EqualTo(AssetValidationStatus.AcceptedWithWarnings));
            Assert.That(entry.Metrics.LoadedTextureCount, Is.EqualTo(1));
            Assert.That(entry.Metrics.EstimatedTextureBytes, Is.EqualTo(512UL * 1024UL * 1024UL));
            Assert.That(entry.Classifications, Does.Contain(AssetImportClassification.HighTextureMemory));
            Assert.That(entry.Diagnostics, Has.Some.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.ExcessiveTextureMemory &&
                    message.Source == "/textures"));
        });
    }

    private static AssetValidationOptions SharpGltfInProcessOptions()
    {
        return new AssetValidationOptions
        {
            ChildProcessMode = AssetValidationChildProcessMode.Never,
            ImporterOptions = new ImporterOptions { Backend = ModelImportBackend.SharpGltf }
        };
    }

    private static string WriteTriangleObj()
    {
        return WriteTriangleObj(CreateTestDirectory(), TestContext.CurrentContext.Test.ID);
    }

    private static string WriteTriangleObj(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{name}.obj");
        File.WriteAllText(
            path,
            """
            v 0 0 0
            v 1 0 0
            v 0 1 0
            f 1 2 3
            """);
        return path;
    }

    private static string CreateMinimalExternalGltf(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{name}.bin");
        string gltfPath = Path.Combine(directory, $"{name}.gltf");
        byte[] positions =
        [
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f)
        ];
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        File.WriteAllBytes(binPath, positions.Concat(indices).ToArray());
        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf asset validation test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0 }],
                "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "mode": 4 }] }],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{positions.Length + indices.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);
        return gltfPath;
    }

    private static string CreateFoliageGltf(
        string directory,
        string name,
        string alphaMode,
        bool doubleSided,
        bool withTexture)
    {
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{name}.bin");
        string gltfPath = Path.Combine(directory, $"{name}.gltf");
        byte[] positions =
        [
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(0f),
            .. BitConverter.GetBytes(0f), .. BitConverter.GetBytes(1f), .. BitConverter.GetBytes(0f)
        ];
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        File.WriteAllBytes(binPath, positions.Concat(indices).ToArray());

        string textureJson = string.Empty;
        string imageJson = string.Empty;
        string textureSlotJson = string.Empty;
        if (withTexture)
        {
            string texturePath = Path.Combine(directory, $"{name}_albedo.png");
            File.WriteAllBytes(texturePath, OnePixelPng());
            imageJson = $"                \"images\": [{{ \"uri\": \"{Path.GetFileName(texturePath)}\", \"mimeType\": \"image/png\" }}],{Environment.NewLine}";
            textureJson = $"                \"textures\": [{{ \"source\": 0 }}],{Environment.NewLine}";
            textureSlotJson = ",\n                      \"baseColorTexture\": { \"index\": 0 }";
        }

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf foliage validation test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "{{name}}" }],
                "meshes": [{ "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "mode": 4, "material": 0 }] }],
                "materials": [
                  {
                    "name": "{{name}}_grass_material",
                    "alphaMode": "{{alphaMode}}",
                    "doubleSided": {{doubleSided.ToString().ToLowerInvariant()}},
                    "pbrMetallicRoughness": {
                      "baseColorFactor": [1, 1, 1, 1]{{textureSlotJson}}
                    }
                  }
                ],
                {{imageJson}}{{textureJson}}
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{positions.Length + indices.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);

        return gltfPath;
    }

    private static byte[] OnePixelPng()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
    }

    private static string CreateUnsupportedRequiredExtensionGltf()
    {
        string directory = CreateTestDirectory();
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");
        File.WriteAllText(
            gltfPath,
            """
            {
              "asset": { "version": "2.0", "generator": "Njulf asset validation unsupported test" },
              "extensionsUsed": [ "VENDOR_required_unknown" ],
              "extensionsRequired": [ "VENDOR_required_unknown" ],
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "name": "EmptyNode" }]
            }
            """);
        return gltfPath;
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "asset-validation-tests", TestContext.CurrentContext.Test.ID);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        string? directory = TestContext.CurrentContext.WorkDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository test asset.", Path.Combine(relativeParts));
    }
}

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }
}

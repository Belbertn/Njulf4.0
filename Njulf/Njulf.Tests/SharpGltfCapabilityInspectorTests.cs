using System;
using System.IO;
using System.Linq;
using Njulf.Assets.Gltf;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SharpGltfCapabilityInspectorTests
{
    [Test]
    public void Inspect_LoadsExternalGltfAndReportsCoreRuntimeCapabilities()
    {
        string path = CreateMinimalExternalGltf();

        SharpGltfCapabilityReport report = SharpGltfCapabilityInspector.Inspect(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.LoadedSuccessfully, Is.True, report.FailureMessage);
            Assert.That(report.PackageVersions.Core, Is.Not.Empty);
            Assert.That(report.PackageVersions.Runtime, Is.Not.Empty);
            Assert.That(report.PackageVersions.Toolkit, Is.Not.Empty);
            Assert.That(report.Document, Is.Not.Null);
            Assert.That(report.Document!.Version, Is.EqualTo("2.0"));
            Assert.That(report.Document.BufferCount, Is.EqualTo(1));
            Assert.That(report.Document.BufferViewCount, Is.EqualTo(2));
            Assert.That(report.Document.AccessorCount, Is.EqualTo(2));
            Assert.That(report.Document.MeshCount, Is.EqualTo(1));
            Assert.That(report.Document.SceneCount, Is.EqualTo(1));
            Assert.That(report.Accessors, Has.Count.EqualTo(2));
            Assert.That(report.BufferViews, Has.Count.EqualTo(2));
            Assert.That(report.Meshes, Has.Count.EqualTo(1));
            Assert.That(report.Meshes[0].Primitives, Has.Count.EqualTo(1));
            Assert.That(report.Meshes[0].Primitives[0].VertexAttributeKeys, Does.Contain("POSITION"));
            Assert.That(report.Runtime, Is.Not.Null);
            Assert.That(report.Runtime!.DecodedMeshCount, Is.EqualTo(1));
            Assert.That(report.Runtime.DecodedPrimitiveCount, Is.EqualTo(1));
            Assert.That(report.Runtime.DecodedTriangleCount, Is.EqualTo(1));
            Assert.That(report.Runtime.DecodedVertexCount, Is.EqualTo(3));
            Assert.That(report.Runtime.Issues, Is.Empty);
        });
    }

    [Test]
    public void Inspect_LoadsSponzaThroughSharpGltfWithoutAssimp()
    {
        string path = FindRepoFile("NjulfHelloGame", "NewSponza_Main_glTF_003.gltf");

        SharpGltfCapabilityReport report = SharpGltfCapabilityInspector.Inspect(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.LoadedSuccessfully, Is.True, report.FailureMessage);
            Assert.That(report.Document, Is.Not.Null);
            Assert.That(report.Document!.MeshCount, Is.GreaterThan(0));
            Assert.That(report.Document.MaterialCount, Is.GreaterThan(0));
            Assert.That(report.Document.ImageCount, Is.GreaterThan(0));
            Assert.That(report.Document.TextureCount, Is.GreaterThan(0));
            Assert.That(report.Runtime, Is.Not.Null);
            Assert.That(report.Runtime!.DecodedMeshCount, Is.GreaterThan(0));
            Assert.That(report.Runtime.DecodedPrimitiveCount, Is.GreaterThan(0));
            Assert.That(report.Runtime.DecodedTriangleCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Inspect_LoadsGlbThroughSharpGltfWithoutAssimp()
    {
        string path = FindRepoFile("NjulfHelloGame", "Strut.glb");

        SharpGltfCapabilityReport report = SharpGltfCapabilityInspector.Inspect(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.LoadedSuccessfully, Is.True, report.FailureMessage);
            Assert.That(report.Document, Is.Not.Null);
            Assert.That(report.Document!.BufferCount, Is.GreaterThan(0));
            Assert.That(report.Document.AccessorCount, Is.GreaterThan(0));
            Assert.That(report.Document.MeshCount, Is.GreaterThan(0));
            Assert.That(report.Runtime, Is.Not.Null);
            Assert.That(report.Runtime!.DecodedMeshCount, Is.GreaterThan(0));
            Assert.That(report.Runtime.DecodedTriangleCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Inspect_ReportsRibbonGrassAlphaAndTextureCapabilities()
    {
        string maskedPath = FindRepoFile(
            "NjulfHelloGame",
            "Assets",
            "ribbon_grass_tbdpec3r_ue_low",
            "standard",
            "tbdpec3r_tier_3_nonUE.gltf");
        string blendedPath = FindRepoFile(
            "NjulfHelloGame",
            "Assets",
            "ribbon_grass_tbdpec3r_ue_low",
            "tbdpec3r_tier_3.gltf");

        SharpGltfCapabilityReport masked = SharpGltfCapabilityInspector.Inspect(maskedPath);
        SharpGltfCapabilityReport blended = SharpGltfCapabilityInspector.Inspect(blendedPath);

        Assert.Multiple(() =>
        {
            Assert.That(masked.LoadedSuccessfully, Is.True, masked.FailureMessage);
            Assert.That(blended.LoadedSuccessfully, Is.True, blended.FailureMessage);
            Assert.That(masked.Materials, Has.Some.Matches<SharpGltfMaterialSummary>(material => material.AlphaMode == "MASK"));
            Assert.That(blended.Materials, Has.Some.Matches<SharpGltfMaterialSummary>(material => material.AlphaMode == "BLEND"));
            Assert.That(masked.Images, Has.Count.GreaterThan(0));
            Assert.That(masked.Textures, Has.Count.GreaterThan(0));
            Assert.That(masked.Samplers, Has.Count.GreaterThan(0));
            Assert.That(masked.Runtime, Is.Not.Null);
            Assert.That(masked.Runtime!.DecodedTriangleCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Inspect_TreeCrashCandidateReturnsManagedCapabilityReport()
    {
        string path = FindRepoFile("NjulfHelloGame", "Assets", "low_poly_trees_free.glb");

        SharpGltfCapabilityReport report = SharpGltfCapabilityInspector.Inspect(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.AssetPath, Is.EqualTo(Path.GetFullPath(path)));
            Assert.That(report.PackageVersions.Core, Is.Not.Empty);
            Assert.That(report.PackageVersions.Runtime, Is.Not.Empty);
            Assert.That(report.PackageVersions.Toolkit, Is.Not.Empty);
            Assert.That(report.Status, Is.AnyOf(SharpGltfCapabilityStatus.Loaded, SharpGltfCapabilityStatus.Failed));
            if (report.LoadedSuccessfully)
            {
                Assert.That(report.Document, Is.Not.Null);
                Assert.That(report.Document!.MeshCount, Is.GreaterThan(0));
            }
            else
            {
                Assert.That(report.FailureType, Is.Not.Empty);
                Assert.That(report.FailureMessage, Is.Not.Empty);
            }
        });
    }

    [Test]
    public void Inspect_MissingFileReturnsFailureReport()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "missing-phase0-asset.glb");

        SharpGltfCapabilityReport report = SharpGltfCapabilityInspector.Inspect(path);

        Assert.Multiple(() =>
        {
            Assert.That(report.LoadedSuccessfully, Is.False);
            Assert.That(report.Status, Is.EqualTo(SharpGltfCapabilityStatus.Failed));
            Assert.That(report.FailureType, Is.EqualTo(nameof(FileNotFoundException)));
            Assert.That(report.FailureMessage, Does.Contain("Asset file was not found"));
            Assert.That(report.Document, Is.Null);
        });
    }

    private static string CreateMinimalExternalGltf()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-phase0");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

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
                "asset": { "version": "2.0", "generator": "Njulf Phase 0 SharpGLTF test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "TriangleNode" }],
                "meshes": [
                  {
                    "name": "Triangle",
                    "primitives": [
                      {
                        "attributes": { "POSITION": 0 },
                        "indices": 1,
                        "mode": 4,
                        "material": 0
                      }
                    ]
                  }
                ],
                "materials": [
                  {
                    "name": "Default",
                    "pbrMetallicRoughness": {
                      "baseColorFactor": [1, 1, 1, 1],
                      "metallicFactor": 0,
                      "roughnessFactor": 1
                    }
                  }
                ],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{positions.Length + indices.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  {
                    "bufferView": 0,
                    "componentType": 5126,
                    "count": 3,
                    "type": "VEC3",
                    "min": [0, 0, 0],
                    "max": [1, 1, 0]
                  },
                  {
                    "bufferView": 1,
                    "componentType": 5123,
                    "count": 3,
                    "type": "SCALAR",
                    "min": [0],
                    "max": [2]
                  }
                ]
              }
              """);

        return gltfPath;
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

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Njulf.Assets;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AssetRegressionCorpusTests
{
    [Test]
    public void SharpGltfCorpus_CoversSupportedResourceKinds()
    {
        string directory = CreateTestDirectory();
        string externalPng = CreateExternalPngGltf(directory, "external_png");
        string dataBuffer = CreateDataUriBufferGltf(directory, "data_uri_buffer");
        string dataUriImage = CreateDataUriImageGltf(directory, "data_uri_image");
        string bufferViewImage = CreateBufferViewImageGltf(directory, "buffer_view_image");
        string richStreams = CreateRichVertexMaterialGltf(directory, "uv1_vertex_color_texture_transform");
        string embeddedGeometryGlb = CreateEmbeddedGeometryGlb(directory, "embedded_geometry");
        string embeddedImageGlb = CreateEmbeddedImageGlb(directory, "embedded_image");

        using var importer = new ModelImporter();
        ModelMesh externalMesh = ImportSharp(importer, externalPng);
        ModelMesh dataBufferMesh = ImportSharp(importer, dataBuffer);
        ModelMesh dataUriImageMesh = ImportSharp(importer, dataUriImage);
        ModelMesh bufferViewImageMesh = ImportSharp(importer, bufferViewImage);
        ModelMesh richMesh = ImportSharp(importer, richStreams);
        ModelMesh geometryGlbMesh = ImportSharp(importer, embeddedGeometryGlb);
        ModelMesh imageGlbMesh = ImportSharp(importer, embeddedImageGlb);

        Assert.Multiple(() =>
        {
            Assert.That(externalMesh.Materials.Single().BaseColorTexture!.Source!.SourceKind, Is.EqualTo(TextureSourceKind.ExternalFile));
            Assert.That(dataBufferMesh.Vertices, Has.Length.EqualTo(3));
            Assert.That(dataUriImageMesh.Materials.Single().BaseColorTexture!.Source!.SourceKind, Is.EqualTo(TextureSourceKind.DataUri));
            Assert.That(bufferViewImageMesh.Materials.Single().BaseColorTexture!.Source!.SourceKind, Is.EqualTo(TextureSourceKind.BufferView));
            Assert.That(richMesh.TexCoords1, Has.Length.EqualTo(3));
            Assert.That(richMesh.VertexColors, Has.Length.EqualTo(3));
            Assert.That(richMesh.ImportDiagnostics.TextureTransformCount, Is.EqualTo(1));
            Assert.That(geometryGlbMesh.Indices, Has.Length.EqualTo(3));
            Assert.That(imageGlbMesh.Materials.Single().BaseColorTexture!.Source!.SourceKind, Is.EqualTo(TextureSourceKind.GlbBinary));
            Assert.That(imageGlbMesh.ImportDiagnostics.BufferViewImageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void KnownBadMalformedGlb_IsRejectedDeterministically()
    {
        string directory = CreateTestDirectory();
        string path = Path.Combine(directory, "malformed.glb");
        File.WriteAllBytes(path, [0x67, 0x6C, 0x54, 0x46, 0x02, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x00, 0x00]);
        using var importer = new ModelImporter();

        ModelImportResult first = importer.ImportDetailed(path, new ImporterOptions { Backend = ModelImportBackend.SharpGltf });
        ModelImportResult second = importer.ImportDetailed(path, new ImporterOptions { Backend = ModelImportBackend.SharpGltf });

        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.Not.EqualTo(ModelImportStatus.Imported));
            Assert.That(second.Status, Is.EqualTo(first.Status));
            Assert.That(second.FailureType, Is.EqualTo(first.FailureType));
            Assert.That(second.FailureMessage, Is.EqualTo(first.FailureMessage));
            Assert.That(first.FailureMessage, Is.Not.Empty);
        });
    }

    [Test]
    public void ValidationReportSchema_ContainsCiGateFields()
    {
        string reportPath = FindRepoFile("Plans", "asset-validation-report-phase6.json");
        AssetValidationReport report = AssetValidationJson.ReadReport(reportPath);
        AssetValidationEntry entry = report.Entries.Single(item => item.RelativePath == "ribbon_grass_tbdpec3r_ue_high/tbdpec3r_tier_1.gltf");

        Assert.Multiple(() =>
        {
            Assert.That(report.SchemaVersion, Is.EqualTo(1));
            Assert.That(report.Summary.TotalCount, Is.EqualTo(7));
            Assert.That(entry.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(entry.FileSizeBytes, Is.GreaterThan(0));
            Assert.That(entry.Metrics.EstimatedTextureBytes, Is.GreaterThan(0));
            Assert.That(entry.Classifications, Does.Contain(AssetImportClassification.Blended));
            Assert.That(entry.Classifications, Does.Contain(AssetImportClassification.FoliageCandidate));
            Assert.That(entry.Decisions, Has.Some.Contains("Not selected by default"));
            Assert.That(entry.Diagnostics, Has.Some.Matches<AssetImportMessage>(
                message => message.Code == AssetImportMessageCode.FoliageBlendAlphaWarning &&
                    message.Source == "/materials/0"));
        });
    }

    [Test]
    public void SampleAssetValidationAllowlist_CoversCurrentSampleManifest()
    {
        string reportPath = FindRepoFile("NjulfHelloGame", "sample-asset-validation-report.json");
        AssetValidationReport report = AssetValidationJson.ReadReport(reportPath);
        AssetValidationEntry main = report.Entries.Single(entry => entry.RelativePath == "NewSponza_Main_glTF_003.gltf");
        AssetValidationEntry curtains = report.Entries.Single(entry => entry.RelativePath == "NewSponza_Curtains_glTF.gltf");
        AssetValidationEntry foliage = report.Entries.Single(entry =>
            entry.RelativePath == "Assets/ribbon_grass_tbdpec3r_ue_low/standard/tbdpec3r_tier_3_nonUE.gltf");

        Assert.Multiple(() =>
        {
            Assert.That(report.Summary.TotalCount, Is.EqualTo(10));
            Assert.That(report.Summary.RejectedCount, Is.EqualTo(0));
            Assert.That(main.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(main.Status, Is.EqualTo(AssetValidationStatus.AcceptedWithWarnings));
            Assert.That(main.Diagnostics, Has.Count.EqualTo(2));
            Assert.That(main.Classifications, Is.EquivalentTo(new[]
            {
                AssetImportClassification.Blended,
                AssetImportClassification.HighTextureMemory
            }));
            Assert.That(curtains.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(curtains.Status, Is.EqualTo(AssetValidationStatus.AcceptedWithWarnings));
            Assert.That(curtains.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(curtains.Classifications, Is.EquivalentTo(new[]
            {
                AssetImportClassification.Opaque,
                AssetImportClassification.HighTextureMemory
            }));
            Assert.That(foliage.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(foliage.Status, Is.EqualTo(AssetValidationStatus.Accepted));
            Assert.That(foliage.Diagnostics, Has.Count.EqualTo(0));
            Assert.That(foliage.Classifications, Is.EquivalentTo(new[]
            {
                AssetImportClassification.Masked,
                AssetImportClassification.Billboard,
                AssetImportClassification.FoliageCandidate
            }));
            Assert.That(foliage.Decisions, Has.Some.Contains("Preferred foliage candidate"));
        });
    }

    private static ModelMesh ImportSharp(ModelImporter importer, string path)
    {
        return importer.Import(path, new ImporterOptions { Backend = ModelImportBackend.SharpGltf });
    }

    private static string CreateExternalPngGltf(string directory, string name)
    {
        string texturePath = Path.Combine(directory, $"{name}.png");
        File.WriteAllBytes(texturePath, OnePixelPng());
        return CreateTriangleGltf(
            directory,
            name,
            bufferUriMode: BufferUriMode.External,
            imagesJson: "[{ \"uri\": " + JsonSerializer.Serialize(Path.GetFileName(texturePath)) + ", \"mimeType\": \"image/png\" }]",
            texturesJson: "[{ \"source\": 0 }]",
            materialExtraJson: "\"baseColorTexture\": { \"index\": 0 }");
    }

    private static string CreateDataUriBufferGltf(string directory, string name)
    {
        return CreateTriangleGltf(directory, name, bufferUriMode: BufferUriMode.DataUri);
    }

    private static string CreateDataUriImageGltf(string directory, string name)
    {
        return CreateTriangleGltf(
            directory,
            name,
            bufferUriMode: BufferUriMode.External,
            imagesJson: "[{ \"uri\": \"data:image/png;base64," + Convert.ToBase64String(OnePixelPng()) + "\", \"mimeType\": \"image/png\" }]",
            texturesJson: "[{ \"source\": 0 }]",
            materialExtraJson: "\"baseColorTexture\": { \"index\": 0 }");
    }

    private static string CreateBufferViewImageGltf(string directory, string name)
    {
        byte[] triangle = CreateTriangleBinary(includeUv1: false, includeColor: false, out int indicesOffset, out int uv0Offset, out _, out _, out _);
        byte[] image = OnePixelPng();
        int imageOffset = triangle.Length;
        byte[] bin = triangle.Concat(image).ToArray();

        string binPath = Path.Combine(directory, $"{name}.bin");
        File.WriteAllBytes(binPath, bin);
        string json = CreateTriangleJson(
            name,
            bufferByteLength: bin.Length,
            bufferUri: Path.GetFileName(binPath),
            imagesJson: "[{ \"bufferView\": 3, \"mimeType\": \"image/png\" }]",
            texturesJson: "[{ \"source\": 0 }]",
            materialExtraJson: "\"baseColorTexture\": { \"index\": 0 }",
            indicesOffset: indicesOffset,
            uv0Offset: uv0Offset,
            extraBufferViewsJson: $",{{ \"buffer\": 0, \"byteOffset\": {imageOffset}, \"byteLength\": {image.Length} }}");

        string path = Path.Combine(directory, $"{name}.gltf");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateRichVertexMaterialGltf(string directory, string name)
    {
        string texturePath = Path.Combine(directory, $"{name}.png");
        File.WriteAllBytes(texturePath, OnePixelPng());
        return CreateTriangleGltf(
            directory,
            name,
            bufferUriMode: BufferUriMode.External,
            includeUv1: true,
            includeColor: true,
            extensionsUsedJson: """["KHR_texture_transform"]""",
            imagesJson: "[{ \"uri\": " + JsonSerializer.Serialize(Path.GetFileName(texturePath)) + ", \"mimeType\": \"image/png\" }]",
            texturesJson: "[{ \"source\": 0 }]",
            materialExtraJson:
                """
                "baseColorTexture": {
                  "index": 0,
                  "texCoord": 0,
                  "extensions": {
                    "KHR_texture_transform": {
                      "offset": [0.125, 0.25],
                      "scale": [0.5, 0.5],
                      "rotation": 0.1,
                      "texCoord": 1
                    }
                  }
                }
                """);
    }

    private static string CreateEmbeddedGeometryGlb(string directory, string name)
    {
        byte[] bin = CreateTriangleBinary(includeUv1: false, includeColor: false, out _, out _, out _, out _, out _);
        string json = CreateTriangleJson(name, bufferByteLength: bin.Length, bufferUri: null);
        string path = Path.Combine(directory, $"{name}.glb");
        WriteGlb(path, json, bin);
        return path;
    }

    private static string CreateEmbeddedImageGlb(string directory, string name)
    {
        byte[] triangle = CreateTriangleBinary(includeUv1: false, includeColor: false, out _, out _, out _, out _, out _);
        byte[] image = OnePixelPng();
        int imageOffset = triangle.Length;
        byte[] bin = triangle.Concat(image).ToArray();
        string json = CreateTriangleJson(
            name,
            bufferByteLength: bin.Length,
            bufferUri: null,
            imagesJson: "[{ \"bufferView\": 3, \"mimeType\": \"image/png\" }]",
            texturesJson: "[{ \"source\": 0 }]",
            materialExtraJson: "\"baseColorTexture\": { \"index\": 0 }",
            extraBufferViewsJson: $",{{ \"buffer\": 0, \"byteOffset\": {imageOffset}, \"byteLength\": {image.Length} }}");
        string path = Path.Combine(directory, $"{name}.glb");
        WriteGlb(path, json, bin);
        return path;
    }

    private static string CreateTriangleGltf(
        string directory,
        string name,
        BufferUriMode bufferUriMode,
        bool includeUv1 = false,
        bool includeColor = false,
        string? extensionsUsedJson = null,
        string? imagesJson = null,
        string? texturesJson = null,
        string? materialExtraJson = null)
    {
        byte[] bin = CreateTriangleBinary(
            includeUv1,
            includeColor,
            out int indicesOffset,
            out int uv0Offset,
            out int uv1Offset,
            out int colorOffset,
            out int totalByteLength);
        string? bufferUri;
        if (bufferUriMode == BufferUriMode.DataUri)
        {
            bufferUri = $"data:application/octet-stream;base64,{Convert.ToBase64String(bin)}";
        }
        else
        {
            string binPath = Path.Combine(directory, $"{name}.bin");
            File.WriteAllBytes(binPath, bin);
            bufferUri = Path.GetFileName(binPath);
        }

        string json = CreateTriangleJson(
            name,
            totalByteLength,
            bufferUri,
            includeUv1,
            includeColor,
            extensionsUsedJson,
            imagesJson,
            texturesJson,
            materialExtraJson,
            indicesOffset,
            uv0Offset,
            uv1Offset,
            colorOffset);
        string path = Path.Combine(directory, $"{name}.gltf");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CreateTriangleJson(
        string name,
        int bufferByteLength,
        string? bufferUri,
        bool includeUv1 = false,
        bool includeColor = false,
        string? extensionsUsedJson = null,
        string? imagesJson = null,
        string? texturesJson = null,
        string? materialExtraJson = null,
        int indicesOffset = 36,
        int uv0Offset = 44,
        int uv1Offset = 68,
        int colorOffset = 92,
        string extraBufferViewsJson = "")
    {
        string extensions = string.IsNullOrWhiteSpace(extensionsUsedJson)
            ? string.Empty
            : "  \"extensionsUsed\": " + extensionsUsedJson + ",";
        string attributes = "\"POSITION\": 0, \"TEXCOORD_0\": 2";
        if (includeUv1)
            attributes += ", \"TEXCOORD_1\": 3";
        if (includeColor)
            attributes += ", \"COLOR_0\": 4";
        string bufferUriJson = bufferUri == null ? string.Empty : ", \"uri\": " + JsonSerializer.Serialize(bufferUri);
        string imageSection = string.IsNullOrWhiteSpace(imagesJson) ? string.Empty : ",\"images\": " + imagesJson;
        string textureSection = string.IsNullOrWhiteSpace(texturesJson) ? string.Empty : ",\"textures\": " + texturesJson;
        string materialExtra = string.IsNullOrWhiteSpace(materialExtraJson) ? string.Empty : "," + materialExtraJson;
        string uv1BufferView = includeUv1 ? $",{{ \"buffer\": 0, \"byteOffset\": {uv1Offset}, \"byteLength\": 24 }}" : string.Empty;
        string colorBufferView = includeColor ? $",{{ \"buffer\": 0, \"byteOffset\": {colorOffset}, \"byteLength\": 48 }}" : string.Empty;
        string uv1Accessor = includeUv1 ? ",{ \"bufferView\": 3, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC2\" }" : string.Empty;
        string colorAccessor = includeColor ? ",{ \"bufferView\": 4, \"componentType\": 5126, \"count\": 3, \"type\": \"VEC4\" }" : string.Empty;

        return $$"""
            {
            {{extensions}}
              "asset": { "version": "2.0", "generator": "Njulf phase 7 regression corpus" },
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "mesh": 0, "name": "{{name}}" }],
              "meshes": [{ "primitives": [{ "attributes": { {{attributes}} }, "indices": 1, "mode": 4, "material": 0 }] }],
              "materials": [{ "name": "{{name}}_material", "pbrMetallicRoughness": { "baseColorFactor": [1, 1, 1, 1]{{materialExtra}} } }]{{imageSection}}{{textureSection}},
              "buffers": [{ "byteLength": {{bufferByteLength}}{{bufferUriJson}} }],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": 36, "target": 34962 },
                { "buffer": 0, "byteOffset": {{indicesOffset}}, "byteLength": 6, "target": 34963 },
                { "buffer": 0, "byteOffset": {{uv0Offset}}, "byteLength": 24 }{{uv1BufferView}}{{colorBufferView}}{{extraBufferViewsJson}}
              ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] },
                { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC2" }{{uv1Accessor}}{{colorAccessor}}
              ]
            }
            """;
    }

    private static byte[] CreateTriangleBinary(
        bool includeUv1,
        bool includeColor,
        out int indicesOffset,
        out int uv0Offset,
        out int uv1Offset,
        out int colorOffset,
        out int totalByteLength)
    {
        byte[] positions = Floats(0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        byte[] indexPadding = [0, 0];
        byte[] uv0 = Floats(0f, 0f, 1f, 0f, 0f, 1f);
        byte[] uv1 = Floats(0f, 1f, 1f, 1f, 0.5f, 0f);
        byte[] color = Floats(1f, 0f, 0f, 1f, 0f, 1f, 0f, 1f, 0f, 0f, 1f, 1f);

        indicesOffset = positions.Length;
        uv0Offset = indicesOffset + indices.Length + indexPadding.Length;
        uv1Offset = uv0Offset + uv0.Length;
        colorOffset = uv1Offset + (includeUv1 ? uv1.Length : 0);

        byte[] data = positions.Concat(indices).Concat(indexPadding).Concat(uv0).ToArray();
        if (includeUv1)
            data = data.Concat(uv1).ToArray();
        if (includeColor)
            data = data.Concat(color).ToArray();

        totalByteLength = data.Length;
        return data;
    }

    private static byte[] Floats(params float[] values)
    {
        return values.SelectMany(BitConverter.GetBytes).ToArray();
    }

    private static byte[] OnePixelPng()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
    }

    private static void WriteGlb(string path, string json, byte[] binaryChunk)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] paddedJson = Pad4(jsonBytes, 0x20);
        byte[] paddedBin = Pad4(binaryChunk, 0x00);
        int totalLength = 12 + 8 + paddedJson.Length + 8 + paddedBin.Length;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)totalLength);
        writer.Write((uint)paddedJson.Length);
        writer.Write(0x4E4F534Au);
        writer.Write(paddedJson);
        writer.Write((uint)paddedBin.Length);
        writer.Write(0x004E4942u);
        writer.Write(paddedBin);
    }

    private static byte[] Pad4(byte[] source, byte padding)
    {
        int paddedLength = (source.Length + 3) & ~3;
        if (paddedLength == source.Length)
            return source;

        byte[] result = new byte[paddedLength];
        Array.Copy(source, result, source.Length);
        for (int i = source.Length; i < result.Length; i++)
            result[i] = padding;
        return result;
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "asset-regression-corpus-tests", TestContext.CurrentContext.Test.ID);
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

    private enum BufferUriMode
    {
        External,
        DataUri
    }
}

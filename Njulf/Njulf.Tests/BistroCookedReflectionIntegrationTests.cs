using Njulf.Assets;
using Njulf.Assets.Cooked;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BistroCookedReflectionIntegrationTests
{
    private const uint FoliageFeature = 1u << 22;

    [Test]
    [Explicit("Requires both local Amazon Bistro source assets and their win-x64 cooks.")]
    public void BothBistroCooks_ResolveUnderExactRuntimeImportContracts()
    {
        string root = FindRepositoryRoot();
        string contentRoot = Path.Combine(root, "NjulfHelloGame");
        var resolver = new CookedContentResolver(contentRoot);

        foreach (SampleAssetReference asset in
                 SampleAssetManifest.Bistro.EnumerateAssets())
        {
            string sourcePath = Path.GetFullPath(Path.Combine(
                contentRoot,
                asset.Path));
            if (!File.Exists(sourcePath))
            {
                Assert.Ignore(
                    $"The local Bistro source is required: {sourcePath}");
            }

            ContentLoadOptions loadOptions = asset.CreateLoadOptions();
            ulong expectedImportContract = CookedModelImportContract.Compute(
                sourcePath,
                loadOptions.ImporterOptions ?? ImporterOptions.Default);
            CookedResolution resolution = resolver.ResolveModel(
                asset.Path,
                sourcePath,
                strictSourceHash: true,
                expectedImportContract,
                captureModelSnapshot: true);

            Assert.Multiple(() =>
            {
                Assert.That(
                    resolution.Status,
                    Is.EqualTo(CookedResolutionStatus.Found),
                    $"{asset.Path}: {resolution.Reason}");
                Assert.That(
                    resolution.Header?.ImportSettingsHash,
                    Is.EqualTo(expectedImportContract),
                    asset.Path);
                Assert.That(
                    resolution.Header?.FormatMinor,
                    Is.EqualTo(CookedFormatVersions.Model.Minor),
                    asset.Path);
                Assert.That(resolution.ModelSnapshot, Is.Not.Null,
                    asset.Path);
            });
        }
    }

    [Test]
    [Explicit("Requires both local Amazon Bistro source assets and their win-x64 cooks.")]
    public void BothBistroCooks_PersistOnlyReviewedAutomaticPlanarReceiver()
    {
        string root = FindRepositoryRoot();
        string contentRoot = Path.Combine(root, "NjulfHelloGame");

        foreach (SampleAssetReference asset in
                 SampleAssetManifest.Bistro.EnumerateAssets())
        {
            string sourcePath = Path.GetFullPath(Path.Combine(
                contentRoot,
                asset.Path));
            string sourceName = Path.GetFileNameWithoutExtension(asset.Path);
            string modelPath = Path.Combine(
                contentRoot,
                "Cooked",
                "win-x64",
                "models",
                sourceName + ".njmodel");
            if (!File.Exists(sourcePath) || !File.Exists(modelPath))
            {
                Assert.Ignore(
                    $"The local Bistro source and win-x64 cook are required: " +
                    asset.Path);
            }

            CookedModelManifest manifest;
            using (var reader = new CookedAssetReader(
                       modelPath,
                       CookedAssetKind.Model,
                       CookedAssetReaderFlags.StrictSourceHash,
                       CookedHash.File(sourcePath)))
            {
                manifest = CookedJson.Deserialize<CookedModelManifest>(
                    reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                    modelPath,
                    "manifest");
            }

            string materialPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(modelPath)!,
                manifest.Material.RelativePath));
            CookedMaterialTable materials = CookedPackage.LoadMaterials(
                materialPath,
                CookedAssetReaderFlags.StrictSourceHash,
                out _);
            string[] enabledMaterials = materials.Materials
                .Where(static material =>
                    material.AutomaticPlanarReflectionEnabled)
                .Select(static material => material.Name)
                .Order()
                .ToArray();
            string[] expected = sourceName == "BistroExterior"
                ? ["Pavement_Ground_Wet"]
                : [];
            string materialNames = string.Join(
                ", ",
                materials.Materials
                    .Select(static material => material.Name)
                    .Order());

            Assert.That(
                enabledMaterials,
                Is.EqualTo(expected),
                $"{asset.Path}; material names: [{materialNames}]");
        }
    }

    [Test]
    [Explicit("Requires the local Amazon Bistro source asset and its win-x64 cook.")]
    public void ExteriorCook_PreservesThinGlassAndImportSemantics()
    {
        string root = FindRepositoryRoot();
        string sourcePath = Path.Combine(
            root,
            "NjulfHelloGame",
            "Assets",
            "Bistro_v5_2",
            "BistroExterior.fbx");
        string modelPath = Path.Combine(
            root,
            "NjulfHelloGame",
            "Cooked",
            "win-x64",
            "models",
            "BistroExterior.njmodel");
        if (!File.Exists(sourcePath) || !File.Exists(modelPath))
        {
            Assert.Ignore(
                "The local Bistro source and win-x64 cooked package are required.");
        }

        var importerOptions = new ImporterOptions
        {
            Backend = ModelImportBackend.Assimp,
            AssimpMaterialTextureConvention =
                AssimpMaterialTextureConvention.AmazonBistro
        };
        ulong expectedImportContract = CookedModelImportContract.Compute(
            sourcePath,
            importerOptions);

        CookedModelManifest manifest;
        CookedAssetHeader header;
        using (var reader = new CookedAssetReader(
                   modelPath,
                   CookedAssetKind.Model,
                   CookedAssetReaderFlags.StrictSourceHash,
                   CookedHash.File(sourcePath)))
        {
            header = reader.Header;
            manifest = CookedJson.Deserialize<CookedModelManifest>(
                reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                modelPath,
                "manifest");
        }

        string materialPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(modelPath)!,
            manifest.Material.RelativePath));
        CookedMaterialTable materials = CookedPackage.LoadMaterials(
            materialPath,
            CookedAssetReaderFlags.StrictSourceHash,
            out _);
        ModelMaterial[] thinGlass = materials.Materials
            .Where(material => material.IsThinGlass)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(header.FormatMajor,
                Is.EqualTo(CookedFormatVersions.Model.Major));
            Assert.That(header.FormatMinor,
                Is.EqualTo(CookedFormatVersions.Model.Minor));
            Assert.That(header.ImportSettingsHash,
                Is.EqualTo(expectedImportContract));
            Assert.That(manifest.ImportSettingsHash,
                Is.EqualTo(expectedImportContract));
            Assert.That(thinGlass, Has.Length.EqualTo(4));
            Assert.That(thinGlass.All(material =>
                    material.AlphaMode == ModelAlphaMode.Blend),
                Is.True);
            Assert.That(thinGlass.All(material => material.DoubleSided),
                Is.True);
            Assert.That(thinGlass.All(material => material.Metallic == 0.0f),
                Is.True);
            Assert.That(thinGlass.All(material =>
                    material.GiTransmissionPolicy ==
                    ModelGiTransmissionPolicy.ThinSurface),
                Is.True);
            Assert.That(thinGlass.Any(material =>
                    material.Roughness <= 0.08f + 1.0e-6f &&
                    material.TransmissionFactor >= 0.94f - 1.0e-6f),
                Is.True,
                "The clear Bistro glass profile must remain a sharp dielectric.");
        });
    }

    [Test]
    [Explicit("Requires both local Amazon Bistro source assets and their win-x64 cooks.")]
    public void BothBistroCooks_PreserveCompressedMaterialTextureBindings()
    {
        string root = FindRepositoryRoot();
        string contentRoot = Path.Combine(root, "NjulfHelloGame");

        foreach (SampleAssetReference asset in
                 SampleAssetManifest.Bistro.EnumerateAssets())
        {
            string sourcePath = Path.GetFullPath(Path.Combine(
                contentRoot,
                asset.Path));
            string modelPath = Path.Combine(
                contentRoot,
                "Cooked",
                "win-x64",
                "models",
                Path.GetFileNameWithoutExtension(asset.Path) + ".njmodel");
            if (!File.Exists(sourcePath) || !File.Exists(modelPath))
            {
                Assert.Ignore(
                    $"The local Bistro source and win-x64 cook are required: " +
                    $"{asset.Path}");
            }

            ContentLoadOptions loadOptions = asset.CreateLoadOptions();
            ulong expectedImportContract = CookedModelImportContract.Compute(
                sourcePath,
                loadOptions.ImporterOptions);
            CookedModelManifest manifest;
            using (var reader = new CookedAssetReader(
                       modelPath,
                       CookedAssetKind.Model,
                       CookedAssetReaderFlags.StrictSourceHash,
                       CookedHash.File(sourcePath)))
            {
                Assert.That(reader.Header.ImportSettingsHash,
                    Is.EqualTo(expectedImportContract), asset.Path);
                manifest = CookedJson.Deserialize<CookedModelManifest>(
                    reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                    modelPath,
                    "manifest");
            }

            string materialPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(modelPath)!,
                manifest.Material.RelativePath));
            CookedMaterialTable materials = CookedPackage.LoadMaterials(
                materialPath,
                CookedAssetReaderFlags.StrictSourceHash,
                out _);
            AssertCompressedTextureBindings(
                asset.Path,
                materialPath,
                materials);
        }
    }

    [Test]
    [Explicit("Requires both local Amazon Bistro source assets and their win-x64 cooks.")]
    public void BothBistroCooks_PreserveMaskedFoliageAndCoverageSemantics()
    {
        string[] expectedFoliage =
        [
            "Foliage_Bux_Hedges46_BaseColor",
            "Foliage_Flowers_BaseColor",
            "Foliage_Ivy_leaf_a_BaseColor",
            "Foliage_Leaves_BaseColor",
            "Foliage_Linde_Tree_Large_Green_Leaves_BaseColor",
            "Foliage_Linde_Tree_Large_Orange_Leaves_BaseColor",
            "Plants_plants_BaseColor"
        ];
        string[] opaqueControls =
        [
            "Foliage_Ivy_branches_BaseColor",
            "Foliage_Linde_Tree_Large_Trunk_BaseColor",
            "Foliage_Trunk_BaseColor",
            "Foliage_Paris_Flowers_BaseColor",
            "Plants_Metal_Base_01_BaseColor"
        ];

        IReadOnlyList<CookedBaseColorMaterial> entries =
            LoadBistroBaseColorMaterials();
        var foliageNames = new HashSet<string>(
            expectedFoliage,
            StringComparer.OrdinalIgnoreCase);
        var controlNames = new HashSet<string>(
            opaqueControls,
            StringComparer.OrdinalIgnoreCase);
        CookedBaseColorMaterial[] foliage = entries
            .Where(entry => foliageNames.Contains(entry.TextureStem))
            .ToArray();
        CookedBaseColorMaterial[] controls = entries
            .Where(entry => controlNames.Contains(entry.TextureStem))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                foliage.Select(entry => entry.TextureStem).Distinct(
                    StringComparer.OrdinalIgnoreCase),
                Is.EquivalentTo(expectedFoliage),
                "Every approved alpha-bearing Bistro foliage identity must be present.");
            Assert.That(
                controls.Select(entry => entry.TextureStem).Distinct(
                    StringComparer.OrdinalIgnoreCase),
                Is.EquivalentTo(opaqueControls),
                "Opaque controls must remain represented in the cooked assets.");
        });

        foreach (CookedBaseColorMaterial entry in foliage)
        {
            double sourceCoverage =
                entry.Metadata.TransportStatistics.GetAlphaCoverage(0.5);
            Assert.Multiple(() =>
            {
                Assert.That(entry.Material.AlphaMode,
                    Is.EqualTo(ModelAlphaMode.Mask), entry.Identity);
                Assert.That(entry.Material.AlphaCutoff,
                    Is.EqualTo(0.5f), entry.Identity);
                Assert.That(entry.Material.DoubleSided,
                    Is.True, entry.Identity);
                Assert.That(entry.Material.FeatureFlags & FoliageFeature,
                    Is.Not.Zero, entry.Identity);
                Assert.That(entry.Material.IsThinGlass,
                    Is.False, entry.Identity);
                Assert.That(entry.Pipeline,
                    Is.EqualTo(CookedMaterialPipeline.Foliage), entry.Identity);
                Assert.That(entry.Metadata.AlphaCoveragePreserved,
                    Is.True, entry.Identity);
                Assert.That(entry.Metadata.AlphaCoverageCutoff,
                    Is.EqualTo(0.5f), entry.Identity);
                Assert.That(sourceCoverage,
                    Is.GreaterThan(0.0).And.LessThan(1.0),
                    $"{entry.Identity} must contain meaningful cutout alpha.");
            });
        }

        foreach (CookedBaseColorMaterial entry in controls)
        {
            Assert.Multiple(() =>
            {
                Assert.That(entry.Material.AlphaMode,
                    Is.EqualTo(ModelAlphaMode.Opaque), entry.Identity);
                Assert.That(entry.Material.FeatureFlags & FoliageFeature,
                    Is.Zero, entry.Identity);
                Assert.That(entry.Material.IsThinGlass,
                    Is.False, entry.Identity);
                Assert.That(entry.Pipeline,
                    Is.EqualTo(CookedMaterialPipeline.Opaque), entry.Identity);
                Assert.That(entry.Metadata.AlphaCoveragePreserved,
                    Is.False, entry.Identity);
                Assert.That(entry.Metadata.AlphaCoverageCutoff,
                    Is.Null, entry.Identity);
            });
        }
    }

    private static IReadOnlyList<CookedBaseColorMaterial>
        LoadBistroBaseColorMaterials()
    {
        string root = FindRepositoryRoot();
        string contentRoot = Path.Combine(root, "NjulfHelloGame");
        var entries = new List<CookedBaseColorMaterial>();

        foreach (SampleAssetReference asset in
                 SampleAssetManifest.Bistro.EnumerateAssets())
        {
            string sourcePath = Path.GetFullPath(Path.Combine(
                contentRoot,
                asset.Path));
            string modelPath = Path.Combine(
                contentRoot,
                "Cooked",
                "win-x64",
                "models",
                Path.GetFileNameWithoutExtension(asset.Path) + ".njmodel");
            if (!File.Exists(sourcePath) || !File.Exists(modelPath))
            {
                Assert.Ignore(
                    $"The local Bistro source and win-x64 cook are required: " +
                    $"{asset.Path}");
            }

            ContentLoadOptions loadOptions = asset.CreateLoadOptions();
            ulong expectedImportContract = CookedModelImportContract.Compute(
                sourcePath,
                loadOptions.ImporterOptions);
            CookedModelManifest manifest;
            using (var reader = new CookedAssetReader(
                       modelPath,
                       CookedAssetKind.Model,
                       CookedAssetReaderFlags.StrictSourceHash,
                       CookedHash.File(sourcePath)))
            {
                Assert.That(reader.Header.ImportSettingsHash,
                    Is.EqualTo(expectedImportContract), asset.Path);
                manifest = CookedJson.Deserialize<CookedModelManifest>(
                    reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                    modelPath,
                    "manifest");
            }

            string materialPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(modelPath)!,
                manifest.Material.RelativePath));
            CookedMaterialTable materials = CookedPackage.LoadMaterials(
                materialPath,
                CookedAssetReaderFlags.StrictSourceHash,
                out _);
            Assert.That(materials.Pipelines,
                Has.Count.EqualTo(materials.Materials.Count), asset.Path);

            string materialDirectory = Path.GetDirectoryName(materialPath)!;
            for (int index = 0; index < materials.Materials.Count; index++)
            {
                ModelMaterial material = materials.Materials[index];
                ModelTextureSource? source = material.BaseColorTexture?.Source;
                if (source is null)
                    continue;

                CookedTextureMeta metadata = LoadBoundTextureMetadata(
                    asset.Path,
                    materialDirectory,
                    source,
                    out string texturePath);
                entries.Add(new CookedBaseColorMaterial(
                    asset.Path,
                    Path.GetFileNameWithoutExtension(metadata.SourceIdentity),
                    material,
                    materials.Pipelines[index],
                    metadata,
                    texturePath));
            }
        }

        return entries;
    }

    private static void AssertCompressedTextureBindings(
        string assetPath,
        string materialPath,
        CookedMaterialTable materials)
    {
        const uint bc5Unorm = 141;
        const uint bc7Unorm = 145;
        const uint bc7Srgb = 146;
        string materialDirectory = Path.GetDirectoryName(materialPath)!;
        ModelTextureSource[] baseColorSources = materials.Materials
            .Select(material => material.BaseColorTexture?.Source)
            .Where(static source => source is not null)
            .Cast<ModelTextureSource>()
            .GroupBy(static source => source.FilePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        ModelTextureSource[] normalSources = materials.Materials
            .Select(material => material.NormalTexture?.Source)
            .Where(static source => source is not null)
            .Cast<ModelTextureSource>()
            .GroupBy(static source => source.FilePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(baseColorSources, Is.Not.Empty,
                $"{assetPath} retained no cooked base-color bindings.");
            Assert.That(normalSources, Is.Not.Empty,
                $"{assetPath} retained no cooked normal bindings.");
        });

        foreach (ModelTextureSource source in baseColorSources)
        {
            CookedTextureMeta metadata = LoadBoundTextureMetadata(
                assetPath,
                materialDirectory,
                source,
                out string texturePath);
            Assert.Multiple(() =>
            {
                Assert.That(metadata.Semantic,
                    Is.EqualTo(TextureSemantic.Color), texturePath);
                Assert.That(metadata.VulkanFormat,
                    Is.AnyOf(bc7Unorm, bc7Srgb), texturePath);
                Assert.That(metadata.MipCount,
                    Is.EqualTo(FullMipCount(
                        metadata.CookedWidth,
                        metadata.CookedHeight)), texturePath);
            });
        }

        foreach (ModelTextureSource source in normalSources)
        {
            CookedTextureMeta metadata = LoadBoundTextureMetadata(
                assetPath,
                materialDirectory,
                source,
                out string texturePath);
            Assert.Multiple(() =>
            {
                Assert.That(metadata.Semantic,
                    Is.EqualTo(TextureSemantic.Normal), texturePath);
                Assert.That(metadata.VulkanFormat,
                    Is.EqualTo(bc5Unorm), texturePath);
                Assert.That(metadata.MipCount,
                    Is.EqualTo(FullMipCount(
                        metadata.CookedWidth,
                        metadata.CookedHeight)), texturePath);
            });
        }
    }

    private static CookedTextureMeta LoadBoundTextureMetadata(
        string assetPath,
        string materialDirectory,
        ModelTextureSource source,
        out string texturePath)
    {
        Assert.Multiple(() =>
        {
            Assert.That(source.ContainerKind,
                Is.EqualTo(TextureContainerKind.Ktx2), assetPath);
            Assert.That(source.CacheIdentity,
                Does.StartWith("cooked:"), assetPath);
            Assert.That(source.FilePath, Is.Not.Null.And.Not.Empty, assetPath);
        });

        string resolvedTexturePath = Path.GetFullPath(Path.Combine(
            materialDirectory,
            source.FilePath!));
        texturePath = resolvedTexturePath;
        string metadataPath = Path.ChangeExtension(
            resolvedTexturePath,
            ".njtex");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(resolvedTexturePath),
                Is.True,
                resolvedTexturePath);
            Assert.That(File.Exists(metadataPath), Is.True, metadataPath);
        });

        CookedTextureMeta metadata = CookedPackage.LoadTextureMeta(
            metadataPath,
            CookedAssetReaderFlags.StrictSourceHash);
        string authenticatedTexturePath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(metadataPath)!,
            metadata.Ktx2RelativePath));
        Assert.That(authenticatedTexturePath,
            Is.EqualTo(resolvedTexturePath).IgnoreCase,
            $"{assetPath} material binding differs from its authenticated KTX2.");
        return metadata;
    }

    private static int FullMipCount(int width, int height)
    {
        int count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Njulf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root containing Njulf.sln.");
    }

    private sealed record CookedBaseColorMaterial(
        string AssetPath,
        string TextureStem,
        ModelMaterial Material,
        CookedMaterialPipeline Pipeline,
        CookedTextureMeta Metadata,
        string TexturePath)
    {
        public string Identity =>
            $"{AssetPath}:{Material.Name}:{TextureStem}:{TexturePath}";
    }
}

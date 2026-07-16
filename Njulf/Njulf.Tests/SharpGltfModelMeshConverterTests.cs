using System;
using System.IO;
using System.Linq;
using System.Text;
using Njulf.Assets;
using Njulf.Core.Animation;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SharpGltfModelMeshConverterTests
{
    [Test]
    public void Import_WithSharpGltfBackend_PreservesTransformsVertexStreamsAndBounds()
    {
        string path = CreateVertexStreamGltf();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf,
                GlobalScale = 2f
            });

        Assert.Multiple(() =>
        {
            Assert.That(mesh.Vertices, Has.Length.EqualTo(3));
            Assert.That(mesh.Normals, Has.Length.EqualTo(3));
            Assert.That(mesh.Tangents, Has.Length.EqualTo(3));
            Assert.That(mesh.TexCoords, Has.Length.EqualTo(3));
            Assert.That(mesh.TexCoords1, Has.Length.EqualTo(3));
            Assert.That(mesh.VertexColors, Has.Length.EqualTo(3));
            Assert.That(mesh.Indices, Is.EqualTo(new uint[] { 0, 1, 2 }));
            Assert.That(mesh.SubMeshes, Has.Count.EqualTo(1));
            Assert.That(mesh.SubMeshes[0].MaterialIndex, Is.EqualTo(0));
            Assert.That(mesh.SubMeshes[0].NodeIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(mesh.Vertices[0].X, Is.EqualTo(20f).Within(0.00001f));
            Assert.That(mesh.Vertices[0].Y, Is.EqualTo(40f).Within(0.00001f));
            Assert.That(mesh.Vertices[0].Z, Is.EqualTo(60f).Within(0.00001f));
            Assert.That(mesh.Vertices[1].X, Is.EqualTo(22f).Within(0.00001f));
            Assert.That(mesh.Vertices[2].Y, Is.EqualTo(42f).Within(0.00001f));
            Assert.That(mesh.TexCoords[1].X, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.TexCoords1[2].Y, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.VertexColors[0].X, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.VertexColors[1].Y, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.VertexColors[2].Z, Is.EqualTo(1f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Min.X, Is.EqualTo(20f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Min.Y, Is.EqualTo(40f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Min.Z, Is.EqualTo(60f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Max.X, Is.EqualTo(22f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Max.Y, Is.EqualTo(42f).Within(0.00001f));
            Assert.That(mesh.BoundingBox.Max.Z, Is.EqualTo(60f).Within(0.00001f));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_LoadsRepositoryGlb()
    {
        string path = FindRepoFile("NjulfHelloGame", "Strut.glb");
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSuccessfully, Is.True, result.FailureMessage);
            Assert.That(result.Mesh, Is.Not.Null);
            Assert.That(result.Mesh!.Vertices, Has.Length.GreaterThan(0));
            Assert.That(result.Mesh.Indices, Has.Length.GreaterThan(0));
            Assert.That(result.Mesh.SubMeshes, Has.Count.GreaterThan(0));
            Assert.That(result.SharpGltfCapability, Is.Not.Null);
            Assert.That(result.SharpGltfCapability!.LoadedSuccessfully, Is.True);
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesRepositoryAnimatedCharacterRenderContract()
    {
        string path = FindRepoFile("NjulfHelloGame", "Strut.glb");
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        Assert.Multiple(() =>
        {
            Assert.That(mesh.SubMeshes, Has.Count.EqualTo(2));
            Assert.That(mesh.Skeletons, Has.Count.EqualTo(1));
            Assert.That(mesh.Skins, Has.Count.EqualTo(1));
            Assert.That(mesh.AnimationClips, Has.Count.EqualTo(1));
            Assert.That(mesh.SubMeshes, Is.All.Matches<ModelSubMesh>(subMesh => subMesh.SkinIndex == 0));
            Assert.That(mesh.SubMeshes, Is.All.Matches<ModelSubMesh>(subMesh => subMesh.JointIndices0.Length == subMesh.Vertices.Length));
            Assert.That(mesh.SubMeshes, Is.All.Matches<ModelSubMesh>(subMesh => subMesh.JointWeights0.Length == subMesh.Vertices.Length));
            Assert.That(mesh.BoundingBox.Size.X, Is.GreaterThan(0f).And.LessThan(10f));
            Assert.That(mesh.BoundingBox.Size.Y, Is.GreaterThan(0f).And.LessThan(10f));
            Assert.That(mesh.BoundingBox.Size.Z, Is.GreaterThan(0f).And.LessThan(10f));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_RepositoryAnimatedCharacterHasSaneSkinnedBounds()
    {
        string path = FindRepoFile("NjulfHelloGame", "Strut.glb");
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        var bindPoseAnimator = new Animator(mesh.Skeletons[0], mesh.Skins, mesh.AnimationClips);
        (Njulf.Core.Math.Vector3 min, Njulf.Core.Math.Vector3 max, Njulf.Core.Math.Vector3 size) bindPose = ComputeSkinnedBounds(
            mesh,
            bindPoseAnimator,
            static (subMesh, skin) => Njulf.Rendering.Resources.SkinningManager.ApplySkinningBindTransform(subMesh.SkinningBindTransform, skin));

        var animator = new Animator(mesh.Skeletons[0], mesh.Skins, mesh.AnimationClips);
        animator.Play(mesh.AnimationClips[0], loop: true);
        animator.Seek(0f);

        (Njulf.Core.Math.Vector3 min, Njulf.Core.Math.Vector3 max, Njulf.Core.Math.Vector3 size) current = ComputeSkinnedBounds(
            mesh,
            animator,
            static (subMesh, skin) => Njulf.Rendering.Resources.SkinningManager.ApplySkinningBindTransform(subMesh.SkinningBindTransform, skin));
        Njulf.Core.Math.Vector3 hips = animator.CurrentPose.GlobalMatrices[FindJoint(mesh.Skeletons[0], "mixamorig:Hips")].Translation;
        Njulf.Core.Math.Vector3 leftHand = animator.CurrentPose.GlobalMatrices[FindJoint(mesh.Skeletons[0], "mixamorig:LeftHand")].Translation;
        Njulf.Core.Math.Vector3 rightHand = animator.CurrentPose.GlobalMatrices[FindJoint(mesh.Skeletons[0], "mixamorig:RightHand")].Translation;

        Njulf.Core.Math.Vector3 size = current.size;
        string details =
            $"bindPose min={bindPose.min}, max={bindPose.max}, size={bindPose.size}; " +
            $"current min={current.min}, max={current.max}, size={current.size}; " +
            $"hips={hips}; leftHand={leftHand}; rightHand={rightHand}";
        Assert.Multiple(() =>
        {
            Assert.That(bindPose.size.X, Is.GreaterThan(1.5f).And.LessThan(3f), details);
            Assert.That(bindPose.size.Y, Is.GreaterThan(1.5f).And.LessThan(3f), details);
            Assert.That(bindPose.size.Z, Is.GreaterThan(0f).And.LessThan(0.75f), details);
            Assert.That(size.X, Is.GreaterThan(0f).And.LessThan(2f), details);
            Assert.That(size.Y, Is.GreaterThan(1.5f).And.LessThan(10f), details);
            Assert.That(size.Z, Is.GreaterThan(0f).And.LessThan(1.25f), details);
            Assert.That(size.Y, Is.GreaterThan(size.Z), details);
            Assert.That(leftHand.X, Is.GreaterThan(0.25f).And.LessThan(0.5f), details);
            Assert.That(rightHand.X, Is.LessThan(-0.25f).And.GreaterThan(-0.5f), details);
            Assert.That(leftHand.Z, Is.GreaterThan(0f).And.LessThan(0.25f), details);
            Assert.That(rightHand.Z, Is.GreaterThan(0f).And.LessThan(0.25f), details);
        });
    }

    private static int FindJoint(Skeleton skeleton, string name)
    {
        for (int i = 0; i < skeleton.Joints.Count; i++)
        {
            if (string.Equals(skeleton.Joints[i].Name, name, StringComparison.Ordinal))
                return i;
        }

        throw new InvalidOperationException($"Joint '{name}' was not found.");
    }

    private static (Njulf.Core.Math.Vector3 min, Njulf.Core.Math.Vector3 max, Njulf.Core.Math.Vector3 size) ComputeSkinnedBounds(
        ModelMesh mesh,
        Animator animator,
        Func<ModelSubMesh, Njulf.Core.Math.Matrix4x4, Njulf.Core.Math.Matrix4x4> transformSkinMatrix)
    {
        var min = new Njulf.Core.Math.Vector3(float.MaxValue);
        var max = new Njulf.Core.Math.Vector3(float.MinValue);
        foreach (ModelSubMesh subMesh in mesh.SubMeshes)
        {
            Njulf.Core.Math.Matrix4x4[] skinMatrices = animator.GetSkinMatrices(subMesh.SkinIndex).ToArray();
            for (int i = 0; i < skinMatrices.Length; i++)
                skinMatrices[i] = transformSkinMatrix(subMesh, skinMatrices[i]);

            for (int i = 0; i < subMesh.Vertices.Length; i++)
            {
                Njulf.Core.Math.Vector3 position = CpuSkinning.SkinPosition(
                    subMesh.Vertices[i],
                    subMesh.JointIndices0[i],
                    subMesh.JointWeights0[i],
                    skinMatrices);

                min.X = Math.Min(min.X, position.X);
                min.Y = Math.Min(min.Y, position.Y);
                min.Z = Math.Min(min.Z, position.Z);
                max.X = Math.Max(max.X, position.X);
                max.Y = Math.Max(max.Y, position.Y);
                max.Z = Math.Max(max.Z, position.Z);
            }
        }

        Njulf.Core.Math.Vector3 size = max - min;
        return (min, max, size);
    }

    [Test]
    public void ImportDetailed_WithSharpGltfBackend_TreeCrashCandidateReturnsManagedResult()
    {
        string path = FindRepoFile("NjulfHelloGame", "Assets", "low_poly_trees_free.glb");
        using var importer = new ModelImporter();

        ModelImportResult result = importer.ImportDetailed(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        Assert.Multiple(() =>
        {
            Assert.That(result.Backend, Is.EqualTo(ModelImportBackend.SharpGltf));
            Assert.That(result.Status, Is.AnyOf(ModelImportStatus.Imported, ModelImportStatus.Failed));
            Assert.That(result.SharpGltfCapability, Is.Not.Null);
            if (result.ImportedSuccessfully)
            {
                Assert.That(result.Mesh, Is.Not.Null);
                Assert.That(result.Mesh!.Vertices, Has.Length.GreaterThan(0));
                Assert.That(result.Mesh.SubMeshes, Has.Count.GreaterThan(0));
            }
            else
            {
                Assert.That(result.FailureType, Is.Not.Empty);
                Assert.That(result.FailureMessage, Is.Not.Empty);
            }
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesSkinAndAnimationChannels()
    {
        string path = CreateAnimatedSkinGltf();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf,
                FlipUVs = false,
                GenerateNormals = false,
                GenerateTangents = false
            });

        AnimationClip clip = mesh.AnimationClips.Single();
        AnimationChannel translation = clip.Channels.Single(channel => channel.Path == AnimationChannelPath.Translation);
        AnimationChannel rotation = clip.Channels.Single(channel => channel.Path == AnimationChannelPath.Rotation);
        AnimationChannel scale = clip.Channels.Single(channel => channel.Path == AnimationChannelPath.Scale);

        Assert.Multiple(() =>
        {
            Assert.That(mesh.Skeletons, Has.Count.EqualTo(1));
            Assert.That(mesh.Skins, Has.Count.EqualTo(1));
            Assert.That(mesh.SubMeshes, Has.Count.EqualTo(1));
            Assert.That(mesh.SubMeshes[0].SkinIndex, Is.EqualTo(0));
            Assert.That(mesh.SubMeshes[0].JointIndices0, Has.Length.EqualTo(3));
            Assert.That(mesh.SubMeshes[0].JointWeights0, Has.Length.EqualTo(3));
            Assert.That(mesh.SubMeshes[0].JointIndices0[1].X, Is.EqualTo(1));
            Assert.That(mesh.SubMeshes[0].JointWeights0[1].X, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(mesh.AnimationDiagnostics.SkeletonCount, Is.EqualTo(1));
            Assert.That(mesh.AnimationDiagnostics.JointCount, Is.EqualTo(2));
            Assert.That(mesh.AnimationDiagnostics.SkinCount, Is.EqualTo(1));
            Assert.That(mesh.AnimationDiagnostics.SkinnedSubMeshCount, Is.EqualTo(1));
            Assert.That(mesh.AnimationDiagnostics.AnimationClipCount, Is.EqualTo(1));
            Assert.That(mesh.AnimationDiagnostics.AnimationChannelCount, Is.EqualTo(3));

            Assert.That(clip.Name, Is.EqualTo("SharpTurn"));
            Assert.That(clip.DurationSeconds, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(translation.TargetJointIndex, Is.EqualTo(1));
            Assert.That(translation.TargetNodeIndex, Is.EqualTo(1));
            Assert.That(translation.Sampler.Interpolation, Is.EqualTo(AnimationInterpolation.Step));
            Assert.That(translation.Sampler.InputTimes, Is.EqualTo(new[] { 0f, 1f }));
            Assert.That(translation.Sampler.OutputValues[1].X, Is.EqualTo(2f).Within(0.0001f));

            Assert.That(rotation.Sampler.Interpolation, Is.EqualTo(AnimationInterpolation.Linear));
            Assert.That(rotation.Sampler.OutputValues[1].X, Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(rotation.Sampler.OutputValues[1].Y, Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(rotation.Sampler.OutputValues[1].Z, Is.EqualTo(-0.5f).Within(0.0001f));
            Assert.That(rotation.Sampler.OutputValues[1].W, Is.EqualTo(0.5f).Within(0.0001f));

            Assert.That(scale.Sampler.Interpolation, Is.EqualTo(AnimationInterpolation.CubicSpline));
            Assert.That(scale.Sampler.InputTimes, Is.EqualTo(new[] { 0f, 1f }));
            Assert.That(scale.Sampler.OutputValues, Has.Count.EqualTo(6));
            Assert.That(scale.Sampler.OutputValues[4].X, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(scale.Sampler.OutputValues[4].Y, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(scale.Sampler.OutputValues[4].Z, Is.EqualTo(2f).Within(0.0001f));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesMaterialTextureSlots()
    {
        string path = CreateTexturedMaterialGltf();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        ModelMaterial material = mesh.Materials.Single();
        ModelTextureSlot baseColor = material.BaseColorTexture!;
        ModelTextureSlot normal = material.NormalTexture!;
        ModelTextureSlot metallicRoughness = material.MetallicRoughnessTexture!;
        ModelTextureSlot occlusion = material.OcclusionTexture!;
        ModelTextureSlot emissive = material.EmissiveTexture!;

        Assert.Multiple(() =>
        {
            Assert.That(material.Name, Is.EqualTo("TexturedMaterial"));
            Assert.That(material.AlphaMode, Is.EqualTo(ModelAlphaMode.Mask));
            Assert.That(material.AlphaCutoff, Is.EqualTo(0.33f).Within(0.0001f));
            Assert.That(material.DoubleSided, Is.True);
            Assert.That(material.Albedo.X, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(material.Albedo.Y, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(material.Albedo.Z, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(material.Albedo.W, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(material.Metallic, Is.EqualTo(0.7f).Within(0.0001f));
            Assert.That(material.Roughness, Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(material.NormalScale, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(material.AmbientOcclusion, Is.EqualTo(0.6f).Within(0.0001f));
            Assert.That(material.Emissive.X, Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(material.Emissive.Y, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(material.Emissive.Z, Is.EqualTo(0.3f).Within(0.0001f));

            Assert.That(baseColor.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));
            Assert.That(baseColor.TexCoordSet, Is.EqualTo(1));
            Assert.That(baseColor.Offset.X, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(baseColor.Offset.Y, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(baseColor.Scale.X, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(baseColor.Scale.Y, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(baseColor.RotationRadians, Is.EqualTo(0.125f).Within(0.0001f));
            Assert.That(baseColor.Sampler.WrapU, Is.EqualTo(TextureWrapMode.ClampToEdge));
            Assert.That(baseColor.Sampler.WrapV, Is.EqualTo(TextureWrapMode.MirroredRepeat));
            Assert.That(baseColor.Sampler.MinFilter, Is.EqualTo(TextureFilterMode.Nearest));
            Assert.That(baseColor.Sampler.MagFilter, Is.EqualTo(TextureFilterMode.Nearest));
            Assert.That(baseColor.Source!.FilePath, Is.Not.Null);
            Assert.That(File.Exists(baseColor.Source!.FilePath), Is.True);
            Assert.That(baseColor.Source.SourceKind, Is.EqualTo(TextureSourceKind.ExternalFile));
            Assert.That(baseColor.Source.EncodedByteLength, Is.EqualTo(OnePixelPng().Length));
            Assert.That(baseColor.Source.ContainerKind, Is.EqualTo(TextureContainerKind.StandardImage));
            Assert.That(baseColor.Source.CacheIdentity, Is.EqualTo(Path.GetFullPath(baseColor.Source.FilePath!)));

            Assert.That(normal.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
            Assert.That(metallicRoughness.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
            Assert.That(occlusion.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
            Assert.That(emissive.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));
            Assert.That(mesh.ImportDiagnostics.ImportedSamplerCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.TextureTransformCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.ExternalImageCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesGlbEmbeddedImageSource()
    {
        string path = CreateEmbeddedImageGlb();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        ModelTextureSource source = mesh.Materials.Single().BaseColorTexture!.Source!;

        Assert.Multiple(() =>
        {
            Assert.That(source.SourceKind, Is.EqualTo(TextureSourceKind.GlbBinary));
            Assert.That(source.FilePath, Is.Null);
            Assert.That(source.Bytes, Is.Not.Null);
            Assert.That(source.Bytes!, Has.Length.EqualTo(OnePixelPng().Length));
            Assert.That(source.EncodedByteLength, Is.EqualTo(OnePixelPng().Length));
            Assert.That(source.MimeType, Is.EqualTo("image/png"));
            Assert.That(source.ContainerKind, Is.EqualTo(TextureContainerKind.StandardImage));
            Assert.That(source.CacheIdentity, Does.Contain("#image:0"));
            Assert.That(mesh.ImportDiagnostics.EmbeddedImageCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.BufferViewImageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesDataUriImageSource()
    {
        string path = CreateImageSourceGltf(TextureSourceKind.DataUri);
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        ModelTextureSource source = mesh.Materials.Single().BaseColorTexture!.Source!;

        Assert.Multiple(() =>
        {
            Assert.That(source.SourceKind, Is.EqualTo(TextureSourceKind.DataUri));
            Assert.That(source.FilePath, Is.Null);
            Assert.That(source.Bytes, Is.Not.Null);
            Assert.That(source.Bytes!, Has.Length.EqualTo(OnePixelPng().Length));
            Assert.That(source.EncodedByteLength, Is.EqualTo(OnePixelPng().Length));
            Assert.That(source.CacheIdentity, Does.Contain("#image:0:data"));
            Assert.That(mesh.ImportDiagnostics.EmbeddedImageCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.DataUriImageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesBufferViewImageSource()
    {
        string path = CreateImageSourceGltf(TextureSourceKind.BufferView);
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        ModelTextureSource source = mesh.Materials.Single().BaseColorTexture!.Source!;

        Assert.Multiple(() =>
        {
            Assert.That(source.SourceKind, Is.EqualTo(TextureSourceKind.BufferView));
            Assert.That(source.FilePath, Is.Null);
            Assert.That(source.Bytes, Is.Not.Null);
            Assert.That(source.Bytes!, Has.Length.EqualTo(OnePixelPng().Length));
            Assert.That(source.EncodedByteLength, Is.EqualTo(OnePixelPng().Length));
            Assert.That(source.CacheIdentity, Does.Contain("#image:0:bufferView:2"));
            Assert.That(mesh.ImportDiagnostics.EmbeddedImageCount, Is.EqualTo(1));
            Assert.That(mesh.ImportDiagnostics.BufferViewImageCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Import_WithSharpGltfBackend_PreservesMaterialExtensions()
    {
        string path = CreateExtensionMaterialGltf();
        using var importer = new ModelImporter();

        ModelMesh mesh = importer.Import(
            path,
            new ImporterOptions
            {
                Backend = ModelImportBackend.SharpGltf
            });

        ModelMaterial material = mesh.Materials.Single(imported => imported.Name == "ExtensionMaterial");
        ModelMaterial unlitMaterial = mesh.Materials.Single(imported => imported.Name == "UnlitMaterial");

        Assert.Multiple(() =>
        {
            Assert.That(unlitMaterial.Unlit, Is.True);
            Assert.That(material.Ior, Is.EqualTo(1.6f).Within(0.0001f));
            Assert.That(material.Dispersion, Is.EqualTo(0.24f).Within(0.0001f));
            Assert.That(material.EmissiveStrength, Is.EqualTo(2.5f).Within(0.0001f));

            Assert.That(material.ClearcoatFactor, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(material.ClearcoatRoughness, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(material.ClearcoatNormalScale, Is.EqualTo(0.4f).Within(0.0001f));
            Assert.That(material.ClearcoatTexture, Is.Not.Null);
            Assert.That(material.ClearcoatRoughnessTexture, Is.Not.Null);
            Assert.That(material.ClearcoatNormalTexture, Is.Not.Null);

            Assert.That(material.SheenColor.X, Is.EqualTo(0.41f).Within(0.0001f));
            Assert.That(material.SheenColor.Y, Is.EqualTo(0.42f).Within(0.0001f));
            Assert.That(material.SheenColor.Z, Is.EqualTo(0.43f).Within(0.0001f));
            Assert.That(material.SheenRoughness, Is.EqualTo(0.44f).Within(0.0001f));
            Assert.That(material.SheenColorTexture!.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));
            Assert.That(material.SheenRoughnessTexture!.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));

            Assert.That(material.AnisotropyStrength, Is.EqualTo(0.51f).Within(0.0001f));
            Assert.That(material.AnisotropyRotation, Is.EqualTo(0.52f).Within(0.0001f));
            Assert.That(material.AnisotropyTexture, Is.Not.Null);

            Assert.That(material.TransmissionFactor, Is.EqualTo(0.61f).Within(0.0001f));
            Assert.That(material.TransmissionTexture, Is.Not.Null);
            Assert.That(material.ThicknessFactor, Is.EqualTo(0.71f).Within(0.0001f));
            Assert.That(material.AttenuationDistance, Is.EqualTo(7.2f).Within(0.0001f));
            Assert.That(material.AttenuationColor.X, Is.EqualTo(0.73f).Within(0.0001f));
            Assert.That(material.AttenuationColor.Y, Is.EqualTo(0.74f).Within(0.0001f));
            Assert.That(material.AttenuationColor.Z, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(material.ThicknessTexture, Is.Not.Null);

            Assert.That(material.SpecularFactor, Is.EqualTo(0.81f).Within(0.0001f));
            Assert.That(material.SpecularColor.X, Is.EqualTo(0.82f).Within(0.0001f));
            Assert.That(material.SpecularColor.Y, Is.EqualTo(0.83f).Within(0.0001f));
            Assert.That(material.SpecularColor.Z, Is.EqualTo(0.84f).Within(0.0001f));
            Assert.That(material.SpecularTexture!.ColorSpace, Is.EqualTo(TextureColorSpace.Linear));
            Assert.That(material.SpecularColorTexture!.ColorSpace, Is.EqualTo(TextureColorSpace.Srgb));

            Assert.That(material.IridescenceFactor, Is.EqualTo(0.91f).Within(0.0001f));
            Assert.That(material.IridescenceIor, Is.EqualTo(1.45f).Within(0.0001f));
            Assert.That(material.IridescenceThicknessMinimum, Is.EqualTo(120f).Within(0.0001f));
            Assert.That(material.IridescenceThicknessMaximum, Is.EqualTo(420f).Within(0.0001f));
            Assert.That(material.IridescenceTexture, Is.Not.Null);
            Assert.That(material.IridescenceThicknessTexture, Is.Not.Null);

            Assert.That((material.FeatureFlags & (1u << 0)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 4)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 7)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 9)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 11)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 14)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 15)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 18)), Is.Not.EqualTo(0u));
            Assert.That((material.FeatureFlags & (1u << 21)), Is.Not.EqualTo(0u));
        });
    }

    private static string CreateVertexStreamGltf()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        byte[] positions = Floats(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f);
        byte[] normals = Floats(
            0f, 0f, 1f,
            0f, 0f, 1f,
            0f, 0f, 1f);
        byte[] tangents = Floats(
            1f, 0f, 0f, 1f,
            1f, 0f, 0f, 1f,
            1f, 0f, 0f, 1f);
        byte[] uv0 = Floats(
            0f, 0f,
            1f, 0f,
            0f, 1f);
        byte[] uv1 = Floats(
            0f, 0f,
            0.5f, 0f,
            0f, 1f);
        byte[] colors = Floats(
            1f, 0f, 0f, 1f,
            0f, 1f, 0f, 1f,
            0f, 0f, 1f, 1f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];

        int normalOffset = positions.Length;
        int tangentOffset = normalOffset + normals.Length;
        int uv0Offset = tangentOffset + tangents.Length;
        int uv1Offset = uv0Offset + uv0.Length;
        int colorOffset = uv1Offset + uv1.Length;
        int indexOffset = colorOffset + colors.Length;
        byte[] data = positions
            .Concat(normals)
            .Concat(tangents)
            .Concat(uv0)
            .Concat(uv1)
            .Concat(colors)
            .Concat(indices)
            .ToArray();
        File.WriteAllBytes(binPath, data);

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf Phase 2 SharpGLTF test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "TransformedTriangle", "translation": [10, 20, 30] }],
                "meshes": [
                  {
                    "name": "Triangle",
                    "primitives": [
                      {
                        "attributes": {
                          "POSITION": 0,
                          "NORMAL": 1,
                          "TANGENT": 2,
                          "TEXCOORD_0": 3,
                          "TEXCOORD_1": 4,
                          "COLOR_0": 5
                        },
                        "indices": 6,
                        "mode": 4,
                        "material": 0
                      }
                    ]
                  }
                ],
                "materials": [
                  {
                    "name": "MaskedMaterial",
                    "alphaMode": "MASK",
                    "alphaCutoff": 0.45,
                    "doubleSided": true,
                    "pbrMetallicRoughness": {
                      "baseColorFactor": [1, 1, 1, 1],
                      "metallicFactor": 0,
                      "roughnessFactor": 1
                    }
                  }
                ],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{data.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{normalOffset}}, "byteLength": {{normals.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{tangentOffset}}, "byteLength": {{tangents.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{uv0Offset}}, "byteLength": {{uv0.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{uv1Offset}}, "byteLength": {{uv1.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{colorOffset}}, "byteLength": {{colors.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5126, "count": 3, "type": "VEC3" },
                  { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC4" },
                  { "bufferView": 3, "componentType": 5126, "count": 3, "type": "VEC2" },
                  { "bufferView": 4, "componentType": 5126, "count": 3, "type": "VEC2" },
                  { "bufferView": 5, "componentType": 5126, "count": 3, "type": "VEC4" },
                  { "bufferView": 6, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);

        return gltfPath;
    }

    private static string CreateAnimatedSkinGltf()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        int positionOffset;
        int jointsOffset;
        int weightsOffset;
        int indicesOffset;
        int inverseBindOffset;
        int animationInputOffset;
        int translationOutputOffset;
        int rotationOutputOffset;
        int scaleOutputOffset;

        using (var stream = File.Create(binPath))
        using (var writer = new BinaryWriter(stream))
        {
            positionOffset = checked((int)stream.Position);
            WriteVec3(writer, 0f, 0f, 0f);
            WriteVec3(writer, 1f, 0f, 0f);
            WriteVec3(writer, 0f, 1f, 0f);

            jointsOffset = checked((int)stream.Position);
            WriteUShort4(writer, 0, 0, 0, 0);
            WriteUShort4(writer, 1, 0, 0, 0);
            WriteUShort4(writer, 0, 0, 0, 0);

            weightsOffset = checked((int)stream.Position);
            WriteVec4(writer, 1f, 0f, 0f, 0f);
            WriteVec4(writer, 1f, 0f, 0f, 0f);
            WriteVec4(writer, 1f, 0f, 0f, 0f);

            indicesOffset = checked((int)stream.Position);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            Align4(stream, writer);

            inverseBindOffset = checked((int)stream.Position);
            WriteMatrix(writer, tx: 0f);
            WriteMatrix(writer, tx: -1f);

            animationInputOffset = checked((int)stream.Position);
            writer.Write(0f);
            writer.Write(1f);

            translationOutputOffset = checked((int)stream.Position);
            WriteVec3(writer, 1f, 0f, 0f);
            WriteVec3(writer, 2f, 0f, 0f);

            rotationOutputOffset = checked((int)stream.Position);
            WriteVec4(writer, 0f, 0f, 0f, 1f);
            WriteVec4(writer, 0.5f, 0.5f, 0.5f, 0.5f);

            scaleOutputOffset = checked((int)stream.Position);
            WriteVec3(writer, 0f, 0f, 0f);
            WriteVec3(writer, 1f, 1f, 1f);
            WriteVec3(writer, 0f, 0f, 0f);
            WriteVec3(writer, 0f, 0f, 0f);
            WriteVec3(writer, 2f, 2f, 2f);
            WriteVec3(writer, 0f, 0f, 0f);
        }

        int byteLength = checked((int)new FileInfo(binPath).Length);
        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf SharpGLTF animation test" },
                "scene": 0,
                "scenes": [{ "nodes": [0, 2] }],
                "nodes": [
                  { "name": "Root", "children": [1] },
                  { "name": "Tip", "translation": [1, 0, 0] },
                  { "name": "SkinnedTriangle", "mesh": 0, "skin": 0 }
                ],
                "skins": [
                  { "name": "TriangleSkin", "skeleton": 0, "joints": [0, 1], "inverseBindMatrices": 4 }
                ],
                "meshes": [
                  {
                    "name": "TriangleMesh",
                    "primitives": [
                      {
                        "attributes": { "POSITION": 0, "JOINTS_0": 1, "WEIGHTS_0": 2 },
                        "indices": 3,
                        "mode": 4
                      }
                    ]
                  }
                ],
                "animations": [
                  {
                    "name": "SharpTurn",
                    "samplers": [
                      { "input": 5, "output": 6, "interpolation": "STEP" },
                      { "input": 5, "output": 7, "interpolation": "LINEAR" },
                      { "input": 5, "output": 8, "interpolation": "CUBICSPLINE" }
                    ],
                    "channels": [
                      { "sampler": 0, "target": { "node": 1, "path": "translation" } },
                      { "sampler": 1, "target": { "node": 1, "path": "rotation" } },
                      { "sampler": 2, "target": { "node": 1, "path": "scale" } }
                    ]
                  }
                ],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{byteLength}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": {{positionOffset}}, "byteLength": 36, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{jointsOffset}}, "byteLength": 24, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{weightsOffset}}, "byteLength": 48, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{indicesOffset}}, "byteLength": 6, "target": 34963 },
                  { "buffer": 0, "byteOffset": {{inverseBindOffset}}, "byteLength": 128 },
                  { "buffer": 0, "byteOffset": {{animationInputOffset}}, "byteLength": 8 },
                  { "buffer": 0, "byteOffset": {{translationOutputOffset}}, "byteLength": 24 },
                  { "buffer": 0, "byteOffset": {{rotationOutputOffset}}, "byteLength": 32 },
                  { "buffer": 0, "byteOffset": {{scaleOutputOffset}}, "byteLength": 72 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "VEC4" },
                  { "bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC4" },
                  { "bufferView": 3, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] },
                  { "bufferView": 4, "componentType": 5126, "count": 2, "type": "MAT4" },
                  { "bufferView": 5, "componentType": 5126, "count": 2, "type": "SCALAR" },
                  { "bufferView": 6, "componentType": 5126, "count": 2, "type": "VEC3" },
                  { "bufferView": 7, "componentType": 5126, "count": 2, "type": "VEC4" },
                  { "bufferView": 8, "componentType": 5126, "count": 6, "type": "VEC3" }
                ]
              }
              """);

        return gltfPath;
    }

    private static string CreateTexturedMaterialGltf()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string albedoPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}-albedo.png");
        string normalPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}-normal.png");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        File.WriteAllBytes(albedoPath, OnePixelPng());
        File.WriteAllBytes(normalPath, OnePixelPng());

        byte[] positions = Floats(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        byte[] data = positions.Concat(indices).ToArray();
        int indexOffset = positions.Length;
        File.WriteAllBytes(binPath, data);

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf SharpGLTF material test" },
                "extensionsUsed": [ "KHR_texture_transform", "KHR_materials_emissive_strength" ],
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "TexturedTriangle" }],
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
                    "name": "TexturedMaterial",
                    "alphaMode": "MASK",
                    "alphaCutoff": 0.33,
                    "doubleSided": true,
                    "emissiveFactor": [0.1, 0.2, 0.3],
                    "pbrMetallicRoughness": {
                      "baseColorFactor": [0.2, 0.3, 0.4, 0.5],
                      "metallicFactor": 0.7,
                      "roughnessFactor": 0.8,
                      "baseColorTexture": {
                        "index": 0,
                        "texCoord": 0,
                        "extensions": {
                          "KHR_texture_transform": {
                            "offset": [0.25, 0.5],
                            "scale": [0.5, 0.75],
                            "rotation": 0.125,
                            "texCoord": 1
                          }
                        }
                      },
                      "metallicRoughnessTexture": { "index": 1 }
                    },
                    "normalTexture": { "index": 1, "scale": 0.4 },
                    "occlusionTexture": { "index": 1, "strength": 0.6 },
                    "emissiveTexture": { "index": 0 },
                    "extensions": {
                      "KHR_materials_emissive_strength": { "emissiveStrength": 2.5 }
                    }
                  }
                ],
                "images": [
                  { "name": "Albedo", "uri": "{{Path.GetFileName(albedoPath)}}", "mimeType": "image/png" },
                  { "name": "Normal", "uri": "{{Path.GetFileName(normalPath)}}", "mimeType": "image/png" }
                ],
                "textures": [
                  { "sampler": 0, "source": 0 },
                  { "sampler": 0, "source": 1 }
                ],
                "samplers": [
                  { "wrapS": 33071, "wrapT": 33648, "minFilter": 9728, "magFilter": 9728 }
                ],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{data.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);

        return gltfPath;
    }

    private static string CreateEmbeddedImageGlb()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string glbPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.glb");

        byte[] positions = Floats(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        byte[] image = OnePixelPng();
        byte[] indexPadding = [0, 0];
        int imageOffset = positions.Length + indices.Length + indexPadding.Length;
        byte[] bin = positions.Concat(indices).Concat(indexPadding).Concat(image).ToArray();

        string json = $$"""
            {
              "asset": { "version": "2.0", "generator": "Njulf SharpGLTF embedded image GLB test" },
              "scene": 0,
              "scenes": [{ "nodes": [0] }],
              "nodes": [{ "mesh": 0, "name": "EmbeddedTextureTriangle" }],
              "meshes": [
                {
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
                  "pbrMetallicRoughness": {
                    "baseColorTexture": { "index": 0 }
                  }
                }
              ],
              "images": [{ "bufferView": 2, "mimeType": "image/png" }],
              "textures": [{ "source": 0 }],
              "buffers": [{ "byteLength": {{bin.Length}} }],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                { "buffer": 0, "byteOffset": {{positions.Length}}, "byteLength": {{indices.Length}}, "target": 34963 },
                { "buffer": 0, "byteOffset": {{imageOffset}}, "byteLength": {{image.Length}} }
              ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
              ]
            }
            """;

        WriteGlb(glbPath, json, bin);
        return glbPath;
    }

    private static string CreateImageSourceGltf(TextureSourceKind sourceKind)
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        byte[] positions = Floats(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        byte[] image = OnePixelPng();
        byte[] indexPadding = [0, 0];
        int imageOffset = positions.Length + indices.Length + indexPadding.Length;
        byte[] data = sourceKind == TextureSourceKind.BufferView
            ? positions.Concat(indices).Concat(indexPadding).Concat(image).ToArray()
            : positions.Concat(indices).ToArray();
        int indexOffset = positions.Length;
        File.WriteAllBytes(binPath, data);

        string imageJson = sourceKind switch
        {
            TextureSourceKind.DataUri => $$"""{ "name": "DataUriTexture", "uri": "data:image/png;base64,{{Convert.ToBase64String(image)}}", "mimeType": "image/png" }""",
            TextureSourceKind.BufferView => $$"""{ "name": "BufferViewTexture", "bufferView": 2, "mimeType": "image/png" }""",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind), sourceKind, null)
        };
        string imageBufferViewJson = sourceKind == TextureSourceKind.BufferView
            ? $",{Environment.NewLine}                  {{ \"buffer\": 0, \"byteOffset\": {imageOffset}, \"byteLength\": {image.Length} }}"
            : string.Empty;

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf SharpGLTF image source test" },
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "ImageSourceTriangle" }],
                "meshes": [
                  {
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
                    "pbrMetallicRoughness": {
                      "baseColorTexture": { "index": 0 }
                    }
                  }
                ],
                "images": [{{imageJson}}],
                "textures": [{ "source": 0 }],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{data.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indices.Length}}, "target": 34963 }
                  {{imageBufferViewJson}}
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);

        return gltfPath;
    }

    private static string CreateExtensionMaterialGltf()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "sharpgltf-modelmesh-tests");
        Directory.CreateDirectory(directory);
        string binPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.bin");
        string texturePath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}-texture.png");
        string gltfPath = Path.Combine(directory, $"{TestContext.CurrentContext.Test.ID}.gltf");

        File.WriteAllBytes(texturePath, OnePixelPng());

        byte[] positions = Floats(
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f);
        byte[] indices =
        [
            .. BitConverter.GetBytes((ushort)0),
            .. BitConverter.GetBytes((ushort)1),
            .. BitConverter.GetBytes((ushort)2)
        ];
        byte[] data = positions.Concat(indices).ToArray();
        int indexOffset = positions.Length;
        File.WriteAllBytes(binPath, data);

        File.WriteAllText(
            gltfPath,
            $$"""
              {
                "asset": { "version": "2.0", "generator": "Njulf SharpGLTF extension material test" },
                "extensionsUsed": [
                  "KHR_materials_clearcoat",
                  "KHR_materials_sheen",
                  "KHR_materials_anisotropy",
                  "KHR_materials_transmission",
                  "KHR_materials_volume",
                  "KHR_materials_ior",
                  "KHR_materials_specular",
                  "KHR_materials_iridescence",
                  "KHR_materials_dispersion",
                  "KHR_materials_emissive_strength",
                  "KHR_materials_unlit"
                ],
                "scene": 0,
                "scenes": [{ "nodes": [0] }],
                "nodes": [{ "mesh": 0, "name": "ExtensionTriangle" }],
                "meshes": [
                  {
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
                    "name": "ExtensionMaterial",
                    "emissiveFactor": [0.1, 0.2, 0.3],
                    "extensions": {
                      "KHR_materials_clearcoat": {
                        "clearcoatFactor": 0.2,
                        "clearcoatRoughnessFactor": 0.3,
                        "clearcoatTexture": { "index": 0 },
                        "clearcoatRoughnessTexture": { "index": 0 },
                        "clearcoatNormalTexture": { "index": 0, "scale": 0.4 }
                      },
                      "KHR_materials_sheen": {
                        "sheenColorFactor": [0.41, 0.42, 0.43],
                        "sheenRoughnessFactor": 0.44,
                        "sheenColorTexture": { "index": 0 },
                        "sheenRoughnessTexture": { "index": 0 }
                      },
                      "KHR_materials_anisotropy": {
                        "anisotropyStrength": 0.51,
                        "anisotropyRotation": 0.52,
                        "anisotropyTexture": { "index": 0 }
                      },
                      "KHR_materials_transmission": {
                        "transmissionFactor": 0.61,
                        "transmissionTexture": { "index": 0 }
                      },
                      "KHR_materials_volume": {
                        "thicknessFactor": 0.71,
                        "thicknessTexture": { "index": 0 },
                        "attenuationDistance": 7.2,
                        "attenuationColor": [0.73, 0.74, 0.75]
                      },
                      "KHR_materials_ior": { "ior": 1.6 },
                      "KHR_materials_specular": {
                        "specularFactor": 0.81,
                        "specularColorFactor": [0.82, 0.83, 0.84],
                        "specularTexture": { "index": 0 },
                        "specularColorTexture": { "index": 0 }
                      },
                      "KHR_materials_iridescence": {
                        "iridescenceFactor": 0.91,
                        "iridescenceIor": 1.45,
                        "iridescenceThicknessMinimum": 120,
                        "iridescenceThicknessMaximum": 420,
                        "iridescenceTexture": { "index": 0 },
                        "iridescenceThicknessTexture": { "index": 0 }
                      },
                      "KHR_materials_dispersion": { "dispersion": 0.24 },
                      "KHR_materials_emissive_strength": { "emissiveStrength": 2.5 }
                    }
                  },
                  {
                    "name": "UnlitMaterial",
                    "extensions": {
                      "KHR_materials_unlit": {}
                    }
                  }
                ],
                "images": [{ "name": "ExtensionTexture", "uri": "{{Path.GetFileName(texturePath)}}", "mimeType": "image/png" }],
                "textures": [{ "source": 0 }],
                "buffers": [{ "uri": "{{Path.GetFileName(binPath)}}", "byteLength": {{data.Length}} }],
                "bufferViews": [
                  { "buffer": 0, "byteOffset": 0, "byteLength": {{positions.Length}}, "target": 34962 },
                  { "buffer": 0, "byteOffset": {{indexOffset}}, "byteLength": {{indices.Length}}, "target": 34963 }
                ],
                "accessors": [
                  { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0, 0, 0], "max": [1, 1, 0] },
                  { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR", "min": [0], "max": [2] }
                ]
              }
              """);

        return gltfPath;
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

    private static void Align4(Stream stream, BinaryWriter writer)
    {
        while ((stream.Position & 3) != 0)
            writer.Write((byte)0);
    }

    private static void WriteVec3(BinaryWriter writer, float x, float y, float z)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
    }

    private static void WriteVec4(BinaryWriter writer, float x, float y, float z, float w)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(w);
    }

    private static void WriteUShort4(BinaryWriter writer, ushort x, ushort y, ushort z, ushort w)
    {
        writer.Write(x);
        writer.Write(y);
        writer.Write(z);
        writer.Write(w);
    }

    private static void WriteMatrix(BinaryWriter writer, float tx)
    {
        writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f);
        writer.Write(0f); writer.Write(1f); writer.Write(0f); writer.Write(0f);
        writer.Write(0f); writer.Write(0f); writer.Write(1f); writer.Write(0f);
        writer.Write(tx); writer.Write(0f); writer.Write(0f); writer.Write(1f);
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

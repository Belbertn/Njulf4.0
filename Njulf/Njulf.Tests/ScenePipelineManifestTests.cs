using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ScenePipelineManifestTests
{
    [Test]
    public void Include_AccumulatesOnlySceneRelevantMaterialFamilies()
    {
        ScenePipelineManifest manifest = ScenePipelineManifest.Empty
            .Include(new MaterialRenderMetadata())
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.Mask
            })
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend
            })
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                ShadingModel = MaterialShadingModel.ThinGlass,
                TransmissionPolicy = GiTransmissionPolicy.ThinSurface
            })
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                TransmissionPolicy = GiTransmissionPolicy.Volume
            })
            .Include(new MaterialRenderMetadata
            {
                SurfaceFlags = MaterialSurfaceFlags.GeometryDecal
            });

        Assert.Multiple(() =>
        {
            Assert.That(manifest.Requires(
                SceneMaterialPipelineKinds.Masked), Is.True);
            Assert.That(manifest.Requires(
                SceneMaterialPipelineKinds.OrdinaryTransparent), Is.True);
            Assert.That(manifest.Requires(
                SceneMaterialPipelineKinds.ThinGlass), Is.True);
            Assert.That(manifest.Requires(
                SceneMaterialPipelineKinds.ThickTransmission), Is.True);
            Assert.That(manifest.Requires(
                SceneMaterialPipelineKinds.GeometryDecal), Is.True);
            Assert.That(manifest.HasTransparentSurface, Is.True);
            Assert.That(manifest.HasRealTransparentSurface, Is.True);
        });
    }

    [Test]
    public void GeometryDecal_TakesPrecedenceOverAlphaMode()
    {
        var metadata = new MaterialRenderMetadata
        {
            BlendMode = MaterialBlendMode.Mask,
            SurfaceFlags = MaterialSurfaceFlags.GeometryDecal
        };

        Assert.That(
            ScenePipelineManifest.Classify(metadata),
            Is.EqualTo(SceneMaterialPipelineKinds.GeometryDecal));
    }

    [Test]
    public void Include_TracksOnlyMaterialsThatConsumeRayFeatures()
    {
        ScenePipelineManifest markerOnly = ScenePipelineManifest.Empty
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.Additive,
                ShadingModel = MaterialShadingModel.Unlit,
                SurfaceFlags = MaterialSurfaceFlags.None
            });
        ScenePipelineManifest receivers = markerOnly
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.AlphaBlend,
                SurfaceFlags = MaterialSurfaceFlags.ReceivesShadows
            })
            .Include(new MaterialRenderMetadata
            {
                SurfaceFlags = MaterialSurfaceFlags.GeometryDecal |
                               MaterialSurfaceFlags.ReceivesShadows
            });

        Assert.Multiple(() =>
        {
            Assert.That(markerOnly.HasRealTransparentSurface, Is.True);
            Assert.That(
                markerOnly.HasRealTransparentShadowReceiver,
                Is.False);
            Assert.That(
                markerOnly.HasGeometryDecalShadowReceiver,
                Is.False);
            Assert.That(
                markerOnly.HasTransparentReflectionReceiver,
                Is.False);
            Assert.That(
                receivers.HasRealTransparentShadowReceiver,
                Is.True);
            Assert.That(
                receivers.HasGeometryDecalShadowReceiver,
                Is.True);
            Assert.That(
                receivers.HasTransparentReflectionReceiver,
                Is.True);
        });
    }

    [Test]
    public void OpaqueMaterial_DoesNotAddPipelineFamily()
    {
        ScenePipelineManifest manifest = ScenePipelineManifest.Empty
            .Include(new MaterialRenderMetadata
            {
                BlendMode = MaterialBlendMode.Opaque
            });

        Assert.Multiple(() =>
        {
            Assert.That(manifest.MaterialKinds,
                Is.EqualTo(SceneMaterialPipelineKinds.None));
            Assert.That(manifest.HasTransparentSurface, Is.False);
        });
    }

    [Test]
    public void Include_TracksOpaqueComplexityIndependentlyFromMasking()
    {
        GPUMaterialData plain = CreateOpaqueMaterial();
        GPUMaterialData masked = plain;
        masked.NormalScaleBias = new Vector4(1f, 1f, 0.5f, 0f);
        GPUMaterialData extended = masked;
        extended.FeatureFlags = (uint)MaterialFeatureFlags.Clearcoat;
        extended.ExtensionDataIndex = 0;

        ScenePipelineManifest manifest = ScenePipelineManifest.Empty
            .Include(
                plain,
                MaterialRenderMetadata.FromGpuMaterial(plain),
                hasVertexColor: false)
            .Include(
                plain,
                MaterialRenderMetadata.FromGpuMaterial(plain),
                hasVertexColor: true)
            .Include(
                masked,
                MaterialRenderMetadata.FromGpuMaterial(masked),
                hasVertexColor: false)
            .Include(
                extended,
                MaterialRenderMetadata.FromGpuMaterial(extended),
                hasVertexColor: false);

        Assert.Multiple(() =>
        {
            Assert.That(
                manifest.ForwardOpaqueKinds,
                Is.EqualTo(SceneForwardOpaquePipelineKinds.All));
            Assert.That(
                manifest.Requires(SceneMaterialPipelineKinds.Masked),
                Is.True);
        });
    }

    private static GPUMaterialData CreateOpaqueMaterial() => new()
    {
        Albedo = Vector4.One,
        NormalScaleBias = new Vector4(1f, 0f, 0.5f, 0f),
        MetallicRoughnessAO = new Vector4(0f, 1f, 1f, 0f),
        BaseColorOffsetScale = new Vector4(0f, 0f, 1f, 1f),
        NormalOffsetScale = new Vector4(0f, 0f, 1f, 1f),
        MetallicRoughnessOffsetScale = new Vector4(0f, 0f, 1f, 1f),
        EmissiveOffsetScale = new Vector4(0f, 0f, 1f, 1f),
        AlbedoTextureIndex = BindlessIndex.DefaultWhiteTexture,
        NormalTextureIndex = BindlessIndex.DefaultNormalTexture,
        MetallicRoughnessTextureIndex = BindlessIndex.DefaultBlackTexture,
        EmissiveTextureIndex = BindlessIndex.DefaultBlackTexture,
        ExtensionDataIndex = -1
    };
}

using Njulf.Rendering.Data;
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
}

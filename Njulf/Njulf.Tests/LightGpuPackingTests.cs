using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class LightGpuPackingTests
{
    [Test]
    public void GpuLight_AppendsAttenuationMetadataToExistingShadowLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPULight>(), Is.EqualTo(112));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.ShadowFlags)).ToInt32(), Is.EqualTo(52));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.ShadowStrength)).ToInt32(), Is.EqualTo(56));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.Padding0)).ToInt32(), Is.EqualTo(60));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.InnerSpotAngle)).ToInt32(), Is.EqualTo(64));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.AttenuationQuadratic)).ToInt32(), Is.EqualTo(76));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.Up)).ToInt32(), Is.EqualTo(80));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.SizeX)).ToInt32(), Is.EqualTo(92));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.SizeY)).ToInt32(), Is.EqualTo(96));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.IesTextureIndex)).ToInt32(), Is.EqualTo(100));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.AreaFlags)).ToInt32(), Is.EqualTo(108));
        });
    }

    [Test]
    public void ToGpuLight_PacksAreaMetadataAndIgnoresInapplicableIesProfile()
    {
        GPULight gpuLight = LightManager.ToGpuLight(new Light
        {
            Type = LightType.Rectangle,
            Direction = -Vector3.UnitZ,
            Up = Vector3.UnitY,
            Size = new Vector2(3f, 2f),
            TwoSided = true,
            PhotometricProfile = new PhotometricProfileHandle(4, 321, 7),
            IesRotationRadians = 0.4f
        });

        Assert.Multiple(() =>
        {
            Assert.That(gpuLight.Up.X, Is.Zero);
            Assert.That(gpuLight.Up.Y, Is.EqualTo(1f));
            Assert.That(gpuLight.Up.Z, Is.Zero);
            Assert.That(gpuLight.SizeX, Is.EqualTo(3f));
            Assert.That(gpuLight.SizeY, Is.EqualTo(2f));
            Assert.That(gpuLight.AreaFlags & GPULight.TwoSidedAreaFlag, Is.Not.Zero);
            Assert.That(gpuLight.IesTextureIndex, Is.EqualTo(-1));
            Assert.That(gpuLight.IesRotationRadians, Is.EqualTo(0.4f));
        });
    }

    [Test]
    public void ToGpuLight_PacksPhotometricMetadataForPunctualLight()
    {
        GPULight gpuLight = LightManager.ToGpuLight(new Light
        {
            Type = LightType.Spot,
            PhotometricProfile = new PhotometricProfileHandle(4, 321, 7),
            IesRotationRadians = 0.4f
        });

        Assert.Multiple(() =>
        {
            Assert.That(gpuLight.IesTextureIndex, Is.EqualTo(321));
            Assert.That(gpuLight.IesRotationRadians, Is.EqualTo(0.4f));
        });
    }

    [Test]
    public void ToGpuLight_PacksShadowCastingMetadataIntoReservedWords()
    {
        GPULight gpuLight = LightManager.ToGpuLight(new Light
        {
            Type = LightType.Directional,
            Direction = new Vector3(0f, -1f, 0f),
            CastsShadows = true,
            ShadowStrength = 0.65f
        });

        Assert.Multiple(() =>
        {
            Assert.That(gpuLight.ShadowFlags & GPULight.CastsShadowsFlag, Is.Not.Zero);
            Assert.That(gpuLight.ShadowStrength, Is.EqualTo(0.65f));
        });
    }

    [Test]
    public void ToGpuLight_NonShadowCasterDisablesGpuShadowing()
    {
        GPULight gpuLight = LightManager.ToGpuLight(new Light
        {
            CastsShadows = false,
            ShadowStrength = 0.8f
        });

        Assert.Multiple(() =>
        {
            Assert.That(gpuLight.ShadowFlags, Is.Zero);
            Assert.That(gpuLight.ShadowStrength, Is.Zero);
        });
    }

    [TestCase(0f, 1f)]
    [TestCase(-0.5f, 1f)]
    [TestCase(0.4f, 0.4f)]
    [TestCase(1.5f, 1f)]
    public void ToGpuLight_NormalizesShadowStrength(float authoredStrength, float expectedStrength)
    {
        GPULight gpuLight = LightManager.ToGpuLight(new Light
        {
            CastsShadows = true,
            ShadowStrength = authoredStrength
        });

        Assert.That(gpuLight.ShadowStrength, Is.EqualTo(expectedStrength).Within(0.0001f));
    }
}

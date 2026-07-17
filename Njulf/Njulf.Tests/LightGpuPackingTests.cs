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
    public void GpuLight_ShadowMetadataKeepsTheExistingSixtyFourByteLayout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPULight>(), Is.EqualTo(64));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.ShadowFlags)).ToInt32(), Is.EqualTo(52));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.ShadowStrength)).ToInt32(), Is.EqualTo(56));
            Assert.That(Marshal.OffsetOf<GPULight>(nameof(GPULight.Padding0)).ToInt32(), Is.EqualTo(60));
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

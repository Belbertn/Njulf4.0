using System;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiMaterialTextureLodPolicyTests
{
    private static readonly DdgiMaterialTriangleFootprint UnitFootprint = new(
        new Vector3(0f, 0f, 0f),
        new Vector3(1f, 0f, 0f),
        new Vector3(0f, 1f, 0f),
        new Vector2(0f, 0f),
        new Vector2(1f, 0f),
        new Vector2(0f, 1f),
        new Vector2(0f, 0f),
        new Vector2(0.25f, 0f),
        new Vector2(0f, 0.25f));

    [Test]
    public void Resolve_UsesBindingUvSetAndScale()
    {
        float uv0 = Resolve(texCoordSet: 0, bindingScale: Vector2.One);
        float uv1 = Resolve(texCoordSet: 1, bindingScale: Vector2.One);
        float scaled = Resolve(texCoordSet: 0, bindingScale: new Vector2(2f));

        Assert.That(uv0 - uv1, Is.EqualTo(2f).Within(1e-5f));
        Assert.That(scaled - uv0, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void Resolve_UsesLargerOfProbeLatticeAndRayCone()
    {
        float latticeLimited = DdgiMaterialTextureLodPolicy.Resolve(
            1024,
            1024,
            0,
            Vector2.One,
            UnitFootprint,
            volumeCascadeIndex: 0,
            probeSpacing: 1f,
            hitDistance: 0f,
            rayAngularRadius: 0f);
        float coneLimited = DdgiMaterialTextureLodPolicy.Resolve(
            1024,
            1024,
            0,
            Vector2.One,
            UnitFootprint,
            volumeCascadeIndex: 0,
            probeSpacing: 1f,
            hitDistance: 8f,
            rayAngularRadius: 0.125f);

        Assert.That(latticeLimited, Is.EqualTo(8f).Within(1e-5f));
        Assert.That(coneLimited, Is.EqualTo(10f).Within(1e-5f));
    }

    [Test]
    public void Resolve_AuthoredVolumeRetainsOneAdditionalMipOfDetail()
    {
        float clipmap = Resolve(volumeCascadeIndex: 0);
        float authored = Resolve(volumeCascadeIndex: DdgiMaterialTextureLodPolicy.AuthoredVolumeCascade);

        Assert.That(clipmap - authored, Is.EqualTo(1f).Within(1e-5f));
    }

    [Test]
    public void Resolve_ClampsToActualTextureMipRangeAndRejectsNonFiniteInputs()
    {
        float maximum = DdgiMaterialTextureLodPolicy.Resolve(
            300,
            100,
            0,
            new Vector2(100f),
            UnitFootprint,
            0,
            100f,
            100f,
            1f,
            20f);
        float missing = DdgiMaterialTextureLodPolicy.Resolve(
            0,
            0,
            0,
            Vector2.One,
            UnitFootprint,
            0,
            1f,
            1f,
            1f);

        Assert.That(maximum, Is.EqualTo(8f));
        Assert.That(missing, Is.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DdgiMaterialTextureLodPolicy.Resolve(
                1024,
                1024,
                0,
                Vector2.One,
                UnitFootprint,
                0,
                float.NaN,
                1f,
                1f));
    }

    private static float Resolve(
        int texCoordSet = 0,
        Vector2? bindingScale = null,
        uint volumeCascadeIndex = 0) =>
        DdgiMaterialTextureLodPolicy.Resolve(
            1024,
            1024,
            texCoordSet,
            bindingScale ?? Vector2.One,
            UnitFootprint,
            volumeCascadeIndex,
            probeSpacing: 1f,
            hitDistance: 0f,
            rayAngularRadius: 0f);
}

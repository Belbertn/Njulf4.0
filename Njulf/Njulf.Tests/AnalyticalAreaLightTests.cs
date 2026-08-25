using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AnalyticalAreaLightTests
{
    [Test]
    public void Geometry_ComputesPhysicalSurfaceAreaAndPowerWeight()
    {
        var rectangle = new Light
        {
            Type = LightType.Rectangle,
            Size = new Vector2(4f, 2f),
            Color = Vector3.One,
            Intensity = 3f
        };
        var disk = WithType(rectangle, LightType.Disk, new Vector2(2f, 2f));
        var tube = WithType(rectangle, LightType.Tube, new Vector2(4f, 2f));

        Assert.Multiple(() =>
        {
            Assert.That(AnalyticalLightGeometry.ComputeSurfaceArea(rectangle), Is.EqualTo(8f));
            Assert.That(AnalyticalLightGeometry.ComputeSurfaceArea(disk), Is.EqualTo(MathF.PI).Within(1e-5f));
            Assert.That(
                AnalyticalLightGeometry.ComputeSurfaceArea(tube),
                Is.EqualTo(10f * MathF.PI).Within(1e-5f));
            Assert.That(
                AnalyticalLightGeometry.ComputePowerWeight(rectangle),
                Is.EqualTo(24f * MathF.PI).Within(1e-4f));
        });
    }

    [Test]
    public void LtcLookupPayloads_AreCompleteAndNonZero()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LtcLookupTableData.Matrix.Length,
                Is.EqualTo(LtcLookupTableData.PayloadSize));
            Assert.That(LtcLookupTableData.Amplitude.Length,
                Is.EqualTo(LtcLookupTableData.PayloadSize));
            Assert.That(LtcLookupTableData.Matrix.ToArray(),
                Has.Some.Not.Zero);
            Assert.That(LtcLookupTableData.Amplitude.ToArray(),
                Has.Some.Not.Zero);
        });
    }

    [Test]
    public void Geometry_ExpandsInfluenceBoundsByOrientedShapeAndRange()
    {
        var light = new Light
        {
            Type = LightType.Rectangle,
            Position = new Vector3(1f, 2f, 3f),
            Direction = Vector3.UnitZ,
            Up = Vector3.UnitY,
            Size = new Vector2(4f, 2f),
            Range = 5f
        };

        Assert.That(
            AnalyticalLightGeometry.TryGetInfluenceBounds(light, out Vector3 minimum, out Vector3 maximum),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(minimum, Is.EqualTo(new Vector3(-6f, -4f, -2f)));
            Assert.That(maximum, Is.EqualTo(new Vector3(8f, 8f, 8f)));
        });
    }

    [Test]
    public void Geometry_MaximumShadowSegmentIncludesTheFarSideOfTheEmitter()
    {
        var light = new Light
        {
            Type = LightType.Rectangle,
            Size = new Vector2(4f, 2f),
            Range = 5f
        };

        float shapeRadius = MathF.Sqrt(5f);
        Assert.Multiple(() =>
        {
            Assert.That(
                AnalyticalLightGeometry.GetBoundingRadius(light),
                Is.EqualTo(5f + shapeRadius).Within(1e-6f));
            Assert.That(
                AnalyticalLightGeometry.GetMaximumSurfaceSampleDistanceWithinRange(light),
                Is.EqualTo(5f + 2f * shapeRadius).Within(1e-6f));
        });
    }

    [Test]
    public void Geometry_UniformRectangleSampleHasCorrectPdfAndSide()
    {
        var light = new Light
        {
            Type = LightType.Rectangle,
            Direction = Vector3.UnitZ,
            Up = Vector3.UnitY,
            Size = new Vector2(4f, 2f),
            TwoSided = true
        };

        Assert.That(
            AnalyticalLightGeometry.TrySampleSurface(
                light,
                new Vector3(0.75f, 0.25f, 0.75f),
                out AreaLightSurfaceSample sample),
            Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(sample.Position, Is.EqualTo(new Vector3(1f, -0.5f, 0f)));
            Assert.That(sample.Normal, Is.EqualTo(-Vector3.UnitZ));
            Assert.That(sample.AreaPdf, Is.EqualTo(1f / 16f).Within(1e-6f));
        });
    }

    [Test]
    public void Geometry_DiskAndTubeSamplesUseTheirPhysicalSurfacePdfs()
    {
        var disk = new Light
        {
            Type = LightType.Disk,
            Direction = Vector3.UnitZ,
            Up = Vector3.UnitY,
            Size = new Vector2(2f, 2f)
        };
        var tube = new Light
        {
            Type = LightType.Tube,
            Direction = Vector3.UnitZ,
            Up = Vector3.UnitY,
            Size = new Vector2(4f, 2f)
        };

        Assert.That(
            AnalyticalLightGeometry.TrySampleSurface(
                disk,
                new Vector3(0.25f, 0f, 0f),
                out AreaLightSurfaceSample diskSample),
            Is.True);
        Assert.That(
            AnalyticalLightGeometry.TrySampleSurface(
                tube,
                new Vector3(0f, 0.75f, 0.1f),
                out AreaLightSurfaceSample tubeSideSample),
            Is.True);
        Assert.That(
            AnalyticalLightGeometry.TrySampleSurface(
                tube,
                new Vector3(0.25f, 0f, 0.95f),
                out AreaLightSurfaceSample tubeCapSample),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(diskSample.Position,
                Is.EqualTo(new Vector3(0.5f, 0f, 0f)));
            Assert.That(diskSample.Normal, Is.EqualTo(Vector3.UnitZ));
            Assert.That(diskSample.AreaPdf,
                Is.EqualTo(1f / MathF.PI).Within(1e-6f));
            Assert.That(tubeSideSample.Position,
                Is.EqualTo(new Vector3(1f, 0f, 1f)));
            Assert.That(tubeSideSample.Normal, Is.EqualTo(Vector3.UnitX));
            Assert.That(tubeSideSample.AreaPdf,
                Is.EqualTo(1f / (10f * MathF.PI)).Within(1e-6f));
            Assert.That(tubeCapSample.Position,
                Is.EqualTo(new Vector3(0.5f, 0f, 2f)));
            Assert.That(tubeCapSample.Normal, Is.EqualTo(Vector3.UnitZ));
        });
    }

    [Test]
    public void LightTree_PropagatesAreaMembershipAndUsesShapeBounds()
    {
        var input = new DdgiLocalLightTreeInput(
            3,
            44,
            8,
            new Vector3(1f, 2f, 3f),
            Vector3.One,
            2f,
            4f,
            Vector3.UnitZ,
            0f,
            LightType.Rectangle,
            Vector3.UnitY,
            new Vector2(4f, 2f));

        SimpleDdgiLightTreeReference tree = SimpleDdgiLightTreeReference.Build([input]);
        var node = tree.CreateGpuNodes().Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                node.FlagsAndChecksum & (uint)DdgiLightTreeNodeFlags.ContainsAreaLight,
                Is.Not.Zero);
            Assert.That(node.BoundsMinimumAndFlux.X, Is.EqualTo(-5f));
            Assert.That(node.BoundsMaximumAndRange.X, Is.EqualTo(7f));
            Assert.That(node.BoundsMaximumAndRange.W, Is.GreaterThan(4f));
        });
    }

    [Test]
    public void LightTree_RejectsAreaLightsWithoutAValidOrientationFrame()
    {
        var input = new DdgiLocalLightTreeInput(
            3,
            44,
            8,
            Vector3.Zero,
            Vector3.One,
            2f,
            4f,
            Vector3.Zero,
            0f,
            LightType.Rectangle,
            Vector3.UnitY,
            new Vector2(4f, 2f));

        SimpleDdgiLightTreeReference tree =
            SimpleDdgiLightTreeReference.Build([input]);

        Assert.That(tree.LocalLightCount, Is.Zero);
    }

    [Test]
    public void DdgiPrimaryLocalSelection_UsesAreaEmitterPower()
    {
        Light[] lights =
        [
            new Light
            {
                Type = LightType.Point,
                Color = Vector3.One,
                Intensity = 10f,
                Range = 10f
            },
            new Light
            {
                Type = LightType.Rectangle,
                Color = Vector3.One,
                Intensity = 1f,
                Range = 10f,
                Direction = Vector3.UnitZ,
                Up = Vector3.UnitY,
                Size = new Vector2(10f, 10f)
            }
        ];
        var snapshot = new LightFrameSnapshot(
            lights,
            lights.Length,
            directionalLightCount: 0,
            localLightCount: lights.Length,
            firstShadowCastingDirectionalLightIndex: -1,
            firstShadowCastingDirectionalLight: default,
            revision: 1);

        int selected = VulkanRenderer.SelectPrimaryDdgiLocalLight(
            snapshot,
            out float selectedWeight,
            out float totalWeight);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo(1));
            Assert.That(selectedWeight, Is.GreaterThan(0f));
            Assert.That(totalWeight, Is.GreaterThan(selectedWeight));
        });
    }

    private static Light WithType(Light source, LightType type, Vector2 size)
    {
        source.Type = type;
        source.Size = size;
        return source;
    }
}

[TestFixture]
public sealed class IesPhotometricProfileParserTests
{
    private const string ValidTypeC = """
        IESNA:LM-63-2002
        [TEST] NJULF
        TILT=NONE
        1 1000 1 3 2 1 2 0 0 0
        1 1 10
        0 90 180
        0 180
        10 20 0
        5 10 0
        """;

    [Test]
    public void Parse_NormalizesAndInterpolatesTypeCDistribution()
    {
        IesPhotometricProfile profile = IesPhotometricProfileParser.Parse(ValidTypeC);

        Assert.Multiple(() =>
        {
            Assert.That(profile.PeakCandela, Is.EqualTo(20f));
            Assert.That(profile.Evaluate(0f, 90f), Is.EqualTo(1f).Within(1e-6f));
            Assert.That(profile.Evaluate(180f, 90f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(profile.Evaluate(270f, 90f), Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(profile.Resample(16, 8), Has.All.InRange(0f, 1f));
        });
    }

    [Test]
    public void Parse_RejectsTiltAndNonTypeCProfiles()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => IesPhotometricProfileParser.Parse(
                    ValidTypeC.Replace("TILT=NONE", "TILT=INCLUDE", StringComparison.Ordinal)),
                Throws.TypeOf<NotSupportedException>());
            Assert.That(
                () => IesPhotometricProfileParser.Parse(
                    ValidTypeC.Replace("3 2 1 2", "3 2 2 2", StringComparison.Ordinal)),
                Throws.TypeOf<NotSupportedException>());
        });
    }

    [Test]
    public void Parse_RejectsNonMonotonicAnglesAndZeroEnergy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => IesPhotometricProfileParser.Parse(
                    ValidTypeC.Replace("0 90 180", "0 90 45", StringComparison.Ordinal)),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                () => IesPhotometricProfileParser.Parse(
                    ValidTypeC.Replace("10 20 0\n5 10 0", "0 0 0\n0 0 0", StringComparison.Ordinal)),
                Throws.TypeOf<InvalidDataException>());
        });
    }
}

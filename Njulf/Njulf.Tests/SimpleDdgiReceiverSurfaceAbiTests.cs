using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiReceiverSurfaceAbiTests
{
    [Test]
    public void LayoutAndMemoryPlan_KeepTheParallelEightByteContract()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUSimpleDdgiReceiverSurface>(), Is.EqualTo(8));
            Assert.That(
                Marshal.OffsetOf<GPUSimpleDdgiReceiverSurface>(
                    nameof(GPUSimpleDdgiReceiverSurface.PackedDepthAndOffset)).ToInt32(),
                Is.EqualTo(4));
            Assert.That(
                SimpleDdgiReceiverSurfaceAbi.CalculateSidecarBytes(1920, 1080, 12, 2),
                Is.EqualTo(230_400UL));
            Assert.That(
                SimpleDdgiReceiverSurfaceAbi.CalculateSidecarBytes(1920, 1080, 2, 2),
                Is.EqualTo(8_294_400UL));
            Assert.That(
                SimpleDdgiReceiverSurfaceAbi.CalculateSidecarBytes(1, 1, 12, 2),
                Is.EqualTo(16UL));
        });
    }

    [Test]
    public void PackDecode_RoundTripsDepthOffsetsAndRandomUnitNormals()
    {
        var random = new Random(0x51deca7);
        float[] depths =
        [
            SimpleDdgiReceiverSurfaceAbi.MinimumReverseZ,
            0.0001f,
            0.01f,
            0.25f,
            1.0f
        ];

        for (int index = 0; index < 2_048; index++)
        {
            Vector3 normal;
            do
            {
                normal = new Vector3(
                    (float)(random.NextDouble() * 2.0 - 1.0),
                    (float)(random.NextDouble() * 2.0 - 1.0),
                    (float)(random.NextDouble() * 2.0 - 1.0));
            } while (normal.LengthSquared() < 0.0001f);
            normal = Vector3.Normalize(normal);
            float depth = depths[index % depths.Length];
            uint offsetX = (uint)(index & 15);
            uint offsetY = (uint)((index >> 4) & 15);

            Assert.That(
                SimpleDdgiReceiverSurfaceAbi.TryPack(
                    normal,
                    depth,
                    offsetX,
                    offsetY,
                    out GPUSimpleDdgiReceiverSurface packed),
                Is.True);
            Assert.That(
                SimpleDdgiReceiverSurfaceAbi.TryDecode(packed, out var decoded),
                Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(Vector3.Dot(normal, decoded.GeometricNormal),
                    Is.GreaterThan(0.9999f));
                Assert.That(decoded.RepresentativeOffsetX, Is.EqualTo(offsetX));
                Assert.That(decoded.RepresentativeOffsetY, Is.EqualTo(offsetY));
                Assert.That(decoded.ReverseZ / depth,
                    Is.EqualTo(1.0f).Within(0.00002f));
            });
        }
    }

    [Test]
    public void InvalidInputs_AlwaysPublishTheZeroDepthSentinel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
                Vector3.Zero, 0.5f, 0, 0, out var zeroNormal), Is.False);
            Assert.That(zeroNormal.PackedDepthAndOffset, Is.Zero);
            Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
                Vector3.UnitY, float.NaN, 0, 0, out var nanDepth), Is.False);
            Assert.That(nanDepth.PackedDepthAndOffset, Is.Zero);
            Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
                Vector3.UnitY, 0.5f, 16, 0, out var badOffset), Is.False);
            Assert.That(badOffset.PackedDepthAndOffset, Is.Zero);
            Assert.That(SimpleDdgiReceiverSurfaceAbi.TryDecode(default, out _), Is.False);
            Assert.That(SimpleDdgiReceiverSurfaceAbi.EncodeReverseZ(0.0f), Is.Zero);
        });
    }

    [Test]
    public void Compatibility_AcceptsSamePlaneAndRejectsNormalDepthAndPlaneChanges()
    {
        Matrix4x4 inverseViewProjection = Matrix4x4.Identity;
        Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
            Vector3.UnitZ, 0.5f, 0, 0, out var planar), Is.True);

        Assert.That(
            SimpleDdgiReceiverSurfaceAbi.EvaluateFragmentCompatibility(
                planar,
                50,
                50,
                2,
                inverseViewProjection,
                200,
                200,
                new Vector3(0, 0, -2),
                100,
                100,
                0.5f,
                new Vector3(0.005f, 0.005f, 0.5f),
                Vector3.UnitZ),
            Is.EqualTo(SimpleDdgiReceiverSurfaceCompatibilityReason.Accepted));

        Assert.That(
            SimpleDdgiReceiverSurfaceAbi.EvaluateFragmentCompatibility(
                planar,
                50,
                50,
                2,
                inverseViewProjection,
                200,
                200,
                new Vector3(0, 0, -2),
                100,
                100,
                0.5f,
                new Vector3(0.005f, 0.005f, 0.5f),
                Vector3.UnitY),
            Is.EqualTo(SimpleDdgiReceiverSurfaceCompatibilityReason.Normal));

        Assert.That(
            SimpleDdgiReceiverSurfaceAbi.EvaluateFragmentCompatibility(
                planar,
                50,
                50,
                2,
                inverseViewProjection,
                200,
                200,
                new Vector3(0, 0, -2),
                100,
                100,
                0.35f,
                new Vector3(0.005f, 0.005f, 0.35f),
                Vector3.UnitZ),
            Is.EqualTo(SimpleDdgiReceiverSurfaceCompatibilityReason.Depth));

        Assert.That(
            SimpleDdgiReceiverSurfaceAbi.EvaluateFragmentCompatibility(
                planar,
                50,
                50,
                2,
                inverseViewProjection,
                200,
                200,
                new Vector3(0, 0, -2),
                100,
                100,
                0.5f,
                new Vector3(0.005f, 0.005f, 0.53f),
                Vector3.UnitZ),
            Is.EqualTo(SimpleDdgiReceiverSurfaceCompatibilityReason.Plane));
    }

    [Test]
    public void PackedCompatibility_RejectsOffsetsOutsideTheOwningScale()
    {
        Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
            Vector3.UnitZ, 0.5f, 11, 11, out var gatherSurface), Is.True);
        Assert.That(SimpleDdgiReceiverSurfaceAbi.TryPack(
            Vector3.UnitZ, 0.5f, 0, 0, out var resolvedSurface), Is.True);

        Assert.That(
            SimpleDdgiReceiverSurfaceAbi.EvaluatePackedCompatibility(
                gatherSurface,
                0,
                0,
                2,
                resolvedSurface,
                0,
                0,
                2,
                Matrix4x4.Identity,
                64,
                64,
                Vector3.Zero),
            Is.EqualTo(SimpleDdgiReceiverSurfaceCompatibilityReason.InvalidSurface));
    }
}

using Njulf.Assets;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RendererMeshletBuildProfileTests
{
    [TestCaseSource(nameof(FlexibleProfiles))]
    public void FlexibleProfile_BuildsWithinPortableOutputLimits(
        RendererMeshletBuildProfile profile)
    {
        const int size = 18;
        var vertices = new Vector3[size * size];
        var indices = new List<uint>();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            vertices[y * size + x] = new Vector3(x, y, MathF.Sin(x * 0.2f));
        for (int y = 0; y < size - 1; y++)
        for (int x = 0; x < size - 1; x++)
        {
            uint a = (uint)(y * size + x);
            uint b = a + 1;
            uint c = a + size;
            uint d = c + 1;
            indices.AddRange([a, c, b, b, c, d]);
        }

        MeshletMesh result = profile.CreateBuilder().BuildMeshlets(
            vertices,
            indices.ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(result.Meshlets, Is.Not.Empty);
            Assert.That(result.Meshlets.Max(meshlet => meshlet.LocalVertexCount),
                Is.LessThanOrEqualTo(48));
            Assert.That(result.Meshlets.Max(meshlet => meshlet.LocalTriangleCount),
                Is.LessThanOrEqualTo(64));
            Assert.That(result.Meshlets.Sum(meshlet => meshlet.LocalTriangleCount),
                Is.EqualTo(indices.Count / 3));
        });
    }

    [Test]
    public void ProfileIds_AreVendorNeutralAndExplicitlyResolvable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                RendererMeshletBuildProfiles.Production,
                Is.SameAs(RendererMeshletBuildProfiles.Portable48V64T));
            Assert.That(
                RendererMeshletBuildProfiles.Resolve("CONNECTED-64V-126T"),
                Is.SameAs(RendererMeshletBuildProfiles.Connected64V126T));
            Assert.That(
                RendererMeshletBuildProfiles.AvailableProfiles.All(
                    profile => !profile.Id.Contains(
                        "rtx",
                        StringComparison.OrdinalIgnoreCase)),
                Is.True);
        });
    }

    private static IEnumerable<RendererMeshletBuildProfile> FlexibleProfiles()
    {
        yield return RendererMeshletBuildProfiles.PortableFlexCone025;
        yield return RendererMeshletBuildProfiles.PortableFlexCone050;
    }
}

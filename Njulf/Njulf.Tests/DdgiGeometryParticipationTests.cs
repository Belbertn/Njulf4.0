using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiGeometryParticipationTests
{
    [Test]
    public void StochasticAlpha_IsStableAndConvergesToCoverage()
    {
        const float coverage = 0.37f;
        var fixedIdentity = new DdgiStochasticIdentity(
            0x1234_5678_9ABC_DEF0UL,
            17,
            23,
            1,
            DdgiStochasticDecisionDomain.LocalLightTreeTraversal,
            91,
            7);
        bool first = DdgiGeometryParticipation.AcceptStableStochasticCoverage(
            coverage,
            fixedIdentity);
        for (int repeat = 0; repeat < 100; repeat++)
        {
            Assert.That(
                DdgiGeometryParticipation.AcceptStableStochasticCoverage(
                    coverage,
                    fixedIdentity),
                Is.EqualTo(first));
        }

        const int samples = 100_000;
        int accepted = 0;
        for (int index = 0; index < samples; index++)
        {
            var identity = fixedIdentity with
            {
                WorldProbeStableKey = unchecked((ulong)(index + 1)),
                DirectionRayOrdinal = unchecked((uint)index)
            };
            if (DdgiGeometryParticipation.AcceptStableStochasticCoverage(
                    coverage,
                    identity))
            {
                accepted++;
            }
        }

        Assert.That((float)accepted / samples, Is.EqualTo(coverage).Within(0.006f));
    }

    [Test]
    public void Visibility_ComposesOrdinaryAndColoredThinLayersAnalytically()
    {
        DdgiVisibilityLayer[] layers =
        {
            DdgiVisibilityLayer.Alpha(0.25f),
            DdgiVisibilityLayer.Thin(
                0.5f,
                new Vector3(0.2f, 0.6f, 1f)),
            DdgiVisibilityLayer.Alpha(0.4f)
        };

        DdgiVisibilityComposition result =
            DdgiGeometryParticipation.ComposeVisibility(layers);

        Assert.Multiple(() =>
        {
            Assert.That(result.Throughput.X, Is.EqualTo(0.27f).Within(1e-6f));
            Assert.That(result.Throughput.Y, Is.EqualTo(0.36f).Within(1e-6f));
            Assert.That(result.Throughput.Z, Is.EqualTo(0.45f).Within(1e-6f));
            Assert.That(result.ComposedLayerCount, Is.EqualTo(3));
            Assert.That(result.ReachedLayerLimit, Is.False);
        });
    }

    [Test]
    public void Visibility_OverflowFailsClosedInsteadOfLeakingLight()
    {
        DdgiVisibilityLayer[] layers = Enumerable.Repeat(
            DdgiVisibilityLayer.Alpha(0.01f),
            DdgiGeometryParticipation.ProductionVisibilityLayerLimit + 1)
            .ToArray();

        DdgiVisibilityComposition result =
            DdgiGeometryParticipation.ComposeVisibility(layers);

        Assert.Multiple(() =>
        {
            Assert.That(result.ReachedLayerLimit, Is.True);
            Assert.That(result.Throughput, Is.EqualTo(Vector3.Zero));
            Assert.That(result.ComposedLayerCount,
                Is.EqualTo(DdgiGeometryParticipation.ProductionVisibilityLayerLimit));
        });
    }

    [Test]
    public void Decals_AssociateAndComposeByLayerThenStableObjectOrder()
    {
        DdgiReferenceSurface baseSurface = Surface(
            diffuse: Vector3.Zero,
            opacity: 1f,
            normal: Vector3.UnitY);
        DdgiDecalCandidate redLayer = Candidate(
            distance: 4.9995f,
            layer: 1,
            stableIdentity: 20,
            diffuse: new Vector3(1f, 0f, 0f),
            opacity: 0.5f);
        DdgiDecalCandidate blueLayer = Candidate(
            distance: 5.0005f,
            layer: 2,
            stableIdentity: 10,
            diffuse: new Vector3(0f, 0f, 1f),
            opacity: 0.5f);

        DdgiDecalComposition ordered = DdgiGeometryParticipation.ComposeDecals(
            baseSurface,
            5f,
            new[] { redLayer, blueLayer });
        DdgiDecalComposition permuted = DdgiGeometryParticipation.ComposeDecals(
            baseSurface,
            5f,
            new[] { blueLayer, redLayer });

        Assert.Multiple(() =>
        {
            Assert.That(ordered.AssociatedCount, Is.EqualTo(2));
            Assert.That(ordered.Surface.DiffuseReflectance.X,
                Is.EqualTo(0.25f).Within(1e-6f));
            Assert.That(ordered.Surface.DiffuseReflectance.Y,
                Is.Zero.Within(1e-6f));
            Assert.That(ordered.Surface.DiffuseReflectance.Z,
                Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(permuted.Surface.DiffuseReflectance,
                Is.EqualTo(ordered.Surface.DiffuseReflectance));
            Assert.That(ordered.Surface.Opacity, Is.EqualTo(1f));
            Assert.That(ordered.Surface.GeometricNormal,
                Is.EqualTo(Vector3.UnitY));
        });
    }

    [Test]
    public void Decals_RejectDepthAndFacingMismatches()
    {
        DdgiReferenceSurface baseSurface = Surface(
            Vector3.Zero,
            1f,
            Vector3.UnitY);
        DdgiDecalCandidate tooFar = Candidate(
            distance: 5.02f,
            layer: 0,
            stableIdentity: 1,
            diffuse: new Vector3(1f, 0f, 0f),
            opacity: 1f);
        DdgiDecalCandidate reversed = Candidate(
            distance: 5f,
            layer: 0,
            stableIdentity: 2,
            diffuse: new Vector3(0f, 1f, 0f),
            opacity: 1f) with
        {
            Surface = Surface(
                new Vector3(0f, 1f, 0f),
                1f,
                -Vector3.UnitY)
        };

        DdgiDecalComposition result = DdgiGeometryParticipation.ComposeDecals(
            baseSurface,
            5f,
            new[] { reversed, tooFar });

        Assert.Multiple(() =>
        {
            Assert.That(result.AssociatedCount, Is.Zero);
            Assert.That(result.DepthRejectedCount, Is.EqualTo(1));
            Assert.That(result.FacingRejectedCount, Is.EqualTo(1));
            Assert.That(result.Surface.DiffuseReflectance, Is.EqualTo(Vector3.Zero));
        });
    }

    [Test]
    public void DecalCandidateCap_RetainsNearestSetDeterministically()
    {
        DdgiReferenceSurface baseSurface = Surface(
            Vector3.Zero,
            1f,
            Vector3.UnitY);
        var candidates = new DdgiDecalCandidate[12];
        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = Candidate(
                distance: 5f + index * 0.0001f,
                layer: index,
                stableIdentity: unchecked((uint)(100 - index)),
                diffuse: new Vector3(index / 11f),
                opacity: 0.1f) with
            {
                DepthTolerance = 1f
            };
        }

        DdgiDecalComposition result = DdgiGeometryParticipation.ComposeDecals(
            baseSurface,
            5f,
            candidates,
            candidateLimit: 4);

        Assert.Multiple(() =>
        {
            Assert.That(result.RetainedCount, Is.EqualTo(4));
            Assert.That(result.AssociatedCount, Is.EqualTo(4));
            Assert.That(result.OverflowCount, Is.EqualTo(8));
        });
    }

    [Test]
    public void SweptBounds_UnionOldAndNewPoseAndExpandInfluence()
    {
        BoundingBox swept = DdgiGeometryParticipation.CreateSweptInfluenceBounds(
            new BoundingBox(new Vector3(-1f), new Vector3(1f)),
            new BoundingBox(new Vector3(3f, 0f, -2f), new Vector3(5f, 2f, 0f)),
            2f);

        Assert.Multiple(() =>
        {
            Assert.That(swept.Min, Is.EqualTo(new Vector3(-3f, -3f, -4f)));
            Assert.That(swept.Max, Is.EqualTo(new Vector3(7f, 4f, 3f)));
        });
    }

    private static DdgiDecalCandidate Candidate(
        float distance,
        int layer,
        uint stableIdentity,
        Vector3 diffuse,
        float opacity) =>
        new(
            distance,
            DepthTolerance: 0.002f,
            DepthBias: 0f,
            layer,
            StableOrder: stableIdentity,
            StableInstanceIdentity: stableIdentity,
            PrimitiveIdentity: 0,
            PremultipliedAlpha: false,
            Surface(diffuse, opacity, Vector3.UnitY));

    private static DdgiReferenceSurface Surface(
        Vector3 diffuse,
        float opacity,
        Vector3 normal) =>
        new(
            normal,
            normal,
            normal,
            diffuse,
            new Vector3(0.04f),
            new Vector3(0.04f),
            diffuse,
            Vector3.Zero,
            Vector3.Zero,
            MaterialOcclusion: 1f,
            opacity,
            Metallic: 0f,
            Roughness: 0.5f);
}

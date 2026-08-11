using System;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiEmissiveSpatialHierarchyTests
{
    [Test]
    public void Build_UsesCompleteBinaryTopologyAndCoversEverySource()
    {
        GPUDdgiEmissiveSource[] sources = BuildSources();
        var hierarchy = new DdgiEmissiveSpatialHierarchy(16);

        hierarchy.BuildOrRefit(sources);

        Assert.Multiple(() =>
        {
            Assert.That(hierarchy.SourceCount, Is.EqualTo(3));
            Assert.That(hierarchy.NodeCount, Is.EqualTo(7));
            Assert.That(hierarchy.Nodes[0].Vertex0Area.W, Is.EqualTo(1.0f).Within(1e-5f));
            Assert.That(
                DdgiEmissiveTriangleTable.DecodeFlags(sources[0]) &
                    DdgiEmissiveSourceFlags.SpatialHierarchy,
                Is.EqualTo(DdgiEmissiveSourceFlags.SpatialHierarchy));
            Assert.That(hierarchy.Diagnostics.BuildCount, Is.EqualTo(1));
            Assert.That(hierarchy.Diagnostics.LastUpdatedNodeCount, Is.EqualTo(7));
        });
    }

    [Test]
    public void PointDependentProbabilities_AreNormalizedAndRetainGlobalSupport()
    {
        GPUDdgiEmissiveSource[] sources = BuildSources();
        var hierarchy = new DdgiEmissiveSpatialHierarchy(16);
        hierarchy.BuildOrRefit(sources);
        var receiverPosition = new Vector3(0.25f, 0.0f, 0.25f);
        var receiverNormal = new Vector3(0.0f, 1.0f, 0.0f);

        float hierarchySum = 0.0f;
        float mixedSum = 0.0f;
        for (int i = 0; i < sources.Length; i++)
        {
            float hierarchyProbability = hierarchy.EvaluateHierarchySelectionProbability(
                i,
                receiverPosition,
                receiverNormal);
            float mixedProbability = hierarchy.EvaluateMixedSelectionProbability(
                i,
                receiverPosition,
                receiverNormal,
                sources);
            float globalFloor =
                (1.0f - DdgiEmissiveSpatialHierarchy.HierarchyTechniqueProbability) *
                sources[i].RadianceSelectionProbability.W;

            Assert.Multiple(() =>
            {
                Assert.That(hierarchyProbability, Is.GreaterThan(0.0f));
                Assert.That(mixedProbability, Is.GreaterThanOrEqualTo(globalFloor));
            });
            hierarchySum += hierarchyProbability;
            mixedSum += mixedProbability;
        }

        Assert.Multiple(() =>
        {
            Assert.That(hierarchySum, Is.EqualTo(1.0f).Within(2e-5f));
            Assert.That(mixedSum, Is.EqualTo(1.0f).Within(2e-5f));
        });
    }

    [Test]
    public void Refit_UpdatesOnlyChangedLeafAndAncestors()
    {
        GPUDdgiEmissiveSource[] sources = BuildSources();
        var hierarchy = new DdgiEmissiveSpatialHierarchy(16);
        hierarchy.BuildOrRefit(sources);
        hierarchy.BuildOrRefit(sources);
        Assert.That(hierarchy.Diagnostics.NoWorkCount, Is.EqualTo(1));

        sources[1].RadianceSelectionProbability.X *= 1.25f;
        hierarchy.BuildOrRefit(sources);

        Assert.Multiple(() =>
        {
            Assert.That(hierarchy.Diagnostics.RefitCount, Is.EqualTo(1));
            Assert.That(hierarchy.Diagnostics.LastUpdatedNodeCount, Is.EqualTo(3));
            Assert.That(hierarchy.Diagnostics.LastUpdatedNodeCount, Is.LessThan(hierarchy.NodeCount));
        });
    }

    [Test]
    public void MixedEstimator_DiscreteExpectationIsIndependentOfProposal()
    {
        GPUDdgiEmissiveSource[] sources = BuildSources();
        var hierarchy = new DdgiEmissiveSpatialHierarchy(16);
        hierarchy.BuildOrRefit(sources);
        var receiverPosition = new Vector3(2.0f, 0.5f, -1.0f);
        var receiverNormal = new Vector3(0.0f, 1.0f, 0.0f);
        float[] integrand = { 2.0f, 7.0f, 0.5f };

        double expected = 0.0;
        for (int i = 0; i < sources.Length; i++)
        {
            float probability = hierarchy.EvaluateMixedSelectionProbability(
                i,
                receiverPosition,
                receiverNormal,
                sources);
            expected += probability * (integrand[i] / probability);
        }

        Assert.That(expected, Is.EqualTo(integrand.Sum(value => (double)value)).Within(1e-5));
    }

    private static GPUDdgiEmissiveSource[] BuildSources()
    {
        DdgiEmissiveTriangleCandidate[] candidates =
        {
            Triangle(new Vector3(0.0f, 2.0f, 0.0f), new Vector3(8.0f, 2.0f, 1.0f), 1),
            Triangle(new Vector3(10.0f, 3.0f, 0.0f), new Vector3(1.0f, 3.0f, 2.0f), 2),
            Triangle(new Vector3(-4.0f, 1.0f, 2.0f), new Vector3(2.0f, 1.0f, 1.0f), 3)
        };
        var sources = new GPUDdgiEmissiveSource[candidates.Length];
        DdgiEmissiveTriangleTableStats stats = DdgiEmissiveTriangleTable.Build(candidates, sources);
        Assert.That(stats.SelectedCount, Is.EqualTo(candidates.Length));
        return sources;
    }

    private static DdgiEmissiveTriangleCandidate Triangle(
        Vector3 origin,
        Vector3 radiance,
        ulong stableKey) => new(
        origin,
        origin + new Vector3(1.0f, 0.0f, 0.0f),
        origin + new Vector3(0.0f, 0.0f, 1.0f),
        radiance,
        DdgiEmissiveSourceFlags.Triangle | DdgiEmissiveSourceFlags.DoubleSided,
        stableKey);
}

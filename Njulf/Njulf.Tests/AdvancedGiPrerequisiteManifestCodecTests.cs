using System;
using System.Linq;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiPrerequisiteManifestCodecTests
{
    [Test]
    public void RoundTrip_PreservesFeatureScopedQualificationIdentity()
    {
        AdvancedGiPrerequisiteManifest source = CreateValidManifest();

        Assert.That(
            AdvancedGiPrerequisiteManifestCodec.TryDeserialize(
                AdvancedGiPrerequisiteManifestCodec.Serialize(source),
                out AdvancedGiPrerequisiteManifest loaded,
                out string failure),
            Is.True,
            failure);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Evaluate(AdvancedGiPrerequisiteFeature.ReceiverFeedback).Passed,
                Is.True);
            Assert.That(
                loaded.ComputeQualificationId(AdvancedGiPrerequisiteFeature.NearFieldResidual),
                Is.EqualTo(source.ComputeQualificationId(
                    AdvancedGiPrerequisiteFeature.NearFieldResidual)));
        });
    }

    [Test]
    public void DuplicateOrIncompleteEvidence_FailsClosed()
    {
        AdvancedGiPrerequisiteManifestDocument complete =
            AdvancedGiPrerequisiteManifestCodec.ToDocument(CreateValidManifest());
        AdvancedGiFrozenContractEvidence[] duplicate = complete.Evidence
            .Append(complete.Evidence[0])
            .ToArray();
        AdvancedGiPrerequisiteManifestDocument malformed = complete with
        {
            Evidence = duplicate
        };

        string json = System.Text.Json.JsonSerializer.Serialize(malformed,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.That(
            AdvancedGiPrerequisiteManifestCodec.TryDeserialize(json, out _, out string failure),
            Is.False);
        Assert.That(failure, Is.EqualTo(
            "advanced-gi-prerequisite-manifest-evidence-duplicate-or-unknown"));
    }

    [Test]
    public void MalformedOrMissingDocument_ReturnsEmptyFailClosedManifest()
    {
        Assert.That(
            AdvancedGiPrerequisiteManifestCodec.TryDeserialize("{", out var malformed, out string malformedFailure),
            Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(malformedFailure, Is.EqualTo("advanced-gi-prerequisite-manifest-json-invalid"));
            Assert.That(malformed.Evaluate(AdvancedGiPrerequisiteFeature.TaggedCaustics).Passed,
                Is.False);
        });
    }

    private static AdvancedGiPrerequisiteManifest CreateValidManifest()
    {
        var manifest = new AdvancedGiPrerequisiteManifest
        {
            SpatialEmissiveAndCachedRelightingQualified = true,
            RefinementBricksQualified = true,
            AlphaConformancePassed = true,
            FeatureIsolatedReferenceCorpusAvailable = true
        };
        foreach (AdvancedGiPrerequisiteContract contract in
                 Enum.GetValues<AdvancedGiPrerequisiteContract>())
        {
            string hash = string.Concat(Enumerable.Repeat(
                ((int)contract % 2 == 0 ? "a" : "b"), 64));
            manifest.Add(new AdvancedGiFrozenContractEvidence(
                contract,
                AbiRevision: (uint)contract + 1u,
                ArtifactSha256: hash,
                Verified: true,
                Detail: "verified-test-contract"));
        }
        return manifest;
    }
}

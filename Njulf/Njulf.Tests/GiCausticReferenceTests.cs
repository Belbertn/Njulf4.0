using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticReferenceTests
{
    [Test]
    public void PhotonReferenceAbi_IsExactlyEightyBytes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Unsafe.SizeOf<GPUCausticPhotonReferenceV1>(), Is.EqualTo(80));
            Assert.DoesNotThrow(GPUCausticPhotonReferenceV1.ValidateAbi);
        });
    }

    [Test]
    public void ClosedDielectric_RequiresClosedCurrentPoseAndThicknessContract()
    {
        var material = new GiCausticMaterialContract(
            GiCausticParticipationMode.ClosedDielectricHero,
            Roughness: 0.0f,
            Ior: 1.5f,
            AbsorptionCoefficient: new Vector3(0.1f),
            IsAlphaBlendedOrMasked: false,
            UsesThinTransmission: false,
            HasExplicitThicknessSemantics: true);
        var geometry = new GiCausticHeroGeometryFacts(
            IsRigidOrQualifiedCurrentPose: true,
            IsClosedManifold: true,
            HasConsistentWinding: true,
            HasValidGeometricNormals: true,
            HasUnsupportedNestedMedia: false,
            HasCurrentPoseAccelerationStructure: true,
            HasStableRevisions: true,
            HasAuthenticatedTopologyEvidence: true);

        GiCausticHeroValidation accepted =
            GiCausticHeroContractValidator.Validate(material, geometry);
        GiCausticHeroValidation rejected =
            GiCausticHeroContractValidator.Validate(
                material with { HasExplicitThicknessSemantics = false },
                geometry);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.IsEligible, Is.True);
            Assert.That(rejected.IsEligible, Is.False);
            Assert.That(rejected.RejectionReason,
                Is.EqualTo(GiCausticHeroRejectionReason.MissingThicknessSemantics));
        });
    }

    [Test]
    public void InitialFlux_UsesEveryProposalPdfAndTaskCount()
    {
        var proposal = new GiCausticPhotonTaskPdf(
            Emitter: 0.25,
            CasterGivenEmitter: 0.5,
            Position: 0.25,
            Direction: 0.5);
        GiCausticRgbd flux = GiCausticPathReference.InitialFlux(
            new GiCausticRgbd(4.0, 2.0, 1.0), 100, proposal);

        Assert.Multiple(() =>
        {
            Assert.That(flux.R, Is.EqualTo(2.56).Within(1.0e-12));
            Assert.That(flux.G, Is.EqualTo(1.28).Within(1.0e-12));
            Assert.That(flux.B, Is.EqualTo(0.64).Within(1.0e-12));
        });
    }

    [Test]
    public void DielectricReflectance_ReturnsTotalInternalReflection()
    {
        double reflectance = GiCausticPathReference.DielectricReflectance(
            cosineIncident: 0.2,
            etaIncident: 1.5,
            etaTransmitted: 1.0);

        Assert.That(reflectance, Is.EqualTo(1.0));
    }

    [Test]
    public void BottomKRetention_IsPermutationInvariantAndUnbiased()
    {
        GiCausticCellKey cell = new(-4, 2, 7, 0);
        GiCausticPhotonCandidate[] candidates = Enumerable.Range(0, 8)
            .Select(index => Candidate(cell, (uint)index, (ulong)(8 - index), 1.0f))
            .ToArray();
        GiCausticCacheBuildConfiguration config = new(
            MaximumPhotonsPerCell: 2,
            MaximumOccupiedCells: 4,
            TargetLoadFactor: 0.5f,
            CacheGeneration: 9);

        GiCausticCacheBuildResult original =
            GiCausticPhotonCacheReference.Build(candidates, config);
        Array.Reverse(candidates);
        GiCausticCacheBuildResult reversed =
            GiCausticPhotonCacheReference.Build(candidates, config);

        Assert.Multiple(() =>
        {
            Assert.That(original.IsValid, Is.True);
            Assert.That(reversed.IsValid, Is.True);
            Assert.That(original.Photons.Select(x => x.Photon.StablePhotonId),
                Is.EqualTo(reversed.Photons.Select(x => x.Photon.StablePhotonId)));
            Assert.That(original.Photons.Sum(x => x.Photon.IncidentFlux.X),
                Is.EqualTo(8.0f).Within(1.0e-6f));
            Assert.That(original.Photons.Select(x => x.Photon.IncidentFlux.X),
                Has.All.EqualTo(4.0f));
            Assert.That(original.Photons.Select(x => x.Photon.CacheGeneration),
                Has.All.EqualTo(9u));
        });
    }

    [Test]
    public void CellTable_UsesFullSignedKeyAfterHashProbe()
    {
        GiCausticCellKey first = new(-1, 0, 0, 0);
        GiCausticCellKey second = new(0, -1, 0, 0);
        GiCausticCacheBuildConfiguration config = new(4, 4, 0.5f, 1);
        GiCausticCacheBuildResult result = GiCausticPhotonCacheReference.Build(
        [
            Candidate(first, 1, 42, 1.0f),
            Candidate(second, 2, 42, 1.0f)
        ], config);

        Assert.That(result.Table, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.Table!.TryGetRange(first, out GiCausticCacheRange firstRange), Is.True);
            Assert.That(result.Table!.TryGetRange(second, out GiCausticCacheRange secondRange), Is.True);
            Assert.That(firstRange.Offset, Is.Not.EqualTo(secondRange.Offset));
        });
    }

    [Test]
    public void CacheLayout_IsAllOrNothingAndDoesNotUseDdgiBytes()
    {
        GiCausticCacheLayout valid = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 1_024,
            maximumPhotonsPerCell: 8,
            maximumOccupiedCells: 512,
            recordStride: 80,
            writeBankCount: 1,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0,
            budgetBytes: 4UL * 1024UL * 1024UL);
        GiCausticCacheLayout rejected = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 1_024,
            maximumPhotonsPerCell: 8,
            maximumOccupiedCells: 512,
            recordStride: 80,
            writeBankCount: 1,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0,
            budgetBytes: 1UL);

        Assert.Multiple(() =>
        {
            Assert.That(valid.IsValid, Is.True);
            Assert.That(valid.PhotonRecordBytes, Is.GreaterThan(0));
            Assert.That(valid.CellTableBytes, Is.GreaterThan(0));
            Assert.That(rejected.IsValid, Is.False);
            Assert.That(rejected.TotalBytes, Is.Zero);
        });
    }

    [Test]
    public void NormalizedKernel_PreservesFluxAndNeverUsesDiffuseBaselineCap()
    {
        var footprint = new GiCausticPhotonFootprint(2.0f, 1.0f, 1.0f, 0.0f);
        float center = GiCausticPhotonKernel.EvaluateNormalized(footprint, 0.0f, 0.0f);
        Vector3 flux = GiCausticPhotonKernel.EvaluateFluxDensity(
            new Vector3(1_000.0f), footprint, 0.0f, 0.0f);

        Assert.Multiple(() =>
        {
            Assert.That(center, Is.EqualTo(1.0f / MathF.PI).Within(1.0e-6f));
            Assert.That(flux.X, Is.GreaterThan(100.0f));
            Assert.That(GiCausticPhotonKernel.EvaluateNormalized(footprint, 2.0f, 0.0f), Is.Zero);
        });
    }

    [Test]
    public void TransactionalCache_NeverPublishesInvalidOrStaleGeneration()
    {
        var manager = new GiCausticCacheManager();
        GiCausticCacheRevision revision = new(1, 2, 3, 4, 5, 6, 7);
        GiCausticCacheRevision changedRevision = revision with { CasterTransformRevision = 8 };
        Assert.That(manager.BeginBuild(revision, representedTaskCount: 4), Is.True);
        Assert.That(manager.CompleteBuild(
        [
            Candidate(new GiCausticCellKey(0, 0, 0, 0), 1, 1, 1.0f)
        ], new GiCausticCacheBuildConfiguration(4, 4, 0.5f, CacheGeneration: 1)), Is.True);
        Assert.That(manager.Publish(changedRevision), Is.False);
        Assert.That(manager.TryGetReadable(revision, out _), Is.False);

        Assert.That(manager.BeginBuild(revision, representedTaskCount: 4), Is.True);
        Assert.That(manager.CompleteBuild(
        [
            Candidate(new GiCausticCellKey(0, 0, 0, 0), 1, 1, 1.0f)
        ], new GiCausticCacheBuildConfiguration(4, 4, 0.5f, CacheGeneration: 2)), Is.True);
        Assert.That(manager.Publish(revision), Is.True);
        Assert.That(manager.TryGetReadable(revision, out GiCausticCachePublication published), Is.True);
        Assert.That(published.IsReadable, Is.True);

        manager.Invalidate(changedRevision, "caster-moved");
        Assert.That(manager.TryGetReadable(changedRevision, out _), Is.False);
    }

    private static GiCausticPhotonCandidate Candidate(
        GiCausticCellKey cell,
        uint id,
        ulong hash,
        float flux) => new(
        cell,
        hash,
        new GPUCausticPhotonReferenceV1
        {
            WorldPosition = Vector3.Zero,
            SupportRadius = 1.0f,
            IncidentFlux = new Vector3(flux),
            PathWeightDebug = 1.0f,
            StablePhotonId = id,
            TangentPlaneFootprint = new Vector4(1.0f, 1.0f, 1.0f, 0.0f)
        });
}

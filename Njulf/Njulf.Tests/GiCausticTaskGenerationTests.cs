using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticTaskGenerationTests
{
    [Test]
    public void TaskGenerationAbis_AreByteExactAndWordAddressable()
    {
        GiCausticGpuAbi.VerifyManagedLayout();

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUCausticEmitterV1>(),
                Is.EqualTo(128));
            Assert.That(Marshal.SizeOf<GPUCausticHeroV1>(),
                Is.EqualTo(128));
            Assert.That(Marshal.SizeOf<GPUCausticProposalPairV1>(),
                Is.EqualTo(32));
            Assert.That(Marshal.SizeOf<GPUCausticResolveRequestV1>(),
                Is.EqualTo(64));
            Assert.That(Marshal.SizeOf<GPUCausticResolveResultV1>(),
                Is.EqualTo(48));
            Assert.That(GiCausticGpuAbi.Version, Is.EqualTo(0xC401_0005u));
        });
    }

    [Test]
    public void Compiler_BuildsDeterministicExactTwoLevelDistribution()
    {
        GiCausticEmitterSource[] emitters =
        [
            PointSource(22u, new Vector3(4.0f, 0.0f, 0.0f), 4.0f),
            PointSource(11u, new Vector3(-4.0f, 0.0f, 0.0f), 2.0f)
        ];
        GiCausticHeroSource[] heroes =
        [
            MirrorHero(8u, new Vector3(0.0f, 0.0f, 2.0f), 2.0f),
            MirrorHero(3u, new Vector3(0.0f, 0.0f, -2.0f), 1.0f)
        ];
        GiCausticCacheRevision revision = Revision(100UL);

        Assert.That(GiCausticTaskGenerationCompiler.TryCompile(
            emitters, heroes, 1_024, revision,
            out GiCausticTaskGenerationBatch? first, out string firstReason),
            Is.True, firstReason);
        Array.Reverse(emitters);
        Array.Reverse(heroes);
        Assert.That(GiCausticTaskGenerationCompiler.TryCompile(
            emitters, heroes, 1_024, revision,
            out GiCausticTaskGenerationBatch? reordered,
            out string reorderedReason), Is.True, reorderedReason);

        GPUCausticProposalPairV1[] pairs = first!.ProposalPairs.ToArray();
        double emitterMass = pairs
            .GroupBy(static pair => pair.EmitterIndex)
            .Sum(static group => group.First().EmitterPdf);
        Assert.Multiple(() =>
        {
            Assert.That(first.Emitters.Span[0].StableSourceId, Is.EqualTo(11u));
            Assert.That(first.Heroes.Span[0].StableHeroId, Is.EqualTo(3u));
            Assert.That(pairs[^1].CdfUpper, Is.EqualTo(1.0f));
            Assert.That(emitterMass, Is.EqualTo(1.0).Within(2.0e-6));
            Assert.That(pairs.Select(static pair => pair.StablePairId),
                Is.EqualTo(reordered!.ProposalPairs.ToArray()
                    .Select(static pair => pair.StablePairId)));
            Assert.That(pairs.Select(static pair => pair.CdfUpper),
                Is.EqualTo(reordered!.ProposalPairs.ToArray()
                    .Select(static pair => pair.CdfUpper)));
        });
    }

    [Test]
    public void Compiler_SupportsEveryFrozenEmitterMeasure()
    {
        GiCausticEmitterSource[] emitters =
        [
            PointSource(1u, new Vector3(-2.0f, 0.0f, 0.0f), 1.0f),
            new GiCausticEmitterSource(
                GiCausticGpuEmitterType.Spot, 2u,
                new Vector3(0.0f, 0.0f, -2.0f), Vector3.UnitZ,
                new Vector3(4.0f), 20.0f, Vector3.Zero, Vector3.Zero,
                MathF.Cos(0.5f), MathF.Cos(0.4f), false, 2.0f, 0.5f, 9UL),
            new GiCausticEmitterSource(
                GiCausticGpuEmitterType.DirectionalDisk, 3u,
                Vector3.Zero, -Vector3.UnitY, new Vector3(2.0f), 10.0f,
                Vector3.Zero, Vector3.Zero, 0.0f, 0.0f, false,
                3.0f, 0.0f, 9UL),
            TriangleSource(GiCausticGpuEmitterType.AreaTriangle, 4u, false),
            TriangleSource(GiCausticGpuEmitterType.EmissiveTriangle, 5u, true)
        ];

        bool compiled = GiCausticTaskGenerationCompiler.TryCompile(
            emitters, [MirrorHero(7u, Vector3.Zero, 1.0f)], 256,
            Revision(10UL), out GiCausticTaskGenerationBatch? batch,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(compiled, Is.True, reason);
            Assert.That(batch, Is.Not.Null);
            Assert.That(batch!.Emitters.Length, Is.EqualTo(5));
            Assert.That(batch.ProposalPairs.Length, Is.EqualTo(5));
            Assert.That(batch.Emitters.Span[4].Flags.HasFlag(
                GiCausticGpuEmitterFlags.TwoSided), Is.True);
        });
    }

    [Test]
    public void Batch_RejectsCdfThatDoesNotMatchStoredJointPdf()
    {
        Assert.That(PointSource(1u, Vector3.Zero, 1.0f).TryCompile(
            out GPUCausticEmitterV1 emitter, out string emitterReason),
            Is.True, emitterReason);
        Assert.That(MirrorHero(2u, Vector3.UnitZ, 1.0f).TryCompile(
            out GPUCausticHeroV1 hero, out string heroReason), Is.True, heroReason);
        var malformed = new GPUCausticProposalPairV1
        {
            EmitterIndex = 0u,
            HeroIndex = 0u,
            StablePairId = 3u,
            EmitterPdf = 1.0f,
            CasterGivenEmitterPdf = 1.0f,
            CdfUpper = 0.5f,
            TargetingMixtureProbability = 0.5f
        };

        bool accepted = GiCausticTaskGenerationBatch.TryCreate(
            [emitter], [hero], [malformed], 16, Revision(20UL),
            out _, out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(reason,
                Is.EqualTo("caustic-task-generation-proposal-cdf-mass-mismatch"));
        });
    }

    [Test]
    public void ResolveRequest_RequiresCurrentOpaqueBrdfAndStableRevisions()
    {
        var request = new GPUCausticResolveRequestV1
        {
            WorldPosition = Vector3.One,
            MaximumDistance = 0.5f,
            PackedReceiverNormal = 0u,
            ExpectedCacheGeneration = 4u,
            OutputIndex = 0u,
            Flags = GiCausticGpuResolveRequestFlags.Valid |
                GiCausticGpuResolveRequestFlags.OpaqueReceiver |
                GiCausticGpuResolveRequestFlags.EnergyConservingDiffuseBrdf,
            DiffuseBrdf = new Vector3(0.2f),
            MinimumNormalCosine = 0.8f,
            StableReceiverId = 5u,
            MaterialRevision = 6u,
            TransportRevision = GiCausticGpuAbi.Version
        };
        GPUCausticResolveRequestV1 negativeBrdf = request;
        negativeBrdf.DiffuseBrdf.X = -0.1f;

        Assert.Multiple(() =>
        {
            Assert.That(request.IsValidFor(4u, GiCausticGpuAbi.Version, 1u),
                Is.True);
            Assert.That(negativeBrdf.IsValidFor(
                4u, GiCausticGpuAbi.Version, 1u), Is.False);
        });
    }

    [Test]
    public void TaskValidation_MatchesRoughDielectricAndSpecularAuthoringPolicy()
    {
        const uint generation = 7u;
        const uint taskCount = 16u;
        var task = new GPUCausticPhotonTaskV1
        {
            AbiVersion = GiCausticGpuAbi.Version,
            CacheGeneration = generation,
            StableTaskIdLow = 1u,
            StableTaskIdHigh = 2u,
            HeroInstanceId = 3u,
            HeroMaterialRevisionLow = 4u,
            SourceId = 5u,
            Flags = GiCausticGpuTaskFlags.AuthoredHero |
                GiCausticGpuTaskFlags.ClosedDielectricHero,
            OriginAndSelectionPdf = new Vector4(Vector3.Zero, 0.5f),
            DirectionAndPathPdf = new Vector4(Vector3.UnitZ, 0.25f),
            EmittedContributionAndPositionPdf = new Vector4(
                1.0f, 0.5f, 0.25f, 0.5f),
            InitialFluxAndDirectionPdf = new Vector4(
                4.0f, 2.0f, 1.0f, 0.25f),
            HeroOptics = new Vector4(1.5f, 0.65f, 0.01f, 0.001f),
            AbsorptionAndMaximumDistance = new Vector4(
                0.0f, 0.0f, 0.0f, 100.0f)
        };
        GPUCausticPhotonTaskV1 roughSpecular = task;
        roughSpecular.Flags = GiCausticGpuTaskFlags.AuthoredHero |
            GiCausticGpuTaskFlags.RoughSpecularReference;
        roughSpecular.HeroOptics.Y = 0.03f;

        Assert.Multiple(() =>
        {
            Assert.That(task.IsInputValid(generation, taskCount), Is.True,
                "The trace backend supports rough closed-dielectric interfaces.");
            Assert.That(roughSpecular.IsInputValid(generation, taskCount),
                Is.True,
                "Task validation must use the dielectric transport delta threshold.");
        });
    }

    [Test]
    public void ShaderContracts_GenerateOnGpuAndApplyReceiverBrdfExactlyOnce()
    {
        string tasks = ReadRepoText("Njulf.Shaders", "gi_caustic_tasks.comp");
        string shared = ReadRepoText("Njulf.Shaders", "gi_caustic_shared.glsl");
        string resolve = ReadRepoText("Njulf.Shaders", "gi_caustic_resolve.comp");

        Assert.Multiple(() =>
        {
            Assert.That(tasks, Does.Contain("GI_CAUSTIC_TASK_PHASE_GENERATE"));
            Assert.That(tasks, Does.Contain("GiCausticSelectPair"));
            Assert.That(tasks, Does.Contain("canonicalPdf"));
            Assert.That(tasks, Does.Contain("targetPdf"));
            Assert.That(tasks, Does.Contain("emitterPdf * casterPdf"));
            Assert.That(tasks, Does.Contain("positionPdf * directionPdf"));
            Assert.That(tasks, Does.Contain(
                "bool insideTargetSupport = targetBranch ||"),
                "A sample drawn from the target proposal must remain in that " +
                "proposal's support after finite-precision normalization.");
            Assert.That(shared, Does.Contain(
                "GI_CAUSTIC_ROUGH_SPECULAR_MINIMUM_ROUGHNESS"));
            Assert.That(shared, Does.Not.Contain(
                "roughness <= 0.04 && ior > 1.0"),
                "Closed-dielectric roughness is handled by the microfacet " +
                "interface sampler and must not be constrained to mirror scope.");
            Assert.That(resolve,
                Does.Contain("flux * kernelWeight * diffuseBrdf"));
            Assert.That(resolve, Does.Contain("dot(photonNormal, receiverNormal)"));
            Assert.That(resolve, Does.Contain("transportRevision !="));
            Assert.That(resolve, Does.Not.Contain("MaximumEnergyFraction"));
            Assert.That(resolve, Does.Not.Contain("diffuseBaseline"));
        });
    }

    private static GiCausticEmitterSource PointSource(
        uint id,
        Vector3 position,
        float selectionWeight) => new(
        GiCausticGpuEmitterType.Point,
        id,
        position,
        Vector3.Zero,
        new Vector3(8.0f, 4.0f, 2.0f),
        50.0f,
        Vector3.Zero,
        Vector3.Zero,
        0.0f,
        0.0f,
        false,
        selectionWeight,
        0.6f,
        9UL);

    private static GiCausticEmitterSource TriangleSource(
        GiCausticGpuEmitterType type,
        uint id,
        bool twoSided) => new(
        type,
        id,
        new Vector3(-1.0f, 2.0f, -1.0f),
        Vector3.Zero,
        new Vector3(3.0f, 2.0f, 1.0f),
        50.0f,
        new Vector3(2.0f, 0.0f, 0.0f),
        new Vector3(0.0f, 0.0f, 2.0f),
        0.0f,
        0.0f,
        twoSided,
        2.0f,
        0.5f,
        9UL);

    private static GiCausticHeroSource MirrorHero(
        uint id,
        Vector3 center,
        float proposalWeight) => new(
        id,
        MaterialRevision: 4u,
        BoundsMinimum: center - Vector3.One,
        BoundsMaximum: center + Vector3.One,
        Material: new GiCausticMaterialContract(
            GiCausticParticipationMode.MirrorHero,
            Roughness: 0.0f,
            Ior: 1.5f,
            AbsorptionCoefficient: Vector3.Zero,
            IsAlphaBlendedOrMasked: false,
            UsesThinTransmission: false,
            HasExplicitThicknessSemantics: false),
        Geometry: new GiCausticHeroGeometryFacts(
            IsRigidOrQualifiedCurrentPose: true,
            IsClosedManifold: false,
            HasConsistentWinding: true,
            HasValidGeometricNormals: true,
            HasUnsupportedNestedMedia: false,
            HasCurrentPoseAccelerationStructure: true,
            HasStableRevisions: true,
            HasAuthenticatedTopologyEvidence: true),
        InitialConeRadius: 0.01f,
        ConeSpread: 0.001f,
        MaximumPathDistance: 100.0f,
        ProposalWeight: proposalWeight,
        GeometryRevision: 5UL,
        TransformRevision: 6UL);

    private static GiCausticCacheRevision Revision(ulong value) => new(
        GiCausticGpuAbi.Version,
        value,
        value + 1UL,
        value + 2UL,
        value + 3UL,
        value + 4UL,
        value + 5UL);

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}

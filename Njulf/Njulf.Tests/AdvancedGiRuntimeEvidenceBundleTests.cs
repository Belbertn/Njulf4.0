using System.Text;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiRuntimeEvidenceBundleTests
{
    [Test]
    public void ValidC4AndC5Bundle_RoundTripsWithExactBindings()
    {
        AdvancedGiRuntimeEvidenceBundleDocument expected = CreateValidBundle();

        string json = AdvancedGiRuntimeEvidenceBundleCodec.Serialize(expected);
        bool accepted = AdvancedGiRuntimeEvidenceBundleCodec.TryDeserialize(
            Encoding.UTF8.GetBytes(json),
            out AdvancedGiRuntimeEvidenceBundleDocument actual,
            out string failure);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, failure);
            Assert.That(actual.SchemaRevision,
                Is.EqualTo(AdvancedGiRuntimeEvidenceBundleDocument
                    .CurrentSchemaRevision));
            Assert.That(actual.Caustics, Is.Not.Null);
            Assert.That(actual.NearFieldResidual, Is.Not.Null);
            Assert.That(actual.Caustics!.Evidence,
                Is.EqualTo(expected.Caustics!.Evidence));
            Assert.That(actual.Caustics.Configuration,
                Is.EqualTo(expected.Caustics.Configuration));
            Assert.That(actual.NearFieldResidual!.Evidence,
                Is.EqualTo(expected.NearFieldResidual!.Evidence));
            Assert.That(actual.NearFieldResidual.Configuration,
                Is.EqualTo(expected.NearFieldResidual.Configuration));
        });
    }

    [Test]
    public void FileLoad_IsBoundedAndUsesTheValidatedDocument()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-advanced-gi-runtime-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "runtime-evidence.json");
        try
        {
            AdvancedGiRuntimeEvidenceBundleDocument expected =
                CreateValidBundle();
            File.WriteAllText(
                path,
                AdvancedGiRuntimeEvidenceBundleCodec.Serialize(expected));

            bool accepted = AdvancedGiRuntimeEvidenceBundleCodec.TryLoad(
                path,
                out AdvancedGiRuntimeEvidenceBundleDocument actual,
                out string failure);

            Assert.Multiple(() =>
            {
                Assert.That(accepted, Is.True, failure);
                Assert.That(actual.Caustics!.Evidence.EvidenceId,
                    Is.EqualTo(expected.Caustics!.Evidence.EvidenceId));
                Assert.That(actual.NearFieldResidual!.Evidence.EvidenceId,
                    Is.EqualTo(expected.NearFieldResidual!.Evidence.EvidenceId));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void EmptyUnknownDuplicateAndStaleBundles_FailClosed()
    {
        AdvancedGiRuntimeEvidenceBundleDocument valid = CreateValidBundle();
        string validJson = AdvancedGiRuntimeEvidenceBundleCodec.Serialize(valid);
        string duplicate = validJson.Replace(
            "\"schemaRevision\": 1",
            "\"schemaRevision\": 1, \"schemaRevision\": 1",
            StringComparison.Ordinal);
        string unknown = validJson.Replace(
            "\"schemaRevision\": 1",
            "\"schemaRevision\": 1, \"unreviewedOverride\": true",
            StringComparison.Ordinal);
        AdvancedGiRuntimeEvidenceBundleDocument stale = valid with
        {
            Caustics = valid.Caustics! with
            {
                AdmissionContext = valid.Caustics!.AdmissionContext with
                {
                    ContentRevision =
                        valid.Caustics.AdmissionContext.ContentRevision + 1UL
                }
            }
        };

        bool emptyAccepted = AdvancedGiRuntimeEvidenceBundleCodec.TryDeserialize(
            "{}"u8,
            out _,
            out string emptyFailure);
        bool duplicateAccepted = AdvancedGiRuntimeEvidenceBundleCodec
            .TryDeserialize(
                Encoding.UTF8.GetBytes(duplicate),
                out _,
                out string duplicateFailure);
        bool unknownAccepted = AdvancedGiRuntimeEvidenceBundleCodec.TryDeserialize(
            Encoding.UTF8.GetBytes(unknown),
            out _,
            out string unknownFailure);
        bool staleAccepted = AdvancedGiRuntimeEvidenceBundleCodec.TryValidate(
            stale,
            out string staleFailure);

        Assert.Multiple(() =>
        {
            Assert.That(emptyAccepted, Is.False);
            Assert.That(emptyFailure,
                Is.EqualTo("advanced-gi-runtime-evidence-bundle-empty"));
            Assert.That(duplicateAccepted, Is.False);
            Assert.That(duplicateFailure, Does.Contain("duplicate"));
            Assert.That(unknownAccepted, Is.False);
            Assert.That(unknownFailure,
                Is.EqualTo("advanced-gi-runtime-evidence-bundle-json-invalid"));
            Assert.That(staleAccepted, Is.False);
            Assert.That(staleFailure,
                Does.StartWith("advanced-gi-runtime-evidence-C4-invalid:"));
        });
    }

    [Test]
    public void Serialize_RejectsEvidenceThatDoesNotCompileToActivePlans()
    {
        AdvancedGiRuntimeEvidenceBundleDocument valid = CreateValidBundle();
        AdvancedGiRuntimeEvidenceBundleDocument invalid = valid with
        {
            NearFieldResidual = valid.NearFieldResidual! with
            {
                Evidence = valid.NearFieldResidual!.Evidence with
                {
                    TemporalStabilityVerified = false
                }
            }
        };

        Assert.That(
            () => AdvancedGiRuntimeEvidenceBundleCodec.Serialize(invalid),
            Throws.ArgumentException.With.Message.Contains(
                "advanced-gi-runtime-evidence-C5-invalid"));
    }

    private static AdvancedGiRuntimeEvidenceBundleDocument CreateValidBundle()
    {
        GiCausticRuntimeEvidenceDocument caustics = CreateCaustics();
        SimpleDdgiNearFieldResidualRuntimeEvidenceDocument nearField =
            CreateNearField();
        return new AdvancedGiRuntimeEvidenceBundleDocument
        {
            Caustics = caustics,
            NearFieldResidual = nearField
        };
    }

    private static GiCausticRuntimeEvidenceDocument CreateCaustics()
    {
        var configuration = new GiTaggedCausticCacheConfiguration(
            Enabled: true,
            HeroMaterialCount: 2,
            PhotonTaskCapacity: 1_024,
            MaximumWorldCells: 1_024,
            MaximumPhotonsPerCell: 8,
            MemoryBudgetBytes: 1UL * 1024UL * 1024UL,
            ScreenResolveProfile: new GiCausticScreenResolveProfile(64, 64));
        var context = new GiCausticAdmissionContext(
            DeviceQualificationKey: "10de-2520-driver-test-c4",
            CorpusId: "c4-runtime-bundle-corpus-v1",
            ContentRevision: 11UL,
            LightDistributionRevision: 12UL,
            EmissiveDistributionRevision: 13UL,
            HeroSourceRevision: 14UL,
            CurrentPoseTlasSignature: 15UL,
            ShaderBundleHash: "sha256:c4-runtime-bundle-test");
        GiCausticCacheLayout cache = GiCausticCacheLayoutCompiler.Compile(
            configuration.PhotonTaskCapacity,
            configuration.MaximumPhotonsPerCell,
            configuration.MaximumWorldCells,
            configuration.RecordStride,
            writeBankCount: 2,
            configuration.CacheBankCount,
            configuration.TargetLoadFactor,
            historyBytes: 0UL,
            configuration.MemoryBudgetBytes);
        Assert.That(cache.IsValid, Is.True, cache.FailureReason);
        GiCausticGpuResourceLayout gpu =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                cache,
                configuration.MemoryBudgetBytes,
                configuration.MaximumStorageBufferRange,
                configuration.MaximumEmitterCount,
                configuration.MaximumHeroCount,
                configuration.MaximumProposalPairCount,
                configuration.ScreenResolveProfile));
        Assert.That(gpu.IsValid, Is.True, gpu.FailureReason);
        var evidence = new GiCausticQualificationEvidence(
            EvidenceId: "c4-runtime-bundle-evidence-v1",
            Binding: GiCausticEvidenceBinding.Create(
                context,
                configuration,
                gpu),
            Measurement: new GiCausticQualificationMeasurement(
                context.CorpusId,
                context.ContentRevision,
                C4OffMaskedReferenceError: 1.0,
                C4MaskedReferenceError: 0.5,
                RelativeEmittedToResolvedEnergyError: 0.01,
                AddedGpuMilliseconds: 0.5,
                P95TotalGpuMilliseconds: 1.0,
                P99TotalGpuMilliseconds: 1.2,
                PeakLiveMemoryBytes: gpu.TotalBytes),
            ReferenceFrameCount: 240u,
            IndependentRunCount: 5u,
            CpuGpuPdfAndThroughputParity: true,
            MirrorAndDielectricEnergyConservation: true,
            DifferentialReferencePassed: true,
            BottomKUnbiasednessPassed: true,
            DarkReceiverReferencePassed: true,
            OwnershipIsolationPassed: true,
            PublicationAndMotionStabilityPassed: true,
            WholeFrameRegressionPassed: true,
            QualityPerMillisecondImproved: true,
            ZeroWorkFallbackPassed: true);
        return new GiCausticRuntimeEvidenceDocument
        {
            Evidence = evidence,
            AdmissionContext = context,
            Configuration = configuration
        };
    }

    private static SimpleDdgiNearFieldResidualRuntimeEvidenceDocument
        CreateNearField()
    {
        const int width = 640;
        const int height = 360;
        const ulong budget = 96UL * 1024UL * 1024UL;
        SimpleDdgiNearFieldResidualProfile profile =
            SimpleDdgiNearFieldResidualProfile.HalfResolutionReference;
        SimpleDdgiNearFieldResidualLayout layout =
            SimpleDdgiNearFieldResidualLayoutCompiler.Compile(
                width,
                height,
                profile,
                budget);
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);
        SimpleDdgiNearFieldTraceSourceContract source =
            SimpleDdgiNearFieldTraceSourceContract
                .CreatePreDdgiDirectDiffuseAndEmissive(
                    layout,
                    profile,
                    abiRevision: 1u,
                    layoutRevision: 1u,
                    sourceRevision: 1u);
        var configuration = new SimpleDdgiNearFieldResidualConfiguration(
            Enabled: true,
            Width: width,
            Height: height,
            MemoryBudgetBytes: budget,
            Profile: profile,
            SourceContract: source);
        var context = new SimpleDdgiNearFieldResidualAdmissionContext(
            DeviceQualificationKey: "10de-2520-driver-test-c5",
            CorpusId: "c5-runtime-bundle-corpus-v1",
            ContentRevision: 21UL,
            B3QualificationId: "b3-runtime-bundle-evidence-v1",
            B3QualificationRevision: 2u);
        var evidence = new SimpleDdgiNearFieldResidualQualificationEvidence(
            EvidenceId: "c5-runtime-bundle-evidence-v1",
            Binding: SimpleDdgiNearFieldResidualEvidenceBinding.Create(
                context,
                configuration,
                layout),
            Measurement: new SimpleDdgiNearFieldResidualMeasurement(
                CorpusId: context.CorpusId,
                ContentRevision: context.ContentRevision,
                B3QualificationRevision: context.B3QualificationRevision,
                PostB3NearFieldError: 10.0,
                C5OracleError: 7.0,
                EqualCostAdditionalB3Error: 8.5,
                ErrorIsScreenLocal: true,
                ErrorIsObservableByShortDepthRay: true,
                RootCauseIsNotDdgiLivenessOrAlpha: true,
                UsesSceneLinearReference: true),
            ReferenceSequenceCount:
                SimpleDdgiNearFieldResidualEvidenceAbi
                    .MinimumReferenceSequenceCount,
            ReferenceFrameCount:
                SimpleDdgiNearFieldResidualEvidenceAbi
                    .MinimumReferenceFrameCount,
            IndependentRunCount:
                SimpleDdgiNearFieldResidualEvidenceAbi
                    .MinimumIndependentRunCount,
            C5AddedMilliseconds: 0.60,
            C5P95Milliseconds: 0.70,
            EqualCostAdditionalB3Milliseconds: 0.62,
            B3ConvergenceVerified: true,
            CpuOrImageSpaceOracleVerified: true,
            TraceSourceIndependenceVerified: true,
            TemporalStabilityVerified: true,
            SignedResidualEnergyVerified: true,
            WholeFrameRegressionVerified: true);
        return new SimpleDdgiNearFieldResidualRuntimeEvidenceDocument
        {
            Evidence = evidence,
            AdmissionContext = context,
            Configuration = configuration
        };
    }
}

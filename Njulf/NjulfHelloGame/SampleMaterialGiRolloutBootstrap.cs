using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Applies the material/GI feature policy after every settings mutation. A
/// release manifest is validated before the renderer is created, while an
/// ordinary sample run remains an explicit non-shipping conformance session.
/// </summary>
public sealed class SampleMaterialGiRolloutBootstrap
{
    private readonly MaterialGiRolloutQualificationManifest? _qualification;
    private readonly bool _qualificationCandidate;
    private readonly DateOnly _evaluationDate;
    private int _qualificationAnnouncementWritten;

    private SampleMaterialGiRolloutBootstrap(
        MaterialGiRolloutQualificationManifest? qualification,
        string? manifestPath,
        DateOnly evaluationDate,
        bool qualificationCandidate)
    {
        _qualification = qualification;
        _qualificationCandidate = qualificationCandidate;
        ManifestPath = manifestPath;
        _evaluationDate = evaluationDate;
    }

    public string? ManifestPath { get; }

    public bool IsQualifiedRelease => _qualification is not null;
    public bool IsQualificationCandidate => _qualificationCandidate;

    public static SampleMaterialGiRolloutBootstrap Load(
        string? manifestPath,
        DateOnly? evaluationDate = null,
        bool qualificationCandidate = false)
    {
        DateOnly date = evaluationDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return new SampleMaterialGiRolloutBootstrap(
                null,
                null,
                date,
                qualificationCandidate);
        }
        if (qualificationCandidate)
        {
            throw new ArgumentException(
                "A qualification candidate cannot consume an already approved " +
                "material-GI release manifest.",
                nameof(qualificationCandidate));
        }

        string normalizedPath = Path.GetFullPath(manifestPath);
        MaterialGiRolloutQualificationManifest manifest =
            MaterialGiRolloutQualificationManifest.Load(normalizedPath);

        // Validate at host construction time so an invalid or expired release
        // approval fails before Vulkan, the window, or any runtime assets exist.
        var preflightPolicy = new MaterialGiRolloutPolicy();
        preflightPolicy.ApplyQualification(manifest, date);

        return new SampleMaterialGiRolloutBootstrap(
            manifest,
            normalizedPath,
            date,
            qualificationCandidate: false);
    }

    public MaterialGiRolloutEvaluation Apply(
        RenderSettings settings,
        TextWriter? announcementWriter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_qualification is null)
        {
            if (_qualificationCandidate)
            {
                settings.GlobalIllumination
                    .EnableMaterialGiV2ForQualificationCandidate();
                MaterialGiRolloutEvaluation candidate =
                    settings.GlobalIllumination
                        .EvaluateMaterialGiRollout(_evaluationDate);
                if (candidate.Mode !=
                        MaterialGiRolloutMode.QualificationCandidate ||
                    !candidate.ReleaseQualificationRequired ||
                    candidate.ReleaseQualified ||
                    candidate.QualificationFailureCount != 0)
                {
                    throw new InvalidDataException(
                        "Material-GI V2 qualification-candidate policy failed " +
                        $"closed: {candidate.QualificationSummary}");
                }

                if (announcementWriter is not null &&
                    Interlocked.Exchange(
                        ref _qualificationAnnouncementWritten,
                        1) == 0)
                {
                    announcementWriter.WriteLine(
                        "Material-GI V2 non-shipping qualification candidate active; " +
                        "benchmark evidence may be captured, but release approval " +
                        "and an authenticated qualification manifest are still required.");
                }
                return candidate;
            }

            settings.GlobalIllumination.EnableMaterialGiV2ForConformance();
            return settings.GlobalIllumination.EvaluateMaterialGiRollout(_evaluationDate);
        }

        settings.GlobalIllumination.ApplyMaterialGiV2Qualification(
            _qualification,
            _evaluationDate);
        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(_evaluationDate);
        if (evaluation.Mode != MaterialGiRolloutMode.QualifiedRelease ||
            !evaluation.ReleaseQualificationRequired ||
            !evaluation.ReleaseQualified ||
            evaluation.QualificationFailureCount != 0)
        {
            throw new InvalidDataException(
                "Material-GI V2 release policy failed closed after applying its " +
                $"qualification manifest: {evaluation.QualificationSummary}");
        }

        if (announcementWriter is not null &&
            Interlocked.Exchange(ref _qualificationAnnouncementWritten, 1) == 0)
        {
            announcementWriter.WriteLine(
                "Material-GI V2 qualified release active: " +
                $"approval={evaluation.ApprovalId}, " +
                $"qualificationSchema={_qualification.SchemaVersion}, " +
                "evidenceBundleSchema=" +
                $"{MaterialGiReleaseEvidenceContract.BundleSchemaVersion}, " +
                "evidenceArtifactSchema=" +
                $"{MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion}, " +
                $"devices={evaluation.QualifiedDeviceCount}, " +
                $"evidenceRoles={_qualification.AuthenticatedReleaseEvidenceRoleCount}, " +
                $"tierDevices={_qualification.AuthenticatedTierDeviceCount}, " +
                "lowerMemoryRayQueryDevices=" +
                $"{_qualification.AuthenticatedLowerMemoryRayQueryDeviceCount}, " +
                "recoveryCapabilities=" +
                $"{_qualification.AuthenticatedRecoveryCapabilitySummary}, " +
                $"evidenceSha256={evaluation.EvidenceSha256}, " +
                $"v1Removal={evaluation.V1RemovalTargetDate:yyyy-MM-dd}.");
        }

        return evaluation;
    }
}

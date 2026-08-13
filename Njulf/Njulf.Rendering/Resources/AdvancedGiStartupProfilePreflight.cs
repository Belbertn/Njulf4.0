using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// User-authored inputs that accompany the immutable render-settings snapshot
/// in an Advanced-GI cold-start transaction.
/// </summary>
public sealed record AdvancedGiStartupProfileInputs(
    string ProfilePath,
    AdvancedGiRuntimeContentBinding ContentBinding,
    string? PrerequisiteManifestPath = null,
    string? QualificationManifestPath = null,
    string? RuntimeEvidenceBundlePath = null,
    string? CandidateProfilePath = null);

public readonly record struct AdvancedGiRuntimeBuildIdentity(
    string BuildCommit,
    string ShaderBundleSha256)
{
    public bool IsWellFormed =>
        BuildCommit is { Length: >= 40 and <= 64 } &&
        string.Equals(BuildCommit, BuildCommit.ToLowerInvariant(),
            StringComparison.Ordinal) &&
        BuildCommit.All(Uri.IsHexDigit) &&
        AdvancedGiQualificationContract.NormalizeSha256(
            ShaderBundleSha256).Length == 64;
}

public readonly record struct AdvancedGiStartupProfileCheck(
    string Id,
    bool Passed,
    string Detail);

public sealed record AdvancedGiStartupProfilePreflightResult(
    IReadOnlyList<AdvancedGiStartupProfileCheck> Checks)
{
    public bool Ready => Checks.Count > 0 && Checks.All(static check => check.Passed);

    public string FailureSummary => string.Join(
        "; ",
        Checks.Where(static check => !check.Passed)
            .Select(static check => $"{check.Id}:{check.Detail}"));
}

/// <summary>
/// Performs all checks that do not require creating a Vulkan device. The
/// renderer repeats the security- and device-sensitive checks during startup;
/// this preflight exists to stop an editor from restarting into an obviously
/// incomplete or mismatched profile.
/// </summary>
public static class AdvancedGiStartupProfilePreflight
{
    public static AdvancedGiStartupProfilePreflightResult Evaluate(
        RenderSettings settings,
        AdvancedGiStartupProfileInputs inputs,
        AdvancedGiRuntimeBuildIdentity? runtimeIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputs);

        var checks = new List<AdvancedGiStartupProfileCheck>();
        Add(checks, "profile-path", IsValidOutputPath(inputs.ProfilePath),
            "valid", "path-is-empty-invalid-or-too-long");
        Add(checks, "content-binding", inputs.ContentBinding.IsWellFormed,
            "complete-exact-content-binding",
            "corpus-content-profile-and-scene-hash-are-required");

        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        (AdvancedGiPrerequisiteFeature Feature, string Label)[] requested =
            GetRequestedFeatures(gi);
        (AdvancedGiPrerequisiteFeature Feature, string Label, string Id)[]
            autoQualified = GetAutoQualifiedFeatures(gi);
        bool c4Candidate = gi.GiCausticMode ==
            GiCausticMode.WorldCacheExperiment;
        bool c5Candidate = gi.SimpleDdgiNearFieldResidualMode ==
            SimpleDdgiNearFieldResidualMode.HiZHalfResolutionExperiment;
        bool runtimeIdentityRequired = autoQualified.Length > 0 ||
            c4Candidate || c5Candidate;
        if (runtimeIdentityRequired)
        {
            Add(checks, "runtime-build-identity",
                runtimeIdentity is { IsWellFormed: true },
                "exact-build-and-shader-identity-present",
                "exact-build-and-shader-identity-required");
        }

        AdvancedGiPrerequisiteManifest? prerequisite = null;
        bool prerequisiteRequired = requested.Length > 0;
        if (prerequisiteRequired ||
            !string.IsNullOrWhiteSpace(inputs.PrerequisiteManifestPath))
        {
            bool loaded = TryLoadPrerequisite(
                inputs.PrerequisiteManifestPath,
                out prerequisite,
                out string detail);
            Add(checks, "prerequisite-manifest", loaded,
                "valid-complete-manifest", detail);
            if (loaded && prerequisite is not null)
            {
                foreach ((AdvancedGiPrerequisiteFeature feature, string label)
                         in requested)
                {
                    AdvancedGiPrerequisiteGateResult gate =
                        prerequisite.Evaluate(feature);
                    Add(checks, $"{label}-prerequisite", gate.Passed,
                        gate.QualificationId, gate.FailureDetail);
                }
            }
        }

        AdvancedGiQualificationManifest? qualification = null;
        if (autoQualified.Length > 0 ||
            !string.IsNullOrWhiteSpace(inputs.QualificationManifestPath))
        {
            bool loaded = TryLoadQualification(
                inputs.QualificationManifestPath,
                out qualification,
                out string detail);
            Add(checks, "qualification-manifest", loaded,
                "authenticated-and-artifact-pinned", detail);
            if (loaded && qualification is not null)
            {
                string settingsFingerprint =
                    AdvancedGiSettingsFingerprint.Compute(
                        settings.GlobalIllumination);
                foreach ((AdvancedGiPrerequisiteFeature feature, string label,
                             string id) in autoQualified)
                {
                    bool idValid =
                        AdvancedGiQualificationContract.NormalizeSha256(id)
                            .Length == 64;
                    Add(checks, $"{label}-qualification-id", idValid,
                        "sha256-id-present", "missing-or-invalid-sha256-id");
                    Add(checks, $"{label}-qualification-entry",
                        qualification.Contains(feature),
                        "authenticated-entry-present",
                        "authenticated-feature-entry-missing");
                    bool hasBinding = qualification.TryGetBinding(
                        feature,
                        out AdvancedGiAuthenticatedQualificationBinding binding);
                    bool idMatches = hasBinding &&
                        AdvancedGiCandidateAuthorization.HashEquals(
                            id, binding.QualificationId);
                    Add(checks, $"{label}-qualification-binding",
                        idMatches,
                        "configured-id-matches-authenticated-entry",
                        "configured-id-does-not-match-authenticated-entry");
                    if (!hasBinding)
                        continue;

                    bool settingsMatch =
                        AdvancedGiCandidateAuthorization.HashEquals(
                            settingsFingerprint,
                            binding.SettingsFingerprintSha256);
                    AdvancedGiRuntimeContentBinding expectedContent = new(
                        binding.CorpusSha256,
                        binding.ContentProfileId,
                        binding.SceneAssetSha256);
                    bool contentMatch = ContentBindingsEqual(
                        expectedContent, inputs.ContentBinding);
                    Add(checks, $"{label}-qualified-settings",
                        settingsMatch,
                        "qualified-settings-match",
                        "qualified-settings-fingerprint-mismatch");
                    Add(checks, $"{label}-qualified-content",
                        contentMatch,
                        "qualified-content-match",
                        "qualified-content-binding-mismatch");

                    if (prerequisite is not null)
                    {
                        AdvancedGiPrerequisiteGateResult gate =
                            prerequisite.Evaluate(feature);
                        bool prerequisiteMatch = gate.Passed &&
                            AdvancedGiCandidateAuthorization.HashEquals(
                                gate.QualificationId,
                                binding.PrerequisiteQualificationId);
                        Add(checks, $"{label}-qualified-prerequisite",
                            prerequisiteMatch,
                            "qualified-prerequisite-id-match",
                            "qualified-prerequisite-id-mismatch");
                    }

                    if (runtimeIdentity is { IsWellFormed: true } identity)
                    {
                        bool buildMatch = string.Equals(
                            identity.BuildCommit,
                            binding.BuildCommit,
                            StringComparison.Ordinal) &&
                            AdvancedGiCandidateAuthorization.HashEquals(
                                identity.ShaderBundleSha256,
                                binding.ShaderBundleSha256);
                        Add(checks, $"{label}-qualified-build",
                            buildMatch,
                            "qualified-build-and-shaders-match",
                            "qualified-build-or-shaders-mismatch");
                    }
                }
            }
        }

        bool c4Auto = gi.GiCausticMode == GiCausticMode.AutoQualified;
        bool c5Auto = gi.SimpleDdgiNearFieldResidualMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified;
        if (c4Auto || c5Auto ||
            !string.IsNullOrWhiteSpace(inputs.RuntimeEvidenceBundlePath))
        {
            bool loaded = TryLoadRuntimeEvidence(
                inputs.RuntimeEvidenceBundlePath,
                out AdvancedGiRuntimeEvidenceBundleDocument? evidence,
                out string detail);
            Add(checks, "runtime-evidence-bundle", loaded,
                "valid-scene-layout-bound-evidence", detail);
            if (loaded && evidence is not null)
            {
                if (c4Auto)
                {
                    Add(checks, "C4-runtime-evidence",
                        evidence.Caustics is not null,
                        "caustic-evidence-present",
                        "caustic-evidence-missing");
                }
                if (c5Auto)
                {
                    Add(checks, "C5-runtime-evidence",
                        evidence.NearFieldResidual is not null,
                        "near-field-evidence-present",
                        "near-field-evidence-missing");
                }
            }
        }

        if (c4Candidate || c5Candidate ||
            !string.IsNullOrWhiteSpace(inputs.CandidateProfilePath))
        {
            bool loaded = TryLoadCandidate(
                inputs.CandidateProfilePath,
                out AdvancedGiCandidateProfileDocument? candidate,
                out string detail);
            Add(checks, "candidate-profile", loaded,
                "valid-bounded-candidate-authorization", detail);
            if (loaded && candidate is not null)
            {
                if (c4Candidate)
                {
                    Add(checks, "C4-candidate-configuration",
                        candidate.Caustics is not null,
                        "caustic-candidate-present",
                        "caustic-candidate-missing");
                }
                if (c5Candidate)
                {
                    Add(checks, "C5-candidate-configuration",
                        candidate.NearFieldResidual is not null,
                        "near-field-candidate-present",
                        "near-field-candidate-missing");
                }

                string settingsFingerprint =
                    AdvancedGiSettingsFingerprint.Compute(
                        settings.GlobalIllumination);
                bool settingsMatch = AdvancedGiCandidateAuthorization.HashEquals(
                    candidate.Authorization.SettingsFingerprintSha256,
                    settingsFingerprint);
                bool contentMatch = ContentBindingsEqual(
                    candidate.Authorization.ContentBinding,
                    inputs.ContentBinding);
                Add(checks, "candidate-settings-binding", settingsMatch,
                    "candidate-settings-match",
                    "candidate-settings-fingerprint-mismatch");
                Add(checks, "candidate-content-binding", contentMatch,
                    "candidate-content-match",
                    "candidate-content-binding-mismatch");

                if (runtimeIdentity is { IsWellFormed: true } identity)
                {
                    bool runtimeMatch = candidate.Authorization.MatchesRuntime(
                        identity.BuildCommit,
                        identity.ShaderBundleSha256,
                        settingsFingerprint,
                        inputs.ContentBinding,
                        out string runtimeDetail);
                    Add(checks, "candidate-build-binding", runtimeMatch,
                        "candidate-build-and-shaders-match", runtimeDetail);
                }
            }
        }

        if (checks.Count == 2)
        {
            Add(checks, "feature-selection", true,
                "all-advanced-features-off",
                "all-advanced-features-off");
        }
        return new AdvancedGiStartupProfilePreflightResult(checks);
    }

    public static AdvancedGiStartupProfilePreflightResult SaveValidated(
        RenderSettings settings,
        AdvancedGiStartupProfileInputs inputs,
        AdvancedGiRuntimeBuildIdentity? runtimeIdentity = null)
    {
        AdvancedGiStartupProfilePreflightResult result = Evaluate(
            settings, inputs, runtimeIdentity);
        if (!result.Ready)
        {
            throw new InvalidOperationException(
                "Advanced GI startup profile is not ready: " +
                result.FailureSummary);
        }
        AdvancedGiStartupProfileCodec.Save(
            inputs.ProfilePath,
            settings,
            inputs.ContentBinding,
            inputs.PrerequisiteManifestPath,
            inputs.QualificationManifestPath,
            inputs.RuntimeEvidenceBundlePath,
            inputs.CandidateProfilePath);
        if (!AdvancedGiStartupProfileCodec.TryLoad(
                inputs.ProfilePath,
                out AdvancedGiStartupProfile? persisted,
                out string detail) || persisted is null)
        {
            throw new IOException(
                "The published Advanced GI startup transaction failed " +
                $"readback verification: {detail}");
        }
        return result;
    }

    private static (AdvancedGiPrerequisiteFeature Feature, string Label)[]
        GetRequestedFeatures(GlobalIlluminationSettings gi)
    {
        var result = new List<(AdvancedGiPrerequisiteFeature, string)>(5);
        if (gi.SimpleDdgiReceiverFeedbackMode !=
            SimpleDdgiReceiverFeedbackMode.Off)
            result.Add((AdvancedGiPrerequisiteFeature.ReceiverFeedback, "B1"));
        if (gi.DdgiOpacityMicromapMode != DdgiOpacityMicromapMode.Off)
            result.Add((AdvancedGiPrerequisiteFeature.OpacityMicromaps, "C1"));
        if (gi.SimpleDdgiDirectionalGuidingMode !=
            SimpleDdgiDirectionalGuidingMode.Off)
            result.Add((AdvancedGiPrerequisiteFeature.DirectionalGuiding, "C3"));
        if (gi.GiCausticMode != GiCausticMode.Off)
            result.Add((AdvancedGiPrerequisiteFeature.TaggedCaustics, "C4"));
        if (gi.SimpleDdgiNearFieldResidualMode !=
            SimpleDdgiNearFieldResidualMode.Off)
            result.Add((AdvancedGiPrerequisiteFeature.NearFieldResidual, "C5"));
        return result.ToArray();
    }

    private static (AdvancedGiPrerequisiteFeature Feature, string Label,
        string Id)[] GetAutoQualifiedFeatures(GlobalIlluminationSettings gi)
    {
        var result = new List<(AdvancedGiPrerequisiteFeature, string, string)>(5);
        if (gi.SimpleDdgiReceiverFeedbackMode ==
            SimpleDdgiReceiverFeedbackMode.AutoQualified)
            result.Add((AdvancedGiPrerequisiteFeature.ReceiverFeedback, "B1",
                gi.SimpleDdgiReceiverFeedbackQualificationId));
        if (gi.DdgiOpacityMicromapMode == DdgiOpacityMicromapMode.AutoQualified)
            result.Add((AdvancedGiPrerequisiteFeature.OpacityMicromaps, "C1",
                gi.DdgiOpacityMicromapQualificationId));
        if (gi.SimpleDdgiDirectionalGuidingMode ==
            SimpleDdgiDirectionalGuidingMode.AutoQualified)
            result.Add((AdvancedGiPrerequisiteFeature.DirectionalGuiding, "C3",
                gi.SimpleDdgiDirectionalGuidingQualificationId));
        if (gi.GiCausticMode == GiCausticMode.AutoQualified)
            result.Add((AdvancedGiPrerequisiteFeature.TaggedCaustics, "C4",
                gi.GiCausticQualificationId));
        if (gi.SimpleDdgiNearFieldResidualMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified)
            result.Add((AdvancedGiPrerequisiteFeature.NearFieldResidual, "C5",
                gi.SimpleDdgiNearFieldResidualQualificationId));
        return result.ToArray();
    }

    private static bool TryLoadPrerequisite(
        string? path,
        out AdvancedGiPrerequisiteManifest? manifest,
        out string detail)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "required-path-missing";
            return false;
        }
        bool loaded = AdvancedGiPrerequisiteManifestCodec.TryLoad(
            path, out AdvancedGiPrerequisiteManifest value, out detail);
        manifest = loaded ? value : null;
        return loaded;
    }

    private static bool TryLoadQualification(
        string? path,
        out AdvancedGiQualificationManifest? manifest,
        out string detail)
    {
        manifest = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "required-path-missing";
            return false;
        }
        bool loaded = AdvancedGiQualificationManifestCodec.TryLoad(
            path, out AdvancedGiQualificationManifest value, out detail);
        manifest = loaded ? value : null;
        return loaded;
    }

    private static bool TryLoadRuntimeEvidence(
        string? path,
        out AdvancedGiRuntimeEvidenceBundleDocument? evidence,
        out string detail)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "required-path-missing";
            return false;
        }
        bool loaded = AdvancedGiRuntimeEvidenceBundleCodec.TryLoad(
            path, out AdvancedGiRuntimeEvidenceBundleDocument value,
            out detail);
        evidence = loaded ? value : null;
        return loaded;
    }

    private static bool TryLoadCandidate(
        string? path,
        out AdvancedGiCandidateProfileDocument? candidate,
        out string detail)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "required-path-missing";
            return false;
        }
        return AdvancedGiCandidateProfileCodec.TryLoad(
            path, out candidate, out detail);
    }

    private static bool ContentBindingsEqual(
        in AdvancedGiRuntimeContentBinding left,
        in AdvancedGiRuntimeContentBinding right)
    {
        AdvancedGiRuntimeContentBinding a = left.Normalize();
        AdvancedGiRuntimeContentBinding b = right.Normalize();
        return AdvancedGiCandidateAuthorization.HashEquals(
                   a.CorpusSha256, b.CorpusSha256) &&
               string.Equals(a.ContentProfileId, b.ContentProfileId,
                   StringComparison.Ordinal) &&
               AdvancedGiCandidateAuthorization.HashEquals(
                   a.SceneAssetSha256, b.SceneAssetSha256);
    }

    private static bool IsValidOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2_048 ||
            path.Any(char.IsControl))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(path);
            return !string.IsNullOrWhiteSpace(Path.GetFileName(fullPath));
        }
        catch (Exception exception) when (exception is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void Add(
        ICollection<AdvancedGiStartupProfileCheck> checks,
        string id,
        bool passed,
        string success,
        string failure) => checks.Add(new AdvancedGiStartupProfileCheck(
            id,
            passed,
            passed ? success : failure));
}

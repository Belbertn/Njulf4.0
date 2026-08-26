using System;
using System.Security.Cryptography;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns the immutable-evidence and runtime-content admission state shared by
/// the optional advanced-GI branches. Vulkan capability discovery stays at the
/// renderer boundary and is supplied as value facts.
/// </summary>
internal sealed class AdvancedGiAdmissionCoordinator
{
    private AdvancedGiPrerequisiteManifest _prerequisiteManifest = new();
    private AdvancedGiQualificationManifest _qualificationManifest =
        AdvancedGiQualificationManifest.Empty;
    private AdvancedGiRuntimeContentBinding _runtimeContentBinding =
        AdvancedGiRuntimeContentBinding.Empty;
    private AdvancedGiRuntimeContentState _runtimeContentState =
        AdvancedGiRuntimeContentState.Unconfigured;
    private string _settingsFingerprint = string.Empty;
    private AdvancedGiCandidateProfileDocument? _candidateProfile;
    private string _candidateProfileStatus = "not-configured";
    private AdvancedGiRenderGraphModes _graphModes =
        AdvancedGiRenderGraphModes.Disabled;
    private bool _hasGiCausticEvidence;
    private GiCausticQualificationEvidence _giCausticEvidence;
    private GiCausticAdmissionContext _giCausticAdmissionContext;
    private bool _hasNearFieldResidualEvidence;
    private SimpleDdgiNearFieldResidualQualificationEvidence
        _nearFieldResidualEvidence;
    private SimpleDdgiNearFieldResidualAdmissionContext
        _nearFieldResidualAdmissionContext;

    public AdvancedGiRuntimeContentState RuntimeContentState =>
        _runtimeContentState;

    public AdvancedGiPrerequisiteManifest PrerequisiteManifest =>
        _prerequisiteManifest;

    public AdvancedGiQualificationManifest QualificationManifest =>
        _qualificationManifest;

    public AdvancedGiRuntimeContentBinding RuntimeContentBinding =>
        _runtimeContentBinding;

    public string SettingsFingerprint => _settingsFingerprint;

    public AdvancedGiCandidateProfileDocument? CandidateProfile =>
        _candidateProfile;

    public string CandidateProfileStatus => _candidateProfileStatus;

    public AdvancedGiRenderGraphModes GraphModes => _graphModes;

    public bool HasGiCausticEvidence => _hasGiCausticEvidence;

    public GiCausticQualificationEvidence GiCausticEvidence =>
        _giCausticEvidence;

    public GiCausticAdmissionContext GiCausticAdmissionContext =>
        _giCausticAdmissionContext;

    public bool HasNearFieldResidualEvidence =>
        _hasNearFieldResidualEvidence;

    public SimpleDdgiNearFieldResidualQualificationEvidence
        NearFieldResidualEvidence => _nearFieldResidualEvidence;

    public SimpleDdgiNearFieldResidualAdmissionContext
        NearFieldResidualAdmissionContext =>
            _nearFieldResidualAdmissionContext;

    public void ConfigurePrerequisiteManifest(
        AdvancedGiPrerequisiteManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _prerequisiteManifest = manifest;
    }

    public void ResetPrerequisiteManifest() =>
        _prerequisiteManifest = new AdvancedGiPrerequisiteManifest();

    public AdvancedGiPrerequisiteGateResult EvaluatePrerequisite(
        AdvancedGiPrerequisiteFeature feature) =>
        _prerequisiteManifest.Evaluate(feature);

    public void ConfigureQualificationManifest(
        AdvancedGiQualificationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _qualificationManifest = manifest;
    }

    public void ResetQualificationManifest() =>
        _qualificationManifest = AdvancedGiQualificationManifest.Empty;

    public AdvancedGiQualificationGateResult EvaluateQualification(
        AdvancedGiPrerequisiteFeature feature,
        in AdvancedGiRuntimeQualificationContext context,
        string prerequisiteQualificationId,
        string? configuredQualificationId) =>
        _qualificationManifest.Evaluate(
            feature,
            context,
            prerequisiteQualificationId,
            configuredQualificationId);

    public void ConfigureRuntimeContentBinding(
        in AdvancedGiRuntimeContentBinding binding)
    {
        _runtimeContentBinding = binding.IsWellFormed
            ? binding.Normalize()
            : AdvancedGiRuntimeContentBinding.Empty;
        _runtimeContentState = _runtimeContentBinding.IsWellFormed
            ? new AdvancedGiRuntimeContentState(
                _runtimeContentBinding,
                string.Empty,
                string.Empty,
                false,
                "advanced-gi-runtime-content-awaiting-first-scene-frame")
            : AdvancedGiRuntimeContentState.Unconfigured;
    }

    public AdvancedGiRuntimeContentState ObserveRuntimeContent(
        string observedProfile,
        string observedSceneAssetHash,
        string currentSettingsFingerprint)
    {
        if (!_runtimeContentBinding.IsWellFormed)
        {
            _runtimeContentState = new AdvancedGiRuntimeContentState(
                default,
                observedProfile,
                observedSceneAssetHash,
                false,
                "advanced-gi-runtime-content-binding-not-configured");
            return _runtimeContentState;
        }

        bool settingsMatch = HashTextEquals(
            currentSettingsFingerprint,
            _settingsFingerprint);
        bool profileMatch = string.Equals(
            observedProfile,
            _runtimeContentBinding.ContentProfileId,
            StringComparison.Ordinal);
        bool sceneMatch = HashTextEquals(
            observedSceneAssetHash,
            _runtimeContentBinding.SceneAssetSha256);
        string reason = !settingsMatch
            ? "advanced-gi-runtime-settings-changed-restart-required"
            : !profileMatch
                ? "advanced-gi-runtime-content-profile-mismatch"
                : !sceneMatch
                    ? "advanced-gi-runtime-scene-asset-mismatch"
                    : "advanced-gi-runtime-content-binding-matched";
        _runtimeContentState = new AdvancedGiRuntimeContentState(
            _runtimeContentBinding,
            observedProfile,
            observedSceneAssetHash,
            settingsMatch && profileMatch && sceneMatch,
            reason);
        return _runtimeContentState;
    }

    public void PublishSettingsFingerprint(string fingerprint) =>
        _settingsFingerprint = fingerprint ?? string.Empty;

    public void PublishGraphModes(in AdvancedGiRenderGraphModes graphModes) =>
        _graphModes = graphModes;

    public AdvancedGiRenderGraphModes ResolveStartup(
        in AdvancedGiStartupRequest request)
    {
        _graphModes = new AdvancedGiRenderGraphModes(
            request.ReceiverFeedback,
            request.OpacityMicromaps.EffectiveMode,
            request.DirectionalGuiding.EffectiveMode,
            request.Caustics.EffectiveMode,
            request.NearFieldResidual.EffectiveMode,
            request.NearFieldProfile);
        return _graphModes;
    }

    public void ConfigureCandidateProfile(
        AdvancedGiCandidateProfileDocument profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _candidateProfile = profile;
        _candidateProfileStatus = "loaded:runtime-binding-pending";
    }

    public void RejectCandidateProfile(string failureDetail)
    {
        _candidateProfile = null;
        _candidateProfileStatus = "rejected:" + failureDetail;
    }

    public bool TryAuthorizeCandidate(
        in PerformanceCaptureBuildIdentity buildIdentity,
        out string reason)
    {
        AdvancedGiCandidateProfileDocument? profile = _candidateProfile;
        if (profile is null)
        {
            reason = "advanced-gi-candidate-profile-not-configured";
            return false;
        }

        bool accepted = profile.Authorization.MatchesRuntime(
            buildIdentity.Commit,
            buildIdentity.ShaderBundleHash,
            _settingsFingerprint,
            _runtimeContentBinding,
            out reason);
        _candidateProfileStatus = accepted
            ? "accepted:" + profile.Authorization.AuthorizationId
            : "rejected:" + reason;
        return accepted;
    }

    public void ConfigureGiCausticEvidence(
        in GiCausticQualificationEvidence evidence,
        in GiCausticAdmissionContext admissionContext)
    {
        _giCausticEvidence = evidence;
        _giCausticAdmissionContext = admissionContext;
        _hasGiCausticEvidence = true;
    }

    public void ConfigureNearFieldResidualEvidence(
        in SimpleDdgiNearFieldResidualQualificationEvidence evidence,
        in SimpleDdgiNearFieldResidualAdmissionContext admissionContext)
    {
        _nearFieldResidualEvidence = evidence;
        _nearFieldResidualAdmissionContext = admissionContext;
        _hasNearFieldResidualEvidence = true;
    }

    public void UpdateGiCausticAdmissionContext(
        in GiCausticAdmissionContext admissionContext) =>
        _giCausticAdmissionContext = admissionContext;

    public void UpdateNearFieldResidualAdmissionContext(
        in SimpleDdgiNearFieldResidualAdmissionContext admissionContext) =>
        _nearFieldResidualAdmissionContext = admissionContext;

    public void ClearRuntimeEvidence()
    {
        _hasGiCausticEvidence = false;
        _giCausticEvidence = default;
        _giCausticAdmissionContext = default;
        _hasNearFieldResidualEvidence = false;
        _nearFieldResidualEvidence = default;
        _nearFieldResidualAdmissionContext = default;
    }

    public AdvancedGiAdmissionSnapshot CaptureSnapshot() => new(
        _runtimeContentBinding,
        _runtimeContentState,
        _settingsFingerprint,
        _candidateProfile,
        _candidateProfileStatus,
        _graphModes,
        _hasGiCausticEvidence,
        _giCausticEvidence,
        _giCausticAdmissionContext,
        _hasNearFieldResidualEvidence,
        _nearFieldResidualEvidence,
        _nearFieldResidualAdmissionContext);

    public static GiExperimentModeState<TMode> ResolveMode<TMode>(
        TMode requestedMode,
        TMode offMode,
        bool supported,
        in AdvancedGiPrerequisiteGateResult prerequisiteGate,
        in AdvancedGiQualificationGateResult qualificationGate,
        bool resourcesComplete,
        string? configuredQualificationId,
        string? resourceFailureDetail = null)
        where TMode : struct, Enum
    {
        bool isAutoQualified =
            AdvancedGiActivationPolicy.RequiresQualification(requestedMode);
        bool prerequisitesSatisfied =
            AdvancedGiActivationPolicy.PrerequisitesSatisfied(
                requestedMode,
                prerequisiteGate);
        string qualificationId = isAutoQualified
            ? configuredQualificationId?.Trim() ?? string.Empty
            : string.IsNullOrWhiteSpace(configuredQualificationId)
                ? prerequisiteGate.QualificationId
                : configuredQualificationId.Trim();
        return GiExperimentModeResolver.Resolve(
            requestedMode,
            offMode,
            new GiExperimentModeEvaluation(
                Supported: supported,
                PrerequisitesSatisfied: prerequisitesSatisfied,
                MemoryAdmitted: prerequisitesSatisfied,
                ResourcesComplete: resourcesComplete,
                RequiresQualification: isAutoQualified,
                QualificationPassed:
                    !isAutoQualified || qualificationGate.Passed,
                QualificationId: qualificationId,
                FailureDetail: isAutoQualified && !prerequisiteGate.Passed
                    ? prerequisiteGate.FailureDetail
                    : isAutoQualified && !qualificationGate.Passed
                        ? qualificationGate.FailureDetail
                        : resourceFailureDetail));
    }

    private static bool HashTextEquals(string? left, string? right)
    {
        string normalizedLeft =
            AdvancedGiQualificationContract.NormalizeSha256(left);
        string normalizedRight =
            AdvancedGiQualificationContract.NormalizeSha256(right);
        return normalizedLeft.Length == 64 &&
            normalizedRight.Length == 64 &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(normalizedLeft),
                Convert.FromHexString(normalizedRight));
    }
}

internal readonly record struct AdvancedGiAdmissionSnapshot(
    AdvancedGiRuntimeContentBinding RuntimeContentBinding,
    AdvancedGiRuntimeContentState RuntimeContentState,
    string SettingsFingerprint,
    AdvancedGiCandidateProfileDocument? CandidateProfile,
    string CandidateProfileStatus,
    AdvancedGiRenderGraphModes GraphModes,
    bool HasGiCausticEvidence,
    GiCausticQualificationEvidence GiCausticEvidence,
    GiCausticAdmissionContext GiCausticAdmissionContext,
    bool HasNearFieldResidualEvidence,
    SimpleDdgiNearFieldResidualQualificationEvidence NearFieldResidualEvidence,
    SimpleDdgiNearFieldResidualAdmissionContext NearFieldResidualAdmissionContext);

internal readonly record struct AdvancedGiStartupRequest(
    SimpleDdgiReceiverFeedbackMode ReceiverFeedback,
    GiExperimentModeState<DdgiOpacityMicromapMode> OpacityMicromaps,
    GiExperimentModeState<SimpleDdgiDirectionalGuidingMode> DirectionalGuiding,
    GiExperimentModeState<GiCausticMode> Caustics,
    GiExperimentModeState<SimpleDdgiNearFieldResidualMode> NearFieldResidual,
    AdvancedGiNearFieldGraphProfile NearFieldProfile);

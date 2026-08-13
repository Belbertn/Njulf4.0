using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Editor;

/// <summary>
/// Immutable description of the profile that produced the current renderer.
/// It is captured from RenderingOptions after startup-profile resolution.
/// </summary>
public sealed record AdvancedGiEditorStartupContext(
    string? StartupProfilePath,
    string StartupProfileStatus,
    AdvancedGiRuntimeContentBinding ContentBinding,
    string? PrerequisiteManifestPath,
    string? QualificationManifestPath,
    string? RuntimeEvidenceBundlePath,
    string? CandidateProfilePath)
{
    public static AdvancedGiEditorStartupContext Unconfigured { get; } = new(
        null,
        "not-configured",
        default,
        null,
        null,
        null,
        null);
}

/// <summary>
/// Detached next-start intent. Advanced mode changes are never applied to the
/// live renderer; the controller copies current settings, applies this draft,
/// and persists the resulting cold-start transaction atomically.
/// </summary>
public sealed record AdvancedGiEditorActivationDraft(
    AdvancedGiStartupProfileInputs Profile,
    SimpleDdgiReceiverFeedbackMode ReceiverFeedbackMode,
    DdgiOpacityMicromapMode OpacityMicromapMode,
    SimpleDdgiDirectionalGuidingMode DirectionalGuidingMode,
    GiCausticMode CausticMode,
    SimpleDdgiNearFieldResidualMode NearFieldResidualMode,
    string ReceiverFeedbackQualificationId,
    string OpacityMicromapQualificationId,
    string DirectionalGuidingQualificationId,
    string CausticQualificationId,
    string NearFieldResidualQualificationId)
{
    public RenderSettings CreateSettingsSnapshot(RenderSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        RenderSettings snapshot = source.CreateSnapshot();
        GlobalIlluminationSettings gi = snapshot.GlobalIllumination;
        gi.SimpleDdgiReceiverFeedbackMode = ReceiverFeedbackMode;
        gi.DdgiOpacityMicromapMode = OpacityMicromapMode;
        gi.SimpleDdgiDirectionalGuidingMode = DirectionalGuidingMode;
        gi.GiCausticMode = CausticMode;
        gi.SimpleDdgiNearFieldResidualMode = NearFieldResidualMode;
        gi.SimpleDdgiReceiverFeedbackQualificationId =
            ReceiverFeedbackQualificationId;
        gi.DdgiOpacityMicromapQualificationId =
            OpacityMicromapQualificationId;
        gi.SimpleDdgiDirectionalGuidingQualificationId =
            DirectionalGuidingQualificationId;
        gi.GiCausticQualificationId = CausticQualificationId;
        gi.SimpleDdgiNearFieldResidualQualificationId =
            NearFieldResidualQualificationId;
        return snapshot;
    }
}

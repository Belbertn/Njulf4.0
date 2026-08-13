using System.Reflection;
using System.Text;
using Hexa.NET.ImGui;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Editor;

/// <summary>
/// Live editor for renderer-owned Simple DDGI configuration.
/// </summary>
internal sealed unsafe class GlobalIlluminationEditorPanel
{
    private static readonly string[] GroupOrder =
    [
        "General",
        "Simple DDGI",
        "Far Field",
        "Ray Query / Acceleration Structures",
        "Effective State"
    ];

    private static readonly HashSet<string> AdvancedGiStartupProperties = new(
        StringComparer.Ordinal)
    {
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverContributionFeedbackEnabled),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapExperimentEnabled),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalRayGuidingExperimentEnabled),
        nameof(GlobalIlluminationSettings.DdgiTaggedCausticCacheExperimentEnabled),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualExperimentEnabled),
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverFeedbackMode),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalGuidingMode),
        nameof(GlobalIlluminationSettings.GiCausticMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualMode),
        nameof(GlobalIlluminationSettings.SimpleDdgiReceiverFeedbackQualificationId),
        nameof(GlobalIlluminationSettings.DdgiOpacityMicromapQualificationId),
        nameof(GlobalIlluminationSettings.SimpleDdgiDirectionalGuidingQualificationId),
        nameof(GlobalIlluminationSettings.GiCausticQualificationId),
        nameof(GlobalIlluminationSettings.SimpleDdgiNearFieldResidualQualificationId)
    };

    private static readonly GiPropertyEditor[] PropertyEditors =
        BuildPropertyEditors();

    private string _filter = string.Empty;
    private string _settingsPath = string.Empty;
    private string? _lastError;
    private bool _advancedGiDraftInitialized;
    private string _advancedGiProfilePath = string.Empty;
    private string _advancedGiCorpusSha256 = string.Empty;
    private string _advancedGiContentProfileId = string.Empty;
    private string _advancedGiSceneAssetSha256 = string.Empty;
    private string _advancedGiPrerequisitePath = string.Empty;
    private string _advancedGiQualificationPath = string.Empty;
    private string _advancedGiRuntimeEvidencePath = string.Empty;
    private string _advancedGiCandidatePath = string.Empty;
    private SimpleDdgiReceiverFeedbackMode _receiverFeedbackMode;
    private DdgiOpacityMicromapMode _opacityMicromapMode;
    private SimpleDdgiDirectionalGuidingMode _directionalGuidingMode;
    private GiCausticMode _causticMode;
    private SimpleDdgiNearFieldResidualMode _nearFieldResidualMode;
    private string _receiverFeedbackQualificationId = string.Empty;
    private string _opacityMicromapQualificationId = string.Empty;
    private string _directionalGuidingQualificationId = string.Empty;
    private string _causticQualificationId = string.Empty;
    private string _nearFieldResidualQualificationId = string.Empty;
    private AdvancedGiStartupProfilePreflightResult? _advancedGiPreflight;
    private string? _advancedGiActionStatus;

    internal static IReadOnlyList<PropertyInfo> EditableProperties => PropertyEditors
        .Where(static editor => editor.Editable)
        .Select(static editor => editor.Property)
        .ToArray();

    internal static bool IsSupportedScalarType(Type type) =>
        type == typeof(bool) ||
        type == typeof(string) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type.IsEnum;

    public void Render(EditorController editor)
    {
        ImGui.Begin("Global Illumination");
        RenderRuntimeSummary(editor);

        RenderSettings? renderSettings = editor.RendererSettings;
        if (renderSettings == null)
        {
            ImGui.TextWrapped("Live GI settings are unavailable because this editor is not attached to a Vulkan renderer.");
            ImGui.End();
            return;
        }

        GlobalIlluminationSettings settings = renderSettings.GlobalIllumination;
        RenderAdvancedGiActivation(editor, settings);
        RenderPersistence(editor);
        ImGui.SeparatorText("Live renderer settings");
        ImGui.InputText("Filter settings", ref _filter, (nuint)256);
        ImGui.TextDisabled(
            $"{PropertyEditors.Count(static item => item.Editable && item.Group != "Advanced GI startup")} " +
            "live GI settings; Advanced GI modes are staged separately for restart.");
        RenderScalarSettings(settings);
        RenderLayeredReceiverSettings(renderSettings, editor.RendererDiagnostics);
        RenderSimpleDdgiAuthoredVolumes(editor, settings);

        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f), _lastError);
        ImGui.End();
    }

    private static void RenderLayeredReceiverSettings(
        RenderSettings settings,
        RendererDiagnostics? diagnostics)
    {
        ImGui.SeparatorText("Transparent and decal receivers");
        ImGui.TextWrapped(
            "Layered receivers sample Simple DDGI directly.");

        bool geometryDecalsEnabled = settings.Decals.GeometryDecalsEnabled;
        if (ImGui.Checkbox("Geometry decals enabled", ref geometryDecalsEnabled))
            settings.Decals.GeometryDecalsEnabled = geometryDecalsEnabled;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enables or disables geometry-decal rendering. Changes apply immediately.");

        bool transparentGi = settings.Transparency.ReceiveGlobalIllumination;
        if (ImGui.Checkbox("Transparent materials receive DDGI", ref transparentGi))
            settings.Transparency.ReceiveGlobalIllumination = transparentGi;

        bool decalGi = settings.Decals.ReceiveGlobalIllumination;
        if (ImGui.Checkbox("Geometry decals receive DDGI", ref decalGi))
            settings.Decals.ReceiveGlobalIllumination = decalGi;

        if (diagnostics == null)
            return;

        ImGui.TextDisabled(
            $"Transparent samples: {diagnostics.DdgiTransparentReceiverSampleCount:N0}, " +
            $"irradiance L: {diagnostics.DdgiTransparentReceiverIrradianceLuminanceAverage:0.####}, " +
            $"final L: {diagnostics.DdgiTransparentReceiverFinalLuminanceAverage:0.####}");
        ImGui.TextDisabled(
            $"Decal samples: {diagnostics.DdgiDecalReceiverSampleCount:N0}, " +
            $"irradiance L: {diagnostics.DdgiDecalReceiverIrradianceLuminanceAverage:0.####}, " +
            $"final L: {diagnostics.DdgiDecalReceiverFinalLuminanceAverage:0.####}");
    }

    private void RenderRuntimeSummary(EditorController editor)
    {
        RendererDiagnostics? diagnostics = editor.RendererDiagnostics;
        ImGui.Text($"Authored scene DDGI volumes: {editor.Scene.GlobalIlluminationProbeVolumes.Count}");
        if (editor.Scene.GlobalIlluminationProbeVolumes.Count == 0)
            ImGui.TextDisabled("Automatic Simple-DDGI rings remain available when no authored volume is present.");
        if (ImGui.Button("Add scene DDGI volume"))
            Run(editor.AddGlobalIlluminationProbeVolumeAtCamera);

        if (diagnostics != null)
        {
            int runtimeVolumeCount = diagnostics.DdgiVolumes.Count > 0
                ? diagnostics.DdgiVolumes.Count
                : diagnostics.DdgiProbeVolumeCount;
            string backend = diagnostics.SimpleDdgiActive != 0
                ? "Simple DDGI"
                : "Inactive";
            ImGui.Text($"Active backend: {backend} ({diagnostics.GlobalIlluminationMode})");
            ImGui.Text($"Runtime DDGI volumes: {runtimeVolumeCount}    Probes: {Math.Max(diagnostics.SimpleDdgiProbeCount, diagnostics.DdgiProbeCount)}");
            RenderDdgiReceiverHealth(diagnostics);
            RenderAdvancedGiRuntimeSummary(diagnostics);
        }
    }

    private static void RenderDdgiReceiverHealth(RendererDiagnostics diagnostics)
    {
        bool active = diagnostics.GlobalIlluminationEnabled != 0 &&
                      diagnostics.GlobalIlluminationDdgiActive != 0 &&
                      diagnostics.SimpleDdgiActive != 0;
        bool hasReceiverEvidence = diagnostics.DdgiForwardEstimateCountersReadbackValid != 0 &&
                                   diagnostics.DdgiForwardEstimateSampleCount > 0;
        bool fallbackOnly = hasReceiverEvidence &&
                            diagnostics.DdgiAverageSpatialCoverageEstimate > 0.5f &&
                            diagnostics.DdgiAverageSupportCoverageEstimate <= 0.001f &&
                            diagnostics.DdgiAverageDataConfidenceEstimate <= 0.001f &&
                            diagnostics.DdgiAverageEffectiveContributionEstimate <= 0.001f;

        if (!active)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f),
                "DDGI receiver path is inactive. Check Enabled, Mode, Use DDGI, ray query support, and emergency fallback.");
        }
        else if (fallbackOnly)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.65f, 0.15f, 1f),
                "DDGI is enabled, but covered receivers have no valid probe data; the image is environment fallback, not bounce lighting.");
        }
        else if (hasReceiverEvidence)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(0.35f, 0.85f, 0.45f, 1f),
                "DDGI receiver data is live.");
        }
        else
        {
            ImGui.TextDisabled("DDGI receiver health is warming or detailed counter readback is unavailable.");
        }

        DdgiContentRuntimeSnapshot content = diagnostics.ContentDependentDdgi;
        DdgiContentFeature inactiveRequested =
            content.ConfiguredFeatures & ~content.ActiveFeatures;
        if (inactiveRequested != DdgiContentFeature.None)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.65f, 0.15f, 1f),
                $"Requested DDGI additions are inactive: {inactiveRequested}.");
            ImGui.TextWrapped(
                "Core diffuse DDGI can still be live, but requested thin/dynamic geometry or the directional rough-reflection lobe may be using its qualified fallback. Inspect requested/effective modes in Advanced GI.");
        }
        ImGui.TextDisabled(
            $"Content-dependent DDGI: requested {content.ConfiguredFeatures}; active {content.ActiveFeatures} | " +
            $"directional {content.RequestedDirectionalRadianceMode}->{content.EffectiveDirectionalRadianceMode}, " +
            $"transparency {content.RequestedTransparentGeometryMode}->{content.EffectiveTransparentGeometryMode}");

        ImGui.TextDisabled(
            $"Receiver coverage: spatial {diagnostics.DdgiAverageSpatialCoverageEstimate:P1}, " +
            $"support {diagnostics.DdgiAverageSupportCoverageEstimate:P1}, " +
            $"data {diagnostics.DdgiAverageDataConfidenceEstimate:P1}, " +
            $"ownership {diagnostics.DdgiAverageOwnershipConsumedEstimate:P1}, " +
            $"fallback {diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight:P1}");

        GlobalIlluminationDebugView view = diagnostics.GlobalIlluminationRequestedDebugView;
        string? viewHint = GetDdgiDebugViewHint(view);
        if (!string.IsNullOrWhiteSpace(viewHint))
            ImGui.TextWrapped(viewHint);
    }

    internal static string? GetDdgiDebugViewHint(GlobalIlluminationDebugView view) => view switch
    {
        GlobalIlluminationDebugView.DdgiGatherLocalVolume =>
            "This view shows authored local volumes only. Black is expected in scenes such as Sponza that use automatic camera-relative rings.",
        GlobalIlluminationDebugView.DdgiGatherClipmap =>
            "This is a categorical camera-ring identity view. Large flat color regions are expected; boundaries should move with the automatic Sponza rings.",
        GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight or
        GlobalIlluminationDebugView.DdgiGatherBlendWeight =>
            "A mostly uniform result is expected away from ring transitions; this view visualizes blend weight, not irradiance.",
        GlobalIlluminationDebugView.DdgiGatherFallback =>
            "Black is the healthy endpoint: the primary automatic ring supplied the receiver without needing a coarser-ring retry.",
        GlobalIlluminationDebugView.DdgiUpdateReasons =>
            "Simple DDGI does not publish per-probe scheduler reasons to the compact receiver ABI. This receiver view is black; use the DDGI Updated Probes / Update Reasons debug overlay for scheduler activity.",
        GlobalIlluminationDebugView.DdgiRayBudget =>
            "A uniform result is normal when the scheduler assigns one steady ray tier.",
        GlobalIlluminationDebugView.DdgiCoverage or
        GlobalIlluminationDebugView.DdgiSpatialCoverage =>
            "White means receiver positions are inside a DDGI interpolation lattice; it does not prove that probe data is valid.",
        GlobalIlluminationDebugView.DdgiSupportCoverage or
        GlobalIlluminationDebugView.DdgiDataConfidence =>
            "This is a scalar support mask. A nearly white healthy field is expected; inspect Sampled Irradiance or Final Diffuse for spatial lighting structure.",
        GlobalIlluminationDebugView.DdgiEffectiveWeight =>
            "White is the healthy endpoint: valid DDGI fully owns the indirect receiver. This mask is not a lighting image.",
        GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight =>
            "Black means DDGI owns the receiver; bright values mean environment fallback is replacing missing probe support.",
        GlobalIlluminationDebugView.DdgiVisibility =>
            "This is a scalar transport-visibility term. A mostly white field is valid; Visibility Moments exposes the spatial distance structure.",
        GlobalIlluminationDebugView.DdgiConfidenceChain =>
            "RGB encodes three confidence factors. Healthy saturated channels can cover broad regions; this is not a color image of indirect light.",
        GlobalIlluminationDebugView.DdgiProbeState =>
            "This is a categorical rejection/status view. Broad solid regions are normal; unexpected rejection colors over visible geometry indicate a problem.",
        GlobalIlluminationDebugView.DdgiProbeIndex =>
            "Published probe identities are hashed to colors. A healthy automatic-ring field should form many stable colored cells, not one screen-wide color.",
        GlobalIlluminationDebugView.DdgiClassificationInvalidScore =>
            "Simple DDGI does not publish the classifier's continuous invalidity score to the compact receiver ABI. Use Probe State, the activity overlay, or a persistent-buffer capture for classification evidence.",
        GlobalIlluminationDebugView.DdgiProbeRelocation =>
            "This samples the nearest published probe. Black means that selected probe was not relocated; most receivers can be black even when other probes in the ring were relocated.",
        GlobalIlluminationDebugView.DdgiProbeRelocationDirection =>
            "This samples the nearest published probe. Neutral gray means no relocation; colored deviations encode relocation direction.",
        GlobalIlluminationDebugView.DdgiProbeResidency =>
            "This is a categorical residency state. Broad flat regions are expected when receivers share the same dense or sparse-page state.",
        GlobalIlluminationDebugView.DdgiResidencyFallback =>
            "A uniform value is expected when all visible receivers use the same residency path; it does not measure irradiance.",
        GlobalIlluminationDebugView.DdgiPageAge =>
            "A settled stationary scene can have nearly uniform page age. Use Probe Index or Visibility Moments to verify spatial payload structure.",
        GlobalIlluminationDebugView.DdgiPhysicalPage =>
            "Physical page identities are hashed to colors. A healthy sparse field should show multiple stable regions rather than a single screen-wide value.",
        _ => null
    };

    private static void RenderAdvancedGiRuntimeSummary(
        RendererDiagnostics diagnostics)
    {
        GiRoadmapExperimentDiagnostics roadmap =
            diagnostics.GiRoadmapExperiments;
        if (!ImGui.CollapsingHeader(
                "Advanced GI experiments",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        SimpleDdgiReceiverFeedbackDiagnostics b1 =
            roadmap.ReceiverFeedbackRuntime ??
            SimpleDdgiReceiverFeedbackDiagnostics.Disabled;
        RenderAdvancedGiMode(
            "B1 exact receiver feedback",
            roadmap.Modes.ReceiverFeedback,
            b1.State.ToString(),
            b1.Memory.AllocatedBytes,
            b1.Reason,
            b1.HasAuthoritativePublication);

        OpacityMicromapGpuRuntimeSnapshot c1 = roadmap.OpacityMicromapRuntime;
        RenderAdvancedGiMode(
            "C1 opacity micromaps",
            roadmap.Modes.OpacityMicromap,
            c1.Enabled
                ? $"Published ({c1.PublishedVariantCount:N0} variants)"
                : c1.Supported ? "Supported / inactive" : "Unavailable",
            c1.AllocatedBytes,
            c1.Detail,
            c1.Enabled && c1.PublicationCount > 0UL);

        SimpleDdgiDirectionalGuidingDiagnostics c3 =
            roadmap.DirectionalGuidingRuntime ??
            SimpleDdgiDirectionalGuidingDiagnostics.Disabled;
        RenderAdvancedGiMode(
            "C3 directional guiding",
            roadmap.Modes.DirectionalGuiding,
            c3.State.ToString(),
            c3.Memory.AllocatedBytes,
            c3.Reason,
            c3.HasAuthoritativeSampleReadback);

        GiCausticDiagnostics c4 = roadmap.CausticRuntime ??
            GiCausticDiagnostics.Disabled;
        RenderAdvancedGiMode(
            "C4 tagged caustic cache",
            roadmap.Modes.Caustic,
            c4.State.ToString(),
            c4.Memory.AllocatedBytes,
            c4.Reason,
            c4.HasAuthoritativePublication);

        SimpleDdgiNearFieldResidualDiagnostics c5 =
            diagnostics.SimpleDdgiNearFieldResidual ??
            SimpleDdgiNearFieldResidualDiagnostics.Disabled();
        RenderAdvancedGiMode(
            "C5 near-field residual",
            roadmap.Modes.NearFieldResidual,
            c5.Readback.State.ToString(),
            c5.Memory.AllocatedBytes,
            c5.Readback.Reason,
            c5.IsAuthoritativeReadback);

        ImGui.TextDisabled(
            "C2 ray-tracing invocation reorder: excluded by this plan");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "C2/SER has no runtime work, resource ownership, or promotion state.");
        }
    }

    private static void RenderAdvancedGiMode<TMode>(
        string label,
        in GiExperimentModeState<TMode> mode,
        string runtimeState,
        ulong allocatedBytes,
        string? runtimeReason,
        bool authoritative)
        where TMode : struct, Enum
    {
        string authority = authoritative ? "authoritative" : "not authoritative";
        ImGui.Text(
            $"{label}: {mode.RequestedMode} -> {mode.EffectiveMode} | " +
            $"{runtimeState} | {FormatBytes(allocatedBytes)} | {authority}");
        if (!ImGui.IsItemHovered())
            return;

        string qualification = string.IsNullOrWhiteSpace(mode.QualificationId)
            ? "none"
            : mode.QualificationId;
        string reason = string.IsNullOrWhiteSpace(runtimeReason)
            ? "none"
            : runtimeReason.Trim();
        ImGui.SetTooltip(
            $"Supported: {mode.SupportedMode}\n" +
            $"Admitted: {mode.AdmittedMode}\n" +
            $"Fallback: {mode.FallbackReason} ({mode.FallbackDetail})\n" +
            $"Qualification ID: {qualification}\n" +
            $"Runtime: {reason}");
    }

    private static string FormatBytes(ulong bytes)
    {
        const double KiB = 1024d;
        const double MiB = 1024d * KiB;
        const double GiB = 1024d * MiB;
        return bytes switch
        {
            >= 1024UL * 1024UL * 1024UL => $"{bytes / GiB:0.##} GiB",
            >= 1024UL * 1024UL => $"{bytes / MiB:0.##} MiB",
            >= 1024UL => $"{bytes / KiB:0.##} KiB",
            _ => $"{bytes:N0} B"
        };
    }

    private void RenderAdvancedGiActivation(
        EditorController editor,
        GlobalIlluminationSettings liveSettings)
    {
        InitializeAdvancedGiDraft(editor, liveSettings);
        if (!ImGui.CollapsingHeader(
                "Advanced GI activation (next cold start)",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        ImGui.TextWrapped(
            "B1/C1/C3/C4/C5 admission is selected before Vulkan optional " +
            "features and immutable graph resources are created. These fields " +
            "edit a detached startup draft; they never claim to enable the " +
            "current renderer.");

        AdvancedGiEditorStartupContext startup = editor.AdvancedGiStartup;
        AdvancedGiRuntimeContentState content =
            editor.AdvancedGiRuntimeContentState;
        ImGui.TextDisabled(
            $"Current startup profile: " +
            $"{(string.IsNullOrWhiteSpace(startup.StartupProfilePath) ? "none" : startup.StartupProfilePath)} " +
            $"({startup.StartupProfileStatus})");
        ImGui.TextDisabled(
            $"Current runtime content: {(content.Matched ? "matched" : "not matched")} " +
            $"({content.Reason})");
        ImGui.TextDisabled(
            $"Observed content profile: " +
            $"{(string.IsNullOrWhiteSpace(content.ObservedContentProfileId) ? "unavailable" : content.ObservedContentProfileId)}");
        ImGui.TextDisabled(
            $"Observed scene asset SHA-256: " +
            $"{(string.IsNullOrWhiteSpace(content.ObservedSceneAssetSha256) ? "unavailable" : content.ObservedSceneAssetSha256)}");
        ImGui.TextDisabled(
            $"Candidate authorization: {editor.AdvancedGiCandidateProfileStatus}");

        bool draftChanged = RenderAdvancedGiModeCombo(
            "B1 exact receiver feedback",
            ref _receiverFeedbackMode);
        draftChanged |= RenderAdvancedGiModeCombo(
            "C1 opacity micromaps",
            ref _opacityMicromapMode);
        draftChanged |= RenderAdvancedGiModeCombo(
            "C3 directional guiding",
            ref _directionalGuidingMode);
        draftChanged |= RenderAdvancedGiModeCombo(
            "C4 tagged caustic cache",
            ref _causticMode);
        draftChanged |= RenderAdvancedGiModeCombo(
            "C5 near-field residual",
            ref _nearFieldResidualMode);

        if (UsesAutoQualification())
        {
            ImGui.SeparatorText("AutoQualified IDs");
            if (_receiverFeedbackMode ==
                SimpleDdgiReceiverFeedbackMode.AutoQualified)
            {
                draftChanged |= InputPath(
                    "B1 qualification ID",
                    ref _receiverFeedbackQualificationId,
                    256);
            }
            if (_opacityMicromapMode ==
                DdgiOpacityMicromapMode.AutoQualified)
            {
                draftChanged |= InputPath(
                    "C1 qualification ID",
                    ref _opacityMicromapQualificationId,
                    256);
            }
            if (_directionalGuidingMode ==
                SimpleDdgiDirectionalGuidingMode.AutoQualified)
            {
                draftChanged |= InputPath(
                    "C3 qualification ID",
                    ref _directionalGuidingQualificationId,
                    256);
            }
            if (_causticMode == GiCausticMode.AutoQualified)
            {
                draftChanged |= InputPath(
                    "C4 qualification ID",
                    ref _causticQualificationId,
                    256);
            }
            if (_nearFieldResidualMode ==
                SimpleDdgiNearFieldResidualMode.AutoQualified)
            {
                draftChanged |= InputPath(
                    "C5 qualification ID",
                    ref _nearFieldResidualQualificationId,
                    256);
            }
        }

        ImGui.SeparatorText("Exact startup binding");
        draftChanged |= InputPath(
            "Startup profile", ref _advancedGiProfilePath);
        draftChanged |= InputPath(
            "Corpus SHA-256", ref _advancedGiCorpusSha256);
        draftChanged |= InputPath(
            "Content profile ID", ref _advancedGiContentProfileId, 256);
        draftChanged |= InputPath(
            "Scene asset SHA-256", ref _advancedGiSceneAssetSha256);
        draftChanged |= InputPath(
            "Prerequisite manifest", ref _advancedGiPrerequisitePath);
        draftChanged |= InputPath(
            "Qualification manifest", ref _advancedGiQualificationPath);
        draftChanged |= InputPath(
            "C4/C5 runtime evidence", ref _advancedGiRuntimeEvidencePath);
        draftChanged |= InputPath(
            "C4/C5 candidate profile", ref _advancedGiCandidatePath);
        if (draftChanged)
        {
            _advancedGiPreflight = null;
            _advancedGiActionStatus = null;
        }

        if (ImGui.Button("Validate startup draft"))
            RunAdvancedGiAction(() =>
                _advancedGiPreflight = editor
                    .PreflightAdvancedGiStartupProfile(CreateAdvancedGiDraft()));
        ImGui.SameLine();
        if (ImGui.Button("Save startup profile"))
        {
            RunAdvancedGiAction(() =>
            {
                _advancedGiPreflight = editor.SaveAdvancedGiStartupProfile(
                    CreateAdvancedGiDraft(), restart: false);
                _advancedGiActionStatus =
                    "Profile saved and verified. Restart is still required.";
            });
        }
        ImGui.SameLine();
        bool canRestart = editor.CanRestartForAdvancedGi;
        if (!canRestart)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save and restart renderer"))
        {
            RunAdvancedGiAction(() =>
            {
                _advancedGiPreflight = editor.SaveAdvancedGiStartupProfile(
                    CreateAdvancedGiDraft(), restart: true);
                _advancedGiActionStatus = "Restart requested.";
            });
        }
        if (!canRestart)
            ImGui.EndDisabled();

        RenderAdvancedGiPreflight();
        if (!string.IsNullOrWhiteSpace(_advancedGiActionStatus))
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(0.35f, 0.9f, 0.45f, 1f),
                _advancedGiActionStatus);
        }
        ImGui.Separator();
    }

    private void InitializeAdvancedGiDraft(
        EditorController editor,
        GlobalIlluminationSettings live)
    {
        if (_advancedGiDraftInitialized)
            return;

        AdvancedGiEditorStartupContext startup = editor.AdvancedGiStartup;
        AdvancedGiRuntimeContentBinding binding = startup.ContentBinding;
        AdvancedGiRuntimeContentState observed =
            editor.AdvancedGiRuntimeContentState;
        _advancedGiProfilePath = startup.StartupProfilePath ?? Path.Combine(
            Environment.CurrentDirectory,
            "advanced-gi.startup-profile.json");
        _advancedGiCorpusSha256 = binding.CorpusSha256;
        _advancedGiContentProfileId = binding.IsWellFormed
            ? binding.ContentProfileId
            : observed.ObservedContentProfileId;
        _advancedGiSceneAssetSha256 = binding.IsWellFormed
            ? binding.SceneAssetSha256
            : observed.ObservedSceneAssetSha256;
        _advancedGiPrerequisitePath = startup.PrerequisiteManifestPath ??
            string.Empty;
        _advancedGiQualificationPath = startup.QualificationManifestPath ??
            string.Empty;
        _advancedGiRuntimeEvidencePath = startup.RuntimeEvidenceBundlePath ??
            string.Empty;
        _advancedGiCandidatePath = startup.CandidateProfilePath ?? string.Empty;
        _receiverFeedbackMode = live.SimpleDdgiReceiverFeedbackMode;
        _opacityMicromapMode = live.DdgiOpacityMicromapMode;
        _directionalGuidingMode = live.SimpleDdgiDirectionalGuidingMode;
        _causticMode = live.GiCausticMode;
        _nearFieldResidualMode = live.SimpleDdgiNearFieldResidualMode;
        _receiverFeedbackQualificationId =
            live.SimpleDdgiReceiverFeedbackQualificationId;
        _opacityMicromapQualificationId =
            live.DdgiOpacityMicromapQualificationId;
        _directionalGuidingQualificationId =
            live.SimpleDdgiDirectionalGuidingQualificationId;
        _causticQualificationId = live.GiCausticQualificationId;
        _nearFieldResidualQualificationId =
            live.SimpleDdgiNearFieldResidualQualificationId;
        _advancedGiDraftInitialized = true;
    }

    private AdvancedGiEditorActivationDraft CreateAdvancedGiDraft() => new(
        new AdvancedGiStartupProfileInputs(
            _advancedGiProfilePath.Trim(),
            new AdvancedGiRuntimeContentBinding(
                _advancedGiCorpusSha256.Trim(),
                _advancedGiContentProfileId.Trim(),
                _advancedGiSceneAssetSha256.Trim()),
            EmptyToNull(_advancedGiPrerequisitePath),
            EmptyToNull(_advancedGiQualificationPath),
            EmptyToNull(_advancedGiRuntimeEvidencePath),
            EmptyToNull(_advancedGiCandidatePath)),
        _receiverFeedbackMode,
        _opacityMicromapMode,
        _directionalGuidingMode,
        _causticMode,
        _nearFieldResidualMode,
        _receiverFeedbackQualificationId.Trim(),
        _opacityMicromapQualificationId.Trim(),
        _directionalGuidingQualificationId.Trim(),
        _causticQualificationId.Trim(),
        _nearFieldResidualQualificationId.Trim());

    private void RenderAdvancedGiPreflight()
    {
        if (_advancedGiPreflight is null)
            return;
        ImGui.TextColored(
            _advancedGiPreflight.Ready
                ? new System.Numerics.Vector4(0.35f, 0.9f, 0.45f, 1f)
                : new System.Numerics.Vector4(1f, 0.55f, 0.2f, 1f),
            _advancedGiPreflight.Ready
                ? "Preflight ready: device admission will be rechecked on startup."
                : "Preflight blocked: resolve every failed item before restart.");
        foreach (AdvancedGiStartupProfileCheck check in
                 _advancedGiPreflight.Checks)
        {
            ImGui.TextColored(
                check.Passed
                    ? new System.Numerics.Vector4(0.55f, 0.85f, 0.6f, 1f)
                    : new System.Numerics.Vector4(1f, 0.45f, 0.25f, 1f),
                $"{(check.Passed ? "PASS" : "FAIL")} {check.Id}: {check.Detail}");
        }
    }

    private bool UsesAutoQualification() =>
        _receiverFeedbackMode == SimpleDdgiReceiverFeedbackMode.AutoQualified ||
        _opacityMicromapMode == DdgiOpacityMicromapMode.AutoQualified ||
        _directionalGuidingMode ==
            SimpleDdgiDirectionalGuidingMode.AutoQualified ||
        _causticMode == GiCausticMode.AutoQualified ||
        _nearFieldResidualMode ==
            SimpleDdgiNearFieldResidualMode.AutoQualified;

    private static bool RenderAdvancedGiModeCombo<TMode>(
        string label,
        ref TMode mode)
        where TMode : struct, Enum
    {
        if (!ImGui.BeginCombo(label, mode.ToString()))
            return false;
        bool changed = false;
        foreach (TMode candidate in Enum.GetValues<TMode>())
        {
            bool selected = EqualityComparer<TMode>.Default.Equals(
                candidate, mode);
            if (ImGui.Selectable(candidate.ToString(), selected))
            {
                mode = candidate;
                changed = true;
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return changed;
    }

    private static bool InputPath(
        string label,
        ref string value,
        uint capacity = 2_048)
    {
        ImGui.SetNextItemWidth(560f);
        return ImGui.InputText(label, ref value, capacity);
    }

    private void RunAdvancedGiAction(Action action)
    {
        try
        {
            action();
            _lastError = null;
        }
        catch (Exception error)
        {
            _advancedGiActionStatus = null;
            _lastError = error.Message;
        }
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void RenderPersistence(EditorController editor)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            _settingsPath = Path.Combine(Environment.CurrentDirectory, "render-settings.json");

        ImGui.SetNextItemWidth(420f);
        ImGui.InputText("Render settings file", ref _settingsPath, (nuint)1024);
        ImGui.SameLine();
        if (ImGui.Button("Save render settings"))
            Run(() => editor.SaveRenderSettings(_settingsPath));
    }

    private void RenderScalarSettings(GlobalIlluminationSettings settings)
    {
        bool filtering = !string.IsNullOrWhiteSpace(_filter);
        foreach (string group in GroupOrder)
        {
            GiPropertyEditor[] visible = PropertyEditors
                .Where(item => item.Group == group && MatchesFilter(item))
                .ToArray();
            if (visible.Length == 0)
                continue;

            ImGuiTreeNodeFlags flags = filtering || group == "General"
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{group} ({visible.Length})", flags))
                continue;

            foreach (GiPropertyEditor item in visible)
                RenderProperty(settings, item);
        }
    }

    private static void RenderProperty(GlobalIlluminationSettings settings, GiPropertyEditor item)
    {
        object? current = item.Property.GetValue(settings);
        if (!item.Editable)
        {
            ImGui.TextDisabled($"{item.Label}: {FormatValue(current)}");
            return;
        }

        Type type = item.Property.PropertyType;
        bool changed = false;
        object? next = current;
        if (type == typeof(bool))
        {
            bool value = (bool)(current ?? false);
            changed = ImGui.Checkbox(item.Label, ref value);
            next = value;
        }
        else if (type == typeof(string))
        {
            string value = (string?)current ?? string.Empty;
            changed = ImGui.InputText(item.Label, ref value, (nuint)256);
            next = value;
        }
        else if (type == typeof(int))
        {
            int value = (int)(current ?? 0);
            changed = ImGui.DragInt(item.Label, ref value, 1f);
            next = value;
        }
        else if (type == typeof(float))
        {
            float value = (float)(current ?? 0f);
            changed = ImGui.DragFloat(item.Label, ref value, ResolveFloatSpeed(item.Property.Name, value));
            next = value;
        }
        else if (type == typeof(uint))
        {
            uint value = (uint)(current ?? 0u);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.U32, &value);
            next = value;
        }
        else if (type == typeof(long))
        {
            long value = (long)(current ?? 0L);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.S64, &value);
            next = value;
        }
        else if (type == typeof(ulong))
        {
            ulong value = (ulong)(current ?? 0UL);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.U64, &value);
            next = value;
        }
        else if (type == typeof(double))
        {
            double value = (double)(current ?? 0d);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.Double, &value);
            next = value;
        }
        else if (type.IsEnum)
        {
            if (ImGui.BeginCombo(item.Label, current?.ToString() ?? string.Empty))
            {
                foreach (object value in Enum.GetValues(type))
                {
                    if (value is GlobalIlluminationDebugView giView &&
                        !RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(giView))
                    {
                        continue;
                    }

                    bool selected = Equals(value, current);
                    if (ImGui.Selectable(value.ToString() ?? string.Empty, selected))
                    {
                        next = value;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            if (current is GlobalIlluminationDebugView currentGiView &&
                !RendererBuildFeatures.IsGlobalIlluminationDebugViewAvailable(currentGiView))
            {
                ImGui.TextDisabled(
                    RendererBuildFeatures.GetGlobalIlluminationDebugViewAvailabilityReason(currentGiView));
            }
        }

        if (changed)
        {
            if (item.Property.Name == nameof(GlobalIlluminationSettings.DdgiQualityTier) && next is DdgiQualityTier tier)
                settings.ApplyDdgiQualityTier(tier);
            else
                item.Property.SetValue(settings, next);
        }

        if (ImGui.IsItemHovered())
        {
            string valueText = FormatValue(item.Property.GetValue(settings));
            if (item.Property.PropertyType == typeof(ulong) && item.Property.Name.EndsWith("Bytes", StringComparison.Ordinal))
            {
                double mebibytes = Convert.ToDouble(item.Property.GetValue(settings)) / (1024d * 1024d);
                valueText += $" ({mebibytes:0.##} MiB)";
            }
            ImGui.SetTooltip($"{item.Property.Name} = {valueText}");
        }
    }

    private void RenderSimpleDdgiAuthoredVolumes(EditorController editor, GlobalIlluminationSettings settings)
    {
        ImGui.SeparatorText($"Simple DDGI local overrides ({settings.SimpleDdgiAuthoredVolumes.Count})");
        ImGui.TextWrapped("Optional local quality boxes layered over the automatic near/mid/far camera-relative rings.");
        if (ImGui.Button("Add local override at camera"))
            Run(editor.AddSimpleDdgiAuthoredVolumeAtCamera);

        int removeIndex = -1;
        for (int index = 0; index < settings.SimpleDdgiAuthoredVolumes.Count; index++)
        {
            SimpleDdgiAuthoredVolume source = settings.SimpleDdgiAuthoredVolumes[index];
            if (!ImGui.CollapsingHeader($"Override {index + 1}: {source.Purpose}##simpleDdgiOverride{index}"))
                continue;

            ImGui.PushID(index);
            NumericsVector3 min = ToNumerics(source.Min);
            NumericsVector3 max = ToNumerics(source.Max);
            NumericsVector3 phase = ToNumerics(source.LatticePhase);
            float spacing = source.Spacing;
            int priority = source.Priority;
            SimpleDdgiVolumePurpose purpose = source.Purpose;
            bool changed = ImGui.DragFloat3("Min", ref min, 0.05f);
            changed |= ImGui.DragFloat3("Max", ref max, 0.05f);
            changed |= ImGui.DragFloat("Spacing", ref spacing, 0.01f, 0.25f, 8f);
            changed |= ImGui.DragFloat3("Lattice phase", ref phase, 0.01f);
            changed |= ImGui.DragInt("Priority", ref priority, 1f);
            if (ImGui.BeginCombo("Purpose", purpose.ToString()))
            {
                foreach (SimpleDdgiVolumePurpose candidate in Enum.GetValues<SimpleDdgiVolumePurpose>())
                {
                    if (ImGui.Selectable(candidate.ToString(), candidate == purpose))
                    {
                        purpose = candidate;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            if (changed)
            {
                CoreVector3 first = ToCore(min);
                CoreVector3 second = ToCore(max);
                CoreVector3 normalizedMin = new(
                    MathF.Min(first.X, second.X),
                    MathF.Min(first.Y, second.Y),
                    MathF.Min(first.Z, second.Z));
                CoreVector3 normalizedMax = new(
                    MathF.Max(first.X, second.X),
                    MathF.Max(first.Y, second.Y),
                    MathF.Max(first.Z, second.Z));
                editor.UpdateSimpleDdgiAuthoredVolume(index, new SimpleDdgiAuthoredVolume(
                    normalizedMin,
                    normalizedMax,
                    Math.Clamp(spacing, 0.25f, 8f),
                    ToCore(phase),
                    purpose,
                    priority));
            }

            if (ImGui.Button("Remove override"))
                removeIndex = index;
            ImGui.PopID();
        }

        if (removeIndex >= 0)
            editor.RemoveSimpleDdgiAuthoredVolume(removeIndex);
    }

    private bool MatchesFilter(GiPropertyEditor item) =>
        string.IsNullOrWhiteSpace(_filter) ||
        item.Property.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.Group.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private void Run(Action action)
    {
        try
        {
            action();
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = error.Message;
        }
    }

    private void Run<T>(Func<T> action)
    {
        try
        {
            _ = action();
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = error.Message;
        }
    }

    private static GiPropertyEditor[] BuildPropertyEditors()
    {
        var result = new List<GiPropertyEditor>();
        foreach (PropertyInfo property in typeof(GlobalIlluminationSettings)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.MetadataToken))
        {
            if (property.GetIndexParameters().Length != 0 ||
                property.Name == nameof(GlobalIlluminationSettings.SimpleDdgiAuthoredVolumes))
            {
                continue;
            }

            bool editable = property.SetMethod?.IsPublic == true;
            if (!IsSupportedScalarType(property.PropertyType))
            {
                if (editable)
                    throw new NotSupportedException($"GI editor does not support writable property '{property.Name}' ({property.PropertyType.Name}).");
                continue;
            }

            result.Add(new GiPropertyEditor(
                property,
                ResolveGroup(property.Name, editable),
                FormatLabel(property.Name),
                editable));
        }
        return result.ToArray();
    }

    private static string ResolveGroup(string name, bool editable)
    {
        if (!editable || name.StartsWith("Effective", StringComparison.Ordinal))
            return "Effective State";
        if (AdvancedGiStartupProperties.Contains(name))
            return "Advanced GI startup";
        if (name.StartsWith("SimpleDdgi", StringComparison.Ordinal))
            return "Simple DDGI";
        if (name.StartsWith("FarField", StringComparison.Ordinal))
            return "Far Field";
        if (name.StartsWith("GiAcceleration", StringComparison.Ordinal) ||
            name.StartsWith("StreamedGi", StringComparison.Ordinal) ||
            name == nameof(GlobalIlluminationSettings.UseRayQueryBackend))
        {
            return "Ray Query / Acceleration Structures";
        }
        if (name.StartsWith("Ddgi", StringComparison.Ordinal) || name == nameof(GlobalIlluminationSettings.UseDdgi))
            return "Simple DDGI";
        return "General";
    }

    private static string FormatLabel(string name)
    {
        var builder = new StringBuilder(name.Length + 12);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) &&
                (char.IsLower(name[index - 1]) ||
                 index + 1 < name.Length && char.IsLower(name[index + 1])))
            {
                builder.Append(' ');
            }
            builder.Append(current);
        }
        return builder.ToString()
            .Replace("Ddgi", "DDGI", StringComparison.Ordinal)
            .Replace("Gpu", "GPU", StringComparison.Ordinal)
            .Replace("Gi ", "GI ", StringComparison.Ordinal);
    }

    private static float ResolveFloatSpeed(string name, float value)
    {
        if (name.Contains("Fraction", StringComparison.Ordinal) ||
            name.Contains("Threshold", StringComparison.Ordinal) ||
            name.Contains("Hysteresis", StringComparison.Ordinal) ||
            name.Contains("Bias", StringComparison.Ordinal) ||
            name.Contains("Intensity", StringComparison.Ordinal) ||
            name.Contains("Scale", StringComparison.Ordinal))
        {
            return 0.01f;
        }
        return MathF.Max(0.01f, MathF.Abs(value) * 0.01f);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        float number => number.ToString("0.####"),
        double number => number.ToString("0.####"),
        _ => value.ToString() ?? string.Empty
    };

    private static NumericsVector3 ToNumerics(CoreVector3 value) => new(value.X, value.Y, value.Z);
    private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);

    private sealed record GiPropertyEditor(PropertyInfo Property, string Group, string Label, bool Editable);
}

namespace Njulf.Rendering.Diagnostics;

internal sealed record PerformanceCaptureBuildIdentity(
    string ApplicationVersion,
    string Commit,
    string ShaderBundleHash,
    string ExecutableHash,
    string DirtyWorktreeState,
    string CompileConfiguration,
    string TargetFramework)
{
    public static PerformanceCaptureBuildIdentity Uninitialized { get; } = new(
        "unavailable:application-version-not-initialized",
        "unavailable:source-revision-not-initialized",
        "unavailable:shader-bundle-not-initialized",
        "unavailable:executable-hash-not-initialized",
        "unavailable:dirty-worktree-state-not-initialized",
        "unavailable:build-configuration-not-initialized",
        "unavailable:target-framework-not-initialized");

    public string CreateBuildConfiguration(string? validationMode)
    {
        string validation = PerformanceCaptureHashing.NormalizeMetadataValue(
            validationMode,
            "unavailable:validation-mode-not-reported");
        return CompileConfiguration + "; validation=" + validation +
            "; framework=" + TargetFramework;
    }
}

internal readonly record struct PerformanceCaptureFramePreparation(
    bool SceneChanged,
    ulong ObservedSceneRevision,
    ulong FramesSinceSceneLoad);

internal sealed record PerformanceCaptureIdentitySnapshot(
    string SceneAssetHash,
    string SceneStateHash,
    PerformanceCaptureRunMetadata Run,
    PerformanceCaptureCameraMetadata Camera,
    PerformanceCaptureFrameMetadata Frame);

internal readonly record struct PerformanceCaptureStartupIdentity(
    string ApplicationVersion,
    string Commit,
    string ShaderBundleHash,
    string CompileConfiguration,
    string TargetFramework);

internal readonly record struct PerformanceCapturePostPipelineIdentity(
    string ExecutableHash,
    string DirtyWorktreeState);

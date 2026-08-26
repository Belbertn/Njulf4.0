using System;
using Njulf.Core.Interfaces;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics;

internal sealed class PerformanceCaptureMetadataProvider
{
    private readonly PerformanceCaptureHostIdentityResolver
        _hostIdentityResolver;
    private ulong _sceneLoadFrameSerial;
    private ulong _cameraCutSerial;

    internal PerformanceCaptureMetadataProvider(
        PerformanceCaptureHostIdentityResolver hostIdentityResolver)
    {
        _hostIdentityResolver = hostIdentityResolver ??
            throw new ArgumentNullException(nameof(hostIdentityResolver));
    }

    internal string SceneKind { get; set; } = string.Empty;
    internal string Scenario { get; set; } = string.Empty;
    internal PerformanceCaptureBuildIdentity BuildIdentity { get; private set; } =
        PerformanceCaptureBuildIdentity.Uninitialized;
    internal ulong ObservedSceneRevision { get; private set; } = ulong.MaxValue;

    internal void ResolveStartupIdentity()
    {
        PerformanceCaptureStartupIdentity identity =
            _hostIdentityResolver.ResolveStartupIdentity();
        BuildIdentity = new PerformanceCaptureBuildIdentity(
            identity.ApplicationVersion,
            identity.Commit,
            identity.ShaderBundleHash,
            "unavailable:executable-hash-not-initialized",
            "unavailable:dirty-worktree-state-not-initialized",
            identity.CompileConfiguration,
            identity.TargetFramework);
    }

    internal void ResolvePostPipelineIdentity()
    {
        PerformanceCapturePostPipelineIdentity identity =
            _hostIdentityResolver.ResolvePostPipelineIdentity();
        BuildIdentity = BuildIdentity with
        {
            ExecutableHash = identity.ExecutableHash,
            DirtyWorktreeState = identity.DirtyWorktreeState
        };
    }

    internal void ApplySceneLabels(
        SceneRenderingData sceneData,
        string? sceneName)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        sceneData.CaptureSceneName = string.IsNullOrWhiteSpace(sceneName)
            ? "unknown-scene"
            : sceneName;
        sceneData.CaptureScenario = Scenario;
    }

    internal PerformanceCaptureFramePreparation ObserveSceneAndCamera(
        SceneRenderingData sceneData,
        ICamera camera,
        ulong frameSerial)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        ArgumentNullException.ThrowIfNull(camera);

        bool sceneChanged =
            ObservedSceneRevision != sceneData.SceneContentRevision;
        if (sceneChanged)
        {
            ObservedSceneRevision = sceneData.SceneContentRevision;
            _sceneLoadFrameSerial = frameSerial;
            _cameraCutSerial = 0;
        }

        sceneData.CaptureCameraYawRadians =
            MathF.Atan2(camera.Forward.X, -camera.Forward.Z);
        sceneData.CaptureCameraPitchRadians =
            PerformanceCaptureHashing.ExtractPitchRadians(camera.Forward);
        sceneData.CaptureCameraFieldOfViewRadians = camera.FieldOfView;
        sceneData.CaptureCameraNearPlane = camera.NearPlane;
        sceneData.CaptureCameraFarPlane = camera.FarPlane;
        ulong framesSinceSceneLoad = frameSerial >= _sceneLoadFrameSerial
            ? frameSerial - _sceneLoadFrameSerial
            : 0;
        sceneData.CaptureFramesSinceSceneLoad = framesSinceSceneLoad;

        return new PerformanceCaptureFramePreparation(
            sceneChanged,
            ObservedSceneRevision,
            framesSinceSceneLoad);
    }

    internal void ApplyCameraCut(
        SceneRenderingData sceneData,
        bool cameraCut)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        if (cameraCut)
            _cameraCutSerial++;
        sceneData.CaptureCameraCutSerial = _cameraCutSerial;
    }

    internal PerformanceCaptureIdentitySnapshot CreateFrameIdentity(
        SceneRenderingData sceneData,
        RendererValidationMode validationMode,
        int settingsSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(sceneData);
        PerformanceCaptureBuildIdentity build = BuildIdentity;
        var run = new PerformanceCaptureRunMetadata(
            PerformanceCaptureHashing.ResolveSceneKind(
                SceneKind,
                sceneData.CaptureSceneName),
            PerformanceCaptureHashing.ResolveScenario(
                sceneData.CaptureScenario),
            build.CreateBuildConfiguration(validationMode.ToString()),
            build.ApplicationVersion,
            build.Commit,
            PerformanceCaptureHashing.ResolveShaderBundleHash(
                build.ShaderBundleHash),
            settingsSchemaVersion)
        {
            ExecutableHash = build.ExecutableHash,
            DirtyWorktreeState = build.DirtyWorktreeState
        };
        var camera = new PerformanceCaptureCameraMetadata(
            sceneData.CameraPosition.X,
            sceneData.CameraPosition.Y,
            sceneData.CameraPosition.Z,
            sceneData.CaptureCameraYawRadians,
            sceneData.CaptureCameraPitchRadians,
            sceneData.CaptureCameraFieldOfViewRadians,
            sceneData.CaptureCameraNearPlane,
            sceneData.CaptureCameraFarPlane,
            PerformanceCaptureHashing.ComputeMatrixHash(sceneData.ViewMatrix),
            PerformanceCaptureHashing.ComputeMatrixHash(
                sceneData.ProjectionMatrix),
            sceneData.CaptureCameraCutSerial);
        var frame = new PerformanceCaptureFrameMetadata(
            sceneData.DdgiFrameSerial,
            sceneData.CaptureFramesSinceSceneLoad,
            sceneData.DdgiWarmupState,
            sceneData.SimpleDdgiFramesSinceLastRecenter,
            sceneData.SimpleDdgiFramesSinceLastClear)
        {
            DdgiCacheGeneration = sceneData.DdgiCacheGeneration,
            SimpleDdgiTransportGeneration =
                sceneData.SimpleDdgiTransportGeneration,
            TransportConvergencePending =
                sceneData.SimpleDdgiTransportGlobalConvergencePending != 0,
            TransportConvergedProbeCount =
                sceneData.SimpleDdgiTransportConvergedProbeCount,
            TransportPendingProbeCount =
                sceneData.SimpleDdgiTransportPendingSolverProbeCount
        };

        return new PerformanceCaptureIdentitySnapshot(
            PerformanceCaptureHashing.ComputeSceneAssetHash(sceneData),
            PerformanceCaptureHashing.ComputeSceneStateHash(sceneData),
            run,
            camera,
            frame);
    }
}

using System.Diagnostics;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Debug;

internal readonly record struct DebugOverlayBuildOptions(
    bool SimpleDdgiEnabled,
    bool ShowXRayVolumes,
    bool ShowDepthTestedVolumes,
    int SelectedReflectionProbeIndex);

internal sealed class DebugOverlayBuilder
{
    private const int MaxDetailedProbeMarkersPerFrame = 768;

    private readonly DebugDrawList _drawList = new();
    private readonly IDebugOverlayResourceLookup _resources;
    private GPUDdgiProbeDebugInstance[]? _ddgiProbeDebugInstanceScratch;
    private DebugDdgiOverlayGpuCounters _completedDebugDdgiOverlayCounters;

    internal DebugOverlayBuilder(IDebugOverlayResourceLookup resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    internal DebugDrawList DrawList => _drawList;

    internal void ConfigureDrawList(bool enabled, int maxLineSegments)
    {
        _drawList.Enabled = enabled;
        _drawList.MaxLineSegments = maxLineSegments;
    }

    internal void ObserveCompletedDdgiCounters(
        in DebugDdgiOverlayGpuCounters counters)
    {
        _completedDebugDdgiOverlayCounters = counters;
    }

    internal DebugDrawFrameSnapshot Build(
        Scene scene,
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager? manager,
        in DebugOverlayBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(sceneData);

        BuildDebugOverlayDrawCommands(scene, sceneData, manager, options);
        return _drawList.Snapshot();
    }

    internal void ClearFrame()
    {
        _drawList.ClearFrame();
    }

    private void BuildDebugOverlayDrawCommands(
        Scene scene,
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager? manager,
        in DebugOverlayBuildOptions options)
    {
        DebugOverlayMode requestedMode = sceneData.DebugOverlayMode;
        if (!sceneData.DebugToolingEnabled)
        {
            sceneData.DebugOverlayStatus = default;
            return;
        }

        if (!DebugOverlayCatalog.TryGet(requestedMode, out DebugOverlayDescriptor descriptor))
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Unavailable(
                requestedMode,
                $"unknown overlay value {(uint)requestedMode}");
            return;
        }

        if (!descriptor.IsActive)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Retired(
                requestedMode,
                descriptor.RetirementReason);
            return;
        }

        if (requestedMode == DebugOverlayMode.None)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Disabled();
            return;
        }

        long start = Stopwatch.GetTimestamp();
        DebugDrawDepthMode depthMode = ResolveOverlayDepthMode(options);
        sceneData.DebugOverlayDepthMode = depthMode;

        switch (requestedMode)
        {
            case DebugOverlayMode.LightTiles:
                PrepareLightTileOverlayStatus(sceneData);
                break;
            case DebugOverlayMode.DirectionalShadowCascades:
                DrawDirectionalShadowCascadeOverlay(sceneData, depthMode);
                break;
            case DebugOverlayMode.ObjectBounds:
                DrawObjectBoundsOverlay(sceneData, depthMode);
                break;
            case DebugOverlayMode.MeshletBounds:
                DrawMeshletBoundsOverlay(sceneData, depthMode);
                break;
            case DebugOverlayMode.SelectedObject:
                DrawSelectedObjectOverlay(sceneData, depthMode);
                break;
            case DebugOverlayMode.ReflectionProbeVolumes:
                DrawReflectionProbeOverlay(
                        scene,
                        sceneData,
                        depthMode,
                        options.SelectedReflectionProbeIndex);
                break;
            case DebugOverlayMode.DdgiProbeVolumes:
                DrawDdgiProbeVolumeOverlay(
                        scene,
                        sceneData,
                        depthMode,
                        manager,
                        options.SimpleDdgiEnabled);
                break;
            case DebugOverlayMode.DdgiProbeSpheres:
                DrawSimpleDdgiProbeVolumeOverlay(
                    sceneData,
                    depthMode,
                    manager,
                    options.SimpleDdgiEnabled,
                    faintBounds: true);
                PrepareDdgiProbeOverlay(
                        sceneData,
                        manager,
                        options.SimpleDdgiEnabled);
                break;
            case DebugOverlayMode.DdgiProbeActivity:
            case DebugOverlayMode.DdgiUpdatedProbes:
            case DebugOverlayMode.DdgiProbeRelocation:
            case DebugOverlayMode.DdgiProbeAge:
            case DebugOverlayMode.DdgiPhysicalSlots:
            case DebugOverlayMode.DdgiNewlyExposedCells:
            case DebugOverlayMode.DdgiFrustumPriority:
            case DebugOverlayMode.DdgiUpdateReasons:
                PrepareDdgiProbeOverlay(
                        sceneData,
                        manager,
                        options.SimpleDdgiEnabled);
                break;
            case DebugOverlayMode.DdgiCascadeBounds:
                DrawDdgiProbeVolumeOverlay(
                        scene,
                        sceneData,
                        depthMode,
                        manager,
                        options.SimpleDdgiEnabled);
                break;
            case DebugOverlayMode.DecalVolumes:
                DrawGeometryDecalOverlay(sceneData, depthMode);
                break;
            default:
                sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Unavailable(
                    requestedMode,
                    "catalog renderer has no registered handler");
                break;
        }

        sceneData.CpuDebugOverlayRecordMicroseconds = ElapsedMicroseconds(start);
    }

    private static void PrepareLightTileOverlayStatus(SceneRenderingData sceneData)
    {
        sceneData.DebugLightTileMaxCount = sceneData.MaxLightsInAnyTile;
        sceneData.DebugLightTileAverageCount = sceneData.AverageLightsPerNonEmptyTile;
        if (sceneData.LocalLightCount <= 0)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.LightTiles,
                "no local lights");
            return;
        }

        int tileCount = checked((int)Math.Min(
            int.MaxValue,
            (ulong)sceneData.TileCountX * sceneData.TileCountY));
        sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
            DebugOverlayMode.LightTiles,
            tileCount,
            sceneData.MaxLightsInAnyTile);
    }

    private void DrawDirectionalShadowCascadeOverlay(
        SceneRenderingData sceneData,
        DebugDrawDepthMode depthMode)
    {
        int cascadeCount = Math.Clamp(
            sceneData.DirectionalShadowCascadeCount,
            0,
            ShadowSettings.MaxDirectionalCascades);
        if (cascadeCount == 0 || sceneData.ShadowedDirectionalLightIndex < 0)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.DirectionalShadowCascades,
                "no active directional shadow cascades");
            return;
        }

        GPUShadowData shadow = sceneData.ShadowData;
        Span<Matrix4x4> matrices = stackalloc Matrix4x4[ShadowSettings.MaxDirectionalCascades]
        {
            shadow.LightViewProjection0,
            shadow.LightViewProjection1,
            shadow.LightViewProjection2,
            shadow.LightViewProjection3
        };
        Span<Vector4> colors = stackalloc Vector4[ShadowSettings.MaxDirectionalCascades]
        {
            new(0.9f, 0.15f, 0.1f, 0.95f),
            new(0.1f, 0.75f, 0.2f, 0.95f),
            new(0.1f, 0.35f, 0.95f, 0.95f),
            new(0.9f, 0.8f, 0.1f, 0.95f)
        };

        int drawn = 0;
        for (int cascade = 0; cascade < cascadeCount; cascade++)
        {
            if (!IsValidDebugFrustumMatrix(matrices[cascade]))
                continue;
            _drawList.Frustum(matrices[cascade], colors[cascade], depthMode);
            drawn++;
        }

        sceneData.DebugDirectionalShadowCascadesDrawn = drawn;
        int meshlets = 0;
        for (int cascade = 0;
            cascade < cascadeCount && cascade < sceneData.DirectionalShadowMeshletCounts.Length;
            cascade++)
        {
            meshlets = (int)Math.Min(
                int.MaxValue,
                (long)meshlets + Math.Max(0, sceneData.DirectionalShadowMeshletCounts[cascade]));
        }
        sceneData.DebugOverlayStatus = drawn > 0
            ? DebugOverlayFrameStatus.Rendered(
                DebugOverlayMode.DirectionalShadowCascades,
                drawn,
                meshlets,
                _drawList.DroppedLineCount)
            : DebugOverlayFrameStatus.Unavailable(
                DebugOverlayMode.DirectionalShadowCascades,
                "active cascade matrices are invalid");
    }

    internal static bool IsValidDebugFrustumMatrix(Matrix4x4 matrix)
    {
        if (matrix.Equals(Matrix4x4.Identity) || matrix.Equals(Matrix4x4.Zero))
            return false;
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                if (!float.IsFinite(matrix[row, column]))
                    return false;
            }
        }
        return float.IsFinite(matrix.Determinant()) &&
            MathF.Abs(matrix.Determinant()) > 1e-12f;
    }

    private static DebugDrawDepthMode ResolveOverlayDepthMode(
        in DebugOverlayBuildOptions options)
    {
        if (options.ShowXRayVolumes)
            return DebugDrawDepthMode.XRay;
        return options.ShowDepthTestedVolumes
            ? DebugDrawDepthMode.DepthTested
            : DebugDrawDepthMode.AlwaysVisible;
    }

    private void DrawObjectBoundsOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
    {
        foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
        {
            Vector4 color = snapshot.Visible
                ? new Vector4(0.15f, 0.9f, 0.35f, 1.0f)
                : new Vector4(1.0f, 0.35f, 0.1f, 1.0f);
            _drawList.Box(snapshot.WorldBounds, color, depthMode);
            sceneData.DebugObjectBoundsDrawn++;
        }

        sceneData.DebugOverlayStatus = sceneData.DebugObjectBoundsDrawn > 0
            ? DebugOverlayFrameStatus.Rendered(
                DebugOverlayMode.ObjectBounds,
                sceneData.DebugObjectBoundsDrawn,
                droppedItemCount: _drawList.DroppedLineCount)
            : DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.ObjectBounds,
                sceneData.CpuDebugSnapshotsEnabled
                    ? "scene has 0 object snapshots"
                    : "CPU object snapshots unavailable");
    }

    private void DrawSelectedObjectOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
    {
        int index = sceneData.DebugSelectedObjectIndex;
        if (index < 0 || index >= sceneData.ObjectDebugSnapshots.Count)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.SelectedObject,
                "select with Ctrl+Left/Right");
            return;
        }

        ObjectDebugSnapshot snapshot = sceneData.ObjectDebugSnapshots[index];
        _drawList.Box(snapshot.WorldBounds, new Vector4(1.0f, 0.85f, 0.1f, 1.0f), depthMode);
        sceneData.DebugObjectBoundsDrawn = 1;
        sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
            DebugOverlayMode.SelectedObject,
            1,
            droppedItemCount: _drawList.DroppedLineCount);
    }

    private void DrawReflectionProbeOverlay(
        Scene scene,
        SceneRenderingData sceneData,
        DebugDrawDepthMode depthMode,
        int selectedProbe)
    {
        IReadOnlyList<ReflectionProbe> probes = scene.ReflectionProbes;
        for (int i = 0; i < probes.Count; i++)
        {
            if (selectedProbe >= 0 && i != selectedProbe)
                continue;

            ReflectionProbe probe = probes[i];
            Vector4 color = i == selectedProbe
                ? new Vector4(0.1f, 0.85f, 1.0f, 1.0f)
                : new Vector4(0.2f, 0.55f, 1.0f, 0.85f);
            if (probe.Shape == ReflectionProbeShape.Sphere)
            {
                _drawList.Sphere(probe.Position, probe.Radius, color, segments: 32, depthMode);
                float innerRadius = MathF.Max(0.0f, probe.Radius - probe.BlendDistance);
                if (probe.BlendDistance > 0.0f && innerRadius > 0.0f)
                {
                    Vector4 blendColor = color;
                    blendColor.W = MathF.Min(blendColor.W, 0.32f);
                    _drawList.Sphere(
                        probe.Position,
                        innerRadius,
                        blendColor,
                        segments: 24,
                        depthMode);
                }
            }
            else
            {
                Matrix4x4 transform = probe.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(probe.Position);
                _drawList.OrientedBox(transform, probe.BoxExtents, color, depthMode);
                Vector3 blendExtents = new(
                    MathF.Max(0.0f, probe.BoxExtents.X - probe.BlendDistance),
                    MathF.Max(0.0f, probe.BoxExtents.Y - probe.BlendDistance),
                    MathF.Max(0.0f, probe.BoxExtents.Z - probe.BlendDistance));
                if (probe.BlendDistance > 0.0f &&
                    blendExtents.X > 0.0f && blendExtents.Y > 0.0f && blendExtents.Z > 0.0f)
                {
                    Vector4 blendColor = color;
                    blendColor.W = MathF.Min(blendColor.W, 0.32f);
                    _drawList.OrientedBox(transform, blendExtents, blendColor, depthMode);
                }
            }

            sceneData.DebugReflectionProbeVolumesDrawn++;
        }

        sceneData.DebugOverlayStatus = sceneData.DebugReflectionProbeVolumesDrawn > 0
            ? DebugOverlayFrameStatus.Rendered(
                DebugOverlayMode.ReflectionProbeVolumes,
                sceneData.DebugReflectionProbeVolumesDrawn,
                droppedItemCount: _drawList.DroppedLineCount)
            : DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.ReflectionProbeVolumes,
                probes.Count == 0
                    ? "scene has 0 reflection probes"
                    : "selected reflection probe is unavailable");
    }

    private void DrawDdgiProbeVolumeOverlay(
        Scene scene,
        SceneRenderingData sceneData,
        DebugDrawDepthMode depthMode,
        SimpleDdgiVolumeManager? manager,
        bool simpleDdgiEnabled)
    {
        _ = scene;
        DrawSimpleDdgiProbeVolumeOverlay(
            sceneData,
            depthMode,
            manager,
            simpleDdgiEnabled);
        if (sceneData.DebugDdgiProbeVolumesDrawn > 0)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
                sceneData.DebugOverlayMode,
                sceneData.DebugDdgiProbeVolumesDrawn,
                sceneData.DebugDdgiProbeMarkersDrawn,
                _drawList.DroppedLineCount);
        }
        else if (!simpleDdgiEnabled)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Unavailable(
                sceneData.DebugOverlayMode,
                "Simple DDGI is disabled");
        }
        else
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                sceneData.DebugOverlayMode,
                "DDGI has 0 admitted volumes");
        }
    }

    private void PrepareDdgiProbeOverlay(
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager? manager,
        bool simpleDdgiEnabled)
    {
        if (!simpleDdgiEnabled)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Unavailable(
                sceneData.DebugOverlayMode,
                "Simple DDGI is disabled");
            return;
        }
        if (manager == null ||
            manager.VolumeCount <= 0 ||
            manager.ProbeCount <= 0)
        {
            sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                sceneData.DebugOverlayMode,
                "DDGI has 0 admitted probes");
            return;
        }

        PrepareDdgiProbeDebugInstances(sceneData, manager);
    }

    private void PrepareDdgiProbeDebugInstances(
        SceneRenderingData sceneData,
        SimpleDdgiVolumeManager manager)
    {
        sceneData.DebugDdgiVolumeTableGeneration = manager.VolumeTableGeneration;
        sceneData.DebugDdgiSchedulerGeneration = manager.GpuScheduler.ResourceGeneration;
        sceneData.DebugDdgiResidencyGeneration = manager.ProbeResidencyResourceGeneration;

        SimpleDdgiGpuSchedulerLayout? schedulerLayout = manager.GpuScheduler.Layout;
        if (schedulerLayout != null)
        {
            sceneData.DebugDdgiSchedulerFrameOffsetWords = schedulerLayout.Frame.OffsetWords;
            sceneData.DebugDdgiSchedulerProbeStateOffsetWords = schedulerLayout.ProbeState.OffsetWords;
            sceneData.DebugDdgiSchedulerCountersOffsetWords = schedulerLayout.Counters.OffsetWords;
            sceneData.DebugDdgiSchedulerUpdateRecordsOffsetWords =
                schedulerLayout.UpdateRecords.OffsetWords;
        }

        bool updateRecordsMode = sceneData.DebugOverlayMode is
            DebugOverlayMode.DdgiUpdatedProbes or
            DebugOverlayMode.DdgiUpdateReasons;
        if (updateRecordsMode)
        {
            int capacity = manager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident
                ? schedulerLayout?.RequestCapacity ?? 0
                : manager.ProbesToUpdate;
            int boundedCapacity = Math.Clamp(
                capacity,
                0,
                MaxDetailedProbeMarkersPerFrame);
            sceneData.DebugDdgiUpdateRecordCapacity = boundedCapacity;
            if (capacity <= 0)
            {
                sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                    sceneData.DebugOverlayMode,
                    "no probes admitted for update");
                return;
            }

            int reportedUpdates = manager.SchedulerMode == SimpleDdgiSchedulerMode.GpuResident &&
                manager.GpuSchedulerFeedbackValid
                    ? checked((int)Math.Min(
                        (uint)int.MaxValue,
                        manager.LastGpuSchedulerFeedback.AcceptedCount))
                    : Math.Max(0, manager.ProbesToUpdate);
            sceneData.DebugDdgiRequestedSamples = capacity;
            sceneData.DebugDdgiProbeMarkersDrawn =
                Math.Min(reportedUpdates, boundedCapacity);
            sceneData.DebugDdgiProbeMarkersDropped =
                Math.Max(0, reportedUpdates - boundedCapacity);
            bool completedCounters =
                TryApplyCompletedDebugDdgiOverlayCounters(sceneData);
            if (sceneData.DebugDdgiProbeMarkersDrawn > 0)
            {
                sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.Rendered(
                    sceneData.DebugOverlayMode,
                    sceneData.DebugDdgiProbeMarkersDrawn,
                    capacity,
                    sceneData.DebugDdgiProbeMarkersDropped);
            }
            else
            {
                sceneData.DebugOverlayStatus = DebugOverlayFrameStatus.NoData(
                    sceneData.DebugOverlayMode,
                    completedCounters && sceneData.DebugDdgiInvalidTransactions > 0
                        ? "all admitted records failed identity validation"
                        : "no probes admitted for update");
            }
            return;
        }

        _ddgiProbeDebugInstanceScratch ??=
            new GPUDdgiProbeDebugInstance[MaxDetailedProbeMarkersPerFrame];
        ReadOnlySpan<GPUSimpleDdgiVolume> volumes = manager.LastVolumes;
        int remainingMarkers = MaxDetailedProbeMarkersPerFrame;
        int remainingVolumes = volumes.Length;
        int instanceCount = 0;
        long logicalProbeCount = 0;
        for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
        {
            GPUSimpleDdgiVolume volume = volumes[volumeIndex];
            int countX = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.X));
            int countY = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Y));
            int countZ = Math.Max(1, (int)MathF.Round(volume.GridCountsAndFirstProbe.Z));
            int firstProbe = Math.Max(0, (int)MathF.Round(volume.GridCountsAndFirstProbe.W));
            float spacing = Math.Max(volume.OriginAndSpacing.W, 0.001f);
            Vector3 origin = new(
                volume.OriginAndSpacing.X,
                volume.OriginAndSpacing.Y,
                volume.OriginAndSpacing.Z);
            logicalProbeCount = Math.Min(
                int.MaxValue,
                logicalProbeCount + (long)countX * countY * countZ);

            int volumeBudget = CalculateDdgiProbeMarkerBudget(
                remainingMarkers,
                remainingVolumes);
            DdgiProbeMarkerSampling sampling = CalculateDdgiProbeMarkerSampling(
                countX,
                countY,
                countZ,
                volumeBudget);
            int volumeDrawn = 0;
            for (int z = 0;
                z < countZ && volumeDrawn < volumeBudget;
                z += Math.Max(1, sampling.StepZ))
                for (int y = 0;
                    y < countY && volumeDrawn < volumeBudget;
                    y += Math.Max(1, sampling.StepY))
                    for (int x = 0;
                        x < countX && volumeDrawn < volumeBudget;
                        x += Math.Max(1, sampling.StepX))
                    {
                        int physicalLocal = SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex(
                            volume,
                            x,
                            y,
                            z);
                        int virtualProbe = checked(firstProbe + physicalLocal);
                        Vector3 logicalPosition = origin + new Vector3(
                            spacing * x,
                            spacing * y,
                            spacing * z);
                        _ddgiProbeDebugInstanceScratch[instanceCount++] =
                            CreateDdgiProbeDebugInstance(
                                sceneData.DdgiFrameSerial,
                                manager.VolumeTableGeneration,
                                manager.GpuScheduler.ResourceGeneration,
                                manager.ProbeResidencyResourceGeneration,
                                volumeIndex,
                                x,
                                y,
                                z,
                                virtualProbe,
                                logicalPosition,
                                spacing,
                                manager.GetDebugSchedulerPriorityFlags(virtualProbe));
                        volumeDrawn++;
                    }

            remainingMarkers -= volumeDrawn;
            remainingVolumes = Math.Max(0, remainingVolumes - 1);
        }

        sceneData.DebugDdgiProbeInstances = _ddgiProbeDebugInstanceScratch;
        sceneData.DebugDdgiProbeInstanceCount = instanceCount;
        sceneData.DebugDdgiRequestedSamples = (int)logicalProbeCount;
        sceneData.DebugDdgiProbeMarkersDrawn = instanceCount;
        sceneData.DebugDdgiProbeMarkersDropped = Math.Max(
            0,
            (int)logicalProbeCount - instanceCount);
        if (sceneData.DebugOverlayMode == DebugOverlayMode.DdgiProbeSpheres)
            sceneData.DebugDdgiSphereLineSegments = checked(instanceCount * 8 * 3);

        _ = TryApplyCompletedDebugDdgiOverlayCounters(sceneData);
        if (sceneData.DebugOverlayMode == DebugOverlayMode.DdgiProbeSpheres &&
            sceneData.DebugDdgiGpuCountersValid)
        {
            sceneData.DebugDdgiSphereLineSegments = checked(
                sceneData.DebugDdgiProbeMarkersDrawn * 8 * 3);
        }

        sceneData.DebugOverlayStatus = instanceCount > 0
            ? DebugOverlayFrameStatus.Rendered(
                sceneData.DebugOverlayMode,
                sceneData.DebugDdgiProbeMarkersDrawn,
                droppedItemCount: sceneData.DebugDdgiProbeMarkersDropped)
            : DebugOverlayFrameStatus.NoData(
                sceneData.DebugOverlayMode,
                "DDGI sampling produced 0 probes");
    }

    private bool TryApplyCompletedDebugDdgiOverlayCounters(
        SceneRenderingData sceneData)
    {
        DebugDdgiOverlayGpuCounters counters =
            _completedDebugDdgiOverlayCounters;
        if (!counters.Valid ||
            counters.Mode != sceneData.DebugOverlayMode ||
            counters.VolumeTableGeneration !=
                sceneData.DebugDdgiVolumeTableGeneration ||
            counters.SchedulerResourceGeneration !=
                sceneData.DebugDdgiSchedulerGeneration ||
            counters.ResidencyResourceGeneration !=
                sceneData.DebugDdgiResidencyGeneration)
        {
            return false;
        }

        sceneData.DebugDdgiGpuCountersValid = true;
        sceneData.DebugDdgiProbeMarkersDrawn = ClampDebugCounter(
            counters.DrawnMarkerCount);
        sceneData.DebugDdgiProbeMarkersFiltered = ClampDebugCounter(
            counters.FilteredMarkerCount);
        sceneData.DebugDdgiNonresidentMarkers = ClampDebugCounter(
            counters.NonresidentMarkerCount);
        sceneData.DebugDdgiStaleMappings = ClampDebugCounter(
            counters.StaleMappingCount);
        sceneData.DebugDdgiStateUnavailableMarkers = ClampDebugCounter(
            counters.StateUnavailableMarkerCount);
        sceneData.DebugDdgiInvalidTransactions = ClampDebugCounter(
            counters.InvalidTransactionCount);
        sceneData.DebugDdgiUpdateReasonCounts = counters.UpdateReasons;
        return true;
    }

    private static int ClampDebugCounter(uint value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    internal static GPUDdgiProbeDebugInstance CreateDdgiProbeDebugInstance(
        ulong frameSerial,
        uint volumeTableGeneration,
        uint schedulerResourceGeneration,
        uint residencyResourceGeneration,
        int volumeIndex,
        int logicalX,
        int logicalY,
        int logicalZ,
        int virtualProbeIndex,
        Vector3 logicalPosition,
        float spacing,
        uint schedulerPriorityFlags = 0u)
    {
        return new GPUDdgiProbeDebugInstance
        {
            LogicalPositionAndRadius = new Vector4(
                logicalPosition.X,
                logicalPosition.Y,
                logicalPosition.Z,
                Math.Clamp(spacing * 0.08f, 0.04f, 0.20f)),
            VolumeIndex = checked((uint)volumeIndex),
            LogicalX = checked((uint)logicalX),
            LogicalY = checked((uint)logicalY),
            LogicalZ = checked((uint)logicalZ),
            VirtualProbeIndex = checked((uint)virtualProbeIndex),
            SnapshotFrameSerialLow = unchecked((uint)frameSerial),
            SnapshotFrameSerialHigh = unchecked((uint)(frameSerial >> 32)),
            VolumeTableGeneration = volumeTableGeneration,
            SchedulerResourceGeneration = schedulerResourceGeneration,
            ResidencyResourceGeneration = residencyResourceGeneration,
            Flags = schedulerPriorityFlags
        };
    }

    private void DrawSimpleDdgiProbeVolumeOverlay(
        SceneRenderingData sceneData,
        DebugDrawDepthMode depthMode,
        SimpleDdgiVolumeManager? manager,
        bool simpleDdgiEnabled,
        bool faintBounds = false)
    {
        if (!simpleDdgiEnabled ||
            manager == null ||
            manager.ProbeCount <= 0)
        {
            return;
        }

        ReadOnlySpan<GPUSimpleDdgiVolume> volumes = manager.LastVolumes;
        for (int volumeIndex = 0; volumeIndex < volumes.Length; volumeIndex++)
        {
            GPUSimpleDdgiVolume volume = volumes[volumeIndex];
            Vector3 worldMin = new(
                volume.WorldMinAndEdgeFade.X,
                volume.WorldMinAndEdgeFade.Y,
                volume.WorldMinAndEdgeFade.Z);
            Vector3 worldMax = new(
                volume.WorldMaxAndKind.X,
                volume.WorldMaxAndKind.Y,
                volume.WorldMaxAndKind.Z);

            Vector4 volumeColor = ResolveSimpleDdgiVolumeDebugColor(volumeIndex, volume);
            if (faintBounds)
                volumeColor.W = MathF.Min(volumeColor.W, 0.25f);
            _drawList.Box(
                new BoundingBox(worldMin, worldMax),
                volumeColor,
                depthMode);
            sceneData.DebugDdgiProbeVolumesDrawn++;
        }
    }

    private static Vector4 ResolveSimpleDdgiVolumeDebugColor(int volumeIndex, GPUSimpleDdgiVolume volume)
    {
        // Authored volumes sort before same-spacing rings. Give them a distinct
        // colour so Ctrl+9 makes the overlap and camera-relative coverage clear.
        int kind = (int)MathF.Round(volume.WorldMaxAndKind.W);
        if (kind == 1)
            return new Vector4(0.95f, 0.9f, 0.25f, 0.95f);
        if (kind == 3)
            return new Vector4(1.0f, 0.48f, 0.08f, 0.98f);

        return (volumeIndex % 3) switch
        {
            0 => new Vector4(0.2f, 0.75f, 1.0f, 0.9f),
            1 => new Vector4(0.3f, 0.95f, 0.55f, 0.9f),
            _ => new Vector4(0.95f, 0.3f, 0.85f, 0.9f)
        };
    }

    internal readonly record struct DdgiProbeMarkerSampling(int StepX, int StepY, int StepZ);

    internal static int CalculateDdgiProbeMarkerBudget(int remainingMarkers, int remainingVolumes)
    {
        if (remainingMarkers <= 0 || remainingVolumes <= 0)
            return 0;

        // Divide the still-available budget among every volume that has not
        // been visited yet. Any markers a sparse/filtering volume does not use
        // remain available and are redistributed by the next iteration.
        return Math.Max(1, (remainingMarkers + remainingVolumes - 1) / remainingVolumes);
    }

    internal static DdgiProbeMarkerSampling CalculateDdgiProbeMarkerSampling(
        int probeCountX,
        int probeCountY,
        int probeCountZ,
        int maxMarkers)
    {
        int safeCountX = Math.Max(1, probeCountX);
        int safeCountY = Math.Max(1, probeCountY);
        int safeCountZ = Math.Max(1, probeCountZ);
        int safeMaxMarkers = Math.Max(1, maxMarkers);
        int stepX = 1;
        int stepY = 1;
        int stepZ = 1;

        while (SampledAxisCount(safeCountX, stepX) *
            SampledAxisCount(safeCountY, stepY) *
            SampledAxisCount(safeCountZ, stepZ) > safeMaxMarkers)
        {
            int sampledX = SampledAxisCount(safeCountX, stepX);
            int sampledY = SampledAxisCount(safeCountY, stepY);
            int sampledZ = SampledAxisCount(safeCountZ, stepZ);
            if (sampledX >= sampledY && sampledX >= sampledZ)
                stepX++;
            else if (sampledZ >= sampledX && sampledZ >= sampledY)
                stepZ++;
            else
                stepY++;
        }

        return new DdgiProbeMarkerSampling(stepX, stepY, stepZ);
    }

    internal static bool ShouldDrawDdgiProbeMarker(int x, int y, int z, DdgiProbeMarkerSampling sampling)
    {
        return x >= 0 &&
            y >= 0 &&
            z >= 0 &&
            x % Math.Max(1, sampling.StepX) == 0 &&
            y % Math.Max(1, sampling.StepY) == 0 &&
            z % Math.Max(1, sampling.StepZ) == 0;
    }

    private static int SampledAxisCount(int count, int step)
    {
        return (Math.Max(1, count) + Math.Max(1, step) - 1) / Math.Max(1, step);
    }

    private static int SaturatingAdd(int left, int right) =>
        (int)Math.Min(
            int.MaxValue,
            (long)Math.Max(0, left) + Math.Max(0, right));

    private void DrawGeometryDecalOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
    {
        foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
        {
            if (!_resources.TryGetMaterialMetadata(
                    snapshot.Material,
                    out MaterialRenderMetadata metadata))
            {
                continue;
            }

            if (!metadata.IsGeometryDecal)
                continue;

            _drawList.Box(snapshot.WorldBounds, new Vector4(1.0f, 0.25f, 0.9f, 1.0f), depthMode);
            sceneData.DebugDecalVolumesDrawn++;
        }

        sceneData.DebugOverlayStatus = sceneData.DebugDecalVolumesDrawn > 0
            ? DebugOverlayFrameStatus.Rendered(
                DebugOverlayMode.DecalVolumes,
                sceneData.DebugDecalVolumesDrawn,
                droppedItemCount: _drawList.DroppedLineCount)
            : DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.DecalVolumes,
                sceneData.CpuDebugSnapshotsEnabled
                    ? "scene has 0 geometry decals"
                    : "CPU object snapshots unavailable");
    }

    private void DrawMeshletBoundsOverlay(SceneRenderingData sceneData, DebugDrawDepthMode depthMode)
    {
        const int SphereSegments = 8;
        const int LinesPerSphere = SphereSegments * 3;
        const int MaxMeshletBoundsPerFrame = 2_048;
        Vector4 color = new(0.1f, 0.75f, 1.0f, 0.9f);
        int lineBudget = Math.Max(0, _drawList.MaxLineSegments);
        int usedLines = _drawList.Snapshot().LineCount;

        foreach (ObjectDebugSnapshot snapshot in sceneData.ObjectDebugSnapshots)
        {
            if (!snapshot.Visible)
                continue;

            if (!_resources.TryGetMeshInfo(snapshot.Mesh, out MeshInfo meshInfo))
                continue;

            uint meshletOffset = meshInfo.MeshletCount > 0
                ? meshInfo.MeshletOffset
                : meshInfo.MeshletLodGeneratedCount > 0
                    ? meshInfo.MeshletOffset
                    : 0u;
            uint meshletCount = meshInfo.MeshletCount > 0
                ? meshInfo.MeshletCount
                : meshInfo.MeshletLodGeneratedCount;
            if (meshletCount == 0)
                continue;

            float radiusScale = GetMaxAbsScale(snapshot.WorldMatrix);
            ulong end = (ulong)meshletOffset + meshletCount;
            for (ulong meshletIndex = meshletOffset; meshletIndex < end; meshletIndex++)
            {
                if (sceneData.DebugMeshletBoundsDrawn >= MaxMeshletBoundsPerFrame)
                {
                    ulong remaining = end - meshletIndex;
                    int dropped = remaining > int.MaxValue
                        ? int.MaxValue
                        : (int)remaining;
                    sceneData.DebugMeshletBoundsItemCapDropped = SaturatingAdd(
                        sceneData.DebugMeshletBoundsItemCapDropped,
                        dropped);
                    sceneData.DebugMeshletBoundsDropped = SaturatingAdd(
                        sceneData.DebugMeshletBoundsDropped,
                        dropped);
                    break;
                }
                if (usedLines + LinesPerSphere > lineBudget)
                {
                    ulong remaining = end - meshletIndex;
                    int dropped = remaining > int.MaxValue
                        ? int.MaxValue
                        : (int)remaining;
                    sceneData.DebugMeshletBoundsLineBudgetDropped = SaturatingAdd(
                        sceneData.DebugMeshletBoundsLineBudgetDropped,
                        dropped);
                    sceneData.DebugMeshletBoundsDropped = SaturatingAdd(
                        sceneData.DebugMeshletBoundsDropped,
                        dropped);
                    break;
                }

                if (!_resources.TryGetMeshlet(
                        (uint)meshletIndex,
                        out Njulf.Core.Geometry.Meshlet meshlet))
                {
                    sceneData.DebugMeshletBoundsDropped++;
                    continue;
                }

                Vector3 center = SceneDataBuilder.TransformPoint(meshlet.BoundingSphereCenter, snapshot.WorldMatrix);
                float radius = meshlet.BoundingSphereRadius * radiusScale;
                if (radius <= 0.0f || float.IsNaN(radius) || float.IsInfinity(radius))
                {
                    sceneData.DebugMeshletBoundsDropped++;
                    continue;
                }

                _drawList.Sphere(center, radius, color, SphereSegments, depthMode);
                usedLines += LinesPerSphere;
                sceneData.DebugMeshletBoundsDrawn++;
            }
        }

        sceneData.DebugOverlayStatus = sceneData.DebugMeshletBoundsDrawn > 0
            ? DebugOverlayFrameStatus.Rendered(
                DebugOverlayMode.MeshletBounds,
                sceneData.DebugMeshletBoundsDrawn,
                secondaryItemCount: MaxMeshletBoundsPerFrame,
                droppedItemCount: SaturatingAdd(
                    sceneData.DebugMeshletBoundsDropped,
                    _drawList.DroppedLineCount))
            : DebugOverlayFrameStatus.NoData(
                DebugOverlayMode.MeshletBounds,
                sceneData.CpuDebugSnapshotsEnabled
                    ? "scene has 0 visible meshlets"
                    : "CPU object snapshots unavailable");
    }

    private static float GetMaxAbsScale(Matrix4x4 matrix)
    {
        Vector3 scale = matrix.Scale;
        return MathF.Max(MathF.Abs(scale.X), MathF.Max(MathF.Abs(scale.Y), MathF.Abs(scale.Z)));
    }
    private static long ElapsedMicroseconds(long startTimestamp)
    {
        return Stopwatch.GetElapsedTime(startTimestamp).Ticks /
            (TimeSpan.TicksPerMillisecond / 1000);
    }
}

using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public sealed class DdgiFrameLayout
    {
        public static readonly DdgiFrameLayout Empty = new(
            Array.Empty<GlobalIlluminationProbeVolume>(),
            Array.Empty<DdgiProbeVolumeRuntimeMetadata>(),
            Array.Empty<BoundingBox>(),
            Array.Empty<DdgiDirtyRegion>(),
            Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
            isDdgiActive: false,
            cameraRelativeEnabled: false,
            defaultVolumeIncluded: false,
            authoredVolumeCount: 0,
            cameraRelativeCascadeCount: 0,
            authoredProbeCount: 0,
            cameraRelativeProbeCount: 0,
            totalPhysicalProbeCount: 0,
            localSlotCount: 0,
            localSlotProbeCapacity: 0,
            activeLocalSlotCount: 0,
            localSlotGeneration: 0,
            localPoolProbeCount: 0,
            localAllocationSignature: 0UL,
            localVolumeEvictionReason: "none");

        internal DdgiFrameLayout(
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes,
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> volumeMetadata,
            IReadOnlyList<BoundingBox> dirtyBounds,
            IReadOnlyList<DdgiDirtyRegion> dirtyRegions,
            IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> dirtyProbeRequests,
            bool isDdgiActive,
            bool cameraRelativeEnabled,
            bool defaultVolumeIncluded,
            int authoredVolumeCount,
            int cameraRelativeCascadeCount,
            int authoredProbeCount,
            int cameraRelativeProbeCount,
            int totalPhysicalProbeCount,
            DdgiViewPriorityContext viewPriority = default,
            IReadOnlyList<DdgiClipmapCascadeState>? cameraRelativeCascades = null,
            DdgiCameraMovementClass movementClass = DdgiCameraMovementClass.None,
            bool fastCameraMovement = false,
            bool viewPriorityHistoryReset = false,
            int localSlotCount = 0,
            int localSlotProbeCapacity = 0,
            int activeLocalSlotCount = 0,
            int localSlotGeneration = 0,
            int localPoolProbeCount = 0,
            ulong localAllocationSignature = 0UL,
            string localVolumeEvictionReason = "none")
        {
            Volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
            VolumeMetadata = volumeMetadata ?? throw new ArgumentNullException(nameof(volumeMetadata));
            DirtyBounds = dirtyBounds ?? throw new ArgumentNullException(nameof(dirtyBounds));
            DirtyRegions = dirtyRegions ?? throw new ArgumentNullException(nameof(dirtyRegions));
            DirtyProbeRequests = dirtyProbeRequests ?? throw new ArgumentNullException(nameof(dirtyProbeRequests));
            CameraRelativeCascades = cameraRelativeCascades ?? Array.Empty<DdgiClipmapCascadeState>();
            if (VolumeMetadata.Count != Volumes.Count)
                throw new ArgumentException("DDGI volume metadata count must match volume count.", nameof(volumeMetadata));

            IsDdgiActive = isDdgiActive;
            CameraRelativeEnabled = cameraRelativeEnabled;
            DefaultVolumeIncluded = defaultVolumeIncluded;
            AuthoredVolumeCount = authoredVolumeCount;
            CameraRelativeCascadeCount = cameraRelativeCascadeCount;
            AuthoredProbeCount = authoredProbeCount;
            CameraRelativeProbeCount = cameraRelativeProbeCount;
            TotalPhysicalProbeCount = totalPhysicalProbeCount;
            LocalSlotCount = localSlotCount;
            LocalSlotProbeCapacity = localSlotProbeCapacity;
            ActiveLocalSlotCount = activeLocalSlotCount;
            LocalSlotGeneration = localSlotGeneration;
            LocalPoolProbeCount = localPoolProbeCount;
            LocalAllocationSignature = localAllocationSignature;
            LocalVolumeEvictionReason = localVolumeEvictionReason ?? "none";
            ViewPriority = viewPriority;
            MovementClass = movementClass;
            FastCameraMovement = fastCameraMovement;
            ViewPriorityHistoryReset = viewPriorityHistoryReset;
        }

        public IReadOnlyList<GlobalIlluminationProbeVolume> Volumes { get; }
        public IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> VolumeMetadata { get; }
        public IReadOnlyList<BoundingBox> DirtyBounds { get; }
        public IReadOnlyList<DdgiDirtyRegion> DirtyRegions { get; }
        public IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> DirtyProbeRequests { get; }
        public IReadOnlyList<DdgiClipmapCascadeState> CameraRelativeCascades { get; }
        public DdgiViewPriorityContext ViewPriority { get; }
        public DdgiCameraMovementClass MovementClass { get; }
        public bool FastCameraMovement { get; }
        public bool ViewPriorityHistoryReset { get; }
        public bool IsDdgiActive { get; }
        public bool CameraRelativeEnabled { get; }
        public bool DefaultVolumeIncluded { get; }
        public int AuthoredVolumeCount { get; }
        public int CameraRelativeCascadeCount { get; }
        public int AuthoredProbeCount { get; }
        public int CameraRelativeProbeCount { get; }
        public int TotalPhysicalProbeCount { get; }
        public int LocalSlotCount { get; }
        public int LocalSlotProbeCapacity { get; }
        public int ActiveLocalSlotCount { get; }
        public int LocalSlotGeneration { get; }
        public int LocalPoolProbeCount { get; }
        public ulong LocalAllocationSignature { get; }
        public string LocalVolumeEvictionReason { get; }

        public DdgiFrameLayout WithDirtyBounds(IReadOnlyList<BoundingBox> dirtyBounds)
        {
            if (dirtyBounds == null)
                throw new ArgumentNullException(nameof(dirtyBounds));

            var dirtyRegions = new DdgiDirtyRegion[dirtyBounds.Count];
            for (int i = 0; i < dirtyBounds.Count; i++)
                dirtyRegions[i] = new DdgiDirtyRegion(dirtyBounds[i], DdgiDirtyReason.Unknown);

            return WithDirtyRegions(dirtyRegions);
        }

        public DdgiFrameLayout WithDirtyRegions(IReadOnlyList<DdgiDirtyRegion> dirtyRegions)
        {
            if (dirtyRegions == null)
                throw new ArgumentNullException(nameof(dirtyRegions));

            var regionCopy = new DdgiDirtyRegion[dirtyRegions.Count];
            var boundsCopy = new BoundingBox[dirtyRegions.Count];
            for (int i = 0; i < dirtyRegions.Count; i++)
            {
                regionCopy[i] = dirtyRegions[i];
                boundsCopy[i] = dirtyRegions[i].Bounds;
            }

            IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> dirtyProbeRequests = DirtyProbeRequests;
            if (CameraRelativeEnabled && regionCopy.Length > 0)
                dirtyProbeRequests = MergeDirtyProbeRequests(DirtyProbeRequests, BuildCameraRelativeDirtyRegionProbeRequests(regionCopy));

            return new DdgiFrameLayout(
                Volumes,
                VolumeMetadata,
                boundsCopy,
                regionCopy,
                dirtyProbeRequests,
                IsDdgiActive,
                CameraRelativeEnabled,
                DefaultVolumeIncluded,
                AuthoredVolumeCount,
                CameraRelativeCascadeCount,
                AuthoredProbeCount,
                CameraRelativeProbeCount,
                TotalPhysicalProbeCount,
                ViewPriority,
                CameraRelativeCascades,
                MovementClass,
                FastCameraMovement,
                ViewPriorityHistoryReset,
                LocalSlotCount,
                LocalSlotProbeCapacity,
                ActiveLocalSlotCount,
                LocalSlotGeneration,
                LocalPoolProbeCount,
                LocalAllocationSignature,
                LocalVolumeEvictionReason);
        }

        private IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> BuildCameraRelativeDirtyRegionProbeRequests(
            IReadOnlyList<DdgiDirtyRegion> dirtyRegions)
        {
            if (dirtyRegions.Count == 0 || VolumeMetadata.Count == 0)
                return Array.Empty<DdgiFrameLayoutDirtyProbeRequest>();

            var scored = new List<ScoredDirtyProbeRequest>();
            for (int volumeIndex = 0; volumeIndex < VolumeMetadata.Count; volumeIndex++)
            {
                DdgiProbeVolumeRuntimeMetadata metadata = VolumeMetadata[volumeIndex];
                if (metadata.Kind != DdgiProbeVolumeKind.CameraClipmap)
                    continue;
                if (volumeIndex >= Volumes.Count)
                    continue;

                GlobalIlluminationProbeVolume volume = Volumes[volumeIndex];
                Vector3 spacing = volume.ProbeSpacing;
                float spacingScale = MathF.Max(MathF.Max(MathF.Abs(spacing.X), MathF.Abs(spacing.Y)), MathF.Abs(spacing.Z));
                if (!float.IsFinite(spacingScale) || spacingScale <= 0.0f)
                    spacingScale = 1.0f;

                float influencePadding = CalculateDirtyBoundsPadding(volume, spacingScale);
                DdgiClipmapCell gridMin = new(metadata.LogicalGridMinX, metadata.LogicalGridMinY, metadata.LogicalGridMinZ);
                int physicalFirstProbeIndex = FindCascadePhysicalFirstProbeIndex(metadata.CascadeIndex);
                if (physicalFirstProbeIndex < 0)
                    continue;

                for (int dirtyIndex = 0; dirtyIndex < dirtyRegions.Count; dirtyIndex++)
                {
                    DdgiDirtyRegion dirtyRegion = dirtyRegions[dirtyIndex];
                    BoundingBox expanded = Expand(dirtyRegion.Bounds, influencePadding);
                    if (!IntersectsVolume(volume, expanded))
                        continue;

                    DdgiClipmapCell min = ClampWorldPositionToClipmapCell(expanded.Min, spacing, gridMin, volume);
                    DdgiClipmapCell max = ClampWorldPositionToClipmapCell(expanded.Max, spacing, gridMin, volume);
                    DdgiClipmapCell requestMin = Min(min, max);
                    DdgiClipmapCell requestMax = Max(min, max);
                    var request = new DdgiFrameLayoutDirtyProbeRequest(
                        volumeIndex,
                        metadata.CascadeIndex,
                        requestMin,
                        requestMax,
                        physicalFirstProbeIndex,
                        DdgiClipmapDirtyReason.DirtyBounds,
                        dirtyRegion.Reason);
                    scored.Add(new ScoredDirtyProbeRequest(
                        request,
                        ScoreDirtyProbeRequest(expanded.Center, metadata.CascadeIndex, spacingScale)));
                }
            }

            if (scored.Count == 0)
                return Array.Empty<DdgiFrameLayoutDirtyProbeRequest>();

            scored.Sort(static (left, right) =>
            {
                int scoreCompare = left.Score.CompareTo(right.Score);
                return scoreCompare != 0 ? scoreCompare : left.Request.VolumeIndex.CompareTo(right.Request.VolumeIndex);
            });
            var result = new DdgiFrameLayoutDirtyProbeRequest[scored.Count];
            for (int i = 0; i < scored.Count; i++)
                result[i] = scored[i].Request;
            return result;
        }

        private int FindCascadePhysicalFirstProbeIndex(int cascadeIndex)
        {
            for (int i = 0; i < CameraRelativeCascades.Count; i++)
            {
                DdgiClipmapCascadeState cascade = CameraRelativeCascades[i];
                if (cascade.CascadeIndex == cascadeIndex)
                    return cascade.PhysicalFirstProbeIndex;
            }

            return -1;
        }

        private float ScoreDirtyProbeRequest(Vector3 dirtyCenter, int cascadeIndex, float spacing)
        {
            float score = Math.Max(0, cascadeIndex) * 2_500.0f;
            if (!ViewPriority.Enabled)
                return score;

            Vector3 toDirty = dirtyCenter - ViewPriority.CameraPosition;
            float distance = toDirty.Length();
            float forwardDistance = Vector3.Dot(toDirty, ViewPriority.Forward);
            float lateralX = MathF.Abs(Vector3.Dot(toDirty, ViewPriority.Right));
            float lateralY = MathF.Abs(Vector3.Dot(toDirty, ViewPriority.Up));
            bool currentFrustum = forwardDistance >= ViewPriority.NearPlane &&
                forwardDistance <= ViewPriority.FarPlane &&
                lateralX <= forwardDistance * ViewPriority.TanHalfFovX &&
                lateralY <= forwardDistance * ViewPriority.TanHalfFovY;
            bool expandedFrustum = forwardDistance >= ViewPriority.NearPlane - MathF.Min(ViewPriority.GuardBandWorldUnits, ViewPriority.NearPlane * 0.5f) &&
                forwardDistance <= ViewPriority.FarPlane + ViewPriority.GuardBandWorldUnits &&
                lateralX <= MathF.Max(forwardDistance, 0.0f) * ViewPriority.TanHalfFovX + ViewPriority.GuardBandWorldUnits &&
                lateralY <= MathF.Max(forwardDistance, 0.0f) * ViewPriority.TanHalfFovY + ViewPriority.GuardBandWorldUnits;

            if (currentFrustum)
                score -= 10_000.0f;
            else if (expandedFrustum)
                score -= 6_000.0f;

            score += distance / MathF.Max(spacing, 0.001f);
            return score;
        }

        private static IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> MergeDirtyProbeRequests(
            IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> existing,
            IReadOnlyList<DdgiFrameLayoutDirtyProbeRequest> additional)
        {
            if (additional.Count == 0)
                return existing;
            if (existing.Count == 0)
                return additional;

            var merged = new DdgiFrameLayoutDirtyProbeRequest[existing.Count + additional.Count];
            for (int i = 0; i < existing.Count; i++)
                merged[i] = existing[i];
            for (int i = 0; i < additional.Count; i++)
                merged[existing.Count + i] = additional[i];
            return merged;
        }

        private static BoundingBox Expand(BoundingBox bounds, float padding)
        {
            Vector3 p = new(MathF.Max(0.0f, padding));
            return new BoundingBox(bounds.Min - p, bounds.Max + p);
        }

        private static bool IntersectsVolume(GlobalIlluminationProbeVolume volume, BoundingBox dirtyBounds)
        {
            BoundingBox volumeBounds = volume.Bounds;
            return dirtyBounds.Intersects(volumeBounds) || volumeBounds.Contains(dirtyBounds.Center);
        }

        private static float CalculateDirtyBoundsPadding(GlobalIlluminationProbeVolume volume, float spacing)
        {
            float maxRayDistance = float.IsFinite(volume.MaxRayDistance) ? MathF.Max(volume.MaxRayDistance, 0.0f) : 0.0f;
            float rayInfluence = maxRayDistance > 0.0f ? maxRayDistance * 0.25f : spacing;
            return MathF.Max(spacing, MathF.Min(rayInfluence, spacing * 2.0f));
        }

        private static DdgiClipmapCell ClampWorldPositionToClipmapCell(
            Vector3 worldPosition,
            Vector3 spacing,
            DdgiClipmapCell gridMin,
            GlobalIlluminationProbeVolume volume)
        {
            return new DdgiClipmapCell(
                Math.Clamp(FloorToCell(worldPosition.X, spacing.X), gridMin.X, gridMin.X + volume.ProbeCountX - 1),
                Math.Clamp(FloorToCell(worldPosition.Y, spacing.Y), gridMin.Y, gridMin.Y + volume.ProbeCountY - 1),
                Math.Clamp(FloorToCell(worldPosition.Z, spacing.Z), gridMin.Z, gridMin.Z + volume.ProbeCountZ - 1));
        }

        private static int FloorToCell(float value, float spacing)
        {
            if (!float.IsFinite(value) || !float.IsFinite(spacing) || spacing <= 0.0f)
                return 0;

            double cell = Math.Floor(value / spacing);
            if (cell <= int.MinValue)
                return int.MinValue;
            if (cell >= int.MaxValue)
                return int.MaxValue;
            return (int)cell;
        }

        private static DdgiClipmapCell Min(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Min(left.Z, right.Z));

        private static DdgiClipmapCell Max(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y), Math.Max(left.Z, right.Z));

        private readonly record struct ScoredDirtyProbeRequest(
            DdgiFrameLayoutDirtyProbeRequest Request,
            float Score);
    }

    public readonly record struct DdgiFrameLayoutDirtyProbeRequest(
        int VolumeIndex,
        int CascadeIndex,
        DdgiClipmapCell MinCell,
        DdgiClipmapCell MaxCell,
        int PhysicalFirstProbeIndex,
        DdgiClipmapDirtyReason Reason,
        DdgiDirtyReason SourceReason = DdgiDirtyReason.Unknown);

    public readonly record struct DdgiViewPriorityContext(
        bool Enabled,
        Vector3 CameraPosition,
        Vector3 Forward,
        Vector3 Right,
        Vector3 Up,
        Vector3 CameraVelocity,
        float NearPlane,
        float FarPlane,
        float TanHalfFovX,
        float TanHalfFovY,
        float GuardBandWorldUnits,
        float SafetyRadius);

    public static class DdgiFrameLayoutBuilder
    {
        private const int DefaultCameraRelativeMaxProbeUpdatesPerCascade = 1024;
        private const float AssumedSteadyMovementFrameRate = 60.0f;

        public static DdgiFrameLayout Build(
            Scene scene,
            ICamera camera,
            GlobalIlluminationSettings settings,
            CameraRelativeDdgiClipmapController clipmaps,
            ulong frameSerial,
            bool cameraCut,
            Vector3 cameraVelocity = default,
            DdgiLocalVolumeSlotAllocator? localVolumeSlots = null)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (clipmaps == null)
                throw new ArgumentNullException(nameof(clipmaps));

            if (!settings.EffectiveUseDdgi)
                return DdgiFrameLayout.Empty;

            bool cameraRelativeEnabled = settings.DdgiCameraRelativeEnabled;
            int activeProbeBudget = GlobalIlluminationProbeVolumeData.CalculateActiveProbeBudget(settings);
            var volumes = new List<GlobalIlluminationProbeVolume>(
                Math.Min(DdgiProbeVolumeManager.AbsoluteMaxVolumeCount, scene.GlobalIlluminationProbeVolumes.Count + settings.DdgiClipmapCascadeCount));
            var volumeMetadata = new List<DdgiProbeVolumeRuntimeMetadata>(volumes.Capacity);

            int authoredProbeCount = 0;
            int cameraRelativeProbeCount = 0;
            int authoredVolumeCount = 0;
            int emittedCameraRelativeCascadeCount = 0;
            DdgiLocalVolumeSlotAssignmentResult localSlotAssignment = DdgiLocalVolumeSlotAssignmentResult.Empty;
            DdgiFrameLayoutDirtyProbeRequest[] dirtyProbeRequests = Array.Empty<DdgiFrameLayoutDirtyProbeRequest>();
            CameraRelativeDdgiClipmapUpdateResult clipmapUpdate = default;

            if (cameraRelativeEnabled)
            {
                clipmapUpdate = clipmaps.Update(
                    camera.Position,
                    frameSerial,
                    settings,
                    cameraCut);
                cameraRelativeProbeCount = AddCameraRelativeCascades(
                    volumes,
                    volumeMetadata,
                    clipmaps.Cascades,
                    activeProbeBudget,
                    settings,
                    out emittedCameraRelativeCascadeCount);
                dirtyProbeRequests = BuildDirtyProbeRequests(
                    clipmaps.Cascades,
                    firstCascadeVolumeIndex: 0,
                    emittedCameraRelativeCascadeCount,
                    cameraRelativeFirstProbeIndex: 0,
                    clipmapUpdate.DirtyProbeCount);
                int maxAuthoredVolumes = Math.Max(0, DdgiProbeVolumeManager.AbsoluteMaxVolumeCount - volumes.Count);
                int remainingProbeBudget = Math.Max(0, activeProbeBudget - cameraRelativeProbeCount);
                if (localVolumeSlots != null)
                {
                    localSlotAssignment = localVolumeSlots.Assign(
                        scene.GlobalIlluminationProbeVolumes,
                        volumes,
                        volumeMetadata,
                        maxAuthoredVolumes,
                        remainingProbeBudget,
                        cameraRelativeProbeCount,
                        camera);
                    authoredProbeCount = localSlotAssignment.AuthoredProbeCount;
                }
                else
                {
                    authoredProbeCount = AddAuthoredVolumes(
                    scene.GlobalIlluminationProbeVolumes,
                    volumes,
                    volumeMetadata,
                    maxAuthoredVolumes,
                    remainingProbeBudget,
                    camera);
                }
                authoredVolumeCount = Math.Max(0, volumes.Count - emittedCameraRelativeCascadeCount);
            }
            else
            {
                if (localVolumeSlots != null)
                {
                    localSlotAssignment = localVolumeSlots.Assign(
                        scene.GlobalIlluminationProbeVolumes,
                        volumes,
                        volumeMetadata,
                        DdgiProbeVolumeManager.AbsoluteMaxVolumeCount,
                        activeProbeBudget,
                        0,
                        camera);
                    authoredProbeCount = localSlotAssignment.AuthoredProbeCount;
                }
                else
                {
                    authoredProbeCount = AddAuthoredVolumes(
                    scene.GlobalIlluminationProbeVolumes,
                    volumes,
                    volumeMetadata,
                    DdgiProbeVolumeManager.AbsoluteMaxVolumeCount,
                    activeProbeBudget,
                    camera);
                }
                authoredVolumeCount = volumes.Count;
            }

            int totalPhysicalProbeCount = Math.Min(
                DdgiProbeVolumeManager.AbsoluteMaxProbeCount,
                checked(cameraRelativeProbeCount + Math.Max(authoredProbeCount, localSlotAssignment.LocalPoolProbeCount)));

            return new DdgiFrameLayout(
                volumes.ToArray(),
                volumeMetadata.ToArray(),
                Array.Empty<BoundingBox>(),
                Array.Empty<DdgiDirtyRegion>(),
                dirtyProbeRequests,
                isDdgiActive: true,
                cameraRelativeEnabled,
                defaultVolumeIncluded: false,
                authoredVolumeCount,
                cameraRelativeCascadeCount: emittedCameraRelativeCascadeCount,
                authoredProbeCount,
                cameraRelativeProbeCount,
                totalPhysicalProbeCount,
                CreateViewPriorityContext(camera, settings, cameraVelocity),
                cameraRelativeEnabled ? clipmaps.Cascades : Array.Empty<DdgiClipmapCascadeState>(),
                cameraRelativeEnabled ? clipmapUpdate.MovementClass : DdgiCameraMovementClass.None,
                cameraRelativeEnabled && clipmapUpdate.FastMovement,
                cameraRelativeEnabled && clipmapUpdate.ViewPriorityHistoryReset,
                localSlotAssignment.SlotCount,
                localSlotAssignment.SlotProbeCapacity,
                localSlotAssignment.ActiveSlotCount,
                localSlotAssignment.LastGeneration,
                localSlotAssignment.LocalPoolProbeCount,
                localSlotAssignment.AllocationSignature,
                localSlotAssignment.EvictionReason);
        }

        public static BoundingBox EstimateSceneProbeBounds(Scene scene)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            bool hasPoint = false;

            foreach (RenderObject renderObject in scene.RenderObjects)
            {
                if (renderObject == null || !renderObject.Enabled || !renderObject.Visible)
                    continue;

                Vector3 position = renderObject.Position;
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
                hasPoint = true;
            }

            if (!hasPoint)
            {
                min = new Vector3(-12.0f, -2.0f, -12.0f);
                max = new Vector3(12.0f, 10.0f, 12.0f);
                return new BoundingBox(min, max);
            }

            Vector3 size = max - min;
            if (size.X < 4.0f)
            {
                min.X -= 12.0f;
                max.X += 12.0f;
            }

            if (size.Y < 4.0f)
            {
                min.Y -= 2.0f;
                max.Y += 10.0f;
            }

            if (size.Z < 4.0f)
            {
                min.Z -= 12.0f;
                max.Z += 12.0f;
            }

            return new BoundingBox(min, max);
        }

        private static int AddAuthoredVolumes(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            List<GlobalIlluminationProbeVolume> destination,
            List<DdgiProbeVolumeRuntimeMetadata> metadataDestination,
            int maxVolumeCount,
            int maxProbeCount,
            ICamera camera)
        {
            if (maxVolumeCount <= 0 || maxProbeCount <= 0)
                return 0;

            var candidates = new List<AuthoredVolumeAdmissionCandidate>(authoredVolumes.Count);
            for (int i = 0; i < authoredVolumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? volume = authoredVolumes[i];
                if (volume == null)
                    continue;

                candidates.Add(new AuthoredVolumeAdmissionCandidate(
                    volume,
                    i,
                    ScoreAuthoredVolume(volume, camera)));
            }

            candidates.Sort(static (left, right) =>
            {
                int scoreCompare = left.Score.CompareTo(right.Score);
                return scoreCompare != 0 ? scoreCompare : left.OriginalIndex.CompareTo(right.OriginalIndex);
            });

            int probeCount = 0;
            int admitted = 0;
            for (int i = 0; i < candidates.Count && admitted < maxVolumeCount; i++)
            {
                GlobalIlluminationProbeVolume volume = candidates[i].Volume;
                if (volume.Enabled && volume.ProbeCount > Math.Max(0, maxProbeCount - probeCount))
                    continue;

                destination.Add(volume);
                metadataDestination.Add(CreateAuthoredMetadata(volume));
                admitted++;
                if (volume.Enabled)
                    probeCount = checked(probeCount + volume.ProbeCount);
            }

            return probeCount;
        }

        private static DdgiProbeVolumeRuntimeMetadata CreateAuthoredMetadata(GlobalIlluminationProbeVolume volume)
        {
            uint flags = GlobalIlluminationProbeVolumeData.VolumeInitializedFlag |
                GlobalIlluminationProbeVolumeData.VolumeAuthoredPriorityFlag;
            if (volume.Interior)
                flags |= GlobalIlluminationProbeVolumeData.VolumeInteriorFlag;

            return new DdgiProbeVolumeRuntimeMetadata(
                DdgiProbeVolumeKind.Authored,
                -1,
                0,
                0,
                0,
                0,
                0,
                volume.StreamingCellId,
                CalculateAuthoredEdgeBlendFraction(volume),
                flags,
                StreamingCellId: volume.StreamingCellId,
                QualityClass: (int)volume.QualityClass,
                Priority: volume.Priority,
                BlendDistance: volume.BlendDistance,
                UpdatePriority: volume.UpdatePriority);
        }

        private static float CalculateAuthoredEdgeBlendFraction(GlobalIlluminationProbeVolume volume)
        {
            if (volume.BlendDistance <= 0.0f)
                return 0.0f;

            float minAxis = MathF.Min(volume.Size.X, MathF.Min(volume.Size.Y, volume.Size.Z));
            if (!float.IsFinite(minAxis) || minAxis <= 0.0f)
                return 0.0f;

            return Math.Clamp(volume.BlendDistance / minAxis, 0.0f, 0.5f);
        }

        private static float ScoreAuthoredVolume(GlobalIlluminationProbeVolume volume, ICamera camera)
        {
            float score = volume.Enabled ? 0.0f : 1_000_000.0f;
            BoundingBox bounds = volume.Bounds;
            Vector3 cameraPosition = camera.Position;
            if (bounds.Contains(cameraPosition))
                score -= 100_000.0f;

            Vector3 closest = new(
                Math.Clamp(cameraPosition.X, bounds.Min.X, bounds.Max.X),
                Math.Clamp(cameraPosition.Y, bounds.Min.Y, bounds.Max.Y),
                Math.Clamp(cameraPosition.Z, bounds.Min.Z, bounds.Max.Z));
            score += Vector3.DistanceSquared(cameraPosition, closest);
            score += Math.Max(0, volume.ProbeCount) * 0.01f;
            return score;
        }

        private static int AddCameraRelativeCascades(
            List<GlobalIlluminationProbeVolume> destination,
            List<DdgiProbeVolumeRuntimeMetadata> metadataDestination,
            IReadOnlyList<DdgiClipmapCascadeState> cascades,
            int remainingProbeBudget,
            GlobalIlluminationSettings settings,
            out int emittedCascadeCount)
        {
            int cameraRelativeProbeCount = 0;
            emittedCascadeCount = 0;
            for (int i = 0; i < cascades.Count && destination.Count < DdgiProbeVolumeManager.AbsoluteMaxVolumeCount; i++)
            {
                DdgiClipmapCascadeState cascade = cascades[i];
                if (cascade.ProbeCount > remainingProbeBudget - cameraRelativeProbeCount)
                    break;

                destination.Add(CreateCameraRelativeVolume(cascade, settings));
                metadataDestination.Add(CreateCameraRelativeMetadata(cascade, settings.DdgiClipmapEdgeBlendFraction));
                cameraRelativeProbeCount = checked(cameraRelativeProbeCount + cascade.ProbeCount);
                emittedCascadeCount++;
            }

            return cameraRelativeProbeCount;
        }

        private static DdgiProbeVolumeRuntimeMetadata CreateCameraRelativeMetadata(
            DdgiClipmapCascadeState cascade,
            float edgeBlendFraction)
        {
            uint flags = GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag;
            if (cascade.DirtyProbeCount == 0)
                flags |= GlobalIlluminationProbeVolumeData.VolumeInitializedFlag;

            return new DdgiProbeVolumeRuntimeMetadata(
                DdgiProbeVolumeKind.CameraClipmap,
                cascade.CascadeIndex,
                cascade.LogicalGridMinCell.X,
                cascade.LogicalGridMinCell.Y,
                cascade.LogicalGridMinCell.Z,
                cascade.RingOffset.X,
                cascade.RingOffset.Y,
                cascade.RingOffset.Z,
                edgeBlendFraction,
                flags);
        }

        private static GlobalIlluminationProbeVolume CreateCameraRelativeVolume(DdgiClipmapCascadeState cascade, GlobalIlluminationSettings settings)
        {
            Vector3 size = new(
                cascade.ProbeSpacing * (cascade.ProbeCountX - 1),
                cascade.ProbeSpacing * (cascade.ProbeCountY - 1),
                cascade.ProbeSpacing * (cascade.ProbeCountZ - 1));
            return new GlobalIlluminationProbeVolume
            {
                Name = $"Camera DDGI Cascade {cascade.CascadeIndex}",
                Enabled = true,
                Origin = cascade.SnappedOrigin,
                Size = size,
                ProbeCountX = cascade.ProbeCountX,
                ProbeCountY = cascade.ProbeCountY,
                ProbeCountZ = cascade.ProbeCountZ,
                RaysPerProbe = settings.ResolveDdgiCascadeRaysPerProbe(cascade.CascadeIndex),
                MaxProbeUpdatesPerFrame = CalculateCameraRelativeUpdateBudget(cascade, settings),
                MaxRayDistance = settings.ResolveDdgiCascadeMaxRayDistance(cascade.CascadeIndex),
                Hysteresis = 0.985f
            };
        }

        private static int CalculateCameraRelativeUpdateBudget(DdgiClipmapCascadeState cascade, GlobalIlluminationSettings settings)
        {
            float targetSeconds = cascade.CascadeIndex switch
            {
                0 => 1.5f,
                1 => 5.0f,
                2 => 12.0f,
                _ => 20.0f
            };
            int steadyRefreshBudget = Math.Max(1, (int)MathF.Ceiling(cascade.ProbeCount / (targetSeconds * AssumedSteadyMovementFrameRate)));
            int warmupBudget = Math.Min(cascade.ProbeCount, DefaultCameraRelativeMaxProbeUpdatesPerCascade);
            int perCascadeBudget = Math.Max(steadyRefreshBudget, Math.Min(warmupBudget, settings.DdgiMaxProbeUpdatesPerFrame));
            return Math.Min(cascade.ProbeCount, perCascadeBudget);
        }

        private static DdgiFrameLayoutDirtyProbeRequest[] BuildDirtyProbeRequests(
            IReadOnlyList<DdgiClipmapCascadeState> cascades,
            int firstCascadeVolumeIndex,
            int emittedCascadeCount,
            int cameraRelativeFirstProbeIndex,
            int expectedDirtyProbeCount)
        {
            if (expectedDirtyProbeCount <= 0)
                return Array.Empty<DdgiFrameLayoutDirtyProbeRequest>();

            var requests = new List<DdgiFrameLayoutDirtyProbeRequest>();
            for (int cascadeIndex = 0; cascadeIndex < emittedCascadeCount; cascadeIndex++)
            {
                DdgiClipmapCascadeState cascade = cascades[cascadeIndex];
                for (int i = 0; i < cascade.DirtyRegions.Count; i++)
                {
                    DdgiClipmapDirtyRegion region = cascade.DirtyRegions[i];
                    requests.Add(new DdgiFrameLayoutDirtyProbeRequest(
                        firstCascadeVolumeIndex + cascadeIndex,
                        cascade.CascadeIndex,
                        region.MinCell,
                        region.MaxCell,
                        cameraRelativeFirstProbeIndex + cascade.PhysicalFirstProbeIndex,
                        region.Reason));
                }
            }

            return requests.ToArray();
        }

        private readonly record struct AuthoredVolumeAdmissionCandidate(
            GlobalIlluminationProbeVolume Volume,
            int OriginalIndex,
            float Score);

        private static DdgiViewPriorityContext CreateViewPriorityContext(
            ICamera camera,
            GlobalIlluminationSettings settings,
            Vector3 cameraVelocity)
        {
            if (!settings.DdgiCameraRelativeEnabled || settings.DdgiFrustumPriorityWeight <= 0.0f)
                return default;

            Vector3 forward = camera.Forward.Normalized();
            Vector3 right = camera.Right.Normalized();
            Vector3 up = camera.Up.Normalized();
            if (forward == Vector3.Zero || right == Vector3.Zero || up == Vector3.Zero)
                return default;

            float fov = float.IsFinite(camera.FieldOfView)
                ? Math.Clamp(camera.FieldOfView, 0.017453292f, 3.05432619f)
                : MathF.PI / 3.0f;
            float aspect = float.IsFinite(camera.AspectRatio) && camera.AspectRatio > 0.0f
                ? Math.Clamp(camera.AspectRatio, 0.1f, 8.0f)
                : 1.0f;
            float nearPlane = float.IsFinite(camera.NearPlane) && camera.NearPlane > 0.0f ? camera.NearPlane : 0.1f;
            float farPlane = float.IsFinite(camera.FarPlane) && camera.FarPlane > nearPlane ? camera.FarPlane : 1000.0f;
            float tanHalfFovY = MathF.Tan(fov * 0.5f);
            float baseSpacing = MathF.Max(settings.DdgiClipmapBaseSpacing, 0.001f);
            float guardBand = baseSpacing * Math.Max(0, settings.DdgiClipmapSafetyMarginCells);
            float safetyRadius = baseSpacing *
                MathF.Max(settings.DdgiClipmapProbeCountX, settings.DdgiClipmapProbeCountZ) *
                0.75f;
            safetyRadius = MathF.Max(safetyRadius, guardBand + cameraVelocity.Length());

            return new DdgiViewPriorityContext(
                true,
                camera.Position,
                forward,
                right,
                up,
                cameraVelocity,
                nearPlane,
                farPlane,
                tanHalfFovY * aspect,
                tanHalfFovY,
                guardBand,
                safetyRadius);
        }
    }
}

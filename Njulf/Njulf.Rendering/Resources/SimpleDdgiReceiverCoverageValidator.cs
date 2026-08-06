using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// A named world-space receiver region that must retain a declared DDGI
    /// resolution while a camera follows a deterministic path.  This is purposefully
    /// receiver-centric: shading selects volumes from the fragment position, not the
    /// camera/player position.
    /// </summary>
    public sealed record SimpleDdgiReceiverCoverageRegion(
        string Name,
        BoundingBox Bounds,
        float MaximumPrimarySpacing,
        bool RequireCoarserFallback = true)
    {
        public float RequiredTransitionBand => Math.Max(MaximumPrimarySpacing * 1.5f, 0.001f);
    }

    /// <summary>One reproducible camera sample in a coverage-validation path.</summary>
    public sealed record SimpleDdgiCoverageCameraSample(string Name, Vector3 Position);

    public enum SimpleDdgiCoverageIssueKind : uint
    {
        LayoutRejected = 0,
        Uncovered = 1,
        UnderResolved = 2,
        MissingTransitionFallback = 3
    }

    /// <summary>
    /// One representative receiver point evaluated at one camera sample.  The
    /// selected volume follows the shared shader ordering: authored volumes by
    /// priority/purpose first, then near/mid/far rings.
    /// </summary>
    public sealed record SimpleDdgiReceiverCoverageSample(
        string Receiver,
        string Camera,
        Vector3 Position,
        string? PrimaryVolume,
        float PrimarySpacing,
        float EdgeDistance,
        float TransitionBand,
        bool HasCoarserFallback,
        bool IsInTransitionBand,
        bool IsCovered,
        bool IsWithinResolutionTarget);

    public sealed record SimpleDdgiReceiverCoverageIssue(
        SimpleDdgiCoverageIssueKind Kind,
        string Receiver,
        string Camera,
        Vector3 Position,
        string Message);

    /// <summary>
    /// Pure receiver-coverage output suitable for a build assertion, capture
    /// metadata, or an editor/debug overlay.  It retains the layout admission
    /// report so an allocation-time degradation cannot masquerade as a coverage
    /// result from the authored configuration.
    /// </summary>
    public sealed record SimpleDdgiReceiverCoverageReport(
        SimpleDdgiLayoutReport Layout,
        IReadOnlyList<SimpleDdgiReceiverCoverageSample> Samples,
        IReadOnlyList<SimpleDdgiReceiverCoverageIssue> Issues,
        int ExpectedRingRecenterEvents)
    {
        public bool IsCovered => Issues.Count == 0;
        public bool HasLayoutDegradation => Layout.WasDegraded;
    }

    /// <summary>
    /// CPU oracle for the Simple-DDGI receiver coverage contract.  It deliberately
    /// mirrors only the stable policy shared by the renderer and shader: authored
    /// priority, world-space containment, ring placement/hysteresis, and explicit
    /// overlap fallback. It does not make a Vulkan allocation or depend on camera
    /// visibility, so it is safe to run in build/CI validation.
    /// </summary>
    public static class SimpleDdgiReceiverCoverageValidator
    {
        private const int RingSourceOrdinalBase = 10_000;

        public static SimpleDdgiReceiverCoverageReport Validate(
            GlobalIlluminationSettings settings,
            BoundingBox sceneBounds,
            IReadOnlyList<SimpleDdgiReceiverCoverageRegion> receivers,
            IReadOnlyList<SimpleDdgiCoverageCameraSample> cameraPath)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (receivers == null)
                throw new ArgumentNullException(nameof(receivers));
            if (cameraPath == null)
                throw new ArgumentNullException(nameof(cameraPath));

            List<CoverageVolume> authored = BuildAuthoredVolumes(settings);
            List<CoverageVolume> ringTemplates = BuildRingTemplates(settings);
            List<CoverageVolume> layoutOrder = BuildLayoutOrder(authored, ringTemplates);
            SimpleDdgiLayoutReport layout = CompileLayout(settings, layoutOrder);
            var acceptedOrdinals = new HashSet<int>(layout.AcceptedSourceOrdinals);

            var samples = new List<SimpleDdgiReceiverCoverageSample>(
                Math.Max(1, receivers.Count) * Math.Max(1, cameraPath.Count) * RepresentativePointCount);
            var issues = new List<SimpleDdgiReceiverCoverageIssue>();
            AppendLayoutIssues(layout, receivers, cameraPath, issues);

            Vector3[] ringOrigins = new Vector3[Math.Max(settings.SimpleDdgiRingCount, 0)];
            bool[] ringHasOrigins = new bool[ringOrigins.Length];
            int expectedRecenters = 0;

            for (int cameraIndex = 0; cameraIndex < cameraPath.Count; cameraIndex++)
            {
                SimpleDdgiCoverageCameraSample camera = cameraPath[cameraIndex] ??
                    throw new ArgumentException("A coverage camera sample cannot be null.", nameof(cameraPath));
                List<CoverageVolume> activeVolumes = new(layoutOrder.Count);
                for (int i = 0; i < authored.Count; i++)
                {
                    CoverageVolume volume = authored[i];
                    if (acceptedOrdinals.Contains(volume.SourceOrdinal))
                        activeVolumes.Add(volume);
                }

                for (int ringIndex = 0; ringIndex < ringTemplates.Count; ringIndex++)
                {
                    CoverageVolume template = ringTemplates[ringIndex];
                    int sourceRingIndex = template.SourceOrdinal - RingSourceOrdinalBase;
                    if (!acceptedOrdinals.Contains(template.SourceOrdinal))
                        continue;

                    Vector3 placementCamera = ResolveRingPlacementCamera(settings, camera.Position);
                    float verticalHysteresis = ResolveVerticalHysteresis(settings);
                    Vector3 origin = SimpleDdgiVolumeManager.ResolveSceneClampedOrigin(
                        sceneBounds.Min,
                        sceneBounds.Max,
                        template.LatticeSize,
                        template.Spacing,
                        placementCamera,
                        ringOrigins[sourceRingIndex],
                        ref ringHasOrigins[sourceRingIndex],
                        out bool recentered,
                        verticalHysteresis);
                    if (recentered)
                        expectedRecenters++;
                    ringOrigins[sourceRingIndex] = origin;
                    activeVolumes.Add(template with
                    {
                        Min = origin,
                        Max = origin + template.LatticeSize
                    });
                }

                activeVolumes.Sort(CompareVolumes);
                for (int receiverIndex = 0; receiverIndex < receivers.Count; receiverIndex++)
                {
                    SimpleDdgiReceiverCoverageRegion receiver = receivers[receiverIndex] ??
                        throw new ArgumentException("A receiver coverage region cannot be null.", nameof(receivers));
                    ValidateReceiverRegion(receiver, camera, activeVolumes, samples, issues);
                }
            }

            return new SimpleDdgiReceiverCoverageReport(
                layout,
                new ReadOnlyCollection<SimpleDdgiReceiverCoverageSample>(samples),
                new ReadOnlyCollection<SimpleDdgiReceiverCoverageIssue>(issues),
                expectedRecenters);
        }

        private static void AppendLayoutIssues(
            SimpleDdgiLayoutReport layout,
            IReadOnlyList<SimpleDdgiReceiverCoverageRegion> receivers,
            IReadOnlyList<SimpleDdgiCoverageCameraSample> cameras,
            List<SimpleDdgiReceiverCoverageIssue> issues)
        {
            if (!layout.WasDegraded)
                return;

            string reason = $"Simple-DDGI layout was degraded before allocation: {layout.Summary}.";
            if (receivers.Count == 0 || cameras.Count == 0)
            {
                issues.Add(new SimpleDdgiReceiverCoverageIssue(
                    SimpleDdgiCoverageIssueKind.LayoutRejected,
                    string.Empty,
                    string.Empty,
                    default,
                    reason));
                return;
            }

            // One deterministic issue is enough to fail a build assertion without
            // inflating a report by every receiver/path sample.
            SimpleDdgiReceiverCoverageRegion receiver = receivers[0];
            SimpleDdgiCoverageCameraSample camera = cameras[0];
            issues.Add(new SimpleDdgiReceiverCoverageIssue(
                SimpleDdgiCoverageIssueKind.LayoutRejected,
                receiver.Name,
                camera.Name,
                receiver.Bounds.Center,
                reason));
        }

        private static void ValidateReceiverRegion(
            SimpleDdgiReceiverCoverageRegion receiver,
            SimpleDdgiCoverageCameraSample camera,
            List<CoverageVolume> volumes,
            List<SimpleDdgiReceiverCoverageSample> samples,
            List<SimpleDdgiReceiverCoverageIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(receiver.Name))
                throw new ArgumentException("A receiver coverage region needs a stable name.", nameof(receiver));
            if (!float.IsFinite(receiver.MaximumPrimarySpacing) || receiver.MaximumPrimarySpacing <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(receiver), "Maximum primary spacing must be finite and positive.");

            Span<Vector3> points = stackalloc Vector3[RepresentativePointCount];
            FillRepresentativePoints(receiver.Bounds, points);
            for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
            {
                Vector3 point = points[pointIndex];
                int primaryIndex = FindPrimaryVolume(volumes, point);
                if (primaryIndex < 0)
                {
                    samples.Add(new SimpleDdgiReceiverCoverageSample(
                        receiver.Name,
                        camera.Name,
                        point,
                        null,
                        0.0f,
                        0.0f,
                        0.0f,
                        false,
                        false,
                        false,
                        false));
                    issues.Add(new SimpleDdgiReceiverCoverageIssue(
                        SimpleDdgiCoverageIssueKind.Uncovered,
                        receiver.Name,
                        camera.Name,
                        point,
                        "No accepted authored or ring DDGI volume contains this receiver point."));
                    continue;
                }

                CoverageVolume primary = volumes[primaryIndex];
                float edgeDistance = DistanceToNearestFace(primary, point);
                float transitionBand = primary.TransitionBand;
                bool inTransition = edgeDistance <= transitionBand;
                bool hasCoarserFallback = FindFallbackVolume(volumes, primaryIndex, point, primary.Spacing) >= 0;
                bool withinResolution = primary.Spacing <= receiver.MaximumPrimarySpacing + 0.0001f;
                samples.Add(new SimpleDdgiReceiverCoverageSample(
                    receiver.Name,
                    camera.Name,
                    point,
                    primary.Id,
                    primary.Spacing,
                    edgeDistance,
                    transitionBand,
                    hasCoarserFallback,
                    inTransition,
                    true,
                    withinResolution));

                if (!withinResolution)
                {
                    issues.Add(new SimpleDdgiReceiverCoverageIssue(
                        SimpleDdgiCoverageIssueKind.UnderResolved,
                        receiver.Name,
                        camera.Name,
                        point,
                        $"Primary volume '{primary.Id}' uses {primary.Spacing:0.###} m spacing; receiver requires <= {receiver.MaximumPrimarySpacing:0.###} m."));
                }

                if (receiver.RequireCoarserFallback && inTransition && !hasCoarserFallback)
                {
                    issues.Add(new SimpleDdgiReceiverCoverageIssue(
                        SimpleDdgiCoverageIssueKind.MissingTransitionFallback,
                        receiver.Name,
                        camera.Name,
                        point,
                        $"Primary volume '{primary.Id}' reaches its {transitionBand:0.###} m transition band without a coarser accepted fallback."));
                }
            }
        }

        private static List<CoverageVolume> BuildAuthoredVolumes(GlobalIlluminationSettings settings)
        {
            var volumes = new List<CoverageVolume>(settings.SimpleDdgiAuthoredVolumes.Count);
            for (int i = 0; i < settings.SimpleDdgiAuthoredVolumes.Count; i++)
            {
                SimpleDdgiAuthoredVolume authored = settings.SimpleDdgiAuthoredVolumes[i];
                Vector3 min = Vector3.Min(authored.Min, authored.Max);
                Vector3 max = Vector3.Max(authored.Min, authored.Max);
                float spacing = Math.Clamp(authored.Spacing, 0.25f, 8.0f);
                if (max.X - min.X <= 0.001f || max.Y - min.Y <= 0.001f || max.Z - min.Z <= 0.001f)
                    continue;

                Vector3 origin = SimpleDdgiVolumeManager.ResolveAuthoredLatticeOrigin(min, spacing, authored.LatticePhase);
                int countX = ResolveAuthoredCount(max.X, origin.X, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountX);
                int countY = ResolveAuthoredCount(max.Y, origin.Y, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountY);
                int countZ = ResolveAuthoredCount(max.Z, origin.Z, spacing, GlobalIlluminationSettings.MaxSimpleDdgiProbeCountZ);
                volumes.Add(new CoverageVolume(
                    $"authored-{i + 1}",
                    i + 1,
                    IsAuthored: true,
                    authored.Purpose,
                    authored.Priority,
                    min,
                    max,
                    spacing,
                    new Vector3((countX - 1) * spacing, (countY - 1) * spacing, (countZ - 1) * spacing),
                    checked(countX * countY * countZ)));
            }

            return volumes;
        }

        private static List<CoverageVolume> BuildRingTemplates(GlobalIlluminationSettings settings)
        {
            int ringCount = Math.Clamp(settings.SimpleDdgiRingCount, 0, 3);
            var volumes = new List<CoverageVolume>(ringCount);
            for (int ringIndex = 0; ringIndex < ringCount; ringIndex++)
            {
                // Use the runtime resolver verbatim. An oracle-only clamp used to
                // model Sponza's far ring at 8 m while the renderer placed it at
                // 11.25 m, invalidating its bounds and recenter predictions.
                float spacing = SimpleDdgiVolumeManager.ResolveRingSpacing(settings, ringIndex);
                (int countX, int countY, int countZ) = SimpleDdgiVolumeManager.ResolveRingGrid(settings, ringIndex);
                Vector3 latticeSize = new(
                    Math.Max(countX - 1, 0) * spacing,
                    Math.Max(countY - 1, 0) * spacing,
                    Math.Max(countZ - 1, 0) * spacing);
                volumes.Add(new CoverageVolume(
                    $"ring-{ringIndex}",
                    RingSourceOrdinalBase + ringIndex,
                    IsAuthored: false,
                    SimpleDdgiVolumePurpose.TransitionSupport,
                    int.MinValue,
                    default,
                    default,
                    spacing,
                    latticeSize,
                    checked(countX * countY * countZ)));
            }

            return volumes;
        }

        private static List<CoverageVolume> BuildLayoutOrder(
            IReadOnlyList<CoverageVolume> authored,
            IReadOnlyList<CoverageVolume> rings)
        {
            var result = new List<CoverageVolume>(authored.Count + rings.Count);
            for (int i = 0; i < authored.Count; i++)
                result.Add(authored[i]);
            for (int i = 0; i < rings.Count; i++)
                result.Add(rings[i]);
            result.Sort(CompareVolumes);
            return result;
        }

        private static SimpleDdgiLayoutReport CompileLayout(
            GlobalIlluminationSettings settings,
            IReadOnlyList<CoverageVolume> volumes)
        {
            var requests = new SimpleDdgiLayoutVolumeRequest[volumes.Count];
            for (int i = 0; i < volumes.Count; i++)
            {
                CoverageVolume volume = volumes[i];
                requests[i] = new SimpleDdgiLayoutVolumeRequest(
                    volume.Id,
                    volume.SourceOrdinal,
                    volume.IsAuthored,
                    volume.Purpose,
                    volume.Priority,
                    volume.Spacing,
                    volume.ProbeCount)
                {
                    GridCountX = ResolveCoverageGridCount(
                        volume.LatticeSize.X,
                        volume.Spacing),
                    GridCountY = ResolveCoverageGridCount(
                        volume.LatticeSize.Y,
                        volume.Spacing),
                    GridCountZ = ResolveCoverageGridCount(
                        volume.LatticeSize.Z,
                        volume.Spacing)
                };
            }

            return SimpleDdgiLayoutCompiler.Compile(
                requests,
                SimpleDdgiLayoutBudget.Resolve(settings),
                settings.SimpleDdgiSampledAtlasEnabled,
                settings.SimpleDdgiLayoutAdmissionMode);
        }

        private static int ResolveCoverageGridCount(
            float latticeExtent,
            float spacing) =>
            Math.Max(
                1,
                checked((int)MathF.Round(
                    Math.Max(latticeExtent, 0.0f) /
                    Math.Max(spacing, 0.001f))) + 1);

        private static Vector3 ResolveRingPlacementCamera(GlobalIlluminationSettings settings, Vector3 cameraPosition)
        {
            if (settings.SimpleDdgiVerticalRingPolicy != SimpleDdgiVerticalRingPolicy.ReceiverAnchored)
                return cameraPosition;
            return new Vector3(
                cameraPosition.X,
                settings.SimpleDdgiReceiverVerticalAnchor,
                cameraPosition.Z);
        }

        private static float ResolveVerticalHysteresis(GlobalIlluminationSettings settings) =>
            settings.SimpleDdgiVerticalRingPolicy switch
            {
                SimpleDdgiVerticalRingPolicy.CameraRelative => 0.0f,
                SimpleDdgiVerticalRingPolicy.ReceiverAnchored => 0.49f,
                _ => settings.SimpleDdgiVerticalRecenterHysteresisFraction
            };

        private static int CompareVolumes(CoverageVolume left, CoverageVolume right)
        {
            int kind = left.KindPriority.CompareTo(right.KindPriority);
            if (kind != 0)
                return kind;
            if (left.IsAuthored)
            {
                int priority = right.Priority.CompareTo(left.Priority);
                if (priority != 0)
                    return priority;
                int purpose = PurposeRank(left.Purpose).CompareTo(PurposeRank(right.Purpose));
                if (purpose != 0)
                    return purpose;
            }

            int spacing = left.Spacing.CompareTo(right.Spacing);
            return spacing != 0 ? spacing : left.SourceOrdinal.CompareTo(right.SourceOrdinal);
        }

        private static int PurposeRank(SimpleDdgiVolumePurpose purpose) => purpose switch
        {
            SimpleDdgiVolumePurpose.ReceiverHero => 0,
            SimpleDdgiVolumePurpose.NavigableInterior => 1,
            SimpleDdgiVolumePurpose.DynamicInfluence => 2,
            _ => 3
        };

        private static int FindPrimaryVolume(IReadOnlyList<CoverageVolume> volumes, Vector3 point)
        {
            for (int i = 0; i < volumes.Count; i++)
            {
                if (Contains(volumes[i], point))
                    return i;
            }

            return -1;
        }

        private static int FindFallbackVolume(
            IReadOnlyList<CoverageVolume> volumes,
            int primaryIndex,
            Vector3 point,
            float primarySpacing)
        {
            for (int i = primaryIndex + 1; i < volumes.Count; i++)
            {
                CoverageVolume candidate = volumes[i];
                if (candidate.Spacing + 0.0001f < primarySpacing || !Contains(candidate, point))
                    continue;
                return i;
            }

            return -1;
        }

        private static bool Contains(CoverageVolume volume, Vector3 point) =>
            point.X >= volume.Min.X && point.X <= volume.Max.X &&
            point.Y >= volume.Min.Y && point.Y <= volume.Max.Y &&
            point.Z >= volume.Min.Z && point.Z <= volume.Max.Z;

        private static float DistanceToNearestFace(CoverageVolume volume, Vector3 point)
        {
            float x = Math.Min(point.X - volume.Min.X, volume.Max.X - point.X);
            float y = Math.Min(point.Y - volume.Min.Y, volume.Max.Y - point.Y);
            float z = Math.Min(point.Z - volume.Min.Z, volume.Max.Z - point.Z);
            return Math.Max(0.0f, Math.Min(x, Math.Min(y, z)));
        }

        private static int ResolveAuthoredCount(float maximum, float origin, float spacing, int maximumCount)
        {
            float extent = Math.Max(maximum - origin, 0.0f);
            return Math.Clamp((int)MathF.Ceiling(extent / spacing) + 1, 2, maximumCount);
        }

        private const int RepresentativePointCount = 15;

        private static void FillRepresentativePoints(BoundingBox bounds, Span<Vector3> destination)
        {
            if (destination.Length < RepresentativePointCount)
                throw new ArgumentException("Destination does not have capacity for the receiver representative points.", nameof(destination));

            Vector3 min = bounds.Min;
            Vector3 max = bounds.Max;
            Vector3 center = bounds.Center;
            destination[0] = center;
            destination[1] = new Vector3(min.X, min.Y, min.Z);
            destination[2] = new Vector3(max.X, min.Y, min.Z);
            destination[3] = new Vector3(min.X, max.Y, min.Z);
            destination[4] = new Vector3(max.X, max.Y, min.Z);
            destination[5] = new Vector3(min.X, min.Y, max.Z);
            destination[6] = new Vector3(max.X, min.Y, max.Z);
            destination[7] = new Vector3(min.X, max.Y, max.Z);
            destination[8] = max;
            destination[9] = new Vector3(min.X, center.Y, center.Z);
            destination[10] = new Vector3(max.X, center.Y, center.Z);
            destination[11] = new Vector3(center.X, min.Y, center.Z);
            destination[12] = new Vector3(center.X, max.Y, center.Z);
            destination[13] = new Vector3(center.X, center.Y, min.Z);
            destination[14] = new Vector3(center.X, center.Y, max.Z);
        }

        private readonly record struct CoverageVolume(
            string Id,
            int SourceOrdinal,
            bool IsAuthored,
            SimpleDdgiVolumePurpose Purpose,
            int Priority,
            Vector3 Min,
            Vector3 Max,
            float Spacing,
            Vector3 LatticeSize,
            int ProbeCount)
        {
            public int KindPriority => IsAuthored ? 0 : 2;
            public float TransitionBand => Math.Max(Spacing * 1.5f, 0.001f);
        }
    }
}

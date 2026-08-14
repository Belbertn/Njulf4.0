using System.Collections.ObjectModel;

namespace Njulf.Rendering.Debug
{
    public enum DebugOverlayRendererKind
    {
        None = 0,
        Line = 1,
        FullScreen = 2,
        DdgiProbe = 3
    }

    [Flags]
    public enum DebugOverlayPrecondition
    {
        None = 0,
        LocalLights = 1 << 0,
        DirectionalShadows = 1 << 1,
        ReflectionProbes = 1 << 2,
        SimpleDdgi = 1 << 3,
        CpuSnapshots = 1 << 4,
        SelectedObject = 1 << 5,
        GeometryDecals = 1 << 6
    }

    public sealed record DebugOverlayDescriptor(
        DebugOverlayMode Mode,
        string DisplayName,
        bool IsActive,
        int CycleOrder,
        DebugOverlayRendererKind RendererKind,
        bool RequiresCpuSnapshots,
        DebugOverlayPrecondition Preconditions,
        string Legend,
        string NoDataGuidance,
        string RetirementReason = "");

    /// <summary>
    /// The single source of truth for overlay identity, traversal, renderer
    /// ownership, preconditions, and user-facing descriptions.
    /// </summary>
    public static class DebugOverlayCatalog
    {
        private static readonly DebugOverlayDescriptor[] DescriptorStorage =
        [
            Active(DebugOverlayMode.None, "None", 0, DebugOverlayRendererKind.None,
                false, DebugOverlayPrecondition.None, "Normal rendering.", "overlay disabled"),
            Active(DebugOverlayMode.LightTiles, "Forward+ light tiles", 1,
                DebugOverlayRendererKind.FullScreen, false,
                DebugOverlayPrecondition.LocalLights,
                "black empty; blue/green low; yellow/red near capacity; magenta saturated",
                "no local lights"),
            Active(DebugOverlayMode.DirectionalShadowCascades,
                "Directional shadow cascade frusta", 2, DebugOverlayRendererKind.Line,
                false, DebugOverlayPrecondition.DirectionalShadows,
                "one stable colour per world-space light frustum",
                "directional shadows unavailable"),
            Active(DebugOverlayMode.ReflectionProbeVolumes, "Reflection probe volumes", 3,
                DebugOverlayRendererKind.Line, false,
                DebugOverlayPrecondition.ReflectionProbes,
                "bright influence shape; faded blend/falloff extent",
                "scene has 0 reflection probes"),
            Active(DebugOverlayMode.DdgiProbeVolumes, "DDGI volume bounds", 4,
                DebugOverlayRendererKind.Line, false, DebugOverlayPrecondition.SimpleDdgi,
                "yellow authored; blue/green/pink near/mid/far ring bounds",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiProbeSpheres, "DDGI probe spheres", 5,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "wire spheres use relocated centres when valid and logical centres otherwise",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiProbeActivity, "DDGI probe activity", 6,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "green active; red inactive; amber fresh; orange relocation pending; magenta invalid; grey nonresident",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiUpdatedProbes, "DDGI updated probes", 7,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "blue full update; cyan maintenance; violet source refresh; red stale/failed",
                "no probes admitted for update"),
            Active(DebugOverlayMode.DdgiProbeRelocation, "DDGI probe relocation", 8,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "faint logical centre; bright relocated centre and connecting vector",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiProbeAge, "DDGI probe age", 9,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "green recent; yellow nearing lifecycle target; red overdue; grey unavailable",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiPhysicalSlots, "DDGI physical slots", 10,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "stable physical-page/slot hash; grey nonresident; magenta stale mapping",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiCascadeBounds, "DDGI cascade bounds", 11,
                DebugOverlayRendererKind.Line, false, DebugOverlayPrecondition.SimpleDdgi,
                "authored and near/mid/far ring bounds only",
                "Simple DDGI is disabled"),
            Active(DebugOverlayMode.DdgiNewlyExposedCells, "DDGI newly exposed cells", 12,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "bright current scroll-exposed probes; other probes are faint context",
                "no newly exposed probes"),
            Active(DebugOverlayMode.DdgiFrustumPriority, "DDGI scheduler priority", 13,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "scheduler visibility/proximity class palette",
                "scheduler priority state unavailable"),
            Active(DebugOverlayMode.DdgiUpdateReasons, "DDGI update reasons", 14,
                DebugOverlayRendererKind.DdgiProbe, false, DebugOverlayPrecondition.SimpleDdgi,
                "admitted records use deterministic reason precedence; multi-reason records are counted",
                "no probes admitted for update"),
            Active(DebugOverlayMode.DecalVolumes, "Decal volumes", 15,
                DebugOverlayRendererKind.Line, true,
                DebugOverlayPrecondition.CpuSnapshots | DebugOverlayPrecondition.GeometryDecals,
                "pink geometry-decal object bounds", "scene has 0 geometry decals"),
            Active(DebugOverlayMode.ObjectBounds, "Object bounds", 16,
                DebugOverlayRendererKind.Line, true, DebugOverlayPrecondition.CpuSnapshots,
                "green visible; orange CPU-culled", "scene has 0 object snapshots"),
            Active(DebugOverlayMode.MeshletBounds, "Meshlet bounds", 17,
                DebugOverlayRendererKind.Line, true, DebugOverlayPrecondition.CpuSnapshots,
                "cyan visible-object meshlet spheres", "scene has 0 visible meshlets"),
            Active(DebugOverlayMode.SelectedObject, "Selected object", 18,
                DebugOverlayRendererKind.Line, true,
                DebugOverlayPrecondition.CpuSnapshots | DebugOverlayPrecondition.SelectedObject,
                "yellow selected-object bounds", "select with Ctrl+Left/Right"),

            Retired(DebugOverlayMode.MaterialInspection, "Material inspection",
                "use /, Ctrl+K, or the editor material panel"),
            Retired(DebugOverlayMode.PassTimings, "Pass timings",
                "use Ctrl+F4 and the diagnostics reporter"),
            Retired(DebugOverlayMode.GpuMemory, "GPU memory",
                "use Ctrl+F2 or Ctrl+Keypad0 diagnostics snapshots"),
            Retired(DebugOverlayMode.DdgiSafetyRefresh, "DDGI safety refresh",
                "routine, retry, and source-repair work is shown by DDGI update reasons"),
            Retired(DebugOverlayMode.DdgiCascadeBlend, "DDGI cascade blend",
                "use the screen-space GI cascade blend-weight debug view")
        ];

        private static readonly ReadOnlyCollection<DebugOverlayDescriptor> DescriptorsView =
            Array.AsReadOnly(DescriptorStorage);
        private static readonly ReadOnlyCollection<DebugOverlayDescriptor> ActiveView =
            Array.AsReadOnly(DescriptorStorage
                .Where(static descriptor => descriptor.IsActive)
                .OrderBy(static descriptor => descriptor.CycleOrder)
                .ToArray());
        private static readonly Dictionary<DebugOverlayMode, DebugOverlayDescriptor> ByMode =
            DescriptorStorage.ToDictionary(static descriptor => descriptor.Mode);

        public static IReadOnlyList<DebugOverlayDescriptor> Descriptors => DescriptorsView;
        public static IReadOnlyList<DebugOverlayDescriptor> ActiveCycle => ActiveView;

        public static bool TryGet(DebugOverlayMode mode, out DebugOverlayDescriptor descriptor) =>
            ByMode.TryGetValue(mode, out descriptor!);

        public static DebugOverlayDescriptor Get(DebugOverlayMode mode) =>
            TryGet(mode, out DebugOverlayDescriptor descriptor)
                ? descriptor
                : throw new ArgumentOutOfRangeException(nameof(mode), mode,
                    "Unknown debug-overlay mode.");

        public static DebugOverlayMode ResolveRendererMode(DebugOverlayMode mode) =>
            TryGet(mode, out DebugOverlayDescriptor descriptor) && descriptor.IsActive
                ? descriptor.Mode
                : DebugOverlayMode.None;

        public static bool RequiresCpuSnapshots(DebugOverlayMode mode) =>
            TryGet(mode, out DebugOverlayDescriptor descriptor) &&
            descriptor.IsActive && descriptor.RequiresCpuSnapshots;

        public static DebugOverlayMode Next(DebugOverlayMode mode, bool reverse = false)
        {
            DebugOverlayMode resolved = ResolveRendererMode(mode);
            int index = -1;
            for (int i = 0; i < ActiveView.Count; i++)
            {
                if (ActiveView[i].Mode == resolved)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                index = 0;
            int delta = reverse ? -1 : 1;
            int next = (index + delta + ActiveView.Count) % ActiveView.Count;
            return ActiveView[next].Mode;
        }

        private static DebugOverlayDescriptor Active(
            DebugOverlayMode mode,
            string displayName,
            int cycleOrder,
            DebugOverlayRendererKind rendererKind,
            bool requiresCpuSnapshots,
            DebugOverlayPrecondition preconditions,
            string legend,
            string noDataGuidance) =>
            new(mode, displayName, true, cycleOrder, rendererKind,
                requiresCpuSnapshots, preconditions, legend, noDataGuidance);

        private static DebugOverlayDescriptor Retired(
            DebugOverlayMode mode,
            string displayName,
            string reason) =>
            new(mode, displayName, false, -1, DebugOverlayRendererKind.None,
                false, DebugOverlayPrecondition.None, string.Empty, reason, reason);
    }
}

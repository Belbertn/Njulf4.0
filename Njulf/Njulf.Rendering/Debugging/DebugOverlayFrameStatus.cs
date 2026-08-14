namespace Njulf.Rendering.Debug
{
    public enum DebugOverlayAvailability
    {
        Disabled = 0,
        Rendered = 1,
        NoData = 2,
        Unavailable = 3,
        Retired = 4
    }

    /// <summary>
    /// The explicit result of resolving and rendering one debug-overlay frame.
    /// The reason accessor is deliberately non-null even for default(T), so a
    /// freshly constructed scene/diagnostic snapshot remains shipping-safe.
    /// </summary>
    public readonly record struct DebugOverlayFrameStatus
    {
        private readonly string? _reason;

        public DebugOverlayFrameStatus(
            DebugOverlayMode mode,
            DebugOverlayAvailability availability,
            int primaryItemCount,
            int secondaryItemCount,
            int droppedItemCount,
            string? reason)
        {
            Mode = mode;
            Availability = availability;
            PrimaryItemCount = Math.Max(0, primaryItemCount);
            SecondaryItemCount = Math.Max(0, secondaryItemCount);
            DroppedItemCount = Math.Max(0, droppedItemCount);
            _reason = reason;
        }

        public DebugOverlayMode Mode { get; }
        public DebugOverlayAvailability Availability { get; }
        public int PrimaryItemCount { get; }
        public int SecondaryItemCount { get; }
        public int DroppedItemCount { get; }
        public string Reason => _reason ??
            (Availability == DebugOverlayAvailability.Disabled
                ? "overlay disabled"
                : string.Empty);

        public static DebugOverlayFrameStatus Disabled(DebugOverlayMode mode = DebugOverlayMode.None) =>
            new(mode, DebugOverlayAvailability.Disabled, 0, 0, 0, "overlay disabled");

        public static DebugOverlayFrameStatus Rendered(
            DebugOverlayMode mode,
            int primaryItemCount,
            int secondaryItemCount = 0,
            int droppedItemCount = 0) =>
            new(mode, DebugOverlayAvailability.Rendered, primaryItemCount,
                secondaryItemCount, droppedItemCount, string.Empty);

        public static DebugOverlayFrameStatus NoData(DebugOverlayMode mode, string reason) =>
            new(mode, DebugOverlayAvailability.NoData, 0, 0, 0, reason);

        public static DebugOverlayFrameStatus Unavailable(DebugOverlayMode mode, string reason) =>
            new(mode, DebugOverlayAvailability.Unavailable, 0, 0, 0, reason);

        public static DebugOverlayFrameStatus Retired(DebugOverlayMode mode, string reason) =>
            new(mode, DebugOverlayAvailability.Retired, 0, 0, 0, reason);
    }
}

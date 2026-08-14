namespace Njulf.Rendering.Debug
{
    public sealed class DebugOverlaySettings
    {
        private int _maxDebugLineSegments = DebugDrawList.DefaultMaxLineSegments;

        public bool Enabled { get; set; }
        public DebugOverlayMode Mode { get; set; } = DebugOverlayMode.None;
        /// <summary>
        /// Retained for settings-file compatibility. World overlays do not
        /// render text labels; use the catalog legend emitted by diagnostics.
        /// </summary>
        public bool ShowLabels { get; set; }
        public bool ShowDepthTestedVolumes { get; set; } = true;
        public bool ShowXRayVolumes { get; set; } = true;
        public int SelectedObjectIndex { get; set; } = -1;
        /// <summary>
        /// Retained for settings-file compatibility. No active overlay selects
        /// an individual light; Forward+ heatmaps visualize every local light.
        /// </summary>
        public int SelectedLightIndex { get; set; } = -1;
        public int SelectedReflectionProbeIndex { get; set; } = -1;
        public bool AllowGpuTiming { get; set; }
        public bool AllowScreenshots { get; set; }
        public bool AllowRenderDocCapture { get; set; }
        public bool CpuSnapshotsEnabled { get; set; }

        public int MaxDebugLineSegments
        {
            get => _maxDebugLineSegments;
            set => _maxDebugLineSegments = Math.Clamp(value, 0, 1_000_000);
        }
    }
}

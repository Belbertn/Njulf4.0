namespace Njulf.Rendering.Debug
{
    public sealed class DebugOverlaySettings
    {
        private int _maxDebugLineSegments = DebugDrawList.DefaultMaxLineSegments;

        public bool Enabled { get; set; }
        public DebugOverlayMode Mode { get; set; } = DebugOverlayMode.None;
        public bool ShowLabels { get; set; }
        public bool ShowDepthTestedVolumes { get; set; } = true;
        public bool ShowXRayVolumes { get; set; } = true;
        public int SelectedObjectIndex { get; set; } = -1;
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

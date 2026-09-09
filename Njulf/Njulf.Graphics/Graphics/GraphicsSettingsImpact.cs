using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Work needed to apply a settings field, as reported by Preview or ApplyAsync.</summary>
public enum GraphicsSettingsImpact
{
    /// <summary>Applies at a frame boundary without rebuilding resources.</summary>
    Runtime,
    /// <summary>Requires preparation or replacement of device resources.</summary>
    ResourceRebuild,
    /// <summary>Requires restarting the host; the current device remains unchanged.</summary>
    RestartRequired,
    /// <summary>Invalid or unsupported request; no changes are applied.</summary>
    Rejected
}

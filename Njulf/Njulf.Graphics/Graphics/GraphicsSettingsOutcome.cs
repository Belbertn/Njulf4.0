using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Result of a settings request. RestartRequired, Rejected and Failed must not be treated as successful application.</summary>
public enum GraphicsSettingsOutcome { NoChange, Applied, Rebuilt, RestartRequired, Rejected, Failed }

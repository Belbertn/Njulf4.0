using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>One requested setting's classification and human-readable explanation.</summary>
public sealed record GraphicsSettingsFieldResult(string Field, GraphicsSettingsImpact Impact, string Reason);

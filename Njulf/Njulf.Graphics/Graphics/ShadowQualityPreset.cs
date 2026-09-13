namespace Njulf.Graphics;

/// <summary>Shadow-only quality tiers. Applying one preserves settings outside its authored shadow budget.</summary>
public enum ShadowQualityPreset
{
    /// <summary>One cascade, legacy filtering, and no local light shadows.</summary>
    Low,
    /// <summary>Two cascades, adaptive tent filtering, and one shadowed area light.</summary>
    Medium,
    /// <summary>Two cascades, adaptive tent filtering, and at least two shadowed area lights.</summary>
    High,
    /// <summary>Four cascades and four shadowed area lights with two samples each.</summary>
    Ultra
}

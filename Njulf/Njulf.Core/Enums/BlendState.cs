namespace Njulf.Core.Enums
{
    /// <summary>Selects one color blending policy for a draw; policies cannot be combined.</summary>
    public enum BlendState
    {
        Opaque,
        AlphaBlend,
        Additive,
        Multiply,
        NonPremultiplied
    }
}

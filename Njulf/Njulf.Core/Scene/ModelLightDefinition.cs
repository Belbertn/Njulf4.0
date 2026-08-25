using Njulf.Core.Math;

namespace Njulf.Core.Scene;

public enum ModelLightType
{
    Point,
    Directional,
    Spot,
    Rectangle,
    Disk,
    Tube
}

public enum ModelLightAttenuationMode
{
    LegacyWindowed,
    InverseSquare,
    Polynomial
}

/// <summary>
/// Renderer-independent light metadata imported from a model scene. Positions
/// and directions are stored in model space; registering them in a live scene
/// is an explicit placement operation.
/// </summary>
public sealed record ModelLightDefinition
{
    public int SourceIndex { get; init; }
    public int SourceNodeIndex { get; init; } = -1;
    public string SourceNodeName { get; init; } = string.Empty;
    public string Name { get; init; } = "Light";
    public ModelLightType Type { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 Direction { get; init; } = Vector3.Forward;
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; } = 1f;
    public float Range { get; init; } = 100f;
    public bool HasAuthoredRange { get; init; }
    public float InnerConeAngle { get; init; }
    public float OuterConeAngle { get; init; } = MathF.PI / 4f;
    public ModelLightAttenuationMode AttenuationMode { get; init; } =
        ModelLightAttenuationMode.InverseSquare;
    public float AttenuationConstant { get; init; } = 1f;
    public float AttenuationLinear { get; init; }
    public float AttenuationQuadratic { get; init; }
}

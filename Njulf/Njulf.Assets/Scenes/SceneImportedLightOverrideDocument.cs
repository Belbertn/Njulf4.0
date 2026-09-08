namespace Njulf.Assets.Scenes;

/// <summary>
/// Scene edits relative to an imported light's generated values. Untouched
/// properties continue to follow the model; edited transforms use world space.
/// </summary>
public sealed record SceneImportedLightOverrideDocument(
    Guid Id,
    SceneLightDocument Source,
    SceneLightDocument Values)
{
    public SceneLightDocument Apply(SceneLightDocument current) => new()
    {
        Id = current.Id,
        Name = Choose(current.Name, Source.Name, Values.Name),
        Type = Choose(current.Type, Source.Type, Values.Type),
        Position = Choose(current.Position, Source.Position, Values.Position),
        Direction = Choose(current.Direction, Source.Direction, Values.Direction),
        Up = Choose(current.Up, Source.Up, Values.Up),
        Size = Choose(current.Size, Source.Size, Values.Size),
        TwoSided = Choose(current.TwoSided, Source.TwoSided, Values.TwoSided),
        Color = Choose(current.Color, Source.Color, Values.Color),
        Intensity = Choose(current.Intensity, Source.Intensity, Values.Intensity),
        Range = Choose(current.Range, Source.Range, Values.Range),
        SpotAngle = Choose(current.SpotAngle, Source.SpotAngle, Values.SpotAngle),
        InnerSpotAngle = Choose(current.InnerSpotAngle, Source.InnerSpotAngle, Values.InnerSpotAngle),
        AttenuationMode = Choose(current.AttenuationMode, Source.AttenuationMode, Values.AttenuationMode),
        AttenuationConstant = Choose(current.AttenuationConstant, Source.AttenuationConstant, Values.AttenuationConstant),
        AttenuationLinear = Choose(current.AttenuationLinear, Source.AttenuationLinear, Values.AttenuationLinear),
        AttenuationQuadratic = Choose(current.AttenuationQuadratic, Source.AttenuationQuadratic, Values.AttenuationQuadratic),
        CastsShadows = Choose(current.CastsShadows, Source.CastsShadows, Values.CastsShadows),
        ShadowStrength = Choose(current.ShadowStrength, Source.ShadowStrength, Values.ShadowStrength),
        ShadowMapSizeOverride = Choose(current.ShadowMapSizeOverride, Source.ShadowMapSizeOverride, Values.ShadowMapSizeOverride),
        ShadowNearPlane = Choose(current.ShadowNearPlane, Source.ShadowNearPlane, Values.ShadowNearPlane),
        ShadowFarPlane = Choose(current.ShadowFarPlane, Source.ShadowFarPlane, Values.ShadowFarPlane),
        ShadowPriority = Choose(current.ShadowPriority, Source.ShadowPriority, Values.ShadowPriority),
        IesProfile = Choose(current.IesProfile, Source.IesProfile, Values.IesProfile),
        IesRotationRadians = Choose(current.IesRotationRadians, Source.IesRotationRadians, Values.IesRotationRadians),
    };

    private static T Choose<T>(T current, T source, T edited) =>
        EqualityComparer<T>.Default.Equals(source, edited) ? current : edited;
}


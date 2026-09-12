namespace Njulf.Core.Scene;

/// <summary>An independently owned placement sharing its template's retained graphics resources.</summary>
public sealed class ModelInstance : Model
{
    internal Scene? AttachedScene { get; set; }
    public SceneNode PlacementRoot { get; internal set; } = new();
}

namespace Njulf.Core.Scene;

/// <summary>An independently owned placement sharing its template's retained graphics resources.</summary>
public sealed class ModelInstance : Model
{
    internal Scene? AttachedScene { get; set; }
    /// <summary>Instance-owned placement node. Move this root to place the model without modifying shared authored geometry.</summary>
    public SceneNode PlacementRoot { get; internal set; } = new();
}

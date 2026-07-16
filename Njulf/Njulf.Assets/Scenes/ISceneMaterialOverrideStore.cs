using Njulf.Core.Scene;

namespace Njulf.Assets.Scenes;

/// <summary>Renderer-neutral bridge for applying and capturing editable material values.</summary>
public interface ISceneMaterialOverrideStore
{
    void Apply(RenderObject renderObject, SceneMaterialOverrideDocument materialOverride);
    SceneMaterialOverrideDocument? Capture(RenderObject renderObject);
}

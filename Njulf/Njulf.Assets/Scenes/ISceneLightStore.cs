using System;
using System.Collections.Generic;

namespace Njulf.Assets.Scenes;

/// <summary>
/// Renderer-neutral bridge used by scene serialization to own runtime lights.
/// Keeping this boundary in Assets prevents source scenes from taking a dependency on Vulkan.
/// </summary>
public interface ISceneLightStore
{
    void Clear();
    void Add(Guid id, SceneLightDocument light);
    IEnumerable<SceneLightDocument> Enumerate();
}

/// <summary>
/// Optional mutable scene-light bridge used by placed model-light instances.
/// Existing serialization-only stores can continue implementing
/// <see cref="ISceneLightStore"/>.
/// </summary>
public interface IMutableSceneLightStore : ISceneLightStore
{
    bool TryUpdate(Guid id, SceneLightDocument light);
    bool TryRemove(Guid id);
}

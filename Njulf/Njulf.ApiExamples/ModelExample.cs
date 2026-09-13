using Njulf.Core.Scene;

namespace Njulf.ApiExamples;

internal sealed class ModelExample(ExampleOptions options) : ExampleGame(options)
{
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        ModelInstance instance = await Content.LoadModelInstanceAsync(Scene, "Assets/tetrahedron.gltf", cancellationToken: cancellationToken);
        instance.PlacementRoot.LocalScale = Njulf.Core.Math.Vector3.One;
    }
}

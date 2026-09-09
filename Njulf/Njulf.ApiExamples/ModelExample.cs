using Njulf.Core.Scene;

namespace Njulf.ApiExamples;

internal sealed class ModelExample(ExampleOptions options) : ExampleGame(options)
{
    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        Model template = await Content.LoadAsync<Model>("Assets/tetrahedron.gltf", cancellationToken: cancellationToken);
        Scene.Add(template.CreateInstance());
    }
}

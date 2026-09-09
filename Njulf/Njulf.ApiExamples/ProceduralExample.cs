using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Core;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Rendering.Data;

namespace Njulf.ApiExamples;

internal sealed class ProceduralExample(ExampleOptions options) : ExampleGame(options)
{
    private InputAction _toggleVisibility = null!;
    private RenderObject _object = null!;

    protected override void Load()
    {
        base.Load();
        using Texture texture = Graphics.CreateTexture2D(1, 1, [255, 128, 64, 255], TextureColorSpace.Srgb);
        using Mesh mesh = Graphics.CreateMesh(
            [new Vector3(0, 1, 0), new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(0, -1, -1)],
            [0u, 1u, 2u, 0u, 2u, 3u, 0u, 3u, 1u, 1u, 3u, 2u]);
        using Material material = Graphics.CreateMaterial(MaterialDefinition.Default with
        {
            Name = "Textured orange",
            BaseColorFactor = Vector4.One,
            MetallicFactor = 0,
            RoughnessFactor = 0.65f
        }, [new(MaterialTextureSlot.BaseColor, texture)]);
        _object = Graphics.CreateRenderObject(mesh, material);
        Scene.Add(_object);
        _toggleVisibility = Input.CreateAction("Toggle visibility");
        _toggleVisibility.AddBinding(new InputBinding(InputKey.Space));
        if (Options.NativeInspect) NativeInspection.Print(Graphics, mesh);
        // The scene object's independent references survive these using scopes.
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (_toggleVisibility.WasPressed) _object.Visible = !_object.Visible;
    }
}

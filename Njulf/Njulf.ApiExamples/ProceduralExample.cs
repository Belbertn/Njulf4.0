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
    private RenderObject _sibling = null!;
    private InputAction _editShared = null!;
    private InputAction _editObject = null!;
    private Mesh _dynamicMesh = null!;
    private Texture _dynamicTexture = null!;
    private VertexPositionNormalTextureTangent[] _vertices = [];
    private Task<TextureReadback>? _textureReadback;

    protected override void Load()
    {
        base.Load();
        byte[] pixels = Enumerable.Range(0, 16).SelectMany(i => i % 2 == 0
            ? new byte[] { 255, 128, 64, 255 } : new byte[] { 64, 128, 255, 255 }).ToArray();
        Texture texture = _dynamicTexture = Graphics.CreateTexture2D(new(4, 4, TextureFormat.Rgba8Srgb, 3),
            new ReadOnlyMemory<byte>[] { pixels }, generateMipmaps: true);
        _vertices = [new(new(0, 1, 0), Vector3.UnitY, new(.5f, 0), new(1, 0, 0, 1)),
            new(new(-1, -1, 1), new(-.577f, -.577f, .577f), new(0, 1), new(1, 0, 0, 1)),
            new(new(1, -1, 1), new(.577f, -.577f, .577f), new(1, 1), new(1, 0, 0, 1)),
            new(new(0, -1, -1), new(0, -.707f, -.707f), new(.5f, .5f), new(1, 0, 0, 1))];
        Mesh mesh = _dynamicMesh = Graphics.CreateMesh(_vertices,
            [0u, 1u, 2u, 0u, 2u, 3u, 0u, 3u, 1u, 1u, 3u, 2u], MeshUsage.Dynamic);
        using Material material = Graphics.CreateMaterial(MaterialDefinition.Default with
        {
            Name = "Textured orange",
            BaseColorFactor = Vector4.One,
            MetallicFactor = 0,
            RoughnessFactor = 0.65f
        }, [new(MaterialTextureSlot.BaseColor, texture)]);
        _object = Graphics.CreateRenderObject(mesh, material);
        _object.Position = new Vector3(-1.5f, 0, 0);
        Scene.Add(_object);
        _sibling = Graphics.CreateRenderObject(mesh, material);
        _sibling.Position = new Vector3(1.5f, 0, 0);
        Scene.Add(_sibling);
        _toggleVisibility = Input.CreateAction("Toggle visibility");
        _toggleVisibility.AddBinding(new InputBinding(InputKey.Space));
        _editShared = Input.CreateAction("Edit shared material");
        _editShared.AddBinding(new InputBinding(InputKey.M));
        _editObject = Input.CreateAction("Edit one object");
        _editObject.AddBinding(new InputBinding(InputKey.N));
        Console.WriteLine("M: change shared roughness/emission. N: isolate the left object and replace its color texture.");
        if (Options.NativeInspect) NativeInspection.Print(Graphics, mesh);
        // The scene object's independent references survive these using scopes.
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        _vertices[0] = _vertices[0] with { Position = new(0, 1.5f + .5f * MathF.Sin((float)gameTime.TotalGameTime.TotalSeconds * 2), 0) };
        Graphics.UpdateMeshVertices(_dynamicMesh, _vertices);
        if (QualityFrames >= 30 && _textureReadback == null)
        {
            Graphics.UpdateTexture2D(_dynamicTexture, 0, new(0, 0, 1, 1), new byte[] { 20, 240, 80, 255 });
            Graphics.GenerateMipmaps(_dynamicTexture);
            _textureReadback = Graphics.ReadTexture2DAsync(_dynamicTexture);
        }
        if (_textureReadback?.IsCompleted == true)
        {
            var result = _textureReadback.GetAwaiter().GetResult();
            if (!result.Data.AsSpan(0, 4).SequenceEqual(new byte[] { 20, 240, 80, 255 }))
                throw new InvalidOperationException("Texture update/readback mismatch.");
        }
        if (_toggleVisibility.WasPressed) _object.Visible = !_object.Visible;
        if (_editShared.WasPressed)
        {
            IMaterial shared = _sibling.Material!;
            shared.UpdateShared(shared.Definition with
            {
                RoughnessFactor = shared.Definition.RoughnessFactor < 0.5f ? 0.8f : 0.2f,
                EmissiveFactor = new Vector3(0.1f, 0.04f, 0), EmissiveStrength = 2
            });
        }
        if (_editObject.WasPressed)
        {
            using Texture blue = Graphics.CreateTexture2D(1, 1, [64, 128, 255, 255], TextureColorSpace.Srgb);
            _object.UpdateMaterial(_object.Material!.Definition with
            {
                BaseColorFactor = new Vector4(0.8f, 1, 1, 1), EmissiveFactor = Vector3.Zero
            }, [new(MaterialTextureSlot.BaseColor, blue)]);
            using Texture? inspected = _object.Material!.RetainTexture(MaterialTextureSlot.BaseColor);
            Console.WriteLine($"Left texture: {inspected?.Width} x {inspected?.Height}; roughness {_object.Material.Definition.RoughnessFactor}");
        }
    }

    protected override void Unload()
    {
        _dynamicMesh?.Dispose(); _dynamicTexture?.Dispose();
        base.Unload();
    }
}

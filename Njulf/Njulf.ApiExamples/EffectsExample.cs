using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Input;

namespace Njulf.ApiExamples;

internal sealed class EffectsExample(ExampleOptions options) : ExampleGame(options)
{
    private EffectRegistration _grade = null!;
    private InputAction _toggleGrade = null!, _toggleEnabled = null!;
    private bool _graded, _automaticGradeApplied;
    protected override void Load()
    {
        base.Load();
        var asset = Content.Load<ShaderEffectAsset>("Assets/Effects/color_grade.njeffect.json");
        foreach (var parameter in asset.Parameters)
            Console.WriteLine($"{parameter.Name}: {parameter.Type}, default={parameter.DefaultValue}, range={parameter.Minimum}..{parameter.Maximum}");
        _grade = Graphics.AddPostProcessEffect("Example.ColorGrade", asset);
        _toggleGrade = Input.CreateAction("Change color grading");
        _toggleGrade.AddBinding(new InputBinding(InputKey.G));
        _toggleEnabled = Input.CreateAction("Enable color grading");
        _toggleEnabled.AddBinding(new InputBinding(InputKey.E));
        using var mesh = Graphics.CreateMesh(
            [new Vector3(0, 1, 0), new Vector3(-1, -1, 1), new Vector3(1, -1, 1)], [0u, 1u, 2u]);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(1, .3f, .08f, 1), MetallicFactor = 0, RoughnessFactor = .7f,
            EmissiveFactor = new Vector3(1, .25f, .05f), EmissiveStrength = 30
        });
        Scene.Add(Graphics.CreateRenderObject(mesh, material));
        Console.WriteLine("G: identity / warm desaturated grade. E: enable / disable. Effect runs after tone mapping, before AA.");
    }
    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        // Finite runs show grading in the captured image; interactive runs start at identity.
        bool automatic = Options.Frames > 0 && QualityFrames >= 60 && !_automaticGradeApplied;
        if (_toggleGrade.WasPressed || automatic)
        {
            if (automatic) _automaticGradeApplied = true;
            _graded = !_graded;
            _grade.SetParameter("Saturation", _graded ? .35f : 1f);
            _grade.SetParameter("Contrast", _graded ? 1.15f : 1f);
            _grade.SetParameter("Tint", _graded ? new Vector3(1, .85f, .7f) : Vector3.One);
        }
        if (_toggleEnabled.WasPressed) _grade.Enabled = !_grade.Enabled;
    }
    protected override void Unload() { _grade?.Dispose(); base.Unload(); }
}

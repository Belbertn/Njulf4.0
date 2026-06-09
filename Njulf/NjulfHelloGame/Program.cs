using System;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Input;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal static class Program
{
    public static void Main()
    {
        using var game = new HelloGame();
        game.Run();
    }
}

internal sealed class HelloGame : Game
{
    private static readonly SampleAssetManifest AssetManifest = SampleAssetManifest.NewSponza;
    private const SampleLightingMode LightingMode = SampleLightingMode.DirectionalKey;

    private SampleInputController? _inputController;
    private SampleSceneLoader? _sceneLoader;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private float _modelRotation;

    public HelloGame()
    {
        Name = "Njulf Hello Game";
        WindowTitle = "Njulf Hello Game - Mesh Shader glTF Sample";
        WindowWidth = 1600;
        WindowHeight = 900;
        VSync = true;
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        if (Window == null)
            throw new InvalidOperationException("Window must exist before configuring the rendering sample.");

        services.AddNjulfCore();
        services.AddCamera(CreateSampleCamera());
        services.AddRendering(Window);
        services.AddAssets(AppContext.BaseDirectory);
        services.AddInput();
    }

    protected override void Load()
    {
        var camera = Camera as FirstPersonCamera
            ?? throw new InvalidOperationException("NjulfHelloGame requires a FirstPersonCamera.");

        ValidateRuntimeServices();

        if (Input is not InputManager input)
            throw new InvalidOperationException("NjulfHelloGame requires the default InputManager.");

        IServiceProvider services = Services
            ?? throw new InvalidOperationException("Service provider was not created.");
        MaterialManager materialManager = services.GetRequiredService<MaterialManager>();
        LightManager lightManager = services.GetRequiredService<LightManager>();

        SampleInputController.Configure(input);
        _inputController = new SampleInputController(camera, input, Exit);

        _sceneLoader = new SampleSceneLoader(Content!, materialManager, AssetManifest);
        var model = _sceneLoader.Load(Scene);

        SampleLighting.Configure(lightManager, LightingMode);

        _diagnosticsReporter = new SampleDiagnosticsReporter(
            materialManager,
            services.GetService<IModelRenderUploadService>());
        _diagnosticsReporter.PrintModelSummary(model, AssetManifest);
    }

    protected override void Update(float deltaTime)
    {
        _inputController?.Update(deltaTime, WindowWidth, WindowHeight);

        if (AssetManifest.RotationSpeed != 0f)
        {
            _modelRotation += deltaTime * AssetManifest.RotationSpeed;
            _sceneLoader?.ApplyModelRotation(_modelRotation);
        }

        base.Update(deltaTime);
    }

    protected override void Draw()
    {
        if (Renderer == null)
            throw new InvalidOperationException("Renderer is not available during Draw().");
        if (Camera == null)
            throw new InvalidOperationException("Camera is not available during Draw().");

        Renderer.DrawScene(Scene, Camera);
        _diagnosticsReporter?.PrintFirstFrameDiagnostics(Renderer);
    }

    private static FirstPersonCamera CreateSampleCamera()
    {
        var camera = new FirstPersonCamera(new CoreVector3(0f, 1.25f, 5.5f), yaw: 0f, pitch: -0.12f)
        {
            FieldOfView = MathF.PI / 3.2f,
            NearPlane = 0.05f,
            FarPlane = 250f
        };

        return camera;
    }

    private void ValidateRuntimeServices()
    {
        if (Renderer == null)
            throw new InvalidOperationException("Renderer was not registered. Call AddRendering before loading content.");
        if (Content == null)
            throw new InvalidOperationException("Content manager was not registered. Call AddAssets after rendering services are registered.");
        if (Input == null)
            throw new InvalidOperationException("Input manager was not registered. Call AddInput before the sample loads.");
        if (Services?.GetService<IModelRenderUploadService>() == null)
        {
            throw new InvalidOperationException(
                "IModelRenderUploadService was not registered. Content.Load<Model>() requires renderer-backed model upload.");
        }
        if (Services.GetService<LightManager>() == null)
            throw new InvalidOperationException("LightManager was not registered by AddRendering.");
        if (Services.GetService<MaterialManager>() == null)
            throw new InvalidOperationException("MaterialManager was not registered by AddRendering.");
    }
}

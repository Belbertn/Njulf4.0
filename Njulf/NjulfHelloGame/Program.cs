using System;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace NjulfHelloGame;

internal static class Program
{
    public static void Main(string[] args)
    {
        using var game = new HelloGame(ParseSmokeFrameCount(args));
        game.Run();
    }

    private static int? ParseSmokeFrameCount(string[] args)
    {
        const string smokeFramesArg = "--smoke-frames";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, smokeFramesArg, StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int frameCount) || frameCount <= 0)
                    throw new ArgumentException($"{smokeFramesArg} requires a positive integer frame count.");

                return frameCount;
            }

            const string smokeFramesPrefix = "--smoke-frames=";
            if (arg.StartsWith(smokeFramesPrefix, StringComparison.Ordinal))
            {
                string value = arg[smokeFramesPrefix.Length..];
                if (!int.TryParse(value, out int frameCount) || frameCount <= 0)
                    throw new ArgumentException($"{smokeFramesArg} requires a positive integer frame count.");

                return frameCount;
            }
        }

        return null;
    }
}

internal sealed class HelloGame : Game
{
    private static readonly SampleAssetManifest AssetManifest = SampleAssetManifest.NewSponza;
    private const SampleLightingMode LightingMode = SampleLightingMode.PointShadowDemo;
    private const SampleEnvironmentMode EnvironmentMode = SampleEnvironmentMode.ProceduralOutdoor;

    private SampleInputController? _inputController;
    private SampleSceneLoader? _sceneLoader;
    private SampleDiagnosticsReporter? _diagnosticsReporter;
    private IReadOnlyList<ParticleEffectInstance>? _sampleVfxEffects;
    private readonly int? _smokeFrameCount;
    private int _drawnFrames;
    private bool _smokeResizeTriggered;
    private float _modelRotation;

    public HelloGame(int? smokeFrameCount = null)
    {
        if (smokeFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(smokeFrameCount));

        _smokeFrameCount = smokeFrameCount;
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
        MeshManager meshManager = services.GetRequiredService<MeshManager>();
        MaterialManager materialManager = services.GetRequiredService<MaterialManager>();
        LightManager lightManager = services.GetRequiredService<LightManager>();
        VulkanRenderer renderer = Renderer as VulkanRenderer
            ?? throw new InvalidOperationException("NjulfHelloGame requires the Vulkan renderer.");

        SampleInputController.Configure(input);
        _sceneLoader = new SampleSceneLoader(Content!, materialManager, AssetManifest);
        var model = _sceneLoader.Load(Scene);
        SampleReflectionProbes.Configure(Scene);
        SampleReflectionTestSpheres.Configure(Scene, meshManager, materialManager);
        SampleAnimatedCharacter.Configure(Scene, Content!);
        _sampleVfxEffects = SampleVfxEffects.Configure(Scene);

        _inputController = new SampleInputController(
            camera,
            input,
            Exit,
            renderer,
            lightManager,
            LightingMode,
            _sampleVfxEffects);

        SampleLighting.Configure(lightManager, LightingMode);
        SampleEnvironment.Configure(renderer, EnvironmentMode);

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

        if (_smokeFrameCount.HasValue && !_smokeResizeTriggered && _drawnFrames >= 1)
        {
            _smokeResizeTriggered = true;
            const int smokeResizeWidth = 1280;
            const int smokeResizeHeight = 720;
            WindowWidth = smokeResizeWidth;
            WindowHeight = smokeResizeHeight;
            Renderer?.Resize(smokeResizeWidth, smokeResizeHeight);
            if (Camera != null)
                Camera.AspectRatio = (float)smokeResizeWidth / smokeResizeHeight;
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

        _drawnFrames++;
        if (_smokeFrameCount.HasValue && _drawnFrames >= _smokeFrameCount.Value)
            Exit();
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
        if (Services.GetService<MeshManager>() == null)
            throw new InvalidOperationException("MeshManager was not registered by AddRendering.");
        if (Services.GetService<MaterialManager>() == null)
            throw new InvalidOperationException("MaterialManager was not registered by AddRendering.");
    }
}

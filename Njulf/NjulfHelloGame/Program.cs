using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;
using Silk.NET.Input;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using GpuVector3 = System.Numerics.Vector3;

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
    private const string MoveForward = "move_forward";
    private const string MoveBackward = "move_backward";
    private const string MoveLeft = "move_left";
    private const string MoveRight = "move_right";
    private const string MoveUp = "move_up";
    private const string MoveDown = "move_down";
    private const string ExitGame = "exit";
    private const string SampleModelPath = "vintage_video_camera_2k.gltf";
    private const float SampleModelScale = 12.0f;

    private static readonly string[] RequiredSampleFiles =
    {
        SampleModelPath,
        "vintage_video_camera.bin",
        Path.Combine("textures", "vintage_video_camera_diff_2k.jpg"),
        Path.Combine("textures", "vintage_video_camera_nor_gl_2k.jpg"),
        Path.Combine("textures", "vintage_video_camera_arm_2k.jpg")
    };

    private FirstPersonCamera? _camera;
    private readonly List<RenderObject> _modelObjects = new();
    private float _modelRotation;
    private bool _printedFrameDiagnostics;

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
        _camera = Camera as FirstPersonCamera
            ?? throw new InvalidOperationException("NjulfHelloGame requires a FirstPersonCamera.");

        ValidateRuntimeServices();
        ValidateSampleFiles();
        ConfigureInput();
        LoadSceneModel();
        ConfigureLights();
    }

    protected override void Update(float deltaTime)
    {
        UpdateCamera(deltaTime);

        if (_modelObjects.Count > 0)
        {
            _modelRotation += deltaTime * 0.25f;
            CoreMatrix4x4 modelWorld = CreateModelWorld(_modelRotation);

            foreach (RenderObject renderObject in _modelObjects)
                renderObject.WorldMatrix = modelWorld;
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
        PrintFirstFrameDiagnostics();
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

    private static void ValidateSampleFiles()
    {
        foreach (string relativePath in RequiredSampleFiles)
        {
            string absolutePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
            if (!File.Exists(absolutePath))
                throw new FileNotFoundException($"Required NjulfHelloGame sample asset is missing: {absolutePath}", absolutePath);
        }
    }

    private void ConfigureInput()
    {
        if (Input is not InputManager input)
            throw new InvalidOperationException("NjulfHelloGame requires the default InputManager.");

        CreateKeyboardAction(input, MoveForward, Key.W);
        CreateKeyboardAction(input, MoveBackward, Key.S);
        CreateKeyboardAction(input, MoveLeft, Key.A);
        CreateKeyboardAction(input, MoveRight, Key.D);
        CreateKeyboardAction(input, MoveUp, Key.E);
        CreateKeyboardAction(input, MoveDown, Key.Q);
        CreateKeyboardAction(input, ExitGame, Key.Escape);
    }

    private void LoadSceneModel()
    {
        if (Content == null)
            throw new InvalidOperationException("Content manager was not created.");

        Model model = Content.Load<Model>(SampleModelPath);
        ValidateUploadedModel(model);

        Scene.Name = "Njulf Hello Scene";
        Scene.AmbientLight = new Color(0.025f, 0.03f, 0.04f, 1f);
        _modelObjects.Clear();

        CoreMatrix4x4 modelWorld = CreateModelWorld(rotation: 0f);

        foreach (RenderObject renderObject in model.RenderObjects)
        {
            renderObject.WorldMatrix = modelWorld;
            renderObject.Visible = true;
            Scene.Add(renderObject);
            _modelObjects.Add(renderObject);
        }

        PrintModelSummary(model);
    }

    private static CoreMatrix4x4 CreateModelWorld(float rotation)
    {
        return CoreMatrix4x4.CreateScale(new CoreVector3(SampleModelScale)) *
               CoreMatrix4x4.CreateRotationY(rotation) *
               CoreMatrix4x4.CreateTranslation(CoreVector3.Zero);
    }

    private void ValidateUploadedModel(Model model)
    {
        if (model.RenderObjects.Count == 0)
            throw new InvalidOperationException("The sample model did not produce renderable objects.");

        MaterialManager materialManager = Services?.GetRequiredService<MaterialManager>()
            ?? throw new InvalidOperationException("MaterialManager was not registered.");

        for (int i = 0; i < model.RenderObjects.Count; i++)
        {
            RenderObject renderObject = model.RenderObjects[i];
            if (renderObject.Mesh is not MeshHandle meshHandle || !meshHandle.IsValid)
                throw new InvalidOperationException($"Render object '{renderObject.Name}' does not contain a valid GPU mesh handle.");
            if (renderObject.Material is not MaterialHandle materialHandle || !materialHandle.IsValid)
                throw new InvalidOperationException($"Render object '{renderObject.Name}' does not contain a valid GPU material handle.");

            GPUMaterialData material = materialManager.GetMaterialData(materialHandle);

            ValidateSampleTextureIndex(renderObject.Name, "base-color", material.AlbedoTextureIndex);
            ValidateSampleTextureIndex(renderObject.Name, "normal", material.NormalTextureIndex);
            ValidateSampleTextureIndex(renderObject.Name, "ARM/metallic-roughness-occlusion", material.MetallicRoughnessTextureIndex);

            if (!BindlessIndex.IsTextureIndex(material.EmissiveTextureIndex))
            {
                throw new InvalidOperationException(
                    $"Render object '{renderObject.Name}' has invalid emissive texture index {material.EmissiveTextureIndex}.");
            }
        }
    }

    private static void ValidateSampleTextureIndex(string objectName, string textureName, int textureIndex)
    {
        if (textureIndex < BindlessIndex.FirstDynamicTextureIndex || !BindlessIndex.IsTextureIndex(textureIndex))
        {
            throw new InvalidOperationException(
                $"Render object '{objectName}' did not receive an imported {textureName} texture. " +
                $"Expected a dynamic bindless texture index >= {BindlessIndex.FirstDynamicTextureIndex}, got {textureIndex}.");
        }
    }

    private void PrintModelSummary(Model model)
    {
        MaterialManager materialManager = Services?.GetRequiredService<MaterialManager>()
            ?? throw new InvalidOperationException("MaterialManager was not registered.");
        PrintModelSummary(model, materialManager, Services?.GetService<IModelRenderUploadService>()?.LastUploadDiagnostics);
    }

    private static void PrintModelSummary(
        Model model,
        MaterialManager materialManager,
        ModelRenderUploadDiagnostics? uploadDiagnostics)
    {
        MaterialHandle[] materialHandles = model.RenderObjects
            .Select(renderObject => (MaterialHandle)renderObject.Material!)
            .Distinct()
            .ToArray();

        int dynamicTextureCount = materialHandles
            .Select(materialManager.GetMaterialData)
            .SelectMany(material => new[]
            {
                material.AlbedoTextureIndex,
                material.NormalTextureIndex,
                material.MetallicRoughnessTextureIndex,
                material.EmissiveTextureIndex
            })
            .Where(index => index >= BindlessIndex.FirstDynamicTextureIndex)
            .Distinct()
            .Count();

        string diagnostics = uploadDiagnostics == null
            ? string.Empty
            : $", uploadedMaterials={uploadDiagnostics.LoadedMaterialCount}, " +
              $"uploadedTextures={uploadDiagnostics.LoadedTextureCount}, " +
              $"defaultWhite={uploadDiagnostics.DefaultWhiteSubstitutions}, " +
              $"defaultNormal={uploadDiagnostics.DefaultNormalSubstitutions}, " +
              $"defaultBlack={uploadDiagnostics.DefaultBlackSubstitutions}";

        Console.WriteLine(
            $"Loaded '{SampleModelPath}': objects={model.RenderObjects.Count}, " +
            $"materials={materialHandles.Length}, importedDynamicTextures={dynamicTextureCount}{diagnostics}.");
    }

    private void PrintFirstFrameDiagnostics()
    {
        if (_printedFrameDiagnostics || Renderer is not VulkanRenderer vulkanRenderer)
            return;

        RendererDiagnostics diagnostics = vulkanRenderer.LastDiagnostics;
        if (diagnostics.VisibleObjectCount == 0 && diagnostics.VisibleMeshletCount == 0)
            return;

        _printedFrameDiagnostics = true;
        Console.WriteLine(
            $"Frame diagnostics: visibleObjects={diagnostics.VisibleObjectCount}, " +
            $"visibleMeshlets={diagnostics.VisibleMeshletCount}, uploadedBytes={diagnostics.UploadedBytes}, " +
            $"lights={diagnostics.LightCount}, tiles={diagnostics.TileCountX}x{diagnostics.TileCountY}, " +
            $"materials={diagnostics.MaterialCount}, textures={diagnostics.TextureCount}, " +
            $"loadedFileTextures={diagnostics.LoadedFileTextureCount}, mipFallbacks={diagnostics.MipmapFallbackCount}, " +
            $"model='{diagnostics.LoadedModelName}', modelObjects={diagnostics.ModelRenderObjectCount}, " +
            $"registeredMeshes={diagnostics.RegisteredMeshCount}, modelMaterials={diagnostics.LoadedMaterialCount}, " +
            $"modelTextures={diagnostics.LoadedTextureCount}, defaultWhite={diagnostics.DefaultWhiteSubstitutions}, " +
            $"defaultNormal={diagnostics.DefaultNormalSubstitutions}, defaultBlack={diagnostics.DefaultBlackSubstitutions}.");
    }

    private void ConfigureLights()
    {
        LightManager lightManager = Services?.GetRequiredService<LightManager>()
            ?? throw new InvalidOperationException("LightManager was not registered.");

        lightManager.ClearLights();
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new GpuVector3(-2.5f, 2.6f, 3.0f),
            Color = new GpuVector3(1.0f, 0.82f, 0.58f),
            Intensity = 22f,
            Range = 8f
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new GpuVector3(2.5f, 1.4f, 1.5f),
            Color = new GpuVector3(0.45f, 0.68f, 1.0f),
            Intensity = 12f,
            Range = 6f
        });
        lightManager.AddLight(new Light
        {
            Type = LightType.Point,
            Position = new GpuVector3(0.0f, 3.0f, -2.75f),
            Color = new GpuVector3(0.7f, 1.0f, 0.72f),
            Intensity = 8f,
            Range = 7f
        });
    }

    private void UpdateCamera(float deltaTime)
    {
        if (_camera == null || Input == null)
            return;

        if (Input.IsKeyDown(ExitGame))
            Exit();

        const float cameraSpeed = 3.0f;
        float distance = cameraSpeed * deltaTime;

        if (Input.IsKeyDown(MoveForward))
            _camera.MoveForward(distance);
        if (Input.IsKeyDown(MoveBackward))
            _camera.MoveBackward(distance);
        if (Input.IsKeyDown(MoveLeft))
            _camera.MoveLeft(distance);
        if (Input.IsKeyDown(MoveRight))
            _camera.MoveRight(distance);
        if (Input.IsKeyDown(MoveUp))
            _camera.MoveUp(distance);
        if (Input.IsKeyDown(MoveDown))
            _camera.MoveDown(distance);

        if (Input.IsMouseButtonDown((int)MouseButton.Right))
        {
            Vector2 mouseDelta = Input.MouseDelta;
            _camera.RotateYawPitch(-mouseDelta.X * 0.0025f, -mouseDelta.Y * 0.0025f);
        }

        _camera.AspectRatio = WindowHeight > 0 ? (float)WindowWidth / WindowHeight : _camera.AspectRatio;
        _camera.Update();
    }

    private static void CreateKeyboardAction(InputManager input, string name, Key key)
    {
        Njulf.Input.Action action = input.CreateAction(name);
        action.AddBinding(new InputBinding(key));
    }
}

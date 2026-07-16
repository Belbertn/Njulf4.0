using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Njulf.Assets;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleAnimatedCharacterTests
{
    [Test]
    public void CreateCharacterWorld_PlacesRepositoryCharacterInInitialCameraForeground()
    {
        string path = FindRepoFile("NjulfHelloGame", "Strut.glb");
        using var importer = new ModelImporter();
        ModelMesh mesh = importer.Import(path, new ImporterOptions { Backend = ModelImportBackend.SharpGltf });
        var model = new Model
        {
            Name = mesh.Name,
            BoundingBox = mesh.BoundingBox,
            BoundingSphere = mesh.BoundingSphere
        };

        Matrix4x4 world = InvokeCreateCharacterWorld(model);
        BoundingBox worldBounds = TransformBounds(model.BoundingBox, world);
        Vector3 worldCenter = worldBounds.Center;
        var initialCamera = new FirstPersonCamera(new Vector3(0f, 1.25f, 5.5f), yaw: 0f, pitch: -0.12f)
        {
            FieldOfView = MathF.PI / 3.2f,
            AspectRatio = 1600f / 900f,
            NearPlane = 0.05f,
            FarPlane = 250f
        };
        Vector3 cameraSpaceCenter = worldCenter * initialCamera.ViewMatrix;

        Assert.Multiple(() =>
        {
            Assert.That(worldBounds.Min.Y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(worldBounds.Size.Y, Is.EqualTo(1.75f).Within(0.001f));
            Assert.That(worldCenter.X, Is.EqualTo(1.35f).Within(0.001f));
            Assert.That(worldCenter.Z, Is.EqualTo(3.6f).Within(0.001f));
            Assert.That(cameraSpaceCenter.Z, Is.LessThan(-1.0f));
            Assert.That(Math.Abs(cameraSpaceCenter.X), Is.LessThan(1.8f));
        });
    }

    private static Matrix4x4 InvokeCreateCharacterWorld(Model model)
    {
        Type type = typeof(NjulfHelloGame.SampleSmokeOptionsParser).Assembly.GetType("NjulfHelloGame.SampleAnimatedCharacter")
            ?? throw new InvalidOperationException("SampleAnimatedCharacter type was not found.");
        MethodInfo method = type.GetMethod("CreateCharacterWorld", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SampleAnimatedCharacter.CreateCharacterWorld was not found.");

        return (Matrix4x4)method.Invoke(null, new object[] { model })!;
    }

    private static BoundingBox TransformBounds(BoundingBox bounds, Matrix4x4 world)
    {
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;
        Vector3[] corners =
        {
            new(min.X, min.Y, min.Z),
            new(max.X, min.Y, min.Z),
            new(min.X, max.Y, min.Z),
            new(max.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z),
            new(max.X, min.Y, max.Z),
            new(min.X, max.Y, max.Z),
            new(max.X, max.Y, max.Z)
        };

        Vector3 transformedMin = corners[0] * world;
        Vector3 transformedMax = transformedMin;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 transformed = corners[i] * world;
            transformedMin = Vector3.Min(transformedMin, transformed);
            transformedMax = Vector3.Max(transformedMax, transformed);
        }

        return new BoundingBox(transformedMin, transformedMax);
    }

    private static string FindRepoFile(params string[] relativeParts)
    {
        string? directory = TestContext.CurrentContext.WorkDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository test asset.", Path.Combine(relativeParts));
    }
}

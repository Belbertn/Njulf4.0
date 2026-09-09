using Njulf.Assets.Validation;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>Deterministic qualification scene for planar and six-face capture geometry.</summary>
internal static class SampleReflectionLodScene
{
    internal static void ConfigureSettings(RenderSettings settings)
    {
        // The deterministic HDR sequence compares native-resolution checkpoints.
        settings.ResolutionScale = 1;
        settings.DynamicResolution.Enabled = false;
        settings.GlobalIllumination.Enabled = false;
        settings.GlobalIllumination.UseDdgi = false;
        settings.Fog.Enabled = false;
        settings.Bloom.Enabled = false;
        settings.AutoExposure.Enabled = false;
        settings.Reflections.Enabled = true;
        settings.Reflections.Mode = ReflectionMode.StaticProbesAndPlanar;
        settings.Reflections.ImplementationMode = ReflectionImplementationMode.Adaptive;
        settings.Reflections.MaxProbes = 1;
        settings.Reflections.CaptureOnLoad = true;
        settings.Reflections.CaptureIncludesDdgi = false;
        settings.Reflections.MaxProbeCapturesPerFrame = 1;
        settings.Reflections.MaxProbeCaptureFacesPerFrame = 1;
        settings.Reflections.MaxProbePrefilterMipsPerFrame = 1;
        settings.Reflections.ReflectionCaptureGpuBudgetMicroseconds = 50000;
    }

    internal static void Build(Scene scene, MeshManager meshes, MaterialManager materials)
    {
        scene.Name = "Reflection LOD Qualification";
        GPUVertex[] vertices = SampleUvSphereMesh.CreateVertices(48, 96);
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 p = vertices[i].Position;
            float relief = 1 + 0.08f * MathF.Sin(MathF.Atan2(p.Z, p.X) * 9) * MathF.Sin(p.Y * 9);
            vertices[i].Position *= relief;
        }
        MeshHandle sphere = meshes.RegisterMesh(vertices, SampleUvSphereMesh.CreateIndices(48, 96));
        MeshHandle panel = meshes.RegisterMesh(new GPUVertex[]
        {
            Vertex(-1, -1), Vertex(1, -1), Vertex(1, 1), Vertex(-1, 1)
        }, new uint[] { 0, 1, 2, 0, 2, 3 });
        int identity = 0;
        void Add(MeshHandle mesh, MaterialHandle material, string name, Vector3 scale,
            Vector3 position, bool floor = false)
        {
            scene.Add(new RenderObject(meshes.GetResourceView(mesh), materials.GetResourceView(material))
            {
                Id = new Guid(++identity, 0, 0, new byte[8]), Name = name,
                WorldMatrix = Matrix4x4.CreateScale(scale) *
                    (floor ? Matrix4x4.CreateRotationX(-MathF.PI / 2) : Matrix4x4.Identity) *
                    Matrix4x4.CreateTranslation(position)
            });
        }
        MaterialHandle Material(string name, Vector3 color, float metallic, float roughness,
            bool planar = false, bool mirror = false) => materials.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = name, BaseColorFactor = new Vector4(color, 1), MetallicFactor = metallic,
            RoughnessFactor = roughness, DoubleSided = true, AutomaticPlanarReflectionEnabled = planar,
            Extensions = mirror ? MaterialExtensionDefinition.None with
                { CausticCasterPolicy = GiCausticCasterPolicy.Mirror } : MaterialExtensionDefinition.None
        });
        Add(panel, Material("Lod.Mirror", new(0.95f), 1, 0.025f, true, true),
            "Lod.Mirror", new(4, 2, 1), new(0, 2, -3));
        Add(panel, Material("Lod.RoughFloor", new(0.45f), 0.85f, 0.28f, true),
            "Lod.RoughFloor", new(5, 7, 1), new(0, 0, 1), true);
        Add(sphere, Material("Lod.Red", new(0.8f, 0.035f, 0.025f), 0, 0.45f),
            "Lod.Red", new(0.8f), new(-1.6f, 1.1f, 0));
        Add(sphere, Material("Lod.Green", new(0.035f, 0.65f, 0.06f), 0, 0.45f),
            "Lod.Green", new(0.8f), new(1.6f, 1.1f, 2.6f));
        Add(sphere, Material("Lod.Blue", new(0.035f, 0.08f, 0.8f), 0, 0.45f),
            "Lod.Blue", new(0.65f), new(0, 0.85f, 0.5f));
        Add(panel, Material("Lod.ThinSilhouette", new(0.9f, 0.7f, 0.04f), 0, 0.5f),
            "Lod.ThinSilhouette", new(0.025f, 1.3f, 1), new(-0.6f, 1.3f, 1.4f));
        Add(sphere, Material("Lod.ProbeReceiver", new(0.9f), 1, 0.04f),
            "Lod.ProbeReceiver", new(0.55f), new(1.5f, 0.8f, -0.8f));
        scene.Add(new ReflectionProbe
        {
            Id = new Guid(100, 0, 0, new byte[8]), Name = "Lod.SeamProbe",
            Position = new(0, 1, 1), BoxExtents = new(20), BlendDistance = 1, Intensity = 1
        });
    }

    private static GPUVertex Vertex(float x, float y) => new()
    {
        Position = new(x, y, 0), Normal = Vector3.UnitZ, Tangent = new(1, 0, 0, 1),
        TexCoord = new((x + 1) * 0.5f, (y + 1) * 0.5f), Color = GPUVertex.DefaultColor
    };
}

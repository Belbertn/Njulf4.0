using System;
using System.Collections.Generic;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

internal static class SampleVfxShowcaseScene
{
    public static void ConfigureRenderSettings(RenderSettings settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        settings.GlobalIllumination.Enabled = false;
        settings.Environment.Enabled = true;
        settings.Environment.SkyIntensity = 0.45f;
        settings.Environment.DiffuseIntensity = 0.65f;
        settings.Environment.SpecularIntensity = 0.35f;
        settings.Reflections.Enabled = false;
        settings.Fog.Enabled = true;
        settings.Bloom.Enabled = true;
        settings.Bloom.Intensity = 0.18f;
        settings.Particles.Enabled = true;
        settings.AmbientOcclusion.Enabled = true;
    }

    public static IReadOnlyList<ParticleEffectInstance> Configure(
        Scene scene,
        MeshManager meshManager,
        MaterialManager materialManager)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (meshManager == null)
            throw new ArgumentNullException(nameof(meshManager));
        if (materialManager == null)
            throw new ArgumentNullException(nameof(materialManager));

        scene.Name = "Njulf VFX Showcase";
        scene.AmbientLight = new Njulf.Core.Math.Color(0.035f, 0.04f, 0.05f, 1f);

        MeshHandle floorMesh = meshManager.RegisterMesh(CreateFloorVertices(), CreateFloorIndices());
        scene.Add(new RenderObject(floorMesh, CreateFloorMaterial(materialManager))
        {
            Name = "VfxShowcase.Floor",
            WorldMatrix = CoreMatrix4x4.Identity,
            Visible = true
        });

        return SampleVfxEffects.Configure(scene);
    }

    private static MaterialHandle CreateFloorMaterial(MaterialManager materialManager)
    {
        return materialManager.RegisterMaterialDefinition(new MaterialDefinition
        {
            Name = "VfxShowcase.Floor",
            BaseColorFactor = new CoreVector4(0.16f, 0.17f, 0.18f, 1f),
            MetallicFactor = 0f,
            RoughnessFactor = 0.82f
        });
    }

    private static GPUVertex[] CreateFloorVertices()
    {
        const float halfWidth = 5.0f;
        const float nearZ = -3.0f;
        const float farZ = 3.0f;

        return
        [
            CreateFloorVertex(-halfWidth, 0f, nearZ, 0f, 0f),
            CreateFloorVertex(halfWidth, 0f, nearZ, 1f, 0f),
            CreateFloorVertex(halfWidth, 0f, farZ, 1f, 1f),
            CreateFloorVertex(-halfWidth, 0f, farZ, 0f, 1f)
        ];
    }

    private static uint[] CreateFloorIndices()
    {
        return [0u, 2u, 1u, 0u, 3u, 2u];
    }

    private static GPUVertex CreateFloorVertex(float x, float y, float z, float u, float v)
    {
        return new GPUVertex
        {
            Position = new CoreVector3(x, y, z),
            Padding0 = 0f,
            Normal = CoreVector3.UnitY,
            Padding1 = 0f,
            TexCoord = new CoreVector2(u, v),
            TexCoord2 = CoreVector2.Zero,
            Tangent = new CoreVector4(CoreVector3.UnitX, 1f),
            Color = GPUVertex.DefaultColor
        };
    }
}

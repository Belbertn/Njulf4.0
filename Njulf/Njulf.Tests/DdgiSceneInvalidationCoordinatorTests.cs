using System.Numerics;
using System.Reflection;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiSceneInvalidationCoordinatorTests
{
    [Test]
    public void DirtyReasonConstants_PreserveRendererFacadeValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VulkanRenderer.SimpleDdgiDirtyReasonLight,
                Is.EqualTo(DdgiSceneInvalidationCoordinator
                    .SimpleDdgiDirtyReasonLight));
            Assert.That(VulkanRenderer.SimpleDdgiDirtyReasonEmissive,
                Is.EqualTo(DdgiSceneInvalidationCoordinator
                    .SimpleDdgiDirtyReasonEmissive));
            Assert.That(VulkanRenderer.SimpleDdgiDirtyReasonDynamicGeometry,
                Is.EqualTo(DdgiSceneInvalidationCoordinator
                    .SimpleDdgiDirtyReasonDynamicGeometry));
        });
    }

    [Test]
    public void WarmStartIdentityCache_DoesNotStronglyRetainScene()
    {
        FieldInfo? sceneCache = typeof(DdgiSceneInvalidationCoordinator)
            .GetField(
                "_simpleDdgiWarmStartIdentityScene",
                BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo[] strongSceneFields = typeof(DdgiSceneInvalidationCoordinator)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => typeof(Scene).IsAssignableFrom(field.FieldType))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(sceneCache, Is.Not.Null);
            Assert.That(sceneCache!.FieldType,
                Is.EqualTo(typeof(WeakReference<Scene>)));
            Assert.That(strongSceneFields, Is.Empty);
        });
    }

    [Test]
    public void LightingSignature_IsStableAndTracksEmissiveRevision()
    {
        Light directional = new()
        {
            Type = LightType.Directional,
            Direction = -Vector3.UnitY,
            Color = Vector3.One,
            Intensity = 2.0f,
            CastsShadows = true,
            ShadowStrength = 1.0f
        };
        Light[] lights = [directional];
        var snapshot = new LightFrameSnapshot(
            lights,
            lights.Length,
            directionalLightCount: 1,
            localLightCount: 0,
            firstShadowCastingDirectionalLightIndex: 0,
            firstShadowCastingDirectionalLight: directional,
            revision: 1);

        ulong first = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiLightingSignature(snapshot, 7u);
        ulong repeated = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiLightingSignature(snapshot, 7u);
        ulong revised = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiLightingSignature(snapshot, 8u);

        Assert.Multiple(() =>
        {
            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(revised, Is.Not.EqualTo(first));
        });
    }

    [Test]
    public void EnvironmentSignature_TracksAuthoredSourceIdentity()
    {
        var first = new EnvironmentSettings
        {
            Enabled = true,
            SourcePath = "studio-a.hdr",
            SkyIntensity = 1.25f
        };
        var second = new EnvironmentSettings
        {
            Enabled = true,
            SourcePath = "studio-b.hdr",
            SkyIntensity = 1.25f
        };

        ulong firstSignature = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiEnvironmentSignature(first);
        ulong repeatedSignature = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiEnvironmentSignature(first);
        ulong secondSignature = DdgiSceneInvalidationCoordinator
            .CreateSimpleDdgiEnvironmentSignature(second);

        Assert.Multiple(() =>
        {
            Assert.That(repeatedSignature, Is.EqualTo(firstSignature));
            Assert.That(secondSignature, Is.Not.EqualTo(firstSignature));
        });
    }
}

using System.IO;
using System.Text;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PerformanceCaptureMetadataProviderTests
{
    [Test]
    public void SceneHashes_KeepAssetAndRenderedStateResponsibilitiesSeparate()
    {
        using var sceneData = new SceneRenderingData
        {
            SceneContentRevision = 17,
            GiTransportMaterialRevision = 3,
            DdgiEmissiveSourceRevision = 5,
            DrawPacketRevision = 11,
            DirectionalShadowMeshletDrawSignature = 13,
            LocalShadowMeshletDrawSignature = 19,
            ObjectCount = 23,
            MeshletCount = 29,
            MaterialCount = 31,
            TextureCount = 37,
            LightCount = 41,
            DirectionalLightCount = 1,
            LocalLightCount = 40,
            GeometryDecalObjectCount = 43,
            CaptureSceneName = " Bistro "
        };

        string asset = PerformanceCaptureHashing.ComputeSceneAssetHash(sceneData);
        string state = PerformanceCaptureHashing.ComputeSceneStateHash(sceneData);
        sceneData.DrawPacketRevision++;
        sceneData.MeshletCount++;

        Assert.Multiple(() =>
        {
            Assert.That(
                PerformanceCaptureHashing.ComputeSceneAssetHash(sceneData),
                Is.EqualTo(asset));
            Assert.That(
                PerformanceCaptureHashing.ComputeSceneStateHash(sceneData),
                Is.Not.EqualTo(state));
            Assert.That(asset, Does.Match("^sha256:[0-9a-f]{64}$"));
        });
    }

    [Test]
    public void Normalization_PreservesExistingUnavailableAndRevisionRules()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                PerformanceCaptureHashing.ResolveScenario(" unknown-scenario "),
                Is.EqualTo(
                    "unavailable:active-scenario-not-supplied-by-renderer-client"));
            Assert.That(
                PerformanceCaptureHashing.ResolveSceneKind("", " Bistro "),
                Is.EqualTo("Bistro"));
            Assert.That(
                PerformanceCaptureHostIdentityResolver.ResolveCommit(
                    "sha:ABCDEF123",
                    "ignored+0123456"),
                Is.EqualTo("abcdef123"));
            Assert.That(
                PerformanceCaptureHostIdentityResolver.ResolveCommit(
                    "not-a-revision",
                    "1.0.0+ABCDEF0"),
                Is.EqualTo("abcdef0"));
            Assert.That(
                PerformanceCaptureHostIdentityResolver.ResolveDirtyWorktreeState(
                    " DIRTY "),
                Is.EqualTo("dirty"));
            Assert.That(
                PerformanceCaptureHostIdentityResolver.ResolveDirtyWorktreeState(
                    "maybe"),
                Is.EqualTo(
                    "unavailable:invalid-dirty-worktree-state"));
        });
    }

    [Test]
    public void ExecutableHash_UsesTheExistingSequentialFileDigest()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "njulf-capture", Encoding.UTF8);

            string expected = PerformanceCaptureHostIdentityResolver.HashFile(path);
            string actual =
                PerformanceCaptureHostIdentityResolver.ResolveExecutableHash(path);

            Assert.That(actual, Is.EqualTo(expected));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void SceneAndCameraLifecycle_PreservesResetCutAndGuardedAgeOrder()
    {
        var provider = new PerformanceCaptureMetadataProvider(
            new PerformanceCaptureHostIdentityResolver(
                typeof(PerformanceCaptureMetadataProviderTests).Assembly,
                typeof(PerformanceCaptureMetadataProvider).Assembly));
        provider.SceneKind = "Bistro";
        provider.Scenario = "presentation";
        using var sceneData = new SceneRenderingData
        {
            SceneContentRevision = 7
        };
        var camera = new FirstPersonCamera(
            new Vector3(1f, 2f, 3f),
            yaw: 1.89f,
            pitch: -0.145f);

        provider.ApplySceneLabels(sceneData, " Bistro Exterior ");
        PerformanceCaptureFramePreparation first =
            provider.ObserveSceneAndCamera(sceneData, camera, 10);
        provider.ApplyCameraCut(sceneData, cameraCut: true);
        PerformanceCaptureFramePreparation steady =
            provider.ObserveSceneAndCamera(sceneData, camera, 12);
        provider.ApplyCameraCut(sceneData, cameraCut: false);
        sceneData.SceneContentRevision = 8;
        PerformanceCaptureFramePreparation changed =
            provider.ObserveSceneAndCamera(sceneData, camera, 20);
        provider.ApplyCameraCut(sceneData, cameraCut: true);
        PerformanceCaptureFramePreparation backward =
            provider.ObserveSceneAndCamera(sceneData, camera, 19);

        Assert.Multiple(() =>
        {
            Assert.That(first.SceneChanged, Is.True);
            Assert.That(first.FramesSinceSceneLoad, Is.Zero);
            Assert.That(steady.SceneChanged, Is.False);
            Assert.That(steady.FramesSinceSceneLoad, Is.EqualTo(2));
            Assert.That(changed.SceneChanged, Is.True);
            Assert.That(changed.FramesSinceSceneLoad, Is.Zero);
            Assert.That(backward.FramesSinceSceneLoad, Is.Zero);
            Assert.That(sceneData.CaptureCameraCutSerial, Is.EqualTo(1));
            Assert.That(sceneData.CaptureScenario, Is.EqualTo("presentation"));
            Assert.That(sceneData.CaptureSceneName,
                Is.EqualTo(" Bistro Exterior "));
            Assert.That(sceneData.CaptureCameraPitchRadians,
                Is.EqualTo(-0.145f).Within(1.0e-6f));
        });
    }
}

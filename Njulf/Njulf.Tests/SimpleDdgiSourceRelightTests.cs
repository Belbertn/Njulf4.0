using System.Numerics;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSourceRelightTests
{
    private const uint SourceRefreshFlag = 1u << 13;

    [Test]
    public void UpdateFlags_RoundTripEnvironmentModeWithoutAliasingRayCount()
    {
        uint mode = SimpleDdgiVolumeManager.EncodeSourceRefreshMode(
            SimpleDdgiSourceRefreshMode.EnvironmentMissRelight);
        uint flags = SourceRefreshFlag | mode | (256u << 16);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.DecodeSourceRefreshMode(flags),
                Is.EqualTo(SimpleDdgiSourceRefreshMode.EnvironmentMissRelight));
            Assert.That((flags >> 16) & 0x1ffu, Is.EqualTo(256u));
            Assert.That(mode & (0x1ffu << 16), Is.Zero);
        });
    }

    [Test]
    public void UnfinishedFullRefresh_CannotBeDowngradedByEnvironmentEdit()
    {
        Assert.That(
            SimpleDdgiVolumeManager.CombineSourceRefreshModes(
                SimpleDdgiSourceRefreshMode.FullTrace,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight),
            Is.EqualTo(SimpleDdgiSourceRefreshMode.FullTrace));
    }

    [Test]
    public void ConsecutiveEnvironmentEdits_RemainCacheRelightable()
    {
        Assert.That(
            SimpleDdgiVolumeManager.CombineSourceRefreshModes(
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight,
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight),
            Is.EqualTo(SimpleDdgiSourceRefreshMode.EnvironmentMissRelight));
    }

    [Test]
    public void MixedPartialRefreshContracts_FallBackToFullTrace()
    {
        Assert.That(
            SimpleDdgiVolumeManager.CombineSourceRefreshModes(
                SimpleDdgiSourceRefreshMode.EnvironmentMissRelight,
                SimpleDdgiSourceRefreshMode.SegmentSelective),
            Is.EqualTo(SimpleDdgiSourceRefreshMode.FullTrace));
    }

    [Test]
    public void PublishedLiveGeneration_ReopensCachedRelightAfterLocalRepair()
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveSourceRefreshModeForNewLightingCohort(
                previousGenerationComplete: false,
                previousRadiometricGenerationPublished: true,
                previousLivePropagationGenerationPublished: true,
                currentMode: SimpleDdgiSourceRefreshMode.FullTrace,
                requestedMode: SimpleDdgiSourceRefreshMode.CachedHitRelight),
            Is.EqualTo(SimpleDdgiSourceRefreshMode.CachedHitRelight));
    }

    [Test]
    public void PublishedGenerationWithoutLiveBoundary_PreservesFullRepair()
    {
        Assert.That(
            SimpleDdgiVolumeManager.ResolveSourceRefreshModeForNewLightingCohort(
                previousGenerationComplete: false,
                previousRadiometricGenerationPublished: true,
                previousLivePropagationGenerationPublished: false,
                currentMode: SimpleDdgiSourceRefreshMode.FullTrace,
                requestedMode: SimpleDdgiSourceRefreshMode.CachedHitRelight),
            Is.EqualTo(SimpleDdgiSourceRefreshMode.FullTrace));
    }

    [Test]
    public void UpdateFlags_RoundTripCachedHitModeWithoutAliasingRayCount()
    {
        uint mode = SimpleDdgiVolumeManager.EncodeSourceRefreshMode(
            SimpleDdgiSourceRefreshMode.CachedHitRelight);
        uint flags = SourceRefreshFlag | mode | (128u << 16);

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.DecodeSourceRefreshMode(flags),
                Is.EqualTo(SimpleDdgiSourceRefreshMode.CachedHitRelight));
            Assert.That((flags >> 16) & 0x1ffu, Is.EqualTo(128u));
            Assert.That(mode & (0x1ffu << 16), Is.Zero);
        });
    }

    [Test]
    public void SoleDirectionalRadianceEdit_ProducesExactComponentScale()
    {
        Light previous = CreateDirectionalLight();
        Light current = previous;
        current.Color = new Vector3(0.5f, 2.0f, 0.25f);
        current.Intensity = 8.0f;

        bool eligible = DdgiSceneInvalidationCoordinator
            .TryComputeSoleDirectionalRelightScale(
            previous,
            current,
            out CoreVector3 scale);

        Assert.Multiple(() =>
        {
            Assert.That(eligible, Is.True);
            Assert.That(scale.X, Is.EqualTo(2.0f).Within(1.0e-6f));
            Assert.That(scale.Y, Is.EqualTo(16.0f).Within(1.0e-6f));
            Assert.That(scale.Z, Is.EqualTo(2.0f).Within(1.0e-6f));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SoleDirectionalRelight_RejectsVisibilityContractChanges(
        bool changeDirection)
    {
        Light previous = CreateDirectionalLight();
        Light current = previous;
        current.Intensity *= 2.0f;
        if (changeDirection)
            current.Direction = Vector3.Normalize(new Vector3(0.1f, -1.0f, 0.0f));
        else
            current.ShadowStrength = 0.75f;

        Assert.That(
            DdgiSceneInvalidationCoordinator
                .TryComputeSoleDirectionalRelightScale(
                previous,
                current,
                out _),
            Is.False);
    }

    [Test]
    public void SoleDirectionalRelight_RejectsEnergyIntroducedIntoZeroChannel()
    {
        Light previous = CreateDirectionalLight();
        previous.Color = new Vector3(1.0f, 0.0f, 1.0f);
        Light current = previous;
        current.Color = Vector3.One;

        Assert.That(
            DdgiSceneInvalidationCoordinator
                .TryComputeSoleDirectionalRelightScale(
                previous,
                current,
                out _),
            Is.False);
    }

    [Test]
    public void SourceRelightScale_InvalidInputFallsBackToIdentity()
    {
        CoreVector3 scale = SimpleDdgiVolumeManager.SanitizeSourceRelightScale(
            SimpleDdgiSourceRefreshMode.CachedHitRelight,
            new CoreVector3(float.NaN, 2.0f, 3.0f));
        CoreVector3 unused = SimpleDdgiVolumeManager.SanitizeSourceRelightScale(
            SimpleDdgiSourceRefreshMode.FullTrace,
            CoreVector3.Zero);

        Assert.Multiple(() =>
        {
            Assert.That(scale, Is.EqualTo(CoreVector3.One));
            Assert.That(unused, Is.EqualTo(CoreVector3.One));
        });
    }

    [Test]
    public void GlobalLightSignature_IgnoresLocalMotionButTracksDirectionalEdits()
    {
        Light directional = CreateDirectionalLight();
        Light local = new()
        {
            Type = LightType.Point,
            Position = new Vector3(1.0f, 2.0f, 3.0f),
            Color = Vector3.One,
            Intensity = 4.0f,
            Range = 6.0f
        };
        LightFrameSnapshot initial = CreateSnapshot(directional, local);

        local.Position += new Vector3(7.0f, 0.0f, -5.0f);
        LightFrameSnapshot localMoved = CreateSnapshot(directional, local);
        directional.Intensity *= 1.5f;
        LightFrameSnapshot sunChanged = CreateSnapshot(directional, local);

        ulong initialSignature =
            DdgiSceneInvalidationCoordinator
                .CreateSimpleDdgiGlobalLightSignature(initial);
        Assert.Multiple(() =>
        {
            Assert.That(
                DdgiSceneInvalidationCoordinator
                    .CreateSimpleDdgiGlobalLightSignature(localMoved),
                Is.EqualTo(initialSignature));
            Assert.That(
                DdgiSceneInvalidationCoordinator
                    .CreateSimpleDdgiGlobalLightSignature(sunChanged),
                Is.Not.EqualTo(initialSignature));
        });
    }

    [Test]
    public void RegionalRadiometricChange_DetectsLightAndEmissionWithoutGeometry()
    {
        var bounds = new CoreBoundingBox(
            new CoreVector3(-1.0f),
            new CoreVector3(1.0f));

        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ContainsRegionalRadiometricChange(
                    new[]
                    {
                        new DdgiDirtyRegion(
                            bounds,
                            DdgiDirtyReason.LocalLightChanged)
                    }),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ContainsRegionalRadiometricChange(
                    new[]
                    {
                        new DdgiDirtyRegion(
                            bounds,
                            DdgiDirtyReason.EmissiveChanged)
                    }),
                Is.True);
            Assert.That(
                SimpleDdgiVolumeManager.ContainsRegionalRadiometricChange(
                    new[]
                    {
                        new DdgiDirtyRegion(
                            bounds,
                            DdgiDirtyReason.TransformChanged)
                    }),
                Is.False);
        });
    }

    [Test]
    public void RadiometricSourceTarget_UsesBoundedTransitionCadence()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SimpleDdgiVolumeManager.ResolveRadiometricSourceProbeTarget(
                    steadyStateTarget: 8,
                    participatingProbeCount: 16_266,
                    lightingTransitionActive: false,
                    transitionProbeBudget: 32),
                Is.EqualTo(8));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveRadiometricSourceProbeTarget(
                    steadyStateTarget: 8,
                    participatingProbeCount: 16_266,
                    lightingTransitionActive: true,
                    transitionProbeBudget: 32),
                Is.EqualTo(32));
            Assert.That(
                SimpleDdgiVolumeManager.ResolveRadiometricSourceProbeTarget(
                    steadyStateTarget: 8,
                    participatingProbeCount: 12,
                    lightingTransitionActive: true,
                    transitionProbeBudget: 32),
                Is.EqualTo(12));
        });
    }

    private static LightFrameSnapshot CreateSnapshot(
        Light directional,
        Light local)
    {
        Light[] lights = [directional, local];
        return new LightFrameSnapshot(
            lights,
            lights.Length,
            directionalLightCount: 1,
            localLightCount: 1,
            firstShadowCastingDirectionalLightIndex: 0,
            firstShadowCastingDirectionalLight: directional,
            revision: 1);
    }

    private static Light CreateDirectionalLight() => new()
    {
        Position = Vector3.Zero,
        Intensity = 2.0f,
        Color = new Vector3(1.0f, 0.5f, 0.5f),
        Range = 100.0f,
        Direction = -Vector3.UnitY,
        SpotAngle = 0.5f,
        Type = LightType.Directional,
        CastsShadows = true,
        ShadowStrength = 1.0f,
        ShadowMapSizeOverride = 2048u,
        ShadowNearPlane = 0.1f,
        ShadowFarPlane = 200.0f,
        ShadowPriority = 7
    };
}

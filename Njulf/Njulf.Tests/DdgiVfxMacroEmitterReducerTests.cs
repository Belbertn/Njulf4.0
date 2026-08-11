using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiVfxMacroEmitterReducerTests
{
    [Test]
    public void SustainedEmitter_IsAdmittedAfterBoundedHysteresis()
    {
        Scene scene = SceneWithEmitter(new ParticleEmitterDefinition
        {
            Looping = true,
            SpawnRatePerSecond = 30.0f,
            EmissiveOverLife = ParticleCurve.Constant(4.0f),
            GlobalIlluminationPower = new Vector3(120.0f, 60.0f, 20.0f)
        });
        var reducer = new DdgiVfxMacroEmitterReducer();
        var output = new DdgiVfxMacroEmitter[4];

        DdgiVfxMacroReductionResult first = reducer.Reduce(scene, 0.05f, output);
        DdgiVfxMacroReductionResult second = reducer.Reduce(scene, 0.05f, output);

        Assert.Multiple(() =>
        {
            Assert.That(first.SourceCount, Is.Zero);
            Assert.That(second.SourceCount, Is.EqualTo(1));
            Assert.That(output[0].IntegratedPower, Is.EqualTo(new Vector3(120.0f, 60.0f, 20.0f)));
            Assert.That(output[0].AuthoredPower, Is.True);
            Assert.That(second.Revision, Is.GreaterThan(0));
        });
    }

    [Test]
    public void AuthoredPower_IsIndependentOfParticleCountAndSpawnTessellation()
    {
        Vector3 authoredPower = new(80.0f, 30.0f, 10.0f);
        Scene sparse = SceneWithEmitter(new ParticleEmitterDefinition
        {
            GlobalIlluminationEmission = ParticleGiEmissionMode.Force,
            GlobalIlluminationPower = authoredPower,
            SpawnRatePerSecond = 2.0f,
            MaxParticles = 8,
            EmissiveOverLife = ParticleCurve.Constant(2.0f)
        });
        Scene dense = SceneWithEmitter(new ParticleEmitterDefinition
        {
            GlobalIlluminationEmission = ParticleGiEmissionMode.Force,
            GlobalIlluminationPower = authoredPower,
            SpawnRatePerSecond = 2000.0f,
            MaxParticles = 100000,
            EmissiveOverLife = ParticleCurve.Constant(2.0f)
        });
        var sparseOutput = new DdgiVfxMacroEmitter[1];
        var denseOutput = new DdgiVfxMacroEmitter[1];

        new DdgiVfxMacroEmitterReducer().Reduce(sparse, 0.0f, sparseOutput);
        new DdgiVfxMacroEmitterReducer().Reduce(dense, 0.0f, denseOutput);

        Assert.That(denseOutput[0].IntegratedPower, Is.EqualTo(sparseOutput[0].IntegratedPower));
    }

    [Test]
    public void BriefBurst_IsExcludedUnlessExplicitlyForced()
    {
        ParticleEmitterDefinition AutoBurst() => new()
        {
            Looping = false,
            DurationSeconds = 0.1f,
            SpawnRatePerSecond = 0.0f,
            BurstCount = 20,
            EmissiveOverLife = ParticleCurve.Constant(8.0f),
            GlobalIlluminationPower = new Vector3(100.0f)
        };
        var output = new DdgiVfxMacroEmitter[2];
        DdgiVfxMacroReductionResult automatic =
            new DdgiVfxMacroEmitterReducer().Reduce(SceneWithEmitter(AutoBurst()), 1.0f, output);
        ParticleEmitterDefinition forcedDefinition = new()
        {
            Looping = false,
            DurationSeconds = 0.1f,
            SpawnRatePerSecond = 0.0f,
            BurstCount = 20,
            EmissiveOverLife = ParticleCurve.Constant(8.0f),
            GlobalIlluminationPower = new Vector3(100.0f),
            GlobalIlluminationEmission = ParticleGiEmissionMode.Force
        };
        DdgiVfxMacroReductionResult forced =
            new DdgiVfxMacroEmitterReducer().Reduce(SceneWithEmitter(forcedDefinition), 0.0f, output);

        Assert.Multiple(() =>
        {
            Assert.That(automatic.SourceCount, Is.Zero);
            Assert.That(automatic.RejectedTransientCount, Is.EqualTo(1));
            Assert.That(forced.SourceCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MovingEmitter_ReportsSweptBoundsAndNewRevision()
    {
        Scene scene = SceneWithEmitter(new ParticleEmitterDefinition
        {
            GlobalIlluminationEmission = ParticleGiEmissionMode.Force,
            GlobalIlluminationPower = new Vector3(10.0f),
            SpawnShape = ParticleSpawnShape.Sphere(1.0f),
            EmissiveOverLife = ParticleCurve.Constant(2.0f)
        });
        ParticleEffectInstance instance = scene.ParticleEffects[0];
        var reducer = new DdgiVfxMacroEmitterReducer();
        var output = new DdgiVfxMacroEmitter[1];
        DdgiVfxMacroReductionResult first = reducer.Reduce(scene, 0.0f, output);
        BoundingBox oldBounds = output[0].CurrentBounds;

        instance.WorldMatrix = Matrix4x4.CreateTranslation(new Vector3(10.0f, 0.0f, 0.0f));
        DdgiVfxMacroReductionResult second = reducer.Reduce(scene, 1.0f / 60.0f, output);

        Assert.Multiple(() =>
        {
            Assert.That(second.Revision, Is.GreaterThan(first.Revision));
            Assert.That(output[0].SweptBounds.Min.X, Is.LessThanOrEqualTo(oldBounds.Min.X));
            Assert.That(output[0].SweptBounds.Max.X, Is.GreaterThanOrEqualTo(output[0].CurrentBounds.Max.X));
        });
    }

    [Test]
    public void PackedMacroSource_PreservesShapeAndIntegratedPower()
    {
        var macro = new DdgiVfxMacroEmitter(
            1,
            2,
            DdgiVfxMacroShape.Line,
            new Vector3(1.0f, 2.0f, 3.0f),
            Vector3.UnitX,
            new Vector3(0.1f, 4.0f, 0.2f),
            new Vector3(9.0f, 8.0f, 7.0f),
            new BoundingBox(new Vector3(-1.0f), new Vector3(1.0f)),
            new BoundingBox(new Vector3(-2.0f), new Vector3(2.0f)),
            true);

        GPUDdgiEmissiveSource packed = DdgiVfxMacroEmitterReducer.PackSource(macro);
        DdgiEmissiveSourceFlags flags = DdgiEmissiveTriangleTable.DecodeFlags(packed);
        uint shape = ((uint)flags & (uint)DdgiEmissiveSourceFlags.MacroShapeMask) >> 8;

        Assert.Multiple(() =>
        {
            Assert.That(flags.HasFlag(DdgiEmissiveSourceFlags.MacroEmitter), Is.True);
            Assert.That(shape, Is.EqualTo((uint)DdgiVfxMacroShape.Line));
            Assert.That(packed.RadianceSelectionProbability.X, Is.EqualTo(9.0f));
            Assert.That(packed.RadianceSelectionProbability.Y, Is.EqualTo(8.0f));
            Assert.That(packed.RadianceSelectionProbability.Z, Is.EqualTo(7.0f));
        });
    }

    private static Scene SceneWithEmitter(ParticleEmitterDefinition emitter)
    {
        var scene = new Scene();
        scene.Add(new ParticleEffectInstance(new ParticleEffect
        {
            Emitters = new[] { emitter }
        }));
        return scene;
    }
}

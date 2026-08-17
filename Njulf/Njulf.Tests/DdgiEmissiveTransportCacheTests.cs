using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiEmissiveTransportCacheTests
{
    [Test]
    public void UnchangedRevisionKey_ReusesBoundedPayloadWithoutRebuilding()
    {
        var cache = new DdgiEmissiveTableCache(capacity: 4);
        var destination = new GPUDdgiEmissiveSource[4];
        var key = new DdgiEmissiveTableCacheKey(
            Guid.NewGuid(),
            SceneContentRevision: 7,
            MaterialDataRevision: 11,
            TriangleSampling: true,
            TriangleBudget: 4);
        int buildCount = 0;

        DdgiEmissiveTableBuildResult Resolve()
        {
            if (cache.TryGet(key, destination, out DdgiEmissiveTableBuildResult cached))
                return cached;

            buildCount++;
            destination[0] = CreateSource(3.0f);
            var rebuilt = new DdgiEmissiveTableBuildResult(
                Count: 1,
                PayloadSignature: 0x1234,
                new DdgiEmissiveTriangleTableStats(1, 1, 3.0, 3.0, 0.0),
                SkippedSkinnedObjectCount: 0,
                SkippedSkinnedImportance: 0.0);
            cache.Store(key, destination.AsSpan(0, rebuilt.Count), rebuilt);
            return rebuilt;
        }

        DdgiEmissiveTableBuildResult first = Resolve();
        destination[0] = default;
        DdgiEmissiveTableBuildResult second = Resolve();
        DdgiEmissiveTableCacheDiagnostics diagnostics = cache.Diagnostics;

        Assert.Multiple(() =>
        {
            Assert.That(buildCount, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(destination[0].RadianceSelectionProbability.X, Is.EqualTo(3.0f));
            Assert.That(diagnostics.HitCount, Is.EqualTo(1));
            Assert.That(diagnostics.MissCount, Is.EqualTo(1));
            Assert.That(diagnostics.RebuildCount, Is.EqualTo(1));
            Assert.That(diagnostics.InvalidationCount, Is.Zero);
            Assert.That(diagnostics.LastLookupWasHit, Is.True);
        });
    }

    [Test]
    public void MetadataOnlyLookup_DoesNotCopyPayloadOnTheResidentBufferHotPath()
    {
        var cache = new DdgiEmissiveTableCache(capacity: 2);
        var key = new DdgiEmissiveTableCacheKey(Guid.NewGuid(), 2, 3, true, 2);
        GPUDdgiEmissiveSource[] payload = [CreateSource(7.0f)];
        var result = new DdgiEmissiveTableBuildResult(1, 20, default, 0, 0.0);
        cache.Store(key, payload, result);
        var destination = new[] { CreateSource(99.0f), CreateSource(99.0f) };

        bool hit = cache.TryGet(key, out DdgiEmissiveTableBuildResult cached);

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.True);
            Assert.That(cached, Is.EqualTo(result));
            Assert.That(destination[0].RadianceSelectionProbability.X, Is.EqualTo(99.0f));
            Assert.That(cache.Diagnostics.HitCount, Is.EqualTo(1));
        });

        cache.CopyPayloadTo(destination);
        Assert.That(destination[0].RadianceSelectionProbability.X, Is.EqualTo(7.0f));
    }

    [Test]
    public void RelevantSceneMaterialModeAndBudgetRevisions_InvalidatePromptly()
    {
        Guid sceneId = Guid.NewGuid();
        var baseline = new DdgiEmissiveTableCacheKey(sceneId, 4, 8, true, 64);
        DdgiEmissiveTableCacheKey[] changedKeys =
        [
            baseline with { SceneId = Guid.NewGuid() },
            baseline with { SceneContentRevision = 5 },
            baseline with { MaterialDataRevision = 9 },
            baseline with { TriangleSampling = false },
            baseline with { TriangleBudget = 32 }
        ];

        foreach (DdgiEmissiveTableCacheKey changedKey in changedKeys)
        {
            var cache = new DdgiEmissiveTableCache(64);
            GPUDdgiEmissiveSource[] payload = [CreateSource(1.0f)];
            var result = new DdgiEmissiveTableBuildResult(1, 1, default, 0, 0.0);
            cache.Store(baseline, payload, result);

            bool hit = cache.TryGet(
                changedKey,
                new GPUDdgiEmissiveSource[64],
                out _);
            cache.Store(changedKey, payload, result with { PayloadSignature = 2 });

            Assert.Multiple(() =>
            {
                Assert.That(hit, Is.False, changedKey.ToString());
                Assert.That(cache.Diagnostics.MissCount, Is.EqualTo(1), changedKey.ToString());
                Assert.That(cache.Diagnostics.RebuildCount, Is.EqualTo(2), changedKey.ToString());
                Assert.That(cache.Diagnostics.InvalidationCount, Is.EqualTo(1), changedKey.ToString());
            });
        }
    }

    [Test]
    public void Clear_DropsPayloadAndKeepsMemoryBounded()
    {
        var cache = new DdgiEmissiveTableCache(2);
        var key = new DdgiEmissiveTableCacheKey(Guid.NewGuid(), 1, 1, true, 2);
        GPUDdgiEmissiveSource[] payload = [CreateSource(1.0f), CreateSource(2.0f)];
        cache.Store(
            key,
            payload,
            new DdgiEmissiveTableBuildResult(2, 10, default, 0, 0.0));

        cache.Clear();
        bool hit = cache.TryGet(key, new GPUDdgiEmissiveSource[2], out _);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Capacity, Is.EqualTo(2));
            Assert.That(hit, Is.False);
            Assert.That(cache.Diagnostics.HasValue, Is.False);
            Assert.That(cache.Diagnostics.InvalidationCount, Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => cache.Store(
                key,
                new GPUDdgiEmissiveSource[3],
                new DdgiEmissiveTableBuildResult(3, 11, default, 0, 0.0)));
        });
    }

    [Test]
    public void OwnershipPolicy_EnablesExactlyOneNextEventEstimator()
    {
        DdgiEmissiveEstimatorOwnership triangle =
            DdgiEmissiveTransportContract.ResolveOwnership(
                triangleSampling: true,
                cachedMultiBounce: true);
        DdgiEmissiveEstimatorOwnership rollback =
            DdgiEmissiveTransportContract.ResolveOwnership(
                triangleSampling: false,
                cachedMultiBounce: false);
        DdgiEmissiveEstimatorOwnership duplicate =
            triangle | DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent;

        Assert.Multiple(() =>
        {
            Assert.That(DdgiEmissiveTransportContract.IsValid(triangle), Is.True);
            Assert.That(DdgiEmissiveTransportContract.IsValid(rollback), Is.True);
            Assert.That(DdgiEmissiveTransportContract.IsValid(duplicate), Is.False);
            Assert.That(
                triangle.HasFlag(DdgiEmissiveEstimatorOwnership.TriangleNextEvent),
                Is.True);
            Assert.That(
                triangle.HasFlag(DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent),
                Is.False);
            Assert.That(
                rollback.HasFlag(DdgiEmissiveEstimatorOwnership.ProxyRollbackNextEvent),
                Is.True);
            Assert.That(
                rollback.HasFlag(DdgiEmissiveEstimatorOwnership.TriangleNextEvent),
                Is.False);
        });
    }

    [Test]
    public void SceneLinearRadiance_IsLinearAndIndependentOfReceiverTerms()
    {
        Vector3 baseRadiance = new(0.25f, 0.5f, 1.0f);
        Vector3 half = DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
            baseRadiance * 0.5f);
        Vector3 one = DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
            baseRadiance);
        Vector3 ten = DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
            baseRadiance * 10.0f);

        Assert.Multiple(() =>
        {
            Assert.That(half, Is.EqualTo(one * 0.5f));
            Assert.That(ten, Is.EqualTo(one * 10.0f));
            Assert.That(
                DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(Vector3.Zero),
                Is.EqualTo(Vector3.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
                    new Vector3(float.NaN, 1.0f, 1.0f)));
        });
    }

    [Test]
    public void SceneLinearRadiance_UsesExplicitFormatSafetyForOverflowAndRejectsNonFiniteInputs()
    {
        float maximum = DdgiEmissiveTransportContract.MaximumSceneLinearRadiance;
        Vector3 storageSafe =
            DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
                new Vector3(maximum + 1_024.0f, maximum, -1.0f));

        Assert.Multiple(() =>
        {
            Assert.That(
                storageSafe,
                Is.EqualTo(new Vector3(maximum, maximum, 0.0f)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
                    new Vector3(float.NaN, 1.0f, 1.0f)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                DdgiEmissiveTransportContract.ResolveDirectSurfaceHitRadiance(
                    new Vector3(float.PositiveInfinity, 1.0f, 1.0f)));
        });
    }

    [Test]
    public void TriangleTable_SelectsByEmittedPowerAndEncodesExclusiveTriangleOwnership()
    {
        DdgiEmissiveTriangleCandidate[] candidates =
        [
            CreateCandidate(radiance: 1.0f, stableKey: 10),
            CreateCandidate(radiance: 4.0f, stableKey: 20),
            CreateCandidate(radiance: 2.0f, stableKey: 30)
        ];
        var payload = new GPUDdgiEmissiveSource[2];

        DdgiEmissiveTriangleTableStats stats =
            DdgiEmissiveTriangleTable.Build(candidates, payload);

        float probabilitySum = payload.Sum(source => source.RadianceSelectionProbability.W);
        Assert.Multiple(() =>
        {
            Assert.That(stats.CandidateCount, Is.EqualTo(3));
            Assert.That(stats.SelectedCount, Is.EqualTo(2));
            Assert.That(stats.SelectedImportance, Is.GreaterThan(stats.SkippedImportance));
            Assert.That(stats.SkippedEnergyFraction, Is.GreaterThan(0.0f).And.LessThan(1.0f));
            Assert.That(probabilitySum, Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(
                payload.Select(source => source.RadianceSelectionProbability.X),
                Is.EquivalentTo(new[] { 4.0f, 2.0f }));
            Assert.That(
                payload.All(source =>
                    DdgiEmissiveTriangleTable.DecodeFlags(source).HasFlag(
                        DdgiEmissiveSourceFlags.Triangle)),
                Is.True);
            Assert.That(
                payload.Any(source =>
                    DdgiEmissiveTriangleTable.DecodeFlags(source).HasFlag(
                        DdgiEmissiveSourceFlags.ProxyRollback)),
                Is.False);
            Assert.That(
                payload.All(source =>
                    DdgiEmissiveTriangleTable.DecodeAliasIndex(source) < stats.SelectedCount),
                Is.True);
        });
    }

    [Test]
    public void DynamicSurfaceSidecars_FollowSelectionSpatialOrderingAndCacheCopies()
    {
        DdgiEmissiveTriangleCandidate[] candidates =
        [
            CreateCandidateWithSurface(new Vector3(20.0f, 0.0f, 0.0f), 1.0f, 10, 101),
            CreateCandidateWithSurface(new Vector3(-20.0f, 0.0f, 0.0f), 4.0f, 20, 202),
            CreateCandidateWithSurface(new Vector3(0.0f, 0.0f, 0.0f), 2.0f, 30, 303)
        ];
        var sources = new GPUDdgiEmissiveSource[3];
        var surfaces = new GPUDdgiEmissiveSurface[3];
        DdgiEmissiveTriangleTableStats stats = DdgiEmissiveTriangleTable.Build(
            candidates,
            sources,
            surfaces);

        // The table starts in importance order. Give the spatial builder the
        // same positive weights used by its rebuilt global alias proposal.
        double[] importance = sources
            .Select(source => (double)source.RadianceSelectionProbability.X)
            .ToArray();
        var sourceSetBuilder = new DdgiEmissiveSourceSetBuilder(3);
        sourceSetBuilder.OrderAndRebuildAlias(sources, surfaces, importance);

        for (int index = 0; index < sources.Length; index++)
        {
            uint materialIndex = BitConverter.SingleToUInt32Bits(
                surfaces[index].MaterialAndVertexAlpha.X);
            float encodedMaterial = sources[index].RadianceSelectionProbability.Y;
            Assert.That(materialIndex, Is.EqualTo((uint)encodedMaterial));
        }

        var cache = new DdgiEmissiveTableCache(3);
        var key = new DdgiEmissiveTableCacheKey(Guid.NewGuid(), 1, 2, true, 3);
        var result = new DdgiEmissiveTableBuildResult(
            stats.SelectedCount,
            99,
            stats,
            0,
            0.0);
        cache.Store(key, sources, surfaces, result);
        Array.Clear(surfaces);
        cache.CopySurfacePayloadTo(surfaces);

        Assert.Multiple(() =>
        {
            Assert.That(
                surfaces.Select(surface => BitConverter.SingleToUInt32Bits(
                    surface.MaterialAndVertexAlpha.X)),
                Is.EquivalentTo(new uint[] { 101, 202, 303 }));
            Assert.That(
                sources.Sum(source => source.RadianceSelectionProbability.W),
                Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(
                sources.All(source =>
                    DdgiEmissiveTriangleTable.DecodeAliasIndex(source) < sources.Length),
                Is.True);
        });
    }

    [Test]
    public void DynamicEmissiveShader_UsesLiveTextureAndMatchingAlphaCoveragePolicy()
    {
        string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(hitShading, Does.Contain("EvaluateDdgiDynamicEmissiveSurfaceRadiance"));
            Assert.That(hitShading, Does.Contain("ReadDdgiEmissiveSurface(sourceIndex)"));
            Assert.That(hitShading, Does.Contain("DdgiAlphaCandidateOccupiesOpaqueTransport"));
            Assert.That(hitShading, Does.Contain("material.EmissiveTextureIndex"));
            Assert.That(hitShading, Does.Contain("EmissiveSourceDynamicTextureFlag"));
            Assert.That(common, Does.Contain("SIMPLE_DDGI_EMISSIVE_SURFACE_BUFFER_INDEX"));
            Assert.That(common, Does.Contain("struct GPUDdgiEmissiveSurface"));
        });
    }

    [Test]
    public void ShaderContract_SeparatesDirectHitNextEventAndCachedBounceOwnership()
    {
        string hitShading = ReadRepoText("Njulf.Shaders", "ddgi_hit_shading.glsl");
        string simpleTrace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
        string simpleTransport = ReadRepoText("Njulf.Shaders", "ddgi_simple_transport.comp");
        string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");
        string sceneBuilder = ReadRepoText(
            "Njulf.Rendering",
            "Data",
            "SceneDataBuilder.cs");

        Assert.Multiple(() =>
        {
            Assert.That(hitShading, Does.Contain("this function is next-event estimation at the"));
            Assert.That(hitShading, Does.Contain("if ((firstFlags & EmissiveSourceProxyRollbackFlag) != 0u)"));
            Assert.That(
                hitShading,
                Does.Contain(
                    "if ((firstFlags & (EmissiveSourceTriangleFlag | EmissiveSourceMacroEmitterFlag)) == 0u)"));
            Assert.That(simpleTrace, Does.Contain("different paths. Transport-atlas ownership only gates the"));
            Assert.That(simpleTrace, Does.Contain("vec3 emissiveDiffuse = surface.EmissiveRadiance + emissiveProxyDiffuse;"));
            Assert.That(simpleTrace, Does.Not.Contain("emissiveProxyDiffuse * (1.0 - bounceOwnership)"));
            Assert.That(simpleTransport, Does.Contain("vec3 totalRadiance = source.sourceRadiance;"));
            Assert.That(simpleTransport, Does.Contain("totalRadiance += bounceRadiance;"));
            Assert.That(
                renderer,
                Does.Contain(
                    "if (Settings.GlobalIllumination.EffectiveGiEmissiveMeshSampling)"));
            Assert.That(renderer, Does.Contain("return BuildDdgiEmissiveTriangleSources(scene, out signature);"));
            Assert.That(renderer, Does.Contain("mutually exclusive, so disabling the feature cannot double energy."));
            Assert.That(renderer, Does.Contain("_materialManager.MaterialDataRevision"));
            Assert.That(renderer, Does.Contain("_ddgiEmissiveTableCache.TryGet("));
            Assert.That(
                sceneBuilder.Split("hash.Add(scene.RenderPayloadRevision);").Length - 1,
                Is.EqualTo(2),
                "Both stable-payload signatures must use the scene's O(1) render revision.");
            Assert.That(sceneBuilder, Does.Not.Contain("hash.Add(renderObject.Enabled);"));
            Assert.That(sceneBuilder, Does.Not.Contain("hash.Add(renderObject.IsStatic);"));
        });
    }

    private static GPUDdgiEmissiveSource CreateSource(float radiance) => new()
    {
        Vertex0Area = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
        Edge1AliasProbability = new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
        Edge2AliasFlags = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
        RadianceSelectionProbability = new Vector4(radiance, radiance, radiance, 1.0f)
    };

    private static DdgiEmissiveTriangleCandidate CreateCandidate(float radiance, ulong stableKey) =>
        new(
            Vertex0: Vector3.Zero,
            Vertex1: Vector3.UnitX,
            Vertex2: Vector3.UnitY,
            CoveredMeanRadiance: new Vector3(radiance),
            Flags: DdgiEmissiveSourceFlags.Triangle,
            StableKey: stableKey);

    private static DdgiEmissiveTriangleCandidate CreateCandidateWithSurface(
        Vector3 origin,
        float radiance,
        ulong stableKey,
        uint materialIndex)
    {
        GPUDdgiEmissiveSurface surface = new()
        {
            MaterialAndVertexAlpha = new Vector4(
                BitConverter.UInt32BitsToSingle(materialIndex),
                1.0f,
                1.0f,
                1.0f)
        };
        // The green channel is an independent alignment sentinel retained in
        // the source while the material index travels in the sidecar.
        return new DdgiEmissiveTriangleCandidate(
            origin,
            origin + Vector3.UnitX,
            origin + Vector3.UnitY,
            new Vector3(radiance, materialIndex, radiance),
            DdgiEmissiveSourceFlags.Triangle |
                DdgiEmissiveSourceFlags.DynamicEmissiveTexture,
            stableKey,
            surface);
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(pathParts));
    }
}

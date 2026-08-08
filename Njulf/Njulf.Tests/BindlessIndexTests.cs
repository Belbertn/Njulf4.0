using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Njulf.Rendering;
using Njulf.Rendering.Descriptors;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class BindlessIndexTests
{
    [Test]
    public void StaticBufferRange_IsContiguous()
    {
        for (int index = 0; index < BindlessIndex.StaticBufferCount; index++)
            Assert.That(BindlessIndex.IsStaticBufferIndex(index), Is.True, index.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(BindlessIndex.IsStaticBufferIndex(-1), Is.False);
            Assert.That(BindlessIndex.IsStaticBufferIndex(BindlessIndex.StaticBufferCount), Is.False);
            Assert.That(BindlessIndex.StaticBufferCount, Is.LessThanOrEqualTo(1024));
        });
    }

    [Test]
    public void SimpleDdgiDescriptors_AreContiguousAndNamed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BindlessIndex.SimpleDdgiParamsBuffer, Is.EqualTo(BindlessIndex.ForwardVisibilityIndirectDispatchBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiIrradianceAtlasBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiParamsBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiTransportSourceCacheBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiEmissiveSourceBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer + 1));
            Assert.That(BindlessIndex.FarFieldClipmapParamsBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiEmissiveSourceBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverProbeBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiSchedulerArenaBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiResidencyArenaBuffer, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverProbeBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiStorageValidationBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiResidencyArenaBuffer + 1));
            Assert.That(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1, Is.EqualTo(BindlessIndex.SimpleDdgiStorageValidationBufferBase + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherBufferBase, Is.EqualTo(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1 + 1));
            Assert.That(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1, Is.EqualTo(BindlessIndex.SimpleDdgiReceiverGatherBufferBase + 1));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiRayQueryInstanceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiEmissiveSourceBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiEmissiveSourceBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverProbeBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverProbeBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiResidencyArenaBuffer), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiResidencyArenaBuffer)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiStorageValidationBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiStorageValidationBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiStorageValidationBufferFrame1)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherBufferBase), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherBufferBase)));
            Assert.That(BindlessIndex.GetIndexName(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1), Is.EqualTo(nameof(BindlessIndex.SimpleDdgiReceiverGatherBufferFrame1)));
            Assert.That(
                BindlessIndex.FirstDynamicTextureIndex,
                Is.EqualTo(
                    BindlessIndex.SimpleDdgiSampledVisibilityTextureBase +
                    BindlessIndex.MaxSimpleDdgiSampledAtlasTextureGroups));
        });
    }

    [Test]
    public void ShaderConstants_MatchCurrentSimpleDdgiContract()
    {
        IReadOnlyDictionary<string, int> expected = new Dictionary<string, int>
        {
            ["SIMPLE_DDGI_PARAMS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiParamsBuffer,
            ["SIMPLE_DDGI_IRRADIANCE_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiIrradianceAtlasBuffer,
            ["SIMPLE_DDGI_VISIBILITY_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiVisibilityAtlasBuffer,
            ["SIMPLE_DDGI_RAY_RESULT_SCRATCH_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRayResultScratchBuffer,
            ["SIMPLE_DDGI_PROBE_STATE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiProbeStateBuffer,
            ["SIMPLE_DDGI_PROBE_UPDATE_QUEUE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
            ["SIMPLE_DDGI_RELOCATION_CLASSIFICATION_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRelocationClassificationBuffer,
            ["SIMPLE_DDGI_TRANSPORT_IRRADIANCE_ATLAS_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiTransportIrradianceAtlasBuffer,
            ["SIMPLE_DDGI_TRANSPORT_SOURCE_CACHE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiTransportSourceCacheBuffer,
            ["SIMPLE_DDGI_RAY_QUERY_INSTANCE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiRayQueryInstanceBuffer,
            ["SIMPLE_DDGI_EMISSIVE_SOURCE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiEmissiveSourceBuffer,
            ["SIMPLE_DDGI_RECEIVER_PROBE_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiReceiverProbeBuffer,
            ["SIMPLE_DDGI_RESIDENCY_ARENA_BUFFER_INDEX"] = BindlessIndex.SimpleDdgiResidencyArenaBuffer,
            ["SIMPLE_DDGI_STORAGE_VALIDATION_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleDdgiStorageValidationBufferBase,
            ["SIMPLE_DDGI_RECEIVER_GATHER_BUFFER_BASE_INDEX"] = BindlessIndex.SimpleDdgiReceiverGatherBufferBase,
            ["FAR_FIELD_CLIPMAP_PARAMS_BUFFER_INDEX"] = BindlessIndex.FarFieldClipmapParamsBuffer,
            ["STATIC_BUFFER_COUNT"] = BindlessIndex.StaticBufferCount
        };

        string source = ReadCommonGlsl();
        foreach ((string name, int value) in expected)
            Assert.That(ReadShaderIntConstant(source, name), Is.EqualTo(value), name);

        Assert.That(source, Does.Not.Contain("DDGI_GATHER_TILE_BUFFER_INDEX"));
    }

    private static int ReadShaderIntConstant(string source, string name)
    {
        Match match = Regex.Match(source, $@"\bconst\s+int\s+{Regex.Escape(name)}\s*=\s*(\d+)\s*;");
        if (!match.Success)
            throw new AssertionException($"Shader constant '{name}' was not found in common.glsl.");

        return int.Parse(match.Groups[1].Value);
    }

    private static string ReadCommonGlsl()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "Njulf.Shaders", "common.glsl");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate Njulf.Shaders/common.glsl from the test output directory.");
    }
}

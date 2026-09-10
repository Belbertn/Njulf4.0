using System.Buffers.Binary;
using Njulf.Assets;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline.PipelineObjects;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShaderEffectAssetTests
{
    internal static string FixtureRoot => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Effects");
    [Test]
    public void ShaderEvidenceKeepsLogicalNamesStableAndDetectsChangedCode()
    {
        byte[] code = File.ReadAllBytes(Path.Combine(FixtureRoot, "effect_math.frag.spv"));
        var before = ShaderModuleLoader.EffectIdentity(new("Effects/grade.njeffect.json", ShaderEffectKind.Fullscreen, code));
        code[8] ^= 1; // SPIR-V generator metadata changes identity without requiring another shader program.
        var after = ShaderModuleLoader.EffectIdentity(new("Effects/grade.njeffect.json", ShaderEffectKind.Fullscreen, code));
        Assert.That(after.FileName, Is.EqualTo(before.FileName));
        Assert.That(after.Sha256, Is.Not.EqualTo(before.Sha256));
        var registry = new ShaderModuleIdentityRegistry();
        registry.Record(before);
        Assert.That(LoadedShaderIdentity.Validate(registry.Snapshot()), Is.Null);
        registry.Record(after);
        Assert.That(LoadedShaderIdentity.Validate(registry.Snapshot()), Is.Not.Null, "Conflicting code for one logical asset must invalidate evidence.");
    }
    [Test]
    public async Task CpuAssetsShareScopesAndAsyncLoadsWithoutADevice()
    {
        using var content = new ContentManager(FixtureRoot);
        using var first = content.CreateScope();
        using var second = content.CreateScope();
        var asset = first.Load<ShaderEffectAsset>("effect_math.njeffect.json");
        Assert.That(await second.LoadAsync<ShaderEffectAsset>("effect_math.njeffect.json"), Is.SameAs(asset));
        Assert.That(asset.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "Gain", "Bias" }));
        Assert.That(asset.Parameters[0].DefaultValue, Is.EqualTo(1f));
        first.Dispose();
        Assert.That(second.Load<ShaderEffectAsset>("effect_math.njeffect.json"), Is.SameAs(asset));
        second.UnloadAll();
        Assert.That(second.Load<ShaderEffectAsset>("effect_math.njeffect.json"), Is.Not.SameAs(asset));
        Assert.That(asset.ShaderCode.Length, Is.GreaterThan(20), "CPU snapshots remain valid for registrations retaining them.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await content.LoadAsync<ShaderEffectAsset>("effect_math.njeffect.json", cancellationToken: cancelled.Token));
    }
    [Test]
    public void ParameterBytesUseDeclaredTypesAndSixteenByteSlots()
    {
        byte[] code = File.ReadAllBytes(Path.Combine(FixtureRoot, "effect_math.frag.spv"));
        var asset = new ShaderEffectAsset("packing", ShaderEffectKind.Fullscreen, code,
            [new("Float", EffectParameterType.Float, .5f), new("Int", EffectParameterType.Int, -7),
             new("UInt", EffectParameterType.UInt, 0xF1234567u), new("Vector", EffectParameterType.Vector3, new Vector3(1, 2, 3))]);
        code[0] = 0;
        var bytes = asset.DefaultBytes;
        Assert.Multiple(() =>
        {
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(asset.ShaderCode), Is.EqualTo(0x07230203));
            Assert.That(asset.DefaultBytes.Length, Is.EqualTo(64));
        });
        Assert.That(BinaryPrimitives.ReadSingleLittleEndian(bytes), Is.EqualTo(.5f));
        Assert.That(BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]), Is.EqualTo(-7));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]), Is.EqualTo(0xF1234567u));
        Assert.That(BinaryPrimitives.ReadSingleLittleEndian(bytes[56..]), Is.EqualTo(3f));
        Assert.That(bytes.Slice(4, 12).ToArray(), Is.All.Zero);
        Assert.Throws<ArgumentException>(() => ShaderEffectAsset.WriteParameter(asset.Parameters[0], 1, new byte[16]));
        Assert.Throws<ArgumentException>(() => ShaderEffectAsset.WriteParameter(asset.Parameters[0], float.NaN, new byte[16]));
    }
    [Test]
    public void InvalidDeclarationsAndMalformedAssetsFailBeforeGpuSetup()
    {
        var code = File.ReadAllBytes(Path.Combine(FixtureRoot, "effect_math.frag.spv"));
        Assert.Throws<InvalidDataException>(() => new ShaderEffectAsset("bad", ShaderEffectKind.Fullscreen, new byte[24]));
        Assert.Throws<ArgumentException>(() => new ShaderEffectAsset("bad", ShaderEffectKind.Fullscreen, code,
            [new("x", EffectParameterType.Float, 1f), new("x", EffectParameterType.Float, 2f)]));
        Assert.Throws<ArgumentException>(() => new ShaderEffectAsset("bad", ShaderEffectKind.Fullscreen, code,
            Enumerable.Range(0, 9).Select(i => new EffectParameterDefinition(i.ToString(), EffectParameterType.Float, 1f))));
        Assert.Throws<ArgumentException>(() => new ShaderEffectAsset("bad", ShaderEffectKind.Compute, code));
        Assert.Throws<ArgumentException>(() => new ShaderEffectAsset("bad", ShaderEffectKind.Fullscreen, code, resources:
            [new("a", 0, EffectResourceKind.SampledTexture2D), new("b", 0, EffectResourceKind.SampledTexture2D)]));
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "effect-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "bad.njeffect.json"), "{\"schemaVersion\":99,\"kind\":\"Fullscreen\",\"shader\":\"missing.spv\"}");
            using var content = new ContentManager(directory);
            Assert.Throws<InvalidDataException>(() => content.Load<ShaderEffectAsset>("bad.njeffect.json"));
        }
        finally { Directory.Delete(directory, true); }
    }
}

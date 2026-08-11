using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Njulf.Assets.Cooked;
using NUnit.Framework;

namespace Njulf.Tests;

/// <summary>
/// Executes the audited native C ABI when a bridge binary is supplied by the
/// build/qualification environment. Ordinary source builds intentionally skip
/// this test because the NVIDIA SDK is an optional, separately licensed asset
/// tool dependency and is never loaded by the shipping renderer.
/// </summary>
[TestFixture]
public sealed class PinnedOpacityMicromapBakeBridgeIntegrationTests
{
    public const string BridgePathEnvironmentVariable =
        "NJULF_TEST_OMM_BRIDGE_PATH";

    [Test]
    [Category("NativeOpacityMicromap")]
    public async Task ConfiguredPinnedBridge_BakesAndRoundTripsFourStatePayload()
    {
        string? configuredPath = Environment.GetEnvironmentVariable(
            BridgePathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Ignore(
                $"Set {BridgePathEnvironmentVariable} to an audited x64 " +
                "njulf_omm_bridge binary to execute the native CPU baker.");
        }

        string bridgePath = Path.GetFullPath(configuredPath!);
        Assert.That(File.Exists(bridgePath), Is.True,
            "The configured native OMM bridge does not exist.");

        string manifestPath = Path.Combine(
            Path.GetDirectoryName(bridgePath)!,
            "njulf_omm_bridge.provenance.json");
        Assert.That(File.Exists(manifestPath), Is.True,
            "The pinned build must accompany the bridge with its provenance manifest.");
        PinnedOpacityMicromapBakeBridgeOptions options =
            OpacityMicromapBridgeProvenanceManifest.LoadOptions(
                manifestPath,
                bridgePath);
        Assert.Multiple(() =>
        {
            Assert.That(options.ExpectedSdkVersion, Is.EqualTo("1.9.2"));
            Assert.That(options.Provenance.CommitOrRelease,
                Is.EqualTo("9abacd0f187d0efca491946a29ba7df8c5345264"));
            Assert.That(options.Provenance.LicenseIdentifier,
                Is.EqualTo("LicenseRef-NVIDIA-RTX-SDKs-2023-01-23"));
        });
        using var bridge = new PinnedOpacityMicromapBakeBridge(options);

        OpacityMicromapEligibilityInput eligibilityInput =
            OpacityMicromapEligibilityInput.ExactStaticMask with
            {
                RequestedSubdivisionLevel = 4,
                MaximumFourStateSubdivisionLevel = 12
            };
        OpacityMicromapEligibility eligibility =
            OpacityMicromapEligibilityEvaluator.Evaluate(eligibilityInput);
        Assert.That(eligibility.Eligible, Is.True, eligibility.Detail);

        OpacityMicromapContentKey textureKey = Key("native-alpha-texture"u8);
        OpacityMicromapContentKey formatKey = Key("r32-alpha-mip0"u8);
        var material = new OpacityMicromapMaterialContract(
            MaterialSlot: 0,
            FirstPrimitive: 0,
            PrimitiveCount: 1,
            TexCoordSet: 0,
            UvTransform: OpacityMicromapUvTransformBits.Identity,
            TextureContentHash: textureKey,
            TextureFormatAndMipHash: formatKey,
            Sampler: eligibilityInput.Sampler,
            MaterialAlphaBits: Bits(1.0f),
            UniformVertexAlphaBits: Bits(1.0f),
            AlphaCutoffBits: Bits(0.5f),
            FixedLodBits: Bits(0.0f),
            AlphaContractRevision: 1,
            ShaderAbiRevision: 2);

        byte[] indexBytes = new byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(8, 4), 2);
        byte[] uvBytes = FloatsToBytes([
            0.0f, 0.0f,
            1.0f, 0.0f,
            0.0f, 1.0f
        ]);
        // A checker whose bilinear reconstruction crosses the exact 0.5 mask
        // cutoff inside the triangle, forcing a non-trivial four-state OMM.
        byte[] alphaBytes = FloatsToBytes([
            0.0f, 1.0f,
            1.0f, 0.0f
        ]);
        OpacityMicromapContentKey contentKey = Key(
            indexBytes.Concat(uvBytes).Concat(alphaBytes).ToArray());
        var request = new OpacityMicromapBakeRequest(
            contentKey,
            eligibility,
            material,
            PrimitiveCount: 1,
            RequestedSubdivisionLevel: 4,
            indexBytes,
            uvBytes,
            alphaBytes)
        {
            VertexCount = 3,
            TextureWidth = 2,
            TextureHeight = 2,
            TextureMipCount = 1,
            TextureVulkanFormat = 37,
            AlphaChannel = 3,
            MaximumWorkloadSize = 1UL << 20,
            MaximumArrayDataBytes = 4U * 1024U * 1024U
        };

        OpacityMicromapBakeResult result = await bridge.BakeAsync(
            request,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Detail);
            Assert.That(result.Failure, Is.EqualTo(OpacityMicromapBakeFailure.None));
            Assert.That(result.Payload, Is.Not.Null);
        });
        OpacityMicromapCookedPayload payload = result.Payload!;
        Assert.Multiple(() =>
        {
            Assert.That(payload.PayloadKind,
                Is.EqualTo(OpacityMicromapPayloadKind.VulkanExtFourState));
            Assert.That(payload.Format, Is.EqualTo(OpacityMicromapFormat.FourState));
            Assert.That(payload.PrimitiveCount, Is.EqualTo(1));
            Assert.That(payload.IndexData.Length, Is.EqualTo(sizeof(uint)));
            Assert.That(payload.DescriptorCount, Is.GreaterThan(0));
            Assert.That(payload.OmmData.IsEmpty, Is.False);
            Assert.That(payload.UsageHistogram, Is.Not.Empty);
        });

        byte[] encoded = OpacityMicromapCookedPayloadCodec.Write(payload);
        OpacityMicromapPayloadReadResult decoded =
            OpacityMicromapCookedPayloadCodec.TryRead(encoded);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.Success, Is.True, decoded.Detail);
            Assert.That(decoded.Payload, Is.Not.Null);
            Assert.That(decoded.Payload!.SourceContentHash,
                Is.EqualTo(contentKey));
        });
    }

    private static byte[] FloatsToBytes(float[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(float))];
        MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static uint Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static OpacityMicromapContentKey Key(ReadOnlySpan<byte> bytes) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData(bytes));
}

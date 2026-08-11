using System.Buffers.Binary;
using System.Security.Cryptography;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Frozen first-profile policy for NVIDIA OMM CPU baking. The profile is
/// deliberately narrow: every admitted input can be related directly to the
/// current DDGI ray-query alpha expression without heuristic resampling.
/// </summary>
public sealed record NvidiaOpacityMicromapCookPolicy
{
    public const uint CurrentCookAbi = 1U;
    public const uint CurrentPolicyRevision = 1U;
    public const uint CurrentAlphaContractRevision = 1U;
    public const uint CurrentDdgiShaderAbiRevision = 2U;

    public uint RequestedSubdivisionLevel { get; init; } = 4U;
    public uint MaximumSubdivisionLevel { get; init; } = 8U;
    public ulong MaximumWorkloadSize { get; init; } = 1UL << 28;
    public uint MaximumArrayDataBytes { get; init; } = 256U * 1024U * 1024U;

    /// <summary>
    /// Converts Njulf's inclusive FP32 alpha test to the SDK's strict-greater
    /// test. Cutoff zero is handled by configuring both SDK sides opaque; for
    /// every positive finite cutoff the predecessor float makes the predicates
    /// identical over the set of representable FP32 alpha samples.
    /// </summary>
    public static float TranslateInclusiveCutoffForSdk(float cutoff)
    {
        if (!float.IsFinite(cutoff) || cutoff is < 0.0f or > 1.0f)
            throw new ArgumentOutOfRangeException(nameof(cutoff));
        return cutoff == 0.0f ? 0.0f : MathF.BitDecrement(cutoff);
    }

    public bool TryValidate(out string detail)
    {
        if (RequestedSubdivisionLevel == 0U ||
            RequestedSubdivisionLevel > MaximumSubdivisionLevel ||
            MaximumSubdivisionLevel == 0U ||
            MaximumSubdivisionLevel > 12U ||
            MaximumWorkloadSize == 0UL || MaximumArrayDataBytes == 0U)
        {
            detail = "omm-model-producer-policy-invalid";
            return false;
        }

        detail = "omm-model-producer-policy-valid";
        return true;
    }
}

/// <summary>
/// Production offline payload producer. It consumes exact, authenticated KTX2
/// files from the current cook transaction and delegates only the bounded CPU
/// bake to a pinned native bridge. A failure never affects the ordinary model
/// publication and never creates a partially trusted OMM chunk.
/// </summary>
public sealed class NvidiaOpacityMicromapModelPayloadProducer :
    IOpacityMicromapModelPayloadProducer
{
    private const uint FullyUnknownOpaqueIndex = 0xffff_fffcu;
    private const uint Rgba8Unorm = 37U;
    private const uint Rgba8Srgb = 43U;
    private const int NativeTriangleDescriptorBytes = 8;

    private readonly IOpacityMicromapBakeBridge _bridge;
    private readonly NvidiaOpacityMicromapCookPolicy _policy;

    public NvidiaOpacityMicromapModelPayloadProducer(
        IOpacityMicromapBakeBridge bridge,
        NvidiaOpacityMicromapCookPolicy? policy = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _policy = policy ?? new NvidiaOpacityMicromapCookPolicy();
        if (!_policy.TryValidate(out string policyDetail))
            throw new ArgumentException(policyDetail, nameof(policy));
        if (!_bridge.Contract.TryValidate(out string bridgeDetail))
            throw new ArgumentException(bridgeDetail, nameof(bridge));
    }

    public OpacityMicromapPayloadProducerIdentity Identity => new(
        "NVIDIA OMM CPU four-state exact-mask producer",
        NvidiaOpacityMicromapCookPolicy.CurrentCookAbi,
        NvidiaOpacityMicromapCookPolicy.CurrentPolicyRevision)
    {
        SdkProvenanceHash = _bridge.Contract.Provenance.ComputeFingerprint()
    };

    public OpacityMicromapPayloadProductionResult Produce(
        in OpacityMicromapModelCookContext context)
    {
        try
        {
            return ProduceCore(context);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not AccessViolationException)
        {
            return OpacityMicromapPayloadProductionResult.Rejected(
                "omm-model-producer-transaction-failed-" +
                BoundedReason(exception.GetType().Name));
        }
    }

    private OpacityMicromapPayloadProductionResult ProduceCore(
        in OpacityMicromapModelCookContext context)
    {
        if (!_policy.TryValidate(out string policyDetail))
            return OpacityMicromapPayloadProductionResult.Rejected(policyDetail);
        if (!_bridge.Contract.TryValidate(out string bridgeDetail))
            return OpacityMicromapPayloadProductionResult.Rejected(bridgeDetail);

        CookedMeshPayload mesh = context.CookedMesh;
        CookedMaterialTable materials = context.CookedMaterials;
        if (mesh.SubMeshes is null || mesh.Indices is null ||
            mesh.VertexPositions is null || mesh.VertexUvColors is null ||
            materials.Materials is null ||
            mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0)
        {
            return OpacityMicromapPayloadProductionResult.Rejected(
                "omm-model-producer-cooked-input-invalid");
        }

        int totalPrimitiveCount = mesh.Indices.Length / 3;
        if (totalPrimitiveCount > _bridge.Contract.MaximumPrimitiveCount)
        {
            return OpacityMicromapPayloadProductionResult.NotProduced(
                "omm-model-producer-primitive-cap-exceeded");
        }

        Dictionary<int, OpacityMicromapCookedTextureArtifact> artifacts =
            BuildArtifactMap(materials.OpacityMicromapTextureArtifacts);
        var parts = new List<BakedPart>();
        string lastIneligibility = "no-submeshes";
        for (int subMeshIndex = 0; subMeshIndex < mesh.SubMeshes.Count; subMeshIndex++)
        {
            CookedSubMeshRecord subMesh = mesh.SubMeshes[subMeshIndex];
            if (!TryPrepareRequest(
                    context,
                    subMesh,
                    artifacts,
                    out OpacityMicromapBakeRequest request,
                    out uint firstPrimitive,
                    out string detail))
            {
                // Ineligible content is represented by FullyUnknownOpaque in
                // the final stream and therefore keeps candidate confirmation.
                lastIneligibility = detail;
                continue;
            }

            OpacityMicromapBakeResult baked = _bridge
                .BakeAsync(request, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (!baked.Succeeded || baked.Payload is null)
            {
                return OpacityMicromapPayloadProductionResult.Rejected(
                    "omm-model-producer-native-bake-rejected-" +
                    BoundedReason(baked.Detail));
            }
            if (!TryValidatePart(request, baked.Payload, out detail))
            {
                return OpacityMicromapPayloadProductionResult.Rejected(detail);
            }

            parts.Add(new BakedPart(firstPrimitive, request, baked.Payload));
        }

        if (parts.Count == 0)
        {
            return OpacityMicromapPayloadProductionResult.NotProduced(
                "omm-model-producer-no-exact-static-mask-submeshes-" +
                BoundedReason(lastIneligibility));
        }

        if (!TryMergeParts(
                context,
                checked((uint)totalPrimitiveCount),
                parts,
                out OpacityMicromapCookedPayload? payload,
                out string mergeDetail))
        {
            return OpacityMicromapPayloadProductionResult.Rejected(mergeDetail);
        }

        return OpacityMicromapPayloadProductionResult.Produced(
            payload!,
            "omm-model-producer-payload-produced");
    }

    private bool TryPrepareRequest(
        in OpacityMicromapModelCookContext context,
        CookedSubMeshRecord subMesh,
        IReadOnlyDictionary<int, OpacityMicromapCookedTextureArtifact> artifacts,
        out OpacityMicromapBakeRequest request,
        out uint firstPrimitive,
        out string detail)
    {
        request = default;
        firstPrimitive = 0U;
        if (subMesh.IndexOffset < 0 || subMesh.IndexCount <= 0 ||
            subMesh.IndexCount % 3 != 0 || subMesh.VertexOffset < 0 ||
            subMesh.VertexCount <= 0 || subMesh.SkinIndex >= 0 ||
            subMesh.IndexOffset % 3 != 0 ||
            (long)subMesh.IndexOffset + subMesh.IndexCount > context.CookedMesh.Indices.Length ||
            (long)subMesh.VertexOffset + subMesh.VertexCount > context.CookedMesh.VertexUvColors.Length ||
            (uint)subMesh.MaterialSlot >= (uint)context.CookedMaterials.Materials.Count ||
            !artifacts.TryGetValue(subMesh.MaterialSlot, out OpacityMicromapCookedTextureArtifact? artifact))
        {
            detail = "omm-submesh-static-layout-or-texture-ineligible";
            return false;
        }

        ModelMaterial material = context.CookedMaterials.Materials[subMesh.MaterialSlot];
        ModelTextureSlot? binding = material.BaseColorTexture;
        if (binding?.Source is null || material.AlphaMode != ModelAlphaMode.Mask ||
            BitConverter.SingleToUInt32Bits(material.Albedo.W) != 0x3f80_0000u ||
            !float.IsFinite(material.AlphaCutoff) ||
            material.AlphaCutoff is < 0.0f or > 1.0f ||
            material.TransmissionFactor != 0.0f ||
            material.ThicknessFactor != 0.0f ||
            material.GiTransmissionPolicy != ModelGiTransmissionPolicy.None ||
            artifact.VulkanFormat is not (Rgba8Unorm or Rgba8Srgb) ||
            artifact.Width <= 0 || artifact.Height <= 0 || artifact.MipCount <= 0 ||
            artifact.ColorSpace != binding.ColorSpace ||
            !File.Exists(artifact.AbsoluteKtx2Path))
        {
            detail = "omm-submesh-material-profile-ineligible";
            return false;
        }

        OpacityMicromapUvTransformBits uvTransform = CreateUvTransform(binding);
        OpacityMicromapSamplerContract sampler = CreateSamplerContract(binding.Sampler);
        uint vertexAlphaBits = ResolveUniformVertexAlphaBits(
            context.CookedMesh.VertexUvColors,
            subMesh.VertexOffset,
            subMesh.VertexCount,
            out bool uniformVertexAlpha);
        int texCoordSet = binding.TexCoordSet;
        bool completeUvStream = texCoordSet is 0 or 1;
        var eligibilityInput = new OpacityMicromapEligibilityInput
        {
            StaticTriangleTopology = true,
            StableDdgiUvStream = completeUvStream,
            AlphaMode = OpacityMicromapMaterialAlphaMode.Mask,
            UsesCanonicalSingleSampledAlphaExpression = true,
            MaterialAlphaBits = BitConverter.SingleToUInt32Bits(material.Albedo.W),
            MaterialAlphaFrozen = true,
            VertexAlphaIsUniform = uniformVertexAlpha,
            UniformVertexAlphaBits = vertexAlphaBits,
            VertexAlphaFrozen = true,
            ImmutableTextureContent = true,
            ExactRuntimeDecodedAlphaValues =
                artifact.VulkanFormat is Rgba8Unorm or Rgba8Srgb,
            RuntimeTextureFormatSupported =
                artifact.VulkanFormat is Rgba8Unorm or Rgba8Srgb,
            TexCoordSet = texCoordSet,
            UvTransform = uvTransform,
            Sampler = sampler,
            FixedDdgiLod = true,
            CompleteResidentMipData = artifact.MipCount > 0,
            AlphaCutoffBits = BitConverter.SingleToUInt32Bits(material.AlphaCutoff),
            AlphaCutoffFrozen = true,
            AlphaComparison = OpacityMicromapAlphaComparison.GreaterThanOrEqual,
            ThinTransmissionAbsent = true,
            AnimatedMaskAbsent = true,
            ProceduralAlphaAbsent = true,
            PerRayAlphaOverrideAbsent = true,
            GeometryAndUvsDoNotDeform = true,
            FourStateFormatSupported = true,
            RequestedSubdivisionLevel = _policy.RequestedSubdivisionLevel,
            MaximumFourStateSubdivisionLevel = _policy.MaximumSubdivisionLevel
        };
        OpacityMicromapEligibility eligibility =
            OpacityMicromapEligibilityEvaluator.Evaluate(eligibilityInput);
        // The first native threshold profile proves >= equivalence by moving
        // the SDK's strict > cutoff down by one representable FP32 value. That
        // proof is valid only when the two multiplicative alpha factors are 1.
        if (!eligibility.Eligible ||
            eligibilityInput.MaterialAlphaBits != 0x3f80_0000u ||
            eligibilityInput.UniformVertexAlphaBits != 0x3f80_0000u)
        {
            detail = "omm-submesh-eligibility-rejected-" + eligibility.Detail;
            return false;
        }

        byte[] ktxBytes = File.ReadAllBytes(artifact.AbsoluteKtx2Path);
        if (ComputeSha256(ktxBytes) != artifact.Ktx2Sha256)
        {
            detail = "omm-submesh-cooked-texture-authentication-failed";
            return false;
        }
        TextureTransportSourceAnalysis decoded = TextureCooker.AnalyzeTransportSource(
            ktxBytes,
            TextureContainerKind.Ktx2,
            artifact.AbsoluteKtx2Path,
            new TextureCookOptions(
                MaxDimension: Math.Max(artifact.Width, artifact.Height),
                ColorSpace: artifact.ColorSpace,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                Semantic: TextureSemantic.Color,
                PreserveAlphaCoverage: artifact.AlphaCoveragePreserved,
                AlphaCutoff: artifact.AlphaCoverageCutoff ?? material.AlphaCutoff),
            _bridge.Contract.MaximumInputBytes,
            checked((long)artifact.Width * artifact.Height));
        if (!decoded.IsSampleable || decoded.Image is null ||
            decoded.Image.Width != artifact.Width || decoded.Image.Height != artifact.Height)
        {
            detail = "omm-submesh-cooked-texture-decode-failed";
            return false;
        }

        byte[] indexBytes = EncodeIndices(
            context.CookedMesh.Indices,
            subMesh.IndexOffset,
            subMesh.IndexCount,
            checked((uint)subMesh.VertexCount));
        byte[] uvBytes = EncodeUvs(
            context.CookedMesh.VertexUvColors,
            subMesh.VertexOffset,
            subMesh.VertexCount,
            texCoordSet);
        float[] alpha = new float[checked(artifact.Width * artifact.Height)];
        decoded.Image.CopyAlphaTo(alpha);
        byte[] alphaBytes = EncodeFloats(alpha);
        OpacityMicromapContentKey formatAndMipHash =
            ComputeTextureLayoutHash(artifact, sampler);
        uint primitiveCount = checked((uint)(subMesh.IndexCount / 3));
        var localMaterial = new OpacityMicromapMaterialContract(
            checked((uint)subMesh.MaterialSlot),
            FirstPrimitive: 0U,
            primitiveCount,
            texCoordSet,
            uvTransform,
            artifact.Ktx2Sha256,
            formatAndMipHash,
            sampler,
            eligibilityInput.MaterialAlphaBits,
            eligibilityInput.UniformVertexAlphaBits,
            eligibilityInput.AlphaCutoffBits,
            FixedLodBits: 0U,
            NvidiaOpacityMicromapCookPolicy.CurrentAlphaContractRevision,
            NvidiaOpacityMicromapCookPolicy.CurrentDdgiShaderAbiRevision);
        var subdivision = new OpacityMicromapSubdivisionPolicy(
            _policy.RequestedSubdivisionLevel,
            _policy.MaximumSubdivisionLevel,
            NvidiaOpacityMicromapCookPolicy.CurrentPolicyRevision);
        byte[] topologyBytes = EncodePositions(
            context.CookedMesh.VertexPositions,
            subMesh.VertexOffset,
            subMesh.VertexCount);
        OpacityMicromapContentKey contentKey =
            OpacityMicromapContentKeyBuilder.Compute(
                new OpacityMicromapContentKeyInput(
                    topologyBytes,
                    indexBytes,
                    uvBytes,
                    localMaterial,
                    TextureResidencyRevision: 1UL,
                    NvidiaOpacityMicromapCookPolicy.CurrentCookAbi,
                    OpacityMicromapCookedPayloadCodec.CurrentSchemaVersion,
                    OpacityMicromapPayloadKind.VulkanExtFourState,
                    subdivision));

        request = new OpacityMicromapBakeRequest(
            contentKey,
            eligibility,
            localMaterial,
            primitiveCount,
            _policy.RequestedSubdivisionLevel,
            indexBytes,
            uvBytes,
            alphaBytes)
        {
            VertexCount = checked((uint)subMesh.VertexCount),
            TextureWidth = checked((uint)artifact.Width),
            TextureHeight = checked((uint)artifact.Height),
            TextureMipCount = 1U,
            TextureVulkanFormat = artifact.VulkanFormat,
            AlphaChannel = 3U,
            MaximumWorkloadSize = _policy.MaximumWorkloadSize,
            MaximumArrayDataBytes = _policy.MaximumArrayDataBytes
        };
        if (!request.TryValidate(_bridge.Contract, out _, out detail))
            return false;

        firstPrimitive = checked((uint)(subMesh.IndexOffset / 3));
        detail = "omm-submesh-bake-request-ready";
        return true;
    }

    private bool TryValidatePart(
        in OpacityMicromapBakeRequest request,
        OpacityMicromapCookedPayload payload,
        out string detail)
    {
        OpacityMicromapContentKey provenance =
            _bridge.Contract.Provenance.ComputeFingerprint();
        if (payload.CookAbi != NvidiaOpacityMicromapCookPolicy.CurrentCookAbi ||
            payload.SourceContentHash != request.ContentKey ||
            payload.SdkProvenanceHash != provenance ||
            payload.PrimitiveCount != request.PrimitiveCount ||
            payload.DescriptorCount == 0U ||
            payload.IndexData.Length != checked((int)payload.PrimitiveCount * sizeof(uint)) ||
            payload.DescriptorData.Length !=
                checked((int)payload.DescriptorCount * NativeTriangleDescriptorBytes) ||
            payload.MaterialContracts.Count != 1 ||
            payload.MaterialContracts[0] != request.MaterialContract)
        {
            detail = "omm-model-producer-native-payload-contract-mismatch";
            return false;
        }

        OpacityMicromapPayloadReadResult roundTrip =
            OpacityMicromapCookedPayloadCodec.TryRead(
                OpacityMicromapCookedPayloadCodec.Write(payload));
        if (!roundTrip.Success)
        {
            detail = "omm-model-producer-native-payload-codec-rejected";
            return false;
        }

        detail = "omm-model-producer-native-payload-valid";
        return true;
    }

    private static bool TryMergeParts(
        in OpacityMicromapModelCookContext context,
        uint totalPrimitiveCount,
        IReadOnlyList<BakedPart> parts,
        out OpacityMicromapCookedPayload? payload,
        out string detail)
    {
        payload = null;
        var ommData = new List<byte>();
        var descriptorData = new List<byte>();
        var materialContracts = new List<OpacityMicromapMaterialContract>(parts.Count);
        var usage = new SortedDictionary<uint, ulong>();
        var perPrimitive = new uint[totalPrimitiveCount];
        Array.Fill(perPrimitive, FullyUnknownOpaqueIndex);
        ulong opaque = 0UL;
        ulong transparent = 0UL;
        ulong unknownOpaque = 0UL;
        ulong unknownTransparent = 0UL;
        uint maximumSubdivision = 0U;

        foreach (BakedPart part in parts.OrderBy(static item => item.FirstPrimitive))
        {
            OpacityMicromapCookedPayload source = part.Payload;
            while ((ommData.Count & 7) != 0)
                ommData.Add(0);
            uint dataBase = checked((uint)ommData.Count);
            uint descriptorBase = checked((uint)(descriptorData.Count /
                NativeTriangleDescriptorBytes));
            ommData.AddRange(source.OmmData.ToArray());

            ReadOnlySpan<byte> sourceDescriptors = source.DescriptorData.Span;
            byte[] encodedDescriptor = new byte[NativeTriangleDescriptorBytes];
            for (uint descriptor = 0U; descriptor < source.DescriptorCount; descriptor++)
            {
                int offset = checked((int)descriptor * NativeTriangleDescriptorBytes);
                uint sourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    sourceDescriptors.Slice(offset, sizeof(uint)));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    encodedDescriptor,
                    checked(dataBase + sourceOffset));
                sourceDescriptors.Slice(offset + sizeof(uint), 4)
                    .CopyTo(encodedDescriptor.AsSpan(4));
                descriptorData.AddRange(encodedDescriptor);
            }

            ReadOnlySpan<byte> sourceIndices = source.IndexData.Span;
            for (uint primitive = 0U; primitive < source.PrimitiveCount; primitive++)
            {
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(
                    sourceIndices.Slice(checked((int)primitive * sizeof(uint)), sizeof(uint)));
                if (index < source.DescriptorCount)
                    index = checked(index + descriptorBase);
                else if (!IsSupportedSpecialIndex(index))
                {
                    detail = "omm-model-producer-part-index-invalid";
                    return false;
                }
                uint target = checked(part.FirstPrimitive + primitive);
                if (target >= totalPrimitiveCount)
                {
                    detail = "omm-model-producer-part-range-invalid";
                    return false;
                }
                perPrimitive[target] = index;
            }

            OpacityMicromapMaterialContract local = source.MaterialContracts[0];
            materialContracts.Add(local with { FirstPrimitive = part.FirstPrimitive });
            foreach (OpacityMicromapUsage entry in source.UsageHistogram)
            {
                usage.TryGetValue(entry.SubdivisionLevel, out ulong count);
                usage[entry.SubdivisionLevel] = checked(count + entry.Count);
            }
            maximumSubdivision = Math.Max(maximumSubdivision, source.MaximumSubdivisionLevel);
            if (source.ClassificationStatistics is { } stats)
            {
                opaque = checked(opaque + stats.Opaque);
                transparent = checked(transparent + stats.Transparent);
                unknownOpaque = checked(unknownOpaque + stats.UnknownOpaque);
                unknownTransparent = checked(unknownTransparent + stats.UnknownTransparent);
            }
        }

        if (descriptorData.Count == 0 || ommData.Count == 0 || usage.Count == 0)
        {
            detail = "omm-model-producer-merged-payload-empty";
            return false;
        }

        byte[] indexData = new byte[checked(perPrimitive.Length * sizeof(uint))];
        for (int i = 0; i < perPrimitive.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                indexData.AsSpan(i * sizeof(uint), sizeof(uint)),
                perPrimitive[i]);
        }
        OpacityMicromapUsage[] histogram = usage
            .Select(static pair => new OpacityMicromapUsage(
                OpacityMicromapFormat.FourState,
                pair.Key,
                pair.Value))
            .ToArray();
        OpacityMicromapContentKey aggregateKey = ComputeAggregateKey(
            context,
            parts,
            indexData,
            descriptorData,
            ommData);

        try
        {
            payload = OpacityMicromapCookedPayload.Create(
                NvidiaOpacityMicromapCookPolicy.CurrentCookAbi,
                aggregateKey,
                parts[0].Payload.SdkProvenanceHash,
                maximumSubdivision,
                totalPrimitiveCount,
                checked((uint)(descriptorData.Count / NativeTriangleDescriptorBytes)),
                materialContracts.ToArray(),
                histogram,
                ommData.ToArray(),
                indexData,
                descriptorData.ToArray(),
                new OpacityMicromapClassificationStatistics(
                    opaque,
                    transparent,
                    unknownOpaque,
                    unknownTransparent));
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException)
        {
            detail = "omm-model-producer-merged-payload-rejected";
            return false;
        }

        detail = "omm-model-producer-parts-merged";
        return true;
    }

    private static Dictionary<int, OpacityMicromapCookedTextureArtifact>
        BuildArtifactMap(
            IReadOnlyList<OpacityMicromapCookedTextureArtifact>? artifacts)
    {
        var result = new Dictionary<int, OpacityMicromapCookedTextureArtifact>();
        if (artifacts is null)
            return result;
        foreach (OpacityMicromapCookedTextureArtifact artifact in artifacts)
        {
            if (artifact is not null)
                result.TryAdd(artifact.MaterialSlot, artifact);
        }
        return result;
    }

    private static OpacityMicromapSamplerContract CreateSamplerContract(
        in TextureSamplerDescription sampler)
    {
        bool filterMatches = sampler.MinFilter == sampler.MagFilter;
        bool addressMatches = sampler.WrapU == sampler.WrapV;
        bool exactAnisotropy =
            BitConverter.SingleToUInt32Bits(sampler.MaxAnisotropy) == 0x3f80_0000u;
        return new OpacityMicromapSamplerContract(
            (uint)sampler.MinFilter,
            (uint)sampler.MagFilter,
            (uint)sampler.MipFilter,
            ToVulkanAddressMode(sampler.WrapU),
            ToVulkanAddressMode(sampler.WrapV),
            0U,
            0U,
            NormalizedCoordinates: true,
            MatchesDdgiPolicy: filterMatches && addressMatches && exactAnisotropy,
            SdkQualified: filterMatches && addressMatches && exactAnisotropy);
    }

    private static uint ToVulkanAddressMode(TextureWrapMode mode) => mode switch
    {
        TextureWrapMode.Repeat => 0U,
        TextureWrapMode.MirroredRepeat => 1U,
        TextureWrapMode.ClampToEdge => 2U,
        _ => uint.MaxValue
    };

    private static OpacityMicromapUvTransformBits CreateUvTransform(
        ModelTextureSlot binding)
    {
        float sine = MathF.Sin(binding.RotationRadians);
        float cosine = MathF.Cos(binding.RotationRadians);
        return new OpacityMicromapUvTransformBits(
            CanonicalBits(binding.Scale.X * cosine),
            CanonicalBits(-binding.Scale.Y * sine),
            CanonicalBits(binding.Offset.X),
            CanonicalBits(binding.Scale.X * sine),
            CanonicalBits(binding.Scale.Y * cosine),
            CanonicalBits(binding.Offset.Y),
            0U,
            0U,
            0x3f80_0000u);
    }

    private static uint ResolveUniformVertexAlphaBits(
        IReadOnlyList<CookedVertexUvColorStream> vertices,
        int offset,
        int count,
        out bool uniform)
    {
        uint first = Bits(vertices[offset].Color.W);
        uniform = true;
        for (int i = 1; i < count; i++)
        {
            if (Bits(vertices[offset + i].Color.W) != first)
            {
                uniform = false;
                return first;
            }
        }
        return first;
    }

    private static byte[] EncodeIndices(
        IReadOnlyList<uint> source,
        int offset,
        int count,
        uint vertexCount)
    {
        byte[] bytes = new byte[checked(count * sizeof(uint))];
        for (int i = 0; i < count; i++)
        {
            uint index = source[offset + i];
            if (index >= vertexCount)
                throw new InvalidDataException("OMM submesh index exceeds its local vertex range.");
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(i * sizeof(uint), sizeof(uint)),
                index);
        }
        return bytes;
    }

    private static byte[] EncodeUvs(
        IReadOnlyList<CookedVertexUvColorStream> source,
        int offset,
        int count,
        int texCoordSet)
    {
        byte[] bytes = new byte[checked(count * 2 * sizeof(float))];
        for (int i = 0; i < count; i++)
        {
            Vector2 uv = texCoordSet == 0
                ? source[offset + i].TexCoord
                : source[offset + i].TexCoord2;
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                throw new InvalidDataException("OMM UV stream contains a non-finite value.");
            int target = i * 2 * sizeof(float);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(target, 4), Bits(uv.X));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(target + 4, 4), Bits(uv.Y));
        }
        return bytes;
    }

    private static byte[] EncodePositions(
        IReadOnlyList<CookedVertexPositionStream> source,
        int offset,
        int count)
    {
        byte[] bytes = new byte[checked(count * 3 * sizeof(float))];
        for (int i = 0; i < count; i++)
        {
            Vector4 position = source[offset + i].Position;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z))
            {
                throw new InvalidDataException("OMM position stream contains a non-finite value.");
            }
            int target = i * 3 * sizeof(float);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(target, 4), Bits(position.X));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(target + 4, 4), Bits(position.Y));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(target + 8, 4), Bits(position.Z));
        }
        return bytes;
    }

    private static byte[] EncodeFloats(ReadOnlySpan<float> source)
    {
        byte[] bytes = new byte[checked(source.Length * sizeof(float))];
        for (int i = 0; i < source.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(i * sizeof(float), sizeof(float)),
                Bits(source[i]));
        }
        return bytes;
    }

    private static OpacityMicromapContentKey ComputeTextureLayoutHash(
        OpacityMicromapCookedTextureArtifact artifact,
        in OpacityMicromapSamplerContract sampler)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("njulf.opacity-micromap.texture-layout"u8);
        OpacityMicromapCanonicalHash.AppendContentKey(hash, artifact.Ktx2Sha256);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, artifact.VulkanFormat);
        OpacityMicromapCanonicalHash.AppendUInt32(hash, checked((uint)artifact.Width));
        OpacityMicromapCanonicalHash.AppendUInt32(hash, checked((uint)artifact.Height));
        OpacityMicromapCanonicalHash.AppendUInt32(hash, checked((uint)artifact.MipCount));
        OpacityMicromapCanonicalHash.AppendUInt32(hash, (uint)artifact.ColorSpace);
        sampler.AppendCanonicalBytes(hash);
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static OpacityMicromapContentKey ComputeAggregateKey(
        in OpacityMicromapModelCookContext context,
        IReadOnlyList<BakedPart> parts,
        ReadOnlySpan<byte> indexData,
        IReadOnlyList<byte> descriptorData,
        IReadOnlyList<byte> ommData)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("njulf.opacity-micromap.model-payload"u8);
        OpacityMicromapCanonicalHash.AppendUInt64(hash, context.SourceHash);
        OpacityMicromapCanonicalHash.AppendUInt64(hash, context.ImportSettingsHash);
        OpacityMicromapCanonicalHash.AppendUInt64(hash, context.DependencyHash);
        foreach (BakedPart part in parts.OrderBy(static item => item.FirstPrimitive))
        {
            OpacityMicromapCanonicalHash.AppendUInt32(hash, part.FirstPrimitive);
            OpacityMicromapCanonicalHash.AppendContentKey(hash, part.Request.ContentKey);
        }
        OpacityMicromapCanonicalHash.AppendBlob(hash, indexData);
        OpacityMicromapCanonicalHash.AppendBlob(hash, descriptorData.ToArray());
        OpacityMicromapCanonicalHash.AppendBlob(hash, ommData.ToArray());
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static OpacityMicromapContentKey ComputeSha256(ReadOnlySpan<byte> bytes) =>
        OpacityMicromapContentKey.FromSha256(SHA256.HashData(bytes));

    private static bool IsSupportedSpecialIndex(uint value) =>
        value is >= 0xffff_fffcu;

    private static uint Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static uint CanonicalBits(float value) =>
        value == 0.0f ? 0U : Bits(value);

    private static string BoundedReason(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "no-detail";
        string result = detail.Replace('\r', ' ').Replace('\n', ' ');
        return result.Length <= 96 ? result : result[..96];
    }

    private sealed record BakedPart(
        uint FirstPrimitive,
        OpacityMicromapBakeRequest Request,
        OpacityMicromapCookedPayload Payload);
}

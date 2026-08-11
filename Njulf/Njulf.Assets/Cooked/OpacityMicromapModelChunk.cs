using Njulf.Core.Geometry;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Immutable, already-validated optional OMM section for a cooked model
/// package.  Keeping the encoded bytes private ensures the payload verified by
/// the cooker is exactly the payload written by <see cref="CookedPackage"/>.
/// </summary>
public sealed class CookedOpacityMicromapModelChunk
{
    private readonly byte[] _encodedBytes;

    private CookedOpacityMicromapModelChunk(
        OpacityMicromapCookedPayload payload,
        byte[] encodedBytes)
    {
        Payload = payload;
        _encodedBytes = encodedBytes;
    }

    public OpacityMicromapCookedPayload Payload { get; }

    internal ReadOnlyMemory<byte> EncodedBytes => _encodedBytes;

    public static bool TryCreate(
        OpacityMicromapCookedPayload? payload,
        out CookedOpacityMicromapModelChunk? chunk,
        out string detail)
    {
        if (payload is null)
        {
            chunk = null;
            detail = "opacity-micromap-payload-not-produced";
            return false;
        }

        try
        {
            byte[] bytes = OpacityMicromapCookedPayloadCodec.Write(payload);
            OpacityMicromapPayloadReadResult validated =
                OpacityMicromapCookedPayloadCodec.TryRead(bytes);
            if (!validated.Success || validated.Payload is null)
            {
                chunk = null;
                detail = "opacity-micromap-payload-self-validation-failed";
                return false;
            }

            chunk = new CookedOpacityMicromapModelChunk(validated.Payload, bytes);
            detail = "opacity-micromap-payload-ready";
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidOperationException or
                                           OverflowException)
        {
            chunk = null;
            detail = "opacity-micromap-payload-serialization-rejected";
            return false;
        }
    }

    /// <summary>
    /// Verifies the small set of model-local facts that can be proved without
    /// re-running the native baker.  The payload's raw content key remains the
    /// authoritative cross-process identity, but an out-of-range primitive or
    /// material slot must never reach a backend as a supposedly usable OMM
    /// attachment.
    /// </summary>
    public static bool TryValidateModelAttachment(
        OpacityMicromapCookedPayload? payload,
        CookedMeshPayload? mesh,
        CookedMaterialTable? materials,
        out OpacityMicromapPayloadValidationFailure failure,
        out string detail)
    {
        if (payload is null || mesh is null || materials is null ||
            mesh.Indices is null || materials.Materials is null)
        {
            failure = OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid;
            detail = "opacity-micromap-model-attachment-missing";
            return false;
        }

        if (mesh.Indices.Length == 0 || mesh.Indices.Length % 3 != 0)
        {
            failure = OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid;
            detail = "opacity-micromap-model-index-triangle-layout-invalid";
            return false;
        }

        ulong meshPrimitiveCapacity = checked((ulong)mesh.Indices.Length / 3UL);
        if (payload.PrimitiveCount > meshPrimitiveCapacity)
        {
            failure = OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid;
            detail = "opacity-micromap-model-primitive-range-out-of-bounds";
            return false;
        }

        int materialCount = materials.Materials.Count;
        if (materialCount == 0)
        {
            failure = OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid;
            detail = "opacity-micromap-model-has-no-materials";
            return false;
        }

        foreach (OpacityMicromapMaterialContract material in payload.MaterialContracts)
        {
            if ((ulong)material.MaterialSlot >= (ulong)materialCount ||
                (ulong)material.FirstPrimitive + material.PrimitiveCount >
                    payload.PrimitiveCount)
            {
                failure = OpacityMicromapPayloadValidationFailure.ModelAttachmentInvalid;
                detail = "opacity-micromap-model-material-or-primitive-range-out-of-bounds";
                return false;
            }
        }

        failure = OpacityMicromapPayloadValidationFailure.None;
        detail = "opacity-micromap-model-attachment-valid";
        return true;
    }
}

/// <summary>
/// Bounded identity for an offline payload producer.  It participates in the
/// model-cook settings hash so changing the native bridge, cook ABI, or policy
/// cannot silently reuse a model package with stale optional OMM data.
/// </summary>
public readonly record struct OpacityMicromapPayloadProducerIdentity(
    string Name,
    uint CookAbi,
    uint PolicyRevision)
{
    public const int MaximumNameCharacters = 128;

    /// <summary>
    /// Fingerprint of the pinned native bridge/SDK provenance.  It is part of
    /// the model cook identity so upgrading a bridge binary cannot silently
    /// reuse a payload produced by a different native implementation.
    /// </summary>
    public OpacityMicromapContentKey SdkProvenanceHash { get; init; }

    public bool TryValidate(out string detail)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumNameCharacters)
        {
            detail = "opacity-micromap-producer-name-invalid";
            return false;
        }
        if (CookAbi == 0 || PolicyRevision == 0)
        {
            detail = "opacity-micromap-producer-revision-invalid";
            return false;
        }
        if (SdkProvenanceHash.IsZero)
        {
            detail = "opacity-micromap-producer-provenance-hash-zero";
            return false;
        }

        detail = "opacity-micromap-producer-identity-valid";
        return true;
    }
}

/// <summary>
/// Immutable-at-publication context supplied to an offline OMM producer.  The
/// producer must only use the exact DDGI alpha inputs represented here and must
/// return no chunk whenever it cannot prove C1 eligibility.
/// </summary>
public readonly record struct OpacityMicromapModelCookContext(
    string SourcePath,
    Guid AssetId,
    ulong SourceHash,
    ulong ImportSettingsHash,
    ulong DependencyHash,
    uint ToolVersion,
    ModelMesh SourceModel,
    ProcessedMeshAsset ProcessedMesh,
    CookedMeshPayload CookedMesh,
    CookedMaterialTable CookedMaterials);

public enum OpacityMicromapPayloadProductionStatus : byte
{
    NotProduced = 0,
    Produced = 1,
    Rejected = 2
}

public readonly record struct OpacityMicromapPayloadProductionResult(
    OpacityMicromapPayloadProductionStatus Status,
    OpacityMicromapCookedPayload? Payload,
    string Detail)
{
    public static OpacityMicromapPayloadProductionResult NotProduced(
        string detail) => new(OpacityMicromapPayloadProductionStatus.NotProduced, null, detail);

    public static OpacityMicromapPayloadProductionResult Rejected(
        string detail) => new(OpacityMicromapPayloadProductionStatus.Rejected, null, detail);

    public static OpacityMicromapPayloadProductionResult Produced(
        OpacityMicromapCookedPayload payload,
        string detail) => new(OpacityMicromapPayloadProductionStatus.Produced, payload, detail);
}

/// <summary>
/// Offline-only extension point for a pinned native OMM CPU baker.  The
/// default cooker does not instantiate a producer and therefore emits no OMM
/// section.  A producer failure is converted to an absent optional chunk; it
/// cannot invalidate the ordinary model, mesh, or material transactions.
/// </summary>
public interface IOpacityMicromapModelPayloadProducer
{
    OpacityMicromapPayloadProducerIdentity Identity { get; }

    OpacityMicromapPayloadProductionResult Produce(
        in OpacityMicromapModelCookContext context);
}

/// <summary>
/// Observable load outcome for the optional section.  A rejected chunk is a
/// normal fallback condition; callers must use the ordinary candidate-tested
/// BLAS and should not retry synchronously.
/// </summary>
public readonly record struct CookedOpacityMicromapPayloadLoadStatus(
    bool SectionPresent,
    bool Accepted,
    OpacityMicromapPayloadValidationFailure Failure,
    string Detail)
{
    public static CookedOpacityMicromapPayloadLoadStatus Missing { get; } = new(
        SectionPresent: false,
        Accepted: false,
        Failure: OpacityMicromapPayloadValidationFailure.None,
        Detail: "opacity-micromap-section-absent");

    public static CookedOpacityMicromapPayloadLoadStatus Valid { get; } = new(
        SectionPresent: true,
        Accepted: true,
        Failure: OpacityMicromapPayloadValidationFailure.None,
        Detail: "opacity-micromap-section-valid");

    public static CookedOpacityMicromapPayloadLoadStatus Rejected(
        OpacityMicromapPayloadValidationFailure failure,
        string detail) => new(true, false, failure, detail);
}

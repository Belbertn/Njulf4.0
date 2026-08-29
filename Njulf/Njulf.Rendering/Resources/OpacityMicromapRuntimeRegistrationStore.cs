using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Material aspects that can change the baked opacity decision or the static
/// BLAS candidate-confirmation policy. Transport-rollout and shading-only
/// revisions are intentionally excluded from this identity.
/// </summary>
public readonly record struct OpacityMicromapMaterialRevision(
    uint AlphaCoverage,
    uint Sidedness)
{
    public bool IsValid => AlphaCoverage != 0U && Sidedness != 0U;

    public static OpacityMicromapMaterialRevision From(
        in MaterialAspectRevisions revisions) =>
        new(revisions.AlphaCoverage, revisions.Sidedness);
}

/// <summary>
/// Immutable association between one submesh-local static BLAS domain and an
/// exact cooked OMM payload. A model-wide payload is deliberately not
/// registered against one of several submesh BLASes: doing so would silently
/// reinterpret the per-primitive index stream.
/// </summary>
public readonly record struct OpacityMicromapRuntimeMeshRegistration(
    MeshHandle Mesh,
    MaterialHandle Material,
    OpacityMicromapMaterialRevision MaterialOpacityRevision,
    OpacityMicromapContentKey MeshGeometryKey,
    OpacityMicromapCookedPayload Payload,
    StaticBlasRayGeometryPolicy RayGeometryPolicy,
    uint AccelerationStructureBuildAbi)
{
    public bool TryValidate(out string detail)
    {
        if (!Mesh.IsValid || !Material.IsValid ||
            !MaterialOpacityRevision.IsValid ||
            MeshGeometryKey.IsZero || Payload is null ||
            AccelerationStructureBuildAbi == 0U)
        {
            detail = "omm-runtime-registration-fields-invalid";
            return false;
        }
        if (RayGeometryPolicy is not
            (StaticBlasRayGeometryPolicy.CandidateConfirmationRequired or
             StaticBlasRayGeometryPolicy.TwoSidedCandidateConfirmationRequired))
        {
            detail = "omm-runtime-registration-ray-policy-invalid";
            return false;
        }
        if (!OpacityMicromapExtNativeInputLayout.PackedUint32.TryValidate(
                Payload,
                out detail))
        {
            detail = "omm-runtime-registration-native-payload-invalid-" + detail;
            return false;
        }

        detail = "omm-runtime-registration-valid";
        return true;
    }

    public OpacityMicromapExtStaticBlasCandidate CreateCandidate() => new(
        Payload.SourceContentHash,
        Mesh,
        MeshGeometryKey,
        RayGeometryPolicy,
        AccelerationStructureBuildAbi,
        OpacityMicromapExtNativeInputLayout.PackedUint32);

    public StaticBlasVariantKey CreateVariantKey() => new(
        MeshGeometryKey,
        RayGeometryPolicy,
        Payload.SourceContentHash,
        AccelerationStructureBuildAbi);
}

public readonly record struct OpacityMicromapRuntimeRegistrationSnapshot(
    int RegistrationCount,
    int TotalReferenceCount,
    ulong RegisteredPayloadBytes,
    ulong RejectedRegistrationCount,
    string LastDetail,
    ulong CandidateSetRevision);

/// <summary>
/// Thread-safe handoff from cooked-model upload to the renderer-owned AS
/// builder. Reference counts follow render-object mesh lifetimes, including
/// cloned model instances, and remove the payload before the last mesh lease
/// can be released.
/// </summary>
public sealed class OpacityMicromapRuntimeRegistrationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<MeshHandle, Entry> _entries = new();
    private ulong _rejectedRegistrationCount;
    private ulong _candidateSetRevision;
    private string _lastDetail = "omm-runtime-registration-store-empty";

    public ulong CandidateSetRevision
    {
        get
        {
            lock (_sync)
                return _candidateSetRevision;
        }
    }

    public bool TryRegisterInitialReference(
        in OpacityMicromapRuntimeMeshRegistration registration,
        out string detail)
    {
        if (!registration.TryValidate(out detail))
        {
            RecordRejection(detail);
            return false;
        }

        lock (_sync)
        {
            if (_entries.TryGetValue(registration.Mesh, out Entry? existing))
            {
                if (AreEquivalent(existing.Registration, registration))
                {
                    existing.ReferenceCount = checked(existing.ReferenceCount + 1);
                    detail = "omm-runtime-registration-retained-existing";
                    _lastDetail = detail;
                    return true;
                }

                detail = "omm-runtime-registration-mesh-conflict";
                _rejectedRegistrationCount++;
                _lastDetail = detail;
                return false;
            }

            _entries.Add(registration.Mesh, new Entry(registration));
            _candidateSetRevision = NextRevision(_candidateSetRevision);
            detail = "omm-runtime-registration-added";
            _lastDetail = detail;
            return true;
        }
    }

    public void RetainMeshReference(MeshHandle mesh)
    {
        if (!mesh.IsValid)
            throw new ArgumentOutOfRangeException(nameof(mesh));

        lock (_sync)
        {
            if (!_entries.TryGetValue(mesh, out Entry? entry))
                return;
            entry.ReferenceCount = checked(entry.ReferenceCount + 1);
            _lastDetail = "omm-runtime-registration-retained";
        }
    }

    public void ReleaseMeshReference(MeshHandle mesh)
    {
        if (!mesh.IsValid)
            throw new ArgumentOutOfRangeException(nameof(mesh));

        lock (_sync)
        {
            if (!_entries.TryGetValue(mesh, out Entry? entry))
                return;
            if (entry.ReferenceCount <= 0)
                throw new InvalidOperationException(
                    "OMM runtime registration reference count is corrupt.");
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                _entries.Remove(mesh);
                _candidateSetRevision = NextRevision(_candidateSetRevision);
            }
            _lastDetail = "omm-runtime-registration-released";
        }
    }

    public bool TryGet(
        MeshHandle mesh,
        out OpacityMicromapRuntimeMeshRegistration registration)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(mesh, out Entry? entry))
            {
                registration = entry.Registration;
                return true;
            }
        }

        registration = default;
        return false;
    }

    public OpacityMicromapRuntimeRegistrationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            int references = 0;
            ulong bytes = 0UL;
            foreach (Entry entry in _entries.Values)
            {
                references = checked(references + entry.ReferenceCount);
                OpacityMicromapCookedPayload payload = entry.Registration.Payload;
                bytes = checked(bytes + (ulong)payload.OmmData.Length +
                    (ulong)payload.IndexData.Length +
                    (ulong)payload.DescriptorData.Length);
            }

            return new OpacityMicromapRuntimeRegistrationSnapshot(
                _entries.Count,
                references,
                bytes,
                _rejectedRegistrationCount,
                _lastDetail,
                _candidateSetRevision);
        }
    }

    /// <summary>
    /// Copies the immutable candidate set only when a renderer observes a new
    /// revision. Retain/release operations that do not add or remove a mesh do
    /// not advance the revision, avoiding per-frame payload enumeration.
    /// </summary>
    public OpacityMicromapRuntimeMeshRegistration[]
        GetRegistrationsSnapshot(out ulong candidateSetRevision)
    {
        lock (_sync)
        {
            candidateSetRevision = _candidateSetRevision;
            var registrations =
                new OpacityMicromapRuntimeMeshRegistration[_entries.Count];
            int index = 0;
            foreach (Entry entry in _entries.Values)
                registrations[index++] = entry.Registration;
            return registrations;
        }
    }

    /// <summary>
    /// Computes the position/index identity consumed by the static BLAS. UVs
    /// and alpha are already part of the payload content key; keeping the
    /// geometry key separate permits plain-BLAS sharing and exact variant
    /// invalidation.
    /// </summary>
    public static OpacityMicromapContentKey ComputeMeshGeometryKey(
        ReadOnlySpan<GPUVertexPositionStream> positions,
        ReadOnlySpan<uint> indices)
    {
        if (positions.IsEmpty || indices.IsEmpty || indices.Length % 3 != 0)
            throw new ArgumentException("Static triangle geometry is required.");

        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        hash.AppendData("njulf.static-blas.geometry.v1"u8);
        AppendBlob(hash, MemoryMarshal.AsBytes(positions));
        AppendBlob(hash, MemoryMarshal.AsBytes(indices));
        return OpacityMicromapContentKey.FromSha256(hash.GetHashAndReset());
    }

    private static void AppendBlob(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            length,
            checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private void RecordRejection(string detail)
    {
        lock (_sync)
        {
            _rejectedRegistrationCount++;
            _lastDetail = detail;
        }
    }

    private static ulong NextRevision(ulong revision) =>
        revision == ulong.MaxValue ? 1UL : revision + 1UL;

    private static bool AreEquivalent(
        in OpacityMicromapRuntimeMeshRegistration left,
        in OpacityMicromapRuntimeMeshRegistration right) =>
        left.Mesh == right.Mesh &&
        left.Material == right.Material &&
        left.MaterialOpacityRevision == right.MaterialOpacityRevision &&
        left.MeshGeometryKey == right.MeshGeometryKey &&
        left.Payload.SourceContentHash == right.Payload.SourceContentHash &&
        left.Payload.CookAbi == right.Payload.CookAbi &&
        left.Payload.PrimitiveCount == right.Payload.PrimitiveCount &&
        left.Payload.DescriptorCount == right.Payload.DescriptorCount &&
        left.RayGeometryPolicy == right.RayGeometryPolicy &&
        left.AccelerationStructureBuildAbi == right.AccelerationStructureBuildAbi;

    private sealed class Entry
    {
        public Entry(in OpacityMicromapRuntimeMeshRegistration registration)
        {
            Registration = registration;
            ReferenceCount = 1;
        }

        public OpacityMicromapRuntimeMeshRegistration Registration { get; }
        public int ReferenceCount { get; set; }
    }
}

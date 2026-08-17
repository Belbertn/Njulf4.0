using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;

[Flags]
internal enum MeshOptimizerSimplificationOptions : uint
{
    None = 0,
    LockBorder = 1u << 0
}

internal readonly record struct MeshOptimizerMeshletDescriptor(
    uint VertexOffset,
    uint TriangleOffset,
    uint VertexCount,
    uint TriangleCount);

internal sealed record MeshOptimizerMeshletBuildResult(
    MeshOptimizerMeshletDescriptor[] Meshlets,
    uint[] Vertices,
    byte[] Triangles);

/// <summary>Thin, bounds-checked access to the meshoptimizer codec shipped by Meshoptimizer.NET.</summary>
internal static unsafe class MeshOptimizerCodec
{
    private const string Library = "meshoptimizer";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMeshlet
    {
        public uint VertexOffset;
        public uint TriangleOffset;
        public uint VertexCount;
        public uint TriangleCount;
    }

    public static MeshOptimizerMeshletBuildResult BuildMeshlets(
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        int maxVertices,
        int maxTriangles)
    {
        if (indices.IsEmpty || indices.Length % 3 != 0)
        {
            throw new ArgumentException(
                "Meshoptimizer meshlet construction requires a non-empty triangle list.",
                nameof(indices));
        }
        if (positions.IsEmpty)
            throw new ArgumentException("Meshoptimizer meshlet construction requires positions.", nameof(positions));
        if (maxVertices is < 3 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxVertices));
        if (maxTriangles is < 4 or > 512 || maxTriangles % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTriangles),
                "Meshoptimizer's triangle limit must be divisible by four and between 4 and 512.");
        }

        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] >= positions.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(indices),
                    $"Index {indices[i]} is outside the vertex buffer.");
            }
        }

        nuint bound = MeshoptBuildMeshletsBound(
            checked((nuint)indices.Length),
            checked((nuint)maxVertices),
            checked((nuint)maxTriangles));
        if (bound == 0 || bound > int.MaxValue)
            throw new InvalidOperationException($"meshoptimizer returned invalid meshlet bound {bound}.");

        int capacity = checked((int)bound);
        var nativeMeshlets = GC.AllocateUninitializedArray<NativeMeshlet>(capacity);
        var meshletVertices = GC.AllocateUninitializedArray<uint>(
            checked(capacity * maxVertices));
        var meshletTriangles = GC.AllocateUninitializedArray<byte>(
            checked(capacity * maxTriangles * 3));

        nuint count;
        fixed (NativeMeshlet* meshletsPtr = nativeMeshlets)
        fixed (uint* meshletVerticesPtr = meshletVertices)
        fixed (byte* meshletTrianglesPtr = meshletTriangles)
        fixed (uint* indicesPtr = indices)
        fixed (Vector3* positionsPtr = positions)
        {
            count = MeshoptBuildMeshlets(
                meshletsPtr,
                meshletVerticesPtr,
                meshletTrianglesPtr,
                indicesPtr,
                checked((nuint)indices.Length),
                (float*)positionsPtr,
                checked((nuint)positions.Length),
                checked((nuint)sizeof(Vector3)),
                checked((nuint)maxVertices),
                checked((nuint)maxTriangles),
                coneWeight: 0.0f);
        }

        if (count == 0 || count > bound)
            throw new InvalidOperationException($"meshoptimizer returned invalid meshlet count {count}.");

        var descriptors = new MeshOptimizerMeshletDescriptor[checked((int)count)];
        int usedVertexCount = 0;
        int usedTriangleByteCount = 0;
        for (int i = 0; i < descriptors.Length; i++)
        {
            NativeMeshlet native = nativeMeshlets[i];
            int vertexEnd = checked((int)(native.VertexOffset + native.VertexCount));
            int triangleEnd = checked((int)(native.TriangleOffset + native.TriangleCount * 3));
            if (native.VertexCount == 0 || native.VertexCount > maxVertices ||
                native.TriangleCount == 0 || native.TriangleCount > maxTriangles ||
                vertexEnd > meshletVertices.Length || triangleEnd > meshletTriangles.Length)
            {
                throw new InvalidOperationException(
                    $"meshoptimizer returned invalid meshlet descriptor {i}.");
            }

            descriptors[i] = new MeshOptimizerMeshletDescriptor(
                native.VertexOffset,
                native.TriangleOffset,
                native.VertexCount,
                native.TriangleCount);
            usedVertexCount = Math.Max(usedVertexCount, vertexEnd);
            usedTriangleByteCount = Math.Max(usedTriangleByteCount, triangleEnd);
        }

        Array.Resize(ref meshletVertices, usedVertexCount);
        Array.Resize(ref meshletTriangles, usedTriangleByteCount);
        return new MeshOptimizerMeshletBuildResult(
            descriptors,
            meshletVertices,
            meshletTriangles);
    }

    public static uint[] Simplify(
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        int targetIndexCount,
        float targetError,
        MeshOptimizerSimplificationOptions options,
        out float resultError)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Meshoptimizer simplification requires a non-empty triangle list.", nameof(indices));
        if (positions.IsEmpty)
            throw new ArgumentException("Meshoptimizer simplification requires positions.", nameof(positions));
        targetIndexCount = Math.Clamp(targetIndexCount - targetIndexCount % 3, 3, indices.Length);
        var destination = GC.AllocateUninitializedArray<uint>(indices.Length);
        fixed (uint* destinationPtr = destination)
        fixed (uint* indicesPtr = indices)
        fixed (Vector3* positionsPtr = positions)
        {
            nuint count = MeshoptSimplify(
                destinationPtr,
                indicesPtr,
                checked((nuint)indices.Length),
                (float*)positionsPtr,
                checked((nuint)positions.Length),
                checked((nuint)sizeof(Vector3)),
                checked((nuint)targetIndexCount),
                targetError,
                (uint)options,
                out resultError);
            if (count == 0 || count > (nuint)indices.Length || count % 3 != 0)
                throw new InvalidOperationException($"meshoptimizer returned invalid simplified index count {count}.");
            Array.Resize(ref destination, checked((int)count));
            return destination;
        }
    }

    public static byte[] EncodeVertexBuffer(ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize)
    {
        if (vertexCount < 0 || vertexSize <= 0 || vertexSize % 4 != 0 || vertices.Length != checked(vertexCount * vertexSize))
            throw new ArgumentException("Invalid meshoptimizer vertex stream dimensions.", nameof(vertices));
        if (vertices.IsEmpty)
            return Array.Empty<byte>();
        nuint bound = MeshoptEncodeVertexBufferBound(checked((nuint)vertexCount), checked((nuint)vertexSize));
        var encoded = GC.AllocateUninitializedArray<byte>(checked((int)bound));
        fixed (byte* destination = encoded)
        fixed (byte* source = vertices)
        {
            nuint length = MeshoptEncodeVertexBuffer(destination, bound, source, checked((nuint)vertexCount), checked((nuint)vertexSize));
            if (length == 0 || length > bound)
                throw new InvalidOperationException("meshoptimizer could not encode the vertex stream.");
            Array.Resize(ref encoded, checked((int)length));
            return encoded;
        }
    }

    public static byte[] EncodeIndexBuffer(ReadOnlySpan<uint> indices, int vertexCount, bool sequence)
    {
        if (indices.IsEmpty)
            return Array.Empty<byte>();
        nuint count = checked((nuint)indices.Length);
        nuint bound = sequence
            ? MeshoptEncodeIndexSequenceBound(count, checked((nuint)vertexCount))
            : MeshoptEncodeIndexBufferBound(count, checked((nuint)vertexCount));
        var encoded = GC.AllocateUninitializedArray<byte>(checked((int)bound));
        fixed (byte* destination = encoded)
        fixed (uint* source = indices)
        {
            nuint length = sequence
                ? MeshoptEncodeIndexSequence(destination, bound, source, count)
                : MeshoptEncodeIndexBuffer(destination, bound, source, count);
            if (length == 0 || length > bound)
                throw new InvalidOperationException("meshoptimizer could not encode the index stream.");
            Array.Resize(ref encoded, checked((int)length));
            return encoded;
        }
    }

    public static void DecodeVertexBuffer(ReadOnlySpan<byte> encoded, Span<byte> destination, int vertexCount, int vertexSize)
    {
        fixed (byte* destinationPtr = destination)
        fixed (byte* encodedPtr = encoded)
        {
            int result = MeshoptDecodeVertexBuffer(destinationPtr, checked((nuint)vertexCount), checked((nuint)vertexSize), encodedPtr, checked((nuint)encoded.Length));
            if (result != 0)
                throw new InvalidDataException($"meshoptimizer vertex decode failed with error {result}.");
        }
    }

    public static void DecodeIndexBuffer(ReadOnlySpan<byte> encoded, Span<uint> destination, bool sequence)
    {
        fixed (uint* destinationPtr = destination)
        fixed (byte* encodedPtr = encoded)
        {
            int result = sequence
                ? MeshoptDecodeIndexSequence(destinationPtr, checked((nuint)destination.Length), sizeof(uint), encodedPtr, checked((nuint)encoded.Length))
                : MeshoptDecodeIndexBuffer(destinationPtr, checked((nuint)destination.Length), sizeof(uint), encodedPtr, checked((nuint)encoded.Length));
            if (result != 0)
                throw new InvalidDataException($"meshoptimizer index decode failed with error {result}.");
        }
    }

    [DllImport(Library, EntryPoint = "meshopt_simplify", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptSimplify(uint* destination, uint* indices, nuint indexCount, float* positions, nuint vertexCount, nuint vertexStride, nuint targetIndexCount, float targetError, uint options, out float resultError);

    [DllImport(Library, EntryPoint = "meshopt_buildMeshletsBound", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptBuildMeshletsBound(
        nuint indexCount,
        nuint maxVertices,
        nuint maxTriangles);

    [DllImport(Library, EntryPoint = "meshopt_buildMeshlets", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptBuildMeshlets(
        NativeMeshlet* meshlets,
        uint* meshletVertices,
        byte* meshletTriangles,
        uint* indices,
        nuint indexCount,
        float* vertexPositions,
        nuint vertexCount,
        nuint vertexPositionsStride,
        nuint maxVertices,
        nuint maxTriangles,
        float coneWeight);

    [DllImport(Library, EntryPoint = "meshopt_encodeVertexBufferBound", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeVertexBufferBound(nuint vertexCount, nuint vertexSize);

    [DllImport(Library, EntryPoint = "meshopt_encodeVertexBuffer", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeVertexBuffer(byte* buffer, nuint bufferSize, void* vertices, nuint vertexCount, nuint vertexSize);

    [DllImport(Library, EntryPoint = "meshopt_decodeVertexBuffer", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MeshoptDecodeVertexBuffer(void* destination, nuint vertexCount, nuint vertexSize, byte* buffer, nuint bufferSize);

    [DllImport(Library, EntryPoint = "meshopt_encodeIndexBufferBound", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeIndexBufferBound(nuint indexCount, nuint vertexCount);

    [DllImport(Library, EntryPoint = "meshopt_encodeIndexBuffer", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeIndexBuffer(byte* buffer, nuint bufferSize, uint* indices, nuint indexCount);

    [DllImport(Library, EntryPoint = "meshopt_decodeIndexBuffer", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MeshoptDecodeIndexBuffer(void* destination, nuint indexCount, nuint indexSize, byte* buffer, nuint bufferSize);

    [DllImport(Library, EntryPoint = "meshopt_encodeIndexSequenceBound", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeIndexSequenceBound(nuint indexCount, nuint vertexCount);

    [DllImport(Library, EntryPoint = "meshopt_encodeIndexSequence", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint MeshoptEncodeIndexSequence(byte* buffer, nuint bufferSize, uint* indices, nuint indexCount);

    [DllImport(Library, EntryPoint = "meshopt_decodeIndexSequence", CallingConvention = CallingConvention.Cdecl)]
    private static extern int MeshoptDecodeIndexSequence(void* destination, nuint indexCount, nuint indexSize, byte* buffer, nuint bufferSize);
}

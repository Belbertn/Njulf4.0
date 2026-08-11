using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Allocation-free spatial ordering and Vose-table refit for an already
/// selected heterogeneous emissive source set. Spatial Morton ordering keeps
/// hierarchy child bounds coherent; the alias table is rebuilt afterwards so
/// source order cannot change the declared global probabilities.
/// </summary>
public sealed class DdgiEmissiveSourceSetBuilder
{
    private readonly Entry[] _entries;
    private readonly double[] _scaled;
    private readonly int[] _small;
    private readonly int[] _large;
    private readonly int[] _aliases;
    private readonly float[] _thresholds;

    public DdgiEmissiveSourceSetBuilder(int capacity)
    {
        if (capacity <= 0 || capacity > DdgiEmissiveTriangleTable.MaximumAliasEntryCount)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = new Entry[capacity];
        _scaled = new double[capacity];
        _small = new int[capacity];
        _large = new int[capacity];
        _aliases = new int[capacity];
        _thresholds = new float[capacity];
    }

    public void OrderAndRebuildAlias(
        Span<GPUDdgiEmissiveSource> sources,
        ReadOnlySpan<double> importance) =>
        OrderAndRebuildAlias(
            sources,
            Span<GPUDdgiEmissiveSurface>.Empty,
            importance);

    public void OrderAndRebuildAlias(
        Span<GPUDdgiEmissiveSource> sources,
        Span<GPUDdgiEmissiveSurface> surfaces,
        ReadOnlySpan<double> importance)
    {
        if (sources.Length > _entries.Length)
            throw new ArgumentOutOfRangeException(nameof(sources));
        if (importance.Length < sources.Length)
            throw new ArgumentException("Importance span is shorter than the source set.", nameof(importance));
        if (!surfaces.IsEmpty && surfaces.Length < sources.Length)
            throw new ArgumentException("Surface span is shorter than the source set.", nameof(surfaces));
        if (sources.IsEmpty)
            return;

        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int index = 0; index < sources.Length; index++)
        {
            Vector3 center = SourceCenter(sources[index]);
            minimum = Vector3.Min(minimum, center);
            maximum = Vector3.Max(maximum, center);
        }

        Vector3 extent = maximum - minimum;
        double totalImportance = 0.0;
        for (int index = 0; index < sources.Length; index++)
        {
            double weight = importance[index];
            if (!double.IsFinite(weight) || weight <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(importance),
                    $"Emissive source {index} has non-positive or non-finite importance.");
            }

            GPUDdgiEmissiveSource source = sources[index];
            Vector3 center = SourceCenter(source);
            _entries[index] = new Entry(
                source,
                surfaces.IsEmpty ? default : surfaces[index],
                weight,
                MortonKey(center, minimum, extent),
                StablePayloadKey(source));
            totalImportance += weight;
        }
        if (!double.IsFinite(totalImportance) || totalImportance <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(importance), "Total source importance is invalid.");

        Array.Sort(_entries, 0, sources.Length, EntryComparer.Instance);
        for (int index = 0; index < sources.Length; index++)
        {
            sources[index] = _entries[index].Source;
            if (!surfaces.IsEmpty)
                surfaces[index] = _entries[index].Surface;
            _scaled[index] = _entries[index].Importance * sources.Length / totalImportance;
        }

        int smallCount = 0;
        int largeCount = 0;
        for (int index = 0; index < sources.Length; index++)
        {
            if (_scaled[index] < 1.0)
                _small[smallCount++] = index;
            else
                _large[largeCount++] = index;
        }

        while (smallCount > 0 && largeCount > 0)
        {
            int low = _small[--smallCount];
            int high = _large[--largeCount];
            _thresholds[low] = (float)Math.Clamp(_scaled[low], 0.0, 1.0);
            _aliases[low] = high;
            _scaled[high] = _scaled[high] + _scaled[low] - 1.0;
            if (_scaled[high] < 1.0)
                _small[smallCount++] = high;
            else
                _large[largeCount++] = high;
        }
        while (largeCount > 0)
        {
            int index = _large[--largeCount];
            _thresholds[index] = 1.0f;
            _aliases[index] = index;
        }
        while (smallCount > 0)
        {
            int index = _small[--smallCount];
            _thresholds[index] = 1.0f;
            _aliases[index] = index;
        }

        for (int index = 0; index < sources.Length; index++)
        {
            GPUDdgiEmissiveSource source = sources[index];
            uint packed = BitConverter.SingleToUInt32Bits(source.Edge2AliasFlags.W);
            uint flags = packed & ~DdgiEmissiveTriangleTable.AliasIndexMask;
            source.Edge1AliasProbability.W = _thresholds[index];
            source.Edge2AliasFlags.W = BitConverter.UInt32BitsToSingle(
                flags | ((uint)_aliases[index] & DdgiEmissiveTriangleTable.AliasIndexMask));
            source.RadianceSelectionProbability.W =
                (float)(_entries[index].Importance / totalImportance);
            sources[index] = source;
        }
    }

    private static Vector3 SourceCenter(GPUDdgiEmissiveSource source)
    {
        Vector3 origin = Xyz(source.Vertex0Area);
        DdgiEmissiveSourceFlags flags = DdgiEmissiveTriangleTable.DecodeFlags(source);
        return (flags & DdgiEmissiveSourceFlags.MacroEmitter) != 0
            ? origin
            : origin + (Xyz(source.Edge1AliasProbability) + Xyz(source.Edge2AliasFlags)) / 3.0f;
    }

    private static uint MortonKey(Vector3 point, Vector3 minimum, Vector3 extent)
    {
        uint x = Quantize(point.X, minimum.X, extent.X);
        uint y = Quantize(point.Y, minimum.Y, extent.Y);
        uint z = Quantize(point.Z, minimum.Z, extent.Z);
        return ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
    }

    private static uint Quantize(float value, float minimum, float extent)
    {
        if (!(extent > 1e-20f) || !float.IsFinite(extent))
            return 0;
        float normalized = Math.Clamp((value - minimum) / extent, 0.0f, 1.0f);
        return (uint)Math.Clamp((int)MathF.Round(normalized * 1023.0f), 0, 1023);
    }

    private static uint ExpandBits(uint value)
    {
        value &= 0x000003ffu;
        value = (value | value << 16) & 0x030000ffu;
        value = (value | value << 8) & 0x0300f00fu;
        value = (value | value << 4) & 0x030c30c3u;
        value = (value | value << 2) & 0x09249249u;
        return value;
    }

    private static ulong StablePayloadKey(GPUDdgiEmissiveSource source)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        Add(source.Vertex0Area);
        Add(source.Edge1AliasProbability, ignoreW: true);
        Add(source.Edge2AliasFlags, ignoreW: true);
        Add(source.RadianceSelectionProbability, ignoreW: true);
        return hash;

        void Add(Vector4 value, bool ignoreW = false)
        {
            Mix(BitConverter.SingleToUInt32Bits(value.X));
            Mix(BitConverter.SingleToUInt32Bits(value.Y));
            Mix(BitConverter.SingleToUInt32Bits(value.Z));
            if (!ignoreW)
                Mix(BitConverter.SingleToUInt32Bits(value.W));
        }

        void Mix(uint value)
        {
            hash ^= value;
            hash *= prime;
        }
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    private readonly record struct Entry(
        GPUDdgiEmissiveSource Source,
        GPUDdgiEmissiveSurface Surface,
        double Importance,
        uint Morton,
        ulong StableKey);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static EntryComparer Instance { get; } = new();

        public int Compare(Entry left, Entry right)
        {
            int morton = left.Morton.CompareTo(right.Morton);
            return morton != 0 ? morton : left.StableKey.CompareTo(right.StableKey);
        }
    }
}

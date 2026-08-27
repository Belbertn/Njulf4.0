using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

[Flags]
public enum SimpleDdgiNearFieldSurfaceFlags : uint
{
    None = 0,
    Opaque = 1u << 0,
    AlphaMasked = 1u << 1,
    AuthoredFoliage = 1u << 2,
    ProceduralGrass = 1u << 3,
    CoverageValid = 1u << 4,
    MotionVectorsValid = 1u << 5
}

/// <summary>
/// V13 surface-table element addressed by the 16-bit token in the receiver
/// payload. Entries are immutable after a frame bank is sealed.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public readonly record struct GPUSimpleDdgiNearFieldSurfaceEntry(
    uint StableObjectId,
    uint StableMaterialId,
    ushort ObjectRevision,
    ushort MaterialRevision,
    SimpleDdgiNearFieldSurfaceFlags Flags)
{
    public bool IsEligible => StableObjectId != 0u && StableMaterialId != 0u &&
        (Flags & SimpleDdgiNearFieldSurfaceFlags.CoverageValid) != 0 &&
        (Flags & SimpleDdgiNearFieldSurfaceFlags.MotionVectorsValid) != 0;
}

public readonly record struct SimpleDdgiNearFieldSurfaceKey(
    uint StableObjectId,
    uint StableMaterialId);

/// <summary>
/// CPU publication side of the frame-buffered C5 surface table. Capacity
/// exhaustion rejects only the surface being registered. Revision wrap is
/// promoted to a scene-generation change so an old 16-bit revision can never
/// alias reusable history.
/// </summary>
public sealed class SimpleDdgiNearFieldSurfaceTable
{
    public const int Capacity =
        (int)SimpleDdgiNearFieldResidualGpuAbi.MaximumSurfaceTableEntryCount;

    private readonly Bank[] _banks;
    private readonly Dictionary<SimpleDdgiNearFieldSurfaceKey, RevisionState>
        _publishedRevisions = new();
    private Bank? _building;

    public SimpleDdgiNearFieldSurfaceTable(int frameBankCount)
    {
        if (frameBankCount < 2)
            throw new ArgumentOutOfRangeException(nameof(frameBankCount));
        _banks = new Bank[frameBankCount];
        for (int i = 0; i < _banks.Length; i++)
            _banks[i] = new Bank();
    }

    public uint SceneGeneration { get; private set; } = 1u;
    public uint OverflowPixelCount { get; private set; }
    public int ActiveEntryCount => _building?.Entries.Count ?? 0;

    public void BeginFrame(uint frameIndex)
    {
        Bank bank = _banks[frameIndex % (uint)_banks.Length];
        bank.Entries.Clear();
        bank.Tokens.Clear();
        bank.Sealed = false;
        _building = bank;
        OverflowPixelCount = 0u;
    }

    public bool TryGetOrAdd(
        in GPUSimpleDdgiNearFieldSurfaceEntry entry,
        out ushort token)
    {
        Bank bank = _building ?? throw new InvalidOperationException(
            "BeginFrame must be called before publishing C5 surfaces.");
        if (bank.Sealed)
            throw new InvalidOperationException("The current C5 surface bank is sealed.");

        token = ushort.MaxValue;
        if (!entry.IsEligible)
            return false;

        var key = new SimpleDdgiNearFieldSurfaceKey(
            entry.StableObjectId, entry.StableMaterialId);
        if (bank.Tokens.TryGetValue(key, out ushort existingToken))
        {
            token = existingToken;
            return true;
        }
        if (bank.Entries.Count >= Capacity)
        {
            OverflowPixelCount++;
            return false;
        }

        DetectRevisionWrap(key, entry.ObjectRevision, entry.MaterialRevision);
        token = checked((ushort)bank.Entries.Count);
        bank.Entries.Add(entry);
        bank.Tokens.Add(key, token);
        return true;
    }

    public ReadOnlyMemory<GPUSimpleDdgiNearFieldSurfaceEntry> Seal()
    {
        Bank bank = _building ?? throw new InvalidOperationException(
            "No C5 surface bank is being built.");
        bank.Sealed = true;
        return bank.Entries.ToArray();
    }

    public void RecordInvalidOverflowPixel() => OverflowPixelCount++;

    private void DetectRevisionWrap(
        SimpleDdgiNearFieldSurfaceKey key,
        ushort objectRevision,
        ushort materialRevision)
    {
        if (_publishedRevisions.TryGetValue(key, out RevisionState previous) &&
            (objectRevision < previous.ObjectRevision ||
             materialRevision < previous.MaterialRevision))
        {
            SceneGeneration = SceneGeneration == uint.MaxValue
                ? 1u
                : SceneGeneration + 1u;
        }
        _publishedRevisions[key] = new RevisionState(
            objectRevision, materialRevision);
    }

    private sealed class Bank
    {
        public List<GPUSimpleDdgiNearFieldSurfaceEntry> Entries { get; } =
            new(Capacity);
        public Dictionary<SimpleDdgiNearFieldSurfaceKey, ushort> Tokens { get; } =
            new(Capacity);
        public bool Sealed { get; set; }
    }

    private readonly record struct RevisionState(
        ushort ObjectRevision,
        ushort MaterialRevision);
}

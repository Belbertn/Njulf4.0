using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Descriptors;

/// <summary>
/// Authoritative allocator for the dynamically owned portion of the bindless
/// sampled-image table. Candidate selection is separated from commit so a
/// descriptor write failure cannot consume an index.
/// </summary>
internal sealed class BindlessTextureIndexAllocator
{
    private readonly int _firstIndex;
    private readonly int _exclusiveEndIndex;
    private readonly Stack<int> _freeIndices = new();
    private readonly HashSet<int> _allocatedIndices = new();
    private int _nextIndex;
    private int _highWater;

    public BindlessTextureIndexAllocator(
        int firstIndex,
        int exclusiveEndIndex)
    {
        if (firstIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(firstIndex));
        if (exclusiveEndIndex <= firstIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(exclusiveEndIndex),
                "The exclusive end index must be greater than the first index.");
        }

        _firstIndex = firstIndex;
        _exclusiveEndIndex = exclusiveEndIndex;
        _nextIndex = firstIndex;
    }

    public int Capacity => _exclusiveEndIndex - _firstIndex;

    public int Used => _allocatedIndices.Count;

    public int HighWater => _highWater;

    /// <summary>
    /// Returns the index whose descriptor may be written next without mutating
    /// allocator state.
    /// </summary>
    public int GetAllocationCandidate()
    {
        if (_freeIndices.Count > 0)
            return _freeIndices.Peek();
        if (_nextIndex >= _exclusiveEndIndex)
            throw new InvalidOperationException("Max texture count reached.");

        return _nextIndex;
    }

    /// <summary>
    /// Commits the current candidate after descriptor publication succeeds.
    /// </summary>
    public void CommitAllocation(int index)
    {
        int candidate = GetAllocationCandidate();
        if (index != candidate)
        {
            throw new InvalidOperationException(
                $"Bindless texture allocation candidate changed from {index} to {candidate}.");
        }

        if (!_allocatedIndices.Add(index))
        {
            throw new InvalidOperationException(
                $"Bindless texture index {index} is already allocated.");
        }

        try
        {
            if (_freeIndices.Count > 0)
            {
                int reusableIndex = _freeIndices.Pop();
                if (reusableIndex != index)
                {
                    _freeIndices.Push(reusableIndex);
                    throw new InvalidOperationException(
                        "Bindless texture free-list changed during allocation.");
                }
            }
            else
            {
                _nextIndex = checked(_nextIndex + 1);
            }
        }
        catch
        {
            _allocatedIndices.Remove(index);
            throw;
        }

        _highWater = Math.Max(_highWater, _allocatedIndices.Count);
    }

    /// <summary>
    /// Releases an allocator-owned dynamic index exactly once.
    /// </summary>
    public void Free(int index)
    {
        if (index < _firstIndex || index >= _exclusiveEndIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"Index must be in the dynamic texture range [{_firstIndex}, {_exclusiveEndIndex}).");
        }

        if (!_allocatedIndices.Contains(index))
        {
            throw new InvalidOperationException(
                $"Bindless texture index {index} is not currently allocated.");
        }

        _freeIndices.Push(index);
        if (!_allocatedIndices.Remove(index))
        {
            int restoredIndex = _freeIndices.Pop();
            throw new InvalidOperationException(
                $"Bindless texture index {restoredIndex} could not be released.");
        }
    }
}

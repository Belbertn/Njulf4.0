using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public sealed class DdgiLocalVolumeSlotAllocator
    {
        public const int MinimumSlotProbeCapacity = 8;

        private readonly LocalSlotState[] _slots = new LocalSlotState[DdgiProbeVolumeManager.AbsoluteMaxVolumeCount];
        private uint _nextGeneration = 1;
        private int _slotCount;
        private int _slotProbeCapacity;
        private int _firstProbeIndex;
        private int _lastRemainingProbeBudget;
        private int _lastMaxVolumeCount;
        private ulong _lastSceneCapacitySignature;

        public DdgiLocalVolumeSlotAssignmentResult Assign(
            IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes,
            List<GlobalIlluminationProbeVolume> destination,
            List<DdgiProbeVolumeRuntimeMetadata> metadataDestination,
            int maxVolumeCount,
            int remainingProbeBudget,
            int firstProbeIndex,
            ICamera camera)
        {
            if (authoredVolumes == null)
                throw new ArgumentNullException(nameof(authoredVolumes));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (metadataDestination == null)
                throw new ArgumentNullException(nameof(metadataDestination));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            if (maxVolumeCount <= 0 || remainingProbeBudget <= 0 || authoredVolumes.Count == 0)
            {
                ReleaseAllSlots("no-capacity");
                return DdgiLocalVolumeSlotAssignmentResult.Empty;
            }

            ulong sceneCapacitySignature = CreateSceneCapacitySignature(authoredVolumes);
            ConfigurePool(maxVolumeCount, remainingProbeBudget, firstProbeIndex, sceneCapacitySignature);
            if (_slotCount <= 0 || _slotProbeCapacity <= 0)
                return DdgiLocalVolumeSlotAssignmentResult.Empty with { EvictionReason = "no-capacity" };

            var candidates = new List<AuthoredVolumeAdmissionCandidate>(authoredVolumes.Count);
            for (int i = 0; i < authoredVolumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? volume = authoredVolumes[i];
                if (volume == null || !volume.Enabled || volume.ProbeCount > _slotProbeCapacity)
                    continue;

                candidates.Add(new AuthoredVolumeAdmissionCandidate(
                    volume,
                    i,
                    CreateStableVolumeKey(volume, i),
                    ScoreAuthoredVolume(volume, camera)));
            }

            candidates.Sort(static (left, right) =>
            {
                int scoreCompare = left.Score.CompareTo(right.Score);
                return scoreCompare != 0 ? scoreCompare : left.OriginalIndex.CompareTo(right.OriginalIndex);
            });

            int selectedCount = Math.Min(_slotCount, candidates.Count);
            Span<ulong> selectedKeys = stackalloc ulong[DdgiProbeVolumeManager.AbsoluteMaxVolumeCount];
            for (int i = 0; i < selectedCount; i++)
                selectedKeys[i] = candidates[i].StableKey;

            string evictionReason = ReleaseUnselectedSlots(selectedKeys[..selectedCount]);
            int activeSlots = 0;
            int authoredProbeCount = 0;
            int maxGeneration = 0;

            for (int i = 0; i < selectedCount && destination.Count < DdgiProbeVolumeManager.AbsoluteMaxVolumeCount; i++)
            {
                AuthoredVolumeAdmissionCandidate candidate = candidates[i];
                int slotIndex = FindSlot(candidate.StableKey);
                if (slotIndex < 0)
                    slotIndex = AllocateSlot(candidate.StableKey, ref evictionReason);
                if (slotIndex < 0)
                    continue;

                LocalSlotState slot = _slots[slotIndex];
                GlobalIlluminationProbeVolume volume = candidate.Volume;
                int physicalFirstProbeIndex = _firstProbeIndex + slotIndex * _slotProbeCapacity;
                float edgeBlendFraction = CalculateEdgeBlendFraction(volume);
                uint flags = GlobalIlluminationProbeVolumeData.VolumeInitializedFlag |
                    GlobalIlluminationProbeVolumeData.VolumeAuthoredPriorityFlag |
                    GlobalIlluminationProbeVolumeData.VolumeLocalSlotFlag;
                if (volume.Interior)
                    flags |= GlobalIlluminationProbeVolumeData.VolumeInteriorFlag;

                destination.Add(volume);
                metadataDestination.Add(new DdgiProbeVolumeRuntimeMetadata(
                    DdgiProbeVolumeKind.Authored,
                    -1,
                    0,
                    0,
                    0,
                    slotIndex,
                    slot.Generation,
                    volume.StreamingCellId,
                    edgeBlendFraction,
                    flags,
                    physicalFirstProbeIndex,
                    _slotProbeCapacity,
                    slotIndex,
                    slot.Generation,
                    volume.StreamingCellId,
                    (int)volume.QualityClass,
                    volume.Priority,
                    volume.BlendDistance,
                    volume.UpdatePriority));
                authoredProbeCount = checked(authoredProbeCount + volume.ProbeCount);
                activeSlots++;
                maxGeneration = Math.Max(maxGeneration, slot.Generation);
            }

            ulong allocationSignature = CreateAllocationSignature();
            return new DdgiLocalVolumeSlotAssignmentResult(
                authoredProbeCount,
                _slotCount,
                _slotProbeCapacity,
                activeSlots,
                activeSlots > 0 ? maxGeneration : 0,
                activeSlots > 0 ? checked(_slotCount * _slotProbeCapacity) : 0,
                allocationSignature,
                evictionReason);
        }

        private void ConfigurePool(int maxVolumeCount, int remainingProbeBudget, int firstProbeIndex, ulong sceneCapacitySignature)
        {
            int largestVolumeProbeCount = Math.Max(MinimumSlotProbeCapacity, ExtractLargestProbeCount(sceneCapacitySignature));
            int slotProbeCapacity = Math.Max(MinimumSlotProbeCapacity, largestVolumeProbeCount);
            int slotCount = Math.Min(maxVolumeCount, remainingProbeBudget / slotProbeCapacity);
            slotCount = Math.Clamp(slotCount, 0, DdgiProbeVolumeManager.AbsoluteMaxVolumeCount);

            if (slotCount == _slotCount &&
                slotProbeCapacity == _slotProbeCapacity &&
                firstProbeIndex == _firstProbeIndex &&
                remainingProbeBudget == _lastRemainingProbeBudget &&
                maxVolumeCount == _lastMaxVolumeCount &&
                sceneCapacitySignature == _lastSceneCapacitySignature)
            {
                return;
            }

            _slotCount = slotCount;
            _slotProbeCapacity = slotProbeCapacity;
            _firstProbeIndex = firstProbeIndex;
            _lastRemainingProbeBudget = remainingProbeBudget;
            _lastMaxVolumeCount = maxVolumeCount;
            _lastSceneCapacitySignature = sceneCapacitySignature;
            ReleaseAllSlots("allocation-change");
        }

        private string ReleaseUnselectedSlots(ReadOnlySpan<ulong> selectedKeys)
        {
            string reason = "none";
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!_slots[i].Assigned)
                    continue;

                bool selected = false;
                for (int keyIndex = 0; keyIndex < selectedKeys.Length; keyIndex++)
                {
                    if (_slots[i].StableKey == selectedKeys[keyIndex])
                    {
                        selected = true;
                        break;
                    }
                }

                if (selected)
                    continue;

                _slots[i] = default;
                reason = "streamed-out";
            }

            return reason;
        }

        private void ReleaseAllSlots(string reason)
        {
            _ = reason;
            Array.Clear(_slots, 0, _slots.Length);
        }

        private int FindSlot(ulong stableKey)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (_slots[i].Assigned && _slots[i].StableKey == stableKey)
                    return i;
            }

            return -1;
        }

        private int AllocateSlot(ulong stableKey, ref string evictionReason)
        {
            for (int i = 0; i < _slotCount; i++)
            {
                if (_slots[i].Assigned)
                    continue;

                _slots[i] = new LocalSlotState(stableKey, NextGeneration(), Assigned: true);
                return i;
            }

            evictionReason = "slot-pressure";
            return -1;
        }

        private int NextGeneration()
        {
            if (_nextGeneration == uint.MaxValue)
                _nextGeneration = 1;

            return checked((int)_nextGeneration++);
        }

        private ulong CreateAllocationSignature()
        {
            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)_slotCount);
            hash = HashAdd(hash, (uint)_slotProbeCapacity);
            hash = HashAdd(hash, (uint)_firstProbeIndex);
            return HashAdd(hash, (uint)_lastSceneCapacitySignature);
        }

        private static ulong CreateSceneCapacitySignature(IReadOnlyList<GlobalIlluminationProbeVolume> authoredVolumes)
        {
            ulong hash = 14695981039346656037UL;
            int largestProbeCount = 0;
            for (int i = 0; i < authoredVolumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? volume = authoredVolumes[i];
                if (volume == null || !volume.Enabled)
                    continue;

                largestProbeCount = Math.Max(largestProbeCount, volume.ProbeCount);
                hash = HashAdd(hash, (uint)Math.Max(0, volume.ProbeCount));
                hash = HashAdd(hash, (uint)Math.Max(0, volume.StreamingCellId));
            }

            hash = HashAdd(hash, (uint)Math.Max(MinimumSlotProbeCapacity, largestProbeCount));
            return (hash & 0xffffffff00000000UL) | (uint)Math.Max(MinimumSlotProbeCapacity, largestProbeCount);
        }

        private static int ExtractLargestProbeCount(ulong sceneCapacitySignature) =>
            Math.Max(MinimumSlotProbeCapacity, unchecked((int)(sceneCapacitySignature & 0xffffffffUL)));

        private static ulong CreateStableVolumeKey(GlobalIlluminationProbeVolume volume, int originalIndex)
        {
            if (volume.StreamingCellId > 0)
                return unchecked((ulong)volume.StreamingCellId);

            ulong hash = 14695981039346656037UL;
            hash = HashAdd(hash, (uint)Math.Max(0, originalIndex));
            hash = HashAdd(hash, volume.Name ?? string.Empty);
            hash = HashAdd(hash, volume.Origin.X);
            hash = HashAdd(hash, volume.Origin.Y);
            hash = HashAdd(hash, volume.Origin.Z);
            return hash == 0UL ? 1UL : hash;
        }

        private static float ScoreAuthoredVolume(GlobalIlluminationProbeVolume volume, ICamera camera)
        {
            float score = 0.0f;
            BoundingBox bounds = volume.Bounds;
            Vector3 cameraPosition = camera.Position;
            bool containsCamera = bounds.Contains(cameraPosition);
            if (containsCamera)
                score -= 500_000.0f;
            if (containsCamera && volume.Interior)
                score -= 80_000.0f;

            Vector3 closest = new(
                Math.Clamp(cameraPosition.X, bounds.Min.X, bounds.Max.X),
                Math.Clamp(cameraPosition.Y, bounds.Min.Y, bounds.Max.Y),
                Math.Clamp(cameraPosition.Z, bounds.Min.Z, bounds.Max.Z));
            float distanceSquared = Vector3.DistanceSquared(cameraPosition, closest);
            float radius = Math.Max(0.001f, bounds.Size.Length() * 0.5f);
            float approximateScreenCoverage = radius * radius / Math.Max(1.0f, distanceSquared);
            float density = volume.ProbeCount / Math.Max(1.0f, volume.Size.X * volume.Size.Y * volume.Size.Z);

            score += distanceSquared;
            score -= volume.Priority * 2_500.0f;
            score -= volume.UpdatePriority * 20.0f;
            score -= approximateScreenCoverage * 25_000.0f;
            score -= density * 250.0f;
            score += Math.Max(0, volume.ProbeCount) * 0.01f;
            return score;
        }

        private static float CalculateEdgeBlendFraction(GlobalIlluminationProbeVolume volume)
        {
            if (volume.BlendDistance <= 0.0f)
                return 0.0f;

            float minAxis = MathF.Min(volume.Size.X, MathF.Min(volume.Size.Y, volume.Size.Z));
            if (!float.IsFinite(minAxis) || minAxis <= 0.0f)
                return 0.0f;

            return Math.Clamp(volume.BlendDistance / minAxis, 0.0f, 0.5f);
        }

        private static ulong HashAdd(ulong hash, string value)
        {
            for (int i = 0; i < value.Length; i++)
                hash = HashAdd(hash, value[i]);
            return hash;
        }

        private static ulong HashAdd(ulong hash, float value) =>
            HashAdd(hash, BitConverter.SingleToUInt32Bits(value));

        private static ulong HashAdd(ulong hash, char value) =>
            HashAdd(hash, (uint)value);

        private static ulong HashAdd(ulong hash, uint value)
        {
            const ulong prime = 1099511628211UL;
            hash ^= value & 0xff;
            hash *= prime;
            hash ^= (value >> 8) & 0xff;
            hash *= prime;
            hash ^= (value >> 16) & 0xff;
            hash *= prime;
            hash ^= (value >> 24) & 0xff;
            hash *= prime;
            return hash;
        }

        private readonly record struct LocalSlotState(ulong StableKey, int Generation, bool Assigned);

        private readonly record struct AuthoredVolumeAdmissionCandidate(
            GlobalIlluminationProbeVolume Volume,
            int OriginalIndex,
            ulong StableKey,
            float Score);
    }

    public readonly record struct DdgiLocalVolumeSlotAssignmentResult(
        int AuthoredProbeCount,
        int SlotCount,
        int SlotProbeCapacity,
        int ActiveSlotCount,
        int LastGeneration,
        int LocalPoolProbeCount,
        ulong AllocationSignature,
        string EvictionReason)
    {
        public static DdgiLocalVolumeSlotAssignmentResult Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            0,
            0UL,
            "none");
    }
}

using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiPageDemandClass : byte
{
    None = 0,
    SuppressedRetry = 1,
    NonDepthTouch = 2,
    RecentRetention = 3,
    PublicationLatency = 4,
    ReceiverMiss = 5,
    VisibleSurface = 6,
    DevelopmentPin = 7
}

public enum SimpleDdgiPageLifecycle : byte
{
    NonResident = 0,
    Initializing = 1,
    ResidentFresh = 2,
    ResidentPublished = 3,
    ExpiredCandidate = 4,
    SuppressedEmpty = 5
}

public enum SimpleDdgiPageTransitionKind : byte
{
    None = 0,
    Admit = 1,
    Evict = 2,
    Suppress = 3,
    Reactivate = 4
}

public readonly record struct SimpleDdgiPageDemand(
    int VirtualPageIndex,
    SimpleDdgiPageDemandClass DemandClass,
    byte DistanceBucket,
    int VolumePriority = 0,
    bool Pin = false);

public readonly record struct SimpleDdgiPageReferenceSettings(
    ulong RetentionFrames,
    int MaximumAdmissionsPerFrame,
    int MaximumEvictionsPerFrame,
    int EmptySuppressionConfirmationCount,
    ulong SuppressedRetryFrames)
{
    public SimpleDdgiPageReferenceSettings Validate(int physicalPageCapacity)
    {
        if (RetentionFrames > 3_600UL)
            throw new ArgumentOutOfRangeException(nameof(RetentionFrames));
        if (MaximumAdmissionsPerFrame < 0 ||
            MaximumAdmissionsPerFrame > physicalPageCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAdmissionsPerFrame));
        }
        if (MaximumEvictionsPerFrame < 0 ||
            MaximumEvictionsPerFrame > physicalPageCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEvictionsPerFrame));
        }
        if (EmptySuppressionConfirmationCount < 1 ||
            EmptySuppressionConfirmationCount > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmptySuppressionConfirmationCount));
        }
        if (SuppressedRetryFrames < 1UL || SuppressedRetryFrames > 36_000UL)
            throw new ArgumentOutOfRangeException(nameof(SuppressedRetryFrames));
        return this;
    }
}

public readonly record struct SimpleDdgiPageTransition(
    SimpleDdgiPageTransitionKind Kind,
    int VirtualPageIndex,
    int PhysicalPageIndex,
    uint MappingGeneration,
    int PreviousVirtualPageIndex = -1);

public readonly record struct SimpleDdgiPageReconcileSummary(
    ulong FrameSerial,
    int DemandedPageCount,
    int ResidentPageCount,
    int FreePageCount,
    int PublishedPageCount,
    int SuppressedPageCount,
    int AdmissionCount,
    int EvictionCount,
    int FailedAdmissionCount,
    bool PoolPressure,
    int ConsecutivePressureFrames,
    uint ResidencyResourceGeneration,
    uint LastMappingGeneration);

public readonly record struct SimpleDdgiPageReferenceState(
    int PhysicalPageIndex,
    uint MappingGeneration,
    SimpleDdgiPageLifecycle Lifecycle,
    SimpleDdgiPageDemandClass DemandClass,
    byte DistanceBucket,
    ulong LastRelevantFrame,
    ulong LastPublishedFrame,
    bool InFlight,
    bool Pinned,
    int EmptyConfirmationCount,
    uint GeometryGeneration);

/// <summary>
/// Deterministic allocation oracle used by unit tests, Shadow qualification and
/// delayed GPU comparison.  Construction allocates fixed arrays; Reconcile does
/// not allocate and atomics are intentionally absent from winner selection.
/// </summary>
public sealed class SimpleDdgiProbePageReferenceModel
{
    private const uint MappingGenerationWrapThreshold = uint.MaxValue - 65_535u;

    private struct PageState
    {
        public int PhysicalPageIndex;
        public uint MappingGeneration;
        public SimpleDdgiPageLifecycle Lifecycle;
        public SimpleDdgiPageDemandClass DemandClass;
        public byte DistanceBucket;
        public int VolumePriority;
        public ulong DemandFirstFrame;
        public ulong LastRelevantFrame;
        public ulong LastPublishedFrame;
        public ulong LastSuppressedFrame;
        public bool InFlight;
        public bool Pinned;
        public int EmptyConfirmationCount;
        public uint GeometryGeneration;
    }

    private readonly struct AdmissionCandidate
    {
        public AdmissionCandidate(
            int virtualPageIndex,
            SimpleDdgiPageDemandClass demandClass,
            byte distanceBucket,
            ulong demandFirstFrame,
            int volumePriority)
        {
            VirtualPageIndex = virtualPageIndex;
            DemandClass = demandClass;
            DistanceBucket = distanceBucket;
            DemandFirstFrame = demandFirstFrame;
            VolumePriority = volumePriority;
        }

        public int VirtualPageIndex { get; }
        public SimpleDdgiPageDemandClass DemandClass { get; }
        public byte DistanceBucket { get; }
        public ulong DemandFirstFrame { get; }
        public int VolumePriority { get; }
    }

    private readonly struct VictimCandidate
    {
        public VictimCandidate(
            int virtualPageIndex,
            int physicalPageIndex,
            bool confirmedEmpty,
            bool expired,
            ulong lastRelevantFrame,
            SimpleDdgiPageDemandClass demandClass,
            byte distanceBucket)
        {
            VirtualPageIndex = virtualPageIndex;
            PhysicalPageIndex = physicalPageIndex;
            ConfirmedEmpty = confirmedEmpty;
            Expired = expired;
            LastRelevantFrame = lastRelevantFrame;
            DemandClass = demandClass;
            DistanceBucket = distanceBucket;
        }

        public int VirtualPageIndex { get; }
        public int PhysicalPageIndex { get; }
        public bool ConfirmedEmpty { get; }
        public bool Expired { get; }
        public ulong LastRelevantFrame { get; }
        public SimpleDdgiPageDemandClass DemandClass { get; }
        public byte DistanceBucket { get; }
    }

    private sealed class AdmissionComparer : IComparer<AdmissionCandidate>
    {
        public static readonly AdmissionComparer Instance = new();

        public int Compare(AdmissionCandidate left, AdmissionCandidate right)
        {
            int demand = right.DemandClass.CompareTo(left.DemandClass);
            if (demand != 0)
                return demand;
            int distance = left.DistanceBucket.CompareTo(right.DistanceBucket);
            if (distance != 0)
                return distance;
            int age = left.DemandFirstFrame.CompareTo(right.DemandFirstFrame);
            if (age != 0)
                return age;
            int priority = right.VolumePriority.CompareTo(left.VolumePriority);
            return priority != 0
                ? priority
                : left.VirtualPageIndex.CompareTo(right.VirtualPageIndex);
        }
    }

    private sealed class VictimComparer : IComparer<VictimCandidate>
    {
        public static readonly VictimComparer Instance = new();

        public int Compare(VictimCandidate left, VictimCandidate right)
        {
            int empty = right.ConfirmedEmpty.CompareTo(left.ConfirmedEmpty);
            if (empty != 0)
                return empty;
            int expired = right.Expired.CompareTo(left.Expired);
            if (expired != 0)
                return expired;
            int relevant = left.LastRelevantFrame.CompareTo(right.LastRelevantFrame);
            if (relevant != 0)
                return relevant;
            int demand = left.DemandClass.CompareTo(right.DemandClass);
            if (demand != 0)
                return demand;
            int distance = right.DistanceBucket.CompareTo(left.DistanceBucket);
            return distance != 0
                ? distance
                : left.PhysicalPageIndex.CompareTo(right.PhysicalPageIndex);
        }
    }

    private readonly PageState[] _pages;
    private readonly int[] _physicalOwners;
    private readonly AdmissionCandidate[] _admissionCandidates;
    private readonly VictimCandidate[] _victimCandidates;
    private uint _mappingGeneration;
    private uint _residencyResourceGeneration = 1u;
    private uint _geometryGeneration = 1u;
    private int _consecutivePressureFrames;

    public SimpleDdgiProbePageReferenceModel(
        int virtualPageCount,
        int physicalPageCapacity)
    {
        if (virtualPageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(virtualPageCount));
        if (physicalPageCapacity < 0 || physicalPageCapacity > virtualPageCount)
            throw new ArgumentOutOfRangeException(nameof(physicalPageCapacity));

        _pages = new PageState[virtualPageCount];
        _physicalOwners = new int[physicalPageCapacity];
        _admissionCandidates = new AdmissionCandidate[virtualPageCount];
        _victimCandidates = new VictimCandidate[physicalPageCapacity];
        Array.Fill(_physicalOwners, -1);
        for (int index = 0; index < _pages.Length; index++)
        {
            _pages[index].PhysicalPageIndex = -1;
            _pages[index].DistanceBucket = byte.MaxValue;
            _pages[index].GeometryGeneration = _geometryGeneration;
        }
    }

    public int VirtualPageCount => _pages.Length;
    public int PhysicalPageCapacity => _physicalOwners.Length;
    public uint ResidencyResourceGeneration => _residencyResourceGeneration;
    public uint LastMappingGeneration => _mappingGeneration;
    public bool RequiresResourceTransaction =>
        _mappingGeneration >= MappingGenerationWrapThreshold;

    public SimpleDdgiPageReferenceState GetPageState(int virtualPageIndex)
    {
        PageState page = GetPage(virtualPageIndex);
        return new SimpleDdgiPageReferenceState(
            page.PhysicalPageIndex,
            page.MappingGeneration,
            page.Lifecycle,
            page.DemandClass,
            page.DistanceBucket,
            page.LastRelevantFrame,
            page.LastPublishedFrame,
            page.InFlight,
            page.Pinned,
            page.EmptyConfirmationCount,
            page.GeometryGeneration);
    }

    public int GetPhysicalOwner(int physicalPageIndex)
    {
        if ((uint)physicalPageIndex >= (uint)_physicalOwners.Length)
            throw new ArgumentOutOfRangeException(nameof(physicalPageIndex));
        return _physicalOwners[physicalPageIndex];
    }

    public void SetPinned(int virtualPageIndex, bool pinned)
    {
        ValidateVirtualPage(virtualPageIndex);
        _pages[virtualPageIndex].Pinned = pinned;
    }

    public void SetInFlight(int virtualPageIndex, bool inFlight)
    {
        ValidateVirtualPage(virtualPageIndex);
        _pages[virtualPageIndex].InFlight = inFlight;
    }

    public void MarkPublished(int virtualPageIndex, ulong frameSerial)
    {
        ValidateVirtualPage(virtualPageIndex);
        ref PageState page = ref _pages[virtualPageIndex];
        if (page.PhysicalPageIndex < 0 || page.MappingGeneration == 0u)
            throw new InvalidOperationException("A nonresident page cannot publish.");
        page.Lifecycle = SimpleDdgiPageLifecycle.ResidentPublished;
        page.LastPublishedFrame = frameSerial;
        page.InFlight = false;
    }

    public void ReportClassification(
        int virtualPageIndex,
        int validProbeCount,
        int inactiveProbeCount,
        uint geometryGeneration,
        ulong frameSerial,
        int requiredConfirmations = 2)
    {
        ValidateVirtualPage(virtualPageIndex);
        if (validProbeCount <= 0 || inactiveProbeCount < 0 ||
            inactiveProbeCount > validProbeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(validProbeCount));
        }
        if (geometryGeneration == 0u)
            throw new ArgumentOutOfRangeException(nameof(geometryGeneration));
        if (requiredConfirmations < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredConfirmations));

        ref PageState page = ref _pages[virtualPageIndex];
        if (page.GeometryGeneration != geometryGeneration)
        {
            page.GeometryGeneration = geometryGeneration;
            page.EmptyConfirmationCount = 0;
            if (page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty)
                page.Lifecycle = SimpleDdgiPageLifecycle.NonResident;
        }

        if (inactiveProbeCount == validProbeCount)
        {
            page.EmptyConfirmationCount = Math.Min(
                requiredConfirmations,
                page.EmptyConfirmationCount + 1);
            if (page.EmptyConfirmationCount >= requiredConfirmations &&
                !page.InFlight)
            {
                page.Lifecycle = SimpleDdgiPageLifecycle.SuppressedEmpty;
                page.LastSuppressedFrame = frameSerial;
            }
        }
        else
        {
            page.EmptyConfirmationCount = 0;
            if (page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty)
            {
                page.Lifecycle = page.PhysicalPageIndex >= 0
                    ? SimpleDdgiPageLifecycle.ResidentFresh
                    : SimpleDdgiPageLifecycle.NonResident;
            }
        }
    }

    public void InvalidateGeometry(uint geometryGeneration)
    {
        if (geometryGeneration == 0u)
            throw new ArgumentOutOfRangeException(nameof(geometryGeneration));
        if (geometryGeneration == _geometryGeneration)
            return;

        _geometryGeneration = geometryGeneration;
        for (int index = 0; index < _pages.Length; index++)
        {
            ref PageState page = ref _pages[index];
            page.GeometryGeneration = geometryGeneration;
            page.EmptyConfirmationCount = 0;
            if (page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty)
            {
                page.Lifecycle = page.PhysicalPageIndex >= 0
                    ? SimpleDdgiPageLifecycle.ResidentFresh
                    : SimpleDdgiPageLifecycle.NonResident;
            }
        }
    }

    public SimpleDdgiPageReconcileSummary Reconcile(
        ulong frameSerial,
        ReadOnlySpan<SimpleDdgiPageDemand> demands,
        in SimpleDdgiPageReferenceSettings settings,
        Span<SimpleDdgiPageTransition> transitions,
        bool cameraCut = false)
    {
        settings.Validate(PhysicalPageCapacity);
        int maximumTransitions = checked(
            settings.MaximumAdmissionsPerFrame +
            settings.MaximumEvictionsPerFrame);
        if (transitions.Length < maximumTransitions)
            throw new ArgumentException("Transition output span is too small.", nameof(transitions));
        if (RequiresResourceTransaction)
        {
            throw new InvalidOperationException(
                "The page mapping generation reached its serialized wrap threshold; create a new residency resource transaction.");
        }

        for (int index = 0; index < _pages.Length; index++)
        {
            _pages[index].DemandClass = _pages[index].Pinned
                ? SimpleDdgiPageDemandClass.DevelopmentPin
                : SimpleDdgiPageDemandClass.None;
            _pages[index].DistanceBucket = _pages[index].Pinned
                ? (byte)0
                : byte.MaxValue;
            _pages[index].VolumePriority = 0;
        }

        for (int demandIndex = 0; demandIndex < demands.Length; demandIndex++)
        {
            SimpleDdgiPageDemand demand = demands[demandIndex];
            ValidateVirtualPage(demand.VirtualPageIndex);
            if (demand.DemandClass == SimpleDdgiPageDemandClass.None)
                continue;

            ref PageState page = ref _pages[demand.VirtualPageIndex];
            bool stronger = demand.DemandClass > page.DemandClass;
            bool nearer = demand.DemandClass == page.DemandClass &&
                demand.DistanceBucket < page.DistanceBucket;
            if (stronger || nearer)
            {
                page.DemandClass = demand.DemandClass;
                page.DistanceBucket = demand.DistanceBucket;
                page.VolumePriority = demand.VolumePriority;
            }
            page.Pinned |= demand.Pin;
            if (demand.Pin)
            {
                page.DemandClass = SimpleDdgiPageDemandClass.DevelopmentPin;
                page.DistanceBucket = 0;
            }
            if (page.DemandFirstFrame == 0UL)
                page.DemandFirstFrame = frameSerial == 0UL ? 1UL : frameSerial;
        }

        int demandedCount = 0;
        int admissionCandidateCount = 0;
        int victimCandidateCount = 0;
        for (int virtualPage = 0; virtualPage < _pages.Length; virtualPage++)
        {
            ref PageState page = ref _pages[virtualPage];
            bool demanded = page.DemandClass != SimpleDdgiPageDemandClass.None;
            if (demanded)
            {
                demandedCount++;
                page.LastRelevantFrame = frameSerial;
            }
            else if (page.PhysicalPageIndex < 0 &&
                     page.Lifecycle !=
                         SimpleDdgiPageLifecycle.SuppressedEmpty)
            {
                // A gap ends the outstanding request. A future request must
                // not inherit an old first-demand tie breaker.
                page.DemandFirstFrame = 0UL;
            }

            bool suppressed = page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty;

            if (page.PhysicalPageIndex < 0)
            {
                if (demanded && !suppressed)
                {
                    _admissionCandidates[admissionCandidateCount++] =
                        new AdmissionCandidate(
                            virtualPage,
                            page.DemandClass,
                            page.DistanceBucket,
                            page.DemandFirstFrame,
                            page.VolumePriority);
                }
                continue;
            }

            bool retentionExpired =
                frameSerial >= page.LastRelevantFrame &&
                frameSerial - page.LastRelevantFrame >= settings.RetentionFrames;
            bool pressureVictim = cameraCut || retentionExpired;
            bool confirmedEmpty =
                page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty;
            bool protectedPage = (demanded && !confirmedEmpty) ||
                page.Pinned || page.InFlight ||
                page.Lifecycle == SimpleDdgiPageLifecycle.Initializing ||
                (page.Lifecycle == SimpleDdgiPageLifecycle.ResidentFresh &&
                    !retentionExpired && !cameraCut);
            if (!protectedPage && (confirmedEmpty || pressureVictim))
            {
                page.Lifecycle = confirmedEmpty
                    ? SimpleDdgiPageLifecycle.SuppressedEmpty
                    : retentionExpired
                        ? SimpleDdgiPageLifecycle.ExpiredCandidate
                        : page.Lifecycle;
                _victimCandidates[victimCandidateCount++] = new VictimCandidate(
                    virtualPage,
                    page.PhysicalPageIndex,
                    confirmedEmpty,
                    retentionExpired,
                    page.LastRelevantFrame,
                    page.DemandClass,
                    page.DistanceBucket);
            }
        }

        Array.Sort(
            _admissionCandidates,
            0,
            admissionCandidateCount,
            AdmissionComparer.Instance);
        Array.Sort(
            _victimCandidates,
            0,
            victimCandidateCount,
            VictimComparer.Instance);

        int transitionCount = 0;
        int admissionCount = 0;
        int evictionCount = 0;
        int failedAdmissions = 0;
        int victimCursor = 0;
        int admissionLimit = Math.Min(
            settings.MaximumAdmissionsPerFrame,
            admissionCandidateCount);
        for (int candidateIndex = 0;
             candidateIndex < admissionCandidateCount;
             candidateIndex++)
        {
            if (admissionCount >= admissionLimit)
            {
                failedAdmissions++;
                continue;
            }

            int physicalPage = FindFirstFreePhysicalPage();
            int previousOwner = -1;
            if (physicalPage < 0)
            {
                if (evictionCount >= settings.MaximumEvictionsPerFrame ||
                    victimCursor >= victimCandidateCount)
                {
                    failedAdmissions++;
                    continue;
                }

                VictimCandidate victim = _victimCandidates[victimCursor++];
                previousOwner = victim.VirtualPageIndex;
                physicalPage = victim.PhysicalPageIndex;
                Unmap(previousOwner, physicalPage);
                transitions[transitionCount++] = new SimpleDdgiPageTransition(
                    SimpleDdgiPageTransitionKind.Evict,
                    previousOwner,
                    physicalPage,
                    0u);
                evictionCount++;
            }

            AdmissionCandidate candidate = _admissionCandidates[candidateIndex];
            uint mappingGeneration = NextMappingGeneration();
            ref PageState page = ref _pages[candidate.VirtualPageIndex];
            page.PhysicalPageIndex = physicalPage;
            page.MappingGeneration = mappingGeneration;
            page.Lifecycle = SimpleDdgiPageLifecycle.ResidentFresh;
            page.LastRelevantFrame = frameSerial;
            page.LastPublishedFrame = 0UL;
            page.InFlight = false;
            page.EmptyConfirmationCount = 0;
            page.DemandFirstFrame = 0UL;
            _physicalOwners[physicalPage] = candidate.VirtualPageIndex;
            transitions[transitionCount++] = new SimpleDdgiPageTransition(
                SimpleDdgiPageTransitionKind.Admit,
                candidate.VirtualPageIndex,
                physicalPage,
                mappingGeneration,
                previousOwner);
            admissionCount++;
        }

        // A confirmed-empty page retires immediately. An ordinary expired
        // page remains as a cache entry while a free slot exists and is still
        // present in the ordered victim list when admission pressure arrives.
        while (evictionCount < settings.MaximumEvictionsPerFrame &&
               victimCursor < victimCandidateCount)
        {
            VictimCandidate victim = _victimCandidates[victimCursor++];
            if (!victim.ConfirmedEmpty)
                continue;
            if (_pages[victim.VirtualPageIndex].PhysicalPageIndex !=
                victim.PhysicalPageIndex)
            {
                continue;
            }
            Unmap(victim.VirtualPageIndex, victim.PhysicalPageIndex);
            transitions[transitionCount++] = new SimpleDdgiPageTransition(
                SimpleDdgiPageTransitionKind.Evict,
                victim.VirtualPageIndex,
                victim.PhysicalPageIndex,
                0u);
            evictionCount++;
        }

        int resident = 0;
        int published = 0;
        int suppressedCount = 0;
        for (int index = 0; index < _pages.Length; index++)
        {
            PageState page = _pages[index];
            if (page.PhysicalPageIndex >= 0)
                resident++;
            if (page.Lifecycle == SimpleDdgiPageLifecycle.ResidentPublished)
                published++;
            if (page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty)
                suppressedCount++;
        }

        bool pressure = failedAdmissions > 0;
        _consecutivePressureFrames = pressure
            ? checked(_consecutivePressureFrames + 1)
            : 0;
        return new SimpleDdgiPageReconcileSummary(
            frameSerial,
            demandedCount,
            resident,
            PhysicalPageCapacity - resident,
            published,
            suppressedCount,
            admissionCount,
            evictionCount,
            failedAdmissions,
            pressure,
            _consecutivePressureFrames,
            _residencyResourceGeneration,
            _mappingGeneration);
    }

    public void BeginNewResourceTransaction()
    {
        _residencyResourceGeneration =
            SimpleDdgiProbePageLayout.AdvanceNonZeroGeneration(
                _residencyResourceGeneration);
        _mappingGeneration = 0u;
        _consecutivePressureFrames = 0;
        Array.Fill(_physicalOwners, -1);
        for (int index = 0; index < _pages.Length; index++)
        {
            ref PageState page = ref _pages[index];
            page.PhysicalPageIndex = -1;
            page.MappingGeneration = 0u;
            page.Lifecycle = SimpleDdgiPageLifecycle.NonResident;
            page.DemandClass = SimpleDdgiPageDemandClass.None;
            page.DistanceBucket = byte.MaxValue;
            page.VolumePriority = 0;
            page.DemandFirstFrame = 0UL;
            page.LastRelevantFrame = 0UL;
            page.InFlight = false;
            page.LastPublishedFrame = 0UL;
            page.LastSuppressedFrame = 0UL;
            page.Pinned = false;
            page.EmptyConfirmationCount = 0;
            page.GeometryGeneration = 0u;
        }
    }

    public bool ValidateBijection(out string reason)
    {
        for (int virtualPage = 0; virtualPage < _pages.Length; virtualPage++)
        {
            PageState page = _pages[virtualPage];
            if (page.PhysicalPageIndex < 0)
                continue;
            if ((uint)page.PhysicalPageIndex >= (uint)_physicalOwners.Length)
            {
                reason = $"virtual page {virtualPage} owns out-of-range physical page {page.PhysicalPageIndex}";
                return false;
            }
            if (_physicalOwners[page.PhysicalPageIndex] != virtualPage)
            {
                reason = $"virtual/reverse owner disagreement at virtual page {virtualPage}";
                return false;
            }
            if (page.MappingGeneration == 0u)
            {
                reason = $"resident virtual page {virtualPage} has generation zero";
                return false;
            }
        }

        for (int physicalPage = 0; physicalPage < _physicalOwners.Length; physicalPage++)
        {
            int owner = _physicalOwners[physicalPage];
            if (owner < 0)
                continue;
            if ((uint)owner >= (uint)_pages.Length ||
                _pages[owner].PhysicalPageIndex != physicalPage)
            {
                reason = $"physical page {physicalPage} has invalid reverse owner {owner}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private PageState GetPage(int virtualPageIndex)
    {
        ValidateVirtualPage(virtualPageIndex);
        return _pages[virtualPageIndex];
    }

    private void ValidateVirtualPage(int virtualPageIndex)
    {
        if ((uint)virtualPageIndex >= (uint)_pages.Length)
            throw new ArgumentOutOfRangeException(nameof(virtualPageIndex));
    }

    private int FindFirstFreePhysicalPage()
    {
        for (int index = 0; index < _physicalOwners.Length; index++)
        {
            if (_physicalOwners[index] < 0)
                return index;
        }
        return -1;
    }

    private void Unmap(int virtualPageIndex, int physicalPageIndex)
    {
        ref PageState page = ref _pages[virtualPageIndex];
        bool keepSuppressed =
            page.Lifecycle == SimpleDdgiPageLifecycle.SuppressedEmpty;
        page.PhysicalPageIndex = -1;
        page.MappingGeneration = 0u;
        page.Lifecycle = keepSuppressed
            ? SimpleDdgiPageLifecycle.SuppressedEmpty
            : SimpleDdgiPageLifecycle.NonResident;
        page.InFlight = false;
        page.LastPublishedFrame = 0UL;
        _physicalOwners[physicalPageIndex] = -1;
    }

    private uint NextMappingGeneration()
    {
        if (RequiresResourceTransaction)
            throw new InvalidOperationException("Mapping generation wrap requires a new resource transaction.");
        _mappingGeneration = SimpleDdgiProbePageLayout.AdvanceNonZeroGeneration(
            _mappingGeneration);
        return _mappingGeneration;
    }
}

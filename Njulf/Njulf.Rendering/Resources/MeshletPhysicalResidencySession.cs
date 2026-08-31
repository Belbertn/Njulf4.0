using Njulf.Assets.Cooked;

namespace Njulf.Rendering.Resources;

public sealed record MeshletPhysicalResidencySessionOpenResult(
    MeshletPhysicalResidencySession? Session,
    MeshletStreamingActivationPlan ActivationPlan,
    bool Active,
    string FallbackReason);

/// <summary>
/// Owns one reference to a renderer-global package registration. Pinned pages
/// are authenticated, decoded, and proven packable before the registration is
/// visible to model publication.
/// </summary>
public sealed class MeshletPhysicalResidencySession : IDisposable
{
    private MeshletStreamingPackageHandle? _handle;

    private MeshletPhysicalResidencySession(
        MeshletStreamingPackageHandle handle,
        MeshletStreamingActivationPlan activationPlan)
    {
        _handle = handle;
        ActivationPlan = activationPlan;
    }

    public MeshletStreamingActivationPlan ActivationPlan { get; }

    public MeshletStreamingPackageHandle Package =>
        Volatile.Read(ref _handle) ??
        throw new ObjectDisposedException(
            nameof(MeshletPhysicalResidencySession));

    internal bool IsReadyForPublication =>
        Package.IsPinnedBootstrapComplete;

    internal void FinalizeSubMeshVertexOffset(
        int subMeshIndex,
        uint vertexOffset) =>
        Package.FinalizeSubMeshVertexOffset(
            subMeshIndex,
            vertexOffset);

    public static async ValueTask<
        MeshletPhysicalResidencySessionOpenResult> TryOpenAsync(
        CookedModelAsset model,
        MeshletStreamingResidencyCoordinator coordinator,
        bool streamingEnabled,
        bool completeWorkingSetAdmissionEnabled = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(coordinator);
        cancellationToken.ThrowIfCancellationRequested();

        MeshletStreamingCoordinatorSnapshot snapshot =
            coordinator.CreateSnapshot();
        MeshletStreamingActivationPlan plan =
            MeshletStreamingActivationPlanner.Evaluate(
                model.Mesh,
                streamingEnabled,
                coordinator.Options.PhysicalPageCapacity,
                completeWorkingSetAdmissionEnabled
                    ? snapshot.PageCount
                    : snapshot.PinnedPageCount,
                snapshot.Banks.CommittedBankCount,
                completeWorkingSetAdmissionEnabled);
        if (!plan.Active)
        {
            return new MeshletPhysicalResidencySessionOpenResult(
                null,
                plan,
                false,
                plan.FallbackReason);
        }
        if (string.IsNullOrWhiteSpace(model.MeshPackagePath))
        {
            const string reason =
                "meshlet-streaming-package-path-missing";
            return new MeshletPhysicalResidencySessionOpenResult(
                null,
                plan,
                false,
                reason);
        }

        string packageKey = BuildPackageKey(model);
        if (coordinator.TryAcquirePackage(
                packageKey,
                out MeshletStreamingPackageHandle? existing))
        {
            return new MeshletPhysicalResidencySessionOpenResult(
                new MeshletPhysicalResidencySession(existing!, plan),
                plan,
                true,
                string.Empty);
        }

        MeshletStreamingManifest manifest =
            model.Mesh.StreamingManifest!;
        IMeshletStreamingPageSource? source = null;
        try
        {
            var fileSource = new MeshletStreamingPageFileSource(
                model.MeshPackagePath,
                manifest);
            HashSet<int> activeSubMeshes = plan.SubMeshes
                .Where(static subMesh => subMesh.Active)
                .Select(static subMesh => subMesh.SubMeshIndex)
                .ToHashSet();
            source = activeSubMeshes.Count == model.Mesh.SubMeshes.Count
                ? fileSource
                : new FilteredMeshletStreamingPageSource(
                    fileSource,
                    activeSubMeshes);
            MeshletStreamingPageRecord[] pinned = source.Manifest.Pages
                .Where(page =>
                    (page.Flags & MeshletStreamingPageFlags.Pinned) != 0)
                .ToArray();
            await AuthenticatePinnedPagesAsync(
                    source,
                    model.Mesh,
                    pinned,
                    coordinator.Options.MaximumConcurrentReads,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!coordinator.TryRegisterPackage(
                    packageKey,
                    source,
                    out MeshletStreamingPackageHandle? handle,
                    out string fallbackReason,
                    requireCompleteWorkingSet:
                        completeWorkingSetAdmissionEnabled))
            {
                (source as IDisposable)?.Dispose();
                source = null;
                return new MeshletPhysicalResidencySessionOpenResult(
                    null,
                    plan,
                    false,
                    fallbackReason);
            }

            // Ownership of the page source transfers to the coordinator.
            source = null;
            return new MeshletPhysicalResidencySessionOpenResult(
                new MeshletPhysicalResidencySession(handle!, plan),
                plan,
                true,
                string.Empty);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
                CookedAssetFormatException or InvalidDataException or
                InvalidOperationException or ArgumentException)
        {
            (source as IDisposable)?.Dispose();
            return new MeshletPhysicalResidencySessionOpenResult(
                null,
                plan,
                false,
                $"meshlet-streaming-full-resident-fallback:{ex.GetType().Name}:{ex.Message}");
        }
    }

    private static async Task AuthenticatePinnedPagesAsync(
        IMeshletStreamingPageSource source,
        CookedMeshPayload mesh,
        IReadOnlyList<MeshletStreamingPageRecord> pinnedPages,
        int maximumConcurrentReads,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(maximumConcurrentReads);
        Task[] tasks = pinnedPages.Select(async page =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] decoded = await source.ReadPageAsync(
                        page.PageId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (decoded.Length != page.UncompressedBytes)
                {
                    throw new InvalidDataException(
                        $"Pinned page {page.PageId} does not match its authenticated byte count.");
                }
                uint vertexOffset = checked((uint)mesh.SubMeshes[
                    page.SubMeshIndex].VertexOffset);
                MeshletGpuPagePackResult packed =
                    MeshletGpuPagePacker.Pack(decoded, vertexOffset);
                if (packed.PageBytes.Length !=
                    MeshletStreamingManifest.ProductionPageSizeBytes)
                {
                    throw new InvalidDataException(
                        $"Pinned page {page.PageId} did not produce one exact physical page.");
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static string BuildPackageKey(CookedModelAsset model)
    {
        MeshletStreamingManifest manifest =
            model.Mesh.StreamingManifest!;
        return $"{Path.GetFullPath(model.MeshPackagePath)}|" +
            $"{manifest.SidecarFileName}|" +
            $"{model.Manifest.Mesh.ContentHash:x16}";
    }

    private sealed class FilteredMeshletStreamingPageSource :
        IMeshletStreamingPageSource,
        IDisposable
    {
        private readonly MeshletStreamingPageFileSource _source;
        private readonly int[] _originalPageIds;

        public FilteredMeshletStreamingPageSource(
            MeshletStreamingPageFileSource source,
            IReadOnlySet<int> activeSubMeshes)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            ArgumentNullException.ThrowIfNull(activeSubMeshes);
            MeshletStreamingPageRecord[] selected = source.Manifest.Pages
                .Where(page => activeSubMeshes.Contains(page.SubMeshIndex))
                .OrderBy(static page => page.PageId)
                .ToArray();
            if (selected.Length == 0)
                throw new InvalidDataException(
                    "The adaptive meshlet cohort contains no pages.");
            var remap = selected
                .Select((page, index) => (page.PageId, index))
                .ToDictionary(static pair => pair.PageId,
                    static pair => pair.index);
            _originalPageIds = selected
                .Select(static page => page.PageId)
                .ToArray();
            MeshletStreamingPageRecord[] pages = new
                MeshletStreamingPageRecord[selected.Length];
            for (int index = 0; index < selected.Length; index++)
            {
                MeshletStreamingPageRecord page = selected[index];
                if (!remap.TryGetValue(
                        page.FallbackPageId,
                        out int fallbackPage))
                {
                    throw new InvalidDataException(
                        "An active meshlet page references a fallback outside its activation cohort.");
                }
                pages[index] = page with
                {
                    PageId = index,
                    FallbackPageId = fallbackPage
                };
            }
            Manifest = new MeshletStreamingManifest(
                source.Manifest.SchemaVersion,
                source.Manifest.PageSizeBytes,
                source.Manifest.SidecarFileName,
                pages,
                pages.Sum(static page => (long)page.StoredBytes),
                pages.Sum(static page => (long)page.UncompressedBytes),
                pages.Count(static page =>
                    (page.Flags & MeshletStreamingPageFlags.Pinned) != 0));
            Manifest.Validate("adaptive-meshlet-residency-cohort");
        }

        public MeshletStreamingManifest Manifest { get; }

        public ValueTask<byte[]> ReadPageAsync(
            int pageId,
            CancellationToken cancellationToken = default)
        {
            if ((uint)pageId >= (uint)_originalPageIds.Length)
                throw new ArgumentOutOfRangeException(nameof(pageId));
            return _source.ReadPageAsync(
                _originalPageIds[pageId],
                cancellationToken);
        }

        public void Dispose() => _source.Dispose();
    }

    public void Dispose()
    {
        MeshletStreamingPackageHandle? handle =
            Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }
}

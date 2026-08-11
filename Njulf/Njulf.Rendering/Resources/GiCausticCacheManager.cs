using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

/// <summary>Revisions that own the placement and energy of a caustic cache.</summary>
public readonly record struct GiCausticCacheRevision(
    uint TransportAbi,
    ulong HeroMaterialRevision,
    ulong LightDistributionRevision,
    ulong CasterGeometryRevision,
    ulong CasterTransformRevision,
    ulong ReceiverGeometryRevision,
    ulong StableIdentityRevision)
{
    public bool IsValid => TransportAbi != 0 && HeroMaterialRevision != 0 &&
        LightDistributionRevision != 0 && CasterGeometryRevision != 0 &&
        CasterTransformRevision != 0 && ReceiverGeometryRevision != 0 &&
        StableIdentityRevision != 0;
}

public enum GiCausticCachePublicationState : byte
{
    Empty,
    Building,
    Published,
    Invalidated,
    Failed
}

public readonly record struct GiCausticCachePublication(
    uint Generation,
    GiCausticCacheRevision Revision,
    GiCausticCachePublicationState State,
    int RepresentedTaskCount,
    GiCausticCacheBuildResult? Build,
    string Status)
{
    public bool IsReadable => State == GiCausticCachePublicationState.Published &&
        Build is { IsValid: true } && RepresentedTaskCount > 0;

    public static GiCausticCachePublication Empty { get; } = new(
        0, default, GiCausticCachePublicationState.Empty, 0, null, "empty");
}

/// <summary>
/// Transactional CPU authority for C4 publication. The GPU implementation
/// mirrors this generation state in a small header: no incomplete/overflowed
/// cache can become readable, and a revision mismatch never returns stale
/// photon placement.
/// </summary>
public sealed class GiCausticCacheManager
{
    private GiCausticCachePublication _published = GiCausticCachePublication.Empty;
    private GiCausticCachePublication _building = GiCausticCachePublication.Empty;
    private uint _nextGeneration = 1;

    public GiCausticCachePublication Published => _published;
    public GiCausticCachePublication Building => _building;
    public uint PublicationFailureCount { get; private set; }
    public uint RevisionMismatchCount { get; private set; }

    public bool BeginBuild(
        in GiCausticCacheRevision revision,
        int representedTaskCount)
    {
        if (!revision.IsValid || representedTaskCount <= 0 ||
            _building.State == GiCausticCachePublicationState.Building)
        {
            return false;
        }

        uint generation = _nextGeneration++;
        if (generation == 0)
            generation = _nextGeneration++;
        _building = new GiCausticCachePublication(
            generation,
            revision,
            GiCausticCachePublicationState.Building,
            representedTaskCount,
            null,
            "building");
        return true;
    }

    public bool CompleteBuild(
        ReadOnlySpan<GiCausticPhotonCandidate> photons,
        in GiCausticCacheBuildConfiguration configuration)
    {
        if (_building.State != GiCausticCachePublicationState.Building)
            return false;
        if (configuration.CacheGeneration != _building.Generation)
        {
            FailBuild("generation-mismatch");
            return false;
        }

        GiCausticCacheBuildResult result = GiCausticPhotonCacheReference.Build(
            photons, configuration);
        if (!result.IsValid)
        {
            FailBuild(result.Diagnostics.FailureReason);
            return false;
        }

        _building = _building with
        {
            State = GiCausticCachePublicationState.Published,
            Build = result,
            Status = "ready-to-publish"
        };
        return true;
    }

    /// <summary>
    /// Atomically flips a fully validated write generation. The expected
    /// revision check models the resolve-side header validation.
    /// </summary>
    public bool Publish(in GiCausticCacheRevision expectedRevision)
    {
        if (_building.State != GiCausticCachePublicationState.Published ||
            !_building.Revision.Equals(expectedRevision) || !_building.IsReadable)
        {
            if (!_building.Revision.Equals(expectedRevision))
                RevisionMismatchCount++;
            FailBuild("publication-revision-mismatch-or-invalid-cache");
            return false;
        }

        _published = _building with { Status = "published" };
        _building = GiCausticCachePublication.Empty;
        return true;
    }

    /// <summary>
    /// Relevant scene changes suppress stale placement immediately. No camera
    /// change is modeled here because world-space cache content is view
    /// independent.
    /// </summary>
    public void Invalidate(in GiCausticCacheRevision revision, string reason)
    {
        if (_published.State != GiCausticCachePublicationState.Empty &&
            !_published.Revision.Equals(revision))
        {
            _published = new GiCausticCachePublication(
                _published.Generation,
                _published.Revision,
                GiCausticCachePublicationState.Invalidated,
                _published.RepresentedTaskCount,
                null,
                string.IsNullOrWhiteSpace(reason) ? "invalidated" : reason);
        }

        if (_building.State == GiCausticCachePublicationState.Building &&
            !_building.Revision.Equals(revision))
        {
            FailBuild(string.IsNullOrWhiteSpace(reason) ? "build-invalidated" : reason);
        }
    }

    public bool TryGetReadable(
        in GiCausticCacheRevision revision,
        out GiCausticCachePublication publication)
    {
        if (_published.IsReadable && _published.Revision.Equals(revision))
        {
            publication = _published;
            return true;
        }

        if (_published.State != GiCausticCachePublicationState.Empty)
            RevisionMismatchCount++;
        publication = GiCausticCachePublication.Empty;
        return false;
    }

    public void Reset()
    {
        _published = GiCausticCachePublication.Empty;
        _building = GiCausticCachePublication.Empty;
        _nextGeneration = 1;
        PublicationFailureCount = 0;
        RevisionMismatchCount = 0;
    }

    private void FailBuild(string reason)
    {
        if (_building.State != GiCausticCachePublicationState.Empty)
        {
            _building = _building with
            {
                State = GiCausticCachePublicationState.Failed,
                Build = null,
                Status = reason
            };
        }
        PublicationFailureCount++;
    }
}

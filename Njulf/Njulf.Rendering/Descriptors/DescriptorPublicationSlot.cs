using System;

namespace Njulf.Rendering.Descriptors;

/// <summary>
/// Allocation-free publication state for one descriptor slot. The desired
/// identity is committed only after the native descriptor write completes.
/// </summary>
internal struct DescriptorPublicationSlot<TIdentity>
    where TIdentity : struct, IEquatable<TIdentity>
{
    private TIdentity _published;
    private bool _hasPublished;

    public readonly bool RequiresPublication(TIdentity desired) =>
        !_hasPublished || !_published.Equals(desired);

    public void Commit(TIdentity published)
    {
        _published = published;
        _hasPublished = true;
    }

    public void Invalidate()
    {
        _published = default;
        _hasPublished = false;
    }
}

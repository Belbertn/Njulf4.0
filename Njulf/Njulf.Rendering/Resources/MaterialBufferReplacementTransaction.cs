using System.Runtime.ExceptionServices;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Publishes a replacement material buffer without invalidating descriptors
/// that still reference the previous buffer. Candidate publication precedes
/// authoritative state commit; rollback restores the old binding before the
/// candidate can be destroyed.
/// </summary>
internal static class MaterialBufferReplacementTransaction
{
    public static void Execute(
        Action publishCandidateBinding,
        Action commitAuthoritativeState,
        Action restoreAuthoritativeBinding,
        Action destroyCandidate,
        Action retireCandidate,
        Action quarantineCandidate,
        Action<Exception> reportDeferredCandidateCleanup)
    {
        ArgumentNullException.ThrowIfNull(
            publishCandidateBinding);
        ArgumentNullException.ThrowIfNull(
            commitAuthoritativeState);
        ArgumentNullException.ThrowIfNull(
            restoreAuthoritativeBinding);
        ArgumentNullException.ThrowIfNull(
            destroyCandidate);
        ArgumentNullException.ThrowIfNull(
            retireCandidate);
        ArgumentNullException.ThrowIfNull(
            quarantineCandidate);
        ArgumentNullException.ThrowIfNull(
            reportDeferredCandidateCleanup);

        try
        {
            publishCandidateBinding();
            commitAuthoritativeState();
            return;
        }
        catch (Exception publicationFailure)
        {
            try
            {
                restoreAuthoritativeBinding();
            }
            catch (Exception restorationFailure)
            {
                quarantineCandidate();
                throw new AggregateException(
                    "Material buffer publication failed and the previous descriptor binding could not be restored. " +
                    "The candidate buffer was quarantined.",
                    publicationFailure,
                    restorationFailure);
            }

            try
            {
                destroyCandidate();
            }
            catch (Exception cleanupFailure)
            {
                // Descriptor restoration makes deferred destruction safe.
                // Capacity for this durable retirement is reserved before
                // publication, so this ownership transfer cannot allocate.
                retireCandidate();
                reportDeferredCandidateCleanup(
                    cleanupFailure);
            }

            ExceptionDispatchInfo
                .Capture(publicationFailure)
                .Throw();
            throw new InvalidOperationException(
                "Unreachable material buffer rollback path.");
        }
    }
}

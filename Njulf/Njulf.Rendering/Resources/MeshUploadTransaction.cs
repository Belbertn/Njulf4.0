using System.Runtime.ExceptionServices;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Orders mesh-upload publication and centralizes fail-closed rollback.
/// Candidate GPU resources are not destroyed unless command cleanup, CPU-state
/// restoration, and descriptor restoration all succeed; otherwise they are
/// quarantined for safe destruction during renderer shutdown.
/// </summary>
internal static class MeshUploadTransaction
{
    public static void Execute(
        Action completeGpuUpload,
        Action publishCandidateBindings,
        Action commitAuthoritativeState,
        Action cleanupGpuUpload,
        Action restoreAuthoritativeState,
        Action restoreAuthoritativeBindings,
        Action destroyCandidateResources,
        Action quarantineCandidateResources,
        Action restoreReservations)
    {
        ArgumentNullException.ThrowIfNull(completeGpuUpload);
        ArgumentNullException.ThrowIfNull(publishCandidateBindings);
        ArgumentNullException.ThrowIfNull(commitAuthoritativeState);
        ArgumentNullException.ThrowIfNull(cleanupGpuUpload);
        ArgumentNullException.ThrowIfNull(restoreAuthoritativeState);
        ArgumentNullException.ThrowIfNull(restoreAuthoritativeBindings);
        ArgumentNullException.ThrowIfNull(destroyCandidateResources);
        ArgumentNullException.ThrowIfNull(quarantineCandidateResources);
        ArgumentNullException.ThrowIfNull(restoreReservations);

        bool bindingPublicationAttempted = false;
        bool statePublicationAttempted = false;
        try
        {
            completeGpuUpload();

            bindingPublicationAttempted = true;
            publishCandidateBindings();

            statePublicationAttempted = true;
            commitAuthoritativeState();
            return;
        }
        catch (Exception uploadFailure)
        {
            var rollbackFailures = new List<Exception>();
            bool candidatesCanBeDestroyed = TryRollbackStep(
                cleanupGpuUpload,
                rollbackFailures);

            if (statePublicationAttempted)
            {
                candidatesCanBeDestroyed &=
                    TryRollbackStep(
                        restoreAuthoritativeState,
                        rollbackFailures);
            }

            if (bindingPublicationAttempted)
            {
                candidatesCanBeDestroyed &=
                    TryRollbackStep(
                        restoreAuthoritativeBindings,
                        rollbackFailures);
            }

            if (candidatesCanBeDestroyed)
            {
                candidatesCanBeDestroyed =
                    TryRollbackStep(
                        destroyCandidateResources,
                        rollbackFailures);
            }

            if (!candidatesCanBeDestroyed)
            {
                TryRollbackStep(
                    quarantineCandidateResources,
                    rollbackFailures);
            }

            TryRollbackStep(restoreReservations, rollbackFailures);

            if (rollbackFailures.Count == 0)
                ExceptionDispatchInfo.Capture(uploadFailure).Throw();

            throw new AggregateException(
                "Mesh upload failed and transactional rollback was incomplete.",
                new[] { uploadFailure }.Concat(rollbackFailures));
        }
    }

    private static bool TryRollbackStep(
        Action rollback,
        ICollection<Exception> failures)
    {
        try
        {
            rollback();
            return true;
        }
        catch (Exception rollbackFailure)
        {
            failures.Add(rollbackFailure);
            return false;
        }
    }
}

using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Pure receiver-authority gate for a refinement brick. No fine probe becomes
/// visible before the global tail certificate proves the current topology and
/// source cohort coherent. Once certified, the complete fine field may be
/// blended against the complete base field without exposing partial probe data.
/// </summary>
public static class SimpleDdgiRefinementPublication
{
    public static bool CanPublishReceiverAuthority(
        bool transactionHasInvalidation,
        bool topologyChangedThisFrame,
        bool tailCertificationEnabled,
        bool currentTailCertificate) =>
        !transactionHasInvalidation &&
        !topologyChangedThisFrame &&
        tailCertificationEnabled &&
        currentTailCertificate;

    public static bool RequiresPublicationRevocation(
        SimpleDdgiTransportRecoveryAction recoveryAction,
        SimpleDdgiTransportPhase phase) =>
        recoveryAction is not
            (SimpleDdgiTransportRecoveryAction.None or
             SimpleDdgiTransportRecoveryAction.AdvanceSolveEpoch) ||
        phase is
            SimpleDdgiTransportPhase.ParticipantReconciliation or
            SimpleDdgiTransportPhase.FailClosedRecovery or
            SimpleDdgiTransportPhase.UnsupportedTolerance;
}

/// <summary>
/// Smooths the receiver handoff from the already-authoritative base field to a
/// newly certified refinement field. Certification remains all-or-nothing;
/// this state changes only the composition weight after the complete fine field
/// is safe to sample.
/// </summary>
public sealed class SimpleDdgiRefinementPublicationBlendState
{
    // Just over one second at the capture contract's 60 Hz presentation rate.
    // This is deliberately shorter than the 256-frame publication-liveness
    // proof and is reset immediately by any authority revocation.
    public const int TransitionFrameCount = 64;

    public float Weight { get; private set; }
    public bool IsComplete => Weight >= 1.0f;

    public float Update(bool coherentPublication, int admittedBrickCount)
    {
        if (!coherentPublication || admittedBrickCount <= 0)
        {
            Weight = 0.0f;
            return Weight;
        }

        Weight = MathF.Min(
            1.0f,
            Weight + 1.0f / TransitionFrameCount);
        return Weight;
    }

    public void Reset() => Weight = 0.0f;
}

/// <summary>
/// The resource identities that make an already-certified refinement field
/// safe to retain while a routine source resample is being re-certified.
/// Source epochs, canonical generations, and audit epochs intentionally do not
/// participate: those are the bounded in-place maintenance transaction. A
/// lighting, topology, ownership, or operator change remains fail-closed.
/// </summary>
public readonly record struct SimpleDdgiRefinementPublicationIdentity(
    uint VolumeTable,
    uint PhysicalOwnership,
    uint TransportOperator,
    ulong LightingSignature,
    ulong SourceCalibrationSignature)
{
    public bool IsInitialized =>
        VolumeTable != 0u &&
        PhysicalOwnership != 0u &&
        TransportOperator != 0u;

    public static SimpleDdgiRefinementPublicationIdentity From(
        SimpleDdgiTransportGenerations generations,
        ulong lightingSignature,
        ulong sourceCalibrationSignature) =>
        new(
            generations.VolumeTable,
            generations.PhysicalOwnership,
            generations.TransportOperator,
            lightingSignature,
            sourceCalibrationSignature);
}

public enum SimpleDdgiRefinementPublicationDecision : byte
{
    None = 0,
    CurrentCertificate = 1,
    RetainedCertifiedContinuity = 2,
    NoCertifiedAuthority = 3,
    TransactionInvalidation = 4,
    TopologyChanged = 5,
    TailCertificationDisabled = 6,
    RecoveryActive = 7,
    IdentityUninitialized = 8,
    VolumeTableChanged = 9,
    PhysicalOwnershipChanged = 10,
    TransportOperatorChanged = 11,
    LightingSignatureChanged = 12,
    SourceCalibrationChanged = 13
}

/// <summary>
/// Retains the last certified all-or-nothing refinement publication across a
/// routine, generation-stable source resample. This avoids switching an
/// unchanged receiver between fine and coarse lattices for the duration of a
/// periodic audit. Real invalidation, topology changes, identity changes, and
/// recovery still revoke authority until a new certificate is accepted.
/// </summary>
public sealed class SimpleDdgiRefinementPublicationState
{
    private bool _hasCertifiedAuthority;
    private SimpleDdgiRefinementPublicationIdentity _publishedIdentity;

    public bool IsRetainingCertifiedAuthority { get; private set; }
    public SimpleDdgiRefinementPublicationDecision Decision { get; private set; }
    public SimpleDdgiRefinementPublicationDecision LastRevocationDecision
    {
        get;
        private set;
    }

    public bool Resolve(
        bool transactionHasInvalidation,
        bool topologyChangedThisFrame,
        bool tailCertificationEnabled,
        bool currentTailCertificate,
        bool recoveryActive,
        SimpleDdgiRefinementPublicationIdentity currentIdentity)
    {
        IsRetainingCertifiedAuthority = false;
        bool currentAuthority =
            SimpleDdgiRefinementPublication.CanPublishReceiverAuthority(
                transactionHasInvalidation,
                topologyChangedThisFrame,
                tailCertificationEnabled,
                currentTailCertificate);

        if (currentAuthority && !recoveryActive && currentIdentity.IsInitialized)
        {
            _hasCertifiedAuthority = true;
            _publishedIdentity = currentIdentity;
            Decision = SimpleDdgiRefinementPublicationDecision.CurrentCertificate;
            LastRevocationDecision = SimpleDdgiRefinementPublicationDecision.None;
            return true;
        }

        SimpleDdgiRefinementPublicationDecision revocation =
            ResolveRevocationDecision(
                transactionHasInvalidation,
                topologyChangedThisFrame,
                tailCertificationEnabled,
                recoveryActive,
                currentIdentity);
        if (revocation != SimpleDdgiRefinementPublicationDecision.None)
        {
            Reset(revocation);
            return false;
        }

        IsRetainingCertifiedAuthority = _hasCertifiedAuthority;
        Decision = _hasCertifiedAuthority
            ? SimpleDdgiRefinementPublicationDecision.RetainedCertifiedContinuity
            : SimpleDdgiRefinementPublicationDecision.NoCertifiedAuthority;
        return _hasCertifiedAuthority;
    }

    public void Reset() =>
        Reset(SimpleDdgiRefinementPublicationDecision.None);

    private void Reset(SimpleDdgiRefinementPublicationDecision decision)
    {
        _hasCertifiedAuthority = false;
        _publishedIdentity = default;
        IsRetainingCertifiedAuthority = false;
        Decision = decision;
        if (decision != SimpleDdgiRefinementPublicationDecision.None)
            LastRevocationDecision = decision;
    }

    private SimpleDdgiRefinementPublicationDecision ResolveRevocationDecision(
        bool transactionHasInvalidation,
        bool topologyChangedThisFrame,
        bool tailCertificationEnabled,
        bool recoveryActive,
        SimpleDdgiRefinementPublicationIdentity currentIdentity)
    {
        if (transactionHasInvalidation)
            return SimpleDdgiRefinementPublicationDecision.TransactionInvalidation;
        if (topologyChangedThisFrame)
            return SimpleDdgiRefinementPublicationDecision.TopologyChanged;
        if (!tailCertificationEnabled)
            return SimpleDdgiRefinementPublicationDecision.TailCertificationDisabled;
        if (recoveryActive)
            return SimpleDdgiRefinementPublicationDecision.RecoveryActive;
        if (!currentIdentity.IsInitialized)
            return SimpleDdgiRefinementPublicationDecision.IdentityUninitialized;
        if (!_hasCertifiedAuthority)
            return SimpleDdgiRefinementPublicationDecision.None;
        if (currentIdentity.VolumeTable != _publishedIdentity.VolumeTable)
            return SimpleDdgiRefinementPublicationDecision.VolumeTableChanged;
        if (currentIdentity.PhysicalOwnership != _publishedIdentity.PhysicalOwnership)
            return SimpleDdgiRefinementPublicationDecision.PhysicalOwnershipChanged;
        if (currentIdentity.TransportOperator != _publishedIdentity.TransportOperator)
            return SimpleDdgiRefinementPublicationDecision.TransportOperatorChanged;
        if (currentIdentity.LightingSignature != _publishedIdentity.LightingSignature)
            return SimpleDdgiRefinementPublicationDecision.LightingSignatureChanged;
        if (currentIdentity.SourceCalibrationSignature !=
            _publishedIdentity.SourceCalibrationSignature)
        {
            return SimpleDdgiRefinementPublicationDecision.SourceCalibrationChanged;
        }

        return SimpleDdgiRefinementPublicationDecision.None;
    }
}

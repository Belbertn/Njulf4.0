namespace Njulf.Rendering.Resources;

/// <summary>
/// Pure receiver-authority gate for a refinement brick. The fine field is
/// all-or-nothing: no individual fine probe becomes visible before the global
/// tail certificate proves the current topology and source cohort coherent.
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
}

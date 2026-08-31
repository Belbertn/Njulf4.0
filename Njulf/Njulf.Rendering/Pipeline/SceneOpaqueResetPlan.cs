using Njulf.Rendering.Data;

namespace Njulf.Rendering.Pipeline;

internal readonly record struct SceneOpaqueResetPlan(
    bool ClearPayloads,
    int ActiveDirectionalCascadeCount,
    uint StaticShadowCascadeMask,
    bool ClearDynamicShadowPayloads)
{
    public static SceneOpaqueResetPlan Create(
        bool indirectDispatchEnabled,
        bool validationReadsPayload,
        int directionalCascadeCount,
        uint staticShadowCascadeMask,
        bool dynamicShadowCompactionActive)
    {
        int activeCascades = Math.Clamp(
            directionalCascadeCount,
            0,
            ShadowSettings.MaxDirectionalCascades);
        uint activeMask = activeCascades ==
            ShadowSettings.MaxDirectionalCascades
                ? (1u << ShadowSettings.MaxDirectionalCascades) - 1u
                : (1u << activeCascades) - 1u;
        return new SceneOpaqueResetPlan(
            ClearPayloads:
                !indirectDispatchEnabled || validationReadsPayload,
            ActiveDirectionalCascadeCount: activeCascades,
            StaticShadowCascadeMask:
                staticShadowCascadeMask & activeMask,
            ClearDynamicShadowPayloads:
                dynamicShadowCompactionActive && activeCascades != 0);
    }

    public bool ClearsStaticShadowCascade(int cascade) =>
        ClearPayloads &&
        (uint)cascade < (uint)ActiveDirectionalCascadeCount &&
        (StaticShadowCascadeMask & (1u << cascade)) != 0u;

    public bool ClearsDynamicShadowCascade(int cascade) =>
        ClearPayloads &&
        ClearDynamicShadowPayloads &&
        (uint)cascade < (uint)ActiveDirectionalCascadeCount;
}

namespace Njulf.Rendering.Resources;

public readonly record struct FoliageSceneRegistrationSnapshot(
    int PrototypeCount,
    int PatchCount,
    int VisiblePatchCount,
    uint Revision);

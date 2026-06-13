namespace Njulf.Rendering.Diagnostics;

public sealed record SceneReloadDiagnostics(
    int ReloadIndex,
    int RenderObjectCountBefore,
    int RenderObjectCountAfter,
    int MeshCountBefore,
    int MeshCountAfter,
    int MaterialCountBefore,
    int MaterialCountAfter,
    int TextureCountBefore,
    int TextureCountAfter,
    int DescriptorWritesBefore,
    int DescriptorWritesAfter,
    ulong GpuBytesBefore,
    ulong GpuBytesAfter,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    int PendingDeletionCountAfter);

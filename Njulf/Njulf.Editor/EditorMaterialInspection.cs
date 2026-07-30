using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Editor;

/// <summary>
/// Immutable editor view of an authored material and its renderer-derived transport state.
/// Derived values are intentionally exposed without setters so the inspector cannot author
/// stale GPU or GI payloads.
/// </summary>
public sealed record EditorMaterialInspection(
    MaterialHandle Handle,
    MaterialDefinition Definition,
    GiMaterialTransportProfile TransportProfile,
    MaterialAspectRevisions AspectRevisions,
    IReadOnlyList<string> CompileDiagnostics);

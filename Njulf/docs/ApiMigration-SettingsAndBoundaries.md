# Settings and API migration

- Import `Njulf.Framework` for `Game` instead of `Njulf.Core`. The framework assembly
  stays the same; no compatibility host class is added. Other Core namespaces remain valid.
- Configure renderer startup in `ConfigureRendering(RenderingOptions)`. Use
  `GraphicsDevice.Settings` for common runtime changes and receipts. Direct renderer
  settings, qualification controls and native Vulkan integration remain advanced.
- Use `ShadowQualityPreset` through `ShadowSettings.ApplyPreset` at startup or
  `GraphicsSettingsChange.ShadowPreset` at runtime. Resolved settings retain the existing
  persistence schema and defaults. See [settings](../RendererSettingsReference.md).
- `DepthStencilState` describes testing/writing only. Comparison members move to
  `DepthComparison`; their numeric values are preserved. These are independent choices,
  not flags, and do not represent stencil configuration. The engine currently has no
  consumers of the old mixed enum, so this migration adds no new pipeline configuration.
- `Game.Input` remains `IInputManager`. Native consumers explicitly cast to
  `Njulf.Input.Advanced.INativeInputIntegration`; `EditorInputBridge` accepts that interface.
  Raw events, native key polling and native cursor control retain their behavior and lifetime.
- Construct the renderer through `AddRendering` or `Game`. Implementation passes,
  internal resource managers, GPU-only layouts and diagnostic builders are not extension APIs.
  Supported editor/tooling services, diagnostic result formats, and
  `Njulf.Graphics.Vulkan` pass/device contracts remain available.

Rendering enums stay with their owning API/domain. `Njulf.Core.Enums` holds shared,
backend-neutral state choices; `Njulf.Graphics` holds common graphics choices;
`Njulf.Rendering.Data` holds advanced renderer settings and shader contracts. Files may
share namespaces across assemblies without requiring a namespace or assembly migration.
Only actual combinable masks use `[Flags]`; shader-visible enum values and layouts are unchanged.

Settings source files are grouped by their existing domains. The root settings object
continues to own serialization, migration and overall preset orchestration.

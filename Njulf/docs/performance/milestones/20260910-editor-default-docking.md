# Default editor docking — 2026-09-10

Source: `10440367`, dirty workspace with pre-existing editor, scene ownership, and renderer changes retained. Hardware: AMD Ryzen 5 5600H; Windows, .NET 10, Development configuration, Hexa.NET.ImGui 2.2.9. Verification uses native ImGui frames without a Vulkan scene at 1280×720 and 1920×1080, followed by resizing to 80%; saved-layout coverage uses 1600×900.

Previously, docking was enabled but there was no application dockspace or initial arrangement. Existing `imgui.ini` contained overlapping floating windows. The editor now creates a default layout when its dockspace is absent: Scene Management across the bottom; Hierarchy above Scene Lights on the left; Rendering Settings, Inspector, Global Illumination, Materials, and Shadows as tabs on the right. Rendering Settings is initially selected. The center stays transparent and accepts scene input. Tabs and dividers remain movable, ImGui saves customization through its existing ini mechanism, and **Layout > Reset layout** restores the defaults. Hidden/collapsed panel contents are skipped using ImGui's Begin result.

Validation: editor Development build succeeded with zero warnings/errors. Five focused tests passed, zero failures/skips: three new native ImGui cases cover placement, legacy floating settings, resize, center/panel mouse capture, custom docking across frames and context recreation, and reset; two existing overlay-host cases cover font-atlas draw data and disposal. The initial candidate failed because setting a dock node's selected tab before its windows existed did not select Rendering Settings. The retained implementation focuses that tab once after the windows exist. `git diff --check` passed. No pre-change test run or in-game screenshot was captured; the layout tests submit the shell's window names to native ImGui without rendering full scene-dependent panel content.

Decision: retain. Baseline/candidate frame timings and tail latency were not measured; this is a usability change with no performance claim. No renderer or shader changes were required.

Storage: only normal Development build outputs and two compact logs under `.tmp/editor-docking-*.log` on D: were generated. The pruning dry run found 1,926 older payload candidates totaling 7.84 GiB; 288.46 GiB remains free. Candidates belong to other investigations, including the active dirty scene-ownership work, and were retained because active-reference status was not established. No bulky captures or isolated builds were created.

Reproduce from the repository root:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~EditorDockLayoutTests|FullyQualifiedName~ImGuiEditorOverlayHostTests' --logger 'console;verbosity=normal'
```

Open the rebuilt editor with **Ctrl+Keypad1**. An existing layout can be restored to the default arrangement using **Layout > Reset layout**.

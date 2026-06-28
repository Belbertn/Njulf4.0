# DDGI Shortcuts

Source: `NjulfHelloGame/SampleInputController.cs`

All shortcuts below are physical key chords: hold either `Left Ctrl` or `Right Ctrl`, then press the listed key.

## DDGI Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+D` | Cycle DDGI-only debug view. Forces DDGI mode on, disables SSGI, enables ray-query DDGI, camera-relative DDGI, probe classification, and probe relocation. |
| `Ctrl+P` | Apply the DDGI production profile (`DdgiHigh`) and print the resulting GI settings. |
| `Ctrl+T` | Cycle `DdgiQualityTier`, then force DDGI-only mode. |
| `Ctrl+R` | Print detailed DDGI diagnostics to the console. |

## GI Controls That Affect DDGI

| Shortcut | Action |
| --- | --- |
| `Ctrl+5` | Toggle global illumination. Disabling GI also clears the GI debug view. |
| `Ctrl+Y` | Cycle GI mode: `Disabled -> Ssgi -> Ddgi -> Hybrid -> RayQueryHybrid -> Disabled`. DDGI is active in `Ddgi`, `Hybrid`, and `RayQueryHybrid`. |
| `Ctrl+6` | Cycle the full GI debug view list, including SSGI, DDGI, and ray-query views. |
| `Ctrl+G` | Cycle focused DDGI GI debug views: `FinalIndirect -> DdgiIrradiance -> DdgiCoverage -> DdgiUpdateReasons -> FinalIndirect`. Also forces DDGI-only mode. |
| `Ctrl+Backspace` | Clear the GI debug view (`GlobalIlluminationDebugView.None`). |
| `Ctrl+J` | Decrease GI max bounce distance by `0.5`. |
| `Ctrl+U` | Increase GI max bounce distance by `0.5`. |
| `Ctrl+M` | Decrease GI indirect intensity by `0.05`. |
| `Ctrl+I` | Increase GI indirect intensity by `0.05`. |

## DDGI Debug View Cycles

### `Ctrl+D` DDGI-Only Cycle

`FinalIndirect -> DdgiIrradiance -> DdgiVisibility -> DdgiProbeIndex -> DdgiProbeState -> DdgiProbeRelocation -> DdgiLeakClamp -> DdgiCoverage -> DdgiCascadeSelection -> DdgiCascadeBlendWeight -> DdgiUpdateReasons -> DdgiRayBudget -> DdgiGatherLocalVolume -> DdgiGatherClipmap -> DdgiGatherClipmapBlendWeight -> DdgiGatherFallback -> FinalIndirect`

### `Ctrl+6` Full GI Debug Cycle

`FinalIndirect -> SsgiRaw -> SsgiFiltered -> SsgiHistory -> SsgiRayHitMask -> SsgiHistoryRejection -> DdgiIrradiance -> DdgiVisibility -> DdgiProbeIndex -> DdgiProbeState -> DdgiProbeRelocation -> DdgiLeakClamp -> DdgiCoverage -> DdgiCascadeSelection -> DdgiCascadeBlendWeight -> DdgiUpdateReasons -> DdgiRayBudget -> DdgiGatherLocalVolume -> DdgiGatherClipmap -> DdgiGatherClipmapBlendWeight -> DdgiGatherFallback -> RayQueryCost -> None`

## DDGI Debug Overlays

| Shortcut | Action |
| --- | --- |
| `Ctrl+Keypad9` | Cycle renderer debug overlays. The cycle includes DDGI overlays after reflection probe volumes. |

DDGI overlay segment:

`DdgiProbeVolumes -> DdgiProbeActivity -> DdgiUpdatedProbes -> DdgiProbeRelocation -> DdgiProbeAge -> DdgiPhysicalSlots -> DdgiCascadeBounds -> DdgiNewlyExposedCells -> DdgiFrustumPriority -> DdgiSafetyRefresh -> DdgiCascadeBlend -> DdgiUpdateReasons`

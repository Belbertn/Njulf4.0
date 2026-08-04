# Simple DDGI Shortcuts

Source: `NjulfHelloGame/SampleInputController.cs`

All shortcuts are physical key chords: hold either `Left Ctrl` or `Right Ctrl`, then press the listed key.

## Simple DDGI Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+D` | Enable Simple DDGI and cycle its debug view. |
| `Ctrl+F` | Toggle the Simple DDGI diagnostics console filter. |
| `Ctrl+V` | Cycle Simple DDGI investigation views. |
| `Ctrl+P` | Restore the scene's normal render profile and clear visualization overrides. |
| `Ctrl+T` | Cycle `DdgiQualityTier` and enable Simple DDGI. |
| `Ctrl+L` | Toggle compact L1 probe metadata (`DdgiProbeL1MetadataEnabled`). |
| `Ctrl+R` | Print Simple DDGI diagnostics to the console. |

## GI Controls

| Shortcut | Action |
| --- | --- |
| `Ctrl+5` | Toggle global illumination. Disabling GI clears the debug view. |
| `Ctrl+Y` | Cycle GI mode: `Disabled -> Ddgi -> Disabled`. |
| `Ctrl+6` | Cycle the Simple DDGI debug view list. |
| `Ctrl+G` | Cycle focused Simple DDGI views, beginning with `FinalIndirect`. |
| `Ctrl+Backspace` | Clear the GI debug view. |
| `Ctrl+J` / `Ctrl+U` | Decrease/increase maximum GI bounce distance by `0.5`. |
| `Ctrl+M` / `Ctrl+I` | Decrease/increase indirect intensity by `0.05`. |

`Ctrl+D` and `Ctrl+6` use the same active Simple DDGI debug cycle. The list includes final indirect light, irradiance, source-cache radiance, sampled irradiance, final diffuse, raw diffuse, support/data/visibility diagnostics, probe state, relocation, coverage, update reasons, ray budget, far-field views, and material transport provenance; it returns to the normal view at the end. The renderer prints a legend for the selected view.

## Debug Overlays

`Ctrl+Keypad9` cycles renderer overlays. The sequence includes the active Simple DDGI probe overlays when available.

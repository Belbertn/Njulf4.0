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

`Ctrl+Keypad9` (also described as `Ctrl+Num9`) traverses the renderer-overlay
catalog forward; add `Shift` to traverse it in reverse. The stable cycle is:

```text
None -> Forward+ light tiles -> Directional shadow cascade frusta ->
Reflection probe volumes -> DDGI volume bounds -> DDGI probe spheres ->
DDGI probe activity -> DDGI updated probes -> DDGI probe relocation ->
DDGI probe age -> DDGI physical slots -> DDGI cascade bounds ->
DDGI newly exposed cells -> DDGI scheduler priority ->
DDGI update reasons -> Decal volumes -> Object bounds -> Meshlet bounds ->
Selected object -> None
```

The DDGI probe views are bounded to 768 procedural wire-sphere instances.
Their vertex shader resolves the production toroidal index, sparse physical
page/slot, canonical state, relocation, receiver publication, admitted update
record, and resource generations directly on the GPU. `DDGI probe spheres`
uses the relocated centre when publication is coherent and the logical lattice
position otherwise. `DDGI updated probes` and `DDGI update reasons` read the
admitted queue directly; they do not use the CPU `_probeQueued` mirror.

After the first rendered frame, the diagnostics reporter prints the catalog
legend and a `Rendered`, `NoData`, `Unavailable`, or `Retired` result. DDGI
marker/residency/state/reason counters arrive through the existing
fence-complete frame ring and are rejected when their volume, scheduler, or
residency generation no longer matches.

`F2` is a separate receiver-space shadow ownership view. The overlay cycle's
`Directional shadow cascade frusta` mode draws the actual world-space light
frusta.

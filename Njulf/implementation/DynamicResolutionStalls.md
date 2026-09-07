# Dynamic resolution stalls

Dynamic resolution is opt-in (`DynamicResolution.Enabled = true`).

- Remove device-idle waits, target rebuilds, and unnecessary pipeline recreation from internal resolution changes.
- Evaluate preallocated resolution steps or maximum-sized targets with an active rendering rectangle; budget the extra VRAM.
- Preserve resource lifetimes and correct history, UV, and mip handling.
- Measure full frame intervals including `BeginFrame`: transition p95/p99 and worst hitch. Current benchmark CPU frame timing covers only `DrawScene`.

# Realtime GI Closure Sponza Baseline

This directory is the durable home for the two 2026-07-15 Sponza low/high source captures and their replacement captures. The original attachments are not in this repository: a full workspace search found neither supplied JSON filename nor either image attachment. `manifest.json` therefore records all four expected files as `pending-external-import`; its null hashes are intentional and are not placeholder values.

## Importing the supplied artifacts

When the original attachments become available, retain the original files long enough to compute their hashes, then copy them here using the canonical names in `manifest.json`:

```powershell
Get-FileHash <original-file> -Algorithm SHA256
```

Populate `sourceSha256`, `sourceSizeBytes`, and, for images, `sourceAttachmentName` in the manifest. Change only the affected artifact status to `imported`; change the top-level status to `complete` only after all four required artifacts are present and hashes have been reviewed. Do not invent a source filename or a hash from a renamed copy.

## Reproducing a new capture

The in-repo capture driver locks `1600x900`, 60 Hz fixed-frame progression, 360 warmup frames, deterministic seed `0x20260715`, the canonical stationary Sponza GI scenario, and a 16-second low-to-high vertical traversal. It produces both endpoint metadata snapshots and the following image set at each bookmark:

- beauty
- direct-only
- final-indirect
- sampled-irradiance
- volume-contributor
- support
- visibility
- ownership
- fallback

Run it from a built tree with:

```powershell
dotnet run --no-build --project NjulfHelloGame/NjulfHelloGame.csproj -- --sponza-gi-capture-dir Plans/Baselines/RealtimeGiClosure-20260715/captures/local-run
```

The fixed image sequence is 1,338 rendered frames, followed by a bounded renderer-screenshot settlement phase when necessary. It writes `sponza-gi-capture-contract.json` (including a stable fingerprint), `sponza-gi-coverage-oracle.json`, `sponza-gi-visual-metric-gate.json`, and `sponza-gi-capture-run.json` next to the artifacts. The required endpoint PNG is an immediate client-area capture taken before the sequence advances to another debug view; a supplementary `.renderer.png` renderer-target image is also required.

Renderer-target requests are queued before the matching debug/output frame is rendered. `status: completed` is fail-closed: all immediate and renderer-target PNGs must exist, parse as complete PNG streams, and be recorded with SHA-256 and byte length that is stable across two rendered-frame observations. A renderer screenshot request is not evidence by itself. The harness waits up to 600 rendered frames for requested renderer files, then writes `status: failed` with the missing-artifact reason rather than producing a mixed or incomplete baseline.

The scripted command records `ProductionTiming`, where only the non-debug beauty endpoint is timing eligible. Interactive `Ctrl+F11` records `DetailedDiagnostics`, disables GPU timing, and is intended for debug-image evidence rather than performance claims. The visual-metric sidecar supplies deterministic world-space ROI and metric requirements, but deliberately remains unevaluated until approved source images and reviewed thresholds are imported; it does not invent a visual baseline.

For interactive use, press `Ctrl+F11`; output is placed in `SponzaGiCaptures` below the app directory. The named endpoint transforms and ROIs are defined in `SampleSponzaGiCaptureHarness.cs` and are checked by unit tests and the CPU receiver-coverage oracle over all 960 fixed traversal samples. Every locked ROI also requires a valid coarser transition fallback.

The high bookmark is a deterministic 9 m vertical counterpart to the known low Y=1.35 m transform. The original high attachment did not contain camera metadata in this workspace, so this transform is the canonical reproduction transform for future captures; it is not represented as recovered attachment metadata.

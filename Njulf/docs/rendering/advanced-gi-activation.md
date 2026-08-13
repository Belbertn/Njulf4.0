# Advanced GI activation and qualification

Advanced GI is enabled through a cold-start transaction, not by mutating the
live renderer. B1, C1, C3, C4, and C5 can change optional Vulkan features,
immutable render-graph branches, descriptor inventory, and persistent memory.
The editor therefore keeps a detached next-start draft and always shows
requested, effective, and authoritative runtime state separately.

## Activation paths

There are two deliberately different activation levels:

- Explicit experiment modes run a bounded measurement candidate. B1, C1, and
  C3 require the complete frozen-prerequisite manifest. C4 and C5 additionally
  require an exact candidate profile because their safe resource layouts are
  content-dependent. Candidate authorization is never promotion evidence.
- `AutoQualified` requires the frozen prerequisites, an authenticated
  schema-v3 qualification entry for the exact feature ID, exact build/shader/
  settings/corpus/content/scene identity, a matching device/driver rule, and
  C4/C5 runtime evidence where applicable.

Any missing or mismatched input resolves to canonical DDGI. A rejected startup
profile is authoritative and cannot be combined with ambient manifests or
command-line mode overrides.

## Editor workflow

Open **Global Illumination > Advanced GI activation (next cold start)**.

1. Select each next-start mode. Use explicit experiment modes while collecting
   evidence; use `AutoQualified` only after the corresponding qualification
   manifest has been reviewed and pinned.
2. Supply the exact corpus hash, runtime content-profile ID, observed scene
   asset hash, prerequisite manifest, and any qualification/runtime/candidate
   files needed by the selected modes.
3. Choose **Validate startup draft**. Every non-device check must pass.
4. Choose **Save and restart renderer**. The editor writes a content-addressed
   render-settings snapshot, records its complete persistence SHA-256 in the
   schema-v2 startup profile, atomically publishes the profile, reads it back,
   fully tears down the old device/window/services, and reconstructs the host
   from that profile.
5. Inspect requested-to-effective mode, fallback detail, allocation, and
   authoritative publication state after restart. Editor preflight does not
   predict physical-device support; startup admission remains authoritative.

The standalone launch equivalent is:

```text
NjulfHelloGame --advanced-gi-startup-profile <profile.json>
```

or `NJULF_ADVANCED_GI_STARTUP_PROFILE=<profile.json>`.

## Locking a corpus

[`advanced-gi-corpus.request.example.json`](advanced-gi-corpus.request.example.json)
contains the 15 required scene/sequence classes from the implementation plan.
It is a request template, not evidence: the zero lengths and empty hashes are
intentionally rejected by verification.

Copy the template into an evidence root, produce each deterministic scene,
camera script, render-settings file, and reviewed reference image, then pin it:

```text
dotnet run --project Njulf.AssetTool -- advanced-gi pin-corpus \
  --root <evidence-root> \
  --request <evidence-root>/corpus.request.json \
  --out <evidence-root>/corpus.json

dotnet run --project Njulf.AssetTool -- advanced-gi verify-corpus \
  --manifest <evidence-root>/corpus.json
```

Pinning requires unique cases and roles, safe contained paths, all five feature
classes, and `scene`, `camera-script`, `settings`, and `reference` artifacts for
every case. It hashes each stable file, atomically publishes the strict manifest,
then verifies the visible result. The output path is rejected if it aliases an
artifact, so pinning cannot overwrite evidence. The printed `corpusSha256` is
the value used by qualification reports and startup content bindings.

## Creating and checking a startup transaction

Create a profile from a reviewed settings file and exact identities:

```text
dotnet run --project Njulf.AssetTool -- advanced-gi create-startup \
  --profile <profile.json> \
  --settings <render-settings.json> \
  --corpus-sha256 <sha256> \
  --content-profile <runtime-scenario-id> \
  --scene-sha256 <observed-scene-asset-sha256> \
  --prerequisite <prerequisite.json> \
  --qualification <qualification.json> \
  --runtime-evidence <runtime-evidence.json> \
  --candidate <candidate.json>
```

Only pass inputs required by the selected modes. Verify a transaction in CI or
before launch with:

```text
dotnet run --project Njulf.AssetTool -- advanced-gi verify-startup \
  --profile <profile.json> \
  --build-commit <commit> \
  --shader-bundle-sha256 <sha256>

dotnet run --project Njulf.AssetTool -- advanced-gi verify-qualification \
  --manifest <qualification.json>
```

Build/shader identity is mandatory when the selected settings contain an
`AutoQualified` feature or an explicit C4/C5 candidate; preflight fails without
it. The runtime always repeats those checks using its own binary identity and
additionally checks Vulkan capability, device/driver rules, memory headroom,
scene identity, and resource completeness.

## Evidence boundary

Source compilation, editor preflight, corpus pinning, and unit tests do not
qualify hardware. `AutoQualified` should be selected only after the required
independent runs, rendered references, 30–60 minute stability captures,
fallback/lifecycle tests, target-device matrix, measurements, and artifact
hashes have passed the schema-v3 validators. No checked-in example carries a
production qualification ID.

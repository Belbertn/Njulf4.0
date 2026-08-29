# Advanced GI controls and qualification

The editor exposes ordinary on/off switches for B1, C1, C3, C4, and C5.
Changing one automatically performs a clean renderer restart because these
features can change optional Vulkan features, immutable render-graph branches,
descriptor inventory, and persistent memory. The switch path does not create,
save, or load an Advanced GI profile, manifest, or evidence file.

## Switch behavior

There are two deliberately different activation levels:

- **Enabled** selects the explicit bounded implementation. It requires no
  qualification artifacts. Hardware support, Vulkan limits, exact ABI/layout,
  independent memory budgets, allocation success, and runtime resource
  completeness are still enforced. C1 therefore remains unavailable on a
  device without `VK_EXT_opacity_micromap`, and C4 produces visible work only
  when the scene contains an eligible authored hero and light source.
- `AutoQualified` requires the frozen prerequisites, an authenticated
  schema-v3 qualification entry for the exact feature ID, exact build/shader/
  settings/corpus/content/scene identity, a matching device/driver rule, and
  C4/C5 runtime evidence where applicable.

Missing promotion input can disable `AutoQualified`; it never disables an
explicit Enabled switch. Profiles are an optional automation facility and are
not part of the editor switch path.

## Editor workflow

Open **Global Illumination > Advanced GI features**.

Toggle an individual feature or **Enable all Advanced GI features**. The host
fully tears down the old renderer/device and reconstructs it with the explicit
selection, then reopens the editor. Inspect requested-to-effective mode,
fallback detail, allocation, and authoritative publication state in the status
section. A switch can be on while its effective state is unavailable for a
real hardware, memory, content, or resource reason; the tooltip reports that
reason.

The remaining sections describe the separate qualification-automation path.
Normal editor use does not require it.

For a strict scene run that proves the receiver cache, accelerated transport
solver, C1, C3, and C4 were simultaneously effective, executed, and consumed,
use the fail-closed workflow in
[`gi-all-on-qualification.md`](gi-all-on-qualification.md). It is deliberately
stricter than an editor switch or requested/effective mode display.

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
`AutoQualified` feature or when a qualification run explicitly supplies a
C4/C5 candidate profile. The runtime always repeats those checks using its own
binary identity and additionally checks Vulkan capability, device/driver rules,
memory headroom, scene identity, and resource completeness.

## Evidence boundary

Source compilation, editor preflight, corpus pinning, and unit tests do not
qualify hardware. `AutoQualified` should be selected only after the required
independent runs, rendered references, 30–60 minute stability captures,
fallback/lifecycle tests, target-device matrix, measurements, and artifact
hashes have passed the schema-v3 validators. No checked-in example carries a
production qualification ID.

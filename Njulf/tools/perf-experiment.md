# Standalone Performance Experiments

`perf-experiment.ps1` qualifies planned or manually reviewed candidates that
cannot enter the pinned campaign's fixed candidate envelopes. It reads the
campaign manifest, lock, workloads, references, and thresholds without changing
them. It contains no Git mutation workflow: both variants must already exist as
clean, checked-out source roots at exact commits.

Run the script with PowerShell 7:

```powershell
pwsh -NoProfile -File ./tools/perf-experiment.ps1 `
  -SpecPath <experiment.json> `
  -RunDirectory .perf-loop-runs/experiments
```

Use `-ValidateOnly` before a run. `-AnalyzeOnly` recomputes a decision from an
existing immutable experiment directory and refuses missing capture evidence.

## Specification

The strict `njulf-perf-experiment/v1` JSON object contains:

- `experimentId`: lowercase filesystem-safe evidence identifier.
- `mode`: `aa` or `ab`. A/A requires identical commits and arguments.
- `campaignRunDirectory`: directory containing the authenticated
  `campaign.lock.json` and references.
- `cookedAssetRoot`: external immutable cooked-asset bundle root containing the
  manifest's platform directory.
- `baseline` and `candidate`: objects with `sourceRoot`, exact lowercase
  `commit`, `arguments`, and a `workloadArguments` object keyed by workload ID.
- `configurations`: a subset of the manifest's final configurations. A decision
  remains inconclusive unless the complete final configuration set runs.
- `claims`: one or more `{ workloadId, targetDomain, targetPass }` objects.
- `acceptanceMode`: `manifest-either`, `frame-and-pass`, `pass-only`, or
  `loop-frame-1ms`.
- `focusedTestFilter`: optional NUnit filter run against both builds.

All qualification workloads are added as non-regression controls. The driver
uses the manifest's three-cycle A-B-B-A topology, six paired differences,
deterministic 10,000-sample bootstrap, hard HDR gates, and one-percent secondary
regression limit. It invokes the frozen reference build's benchmark pair
comparer for each A/B pair and writes build logs, raw reports, health reports,
HDR images, pair reports, hashes, and `decision.json` beneath the experiment ID.

Phase arguments cannot override scene, resolution, trajectory, warmup,
measurement, validation, quality-reference, or production requirements. They
are intended only for a candidate's narrow internal selector or rollback
toggle.

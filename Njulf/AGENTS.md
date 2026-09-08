# Artifact retention

The user wants bounded disk usage for benchmarks and diagnostics. Cleanup of obsolete generated run data is authorized.

- At major milestones, save a compact Markdown record under `docs/performance/milestones/`: date, source revision/dirty state, hardware and workload/settings, baseline and candidate timings (including tail latency), quality/validation results, decision, limitations, and reproduction command. Record regressions and rejected candidates as well as improvements. Link compact machine-readable comparisons where useful.
- Keep bulky raw captures, traces, images, and isolated build copies only for the active investigation and at most two days after completion. Preserve any reference still required by an active campaign. Prune superseded and failed runs after recording useful findings; do not accumulate full run archives.
- Use `tools/prune-local-artifacts.ps1` to inspect old generated payloads, then `-Apply` to remove them. It preserves compact text/JSON/CSV evidence and recent files. Review remaining storage at milestones; consolidate redundant reports into milestone records.
- Cleanup must stay inside the repository's ignored artifact/temp/test-result roots, skip tracked files and reparse points, and avoid running processes' inputs/outputs. Never delete source assets or user work to make room.
- Keep Codex-created benchmark, diagnostic, capture, and isolated-build output on D: inside this workspace. Do not fall back to the C: system drive when space is low; prune obsolete generated data first. C: cleanup is authorized only for files verified as created by Codex; leave uncertain ownership and unrelated files untouched. Preserve relevant evidence on D: and verify copies before deleting originals.

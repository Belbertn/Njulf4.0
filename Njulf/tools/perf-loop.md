# Performance Loop Harness

`tools/perf-loop.ps1` wraps the existing `NjulfHelloGame` benchmark mode so an
external agent or cheaper model can try a candidate change, benchmark it, and keep
or roll it back automatically.

Default behavior per iteration:

1. Checkpoint the current worktree with `git stash --include-untracked`, then
   immediately re-apply it so existing dirty work stays visible.
2. Run a baseline benchmark.
3. Run `-TrialCommand`.
4. Run a candidate benchmark.
5. Compare median p95 frame time across repeats.
6. Keep the candidate only when it improves by at least
   `-MinImprovementPercent` and does not introduce a new budget-status
   regression.
7. Restore the exact pre-trial worktree when the candidate is rejected.

Example:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 5 `
  -RepeatCount 3 `
  -WarmupFrames 30 `
  -MeasureFrames 120 `
  -TrialCommand "codex exec --model gpt-5-mini 'Find one narrow renderer performance improvement and edit the code.'"
```

Useful options:

- `-PrimaryMetric cpu|gpu|auto`: `auto` uses GPU p95 only when all reports have
  valid GPU timing; otherwise it falls back to CPU p95.
- `-MinImprovementPercent 3`: minimum required improvement to keep a candidate.
- `-MaxRegressionPercent 1`: obvious regressions are rejected immediately.
- `-KeepInconclusive`: keep changes that land inside the noise band.
- `-RollbackRejected:$false`: report the decision but leave rejected changes in
  the worktree for inspection.
- `-BenchmarkCommand`: custom benchmark command template. Use `{ReportPath}`,
  `{Iteration}`, `{Phase}`, `{Repeat}`, and `{RunDirectory}` placeholders.

Reports and decisions are written under `.perf-loop-runs/`, which is ignored by
git.

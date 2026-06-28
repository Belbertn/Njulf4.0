# Performance Loop Harness

`tools/perf-loop.ps1` runs a repeatable benchmark loop around `NjulfHelloGame`.
Use it when you want an automated agent or model to try a narrow performance
change, measure it against the current code, and keep only changes that improve
the chosen frame-time metric.

## What It Does

Each iteration:

1. Saves a pre-trial checkpoint of the current worktree with
   `git stash --include-untracked`, then reapplies it so existing local edits are
   still visible to the trial command.
2. Runs one or more baseline benchmark repeats.
3. Runs `-TrialCommand`.
4. Runs one or more candidate benchmark repeats.
5. Compares the median p95 frame time.
6. Keeps the candidate only when it improves by at least
   `-MinImprovementPercent` and does not introduce a worse budget status.
7. Restores the exact pre-trial worktree when the candidate is rejected.

Reports and decisions are written under `.perf-loop-runs/`, which is ignored by
git. Default benchmark runs also include a smoke-frame watchdog and a process
timeout so the loop can roll back instead of hanging forever if the sample window
does not close normally.

## Before Running

Run from the repository root:

```powershell
cd D:\Code\C#\Njulf4.0-Simplified\Njulf
```

Make sure the project builds and the benchmark mode can start:

```powershell
dotnet build .\Njulf.sln -c Release
dotnet run --project .\NjulfHelloGame\NjulfHelloGame.csproj -c Release -- `
  --benchmark `
  --benchmark-report .\.perf-loop-runs\manual-check.json `
  --benchmark-warmup-frames 5 `
  --benchmark-measure-frames 10 `
  --performance-scenario Normal
```

Check the working tree before using the loop:

```powershell
git status --short
```

Dirty work is supported, but the loop uses temporary stashes. If you have
valuable uncommitted work, either commit it first or be prepared to inspect
`git stash list` if a run is interrupted.

## Basic Use

Run one automatic optimization attempt:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 1 `
  -RepeatCount 3 `
  -WarmupFrames 30 `
  -MeasureFrames 120 `
  -TrialCommand "codex exec --model gpt-5-mini 'Find one narrow renderer performance improvement. Keep behavior unchanged, edit the code, and run focused tests if practical.'"
```

Run a longer loop:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 5 `
  -RepeatCount 3 `
  -WarmupFrames 30 `
  -MeasureFrames 120 `
  -MinImprovementPercent 3 `
  -MaxRegressionPercent 1 `
  -TrialCommand "codex exec --model gpt-5-mini 'Find one narrow renderer performance improvement. Prefer low-risk hot-path allocation, command recording, or shader-side simplifications. Do not change visuals intentionally.'"
```

The loop prints `KEEP` or `ROLLBACK` for each iteration. A kept candidate remains
in the working tree for review. A rejected candidate is rolled back by default.

## Mistral Vibe CLI

Use this path when the loop should run Mistral AI's coding models automatically.
The harness itself is still launched from PowerShell, but the trial command can
run through Git Bash with `-TrialShell git-bash`.

Install and verify Vibe from Git Bash:

```bash
curl -LsSf https://mistral.ai/vibe/install.sh | bash
vibe --version
vibe --setup
```

If you prefer `uv` on Windows:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
uv tool install mistral-vibe
```

Vibe stores API keys and config under `~/.vibe/`. To use a Mistral-hosted model,
either complete `vibe --setup` or export an API key in Git Bash:

```bash
export MISTRAL_API_KEY="..."
```

To choose a model explicitly, edit `~/.vibe/config.toml` or project-local
`.vibe/config.toml`. Example:

```toml
active_model = "mistral-medium-3.5"
```

For OpenRouter or another OpenAI-compatible endpoint, define a provider and a
model alias in Vibe's config, then set `active_model` to the alias. Example:

```toml
active_model = "devstral-openrouter"

[[providers]]
name = "openrouter"
api_base = "https://openrouter.ai/api/v1"
api_key_env_var = "OPENROUTER_API_KEY"
api_style = "openai"
backend = "generic"

[[models]]
name = "mistralai/devstral-2512"
provider = "openrouter"
alias = "devstral-openrouter"
```

Run the perf loop with Vibe through Git Bash:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 3 `
  -RepeatCount 3 `
  -WarmupFrames 30 `
  -MeasureFrames 120 `
  -TrialShell git-bash `
  -TrialCommand "vibe --prompt 'Find one narrow renderer performance improvement. Keep behavior unchanged. Run focused tests if practical.' --auto-approve --max-turns 8"
```

If Git Bash is not on `PATH`, pass it explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 1 `
  -RepeatCount 1 `
  -TrialShell git-bash `
  -GitBashPath "C:\Program Files\Git\bin\bash.exe" `
  -TrialCommand "vibe --prompt 'Inspect the renderer and make one small performance change.' --auto-approve --max-turns 6"
```

Use Vibe's programmatic mode for the loop. Interactive `vibe` sessions can block
the benchmark loop waiting for user input.

## JetBrains ACP

JetBrains ACP is useful when you want to use Mistral Vibe manually from a
JetBrains IDE instead of inside the automatic loop. It is not a good fit for
`-TrialCommand` because the perf loop needs a non-interactive process that exits.
For the loop, use Vibe CLI with `-TrialShell git-bash`.

After installing Vibe, verify that `vibe-acp` exists:

```bash
vibe-acp --help
```

In the JetBrains IDE:

1. Open AI Chat.
2. Add an ACP agent from the registry if Vibe is listed, or add a custom agent.
3. For a custom agent, use `vibe-acp` as the command.
4. Authenticate through Vibe first with `vibe --setup`, or provide the needed
   environment variables in the ACP config.

Example `~/.jetbrains/acp.json` shape:

```json
{
  "default_mcp_settings": {
    "use_custom_mcp": true,
    "use_idea_mcp": true
  },
  "agent_servers": {
    "Mistral Vibe": {
      "command": "C:\\Users\\<you>\\.local\\bin\\vibe-acp.exe",
      "args": [],
      "env": {
        "MISTRAL_API_KEY": "<optional-if-already-configured>"
      }
    }
  }
}
```

Use the real `vibe-acp` path from:

```powershell
Get-Command vibe-acp
```

## Recommended Workflow

1. Start with `-Iterations 1 -RepeatCount 1 -WarmupFrames 5 -MeasureFrames 10`
   to prove the benchmark launches on your machine.
2. Move to `-RepeatCount 3` or higher before trusting the decision.
3. Keep trial prompts narrow. Ask for one small optimization per iteration.
4. After a `KEEP`, review the diff with `git diff`, run relevant tests, and
   commit or discard the accepted change before starting unrelated work.
5. Delete old benchmark output when it is no longer useful:

```powershell
Remove-Item -Recurse -Force .\.perf-loop-runs
```

## Choosing The Benchmark Scenario

The default scenario is `Normal`. Use `-Scenario` to target a subsystem:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 3 `
  -RepeatCount 3 `
  -Scenario ForestFoliage `
  -TrialCommand "codex exec --model gpt-5-mini 'Improve foliage rendering performance with a small, behavior-preserving change.'"
```

Common scenarios include:

- `Normal`
- `ManyLights`
- `ManyMaterials`
- `ManyTransparentObjects`
- `LargeMeshletCount`
- `DenseGrassField`
- `ForestFoliage`
- `ReflectionHeavy`
- `GiSponzaRightWallStationary`
- `CombinedWorstCase`

Scenario names are parsed case-insensitively and may also use hyphens or
underscores through the game command line. For the PowerShell loop, passing the
enum-style name is the clearest option.

## Important Options

- `-Iterations`: number of improvement attempts.
- `-RepeatCount`: benchmark repeats per baseline and candidate. Use at least `3`
  for meaningful comparisons.
- `-WarmupFrames`: frames ignored before measurement begins.
- `-MeasureFrames`: measured frames per repeat.
- `-BenchmarkTimeoutSeconds`: max seconds for each benchmark process. Default
  is `900`; use `0` to disable.
- `-TrialTimeoutSeconds`: max seconds for `-TrialCommand`. Default is `1800`;
  use `0` to disable.
- `-Scenario`: `SamplePerformanceScenario` used by `NjulfHelloGame`.
- `-TrialShell powershell|git-bash`: shell used for `-TrialCommand`. Use
  `git-bash` for Vibe CLI on Windows.
- `-GitBashPath`: explicit path to Git Bash when `bash.exe` is not on `PATH`.
- `-PrimaryMetric auto|cpu|gpu`: `auto` uses GPU p95 only when every report has
  valid GPU timing; otherwise it uses CPU p95.
- `-MinImprovementPercent`: minimum improvement required to keep the candidate.
- `-MaxRegressionPercent`: regression threshold that makes rollback obvious in
  the decision reason.
- `-KeepInconclusive`: keeps changes inside the noise band instead of rolling
  them back.
- `-RollbackRejected:$false`: leaves rejected changes in the worktree for manual
  inspection.
- `-KeepRejectedStashes`: keeps the temporary stash for rejected candidates.
- `-RunDirectory`: output directory for benchmark reports and decisions.
- `-BenchmarkCommand`: replaces the default `dotnet run` benchmark command.

## Custom Benchmark Command

`-BenchmarkCommand` is useful when you need extra command-line flags:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\perf-loop.ps1 `
  -Iterations 2 `
  -RepeatCount 3 `
  -BenchmarkCommand "dotnet run --project '.\NjulfHelloGame\NjulfHelloGame.csproj' -c Release -- --benchmark --benchmark-report '{ReportPath}' --benchmark-warmup-frames 30 --benchmark-measure-frames 120 --performance-scenario ForestFoliage --scene-gpu-compaction --scene-indirect-dispatch" `
  -TrialCommand "codex exec --model gpt-5-mini 'Improve GPU-driven scene submission performance with one narrow code change.'"
```

Supported placeholders:

- `{ReportPath}`
- `{Iteration}`
- `{Phase}`
- `{Repeat}`
- `{RunDirectory}`
- `{SolutionRoot}`

## Reading Results

Each iteration writes:

- `.perf-loop-runs/iteration-###/baseline-##.json`
- `.perf-loop-runs/iteration-###/candidate-##.json`
- `.perf-loop-runs/iteration-###/decision.json`

The full run writes:

- `.perf-loop-runs/summary.json`

`decision.json` contains the decision, metric, baseline p95, candidate p95,
improvement percentage, and any budget regressions. A successful candidate has:

```json
{
  "Decision": "keep",
  "Failed": false
}
```

## Recovery Notes

If the script is interrupted, inspect the worktree and stash list:

```powershell
git status --short
git stash list
```

Pre-trial checkpoints are named like `perf-loop pretrial iteration ...`.
Rejected candidate stashes are named like `perf-loop rejected candidate ...`.
Apply or drop them manually only after confirming which stash contains the state
you want:

```powershell
git stash show --stat stash@{0}
git stash apply --index stash@{0}
git stash drop stash@{0}
```

Do not treat an automatic `KEEP` as a finished change. It means the measured
candidate beat the configured benchmark gate. Review the diff, run the relevant
tests, and check the rendered scene before committing.

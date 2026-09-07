# Inter-frame dependencies

Consumer dependencies are enabled by default with `--async-compute-mode Disabled`.
Set `NJULF_PRECISE_INTERFRAME_DEPENDENCIES=0` to restore the original full boundary.
Async compute configurations and the first priming frame retain the full dependency.
Remaining incomplete resource contracts retain conservative consumer fallbacks.

Latest revision: 3 consumer barriers (2 conservative fallbacks), down from 35.
Nine focused tests and the resize synchronization smoke pass. The matched GPU
average is effectively unchanged (21.678 -> 21.665 ms). The user requested making
this revision the default after reviewing the results. This is not a demonstrated
speedup or proof of greater correctness than the corrected original path.

## Resource audit and implementation

| Resource / access | Current treatment |
| --- | --- |
| Graph-owned targets and temporal history banks | Track submitted readers and writers by physical handle and allocation generation. Before a conflicting consumer, batch narrow stage/access dependencies; unchanged image layouts still receive memory ordering. |
| Imported buffers, scene color/depth, Hi-Z, DDGI atlases/state/scheduler, reflection resources | Resolve through existing concrete bindings. Their owners also upload, reset, capture or read back outside the graph, so retain an all-commands **source** scope at the declared consumer. Missing stage/access declarations and missing bindings retain conservative consumer barriers. |
| Meshlet uploads and CPU scene/light uploads; external foliage/skinning compute, AS and micromap builds | Retain the prelude dependency, restricted to transfer, compute and device-enabled AS/micromap-build destination stages. Current owner APIs do not expose complete first-access contracts. |
| Explicit renderer color/depth clear | Order color/depth attachment stages before touching the shared targets. |
| Independent compute queues | Keep the original frame boundary and existing timeline/ownership machinery. The candidate has no new cross-queue synchronization protocol. |

Recording history is committed only after successful terminal graphics submission.
Abandoned recordings do not become history; unused resources retain their prior
accesses, and retired binding generations are pruned on submission. A first
candidate frame, including a transition back from the legacy path, uses the full
barrier to establish history. Exact destination stage/access pairs are tracked
separately; unioning independent pairs must not invent visibility at a third pair.

The candidate deliberately does **not** claim complete replacement coverage:
legacy unscoped declarations and owner-managed accesses still constrain overlap.
No resource duplication, shader edits or scheduler replacement are included.

## Verification

- Focused Release C# renderer and sample-host builds succeeded without warnings.
- `InterFrameAccessTrackerTests`: 7 cases passed, covering RAW, WAR, WAW,
  independent reads, accumulated readers/skipped frames, writer visibility at a
  later consumer stage, abandoned recordings and allocation replacement.
- The full build compiled 78 existing shader misses. Its receiver-SPIR-V verifier
  was stopped after more than ten minutes; that full build is not a passing check.
  C# verification used `BuildProjectReferences=false`, `DesignTimeBuild=true`,
  `SkipCompilerExecution=false` against existing dependency/shader artifacts, with
  `SourceRevisionId` set to the actual HEAD for the existing health-report identity
  contract. The working tree remains dirty and is reported as such.

Synchronization validation found an existing swapchain acquire hazard in both
legacy and candidate runs, before production rendering started. The swapchain
transition now includes `COLOR_ATTACHMENT_OUTPUT` in its source stage when entering
color-attachment layout from undefined/present, chaining it to the acquire
semaphore's wait stage. This is a correctness fix independent of the candidate.
See the [Khronos acquire example](https://docs.vulkan.org/guide/latest/synchronization_examples.html#_multiple_queues).

The initial baseline (before that acquire correction) measured 27.654 ms average
GPU time and 28.152 ms GPU p95. It is retained as preliminary evidence, not used to
qualify the corrected candidate. GPU-frame timestamps begin at top-of-pipe after
the frame-boundary command; moving its destination scope changes the waits inside
that interval. Frame pacing must also be compared, rather than interpreting a
timestamp-scope change as a performance result.

Two additional owner-local synchronization corrections were required:

- Meshlet residency clears now order transfer writes before subsequent header/range
  copies. The original frame-boundary barrier cannot order these same-frame commands.
- Reflection task/indirect/tile header updates now use `CLEAR`, the execution stage
  of `vkCmdUpdateBuffer`, in their reset barriers. Counter readback retains `COPY`.
  The existing destination includes compute and indirect-command reads. See
  [Khronos clear commands](https://docs.vulkan.org/spec/latest/chapters/clears.html).

The final candidate passed the 12-frame Bistro resize synchronization smoke with
zero warnings/errors, including 1280x720, 1600x900 and 800x600 resizes. This is a
focused check, not exhaustive coverage of every feature or queue configuration.

## Initial comparison and decision

Both runs used the same corrected Release binary, Bistro Normal, 1920x1080,
graphics-only scheduling, validation off, GPU timing on, VSync off, no FPS cap,
30 warmup frames and 240 measured frames. Both settled without timeout. Both use
the host's `baseline` variant; only the dependency environment switch differs.
The post-measurement HDR capture is outside the timed interval.

| Metric | Original dependency | Candidate |
| --- | ---: | ---: |
| GPU average | 21.306 ms | 21.534 ms |
| GPU p95 | 21.705 ms | 22.160 ms |
| CPU DrawScene average | 8.933 ms | 12.852 ms |
| Last 120-frame pacing window average | 22.46 ms | 23.08 ms |
| Last 120-frame pacing window p95 | 26.17 ms | 28.86 ms |

The HDR comparison passed the existing thresholds (relative RMSE 0.07067 <= 0.12,
FLIP p95 0.01215 <= 0.02). This does not imply pixel identity; startup frame counts
and temporal history differ. The pacing windows are diagnostic windows, not an
exact aggregate of the 240 measured frames.

**Initial decision: do not promote that revision.** There was no measured speedup, DrawScene elapsed time increased,
and Bistro reported 35 consumer barriers, all with conservative fallbacks. The
original dependency remained the production default. Completing the replacement
requires precise owner-managed/imported-resource access contracts, then repeating
the focused qualification; broad fallback barriers are not the finished optimization.

Evidence directory: `C:\Users\njaal\AppData\Local\Temp\njulf-interframe-20260906`.
Final files are `candidate-sync-corrected-health.json`, `final-baseline.json`,
`final-candidate.json`, corresponding logs/health reports, and the two final PFMs.

## Review follow-up

The original CPU-cost interpretation was too strong: DrawScene increased by
3.918 ms while frame-fence waiting fell by 4.018 ms (11.495 to 7.477 ms). These
elapsed-time measurements do not isolate additional CPU processing.

The revised candidate reuses dependencies that already order all prior-submission
memory accesses at a destination stage. The prelude and explicit clear seed that
coverage; an imported-resource fallback adds only uncovered stages. Coverage resets
for every recording, including abandoned recordings. This does not synchronize
writes later in the same recording: existing owner-local barriers remain required.
Graph-owned accesses still record history even when their barrier is covered.

Depth, motion and shadow draw-input declarations now identify indirect, vertex-input
and graphics shader reads instead of an unscoped read. Other incomplete declarations
remain conservative. No new tracking subsystem or resource duplication was added.

The planner used by command recording is exercised directly by two additional tests:
production depth/motion consumers reuse prelude coverage without an all-commands
destination, and the production motion-vector image declarations produce a
color-attachment to compute-sampled dependency with no fallback. All nine focused
tests pass; focused renderer/host Release C# builds pass.

The revised 12-frame resize synchronization smoke passed with zero warnings and
zero errors. The same one-pair benchmark configuration described above was repeated
on the revised binary; both captures have identical loaded-shader fingerprints,
240 measured GPU samples, 1920x1080 resolution and no settling timeout.

| Metric | Original dependency | Revised candidate |
| --- | ---: | ---: |
| GPU average | 21.678 ms | 21.665 ms |
| GPU p95 | 22.241 ms | 22.393 ms |
| DrawScene elapsed average | 13.793 ms | 14.691 ms |
| Frame-fence wait average | 6.108 ms | 5.335 ms |
| Last 120-frame pacing window average | 23.34 ms | 23.51 ms |
| Last 120-frame pacing window p95 | 28.12 ms | 31.25 ms |

The candidate emits 3 consumer barriers, 2 of which use conservative fallbacks,
versus 35/35 in the initial experiment. HDR comparison passed (relative RMSE
0.06071, FLIP p95 0.00175). GPU average differs by only -0.06%; frame pacing does
not improve. This pair is inconclusive for a performance benefit. The revised path
was subsequently made the default at the user's request. It still needs complete owner-managed
resource contracts before removing its remaining conservative dependencies.

Revised evidence uses the `revised-sync`, `revised-baseline` and `revised-candidate`
prefixes in the same evidence directory. No additional benchmark campaign was run.

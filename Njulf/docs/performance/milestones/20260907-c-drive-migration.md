# Codex diagnostic data moved off the system drive

On 2026-09-07, six dedicated diagnostic directories under `C:\Users\njaal\AppData\Local\Temp` were verified against Codex session tool calls that created them. Only their regular files were processed. Unverified temp directories, system files, application installations, Codex configuration/session databases, and junction targets were left untouched.

The retained files now live under `artifacts/c-drive-migration-20260907/`, with each original directory name and relative path preserved:

- `NjulfSecondarySnapshot`: before/after/final timings, comparison images, summary, launch arguments, binary hashes, tests and logs. [Implementation milestone](../../../implementation/SecondaryScenePreparation.md) reports a provisional 55.6% reduction in median secondary preparation time (2.159 ms), with matched workload signatures and a passing image gate; keep the cache, defer spatial indexing.
- `NjulfReflectionLod`: quality evidence and compact build metadata.
- `njulf-wall-leak-20260906`: numeric pixel/probe evidence, images, reproducer and patches. [Wall-leak findings](../../../implementation/Complete/BistroWallLightLeakFix-20260906.md).
- `njulf-shadow-motion-20260906`: comparison results, images and reproducer. [Reflection-motion findings](../../../implementation/Complete/ReflectionMotionBrightnessFix-20260906.md).
- `njulf-interframe-20260906`: baseline/candidate timings, health reports, images and synchronization logs.
- `njulf-curtain-diagnosis-20260906`: diagnostic results and logs.

Every retained file was SHA-256 verified on D: before its original was deleted from C:. Disposable binaries, build caches, raw buffer dumps and RenderDoc captures were discarded. The migration manifest records each file's source, destination, action and retained-file digest. Raw-capture paths in historical reports can no longer be used; retained script paths may need updating to the migration location before reproduction.

The user-confirmed `njulf-tile-diagnostics-20260907` directory was already empty on C:; only that empty directory was removed. Its 11 files remain in `.perf-loop-runs/tile-diagnostics-20260907/direct-light-smoke/`.

Eight junctions under the old `NjulfReflectionLod/app` directory were skipped and remain on C:. Their target files were neither traversed nor changed by migration.

Additional pruning of redundant migrated test assets on D: was blocked by automatic approval review (`blocked by policy`). Those copies remain under `artifacts/c-drive-migration-20260907/NjulfSecondarySnapshot/tests/`; they are included in retained-byte totals and are not needed as milestone evidence.

See [the compact migration audit](c-drive-migration-20260907.json) for provenance, byte totals, and the full manifest location. Future diagnostic output belongs on D:; the repository instructions prohibit falling back to the C: system drive.


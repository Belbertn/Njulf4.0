# Trim Scene-Compaction Output Duplication

## Goal

Production Forward consumes the material-bucket streams, while the aggregate stream exists for CPU/GPU validation. Keep bucket commands canonical and skip aggregate reservation/writes in production.

## Implementation

- Use a currently free bit in the existing compaction flags to request aggregate validation output; do not change push-constant size.
- In flat and GPU-instance compaction, perform aggregate reservation, capacity checks, writes, and indirect-count updates only when that bit is set.
- In production, reserve and emit directly to the selected material/sided bucket. Aggregate capacity must not reject a valid bucket command.
- Preserve logical global candidate, emitted, and overflow diagnostics by aggregating bucket counters or maintaining lightweight global counters. Preserve bucket, sided, LOD, and hierarchy/fallback telemetry.
- Retain aggregate buffers, descriptor slots, and validation readback. Update their clear, barrier, indirect-count, and readback assumptions for both modes.

## Verification

- Compile all compaction variants and add one focused contract test covering flat/instance, sided buckets, hierarchy/LOD fallback, and validation on/off.
- Run one validation-enabled Cornell comparison to prove the aggregate stream still matches bucket output.
- Take one fresh matched Bistro baseline/candidate pair. Record opaque compaction, Forward visibility compaction, Forward, and whole-frame time; keep only a clear compaction improvement without downstream regression.

## Out of Scope

- Removing the aggregate allocation or renumbering bindless slots.

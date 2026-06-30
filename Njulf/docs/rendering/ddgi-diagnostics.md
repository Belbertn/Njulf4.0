# DDGI Diagnostics

DDGI diagnostics distinguish geometric coverage from usable lighting support.

## Forward Estimate Metrics

`spatial` is geometric volume and lattice coverage. A high value means DDGI volumes cover the shaded pixel.

`support` is usable probe support. It only rises when sampled probes are active and have irradiance alpha, quality confidence, and contribution weight.

`data` is the data confidence used for final ownership and composition.

`visibility` is the sampled visibility confidence for the strongest supported probe chain.

`leak` is final leak attenuation after visibility transport.

`effective` is the final DDGI trust after support, data confidence, and leak attenuation.

`rawLum` is raw DDGI diffuse luminance before final fallback composition.

`finalLum` is final composed indirect diffuse luminance.

`ownership` is support-based DDGI ownership consumed for the pixel. Unsupported spatial coverage must not consume ownership.

## Scheduler Metrics

`requestBudget` is the intended probe request budget for the frame.

`primaryRayBudget` is the intended DDGI primary ray budget for the frame.

`ddgiDispatchCapacity` is the predicted GPU scheduler dispatch/request upper bound.

`ddgiActualRequests` and `ddgiActualPrimaryRays` come from GPU scheduler readback. They are reported as `pending` until a valid readback exists.

`candidates` is compacted scheduler candidate count. `stableSkipped` is the count of stable probes intentionally not emitted as candidates.

## Troubleshooting

| Symptom | Likely area |
| --- | --- |
| `spatial` high, `support` low | Probe data, irradiance confidence, quality confidence, or warmup scheduling |
| `support` high, `effective` low | Final composition, leak attenuation, visibility, or AO interaction |
| `gatherFallback` high | Gather tile assignment or volume coverage |
| Scheduler overflow high | Candidate generation caps, dirty region volume, or request/ray budget |
| `rawLum` high, `finalLum` low | Final composition or suppression mask |
| `ddgiActualRequests=pending` | First readback frames have not completed yet |

## Required Ownership Invariant

Spatial coverage alone must not suppress environment fallback. If a frame reports:

```text
spatial=1.000
support=0.000
effective=0.000
```

then `ownership` must also be `0.000`, and environment fallback must remain available.

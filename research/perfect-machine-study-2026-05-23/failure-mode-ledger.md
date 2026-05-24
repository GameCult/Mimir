# Failure Mode Ledger

## Purpose

This ledger names failure modes as data-flow problems. The fix should generally
move authority or measurement to the right place, not add another after-the-fact
filter.

## Active Witness Failures

### Dead Frequency Band

Symptom:

- emitted symbol is consistently absent or weak on one mic.

Coherent fix:

- calibration marks band unreliable;
- codebook emission avoids it for that path or shared room plan;
- decoder weights it down before trellis selection.

Bad fix:

- classify as normal, then reject "outliers" later.

### Symbol Alias

Symptom:

- expected symbol often scores as a neighboring bin.

Coherent fix:

- confusion matrix changes likelihood surface;
- global bin-shift hypotheses are searched as part of the window;
- codebook spacing/order adapts.

Bad fix:

- hard-code a special-case symbol remap without path identity.

### Reflection-Dominated Timing

Symptom:

- anchors decode, but delay residuals jump between plausible paths.

Coherent fix:

- preserve multiple delay hypotheses;
- combine with mic/speaker geometry and repeated state;
- report uncertainty to Fensalir/DSP.

Bad fix:

- smooth the jump until it looks calm.

### Gain / Clipping

Symptom:

- loopback or mic confidence collapses despite audible witness.

Coherent fix:

- calibration preflight checks clipping/noise floor;
- artifacts record gain state;
- witness amplitude adapts within safe bounds.

Bad fix:

- lower thresholds globally.

## Passive Sync Failures

### Silence

Symptom:

- passive estimator emits weak or meaningless delay.

Coherent fix:

- confidence falls;
- hybrid emits active watermark/chirp witness.

Bad fix:

- hold stale passive delay as if it were current truth.

### Narrowband Music

Symptom:

- PHAT correlation peak is broad or ambiguous.

Coherent fix:

- passive confidence remains advisory;
- active witness or longer evidence window takes authority.

Bad fix:

- pretend a low-frequency beat gives microsecond timing.

## Audio Actuator Failures

### Delay Corrected, Drift Ignored

Symptom:

- streams align briefly and then walk apart.

Coherent fix:

- separate delay and SRO loops;
- drive ASRC/fractional delay continuously.

Bad fix:

- periodically snap delay back with audible discontinuity.

### Actuator Chases Source Motion

Symptom:

- moving speaker/mic looks like sample-rate drift.

Coherent fix:

- slow SRO loop uses long-term clock evidence;
- fast TDOA/source motion remains separate.

Bad fix:

- one smoothing constant for everything.

## Camera Failures

### Advertised FPS Lies

Symptom:

- device reports 60/120 fps but delivers less.

Coherent fix:

- measure actual cadence after warm-up;
- record topology/control settings;
- treat measured cadence as truth.

Bad fix:

- trust media type advertisements.

### Shared USB Collapse

Symptom:

- one device's cadence drops when another device appears.

Coherent fix:

- topology/cadence benchmark;
- distribute devices across controllers/hosts;
- record known bad neighborhoods.

Bad fix:

- add buffering and call latency acceptable.

### JSON Bridge Becomes Production

Symptom:

- diagnostic frame events are relied on for live six-camera ingest.

Coherent fix:

- promote probe logic into native worker ABI;
- payload handles enter runtime/reservoir.

Bad fix:

- optimize JSON parsing.

## Fusion Failures

### CPU Pixel Furnace

Symptom:

- managed code demosaics/converts/processes every camera frame.

Coherent fix:

- push feature extraction/upload into Fensalir GPU path;
- keep CPU metadata small.

Bad fix:

- add worker threads until the thermal budget files a complaint.

### Parallel Stable Track Owners

Symptom:

- Mimir and Fensalir both maintain stable visual/acoustic track identity.

Coherent fix:

- Mimir owns observations;
- Fensalir temporal evidence reservoir owns tracks after lowering.

Bad fix:

- add id synchronization glue.

## Network Failures

### Transport Timestamp Worship

Symptom:

- Raven/phone timing is derived from packet arrival time.

Coherent fix:

- remote receiver decodes codebook anchors locally;
- network time is coarse metadata only.

Bad fix:

- add latency compensation constants per sender.


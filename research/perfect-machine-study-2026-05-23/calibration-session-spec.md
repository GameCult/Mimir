# Calibration Session Spec

## Purpose

The calibration command should be the machine that turns a real room into a
path model. It should not be a pile of one-off artifact commands that only the
current operator remembers how to arrange.

## Objective

Emit a known active witness, capture loopback and every microphone in the same
clock domain where possible, compute per-output/mic response/confusion/delay
models, persist them, and immediately prove that the live decoder improves when
those models are loaded.

## Inputs

- output path id;
- ASIO driver id / source manifest;
- sample rate;
- duration;
- emission mode (`chirp-only`, `hybrid-watermark`, `silent-passive-baseline`);
- candidate symbol plan;
- mic geometry file if available;
- artifact directory.

## Outputs

- raw capture artifact;
- rendered witness artifact;
- manifest with device/channel/sample-rate/buffer-size/gain notes;
- `MimirChirpBinCalibrationModel`;
- per-path usable-band table;
- confusion matrix;
- timing residual table;
- group-delay estimate;
- recommended codebook plan;
- before/after decode report.

## Command Shape

Future production command:

```powershell
dotnet run --project .\src\Mimir.BufferSmoke\Mimir.BufferSmoke.csproj -- `
  --calibration-session `
  --config .\config\mimir-runtime.asio.json `
  --output-path scarlett-main-out `
  --seconds 30 `
  --sample-rate 192000 `
  --artifact-dir .\calibration\runs\2026-05-24-starfire
```

This can start as `BufferSmoke`, but the final owner should be a runtime command
or app action that uses the same ASIO/native path as production.

## Session Phases

### 1. Preflight

- enumerate ASIO inputs/outputs;
- verify requested sample rate and buffer size;
- verify loopback channels are present;
- verify input channels are non-silent when expected;
- record channel names and source ids;
- load existing calibration for comparison.

Fail fast if loopback is absent. Without loopback, the session can still record
diagnostics, but it cannot become canonical calibration.

### 2. Passive Baseline

Capture room/program audio without active witness for a short window.

Purpose:

- noise floor;
- current music/program spectrum;
- passive correlation strength;
- mic gain sanity.

### 3. Active Emission

Render a deterministic chirp-bin/watermark witness using the candidate symbol
plan. Capture loopback and all inputs.

Important:

- event ids and schedule seed go into the manifest;
- emitted codebook plan goes into the artifact;
- output gain should be recorded manually or through driver state if possible.

### 4. Decode And Score

For each input:

- decode loopback anchors;
- decode mic anchors;
- compute matched anchor delays;
- compute expected-symbol versus observed-bin matrix;
- compute timing residuals;
- estimate magnitude response;
- estimate phase/group delay where phase evidence is stable;
- generate delay/bin-shift hypotheses.

### 5. Plan Adaptation

Across all selected mics:

- identify reliable bins;
- identify dead/aliased bins;
- choose reduced alphabet;
- choose higher de Bruijn order if necessary;
- estimate expected time-to-unique-lock;
- write recommended emission plan.

### 6. Replay Proof

Reload the persisted model and decode the same artifact:

- unweighted decode report;
- weighted/adapted decode report;
- anchor gain/loss;
- confidence gain/loss;
- delay residual gain/loss.

If the calibrated model does not improve the capture, the command should say so
plainly. A calibration file that cannot improve the decoder is just decorative
paperwork with a badge.

## Persisted Model Shape

Minimum:

```json
{
  "schema": "gamecult.mimir.chirp_bin_calibration.v1",
  "createdUtc": "2026-05-24T00:00:00Z",
  "sampleRate": 192000,
  "outputPathId": "scarlett-main-out",
  "emissionPlan": {},
  "paths": [
    {
      "sourceId": "asio-ch0",
      "usableBands": [],
      "symbols": [],
      "delayHypotheses": [],
      "phaseModel": {},
      "groupDelayModel": {},
      "confidence": 0.0
    }
  ]
}
```

## Acceptance Criteria

- loopback decodes confidently;
- at least one physical mic produces code-valid anchors;
- calibration identifies reliable and unreliable bands;
- adapted emission plan changes the symbol set when the original bands are bad;
- loaded calibration affects likelihood before codebook solving;
- replay proof is stored with the model.

## Failure Records

A failed calibration is still useful if it records:

- no loopback;
- silent mic;
- clipping;
- insufficient high-frequency response;
- strong alias/confusion;
- group delay too unstable;
- active witness too quiet/loud;
- room reflection dominated direct path.

Do not delete failed runs unless they are accidental trash. They are evidence.


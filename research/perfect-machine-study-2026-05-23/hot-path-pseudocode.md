# Hot Path Pseudocode

## Purpose

This note writes the intended hot paths as code-shaped prose. It is not
production code. It is a guard against implementation drift.

## Streaming Active Decoder

```text
onAudioBlock(source, absoluteStart, samples):
  pcmRing.append(absoluteStart, samples)
  for each analysisHop completed by this block:
    energy = updateProposalEnergy(pcmRing, analysisHop)
    if energy is plausible:
      proposalRing.push(analysisHop, energy)

  candidates = proposalRing.readySince(lastScoredSample)
  if candidates is empty:
    return currentClockFit

  scoreRows = scoreKernel.scoreBatch(pcmRing, candidates, codebook, calibration)
  for row in scoreRows:
    ambiguityRing.push(row)

  anchors = trellis.solveRecent(ambiguityRing, schedule, calibration)
  clockFit.update(anchors)
  responseModel.observe(scoreRows, anchors)
  return clockFit
```

Invariant:

- proposal, scoring, schedule solving, and clock fit are separate phases;
- calibration changes likelihood before trellis solving;
- old PCM expires with the rolling window.

## Calibration-Weighted Symbol Score

```text
score(symbol, observedBinEnergy, phase, timing):
  magnitude = normalize(observedBinEnergy)
  responseWeight = path.usableBandWeight(symbol)
  confusion = path.confusionProbability(expected=symbol, observed=bin)
  phaseWeight = path.phaseCoherence(symbol, phase)
  groupDelay = path.groupDelayCorrection(symbol)
  correctedTime = timing - groupDelay
  return likelihood(magnitude, responseWeight, confusion, phaseWeight, correctedTime)
```

Invariant:

- dead bands lose authority before code selection;
- group delay changes event timing before clock fit;
- phase is evidence when stable, not mandatory when unstable.

## Pairwise Delay From Anchors

```text
estimateDelay(referenceAnchors, sourceAnchors):
  matches = joinByEventIndex(referenceAnchors, sourceAnchors)
  if enough(matches):
    return weightedMean(source.sampleOffset - reference.sampleOffset)

  if bothClockFitsConfident:
    return sourceFit.sampleFor(t) - referenceFit.sampleFor(t)

  return noReport
```

Invariant:

- matched event anchors beat independent fits;
- independent fits must stay inside live horizon;
- no report is better than absurd confidence.

## DSP Actuator Control

```text
onSyncState(source, delaySamples, sroPpm, confidence):
  if confidence < threshold:
    holdOrFadeCorrection()
    return

  pll.update(delaySamples, sroPpm)
  actuator.setTargetDelay(pll.delay)
  actuator.setResampleRatio(1.0 - pll.sroPpm * 1e-6)
```

Invariant:

- control loop runs at control rate;
- sample movement runs at audio rate;
- drift and delay are not the same state.

## Native ASIO Ring

```text
onBufferSwitch(index, samplePosition):
  block = ring.tryReserve()
  if block is unavailable:
    droppedBlocks++
    return

  block.sourceChannel = channel
  block.startFrame = samplePosition
  block.sampleRate = currentRate
  convertOrCopyDeviceBuffer(block.payload)
  ring.publish(block)
```

Invariant:

- callback never allocates;
- queue is bounded;
- dropped blocks are counted, not hidden.

## Camera Frame Ring

```text
onReadComplete(slot):
  frame = ring.tryReserve()
  if frame unavailable:
    droppedFrames++
    resubmit(slot)
    return

  frame.source = deviceId
  frame.sequence = sequence++
  frame.deviceTimestamp = slot.timestamp
  frame.payloadHandle = slot.payload
  frame.format = negotiatedFormat
  ring.publish(frame)
  resubmit(nextSlot)
```

Invariant:

- read queue stays full;
- frame metadata is typed;
- payload ownership is explicit.

## Fensalir Lowering

```text
buildSceneState(runtimeSnapshot):
  gpuSensors = lowerVideoDescriptors(runtimeSnapshot.videoWindow)
  acoustic = lowerSyncAndCalibration(runtimeSnapshot.audioState)
  calibrationEvents = lowerHighConfidenceEvents(runtimeSnapshot.events)
  return AquariumSceneState(gpuSensors, acoustic, calibrationEvents)
```

Invariant:

- lowering reads cached/snapshotted state;
- lowering does not poll devices;
- lowering does not run analyzers;
- Fensalir owns what happens after contract construction.


# Distributed Receiver Spec

## Purpose

Mimir must work when Raven, a phone, or another networked sensor hears the same
active witness late, compressed, resampled, and out of Starfire's immediate
clock domain. The active codebook is meant to make that possible.

## Objective

A remote receiver that knows the source codebook and schedule should be able to
place local samples on the canonical timeline without round-tripping every
sample through Starfire. Network timestamps are metadata. Decoded anchors are
the timing truth.

## Receiver Inputs

- codebook id;
- schedule seed/epoch;
- nominal sample rate;
- local audio stream;
- optional path calibration;
- optional expected frequency band limits;
- optional network time estimate for coarse acquisition only.

## Receiver State

Minimum:

- rolling PCM ring;
- proposal ring;
- candidate symbol ring;
- codebook trellis state;
- local clock fit to canonical time;
- response/confusion accumulator;
- health counters.

It does not need:

- Fensalir render state;
- OBS state;
- Starfire ASIO state;
- per-sample network acknowledgements.

## Receiver Output

```json
{
  "sourceId": "phone-left",
  "codebookId": "main-room-2026-05-24",
  "canonicalClockFit": {
    "localSampleAtEpoch": 12345678.5,
    "effectiveSampleRate": 47999.92,
    "confidence": 0.91,
    "meanAbsErrorSamples": 0.37
  },
  "anchors": [],
  "responseProfile": {},
  "health": {}
}
```

The output is compact enough for LAN transport. Raw audio can still be streamed
when needed, but the timing machine should not depend on raw audio arriving
with low latency.

## Canonical Mapping

The receiver fits:

```text
local_sample = source_offset + canonical_seconds * effective_sample_rate
```

The inverse places a local sample on the source timeline:

```text
canonical_seconds = (local_sample - source_offset) / effective_sample_rate
```

Any code-valid anchor improves the fit. A triplet or higher-order tuple
identifies which canonical event was heard. Repeated anchors estimate drift.

## Acquisition Strategy

1. Use broad energy/onset proposals to avoid scanning every symbol everywhere.
2. Score candidates through the calibrated symbol likelihood.
3. Preserve top-K ambiguities.
4. Search code-valid tuples against the known schedule.
5. Fit local clock.
6. Narrow future search around predicted event times.

That last step is the efficiency win. Once locked, the receiver is not hunting
the whole universe; it is checking whether expected events arrived where the
current clock predicts.

## Network Behavior

Remote nodes should send:

- periodic clock-fit summaries;
- decoded anchor batches;
- response/confusion updates;
- buffer health;
- optional compressed/raw audio windows for debugging or program use.

Starfire should not assume:

- network latency is stable;
- transport order is canonical order;
- remote capture sample rate is exact;
- packet timestamps are better than decoded anchors.

## Raven Shape

Raven is special because it is a co-stream/game machine with its own Scarlett.
It can:

- hear/emit the same active witness;
- decode local clock against Starfire's schedule;
- stream compact anchor/response state to Starfire;
- optionally stream audio/video payloads into Starfire's rolling buffers with
  canonical-time metadata attached.

Raven should not:

- do heavy soundfield reconstruction;
- become the global clock authority unless explicitly selected;
- depend on OBS or game timing as source truth.

## Phone Shape

A phone is the harsh receiver target:

- unknown mic response;
- OS audio resampling;
- possible AGC/noise suppression;
- weaker CPU budget;
- awkward latency.

If the phone can decode enough anchors to estimate canonical time, the codebook
is doing its job. If it cannot, the failure is likely response/codebook design,
not network transport.

## Security / Identity Note

Codebook possession should be enough to decode time, not enough to control
Mimir. Remote timing reports need source identity and trust policy before they
affect program output.


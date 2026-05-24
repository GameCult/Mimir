# State Machine Invariants

## Purpose

The Perfect Machine is mostly a state discipline problem. This note lists the
state each subsystem may own and the state it must not smuggle in through a
friendly-looking helper.

## Runtime State

`MimirRuntime` may own:

- runtime lifecycle;
- loaded configuration;
- witness emission policy;
- Fensalir scene state assembly;
- UI/debug readout surfaces.

It must not own:

- raw driver callback loops;
- audio DSP sample movement;
- dense visual tracks;
- OBS composition.

## Hub State

`MimirSynchronizationHub` may own:

- registered sources;
- rolling buffer set;
- polling/update cadence;
- latest reports;
- smoothed synchronization states.

It must not own:

- analyzer internals;
- render tick work;
- hidden per-source DSP buffers;
- long-lived history outside retention window.

## Rolling Buffer State

Each rolling buffer owns:

- one stream descriptor;
- bounded sample window;
- current edge;
- snapshot semantics.

It must not own:

- a second clock model;
- per-kind retention policy;
- decoded field tracks;
- calibration truth.

## Active Decoder State

The streaming decoder should own:

- PCM window/ring;
- event proposal ring;
- candidate ambiguity ring;
- codebook/schedule view;
- local clock fit;
- response/confusion accumulation.

It must not own:

- UI state;
- emitter policy;
- DSP actuator state;
- global app timing authority.

## Calibration State

Calibration owns:

- output/mic path identity;
- usable bands;
- confusion matrix;
- delay/bin-shift hypotheses;
- magnitude response;
- phase/group delay;
- recommended emission plan.

It must not own:

- raw capture buffers forever;
- UI-specific names as primary identity;
- unverified learned patches that cannot be replayed.

## DSP Actuator State

The actuator should own:

- fractional delay line state;
- resampler phase;
- SRO control state;
- per-source gain/phase correction;
- audio-rate buffers.

It must not own:

- source discovery;
- calibration file persistence;
- UI controls except exposed parameters;
- timeline decoding.

## Native Capture State

Native capture workers own:

- device handles;
- callback/read state;
- block/frame rings;
- device timestamps/counters;
- low-level format conversion where unavoidable.

They must not own:

- canonical timeline decisions;
- OBS routing;
- Fensalir render state;
- long-term calibration policy.

## Fensalir State

Fensalir owns:

- window/input/render loop;
- D3D12 resource lifetime;
- external texture import;
- GPU fusion buffers;
- temporal evidence reservoir after lowering;
- program render surfaces.

It must not own:

- source capture configuration;
- audio clock authority;
- Mimir calibration truth;
- app-specific device policy.

## Network Receiver State

Remote nodes own:

- local PCM ring;
- local decode state;
- local clock fit;
- local response/confusion observations;
- transport health.

They must not own:

- Starfire canonical clock authority;
- program output mixing;
- global calibration policy;
- trust decisions.

## OBS State

OBS owns:

- final scene composition;
- stream/record controls;
- operator mix choices.

It must not own:

- stream synchronization;
- canonical timing;
- raw calibration truth;
- volumetric field reconstruction.


# Moonlight lessons for Muninn streaming and Sleipnir input

Date: 2026-07-16  
Upstream examined: `moonlight-stream/moonlight-common-c` commit `82e25148549d249a80aaf4afc68e2abb07072edf`

## Verdict

Moonlight is strong because it does not ask one transport policy to serve every
realtime signal. It gives video, audio, control, and input different loss,
ordering, recovery, and freshness rules, then keeps each queue bounded by what
the user can still perceive.

Muninn is not missing all of that machinery. Its media packetizer already owns
access-unit boundaries, timestamps and deadlines, video parity, dependency and
keyframe metadata, bounded assembly, receiver damage feedback, repair caching,
and keyframe requests. Sleipnir already rejects regressing sequence/timestamp
pairs, expires reliable HID sends after 25 ms, reports stage latency, and
neutralizes stale output.

The weakness is that these parts do not yet form one closed realtime policy:

- video already uses unreliable media delivery with parity/manual repair, but
  audio and HID still lean on generic expiring reliability and none of the
  lanes yet forms one closed media/input policy;
- the default Muninn media latency budget is 2000 ms, which is an archive-shaped
  tolerance wearing a realtime badge;
- receiver feedback is measured and repair/keyframe pressure is recorded, but
  the encoder and sender do not visibly adapt bitrate, parity, or pacing from it;
- Sleipnir coalesces whole controller snapshots implicitly at the producer, but
  its protocol does not name which values are replaceable state and which are
  non-replaceable edges;
- wall-clock source age is reported as latency even though no explicit clock
  synchronization contract proves that subtraction valid across hosts.

Do not transplant Moonlight wholesale. Distill its ownership model into the
existing typed Muninn/Sleipnir pipeline.

## Authority map

### Muninn media

- Owner: Muninn owns source-local capture, encoding child lifecycle, media
  packetization, repair material, and sender feedback response.
- Inputs: encoded H.264/H.265 access units, encoded audio packets, source media
  timestamps, configured latency target, and receiver feedback.
- Outputs: typed video chunks/parity, audio packets, recovery/keyframe actions,
  and transport/quality telemetry.
- Derived state: RUDP acknowledgements, resend counters, CultCache receipts, OBS
  local lowering, and operator cards are evidence or delivery substrate. They do
  not own media deadlines.
- Forbidden writers: generic RUDP retry policy must not decide whether expired
  media is still useful; OBS must not decide stream synchronization; persistence
  must not become a hot media queue.

### Sleipnir input

- Owner: Sleipnir owns selection/remapping and local virtual-HID emission.
  Muninn owns source-local device sampling and event classification.
- Inputs: a discovered Muninn input stream, typed mapping intent, monotonic
  stream sequence, source-local capture time, and receive time.
- Outputs: local virtual-HID state plus latency/freshness receipts.
- Derived state: the latest full controller snapshot is replaceable state.
  Button transitions, text/code points, connect/disconnect, and neutralization
  are non-replaceable edges until acknowledged or superseded by an explicit
  state transition.
- Forbidden writers: generic reliable queue order must not replay stale analog
  or motion samples; wall-clock subtraction must not claim one-way latency
  without a clock model; the telemetry/store path must not repair a missing hot
  stream.

## What Moonlight actually does

### It separates stream semantics

Moonlight receives video and audio over UDP/RTP-like paths while input travels
through its control path (or a legacy TCP socket). The important lesson is not
the literal socket choice. It is that the queues have different contracts.
Video is frame/dependency oriented; audio is short-block continuity oriented;
input is event/state oriented.

Source: [stream initialization and input transport](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/InputStream.c),
[video receive path](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/VideoStream.c), and
[audio receive path](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/AudioStream.c).

### Video recovery is dependency-aware and deadline-shaped

The video queue groups packets by frame and FEC block, tracks missing data,
reconstructs recoverable blocks with Reed-Solomon parity, rejects old/replayed
frames, and advances frame authority explicitly. Depacketization can request an
IDR when damage makes the predictive chain unsafe. The system repairs useful
frames and abandons poisoned dependency chains; it does not wait forever for
generic reliable delivery.

Source: [RTP video queue](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/RtpVideoQueue.c) and
[video depacketizer](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/VideoDepacketizer.c).

### Audio recovery is a small bounded continuity problem

Moonlight groups audio into fixed FEC blocks, orders packets by RTP sequence,
recovers missing shards, detects out-of-order behavior, and changes how long it
waits based on observed reordering. When recovery cannot complete in time, it
allows a discontinuity and lets the audio decoder's concealment path carry the
scar. It does not turn late audio into permanent latency.

Source: [audio FEC and reorder queue](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/RtpAudioQueue.c) and
[packet-loss concealment handoff](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/AudioStream.c).

### Input reliability is constrained by semantic coalescing

Moonlight has one send thread and a bounded queue, but it avoids blindly sending
every sampled value. Relative mouse motion is accumulated, absolute pointer
motion keeps the newest position, pen hover/move keeps the newest compatible
event, controller analog state is merged while button changes split batches,
and sensor motion keeps the newest value per sensor. A roughly millisecond-scale
batching opportunity can reduce latency by preventing ENet queue buildup.

This is the most important Sleipnir lesson: reliable delivery is safe only after
replaceable state has been collapsed and edges have been preserved.

Source: [input batching and send queue](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/src/InputStream.c).

### It exposes failure to the owning layer

Packet loss, FEC recovery/failure, queue overflow, frame loss, and keyframe need
are explicit signals. Moonlight's public documentation also warns that allowing
frames to queue for smoothness can create high latency; its balanced mode keeps
only about one frame for jitter smoothing. The machine treats queue depth as a
product decision, not an incidental container property.

Source: [Moonlight frame-pacing FAQ](https://github.com/moonlight-stream/moonlight-docs/wiki/Frequently-Asked-Questions#what-do-the-frame-pacing-options-on-the-android-client-mean).

## Current Muninn/Sleipnir comparison

| Concern | Moonlight | Muninn / Sleipnir now | Distilled judgment |
|---|---|---|---|
| Video unit | Encoded frame, FEC blocks, dependency recovery | Encoded access unit, chunks/parity, frame/dependency IDs, deadlines | The conceptual unit is already correct. Keep it. |
| Video loss | FEC first; abandon damaged/obsolete work; request IDR | Parity, missing-chunk feedback, repair cache, keyframe pressure | Close the loop into encoder action and measure whether repair beats deadline. |
| Audio loss | Small FEC blocks, reorder-aware wait, decoder concealment | Typed audio packets and deadlines over generic expiring reliable delivery | Add an audio continuity policy with bounded FEC/reorder/concealment ownership. |
| Congestion | Stream-specific queues and feedback behavior | Sender queue bounds and receiver feedback stats, fixed configured bitrate | Feedback must alter pacing/bitrate/parity or it is only an autopsy report. |
| Latency budget | Minimal queueing; frame pacing explicitly trades latency for smoothness | Default media budget 2000 ms; Sleipnir reliable expiry 25 ms | Replace one broad media budget with video/audio/input budgets derived from consumption deadlines. |
| Input state | Coalesces replaceable motion/sensor/analog values | Producer emits latest full controller state; receiver filters regressions | Make coalescing and edge preservation typed protocol semantics, not an accident of polling. |
| Input edges | Button/type changes terminate batches; text is ordered | Whole snapshots contain buttons and axes | Add edge sequence/ack or a compact transition log so a press/release cannot vanish between snapshots. |
| Staleness | Queue construction prevents much stale work | Receiver drops regressing timestamps and neutralizes after timeout | Keep the guard, but move freshness authority earlier into enqueue/supersession. |
| Timing | RTP/media clocks and local queue timing | source Unix nanoseconds plus receiver instants | Use monotonic source time plus an explicit clock-offset/uncertainty model; do not call raw wall-clock subtraction latency. |
| Security | Authenticated encryption in modern stream generations | CultNet typed transport policy varies by lane | Preserve typed schemas, but specify replay/authentication guarantees for hot media and HID lanes. |

## Recommended cut

### 1. Define traffic classes before changing transport

Add one narrow typed delivery policy shared by Muninn and Sleipnir:

- `state-latest`: analog axes, pointer position, gyro/accelerometer samples;
- `edge-ordered`: button/key transitions, text/code points, device lifecycle;
- `media-video`: dependency-bearing access units with decode deadlines;
- `media-audio`: continuity blocks with playout deadlines;
- `control-reliable`: subscriptions, mappings, capability negotiation, and
  keyframe requests.

This is not a generic router. It is a finite statement of the five contracts the
existing machine already has. Each class must name supersession, ordering,
expiry, recovery, and observability.

### 2. Cut the two-second realtime default

Derive deadlines from the actual consumer:

- Sleipnir state-latest: tens of milliseconds, with replacement before send;
- input edges: reliable until acknowledged, but compacted against later
  authoritative state where safe;
- interactive video: frame/decode budget, initially test 50–150 ms on LAN;
- audio: playout budget, initially test 40–100 ms on LAN;
- Mimir's five-second analysis reservoir remains Mimir compute budget, not a
  network permission to deliver old frames.

The exact values must be earned by field traces. The ownership change is not
optional: transport retry duration becomes derived from the media/input
deadline, never the other way around.

### 3. Make Sleipnir freshness structural

Split the hot input frame into:

- a latest-state snapshot with `state_sequence`, source monotonic capture time,
  and supersedes-through sequence;
- an ordered edge list with its own sequence range;
- receiver acknowledgement of the highest applied state and edge sequence.

Muninn may overwrite an unsent latest-state snapshot. It may not overwrite an
unacknowledged edge. Sleipnir applies edges in order, applies only the newest
state, and neutralizes on a named lease timeout. Manual, remapped, replayed, and
live paths must use the same commit primitive.

### 4. Close Muninn's feedback loop

Receiver feedback should drive named sender decisions:

- request/force keyframe after unrecoverable dependency loss;
- choose repair only when estimated arrival precedes frame deadline;
- tune parity within a bounded profile from recent burst-loss evidence;
- reduce encoder bitrate/pacing when queue delay grows;
- expose encode, queue, network, assembly, decode, and presentation stages
  separately.

Do not add a second congestion controller beside generic RUDP and hope the two
argue productively. Media policy owns the send budget; RUDP exposes delivery
mechanics and obeys the deadline.

### 5. Prove it on the layer the user feels

Build one loss/jitter/reorder test harness and record timelines for:

- controller press and release during 1%, 5%, and burst packet loss;
- high-rate analog/gyro motion under an artificial sender stall;
- video dependency loss and keyframe recovery;
- audio burst loss and concealment;
- queue growth under bitrate oversubscription;
- disconnect/reconnect and stale-session replay;
- clock offset and drift between Muninn and Sleipnir hosts.

Acceptance should bound p50/p95/p99 capture-to-HID age, lost/duplicated edges,
time-to-neutral, glass-to-glass video age, audio discontinuities, A/V skew, and
recovery time. Final-state-only tests will politely conceal the corpse.

## What not to copy

- Do not copy Moonlight's C implementation or protocol packets into GameCult
  code without an explicit GPLv3 licensing decision. Architectural study is not
  license laundering. See [Moonlight's license](https://github.com/moonlight-stream/moonlight-common-c/blob/82e25148549d249a80aaf4afc68e2abb07072edf/LICENSE.txt).
- Do not replace typed CultMesh discovery, command state, or ownership with
  RTSP/SDP or a Moonlight compatibility stack.
- Do not make ENet the new universal answer. Moonlight's strength comes from
  semantic queues above the carrier, not from the carrier's brand name.
- Do not merge input, audio, and video into one ordered reliable stream. One
  lost video fragment must not head-of-line block a button release or audio
  packet.
- Do not treat FEC as magic. Parity is useful only when its overhead and recovery
  time fit the deadline.

## Distilled doctrine

Realtime strength is not “reliable UDP.” It is knowing which truth survives
loss:

- the newest state replaces old state;
- an edge remains until applied or explicitly superseded;
- a media packet is valuable only while its frame or playout deadline is alive;
- predictive video damage is healed by recovery or a new reference, not by
  delivering every corpse;
- queues are latency budgets made physical;
- feedback that cannot change sender behavior is telemetry, not control.

Muninn and Sleipnir already have enough organs to become strong. The next work
is to give each signal one explicit survival law and make the transport obey it.

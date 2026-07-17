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

The 2026-07-17 field cut closes most of the original mechanism gaps:

- canonical video and video parity use the unreliable `realtime` lane; typed
  8+8 GF(256) blocks, selective repair, and decode-chain IDR recovery own loss;
- canonical 10 ms PCM audio uses `realtime`, with 4+2 FEC, a 40 ms reorder
  owner, and short concealment; reliable delivery is reserved for control;
- the consumer deadline is 100 ms by default and 250 ms in the Raven field
  profile; queues, retransmits, repair material, and assemblies derive from it;
- receiver pressure actuates the live NVENC encoder through multiplicative
  backoff and conservative additive recovery; startup begins at half the
  requested ceiling because fixed video parity can roughly double wire load;
- Sleipnir latest state and ordered edges have distinct epoch/sequence and
  application-ack semantics, including bounded overflow rebase.

The remaining claim boundary is evidence: direct and impaired Raven-to-
Starfire soaks, reconnect repetition, A/V skew measurement, and a live virtual
HID exercise. The machine is no longer missing a named owner for these paths;
it still has to prove those owners under pressure.

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
| Video loss | FEC first; abandon damaged/obsolete work; request IDR | Typed 8+8 block FEC, deadline-bound repair, completed-frame tombstones, IDR actuation | Same ownership pattern; field loss ratios and parity overhead remain tunable. |
| Audio loss | Small FEC blocks, reorder-aware wait, decoder concealment | Realtime 4+2 PCM FEC, 40 ms reorder, bounded concealment | Mechanism is closed; Opus FEC/PLC is a later efficiency/quality cut. |
| Congestion | Stream-specific queues and feedback behavior | Half-ceiling startup, 15% pressure backoff, bounded additive ramp, fresh media ahead of repair | Closed loop exists; impairment evidence decides its final constants. |
| Latency budget | Minimal queueing; frame pacing explicitly trades latency for smoothness | Consumer-owned 100 ms default/250 ms Raven proof; 25 ms HID expiry | Correct owner and bounded values; glass-to-glass measurement remains. |
| Input state | Coalesces replaceable motion/sensor/analog values | Epoch-scoped latest state supersedes stale state | Structural rather than polling-accidental. |
| Input edges | Button/type changes terminate batches; text is ordered | Ordered edge sequence, application ACK, bounded replay, epoch rebase | Structural; live virtual-HID impairment proof remains. |
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

## 2026-07-17 LAN field results

The Moonlight-derived ownership changes now have Raven-to-Starfire field
evidence over LAN, with Raven processes launched only through hidden scheduled
tasks:

- Muninn video uses independently recoverable block parity plus bounded repair.
  During a seeded 1% datagram-loss run, the OBS receiver advanced from 2 to 12
  video assembly expiries while forwarding 14,400 additional payload groups:
  99.93% completed without expiry. Audio reported zero failures, skips, or stale
  packets in the same interval. The impairment proxy forwarded 2,147,356 of
  2,169,105 datagrams with zero queue overflow.
- Sleipnir latest-state delivery remained live through seeded 1% loss. Cutting
  the proxy caused the non-neutral virtual Xbox pad to neutralize after 1.56 s,
  owned by the named stale-input lease; restoring the hidden proxy reconnected
  and resumed state without operator action.
- A continuous 64-packet receive drain was retaining the newest input for about
  48-57 ms before HID application. Bounding that drain to 2 ms reduced live
  receiver-owned receive-to-apply time to typically 69-204 microseconds. A
  witnessed 16 ms spike remains to characterize.
- Starfire and Raven wall clocks differ by roughly 2.38 s. Sleipnir no longer
  publishes their raw subtraction as one-way latency. Its surface reports that
  source-clock latency is unavailable without a clock model and exposes only
  monotonic receiver-owned stage durations.

These results clear the initial 99.9% media-completion and disconnect-safety
gates at 1% independent loss. They do not yet prove glass-to-glass latency,
A/V skew, ordered quick-tap delivery under loss, 5%/burst behavior, or a long
mixed-impairment soak. Those remain acceptance gates, not footnotes.

The field comparison also exposed a generic transport invariant Moonlight's
long-running sessions force us to respect: bounded wire identifiers must wrap
deliberately. CultNet's fragmented-message ID previously saturated at the
maximum `u16`, so fragmented audio became unreassemblable after the boundary.
Wrap-to-one plus a boundary test removed the repeatable minute-eight collapse;
a subsequent 10-minute direct LAN run remained clean across the boundary.

## 2026-07-17 ordered-input closure

The quick-tap gate exposed four authorities that generic RUDP reliability had
blurred together:

- A Windows sleep requested at two milliseconds could still miss a short tap.
  Muninn now observes XInput on a dedicated high-resolution worker; transport,
  discovery, serialization, and logging cannot delay capture.
- Global cumulative ACKs could not retire a reliable packet arriving more than
  32 realtime sequence numbers late, and reliable duplicates were not ACKed
  after application deduplication. CultNet now directly acknowledges the late
  packet and acknowledges reliable duplicates. This collapsed a runaway field
  trace from hundreds of thousands of resend datagrams to a bounded flow.
- Muninn used semantic application ACK as permission to send the next edge.
  One delayed ACK therefore serialized the entire controller. It now sends a
  bounded window of captured edges; CultNet owns retransmission, Sleipnir's
  epoch/sequence cursor owns ordering, and semantic ACK only retires history.
- Sleipnir published CultMesh/Eve and Idunn state synchronously on the actuator
  thread. Those writes produced 0.7-1.8 second visible button holds. A bounded
  coalescing publisher worker now owns control-plane projection, leaving the
  input thread to receive, order, actuate, and acknowledge.

The resulting first-hot-plug field proof generated 100 taps with a 16 ms source
press and 84 ms gap. Under seeded 1% independent loss, every layer observed
exactly 100 presses and 100 releases; Raven-visible durations were 24-57 ms.
Under eight consecutive dropped packets every 200 datagrams, every layer again
observed exactly 100 presses and 100 releases; visible durations were 24-40 ms,
the final virtual pad was neutral, and the impairment proxy reported no queue
overflow. Raven ran through hidden scheduled tasks over `192.168.1.0/24`; no
WireGuard route participated. Both daemons used the Eve/Aetheria/VoidBot
experimental CultLib lineage pinned at `8965f3c0`.

This closes ordered digital edges at one 60 Hz source frame. It does not prove
sub-frame synthetic ViGEm pulses: eight-millisecond state-only pulses can fall
between XInput snapshots, and `XInputGetKeystroke` was rejected after it emitted
stale events from a reused user slot. Analog motion under a sender stall,
mixed media/input endurance, glass-to-glass video age, and A/V skew remain open.

# Moonlight-level reliability acceptance ledger

This ledger distinguishes implemented mechanisms from evidence that the actual
Raven-to-Starfire program timeline survives pressure. Passing unit tests is not
permission to call the field path done.

## Required contracts

| Contract | Current authority | Evidence | Status |
|---|---|---|---|
| Experimental CultMesh/CultNet lineage | Odin vendored CultLib snapshot `8965f3c0` | Snapshot provenance plus CultNet/Muninn/Sleipnir suites | proved locally |
| Low-delay NVENC | Muninn controllable encoder | D3D11 desktop capture, no B frames/lookahead, two-frame VBV, clean Annex-B decode | proved locally |
| Consumer deadline owns media lifetime | Muninn queues, repair cache, Mimir assembly/reorder | 100 ms default/250 ms Raven profile, bounded queues/cache/reliable expiry, late-frame repair refusal | proved structurally |
| Recoverable video loss avoids decoder reset | Mimir V4 block FEC/repair, Odin repair cache | Exhaustive 2..80 chunk, burst 1..8 tests in Rust/C++; completed-frame tombstones | proved locally |
| Decoder-chain loss forces immediate IDR | Muninn feedback owner and native encoder | Live 2560x1440 command produced IDR at frame 22 under a 600-frame GOP | proved locally |
| Congestion changes encoder output in-session | Muninn AIMD controller and native encoder | Half-ceiling start and protected cap; live 12-to-6 Mbps reconfiguration produced clean IDR | proved locally |
| Audio survives small erasure bursts | Odin 4+2 GF(256), Mimir FEC/reorder/concealment | Exhaustive one/two-erasure Rust and C++ tests; production DLL build | proved locally |
| Latest input state supersedes stale state | Muninn epoch/state sequence, Sleipnir cursor | Duplicate/reorder/stale epoch tests | proved locally |
| Button edges are not lost or retained forever | Muninn replay/app ACK/epoch rebase, Sleipnir ordered cursor | Quick-tap, ACK, overflow rebase, sparse-gap tests | proved locally |
| Raven runtime is LAN-only and non-interrupting | Hidden Raven scheduled task, direct LAN SSH/media | One coherent Muninn/WASAPI/NVENC/FFmpeg tree; no WireGuard route; `-WindowStyle Hidden` | proved operationally |
| Real field path survives impairment and soak | Raven sender, Starfire receiver/OBS, `cultnet-impair` | Direct LAN clean run 05:34:17–05:44:37: 79,200 records, 697,450,085 bytes, 18,588 recovered shards, zero decode/assembly/media/audio failures and zero sender capacity eviction; proxy matrix and long soak remain | **direct clean passed** |

## Field acceptance matrix

Run the real capture body, production receiver plugin, decoder, audio output,
and Sleipnir virtual input through the seeded profiles under
`E:\Projects\Odin\tests\realtime-impairment`. Observe the rendered/audio/input
timeline, not only proxy CSV counters.

| Profile | Required result |
|---|---|
| clean, 10 minutes | no decoder reset, audio discontinuity, input edge loss, queue growth, or timestamp drift; video p99 delivery under 50 ms |
| iid 1%, 10 minutes | no input edge loss; audio FEC recovers eligible holes; at least 99.9% video frames presented; media p99 under 80 ms |
| iid 3%, 5 minutes | bounded degradation; at least 99% video frames presented; decoder recovery within 250 ms; no stuck controls |
| burst 4 and burst 8 | parity/repair handles eligible damage; unrecoverable dependency loss produces one requested IDR and recovery within 250 ms |
| reorder and duplicate | no duplicate input action, stale state regression, or unbounded assembly/reorder storage |
| delay/jitter | AIMD reduces bitrate before sustained deadline loss and recovers no faster than one step per two stable seconds |
| stall 250 ms | obsolete media expires; queues remain bounded; input latest state converges within 35 ms after release |
| reconnect | old input epoch cannot act; neutral/current state within 100 ms; decoded video within 500 ms |
| mixed 60-minute soak | stable memory/handle/thread counts and no progressive A/V drift |

The first seeded 1% proxy run is a failed acceptance result, not a pass: 987 of
95,741 datagrams were dropped; in roughly one minute the receiver recorded 38
expired video assemblies and 56 skipped audio packets while remaining bounded
and avoiding decoder reset. CultNet fragmentation currently sits below
application-level FEC, so one lost transport fragment discards an entire FEC
shard. The next reliability cut is to make protected media shards fit one wire
datagram or move fragmentation above the erasure-code boundary.

## Field scars

- A fragmented CultNet RUDP session failed repeatably after roughly eight to
  nine minutes because the `u16` fragment-set ID used saturating increment and
  became permanently stuck at 65,535. Every later fragmented audio document
  then collided in one receiver reassembly key. The allocator now wraps from
  65,535 to 1, with a boundary test. The clean field run above crossed that
  boundary without a single audio reorder, skip, stale packet, or failure.
- Audio and video producer ingress now use separate bounded channels. Video
  capture can no longer block audio ingestion before the audio-priority send
  scheduler sees it. Capacity-eviction counters proved zero during the clean
  acceptance run.

## Completion rule

Moonlight-level performance is reasonably expected only after all local rows
remain green and the real field matrix passes twice: once on direct LAN and once
on the deployed route actually used during streaming. Failure should change the
owning deadline, FEC, encoder, or input authority—not add a repair loop around
the symptom.

# Moonlight-level reliability acceptance ledger

This ledger distinguishes implemented mechanisms from evidence that the actual
Raven-to-Starfire program timeline survives pressure. Passing unit tests is not
permission to call the field path done.

## Required contracts

| Contract | Current authority | Evidence | Status |
|---|---|---|---|
| Experimental CultMesh/CultNet lineage | Odin vendored CultLib snapshot `8965f3c0` | Snapshot provenance plus CultNet/Muninn/Sleipnir suites | proved locally |
| Low-delay NVENC | Muninn controllable encoder | D3D11 desktop capture, no B frames/lookahead, two-frame VBV, clean Annex-B decode | proved locally |
| Consumer deadline owns media lifetime | Muninn queues, repair cache, Mimir assembly/reorder | 100 ms default, bounded queues/cache, late-frame repair refusal | proved structurally |
| Recoverable video loss avoids decoder reset | Mimir parity/repair, Odin repair cache | Production XOR parity; live early-feedback fixture; repair expiry tests | proved locally |
| Decoder-chain loss forces immediate IDR | Muninn feedback owner and native encoder | Live 2560x1440 command produced IDR at frame 22 under a 600-frame GOP | proved locally |
| Congestion changes encoder output in-session | Muninn AIMD controller and native encoder | Live 12-to-6 Mbps reconfiguration produced clean IDR at frame 35 | proved locally |
| Audio survives small erasure bursts | Odin 4+2 GF(256), Mimir FEC/reorder/concealment | Exhaustive one/two-erasure Rust and C++ tests; production DLL build | proved locally |
| Latest input state supersedes stale state | Muninn epoch/state sequence, Sleipnir cursor | Duplicate/reorder/stale epoch tests | proved locally |
| Button edges are not lost or retained forever | Muninn replay/app ACK/epoch rebase, Sleipnir ordered cursor | Quick-tap, ACK, overflow rebase, sparse-gap tests | proved locally |
| Real field path survives impairment and soak | Raven sender, Starfire receiver/OBS, `cultnet-impair` | No current run: Raven WireGuard SSH/TCP 22 timed out | **missing** |

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

## Completion rule

Moonlight-level performance is reasonably expected only after all local rows
remain green and the real field matrix passes twice: once on direct LAN and once
on the deployed route actually used during streaming. Failure should change the
owning deadline, FEC, encoder, or input authority—not add a repair loop around
the symptom.

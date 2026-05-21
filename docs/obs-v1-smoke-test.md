# OBS V1 Smoke Test

No plugin/native receiver work gets promoted until this ledger exists for the
plain FFmpeg/SRT OBS path.

The smoke test is deliberately small:

- `sender_capture`: sender-side flash/chirp or packet-origin timestamp.
- `srt_receive`: receiver-side stream arrival timestamp.
- `obs_present`: OBS preview/recording presentation timestamp.
- endpoint drift: media-time slope versus receiver monotonic time.
- end-to-end latency: matched `eventId` from `sender_capture` to `obs_present`.
- confidence: stage coverage, observation count, event confidence, latency, and drift.

The output ledger is `calibration/runs/obs-v1-smoke-ledger.json`. A passing
ledger sets `gate.pluginOrNativeReceiverExpansionAllowed` to `true`. Anything
else means the bridge is still talking without enough clocks. Adorable, but no.

## Run

From the repo root:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\scripts\obs_smoke_test.py --config .\config\localcast.json plan
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\scripts\obs_smoke_test.py --config .\config\localcast.json template-events
```

Edit `calibration/runs/obs-v1-smoke-events.jsonl` with real observations from
one bounded pass. Use the same `eventId` for the same visible flash or audible
chirp as it moves through stages.

Then summarize:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\scripts\obs_smoke_test.py --config .\config\localcast.json summarize
```

## Event Shape

Each line is one JSON object:

```json
{
  "endpoint": "video",
  "stage": "obs_present",
  "eventId": "video-flash-001",
  "monotonicNs": 1000180000000,
  "wallTimeNs": null,
  "mediaTimeNs": 180000000,
  "confidence": 0.9,
  "note": "OBS recording frame containing flash"
}
```

Required stages are:

- `sender_capture`
- `srt_receive`
- `obs_present`

`monotonicNs` is mandatory because wall clocks lie casually. `wallTimeNs` is
optional human correlation only. `mediaTimeNs` is optional for individual events,
but drift confidence needs at least two receiver-side media-time observations.

## Thresholds

Defaults:

- maximum absolute endpoint drift: `100 ppm`
- maximum end-to-end latency: `500 ms`

Override them when running the summary:

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\scripts\obs_smoke_test.py --config .\config\localcast.json --max-abs-drift-ppm 50 --max-latency-ms 350 summarize
```

## Interpretation

`status: ok` means every planned endpoint has enough timestamp evidence for this
bounded pass. It does not prove the native reservoir is done. It proves the v1
OBS bridge has a clocked witness.

`status: fail` means stop bridge expansion and fix the missing or drifting
stage first.

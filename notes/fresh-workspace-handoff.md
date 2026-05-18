# Fresh Workspace Handoff

This is the short re-entry packet for `E:\Projects\LocalCastBridge`.

Do not trust this file for the exact live HEAD. Exact branch, HEAD, and dirty state are volatile. Always ask git.

## Rehydrate

```powershell
& 'C:\Users\Meta\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' .\tools\localcast_state.py status
Get-Content .\state\map.yaml
Get-Content .\notes\fresh-workspace-handoff.md
Get-Content .\notes\current-system-map.md
Get-Content .\docs\implementation-plan.md
git status --short --branch
git log --oneline -5
Get-Content .\state\evidence.jsonl -Tail 8
```

## Current Shape

- V1 uses FFmpeg plus OBS Media Source over SRT.
- Sender-side video encoding is `h264_nvenc`.
- Audio sources are separate SRT endpoints so OBS can mix them separately.
- The repo contains scripts and docs, not a native OBS plugin.

## Current Pressure

The useful next work is real hardware validation:

- confirm the sender FFmpeg build has `srt` and `h264_nvenc`
- capture actual audio device names
- tune latency once streams are live in OBS

## Immediate Re-entry Instruction

Do not continue implementation automatically from a rehydrate-only request. Rehydrate, then follow the user's next instruction.

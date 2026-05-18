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
- Neighbor sender is deployed at `C:\Meta\LocalCastBridge` on `192.168.1.84`.
- Madman's desktop has `Start LocalCast Sender.cmd` and `Stop LocalCast Sender.cmd`.
- Receiver OBS scene collection has `Neighbor PC - Video`, `Neighbor PC - Focusrite`, and `Neighbor PC - System Audio` Media Sources added.
- Sender has Voicemeeter plus SoundVolumeView installed for system-output loopback.
- Sender script was patched after failed transmission: live FFmpeg commands now include `-nostdin`, logs go under `C:\Meta\LocalCastBridge\logs`, and video capture size is `1024x768`.
- SSH-launched video capture fails with `gdigrab` error 5; test video from Madman's interactive desktop launcher.

## Current Pressure

The useful next work is real hardware validation:

- open OBS and smoke-test the three configured receiver sources from Madman's desktop launcher
- tune latency once streams are live in OBS
- add a real loopback/virtual audio capture source if system-output audio is required

## Immediate Re-entry Instruction

Do not continue implementation automatically from a rehydrate-only request. Rehydrate, then follow the user's next instruction.
